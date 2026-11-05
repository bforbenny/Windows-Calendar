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
