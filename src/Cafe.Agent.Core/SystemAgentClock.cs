namespace Cafe.Agent.Core;

public sealed class SystemAgentClock : IAgentClock
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}
