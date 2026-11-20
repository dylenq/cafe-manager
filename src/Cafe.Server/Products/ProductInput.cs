using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Products;

public class ProductInput : IValidatableObject
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100, ErrorMessage = "Name can be at most 100 characters.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Category is required.")]
    [StringLength(50, ErrorMessage = "Category can be at most 50 characters.")]
    public string Category { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Price must be between 0 and 100,000,000.")]
    public decimal Price { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>Money is stored with two decimals, so a price with more is rejected rather than silently rounded.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Price != Math.Round(Price, 2))
        {
            yield return new ValidationResult("Price can have at most 2 decimal places.", [nameof(Price)]);
        }
    }
}
