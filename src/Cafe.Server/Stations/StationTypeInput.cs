using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Stations;

public class StationTypeInput
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100, ErrorMessage = "Name must be 100 characters or fewer.")]
    public string Name { get; set; } = string.Empty;

    [Range(0.0, 1000000.0, ErrorMessage = "Hourly rate must be 0 or more.")]
    public decimal HourlyRate { get; set; }

    public int SortOrder { get; set; }
}
