namespace Cafe.Server.Data;

/// <summary>Why a ledger row exists. Explicit values: they are stored as integers.</summary>
public enum LedgerReason
{
    PackagePurchase = 0,
    SessionDebit = 1,
    SessionRefund = 2,
    ManualAdjustment = 3,
}

public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
}

/// <summary>Who a rate rule applies to (member pricing).</summary>
public enum RateRuleAudience
{
    Everyone = 0,
    MembersOnly = 1,
    GuestsOnly = 2,
}
