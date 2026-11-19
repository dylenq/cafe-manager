namespace Cafe.Server.Auth;

/// <summary>Guards post-login redirects so a ReturnUrl can never send the user to another site.</summary>
public static class ReturnUrlHelper
{
    public static string Sanitize(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/')
        {
            return "/";
        }

        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
        {
            return "/";
        }

        // Control characters (e.g. CR/LF, tabs that browsers strip) can smuggle other targets.
        if (returnUrl.Any(char.IsControl))
        {
            return "/";
        }

        return returnUrl;
    }
}
