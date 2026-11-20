using Cafe.Server.Reports;

namespace Cafe.Tests;

public class ReportRangeTests
{
    private static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd");

    [Theory]
    [InlineData(ReportPreset.Today, "2025-02-05", "2025-02-05", "2025-02-05")]
    [InlineData(ReportPreset.Yesterday, "2025-02-05", "2025-02-04", "2025-02-04")]
    [InlineData(ReportPreset.ThisWeek, "2025-02-05", "2025-02-03", "2025-02-09")]
    [InlineData(ReportPreset.ThisWeek, "2025-02-09", "2025-02-03", "2025-02-09")] // Sunday belongs to the week that began Monday
    [InlineData(ReportPreset.ThisWeek, "2025-02-03", "2025-02-03", "2025-02-09")]
    [InlineData(ReportPreset.Last7Days, "2025-02-05", "2025-01-30", "2025-02-05")]
    [InlineData(ReportPreset.ThisMonth, "2025-02-05", "2025-02-01", "2025-02-28")]
    [InlineData(ReportPreset.ThisMonth, "2025-03-03", "2025-03-01", "2025-03-31")]
    [InlineData(ReportPreset.LastMonth, "2025-02-05", "2025-01-01", "2025-01-31")]
    [InlineData(ReportPreset.LastMonth, "2025-03-03", "2025-02-01", "2025-02-28")]
    [InlineData(ReportPreset.LastMonth, "2024-03-10", "2024-02-01", "2024-02-29")]
    [InlineData(ReportPreset.LastMonth, "2025-01-15", "2024-12-01", "2024-12-31")]
    public void Presets_resolve_against_today(ReportPreset preset, string today, string from, string to)
    {
        var range = ReportRange.Resolve(preset, D(today));

        Assert.Equal(D(from), range.From);
        Assert.Equal(D(to), range.To);
    }

    [Fact]
    public void Custom_range_is_used_as_given()
    {
        Assert.True(ReportRange.TryResolve(ReportPreset.Custom, D("2025-02-05"), D("2025-01-01"), D("2025-01-10"), out var range, out var error));
        Assert.Null(error);
        Assert.Equal(new ReportRange(D("2025-01-01"), D("2025-01-10")), range);
    }

    [Fact]
    public void Custom_range_rejects_reversed_missing_and_over_long_spans()
    {
        var today = D("2025-02-05");

        Assert.False(ReportRange.TryResolve(ReportPreset.Custom, today, D("2025-02-02"), D("2025-02-01"), out _, out var reversed));
        Assert.Equal("From date must not be after To date.", reversed);

        Assert.False(ReportRange.TryResolve(ReportPreset.Custom, today, null, D("2025-02-01"), out _, out var missingFrom));
        Assert.Equal("Choose a from and a to date.", missingFrom);
        Assert.False(ReportRange.TryResolve(ReportPreset.Custom, today, D("2025-02-01"), null, out _, out var missingTo));
        Assert.Equal("Choose a from and a to date.", missingTo);

        Assert.True(ReportRange.TryResolve(ReportPreset.Custom, today, D("2024-01-01"), D("2024-01-01").AddDays(399), out _, out _));
        Assert.False(ReportRange.TryResolve(ReportPreset.Custom, today, D("2024-01-01"), D("2024-01-01").AddDays(400), out _, out var tooLong));
        Assert.Equal("Choose a range of 400 days or less.", tooLong);

        Assert.Throws<ArgumentException>(() => ReportRange.Resolve(ReportPreset.Custom, today));
    }

    [Theory]
    [InlineData("2025-02-02", "2025-01-27")] // Sunday -> previous Monday
    [InlineData("2025-02-03", "2025-02-03")] // Monday -> itself
    [InlineData("2025-02-08", "2025-02-03")]
    [InlineData("2025-03-01", "2025-02-24")] // week straddling a month boundary
    public void WeekStart_is_the_monday(string date, string monday)
    {
        Assert.Equal(D(monday), ReportRange.WeekStart(D(date)));
    }

    [Theory]
    [InlineData("2025-02-02", "2025-W05")]
    [InlineData("2025-02-03", "2025-W06")]
    [InlineData("2024-12-30", "2025-W01")] // ISO week-year, not the calendar year
    [InlineData("2021-01-01", "2020-W53")]
    public void IsoWeekLabel_uses_the_week_year(string date, string label)
    {
        Assert.Equal(label, ReportRange.IsoWeekLabel(D(date)));
    }

    [Fact]
    public void Bounds_in_ho_chi_minh_are_exactly_24_hours()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

        var (start, end) = new ReportRange(D("2025-02-05"), D("2025-02-05")).ToUtcBounds(zone);

        Assert.Equal(DateTimeOffset.Parse("2025-02-04T17:00:00Z"), start);
        Assert.Equal(TimeSpan.FromHours(24), end - start);
    }

    [Fact]
    public void Bounds_on_spring_forward_day_are_23_hours()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var (start, end) = new ReportRange(D("2025-03-09"), D("2025-03-09")).ToUtcBounds(zone);

        Assert.Equal(DateTimeOffset.Parse("2025-03-09T05:00:00Z"), start);
        Assert.Equal(DateTimeOffset.Parse("2025-03-10T04:00:00Z"), end);
        Assert.Equal(TimeSpan.FromHours(23), end - start);
    }

    [Fact]
    public void Bounds_on_fall_back_day_are_25_hours()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var (start, end) = new ReportRange(D("2025-11-02"), D("2025-11-02")).ToUtcBounds(zone);

        Assert.Equal(DateTimeOffset.Parse("2025-11-02T04:00:00Z"), start);
        Assert.Equal(DateTimeOffset.Parse("2025-11-03T05:00:00Z"), end);
        Assert.Equal(TimeSpan.FromHours(25), end - start);
    }

    [Fact]
    public void Bounds_use_the_first_valid_instant_when_local_midnight_does_not_exist()
    {
        // Sao Paulo historically sprang forward at local midnight (2018-11-04 00:00 -> 01:00).
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            return;
        }

        var (start, _) = new ReportRange(D("2018-11-04"), D("2018-11-04")).ToUtcBounds(zone);

        Assert.Equal(DateTimeOffset.Parse("2018-11-04T03:00:00Z"), start); // 01:00 local (-02:00)
    }
}
