using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class RateRuleServiceTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private IServiceScope Scope() => _factory.Services.CreateScope();

    private static RateRuleInput Input(string name = "Evening", int? typeId = null) => new()
    {
        Name = name,
        StationTypeId = typeId,
        DaysOfWeek = DaysOfWeekFlags.All,
        StartTime = new TimeOnly(20, 0),
        EndTime = new TimeOnly(22, 0),
        Kind = RateRuleKind.FixedHourlyRate,
        Value = 4000m,
        Priority = 10,
    };

    private static async Task<int> TypeAsync(IServiceScope scope, string name = "Standard") =>
        (await scope.ServiceProvider.GetRequiredService<StationTypeService>()
            .CreateAsync(new StationTypeInput { Name = name, HourlyRate = 6000m })).Value!.Id;

    [Fact]
    public async Task Create_valid_succeeds_and_get_returns_it()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var typeId = await TypeAsync(scope);

        var result = await rules.CreateAsync(Input("Evening", typeId));

        Assert.True(result.Success, result.Error);
        var loaded = await rules.GetAsync(result.Value!.Id);
        Assert.Equal("Evening", loaded!.Name);
        Assert.Equal(typeId, loaded.StationTypeId);
        Assert.Equal(4000m, loaded.Value);
        Assert.Equal(new TimeOnly(20, 0), loaded.StartTime);
    }

    [Fact]
    public async Task List_orders_by_priority_descending_then_id()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var a = Input("A"); a.Priority = 5;
        var b = Input("B"); b.Priority = 50;
        var c = Input("C"); c.Priority = 5;
        await rules.CreateAsync(a);
        await rules.CreateAsync(b);
        await rules.CreateAsync(c);

        var names = (await rules.ListAsync()).Select(r => r.Name).ToList();

        Assert.Equal(["B", "A", "C"], names);
    }

    [Fact]
    public async Task Overnight_window_is_valid()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var input = Input();
        input.StartTime = new TimeOnly(22, 0);
        input.EndTime = new TimeOnly(2, 0);

        Assert.True((await rules.CreateAsync(input)).Success);
    }

    [Theory]
    [InlineData("value0")]
    [InlineData("valueNegative")]
    [InlineData("noDays")]
    [InlineData("sameTimes")]
    [InlineData("blankName")]
    [InlineData("longName")]
    [InlineData("unknownType")]
    [InlineData("multiplierTooBig")]
    [InlineData("priorityHigh")]
    [InlineData("priorityNegative")]
    public async Task Invalid_input_is_rejected_with_field_error_and_nothing_stored(string scenario)
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var input = Input();
        string field;
        switch (scenario)
        {
            case "value0": input.Value = 0m; field = "Value"; break;
            case "valueNegative": input.Value = -5m; field = "Value"; break;
            case "noDays": input.DaysOfWeek = DaysOfWeekFlags.None; field = "DaysOfWeek"; break;
            case "sameTimes": input.EndTime = input.StartTime; field = "EndTime"; break;
            case "blankName": input.Name = "   "; field = "Name"; break;
            case "longName": input.Name = new string('x', 101); field = "Name"; break;
            case "unknownType": input.StationTypeId = 9999; field = "StationTypeId"; break;
            case "multiplierTooBig": input.Kind = RateRuleKind.Multiplier; input.Value = 100.5m; field = "Value"; break;
            case "priorityHigh": input.Priority = 1001; field = "Priority"; break;
            default: input.Priority = -1; field = "Priority"; break;
        }

        var result = await rules.CreateAsync(input);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Validation, result.Kind);
        Assert.True(result.FieldErrors.ContainsKey(field), $"expected field error for {field}");
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(await rules.ListAsync());
    }

    [Fact]
    public async Task Multiplier_at_cap_is_valid()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var input = Input();
        input.Kind = RateRuleKind.Multiplier;
        input.Value = 100m;

        Assert.True((await rules.CreateAsync(input)).Success);
    }

    [Fact]
    public async Task Update_changes_values_and_missing_id_is_not_found()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var id = (await rules.CreateAsync(Input())).Value!.Id;
        var edit = Input("Renamed");
        edit.Value = 3500m;

        Assert.True((await rules.UpdateAsync(id, edit)).Success);
        var loaded = await rules.GetAsync(id);
        Assert.Equal("Renamed", loaded!.Name);
        Assert.Equal(3500m, loaded.Value);

        var missing = await rules.UpdateAsync(9999, Input());
        Assert.False(missing.Success);
        Assert.Equal(FailureKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task Update_with_invalid_input_keeps_stored_values()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var id = (await rules.CreateAsync(Input())).Value!.Id;
        var bad = Input();
        bad.Value = 0m;

        var result = await rules.UpdateAsync(id, bad);

        Assert.False(result.Success);
        Assert.Equal(4000m, (await rules.GetAsync(id))!.Value);
    }

    [Fact]
    public async Task Delete_removes_and_missing_id_is_not_found()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var id = (await rules.CreateAsync(Input())).Value!.Id;

        Assert.True((await rules.DeleteAsync(id)).Success);
        Assert.Null(await rules.GetAsync(id));
        Assert.Equal(FailureKind.NotFound, (await rules.DeleteAsync(id)).Kind);
    }

    [Fact]
    public async Task SetEnabled_toggles_and_missing_id_is_not_found()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var id = (await rules.CreateAsync(Input())).Value!.Id;

        Assert.True((await rules.SetEnabledAsync(id, false)).Success);
        Assert.False((await rules.GetAsync(id))!.IsEnabled);
        Assert.True((await rules.SetEnabledAsync(id, true)).Success);
        Assert.True((await rules.GetAsync(id))!.IsEnabled);
        Assert.Equal(FailureKind.NotFound, (await rules.SetEnabledAsync(9999, true)).Kind);
    }

    [Fact]
    public async Task Station_type_used_by_a_rule_cannot_be_deleted()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var types = scope.ServiceProvider.GetRequiredService<StationTypeService>();
        var typeId = await TypeAsync(scope);
        await rules.CreateAsync(Input("Evening", typeId));

        var result = await types.DeleteAsync(typeId);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Validation, result.Kind);
        Assert.Contains("rate rule", result.Error);
        Assert.NotNull(await types.GetAsync(typeId));
    }

    [Fact]
    public async Task Station_type_can_be_deleted_once_its_rule_is_gone()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var types = scope.ServiceProvider.GetRequiredService<StationTypeService>();
        var typeId = await TypeAsync(scope);
        var ruleId = (await rules.CreateAsync(Input("Evening", typeId))).Value!.Id;
        await rules.DeleteAsync(ruleId);

        Assert.True((await types.DeleteAsync(typeId)).Success);
    }
}
