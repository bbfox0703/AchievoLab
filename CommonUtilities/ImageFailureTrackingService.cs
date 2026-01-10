using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CommonUtilities
{
    /// <summary>
    /// Unified image failure tracking service that manages both general and language-specific image download failures.
    /// Uses exponential backoff strategy: 5min → 10min → 20min → 40min → 80min → 160min → 320min → 640min → 1280min → 2560min → 5120min → 10240min → 20480min (max)
    /// Replaces both steam_games_failed.xml and manages games_image_failed_log.xml
    /// </summary>
    public class ImageFailureTrackingService
    {
        private readonly string _xmlFilePath;
        private readonly object _lockObject = new object();

        // Exponential backoff configuration
        private const int BaseBackoffMinutes = 5;
        private const int MaxBackoffMinutes = 20480; // 失敗 12 次後的上限 (約 14.22 天)

        public ImageFailureTrackingService()
        {
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "cache");
            
            Directory.CreateDirectory(cacheDirectory);
            _xmlFilePath = Path.Combine(cacheDirectory, "games_image_failed_log.xml");
        }

        public ImageFailureTrackingService(string customCacheDirectory)
        {
            Directory.CreateDirectory(customCacheDirectory);
            _xmlFilePath = Path.Combine(customCacheDirectory, "games_image_failed_log.xml");
        }

        /// <summary>
        /// Calculates exponential backoff time in minutes based on failure count
        /// </summary>
        /// <param name="failureCount">Number of consecutive failures</param>
        /// <returns>Backoff time in minutes (capped at MaxBackoffMinutes)</returns>
        private int CalculateBackoffMinutes(int failureCount)
        {
            // 失敗 0 次: 5 分鐘
            // 失敗 1 次: 10 分鐘
            // 失敗 2 次: 20 分鐘
            // ...
            // 失敗 12 次+: 20480 分鐘 (上限)
            if (failureCount < 0) failureCount = 0;

            int backoffMinutes = BaseBackoffMinutes * (int)Math.Pow(2, failureCount);
            return Math.Min(backoffMinutes, MaxBackoffMinutes);
        }

        /// <summary>
        /// Checks if we should skip downloading for a specific App ID and language
        /// </summary>
        /// <param name="appId">The Steam App ID</param>
        /// <param name="language">Language code (use "english" for default/general)</param>
        /// <returns>True if we should skip download (recent failure within threshold days)</returns>
        public bool ShouldSkipDownload(int appId, string language = "english")
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

                    var languageElement = gameElement.Elements("Language")
                        .FirstOrDefault(l => l.Attribute("Code")?.Value == language);

                    if (languageElement == null)
                        return false;

                    var lastFailedStr = languageElement.Attribute("LastFailed")?.Value;
                    var failureCount = (int?)languageElement.Attribute("FailureCount") ?? 0;

                    if (DateTime.TryParse(lastFailedStr, out var lastFailed))
                    {
                        // Use exponential backoff based on failure count
                        var backoffMinutes = CalculateBackoffMinutes(failureCount);
                        var minutesSinceFailure = (DateTime.Now - lastFailed).TotalMinutes;

                        if (minutesSinceFailure <= backoffMinutes)
                        {
                            var hoursRemaining = (backoffMinutes - minutesSinceFailure) / 60.0;
                            DebugLogger.LogDebug($"Skipping download for {appId} ({language}) - failed {failureCount} times, retry in {hoursRemaining:F1} hours (backoff: {backoffMinutes} min)");
                            return true;
                        }
                        else
                        {
                            DebugLogger.LogDebug($"Retrying download for {appId} ({language}) - last failure was {minutesSinceFailure:F0} minutes ago (failed {failureCount} times)");
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error checking failed download for {appId} ({language}): {ex.Message}");
                }

                return false;
            }
        }

        /// <summary>
        /// Records a failed download attempt for specific language
        /// </summary>
        /// <param name="appId">The Steam App ID</param>
        /// <param name="language">Language code</param>
        /// <param name="gameName">Optional game name for reference</param>
        /// <param name="failedAt">Optional timestamp of when the failure occurred</param>
        public void RecordFailedDownload(int appId, string language = "english", string? gameName = null, DateTime? failedAt = null)
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
                        doc = new XDocument(new XElement("ImageFailures"));
                    }

                    var root = doc.Root;
                    if (root == null)
                    {
                        root = new XElement("ImageFailures");
                        doc.Add(root);
                    }

                    // Find or create game element
                    var gameElement = root.Elements("Game")
                        .FirstOrDefault(g => (int?)g.Attribute("AppId") == appId);

                    if (gameElement == null)
                    {
                        gameElement = new XElement("Game", new XAttribute("AppId", appId));
                        if (!string.IsNullOrEmpty(gameName))
                        {
                            gameElement.Add(new XAttribute("GameName", gameName));
                        }
                        root.Add(gameElement);
                    }
                    else if (!string.IsNullOrEmpty(gameName) && gameElement.Attribute("GameName") == null)
                    {
                        gameElement.Add(new XAttribute("GameName", gameName));
                    }

                    // Find or create language element
                    var languageElement = gameElement.Elements("Language")
                        .FirstOrDefault(l => l.Attribute("Code")?.Value == language);

                    var timestamp = (failedAt ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss");

                    if (languageElement == null)
                    {
                        // New failure record starts at count 1
                        languageElement = new XElement("Language",
                            new XAttribute("Code", language),
                            new XAttribute("LastFailed", timestamp),
                            new XAttribute("FailureCount", 1));
                        gameElement.Add(languageElement);
                    }
                    else
                    {
                        // Update existing record - increment failure count
                        var currentCount = (int?)languageElement.Attribute("FailureCount") ?? 0;
                        languageElement.SetAttributeValue("LastFailed", timestamp);
                        languageElement.SetAttributeValue("FailureCount", currentCount + 1);
                    }

                    // Save with backup mechanism
                    var tempPath = _xmlFilePath + ".tmp";
                    doc.Save(tempPath);
                    File.Move(tempPath, _xmlFilePath, true);

                    var finalCount = (int?)languageElement.Attribute("FailureCount") ?? 1;
                    var backoffMinutes = CalculateBackoffMinutes(finalCount);
                    DebugLogger.LogDebug($"Recorded failed download for {appId} ({language}) - {gameName ?? "unknown"} (failure count: {finalCount}, next retry in {backoffMinutes} min)");
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error recording failed download for {appId} ({language}): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Removes a failed download record for specific language (called when download succeeds)
        /// </summary>
        /// <param name="appId">The Steam App ID</param>
        /// <param name="language">Language code</param>
        public void RemoveFailedRecord(int appId, string language = "english")
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
                        var languageElement = gameElement.Elements("Language")
                            .FirstOrDefault(l => l.Attribute("Code")?.Value == language);

                        languageElement?.Remove();

                        // If no more language failures for this game, remove the game element entirely
                        if (!gameElement.Elements("Language").Any())
                        {
                            gameElement.Remove();
                        }

                        // Save the updated document
                        var tempPath = _xmlFilePath + ".tmp";
                        doc.Save(tempPath);
                        File.Move(tempPath, _xmlFilePath, true);

                        DebugLogger.LogDebug($"Removed failed download record for {appId} ({language}) - download now successful");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error removing failed download record for {appId} ({language}): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets all failed download records for debugging/maintenance
        /// </summary>
        /// <returns>List of failed download records</returns>
        public List<(int AppId, string Language, DateTime LastFailed, string? GameName)> GetFailedRecords()
        {
            lock (_lockObject)
            {
                var records = new List<(int AppId, string Language, DateTime LastFailed, string? GameName)>();

                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return records;

                    var doc = XDocument.Load(_xmlFilePath);
                    foreach (var gameElement in doc.Root?.Elements("Game") ?? Enumerable.Empty<XElement>())
                    {
                        var appId = (int?)gameElement.Attribute("AppId");
                        var gameName = gameElement.Attribute("GameName")?.Value;

                        if (appId.HasValue)
                        {
                            foreach (var langElement in gameElement.Elements("Language"))
                            {
                                var language = langElement.Attribute("Code")?.Value;
                                var lastFailedStr = langElement.Attribute("LastFailed")?.Value;

                                if (!string.IsNullOrEmpty(language) && DateTime.TryParse(lastFailedStr, out var lastFailed))
                                {
                                    records.Add((appId.Value, language, lastFailed, gameName));
                                }
                            }
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

                    var gamesToRemove = new List<XElement>();
                    
                    foreach (var gameElement in doc.Root?.Elements("Game") ?? Enumerable.Empty<XElement>())
                    {
                        var languagesToRemove = gameElement.Elements("Language")
                            .Where(l =>
                            {
                                var lastFailedStr = l.Attribute("LastFailed")?.Value;
                                return DateTime.TryParse(lastFailedStr, out var lastFailed) && lastFailed < cutoffDate;
                            })
                            .ToList();

                        foreach (var langElement in languagesToRemove)
                        {
                            langElement.Remove();
                            removedCount++;
                        }

                        // If no more language failures for this game, mark for removal
                        if (!gameElement.Elements("Language").Any())
                        {
                            gamesToRemove.Add(gameElement);
                        }
                    }

                    // Remove games with no language failures
                    foreach (var gameElement in gamesToRemove)
                    {
                        gameElement.Remove();
                    }

                    if (removedCount > 0)
                    {
                        var tempPath = _xmlFilePath + ".tmp";
                        doc.Save(tempPath);
                        File.Move(tempPath, _xmlFilePath, true);

                        DebugLogger.LogDebug($"Cleaned up {removedCount} old image failure records (older than 30 days)");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error cleaning up old image failure records: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Migrates old steam_games_failed.xml records to the new unified system
        /// </summary>
        public void MigrateOldFailedRecords()
        {
            var oldFilePath = Path.Combine(Path.GetDirectoryName(_xmlFilePath) ?? "", "steam_games_failed.xml");
            
            if (!File.Exists(oldFilePath))
                return;

            try
            {
                var oldDoc = XDocument.Load(oldFilePath);
                var migratedCount = 0;

                foreach (var gameElement in oldDoc.Root?.Elements("Game") ?? Enumerable.Empty<XElement>())
                {
                    var appId = (int?)gameElement.Attribute("AppId");
                    var gameName = gameElement.Attribute("GameName")?.Value;
                    var lastFailedStr = gameElement.Attribute("LastFailed")?.Value;

                    if (appId.HasValue && DateTime.TryParse(lastFailedStr, out var lastFailed))
                    {
                        // Migrate as English (default) language record using original timestamp
                        RecordFailedDownload(appId.Value, "english", gameName, lastFailed);
                        migratedCount++;
                    }
                }

                if (migratedCount > 0)
                {
                    // Backup the old file before deleting
                    File.Move(oldFilePath, oldFilePath + ".migrated", true);
                    DebugLogger.LogDebug($"Migrated {migratedCount} records from old steam_games_failed.xml");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error migrating old failed records: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the path to the image failures XML file
        /// </summary>
        public string GetXmlFilePath() => _xmlFilePath;
    }
}
