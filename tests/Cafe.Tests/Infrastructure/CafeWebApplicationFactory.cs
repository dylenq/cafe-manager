using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Cafe.Tests.Infrastructure;

/// <summary>WebApplicationFactory backed by a throwaway SQLite file, so every test gets a fresh database.</summary>
public class CafeWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cafe-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Cafe", $"Data Source={_dbPath}");
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
