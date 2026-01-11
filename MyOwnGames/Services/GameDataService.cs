using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using CommonUtilities;

namespace MyOwnGames.Services
{
    public class GameDataService
    {
        private readonly string _xmlFilePath;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private readonly CrossProcessFileLock _crossProcessLock;

        public GameDataService()
        {
            var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataPath = Path.Combine(basePath, "AchievoLab", "cache");
            Directory.CreateDirectory(appDataPath);
            _xmlFilePath = Path.Combine(appDataPath, "steam_games.xml");
            _crossProcessLock = new CrossProcessFileLock(_xmlFilePath);
        }

        /// <summary>
        /// Helper method to execute an action with cross-process file lock
        /// </summary>
        private async Task<T> WithFileLockAsync<T>(Func<Task<T>> action, bool isWrite = false)
        {
            await _fileLock.WaitAsync();
            try
            {
                // Acquire cross-process lock with appropriate timeout
                // Increased read timeout to reduce contention with batch writes
                var timeout = isWrite ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(30);

                await using var lockHandle = await _crossProcessLock.AcquireHandleAsync(timeout);
                if (!lockHandle.IsAcquired)
                {
                    if (isWrite)
                    {
                        throw new TimeoutException("Failed to acquire cross-process file lock for steam_games.xml (write operation)");
                    }
                    else
                    {
                        // For read operations, log warning but return default
                        AppLogger.LogDebug("Failed to acquire cross-process file lock for steam_games.xml (read operation), returning default");
                    }
                }
                return await action();
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task SaveGamesToXmlAsync(List<SteamGame> games, string steamId64, string apiKey, string language = "english")
        {
            await WithFileLockAsync(async () =>
            {
                var root = new XElement("SteamGames",
                    new XAttribute("SteamID64", steamId64),
                    new XAttribute("SteamIdHash", GetSteamIdHash(steamId64)),
                    new XAttribute("ExportDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    new XAttribute("TotalGames", games.Count),
                    new XAttribute("Language", language),
                    new XAttribute("ApiKeyHash", GetApiKeyHash(apiKey)),

                    games.Select(game => new XElement("Game",
                        new XAttribute("AppID", game.AppId),
                        new XAttribute("PlaytimeForever", game.PlaytimeForever),
                        new XElement("NameEN", game.NameEn ?? ""),
                        new XElement($"Name_{language}", game.NameLocalized ?? ""), // Language-specific name
                        new XElement("IconURL", game.IconUrl ?? "")
                    ))
                );

                await Task.Run(() => root.Save(_xmlFilePath));
                AppLogger.LogDebug($"Saved {games.Count} games to {_xmlFilePath}");
                return Task.CompletedTask;
            }, isWrite: true);
        }

        public async Task AppendGameAsync(SteamGame game, string steamId64, string apiKey, string language = "english")
        {
            // For backward compatibility, use batch with single game
            await AppendGamesAsync(new[] { game }, steamId64, apiKey, language);
        }

        /// <summary>
        /// Batch appends multiple games to reduce file lock contention.
        /// Much more efficient than calling AppendGameAsync repeatedly.
        /// </summary>
        public async Task AppendGamesAsync(IEnumerable<SteamGame> games, string steamId64, string apiKey, string language = "english")
        {
            var gamesList = games.ToList();
            if (gamesList.Count == 0)
                return;

            await WithFileLockAsync(async () =>
            {
                XDocument doc;
                XElement root;

                if (File.Exists(_xmlFilePath))
                {
                    doc = await Task.Run(() => XDocument.Load(_xmlFilePath));
                    root = doc.Root ?? new XElement("SteamGames");
                    if (doc.Root == null)
                        doc.Add(root);
                }
                else
                {
                    root = new XElement("SteamGames");
                    doc = new XDocument(root);
                }

                // Update metadata
                root.SetAttributeValue("SteamID64", steamId64);
                root.SetAttributeValue("SteamIdHash", GetSteamIdHash(steamId64));
                root.SetAttributeValue("ExportDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                root.SetAttributeValue("Language", language);
                root.SetAttributeValue("ApiKeyHash", GetApiKeyHash(apiKey));

                // Process all games in batch
                foreach (var game in gamesList)
                {
                    // Check if game already exists
                    var existing = root.Elements("Game")
                        .FirstOrDefault(x => x.Attribute("AppID")?.Value == game.AppId.ToString());

                    if (existing != null)
                    {
                        // Update existing game with new language data
                        existing.SetAttributeValue("PlaytimeForever", game.PlaytimeForever);

                        // Update or add English name
                        var nameEnElement = existing.Element("NameEN");
                        if (nameEnElement != null)
                            nameEnElement.Value = game.NameEn ?? string.Empty;
                        else
                            existing.Add(new XElement("NameEN", game.NameEn ?? string.Empty));

                        // Update or add language-specific name
                        var langElementName = $"Name_{language}";
                        var langElement = existing.Element(langElementName);
                        if (langElement != null)
                            langElement.Value = game.NameLocalized ?? string.Empty;
                        else
                            existing.Add(new XElement(langElementName, game.NameLocalized ?? string.Empty));

                        // Update or add icon URL
                        var iconElement = existing.Element("IconURL");
                        if (iconElement != null)
                            iconElement.Value = game.IconUrl ?? string.Empty;
                        else
                            existing.Add(new XElement("IconURL", game.IconUrl ?? string.Empty));
                    }
                    else
                    {
                        // Create new game element
                        var gameElement = new XElement("Game",
                            new XAttribute("AppID", game.AppId),
                            new XAttribute("PlaytimeForever", game.PlaytimeForever),
                            new XElement("NameEN", game.NameEn ?? string.Empty),
                            new XElement($"Name_{language}", game.NameLocalized ?? string.Empty),
                            new XElement("IconURL", game.IconUrl ?? string.Empty)
                        );

                        root.Add(gameElement);
                    }
                }

                // Update total count
                root.SetAttributeValue("TotalGames", root.Elements("Game").Count());

                await Task.Run(() => doc.Save(_xmlFilePath));
                AppLogger.LogDebug($"Batch appended {gamesList.Count} games to {_xmlFilePath}");
                return Task.CompletedTask;
            }, isWrite: true);
        }

        public async Task<List<SteamGame>> LoadGamesFromXmlAsync()
        {
            return await WithFileLockAsync(async () =>
            {
                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return new List<SteamGame>();

                    var doc = await Task.Run(() => XDocument.Load(_xmlFilePath));
                    var games = doc.Root?.Elements("Game")
                        .Select(element => new SteamGame
                        {
                            AppId = int.Parse(element.Attribute("AppID")?.Value ?? "0"),
                            PlaytimeForever = int.Parse(element.Attribute("PlaytimeForever")?.Value ?? "0"),
                            NameEn = element.Element("NameEN")?.Value ?? "",
                            // Legacy fallback - try NameLocalized first, then use English
                            NameLocalized = element.Element("NameLocalized")?.Value ??
                                          element.Element("NameEN")?.Value ?? "",
                            IconUrl = element.Element("IconURL")?.Value ?? ""
                        })
                        .ToList() ?? new List<SteamGame>();

                    AppLogger.LogDebug($"Loaded {games.Count} games from {_xmlFilePath}");
                    return games;
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error loading games from XML: {ex.Message}");
                    return new List<SteamGame>();
                }
            });
        }

        /// <summary>
        /// Loads games with multi-language support
        /// </summary>
        public async Task<List<MultiLanguageGameData>> LoadGamesWithLanguagesAsync()
        {
            return await WithFileLockAsync(async () =>
            {
                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return new List<MultiLanguageGameData>();

                    var doc = await Task.Run(() => XDocument.Load(_xmlFilePath));
                    var games = doc.Root?.Elements("Game")
                        .Select(element =>
                        {
                            var gameData = new MultiLanguageGameData
                            {
                                AppId = int.Parse(element.Attribute("AppID")?.Value ?? "0"),
                                PlaytimeForever = int.Parse(element.Attribute("PlaytimeForever")?.Value ?? "0"),
                                NameEn = element.Element("NameEN")?.Value ?? "",
                                IconUrl = element.Element("IconURL")?.Value ?? "",
                                LocalizedNames = new Dictionary<string, string>()
                            };

                            // Load all language-specific names
                            foreach (var nameElement in element.Elements().Where(e => e.Name.LocalName.StartsWith("Name_")))
                            {
                                var language = nameElement.Name.LocalName.Substring(5); // Remove "Name_" prefix
                                gameData.LocalizedNames[language] = nameElement.Value;
                            }

                            // Legacy support - if NameLocalized exists, treat as unknown language
                            var legacyLocalized = element.Element("NameLocalized")?.Value;
                            if (!string.IsNullOrEmpty(legacyLocalized))
                            {
                                gameData.LocalizedNames["legacy"] = legacyLocalized;
                            }

                            return gameData;
                        })
                        .ToList() ?? new List<MultiLanguageGameData>();

                    AppLogger.LogDebug($"Loaded {games.Count} games with multi-language support from {_xmlFilePath}");
                    return games;
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error loading multi-language games from XML: {ex.Message}");
                    return new List<MultiLanguageGameData>();
                }
            });
        }

        public async Task<(ISet<int> AppIds, int? ExpectedTotal)> LoadRetrievedAppIdsAsync()
        {
            return await WithFileLockAsync(async () =>
            {
                var appIds = new HashSet<int>();
                int? expectedTotal = null;

                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return (appIds, (int?)null);

                    var doc = await Task.Run(() => XDocument.Load(_xmlFilePath));
                    var root = doc.Root;
                    if (root == null) return (appIds, (int?)null);

                    appIds = root.Elements("Game")
                        .Select(e => int.Parse(e.Attribute("AppID")?.Value ?? "0"))
                        .ToHashSet();

                    if (int.TryParse(root.Attribute("Remaining")?.Value, out var remaining))
                    {
                        expectedTotal = appIds.Count + remaining;
                    }
                    else if (int.TryParse(root.Attribute("TotalGames")?.Value, out var total))
                    {
                        expectedTotal = total;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error loading retrieved app IDs: {ex.Message}");
                }

                return (appIds, expectedTotal);
            });
        }

        public async Task UpdateRemainingCountAsync(int remaining)
        {
            await WithFileLockAsync(async () =>
            {
                try
                {
                    XDocument doc;
                    XElement root;

                    if (File.Exists(_xmlFilePath))
                    {
                        doc = await Task.Run(() => XDocument.Load(_xmlFilePath));
                        root = doc.Root ?? new XElement("SteamGames");
                        if (doc.Root == null)
                            doc.Add(root);
                    }
                    else
                    {
                        root = new XElement("SteamGames");
                        doc = new XDocument(root);
                    }

                    if (remaining > 0)
                        root.SetAttributeValue("Remaining", remaining);
                    else
                        root.Attribute("Remaining")?.Remove();

                    await Task.Run(() => doc.Save(_xmlFilePath));
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error updating remaining count: {ex.Message}");
                }
                return Task.CompletedTask;
            }, isWrite: true);
        }

        public async Task<GameExportInfo?> GetExportInfoAsync()
        {
            return await WithFileLockAsync(async () =>
            {
                try
                {
                    if (!File.Exists(_xmlFilePath))
                        return (GameExportInfo?)null;

                    var doc = await Task.Run(() => XDocument.Load(_xmlFilePath));
                    var root = doc.Root;
                    if (root == null) return (GameExportInfo?)null;

                    return (GameExportInfo?)new GameExportInfo
                    {
                        SteamId64 = root.Attribute("SteamID64")?.Value ?? "",
                        SteamIdHash = root.Attribute("SteamIdHash")?.Value ?? "",
                        ExportDate = DateTime.Parse(root.Attribute("ExportDate")?.Value ?? DateTime.MinValue.ToString()),
                        TotalGames = int.Parse(root.Attribute("TotalGames")?.Value ?? "0"),
                        Language = root.Attribute("Language")?.Value ?? "english",
                        ApiKeyHash = root.Attribute("ApiKeyHash")?.Value ?? ""
                    };
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error getting export info: {ex.Message}");
                    return (GameExportInfo?)null;
                }
            });
        }

        public string GetXmlFilePath() => _xmlFilePath;

        public void ClearGameData()
        {
            try
            {
                if (File.Exists(_xmlFilePath))
                {
                    File.Delete(_xmlFilePath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error clearing game data: {ex.Message}");
            }
        }

        public string GetSteamIdHash(string steamId64)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(steamId64);
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private string GetApiKeyHash(string apiKey)
        {
            // Simple hash for privacy - don't store actual API key
            return apiKey.Length > 8 ? 
                $"{apiKey.Substring(0, 4)}****{apiKey.Substring(apiKey.Length - 4)}" : 
                "****";
        }
    }

    public class GameExportInfo
    {
        public string SteamId64 { get; set; } = "";
        public string SteamIdHash { get; set; } = "";
        public DateTime ExportDate { get; set; }
        public int TotalGames { get; set; }
        public string Language { get; set; } = "english";
        public string ApiKeyHash { get; set; } = "";
    }
}
