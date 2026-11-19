using Cafe.AgentSim;

var parsed = SimOptions.Parse(args, Environment.GetEnvironmentVariable);
if (parsed.ShowHelp)
{
    Console.WriteLine(SimOptions.Usage);
    return 0;
}

if (parsed.Options is null)
{
    Console.Error.WriteLine(parsed.Error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(SimOptions.Usage);
    return 2;
}

var options = parsed.Options with { NoColor = parsed.Options.NoColor || Console.IsOutputRedirected };
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Simulating {options.Count} agent(s) against {options.Server}. Press Ctrl+C to stop.");
await SimFleet.RunAsync(options, null, Console.Out, cts.Token);
return 0;
