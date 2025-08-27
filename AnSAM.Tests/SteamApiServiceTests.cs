using System;
using System.Net;
using System.Net.Http;
using System.Threading;
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
            using var service = new SteamApiService("0123456789ABCDEF0123456789ABCDEF");
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetOwnedGamesAsync("123"));
        }

        [Fact]
        public void Dispose_DisposesUnderlyingHandler()
        {
            var handler = new TestHandler();
            var client = new HttpClient(handler);
            var service = new SteamApiService("0123456789ABCDEF0123456789ABCDEF", client, true);

            service.Dispose();

            Assert.True(handler.Disposed);
        }

        private class TestHandler : HttpMessageHandler
        {
            public bool Disposed { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Disposed = true;
                }
                base.Dispose(disposing);
            }
        }
    }
}
