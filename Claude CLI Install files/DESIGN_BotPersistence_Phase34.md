# Design Document — Bot Persistence (Phase 34)

## Overview

Persistent bots survive region restarts by storing their configuration in SQLite and auto-respawning on region load via BotManager. This document covers the security model, data schema, lifecycle management, configuration, and implementation plan.

---

## 1. Security Model

### 1.1 Ownership and Permission Checks

Every persistent bot has a single **owner** — the UUID of the avatar whose script created it. All permission checks flow from this owner identity.

**On creation (persist request):**

- Script owner must have **rez rights** on the target parcel (owner, group member with rez, or parcel allows everyone to rez).
- Script owner must not be **banned** from the parcel or region.
- The requesting script must hold `PERMISSION_TRIGGER_ANIMATION` or an equivalent bot permission grant (consistent with existing BotManager permission checks).

**On respawn (region load):**

- BotManager re-checks the owner's parcel permissions before spawning. If any check fails, the bot entry is marked `active = 0` in SQLite — not deleted — so the owner can investigate.
- If the parcel has changed ownership since the bot was persisted, the bot does not respawn.
- If the owner is banned from the region, all their persistent bots in that region are marked inactive.

**On parcel transfer:**

- A background check on parcel ownership changes should mark affected bots inactive. This can run as part of the region load sequence rather than a real-time event hook, keeping complexity low.

### 1.2 Enable/Disable Controls

Bot persistence can be turned off at two levels:

**Grid level (OpenSim.ini):** `BotPersistence.Enabled = false` disables the feature entirely. The SQLite database is never opened, persist requests return `BOT_PERSIST_DISABLED`, and no respawn logic runs. This is the default — grid operators must opt in.

**Region level (in-world):** Region owners can toggle persistence for their region without console access. A new boolean `AllowBotPersistence` is stored in `RegionSettings` (same table that holds terrain textures, covenant, etc.) and exposed through the existing Estate tools panel. When disabled at the region level:

- New persist requests return `BOT_PERSIST_DISABLED`.
- Existing persistent bots are **not** automatically deactivated — they simply won't respawn on the next restart. This avoids data loss if the region owner is just temporarily disabling the feature.
- If re-enabled, all previously active entries respawn normally on the next region load.

The effective state is: persistence works only if **both** grid-level `Enabled = true` **and** region-level `AllowBotPersistence = true`. Grid-level off overrides everything.

| Level | Control | Who Sets It | Default |
|-------|---------|-------------|---------|
| Grid | `[BotPersistence] Enabled` in OpenSim.ini | Grid operator | `false` |
| Region | `AllowBotPersistence` in RegionSettings | Region owner (via Estate tools) | `true` |

### 1.3 Capacity Limits

Three layers of configurable caps prevent resource exhaustion:

| Limit | Scope | Default | Config Key |
|-------|-------|---------|------------|
| Per-parcel cap | Max persistent bots on a single parcel | 10 | `BotPersistence.MaxPerParcel` |
| Per-owner cap | Max persistent bots per owner per region | 15 | `BotPersistence.MaxPerOwner` |
| Per-region cap | Max total persistent bots in the region | 50 | `BotPersistence.MaxPerRegion` |

When a persist request would exceed any cap, the request fails and the script receives an error (e.g., via `bot_error` event or a return code). The bot continues to exist as a non-persistent bot — only the persistence is denied.

**Agent limit interaction:** Persistent bots each consume a `ScenePresence` slot. By default, they count against the region's `MaxAgents` limit. An optional config flag `BotPersistence.SeparateAgentPool` (default `false`) allows operators to give bots their own pool:

- `false` — bots + real avatars share the `MaxAgents` cap. If 20 bots are persistent and `MaxAgents = 40`, only 20 real avatars can connect.
- `true` — bots draw from their own `MaxPerRegion` cap and do not reduce avatar slots. This is better for regions designed around NPC populations but uses more memory.

### 1.4 Admin Controls

Estate managers and region owners need the ability to intervene without depending on individual bot owners.

**Console commands:**

