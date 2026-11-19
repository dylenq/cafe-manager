using System.Text.RegularExpressions;
using Cafe.AgentSim;
using Cafe.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests;

public class AgentSimTests : AgentTestBase
{
    private sealed class LockedWriter : StringWriter
    {
        private readonly object _gate = new();

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                base.WriteLine(value);
            }
        }

        public override void Flush()
        {
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                return ToString();
            }
        }
    }

    private static string? Env(string name) => null;

    [Fact]
    public void Parse_reads_switches_and_applies_defaults()
    {
        var full = SimOptions.Parse(["--server", "http://10.0.0.5:5080", "--key", "k1", "--count", "12", "--prefix", "T-", "--no-color"], Env);
        Assert.Null(full.Error);
        Assert.Equal(new SimOptions("http://10.0.0.5:5080", "k1", 12, "T-", true), full.Options);

        var defaults = SimOptions.Parse(["--key", "k"], Env);
        Assert.Equal("http://localhost:5080", defaults.Options!.Server);
        Assert.Equal(1, defaults.Options.Count);
        Assert.Equal("SIM-", defaults.Options.Prefix);
        Assert.False(defaults.Options.NoColor);

        var fromEnv = SimOptions.Parse([], n => n == "CAFE_AGENT_KEY" ? "envkey" : null);
        Assert.Equal("envkey", fromEnv.Options!.Key);

        Assert.True(SimOptions.Parse(["--help"], Env).ShowHelp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--key|k|--count|0")]
    [InlineData("--key|k|--count|201")]
    [InlineData("--key|k|--count|abc")]
    [InlineData("--key|k|--bogus")]
    [InlineData("--key")]
    [InlineData("--key|k|--server|not a url")]
    public void Parse_returns_an_error_instead_of_throwing(string joined)
    {
        var args = joined.Length == 0 ? [] : joined.Split('|');
        var result = SimOptions.Parse(args, Env);

        Assert.Null(result.Options);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Contains("--count", SimOptions.Usage);
    }

    [Fact]
    public void Machine_ids_are_prefix_plus_zero_padded_index()
    {
        var ten = new SimOptions("http://x", "k", 10, "SIM-", false);
        Assert.Equal("SIM-01", ten.MachineId(1));
        Assert.Equal("SIM-10", ten.MachineId(10));

        var hundred = ten with { Count = 100 };
        Assert.Equal("SIM-001", hundred.MachineId(1));
        Assert.Equal("SIM-100", hundred.MachineId(100));
    }

    [Fact]
    public async Task Fleet_shows_locked_then_unlocked_then_locked_for_a_started_and_stopped_session()
    {
        await PcStationAsync("SIM-01", "SIM-01");
        var second = await PcStationAsync("SIM-02", "SIM-02");
        await PcStationAsync("SIM-03", "SIM-03");
        var options = new SimOptions("http://localhost", await KeyAsync(), 3, "SIM-", true);
        var output = new LockedWriter();
        using var cts = new CancellationTokenSource();

        var run = SimFleet.RunAsync(options,
            o =>
            {
                o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            },
            output, cts.Token, NullLoggerFactory.Instance);

        await WaitUntilAsync(() => output.Snapshot().Contains("[SIM-01] LOCKED") && output.Snapshot().Contains("[SIM-03] LOCKED"), "initial locks");

        var session = await PrepaidAsync(second, 60);
        await WaitUntilAsync(() => output.Snapshot().Contains("[SIM-02] UNLOCKED 60:00 remaining"), "unlock line");

        var stopped = await SvcAsync(s => s.StopAsync(session.Id));
        Assert.True(stopped.Success, stopped.Error);
        await WaitUntilAsync(() => Regex.Matches(output.Snapshot(), @"\[SIM-02\] LOCKED").Count >= 2, "second lock");

        await cts.CancelAsync();
        await run;

        var lines = output.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.All(lines, l => Assert.Matches(@"^\[SIM-0[1-3]\] \S", l));
        Assert.DoesNotContain('\u001b', output.Snapshot());
    }

    [Fact]
    public async Task Log_lines_from_many_threads_never_interleave_and_color_is_optional()
    {
        var colored = new LockedWriter();
        var plain = new LockedWriter();
        var coloredLog = new SimLog(colored, true);
        var plainLog = new SimLog(plain, false);

        await Task.WhenAll(Enumerable.Range(1, 20).Select(agent => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                coloredLog.Write($"SIM-{agent:00}", "UNLOCKED 60:00 remaining", SimColor.Green);
                plainLog.Write($"SIM-{agent:00}", "UNLOCKED 60:00 remaining", SimColor.Green);
            }
        })));

        var plainLines = plain.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(4000, plainLines.Length);
        Assert.All(plainLines, l => Assert.Matches(@"^\[SIM-\d\d\] UNLOCKED 60:00 remaining$", l));
        Assert.DoesNotContain('\u001b', plain.Snapshot());

        var coloredLines = colored.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(4000, coloredLines.Length);
        Assert.All(coloredLines, l => Assert.Matches(@"^\u001b\[32m\[SIM-\d\d\] UNLOCKED 60:00 remaining\u001b\[0m$", l));
    }
}
