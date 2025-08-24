using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CommonUtilities
{
    /// <summary>
    /// Rate limiter that throttles requests per-domain with a base delay plus jitter and
    /// globally using a token bucket. Also enforces single concurrent request per domain.
    /// </summary>
    internal class DomainRateLimiter
    {
        private readonly Dictionary<string, DateTime> _lastCall = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SemaphoreSlim> _domainSemaphores = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        // Maximum concurrent requests per domain (configurable)
        private const int MaxConcurrentRequestsPerDomain = 1; // Only 1 request per domain to avoid blocks

        // Token bucket parameters: allows up to 120 downloads per minute
        private const int Capacity = 120; // max 120 requests
        private const double FillRatePerSecond = 2; // 2 tokens per second => 120 per minute
        private double _tokens = 10; // Start with fewer tokens for testing
        private DateTime _lastRefill = DateTime.UtcNow;
        
        // Global request counter for debugging
        private int _totalRequestsProcessed = 0;

        // Per-domain delay with jitter to avoid bursts
        private static readonly TimeSpan BaseDomainDelay = TimeSpan.FromSeconds(1);
        private const double JitterSeconds = 0.5;

        /// <summary>
        /// Waits until a request is allowed for the given URI based on domain and global limits.
        /// Also enforces single concurrent request per domain.
        /// </summary>
        public async Task WaitAsync(Uri uri)
        {
            var host = uri.Host;
            
            // First acquire domain-specific semaphore to limit concurrent requests
            var domainSemaphore = GetOrCreateDomainSemaphore(host);
            await domainSemaphore.WaitAsync().ConfigureAwait(false);
            
            try
            {
                var minInterval = BaseDomainDelay + TimeSpan.FromSeconds(Random.Shared.NextDouble() * JitterSeconds);
                while (true)
                {
                    TimeSpan delay = TimeSpan.Zero;
                    lock (_lock)
                    {
                        // domain delay
                        if (_lastCall.TryGetValue(host, out var last))
                        {
                            var since = DateTime.UtcNow - last;
                            if (since < minInterval)
                            {
                                delay = minInterval - since;
                            }
                        }

                        RefillTokens(DateTime.UtcNow);
                        if (_tokens >= 1 && delay == TimeSpan.Zero)
                        {
                            _tokens -= 1;
                            _totalRequestsProcessed++;
                            DebugLogger.LogDebug($"Token consumed for {host}, tokens remaining: {_tokens:F2}, total processed: {_totalRequestsProcessed}");
                            // Don't release semaphore here - it will be released in RecordCall
                            return;
                        }
                        if (_tokens < 1)
                        {
                            var needed = 1 - _tokens;
                            var tokenDelay = TimeSpan.FromSeconds(needed / FillRatePerSecond);
                            if (tokenDelay > delay)
                            {
                                delay = tokenDelay;
                            }
                        }
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // If we fail to get through rate limiting, release the semaphore
                domainSemaphore.Release();
                throw;
            }
        }

        private SemaphoreSlim GetOrCreateDomainSemaphore(string host)
        {
            lock (_lock)
            {
                if (!_domainSemaphores.TryGetValue(host, out var semaphore))
                {
                    semaphore = new SemaphoreSlim(MaxConcurrentRequestsPerDomain);
                    _domainSemaphores[host] = semaphore;
                }
                return semaphore;
            }
        }

        /// <summary>
        /// Records that a request to the URI's domain has completed and releases domain semaphore.
        /// </summary>
        public void RecordCall(Uri uri)
        {
            lock (_lock)
            {
                _lastCall[uri.Host] = DateTime.UtcNow;
                
                // Release the domain semaphore
                if (_domainSemaphores.TryGetValue(uri.Host, out var semaphore))
                {
                    semaphore.Release();
                }
            }
        }

        private void RefillTokens(DateTime now)
        {
            var elapsed = (now - _lastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens = Math.Min(Capacity, _tokens + elapsed * FillRatePerSecond);
                _lastRefill = now;
            }
        }
    }
}

