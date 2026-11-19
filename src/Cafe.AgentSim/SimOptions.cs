using System.Globalization;

namespace Cafe.AgentSim;

public sealed record SimOptions(string Server, string Key, int Count, string Prefix, bool NoColor)
{
    public const string DefaultServer = "http://localhost:5080";
    public const string KeyEnvironmentVariable = "CAFE_AGENT_KEY";
    public const int MaxCount = 200;

    public const string Usage =
        """
        Usage: dotnet run --project src/Cafe.AgentSim -- --server <url> --key <key> [--count N] [--prefix SIM-] [--no-color]

          --server    Server base URL (default http://localhost:5080)
          --key       Agent shared key from the server's settings page (or set CAFE_AGENT_KEY)
          --count     Number of simulated agents, 1 to 200 (default 1)
          --prefix    Machine id prefix (default SIM-); ids are SIM-01, SIM-02, ...
          --no-color  Plain text output
          --help      Show this help
        """;

    /// <summary>Machine id for a 1-based index: prefix plus zero-padded number, at least two digits wide.</summary>
    public string MachineId(int index)
    {
        var width = Math.Max(2, Count.ToString(CultureInfo.InvariantCulture).Length);
        return Prefix + index.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
    }

    public static SimParseResult Parse(string[] args, Func<string, string?> getEnv)
    {
        var server = DefaultServer;
        string? key = null;
        var count = 1;
        var prefix = "SIM-";
        var noColor = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h" or "-?":
                    return SimParseResult.Help();
                case "--no-color":
                    noColor = true;
                    break;
                case "--server" or "--key" or "--count" or "--prefix":
                    if (i + 1 >= args.Length)
                    {
                        return SimParseResult.Fail($"Missing value for {arg}.");
                    }

                    var value = args[++i];
                    if (arg == "--server")
                    {
                        server = value;
                    }
                    else if (arg == "--key")
                    {
                        key = value;
                    }
                    else if (arg == "--prefix")
                    {
                        prefix = value;
                    }
                    else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count))
                    {
                        return SimParseResult.Fail("--count must be a whole number.");
                    }

                    break;
                default:
                    return SimParseResult.Fail($"Unknown option: {arg}");
            }
        }

        key ??= getEnv(KeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            return SimParseResult.Fail($"A key is required: pass --key or set {KeyEnvironmentVariable}.");
        }

        if (count < 1 || count > MaxCount)
        {
            return SimParseResult.Fail($"--count must be between 1 and {MaxCount}.");
        }

        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return SimParseResult.Fail("--server must be an absolute http or https URL.");
        }

        if (string.IsNullOrWhiteSpace(prefix))
        {
            return SimParseResult.Fail("--prefix must not be empty.");
        }

        return SimParseResult.Ok(new SimOptions(server, key.Trim(), count, prefix, noColor));
    }
}

public sealed record SimParseResult(SimOptions? Options, string? Error, bool ShowHelp)
{
    public static SimParseResult Ok(SimOptions options) => new(options, null, false);

    public static SimParseResult Fail(string error) => new(null, error, false);

    public static SimParseResult Help() => new(null, null, true);
}
