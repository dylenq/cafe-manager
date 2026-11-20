using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Devices;

/// <summary>Pairs a console station's TV and stores the token on the station. The token is never logged or returned.</summary>
public sealed class TvPairingService(IDbContextFactory<CafeDbContext> dbFactory, ITvDriver driver, ILogger<TvPairingService> logger)
{
    public async Task<ServiceResult> PairAsync(int stationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == stationId, cancellationToken);
        if (station is null)
        {
            return ServiceResult.NotFound("That station no longer exists.");
        }

        if (station.Kind != StationKind.Console)
        {
            return ServiceResult.Fail("Only console stations have a TV.");
        }

        if (string.IsNullOrWhiteSpace(station.TvIpAddress))
        {
            return ServiceResult.Fail("This station has no TV IP address. Enter one on the station first.");
        }

        var result = await driver.PairAsync(station, cancellationToken);
        if (!result.Success || string.IsNullOrEmpty(result.Token))
        {
            logger.LogInformation("Pairing the TV of station {Station} failed.", station.Name);
            return ServiceResult.Fail(result.Error ?? "Pairing failed.");
        }

        station.TvToken = result.Token;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Paired the TV of station {Station}.", station.Name);
        return ServiceResult.Ok();
    }
}
