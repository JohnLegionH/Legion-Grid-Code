# s_primCharacters Cross-Region Fix — Recon and Plan

**Finding:** M-14 from `memory-resource-triage.md`
**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs`
**Scope:** Strictly limited to `s_primCharacters` and its 5 access sites.

---

## Current state

### Declaration and keying (Q1)

```csharp
// LSLSystemAPI.cs lines 11108–11109
private static readonly Dictionary<uint, UUID> s_primCharacters = new();
private static readonly object s_charLock = new();
```

- `static readonly` — one instance shared across ALL `LSLSystemAPI` instances in the process
- Key: `uint` — the host prim's `m_host.LocalId`
- Value: `UUID` — the bot/character UUID registered with `IBotManager`
- **Thread safety: HANDLED** — all access is inside `lock (s_charLock)`. Thread-safety is not a bug here.
- **The bug**: the `uint` localID is only unique within one region. In a multi-region process, two prims in two different regions can have the same `uint` localID, causing key collision.

### Access site inventory (Q2)

All five sites are in a contiguous block at lines 11108–11179. Key type at each site is always `m_host.LocalId`.

| Line | Function | Operation | Key | Region reference available? |
|------|----------|-----------|-----|-----------------------------|
| 11108 | — | Declaration | `uint` | — |
| 11115 | `GetCharacterBot()` | `TryGetValue` (read) | `m_host.LocalId` | `World` accessible (class property) |
| 11148 | `llDeleteCharacter()` | `TryGetValue` (read) | `m_host.LocalId` | `World` accessible |
| 11150 | `llDeleteCharacter()` | `Remove` | `m_host.LocalId` | `World` accessible |
| 11179 | `llCreateCharacter()` | `[key] =` (write) | `m_host.LocalId` | `World` accessible |

### What the localID represents (Q3)

`m_host.LocalId` is the **host prim's** localID — the prim that holds the collision/character script. This is consistent across all five sites: the dictionary tracks "which prim has an active character".

### Region reference availability (Q4)

`World.RegionInfo.RegionID` (a `UUID`) is already used throughout LSLSystemAPI:

```csharp
// line 366 (already present):
RegionID = World.RegionInfo.RegionID.Guid,
```

`World` is a class property (`=> m_ScriptEngine.World`, line 50), where `m_ScriptEngine` is the PhloxEngine instance for this script's scene. Available at all five access sites without any new plumbing.

### LocalID collision is real (Q4 continued)

Each `Scene` initializes its localID counter at a **random offset in the first quarter of uint space**:

```csharp
// Scene.cs line 806:
m_lastAllocatedLocalId = (int)(Random.Shared.NextDouble() * (uint.MaxValue / 4));
```

Two regions in the same process start from independently-chosen random bases in the 0–~1B range and allocate sequentially. With any significant number of objects, localID overlap between two long-running regions is a virtual certainty over time.

**The collision is real and reachable.**

### Lifecycle: add/remove points and leak paths (Q7)

**Entry is added:** `llCreateCharacter()` at line 11179. Always preceded by `llDeleteCharacter()` (line 11160) to ensure no duplicate.

**Entry is removed:** `llDeleteCharacter()` at line 11150 — the only removal site.

**Leak paths** (entry added but never removed):
1. **Prim deleted without calling `llDeleteCharacter()`**: The entry lives in the static dict for the process lifetime. No `OnObjectRemoved` event is subscribed in PhloxEngine to trigger cleanup.
2. **Script removed/reset**: `OnStopScript` (PhloxEngine.cs:235) only calls `ChangeEnabledStatus` — no character cleanup.
3. **Region shutdown**: `BotManager.RemoveRegion()` removes bots from `m_bots` but does NOT touch `s_primCharacters`. After region shutdown, stale `(localID → botUUID)` entries remain in the static dict forever for that process lifetime. If the region restarts (without process restart), new prims may coincidentally receive the same localIDs and find stale entries.

The stale entry for a dead bot is mostly harmless at read time (BotManager just returns null/zero when the botID is looked up), but it does prevent the prim from creating a fresh character until the stale entry is detected and overwritten by `llCreateCharacter` → `llDeleteCharacter()`.

---

## The bugs

### Bug 1: Cross-region key collision (Correctness — HIGHEST priority)

**Scenario:**
1. Region A, prim P1 (localID=1000) calls `llCreateCharacter()` → `s_primCharacters[1000] = botA`
2. Region B, prim P2 (localID=1000) calls `llCreateCharacter()` → `s_primCharacters[1000] = botB` (overwrites!)
3. P1 now calls `GetCharacterBot()` → returns `botB` (wrong region's bot)
4. P1 calls `llDeleteCharacter()` → removes `botB` from dict, calls `manager.RemoveBot(botB, ...)` (deletes Region B's bot!)
5. P1's original bot `botA` is now leaked in BotManager, can never be cleaned up through LSL

**Impact:** With the Legion Grid running two or more regions (Ebony + others), any prim in either region using pathfinding characters is at risk.

### Bug 2: Memory leak on prim/script death (Memory — MEDIUM)

Entries in `s_primCharacters` are never cleaned up except by explicit `llDeleteCharacter()` calls. Prim deletions, script removals, and region shutdowns all leave entries behind. The dict grows monotonically for the process lifetime.

In practice, the bot UUID values in leaked entries point to bots that BotManager may have already cleaned up (if the region shutdown triggered `RemoveRegion`). The entry is harmless for correctness once the region is gone, but it's wasted memory.

---

## Proposed fix

### Recommended: Option A — Composite key `(UUID region, uint localId)`

**Why Option A over the others:**
- **Option B (non-static, per-LSLSystemAPI instance)**: Wrong — `s_primCharacters` must be shared across all scripts on the same prim (a prim can have multiple scripts; they must share one character). Making it a per-instance field would fragment state per-script.
- **Option C (nested dict `Dict<UUID, Dict<uint, UUID>>`)**: Works but two-level lookup is more complex with no real benefit over a ValueTuple key.
- **Option D (move to BotManager)**: BotManager is `ISharedRegionModule` (process-global). Adding `s_primCharacters` there doesn't fix anything unless the key is still composite. Simpler to fix in place.

**Change shape:**

Add a helper property above the declaration:
```csharp
// Region-qualified key: (regionID, prim localID). LocalIDs are only unique within
// one region; a static dict keyed by localID alone collides across regions.
private (UUID, uint) CharKey => (World.RegionInfo.RegionID, m_host.LocalId);
```

Change the declaration:
```csharp
// BEFORE:
private static readonly Dictionary<uint, UUID> s_primCharacters = new();

