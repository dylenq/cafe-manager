using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Cafe.Server.Devices;

/// <summary>
/// Runs automatic TV actions in the background so a session operation never waits for a TV. Latest wins per station: an
/// action queued earlier is dropped when a newer one for the same station was queued before it started. Actions are
/// serialised per station, time boxed and cancelled on shutdown; exceptions are logged and swallowed.
/// </summary>
public sealed class TvActionQueue(ILogger<TvActionQueue> logger, IHostApplicationLifetime lifetime, IOptions<TvOptions> options)
{
    private readonly ConcurrentDictionary<int, long> _latest = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private long _sequence;
    private long _taskId;

    public void Enqueue(int stationId, Func<CancellationToken, Task> action)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        _latest.AddOrUpdate(stationId, sequence, (_, current) => Math.Max(current, sequence)); // never move back to an older action
        var id = Interlocked.Increment(ref _taskId);
        var task = Task.Run(() => RunAsync(stationId, sequence, action));
        _running[id] = task;
        _ = task.ContinueWith(_ => _running.TryRemove(id, out Task? _), TaskScheduler.Default);
    }

    /// <summary>Completes when every tracked action is done, including ones added while waiting.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            var pending = _running.Values.ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending);
            await Task.Yield();
        }
    }

    private async Task RunAsync(int stationId, long sequence, Func<CancellationToken, Task> action)
    {
        var gate = _locks.GetOrAdd(stationId, _ => new SemaphoreSlim(1, 1));
        try
        {
            // The per-station gate is only held for at most one action timeout, so waiting for it is bounded.
            await gate.WaitAsync(lifetime.ApplicationStopping);
            try
            {
                if (_latest.TryGetValue(stationId, out var latest) && latest != sequence)
                {
                    return; // A newer action for this station exists; this one is stale.
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.ActionTimeoutSeconds)));
                await action(cts.Token); // TvControlService abandons a driver that ignores cancellation
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("A TV action for station {StationId} was cancelled or timed out.", stationId);
        }
        catch (Exception ex)
        {
            logger.LogWarning("A TV action for station {StationId} failed: {Error}", stationId, ex.Message);
        }
    }
}
