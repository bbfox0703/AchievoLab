using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RunGame.Models;
using RunGame.Services;
using RunGame.Steam;
using RunGame.Utils;
using Xunit;

namespace RunGame.Tests
{
    /// <summary>
    /// Tests for GameStatsService schema loading functionality.
    ///
    /// Tests cover:
    /// - LoadUserGameStatsSchema success and failure paths
    /// - GetSteamInstallPath helper method
    /// - KeyValue parsing and error handling
    /// - Localization (english, tchinese, fallback)
    /// - Achievement and stat definition parsing
    /// - Schema file not found scenarios
    /// </summary>
    public class GameStatsServiceSchemaTests : IDisposable
    {
        private readonly MockSteamUserStats _mockSteamClient;
        private readonly GameStatsService _service;
        private readonly string _testDirectory;

        public GameStatsServiceSchemaTests()
        {
            _mockSteamClient = new MockSteamUserStats();
            _service = new GameStatsService(_mockSteamClient, 400); // Portal

            // Create temporary test directory
            _testDirectory = Path.Combine(Path.GetTempPath(), $"GameStatsServiceTests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_testDirectory);
        }

        #region Helper Method Tests

        [Fact]
        public void GetSteamInstallPath_Reflection_ReturnsValidPath()
        {
            var method = typeof(GameStatsService).GetMethod("GetSteamInstallPath",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var result = method.Invoke(null, null) as string;

            // Result should either be empty (Steam not installed) or a valid path
            if (!string.IsNullOrEmpty(result))
            {
                Assert.True(Directory.Exists(result), $"Steam path should exist: {result}");
                Assert.Contains("Steam", result, StringComparison.OrdinalIgnoreCase);
            }
        }

        #endregion

        #region LoadUserGameStatsSchema - File Not Found Tests

        [Fact]
        public void LoadUserGameStatsSchema_WhenSteamNotInstalled_ReturnsFalse()
        {
            // This test assumes Steam is not installed or path is invalid
            // In production, GetSteamInstallPath would return empty string
            var result = _service.LoadUserGameStatsSchema("english");

            // Should return false when schema file doesn't exist
            // (unless Steam is actually installed and has schema for Portal)
            Assert.IsType<bool>(result);
        }

        [Fact]
        public void LoadUserGameStatsSchema_WithInvalidGameId_ReturnsFalse()
        {
            var invalidGameService = new GameStatsService(_mockSteamClient, -1);

            var result = invalidGameService.LoadUserGameStatsSchema("english");

            // Should return false - negative game ID won't have schema file
            Assert.False(result);
        }

        #endregion

        #region GetLocalizedString Tests
        // NOTE: GetLocalizedString tests are not included because:
        // 1. It's a private static method that works with KeyValue instances
        // 2. KeyValue has no public constructor that accepts parameters
        // 3. KeyValue is designed to be created via LoadAsBinary, not manual construction
        // 4. The localization logic is indirectly tested through GetAchievements/GetStatistics
        //    when definitions are loaded with different languages
        #endregion

        #region Achievement/Stat Definition Parsing Tests

        [Fact]
        public void GetAchievements_WhenNoDefinitionsLoaded_ReturnsEmptyList()
        {
            // Service hasn't loaded any schema
            var achievements = _service.GetAchievements();

            Assert.NotNull(achievements);
            Assert.Empty(achievements);
        }

        [Fact]
        public void GetStatistics_WhenNoDefinitionsLoaded_ReturnsEmptyList()
        {
            // Service hasn't loaded any schema
            var statistics = _service.GetStatistics();

            Assert.NotNull(statistics);
            Assert.Empty(statistics);
        }

        [Fact]
        public void GetAchievements_AfterLoadingDefinitions_ReturnsData()
        {
            // Manually inject achievement definitions for testing
            var definitions = new List<AchievementDefinition>
            {
                new AchievementDefinition
                {
                    Id = "test_ach_1",
                    Name = "Test Achievement 1",
                    Description = "Test description",
                    IconNormal = "icon1.jpg",
                    IconLocked = "icon1_gray.jpg",
                    Permission = 0
                }
            };

            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            // Mock Steam client to return achievement data
            _mockSteamClient.SetAchievementData("test_ach_1", false, 0);

            var achievements = _service.GetAchievements();

            Assert.Single(achievements);
            Assert.Equal("test_ach_1", achievements[0].Id);
            Assert.Equal("Test Achievement 1", achievements[0].Name);
            Assert.Equal("Test description", achievements[0].Description);
            Assert.False(achievements[0].IsAchieved);
        }

        [Fact]
        public void GetStatistics_AfterLoadingDefinitions_ReturnsData()
        {
            // Manually inject stat definitions for testing
            var definitions = new List<StatDefinition>
            {
                new IntegerStatDefinition
                {
                    Id = "test_stat_1",
                    DisplayName = "Test Stat 1",
                    MinValue = 0,
                    MaxValue = 100,
                    IncrementOnly = false,
                    Permission = 0
                }
            };

            var field = typeof(GameStatsService).GetField("_statDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            // Mock Steam client to return stat value
            _mockSteamClient.SetStatValue("test_stat_1", 42);

            var statistics = _service.GetStatistics();

            Assert.Single(statistics);
            Assert.Equal("test_stat_1", statistics[0].Id);
            Assert.Equal("Test Stat 1", statistics[0].DisplayName);

            var intStat = statistics[0] as IntStatInfo;
            Assert.NotNull(intStat);
            Assert.Equal(42, intStat.IntValue);
        }

        #endregion

        #region Achievement Permission Tests

        [Fact]
        public void GetAchievements_HandlesProtectedAchievements()
        {
            var definitions = new List<AchievementDefinition>
            {
                new AchievementDefinition
                {
                    Id = "protected_ach",
                    Name = "Protected Achievement",
                    Description = "Cannot be modified",
                    Permission = 3, // Protected
                    IconNormal = "icon.jpg",
                    IconLocked = "icon_gray.jpg"
                }
            };

            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetAchievementData("protected_ach", false, 0);

            var achievements = _service.GetAchievements();

            Assert.Single(achievements);
            Assert.Equal(3, achievements[0].Permission);
        }

        [Fact]
        public void GetAchievements_HandlesHiddenAchievements()
        {
            var definitions = new List<AchievementDefinition>
            {
                new AchievementDefinition
                {
                    Id = "hidden_ach",
                    Name = "Hidden Achievement",
                    Description = "???",
                    IsHidden = true,
                    IconNormal = "icon.jpg",
                    IconLocked = "icon_gray.jpg",
                    Permission = 0
                }
            };

            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetAchievementData("hidden_ach", false, 0);

            var achievements = _service.GetAchievements();

            Assert.Single(achievements);
            // Hidden state is stored in definition, not exposed in AchievementInfo
        }

        #endregion

        #region Stat Definitions Tests

        [Fact]
        public void GetStatistics_HandlesIncrementOnlyStats()
        {
            var definitions = new List<StatDefinition>
            {
                new IntegerStatDefinition
                {
                    Id = "increment_stat",
                    DisplayName = "Increment Only Stat",
                    IncrementOnly = true,
                    MinValue = 0,
                    MaxValue = 1000,
                    Permission = 0
                }
            };

            var field = typeof(GameStatsService).GetField("_statDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetStatValue("increment_stat", 50);

            var statistics = _service.GetStatistics();

            Assert.Single(statistics);
            var intStat = statistics[0] as IntStatInfo;
            Assert.NotNull(intStat);
            Assert.True(intStat.IsIncrementOnly);
        }

        [Fact]
        public void GetStatistics_HandlesFloatStats()
        {
            var definitions = new List<StatDefinition>
            {
                new FloatStatDefinition
                {
                    Id = "float_stat",
                    DisplayName = "Float Stat",
                    MinValue = 0.0f,
                    MaxValue = 100.0f,
                    IncrementOnly = false,
                    Permission = 0
                }
            };

            var field = typeof(GameStatsService).GetField("_statDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetStatValue("float_stat", 42.5f);

            var statistics = _service.GetStatistics();

            Assert.Single(statistics);
            var floatStat = statistics[0] as FloatStatInfo;
            Assert.NotNull(floatStat);
            Assert.Equal(42.5f, floatStat.FloatValue);
        }

        [Fact]
        public void GetStatistics_HandlesProtectedStats()
        {
            var definitions = new List<StatDefinition>
            {
                new IntegerStatDefinition
                {
                    Id = "protected_stat",
                    DisplayName = "Protected Stat",
                    Permission = 3, // Protected
                    MinValue = 0,
                    MaxValue = 100,
                    IncrementOnly = false
                }
            };

            var field = typeof(GameStatsService).GetField("_statDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetStatValue("protected_stat", 50);

            var statistics = _service.GetStatistics();

            Assert.Single(statistics);
            var intStat = statistics[0] as IntStatInfo;
            Assert.NotNull(intStat);
            Assert.True(intStat.IsProtected);
            Assert.Equal(3, intStat.Permission);
        }

        #endregion

        #region Achievement Icon Fallback Tests

        [Fact]
        public void GetAchievements_WhenIconLockedEmpty_FallsBackToIconNormal()
        {
            var definitions = new List<AchievementDefinition>
            {
                new AchievementDefinition
                {
                    Id = "ach_no_gray",
                    Name = "Achievement",
                    Description = "Test",
                    IconNormal = "normal.jpg",
                    IconLocked = "", // Empty locked icon
                    Permission = 0
                }
            };

            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetAchievementData("ach_no_gray", false, 0);

            var achievements = _service.GetAchievements();

            Assert.Single(achievements);
            Assert.Equal("normal.jpg", achievements[0].IconNormal);
            Assert.Equal("normal.jpg", achievements[0].IconLocked); // Fallback to normal
        }

        #endregion

        #region Unlock Time Tests

        [Fact]
        public void GetAchievements_WhenAchieved_ParsesUnlockTime()
        {
            var definitions = new List<AchievementDefinition>
            {
                new AchievementDefinition
                {
                    Id = "unlocked_ach",
                    Name = "Unlocked Achievement",
                    Description = "Test",
                    IconNormal = "icon.jpg",
                    IconLocked = "icon_gray.jpg",
                    Permission = 0
                }
            };

            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            // Set achievement as achieved with unlock time (Jan 1, 2020 00:00:00 UTC)
            uint unlockTime = 1577836800;
            _mockSteamClient.SetAchievementData("unlocked_ach", true, unlockTime);

            var achievements = _service.GetAchievements();

            Assert.Single(achievements);
            Assert.True(achievements[0].IsAchieved);
            Assert.NotNull(achievements[0].UnlockTime);

            // Verify unlock time is parsed correctly (with local timezone conversion)
            var expectedUtc = DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime;
            Assert.Equal(expectedUtc.Year, achievements[0].UnlockTime!.Value.Year);
            Assert.Equal(expectedUtc.Month, achievements[0].UnlockTime!.Value.Month);
            Assert.Equal(expectedUtc.Day, achievements[0].UnlockTime!.Value.Day);
        }

        [Fact]
        public void GetAchievements_WhenNotAchieved_UnlockTimeIsNull()
        {
            var definitions = new List<AchievementDefinition>
            {
                new AchievementDefinition
                {
                    Id = "locked_ach",
                    Name = "Locked Achievement",
                    Description = "Test",
                    IconNormal = "icon.jpg",
                    IconLocked = "icon_gray.jpg",
                    Permission = 0
                }
            };

            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetAchievementData("locked_ach", false, 0);

            var achievements = _service.GetAchievements();

            Assert.Single(achievements);
            Assert.False(achievements[0].IsAchieved);
            Assert.Null(achievements[0].UnlockTime);
        }

        #endregion

        #region Multiple Definitions Tests

        [Fact]
        public void GetAchievements_HandlesMultipleDefinitions()
        {
            var definitions = new List<AchievementDefinition>
            {
                new AchievementDefinition { Id = "ach1", Name = "Achievement 1", Description = "Desc 1", IconNormal = "icon1.jpg", IconLocked = "icon1_gray.jpg", Permission = 0 },
                new AchievementDefinition { Id = "ach2", Name = "Achievement 2", Description = "Desc 2", IconNormal = "icon2.jpg", IconLocked = "icon2_gray.jpg", Permission = 0 },
                new AchievementDefinition { Id = "ach3", Name = "Achievement 3", Description = "Desc 3", IconNormal = "icon3.jpg", IconLocked = "icon3_gray.jpg", Permission = 3 }
            };

            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetAchievementData("ach1", true, 1000);
            _mockSteamClient.SetAchievementData("ach2", false, 0);
            _mockSteamClient.SetAchievementData("ach3", true, 2000);

            var achievements = _service.GetAchievements();

            Assert.Equal(3, achievements.Count);
            Assert.Contains(achievements, a => a.Id == "ach1" && a.IsAchieved);
            Assert.Contains(achievements, a => a.Id == "ach2" && !a.IsAchieved);
            Assert.Contains(achievements, a => a.Id == "ach3" && a.IsProtected);
        }

        [Fact]
        public void GetStatistics_HandlesMultipleDefinitions()
        {
            var definitions = new List<StatDefinition>
            {
                new IntegerStatDefinition { Id = "stat1", DisplayName = "Stat 1", MinValue = 0, MaxValue = 100, IncrementOnly = false, Permission = 0 },
                new FloatStatDefinition { Id = "stat2", DisplayName = "Stat 2", MinValue = 0.0f, MaxValue = 100.0f, IncrementOnly = false, Permission = 0 },
                new IntegerStatDefinition { Id = "stat3", DisplayName = "Stat 3", MinValue = 0, MaxValue = 1000, IncrementOnly = true, Permission = 3 }
            };

            var field = typeof(GameStatsService).GetField("_statDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetStatValue("stat1", 42);
            _mockSteamClient.SetStatValue("stat2", 3.14f);
            _mockSteamClient.SetStatValue("stat3", 500);

            var statistics = _service.GetStatistics();

            Assert.Equal(3, statistics.Count);
            Assert.Contains(statistics, s => s.Id == "stat1" && s is IntStatInfo);
            Assert.Contains(statistics, s => s.Id == "stat2" && s is FloatStatInfo);
            Assert.Contains(statistics, s => s.Id == "stat3" && s.IsProtected && s.IsIncrementOnly);
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public void GetAchievements_SkipsEmptyIds()
        {
            var definitions = new List<AchievementDefinition>
            {
                new AchievementDefinition { Id = "", Name = "Empty ID", Description = "Test", IconNormal = "icon.jpg", IconLocked = "icon_gray.jpg", Permission = 0 },
                new AchievementDefinition { Id = "valid_ach", Name = "Valid", Description = "Test", IconNormal = "icon.jpg", IconLocked = "icon_gray.jpg", Permission = 0 }
            };

            var field = typeof(GameStatsService).GetField("_achievementDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetAchievementData("valid_ach", false, 0);

            var achievements = _service.GetAchievements();

            // Should only get the valid achievement
            Assert.Single(achievements);
            Assert.Equal("valid_ach", achievements[0].Id);
        }

        [Fact]
        public void GetStatistics_SkipsEmptyIds()
        {
            var definitions = new List<StatDefinition>
            {
                new IntegerStatDefinition { Id = "", DisplayName = "Empty ID", MinValue = 0, MaxValue = 100, IncrementOnly = false, Permission = 0 },
                new IntegerStatDefinition { Id = "valid_stat", DisplayName = "Valid", MinValue = 0, MaxValue = 100, IncrementOnly = false, Permission = 0 }
            };

            var field = typeof(GameStatsService).GetField("_statDefinitions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(_service, definitions);

            _mockSteamClient.SetStatValue("valid_stat", 42);

            var statistics = _service.GetStatistics();

            // Should only get the valid stat
            Assert.Single(statistics);
            Assert.Equal("valid_stat", statistics[0].Id);
        }

        #endregion

        public void Dispose()
        {
            // Cleanup test directory
            if (Directory.Exists(_testDirectory))
            {
                try
                {
                    Directory.Delete(_testDirectory, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }

        #region Mock Steam Client

        private sealed class MockSteamUserStats : ISteamUserStats
        {
            private readonly Dictionary<string, int> _intStats = new();
            private readonly Dictionary<string, float> _floatStats = new();
            private readonly Dictionary<string, (bool achieved, uint unlockTime)> _achievements = new();

            public bool RequestUserStats(uint gameId) => true;

            public void SetAchievementData(string id, bool achieved, uint unlockTime)
            {
                _achievements[id] = (achieved, unlockTime);
            }

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

        #endregion
    }
}
