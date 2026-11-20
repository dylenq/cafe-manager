using System.Net;
using Cafe.Server.Data;
using Cafe.Server.Devices;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cafe.Tests;

public class TvDriverTests
{
    private readonly FakeTvTransport _net = new();

    private SamsungTizenDriver Driver(Action<TvOptions>? tweak = null)
    {
        var o = new TvOptions
        {
            SettleMilliseconds = 0, KeyFlushMilliseconds = 0, WakeWaitMilliseconds = 0, WolRoundDelayMilliseconds = 0, PairTimeoutSeconds = 1,
        };
        tweak?.Invoke(o);
        return new SamsungTizenDriver(_net, Options.Create(o), NullLogger<SamsungTizenDriver>.Instance);
    }

    private static Station Tv(string? token = "TOK", string? ip = "192.168.1.50", string? mac = "AA:BB:CC:DD:EE:FF") =>
        new() { Id = 1, Name = "PS5-01", Kind = StationKind.Console, TvIpAddress = ip, TvMacAddress = mac, TvToken = token };

    // ---- pairing ----

    [Fact]
    public async Task Pairing_reads_the_token()
    {
        _net.Incoming.Enqueue(FakeTvTransport.Connected("T0K3N"));
        var r = await Driver().PairAsync(Tv(token: null), default);
        Assert.True(r.Success);
        Assert.Equal("T0K3N", r.Token);
        Assert.DoesNotContain("token=", _net.Connects[0]);
    }

    [Fact]
    public async Task Pairing_without_a_token_stores_the_marker()
    {
        _net.Incoming.Enqueue(FakeTvTransport.Connected());
        var r = await Driver().PairAsync(Tv(token: null), default);
        Assert.True(r.Success);
        Assert.Equal(SamsungTizenDriver.NoTokenMarker, r.Token);
    }

