using Cafe.Shared.Agents;

namespace Cafe.Agent.Core;

public interface ILockScreen
{
    void Show(LockInfo info);

    void Hide();
}

public record AgentCountdown(long RemainingSeconds, bool IsGrace, bool IsOpenEnded);

public interface IWarningDisplay
{
    void ShowCountdown(AgentCountdown countdown);

    void ShowMessage(string text);

    void Hide();
}

public interface IPowerControl
{
    void Shutdown();

    void Restart();
}

public interface IAgentClock
{
    /// <summary>Wall clock. Only for display and clock-skew estimates: the user can change it.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Monotonic milliseconds from an arbitrary origin. Never goes backwards when the PC date or time is changed, so
    /// every offline countdown is measured with it.
    /// </summary>
    long MonotonicMilliseconds { get; }
}
