using Cafe.Server.Data;
using Cafe.Shared;

namespace Cafe.Server.Dashboard;

/// <summary>Seam for station status. Phases 2-4 replace the default with session/agent/TV aware logic.</summary>
public interface IStationStatusProvider
{
    StationStatus GetStatus(Station station);

    /// <summary>Session-aware overload; providers that only know device state keep the default.</summary>
    StationStatus GetStatus(Station station, SessionCardModel? session, DateTimeOffset nowUtc) => GetStatus(station);
}
