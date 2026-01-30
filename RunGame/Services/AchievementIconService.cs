using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommonUtilities;

namespace RunGame.Services
{
    /// <summary>
    /// Manages caching and retrieval of achievement icons from Steam CDN.
    /// Provides two-tier caching: in-memory dictionary and disk-based GameImageCache.
    /// </summary>
    public class AchievementIconService : IDisposable
    {
        private readonly GameImageCache _cache;
        private readonly Dictionary<string, string> _memoryCache = new();
        private readonly long _gameId;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="AchievementIconService"/> class.
        /// Creates a game-specific cache directory under LocalApplicationData/AchievoLab/Achievement_IconCache.
        /// </summary>
        /// <param name="gameId">The Steam AppID for which to cache achievement icons.</param>
        public AchievementIconService(long gameId)
        {
            _gameId = gameId;
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "Achievement_IconCache", gameId.ToString());
            _cache = new GameImageCache(baseDir);
        }

        /// <summary>
        /// Retrieves the file path to an achievement icon, downloading from Steam CDN if necessary.
        /// Icons are cached both in memory and on disk for performance.
        /// </summary>
        /// <param name="achievementId">The unique achievement identifier.</param>
        /// <param name="iconFileName">The filename of the icon (e.g., "achievement_icon.jpg").</param>
        /// <param name="isAchieved">True for the colored (achieved) icon, false for the grayscale (locked) icon.</param>
        /// <returns>The local file path to the cached icon, or null if the icon could not be retrieved.</returns>
        public async Task<string?> GetAchievementIconAsync(string achievementId, string iconFileName, bool isAchieved)
        {
            if (string.IsNullOrEmpty(iconFileName))
            {
                return null;
            }

            string cacheKey = $"{achievementId}_{(isAchieved ? "achieved" : "locked")}";
            if (_memoryCache.TryGetValue(cacheKey, out var cachedPath))
            {
                return cachedPath;
            }

            string url = $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{_gameId}/{Uri.EscapeDataString(iconFileName)}";
            var result = await _cache.GetImagePathAsync(cacheKey, new Uri(url));
            if (!string.IsNullOrEmpty(result.Path))
            {
                _memoryCache[cacheKey] = result.Path;
                return result.Path;
            }

            return null;
        }

        /// <summary>
        /// Clears both in-memory and disk caches for achievement icons.
        /// Useful when switching games or forcing a refresh.
        /// </summary>
        public void ClearCache()
        {
            _memoryCache.Clear();
            _cache.ClearCache();
        }

        /// <summary>
        /// Releases resources used by the service, clearing the in-memory cache.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _memoryCache.Clear();
                _cache.Dispose();
                _disposed = true;
            }
        }
    }
}
