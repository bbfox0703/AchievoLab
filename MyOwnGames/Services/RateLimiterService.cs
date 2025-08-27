using System;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MyOwnGames.Services
{
    public class RateLimiterOptions
    {
        public int MaxCallsPerMinute { get; set; } = 30; // Stricter: 30 calls per minute (1 every 5 seconds)
        public double JitterMinSeconds { get; set; } = 1.5; // More conservative delay
        public double JitterMaxSeconds { get; set; } = 3; // Up to 3 seconds between calls
        public int SteamMaxCallsPerMinute { get; set; } = 12; // Stricter: 12 calls per minute (1 every 5 seconds)
        public double SteamJitterMinSeconds { get; set; } = 5.5; // More conservative delay
        public double SteamJitterMaxSeconds { get; set; } = 7.5; // Up to 7 seconds between calls
    }

    public class RateLimiterService : IDisposable
    {
        private readonly TokenBucketRateLimiter _limiter;
        private readonly double _minDelaySeconds;
        private readonly double _maxDelaySeconds;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private DateTime _lastCall = DateTime.MinValue;

        private readonly TokenBucketRateLimiter _steamLimiter;
        private readonly double _steamMinDelaySeconds;
        private readonly double _steamMaxDelaySeconds;
        private readonly SemaphoreSlim _steamSemaphore = new(1, 1);
        private DateTime _steamLastCall = DateTime.MinValue;
        private bool _disposed;

        public RateLimiterService(RateLimiterOptions options)
        {
            options.JitterMinSeconds = Math.Max(5.0, options.JitterMinSeconds); // Enforce minimum 5 seconds
            if (options.JitterMaxSeconds < options.JitterMinSeconds)
            {
                options.JitterMaxSeconds = options.JitterMinSeconds;
            }

            options.SteamJitterMinSeconds = Math.Max(5.0, options.SteamJitterMinSeconds);
            if (options.SteamJitterMaxSeconds < options.SteamJitterMinSeconds)
            {
                options.SteamJitterMaxSeconds = options.SteamJitterMinSeconds;
            }

            _minDelaySeconds = options.JitterMinSeconds;
            _maxDelaySeconds = options.JitterMaxSeconds;
            _steamMinDelaySeconds = options.SteamJitterMinSeconds;
            _steamMaxDelaySeconds = options.SteamJitterMaxSeconds;

            _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.MaxCallsPerMinute,
                TokensPerPeriod = options.MaxCallsPerMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = options.MaxCallsPerMinute
            });

            _steamLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.SteamMaxCallsPerMinute,
                TokensPerPeriod = options.SteamMaxCallsPerMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = options.SteamMaxCallsPerMinute
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
                if (int.TryParse(section[nameof(RateLimiterOptions.MaxCallsPerMinute)], out int maxCalls))
                {
                    options.MaxCallsPerMinute = maxCalls;
                }

                if (double.TryParse(section[nameof(RateLimiterOptions.JitterMinSeconds)], out double jitterMin))
                {
                    options.JitterMinSeconds = jitterMin;
                }

                if (double.TryParse(section[nameof(RateLimiterOptions.JitterMaxSeconds)], out double jitterMax))
                {
                    options.JitterMaxSeconds = jitterMax;
                }

                if (int.TryParse(section[nameof(RateLimiterOptions.SteamMaxCallsPerMinute)], out int steamMaxCalls))
                {
                    options.SteamMaxCallsPerMinute = steamMaxCalls;
                }

                if (double.TryParse(section[nameof(RateLimiterOptions.SteamJitterMinSeconds)], out double steamJitterMin))
                {
                    options.SteamJitterMinSeconds = steamJitterMin;
                }

                if (double.TryParse(section[nameof(RateLimiterOptions.SteamJitterMaxSeconds)], out double steamJitterMax))
                {
                    options.SteamJitterMaxSeconds = steamJitterMax;
                }
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

        public async Task WaitSteamAsync(CancellationToken cancellationToken = default)
        {
            var lease = await _steamLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
            {
                throw new InvalidOperationException("Unable to acquire rate limiter lease.");
            }

            await _steamSemaphore.WaitAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _steamLastCall;
                var jitterSeconds = _steamMinDelaySeconds + Random.Shared.NextDouble() * (_steamMaxDelaySeconds - _steamMinDelaySeconds);
                var delay = TimeSpan.FromSeconds(jitterSeconds);
                if (elapsed < delay)
                {
                    await Task.Delay(delay - elapsed, cancellationToken);
                }
                _steamLastCall = DateTime.UtcNow;
            }
            finally
            {
                _steamSemaphore.Release();
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
            _steamLimiter.Dispose();
            _semaphore.Dispose();
            _steamSemaphore.Dispose();
            _disposed = true;
        }
    }
}

