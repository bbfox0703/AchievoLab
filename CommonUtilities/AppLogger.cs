using System;
using Serilog;
using Serilog.Core;

namespace CommonUtilities
{
    /// <summary>
    /// Application logger using Serilog
    /// Replaces the old DebugLogger with a more robust logging solution
    /// </summary>
    public static class AppLogger
    {
        private static ILogger? _logger;
        private static readonly object _logLock = new();

        /// <summary>
        /// Initializes the logger with the provided Serilog logger instance
        /// This should be called once during application startup
        /// </summary>
        public static void Initialize(ILogger logger)
        {
            lock (_logLock)
            {
                _logger = logger;
            }
        }

        /// <summary>
        /// Gets whether the logger has been initialized
        /// </summary>
        public static bool IsInitialized => _logger != null;

        /// <summary>
        /// Logs a debug message
        /// </summary>
        public static void LogDebug(string message)
        {
            _logger?.Debug(message);
        }

        /// <summary>
        /// Logs an informational message
        /// </summary>
        public static void LogInfo(string message)
        {
            _logger?.Information(message);
        }

        /// <summary>
        /// Logs a warning message
        /// </summary>
        public static void LogWarning(string message)
        {
            _logger?.Warning(message);
        }

        /// <summary>
        /// Logs an error message
        /// </summary>
        public static void LogError(string message)
        {
            _logger?.Error(message);
        }

        /// <summary>
        /// Logs an error message with exception
        /// </summary>
        public static void LogError(string message, Exception exception)
        {
            _logger?.Error(exception, message);
        }

        /// <summary>
        /// Logs a fatal error message
        /// </summary>
        public static void LogFatal(string message)
        {
            _logger?.Fatal(message);
        }

        /// <summary>
        /// Logs a fatal error message with exception
        /// </summary>
        public static void LogFatal(string message, Exception exception)
        {
            _logger?.Fatal(exception, message);
        }

        /// <summary>
        /// Logs a Steam achievement set operation
        /// </summary>
        public static void LogAchievementSet(string achievementId, bool achieved, bool isDebugMode)
        {
            var mode = isDebugMode ? "[MOCK]" : "[REAL]";
            _logger?.Debug("{Mode} SetAchievement: {AchievementId} = {Achieved}", mode, achievementId, achieved);
        }

        /// <summary>
        /// Logs a Steam stat set operation
        /// </summary>
        public static void LogStatSet(string statId, object value, bool isDebugMode)
        {
            var mode = isDebugMode ? "[MOCK]" : "[REAL]";
            _logger?.Debug("{Mode} SetStat: {StatId} = {Value}", mode, statId, value);
        }

        /// <summary>
        /// Logs a Steam store stats operation
        /// </summary>
        public static void LogStoreStats(bool isDebugMode)
        {
            if (isDebugMode)
            {
                _logger?.Debug("[MOCK] StoreStats: Changes would be committed to Steam (but not in debug mode)");
            }
            else
            {
                _logger?.Debug("[REAL] StoreStats: Committing changes to Steam");
            }
        }

        /// <summary>
        /// Logs a Steam reset all stats operation
        /// </summary>
        public static void LogResetAllStats(bool achievementsToo, bool isDebugMode)
        {
            var mode = isDebugMode ? "[MOCK]" : "[REAL]";
            var action = isDebugMode ? "(would reset but not in debug mode)" : string.Empty;
            _logger?.Debug("{Mode} ResetAllStats: achievements={AchievementsToo} {Action}", mode, achievementsToo, action);
        }

        /// <summary>
        /// Event that can be subscribed to for log messages
        /// Note: This is provided for compatibility with old DebugLogger.OnLog event
        /// Consider using Serilog sinks instead for more flexibility
        /// </summary>
        public static event Action<string>? OnLog;

        /// <summary>
        /// Internal method to trigger OnLog event
        /// Can be used with a custom Serilog sink if needed
        /// </summary>
        internal static void TriggerOnLog(string message)
        {
            OnLog?.Invoke(message);
        }

        /// <summary>
        /// Gets whether debug mode is enabled
        /// </summary>
        public static bool IsDebugMode
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Clears the log file (for compatibility with old DebugLogger)
        /// Note: With Serilog, this is a no-op as logs are managed by file sinks
        /// </summary>
        public static void ClearLog()
        {
            // For Serilog, clearing logs would require closing file handles
            // and deleting files, which is complex and not recommended
            // This method is kept for compatibility but does nothing
            _logger?.Information("ClearLog called (no-op with Serilog)");
        }

        /// <summary>
        /// Flushes any buffered log messages
        /// Should be called before application exit
        /// </summary>
        public static void Flush()
        {
            if (_logger is Logger logger)
            {
                logger.Dispose();
            }
        }
    }
}
