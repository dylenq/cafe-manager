using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Agents;

/// <summary>
/// Manual commands from staff to a PC agent. Every attempt for a known station is validated, delivered with a
/// time box and written to the AgentCommandLog, whether or not it reached the PC.
/// </summary>
public sealed class AgentRemoteControlService(
    IHubContext<AgentHub, IAgentClient> hub,
    AgentRegistry registry,
    AgentSettingsService settings,
    IDbContextFactory<CafeDbContext> dbFactory,
    TimeProvider time,
    ILogger<AgentRemoteControlService> logger)
{
    public const int MaxMessageLength = 500;
    public const string OfflineMessage = "PC agent is offline";
    public const string LockReason = "Locked by staff";

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public async Task<ServiceResult> SendAsync(int stationId, AgentCommandKind kind, string? text = null, CancellationToken cancellationToken = default)
    {
        Station? station;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stationId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not load station {StationId} for a PC command.", stationId);
            return ServiceResult.Fail("Could not send the command. Try again.");
        }

        if (station is null)
        {
            return ServiceResult.NotFound("That station no longer exists.");
        }

        if (station.Kind != StationKind.PC)
        {
            return await RejectAsync(station, kind, "Only PC stations can receive PC commands.", "Not sent: not a PC", cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(station.AgentMachineId))
        {
            return await RejectAsync(station, kind, "This PC has no agent assigned. Assign one in Settings first.", "Not sent: no agent assigned", cancellationToken);
        }

        string? message = null;
        if (kind == AgentCommandKind.Message)
        {
            message = CleanMessage(text);
            if (message is null)
            {
                return await RejectAsync(station, kind, $"Enter a message of 1 to {MaxMessageLength} characters.",
                    $"Not sent: message must be 1 to {MaxMessageLength} characters", cancellationToken);
            }
        }

        if (!registry.TryGetConnectionId(station.AgentMachineId, out var connectionId))
        {
            return await RejectAsync(station, kind, OfflineMessage, "Not sent: agent offline", cancellationToken);
        }

        try
        {
            var client = hub.Clients.Client(connectionId);
            Task delivery;
            switch (kind)
            {
                case AgentCommandKind.Lock:
                    var cafeName = await settings.GetCafeNameAsync(cancellationToken);
                    delivery = client.Lock(new LockInfo(station.Name, cafeName, LockReason));
                    break;
                case AgentCommandKind.Unlock:
                    // Free unlock: no session, so open-ended with no remaining time and nothing elapsed.
                    delivery = client.Unlock(new SessionInfo(AgentSessionMode.OpenEnded, null, 0, station.Name, time.GetUtcNow()));
                    break;
                case AgentCommandKind.Message:
                    delivery = client.ShowMessage(message!);
                    break;
                case AgentCommandKind.Shutdown:
                    delivery = client.Shutdown();
                    break;
                case AgentCommandKind.Restart:
                    delivery = client.Restart();
                    break;
                default:
                    return await RejectAsync(station, kind, "Unknown command.", "Not sent: unknown command", cancellationToken);
            }

            await delivery.WaitAsync(SendTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Log the exception for diagnostics; the audit row and the user only get the short message.
            logger.LogWarning(ex, "Sending {Command} to the agent of station {StationId} failed.", kind, station.Id);
            await WriteLogAsync(station, kind, "Failed: " + ex.Message, cancellationToken);
            return ServiceResult.Fail("Could not reach the PC agent. Try again.");
        }

        await WriteLogAsync(station, kind, message is null ? "Delivered" : "Delivered: " + message, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Strips control characters (newlines are kept) and trims; null when empty or too long.</summary>
    internal static string? CleanMessage(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = new string(text.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        return cleaned.Length is >= 1 and <= MaxMessageLength ? cleaned : null;
    }

    private async Task<ServiceResult> RejectAsync(Station station, AgentCommandKind kind, string userMessage, string logDetails, CancellationToken ct)
    {
        await WriteLogAsync(station, kind, logDetails, ct);
        return ServiceResult.Fail(userMessage);
    }

    private async Task WriteLogAsync(Station station, AgentCommandKind kind, string details, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.AgentCommandLogs.Add(new AgentCommandLog
            {
                StationId = station.Id,
                StationName = Truncate(station.Name, 100),
                Command = kind,
                AtUtc = time.GetUtcNow(),
                Details = Truncate(details, 300),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write the agent command log for station {StationId}.", station.Id);
        }
    }

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
