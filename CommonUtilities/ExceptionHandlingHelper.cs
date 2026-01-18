using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace CommonUtilities
{
    /// <summary>
    /// Provides unified exception handling utilities for WinUI 3 applications.
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
        /// Handler for WinUI 3 UnhandledException events (UI thread exceptions).
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The exception event args.</param>
        /// <param name="markAsHandled">Whether to mark the exception as handled (prevents app crash).</param>
        public static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e, bool markAsHandled = true)
        {
            LogException(e.Exception, "UI Thread - UnhandledException");

            // Mark as handled to prevent crash (for debugging purposes)
            // Remove markAsHandled=true if you want the app to crash and show the error
            e.Handled = markAsHandled;
        }

        /// <summary>
        /// Handler for AppDomain.UnhandledException events (background thread exceptions).
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The exception event args.</param>
        public static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
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
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The exception event args.</param>
        /// <param name="setObserved">Whether to mark the exception as observed (prevents crash during GC).</param>
        public static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e, bool setObserved = true)
        {
            LogAggregateException(e.Exception, "Fire-and-forget Task - UnobservedTaskException");

            // Mark as observed to prevent crash during GC
            if (setObserved)
            {
                e.SetObserved();
            }
        }

        /// <summary>
        /// Registers all global exception handlers for a WinUI 3 application.
        /// </summary>
        /// <param name="app">The WinUI 3 Application instance.</param>
        /// <param name="onUnhandledException">Optional custom handler for UI thread exceptions.</param>
        /// <param name="markUIExceptionsAsHandled">Whether to mark UI exceptions as handled (prevents crash).</param>
        public static void RegisterGlobalExceptionHandlers(
            Application app,
            Action<object, Microsoft.UI.Xaml.UnhandledExceptionEventArgs>? onUnhandledException = null,
            bool markUIExceptionsAsHandled = true)
        {
            app.UnhandledException += (sender, e) =>
            {
                if (onUnhandledException != null)
                {
                    onUnhandledException(sender, e);
                }
                else
                {
                    OnUnhandledException(sender, e, markUIExceptionsAsHandled);
                }
            };

            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += (sender, e) => OnUnobservedTaskException(sender, e, true);
        }
    }
}
