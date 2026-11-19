namespace Cafe.Server.Data;

/// <summary>One active (billable, countdown-consuming) interval on one station.</summary>
public class SessionSegment
{
    public int Id { get; set; }

    public int SessionId { get; set; }

    public Session Session { get; set; } = null!;

    public int StationId { get; set; }

    public DateTimeOffset StartUtc { get; set; }

    public DateTimeOffset? EndUtc { get; set; }
}
