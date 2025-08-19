using System;
using System.Diagnostics;
using System.IO;

namespace AnSAM.RunGame.Services
{
    public static class DebugLogger
    {
        private static readonly string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnSAM_RunGame", "debug.log");

        static DebugLogger()
        {
            // 確保日誌目錄存在
            var logDir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
        }

        /// <summary>
        /// 記錄調試信息（僅在 Debug 模式下輸出）
        /// </summary>
        public static void LogDebug(string message)
        {
#if DEBUG
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logMessage = $"[{timestamp}] DEBUG: {message}";
            
            // 輸出到控制台
            Debug.WriteLine(logMessage);
            Console.WriteLine(logMessage);
            
            // 寫入到日誌文件
            try
            {
                File.AppendAllText(LogFilePath, logMessage + Environment.NewLine);
            }
            catch
            {
                // 忽略日誌寫入錯誤
            }
#endif
        }

        /// <summary>
        /// 記錄成就設置操作
        /// </summary>
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

        /// <summary>
        /// 記錄統計設置操作
        /// </summary>
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

        /// <summary>
        /// 記錄存儲操作
        /// </summary>
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

        /// <summary>
        /// 記錄重置操作
        /// </summary>
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

        /// <summary>
        /// 清除日誌文件
        /// </summary>
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
            catch
            {
                // 忽略清除錯誤
            }
#endif
        }

        /// <summary>
        /// 檢查是否為 Debug 模式
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
    }
}