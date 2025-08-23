using System.Globalization;
using AnSAM.Services;
using Xunit;

public class SteamLanguageResolverTests
{
    [Theory]
    [InlineData("zh-TW", "tchinese")]
    [InlineData("zh-HK", "tchinese")]
    [InlineData("zh-CN", "schinese")]
    [InlineData("zh", "tchinese")]
    public void GetSteamLanguage_ReturnsExpectedLanguage(string cultureName, string expected)
    {
        var culture = new CultureInfo(cultureName);
        var language = SteamLanguageResolver.GetSteamLanguage(culture);
        Assert.Equal(expected, language);
    }
}
