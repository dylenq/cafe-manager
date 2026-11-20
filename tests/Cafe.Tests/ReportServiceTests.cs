using Cafe.Server.Data;
using Cafe.Server.Reports;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>Pure ReportService.Build tests plus a few DB loader tests.</summary>
public class ReportServiceTests : ReportTestBase
{
    private static readonly TimeZoneInfo Vn = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd");

    private static DateTimeOffset U(string s) => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static ReportRange R(string from, string to) => new(D(from), D(to));

    private static PaymentFact Pay(int id, PaymentMethod m, string at, params (AllocationTargetType T, int Id, decimal A)[] allocs) =>
        new(id, m, U(at), allocs.Select(a => new AllocationFact(a.T, a.Id, a.A)).ToList());

    private static ReportData Build(ReportInput input, ReportRange range, TimeZoneInfo? zone = null) =>
        ReportService.Build(input, range, zone ?? TimeZoneInfo.Utc);

    private static ReportInput Payments(params PaymentFact[] p) => new(p);

    [Fact]
    public void Local_day_boundary_uses_the_cafe_zone()
    {
        var input = Payments(
            Pay(1, PaymentMethod.Cash, "2025-01-31T16:30:00Z", (AllocationTargetType.Session, 1, 100m)), // 23:30 local 31 Jan
            Pay(2, PaymentMethod.Cash, "2025-01-31T17:00:00Z", (AllocationTargetType.Session, 1, 10m)));  // 00:00 local 1 Feb

        var data = Build(input, R("2025-01-31", "2025-02-01"), Vn);

        Assert.Equal(100m, data.Days.Single(d => d.Date == D("2025-01-31")).Collected);
        Assert.Equal(10m, data.Days.Single(d => d.Date == D("2025-02-01")).Collected);
        Assert.Equal(110m, data.Summary.Collected);
    }

    [Fact]
    public void Payments_outside_the_range_are_ignored()
    {
        var input = Payments(
            Pay(1, PaymentMethod.Cash, "2025-01-31T16:59:00Z", (AllocationTargetType.Session, 1, 100m)),
            Pay(2, PaymentMethod.Cash, "2025-02-01T16:59:00Z", (AllocationTargetType.Session, 1, 7m)),
            Pay(3, PaymentMethod.Cash, "2025-02-01T17:00:00Z", (AllocationTargetType.Session, 1, 999m)));

        var data = Build(input, R("2025-02-01", "2025-02-01"), Vn);

        Assert.Equal(7m, data.Summary.Collected);
    }

    [Fact]
    public void Weeks_start_on_monday_and_split_at_the_sunday_boundary()
    {
        var input = Payments(
            Pay(1, PaymentMethod.Cash, "2025-02-02T10:00:00Z", (AllocationTargetType.Session, 1, 100m)), // Sunday, week 05
            Pay(2, PaymentMethod.Cash, "2025-02-03T10:00:00Z", (AllocationTargetType.Session, 1, 40m)));  // Monday, week 06

        var data = Build(input, R("2025-01-27", "2025-02-09"));

        Assert.Equal(2, data.Weeks.Count);
        Assert.Equal(new WeekRow(D("2025-01-27"), "2025-W05", 100m), data.Weeks[0]);
        Assert.Equal(new WeekRow(D("2025-02-03"), "2025-W06", 40m), data.Weeks[1]);
    }

    [Fact]
    public void Month_range_includes_the_partial_first_week_and_zero_filled_weeks()
    {
        var input = Payments(
            Pay(1, PaymentMethod.Cash, "2025-01-30T10:00:00Z", (AllocationTargetType.Session, 1, 500m)), // January, outside
            Pay(2, PaymentMethod.Cash, "2025-02-02T10:00:00Z", (AllocationTargetType.Session, 1, 20m)));

        var data = Build(input, R("2025-02-01", "2025-02-28"));

        Assert.Equal(5, data.Weeks.Count); // Mon 01-27, 02-03, 02-10, 02-17, 02-24
        Assert.Equal(D("2025-01-27"), data.Weeks[0].WeekStart);
        Assert.Equal(20m, data.Weeks[0].Collected);
        Assert.All(data.Weeks.Skip(1), w => Assert.Equal(0m, w.Collected));
        Assert.Equal(28, data.Days.Count);
    }

    [Fact]
    public void Empty_range_returns_zeros_without_error()
    {
        var data = Build(Payments(), R("2025-02-03", "2025-02-05"));

        Assert.Equal(new ReportSummary(0m, 0m, 0m, 0, 0m, 0m, 0m), data.Summary);
        Assert.Equal(3, data.Days.Count);
        Assert.All(data.Days, d => Assert.Equal(0m, d.Collected));
        Assert.Single(data.Weeks);
        Assert.Empty(data.StationTypes);
        Assert.Empty(data.Products);
        Assert.Empty(data.Categories);
        Assert.Equal(2, data.Methods.Count);
        Assert.All(data.Methods, m => Assert.Equal(0m, m.Collected));
    }

