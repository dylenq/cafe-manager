namespace Cafe.Server.Auth;

/// <summary>Redirects every page request to /setup until the admin password has been chosen.</summary>
public class SetupGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (IsExempt(path))
        {
            await next(context);
            return;
        }

        var passwords = context.RequestServices.GetRequiredService<AdminPasswordService>();
        if (!await passwords.IsConfiguredAsync(context.RequestAborted))
        {
            context.Response.Redirect("/setup");
            return;
        }

        await next(context);
    }

    private static bool IsExempt(PathString path)
    {
        if (path.StartsWithSegments("/setup", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Static assets: final segment has a file extension (e.g. /lib/bootstrap/dist/css/bootstrap.min.css).
        var value = path.Value ?? string.Empty;
        var lastSegment = value[(value.LastIndexOf('/') + 1)..];
        return lastSegment.Contains('.');
    }
}
