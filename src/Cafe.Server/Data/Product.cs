namespace Cafe.Server.Data;

/// <summary>A food or drink item on sale at the counter.</summary>
public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public decimal Price { get; set; }

    /// <summary>Defaults to true in C# only (no SQL default).</summary>
    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }
}
