using System.IO;
using System.Runtime.InteropServices;
using Cafe.Agent.Core;
using Microsoft.Extensions.Logging;

namespace Cafe.Agent;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        switch (AgentCommandLine.Parse(args))
        {
            case AgentCommand.HashCode hash:
                AttachConsole(AttachParentProcess);
                Console.WriteLine(EmergencyCodeHasher.Hash(hash.Value));
                return 0;
            case AgentCommand.Help:
                AttachConsole(AttachParentProcess);
                Console.WriteLine(AgentCommandLine.Usage);
                return 0;
            case AgentCommand.Error error:
                AttachConsole(AttachParentProcess);
                Console.Error.WriteLine(error.Message);
                Console.Error.WriteLine(AgentCommandLine.Usage);
                return 2;
        }

        using var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
        {
            // Fail secure: the mutex alone proves nothing (any process can pre-create it, or the owner may be hung).
            // Exit only when the running agent answers a ping within 2 s; otherwise take over and show our own lock screen.
            using var provider = new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs"));
            var logger = provider.CreateLogger("Program");
            if (SingleInstance.PingExisting(TimeSpan.FromSeconds(2)))
            {
                logger.LogWarning("Another agent instance is already running and responsive; exiting.");
                return 3;
            }

            logger.LogWarning("The single-instance mutex is taken but no agent answered; taking over.");
        }

        var app = new AgentApplication();
        if (instance.IsFirst)
        {
            instance.StartResponder(() =>
            {
                // Healthy only when the UI thread (which owns the lock screen) still processes work.
                try
                {
                    app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(1));
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            });
        }

        return app.Run();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