    [Fact]
    public void Split_payment_counts_each_method_once()
    {
        var input = Payments(
            Pay(1, PaymentMethod.Cash, "2025-02-03T10:00:00Z", (AllocationTargetType.Session, 1, 6000m), (AllocationTargetType.Order, 1, 24000m)),
            Pay(2, PaymentMethod.Card, "2025-02-03T10:00:00Z", (AllocationTargetType.Order, 1, 26000m)));

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        Assert.Equal(56000m, data.Summary.Collected);
        Assert.Equal(30000m, data.Summary.Cash);
        Assert.Equal(26000m, data.Summary.Card);
        Assert.Equal(30000m, data.Methods.Single(m => m.Method == PaymentMethod.Cash).Collected);
        Assert.Equal(26000m, data.Methods.Single(m => m.Method == PaymentMethod.Card).Collected);
        Assert.Equal(56000m, data.Days.Single().Collected);
    }

    private static OrderFact Order(int id, params (string Name, string Cat, int Qty, decimal Total)[] lines) =>
        new(id, lines.Sum(l => l.Total), lines.Select(l => new OrderLineFact(l.Name, l.Cat, l.Qty, l.Total)).ToList());

    [Fact]
    public void Partial_payment_is_split_across_lines_pro_rata()
    {
        var input = new ReportInput(
            [Pay(1, PaymentMethod.Cash, "2025-02-03T10:00:00Z", (AllocationTargetType.Order, 1, 50m))],
            Orders: [Order(1, ("A", "Food", 1, 60m), ("B", "Drinks", 1, 40m))]);

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        Assert.Equal(30m, data.Products.Single(p => p.Name == "A").Revenue);
        Assert.Equal(20m, data.Products.Single(p => p.Name == "B").Revenue);
        Assert.Equal(50m, data.Products.Sum(p => p.Revenue));
        Assert.Equal(0.5m, data.Products.Single(p => p.Name == "A").Quantity);
    }

    [Theory]
    [InlineData("33.33")]
    [InlineData("100")]
    [InlineData("0.01")]
    public void Split_remainder_goes_to_the_last_line_so_lines_sum_exactly(string allocation)
    {
        var amount = decimal.Parse(allocation, System.Globalization.CultureInfo.InvariantCulture);
        var input = new ReportInput(
            [Pay(1, PaymentMethod.Cash, "2025-02-03T10:00:00Z", (AllocationTargetType.Order, 1, amount))],
            Orders: [Order(1, ("X", "Food", 1, 1m), ("Y", "Food", 1, 1m), ("Z", "Food", 1, 1m))]);

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        Assert.Equal(amount, data.Products.Sum(p => p.Revenue));
        Assert.Equal(amount, data.Categories.Single().Revenue);
    }

    [Fact]
    public void Fully_paid_order_shows_whole_quantities()
    {
        var input = new ReportInput(
            [Pay(1, PaymentMethod.Cash, "2025-02-03T10:00:00Z", (AllocationTargetType.Order, 1, 50000m))],
            Orders: [Order(1, ("Coffee", "Drinks", 2, 50000m))]);

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        var row = Assert.Single(data.Products);
        Assert.Equal(2m, row.Quantity);
        Assert.Equal(50000m, row.Revenue);
    }

    [Fact]
    public void Two_payments_on_different_days_attribute_their_own_share_and_never_exceed_the_line()
    {
        var order = Order(1, ("A", "Food", 3, 90m), ("B", "Drinks", 1, 10m));
        var first = Pay(1, PaymentMethod.Cash, "2025-02-03T10:00:00Z", (AllocationTargetType.Order, 1, 33.33m));
        var second = Pay(2, PaymentMethod.Cash, "2025-02-04T10:00:00Z", (AllocationTargetType.Order, 1, 66.67m));
        var all = new ReportInput([first, second], Orders: [order]);

        var day1 = Build(all, R("2025-02-03", "2025-02-03"));
        var day2 = Build(all, R("2025-02-04", "2025-02-04"));
        var both = Build(all, R("2025-02-03", "2025-02-04"));

        Assert.Equal(33.33m, day1.Products.Sum(p => p.Revenue));
        Assert.Equal(66.67m, day2.Products.Sum(p => p.Revenue));
        Assert.Equal(100m, both.Products.Sum(p => p.Revenue));
        Assert.Equal(90m, both.Products.Single(p => p.Name == "A").Revenue);
        Assert.Equal(3m, both.Products.Single(p => p.Name == "A").Quantity);
    }

