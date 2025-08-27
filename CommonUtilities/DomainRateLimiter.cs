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
        private readonly Dictionary<string, TimeSpan> _domainExtraDelay = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _failureCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        private readonly int _maxConcurrentRequestsPerDomain;
        private readonly double _capacity;
        private readonly double _fillRatePerSecond;
        private double _tokens;
        private DateTime _lastRefill;

        private readonly TimeSpan _baseDomainDelay;
        private readonly double _jitterSeconds;

        public DomainRateLimiter(
            int maxConcurrentRequestsPerDomain = 2,
            double capacity = 60,
            double fillRatePerSecond = 1,
            double initialTokens = -1,
            TimeSpan? baseDomainDelay = null,
            double jitterSeconds = 0.1)
        {
            _maxConcurrentRequestsPerDomain = maxConcurrentRequestsPerDomain;
            _capacity = capacity;
            _fillRatePerSecond = fillRatePerSecond;
            _tokens = initialTokens >= 0 ? initialTokens : capacity;
            _lastRefill = DateTime.UtcNow;
            _baseDomainDelay = baseDomainDelay ?? TimeSpan.FromMilliseconds(100);
            _jitterSeconds = jitterSeconds;
        }

        // Global request counter for debugging
        private int _totalRequestsProcessed = 0;

        /// <summary>
        /// Waits until a request is allowed for the given URI based on domain and global limits.
        /// Also enforces single concurrent request per domain.
        /// </summary>
        public async Task WaitAsync(Uri uri, CancellationToken cancellationToken)
        {
            var host = uri.Host;

            // First acquire domain-specific semaphore to limit concurrent requests
            var domainSemaphore = GetOrCreateDomainSemaphore(host);
            await domainSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                TimeSpan extraDelay;
                lock (_lock)
                {
                    _domainExtraDelay.TryGetValue(host, out extraDelay);
                }

                var minInterval = _baseDomainDelay + extraDelay + TimeSpan.FromSeconds(Random.Shared.NextDouble() * _jitterSeconds);

                while (true)
                {
                    TimeSpan tokenDelay;
                    lock (_lock)
                    {
                        var now = DateTime.UtcNow;
                        var elapsed = (now - _lastRefill).TotalSeconds;
                        var available = Math.Min(_capacity, _tokens + elapsed * _fillRatePerSecond);
                        if (available >= 1)
                        {
                            _tokens = available - 1;
                            _lastRefill = now;
                            _totalRequestsProcessed++;
                            DebugLogger.LogDebug($"Token consumed for {host}, tokens remaining: {_tokens:F2}, total processed: {_totalRequestsProcessed}");
                            tokenDelay = TimeSpan.Zero;
                        }
                        else
                        {
                            tokenDelay = TimeSpan.FromSeconds((1 - available) / _fillRatePerSecond);
                        }
                    }

                    if (tokenDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(tokenDelay, cancellationToken).ConfigureAwait(false);
                        lock (_lock)
                        {
                            RefillTokens(DateTime.UtcNow);
                        }
                        continue;
                    }
                    break;
                }

                TimeSpan domainDelay = TimeSpan.Zero;
                lock (_lock)
                {
                    if (_lastCall.TryGetValue(host, out var last))
                    {
                        var since = DateTime.UtcNow - last;
                        if (since < minInterval)
                        {
                            domainDelay = minInterval - since;
                        }
                    }
                }

                if (domainDelay > TimeSpan.Zero)
                {
                    await Task.Delay(domainDelay, cancellationToken).ConfigureAwait(false);
                    lock (_lock)
                    {
                        RefillTokens(DateTime.UtcNow);
                    }
                }

                return;
            }
            catch
            {
                // If we fail to get through rate limiting, release the semaphore
                domainSemaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// Retrieves the semaphore guarding concurrent requests for a host, creating it if necessary.
        /// </summary>
        private SemaphoreSlim GetOrCreateDomainSemaphore(string host)
        {
            lock (_lock)
            {
                if (!_domainSemaphores.TryGetValue(host, out var semaphore))
                {
                    semaphore = new SemaphoreSlim(_maxConcurrentRequestsPerDomain);
                    _domainSemaphores[host] = semaphore;
                }
                return semaphore;
            }
        }

        /// <summary>
        /// Records that a request to the URI's domain has completed and releases the domain semaphore.
        /// If <paramref name="success"/> is false, the domain's delay will be increased and optionally
        /// extended by <paramref name="serverDelay"/> (e.g. from a Retry-After header).
        /// </summary>
        public void RecordCall(Uri uri, bool success, TimeSpan? serverDelay = null)
        {
            var host = uri.Host;
            lock (_lock)
            {
                _lastCall[host] = DateTime.UtcNow;

                if (success)
                {
                    _failureCounts.Remove(host);
                    _domainExtraDelay.Remove(host);
                }
                else
                {
                    var count = _failureCounts.TryGetValue(host, out var existing) ? existing + 1 : 1;
                    _failureCounts[host] = count;
                    var computed = TimeSpan.FromTicks((long)(_baseDomainDelay.Ticks * Math.Pow(2, count)));
                    if (serverDelay.HasValue && serverDelay.Value > computed)
                    {
                        computed = serverDelay.Value;
                    }

                    var cap = TimeSpan.FromSeconds(30);
                    if (computed > cap)
                    {
                        computed = cap;
                    }

                    _domainExtraDelay[host] = computed;
                }

                // Release the domain semaphore
                if (_domainSemaphores.TryGetValue(host, out var semaphore))
                {
                    semaphore.Release();
                }
            }
        }

        /// <summary>
        /// Replenishes tokens in the global bucket based on elapsed time.
        /// </summary>
        private void RefillTokens(DateTime now)
        {
            var elapsed = (now - _lastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens = Math.Min(_capacity, _tokens + elapsed * _fillRatePerSecond);
                _lastRefill = now;
            }
        }
    }
}

