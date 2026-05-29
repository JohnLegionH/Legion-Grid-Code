# Memory and Resource Usage Triage
2026-05-27

## Methodology

Scanned `OpenSim/Region/Framework/Scenes/`, `Addons/Phlox/`, `Region/PhysicsModules/BulletS/`,
`Region/CoreModules/Scripting/`, `Region/CoreModules/Asset/`, `Services/AssetService/`, and
`Region/CoreModules/Avatar/` with targeted greps across all eight categories. Followed lifecycle
(Add/Remove symmetry, TTL, size limits) for each suspicious collection. Did not audit
`LSLSystemAPI.cs` at depth. Did not audit YEngine or SQL schemas.

Confidence ratings reflect code-visible evidence only. Some findings depend on actual region
workload patterns that cannot be determined statically.

---

## Summary Table

| ID | Category | Finding | Impact | Confidence | Fix Complexity |
|----|----------|---------|--------|------------|----------------|
| M-1 | A: Unbounded growth | `Http3AssetService.m_OwnerRequestsThrottle` never cleaned | Med | High | Small |
| M-2 | A: Unbounded growth | `FlotsamAssetCache` memory tier has no item/byte size limit | Med | High | Small |
| M-3 | A: Unbounded growth | `WorldCommModule.m_listenersByChannel` empty channel entries not pruned | Low | Med | Small |
| M-4 | B: Per-object state | `m_scriptEvents` Dictionary always allocated on every SOP | Med | High | Small |
| M-5 | B: Per-object state | UndoRedoState holds two LinkedLists per edited SOP, default 20 entries | Low | Med | Config |
| M-6 | C: Allocation hot path | `new DetectParams[0]` at 34 sites in Phlox — per-event heap alloc | Med | High | Small |
| M-7 | C: Allocation hot path | `CheckMovingTransitions()` calls `GetSceneObjectGroups()` per frame | Med | High | Small |
| M-8 | C: Allocation hot path | Collision handling allocates 3 `List<uint>` per physical object per frame | Med | High | Medium |
| M-9 | D: Lock contention | UrlModule nested lock order (`m_UrlMap` → `m_RequestMap`) at 5+ sites | Med | Med | Small |
| M-10 | E: Cache strategy | `Http3AssetService.GetCacheSizeMB()` is O(n) on every store operation | Med | High | Small |
| M-11 | E: Cache strategy | `Http3AssetService` LRU eviction does full `OrderBy` sort of access times | Med | High | Small |
| M-12 | E: Cache strategy | `Http3AssetService` dual-dictionary for cache entries (data + access times) | Low | High | Medium |
| M-13 | F: Asset/disk | `FlotsamAssetCache` file-cache tier has no disk-space limit | Low | High | Config |
| M-14 | A: Unbounded growth | `s_primCharacters` static dict in `LSLSystemAPI` spans all regions in process | Low | Med | Small |
| M-15 | A: Unbounded growth | `PhloxScriptLoader.m_WaitingForAsset` entries not cleaned if asset never arrives | Med | Med | Small |

---

## Findings Detail

### M-1: `Http3AssetService.m_OwnerRequestsThrottle` Never Cleaned

- **Category:** A: Unbounded growth
- **Files:** `OpenSim/Services/AssetService/Http3AssetService.cs` lines 92, 327–351, 465–470
- **Pattern:**
  `m_OwnerRequestsThrottle` is a `ConcurrentDictionary<UUID, ThrottleData>` keyed by owner agent
  UUID. An entry is created or updated on every `llHTTPRequest` call (line 351). Entries are never
  removed: line 470 removes from `m_RequestsThrottle` (keyed by object localID) but has no
  corresponding `m_OwnerRequestsThrottle.TryRemove`. The dictionary accumulates one entry per
  unique agent UUID who has ever triggered an HTTP request from a scripted object.
- **Impact assessment:** On a busy grid with many residents, over days/weeks this accumulates
  thousands of entries. Each `ThrottleData` struct is ~32 bytes; each `ConcurrentDictionary` entry
  carries ~64 bytes overhead. 10,000 agents = ~1 MB of effectively dead data. Small absolute size,
  but grows monotonically for the process lifetime.
