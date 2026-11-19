using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Sessions;

public sealed record SessionHistoryEvent(SessionEventType Type, DateTimeOffset AtUtc, string StationName, string Details);

public sealed record SessionHistoryRow(
    int SessionId,
    string StationName,
    SessionMode Mode,
    SessionStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    SessionEndReason? EndReason,
    long ActiveSeconds,
    decimal ChargedAmount,
    IReadOnlyList<SessionHistoryEvent> Events);

/// <summary>Read side of the session audit trail for the /sessions page.</summary>
public class SessionHistoryService(IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time)
{
    public const int MaxRows = 200;

    /// <summary>Newest sessions first. A station filter matches sessions that started on, ran on or moved through it.</summary>
    public async Task<List<SessionHistoryRow>> GetRecentAsync(int? stationId = null, int take = MaxRows, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, MaxRows);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Sessions.AsNoTracking().AsQueryable();
        if (stationId is { } id)
        {
            query = query.Where(s => s.StationId == id || s.Segments.Any(x => x.StationId == id) || s.Events.Any(e => e.StationId == id));
        }

        var sessions = await query
            .OrderByDescending(s => s.StartedAtUtc).ThenByDescending(s => s.Id)
            .Take(take)
            .Include(s => s.Segments)
            .Include(s => s.Events)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var names = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        string Name(int id) => names.TryGetValue(id, out var name) ? name : "(deleted station)";
        var now = time.GetUtcNow();

        return sessions.Select(s =>
        {
            var closed = s.Segments.Where(x => x.EndUtc is not null).Sum(x => (long)(x.EndUtc!.Value - x.StartUtc).TotalSeconds);
            var open = s.Segments.FirstOrDefault(x => x.EndUtc is null)?.StartUtc;
            var events = s.Events.OrderBy(e => e.AtUtc).ThenBy(e => e.Id)
                .Select(e => new SessionHistoryEvent(e.Type, e.AtUtc, Name(e.StationId), e.Details))
                .ToList();
            return new SessionHistoryRow(
                s.Id, Name(s.StationId), s.Mode, s.Status, s.StartedAtUtc, s.EndedAtUtc, s.EndReason,
                SessionClock.ActiveSeconds(closed, open, now), s.ChargedAmount, events);
        }).ToList();
    }
}
