using System;
using System.IO;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FractureCharacters
{
    /// <summary>
    /// FractureCharacters - Server-side character storage for Valheim
    /// 
    /// This mod runs SERVER-SIDE ONLY. Clients do not need the mod installed.
    /// 
    /// Features:
    /// - Stores character data on the server, preventing loss from network issues
    /// - One-time migration for existing players bringing client-side characters
    /// - Automatic backups with easy restoration via console commands
    /// - Tracks which players have migrated to prevent exploits
    /// </summary>
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "com.fracture.servercharacters";
        public const string ModName = "FractureCharacters";
        public const string ModVersion = "1.2.0";

        private static Plugin _instance;
        public static Plugin Instance => _instance;
        
        internal static ManualLogSource Log;
        private Harmony _harmony;
        private AutoUpdater _autoUpdater;

        // Configuration
        public static ConfigEntry<bool> EnableMod;
        public static ConfigEntry<bool> AllowNewMigrations;
        public static ConfigEntry<int> BackupCount;
        public static ConfigEntry<int> SaveIntervalSeconds;

        // Server-side character storage path
        public static string CharacterSavePath => Path.Combine(
            global::Utils.GetSaveDataPath(FileHelpers.FileSource.Local),
            "characters_server"
        );

        // Track migrated players
        public static string MigratedPlayersFile => Path.Combine(CharacterSavePath, "migrated_players.txt");
        private static HashSet<string> _migratedPlayers = new HashSet<string>();

        private void Awake()
        {
            _instance = this;
            Log = Logger;

            // Initialize auto-updater FIRST (before anything else)
            string pluginPath = Info.Location;
            _autoUpdater = new AutoUpdater(ModVersion, pluginPath, Config);
            
            // Apply any pending updates from previous session
            bool updateApplied = _autoUpdater.ApplyPendingUpdate();
            if (updateApplied)
            {
                Log.LogWarning("An update was applied. Please restart to use the new version.");
            }
            
            // Check for new updates asynchronously (non-blocking)
            _autoUpdater.CheckForUpdatesAsync();

            // Configuration
            EnableMod = Config.Bind("General", "EnableMod", true,
                "Enable server-side character storage");
            
            AllowNewMigrations = Config.Bind("Migration", "AllowNewMigrations", true,
                "Allow players without server-side characters to migrate their client character (one-time per player). " +
                "Set to false after all your players have connected once to prevent new character imports.");
            
            BackupCount = Config.Bind("Backups", "BackupCount", 10,
                "Number of character backups to keep per player");
            
            SaveIntervalSeconds = Config.Bind("Saving", "SaveIntervalSeconds", 300,
                "How often to save all connected player characters (in seconds). Default: 300 (5 minutes)");

            if (!EnableMod.Value)
            {
                Log.LogInfo("FractureCharacters is disabled in config");
                return;
            }

            // Create save directory
            Directory.CreateDirectory(CharacterSavePath);
            Directory.CreateDirectory(Path.Combine(CharacterSavePath, "backups"));

            // Load migrated players list
            LoadMigratedPlayers();

            // Apply Harmony patches (server-side only)
            _harmony = new Harmony(ModGUID);
            _harmony.PatchAll(typeof(ServerPatches));

            // Register console commands
            ConsoleCommands.Register();

            Log.LogInfo($"FractureCharacters v{ModVersion} loaded (SERVER-SIDE ONLY)");
            Log.LogInfo($"Character save path: {CharacterSavePath}");
            Log.LogInfo($"Allow new migrations: {AllowNewMigrations.Value}");
            Log.LogInfo($"Migrated players: {_migratedPlayers.Count}");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        #region Migration Tracking

        private static void LoadMigratedPlayers()
        {
            _migratedPlayers.Clear();
            if (File.Exists(MigratedPlayersFile))
            {
                foreach (var line in File.ReadAllLines(MigratedPlayersFile))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                    {
                        _migratedPlayers.Add(trimmed);
                    }
                }
            }
        }

        public static bool HasPlayerMigrated(string steamId)
        {
            return _migratedPlayers.Contains(steamId);
        }

        public static void MarkPlayerMigrated(string steamId)
        {
            if (_migratedPlayers.Add(steamId))
            {
                // Append to file
                File.AppendAllText(MigratedPlayersFile, $"{steamId}\n");
                Log.LogInfo($"Marked player {steamId} as migrated");
            }
        }

        public static bool CanPlayerMigrate(string steamId)
        {
            // Player can migrate if:
            // 1. Migrations are allowed globally
            // 2. This specific player hasn't migrated yet
            return AllowNewMigrations.Value && !HasPlayerMigrated(steamId);
        }

        #endregion

        #region File Paths

        /// <summary>
        /// Get the server-side character file path for a player
        /// Format: {steamId}_{characterName}.fch
        /// </summary>
        public static string GetCharacterPath(string steamId, string characterName)
        {
            string safeName = SanitizeFileName(characterName);
            return Path.Combine(CharacterSavePath, $"{steamId}_{safeName}.fch");
        }

        /// <summary>
        /// Get the backup directory for a player's character
        /// </summary>
        public static string GetBackupDir(string steamId, string characterName)
        {
            string safeName = SanitizeFileName(characterName);
            return Path.Combine(CharacterSavePath, "backups", $"{steamId}_{safeName}");
        }

        /// <summary>
        /// Check if a server-side character exists
        /// </summary>
        public static bool HasServerCharacter(string steamId, string characterName)
        {
            return File.Exists(GetCharacterPath(steamId, characterName));
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.ToLowerInvariant();
        }

        #endregion

        #region Backup Management

        /// <summary>
        /// Create a backup of a character file before overwriting
        /// </summary>
        public static void CreateBackup(string steamId, string characterName)
        {
            string characterPath = GetCharacterPath(steamId, characterName);
            if (!File.Exists(characterPath)) return;

            string backupDir = GetBackupDir(steamId, characterName);
            Directory.CreateDirectory(backupDir);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupPath = Path.Combine(backupDir, $"{timestamp}.fch");

            File.Copy(characterPath, backupPath);
            Log.LogDebug($"Created backup: {backupPath}");

            // Cleanup old backups
            CleanupBackups(backupDir);
        }

        private static void CleanupBackups(string backupDir)
        {
            try
            {
                var files = new DirectoryInfo(backupDir).GetFiles("*.fch");
                if (files.Length <= BackupCount.Value) return;

                // Sort by creation time, newest first
                Array.Sort(files, (a, b) => b.CreationTime.CompareTo(a.CreationTime));

                // Delete oldest files beyond the limit
                for (int i = BackupCount.Value; i < files.Length; i++)
                {
                    files[i].Delete();
                    Log.LogDebug($"Deleted old backup: {files[i].Name}");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Error cleaning up backups: {ex.Message}");
            }
        }

        /// <summary>
        /// Get list of available backups for a character
        /// </summary>
        public static List<FileInfo> GetBackups(string steamId, string characterName)
        {
            string backupDir = GetBackupDir(steamId, characterName);
            if (!Directory.Exists(backupDir))
                return new List<FileInfo>();

            var files = new DirectoryInfo(backupDir).GetFiles("*.fch");
            var list = new List<FileInfo>(files);
            list.Sort((a, b) => b.CreationTime.CompareTo(a.CreationTime)); // Newest first
            return list;
        }

        /// <summary>
        /// Restore a character from backup
        /// </summary>
        public static bool RestoreFromBackup(string steamId, string characterName, int backupIndex = 0)
        {
            var backups = GetBackups(steamId, characterName);
            if (backupIndex < 0 || backupIndex >= backups.Count)
            {
                Log.LogError($"Invalid backup index {backupIndex}. Available: 0-{backups.Count - 1}");
                return false;
            }

            string characterPath = GetCharacterPath(steamId, characterName);
            string backupPath = backups[backupIndex].FullName;

            // Create a backup of current state before restoring
            if (File.Exists(characterPath))
            {
                string preRestoreBackup = characterPath + ".pre_restore";
                File.Copy(characterPath, preRestoreBackup, true);
            }

            File.Copy(backupPath, characterPath, true);
            Log.LogInfo($"Restored {steamId}/{characterName} from backup {backups[backupIndex].Name}");
            return true;
        }

        #endregion

        #region Player Listing

        /// <summary>
        /// Get all server-side characters
        /// </summary>
        public static List<(string steamId, string characterName, DateTime lastModified)> GetAllCharacters()
        {
            var result = new List<(string, string, DateTime)>();
            
            foreach (var file in Directory.GetFiles(CharacterSavePath, "*.fch"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var parts = fileName.Split(new[] { '_' }, 2);
                if (parts.Length >= 2)
                {
                    string steamId = parts[0];
                    string charName = parts[1];
                    var lastModified = File.GetLastWriteTime(file);
                    result.Add((steamId, charName, lastModified));
                }
            }
            
            return result;
        }

        #endregion
    }
}
