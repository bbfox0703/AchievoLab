using System;
using System.Collections.Generic;
using RunGame.Models;
using RunGame.Services;
using RunGame.Steam;
using Xunit;

namespace RunGame.Tests
{
    public class GameStatsServiceTests
    {
        private sealed class MockSteamUserStats : ISteamUserStats
        {
            private readonly Dictionary<string, int> _intStats = new();
            private readonly Dictionary<string, float> _floatStats = new();
            private readonly Dictionary<string, (bool achieved, uint unlockTime)> _achievements = new();

            public bool RequestUserStats(uint gameId) => true;

            public bool GetAchievementAndUnlockTime(string id, out bool achieved, out uint unlockTime)
            {
                if (_achievements.TryGetValue(id, out var value))
                {
                    achieved = value.achieved;
                    unlockTime = value.unlockTime;
                    return true;
                }
                achieved = false;
                unlockTime = 0;
                return false;
            }

            public bool SetAchievement(string id, bool achieved)
            {
                _achievements[id] = (achieved, 0);
                return true;
            }

            public bool GetStatValue(string name, out int value)
            {
                return _intStats.TryGetValue(name, out value);
            }

            public bool GetStatValue(string name, out float value)
            {
                return _floatStats.TryGetValue(name, out value);
            }

            public bool SetStatValue(string name, int value)
            {
                _intStats[name] = value;
                return true;
            }

            public bool SetStatValue(string name, float value)
            {
                _floatStats[name] = value;
                return true;
            }

            public bool StoreStats() => true;
            public bool ResetAllStats(bool achievementsToo) => true;
            public void RunCallbacks() { }
            public bool IsSubscribedApp(uint gameId) => true;
            public string? GetAppData(uint appId, string key) => null;
            public void RegisterUserStatsCallback(Action<SteamGameClient.UserStatsReceived> callback) { }
            public string GetCurrentGameLanguage() => "english";
        }

        [Fact]
        public void StatInfo_IsProtected_ReturnsTrueWhenPermissionBitsSet()
        {
            var stat = new IntStatInfo
            {
                Permission = 3 // Both bits set
            };

            Assert.True(stat.IsProtected);
            Assert.False(stat.IsNotProtected);
        }

        [Fact]
        public void StatInfo_IsProtected_ReturnsFalseWhenPermissionBitsNotSet()
        {
            var stat = new IntStatInfo
            {
                Permission = 0 // No bits set
            };

            Assert.False(stat.IsProtected);
            Assert.True(stat.IsNotProtected);
        }

        [Fact]
        public void EnableAchievementCascading_DefaultsToFalse()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            Assert.False(service.EnableAchievementCascading);
        }

        [Fact]
        public void SetStatistic_RejectsDecreasingIncrementOnlyStat()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new IntStatInfo
            {
                Id = "test_stat",
                IntValue = 50,
                OriginalValue = 100,
                IsIncrementOnly = true
            };

            bool result = service.SetStatistic(stat);

