namespace Cafe.Shared.Agents;

public static class AgentProtocol
{
    public const string HubPath = "/hubs/agent";
    public const string KeyHeader = "X-Agent-Key";
    public const string MachineIdHeader = "X-Machine-Id";
    public const string VersionHeader = "X-Agent-Version";
    public const string AccessTokenQuery = "access_token";
    public const int MaxMachineIdLength = 100;
}
