using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Customers;

public class TimePackageInput : IValidatableObject
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100, ErrorMessage = "Name must be 100 characters or fewer.")]
    public string Name { get; set; } = string.Empty;

    [Range(1, 10000, ErrorMessage = "Minutes must be between 1 and 10000.")]
    public int Minutes { get; set; }

    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Price must be zero or more.")]
    public decimal Price { get; set; }

    /// <summary>Null means the package is usable on any station type.</summary>
    public int? StationTypeId { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Money is stored with two decimals, so a price with more is rejected rather than silently rounded.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Price != Math.Round(Price, 2))
        {
            yield return new ValidationResult("Price can have at most 2 decimal places.", [nameof(Price)]);
        }
    }
}
