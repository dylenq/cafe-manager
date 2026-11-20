namespace Cafe.Server.Reports;

/// <summary>An inclusive range of cafe-local dates.</summary>
public record ReportRange(DateOnly From, DateOnly To)
{
    /// <summary>
    /// UTC bounds of the range: local midnight of From (inclusive) to local midnight of To + 1 (exclusive). Only
    /// local-midnight arithmetic is used, never "add 24 hours to a UTC instant", so DST days are 23 or 25 hours long.
    /// </summary>
    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ToUtcBounds(TimeZoneInfo zone) =>
        (LocalMidnightToUtc(From, zone), LocalMidnightToUtc(To.AddDays(1), zone));

    private static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Midnight falls in a DST gap: the day starts at the first valid instant after it.
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
