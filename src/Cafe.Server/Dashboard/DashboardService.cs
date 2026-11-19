using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Dashboard;

public class DashboardService(IDbContextFactory<CafeDbContext> dbFactory, IStationStatusProvider statusProvider)
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

        return stations
            .Select(s => new StationCardModel(s.Id, s.Name, s.StationType.Name, s.Kind, statusProvider.GetStatus(s)))
            .ToList();
    }
}
