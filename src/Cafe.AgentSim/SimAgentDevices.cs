using Cafe.Agent.Core;
using Cafe.Shared.Agents;

namespace Cafe.AgentSim;

public enum SimColor
{
    None,
    Red,
    Green,
    Yellow,
    Cyan,
    Gray,
}

/// <summary>One thread-safe writer so lines from many agents never interleave. Shape: "[SIM-03] UNLOCKED 60:00 remaining".</summary>
public sealed class SimLog(TextWriter output, bool color)
{
    private readonly object _gate = new();

    public void Write(string machineId, string text, SimColor tint = SimColor.None)
    {
        var line = $"[{machineId}] {text}";
        if (color && tint != SimColor.None)
        {
            line = $"\u001b[{Code(tint)}m{line}\u001b[0m";
        }

        lock (_gate)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }

    private static string Code(SimColor tint) => tint switch
    {
        SimColor.Red => "31",
        SimColor.Green => "32",
        SimColor.Yellow => "33",
        SimColor.Cyan => "36",
        _ => "90",
    };
}

/// <summary>Console stand-ins for the lock screen, toast and power control of one simulated PC.</summary>
public sealed class SimAgentDevices(string machineId, SimLog log) : ILockScreen, IWarningDisplay, IPowerControl
{
    private bool _countdownShown;

    public void Attach(AgentController controller)
    {
        controller.StateChanged += state => OnStateChanged(controller, state);
        controller.ConnectedChanged += connected =>
            log.Write(machineId, connected ? "CONNECTED" : "DISCONNECTED", SimColor.Gray);
    }

    private void OnStateChanged(AgentController controller, AgentState state)
    {
        switch (state)
        {
            case AgentState.Unlocked:
                if (controller.RemainingSeconds is { } remaining)
                {
                    log.Write(machineId, $"UNLOCKED {Format(remaining)} remaining", SimColor.Green);
                }
                else if (controller.IsEmergencyUnlocked)
                {
                    log.Write(machineId, "UNLOCKED emergency", SimColor.Green);
                }
                else
                {
                    log.Write(machineId, $"UNLOCKED open-ended {FormatElapsed(controller.ElapsedSeconds)} elapsed", SimColor.Green);
                }

                break;
            case AgentState.Grace:
                log.Write(machineId, $"GRACE {controller.GraceRemainingSeconds}s", SimColor.Yellow);
                break;
        }
    }

    // ILockScreen
    public void Show(LockInfo info)
    {
        var reason = string.IsNullOrWhiteSpace(info.Reason) ? string.Empty : $" {info.Reason}";
        log.Write(machineId, "LOCKED" + reason, SimColor.Red);
    }

    void ILockScreen.Hide()
    {
        // The UNLOCKED line is written from the state change.
    }

    // IWarningDisplay
    public void ShowCountdown(AgentCountdown countdown)
    {
        if (_countdownShown || countdown.IsGrace)
        {
            return;
        }

        _countdownShown = true;
        log.Write(machineId, $"COUNTDOWN {Format(countdown.RemainingSeconds)}", SimColor.Yellow);
    }

    public void ShowMessage(string text)
    {
        var warning = text.Contains("remaining", StringComparison.OrdinalIgnoreCase);
        log.Write(machineId, (warning ? "WARNING " : "MESSAGE ") + text, warning ? SimColor.Yellow : SimColor.Cyan);
    }

    void IWarningDisplay.Hide() => _countdownShown = false;

    // IPowerControl (the simulator never powers anything off)
    public void Shutdown() => log.Write(machineId, "SHUTDOWN requested (ignored)", SimColor.Cyan);

    public void Restart() => log.Write(machineId, "RESTART requested (ignored)", SimColor.Cyan);

    /// <summary>Countdown as total minutes and seconds, for example 60:00.</summary>
    public static string Format(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    /// <summary>Elapsed time as h:mm:ss.</summary>
    public static string FormatElapsed(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
