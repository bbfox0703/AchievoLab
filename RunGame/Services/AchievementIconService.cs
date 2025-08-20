using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
// Service no longer constructs BitmapImages directly

namespace RunGame.Services
{
    public class AchievementIconService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _cacheDirectory;
        private readonly Dictionary<string, string> _memoryCache = new();
        private readonly long _gameId;
        private bool _disposed = false;

        public AchievementIconService(long gameId)
        {
            _gameId = gameId;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AnSAM_RunGame/1.0");
            
            // Create cache directory in user's AppData
            _cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AnSAM_RunGame", "IconCache", gameId.ToString());
            
            Directory.CreateDirectory(_cacheDirectory);
            DebugLogger.LogDebug($"AchievementIconService initialized with cache directory: {_cacheDirectory}");
        }

        public async Task<string?> GetAchievementIconAsync(string achievementId, string iconFileName, bool isAchieved)
        {
            if (string.IsNullOrEmpty(iconFileName))
            {
                DebugLogger.LogDebug($"Icon filename is empty for achievement {achievementId}");
                return null;
            }

            try
            {
                string cacheKey = $"{achievementId}_{(isAchieved ? "achieved" : "locked")}";
                DebugLogger.LogDebug($"Processing icon for {achievementId}, filename: {iconFileName}, isAchieved: {isAchieved}");
                
                // Check memory cache first
                if (_memoryCache.TryGetValue(cacheKey, out var cachedPath))
                {
                    DebugLogger.LogDebug($"Found cached icon for {achievementId}");
                    return cachedPath;
                }

                // Check disk cache - keep original extension (usually .jpg)
                string fileExtension = Path.GetExtension(iconFileName);
                if (string.IsNullOrEmpty(fileExtension))
                    fileExtension = ".jpg"; // Default to jpg if no extension
                string fileName = $"{cacheKey}{fileExtension}";
                string filePath = Path.Combine(_cacheDirectory, fileName);
                
                if (!File.Exists(filePath))
                {
                    // Download the icon
                    DebugLogger.LogDebug($"Downloading icon for {achievementId}: {iconFileName}");
                    await DownloadIconAsync(iconFileName, filePath);
                }
                else
                {
                    DebugLogger.LogDebug($"Found cached file for {achievementId}: {filePath}");
                }

                if (File.Exists(filePath))
                {
                    return await LoadImageFromBytesAsync(filePath, cacheKey, achievementId);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading icon for {achievementId}: {ex.Message}");
            }

            return null;
        }

        private async Task DownloadIconAsync(string iconFileName, string filePath)
        {
            try
            {
                // Use the same URL pattern as Legacy SAM.Game:
                // https://cdn.steamstatic.com/steamcommunity/public/images/apps/{gameId}/{fileName}
                string steamIconUrl = $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{_gameId}/{Uri.EscapeDataString(iconFileName)}";
                
                DebugLogger.LogDebug($"Downloading from: {steamIconUrl}");
                
                var response = await _httpClient.GetAsync(steamIconUrl);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(filePath, content);
                    DebugLogger.LogDebug($"Successfully downloaded icon: {steamIconUrl} -> {filePath}");
                }
                else
                {
                    DebugLogger.LogDebug($"Failed to download icon: {steamIconUrl} (Status: {response.StatusCode})");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error downloading icon from {iconFileName}: {ex.Message}");
            }
        }


        private Task<string?> LoadImageFromBytesAsync(string filePath, string cacheKey, string achievementId)
        {
            try
            {
                DebugLogger.LogDebug($"Attempting to load icon for {achievementId} from {filePath}");

                if (!File.Exists(filePath))
                {
                    DebugLogger.LogDebug($"File does not exist: {filePath}");
                    return Task.FromResult<string?>(null);
                }

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length == 0)
                {
                    DebugLogger.LogDebug($"File is empty: {filePath}");
                    return Task.FromResult<string?>(null);
                }

                _memoryCache[cacheKey] = filePath;
                DebugLogger.LogDebug($"Icon path cached for {achievementId}: {filePath}");
                return Task.FromResult<string?>(filePath);
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Failed to load icon path for {achievementId}: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }

        public void ClearCache()
        {
            try
            {
                _memoryCache.Clear();
                
                if (Directory.Exists(_cacheDirectory))
                {
                    Directory.Delete(_cacheDirectory, true);
                    Directory.CreateDirectory(_cacheDirectory);
                }
                
                DebugLogger.LogDebug("Achievement icon cache cleared");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error clearing icon cache: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _memoryCache.Clear();
                _disposed = true;
                DebugLogger.LogDebug("AchievementIconService disposed");
            }
        }
    }
}