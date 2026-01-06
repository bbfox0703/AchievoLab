using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using AnSAM.Steam;

namespace AnSAM.Services
{
    /// <summary>
    /// Handles reconciliation of the global game list, user cache and Steam ownership.
    /// </summary>
    public static class GameCacheService
    {
        /// <summary>
        /// Loads the global game list, resolves owned games and updates the user cache.
        /// Uses union of: games.xml (global) ∪ usergames.xml (user cache) ∪ steam_games.xml (MyOwnGames).
        /// </summary>
        /// <param name="baseDir">Application data directory.</param>
        /// <param name="steam">Steam client used for ownership queries.</param>
        /// <param name="http">HttpClient used to download the game list.</param>
        public static async Task<IReadOnlyList<SteamAppData>> RefreshAsync(string baseDir, ISteamClient steam, HttpClient http)
        {
            Directory.CreateDirectory(baseDir);
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var userGamesPath = Path.Combine(cacheDir, "usergames.xml");
            var steamGamesPath = Path.Combine(cacheDir, "steam_games.xml");

            // Step 1: Load games.xml via three-tier fallback (download → cache → embedded)
            // This may throw if all three sources fail
            bool hasGamesXml = false;
            try
            {
                await GameListService.LoadAsync(cacheDir, http).ConfigureAwait(false);
                hasGamesXml = true;
            }
            catch (GameListDownloadException)
            {
                // games.xml failed - will check other sources
            }

            // Step 2 & 3 & 4: Build union of app IDs from all sources
            var ids = new HashSet<int>();

            // Add from games.xml (if loaded)
            if (hasGamesXml)
            {
                foreach (var game in GameListService.Games)
                {
                    ids.Add(game.Id);
                }
            }

            // Add from usergames.xml (last user-owned games cache)
            if (File.Exists(userGamesPath))
            {
                try
                {
                    var userDoc = XDocument.Load(userGamesPath);
                    foreach (var node in userDoc.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                    {
                        if (int.TryParse(node.Attribute("id")?.Value, out var id))
                            ids.Add(id);
                    }
#if DEBUG
                    CommonUtilities.DebugLogger.LogDebug($"Loaded {ids.Count} app IDs from usergames.xml");
#endif
                }
                catch (Exception ex)
                {
#if DEBUG
                    CommonUtilities.DebugLogger.LogDebug($"Failed to read usergames.xml: {ex.Message}");
#endif
                }
            }

            // Add from steam_games.xml (MyOwnGames complete list)
            if (File.Exists(steamGamesPath))
            {
                try
                {
                    // Use cross-process file lock to safely read steam_games.xml
                    using var fileLock = new CommonUtilities.CrossProcessFileLock(steamGamesPath);
                    if (await fileLock.TryAcquireAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    {
                        var steamDoc = XDocument.Load(steamGamesPath);
                        foreach (var node in steamDoc.Descendants("AppID"))
                        {
                            if (int.TryParse(node.Value, out var id))
                                ids.Add(id);
                        }
                        foreach (var node in steamDoc.Descendants("Game"))
                        {
                            if (int.TryParse(node.Attribute("AppID")?.Value, out var id))
                                ids.Add(id);
                        }
#if DEBUG
                        CommonUtilities.DebugLogger.LogDebug($"Loaded {ids.Count} total app IDs after adding steam_games.xml");
#endif
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    CommonUtilities.DebugLogger.LogDebug($"Failed to read steam_games.xml: {ex.Message}");
#endif
                }
            }

            // Step 6: If all sources failed, throw exception
            if (ids.Count == 0)
            {
                throw new InvalidOperationException(
                    "No game data available. All sources failed:\n" +
                    "- games.xml (download/cache/embedded)\n" +
                    "- usergames.xml (last session cache)\n" +
                    "- steam_games.xml (MyOwnGames data)\n\n" +
                    "Please check your network connection or run MyOwnGames to generate steam_games.xml.");
            }

#if DEBUG
            CommonUtilities.DebugLogger.LogDebug($"Using union of {ids.Count} app IDs from all sources");
#endif

            // Step 5: Query Steam client for ownership
            var result = new List<SteamAppData>();
            if (steam.Initialized)
            {
                foreach (var id in ids)
                {
                    uint appId = (uint)id;
                    if (!steam.IsSubscribedApp(appId))
                        continue;
                    var title = steam.GetAppData(appId, "name") ?? appId.ToString(CultureInfo.InvariantCulture);
                    result.Add(new SteamAppData(id, title));
                }

                try
                {
                    var tempPath = userGamesPath + ".tmp";
                    using (var writer = XmlWriter.Create(tempPath, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
                    {
                        writer.WriteStartElement("games");
                        foreach (var id in result.Select(g => g.AppId).Distinct().OrderBy(i => i))
                        {
                            writer.WriteStartElement("game");
                            writer.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }

                    if (File.Exists(userGamesPath))
                        File.Replace(tempPath, userGamesPath, null);
                    else
                        File.Move(tempPath, userGamesPath);
                }
                catch
                {
                    // Ignore cache failures
                }
            }
            else if (File.Exists(userGamesPath))
            {
                try
                {
                    var doc = XDocument.Load(userGamesPath);
                    var gamesById = GameListService.Games.ToDictionary(g => g.Id);
                    foreach (var node in doc.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                    {
                        if (!int.TryParse(node.Attribute("id")?.Value, out var id))
                            continue;
                        gamesById.TryGetValue(id, out var game);
                        var title = string.IsNullOrEmpty(game.Name) ? id.ToString(CultureInfo.InvariantCulture) : game.Name;
                        result.Add(new SteamAppData(id, title));
                    }
                }
                catch
                {
                    // Ignore corrupt cache
                }
            }

            return result;
        }

        /// <summary>
        /// Adds a single App ID to usergames.xml if the user owns it.
        /// </summary>
        /// <param name="baseDir">Application data directory.</param>
        /// <param name="steam">Steam client used for ownership verification.</param>
        /// <param name="appId">The App ID to add.</param>
        /// <returns>True if the app was verified and added, false otherwise.</returns>
        public static bool TryAddUserGame(string baseDir, ISteamClient steam, int appId)
        {
            if (!steam.Initialized)
                return false;

            // Verify user owns this game
            if (!steam.IsSubscribedApp((uint)appId))
                return false;

            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var userGamesPath = Path.Combine(cacheDir, "usergames.xml");

            try
            {
                // Read existing IDs
                var existingIds = new HashSet<int>();
                if (File.Exists(userGamesPath))
                {
                    try
                    {
                        var doc = XDocument.Load(userGamesPath);
                        foreach (var node in doc.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                        {
                            if (int.TryParse(node.Attribute("id")?.Value, out var id))
                                existingIds.Add(id);
                        }
                    }
                    catch
                    {
                        // Ignore corrupt file, will recreate
                    }
                }

                // Add new ID
                existingIds.Add(appId);

                // Write updated list
                var tempPath = userGamesPath + ".tmp";
                using (var writer = XmlWriter.Create(tempPath, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
                {
                    writer.WriteStartElement("games");
                    foreach (var id in existingIds.OrderBy(i => i))
                    {
                        writer.WriteStartElement("game");
                        writer.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }

                if (File.Exists(userGamesPath))
                    File.Replace(tempPath, userGamesPath, null);
                else
                    File.Move(tempPath, userGamesPath);

#if DEBUG
                CommonUtilities.DebugLogger.LogDebug($"Added App ID {appId} to usergames.xml");
#endif
                return true;
            }
            catch (Exception ex)
            {
#if DEBUG
                CommonUtilities.DebugLogger.LogDebug($"Failed to add App ID {appId} to usergames.xml: {ex.Message}");
#endif
                return false;
            }
        }
    }
}
