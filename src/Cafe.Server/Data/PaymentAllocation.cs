namespace Cafe.Server.Data;

/// <summary>
/// How much of a payment pays towards one target. TargetId is polymorphic (session, order or package sale), so it has
/// no foreign key. Allocations are the source of truth for what is outstanding; they are insert-only.
/// </summary>
public class PaymentAllocation
{
    public int Id { get; set; }

    public int PaymentId { get; set; }

    public Payment Payment { get; set; } = null!;

    public AllocationTargetType TargetType { get; set; }

    public int TargetId { get; set; }

    public decimal Amount { get; set; }
}
