using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Agents;

public sealed record ConnectedAgent(AgentConnectionInfo Connection, int? StationId, string? StationName);

/// <summary>
/// The /settings agents screen: who is connected, which machines match no station, and turning an unregistered
/// agent into a station (or attaching it to an existing PC station). Every change pushes state to the agent at once.
/// </summary>
public sealed class AgentAdminService(
    IDbContextFactory<CafeDbContext> dbFactory,
    AgentRegistry registry,
    IServiceScopeFactory scopes,
    AgentCommandService commands)
{
    private const string MissingMachine = "Choose an agent first.";

    /// <summary>Creates a PC station bound to the machine id (name, type and uniqueness rules come from StationService).</summary>
    public async Task<ServiceResult<Station>> CreateStationFromAgentAsync(
        string machineId, string name, int stationTypeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineId))
        {
            return ServiceResult<Station>.Fail(MissingMachine);
        }

        ServiceResult<Station> result;
        using (var scope = scopes.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = name ?? string.Empty,
                StationTypeId = stationTypeId,
                Kind = StationKind.PC,
                AgentMachineId = machineId.Trim(),
            });
        }

        if (result.Success)
        {
            await commands.PushStateAsync(machineId.Trim(), cancellationToken);
        }

        return result;
    }

    /// <summary>Sets AgentMachineId on an existing PC station. Rejects consoles and stations that vanished.</summary>
    public async Task<ServiceResult> AssignAgentToStationAsync(string machineId, int stationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineId))
        {
            return ServiceResult.Fail(MissingMachine);
        }

        ServiceResult result;
        string? previousMachineId;
        using (var scope = scopes.CreateScope())
        {
            var stationService = scope.ServiceProvider.GetRequiredService<StationService>();
            var station = await stationService.GetAsync(stationId);
            if (station is null)
            {
                return ServiceResult.NotFound("That station no longer exists. Refresh the list.");
            }

            if (station.Kind != StationKind.PC)
            {
                return ServiceResult.Fail($"{station.Name} is not a PC station, so it cannot have an agent.");
            }

            previousMachineId = station.AgentMachineId;
            result = await stationService.UpdateAsync(stationId, new StationInput
            {
                Name = station.Name,
                StationTypeId = station.StationTypeId,
                Kind = StationKind.PC,
                AgentMachineId = machineId.Trim(),
                SortOrder = station.SortOrder,
                IsEnabled = station.IsEnabled,
            });
        }

        if (result.Success)
        {
            await commands.PushStateAsync(machineId.Trim(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(previousMachineId)
                && !string.Equals(previousMachineId, machineId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // The agent that was replaced is now unregistered: tell it so it does not keep a stale unlock.
                await commands.PushStateAsync(previousMachineId, cancellationToken);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<Station>> ListPcStationsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Stations.AsNoTracking().Where(s => s.Kind == StationKind.PC)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
    }

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

    /// <summary>Refused duplicate connections (two PCs claiming the same machine id), for the /settings warning.</summary>
    public IReadOnlyList<AgentConflictInfo> GetConflicts() => registry.Conflicts();

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
