using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

    public string ConnectionString => $"Data Source={_dbPath}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Cafe", ConnectionString);
        foreach (var (key, value) in ExtraSettings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(this)));
    }

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
