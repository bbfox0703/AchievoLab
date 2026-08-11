using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommonUtilities;
using Xunit;

namespace CommonUtilities.Tests
{
    /// <summary>
    /// Tests for the cross-process file lock.
    /// </summary>
    public class CrossProcessFileLockTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _target;

        public CrossProcessFileLockTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"CrossProcessFileLockTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _target = Path.Combine(_dir, "data.xml");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        /// <summary>
        /// Dispose used to set _disposed before calling Release, and Release started with
        /// `if (_disposed) return;` — so `using var fileLock = ...` never actually released
        /// anything and the lock file handle stayed open for the life of the process.
        /// </summary>
        [Fact]
        public void DisposeReleasesTheLock()
        {
            using (var first = new CrossProcessFileLock(_target))
            {
                Assert.True(first.TryAcquire(TimeSpan.FromSeconds(1)));
            }

            using var second = new CrossProcessFileLock(_target);
            Assert.True(second.TryAcquire(TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void DisposeDeletesTheLockFile()
        {
            var lockFile = _target + ".lock";

            using (var fileLock = new CrossProcessFileLock(_target))
            {
                Assert.True(fileLock.TryAcquire(TimeSpan.FromSeconds(1)));
                Assert.True(File.Exists(lockFile));
            }

            Assert.False(File.Exists(lockFile));
        }

        [Fact]
        public void SecondHolderTimesOutWhileTheFirstHoldsTheLock()
        {
            using var held = new CrossProcessFileLock(_target);
            Assert.True(held.TryAcquire(TimeSpan.FromSeconds(1)));

            using var contender = new CrossProcessFileLock(_target);
            Assert.False(contender.TryAcquire(TimeSpan.FromMilliseconds(200)));
        }

        [Fact]
        public void ReleaseIsIdempotent()
        {
            using var fileLock = new CrossProcessFileLock(_target);
            Assert.True(fileLock.TryAcquire(TimeSpan.FromSeconds(1)));

            fileLock.Release();
            fileLock.Release();

            Assert.True(fileLock.TryAcquire(TimeSpan.FromSeconds(1)));
        }

        /// <summary>
        /// The whole reason the Mutex was removed: acquiring on one thread and releasing on
        /// another is exactly what TryAcquireAsync does after resuming from an await, and a Mutex
        /// may only be released by the thread that took it.
        /// </summary>
        [Fact]
        public async Task CanBeAcquiredAndReleasedOnDifferentThreads()
        {
            using var fileLock = new CrossProcessFileLock(_target);

            var acquiredOn = await Task.Run(() =>
            {
                Assert.True(fileLock.TryAcquire(TimeSpan.FromSeconds(1)));
                return Environment.CurrentManagedThreadId;
            });

            await Task.Run(() =>
            {
                Assert.NotEqual(acquiredOn, Environment.CurrentManagedThreadId);
                fileLock.Release();
            });

            // Releasing from the other thread really worked, so it can be taken again.
            Assert.True(fileLock.TryAcquire(TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public async Task TryAcquireAsyncSucceedsWhenUncontended()
        {
            using var fileLock = new CrossProcessFileLock(_target);

            Assert.True(await fileLock.TryAcquireAsync(TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public async Task TryAcquireAsyncReturnsFalseWhenCancelled()
        {
            using var held = new CrossProcessFileLock(_target);
            Assert.True(held.TryAcquire(TimeSpan.FromSeconds(1)));

            using var contender = new CrossProcessFileLock(_target);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            Assert.False(await contender.TryAcquireAsync(TimeSpan.FromSeconds(30), cts.Token));
        }
    }
}
