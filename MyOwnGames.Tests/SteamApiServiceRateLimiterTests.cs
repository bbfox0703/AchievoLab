using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MyOwnGames;
using MyOwnGames.Services;
using Xunit;

public class SteamApiServiceRateLimiterTests
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef";
    private const string SteamId = "76561198000000000";

    [Fact]
    public async Task GetOwnedGamesAsync_RespectsSteamThrottle()
    {
        var rateLimiter = RateLimiterService.FromAppSettings();
        var lastCallField = typeof(RateLimiterService).GetField("_steamLastCall", BindingFlags.NonPublic | BindingFlags.Instance);
        lastCallField!.SetValue(rateLimiter, DateTime.UtcNow);

        using var httpClient = new HttpClient(new FakeSteamHandler());
        var service = new SteamApiService(ApiKey, httpClient, false, rateLimiter, rateLimiter);

        var sw = Stopwatch.StartNew();
        await service.GetOwnedGamesAsync(SteamId, targetLanguage: "english");
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetLocalizedGameNameAsync_RespectsSteamThrottle()
    {
        var rateLimiter = RateLimiterService.FromAppSettings();
        using var httpClient = new HttpClient(new FakeSteamHandler());
        var service = new SteamApiService(ApiKey, httpClient, false, rateLimiter, rateLimiter);

        var method = typeof(SteamApiService).GetMethod("GetLocalizedGameNameAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        // initial call to establish last call time
        var first = (Task<string>)method!.Invoke(service, new object[] { 10, "Test", "german", CancellationToken.None })!;
        await first;

        var sw = Stopwatch.StartNew();
        var second = (Task<string>)method.Invoke(service, new object[] { 10, "Test", "german", CancellationToken.None })!;
        await second;
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(5));
    }

    private class FakeSteamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("GetOwnedGames"))
            {
                var json = "{\"response\":{\"game_count\":1,\"games\":[{\"appid\":10,\"name\":\"Test\",\"playtime_forever\":0}]}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (uri.Contains("appdetails"))
            {
                var json = "{\"10\":{\"success\":true,\"data\":{\"name\":\"Test Local\"}}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
