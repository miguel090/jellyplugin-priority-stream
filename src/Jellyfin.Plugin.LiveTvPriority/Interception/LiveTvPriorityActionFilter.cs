using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvPriority.Configuration;
using Jellyfin.Plugin.LiveTvPriority.Preemption;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvPriority.Interception;

/// <summary>
/// Global MVC action filter that runs before Jellyfin opens a Live TV tuner.
/// </summary>
/// <remarks>
/// In Jellyfin 12.2 every tuner open goes through IMediaSourceManager.OpenLiveStream(Internal), reached from:
/// <list type="bullet">
/// <item>POST Items/{itemId}/PlaybackInfo with AutoOpenLiveStream (MediaInfoController.GetPostedPlaybackInfo),</item>
/// <item>POST LiveStreams/Open (MediaInfoController.OpenLiveStream),</item>
/// <item>streaming endpoints without a LiveStreamId (TranscodeManager.AcquireResources).</item>
/// </list>
/// An action filter runs after authentication (the user claim is available) and after model binding
/// (the request DTOs are available), and before the action body, so it can free the tuner in time.
/// </remarks>
public sealed class LiveTvPriorityActionFilter : IAsyncActionFilter
{
    // Claim types set by Jellyfin's CustomAuthenticationHandler (Jellyfin.Api.Constants.InternalClaimTypes).
    internal const string UserIdClaim = "Jellyfin-UserId";
    internal const string DeviceIdClaim = "Jellyfin-DeviceId";

    private static readonly TimeSpan ReservationTtl = TimeSpan.FromSeconds(20);

