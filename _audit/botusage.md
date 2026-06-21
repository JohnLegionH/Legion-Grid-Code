# Legion Grid — Persistent Bot System (Bot Usage & Operations)

Phase 34 added persistent bots to Legion Grid. Bots created by Phlox scripts can now survive region restarts: their configuration, position, appearance, and custom script data are stored in SQLite and auto-respawned when the region loads. This document covers what the system does, how it's run, the script API, configuration, and the safety/security model.

---

## 1. Overview

Without persistence, every bot vanishes on region restart, which is a real usability problem on a live grid. The persistent bot system solves this by:

- Storing each persistent bot's full record (owner, parcel, position, rotation, appearance, tags, custom data, TTL) in a local SQLite database.
- Re-spawning persistent bots automatically during region startup, with permission re-checks and staggered (throttled) spawning so startup isn't overwhelmed.
- Letting scripts reconnect to their respawned bots via `botGetAllMyBotsInRegion`.
- Enforcing layered safety controls so a non-owner can't create persistent bots and a region can't be flooded with them.

The feature is self-contained: no viewer-protocol changes and no physics-engine changes are required.

---

## 2. Code Layout

### New file

| File | Location | Purpose |
|------|----------|---------|
| `BotPersistenceManager.cs` | `Region/OptionalModules/World/NPC` | Core persistence logic: SQLite access, staggered respawn, cleanup, capacity enforcement, admin commands |

### Modified files

| File | Location | Change |
|------|----------|--------|
| `BotManager.cs` | `Region/OptionalModules/World/NPC` | Added `PersistenceManager` field, lifecycle hooks (spawn/despawn), console commands |
| `LSLSystemAPI.cs` | `Phlox.ScriptEngine` | 5 new bot persistence functions (667–671) + `GetBotPersistence()` helper |
| `ISystemAPI.cs` | `InWorldz.Phlox/Glue` | 5 new interface declarations (667–671) |
| `SyscallShim.cs` | `InWorldz.Phlox/Glue` | 5 shim methods + 5 syscall table entries (667–671) |
| `RegionSettings.cs` | (region framework) | Added `AllowBotPersistence` boolean field |
| `EstateManagementModule.cs` | (estate tools) | Exposes the region-level persistence toggle |
| `OpenSim.Region.OptionalModules.csproj` | `Region/OptionalModules` | Added `BotPersistenceManager.cs` compile entry + `Microsoft.Data.Sqlite` package |
| `Phlox.ScriptEngine.csproj` | `Addons/Phlox/Phlox.ScriptEngine` | Updated `Microsoft.Data.Sqlite` to 10.0.7, added OptionalModules project reference |

> Note: `AvatarFactoryModule.cs` was also changed in this session (added `SaveBakedTextures(id)` in `SaveAppearance()` to fix the avatar "white cloud" bug). That fix is unrelated to bots but shipped at the same time.

### Storage backend

- **Engine:** SQLite via `Microsoft.Data.Sqlite` (preferred for .NET 8).
- **Mode:** WAL (Write-Ahead Logging) for concurrent read/write safety.
- **DB file:** `botpersistence.db` in the `bin/` folder (configurable).
- **Size:** ~50 bots with appearance data is typically under 1 MB on disk.

---

## 3. Script API (Phlox functions 667–671)

| Index | Function | Returns | Description |
|-------|----------|---------|-------------|
| 667 | `botSetPersistent(string botID, int ttl)` | int | Marks a bot persistent. `ttl` = time-to-live in seconds (overrides `DefaultTTL`; 0 = no expiry). |
| 668 | `botRemovePersistent(string botID)` | int | Removes persistence from a bot. |
| 669 | `botIsPersistent(string botID)` | int | Returns `1` if the bot is persistent, `0` otherwise. |
| 670 | `botGetPersistentData(string botID, string key)` | string | Reads a custom key/value entry stored with the bot. |
| 671 | `botSetPersistentData(string botID, string key, string value)` | int | Writes a custom key/value entry that survives restarts. |

Functions returning `int` return `0` (OK) on success or a negative error code (see §6).

Scripts reconnect to respawned bots after a restart with `botGetAllMyBotsInRegion`.

---

## 4. Configuration

Configuration lives in **`bin/config/BotPersistence.ini`** as a `[BotPersistence]` section. This separate file in the `config/` folder (which loads last) is used deliberately instead of editing `OpenSim.ini` directly, to avoid the risk of an `OpenSim.ini` overwrite wiping the settings.

```ini
[BotPersistence]
    ;; Master switch — grid operators must opt in.
    ;; When false, all persistence features are completely disabled.
    Enabled = true

    ;; Database file path (relative to bin/)
    DatabaseFile = "botpersistence.db"

    ;; Capacity limits
    MaxPerParcel = 10
    MaxPerOwner  = 15
    MaxPerRegion = 50

    ;; Whether persistent bots count against the region's MaxAgents limit.
    ;;   false = shared pool (bots reduce avatar capacity)
    ;;   true  = separate pool (bots use their own MaxPerRegion cap)
    SeparateAgentPool = false

    ;; Default time-to-live in seconds (0 = no expiry).
    ;; Scripts can override per-bot via botSetPersistent.
    DefaultTTL = 604800        ;; 7 days

    ;; How often to save bot positions to SQLite (seconds)
    PositionSaveInterval = 60

    ;; How often to run the expiration/orphan cleanup (seconds)
    CleanupInterval = 21600    ;; 6 hours

    ;; Staggered spawning: how many bots to respawn per second on region load.
    ;; Lower = less startup impact, slower full population.
    RespawnRate = 2
```

