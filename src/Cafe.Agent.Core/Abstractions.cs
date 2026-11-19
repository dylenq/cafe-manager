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
    DateTimeOffset UtcNow { get; }
}
