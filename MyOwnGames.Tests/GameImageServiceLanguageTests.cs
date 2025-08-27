using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CommonUtilities;
using Xunit;

public class GameImageServiceLanguageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SharedImageService _service;
    private readonly FakeImageHandler _imageHandler;

    public GameImageServiceLanguageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _imageHandler = new FakeImageHandler();
        var cacheClient = new HttpClient(_imageHandler);
        var cache = new GameImageCache(_tempDir, new ImageFailureTrackingService(), httpClient: cacheClient, disposeHttpClient: true);

        var storeClient = new HttpClient(new FakeStoreApiHandler());
        _service = new SharedImageService(storeClient, cache, disposeHttpClient: true);
    }

    [Fact(Skip = "Flaky with configurable rate limiter")]
    public async Task RedownloadsImage_WhenLanguageChanges_FiresEventWithNewLanguagePath()
    {
        var appId = 12345;
        string? lastPath = null;
        int eventCount = 0;
        _service.ImageDownloadCompleted += (_, path) => { eventCount++; lastPath = path; };

        var firstPath = await _service.GetGameImageAsync(appId); // initial english download
        Assert.NotNull(firstPath);
        Assert.Contains(Path.Combine(_tempDir, "english"), firstPath!);
        Assert.Contains("https://example.com/header.jpg?l=english", _imageHandler.RequestedUrls);
        Assert.DoesNotContain(_imageHandler.RequestedUrls, url => url.Contains("header_english"));

        // Remove the english cache to force a redownload for the next language
        Directory.Delete(Path.Combine(_tempDir, "english"), true);

        await _service.SetLanguage("german");
        var secondPath = await _service.GetGameImageAsync(appId); // force redownload with english fallback

        Assert.NotNull(secondPath);
        Assert.Contains(Path.Combine(_tempDir, "german"), secondPath!);
        Assert.Equal(secondPath, lastPath);
        Assert.Equal(2, eventCount);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private class FakeStoreApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var lang = request.RequestUri!.Query.Contains("l=german") ? "german" : "english";
            var headerUrl = lang == "german" ? "https://example.com/header_german.jpg" : "https://example.com/header.jpg";
            var json = $"{{\"12345\":{{\"success\":true,\"data\":{{\"header_image\":\"{headerUrl}\"}}}}}}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private class FakeImageHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();

        private static readonly byte[] PngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMB/6XfZp8AAAAASUVORK5CYII=");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            RequestedUrls.Add(url);
            if (url.Contains("header_german") || url.Contains("logo_german") ||
                url.Contains("header_english") || url.Contains("logo_english"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(PngBytes)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(resp);
        }
    }
}
