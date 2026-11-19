using Cafe.Server.Data;
using Cafe.Shared;

namespace Cafe.Server.Dashboard;

/// <summary>Phase 1 rule: a station with an agent or TV configured is Offline (nothing has connected yet), otherwise Idle.</summary>
public class DefaultStationStatusProvider : IStationStatusProvider
{
    public StationStatus GetStatus(Station station) =>
        !string.IsNullOrWhiteSpace(station.AgentMachineId) || !string.IsNullOrWhiteSpace(station.TvIpAddress)
            ? StationStatus.Offline
            : StationStatus.Idle;
}
