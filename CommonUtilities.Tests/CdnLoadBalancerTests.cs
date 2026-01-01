using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CommonUtilities.Tests
{
    public class CdnLoadBalancerTests
    {
        [Fact]
        public void SelectBestCdn_ShouldReturnCloudFlareWhenAllAvailable()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>
            {
                "https://shared.cloudflare.steamstatic.com/test.jpg",
                "https://cdn.steamstatic.com/test.jpg",
                "https://shared.akamai.steamstatic.com/test.jpg"
            };

            // Act
            var selected = balancer.SelectBestCdn(urls);

            // Assert
            Assert.Contains("cloudflare", selected);
        }

        [Fact]
        public void SelectBestCdn_ShouldAvoidBlockedDomain()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>
            {
                "https://shared.cloudflare.steamstatic.com/test.jpg",
                "https://cdn.steamstatic.com/test.jpg",
                "https://shared.akamai.steamstatic.com/test.jpg"
            };

            // Block CloudFlare
            balancer.RecordBlockedDomain("shared.cloudflare.steamstatic.com", TimeSpan.FromMinutes(1));

            // Act
            var selected = balancer.SelectBestCdn(urls);

            // Assert
            Assert.DoesNotContain("cloudflare", selected);
            Assert.Contains("cdn.steamstatic.com", selected);
        }

        [Fact]
        public void SelectBestCdn_ShouldSelectLeastBusyCdn()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>
            {
                "https://shared.cloudflare.steamstatic.com/test.jpg",
                "https://cdn.steamstatic.com/test.jpg"
            };

            // Simulate CloudFlare being at capacity
            balancer.IncrementActiveRequests("shared.cloudflare.steamstatic.com");
            balancer.IncrementActiveRequests("shared.cloudflare.steamstatic.com");

            // Act
            var selected = balancer.SelectBestCdn(urls);

            // Assert
            Assert.Contains("cdn.steamstatic.com", selected);
        }

        [Fact]
        public void SelectBestCdn_ShouldFallbackWhenAllBlocked()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>
            {
                "https://shared.cloudflare.steamstatic.com/test.jpg",
                "https://cdn.steamstatic.com/test.jpg",
                "https://shared.akamai.steamstatic.com/test.jpg"
            };

            // Block all CDNs
            balancer.RecordBlockedDomain("shared.cloudflare.steamstatic.com", TimeSpan.FromMinutes(1));
            balancer.RecordBlockedDomain("cdn.steamstatic.com", TimeSpan.FromMinutes(1));
            balancer.RecordBlockedDomain("shared.akamai.steamstatic.com", TimeSpan.FromMinutes(1));

            // Act
            var selected = balancer.SelectBestCdn(urls);

            // Assert
            Assert.NotNull(selected);
            Assert.Equal(urls[0], selected);
        }

        [Fact]
        public void IncrementDecrement_ShouldTrackActiveRequests()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var domain = "shared.cloudflare.steamstatic.com";

            // Act - First record some activity to create stats entry
            balancer.RecordSuccess(domain);

            balancer.IncrementActiveRequests(domain);
            balancer.IncrementActiveRequests(domain);

            var stats1 = balancer.GetStats();
            var activeCount1 = stats1.ContainsKey(domain) ? stats1[domain].Active : 0;

            balancer.DecrementActiveRequests(domain);

            var stats2 = balancer.GetStats();
            var activeCount2 = stats2.ContainsKey(domain) ? stats2[domain].Active : 0;

            // Assert
            Assert.Equal(2, activeCount1);
            Assert.Equal(1, activeCount2);
        }

        [Fact]
        public void RecordSuccess_ShouldIncreaseSuccessRate()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var domain = "shared.cloudflare.steamstatic.com";

            // Act
            balancer.RecordSuccess(domain);
            balancer.RecordSuccess(domain);
            balancer.RecordFailure(domain);

            var stats = balancer.GetStats();
            var successRate = stats[domain].SuccessRate;

            // Assert
            // 2 successes out of 3 total = 0.666...
            Assert.True(successRate > 0.66 && successRate < 0.67, $"Success rate should be ~0.667, got {successRate}");
        }

        [Fact]
        public void RecordBlockedDomain_ShouldMarkAsBlocked()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var domain = "shared.cloudflare.steamstatic.com";

            // Act
            balancer.RecordBlockedDomain(domain, TimeSpan.FromSeconds(1));

            var stats1 = balancer.GetStats();
            var isBlocked1 = stats1[domain].IsBlocked;

            // Wait for block to expire
            Thread.Sleep(1100);

            // Try to select CDN (which should check and clear expired blocks)
            var urls = new List<string> { $"https://{domain}/test.jpg" };
            balancer.SelectBestCdn(urls);

            var stats2 = balancer.GetStats();
            var isBlocked2 = stats2.ContainsKey(domain) ? stats2[domain].IsBlocked : false;

            // Assert
            Assert.True(isBlocked1);
            Assert.False(isBlocked2);
        }

        [Fact]
        public void SelectBestCdn_ShouldPreferHighPriority()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>
            {
                "https://shared.cloudflare.steamstatic.com/test.jpg",
                "https://cdn.steamstatic.com/test.jpg"
            };

            // Make Steam CDN have higher success rate than CloudFlare
            balancer.RecordSuccess("cdn.steamstatic.com");
            balancer.RecordSuccess("cdn.steamstatic.com");
            balancer.RecordSuccess("cdn.steamstatic.com");

            balancer.RecordFailure("shared.cloudflare.steamstatic.com");
            balancer.RecordFailure("shared.cloudflare.steamstatic.com");
            balancer.RecordFailure("shared.cloudflare.steamstatic.com");

            // CloudFlare still has higher priority (3 vs 2), so it should still be selected

            // Act
            var selected = balancer.SelectBestCdn(urls);

            // Assert
            Assert.Contains("cloudflare", selected);
        }

        [Fact]
        public void SelectBestCdn_ShouldThrowWhenUrlListEmpty()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>();

            // Act & Assert
            Assert.Throws<ArgumentException>(() => balancer.SelectBestCdn(urls));
        }

        [Fact]
        public void GetStats_ShouldReturnEmptyForNewBalancer()
        {
            // Arrange
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);

            // Act
            var stats = balancer.GetStats();

            // Assert
            Assert.Empty(stats);
        }
    }
}
