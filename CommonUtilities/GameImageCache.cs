using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CommonUtilities
{
    /// <summary>
    /// Shared image cache that downloads and stores images on disk with
    /// MIME validation, cache expiration and optional failure tracking.
    /// </summary>
    public class GameImageCache
    {
        public readonly record struct ImageResult(string Path, bool Downloaded);

        private readonly string _baseCacheDir;
        private readonly HttpClient _http = new();
        private readonly SemaphoreSlim _concurrency;
        private readonly ConcurrentDictionary<string, Task<ImageResult>> _inFlight = new();
        private readonly TimeSpan _cacheDuration;
        private readonly ImageFailureTrackingService? _failureTracker;
        private readonly DomainRateLimiter _rateLimiter = new();

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
            TimeSpan? cacheDuration = null)
        {
            _baseCacheDir = baseCacheDir;
            Directory.CreateDirectory(_baseCacheDir);
            _failureTracker = failureTracker;
            _concurrency = new SemaphoreSlim(maxConcurrency);
            _cacheDuration = cacheDuration ?? TimeSpan.FromDays(30);
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

        public Task<ImageResult> GetImagePathAsync(string cacheKey, Uri uri, string language = "english", int? failureId = null)
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
                return DownloadAsync(cacheKey, language, uri, basePath, ext, failureId);
            });
        }

        public async Task<ImageResult?> GetImagePathAsync(string cacheKey, IEnumerable<string> uris, string language = "english", int? failureId = null)
        {
            foreach (var url in uris)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var result = await GetImagePathAsync(cacheKey, uri, language, failureId).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(result.Path))
                    {
                        return result;
                    }
                }
            }
            return null;
        }

        private async Task<ImageResult> DownloadAsync(string cacheKey, string language, Uri uri, string basePath, string ext, int? failureId)
        {
            await _concurrency.WaitAsync().ConfigureAwait(false);
            await _rateLimiter.WaitAsync(uri).ConfigureAwait(false);
            try
            {
                using var response = await _http.GetAsync(uri).ConfigureAwait(false);
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
                    await response.Content.CopyToAsync(fs).ConfigureAwait(false);
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
                return new ImageResult(path, true);
            }
            catch
            {
                if (failureId.HasValue)
                {
                    _failureTracker?.RecordFailedDownload(failureId.Value, language);
                }
                return new ImageResult(string.Empty, false);
            }
            finally
            {
                _rateLimiter.RecordCall(uri);
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
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    return false;
                }

                if (DateTime.UtcNow - info.LastWriteTimeUtc > _cacheDuration)
                {
                    return false;
                }

                Span<byte> header = stackalloc byte[12];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int read = fs.Read(header);
                if (read >= 4)
                {
                    if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                        return true; // PNG
                    if (header[0] == 0xFF && header[1] == 0xD8)
                        return true; // JPEG
                    if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                        return true; // GIF
                    if (header[0] == 0x42 && header[1] == 0x4D)
                        return true; // BMP
                    if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00)
                        return true; // ICO
                    if (read >= 12 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70 &&
                        header[8] == 0x61 && header[9] == 0x76 && header[10] == 0x69 && header[11] == 0x66)
                        return true; // AVIF
                    if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                        header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                        return true; // WEBP
                }
            }
            catch { }
            return false;
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
    }
}
