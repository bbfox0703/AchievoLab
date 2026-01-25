using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RunGame.Models;
using RunGame.Steam;
using RunGame.Utils;
using CommonUtilities;

namespace RunGame.Services
{
    /// <summary>
    /// Manages game achievements and statistics by parsing UserGameStatsSchema VDF files
    /// and interfacing with Steam's UserStats API.
    /// Handles VDF binary parsing, localization, and validation of stat/achievement modifications.
    /// </summary>
    /// <remarks>
    /// Design notes:
    /// - VDF (Valve Data Format) files are binary KeyValue structures stored in %STEAM%/appcache/stats/
    /// - Supports multiple languages with fallback to English
    /// - Protected stats/achievements (permission & 3 != 0) cannot be modified
    /// - Debug builds log operations without writing to Steam (safety feature)
    /// - Release builds write directly to Steam via ISteamUserStats interface
    /// </remarks>
    public class GameStatsService
    {
        private readonly ISteamUserStats _steamClient;
        private readonly long _gameId;
        private readonly List<AchievementDefinition> _achievementDefinitions = new();
        private readonly List<StatDefinition> _statDefinitions = new();

        /// <summary>
        /// Gets or sets a value indicating whether automatic cascading of stat-based achievements is enabled.
        /// </summary>
        /// <remarks>
        /// EXPERIMENTAL: Enable automatic cascading of stat-based achievements.
        /// WARNING: This feature is game-specific and may cause unintended achievement unlocks.
        /// Only enable if you understand the risks.
        /// Currently hardcoded for Warhammer 40,000: Battlesector (AppID 1295480).
        /// </remarks>
        public bool EnableAchievementCascading { get; set; } = false;

        /// <summary>
        /// Occurs when Steam sends a UserStatsReceived callback indicating stats have been loaded.
        /// </summary>
        public event EventHandler<UserStatsReceivedEventArgs>? UserStatsReceived;

        /// <summary>
        /// Initializes a new instance of the <see cref="GameStatsService"/> class.
        /// Registers a callback to receive UserStatsReceived events from Steam.
        /// </summary>
        /// <param name="steamClient">The Steam client interface for accessing UserStats API.</param>
        /// <param name="gameId">The Steam AppID of the game.</param>
        public GameStatsService(ISteamUserStats steamClient, long gameId)
        {
            _steamClient = steamClient;
            _gameId = gameId;
            _steamClient.RegisterUserStatsCallback(OnUserStatsReceived);
        }

