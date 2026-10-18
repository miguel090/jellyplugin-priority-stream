using System;
using Jellyfin.Plugin.LiveTvPriority.Configuration;
using Jellyfin.Plugin.LiveTvPriority.Preemption;
using Xunit;

namespace Jellyfin.Plugin.LiveTvPriority.Tests;

public class PreemptionPlannerTests
{
    private static readonly Guid Priority = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid OtherPriority = Guid.NewGuid();
    private static readonly Guid ChannelA = Guid.NewGuid();
    private static readonly Guid ChannelB = Guid.NewGuid();
    private static readonly Guid ChannelC = Guid.NewGuid();

    private static PluginConfiguration Config() => new() { PriorityUserIds = [Priority, OtherPriority] };

    private static PreemptionRequest Request(Guid channel, string device = "prio-phone")
        => new(Priority, "Prio", device, channel, "Requested");

    private static LiveSession Session(Guid user, string device, Guid channel, string? liveStreamId = "ls1")
        => new("s-" + device, user, "U-" + device, device, device, "Client", channel, "Ch", liveStreamId);

    [Fact]
    public void A_NonPriorityOnOtherChannel_IsKicked()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelB), [Session(Other, "living-room", ChannelA)], [], Config(), 1);

        var victim = Assert.Single(plan.Victims);
        Assert.Equal(VictimKind.Kick, victim.Kind);
        Assert.Equal("living-room", victim.Session.DeviceId);
    }

    [Fact]
    public void B_SameChannel_NothingToDo()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelA), [Session(Other, "living-room", ChannelA)], [], Config(), 1);

        Assert.False(plan.HasWork);
        Assert.Contains(plan.Notes, n => n.Contains("shared", StringComparison.Ordinal));
    }

    [Fact]
    public void C_RecordingOnOtherChannel_ProtectedByDefault()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelB), [], [new ActiveRecording("t1", ChannelA, "News")], Config(), 1);

        Assert.False(plan.HasWork);
        Assert.Contains(plan.Notes, n => n.Contains("recording", StringComparison.Ordinal));
    }

    [Fact]
    public void C_RecordingWithViewerOnSameChannel_ViewerNotKickedWhenRecordingProtected()
    {
        // Kicking the viewer would not free the tuner, the recording still holds it.
        var plan = PreemptionPlanner.Plan(
            Request(ChannelB),
            [Session(Other, "living-room", ChannelA)],
            [new ActiveRecording("t1", ChannelA, "News")],
            Config(),
            1);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void C_RecordingPreemptable_WhenEnabled()
    {
        var config = Config();
        config.PreemptRecordings = true;
        var plan = PreemptionPlanner.Plan(
            Request(ChannelB),
            [Session(Other, "living-room", ChannelA)],
            [new ActiveRecording("t1", ChannelA, "News")],
            config,
            1);

        Assert.Single(plan.RecordingsToCancel);
        Assert.Single(plan.Victims);
    }

    [Fact]
    public void C_RecordingOnRequestedChannel_IsShared()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelA), [], [new ActiveRecording("t1", ChannelA, "News")], Config(), 1);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void D_OwnOtherDevice_KickedByDefault()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelB), [Session(Priority, "prio-tv", ChannelA)], [], Config(), 1);

        var victim = Assert.Single(plan.Victims);
        Assert.Equal(VictimKind.Kick, victim.Kind);
    }

    [Fact]
    public void D_OwnOtherDevice_ProtectedWhenDisabled()
    {
        var config = Config();
        config.PreemptOwnDevices = false;
        var plan = PreemptionPlanner.Plan(Request(ChannelB), [Session(Priority, "prio-tv", ChannelA)], [], config, 1);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void D_OwnOtherDeviceOnSameChannel_Shared()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelA), [Session(Priority, "prio-tv", ChannelA)], [], Config(), 1);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void SameDeviceChannelSwitch_ReleasedSilently()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelB, "prio-phone"), [Session(Priority, "PRIO-PHONE", ChannelA)], [], Config(), 1);

        var victim = Assert.Single(plan.Victims);
        Assert.Equal(VictimKind.SameDeviceSwitch, victim.Kind);
    }

    [Fact]
    public void OtherPriorityUser_ProtectedByDefault()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelB), [Session(OtherPriority, "spouse-tv", ChannelA)], [], Config(), 1);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void OtherPriorityUser_KickedWhenAllowed()
    {
        var config = Config();
        config.PriorityUsersPreemptEachOther = true;
        var plan = PreemptionPlanner.Plan(Request(ChannelB), [Session(OtherPriority, "spouse-tv", ChannelA)], [], config, 1);

        Assert.Single(plan.Victims);
    }

    [Fact]
    public void AllViewersOfTheChannelAreKicked()
    {
        var plan = PreemptionPlanner.Plan(
            Request(ChannelB),
            [Session(Other, "tv1", ChannelA), Session(Other, "tv2", ChannelA)],
            [],
            Config(),
            1);

        Assert.Equal(2, plan.Victims.Count);
    }

    [Fact]
    public void ProtectedViewerOnChannel_NobodyOnThatChannelIsKicked()
    {
        var plan = PreemptionPlanner.Plan(
            Request(ChannelB),
            [Session(Other, "tv1", ChannelA), Session(OtherPriority, "spouse-tv", ChannelA)],
            [],
            Config(),
            1);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void FreeTuner_NothingToDo()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelB), [Session(Other, "tv1", ChannelA)], [], Config(), 2);

        Assert.False(plan.HasWork);
    }

    [Fact]
    public void TwoTunersBusy_FreesOnlyOneChannel_PreferringFewestViewers()
    {
        var plan = PreemptionPlanner.Plan(
            Request(ChannelC),
            [Session(Other, "tv1", ChannelA), Session(Other, "tv2", ChannelA), Session(Other, "tv3", ChannelB)],
            [],
            Config(),
            2);

        var victim = Assert.Single(plan.Victims);
        Assert.Equal(ChannelB, victim.Session.ChannelId);
    }

    [Fact]
    public void TwoTunersBusy_PrefersOwnChannelSwitch()
    {
        var plan = PreemptionPlanner.Plan(
            Request(ChannelC, "prio-phone"),
            [Session(Other, "tv1", ChannelA), Session(Priority, "prio-phone", ChannelB)],
            [],
            Config(),
            2);

        var victim = Assert.Single(plan.Victims);
        Assert.Equal(VictimKind.SameDeviceSwitch, victim.Kind);
    }

    [Fact]
    public void NotEnoughPreemptableChannels_LeavesEverything()
    {
        var plan = PreemptionPlanner.Plan(
            Request(ChannelC),
            [Session(OtherPriority, "spouse-tv", ChannelA)],
            [new ActiveRecording("t1", ChannelB, "News")],
            Config(),
            2);

        Assert.False(plan.HasWork);
        Assert.Contains(plan.Notes, n => n.StartsWith("Need to free", StringComparison.Ordinal));
    }

    [Fact]
    public void NoSessions_NothingToDo()
    {
        var plan = PreemptionPlanner.Plan(Request(ChannelB), [], [], Config(), 1);

        Assert.False(plan.HasWork);
    }
}
