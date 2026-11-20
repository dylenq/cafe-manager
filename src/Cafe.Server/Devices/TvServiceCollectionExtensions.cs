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
        services.AddSingleton<IExpiryActionHandler, TvExpiryActionHandler>();
        return services;
    }
}