    [Fact]
    public async Task Pairing_failures_have_distinct_readable_messages()
    {
        _net.Incoming.Enqueue(FakeTvTransport.Unauthorized);
        var unauthorized = await Driver().PairAsync(Tv(token: null), default);
        _net.Incoming.Enqueue(FakeTvTransport.TimeOut);
        var timeOut = await Driver().PairAsync(Tv(token: null), default);
        _net.Incoming.Enqueue(null);
        var silent = await Driver().PairAsync(Tv(token: null), default);

        Assert.All(new[] { unauthorized, timeOut, silent }, r => Assert.False(r.Success));
        Assert.Contains("refused", unauthorized.Error);
        Assert.Contains("timed out", timeOut.Error);
        Assert.Contains("did not respond", silent.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("1.2.3")]
    [InlineData("::1")]
    public async Task Pairing_a_station_without_a_valid_ip_never_opens_a_socket(string? ip)
    {
        var r = await Driver().PairAsync(Tv(token: null, ip: ip), default);
        Assert.False(r.Success);
        Assert.Contains("valid TV IP", r.Error);
        Assert.Empty(_net.Connects);
    }

    [Fact]
    public async Task A_tv_that_cannot_be_reached_fails_pairing()
    {
        _net.ConnectFails = true;
        var r = await Driver().PairAsync(Tv(token: null), default);
        Assert.False(r.Success);
    }

    // ---- state ----

    public static TheoryData<int, string?, TvPowerState> StateCases => new()
    {
        { 200, "{\"device\":{\"PowerState\":\"on\"}}", TvPowerState.On },
        { 200, "{\"device\":{\"PowerState\":\"standby\"}}", TvPowerState.Standby },
        { 200, "{\"device\":{\"PowerState\":\"warming\"}}", TvPowerState.Unknown },
        { 200, "{\"device\":{\"name\":\"x\"}}", TvPowerState.On },
        { 500, "{\"device\":{\"PowerState\":\"on\"}}", TvPowerState.Unknown },
        { 200, "{not json", TvPowerState.Unknown },
        { 200, null, TvPowerState.Unknown },
    };

    [Theory]
    [MemberData(nameof(StateCases))]
    public async Task State_is_parsed_from_the_rest_reply(int status, string? body, TvPowerState expected)
    {
        _net.Http = () => new TvHttpResult(true, status, body);
        Assert.Equal(expected, await Driver().GetStateAsync(Tv(), default));
    }

    [Fact]
    public async Task A_tv_that_does_not_answer_is_unreachable()
    {
        _net.Http = FakeTvTransport.Down;
        Assert.Equal(TvPowerState.Unreachable, await Driver().GetStateAsync(Tv(), default));
    }

    // ---- power off ----

    [Fact]
    public async Task Standby_sends_no_key()
    {
        _net.Http = () => FakeTvTransport.State("standby");
        var r = await Driver().PowerOffAsync(Tv(), default);
        Assert.Equal(TvActionOutcome.AlreadyOff, r.Outcome);
        Assert.Empty(_net.Connects);
    }

    [Fact]
    public async Task Unreachable_sends_no_key()
    {
        _net.Http = FakeTvTransport.Down;
        var r = await Driver().PowerOffAsync(Tv(), default);
        Assert.Equal(TvActionOutcome.Unreachable, r.Outcome);
        Assert.Empty(_net.Connects);
    }

    [Fact]
    public async Task On_sends_one_key_and_rechecks()
    {
        var power = "on";
        _net.Http = () => FakeTvTransport.State(power);
        _net.OnKeyPower = () => power = "standby";
        _net.Incoming.Enqueue(FakeTvTransport.Connected());

        var r = await Driver().PowerOffAsync(Tv(), default);

        Assert.Equal(TvActionOutcome.PoweredOff, r.Outcome);
        Assert.Equal(1, _net.KeyCount);
        Assert.Contains("token=TOK", _net.Connects[0]);
    }

    [Fact]
    public async Task Still_on_after_the_key_is_a_failure()
    {
        _net.Incoming.Enqueue(FakeTvTransport.Connected());
        var r = await Driver().PowerOffAsync(Tv(), default);
        Assert.Equal(TvActionOutcome.Failed, r.Outcome);
        Assert.Equal(1, _net.KeyCount);
    }

    [Fact]
    public async Task Unknown_state_sends_the_key()
    {
        _net.Http = () => new TvHttpResult(true, 500, null);
        _net.Incoming.Enqueue(FakeTvTransport.Connected());
        var r = await Driver().PowerOffAsync(Tv(), default);
        Assert.Equal(TvActionOutcome.KeySent, r.Outcome);
        Assert.Equal(1, _net.KeyCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task An_unpaired_tv_is_not_contacted(string? token)
    {
        var r = await Driver().PowerOffAsync(Tv(token), default);
        Assert.Equal(TvActionOutcome.Failed, r.Outcome);
        Assert.Contains("not paired", r.Message);
        Assert.Empty(_net.Connects);
        Assert.Equal(0, _net.HttpRequests);
    }

    [Fact]
    public async Task A_rejected_token_asks_for_a_new_pairing()
    {
        _net.Incoming.Enqueue(FakeTvTransport.Unauthorized);
        var r = await Driver().PowerOffAsync(Tv(), default);
        Assert.Equal(TvActionOutcome.Failed, r.Outcome);
        Assert.Contains("pair it again", r.Message);
        Assert.Equal(0, _net.KeyCount);
    }

    [Fact]
    public async Task The_no_token_marker_is_not_sent_as_a_token()
    {
        _net.Incoming.Enqueue(FakeTvTransport.Connected());
        await Driver().PowerOffAsync(Tv(SamsungTizenDriver.NoTokenMarker), default);
        Assert.DoesNotContain("token=", _net.Connects[0]);
    }

    [Fact]
    public async Task Concurrent_power_offs_for_one_tv_send_one_key()
    {
        var power = "on";
        _net.Http = () => FakeTvTransport.State(power);
        _net.OnKeyPower = () => power = "standby";
        _net.Incoming.Enqueue(FakeTvTransport.Connected());
        _net.Incoming.Enqueue(FakeTvTransport.Connected());
        var driver = Driver();

        var results = await Task.WhenAll(driver.PowerOffAsync(Tv(), default), driver.PowerOffAsync(Tv(), default));

        Assert.Equal(1, _net.KeyCount);
        Assert.Contains(results, r => r.Outcome == TvActionOutcome.PoweredOff);
        Assert.Contains(results, r => r.Outcome == TvActionOutcome.AlreadyOff);
    }

    // ---- power on / wake on lan ----

    [Fact]
    public void Magic_packet_is_102_bytes_ff_then_the_mac_16_times()
    {
        Assert.True(WakeOnLan.TryBuildMagicPacket("AA:BB:CC:DD:EE:FF", out var p));
        Assert.Equal(102, p.Length);
        Assert.All(p.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, p.Skip(6 + (i * 6)).Take(6));
        }

        Assert.True(WakeOnLan.TryBuildMagicPacket("aa-bb-cc-dd-ee-ff", out var q));
        Assert.Equal(p, q);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA:BB:CC:DD:EE:GG")]
    [InlineData("AA:BB:CC:DD:EE:FF:00")]
    [InlineData("A:BB:CC:DD:EE:FF")]
    public void Invalid_macs_are_rejected(string? mac) => Assert.False(WakeOnLan.TryBuildMagicPacket(mac, out _));

    [Fact]
    public void Subnet_broadcast_is_the_slash_24()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.255"), WakeOnLan.SubnetBroadcast(IPAddress.Parse("192.168.1.50")));
    }

    [Fact]
    public async Task A_tv_that_is_on_is_left_alone()
    {
        var r = await Driver().PowerOnAsync(Tv(), default);
        Assert.Equal(TvActionOutcome.AlreadyOn, r.Outcome);
        Assert.Empty(_net.Datagrams);
    }

    [Fact]
    public async Task Wake_on_lan_sends_12_datagrams_to_both_broadcasts_on_ports_9_and_7_then_the_key_from_standby()
    {
        _net.Http = () => FakeTvTransport.State("standby");
        _net.Incoming.Enqueue(FakeTvTransport.Connected());

        var r = await Driver().PowerOnAsync(Tv(), default);

        Assert.Equal(TvActionOutcome.PoweredOn, r.Outcome);
        Assert.Equal(12, _net.Datagrams.Count);
        Assert.All(_net.Datagrams, d => Assert.Equal(102, d.Payload.Length));
        foreach (var target in new[] { "255.255.255.255", "192.168.1.255" })
        {
            foreach (var port in new[] { 9, 7 })
            {
                Assert.Equal(3, _net.Datagrams.Count(d => d.Target.ToString() == target && d.Port == port));
            }
        }

        Assert.Equal(1, _net.KeyCount);
    }

    [Fact]
    public async Task A_tv_that_wakes_by_itself_gets_no_key()
    {
        var calls = 0;
        _net.Http = () => ++calls == 1 ? FakeTvTransport.Down() : FakeTvTransport.State("on");
        var r = await Driver().PowerOnAsync(Tv(), default);
        Assert.Equal(TvActionOutcome.PoweredOn, r.Outcome);
        Assert.Equal(0, _net.KeyCount);
    }

    [Fact]
    public async Task A_tv_that_stays_unreachable_reports_wake_sent()
    {
        _net.Http = FakeTvTransport.Down;
        var r = await Driver().PowerOnAsync(Tv(), default);
        Assert.Equal(TvActionOutcome.WakeSent, r.Outcome);
        Assert.Equal(0, _net.KeyCount);
    }

    [Fact]
    public async Task Without_a_mac_a_standby_tv_still_gets_the_key()
    {
        _net.Http = () => FakeTvTransport.State("standby");
        _net.Incoming.Enqueue(FakeTvTransport.Connected());
        var r = await Driver().PowerOnAsync(Tv(mac: null), default);
        Assert.Equal(TvActionOutcome.PoweredOn, r.Outcome);
        Assert.Empty(_net.Datagrams);
        Assert.Equal(1, _net.KeyCount);
    }

    [Fact]
    public async Task Without_a_mac_an_unreachable_tv_fails_with_a_clear_message()
    {
        _net.Http = FakeTvTransport.Down;
        var r = await Driver().PowerOnAsync(Tv(mac: "bogus"), default);
        Assert.Equal(TvActionOutcome.Failed, r.Outcome);
        Assert.Contains("MAC address", r.Message);
    }

    // ---- expiry handler ----

    private sealed class StubDriver(TvActionOutcome outcome) : ITvDriver
    {
        public int Off { get; private set; }

        public Task<TvPairResult> PairAsync(Station station, CancellationToken ct) => throw new NotSupportedException();

        public Task<TvActionResult> PowerOffAsync(Station station, CancellationToken ct)
        {
            Off++;
            return Task.FromResult(new TvActionResult(outcome, TvPowerState.Unknown, "stub"));
        }

        public Task<TvActionResult> PowerOnAsync(Station station, CancellationToken ct) => throw new NotSupportedException();

        public Task<TvPowerState> GetStateAsync(Station station, CancellationToken ct) => throw new NotSupportedException();
    }

    public class HandlerTests : SessionTestBase
    {
        private TvExpiryActionHandler Handler(ITvDriver driver)
        {
            var dbFactory = Factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>();
            var control = new TvControlService(driver, dbFactory, new TvStateCache(TimeProvider.System, Options.Create(new TvOptions())),
                TimeProvider.System, NullLogger<TvControlService>.Instance);
            return new(control, dbFactory, NullLogger<TvExpiryActionHandler>.Instance);
        }

        private async Task<Station> ConsoleAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PS5-01", StationTypeId = await TypeAsync(), Kind = StationKind.Console, TvIpAddress = "10.0.0.5",
            });
            return created.Value!;
        }

