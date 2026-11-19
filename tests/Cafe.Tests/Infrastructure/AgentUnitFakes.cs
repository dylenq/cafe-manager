using Cafe.Agent.Core;
using Cafe.Shared.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests.Infrastructure;

public sealed class FakeAgentClock : IAgentClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class RecordingLockScreen : ILockScreen
{
    public int ShowCount;
    public int HideCount;
    public LockInfo? LastLock;

    public void Show(LockInfo info)
    {
        ShowCount++;
        LastLock = info;
    }

    public void Hide() => HideCount++;
}

public sealed class RecordingWarningDisplay : IWarningDisplay
{
    public List<string> Messages { get; } = [];

    public List<AgentCountdown> Countdowns { get; } = [];

    public int HideCount;

    public void ShowCountdown(AgentCountdown countdown) => Countdowns.Add(countdown);

    public void ShowMessage(string text) => Messages.Add(text);

    public void Hide() => HideCount++;
}

public sealed class RecordingPowerControl : IPowerControl
{
    public int ShutdownCount;
    public int RestartCount;

    public void Shutdown() => ShutdownCount++;

    public void Restart() => RestartCount++;
}

/// <summary>A controller wired to recording fakes and a fake clock; no network is involved until StartAsync is called.</summary>
public sealed class UnitAgent
{
    public FakeAgentClock Clock { get; } = new();

    public RecordingLockScreen Lock { get; } = new();

    public RecordingWarningDisplay Warnings { get; } = new();

    public RecordingPowerControl Power { get; } = new();

    public AgentController Controller { get; }

    public int StateChanges;

    public UnitAgent(string serverUrl = "http://127.0.0.1:1")
    {
        Controller = new AgentController(
            new AgentConnectionOptions
            {
                ServerUrl = serverUrl, SharedKey = "k", MachineId = "PC-01",
                ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
            },
            Lock, Warnings, Power, Clock, NullLogger<AgentController>.Instance);
        Controller.StateChanged += _ => StateChanges++;
    }

    public SessionInfo Prepaid(long remaining, TimeSpan? clockAhead = null) =>
        new(AgentSessionMode.Prepaid, remaining, 0, "PC-01", Clock.UtcNow - (clockAhead ?? TimeSpan.Zero));

    public SessionInfo OpenEnded(long elapsed) =>
        new(AgentSessionMode.OpenEnded, null, elapsed, "PC-01", Clock.UtcNow);
}
