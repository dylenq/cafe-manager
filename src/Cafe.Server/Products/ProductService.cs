using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Products;

/// <summary>Product catalogue (POS-01, D-01). Names are unique case-insensitively; ordered products are deactivated, never deleted.</summary>
public class ProductService(IDbContextFactory<CafeDbContext> dbFactory)
{
    private const string Gone = "That product no longer exists. Refresh the list.";

    public async Task<Product?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    /// <summary>Ordered by category, sort order, then name (sorted in memory so the order is culture aware).</summary>
    public async Task<List<Product>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Products.AsNoTracking().Where(p => includeInactive || p.IsActive).ToListAsync(cancellationToken);
        return rows
            .OrderBy(p => p.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Id)
            .ToList();
    }

    public Task<List<Product>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        ListAsync(includeInactive: false, cancellationToken);

    /// <summary>Distinct categories of the active products, sorted.</summary>
    public async Task<List<string>> CategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var categories = await db.Products.AsNoTracking().Where(p => p.IsActive).Select(p => p.Category).Distinct().ToListAsync(cancellationToken);
        return categories.OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<ServiceResult<Product>> CreateAsync(ProductInput input, CancellationToken cancellationToken = default)
    {
        if (Check(input) is { } invalid)
        {
            return invalid;
        }

        var name = input.Name.Trim();
        var category = input.Category.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await NameTakenAsync(db, name, null, cancellationToken))
        {
            return Duplicate(name);
        }

        var product = new Product
        {
            Name = name, Category = category, Price = input.Price, IsActive = input.IsActive, SortOrder = input.SortOrder,
        };
        db.Products.Add(product);
        return await SaveAsync(db, product, name, cancellationToken);
    }

    public async Task<ServiceResult<Product>> UpdateAsync(int id, ProductInput input, CancellationToken cancellationToken = default)
    {
        if (Check(input) is { } invalid)
        {
            return invalid;
        }

        var name = input.Name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return ServiceResult<Product>.NotFound(Gone);
        }

        if (await NameTakenAsync(db, name, id, cancellationToken))
        {
            return Duplicate(name);
        }

        product.Name = name;
        product.Category = input.Category.Trim();
        product.Price = input.Price;
        product.IsActive = input.IsActive;
        product.SortOrder = input.SortOrder;
        return await SaveAsync(db, product, name, cancellationToken);
    }

    public async Task<ServiceResult> SetActiveAsync(int id, bool active, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        product.IsActive = active;
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Deletes a product that was never ordered; an ordered one must be deactivated instead (D-01).</summary>
    public async Task<ServiceResult> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        const string Ordered = "This product has been ordered, so it cannot be deleted. Deactivate it instead.";
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return ServiceResult.NotFound(Gone);
        }

        if (await db.OrderLines.AnyAsync(l => l.ProductId == id, cancellationToken))
        {
            return ServiceResult.Fail(Ordered);
        }

        db.Products.Remove(product);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (InputValidation.IsForeignKeyViolation(ex))
        {
            return ServiceResult.Fail(Ordered);
        }

        return ServiceResult.Ok();
    }

    private static ServiceResult<Product>? Check(ProductInput input)
    {
        if (InputValidation.Validate(input) is { } errors)
        {
            return ServiceResult<Product>.Fail(InputValidation.Summarise(errors), errors);
        }

        var name = input.Name.Trim();
        var category = input.Category.Trim();
        if (name.Length == 0 || category.Length == 0)
        {
            var field = name.Length == 0 ? "Name" : "Category";
            return ServiceResult<Product>.Fail($"{field} is required.", new Dictionary<string, string> { [field] = $"{field} is required." });
        }

        return null;
    }

    // The column is NOCASE, so Name == name compares case-insensitively in SQLite.
    private static Task<bool> NameTakenAsync(CafeDbContext db, string name, int? exceptId, CancellationToken ct) =>
        db.Products.AnyAsync(p => p.Name == name && (exceptId == null || p.Id != exceptId), ct);

    private static async Task<ServiceResult<Product>> SaveAsync(CafeDbContext db, Product product, string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            return Duplicate(name);
        }

        return ServiceResult<Product>.Ok(product);
    }

    private static ServiceResult<Product> Duplicate(string name)
    {
        var message = $"A product named \"{name}\" already exists.";
        return ServiceResult<Product>.Fail(message, new Dictionary<string, string> { ["Name"] = message });
    }
}
