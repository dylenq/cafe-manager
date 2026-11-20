namespace Cafe.Server.Orders;

/// <summary>What the POS order is attached to.</summary>
public enum AttachKind
{
    WalkIn,
    Session,
    Customer,
}

public static class AttachRules
{
    public const string ChooseSession = "Choose a session, or switch to Walk-in.";
    public const string ChooseCustomer = "Choose a customer, or switch to Walk-in.";

    /// <summary>
    /// Null when the order may be charged. Selecting Session or Customer without choosing one is an error, so a sale
    /// meant for a tab is never silently rung up as a walk-in.
    /// </summary>
    public static string? ChargeError(AttachKind kind, bool hasTarget) => hasTarget
        ? null
        : kind switch
        {
            AttachKind.Session => ChooseSession,
            AttachKind.Customer => ChooseCustomer,
            _ => null,
        };
}
