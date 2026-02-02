using System;
using System.Linq;

namespace FractureCharacters
{
    /// <summary>
    /// Console commands for FractureCharacters administration
    /// 
    /// Commands:
    ///   fc_list                     - List all server-side characters
    ///   fc_list_online              - List currently connected players
    ///   fc_backups <steamid> <name> - List available backups for a character
    ///   fc_restore <steamid> <name> [index] - Restore a character from backup
    ///   fc_migration <on|off>       - Enable/disable new player migrations
    ///   fc_save                     - Force save all connected characters
    /// </summary>
    public static class ConsoleCommands
    {
        public static void Register()
        {
            // Commands are registered via new Terminal.ConsoleCommand(...)
            // They'll be available when the terminal is initialized
            new Terminal.ConsoleCommand("fc_list", "List all server-side characters", args =>
            {
                ListCharacters();
            });

            new Terminal.ConsoleCommand("fc_list_online", "List currently connected players", args =>
            {
                ListOnlinePlayers();
            });

            new Terminal.ConsoleCommand("fc_backups", "List backups for a character: fc_backups <steamid> <charactername>", args =>
            {
                if (args.Length < 3)
                {
                    Plugin.Log.LogWarning("Usage: fc_backups <steamid> <charactername>");
                    return;
                }
                ListBackups(args[1], args[2]);
            });

            new Terminal.ConsoleCommand("fc_restore", "Restore character from backup: fc_restore <steamid> <charactername> [backup_index]", args =>
            {
                if (args.Length < 3)
                {
                    Plugin.Log.LogWarning("Usage: fc_restore <steamid> <charactername> [backup_index]");
                    Plugin.Log.LogWarning("  backup_index defaults to 0 (most recent backup)");
                    return;
                }
                int backupIndex = 0;
                if (args.Length >= 4)
                {
                    int.TryParse(args[3], out backupIndex);
                }
                RestoreCharacter(args[1], args[2], backupIndex);
            });

            new Terminal.ConsoleCommand("fc_migration", "Enable/disable new migrations: fc_migration <on|off>", args =>
            {
                if (args.Length < 2)
                {
                    Plugin.Log.LogInfo($"Current migration status: {(Plugin.AllowNewMigrations.Value ? "ON" : "OFF")}");
                    return;
                }
                bool enable = args[1].ToLower() == "on" || args[1] == "1" || args[1].ToLower() == "true";
                Plugin.AllowNewMigrations.Value = enable;
                Plugin.Log.LogInfo($"Migration is now: {(enable ? "ON" : "OFF")}");
            });

            new Terminal.ConsoleCommand("fc_save", "Force save all connected characters", args =>
            {
                ForceSaveAll();
            });

            new Terminal.ConsoleCommand("fc_status", "Show FractureCharacters status", args =>
            {
                ShowStatus();
            });

            Plugin.Log.LogInfo("Registered FractureCharacters console commands (fc_*)");
        }

        private static void ListCharacters()
        {
            var characters = Plugin.GetAllCharacters();
            
            if (characters.Count == 0)
            {
                Console.instance.Print("No server-side characters found.");
                return;
            }

            Console.instance.Print($"=== Server-Side Characters ({characters.Count}) ===");
            foreach (var (steamId, charName, lastMod) in characters.OrderBy(c => c.steamId))
            {
                var backupCount = Plugin.GetBackups(steamId, charName).Count;
                Console.instance.Print($"  {steamId} / {charName}");
                Console.instance.Print($"    Last modified: {lastMod:yyyy-MM-dd HH:mm:ss}");
                Console.instance.Print($"    Backups: {backupCount}");
            }
        }

