using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared;

namespace Cafe.Server.Dashboard;

/// <summary>An active session takes precedence over the device-level Offline/Idle result.</summary>
public class SessionAwareStatusProvider(DefaultStationStatusProvider inner) : IStationStatusProvider
{
    public StationStatus GetStatus(Station station) => inner.GetStatus(station);

    public StationStatus GetStatus(Station station, SessionCardModel? session, DateTimeOffset nowUtc)
    {
        if (session is null)
        {
            return inner.GetStatus(station);
        }

        var remaining = session.PurchasedMinutes is { } minutes
            ? SessionClock.RemainingSeconds(minutes, SessionClock.ActiveSeconds(session.ClosedActiveSeconds, session.OpenSegmentStartUtc, nowUtc))
            : long.MaxValue;
        return SessionStatusMapper.Map(session.Status, session.Mode, remaining, session.WarningWindowSeconds / 60);
    }
}
