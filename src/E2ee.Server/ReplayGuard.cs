using System.Collections.Concurrent;
using E2ee.Wire;

namespace E2ee.Server;

/// <summary>
/// Remembers request signatures for as long as their timestamp is acceptable, so a
/// captured request cannot be resent. In memory only: a restart forgets them, which
/// reopens a replay window of at most <see cref="RelayAuth.MaxSkew"/>.
/// </summary>
public sealed class ReplayGuard
{
    private readonly ConcurrentDictionary<string, long> _seen = new();
    private long _lastPrune;

    public bool TryUse(byte[] signature, long timestampMs)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var window = (long)RelayAuth.MaxSkew.TotalMilliseconds;
        if (now - Interlocked.Read(ref _lastPrune) > window)
        {
            Interlocked.Exchange(ref _lastPrune, now);
            foreach (var (key, ts) in _seen)
                if (now - ts > window) _seen.TryRemove(key, out _);
        }
        return _seen.TryAdd(Convert.ToBase64String(signature), timestampMs);
    }
}
