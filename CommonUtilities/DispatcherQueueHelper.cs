using Avalonia.Threading;
using System;

namespace CommonUtilities
{
    /// <summary>
    /// Provides utilities for Avalonia Dispatcher operations.
    /// Shared across AnSAM, RunGame, and MyOwnGames for consistent dispatcher handling.
    /// </summary>
    public static class DispatcherHelper
    {
        /// <summary>
        /// Attempts to post a callback to run on the UI thread with error logging.
        /// Uses Normal priority.
        /// </summary>
        public static bool TryPostSafe(Action callback)
        {
            return TryPostSafe(callback, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Attempts to post a callback to run on the UI thread with error logging.
        /// </summary>
        public static bool TryPostSafe(Action callback, DispatcherPriority priority)
        {
            try
            {
                Dispatcher.UIThread.Post(callback, priority);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"DispatcherHelper.TryPostSafe: Error posting callback: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Posts a callback to the UI thread and logs a warning if it fails.
        /// Uses Normal priority.
        /// </summary>
        public static void PostOrWarn(Action callback, string warningMessage)
        {
            PostOrWarn(callback, warningMessage, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Posts a callback to the UI thread and logs a warning if it fails.
        /// </summary>
        public static void PostOrWarn(Action callback, string warningMessage, DispatcherPriority priority)
        {
            if (!TryPostSafe(callback, priority))
            {
                AppLogger.LogDebug($"DispatcherHelper.PostOrWarn: {warningMessage}");
            }
        }

        /// <summary>
        /// Executes an action on the UI thread if not already on it, otherwise executes immediately.
        /// Uses Normal priority.
        /// </summary>
        public static void ExecuteOnUIThread(Action action)
        {
            ExecuteOnUIThread(action, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Executes an action on the UI thread if not already on it, otherwise executes immediately.
        /// </summary>
        public static void ExecuteOnUIThread(Action action, DispatcherPriority priority)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                TryPostSafe(action, priority);
            }
        }
    }
}
