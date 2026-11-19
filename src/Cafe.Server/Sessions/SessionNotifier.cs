namespace Cafe.Server.Sessions;

/// <summary>In-process change signal: raised after every committed session mutation so the dashboard refreshes at once.</summary>
public class SessionNotifier(ILogger<SessionNotifier>? logger = null)
{
    public event Action<int>? Changed;

    public void Raise(int stationId)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<int>>())
        {
            try
            {
                handler(stationId);
            }
            catch (Exception ex)
            {
                // A faulty subscriber (e.g. a disposed circuit) must never break a session mutation.
                logger?.LogWarning(ex, "A session change subscriber threw.");
            }
        }
    }
}
