using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiveTvPriority.Configuration;
using Jellyfin.Plugin.LiveTvPriority.Preemption;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LiveTvPriority.Tests;

public class PreemptionServiceTests
{
    private static readonly Guid PriorityUser = Guid.NewGuid();
    private static readonly Guid OtherUser = Guid.NewGuid();
    private static readonly Guid ChannelA = Guid.NewGuid();
    private static readonly Guid ChannelB = Guid.NewGuid();

    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<IMediaSourceManager> _mediaSourceManager = new();
    private readonly Mock<ILiveTvManager> _liveTvManager = new();
    private readonly Mock<ITranscodeManager> _transcodeManager = new();
    private readonly Mock<IConfigurationManager> _configurationManager = new();
    private readonly List<SessionInfo> _sessions = new();
    private readonly HashSet<string> _openStreams = new();

    public PreemptionServiceTests()
    {
        _sessionManager.SetupGet(m => m.Sessions).Returns(() => _sessions);

        // Streams are open until closed once (single consumer), unless a test overrides this.
        var stream = new Mock<ILiveStream>().Object;
        _mediaSourceManager.Setup(m => m.GetLiveStreamInfo(It.IsAny<string>())).Returns<string>(id => _openStreams.Contains(id) ? stream : null!);
        _mediaSourceManager.Setup(m => m.CloseLiveStream(It.IsAny<string>())).Callback<string>(id => _openStreams.Remove(id)).Returns(Task.CompletedTask);
        _liveTvManager
            .Setup(m => m.GetTimers(It.IsAny<TimerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult<TimerInfoDto>(Array.Empty<TimerInfoDto>()));
        _configurationManager
            .Setup(m => m.GetConfiguration("livetv"))
            .Returns(new LiveTvOptions { TunerHosts = [new TunerHostInfo { TunerCount = 1 }] });
    }

    private static PluginConfiguration Config() => new()
    {
        PriorityUserIds = [PriorityUser],
        TunerReleaseTimeoutSeconds = 1,
        UpstreamSettleDelayMs = 0
    };

    private static PreemptionRequest Request(Guid channel) => new(PriorityUser, "Alice", "alice-phone", channel, "Channel B");

    private PreemptionService CreateService() => new(
        _sessionManager.Object,
        _mediaSourceManager.Object,
        _liveTvManager.Object,
        _transcodeManager.Object,
        _configurationManager.Object,
        NullLogger<PreemptionService>.Instance);

    private SessionInfo AddLiveSession(string id, Guid userId, string deviceId, Guid channelId, string? liveStreamId)
    {
        var session = new SessionInfo(_sessionManager.Object, NullLogger.Instance)
        {
            Id = id,
            UserId = userId,
            UserName = "Bob",
            DeviceId = deviceId,
            DeviceName = deviceId,
            Client = "Android TV",
            NowPlayingItem = new BaseItemDto { Id = channelId, Type = BaseItemKind.TvChannel, Name = "Channel A" },
            PlayState = new PlayerStateInfo { LiveStreamId = liveStreamId }
        };
        _sessions.Add(session);
        if (liveStreamId is not null)
        {
            _openStreams.Add(liveStreamId);
        }

        return session;
    }

    [Fact]
    public async Task KicksOtherChannel_SendsMessageStopAndClosesUntilReleased()
    {
        AddLiveSession("victim", OtherUser, "living-room", ChannelA, "ls-a");

        // Two consumers (player + transcoder): the stream disappears after the second close.
        var stream = new Mock<ILiveStream>().Object;
        var closes = 0;
        _mediaSourceManager.Setup(m => m.GetLiveStreamInfo("ls-a")).Returns(() => closes >= 2 ? null! : stream);
        _mediaSourceManager.Setup(m => m.CloseLiveStream("ls-a")).Callback(() => closes++).Returns(Task.CompletedTask);

        var preempted = await CreateService().PreemptIfNeededAsync(Request(ChannelB), Config());

        Assert.True(preempted);
        Assert.Equal(2, closes);
        _sessionManager.Verify(
            m => m.SendMessageCommand(null!, "victim", It.Is<MessageCommand>(c => c.Text == "Live TV was taken over by Alice."), It.IsAny<CancellationToken>()),
            Times.Once);
        _sessionManager.Verify(
            m => m.SendPlaystateCommand(null!, "victim", It.Is<PlaystateRequest>(r => r.Command == PlaystateCommand.Stop), It.IsAny<CancellationToken>()),
            Times.Once);
        _transcodeManager.Verify(m => m.KillTranscodingJobs("living-room", null, It.IsAny<Func<string, bool>>()), Times.Once);
    }

    [Fact]
    public async Task MessageDisabled_NoMessageButStillStops()
    {
        AddLiveSession("victim", OtherUser, "living-room", ChannelA, "ls-a");
        var config = Config();
        config.SendKickMessage = false;

        await CreateService().PreemptIfNeededAsync(Request(ChannelB), config);

        _sessionManager.Verify(m => m.SendMessageCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        _sessionManager.Verify(m => m.SendPlaystateCommand(null!, "victim", It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SameChannel_DoesNothing()
    {
        AddLiveSession("other", OtherUser, "living-room", ChannelB, "ls-b");

        var preempted = await CreateService().PreemptIfNeededAsync(Request(ChannelB), Config());

        Assert.False(preempted);
        _sessionManager.Verify(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mediaSourceManager.Verify(m => m.CloseLiveStream(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ClientIgnoresStopAndStreamNeverCloses_ReturnsAfterTimeout()
    {
        AddLiveSession("stubborn", OtherUser, "firestick", ChannelA, "ls-a");
        _mediaSourceManager.Setup(m => m.GetLiveStreamInfo("ls-a")).Returns(new Mock<ILiveStream>().Object);
        _sessionManager
            .Setup(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no controller"));

        var start = DateTime.UtcNow;
        var preempted = await CreateService().PreemptIfNeededAsync(Request(ChannelB), Config());

        Assert.True(preempted);
        Assert.InRange(DateTime.UtcNow - start, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SameDeviceSwitch_ClosesSilently()
    {
        AddLiveSession("mine", PriorityUser, "alice-phone", ChannelA, "ls-a");

        var preempted = await CreateService().PreemptIfNeededAsync(Request(ChannelB), Config());

        Assert.True(preempted);
        _sessionManager.Verify(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _sessionManager.Verify(m => m.SendMessageCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActiveRecordingProtected_NothingHappens()
    {
        _liveTvManager
            .Setup(m => m.GetTimers(It.IsAny<TimerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult<TimerInfoDto>([new TimerInfoDto { Id = "t1", ChannelId = ChannelA, Name = "News", Status = RecordingStatus.InProgress }]));

        var preempted = await CreateService().PreemptIfNeededAsync(Request(ChannelB), Config());

        Assert.False(preempted);
        _liveTvManager.Verify(m => m.CancelTimer(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ActiveRecordingPreemptable_CancelsTimerAndWaits()
    {
        var active = true;
        _liveTvManager
            .Setup(m => m.GetTimers(It.IsAny<TimerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new QueryResult<TimerInfoDto>(active
                ? [new TimerInfoDto { Id = "t1", ChannelId = ChannelA, Name = "News", Status = RecordingStatus.InProgress }]
                : Array.Empty<TimerInfoDto>()));
        _liveTvManager.Setup(m => m.CancelTimer("t1")).Callback(() => active = false).Returns(Task.CompletedTask);
        var config = Config();
        config.PreemptRecordings = true;

        var preempted = await CreateService().PreemptIfNeededAsync(Request(ChannelB), config);

        Assert.True(preempted);
        _liveTvManager.Verify(m => m.CancelTimer("t1"), Times.Once);
    }

    [Fact]
    public async Task StaleSessionWhoseStreamIsClosed_IsIgnored()
    {
        AddLiveSession("stale", OtherUser, "living-room", ChannelA, "ls-a");
        _openStreams.Clear();

        var preempted = await CreateService().PreemptIfNeededAsync(Request(ChannelB), Config());

        Assert.False(preempted);
        _sessionManager.Verify(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JustOpenedStreamNotYetReported_SameChannelIsShared()
    {
        var service = CreateService();
        _openStreams.Add("ls-b");
        service.RecordOpenedStream(PriorityUser, "Alice", "alice-tablet", ChannelB, "Channel B", "ls-b");

        var preempted = await service.PreemptIfNeededAsync(Request(ChannelB), Config());

        Assert.False(preempted);
        Assert.Contains("ls-b", _openStreams);
    }

    [Fact]
    public async Task JustOpenedStreamNotYetReported_OtherChannelIsReleased()
    {
        var service = CreateService();
        _openStreams.Add("ls-a");
        service.RecordOpenedStream(OtherUser, "Bob", "living-room", ChannelA, "Channel A", "ls-a");

        var preempted = await service.PreemptIfNeededAsync(Request(ChannelB), Config());

        Assert.True(preempted);
        Assert.DoesNotContain("ls-a", _openStreams);
    }

    [Fact]
    public void PendingStream_DroppedOnceClientReportsIt()
    {
        var service = CreateService();
        service.RecordOpenedStream(OtherUser, "Bob", "living-room", ChannelA, "Channel A", "ls-a");
        AddLiveSession("bob", OtherUser, "living-room", ChannelA, "ls-a");

        var holder = Assert.Single(service.GetTunerHolders());
        Assert.Equal("bob", holder.SessionId);
    }
}
