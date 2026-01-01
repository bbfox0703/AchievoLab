using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace CommonUtilities
{
    public class SharedImageService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly GameImageCache _cache;
        private readonly bool _disposeHttpClient;
        private readonly Dictionary<string, string> _imageCache = new();
        private readonly ConcurrentDictionary<string, Task<string?>> _pendingRequests = new();
        private readonly HashSet<string> _completedEvents = new();
        private readonly object _eventLock = new();
        private CancellationTokenSource _cts = new();
        private string _currentLanguage = "english";

        // Concurrency limiter to prevent resource exhaustion
        private const int MAX_CONCURRENT_DOWNLOADS = 10;
        private readonly SemaphoreSlim _downloadSemaphore = new(MAX_CONCURRENT_DOWNLOADS, MAX_CONCURRENT_DOWNLOADS);

        // CDN load balancer for intelligent CDN selection
        private readonly CdnLoadBalancer _cdnLoadBalancer;

        private static readonly JsonSerializerOptions JsonOptions =
            new() { TypeInfoResolver = StoreApiJsonContext.Default };

        public event Action<int, string?>? ImageDownloadCompleted;

        public SharedImageService(HttpClient httpClient, GameImageCache? cache = null, bool disposeHttpClient = false)
        {
            _httpClient = httpClient;
            _disposeHttpClient = disposeHttpClient;
            _cdnLoadBalancer = new CdnLoadBalancer(maxConcurrentPerDomain: 4);

            if (cache != null)
            {
                _cache = cache;
            }
            else
            {
                var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AchievoLab", "ImageCache");
                _cache = new GameImageCache(baseDir, new ImageFailureTrackingService(), maxConcurrentRequestsPerDomain: 4);
            }
        }

        public Task SetLanguage(string language)
        {
            if (_currentLanguage != language)
            {
                DebugLogger.LogDebug($"Switching language from {_currentLanguage} to {language}. Pending requests: {_pendingRequests.Count}");
                
                // Cancel ongoing operations
                _cts.Cancel();

                // Capture pending requests before clearing
                var pending = _pendingRequests.Values.ToArray();
                // Don't block waiting for all requests to finish
                _ = Task.WhenAll(pending); // 背景等待

                // Now clear caches and reset state
                _imageCache.Clear();
                _pendingRequests.Clear();
                lock (_eventLock)
                {
                    _completedEvents.Clear();
                }

                _cts.Dispose();
                _cts = new CancellationTokenSource();
                _currentLanguage = language;
                
                DebugLogger.LogDebug($"Language switch completed. Reset state for {language}");
            }
            return Task.CompletedTask;
        }

        public string GetCurrentLanguage() => _currentLanguage;
        
        // Resource monitoring methods
        public int GetPendingRequestsCount() => _pendingRequests.Count;
        public int GetAvailableDownloadSlots() => _downloadSemaphore.CurrentCount;
        
        // Cleanup method for stale pending requests
        public void CleanupStaleRequests()
        {
            var staleKeys = new List<string>();
            foreach (var kvp in _pendingRequests)
            {
                if (kvp.Value.IsCompleted || kvp.Value.IsCanceled || kvp.Value.IsFaulted)
                {
                    staleKeys.Add(kvp.Key);
                }
            }
            
            foreach (var key in staleKeys)
            {
                _pendingRequests.TryRemove(key, out _);
            }
            
            if (staleKeys.Count > 0)
            {
                DebugLogger.LogDebug($"Cleaned up {staleKeys.Count} stale pending requests. Remaining: {_pendingRequests.Count}");
            }
        }

        public bool HasImage(int appId, string language)
        {
            return _cache.TryGetCachedPath(appId.ToString(), language, checkEnglishFallback: false) != null;
        }

        public bool IsImageCached(int appId, string language) => HasImage(appId, language);

        public async Task<string?> GetGameImageAsync(int appId, string? language = null)
        {
            language ??= _currentLanguage;
            var originalLanguage = language;
            var cacheKey = $"{appId}_{language}";

            // Check if there's already a request in progress for this image
            if (_pendingRequests.TryGetValue(cacheKey, out var existingTask))
            {
                try
                {
                    return await existingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    DebugLogger.LogDebug($"Image request for {appId} cancelled");
                    return GetFallbackImagePath();
                }
            }

            // Start a new request
            var requestTask = GetGameImageInternalAsync(appId, language, originalLanguage, cacheKey);
            _pendingRequests.TryAdd(cacheKey, requestTask);

            try
            {
                var result = await requestTask.ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                DebugLogger.LogDebug($"Image request for {appId} cancelled");
                return GetFallbackImagePath();
            }
            finally
            {
                // Always remove from pending requests when done
                _pendingRequests.TryRemove(cacheKey, out _);
            }
        }

        private async Task<string?> GetGameImageInternalAsync(int appId, string language, string originalLanguage, string cacheKey)
        {
            // Check in-memory cache first
            if (_imageCache.TryGetValue(cacheKey, out var cached))
            {
                if (IsFreshImage(cached))
                {
                    return cached;
                }

                try { File.Delete(cached); } catch { }
                _imageCache.Remove(cacheKey);
                // Don't record as failed download - file was corrupted, not missing
            }

            // Step 1: Check failure tracking - if failed within 7 days for this language, skip to English fallback
            if (_cache.ShouldSkipDownload(appId, language))
            {
                DebugLogger.LogDebug($"Skipping {language} download for {appId} due to recent failure, checking English fallback");
                return await TryEnglishFallbackAsync(appId, language, cacheKey);
            }

            // Step 2: Check language-specific cache
            var diskCachedPath = _cache.TryGetCachedPath(appId.ToString(), language, checkEnglishFallback: false);
            if (!string.IsNullOrEmpty(diskCachedPath))
            {
                if (IsFreshImage(diskCachedPath))
                {
                    _imageCache[cacheKey] = diskCachedPath;
                    TriggerImageDownloadCompletedEvent(appId, diskCachedPath);
                    return diskCachedPath;
                }

                try { File.Delete(diskCachedPath); } catch { }
                // Don't record as failed download - file was corrupted or expired
            }

            // Step 3: Try to download language-specific image
            var downloadResult = await TryDownloadLanguageSpecificImageAsync(appId, language, cacheKey);
            if (downloadResult != null)
            {
                // Step 4: Download successful - remove failure record if exists and return
                _cache.RemoveFailedRecord(appId, language);
                return downloadResult;
            }

            // Step 5: Language-specific download failed - record failure only if not cancelled
            if (!_cts.IsCancellationRequested)
            {
                _cache.RecordFailedDownload(appId, language);
                DebugLogger.LogDebug($"Failed to download {language} image for {appId}");
            }
            else
            {
                DebugLogger.LogDebug($"Download for {appId} ({language}) was cancelled, not recording failure");
            }

            // Step 6-8: English fallback logic (only for non-English languages)
            if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                return await TryEnglishFallbackAsync(appId, language, cacheKey);
            }

            // If we reach here, English download failed - return fallback image
            return GetFallbackImagePath();
        }

        private async Task<string?> TryDownloadLanguageSpecificImageAsync(int appId, string language, string cacheKey)
        {

            // Wait for available download slot
            await _downloadSemaphore.WaitAsync(_cts.Token);
            var pending = _pendingRequests.Count;
            var available = _downloadSemaphore.CurrentCount;
            DebugLogger.LogDebug($"Starting download for {appId} ({language}) - Pending: {pending}, Available slots: {available}");
            try
            {
                var languageSpecificUrlMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            static void AddUrl(Dictionary<string, List<string>> map, string url)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var domain = uri.Host;
                    if (!map.TryGetValue(domain, out var list))
                    {
                        list = new List<string>();
                        map[domain] = list;
                    }
                    list.Add(url);
                }
            }

            static List<string> RoundRobin(Dictionary<string, List<string>> map)
            {
                var domainQueues = map.ToDictionary(kv => kv.Key, kv => new Queue<string>(kv.Value));
                var keys = domainQueues.Keys.ToList();
                var result = new List<string>();
                bool added;
                do
                {
                    added = false;
                    foreach (var key in keys)
                    {
                        var queue = domainQueues[key];
                        if (queue.Count > 0)
                        {
                            result.Add(queue.Dequeue());
                            added = true;
                        }
                    }
                } while (added);
                return result;
            }

            var header = await GetHeaderImageFromStoreApiAsync(appId, language, _cts.Token);
            if (!string.IsNullOrEmpty(header))
            {
                AddUrl(languageSpecificUrlMap, header);
            }

            // Fastly CDN (will be blocked if access too many times)
            //if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            //{
            //    AddUrl(languageSpecificUrlMap, $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg");
            //}

            if (string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                // Cloudflare CDN
                AddUrl(languageSpecificUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");

                // Steam CDN
                AddUrl(languageSpecificUrlMap, $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg");

                // Akamai CDN
                AddUrl(languageSpecificUrlMap, $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");
            }
            else
            {
                // Cloudflare CDN
                AddUrl(languageSpecificUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg");

                // Steam CDN
                AddUrl(languageSpecificUrlMap, $"https://cdn.steamstatic.com/steam/apps/{appId}/header_{language}.jpg");

                // Akamai CDN
                AddUrl(languageSpecificUrlMap, $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg");
            }

                var languageUrls = RoundRobin(languageSpecificUrlMap);

                // Use CDN load balancer with failover strategy
                var result = await TryDownloadWithCdnFailover(appId.ToString(), languageUrls, language, appId, _cts.Token);

                if (!string.IsNullOrEmpty(result?.Path) && IsFreshImage(result.Value.Path))
                {
                    _imageCache[cacheKey] = result.Value.Path;
                    if (result.Value.Downloaded)
                    {
                        TriggerImageDownloadCompletedEvent(appId, result.Value.Path);
                    }
                    return result.Value.Path;
                }

                // Clean up any invalid result
                if (!string.IsNullOrEmpty(result?.Path))
                {
                    try { File.Delete(result.Value.Path); } catch { }
                }

                return null;
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }

        private async Task<string?> TryEnglishFallbackAsync(int appId, string targetLanguage, string cacheKey)
        {
            // Step 6: Check English cache first
            var englishCachedPath = _cache.TryGetCachedPath(appId.ToString(), "english", checkEnglishFallback: false);
            if (!string.IsNullOrEmpty(englishCachedPath) && IsFreshImage(englishCachedPath))
            {
                DebugLogger.LogDebug($"Found English cached image for {appId}, displaying it");
                _imageCache[cacheKey] = englishCachedPath;
                TriggerImageDownloadCompletedEvent(appId, englishCachedPath);
                return englishCachedPath;
            }

            // Step 7: Download English image
            var englishDownloadResult = await TryDownloadEnglishImageAsync(appId, cacheKey);
            if (englishDownloadResult != null)
            {
                DebugLogger.LogDebug($"Downloaded English image for {appId}");
                return englishDownloadResult;
            }

            // Step 8: English download failed - show fallback
            return GetFallbackImagePath();
        }

        private async Task<string?> TryDownloadEnglishImageAsync(int appId, string cacheKey)
        {
            // Wait for available download slot
            await _downloadSemaphore.WaitAsync(_cts.Token);
            var pending = _pendingRequests.Count;
            var available = _downloadSemaphore.CurrentCount;
            DebugLogger.LogDebug($"Starting English download for {appId} - Pending: {pending}, Available slots: {available}");
            try
            {
                var languageSpecificUrlMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            static void AddUrl(Dictionary<string, List<string>> map, string url)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var domain = uri.Host;
                    if (!map.TryGetValue(domain, out var list))
                    {
                        list = new List<string>();
                        map[domain] = list;
                    }
                    list.Add(url);
                }
            }

            static List<string> RoundRobin(Dictionary<string, List<string>> map)
            {
                var domainQueues = map.ToDictionary(kv => kv.Key, kv => new Queue<string>(kv.Value));
                var keys = domainQueues.Keys.ToList();
                var result = new List<string>();
                bool added;
                do
                {
                    added = false;
                    foreach (var key in keys)
                    {
                        var queue = domainQueues[key];
                        if (queue.Count > 0)
                        {
                            result.Add(queue.Dequeue());
                            added = true;
                        }
                    }
                } while (added);
                return result;
            }

            // Get English header from Store API
            var header = await GetHeaderImageFromStoreApiAsync(appId, "english", _cts.Token);
            if (!string.IsNullOrEmpty(header))
            {
                AddUrl(languageSpecificUrlMap, header);
            }

            // Cloudflare CDN
            AddUrl(languageSpecificUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");

            // Steam CDN
            AddUrl(languageSpecificUrlMap, $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg");

            // Akamai CDN
            AddUrl(languageSpecificUrlMap, $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");

                var englishUrls = RoundRobin(languageSpecificUrlMap);

                // Use CDN load balancer with failover strategy
                var result = await TryDownloadWithCdnFailover(appId.ToString(), englishUrls, "english", appId, _cts.Token);

                if (!string.IsNullOrEmpty(result?.Path) && IsFreshImage(result.Value.Path))
                {
                    _imageCache[cacheKey] = result.Value.Path;
                    if (result.Value.Downloaded)
                    {
                        TriggerImageDownloadCompletedEvent(appId, result.Value.Path);
                    }
                    return result.Value.Path;
                }

                // Clean up any invalid result and record failure
                if (!string.IsNullOrEmpty(result?.Path))
                {
                    try { File.Delete(result.Value.Path); } catch { }
                }

                // Record English download failure only if not cancelled
                if (!_cts.IsCancellationRequested)
                {
                    _cache.RecordFailedDownload(appId, "english");
                }
                return null;
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }

        private string? GetFallbackImagePath()
        {
            var noIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "no_icon.png");
            return File.Exists(noIconPath) ? noIconPath : null;
        }

        private void TriggerImageDownloadCompletedEvent(int appId, string? path)
        {
            var eventKey = $"{appId}_{path ?? "null"}";
            lock (_eventLock)
            {
                if (_completedEvents.Contains(eventKey))
                {
                    DebugLogger.LogDebug($"Skipping duplicate ImageDownloadCompleted event for {appId}");
                    return;
                }
                _completedEvents.Add(eventKey);
            }
            ImageDownloadCompleted?.Invoke(appId, path);
        }
        private async Task<string?> GetHeaderImageFromStoreApiAsync(int appId, string language, CancellationToken cancellationToken)
        {
            try
            {
                var storeApiUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={language}";
                using var response = await _httpClient.GetAsync(storeApiUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var storeData = JsonSerializer.Deserialize(jsonContent, StoreApiJsonContext.Default.DictionaryStringStoreApiResponse);

                if (storeData != null && storeData.TryGetValue(appId.ToString(), out var app) && app.Success)
                {
                    var header = app.Data?.HeaderImage;
                    if (!string.IsNullOrEmpty(header))
                    {
                        if (!header.Contains("?"))
                        {
                            header += $"?l={language}";
                        }
                        return header;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                DebugLogger.LogDebug($"Error fetching store API data for {appId}: {ex.Message}");
            }
            catch (JsonException ex)
            {
                DebugLogger.LogDebug($"Malformed store API response for {appId}: {ex.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DebugLogger.LogDebug($"Store API request for {appId} cancelled");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                DebugLogger.LogDebug($"Store API request for {appId} timed out: {ex.Message}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Unexpected error parsing store API response for {appId}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Download image with CDN failover strategy
        /// </summary>
        private async Task<GameImageCache.ImageResult?> TryDownloadWithCdnFailover(
            string cacheKey,
            List<string> cdnUrls,
            string language,
            int? failureId,
            CancellationToken cancellationToken)
        {
            if (cdnUrls == null || cdnUrls.Count == 0)
                return null;

            // Try up to 3 times (at least once per CDN)
            var attemptedUrls = new HashSet<string>();
            for (int attempt = 0; attempt < Math.Min(3, cdnUrls.Count); attempt++)
            {
                // Select best available CDN
                var availableUrls = cdnUrls.Where(u => !attemptedUrls.Contains(u)).ToList();
                if (availableUrls.Count == 0)
                    break;

                var selectedUrl = _cdnLoadBalancer.SelectBestCdn(availableUrls);
                attemptedUrls.Add(selectedUrl);

                if (!Uri.TryCreate(selectedUrl, UriKind.Absolute, out var uri))
                {
                    DebugLogger.LogDebug($"Invalid URL: {selectedUrl}");
                    continue;
                }

                var domain = uri.Host;
                _cdnLoadBalancer.IncrementActiveRequests(domain);

                try
                {
                    DebugLogger.LogDebug($"Attempting download from {domain} (attempt {attempt + 1}/{Math.Min(3, cdnUrls.Count)})");

                    var result = await _cache.GetImagePathAsync(
                        cacheKey,
                        uri,
                        language,
                        failureId,
                        cancellationToken,
                        checkEnglishFallback: false);

                    if (!string.IsNullOrEmpty(result.Path))
                    {
                        _cdnLoadBalancer.RecordSuccess(domain);
                        DebugLogger.LogDebug($"Successfully downloaded from {domain}");
                        return result;
                    }
                    else
                    {
                        _cdnLoadBalancer.RecordFailure(domain);
                        DebugLogger.LogDebug($"Download failed from {domain} (empty result)");
                    }
                }
                catch (HttpRequestException ex) when (
                    ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
                {
                    // CDN returned rate limit error, mark as blocked
                    _cdnLoadBalancer.RecordBlockedDomain(domain, TimeSpan.FromMinutes(5));
                    DebugLogger.LogDebug($"CDN {domain} returned rate limit error ({ex.Message}), marking as blocked");
                }
                catch (Exception ex)
                {
                    _cdnLoadBalancer.RecordFailure(domain);
                    DebugLogger.LogDebug($"Download error from {domain}: {ex.Message}");
                }
                finally
                {
                    _cdnLoadBalancer.DecrementActiveRequests(domain);
                }
            }

            // All CDNs failed
            return null;
        }

        /// <summary>
        /// Get CDN statistics for monitoring
        /// </summary>
        public Dictionary<string, (int Active, bool IsBlocked, double SuccessRate)> GetCdnStats()
        {
            return _cdnLoadBalancer.GetStats();
        }

        public void ClearCache(string? specificLanguage = null)
        {
            if (specificLanguage != null)
            {
                _cache.ClearCache(specificLanguage);
                var keys = new List<string>();
                foreach (var kv in _imageCache)
                {
                    if (kv.Key.EndsWith($"_{specificLanguage}"))
                    {
                        keys.Add(kv.Key);
                    }
                }
                foreach (var key in keys)
                {
                    _imageCache.Remove(key);
                }
            }
            else
            {
                _cache.ClearCache();
                _imageCache.Clear();
                lock (_eventLock)
                {
                    _completedEvents.Clear();
                }
            }
        }

        public void ClearGeneralCache() => ClearCache();

        /// <summary>
        /// Cleanup duplicated English images that were previously copied to language-specific folders.
        /// This removes redundant files and reclaims disk space.
        /// </summary>
        /// <param name="dryRun">If true, only reports what would be deleted without actually deleting</param>
        /// <returns>Number of duplicated files found (and deleted if not dry run)</returns>
        public int CleanupDuplicatedEnglishImages(bool dryRun = false)
        {
            return _cache.CleanupDuplicatedEnglishImages(dryRun);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _downloadSemaphore.Dispose();
            if (_disposeHttpClient)
            {
                _httpClient.Dispose();
            }
            _cache.Dispose();
            _imageCache.Clear();
        }

        private static bool IsFreshImage(string path)
        {
            try
            {
                if (!ImageValidation.IsValidImage(path))
                {
                    return false;
                }

                var info = new FileInfo(path);
                if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(30))
                {
                    return false;
                }
                return true;
            }
            catch { }
            return false;
        }
    }

    internal class StoreApiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public StoreApiData? Data { get; set; }
    }

    internal class StoreApiData
    {
        [JsonPropertyName("header_image")]
        public string? HeaderImage { get; set; }
    }
}
