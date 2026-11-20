using System.Net;
using System.Net.Sockets;
using Cafe.Server.Data;
using Cafe.Server.Payments;
using Cafe.Server.Receipts;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cafe.Tests;

public class ReceiptPrintServiceTests : PosTestBase
{
    private sealed class ThrowingPrinter : INetworkReceiptPrinter, IWindowsReceiptPrinter
    {
        public Task<PrintResult> SendAsync(byte[] data, ReceiptSettings settings, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private static ReceiptSettingsInput Input(ReceiptMode mode, string host = "", int port = 9100, string windows = "", int columns = 42) =>
        new() { Mode = mode, Host = host, Port = port, WindowsPrinter = windows, Columns = columns, CafeName = "Pixel Den", Footer = "Bye" };

    private async Task SaveAsync(ReceiptSettingsInput input)
    {
        var saved = await Svc(sp => sp.GetRequiredService<ReceiptSettingsService>().SaveAsync(input));
        Assert.True(saved.Success, saved.Error);
    }

    private async Task<int> PaidReceiptAsync()
    {
        var stationId = await StationAsync("PC-01");
        var coffee = await ProductAsync("Iced Coffee", 25000m);
        var session = await PrepaidAsync(stationId, 60);
        await OrderAsync(session.Id, null, (coffee, 2));
        var quote = (await Svc(sp => sp.GetRequiredService<SessionService>().QuoteStopAsync(session.Id))).Value!;
        var due = quote.Amount + 50000m;
        var done = await Svc(sp => sp.GetRequiredService<CheckoutService>().StopAndSettleAsync(
            session.Id, quote.Version, quote.Amount,
            [new TenderLine(PaymentMethod.Cash, 40000m), new TenderLine(PaymentMethod.Card, due - 40000m)], due));
        Assert.True(done.Success, done.Error);
        return done.Value!.Settlement!.PaymentIds[1];
    }

    private Task<PrintOutcome> PrintAsync(int id, decimal? change = null) =>
        Svc(sp => sp.GetRequiredService<ReceiptPrintService>().PrintAsync(id, change));

    private async Task<(int Payments, int Allocations)> CountsAsync()
    {
        await using var db = await DbAsync();
        return (await db.Payments.CountAsync(), await db.PaymentAllocations.CountAsync());
    }

    // ---- settings ----

    [Fact]
    public async Task Settings_default_then_round_trip()
    {
        var service = Factory.Services.GetRequiredService<ReceiptSettingsService>();
        var d = await service.GetAsync();
        Assert.Equal(ReceiptMode.Browser, d.Mode);
        Assert.Equal(9100, d.Port);
        Assert.Equal(42, d.Columns);
        Assert.Equal("Thank you!", d.Footer);

        await SaveAsync(new ReceiptSettingsInput
        {
            Mode = ReceiptMode.Network, Host = "192.168.1.50", Port = 9101, WindowsPrinter = "POS-80", Columns = 32,
            CafeName = "Pixel Den", Address = "1 Main St", Phone = "555", Footer = "Bye",
        });

        var s = await service.GetAsync();
        Assert.Equal(new ReceiptSettings(ReceiptMode.Network, "192.168.1.50", 9101, "POS-80", 32, "Pixel Den", "1 Main St", "555", "Bye"), s);
        await using var db = await DbAsync();
        Assert.True(await db.Settings.AnyAsync(x => x.Key == "Receipts:Mode" && x.Value == "Network"));
    }

    [Theory]
    [InlineData(0, 42, "Browser", "", "Port")]
    [InlineData(70000, 42, "Browser", "", "Port")]
    [InlineData(9100, 40, "Browser", "", "Columns")]
    [InlineData(9100, 42, "Network", "", "Host")]
    [InlineData(9100, 42, "Browser", "bad host!", "Host")]
    public async Task Invalid_settings_are_rejected_and_nothing_is_stored(int port, int columns, string mode, string host, string field)
    {
        var input = Input(Enum.Parse<ReceiptMode>(mode), host, port, columns: columns);

        var result = await Svc(sp => sp.GetRequiredService<ReceiptSettingsService>().SaveAsync(input));

        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey(field), string.Join(",", result.FieldErrors.Keys));
        await using var db = await DbAsync();
        Assert.False(await db.Settings.AnyAsync(s => s.Key.StartsWith("Receipts:")));
    }

    [Fact]
    public async Task Unknown_mode_and_overlong_fields_are_rejected()
    {
        var bad = Input(ReceiptMode.Browser);
        bad.Mode = (ReceiptMode)99;
        bad.CafeName = new string('x', 61);

        var result = await Svc(sp => sp.GetRequiredService<ReceiptSettingsService>().SaveAsync(bad));

        Assert.False(result.Success);
        Assert.Contains("Mode", result.FieldErrors.Keys);
        Assert.Contains("CafeName", result.FieldErrors.Keys);
    }

