using System.Globalization;
using AnSAM.Services;
using Xunit;

public class SteamLanguageResolverTests
{
    [Theory]
    [InlineData("ar", "arabic")]
    [InlineData("pt-BR", "brazilian")]
    [InlineData("bg", "bulgarian")]
    [InlineData("cs", "czech")]
    [InlineData("da", "danish")]
    [InlineData("nl", "dutch")]
    [InlineData("fi", "finnish")]
    [InlineData("fr", "french")]
    [InlineData("de", "german")]
    [InlineData("el", "greek")]
    [InlineData("hu", "hungarian")]
    [InlineData("id", "indonesian")]
    [InlineData("it", "italian")]
    [InlineData("ja", "japanese")]
    [InlineData("ko", "koreana")]
    [InlineData("es-419", "latam")]
    [InlineData("no", "norwegian")]
    [InlineData("pl", "polish")]
    [InlineData("pt-PT", "portuguese")]
    [InlineData("ro", "romanian")]
    [InlineData("ru", "russian")]
    [InlineData("zh-CN", "schinese")]
    [InlineData("es", "spanish")]
    [InlineData("sv", "swedish")]
    [InlineData("th", "thai")]
    [InlineData("tr", "turkish")]
    [InlineData("uk", "ukrainian")]
    [InlineData("vi", "vietnamese")]
    [InlineData("zh-TW", "tchinese")]
    [InlineData("zh-HK", "tchinese")]
    [InlineData("zh", "tchinese")]
    public void GetSteamLanguage_ReturnsExpectedLanguage(string cultureName, string expected)
    {
        var culture = new CultureInfo(cultureName);
        var language = SteamLanguageResolver.GetSteamLanguage(culture);
        Assert.Equal(expected, language);
    }

    [Fact]
    public void GetSteamLanguage_UnknownCulture_FallsBackToEnglish()
    {
        var culture = new CultureInfo("fa");
        var language = SteamLanguageResolver.GetSteamLanguage(culture);
        Assert.Equal("english", language);
    }

    [Fact]
    public void GetSteamLanguage_UsesCurrentUICulture()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("es-419");
            var language = SteamLanguageResolver.GetSteamLanguage();
            Assert.Equal("latam", language);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
