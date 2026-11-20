using Cafe.Server.Data;
using Cafe.Server.Payments;

namespace Cafe.Tests;

/// <summary>D-01/D-04: the waterfall and the outstanding floor are pure and exact.</summary>
public class AllocationPlannerTests
{
    private static PlanTarget T(int id, decimal outstanding) => new(AllocationTargetType.Order, id, outstanding);

    private static string Flat(IReadOnlyList<PlannedAllocation> parts) => string.Join(",", parts.Select(p => $"{p.Id}:{p.Amount}"));

    [Fact]
    public void Single_payment_fills_targets_in_order()
    {
        var plan = AllocationPlanner.Plan([T(1, 60m), T(2, 40m)], [100m]);

        Assert.Equal("1:60,2:40", Flat(Assert.Single(plan)));
    }

    [Fact]
    public void Split_payments_waterfall_across_targets()
    {
        var plan = AllocationPlanner.Plan([T(1, 60m), T(2, 40m)], [50m, 50m]);

        Assert.Equal("1:50", Flat(plan[0]));
        Assert.Equal("1:10,2:40", Flat(plan[1]));
    }

    [Fact]
    public void Cash_5000_plus_card_3000_over_6000_and_2000()
    {
        var plan = AllocationPlanner.Plan([T(1, 6000m), T(2, 2000m)], [5000m, 3000m]);

        Assert.Equal("1:5000", Flat(plan[0]));
        Assert.Equal("1:1000,2:2000", Flat(plan[1]));
    }

    [Fact]
    public void Payment_smaller_than_first_target_only_touches_it()
    {
        var plan = AllocationPlanner.Plan([T(1, 60m), T(2, 40m)], [20m]);

        Assert.Equal("1:20", Flat(plan[0]));
    }

    [Fact]
    public void Exact_fit_leaves_nothing()
    {
        var plan = AllocationPlanner.Plan([T(1, 60m), T(2, 40m)], [60m, 40m]);

        Assert.Equal("1:60", Flat(plan[0]));
        Assert.Equal("2:40", Flat(plan[1]));
    }

    [Fact]
    public void Payments_above_the_total_throw()
    {
        Assert.Throws<ArgumentException>(() => AllocationPlanner.Plan([T(1, 60m), T(2, 40m)], [100m, 0.01m]));
    }

    [Fact]
    public void Zero_outstanding_targets_are_skipped()
    {
        var plan = AllocationPlanner.Plan([T(1, 0m), T(2, 40m)], [40m]);

        Assert.Equal("2:40", Flat(plan[0]));
    }

    [Fact]
    public void Decimal_amounts_sum_exactly()
    {
        var plan = AllocationPlanner.Plan([T(1, 100m)], [33.33m, 33.33m, 33.34m]);

        Assert.Equal(100m, plan.SelectMany(p => p).Sum(a => a.Amount));
        Assert.Equal(33.34m, plan[2][0].Amount);
    }

    [Fact]
    public void Outstanding_subtracts_allocations()
    {
        Assert.Equal(50m, OutstandingCalculator.Remaining(100m, [30m, 20m]));
    }

    [Fact]
    public void Outstanding_is_never_negative()
    {
        Assert.Equal(0m, OutstandingCalculator.Remaining(100m, [120m]));
    }

    [Fact]
    public void Outstanding_without_allocations_is_the_total()
    {
        Assert.Equal(100m, OutstandingCalculator.Remaining(100m, []));
    }
}
