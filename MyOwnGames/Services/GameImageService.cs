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
                // Phase 1: Steam-friend-history style rules (Store API based)
                DebugLogger.LogDebug($"Phase 1: Trying steam-friend-history style download for {appId}");
                var steamFriendHistoryUrls = new[]
                {
                    // 1. Steam Store API header_image (highest priority - like steam-friend-history)
                    await GetHeaderImageFromStoreApiAsync(appId),
                    // 2. Fallback CDN URLs for header images
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                    $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg", 
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
                };

                foreach (var url in steamFriendHistoryUrls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    
                    if (await TryDownloadWithRetryAsync(url, filePath))
                    {
                        DebugLogger.LogDebug($"Phase 1 success: Downloaded {appId} from {url}");
                        return;
                    }
                }

                // Phase 2: AnSAM style rules (SteamClient API based) - fallback
                DebugLogger.LogDebug($"Phase 1 failed, Phase 2: Trying AnSAM style download for {appId}");
                var ansamUrls = await GetAnsamStyleUrlsAsync(appId);
                
                foreach (var url in ansamUrls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    
                    if (await TryDownloadWithRetryAsync(url, filePath))
                    {
                        DebugLogger.LogDebug($"Phase 2 success: Downloaded {appId} from {url}");
                        return;
                    }
                }

                // Phase 3: Last resort - additional fallback URLs
                DebugLogger.LogDebug($"Phase 2 failed, Phase 3: Trying additional fallback URLs for {appId}");
                var lastResortUrls = new[]
                {
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/logo.png",
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_231x87.jpg",
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
                };

                foreach (var url in lastResortUrls)
                {
                    if (await TryDownloadWithRetryAsync(url, filePath))
                    {
                        DebugLogger.LogDebug($"Phase 3 success: Downloaded {appId} from {url}");
                        return;
                    }
                }

                DebugLogger.LogDebug($"All phases failed for {appId}");
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
                                // Fix escaped slashes from JSON
                                headerImageUrl = headerImageUrl.Replace("\\/", "/");
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

        private async Task<string[]> GetAnsamStyleUrlsAsync(int appId)
        {
            var urls = new List<string>();
            
            try
            {
                // Try to get app details from Steam Store API to extract image metadata
                var storeApiUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                using var response = await _httpClient.GetAsync(storeApiUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    var jsonContent = await response.Content.ReadAsStringAsync();
                    
                    // Extract various image types similar to AnSAM's GameImageUrlResolver
                    // Look for small_capsule, logo, library images, etc.
                    
                    // Small capsule (highest priority in AnSAM)
                    var capsuleMatch = ExtractJsonValue(jsonContent, "capsule_image");
                    if (!string.IsNullOrEmpty(capsuleMatch))
                    {
                        urls.Add(capsuleMatch.Replace("\\/", "/"));
                    }
                    
                    // Header image
                    var headerMatch = ExtractJsonValue(jsonContent, "header_image");
                    if (!string.IsNullOrEmpty(headerMatch))
                    {
                        urls.Add(headerMatch.Replace("\\/", "/"));
                    }
                    
                    // Logo
                    var logoMatch = ExtractJsonValue(jsonContent, "logo");
                    if (!string.IsNullOrEmpty(logoMatch))
                    {
                        urls.Add(logoMatch.Replace("\\/", "/"));
                    }
                    
                    // Screenshots (first one)
                    var screenshotMatch = ExtractFirstScreenshot(jsonContent);
                    if (!string.IsNullOrEmpty(screenshotMatch))
                    {
                        urls.Add(screenshotMatch.Replace("\\/", "/"));
                    }
                }
                
                // Add AnSAM-style constructed URLs as fallback
                urls.AddRange(new[]
                {
                    // Small capsule patterns (AnSAM style)
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_sm_120.jpg",
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_184x69.jpg",
                    // Logo patterns
                    $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/logo.jpg",
                    // Library patterns  
                    $"https://shared.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                    $"https://shared.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero.jpg"
                });
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error getting AnSAM style URLs for {appId}: {ex.Message}");
                
                // Fallback to constructed URLs only
                urls.AddRange(new[]
                {
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_sm_120.jpg",
                    $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/logo.jpg",
                    $"https://shared.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg"
                });
            }
            
            return urls.ToArray();
        }

        private string? ExtractJsonValue(string json, string key)
        {
            var searchKey = $"\"{key}\":\"";
            var startIndex = json.IndexOf(searchKey);
            if (startIndex >= 0)
            {
                startIndex += searchKey.Length;
                var endIndex = json.IndexOf("\"", startIndex);
                if (endIndex > startIndex)
                {
                    return json.Substring(startIndex, endIndex - startIndex);
                }
            }
            return null;
        }

        private string? ExtractFirstScreenshot(string json)
        {
            // Look for first screenshot in screenshots array
            var screenshotsStart = json.IndexOf("\"screenshots\":");
            if (screenshotsStart >= 0)
            {
                var pathFullStart = json.IndexOf("\"path_full\":\"", screenshotsStart);
                if (pathFullStart >= 0)
                {
                    pathFullStart += 13; // Length of "path_full":"
                    var pathFullEnd = json.IndexOf("\"", pathFullStart);
                    if (pathFullEnd > pathFullStart)
                    {
                        return json.Substring(pathFullStart, pathFullEnd - pathFullStart);
                    }
                }
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