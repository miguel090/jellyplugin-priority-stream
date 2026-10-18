using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiveTvPriority.Configuration;

/// <summary>
/// Plugin configuration, persisted by Jellyfin as XML and edited from the dashboard page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Placeholder replaced with the priority user's name in the kick message.</summary>
    public const string UserPlaceholder = "{user}";

    /// <summary>Gets or sets a value indicating whether preemption is enabled at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the users whose Live TV requests may preempt other sessions.</summary>
    public Guid[] PriorityUserIds { get; set; } = Array.Empty<Guid>();

    /// <summary>Gets or sets a value indicating whether a message is shown on the preempted client.</summary>
    public bool SendKickMessage { get; set; } = true;

    /// <summary>Gets or sets the header of the message shown on the preempted client.</summary>
    public string KickMessageHeader { get; set; } = "Live TV";

    /// <summary>Gets or sets the message shown on the preempted client. Supports <c>{user}</c>.</summary>
    public string KickMessageText { get; set; } = "Live TV was taken over by {user}.";

    /// <summary>Gets or sets how long the message stays on screen, in milliseconds.</summary>
    public int KickMessageTimeoutMs { get; set; } = 8000;

    /// <summary>Gets or sets a value indicating whether the priority user's own other devices can be preempted.</summary>
    public bool PreemptOwnDevices { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether in-progress DVR recordings can be cancelled to free the tuner.</summary>
    public bool PreemptRecordings { get; set; }

    /// <summary>Gets or sets a value indicating whether one priority user may preempt another priority user.</summary>
    public bool PriorityUsersPreemptEachOther { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a PlaybackInfo request without AutoOpenLiveStream already triggers preemption.
    /// Preempting there gives the tuner more time to release before the client opens the stream.
    /// </summary>
    public bool PreemptOnPlaybackInfo { get; set; } = true;

    /// <summary>Gets or sets how long to wait for the tuner to be released, in seconds.</summary>
    public int TunerReleaseTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets an extra delay after the tuner is released, in milliseconds, so the upstream
    /// (e.g. Dispatcharr / IPTV provider) can drop its connection before the new one is opened.
    /// </summary>
    public int UpstreamSettleDelayMs { get; set; } = 500;

    /// <summary>
    /// Gets or sets the number of tuners to assume. 0 means "read TunerCount from the Live TV tuner
    /// configuration, and assume 1 when it is unknown".
    /// </summary>
    public int TunerCountOverride { get; set; }

    /// <summary>Gets or sets a value indicating whether decisions are logged at Information level.</summary>
    public bool VerboseLogging { get; set; }
}
