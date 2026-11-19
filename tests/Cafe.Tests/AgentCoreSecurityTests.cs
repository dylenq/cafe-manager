using Cafe.Agent.Core;

namespace Cafe.Tests;

public sealed class AgentCoreSecurityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentsettings").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string content)
    {
        var path = Path.Combine(_dir, "agentsettings.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Hash_is_self_describing_verifies_and_uses_a_random_salt()
    {
        var hash = EmergencyCodeHasher.Hash("1234-secret");

        Assert.StartsWith("pbkdf2-sha256$200000$", hash);
        Assert.Equal(4, hash.Split('$').Length);
        Assert.True(EmergencyCodeHasher.Verify("1234-secret", hash));
        Assert.False(EmergencyCodeHasher.Verify("1234-secreT", hash));
        Assert.False(EmergencyCodeHasher.Verify("", hash));
        Assert.False(EmergencyCodeHasher.Verify(null, hash));
        Assert.NotEqual(hash, EmergencyCodeHasher.Hash("1234-secret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$200000$!!!$!!!")]
    [InlineData("pbkdf2-sha256$10$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$999999999$AAAA$AAAA")]
    [InlineData("md5$200000$AAAA$AAAA")]
    public void Malformed_or_empty_stored_hash_never_verifies(string? stored)
    {
        Assert.False(EmergencyCodeHasher.Verify("anything", stored));
    }

    [Fact]
    public void Loader_reads_case_insensitive_json_and_defaults_the_machine_id()
    {
        var path = Write("""
            {
              // comment
              "serverurl": "http://192.168.1.10:5080",
              "SHAREDKEY": " abc123 ",
              "EmergencyUnlockCodeHash": "x",
            }
            """);

        var result = AgentSettingsLoader.Load(path, "DESKTOP-9");

        Assert.True(result.Success);
        Assert.Equal("http://192.168.1.10:5080", result.Settings!.ServerUrl);
        Assert.Equal("abc123", result.Settings.SharedKey);
        Assert.Equal("DESKTOP-9", result.Settings.MachineId);
        Assert.Equal("x", result.Settings.EmergencyUnlockCodeHash);
    }

    [Fact]
    public void Loader_keeps_an_explicit_machine_id()
    {
        var path = Write("""{ "ServerUrl": "https://cafe.lan", "SharedKey": "k", "MachineId": "PC-07" }""");

        Assert.Equal("PC-07", AgentSettingsLoader.Load(path, "fallback").Settings!.MachineId);
    }

    [Fact]
    public void Loader_reports_errors_instead_of_throwing()
    {
        var missing = AgentSettingsLoader.Load(Path.Combine(_dir, "nope.json"), "m");
        Assert.False(missing.Success);
        Assert.NotEmpty(missing.Errors);

        var malformed = AgentSettingsLoader.Load(Write("{ not json"), "m");
        Assert.False(malformed.Success);

        var noKey = AgentSettingsLoader.Load(Write("""{ "ServerUrl": "http://x:5080", "SharedKey": " " }"""), "m");
        Assert.False(noKey.Success);
        Assert.Contains(noKey.Errors, e => e.Contains("SharedKey"));

        foreach (var url in new[] { "", "not a url", "ftp://host", "/relative" })
        {
            var bad = AgentSettingsLoader.Load(Write($$"""{ "ServerUrl": "{{url}}", "SharedKey": "super-secret-key" }"""), "m");
            Assert.False(bad.Success);
            Assert.DoesNotContain(bad.Errors, e => e.Contains("super-secret-key"));
        }
    }
}
