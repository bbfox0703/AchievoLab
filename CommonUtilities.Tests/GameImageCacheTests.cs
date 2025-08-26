using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Linq;
using System.Reflection;
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

    private class Always404Handler : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
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
    public async Task CancelledDownloadReturnsEmptyPath()
    {
        var language = "english";
        var id = Random.Shared.Next(5000000, 6000000);
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
            await Task.Delay(5000); // hold connection to allow cancellation
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            listener.Stop();
        });

        using var cts = new CancellationTokenSource();
        var downloadTask = _cache.GetImagePathAsync(id.ToString(), new Uri(prefix + "slow.png"), language, id, cts.Token);
        cts.CancelAfter(100);
        var result = await downloadTask;
        Assert.Equal(string.Empty, result.Path);
        Assert.False(result.Downloaded);
        await serverTask;
    }

    [Fact]
    public async Task CancelledDownloadReleasesConcurrency()
    {
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
            var ctx1 = await listener.GetContextAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000);
                    ctx1.Response.StatusCode = 200;
                    ctx1.Response.Close();
                }
                catch { }
            });

            var ctx2 = await listener.GetContextAsync();
            var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 };
            ctx2.Response.ContentType = "image/png";
            ctx2.Response.ContentLength64 = data.Length;
            await ctx2.Response.OutputStream.WriteAsync(data);
            ctx2.Response.Close();
            listener.Stop();
        });

        using var cache = new GameImageCache(_baseCacheDir, _tracker, maxConcurrency: 1, maxConcurrentRequestsPerDomain: 1, baseDomainDelay: TimeSpan.Zero, jitterSeconds: 0);
        using var cts = new CancellationTokenSource(100);
        _ = cache.GetImagePathAsync("slow", new Uri(prefix + "slow"), "english", null, cts.Token);
        await Task.Delay(200);

        var sw = Stopwatch.StartNew();
        var result = await cache.GetImagePathAsync("fast", new Uri(prefix + "fast"), "english", null);
        sw.Stop();

        await serverTask;
        Assert.True(result.Downloaded);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 1000);
    }

    [Fact]
    public async Task RateLimiterDelayIsConfigurable()
    {
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
        var requestTimes = new List<DateTime>();
        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var ctx = await listener.GetContextAsync();
                requestTimes.Add(DateTime.UtcNow);
                var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 };
                ctx.Response.ContentType = "image/png";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
                ctx.Response.Close();
            }
            listener.Stop();
        });

        using var cache = new GameImageCache(_baseCacheDir, _tracker, baseDomainDelay: TimeSpan.FromMilliseconds(100), jitterSeconds: 0);

        var id1 = Random.Shared.Next(1000000, 2000000);
        var id2 = id1 + 1;
        await cache.GetImagePathAsync("delay1", new Uri(prefix + "1.png"), "english", id1);
        await cache.GetImagePathAsync("delay2", new Uri(prefix + "2.png"), "english", id2);

        await serverTask;

        Assert.Equal(2, requestTimes.Count);
        var interval = requestTimes[1] - requestTimes[0];
        Assert.InRange(interval.TotalMilliseconds, 100, 500);
        _tracker.RemoveFailedRecord(id1, "english");
        _tracker.RemoveFailedRecord(id2, "english");
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

        Assert.NotNull(result);
        var image = result.Value;
        Assert.True(image.Downloaded);
        Assert.EndsWith(".png", image.Path);
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

    [Fact]
    public async Task RecordsFailureFor404sAndSkipsFurtherRequests()
    {
        var handler = new Always404Handler();
        var tracker = new ImageFailureTrackingService();
        using var cache = new GameImageCache(_baseCacheDir, tracker, httpClient: new HttpClient(handler), disposeHttpClient: true);

        var language = "spanish";
        var id = Random.Shared.Next(900001, 1000000);

        var urls = new[]
        {
            $"https://example.com/{id}/a.png",
            $"https://example.com/{id}/b.png"
        };

        var result = await cache.GetImagePathAsync(id.ToString(), urls, language, id);
        Assert.Null(result);
        Assert.True(tracker.ShouldSkipDownload(id, language));
        Assert.True(tracker.ShouldSkipDownload(id, "english"));
        Assert.Equal(5, handler.RequestCount);

        var logos = new[]
        {
            $"https://example.com/{id}/logo_{language}.png",
            $"https://example.com/{id}/logo.png"
        };

        var logoResult = await cache.GetImagePathAsync(id.ToString(), logos, language, id);
        Assert.Null(logoResult);
        Assert.Equal(5, handler.RequestCount);

        tracker.RemoveFailedRecord(id, language);
        tracker.RemoveFailedRecord(id, "english");
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

    [Fact]
    public async Task RetryAfterDelaysNextRequest()
    {
        using var cache = new GameImageCache(_baseCacheDir, _tracker, baseDomainDelay: TimeSpan.FromMilliseconds(10), jitterSeconds: 0);
        var httpField = typeof(GameImageCache).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var responses = new[]
        {
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(200)) }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x89,0x50,0x4E,0x47,0,0,0,0 })
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/png") }
                }
            }
        };
        var handler = new QueueMessageHandler(responses);
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "AchievoLab/1.0");
        client.DefaultRequestHeaders.Add("Accept", "image/webp,image/avif,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        httpField.SetValue(cache, client);

        var uri = new Uri("http://example.com/test.png");
        await cache.GetImagePathAsync("1", uri, "english");

        var sw = Stopwatch.StartNew();
        await cache.GetImagePathAsync("2", uri, "english");
        sw.Stop();
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task ConsecutiveForbiddenIncreasesDelay()
    {
        using var cache = new GameImageCache(_baseCacheDir, _tracker, baseDomainDelay: TimeSpan.FromMilliseconds(50), jitterSeconds: 0);
        var httpField = typeof(GameImageCache).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var responses = new[]
        {
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x89,0x50,0x4E,0x47,0,0,0,0 })
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/png") }
                }
            }
        };
        var handler = new QueueMessageHandler(responses);
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "AchievoLab/1.0");
        client.DefaultRequestHeaders.Add("Accept", "image/webp,image/avif,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        httpField.SetValue(cache, client);

        var uri = new Uri("http://example.com/test2.png");
        await cache.GetImagePathAsync("a", uri, "english");

        var rateLimiterField = typeof(GameImageCache).GetField("_rateLimiter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rateLimiter = rateLimiterField.GetValue(cache)!;
        var extraField = rateLimiter.GetType().GetField("_domainExtraDelay", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)extraField.GetValue(rateLimiter)!;
        Assert.True(dict.Contains(uri.Host));
        var delay = (TimeSpan)dict[uri.Host]!;
        Assert.True(delay >= TimeSpan.FromMilliseconds(100));

        await cache.GetImagePathAsync("b", uri, "english");
        dict = (System.Collections.IDictionary)extraField.GetValue(rateLimiter)!;
        Assert.True(dict.Contains(uri.Host));
        delay = (TimeSpan)dict[uri.Host]!;
        Assert.True(delay >= TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        await cache.GetImagePathAsync("c", uri, "english");
        sw.Stop();
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(250));

        dict = (System.Collections.IDictionary)extraField.GetValue(rateLimiter)!;
        Assert.False(dict.Contains(uri.Host));
    }

    [Fact]
    public async Task NotFoundDoesNotIncreaseRateLimiterDelay()
    {
        using var cache = new GameImageCache(_baseCacheDir, _tracker, baseDomainDelay: TimeSpan.FromMilliseconds(10), jitterSeconds: 0);
        var httpField = typeof(GameImageCache).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var responses = new[]
        {
            new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        var handler = new QueueMessageHandler(responses);
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "AchievoLab/1.0");
        client.DefaultRequestHeaders.Add("Accept", "image/webp,image/avif,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        httpField.SetValue(cache, client);

        var uri = new Uri("http://example.com/missing.png");
        var result = await cache.GetImagePathAsync("z", uri, "english");
        Assert.Equal(string.Empty, result.Path);
        Assert.False(result.Downloaded);

        var rateLimiterField = typeof(GameImageCache).GetField("_rateLimiter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rateLimiter = rateLimiterField.GetValue(cache)!;

        var extraField = rateLimiter.GetType().GetField("_domainExtraDelay", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var extraDict = (System.Collections.IDictionary)extraField.GetValue(rateLimiter)!;
        Assert.False(extraDict.Contains(uri.Host));

        var failureField = rateLimiter.GetType().GetField("_failureCounts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var failDict = (System.Collections.IDictionary)failureField.GetValue(rateLimiter)!;
        Assert.False(failDict.Contains(uri.Host));
    }

    [Fact]
    public async Task SuccessfulEnglishFallbackClearsFailureRecord()
    {
        var id = 440; // Known app id with English header

        // Replace internal HttpClient to avoid external network dependency
        var httpField = typeof(GameImageCache).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
        var handler = new SteamFallbackHandler(new HttpClientHandler());
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "AchievoLab/1.0");
        client.DefaultRequestHeaders.Add("Accept", "image/webp,image/avif,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        httpField!.SetValue(_cache, client);

        // First request: localized download fails with server error to record failure
        int port1;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port1 = ((IPEndPoint)l.LocalEndpoint).Port;
        }
        var prefix1 = $"http://localhost:{port1}/";
        using var listener1 = new HttpListener();
        listener1.Prefixes.Add(prefix1);
        listener1.Start();
        var serverTask1 = Task.Run(async () =>
        {
            var ctx = await listener1.GetContextAsync();
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            listener1.Stop();
        });

        var first = await _cache.GetImagePathAsync(id.ToString(), new Uri(prefix1 + "fail.png"), "spanish", id);
        await serverTask1;
        Assert.Equal(string.Empty, first.Path);
        Assert.True(_tracker.ShouldSkipDownload(id, "spanish"));

        // Invoke English fallback via reflection
        var method = typeof(GameImageCache).GetMethod("TryEnglishFallbackAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task<GameImageCache.ImageResult?>)method!.Invoke(
            _cache,
            new object?[] { id.ToString(), "spanish", id, CancellationToken.None })!;
        var fallback = await task;
        Assert.NotNull(fallback);
        var spanishPath = fallback.Value.Path;
        Assert.True(File.Exists(spanishPath));

        // English cache should also contain the downloaded image
        var englishPath = _cache.TryGetCachedPath(id.ToString(), "english", checkEnglishFallback: false);
        Assert.NotNull(englishPath);
        Assert.True(File.Exists(englishPath));

        // Failure record for Spanish should be cleared
        Assert.False(_tracker.ShouldSkipDownload(id, "spanish"));

        // Remove files to force a new request and verify no skip occurs
        File.Delete(spanishPath);
        var englishCached = _cache.TryGetCachedPath(id.ToString(), "english", checkEnglishFallback: false);
        if (englishCached != null)
        {
            File.Delete(englishCached);
        }

        int port2;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port2 = ((IPEndPoint)l.LocalEndpoint).Port;
        }
        var prefix2 = $"http://localhost:{port2}/";
        using var listener2 = new HttpListener();
        listener2.Prefixes.Add(prefix2);
        listener2.Start();
        var contextTask = listener2.GetContextAsync();

        var second = await _cache.GetImagePathAsync(id.ToString(), new Uri(prefix2 + "again.png"), "spanish", id);
        await Task.Delay(200);
        Assert.True(contextTask.IsCompleted);
        if (contextTask.IsCompleted)
        {
            var ctx = await contextTask;
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        listener2.Stop();
        Assert.Equal(string.Empty, second.Path);

        _tracker.RemoveFailedRecord(id, "spanish");
    }

    private class QueueMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public QueueMessageHandler(IEnumerable<HttpResponseMessage> responses)
        {
            foreach (var r in responses)
            {
                _responses.Enqueue(r);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count > 0)
            {
                return Task.FromResult(_responses.Dequeue());
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private class SteamFallbackHandler : DelegatingHandler
    {
        public SteamFallbackHandler(HttpMessageHandler inner) : base(inner) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.Host.EndsWith("steamstatic.com", StringComparison.OrdinalIgnoreCase))
            {
                var data = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 };
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(data)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return Task.FromResult(response);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
