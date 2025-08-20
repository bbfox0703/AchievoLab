using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RunGame.Models;
using RunGame.Steam;
using RunGame.Utils;

namespace RunGame.Services
{
    public class GameStatsService
    {
        private readonly SteamGameClient _steamClient;
        private readonly long _gameId;
        private readonly List<AchievementDefinition> _achievementDefinitions = new();
        private readonly List<StatDefinition> _statDefinitions = new();

        public event EventHandler<UserStatsReceivedEventArgs>? UserStatsReceived;

        public GameStatsService(SteamGameClient steamClient, long gameId)
        {
            _steamClient = steamClient;
            _gameId = gameId;
            _steamClient.RegisterUserStatsCallback(OnUserStatsReceived);
        }

        public bool LoadUserGameStatsSchema(string currentLanguage = "english")
        {
            try
            {
                string fileName = $"UserGameStatsSchema_{_gameId}.bin";
                string installPath = GetSteamInstallPath();
                DebugLogger.LogDebug($"Steam install path: {installPath}");
                
                string appcachePath = Path.Combine(installPath, "appcache", "stats");
                DebugLogger.LogDebug($"Appcache stats path: {appcachePath}");
                
                string path = Path.Combine(appcachePath, fileName);
                DebugLogger.LogDebug($"Looking for schema file: {path}");
                
                if (!Directory.Exists(appcachePath))
                {
                    DebugLogger.LogDebug($"Appcache stats directory does not exist: {appcachePath}");
                    return false;
                }
                
                if (!File.Exists(path))
                {
                    DebugLogger.LogDebug($"Schema file does not exist: {path}");
                    
                    // List all files in the appcache stats directory for debugging
                    try
                    {
                        var files = Directory.GetFiles(appcachePath, "*.bin");
                        DebugLogger.LogDebug($"Available .bin files in {appcachePath}:");
                        foreach (var file in files)
                        {
                            DebugLogger.LogDebug($"  - {Path.GetFileName(file)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogDebug($"Error listing files in {appcachePath}: {ex.Message}");
                    }
                    
                    return false;
                }

                DebugLogger.LogDebug($"Found schema file, attempting to load: {path}");
                
                KeyValue? kv = null;
                try
                {
                    // Try multiple times with different approaches
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            kv = KeyValue.LoadAsBinary(path);
                            if (kv != null)
                            {
                                DebugLogger.LogDebug($"Successfully loaded KeyValue on attempt {attempt + 1}");
                                break;
                            }
                            DebugLogger.LogDebug($"KeyValue.LoadAsBinary returned null on attempt {attempt + 1}");
                        }
                        catch (IOException ioEx)
                        {
                            DebugLogger.LogDebug($"IOException on attempt {attempt + 1}: {ioEx.Message}");
                            if (attempt < 2) // Wait and retry
                            {
                                System.Threading.Thread.Sleep(100 * (attempt + 1)); // 100ms, 200ms
                                continue;
                            }
                            throw;
                        }
                        catch (UnauthorizedAccessException authEx)
                        {
                            DebugLogger.LogDebug($"UnauthorizedAccessException: {authEx.Message}");
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Exception loading KeyValue: {ex.GetType().Name} - {ex.Message}");
                    return false;
                }
                
                if (kv == null)
                {
                    DebugLogger.LogDebug("Failed to parse KeyValue from schema file after all attempts");
                    return false;
                }

                DebugLogger.LogDebug("Successfully loaded KeyValue from schema file");
                
                // Debug: Print the structure of the loaded KeyValue
                DebugLogger.LogDebug($"Root KeyValue name: '{kv.Name}', Valid: {kv.Valid}, HasChildren: {kv.Children != null}");
                if (kv.Children != null)
                {
                    DebugLogger.LogDebug($"Root has {kv.Children.Count} children:");
                    foreach (var child in kv.Children.Take(10)) // Limit to first 10 children
                    {
                        DebugLogger.LogDebug($"  - Child: '{child.Name}', Valid: {child.Valid}, HasChildren: {child.Children != null}");
                    }
                }
                else
                {
                    DebugLogger.LogDebug("Root KeyValue has no children");
                    // Try to get raw data info
                    DebugLogger.LogDebug($"KeyValue AsString: '{kv.AsString("")}'");
                    DebugLogger.LogDebug($"KeyValue AsInteger: {kv.AsInteger(-1)}");
                    
                    // Check if this is a different KeyValue structure
                    // Sometimes the root itself might be the game ID section
                    if (kv.Valid)
                    {
                        var directStats = kv["stats"];
                        DebugLogger.LogDebug($"Direct stats check - Valid: {directStats.Valid}, HasChildren: {directStats.Children != null}");
                        if (directStats.Valid && directStats.Children != null)
                        {
                            DebugLogger.LogDebug($"Direct stats has {directStats.Children.Count} children");
                        }
                    }
                }
                
                _achievementDefinitions.Clear();
                _statDefinitions.Clear();

                var gameIdStr = _gameId.ToString(CultureInfo.InvariantCulture);
                DebugLogger.LogDebug($"Looking for game ID section: {gameIdStr}");
                
                var gameSection = kv[gameIdStr];
                if (!gameSection.Valid)
                {
                    DebugLogger.LogDebug($"Game section not found for ID: {gameIdStr}");
                    
                    // Try different approaches to find the game data
                    // 1. Try the root directly
                    if (kv.Valid && kv.Children != null)
                    {
                        DebugLogger.LogDebug("Trying root level stats...");
                        var rootStats = kv["stats"];
                        if (rootStats.Valid)
                        {
                            DebugLogger.LogDebug("Found stats at root level");
                            gameSection = kv;
                        }
                    }
                    
                    // 2. Try looking in first child
                    if (!gameSection.Valid && kv.Children != null && kv.Children.Count > 0)
                    {
                        DebugLogger.LogDebug("Trying first child...");
                        var firstChild = kv.Children[0];
                        DebugLogger.LogDebug($"First child name: '{firstChild.Name}'");
                        var firstChildStats = firstChild["stats"];
                        if (firstChildStats.Valid)
                        {
                            DebugLogger.LogDebug("Found stats in first child");
                            gameSection = firstChild;
                        }
                    }
                    
                    if (!gameSection.Valid)
                    {
                        DebugLogger.LogDebug("No valid game section found. This game may not have achievements/stats defined.");
                        // For games without achievements, we can still show the interface
                        // but with no data - this matches Legacy SAM behavior
                        return true; // Return true but with empty definitions
                    }
                }
                
                var stats = gameSection["stats"];
                if (!stats.Valid || stats.Children == null)
                {
                    DebugLogger.LogDebug("Stats section not found or invalid");
                    return false;
                }

                DebugLogger.LogDebug($"Found stats section with {stats.Children.Count} children");
                
                foreach (var stat in stats.Children)
                {
                    if (!stat.Valid) continue;

                    var rawType = stat["type_int"].Valid ? stat["type_int"].AsInteger(0) : stat["type"].AsInteger(0);
                    var type = (UserStatType)rawType;
                    
                    DebugLogger.LogDebug($"Processing stat: {stat.Name}, type: {type}");
                    
                    switch (type)
                    {
                        case UserStatType.Integer:
                            ParseIntegerStat(stat, currentLanguage);
                            break;
                        case UserStatType.Float:
                        case UserStatType.AverageRate:
                            ParseFloatStat(stat, currentLanguage);
                            break;
                        case UserStatType.Achievements:
                        case UserStatType.GroupAchievements:
                            ParseAchievements(stat, currentLanguage);
                            break;
                    }
                }

                DebugLogger.LogDebug($"Schema loading completed. Found {_achievementDefinitions.Count} achievements and {_statDefinitions.Count} stats");
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Exception in LoadUserGameStatsSchema: {ex.Message}");
                return false;
            }
        }

        private void ParseIntegerStat(KeyValue stat, string currentLanguage)
        {
            var id = stat["name"].AsString("");
            string name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);
            bool incrementOnly = stat["incrementonly"].AsBoolean(false);

            DebugLogger.LogDebug($"Integer Stat parsed - ID: {id}, Name: '{name}', IncrementOnly: {incrementOnly}, Language: {currentLanguage}");

            _statDefinitions.Add(new IntegerStatDefinition
            {
                Id = id,
                DisplayName = name,
                MinValue = stat["min"].AsInteger(int.MinValue),
                MaxValue = stat["max"].AsInteger(int.MaxValue),
                MaxChange = stat["maxchange"].AsInteger(0),
                IncrementOnly = incrementOnly,
                SetByTrustedGameServer = stat["bSetByTrustedGS"].AsBoolean(false),
                DefaultValue = stat["default"].AsInteger(0),
                Permission = stat["permission"].AsInteger(0)
            });
        }

        private void ParseFloatStat(KeyValue stat, string currentLanguage)
        {
            var id = stat["name"].AsString("");
            string name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);

            _statDefinitions.Add(new FloatStatDefinition
            {
                Id = id,
                DisplayName = name,
                MinValue = stat["min"].AsFloat(float.MinValue),
                MaxValue = stat["max"].AsFloat(float.MaxValue),
                MaxChange = stat["maxchange"].AsFloat(0.0f),
                IncrementOnly = stat["incrementonly"].AsBoolean(false),
                DefaultValue = stat["default"].AsFloat(0.0f),
                Permission = stat["permission"].AsInteger(0)
            });
        }

