using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Devices;

/// <summary>
/// Powers a console's TV off when its paid time and grace are used up. Idempotent: a TV that is already off is left
/// alone (KEY_POWER toggles). Throws when the TV could not be switched off, so the expiry evaluator retries it
/// (bounded by <see cref="ExpiryEvaluator.MaxActionAttempts"/>). Never acts on a station that has already started a
/// new session, so a retried expiry cannot cut a new customer off.
/// </summary>
public sealed class TvExpiryActionHandler(ITvDriver driver, IDbContextFactory<CafeDbContext> dbFactory, ILogger<TvExpiryActionHandler> logger) : IExpiryActionHandler
{
    public async Task ExecuteAsync(Station station, CancellationToken cancellationToken)
    {
        if (station.Kind != StationKind.Console || string.IsNullOrWhiteSpace(station.TvIpAddress))
        {
            return;
        }

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.Sessions.AnyAsync(s => s.StationId == station.Id && s.Status != SessionStatus.Ended, cancellationToken))
            {
                logger.LogInformation("Station {Station} already runs a new session; its TV is not powered off.", station.Name);
                return;
            }
        }

        var result = await driver.PowerOffAsync(station, cancellationToken);
        if (result.Outcome is TvActionOutcome.Failed or TvActionOutcome.Unreachable)
        {
            throw new InvalidOperationException($"The TV of {station.Name} was not powered off: {result.Message}.");
        }

        logger.LogInformation("TV of station {Station}: {Outcome}.", station.Name, result.Outcome);
    }
}