// AFTER:
private static readonly Dictionary<(UUID, uint), UUID> s_primCharacters = new();
```

Update the five access sites — only the key expression changes, nothing else:
```csharp
// GetCharacterBot():
s_primCharacters.TryGetValue(CharKey, out UUID botID)

// llDeleteCharacter():
s_primCharacters.TryGetValue(CharKey, out botID)
s_primCharacters.Remove(CharKey)

// llCreateCharacter():
s_primCharacters[CharKey] = botID;
```

**Thread safety:** Unchanged — `s_charLock` is already correct and sufficient.

**IBotManager interface:** Unchanged — the fix is entirely internal to `LSLSystemAPI`.

**Function signatures (llCreateCharacter, llDeleteCharacter, etc.):** Unchanged.

**Estimated change:** 6 lines (1 new property, 1 modified declaration, 4 modified access sites). All in one contiguous block (lines 11108–11179). Single commit.

### Addressing Bug 2 (leak) — recommended follow-up

The cleanest place to add cleanup is in `PhloxEngine` where the engine's lifecycle events are hooked. `PhloxEngine` subscribes to `EventManager.OnRezScript`, `OnStopScript`, etc. Adding:

```csharp
// In PhloxEngine.AddRegion():
m_Scene.EventManager.OnObjectRemovedFromScene += OnObjectRemovedFromScene;

