using System.Net;
using Cafe.Server.Auth;
using Cafe.Server.Data;
using Cafe.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cafe.Tests;

public class SetupFlowTests
{
    private const string StrongPassword = "correct-horse-1";

    private static async Task<string?> StoredHashAsync(CafeWebApplicationFactory factory)
    {
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Settings.Where(s => s.Key == AdminPasswordService.HashKey)
            .Select(s => s.Value).FirstOrDefaultAsync();
    }

    private static Task<HttpResponseMessage> PostSetup(HttpClient client, string password, string confirm) =>
        FormPostHelper.PostFormAsync(client, "/setup",
            new Dictionary<string, string> { ["Password"] = password, ["ConfirmPassword"] = confirm });

    [Fact]
    public async Task Root_on_empty_db_redirects_to_setup()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("/setup", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Static_assets_are_not_gated_on_empty_db()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/lib/bootstrap/dist/css/bootstrap.min.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Setup_page_renders_two_password_inputs()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/setup");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var passwordInputs = FormPostHelper.ParseInputs(html)
            .Count(i => i.TryGetValue("type", out var t) && t == "password");
        Assert.Equal(2, passwordInputs);
    }

    [Fact]
    public async Task Valid_setup_stores_hash_and_redirects_to_login()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();

        var response = await PostSetup(client, StrongPassword, StrongPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("/login", response.Headers.Location!.OriginalString);

        var hash = await StoredHashAsync(factory);
        Assert.NotNull(hash);
        Assert.NotEqual(StrongPassword, hash);
        var result = new PasswordHasher<object>().VerifyHashedPassword(new object(), hash, StrongPassword);
        Assert.NotEqual(PasswordVerificationResult.Failed, result);
    }

    [Fact]
    public async Task Setup_cannot_be_rerun_after_completion()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();
        await PostSetup(client, StrongPassword, StrongPassword);
        var originalHash = await StoredHashAsync(factory);

        var get = await client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.EndsWith("/login", get.Headers.Location!.OriginalString);

        // A direct service call must refuse to overwrite.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AdminPasswordService>();
        Assert.False(await service.SetPasswordAsync("a-different-password"));

        Assert.Equal(originalHash, await StoredHashAsync(factory));
    }

    [Fact]
    public async Task Post_to_setup_after_completion_does_not_change_hash()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();
        // Fetch a valid form (antiforgery token + cookie) while still unconfigured.
        var page = await client.GetAsync("/setup");
        var html = await page.Content.ReadAsStringAsync();
        await PostSetup(client, StrongPassword, StrongPassword);
        var originalHash = await StoredHashAsync(factory);

        var fields = FormPostHelper.ParseInputs(html)
            .Where(i => i.ContainsKey("name"))
            .Select(i => new KeyValuePair<string, string>(i["name"],
                i.TryGetValue("type", out var t) && t == "hidden"
                    ? i.GetValueOrDefault("value", string.Empty)
                    : "attacker-password-9"))
            .ToList();
        await client.PostAsync("/setup", new FormUrlEncodedContent(fields));

        Assert.Equal(originalHash, await StoredHashAsync(factory));
    }

    [Theory]
    [InlineData("short", "short", "Password must be at least 8 characters.")]
    [InlineData("long-enough-1", "different-pass-2", "Passwords do not match.")]
    [InlineData("", "", "Password is required and cannot be blank.")]
    [InlineData("        ", "        ", "Password is required and cannot be blank.")]
    public async Task Invalid_setup_is_rejected_with_message_and_stores_nothing(string password, string confirm, string message)
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();

        var response = await PostSetup(client, password, confirm);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, body);
        Assert.Null(await StoredHashAsync(factory));
    }

    [Fact]
    public async Task Service_rethrows_non_constraint_save_failures_instead_of_reporting_already_configured()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();
        await client.GetAsync("/setup"); // forces startup + migration
        var cs = factory.ConnectionString;
        var service = new AdminPasswordService(new FailingSaveDbContextFactory(cs));

        await Assert.ThrowsAsync<DbUpdateException>(() => service.SetPasswordAsync(StrongPassword));
    }

    [Fact]
    public async Task Setup_shows_error_and_stays_on_page_when_password_cannot_be_saved()
    {
        using var baseFactory = new CafeWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var cs = baseFactory.ConnectionString;
            services.RemoveAll<IDbContextFactory<CafeDbContext>>();
            services.AddSingleton<IDbContextFactory<CafeDbContext>>(new FailingSaveDbContextFactory(cs));
        }));
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var response = await PostSetup(client, StrongPassword, StrongPassword);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Could not save the password", body);
        Assert.Null(await StoredHashAsync(baseFactory));
    }

    private static CafeWebApplicationFactory RemoteFactory(bool allowRemote = false)
    {
        var factory = new CafeWebApplicationFactory { RemoteAddress = System.Net.IPAddress.Parse("192.168.1.50") };
        if (allowRemote)
        {
            factory.ExtraSettings["Setup:AllowRemote"] = "true";
        }

        return factory;
    }

    [Theory]
    [InlineData("/setup")]
    [InlineData("/")]
    public async Task Remote_visitor_before_setup_sees_friendly_page_instead_of_setup_form(string path)
    {
        using var factory = RemoteFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = true });

        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("server PC", body);
        Assert.Empty(FormPostHelper.ParseInputs(body));
    }

    [Fact]
    public async Task Remote_visitor_cannot_post_setup_by_default()
    {
        using var factory = RemoteFactory();
        using var client = factory.CreateNoRedirectClient();

        // Even a well-formed POST (no antiforgery needed to prove the point) is refused before it reaches the page.
        var response = await client.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Model.Password"] = StrongPassword,
            ["Model.ConfirmPassword"] = StrongPassword,
        }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await StoredHashAsync(factory));
    }

    [Fact]
    public async Task Remote_setup_works_when_allow_remote_is_enabled()
    {
        using var factory = RemoteFactory(allowRemote: true);
        using var client = factory.CreateNoRedirectClient();

        var response = await PostSetup(client, StrongPassword, StrongPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(await StoredHashAsync(factory));
    }

    [Fact]
    public async Task Remote_visitor_is_redirected_to_login_once_setup_is_complete()
    {
        using var factory = new CafeWebApplicationFactory();
        using (var local = factory.CreateNoRedirectClient())
        {
            await PostSetup(local, StrongPassword, StrongPassword);
        }

        factory.RemoteAddress = System.Net.IPAddress.Parse("192.168.1.50");
        using var remote = factory.CreateNoRedirectClient();
        var login = await remote.GetAsync("/login");
        var setup = await remote.GetAsync("/setup");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
        Assert.EndsWith("/login", setup.Headers.Location!.OriginalString);
    }
}