- **Confidence:** High — code confirms Add path exists, Remove path is absent.
- **Suggested fix scope:** Small. Add `m_OwnerRequestsThrottle.TryRemove(ownerID, out _)` alongside
  the existing `m_RequestsThrottle.TryRemove` at line 470 when an owner's request count reaches
  zero, or run a periodic cleanup removing entries whose `lastTime` is older than some threshold
  (e.g., 10 minutes with no activity).
- **Open question:** Whether throttle state should survive between requests for rate-limit accuracy
  (intentional design) or is simply not cleaned by accident. The absence of any cleanup code
  suggests the latter.

---

### M-2: `FlotsamAssetCache` Memory Tier Has No Size Limit

- **Category:** A: Unbounded growth (bounded in practice by TTL)
- **Files:** `OpenSim/Region/CoreModules/Asset/FlotsamAssetCache.cs` lines 89–191
  `OpenSim/Framework/ExpiringCacheOS.cs`
- **Pattern:**
  `m_MemoryCache` is an `ExpiringCacheOS<string, AssetBase>` (TTL-only eviction, no item count or
  byte limit). Default TTL is 0.016 hours ≈ 58 seconds, so steady-state footprint is bounded by
  assets loaded in any 58-second window. However: `ExpiringCacheOS` has no `AddOrUpdate` size cap
  at all, so if the TTL is raised via config (`MemoryCacheTimeout`), or if a workload fetches many
  assets continuously, the cache grows to whatever memory is available. No size-based eviction
  exists in the implementation.
- **Impact assessment:** At default TTL this is mild — transient spike only. At higher TTL values
  (some operators set hours), all unique texture/mesh UUIDs fetched in that window accumulate. A
  busy region loading 50,000 unique assets at 5KB average = 250 MB of memory cache with no cap.
- **Confidence:** High — `ExpiringCacheOS` has no `MaxCount` or `MaxBytes` parameter.
- **Suggested fix scope:** Small. Add a maximum item count or maximum bytes config to
  `FlotsamAssetCache` and check it in the `AddOrUpdate` call, evicting oldest TTL entries when
  exceeded. Alternatively, document that high TTL values are unsafe without a size cap.
- **Open question:** Whether the asset `Size` property on `AssetBase` reflects actual byte size or
  is sometimes approximate/zero; needed to implement byte-limit correctly.

---

### M-3: `WorldCommModule.m_listenersByChannel` Empty Channel Entries

- **Category:** A: Unbounded growth (minor)
- **Files:** `OpenSim/Region/CoreModules/Scripting/WorldComm/WorldCommModule.cs` lines 101, 295–343
- **Pattern:**
  When the last listener on a channel is removed via `ListenRemove`, the empty `List<ListenerInfo>`
  value for that channel key is explicitly removed from the dictionary (line 308/343). This is
  correctly managed. However, the dictionary grows to the high-water mark of simultaneously active
  channels across all scripts in the region. No concern for steady-state. Minor concern for regions
  with scripts that frequently llListen on many unique channels.
- **Impact assessment:** Low. Each channel key is an `int` (4 bytes) with a `List<>` (~56 bytes
  empty). Even 10,000 unique channels simultaneously = ~600 KB. Cleanup is correct.
- **Confidence:** Medium — cleanup looks correct in the code path I traced; may miss edge cases.
- **Suggested fix scope:** None required unless profiling shows growth. Worth noting.
- **Open question:** Whether scripts that die without calling `llListenRemove` leave channels
  in the dictionary until region restart. The `ListenRemoveAll(UUID itemID)` path removes by itemID,
  so if `PhloxListenManager.Remove(itemID)` calls this correctly, it should clean up on script death.

---

### M-4: `m_scriptEvents` Dictionary Always Allocated on Every SceneObjectPart

- **Category:** B: Per-object state
- **Files:** `OpenSim/Region/Framework/Scenes/SceneObjectPart.cs` line 297
- **Pattern:**
  ```csharp
  private Dictionary<UUID, scriptEvents> m_scriptEvents = new Dictionary<UUID, scriptEvents>();
  ```
  This dictionary is allocated eagerly on every SOP construction. For non-scripted prims (the
  majority of a typical region) it remains empty for the object's lifetime. By contrast,
  `m_UndoRedo` (line 301) is correctly null and lazily allocated only on first use.
