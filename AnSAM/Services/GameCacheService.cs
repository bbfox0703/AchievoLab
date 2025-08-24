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

            await GameListService.LoadAsync(cacheDir, http).ConfigureAwait(false);

            var result = new List<SteamAppData>();
            if (steam.Initialized)
            {
                var ids = new HashSet<int>(GameListService.Games.Select(g => g.Id));

                var steamGamesPath = Path.Combine(cacheDir, "steam_games.xml");
                if (File.Exists(steamGamesPath))
                {
                    try
                    {
                        var doc = XDocument.Load(steamGamesPath);
                        foreach (var node in doc.Descendants("AppID"))
                        {
                            if (int.TryParse(node.Value, out var id))
                                ids.Add(id);
                        }
                        foreach (var node in doc.Descendants("Game"))
                        {
                            if (int.TryParse(node.Attribute("AppID")?.Value, out var id))
                                ids.Add(id);
                        }
                    }
                    catch
                    {
                        // Ignore corrupt steam_games.xml
                    }
                }

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
    }
}
