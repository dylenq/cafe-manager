using Cafe.Server.Data;
using Cafe.Shared;

namespace Cafe.Server.Dashboard;

/// <summary>Seam for station status. Phases 2-4 replace the default with session/agent/TV aware logic.</summary>
public interface IStationStatusProvider
{
    StationStatus GetStatus(Station station);
}