    [Fact]
    public void Allocations_to_orders_not_in_the_input_are_skipped_for_products_but_still_collected()
    {
        // The loader never passes voided orders, so a payment pointing at one has no order fact.
        var input = new ReportInput([Pay(1, PaymentMethod.Cash, "2025-02-03T10:00:00Z", (AllocationTargetType.Order, 9, 25m))], Orders: []);

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        Assert.Empty(data.Products);
        Assert.Equal(25m, data.Summary.Collected);
    }

    [Fact]
    public void Products_sort_by_revenue_then_name_and_categories_roll_up()
    {
        var input = new ReportInput(
            [Pay(1, PaymentMethod.Cash, "2025-02-03T10:00:00Z", (AllocationTargetType.Order, 1, 130m))],
            Orders: [Order(1, ("Tea", "Drinks", 1, 30m), ("Burger", "Food", 1, 70m), ("Coffee", "Drinks", 1, 30m))]);

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        Assert.Equal(["Burger", "Coffee", "Tea"], data.Products.Select(p => p.Name));
        Assert.Equal(["Food", "Drinks"], data.Categories.Select(c => c.Category));
        Assert.Equal(60m, data.Categories.Single(c => c.Category == "Drinks").Revenue);
    }

    [Fact]
    public void Station_type_groups_session_allocations_and_package_sales_last()
    {
        var input = new ReportInput(
            [
                Pay(1, PaymentMethod.Cash, "2025-02-03T10:00:00Z",
                    (AllocationTargetType.Session, 1, 100m), (AllocationTargetType.Session, 2, 300m), (AllocationTargetType.PackageSale, 1, 900m)),
            ],
            Sessions:
            [
                new SessionFact(1, "Console", 3600, U("2025-02-03T09:00:00Z")),
                new SessionFact(2, "PC", 7200, U("2025-02-03T09:30:00Z")),
            ],
            SessionStationTypes: new Dictionary<int, string> { [1] = "Console", [2] = "PC" });

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        Assert.Equal(["PC", "Console", "Time packages"], data.StationTypes.Select(t => t.Name));
        Assert.Equal(new StationTypeRow("PC", 300m, 2m), data.StationTypes[0]);
        Assert.Equal(new StationTypeRow("Console", 100m, 1m), data.StationTypes[1]);
        Assert.Equal(new StationTypeRow("Time packages", 900m, 0m), data.StationTypes[2]);
    }

    [Fact]
    public void Session_tiles_count_hours_and_average_and_ignore_sessions_outside_the_range()
    {
        var input = new ReportInput(
            [],
            Sessions:
            [
                new SessionFact(1, "PC", 3600, U("2025-02-03T09:00:00Z")),
                new SessionFact(2, "PC", 5400, U("2025-02-03T11:00:00Z")),
                new SessionFact(3, "PC", 3600, U("2025-02-10T11:00:00Z")),
            ]);

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        Assert.Equal(2, data.Summary.SessionCount);
        Assert.Equal(2.5m, data.Summary.SessionHours);
        Assert.Equal(75m, data.Summary.AverageSessionMinutes);
    }

    [Fact]
    public void Charged_unpaid_sums_outstanding_inside_the_range_and_never_goes_negative()
    {
        var input = new ReportInput(
            [],
            Charges:
            [
                new ChargeFact(AllocationTargetType.Session, 1, U("2025-02-03T09:00:00Z"), 6000m, 0m),
                new ChargeFact(AllocationTargetType.Order, 1, U("2025-02-03T09:00:00Z"), 50000m, 20000m),
                new ChargeFact(AllocationTargetType.Order, 2, U("2025-02-03T09:00:00Z"), 100m, 100m),
                new ChargeFact(AllocationTargetType.PackageSale, 1, U("2025-02-03T09:00:00Z"), 10m, 25m),
                new ChargeFact(AllocationTargetType.Order, 3, U("2025-02-09T09:00:00Z"), 777m, 0m),
            ]);

        var data = Build(input, R("2025-02-03", "2025-02-03"));

        Assert.Equal(36000m, data.Summary.ChargedUnpaid);
    }

    [Fact]
    public void Dst_days_land_on_the_right_local_date()
    {
        var input = Payments(
            Pay(1, PaymentMethod.Cash, "2025-03-09T04:59:00Z", (AllocationTargetType.Session, 1, 1m)),  // 23:59 EST on 8 March
            Pay(2, PaymentMethod.Cash, "2025-03-09T05:00:00Z", (AllocationTargetType.Session, 1, 10m))); // 00:00 EST on 9 March

        var data = Build(input, R("2025-03-08", "2025-03-09"), Ny);

        Assert.Equal(1m, data.Days.Single(d => d.Date == D("2025-03-08")).Collected);
        Assert.Equal(10m, data.Days.Single(d => d.Date == D("2025-03-09")).Collected);
        var (start, end) = R("2025-03-09", "2025-03-09").ToUtcBounds(Ny);
        Assert.Equal(TimeSpan.FromHours(23), end - start);
    }

