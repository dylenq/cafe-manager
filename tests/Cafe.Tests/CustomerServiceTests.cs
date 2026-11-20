using Cafe.Server.Customers;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class CustomerServiceTests : CustomerTestBase
{
    private Task<ServiceResult<Customer>> CreateAsync(string name, string? phone = null, bool member = false, string? notes = null) =>
        InScopeAsync(sp => sp.GetRequiredService<CustomerService>()
            .CreateAsync(new CustomerInput { Name = name, Phone = phone, IsMember = member, Notes = notes }));

    private Task<ServiceResult<Customer>> UpdateAsync(int id, string name, string? phone = null, bool member = false) =>
        InScopeAsync(sp => sp.GetRequiredService<CustomerService>()
            .UpdateAsync(id, new CustomerInput { Name = name, Phone = phone, IsMember = member }));

    private Task<List<Customer>> SearchAsync(string? term, bool includeInactive = false, int take = 50) =>
        InScopeAsync(sp => sp.GetRequiredService<CustomerService>().SearchAsync(term, includeInactive, take));

    private Task<ServiceResult> SetActiveAsync(int id, bool active) =>
        InScopeAsync(sp => sp.GetRequiredService<CustomerService>().SetActiveAsync(id, active));

    [Fact]
    public async Task CUST01_create_trims_name()
    {
        var result = await CreateAsync("  Alice  ");
        Assert.True(result.Success, result.Error);
        Assert.Equal("Alice", result.Value!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CUST01_create_blank_name_is_a_Name_field_error(string name)
    {
        var result = await CreateAsync(name);
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Name"));
    }

    [Fact]
    public async Task CUST01_notes_over_500_chars_is_a_field_error()
    {
        var result = await CreateAsync("Alice", notes: new string('x', 501));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Notes"));
        Assert.True((await CreateAsync("Bob", notes: new string('x', 500))).Success);
    }

    [Theory]
    [InlineData("(555) 123-4567", "5551234567")]
    [InlineData("555.123.4567", "5551234567")]
    [InlineData("+84 90-123 4567", "84901234567")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task CUST01_phone_is_stored_as_digits_only(string? input, string? expected)
    {
        var result = await CreateAsync("Alice", input);
        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.Value!.Phone);
    }

    [Fact]
    public async Task CUST01_phone_without_digits_is_a_Phone_field_error()
    {
        var result = await CreateAsync("Alice", "abc");
        Assert.False(result.Success);
        Assert.Equal("Phone must contain digits.", result.FieldErrors["Phone"]);
    }

    [Fact]
    public async Task CUST01_phone_over_20_digits_is_a_Phone_field_error()
    {
        Assert.True((await CreateAsync("Max", new string('1', 20))).Success);
        var result = await CreateAsync("Alice", new string('1', 21));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Phone"));
    }

    [Fact]
    public async Task CUST01_phones_that_normalise_equal_collide_as_a_Phone_field_error()
    {
        Assert.True((await CreateAsync("Alice", "(555) 123-4567")).Success);
        var result = await CreateAsync("Bob", "555.123.4567");
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Phone"));
        Assert.DoesNotContain("inactive", result.FieldErrors["Phone"]);
    }

    [Fact]
    public async Task CUST01_phone_collision_with_an_inactive_customer_says_inactive()
    {
        var alice = await CustomerAsync("Alice", "5551234567");
        await SetActiveAsync(alice, false);
        var result = await CreateAsync("Bob", "555-123-4567");
        Assert.False(result.Success);
        Assert.Contains("inactive", result.FieldErrors["Phone"]);
    }

    [Fact]
    public async Task CUST01_two_customers_without_phone_are_fine()
    {
        Assert.True((await CreateAsync("Alice")).Success);
        Assert.True((await CreateAsync("Bob", "  ")).Success);
    }

    [Fact]
    public async Task CUST01_update_changes_fields_and_unknown_id_is_NotFound()
    {
        var id = await CustomerAsync("Alice", "5551112222");
        var result = await UpdateAsync(id, " Alicia ", "(555) 333-4444", member: true);
        Assert.True(result.Success, result.Error);
        var saved = (await InScopeAsync(sp => sp.GetRequiredService<CustomerService>().GetAsync(id)))!;
        Assert.Equal("Alicia", saved.Name);
        Assert.Equal("5553334444", saved.Phone);
        Assert.True(saved.IsMember);

        var missing = await UpdateAsync(9999, "Ghost");
        Assert.False(missing.Success);
        Assert.Equal(FailureKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task CUST01_update_to_own_phone_is_fine_and_to_anothers_is_a_Phone_error()
    {
        var alice = await CustomerAsync("Alice", "5551112222");
        await CustomerAsync("Bob", "5553334444");
        Assert.True((await UpdateAsync(alice, "Alice B", "555-111-2222")).Success);
        var clash = await UpdateAsync(alice, "Alice", "5553334444");
        Assert.False(clash.Success);
        Assert.True(clash.FieldErrors.ContainsKey("Phone"));
    }

    [Fact]
    public async Task CUST01_unique_race_on_save_is_a_Phone_field_error_not_an_exception()
    {
        // The interceptor inserts a conflicting row after the pre-check passed but before the save runs.
        _ = Factory.Services; // boots the host so the schema exists
        var racing = new CustomerService(new RacingInsertFactory(Factory.ConnectionString, "5559990000"), Factory.Clock);
        var result = await racing.CreateAsync(new CustomerInput { Name = "Alice", Phone = "555-999-0000" });
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Phone"));
    }

    [Fact]
    public async Task CUST01_changing_IsMember_does_not_alter_an_existing_session_snapshot()
    {
        var station = await StationAsync();
        var customer = await CustomerAsync("Alice", member: true);
        await SellAsync(customer, await PackageAsync("2 hours", 120, 100m));
        var session = await StartBalanceAsync(station, customer, 30);
        Assert.True((await LoadAsync(session.Id)).IsMemberPricing);

        Assert.True((await UpdateAsync(customer, "Alice", member: false)).Success);

        Assert.True((await LoadAsync(session.Id)).IsMemberPricing);
    }

    [Fact]
    public async Task CUST01_deactivate_keeps_the_row_and_reactivate_restores_it()
    {
        var id = await CustomerAsync("Alice");
        Assert.True((await SetActiveAsync(id, false)).Success);
        await using (var db = await DbAsync())
        {
            Assert.False((await db.Customers.SingleAsync(c => c.Id == id)).IsActive);
        }

        Assert.True((await SetActiveAsync(id, true)).Success);
        await using (var db = await DbAsync())
        {
            Assert.True((await db.Customers.SingleAsync(c => c.Id == id)).IsActive);
        }

        var missing = await SetActiveAsync(9999, false);
        Assert.Equal(FailureKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task CUST02_search_is_case_insensitive_contains_on_name()
    {
        await CustomerAsync("Alice");
        await CustomerAsync("MALIK");
        await CustomerAsync("Bob");
        var names = (await SearchAsync("ali")).Select(c => c.Name).ToList();
        Assert.Equal(["Alice", "MALIK"], names);
    }

    [Fact]
    public async Task CUST02_search_finds_non_ascii_names_case_insensitively()
    {
        await CustomerAsync("Nguyễn An");
        await CustomerAsync("Bob");
        Assert.Equal("Nguyễn An", Assert.Single(await SearchAsync("nguyễn")).Name);
        Assert.Equal("Nguyễn An", Assert.Single(await SearchAsync("NGUYỄN")).Name);
    }

    [Fact]
    public async Task CUST02_digit_term_matches_phone_contains_and_letter_terms_never_do()
    {
        await CustomerAsync("Alice", "5551234567");
        await CustomerAsync("Bob", "9998887777");
        Assert.Equal("Alice", Assert.Single(await SearchAsync("123")).Name);
        Assert.Equal("Alice", Assert.Single(await SearchAsync("(555) 12")).Name);
        Assert.Empty(await SearchAsync("a123"));
    }

    [Fact]
    public async Task CUST02_empty_term_returns_active_customers_ordered_by_name_then_id()
    {
        await CustomerAsync("Bob");
        await CustomerAsync("Alice");
        var gone = await CustomerAsync("Aaron");
        await SetActiveAsync(gone, false);
        var rows = await SearchAsync("");
        Assert.Equal(["Alice", "Bob"], rows.Select(c => c.Name).ToList());
        Assert.Equal(3, (await SearchAsync(null, includeInactive: true)).Count);
    }

    [Fact]
    public async Task CUST02_search_returns_at_most_50_rows()
    {
        for (var i = 0; i < 60; i++)
        {
            await CustomerAsync($"Player {i:00}");
        }

        Assert.Equal(50, (await SearchAsync("player")).Count);
        Assert.Equal(50, (await SearchAsync("", take: 500)).Count);
        Assert.Equal(5, (await SearchAsync("", take: 5)).Count);
    }

    [Fact]
    public async Task CUST02_inactive_customers_are_hidden_unless_asked()
    {
        var id = await CustomerAsync("Alice");
        await CustomerAsync("Alicia");
        await SetActiveAsync(id, false);
        Assert.Equal("Alicia", Assert.Single(await SearchAsync("ali")).Name);
        Assert.Equal(2, (await SearchAsync("ali", includeInactive: true)).Count);
    }

    /// <summary>Inserts a conflicting customer through a raw connection right before the context saves.</summary>
    private sealed class RacingInsertFactory(string connectionString, string phone) : IDbContextFactory<CafeDbContext>
    {
        private sealed class InsertBeforeSave(string connectionString, string phone) : SaveChangesInterceptor
        {
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO Customers (Name, Phone, IsMember, IsActive, CreatedAtUtc) VALUES ('Racer', $p, 0, 1, '2026-01-01 00:00:00')";
                command.Parameters.AddWithValue("$p", phone);
                command.ExecuteNonQuery();
                return ValueTask.FromResult(result);
            }
        }

        public CafeDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CafeDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new InsertBeforeSave(connectionString, phone))
                .Options);
    }
}
