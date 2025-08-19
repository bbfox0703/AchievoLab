using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnSAM.RunGame.Models;
using AnSAM.RunGame.Steam;
using AnSAM.RunGame.Utils;

namespace AnSAM.RunGame.Services
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
                string path = Path.Combine(installPath, "appcache", "stats", fileName);
                
                if (!File.Exists(path)) return false;

                var kv = KeyValue.LoadAsBinary(path);
                if (kv == null) return false;

                _achievementDefinitions.Clear();
                _statDefinitions.Clear();

                var stats = kv[_gameId.ToString(CultureInfo.InvariantCulture)]["stats"];
                if (!stats.Valid || stats.Children == null) return false;

                foreach (var stat in stats.Children)
                {
                    if (!stat.Valid) continue;

                    var rawType = stat["type_int"].Valid ? stat["type_int"].AsInteger(0) : stat["type"].AsInteger(0);
                    var type = (UserStatType)rawType;
                    
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

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ParseIntegerStat(KeyValue stat, string currentLanguage)
        {
            var id = stat["name"].AsString("");
            string name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);

            _statDefinitions.Add(new IntegerStatDefinition
            {
                Id = id,
                DisplayName = name,
                MinValue = stat["min"].AsInteger(int.MinValue),
                MaxValue = stat["max"].AsInteger(int.MaxValue),
                MaxChange = stat["maxchange"].AsInteger(0),
                IncrementOnly = stat["incrementonly"].AsBoolean(false),
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

                    _achievementDefinitions.Add(new AchievementDefinition
                    {
                        Id = id,
                        Name = name,
                        Description = desc,
                        IconNormal = bit["display"]["icon"].AsString(""),
                        IconLocked = bit["display"]["icon_gray"].AsString(""),
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
            var achievements = new List<AchievementInfo>();

            foreach (var def in _achievementDefinitions)
            {
                if (string.IsNullOrEmpty(def.Id)) continue;

                if (_steamClient.GetAchievementAndUnlockTime(def.Id, out bool isAchieved, out var unlockTime))
                {
                    achievements.Add(new AchievementInfo
                    {
                        Id = def.Id,
                        Name = def.Name,
                        Description = def.Description,
                        IsAchieved = isAchieved,
                        UnlockTime = isAchieved && unlockTime > 0 
                            ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime 
                            : null,
                        IconNormal = def.IconNormal,
                        IconLocked = def.IconLocked,
                        Permission = def.Permission
                    });
                }
            }

            return achievements;
        }

        public List<StatInfo> GetStatistics()
        {
            var statistics = new List<StatInfo>();

            foreach (var stat in _statDefinitions)
            {
                if (string.IsNullOrEmpty(stat.Id)) continue;

                if (stat is IntegerStatDefinition intStat)
                {
                    if (_steamClient.GetStatValue(intStat.Id, out int value))
                    {
                        statistics.Add(new IntStatInfo
                        {
                            Id = intStat.Id,
                            DisplayName = intStat.DisplayName,
                            IntValue = value,
                            OriginalValue = value,
                            IsIncrementOnly = intStat.IncrementOnly,
                            Permission = intStat.Permission
                        });
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
            DebugLogger.LogDebug($"GameStatsService.SetAchievement called: {id} = {achieved}");
            return _steamClient.SetAchievement(id, achieved);
        }

        public bool SetStatistic(StatInfo stat)
        {
            if (stat is IntStatInfo intStat)
            {
                DebugLogger.LogDebug($"GameStatsService.SetStatistic called: {intStat.Id} = {intStat.IntValue}");
                return _steamClient.SetStatValue(intStat.Id, intStat.IntValue);
            }
            else if (stat is FloatStatInfo floatStat)
            {
                DebugLogger.LogDebug($"GameStatsService.SetStatistic called: {floatStat.Id} = {floatStat.FloatValue}");
                return _steamClient.SetStatValue(floatStat.Id, floatStat.FloatValue);
            }
            return false;
        }

        public bool StoreStats()
        {
            DebugLogger.LogDebug("GameStatsService.StoreStats called");
            return _steamClient.StoreStats();
        }

        public bool ResetAllStats(bool achievementsToo)
        {
            DebugLogger.LogDebug($"GameStatsService.ResetAllStats called: achievements={achievementsToo}");
            return _steamClient.ResetAllStats(achievementsToo);
        }

        private void OnUserStatsReceived(SteamGameClient.UserStatsReceived userStatsReceived)
        {
            UserStatsReceived?.Invoke(this, new UserStatsReceivedEventArgs
            {
                GameId = userStatsReceived.GameId,
                Result = userStatsReceived.Result,
                UserId = userStatsReceived.UserId
            });
        }

        private static string GetSteamInstallPath()
        {
            const string subKey = @"Software\\Valve\\Steam";
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view).OpenSubKey(subKey);
                if (key == null) continue;
                var path = key.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(path)) return path;
            }
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