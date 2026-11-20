using Cafe.Server.Data;
using Cafe.Server.Devices;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>Tracer: a fake Samsung TV driven through the real transport, from pairing to expiry power off.</summary>
public class TvTracerTests : SessionTestBase, IAsyncLifetime
{
    private FakeSamsungTv _tv = null!;

    public async Task InitializeAsync()
    {
        _tv = await FakeSamsungTv.StartAsync();
        Factory.ExtraSettings["Tv:RemotePort"] = _tv.Port.ToString();
        Factory.ExtraSettings["Tv:RestPort"] = _tv.Port.ToString();
        Factory.ExtraSettings["Tv:UseTls"] = "false";
        Factory.ExtraSettings["Tv:SettleMilliseconds"] = "0";
    }

    public async Task DisposeAsync() => await _tv.DisposeAsync();

    private async Task<int> ConsoleAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = "PS5-01", StationTypeId = await TypeAsync(), Kind = StationKind.Console, TvIpAddress = "127.0.0.1",
        });
        Assert.True(created.Success, created.Error);
        return created.Value!.Id;
    }

    private async Task<string?> TokenAsync(int stationId)
    {
        await using var db = await DbAsync();
        return (await db.Stations.AsNoTracking().SingleAsync(s => s.Id == stationId)).TvToken;
    }

    [Fact]
    public async Task Pairing_through_the_real_transport_stores_the_token()
    {
        var stationId = await ConsoleAsync();

        var result = await Factory.Services.GetRequiredService<TvPairingService>().PairAsync(stationId);

        Assert.True(result.Success, result.Error);
        Assert.Equal("TOKEN123", await TokenAsync(stationId));
    }

    [Fact]
    public async Task An_expired_console_session_powers_its_tv_off_exactly_once()
    {
        var stationId = await ConsoleAsync();
        Assert.True((await Factory.Services.GetRequiredService<TvPairingService>().PairAsync(stationId)).Success);
        var session = await PrepaidAsync(stationId, 1);

        Advance(TimeSpan.FromHours(3));
        var evaluator = Factory.Services.GetRequiredService<IExpiryEvaluator>();
        for (var i = 0; i < 3 && (await LoadAsync(session.Id)).Status != SessionStatus.Ended; i++)
        {
            await evaluator.EvaluateAsync(CancellationToken.None);
        }

        var ended = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, ended.Status);
        Assert.Equal(["KEY_POWER"], _tv.Keys);
        Assert.Equal("standby", _tv.PowerState);
        Assert.True(ended.Events.Any(e => e.Type == SessionEventType.ExpiryActionFired), string.Join(" | ", ended.Events.Select(e => $"{e.Type}: {e.Details}")));
    }

    [Fact]
    public async Task A_tv_that_refuses_pairing_leaves_the_token_empty()
    {
        _tv.RemoteMode = FakeTvRemoteMode.Unauthorized;
        var stationId = await ConsoleAsync();

        var result = await Factory.Services.GetRequiredService<TvPairingService>().PairAsync(stationId);

        Assert.False(result.Success);
        Assert.Contains("refused", result.Error);
        Assert.Null(await TokenAsync(stationId));
    }
}
