namespace Cafe.Server.Data;

public class StationType
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Stored as TEXT so decimals round-trip exactly. Never sort by this in SQL.</summary>
    public decimal HourlyRate { get; set; }

    /// <summary>Hourly rate for members; null means members pay the normal rate. Stored as TEXT.</summary>
    public decimal? MemberHourlyRate { get; set; }

    public int SortOrder { get; set; }

    public ICollection<Station> Stations { get; set; } = new List<Station>();
}
