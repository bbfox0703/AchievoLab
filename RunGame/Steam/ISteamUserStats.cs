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
    }
}