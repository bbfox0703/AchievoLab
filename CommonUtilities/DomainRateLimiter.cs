using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CommonUtilities
{
    /// <summary>
    /// Rate limiter that throttles requests per-domain and globally using a token bucket.
    /// </summary>
    internal class DomainRateLimiter
    {
        private readonly Dictionary<string, DateTime> _lastCall = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        // Token bucket parameters
        private const int Capacity = 50; // max 50 requests
        private const double FillRatePerSecond = Capacity / 60.0; // 50 per minute
        private double _tokens = Capacity;
        private DateTime _lastRefill = DateTime.UtcNow;

        private static readonly TimeSpan PerDomainDelay = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Waits until a request is allowed for the given URI based on domain and global limits.
        /// </summary>
        public async Task WaitAsync(Uri uri)
        {
            var host = uri.Host;
            while (true)
            {
                TimeSpan delay = TimeSpan.Zero;
                lock (_lock)
                {
                    // domain delay
                    if (_lastCall.TryGetValue(host, out var last))
                    {
                        var since = DateTime.UtcNow - last;
                        if (since < PerDomainDelay)
                        {
                            delay = PerDomainDelay - since;
                        }
                    }

                    RefillTokens(DateTime.UtcNow);
                    if (_tokens >= 1 && delay == TimeSpan.Zero)
                    {
                        _tokens -= 1;
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

        /// <summary>
        /// Records that a request to the URI's domain has completed.
        /// </summary>
        public void RecordCall(Uri uri)
        {
            lock (_lock)
            {
                _lastCall[uri.Host] = DateTime.UtcNow;
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