        private void ParseAchievements(KeyValue stat, string currentLanguage)
        {
            if (stat.Children == null) return;

            foreach (var bits in stat.Children.Where(b => 
                string.Compare(b.Name, "bits", StringComparison.InvariantCultureIgnoreCase) == 0))
            {
                if (!bits.Valid || bits.Children == null) continue;

                foreach (var bit in bits.Children)
                {
                    string id = bit["name"].AsString("");
                    string name = GetLocalizedString(bit["display"]["name"], currentLanguage, id);
                    string desc = GetLocalizedString(bit["display"]["desc"], currentLanguage, "");

                    DebugLogger.LogDebug($"Achievement parsed - ID: {id}, Name: '{name}', Desc: '{desc}', Language: {currentLanguage}");

                    string iconNormal = bit["display"]["icon"].AsString("");
                    string iconLocked = bit["display"]["icon_gray"].AsString("");
                    
                    DebugLogger.LogDebug($"Achievement {id} icons - Normal: '{iconNormal}', Locked: '{iconLocked}'");
                    
                    _achievementDefinitions.Add(new AchievementDefinition
                    {
                        Id = id,
                        Name = name,
                        Description = desc,
                        IconNormal = iconNormal,
                        IconLocked = iconLocked,
                        IsHidden = bit["display"]["hidden"].AsBoolean(false),
                        Permission = bit["permission"].AsInteger(0)
                    });
                }
            }
        }

