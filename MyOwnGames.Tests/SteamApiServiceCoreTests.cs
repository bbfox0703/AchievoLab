using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MyOwnGames;
using MyOwnGames.Services;
using Xunit;

namespace MyOwnGames.Tests
{
    /// <summary>
    /// Comprehensive tests for SteamApiService core API methods
    /// Tests constructor validation, GetOwnedGamesAsync, rate limiting, error handling, and disposal
    /// </summary>
    public class SteamApiServiceCoreTests : IDisposable
    {
        private const string ValidApiKey = "0123456789abcdef0123456789abcdef";
        private const string ValidSteamId = "76561198000000000";
        private readonly RateLimiterService _testRateLimiter;

        public SteamApiServiceCoreTests()
        {
            // Create test rate limiter with no delays for fast tests
            _testRateLimiter = new RateLimiterService(new RateLimiterOptions
            {
                MaxCallsPerMinute = 100,
                JitterMinSeconds = 0,
                JitterMaxSeconds = 0,
                SteamMaxCallsPerMinute = 100,
                SteamJitterMinSeconds = 0,
                SteamJitterMaxSeconds = 0
            });
        }

        #region Constructor Validation Tests

        [Fact]
        public void Constructor_ValidApiKey_Success()
        {
            using var httpClient = new HttpClient();
            var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            Assert.NotNull(service);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("invalid")]
        [InlineData("12345")]
        [InlineData("0123456789abcdef0123456789abcdeg")] // contains 'g' (not hex)
        [InlineData("0123456789abcdef0123456789abcde")]  // 31 chars (too short)
        [InlineData("0123456789abcdef0123456789abcdef0")] // 33 chars (too long)
        public void Constructor_InvalidApiKey_ThrowsArgumentException(string? apiKey)
        {
            using var httpClient = new HttpClient();

            var ex = Assert.Throws<ArgumentException>(() =>
                new SteamApiService(apiKey!, httpClient, false, _testRateLimiter, _testRateLimiter));

            Assert.Contains("Invalid Steam API Key", ex.Message);
        }

