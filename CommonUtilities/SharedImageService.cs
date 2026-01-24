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
    /// <summary>
    /// High-level image service providing multi-language support, intelligent caching, and CDN failover.
    /// Coordinates image downloads across Steam CDNs with rate limiting, concurrency control, and English fallback logic.
    /// </summary>
    /// <remarks>
    /// This service implements a three-layer caching strategy:
    /// <list type="number">
    /// <item>In-memory cache for immediate access to recently used images</item>
    /// <item>Disk cache with 30-day TTL and MIME validation</item>
    /// <item>Language-specific cache folders with English fallback support</item>
    /// </list>
    /// Features language switching with non-blocking downloads, preventing UI freezes during rapid language changes.
    /// </remarks>
    public class SharedImageService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly GameImageCache _cache;
        private readonly bool _disposeHttpClient;
        private readonly ConcurrentDictionary<string, string> _imageCache = new();
        private readonly ConcurrentDictionary<string, Task<string?>> _pendingRequests = new();
        private readonly HashSet<string> _completedEvents = new();
        private readonly object _eventLock = new();
        private readonly object _ctsLock = new();
        private CancellationTokenSource _cts = new();
        private string _currentLanguage = "english";
        private int _requestCount = 0;

        // Concurrency limiter to prevent resource exhaustion
        private const int MAX_CONCURRENT_DOWNLOADS = 10;
        private const int MAX_PENDING_QUEUE_SIZE = 50; // Aggressively reduced from 100 to prevent WinUI 3 native crash
        private readonly SemaphoreSlim _downloadSemaphore = new(MAX_CONCURRENT_DOWNLOADS, MAX_CONCURRENT_DOWNLOADS);

        // CDN load balancer for intelligent CDN selection
        private readonly CdnLoadBalancer _cdnLoadBalancer;

        private static readonly JsonSerializerOptions JsonOptions =
            new() { TypeInfoResolver = StoreApiJsonContext.Default };

        /// <summary>
        /// Raised when an image download completes successfully (either from network or cache).
        /// </summary>
        /// <remarks>
        /// The event is triggered only once per unique appId-path combination to prevent duplicate notifications.
        /// Event handlers should not throw exceptions as errors are caught and logged.
        /// </remarks>
        public event Action<int, string?>? ImageDownloadCompleted;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedImageService"/> with HTTP client and optional cache.
        /// </summary>
        /// <param name="httpClient">The HTTP client to use for downloading images. Should be a shared singleton for connection pooling.</param>
        /// <param name="cache">Optional custom cache implementation. If null, creates default cache in %LOCALAPPDATA%/AchievoLab/ImageCache.</param>
        /// <param name="disposeHttpClient">Whether to dispose the HTTP client when this service is disposed. Set to true if passing a dedicated client.</param>
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

        /// <summary>
        /// Switches the service to a new language, cancelling ongoing downloads and clearing language-specific caches.
        /// </summary>
        /// <param name="language">The new language code (e.g., "english", "tchinese", "japanese").</param>
        /// <remarks>
        /// This method waits up to 5 seconds for pending downloads to cancel gracefully before clearing caches.
        /// The old CancellationTokenSource is NOT disposed to prevent ObjectDisposedException in ongoing downloads.
        /// After switching, cached images from the previous language are invalidated to trigger reloads with correct language.
        /// </remarks>
        public async Task SetLanguage(string language)
        {
            if (_currentLanguage != language)
            {
                AppLogger.LogDebug($"Switching language from {_currentLanguage} to {language}. Pending requests: {_pendingRequests.Count}");

                // Cancel ongoing operations
                _cts.Cancel();

                // CRITICAL: Wait for all pending requests to actually finish or be cancelled
                // before clearing caches to prevent race conditions
                var pending = _pendingRequests.Values.ToArray();
                if (pending.Length > 0)
                {
                    AppLogger.LogDebug($"Waiting for {pending.Length} pending downloads to complete or cancel...");
                    try
                    {
                        // Wait up to 5 seconds for pending requests to complete/cancel
                        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(5000));
                        AppLogger.LogDebug($"Pending downloads completed or timed out");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Exception while waiting for pending downloads: {ex.Message}");
                    }
                }

                // Now clear caches and reset state
                _imageCache.Clear();
                _pendingRequests.Clear();
                lock (_eventLock)
                {
                    _completedEvents.Clear();
                }

                // CRITICAL: Do NOT dispose old CTS immediately - old downloads may still be using it
                // Just create a new CTS for new operations and let old one be garbage collected
                // _cts.Dispose(); // Removed - causes ObjectDisposedException in old downloads
                _cts = new CancellationTokenSource();
                _currentLanguage = language;

                AppLogger.LogDebug($"Language switch completed. Reset state for {language}");
            }
        }

        /// <summary>
        /// Gets the currently active language code.
        /// </summary>
        /// <returns>The current language code (e.g., "english", "tchinese", "japanese").</returns>
        public string GetCurrentLanguage() => _currentLanguage;

        /// <summary>
        /// Gets the count of pending download requests currently in progress or queued.
        /// </summary>
        /// <returns>The number of pending image download requests.</returns>
        /// <remarks>
        /// Used for monitoring and preventing queue overflow. Requests are automatically cleaned up when completed.
        /// </remarks>
        public int GetPendingRequestsCount() => _pendingRequests.Count;

        /// <summary>
        /// Gets the number of available download slots before hitting the concurrency limit.
        /// </summary>
        /// <returns>The number of available download slots (0 to MAX_CONCURRENT_DOWNLOADS).</returns>
        /// <remarks>
        /// When this returns 0, new downloads will wait for existing downloads to complete.
        /// </remarks>
        public int GetAvailableDownloadSlots() => _downloadSemaphore.CurrentCount;

        /// <summary>
        /// Removes completed, cancelled, or faulted requests from the pending requests dictionary.
        /// </summary>
        /// <remarks>
        /// This method is called automatically every 50 requests and during language switches to prevent memory leaks.
        /// Stale requests are identified by checking their task completion status.
        /// </remarks>
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
                AppLogger.LogDebug($"Cleaned up {staleKeys.Count} stale pending requests. Remaining: {_pendingRequests.Count}");
            }
        }

        /// <summary>
        /// Cancels all pending downloads without clearing caches.
        /// </summary>
        /// <remarks>
        /// Designed for rapid scrolling/viewport changes where downloads for off-screen items should be cancelled.
        /// Unlike <see cref="SetLanguage"/>, this preserves in-memory and disk caches for fast re-display.
        /// A new CancellationTokenSource is created for subsequent downloads.
        /// </remarks>
        public void CancelPendingDownloads()
        {
            var pendingCount = _pendingRequests.Count;
            if (pendingCount == 0)
                return;

            AppLogger.LogDebug($"Cancelling {pendingCount} pending downloads due to rapid viewport change");

            // Cancel all in-flight downloads
            _cts.Cancel();

            // Create new CTS for future requests
            _cts = new CancellationTokenSource();

            // Clean up cancelled tasks from pending dictionary
            CleanupStaleRequests();

            AppLogger.LogDebug($"Pending downloads cancelled. Remaining: {_pendingRequests.Count}");
        }

        /// <summary>
        /// Checks whether an image for the specified app exists in the cache.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="language">The language code to check.</param>
        /// <param name="checkEnglishFallback">Whether to check the English cache if language-specific image not found.</param>
        /// <returns>True if the image exists in cache; otherwise, false.</returns>
        public bool HasImage(int appId, string language, bool checkEnglishFallback = false)
        {
            return _cache.TryGetCachedPath(appId.ToString(), language, checkEnglishFallback) != null;
        }

        /// <summary>
        /// Checks whether an image for the specified app exists in the cache. Alias for <see cref="HasImage"/>.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="language">The language code to check.</param>
        /// <param name="checkEnglishFallback">Whether to check the English cache if language-specific image not found.</param>
        /// <returns>True if the image exists in cache; otherwise, false.</returns>
        public bool IsImageCached(int appId, string language, bool checkEnglishFallback = false) => HasImage(appId, language, checkEnglishFallback);

        /// <summary>
        /// Gets the cached image path for an app without triggering a download.
        /// </summary>
        /// <param name="appId">Steam app ID</param>
        /// <param name="language">Language code (e.g., "english", "tchinese")</param>
        /// <param name="checkEnglishFallback">Whether to check English cache if language-specific image not found</param>
        /// <returns>Full path to cached image file, or null if not cached</returns>
        public string? TryGetCachedPath(int appId, string language, bool checkEnglishFallback = false)
        {
            return _cache.TryGetCachedPath(appId.ToString(), language, checkEnglishFallback);
        }

        /// <summary>
        /// Gets the cached or downloads the game image for the specified app and language.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="language">Optional language code. If null, uses the current language set via <see cref="SetLanguage"/>.</param>
        /// <returns>The full path to the cached or downloaded image, or path to fallback icon if download fails.</returns>
        /// <remarks>
        /// <para>This method implements an intelligent multi-step caching and download strategy:</para>
        /// <list type="number">
        /// <item>Checks in-memory cache for instant access</item>
        /// <item>Checks disk cache with freshness validation</item>
        /// <item>For non-English languages, checks English cache as potential fallback</item>
        /// <item>Skips download if recently failed (7-day failure tracking)</item>
        /// <item>Attempts language-specific download from multiple CDNs</item>
        /// <item>Falls back to English download if language-specific fails</item>
        /// <item>Uses expired cache if all downloads fail</item>
        /// <item>Returns hardcoded fallback icon as last resort</item>
        /// </list>
        /// <para>
        /// Queue overflow protection: If pending requests exceed MAX_PENDING_QUEUE_SIZE, attempts to return
        /// cached English image or fallback without starting a new download.
        /// </para>
        /// <para>
        /// Duplicate request protection: If a request for the same appId+language is already in progress,
        /// returns the existing task result instead of starting a duplicate download.
        /// </para>
        /// </remarks>
        public async Task<string?> GetGameImageAsync(int appId, string? language = null)
        {
            // Auto-cleanup stale pending requests every 50 calls for faster queue turnover
            if (Interlocked.Increment(ref _requestCount) % 50 == 0)
            {
                CleanupStaleRequests();
            }

            language ??= _currentLanguage;
            var originalLanguage = language;
            var cacheKey = $"{appId}_{language}";

            // Prevent queue overflow - if too many pending requests, try English fallback first
            if (_pendingRequests.Count >= MAX_PENDING_QUEUE_SIZE)
            {
                AppLogger.LogDebug($"Skipping image request for {appId} - queue full ({_pendingRequests.Count} pending)");

                // Try to use English fallback if requesting non-English language
                if (language != "english")
                {
                    var englishPath = _cache.TryGetCachedPath(appId.ToString(), "english", checkEnglishFallback: false);
                    if (!string.IsNullOrEmpty(englishPath) && File.Exists(englishPath))
                    {
                        AppLogger.LogDebug($"Queue full - using English fallback for {appId}");
                        return englishPath;
                    }
                }

                return GetFallbackImagePath();
            }

            // Check if there's already a request in progress for this image
            if (_pendingRequests.TryGetValue(cacheKey, out var existingTask))
            {
                try
                {
                    return await existingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    AppLogger.LogDebug($"Image request for {appId} cancelled");
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
                AppLogger.LogDebug($"Image request for {appId} cancelled");
                return GetFallbackImagePath();
            }
            finally
            {
                // Always remove from pending requests when done
                _pendingRequests.TryRemove(cacheKey, out _);
            }
        }

        /// <summary>
        /// Internal implementation of image retrieval with full caching and fallback logic.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="language">The requested language code.</param>
        /// <param name="originalLanguage">The original requested language (preserved for logging).</param>
        /// <param name="cacheKey">The cache key in format "appId_language".</param>
        /// <returns>The path to the image file, or null if all retrieval attempts fail.</returns>
        /// <remarks>
        /// This method implements the core 8-step retrieval strategy described in <see cref="GetGameImageAsync"/>.
        /// It respects failure tracking (7-day cooldown) and uses expired cache as fallback when fresh downloads fail.
        /// </remarks>
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
                _imageCache.TryRemove(cacheKey, out _);
                // Don't record as failed download - file was corrupted, not missing
            }

            // Step 1: Check language-specific disk cache (even if expired, we'll use it as fallback)
            var diskCachedPath = _cache.TryGetCachedPath(appId.ToString(), language, checkEnglishFallback: false);
            if (!string.IsNullOrEmpty(diskCachedPath))
            {
                if (IsFreshImage(diskCachedPath))
                {
                    _imageCache[cacheKey] = diskCachedPath;
                    TriggerImageDownloadCompletedEvent(appId, diskCachedPath);
                    return diskCachedPath;
                }
                // If expired, keep the path and try to download fresh version, but use expired as fallback if download fails
            }

            // Step 2: For non-English languages, check English cache as potential fallback
            string? englishFallbackPath = null;
            if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                englishFallbackPath = _cache.TryGetCachedPath(appId.ToString(), "english", checkEnglishFallback: false);
                if (!string.IsNullOrEmpty(englishFallbackPath) && IsFreshImage(englishFallbackPath))
                {
                    // Fresh English image found, we can use it immediately if target language download fails
                    AppLogger.LogDebug($"Fresh English fallback available for {appId}");
                }
            }

            // Step 3: Check failure tracking - if failed within 7 days for this language, skip download but use cache
            if (_cache.ShouldSkipDownload(appId, language))
            {
                AppLogger.LogDebug($"Skipping {language} download for {appId} due to recent failure (within 7 days)");

                // Use expired language-specific cache if available
                if (!string.IsNullOrEmpty(diskCachedPath) && File.Exists(diskCachedPath) && ImageValidation.IsValidImage(diskCachedPath))
                {
                    AppLogger.LogDebug($"Using expired {language} cache for {appId}");
                    _imageCache[cacheKey] = diskCachedPath;
                    TriggerImageDownloadCompletedEvent(appId, diskCachedPath);
                    return diskCachedPath;
                }

                // Fall back to English
                return await TryEnglishFallbackAsync(appId, language, cacheKey);
            }

            // Step 4: Try to download language-specific image
            var downloadResult = await TryDownloadLanguageSpecificImageAsync(appId, language, cacheKey);
            if (downloadResult != null)
            {
                // Step 5: Download successful - remove failure record if exists and return
                _cache.RemoveFailedRecord(appId, language);
                return downloadResult;
            }

            // Step 6: Language-specific download failed - record failure only if not cancelled
            if (!_cts.IsCancellationRequested)
            {
                _cache.RecordFailedDownload(appId, language);
                AppLogger.LogDebug($"Failed to download {language} image for {appId}");
            }
            else
            {
                AppLogger.LogDebug($"Download for {appId} ({language}) was cancelled, not recording failure");
            }

            // Step 7: Use expired language-specific cache as fallback if available
            if (!string.IsNullOrEmpty(diskCachedPath) && File.Exists(diskCachedPath) && ImageValidation.IsValidImage(diskCachedPath))
            {
                AppLogger.LogDebug($"Using expired {language} cache as fallback for {appId}");
                _imageCache[cacheKey] = diskCachedPath;
                TriggerImageDownloadCompletedEvent(appId, diskCachedPath);
                return diskCachedPath;
            }

            // Step 8: English fallback logic (only for non-English languages)
            if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                return await TryEnglishFallbackAsync(appId, language, cacheKey);
            }

            // If we reach here, English download failed - return fallback image
            return GetFallbackImagePath();
        }

        /// <summary>
        /// Attempts to download the language-specific image from multiple CDNs with failover.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="language">The target language code.</param>
        /// <param name="cacheKey">The cache key for storing the result.</param>
        /// <returns>The path to the downloaded image, or null if all CDN attempts fail.</returns>
        /// <remarks>
        /// This method queries Steam Store API for the official header image URL, then attempts download from
        /// multiple CDNs (Cloudflare, Steam, Akamai) using round-robin load balancing. Enforces concurrency limits
        /// via semaphore to prevent resource exhaustion.
        /// </remarks>
        private async Task<string?> TryDownloadLanguageSpecificImageAsync(int appId, string language, string cacheKey)
        {

            // Wait for available download slot
            await _downloadSemaphore.WaitAsync(_cts.Token);
            var pending = _pendingRequests.Count;
            var available = _downloadSemaphore.CurrentCount;
            AppLogger.LogDebug($"Starting download for {appId} ({language}) - Pending: {pending}, Available slots: {available}");
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
                try
                {
                    _downloadSemaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore was disposed during language switch or shutdown, ignore
                    AppLogger.LogDebug($"SharedImageService semaphore already disposed, skipping release");
                }
                catch (SemaphoreFullException)
                {
                    // Semaphore was already released, ignore
                    AppLogger.LogDebug($"SharedImageService semaphore already at full count, skipping release");
                }
            }
        }

        /// <summary>
        /// Attempts to use English image as fallback when language-specific image is unavailable.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="targetLanguage">The originally requested language (for logging purposes).</param>
        /// <param name="cacheKey">The cache key for storing the result.</param>
        /// <returns>The path to English image (fresh or expired), or fallback icon if English also unavailable.</returns>
        /// <remarks>
        /// Priority order: fresh English cache → download fresh English → expired English cache → fallback icon.
        /// This ensures users see English images quickly while target language images are downloading in background.
        /// </remarks>
        private async Task<string?> TryEnglishFallbackAsync(int appId, string targetLanguage, string cacheKey)
        {
            // Check English cache first (prefer fresh, but accept expired as fallback)
            var englishCachedPath = _cache.TryGetCachedPath(appId.ToString(), "english", checkEnglishFallback: false);

            // If fresh English cache exists, use it immediately
            if (!string.IsNullOrEmpty(englishCachedPath) && IsFreshImage(englishCachedPath))
            {
                AppLogger.LogDebug($"Found fresh English cached image for {appId}, using as fallback");
                _imageCache[cacheKey] = englishCachedPath;
                TriggerImageDownloadCompletedEvent(appId, englishCachedPath);
                return englishCachedPath;
            }

            // Try to download fresh English image
            var englishDownloadResult = await TryDownloadEnglishImageAsync(appId, cacheKey);
            if (englishDownloadResult != null)
            {
                AppLogger.LogDebug($"Downloaded fresh English image for {appId}");
                return englishDownloadResult;
            }

            // Download failed - use expired English cache if available and valid
            if (!string.IsNullOrEmpty(englishCachedPath) && File.Exists(englishCachedPath) && ImageValidation.IsValidImage(englishCachedPath))
            {
                AppLogger.LogDebug($"Using expired English cached image for {appId} as fallback (download failed)");
                _imageCache[cacheKey] = englishCachedPath;
                TriggerImageDownloadCompletedEvent(appId, englishCachedPath);
                return englishCachedPath;
            }

            // No English image available - show fallback icon
            AppLogger.LogDebug($"No English image available for {appId}, showing fallback icon");
            return GetFallbackImagePath();
        }

        /// <summary>
        /// Attempts to download the English version of a game image from multiple CDNs.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="cacheKey">The cache key for storing the result.</param>
        /// <returns>The path to the downloaded English image, or null if all attempts fail.</returns>
        /// <remarks>
        /// Similar to <see cref="TryDownloadLanguageSpecificImageAsync"/> but specifically for English images.
        /// Records download failures to prevent retry storms. Uses same CDN failover strategy.
        /// </remarks>
        private async Task<string?> TryDownloadEnglishImageAsync(int appId, string cacheKey)
        {
            // Wait for available download slot
            await _downloadSemaphore.WaitAsync(_cts.Token);
            var pending = _pendingRequests.Count;
            var available = _downloadSemaphore.CurrentCount;
            AppLogger.LogDebug($"Starting English download for {appId} - Pending: {pending}, Available slots: {available}");
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
                try
                {
                    _downloadSemaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore was disposed during language switch or shutdown, ignore
                    AppLogger.LogDebug($"SharedImageService semaphore already disposed, skipping release");
                }
                catch (SemaphoreFullException)
                {
                    // Semaphore was already released, ignore
                    AppLogger.LogDebug($"SharedImageService semaphore already at full count, skipping release");
                }
            }
        }

        /// <summary>
        /// Gets the path to the hardcoded fallback image shown when no game image is available.
        /// </summary>
        /// <returns>The path to Assets/no_icon.png, or null if the file doesn't exist.</returns>
        private string? GetFallbackImagePath()
        {
            var noIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "no_icon.png");
            return File.Exists(noIconPath) ? noIconPath : null;
        }

        /// <summary>
        /// Raises the <see cref="ImageDownloadCompleted"/> event with duplicate detection.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="path">The path to the downloaded or cached image.</param>
        /// <remarks>
        /// Uses a HashSet to track already-fired events and prevent duplicate notifications for the same appId-path combination.
        /// Catches and logs exceptions from event handlers to prevent crashes in the image service.
        /// </remarks>
        private void TriggerImageDownloadCompletedEvent(int appId, string? path)
        {
            var eventKey = $"{appId}_{path ?? "null"}";
            lock (_eventLock)
            {
                if (_completedEvents.Contains(eventKey))
                {
                    AppLogger.LogDebug($"Skipping duplicate ImageDownloadCompleted event for {appId}");
                    return;
                }
                _completedEvents.Add(eventKey);
            }

            try
            {
                ImageDownloadCompleted?.Invoke(appId, path);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in ImageDownloadCompleted event handler for {appId}: {ex.GetType().Name}: {ex.Message}");
                AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                // Don't rethrow - event handler errors shouldn't crash the image service
            }
        }

        /// <summary>
        /// Queries the Steam Store API to get the official header image URL for a game.
        /// </summary>
        /// <param name="appId">The Steam application ID.</param>
        /// <param name="language">The language code to request (e.g., "english", "tchinese").</param>
        /// <param name="cancellationToken">Cancellation token for request cancellation.</param>
        /// <returns>The header image URL with language parameter, or null if API request fails.</returns>
        /// <remarks>
        /// The Store API provides the most accurate image URLs but has rate limits. This method handles common
        /// exceptions (HTTP errors, JSON parsing errors, timeouts) gracefully and returns null on failure.
        /// Appends language parameter to URL if not already present.
        /// </remarks>
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
                AppLogger.LogDebug($"Error fetching store API data for {appId}: {ex.Message}");
            }
            catch (JsonException ex)
            {
                AppLogger.LogDebug($"Malformed store API response for {appId}: {ex.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppLogger.LogDebug($"Store API request for {appId} cancelled");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                AppLogger.LogDebug($"Store API request for {appId} timed out: {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Unexpected error parsing store API response for {appId}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Downloads an image using CDN failover strategy with intelligent load balancing.
        /// </summary>
        /// <param name="cacheKey">The cache key for storing the downloaded image.</param>
        /// <param name="cdnUrls">List of CDN URLs to try, in priority order.</param>
        /// <param name="language">The language code for the image.</param>
        /// <param name="failureId">Optional app ID to record download failures for failure tracking.</param>
        /// <param name="cancellationToken">Cancellation token for request cancellation.</param>
        /// <returns>The image result containing path and download status, or null if all CDNs fail.</returns>
        /// <remarks>
        /// Attempts up to 3 CDN downloads, selecting the best available CDN based on:
        /// <list type="bullet">
        /// <item>Active request count (prefer less loaded CDNs)</item>
        /// <item>Success rate history</item>
        /// <item>Whether CDN is currently blocked (429/403 responses trigger 5-minute blocks)</item>
        /// </list>
        /// Records success/failure metrics for each CDN to optimize future selections.
        /// </remarks>
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
                    AppLogger.LogDebug($"Invalid URL: {selectedUrl}");
                    continue;
                }

                var domain = uri.Host;
                _cdnLoadBalancer.IncrementActiveRequests(domain);

                try
                {
                    AppLogger.LogDebug($"Attempting download from {domain} (attempt {attempt + 1}/{Math.Min(3, cdnUrls.Count)})");

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
                        AppLogger.LogDebug($"Successfully downloaded from {domain}");
                        return result;
                    }
                    else
                    {
                        _cdnLoadBalancer.RecordFailure(domain);
                        AppLogger.LogDebug($"Download failed from {domain} (empty result)");
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
                    AppLogger.LogDebug($"CDN {domain} returned rate limit error ({ex.Message}), marking as blocked");
                }
                catch (Exception ex)
                {
                    _cdnLoadBalancer.RecordFailure(domain);
                    AppLogger.LogDebug($"Download error from {domain}: {ex.Message}");
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
        /// Gets statistics for all monitored CDNs.
        /// </summary>
        /// <returns>Dictionary mapping CDN domain to active request count, blocked status, and success rate.</returns>
        /// <remarks>
        /// Useful for monitoring CDN health and debugging download issues. Success rate is calculated as
        /// successful downloads / total attempts.
        /// </remarks>
        public Dictionary<string, (int Active, bool IsBlocked, double SuccessRate)> GetCdnStats()
        {
            return _cdnLoadBalancer.GetStats();
        }

        /// <summary>
        /// Clears cached images from memory and disk.
        /// </summary>
        /// <param name="specificLanguage">Optional language code to clear only that language's cache. If null, clears all languages.</param>
        /// <remarks>
        /// When clearing a specific language, only removes files from that language's cache folder.
        /// When clearing all languages (specificLanguage=null), also clears the event deduplication tracking.
        /// </remarks>
        public void ClearCache(string? specificLanguage = null)
        {
            if (specificLanguage != null)
            {
                _cache.ClearCache(specificLanguage);
                var keysToRemove = _imageCache.Keys
                    .Where(k => k.EndsWith($"_{specificLanguage}", StringComparison.Ordinal))
                    .ToList();
                foreach (var key in keysToRemove)
                {
                    _imageCache.TryRemove(key, out _);
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

        /// <summary>
        /// Clears all cached images from memory and disk. Alias for <see cref="ClearCache()"/> with null parameter.
        /// </summary>
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

        /// <summary>
        /// Releases all resources used by the <see cref="SharedImageService"/>.
        /// </summary>
        /// <remarks>
        /// Cancels all pending downloads, disposes the semaphore and cache, and optionally disposes the HTTP client
        /// if <c>disposeHttpClient</c> was set to true in the constructor. Clears the in-memory image cache.
        /// </remarks>
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

        /// <summary>
        /// Validates that an image file exists, is readable, and passes MIME type validation.
        /// </summary>
        /// <param name="path">The file path to validate.</param>
        /// <returns>True if the image is valid and fresh; otherwise, false.</returns>
        /// <remarks>
        /// Images are considered fresh if they pass MIME validation, regardless of age.
        /// The 30-day TTL check has been removed - successfully cached images never expire.
        /// </remarks>
        private static bool IsFreshImage(string path)
        {
            try
            {
                if (!ImageValidation.IsValidImage(path))
                {
                    return false;
                }

                var info = new FileInfo(path);
                // CHANGED: Successfully downloaded images never expire (was 30 days)
                // Check removed - cached images are always considered fresh
                // if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(30))
                // {
                //     return false;
                // }
                return true;
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// Represents the response from Steam Store API's appdetails endpoint.
    /// </summary>
    internal class StoreApiResponse
    {
        /// <summary>
        /// Gets or sets whether the API request was successful.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the app data returned by the API.
        /// </summary>
        [JsonPropertyName("data")]
        public StoreApiData? Data { get; set; }
    }

    /// <summary>
    /// Represents the data portion of a Steam Store API response.
    /// </summary>
    internal class StoreApiData
    {
        /// <summary>
        /// Gets or sets the URL to the game's header image.
        /// </summary>
        [JsonPropertyName("header_image")]
        public string? HeaderImage { get; set; }
    }
}
