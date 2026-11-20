using Cafe.Server.Data;

namespace Cafe.Tests.Infrastructure;

/// <summary>Helpers for report tests: place payments on exact instants without going through the services.</summary>
public abstract class ReportTestBase : PosTestBase
{
    /// <summary>Inserts one payment (Amount = sum of the allocations) with its allocation rows.</summary>
    protected async Task<int> AddPaymentAsync(
        PaymentMethod method, DateTimeOffset atUtc, params (AllocationTargetType Type, int TargetId, decimal Amount)[] allocations)
    {
        await using var db = await DbAsync();
        var payment = new Payment
        {
            Amount = allocations.Sum(a => a.Amount),
            Method = method,
            AtUtc = atUtc,
        };
        foreach (var (type, targetId, amount) in allocations)
        {
            payment.Allocations.Add(new PaymentAllocation { TargetType = type, TargetId = targetId, Amount = amount });
        }

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }
}