| Command | Effect |
|---------|--------|
| `clear persistent bots` | Deactivates all persistent bots in the region |
| `clear persistent bots <owner_uuid>` | Deactivates all persistent bots for a specific owner |
| `clear persistent bots parcel <parcel_name>` | Deactivates all persistent bots on a specific parcel |
| `list persistent bots` | Lists all active persistent bot entries with owner, parcel, position |

**In-world functions (estate manager only):**

| Function | Description |
|----------|-------------|
| `osBotClearPersistent(key botID)` | Deactivates a specific persistent bot |
| `osBotClearAllPersistent()` | Deactivates all persistent bots in the region |

These functions require `PERMISSION_ESTATE` or equivalent estate-level trust.

**Deactivation vs deletion:** Admin actions set `active = 0` rather than deleting rows. This preserves an audit trail and lets owners see that their bots were administratively disabled rather than mysteriously vanishing.

### 1.5 Expiration and Heartbeat

Persistent bots should not live forever without supervision.

**TTL (Time To Live):**

- Every persistent bot has an `expires_at` timestamp, set at creation time.
- Default TTL is configurable: `BotPersistence.DefaultTTL` (default: 7 days, `0` = no expiry).
- Scripts can set a custom TTL at persist time: `botSetPersistent(botID, ttl_seconds)`.
- On region load, expired bots are marked inactive and not respawned.

**Script heartbeat:**

- A persistent bot's `expires_at` is refreshed each time the owning script interacts with it (movement commands, chat, animation, etc.). This acts as a natural heartbeat — actively used bots stay alive.
- If the owning object (containing the script) no longer exists in the region, the bot is marked inactive on the next region load. This prevents orphaned bots from scripts that were deleted.

**Cleanup cycle:**

- On region startup, BotManager runs a cleanup pass: mark expired bots inactive, check for orphaned scripts, verify parcel permissions, then spawn valid bots.
- A periodic timer (configurable, default every 6 hours) re-checks expiration and orphan status for bots that were spawned before the region's current uptime.

---

## 2. Data Schema

### 2.1 SQLite Database

