using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommonUtilities;
using Xunit;

public class GameImageServiceCancellationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SharedImageService _service;
    private readonly ImageFailureTrackingService _tracker;
    private readonly string? _originalXdg;

    public GameImageServiceCancellationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _originalXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _tempDir);

        // Setup cache with hanging image handler
        _tracker = new ImageFailureTrackingService(_tempDir);
        var cacheClient = new HttpClient(new HangingImageHandler());
        var cache = new GameImageCache(_tempDir, _tracker, httpClient: cacheClient, disposeHttpClient: true);

        // SharedImageService with fake store API client
        var storeClient = new HttpClient(new FakeStoreApiHandler());
        _service = new SharedImageService(storeClient, cache, disposeHttpClient: true);
    }

    [Fact]
    public async Task CancelDownloadOnLanguageChange_DoesNotRecordFailure()
    {
        var appId = 12345;
        var downloadTask = _service.GetGameImageAsync(appId);
        await Task.Delay(100);
        await _service.SetLanguage("german");

        var result = await downloadTask;
        Assert.True(string.IsNullOrEmpty(result) || File.Exists(result));
        Assert.False(_tracker.ShouldSkipDownload(appId, "english"));
        Assert.False(_tracker.ShouldSkipDownload(appId, "german"));
    }

    public void Dispose()
    {
        _service.Dispose();
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdg);
        try { Directory.Delete(_tempDir, true); }
        catch
        {
            // Ignore cleanup failures in test teardown
        }
    }

    private class FakeStoreApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = "{\"12345\":{\"success\":true,\"data\":{\"header_image\":\"http://example.com/hanging.png\"}}}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private class HangingImageHandler : HttpMessageHandler
    {
        private static readonly byte[] PngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMB/6XfZp8AAAAASUVORK5CYII=");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Make ALL image download requests hang for 10 seconds to simulate slow download
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(PngBytes)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return resp;
        }
    }
}

