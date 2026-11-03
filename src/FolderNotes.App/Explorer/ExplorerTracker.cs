using FolderNotes.App.Explorer.Com;
using FolderNotes.App.Explorer.Native;
using FolderNotes.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FolderNotes.App.Explorer;

internal sealed record ExplorerTrackerOptions
{
    public WatchdogOptions Watchdog { get; init; } = WatchdogOptions.Default;

    public SchedulerOptions? Scheduler { get; init; }

    public TrackerLimits? Limits { get; init; }

    public HookThreadOptions Hook { get; init; } = HookThreadOptions.Default;

    public TimeSpan WorkerStartTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan WorkerStopTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The composition of 04 L: state machine, publisher, signal router, hook thread, watchdog, worker supervisor and one
/// <see cref="TrackerEngine"/> per worker. COM objects exist only inside the engine on the worker thread. Nothing starts
/// it but its owner (T3.4); constructing it creates no thread.
/// </summary>
internal sealed class ExplorerTracker : IExplorerTracker
{
    private sealed class WorkerState(TrackerEngine engine, Timer timer)
    {
        public TrackerEngine Engine { get; } = engine;

        public Timer Timer { get; } = timer;
    }

    private readonly IUiDispatcher _ui;
    private readonly IShellWindowsGatewayFactory _factory;
    private readonly IWin32Shell _win;
    private readonly TimeProvider _clock;
    private readonly ExplorerTrackerOptions _options;
    private readonly Action<string> _log;
    private readonly RcwTracker _rcw = new();
    private readonly TrackerCounters _counters = new();
    private readonly TrackerProcessGuard _guard;
    private readonly SignalRouter _router = new();
    private readonly StatePublisher _publisher;
    private readonly TrackerStateMachine _machine;
    private readonly HookThread _hooks;
    private readonly WatchdogThread _watchdog;
    private readonly WorkerSupervisor _supervisor;
    private readonly Lock _deliveryGate = new();
    private ActiveFolderState? _pendingDelivery;
    private bool _deliveryQueued;
    private int _disposed;

    public ExplorerTracker(
        IUiDispatcher ui,
        IShellWindowsGatewayFactory? factory = null,
        IWin32Shell? win = null,
        IWinEventApi? winEvents = null,
        TrackerProcessGuard? guard = null,
        TimeProvider? clock = null,
        ExplorerTrackerOptions? options = null,
        Action<string>? log = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _factory = factory ?? new ComShellWindowsGatewayFactory(_rcw);
        _win = win ?? new Win32Shell();
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new ExplorerTrackerOptions();
        _log = log ?? (_ => { });
        _guard = guard ?? TrackerProcessGuard.Shared;
        _publisher = new StatePublisher(Deliver);
        _machine = new TrackerStateMachine(_clock, _publisher, _guard, _options.Limits);
        _hooks = new HookThread(winEvents ?? new Win32WinEventApi(), _router, _guard, _log, _options.Hook);
        _watchdog = new WatchdogThread(_guard, w => _supervisor!.OnWorkerWedged(w), null, _log);
        _supervisor = new WorkerSupervisor(
            _machine, _router, _publisher, _watchdog, CreateWorker, _log,
            _options.WorkerStartTimeout, _options.WorkerStopTimeout,
            beforeWorker: StartHooks, onStopping: () => _hooks.Stop());
    }

    public event EventHandler<ActiveFolderChangedEventArgs>? ActiveFolderChanged;

    public ActiveFolderState Current => _publisher.Current;

    public StartResult Start() => Volatile.Read(ref _disposed) != 0 ? StartResult.Disabled : _supervisor.Start();

    public void Stop() => _supervisor.Stop();

    public RetryResult Retry() => _supervisor.Retry();

    public void NotifyShellRestarted()
    {
        var worker = _supervisor.Current;
        if (worker?.State is WorkerState state)
        {
            worker.Post(state.Engine.OnShellRestarted);
        }
    }

