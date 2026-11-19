using System.Text.Json;

namespace Cafe.Agent.Core;

public sealed record AgentSettingsLoadResult(AgentSettings? Settings, IReadOnlyList<string> Errors)
{
    public bool Success => Settings is not null;
}

/// <summary>Reads agentsettings.json. Reports user-fixable problems as messages and never includes the shared key.</summary>
public static class AgentSettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Raw
    {
        public string? ServerUrl { get; set; }

        public string? SharedKey { get; set; }

        public string? MachineId { get; set; }

        public string? EmergencyUnlockCodeHash { get; set; }
    }

    public static AgentSettingsLoadResult Load(string path, string machineNameFallback)
    {
        string json;
        try
        {
            if (!File.Exists(path))
            {
                return Fail($"Settings file not found: {path}");
            }

            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not read the settings file: {ex.Message}");
        }

        Raw? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Raw>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return Fail("The settings file is not valid JSON.");
        }

        if (raw is null)
        {
            return Fail("The settings file is empty.");
        }

        var errors = new List<string>();
        var url = raw.ServerUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("ServerUrl must be an absolute http or https URL, for example http://192.168.1.10:5080.");
        }

        var key = raw.SharedKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            errors.Add("SharedKey is missing. Copy it from the server's settings page.");
        }

        if (errors.Count > 0)
        {
            return new AgentSettingsLoadResult(null, errors);
        }

        var machineId = string.IsNullOrWhiteSpace(raw.MachineId) ? machineNameFallback : raw.MachineId.Trim();
        return new AgentSettingsLoadResult(
            new AgentSettings(url, key, machineId, raw.EmergencyUnlockCodeHash?.Trim() ?? string.Empty), []);
    }

    private static AgentSettingsLoadResult Fail(string message) => new(null, [message]);
}
