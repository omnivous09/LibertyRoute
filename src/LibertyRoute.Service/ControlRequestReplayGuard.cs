using System.Security.Cryptography;
using LibertyRoute.ControlProtocol;

namespace LibertyRoute.Service;

internal enum ControlFreshnessDecision
{
    Current,
    Stale,
    TooFarInFuture
}

internal enum ControlReplayReservationResult
{
    Reserved,
    Duplicate,
    Conflict,
    CapacityExceeded
}

internal sealed class ControlRequestReplayGuard
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan Retention = MaximumAge + MaximumFutureSkew;
    internal const int DefaultCapacity = 4096;

    private readonly object _sync = new();
    private readonly Dictionary<ReplayKey, ReplayEntry> _entries = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;

    internal ControlRequestReplayGuard(TimeProvider timeProvider, int capacity = DefaultCapacity)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal ControlFreshnessDecision EvaluateFreshness(DateTimeOffset sentAtUtc)
    {
        var now = _timeProvider.GetUtcNow();
        if (sentAtUtc < now - MaximumAge)
            return ControlFreshnessDecision.Stale;
        if (sentAtUtc > now + MaximumFutureSkew)
            return ControlFreshnessDecision.TooFarInFuture;
        return ControlFreshnessDecision.Current;
    }

    internal async Task<ControlReplayReservationResult> ReserveAsync(
        ControlCallerIdentity caller,
        ControlRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var digest = await CreateDigestAsync(request, cancellationToken);
        var key = new ReplayKey(caller.UserSid, request.RequestId);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            foreach (var expired in _entries.Where(pair => pair.Value.ExpiresAtUtc <= now).ToArray())
            {
                if (_entries.TryGetValue(expired.Key, out var current) && ReferenceEquals(current, expired.Value))
                    _entries.Remove(expired.Key);
            }

            if (_entries.TryGetValue(key, out var existing))
                return CryptographicOperations.FixedTimeEquals(existing.Digest, digest)
                    ? ControlReplayReservationResult.Duplicate
                    : ControlReplayReservationResult.Conflict;

            if (_entries.Count >= _capacity)
                return ControlReplayReservationResult.CapacityExceeded;

            _entries.Add(key, new ReplayEntry(digest, now + Retention));
            return ControlReplayReservationResult.Reserved;
        }
    }

    private static async Task<byte[]> CreateDigestAsync(
        ControlRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await LengthPrefixedJsonProtocol.WriteRequestAsync(stream, request, cancellationToken);
        return SHA256.HashData(stream.GetBuffer().AsSpan(
            ControlProtocolConstants.LengthPrefixSize,
            checked((int)stream.Length - ControlProtocolConstants.LengthPrefixSize)));
    }

    private readonly record struct ReplayKey(string CallerSid, Guid RequestId);
    private sealed record ReplayEntry(byte[] Digest, DateTimeOffset ExpiresAtUtc);
}