        private static string GetLocalizedString(KeyValue kv, string language, string defaultValue)
        {
            var name = kv[language].AsString("");
            if (!string.IsNullOrEmpty(name)) return name;

            if (language != "english")
            {
                name = kv["english"].AsString("");
                if (!string.IsNullOrEmpty(name)) return name;
            }

            name = kv.AsString("");
            if (!string.IsNullOrEmpty(name)) return name;

            return defaultValue;
        }

        public async Task<bool> RequestUserStatsAsync()
        {
            DebugLogger.LogDebug($"GameStatsService.RequestUserStatsAsync called for game {_gameId}");
            bool result = await Task.Run(() => _steamClient.RequestUserStats((uint)_gameId));
            DebugLogger.LogDebug($"GameStatsService.RequestUserStatsAsync result: {result}");
            return result;
        }
        
        public string GetCurrentGameLanguage()
        {
            return _steamClient.GetCurrentGameLanguage() ?? "english";
        }

        public List<AchievementInfo> GetAchievements()
        {
            DebugLogger.LogDebug($"GetAchievements called - {_achievementDefinitions.Count} definitions available");
            var achievements = new List<AchievementInfo>();

            foreach (var def in _achievementDefinitions)
            {
                if (string.IsNullOrEmpty(def.Id)) continue;

                if (_steamClient.GetAchievementAndUnlockTime(def.Id, out bool isAchieved, out var unlockTime))
                {
                    var achievement = new AchievementInfo
                    {
                        Id = def.Id,
                        Name = def.Name,
                        Description = def.Description,
                        IsAchieved = isAchieved,
                        UnlockTime = isAchieved && unlockTime > 0 
                            ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime 
                            : null,
                        IconNormal = def.IconNormal,
                        IconLocked = string.IsNullOrEmpty(def.IconLocked) ? def.IconNormal : def.IconLocked,
                        Permission = def.Permission
                    };
                    
                    DebugLogger.LogDebug($"Achievement created - ID: {achievement.Id}, Name: '{achievement.Name}', IsAchieved: {achievement.IsAchieved}");
                    achievements.Add(achievement);
                }
                else
                {
                    DebugLogger.LogDebug($"Failed to get achievement data for ID: {def.Id}");
                }
            }

            return achievements;
        }

