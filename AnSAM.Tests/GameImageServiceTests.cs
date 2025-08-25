using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommonUtilities;
using Xunit;

public class GameImageServiceTests
{
    [Fact(Skip = "Environment dependent")]
    public async Task InvalidCachedImage_RemovedAndFailureRecorded()
    {
        var setupTracker = new ImageFailureTrackingService();
        var appId = int.MaxValue;
        setupTracker.RemoveFailedRecord(appId, "english");
        var oldTime = DateTime.Now.AddDays(-1);
        setupTracker.RecordFailedDownload(appId, "english", failedAt: oldTime);

        var service = new SharedImageService();
        var cacheField = typeof(SharedImageService).GetField("_imageCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (Dictionary<string, string>)cacheField!.GetValue(service)!;
        var cacheKey = $"{appId}_english";
        var invalidPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        File.WriteAllText(invalidPath, "not an image");
        dict[cacheKey] = invalidPath;

        await service.GetGameImageAsync(appId);

        Assert.False(File.Exists(invalidPath));
        Assert.False(dict.ContainsKey(cacheKey));

        var verifyTracker = new ImageFailureTrackingService();
        var doc = XDocument.Load(verifyTracker.GetXmlFilePath());
        var gameElement = doc.Root?.Elements("Game")
            .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId);
        var lastFailedStr = gameElement?
            .Elements("Language")
            .FirstOrDefault(l => l.Attribute("Code")?.Value == "english")?
            .Attribute("LastFailed")?.Value;

        Assert.False(string.IsNullOrEmpty(lastFailedStr));
        var lastFailed = DateTime.Parse(lastFailedStr!);
        Assert.True(lastFailed > oldTime);

        verifyTracker.RemoveFailedRecord(appId, "english");
        service.Dispose();
    }

    [Fact]
    public async Task ImageDownloadCompleted_FiresAgain_AfterLanguageChange()
    {
        var appId = int.MaxValue - 1;
        var cleanupTracker = new ImageFailureTrackingService();
        cleanupTracker.RemoveFailedRecord(appId, "english");
        cleanupTracker.RemoveFailedRecord(appId, "german");

        var service = new SharedImageService();
        int eventCount = 0;
        service.ImageDownloadCompleted += (_, _) => eventCount++;

        await service.GetGameImageAsync(appId); // initial download in english

        await service.SetLanguage("german");
        await service.GetGameImageAsync(appId); // download after language switch

        Assert.Equal(2, eventCount);

        cleanupTracker.RemoveFailedRecord(appId, "english");
        cleanupTracker.RemoveFailedRecord(appId, "german");
        service.Dispose();
    }
}
