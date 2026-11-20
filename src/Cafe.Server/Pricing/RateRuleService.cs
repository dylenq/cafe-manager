using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Pricing;

public class RateRuleService(IDbContextFactory<CafeDbContext> dbFactory)
{
    public const decimal MaxMultiplier = 100m;
    private const string Gone = "That rate rule no longer exists. Refresh the list.";

    public async Task<List<RateRule>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RateRules.AsNoTracking()
            .OrderByDescending(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync();
    }

    public async Task<RateRule?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RateRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<ServiceResult<RateRule>> CreateAsync(RateRuleInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await ValidateAsync(db, input) is { } errors)
        {
            return ServiceResult<RateRule>.Fail(InputValidation.Summarise(errors), errors);
        }

        var entity = new RateRule();
        Apply(entity, input);
        db.RateRules.Add(entity);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (InputValidation.IsForeignKeyViolation(ex))
        {
            return TypeGoneResult();
        }

        return ServiceResult<RateRule>.Ok(entity);
    }

    public async Task<ServiceResult> UpdateAsync(int id, RateRuleInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.RateRules.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        if (await ValidateAsync(db, input) is { } errors)
        {
            return ServiceResult.Fail(InputValidation.Summarise(errors), errors);
        }

        Apply(entity, input);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.NotFound(Gone);
        }
        catch (DbUpdateException ex) when (InputValidation.IsForeignKeyViolation(ex))
        {
            return TypeGoneResult();
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.RateRules.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        db.RateRules.Remove(entity);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.NotFound(Gone);
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SetEnabledAsync(int id, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.RateRules.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        entity.IsEnabled = enabled;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.NotFound(Gone);
        }

        return ServiceResult.Ok();
    }

    private static void Apply(RateRule entity, RateRuleInput input)
    {
        entity.Name = input.Name.Trim();
        entity.StationTypeId = input.StationTypeId;
        entity.DaysOfWeek = input.DaysOfWeek;
        entity.StartTime = input.StartTime;
        entity.EndTime = input.EndTime;
        entity.Kind = input.Kind;
        entity.Value = input.Value;
        entity.Priority = input.Priority;
        entity.AppliesTo = input.AppliesTo;
        entity.IsEnabled = input.IsEnabled;
    }

    private static async Task<Dictionary<string, string>?> ValidateAsync(CafeDbContext db, RateRuleInput input)
    {
        var errors = InputValidation.Validate(input) ?? new Dictionary<string, string>();

        if (input.Value <= 0)
        {
            errors.TryAdd(nameof(RateRuleInput.Value), "Value must be greater than 0.");
        }
        else if (input.Kind == RateRuleKind.Multiplier && input.Value > MaxMultiplier)
        {
            errors.TryAdd(nameof(RateRuleInput.Value), $"A multiplier cannot be more than {MaxMultiplier:0}.");
        }

        if ((input.DaysOfWeek & DaysOfWeekFlags.All) == DaysOfWeekFlags.None
            || (input.DaysOfWeek & ~DaysOfWeekFlags.All) != DaysOfWeekFlags.None)
        {
            errors.TryAdd(nameof(RateRuleInput.DaysOfWeek), "Pick at least one day.");
        }

        if (input.StartTime == input.EndTime)
        {
            errors.TryAdd(nameof(RateRuleInput.EndTime), "Start and end time must be different.");
        }

        if (!Enum.IsDefined(input.Kind))
        {
            errors.TryAdd(nameof(RateRuleInput.Kind), "Pick a rule kind.");
        }

        if (!Enum.IsDefined(input.AppliesTo))
        {
            errors.TryAdd(nameof(RateRuleInput.AppliesTo), "Pick who the rule applies to.");
        }

        if (input.StationTypeId is { } typeId && !await db.StationTypes.AnyAsync(t => t.Id == typeId))
        {
            errors.TryAdd(nameof(RateRuleInput.StationTypeId), "That station type no longer exists.");
        }

        return errors.Count == 0 ? null : errors;
    }

    private static ServiceResult<RateRule> TypeGoneResult() =>
        ServiceResult<RateRule>.Fail(
            "That station type no longer exists.",
            new Dictionary<string, string> { [nameof(RateRuleInput.StationTypeId)] = "That station type no longer exists." });
}
