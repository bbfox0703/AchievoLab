using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Diagnostics;

namespace AnSAM.Services
{
    /// <summary>
    /// Downloads and caches the global game list used by SAM.
    /// </summary>
    public static class GameListService
    {
        private const string GameListUrl = "https://gib.me/sam/games.xml";
        private const int MaxSizeBytes = 4 * 1024 * 1024; // 4MB
        private const string CacheFileName = "games.xml";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Raised with a value from 0 to 100 representing download progress.
        /// </summary>
        public static event Action<double>? ProgressChanged;

        /// <summary>
        /// Raised with a textual status update suitable for a status bar.
        /// </summary>
        public static event Action<string>? StatusChanged;

        /// <summary>
        /// Parsed list of (id, type) pairs from the downloaded XML.
        /// </summary>
        public static IReadOnlyList<(int Id, string Type)> GameTypes { get; private set; } = Array.Empty<(int, string)>();

        /// <summary>
        /// Downloads the game list, applying caching and validation rules.
        /// </summary>
        /// <param name="baseDir">Directory to store cached data.</param>
        /// <param name="http">HttpClient used for downloading.</param>
        public static async Task<byte[]> LoadAsync(string baseDir, HttpClient http)
        {
            Directory.CreateDirectory(baseDir);
            var cachePath = Path.Combine(baseDir, CacheFileName);

            if (TryGetValidCache(cachePath, out var cached))
            {
                ReportStatus("Using cached game list...");
#if DEBUG
                Debug.WriteLine($"Using cached game list at {cachePath}");
#endif
                ValidateAndParse(cached);
                ReportProgress(100);
                return cached;
            }

            ReportStatus("Downloading game list...");
#if DEBUG
            Debug.WriteLine($"Downloading game list from {GameListUrl} to {cachePath}");
#endif
            using var response = await http.GetAsync(GameListUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var network = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var ms = new MemoryStream();

            var buffer = new byte[81920];
            int total = 0;
            int read;
            while ((read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxSizeBytes)
                {
                    throw new InvalidOperationException("Game list exceeds 4 MB limit.");
                }
                ms.Write(buffer, 0, read);
                if (totalBytes > 0)
                {
                    ReportProgress((double)total / totalBytes * 100);
                }
            }

            var data = ms.ToArray();
            ValidateAndParse(data);

            await File.WriteAllBytesAsync(cachePath, data).ConfigureAwait(false);
#if DEBUG
            Debug.WriteLine($"Game list saved to {cachePath}");
#endif
            ReportStatus("Game list downloaded.");
            ReportProgress(100);
            return data;
        }

        private static bool TryGetValidCache(string cachePath, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (!File.Exists(cachePath))
            {
                return false;
            }

            var info = new FileInfo(cachePath);
            if (DateTime.UtcNow - info.LastWriteTimeUtc > CacheDuration)
            {
                return false;
            }

            data = File.ReadAllBytes(cachePath);
            return true;
        }

        private static void ValidateAndParse(byte[] data)
        {
            using var ms = new MemoryStream(data);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(ms, settings);
            var doc = XDocument.Load(reader, LoadOptions.None);

            GameTypes = doc.Root?.Elements("game")
                                .Select(e => (
                                    Id: (int?)e.Attribute("id") ?? 0,
                                    Type: (string?)e.Attribute("type") ?? string.Empty))
                                .Where(t => t.Id > 0)
                                .ToArray() ?? Array.Empty<(int, string)>();
        }

        private static void ReportProgress(double value) => ProgressChanged?.Invoke(value);

        private static void ReportStatus(string message) => StatusChanged?.Invoke(message);
    }
}

