using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveTvPriority.Configuration;

namespace Jellyfin.Plugin.LiveTvPriority.Preemption;

/// <summary>
/// Decides who has to be preempted for a priority request. Pure logic, no Jellyfin services.
/// </summary>
/// <remarks>
/// A tuner carries one channel; every session or recording on that channel shares it.
/// So decisions are made per channel: a channel is freed only if everything on it may be preempted,
/// otherwise kicking some of its viewers would not release the tuner.
/// </remarks>
public static class PreemptionPlanner
{
    /// <summary>
    /// Builds the plan.
    /// </summary>
    /// <param name="request">The priority request.</param>
    /// <param name="sessions">Sessions currently playing Live TV channels.</param>
    /// <param name="recordings">In-progress recordings.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="tunerCount">Number of tuners (at least 1).</param>
    /// <returns>The plan.</returns>
    public static PreemptionPlan Plan(
        PreemptionRequest request,
        IReadOnlyCollection<LiveSession> sessions,
        IReadOnlyCollection<ActiveRecording> recordings,
        PluginConfiguration config,
        int tunerCount)
    {
        var plan = new PreemptionPlan();
        tunerCount = Math.Max(1, tunerCount);

        // Requested channel already tuned (by anyone, or a recording): the stream is shared, nothing to free.
        var sharing = sessions.FirstOrDefault(s => s.ChannelId == request.ChannelId);
        if (sharing is not null || recordings.Any(r => r.ChannelId == request.ChannelId))
        {
            plan.Notes.Add(sharing is not null
                ? Format("Requested channel '{0}' is already tuned (shared with {1} on {2}); nothing to preempt.", request.ChannelName, sharing.UserName, sharing.DeviceName)
                : Format("Requested channel '{0}' is already being recorded; the stream is shared, nothing to preempt.", request.ChannelName));
            return plan;
        }

        var busyChannels = sessions.Select(s => s.ChannelId)
            .Concat(recordings.Select(r => r.ChannelId))
            .Distinct()
            .ToList();

        if (busyChannels.Count < tunerCount)
        {
            plan.Notes.Add(Format("{0} of {1} tuner(s) in use; a tuner is free, nothing to preempt.", busyChannels.Count, tunerCount));
            return plan;
        }

        var channelsToFree = busyChannels.Count - tunerCount + 1;
        var priorityUsers = new HashSet<Guid>(config.PriorityUserIds ?? Array.Empty<Guid>());

        var candidates = new List<(Guid ChannelId, List<Victim> Victims, List<ActiveRecording> Recordings)>();
        foreach (var channelId in busyChannels)
        {
            var channelSessions = sessions.Where(s => s.ChannelId == channelId).ToList();
            var channelRecordings = recordings.Where(r => r.ChannelId == channelId).ToList();
            var channelName = channelSessions.FirstOrDefault()?.ChannelName ?? channelRecordings.FirstOrDefault()?.Name ?? channelId.ToString("N", CultureInfo.InvariantCulture);

            var blocker = FindBlocker(request, channelSessions, channelRecordings, config, priorityUsers);
            if (blocker is not null)
            {
                plan.Notes.Add(Format("Channel '{0}' cannot be preempted: {1}.", channelName, blocker));
                continue;
            }

            var victims = channelSessions
                .Select(s => new Victim(s, IsSameDevice(s, request) ? VictimKind.SameDeviceSwitch : VictimKind.Kick))
                .ToList();
            candidates.Add((channelId, victims, channelRecordings));
        }

        if (candidates.Count < channelsToFree)
        {
            plan.Notes.Add(Format(
                "Need to free {0} channel(s) but only {1} can be preempted; leaving everything as is.",
                channelsToFree,
                candidates.Count));
            return plan;
        }

        // Cheapest first: our own device switching channel, then channels without recordings, then fewest viewers.
        foreach (var candidate in candidates
            .OrderBy(c => c.Victims.All(v => v.Kind == VictimKind.SameDeviceSwitch) && c.Recordings.Count == 0 ? 0 : 1)
            .ThenBy(c => c.Recordings.Count)
            .ThenBy(c => c.Victims.Count(v => v.Kind == VictimKind.Kick))
            .Take(channelsToFree))
        {
            plan.Victims.AddRange(candidate.Victims);
            plan.RecordingsToCancel.AddRange(candidate.Recordings);
        }

        return plan;
    }

    private static string? FindBlocker(
        PreemptionRequest request,
        List<LiveSession> channelSessions,
        List<ActiveRecording> channelRecordings,
        PluginConfiguration config,
        HashSet<Guid> priorityUsers)
    {
        if (channelRecordings.Count > 0 && !config.PreemptRecordings)
        {
            return Format("recording '{0}' is in progress and recordings are protected", channelRecordings[0].Name);
        }

        foreach (var session in channelSessions)
        {
            if (IsSameDevice(session, request))
            {
                continue;
            }

            if (session.UserId == request.UserId)
            {
                if (!config.PreemptOwnDevices)
                {
                    return Format("{0}'s own device '{1}' is protected", session.UserName, session.DeviceName);
                }

                continue;
            }

            if (priorityUsers.Contains(session.UserId) && !config.PriorityUsersPreemptEachOther)
            {
                return Format("{0} is also a priority user", session.UserName);
            }
        }

        return null;
    }

    private static bool IsSameDevice(LiveSession session, PreemptionRequest request)
        => !string.IsNullOrEmpty(request.DeviceId)
            && string.Equals(session.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase);

    private static string Format(string format, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, format, args);
}
