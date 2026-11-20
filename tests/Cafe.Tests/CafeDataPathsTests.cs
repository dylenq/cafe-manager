using Cafe.Server.Hosting;

namespace Cafe.Tests;

public class CafeDataPathsTests
{
    private static readonly string Common = Path.Combine(Path.GetTempPath(), "pd");
    private static readonly string Content = Path.Combine(Path.GetTempPath(), "content");

    private static DataLocation Resolve(string? explicitCs = null, string? over = null, bool prod = false, bool service = false) =>
        CafeDataPaths.Resolve(explicitCs, over, prod, service, Common, Content);

    [Fact]
    public void Development_keeps_cafe_db_in_the_content_root()
    {
        var loc = Resolve();

        Assert.Equal("Data Source=cafe.db", loc.ConnectionString);
        Assert.False(loc.UseProductionLayout);
        Assert.Equal(Content, loc.DataDirectory);
        Assert.Equal(Path.Combine(Content, "logs"), loc.LogDirectory);
        Assert.Equal(Path.Combine(Content, "backups"), loc.BackupDirectory);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Production_or_service_uses_the_ProgramData_layout(bool prod, bool service)
    {
        var loc = Resolve(prod: prod, service: service);
        var root = Path.Combine(Common, "CafeManager");

        Assert.True(loc.UseProductionLayout);
        Assert.Equal($"Data Source={Path.Combine(root, "cafe.db")}", loc.ConnectionString);
        Assert.Equal(root, loc.DataDirectory);
        Assert.Equal(Path.Combine(root, "logs"), loc.LogDirectory);
        Assert.Equal(Path.Combine(root, "backups"), loc.BackupDirectory);
    }

    [Fact]
    public void Explicit_connection_string_wins_in_production_and_service_mode()
    {
        var file = Path.Combine(Path.GetTempPath(), "mine", "x.db");
        var loc = Resolve($"Data Source={file}", prod: true, service: true);

        Assert.Equal($"Data Source={file}", loc.ConnectionString);
        Assert.Equal(Path.GetDirectoryName(file), loc.DataDirectory);
        Assert.True(loc.UseProductionLayout);
    }

    [Fact]
    public void Explicit_relative_connection_string_uses_the_content_root_as_data_directory()
    {
        var loc = Resolve("Data Source=custom.db");

        Assert.Equal("Data Source=custom.db", loc.ConnectionString);
        Assert.Equal(Content, loc.DataDirectory);
    }

    [Fact]
    public void Data_directory_override_replaces_the_ProgramData_root()
    {
        var over = Path.Combine(Path.GetTempPath(), "override");
        var loc = Resolve(over: over, prod: true);

        Assert.Equal($"Data Source={Path.Combine(over, "cafe.db")}", loc.ConnectionString);
        Assert.Equal(Path.Combine(over, "logs"), loc.LogDirectory);
        Assert.Equal(Path.Combine(over, "backups"), loc.BackupDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_connection_string_counts_as_not_set(string value)
    {
        Assert.Equal("Data Source=cafe.db", Resolve(value).ConnectionString);
        Assert.Contains("CafeManager", Resolve(value, prod: true).ConnectionString);
    }

    [Fact]
    public void EnsureDirectories_creates_the_production_folder_only()
    {
        var over = Path.Combine(Path.GetTempPath(), $"cafe-paths-{Guid.NewGuid():N}");
        try
        {
            Resolve(over: over).EnsureDirectories();
            Assert.False(Directory.Exists(over));

            Resolve(over: over, prod: true).EnsureDirectories();
            Assert.True(Directory.Exists(over));
        }
        finally
        {
            if (Directory.Exists(over))
            {
                Directory.Delete(over, true);
            }
        }
    }

    [Fact]
    public void Default_root_is_ProgramData_on_Windows_and_the_per_user_folder_elsewhere()
    {
        Assert.Equal("/pd", CafeDataPaths.DefaultAppDataRoot(true, "/pd", "/local"));
        Assert.Equal("/local", CafeDataPaths.DefaultAppDataRoot(false, "/usr/share", "/local"));
        Assert.NotEqual("/usr/share", CafeDataPaths.DefaultAppDataRoot(false, "/usr/share", ""));
        Assert.Contains("CafeManager", CafeDataPaths.DefaultAppDataRoot(false, "/usr/share", ""));
    }
}
