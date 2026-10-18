using System;
using Jellyfin.Plugin.LiveTvPriority.Preemption;
using Xunit;

namespace Jellyfin.Plugin.LiveTvPriority.Tests;

public class BusyAndExemptTests
{
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();
    private static readonly Guid ChannelA = Guid.NewGuid();
    private static readonly Guid ChannelB = Guid.NewGuid();
    private static readonly Guid ChannelC = Guid.NewGuid();

    private static LiveSession Session(Guid user, string name, string device, Guid channel, string channelName)
        => new("s-" + device, user, name, device, device, "Client", channel, channelName, "ls-" + device);

    [Fact]
    public void Busy_OtherChannelHeld_SuggestsTheTunedChannel()
    {
        var tuned = PreemptionPlanner.CheckBusy(ChannelB, "carol-tv", [Session(Bob, "Bob", "bob-tv", ChannelA, "News")], [], 1);

        var channel = Assert.Single(tuned!);
        Assert.Equal(ChannelA, channel.ChannelId);
        Assert.Equal("News", channel.ChannelName);
        Assert.Equal(["Bob"], channel.Watchers);
    }

    [Fact]
    public void Busy_SameChannel_IsShared()
    {
        Assert.Null(PreemptionPlanner.CheckBusy(ChannelA, "carol-tv", [Session(Bob, "Bob", "bob-tv", ChannelA, "News")], [], 1));
    }

    [Fact]
    public void Busy_FreeTuner_NotBusy()
    {
        Assert.Null(PreemptionPlanner.CheckBusy(ChannelB, "carol-tv", [Session(Bob, "Bob", "bob-tv", ChannelA, "News")], [], 2));
    }

    [Fact]
    public void Busy_OwnDeviceSwitchingChannel_NotBusy()
    {
        Assert.Null(PreemptionPlanner.CheckBusy(ChannelB, "bob-tv", [Session(Bob, "Bob", "BOB-TV", ChannelA, "News")], [], 1));
    }

    [Fact]
    public void Busy_Recording_ListedAsARecording()
    {
        var tuned = PreemptionPlanner.CheckBusy(ChannelB, "carol-tv", [], [new ActiveRecording("t1", ChannelA, "Evening News", "News")], 1);

        var channel = Assert.Single(tuned!);
        Assert.Equal("News", channel.ChannelName);
        Assert.Equal(["a recording"], channel.Watchers);
    }

    [Fact]
    public void Busy_RequestedChannelBeingRecorded_IsShared()
    {
        Assert.Null(PreemptionPlanner.CheckBusy(ChannelA, "carol-tv", [], [new ActiveRecording("t1", ChannelA, "Evening News", "News")], 1));
    }

    [Fact]
    public void Message_SingleChannel()
    {
        var text = PreemptionService.FormatBusyMessage(
            "{user} is watching {channel}. You can watch {channel} instead.",
            [new TunedChannel(ChannelA, "News", ["Bob", "Carol"])]);

        Assert.Equal("Bob, Carol is watching News. You can watch News instead.", text);
    }

    [Fact]
    public void Message_SeveralChannels()
    {
        var text = PreemptionService.FormatBusyMessage(
            "You can watch {channel}.",
            [new TunedChannel(ChannelA, "News", ["Bob"]), new TunedChannel(ChannelC, "Sports", ["Carol"])]);

        Assert.Equal("You can watch News or Sports.", text);
    }
}
