using FolderNotes.Core.Storage;
using Serilog;

namespace FolderNotes.App.Services;

/// <summary>
/// Runs the orphan scan (03 § Orphan detection) 10 s after startup, every 6 h, and on demand
/// (02 § Orphaned page and orphan scan). One scan at a time: a request while one runs joins it.
/// Scans run on the thread pool; their writes go through the store's single writer (ADR-011).
/// </summary>
internal sealed class OrphanScanService : IDisposable
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly Func<CancellationToken, Task<OrphanScanReport>> _scan;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Task<OrphanScanReport>? _running;
    private ITimer? _timer;
    private bool _disposed;

    public OrphanScanService(Func<CancellationToken, Task<OrphanScanReport>> scan, TimeProvider clock, ILogger log)
    {
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<OrphanScanService>();
    }

    /// <summary>Starts the schedule (first run after <see cref="StartupDelay"/>).</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer ??= _clock.CreateTimer(_ => OnTimer(), null, StartupDelay, Interval);
        }
    }

    /// <summary>Runs a scan now, or joins the one that's running. Failures reach the caller.</summary>
    public Task<OrphanScanReport> ScanNowAsync() => RunAsync("on demand");

    private async void OnTimer()
    {
        try
        {
            await RunAsync("scheduled");
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
#pragma warning disable CA1031 // A failed background scan is logged; the next run retries.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "The scheduled orphan scan failed");
        }
    }

    private Task<OrphanScanReport> RunAsync(string why)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running is { IsCompleted: false } running)
            {
                return running;
            }

            var ct = _stop.Token;
            return _running = Task.Run(() => ScanAsync(why, ct), ct);
        }
    }

    private async Task<OrphanScanReport> ScanAsync(string why, CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        var report = await _scan(ct).ConfigureAwait(false);
        _log.Information(
            "Orphan scan ({Why}) checked {Checked} notes in {Ms:F0} ms: {Relinked} relinked, {Orphaned} orphaned, {Recovered} recovered, {Conflicts} conflicts, {Skipped} skipped",
            why, report.Checked, _clock.GetElapsedTime(started).TotalMilliseconds, report.Relinked, report.Orphaned, report.Recovered, report.Conflicts, report.Skipped);
        return report;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
        }

        _stop.Cancel();
        _stop.Dispose();
    }
}
