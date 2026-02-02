using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;

namespace FractureCharacters
{
    /// <summary>
    /// Auto-updater for FractureCharacters mod.
    /// 
    /// Checks GitHub releases for new versions and downloads updates automatically.
    /// Updates are staged and applied on next server restart (can't hot-reload assemblies).
    /// 
    /// The updater:
    /// 1. Checks GitHub API for latest release on mod load
    /// 2. Compares version numbers
    /// 3. Downloads new DLL to .update file
    /// 4. On next load, detects .update file and replaces current DLL
    /// </summary>
    public class AutoUpdater
    {
        // GitHub repository info - UPDATE THESE FOR YOUR REPO
        private const string GITHUB_OWNER = "YourGitHubUsername";  // TODO: Update this
        private const string GITHUB_REPO = "Valheim";              // TODO: Update this
        private const string GITHUB_API_BASE = "https://api.github.com";
        private const string GITHUB_RELEASES_LATEST = "/repos/{0}/{1}/releases/latest";
        
        private readonly string _currentVersion;
        private readonly string _pluginPath;
        private readonly string _updatePath;
        private readonly string _backupPath;
        
        public ConfigEntry<bool> EnableAutoUpdate;
        public ConfigEntry<bool> CheckPreReleases;
        public ConfigEntry<bool> AutoRestartOnUpdate;
        public ConfigEntry<int> RestartDelaySeconds;
        
        private static readonly HttpClient _httpClient = new HttpClient();
        
