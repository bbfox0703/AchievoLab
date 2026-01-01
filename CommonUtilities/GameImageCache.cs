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
        public readonly record struct ImageResult(string Path, bool Downloaded);

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
            _cacheDuration = cacheDuration ?? TimeSpan.FromDays(30);

            _rateLimiter = new DomainRateLimiter(maxConcurrentRequestsPerDomain, tokenBucketCapacity, fillRatePerSecond, initialTokens ?? tokenBucketCapacity, baseDomainDelay, jitterSeconds);
            _http = httpClient ?? HttpClientProvider.Shared;
            _disposeHttpClient = disposeHttpClient && httpClient != null;
        }

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

        public void ResetProgress()
        {
            Interlocked.Exchange(ref _totalRequests, 0);
            Interlocked.Exchange(ref _completed, 0);
            ReportProgress();
        }

        public (int completed, int total) GetProgress()
        {
            var total = Volatile.Read(ref _totalRequests);
            var completed = Volatile.Read(ref _completed);
            return (completed, total);
        }

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

        public Uri? TryGetCachedUri(string cacheKey, string language = "english", bool checkEnglishFallback = true)
        {
            var path = TryGetCachedPath(cacheKey, language, checkEnglishFallback);
            if (path != null && Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                return uri;
            }
            return null;
        }

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

            return _inFlight.GetOrAdd(basePath, _ =>
            {
                Interlocked.Increment(ref _totalRequests);
                ReportProgress();
                return DownloadAsync(cacheKey, language, uri, basePath, ext, failureId, cancellationToken);
            });
        }

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

                    // Check if this was a 404 (we can detect this by checking the last error)
                    if (IsRecentNotFoundError(uri))
                    {
                        notFoundCount++;
                        DebugLogger.LogDebug($"404 count for {cacheKey} in {language}: {notFoundCount}/{totalUrls}");

                        // After 2 CDN failures, try English fallback if we're not already on English
                        if (tryEnglishFallback && notFoundCount >= 2 && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
                        {
                            DebugLogger.LogDebug($"Switching to English fallback for {cacheKey} after {notFoundCount} 404s");
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
                DebugLogger.LogDebug($"Trying English fallback for {cacheKey} after {notFoundCount} 404s");
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
                    DebugLogger.LogDebug($"All {totalUrls} URLs failed for {cacheKey} in {language} with no 404s, trying English fallback as last resort");
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

        private void RecordError(Uri uri, bool wasNotFound)
        {
            var key = uri.ToString();
            _lastErrors[key] = (DateTime.UtcNow, wasNotFound);
        }

        private async Task<ImageResult?> TryEnglishFallbackAsync(string cacheKey, string originalLanguage, int? failureId, CancellationToken cancellationToken)
        {
            DebugLogger.LogDebug($"Attempting English fallback for {cacheKey} (original: {originalLanguage})");

            if (!failureId.HasValue)
            {
                return null;
            }

            // First, check if there's already a valid English cached image
            var existingEnglishPath = TryGetCachedPath(cacheKey, "english", checkEnglishFallback: false);
            if (!string.IsNullOrEmpty(existingEnglishPath) && IsCacheValid(existingEnglishPath))
            {
                DebugLogger.LogDebug($"Found existing English cached image for {cacheKey}, using directly as fallback");

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

                    if (IsRecentNotFoundError(uri))
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


        private async Task<ImageResult> DownloadAsync(string cacheKey, string language, Uri uri, string basePath, string ext, int? failureId, CancellationToken cancellationToken)
        {
            try
            {
                await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    DebugLogger.LogDebug($"Unexpected cancellation for {uri}: {ex.Message}");
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
                    DebugLogger.LogDebug($"Starting image download for {uri}");
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Add("Accept", "image/webp,image/avif,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                    using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        retryDelay = ParseRetryAfter(response);
                        throw new HttpRequestException($"Failed: {response.StatusCode}");
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"Failed: {response.StatusCode}");
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
                    bool isNotFound = ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase);
                    RecordError(uri, isNotFound);

                    if (isNotFound)
                    {
                        DebugLogger.LogDebug($"Image not found at {uri} (404) - will try fallback");
                        // Don't record 404 as a failure for tracking purposes
                        success = true;
                        return new ImageResult(string.Empty, false);
                    }
                    else
                    {
                        DebugLogger.LogDebug($"HTTP request failed for {uri}: {ex.Message}");
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
                        DebugLogger.LogDebug($"Request timeout for {uri}: {ex.Message}");
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
                        DebugLogger.LogDebug($"Unexpected cancellation for {uri}: {ex.Message}");
                        if (failureId.HasValue)
                        {
                            _failureTracker?.RecordFailedDownload(failureId.Value, language);
                        }
                    }
                    return new ImageResult(string.Empty, false);
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Unexpected error downloading {uri}: {ex.GetType().Name} - {ex.Message}");
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
                        _rateLimiter.RecordCall(uri, success, retryDelay);
                    }
                }
            }
            finally
            {
                _concurrency.Release();
                _inFlight.TryRemove(basePath, out _);
                Interlocked.Increment(ref _completed);
                ReportProgress();
            }
        }

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
            catch { }
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
            var languages = new[] { "tchinese", "schinese", "japanese", "korean", "koreana" };
            var englishDir = GetCacheDir("english");
            int duplicatesFound = 0;
            long spaceReclaimed = 0;

            if (!Directory.Exists(englishDir))
            {
                DebugLogger.LogDebug("English cache directory does not exist, nothing to cleanup");
                return 0;
            }

            foreach (var language in languages)
            {
                var languageDir = GetCacheDir(language);
                if (!Directory.Exists(languageDir))
                    continue;

                var languageFiles = Directory.GetFiles(languageDir);
                DebugLogger.LogDebug($"Checking {languageFiles.Length} files in {language} folder for duplicates");

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
                                        DebugLogger.LogDebug($"[DRY RUN] Would delete duplicated English image: {languageFile} ({languageInfo.Length} bytes)");
                                    }
                                    else
                                    {
                                        File.Delete(languageFile);
                                        DebugLogger.LogDebug($"Deleted duplicated English image: {languageFile} ({languageInfo.Length} bytes)");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogDebug($"Error processing {languageFile}: {ex.Message}");
                    }
                }
            }

            if (duplicatesFound > 0)
            {
                var spaceMB = spaceReclaimed / (1024.0 * 1024.0);
                if (dryRun)
                {
                    DebugLogger.LogDebug($"[DRY RUN] Found {duplicatesFound} duplicated files ({spaceMB:F2} MB that could be reclaimed)");
                }
                else
                {
                    DebugLogger.LogDebug($"Cleaned up {duplicatesFound} duplicated files, reclaimed {spaceMB:F2} MB of disk space");
                }
            }
            else
            {
                DebugLogger.LogDebug("No duplicated English images found");
            }

            return duplicatesFound;
        }

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
            catch { }
            return false;
        }

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
        }
    }
}
