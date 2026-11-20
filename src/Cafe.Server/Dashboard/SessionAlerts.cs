using Cafe.Server.Data;
using Cafe.Server.Sessions;

namespace Cafe.Server.Dashboard;

public enum AlertLevel
{
    None = 0,
    Warning = 1,
    TimeUp = 2,
}

/// <summary>Pure alert-level rules for a session card. No clock access: callers pass the instant.</summary>
public static class SessionAlerts
{
    public static AlertLevel GetLevel(SessionCardModel session, DateTimeOffset nowUtc)
    {
        if (session.Status == SessionStatus.Grace)
        {
            return AlertLevel.TimeUp;
        }

        if (session.Status != SessionStatus.Active || !session.Mode.IsTimed() || session.PurchasedMinutes is not { } minutes)
        {
            return AlertLevel.None;
        }

        var active = SessionClock.ActiveSeconds(session.ClosedActiveSeconds, session.OpenSegmentStartUtc, nowUtc);
        var remaining = SessionClock.RemainingSeconds(minutes, active);
        if (remaining <= 0)
        {
            return AlertLevel.TimeUp;
        }

        return remaining <= session.WarningWindowSeconds ? AlertLevel.Warning : AlertLevel.None;
    }

    /// <summary>Seconds left of the grace period, clamped at zero; zero when the session is not in grace.</summary>
    public static long GraceRemainingSeconds(SessionCardModel session, DateTimeOffset nowUtc)
    {
        if (session.Status != SessionStatus.Grace || session.GraceStartedAtUtc is not { } started)
        {
            return 0;
        }

        var elapsed = Math.Max(0, (long)(nowUtc - started).TotalSeconds);
        return Math.Max(0, session.GraceMinutes * 60L - elapsed);
    }
}
