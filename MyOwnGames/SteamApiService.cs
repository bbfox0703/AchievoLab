using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Globalization;
using System.Threading;
using MyOwnGames.Services;

namespace MyOwnGames
{
    public class SteamApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private DateTime _lastApiCall = DateTime.MinValue;
        private readonly SemaphoreSlim _apiSemaphore = new(1, 1);
        private readonly bool _disposeHttpClient;
        private bool _disposed;

        public SteamApiService(string apiKey)
            : this(apiKey, new HttpClient(), true)
        {
        }

        public SteamApiService(string apiKey, HttpClient httpClient, bool disposeHttpClient = false)
        {
            ValidateCredentials(apiKey);
            _apiKey = apiKey;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _disposeHttpClient = disposeHttpClient;
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "MyOwnGames/1.0");
            }
        }

        private async Task ThrottleApiCallAsync()
        {
            await _apiSemaphore.WaitAsync();
            try
            {
                var elapsed = DateTime.UtcNow - _lastApiCall;
                var minDelay = TimeSpan.FromSeconds(2.5); // 2.5 seconds minimum between API calls
                if (elapsed < minDelay)
                {
                    var waitTime = minDelay - elapsed;
                    await Task.Delay(waitTime);
                }
                _lastApiCall = DateTime.UtcNow;
            }
            finally
            {
                _apiSemaphore.Release();
            }
        }

        private static void ValidateCredentials(string apiKey, string? steamId64 = null)
        {
            if (!InputValidator.IsValidApiKey(apiKey))
                throw new ArgumentException("Invalid Steam API Key. It must be 32 hexadecimal characters.", nameof(apiKey));
            if (steamId64 != null && !InputValidator.IsValidSteamId64(steamId64))
                throw new ArgumentException("Invalid SteamID64. It must be a 17-digit number starting with 7656119.", nameof(steamId64));
        }

        public async Task<int> GetOwnedGamesAsync(string steamId64, string targetLanguage = "tchinese", Func<SteamGame, Task>? onGameRetrieved = null, IProgress<double>? progress = null, ISet<int>? existingAppIds = null)
        {
            ValidateCredentials(_apiKey, steamId64);
            try
            {
                // Step 1: Get owned games with throttling
                progress?.Report(10);
                await ThrottleApiCallAsync();
                var ownedGamesUrl = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={_apiKey}&steamid={steamId64}&format=json&include_appinfo=true";
                var ownedGamesResponse = await _httpClient.GetStringAsync(ownedGamesUrl);
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
                    var game = ownedGamesData.response.games[i];

                    if (existingAppIds != null && existingAppIds.Contains(game.appid))
                    {
                        var skipProgress = 30 + (70.0 * (i + 1) / total);
                        progress?.Report(skipProgress);
                        continue;
                    }

                    // Get localized name if not English
                    string localizedName = game.name; // Default to English name from owned games API
                    if (targetLanguage != "english")
                    {
                        localizedName = await GetLocalizedGameNameAsync(game.appid, game.name, targetLanguage);
                    }

                    var steamGame = new SteamGame
                    {
                        AppId = game.appid,
                        NameEn = game.name,
                        NameLocalized = localizedName,
                        IconUrl = GetGameImageUrl(game.appid),
                        PlaytimeForever = game.playtime_forever
                    };

                    // Log retrieval progress
                    DebugLogger.LogDebug($"Retrieving game {i + 1}/{total}: {steamGame.NameEn}");

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


        private async Task<string> GetLocalizedGameNameAsync(int appId, string englishName, string targetLanguage)
        {
            if (targetLanguage == "english")
                return englishName;

            try
            {
                await ThrottleApiCallAsync();
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={targetLanguage}";
                var response = await _httpClient.GetStringAsync(url);
                var data = DeserializeAppDetailsResponse(response);
                
                if (data != null && data.TryGetValue(appId.ToString(), out var appDetails) && 
                    appDetails.success && !string.IsNullOrEmpty(appDetails.data?.name))
                {
                    return appDetails.data.name;
                }
            }
            catch (Exception ex)
            {
                // Log error and fall back to English name
                DebugLogger.LogDebug($"Error getting localized name for {appId}: {ex.Message}");
            }

            return englishName; // Return English name as fallback
        }

        private string GetGameImageUrl(int appId)
        {
            // Use the same pattern as AnSAM GameImageUrlResolver
            // Priority: small_capsule -> logo -> library_600x900 -> header_image
            return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "The types used in JSON deserialization are explicitly referenced and won't be trimmed")]
        private static OwnedGamesResponse? DeserializeOwnedGamesResponse(string json)
        {
            return JsonSerializer.Deserialize<OwnedGamesResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "The types used in JSON deserialization are explicitly referenced and won't be trimmed")]
        private static Dictionary<string, AppDetailsResponse>? DeserializeAppDetailsResponse(string json)
        {
            return JsonSerializer.Deserialize<Dictionary<string, AppDetailsResponse>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
