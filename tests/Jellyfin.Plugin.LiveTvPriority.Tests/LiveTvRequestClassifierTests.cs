using System;
using Jellyfin.Plugin.LiveTvPriority.Interception;
using Xunit;

namespace Jellyfin.Plugin.LiveTvPriority.Tests;

public class LiveTvRequestClassifierTests
{
    [Theory]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", LiveTvRequestKind.PlaybackInfo)]
    [InlineData("MediaInfo", "GetPlaybackInfo", LiveTvRequestKind.PlaybackInfo)]
    [InlineData("MediaInfo", "OpenLiveStream", LiveTvRequestKind.OpenLiveStream)]
    [InlineData("MediaInfo", "CloseLiveStream", LiveTvRequestKind.None)]
    [InlineData("Videos", "GetVideoStream", LiveTvRequestKind.StreamOpening)]
    [InlineData("DynamicHls", "GetLiveHlsStream", LiveTvRequestKind.StreamOpening)]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", LiveTvRequestKind.StreamPlaylist)]
    [InlineData("DynamicHls", "GetHlsVideoSegment", LiveTvRequestKind.Segment)]
    [InlineData("Items", "GetItems", LiveTvRequestKind.None)]
    [InlineData(null, null, LiveTvRequestKind.None)]
    public void Classify(string? controller, string? action, LiveTvRequestKind expected)
    {
        Assert.Equal(expected, LiveTvRequestClassifier.Classify(controller, action));
    }

    [Fact]
    public void OpenToken_LiveTvChannel_Parsed()
    {
        var id = Guid.NewGuid();
        var token = "0123456789abcdef0123456789abcdef_LiveTvChannel_" + id.ToString("N") + "_hdhr_5_1_native";

        Assert.True(LiveTvRequestClassifier.TryParseChannelIdFromOpenToken(token, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc_Movie_0123456789abcdef0123456789abcdef_x")]
    [InlineData("abc_LiveTvChannel_not-a-guid")]
    [InlineData("abc_LiveTvChannel")]
    public void OpenToken_Other_NotParsed(string? token)
    {
        Assert.False(LiveTvRequestClassifier.TryParseChannelIdFromOpenToken(token, out var parsed));
        Assert.Equal(Guid.Empty, parsed);
    }
}
