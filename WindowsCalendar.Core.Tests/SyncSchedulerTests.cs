using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Tests;

public sealed class SyncSchedulerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new();
    private readonly Channel<string> _runs = Channel.CreateUnbounded<string>();

    private async Task<string> NextRun()
    {
        using var cts = new CancellationTokenSource(Wait);
        return await _runs.Reader.ReadAsync(cts.Token);
    }

    private async Task AssertNoRun()
    {
        await Task.Delay(200);
        Assert.False(_runs.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Timer_RunsEveryFiveMinutes()
    {
        using var scheduler = new SyncScheduler((r, _) => { _runs.Writer.TryWrite(r); return Task.CompletedTask; }, _ => { }, _time);

        _time.Advance(TimeSpan.FromMinutes(4));
        await AssertNoRun();
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("timer", await NextRun());
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("timer", await NextRun());
    }

    [Fact]
    public async Task RequestsDuringRun_CauseExactlyOneMoreRun()
    {
        var release = new TaskCompletionSource();
        using var scheduler = new SyncScheduler(async (r, _) =>
        {
            _runs.Writer.TryWrite(r);
            if (r == "first")
            {
                await release.Task;
            }
        }, _ => { }, _time);

        scheduler.Request("first");
        Assert.Equal("first", await NextRun());
        scheduler.Request("focus");
        scheduler.Request("network");
        release.SetResult();

        Assert.Equal("network", await NextRun());
        await AssertNoRun();
    }

    /// <summary>A scheduler whose "first" run waits for <paramref name="release"/>; runs are written as reason:collections.</summary>
    private SyncScheduler Targeted(TaskCompletionSource release) => new(async (r, only, _) =>
    {
        _runs.Writer.TryWrite(only is null ? r : $"{r}:{string.Join(",", only.Order())}");
        if (r == "first")
        {
            await release.Task;
        }
    }, _ => { }, _time);

    [Fact]
    public async Task CollectionRequests_RunForThoseCollectionsOnly()
    {
        var release = new TaskCompletionSource();
        using var scheduler = Targeted(release);

        scheduler.RequestCollection("shown", "c1");
        Assert.Equal("shown:c1", await NextRun());
        scheduler.Request("first");
        Assert.Equal("first", await NextRun());
        scheduler.RequestCollection("shown", "c2");
        scheduler.RequestCollection("shown", "c3");
        release.SetResult();

        Assert.Equal("shown:c2,c3", await NextRun());
        await AssertNoRun();
    }

    [Fact]
    public async Task FullRequest_WinsOverPendingCollectionRequest()
    {
        var release = new TaskCompletionSource();
        using var scheduler = Targeted(release);

        scheduler.Request("first");
        Assert.Equal("first", await NextRun());
        scheduler.RequestCollection("shown", "c1");
        scheduler.Request("local change");
        scheduler.RequestCollection("shown", "c2");
        release.SetResult();

        Assert.Equal("local change", await NextRun());
        await AssertNoRun();
    }

    [Fact]
    public async Task FailingRun_IsReportedAndSchedulerKeepsGoing()
    {
        var errors = Channel.CreateUnbounded<Exception>();
        using var scheduler = new SyncScheduler((r, _) =>
        {
            _runs.Writer.TryWrite(r);
            return r == "bad" ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }, ex => errors.Writer.TryWrite(ex), _time);

        scheduler.Request("bad");
        Assert.Equal("bad", await NextRun());
        using var cts = new CancellationTokenSource(Wait);
        Assert.Equal("boom", (await errors.Reader.ReadAsync(cts.Token)).Message);

        scheduler.Request("sync now");
        Assert.Equal("sync now", await NextRun());
    }

    [Fact]
    public async Task AfterDispose_RequestsAreIgnored()
    {
        var scheduler = new SyncScheduler((r, _) => { _runs.Writer.TryWrite(r); return Task.CompletedTask; }, _ => { }, _time);
        scheduler.Dispose();

        scheduler.Request("sync now");
        _time.Advance(TimeSpan.FromMinutes(5));
        await AssertNoRun();
    }
}
