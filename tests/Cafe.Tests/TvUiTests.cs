using System.Net;
using System.Text.RegularExpressions;
using Cafe.Server.Data;
using Cafe.Server.Devices;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cafe.Tests;

/// <summary>UI and service behaviour of the TV controls: Stations page, dashboard cards and Settings panel.</summary>
public class TvUiTests : SessionTestBase, IAsyncLifetime
{
    private readonly FakeTvDriver _driver = new();
    private FakeSamsungTv? _tv;

    public async Task InitializeAsync()
    {
        // Real fake TV for the pairing tests; the driver override is only applied by tests that ask for it.
        _tv = await FakeSamsungTv.StartAsync();
        Factory.ExtraSettings["Tv:RemotePort"] = _tv.Port.ToString();
        Factory.ExtraSettings["Tv:RestPort"] = _tv.Port.ToString();
        Factory.ExtraSettings["Tv:UseTls"] = "false";
        Factory.ExtraSettings["Tv:SettleMilliseconds"] = "0";
    }

    public async Task DisposeAsync()
    {
        if (_tv is not null)
        {
            await _tv.DisposeAsync();
        }
    }

    private void UseFakeDriver() =>
        Factory.ServiceOverrides.Add(services =>
        {
            services.RemoveAll<ITvDriver>();
            services.AddSingleton<ITvDriver>(_driver);
        });

    private TvStateCache Cache => Factory.Services.GetRequiredService<TvStateCache>();

