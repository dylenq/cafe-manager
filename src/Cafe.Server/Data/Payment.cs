namespace Cafe.Server.Data;

/// <summary>Money received. Allocations say what it paid for; the single-target ids are a denormalised convenience.</summary>
public class Payment
{
    public int Id { get; set; }

    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public int? PackageSaleId { get; set; }

    public PackageSale? PackageSale { get; set; }

    public int? SessionId { get; set; }

    public Session? Session { get; set; }

    public string? Note { get; set; }

    public int? OrderId { get; set; }

    public Order? Order { get; set; }

    /// <summary>
    /// Null for a lone payment or the first payment of a split checkout; otherwise the first payment's id, so one
    /// checkout prints as one receipt.
    /// </summary>
    public int? PrimaryPaymentId { get; set; }

    public Payment? PrimaryPayment { get; set; }

    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}
