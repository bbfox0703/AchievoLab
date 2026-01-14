using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CommonUtilities
{
    /// <summary>
    /// CDN load balancer that selects the best CDN based on current load and failure tracking
    /// </summary>
    public class CdnLoadBalancer
    {
        private readonly ConcurrentDictionary<string, int> _activeRequests = new();
        private readonly ConcurrentDictionary<string, DateTime> _blockedUntil = new();
        private readonly ConcurrentDictionary<string, CdnStats> _stats = new();
        private readonly int _maxConcurrentPerDomain;

        /// <summary>
        /// Initializes a new instance of the <see cref="CdnLoadBalancer"/> class.
        /// </summary>
        /// <param name="maxConcurrentPerDomain">Maximum number of concurrent requests allowed per CDN domain (default: 2).</param>
        public CdnLoadBalancer(int maxConcurrentPerDomain = 2)
        {
            _maxConcurrentPerDomain = maxConcurrentPerDomain;
        }

        /// <summary>
        /// Select the best CDN from a list of URLs
        /// </summary>
        public string SelectBestCdn(List<string> cdnUrls)
        {
            if (cdnUrls == null || cdnUrls.Count == 0)
                throw new ArgumentException("CDN URL list cannot be empty", nameof(cdnUrls));

            var now = DateTime.UtcNow;

            // Evaluate each CDN's availability
            var candidates = cdnUrls
                .Select(url =>
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                        return null;

                    var domain = uri.Host;
                    var activeCount = _activeRequests.GetOrAdd(domain, 0);
                    var stats = _stats.GetOrAdd(domain, _ => new CdnStats());

                    // Check if blocked
                    bool isBlocked = false;
                    if (_blockedUntil.TryGetValue(domain, out var blockedTime))
                    {
                        if (now < blockedTime)
                        {
                            isBlocked = true;
                            AppLogger.LogDebug($"CDN {domain} is blocked until {blockedTime:HH:mm:ss}");
                        }
                        else
                        {
                            // Expired, remove block record
                            _blockedUntil.TryRemove(domain, out _);
                        }
                    }

                    return new
                    {
                        Url = url,
                        Domain = domain,
                        ActiveCount = activeCount,
                        IsBlocked = isBlocked,
                        IsAvailable = !isBlocked && activeCount < _maxConcurrentPerDomain,
                        Priority = GetDomainPriority(domain),
                        SuccessRate = stats.GetSuccessRate()
                    };
                })
                .Where(x => x != null)
                .ToList();

            // Selection strategy:
            // 1. Prefer available CDNs (not blocked and under concurrent limit)
            // 2. Sort by priority (CloudFlare > Steam > Akamai)
            // 3. Sort by success rate
            // 4. Sort by current active count (select least busy)
            var best = candidates
                .Where(x => x != null && x.IsAvailable)
                .OrderByDescending(x => x!.Priority)
                .ThenByDescending(x => x!.SuccessRate)
                .ThenBy(x => x!.ActiveCount)
                .ThenBy(x => Guid.NewGuid()) // Random selection for ties
                .FirstOrDefault();

            if (best != null)
            {
                AppLogger.LogDebug($"Selected CDN: {best.Domain} (Active: {best.ActiveCount}/{_maxConcurrentPerDomain}, Priority: {best.Priority}, Success: {best.SuccessRate:P})");
                return best.Url;
            }

            // If all CDNs are unavailable, select the one that will recover soonest
            var leastBusy = candidates
                .Where(x => x != null && !x.IsBlocked)
                .OrderBy(x => x!.ActiveCount)
                .FirstOrDefault();

            if (leastBusy != null)
            {
                AppLogger.LogDebug($"All CDN slots full, selecting least busy: {leastBusy.Domain} (Active: {leastBusy.ActiveCount})");
                return leastBusy.Url;
            }

            // Fallback: return first URL
            AppLogger.LogDebug("All CDNs blocked or unavailable, using first URL as fallback");
            return cdnUrls[0];
        }

        /// <summary>
        /// Get domain priority (higher = better)
        /// </summary>
        private int GetDomainPriority(string domain)
        {
            // CloudFlare preferred (usually best quality)
            if (domain.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
                return 3;

            // Steam CDN second
            if (domain.Contains("cdn.steamstatic.com", StringComparison.OrdinalIgnoreCase))
                return 2;

            // Akamai last
            if (domain.Contains("akamai", StringComparison.OrdinalIgnoreCase))
                return 1;

            return 0;
        }

        /// <summary>
        /// Record that a domain is being used
        /// </summary>
        public void IncrementActiveRequests(string domain)
        {
            var count = _activeRequests.AddOrUpdate(domain, 1, (_, c) => c + 1);
            AppLogger.LogDebug($"CDN {domain} active requests: {count}");
        }

        /// <summary>
        /// Record that a domain request has completed
        /// </summary>
        public void DecrementActiveRequests(string domain)
        {
            var count = _activeRequests.AddOrUpdate(domain, 0, (_, c) => Math.Max(0, c - 1));
            AppLogger.LogDebug($"CDN {domain} active requests: {count}");
        }

        /// <summary>
        /// Record that a CDN is blocked (429/403 response)
        /// </summary>
        public void RecordBlockedDomain(string domain, TimeSpan? duration = null)
        {
            var blockDuration = duration ?? TimeSpan.FromMinutes(5);
            _blockedUntil[domain] = DateTime.UtcNow.Add(blockDuration);
            AppLogger.LogDebug($"CDN {domain} blocked for {blockDuration.TotalMinutes} minutes");

            // Update statistics
            var stats = _stats.GetOrAdd(domain, _ => new CdnStats());
            stats.RecordFailure();
        }

        /// <summary>
        /// Record a successful download
        /// </summary>
        public void RecordSuccess(string domain)
        {
            var stats = _stats.GetOrAdd(domain, _ => new CdnStats());
            stats.RecordSuccess();
        }

        /// <summary>
        /// Record a failed download
        /// </summary>
        public void RecordFailure(string domain)
        {
            var stats = _stats.GetOrAdd(domain, _ => new CdnStats());
            stats.RecordFailure();
        }

        /// <summary>
        /// Get statistics for all CDNs
        /// </summary>
        public Dictionary<string, (int Active, bool IsBlocked, double SuccessRate)> GetStats()
        {
            var now = DateTime.UtcNow;
            return _stats.ToDictionary(
                kvp => kvp.Key,
                kvp =>
                {
                    var active = _activeRequests.GetOrAdd(kvp.Key, 0);
                    var isBlocked = _blockedUntil.TryGetValue(kvp.Key, out var blockedTime) && now < blockedTime;
                    var successRate = kvp.Value.GetSuccessRate();
                    return (active, isBlocked, successRate);
                });
        }

        /// <summary>
        /// Tracks success and failure statistics for a CDN domain to calculate success rates.
        /// Thread-safe tracking of request outcomes for CDN selection prioritization.
        /// </summary>
        private class CdnStats
        {
            private int _totalRequests;
            private int _successCount;
            private readonly object _lock = new();

            /// <summary>
            /// Records a successful request, incrementing both total requests and successful requests counters.
            /// </summary>
            public void RecordSuccess()
            {
                lock (_lock)
                {
                    _totalRequests++;
                    _successCount++;
                }
            }

            /// <summary>
            /// Records a failed request, incrementing only the total requests counter.
            /// </summary>
            public void RecordFailure()
            {
                lock (_lock)
                {
                    _totalRequests++;
                }
            }

            /// <summary>
            /// Calculates the success rate as a ratio of successful requests to total requests.
            /// </summary>
            /// <returns>Success rate between 0.0 and 1.0, or 1.0 (100%) if no requests have been tracked yet.</returns>
            public double GetSuccessRate()
            {
                lock (_lock)
                {
                    if (_totalRequests == 0)
                        return 1.0; // Assume 100% for new CDNs

                    return (double)_successCount / _totalRequests;
                }
            }
        }
    }
}
