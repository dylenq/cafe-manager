namespace Cafe.Shared.Agents;

/// <summary>
/// Server-to-agent calls. <reversibility rating="costly">Deployed agents depend on these method names and
/// shapes, so changing them later means redeploying every PC.</reversibility>
/// </summary>
public interface IAgentClient
{
    Task Lock(LockInfo info);

    Task Unlock(SessionInfo info);

    Task SessionUpdated(SessionInfo info);

    Task ShowWarning(int minutesRemaining);

    Task GraceStarted(int graceSeconds);

    Task ShowMessage(string text);

    Task Shutdown();

    Task Restart();
}
