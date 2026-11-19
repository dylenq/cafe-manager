using Cafe.Server.Data;
using Cafe.Server.Sessions;

namespace Cafe.Server.Agents;

/// <summary>
/// Locks a PC when its paid time and grace are used up. Sending Lock again is harmless, so at-least-once delivery is
/// fine. When the agent is offline or the command was not delivered the handler throws, so the expiry evaluator records
/// the failure and retries (up to its attempt limit). A PC that stays offline longer than that is locked by the
/// on-connect state push (<see cref="AgentCommandService.PushStateAsync"/>) as soon as it reconnects, and the agent
/// locks itself locally when it loses the server. A station that is not a PC agent station is a no-op.
/// </summary>
public sealed class PcLockExpiryActionHandler(AgentCommandService commands) : IExpiryActionHandler
{
    public const string Reason = "Time is up. Please see the front desk.";

    public async Task ExecuteAsync(Station station, CancellationToken cancellationToken)
    {
        if (!await commands.LockExpiredStationAsync(station, Reason, cancellationToken))
        {
            throw new InvalidOperationException($"The lock command for {station.Name} was not delivered (agent offline or send failed).");
        }
    }
}