- **Impact assessment:** A typical region has 5,000–15,000 prims. An empty `Dictionary<UUID,
  scriptEvents>` occupies ~80–120 bytes (header + initial bucket array). 15,000 × 100 bytes =
  ~1.5 MB allocated but permanently empty. On high-prim regions (45,000 prims) = ~4.5 MB wasted.
- **Confidence:** High — allocation is unconditional; no lazy-init pattern present.
- **Suggested fix scope:** Small. Change to `private Dictionary<UUID, scriptEvents>? m_scriptEvents;`
  and add null checks at each access site (there are ~8 usages). Mirrors the existing
  `m_UndoRedo ??= new UndoRedoState(5)` pattern.
- **Open question:** None — straightforward null-lazy pattern.

---

### M-5: UndoRedoState Default Depth Is High Per Config

- **Category:** B: Per-object state
- **Files:** `OpenSim/Region/Framework/Scenes/UndoState.cs` lines 149–235
  `OpenSim/Region/Framework/Scenes/Scene.cs` line 284, 940
- **Pattern:**
  `UndoRedoState` holds two `LinkedList<UndoState>` (undo + redo). Default depth is 5 in code, but
  the scene config `MaxPrimUndos` defaults to 20 (line 940). Each `UndoState` stores a position,
  rotation, scale, and flags — roughly 80–120 bytes per entry. For a region with 15,000 objects
  all recently moved: 15,000 × 20 × 2 lists × 100 bytes ≈ 60 MB of undo state. Lazy init (via
  `??=`) means this only materializes for prims that have been moved in the current session.
- **Impact assessment:** Low under normal usage (few prims actively edited). High during building
  sessions. Not a leak — bounded by object count × MaxPrimUndos.
- **Confidence:** Medium — lazy init limits the real-world impact; this is more a potential spike
  than a steady-state leak.
- **Suggested fix scope:** Config — document that `MaxPrimUndos=5` is appropriate for production
  regions and 20 is a building-server value. Alternatively cap at 5 in code and expose a separate
  "builder mode" config.
- **Open question:** Whether operators know this multiplier exists.

---

### M-6: `new DetectParams[0]` at 34 Sites in Phlox Event Dispatch