    // --- loader tests (real database) -------------------------------------------------------------------------

    private async Task<ReportData> LoadAsync(string from, string to) =>
        await Svc(sp => sp.GetRequiredService<ReportService>().GetAsync(R(from, to)));

    private string StartDay => DateOnly.FromDateTime(Start.UtcDateTime).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Loader_excludes_voided_orders_from_products_and_unpaid()
    {
        var productId = await ProductAsync("Iced Coffee", 25000m);
        var paid = await AddOrderAsync(Start, OrderStatus.Open, (productId, "Iced Coffee", 2, 25000m));
        await AddOrderAsync(Start, OrderStatus.Voided, (productId, "Iced Coffee", 4, 25000m));
        await AddPaymentAsync(PaymentMethod.Cash, Start, (AllocationTargetType.Order, paid, 20000m));

        var data = await LoadAsync(StartDay, StartDay);

        var product = Assert.Single(data.Products);
        Assert.Equal(20000m, product.Revenue);
        Assert.Equal("Drinks", product.Category);
        Assert.Equal(30000m, data.Summary.ChargedUnpaid); // only the open order's remainder, nothing from the voided one
    }

    [Fact]
    public async Task Loader_ignores_sessions_ended_before_billing_start_in_unpaid()
    {
        var station = await StationAsync("PC-01");
        await AddEndedSessionAsync(6000m, (station, Start.AddHours(-3), Start.AddHours(-2)));
        await AddEndedSessionAsync(9000m, (station, Start.AddHours(-1), Start.AddMinutes(-30)));
        await SetBillingStartAsync(Start.AddHours(-1.5));

        var data = await LoadAsync(StartDay, StartDay);

        Assert.Equal(9000m, data.Summary.ChargedUnpaid); // the 6000 session predates billing
        Assert.Equal(2, data.Summary.SessionCount);      // but both still count as sessions
    }

    [Fact]
    public async Task Loader_groups_a_moved_session_by_its_first_segment_station_type()
    {
        var consoleType = await TypeAsync("Console", 8000m);
        var pcType = await TypeAsync("PC", 6000m);
        var console = await StationAsync("TV-01", consoleType);
        var pc = await StationAsync("PC-01", pcType);
        var sessionId = await AddEndedSessionAsync(
            14000m,
            (console, Start.AddHours(-2), Start.AddHours(-1)),
            (pc, Start.AddHours(-1), Start));
        await AddPaymentAsync(PaymentMethod.Cash, Start, (AllocationTargetType.Session, sessionId, 14000m));

        var data = await LoadAsync(StartDay, StartDay);

        var row = Assert.Single(data.StationTypes);
        Assert.Equal("Console", row.Name);
        Assert.Equal(14000m, row.Collected);
        Assert.Equal(2m, row.Hours);
        Assert.Equal(0m, data.Summary.ChargedUnpaid);
    }

    [Fact]
    public async Task Loader_uses_the_configured_cafe_time_zone_for_the_local_day()
    {
        Factory.ExtraSettings["Cafe:TimeZone"] = "Asia/Ho_Chi_Minh";
        var late = new DateTimeOffset(2025, 1, 31, 16, 30, 0, TimeSpan.Zero); // 23:30 local on 31 January
        await AddPaymentAsync(PaymentMethod.Cash, late, (AllocationTargetType.Session, 1, 100m));

        var data = await LoadAsync("2025-01-31", "2025-02-01");

        Assert.Equal(100m, data.Days.Single(d => d.Date == D("2025-01-31")).Collected);
        Assert.Equal(0m, data.Days.Single(d => d.Date == D("2025-02-01")).Collected);
    }

    [Fact]
    public async Task Loader_returns_a_package_sale_row_and_counts_its_unpaid_remainder()
    {
        var customer = await CustomerAsync("Alice");
        var package = await PackageAsync("5h", 300, 50000m);
        var sale = await AddPackageSaleAsync(customer, package, 50000m, Start);
        await AddPaymentAsync(PaymentMethod.Card, Start, (AllocationTargetType.PackageSale, sale, 30000m));

        var data = await LoadAsync(StartDay, StartDay);

        var row = Assert.Single(data.StationTypes);
        Assert.Equal("Time packages", row.Name);
        Assert.Equal(30000m, row.Collected);
        Assert.Equal(30000m, data.Summary.Card);
        Assert.Equal(20000m, data.Summary.ChargedUnpaid);
    }
}
