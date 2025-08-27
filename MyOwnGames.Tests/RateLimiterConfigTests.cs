using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using MyOwnGames.Services;
using Xunit;

public class RateLimiterConfigTests
{
    [Fact]
    public void BindsSteamSpecificValues()
    {
        var json = """
        {
          "RateLimiter": {
            "SteamMaxCallsPerMinute": 15,
            "SteamJitterMinSeconds": 6.5,
            "SteamJitterMaxSeconds": 7.5
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
        Assert.Equal(7.5, options.SteamJitterMaxSeconds);
    }
}
