using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Agents;

/// <summary>Read side of the manual agent command audit trail.</summary>
public sealed class AgentCommandLogService(IDbContextFactory<CafeDbContext> dbFactory)
{
    public const int MaxLimit = 200;

    /// <summary>Newest first, optionally for one station, capped at 200 rows.</summary>
    public async Task<List<AgentCommandLog>> ListAsync(int? stationId = null, int limit = MaxLimit, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AgentCommandLogs.AsNoTracking().AsQueryable();
        if (stationId is { } id)
        {
            query = query.Where(l => l.StationId == id);
        }

        return await query
            .OrderByDescending(l => l.AtUtc).ThenByDescending(l => l.Id)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(cancellationToken);
    }
}
