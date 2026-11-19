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
    SessionCharges charges,
    SessionNotifier notifier)
{
    public const int MinMinutes = 1;
    public const int MaxMinutes = 1440;

    private const string Busy = "That station already has a session. Refresh the dashboard.";
    private const string Stale = "This session was changed by someone else. Refresh and try again.";
    private const string TimeUp = "Time is up. Extend or stop the session.";
    private const string Gone = "That session no longer exists. Refresh the dashboard.";
    private const string AlreadyEnded = "That session has already ended.";

    /// <summary>Test seam: runs just before SaveChanges so a test can make the loaded session stale.</summary>
    internal Func<Task>? BeforeSave { get; set; }

    public Task<ServiceResult<Session>> StartPrepaidAsync(int stationId, int minutes, CancellationToken cancellationToken = default)
    {
        if (minutes is < MinMinutes or > MaxMinutes)
        {
            return Task.FromResult(ServiceResult<Session>.Fail($"Minutes must be between {MinMinutes} and {MaxMinutes}."));
        }

        return StartAsync(stationId, SessionMode.Prepaid, minutes, cancellationToken);
    }

    public Task<ServiceResult<Session>> StartOpenEndedAsync(int stationId, CancellationToken cancellationToken = default) =>
        StartAsync(stationId, SessionMode.OpenEnded, null, cancellationToken);

    private async Task<ServiceResult<Session>> StartAsync(int stationId, SessionMode mode, int? minutes, CancellationToken cancellationToken)
    {
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
        decimal quote = 0m;
        string details = "Open-ended";
        if (minutes is { } m)
        {
            var rules = await pricing.GetEnabledRulesAsync(db, cancellationToken);
            quote = PricingEngine.Price(new PricingRequest(
                [new PricedInterval(now, now.AddMinutes(m), station.StationTypeId, station.StationType.HourlyRate)],
                rules,
                pricing.TimeZone)).Total;
            details = string.Create(CultureInfo.InvariantCulture, $"Prepaid {m} min, charge {quote:0.00}");
        }

        var session = new Session
        {
            StationId = stationId,
            Mode = mode,
            Status = SessionStatus.Active,
            StartedAtUtc = now,
            PurchasedMinutes = minutes,
            ChargedAmount = quote,
        };
        session.Segments.Add(new SessionSegment { StationId = stationId, StartUtc = now });
        session.Events.Add(new SessionEvent { StationId = stationId, Type = SessionEventType.Started, AtUtc = now, Details = details });
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

    /// <summary>Pauses an Active session: closes the open segment so paused time neither counts down nor bills.</summary>
    public Task<ServiceResult<Session>> PauseAsync(int sessionId, CancellationToken cancellationToken = default) =>
        MutateAsync<Session>(sessionId, ctx =>
        {
            var session = ctx.Session;
            if (session.Status == SessionStatus.Grace)
            {
                return Task.FromResult(ServiceResult<Session>.Fail(TimeUp));
            }

            if (session.Status != SessionStatus.Active)
            {
                return Task.FromResult(ServiceResult<Session>.Fail("Only an active session can be paused."));
            }

            CloseOpenSegments(session, ctx.Now);
            session.Status = SessionStatus.Paused;
            AddEvent(session, SessionEventType.Paused, session.StationId, ctx.Now, "Paused");
            return Task.FromResult(ServiceResult<Session>.Ok(session));
        }, cancellationToken);

    /// <summary>Resumes a Paused session by opening a new segment on its current station.</summary>
    public Task<ServiceResult<Session>> ResumeAsync(int sessionId, CancellationToken cancellationToken = default) =>
        MutateAsync<Session>(sessionId, ctx =>
        {
            var session = ctx.Session;
            if (session.Status != SessionStatus.Paused)
            {
                return Task.FromResult(ServiceResult<Session>.Fail("Only a paused session can be resumed."));
            }

            session.Segments.Add(new SessionSegment { StationId = session.StationId, StartUtc = ctx.Now });
            session.Status = SessionStatus.Active;
            AddEvent(session, SessionEventType.Resumed, session.StationId, ctx.Now, "Resumed");
            return Task.FromResult(ServiceResult<Session>.Ok(session));
        }, cancellationToken);

    /// <summary>
    /// Adds minutes to a prepaid session and returns the extension quote (not the new total; the new total is
    /// stored in ChargedAmount). The quote is projected from the current planned end at the current station's type.
    /// </summary>
    public Task<ServiceResult<decimal>> ExtendAsync(int sessionId, int minutes, CancellationToken cancellationToken = default)
    {
        if (minutes is < MinMinutes or > MaxMinutes)
        {
            return Task.FromResult(ServiceResult<decimal>.Fail($"Minutes must be between {MinMinutes} and {MaxMinutes}."));
        }

        return MutateAsync<decimal>(sessionId, async ctx =>
        {
            var session = ctx.Session;
            if (session.Mode != SessionMode.Prepaid || session.PurchasedMinutes is not { } purchased)
            {
                return ServiceResult<decimal>.Fail("Only prepaid sessions can be extended.");
            }

            var active = ActiveSeconds(session, ctx.Now);
            var plannedEnd = session.Status == SessionStatus.Grace
                ? ctx.Now
                : ctx.Now.AddSeconds(Math.Max(SessionClock.RemainingSeconds(purchased, active), 0));

            var rules = await pricing.GetEnabledRulesAsync(ctx.Db, ctx.Cancellation);
            var type = session.Station.StationType;
            var quote = PricingEngine.Price(new PricingRequest(
                [new PricedInterval(plannedEnd, plannedEnd.AddMinutes(minutes), type.Id, type.HourlyRate)],
                rules,
                pricing.TimeZone)).Total;

            session.PurchasedMinutes = purchased + minutes;
            session.ChargedAmount += quote;

            if (session.Status == SessionStatus.Grace && SessionClock.RemainingSeconds(session.PurchasedMinutes.Value, active) > 0)
            {
                session.Status = SessionStatus.Active;
                session.GraceStartedAtUtc = null;
                session.Segments.Add(new SessionSegment { StationId = session.StationId, StartUtc = ctx.Now });
            }

            AddEvent(session, SessionEventType.Extended, session.StationId, ctx.Now,
                string.Create(CultureInfo.InvariantCulture, $"Extended by {minutes} min, +{quote:0.00}"));
            return ServiceResult<decimal>.Ok(quote);
        }, cancellationToken);
    }

    /// <summary>Moves an Active or Paused session to an enabled, idle, different station. Prepaid charges are not re-priced.</summary>
    public Task<ServiceResult<Session>> MoveAsync(int sessionId, int targetStationId, CancellationToken cancellationToken = default) =>
        MutateAsync<Session>(sessionId, async ctx =>
        {
            var session = ctx.Session;
            if (session.Status == SessionStatus.Grace)
            {
                return ServiceResult<Session>.Fail(TimeUp);
            }

            if (session.Status is not (SessionStatus.Active or SessionStatus.Paused))
            {
                return ServiceResult<Session>.Fail("Only an active or paused session can be moved.");
            }

            var target = await ctx.Db.Stations.FirstOrDefaultAsync(s => s.Id == targetStationId, ctx.Cancellation);
            if (target is null)
            {
                return ServiceResult<Session>.NotFound("That station no longer exists. Refresh the dashboard.");
            }

            if (target.Id == session.StationId)
            {
                return ServiceResult<Session>.Fail("The session is already on that station.");
            }

            if (!target.IsEnabled)
            {
                return ServiceResult<Session>.Fail($"{target.Name} is disabled.");
            }

            if (await ctx.Db.Sessions.AnyAsync(s => s.StationId == target.Id && s.Status != SessionStatus.Ended, ctx.Cancellation))
            {
                return ServiceResult<Session>.Fail($"{target.Name} already has a session.");
            }

            var from = session.Station;
            if (session.Status == SessionStatus.Active)
            {
                CloseOpenSegments(session, ctx.Now);
                session.Segments.Add(new SessionSegment { StationId = target.Id, StartUtc = ctx.Now });
            }

            session.StationId = target.Id;
            session.Station = target;
            ctx.Affected.Add(target.Id);
            AddEvent(session, SessionEventType.Moved, target.Id, ctx.Now, $"Moved {from.Name} -> {target.Name}");
            return ServiceResult<Session>.Ok(session);
        }, cancellationToken);

    /// <summary>Ends the session and returns its final charge. Prepaid charges stay as quoted (no automatic refund).</summary>
    public Task<ServiceResult<decimal>> StopAsync(int sessionId, CancellationToken cancellationToken = default) =>
        MutateAsync<decimal>(sessionId, async ctx =>
        {
            var session = ctx.Session;
            CloseOpenSegments(session, ctx.Now);
            if (session.Mode == SessionMode.OpenEnded)
            {
                session.ChargedAmount = await charges.OpenEndedAsync(ctx.Db, session.Segments, ctx.Now, cancellationToken: ctx.Cancellation);
            }

            session.Status = SessionStatus.Ended;
            session.EndReason = SessionEndReason.Stopped;
            session.EndedAtUtc = ctx.Now;
            AddEvent(session, SessionEventType.Stopped, session.StationId, ctx.Now,
                string.Create(CultureInfo.InvariantCulture, $"Charge {session.ChargedAmount:0.00}"));
            return ServiceResult<decimal>.Ok(session.ChargedAmount);
        }, cancellationToken);

    /// <summary>The charge StopAsync would return right now. Reads only: no events, no status change.</summary>
    public async Task<ServiceResult<decimal>> GetStopQuoteAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions.AsNoTracking().Include(s => s.Segments).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return ServiceResult<decimal>.NotFound(Gone);
        }

        if (session.Status == SessionStatus.Ended)
        {
            return ServiceResult<decimal>.Fail(AlreadyEnded);
        }

        if (session.Mode == SessionMode.Prepaid)
        {
            return ServiceResult<decimal>.Ok(session.ChargedAmount);
        }

        return ServiceResult<decimal>.Ok(await charges.OpenEndedAsync(db, session.Segments, time.GetUtcNow(), cancellationToken: cancellationToken));
    }

    private sealed class MutationContext(CafeDbContext db, Session session, DateTimeOffset now, CancellationToken cancellation)
    {
        public CafeDbContext Db { get; } = db;

        public Session Session { get; } = session;

        public DateTimeOffset Now { get; } = now;

        public CancellationToken Cancellation { get; } = cancellation;

        public HashSet<int> Affected { get; } = [session.StationId];
    }

    /// <summary>
    /// The shared mutation skeleton: fresh context, load, state check by the action, one SaveChanges (state, segments
    /// and event together), stale and unique-index conflicts as validation failures, notify after commit.
    /// </summary>
    private async Task<ServiceResult<T>> MutateAsync<T>(
        int sessionId,
        Func<MutationContext, Task<ServiceResult<T>>> action,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions
            .Include(s => s.Segments)
            .Include(s => s.Station).ThenInclude(s => s.StationType)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return ServiceResult<T>.NotFound(Gone);
        }

        if (session.Status == SessionStatus.Ended)
        {
            return ServiceResult<T>.Fail(AlreadyEnded);
        }

        var ctx = new MutationContext(db, session, time.GetUtcNow(), cancellationToken);
        var result = await action(ctx);
        if (!result.Success)
        {
            return result;
        }

        session.Version++;
        try
        {
            if (BeforeSave is { } hook)
            {
                await hook();
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<T>.Fail(Stale);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return ServiceResult<T>.Fail(Busy);
        }

        foreach (var stationId in ctx.Affected)
        {
            notifier.Raise(stationId);
        }

        return result;
    }

    private static void CloseOpenSegments(Session session, DateTimeOffset now)
    {
        foreach (var segment in session.Segments.Where(x => x.EndUtc is null))
        {
            segment.EndUtc = now;
        }
    }

    private static long ActiveSeconds(Session session, DateTimeOffset now)
    {
        var closed = session.Segments.Where(x => x.EndUtc is not null).Sum(x => (long)(x.EndUtc!.Value - x.StartUtc).TotalSeconds);
        var open = session.Segments.FirstOrDefault(x => x.EndUtc is null)?.StartUtc;
        return SessionClock.ActiveSeconds(closed, open, now);
    }

    private static void AddEvent(Session session, SessionEventType type, int stationId, DateTimeOffset at, string details) =>
        session.Events.Add(new SessionEvent { StationId = stationId, Type = type, AtUtc = at, Details = details });
}
