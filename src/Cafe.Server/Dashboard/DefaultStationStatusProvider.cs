using Cafe.Server.Agents;
using Cafe.Server.Data;
using Cafe.Shared;

namespace Cafe.Server.Dashboard;

/// <summary>
/// Device-level status. A PC with an agent machine id is Idle while its agent is connected and Offline otherwise
/// (no registry means nothing can be connected). A console with a TV address is Offline unless the TV state cache holds
/// a fresh state other than Unreachable (without a cache it stays Offline); a station with neither is Idle.
/// </summary>
public class DefaultStationStatusProvider(AgentRegistry? registry = null, Cafe.Server.Devices.TvStateCache? tv = null) : IStationStatusProvider
{
    public StationStatus GetStatus(Station station)
    {
        if (station.Kind == StationKind.PC && !string.IsNullOrWhiteSpace(station.AgentMachineId))
        {
            return registry is not null && registry.IsConnected(station.AgentMachineId) ? StationStatus.Idle : StationStatus.Offline;
        }

        if (station.Kind == StationKind.Console && !string.IsNullOrWhiteSpace(station.TvIpAddress) && tv is not null)
        {
            return tv.Get(station.Id) is { } state && state != Cafe.Server.Devices.TvPowerState.Unreachable
                ? StationStatus.Idle
                : StationStatus.Offline;
        }

        return !string.IsNullOrWhiteSpace(station.AgentMachineId) || !string.IsNullOrWhiteSpace(station.TvIpAddress)
            ? StationStatus.Offline
            : StationStatus.Idle;
    }
}
