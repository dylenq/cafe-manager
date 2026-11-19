using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Agents;

/// <summary>Maps session state to commands for the PC agents. Every outbound send is time-boxed and fault-isolated.</summary>
public sealed class AgentCommandService(
    IHubContext<AgentHub, IAgentClient> hub,
    AgentRegistry registry,
    IDbContextFactory<CafeDbContext> dbFactory,
    TimeProvider time,
    AgentSettingsService settings,
    ILogger<AgentCommandService> logger) : ISessionEventSink
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public const string DefaultLockReason = "Please see the front desk to start a session";
    public const string UnknownMachineReason = "This PC is not set up yet. Please see the front desk.";

    /// <summary>Sends the state the machine should be in right now: Unlock with an Active session, otherwise Lock.</summary>
    public async Task PushStateAsync(string machineId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!registry.TryGetConnectionId(machineId, out var connectionId))
            {
                return;
            }

            var cafeName = await settings.GetCafeNameAsync(cancellationToken);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.AgentMachineId == machineId, cancellationToken);
            if (station is null)
            {
                await SendAsync(connectionId, c => c.Lock(new LockInfo(machineId, cafeName, UnknownMachineReason)));
                return;
            }

            var session = await db.Sessions.AsNoTracking().Include(s => s.Segments)
                .FirstOrDefaultAsync(s => s.StationId == station.Id && s.Status != SessionStatus.Ended, cancellationToken);
            if (session is { Status: SessionStatus.Active })
            {
                var info = BuildSessionInfo(session, station.Name);
                await SendAsync(connectionId, c => c.Unlock(info));
                return;
            }

            var reason = session is { Status: SessionStatus.Paused } ? "Session paused" : DefaultLockReason;
            await SendAsync(connectionId, c => c.Lock(new LockInfo(station.Name, cafeName, reason)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not push state to agent.");
        }
    }

    public async Task OnSessionSignalAsync(SessionSignal signal, CancellationToken cancellationToken)
    {
        if (signal.Kind is not (SessionSignalKind.Started or SessionSignalKind.Stopped))
        {
            return; // The remaining kinds are completed by plan 03-02.
        }

        string? machineId;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var station = await db.Stations.AsNoTracking()
                .Where(s => s.Id == signal.StationId && s.Kind == StationKind.PC)
                .Select(s => new { s.AgentMachineId })
                .FirstOrDefaultAsync(cancellationToken);
            machineId = station?.AgentMachineId;
        }

        if (!string.IsNullOrEmpty(machineId))
        {
            await PushStateAsync(machineId, cancellationToken);
        }
    }

    private SessionInfo BuildSessionInfo(Session session, string stationName)
    {
        var now = time.GetUtcNow();
        var closed = session.Segments.Where(x => x.EndUtc is not null).Sum(x => (long)(x.EndUtc!.Value - x.StartUtc).TotalSeconds);
        var open = session.Segments.FirstOrDefault(x => x.EndUtc is null)?.StartUtc;
        var active = SessionClock.ActiveSeconds(closed, open, now);
        return session.Mode == SessionMode.Prepaid && session.PurchasedMinutes is { } purchased
            ? new SessionInfo(AgentSessionMode.Prepaid, Math.Max(SessionClock.RemainingSeconds(purchased, active), 0), active, stationName, now)
            : new SessionInfo(AgentSessionMode.OpenEnded, null, active, stationName, now);
    }

    private async Task SendAsync(string connectionId, Func<IAgentClient, Task> send)
    {
        try
        {
            await send(hub.Clients.Client(connectionId)).WaitAsync(SendTimeout);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sending a command to an agent failed.");
        }
    }
}
