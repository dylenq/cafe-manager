namespace Cafe.Server.Data;

/// <summary>A sellable block of prepaid minutes.</summary>
public class TimePackage
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Minutes { get; set; }

    /// <summary>Stored as TEXT so decimals round-trip exactly.</summary>
    public decimal Price { get; set; }

    /// <summary>Null means the minutes are usable on any station type.</summary>
    public int? StationTypeId { get; set; }

    public StationType? StationType { get; set; }

    public bool IsActive { get; set; } = true;
}
