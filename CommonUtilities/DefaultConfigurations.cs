namespace CommonUtilities
{
    /// <summary>
    /// Contains default configuration templates for all applications.
    /// </summary>
    public static class DefaultConfigurations
    {
        /// <summary>
        /// Default configuration for AnSAM application.
        /// </summary>
        public const string AnSAM = @"{
  // Game List Download Configuration
  // Controls how the global game list is downloaded and cached
  ""GameList"": {
    // URL source for downloading the Steam game list XML (default: ""https://gib.me/sam/games.xml"")
    ""SourceUrl"": ""https://gib.me/sam/games.xml"",

    // Maximum allowed size for the downloaded game list file in MB (default: 4)
    ""MaxSizeMB"": 4,

    // How long to cache the game list before re-downloading, in minutes (default: 30)
    ""CacheDurationMinutes"": 30
  },

  // Image Download and Caching Configuration
  // Controls how game cover images are downloaded and cached locally
  ""ImageCaching"": {
    // Maximum number of images to download simultaneously across all sources (default: 10)
    ""MaxConcurrentDownloads"": 10,

    // Maximum concurrent downloads per CDN domain to avoid overwhelming servers (default: 4)
    ""MaxConcurrentPerDomain"": 4,

    // How long to keep cached images before re-downloading, in days (CHANGED: never expire, was 30)
    ""CacheDurationDays"": 36500,

    // Days to wait before retrying failed English image downloads (default: 15)
    ""FailureTrackingDaysEnglish"": 15,

    // Days to wait before retrying failed non-English image downloads (CHANGED: 14, was 7)
    ""FailureTrackingDaysOtherLanguages"": 14,

    // Minutes to block a CDN after receiving rate limit errors (429/403) (default: 5)
    ""CdnBlockDurationMinutes"": 5
  },

  // Cross-Process File Locking Configuration
  // Controls timeouts when accessing shared files with MyOwnGames
  ""FileLocking"": {
    // Timeout in seconds for acquiring file lock when reading steam_games.xml (default: 5)
    ""ReadTimeoutSeconds"": 5,

    // Timeout in seconds for acquiring file lock when writing files (default: 30)
    ""WriteTimeoutSeconds"": 30
  },

  // HTTP Client Configuration
  // General settings for all HTTP requests
  ""HttpClient"": {
    // Timeout for HTTP requests in seconds before giving up (default: 30)
    ""TimeoutSeconds"": 30
  },

  // User Interface Configuration
  ""UI"": {
    // Update interval for CDN statistics display in seconds (default: 2)
    ""CdnStatsUpdateIntervalSeconds"": 2
  }
}
";

        /// <summary>
        /// Default configuration for RunGame application.
        /// </summary>
        public const string RunGame = @"{
  // Steam Integration Configuration
  // Controls how the application interacts with Steam client
  ""Steam"": {
    // Interval in milliseconds for polling Steam callbacks (default: 100)
    // Lower = more responsive but higher CPU usage
    ""CallbackPumpIntervalMs"": 100
  },

  // Mouse Movement Anti-Idle Configuration
  // Prevents Steam from marking you as idle/away
  ""MouseMovement"": {
    // Interval in seconds between automatic mouse movements when enabled (default: 30)
    ""MovementIntervalSeconds"": 30,

    // Distance in pixels to move the mouse cursor (default: 5)
    ""MoveDistancePixels"": 5,

    // Delay in milliseconds between each pixel step of movement (default: 15)
    // Lower = faster movement, higher = smoother movement
    ""StepDelayMs"": 15
  },

  // Achievement Timer Configuration
  // Controls scheduled/timed achievement unlocks
  ""AchievementTimer"": {
    // How often to check if scheduled achievements should unlock, in seconds (default: 1)
    ""CheckIntervalSeconds"": 1,

    // Delay threshold in seconds before storing stats to Steam (default: 12)
    // Prevents excessive Steam API calls when making multiple changes
    ""StoreStatsDelaySeconds"": 12
  },

  // User Interface Configuration
  ""UI"": {
    // Update interval for time display in seconds (default: 1)
    ""TimeUpdateIntervalSeconds"": 1,

    // Update interval for achievement timer display in seconds (default: 1)
    ""AchievementTimerUpdateIntervalSeconds"": 1,

    // Update interval for mouse movement check in seconds (default: 5)
    ""MouseTimerUpdateIntervalSeconds"": 5,

    // Delay in milliseconds before triggering search while typing (prevents lag) (default: 300)
    ""SearchDebounceDelayMs"": 300
  }
}
";

        /// <summary>
        /// Default configuration for MyOwnGames application.
        /// </summary>
        public const string MyOwnGames = @"{
  // Steam Web API Configuration
  // API key is required for accessing Steam Web API
  ""SteamWebApi"": {
    // Your Steam Web API key from https://steamcommunity.com/dev/apikey
    // Leave empty to be prompted on first run
    ""ApiKey"": """",

    // Base URL for Steam Web API (default: ""https://api.steampowered.com"")
    ""BaseUrl"": ""https://api.steampowered.com""
  },

  // Rate Limiting Configuration
  // Controls API request throttling to avoid rate limits
  ""RateLimiting"": {
    // Maximum number of concurrent API requests (default: 3)
    ""MaxConcurrentRequests"": 3,

    // Time window in seconds for rate limiting (default: 10)
    ""TimeWindowSeconds"": 10,

    // Maximum requests allowed per time window (default: 5)
    ""MaxRequestsPerWindow"": 5
  },

  // Image Download and Caching Configuration
  ""ImageCaching"": {
    // Maximum number of images to download simultaneously (default: 10)
    ""MaxConcurrentDownloads"": 10,

    // Maximum concurrent downloads per CDN domain (default: 4)
    ""MaxConcurrentPerDomain"": 4,

    // How long to keep cached images in days (CHANGED: never expire, was 30)
    ""CacheDurationDays"": 36500,

    // Days to wait before retrying failed English image downloads (default: 15)
    ""FailureTrackingDaysEnglish"": 15,

    // Days to wait before retrying failed non-English image downloads (CHANGED: 14, was 7)
    ""FailureTrackingDaysOtherLanguages"": 14,

    // Minutes to block a CDN after rate limit errors (default: 5)
    ""CdnBlockDurationMinutes"": 5
  },

  // Cross-Process File Locking Configuration
  ""FileLocking"": {
    // Timeout in seconds for reading shared files (default: 5)
    ""ReadTimeoutSeconds"": 5,

    // Timeout in seconds for writing shared files (default: 30)
    ""WriteTimeoutSeconds"": 30
  },

  // HTTP Client Configuration
  ""HttpClient"": {
    // Timeout for HTTP requests in seconds (default: 30)
    ""TimeoutSeconds"": 30
  },

  // User Interface Configuration
  ""UI"": {
    // Delay in milliseconds before triggering search while typing (default: 300)
    ""SearchDebounceDelayMs"": 300
  }
}
";
    }
}
