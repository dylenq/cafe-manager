namespace Cafe.Server.Data;

/// <summary>A command sent to a PC agent or a console TV (manual or automatic). Stored as an int, so never reorder.</summary>
public enum AgentCommandKind
{
    Lock = 0,
    Unlock = 1,
    Message = 2,
    Shutdown = 3,
    Restart = 4,
    TvPowerOn = 5,
    TvPowerOff = 6,
}

/// <summary>
/// Audit row for an agent or TV power command. StationId is a plain int (no foreign key) so the trail survives
/// station deletion; the station name is snapshotted for the same reason.
/// </summary>
public class AgentCommandLog
{
    public int Id { get; set; }

    public int StationId { get; set; }

    public string StationName { get; set; } = string.Empty;

    public AgentCommandKind Command { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public string Details { get; set; } = string.Empty;
}
