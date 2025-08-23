using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CommonUtilities;

namespace MyOwnGames.Services
{
    /// <summary>
    /// Manages failed image download records to avoid repeatedly attempting to download
    /// images for games that don't have them available.
    /// </summary>
    public class FailedDownloadService
    {
        private readonly string _xmlFilePath;
        private readonly object _lockObject = new object();

        public FailedDownloadService()
        {
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "cache");
            
            Directory.CreateDirectory(cacheDirectory);
            _xmlFilePath = Path.Combine(cacheDirectory, "steam_games_failed.xml");
        }

        /// <summary>
        /// Checks if we should skip downloading for a specific App ID based on recent failures
        /// </summary>
        /// <param name="appId">The Steam App ID</param>
        /// <returns>True if we should skip download (recent failure within 15 days)</returns>
        public bool ShouldSkipDownload(int appId)
        {
            lock (_lockObject)
            {
                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return false;

                    var doc = XDocument.Load(_xmlFilePath);
                    var gameElement = doc.Root?.Elements("Game")
                        .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId);

                    if (gameElement == null)
                        return false;

                    var lastFailedStr = gameElement.Attribute("LastFailed")?.Value;
                    if (DateTime.TryParse(lastFailedStr, out var lastFailed))
                    {
                        var daysSinceFailure = (DateTime.Now - lastFailed).TotalDays;
                        if (daysSinceFailure <= 15)
                        {
                            DebugLogger.LogDebug($"Skipping download for {appId} - failed {daysSinceFailure:F1} days ago");
                            return true;
                        }
                        else
                        {
                            DebugLogger.LogDebug($"Retrying download for {appId} - last failure was {daysSinceFailure:F1} days ago");
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error checking failed download for {appId}: {ex.Message}");
                }

                return false;
            }
        }

        /// <summary>
        /// Records a failed download attempt
        /// </summary>
        /// <param name="appId">The Steam App ID</param>
        /// <param name="gameName">Optional game name for reference</param>
        public void RecordFailedDownload(int appId, string? gameName = null)
        {
            lock (_lockObject)
            {
                try
                {
                    XDocument doc;
                    if (File.Exists(_xmlFilePath))
                    {
                        doc = XDocument.Load(_xmlFilePath);
                    }
                    else
                    {
                        doc = new XDocument(new XElement("FailedDownloads"));
                    }

                    var root = doc.Root;
                    if (root == null)
                    {
                        root = new XElement("FailedDownloads");
                        doc.Add(root);
                    }

                    // Remove existing record if present
                    var existingElement = root.Elements("Game")
                        .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId);
                    existingElement?.Remove();

                    // Add new failure record
                    var gameElement = new XElement("Game",
                        new XAttribute("AppId", appId),
                        new XAttribute("LastFailed", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

                    if (!string.IsNullOrEmpty(gameName))
                    {
                        gameElement.Add(new XAttribute("GameName", gameName));
                    }

                    root.Add(gameElement);

                    // Save with backup mechanism
                    var tempPath = _xmlFilePath + ".tmp";
                    doc.Save(tempPath);
                    File.Move(tempPath, _xmlFilePath, true);

                    DebugLogger.LogDebug($"Recorded failed download for {appId} ({gameName ?? "unknown"})");
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error recording failed download for {appId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Removes a failed download record (called when download succeeds)
        /// </summary>
        /// <param name="appId">The Steam App ID</param>
        public void RemoveFailedRecord(int appId)
        {
            lock (_lockObject)
            {
                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return;

                    var doc = XDocument.Load(_xmlFilePath);
                    var gameElement = doc.Root?.Elements("Game")
                        .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId);

                    if (gameElement != null)
                    {
                        gameElement.Remove();
                        
                        // Save the updated document
                        var tempPath = _xmlFilePath + ".tmp";
                        doc.Save(tempPath);
                        File.Move(tempPath, _xmlFilePath, true);

                        DebugLogger.LogDebug($"Removed failed download record for {appId} (download now successful)");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error removing failed download record for {appId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets all failed download records for debugging/maintenance
        /// </summary>
        /// <returns>List of failed download records</returns>
        public List<(int AppId, DateTime LastFailed, string? GameName)> GetFailedRecords()
        {
            lock (_lockObject)
            {
                var records = new List<(int AppId, DateTime LastFailed, string? GameName)>();

                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return records;

                    var doc = XDocument.Load(_xmlFilePath);
                    foreach (var gameElement in doc.Root?.Elements("Game") ?? Enumerable.Empty<XElement>())
                    {
                        var appId = (int?)gameElement.Attribute("AppId");
                        var lastFailedStr = gameElement.Attribute("LastFailed")?.Value;
                        var gameName = gameElement.Attribute("GameName")?.Value;

                        if (appId.HasValue && DateTime.TryParse(lastFailedStr, out var lastFailed))
                        {
                            records.Add((appId.Value, lastFailed, gameName));
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error getting failed download records: {ex.Message}");
                }

                return records.OrderByDescending(r => r.LastFailed).ToList();
            }
        }

        /// <summary>
        /// Cleans up old failed records (older than 30 days)
        /// </summary>
        public void CleanupOldRecords()
        {
            lock (_lockObject)
            {
                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return;

                    var doc = XDocument.Load(_xmlFilePath);
                    var cutoffDate = DateTime.Now.AddDays(-30);
                    var removedCount = 0;

                    var elementsToRemove = doc.Root?.Elements("Game")
                        .Where(g =>
                        {
                            var lastFailedStr = g.Attribute("LastFailed")?.Value;
                            return DateTime.TryParse(lastFailedStr, out var lastFailed) && lastFailed < cutoffDate;
                        })
                        .ToList() ?? new List<XElement>();

                    foreach (var element in elementsToRemove)
                    {
                        element.Remove();
                        removedCount++;
                    }

                    if (removedCount > 0)
                    {
                        var tempPath = _xmlFilePath + ".tmp";
                        doc.Save(tempPath);
                        File.Move(tempPath, _xmlFilePath, true);

                        DebugLogger.LogDebug($"Cleaned up {removedCount} old failed download records (older than 30 days)");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error cleaning up old failed download records: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets the path to the failed downloads XML file
        /// </summary>
        public string GetXmlFilePath() => _xmlFilePath;
    }
}