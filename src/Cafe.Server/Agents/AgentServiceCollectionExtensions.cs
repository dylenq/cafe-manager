using Cafe.Server.Sessions;

namespace Cafe.Server.Agents;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAgentServices(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<AgentSettingsService>();
        services.AddSingleton<AgentCommandService>();
        services.AddSingleton<ISessionEventSink>(sp => sp.GetRequiredService<AgentCommandService>());
        services.AddSingleton<SessionSignalPublisher>();
        return services;
    }
}
