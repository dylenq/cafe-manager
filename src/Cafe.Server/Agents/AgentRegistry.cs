namespace Cafe.Server.Agents;

public sealed record AgentConnectionInfo(
    string MachineId,
    string ConnectionId,
    string Version,
    DateTimeOffset ConnectedAtUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>Live map of machine id to agent connection. Keys are case-insensitive; the newest connection wins.</summary>
public sealed class AgentRegistry(TimeProvider time, ILogger<AgentRegistry>? logger = null)
{
    private sealed record Entry(AgentConnectionInfo Info, Action Abort);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a connect or disconnect with the machine id.</summary>
    public event Action<string>? Changed;

    public void Register(string machineId, string connectionId, string version, Action abort)
    {
        Entry? replaced;
        lock (_gate)
        {
            _entries.TryGetValue(machineId, out replaced);
            var now = time.GetUtcNow();
            _entries[machineId] = new Entry(new AgentConnectionInfo(machineId, connectionId, version, now, now), abort);
        }

        if (replaced is not null)
        {
            // The replaced connection's late disconnect is ignored by Unregister (connection id no longer matches).
            try
            {
                replaced.Abort();
            }
            catch
            {
                // Already gone.
            }
        }

        RaiseChanged(machineId);
    }

    /// <summary>
    /// Notifies every subscriber on its own, outside the registry lock. A throwing subscriber (for example a disposed
    /// Blazor component) is logged and never stops the others or breaks the hub connect/disconnect that raised this.
    /// </summary>
    private void RaiseChanged(string machineId)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(machineId);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "An agent registry subscriber failed for {MachineId}; ignoring.", machineId);
            }
        }
    }

    /// <summary>Removes the entry only when it still belongs to this connection.</summary>
    public void Unregister(string machineId, string connectionId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(machineId, out var entry) || entry.Info.ConnectionId != connectionId)
            {
                return;
            }

            _entries.Remove(machineId);
        }

        RaiseChanged(machineId);
    }

    /// <summary>Aborts every connection; entries are removed by their own disconnect callbacks.</summary>
    public void DisconnectAll()
    {
        Entry[] all;
        lock (_gate)
        {
            all = _entries.Values.ToArray();
        }

        foreach (var entry in all)
        {
            try
            {
                entry.Abort();
            }
            catch
            {
                // Already gone.
            }
        }
    }

    public bool IsConnected(string machineId)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(machineId);
        }
    }

    public bool TryGetConnectionId(string machineId, out string connectionId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(machineId, out var entry))
            {
                connectionId = entry.Info.ConnectionId;
                return true;
            }
        }

        connectionId = string.Empty;
        return false;
    }

    public IReadOnlyList<AgentConnectionInfo> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values.Select(e => e.Info).ToList();
        }
    }
}
