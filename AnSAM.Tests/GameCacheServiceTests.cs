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
            try { Directory.Delete(baseDir, true); }
            catch
            {
                // Ignore cleanup failures in test teardown
            }
        }
    }

    [Fact]
    public async Task SteamGamesXmlIdsAreProcessedCorrectly()
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
        }
        finally
        {
            try { Directory.Delete(baseDir, true); }
            catch
            {
                // Ignore cleanup failures in test teardown
            }
        }
    }

    [Fact]
    public async Task UnionStrategyMergesAllSources()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);

            // Setup three sources with different App IDs
            // games.xml contributes: 1, 2
            // usergames.xml contributes: 2, 3 (2 is duplicate)
            // steam_games.xml contributes: 3, 4 (3 is duplicate)

            var userGamesPath = Path.Combine(cacheDir, "usergames.xml");
            File.WriteAllText(userGamesPath, "<games><game id=\"2\" /><game id=\"3\" /></games>");

            var steamGamesPath = Path.Combine(cacheDir, "steam_games.xml");
            File.WriteAllText(steamGamesPath, "<SteamGames><Game><AppID>3</AppID></Game><Game><AppID>4</AppID></Game></SteamGames>");

            var steam = new StubSteamClient(); // Only owns App ID 2
            using var http = new HttpClient(new StubHandler()); // Returns App IDs 1, 2

            var apps = await GameCacheService.RefreshAsync(baseDir, steam, http);

            // Should only return App ID 2 (the only one owned according to StubSteamClient)
            Assert.Collection(apps, a => Assert.Equal(2, a.AppId));

            // But usergames.xml should be updated with only owned games
            var doc = XDocument.Load(userGamesPath);
            var ids = doc.Root?.Elements("game").Select(g => (int?)g.Attribute("id")).Where(i => i.HasValue).Select(i => i!.Value).ToArray();
            Assert.Equal(new[] { 2 }, ids);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); }
            catch
            {
                // Ignore cleanup failures in test teardown
            }
        }
    }

    [Fact]
    public void TryAddUserGameAddsNewAppIdWhenOwned()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var steam = new StubSteamClient(); // Owns App ID 2

            // Add App ID 2 (owned)
            var added = GameCacheService.TryAddUserGame(baseDir, steam, 2);
            Assert.True(added);

            // Verify it was written to usergames.xml
            var userGamesPath = Path.Combine(baseDir, "cache", "usergames.xml");
            Assert.True(File.Exists(userGamesPath));

            var doc = XDocument.Load(userGamesPath);
            var ids = doc.Root?.Elements("game").Select(g => (int?)g.Attribute("id")).Where(i => i.HasValue).Select(i => i!.Value).ToArray();
            Assert.Equal(new[] { 2 }, ids);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); }
            catch
            {
                // Ignore cleanup failures in test teardown
            }
        }
    }

    [Fact]
    public void TryAddUserGameReturnsFalseWhenNotOwned()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var steam = new StubSteamClient(); // Only owns App ID 2

            // Try to add App ID 999 (not owned)
            var added = GameCacheService.TryAddUserGame(baseDir, steam, 999);
            Assert.False(added);

            // Verify usergames.xml was not created
            var userGamesPath = Path.Combine(baseDir, "cache", "usergames.xml");
            Assert.False(File.Exists(userGamesPath));
        }
        finally
        {
            try { Directory.Delete(baseDir, true); }
            catch
            {
                // Ignore cleanup failures in test teardown
            }
        }
    }

    [Fact]
    public void TryAddUserGamePreservesExistingIds()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var userGamesPath = Path.Combine(cacheDir, "usergames.xml");

            // Pre-populate with App ID 2
            File.WriteAllText(userGamesPath, "<games><game id=\"2\" /></games>");

            var steam = new StubSteamClient(); // Owns App ID 2

            // Add App ID 2 again (should be idempotent)
            var added = GameCacheService.TryAddUserGame(baseDir, steam, 2);
            Assert.True(added);

            // Verify no duplicates
            var doc = XDocument.Load(userGamesPath);
            var ids = doc.Root?.Elements("game").Select(g => (int?)g.Attribute("id")).Where(i => i.HasValue).Select(i => i!.Value).ToArray();
            Assert.Equal(new[] { 2 }, ids);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); }
            catch
            {
                // Ignore cleanup failures in test teardown
            }
        }
    }

    [Fact]
    public void TryAddUserGameReturnsFalseWhenSteamNotInitialized()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var steam = new UninitializedSteamClient();

            var added = GameCacheService.TryAddUserGame(baseDir, steam, 2);
            Assert.False(added);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); }
            catch
            {
                // Ignore cleanup failures in test teardown
            }
        }
    }

    private sealed class UninitializedSteamClient : ISteamClient
    {
        public bool Initialized => false;
        public bool IsSubscribedApp(uint appId) => false;
        public string? GetAppData(uint appId, string key) => null;
    }
}
