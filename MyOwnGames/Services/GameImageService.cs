using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace MyOwnGames.Services
{
    public class GameImageService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseCacheDirectory;
        private readonly Dictionary<string, string> _imageCache = new(); // Changed key to string for language support
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly ImageFailureTrackingService _imageFailureService;
        private string _currentLanguage = "english";
        
        // Event to notify when an image download completes
        public event Action<int, string?>? ImageDownloadCompleted;
        
        public void SetLanguage(string language)
        {
            if (_currentLanguage != language)
            {
                _currentLanguage = language;
                _imageCache.Clear(); // Clear memory cache when language changes
            }
        }
        
        private string GetLanguageCacheDirectory(string language)
        {
            var langDir = Path.Combine(_baseCacheDirectory, language);
            Directory.CreateDirectory(langDir);
            return langDir;
        }

        public GameImageService()
        {
            // Configure HttpClient with timeout and retry settings
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MyOwnGames/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(8); // Reduced timeout for faster failure detection
            
            // Create base cache directory
            _baseCacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "ImageCache");
            Directory.CreateDirectory(_baseCacheDirectory);

            // Get current UI dispatcher for thread-safe operations
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _imageFailureService = new ImageFailureTrackingService();
        }

        public async Task<string?> GetGameImageAsync(int appId, string? language = null)
        {
            language ??= _currentLanguage;
            var cacheKey = $"{appId}_{language}";
            
            // Check memory cache first
            if (_imageCache.TryGetValue(cacheKey, out var cachedPath))
            {
                return cachedPath;
            }

            // Check disk cache - language-specific directory
            var langCacheDir = GetLanguageCacheDirectory(language);
            string fileName = $"{appId}_header.jpg";
            string filePath = Path.Combine(langCacheDir, fileName);
            
            if (!File.Exists(filePath))
            {
                // Check if we should skip download due to recent failures
                if (_imageFailureService.ShouldSkipDownload(appId, language))
                {
                    return null;
                }

                // Download image with language-specific URLs
                var downloadSuccess = await DownloadGameImageAsync(appId, filePath, language);
                
                if (!downloadSuccess || !File.Exists(filePath))
                {
                    // Record failed download
                    _imageFailureService.RecordFailedDownload(appId, language);
                    // Notify that download failed
                    ImageDownloadCompleted?.Invoke(appId, null);
                    return null;
                }
                else
                {
                    // Remove any previous failed record since download succeeded
                    _imageFailureService.RemoveFailedRecord(appId, language);
                    // Notify that download completed successfully
                    ImageDownloadCompleted?.Invoke(appId, filePath);
                }
            }

            if (File.Exists(filePath))
            {
                // Cache the path
                _imageCache[cacheKey] = filePath;
                return filePath;
            }

            return null;
        }

        private async Task<bool> DownloadGameImageAsync(int appId, string filePath, string language = "english")
        {
            try
            {
                // Phase 1: Language-aware Store API based download
                DebugLogger.LogDebug($"Phase 1: Trying language-aware download for {appId} (language: {language})");
                var storeApiUrls = new[]
                {
                    // 1. Steam Store API header_image with language parameter
                    await GetHeaderImageFromStoreApiAsync(appId, language),
                    // 2. Fallback CDN URLs for header images - still universal
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                    $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg"
                };

                foreach (var url in storeApiUrls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    
                    if (await TryDownloadWithRetryAsync(url, filePath))
                    {
                        DebugLogger.LogDebug($"Phase 1 success: Downloaded {appId} from {url}");
                        return true;
                    }
                }

                // Phase 2: Reduced retry fallback URLs
                DebugLogger.LogDebug($"Phase 1 failed, Phase 2: Trying essential fallback URLs for {appId}");
                var fallbackUrls = new[]
                {
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_sm_120.jpg",
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/logo.png",
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
                };
                
                foreach (var url in fallbackUrls)
                {
                    if (await TryDownloadWithRetryAsync(url, filePath))
                    {
                        DebugLogger.LogDebug($"Phase 2 success: Downloaded {appId} from {url}");
                        return true;
                    }
                }

                DebugLogger.LogDebug($"All phases failed for {appId}");
                return false;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error downloading game image for {appId}: {ex.Message}");
                return false;
            }
        }

        private async Task<string?> GetHeaderImageFromStoreApiAsync(int appId, string language = "english")
        {
            try
            {
                // Language-aware Store API call
                var storeApiUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={language}";
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
                
                // Reduced delay for faster processing
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Failed to get header image from Store API for {appId}: {ex.Message}");
            }
            
            return null;
        }


        public void ClearCache(string? specificLanguage = null)
        {
            try
            {
                if (specificLanguage != null)
                {
                    // Clear cache for specific language
                    var langCacheDir = GetLanguageCacheDirectory(specificLanguage);
                    if (Directory.Exists(langCacheDir))
                    {
                        Directory.Delete(langCacheDir, true);
                        Directory.CreateDirectory(langCacheDir);
                    }
                    // Clear memory cache entries for this language
                    var keysToRemove = _imageCache.Keys.Where(k => k.EndsWith($"_{specificLanguage}")).ToList();
                    foreach (var key in keysToRemove)
                    {
                        _imageCache.Remove(key);
                    }
                }
                else
                {
                    // Clear all caches
                    _imageCache.Clear();
                    if (Directory.Exists(_baseCacheDirectory))
                    {
                        Directory.Delete(_baseCacheDirectory, true);
                        Directory.CreateDirectory(_baseCacheDirectory);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error clearing image cache: {ex.Message}");
            }
        }

        private async Task<bool> TryDownloadWithRetryAsync(string url, string filePath)
        {
            const int maxRetries = 2; // Reduced retries
            const int backoffTimeSeconds = 15; // Reduced backoff time
            
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
                        await Task.Delay(TimeSpan.FromSeconds(5)); // Reduced delay for HTTP errors
                    }
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    DebugLogger.LogDebug($"Timeout downloading from {url} (attempt {attempt})");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3)); // Reduced delay for timeouts
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Unexpected error downloading from {url} (attempt {attempt}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5)); // Reduced delay for other errors
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