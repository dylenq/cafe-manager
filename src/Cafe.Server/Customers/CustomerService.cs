using System.Globalization;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Customers;

public class CustomerService(IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time)
{
    public const string Gone = "That customer no longer exists. Refresh the list.";

    public const int MaxPhoneDigits = 20;
    public const int MaxSearchRows = 50;

    public async Task<ServiceResult<Customer>> CreateAsync(CustomerInput input, CancellationToken cancellationToken = default)
    {
        var checkedInput = Check(input);
        if (checkedInput.Failure is { } failure)
        {
            return failure;
        }

        var (name, phone) = (checkedInput.Name!, checkedInput.Phone);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (phone is not null && await PhoneHolderAsync(db, phone, null, cancellationToken) is { } holder)
        {
            return DuplicatePhone(holder.IsActive);
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
            return DuplicatePhone(null);
        }

        return ServiceResult<Customer>.Ok(entity);
    }

    /// <summary>Edits a customer. IsMember changes never touch existing sessions (their IsMemberPricing is a snapshot).</summary>
    public async Task<ServiceResult<Customer>> UpdateAsync(int id, CustomerInput input, CancellationToken cancellationToken = default)
    {
        var checkedInput = Check(input);
        if (checkedInput.Failure is { } failure)
        {
            return failure;
        }

        var (name, phone) = (checkedInput.Name!, checkedInput.Phone);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (entity is null)
        {
            return ServiceResult<Customer>.NotFound(Gone);
        }

        if (phone is not null && await PhoneHolderAsync(db, phone, id, cancellationToken) is { } holder)
        {
            return DuplicatePhone(holder.IsActive);
        }

        entity.Name = name;
        entity.Phone = phone;
        entity.IsMember = input.IsMember;
        entity.Notes = InputValidation.Clean(input.Notes);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return DuplicatePhone(null);
        }