    // ---- dispatch ----

    [Fact]
    public async Task None_mode_skips_and_browser_mode_returns_the_root_url_even_for_a_sibling_id()
    {
        var sibling = await PaidReceiptAsync();
        await SaveAsync(Input(ReceiptMode.None));
        Assert.Equal(PrintOutcomeKind.Skipped, (await PrintAsync(sibling)).Kind);

        await SaveAsync(Input(ReceiptMode.Browser));
        var outcome = await PrintAsync(sibling);

        Assert.Equal(PrintOutcomeKind.BrowserUrl, outcome.Kind);
        Assert.Equal($"/receipts/{sibling - 1}?print=1", outcome.Url);
    }

    [Fact]
    public async Task Network_mode_sends_the_receipt_bytes_to_the_listener()
    {
        var id = await PaidReceiptAsync();
        var (listener, received) = NetworkEscPosPrinterTests.Capture();
        try
        {
            await SaveAsync(Input(ReceiptMode.Network, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port));

            var outcome = await PrintAsync(id, 1000m);

            Assert.Equal(PrintOutcomeKind.Printed, outcome.Kind);
            var bytes = await received.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
            Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, bytes[^4..]);
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            Assert.Contains($"No. {(id - 1):D6}", text);
            Assert.Contains("Pixel Den", text);
            Assert.Contains("Change", text);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<int> DeadPortAsync()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        await Task.CompletedTask;
        return port;
    }

    [Fact]
    public async Task Network_failure_without_a_windows_printer_fails_without_touching_payments()
    {
        var id = await PaidReceiptAsync();
        var before = await CountsAsync();
        await SaveAsync(Input(ReceiptMode.Network, "127.0.0.1", await DeadPortAsync()));

        var outcome = await PrintAsync(id);

        Assert.Equal(PrintOutcomeKind.Failed, outcome.Kind);
        Assert.False(string.IsNullOrEmpty(outcome.Error));
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task Network_failure_with_a_windows_printer_off_windows_mentions_both_attempts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var id = await PaidReceiptAsync();
        await SaveAsync(Input(ReceiptMode.Network, "127.0.0.1", await DeadPortAsync(), windows: "POS-80"));

        var outcome = await PrintAsync(id);

        Assert.Equal(PrintOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("Network printer", outcome.Error);
        Assert.Contains("Windows printer", outcome.Error);
    }

    [Fact]
    public async Task Unknown_payment_id_fails()
    {
        await SaveAsync(Input(ReceiptMode.Network, "127.0.0.1", 9100));

        var outcome = await PrintAsync(4040);

        Assert.Equal(PrintOutcomeKind.Failed, outcome.Kind);
    }

    [Fact]
    public async Task A_throwing_printer_becomes_a_failed_outcome_and_payments_are_unchanged()
    {
        Factory.ServiceOverrides.Add(services =>
        {
            services.RemoveAll<INetworkReceiptPrinter>();
            services.RemoveAll<IWindowsReceiptPrinter>();
            services.AddSingleton<INetworkReceiptPrinter, ThrowingPrinter>();
            services.AddSingleton<IWindowsReceiptPrinter, ThrowingPrinter>();
        });
        var id = await PaidReceiptAsync();
        var before = await CountsAsync();
        await SaveAsync(Input(ReceiptMode.Network, "127.0.0.1", 9100, windows: "POS-80"));

        var outcome = await PrintAsync(id);

        Assert.Equal(PrintOutcomeKind.Failed, outcome.Kind);
        Assert.NotNull(outcome.Error);
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task Test_receipt_is_skipped_in_browser_and_none_modes_and_prints_in_network_mode()
    {
        await SaveAsync(Input(ReceiptMode.Browser));
        var browser = await Svc(sp => sp.GetRequiredService<ReceiptPrintService>().PrintTestAsync());
        Assert.Equal(PrintOutcomeKind.Skipped, browser.Kind);
        Assert.Equal(ReceiptPrintService.BrowserTestMessage, browser.Message);

        await SaveAsync(Input(ReceiptMode.None));
        var off = await Svc(sp => sp.GetRequiredService<ReceiptPrintService>().PrintTestAsync());
        Assert.Equal("Receipt printing is turned off.", off.Message);

        var (listener, received) = NetworkEscPosPrinterTests.Capture();
        try
        {
            await SaveAsync(Input(ReceiptMode.Network, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port));
            var sent = await Svc(sp => sp.GetRequiredService<ReceiptPrintService>().PrintTestAsync());

            Assert.Equal(PrintOutcomeKind.Printed, sent.Kind);
            var text = System.Text.Encoding.Latin1.GetString(await received.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("TEST RECEIPT", text);
            Assert.Contains("Pixel Den", text);
        }
        finally
        {
            listener.Stop();
        }
    }
}
