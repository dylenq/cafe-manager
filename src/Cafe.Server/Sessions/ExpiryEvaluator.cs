using System.Globalization;
using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Sessions;

public interface IExpiryEvaluator
{
    Task EvaluateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One evaluation pass over all prepaid sessions: warning events, expiry into Grace and grace-elapsed ending.
/// Every instant is derived from stored data (segments, GraceStartedAtUtc), never from the tick time, so a late
/// or post-restart pass resolves a session exactly as if it had been evaluated on time.
/// </summary>
public class ExpiryEvaluator(
    IDbContextFactory<CafeDbContext> dbFactory,
    TimeProvider time,
    SessionSettingsService settingsService,
    SessionNotifier notifier,
    IEnumerable<IExpiryActionHandler> handlers,
    ILogger<ExpiryEvaluator> logger) : IExpiryEvaluator
{
    /// <summary>Test seam: runs just before a session's SaveChanges (to make it stale or to fail).</summary>
    internal Func<Task>? BeforeSave { get; set; }

    public async Task EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            ids = await db.Sessions.AsNoTracking()
                .Where(s => s.Mode == SessionMode.Prepaid && (s.Status == SessionStatus.Active || s.Status == SessionStatus.Grace))
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
        }

        var now = time.GetUtcNow();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessAsync(id, now, settings, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expiry evaluation failed for session {SessionId}.", id);
            }
        }
    }

    private async Task ProcessAsync(int sessionId, DateTimeOffset now, SessionSettings settings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions
            .Include(s => s.Segments)
            .Include(s => s.Events)
            .Include(s => s.Station)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.Mode != SessionMode.Prepaid || session.PurchasedMinutes is not { } purchased)
        {
            return;
        }

        var changed = false;
        var ended = false;

        if (session.Status == SessionStatus.Active)
        {
            var closed = session.Segments.Where(x => x.EndUtc is not null).Sum(x => (long)(x.EndUtc!.Value - x.StartUtc).TotalSeconds);
            var open = session.Segments.FirstOrDefault(x => x.EndUtc is null);
            var remaining = SessionClock.RemainingSeconds(purchased, SessionClock.ActiveSeconds(closed, open?.StartUtc, now));

            var lastExtendedId = session.Events.Where(e => e.Type == SessionEventType.Extended).Select(e => e.Id).DefaultIfEmpty(0).Max();
            foreach (var minutes in settings.WarningMinutes.OrderByDescending(w => w))
            {
                var details = string.Create(CultureInfo.InvariantCulture, $"{minutes} min remaining");
                var fired = session.Events.Any(e => e.Type == SessionEventType.WarningReached && e.Details == details && e.Id > lastExtendedId);
                if (remaining <= minutes * 60L && !fired)
                {
                    AddEvent(session, SessionEventType.WarningReached, now, details);
                    changed = true;
                }
            }

            if (remaining <= 0)
            {
                var expiry = open is null
                    ? now
                    : open.StartUtc.AddSeconds(Math.Max(0, SessionClock.RemainingSeconds(purchased, closed)));
                if (open is not null)
                {
                    open.EndUtc = expiry;
                }

                session.Status = SessionStatus.Grace;
                session.GraceStartedAtUtc = expiry;
                AddEvent(session, SessionEventType.Expired, expiry, "Time ran out");
                changed = true;
            }
        }

        if (session.Status == SessionStatus.Grace && session.GraceStartedAtUtc is { } graceStart)
        {
            var graceEnd = graceStart.AddMinutes(settings.GracePeriodMinutes);
            if (now >= graceEnd)
            {
                session.Status = SessionStatus.Ended;
                session.EndReason = SessionEndReason.Expired;
                session.EndedAtUtc = graceEnd;
                AddEvent(session, SessionEventType.GraceElapsed, graceEnd,
                    string.Create(CultureInfo.InvariantCulture, $"Grace of {settings.GracePeriodMinutes} min used up, charge {session.ChargedAmount:0.00}"));
                changed = true;
                ended = true;
            }
        }

        if (!changed)
        {
            return;
        }

        session.Version++;
        try
        {
            if (BeforeSave is { } hook)
            {
                await hook();
            }

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogDebug("Session {SessionId} changed concurrently; skipped, the next tick re-evaluates.", sessionId);
            return;
        }

        notifier.Raise(session.StationId);

        if (ended)
        {
            await FireHandlersAsync(db, session, now, ct);
        }
    }

    /// <summary>Runs after the Ended commit, so handlers run at most once per session.</summary>
    private async Task FireHandlersAsync(CafeDbContext db, Session session, DateTimeOffset now, CancellationToken ct)
    {
        var failures = new List<string>();
        var list = handlers.ToList();
        foreach (var handler in list)
        {
            try
            {
                await handler.ExecuteAsync(session.Station, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expiry action handler {Handler} failed for station {Station}.", handler.GetType().Name, session.Station.Name);
                failures.Add($"{handler.GetType().Name}: {ex.Message}");
            }
        }

        var details = failures.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{list.Count} handler(s) ok")
            : string.Create(CultureInfo.InvariantCulture, $"{list.Count} handler(s), {failures.Count} failed: {string.Join("; ", failures)}");
        AddEvent(session, SessionEventType.ExpiryActionFired, now, details);
        await db.SaveChangesAsync(CancellationToken.None);
        notifier.Raise(session.StationId);
    }

    private static void AddEvent(Session session, SessionEventType type, DateTimeOffset at, string details) =>
        session.Events.Add(new SessionEvent { StationId = session.StationId, Type = type, AtUtc = at, Details = details });
}
