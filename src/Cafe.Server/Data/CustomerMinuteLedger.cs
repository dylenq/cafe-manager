namespace Cafe.Server.Data;

/// <summary>
/// One immutable balance movement. The ledger is the only source of truth: the balance of a bucket is the
/// SUM of MinutesDelta for (CustomerId, StationTypeId); there is no mutable balance column.
/// </summary>
public class CustomerMinuteLedger
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    /// <summary>The bucket; null is the any-station-type bucket.</summary>
    public int? StationTypeId { get; set; }

    /// <summary>Positive credits, negative debits.</summary>
    public int MinutesDelta { get; set; }

    public LedgerReason Reason { get; set; }

    public int? SessionId { get; set; }

    public Session? Session { get; set; }

    public int? PackageSaleId { get; set; }

    public PackageSale? PackageSale { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public string? Note { get; set; }
}
