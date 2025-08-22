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
    }
}
