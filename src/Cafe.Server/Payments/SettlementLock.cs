namespace Cafe.Server.Payments;

/// <summary>
/// Serialises settle and void so two circuits can never pay or void the same order twice. The server is a single
/// process, so one semaphore is enough (same shape as BalanceLock).
/// </summary>
public sealed class SettlementLock
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
