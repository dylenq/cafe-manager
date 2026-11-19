using Cafe.Server.Data;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Stations;

public class StationService(IDbContextFactory<CafeDbContext> dbFactory)
{
    private const string Gone = "That station no longer exists. Refresh the list.";

    public async Task<List<Station>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Stations.AsNoTracking().Include(s => s.StationType)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<Station?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Stations.AsNoTracking().Include(s => s.StationType).FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<ServiceResult<Station>> CreateAsync(StationInput input)
    {
        var entity = new Station();
        var failure = Prepare(entity, null, input);
        if (failure is not null)
        {
            return ServiceResult<Station>.Fail(failure.Error!, failure.FieldErrors);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var checkFailure = await CheckRulesAsync(db, null, entity);
        if (checkFailure is not null)
        {
            return ServiceResult<Station>.Fail(checkFailure.Error!, checkFailure.FieldErrors);
        }

        db.Stations.Add(entity);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            var mapped = Map(ex, entity);
            return ServiceResult<Station>.Fail(mapped.Error!, mapped.FieldErrors);
        }

        return ServiceResult<Station>.Ok(entity);
    }

    public async Task<ServiceResult> UpdateAsync(int id, StationInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.Stations.FirstOrDefaultAsync(s => s.Id == id);
        if (entity is null)
        {
            return ServiceResult.Fail(Gone);
        }

        // TvToken is deliberately never assigned here: pairing (Phase 4) owns it.
        var failure = Prepare(entity, id, input);
        if (failure is not null)
        {
            return failure;
        }

        var checkFailure = await CheckRulesAsync(db, id, entity);
        if (checkFailure is not null)
        {
            return checkFailure;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.Fail(Gone);
        }
        catch (DbUpdateException ex)
        {
            return Map(ex, entity);
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.Stations.FirstOrDefaultAsync(s => s.Id == id);
        if (entity is null)
        {
            return ServiceResult.Fail(Gone);
        }

        db.Stations.Remove(entity);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.Fail(Gone);
        }

        return ServiceResult.Ok();
    }

    /// <summary>Validates the input and copies it onto the entity with kind rules and normalisation applied.</summary>
    private static ServiceResult? Prepare(Station entity, int? id, StationInput input)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return ServiceResult.Fail(InputValidation.Summarise(errors), errors);
        }

        entity.Name = input.Name.Trim();
        entity.StationTypeId = input.StationTypeId;
        entity.Kind = input.Kind;
        entity.SortOrder = input.SortOrder;
        entity.IsEnabled = input.IsEnabled;

        if (input.Kind == StationKind.PC)
        {
            entity.AgentMachineId = InputValidation.Clean(input.AgentMachineId);
            entity.TvIpAddress = null;
            entity.TvMacAddress = null;
        }
        else
        {
            entity.AgentMachineId = null;
            entity.TvIpAddress = InputValidation.Clean(input.TvIpAddress);
            entity.TvMacAddress = InputValidation.Clean(input.TvMacAddress)?.Replace('-', ':').ToUpperInvariant();
        }

        return null;
    }

    private static async Task<ServiceResult?> CheckRulesAsync(CafeDbContext db, int? id, Station entity)
    {
        if (!await db.StationTypes.AnyAsync(t => t.Id == entity.StationTypeId))
        {
            return Fail("StationTypeId", "That station type no longer exists. Choose another.");
        }

        var name = entity.Name;
        if (await db.Stations.AnyAsync(s => s.Id != id && s.Name == name))
        {
            return Fail("Name", $"A station named '{name}' already exists.");
        }

        if (entity.AgentMachineId is { } machine)
        {
            var lower = machine.ToLower();
            if (await db.Stations.AnyAsync(s => s.Id != id && s.AgentMachineId != null && s.AgentMachineId.ToLower() == lower))
            {
                return Fail("AgentMachineId", $"Machine ID '{machine}' is already used by another station.");
            }
        }

        return null;
    }

    private static ServiceResult Map(DbUpdateException ex, Station entity)
    {
        if (InputValidation.IsUniqueViolation(ex))
        {
            return Fail("Name", $"A station named '{entity.Name}' already exists.");
        }

        return ServiceResult.Fail("The station could not be saved. Check the type still exists and try again.");
    }

    private static ServiceResult Fail(string field, string message) =>
        ServiceResult.Fail(message, new Dictionary<string, string> { [field] = message });
}
