using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using AnSAM.Services;
using System.Xml.Linq;
using System.Linq;
using CommonUtilities;
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
    public void ProgressChangedHandlerExceptionIsSwallowed()
    {
        IconCache.ResetProgress();
        void Handler(int c, int t) => throw new InvalidOperationException();
        IconCache.ProgressChanged += Handler;
        try
        {
            var ex = Record.Exception(() => IconCache.ResetProgress());
            Assert.Null(ex);
        }
        finally
        {
            IconCache.ProgressChanged -= Handler;
        }
    }

    [Fact]
    public async Task InvalidDownloadIsIgnored()
    {
        SteamLanguageResolver.OverrideLanguage = "english";
        var id = Random.Shared.Next(300001, 400000);
        try
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab", "ImageCache", "english");
            Directory.CreateDirectory(cacheDir);
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
            new ImageFailureTrackingService().RemoveFailedRecord(id, "english");
            SteamLanguageResolver.OverrideLanguage = null;
        }
    }

    [Fact]
    public async Task FailedDownloadReturnsEmptyPath()
    {
        SteamLanguageResolver.OverrideLanguage = "english";
        var id = Random.Shared.Next(400001, 500000);
        try
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab", "ImageCache", "english");
            Directory.CreateDirectory(cacheDir);
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
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                listener.Stop();
            });

            var result = await IconCache.GetIconPathAsync(id, new Uri(prefix + "missing.png"));
            await serverTask;

            Assert.Equal(string.Empty, result.Path);
            Assert.False(result.Downloaded);
            Assert.Empty(Directory.EnumerateFiles(cacheDir, $"{id}.*"));
        }
        finally
        {
            new ImageFailureTrackingService().RemoveFailedRecord(id, "english");
            SteamLanguageResolver.OverrideLanguage = null;
        }
    }

    [Fact]
    public async Task SkipsAndRetriesBasedOnFailureLog()
    {
        SteamLanguageResolver.OverrideLanguage = "english";
        var tracker = new ImageFailureTrackingService();
        var id = Random.Shared.Next(500001, 600000);
        try
        {
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab", "ImageCache", "english");
            Directory.CreateDirectory(cacheDir);
            foreach (var file in Directory.EnumerateFiles(cacheDir, $"{id}.*"))
            {
                try { File.Delete(file); } catch { }
            }

            tracker.RecordFailedDownload(id, "english");

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
            var contextTask = listener.GetContextAsync();
            var skipResult = await IconCache.GetIconPathAsync(id, new Uri(prefix + "icon.png"));
            await Task.Delay(200);
            Assert.False(contextTask.IsCompleted);
            listener.Stop();
            Assert.Equal(string.Empty, skipResult.Path);

            var doc = XDocument.Load(tracker.GetXmlFilePath());
            var game = doc.Root?.Elements("Game").FirstOrDefault(g => (int?)g.Attribute("AppId") == id);
            var lang = game?.Elements("Language").FirstOrDefault(l => l.Attribute("Code")?.Value == "english");
            lang?.SetAttributeValue("LastFailed", DateTime.Now.AddDays(-20).ToString("yyyy-MM-dd HH:mm:ss"));
            doc.Save(tracker.GetXmlFilePath());

            using var listener2 = new HttpListener();
            listener2.Prefixes.Add(prefix);
            listener2.Start();
            var serverTask = Task.Run(async () =>
            {
                var ctx = await listener2.GetContextAsync();
                var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 };
                ctx.Response.ContentType = "image/png";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
                ctx.Response.Close();
                listener2.Stop();
            });

            var retryResult = await IconCache.GetIconPathAsync(id, new Uri(prefix + "icon.png"));
            await serverTask;
            Assert.True(retryResult.Downloaded);
            Assert.False(tracker.ShouldSkipDownload(id, "english"));
        }
        finally
        {
            tracker.RemoveFailedRecord(id, "english");
            SteamLanguageResolver.OverrideLanguage = null;
        }
    }
}
