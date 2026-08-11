using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CommonUtilities
{
    /// <summary>
    /// Provides cross-process file locking using an exclusively opened lock file.
    /// This ensures safe concurrent access to shared files between multiple processes.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT use a named <see cref="Mutex"/>. A mutex is owned by the thread that
    /// waited on it and may only be released by that same thread, which cannot be guaranteed here:
    /// <see cref="TryAcquireAsync"/> resumes on an arbitrary thread pool thread after each await,
    /// so the acquiring and releasing threads routinely differ. ReleaseMutex then throws
    /// ApplicationException and the mutex stays held until its owning thread exits.
    ///
    /// A FileStream opened with <see cref="FileShare.None"/> gives the same cross-process exclusion
    /// with no thread affinity, and <see cref="FileOptions.DeleteOnClose"/> lets the OS clean up if
    /// the holding process dies.
    /// </remarks>
    public class CrossProcessFileLock : IDisposable
    {
        private const int RetryDelayMs = 50;

        private readonly string _lockFilePath;
        private FileStream? _lockFileStream;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the CrossProcessFileLock class for the specified file path.
        /// </summary>
        /// <param name="filePath">The file path to create a lock for. A .lock file will be created alongside this path.</param>
        /// <exception cref="ArgumentException">Thrown when filePath is null or empty.</exception>
        public CrossProcessFileLock(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            _lockFilePath = filePath + ".lock";
        }

        /// <summary>
        /// Attempts a single exclusive open of the lock file.
        /// </summary>
        /// <returns>True if the lock file is now held by this instance.</returns>
        private bool TryOpenLockFile()
        {
            try
            {
                var directory = Path.GetDirectoryName(_lockFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _lockFileStream = new FileStream(
                    _lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None, // Exclusive access - no sharing
                    1,
                    FileOptions.DeleteOnClose); // Auto-delete when closed

                return true;
            }
            catch (IOException)
            {
                // Held by another process.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Transient: the previous holder closed the handle and the delete is still pending.
                return false;
            }
        }

        /// <summary>
        /// Acquires the cross-process lock with optional timeout.
        /// </summary>
        /// <param name="timeout">Maximum time to wait for the lock. Use TimeSpan.Zero for immediate return, or Timeout.InfiniteTimeSpan to wait indefinitely.</param>
        /// <returns>True if lock was acquired, false if timeout occurred.</returns>
        public bool TryAcquire(TimeSpan timeout = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CrossProcessFileLock));

            if (_lockFileStream != null)
                return true; // Already held by this instance.

            if (timeout == default)
                timeout = TimeSpan.FromSeconds(30); // Default 30 second timeout

            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

            while (true)
            {
                if (TryOpenLockFile())
                {
                    AppLogger.LogDebug($"Acquired cross-process lock for {_lockFilePath}");
                    return true;
                }

                if (Environment.TickCount64 >= deadline)
                {
                    if (timeout.TotalMilliseconds > 500)
                    {
                        AppLogger.LogDebug($"Failed to acquire lock on {_lockFilePath} within {timeout.TotalSeconds}s timeout");
                    }
                    return false;
                }

                Thread.Sleep(RetryDelayMs);
            }
        }

        /// <summary>
        /// Asynchronously acquires the cross-process lock with optional timeout.
        /// </summary>
        public async Task<bool> TryAcquireAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CrossProcessFileLock));

            if (_lockFileStream != null)
                return true; // Already held by this instance.

            if (timeout == default)
                timeout = TimeSpan.FromSeconds(30);

            try
            {
                var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
                var loggedWarning = false;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (TryOpenLockFile())
                    {
                        AppLogger.LogDebug($"Acquired cross-process lock for {_lockFilePath}");
                        return true;
                    }

                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        AppLogger.LogDebug($"Failed to acquire lock on {_lockFilePath} after {timeout.TotalSeconds}s timeout");
                        return false;
                    }

                    // Log warning only once after 5 seconds of waiting
                    if (!loggedWarning && (timeout.TotalMilliseconds - remaining) > 5000)
                    {
                        AppLogger.LogDebug($"Still waiting for lock on {_lockFilePath}");
                        loggedWarning = true;
                    }

                    await Task.Delay((int)Math.Min(RetryDelayMs, remaining), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Releases the cross-process lock. Idempotent.
        /// </summary>
        public void Release()
        {
            try
            {
                // Closing the stream drops the exclusive handle; DeleteOnClose removes the file.
                // Safe from any thread — unlike a Mutex, a FileStream has no thread affinity.
                if (_lockFileStream != null)
                {
                    _lockFileStream.Dispose();
                    _lockFileStream = null;
                    AppLogger.LogDebug($"Released file lock for {_lockFilePath}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error releasing lock for {_lockFilePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Releases all resources used by the CrossProcessFileLock and releases the lock if held.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            // Release first, THEN mark disposed. The other order made Dispose a no-op — Release
            // used to start with `if (_disposed) return;`, so `using var fileLock = ...` never
            // actually let go of the lock file and its handle stayed open until the process ended.
            Release();
            _disposed = true;
        }
    }

    /// <summary>
    /// Helper class for using CrossProcessFileLock with using statement.
    /// Automatically releases the lock when disposed.
    /// </summary>
    public class CrossProcessFileLockHandle : IDisposable, IAsyncDisposable
    {
        private readonly CrossProcessFileLock _lock;
        private readonly bool _acquired;

        /// <summary>
        /// Gets a value indicating whether the lock was successfully acquired.
        /// </summary>
        public bool IsAcquired => _acquired;

        /// <summary>
        /// Initializes a new instance of the CrossProcessFileLockHandle class.
        /// </summary>
        /// <param name="fileLock">The CrossProcessFileLock instance to wrap.</param>
        /// <param name="acquired">Indicates whether the lock was successfully acquired.</param>
        internal CrossProcessFileLockHandle(CrossProcessFileLock fileLock, bool acquired)
        {
            _lock = fileLock;
            _acquired = acquired;
        }

        /// <summary>
        /// Releases the lock if it was acquired.
        /// </summary>
        public void Dispose()
        {
            if (_acquired)
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Asynchronously releases the lock if it was acquired.
        /// </summary>
        /// <returns>A ValueTask representing the asynchronous operation.</returns>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Extension methods for CrossProcessFileLock
    /// </summary>
    public static class CrossProcessFileLockExtensions
    {
        /// <summary>
        /// Acquires the lock and returns a handle that automatically releases it when disposed.
        /// Use with 'using' statement for automatic release.
        /// </summary>
        public static CrossProcessFileLockHandle AcquireHandle(this CrossProcessFileLock fileLock, TimeSpan timeout = default)
        {
            bool acquired = fileLock.TryAcquire(timeout);
            return new CrossProcessFileLockHandle(fileLock, acquired);
        }

        /// <summary>
        /// Asynchronously acquires the lock and returns a handle that automatically releases it when disposed.
        /// Use with 'await using' statement for automatic release.
        /// </summary>
        public static async Task<CrossProcessFileLockHandle> AcquireHandleAsync(this CrossProcessFileLock fileLock, TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            bool acquired = await fileLock.TryAcquireAsync(timeout, cancellationToken);
            return new CrossProcessFileLockHandle(fileLock, acquired);
        }
    }
}