        /// <summary>
        /// Loads and parses the UserGameStatsSchema VDF file for the current game.
        /// Extracts achievement and statistic definitions with localized names/descriptions.
        /// </summary>
        /// <param name="currentLanguage">The language code (e.g., "english", "tchinese", "japanese") for localized strings.</param>
        /// <returns>True if the schema was loaded successfully; false if the file doesn't exist or parsing failed.</returns>
        /// <remarks>
        /// VDF file location: %STEAM%/appcache/stats/UserGameStatsSchema_{gameId}.bin
        /// The file is a binary KeyValue structure with the following hierarchy:
        /// - Root → GameID → stats → [stat entries]
        /// - Each stat entry has type_int or type fields indicating Integer, Float, AverageRate, Achievements, or GroupAchievements
        /// - Achievements are stored under "bits" sections within Achievement-type stats
        /// - Localized strings are stored with language codes as keys (e.g., "english", "tchinese")
        /// - Falls back to English if the requested language is not available
        /// </remarks>
        public bool LoadUserGameStatsSchema(string currentLanguage = "english")
        {
            try
            {
                string fileName = $"UserGameStatsSchema_{_gameId}.bin";
                string installPath = GetSteamInstallPath();
                AppLogger.LogDebug($"Steam install path: {installPath}");
                
                string appcachePath = Path.Combine(installPath, "appcache", "stats");
                AppLogger.LogDebug($"Appcache stats path: {appcachePath}");
                
                string path = Path.Combine(appcachePath, fileName);
                AppLogger.LogDebug($"Looking for schema file: {path}");
                
                if (!Directory.Exists(appcachePath))
                {
                    AppLogger.LogDebug($"Appcache stats directory does not exist: {appcachePath}");
                    return false;
                }
                
                if (!File.Exists(path))
                {
                    AppLogger.LogDebug($"Schema file does not exist: {path}");
                    
                    // List all files in the appcache stats directory for debugging
                    try
                    {
                        var files = Directory.GetFiles(appcachePath, "*.bin");
                        AppLogger.LogDebug($"Available .bin files in {appcachePath}:");
                        foreach (var file in files)
                        {
                            AppLogger.LogDebug($"  - {Path.GetFileName(file)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Error listing files in {appcachePath}: {ex.Message}");
                    }
                    
                    return false;
                }

                AppLogger.LogDebug($"Found schema file, attempting to load: {path}");
                
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
                                AppLogger.LogDebug($"Successfully loaded KeyValue on attempt {attempt + 1}");
                                break;
                            }
                            AppLogger.LogDebug($"KeyValue.LoadAsBinary returned null on attempt {attempt + 1}");
                        }
                        catch (IOException ioEx)
                        {
                            AppLogger.LogDebug($"IOException on attempt {attempt + 1}: {ioEx.Message}");
                            if (attempt < 2) // Wait and retry
                            {
                                System.Threading.Thread.Sleep(100 * (attempt + 1)); // 100ms, 200ms
                                continue;
                            }
                            throw;
                        }
                        catch (UnauthorizedAccessException authEx)
                        {
                            AppLogger.LogDebug($"UnauthorizedAccessException: {authEx.Message}");
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Exception loading KeyValue: {ex.GetType().Name} - {ex.Message}");
                    return false;
                }
                
                if (kv == null)
                {
                    AppLogger.LogDebug("Failed to parse KeyValue from schema file after all attempts");
                    return false;
                }

                AppLogger.LogDebug("Successfully loaded KeyValue from schema file");
                
                // Debug: Print the structure of the loaded KeyValue
                AppLogger.LogDebug($"Root KeyValue name: '{kv.Name}', Valid: {kv.Valid}, HasChildren: {kv.Children != null}");
                if (kv.Children != null)
                {
                    AppLogger.LogDebug($"Root has {kv.Children.Count} children:");
                    foreach (var child in kv.Children.Take(10)) // Limit to first 10 children
                    {
                        AppLogger.LogDebug($"  - Child: '{child.Name}', Valid: {child.Valid}, HasChildren: {child.Children != null}");
                    }
                }
                else
                {
                    AppLogger.LogDebug("Root KeyValue has no children");
                    // Try to get raw data info
                    AppLogger.LogDebug($"KeyValue AsString: '{kv.AsString("")}'");
                    AppLogger.LogDebug($"KeyValue AsInteger: {kv.AsInteger(-1)}");
                    
                    // Check if this is a different KeyValue structure
                    // Sometimes the root itself might be the game ID section
                    if (kv.Valid)
                    {
                        var directStats = kv["stats"];
                        AppLogger.LogDebug($"Direct stats check - Valid: {directStats.Valid}, HasChildren: {directStats.Children != null}");
                        if (directStats.Valid && directStats.Children != null)
                        {
                            AppLogger.LogDebug($"Direct stats has {directStats.Children.Count} children");
                        }
                    }
                }
                
                _achievementDefinitions.Clear();
                _statDefinitions.Clear();

                var gameIdStr = _gameId.ToString(CultureInfo.InvariantCulture);
                AppLogger.LogDebug($"Looking for game ID section: {gameIdStr}");
                
                var gameSection = kv[gameIdStr];
                if (!gameSection.Valid)
                {
                    AppLogger.LogDebug($"Game section not found for ID: {gameIdStr}");
                    
                    // Try different approaches to find the game data
                    // 1. Try the root directly
                    if (kv.Valid && kv.Children != null)
                    {
                        AppLogger.LogDebug("Trying root level stats...");
                        var rootStats = kv["stats"];
                        if (rootStats.Valid)
                        {
                            AppLogger.LogDebug("Found stats at root level");
                            gameSection = kv;
                        }
                    }
                    
                    // 2. Try looking in first child
                    if (!gameSection.Valid && kv.Children != null && kv.Children.Count > 0)
                    {
                        AppLogger.LogDebug("Trying first child...");
                        var firstChild = kv.Children[0];
                        AppLogger.LogDebug($"First child name: '{firstChild.Name}'");
                        var firstChildStats = firstChild["stats"];
                        if (firstChildStats.Valid)
                        {
                            AppLogger.LogDebug("Found stats in first child");
                            gameSection = firstChild;
                        }
                    }
                    
                    if (!gameSection.Valid)
                    {
                        AppLogger.LogDebug("No valid game section found. This game may not have achievements/stats defined.");
                        // For games without achievements, we can still show the interface
                        // but with no data - this matches Legacy SAM behavior
                        return true; // Return true but with empty definitions
                    }
                }
                
                var stats = gameSection["stats"];
                if (!stats.Valid || stats.Children == null)
                {
                    AppLogger.LogDebug("Stats section not found or invalid");
                    return false;
                }

                AppLogger.LogDebug($"Found stats section with {stats.Children.Count} children");
                
                foreach (var stat in stats.Children)
                {
                    if (!stat.Valid) continue;

                    var rawType = stat["type_int"].Valid ? stat["type_int"].AsInteger(0) : stat["type"].AsInteger(0);
                    var type = (UserStatType)rawType;
                    
                    AppLogger.LogDebug($"Processing stat: {stat.Name}, type: {type}");
                    
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

                AppLogger.LogDebug($"Schema loading completed. Found {_achievementDefinitions.Count} achievements and {_statDefinitions.Count} stats");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Exception in LoadUserGameStatsSchema: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Parses an integer statistic from a VDF KeyValue node.
        /// </summary>
        /// <param name="stat">The KeyValue node containing the stat definition.</param>
        /// <param name="currentLanguage">The language code for localized display names.</param>
        private void ParseIntegerStat(KeyValue stat, string currentLanguage)
        {
            var id = stat["name"].AsString("");
            string name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);
            bool incrementOnly = stat["incrementonly"].AsBoolean(false);

            AppLogger.LogDebug($"Integer Stat parsed - ID: {id}, Name: '{name}', IncrementOnly: {incrementOnly}, Language: {currentLanguage}");

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

        /// <summary>
        /// Parses a floating-point statistic from a VDF KeyValue node.
        /// </summary>
        /// <param name="stat">The KeyValue node containing the stat definition.</param>
        /// <param name="currentLanguage">The language code for localized display names.</param>
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

        /// <summary>
        /// Parses achievement definitions from a VDF KeyValue node.
        /// Achievements are stored under "bits" sections with display information (name, description, icons).
        /// </summary>
        /// <param name="stat">The KeyValue node containing achievement definitions.</param>
        /// <param name="currentLanguage">The language code for localized names and descriptions.</param>
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

                    // Always get English names for search purposes
                    string englishName = GetLocalizedString(bit["display"]["name"], "english", id);
                    string englishDesc = GetLocalizedString(bit["display"]["desc"], "english", "");

                    AppLogger.LogDebug($"Achievement parsed - ID: {id}, Name: '{name}', EnglishName: '{englishName}', Language: {currentLanguage}");

                    string iconNormal = bit["display"]["icon"].AsString("");
                    string iconLocked = bit["display"]["icon_gray"].AsString("");

                    AppLogger.LogDebug($"Achievement {id} icons - Normal: '{iconNormal}', Locked: '{iconLocked}'");

                    _achievementDefinitions.Add(new AchievementDefinition
                    {
                        Id = id,
                        Name = name,
                        EnglishName = englishName,
                        Description = desc,
                        EnglishDescription = englishDesc,
                        IconNormal = iconNormal,
                        IconLocked = iconLocked,
                        IsHidden = bit["display"]["hidden"].AsBoolean(false),
                        Permission = bit["permission"].AsInteger(0)
                    });
                }
            }
        }

