using Cafe.Shared.Agents;
using Microsoft.AspNetCore.SignalR;

namespace Cafe.Server.Agents;

public sealed class AgentHub(AgentRegistry registry, AgentCommandService commands, ILogger<AgentHub> logger) : Hub<IAgentClient>
{
    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var machineId = http?.Request.Headers[AgentProtocol.MachineIdHeader].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(machineId))
        {
            Context.Abort();
            return;
        }

        var version = http?.Request.Headers[AgentProtocol.VersionHeader].FirstOrDefault() ?? string.Empty;
        var remote = http?.Connection.RemoteIpAddress?.ToString();
        if (!registry.Register(machineId, Context.ConnectionId, version, Context.Abort, remote))
        {
            // First connection wins: refuse the newcomer so it cannot displace or impersonate the live agent.
            logger.LogWarning(
                "Rejected a second connection for machine id {MachineId} from {Remote}: that machine id is already connected.",
                machineId, remote ?? "unknown");
            Context.Abort();
            return;
        }

        Context.Items["machineId"] = machineId;
        await base.OnConnectedAsync();
        await commands.PushStateAsync(machineId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("machineId", out var id) && id is string machineId)
        {
            registry.Unregister(machineId, Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
