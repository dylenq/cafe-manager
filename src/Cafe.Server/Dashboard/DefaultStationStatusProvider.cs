using Cafe.Server.Agents;
using Cafe.Server.Data;
using Cafe.Shared;

namespace Cafe.Server.Dashboard;

/// <summary>
/// Device-level status. A PC with an agent machine id is Idle while its agent is connected and Offline otherwise
/// (no registry means nothing can be connected). A console with a TV address stays Offline until Phase 4 owns TV
/// status; a station with neither is Idle.
/// </summary>
public class DefaultStationStatusProvider(AgentRegistry? registry = null) : IStationStatusProvider
{
    public StationStatus GetStatus(Station station)
    {
        if (station.Kind == StationKind.PC && !string.IsNullOrWhiteSpace(station.AgentMachineId))
        {
            return registry is not null && registry.IsConnected(station.AgentMachineId) ? StationStatus.Idle : StationStatus.Offline;
        }

        return !string.IsNullOrWhiteSpace(station.AgentMachineId) || !string.IsNullOrWhiteSpace(station.TvIpAddress)
            ? StationStatus.Offline
            : StationStatus.Idle;
    }
}
