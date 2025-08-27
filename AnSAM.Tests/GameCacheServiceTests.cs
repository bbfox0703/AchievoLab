using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AnSAM.Services;
using AnSAM.Steam;
using CommonUtilities;
using Xunit;

public class GameCacheServiceTests
{
    private sealed class StubSteamClient : ISteamClient
    {
        public bool Initialized => true;
        public bool IsSubscribedApp(uint appId) => appId == 2;
        public string? GetAppData(uint appId, string key) => appId == 2 && key == "name" ? "Two" : null;
    }

    private sealed class ExtraSteamClient : ISteamClient
    {
        public bool Initialized => true;
        public bool IsSubscribedApp(uint appId) => appId == 570;
        public string? GetAppData(uint appId, string key) => appId == 570 && key == "name" ? "Dota 2" : null;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string xml = "<games><game>1</game><game>2</game></games>";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task RefreshUpdatesUserGamesCache()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var userGamesPath = Path.Combine(cacheDir, "usergames.xml");
            File.WriteAllText(userGamesPath, "<games><game id=\"1\" /></games>");

            var steam = new StubSteamClient();
            using var http = new HttpClient(new StubHandler());

            var apps = await GameCacheService.RefreshAsync(baseDir, steam, http);
            Assert.Collection(apps, a => Assert.Equal(2, a.AppId));

            var doc = XDocument.Load(userGamesPath);
            var ids = doc.Root?.Elements("game").Select(g => (int?)g.Attribute("id")).Where(i => i.HasValue).Select(i => i!.Value).ToArray();
            Assert.Equal(new[] { 2 }, ids);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SteamGamesXmlIdsAreProcessedAndImagesRetrieved()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);

            var steamGamesPath = Path.Combine(cacheDir, "steam_games.xml");
            File.WriteAllText(steamGamesPath, "<SteamGames><Game><AppID>570</AppID></Game></SteamGames>");

            var steam = new ExtraSteamClient();
            using var http = new HttpClient(new StubHandler());

            var apps = await GameCacheService.RefreshAsync(baseDir, steam, http);
            Assert.Contains(apps, a => a.AppId == 570);

            var userGamesPath = Path.Combine(cacheDir, "usergames.xml");
            var doc = XDocument.Load(userGamesPath);
            var ids = doc.Root?.Elements("game").Select(g => (int?)g.Attribute("id")).Where(i => i.HasValue).Select(i => i!.Value).ToArray();
            Assert.NotNull(ids);
            Assert.Contains(570, ids!);

            var tracker = new ImageFailureTrackingService();
            tracker.RemoveFailedRecord(570, "english");

            var imageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AchievoLab", "ImageCache", "english");
            Directory.CreateDirectory(imageDir);
            var cachedImagePath = Path.Combine(imageDir, "570.png");
            var pngData = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");
            File.WriteAllBytes(cachedImagePath, pngData);

            var service = new SharedImageService(new HttpClient(), disposeHttpClient: true);
            var path = await service.GetGameImageAsync(570);
            Assert.Equal(cachedImagePath, path);
            service.Dispose();
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }
}
