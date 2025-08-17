using System;
using System.Collections.Concurrent;
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
        private static readonly string CacheDir = Path.Combine(AppContext.BaseDirectory, "appcache");
        private static readonly HttpClient Http = new();
        private static readonly SemaphoreSlim Concurrency = new(4);
        private static readonly ConcurrentDictionary<string, Task<string>> InFlight = new();

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
            if (File.Exists(path))
            {
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
                    using var response = await Http.GetAsync(uri).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var fs = File.Create(path);
                    await response.Content.CopyToAsync(fs).ConfigureAwait(false);
                }

                return path;
            }
            finally
            {
                Concurrency.Release();
                InFlight.TryRemove(path, out _);
            }
        }
    }
}
