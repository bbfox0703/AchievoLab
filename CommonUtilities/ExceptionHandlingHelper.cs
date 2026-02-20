using System;
using System.Threading.Tasks;

namespace CommonUtilities
{
    /// <summary>
    /// Provides unified exception handling utilities for applications.
    /// Shared across AnSAM, RunGame, and MyOwnGames for consistent exception logging.
    /// </summary>
    public static class ExceptionHandlingHelper
    {
        /// <summary>
        /// Logs an exception with full details including inner exceptions and stack traces.
        /// </summary>
        /// <param name="ex">The exception to log.</param>
        /// <param name="context">A description of where the exception occurred (e.g., "UI Thread", "Background Thread").</param>
        public static void LogException(Exception ex, string context)
        {
            AppLogger.LogDebug($"=== EXCEPTION ({context}) ===");
            AppLogger.LogDebug($"Exception Type: {ex.GetType().FullName}");
            AppLogger.LogDebug($"Message: {ex.Message}");
            AppLogger.LogDebug($"Stack Trace: {ex.StackTrace}");

            if (ex.InnerException != null)
            {
                AppLogger.LogDebug($"Inner Exception: {ex.InnerException.GetType().FullName}");
                AppLogger.LogDebug($"Inner Message: {ex.InnerException.Message}");
                AppLogger.LogDebug($"Inner Stack Trace: {ex.InnerException.StackTrace}");
            }

            AppLogger.LogDebug($"=== END EXCEPTION ===");
        }

        /// <summary>
        /// Logs an AggregateException with all inner exceptions.
        /// </summary>
        /// <param name="ex">The AggregateException to log.</param>
        /// <param name="context">A description of where the exception occurred.</param>
        public static void LogAggregateException(AggregateException ex, string context)
        {
            AppLogger.LogDebug($"=== AGGREGATE EXCEPTION ({context}) ===");
            AppLogger.LogDebug($"Exception Type: {ex.GetType().FullName}");
            AppLogger.LogDebug($"Message: {ex.Message}");

            foreach (var innerEx in ex.InnerExceptions)
            {
                AppLogger.LogDebug($"  Inner Exception: {innerEx.GetType().FullName}");
                AppLogger.LogDebug($"  Inner Message: {innerEx.Message}");
                AppLogger.LogDebug($"  Inner Stack Trace: {innerEx.StackTrace}");
            }

            AppLogger.LogDebug($"=== END AGGREGATE EXCEPTION ===");
        }

        /// <summary>
        /// Handler for AppDomain.UnhandledException events (background thread exceptions).
        /// </summary>
        public static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            AppLogger.LogDebug("=== APPDOMAIN UNHANDLED EXCEPTION (Background Thread) ===");

            if (e.ExceptionObject is Exception ex)
            {
                AppLogger.LogDebug($"Exception Type: {ex.GetType().FullName}");
                AppLogger.LogDebug($"Message: {ex.Message}");
                AppLogger.LogDebug($"Stack Trace: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    AppLogger.LogDebug($"Inner Exception: {ex.InnerException.GetType().FullName}");
                    AppLogger.LogDebug($"Inner Message: {ex.InnerException.Message}");
                    AppLogger.LogDebug($"Inner Stack Trace: {ex.InnerException.StackTrace}");
                }
            }
            else
            {
                AppLogger.LogDebug($"Non-Exception object thrown: {e.ExceptionObject}");
            }

            AppLogger.LogDebug($"Is Terminating: {e.IsTerminating}");
            AppLogger.LogDebug("=== END APPDOMAIN UNHANDLED EXCEPTION ===");
        }

        /// <summary>
        /// Handler for TaskScheduler.UnobservedTaskException events (fire-and-forget task exceptions).
        /// </summary>
        public static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e, bool setObserved = true)
        {
            LogAggregateException(e.Exception, "Fire-and-forget Task - UnobservedTaskException");

            if (setObserved)
            {
                e.SetObserved();
            }
        }

        /// <summary>
        /// Registers global exception handlers (AppDomain and TaskScheduler).
        /// </summary>
        public static void RegisterGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += (sender, e) => OnUnobservedTaskException(sender, e, true);
        }
    }
}
