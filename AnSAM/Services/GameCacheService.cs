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
using CommonUtilities;

namespace AnSAM.Services
{
    /// <summary>
    /// Handles reconciliation of the global game list, user cache and Steam ownership.
    /// </summary>
    public static class GameCacheService
    {
        /// <summary>
        /// Loads the global game list, resolves owned games and updates the user cache.
        /// Uses union of: games.xml (global) ??usergames.xml (user cache) ??steam_games.xml (MyOwnGames).
        /// </summary>
        /// <param name="baseDir">Application data directory.</param>
        /// <param name="steam">Steam client used for ownership queries.</param>
        /// <param name="http">HttpClient used to download the game list.</param>
        public static async Task<IReadOnlyList<SteamAppData>> RefreshAsync(string baseDir, ISteamClient steam, HttpClient http)
        {
            try
            {
                Directory.CreateDirectory(baseDir);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Failed to create base directory '{baseDir}': {ex.GetType().Name} - {ex.Message}");
                throw;
            }

            var cacheDir = Path.Combine(baseDir, "cache");
            try
            {
                Directory.CreateDirectory(cacheDir);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Failed to create cache directory '{cacheDir}': {ex.GetType().Name} - {ex.Message}");
                throw;
            }

            var userGamesPath = Path.Combine(cacheDir, "usergames.xml");
            var steamGamesPath = Path.Combine(cacheDir, "steam_games.xml");

            // Step 1: Load games.xml via three-tier fallback (download ??cache ??embedded)
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

            // The user's own games (games.xml is the global catalog), kept as the fallback list
            // for when Steam cannot confirm ownership.
            var cachedIds = new HashSet<int>();
            var cachedTitles = new Dictionary<int, string>();

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
                        {
                            ids.Add(id);
                            cachedIds.Add(id);
                        }
                    }
#if DEBUG
                    CommonUtilities.AppLogger.LogDebug($"Loaded {ids.Count} app IDs from usergames.xml");
#endif
                }
#if DEBUG
                catch (Exception ex)
                {
                    CommonUtilities.AppLogger.LogDebug($"Failed to read usergames.xml: {ex.Message}");
                }
#else
                catch { }
#endif
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
                                AddCachedGame(id, node.Parent);
                        }
                        foreach (var node in steamDoc.Descendants("Game"))
                        {
                            if (int.TryParse(node.Attribute("AppID")?.Value, out var id))
                                AddCachedGame(id, node);
                        }

                        void AddCachedGame(int id, XElement? game)
                        {
                            ids.Add(id);
                            cachedIds.Add(id);
                            var name = game?.Element("NameEN")?.Value;
                            if (!string.IsNullOrEmpty(name))
                                cachedTitles[id] = name;
                        }
#if DEBUG
                        CommonUtilities.AppLogger.LogDebug($"Loaded {ids.Count} total app IDs after adding steam_games.xml");
#endif
                    }
                }
#if DEBUG
                catch (Exception ex)
                {
                    CommonUtilities.AppLogger.LogDebug($"Failed to read steam_games.xml: {ex.Message}");
                }
#else
                catch { }
#endif
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
            CommonUtilities.AppLogger.LogDebug($"Using union of {ids.Count} app IDs from all sources");
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
            }

            // No owned games at all means Steam cannot answer, not that the library is empty: at its
            // sign-in prompt the pipe and user still connect but every ownership query returns false.
            // Show the cached list and leave usergames.xml alone so the answer does not overwrite it.
            if (result.Count == 0)
            {
                AppLogger.LogDebug($"Steam confirmed no owned games out of {ids.Count} app IDs (initialized={steam.Initialized}); using {cachedIds.Count} cached games");
                foreach (var id in cachedIds)
                {
                    var title = cachedTitles.TryGetValue(id, out var name) ? name : id.ToString(CultureInfo.InvariantCulture);
                    result.Add(new SteamAppData(id, title, OwnershipVerified: false));
                }
                return result;
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

            return result;
        }

        /// <summary>
        /// Explains why Steam could not confirm the game list, for display to the user.
        /// </summary>
        /// <param name="steamInitialized">Whether the Steam client connected.</param>
        /// <param name="apps">The list returned by <see cref="RefreshAsync"/>.</param>
        /// <returns>The message to show, or null when Steam confirmed the list.</returns>
        public static string? DescribeSteamProblem(bool steamInitialized, IReadOnlyList<SteamAppData> apps)
        {
            if (steamInitialized && apps.Any(a => a.OwnershipVerified))
                return null;

            var problem = steamInitialized
                ? "Steam is running but not signed in, or sign-in failed. Sign in to Steam, then reopen AnSAM."
                : "Steam is not running. Start Steam and sign in, then reopen AnSAM.";

            return apps.Count > 0
                ? $"{problem} Showing {apps.Count} cached games; ownership not verified."
                : $"{problem} No cached games to show.";
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
                CommonUtilities.AppLogger.LogDebug($"Added App ID {appId} to usergames.xml");
#endif
                return true;
            }
#if DEBUG
            catch (Exception ex)
            {
                CommonUtilities.AppLogger.LogDebug($"Failed to add App ID {appId} to usergames.xml: {ex.Message}");
#else
            catch
            {
#endif
                return false;
            }
        }
    }
}