        public List<StatInfo> GetStatistics()
        {
            DebugLogger.LogDebug($"GetStatistics called - {_statDefinitions.Count} definitions available");
            var statistics = new List<StatInfo>();

            foreach (var stat in _statDefinitions)
            {
                if (string.IsNullOrEmpty(stat.Id)) continue;

                if (stat is IntegerStatDefinition intStat)
                {
                    if (_steamClient.GetStatValue(intStat.Id, out int value))
                    {
                        var statInfo = new IntStatInfo
                        {
                            Id = intStat.Id,
                            DisplayName = intStat.DisplayName,
                            IntValue = value,
                            OriginalValue = value,
                            IsIncrementOnly = intStat.IncrementOnly,
                            Permission = intStat.Permission
                        };
                        
                        DebugLogger.LogDebug($"Integer Stat created - ID: {statInfo.Id}, Name: '{statInfo.DisplayName}', Value: {statInfo.IntValue}, IncrementOnly: {statInfo.IsIncrementOnly}");
                        statistics.Add(statInfo);
                    }
                    else
                    {
                        DebugLogger.LogDebug($"Failed to get stat value for ID: {intStat.Id}");
                    }
                }
                else if (stat is FloatStatDefinition floatStat)
                {
                    if (_steamClient.GetStatValue(floatStat.Id, out float value))
                    {
                        statistics.Add(new FloatStatInfo
                        {
                            Id = floatStat.Id,
                            DisplayName = floatStat.DisplayName,
                            FloatValue = value,
                            OriginalValue = value,
                            IsIncrementOnly = floatStat.IncrementOnly,
                            Permission = floatStat.Permission
                        });
                    }
                }
            }

            return statistics;
        }

        public bool SetAchievement(string id, bool achieved)
        {
            if (DebugLogger.IsDebugMode)
            {
                DebugLogger.LogDebug($"[DEBUG FAKE WRITE] SetAchievement: {id} = {achieved} (not actually written to Steam)");
                
                // Even in debug mode, we should adjust related statistics for consistency
                if (achieved)
                {
                    AdjustRelatedStatistics(id);
                }
                
                return true; // Always return success in debug mode
            }
            
            DebugLogger.LogDebug($"GameStatsService.SetAchievement called: {id} = {achieved}");
            
            // If setting achievement to true, check for related statistics and adjust them
            if (achieved)
            {
                AdjustRelatedStatistics(id);
            }
            
            bool success = _steamClient.SetAchievement(id, achieved);
            DebugLogger.LogDebug($"SetAchievement result: {success} for {id} = {achieved}");
            return success;
        }
        