    private readonly PreemptionService _preemptionService;
    private readonly TunerArbiter _arbiter;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<LiveTvPriorityActionFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvPriorityActionFilter"/> class.
    /// </summary>
    public LiveTvPriorityActionFilter(
        PreemptionService preemptionService,
        TunerArbiter arbiter,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<LiveTvPriorityActionFilter> logger)
    {
        _preemptionService = preemptionService;
        _arbiter = arbiter;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null
            || !config.Enabled
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || HttpMethods.IsHead(context.HttpContext.Request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var kind = LiveTvRequestClassifier.Classify(action.ControllerName, action.ActionName);
        if (kind == LiveTvRequestKind.None)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var user = context.HttpContext.User;
        var deviceId = user.FindFirst(DeviceIdClaim)?.Value ?? string.Empty;

        if (kind == LiveTvRequestKind.Segment)
        {
            // The first segment of an HLS stream without LiveStreamId is what opens the tuner;
            // once it went through, the priority device no longer needs its reservation.
            var reserved = deviceId.Length > 0 && _arbiter.IsReservedBy(deviceId);
            await next().ConfigureAwait(false);
            if (reserved)
            {
                _arbiter.ReleaseFor(deviceId);
            }

            return;
        }

        if (!Guid.TryParse(user.FindFirst(UserIdClaim)?.Value, out var userId) || userId == Guid.Empty)
        {
            await next().ConfigureAwait(false);
            return;
        }

        Target? target;
        try
        {
            target = ResolveTarget(context, kind, config);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect {Controller}.{Action}", action.ControllerName, action.ActionName);
            target = null;
        }

        if (target is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var ct = context.HttpContext.RequestAborted;
        var isPriority = config.PriorityUserIds?.Contains(userId) ?? false;
        var timeout = TimeSpan.FromSeconds(Math.Max(0, config.TunerReleaseTimeoutSeconds) + 30);

        if (!isPriority)
        {
            // Unchanged behavior, except that the request queues behind an in-flight preemption
            // instead of racing the priority user for the tuner that was just freed.
            if (await _arbiter.WaitForTurnAsync(target.Channel.Id, timeout, ct).ConfigureAwait(false))
            {
                Log(config, "Non-priority Live TV request ({Action}) for {Channel} waited for an in-flight preemption.", action.ActionName, target.Channel.Name);
            }

            // Hold the lock while the server opens the stream, so a priority request arriving meanwhile
            // waits and then sees this stream instead of failing to tune next to an invisible one.
            using (await _arbiter.EnterAsync(timeout, ct).ConfigureAwait(false))
            {
                var executedNonPriority = await next().ConfigureAwait(false);
                RecordOpenedStream(executedNonPriority, userId, deviceId, target);
            }

            return;
        }

        var userName = GetUserName(userId);
        var request = new PreemptionRequest(userId, userName, deviceId, target.Channel.Id, target.Channel.Name ?? string.Empty);

        Log(config, "Priority Live TV request: {User} on {Device} -> {Channel} via {Action} (opens tuner: {Opens}).", userName, deviceId, request.ChannelName, action.ActionName, target.OpensTuner);

        using var handle = await _arbiter.EnterAsync(timeout, ct).ConfigureAwait(false);
        if (handle is null)
        {
            _logger.LogWarning("Timed out waiting for another Live TV preemption; continuing {User}'s request without preempting.", userName);
            await next().ConfigureAwait(false);
            return;
        }

        bool preempted;
        try
        {
            preempted = await _preemptionService.PreemptIfNeededAsync(request, config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live TV preemption failed for {User} -> {Channel}", userName, request.ChannelName);
            preempted = false;
        }

        if (preempted && !target.OpensTuner)
        {
            // This request does not open the tuner itself; the client's next request will.
            _arbiter.Reserve(request.ChannelId, deviceId, ReservationTtl);
        }

        // The tuner is opened by the action itself; keep the lock until it finished.
        try
        {
            var executed = await next().ConfigureAwait(false);
            RecordOpenedStream(executed, userId, deviceId, target);
        }
        finally
        {
            if (target.OpensTuner)
            {
                _arbiter.Release(request.ChannelId, deviceId);
            }
        }
    }

    private Target? ResolveTarget(ActionExecutingContext context, LiveTvRequestKind kind, PluginConfiguration config)
    {
        Guid itemId;
        bool opensTuner;

        switch (kind)
        {
            case LiveTvRequestKind.PlaybackInfo:
            {
                var dto = GetArgument<object>(context, "playbackInfoDto");
                var liveStreamId = GetArgument<string>(context, "liveStreamId") ?? GetProperty<string>(dto, "LiveStreamId");
                if (!string.IsNullOrEmpty(liveStreamId))
                {
                    return null; // already opened
                }

                opensTuner = GetArgument<bool?>(context, "autoOpenLiveStream") ?? GetProperty<bool?>(dto, "AutoOpenLiveStream") ?? false;
                if (!opensTuner && !config.PreemptOnPlaybackInfo)
                {
                    return null;
                }

                itemId = GetArgument<Guid?>(context, "itemId") ?? Guid.Empty;
                break;
            }

            case LiveTvRequestKind.OpenLiveStream:
            {
                var dto = GetArgument<object>(context, "openLiveStreamDto");
                var openToken = GetArgument<string>(context, "openToken") ?? GetProperty<string>(dto, "OpenToken");
                if (!LiveTvRequestClassifier.TryParseChannelIdFromOpenToken(openToken, out itemId))
                {
                    itemId = GetArgument<Guid?>(context, "itemId") ?? GetProperty<Guid?>(dto, "ItemId") ?? Guid.Empty;
                }

                opensTuner = true;
                break;
            }

            case LiveTvRequestKind.StreamOpening:
            case LiveTvRequestKind.StreamPlaylist:
            {
                if (!string.IsNullOrEmpty(context.HttpContext.Request.Query["liveStreamId"].ToString()))
                {
                    return null; // already opened
                }

                itemId = GetArgument<Guid?>(context, "itemId")
                    ?? (Guid.TryParse(context.RouteData.Values["itemId"]?.ToString(), out var routeId) ? routeId : Guid.Empty);
                opensTuner = kind == LiveTvRequestKind.StreamOpening;
                break;
            }

            default:
                return null;
        }

        if (itemId == Guid.Empty)
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);

        // Guide programs are played through their channel.
        if (item is LiveTvProgram program && program.ChannelId != Guid.Empty)
        {
            item = _libraryManager.GetItemById(program.ChannelId);
        }

        return item is LiveTvChannel channel ? new Target(channel, opensTuner) : null;
    }

    private void RecordOpenedStream(ActionExecutedContext executed, Guid userId, string deviceId, Target target)
    {
        // PlaybackInfo (AutoOpenLiveStream) and LiveStreams/Open return the opened stream. The client reports
        // playback a moment later; until then the stream would be invisible in the session list.
        var liveStreamId = (executed.Result as ObjectResult)?.Value switch
        {
            PlaybackInfoResponse info => info.MediaSources?.FirstOrDefault(m => !string.IsNullOrEmpty(m.LiveStreamId))?.LiveStreamId,
            LiveStreamResponse open => open.MediaSource?.LiveStreamId,
            _ => null
        };

        if (executed.Exception is null && !string.IsNullOrEmpty(liveStreamId))
        {
            _preemptionService.RecordOpenedStream(userId, GetUserName(userId), deviceId, target.Channel.Id, target.Channel.Name ?? string.Empty, liveStreamId);
        }
    }

    private string GetUserName(Guid userId)
        => _userManager.GetUserById(userId)?.Username ?? userId.ToString("N", CultureInfo.InvariantCulture);

    private static T? GetArgument<T>(ActionExecutingContext context, string name)
        => context.ActionArguments.TryGetValue(name, out var value) && value is T typed ? typed : default;

    private static T? GetProperty<T>(object? obj, string name)
    {
        if (obj is null)
        {
            return default;
        }

        var value = obj.GetType().GetProperty(name)?.GetValue(obj);
        return value is T typed ? typed : default;
    }

    private void Log(PluginConfiguration config, string message, params object?[] args)
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

    private sealed record Target(BaseItem Channel, bool OpensTuner);
}
