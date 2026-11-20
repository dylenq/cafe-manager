using Cafe.Server.Payments;

namespace Cafe.Server.Components.Shared;

/// <summary>What a caller's confirm callback returns to <see cref="PaymentDialog"/>.</summary>
public sealed record PaymentDialogResult(bool Success, string? Error, SettleResult? Settlement);