        private void AdjustRelatedStatistics(string achievementId)
        {
            // Define achievement-statistic relationships with required values
            var achievementStatMap = new Dictionary<string, (string statId, int requiredValue)>
            {
                { "DestroyXUnits", ("DestroyXUnits_Stat", 5000) },
                { "037_DestroyXUnitsLow", ("DestroyXUnits_Stat", 2500) },
                { "WinXSkirmishGames", ("WinXSkirmishGames_Stat", 10) },
                { "PillageXLocations", ("PillageXLocations_Stat", 100) },
                { "RebuildXLocations", ("RebuildXLocations_Stat", 100) },
                { "GetXLocationsToMaxProsperity", ("GetXLocationsToMaxProsperity_Stat", 500) },
                { "BuildXBuildings", ("BuildXBuildings_Stat", 500) },
                { "RecruitXUnits", ("RecruitXUnits_Stat", 2000) },
                { "PlayXCombatCards", ("PlayXCombatCards_Stat", 6000) },
                { "FindWanderingEruditeOnAllMaps", ("FindWanderingEruditeOnAllMaps_Stat", 8) }, // Assuming 8 maps
                { "FinishCampaignOnHardDifficulty", ("FinishCampaignOnHardDifficulty_Stat", 1) },
                { "UnlockXLexicanumEntries", ("UnlockXLexicanumEntries_Stat", 999) } // Assuming all entries
            };
            
            if (achievementStatMap.TryGetValue(achievementId, out var statInfo))
            {
                var (statId, requiredValue) = statInfo;
                
                // Get current stat value
                if (_steamClient.GetStatValue(statId, out int currentValue))
                {
                    // Always set to the required value to ensure consistency
                    // This handles cases where multiple achievements use the same stat with different values
                    DebugLogger.LogDebug($"Setting stat {statId} to {requiredValue} for achievement {achievementId} (current: {currentValue})");
                    
                    if (DebugLogger.IsDebugMode)
                    {
                        DebugLogger.LogDebug($"[DEBUG FAKE WRITE] SetStatValue: {statId} = {requiredValue} (not actually written to Steam)");
                    }
                    else
                    {
                        bool success = _steamClient.SetStatValue(statId, requiredValue);
                        DebugLogger.LogDebug($"SetStatValue result: {success} for {statId} = {requiredValue}");
                    }
                }
                else
                {
                    DebugLogger.LogDebug($"Failed to get current value for stat {statId}");
                }
                
                // Auto-trigger other achievements that use the same statistic with lower requirements
                AutoTriggerRelatedAchievements(statId, requiredValue);
            }
        }
        
        private void AutoTriggerRelatedAchievements(string statId, int newValue)
        {
            // Find all achievements that use this statistic with lower or equal requirements
            var relatedAchievements = new Dictionary<string, (string statId, int requiredValue)>
            {
                { "DestroyXUnits", ("DestroyXUnits_Stat", 5000) },
                { "037_DestroyXUnitsLow", ("DestroyXUnits_Stat", 2500) },
                { "WinXSkirmishGames", ("WinXSkirmishGames_Stat", 10) },
                { "PillageXLocations", ("PillageXLocations_Stat", 100) },
                { "RebuildXLocations", ("RebuildXLocations_Stat", 100) },
                { "GetXLocationsToMaxProsperity", ("GetXLocationsToMaxProsperity_Stat", 500) },
                { "BuildXBuildings", ("BuildXBuildings_Stat", 500) },
                { "RecruitXUnits", ("RecruitXUnits_Stat", 2000) },
                { "PlayXCombatCards", ("PlayXCombatCards_Stat", 6000) },
                { "FindWanderingEruditeOnAllMaps", ("FindWanderingEruditeOnAllMaps_Stat", 8) },
                { "FinishCampaignOnHardDifficulty", ("FinishCampaignOnHardDifficulty_Stat", 1) },
                { "UnlockXLexicanumEntries", ("UnlockXLexicanumEntries_Stat", 999) }
            };
            
            foreach (var kvp in relatedAchievements)
            {
                var achievementId = kvp.Key;
                var (relatedStatId, requiredValue) = kvp.Value;
                
                // If this achievement uses the same statistic and has lower/equal requirement
                if (relatedStatId == statId && requiredValue <= newValue)
                {
                    // Check if achievement is not already achieved
                    var achievements = GetAchievements();
                    var targetAchievement = achievements.FirstOrDefault(a => a.Id == achievementId);
                    
                    if (targetAchievement != null && !targetAchievement.IsAchieved)
                    {
                        DebugLogger.LogDebug($"Auto-triggering achievement {achievementId} due to stat {statId} = {newValue} (requires {requiredValue})");
                        
                        if (DebugLogger.IsDebugMode)
                        {
                            DebugLogger.LogDebug($"[DEBUG FAKE WRITE] Auto SetAchievement: {achievementId} = True (not actually written to Steam)");
                        }
                        else
                        {
                            bool success = _steamClient.SetAchievement(achievementId, true);
                            DebugLogger.LogDebug($"Auto SetAchievement result: {success} for {achievementId} = True");
                        }
                    }
                }
            }
        }

