using Microsoft.AspNetCore.Http.Connections.Client;

namespace Cafe.Agent.Core;

public sealed class AgentConnectionOptions
{
    /// <summary>0, 2, 5, 10, 30 seconds, then every 30 seconds forever.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultReconnectDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public string ServerUrl { get; set; } = string.Empty;

    public string SharedKey { get; set; } = string.Empty;

    public string MachineId { get; set; } = string.Empty;

    public string AgentVersion { get; set; } = "1.0";

    /// <summary>Tests inject the TestServer handler and transport here.</summary>
    public Action<HttpConnectionOptions>? ConfigureHttp { get; set; }

    public IReadOnlyList<TimeSpan>? ReconnectDelays { get; set; }
}
