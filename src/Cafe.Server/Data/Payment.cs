namespace Cafe.Server.Data;

/// <summary>Money received. Minimal in Phase 5; Phase 6 extends it with orders.</summary>
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
}
