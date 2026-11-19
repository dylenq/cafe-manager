using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests;

/// <summary>Pure PricingEngine matrix. No database, no clock. The America/New_York zone must exist or the class fails loudly.</summary>
public class PricingEngineTests
{
    private static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const decimal Base = 6.00m;
    private const DaysOfWeekFlags Daily = DaysOfWeekFlags.All;

    // Calendar reference (2026): Wed 10 Jun, Thu 11, Fri 12, Sat 13, Sun 14 Jun.
    private static DateTimeOffset Local(int y, int mo, int d, int h, int mi = 0, int s = 0) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified), Ny), TimeSpan.Zero);

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0, int s = 0) =>
        new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private static PricedInterval Span(DateTimeOffset start, DateTimeOffset end, int type = 1, decimal rate = Base) =>
        new(start, end, type, rate);

    private static PricedInterval Mins(DateTimeOffset start, int minutes, int type = 1, decimal rate = Base) =>
        new(start, start.AddMinutes(minutes), type, rate);

    private static RateRule Rule(
        int id,
        string start = "20:00",
        string end = "22:00",
        RateRuleKind kind = RateRuleKind.FixedHourlyRate,
        decimal value = 4.00m,
        int priority = 0,
        int? type = null,
        DaysOfWeekFlags days = Daily,
        bool enabled = true) => new()
    {
        Id = id,
        Name = $"rule-{id}",
        StationTypeId = type,
        DaysOfWeek = days,
        StartTime = TimeOnly.Parse(start),
        EndTime = TimeOnly.Parse(end),
        Kind = kind,
        Value = value,
        Priority = priority,
        IsEnabled = enabled,
    };

    private static PricingResult Price(IEnumerable<PricedInterval> intervals, IEnumerable<RateRule>? rules = null, TimeZoneInfo? tz = null) =>
        PricingEngine.Price(new PricingRequest(intervals.ToList(), (rules ?? []).ToList(), tz ?? Ny));

    /// <summary>Windows are ordered, non-overlapping, internally consistent and add up to the billed seconds.</summary>
    private static PricingResult Checked(PricingResult result, long billedSeconds)
    {
        Assert.Equal(billedSeconds, result.Windows.Sum(w => w.Seconds));
        for (var i = 0; i < result.Windows.Count; i++)
        {
            var w = result.Windows[i];
            Assert.True(w.EndUtc > w.StartUtc);
            Assert.Equal((long)(w.EndUtc - w.StartUtc).TotalSeconds, w.Seconds);
            if (i > 0)
            {
                Assert.True(w.StartUtc >= result.Windows[i - 1].EndUtc, "windows must be ordered and not overlap");
            }
        }

        return result;
    }

    // ---- no rules -------------------------------------------------------------------------------------------

    [Fact]
    public void No_rules_sixty_minutes_costs_the_base_rate_in_one_window()
    {
        var r = Checked(Price([Mins(Local(2026, 6, 10, 12), 60)]), 3600);
        Assert.Equal(6.00m, r.Total);
        var w = Assert.Single(r.Windows);
        Assert.Null(w.RuleId);
        Assert.Equal(6.00m, w.HourlyRate);
    }

    [Fact]
    public void No_rules_ninety_minutes_costs_nine()
    {
        Assert.Equal(9.00m, Checked(Price([Mins(Local(2026, 6, 10, 12), 90)]), 5400).Total);
    }

    [Fact]
    public void Disabled_rules_are_ignored_even_at_top_priority()
    {
        var rules = new[] { Rule(1, "00:00", "23:59", value: 1.00m, priority: 99, enabled: false) };
        var r = Checked(Price([Mins(Local(2026, 6, 10, 12), 60)], rules), 3600);
        Assert.Equal(6.00m, r.Total);
        Assert.Null(Assert.Single(r.Windows).RuleId);
    }

    // ---- happy hour -----------------------------------------------------------------------------------------

    [Fact]
    public void Span_crossing_happy_hour_start_splits_in_two()
    {
        var r = Checked(Price([Mins(Local(2026, 6, 10, 19, 30), 60)], [Rule(1)]), 3600);
        Assert.Equal(5.00m, r.Total);
        Assert.Equal(2, r.Windows.Count);
        Assert.Null(r.Windows[0].RuleId);
        Assert.Equal(1, r.Windows[1].RuleId);
        Assert.Equal(1800, r.Windows[0].Seconds);
    }

    [Fact]
    public void Span_crossing_happy_hour_end_splits_the_other_way()
    {
        var r = Checked(Price([Mins(Local(2026, 6, 10, 21, 30), 60)], [Rule(1)]), 3600);
        Assert.Equal(5.00m, r.Total);
        Assert.Equal(1, r.Windows[0].RuleId);
        Assert.Null(r.Windows[1].RuleId);
    }

    [Fact]
    public void Span_fully_inside_happy_hour_costs_the_rule_rate()
    {
        var r = Checked(Price([Mins(Local(2026, 6, 10, 20, 15), 60)], [Rule(1)]), 3600);
        Assert.Equal(4.00m, r.Total);
        Assert.Single(r.Windows);
    }

    [Fact]
    public void Window_start_is_inclusive_and_end_is_exclusive()
    {
        Assert.Equal(4.00m, Price([Mins(Local(2026, 6, 10, 20), 60)], [Rule(1)]).Total);
        Assert.Equal(6.00m, Price([Mins(Local(2026, 6, 10, 22), 60)], [Rule(1)]).Total);
        Assert.Equal(6.00m, Price([Mins(Local(2026, 6, 10, 19), 60)], [Rule(1)]).Total);
    }

    [Fact]
    public void Rule_only_applies_on_its_flagged_days()
    {
        var rule = Rule(1, days: DaysOfWeekFlags.Friday);
        Assert.Equal(4.00m, Price([Mins(Local(2026, 6, 12, 20), 60)], [rule]).Total);
        Assert.Equal(6.00m, Price([Mins(Local(2026, 6, 13, 20), 60)], [rule]).Total);
    }

    // ---- midnight-crossing windows --------------------------------------------------------------------------

    [Fact]
    public void Friday_night_window_discounts_through_saturday_morning()
    {
        var rule = Rule(1, "22:00", "02:00", days: DaysOfWeekFlags.Friday);
        var r = Checked(Price([Mins(Local(2026, 6, 12, 23), 120)], [rule]), 7200);
        Assert.Equal(8.00m, r.Total);
        Assert.All(r.Windows, w => Assert.Equal(1, w.RuleId));
    }

    [Fact]
    public void Saturday_night_is_not_discounted_by_a_friday_only_window()
    {
        var rule = Rule(1, "22:00", "02:00", days: DaysOfWeekFlags.Friday);
        Assert.Equal(12.00m, Price([Mins(Local(2026, 6, 13, 23), 120)], [rule]).Total);
    }

    [Fact]
    public void Friday_early_morning_belongs_to_thursday_night_so_it_is_not_discounted()
    {
        var rule = Rule(1, "22:00", "02:00", days: DaysOfWeekFlags.Friday);
        Assert.Equal(3.00m, Price([Mins(Local(2026, 6, 12, 1), 30)], [rule]).Total);
    }

    [Fact]
    public void Friday_early_morning_is_discounted_when_thursday_is_also_flagged()
    {
        var rule = Rule(1, "22:00", "02:00", days: DaysOfWeekFlags.Thursday | DaysOfWeekFlags.Friday);
        Assert.Equal(2.00m, Price([Mins(Local(2026, 6, 12, 1), 30)], [rule]).Total);
    }

    // ---- day-of-week boundary at midnight -------------------------------------------------------------------

    [Fact]
    public void Day_boundary_splits_exactly_at_local_midnight()
    {
        var rule = Rule(1, "00:00", "23:59", RateRuleKind.Multiplier, 0.5m, days: DaysOfWeekFlags.Sunday);
        var r = Checked(Price([Mins(Local(2026, 6, 13, 23, 30), 60)], [rule]), 3600);
        Assert.Equal(4.50m, r.Total);
        Assert.Equal(2, r.Windows.Count);
        Assert.Equal(Local(2026, 6, 14, 0), r.Windows[0].EndUtc);
        Assert.Equal(6.00m, r.Windows[0].HourlyRate);
        Assert.Equal(3.00m, r.Windows[1].HourlyRate);
    }

    // ---- priority, ties, scoping ----------------------------------------------------------------------------

    [Fact]
    public void Higher_priority_wins_when_rules_overlap()
    {
        var rules = new[] { Rule(1, value: 4.00m, priority: 1), Rule(2, value: 3.00m, priority: 5) };
        var r = Price([Mins(Local(2026, 6, 10, 20, 30), 60)], rules);
        Assert.Equal(3.00m, r.Total);
        Assert.Equal(2, Assert.Single(r.Windows).RuleId);
    }

    [Fact]
    public void Equal_priority_resolves_to_the_lowest_id_regardless_of_list_order()
    {
        var rules = new[] { Rule(3, value: 1.00m, priority: 2), Rule(2, value: 2.00m, priority: 2) };
        var r = Price([Mins(Local(2026, 6, 10, 20, 30), 60)], rules);
        Assert.Equal(2.00m, r.Total);
        Assert.Equal(2, Assert.Single(r.Windows).RuleId);
    }

    [Fact]
    public void Type_specific_rule_does_not_apply_to_another_type()
    {
        var rule = Rule(1, type: 2, value: 3.00m);
        Assert.Equal(6.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60, type: 1)], [rule]).Total);
        Assert.Equal(3.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60, type: 2)], [rule]).Total);
    }

    [Fact]
    public void All_types_rule_applies_to_every_type()
    {
        var rule = Rule(1, type: null, value: 3.00m);
        Assert.Equal(3.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60, type: 1)], [rule]).Total);
        Assert.Equal(3.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60, type: 2)], [rule]).Total);
    }

    [Fact]
    public void Type_specific_rule_with_higher_priority_beats_the_all_types_rule()
    {
        var rules = new[] { Rule(1, type: null, value: 3.00m, priority: 1), Rule(2, type: 2, value: 2.00m, priority: 5) };
        Assert.Equal(2.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60, type: 2)], rules).Total);
        Assert.Equal(3.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60, type: 1)], rules).Total);
    }

    // ---- rule kinds -----------------------------------------------------------------------------------------

    [Fact]
    public void Multiplier_scales_the_base_rate()
    {
        var rule = Rule(1, kind: RateRuleKind.Multiplier, value: 1.5m);
        Assert.Equal(9.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60)], [rule]).Total);
    }

    [Fact]
    public void Fixed_rate_ignores_the_base_rate()
    {
        var rule = Rule(1, value: 10.00m);
        Assert.Equal(10.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60, rate: 1.00m)], [rule]).Total);
        Assert.Equal(10.00m, Price([Mins(Local(2026, 6, 10, 20, 30), 60, rate: 99.00m)], [rule]).Total);
    }

    // ---- zero, sub-minute and rounding ----------------------------------------------------------------------

    [Fact]
    public void Zero_length_and_negative_spans_cost_nothing()
    {
        var t = Local(2026, 6, 10, 12);
        var zero = Price([Span(t, t)]);
        Assert.Equal(0.00m, zero.Total);
        Assert.Empty(zero.Windows);
        var negative = Price([Span(t, t.AddMinutes(-30))]);
        Assert.Equal(0.00m, negative.Total);
        Assert.Empty(negative.Windows);
        Assert.Equal(0.00m, Price([]).Total);
    }

    [Theory]
    [InlineData(1, 0.10)]
    [InlineData(10, 0.10)]
    [InlineData(59, 0.10)]
    [InlineData(60, 0.10)]
    [InlineData(61, 0.20)]
    [InlineData(119, 0.20)]
    [InlineData(120, 0.20)]
    [InlineData(121, 0.30)]
    public void Spans_bill_up_to_the_next_whole_minute(int seconds, decimal expected)
    {
        var t = Local(2026, 6, 10, 12);
        var r = Checked(Price([Span(t, t.AddSeconds(seconds))]), (seconds + 59) / 60 * 60);
        Assert.Equal(expected, r.Total);
    }

    [Theory]
    [InlineData(7.50, 0.13)] // 0.125 rounds away from zero
    [InlineData(22.50, 0.38)] // 0.375
    [InlineData(4.50, 0.08)] // 0.075
    [InlineData(6.00, 0.10)]
    public void Total_rounds_to_two_decimals_away_from_zero(decimal rate, decimal expected)
    {
        var t = Local(2026, 6, 10, 12);
        Assert.Equal(expected, Price([Span(t, t.AddSeconds(60), rate: rate)]).Total);
    }

    // ---- multiple intervals ---------------------------------------------------------------------------------

    [Fact]
    public void Intervals_on_different_types_are_priced_at_their_own_base_rate()
    {
        var t = Local(2026, 6, 10, 12);
        var r = Checked(Price([Mins(t, 30, type: 1, rate: 6.00m), Mins(t.AddMinutes(30), 30, type: 2, rate: 12.00m)]), 3600);
        Assert.Equal(9.00m, r.Total);
        Assert.Equal(2, r.Windows.Count);
        Assert.Equal([1, 2], r.Windows.Select(w => w.StationTypeId));
    }

    [Fact]
    public void Round_up_applies_to_the_sum_and_the_padding_lands_on_the_last_interval()
    {
        var t = Local(2026, 6, 10, 12);
        // 20 s + 20 s = 40 s, billed as 60 s: the extra 20 s are charged at the LAST interval's rate.
        var r = Checked(Price([Span(t, t.AddSeconds(20), 1, 6.00m), Span(t.AddMinutes(5), t.AddMinutes(5).AddSeconds(20), 2, 12.00m)]), 60);
        Assert.Equal(0.17m, r.Total); // 20s*6/3600 + 40s*12/3600 = 0.1667
        Assert.Equal(20, r.Windows[0].Seconds);
        Assert.Equal(40, r.Windows[1].Seconds);
    }

    [Fact]
    public void Two_thirty_second_intervals_bill_exactly_one_minute()
    {
        var t = Local(2026, 6, 10, 12);
        var r = Checked(Price([Span(t, t.AddSeconds(30)), Span(t.AddHours(1), t.AddHours(1).AddSeconds(30))]), 60);
        Assert.Equal(0.10m, r.Total);
    }

    // ---- daylight saving ------------------------------------------------------------------------------------

    [Fact]
    public void Spring_forward_bills_elapsed_time_not_wall_clock_time()
    {
        var start = Utc(2026, 3, 8, 6); // 01:00 EST
        var r = Checked(Price([Span(start, start.AddHours(3))]), 10800);
        Assert.Equal(18.00m, r.Total);
    }

    [Fact]
    public void Spring_forward_gap_rule_never_applies()
    {
        var start = Utc(2026, 3, 8, 6);
        var r = Checked(Price([Span(start, start.AddHours(3))], [Rule(1, "02:00", "03:00")]), 10800);
        Assert.Equal(18.00m, r.Total);
        Assert.All(r.Windows, w => Assert.Null(w.RuleId));
    }

    [Fact]
    public void Spring_forward_rule_after_the_gap_starts_at_the_gap_adjusted_instant()
    {
        var start = Utc(2026, 3, 8, 6);
        var r = Checked(Price([Span(start, start.AddHours(3))], [Rule(1, "03:00", "04:00")]), 10800);
        Assert.Equal(16.00m, r.Total); // 06-07 UTC at 6, 07-08 UTC (03:00-04:00 EDT) at 4, 08-09 UTC at 6
        var discounted = Assert.Single(r.Windows, w => w.RuleId == 1);
        Assert.Equal(Utc(2026, 3, 8, 7), discounted.StartUtc);
        Assert.Equal(3600, discounted.Seconds);
    }

    [Fact]
    public void Fall_back_bills_three_elapsed_hours()
    {
        var start = Utc(2026, 11, 1, 4); // 00:00 EDT
        var r = Checked(Price([Span(start, start.AddHours(3))]), 10800);
        Assert.Equal(18.00m, r.Total);
    }

    [Fact]
    public void Fall_back_rule_applies_to_both_occurrences_of_the_repeated_hour()
    {
        var start = Utc(2026, 11, 1, 4);
        var r = Checked(Price([Span(start, start.AddHours(3))], [Rule(1, "01:00", "02:00")]), 10800);
        Assert.Equal(14.00m, r.Total); // 1h at 6.00 plus 2h at 4.00
        Assert.Equal(7200, Assert.Single(r.Windows, w => w.RuleId == 1).Seconds);
    }

    [Fact]
    public void Midnight_crossing_rule_on_the_fall_back_night_neither_double_counts_nor_drops_seconds()
    {
        var start = Utc(2026, 11, 1, 2); // 22:00 EDT on 31 Oct
        var r = Checked(Price([Span(start, start.AddHours(8))], [Rule(1, "22:00", "02:00")]), 28800);
        // Rule window 22:00 EDT .. 02:00 EST is 5 elapsed hours at 4.00, then 3 hours at 6.00.
        Assert.Equal(38.00m, r.Total);
        Assert.Equal(18000, r.Windows.Where(w => w.RuleId == 1).Sum(w => w.Seconds));
    }

    // ---- misc -----------------------------------------------------------------------------------------------

    [Fact]
    public void Utc_zone_works_without_dst()
    {
        var utc = TimeZoneInfo.Utc;
        var r = Checked(Price([Mins(Utc(2026, 6, 10, 19, 30), 60)], [Rule(1)], utc), 3600);
        Assert.Equal(5.00m, r.Total);
    }

    [Fact]
    public void Long_open_ended_span_across_several_days_is_consistent()
    {
        var start = Local(2026, 6, 10, 12);
        var r = Checked(Price([Mins(start, 3 * 24 * 60)], [Rule(1)]), 3 * 24 * 3600);
        // 3 days * (22h at 6.00 + 2h at 4.00)
        Assert.Equal(3 * (22 * 6.00m + 2 * 4.00m), r.Total);
    }

    [Fact]
    public void Engine_source_stays_pure()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Cafe.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!, "src", "Cafe.Server", "Pricing", "PricingEngine.cs"));
        foreach (var forbidden in new[] { "DateTime.Now", "UtcNow", "DbContext", "TimeProvider", "ILogger" })
        {
            Assert.DoesNotContain(forbidden, source);
        }
    }

    [Fact]
    public void Time_zone_resolution_falls_back_to_local_and_never_throws()
    {
        Assert.Equal(TimeZoneInfo.Local.Id, CafeTimeZone.Resolve(null, NullLogger.Instance).Id);
        Assert.Equal(TimeZoneInfo.Local.Id, CafeTimeZone.Resolve("   ", NullLogger.Instance).Id);
        Assert.Equal(TimeZoneInfo.Local.Id, CafeTimeZone.Resolve("Not/AZone", NullLogger.Instance).Id);
        Assert.Equal("America/New_York", CafeTimeZone.Resolve("America/New_York", NullLogger.Instance).Id);
    }
}