        return ServiceResult<Customer>.Ok(entity);
    }

    /// <summary>Soft delete / reactivate. There is deliberately no hard delete: history keeps its references.</summary>
    public async Task<ServiceResult> SetActiveAsync(int id, bool active, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (entity is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        entity.IsActive = active;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Case-insensitive contains on name (Unicode-safe, done in memory because SQLite LIKE only folds ASCII) or
    /// digits-contains on phone when the term has digits and formatting but no letters. At most 50 rows.
    /// </summary>
    public async Task<List<Customer>> SearchAsync(string? term, bool includeInactive = false, int take = MaxSearchRows, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxSearchRows);
        var trimmed = (term ?? string.Empty).Trim();
        var phoneDigits = trimmed.Length > 0 && !trimmed.Any(char.IsLetter) ? NormalizePhone(trimmed) : null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Customers.AsNoTracking()
            .Where(c => includeInactive || c.IsActive)
            .Select(c => new { c.Id, c.Name, c.Phone })
            .ToListAsync(ct);

        var compare = CultureInfo.InvariantCulture.CompareInfo;
        var ids = rows
            .Where(r => trimmed.Length == 0
                || compare.IndexOf(r.Name, trimmed, CompareOptions.IgnoreCase) >= 0
                || (phoneDigits is not null && r.Phone is not null && r.Phone.Contains(phoneDigits, StringComparison.Ordinal)))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Id)
            .Take(take)
            .Select(r => r.Id)
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var loaded = await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        var byId = loaded.ToDictionary(c => c.Id);
        return ids.Select(i => byId[i]).ToList();
    }

    /// <summary>Everything /customers/{id} shows: balance per bucket, sales, ledger (newest 100) and the 20 latest sessions.</summary>
    public async Task<ServiceResult<CustomerDetail>> GetDetailAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return ServiceResult<CustomerDetail>.NotFound(Gone);
        }

        var balance = await BalanceService.SummaryAsync(db, id, cancellationToken);

        var sales = await (
                from s in db.PackageSales.AsNoTracking()
                where s.CustomerId == id
                join p in db.TimePackages.AsNoTracking() on s.TimePackageId equals p.Id
                orderby s.AtUtc descending, s.Id descending
                select new { s.Id, p.Name, s.Price, s.Minutes, s.StationTypeId, s.AtUtc })
            .ToListAsync(cancellationToken);
        var saleIds = sales.Select(s => s.Id).ToList();
        var methods = await db.Payments.AsNoTracking()
            .Where(p => p.PackageSaleId != null && saleIds.Contains(p.PackageSaleId.Value))
            .Select(p => new { SaleId = p.PackageSaleId!.Value, p.Method })
            .ToListAsync(cancellationToken);
        var methodBySale = methods.GroupBy(m => m.SaleId).ToDictionary(g => g.Key, g => (PaymentMethod?)g.First().Method);
        var saleRows = sales
            .Select(s => new SaleRow(s.Id, s.Name, s.Price, s.Minutes, s.StationTypeId, methodBySale.GetValueOrDefault(s.Id), s.AtUtc))
            .ToList();

        var ledger = await db.CustomerMinuteLedger.AsNoTracking()
            .Where(l => l.CustomerId == id)
            .OrderByDescending(l => l.AtUtc).ThenByDescending(l => l.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        var ledgerRows = ledger
            .Select(l => new LedgerRow(l.Id, l.AtUtc, l.StationTypeId, l.MinutesDelta, l.Reason, ReasonText(l.Reason), l.SessionId, l.Note))
            .ToList();

        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => s.CustomerId == id)
            .OrderByDescending(s => s.StartedAtUtc).ThenByDescending(s => s.Id)
            .Take(20)
            .ToListAsync(cancellationToken);
        var stationNames = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var sessionRows = sessions
            .Select(s => new RecentSessionRow(
                s.Id, stationNames.GetValueOrDefault(s.StationId, "Unknown station"), s.Mode, s.ChargedAmount, s.EndReason, s.StartedAtUtc, s.EndedAtUtc))
            .ToList();

        return ServiceResult<CustomerDetail>.Ok(new CustomerDetail(customer, balance, saleRows, ledgerRows, sessionRows));
    }

    public static string ReasonText(LedgerReason reason) => reason switch
    {
        LedgerReason.PackagePurchase => "Package purchase",
        LedgerReason.SessionDebit => "Session",
        LedgerReason.SessionRefund => "Refund",
        LedgerReason.ManualAdjustment => "Manual adjustment",
        _ => reason.ToString(),
    };

    public async Task<Customer?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    /// <summary>Digits only; no digits becomes null.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (phone is null)
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    private static (ServiceResult<Customer>? Failure, string? Name, string? Phone) Check(CustomerInput input)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return (ServiceResult<Customer>.Fail(InputValidation.Summarise(errors), errors), null, null);
        }

        var name = input.Name.Trim();
        if (name.Length == 0)
        {
            return (FieldFail("Name", "Name is required."), null, null);
        }

        var raw = input.Phone?.Trim();
        var phone = NormalizePhone(raw);
        if (!string.IsNullOrEmpty(raw) && phone is null)
        {
            return (FieldFail("Phone", "Phone must contain digits."), null, null);
        }

        if (phone is { Length: > MaxPhoneDigits })
        {
            return (FieldFail("Phone", "Phone must have 20 digits or fewer."), null, null);
        }

        return (null, name, phone);
    }

    private static ServiceResult<Customer> FieldFail(string field, string message) =>
        ServiceResult<Customer>.Fail(message, new Dictionary<string, string> { [field] = message });

    private static async Task<Customer?> PhoneHolderAsync(CafeDbContext db, string phone, int? exceptId, CancellationToken ct) =>
        await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Phone == phone && (exceptId == null || c.Id != exceptId), ct);

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

    private static ServiceResult<Customer> DuplicatePhone(bool? holderActive)
    {
        var message = holderActive == false
            ? "An inactive customer already has that phone number. Reactivate them instead."
            : "A customer with that phone number already exists.";
        return FieldFail("Phone", message);
    }
}
