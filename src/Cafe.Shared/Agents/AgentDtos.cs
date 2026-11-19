namespace Cafe.Shared.Agents;

public record LockInfo(string StationName, string CafeName, string Reason);

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
    DateTimeOffset ServerUtcNow);
