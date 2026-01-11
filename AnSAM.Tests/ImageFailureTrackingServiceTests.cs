using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CommonUtilities;
using Xunit;

public class ImageFailureTrackingServiceTests
{
    [Fact]
    public void MigratedRecordsRetainLastFailedTimestamp()
    {
        var tracker = new ImageFailureTrackingService();
        var xmlPath = tracker.GetXmlFilePath();
        var dir = Path.GetDirectoryName(xmlPath)!;

        var oldFile = Path.Combine(dir, "steam_games_failed.xml");

        // Clean up before test
        if (File.Exists(oldFile)) File.Delete(oldFile);
        if (File.Exists(oldFile + ".migrated")) File.Delete(oldFile + ".migrated");

        var appId = Random.Shared.Next(700001, 800000);
        var lastFailed = DateTime.Now.AddDays(-3);
        var lastFailedStr = lastFailed.ToString("yyyy-MM-dd HH:mm:ss");

        var oldDoc = new XDocument(new XElement("FailedDownloads",
            new XElement("Game",
                new XAttribute("AppId", appId),
                new XAttribute("GameName", "TestGame"),
                new XAttribute("LastFailed", lastFailedStr))));
        oldDoc.Save(oldFile);

        tracker.MigrateOldFailedRecords();

        var newDoc = XDocument.Load(xmlPath);
        var migrated = newDoc.Root?.Elements("Game")
            .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId)?
            .Elements("Language")
            .FirstOrDefault(l => l.Attribute("Code")?.Value == "english")?
            .Attribute("LastFailed")?.Value;

        Assert.Equal(lastFailedStr, migrated);

