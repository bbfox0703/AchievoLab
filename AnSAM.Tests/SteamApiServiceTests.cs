using System;
using System.Threading.Tasks;
using MyOwnGames;
using Xunit;

namespace AnSAM.Tests
{
    public class SteamApiServiceTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("0123456789ABCDEFG0123456789ABCDEF")] // non-hex
        [InlineData("0123456789ABCDEF0123456789ABCDE")] // too short
        public void Constructor_InvalidApiKey_ThrowsArgumentException(string apiKey)
        {
            Assert.Throws<ArgumentException>(() => new SteamApiService(apiKey));
        }

        [Fact]
        public async Task GetOwnedGamesAsync_InvalidSteamId_ThrowsArgumentException()
        {
            var service = new SteamApiService("0123456789ABCDEF0123456789ABCDEF");
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetOwnedGamesAsync("123"));
        }
    }
}
