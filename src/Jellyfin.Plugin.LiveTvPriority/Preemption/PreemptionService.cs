using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiveTvPriority.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvPriority.Preemption;

/// <summary>
/// Gathers the Live TV state from Jellyfin and carries out a <see cref="PreemptionPlan"/>.
/// </summary>
public sealed class PreemptionService
{
    private const int MaxCloseAttempts = 32;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PendingStreamTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, PendingStream> _pendingStreams = new(StringComparer.OrdinalIgnoreCase);

    private readonly ISessionManager _sessionManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<PreemptionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreemptionService"/> class.
    /// </summary>
    public PreemptionService(
        ISessionManager sessionManager,
        IMediaSourceManager mediaSourceManager,
        ILiveTvManager liveTvManager,
        ITranscodeManager transcodeManager,
        IConfigurationManager configurationManager,
        ILogger<PreemptionService> logger)
    {
        _sessionManager = sessionManager;
        _mediaSourceManager = mediaSourceManager;
        _liveTvManager = liveTvManager;
        _transcodeManager = transcodeManager;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Frees the tuner for <paramref name="request"/> if needed and waits until it is released.
    /// </summary>
    /// <param name="request">The priority request.</param>
    /// <param name="config">Current configuration.</param>
    /// <returns><c>true</c> if something was preempted.</returns>
    public async Task<bool> PreemptIfNeededAsync(PreemptionRequest request, PluginConfiguration config)
    {
        var exempt = ExemptSet(config);
        var sessions = GetTunerHolders(exempt);
        var recordings = (await GetActiveRecordingsAsync().ConfigureAwait(false)).Where(r => !exempt.Contains(r.ChannelId)).ToList();
        var tunerCount = GetTunerCount(config);

        var plan = PreemptionPlanner.Plan(request, sessions, recordings, config, tunerCount);

        foreach (var note in plan.Notes)
        {
            LogDecision(config, "[{User} on {Device} -> {Channel}] {Note}", request.UserName, request.DeviceId, request.ChannelName, note);
        }

        if (!plan.HasWork)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();

        foreach (var recording in plan.RecordingsToCancel)
        {
            _logger.LogWarning(
                "Live TV preemption: {PriorityUser} requested {RequestedChannel}; cancelling recording {Recording} (timer {TimerId}).",
                request.UserName,
                request.ChannelName,
                recording.Name,
                recording.TimerId);
            try
            {
                await _liveTvManager.CancelTimer(recording.TimerId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel recording timer {TimerId}", recording.TimerId);
            }
        }

        foreach (var victim in plan.Victims)
        {
            var s = victim.Session;
            if (victim.Kind == VictimKind.Kick)
            {
                _logger.LogWarning(
                    "Live TV preemption: {PriorityUser} ({PriorityDevice}) requested {RequestedChannel}; stopping {VictimUser} on {VictimDevice} ({VictimClient}) watching {VictimChannel}.",
                    request.UserName,
                    request.DeviceId,
                    request.ChannelName,
                    s.UserName,
                    s.DeviceName,
                    s.Client,
                    s.ChannelName);
                await NotifyAndStopAsync(s, request, config).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "Live TV preemption: {PriorityUser} is switching {OldChannel} -> {RequestedChannel} on {Device}; releasing the old stream.",
                    request.UserName,
                    s.ChannelName,
                    request.ChannelName,
                    s.DeviceName);
            }
        }

        // Server-side release, so a client that ignores the Stop command still loses the tuner.
        foreach (var deviceId in plan.Victims.Select(v => v.Session.DeviceId).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await _transcodeManager.KillTranscodingJobs(deviceId, null, _ => true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to kill transcoding jobs for device {DeviceId}", deviceId);
            }
        }

        var liveStreamIds = plan.Victims
            .Select(v => v.Session.LiveStreamId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var liveStreamId in liveStreamIds)
        {
            await ForceCloseLiveStreamAsync(liveStreamId).ConfigureAwait(false);
        }

        foreach (var victim in plan.Victims.Where(v => string.IsNullOrEmpty(v.Session.LiveStreamId)))
        {
            _logger.LogWarning(
                "Session {SessionId} ({Device}) did not report a live stream id; relying on Stop and transcode kill to release the tuner.",
                victim.Session.SessionId,
                victim.Session.DeviceName);
        }

        var released = await WaitForReleaseAsync(plan, liveStreamIds, TimeSpan.FromSeconds(Math.Max(0, config.TunerReleaseTimeoutSeconds))).ConfigureAwait(false);
        if (released)
        {
            if (config.UpstreamSettleDelayMs > 0)
            {
                await Task.Delay(config.UpstreamSettleDelayMs).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Live TV preemption: tuner released for {PriorityUser} -> {RequestedChannel} after {ElapsedMs} ms.",
                request.UserName,
                request.ChannelName,
                stopwatch.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning(
                "Live TV preemption: tuner not confirmed free after {Timeout} s; continuing {PriorityUser}'s request anyway.",
                config.TunerReleaseTimeoutSeconds,
                request.UserName);
        }

        return true;
    }

    /// <summary>
    /// Checks whether a channel can be tuned without preempting anyone.
    /// </summary>
    /// <param name="channelId">Requested channel.</param>
    /// <param name="deviceId">Requesting device.</param>
    /// <param name="config">Current configuration.</param>
    /// <returns><c>null</c> if it can be tuned or shared; otherwise the tuned channels that can be joined instead.</returns>
    public async Task<IReadOnlyList<TunedChannel>?> CheckBusyAsync(Guid channelId, string deviceId, PluginConfiguration config)
    {
        var exempt = ExemptSet(config);
        var holders = GetTunerHolders(exempt);
        var recordings = (await GetActiveRecordingsAsync().ConfigureAwait(false)).Where(r => !exempt.Contains(r.ChannelId)).ToList();
        return PreemptionPlanner.CheckBusy(channelId, deviceId, holders, recordings, GetTunerCount(config));
    }

    /// <summary>
    /// Tells the requesting device that the tuner is busy and which channels it can watch instead.
    /// </summary>
    /// <param name="userId">Requesting user.</param>
    /// <param name="deviceId">Requesting device.</param>
    /// <param name="tuned">Channels currently tuned.</param>
    /// <param name="config">Current configuration.</param>
    /// <returns>The message text.</returns>
    public async Task<string> NotifyBusyAsync(Guid userId, string deviceId, IReadOnlyList<TunedChannel> tuned, PluginConfiguration config)
    {
        var text = FormatBusyMessage(config.BusyMessageText, tuned);
        var session = _sessionManager.Sessions.FirstOrDefault(s =>
            s.UserId == userId && string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        if (session is not null && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                await _sessionManager.SendMessageCommand(
                    null!,
                    session.Id,
                    new MessageCommand
                    {
                        Header = string.IsNullOrWhiteSpace(config.BusyMessageHeader) ? "Live TV busy" : config.BusyMessageHeader,
                        Text = text,
                        TimeoutMs = config.KickMessageTimeoutMs > 0 ? config.KickMessageTimeoutMs : null
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not send busy message to session {SessionId}", session.Id);
            }
        }

        return text;
    }

    /// <summary>
    /// Fills the busy message placeholders.
    /// </summary>
    /// <param name="template">Message template.</param>
    /// <param name="tuned">Channels currently tuned.</param>
    /// <returns>The message.</returns>
    public static string FormatBusyMessage(string? template, IReadOnlyList<TunedChannel> tuned)
    {
        var users = string.Join(", ", tuned.SelectMany(t => t.Watchers).Distinct(StringComparer.OrdinalIgnoreCase));
        var channels = string.Join(" or ", tuned.Select(t => t.ChannelName).Distinct(StringComparer.OrdinalIgnoreCase));
        return (template ?? string.Empty)
            .Replace(PluginConfiguration.UserPlaceholder, users.Length > 0 ? users : "Someone", StringComparison.OrdinalIgnoreCase)
            .Replace(PluginConfiguration.ChannelPlaceholder, channels, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fills the kick message placeholders.
    /// </summary>
    /// <param name="template">Message template.</param>
    /// <param name="request">The priority request that caused the kick.</param>
    /// <returns>The message.</returns>
    public static string FormatKickMessage(string? template, PreemptionRequest request)
        => (template ?? string.Empty)
            .Replace(PluginConfiguration.UserPlaceholder, request.UserName, StringComparison.OrdinalIgnoreCase)
            .Replace(PluginConfiguration.ChannelPlaceholder, request.ChannelName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the configured exempt channels.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <returns>The set.</returns>
    public static IReadOnlySet<Guid> ExemptSet(PluginConfiguration config)
        => new HashSet<Guid>(config.ExemptChannelIds ?? Array.Empty<Guid>());

    /// <summary>
    /// Remembers a live stream that an intercepted request just opened, until the client reports playback.
    /// </summary>
    /// <param name="userId">User that opened it.</param>
    /// <param name="userName">User name.</param>
    /// <param name="deviceId">Device that opened it.</param>
    /// <param name="channelId">Channel.</param>
    /// <param name="channelName">Channel name.</param>
    /// <param name="liveStreamId">Opened live stream id.</param>
    public void RecordOpenedStream(Guid userId, string userName, string deviceId, Guid channelId, string channelName, string liveStreamId)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(liveStreamId))
        {
            return;
        }

        var session = _sessionManager.Sessions.FirstOrDefault(s =>
            s.UserId == userId && string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        _pendingStreams[deviceId] = new PendingStream(
            new LiveSession(
                session?.Id ?? string.Empty,
                userId,
                userName,
                deviceId,
                session?.DeviceName ?? deviceId,
                session?.Client ?? string.Empty,
                channelId,
                channelName,
                liveStreamId),
            DateTime.UtcNow + PendingStreamTtl);
    }

    /// <summary>
    /// Gets everything that currently holds a tuner: sessions playing a Live TV channel whose stream is
    /// still open, plus streams opened through the API that the client has not reported as playing yet.
    /// </summary>
    /// <returns>The holders.</returns>
    /// <param name="exemptChannels">Channels to leave out (they do not use the shared tuner).</param>
    internal List<LiveSession> GetTunerHolders(IReadOnlySet<Guid>? exemptChannels = null)
    {
        // A session whose reported live stream was closed (e.g. preempted, but its client never reported stop)
        // no longer holds a tuner.
        var holders = GetLiveSessions()
            .Where(s => exemptChannels is null || !exemptChannels.Contains(s.ChannelId))
            .Where(s => string.IsNullOrEmpty(s.LiveStreamId) || _mediaSourceManager.GetLiveStreamInfo(s.LiveStreamId) is not null)
            .ToList();

        var now = DateTime.UtcNow;
        foreach (var (deviceId, pending) in _pendingStreams)
        {
            var stream = pending.Session.LiveStreamId!;
            if (pending.ExpiresUtc <= now
                || _mediaSourceManager.GetLiveStreamInfo(stream) is null
                || holders.Any(h => string.Equals(h.LiveStreamId, stream, StringComparison.Ordinal)
                    || (string.Equals(h.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) && h.ChannelId == pending.Session.ChannelId)))
            {
                _pendingStreams.TryRemove(new KeyValuePair<string, PendingStream>(deviceId, pending));
                continue;
            }

            if (exemptChannels is null || !exemptChannels.Contains(pending.Session.ChannelId))
            {
                holders.Add(pending.Session);
            }
        }

        return holders;
    }

    /// <summary>
    /// Gets the sessions currently playing a Live TV channel.
    /// </summary>
    /// <returns>The sessions.</returns>
    internal List<LiveSession> GetLiveSessions()
    {
        var result = new List<LiveSession>();
        foreach (var session in _sessionManager.Sessions)
        {
            if (!TryGetPlayingChannel(session, out var channelId, out var channelName))
            {
                continue;
            }

            result.Add(new LiveSession(
                session.Id,
                session.UserId,
                session.UserName ?? string.Empty,
                session.DeviceId ?? string.Empty,
                session.DeviceName ?? string.Empty,
                session.Client ?? string.Empty,
                channelId,
                channelName,
                session.PlayState?.LiveStreamId));
        }

        return result;
    }

    private static bool TryGetPlayingChannel(SessionInfo session, out Guid channelId, out string channelName)
    {
        if (session.FullNowPlayingItem is LiveTvChannel channel)
        {
            channelId = channel.Id;
            channelName = channel.Name ?? string.Empty;
            return true;
        }

        var dto = session.NowPlayingItem;
        if (dto is not null && dto.Type == BaseItemKind.TvChannel)
        {
            channelId = dto.Id;
            channelName = dto.Name ?? string.Empty;
            return true;
        }

        channelId = Guid.Empty;
        channelName = string.Empty;
        return false;
    }

    private async Task<List<ActiveRecording>> GetActiveRecordingsAsync()
    {
        try
        {
            var timers = await _liveTvManager.GetTimers(new TimerQuery { IsActive = true }, CancellationToken.None).ConfigureAwait(false);
            return timers.Items
                .Where(t => t.Status == RecordingStatus.InProgress)
                .Select(t => new ActiveRecording(t.Id, t.ChannelId, t.Name ?? t.ChannelName ?? t.Id, t.ChannelName))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read active recordings");
            return new List<ActiveRecording>();
        }
    }

    private int GetTunerCount(PluginConfiguration config)
    {
        if (config.TunerCountOverride > 0)
        {
            return config.TunerCountOverride;
        }

        try
        {
            var options = _configurationManager.GetConfiguration<LiveTvOptions>("livetv");
            var hosts = options?.TunerHosts ?? Array.Empty<TunerHostInfo>();

            // A host with TunerCount 0 has an unknown/unlimited count; with the IPTV single-connection
            // setup this plugin targets, assume one tuner rather than "plenty".
            var total = hosts.Sum(h => h.TunerCount > 0 ? h.TunerCount : 1);
            return Math.Max(1, total);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Live TV tuner configuration; assuming one tuner");
            return 1;
        }
    }

    private async Task NotifyAndStopAsync(LiveSession victim, PreemptionRequest request, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(victim.SessionId))
        {
            return;
        }

        // controllingSessionId is null: the server itself is the controller, so no
        // "can this user control that session" check applies.
        if (config.SendKickMessage && !string.IsNullOrWhiteSpace(config.KickMessageText))
        {
            try
            {
                await _sessionManager.SendMessageCommand(
                    null!,
                    victim.SessionId,
                    new MessageCommand
                    {
                        Header = string.IsNullOrWhiteSpace(config.KickMessageHeader) ? "Live TV" : config.KickMessageHeader,
                        Text = FormatKickMessage(config.KickMessageText, request),
                        TimeoutMs = config.KickMessageTimeoutMs > 0 ? config.KickMessageTimeoutMs : null
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not send message to session {SessionId}", victim.SessionId);
            }
        }

        try
        {
            await _sessionManager.SendPlaystateCommand(
                null!,
                victim.SessionId,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send Stop to session {SessionId}; the stream will be closed server-side", victim.SessionId);
        }
    }

    private async Task ForceCloseLiveStreamAsync(string liveStreamId)
    {
        // CloseLiveStream only decrements the consumer count; the tuner is released when it reaches 0.
        // Close until the stream is gone so every consumer (player, transcoder) lets go.
        for (var attempt = 0; attempt < MaxCloseAttempts; attempt++)
        {
            if (_mediaSourceManager.GetLiveStreamInfo(liveStreamId) is null)
            {
                return;
            }

            try
            {
                await _mediaSourceManager.CloseLiveStream(liveStreamId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing live stream {LiveStreamId}", liveStreamId);
                return;
            }
        }

        _logger.LogWarning("Live stream {LiveStreamId} still open after {Attempts} close attempts", liveStreamId, MaxCloseAttempts);
    }

    private async Task<bool> WaitForReleaseAsync(PreemptionPlan plan, List<string> liveStreamIds, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        // The requesting device only reports its old playback stopped once the new one starts,
        // so it is never waited on here.
        var untrackedSessions = plan.Victims
            .Where(v => v.Kind == VictimKind.Kick && string.IsNullOrEmpty(v.Session.LiveStreamId))
            .Select(v => v.Session)
            .ToList();
        var timerIds = plan.RecordingsToCancel.Select(r => r.TimerId).ToHashSet(StringComparer.Ordinal);

        while (true)
        {
            var streamsOpen = liveStreamIds.Any(id => _mediaSourceManager.GetLiveStreamInfo(id) is not null);

            var sessionsPlaying = untrackedSessions.Count > 0 && GetLiveSessions()
                .Any(live => untrackedSessions.Any(u => u.SessionId == live.SessionId && u.ChannelId == live.ChannelId));

            var recordingsActive = timerIds.Count > 0 && (await GetActiveRecordingsAsync().ConfigureAwait(false))
                .Any(r => timerIds.Contains(r.TimerId));

            if (!streamsOpen && !sessionsPlaying && !recordingsActive)
            {
                return true;
            }

            if (deadline.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    private sealed record PendingStream(LiveSession Session, DateTime ExpiresUtc);

    private void LogDecision(PluginConfiguration config, string message, params object?[] args)
    {
#pragma warning disable CA2254 // Template is a constant passed through.
        if (config.VerboseLogging)
        {
            _logger.LogInformation(message, args);
        }
        else
        {
            _logger.LogDebug(message, args);
        }
#pragma warning restore CA2254
    }
}
