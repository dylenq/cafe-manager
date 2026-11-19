namespace Cafe.Server.Dashboard;

public sealed record AlertInput(int StationId, string StationName, AlertLevel Level);

public sealed record ActiveAlert(int StationId, string StationName, AlertLevel Level, bool Acknowledged);

public sealed record AlertUpdate(IReadOnlyList<ActiveAlert> NewAlerts, IReadOnlyList<ActiveAlert> ActiveAlerts);

/// <summary>
/// Pure tracking of which stations are alerting, which just started (to beep once) and which the desk
/// acknowledged. No clock and no JS; one instance per dashboard circuit.
/// </summary>
public sealed class AlertTracker
{
    private readonly Dictionary<int, (AlertLevel Level, bool Acknowledged)> _state = [];

    public AlertUpdate Update(IEnumerable<AlertInput> inputs)
    {
        var current = inputs.Where(i => i.Level != AlertLevel.None).ToDictionary(i => i.StationId);

        foreach (var gone in _state.Keys.Where(id => !current.ContainsKey(id)).ToList())
        {
            _state.Remove(gone);
        }

        var added = new List<ActiveAlert>();
        foreach (var input in current.Values)
        {
            if (!_state.TryGetValue(input.StationId, out var existing) || existing.Level != input.Level)
            {
                _state[input.StationId] = (input.Level, false);
                added.Add(new ActiveAlert(input.StationId, input.StationName, input.Level, false));
            }
        }

        var active = current.Values
            .Select(i => new ActiveAlert(i.StationId, i.StationName, i.Level, _state[i.StationId].Acknowledged))
            .OrderByDescending(a => a.Level)
            .ThenBy(a => a.StationName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AlertUpdate(added, active);
    }

    /// <summary>Silences repeat beeps for the station until its alert level changes or clears.</summary>
    public void Acknowledge(int stationId)
    {
        if (_state.TryGetValue(stationId, out var existing))
        {
            _state[stationId] = (existing.Level, true);
        }
    }

    public bool IsAcknowledged(int stationId) => _state.TryGetValue(stationId, out var s) && s.Acknowledged;
}
