namespace Cafe.Agent.Core;

public abstract record AgentCommand
{
    public sealed record Run : AgentCommand;

    public sealed record HashCode(string Value) : AgentCommand;

    public sealed record Help : AgentCommand;

    public sealed record Error(string Message) : AgentCommand;
}

/// <summary>Parses the agent command line. Never throws.</summary>
public static class AgentCommandLine
{
    public const int MaxCodeLength = 200;

    public const string Usage =
        """
        Usage: Cafe.Agent.exe [--hash-code <code>] [--help]

          (no arguments)       Run the agent (lock screen and server connection)
          --hash-code <code>   Print the hash to paste into agentsettings.json as EmergencyUnlockCodeHash, then exit
          --help               Show this help
        """;

    public static AgentCommand Parse(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return new AgentCommand.Run();
        }

        switch (args[0])
        {
            case "--help" or "-h" or "-?":
                return new AgentCommand.Help();
            case "--hash-code":
                if (args.Length < 2)
                {
                    return new AgentCommand.Error("Missing value for --hash-code.");
                }

                if (args.Length > 2)
                {
                    return new AgentCommand.Error("Unexpected extra arguments after --hash-code <code>.");
                }

                var value = args[1];
                if (string.IsNullOrWhiteSpace(value))
                {
                    return new AgentCommand.Error("The code must not be blank.");
                }

                if (value.Length > MaxCodeLength)
                {
                    return new AgentCommand.Error($"The code must be at most {MaxCodeLength} characters.");
                }

                return new AgentCommand.HashCode(value);
            default:
                return new AgentCommand.Error($"Unknown argument: {args[0]}.");
        }
    }
}