            Assert.False(result);
        }

        [Fact]
        public void SetStatistic_AllowsIncreasingIncrementOnlyStat()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new IntStatInfo
            {
                Id = "test_stat",
                IntValue = 150,
                OriginalValue = 100,
                IsIncrementOnly = true
            };

            bool result = service.SetStatistic(stat);

            Assert.True(result);
        }

        [Fact]
        public void SetStatistic_RejectsValueBelowMinimum()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new IntStatInfo
            {
                Id = "test_stat",
                IntValue = 5,
                OriginalValue = 50,
                MinValue = 10,
                MaxValue = 100
            };

            bool result = service.SetStatistic(stat);

            Assert.False(result);
        }

        [Fact]
        public void SetStatistic_RejectsValueAboveMaximum()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new IntStatInfo
            {
                Id = "test_stat",
                IntValue = 150,
                OriginalValue = 50,
                MinValue = 0,
                MaxValue = 100
            };

            bool result = service.SetStatistic(stat);

            Assert.False(result);
        }

        [Fact]
        public void SetStatistic_RejectsChangeExceedingMaxChange()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new IntStatInfo
            {
                Id = "test_stat",
                IntValue = 160,
                OriginalValue = 100,
                MaxChange = 50
            };

            bool result = service.SetStatistic(stat);

            Assert.False(result);
        }

        [Fact]
        public void SetStatistic_AllowsChangeWithinMaxChange()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new IntStatInfo
            {
                Id = "test_stat",
                IntValue = 140,
                OriginalValue = 100,
                MaxChange = 50
            };

            bool result = service.SetStatistic(stat);

            Assert.True(result);
        }

        [Fact]
        public void SetStatistic_FloatStat_RejectsDecreasingIncrementOnly()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new FloatStatInfo
            {
                Id = "test_float_stat",
                FloatValue = 50.5f,
                OriginalValue = 100.0f,
                IsIncrementOnly = true
            };

            bool result = service.SetStatistic(stat);

            Assert.False(result);
        }

        [Fact]
        public void SetStatistic_FloatStat_RejectsValueBelowMinimum()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new FloatStatInfo
            {
                Id = "test_float_stat",
                FloatValue = 5.0f,
                OriginalValue = 50.0f,
                MinValue = 10.0f,
                MaxValue = 100.0f
            };

            bool result = service.SetStatistic(stat);

            Assert.False(result);
        }

        [Fact]
        public void SetStatistic_FloatStat_RejectsChangeExceedingMaxChange()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new FloatStatInfo
            {
                Id = "test_float_stat",
                FloatValue = 160.0f,
                OriginalValue = 100.0f,
                MaxChange = 50.0f
            };

            bool result = service.SetStatistic(stat);

            Assert.False(result);
        }

        [Fact]
        public void SetStatistic_RejectsProtectedStat()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new IntStatInfo
            {
                Id = "test_protected_stat",
                IntValue = 100,
                OriginalValue = 50,
                Permission = 3 // Protected
            };

            bool result = service.SetStatistic(stat);

            Assert.False(result);
        }

        [Fact]
        public void SetStatistic_AllowsNonProtectedStat()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            var stat = new IntStatInfo
            {
                Id = "test_stat",
                IntValue = 100,
                OriginalValue = 50,
                Permission = 0 // Not protected
            };

            bool result = service.SetStatistic(stat);

            Assert.True(result);
        }

        [Fact]
        public void SetAchievement_RejectsProtectedAchievement()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            // Add achievement definition first
            var achievements = new List<AchievementDefinition>
            {
                new AchievementDefinition
                {
                    Id = "protected_achievement",
                    Permission = 3 // Protected
                }
            };

            // Use reflection to set private field for testing
            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(service, achievements);

            bool result = service.SetAchievement("protected_achievement", true);

            Assert.False(result);
        }

        [Fact]
        public void SetAchievement_AllowsNonProtectedAchievement()
        {
            var steamClient = new MockSteamUserStats();
            var service = new GameStatsService(steamClient, 400);

            // Add achievement definition first
            var achievements = new List<AchievementDefinition>
            {
                new AchievementDefinition
                {
                    Id = "normal_achievement",
                    Permission = 0 // Not protected
                }
            };

            // Use reflection to set private field for testing
            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(service, achievements);

            bool result = service.SetAchievement("normal_achievement", true);

            Assert.True(result);
        }

        [Fact]
        public void AchievementInfo_IsLockVisible_ShowsForProtectedUnachieved()
        {
            var achievement = new AchievementInfo
            {
                Permission = 3, // Protected
                IsAchieved = false
            };

            Assert.True(achievement.IsLockVisible);
        }

        [Fact]
        public void AchievementInfo_IsLockVisible_HidesForProtectedAchieved()
        {
            var achievement = new AchievementInfo
            {
                Permission = 3, // Protected
                IsAchieved = true
            };

            Assert.False(achievement.IsLockVisible);
        }

        [Fact]
        public void AchievementInfo_IsLockVisible_HidesForNonProtected()
        {
            var achievement = new AchievementInfo
            {
                Permission = 0, // Not protected
                IsAchieved = false
            };

            Assert.False(achievement.IsLockVisible);
        }
    }
}
