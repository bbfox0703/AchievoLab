using System;
using System.Collections.Concurrent;
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
        private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnSAM", "appcache");
        private static readonly HttpClient Http = new();
        private static readonly SemaphoreSlim Concurrency = new(4);
        private static readonly ConcurrentDictionary<string, Task<string>> InFlight = new();

        private static int _totalRequests;
        private static int _completed;

        /// <summary>
        /// Raised whenever icon download progress changes. Parameters are the
        /// number of completed downloads and the total number of requested icons.
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
        public static Task<string> GetIconPathAsync(int id, Uri uri)
        {
            Directory.CreateDirectory(CacheDir);

            var ext = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".jpg";
            }

            var path = Path.Combine(CacheDir, $"{id}{ext}");
            Interlocked.Increment(ref _totalRequests);
            if (File.Exists(path))
            {
                Interlocked.Increment(ref _completed);
                ReportProgress();
                return Task.FromResult(path);
            }

            return InFlight.GetOrAdd(path, _ => DownloadAsync(uri, path));
        }

        private static async Task<string> DownloadAsync(Uri uri, string path)
        {
            await Concurrency.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(path))
                {
#if DEBUG
                    Debug.WriteLine($"Downloading icon {uri} -> {path}");
#endif
                    using var response = await Http.GetAsync(uri).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var fs = File.Create(path);
                    await response.Content.CopyToAsync(fs).ConfigureAwait(false);
#if DEBUG
                    Debug.WriteLine($"Icon downloaded to {path}");
#endif
                }

                return path;
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
                InFlight.TryRemove(path, out _);
                Interlocked.Increment(ref _completed);
                ReportProgress();
            }
        }

        private static void ReportProgress()
        {
            var total = Volatile.Read(ref _totalRequests);
            var completed = Volatile.Read(ref _completed);
            ProgressChanged?.Invoke(completed, total);
        }
    }
}
