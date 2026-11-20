namespace Cafe.Server.Data;

public class Session
{
    public int Id { get; set; }

    public int StationId { get; set; }

    public Station Station { get; set; } = null!;

    public SessionMode Mode { get; set; }

    public SessionStatus Status { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>Prepaid only; extensions add to it.</summary>
    public int? PurchasedMinutes { get; set; }

    public DateTimeOffset? EndedAtUtc { get; set; }

    public SessionEndReason? EndReason { get; set; }

    /// <summary>Stored as TEXT so decimals round-trip exactly.</summary>
    public decimal ChargedAmount { get; set; }

    public string? Notes { get; set; }

    /// <summary>The instant remaining time reached zero, so a restart can resolve Grace from the wall clock.</summary>
    public DateTimeOffset? GraceStartedAtUtc { get; set; }

    /// <summary>The linked customer, if any.</summary>
    public int? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    /// <summary>Snapshot of Customer.IsMember at start; never updated afterwards.</summary>
    public bool IsMemberPricing { get; set; }

    /// <summary>Concurrency token incremented by every mutation.</summary>
    public int Version { get; set; }

    public ICollection<SessionSegment> Segments { get; set; } = new List<SessionSegment>();

    public ICollection<SessionEvent> Events { get; set; } = new List<SessionEvent>();
}
