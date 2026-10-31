using FolderNotes.App.Services;
using FolderNotes.App.Themes;
using Microsoft.UI.Xaml;
using Serilog.Core;

namespace FolderNotes.App.Tests.Themes;

/// <summary>REQ-081 / REQ-082: the theme setting reaches every window; high contrast is marshalled to the UI thread.</summary>
public sealed class ThemeServiceTests
{
    private readonly FakeSignal _hc = new();
    private readonly QueueDispatcher _ui = new();

    [Theory]
    [InlineData(AppTheme.System, ElementTheme.Default)]
    [InlineData(AppTheme.Light, ElementTheme.Light)]
    [InlineData(AppTheme.Dark, ElementTheme.Dark)]
    public void REQ081_MapsToElementTheme(AppTheme theme, ElementTheme expected) =>
        ThemeService.ToElementTheme(theme).ShouldBe(expected);

    [Fact]
    public void REQ081_Register_AppliesCurrentTheme_AndLaterChangesReachEveryTarget()
    {
        using var service = new ThemeService(_hc, _ui, Logger.None);
        var a = new Target();
        var b = new Target();
        service.Register(a);
        a.Applied.ShouldBe([AppTheme.System]);

        service.Theme = AppTheme.Dark;
        service.Register(b);
        service.Theme = AppTheme.Light;

        a.Applied.ShouldBe([AppTheme.System, AppTheme.Dark, AppTheme.Light]);
        b.Applied.ShouldBe([AppTheme.Dark, AppTheme.Light]);
    }

    [Fact]
    public void Unregister_StopsUpdates_AndSameValueIsANoOp()
    {
        using var service = new ThemeService(_hc, _ui, Logger.None);
        var target = new Target();
        var raised = 0;
        service.ThemeChanged += (_, _) => raised++;
        service.Register(target);

        service.Theme = AppTheme.System; // unchanged
        raised.ShouldBe(0);

        service.Unregister(target);
        service.Theme = AppTheme.Dark;
        target.Applied.ShouldBe([AppTheme.System]);
        raised.ShouldBe(1);
    }

    [Fact]
    public void InvalidTheme_Throws()
    {
        using var service = new ThemeService(_hc, _ui, Logger.None);
        Should.Throw<ArgumentOutOfRangeException>(() => service.Theme = (AppTheme)7);
    }

    [Fact]
    public void REQ082_HighContrastChange_IsRaisedOnlyWhenTheUiQueueRuns()
    {
        using var service = new ThemeService(_hc, _ui, Logger.None);
        var raised = 0;
        service.ThemeChanged += (_, _) => raised++;
        service.IsHighContrast.ShouldBeFalse();

        _hc.Set(true);
        raised.ShouldBe(0);
        service.IsHighContrast.ShouldBeFalse();

        _ui.Pump();
        service.IsHighContrast.ShouldBeTrue();
        raised.ShouldBe(1);

        _hc.Raise(); // spurious signal, no change
        _ui.Pump();
        raised.ShouldBe(1);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var service = new ThemeService(_hc, _ui, Logger.None);
        service.Dispose();
        _hc.Set(true);
        _ui.Pending.ShouldBe(0);
    }

    private sealed class Target : IThemeTarget
    {
        public List<AppTheme> Applied { get; } = [];

        public void ApplyTheme(AppTheme theme) => Applied.Add(theme);
    }

    private sealed class FakeSignal : IHighContrastSignal
    {
        public bool IsOn { get; private set; }

        public event EventHandler? Changed;

        public void Set(bool on)
        {
            IsOn = on;
            Raise();
        }

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class QueueDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int Pending => _queue.Count;

        public bool Post(Action action)
        {
            _queue.Enqueue(action);
            return true;
        }

        public void Pump()
        {
            while (_queue.TryDequeue(out var a))
            {
                a();
            }
        }
    }
}
