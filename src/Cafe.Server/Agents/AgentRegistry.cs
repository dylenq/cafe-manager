namespace Cafe.Server.Agents;

public sealed record AgentConnectionInfo(
    string MachineId,
    string ConnectionId,
    string Version,
    DateTimeOffset ConnectedAtUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>A connection that was refused because its machine id is already connected (cloned install or rogue client).</summary>
public sealed record AgentConflictInfo(
    string MachineId,
    string RejectedConnectionId,
    string? RemoteAddress,
    int Attempts,
    DateTimeOffset FirstAtUtc,
    DateTimeOffset LastAtUtc);

/// <summary>
/// Live map of machine id to agent connection. Keys are case-insensitive; the FIRST connection wins and a second
/// connection with the same machine id is refused and recorded as a conflict (shown in /settings). A clone or a
/// rogue client holding the shared key therefore cannot displace or impersonate the legitimate agent. A genuinely
/// dead first connection (half-open TCP) is dropped by the SignalR keep-alive timeout, after which the real agent's
/// backed-off retry gets in.
/// </summary>
public sealed class AgentRegistry(TimeProvider time, ILogger<AgentRegistry>? logger = null)
{
    private sealed record Entry(AgentConnectionInfo Info, Action Abort);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentConflictInfo> _conflicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a connect, disconnect or refused duplicate with the machine id.</summary>
    public event Action<string>? Changed;

    /// <summary>Registers the connection; false when the machine id is already connected (the caller must abort the newcomer).</summary>
    public bool Register(string machineId, string connectionId, string version, Action abort, string? remoteAddress = null)
    {
        bool accepted;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_entries.ContainsKey(machineId))
            {
                _conflicts.TryGetValue(machineId, out var previous);
                _conflicts[machineId] = new AgentConflictInfo(
                    machineId, connectionId, remoteAddress, (previous?.Attempts ?? 0) + 1, previous?.FirstAtUtc ?? now, now);
                accepted = false;
            }
            else
            {
                _entries[machineId] = new Entry(new AgentConnectionInfo(machineId, connectionId, version, now, now), abort);
                _conflicts.Remove(machineId);
                accepted = true;
            }
        }

        RaiseChanged(machineId);
        return accepted;
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
            _conflicts.Remove(machineId);
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

    /// <summary>Machine ids that currently have a refused duplicate connection, newest first.</summary>
    public IReadOnlyList<AgentConflictInfo> Conflicts()
    {
        lock (_gate)
        {
            return _conflicts.Values.OrderByDescending(c => c.LastAtUtc).ToList();
        }
    }

    public IReadOnlyList<AgentConnectionInfo> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values.Select(e => e.Info).ToList();
        }
    }
}
