using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using AnSAM.Services;
using Xunit;

public class IconCacheTests
{
    public static IEnumerable<object[]> ValidHeaders()
    {
        yield return new object[] { "bmp", new byte[] { 0x42, 0x4D, 0, 0, 0, 0 } };
        yield return new object[] { "ico", new byte[] { 0x00, 0x00, 0x01, 0x00, 0, 0 } };
        yield return new object[] { "avif", new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 } };
    }

    [Theory]
    [MemberData(nameof(ValidHeaders))]
    public async Task ValidCachedIconIsUsed(string ext, byte[] data)
    {
        var id = Random.Shared.Next(100000, 200000);
        SteamLanguageResolver.OverrideLanguage = "english";
        try
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab", "ImageCache", "english");
            Directory.CreateDirectory(cacheDir);
            foreach (var file in Directory.EnumerateFiles(cacheDir, $"{id}.*"))
            {
                try { File.Delete(file); } catch { }
            }
            var path = Path.Combine(cacheDir, $"{id}.{ext}");
            await File.WriteAllBytesAsync(path, data);
            var uri = new Uri($"http://example.invalid/{id}.{ext}");
            var result = await IconCache.GetIconPathAsync(id, uri);
            Assert.Equal(path, result.Path);
            Assert.False(result.Downloaded);
            try { File.Delete(path); } catch { }
        }
        finally
        {
            SteamLanguageResolver.OverrideLanguage = null;
        }
}

    [Fact]
    public async Task ProgressCountsCachedIcons()
    {
        IconCache.ResetProgress();
        var events = new List<(int completed, int total)>();
        void Handler(int c, int t) => events.Add((c, t));
        IconCache.ProgressChanged += Handler;
        try
        {
            SteamLanguageResolver.OverrideLanguage = "english";
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab", "ImageCache", "english");
            Directory.CreateDirectory(cacheDir);

            var cachedId = Random.Shared.Next(200001, 300000);
            foreach (var file in Directory.EnumerateFiles(cacheDir, $"{cachedId}.*"))
            {
                try { File.Delete(file); } catch { }
            }
            var cachedPath = Path.Combine(cacheDir, $"{cachedId}.png");
            await File.WriteAllBytesAsync(cachedPath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 });
            await IconCache.GetIconPathAsync(cachedId, new Uri($"http://example.invalid/{cachedId}.png"));

            int port;
            using (var l = new TcpListener(IPAddress.Loopback, 0))
            {
                l.Start();
                port = ((IPEndPoint)l.LocalEndpoint).Port;
            }
            var prefix = $"http://localhost:{port}/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            var serverTask = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 };
                ctx.Response.ContentType = "image/png";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
                ctx.Response.Close();
                listener.Stop();
            });

            var downloadId = cachedId + 1;
            foreach (var file in Directory.EnumerateFiles(cacheDir, $"{downloadId}.*"))
            {
                try { File.Delete(file); } catch { }
            }
            var result = await IconCache.GetIconPathAsync(downloadId, new Uri(prefix + "icon.png"));
            await serverTask;
            Assert.True(result.Downloaded);
        }
        finally
        {
            IconCache.ProgressChanged -= Handler;
            SteamLanguageResolver.OverrideLanguage = null;
        }

        Assert.Equal(new (int completed, int total)[] { (1, 1), (1, 2), (2, 2) }, events.ToArray());
    }

    [Fact]
    public async Task InvalidDownloadIsIgnored()
    {
        SteamLanguageResolver.OverrideLanguage = "english";
        try
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab", "ImageCache", "english");
            Directory.CreateDirectory(cacheDir);

            var id = Random.Shared.Next(300001, 400000);
            foreach (var file in Directory.EnumerateFiles(cacheDir, $"{id}.*"))
            {
                try { File.Delete(file); } catch { }
            }

            int port;
            using (var l = new TcpListener(IPAddress.Loopback, 0))
            {
                l.Start();
                port = ((IPEndPoint)l.LocalEndpoint).Port;
            }
            var prefix = $"http://localhost:{port}/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            var serverTask = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                var data = new byte[] { 1, 2, 3, 4 };
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
                ctx.Response.Close();
                listener.Stop();
            });

            var result = await IconCache.GetIconPathAsync(id, new[] { prefix + "bad.png" });
            await serverTask;

            Assert.Null(result);
            Assert.False(File.Exists(Path.Combine(cacheDir, $"{id}.png")));
        }
        finally
        {
            SteamLanguageResolver.OverrideLanguage = null;
        }
    }
}
