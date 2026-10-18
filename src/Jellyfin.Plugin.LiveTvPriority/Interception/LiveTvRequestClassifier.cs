using System;
using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.LiveTvPriority.Interception;

/// <summary>What an intercepted API action means for the tuner.</summary>
public enum LiveTvRequestKind
{
    /// <summary>Not relevant.</summary>
    None,

    /// <summary><c>GET/POST Items/{itemId}/PlaybackInfo</c>; opens the tuner when AutoOpenLiveStream is set.</summary>
    PlaybackInfo,

    /// <summary><c>POST LiveStreams/Open</c>; always opens the tuner.</summary>
    OpenLiveStream,

    /// <summary>A streaming endpoint that opens the tuner itself when no LiveStreamId is passed (progressive, live.m3u8).</summary>
    StreamOpening,

    /// <summary>A playlist endpoint that precedes the tuner open (master/variant playlists, universal audio redirect).</summary>
    StreamPlaylist,

    /// <summary>An HLS segment; the first one opens the tuner when no LiveStreamId is passed.</summary>
    Segment
}

/// <summary>
/// Maps Jellyfin 12.2 API actions (Jellyfin.Api controllers) to <see cref="LiveTvRequestKind"/>.
/// Matching is by controller/action name because Jellyfin.Api is not published as a package.
/// </summary>
public static class LiveTvRequestClassifier
{
    private static readonly Dictionary<(string Controller, string Action), LiveTvRequestKind> Actions = new()
    {
        // MediaInfoController
        [("MediaInfo", "GetPlaybackInfo")] = LiveTvRequestKind.PlaybackInfo,
        [("MediaInfo", "GetPostedPlaybackInfo")] = LiveTvRequestKind.PlaybackInfo,
        [("MediaInfo", "OpenLiveStream")] = LiveTvRequestKind.OpenLiveStream,

        // VideosController / AudioController (progressive streams start ffmpeg -> TranscodeManager opens the stream)
        [("Videos", "GetVideoStream")] = LiveTvRequestKind.StreamOpening,
        [("Videos", "GetVideoStreamByContainer")] = LiveTvRequestKind.StreamOpening,
        [("Audio", "GetAudioStream")] = LiveTvRequestKind.StreamOpening,
        [("Audio", "GetAudioStreamByContainer")] = LiveTvRequestKind.StreamOpening,

        // DynamicHlsController
        [("DynamicHls", "GetLiveHlsStream")] = LiveTvRequestKind.StreamOpening,
        [("DynamicHls", "GetMasterHlsVideoPlaylist")] = LiveTvRequestKind.StreamPlaylist,
        [("DynamicHls", "GetMasterHlsAudioPlaylist")] = LiveTvRequestKind.StreamPlaylist,
        [("DynamicHls", "GetVariantHlsVideoPlaylist")] = LiveTvRequestKind.StreamPlaylist,
        [("DynamicHls", "GetVariantHlsAudioPlaylist")] = LiveTvRequestKind.StreamPlaylist,
        [("DynamicHls", "GetHlsVideoSegment")] = LiveTvRequestKind.Segment,
        [("DynamicHls", "GetHlsAudioSegment")] = LiveTvRequestKind.Segment,

        // UniversalAudioController
        [("UniversalAudio", "GetUniversalAudioStream")] = LiveTvRequestKind.StreamPlaylist,
    };

    /// <summary>
    /// Classifies an action.
    /// </summary>
    /// <param name="controllerName">Controller name without the "Controller" suffix.</param>
    /// <param name="actionName">Action method name.</param>
    /// <returns>The kind.</returns>
    public static LiveTvRequestKind Classify(string? controllerName, string? actionName)
    {
        if (controllerName is null || actionName is null)
        {
            return LiveTvRequestKind.None;
        }

        return Actions.TryGetValue((controllerName, actionName), out var kind) ? kind : LiveTvRequestKind.None;
    }

    /// <summary>
    /// Extracts the Live TV channel id from a media source open token.
    /// </summary>
    /// <remarks>
    /// Format in 12.2: <c>{providerKey}_{ItemTypeName}_{itemIdN}_{mediaSourceId}</c> where the provider key is
    /// added by MediaSourceManager.SetKeyProperties and the rest by LiveTvMediaSourceProvider (delimiter '_').
    /// </remarks>
    /// <param name="openToken">The open token.</param>
    /// <param name="channelId">The channel id.</param>
    /// <returns><c>true</c> if the token refers to a Live TV channel.</returns>
    public static bool TryParseChannelIdFromOpenToken(string? openToken, out Guid channelId)
    {
        channelId = Guid.Empty;
        if (string.IsNullOrEmpty(openToken))
        {
            return false;
        }

        var parts = openToken.Split('_');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], nameof(LiveTvChannel), StringComparison.Ordinal)
                && Guid.TryParse(parts[i + 1], out channelId))
            {
                return true;
            }
        }

        channelId = Guid.Empty;
        return false;
    }
}
