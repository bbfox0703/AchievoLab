using static RunGame.Steam.SteamGameClient;

namespace RunGame.Steam
{
    public interface ISteamUserStats
    {
        bool RequestUserStats(uint gameId);
        bool GetAchievementAndUnlockTime(string id, out bool achieved, out uint unlockTime);
        bool SetAchievement(string id, bool achieved);
        bool GetStatValue(string name, out int value);
        bool GetStatValue(string name, out float value);
        bool SetStatValue(string name, int value);
        bool SetStatValue(string name, float value);
        bool StoreStats();
        bool ResetAllStats(bool achievementsToo);
        void RunCallbacks();
        
        // Steam Apps functionality needed by MainWindow
        bool IsSubscribedApp(uint gameId);
        string? GetAppData(uint appId, string key);
        
        // Additional functionality needed by GameStatsService
        void RegisterUserStatsCallback(System.Action<UserStatsReceived> callback);
        string GetCurrentGameLanguage();
    }
}
