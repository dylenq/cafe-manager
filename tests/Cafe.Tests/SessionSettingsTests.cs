using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class SessionSettingsTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private SessionSettingsService Service() => _factory.Services.GetRequiredService<SessionSettingsService>();

    private async Task<List<Setting>> RowsAsync()
    {
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>().CreateDbContextAsync();
        return await db.Settings.AsNoTracking()
            .Where(s => s.Key == SessionSettingsService.GraceKey || s.Key == SessionSettingsService.WarningKey)
            .ToListAsync();
    }

    [Fact]
    public async Task Defaults_when_nothing_stored()
    {
        var settings = await Service().GetAsync();

        Assert.Equal(2, settings.GracePeriodMinutes);
        Assert.Equal([10, 5, 1], settings.WarningMinutes);
        Assert.Equal(10, settings.MaxWarningMinutes);
    }

    [Fact]
    public async Task Save_normalises_and_round_trips()
    {
        var result = await Service().SaveAsync(new SessionSettingsInput { GracePeriodMinutes = 5, WarningMinutes = "15, 10,5,5" });

        Assert.True(result.Success, result.Error);
        var rows = await RowsAsync();
        Assert.Equal("15,10,5", rows.Single(r => r.Key == SessionSettingsService.WarningKey).Value);
        var settings = await Service().GetAsync();
        Assert.Equal(5, settings.GracePeriodMinutes);
        Assert.Equal([15, 10, 5], settings.WarningMinutes);
        Assert.Equal(15, settings.MaxWarningMinutes);
    }

    [Fact]
    public async Task Save_twice_updates_existing_rows()
    {
        await Service().SaveAsync(new SessionSettingsInput { GracePeriodMinutes = 5, WarningMinutes = "10,5" });
        var second = await Service().SaveAsync(new SessionSettingsInput { GracePeriodMinutes = 0, WarningMinutes = "1" });

        Assert.True(second.Success, second.Error);
        Assert.Equal(2, (await RowsAsync()).Count);
        var settings = await Service().GetAsync();
        Assert.Equal(0, settings.GracePeriodMinutes);
        Assert.Equal([1], settings.WarningMinutes);
    }

    [Theory]
    [InlineData(-1, "10,5,1", "GracePeriodMinutes")]
    [InlineData(31, "10,5,1", "GracePeriodMinutes")]
    [InlineData(2, "", "WarningMinutes")]
    [InlineData(2, "   ", "WarningMinutes")]
    [InlineData(2, "abc", "WarningMinutes")]
    [InlineData(2, "10,abc", "WarningMinutes")]
    [InlineData(2, "0", "WarningMinutes")]
    [InlineData(2, "61", "WarningMinutes")]
    [InlineData(2, "-5", "WarningMinutes")]
    [InlineData(2, "1.5", "WarningMinutes")]
    public async Task Invalid_input_is_rejected_and_nothing_stored(int grace, string warnings, string field)
    {
        var result = await Service().SaveAsync(new SessionSettingsInput { GracePeriodMinutes = grace, WarningMinutes = warnings });

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Validation, result.Kind);
        Assert.True(result.FieldErrors.ContainsKey(field));
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task Grace_zero_and_single_warning_are_valid()
    {
        var result = await Service().SaveAsync(new SessionSettingsInput { GracePeriodMinutes = 0, WarningMinutes = "1" });

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Malformed_stored_data_falls_back_to_defaults()
    {
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>().CreateDbContextAsync())
        {
            db.Settings.Add(new Setting { Key = SessionSettingsService.GraceKey, Value = "x" });
            db.Settings.Add(new Setting { Key = SessionSettingsService.WarningKey, Value = "x,y" });
            await db.SaveChangesAsync();
        }

        var settings = await Service().GetAsync();

        Assert.Equal(2, settings.GracePeriodMinutes);
        Assert.Equal([10, 5, 1], settings.WarningMinutes);
    }
}