    private async Task<int> ConsoleAsync(string name = "PS5-01", string? ip = "127.0.0.1", string? token = null)
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = name, StationTypeId = await TypeAsync(), Kind = StationKind.Console, TvIpAddress = ip,
        });
        Assert.True(created.Success, created.Error);
        if (token is not null)
        {
            await using var db = await DbAsync();
            (await db.Stations.SingleAsync(s => s.Id == created.Value!.Id)).TvToken = token;
            await db.SaveChangesAsync();
        }

        return created.Value!.Id;
    }

    private static int Count(string html, string needle) => Regex.Matches(html, Regex.Escape(needle)).Count;

    // ---- Task 1: Stations page ------------------------------------------------------------------------------

    [Fact]
    public async Task Stations_page_shows_pair_and_test_only_for_consoles_with_a_tv_address()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await ConsoleAsync("PS5-01", "10.0.0.5");
        await ConsoleAsync("PS5-02", null);
        await StationAsync("PC-01");

        var body = await client.GetStringAsync("/stations");

        Assert.Equal(1, Count(body, "tv-pair-button"));
        Assert.Equal(1, Count(body, "tv-test-button"));
        Assert.Contains("Pair TV", body);
        Assert.Contains("Not paired", body);
    }

    [Fact]
    public async Task Stations_page_shows_paired_and_never_the_token()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await ConsoleAsync("PS5-01", "10.0.0.5", token: "SECRET-TOKEN-VALUE-9f3a");

        var body = await client.GetStringAsync("/stations");

        Assert.Contains("Paired", body);
        Assert.Contains("Re-pair TV", body);
        Assert.DoesNotContain("Not paired", body);
        Assert.DoesNotContain("SECRET-TOKEN-VALUE-9f3a", body);

        var home = await client.GetStringAsync("/");
        Assert.DoesNotContain("SECRET-TOKEN-VALUE-9f3a", home);
    }

    [Theory]
    [InlineData("/stations")]
    [InlineData("/settings")]
    [InlineData("/")]
    public async Task Anonymous_requests_for_the_tv_pages_redirect_to_login(string path)
    {
        using var auth = await Factory.CreateAuthenticatedClientAsync();
        var anonymous = Factory.CreateNoRedirectClient();

        var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task Pairing_against_the_fake_tv_stores_the_token_and_testing_updates_the_cache()
    {
        var id = await ConsoleAsync();

        var paired = await Factory.Services.GetRequiredService<TvPairingService>().PairAsync(id);
        Assert.True(paired.Success, paired.Error);
        await using (var db = await DbAsync())
        {
            Assert.False(string.IsNullOrEmpty((await db.Stations.AsNoTracking().SingleAsync(s => s.Id == id)).TvToken));
        }

        var tested = await Factory.Services.GetRequiredService<TvControlService>().TestAsync(id);

        Assert.True(tested.Success, tested.Message);
        Assert.StartsWith("TV is ", tested.Message);
        Assert.NotNull(Cache.Get(id));
    }

    // ---- Task 2: dashboard card ------------------------------------------------------------------------------

    [Theory]
    [InlineData(TvPowerState.On, "TV On")]
    [InlineData(TvPowerState.Standby, "TV Standby")]
    [InlineData(TvPowerState.Unreachable, "TV Unreachable")]
    [InlineData(TvPowerState.Unknown, "TV Unknown")]
    public async Task Console_card_shows_the_cached_tv_state_and_both_buttons(TvPowerState state, string label)
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var id = await ConsoleAsync();
        Cache.Set(id, state);

        var body = await client.GetStringAsync("/");

        Assert.Matches(new Regex($"tv-indicator[^>]*>{Regex.Escape(label)}<"), body);
        Assert.Contains("tv-on-button", body);
        Assert.Contains("tv-off-button", body);
    }

    [Fact]
    public async Task Console_card_without_a_cache_entry_says_tv_not_checked()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await ConsoleAsync();

        var body = await client.GetStringAsync("/");

        Assert.Contains("TV not checked", body);
    }

    [Fact]
    public async Task Pc_cards_and_consoles_without_a_tv_address_render_no_tv_controls()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await StationAsync("PC-01");
        await ConsoleAsync("PS5-02", null);

        var body = await client.GetStringAsync("/");

        Assert.Contains("PC-01", body);
        Assert.Contains("PS5-02", body);
        Assert.DoesNotContain("tv-indicator", body);
        Assert.DoesNotContain("tv-off-button", body);
    }

    [Fact]
    public async Task Idle_console_with_an_unreachable_tv_keeps_its_offline_badge_next_to_the_indicator()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var id = await ConsoleAsync();
        Cache.Set(id, TvPowerState.Unreachable);

        var body = await client.GetStringAsync("/");

        Assert.Contains("Offline", body);
        Assert.Contains("TV Unreachable", body);
    }

    [Fact]
    public async Task Manual_power_off_succeeds_updates_the_cache_and_a_failed_result_gives_a_failure_message()
    {
        UseFakeDriver();
        var id = await ConsoleAsync();
        var control = Factory.Services.GetRequiredService<TvControlService>();

        var off = await control.PowerOffAsync(id);
        Assert.True(off.Success, off.Message);
        Assert.Equal(TvPowerState.Standby, Cache.Get(id));

        _driver.OnResult = _ => new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unreachable, "TV did not respond");
        var on = await control.PowerOnAsync(id);
        Assert.False(on.Success);
        Assert.Contains("did not respond", on.Message);
    }

    [Fact]
    public async Task Manual_power_off_on_an_unpaired_tv_returns_the_driver_message_about_pairing()
    {
        // The real driver, a station with no token.
        var id = await ConsoleAsync();

        var result = await Factory.Services.GetRequiredService<TvControlService>().PowerOffAsync(id);

        Assert.False(result.Success);
        Assert.Contains("pair", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Task 3: settings panel ------------------------------------------------------------------------------

    [Fact]
    public async Task Settings_page_shows_the_tv_section_checked_by_default_and_the_help_text()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();

        var body = await client.GetStringAsync("/settings");

        Assert.Contains("Samsung TVs", body);
        Assert.Contains("Power off the TV when a session is stopped", body);
        Assert.Matches(new Regex("<input[^>]*tv-poweroff-toggle[^>]*checked|<input[^>]*checked[^>]*tv-poweroff-toggle"), body);
        Assert.Contains("Power On with Mobile", body);
        Assert.Contains("IP Remote", body);
        Assert.Contains("network standby", body);
        Assert.Contains("fixed IP", body);
    }

    [Fact]
    public async Task Settings_page_shows_the_toggle_unchecked_after_it_is_turned_off()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await Factory.Services.GetRequiredService<TvSettingsService>().SetPowerOffOnStopAsync(false);

        var body = await client.GetStringAsync("/settings");

        Assert.Contains("tv-poweroff-toggle", body);
        Assert.DoesNotMatch(new Regex("<input[^>]*tv-poweroff-toggle[^>]*checked|<input[^>]*checked[^>]*tv-poweroff-toggle"), body);
    }

    [Fact]
    public async Task A_saved_false_toggle_stops_the_sink_from_powering_the_tv_off()
    {
        UseFakeDriver();
        var id = await ConsoleAsync("PS5-01", "10.0.0.5");
        await Factory.Services.GetRequiredService<TvSettingsService>().SetPowerOffOnStopAsync(false);
        var session = await PrepaidAsync(id);
        await Factory.Services.GetRequiredService<TvActionQueue>().WhenIdleAsync();

        var stop = await SvcAsync(async s =>
        {
            var quote = await s.QuoteStopAsync(session.Id);
            return await s.StopAsync(session.Id, quote.Value!.Version, quote.Value.Amount);
        });
        Assert.True(stop.Success, stop.Error);
        await Factory.Services.GetRequiredService<TvActionQueue>().WhenIdleAsync();

        Assert.Equal(["PowerOn"], _driver.Actions(id));
    }
}
