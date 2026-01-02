using Microsoft.UI.Dispatching;
using System;

namespace CommonUtilities
{
    /// <summary>
    /// Provides extension methods and utilities for DispatcherQueue operations.
    /// Shared across AnSAM, RunGame, and MyOwnGames for consistent dispatcher handling.
    /// </summary>
    public static class DispatcherQueueHelper
    {
        /// <summary>
        /// Attempts to enqueue a callback to run on the dispatcher queue with error logging.
        /// </summary>
        /// <param name="dispatcher">The dispatcher queue</param>
        /// <param name="callback">The callback to execute</param>
        /// <param name="priority">The priority for the callback (default: Normal)</param>
        /// <returns>True if the callback was successfully enqueued, false otherwise</returns>
        public static bool TryEnqueueSafe(
            this DispatcherQueue dispatcher,
            DispatcherQueueHandler callback,
            DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
        {
            if (dispatcher == null)
            {
                DebugLogger.LogDebug("DispatcherQueueHelper.TryEnqueueSafe: dispatcher is null");
                return false;
            }

            try
            {
                return dispatcher.TryEnqueue(priority, callback);
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"DispatcherQueueHelper.TryEnqueueSafe: Error enqueuing callback: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Enqueues a callback and logs a warning if it fails.
        /// </summary>
        /// <param name="dispatcher">The dispatcher queue</param>
        /// <param name="callback">The callback to execute</param>
        /// <param name="warningMessage">Custom warning message if enqueue fails</param>
        /// <param name="priority">The priority for the callback (default: Normal)</param>
        public static void EnqueueOrWarn(
            this DispatcherQueue dispatcher,
            DispatcherQueueHandler callback,
            string warningMessage,
            DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
        {
            if (!TryEnqueueSafe(dispatcher, callback, priority))
            {
                DebugLogger.LogDebug($"DispatcherQueueHelper.EnqueueOrWarn: {warningMessage}");
            }
        }

        /// <summary>
        /// Executes an action on the UI thread if not already on it, otherwise executes immediately.
        /// Note: This method is synchronous only if already on the UI thread.
        /// </summary>
        /// <param name="dispatcher">The dispatcher queue</param>
        /// <param name="action">The action to execute</param>
        /// <param name="priority">The priority if dispatching is needed (default: Normal)</param>
        public static void ExecuteOnUIThread(
            this DispatcherQueue dispatcher,
            Action action,
            DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
        {
            if (dispatcher == null)
            {
                DebugLogger.LogDebug("DispatcherQueueHelper.ExecuteOnUIThread: dispatcher is null, executing synchronously");
                action();
                return;
            }

            if (dispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                TryEnqueueSafe(dispatcher, () => action(), priority);
            }
        }
    }
}
