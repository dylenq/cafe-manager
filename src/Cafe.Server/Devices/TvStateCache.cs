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
    private readonly ConcurrentDictionary<int, long> _actionStamps = new();
    private long _sequence;

    /// <summary>Raised with the station id when its state changed (or was first learned).</summary>
    public event Action<int>? Changed;

    /// <summary>Raised with a staff-readable text when a TV action failed.</summary>
    public event Action<string>? NoticeRaised;

    /// <summary>
    /// A marker to take before starting a poll. A poll result is only applied (<see cref="SetFromPoll"/>) when no
    /// authoritative write (<see cref="Set"/>) happened after the marker, so a slow poll that began before a manual
    /// power action cannot overwrite the action's fresher result.
    /// </summary>
    public long BeginPoll() => Interlocked.Read(ref _sequence);

    /// <summary>Stores a poll result unless an action result was written after <paramref name="marker"/>. Returns whether it was applied.</summary>
    public bool SetFromPoll(int stationId, TvPowerState state, long marker)
    {
        if (_actionStamps.TryGetValue(stationId, out var stamp) && stamp > marker)
        {
            return false;
        }

        Store(stationId, state);
        return true;
    }

    /// <summary>Stores an authoritative result (a power action or an explicit test). Poll results that started earlier are discarded.</summary>
    public void Set(int stationId, TvPowerState state)
    {
        _actionStamps[stationId] = Interlocked.Increment(ref _sequence);
        Store(stationId, state);
    }

    private void Store(int stationId, TvPowerState state)
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
        _actionStamps.TryRemove(stationId, out _);
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
