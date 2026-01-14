using static RunGame.Steam.SteamGameClient;

namespace RunGame.Steam
{
    /// <summary>
    /// Abstraction interface for Steam UserStats and Apps API functionality.
    /// Implemented by both SteamGameClient (legacy vtable-based) and ModernSteamClient (SDK 162 flat API).
    /// </summary>
    public interface ISteamUserStats
    {
        /// <summary>
        /// Requests user statistics from Steam for the specified game.
        /// </summary>
        /// <param name="gameId">The Steam AppID.</param>
        /// <returns>True if the request was initiated successfully; false otherwise.</returns>
        bool RequestUserStats(uint gameId);

        /// <summary>
        /// Gets the achievement status and unlock time for a specific achievement.
        /// </summary>
        /// <param name="id">The unique achievement identifier.</param>
        /// <param name="achieved">Receives true if the achievement is unlocked; false otherwise.</param>
        /// <param name="unlockTime">Receives the Unix timestamp when the achievement was unlocked (0 if locked).</param>
        /// <returns>True if the achievement data was retrieved successfully; false otherwise.</returns>
        bool GetAchievementAndUnlockTime(string id, out bool achieved, out uint unlockTime);

        /// <summary>
        /// Sets an achievement to achieved or locked state (batched until StoreStats is called).
        /// </summary>
        /// <param name="id">The unique achievement identifier.</param>
        /// <param name="achieved">True to unlock the achievement, false to lock it.</param>
        /// <returns>True if the operation succeeded; false otherwise.</returns>
        bool SetAchievement(string id, bool achieved);

        /// <summary>
        /// Gets the current value of an integer statistic.
        /// </summary>
        /// <param name="name">The statistic name.</param>
        /// <param name="value">Receives the current value.</param>
        /// <returns>True if the statistic was retrieved successfully; false otherwise.</returns>
        bool GetStatValue(string name, out int value);

        /// <summary>
        /// Gets the current value of a floating-point statistic.
        /// </summary>
        /// <param name="name">The statistic name.</param>
        /// <param name="value">Receives the current value.</param>
        /// <returns>True if the statistic was retrieved successfully; false otherwise.</returns>
        bool GetStatValue(string name, out float value);

        /// <summary>
        /// Sets an integer statistic to a new value (batched until StoreStats is called).
        /// </summary>
        /// <param name="name">The statistic name.</param>
        /// <param name="value">The new value.</param>
        /// <returns>True if the operation succeeded; false otherwise.</returns>
        bool SetStatValue(string name, int value);

        /// <summary>
        /// Sets a floating-point statistic to a new value (batched until StoreStats is called).
        /// </summary>
        /// <param name="name">The statistic name.</param>
        /// <param name="value">The new value.</param>
        /// <returns>True if the operation succeeded; false otherwise.</returns>
        bool SetStatValue(string name, float value);

        /// <summary>
        /// Commits all pending achievement and statistic changes to Steam.
        /// </summary>
        /// <returns>True if the changes were stored successfully; false otherwise.</returns>
        bool StoreStats();

        /// <summary>
        /// Resets all statistics (and optionally achievements) to their default values.
        /// </summary>
        /// <param name="achievementsToo">True to also reset achievements; false to only reset statistics.</param>
        /// <returns>True if the reset succeeded; false otherwise.</returns>
        bool ResetAllStats(bool achievementsToo);

        /// <summary>
        /// Processes Steam callbacks (for legacy client implementations).
        /// </summary>
        void RunCallbacks();

        /// <summary>
        /// Checks if the current user owns/is subscribed to a specific app.
        /// </summary>
        /// <param name="gameId">The Steam AppID.</param>
        /// <returns>True if the user owns the app; false otherwise.</returns>
        bool IsSubscribedApp(uint gameId);

        /// <summary>
        /// Gets application metadata for a specific app.
        /// </summary>
        /// <param name="appId">The Steam AppID.</param>
        /// <param name="key">The metadata key (e.g., "name", "type", "state").</param>
        /// <returns>The metadata value, or null if not found.</returns>
        string? GetAppData(uint appId, string key);

        /// <summary>
        /// Registers a callback to receive UserStatsReceived events from Steam.
        /// </summary>
        /// <param name="callback">The callback action to invoke when stats are received.</param>
        void RegisterUserStatsCallback(System.Action<UserStatsReceived> callback);

        /// <summary>
        /// Gets the current Steam UI language code.
        /// </summary>
        /// <returns>The language code (e.g., "english", "tchinese", "japanese").</returns>
        string GetCurrentGameLanguage();
    }
}
