namespace Cafe.Shared.Agents;

/// <summary>
/// Sequence is a server-side monotonic counter per message (0 means unsequenced). The agent ignores a Lock, Unlock or
/// SessionUpdated whose sequence is not newer than the last one it applied, so a late stale message cannot undo a newer one.
/// </summary>
public record LockInfo(string StationName, string CafeName, string Reason, long Sequence = 0);

/// <summary>Named to avoid clashing with the server's own SessionMode.</summary>
public enum AgentSessionMode
{
    Prepaid = 0,
    OpenEnded = 1,
}

/// <summary>RemainingSeconds is null for open-ended sessions. ServerUtcNow lets the agent correct for clock skew.</summary>
public record SessionInfo(
    AgentSessionMode Mode,
    long? RemainingSeconds,
    long ElapsedSeconds,
    string StationName,
    DateTimeOffset ServerUtcNow,
    long Sequence = 0);
