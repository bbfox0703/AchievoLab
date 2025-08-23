using System;
using System.Diagnostics;
using System.IO;

namespace CommonUtilities
{
    public static class DebugLogger
    {
        public static event Action<string>? OnLog;
        private static readonly string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AchievoLab", "debug.log");
        private static readonly object _logLock = new();

        static DebugLogger()
        {
            var logDir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
        }

        public static void LogDebug(string message)
        {
#if DEBUG
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logMessage = $"[{timestamp}] DEBUG: {message}";

            OnLog?.Invoke(logMessage);

            try
            {
                Debug.WriteLine(logMessage);
            }
            catch { }

            if (Environment.UserInteractive || Debugger.IsAttached)
            {
                try { Console.WriteLine(logMessage); }
                catch { }
            }

            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(LogFilePath, logMessage + Environment.NewLine);
                }
            }
            catch { }
#endif
        }

        public static void LogAchievementSet(string achievementId, bool achieved, bool isDebugMode)
        {
            if (isDebugMode)
            {
                LogDebug($"[MOCK] SetAchievement: {achievementId} = {achieved}");
            }
            else
            {
                LogDebug($"[REAL] SetAchievement: {achievementId} = {achieved}");
            }
        }

        public static void LogStatSet(string statId, object value, bool isDebugMode)
        {
            if (isDebugMode)
            {
                LogDebug($"[MOCK] SetStat: {statId} = {value}");
            }
            else
            {
                LogDebug($"[REAL] SetStat: {statId} = {value}");
            }
        }

        public static void LogStoreStats(bool isDebugMode)
        {
            if (isDebugMode)
            {
                LogDebug("[MOCK] StoreStats: Changes would be committed to Steam (but not in debug mode)");
            }
            else
            {
                LogDebug("[REAL] StoreStats: Committing changes to Steam");
            }
        }

        public static void LogResetAllStats(bool achievementsToo, bool isDebugMode)
        {
            if (isDebugMode)
            {
                LogDebug($"[MOCK] ResetAllStats: achievements={achievementsToo} (would reset but not in debug mode)");
            }
            else
            {
                LogDebug($"[REAL] ResetAllStats: achievements={achievementsToo}");
            }
        }

        public static void ClearLog()
        {
#if DEBUG
            try
            {
                if (File.Exists(LogFilePath))
                {
                    File.WriteAllText(LogFilePath, string.Empty);
                }
            }
            catch { }
#endif
        }

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
    }
}
