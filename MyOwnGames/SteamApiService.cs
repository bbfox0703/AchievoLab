using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using MyOwnGames.Services;
using CommonUtilities;

namespace MyOwnGames
{
    public class SteamApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly RateLimiterService _rateLimiter;
        private readonly RateLimiterService _steamRateLimiter;
        private readonly bool _disposeHttpClient;
        private bool _disposed;

        // Steam API rate limit tracking
        private DateTime? _steamApiBlockedUntil = null;
        private readonly object _blockLock = new();

        private static readonly JsonSerializerOptions JsonOptions =
            new() { TypeInfoResolver = SteamApiJsonContext.Default };

        public SteamApiService(string apiKey)
            : this(apiKey, HttpClientProvider.Shared, false, null, null)
        {
        }

        public SteamApiService(string apiKey, HttpClient httpClient, bool disposeHttpClient = false, RateLimiterService? rateLimiter = null, RateLimiterService? steamRateLimiter = null)
        {
            ValidateCredentials(apiKey);
            _apiKey = apiKey;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _disposeHttpClient = disposeHttpClient;
            _rateLimiter = rateLimiter ?? RateLimiterService.FromAppSettings();
            _steamRateLimiter = steamRateLimiter ?? _rateLimiter;

            if (_disposeHttpClient)
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(30);
                if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                }
            }
        }

        private static void ValidateCredentials(string apiKey, string? steamId64 = null)
        {
            if (!InputValidator.IsValidApiKey(apiKey))
                throw new ArgumentException("Invalid Steam API Key. It must be 32 hexadecimal characters.", nameof(apiKey));
            if (steamId64 != null && !InputValidator.IsValidSteamId64(steamId64))
                throw new ArgumentException("Invalid SteamID64. It must be a 17-digit number starting with 7656119.", nameof(steamId64));
        }

        public async Task<int> GetOwnedGamesAsync(string steamId64, string targetLanguage = "english", Func<SteamGame, Task>? onGameRetrieved = null, IProgress<double>? progress = null, ISet<int>? existingAppIds = null, IDictionary<int, string>? existingLocalizedNames = null, CancellationToken cancellationToken = default)
        {
            ValidateCredentials(_apiKey, steamId64);

            // Check if Steam API is currently blocked
            if (IsSteamApiBlocked())
            {
                throw new InvalidOperationException("Steam API is currently blocked due to rate limiting. Please wait 30 minutes before trying again.");
            }

            try
            {
                // Step 1: Get owned games with throttling
                progress?.Report(10);
                cancellationToken.ThrowIfCancellationRequested();

                await _steamRateLimiter.WaitSteamAsync();
                cancellationToken.ThrowIfCancellationRequested();

                var ownedGamesUrl = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={_apiKey}&steamid={steamId64}&format=json&include_appinfo=true";
                var ownedGamesResponse = await GetStringWithRateLimitCheckAsync(ownedGamesUrl, cancellationToken);
                var ownedGamesData = DeserializeOwnedGamesResponse(ownedGamesResponse);

                if (ownedGamesData?.response?.games == null)
                {
                    return 0;
                }

                progress?.Report(30);

                var total = ownedGamesData.response.games.Length;

                // Step 2: Process games with localized names (with throttling)
                for (int i = 0; i < total; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var game = ownedGamesData.response.games[i];

                    if (existingAppIds != null && existingAppIds.Contains(game.appid))
                    {
                        var skipProgress = 30 + (70.0 * (i + 1) / total);
                        progress?.Report(skipProgress);
                        continue;
                    }

                    // Get localized name if not English, using existing data when available
                    string localizedName = game.name; // Default to English name from owned games API
                    if (targetLanguage != "english")
                    {
                        if (existingLocalizedNames != null && existingLocalizedNames.TryGetValue(game.appid, out var cachedName))
                        {
                            localizedName = cachedName;
                        }
                        else
                        {
                            localizedName = await GetLocalizedGameNameAsync(game.appid, game.name, targetLanguage, cancellationToken);
                        }
                    }

                    var steamGame = new SteamGame
                    {
                        AppId = game.appid,
                        NameEn = game.name,
                        NameLocalized = localizedName,
                        IconUrl = GetGameImageUrl(game.appid, targetLanguage),
                        PlaytimeForever = game.playtime_forever
                    };

                    // Log retrieval progress
                    AppLogger.LogDebug($"Retrieving game {i + 1}/{total}: {steamGame.NameEn}");

                    if (onGameRetrieved != null)
                    {
                        await onGameRetrieved(steamGame);
                    }

                    // Update progress more smoothly
                    var gameProgress = 30 + (70.0 * (i + 1) / total);
                    progress?.Report(gameProgress);
                }

                return total;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching Steam games: {ex.Message}", ex);
            }
        }


        private async Task<string> GetLocalizedGameNameAsync(int appId, string englishName, string targetLanguage, CancellationToken cancellationToken = default)
        {
            if (targetLanguage == "english")
                return englishName;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // More aggressive rate limiting for Steam Store API
                await _steamRateLimiter.WaitSteamAsync();
                cancellationToken.ThrowIfCancellationRequested();
                
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={targetLanguage}";
                AppLogger.LogDebug($"Fetching localized name for {appId} ({englishName}) in {targetLanguage}");

                var response = await GetStringWithRateLimitCheckAsync(url, cancellationToken);
                var data = DeserializeAppDetailsResponse(response);
                
                if (data != null && data.TryGetValue(appId.ToString(), out var appDetails) && 
                    appDetails.success && !string.IsNullOrEmpty(appDetails.data?.name))
                {
                    var localizedName = appDetails.data.name;
                    AppLogger.LogDebug($"Got localized name for {appId}: '{localizedName}' (was: '{englishName}')");
                    return localizedName;
                }
                else
                {
                    AppLogger.LogDebug($"No localized name available for {appId} in {targetLanguage}, using English fallback");
                }
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests") || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                // 429 already recorded by GetStringWithRateLimitCheckAsync, Steam API blocked for 30 minutes
                AppLogger.LogDebug($"Rate limited when getting localized name for {appId}, using English fallback. Steam API blocked for 30 minutes.");
            }
            catch (Exception ex)
            {
                // Log error and fall back to English name
                AppLogger.LogDebug($"Error getting localized name for {appId}: {ex.Message}");
            }

            return englishName; // Return English name as fallback
        }

        private string GetGameImageUrl(int appId, string language = "english")
        {
            // Return default header for English, otherwise try language-specific header
            if (language == "english")
            {
                return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
            }

            return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg";
        }

        /// <summary>
        /// Check if Steam API is currently blocked due to 429 rate limit
        /// </summary>
        private bool IsSteamApiBlocked()
        {
            lock (_blockLock)
            {
                if (_steamApiBlockedUntil.HasValue)
                {
                    if (DateTime.UtcNow < _steamApiBlockedUntil.Value)
                    {
                        var timeRemaining = _steamApiBlockedUntil.Value - DateTime.UtcNow;
                        AppLogger.LogDebug($"Steam API is blocked for {timeRemaining.TotalMinutes:F1} more minutes");
                        return true;
                    }
                    else
                    {
                        // Block has expired
                        _steamApiBlockedUntil = null;
                        AppLogger.LogDebug("Steam API block has expired");
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Record that Steam API returned 429 and block it for 30 minutes
        /// </summary>
        private void RecordSteamApiRateLimit()
        {
            lock (_blockLock)
            {
                _steamApiBlockedUntil = DateTime.UtcNow.AddMinutes(30);
                AppLogger.LogDebug($"Steam API blocked until {_steamApiBlockedUntil.Value:HH:mm:ss} (30 minutes)");
            }
        }

        /// <summary>
        /// Safe HTTP GET with 429 detection
        /// </summary>
        private async Task<string> GetStringWithRateLimitCheckAsync(string url, CancellationToken cancellationToken = default)
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                RecordSteamApiRateLimit();
                throw new HttpRequestException($"Steam API rate limit exceeded (429). Blocked for 30 minutes.");
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        private static OwnedGamesResponse? DeserializeOwnedGamesResponse(string json)
        {
            return JsonSerializer.Deserialize(json, SteamApiJsonContext.Default.OwnedGamesResponse);
        }

        private static Dictionary<string, AppDetailsResponse>? DeserializeAppDetailsResponse(string json)
        {
            return JsonSerializer.Deserialize(json, SteamApiJsonContext.Default.DictionaryStringAppDetailsResponse);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_disposeHttpClient)
            {
                _httpClient.Dispose();
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // Data models for JSON deserialization
    public class SteamGame
    {
        public int AppId { get; set; }
        public string NameEn { get; set; } = "";
        public string NameLocalized { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public int PlaytimeForever { get; set; }
    }

    public class OwnedGamesResponse
    {
        public OwnedGamesResponseData? response { get; set; }
    }

    public class OwnedGamesResponseData
    {
        public int game_count { get; set; }
        public OwnedGame[]? games { get; set; }
    }

    public class OwnedGame
    {
        public int appid { get; set; }
        public string name { get; set; } = "";
        public int playtime_forever { get; set; }
        public string img_icon_url { get; set; } = "";
        public string img_logo_url { get; set; } = "";
    }

    public class AppListResponse
    {
        public AppList? applist { get; set; }
    }

    public class AppList
    {
        public AppInfo[]? apps { get; set; }
    }

    public class AppInfo
    {
        public int appid { get; set; }
        public string name { get; set; } = "";
    }

    // For Steam Store API response
    public class AppDetailsResponse
    {
        public bool success { get; set; }
        public AppData? data { get; set; }
    }

    public class AppData
    {
        public string name { get; set; } = "";
        public string header_image { get; set; } = "";
    }
}
