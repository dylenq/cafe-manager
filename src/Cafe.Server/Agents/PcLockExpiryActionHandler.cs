using Cafe.Server.Data;
using Cafe.Server.Sessions;

namespace Cafe.Server.Agents;

/// <summary>
/// Locks a PC when its paid time and grace are used up. Sending Lock again is harmless, so at-least-once delivery
/// is fine. A PC whose agent is offline at expiry is not an error here (no retry storm): it is locked by the
/// on-connect state push (<see cref="AgentCommandService.PushStateAsync"/>) as soon as it reconnects.
/// </summary>
public sealed class PcLockExpiryActionHandler(AgentCommandService commands) : IExpiryActionHandler
{
    public const string Reason = "Time is up. Please see the front desk.";

    public Task ExecuteAsync(Station station, CancellationToken cancellationToken) =>
        commands.LockStationAsync(station, Reason, cancellationToken);
}