// Handler:
private void OnObjectRemovedFromScene(SceneObjectGroup sog)
{
    foreach (var part in sog.Parts)
        LSLSystemAPI.ClearCharacter(m_Scene.RegionInfo.RegionID, part.LocalId);
}
```

Where LSLSystemAPI exposes:
```csharp
internal static void ClearCharacter(UUID regionID, uint localId)
{
    lock (s_charLock)
        s_primCharacters.Remove((regionID, localId));
}
```

This should be a **separate follow-up commit** — it requires subscribing a new event in PhloxEngine and is independent of the key-collision fix. Mark as M-14b.

---

## Risk assessment

### Could the fix break the pathfinding character system?

**Extremely low risk.** The fix changes only the internal dictionary key in `s_primCharacters`. From the perspective of every LSL function:

- `GetCharacterBot()` still returns the same botUUID for the calling prim's character — now correctly isolated to this region
- `llDeleteCharacter()` still deletes the calling prim's character — now guaranteed to be the right one
- `llCreateCharacter()` still creates a new character and records it — now under a region-qualified key

No `IBotManager` method signatures change. No pathfinding function signatures change. No events change.

**Correctness improvement:** After the fix, prims in different regions with matching localIDs no longer interfere. The system becomes MORE correct, not less.

### Migration: in-flight characters when the fix deploys

On hot deploy (region restart), all `s_primCharacters` entries are reset to empty (static dict is reinitialized on process start). Any characters that were active before restart need to be re-created via script re-rez. This is the same behavior as today — the fix does not change this.

No migration needed: the dict starts empty each process lifetime.

### ValueTuple as Dictionary key

`(UUID, uint)` uses C# ValueTuple, which has correct `Equals`/`GetHashCode` via `ValueTuple`'s generated implementation (combines both fields). UUID also implements correct equality. This is a safe pattern and widely used in .NET 7+.

---

## Implementation sequence

| # | File | Change | Lines |
|---|------|--------|-------|
| 1 | `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs` | Add `CharKey` property; change dict declaration to `Dictionary<(UUID, uint), UUID>`; update 4 access sites | ~7 lines |

**Single commit.** All changes in one contiguous block.

### Change checklist

1. Add `private (UUID, uint) CharKey => (World.RegionInfo.RegionID, m_host.LocalId);` above the `s_primCharacters` declaration
2. Change `Dictionary<uint, UUID>` to `Dictionary<(UUID, uint), UUID>` at line 11108
3. Replace `m_host.LocalId` with `CharKey` at lines 11115, 11148, 11150, 11179
4. Verify: grep for any remaining bare `m_host.LocalId` in the character block (lines 11106–11180) to confirm none missed
5. No other files need changing

### Do NOT touch

- `IBotManager.cs` — interface unchanged
- `BotManager.cs` — implementation unchanged
- Any of the 621-628 bot* LSL functions that use `botID` directly (they're already UUID-keyed via BotManager, not via `s_primCharacters`)
- `PhloxEngine.cs` — follow-up only

---

## Verification plan

### Functional correctness (single region)

1. Create a physical prim with a character script:
   ```lsl
   default { state_entry() { llCreateCharacter([]); llNavigateTo(<128,128,22>, []); } }
   ```
2. Verify the character bot appears in the region and moves.
3. Delete the prim — verify the bot is removed (BotManager cleanup via `RemoveRegion` at worst).
4. Re-rez prim — verify a fresh character is created correctly.

### Cross-region isolation (the actual bug test)

If two regions are running in the process:
1. In region A, rez a prim at a position that gives it a low localID (first object rezzed after region start).
2. In region B, rez a prim at a position that gives it a low localID (same).
3. Run `llCreateCharacter([])` in both scripts simultaneously.
4. Before fix: one bot's UUID is overwritten; one region's `llDeleteCharacter()` will kill the other region's bot.
5. After fix: both characters coexist; each prim's `GetCharacterBot()` returns only its own bot.

**Forcing a collision for testing:** LocalIDs can't be directly controlled from LSL, but a region restart guarantees fresh sequential allocation. Start both regions fresh, then rez the first prim in each region — both get localID = (base + 1). Or: `grep -n "m_lastAllocatedLocalId" Scene.cs` to find the starting value for a given run, then rez exactly that many prims + 1 to get a known localID.

### Regression check

After the fix, verify that calling `llNavigateTo`, `llPursue`, `llEvade`, `llFleeFrom`, `llWanderWithin`, `llExecCharacterCmd`, and `llUpdateCharacter` still correctly find and operate on the character by checking `GetCharacterBot()` returns non-zero within the script's prim context.
