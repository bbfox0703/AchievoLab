using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RunGame.Models;
using RunGame.Services;

namespace RunGame.Services
{
    public class AchievementTimerService : IDisposable
    {
        private readonly GameStatsService _gameStatsService;
        private readonly Dictionary<string, DateTime> _scheduledAchievements = new();
        private readonly System.Threading.Timer _timer;
        private bool _disposed = false;

        public AchievementTimerService(GameStatsService gameStatsService)
        {
            _gameStatsService = gameStatsService;
            
            // Check every 10 seconds for achievements that should be unlocked
            _timer = new System.Threading.Timer(CheckScheduledAchievements, null, 
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            
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

        private void CheckScheduledAchievements(object? state)
        {
            try
            {
                var now = DateTime.Now;
                var achievementsToUnlock = _scheduledAchievements
                    .Where(kvp => kvp.Value <= now)
                    .ToList();

                foreach (var achievement in achievementsToUnlock)
                {
                    var achievementId = achievement.Key;
                    var scheduledTime = achievement.Value;

                    DebugLogger.LogDebug($"Unlocking scheduled achievement {achievementId} (scheduled for {scheduledTime}, now {now})");

                    // Set the achievement as achieved
                    if (_gameStatsService.SetAchievement(achievementId, true))
                    {
                        DebugLogger.LogDebug($"Successfully unlocked achievement {achievementId}");
                        
                        // Store the changes to Steam
                        _gameStatsService.StoreStats();
                    }
                    else
                    {
                        DebugLogger.LogDebug($"Failed to unlock achievement {achievementId}");
                    }

                    // Remove from scheduled list
                    _scheduledAchievements.Remove(achievementId);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in CheckScheduledAchievements: {ex.Message}");
            }
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