---

## 5. Runtime Lifecycle (How It Runs)

**On first run:**
- SQLite database is created and initialized with WAL mode; schema is created automatically.

**During normal operation:**
- A position-save timer writes bot positions to SQLite every `PositionSaveInterval` seconds using batched transactions, plus a save on region shutdown.
- A cleanup pass runs every `CleanupInterval` seconds: expires TTL'd bots, detects orphans, and re-checks permissions.

**On region startup (respawn):**
1. `LoadPersistentBots()` runs after parcels are loaded and after connections are accepted.
2. A cleanup pass runs first (expiration, orphan detection, permission re-check).
3. Bots are spawned in a staggered fashion throttled by `RespawnRate`, with avatar-login given priority over bot respawn.
4. Appearance is restored from persisted data using dirty-flag updates only.

**Admin / console commands:**
- `list persistent bots` — show persistent bots known to the region.
- `clear persistent bots` — remove persistent bot records.
- Region-level toggle is also exposed through the Estate tools (`AllowBotPersistence`).

---

## 6. Error Codes

Returned by the `int`-returning script functions (`BotPersistError`):

| Code | Name | Meaning |
|------|------|---------|
| `0`  | OK | Success |
| `-1` | NOT_FOUND | Bot ID not found |
| `-2` | NO_PERMISSION | Caller lacks rights for the operation |
| `-3` | PARCEL_CAP | Per-parcel limit reached |
| `-4` | OWNER_CAP | Per-owner limit reached |
| `-5` | REGION_CAP | Per-region limit reached |
| `-6` | DISABLED | Persistence is disabled (master switch off / no config section) |
| `-7` | ALREADY | Bot is already in the requested state |

> Operational note: `-6` (DISABLED) was hit during initial testing because the `[BotPersistence]` section wasn't being read at boot even though it existed. If you see `-6` with a valid config present, confirm the config file is being loaded and the manager initialized (`No [BotPersistence] section in config - disabled` in the startup log is the tell).

---

## 7. Safety & Security Model

The design assumes bots consume resources and that the persist API is reachable by any script, so multiple independent layers gate it.

### 7.1 Master and region-level enable switches
- **Grid-level master switch** (`Enabled`): when `false`, all persistence features are completely off. Operators must explicitly opt in.
- **Region-level toggle** (`AllowBotPersistence` in `RegionSettings`, exposed in Estate tools): lets an estate manager allow or deny persistence per region even when the grid switch is on.

### 7.2 Ownership and permission checks
- A bot can only be made persistent by its **script owner**, and only if that owner has **rez rights** on the parcel where the bot lives.
- **On respawn after restart**, the system re-verifies that the original owner still has parcel rights before recreating the bot.
- If ownership or permissions have changed (parcel sold, owner banned, etc.), the bot record is **marked inactive (`active = 0`) rather than deleted**, so the owner can investigate instead of silently losing data.
- If an owner is banned from the region, their persistent bots in that region are marked inactive.

### 7.3 Capacity caps (anti-flood)
Three configurable, independently enforced limits prevent both accidental and intentional resource exhaustion:

| Limit | Scope | Default | Config key |
|-------|-------|---------|-----------|
| Per-parcel | Max persistent bots on one parcel | 10 | `MaxPerParcel` |
| Per-owner | Max persistent bots per owner per region | 15 | `MaxPerOwner` |
| Per-region | Max total persistent bots in the region | 50 | `MaxPerRegion` |

A persist request that would exceed any cap fails with the matching error code (`-3`/`-4`/`-5`).

### 7.4 Agent pool isolation
- `SeparateAgentPool` controls whether persistent bots count against the region's `MaxAgents` (shared pool) or use their own `MaxPerRegion` budget (separate pool), so bot population can be prevented from eating real avatar capacity.

### 7.5 Expiration and cleanup
- `DefaultTTL` (7 days default) bounds how long a bot persists unless a script overrides it; `0` means no expiry.
- The periodic cleanup pass removes expired and orphaned records, keeping the database from accumulating stale entries.

### 7.6 Startup protection
- Staggered respawn (`RespawnRate`, default 2/sec) with avatar-login priority prevents a large bot population from overwhelming region startup or delaying real users logging in.

---

## 8. Deployment Notes

- **DLLs to deploy together:** `OpenSim.Region.OptionalModules.dll`, `Phlox.ScriptEngine.dll`, and `InWorldz.Phlox.dll` must be copied as a set to the live `bin/` folder.
- **NuGet runtime DLLs:** if SQLite fails to load at runtime, manually copy `Microsoft.Data.Sqlite` and the `SQLitePCLRaw` DLLs from the NuGet cache into the live `bin/` folder.
- **Config placement:** keep the `[BotPersistence]` section in `bin/config/BotPersistence.ini` (loads last) rather than in `OpenSim.ini`.
- **Standard build rule still applies:** copy `OpenMetaverse*.dll` from the webrtc bin into the source bin before building.

---

## 9. Test Procedure (Survive-Restart)

A touch-driven test object was used to validate end-to-end:

1. Touch → script creates a bot (`OnRezScript` / listen handles register).
2. Touch again → script finds the bot's UUID and calls `botSetPersistent(botID, 3600)` (1-hour TTL).
3. Confirm the call returns `0` (OK). (A `-6` here means persistence is disabled — fix config and retry from step 1.)
4. Restart the region.
5. Confirm the bot respawns automatically with correct **position, appearance, and custom data**, and that the script can re-find it via `botGetAllMyBotsInRegion`.

This survive-restart test passed: bots respawned with correct position, appearance, and persisted custom data.
