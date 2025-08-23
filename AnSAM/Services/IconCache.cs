using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommonUtilities;

namespace AnSAM.Services
{
    /// <summary>
    /// Thin wrapper around <see cref="GameImageCache"/> that preserves the
    /// public API previously exposed by AnSAM.
    /// </summary>
    public static class IconCache
    {
        private static readonly GameImageCache Cache;
        static IconCache()
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "ImageCache");
            try
            {
                Directory.CreateDirectory(baseDir);
                var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AchievoLab", "appcache");
                if (Directory.Exists(oldDir))
                {
                    var englishDir = Path.Combine(baseDir, "english");
                    Directory.CreateDirectory(englishDir);
                    foreach (var file in Directory.EnumerateFiles(oldDir))
                    {
                        try
                        {
                            var dest = Path.Combine(englishDir, Path.GetFileName(file));
                            File.Move(file, dest, true);
                        }
                        catch { }
                    }
                    try { Directory.Delete(oldDir, true); } catch { }
                }
            }
            catch { }

            Cache = new GameImageCache(baseDir, new ImageFailureTrackingService());
        }

        public static event Action<int, int>? ProgressChanged
        {
            add { Cache.ProgressChanged += value; }
            remove { Cache.ProgressChanged -= value; }
        }

        public static void ResetProgress() => Cache.ResetProgress();
        public static (int completed, int total) GetProgress() => Cache.GetProgress();

        public static Task<GameImageCache.ImageResult> GetIconPathAsync(int id, Uri uri, string? language = null)
            => Cache.GetImagePathAsync(id.ToString(), uri, language ?? SteamLanguageResolver.GetSteamLanguage(), id);

        public static Task<GameImageCache.ImageResult?> GetIconPathAsync(int id, IEnumerable<string> uris, string? language = null)
            => Cache.GetImagePathAsync(id.ToString(), uris, language ?? SteamLanguageResolver.GetSteamLanguage(), id);

        public static string? TryGetCachedPath(int id, string? language = null, bool checkEnglishFallback = true)
            => Cache.TryGetCachedPath(id.ToString(), language ?? SteamLanguageResolver.GetSteamLanguage(), checkEnglishFallback);

        public static Uri? TryGetCachedIconUri(int id, string? language = null, bool checkEnglishFallback = true)
            => Cache.TryGetCachedUri(id.ToString(), language ?? SteamLanguageResolver.GetSteamLanguage(), checkEnglishFallback);
    }
}
