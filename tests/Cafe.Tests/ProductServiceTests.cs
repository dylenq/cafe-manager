using Cafe.Server.Data;
using Cafe.Server.Orders;
using Cafe.Server.Products;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>POS-01 / D-01: product catalogue rules.</summary>
public class ProductServiceTests : PosTestBase
{
    private static ProductInput Input(string name, decimal price = 10m, string category = "Drinks", int sort = 0) =>
        new() { Name = name, Price = price, Category = category, SortOrder = sort };

    private Task<T> Products<T>(Func<ProductService, Task<T>> action) => Svc(sp => action(sp.GetRequiredService<ProductService>()));

    [Fact]
    public async Task D01_duplicate_name_is_rejected_case_insensitively_with_a_name_field_error()
    {
        Assert.True((await Products(p => p.CreateAsync(Input("Coke")))).Success);

        var again = await Products(p => p.CreateAsync(Input("coke")));

        Assert.False(again.Success);
        Assert.True(again.FieldErrors.ContainsKey("Name"));
    }

    [Fact]
    public async Task D01_update_to_another_products_name_is_rejected_but_own_name_is_fine()
    {
        var a = (await Products(p => p.CreateAsync(Input("Coke")))).Value!;
        await Products(p => p.CreateAsync(Input("Fanta")));

        Assert.True((await Products(p => p.UpdateAsync(a.Id, Input("COKE", 12m)))).Success);
        var clash = await Products(p => p.UpdateAsync(a.Id, Input("fanta")));

        Assert.False(clash.Success);
        Assert.True(clash.FieldErrors.ContainsKey("Name"));
    }

    [Fact]
    public async Task D01_negative_price_and_empty_category_are_rejected()
    {
        Assert.False((await Products(p => p.CreateAsync(Input("Water", -1m)))).Success);
        Assert.False((await Products(p => p.CreateAsync(Input("Water", 1m, "   ")))).Success);
        Assert.True((await Products(p => p.CreateAsync(Input("Free sample", 0m)))).Success);
    }

    [Fact]
    public async Task D01_non_ascii_name_round_trips_unchanged()
    {
        var id = (await Products(p => p.CreateAsync(Input("Cà phê sữa đá", 25000m, "Đồ uống")))).Value!.Id;

        var loaded = await Products(p => p.GetAsync(id));

        Assert.Equal("Cà phê sữa đá", loaded!.Name);
        Assert.Equal("Đồ uống", loaded.Category);
    }

    [Fact]
    public async Task D01_update_changes_price_and_deactivate_hides_from_active_list()
    {
        var id = (await Products(p => p.CreateAsync(Input("Coke", 10m)))).Value!.Id;

        var updated = await Products(p => p.UpdateAsync(id, Input("Coke", 15m)));
        await Products(p => p.SetActiveAsync(id, false));

        Assert.Equal(15m, updated.Value!.Price);
        Assert.DoesNotContain(await Products(p => p.ListActiveAsync()), x => x.Id == id);
        Assert.Contains(await Products(p => p.ListAsync(includeInactive: true)), x => x.Id == id);
        Assert.DoesNotContain(await Products(p => p.ListAsync()), x => x.Id == id);
    }

    [Fact]
    public async Task D01_delete_removes_an_unused_product()
    {
        var id = (await Products(p => p.CreateAsync(Input("Coke")))).Value!.Id;

        Assert.True((await Products(p => p.DeleteAsync(id))).Success);
        Assert.Null(await Products(p => p.GetAsync(id)));
    }

    [Fact]
    public async Task D01_delete_of_an_ordered_product_tells_staff_to_deactivate()
    {
        var id = await ProductAsync("Coke", 10m);
        await OrderAsync(null, null, (id, 1));

        var result = await Products(p => p.DeleteAsync(id));

        Assert.False(result.Success);
        Assert.Contains("deactivate", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await Products(p => p.GetAsync(id)));
    }

    [Fact]
    public async Task D01_delete_and_update_of_a_missing_product_are_not_found()
    {
        Assert.Equal(FailureKind.NotFound, (await Products(p => p.DeleteAsync(999))).Kind);
        Assert.Equal(FailureKind.NotFound, (await Products(p => p.UpdateAsync(999, Input("X")))).Kind);
        Assert.Equal(FailureKind.NotFound, (await Products(p => p.SetActiveAsync(999, true))).Kind);
    }

    [Fact]
    public async Task D01_categories_are_distinct_sorted_and_only_from_active_products()
    {
        await Products(p => p.CreateAsync(Input("Coke", 1m, "Drinks")));
        await Products(p => p.CreateAsync(Input("Fanta", 1m, "Drinks")));
        await Products(p => p.CreateAsync(Input("Chips", 1m, "Snacks")));
        await Products(p => p.CreateAsync(Input("Cake", 1m, "Bakery")));
        var old = (await Products(p => p.CreateAsync(Input("Old", 1m, "Zzz")))).Value!;
        await Products(p => p.SetActiveAsync(old.Id, false));

        Assert.Equal(["Bakery", "Drinks", "Snacks"], await Products(p => p.CategoriesAsync()));
    }

    [Fact]
    public async Task D01_list_is_ordered_by_category_then_sort_order_then_name()
    {
        await Products(p => p.CreateAsync(Input("B-item", 1m, "Snacks", 0)));
        await Products(p => p.CreateAsync(Input("Z-drink", 1m, "Drinks", 0)));
        await Products(p => p.CreateAsync(Input("A-drink", 1m, "Drinks", 5)));
        await Products(p => p.CreateAsync(Input("M-drink", 1m, "Drinks", 0)));

        var names = (await Products(p => p.ListActiveAsync())).Select(x => x.Name).ToList();

        Assert.Equal(["M-drink", "Z-drink", "A-drink", "B-item"], names);
    }
}
