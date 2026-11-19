using System.Net;
using System.Text.RegularExpressions;

namespace Cafe.Tests.Infrastructure;

/// <summary>Posts static SSR Blazor forms: scrapes hidden inputs (antiforgery, _handler) and field names from the page.</summary>
public static partial class FormPostHelper
{
    [GeneratedRegex("<input\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputTag();

    [GeneratedRegex("\\b(name|value|type)=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Attribute();

    public static List<Dictionary<string, string>> ParseInputs(string html)
    {
        var inputs = new List<Dictionary<string, string>>();
        foreach (Match tag in InputTag().Matches(html))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in Attribute().Matches(tag.Value))
            {
                attrs[a.Groups[1].Value] = WebUtility.HtmlDecode(a.Groups[2].Value);
            }

            inputs.Add(attrs);
        }

        return inputs;
    }

    /// <summary>
    /// GETs <paramref name="formPath"/>, then POSTs it to <paramref name="postPath"/> (default same page).
    /// <paramref name="values"/> are keyed by field-name suffix, e.g. "Password" matches "Model.Password".
    /// </summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string formPath, IDictionary<string, string> values, string? postPath = null)
    {
        var page = await client.GetAsync(formPath);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();

        var fields = new List<KeyValuePair<string, string>>();
        foreach (var input in ParseInputs(html))
        {
            if (!input.TryGetValue("name", out var name))
            {
                continue;
            }

            input.TryGetValue("type", out var type);
            if (string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase))
            {
                input.TryGetValue("value", out var hidden);
                fields.Add(new(name, hidden ?? string.Empty));
            }
            else
            {
                var match = values.FirstOrDefault(v => name.EndsWith("." + v.Key, StringComparison.Ordinal) || name == v.Key);
                fields.Add(new(name, match.Key is null ? string.Empty : match.Value));
            }
        }

        return await client.PostAsync(postPath ?? formPath, new FormUrlEncodedContent(fields));
    }
}
