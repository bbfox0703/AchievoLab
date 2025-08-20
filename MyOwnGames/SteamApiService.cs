using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Globalization;

namespace MyOwnGames
{
    public class SteamApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public SteamApiService(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task<List<SteamGame>> GetOwnedGamesAsync(string steamId64, IProgress<double>? progress = null)
        {
            var games = new List<SteamGame>();

            try
            {
                // Step 1: Get owned games
                progress?.Report(10);
                var ownedGamesUrl = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={_apiKey}&steamid={steamId64}&format=json&include_appinfo=true";
                var ownedGamesResponse = await _httpClient.GetStringAsync(ownedGamesUrl);
                var ownedGamesData = JsonSerializer.Deserialize<OwnedGamesResponse>(ownedGamesResponse);

                if (ownedGamesData?.response?.games == null)
                {
                    return games;
                }

                progress?.Report(30);

                // Step 2: Get app list for localized names
                var appListUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
                var appListResponse = await _httpClient.GetStringAsync(appListUrl);
                var appListData = JsonSerializer.Deserialize<AppListResponse>(appListResponse);

                progress?.Report(60);

                // Create dictionary for faster lookup
                var appDict = new Dictionary<int, string>();
                if (appListData?.applist?.apps != null)
                {
                    foreach (var app in appListData.applist.apps)
                    {
                        if (!appDict.ContainsKey(app.appid))
                        {
                            appDict[app.appid] = app.name;
                        }
                    }
                }

                progress?.Report(80);

                // Step 3: Build game list
                var currentCulture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var total = ownedGamesData.response.games.Length;
                
                for (int i = 0; i < total; i++)
                {
                    var game = ownedGamesData.response.games[i];
                    var steamGame = new SteamGame
                    {
                        AppId = game.appid,
                        NameEn = game.name,
                        NameLocalized = GetLocalizedName(game.name, currentCulture),
                        IconUrl = $"https://media.steampowered.com/steamcommunity/public/images/apps/{game.appid}/{game.img_icon_url}.jpg",
                        PlaytimeForever = game.playtime_forever
                    };
                    
                    games.Add(steamGame);
                    
                    // Update progress
                    var gameProgress = 80 + (20.0 * (i + 1) / total);
                    progress?.Report(gameProgress);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching Steam games: {ex.Message}", ex);
            }

            return games;
        }

        private string GetLocalizedName(string englishName, string culture)
        {
            // Simple localization mapping for demo purposes
            // In a real implementation, you might use Steam's localization API or a translation service
            if (culture == "zh")
            {
                var translations = new Dictionary<string, string>
                {
                    { "Cyberpunk 2077", "賽博龐克 2077" },
                    { "Red Dead Redemption 2", "荒野大鏢客 2" },
                    { "Counter-Strike 2", "絕對武力 2" },
                    { "Dota 2", "刀塔 2" }
                };
                
                if (translations.TryGetValue(englishName, out var translated))
                {
                    return translated;
                }
            }
            
            return englishName; // Return English name as fallback
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
}