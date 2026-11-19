namespace Cafe.Server.Auth;

/// <summary>
/// Redirects every page request to /setup until the admin password has been chosen. Until then /setup is only served
/// to loopback requests (first-run setup happens on the server PC) unless <c>Setup:AllowRemote</c> is true.
/// </summary>
public class SetupGateMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private const string RemoteSetupPage = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Setup required - Cafe Manager</title>
        <style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem;line-height:1.5}</style>
        </head>
        <body>
        <h1>Cafe Manager is not set up yet</h1>
        <p>First-time setup must be done on the server PC itself, at <strong>http://localhost:5080</strong>, not from this device.</p>
        <p>Ask the owner to open that address on the front-desk PC and choose the admin password. Once that is done, come back here and log in.</p>
        </body>
        </html>
        """;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var passwords = context.RequestServices.GetRequiredService<AdminPasswordService>();

        if (path.StartsWithSegments("/setup", StringComparison.OrdinalIgnoreCase)
            && !IsLoopback(context)
            && !configuration.GetValue<bool>("Setup:AllowRemote")
            && !await passwords.IsConfiguredAsync(context.RequestAborted))
        {
            // Covers GET and POST: nobody on the LAN can claim the admin password remotely.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(RemoteSetupPage, context.RequestAborted);
            return;
        }

        if (IsExempt(path))
        {
            await next(context);
            return;
        }

        if (!await passwords.IsConfiguredAsync(context.RequestAborted))
        {
            context.Response.Redirect("/setup");
            return;
        }

        await next(context);
    }

    // A missing remote address is not trusted as loopback: Kestrel always sets one, so null only happens in tests,
    // which supply an explicit address instead.
    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address);

    private static bool IsExempt(PathString path)
    {
        if (path.StartsWithSegments("/setup", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
            // PC agents authenticate with the shared key and must connect before admin setup finishes.
            || path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Static assets: final segment has a file extension (e.g. /lib/bootstrap/dist/css/bootstrap.min.css).
        var value = path.Value ?? string.Empty;
        var lastSegment = value[(value.LastIndexOf('/') + 1)..];
        return lastSegment.Contains('.');
    }
}
