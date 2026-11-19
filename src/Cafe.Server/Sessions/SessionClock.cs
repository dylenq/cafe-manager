namespace Cafe.Server.Sessions;

/// <summary>Pure helpers for session time arithmetic and display. No clock access: callers pass the instant.</summary>
public static class SessionClock
{
    public static long ActiveSeconds(long closedSeconds, DateTimeOffset? openSegmentStartUtc, DateTimeOffset nowUtc)
    {
        var open = openSegmentStartUtc is { } start ? Math.Max(0, (long)(nowUtc - start).TotalSeconds) : 0;
        return closedSeconds + open;
    }

    /// <summary>May be negative once time is used up; callers clamp.</summary>
    public static long RemainingSeconds(int purchasedMinutes, long activeSeconds) =>
        purchasedMinutes * 60L - activeSeconds;

    /// <summary>mm:ss under one hour, h:mm:ss at or over one hour; negative values show 00:00.</summary>
    public static string FormatCountdown(long seconds)
    {
        if (seconds <= 0)
        {
            return "00:00";
        }

        var t = TimeSpan.FromSeconds(seconds);
        return seconds >= 3600
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
