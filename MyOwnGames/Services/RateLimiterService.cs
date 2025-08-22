using System;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MyOwnGames.Services
{
    public class RateLimiterOptions
    {
        public int MaxCallsPerMinute { get; set; } = 30;
        public double JitterMinSeconds { get; set; } = 1.5;
        public double JitterMaxSeconds { get; set; } = 3.0;
    }

    public class RateLimiterService : IDisposable
    {
        private readonly TokenBucketRateLimiter _limiter;
        private readonly double _minDelaySeconds;
        private readonly double _maxDelaySeconds;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private DateTime _lastCall = DateTime.MinValue;
        private bool _disposed;

        public RateLimiterService(RateLimiterOptions options)
        {
            options.JitterMinSeconds = Math.Max(1.5, options.JitterMinSeconds);
            if (options.JitterMaxSeconds < options.JitterMinSeconds)
            {
                options.JitterMaxSeconds = options.JitterMinSeconds;
            }

            _minDelaySeconds = options.JitterMinSeconds;
            _maxDelaySeconds = options.JitterMaxSeconds;

            _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.MaxCallsPerMinute,
                TokensPerPeriod = options.MaxCallsPerMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = options.MaxCallsPerMinute
            });
        }

        public static RateLimiterService FromAppSettings()
        {
            RateLimiterOptions options = new();
            try
            {
                var config = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional: true)
                    .AddEnvironmentVariables()
                    .Build();
                var section = config.GetSection("RateLimiter");
                section.Bind(options);
            }
            catch
            {
                // Ignore configuration errors and use defaults
            }
            return new RateLimiterService(options);
        }

        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            var lease = await _limiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
            {
                throw new InvalidOperationException("Unable to acquire rate limiter lease.");
            }

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastCall;
                var jitterSeconds = _minDelaySeconds + Random.Shared.NextDouble() * (_maxDelaySeconds - _minDelaySeconds);
                var delay = TimeSpan.FromSeconds(jitterSeconds);
                if (elapsed < delay)
                {
                    await Task.Delay(delay - elapsed, cancellationToken);
                }
                _lastCall = DateTime.UtcNow;
            }
            finally
            {
                _semaphore.Release();
                lease.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _limiter.Dispose();
            _semaphore.Dispose();
            _disposed = true;
        }
    }
}

