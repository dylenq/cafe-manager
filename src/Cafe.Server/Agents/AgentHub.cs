using Cafe.Shared.Agents;
using Microsoft.AspNetCore.SignalR;

namespace Cafe.Server.Agents;

public sealed class AgentHub(AgentRegistry registry, AgentCommandService commands) : Hub<IAgentClient>
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
        Context.Items["machineId"] = machineId;
        registry.Register(machineId, Context.ConnectionId, version, Context.Abort);
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
