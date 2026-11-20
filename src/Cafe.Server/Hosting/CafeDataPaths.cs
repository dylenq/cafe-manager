using Microsoft.Data.Sqlite;

namespace Cafe.Server.Hosting;

/// <summary>Where the server keeps its database, logs and backups.</summary>
public sealed record DataLocation(
    string ConnectionString,
    string DataDirectory,
    string LogDirectory,
    string BackupDirectory,
    bool UseProductionLayout)
{
    /// <summary>The database file named by the connection string (relative paths stay relative to the content root).</summary>
    public string DatabaseFile => new SqliteConnectionStringBuilder(ConnectionString).DataSource;

    /// <summary>Creates the data folder (production layout only; development files stay in the content root).</summary>
    public void EnsureDirectories()
    {
        if (UseProductionLayout && !string.IsNullOrWhiteSpace(DataDirectory))
        {
            Directory.CreateDirectory(DataDirectory);
        }
    }
}

/// <summary>
/// Pure resolution of the data location. Precedence: an explicit ConnectionStrings:Cafe always wins; otherwise
/// Production or a Windows service uses the ProgramData layout (or Cafe:DataDirectory); otherwise cafe.db in the
/// content root. The override is read from server configuration only, never from request input.
/// </summary>
public static class CafeDataPaths
{
    public const string FolderName = "CafeManager";
    public const string DatabaseFileName = "cafe.db";

    /// <summary>
    /// The folder that holds CafeManager's data by default: ProgramData on Windows. On other hosts
    /// CommonApplicationData is a system folder such as /usr/share that a normal user cannot write, so use the
    /// per-user LocalApplicationData instead.
    /// </summary>
    public static string DefaultAppDataRoot() =>
        DefaultAppDataRoot(
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string DefaultAppDataRoot(bool isWindows, string commonAppData, string localAppData)
    {
        if (isWindows && !string.IsNullOrWhiteSpace(commonAppData))
        {
            return commonAppData;
        }

        return !string.IsNullOrWhiteSpace(localAppData)
            ? localAppData
            : Path.Combine(Path.GetTempPath(), FolderName + "-data");
    }

    public static DataLocation Resolve(
        string? explicitConnectionString,
        string? dataDirectoryOverride,
        bool isProduction,
        bool isWindowsService,
        string commonAppData,
        string contentRoot)
    {
        var production = isProduction || isWindowsService;
        var root = !string.IsNullOrWhiteSpace(dataDirectoryOverride)
            ? dataDirectoryOverride.Trim()
            : Path.Combine(commonAppData, FolderName);

        var logs = production ? Path.Combine(root, "logs") : Path.Combine(contentRoot, "logs");
        var backups = production ? Path.Combine(root, "backups") : Path.Combine(contentRoot, "backups");

        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return new DataLocation(explicitConnectionString, ExplicitDirectory(explicitConnectionString, contentRoot), logs, backups, production);
        }

        if (production)
        {
            return new DataLocation($"Data Source={Path.Combine(root, DatabaseFileName)}", root, logs, backups, true);
        }

        return new DataLocation($"Data Source={DatabaseFileName}", contentRoot, logs, backups, false);
    }

    private static string ExplicitDirectory(string connectionString, string contentRoot)
    {
        try
        {
            var file = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (!string.IsNullOrWhiteSpace(file) && Path.IsPathRooted(file))
            {
                return Path.GetDirectoryName(file) ?? contentRoot;
            }
        }
        catch (ArgumentException)
        {
            // Unparseable string: the database layer reports it; fall back to the content root.
        }

        return contentRoot;
    }
}