        public AutoUpdater(string currentVersion, string pluginPath, ConfigFile config)
        {
            _currentVersion = currentVersion;
            _pluginPath = pluginPath;
            _updatePath = pluginPath + ".update";
            _backupPath = pluginPath + ".backup";
            
            // Configure HTTP client for GitHub API
            _httpClient.DefaultRequestHeaders.Add("User-Agent", $"FractureCharacters/{currentVersion}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            
            // Config entries
            EnableAutoUpdate = config.Bind(
                "AutoUpdate",
                "EnableAutoUpdate",
                true,
                "Enable automatic update checking and downloading from GitHub"
            );
            
            CheckPreReleases = config.Bind(
                "AutoUpdate",
                "CheckPreReleases",
                false,
                "Include pre-release versions (alpha, beta, rc) when checking for updates"
            );
            
            AutoRestartOnUpdate = config.Bind(
                "AutoUpdate",
                "AutoRestartOnUpdate",
                true,
                "Automatically restart the server after downloading an update. " +
                "Requires a service manager or crash handler to restart the server."
            );
            
            RestartDelaySeconds = config.Bind(
                "AutoUpdate",
                "RestartDelaySeconds",
                5,
                "Seconds to wait before restarting after update download (gives time to save)"
            );
        }
        
        /// <summary>
        /// Apply any pending updates (call this early in plugin Awake)
        /// </summary>
        public bool ApplyPendingUpdate()
        {
            try
            {
                if (File.Exists(_updatePath))
                {
                    Plugin.Log.LogInfo("Pending update found, applying...");
                    
                    // Backup current DLL
                    if (File.Exists(_pluginPath))
                    {
                        if (File.Exists(_backupPath))
                            File.Delete(_backupPath);
                        File.Copy(_pluginPath, _backupPath);
                    }
                    
                    // We can't replace a loaded DLL, but we can rename it
                    // The new DLL will be loaded on next restart
                    string oldPath = _pluginPath + ".old";
                    if (File.Exists(oldPath))
                        File.Delete(oldPath);
                    
                    // Rename current to .old (will be cleaned up next time)
                    if (File.Exists(_pluginPath))
                        File.Move(_pluginPath, oldPath);
                    
                    // Move update to current
                    File.Move(_updatePath, _pluginPath);
                    
                    Plugin.Log.LogWarning("===========================================");
                    Plugin.Log.LogWarning("UPDATE APPLIED! Please restart the server");
                    Plugin.Log.LogWarning("to load the new version of FractureCharacters");
                    Plugin.Log.LogWarning("===========================================");
                    
                    return true;
                }
                
                // Clean up old files from previous updates
                string oldDll = _pluginPath + ".old";
                if (File.Exists(oldDll))
                {
                    try { File.Delete(oldDll); }
                    catch { /* Ignore - still in use */ }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to apply pending update: {ex.Message}");
            }
            
            return false;
        }
        
        /// <summary>
        /// Check for updates asynchronously (non-blocking)
        /// </summary>
        public async void CheckForUpdatesAsync()
        {
            if (!EnableAutoUpdate.Value)
            {
                Plugin.Log.LogInfo("Auto-update is disabled");
                return;
            }
            
            try
            {
                Plugin.Log.LogInfo("Checking for updates...");
                
                var latestRelease = await GetLatestReleaseAsync();
                if (latestRelease == null)
                {
                    Plugin.Log.LogInfo("No releases found or unable to check");
                    return;
                }
                
                string latestVersion = latestRelease.Version;
                Plugin.Log.LogInfo($"Current version: {_currentVersion}, Latest: {latestVersion}");
                
                if (IsNewerVersion(latestVersion, _currentVersion))
                {
                    Plugin.Log.LogWarning($"New version available: {latestVersion}");
                    Plugin.Log.LogInfo($"Release URL: {latestRelease.ReleaseUrl}");
                    
                    // Download the update
                    if (!string.IsNullOrEmpty(latestRelease.DownloadUrl))
                    {
                        await DownloadUpdateAsync(latestRelease.DownloadUrl, latestVersion);
                    }
                }
                else
                {
                    Plugin.Log.LogInfo("FractureCharacters is up to date");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error checking for updates: {ex.Message}");
            }
        }
        
        private async Task<ReleaseInfo> GetLatestReleaseAsync()
        {
            try
            {
                string url = GITHUB_API_BASE + string.Format(GITHUB_RELEASES_LATEST, GITHUB_OWNER, GITHUB_REPO);
                
                var response = await _httpClient.GetAsync(url);
                
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Plugin.Log.LogInfo("No releases found on GitHub");
                    return null;
                }
                
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync();
                
                // Simple JSON parsing without external dependencies
                return ParseReleaseJson(json);
            }
            catch (HttpRequestException ex)
            {
                Plugin.Log.LogWarning($"Failed to fetch release info: {ex.Message}");
                return null;
            }
        }
        
        private ReleaseInfo ParseReleaseJson(string json)
        {
            // Simple regex-based JSON parsing (no external dependencies)
            var info = new ReleaseInfo();
            
            // Extract tag_name (version)
            var tagMatch = Regex.Match(json, @"""tag_name""\s*:\s*""([^""]+)""");
            if (tagMatch.Success)
            {
                info.Version = tagMatch.Groups[1].Value.TrimStart('v');
                info.Tag = tagMatch.Groups[1].Value;
            }
            
            // Extract html_url (release page)
            var urlMatch = Regex.Match(json, @"""html_url""\s*:\s*""([^""]+)""");
            if (urlMatch.Success)
            {
                info.ReleaseUrl = urlMatch.Groups[1].Value;
            }
            
            // Extract prerelease flag
            var prereleaseMatch = Regex.Match(json, @"""prerelease""\s*:\s*(true|false)");
            if (prereleaseMatch.Success)
            {
                info.IsPreRelease = prereleaseMatch.Groups[1].Value == "true";
            }
            
            // Skip pre-releases if not configured to check them
            if (info.IsPreRelease && !CheckPreReleases.Value)
            {
                Plugin.Log.LogInfo($"Skipping pre-release {info.Version}");
                return null;
            }
            
            // Find the DLL-only zip asset
            var assetsMatch = Regex.Match(json, @"""assets""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (assetsMatch.Success)
            {
                string assets = assetsMatch.Groups[1].Value;
                
                // Look for dll-only zip first, then fall back to full package
                var dllOnlyMatch = Regex.Match(assets, @"""browser_download_url""\s*:\s*""([^""]*dll-only[^""]*\.zip)""");
                if (dllOnlyMatch.Success)
                {
                    info.DownloadUrl = dllOnlyMatch.Groups[1].Value;
                }
                else
                {
                    // Fall back to any FractureCharacters zip
                    var anyZipMatch = Regex.Match(assets, @"""browser_download_url""\s*:\s*""([^""]*FractureCharacters[^""]*\.zip)""");
                    if (anyZipMatch.Success)
                    {
                        info.DownloadUrl = anyZipMatch.Groups[1].Value;
                    }
                }
            }
            
            return info;
        }
        
        private async Task DownloadUpdateAsync(string downloadUrl, string version)
        {
            try
            {
                Plugin.Log.LogInfo($"Downloading update from: {downloadUrl}");
                
                // Download to temp file
                string tempZip = Path.GetTempFileName();
                
                using (var response = await _httpClient.GetAsync(downloadUrl))
                {
                    response.EnsureSuccessStatusCode();
                    
                    using (var fs = new FileStream(tempZip, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }
                
                Plugin.Log.LogInfo("Download complete, extracting...");
                
                // Extract DLL from zip
                string tempExtract = Path.Combine(Path.GetTempPath(), "FractureCharacters_update");
                if (Directory.Exists(tempExtract))
                    Directory.Delete(tempExtract, true);
                
                ZipFile.ExtractToDirectory(tempZip, tempExtract);
                
                // Find the DLL (might be in root or BepInEx/plugins subfolder)
                string dllPath = FindDllInDirectory(tempExtract);
                
                if (dllPath != null)
                {
                    // Copy to update path
                    File.Copy(dllPath, _updatePath, true);
                    
                    Plugin.Log.LogWarning("===========================================");
                    Plugin.Log.LogWarning($"UPDATE DOWNLOADED: v{version}");
                    Plugin.Log.LogWarning("===========================================");
                    
                    // Trigger restart if enabled
                    if (AutoRestartOnUpdate.Value)
                    {
                        TriggerServerRestart();
                    }
                    else
                    {
                        Plugin.Log.LogWarning("Restart the server to apply the update");
                    }
                }
                else
                {
                    Plugin.Log.LogError("Could not find FractureCharacters.dll in update package");
                }
                
                // Cleanup
                try
                {
                    File.Delete(tempZip);
                    Directory.Delete(tempExtract, true);
                }
                catch { /* Ignore cleanup errors */ }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to download update: {ex.Message}");
            }
        }
        
        private async void TriggerServerRestart()
        {
            int delay = RestartDelaySeconds.Value;
            
            Plugin.Log.LogWarning("\n");
            Plugin.Log.LogWarning("============================================================");
            Plugin.Log.LogWarning("  FRACTURECHARACTERS AUTO-UPDATE");
            Plugin.Log.LogWarning("============================================================");
            Plugin.Log.LogWarning($"  A new version has been downloaded and staged.");
            Plugin.Log.LogWarning($"  Server will SHUT DOWN in {delay} seconds to apply the update.");
            Plugin.Log.LogWarning("");
            Plugin.Log.LogWarning("  THIS IS NORMAL BEHAVIOR - NOT A CRASH!");
            Plugin.Log.LogWarning("");
            Plugin.Log.LogWarning("  If you're running the server manually, just restart it.");
            Plugin.Log.LogWarning("  If using a service/crash handler, it will restart automatically.");
            Plugin.Log.LogWarning("============================================================");
            Plugin.Log.LogWarning("\n");
            
            // Broadcast warning to players if possible
            try
            {
                if (ZNet.instance != null)
                {
                    // Send message to all connected players
                    ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", 
                        (int)MessageHud.MessageType.Center, 
                        $"Server restarting for update in {delay} seconds!");
                }
            }
            catch { /* Ignore if messaging fails */ }
            
            // Wait before restart
            await Task.Delay(delay * 1000);
            
            // Save world before quitting
            try
            {
                Plugin.Log.LogInfo("[AUTO-UPDATE] Saving world before shutdown...");
                if (ZNet.instance != null)
                {
                    ZNet.instance.SaveWorld(true);
                    Plugin.Log.LogInfo("[AUTO-UPDATE] World saved successfully.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AUTO-UPDATE] Failed to save world: {ex.Message}");
            }
            
            Plugin.Log.LogWarning("\n");
            Plugin.Log.LogWarning("============================================================");
            Plugin.Log.LogWarning("  SHUTTING DOWN FOR UPDATE - THIS IS NOT A CRASH!");
            Plugin.Log.LogWarning("  ");
            Plugin.Log.LogWarning("  The FractureCharacters mod downloaded an update.");
            Plugin.Log.LogWarning("  Restart the server to load the new version.");
            Plugin.Log.LogWarning("============================================================");
            Plugin.Log.LogWarning("\n");
            
            // Quit the application - service manager/crash handler will restart
            UnityEngine.Application.Quit();
        }
        
        private string FindDllInDirectory(string directory)
        {
            // Check root
            string rootDll = Path.Combine(directory, "FractureCharacters.dll");
            if (File.Exists(rootDll))
                return rootDll;
            
            // Check BepInEx/plugins
            string pluginsDll = Path.Combine(directory, "BepInEx", "plugins", "FractureCharacters.dll");
            if (File.Exists(pluginsDll))
                return pluginsDll;
            
            // Recursive search
            foreach (var file in Directory.GetFiles(directory, "FractureCharacters.dll", SearchOption.AllDirectories))
            {
                return file;
            }
            
            return null;
        }
        
        private bool IsNewerVersion(string latest, string current)
        {
            try
            {
                // Clean version strings
                latest = latest.TrimStart('v').Split('-')[0];
                current = current.TrimStart('v').Split('-')[0];
                
                var latestParts = latest.Split('.');
                var currentParts = current.Split('.');
                
                int maxParts = Math.Max(latestParts.Length, currentParts.Length);
                
                for (int i = 0; i < maxParts; i++)
                {
                    int latestNum = i < latestParts.Length ? int.Parse(latestParts[i]) : 0;
                    int currentNum = i < currentParts.Length ? int.Parse(currentParts[i]) : 0;
                    
                    if (latestNum > currentNum)
                        return true;
                    if (latestNum < currentNum)
                        return false;
                }
                
                return false; // Versions are equal
            }
            catch
            {
                // If parsing fails, assume no update needed
                return false;
            }
        }
        
        private class ReleaseInfo
        {
            public string Version { get; set; }
            public string Tag { get; set; }
            public string DownloadUrl { get; set; }
            public string ReleaseUrl { get; set; }
            public bool IsPreRelease { get; set; }
        }
    }
}
