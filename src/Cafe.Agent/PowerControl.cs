using System.Diagnostics;
using System.IO;
using Cafe.Agent.Core;
using Microsoft.Extensions.Logging;

namespace Cafe.Agent;

/// <summary>Executes remote power commands through System32\shutdown.exe (full path, so a planted copy in the app folder is never run).</summary>
internal sealed class PowerControl(ILogger<PowerControl> logger) : IPowerControl
{
    public void Shutdown() => Run("/s /t 0", "shutdown");

    public void Restart() => Run("/r /t 0", "restart");

    private void Run(string arguments, string what)
    {
        try
        {
            logger.LogInformation("Executing remote {Command}.", what);
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            logger.LogError("Could not execute {Command}: {Reason}", what, ex.Message);
        }
    }
}
