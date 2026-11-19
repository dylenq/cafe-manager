using Cafe.Agent.Core;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Logging;

namespace Cafe.AgentSim;

public static class SimFleet
{
    /// <summary>Runs N simulated agents until the token is cancelled, then disposes them all.</summary>
    public static async Task RunAsync(
        SimOptions options,
        Action<HttpConnectionOptions>? configureHttp,
        TextWriter output,
        CancellationToken cancellationToken,
        ILoggerFactory? loggerFactory = null)
    {
        var ownsFactory = loggerFactory is null;
        loggerFactory ??= LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var log = new SimLog(output, !options.NoColor);
        var clock = new SystemAgentClock();
        var agents = new List<AgentController>();

        try
        {
            for (var i = 1; i <= options.Count; i++)
            {
                var id = options.MachineId(i);
                var devices = new SimAgentDevices(id, log);
                var controller = new AgentController(
                    new AgentConnectionOptions
                    {
                        ServerUrl = options.Server,
                        SharedKey = options.Key,
                        MachineId = id,
                        AgentVersion = "sim",
                        ConfigureHttp = configureHttp,
                    },
                    devices, devices, devices, clock, loggerFactory.CreateLogger<AgentController>());
                devices.Attach(controller);
                agents.Add(controller);

                await controller.StartAsync(cancellationToken);
                if (i < options.Count)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }

            if (ownsFactory)
            {
                loggerFactory.Dispose();
            }
        }
    }
}
