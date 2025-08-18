using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AnSAM.Services
{
    /// <summary>
    /// Downloads and caches game cover images under the <c>appcache</c> directory.
    /// Requests are queued and limited to a small number of concurrent downloads.
    /// </summary>
    public static class IconCache
    {
        public readonly record struct IconPathResult(string Path, bool Downloaded);
        private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnSAM", "appcache");
        private static readonly HttpClient Http = new();
        private static readonly SemaphoreSlim Concurrency = new(4);
        private static readonly ConcurrentDictionary<string, Task<IconPathResult>> InFlight = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(30);
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

        private static int _totalRequests;
        private static int _completed;

        /// <summary>
        /// Raised whenever icon download progress changes. Parameters are the
        /// number of completed downloads and the total number of initiated downloads.
        /// </summary>
        public static event Action<int, int>? ProgressChanged;

        /// <summary>
        /// Resets the progress counters so that a new batch of downloads can be
        /// tracked from zero.
        /// </summary>
        public static void ResetProgress()
        {
            Interlocked.Exchange(ref _totalRequests, 0);
            Interlocked.Exchange(ref _completed, 0);
            ReportProgress();
        }

        /// <summary>
        /// Returns a local file path for the provided cover URI, downloading it if necessary.
        /// </summary>
        /// <param name="id">Steam application identifier used to name the file.</param>
        /// <param name="uri">Remote URI for the cover image.</param>
        public static Task<IconPathResult> GetIconPathAsync(int id, Uri uri)
        {
            Directory.CreateDirectory(CacheDir);

            var basePath = Path.Combine(CacheDir, id.ToString());

            if (InFlight.TryGetValue(basePath, out var existing))
            {
                return existing;
            }

            foreach (var candidateExt in new HashSet<string>(MimeToExtension.Values))
            {
                var path = basePath + candidateExt;
                if (File.Exists(path))
                {
                    if (IsCacheValid(path))
                    {
#if DEBUG
                        Debug.WriteLine($"Using cached icon for {id} at {path}");
#endif
                        return Task.FromResult(new IconPathResult(path, false));
                    }

                    try { File.Delete(path); } catch { }
                }
            }

            var ext = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".jpg";
            }

            return InFlight.GetOrAdd(basePath, _ =>
            {
                Interlocked.Increment(ref _totalRequests);
                ReportProgress();
                return DownloadAsync(uri, basePath, ext);
            });
        }

        public static async Task<IconPathResult?> GetIconPathAsync(int id, IEnumerable<string> uris)
        {
            foreach (var url in uris)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    try
                    {
                        return await GetIconPathAsync(id, uri).ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex)
                    {
#if DEBUG
                        Debug.WriteLine($"Icon download failed for {id}: {ex.Message}");
#endif
                    }
                }
            }

            return null;
        }

        private static async Task<IconPathResult> DownloadAsync(Uri uri, string basePath, string defaultExt)
        {
            await Concurrency.WaitAsync().ConfigureAwait(false);
            try
            {
                using var response = await Http.GetAsync(uri).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                var ext = defaultExt;
                if (!string.IsNullOrWhiteSpace(mediaType) && MimeToExtension.TryGetValue(mediaType, out var mapped))
                {
                    ext = mapped;
                }

                var path = basePath + ext;
#if DEBUG
                Debug.WriteLine($"Downloading icon {uri} -> {path}");
#endif
                await using (var fs = File.Create(path))
                {
                    await response.Content.CopyToAsync(fs).ConfigureAwait(false);
                }

                if (!IsCacheValid(path))
                {
                    try { File.Delete(path); } catch { }
                    throw new InvalidDataException("Invalid image file");
                }

                return new IconPathResult(path, true);
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"Icon download failed: {ex.Message}");
#endif
                throw;
            }
            finally
            {
                Concurrency.Release();
                InFlight.TryRemove(basePath, out _);
                Interlocked.Increment(ref _completed);
                ReportProgress();
            }
        }

        private static bool IsCacheValid(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    return false;
                }

                if (DateTime.UtcNow - info.LastWriteTimeUtc > CacheDuration)
                {
                    return false;
                }

                Span<byte> header = stackalloc byte[12];
                using var fs = File.OpenRead(path);
                int read = fs.Read(header);
                if (read >= 4)
                {
                    if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                    {
                        return true; // PNG
                    }
                    if (header[0] == 0xFF && header[1] == 0xD8)
                    {
                        return true; // JPEG
                    }
                    if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                    {
                        return true; // GIF
                    }
                    if (header[0] == 0x42 && header[1] == 0x4D)
                    {
                        return true; // BMP
                    }
                    if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00)
                    {
                        return true; // ICO
                    }
                    if (read >= 12 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70 &&
                        header[8] == 0x61 && header[9] == 0x76 && header[10] == 0x69 && header[11] == 0x66)
                    {
                        return true; // AVIF
                    }
                    if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                        header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                    {
                        return true; // WEBP
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void ReportProgress()
        {
            var total = Volatile.Read(ref _totalRequests);
            var completed = Volatile.Read(ref _completed);
            ProgressChanged?.Invoke(completed, total);
        }
    }
}
