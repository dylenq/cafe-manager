using System.Net;
using Cafe.Server.Auth;
using Cafe.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cafe.Tests;

public class AuthTests
{
    private const string Password = AuthTestExtensions.TestPassword;

    private static Task<HttpResponseMessage> PostLogin(HttpClient client, string password, string path = "/login") =>
        FormPostHelper.PostFormAsync(client, path, new Dictionary<string, string> { ["Password"] = password });

    private static string PathOf(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static bool SetsAuthCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
        && values.Any(v => v.StartsWith(".Cafe.Auth=", StringComparison.Ordinal) && !v.Contains("expires=Thu, 01 Jan 1970"));

    private static async Task<CafeWebApplicationFactory> ConfiguredFactoryAsync()
    {
        var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();
        await FormPostHelper.PostFormAsync(client, "/setup",
            new Dictionary<string, string> { ["Password"] = Password, ["ConfirmPassword"] = Password });
        return factory;
    }

    [Fact]
    public async Task Correct_password_redirects_with_persistent_cookie_and_grants_access()
    {
        using var factory = await ConfiguredFactoryAsync();
        using var client = factory.CreateNoRedirectClient();

        var response = await PostLogin(client, Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(".Cafe.Auth=", StringComparison.Ordinal));
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        var expiresText = cookie.Split(';').Select(p => p.Trim()).First(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))[8..];
        var expires = DateTimeOffset.Parse(expiresText);
        Assert.InRange((expires - DateTimeOffset.UtcNow).TotalDays, 29, 31);

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_shows_message_and_sets_no_cookie()
    {
        using var factory = await ConfiguredFactoryAsync();
        using var client = factory.CreateNoRedirectClient();

        var response = await PostLogin(client, "not-the-password");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Incorrect password", html);
        Assert.False(SetsAuthCookie(response));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Empty_password_shows_required_message()
    {
        using var factory = await ConfiguredFactoryAsync();
        using var client = factory.CreateNoRedirectClient();

        var response = await PostLogin(client, "");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Password is required.", html);
        Assert.False(SetsAuthCookie(response));
    }

    [Fact]
    public async Task Anonymous_root_on_configured_install_redirects_to_login_with_return_url()
    {
        using var factory = await ConfiguredFactoryAsync();
        using var client = factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.Contains("/login", location);
        Assert.Contains("ReturnUrl=%2F", location);
    }

    [Fact]
    public async Task Anonymous_root_on_unconfigured_install_redirects_to_setup()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("/setup", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Unknown_protected_path_redirects_anonymous_users_away()
    {
        using var factory = await ConfiguredFactoryAsync();
        using var client = factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/stations");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("evil.example", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("/stations", "/stations")]
    [InlineData("/station-types?x=1", "/station-types?x=1")]
    public void ReturnUrl_sanitizer_only_allows_local_paths(string? input, string expected)
    {
        Assert.Equal(expected, ReturnUrlHelper.Sanitize(input));
    }

    [Fact]
    public async Task Login_honours_local_return_url_and_ignores_external()
    {
        using var factory = await ConfiguredFactoryAsync();
        using var client = factory.CreateNoRedirectClient();

        var local = await PostLogin(client, Password, "/login?ReturnUrl=%2Fstations");
        Assert.Equal("/stations", PathOf(local));

        using var client2 = factory.CreateNoRedirectClient();
        var external = await PostLogin(client2, Password, "/login?ReturnUrl=https%3A%2F%2Fevil.example");
        Assert.Equal("/", PathOf(external));

        using var client3 = factory.CreateNoRedirectClient();
        var protocolRelative = await PostLogin(client3, Password, "/login?ReturnUrl=%2F%2Fevil.example");
        Assert.Equal("/", PathOf(protocolRelative));
    }

    [Fact]
    public async Task Cookie_scheme_is_30_day_sliding()
    {
        using var factory = new CafeWebApplicationFactory();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(TimeSpan.FromDays(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
    }

    [Fact]
    public async Task Nav_shows_links_and_logout_form_for_signed_in_user()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();

        var html = await (await client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("Dashboard", html);
        Assert.Contains("href=\"stations\"", html);
        Assert.Contains("Station Types", html);
        Assert.Contains("action=\"/logout\"", html);
    }

    [Fact]
    public async Task Logout_with_token_clears_cookie_and_locks_out_again()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

        var response = await FormPostHelper.PostFormAsync(client, "/", new Dictionary<string, string>(), "/logout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", PathOf(response));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Login_before_setup_redirects_to_setup()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/setup", PathOf(response));
    }

    [Fact]
    public async Task Full_journey_setup_login_logout()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = factory.CreateNoRedirectClient();

        var first = await client.GetAsync("/");
        Assert.Equal("/setup", PathOf(first));

        var setup = await FormPostHelper.PostFormAsync(client, "/setup",
            new Dictionary<string, string> { ["Password"] = Password, ["ConfirmPassword"] = Password });
        Assert.Equal("/login", PathOf(setup));

        var wrong = await PostLogin(client, "wrong-password-1");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);

        var right = await PostLogin(client, Password);
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Station Types", await home.Content.ReadAsStringAsync());

        var logout = await FormPostHelper.PostFormAsync(client, "/", new Dictionary<string, string>(), "/logout");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var after = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/login", PathOf(after));
    }

    [Fact]
    public async Task Static_assets_and_framework_paths_do_not_need_a_cookie_when_configured()
    {
        using var factory = await ConfiguredFactoryAsync();
        using var client = factory.CreateNoRedirectClient();

        var css = await client.GetAsync("/lib/bootstrap/dist/css/bootstrap.min.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);

        // The Testing environment does not map framework static assets (they resolve in Development/published
        // builds), so only assert the request is never bounced to /login or /setup.
        var framework = await client.GetAsync("/_framework/blazor.web.js");
        Assert.NotEqual(HttpStatusCode.Redirect, framework.StatusCode);
    }

    [Fact]
    public async Task Authenticated_session_survives_repeated_requests()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Authenticated_user_visiting_login_is_sent_home()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", PathOf(response));
    }

    [Fact]
    public async Task Logout_without_token_is_rejected()
    {
        using var factory = new CafeWebApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }
}
