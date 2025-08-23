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
}
