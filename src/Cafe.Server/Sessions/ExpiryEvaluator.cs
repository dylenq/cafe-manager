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
    ILogger<ExpiryEvaluator> logger,
    SessionSignalPublisher? signals = null) : IExpiryEvaluator
{
    /// <summary>Only sessions that expired within this window get their expiry actions (re)run.</summary>
    public static readonly TimeSpan ActionLookback = TimeSpan.FromHours(24);

    /// <summary>A failed run is retried at most this many times in total; then the session is marked as given up.</summary>
    public const int MaxActionAttempts = 5;

    /// <summary>Minimum wait between two attempts for the same session.</summary>
    public static readonly TimeSpan ActionRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>Upper bound for one handler call; a handler that exceeds it counts as failed and is retried later.</summary>
    internal TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Matches the SessionEvent.Details column length.</summary>
    private const int DetailsMaxLength = 300;

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

        await RunPendingActionsAsync(now, cancellationToken);
    }

    /// <summary>
    /// Brings one session up to date right now (expiry into Grace, grace-elapsed ending) without waiting for the
    /// next tick, so a mutation never acts on a session whose time has already run out. Handlers are not run here;
    /// the next pass picks them up. Returns true when this call ended the session.
    /// </summary>
    public async Task<bool> ResolveAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        return await ProcessAsync(sessionId, time.GetUtcNow(), settings, cancellationToken);
    }

    /// <summary>
    /// At-least-once expiry actions: every pass (including the first one at startup) also picks up sessions that
    /// expired within the last <see cref="ActionLookback"/> and have no ExpiryActionFired marker yet, whether they
    /// just ended in this pass or were left behind by a crash, a restart or failed handlers.
    /// </summary>
    private async Task RunPendingActionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<int> pending;
        var cutoff = now - ActionLookback;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            pending = await db.Sessions.AsNoTracking()
                .Where(s => s.Status == SessionStatus.Ended && s.EndReason == SessionEndReason.Expired
                    && s.EndedAtUtc >= cutoff
                    && !s.Events.Any(e => e.Type == SessionEventType.ExpiryActionFired))
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(ct);
        }

        // Sessions run concurrently, so one unreachable device only delays its own session (handlers are also
        // individually time-limited, see HandlerTimeout).
        await Task.WhenAll(pending.Select(async id =>
        {
            try
            {
                await RunActionsAsync(id, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expiry actions failed for session {SessionId}.", id);
            }
        }));
    }

    private async Task<bool> ProcessAsync(int sessionId, DateTimeOffset now, SessionSettings settings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions
            .Include(s => s.Segments)
            .Include(s => s.Events)
            .Include(s => s.Station)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.Mode != SessionMode.Prepaid || session.PurchasedMinutes is not { } purchased)
        {
            return false;
        }

        var changed = false;
        var ended = false;
        var firedWarnings = new List<int>();
        var enteredGrace = false;

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
                    firedWarnings.Add(minutes);
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
                enteredGrace = true;
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
            return false;
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
            return false;
        }

        notifier.Raise(session.StationId);
        await PublishSignalsAsync(session, settings, now, firedWarnings, enteredGrace, ended, ct);
        return ended;
    }

    /// <summary>
    /// Tells the PC agent what this pass did, after the commit. A late pass that fires several warning thresholds
    /// sends only the smallest one, and only while the session is still Active; GraceStarted is sent only when the
    /// session entered Grace in this pass, did not already end, and has grace time left.
    /// </summary>
    private async Task PublishSignalsAsync(
        Session session, SessionSettings settings, DateTimeOffset now, List<int> firedWarnings, bool enteredGrace, bool ended, CancellationToken ct)
    {
        if (signals is null)
        {
            return;
        }

        var outgoing = new List<SessionSignal>();
        if (session.Status == SessionStatus.Active && firedWarnings.Count > 0)
        {
            outgoing.Add(new SessionSignal(SessionSignalKind.WarningReached, session.StationId, session.Id, Minutes: firedWarnings.Min()));
        }

        if (enteredGrace && !ended && session.Status == SessionStatus.Grace && session.GraceStartedAtUtc is { } graceStart)
        {
            var left = (long)Math.Ceiling((graceStart.AddMinutes(settings.GracePeriodMinutes) - now).TotalSeconds);
            if (left > 0)
            {
                outgoing.Add(new SessionSignal(SessionSignalKind.GraceStarted, session.StationId, session.Id, GraceSeconds: (int)left));
            }
        }

        if (outgoing.Count > 0)
        {
            await signals.PublishAsync(outgoing, ct);
        }
    }

    /// <summary>
    /// Runs the handlers for one expired session and records the outcome. ExpiryActionFired is written only after
    /// the handlers ran and all succeeded (or the attempts are used up); failures are recorded as ExpiryActionFailed
    /// and retried on later passes. Cancellation (shutdown) records nothing, so the next start retries.
    /// </summary>
    private async Task RunActionsAsync(int sessionId, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions
            .Include(s => s.Events)
            .Include(s => s.Station)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.Events.Any(e => e.Type == SessionEventType.ExpiryActionFired))
        {
            return;
        }

        var failed = session.Events.Where(e => e.Type == SessionEventType.ExpiryActionFailed).ToList();
        if (failed.Count > 0 && failed.Max(e => e.AtUtc) + ActionRetryDelay > now)
        {
            return;
        }

        var attempt = failed.Count + 1;
        var failures = new List<string>();
        var list = handlers.ToList();
        foreach (var handler in list)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(HandlerTimeout);
                // WaitAsync also frees us from a handler that ignores its token.
                await handler.ExecuteAsync(session.Station, timeout.Token).WaitAsync(HandlerTimeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                logger.LogError("Expiry action handler {Handler} timed out for station {Station}.", handler.GetType().Name, session.Station.Name);
                failures.Add($"{handler.GetType().Name}: timed out after {HandlerTimeout.TotalSeconds:0.#}s");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expiry action handler {Handler} failed for station {Station}.", handler.GetType().Name, session.Station.Name);
                failures.Add($"{handler.GetType().Name}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
        {
            AddEvent(session, SessionEventType.ExpiryActionFired, now,
                Truncate(string.Create(CultureInfo.InvariantCulture, $"{list.Count} handler(s) ok (attempt {attempt})")));
        }
        else
        {
            var summary = string.Create(CultureInfo.InvariantCulture,
                $"attempt {attempt}/{MaxActionAttempts}, {failures.Count} of {list.Count} failed: {string.Join("; ", failures)}");
            AddEvent(session, SessionEventType.ExpiryActionFailed, now, Truncate(summary));
            if (attempt >= MaxActionAttempts)
            {
                logger.LogError("Expiry actions for station {Station} failed {Attempts} times; giving up.", session.Station.Name, attempt);
                AddEvent(session, SessionEventType.ExpiryActionFired, now,
                    Truncate(string.Create(CultureInfo.InvariantCulture, $"gave up after {attempt} failed attempts")));
            }
        }

        // Not cancellable: the marker must land even if shutdown starts right after the handlers ran.
        await db.SaveChangesAsync(CancellationToken.None);
        notifier.Raise(session.StationId);
    }

    private static string Truncate(string text) =>
        text.Length <= DetailsMaxLength ? text : string.Concat(text.AsSpan(0, DetailsMaxLength - 3), "...");

    private static void AddEvent(Session session, SessionEventType type, DateTimeOffset at, string details) =>
        session.Events.Add(new SessionEvent { StationId = session.StationId, Type = type, AtUtc = at, Details = details });
}
