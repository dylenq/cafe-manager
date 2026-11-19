namespace Cafe.Agent.Core;

public sealed class SystemAgentClock : IAgentClock
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();

    // Environment.TickCount64 is the OS uptime counter: unaffected by changes to the system date and time.
    public long MonotonicMilliseconds => Environment.TickCount64;
}
