using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RunGame.Models;
using RunGame.Services;
using CommonUtilities;

namespace RunGame.Services
{
    public class AchievementTimerService : IDisposable
    {
        private readonly GameStatsService _gameStatsService;
        private readonly Dictionary<string, DateTime> _scheduledAchievements = new();
        private readonly System.Threading.Timer _timer;
        private readonly List<string> _pendingUnlocks = new();
        private DateTime? _lastStoreTime = null;
        private bool _disposed = false;

        public event Action<string>? StatusUpdated;

        public AchievementTimerService(GameStatsService gameStatsService)
        {
            _gameStatsService = gameStatsService;
            
            // Check every 1 second for achievements that should be unlocked
            _timer = new System.Threading.Timer(CheckScheduledAchievements, null, 
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            
            DebugLogger.LogDebug("AchievementTimerService initialized");
        }

        public void ScheduleAchievement(string achievementId, DateTime unlockTime)
        {
            if (unlockTime <= DateTime.Now)
            {
                DebugLogger.LogDebug($"Unlock time {unlockTime} is in the past, ignoring schedule for {achievementId}");
                return;
            }

            _scheduledAchievements[achievementId] = unlockTime;
            DebugLogger.LogDebug($"Scheduled achievement {achievementId} to unlock at {unlockTime}");
        }

        public void CancelSchedule(string achievementId)
        {
            if (_scheduledAchievements.Remove(achievementId))
            {
                DebugLogger.LogDebug($"Cancelled scheduled unlock for achievement {achievementId}");
            }
        }

        public DateTime? GetScheduledTime(string achievementId)
        {
            return _scheduledAchievements.TryGetValue(achievementId, out var time) ? time : null;
        }

        public Dictionary<string, DateTime> GetAllScheduledAchievements()
        {
            return new Dictionary<string, DateTime>(_scheduledAchievements);
        }

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

                    DebugLogger.LogDebug($"Unlocking scheduled achievement {achievementId} (scheduled for {scheduledTime}, now {now})");

                    // Set the achievement as achieved
                    if (_gameStatsService.SetAchievement(achievementId, true))
                    {
                        DebugLogger.LogDebug($"Successfully set achievement {achievementId} to unlocked");
                        _pendingUnlocks.Add(achievementId);
                        hasNewUnlocks = true;
                    }
                    else
                    {
                        DebugLogger.LogDebug($"Failed to unlock achievement {achievementId}");
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
                        DebugLogger.LogDebug("No more scheduled achievements, storing changes now");
                    }
                    else if ((nextScheduledTime.Value - now).TotalSeconds > 12)
                    {
                        // Next achievement is more than 12 seconds away, store now
                        shouldStore = true;
                        DebugLogger.LogDebug($"Next achievement is {(nextScheduledTime.Value - now).TotalSeconds:F1} seconds away, storing changes now");
                    }
                    else
                    {
                        DebugLogger.LogDebug($"Next achievement is in {(nextScheduledTime.Value - now).TotalSeconds:F1} seconds, delaying store");
                    }

                    if (shouldStore)
                    {
                        _gameStatsService.StoreStats();
                        _lastStoreTime = now;
                        var unlockCount = _pendingUnlocks.Count;
                        DebugLogger.LogDebug($"Stored {unlockCount} achievement unlocks to Steam");
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
                DebugLogger.LogDebug($"Error in CheckScheduledAchievements: {ex.Message}");
            }
        }

        private DateTime? GetNextScheduledTime()
        {
            if (_scheduledAchievements.Count == 0)
                return null;
            
            return _scheduledAchievements.Values.Min();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _timer?.Dispose();
                _disposed = true;
                DebugLogger.LogDebug("AchievementTimerService disposed");
            }
        }
    }
}