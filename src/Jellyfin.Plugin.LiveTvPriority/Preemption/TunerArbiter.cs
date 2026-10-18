using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveTvPriority.Preemption;

/// <summary>
/// Serializes Live TV tune requests while a preemption is in flight and holds a short reservation
/// for the priority device, so a freed tuner is not grabbed by someone else first.
/// </summary>
/// <remarks>
/// Every intercepted Live TV request runs the server's own handling (which opens the tuner) inside the
/// lock, priority requests also their preemption. Tune requests are thereby serialized: a request never
/// races an open that is still in progress, and outcomes for non-priority users stay the same.
/// </remarks>
public sealed class TunerArbiter : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private Reservation? _reservation;

    /// <summary>
    /// Initializes a new instance of the <see cref="TunerArbiter"/> class.
    /// </summary>
    public TunerArbiter()
        : this(TimeProvider.System)
    {
    }

    internal TunerArbiter(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Enters the lock.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handle that leaves the lock when disposed, or <c>null</c> on timeout.</returns>
    public async Task<IDisposable?> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await _lock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new Releaser(_lock);
    }

    /// <summary>
    /// Waits until no preemption is in flight and no reservation blocks <paramref name="channelId"/>.
    /// </summary>
    /// <param name="channelId">Channel the caller wants to tune.</param>
    /// <param name="timeout">Maximum total wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the caller had to wait.</returns>
    public async Task<bool> WaitForTurnAsync(Guid channelId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = _timeProvider.GetTimestamp();
        var waited = false;

        if (_lock.CurrentCount == 0)
        {
            waited = true;
        }

        using (await EnterAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
        }

        var reservation = GetActiveReservation();
        if (reservation is not null && reservation.ChannelId != channelId)
        {
            var remaining = timeout - _timeProvider.GetElapsedTime(start);
            var untilExpiry = reservation.ExpiresAt - _timeProvider.GetUtcNow();
            var wait = remaining < untilExpiry ? remaining : untilExpiry;
            if (wait > TimeSpan.Zero)
            {
                waited = true;
                await Task.WhenAny(reservation.Released.Task, Task.Delay(wait, _timeProvider, cancellationToken)).ConfigureAwait(false);
            }
        }

        return waited;
    }

    /// <summary>
    /// Reserves the tuner for a device and channel until <see cref="Release"/> or expiry.
    /// </summary>
    /// <param name="channelId">Reserved channel.</param>
    /// <param name="deviceId">Device that owns the reservation.</param>
    /// <param name="ttl">Time to live.</param>
    public void Reserve(Guid channelId, string deviceId, TimeSpan ttl)
    {
        lock (_sync)
        {
            _reservation?.Released.TrySetResult();
            _reservation = new Reservation(channelId, deviceId, _timeProvider.GetUtcNow() + ttl);
        }
    }

    /// <summary>
    /// Releases the reservation if it belongs to this device and channel.
    /// </summary>
    /// <param name="channelId">Channel.</param>
    /// <param name="deviceId">Device.</param>
    public void Release(Guid channelId, string deviceId)
    {
        lock (_sync)
        {
            if (_reservation is not null
                && _reservation.ChannelId == channelId
                && string.Equals(_reservation.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                _reservation.Released.TrySetResult();
                _reservation = null;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether an active reservation belongs to <paramref name="deviceId"/>.
    /// </summary>
    /// <param name="deviceId">Device.</param>
    /// <returns><c>true</c> if the device holds the reservation.</returns>
    public bool IsReservedBy(string deviceId)
    {
        var reservation = GetActiveReservation();
        return reservation is not null && string.Equals(reservation.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Releases the reservation if it belongs to <paramref name="deviceId"/>, whatever its channel.
    /// </summary>
    /// <param name="deviceId">Device.</param>
    public void ReleaseFor(string deviceId)
    {
        lock (_sync)
        {
            if (_reservation is not null && string.Equals(_reservation.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                _reservation.Released.TrySetResult();
                _reservation = null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }

    private Reservation? GetActiveReservation()
    {
        lock (_sync)
        {
            if (_reservation is not null && _reservation.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _reservation.Released.TrySetResult();
                _reservation = null;
            }

            return _reservation;
        }
    }

    private sealed class Reservation(Guid channelId, string deviceId, DateTimeOffset expiresAt)
    {
        public Guid ChannelId { get; } = channelId;

        public string DeviceId { get; } = deviceId;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
