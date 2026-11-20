using Cafe.Server.Data;
using Cafe.Shared;

namespace Cafe.Server.Sessions;

public static class SessionStatusMapper
{
    public static StationStatus Map(SessionStatus status, SessionMode mode, long remainingSeconds, int maxWarningMinutes) =>
        status switch
        {
            SessionStatus.Active => mode.IsTimed() && remainingSeconds <= maxWarningMinutes * 60L
                ? StationStatus.Expiring
                : StationStatus.InSession,
            SessionStatus.Paused => StationStatus.Paused,
            SessionStatus.Grace => StationStatus.Expiring,
            _ => StationStatus.Idle,
        };
}
