using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Linq;
using CommonUtilities;
using Xunit;

public class GameImageCacheTests : IDisposable
{
    private readonly string _baseCacheDir;
    private readonly string? _originalXdg;
    private readonly GameImageCache _cache;
    private readonly ImageFailureTrackingService _tracker;

    public GameImageCacheTests()
    {
        _baseCacheDir = Path.Combine(Path.GetTempPath(), "GameImageCacheTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_baseCacheDir);
        _originalXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = Path.Combine(_baseCacheDir, "data");
        Directory.CreateDirectory(dataHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
        _tracker = new ImageFailureTrackingService();
        _cache = new GameImageCache(_baseCacheDir, _tracker);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdg);
        try { Directory.Delete(_baseCacheDir, true); } catch { }
    }

    public static IEnumerable<object[]> ValidHeaders()
    {
        yield return new object[] { "bmp", new byte[] { 0x42, 0x4D, 0, 0, 0, 0 } };
        yield return new object[] { "ico", new byte[] { 0x00, 0x00, 0x01, 0x00, 0, 0 } };
        yield return new object[] { "avif", new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 } };
    }

    [Theory]
    [MemberData(nameof(ValidHeaders))]
    public async Task ValidCachedImageIsUsed(string ext, byte[] data)
    {
        var language = "english";
        var id = Random.Shared.Next(100000, 200000);
        var cacheDir = Path.Combine(_baseCacheDir, language);
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, $"{id}.{ext}");
        await File.WriteAllBytesAsync(path, data);
        var uri = new Uri($"http://example.invalid/{id}.{ext}");
        var result = await _cache.GetImagePathAsync(id.ToString(), uri, language, id);
        Assert.Equal(path, result.Path);
        Assert.False(result.Downloaded);
    }

    [Fact]
    public async Task ProgressCountsCachedImages()
    {
        _cache.ResetProgress();
        var events = new List<(int completed, int total)>();
        void Handler(int c, int t) => events.Add((c, t));
        _cache.ProgressChanged += Handler;
        try
        {
            var language = "english";
            var cacheDir = Path.Combine(_baseCacheDir, language);
            Directory.CreateDirectory(cacheDir);
            var cachedId = Random.Shared.Next(200001, 300000);
            var cachedPath = Path.Combine(cacheDir, $"{cachedId}.png");
            await File.WriteAllBytesAsync(cachedPath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 });
            await _cache.GetImagePathAsync(cachedId.ToString(), new Uri($"http://example.invalid/{cachedId}.png"), language, cachedId);

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
            var result = await _cache.GetImagePathAsync(downloadId.ToString(), new Uri(prefix + "icon.png"), language, downloadId);
            await serverTask;
            Assert.True(result.Downloaded);
        }
        finally
        {
            _cache.ProgressChanged -= Handler;
        }

        Assert.Equal(new (int, int)[] { (1, 1), (1, 2), (2, 2) }, events.ToArray());
    }

    [Fact]
    public void ProgressChangedHandlerExceptionIsSwallowed()
    {
        _cache.ResetProgress();
        void Handler(int c, int t) => throw new InvalidOperationException();
        _cache.ProgressChanged += Handler;
        try
        {
            var ex = Record.Exception(() => _cache.ResetProgress());
            Assert.Null(ex);
        }
        finally
        {
            _cache.ProgressChanged -= Handler;
        }
    }

    [Theory]
    [InlineData("english")]
    [InlineData("spanish")]
    public async Task InvalidDownloadIsIgnored(string language)
    {
        var id = Random.Shared.Next(300001, 400000);
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

        var result = await _cache.GetImagePathAsync(id.ToString(), new[] { prefix + "bad.png" }, language, id);
        await serverTask;
        Assert.Null(result);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_baseCacheDir, language), $"{id}.*"));
        _tracker.RemoveFailedRecord(id, language);
    }

    [Fact]
    public async Task FailedDownloadReturnsEmptyPath()
    {
        var language = "english";
        var id = Random.Shared.Next(400001, 500000);
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

        var result = await _cache.GetImagePathAsync(id.ToString(), new Uri(prefix + "missing.png"), language, id);
        await serverTask;

        Assert.Equal(string.Empty, result.Path);
        Assert.False(result.Downloaded);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_baseCacheDir, language), $"{id}.*"));
        _tracker.RemoveFailedRecord(id, language);
    }

    [Fact]
    public async Task UsesNextUrlWhenFirstFails()
    {
        var language = "english";
        var id = Random.Shared.Next(600001, 700000);
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
            // First request - 404
            var ctx1 = await listener.GetContextAsync();
            ctx1.Response.StatusCode = 404;
            ctx1.Response.Close();

            // Second request - valid image
            var ctx2 = await listener.GetContextAsync();
            var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 };
            ctx2.Response.ContentType = "image/png";
            ctx2.Response.ContentLength64 = data.Length;
            await ctx2.Response.OutputStream.WriteAsync(data);
            ctx2.Response.Close();
            listener.Stop();
        });

        var urls = new[] { prefix + "missing.png", prefix + "icon.png" };
        var result = await _cache.GetImagePathAsync(id.ToString(), urls, language, id);
        await serverTask;

        Assert.True(result.Downloaded);
        Assert.EndsWith(".png", result.Path);
        _tracker.RemoveFailedRecord(id, language);
    }

    [Fact]
    public async Task SkipsAndRetriesBasedOnFailureLog()
    {
        var language = "english";
        var id = Random.Shared.Next(500001, 600000);
        _tracker.RecordFailedDownload(id, language);

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
        var skipResult = await _cache.GetImagePathAsync(id.ToString(), new Uri(prefix + "icon.png"), language, id);
        await Task.Delay(200);
        Assert.False(contextTask.IsCompleted);
        listener.Stop();
        Assert.Equal(string.Empty, skipResult.Path);

        var doc = XDocument.Load(_tracker.GetXmlFilePath());
        var game = doc.Root?.Elements("Game").FirstOrDefault(g => (int?)g.Attribute("AppId") == id);
        var lang = game?.Elements("Language").FirstOrDefault(l => l.Attribute("Code")?.Value == language);
        lang?.SetAttributeValue("LastFailed", DateTime.Now.AddDays(-20).ToString("yyyy-MM-dd HH:mm:ss"));
        doc.Save(_tracker.GetXmlFilePath());

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

        var retryResult = await _cache.GetImagePathAsync(id.ToString(), new Uri(prefix + "icon.png"), language, id);
        await serverTask;
        Assert.True(retryResult.Downloaded);
        Assert.False(_tracker.ShouldSkipDownload(id, language));
        _tracker.RemoveFailedRecord(id, language);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("french")]
    public void CreatesLanguageSpecificDirectory(string language)
    {
        var cacheDir = Path.Combine(_baseCacheDir, language);
        if (Directory.Exists(cacheDir))
        {
            Directory.Delete(cacheDir, true);
        }
        _cache.TryGetCachedPath("abc", language);
        Assert.True(Directory.Exists(cacheDir));
    }

    [Theory]
    [InlineData("english")]
    public void ReusesLanguageSpecificDirectory(string language)
    {
        var cacheDir = Path.Combine(_baseCacheDir, language);
        Directory.CreateDirectory(cacheDir);
        var sentinel = Path.Combine(cacheDir, "sentinel.txt");
        File.WriteAllText(sentinel, "sentinel");
        _cache.TryGetCachedPath("def", language);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task CacheIsLanguageSpecific()
    {
        var id = Random.Shared.Next(700001, 800000);
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

        await _cache.GetImagePathAsync(id.ToString(), new Uri(prefix + "icon.png"), "english", id);
        await serverTask;

        var other = _cache.TryGetCachedPath(id.ToString(), "spanish", checkEnglishFallback: false);
        Assert.Null(other);
    }

    [Fact]
    public async Task NonEnglishDownloadDoesNotPopulateEnglishCache()
    {
        var id = Random.Shared.Next(800001, 900000);
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

        var result = await _cache.GetImagePathAsync(id.ToString(), new Uri(prefix + "icon.png"), "spanish", id);
        await serverTask;
        Assert.True(result.Downloaded);

        var englishFiles = Directory.EnumerateFiles(Path.Combine(_baseCacheDir, "english"), $"{id}.*");
        Assert.Empty(englishFiles);
        _tracker.RemoveFailedRecord(id, "spanish");
    }
}
