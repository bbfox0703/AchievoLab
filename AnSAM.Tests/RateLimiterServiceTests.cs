using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MyOwnGames.Services;
using Xunit;

namespace AnSAM.Tests
{
    public class RateLimiterServiceTests
    {
        [Fact]
        public async Task WaitAsync_MultipleConcurrentCalls_EnforcesMinimumInterval()
        {
            var options = new RateLimiterOptions
            {
                MaxCallsPerMinute = 100,
                JitterMinSeconds = 1.5,
                JitterMaxSeconds = 1.5
            };
            using var rateLimiter = new RateLimiterService(options);

            var stopwatch = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                await rateLimiter.WaitAsync();
                return stopwatch.Elapsed;
            })).ToArray();

            var results = await Task.WhenAll(tasks);
            Array.Sort(results);

            Assert.True(results[1] >= TimeSpan.FromSeconds(1.5));
            Assert.True(results[2] >= TimeSpan.FromSeconds(3.0));
        }

        [Fact]
        public async Task WaitAsync_UsesConfiguredJitterRange()
        {
            var options = new RateLimiterOptions
            {
                MaxCallsPerMinute = 100,
                JitterMinSeconds = 5,
                JitterMaxSeconds = 6
            };
            using var rateLimiter = new RateLimiterService(options);

            await rateLimiter.WaitAsync();
            var stopwatch = Stopwatch.StartNew();
            await rateLimiter.WaitAsync();
            var elapsed = stopwatch.Elapsed;

            Assert.True(elapsed >= TimeSpan.FromSeconds(5));
            Assert.True(elapsed <= TimeSpan.FromSeconds(7));
        }

        [Fact]
        public async Task WaitSteamAsync_UsesSteamJitterRange()
        {
            var options = new RateLimiterOptions
            {
                MaxCallsPerMinute = 100,
                JitterMinSeconds = 5,
                JitterMaxSeconds = 6,
                SteamMaxCallsPerMinute = 100,
                SteamJitterMinSeconds = 5.5,
                SteamJitterMaxSeconds = 8
            };
            using var rateLimiter = new RateLimiterService(options);

            await rateLimiter.WaitSteamAsync();
            var stopwatch = Stopwatch.StartNew();
            await rateLimiter.WaitSteamAsync();
            var elapsed = stopwatch.Elapsed;

            Assert.True(elapsed >= TimeSpan.FromSeconds(5.5));
            // Allow up to SteamJitterMaxSeconds (8) plus 0.5s tolerance for system overhead
            Assert.True(elapsed <= TimeSpan.FromSeconds(8.5));
        }
    }
}
