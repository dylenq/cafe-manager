namespace Cafe.Server.Customers;

/// <summary>
/// Serialises every balance-mutating operation (package sale, balance debit, manual adjustment). The server is a
/// single process, so one semaphore is enough to stop two circuits from overspending the same balance.
/// </summary>
public sealed class BalanceLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
