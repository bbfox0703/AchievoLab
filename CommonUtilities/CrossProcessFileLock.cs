using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CommonUtilities
{
    /// <summary>
    /// Provides cross-process file locking using a combination of Mutex and FileStream.
    /// This ensures safe concurrent access to shared files between multiple processes.
    /// </summary>
    public class CrossProcessFileLock : IDisposable
    {
        private readonly string _lockFilePath;
        private readonly Mutex _mutex;
        private readonly string _mutexName;
        private FileStream? _lockFileStream;
        private bool _disposed;
        private bool _mutexOwned = false;

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

            // Create a global mutex name based on the file path
            // Replace invalid characters for mutex names
            var safeName = filePath.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
            _mutexName = $"Global\\AchievoLab_FileLock_{safeName}";

            _mutex = new Mutex(false, _mutexName);
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

            if (timeout == default)
                timeout = TimeSpan.FromSeconds(30); // Default 30 second timeout

            try
            {
                // First, acquire the mutex with longer timeout for initial attempt
                var mutexTimeout = timeout.TotalMilliseconds > 500 ? timeout : TimeSpan.FromMilliseconds(Math.Max(500, timeout.TotalMilliseconds));
                bool mutexAcquired = _mutex.WaitOne(mutexTimeout);
                if (!mutexAcquired)
                {
                    // Only log if this was a significant wait
                    if (timeout.TotalMilliseconds > 500)
                    {
                        AppLogger.LogDebug($"Failed to acquire mutex for {_lockFilePath} within {timeout.TotalSeconds}s timeout");
                    }
                    return false;
                }

                _mutexOwned = true; // Mark that we own the mutex

                try
                {
                    // Then create/open the lock file with exclusive access
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

                    AppLogger.LogDebug($"Acquired cross-process lock for {_lockFilePath}");
                    return true;
                }
                catch (IOException ex)
                {
                    // Failed to acquire file lock, release mutex
                    try
                    {
                        _mutex.ReleaseMutex();
                        _mutexOwned = false;
                    }
                    catch (ApplicationException)
                    {
                        // Mutex was not owned by current thread, ignore
                    }
                    AppLogger.LogDebug($"Failed to acquire file lock for {_lockFilePath}: {ex.Message}");
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous process holding the mutex terminated without releasing it
                // We now own the mutex, try to acquire the file lock
                AppLogger.LogDebug($"Acquired abandoned mutex for {_lockFilePath}");
                _mutexOwned = true; // Mark that we own the mutex

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
                        FileShare.None,
                        1,
                        FileOptions.DeleteOnClose);

                    return true;
                }
                catch (IOException ex)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                        _mutexOwned = false;
                    }
                    catch (ApplicationException)
                    {
                        // Mutex was not owned by current thread, ignore
                    }
                    AppLogger.LogDebug($"Failed to acquire file lock after abandoned mutex for {_lockFilePath}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Asynchronously acquires the cross-process lock with optional timeout.
        /// Uses synchronous acquisition to ensure mutex is owned by the calling thread.
        /// </summary>
        public async Task<bool> TryAcquireAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CrossProcessFileLock));

            if (timeout == default)
                timeout = TimeSpan.FromSeconds(30);

            try
            {
                var startTime = DateTime.Now;
                var loggedWarning = false;

                while ((DateTime.Now - startTime) < timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Try to acquire with short timeout to avoid blocking UI thread too long
                    if (TryAcquire(TimeSpan.FromMilliseconds(100)))
                        return true;

                    // Log warning only once after 5 seconds of waiting
                    if (!loggedWarning && (DateTime.Now - startTime).TotalSeconds > 5)
                    {
                        AppLogger.LogDebug($"Still waiting for lock on {_lockFilePath} (elapsed: {(DateTime.Now - startTime).TotalSeconds:F1}s)");
                        loggedWarning = true;
                    }

                    // Small async delay before retry
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                // Only log final timeout message
                if (loggedWarning)
                {
                    AppLogger.LogDebug($"Failed to acquire lock on {_lockFilePath} after {timeout.TotalSeconds}s timeout");
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Releases the cross-process lock.
        /// </summary>
        public void Release()
        {
            if (_disposed)
                return;

            try
            {
                // Close and dispose the file stream first
                if (_lockFileStream != null)
                {
                    _lockFileStream.Dispose();
                    _lockFileStream = null;
                    AppLogger.LogDebug($"Released file lock for {_lockFilePath}");
                }

                // Then release the mutex only if we own it
                if (_mutexOwned)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                        _mutexOwned = false;
                        AppLogger.LogDebug($"Released mutex for {_lockFilePath}");
                    }
                    catch (ApplicationException ex)
                    {
                        // Mutex was not owned by current thread
                        AppLogger.LogDebug($"Could not release mutex for {_lockFilePath}: {ex.Message}");
                        _mutexOwned = false;
                    }
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

            _disposed = true;
            Release();
            _mutex.Dispose();
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
