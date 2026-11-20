using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cafe.Server.Devices;

/// <summary>
/// Powers a console's TV off when its paid time and grace are used up. Idempotent: a TV that is already off is left
/// alone (KEY_POWER toggles). An unreachable TV counts as already off (there is nothing to power off; a TV with no
/// network standby or switched off at the wall looks exactly like this) and raises one rate-limited desk notice so
/// staff can check it. Throws when the TV could not be switched off (Failed), so the expiry evaluator retries it
/// (bounded by <see cref="ExpiryEvaluator.MaxActionAttempts"/>). Never acts on a station that has already started a
/// new session, so a retried expiry cannot cut a new customer off.
/// </summary>
public sealed class TvExpiryActionHandler(
    TvControlService control,
    IDbContextFactory<CafeDbContext> dbFactory,
    IOptions<TvOptions> options,
    ILogger<TvExpiryActionHandler> logger) : IExpiryActionHandler
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

        // Stay inside the evaluator's handler timeout: a TV that is still busy when the budget runs out is a failed,
        // retryable attempt (the retry sees Standby and ends as AlreadyOff) rather than an opaque evaluator timeout.
        var budget = TimeSpan.FromSeconds(Math.Max(1, options.Value.ExpiryActionTimeoutSeconds));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(budget);
        TvActionResult result;
        try
        {
            result = await control.ExecuteAsync(station, on: false, "Expiry", limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The TV of {station.Name} did not finish powering off within {budget.TotalSeconds:0.#} s; it will be retried.");
        }

        if (result.Outcome == TvActionOutcome.Failed)
        {
            throw new InvalidOperationException($"The TV of {station.Name} was not powered off: {result.Message}.");
        }

        logger.LogInformation("TV of station {Station}: {Outcome}.", station.Name, result.Outcome);
    }
}
