using Cafe.Server.Orders;
using Cafe.Server.Products;

namespace Cafe.Server.Payments;

/// <summary>POS registrations. Later Phase 6 plans add theirs here, not in Program.cs.</summary>
public static class PosServiceCollectionExtensions
{
    public static IServiceCollection AddPosServices(this IServiceCollection services)
    {
        services.AddSingleton<SettlementLock>();
        services.AddScoped<ProductService>();
        services.AddScoped<OrderService>();
        services.AddScoped<SettlementService>();
        services.AddScoped<CheckoutService>();
        services.AddScoped<OpenTabsService>();
        services.AddScoped<PaymentHistoryService>();
        return services;
    }
}