        /// <summary>
        /// Retrieves a localized string from a VDF KeyValue node with language fallback.
        /// Attempts to get the string in the specified language, falls back to English, then to the raw value.
        /// </summary>
        /// <param name="kv">The KeyValue node containing localized strings.</param>
        /// <param name="language">The preferred language code.</param>
        /// <param name="defaultValue">The default value to return if no localized string is found.</param>
        /// <returns>The localized string, or the default value if not found.</returns>
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

        /// <summary>
        /// Requests user statistics from Steam for the current game.
        /// This triggers a UserStatsReceived callback when Steam responds.
        /// </summary>
        /// <returns>True if the request was initiated successfully; false otherwise.</returns>
        public async Task<bool> RequestUserStatsAsync()
        {
            AppLogger.LogDebug($"GameStatsService.RequestUserStatsAsync called for game {_gameId}");
            bool result = await Task.Run(() => _steamClient.RequestUserStats((uint)_gameId));
            AppLogger.LogDebug($"GameStatsService.RequestUserStatsAsync result: {result}");
            return result;
        }

        /// <summary>
        /// Gets the current Steam UI language code.
        /// </summary>
        /// <returns>The language code (e.g., "english", "tchinese", "japanese").</returns>
        public string GetCurrentGameLanguage()
        {
            return _steamClient.GetCurrentGameLanguage();
        }

