using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;
using System.Diagnostics;
using AnSAM.Services;

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
        /// Represents a parsed game entry from the SAM game list.
        /// </summary>
        public readonly record struct GameInfo(int Id, string Name, string Type);

        /// <summary>
        /// Parsed list of games from the downloaded XML.
        /// </summary>
        public static IReadOnlyList<GameInfo> Games { get; private set; } = Array.Empty<GameInfo>();

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
                DebugLogger.LogDebug($"Using cached game list at {cachePath}");
#endif
                ValidateAndParse(cached);
                ReportProgress(100);
                return cached;
            }

            ReportStatus("Downloading game list...");
#if DEBUG
            DebugLogger.LogDebug($"Downloading game list from {GameListUrl} to {cachePath}");
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

            var tempPath = cachePath + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, data).ConfigureAwait(false);
                if (File.Exists(cachePath))
                {
                    File.Replace(tempPath, cachePath, null);
                }
                else
                {
                    File.Move(tempPath, cachePath);
                }
#if DEBUG
                DebugLogger.LogDebug($"Game list saved to {cachePath}");
#endif
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }

                throw;
            }
            ReportStatus("Game list downloaded.");
            ReportProgress(100);
            return data;
        }

        /// <summary>
        /// Attempts to load and parse the cached game list if it is still fresh.
        /// </summary>
        /// <param name="baseDir">Directory containing the cached file.</param>
        /// <returns>True if a recent cache was loaded and parsed.</returns>
        public static bool TryLoadCache(string baseDir)
        {
            var cachePath = Path.Combine(baseDir, CacheFileName);
            try
            {
                if (!TryGetValidCache(cachePath, out var data))
                {
                    Games = Array.Empty<GameInfo>();
                    return false;
                }

                ValidateAndParse(data);
                ReportStatus("Using cached game list...");
                ReportProgress(100);
                return true;
            }
            catch
            {
                Games = Array.Empty<GameInfo>();
                return false;
            }
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
            reader.MoveToContent();

            var parsed = new List<GameInfo>();
            if (reader.ReadToDescendant("game"))
            {
                while (reader.NodeType == XmlNodeType.Element && reader.Name == "game")
                {
                    var raw = reader.ReadElementContentAsString().Trim();
                    if (string.IsNullOrEmpty(raw))
                    {
#if DEBUG
                        DebugLogger.LogDebug("Skipping empty <game> entry in XML");
#endif
                        reader.MoveToContent();
                        continue;
                    }

                    if (int.TryParse(raw, out int id) && id > 0)
                    {
                        parsed.Add(new GameInfo(id, string.Empty, string.Empty));
                    }
#if DEBUG
                    else
                    {
                        DebugLogger.LogDebug($"Invalid game id '{raw}' in XML");
                    }
#endif
                    reader.MoveToContent();
                }
            }

            Games = parsed;
#if DEBUG
            DebugLogger.LogDebug($"Parsed {Games.Count} games from XML");
            if (Games.Count > 0)
            {
                var sample = string.Join(", ", Games.Take(20).Select(g => g.Id));
                DebugLogger.LogDebug($"Sample game IDs: {sample}{(Games.Count > 20 ? ", ..." : string.Empty)}");
            }
#endif
        }

        private static void ReportProgress(double value) => ProgressChanged?.Invoke(value);

        private static void ReportStatus(string message) => StatusChanged?.Invoke(message);
    }
}

