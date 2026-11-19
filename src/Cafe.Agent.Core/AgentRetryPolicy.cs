using Microsoft.AspNetCore.SignalR.Client;

namespace Cafe.Agent.Core;

/// <summary>Backoff 0, 2, 5, 10, 30 s, then every 30 s forever. Never gives up.</summary>
public sealed class AgentRetryPolicy(IReadOnlyList<TimeSpan>? schedule = null) : IRetryPolicy
{
    public static TimeSpan DelayFor(int attempt, IReadOnlyList<TimeSpan>? schedule = null)
    {
        var delays = schedule is { Count: > 0 } ? schedule : AgentConnectionOptions.DefaultReconnectDelays;
        return delays[Math.Clamp(attempt, 0, delays.Count - 1)];
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        DelayFor((int)Math.Min(retryContext.PreviousRetryCount, int.MaxValue), schedule);
}
