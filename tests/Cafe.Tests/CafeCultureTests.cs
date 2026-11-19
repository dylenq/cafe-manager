using System.Globalization;
using Cafe.Server.Pricing;

namespace Cafe.Tests;

public class CafeCultureTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-culture-xx-yy-zz")]
    public void Empty_or_unknown_names_keep_the_server_culture(string? configured) =>
        Assert.Null(CafeCulture.Resolve(configured, null));

    [Fact]
    public void A_valid_name_resolves_and_formats_currency_predictably()
    {
        var culture = CafeCulture.Resolve(" en-US ", null);

        Assert.Equal("en-US", culture!.Name);
        Assert.Equal("$1,234.50", 1234.5m.ToString("C", culture));
    }

    [Fact]
    public void Apply_with_null_changes_nothing()
    {
        var before = CultureInfo.DefaultThreadCurrentCulture;
        CafeCulture.Apply(null);
        Assert.Same(before, CultureInfo.DefaultThreadCurrentCulture);
    }
}
