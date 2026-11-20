using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Cafe.Tests.Infrastructure;

/// <summary>WebApplicationFactory backed by a throwaway SQLite file, so every test gets a fresh database.</summary>
public class CafeWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cafe-test-{Guid.NewGuid():N}.db");

    /// <summary>Remote address the TestServer reports for every request (it has none of its own). Defaults to loopback.</summary>
    public System.Net.IPAddress? RemoteAddress { get; set; } = System.Net.IPAddress.Loopback;

    /// <summary>Extra configuration (e.g. Setup:AllowRemote) applied when the host is built.</summary>
    public Dictionary<string, string?> ExtraSettings { get; } = new();

    /// <summary>Service registrations applied after the server's own, to swap in test doubles.</summary>
    public List<Action<IServiceCollection>> ServiceOverrides { get; } = [];

    /// <summary>
    /// The TimeProvider injected into the server. Starts at the real current instant (whole seconds) so
    /// absolute-time assertions such as cookie expiry still hold; tests move it with Advance.
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(TruncateToSecond(DateTimeOffset.UtcNow));

    public string ConnectionString => $"Data Source={_dbPath}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Cafe", ConnectionString);
        builder.UseSetting("Cafe:TimeZone", "UTC");
        // The real-time ticker would interfere with FakeTimeProvider tests; ExtraSettings can turn it back on.
        builder.UseSetting("Sessions:RunTicker", "false");
        builder.UseSetting("Tv:RunMonitor", "false");
        foreach (var (key, value) in ExtraSettings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(this));
            foreach (var apply in ServiceOverrides)
            {
                apply(services);
            }
        });
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Offset);

    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-shm", _dbPath + "-wal" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}

internal sealed class RemoteAddressStartupFilter(CafeWebApplicationFactory factory) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = factory.RemoteAddress;
            return nextMiddleware(context);
        });
        next(app);
    };
}
