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
    /// <summary>
    /// Manages persistent storage of Steam game data in XML format with multi-language support.
    /// Provides thread-safe and cross-process file access using semaphores and file locking.
    /// </summary>
    /// <remarks>
    /// This service stores game data in %LOCALAPPDATA%\AchievoLab\cache\steam_games.xml.
    /// The XML format supports:
    /// - Multiple languages (stores English names + language-specific names)
    /// - Incremental updates (append games without rewriting entire file)
    /// - Batch operations (reduces file lock contention)
    /// - Metadata tracking (Steam ID, export date, API key hash)
    ///
    /// Thread safety is ensured via in-process semaphore and cross-process file locking.
    /// Read operations have 30-second timeout, write operations have 60-second timeout.
    /// </remarks>
    public class GameDataService
    {
        /// <summary>
        /// Full path to the XML file storing game data.
        /// Located at %LOCALAPPDATA%\AchievoLab\cache\steam_games.xml.
        /// </summary>
        private readonly string _xmlFilePath;

        /// <summary>
        /// In-process semaphore ensuring only one thread accesses the file at a time.
        /// Prevents race conditions within the same process.
        /// </summary>
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        /// <summary>
        /// Cross-process file lock preventing concurrent access from multiple MyOwnGames instances.
        /// Uses Windows file locking mechanisms to coordinate access across processes.
        /// </summary>
        private readonly CrossProcessFileLock _crossProcessLock;

        /// <summary>
        /// Initializes a new instance of <see cref="GameDataService"/>.
        /// Creates the cache directory if it doesn't exist and sets up file locking.
        /// </summary>
        /// <remarks>
        /// The constructor:
        /// - Resolves %LOCALAPPDATA%\AchievoLab\cache path
        /// - Creates the directory structure if needed
        /// - Initializes cross-process file lock for steam_games.xml
        /// </remarks>
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

        /// <summary>
        /// Saves a complete list of games to the XML file, replacing any existing data.
        /// Stores both English and localized names along with metadata.
        /// </summary>
        /// <param name="games">List of Steam games to save.</param>
        /// <param name="steamId64">Steam ID of the user who owns these games.</param>
        /// <param name="apiKey">Steam Web API key (will be hashed for privacy).</param>
        /// <param name="language">Target language for localized names (default: "english").</param>
        /// <returns>A task representing the asynchronous save operation.</returns>
        /// <remarks>
        /// This method creates a new XML file with:
        /// - SteamID64 and its SHA256 hash
        /// - Export timestamp
        /// - Total game count
        /// - Language identifier
        /// - API key hash (for verification without storing actual key)
        /// - Game entries with AppID, playtime, English name, localized name, and icon URL
        ///
        /// The operation uses a 60-second timeout for the cross-process file lock.
        /// </remarks>
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

        /// <summary>
        /// Appends a single game to the XML file or updates it if it already exists.
        /// This is a convenience wrapper around <see cref="AppendGamesAsync"/> for backward compatibility.
        /// </summary>
        /// <param name="game">Steam game to append or update.</param>
        /// <param name="steamId64">Steam ID of the user who owns this game.</param>
        /// <param name="apiKey">Steam Web API key (will be hashed for privacy).</param>
        /// <param name="language">Target language for localized name (default: "english").</param>
        /// <returns>A task representing the asynchronous append operation.</returns>
        /// <remarks>
        /// For better performance when adding multiple games, use <see cref="AppendGamesAsync"/> instead
        /// to reduce file lock contention.
        /// </remarks>
        public async Task AppendGameAsync(SteamGame game, string steamId64, string apiKey, string language = "english")
        {
            // For backward compatibility, use batch with single game
            await AppendGamesAsync(new[] { game }, steamId64, apiKey, language);
        }

        /// <summary>
        /// Batch appends multiple games to reduce file lock contention.
        /// Much more efficient than calling AppendGameAsync repeatedly.
        /// </summary>
        /// <param name="games">Collection of Steam games to append or update.</param>
        /// <param name="steamId64">Steam ID of the user who owns these games.</param>
        /// <param name="apiKey">Steam Web API key (will be hashed for privacy).</param>
        /// <param name="language">Target language for localized names (default: "english").</param>
        /// <returns>A task representing the asynchronous batch append operation.</returns>
        /// <remarks>
        /// This method:
        /// - Loads existing XML or creates new if file doesn't exist
        /// - For each game, either updates existing entry or creates new one
        /// - Preserves existing language-specific names when adding new language data
        /// - Updates metadata (Steam ID, export date, total count)
        /// - Writes entire document atomically
        ///
        /// Multi-language support: Games can have multiple Name_{language} elements.
        /// For example, Name_english, Name_tchinese, Name_japanese.
        ///
        /// Performance: This method acquires the file lock once for all games,
        /// making it much faster than individual AppendGameAsync calls.
        /// </remarks>
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

        /// <summary>
        /// Loads all games from the XML file, returning a simplified list with English and legacy localized names.
        /// </summary>
        /// <returns>List of Steam games with English names and legacy localized names, or empty list if file doesn't exist.</returns>
        /// <remarks>
        /// This method returns <see cref="SteamGame"/> objects with:
        /// - AppID, playtime, English name, icon URL
        /// - NameLocalized field populated from legacy NameLocalized element (backward compatibility)
        ///
        /// For multi-language support with access to all language-specific names,
        /// use <see cref="LoadGamesWithLanguagesAsync"/> instead.
        ///
        /// The operation uses a 30-second timeout for the cross-process file lock.
        /// Returns empty list on error or if file doesn't exist.
        /// </remarks>
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

        /// <summary>
        /// Loads the set of AppIDs that have already been retrieved and the expected total game count.
        /// Used to track progress during incremental game data retrieval.
        /// </summary>
        /// <returns>
        /// A tuple containing:
        /// - AppIds: HashSet of already-retrieved AppIDs
        /// - ExpectedTotal: Expected total number of games (calculated from Remaining attribute or TotalGames attribute)
        /// </returns>
        /// <remarks>
        /// This method is used during incremental retrieval to:
        /// - Skip games that have already been retrieved
        /// - Calculate progress percentage (retrieved count / expected total)
        ///
        /// The expected total is calculated from:
        /// 1. "Remaining" attribute (if present): current count + remaining = total
        /// 2. "TotalGames" attribute (fallback)
        ///
        /// Returns empty set and null if file doesn't exist or on error.
        /// The operation uses a 30-second timeout for the cross-process file lock.
        /// </remarks>
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

        /// <summary>
        /// Updates the "Remaining" attribute in the XML file to track how many games are left to retrieve.
        /// Used during incremental game data retrieval to update progress.
        /// </summary>
        /// <param name="remaining">Number of games remaining to retrieve. If 0 or negative, the attribute is removed.</param>
        /// <returns>A task representing the asynchronous update operation.</returns>
        /// <remarks>
        /// This method:
        /// - Loads existing XML or creates new if file doesn't exist
        /// - Sets or removes the "Remaining" attribute based on the value
        /// - Preserves all other data and attributes
        ///
        /// Used in conjunction with <see cref="LoadRetrievedAppIdsAsync"/> to track retrieval progress.
        /// The operation uses a 60-second timeout for the cross-process file lock.
        /// </remarks>
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

        /// <summary>
        /// Retrieves metadata about the exported game data from the XML file.
        /// </summary>
        /// <returns>
        /// A <see cref="GameExportInfo"/> object containing metadata, or null if file doesn't exist or on error.
        /// </returns>
        /// <remarks>
        /// The returned <see cref="GameExportInfo"/> includes:
        /// - SteamID64: The Steam ID that owns these games
        /// - SteamIdHash: SHA256 hash of the Steam ID
        /// - ExportDate: When the data was last exported
        /// - TotalGames: Total number of games stored
        /// - Language: Language used for localized names
        /// - ApiKeyHash: Masked API key (e.g., "ABCD****WXYZ")
        ///
        /// This is useful for:
        /// - Verifying the data is for the correct Steam account
        /// - Checking if data needs to be refreshed
        /// - Displaying export metadata to the user
        ///
        /// The operation uses a 30-second timeout for the cross-process file lock.
        /// </remarks>
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

        /// <summary>
        /// Gets the full path to the XML file used for storing game data.
        /// </summary>
        /// <returns>Full path to steam_games.xml in %LOCALAPPDATA%\AchievoLab\cache.</returns>
        public string GetXmlFilePath() => _xmlFilePath;

        /// <summary>
        /// Deletes the XML file containing all game data.
        /// Used when the user wants to clear cached data or switch Steam accounts.
        /// </summary>
        /// <remarks>
        /// This operation does not use file locking, so it should only be called
        /// when no other operations are in progress. Errors are logged but not thrown.
        /// </remarks>
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

        /// <summary>
        /// Computes a SHA256 hash of the Steam ID for privacy-preserving storage.
        /// Used to verify data ownership without exposing the actual Steam ID.
        /// </summary>
        /// <param name="steamId64">Steam ID to hash (17-digit number).</param>
        /// <returns>Lowercase hexadecimal SHA256 hash of the Steam ID.</returns>
        public string GetSteamIdHash(string steamId64)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(steamId64);
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Creates a masked representation of the API key for privacy.
        /// Shows only first 4 and last 4 characters, masking the middle.
        /// </summary>
        /// <param name="apiKey">Steam Web API key to mask.</param>
        /// <returns>Masked API key in format "ABCD****WXYZ" or "****" if key is too short.</returns>
        /// <remarks>
        /// This prevents the actual API key from being stored in the XML file
        /// while still allowing verification that the same key was used.
        /// </remarks>
        private string GetApiKeyHash(string apiKey)
        {
            // Simple hash for privacy - don't store actual API key
            return apiKey.Length > 8 ?
                $"{apiKey.Substring(0, 4)}****{apiKey.Substring(apiKey.Length - 4)}" :
                "****";
        }
    }

    /// <summary>
    /// Contains metadata about an exported game collection from the XML file.
    /// Provides information about when and how the data was retrieved.
    /// </summary>
    public class GameExportInfo
    {
        /// <summary>
        /// Gets or sets the Steam ID (17-digit number) that owns these games.
        /// </summary>
        public string SteamId64 { get; set; } = "";

        /// <summary>
        /// Gets or sets the SHA256 hash of the Steam ID for verification without exposing the actual ID.
        /// </summary>
        public string SteamIdHash { get; set; } = "";

        /// <summary>
        /// Gets or sets the date and time when the game data was exported.
        /// </summary>
        public DateTime ExportDate { get; set; }

        /// <summary>
        /// Gets or sets the total number of games in the collection.
        /// </summary>
        public int TotalGames { get; set; }

        /// <summary>
        /// Gets or sets the language used for localized game names.
        /// Examples: "english", "tchinese", "japanese", "korean".
        /// </summary>
        public string Language { get; set; } = "english";

        /// <summary>
        /// Gets or sets the masked API key used for retrieval.
        /// Format: "ABCD****WXYZ" (only first and last 4 characters visible).
        /// </summary>
        public string ApiKeyHash { get; set; } = "";
    }
}
