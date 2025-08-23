using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using CommonUtilities;

namespace MyOwnGames.Services
{
    public class GameImageService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly GameImageCache _cache;
        private readonly ImageFailureTrackingService _failureTracker;
        private readonly Dictionary<string, string> _imageCache = new();
        private string _currentLanguage = "english";

        public event Action<int, string?>? ImageDownloadCompleted;

        public GameImageService()
        {
            // Initialize the HTTP client used for downloading images.
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MyOwnGames/1.0");

            // Configure the local cache for storing image files.
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "ImageCache");
            _failureTracker = new ImageFailureTrackingService();
            _cache = new GameImageCache(baseDir, _failureTracker);
        }

        public void SetLanguage(string language)
        {
            if (_currentLanguage != language)
            {
                _currentLanguage = language;
                _imageCache.Clear();
            }
        }

        public string GetCurrentLanguage() => _currentLanguage;

        public async Task<string?> GetGameImageAsync(int appId, string? language = null)
        {
            language ??= _currentLanguage;
            var cacheKey = $"{appId}_{language}";

            if (_imageCache.TryGetValue(cacheKey, out var cached))
            {
                if (IsValidImage(cached))
                {
                    return cached;
                }

                try { File.Delete(cached); } catch { }
                _imageCache.Remove(cacheKey);
                _failureTracker.RecordFailedDownload(appId, language);
            }

            var languageSpecificUrlMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var englishUrlMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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

            var header = await GetHeaderImageFromStoreApiAsync(appId, language);
            if (!string.IsNullOrEmpty(header))
            {
                AddUrl(languageSpecificUrlMap, header);
                if (string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
                {
                    AddUrl(englishUrlMap, header);
                }
            }

            if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                var englishHeader = await GetHeaderImageFromStoreApiAsync(appId, "english");
                if (!string.IsNullOrEmpty(englishHeader))
                {
                    AddUrl(englishUrlMap, englishHeader);
                }
            }

            // Fastly CDN (will be blocked if access too many times)
            //if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            //{
            //    AddUrl(languageSpecificUrlMap, $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg");
            //}
            //AddUrl(englishUrlMap, $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");

            // Cloudflare CDN
            AddUrl(languageSpecificUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg");
            AddUrl(englishUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");

            // Steam CDN
            AddUrl(languageSpecificUrlMap, $"https://cdn.steamstatic.com/steam/apps/{appId}/header_{language}.jpg");
            AddUrl(englishUrlMap, $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg");

            // Akamai CDN
            AddUrl(languageSpecificUrlMap, $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg");
            AddUrl(englishUrlMap, $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");

            // Additional assets
            AddUrl(languageSpecificUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/logo_{language}.png");
            AddUrl(englishUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/logo.png");
            var languageUrls = RoundRobin(languageSpecificUrlMap);
            var englishUrls = RoundRobin(englishUrlMap);

            var result = await _cache.GetImagePathAsync(appId.ToString(), languageUrls, language, appId);
            if (!string.IsNullOrEmpty(result?.Path) && IsValidImage(result.Value.Path))
            {
                _imageCache[cacheKey] = result.Value.Path;
                if (result.Value.Downloaded)
                {
                    ImageDownloadCompleted?.Invoke(appId, result.Value.Path);
                }
                return result.Value.Path;
            }

            if (!string.IsNullOrEmpty(result?.Path))
            {
                try { File.Delete(result.Value.Path); } catch { }
            }

            result = await _cache.GetImagePathAsync(appId.ToString(), englishUrls, language, appId);
            if (!string.IsNullOrEmpty(result?.Path) && IsValidImage(result.Value.Path))
            {
                _imageCache[cacheKey] = result.Value.Path;
                if (result.Value.Downloaded)
                {
                    ImageDownloadCompleted?.Invoke(appId, result.Value.Path);
                }
                return result.Value.Path;
            }

            if (!string.IsNullOrEmpty(result?.Path))
            {
                try { File.Delete(result.Value.Path); } catch { }
                _failureTracker.RecordFailedDownload(appId, language);
            }

            var noIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "no_icon.png");
            if (File.Exists(noIconPath))
            {
                return noIconPath;
            }

            ImageDownloadCompleted?.Invoke(appId, null);
            return null;
        }

        private async Task<string?> GetHeaderImageFromStoreApiAsync(int appId, string language)
        {
            try
            {
                var storeApiUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={language}";
                using var response = await _httpClient.GetAsync(storeApiUrl);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var storeData = JsonSerializer.Deserialize<Dictionary<string, StoreApiResponse>>(jsonContent, options);

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
            }
        }

        public void ClearGeneralCache() => ClearCache();

        public void Dispose()
        {
            _httpClient.Dispose();
            _imageCache.Clear();
        }

        private static bool IsValidImage(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    return false;
                }

                if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(30))
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
    }

    internal class StoreApiResponse
    {
        public bool Success { get; set; }
        public StoreApiData? Data { get; set; }
    }

    internal class StoreApiData
    {
        [JsonPropertyName("header_image")]
        public string? HeaderImage { get; set; }
    }
}