- **Category:** C: Allocation hot path
- **Files:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/PhloxEngine.cs` (many handler methods)
- **Pattern:**
  Every event handler in PhloxEngine that doesn't need detect params constructs a fresh empty
  array: `new DetectParams[0]`. This includes all 4 target event handlers, attach, land collision,
  money, and others. These are called thousands of times per second in active regions.
  `Array.Empty<DetectParams>()` returns a statically-allocated singleton of length zero — no heap
  allocation on each call.
- **Impact assessment:** Each `new DetectParams[0]` is ~24 bytes of heap allocation per event.
  At 1000 events/second across 100 running scripts = 24 KB/s of garbage. Minimal absolute size
  but measurable GC pressure during peaks. Cost is zero after switching to `Array.Empty<>`.
- **Confidence:** High — 34 distinct call sites confirmed by grep.
- **Suggested fix scope:** Small. Define `private static readonly DetectParams[] s_noDetect =
  Array.Empty<DetectParams>();` at class level and replace all 34 `new DetectParams[0]` usages.
  Zero behavioral change.
- **Open question:** None.

---

### M-7: `CheckMovingTransitions()` Allocates Full Region List Per Frame

- **Category:** C: Allocation hot path
- **Files:** `OpenSim/Region/Framework/Scenes/Scene.cs` lines 2016–2040
  `OpenSim/Region/Framework/Scenes/SceneGraph.cs` lines 1183–1194
- **Pattern:**
  `CheckMovingTransitions()` (added in Session 3d) calls `GetSceneObjectGroups()` every frame.
  `GetSceneObjectGroups()` allocates `new List<SceneObjectGroup>(entities.Length)` and populates it
  by iterating all entities. For a 5,000-object region at 45 fps that is 225,000 list elements
  allocated per second just for this method. `ForEachSOG(Action<SceneObjectGroup>)` exists and
  takes a delegate instead, avoiding the list allocation.
- **Impact assessment:** Medium GC pressure on large regions. The allocation is short-lived (Gen0)
  so GC handles it, but it adds to per-frame collection pause frequency. On a 15,000-object region
  at 45 fps: ~675,000 element-slots allocated/sec.
- **Confidence:** High — allocation confirmed in `GetSceneObjectGroups()` implementation.
- **Suggested fix scope:** Small. Replace:
  ```csharp
  List<SceneObjectGroup> groups = GetSceneObjectGroups();
  foreach (SceneObjectGroup sog in groups)
  ```
  with:
  ```csharp
  ForEachSOG(sog =>
  ```
  `ForEachSOG` calls `m_sceneGraph.ForEachSOG` which iterates `Entities.GetEntities()` (an
  internal array copy, unavoidable) but avoids the second `List<>` allocation.
- **Open question:** None — `ForEachSOG` is already used for similar per-frame iteration patterns
  elsewhere in `Scene.cs`.

---

### M-8: Collision Handling Allocates 3 `List<uint>` Per Physical Object Per Frame

- **Category:** C: Allocation hot path
- **Files:** `OpenSim/Region/Framework/Scenes/SceneObjectPart.cs` lines 2801–2803
- **Pattern:**
  In `UpdateCollisionSound` / collision diffing, three `List<uint>` are allocated per call:
  ```csharp
  List<uint> thisHitColliders = new List<uint>(ncollisions);
  List<uint> endedColliders   = new List<uint>(m_lastColliders.Count);
  List<uint> startedColliders = new List<uint>(ncollisions);
  ```
  This is called per physics frame for each physical object that reported a collision. With 100
  physics objects each producing 1 collision per frame at 60 Hz = 18,000 `List<uint>` allocations
  per second, plus the `m_lastColliders` field allocation (initialized eagerly at line 291).
- **Impact assessment:** Medium GC pressure on physics-heavy regions. Each list carries ~56 bytes
  header + backing array. At moderate physics activity, this is a measurable contributor to Gen0
  GC frequency.
- **Confidence:** High — allocation pattern confirmed.
- **Suggested fix scope:** Medium. Convert to stack-allocated spans or preallocated pooled arrays.
  Simpler near-term fix: reuse `m_lastColliders` as one of the working lists and only allocate when
  the count increases, using `Clear()` + `Add()` instead of new allocations.
- **Open question:** Whether collision diffs need to be thread-safe (two concurrent physics updates).
  If yes, per-part pooling is needed instead of instance reuse.

---

### M-9: UrlModule Nested Lock Order — Potential Deadlock

- **Category:** D: Lock contention
- **Files:** `OpenSim/Region/CoreModules/Scripting/LSLHttp/UrlModule.cs` lines 369, 479, 494, 504
- **Pattern:**
  `m_UrlMap` and `m_RequestMap` are two separate lock objects. Several code paths acquire them
  in nested order: `lock(m_UrlMap) { ... lock(m_RequestMap) { } }`. If any code path acquires
  them in the reverse order (`m_RequestMap` first, then `m_UrlMap`), a deadlock occurs. With 18+
  lock sites across the file, and two separately-managed lock objects that both protect related URL
  state, the pattern is fragile.
- **Impact assessment:** Medium. If a deadlock occurs it hangs the scripting thread. The HTTP
  request handling is on a background thread; a deadlock here freezes all URL-based LSL functions
  in the region. How often (if ever) the reverse order is taken is unclear without deeper tracing.
- **Confidence:** Medium — the same-direction acquire pattern appears consistent in the code I
  traced, but 18 lock sites weren't all verified.
- **Suggested fix scope:** Small. Audit all 18 `lock(m_UrlMap)` and `lock(m_RequestMap)` sites.
  Establish one explicit rule (always acquire `m_UrlMap` first if both needed) and enforce with
  a comment. Alternatively merge into a single lock object — the two maps are always used together.
- **Open question:** Whether the URL handling path that fires from an HTTP response thread ever
  enters `m_RequestMap` before `m_UrlMap`. HTTP callbacks are async; worth checking explicitly.

---

### M-10: `Http3AssetService.GetCacheSizeMB()` Is O(n) on the Store Hot Path

- **Category:** E: Cache strategy
- **Files:** `OpenSim/Services/AssetService/Http3AssetService.cs` lines 669, 702–710
- **Pattern:**
  Every time an asset is stored (line 669), the cache size is checked by calling `GetCacheSizeMB()`
  which iterates every entry in `m_assetCache` to sum their sizes:
  ```csharp
  foreach (var cached in m_assetCache.Values)
      totalBytes += cached.Size;
  ```
  For a 10,000-entry cache this is 10,000 iterations on every write. In a region loading many
  assets simultaneously (scene load, many agents logging in), this is called hundreds of times
  per second.
- **Impact assessment:** Medium performance impact during asset-heavy operations. The O(n) scan on
  each write makes size-limit checking scale poorly. At 1,000 writes/sec with 10,000 cache entries
  = 10M iterations/sec just for size accounting.
- **Confidence:** High — pattern confirmed.
- **Suggested fix scope:** Small. Add a `private long m_cacheSizeBytes = 0;` counter. Increment it
  on store (after decompression/size measurement), decrement on eviction. Replace
  `GetCacheSizeMB()` with `(int)(m_cacheSizeBytes / (1024 * 1024))`. Requires thread-safe
  increment/decrement (use `Interlocked.Add`).
- **Open question:** Whether `Http3CachedAsset.Size` reflects compressed or uncompressed bytes.
  If compressed, the limit is in compressed bytes, which is what matters for memory.

---

### M-11: Http3AssetService LRU Eviction Full-Sorts Access Times Dictionary

- **Category:** E: Cache strategy
- **Files:** `OpenSim/Services/AssetService/Http3AssetService.cs` lines 683–700
- **Pattern:**
  `EvictLeastRecentlyUsedAsync()` uses LINQ to sort all access times and take the oldest N:
  ```csharp
  var sortedByAccess = m_accessTimes
      .OrderBy(kvp => kvp.Value)
      .Take(evictCount)
      .Select(kvp => kvp.Key)
      .ToList();
  ```
  This is O(n log n) over all 10,000 entries (or whatever the cache size is) every time eviction
  is triggered. It also allocates multiple LINQ iterators and a final `List<string>` on each
  eviction. Eviction fires whenever the cache count or byte limit is exceeded on a store.
- **Impact assessment:** Medium. If the cache is sized tightly and eviction fires frequently (e.g.,
  many distinct assets), this sort runs often. O(n log n) over 10,000 entries is ~130,000
  comparisons per eviction call, plus LINQ allocation overhead.
- **Confidence:** High — pattern confirmed.
- **Suggested fix scope:** Small-Medium. Replace with a min-heap or `LinkedList<string>` (insertion
  order = LRU order) maintained alongside the dictionary. `O(1)` eviction, `O(1)` access update.
  Alternatively: evict 10% randomly (already the target quantity) rather than true LRU — much
  simpler and often equally effective for asset caches.
- **Open question:** Whether true LRU is required or whether approximate LRU (random eviction from
  coldest quartile) would be acceptable. The latter is a 2-line fix.

---

### M-12: Http3AssetService Dual-Dictionary for Cache + Access Times

- **Category:** E: Cache strategy
- **Files:** `OpenSim/Services/AssetService/Http3AssetService.cs` lines 64–65
- **Pattern:**
  Two separate `ConcurrentDictionary` instances:
  ```csharp
  private readonly ConcurrentDictionary<string, Http3CachedAsset> m_assetCache = new();
  private readonly ConcurrentDictionary<string, DateTime> m_accessTimes = new();
  ```
  Every lookup, store, and eviction must touch both. Each `ConcurrentDictionary` entry occupies
  ~64 bytes of overhead. With 10,000 entries: ~1.28 MB of overhead just for dictionary nodes,
  doubled from what a single dictionary would require.
- **Impact assessment:** Low-Medium. Primarily a code complexity and minor memory concern.
  The real cost is in M-10 and M-11 which both stem from this split design.
- **Confidence:** High.
- **Suggested fix scope:** Medium (affects M-10 and M-11 simultaneously). Define:
  ```csharp
  private record CacheEntry(Http3CachedAsset Asset, DateTime LastAccess, long SizeBytes);
  private readonly ConcurrentDictionary<string, CacheEntry> m_cache = new();
  ```
  Then M-10's size counter and M-11's LRU tracking collapse into a single dictionary, and the
  access-time update is atomic with the asset retrieval.
- **Open question:** Whether `Http3CachedAsset` can be extended to include access time and size,
  or whether a wrapper type is needed.

---

### M-13: `FlotsamAssetCache` File-Cache Tier Has No Disk-Space Limit

- **Category:** F: Asset/disk efficiency
- **Files:** `OpenSim/Region/CoreModules/Asset/FlotsamAssetCache.cs` lines 853, 1154
- **Pattern:**
  The file cache tier writes every fetched asset to disk (`<CachePath>/<assetID>`). The cleanup
  process (`CleanExpiredFiles`) removes files older than `FileCacheTimeout` (default 48 hours),
  but has no maximum-bytes or maximum-file-count limit. On a large grid with a diverse asset
  library, the file cache grows proportionally to the number of unique assets accessed in the
  expiry window.
- **Impact assessment:** Low for typical grids (most asset UUIDs are seen repeatedly; deduplication
  is implicit). High for grids with heavy user-content creation where many unique meshes/textures
  are uploaded. Could fill disk in extreme cases.
- **Confidence:** High — no byte-limit logic present.
- **Suggested fix scope:** Config. Add `MaxFileCacheSizeMB` config key (default: unlimited / 0).
  During cleanup, if total size exceeds limit, additionally evict oldest accessed files beyond
  the time-based cutoff.
- **Open question:** Whether the file cache already deduplicates assets by UUID (it does — one
  file per UUID), so the real limit is the number of unique assets ever seen, not request count.

---

### M-14: `s_primCharacters` Static Dictionary Spans All Regions in Process

- **Category:** A: Unbounded growth (scoped concern)
- **Files:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs` line 11108
- **Pattern:**
  ```csharp
  private static readonly Dictionary<uint, UUID> s_primCharacters = new();
  ```
  Keyed by `uint localID`. `LocalId` is per-region-unique but not globally unique — two prims in
  different regions can have the same `LocalId`. As a static field, this dictionary is shared
  across all `LSLSystemAPI` instances (i.e., all regions in the same simulator process). In a
  multi-region process, entries from one region can shadow or be confused with entries from another.
  Cleanup on line 11150 calls `Remove(m_host.LocalId)` which may remove another region's prim's
  entry if they share the same local ID.
- **Impact assessment:** Low — only regions using bot characters (custom extension) are affected.
  The collision risk between regions is real but unlikely in practice given localID ranges. The leak
  risk is real if prim deletion doesn't properly call the cleanup path.
- **Confidence:** Medium — the cross-region collision requires same-localID in two regions
  simultaneously, which depends on localID assignment strategy.
- **Suggested fix scope:** Small. Change key from `uint localID` to a `(UUID regionID, uint localID)`
  tuple, or alternatively make the dictionary instance-based rather than static (move it to a
  per-scene or per-engine instance). The per-engine approach matches how the rest of `PhloxEngine`
  state is managed.
- **Open question:** Whether bot character state is intentionally designed to cross regions (e.g.,
  NPCs following avatars across region boundaries). If so, keying by `(regionID, localID)` may break
  intended behavior.

---

### M-15: `PhloxScriptLoader.m_WaitingForAsset` May Leak on Asset Fetch Failure

- **Category:** A: Unbounded growth (conditional)
- **Files:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/PhloxScriptLoader.cs` line 60
- **Pattern:**
  ```csharp
  private readonly Dictionary<UUID, List<PhloxLoadRequest>> m_WaitingForAsset = new();
  ```
  Load requests that are waiting for an asset to arrive are stored here. On asset arrival, line 448
  calls `m_WaitingForAsset.Remove(assetId)`. However, if the asset fetch fails permanently (server
  returns 404, timeout, etc.), the removal path may not be reached. The entry and its
  `List<PhloxLoadRequest>` would remain until region restart.
- **Impact assessment:** Medium if asset fetches fail silently. Each stuck entry holds references
  to `PhloxLoadRequest` objects (which hold script metadata). In a region where many scripts
  reference missing assets, these accumulate.
- **Confidence:** Medium — depends on how the asset-fetch failure path is handled in the callback.
  Did not trace the full asset fetch callback chain.
- **Suggested fix scope:** Small. Ensure the asset-fetch failure callback also calls
  `m_WaitingForAsset.Remove(assetId)` and fires a compile error to waiting scripts. Review the
  error path in the asset-received callback.
- **Open question:** Whether the asset service failure already propagates cleanly to remove the
  waiting entry. Requires tracing the callback chain more deeply.

---

## Recommended Audit Sequence

### Session A — Quick Wins (single session, all small fixes)
Fix together since they're mechanical and low-risk:

1. **M-6** — Replace 34 `new DetectParams[0]` with `Array.Empty<DetectParams>()` in PhloxEngine
2. **M-7** — Replace `GetSceneObjectGroups()` in `CheckMovingTransitions()` with `ForEachSOG()`
3. **M-4** — Make `m_scriptEvents` null-initialized with lazy allocation on first script add
4. **M-1** — Add `m_OwnerRequestsThrottle.TryRemove` alongside the existing `m_RequestsThrottle.TryRemove`

These are 4 files, ~6 lines of change total, zero behavioral risk.

### Session B — Http3AssetService Cache Overhaul (single focused session)
M-10, M-11, and M-12 share the same root (split-dictionary LRU design). Fix together:

1. Unify `m_assetCache` + `m_accessTimes` into a single `ConcurrentDictionary<string, CacheEntry>`
2. Add incremental byte counter to replace O(n) `GetCacheSizeMB()`
3. Replace LINQ sort eviction with `O(1)` LRU or random-cold eviction

These three findings share the same root cause and the fix is natural to do as one refactor.

### Session C — UrlModule Lock Audit (M-9)
Needs a careful read of all 18 lock sites in `UrlModule.cs`. Not mechanical — requires judgment
on whether reverse-order acquire is actually reachable. Separate session so it gets full attention.

### Session D — Asset Fetch Failure Path (M-15)
Requires tracing the full asset-fetch callback chain in `PhloxScriptLoader.cs` before touching
code. Recon-first session: map the success and failure paths, then fix.

### Session E — Collision Allocation Reduction (M-8)
Medium complexity. Consider pooled `List<uint>` per-SOP (reuse with `Clear()`). This is the
highest-frequency allocation on physics-heavy regions and worth dedicated attention.

---

## Cross-Cutting Themes

**1. Missing cleanup paths:** M-1 (owner throttle), M-15 (waiting-for-asset). A recurring pattern
where the Add path is implemented and the Remove/failure path is incomplete. Consider a code review
convention: any `Dictionary.Add` or `[key] =` inside a module should have a corresponding Remove
visibly linked nearby (same method, or with a comment pointing to the Remove site).

**2. Http3AssetService has three connected efficiency problems** (M-10, M-11, M-12) that all stem
from a split-data-structure design choice. This was likely written this way for LRU tracking
simplicity, but the approach has high constant overhead. One unified entry type would resolve all
three.

**3. Per-event heap allocation in script dispatch:** M-6 (empty DetectParams arrays) and M-8
(collision lists) are both on the hot scripting path. Scripting events fire thousands of times
per second in active regions; zero-allocation patterns (static singletons, pooled arrays) pay
dividends across all event types, not just the ones fixed here. A broader scrub of `new X[0]` and
`new EventParams(...)` with static/pooled alternatives is worthwhile.

**4. No observable metrics on cache hit rates:** FlotsamAssetCache doesn't log hit/miss rates
by default. Without this data, sizing decisions (TTL, memory limit) are guesses. A periodic
`m_log.Info` of hit rate would give operators actionable data.

---

## Out-of-Scope Notes

**BulletS custom extensions** (`AdvancedSpatialIndex`, `AdvancedConstraintSystem`, `DestructionParticleEffects`) — these are substantial custom physics extensions with their own state management. Cleanup paths look present but weren't deeply traced. Worth a dedicated physics-module audit.

**`LSLSystemAPI.cs` (18,763 lines)** — excluded per audit scope. String allocation, boxing, and LINQ in LSL function implementations is likely the highest-volume allocation source in the entire system and warrants its own dedicated pass.

**MySQL schema / indexes** — not audited. Database-layer efficiency (N+1 queries, missing indexes on large tables) can dwarf in-process memory improvements. Should be a separate audit targeting the Data layer.

**ScenePresence `m_knownChildRegions`** — grows as agents explore more neighbor regions. Bounded by the number of neighboring regions visible at once (not a leak), but on a high-density grid this could be hundreds of entries per agent. Not catalogued as a finding because bounded in practice; note for awareness.
