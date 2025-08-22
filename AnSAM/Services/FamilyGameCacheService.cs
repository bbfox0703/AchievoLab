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
    /// Handles reconciliation of Family Sharing games, user cache and Steam Family Sharing status.
    /// </summary>
    public static class FamilyGameCacheService
    {
        /// <summary>
        /// Loads the global game list, resolves Family Sharing games and updates the family cache.
        /// </summary>
        /// <param name="baseDir">Application data directory.</param>
        /// <param name="steam">Steam client used for Family Sharing queries.</param>
        /// <param name="http">HttpClient used to download the game list.</param>
        public static async Task<IReadOnlyList<SteamAppData>> RefreshAsync(string baseDir, ISteamClient steam, HttpClient http)
        {
            Directory.CreateDirectory(baseDir);
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var familyGamesPath = Path.Combine(cacheDir, "familygames.xml");

            await GameListService.LoadAsync(cacheDir, http).ConfigureAwait(false);

            var result = new List<SteamAppData>();
            if (steam.Initialized)
            {
                foreach (var game in GameListService.Games)
                {
                    uint appId = (uint)game.Id;
                    
                    // Check if this game is available via Family Sharing
                    if (!steam.IsSubscribedFromFamilySharing(appId))
                        continue;
                        
                    var title = steam.GetAppData(appId, "name") ?? appId.ToString(CultureInfo.InvariantCulture);
                    var owner = steam.GetAppOwner(appId);
                    
                    // Add Family Sharing indicator to title if we have owner info
                    if (owner != 0)
                    {
                        title = $"{title} (Family Shared)";
                    }
                    
                    result.Add(new SteamAppData(game.Id, title));
                }

                try
                {
                    var tempPath = familyGamesPath + ".tmp";
                    using (var writer = XmlWriter.Create(tempPath, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
                    {
                        writer.WriteStartElement("familygames");
                        foreach (var game in result.OrderBy(g => g.AppId))
                        {
                            writer.WriteStartElement("game");
                            writer.WriteAttributeString("id", game.AppId.ToString(CultureInfo.InvariantCulture));
                            writer.WriteAttributeString("title", game.Title);
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }

                    if (File.Exists(familyGamesPath))
                        File.Replace(tempPath, familyGamesPath, null);
                    else
                        File.Move(tempPath, familyGamesPath);
                }
                catch
                {
                    // Ignore cache failures
                }
            }
            else if (File.Exists(familyGamesPath))
            {
                try
                {
                    var doc = XDocument.Load(familyGamesPath);
                    var gamesById = GameListService.Games.ToDictionary(g => g.Id);
                    foreach (var node in doc.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                    {
                        if (!int.TryParse(node.Attribute("id")?.Value, out var id))
                            continue;
                        
                        var title = node.Attribute("title")?.Value;
                        if (string.IsNullOrEmpty(title))
                        {
                            gamesById.TryGetValue(id, out var game);
                            title = string.IsNullOrEmpty(game.Name) ? id.ToString(CultureInfo.InvariantCulture) : game.Name;
                            title = $"{title} (Family Shared)";
                        }
                        
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