using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
            // Configure HttpClient with timeout and retry settings like steam-friend-history
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MyOwnGames/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(10); // Shorter timeout like steam-friend-history
            
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
                // Based on steam-friend-history analysis: header_image is the primary source
                // Priority order inspired by steam-friend-history fetch_game_info()
                var urls = new[]
                {
                    // 1. Steam Store API header_image (highest priority - like steam-friend-history)
                    await GetHeaderImageFromStoreApiAsync(appId),
                    // 2. Fallback CDN URLs for header images
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                    $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg", 
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                    // 3. Alternative image formats as last resort
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/logo.png",
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_231x87.jpg"
                };

                foreach (var url in urls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    
                    if (await TryDownloadWithRetryAsync(url, filePath))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error downloading game image for {appId}: {ex.Message}");
            }
        }

        private async Task<string?> GetHeaderImageFromStoreApiAsync(int appId)
        {
            try
            {
                // Similar to steam-friend-history's fetch_game_info() function
                var storeApiUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                using var response = await _httpClient.GetAsync(storeApiUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    var jsonContent = await response.Content.ReadAsStringAsync();
                    
                    // Simple JSON parsing to extract header_image
                    var headerImageStart = jsonContent.IndexOf("\"header_image\":\"");
                    if (headerImageStart >= 0)
                    {
                        headerImageStart += 16; // Length of "header_image":"
                        var headerImageEnd = jsonContent.IndexOf("\"", headerImageStart);
                        if (headerImageEnd > headerImageStart)
                        {
                            var headerImageUrl = jsonContent.Substring(headerImageStart, headerImageEnd - headerImageStart);
                            if (!string.IsNullOrEmpty(headerImageUrl))
                            {
                                DebugLogger.LogDebug($"Found header_image from Store API: {headerImageUrl}");
                                return headerImageUrl;
                            }
                        }
                    }
                }
                
                // Add delay like steam-friend-history
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Failed to get header image from Store API for {appId}: {ex.Message}");
            }
            
            return null;
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

        private async Task<bool> TryDownloadWithRetryAsync(string url, string filePath)
        {
            const int maxRetries = 3;
            const int backoffTimeSeconds = 30;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    DebugLogger.LogDebug($"Downloading image from: {url} (attempt {attempt})");
                    using var response = await _httpClient.GetAsync(url);
                    
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        DebugLogger.LogDebug($"429 Too Many Requests - retrying in {backoffTimeSeconds} sec...");
                        await Task.Delay(TimeSpan.FromSeconds(backoffTimeSeconds));
                        continue;
                    }
                    
                    if (response.IsSuccessStatusCode)
                    {
                        using var fileStream = File.Create(filePath);
                        await response.Content.CopyToAsync(fileStream);
                        DebugLogger.LogDebug($"Successfully downloaded image to: {filePath}");
                        return true;
                    }
                    
                    DebugLogger.LogDebug($"Failed to download from {url}: {response.StatusCode}");
                    await Task.Delay(1000); // Short delay between attempts
                }
                catch (HttpRequestException ex)
                {
                    DebugLogger.LogDebug($"HTTP error downloading from {url} (attempt {attempt}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10)); // Longer delay for HTTP errors
                    }
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    DebugLogger.LogDebug($"Timeout downloading from {url} (attempt {attempt})");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5)); // Medium delay for timeouts
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Unexpected error downloading from {url} (attempt {attempt}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    }
                }
            }
            
            return false;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _imageCache.Clear();
        }
    }
}