        // Cleanup
        tracker.RemoveFailedRecord(appId, "english");
        if (File.Exists(oldFile)) File.Delete(oldFile);
        if (File.Exists(oldFile + ".migrated")) File.Delete(oldFile + ".migrated");
    }

    [Fact]
    public void FailureCount_IncrementsOnEachFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tracker = new ImageFailureTrackingService(tempDir);
        var appId = Random.Shared.Next(800001, 900000);

        try
        {
            // First failure
            tracker.RecordFailedDownload(appId, "tchinese", "TestGame1");
            var records = tracker.GetFailedRecords();
            var record = records.FirstOrDefault(r => r.AppId == appId && r.Language == "tchinese");
            Assert.NotNull(record);

            // Read XML to check FailureCount
            var doc = XDocument.Load(tracker.GetXmlFilePath());
            var failureCount = (int?)doc.Root?.Elements("Game")
                .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId)?
                .Elements("Language")
                .FirstOrDefault(l => l.Attribute("Code")?.Value == "tchinese")?
                .Attribute("FailureCount");
            Assert.Equal(1, failureCount);

            // Second failure
            tracker.RecordFailedDownload(appId, "tchinese", "TestGame1");
            doc = XDocument.Load(tracker.GetXmlFilePath());
            failureCount = (int?)doc.Root?.Elements("Game")
                .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId)?
                .Elements("Language")
                .FirstOrDefault(l => l.Attribute("Code")?.Value == "tchinese")?
                .Attribute("FailureCount");
            Assert.Equal(2, failureCount);

            // Third failure
            tracker.RecordFailedDownload(appId, "tchinese", "TestGame1");
            doc = XDocument.Load(tracker.GetXmlFilePath());
            failureCount = (int?)doc.Root?.Elements("Game")
                .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId)?
                .Elements("Language")
                .FirstOrDefault(l => l.Attribute("Code")?.Value == "tchinese")?
                .Attribute("FailureCount");
            Assert.Equal(3, failureCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExponentialBackoff_WorksCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tracker = new ImageFailureTrackingService(tempDir);
        var appId = Random.Shared.Next(900001, 1000000);

        try
        {
            // First failure - should skip for 10 minutes (failure count = 1)
            var firstFailTime = DateTime.Now.AddMinutes(-8);
            tracker.RecordFailedDownload(appId, "japanese", "TestGame2", firstFailTime);
            Assert.True(tracker.ShouldSkipDownload(appId, "japanese")); // 8 ?†é??å¤±?—ï??„åœ¨ 10 ?†é??€?¿æ???

            // Simulate 11 minutes passed - should allow retry (still failure count = 1, backoff = 10 min)
            var elevenMinutesAgo = DateTime.Now.AddMinutes(-11);
            // Manually update the LastFailed time without incrementing failure count
            var doc = XDocument.Load(tracker.GetXmlFilePath());
            var languageElement = doc.Root?.Elements("Game")
                .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId)?
                .Elements("Language")
                .FirstOrDefault(l => l.Attribute("Code")?.Value == "japanese");
            languageElement?.SetAttributeValue("LastFailed", elevenMinutesAgo.ToString("yyyy-MM-dd HH:mm:ss"));
            doc.Save(tracker.GetXmlFilePath());

            Assert.False(tracker.ShouldSkipDownload(appId, "japanese")); // 11 ?†é??å¤±?—ï?å·²è???10 ?†é?

            // Second failure - should skip for 20 minutes (failure count = 2)
            tracker.RecordFailedDownload(appId, "japanese", "TestGame2");
            Assert.True(tracker.ShouldSkipDownload(appId, "japanese")); // ?›å¤±?—ï???20 ?†é??€?¿æ???

            // Simulate 25 minutes passed - should allow retry (failure count = 2, backoff = 20 min)
            var twentyFiveMinutesAgo = DateTime.Now.AddMinutes(-25);
            doc = XDocument.Load(tracker.GetXmlFilePath());
            languageElement = doc.Root?.Elements("Game")
                .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId)?
                .Elements("Language")
                .FirstOrDefault(l => l.Attribute("Code")?.Value == "japanese");
            languageElement?.SetAttributeValue("LastFailed", twentyFiveMinutesAgo.ToString("yyyy-MM-dd HH:mm:ss"));
            doc.Save(tracker.GetXmlFilePath());

            Assert.False(tracker.ShouldSkipDownload(appId, "japanese")); // 25 ?†é??å¤±?—ï?å·²è???20 ?†é?

            // Third failure - should skip for 40 minutes (failure count = 3)
            tracker.RecordFailedDownload(appId, "japanese", "TestGame2");
            Assert.True(tracker.ShouldSkipDownload(appId, "japanese")); // ?›å¤±?—ï???40 ?†é??€?¿æ???
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SuccessfulDownload_ResetsFailureCount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tracker = new ImageFailureTrackingService(tempDir);
        var appId = Random.Shared.Next(1000001, 1100000);

        try
        {
            // Record multiple failures
            tracker.RecordFailedDownload(appId, "korean", "TestGame3");
            tracker.RecordFailedDownload(appId, "korean", "TestGame3");
            tracker.RecordFailedDownload(appId, "korean", "TestGame3");

            var records = tracker.GetFailedRecords();
            Assert.Contains(records, r => r.AppId == appId && r.Language == "korean");

            // Successful download - should remove the record entirely
            tracker.RemoveFailedRecord(appId, "korean");

            records = tracker.GetFailedRecords();
            Assert.DoesNotContain(records, r => r.AppId == appId && r.Language == "korean");

            // Next failure should start from count 1 again
            tracker.RecordFailedDownload(appId, "korean", "TestGame3");
            var doc = XDocument.Load(tracker.GetXmlFilePath());
            var failureCount = (int?)doc.Root?.Elements("Game")
                .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId)?
                .Elements("Language")
                .FirstOrDefault(l => l.Attribute("Code")?.Value == "korean")?
                .Attribute("FailureCount");
            Assert.Equal(1, failureCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BackwardCompatibility_OldRecordsWithoutFailureCount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tracker = new ImageFailureTrackingService(tempDir);
        var appId = Random.Shared.Next(1100001, 1200000);

        try
        {
            // Manually create old-style record without FailureCount
            var doc = new XDocument(new XElement("ImageFailures",
                new XElement("Game",
                    new XAttribute("AppId", appId),
                    new XAttribute("GameName", "OldStyleGame"),
                    new XElement("Language",
                        new XAttribute("Code", "english"),
                        new XAttribute("LastFailed", DateTime.Now.AddMinutes(-3).ToString("yyyy-MM-dd HH:mm:ss"))))));
            doc.Save(tracker.GetXmlFilePath());

            // Should default to 0 failure count, which means 5 minute backoff
            // 3 minutes ago is within 5 minutes, so should skip
            Assert.True(tracker.ShouldSkipDownload(appId, "english"));

            // Recording a new failure should add FailureCount = 1
            tracker.RecordFailedDownload(appId, "english", "OldStyleGame");
            doc = XDocument.Load(tracker.GetXmlFilePath());
            var failureCount = (int?)doc.Root?.Elements("Game")
                .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId)?
                .Elements("Language")
                .FirstOrDefault(l => l.Attribute("Code")?.Value == "english")?
                .Attribute("FailureCount");
            Assert.Equal(1, failureCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
