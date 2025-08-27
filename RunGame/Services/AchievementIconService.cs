using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommonUtilities;

namespace RunGame.Services
{
    public class AchievementIconService : IDisposable
    {
        private readonly GameImageCache _cache;
        private readonly Dictionary<string, string> _memoryCache = new();
        private readonly long _gameId;
        private bool _disposed;

        public AchievementIconService(long gameId)
        {
            _gameId = gameId;
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "Achievement_IconCache", gameId.ToString());
            _cache = new GameImageCache(baseDir);
        }

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

        public void ClearCache()
        {
            _memoryCache.Clear();
            _cache.ClearCache();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _memoryCache.Clear();
                _disposed = true;
            }
        }
    }
}
