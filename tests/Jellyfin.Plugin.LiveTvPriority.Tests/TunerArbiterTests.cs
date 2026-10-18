using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvPriority.Preemption;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.LiveTvPriority.Tests;

public class TunerArbiterTests
{
    private static readonly Guid ChannelA = Guid.NewGuid();
    private static readonly Guid ChannelB = Guid.NewGuid();

    [Fact]
    public async Task Enter_IsExclusive()
    {
        using var arbiter = new TunerArbiter();
        using var first = await arbiter.EnterAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);

        var second = await arbiter.EnterAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Null(second);
    }

    [Fact]
    public async Task Enter_DoubleDisposeReleasesOnce()
    {
        using var arbiter = new TunerArbiter();
        var handle = await arbiter.EnterAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        handle!.Dispose();
        handle.Dispose();

        using var a = await arbiter.EnterAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(a);
        var b = await arbiter.EnterAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Null(b);
    }

    [Fact]
    public async Task WaitForTurn_QueuesBehindInFlightPreemption()
    {
        using var arbiter = new TunerArbiter();
        var handle = await arbiter.EnterAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        var waiter = arbiter.WaitForTurnAsync(ChannelA, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        handle!.Dispose();
        Assert.True(await waiter);
    }

    [Fact]
    public async Task WaitForTurn_ReservationForOtherChannel_WaitsUntilReleased()
    {
        using var arbiter = new TunerArbiter();
        arbiter.Reserve(ChannelA, "prio", TimeSpan.FromSeconds(30));

        var waiter = arbiter.WaitForTurnAsync(ChannelB, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        arbiter.Release(ChannelA, "PRIO");
        Assert.True(await waiter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForTurn_ReservationForSameChannel_DoesNotWait()
    {
        using var arbiter = new TunerArbiter();
        arbiter.Reserve(ChannelA, "prio", TimeSpan.FromSeconds(30));

        Assert.False(await arbiter.WaitForTurnAsync(ChannelA, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reservation_Expires()
    {
        var time = new FakeTimeProvider();
        using var arbiter = new TunerArbiter(time);
        arbiter.Reserve(ChannelA, "prio", TimeSpan.FromSeconds(20));
        Assert.True(arbiter.IsReservedBy("prio"));

        time.Advance(TimeSpan.FromSeconds(21));

        Assert.False(arbiter.IsReservedBy("prio"));
        Assert.False(await arbiter.WaitForTurnAsync(ChannelB, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public void ReleaseFor_OnlyOwnerReleases()
    {
        using var arbiter = new TunerArbiter();
        arbiter.Reserve(ChannelA, "prio", TimeSpan.FromSeconds(20));

        arbiter.ReleaseFor("someone-else");
        Assert.True(arbiter.IsReservedBy("prio"));

        arbiter.ReleaseFor("prio");
        Assert.False(arbiter.IsReservedBy("prio"));
    }
}
