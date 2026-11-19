using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests.Infrastructure;

/// <summary>Shared setup for session tests: a factory, a clock parked at the next 12:00 UTC and seeding helpers.</summary>
public abstract class SessionTestBase : IDisposable
{
    protected readonly CafeWebApplicationFactory Factory = new();

    protected SessionTestBase()
    {
        // FakeTimeProvider cannot go back and starts at the real instant, so jump forward to the next 12:00 UTC.
        // Cafe:TimeZone is UTC in the test factory.
        var now = Factory.Clock.GetUtcNow();
        var target = new DateTimeOffset(now.Year, now.Month, now.Day, 12, 0, 0, TimeSpan.Zero);
        if (target <= now)
        {
            target = target.AddDays(1);
        }

        Factory.Clock.SetUtcNow(target);
        Start = target;
    }

    /// <summary>The 12:00 UTC instant every test starts from.</summary>
    protected DateTimeOffset Start { get; }

    public void Dispose()
    {
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }

    protected async Task<int> TypeAsync(string name = "Standard", decimal rate = 6000m)
    {
        using var scope = Factory.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<StationTypeService>();
        var existing = (await types.ListAsync()).FirstOrDefault(t => t.Name == name);
        return existing?.Id ?? (await types.CreateAsync(new StationTypeInput { Name = name, HourlyRate = rate })).Value!.Id;
    }

    protected async Task<int> StationAsync(string name = "PC-01", int? typeId = null, bool enabled = true)
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = name, StationTypeId = typeId ?? await TypeAsync(), Kind = StationKind.PC, IsEnabled = enabled,
        });
        Assert.True(created.Success, created.Error);
        return created.Value!.Id;
    }

    protected async Task<int> RuleAsync(RateRuleKind kind, decimal value, TimeOnly start, TimeOnly end, int? typeId = null, int priority = 0)
    {
        using var scope = Factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<RateRuleService>().CreateAsync(new RateRuleInput
        {
            Name = "Rule", StationTypeId = typeId, DaysOfWeek = DaysOfWeekFlags.All, StartTime = start, EndTime = end,
            Kind = kind, Value = value, Priority = priority,
        });
        Assert.True(result.Success, result.Error);
        return result.Value!.Id;
    }

    protected async Task<T> SvcAsync<T>(Func<SessionService, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<SessionService>());
    }

    protected async Task<Session> PrepaidAsync(int stationId, int minutes = 60)
    {
        var result = await SvcAsync(s => s.StartPrepaidAsync(stationId, minutes));
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }

    protected async Task<Session> OpenEndedAsync(int stationId)
    {
        var result = await SvcAsync(s => s.StartOpenEndedAsync(stationId));
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }

    protected void Advance(TimeSpan by) => Factory.Clock.Advance(by);

    protected async Task<CafeDbContext> DbAsync() =>
        await Factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>().CreateDbContextAsync();

    protected async Task<Session> LoadAsync(int sessionId)
    {
        await using var db = await DbAsync();
        return await db.Sessions.AsNoTracking().Include(s => s.Segments).Include(s => s.Events).SingleAsync(s => s.Id == sessionId);
    }

    protected async Task UpdateAsync(int sessionId, Action<Session> change)
    {
        await using var db = await DbAsync();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        change(session);
        await db.SaveChangesAsync();
    }

    /// <summary>Active seconds and remaining seconds for a prepaid session, through SessionClock at the current fake time.</summary>
    protected static (long Active, long Remaining) Clocked(Session session, DateTimeOffset now)
    {
        var closed = session.Segments.Where(s => s.EndUtc is not null).Sum(s => (long)(s.EndUtc!.Value - s.StartUtc).TotalSeconds);
        var open = session.Segments.FirstOrDefault(s => s.EndUtc is null)?.StartUtc;
        var active = SessionClock.ActiveSeconds(closed, open, now);
        return (active, session.PurchasedMinutes is { } p ? SessionClock.RemainingSeconds(p, active) : 0);
    }
}
