using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Stations;

public class StationTypeService(IDbContextFactory<CafeDbContext> dbFactory)
{
    private const string Gone = "That station type no longer exists. Refresh the list.";

    public async Task<List<StationType>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.StationTypes.AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync();
    }

    public async Task<StationType?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.StationTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<ServiceResult<StationType>> CreateAsync(StationTypeInput input)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return ServiceResult<StationType>.Fail(InputValidation.Summarise(errors), errors);
        }

        var name = input.Name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.StationTypes.AnyAsync(t => t.Name == name))
        {
            return Duplicate(name);
        }

        var entity = new StationType { Name = name, HourlyRate = input.HourlyRate, SortOrder = input.SortOrder };
        db.StationTypes.Add(entity);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return Duplicate(name);
        }

        return ServiceResult<StationType>.Ok(entity);
    }

    public async Task<ServiceResult> UpdateAsync(int id, StationTypeInput input)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return ServiceResult.Fail(InputValidation.Summarise(errors), errors);
        }

        var name = input.Name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.StationTypes.FirstOrDefaultAsync(t => t.Id == id);
        if (entity is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        if (await db.StationTypes.AnyAsync(t => t.Id != id && t.Name == name))
        {
            return Duplicate(name);
        }

        entity.Name = name;
        entity.HourlyRate = input.HourlyRate;
        entity.SortOrder = input.SortOrder;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.NotFound(Gone);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return Duplicate(name);
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.StationTypes.FirstOrDefaultAsync(t => t.Id == id);
        if (entity is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        var count = await db.Stations.CountAsync(s => s.StationTypeId == id);
        if (count > 0)
        {
            return ServiceResult.Fail(InUse(entity.Name, count));
        }

        db.StationTypes.Remove(entity);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.NotFound(Gone);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            // A station was added between the count and the delete; the restrict FK refused it.
            // Any other database failure propagates so it is not reported as "in use".
            var now = await db.Stations.CountAsync(s => s.StationTypeId == id);
            return ServiceResult.Fail(InUse(entity.Name, Math.Max(now, 1)));
        }

        return ServiceResult.Ok();
    }

    private static string InUse(string name, int count) =>
        $"Cannot delete '{name}': {count} station(s) still use this type. Move or delete them first.";

    private static ServiceResult<StationType> Duplicate(string name) =>
        ServiceResult<StationType>.Fail(
            $"A station type named '{name}' already exists.",
            new Dictionary<string, string> { ["Name"] = $"A station type named '{name}' already exists." });
}
