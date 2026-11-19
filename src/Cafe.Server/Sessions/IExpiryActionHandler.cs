using Cafe.Server.Data;

namespace Cafe.Server.Sessions;

/// <summary>
/// Seam for what happens to a station when its paid time (and grace) is used up. Phase 3 (PC lock) and Phase 4
/// (TV power off) register more implementations; each decides by station kind whether it applies.
/// <para>
/// Delivery is at-least-once, not exactly-once: a handler can run again for the same expiry after a crash, a
/// restart or when another handler failed. Implementations MUST be idempotent (locking an already locked PC or
/// powering off an already powered-off TV must be harmless) and should throw when the action did not happen so
/// it is retried (up to <see cref="ExpiryEvaluator.MaxActionAttempts"/> attempts).
/// </para>
/// </summary>
public interface IExpiryActionHandler
{
    Task ExecuteAsync(Station station, CancellationToken cancellationToken);
}
