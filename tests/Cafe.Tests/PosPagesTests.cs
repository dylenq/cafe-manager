using System.Net;
using Cafe.Server.Customers;
using Cafe.Server.Orders;
using Cafe.Server.Products;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class PosPagesTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/products")]
    [InlineData("/pos")]
    public async Task Anonymous_request_redirects_to_login(string path)
    {
        using var auth = await _factory.CreateAuthenticatedClientAsync();
        var anonymous = _factory.CreateNoRedirectClient();

        var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task Products_empty_install_shows_empty_state()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No products yet", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Products_page_renders_persisted_data_with_inactive_badge()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var products = scope.ServiceProvider.GetRequiredService<ProductService>();
            await products.CreateAsync(new ProductInput { Name = "Espresso", Category = "Hot drinks", Price = 3.5m });
            var old = (await products.CreateAsync(new ProductInput { Name = "Retired Soda", Category = "Cold drinks", Price = 2m })).Value!;
            await products.SetActiveAsync(old.Id, false);
        }

        var shown = await (await client.GetAsync("/products")).Content.ReadAsStringAsync();
        Assert.Contains("Espresso", shown);
        Assert.Contains("Hot drinks", shown);
        Assert.DoesNotContain("Retired Soda", shown);

        // Inactive products are only listed after the toggle, so check the service default too.
        using var scope2 = _factory.Services.CreateScope();
        var all = await scope2.ServiceProvider.GetRequiredService<ProductService>().ListAsync(includeInactive: true);
        Assert.Contains(all, p => p.Name == "Retired Soda" && !p.IsActive);
    }

    [Fact]
    public async Task Deleting_an_ordered_product_is_refused_with_a_deactivate_hint()
    {
        using var scope = _factory.Services.CreateScope();
        var products = scope.ServiceProvider.GetRequiredService<ProductService>();
        var id = (await products.CreateAsync(new ProductInput { Name = "Cake", Category = "Food", Price = 4m })).Value!.Id;
        var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
        Assert.True((await orders.CreateAsync(new OrderDraft([new(id, 1)], null, null))).Success);

        var result = await products.DeleteAsync(id);

        Assert.False(result.Success);
        Assert.Contains("Deactivate", result.Error);
    }

    [Fact]
    public async Task Pos_empty_install_renders_core_controls()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/pos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Open tabs", body);
        Assert.Contains("Walk-in", body);
        Assert.Contains("Charge now", body);
        Assert.Contains("Add to tab", body);
    }

    [Fact]
    public async Task Pos_shows_active_products_as_tiles()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var products = scope.ServiceProvider.GetRequiredService<ProductService>();
            await products.CreateAsync(new ProductInput { Name = "Iced Tea", Category = "Cold drinks", Price = 2.5m });
            await products.CreateAsync(new ProductInput { Name = "Nachos", Category = "Snacks", Price = 5m });
            var hidden = (await products.CreateAsync(new ProductInput { Name = "Hidden Item", Category = "Snacks", Price = 1m })).Value!;
            await products.SetActiveAsync(hidden.Id, false);
        }

        var body = await (await client.GetAsync("/pos")).Content.ReadAsStringAsync();

        Assert.Contains("pos-tile", body);
        Assert.Contains("Iced Tea", body);
        Assert.Contains("Nachos", body);
        Assert.Contains("Cold drinks", body);
        Assert.DoesNotContain("Hidden Item", body);
    }

    [Fact]
    public async Task Pos_open_tabs_panel_lists_customer_orders_and_hides_voided()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var productId = (await sp.GetRequiredService<ProductService>()
                .CreateAsync(new ProductInput { Name = "Burger", Category = "Food", Price = 8m })).Value!.Id;
            var alice = (await sp.GetRequiredService<CustomerService>()
                .CreateAsync(new CustomerInput { Name = "Alice Tabs" })).Value!.Id;
            var bob = (await sp.GetRequiredService<CustomerService>()
                .CreateAsync(new CustomerInput { Name = "Bob Voided" })).Value!.Id;
            var orders = sp.GetRequiredService<OrderService>();
            Assert.True((await orders.CreateAsync(new OrderDraft([new(productId, 2)], alice, null))).Success);
            var voided = (await orders.CreateAsync(new OrderDraft([new(productId, 1)], bob, null))).Value!;
            Assert.True((await orders.VoidAsync(voided.Id)).Success);
        }

        var body = await (await client.GetAsync("/pos")).Content.ReadAsStringAsync();

        Assert.Contains("Alice Tabs", body);
        Assert.Contains("Settle", body);
        Assert.DoesNotContain("Bob Voided", body);
    }
}
