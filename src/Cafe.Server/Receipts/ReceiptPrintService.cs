using Cafe.Server.Data;
using Cafe.Server.Pricing;

namespace Cafe.Server.Receipts;

public enum PrintOutcomeKind
{
    Skipped,
    BrowserUrl,
    Printed,
    Failed,
}

/// <summary>Result of a print request. Url is set for BrowserUrl; Error for Failed; Message is informational.</summary>
public sealed record PrintOutcome(PrintOutcomeKind Kind, string? Url = null, string? Error = null, string? Message = null);

/// <summary>
/// Dispatches receipts by the configured mode. Never throws and never touches a payment: printing happens after
/// the payment is stored, so a printer problem can only ever produce a Failed outcome.
/// </summary>
public class ReceiptPrintService(
    ReceiptService receipts,
    ReceiptSettingsService settingsService,
    EscPosReceiptRenderer renderer,
    INetworkReceiptPrinter network,
    IWindowsReceiptPrinter windows,
    PricingConfigService pricing,
    TimeProvider time,
    ILogger<ReceiptPrintService> logger)
{
    public const string BrowserTestMessage =
        "Browser mode prints from the receipt page after a payment; choose Network or Windows printer to test a device.";

    public const string OffMessage = "Receipt printing is turned off.";

    public async Task<PrintOutcome> PrintAsync(
        int paymentId, decimal? changeGiven = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await settingsService.GetAsync(cancellationToken);
            if (settings.Mode == ReceiptMode.None)
            {
                return new PrintOutcome(PrintOutcomeKind.Skipped, Message: OffMessage);
            }

            var result = await receipts.GetAsync(paymentId, changeGiven, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                return new PrintOutcome(PrintOutcomeKind.Failed, Error: result.Error ?? ReceiptService.NotFoundMessage);
            }

            var doc = result.Value;
            if (settings.Mode == ReceiptMode.Browser)
            {
                return new PrintOutcome(PrintOutcomeKind.BrowserUrl, Url: $"/receipts/{doc.RootPaymentId}?print=1");
            }

            return await SendAsync(renderer.Render(doc, settings.Columns), settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Printing receipt for payment {PaymentId} failed.", paymentId);
            return new PrintOutcome(PrintOutcomeKind.Failed, Error: "Printing failed unexpectedly. The payment was saved.");
        }
    }

    public async Task<PrintOutcome> PrintTestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await settingsService.GetAsync(cancellationToken);
            if (settings.Mode == ReceiptMode.None)
            {
                return new PrintOutcome(PrintOutcomeKind.Skipped, Message: OffMessage);
            }

            if (settings.Mode == ReceiptMode.Browser)
            {
                return new PrintOutcome(PrintOutcomeKind.Skipped, Message: BrowserTestMessage);
            }

            var target = new ReceiptTarget(
                ReceiptTargetKind.Order, "TEST RECEIPT", null, 35000m, 35000m, 0m,
                [new ReceiptItem("Sample Coffee", 2, 15000m, 30000m), new ReceiptItem("Sample Snack", 1, 5000m, 5000m)]);
            var doc = ReceiptBuilder.Build(new ReceiptSource(
                0, time.GetUtcNow(), pricing.TimeZone, [target], [new ReceiptPayment(PaymentMethod.Cash, 35000m)], null,
                new ReceiptHeader(settings.CafeName, new[] { settings.Address, settings.Phone }.Where(l => l.Length > 0).ToList(), settings.Footer)));
            return await SendAsync(renderer.Render(doc, settings.Columns), settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Printing the test receipt failed.");
            return new PrintOutcome(PrintOutcomeKind.Failed, Error: "Printing failed unexpectedly.");
        }
    }

    private async Task<PrintOutcome> SendAsync(byte[] bytes, ReceiptSettings settings, CancellationToken ct)
    {
        if (settings.Mode == ReceiptMode.WindowsPrinter)
        {
            var only = await SafeSendAsync(windows, bytes, settings, ct);
            return only.Success
                ? new PrintOutcome(PrintOutcomeKind.Printed)
                : new PrintOutcome(PrintOutcomeKind.Failed, Error: only.Error);
        }

        var first = await SafeSendAsync(network, bytes, settings, ct);
        if (first.Success)
        {
            return new PrintOutcome(PrintOutcomeKind.Printed);
        }

        if (settings.WindowsPrinter.Length == 0)
        {
            return new PrintOutcome(PrintOutcomeKind.Failed, Error: first.Error);
        }

        var second = await SafeSendAsync(windows, bytes, settings, ct);
        return second.Success
            ? new PrintOutcome(PrintOutcomeKind.Printed)
            : new PrintOutcome(
                PrintOutcomeKind.Failed,
                Error: $"Network printer: {first.Error} Windows printer '{settings.WindowsPrinter}': {second.Error}");
    }

    private async Task<PrintResult> SafeSendAsync(IReceiptPrinter printer, byte[] bytes, ReceiptSettings settings, CancellationToken ct)
    {
        try
        {
            return await printer.SendAsync(bytes, settings, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A receipt printer threw.");
            return PrintResult.Fail("The printer failed unexpectedly.");
        }
    }
}
