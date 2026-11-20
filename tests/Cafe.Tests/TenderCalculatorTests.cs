using Cafe.Server.Data;
using Cafe.Server.Payments;

namespace Cafe.Tests;

/// <summary>D-04: pure tender, change and remaining maths behind the payment dialog.</summary>
public class TenderCalculatorTests
{
    private static TenderResult Calc(decimal due, params (PaymentMethod Method, decimal Entered)[] entries) =>
        TenderCalculator.Calculate(due, entries.Select(e => new TenderEntry(e.Method, e.Entered)).ToList());

    [Fact]
    public void D04_cash_overshoot_returns_change_and_applies_only_the_due()
    {
        var r = Calc(8000m, (PaymentMethod.Cash, 10000m));

        Assert.Equal(8000m, r.Applied);
        Assert.Equal(2000m, r.Change);
        Assert.Equal(0m, r.Remaining);
        var line = Assert.Single(r.Lines);
        Assert.Equal(new TenderLine(PaymentMethod.Cash, 8000m, 10000m), line);
    }

    [Fact]
    public void D04_exact_split_cash_plus_card_has_no_change_and_nothing_remaining()
    {
        var r = Calc(8000m, (PaymentMethod.Cash, 5000m), (PaymentMethod.Card, 3000m));

        Assert.Equal(0m, r.Remaining);
        Assert.Equal(0m, r.Change);
        Assert.False(r.CardClamped);
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(8000m, r.Applied);
    }

    [Fact]
    public void D04_card_above_the_due_is_clamped_and_flagged()
    {
        var r = Calc(8000m, (PaymentMethod.Card, 9000m));

        Assert.True(r.CardClamped);
        Assert.Equal(8000m, r.Applied);
        Assert.Equal(0m, r.Change);
        Assert.Equal(new TenderLine(PaymentMethod.Card, 8000m), Assert.Single(r.Lines));
    }

    [Fact]
    public void D04_partial_cash_leaves_the_remainder()
    {
        var r = Calc(8000m, (PaymentMethod.Cash, 3000m));

        Assert.Equal(5000m, r.Remaining);
        Assert.Equal(3000m, r.Applied);
        Assert.Equal(0m, r.Change);
    }

    [Fact]
    public void D04_cash_change_is_only_on_the_line_that_overshoots()
    {
        var r = Calc(8000m, (PaymentMethod.Cash, 5000m), (PaymentMethod.Cash, 5000m));

        Assert.Equal(2000m, r.Change);
        Assert.Equal(5000m, r.Lines[0].Tendered);
        Assert.Equal(3000m, r.Lines[1].Amount);
        Assert.Equal(5000m, r.Lines[1].Tendered);
    }

    [Fact]
    public void D04_zero_and_negative_entries_are_ignored()
    {
        var r = Calc(8000m, (PaymentMethod.Cash, 0m), (PaymentMethod.Card, -5m), (PaymentMethod.Cash, 8000m));

        Assert.Single(r.Lines);
        Assert.Equal(0m, r.Remaining);
    }

    [Fact]
    public void D04_nothing_due_produces_no_lines()
    {
        var r = Calc(0m, (PaymentMethod.Cash, 1000m));

        Assert.Empty(r.Lines);
        Assert.Equal(0m, r.Applied);
        Assert.Equal(0m, r.Remaining);
    }

    [Fact]
    public void D04_decimal_point_one_plus_point_two_on_point_three_leaves_exactly_zero()
    {
        var r = Calc(0.3m, (PaymentMethod.Cash, 0.1m), (PaymentMethod.Card, 0.2m));

        Assert.Equal(0m, r.Remaining);
        Assert.Equal(0.3m, r.Applied);
    }

    [Fact]
    public void WR06_entries_round_half_away_from_zero_like_the_pricing_engine()
    {
        var r = Calc(10m, (PaymentMethod.Cash, 0.125m));

        Assert.Equal(0.13m, r.Applied);
        Assert.Equal(new TenderLine(PaymentMethod.Cash, 0.13m, 0.13m), Assert.Single(r.Lines));
    }
}
