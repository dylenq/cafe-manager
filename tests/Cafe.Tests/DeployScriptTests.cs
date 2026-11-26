namespace Cafe.Tests;

// Static checks on the Windows install scripts, which cannot run on the macOS dev machine.
public class DeployScriptTests
{
    private static string DeployDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Cafe.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!, "deploy", "windows");
    }

    public static TheoryData<string> Scripts() => new()
    {
        "install-server.ps1", "uninstall-server.ps1", "install-agent.ps1", "uninstall-agent.ps1",
    };

    // Windows PowerShell 5.1 leaves $PSScriptRoot empty in param() defaults when started with -File (found on a real
    // PC: Join-Path failed before the script's error handling ran), so the scripts resolve their path from
    // $MyInvocation after the param block instead.
    [Theory]
    [MemberData(nameof(Scripts))]
    public void Param_block_does_not_use_script_path_variables(string script)
    {
        var text = File.ReadAllText(Path.Combine(DeployDir(), script));
        var start = text.IndexOf("\nparam(", StringComparison.Ordinal);
        Assert.True(start >= 0, "param( block not found");
        var end = text.IndexOf("\n)", start, StringComparison.Ordinal);
        Assert.True(end > start, "end of param block not found");
        var paramBlock = text[start..end];

        Assert.DoesNotContain("$PSScriptRoot", paramBlock);
        Assert.DoesNotContain("$PSCommandPath", paramBlock);
        Assert.DoesNotContain("$MyInvocation", paramBlock);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Elevation_relaunches_with_the_resolved_script_path(string script)
    {
        var text = File.ReadAllText(Path.Combine(DeployDir(), script));
        Assert.Contains("$ScriptPath = $MyInvocation.MyCommand.Path", text);
        Assert.Contains("(Quote-NativeArg $ScriptPath)", text);
        Assert.DoesNotContain("$PSScriptRoot", text.Replace("$PSScriptRoot and $PSCommandPath can be empty", string.Empty));
    }

    [Theory]
    [InlineData("Install Cafe Manager Server.cmd")]
    [InlineData("Install on this Gaming PC.cmd")]
    [InlineData("Uninstall Cafe Manager Server.cmd")]
    [InlineData("Uninstall from this Gaming PC.cmd")]
    public void Launcher_has_crlf_endings_and_always_pauses(string launcher)
    {
        var text = File.ReadAllText(Path.Combine(DeployDir(), launcher));
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
        Assert.Contains("-FromLauncher\r\n", text);
        Assert.EndsWith("pause\r\nexit /b %CAFE_EXIT%\r\n", text);
    }
}
