# FractureCharacters

A server-side only mod for Valheim that stores player characters on the server, preventing item loss from network desync issues and providing centralized character management.

## Features

- **Server-Side Character Storage**: Characters are saved on the server, not client-side
- **No Client Mod Required**: Players don't need to install anything
- **One-Time Migration**: Existing players can migrate their characters once, controlled by admin
- **Automatic Backups**: Configurable number of backups before each save
- **Easy Restore**: Console commands to restore characters from backups
- **Admin Controls**: Full suite of management commands
- **Auto-Update**: Automatically checks for and downloads updates from GitHub

## Installation

1. Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) on your server
2. Copy `FractureCharacters.dll` to `BepInEx/plugins/`
3. Start the server to generate the config file
4. Configure settings in `BepInEx/config/com.fracture.characters.cfg`

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `EnableMod` | `true` | Enable/disable the mod |
| `AllowNewMigrations` | `true` | Allow new players to migrate their client characters |
| `BackupCount` | `5` | Number of backups to keep per character |
| `SaveIntervalSeconds` | `300` | How often to auto-save characters (seconds) |
| `EnableAutoUpdate` | `true` | Enable automatic update checking from GitHub |
| `CheckPreReleases` | `false` | Include pre-release versions when checking for updates |

## How It Works

### Migration System

When a player connects for the first time:
1. If `AllowNewMigrations` is enabled and they haven't migrated before, their client character is saved server-side
2. The player's Steam ID is recorded in `migrated_players.txt`
3. Future logins use the server-side character

This is a **one-time migration per player**. Once migrated, they always use the server character.

### Backup System

Before every save:
1. The current character file is backed up to `backups/{steamid}_{charname}/`
2. Backups are timestamped and rotated (oldest deleted when exceeding `BackupCount`)
3. When restoring, a `.pre_restore` backup is created first

## Console Commands

All commands require admin privileges and are prefixed with `fc_`:

| Command | Description |
|---------|-------------|
| `fc_status` | Show mod status and statistics |
| `fc_list` | List all server-side characters |
| `fc_list_online` | List currently connected players |
| `fc_backups <steamid> <name>` | List available backups for a character |
| `fc_restore <steamid> <name> [index]` | Restore character from backup (index 0 = most recent) |
| `fc_migration <on\|off>` | Enable/disable new player migrations |
| `fc_save` | Force save all connected characters |

### Examples

```
fc_list
fc_backups Steam_12345678 Viking
fc_restore Steam_12345678 Viking 0
fc_migration off
```

## File Locations

| Type | Path |
|------|------|
| Characters | `<SavePath>/characters_server/{steamid}_{charname}.fch` |
| Backups | `<SavePath>/characters_server/backups/{steamid}_{charname}/` |
| Migration Tracking | `<SavePath>/characters_server/migrated_players.txt` |
| Config | `BepInEx/config/com.fracture.characters.cfg` |

## Recommended Workflow

1. **Initial Setup**:
   - Install mod with `AllowNewMigrations = true`
   - Notify players their characters will be migrated on first login
   
2. **After Migration Period**:
   - Set `AllowNewMigrations = false` via console: `fc_migration off`
   - New players will start fresh on the server

3. **If Corruption Occurs**:
   - Check available backups: `fc_backups <steamid> <name>`
   - Restore from backup: `fc_restore <steamid> <name> 0`
   - A pre-restore backup is automatically created

## Why Server-Side Characters?

Client-side character storage can lead to:
- **Item duplication** exploits
- **Item loss** from network desync during death
- **Character rollback** from client crashes
- **Cheating** via save file editing

Server-side storage eliminates these issues by making the server the authoritative source.

## Compatibility

- **Valheim**: 0.221.10+
- **BepInEx**: 5.4.2202+
- **Server Only**: Clients do not need this mod

## Auto-Update System

FractureCharacters includes an automatic update system that:

1. **Checks GitHub** for new releases when the server starts
2. **Downloads updates** automatically in the background
3. **Stages the update** as a `.update` file
4. **Applies on restart** - the new version is loaded when the server restarts

### How It Works

```
Server Start → Check GitHub API → New version? → Download DLL
                                                       ↓
Server Restart → Detect .update file → Backup current → Apply update
```

### Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `EnableAutoUpdate` | `true` | Enable/disable auto-update checking |
| `CheckPreReleases` | `false` | Include alpha/beta/rc versions |

### Manual Update

If you prefer manual updates:
1. Set `EnableAutoUpdate = false` in the config
2. Download releases from the [GitHub Releases](../../releases) page
3. Replace `BepInEx/plugins/FractureCharacters.dll`

### Update Files

| File | Purpose |
|------|---------|
| `FractureCharacters.dll` | Current active mod |
| `FractureCharacters.dll.update` | Downloaded update (waiting for restart) |
| `FractureCharacters.dll.backup` | Backup of previous version |
| `FractureCharacters.dll.old` | Previous version (cleaned up automatically) |

## Version History

### 1.2.0
- Added automatic update system from GitHub
- GitHub Actions workflow for automated releases
- DLL-only and full package releases

### 1.1.0
- Server-side only operation (no client mod required)
- One-time migration system with tracking
- Configurable backup retention
- Admin console commands
- Pre-restore backup safety

## License

MIT License - See LICENSE file

## Credits

- Inspired by [blaxxun's ServerCharacters](https://valheim.thunderstore.io/package/Smoothbrain/ServerCharacters/)
- Built for the Fracture Valheim server
