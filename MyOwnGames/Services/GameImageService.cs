using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CommonUtilities;

namespace MyOwnGames.Services
{
    public class GameImageService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly GameImageCache _cache;
        private readonly Dictionary<string, string> _imageCache = new();
        private string _currentLanguage = "english";

        public event Action<int, string?>? ImageDownloadCompleted;

        public GameImageService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MyOwnGames/1.0");

            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "ImageCache");
            _cache = new GameImageCache(baseDir, new ImageFailureTrackingService());
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
                return cached;
            }

            var urls = new List<string>();
            var header = await GetHeaderImageFromStoreApiAsync(appId, language);
            if (!string.IsNullOrEmpty(header))
            {
                urls.Add(header);
            }
            urls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");
            urls.Add($"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg");
            urls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_sm_120.jpg");
            urls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/logo.png");
            urls.Add($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");

            var result = await _cache.GetImagePathAsync(appId.ToString(), urls, language, appId);
            if (!string.IsNullOrEmpty(result?.Path))
            {
                _imageCache[cacheKey] = result.Value.Path;
                if (result.Value.Downloaded)
                {
                    ImageDownloadCompleted?.Invoke(appId, result.Value.Path);
                }
                return result.Value.Path;
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
                if (response.IsSuccessStatusCode)
                {
                    var jsonContent = await response.Content.ReadAsStringAsync();
                    var start = jsonContent.IndexOf("\"header_image\":\"");
                    if (start >= 0)
                    {
                        start += 16;
                        var end = jsonContent.IndexOf("\"", start);
                        if (end > start)
                        {
                            return jsonContent.Substring(start, end - start);
                        }
                    }
                }
            }
            catch { }
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
    }
}