        /// <summary>
        /// Retrieves all achievements with their current status (achieved/locked) and unlock times.
        /// Queries Steam API for each achievement defined in the loaded schema.
        /// </summary>
        /// <returns>A list of achievement information objects.</returns>
        public List<AchievementInfo> GetAchievements()
        {
            AppLogger.LogDebug($"GetAchievements called - {_achievementDefinitions.Count} definitions available");
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
                        EnglishName = def.EnglishName,
                        Description = def.Description,
                        EnglishDescription = def.EnglishDescription,
                        IsAchieved = isAchieved,
                        UnlockTime = isAchieved && unlockTime > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime
                            : null,
                        IconNormal = def.IconNormal,
                        IconLocked = string.IsNullOrEmpty(def.IconLocked) ? def.IconNormal : def.IconLocked,
                        Permission = def.Permission
                    };

                    AppLogger.LogDebug($"Achievement created - ID: {achievement.Id}, Name: '{achievement.Name}', IsAchieved: {achievement.IsAchieved}");
                    achievements.Add(achievement);
                }
                else
                {
                    AppLogger.LogDebug($"Failed to get achievement data for ID: {def.Id}");
                }
            }

            return achievements;
        }

        /// <summary>
        /// Retrieves all statistics with their current values from Steam.
        /// Queries Steam API for each stat defined in the loaded schema.
        /// </summary>
        /// <returns>A list of statistic information objects (IntStatInfo or FloatStatInfo).</returns>
        public List<StatInfo> GetStatistics()
        {
            AppLogger.LogDebug($"GetStatistics called - {_statDefinitions.Count} definitions available");
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
                            Permission = intStat.Permission,
                            MinValue = intStat.MinValue,
                            MaxValue = intStat.MaxValue,
                            MaxChange = intStat.MaxChange
                        };

                        AppLogger.LogDebug($"Integer Stat created - ID: {statInfo.Id}, Name: '{statInfo.DisplayName}', Value: {statInfo.IntValue}, IncrementOnly: {statInfo.IsIncrementOnly}, Range: [{intStat.MinValue}, {intStat.MaxValue}]");
                        statistics.Add(statInfo);
                    }
                    else
                    {
                        AppLogger.LogDebug($"Failed to get stat value for ID: {intStat.Id}");
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
                            Permission = floatStat.Permission,
                            MinValue = floatStat.MinValue,
                            MaxValue = floatStat.MaxValue,
                            MaxChange = floatStat.MaxChange
                        });
                    }
                }
            }

            return statistics;
        }

        /// <summary>
        /// Sets an achievement to achieved or locked state.
        /// Protected achievements (permission & 3 != 0) cannot be modified.
        /// Debug builds log the operation without writing to Steam.
        /// </summary>
        /// <param name="id">The unique achievement identifier.</param>
        /// <param name="achieved">True to unlock the achievement, false to lock it.</param>
        /// <returns>True if the operation succeeded (or was logged in debug mode); false if the achievement is protected or the API call failed.</returns>
        /// <remarks>
        /// If EnableAchievementCascading is true and the achievement is being set to achieved,
        /// related statistics will be automatically adjusted (game-specific behavior).
        /// </remarks>
        public bool SetAchievement(string id, bool achieved)
        {
            // Check if achievement is protected
            var achievementDef = _achievementDefinitions.FirstOrDefault(a => a.Id == id);
            if (achievementDef != null)
            {
                bool isProtected = (achievementDef.Permission & 3) != 0;
                if (isProtected)
                {
                    AppLogger.LogDebug($"ERROR: Cannot modify protected achievement {id} (Permission: {achievementDef.Permission})");
                    return false;
                }
            }

            if (AppLogger.IsDebugMode)
            {
                AppLogger.LogDebug($"[DEBUG FAKE WRITE] SetAchievement: {id} = {achieved} (not actually written to Steam)");

                // Only adjust related statistics if cascading is enabled
                if (achieved && EnableAchievementCascading)
                {
                    AppLogger.LogDebug($"[CASCADING ENABLED] Adjusting related statistics for {id}");
                    AdjustRelatedStatistics(id);
                }

                return true; // Always return success in debug mode
            }

            AppLogger.LogDebug($"GameStatsService.SetAchievement called: {id} = {achieved}");

            // If setting achievement to true and cascading is enabled, adjust related statistics
            if (achieved && EnableAchievementCascading)
            {
                AppLogger.LogDebug($"[CASCADING ENABLED] Adjusting related statistics for {id}");
                AdjustRelatedStatistics(id);
            }
            else if (achieved && !EnableAchievementCascading)
            {
                AppLogger.LogDebug($"[CASCADING DISABLED] Skipping automatic stat adjustment for {id}");
            }

            bool success = _steamClient.SetAchievement(id, achieved);
            AppLogger.LogDebug($"SetAchievement result: {success} for {id} = {achieved}");
            return success;
        }

        /// <summary>
        /// Adjusts statistics related to achievements for specific games (experimental cascading feature).
        /// </summary>
        /// <param name="achievementId">The achievement ID being unlocked.</param>
        /// <remarks>
        /// EXPERIMENTAL: Adjusts statistics related to achievements for specific games.
        /// WARNING: This is GAME-SPECIFIC logic hardcoded for Warhammer 40,000: Battlesector.
        /// This should NOT be used for other games as it may cause incorrect stat values.
        /// Only activated when EnableAchievementCascading = true.
        /// When an achievement is unlocked, this method sets the related statistic to the required value
        /// and automatically triggers any other achievements that depend on the same stat with lower requirements.
        /// </remarks>
        private void AdjustRelatedStatistics(string achievementId)
        {
            // GAME-SPECIFIC: Warhammer 40,000: Battlesector (App ID: 1295480)
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
                { "FindWanderingEruditeOnAllMaps", ("FindWanderingEruditeOnAllMaps_Stat", 8) },
                { "FinishCampaignOnHardDifficulty", ("FinishCampaignOnHardDifficulty_Stat", 1) },
                { "UnlockXLexicanumEntries", ("UnlockXLexicanumEntries_Stat", 999) }
            };
            
            if (achievementStatMap.TryGetValue(achievementId, out var statInfo))
            {
                var (statId, requiredValue) = statInfo;
                
                // Get current stat value
                if (_steamClient.GetStatValue(statId, out int currentValue))
                {
                    // Always set to the required value to ensure consistency
                    // This handles cases where multiple achievements use the same stat with different values
                    AppLogger.LogDebug($"Setting stat {statId} to {requiredValue} for achievement {achievementId} (current: {currentValue})");
                    
                    if (AppLogger.IsDebugMode)
                    {
                        AppLogger.LogDebug($"[DEBUG FAKE WRITE] SetStatValue: {statId} = {requiredValue} (not actually written to Steam)");
                    }
                    else
                    {
                        bool success = _steamClient.SetStatValue(statId, requiredValue);
                        AppLogger.LogDebug($"SetStatValue result: {success} for {statId} = {requiredValue}");
                    }
                }
                else
                {
                    AppLogger.LogDebug($"Failed to get current value for stat {statId}");
                }
                
                // Auto-trigger other achievements that use the same statistic with lower requirements
                AutoTriggerRelatedAchievements(statId, requiredValue);
            }
        }

        /// <summary>
        /// Automatically triggers achievements that depend on the same statistic with lower or equal requirements.
        /// </summary>
        /// <param name="statId">The statistic ID that was modified.</param>
        /// <param name="newValue">The new value of the statistic.</param>
        /// <remarks>
        /// This method finds all achievements that use the specified statistic and unlocks those
        /// whose required value is less than or equal to the new stat value.
        /// Skips achievements that are already unlocked.
        /// </remarks>
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
                    // Check if achievement is not already achieved using direct Steam API call
                    if (_steamClient.GetAchievementAndUnlockTime(achievementId, out bool isAchieved, out _))
                    {
                        if (!isAchieved)
                        {
                            AppLogger.LogDebug($"Auto-triggering achievement {achievementId} due to stat {statId} = {newValue} (requires {requiredValue})");
                            
                            if (AppLogger.IsDebugMode)
                            {
                                AppLogger.LogDebug($"[DEBUG FAKE WRITE] Auto SetAchievement: {achievementId} = True (not actually written to Steam)");
                            }
                            else
                            {
                                bool success = _steamClient.SetAchievement(achievementId, true);
                                AppLogger.LogDebug($"Auto SetAchievement result: {success} for {achievementId} = True");
                            }
                        }
                        else
                        {
                            AppLogger.LogDebug($"Achievement {achievementId} is already achieved, skipping auto-trigger");
                        }
                    }
                    else
                    {
                        AppLogger.LogDebug($"Failed to get achievement status for {achievementId}, skipping auto-trigger");
                    }
                }
            }
        }

        /// <summary>
        /// Sets a statistic to a new value with validation.
        /// Validates IncrementOnly, Min/Max bounds, MaxChange constraints, and protection status.
        /// Debug builds log the operation without writing to Steam.
        /// </summary>
        /// <param name="stat">The statistic information object containing the new value.</param>
        /// <returns>True if the operation succeeded (or was logged in debug mode); false if validation failed or the stat is protected.</returns>
        /// <remarks>
        /// Validation rules:
        /// - Protected stats (permission & 3 != 0) cannot be modified
        /// - IncrementOnly stats cannot be decreased
        /// - Values must be within MinValue and MaxValue bounds
        /// - Change must not exceed MaxChange (if specified)
        /// </remarks>
        public bool SetStatistic(StatInfo stat)
        {
            // Check if stat is protected
            if (stat.IsProtected)
            {
                AppLogger.LogDebug($"ERROR: Cannot modify protected stat {stat.Id} (Permission: {stat.Permission})");
                return false;
            }

            if (stat is IntStatInfo intStat)
            {
                // Validate IncrementOnly constraint
                if (intStat.IsIncrementOnly && intStat.IntValue < intStat.OriginalValue)
                {
                    AppLogger.LogDebug($"ERROR: Cannot decrease IncrementOnly stat {intStat.Id} from {intStat.OriginalValue} to {intStat.IntValue}");
                    return false;
                }

                // Validate Min/Max bounds
                if (intStat.IntValue < intStat.MinValue || intStat.IntValue > intStat.MaxValue)
                {
                    AppLogger.LogDebug($"ERROR: Stat {intStat.Id} value {intStat.IntValue} is out of range [{intStat.MinValue}, {intStat.MaxValue}]");
                    return false;
                }

                // Validate MaxChange constraint (if specified)
                if (intStat.MaxChange > 0)
                {
                    int change = Math.Abs(intStat.IntValue - intStat.OriginalValue);
                    if (change > intStat.MaxChange)
                    {
                        AppLogger.LogDebug($"ERROR: Stat {intStat.Id} change {change} exceeds MaxChange {intStat.MaxChange}");
                        return false;
                    }
                }

                if (AppLogger.IsDebugMode)
                {
                    AppLogger.LogDebug($"[DEBUG FAKE WRITE] SetStatistic: {intStat.Id} = {intStat.IntValue} (not actually written to Steam)");
                    return true; // Always return success in debug mode
                }

                AppLogger.LogDebug($"GameStatsService.SetStatistic called: {intStat.Id} = {intStat.IntValue}");
                bool success = _steamClient.SetStatValue(intStat.Id, intStat.IntValue);
                AppLogger.LogDebug($"SetStatistic result: {success} for {intStat.Id} = {intStat.IntValue}");
                return success;
            }
            else if (stat is FloatStatInfo floatStat)
            {
                // Validate IncrementOnly constraint
                if (floatStat.IsIncrementOnly && floatStat.FloatValue < floatStat.OriginalValue)
                {
                    AppLogger.LogDebug($"ERROR: Cannot decrease IncrementOnly stat {floatStat.Id} from {floatStat.OriginalValue} to {floatStat.FloatValue}");
                    return false;
                }

                // Validate Min/Max bounds
                if (floatStat.FloatValue < floatStat.MinValue || floatStat.FloatValue > floatStat.MaxValue)
                {
                    AppLogger.LogDebug($"ERROR: Stat {floatStat.Id} value {floatStat.FloatValue} is out of range [{floatStat.MinValue}, {floatStat.MaxValue}]");
                    return false;
                }

                // Validate MaxChange constraint (if specified)
                if (floatStat.MaxChange > float.Epsilon)
                {
                    float change = Math.Abs(floatStat.FloatValue - floatStat.OriginalValue);
                    if (change > floatStat.MaxChange)
                    {
                        AppLogger.LogDebug($"ERROR: Stat {floatStat.Id} change {change:F2} exceeds MaxChange {floatStat.MaxChange:F2}");
                        return false;
                    }
                }

                if (AppLogger.IsDebugMode)
                {
                    AppLogger.LogDebug($"[DEBUG FAKE WRITE] SetStatistic: {floatStat.Id} = {floatStat.FloatValue} (not actually written to Steam)");
                    return true; // Always return success in debug mode
                }

                AppLogger.LogDebug($"GameStatsService.SetStatistic called: {floatStat.Id} = {floatStat.FloatValue}");
                bool success = _steamClient.SetStatValue(floatStat.Id, floatStat.FloatValue);
                AppLogger.LogDebug($"SetStatistic result: {success} for {floatStat.Id} = {floatStat.FloatValue}");
                return success;
            }
            return false;
        }

        /// <summary>
        /// Commits all pending achievement and statistic changes to Steam.
        /// Debug builds log the operation without writing to Steam.
        /// </summary>
        /// <returns>True if the changes were stored successfully (or logged in debug mode); false otherwise.</returns>
        /// <remarks>
        /// This method must be called after SetAchievement() or SetStatistic() to persist changes.
        /// Changes are batched locally until StoreStats() is called.
        /// </remarks>
        public bool StoreStats()
        {
            if (AppLogger.IsDebugMode)
            {
                AppLogger.LogDebug("[DEBUG FAKE WRITE] StoreStats: All changes committed to fake cache (not actually written to Steam)");
                return true; // Always return success in debug mode
            }
            
            AppLogger.LogDebug("GameStatsService.StoreStats called");
            bool success = _steamClient.StoreStats();
            AppLogger.LogDebug($"StoreStats result: {success}");
            return success;
        }

        /// <summary>
        /// Resets all statistics (and optionally achievements) to their default values.
        /// </summary>
        /// <param name="achievementsToo">True to also reset achievements; false to only reset statistics.</param>
        /// <returns>True if the reset succeeded; false otherwise.</returns>
        /// <remarks>
        /// WARNING: This operation is irreversible and will reset all progress.
        /// Use with caution.
        /// </remarks>
        public bool ResetAllStats(bool achievementsToo)
        {
            AppLogger.LogDebug($"GameStatsService.ResetAllStats called: achievements={achievementsToo}");
            return _steamClient.ResetAllStats(achievementsToo);
        }

        /// <summary>
        /// Handles the UserStatsReceived callback from Steam.
        /// Forwards the callback to registered event handlers.
        /// </summary>
        /// <param name="userStatsReceived">The callback data from Steam containing game ID, result code, and user ID.</param>
        private void OnUserStatsReceived(SteamGameClient.UserStatsReceived userStatsReceived)
        {
            AppLogger.LogDebug($"GameStatsService.OnUserStatsReceived - GameId: {userStatsReceived.GameId}, Result: {userStatsReceived.Result}, UserId: {userStatsReceived.UserId}");
            AppLogger.LogDebug($"UserStatsReceived event has {(UserStatsReceived == null ? 0 : UserStatsReceived.GetInvocationList().Length)} subscribers");
            
            UserStatsReceived?.Invoke(this, new UserStatsReceivedEventArgs
            {
                GameId = userStatsReceived.GameId,
                Result = userStatsReceived.Result,
                UserId = userStatsReceived.UserId
            });
        }

        /// <summary>
        /// Retrieves the Steam installation path from the Windows registry.
        /// Checks both HKLM and HKCU registry hives in 64-bit and 32-bit views.
        /// </summary>
        /// <returns>The Steam installation path, or an empty string if not found.</returns>
        private static string GetSteamInstallPath()
        {
            const string subKey = @"Software\Valve\Steam";
            
            AppLogger.LogDebug("Searching for Steam install path in registry...");
            
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
                            AppLogger.LogDebug($"Found Steam install path in HKLM {view}: {path}");
                            return path;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error reading HKLM {view}: {ex.Message}");
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
                            AppLogger.LogDebug($"Found Steam install path in HKCU {view}: {path}");
                            return path;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error reading HKCU {view}: {ex.Message}");
                }
            }
            
            AppLogger.LogDebug("Steam install path not found in registry");
            return "";
        }
    }

    /// <summary>
    /// Event arguments for the UserStatsReceived event.
    /// </summary>
    public class UserStatsReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the Steam AppID of the game for which stats were received.
        /// </summary>
        public ulong GameId { get; set; }

        /// <summary>
        /// Gets or sets the result code from Steam (0 = success, non-zero = error).
        /// </summary>
        public int Result { get; set; }

        /// <summary>
        /// Gets or sets the Steam user ID for which stats were received.
        /// </summary>
        public ulong UserId { get; set; }
    }

    /// <summary>
    /// Enumeration of VDF stat types found in UserGameStatsSchema files.
    /// </summary>
    public enum UserStatType
    {
        /// <summary>
        /// Invalid or unknown stat type.
        /// </summary>
        Invalid = 0,

        /// <summary>
        /// Integer-based statistic.
        /// </summary>
        Integer = 1,

        /// <summary>
        /// Floating-point statistic.
        /// </summary>
        Float = 2,

        /// <summary>
        /// Average rate statistic (computed from two values).
        /// </summary>
        AverageRate = 3,

        /// <summary>
        /// Achievement container (contains "bits" sections with individual achievements).
        /// </summary>
        Achievements = 4,

        /// <summary>
        /// Grouped achievement container.
        /// </summary>
        GroupAchievements = 5
    }
}
