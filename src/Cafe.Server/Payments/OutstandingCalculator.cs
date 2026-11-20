namespace Cafe.Server.Payments;

/// <summary>Outstanding = total minus allocations, never negative. Summed in memory (SQLite cannot SUM TEXT decimals).</summary>
public static class OutstandingCalculator
{
    public static decimal Remaining(decimal total, IEnumerable<decimal> allocated) =>
        Math.Max(0m, total - allocated.Sum());
}
