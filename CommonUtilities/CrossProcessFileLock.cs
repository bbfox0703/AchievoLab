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
                // First, acquire the mutex
                bool mutexAcquired = _mutex.WaitOne(timeout);
                if (!mutexAcquired)
                {
                    DebugLogger.LogDebug($"Failed to acquire mutex for {_lockFilePath} within timeout");
                    return false;
                }

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

                    DebugLogger.LogDebug($"Acquired cross-process lock for {_lockFilePath}");
                    return true;
                }
                catch (IOException ex)
                {
                    // Failed to acquire file lock, release mutex
                    _mutex.ReleaseMutex();
                    DebugLogger.LogDebug($"Failed to acquire file lock for {_lockFilePath}: {ex.Message}");
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous process holding the mutex terminated without releasing it
                // We now own the mutex, try to acquire the file lock
                DebugLogger.LogDebug($"Acquired abandoned mutex for {_lockFilePath}");

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
                    _mutex.ReleaseMutex();
                    DebugLogger.LogDebug($"Failed to acquire file lock after abandoned mutex for {_lockFilePath}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Asynchronously acquires the cross-process lock with optional timeout.
        /// </summary>
        public async Task<bool> TryAcquireAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CrossProcessFileLock));

            if (timeout == default)
                timeout = TimeSpan.FromSeconds(30);

            // Use Task.Run to avoid blocking the async context
            return await Task.Run(() =>
            {
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        cts.CancelAfter(timeout);

                        while (!cts.Token.IsCancellationRequested)
                        {
                            if (TryAcquire(TimeSpan.FromMilliseconds(100)))
                                return true;

                            // Small delay before retry
                            Thread.Sleep(50);
                        }

                        return false;
                    }
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }, cancellationToken);
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
                    DebugLogger.LogDebug($"Released file lock for {_lockFilePath}");
                }

                // Then release the mutex
                _mutex.ReleaseMutex();
                DebugLogger.LogDebug($"Released mutex for {_lockFilePath}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error releasing lock for {_lockFilePath}: {ex.Message}");
            }
        }

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
    /// Helper class for using CrossProcessFileLock with using statement
    /// </summary>
    public class CrossProcessFileLockHandle : IDisposable
    {
        private readonly CrossProcessFileLock _lock;
        private readonly bool _acquired;

        public bool IsAcquired => _acquired;

        internal CrossProcessFileLockHandle(CrossProcessFileLock fileLock, bool acquired)
        {
            _lock = fileLock;
            _acquired = acquired;
        }

        public void Dispose()
        {
            if (_acquired)
            {
                _lock.Release();
            }
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
