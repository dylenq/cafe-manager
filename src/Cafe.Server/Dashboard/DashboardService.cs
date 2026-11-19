using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Dashboard;

public class DashboardService(
    IDbContextFactory<CafeDbContext> dbFactory,
    IStationStatusProvider statusProvider,
    SessionSettingsService settingsService,
    TimeProvider time)
{
    public async Task<List<StationCardModel>> GetCardsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Station.Name uses the NOCASE collation, so ThenBy(Name) is case-insensitive in SQL.
        var stations = await db.Stations.AsNoTracking()
            .Include(s => s.StationType)
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);

        var stationIds = stations.Select(s => s.Id).ToList();
        var sessions = await db.Sessions.AsNoTracking()
            .Include(s => s.Segments)
            .Where(s => s.Status != SessionStatus.Ended && stationIds.Contains(s.StationId))
            .ToListAsync(cancellationToken);
        var byStation = sessions.ToDictionary(s => s.StationId);

        var settings = await settingsService.GetAsync(cancellationToken);
        var now = time.GetUtcNow();

        return stations.Select(s =>
        {
            var session = byStation.TryGetValue(s.Id, out var entity) ? ToModel(entity, settings, now) : null;
            return new StationCardModel(s.Id, s.Name, s.StationType.Name, s.Kind, statusProvider.GetStatus(s, session, now), session);
        }).ToList();
    }

    private static SessionCardModel ToModel(Session session, SessionSettings settings, DateTimeOffset now)
    {
        var closed = session.Segments.Where(x => x.EndUtc is not null).Sum(x => (long)(x.EndUtc!.Value - x.StartUtc).TotalSeconds);
        var open = session.Segments.FirstOrDefault(x => x.EndUtc is null)?.StartUtc;
        return new SessionCardModel(
            session.Id,
            session.Mode,
            session.Status,
            session.PurchasedMinutes,
            closed,
            open,
            session.GraceStartedAtUtc,
            settings.GracePeriodMinutes,
            settings.MaxWarningMinutes * 60,
            session.ChargedAmount,
            now);
    }
}
