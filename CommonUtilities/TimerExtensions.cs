using System;
using System.Threading;

namespace CommonUtilities
{
    /// <summary>
    /// Helpers for shutting down <see cref="Timer"/> instances deterministically.
    /// </summary>
    public static class TimerExtensions
    {
        /// <summary>
        /// Disposes <paramref name="timer"/> and blocks until any callback already running has
        /// returned, so the caller can then release resources that callback touches.
        /// </summary>
        /// <param name="timer">The timer to dispose.</param>
        /// <param name="timeout">How long to wait for an in-flight callback.</param>
        /// <param name="context">Name used in the log message if the wait times out.</param>
        /// <returns>True if no callback is still running when this returns.</returns>
        /// <remarks>
        /// The parameterless <see cref="Timer.Dispose()"/> does NOT wait for a running callback;
        /// only the <see cref="Timer.Dispose(WaitHandle)"/> overload signals once the last one has
        /// finished. Sleeping for a fixed interval instead is a guess, not a synchronisation
        /// primitive.
        ///
        /// The wait handle is disposed only once it has been signalled. On timeout the timer still
        /// owns it and will signal it later, so disposing it here would break that contract and
        /// raise ObjectDisposedException on a thread pool thread — it is left to the GC instead.
        /// </remarks>
        public static bool DisposeAndWait(this Timer timer, TimeSpan timeout, string context)
        {
            ArgumentNullException.ThrowIfNull(timer);

            var callbacksDone = new ManualResetEvent(false);

            if (!timer.Dispose(callbacksDone))
            {
                // Already disposed: nothing will ever signal the handle.
                callbacksDone.Dispose();
                return true;
            }

            if (callbacksDone.WaitOne(timeout))
            {
                callbacksDone.Dispose();
                return true;
            }

            AppLogger.LogDebug(
                $"{context}: timed out after {timeout.TotalSeconds:F1}s waiting for the timer callback to finish; " +
                "continuing without that guarantee.");
            return false;
        }
    }
}
