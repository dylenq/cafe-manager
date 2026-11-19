using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Agents;

public sealed record ConnectedAgent(AgentConnectionInfo Connection, int? StationId, string? StationName);

/// <summary>Read models for the /settings agents screen: who is connected and which machines match no station.</summary>
public sealed class AgentAdminService(IDbContextFactory<CafeDbContext> dbFactory, AgentRegistry registry)
{
    public async Task<IReadOnlyList<ConnectedAgent>> GetConnectedAsync(CancellationToken cancellationToken = default)
    {
        var stations = await LoadStationsAsync(cancellationToken);
        return registry.Snapshot()
            .Select(c =>
            {
                return stations.TryGetValue(c.MachineId, out var station)
                    ? new ConnectedAgent(c, station.Id, station.Name)
                    : new ConnectedAgent(c, null, null);
            })
            .OrderBy(a => a.Connection.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AgentConnectionInfo>> GetUnregisteredAsync(CancellationToken cancellationToken = default)
    {
        var stations = await LoadStationsAsync(cancellationToken);
        return registry.Snapshot()
            .Where(c => !stations.ContainsKey(c.MachineId))
            .OrderBy(c => c.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<string, (int Id, string Name)>> LoadStationsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Stations.AsNoTracking()
            .Where(s => s.AgentMachineId != null)
            .Select(s => new { s.Id, s.Name, s.AgentMachineId })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.AgentMachineId!, r => (r.Id, r.Name), StringComparer.OrdinalIgnoreCase);
    }
}
