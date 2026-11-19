using Cafe.Server.Data;

namespace Cafe.Server.Sessions;

/// <summary>
/// Seam for what happens to a station when its paid time (and grace) is used up. Phase 3 (PC lock) and Phase 4
/// (TV power off) register more implementations; each decides by station kind whether it applies.
/// </summary>
public interface IExpiryActionHandler
{
    Task ExecuteAsync(Station station, CancellationToken cancellationToken);
}
