/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Playtime Tracker", "VisEntities", "1.0.0")]
    [Description("Provides an API to track player playtime and AFK time for other plugins.")]
    public class PlaytimeTracker : RustPlugin
    {
        #region Fields

        private static PlaytimeTracker _plugin;
        private static Configuration _config;
        private StoredData _storedData;
        private readonly Dictionary<ulong, AfkTracker> _afkTrackers = new Dictionary<ulong, AfkTracker>();

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Update Interval Seconds")]
            public int UpdateIntervalSeconds { get; set; }

            [JsonProperty("Enable Afk Tracking")]
            public bool EnableAfkTracking { get; set; }

            [JsonProperty("Idle Timeout Seconds")]
            public int IdleTimeoutSeconds { get; set; }

            [JsonProperty("Data Autosave Interval Seconds")]
            public int DataAutosaveIntervalSeconds { get; set; }

            [JsonProperty("Reset Data On New Save")]
            public bool ResetDataOnNewSave { get; set; }

            [JsonProperty("Top Leaderboard Entries")]
            public int TopLeaderboardEntries { get; set; }

            [JsonProperty("Do Not Track Players")]
            public List<ulong> DoNotTrackPlayers { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                UpdateIntervalSeconds = 60,
                EnableAfkTracking = true,
                IdleTimeoutSeconds = 300,
                DataAutosaveIntervalSeconds = 300,
                ResetDataOnNewSave = false,
                TopLeaderboardEntries = 10,
                DoNotTrackPlayers = new List<ulong>()
            };
        }

        #endregion Configuration

        #region Stored Data

        public class StoredData
        {
            [JsonProperty("Players")]
            public Dictionary<ulong, PlayerData> Players { get; set; } = new Dictionary<ulong, PlayerData>();
        }

        public class PlayerData
        {
            [JsonProperty("Name")]
            public string Name { get; set; }

            [JsonProperty("Playtime Seconds")]
            public double PlaytimeSeconds { get; set; }

            [JsonProperty("Afk Seconds")]
            public double AfkSeconds { get; set; }
        }

        private PlayerData GetOrCreatePlayerData(BasePlayer player)
        {
            if (!_storedData.Players.TryGetValue(player.userID, out var playerData))
            {
                playerData = new PlayerData
                {
                    Name = player.displayName,
                    PlaytimeSeconds = 0,
                    AfkSeconds = 0
                };
                _storedData.Players[player.userID] = playerData;
            }
            else
                playerData.Name = player.displayName;

            return playerData;
        }

        private void SaveLoop() => timer.In(_config.DataAutosaveIntervalSeconds, () =>
        {
            DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);
            SaveLoop();
        });

        #endregion Stored Data

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
            _storedData = DataFileUtil.LoadOrCreate<StoredData>(DataFileUtil.GetFilePath());
        }

        private void OnServerInitialized(bool isStartup)
        {
            timer.Every(_config.UpdateIntervalSeconds, UpdatePlaytime);
            SaveLoop();
        }

        private void Unload()
        {
            DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);
            _plugin = null;
            _config = null;
        }

        private void OnNewSave(string filename)
        {
            if (!_config.ResetDataOnNewSave)
                return;

            DataFileUtil.Delete(DataFileUtil.GetFilePath());
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (IsPlayerExcluded(player.userID))
                return;

            _afkTrackers[player.userID] = new AfkTracker
            {
                LastPosition = player.transform.position,
                LastMoveTime = Time.realtimeSinceStartup
            };
            GetOrCreatePlayerData(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            SavePlayerTimeOnDisconnect(player);
            _afkTrackers.Remove(player.userID);
        }

        #endregion Oxide Hooks

        #region Core

        private void UpdatePlaytime()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected)
                    continue;

                if (IsPlayerExcluded(player.userID))
                    continue;

                if (!_afkTrackers.TryGetValue(player.userID, out var inactivityTracker))
                {
                    inactivityTracker = new AfkTracker
                    {
                        LastPosition = player.transform.position,
                        LastMoveTime = Time.realtimeSinceStartup
                    };
                    _afkTrackers[player.userID] = inactivityTracker;
                }

                var playerData = GetOrCreatePlayerData(player);
                bool moved = Vector3.Distance(player.transform.position, inactivityTracker.LastPosition) > 0.1f;

                if (moved)
                {
                    playerData.PlaytimeSeconds += _config.UpdateIntervalSeconds;
                    inactivityTracker.LastPosition = player.transform.position;
                    inactivityTracker.LastMoveTime = Time.realtimeSinceStartup;
                }
                else
                {
                    if (_config.EnableAfkTracking)
                    {
                        float idleSeconds = Time.realtimeSinceStartup - inactivityTracker.LastMoveTime;
                        if (idleSeconds >= _config.IdleTimeoutSeconds)
                            playerData.AfkSeconds += _config.UpdateIntervalSeconds;
                        else
                            playerData.PlaytimeSeconds += _config.UpdateIntervalSeconds;
                    }
                    else
                    {
                        playerData.PlaytimeSeconds += _config.UpdateIntervalSeconds;
                    }
                }

                bool isAfkNow = _config.EnableAfkTracking &&
                    !moved &&
                    (Time.realtimeSinceStartup - inactivityTracker.LastMoveTime) >= _config.IdleTimeoutSeconds;
                ExposedHooks.OnPlaytimeUpdate(player, playerData.PlaytimeSeconds, playerData.AfkSeconds, isAfkNow);
            }
        }

        private void SavePlayerTimeOnDisconnect(BasePlayer player)
        {
            if (!_afkTrackers.TryGetValue(player.userID, out AfkTracker afkTracker))
                return;

            PlayerData data = GetOrCreatePlayerData(player);
            float idleSeconds = Time.realtimeSinceStartup - afkTracker.LastMoveTime;

            if (_config.EnableAfkTracking && idleSeconds >= _config.IdleTimeoutSeconds)
                data.AfkSeconds += idleSeconds;
            else
                data.PlaytimeSeconds += idleSeconds;
        }

        #endregion Core

        #region Afk Tracking

        private class AfkTracker
        {
            public Vector3 LastPosition;
            public float LastMoveTime;
        }

        #endregion Afk Tracking

        #region API

        [HookMethod(nameof(API_GetPlaytimeSeconds))]
        public double API_GetPlaytimeSeconds(ulong playerId)
        {
            PlayerData playerData;
            if (_storedData.Players.TryGetValue(playerId, out playerData))
            {
                return playerData.PlaytimeSeconds;
            }

            return 0d;
        }

        [HookMethod(nameof(API_GetAfkSeconds))]
        public double API_GetAfkSeconds(ulong playerId)
        {
            PlayerData playerData;
            if (_storedData.Players.TryGetValue(playerId, out playerData))
            {
                return playerData.AfkSeconds;
            }

            return 0d;
        }

        [HookMethod(nameof(API_GetFormattedPlaytime))]
        public string API_GetFormattedPlaytime(ulong playerId)
        {
            double playtimeSeconds = API_GetPlaytimeSeconds(playerId);
            return FormatTime(playtimeSeconds);
        }

        [HookMethod(nameof(API_GetTopPlaytimes))]
        public List<Dictionary<string, object>> API_GetTopPlaytimes(int count = 10)
        {
            IEnumerable<KeyValuePair<ulong, PlayerData>> orderedPlayers =
                _storedData.Players
                           .OrderByDescending(pair => pair.Value.PlaytimeSeconds)
                           .Take(count);

            List<Dictionary<string, object>> topList = new List<Dictionary<string, object>>();

            foreach (KeyValuePair<ulong, PlayerData> pair in orderedPlayers)
            {
                Dictionary<string, object> entry = new Dictionary<string, object>
                {
                    ["PlayerId"] = pair.Key,
                    ["PlaytimeSeconds"] = pair.Value.PlaytimeSeconds
                };

                topList.Add(entry);
            }

            return topList;
        }

        #endregion API

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static void OnPlaytimeUpdate(BasePlayer player, double playtime, double afkTime, bool isAfkNow)
            {
                var timeData = new Dictionary<string, object>
                {
                    ["PlaytimeSeconds"] = playtime,
                    ["AfkSeconds"] = afkTime,
                    ["IsCurrentlyAfk"] = isAfkNow
                };

                Interface.CallHook("OnPlaytimeUpdate", player, timeData);
            }
        }

        #endregion Exposed Hooks

        #region Helper Functions

        private static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            int hours = (int)t.TotalHours;
            return $"{hours:00}h:{t.Minutes:00}m:{t.Seconds:00}s";
        }

        private BasePlayer FindPlayerByPartialNameOrId(string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId))
                return null;

            if (ulong.TryParse(nameOrId, out ulong id))
            {
                BasePlayer found = BasePlayer.activePlayerList.FirstOrDefault(x => x.userID == id);
                if (found != null)
                    return found;
            }

            nameOrId = nameOrId.ToLower();
            List<BasePlayer> matches = BasePlayer.activePlayerList.Where(x => x.displayName.ToLower().Contains(nameOrId)).ToList();
            if (matches.Count == 1)
                return matches[0];

            return null;
        }

        private bool IsPlayerExcluded(ulong playerId)
        {
            return _config.DoNotTrackPlayers != null &&
                   _config.DoNotTrackPlayers.Contains(playerId);
        }

        #endregion Helper Functions

        #region Helper Classes

        public static class DataFileUtil
        {
            private const string FOLDER = "";

            public static void EnsureFolderCreated()
            {
                string path = Path.Combine(Interface.Oxide.DataDirectory, FOLDER);

                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }

            public static string GetFilePath(string filename = null)
            {
                if (filename == null)
                    filename = _plugin.Name;

                return Path.Combine(FOLDER, filename);
            }

            public static string[] GetAllFilePaths()
            {
                string[] filePaths = Interface.Oxide.DataFileSystem.GetFiles(FOLDER);
                for (int i = 0; i < filePaths.Length; i++)
                {
                    filePaths[i] = filePaths[i].Substring(0, filePaths[i].Length - 5);
                }

                return filePaths;
            }

            public static bool Exists(string filePath)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(filePath);
            }

            public static T Load<T>(string filePath) where T : class, new()
            {
                T data = Interface.Oxide.DataFileSystem.ReadObject<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static T LoadIfExists<T>(string filePath) where T : class, new()
            {
                if (Exists(filePath))
                    return Load<T>(filePath);
                else
                    return null;
            }

            public static T LoadOrCreate<T>(string filePath) where T : class, new()
            {
                T data = LoadIfExists<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static void Save<T>(string filePath, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject<T>(filePath, data);
            }

            public static void Delete(string filePath)
            {
                Interface.Oxide.DataFileSystem.DeleteDataFile(filePath);
            }
        }

        #endregion Helper Classes

        #region Permissions

        private static class PermissionUtil
        {
            public const string USE = "playtimetracker.use";
            private static readonly List<string> _permissions = new List<string>
            {
                ADMIN,
                USE,
            };

            public static void RegisterPermissions()
            {
                foreach (var permission in _permissions)
                {
                    _plugin.permission.RegisterPermission(permission, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions

        #region Commands

        [ChatCommand("playtime")]
        private void cmdPlaytime(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                PlayerData data = GetOrCreatePlayerData(player);
                MessagePlayer(player, Lang.Info_SelfTime, FormatTime(data.PlaytimeSeconds), FormatTime(data.AfkSeconds));
                MessagePlayer(player, Lang.Help_General);
                return;
            }

            if (args[0].Equals("top", StringComparison.OrdinalIgnoreCase))
            {
                List<PlayerData> top =
                    _storedData.Players
                    .Where(kv => !IsPlayerExcluded(kv.Key))
                    .Select(kv => kv.Value)
                    .OrderByDescending(d => d.PlaytimeSeconds)
                    .Take(_config.TopLeaderboardEntries)
                    .ToList();

                MessagePlayer(player, Lang.Leaderboard_Header);
                int rank = 1;
                foreach (PlayerData entry in top)
                {
                    MessagePlayer(player, Lang.Leaderboard_Entry, rank, entry.Name, FormatTime(entry.PlaytimeSeconds));
                    rank++;
                }

                return;
            }

            MessagePlayer(player, Lang.Error_UnknownSubCommand);
        }

        [ConsoleCommand("playtime")]
        private void cmdConsolePlaytime(ConsoleSystem.Arg arg)
        {
            BasePlayer caller = arg.Player();
            bool isServerConsole = caller == null;

            if (!isServerConsole && !caller.IsAdmin)
            {
                MessagePlayer(caller, Lang.Error_NoPermission);
                return;
            }

            if (arg.Args == null || arg.Args.Length == 0)
            {
                string usage = GetMessage(caller, Lang.Usage_Console_General); ;
                if (isServerConsole)
                    Puts(usage); 
               else
                    SendReply(caller, usage);
                return;
            }

            bool wantReset = arg.Args[0].Equals("reset", StringComparison.OrdinalIgnoreCase);
            string search;
            if (wantReset)
            {
                if (arg.Args.Length > 1)
                    search = arg.Args[1];
                else
                    search = null;
            }
            else
                search = arg.Args[0];

            if (string.IsNullOrEmpty(search))
            {
                string usage = GetMessage(caller, Lang.Usage_Console_Reset); ;
                if (isServerConsole)
                    Puts(usage);
                else
                    SendReply(caller, usage);
                return;
            }

            ulong targetId = 0;
            BasePlayer online = FindPlayerByPartialNameOrId(search);
            if (online != null) targetId = online.userID;
            else if (!ulong.TryParse(search, out targetId))
            {
                string msg = GetMessage(caller, Lang.Error_PlayerNotFound, search);
                if (isServerConsole)
                    Puts(msg); 
               else
                    SendReply(caller, msg);
                return;
            }

            if (wantReset)
            {
                if (_storedData.Players.Remove(targetId))
                {
                    DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);

                    string msg = GetMessage(caller, Lang.Info_ResetSuccess, targetId);
                    if (isServerConsole)
                        Puts(msg);
                    else
                        SendReply(caller, msg);
                }
                else
                {
                    string msg = GetMessage(caller, Lang.Error_NoDataStored);
                    if (isServerConsole)
                        Puts(msg);
                    else
                        SendReply(caller, msg);
                }
                return;
            }

            if (!_storedData.Players.TryGetValue(targetId, out PlayerData data))
            {
                string msg = GetMessage(caller, Lang.Error_NoDataForPlayer);
                if (isServerConsole)
                    Puts(msg);
                else
                    SendReply(caller, msg);
                return;
            }

            string result = $"{targetId} {data.Name} {FormatTime(data.PlaytimeSeconds)} (AFK {FormatTime(data.AfkSeconds)})";
            if (isServerConsole)
                Puts(result);
            else
                SendReply(caller, result);
        }

        #endregion Commands

        #region Localization

        private class Lang
        {
            public const string Info_SelfTime = "Info.SelfTime";
            public const string Info_ResetSuccess = "Info.ResetSuccess";
            public const string Help_General = "Help.General";
            public const string Usage_Console_General = "Usage.Console.General";
            public const string Usage_Console_Reset = "Usage.Console.Reset";
            public const string Leaderboard_Header = "Leaderboard.Header";
            public const string Leaderboard_Entry = "Leaderboard.Entry";
            public const string Error_NoPermission = "Error.NoPermission";
            public const string Error_PlayerNotFound = "Error.PlayerNotFound";
            public const string Error_NoDataStored = "Error.NoDataStored";
            public const string Error_NoDataForPlayer = "Error.NoDataForPlayer";
            public const string Error_UnknownSubCommand = "Error.UnknownSubCommand";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.Info_SelfTime] = "Playtime: {0} (AFK {1})",
                [Lang.Info_ResetSuccess] = "Reset stats for {0}.",
                [Lang.Help_General] = "Type /playtime top to see the leaders.",
                [Lang.Usage_Console_General] = "Usage: playtime <nameOrId> | playtime reset <nameOrId>",
                [Lang.Usage_Console_Reset] = "Usage: playtime reset <nameOrId>",
                [Lang.Leaderboard_Header] = "Top playtimes:",
                [Lang.Leaderboard_Entry] = "{0}. {1} – {2}",
                [Lang.Error_NoPermission] = "You do not have permission to use this command.",
                [Lang.Error_PlayerNotFound] = "Player '{0}' not found.",
                [Lang.Error_NoDataStored] = "No data stored for that player.",
                [Lang.Error_NoDataForPlayer] = "No data for that player.",
                [Lang.Error_UnknownSubCommand] = "Unknown sub-command."

            }, this, "en");
        }

        private static string GetMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string userId;
            if (player != null)
                userId = player.UserIDString;
            else
                userId = null;

            string message = _plugin.lang.GetMessage(messageKey, _plugin, userId);

            if (args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void MessagePlayer(BasePlayer player, string messageKey, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);

            if (!string.IsNullOrWhiteSpace(message))
                _plugin.SendReply(player, message);
        }

        #endregion Localization
    }
}