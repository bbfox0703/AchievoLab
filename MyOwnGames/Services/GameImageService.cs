using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace MyOwnGames.Services
{
    public class GameImageService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _cacheDirectory;
        private readonly Dictionary<int, string> _imageCache = new();
        private readonly DispatcherQueue _dispatcherQueue;

        public GameImageService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MyOwnGames/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            
            // Create cache directory
            _cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "ImageCache");
            Directory.CreateDirectory(_cacheDirectory);

            // Get current UI dispatcher for thread-safe operations
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        public async Task<string?> GetGameImageAsync(int appId)
        {
            // Check memory cache first
            if (_imageCache.TryGetValue(appId, out var cachedPath))
            {
                return cachedPath;
            }

            // Check disk cache
            string fileName = $"{appId}_header.jpg";
            string filePath = Path.Combine(_cacheDirectory, fileName);
            
            if (!File.Exists(filePath))
            {
                // Download image
                await DownloadGameImageAsync(appId, filePath);
            }

            if (File.Exists(filePath))
            {
                // Cache the path
                _imageCache[appId] = filePath;
                return filePath;
            }

            return null;
        }

        private async Task DownloadGameImageAsync(int appId, string filePath)
        {
            try
            {
                // Use the same URL pattern as AnSAM GameImageUrlResolver
                // Priority order: small_capsule -> logo -> library_600x900 -> header_image
                var urls = new[]
                {
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                    $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg",
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
                };

                foreach (var url in urls)
                {
                    try
                    {
                        var response = await _httpClient.GetAsync(url);
                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsByteArrayAsync();
                            if (content.Length > 0)
                            {
                                // Ensure thread-safe file write
                                await Task.Run(() => File.WriteAllBytes(filePath, content));
                                DebugLogger.LogDebug($"Downloaded game image: {url} -> {filePath}");
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogDebug($"Failed to download from {url}: {ex.Message}");
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error downloading game image for {appId}: {ex.Message}");
            }
        }

        public void ClearCache()
        {
            try
            {
                _imageCache.Clear();
                if (Directory.Exists(_cacheDirectory))
                {
                    Directory.Delete(_cacheDirectory, true);
                    Directory.CreateDirectory(_cacheDirectory);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error clearing image cache: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _imageCache.Clear();
        }
    }
}