        private static void ListOnlinePlayers()
        {
            var online = ServerPatches.GetConnectedPlayers().ToList();
            
            if (online.Count == 0)
            {
                Console.instance.Print("No players currently connected.");
                return;
            }

            Console.instance.Print($"=== Connected Players ({online.Count}) ===");
            foreach (var player in online)
            {
                Console.instance.Print($"  {player.SteamId} / {player.CharacterName}");
                Console.instance.Print($"    Last save: {player.LastSave:HH:mm:ss}");
                Console.instance.Print($"    Data size: {player.ProfileData?.Length ?? player.PlayerData?.Length ?? 0} bytes");
            }
        }

        private static void ListBackups(string steamId, string characterName)
        {
            var backups = Plugin.GetBackups(steamId, characterName);
            
            if (backups.Count == 0)
            {
                Console.instance.Print($"No backups found for {steamId}/{characterName}");
                return;
            }

            Console.instance.Print($"=== Backups for {steamId}/{characterName} ===");
            for (int i = 0; i < backups.Count; i++)
            {
                var backup = backups[i];
                Console.instance.Print($"  [{i}] {backup.Name} ({backup.Length:N0} bytes) - {backup.CreationTime:yyyy-MM-dd HH:mm:ss}");
            }
            Console.instance.Print($"\nTo restore: fc_restore {steamId} {characterName} <index>");
        }

        private static void RestoreCharacter(string steamId, string characterName, int backupIndex)
        {
            // Check if player is online - warn if so
            var online = ServerPatches.GetConnectedPlayers()
                .FirstOrDefault(p => p.SteamId == steamId && 
                    p.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase));
            
            if (online != null)
            {
                Console.instance.Print("WARNING: This player is currently online!");
                Console.instance.Print("The restored character will take effect on their next login.");
            }

            // List available backups first
            var backups = Plugin.GetBackups(steamId, characterName);
            if (backups.Count == 0)
            {
                Console.instance.Print($"ERROR: No backups found for {steamId}/{characterName}");
                return;
            }

            if (backupIndex < 0 || backupIndex >= backups.Count)
            {
                Console.instance.Print($"ERROR: Invalid backup index. Available: 0 to {backups.Count - 1}");
                ListBackups(steamId, characterName);
                return;
            }

            // Perform restore
            if (Plugin.RestoreFromBackup(steamId, characterName, backupIndex))
            {
                Console.instance.Print($"SUCCESS: Restored {steamId}/{characterName} from backup:");
                Console.instance.Print($"  {backups[backupIndex].Name}");
                Console.instance.Print($"  (A pre-restore backup was created as .pre_restore)");
            }
            else
            {
                Console.instance.Print("ERROR: Restore failed. Check server logs.");
            }
        }

        private static void ForceSaveAll()
        {
            var online = ServerPatches.GetConnectedPlayers().ToList();
            
            if (online.Count == 0)
            {
                Console.instance.Print("No players to save.");
                return;
            }

            Console.instance.Print($"Triggering save for {online.Count} player(s)...");
            
            // Trigger a world save which will save all player data
            if (ZNet.instance != null)
            {
                ZNet.instance.SaveWorld(true);
                Console.instance.Print("Save triggered successfully.");
            }
        }

        private static void ShowStatus()
        {
            Console.instance.Print("=== FractureCharacters Status ===");
            Console.instance.Print($"  Version: {Plugin.ModVersion}");
            Console.instance.Print($"  Enabled: {Plugin.EnableMod.Value}");
            Console.instance.Print($"  Allow new migrations: {Plugin.AllowNewMigrations.Value}");
            Console.instance.Print($"  Backup count: {Plugin.BackupCount.Value}");
            Console.instance.Print($"  Save interval: {Plugin.SaveIntervalSeconds.Value}s");
            Console.instance.Print($"  Save path: {Plugin.CharacterSavePath}");
            
            var characters = Plugin.GetAllCharacters();
            var online = ServerPatches.GetConnectedPlayers().Count();
            Console.instance.Print($"  Total characters: {characters.Count}");
            Console.instance.Print($"  Currently online: {online}");
        }
    }
}
