namespace Cafe.Server.Data;

/// <summary>A cafe customer. Soft-deleted via IsActive so history keeps its references.</summary>
public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Digits only; unique when not null.</summary>
    public string? Phone { get; set; }

    public bool IsMember { get; set; }

    public string? Notes { get; set; }

    /// <summary>Defaults to true in C# only (no SQL default).</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
}
