using System;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MyOwnGames.Services
{
    /// <summary>
    /// Configuration options for rate limiting Steam API requests.
    /// Controls both token bucket limits and jitter delays to prevent API throttling.
    /// </summary>
    public class RateLimiterOptions
    {
        /// <summary>
        /// Maximum number of general API calls allowed per minute.
        /// Default is 30 calls per minute (1 every 2 seconds).
        /// Used for general HTTP requests that don't target Steam Web API.
        /// </summary>
        public int MaxCallsPerMinute { get; set; } = 30; // Stricter: 30 calls per minute (1 every 2 seconds)

        /// <summary>
        /// Minimum delay in seconds between general API calls (conservative lower bound).
        /// Default is 1.5 seconds. Combined with JitterMaxSeconds to create randomized delays.
        /// </summary>
        public double JitterMinSeconds { get; set; } = 1.5; // More conservative delay

        /// <summary>
        /// Maximum delay in seconds between general API calls (conservative upper bound).
        /// Default is 3 seconds. Combined with JitterMinSeconds to create randomized delays.
        /// </summary>
        public double JitterMaxSeconds { get; set; } = 3; // Up to 3 seconds between calls

        /// <summary>
        /// Maximum number of Steam Web API calls allowed per minute.
        /// Default is 12 calls per minute (1 every 5 seconds).
        /// Steam has stricter limits than general HTTP endpoints.
        /// </summary>
        public int SteamMaxCallsPerMinute { get; set; } = 12; // Stricter: 12 calls per minute (1 every 5 seconds)

        /// <summary>
        /// Minimum delay in seconds between Steam API calls (conservative lower bound).
        /// Default is 5.5 seconds. Combined with SteamJitterMaxSeconds to create randomized delays.
        /// </summary>
        public double SteamJitterMinSeconds { get; set; } = 5.5; // More conservative delay

        /// <summary>
        /// Maximum delay in seconds between Steam API calls (conservative upper bound).
        /// Default is 8 seconds. Combined with SteamJitterMinSeconds to create randomized delays.
        /// </summary>
        public double SteamJitterMaxSeconds { get; set; } = 8; // Up to 8 seconds between calls
    }

    /// <summary>
    /// Provides rate limiting for Steam API requests using token bucket algorithm with randomized jitter delays.
    /// Maintains separate rate limiters for general API calls and Steam-specific calls.
    /// Thread-safe and supports cancellation.
    /// </summary>
    /// <remarks>
    /// This service uses two-level throttling:
    /// 1. Token bucket: Controls maximum calls per minute (configurable via options)
    /// 2. Jitter delays: Adds randomized delays between calls to avoid burst patterns that trigger rate limiting
    ///
    /// The service enforces minimum 5-second delays for all calls to be conservative with Steam's undocumented limits.
    /// Steam Web API calls use stricter limits (default 12/minute) than general HTTP calls (default 30/minute).
    /// </remarks>
    public class RateLimiterService : IDisposable
    {
        /// <summary>
        /// Token bucket rate limiter for general API calls.
        /// Controls maximum calls per minute based on MaxCallsPerMinute option.
        /// </summary>
        private readonly TokenBucketRateLimiter _limiter;

        /// <summary>
        /// Minimum delay in seconds between general API calls (from configuration).
        /// Enforced to be at least 5.0 seconds to prevent rate limiting.
        /// </summary>
        private readonly double _minDelaySeconds;

        /// <summary>
        /// Maximum delay in seconds between general API calls (from configuration).
        /// Combined with _minDelaySeconds to create randomized jitter delays.
        /// </summary>
        private readonly double _maxDelaySeconds;

        /// <summary>
        /// Semaphore to ensure only one general API call is processed at a time.
        /// Prevents race conditions when calculating delays based on _lastCall.
        /// </summary>
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        /// <summary>
        /// Timestamp of the last general API call.
        /// Used to calculate elapsed time and enforce minimum delays between calls.
        /// </summary>
        private DateTime _lastCall = DateTime.MinValue;

        /// <summary>
        /// Token bucket rate limiter for Steam Web API calls.
        /// Controls maximum calls per minute based on SteamMaxCallsPerMinute option.
        /// More restrictive than general limiter.
        /// </summary>
        private readonly TokenBucketRateLimiter _steamLimiter;

        /// <summary>
        /// Minimum delay in seconds between Steam API calls (from configuration).
        /// Enforced to be at least 5.0 seconds to prevent rate limiting.
        /// </summary>
        private readonly double _steamMinDelaySeconds;

        /// <summary>
        /// Maximum delay in seconds between Steam API calls (from configuration).
        /// Combined with _steamMinDelaySeconds to create randomized jitter delays.
        /// </summary>
        private readonly double _steamMaxDelaySeconds;

        /// <summary>
        /// Semaphore to ensure only one Steam API call is processed at a time.
        /// Prevents race conditions when calculating delays based on _steamLastCall.
        /// </summary>
        private readonly SemaphoreSlim _steamSemaphore = new(1, 1);

        /// <summary>
        /// Timestamp of the last Steam API call.
        /// Used to calculate elapsed time and enforce minimum delays between calls.
        /// </summary>
        private DateTime _steamLastCall = DateTime.MinValue;

        /// <summary>
        /// Flag indicating whether this instance has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="RateLimiterService"/> with specified options.
        /// Enforces minimum 5-second delays and creates token bucket rate limiters.
        /// </summary>
        /// <param name="options">Configuration options controlling rate limits and jitter delays.</param>
        /// <remarks>
        /// This constructor:
        /// - Enforces minimum 5-second jitter delays for both general and Steam API calls
        /// - Ensures max jitter is at least as large as min jitter
        /// - Creates two separate token bucket limiters (general and Steam)
        /// - Configures automatic token replenishment every minute
        /// </remarks>
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

        /// <summary>
        /// Creates a new <see cref="RateLimiterService"/> by reading configuration from appsettings.json.
        /// Falls back to default values if configuration is missing or invalid.
        /// </summary>
        /// <returns>A new <see cref="RateLimiterService"/> instance configured from appsettings.json or defaults.</returns>
        /// <remarks>
        /// Configuration is read from the "RateLimiter" section in appsettings.json.
        /// Example configuration:
        /// <code>
        /// "RateLimiter": {
        ///   "MaxCallsPerMinute": 30,
        ///   "JitterMinSeconds": 1.5,
        ///   "JitterMaxSeconds": 3.0,
        ///   "SteamMaxCallsPerMinute": 12,
        ///   "SteamJitterMinSeconds": 5.5,
        ///   "SteamJitterMaxSeconds": 8.0
        /// }
        /// </code>
        /// If configuration fails to load or parse, default values from <see cref="RateLimiterOptions"/> are used.
        /// </remarks>
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

        /// <summary>
        /// Waits asynchronously before allowing a general API call to proceed.
        /// Enforces both token bucket limits and randomized jitter delays.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the wait operation.</param>
        /// <returns>A task that completes when the rate limit allows the call to proceed.</returns>
        /// <exception cref="InvalidOperationException">Thrown if unable to acquire a rate limiter lease.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled.</exception>
        /// <remarks>
        /// This method:
        /// 1. Acquires a token from the token bucket (blocks until available)
        /// 2. Calculates a random jitter delay between _minDelaySeconds and _maxDelaySeconds
        /// 3. Waits for the remaining time since the last call (if needed)
        /// 4. Updates the last call timestamp
        ///
        /// The method is thread-safe via semaphore and ensures calls are properly spaced.
        /// </remarks>
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

        /// <summary>
        /// Waits asynchronously before allowing a Steam Web API call to proceed.
        /// Uses stricter rate limiting than general API calls.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the wait operation.</param>
        /// <returns>A task that completes when the rate limit allows the call to proceed.</returns>
        /// <exception cref="InvalidOperationException">Thrown if unable to acquire a rate limiter lease.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled.</exception>
        /// <remarks>
        /// This method:
        /// 1. Acquires a token from the Steam-specific token bucket (blocks until available)
        /// 2. Calculates a random jitter delay between _steamMinDelaySeconds and _steamMaxDelaySeconds
        /// 3. Waits for the remaining time since the last Steam call (if needed)
        /// 4. Updates the last Steam call timestamp
        ///
        /// Steam Web API has stricter undocumented rate limits than other endpoints.
        /// Default configuration allows 12 calls/minute with 5.5-8 second jitter delays.
        /// The method is thread-safe via semaphore and ensures calls are properly spaced.
        /// </remarks>
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

        /// <summary>
        /// Releases all resources used by the <see cref="RateLimiterService"/>.
        /// Disposes both rate limiters and semaphores.
        /// </summary>
        /// <remarks>
        /// This method is idempotent and can be called multiple times safely.
        /// After disposal, the service should not be used for further rate limiting.
        /// </remarks>
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

