using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Products;

public class ProductInput
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
}
