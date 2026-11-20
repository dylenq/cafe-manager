using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Cafe.Server.Devices;

/// <summary>
/// Latest known power state per console TV, filled by the monitor and by every power action. Entries older than three
/// poll periods plus five seconds read as unknown (null). Also relays desk notices, de-duplicated per station.
/// </summary>
public sealed class TvStateCache(TimeProvider time, IOptions<TvOptions> options)
{
    private static readonly TimeSpan NoticeWindow = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<int, (TvPowerState State, DateTimeOffset AtUtc)> _states = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _notices = new();

    /// <summary>Raised with the station id when its state changed (or was first learned).</summary>
    public event Action<int>? Changed;

    /// <summary>Raised with a staff-readable text when a TV action failed.</summary>
    public event Action<string>? NoticeRaised;

    public void Set(int stationId, TvPowerState state)
    {
        var now = time.GetUtcNow();
        var changed = false;
        _states.AddOrUpdate(
            stationId,
            _ => { changed = true; return (state, now); },
            (_, old) => { changed = old.State != state; return (state, now); });
        if (changed)
        {
            RaiseSafe(() => Changed?.Invoke(stationId));
        }
    }

    /// <summary>The cached state, or null when none is known or it is stale.</summary>
    public TvPowerState? Get(int stationId)
    {
        if (!_states.TryGetValue(stationId, out var entry))
        {
            return null;
        }

        var maxAge = TimeSpan.FromSeconds(3L * Math.Max(1, options.Value.PollSeconds) + 5);
        return time.GetUtcNow() - entry.AtUtc > maxAge ? null : entry.State;
    }

    public void Remove(int stationId)
    {
        if (_states.TryRemove(stationId, out _))
        {
            RaiseSafe(() => Changed?.Invoke(stationId));
        }
    }

    public IReadOnlyCollection<int> KnownStationIds => _states.Keys.ToArray();

    public void RaiseNotice(int stationId, string text)
    {
        var now = time.GetUtcNow();
        if (_notices.TryGetValue(stationId, out var last) && now - last < NoticeWindow)
        {
            return;
        }

        _notices[stationId] = now;
        RaiseSafe(() => NoticeRaised?.Invoke(text));
    }

    private static void RaiseSafe(Action raise)
    {
        try
        {
            raise();
        }
        catch
        {
            // A broken subscriber must never break a TV action or the monitor.
        }
    }
}
