using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Configuration;
using MyOwnGames.Services;
using Xunit;

public class RateLimiterConfigTests
{
    private static bool InvokeTryParseDouble(string value, out double result)
    {
        var method = typeof(RateLimiterService).GetMethod(
            "TryParseDouble", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { value, 0d };
        var ok = (bool)method!.Invoke(null, args)!;
        result = (double)args[1]!;
        return ok;
    }

    private static bool InvokeTryParseInt(string value, out int result)
    {
        var method = typeof(RateLimiterService).GetMethod(
            "TryParseInt", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { value, 0 };
        var ok = (bool)method!.Invoke(null, args)!;
        result = (int)args[1]!;
        return ok;
    }

    /// <summary>
    /// appsettings.json is always invariant-formatted. Under a culture that uses '.' as the
    /// group separator, culture-sensitive parsing turns "5.5" into 55 — a tenfold increase in
    /// every Steam throttle delay, with no error anywhere.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("es-ES")]
    [InlineData("it-IT")]
    [InlineData("pt-BR")]
    [InlineData("fr-FR")]
    public void ParsesInvariantDecimalsRegardlessOfCurrentCulture(string cultureName)
    {
        var prior = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);

            Assert.True(InvokeTryParseDouble("5.5", out var jitterMin));
            Assert.Equal(5.5, jitterMin);

            Assert.True(InvokeTryParseDouble("8.0", out var jitterMax));
            Assert.Equal(8.0, jitterMax);

            Assert.True(InvokeTryParseInt("15", out var maxCalls));
            Assert.Equal(15, maxCalls);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prior;
        }
    }

    [Fact]
    public void RejectsUnparseableValuesSoDefaultsSurvive()
    {
        Assert.False(InvokeTryParseDouble("not-a-number", out _));
        Assert.False(InvokeTryParseInt("", out _));
    }

    [Fact]
    public void BindsSteamSpecificValues()
    {
        var json = """
        {
          "RateLimiter": {
            "SteamMaxCallsPerMinute": 15,
            "SteamJitterMinSeconds": 6.5,
            "SteamJitterMaxSeconds": 8
          }
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var options = new RateLimiterOptions();
        config.GetSection("RateLimiter").Bind(options);

        Assert.Equal(15, options.SteamMaxCallsPerMinute);
        Assert.Equal(6.5, options.SteamJitterMinSeconds);
        Assert.Equal(8, options.SteamJitterMaxSeconds);
    }
}
