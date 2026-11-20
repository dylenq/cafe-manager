namespace Cafe.Server.Payments;

/// <summary>
/// Waterfalls payments across targets: each payment, in order, fills the first target's outstanding, then the next.
/// Pure; callers validate that payments do not exceed the total outstanding.
/// </summary>
public static class AllocationPlanner
{
    public static IReadOnlyList<IReadOnlyList<PlannedAllocation>> Plan(
        IReadOnlyList<PlanTarget> targets, IReadOnlyList<decimal> paymentAmounts)
    {
        var total = targets.Sum(t => Math.Max(0m, t.Outstanding));
        if (paymentAmounts.Sum() > total)
        {
            throw new ArgumentException("Payments exceed the total outstanding.", nameof(paymentAmounts));
        }

        var left = targets.Select(t => Math.Max(0m, t.Outstanding)).ToArray();
        var result = new List<IReadOnlyList<PlannedAllocation>>();
        foreach (var amount in paymentAmounts)
        {
            var remaining = amount;
            var parts = new List<PlannedAllocation>();
            for (var i = 0; i < targets.Count && remaining > 0m; i++)
            {
                if (left[i] <= 0m)
                {
                    continue;
                }

                var take = Math.Min(left[i], remaining);
                left[i] -= take;
                remaining -= take;
                parts.Add(new PlannedAllocation(targets[i].Type, targets[i].Id, take));
            }

            result.Add(parts);
        }

        return result;
    }
}
