using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommonUtilities;
using MyOwnGames.Services;
using Xunit;

public class GameImageServiceTests
{
    [Fact]
    public async Task InvalidCachedImage_RemovedAndFailureRecorded()
    {
        var tracker = new ImageFailureTrackingService();
        var appId = Random.Shared.Next(900000, 1000000);
        tracker.RemoveFailedRecord(appId, "english");
        var oldTime = DateTime.Now.AddDays(-20);
        tracker.RecordFailedDownload(appId, "english", failedAt: oldTime);

        var service = new GameImageService();
        var cacheField = typeof(GameImageService).GetField("_imageCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (ConcurrentDictionary<string, string>)cacheField!.GetValue(service)!;
        var cacheKey = $"{appId}_english";
        var invalidPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        File.WriteAllText(invalidPath, "not an image");
        dict[cacheKey] = invalidPath;

        await service.GetGameImageAsync(appId);

        Assert.False(File.Exists(invalidPath));
        Assert.False(dict.ContainsKey(cacheKey));

        var doc = XDocument.Load(tracker.GetXmlFilePath());
        var gameElement = doc.Root?.Elements("Game")
            .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId);
        var lastFailedStr = gameElement?
            .Elements("Language")
            .FirstOrDefault(l => l.Attribute("Code")?.Value == "english")?
            .Attribute("LastFailed")?.Value;

        Assert.False(string.IsNullOrEmpty(lastFailedStr));
        var lastFailed = DateTime.Parse(lastFailedStr!);
        Assert.True(lastFailed > oldTime);

        tracker.RemoveFailedRecord(appId, "english");
        service.Dispose();
    }
}
