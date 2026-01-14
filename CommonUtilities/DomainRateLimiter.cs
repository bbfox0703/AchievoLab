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

        /// <summary>
        /// Initializes a new instance of the <see cref="DomainRateLimiter"/> class with token bucket rate limiting.
        /// </summary>
        /// <param name="maxConcurrentRequestsPerDomain">Maximum number of concurrent requests allowed per domain (default: 2).</param>
        /// <param name="capacity">Maximum number of tokens in the bucket for global rate limiting (default: 60).</param>
        /// <param name="fillRatePerSecond">Number of tokens added to the bucket per second (default: 1).</param>
        /// <param name="initialTokens">Starting number of tokens in the bucket. If negative, defaults to <paramref name="capacity"/>.</param>
        /// <param name="baseDomainDelay">Base delay between requests to the same domain. If null, defaults to 100 milliseconds.</param>
        /// <param name="jitterSeconds">Random time variance added to domain delays in seconds to prevent synchronized requests (default: 0.1).</param>
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

        /// <summary>
        /// Tracks the total number of requests successfully processed through the rate limiter for monitoring and debugging purposes.
        /// </summary>
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

            try
            {
                await domainSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // CRITICAL: Domain semaphore was disposed during language switch or app shutdown
                // This happens when downloads are waiting to acquire semaphore and it gets disposed
                AppLogger.LogDebug($"Domain semaphore for {host} disposed while waiting, aborting request");
                throw; // Re-throw to let caller handle it
            }

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
                            AppLogger.LogDebug($"Token consumed for {host}, tokens remaining: {_tokens:F2}, total processed: {_totalRequestsProcessed}");
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
            catch (Exception ex)
            {
                // If we fail to get through rate limiting, log and release the semaphore
                AppLogger.LogDebug($"Failed to wait for rate limiter on '{host}': {ex.GetType().Name} - {ex.Message}");
                try
                {
                    domainSemaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore was disposed during language switch or app shutdown
                    AppLogger.LogDebug($"Domain semaphore already disposed in catch block, skipping release");
                }
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
                    try
                    {
                        semaphore.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // CRITICAL: Semaphore was disposed during language switch or app shutdown
                        // This is expected when downloads complete after language switch starts
                        AppLogger.LogDebug($"Domain semaphore for {host} already disposed, skipping release");
                    }
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

