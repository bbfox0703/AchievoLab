using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

namespace MyOwnGames.Services
{
    public class GameDataService
    {
        private readonly string _xmlFilePath;

        public GameDataService()
        {
            var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataPath = Path.Combine(basePath, "AchievoLab", "cache");
            Directory.CreateDirectory(appDataPath);
            _xmlFilePath = Path.Combine(appDataPath, "steam_games.xml");
        }

        public async Task SaveGamesToXmlAsync(List<SteamGame> games, string steamId64, string apiKey, string language = "english")
        {
            try
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
                        new XElement("NameLocalized", game.NameLocalized ?? ""),
                        new XElement("IconURL", game.IconUrl ?? "")
                    ))
                );

                await Task.Run(() => root.Save(_xmlFilePath));
                DebugLogger.LogDebug($"Saved {games.Count} games to {_xmlFilePath}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error saving games to XML: {ex.Message}", ex);
            }
        }

        public async Task AppendGameAsync(SteamGame game, string steamId64, string apiKey, string language = "english")
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

                // Update metadata
                root.SetAttributeValue("SteamID64", steamId64);
                root.SetAttributeValue("SteamIdHash", GetSteamIdHash(steamId64));
                root.SetAttributeValue("ExportDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                root.SetAttributeValue("Language", language);
                root.SetAttributeValue("ApiKeyHash", GetApiKeyHash(apiKey));

                // Check if game already exists
                var existing = root.Elements("Game")
                    .FirstOrDefault(x => x.Attribute("AppID")?.Value == game.AppId.ToString());

                var gameElement = new XElement("Game",
                    new XAttribute("AppID", game.AppId),
                    new XAttribute("PlaytimeForever", game.PlaytimeForever),
                    new XElement("NameEN", game.NameEn ?? string.Empty),
                    new XElement("NameLocalized", game.NameLocalized ?? string.Empty),
                    new XElement("IconURL", game.IconUrl ?? string.Empty)
                );

                if (existing != null)
                {
                    existing.ReplaceWith(gameElement);
                }
                else
                {
                    root.Add(gameElement);
                }

                // Update total count
                root.SetAttributeValue("TotalGames", root.Elements("Game").Count());

                await Task.Run(() => doc.Save(_xmlFilePath));
                DebugLogger.LogDebug($"Appended game {game.AppId} to {_xmlFilePath}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error appending game to XML: {ex.Message}", ex);
            }
        }

        public async Task<List<SteamGame>> LoadGamesFromXmlAsync()
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
                        NameLocalized = element.Element("NameLocalized")?.Value ?? "",
                        IconUrl = element.Element("IconURL")?.Value ?? ""
                    })
                    .ToList() ?? new List<SteamGame>();

                DebugLogger.LogDebug($"Loaded {games.Count} games from {_xmlFilePath}");
                return games;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading games from XML: {ex.Message}");
                return new List<SteamGame>();
            }
        }

        public async Task<(ISet<int> AppIds, int? ExpectedTotal)> LoadRetrievedAppIdsAsync()
        {
            var appIds = new HashSet<int>();
            int? expectedTotal = null;

            try
            {
                if (!File.Exists(_xmlFilePath))
                    return (appIds, null);

                var doc = await Task.Run(() => XDocument.Load(_xmlFilePath));
                var root = doc.Root;
                if (root == null) return (appIds, null);

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
                DebugLogger.LogDebug($"Error loading retrieved app IDs: {ex.Message}");
            }

            return (appIds, expectedTotal);
        }

        public async Task UpdateRemainingCountAsync(int remaining)
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
                DebugLogger.LogDebug($"Error updating remaining count: {ex.Message}");
            }
        }

        public async Task<GameExportInfo?> GetExportInfoAsync()
        {
            try
            {
                if (!File.Exists(_xmlFilePath))
                    return null;

                var doc = await Task.Run(() => XDocument.Load(_xmlFilePath));
                var root = doc.Root;
                if (root == null) return null;

                return new GameExportInfo
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
                DebugLogger.LogDebug($"Error getting export info: {ex.Message}");
                return null;
            }
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
                DebugLogger.LogDebug($"Error clearing game data: {ex.Message}");
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