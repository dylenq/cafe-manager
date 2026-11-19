namespace Cafe.Server.Dashboard;

/// <summary>
/// Orders overlapping asynchronous fetches: take a ticket before fetching, and apply the result only if no newer
/// ticket has been applied already. Stops a slow, older response from overwriting a newer one.
/// </summary>
public sealed class LatestWinsGate
{
    private long _issued;
    private long _applied;

    public long Begin() => Interlocked.Increment(ref _issued);

    /// <summary>True when this ticket is newer than everything applied so far (and records it).</summary>
    public bool TryApply(long ticket)
    {
        lock (this)
        {
            if (ticket <= _applied)
            {
                return false;
            }

            _applied = ticket;
            return true;
        }
    }
}
