using Cafe.Server.Data;
using Cafe.Server.Orders;
using Cafe.Server.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests.Infrastructure;

/// <summary>Product, order and settlement helpers on top of the customer test setup (Phase 6).</summary>
public abstract class PosTestBase : CustomerTestBase
{
    protected async Task<T> Svc<T>(Func<IServiceProvider, Task<T>> action) => await InScopeAsync(action);

    protected async Task<int> ProductAsync(string name, decimal price, string category = "Drinks")
    {
        var result = await InScopeAsync(sp => sp.GetRequiredService<ProductService>()
            .CreateAsync(new ProductInput { Name = name, Category = category, Price = price }));
        Assert.True(result.Success, result.Error);
        return result.Value!.Id;
    }

    protected async Task<Order> OrderAsync(int? sessionId, int? customerId, params (int ProductId, int Quantity)[] lines)
    {
        var result = await InScopeAsync(sp => sp.GetRequiredService<OrderService>().CreateAsync(
            new OrderDraft(lines.Select(l => new OrderLineDraft(l.ProductId, l.Quantity)).ToList(), customerId, sessionId)));
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }

    protected async Task<Order> LoadOrderAsync(int orderId)
    {
        await using var db = await DbAsync();
        return await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
    }
}
