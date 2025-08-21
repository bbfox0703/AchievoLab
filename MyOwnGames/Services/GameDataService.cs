using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Threading.Tasks;

namespace MyOwnGames.Services
{
    public class GameDataService
    {
        private readonly string _xmlFilePath;

        public GameDataService()
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var appDataPath = Path.Combine(documentsPath, "MyOwnGames");
            Directory.CreateDirectory(appDataPath);
            _xmlFilePath = Path.Combine(appDataPath, "steam_games.xml");
        }

        public async Task SaveGamesToXmlAsync(List<SteamGame> games, string steamId64, string apiKey, string language = "tchinese")
        {
            try
            {
                var root = new XElement("SteamGames",
                    new XAttribute("SteamID64", steamId64),
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
                    ExportDate = DateTime.Parse(root.Attribute("ExportDate")?.Value ?? DateTime.MinValue.ToString()),
                    TotalGames = int.Parse(root.Attribute("TotalGames")?.Value ?? "0"),
                    Language = root.Attribute("Language")?.Value ?? "tchinese",
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
        public DateTime ExportDate { get; set; }
        public int TotalGames { get; set; }
        public string Language { get; set; } = "tchinese";
        public string ApiKeyHash { get; set; } = "";
    }
}