using Cafe.Server.Customers;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests.Infrastructure;

/// <summary>Customer, package and balance helpers on top of the session test setup.</summary>
public abstract class CustomerTestBase : SessionTestBase
{
    protected async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    protected async Task<int> CustomerAsync(string name = "Alice", string? phone = null, bool member = false)
    {
        var result = await InScopeAsync(sp => sp.GetRequiredService<CustomerService>()
            .CreateAsync(new CustomerInput { Name = name, Phone = phone, IsMember = member }));
        Assert.True(result.Success, result.Error);
        return result.Value!.Id;
    }

    protected async Task<int> PackageAsync(string name, int minutes, decimal price, int? typeId = null)
    {
        var result = await InScopeAsync(sp => sp.GetRequiredService<PackageService>()
            .CreateAsync(new TimePackageInput { Name = name, Minutes = minutes, Price = price, StationTypeId = typeId }));
        Assert.True(result.Success, result.Error);
        return result.Value!.Id;
    }

    protected async Task<PackageSale> SellAsync(int customerId, int packageId, PaymentMethod method = PaymentMethod.Cash)
    {
        var result = await InScopeAsync(sp => sp.GetRequiredService<PackageService>().SellAsync(customerId, packageId, method));
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }

    /// <summary>Ledger sum for one bucket (null = any-type), straight from the database.</summary>
    protected async Task<int> BalanceAsync(int customerId, int? stationTypeId = null)
    {
        await using var db = await DbAsync();
        return await db.CustomerMinuteLedger
            .Where(l => l.CustomerId == customerId && l.StationTypeId == stationTypeId)
            .SumAsync(l => l.MinutesDelta);
    }

    protected async Task<ServiceResult<Session>> TryStartBalanceAsync(int stationId, int customerId, int minutes) =>
        await SvcAsync(s => s.StartFromBalanceAsync(stationId, customerId, minutes));

    protected async Task<Session> StartBalanceAsync(int stationId, int customerId, int minutes)
    {
        var result = await TryStartBalanceAsync(stationId, customerId, minutes);
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }
}
