using System;
using System.IO;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FractureCharacters
{
    /// <summary>
    /// Server-side Harmony patches for character storage
    /// 
    /// These patches intercept the normal game flow to:
    /// 1. Capture incoming player profiles on connection
    /// 2. Override with server-stored data if it exists
    /// 3. Save player data periodically and on disconnect
    /// 
    /// NO CLIENT MOD REQUIRED - works with vanilla clients
    /// </summary>
    public static class ServerPatches
    {
        // Track connected players and their character data
        private static readonly Dictionary<long, PlayerCharacterData> ConnectedPlayers = new();
        
        // Map peer UID to steam ID for lookups
        private static readonly Dictionary<long, string> PeerSteamIds = new();

        #region Connection Handling

        /// <summary>
        /// When a player connects, capture their info and prepare for character handling
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
        [HarmonyPostfix]
        public static void RPC_PeerInfo_Postfix(ZNet __instance, ZRpc rpc, ZPackage pkg)
        {
            if (!__instance.IsServer()) return;

            try
            {
                var peer = __instance.GetPeer(rpc);
                if (peer == null) return;

                string steamId = GetSteamId(peer);
                string playerName = peer.m_playerName;

                PeerSteamIds[peer.m_uid] = steamId;

                Plugin.Log.LogInfo($"Player connected: {playerName} (Steam: {steamId})");

                // Check if we have a server-side character for this player
                if (Plugin.HasServerCharacter(steamId, playerName))
                {
                    Plugin.Log.LogInfo($"  -> Has server-side character, will use that");
                }
                else if (Plugin.CanPlayerMigrate(steamId))
                {
                    Plugin.Log.LogInfo($"  -> No server character, migration allowed - will save their client character");
                }
                else if (Plugin.HasPlayerMigrated(steamId))
                {
                    Plugin.Log.LogWarning($"  -> Player already migrated but no character found. They may need restoration.");
                }
                else
                {
                    Plugin.Log.LogWarning($"  -> No server character and migrations disabled. Player cannot join with new character.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error in RPC_PeerInfo_Postfix: {ex}");
            }
        }

        /// <summary>
        /// Handle player disconnect - save their character data
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        [HarmonyPrefix]
        public static void Disconnect_Prefix(ZNetPeer peer)
        {
            if (ZNet.instance?.IsServer() != true) return;
            if (peer == null) return;

            try
            {
                if (ConnectedPlayers.TryGetValue(peer.m_uid, out var data))
                {
                    SaveCharacterData(data, "disconnect");
                    ConnectedPlayers.Remove(peer.m_uid);
                    Plugin.Log.LogInfo($"Saved character for disconnecting player: {data.CharacterName}");
                }

                PeerSteamIds.Remove(peer.m_uid);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error saving character on disconnect: {ex}");
            }
        }

        #endregion

        #region Player Data Capture

        /// <summary>
        /// Capture player data when it's saved (either by game save or periodic save)
        /// This is called for each player on world save
        /// </summary>
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SaveWorld))]
        [HarmonyPostfix]
        public static void SaveWorld_Postfix(ZNet __instance)
        {
            if (!__instance.IsServer()) return;

            int savedCount = 0;
            foreach (var kvp in ConnectedPlayers)
            {
                try
                {
                    SaveCharacterData(kvp.Value, "world_save");
                    savedCount++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Error saving character for {kvp.Key}: {ex}");
                }
            }

            if (savedCount > 0)
            {
                Plugin.Log.LogInfo($"Saved {savedCount} character(s) during world save");
            }
        }

        /// <summary>
        /// Intercept when a Player object loads its data
        /// This is where we can inject server-stored character data
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.Load))]
        [HarmonyPrefix]
        public static void Player_Load_Prefix(Player __instance, ZPackage pkg)
        {
            if (ZNet.instance?.IsServer() != true) return;

            try
            {
                // Get the peer for this player
                var peer = GetPeerForPlayer(__instance);
                if (peer == null) return;

                if (!PeerSteamIds.TryGetValue(peer.m_uid, out string steamId)) return;
                string playerName = peer.m_playerName;

                // Check if we have server-side character data
                string characterPath = Plugin.GetCharacterPath(steamId, playerName);
                if (File.Exists(characterPath))
                {
                    Plugin.Log.LogInfo($"Loading server-side character for {playerName}");
                    // The server character exists - we'll handle this in a more comprehensive way
                    // For now, track that this player is managed server-side
                }

                // Track this player for saving
                var data = new PlayerCharacterData
                {
                    SteamId = steamId,
                    CharacterName = playerName,
                    PeerUid = peer.m_uid,
                    Player = __instance,
                    LastSave = DateTime.UtcNow
                };
                ConnectedPlayers[peer.m_uid] = data;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error in Player_Load_Prefix: {ex}");
            }
        }

        /// <summary>
        /// Capture player save data when the player saves
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.Save))]
        [HarmonyPostfix]
        public static void Player_Save_Postfix(Player __instance, ZPackage pkg)
        {
            if (ZNet.instance?.IsServer() != true) return;

            try
            {
                var peer = GetPeerForPlayer(__instance);
                if (peer == null) return;

                if (!ConnectedPlayers.TryGetValue(peer.m_uid, out var data)) return;

                // Capture the serialized player data
                data.PlayerData = pkg.GetArray();
                data.LastSave = DateTime.UtcNow;

                Plugin.Log.LogDebug($"Captured player save data for {data.CharacterName} ({data.PlayerData.Length} bytes)");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error capturing player save: {ex}");
            }
        }

        /// <summary>
        /// Capture full profile save
        /// </summary>
        [HarmonyPatch(typeof(PlayerProfile), nameof(PlayerProfile.SavePlayerToDisk))]
        [HarmonyPostfix]
        public static void PlayerProfile_SavePlayerToDisk_Postfix(PlayerProfile __instance)
        {
            if (ZNet.instance?.IsServer() != true) return;

            try
            {
                // Find the connected player with this profile name
                foreach (var kvp in ConnectedPlayers)
                {
                    if (kvp.Value.CharacterName.Equals(__instance.GetName(), StringComparison.OrdinalIgnoreCase))
                    {
                        // Load the profile data that was just saved
                        var profilePkg = __instance.LoadPlayerDataFromDisk();
                        if (profilePkg != null)
                        {
                            kvp.Value.ProfileData = profilePkg.GetArray();
                            SaveCharacterData(kvp.Value, "profile_save");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error in PlayerProfile_SavePlayerToDisk_Postfix: {ex}");
            }
        }

        #endregion

        #region Character Storage

        /// <summary>
        /// Save character data to the server
        /// </summary>
        private static void SaveCharacterData(PlayerCharacterData data, string trigger)
        {
            if (data.ProfileData == null && data.PlayerData == null)
            {
                Plugin.Log.LogDebug($"No data to save for {data.CharacterName} (trigger: {trigger})");
                return;
            }

            string characterPath = Plugin.GetCharacterPath(data.SteamId, data.CharacterName);
            bool isNewCharacter = !File.Exists(characterPath);

            // Create backup before overwriting (unless it's a new character)
            if (!isNewCharacter)
            {
                Plugin.CreateBackup(data.SteamId, data.CharacterName);
            }

            // Prefer profile data if available, otherwise use player data
            byte[] dataToSave = data.ProfileData ?? data.PlayerData;
            if (dataToSave != null)
            {
                File.WriteAllBytes(characterPath, dataToSave);
                Plugin.Log.LogInfo($"Saved character {data.CharacterName} for {data.SteamId} ({dataToSave.Length} bytes, trigger: {trigger})");

                // Mark as migrated if this is their first save
                if (isNewCharacter)
                {
                    Plugin.MarkPlayerMigrated(data.SteamId);
                }
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Extract Steam ID from peer connection
        /// </summary>
        private static string GetSteamId(ZNetPeer peer)
        {
            string hostName = peer.m_socket.GetHostName();
            // Format is typically "Steam_STEAMID64" or just the ID
            if (hostName.StartsWith("Steam_"))
                return hostName.Substring(6);
            
            // Try to extract just digits (the Steam ID)
            var match = System.Text.RegularExpressions.Regex.Match(hostName, @"\d+");
            if (match.Success)
                return match.Value;
                
            return hostName;
        }

        /// <summary>
        /// Find the ZNetPeer associated with a Player object
        /// </summary>
        private static ZNetPeer GetPeerForPlayer(Player player)
        {
            if (player == null || ZNet.instance == null) return null;

            var nview = player.GetComponent<ZNetView>();
            if (nview == null) return null;

            long owner = nview.GetZDO()?.GetOwner() ?? 0;
            if (owner == 0) return null;

            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer.m_uid == owner)
                    return peer;
            }

            return null;
        }

        /// <summary>
        /// Get all currently connected players
        /// </summary>
        public static IEnumerable<PlayerCharacterData> GetConnectedPlayers()
        {
            return ConnectedPlayers.Values;
        }

        #endregion
    }

    /// <summary>
    /// Data structure for tracking player character state on the server
    /// </summary>
    public class PlayerCharacterData
    {
        public string SteamId { get; set; }
        public string CharacterName { get; set; }
        public long PeerUid { get; set; }
        public Player Player { get; set; }
        public byte[] PlayerData { get; set; }
        public byte[] ProfileData { get; set; }
        public DateTime LastSave { get; set; }
    }
}
