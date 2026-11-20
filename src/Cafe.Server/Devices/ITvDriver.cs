using Cafe.Server.Data;

namespace Cafe.Server.Devices;

/// <summary>Brand-specific TV control. Only the Samsung Tizen driver exists; this interface is the extension point.</summary>
public interface ITvDriver
{
    Task<TvPairResult> PairAsync(Station station, CancellationToken cancellationToken);

    Task<TvActionResult> PowerOffAsync(Station station, CancellationToken cancellationToken);

    Task<TvActionResult> PowerOnAsync(Station station, CancellationToken cancellationToken);

    Task<TvPowerState> GetStateAsync(Station station, CancellationToken cancellationToken);
}
