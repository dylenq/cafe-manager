using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Products;

public class ProductService(IDbContextFactory<CafeDbContext> dbFactory)
{
    public async Task<ServiceResult<Product>> CreateAsync(ProductInput input, CancellationToken cancellationToken = default)
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

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = new Product
        {
            Name = name, Category = category, Price = input.Price, IsActive = input.IsActive, SortOrder = input.SortOrder,
        };
        db.Products.Add(product);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
        {
            var message = $"A product named \"{name}\" already exists.";
            return ServiceResult<Product>.Fail(message, new Dictionary<string, string> { ["Name"] = message });
        }

        return ServiceResult<Product>.Ok(product);
    }
}
