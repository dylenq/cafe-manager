using System.Globalization;

namespace Cafe.Server.Reports;

public enum ReportPreset
{
    Today,
    Yesterday,
    ThisWeek,
    Last7Days,
    ThisMonth,
    LastMonth,
    Custom,
}

/// <summary>An inclusive range of cafe-local dates.</summary>
public record ReportRange(DateOnly From, DateOnly To)
{
    /// <summary>Longest custom range allowed; caps the work a single page view or export can trigger.</summary>
    public const int MaxDays = 400;

    public const string FromAfterTo = "From date must not be after To date.";
    public const string TooLong = "Choose a range of 400 days or less.";
    public const string MissingDates = "Choose a from and a to date.";

    /// <summary>Resolves a preset against the cafe-local <paramref name="today"/>; throws when a custom range is invalid.</summary>
    public static ReportRange Resolve(ReportPreset preset, DateOnly today, DateOnly? customFrom = null, DateOnly? customTo = null) =>
        TryResolve(preset, today, customFrom, customTo, out var range, out var error)
            ? range!
            : throw new ArgumentException(error);

    public static bool TryResolve(
        ReportPreset preset, DateOnly today, DateOnly? customFrom, DateOnly? customTo, out ReportRange? range, out string? error)
    {
        error = null;
        range = null;
        switch (preset)
        {
            case ReportPreset.Today:
                range = new ReportRange(today, today);
                break;
            case ReportPreset.Yesterday:
                range = new ReportRange(today.AddDays(-1), today.AddDays(-1));
                break;
            case ReportPreset.ThisWeek:
                var monday = WeekStart(today);
                range = new ReportRange(monday, monday.AddDays(6));
                break;
            case ReportPreset.Last7Days:
                range = new ReportRange(today.AddDays(-6), today);
                break;
            case ReportPreset.ThisMonth:
                var first = new DateOnly(today.Year, today.Month, 1);
                range = new ReportRange(first, first.AddMonths(1).AddDays(-1));
                break;
            case ReportPreset.LastMonth:
                var lastFirst = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                range = new ReportRange(lastFirst, lastFirst.AddMonths(1).AddDays(-1));
                break;
            default:
                if (customFrom is null || customTo is null)
                {
                    error = MissingDates;
                }
                else if (customFrom > customTo)
                {
                    error = FromAfterTo;
                }
                else if (customTo.Value.DayNumber - customFrom.Value.DayNumber + 1 > MaxDays)
                {
                    error = TooLong;
                }
                else
                {
                    range = new ReportRange(customFrom.Value, customTo.Value);
                }

                break;
        }

        return error is null;
    }

    /// <summary>The Monday of the ISO week containing <paramref name="date"/> (weeks never start on Sunday).</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>"2025-W05" style label using the ISO week-year (2024-12-30 is "2025-W01").</summary>
    public static string IsoWeekLabel(DateOnly date)
    {
        var dt = date.ToDateTime(TimeOnly.MinValue);
        return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(dt):D4}-W{ISOWeek.GetWeekOfYear(dt):D2}");
    }

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
