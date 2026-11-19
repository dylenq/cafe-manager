namespace Cafe.Server.Data;

/// <summary>Audit trail entry for a session.</summary>
public class SessionEvent
{
    public int Id { get; set; }

    public int SessionId { get; set; }

    public Session Session { get; set; } = null!;

    public int StationId { get; set; }

    public SessionEventType Type { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public string Details { get; set; } = string.Empty;
}
