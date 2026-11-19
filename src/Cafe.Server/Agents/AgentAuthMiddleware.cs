using System.Security.Cryptography;
using System.Text;
using Cafe.Shared.Agents;

namespace Cafe.Server.Agents;

/// <summary>
/// The only gate in front of the agent hub: validates the shared key on every request to the hub route
/// (negotiate, long-poll and websocket alike). The admin cookie is not involved.
/// </summary>
public sealed class AgentAuthMiddleware(RequestDelegate next, ILogger<AgentAuthMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AgentSettingsService settings)
    {
        if (!context.Request.Path.StartsWithSegments(AgentProtocol.HubPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var supplied = context.Request.Headers[AgentProtocol.KeyHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(supplied))
        {
            supplied = context.Request.Query[AgentProtocol.AccessTokenQuery].FirstOrDefault();
        }

        var expected = await settings.GetOrCreateSharedKeyAsync(context.RequestAborted);
        if (string.IsNullOrEmpty(supplied) || !KeysMatch(supplied, expected))
        {
            // Never log the supplied key or machine id.
            logger.LogWarning("Agent hub request rejected (bad or missing key) from {Remote}.", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var machineId = context.Request.Headers[AgentProtocol.MachineIdHeader].FirstOrDefault();
        if (!IsValidMachineId(machineId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await next(context);
    }

    /// <summary>Trimmed length 1 to 100 with no control characters.</summary>
    internal static bool IsValidMachineId(string? machineId)
    {
        var trimmed = machineId?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= AgentProtocol.MaxMachineIdLength
            && !trimmed.Any(c => c < 0x20 || c == 0x7F);
    }

    private static bool KeysMatch(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