        [Fact]
        public void Constructor_NullHttpClient_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new SteamApiService(ValidApiKey, null!, false, _testRateLimiter, _testRateLimiter));
        }

        [Fact]
        public void Constructor_DefaultRateLimiter_UsesAppSettings()
        {
            using var httpClient = new HttpClient();
            using var service = new SteamApiService(ValidApiKey, httpClient, false, null, null);

            // Should not throw - verifies that FromAppSettings() works
            Assert.NotNull(service);
        }

        #endregion

        #region GetOwnedGamesAsync - Success Path Tests

        [Fact]
        public async Task GetOwnedGamesAsync_ValidRequest_ReturnsGameCount()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, new[]
                {
                    new OwnedGame { appid = 10, name = "Counter-Strike", playtime_forever = 100 },
                    new OwnedGame { appid = 20, name = "Team Fortress 2", playtime_forever = 200 }
                });

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var count = await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english");

            Assert.Equal(2, count);
        }

        [Fact]
        public async Task GetOwnedGamesAsync_EnglishLanguage_CallsOnGameRetrievedWithEnglishNames()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, new[]
                {
                    new OwnedGame { appid = 10, name = "Counter-Strike", playtime_forever = 100 }
                });

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var retrievedGames = new List<SteamGame>();
            await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english",
                onGameRetrieved: game =>
                {
                    retrievedGames.Add(game);
                    return Task.CompletedTask;
                });

            Assert.Single(retrievedGames);
            Assert.Equal(10, retrievedGames[0].AppId);
            Assert.Equal("Counter-Strike", retrievedGames[0].NameEn);
            Assert.Equal("Counter-Strike", retrievedGames[0].NameLocalized);
        }

        [Fact]
        public async Task GetOwnedGamesAsync_NonEnglishLanguage_FetchesLocalizedNames()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, new[]
                {
                    new OwnedGame { appid = 10, name = "Counter-Strike", playtime_forever = 100 }
                })
                .WithAppDetailsResponse(10, "german", "Gegenschlag"); // Localized name

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var retrievedGames = new List<SteamGame>();
            await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "german",
                onGameRetrieved: game =>
                {
                    retrievedGames.Add(game);
                    return Task.CompletedTask;
                });

            Assert.Single(retrievedGames);
            Assert.Equal("Counter-Strike", retrievedGames[0].NameEn);
            Assert.Equal("Gegenschlag", retrievedGames[0].NameLocalized);
        }

        [Fact]
        public async Task GetOwnedGamesAsync_LocalizationFails_FallsBackToEnglish()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, new[]
                {
                    new OwnedGame { appid = 10, name = "Counter-Strike", playtime_forever = 100 }
                })
                .WithAppDetailsNotFound(10); // Localization fails

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var retrievedGames = new List<SteamGame>();
            await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "german",
                onGameRetrieved: game =>
                {
                    retrievedGames.Add(game);
                    return Task.CompletedTask;
                });

            Assert.Single(retrievedGames);
            Assert.Equal("Counter-Strike", retrievedGames[0].NameEn);
            Assert.Equal("Counter-Strike", retrievedGames[0].NameLocalized); // Fallback to English
        }

        [Fact]
        public async Task GetOwnedGamesAsync_EmptyGameList_ReturnsZero()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, Array.Empty<OwnedGame>());

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var count = await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english");

            Assert.Equal(0, count);
        }

        [Fact]
        public async Task GetOwnedGamesAsync_NullGamesArray_ReturnsZero()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, null); // null games array

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var count = await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english");

            Assert.Equal(0, count);
        }

        #endregion

        #region GetOwnedGamesAsync - Validation Tests

        [Theory]
        [InlineData("")]
        [InlineData("12345")]
        [InlineData("76561198")] // Too short
        [InlineData("765611980000000000")] // Too long
        [InlineData("8656119000000000")] // Wrong prefix
        public async Task GetOwnedGamesAsync_InvalidSteamId_ThrowsArgumentException(string steamId)
        {
            using var httpClient = new HttpClient(new MockSteamHttpHandler());
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                service.GetOwnedGamesAsync(steamId, targetLanguage: "english"));

            Assert.Contains("Invalid SteamID64", ex.Message);
        }

        [Fact]
        public async Task GetOwnedGamesAsync_NullSteamId_ThrowsException()
        {
            using var httpClient = new HttpClient(new MockSteamHttpHandler());
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            // Null steamId passes validation (because null?.Match fails), but results in HTTP error
            await Assert.ThrowsAsync<Exception>(() =>
                service.GetOwnedGamesAsync(null!, targetLanguage: "english"));
        }

        #endregion

        #region GetOwnedGamesAsync - Progress Reporting Tests

        [Fact]
        public async Task GetOwnedGamesAsync_WithProgress_ReportsProgressCorrectly()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, new[]
                {
                    new OwnedGame { appid = 10, name = "Game1", playtime_forever = 0 },
                    new OwnedGame { appid = 20, name = "Game2", playtime_forever = 0 }
                });

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var progressReports = new List<double>();
            var progress = new Progress<double>(p => progressReports.Add(p));

            await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english",
                onGameRetrieved: async game =>
                {
                    // Wait a bit to allow progress updates to propagate
                    await Task.Delay(10);
                },
                progress: progress);

            // Give progress a moment to update
            await Task.Delay(50);

            Assert.NotEmpty(progressReports);
            // Progress should include initial stages and reach 100
            Assert.True(progressReports.Count >= 2, $"Expected at least 2 progress reports, got {progressReports.Count}");
            Assert.Contains(progressReports, p => p >= 10 && p <= 35); // Early progress
            Assert.Contains(progressReports, p => p >= 60); // Late progress (games being processed)
        }

        #endregion

        #region GetOwnedGamesAsync - Caching Tests

        [Fact]
        public async Task GetOwnedGamesAsync_WithExistingAppIds_SkipsThoseGames()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, new[]
                {
                    new OwnedGame { appid = 10, name = "Game1", playtime_forever = 0 },
                    new OwnedGame { appid = 20, name = "Game2", playtime_forever = 0 },
                    new OwnedGame { appid = 30, name = "Game3", playtime_forever = 0 }
                });

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var existingAppIds = new HashSet<int> { 10, 30 }; // Skip games 10 and 30
            var retrievedGames = new List<SteamGame>();

            await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english",
                onGameRetrieved: game =>
                {
                    retrievedGames.Add(game);
                    return Task.CompletedTask;
                },
                existingAppIds: existingAppIds);

            Assert.Single(retrievedGames);
            Assert.Equal(20, retrievedGames[0].AppId); // Only game 20 should be retrieved
        }

        [Fact]
        public async Task GetOwnedGamesAsync_WithExistingLocalizedNames_UsesCache()
        {
            var handler = new MockSteamHttpHandler()
                .WithOwnedGamesResponse(ValidSteamId, new[]
                {
                    new OwnedGame { appid = 10, name = "Counter-Strike", playtime_forever = 0 }
                });
            // Note: NOT adding AppDetailsResponse - if cache works, it won't be called

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var existingLocalizedNames = new Dictionary<int, string>
            {
                { 10, "Cached German Name" }
            };

            var retrievedGames = new List<SteamGame>();
            await service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "german",
                onGameRetrieved: game =>
                {
                    retrievedGames.Add(game);
                    return Task.CompletedTask;
                },
                existingLocalizedNames: existingLocalizedNames);

            Assert.Single(retrievedGames);
            Assert.Equal("Cached German Name", retrievedGames[0].NameLocalized);
        }

        #endregion

        #region GetOwnedGamesAsync - Cancellation Tests

        [Fact]
        public async Task GetOwnedGamesAsync_CancelledDuringExecution_ThrowsOperationCanceledException()
        {
            var handler = new MockSteamHttpHandler()
                .WithDelayedResponse(ValidSteamId, 1000) // 1 second delay
                .WithOwnedGamesResponse(ValidSteamId, new[]
                {
                    new OwnedGame { appid = 10, name = "Game1", playtime_forever = 0 }
                });

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var cts = new CancellationTokenSource();
            var task = service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english", cancellationToken: cts.Token);

            // Cancel after a short delay to ensure request has started
            await Task.Delay(50);
            cts.Cancel();

            // TaskCanceledException is wrapped in Exception due to catch block in GetOwnedGamesAsync
            await Assert.ThrowsAsync<Exception>(() => task);
        }

        #endregion

        #region Rate Limiting Tests

        [Fact]
        public async Task GetOwnedGamesAsync_After429Response_BlocksSteamApiFor30Minutes()
        {
            var handler = new MockSteamHttpHandler()
                .WithRateLimitResponse(); // Return 429

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            // First call triggers 429 - HttpRequestException is wrapped in Exception
            var ex1 = await Assert.ThrowsAsync<Exception>(() =>
                service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english"));
            Assert.Contains("rate limit exceeded", ex1.Message);

            // Second call should be blocked before making HTTP request
            var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english"));
            Assert.Contains("Steam API is currently blocked", ex2.Message);
        }

        #endregion

        #region HTTP Error Handling Tests

        [Fact]
        public async Task GetOwnedGamesAsync_HttpRequestFails_ThrowsExceptionWithMessage()
        {
            var handler = new MockSteamHttpHandler()
                .WithHttpError(HttpStatusCode.InternalServerError);

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            var ex = await Assert.ThrowsAsync<Exception>(() =>
                service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english"));

            Assert.Contains("Error fetching Steam games", ex.Message);
        }

        [Fact]
        public async Task GetOwnedGamesAsync_Unauthorized_ThrowsHttpRequestException()
        {
            var handler = new MockSteamHttpHandler()
                .WithHttpError(HttpStatusCode.Unauthorized);

            using var httpClient = new HttpClient(handler);
            using var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            await Assert.ThrowsAsync<Exception>(() =>
                service.GetOwnedGamesAsync(ValidSteamId, targetLanguage: "english"));
        }

        #endregion

        #region Disposal Tests

        [Fact]
        public async Task Dispose_WithDisposeHttpClientTrue_DisposesHttpClient()
        {
            var httpClient = new HttpClient(new MockSteamHttpHandler());
            var service = new SteamApiService(ValidApiKey, httpClient, disposeHttpClient: true, _testRateLimiter, _testRateLimiter);

            service.Dispose();

            // Verify HttpClient is disposed by attempting to use it
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await httpClient.GetAsync("http://test.com"));
        }

        [Fact]
        public void Dispose_WithDisposeHttpClientFalse_DoesNotDisposeHttpClient()
        {
            var httpClient = new HttpClient(new MockSteamHttpHandler());
            var service = new SteamApiService(ValidApiKey, httpClient, disposeHttpClient: false, _testRateLimiter, _testRateLimiter);

            service.Dispose();

            // HttpClient should still be usable
            var task = httpClient.GetAsync("http://test.com");
            Assert.NotNull(task);
        }

        [Fact]
        public void Dispose_CalledMultipleTimes_DoesNotThrow()
        {
            using var httpClient = new HttpClient(new MockSteamHttpHandler());
            var service = new SteamApiService(ValidApiKey, httpClient, false, _testRateLimiter, _testRateLimiter);

            service.Dispose();
            service.Dispose(); // Should not throw
            service.Dispose(); // Should not throw
        }

        #endregion

        public void Dispose()
        {
            // Cleanup test resources if needed
        }
    }

    #region Mock HTTP Handler

    internal class MockSteamHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<Task<HttpResponseMessage>>> _responses = new();
        private HttpStatusCode? _errorCode;
        private bool _returnRateLimit;
        private int _delayMilliseconds;

        public MockSteamHttpHandler WithDelayedResponse(string steamId, int delayMs)
        {
            _delayMilliseconds = delayMs;
            return this;
        }

        public MockSteamHttpHandler WithOwnedGamesResponse(string steamId, OwnedGame[]? games)
        {
            var responseData = new OwnedGamesResponse
            {
                response = games != null ? new OwnedGamesResponseData
                {
                    game_count = games.Length,
                    games = games
                } : null
            };

            var json = System.Text.Json.JsonSerializer.Serialize(responseData);
            _responses[$"GetOwnedGames_{steamId}"] = () => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                });

            return this;
        }

        public MockSteamHttpHandler WithAppDetailsResponse(int appId, string language, string localizedName)
        {
            var responseData = new Dictionary<string, AppDetailsResponse>
            {
                { appId.ToString(), new AppDetailsResponse
                    {
                        success = true,
                        data = new AppData { name = localizedName }
                    }
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(responseData);
            _responses[$"appdetails_{appId}_{language}"] = () => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                });

            return this;
        }

        public MockSteamHttpHandler WithAppDetailsNotFound(int appId)
        {
            var responseData = new Dictionary<string, AppDetailsResponse>
            {
                { appId.ToString(), new AppDetailsResponse { success = false, data = null } }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(responseData);
            _responses[$"appdetails_{appId}"] = () => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                });

            return this;
        }

        public MockSteamHttpHandler WithHttpError(HttpStatusCode errorCode)
        {
            _errorCode = errorCode;
            return this;
        }

        public MockSteamHttpHandler WithRateLimitResponse()
        {
            _returnRateLimit = true;
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Apply delay if configured
            if (_delayMilliseconds > 0)
            {
                await Task.Delay(_delayMilliseconds, cancellationToken);
            }

            if (_returnRateLimit)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            if (_errorCode.HasValue)
            {
                return new HttpResponseMessage(_errorCode.Value);
            }

            var uri = request.RequestUri!.AbsoluteUri;

            // Match GetOwnedGames request
            if (uri.Contains("GetOwnedGames"))
            {
                var steamIdMatch = System.Text.RegularExpressions.Regex.Match(uri, @"steamid=(\d+)");
                if (steamIdMatch.Success)
                {
                    var steamId = steamIdMatch.Groups[1].Value;
                    var key = $"GetOwnedGames_{steamId}";
                    if (_responses.TryGetValue(key, out var response))
                    {
                        return await response();
                    }
                }
            }

            // Match appdetails request
            if (uri.Contains("appdetails"))
            {
                var appIdMatch = System.Text.RegularExpressions.Regex.Match(uri, @"appids=(\d+)");
                var langMatch = System.Text.RegularExpressions.Regex.Match(uri, @"l=(\w+)");

                if (appIdMatch.Success)
                {
                    var appId = appIdMatch.Groups[1].Value;
                    var language = langMatch.Success ? langMatch.Groups[1].Value : "";

                    // Try with language first
                    if (!string.IsNullOrEmpty(language))
                    {
                        var keyWithLang = $"appdetails_{appId}_{language}";
                        if (_responses.TryGetValue(keyWithLang, out var response))
                        {
                            return await response();
                        }
                    }

                    // Try without language
                    var key = $"appdetails_{appId}";
                    if (_responses.TryGetValue(key, out var response2))
                    {
                        return await response2();
                    }
                }
            }

            // Default: return 404
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    #endregion
}
