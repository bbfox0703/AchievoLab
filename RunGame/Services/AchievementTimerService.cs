using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RunGame.Models;
using RunGame.Services;
using CommonUtilities;

namespace RunGame.Services
{
    /// <summary>
    /// Manages scheduled achievement unlocks with automatic batching to minimize Steam API calls.
    /// Checks every second for achievements that should be unlocked, then batches unlocks to reduce
    /// the number of StoreStats() calls sent to Steam.
    /// </summary>
    public class AchievementTimerService : IDisposable
    {
        private readonly GameStatsService _gameStatsService;
        private readonly Dictionary<string, DateTime> _scheduledAchievements = new();
        private readonly System.Threading.Timer _timer;
        private readonly List<string> _pendingUnlocks = new();
        private DateTime? _lastStoreTime = null;
        private bool _disposed = false;

        /// <summary>
        /// Occurs when the service status changes (e.g., achievement unlocked, stats stored).
        /// </summary>
        public event Action<string>? StatusUpdated;

        /// <summary>
        /// Occurs when an achievement is unlocked by the timer service.
        /// The string parameter is the achievement ID that was unlocked.
        /// </summary>
        public event Action<string>? AchievementUnlocked;

        /// <summary>
        /// Initializes a new instance of the <see cref="AchievementTimerService"/> class.
        /// Starts a timer that checks every second for scheduled achievements to unlock.
        /// </summary>
        /// <param name="gameStatsService">The game stats service used to unlock achievements and store stats.</param>
        public AchievementTimerService(GameStatsService gameStatsService)
        {
            _gameStatsService = gameStatsService;
            
            // Check every 1 second for achievements that should be unlocked
            _timer = new System.Threading.Timer(CheckScheduledAchievements, null, 
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            
            AppLogger.LogDebug("AchievementTimerService initialized");
        }

        /// <summary>
        /// Schedules an achievement to be unlocked at a specific time.
        /// If the unlock time is in the past, the schedule is ignored.
        /// </summary>
        /// <param name="achievementId">The unique achievement identifier.</param>
        /// <param name="unlockTime">The date and time when the achievement should be unlocked.</param>
        public void ScheduleAchievement(string achievementId, DateTime unlockTime)
        {
            if (unlockTime <= DateTime.Now)
            {
                AppLogger.LogDebug($"Unlock time {unlockTime} is in the past, ignoring schedule for {achievementId}");
                return;
            }

            _scheduledAchievements[achievementId] = unlockTime;
            AppLogger.LogDebug($"Scheduled achievement {achievementId} to unlock at {unlockTime}");
        }

        /// <summary>
        /// Cancels a previously scheduled achievement unlock.
        /// </summary>
        /// <param name="achievementId">The unique achievement identifier.</param>
        public void CancelSchedule(string achievementId)
        {
            if (_scheduledAchievements.Remove(achievementId))
            {
                AppLogger.LogDebug($"Cancelled scheduled unlock for achievement {achievementId}");
            }
        }

        /// <summary>
        /// Gets the scheduled unlock time for a specific achievement.
        /// </summary>
        /// <param name="achievementId">The unique achievement identifier.</param>
        /// <returns>The scheduled unlock time, or null if the achievement is not scheduled.</returns>
        public DateTime? GetScheduledTime(string achievementId)
        {
            return _scheduledAchievements.TryGetValue(achievementId, out var time) ? time : null;
        }

        /// <summary>
        /// Gets a copy of all currently scheduled achievements and their unlock times.
        /// </summary>
        /// <returns>A dictionary mapping achievement IDs to their scheduled unlock times.</returns>
        public Dictionary<string, DateTime> GetAllScheduledAchievements()
        {
            return new Dictionary<string, DateTime>(_scheduledAchievements);
        }

        /// <summary>
        /// Notifies the service that stats have been reloaded from Steam.
        /// Updates the status message with information about active timers.
        /// </summary>
        public void NotifyStatsReloaded()
        {
            // After stats reload, check if we still have pending timers
            if (_scheduledAchievements.Count > 0)
            {
                var now = DateTime.Now;
                var nextScheduledTime = GetNextScheduledTime();
                if (nextScheduledTime.HasValue)
                {
                    var secondsToNext = (nextScheduledTime.Value - now).TotalSeconds;
                    StatusUpdated?.Invoke($"Stats reloaded. {_scheduledAchievements.Count} timer{(_scheduledAchievements.Count != 1 ? "s" : "")} active, next in {secondsToNext:F0}s");
                }
            }
        }

        /// <summary>
        /// Timer callback that checks for achievements scheduled to be unlocked.
        /// Implements intelligent batching: unlocks achievements immediately but delays StoreStats()
        /// if another achievement is scheduled within 12 seconds to batch multiple unlocks together.
        /// </summary>
        /// <param name="state">Timer state (unused).</param>
        private void CheckScheduledAchievements(object? state)
        {
            try
            {
                var now = DateTime.Now;
                var achievementsToUnlock = _scheduledAchievements
                    .Where(kvp => kvp.Value <= now)
                    .ToList();

                bool hasNewUnlocks = false;
                foreach (var achievement in achievementsToUnlock)
                {
                    var achievementId = achievement.Key;
                    var scheduledTime = achievement.Value;

                    AppLogger.LogDebug($"Unlocking scheduled achievement {achievementId} (scheduled for {scheduledTime}, now {now})");

                    // Set the achievement as achieved
                    if (_gameStatsService.SetAchievement(achievementId, true))
                    {
                        AppLogger.LogDebug($"Successfully set achievement {achievementId} to unlocked");
                        _pendingUnlocks.Add(achievementId);
                        hasNewUnlocks = true;

                        // Notify UI to update the achievement's IsAchieved property and icon
                        AchievementUnlocked?.Invoke(achievementId);
                    }
                    else
                    {
                        AppLogger.LogDebug($"Failed to unlock achievement {achievementId}");
                    }

                    // Remove from scheduled list
                    _scheduledAchievements.Remove(achievementId);
                }

                // Decide whether to store changes to Steam now
                if (hasNewUnlocks)
                {
                    var nextScheduledTime = GetNextScheduledTime();
                    bool shouldStore = false;

                    if (nextScheduledTime == null)
                    {
                        // No more scheduled achievements, store now
                        shouldStore = true;
                        AppLogger.LogDebug("No more scheduled achievements, storing changes now");
                    }
                    else if ((nextScheduledTime.Value - now).TotalSeconds > 12)
                    {
                        // Next achievement is more than 12 seconds away, store now
                        shouldStore = true;
                        AppLogger.LogDebug($"Next achievement is {(nextScheduledTime.Value - now).TotalSeconds:F1} seconds away, storing changes now");
                    }
                    else
                    {
                        AppLogger.LogDebug($"Next achievement is in {(nextScheduledTime.Value - now).TotalSeconds:F1} seconds, delaying store");
                    }

                    if (shouldStore)
                    {
                        _gameStatsService.StoreStats();
                        _lastStoreTime = now;
                        var unlockCount = _pendingUnlocks.Count;
                        AppLogger.LogDebug($"Stored {unlockCount} achievement unlocks to Steam");
                        StatusUpdated?.Invoke($"Stored {unlockCount} achievement unlock{(unlockCount != 1 ? "s" : "")} to Steam at {now:HH:mm:ss}");
                        _pendingUnlocks.Clear();
                    }
                    else if (_pendingUnlocks.Count > 0)
                    {
                        StatusUpdated?.Invoke($"Pending {_pendingUnlocks.Count} achievement unlock{(_pendingUnlocks.Count != 1 ? "s" : "")}, next store in {(nextScheduledTime!.Value - now).TotalSeconds:F0}s");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in CheckScheduledAchievements: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the next scheduled unlock time from all pending achievements.
        /// </summary>
        /// <returns>The earliest scheduled unlock time, or null if no achievements are scheduled.</returns>
        private DateTime? GetNextScheduledTime()
        {
            if (_scheduledAchievements.Count == 0)
                return null;

            return _scheduledAchievements.Values.Min();
        }

        /// <summary>
        /// Releases resources used by the service, stopping the timer.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _timer?.Dispose();
                _disposed = true;
                AppLogger.LogDebug("AchievementTimerService disposed");
            }
        }
    }
}
