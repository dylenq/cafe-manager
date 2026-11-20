using Cafe.Server.Sessions;

namespace Cafe.Server.Devices;

public static class TvServiceCollectionExtensions
{
    public static IServiceCollection AddTvServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TvOptions>(configuration.GetSection(TvOptions.SectionName));
        services.AddSingleton<ITvTransport, SamsungTvTransport>();
        services.AddSingleton<ITvDriver, SamsungTizenDriver>();
        services.AddSingleton<TvPairingService>();
        services.AddSingleton<TvStateCache>();
        services.AddSingleton<TvSettingsService>();
        services.AddSingleton<TvControlService>();
        services.AddSingleton<TvActionQueue>();
        services.AddSingleton<ISessionEventSink, TvSessionEventSink>();
        services.AddSingleton<IExpiryActionHandler, TvExpiryActionHandler>();
        if (TvMonitorService.IsEnabled(configuration))
        {
            services.AddHostedService<TvMonitorService>();
        }

        return services;
    }
}