File location: `bin/botpersistence.db` (alongside the region's other data files).

One database per region instance. If a simulator hosts multiple regions, each region gets its own database file: `botpersistence_{region_uuid}.db`.

### 2.2 Table: `persistent_bots`

```sql
CREATE TABLE persistent_bots (
    bot_id          TEXT PRIMARY KEY,       -- UUID of the bot (ScenePresence UUID)
    owner_id        TEXT NOT NULL,          -- UUID of the owning avatar
    creator_script  TEXT NOT NULL,          -- UUID of the script that created the bot
    creator_object  TEXT NOT NULL,          -- UUID of the object containing the script
    region_id       TEXT NOT NULL,          -- UUID of the region
    parcel_id       TEXT NOT NULL,          -- UUID of the parcel at creation time

    -- Identity
    bot_name        TEXT NOT NULL,          -- Display name (first + last)
    bot_first_name  TEXT NOT NULL,          -- First name
    bot_last_name   TEXT NOT NULL,          -- Last name

    -- Position and state
    position_x      REAL NOT NULL,          -- Last known X position
    position_y      REAL NOT NULL,          -- Last known Y position
    position_z      REAL NOT NULL,          -- Last known Z position
    rotation_x      REAL NOT NULL DEFAULT 0,
    rotation_y      REAL NOT NULL DEFAULT 0,
    rotation_z      REAL NOT NULL DEFAULT 0,
    rotation_w      REAL NOT NULL DEFAULT 1,

    -- Appearance
    appearance_data BLOB,                   -- Serialized appearance (AvatarAppearance)

    -- Tags and metadata
    tags            TEXT,                   -- JSON array of string tags for script queries
    custom_data     TEXT,                   -- JSON object for script-defined key-value data

    -- Lifecycle
    active          INTEGER NOT NULL DEFAULT 1,  -- 1 = will respawn, 0 = deactivated
    deactivation_reason TEXT,              -- Why it was deactivated (expired, admin, parcel_change, etc.)
    created_at      TEXT NOT NULL,          -- ISO 8601 timestamp
    updated_at      TEXT NOT NULL,          -- ISO 8601, refreshed on any interaction
    expires_at      TEXT,                   -- ISO 8601, NULL = no expiry

    -- Indexes for common queries
    UNIQUE(bot_id, region_id)
);

CREATE INDEX idx_pb_owner ON persistent_bots(owner_id);
CREATE INDEX idx_pb_region ON persistent_bots(region_id, active);
CREATE INDEX idx_pb_parcel ON persistent_bots(parcel_id, active);
CREATE INDEX idx_pb_expires ON persistent_bots(expires_at) WHERE active = 1;
```

### 2.3 Position Updates

Bot positions are updated in SQLite periodically (configurable, default every 60 seconds) rather than on every frame, to avoid I/O overhead. A final save occurs during region shutdown to capture the most recent state.

---

## 3. Lifecycle

### 3.1 Creation Flow

```
Script calls botSetPersistent(botID, ttl)
    │
    ├── Validate: bot exists and is owned by script owner
    ├── Validate: owner has rez rights on current parcel
    ├── Validate: per-parcel cap not exceeded
    ├── Validate: per-owner cap not exceeded
    ├── Validate: per-region cap not exceeded
    │
    ├── On failure: return error code, bot remains non-persistent
    │
    └── On success:
        ├── Insert row into persistent_bots
        ├── Set expires_at = now + ttl (or NULL if ttl = 0)
        └── Return success
```

### 3.2 Region Shutdown

```
Region shutting down
    │
    └── BotManager.SavePersistentBots()
        ├── For each active persistent bot:
        │   ├── Save current position/rotation
        │   ├── Save current appearance (if changed)
        │   └── Update updated_at timestamp
        └── Close SQLite connection
```

### 3.3 Region Startup (Respawn)

```
Region loaded, parcels initialized, connections accepted
    │
    └── BotManager.LoadPersistentBots()
        │
        ├── Check: grid-level Enabled AND region-level AllowBotPersistence
        │   └── If either is off → skip entirely, log "Bot persistence disabled"
        │
        ├── Cleanup pass (immediate, sub-millisecond):
        │   ├── Mark expired bots inactive (reason: "expired")
        │   └── Mark bots with missing creator objects inactive (reason: "orphaned")
        │
        ├── Validation pass (immediate):
        │   ├── Re-check: owner has rez rights on parcel → if not, deactivate (reason: "permission_denied")
        │   ├── Re-check: owner not banned → if banned, deactivate (reason: "owner_banned")
        │   └── Re-check: region cap not exceeded → if exceeded, deactivate oldest first (reason: "cap_exceeded")
        │
        ├── Build spawn queue of validated bots
        │
        └── Staggered spawn timer (RespawnRate bots/sec):
            ├── If avatar login in progress → pause spawning until login completes
            ├── Create ScenePresence with saved appearance
            ├── Place at saved position/rotation
            ├── Restore tags and custom_data
            ├── Register in BotManager's active bot dictionary
            └── When queue empty → log "Respawned X of Y persistent bots, Z deactivated"
```

### 3.4 Script Reconnection

After a region restart, scripts need to find their bots again. The existing `botGetAllMyBotsInRegion` function already returns bots owned by the calling script's owner. Persistent bots that were respawned will appear in this list, so scripts can reconnect without any new API.

Additional helper:

| Function | Description |
|----------|-------------|
| `botIsPersistent(key botID)` | Returns 1 if the bot has an active persistence entry |
| `botSetPersistent(key botID, integer ttl)` | Persists a bot with the given TTL in seconds (0 = use default) |
| `botRemovePersistent(key botID)` | Removes persistence (bot continues as non-persistent) |
| `botGetPersistentData(key botID, string key)` | Reads a value from the bot's custom_data JSON |
| `botSetPersistentData(key botID, string key, string value)` | Writes a value to the bot's custom_data JSON |

These would be registered in the Phlox function table starting at index 667.

---

## 4. Configuration

All settings live in `OpenSim.ini` under a new `[BotPersistence]` section:

```ini
[BotPersistence]
    ;; Master switch — grid operators must opt in
    ;; When false, all persistence features are completely disabled
    Enabled = false

    ;; Database file path (relative to bin/)
    DatabaseFile = "botpersistence.db"

    ;; Capacity limits
    MaxPerParcel = 10
    MaxPerOwner = 15
    MaxPerRegion = 50

    ;; Whether persistent bots count against the region's MaxAgents limit
    ;; false = shared pool (bots reduce avatar capacity)
    ;; true = separate pool (bots use their own MaxPerRegion cap)
    SeparateAgentPool = false

    ;; Default time-to-live in seconds (0 = no expiry)
    ;; Scripts can override per-bot
    DefaultTTL = 604800  ;; 7 days

    ;; How often to save bot positions to SQLite (seconds)
    PositionSaveInterval = 60

    ;; How often to run the expiration/orphan cleanup (seconds)
    CleanupInterval = 21600  ;; 6 hours

    ;; Staggered spawning: how many bots to respawn per second on region load
    ;; Lower values = less startup impact, slower full population
    RespawnRate = 2
```

**Region-level override:** Each region also has `AllowBotPersistence` stored in `RegionSettings`, toggled by the region owner via Estate tools. Persistence only works when both the grid-level and region-level switches are on. See section 1.2 for details.

---

## 5. Performance Analysis and Mitigation

### 5.1 Region Restart Impact

The primary cost of bot persistence is ScenePresence creation during region startup. Each bot requires appearance loading, animation state setup, and scene graph registration — the same cost as an avatar logging in.

**Mitigation — staggered spawning:** Bots are not all spawned in the same frame. The `RespawnRate` config (default: 2 per second) spreads creation across multiple ticks. A region with 50 persistent bots takes ~25 seconds to fully populate, but the region is accepting real avatar connections immediately. Real logins always take priority — if an avatar is connecting, bot spawning pauses until the login completes.

**Startup sequence:**

1. Region loads terrain, parcels, scene objects (normal startup).
2. Region begins accepting connections (avatars can log in).
3. `BotManager.LoadPersistentBots()` runs cleanup pass (instant — SQLite queries on 50 rows take milliseconds).
4. Staggered spawn timer begins, creating `RespawnRate` bots per second.
5. Log summary once all bots are spawned or deactivated.

This means region restart time is effectively unchanged. The bots trickle in after the region is already live.

### 5.2 Grid-Wide Restart Impact

Each region is its own process (or at least its own scene loop), so bot respawning is naturally parallel across regions. A grid restart with 20 regions each having 50 bots is not 1,000 sequential spawns — it's 20 independent staggered sequences running simultaneously. SQLite is per-region, so there's no shared database contention.

### 5.3 Runtime Overhead

**Position saves:** The periodic SQLite write (default every 60 seconds) batches all active persistent bots into a single transaction. For 50 bots, this is ~50 UPDATE statements in one transaction — typically under 5ms on any modern disk, invisible to frame time.

**Cleanup timer:** The 6-hour cleanup cycle runs a few SELECT/UPDATE queries against the SQLite database. Sub-millisecond impact.

**Bot ScenePresence cost:** Each persistent bot consumes the same resources as any NPC — physics updates, animation ticks, scene presence in the entity list. This is the real ongoing cost, and it's already bounded by the capacity caps (MaxPerRegion). A region with 50 bots uses roughly the same resources as a region with 50 avatars standing around. Grid operators should set MaxPerRegion based on their hardware.

### 5.4 Crash Safety

If a region crashes rather than shutting down cleanly, the shutdown save pass doesn't run. Mitigations:

- **Appearance data** is saved at persist time and only updated when the appearance actually changes (via a dirty flag). So a crash loses at most the current position, not the appearance.
- **Position data** is saved by the periodic timer (default 60s), so worst case a crash loses up to 60 seconds of movement. This is acceptable — the bot reappears near its last known position.
- **SQLite WAL mode** is enabled on database creation to prevent corruption from unclean shutdown. WAL (Write-Ahead Logging) ensures that committed transactions survive crashes.
- **No data loss for the persistence entry itself** — the row was written at persist time and only position/rotation are updated periodically.

### 5.5 Memory Impact

Each ScenePresence (bot or avatar) consumes approximately 50-100KB of memory depending on appearance complexity. At the maximum default of 50 bots per region, that's roughly 2.5-5MB of additional memory — negligible on any modern system. The SQLite database for 50 bots with appearance data is typically under 1MB on disk.

---

## 6. Implementation Plan

### Phase 34a — Core Infrastructure

1. Create `BotPersistenceManager` class alongside BotManager.
2. SQLite database initialization with WAL mode, schema creation on first run.
3. `botSetPersistent()` / `botRemovePersistent()` / `botIsPersistent()` script functions.
4. Permission validation (parcel rez rights, caps, region-level toggle).
5. Position save timer (batched transactions) and shutdown save.
6. `AllowBotPersistence` field in RegionSettings + Estate tools integration.

### Phase 34b — Respawn on Region Load

1. `LoadPersistentBots()` during region startup (after parcels are loaded, after connections accepted).
2. Cleanup pass (expiration, orphan detection, permission re-check).
3. Staggered bot spawning with `RespawnRate` throttle and avatar-login priority.
4. Appearance restore from persisted data (dirty-flag updates only).
5. Script reconnection testing via `botGetAllMyBotsInRegion`.

### Phase 34c — Admin Tools and Polish

1. Console commands (`clear persistent bots`, `list persistent bots`).
2. Estate manager in-world functions.
3. `botGetPersistentData()` / `botSetPersistentData()` for custom script state.
4. Logging and diagnostics.

### Files to Create/Modify

| File | Action | Description |
|------|--------|-------------|
| `BotPersistenceManager.cs` | Create | Core persistence logic, SQLite access, cleanup, staggered spawn |
| `BotManager.cs` | Modify | Hook persistence into spawn/despawn lifecycle |
| `RegionSettings.cs` | Modify | Add `AllowBotPersistence` boolean field |
| `EstateManagementModule.cs` | Modify | Expose region-level toggle in Estate tools |
| `LSLSystemAPI.cs` | Modify | New script functions (667+) |
| `ISystemAPI.cs` | Modify | Interface declarations for new functions |
| `SyscallShim.cs` | Modify | Shim methods and table entries |
| `OpenSim.ini.example` | Modify | Add `[BotPersistence]` section with defaults |

### Dependencies

- `Microsoft.Data.Sqlite` (preferred for .NET 8) or `System.Data.SQLite` — check which is already referenced in the solution.
- No viewer changes required.
- No physics engine changes required.

---

## 7. Error Codes

Script functions return integer error codes rather than silent failure:

| Code | Constant | Meaning |
|------|----------|---------|
| 0 | BOT_PERSIST_OK | Success |
| -1 | BOT_PERSIST_NOT_FOUND | Bot ID not found or not owned by caller |
| -2 | BOT_PERSIST_NO_PERMISSION | Owner lacks rez rights on parcel |
| -3 | BOT_PERSIST_PARCEL_CAP | Parcel bot limit exceeded |
| -4 | BOT_PERSIST_OWNER_CAP | Per-owner bot limit exceeded |
| -5 | BOT_PERSIST_REGION_CAP | Region bot limit exceeded |
| -6 | BOT_PERSIST_DISABLED | Bot persistence is disabled in config |
| -7 | BOT_PERSIST_ALREADY | Bot is already persistent |

---

## 8. Security Summary

| Threat | Mitigation |
|--------|------------|
| Unauthorized bot creation | Parcel rez-rights check at persist time and respawn time |
| Resource exhaustion (bot flood) | Three-layer caps: per-parcel, per-owner, per-region |
| Region lag from mass bot spawn | Staggered spawning (RespawnRate) with avatar-login priority |
| Grid restart overload | Per-region SQLite (no shared DB), parallel staggered spawns |
| Crash data loss | WAL mode, appearance saved at persist time, position saved every 60s |
| Orphaned bots from deleted scripts | Heartbeat via script interaction + orphan detection on startup |
| Stale bots from inactive owners | Configurable TTL with automatic expiration |
| Parcel ownership change | Permission re-check on respawn; deactivate if rights lost |
| Banned owner | Ban check on respawn; deactivate all owner's bots |
| Unwanted feature on a region | Region-level toggle via Estate tools (no console needed) |
| Unwanted feature grid-wide | Grid-level `Enabled = false` (default) — must opt in |
| Admin inability to intervene | Console commands + estate manager in-world functions |
| Silent failures confusing scripters | Explicit integer error codes on all persistence operations |
| Audit trail loss | Soft-delete (active flag) with deactivation_reason, never hard-delete |
