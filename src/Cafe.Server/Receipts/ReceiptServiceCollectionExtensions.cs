namespace Cafe.Server.Receipts;

/// <summary>Receipt registrations. Later Phase 6 plans add theirs here, not in Program.cs.</summary>
public static class ReceiptServiceCollectionExtensions
{
    public static IServiceCollection AddReceiptServices(this IServiceCollection services)
    {
        services.AddSingleton<ReceiptSettingsService>();
        services.AddSingleton<EscPosReceiptRenderer>();
        services.AddSingleton<INetworkReceiptPrinter>(_ => new NetworkEscPosPrinter());
        services.AddSingleton<IWindowsReceiptPrinter, WindowsRawPrinter>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<ReceiptPrintService>();
        return services;
    }
}
