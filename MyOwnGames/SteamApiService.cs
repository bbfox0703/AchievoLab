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
    public class SteamApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private DateTime _lastApiCall = DateTime.MinValue;
        private readonly object _apiCallLock = new object();

        public SteamApiService(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MyOwnGames/1.0");
        }

        private Task ThrottleApiCallAsync()
        {
            return Task.Run(() =>
            {
                lock (_apiCallLock)
                {
                    var elapsed = DateTime.Now - _lastApiCall;
                    var minDelay = TimeSpan.FromSeconds(1.35); // 1.35 seconds minimum between API calls
                    if (elapsed < minDelay)
                    {
                        var waitTime = minDelay - elapsed;
                        Thread.Sleep(waitTime);
                    }
                    _lastApiCall = DateTime.Now;
                }
            });
        }

        public async Task<List<SteamGame>> GetOwnedGamesAsync(string steamId64, string targetLanguage = "tchinese", IProgress<double>? progress = null)
        {
            var games = new List<SteamGame>();

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
                    return games;
                }

                progress?.Report(30);

                var total = ownedGamesData.response.games.Length;
                progress?.Report(40);

                // Step 2: Process games with localized names (with throttling)
                for (int i = 0; i < total; i++)
                {
                    var game = ownedGamesData.response.games[i];
                    
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
                    
                    games.Add(steamGame);

                    // Log retrieval progress
                    DebugLogger.LogDebug($"Retrieving game {i + 1}/{total}: {steamGame.NameEn}");

                    // Update progress more smoothly
                    var gameProgress = 40 + (60.0 * (i + 1) / total);
                    progress?.Report(gameProgress);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching Steam games: {ex.Message}", ex);
            }

            return games;
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
            _httpClient?.Dispose();
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