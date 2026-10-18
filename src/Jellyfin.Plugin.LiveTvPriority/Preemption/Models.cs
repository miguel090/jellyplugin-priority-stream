using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvPriority.Preemption;

/// <summary>A Live TV tune request from a priority user.</summary>
/// <param name="UserId">Requesting user.</param>
/// <param name="UserName">Requesting user's name.</param>
/// <param name="DeviceId">Requesting device id (from the auth token).</param>
/// <param name="ChannelId">Requested Live TV channel item id.</param>
/// <param name="ChannelName">Requested channel name, for logs.</param>
public sealed record PreemptionRequest(Guid UserId, string UserName, string DeviceId, Guid ChannelId, string ChannelName);

/// <summary>A session that is currently playing a Live TV channel.</summary>
/// <param name="SessionId">Jellyfin session id.</param>
/// <param name="UserId">Session user.</param>
/// <param name="UserName">Session user name.</param>
/// <param name="DeviceId">Device id.</param>
/// <param name="DeviceName">Device name, for logs.</param>
/// <param name="Client">Client name, for logs.</param>
/// <param name="ChannelId">Channel being watched.</param>
/// <param name="ChannelName">Channel name, for logs.</param>
/// <param name="LiveStreamId">Live stream id reported by the client, if any.</param>
public sealed record LiveSession(
    string SessionId,
    Guid UserId,
    string UserName,
    string DeviceId,
    string DeviceName,
    string Client,
    Guid ChannelId,
    string ChannelName,
    string? LiveStreamId);

/// <summary>An in-progress DVR recording.</summary>
/// <param name="TimerId">Timer id (used to cancel it).</param>
/// <param name="ChannelId">Channel being recorded.</param>
/// <param name="Name">Program name, for logs.</param>
/// <param name="ChannelName">Channel name, if known.</param>
public sealed record ActiveRecording(string TimerId, Guid ChannelId, string Name, string? ChannelName = null);

/// <summary>A tuned channel that can be joined instead of a busy one.</summary>
/// <param name="ChannelId">Channel id.</param>
/// <param name="ChannelName">Channel name.</param>
/// <param name="Watchers">Who is on it: user names, or "a recording".</param>
public sealed record TunedChannel(Guid ChannelId, string ChannelName, IReadOnlyList<string> Watchers);

/// <summary>How a session is preempted.</summary>
public enum VictimKind
{
    /// <summary>Another device: show the message, send Stop, close its stream.</summary>
    Kick,

    /// <summary>
    /// The requesting device itself, still holding the previous channel while switching.
    /// Its stream is closed silently: a Stop command could race with the playback being started.
    /// </summary>
    SameDeviceSwitch
}

/// <summary>A session chosen for preemption.</summary>
/// <param name="Session">The session.</param>
/// <param name="Kind">How to preempt it.</param>
public sealed record Victim(LiveSession Session, VictimKind Kind);

/// <summary>Result of planning a preemption.</summary>
public sealed class PreemptionPlan
{
    /// <summary>Gets the sessions to preempt.</summary>
    public List<Victim> Victims { get; } = new();

    /// <summary>Gets the recordings to cancel.</summary>
    public List<ActiveRecording> RecordingsToCancel { get; } = new();

    /// <summary>Gets human-readable notes explaining the decision (logged).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Gets a value indicating whether anything has to be done.</summary>
    public bool HasWork => Victims.Count > 0 || RecordingsToCancel.Count > 0;
}
