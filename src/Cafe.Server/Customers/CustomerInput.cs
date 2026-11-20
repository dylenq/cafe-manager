using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Customers;

public class CustomerInput
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100, ErrorMessage = "Name must be 100 characters or fewer.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(40, ErrorMessage = "Phone must be 40 characters or fewer.")]
    public string? Phone { get; set; }

    public bool IsMember { get; set; }

    [StringLength(500, ErrorMessage = "Notes must be 500 characters or fewer.")]
    public string? Notes { get; set; }
}
