using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AnSAM.RunGame.Services
{
    public class AchievementIconService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _cacheDirectory;
        private readonly Dictionary<string, BitmapImage> _memoryCache = new();
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

        public async Task<BitmapImage?> GetAchievementIconAsync(string achievementId, string iconUrl, bool isUnlocked)
        {
            if (string.IsNullOrEmpty(iconUrl))
                return null;

            try
            {
                string cacheKey = $"{achievementId}_{(isUnlocked ? "unlocked" : "locked")}";
                
                // Check memory cache first
                if (_memoryCache.TryGetValue(cacheKey, out var cachedImage))
                {
                    return cachedImage;
                }

                // Check disk cache
                string fileName = $"{cacheKey}.jpg";
                string filePath = Path.Combine(_cacheDirectory, fileName);
                
                if (!File.Exists(filePath))
                {
                    // Download the icon
                    DebugLogger.LogDebug($"Downloading icon for {achievementId}: {iconUrl}");
                    await DownloadIconAsync(iconUrl, filePath);
                }

                if (File.Exists(filePath))
                {
                    // Load from file
                    var bitmap = new BitmapImage();
                    using (var stream = File.OpenRead(filePath))
                    {
                        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                    }
                    
                    // Cache in memory
                    _memoryCache[cacheKey] = bitmap;
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading icon for {achievementId}: {ex.Message}");
            }

            return null;
        }

        private async Task DownloadIconAsync(string iconUrl, string filePath)
        {
            try
            {
                // Steam achievement icons are typically in the format:
                // https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{appid}/{hash}.jpg
                string steamIconUrl = ConvertToSteamIconUrl(iconUrl);
                
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
                DebugLogger.LogDebug($"Error downloading icon from {iconUrl}: {ex.Message}");
            }
        }

        private string ConvertToSteamIconUrl(string iconHash)
        {
            // Steam achievement icons follow this pattern:
            // https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{appid}/{hash}.jpg
            if (string.IsNullOrEmpty(iconHash))
                return string.Empty;

            // If it's already a full URL, return as-is
            if (iconHash.StartsWith("http"))
                return iconHash;

            // Otherwise, construct the Steam CDN URL
            return $"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{_gameId}/{iconHash}.jpg";
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