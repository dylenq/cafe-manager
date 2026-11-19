using System.Globalization;
using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Sessions;

/// <summary>All session mutations. Every mutation writes a SessionEvent in the same transaction and raises the notifier.</summary>
public class SessionService(
    IDbContextFactory<CafeDbContext> dbFactory,
    TimeProvider time,
    PricingConfigService pricing,
    SessionNotifier notifier)
{
    public const int MinMinutes = 1;
    public const int MaxMinutes = 1440;

    private const string Busy = "That station already has a session. Refresh the dashboard.";
    private const string Stale = "This session was changed by someone else. Refresh and try again.";

    public async Task<ServiceResult<Session>> StartPrepaidAsync(int stationId, int minutes, CancellationToken cancellationToken = default)
    {
        if (minutes is < MinMinutes or > MaxMinutes)
        {
            return ServiceResult<Session>.Fail($"Minutes must be between {MinMinutes} and {MaxMinutes}.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var station = await db.Stations.Include(s => s.StationType).FirstOrDefaultAsync(s => s.Id == stationId, cancellationToken);
        if (station is null)
        {
            return ServiceResult<Session>.NotFound("That station no longer exists. Refresh the dashboard.");
        }

        if (!station.IsEnabled)
        {
            return ServiceResult<Session>.Fail($"{station.Name} is disabled.");
        }

        if (await db.Sessions.AnyAsync(s => s.StationId == stationId && s.Status != SessionStatus.Ended, cancellationToken))
        {
            return ServiceResult<Session>.Fail(Busy);
        }

        var now = time.GetUtcNow();
        var rules = await pricing.GetEnabledRulesAsync(db, cancellationToken);
        var quote = PricingEngine.Price(new PricingRequest(
            [new PricedInterval(now, now.AddMinutes(minutes), station.StationTypeId, station.StationType.HourlyRate)],
            rules,
            pricing.TimeZone)).Total;

        var session = new Session
        {
            StationId = stationId,
            Mode = SessionMode.Prepaid,
            Status = SessionStatus.Active,
            StartedAtUtc = now,
            PurchasedMinutes = minutes,
            ChargedAmount = quote,
        };
        session.Segments.Add(new SessionSegment { StationId = stationId, StartUtc = now });
        session.Events.Add(new SessionEvent
        {
            StationId = stationId,
            Type = SessionEventType.Started,
            AtUtc = now,
            Details = string.Create(CultureInfo.InvariantCulture, $"Prepaid {minutes} min, charge {quote:0.00}"),
        });
        db.Sessions.Add(session);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return ServiceResult<Session>.Fail(Busy);
        }

        notifier.Raise(stationId);
        return ServiceResult<Session>.Ok(session);
    }

    /// <summary>Ends the session and returns its final charge. Prepaid charges stay as quoted (no automatic refund).</summary>
    public async Task<ServiceResult<decimal>> StopAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.Include(s => s.Segments).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return ServiceResult<decimal>.NotFound("That session no longer exists. Refresh the dashboard.");
        }

        if (session.Status == SessionStatus.Ended)
        {
            return ServiceResult<decimal>.Fail("That session has already ended.");
        }

        var now = time.GetUtcNow();
        foreach (var segment in session.Segments.Where(x => x.EndUtc is null))
        {
            segment.EndUtc = now;
        }

        session.Status = SessionStatus.Ended;
        session.EndReason = SessionEndReason.Stopped;
        session.EndedAtUtc = now;
        session.Version++;
        session.Events.Add(new SessionEvent
        {
            SessionId = session.Id,
            StationId = session.StationId,
            Type = SessionEventType.Stopped,
            AtUtc = now,
            Details = string.Create(CultureInfo.InvariantCulture, $"Charge {session.ChargedAmount:0.00}"),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<decimal>.Fail(Stale);
        }

        notifier.Raise(session.StationId);
        return ServiceResult<decimal>.Ok(session.ChargedAmount);
    }
}
