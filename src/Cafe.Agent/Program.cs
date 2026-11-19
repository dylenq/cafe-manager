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
            // Another agent is already running on this PC.
            using var provider = new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs"));
            provider.CreateLogger("Program").LogWarning("Another agent instance is already running; exiting.");
            return 3;
        }

        var app = new AgentApplication();
        return app.Run();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
