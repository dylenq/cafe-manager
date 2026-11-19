namespace Cafe.Tests.Infrastructure;

public static class AuthTestExtensions
{
    public const string TestPassword = "correct-horse-1";

    /// <summary>Runs first-run /setup then /login and returns a cookie-holding, non-redirecting client.</summary>
    public static async Task<HttpClient> CreateAuthenticatedClientAsync(this CafeWebApplicationFactory factory)
    {
        var client = factory.CreateNoRedirectClient();

        var setup = await FormPostHelper.PostFormAsync(client, "/setup",
            new Dictionary<string, string> { ["Password"] = TestPassword, ["ConfirmPassword"] = TestPassword });
        if ((int)setup.StatusCode is < 300 or >= 400)
        {
            throw new InvalidOperationException($"Setup did not redirect: {(int)setup.StatusCode}");
        }

        var login = await FormPostHelper.PostFormAsync(client, "/login",
            new Dictionary<string, string> { ["Password"] = TestPassword });
        if ((int)login.StatusCode is < 300 or >= 400)
        {
            throw new InvalidOperationException($"Login did not redirect: {(int)login.StatusCode}");
        }

        return client;
    }
}
