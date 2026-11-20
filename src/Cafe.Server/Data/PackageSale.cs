namespace Cafe.Server.Data;

/// <summary>A package sold to a customer. Price, minutes and bucket are snapshots of the package at sale time.</summary>
public class PackageSale
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public int TimePackageId { get; set; }

    public TimePackage TimePackage { get; set; } = null!;

    public decimal Price { get; set; }

    public int Minutes { get; set; }

    /// <summary>Snapshot of the package's bucket; a plain column, not a foreign key.</summary>
    public int? StationTypeId { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}