        [Fact]
        public async Task Pcs_and_consoles_without_a_tv_ip_do_not_touch_the_driver()
        {
            var stub = new StubDriver(TvActionOutcome.PoweredOff);
            await Handler(stub).ExecuteAsync(new Station { Id = 5, Name = "PC", Kind = StationKind.PC, TvIpAddress = "10.0.0.5" }, default);
            await Handler(stub).ExecuteAsync(new Station { Id = 6, Name = "PS", Kind = StationKind.Console }, default);
            Assert.Equal(0, stub.Off);
        }

        [Fact]
        public async Task A_station_with_a_new_session_is_not_powered_off()
        {
            var station = await ConsoleAsync();
            await PrepaidAsync(station.Id, 60);
            var stub = new StubDriver(TvActionOutcome.PoweredOff);

            await Handler(stub).ExecuteAsync(station, default);

            Assert.Equal(0, stub.Off);
        }

        [Theory]
        [InlineData(TvActionOutcome.Failed, true)]
        [InlineData(TvActionOutcome.Unreachable, true)]
        [InlineData(TvActionOutcome.AlreadyOff, false)]
        [InlineData(TvActionOutcome.PoweredOff, false)]
        [InlineData(TvActionOutcome.KeySent, false)]
        public async Task Failed_and_unreachable_throw_so_the_expiry_is_retried(TvActionOutcome outcome, bool throws)
        {
            var station = await ConsoleAsync();
            var stub = new StubDriver(outcome);

            var run = Handler(stub).ExecuteAsync(station, default);

            if (throws)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => run);
            }
            else
            {
                await run;
            }

            Assert.Equal(1, stub.Off);
        }
    }
}
