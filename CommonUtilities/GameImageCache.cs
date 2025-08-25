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

        public Task<ImageResult> GetImagePathAsync(string cacheKey, Uri uri, string language = "english", int? failureId = null, CancellationToken cancellationToken = default)
        {
            var cacheDir = GetCacheDir(language);
            var basePath = Path.Combine(cacheDir, cacheKey);

            if (_inFlight.TryGetValue(basePath, out var existing))
            {
                return existing;
            }

            var cached = TryGetCachedPath(cacheKey, language);
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

        public async Task<ImageResult?> GetImagePathAsync(string cacheKey, IEnumerable<string> uris, string language = "english", int? failureId = null, CancellationToken cancellationToken = default)
        {
            int notFoundCount = 0;
            var totalUrls = uris.Count();

            foreach (var url in uris)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var result = await GetImagePathAsync(cacheKey, uri, language, failureId, cancellationToken).ConfigureAwait(false);
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
                        if (notFoundCount >= 2 && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
                        {
                            DebugLogger.LogDebug($"Switching to English fallback for {cacheKey} after {notFoundCount} 404s");
                            return await TryEnglishFallbackAsync(cacheKey, language, failureId, cancellationToken);
                        }
                    }
                }
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
            
            // Generate English URLs
            var englishUrls = new List<string>();
            
            if (failureId.HasValue)
            {
                // Cloudflare CDN
                englishUrls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{failureId}/header.jpg");
                
                // Steam CDN  
                englishUrls.Add($"https://cdn.steamstatic.com/steam/apps/{failureId}/header.jpg");
                
                // Akamai CDN
                englishUrls.Add($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{failureId}/header.jpg");
            }
            
            foreach (var url in englishUrls)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var result = await GetImagePathAsync(cacheKey, uri, "english", failureId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(result.Path))
                    {
                        // Success! Copy to original language folder
                        CopyToOriginalLanguageFolder(result.Path, cacheKey, originalLanguage);

                        if (failureId.HasValue)
                        {
                            _failureTracker?.RemoveFailedRecord(failureId.Value, originalLanguage);
                        }

                        // Create result for original language
                        var originalPath = GetCacheDir(originalLanguage);
                        var finalPath = Path.Combine(originalPath, cacheKey + Path.GetExtension(result.Path));
                        
                        return new ImageResult(finalPath, true);
                    }
                }
            }
            
            return null;
        }

        private void CopyToOriginalLanguageFolder(string englishImagePath, string cacheKey, string originalLanguage)
        {
            try
            {
                var originalDir = GetCacheDir(originalLanguage);
                var extension = Path.GetExtension(englishImagePath);
                var targetPath = Path.Combine(originalDir, cacheKey + extension);
                
                DebugLogger.LogDebug($"Copying English image to {originalLanguage} folder: {targetPath}");
                
                // Copy the file
                File.Copy(englishImagePath, targetPath, overwrite: true);
                
                DebugLogger.LogDebug($"Successfully copied English image for {cacheKey} to both folders");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Failed to copy English image to original language folder: {ex.Message}");
            }
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
