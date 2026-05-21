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
        // _state guards _scheduledAchievements, _pendingUnlocks, _lastStoreTime —
        // mutated from the System.Threading.Timer thread + UI thread.
        private readonly object _state = new();
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
            // Normalize to UTC so DST shifts don't shift the schedule.
            // Unspecified kind is treated as Local (typical for callers using DateTime.Now-based input).
            var unlockUtc = unlockTime.Kind == DateTimeKind.Utc
                ? unlockTime
                : unlockTime.ToUniversalTime();

            if (unlockUtc <= DateTime.UtcNow)
            {
                AppLogger.LogDebug($"Unlock time {unlockTime} is in the past, ignoring schedule for {achievementId}");
                return;
            }

            lock (_state)
            {
                _scheduledAchievements[achievementId] = unlockUtc;
            }
            AppLogger.LogDebug($"Scheduled achievement {achievementId} to unlock at {unlockTime}");
        }

        /// <summary>
        /// Cancels a previously scheduled achievement unlock.
        /// </summary>
        /// <param name="achievementId">The unique achievement identifier.</param>
        public void CancelSchedule(string achievementId)
        {
            bool removed;
            lock (_state)
            {
                removed = _scheduledAchievements.Remove(achievementId);
            }
            if (removed)
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
            lock (_state)
            {
                // Return as Local so existing UI binding semantics are preserved.
                return _scheduledAchievements.TryGetValue(achievementId, out var timeUtc)
                    ? timeUtc.ToLocalTime()
                    : null;
            }
        }

        /// <summary>
        /// Gets a copy of all currently scheduled achievements and their unlock times.
        /// </summary>
        /// <returns>A dictionary mapping achievement IDs to their scheduled unlock times (local time).</returns>
        public Dictionary<string, DateTime> GetAllScheduledAchievements()
        {
            lock (_state)
            {
                return _scheduledAchievements.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocalTime());
            }
        }

        /// <summary>
        /// Notifies the service that stats have been reloaded from Steam.
        /// Updates the status message with information about active timers.
        /// </summary>
        public void NotifyStatsReloaded()
        {
            int count;
            double? secondsToNext = null;
            lock (_state)
            {
                count = _scheduledAchievements.Count;
                if (count > 0)
                {
                    var nextUtc = GetNextScheduledTimeUtcUnlocked();
                    if (nextUtc.HasValue)
                    {
                        secondsToNext = (nextUtc.Value - DateTime.UtcNow).TotalSeconds;
                    }
                }
            }
            if (count > 0 && secondsToNext.HasValue)
            {
                StatusUpdated?.Invoke($"Stats reloaded. {count} timer{(count != 1 ? "s" : "")} active, next in {secondsToNext.Value:F0}s");
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
                var nowUtc = DateTime.UtcNow;

                // Snapshot due achievements under lock so we can release it during Steam I/O.
                List<KeyValuePair<string, DateTime>> achievementsToUnlock;
                lock (_state)
                {
                    achievementsToUnlock = _scheduledAchievements
                        .Where(kvp => kvp.Value <= nowUtc)
                        .ToList();
                }

                bool hasNewUnlocks = false;
                foreach (var achievement in achievementsToUnlock)
                {
                    var achievementId = achievement.Key;
                    var scheduledTimeUtc = achievement.Value;

                    AppLogger.LogDebug($"Unlocking scheduled achievement {achievementId} (scheduled for {scheduledTimeUtc.ToLocalTime()}, now {nowUtc.ToLocalTime()})");

                    // Set the achievement as achieved
                    if (_gameStatsService.SetAchievement(achievementId, true))
                    {
                        AppLogger.LogDebug($"Successfully set achievement {achievementId} to unlocked");
                        lock (_state)
                        {
                            _pendingUnlocks.Add(achievementId);
                        }
                        hasNewUnlocks = true;

                        // Notify UI to update the achievement's IsAchieved property and icon
                        AchievementUnlocked?.Invoke(achievementId);
                    }
                    else
                    {
                        AppLogger.LogDebug($"Failed to unlock achievement {achievementId}");
                    }

                    lock (_state)
                    {
                        _scheduledAchievements.Remove(achievementId);
                    }
                }

                // Decide whether to store changes to Steam now
                if (hasNewUnlocks)
                {
                    DateTime? nextScheduledTimeUtc;
                    int pendingCount;
                    lock (_state)
                    {
                        nextScheduledTimeUtc = GetNextScheduledTimeUtcUnlocked();
                        pendingCount = _pendingUnlocks.Count;
                    }

                    bool shouldStore = false;

                    if (nextScheduledTimeUtc == null)
                    {
                        shouldStore = true;
                        AppLogger.LogDebug("No more scheduled achievements, storing changes now");
                    }
                    else if ((nextScheduledTimeUtc.Value - nowUtc).TotalSeconds > 12)
                    {
                        shouldStore = true;
                        AppLogger.LogDebug($"Next achievement is {(nextScheduledTimeUtc.Value - nowUtc).TotalSeconds:F1} seconds away, storing changes now");
                    }
                    else
                    {
                        AppLogger.LogDebug($"Next achievement is in {(nextScheduledTimeUtc.Value - nowUtc).TotalSeconds:F1} seconds, delaying store");
                    }

                    if (shouldStore)
                    {
                        _gameStatsService.StoreStats();
                        int unlockCount;
                        lock (_state)
                        {
                            _lastStoreTime = nowUtc;
                            unlockCount = _pendingUnlocks.Count;
                            _pendingUnlocks.Clear();
                        }
                        AppLogger.LogDebug($"Stored {unlockCount} achievement unlocks to Steam");
                        StatusUpdated?.Invoke($"Stored {unlockCount} achievement unlock{(unlockCount != 1 ? "s" : "")} to Steam at {nowUtc.ToLocalTime():HH:mm:ss}");
                    }
                    else if (pendingCount > 0)
                    {
                        StatusUpdated?.Invoke($"Pending {pendingCount} achievement unlock{(pendingCount != 1 ? "s" : "")}, next store in {(nextScheduledTimeUtc!.Value - nowUtc).TotalSeconds:F0}s");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in CheckScheduledAchievements: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the next scheduled UTC unlock time. Caller must hold <c>_state</c>.
        /// </summary>
        private DateTime? GetNextScheduledTimeUtcUnlocked()
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
