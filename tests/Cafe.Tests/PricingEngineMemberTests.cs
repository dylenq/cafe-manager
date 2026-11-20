using Cafe.Server.Data;
using Cafe.Server.Pricing;

namespace Cafe.Tests;

/// <summary>PRIC-03: member base rate and rule audience in the pure engine.</summary>
public class PricingEngineMemberTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset At18 = new(2026, 6, 10, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset At12 = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static StationType Type(decimal rate = 6000m, decimal? member = 4000m) =>
        new() { Id = 1, Name = "PC", HourlyRate = rate, MemberHourlyRate = member };

    private static RateRule Rule(
        int id,
        RateRuleAudience audience,
        RateRuleKind kind = RateRuleKind.FixedHourlyRate,
        decimal value = 2000m,
        int priority = 0) => new()
    {
        Id = id,
        Name = $"rule-{id}",
        DaysOfWeek = DaysOfWeekFlags.All,
        StartTime = new TimeOnly(18, 0),
        EndTime = new TimeOnly(20, 0),
        Kind = kind,
        Value = value,
        Priority = priority,
        AppliesTo = audience,
        IsEnabled = true,
    };

    private static decimal Price(DateTimeOffset start, int minutes, bool member, StationType type, params RateRule[] rules)
    {
        var interval = new PricedInterval(start, start.AddMinutes(minutes), type.Id, PricingEngine.BaseRate(type, member));
        return PricingEngine.Price(new PricingRequest([interval], rules, Utc, member)).Total;
    }

    [Fact]
    public void PRIC03_member_pays_member_rate_and_guest_pays_base_rate()
    {
        Assert.Equal(4000m, Price(At12, 60, true, Type()));
        Assert.Equal(6000m, Price(At12, 60, false, Type()));
    }

    [Fact]
    public void PRIC03_member_without_member_rate_falls_back_to_base_rate()
    {
        Assert.Equal(6000m, Price(At12, 60, true, Type(member: null)));
    }

    [Fact]
    public void PRIC03_members_only_rule_applies_to_member_and_is_ignored_for_guest()
    {
        var rule = Rule(1, RateRuleAudience.MembersOnly);
        Assert.Equal(2000m, Price(At18, 60, true, Type(), rule));
        Assert.Equal(6000m, Price(At18, 60, false, Type(), rule));
    }

    [Fact]
    public void PRIC03_guests_only_rule_is_ignored_for_member_and_applies_to_guest()
    {
        var rule = Rule(1, RateRuleAudience.GuestsOnly, value: 5000m);
        Assert.Equal(4000m, Price(At18, 60, true, Type(), rule));
        Assert.Equal(5000m, Price(At18, 60, false, Type(), rule));
    }

    [Fact]
    public void PRIC03_multiplier_scales_the_audience_base_rate()
    {
        var rule = Rule(1, RateRuleAudience.Everyone, RateRuleKind.Multiplier, 0.5m);
        Assert.Equal(2000m, Price(At18, 60, true, Type(), rule));
        Assert.Equal(3000m, Price(At18, 60, false, Type(), rule));
    }

    [Fact]
    public void PRIC03_priority_is_chosen_after_audience_filtering()
    {
        var high = Rule(1, RateRuleAudience.MembersOnly, value: 1000m, priority: 10);
        var low = Rule(2, RateRuleAudience.Everyone, value: 3000m, priority: 1);
        Assert.Equal(1000m, Price(At18, 60, true, Type(), high, low));
        Assert.Equal(3000m, Price(At18, 60, false, Type(), high, low));
    }
}
