#pragma warning disable xUnit1069 // Timeouts guard against a hang in the scheduling plumbing.
using FolderNotes.App.Services;
using FolderNotes.Core.Storage;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;

namespace FolderNotes.App.Tests.Services;

/// <summary>02 § Orphaned page and orphan scan: 10 s after start, every 6 h, on demand, one at a time.</summary>
public sealed class OrphanScanServiceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private int _runs;
    private TaskCompletionSource? _hold;
    private Exception? _fail;

    private async Task<OrphanScanReport> ScanAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _runs);
        if (_hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        return _fail is { } ex ? throw ex : new OrphanScanReport(3, 0, 0, 0);
    }

    private async Task WaitForRunsAsync(int n)
    {
        for (var i = 0; i < 200 && Volatile.Read(ref _runs) < n; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task REQ061_Schedule_FirstAfter10Seconds_ThenEvery6Hours()
    {
        using var sut = new OrphanScanService(ScanAsync, _clock, Logger.None);
        sut.Start();

        _clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _runs.ShouldBe(0);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForRunsAsync(1);
        _runs.ShouldBe(1);

        _clock.Advance(TimeSpan.FromHours(6));
        await WaitForRunsAsync(2);
        _runs.ShouldBe(2);
    }

    [Fact(Timeout = 10_000)]
    public async Task REQ061_ScanNow_WhileOneRuns_JoinsIt()
    {
        using var sut = new OrphanScanService(ScanAsync, _clock, Logger.None);
        _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = sut.ScanNowAsync();
        var second = sut.ScanNowAsync();
        await WaitForRunsAsync(1);
        _hold.SetResult();

        (await first).Checked.ShouldBe(3);
        (await second).ShouldBeSameAs(await first);
        _runs.ShouldBe(1);

        _hold = null;
        await sut.ScanNowAsync();
        _runs.ShouldBe(2);
    }

    [Fact(Timeout = 10_000)]
    public async Task REQ061_AFailedScheduledScan_IsLogged_AndTheNextRunRetries()
    {
        using var sut = new OrphanScanService(ScanAsync, _clock, Logger.None);
        _fail = new IOException("disk went away");
        sut.Start();
        _clock.Advance(TimeSpan.FromSeconds(10));
        await WaitForRunsAsync(1);

        _fail = null;
        (await sut.ScanNowAsync()).Checked.ShouldBe(3); // not stuck on the failed one
    }

    [Fact(Timeout = 10_000)]
    public async Task REQ061_OnDemandFailure_ReachesTheCaller()
    {
        using var sut = new OrphanScanService(ScanAsync, _clock, Logger.None);
        _fail = new IOException("disk went away");

        await Should.ThrowAsync<IOException>(sut.ScanNowAsync);
    }

    [Fact(Timeout = 10_000)]
    public async Task REQ061_Dispose_StopsTheSchedule()
    {
        var sut = new OrphanScanService(ScanAsync, _clock, Logger.None);
        sut.Start();
        sut.Dispose();

        _clock.Advance(TimeSpan.FromHours(7));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _runs.ShouldBe(0);
        Should.Throw<ObjectDisposedException>(sut.ScanNowAsync);
    }
}
