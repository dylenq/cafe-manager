using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Customers;

public class CustomerService(IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time)
{
    public const string Gone = "That customer no longer exists. Refresh the list.";

    public async Task<ServiceResult<Customer>> CreateAsync(CustomerInput input, CancellationToken cancellationToken = default)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return ServiceResult<Customer>.Fail(InputValidation.Summarise(errors), errors);
        }

        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            return ServiceResult<Customer>.Fail("Name is required.", new Dictionary<string, string> { ["Name"] = "Name is required." });
        }

        var phone = NormalizePhone(input.Phone);
        if (phone is { Length: > 20 })
        {
            return ServiceResult<Customer>.Fail("Phone must have 20 digits or fewer.",
                new Dictionary<string, string> { ["Phone"] = "Phone must have 20 digits or fewer." });
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (phone is not null && await db.Customers.AnyAsync(c => c.Phone == phone, cancellationToken))
        {
            return DuplicatePhone();
        }

        var entity = new Customer
        {
            Name = name,
            Phone = phone,
            IsMember = input.IsMember,
            Notes = InputValidation.Clean(input.Notes),
            CreatedAtUtc = time.GetUtcNow(),
        };
        db.Customers.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return DuplicatePhone();
        }

        return ServiceResult<Customer>.Ok(entity);
    }

    public async Task<Customer?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    /// <summary>Digits only; empty becomes null.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (phone is null)
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>Loads a tracked customer that sessions and sales may link to: NotFound when absent, a failure when inactive.</summary>
    internal static async Task<ServiceResult<Customer>> LoadLinkableAsync(CafeDbContext db, int customerId, CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null)
        {
            return ServiceResult<Customer>.NotFound(Gone);
        }

        return customer.IsActive
            ? ServiceResult<Customer>.Ok(customer)
            : ServiceResult<Customer>.Fail("That customer is inactive. Reactivate them first.");
    }

    private static ServiceResult<Customer> DuplicatePhone() =>
        ServiceResult<Customer>.Fail("A customer with that phone number already exists.",
            new Dictionary<string, string> { ["Phone"] = "A customer with that phone number already exists." });
}