    public TrackerStats GetStats()
    {
        var (p50, p95, max) = _counters.Percentiles();
        return new TrackerStats
        {
            Evaluations = _counters.Evaluations,
            Rebuilds = _counters.Rebuilds,
            Recoveries = _counters.Recoveries,
            PublishedStates = _counters.PublishedStates,
            EvaluationP50Ms = p50,
            EvaluationP95Ms = p95,
            EvaluationMaxMs = max,
            AbandonedWorkers = _guard.AbandonedThreads,
            Replacements = _supervisor.Replacements,
            LiveRcws = _rcw.Live,
            CrossThreadReleases = _rcw.CrossThreadReleases,
            HooksActive = _hooks.HooksActive,
            CancelCalls = _watchdog.CancelCalls,
            MaxCancelCallMs = _watchdog.MaxCancelCallMs,
            State = _machine.State,
            DisabledReason = _machine.DisabledReason,
            CanRetry = _machine.CanRetry,
            PermanentlyDisabled = _machine.IsPermanentlyDisabled,
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _supervisor.Stop();
        }
    }

    // ---- composition -----------------------------------------------------------------------------------

    private void StartHooks()
    {
        var result = _hooks.Start();
        if (result is not (HookStartResult.Started or HookStartResult.AlreadyStarted))
        {
            _log($"WinEvent hooks are not active ({result}); the tracker runs on the poll and the reconciliation (04 B.7, J-H1).");
        }
    }

    /// <summary>The factory for a worker generation: every delegate below runs on that worker's thread.</summary>
    private StaWorkerHost CreateWorker(int generation) =>
        new(
            generation,
            onStart: host =>
            {
                var engine = new TrackerEngine(generation, _factory, _win, _publisher, _machine, new GenerationScope(generation, _publisher, _hooks), _counters, _clock, _options.Scheduler, _log);
                var timer = new Timer(static state => ((StaWorkerHost)state!).TryPost(), host, Timeout.Infinite, Timeout.Infinite);
                host.State = new WorkerState(engine, timer);
                engine.Start();
                ArmTimer(host, engine.Tick());
            },
            onWake: host =>
            {
                if (host.State is not WorkerState state)
                {
                    return;
                }

                state.Engine.OnSignals(_router.Consume(host));
                ArmTimer(host, state.Engine.Tick());
            },
            onStop: host =>
            {
                if (host.State is WorkerState state)
                {
                    state.Timer.Dispose();
                    state.Engine.Dispose();                   // releases the cache and the root on this thread
                }
            },
            clock: _clock,
            watchdogOptions: _options.Watchdog,
            log: _log,
            afterBegin: _watchdog.Poke);

    private static void ArmTimer(StaWorkerHost host, TimeSpan? due)
    {
        if (host.State is not WorkerState state || host.IsAbandoned)
        {
            return;
        }

        if (due is null)
        {
            state.Timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        else if (due.Value <= TimeSpan.Zero)
        {
            state.Timer.Change(Timeout.Infinite, Timeout.Infinite);
            host.TryPost();                                   // more is due: another turn of the message loop, not a spin
        }
        else
        {
            state.Timer.Change(due.Value, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Latest-wins (04 L): the gate calls this under its lock, so it only stores and queues.</summary>
    private void Deliver(ActiveFolderState state)
    {
        bool post;
        lock (_deliveryGate)
        {
            _pendingDelivery = state;
            post = !_deliveryQueued;
            _deliveryQueued = true;
        }

        if (post && !_ui.Post(RaiseOnUiThread))
        {
            lock (_deliveryGate)
            {
                _deliveryQueued = false;                      // the UI queue is shutting down: drop silently
            }
        }
    }

    private void RaiseOnUiThread()
    {
        ActiveFolderState? latest;
        lock (_deliveryGate)
        {
            latest = _pendingDelivery;
            _pendingDelivery = null;
            _deliveryQueued = false;
        }

        if (latest is { } state)
        {
            try
            {
                ActiveFolderChanged?.Invoke(this, new ActiveFolderChangedEventArgs(state));
            }
            catch (Exception ex)
            {
                _log($"ActiveFolderChanged handler failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

internal static class ExplorerTrackerServices
{
    /// <summary>
    /// Registers <see cref="IExplorerTracker"/> as a singleton. It is <b>not started</b> by registering or resolving it:
    /// T3.4 starts it behind the panel setting (REQ-037). <c>App.xaml.cs</c> does not call this yet (guard test).
    /// </summary>
    public static IServiceCollection AddExplorerTracker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IExplorerTracker>(sp =>
        {
            var log = sp.GetService<Serilog.ILogger>();
            return new ExplorerTracker(sp.GetRequiredService<IUiDispatcher>(), log: log is null ? null : m => log.Warning("{TrackerMessage}", m));
        });
        return services;
    }
}
