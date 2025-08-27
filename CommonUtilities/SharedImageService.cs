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

        private static readonly JsonSerializerOptions JsonOptions =
            new() { TypeInfoResolver = StoreApiJsonContext.Default };

        public event Action<int, string?>? ImageDownloadCompleted;

        public SharedImageService(HttpClient httpClient, GameImageCache? cache = null, bool disposeHttpClient = false)
        {
            _httpClient = httpClient;
            _disposeHttpClient = disposeHttpClient;

            if (cache != null)
            {
                _cache = cache;
            }
            else
            {
                var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AchievoLab", "ImageCache");
                _cache = new GameImageCache(baseDir, new ImageFailureTrackingService());
            }
        }

        public Task SetLanguage(string language)
        {
            if (_currentLanguage != language)
            {
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
            }
            return Task.CompletedTask;
        }

        public string GetCurrentLanguage() => _currentLanguage;

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

            // Check the on-disk cache before making any network calls
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
            var result = await _cache.GetImagePathAsync(appId.ToString(), languageUrls, language, appId, _cts.Token);
            if (!string.IsNullOrEmpty(result?.Path) && IsFreshImage(result.Value.Path))
            {
                _imageCache[cacheKey] = result.Value.Path;
                if (result.Value.Downloaded)
                {
                    TriggerImageDownloadCompletedEvent(appId, result.Value.Path);
                }
                return result.Value.Path;
            }

            if (!string.IsNullOrEmpty(result?.Path))
            {
                try { File.Delete(result.Value.Path); } catch { }
            }

            var noIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "no_icon.png");
            if (File.Exists(noIconPath))
            {
                return noIconPath;
            }

            TriggerImageDownloadCompletedEvent(appId, null);
            return null;
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

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
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
