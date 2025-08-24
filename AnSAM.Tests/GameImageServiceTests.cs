using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommonUtilities;
using MyOwnGames.Services;
using Xunit;

public class GameImageServiceTests
{
    [Fact(Skip = "Environment dependent")]
    public async Task InvalidCachedImage_RemovedAndFailureRecorded()
    {
        var tracker = new ImageFailureTrackingService();
        var appId = int.MaxValue;
        tracker.RemoveFailedRecord(appId, "english");
        var oldTime = DateTime.Now.AddDays(-1);
        tracker.RecordFailedDownload(appId, "english", failedAt: oldTime);

        var service = new GameImageService();
        var cacheField = typeof(GameImageService).GetField("_imageCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (Dictionary<string, string>)cacheField!.GetValue(service)!;
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

    [Fact]
    public async Task ImageDownloadCompleted_FiresAgain_AfterLanguageChange()
    {
        var tracker = new ImageFailureTrackingService();
        var appId = int.MaxValue - 1;
        tracker.RemoveFailedRecord(appId, "english");
        tracker.RemoveFailedRecord(appId, "german");

        var service = new GameImageService();
        int eventCount = 0;
        service.ImageDownloadCompleted += (_, _) => eventCount++;

        await service.GetGameImageAsync(appId); // initial download in english

        service.SetLanguage("german");
        await service.GetGameImageAsync(appId); // download after language switch

        Assert.Equal(2, eventCount);

        tracker.RemoveFailedRecord(appId, "english");
        tracker.RemoveFailedRecord(appId, "german");
        service.Dispose();
    }
}
