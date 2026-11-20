using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Dashboard;

public class DashboardService(
    IDbContextFactory<CafeDbContext> dbFactory,
    IStationStatusProvider statusProvider,
    SessionSettingsService settingsService,
    SessionCharges charges,
    Cafe.Server.Pricing.PricingConfigService pricing,
    TimeProvider time,
    Cafe.Server.Agents.AgentRegistry agents,
    Cafe.Server.Devices.TvStateCache? tv = null)
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
            .Include(s => s.Customer)
            .Where(s => s.Status != SessionStatus.Ended && stationIds.Contains(s.StationId))
            .ToListAsync(cancellationToken);
        var byStation = sessions.ToDictionary(s => s.StationId);

        var settings = await settingsService.GetAsync(cancellationToken);
        var now = time.GetUtcNow();

        IReadOnlyList<RateRule>? rules = null;
        var cards = new List<StationCardModel>(stations.Count);
        foreach (var s in stations)
        {
            SessionCardModel? session = null;
            if (byStation.TryGetValue(s.Id, out var entity))
            {
                decimal? estimate = null;
                if (entity.Mode == SessionMode.OpenEnded)
                {
                    // Same computation as StopAsync, so the card shows what a stop right now would charge.
                    rules ??= await pricing.GetEnabledRulesAsync(db, cancellationToken);
                    estimate = await charges.OpenEndedAsync(db, entity.Segments, now, rules, cancellationToken);
                }

                session = ToModel(entity, settings, now, estimate);
            }

            // A PC with a machine id whose agent is not connected: shown even while a session runs (the status then
            // stays InSession, so the card can add a small secondary indicator).
            var hasAgent = s.Kind == Cafe.Shared.StationKind.PC && !string.IsNullOrWhiteSpace(s.AgentMachineId);
            var agentOffline = hasAgent && !agents.IsConnected(s.AgentMachineId!);
            var hasTv = s.Kind == Cafe.Shared.StationKind.Console && !string.IsNullOrWhiteSpace(s.TvIpAddress);
            cards.Add(new StationCardModel(s.Id, s.Name, s.StationType.Name, s.Kind, statusProvider.GetStatus(s, session, now), session, agentOffline, hasAgent,
                hasTv, hasTv ? tv?.Get(s.Id) : null));
        }

        return cards;
    }

    private static SessionCardModel ToModel(Session session, SessionSettings settings, DateTimeOffset now, decimal? estimate)
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
            estimate ?? session.ChargedAmount,
            now,
            session.Customer?.Name,
            session.IsMemberPricing);
    }
}
