using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CommonUtilities
{
    /// <summary>
    /// Shared image cache that downloads and stores images on disk with
    /// MIME validation, cache expiration, optional failure tracking, and
    /// per-domain/global throttling.
    /// </summary>
    public class GameImageCache : IDisposable
    {
        /// <summary>
        /// Represents the result of an image retrieval operation.
        /// </summary>
        /// <param name="Path">The absolute file path to the image, or empty string if retrieval failed.</param>
        /// <param name="Downloaded">True if the image was freshly downloaded from a CDN; false if loaded from cache or failed.</param>
        /// <param name="IsNotFound">True if the image was not found (404); false otherwise. Used to distinguish "image doesn't exist" from "CDN failure".</param>
        public readonly record struct ImageResult(string Path, bool Downloaded, bool IsNotFound = false);

        private readonly string _baseCacheDir;
        private readonly HttpClient _http;
        private readonly bool _disposeHttpClient;
        private readonly SemaphoreSlim _concurrency;
        private readonly ConcurrentDictionary<string, Task<ImageResult>> _inFlight = new();
        private readonly TimeSpan _cacheDuration;
        private readonly ImageFailureTrackingService? _failureTracker;
        private readonly DomainRateLimiter _rateLimiter;
        private readonly ConcurrentDictionary<string, (DateTime Time, bool WasNotFound)> _lastErrors = new();

        private int _totalRequests;
        private int _completed;

        private static readonly Dictionary<string, string> MimeToExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/jpg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
            ["image/bmp"] = ".bmp",
            ["image/avif"] = ".avif",
            ["image/x-icon"] = ".ico",
            ["image/vnd.microsoft.icon"] = ".ico",
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="GameImageCache"/> with configurable rate limiting,
        /// concurrency control, and optional failure tracking.
        /// </summary>
        /// <param name="baseCacheDir">The base directory where language-specific cache folders will be created (e.g., %LOCALAPPDATA%/AchievoLab/ImageCache).</param>
        /// <param name="failureTracker">Optional service to track download failures per language to prevent retry storms. Implements 7-day failure tracking.</param>
        /// <param name="maxConcurrency">Maximum number of concurrent downloads allowed across all domains (default: 4). This is the per-cache semaphore limit.</param>
        /// <param name="cacheDuration">Time-to-live for successfully cached images. Defaults to TimeSpan.MaxValue (never expire). Images are validated via MIME check on every access.</param>
        /// <param name="maxConcurrentRequestsPerDomain">Maximum concurrent requests allowed per domain to prevent overwhelming CDN servers (default: 2).</param>
        /// <param name="tokenBucketCapacity">Maximum token capacity for the rate limiter's token bucket (default: 60). Controls burst allowance.</param>
        /// <param name="fillRatePerSecond">Rate at which tokens are added to the bucket per second (default: 1). Lower values = stricter rate limiting.</param>
        /// <param name="initialTokens">Initial tokens in the bucket. Defaults to <paramref name="tokenBucketCapacity"/> (start with full bucket).</param>
        /// <param name="baseDomainDelay">Base delay between requests to the same domain. If null, only token bucket limits apply.</param>
        /// <param name="jitterSeconds">Random jitter added to delays to avoid thundering herd (default: 0.1s).</param>
        /// <param name="httpClient">Optional HttpClient instance. If null, uses <see cref="HttpClientProvider.Shared"/> singleton.</param>
        /// <param name="disposeHttpClient">If true and <paramref name="httpClient"/> is provided, the HttpClient will be disposed with this cache instance.</param>
        public GameImageCache(string baseCacheDir,
            ImageFailureTrackingService? failureTracker = null,
            int maxConcurrency = 4,
            TimeSpan? cacheDuration = null,
            int maxConcurrentRequestsPerDomain = 2,
            int tokenBucketCapacity = 60,
            double fillRatePerSecond = 1,
            double? initialTokens = null,
            TimeSpan? baseDomainDelay = null,
            double jitterSeconds = 0.1,
            HttpClient? httpClient = null,
            bool disposeHttpClient = false)
        {
            _baseCacheDir = baseCacheDir;
            Directory.CreateDirectory(_baseCacheDir);
            _failureTracker = failureTracker;
            _concurrency = new SemaphoreSlim(maxConcurrency);
            // CHANGED: Successfully downloaded images never expire (was 30 days)
            _cacheDuration = cacheDuration ?? TimeSpan.MaxValue;

            _rateLimiter = new DomainRateLimiter(maxConcurrentRequestsPerDomain, tokenBucketCapacity, fillRatePerSecond, initialTokens ?? tokenBucketCapacity, baseDomainDelay, jitterSeconds);
            _http = httpClient ?? HttpClientProvider.Shared;
            _disposeHttpClient = disposeHttpClient && httpClient != null;
        }

        /// <summary>
        /// Gets the language-specific cache directory, creating it if it doesn't exist.
        /// Cache isolation by language prevents cross-contamination of localized images.
        /// </summary>
        /// <param name="language">The language code (e.g., "english", "tchinese", "japanese").</param>
        /// <returns>The absolute path to the language-specific cache directory.</returns>
        private string GetCacheDir(string language)
        {
            var dir = Path.Combine(_baseCacheDir, language);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Raised whenever download progress changes. Parameters are the number
        /// of completed downloads and total initiated downloads.
        /// </summary>
        public event Action<int, int>? ProgressChanged;

        /// <summary>
        /// Resets the download progress counters to zero and triggers a <see cref="ProgressChanged"/> event.
        /// Useful when starting a new batch of downloads (e.g., after language switch) to reset UI progress indicators.
        /// </summary>
        public void ResetProgress()
        {
            Interlocked.Exchange(ref _totalRequests, 0);
            Interlocked.Exchange(ref _completed, 0);
            ReportProgress();
        }

        /// <summary>
        /// Gets the current download progress counters in a thread-safe manner.
        /// </summary>
        /// <returns>A tuple containing (completed downloads, total initiated downloads).</returns>
        public (int completed, int total) GetProgress()
        {
            var total = Volatile.Read(ref _totalRequests);
            var completed = Volatile.Read(ref _completed);
            return (completed, total);
        }

        /// <summary>
        /// Attempts to retrieve a cached image path synchronously without downloading.
        /// Checks all supported image extensions and validates cache freshness via MIME validation.
        /// </summary>
        /// <param name="cacheKey">The cache key, typically the Steam AppID (e.g., "480").</param>
        /// <param name="language">The target language for the image (default: "english").</param>
        /// <param name="checkEnglishFallback">If true and target language is not English, also checks English cache as fallback (default: true).</param>
        /// <returns>The absolute path to the cached image if found and valid, otherwise null.</returns>
        /// <remarks>
        /// This method is optimized for instant display during language switches. It enables the smart fallback strategy
        /// where English images display immediately while target language downloads in background.
        /// </remarks>
        public string? TryGetCachedPath(string cacheKey, string language = "english", bool checkEnglishFallback = true)
        {
            string? Check(string basePath)
            {
                foreach (var candidateExt in new HashSet<string>(MimeToExtension.Values))
                {
                    var path = basePath + candidateExt;
                    if (File.Exists(path))
                    {
                        if (IsCacheValid(path))
                        {
                            return path;
                        }
                    }
                }
                return null;
            }

            var dir = GetCacheDir(language);
            var basePath = Path.Combine(dir, cacheKey);
            var result = Check(basePath);
            if (result != null)
            {
                return result;
            }

            if (checkEnglishFallback && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                var englishBase = Path.Combine(GetCacheDir("english"), cacheKey);
                return Check(englishBase);
            }

            return null;
        }

        /// <summary>
        /// Attempts to retrieve a cached image URI synchronously without downloading.
        /// This is a convenience wrapper around <see cref="TryGetCachedPath"/> that returns a file:// URI.
        /// </summary>
        /// <param name="cacheKey">The cache key, typically the Steam AppID (e.g., "480").</param>
        /// <param name="language">The target language for the image (default: "english").</param>
        /// <param name="checkEnglishFallback">If true and target language is not English, also checks English cache as fallback (default: true).</param>
        /// <returns>A file:// URI to the cached image if found and valid, otherwise null.</returns>
        public Uri? TryGetCachedUri(string cacheKey, string language = "english", bool checkEnglishFallback = true)
        {
            var path = TryGetCachedPath(cacheKey, language, checkEnglishFallback);
            if (path != null && Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                return uri;
            }
            return null;
        }

        /// <summary>
        /// Gets the image path asynchronously, downloading from the specified URI if not cached.
        /// This is the core single-URL retrieval method with deduplication, failure tracking, and rate limiting.
        /// </summary>
        /// <param name="cacheKey">The cache key, typically the Steam AppID (e.g., "480").</param>
        /// <param name="uri">The CDN URL to download from if not cached.</param>
        /// <param name="language">The target language for the image (default: "english").</param>
        /// <param name="failureId">Optional AppID for failure tracking. If provided and download fails, this ID will be tracked to prevent retry storms for 7 days.</param>
        /// <param name="cancellationToken">Cancellation token to abort the download operation.</param>
        /// <param name="checkEnglishFallback">If true and target language is not English, checks English cache before downloading (default: true).</param>
        /// <returns>An <see cref="ImageResult"/> containing the image path and whether it was downloaded. Empty path indicates failure.</returns>
        /// <remarks>
        /// In-flight deduplication ensures multiple concurrent requests for the same image share a single download task.
        /// If the image is already cached and valid, it returns immediately with Downloaded=false.
        /// If failure tracking indicates recent failures for this ID+language, returns empty path without attempting download.
        /// </remarks>
        public Task<ImageResult> GetImagePathAsync(string cacheKey, Uri uri, string language = "english", int? failureId = null, CancellationToken cancellationToken = default, bool checkEnglishFallback = true)
        {
            var cacheDir = GetCacheDir(language);
            var basePath = Path.Combine(cacheDir, cacheKey);

            if (_inFlight.TryGetValue(basePath, out var existing))
            {
                return existing;
            }

            var cached = TryGetCachedPath(cacheKey, language, checkEnglishFallback);
            if (cached != null)
            {
                Interlocked.Increment(ref _totalRequests);
                Interlocked.Increment(ref _completed);
                ReportProgress();
                if (failureId.HasValue)
                {
                    _failureTracker?.RemoveFailedRecord(failureId.Value, language);
                }
                return Task.FromResult(new ImageResult(cached, false));
            }

            if (failureId.HasValue && _failureTracker?.ShouldSkipDownload(failureId.Value, language) == true)
            {
                Interlocked.Increment(ref _totalRequests);
                Interlocked.Increment(ref _completed);
                ReportProgress();
                return Task.FromResult(new ImageResult(string.Empty, false));
            }

            var ext = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".jpg";
            }

            // Use a wrapper task that ensures the key is added to _inFlight BEFORE DownloadAsync starts
            // This fixes a race condition where synchronous HTTP handlers (like in tests) would cause
            // DownloadAsync to complete before GetOrAdd returns, making TryRemove fail
            var task = _inFlight.GetOrAdd(basePath, _ =>
            {
                Interlocked.Increment(ref _totalRequests);
                ReportProgress();
                // Wrap in Task.Run to ensure the task is added to _inFlight before execution starts
                return Task.Run(async () =>
                {
                    try
                    {
                        return await DownloadAsync(cacheKey, language, uri, basePath, ext, failureId, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _inFlight.TryRemove(basePath, out Task<ImageResult>? _);
                        Interlocked.Increment(ref _completed);
                        ReportProgress();
                    }
                }, cancellationToken);
            });
            return task;
        }

        /// <summary>
        /// Gets the image path asynchronously by trying multiple CDN URLs in sequence with automatic English fallback.
        /// This is the high-level multi-URL retrieval method used by SharedImageService for Steam CDN resilience.
        /// </summary>
        /// <param name="cacheKey">The cache key, typically the Steam AppID (e.g., "480").</param>
        /// <param name="uris">A list of CDN URLs to try in order (e.g., Cloudflare, Akamai, Steam CDN).</param>
        /// <param name="language">The target language for the image (default: "english").</param>
        /// <param name="failureId">Optional AppID for failure tracking. Required for English fallback to work.</param>
        /// <param name="cancellationToken">Cancellation token to abort the download operation.</param>
        /// <param name="tryEnglishFallback">If true and non-English language fails after 2 404s, automatically tries English URLs (default: true).</param>
        /// <param name="checkEnglishFallback">If true and target language is not English, checks English cache before downloading (default: true).</param>
        /// <returns>An <see cref="ImageResult"/> containing the image path and whether it was downloaded, or null if all attempts failed.</returns>
        /// <remarks>
        /// This method implements the CDN fallback chain with smart 404 detection:
        /// 1. Tries each URL in the provided list sequentially
        /// 2. After 2 consecutive 404 errors for non-English languages, switches to English fallback URLs
        /// 3. English fallback tries header.jpg and logo.png URLs from multiple CDNs
        /// 4. Only records failure tracking if operation wasn't cancelled
        /// 5. Returns the first successful download or cached image found
        /// </remarks>
        public async Task<ImageResult?> GetImagePathAsync(string cacheKey, IEnumerable<string> uris, string language = "english", int? failureId = null, CancellationToken cancellationToken = default, bool tryEnglishFallback = true, bool checkEnglishFallback = true)
        {
            var urlList = uris as IList<string> ?? uris.ToList();
            int notFoundCount = 0;
            var totalUrls = urlList.Count;

            foreach (var url in urlList)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var result = await GetImagePathAsync(cacheKey, uri, language, failureId, cancellationToken, checkEnglishFallback).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(result.Path))
                    {
                        return result;
                    }

                    // Check if this was a 404 - use result.IsNotFound directly instead of _lastErrors lookup
                    // because each URL has a different key in _lastErrors
                    if (result.IsNotFound)
                    {
                        notFoundCount++;
                        AppLogger.LogDebug($"404 count for {cacheKey} in {language}: {notFoundCount}/{totalUrls}");

                        // After 2 CDN failures, try English fallback if we're not already on English
                        if (tryEnglishFallback && notFoundCount >= 2 && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
                        {
                            AppLogger.LogDebug($"Switching to English fallback for {cacheKey} after {notFoundCount} 404s");
                            var fallback = await TryEnglishFallbackAsync(cacheKey, language, failureId, cancellationToken).ConfigureAwait(false);
                            if (fallback != null)
                            {
                                return fallback;
                            }
                            if (failureId.HasValue)
                            {
                                _failureTracker?.RecordFailedDownload(failureId.Value, language);
                            }
                            return null;
                        }
                    }
                }
            }
            // If we got enough 404s, try English fallback
            if (tryEnglishFallback && notFoundCount >= 2 && failureId.HasValue && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.LogDebug($"Trying English fallback for {cacheKey} after {notFoundCount} 404s");
                var fallback = await TryEnglishFallbackAsync(cacheKey, language, failureId, cancellationToken).ConfigureAwait(false);
                if (fallback != null)
                {
                    return fallback;
                }
            }
            
            // If no 404s but all URLs failed and we haven't tried English fallback yet, try it as a last resort
            // But be careful not to record this as a failure if it's due to cancellation
            if (tryEnglishFallback && notFoundCount == 0 && failureId.HasValue && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppLogger.LogDebug($"All {totalUrls} URLs failed for {cacheKey} in {language} with no 404s, trying English fallback as last resort");
                    var fallback = await TryEnglishFallbackAsync(cacheKey, language, failureId, cancellationToken).ConfigureAwait(false);
                    if (fallback != null)
                    {
                        return fallback;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Don't record failure for cancelled operations
                    throw;
                }
            }
            
            // Only record failure if operation wasn't cancelled
            if (failureId.HasValue && !cancellationToken.IsCancellationRequested)
            {
                _failureTracker?.RecordFailedDownload(failureId.Value, language);
            }
            return null;
        }

        /// <summary>
        /// Checks if a URI has recently (within 10 seconds) returned a 404 Not Found error.
        /// This is used to implement the 404-counting logic that triggers English fallback after 2 consecutive 404s.
        /// </summary>
        /// <param name="uri">The URI to check.</param>
        /// <returns>True if the URI returned a 404 within the last 10 seconds, otherwise false.</returns>
        private bool IsRecentNotFoundError(Uri uri)
        {
            var key = uri.ToString();
            if (_lastErrors.TryGetValue(key, out var error))
            {
                // Consider an error recent if it happened within the last 10 seconds
                var isRecent = DateTime.UtcNow - error.Time < TimeSpan.FromSeconds(10);
                return isRecent && error.WasNotFound;
            }
            return false;
        }

        /// <summary>
        /// Records an error for a URI with timestamp and whether it was a 404 Not Found.
        /// Used by the 404-counting logic to determine when to trigger English fallback.
        /// </summary>
        /// <param name="uri">The URI that failed.</param>
        /// <param name="wasNotFound">True if the error was a 404 Not Found, false for other HTTP errors.</param>
        private void RecordError(Uri uri, bool wasNotFound)
        {
            var key = uri.ToString();
            _lastErrors[key] = (DateTime.UtcNow, wasNotFound);
        }

        /// <summary>
        /// Attempts to download an English version of the image as fallback when the requested language is not available.
        /// This is a critical part of the language fallback strategy that ensures users always see something.
        /// </summary>
        /// <param name="cacheKey">The cache key, typically the Steam AppID.</param>
        /// <param name="originalLanguage">The originally requested language that failed (used for logging and failure tracking removal).</param>
        /// <param name="failureId">The Steam AppID for constructing English CDN URLs. Required for this method to work.</param>
        /// <param name="cancellationToken">Cancellation token to abort the download operation.</param>
        /// <returns>An <see cref="ImageResult"/> with the English image path if found, otherwise null.</returns>
        /// <remarks>
        /// Fallback strategy:
        /// 1. First checks if English image is already cached (instant return)
        /// 2. If not cached, tries downloading from multiple English CDN URLs (header.jpg from Cloudflare, Steam CDN, Akamai)
        /// 3. After 2 404s, tries logo.png URLs as final fallback
        /// 4. Returns the English image path directly (no copying to original language folder)
        /// 5. On success, removes the failure record for the original language
        /// 6. On complete failure, records failure for English language
        /// </remarks>
        private async Task<ImageResult?> TryEnglishFallbackAsync(string cacheKey, string originalLanguage, int? failureId, CancellationToken cancellationToken)
        {
            AppLogger.LogDebug($"Attempting English fallback for {cacheKey} (original: {originalLanguage})");

            if (!failureId.HasValue)
            {
                return null;
            }

            // First, check if there's already a valid English cached image
            var existingEnglishPath = TryGetCachedPath(cacheKey, "english", checkEnglishFallback: false);
            if (!string.IsNullOrEmpty(existingEnglishPath) && IsCacheValid(existingEnglishPath))
            {
                AppLogger.LogDebug($"Found existing English cached image for {cacheKey}, using directly as fallback");

                _failureTracker?.RemoveFailedRecord(failureId.Value, originalLanguage);

                // Return English image path directly - no copying needed
                return new ImageResult(existingEnglishPath, false);
            }

            // Generate English header URLs
            var englishHeaderUrls = new List<string>
            {
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{failureId}/header.jpg",
                $"https://cdn.steamstatic.com/steam/apps/{failureId}/header.jpg",
                $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{failureId}/header.jpg"
            };

            int notFoundCount = 0;
            foreach (var url in englishHeaderUrls)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var result = await GetImagePathAsync(cacheKey, uri, "english", failureId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(result.Path))
                    {
                        _failureTracker?.RemoveFailedRecord(failureId.Value, originalLanguage);

                        // Return English image path directly - no copying needed
                        return new ImageResult(result.Path, true);
                    }

                    // Use result.IsNotFound directly instead of _lastErrors lookup
                    // because each URL has a different key in _lastErrors
                    if (result.IsNotFound)
                    {
                        notFoundCount++;
                    }
                }
            }

            if (notFoundCount >= 2)
            {
                // Try English logo URLs as a final fallback
                var englishLogoUrls = new List<string>
                {
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{failureId}/logo.png",
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{failureId}/logo_english.png"
                };

                foreach (var url in englishLogoUrls)
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        var result = await GetImagePathAsync(cacheKey, uri, "english", failureId, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(result.Path))
                        {
                            _failureTracker?.RemoveFailedRecord(failureId.Value, originalLanguage);

                            // Return English image path directly - no copying needed
                            return new ImageResult(result.Path, true);
                        }
                    }
                }
            }

            _failureTracker?.RecordFailedDownload(failureId.Value, "english");

            return null;
        }


        /// <summary>
        /// Downloads an image from a CDN with rate limiting, MIME validation, and comprehensive error handling.
        /// This is the low-level download worker that handles the actual HTTP request and file caching.
        /// </summary>
        /// <param name="cacheKey">The cache key, typically the Steam AppID.</param>
        /// <param name="language">The target language for the image.</param>
        /// <param name="uri">The CDN URL to download from.</param>
        /// <param name="basePath">The base file path for caching (without extension).</param>
        /// <param name="ext">The initial file extension guess (will be replaced based on Content-Type header).</param>
        /// <param name="failureId">Optional AppID for failure tracking.</param>
        /// <param name="cancellationToken">Cancellation token to abort the download operation.</param>
        /// <returns>An <see cref="ImageResult"/> containing the downloaded image path or empty string on failure.</returns>
        /// <remarks>
        /// Download pipeline:
        /// 1. Waits for concurrency semaphore slot (max 4 concurrent downloads by default)
        /// 2. Waits for domain rate limiter token (prevents CDN throttling)
        /// 3. Sends HTTP GET with image Accept headers
        /// 4. Handles 429 Too Many Requests and 403 Forbidden with Retry-After parsing
        /// 5. Validates Content-Type and maps to correct file extension via MimeToExtension
        /// 6. Writes response stream to disk
        /// 7. Validates downloaded file with MIME magic number check (prevents corrupted cache)
        /// 8. Updates failure tracker on success/failure
        /// 9. Handles ObjectDisposedException gracefully during app shutdown or language switch
        /// 10. Distinguishes 404 Not Found (expected for localized images) from other errors
        /// 11. Updates progress counters and reports to subscribers
        /// </remarks>
        private async Task<ImageResult> DownloadAsync(string cacheKey, string language, Uri uri, string basePath, string ext, int? failureId, CancellationToken cancellationToken)
        {
            try
            {
                await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // CRITICAL: Semaphore was disposed during language switch or app shutdown
                // This happens when downloads are in-flight and semaphore gets disposed
                AppLogger.LogDebug($"Concurrency semaphore disposed while waiting for {uri}, aborting download");
                return new ImageResult(string.Empty, false);
            }
            catch (OperationCanceledException ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    AppLogger.LogDebug($"Unexpected cancellation for {uri}: {ex.Message}");
                    if (failureId.HasValue)
                    {
                        _failureTracker?.RecordFailedDownload(failureId.Value, language);
                    }
                }
                return new ImageResult(string.Empty, false);
            }

            try
            {
                bool success = false;
                TimeSpan? retryDelay = null;
                bool rateLimiterAcquired = false;
                try
                {
                    await _rateLimiter.WaitAsync(uri, cancellationToken).ConfigureAwait(false);
                    rateLimiterAcquired = true;
                    AppLogger.LogDebug($"Starting image download for {uri}");
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Add("Accept", "image/webp,image/avif,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                    using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        retryDelay = ParseRetryAfter(response);
                        throw new HttpRequestException($"Failed: {response.StatusCode}", null, response.StatusCode);
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"Failed: {response.StatusCode}", null, response.StatusCode);
                    }

                    var mime = response.Content.Headers.ContentType?.MediaType;
                    if (!string.IsNullOrEmpty(mime) && MimeToExtension.TryGetValue(mime, out var mapped))
                    {
                        ext = mapped;
                    }

                    var path = basePath + ext;
                    await using (var fs = File.Create(path))
                    {
                        await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                    }

                    if (!IsCacheValid(path))
                    {
                        try { File.Delete(path); } catch { }
                        throw new InvalidDataException("Invalid image file");
                    }

                    if (failureId.HasValue)
                    {
                        _failureTracker?.RemoveFailedRecord(failureId.Value, language);
                    }
                    success = true;
                    return new ImageResult(path, true);
                }
                catch (HttpRequestException ex)
                {
                    // Check if it's a 404 - this is expected for many localized images
                    bool isNotFound = ex.StatusCode == System.Net.HttpStatusCode.NotFound;
                    RecordError(uri, isNotFound);

                    if (isNotFound)
                    {
                        AppLogger.LogDebug($"Image not found at {uri} (404) - will try fallback");
                        // Don't record 404 as a failure for tracking purposes
                        success = true;
                        return new ImageResult(string.Empty, false, IsNotFound: true);
                    }
                    else
                    {
                        AppLogger.LogDebug($"HTTP request failed for {uri}: {ex.StatusCode} - {ex.Message}");
                        if (failureId.HasValue)
                        {
                            _failureTracker?.RecordFailedDownload(failureId.Value, language);
                        }
                        return new ImageResult(string.Empty, false);
                    }
                }
                catch (TaskCanceledException ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        AppLogger.LogDebug($"Request timeout for {uri}: {ex.Message}");
                        if (failureId.HasValue)
                        {
                            _failureTracker?.RecordFailedDownload(failureId.Value, language);
                        }
                    }
                    return new ImageResult(string.Empty, false);
                }
                catch (OperationCanceledException ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        AppLogger.LogDebug($"Unexpected cancellation for {uri}: {ex.Message}");
                        if (failureId.HasValue)
                        {
                            _failureTracker?.RecordFailedDownload(failureId.Value, language);
                        }
                    }
                    return new ImageResult(string.Empty, false);
                }
                catch (ObjectDisposedException ex)
                {
                    // CRITICAL: Semaphore (concurrency or rate limiter) was disposed during download
                    // This happens during language switch or app shutdown
                    AppLogger.LogDebug($"Semaphore disposed during download for {uri}: {ex.Message}");
                    return new ImageResult(string.Empty, false);
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Unexpected error downloading {uri}: {ex.GetType().Name} - {ex.Message}");
                    if (failureId.HasValue)
                    {
                        _failureTracker?.RecordFailedDownload(failureId.Value, language);
                    }
                    return new ImageResult(string.Empty, false);
                }
                finally
                {
                    if (rateLimiterAcquired)
                    {
                        try
                        {
                            _rateLimiter.RecordCall(uri, success, retryDelay);
                        }
                        catch (Exception ex)
                        {
                            // CRITICAL: Catch exceptions from RecordCall (especially ObjectDisposedException during language switch)
                            // If semaphore.Release() throws in RecordCall, this prevents crash
                            AppLogger.LogDebug($"Error in RecordCall for {uri}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    _concurrency.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore was disposed (app shutting down or language switch), ignore
                    AppLogger.LogDebug($"Semaphore already disposed for {uri}, skipping release");
                }
                // Note: _inFlight cleanup, _completed increment, and ReportProgress() are now handled
                // in the wrapper task in GetImagePathAsync to avoid a race condition
            }
        }

        /// <summary>
        /// Clears cached images from disk for a specific language or all languages.
        /// Used when user wants to force re-download of images or free up disk space.
        /// </summary>
        /// <param name="language">The language to clear cache for, or null to clear all language caches. Defaults to null (clear all).</param>
        /// <remarks>
        /// If <paramref name="language"/> is null or empty, the entire base cache directory is deleted and recreated.
        /// If <paramref name="language"/> is specified, only that language's subdirectory is deleted.
        /// Exceptions are logged but not thrown to prevent cache clearing failures from crashing the app.
        /// </remarks>
        public void ClearCache(string? language = null)
        {
            try
            {
                if (string.IsNullOrEmpty(language))
                {
                    if (Directory.Exists(_baseCacheDir))
                    {
                        Directory.Delete(_baseCacheDir, true);
                        Directory.CreateDirectory(_baseCacheDir);
                    }
                }
                else
                {
                    var dir = GetCacheDir(language);
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Failed to clear cache for language '{language ?? "all"}': {ex.GetType().Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// Cleanup duplicated English images that were copied to language-specific folders.
        /// This removes files from language folders that are identical to English versions,
        /// saving disk space and ensuring cache consistency.
        /// </summary>
        /// <param name="dryRun">If true, only reports what would be deleted without actually deleting</param>
        /// <returns>Number of duplicated files found (and deleted if not dry run)</returns>
        public int CleanupDuplicatedEnglishImages(bool dryRun = false)
        {
            var languages = new[] { "tchinese", "schinese", "japanese", "korean" };
            var englishDir = GetCacheDir("english");
            int duplicatesFound = 0;
            long spaceReclaimed = 0;

            if (!Directory.Exists(englishDir))
            {
                AppLogger.LogDebug("English cache directory does not exist, nothing to cleanup");
                return 0;
            }

            foreach (var language in languages)
            {
                var languageDir = GetCacheDir(language);
                if (!Directory.Exists(languageDir))
                    continue;

                var languageFiles = Directory.GetFiles(languageDir);
                AppLogger.LogDebug($"Checking {languageFiles.Length} files in {language} folder for duplicates");

                foreach (var languageFile in languageFiles)
                {
                    try
                    {
                        var fileName = Path.GetFileName(languageFile);
                        var englishFile = Path.Combine(englishDir, fileName);

                        // If English version exists
                        if (File.Exists(englishFile))
                        {
                            // Compare file sizes first (faster than content comparison)
                            var languageInfo = new FileInfo(languageFile);
                            var englishInfo = new FileInfo(englishFile);

                            if (languageInfo.Length == englishInfo.Length)
                            {
                                // Compare file content to confirm they're identical
                                var languageBytes = File.ReadAllBytes(languageFile);
                                var englishBytes = File.ReadAllBytes(englishFile);

                                if (languageBytes.SequenceEqual(englishBytes))
                                {
                                    duplicatesFound++;
                                    spaceReclaimed += languageInfo.Length;

                                    if (dryRun)
                                    {
                                        AppLogger.LogDebug($"[DRY RUN] Would delete duplicated English image: {languageFile} ({languageInfo.Length} bytes)");
                                    }
                                    else
                                    {
                                        File.Delete(languageFile);
                                        AppLogger.LogDebug($"Deleted duplicated English image: {languageFile} ({languageInfo.Length} bytes)");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Error processing {languageFile}: {ex.Message}");
                    }
                }
            }

            if (duplicatesFound > 0)
            {
                var spaceMB = spaceReclaimed / (1024.0 * 1024.0);
                if (dryRun)
                {
                    AppLogger.LogDebug($"[DRY RUN] Found {duplicatesFound} duplicated files ({spaceMB:F2} MB that could be reclaimed)");
                }
                else
                {
                    AppLogger.LogDebug($"Cleaned up {duplicatesFound} duplicated files, reclaimed {spaceMB:F2} MB of disk space");
                }
            }
            else
            {
                AppLogger.LogDebug("No duplicated English images found");
            }

            return duplicatesFound;
        }

        /// <summary>
        /// Validates that a cached image file is still valid and usable.
        /// Performs MIME magic number validation and TTL check to prevent corrupted or stale cache usage.
        /// </summary>
        /// <param name="path">The absolute path to the cached image file.</param>
        /// <returns>True if the file is a valid image and hasn't exceeded the cache duration TTL, otherwise false.</returns>
        /// <remarks>
        /// Validation checks performed:
        /// 1. MIME magic number validation via <see cref="ImageValidation.IsValidImage"/> (reads first few bytes to confirm it's a real image)
        /// 2. Cache duration check - compares file's LastWriteTimeUtc against <see cref="_cacheDuration"/>
        ///
        /// As of current implementation, _cacheDuration defaults to TimeSpan.MaxValue, so successfully downloaded images never expire.
        /// This validation still runs to catch corrupted files and provides future flexibility for expiration policies.
        /// </remarks>
        private bool IsCacheValid(string path)
        {
            try
            {
                if (!ImageValidation.IsValidImage(path))
                {
                    return false;
                }

                var info = new FileInfo(path);
                if (DateTime.UtcNow - info.LastWriteTimeUtc > _cacheDuration)
                {
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Failed to validate cache for '{path}': {ex.GetType().Name} - {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Parses the Retry-After HTTP header from a 429 Too Many Requests or 403 Forbidden response.
        /// Used by the rate limiter to respect CDN-imposed back-off delays.
        /// </summary>
        /// <param name="response">The HTTP response message containing the Retry-After header.</param>
        /// <returns>
        /// A TimeSpan indicating how long to wait before retrying, or null if the header is missing or invalid.
        /// Supports both delta-seconds format (e.g., "120") and HTTP-date format (e.g., "Wed, 21 Oct 2015 07:28:00 GMT").
        /// </returns>
        private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
        {
            var retry = response.Headers.RetryAfter;
            if (retry != null)
            {
                if (retry.Delta.HasValue)
                    return retry.Delta;
                if (retry.Date.HasValue)
                {
                    var delay = retry.Date.Value - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                        return delay;
                }
            }
            return null;
        }

        /// <summary>
        /// Notifies all <see cref="ProgressChanged"/> subscribers with current download progress.
        /// Safely invokes each handler in the invocation list with exception isolation.
        /// </summary>
        /// <remarks>
        /// This method is called after every progress counter update (total incremented, completed incremented).
        /// Each handler is invoked individually with try-catch to prevent one failing subscriber from affecting others.
        /// Uses volatile reads to ensure thread-safe access to counters.
        /// </remarks>
        private void ReportProgress()
        {
            var total = Volatile.Read(ref _totalRequests);
            var completed = Volatile.Read(ref _completed);
            var handlers = ProgressChanged;
            if (handlers == null)
                return;
            foreach (Action<int, int> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(completed, total);
                }
                catch { }
            }
        }

        /// <summary>
        /// Checks if we should skip downloading for a specific App ID and language due to recent failures
        /// </summary>
        public bool ShouldSkipDownload(int appId, string language) => _failureTracker?.ShouldSkipDownload(appId, language) ?? false;

        /// <summary>
        /// Records a failed download attempt for specific language
        /// </summary>
        public void RecordFailedDownload(int appId, string language) => _failureTracker?.RecordFailedDownload(appId, language);

        /// <summary>
        /// Removes a failed download record for specific language (called when download succeeds)
        /// </summary>
        public void RemoveFailedRecord(int appId, string language) => _failureTracker?.RemoveFailedRecord(appId, language);

        public void Dispose()
        {
            if (_disposeHttpClient)
            {
                _http.Dispose();
            }
            _concurrency?.Dispose();
            _rateLimiter.Dispose();
        }
    }
}
