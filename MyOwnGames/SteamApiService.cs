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
    /// <summary>
    /// Provides access to Steam Web API for retrieving owned games and localized game information.
    /// Implements rate limiting and 429 detection to prevent API blocks.
    /// </summary>
    /// <remarks>
    /// This service interacts with two Steam APIs:
    /// 1. Steam Web API (IPlayerService/GetOwnedGames) - Requires API key, returns owned games with English names
    /// 2. Steam Store API (appdetails) - Public endpoint, returns localized game names and details
    ///
    /// Rate limiting strategy:
    /// - Uses separate rate limiters for general and Steam-specific calls
    /// - Default: 30 general calls/minute, 12 Steam calls/minute
    /// - Randomized jitter delays (5.5-8 seconds for Steam)
    /// - Automatic 30-minute block on HTTP 429 response
    ///
    /// Thread-safe and supports cancellation for all async operations.
    /// </remarks>
    public class SteamApiService : IDisposable
    {
        /// <summary>
        /// HTTP client used for all API requests.
        /// May be shared or owned depending on constructor.
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Steam Web API key used for authenticated endpoints.
        /// Must be 32 hexadecimal characters.
        /// </summary>
        private readonly string _apiKey;

        /// <summary>
        /// Rate limiter for general HTTP requests.
        /// Default: 30 calls/minute with 1.5-3 second jitter.
        /// </summary>
        private readonly RateLimiterService _rateLimiter;

        /// <summary>
        /// Rate limiter specifically for Steam Web API calls.
        /// More restrictive: 12 calls/minute with 5.5-8 second jitter.
        /// </summary>
        private readonly RateLimiterService _steamRateLimiter;

        /// <summary>
        /// Flag indicating whether this instance should dispose the HttpClient on disposal.
        /// True if HttpClient was created by this instance, false if shared.
        /// </summary>
        private readonly bool _disposeHttpClient;

        /// <summary>
        /// Flag indicating whether this instance has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Timestamp until which Steam API calls are blocked due to HTTP 429 rate limiting.
        /// Null if not currently blocked.
        /// </summary>
        private DateTime? _steamApiBlockedUntil = null;

        /// <summary>
        /// Lock object for thread-safe access to _steamApiBlockedUntil.
        /// </summary>
        private readonly object _blockLock = new();

        /// <summary>
        /// JSON serializer options configured for Steam API response deserialization.
        /// Uses source-generated context for AOT compatibility.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions =
            new() { TypeInfoResolver = SteamApiJsonContext.Default };

        /// <summary>
        /// Initializes a new instance of <see cref="SteamApiService"/> with the specified API key.
        /// Uses the shared HttpClient and default rate limiter configuration.
        /// </summary>
        /// <param name="apiKey">Steam Web API key (must be 32 hexadecimal characters).</param>
        /// <exception cref="ArgumentException">Thrown if API key is invalid.</exception>
        /// <remarks>
        /// This constructor uses:
        /// - Shared HttpClient from HttpClientProvider (not disposed by this instance)
        /// - Rate limiter settings from appsettings.json (or defaults if not configured)
        /// </remarks>
        public SteamApiService(string apiKey)
            : this(apiKey, HttpClientProvider.Shared, false, null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="SteamApiService"/> with full configuration control.
        /// </summary>
        /// <param name="apiKey">Steam Web API key (must be 32 hexadecimal characters).</param>
        /// <param name="httpClient">HTTP client to use for requests.</param>
        /// <param name="disposeHttpClient">Whether to dispose the HttpClient when this instance is disposed.</param>
        /// <param name="rateLimiter">Rate limiter for general API calls (null to create from appsettings.json).</param>
        /// <param name="steamRateLimiter">Rate limiter for Steam API calls (null to use same as rateLimiter).</param>
        /// <exception cref="ArgumentNullException">Thrown if httpClient is null.</exception>
        /// <exception cref="ArgumentException">Thrown if API key is invalid.</exception>
        /// <remarks>
        /// If disposeHttpClient is true, this constructor configures the HttpClient with:
        /// - 30-second timeout
        /// - Chrome-like User-Agent header (if not already set)
        /// </remarks>
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

        /// <summary>
        /// Validates Steam API credentials (API key and optionally Steam ID).
        /// </summary>
        /// <param name="apiKey">Steam Web API key to validate (must be 32 hex characters).</param>
        /// <param name="steamId64">Optional Steam ID to validate (must be 17-digit number starting with 7656119).</param>
        /// <exception cref="ArgumentException">Thrown if API key or Steam ID is invalid.</exception>
        private static void ValidateCredentials(string apiKey, string? steamId64 = null)
        {
            if (!InputValidator.IsValidApiKey(apiKey))
                throw new ArgumentException("Invalid Steam API Key. It must be 32 hexadecimal characters.", nameof(apiKey));
            if (steamId64 != null && !InputValidator.IsValidSteamId64(steamId64))
                throw new ArgumentException("Invalid SteamID64. It must be a 17-digit number starting with 7656119.", nameof(steamId64));
        }

        /// <summary>
        /// Retrieves all owned games for a Steam user with optional localized names.
        /// Supports incremental retrieval and progress reporting.
        /// </summary>
        /// <param name="steamId64">Steam ID of the user (17-digit number starting with 7656119).</param>
        /// <param name="targetLanguage">Language for localized names (default: "english"). Examples: "tchinese", "japanese", "korean".</param>
        /// <param name="onGameRetrieved">Optional callback invoked for each game retrieved (for incremental processing).</param>
        /// <param name="progress">Optional progress reporter (0-100 percentage).</param>
        /// <param name="existingAppIds">Optional set of already-retrieved AppIDs to skip.</param>
        /// <param name="existingLocalizedNames">Optional dictionary of already-retrieved localized names to reuse.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>Total number of games owned by the user.</returns>
        /// <exception cref="ArgumentException">Thrown if API key or Steam ID is invalid.</exception>
        /// <exception cref="InvalidOperationException">Thrown if Steam API is currently blocked due to rate limiting.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled.</exception>
        /// <exception cref="Exception">Thrown for other errors during game retrieval.</exception>
        /// <remarks>
        /// This method:
        /// 1. Checks if Steam API is currently blocked (30-minute block after HTTP 429)
        /// 2. Retrieves owned games list from Steam Web API (IPlayerService/GetOwnedGames)
        /// 3. For each game (unless in existingAppIds):
        ///    - Uses cached localized name if available in existingLocalizedNames
        ///    - Otherwise fetches localized name from Steam Store API if targetLanguage != "english"
        ///    - Invokes onGameRetrieved callback if provided
        ///    - Reports progress (30% for initial fetch, 30-100% for per-game processing)
        ///
        /// Rate limiting is enforced throughout:
        /// - Steam Web API calls use stricter rate limiter (12/minute, 5.5-8s jitter)
        /// - Progress is smoothly reported as games are processed
        ///
        /// If HTTP 429 is received, Steam API is blocked for 30 minutes and exception is thrown.
        /// </remarks>
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


        /// <summary>
        /// Retrieves the localized name for a specific game from the Steam Store API.
        /// Falls back to English name if localization is unavailable or on error.
        /// </summary>
        /// <param name="appId">Steam App ID of the game.</param>
        /// <param name="englishName">English name of the game (used as fallback).</param>
        /// <param name="targetLanguage">Target language code (e.g., "tchinese", "japanese", "korean").</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>Localized game name if available, otherwise English name.</returns>
        /// <remarks>
        /// This method:
        /// - Returns English name immediately if targetLanguage is "english"
        /// - Uses Steam Store API endpoint (store.steampowered.com/api/appdetails)
        /// - Applies aggressive rate limiting via WaitSteamAsync
        /// - Falls back to English name on any error (HTTP 429, network error, missing data)
        ///
        /// HTTP 429 responses trigger automatic 30-minute block.
        /// All errors are logged but not thrown (silent fallback to English).
        /// </remarks>
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
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
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

        /// <summary>
        /// Constructs the CDN URL for a game's header image.
        /// Returns language-specific URL if language is not English.
        /// </summary>
        /// <param name="appId">Steam App ID of the game.</param>
        /// <param name="language">Language code for the image (default: "english").</param>
        /// <returns>Full URL to the game's header image on Steam CDN.</returns>
        /// <remarks>
        /// URL formats:
        /// - English: https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg
        /// - Other languages: https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg
        ///
        /// The actual image downloading and caching is handled by SharedImageService/GameImageCache.
        /// This method only constructs the URL.
        /// </remarks>
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
        /// Checks if Steam API is currently blocked due to HTTP 429 rate limit.
        /// </summary>
        /// <returns>True if currently blocked, false otherwise.</returns>
        /// <remarks>
        /// Thread-safe check using _blockLock.
        /// Automatically clears the block if the 30-minute timeout has expired.
        /// Logs remaining block time or expiration.
        /// </remarks>
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
        /// Records that Steam API returned HTTP 429 and blocks it for 30 minutes.
        /// </summary>
        /// <remarks>
        /// Thread-safe operation using _blockLock.
        /// Sets _steamApiBlockedUntil to 30 minutes from now.
        /// All subsequent API calls will be blocked until the timeout expires.
        /// </remarks>
        private void RecordSteamApiRateLimit()
        {
            lock (_blockLock)
            {
                _steamApiBlockedUntil = DateTime.UtcNow.AddMinutes(30);
                AppLogger.LogDebug($"Steam API blocked until {_steamApiBlockedUntil.Value:HH:mm:ss} (30 minutes)");
            }
        }

        /// <summary>
        /// Performs an HTTP GET request with automatic HTTP 429 detection and blocking.
        /// </summary>
        /// <param name="url">URL to request.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>Response content as string.</returns>
        /// <exception cref="HttpRequestException">Thrown if HTTP 429 is received or other HTTP error occurs.</exception>
        /// <remarks>
        /// If HTTP 429 (Too Many Requests) is detected:
        /// - Records a 30-minute block via RecordSteamApiRateLimit()
        /// - Throws HttpRequestException with descriptive message
        ///
        /// All other HTTP errors are propagated via EnsureSuccessStatusCode().
        /// </remarks>
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

        /// <summary>
        /// Deserializes JSON response from IPlayerService/GetOwnedGames API.
        /// </summary>
        /// <param name="json">JSON string from Steam Web API.</param>
        /// <returns>Deserialized <see cref="OwnedGamesResponse"/> or null if deserialization fails.</returns>
        /// <remarks>
        /// Uses source-generated JSON context for AOT compatibility.
        /// </remarks>
        private static OwnedGamesResponse? DeserializeOwnedGamesResponse(string json)
        {
            return JsonSerializer.Deserialize(json, SteamApiJsonContext.Default.OwnedGamesResponse);
        }

        /// <summary>
        /// Deserializes JSON response from Steam Store API appdetails endpoint.
        /// </summary>
        /// <param name="json">JSON string from Steam Store API.</param>
        /// <returns>Dictionary mapping AppID strings to <see cref="AppDetailsResponse"/>, or null if deserialization fails.</returns>
        /// <remarks>
        /// The Steam Store API returns a dictionary with AppID as key.
        /// Uses source-generated JSON context for AOT compatibility.
        /// </remarks>
        private static Dictionary<string, AppDetailsResponse>? DeserializeAppDetailsResponse(string json)
        {
            return JsonSerializer.Deserialize(json, SteamApiJsonContext.Default.DictionaryStringAppDetailsResponse);
        }

        /// <summary>
        /// Releases all resources used by the <see cref="SteamApiService"/>.
        /// Optionally disposes the HttpClient if owned by this instance.
        /// </summary>
        /// <remarks>
        /// This method is idempotent and can be called multiple times safely.
        /// The HttpClient is only disposed if disposeHttpClient was true in the constructor.
        /// Rate limiters are not disposed (they may be shared across instances).
        /// </remarks>
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

    /// <summary>
    /// Represents a Steam game with English and localized names, icon URL, and playtime.
    /// Used as the primary data transfer object for game information.
    /// </summary>
    public class SteamGame
    {
        /// <summary>
        /// Gets or sets the Steam App ID (unique identifier for the game).
        /// </summary>
        public int AppId { get; set; }

        /// <summary>
        /// Gets or sets the English name of the game.
        /// </summary>
        public string NameEn { get; set; } = "";

        /// <summary>
        /// Gets or sets the localized name of the game in the target language.
        /// Falls back to English if localization is unavailable.
        /// </summary>
        public string NameLocalized { get; set; } = "";

        /// <summary>
        /// Gets or sets the URL to the game's header/cover image.
        /// </summary>
        public string IconUrl { get; set; } = "";

        /// <summary>
        /// Gets or sets the total playtime in minutes.
        /// </summary>
        public int PlaytimeForever { get; set; }
    }

    /// <summary>
    /// Root response object from Steam Web API IPlayerService/GetOwnedGames endpoint.
    /// </summary>
    public class OwnedGamesResponse
    {
        /// <summary>
        /// Gets or sets the response data containing game information.
        /// </summary>
        public OwnedGamesResponseData? response { get; set; }
    }

    /// <summary>
    /// Response data from IPlayerService/GetOwnedGames containing game count and game array.
    /// </summary>
    public class OwnedGamesResponseData
    {
        /// <summary>
        /// Gets or sets the total number of games owned by the user.
        /// </summary>
        public int game_count { get; set; }

        /// <summary>
        /// Gets or sets the array of owned games with basic information.
        /// </summary>
        public OwnedGame[]? games { get; set; }
    }

    /// <summary>
    /// Represents a single owned game from the Steam Web API response.
    /// Contains basic game information including App ID, name, and playtime.
    /// </summary>
    public class OwnedGame
    {
        /// <summary>
        /// Gets or sets the Steam App ID.
        /// </summary>
        public int appid { get; set; }

        /// <summary>
        /// Gets or sets the game name (typically English).
        /// </summary>
        public string name { get; set; } = "";

        /// <summary>
        /// Gets or sets the total playtime in minutes.
        /// </summary>
        public int playtime_forever { get; set; }

        /// <summary>
        /// Gets or sets the icon image hash (used to construct icon URL).
        /// </summary>
        public string img_icon_url { get; set; } = "";

        /// <summary>
        /// Gets or sets the logo image hash (used to construct logo URL).
        /// </summary>
        public string img_logo_url { get; set; } = "";
    }

    /// <summary>
    /// Root response object from Steam Web API ISteamApps/GetAppList endpoint.
    /// Contains the complete list of all Steam applications.
    /// </summary>
    public class AppListResponse
    {
        /// <summary>
        /// Gets or sets the app list data.
        /// </summary>
        public AppList? applist { get; set; }
    }

    /// <summary>
    /// Contains the array of all Steam applications from GetAppList endpoint.
    /// </summary>
    public class AppList
    {
        /// <summary>
        /// Gets or sets the array of app information objects.
        /// </summary>
        public AppInfo[]? apps { get; set; }
    }

    /// <summary>
    /// Basic information about a Steam application from the app list.
    /// Contains only App ID and name.
    /// </summary>
    public class AppInfo
    {
        /// <summary>
        /// Gets or sets the Steam App ID.
        /// </summary>
        public int appid { get; set; }

        /// <summary>
        /// Gets or sets the application name.
        /// </summary>
        public string name { get; set; } = "";
    }

    /// <summary>
    /// Response object from Steam Store API appdetails endpoint.
    /// Contains success flag and detailed game data.
    /// </summary>
    public class AppDetailsResponse
    {
        /// <summary>
        /// Gets or sets whether the request was successful.
        /// False if the App ID is invalid or data is unavailable.
        /// </summary>
        public bool success { get; set; }

        /// <summary>
        /// Gets or sets the detailed game data.
        /// Null if success is false.
        /// </summary>
        public AppData? data { get; set; }
    }

    /// <summary>
    /// Detailed game data from Steam Store API appdetails endpoint.
    /// Contains localized name and header image URL.
    /// </summary>
    public class AppData
    {
        /// <summary>
        /// Gets or sets the game name in the requested language.
        /// </summary>
        public string name { get; set; } = "";

        /// <summary>
        /// Gets or sets the URL to the game's header image.
        /// </summary>
        public string header_image { get; set; } = "";
    }
}