        public bool SetStatistic(StatInfo stat)
        {
            if (DebugLogger.IsDebugMode)
            {
                if (stat is IntStatInfo intStat)
                {
                    DebugLogger.LogDebug($"[DEBUG FAKE WRITE] SetStatistic: {intStat.Id} = {intStat.IntValue} (not actually written to Steam)");
                }
                else if (stat is FloatStatInfo floatStat)
                {
                    DebugLogger.LogDebug($"[DEBUG FAKE WRITE] SetStatistic: {floatStat.Id} = {floatStat.FloatValue} (not actually written to Steam)");
                }
                return true; // Always return success in debug mode
            }
            
            if (stat is IntStatInfo intStatRelease)
            {
                DebugLogger.LogDebug($"GameStatsService.SetStatistic called: {intStatRelease.Id} = {intStatRelease.IntValue}");
                bool success = _steamClient.SetStatValue(intStatRelease.Id, intStatRelease.IntValue);
                DebugLogger.LogDebug($"SetStatistic result: {success} for {intStatRelease.Id} = {intStatRelease.IntValue}");
                return success;
            }
            else if (stat is FloatStatInfo floatStatRelease)
            {
                DebugLogger.LogDebug($"GameStatsService.SetStatistic called: {floatStatRelease.Id} = {floatStatRelease.FloatValue}");
                bool success = _steamClient.SetStatValue(floatStatRelease.Id, floatStatRelease.FloatValue);
                DebugLogger.LogDebug($"SetStatistic result: {success} for {floatStatRelease.Id} = {floatStatRelease.FloatValue}");
                return success;
            }
            return false;
        }

        public bool StoreStats()
        {
            if (DebugLogger.IsDebugMode)
            {
                DebugLogger.LogDebug("[DEBUG FAKE WRITE] StoreStats: All changes committed to fake cache (not actually written to Steam)");
                return true; // Always return success in debug mode
            }
            
            DebugLogger.LogDebug("GameStatsService.StoreStats called");
            bool success = _steamClient.StoreStats();
            DebugLogger.LogDebug($"StoreStats result: {success}");
            return success;
        }

        public bool ResetAllStats(bool achievementsToo)
        {
            DebugLogger.LogDebug($"GameStatsService.ResetAllStats called: achievements={achievementsToo}");
            return _steamClient.ResetAllStats(achievementsToo);
        }

        private void OnUserStatsReceived(SteamGameClient.UserStatsReceived userStatsReceived)
        {
            DebugLogger.LogDebug($"GameStatsService.OnUserStatsReceived - GameId: {userStatsReceived.GameId}, Result: {userStatsReceived.Result}, UserId: {userStatsReceived.UserId}");
            DebugLogger.LogDebug($"UserStatsReceived event has {(UserStatsReceived == null ? 0 : UserStatsReceived.GetInvocationList().Length)} subscribers");
            
            UserStatsReceived?.Invoke(this, new UserStatsReceivedEventArgs
            {
                GameId = userStatsReceived.GameId,
                Result = userStatsReceived.Result,
                UserId = userStatsReceived.UserId
            });
        }

        private static string GetSteamInstallPath()
        {
            const string subKey = @"Software\Valve\Steam";
            
            DebugLogger.LogDebug("Searching for Steam install path in registry...");
            
            // Check HKLM 64-bit and 32-bit (WOW6432Node) views
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view).OpenSubKey(subKey);
                    if (key != null)
                    {
                        var path = key.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            DebugLogger.LogDebug($"Found Steam install path in HKLM {view}: {path}");
                            return path;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error reading HKLM {view}: {ex.Message}");
                }
            }

            // Fall back to HKCU
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, view).OpenSubKey(subKey);
                    if (key != null)
                    {
                        var path = key.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            DebugLogger.LogDebug($"Found Steam install path in HKCU {view}: {path}");
                            return path;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error reading HKCU {view}: {ex.Message}");
                }
            }
            
            DebugLogger.LogDebug("Steam install path not found in registry");
            return "";
        }
    }

    public class UserStatsReceivedEventArgs : EventArgs
    {
        public ulong GameId { get; set; }
        public int Result { get; set; }
        public ulong UserId { get; set; }
    }

    public enum UserStatType
    {
        Invalid = 0,
        Integer = 1,
        Float = 2,
        AverageRate = 3,
        Achievements = 4,
        GroupAchievements = 5
    }
}