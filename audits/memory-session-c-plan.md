# FlotsamAssetCache Size Limits — Recon and Plan

Date: 2026-05-27  
Source: `memory-resource-triage.md` findings M-2 (memory tier) and M-13 (file tier)  
File: `OpenSim/Region/CoreModules/Asset/FlotsamAssetCache.cs` (1724 lines)  
No code changes in this document.

---

## Current State

### Q1 — Architecture

**Class:** `FlotsamAssetCache : ISharedRegionModule, IAssetCache, IAssetService`

**Three-tier access pattern** (executed in order on every `Get`):

| Tier | Field | Type | Scope | Always active? |
|------|-------|------|-------|---------------|
| Weak ref | `weakAssetReferences` | `Dictionary<string, WeakReference>` | Memory; GC-collected | Yes |
| Memory | `m_MemoryCache` | `ExpiringCacheOS<string, AssetBase>` | Memory; TTL eviction | Optional (`MemoryCacheEnabled`, default **false**) |
| File | `<m_CacheDirectory>/<shard>/<uuid>` | Serialized file | Disk; TTL cleanup | Optional (`FileCacheEnabled`, default **true**) |

**Get path** (lines 581–628):
1. Check negative cache → bail if present
2. Weak ref hit → promote to memory (if enabled), return
3. Memory hit → promote to weak ref, return
4. File hit → promote to weak ref + memory, return
5. Return null (miss propagates to `IAssetService.Get` on the scene)

**Cache/store path** (`Cache`, line 404): updates all tiers unconditionally.

**Key public interface methods:** `Cache(AssetBase, bool)`, `CacheNegative(string)`, `Get(string, out AssetBase)`, `GetCached(string)`, `Expire(string)`, `Clear()`. These come from `IAssetCache` — the interface contract must be preserved.

**Async file writes:** `ObjectJobEngine m_assetFileWriteWorker` (single background thread). Writes are queued as `WriteAssetInfo` structs, processed by `ProcessWrites`. The `m_CurrentlyWriting` static `HashSet<string>` prevents duplicate in-flight writes.

### Q2 — ExpiringCacheOS Shape

**Definition:** `OpenSim/Framework/ExpiringCacheOS.cs` (569 lines)

**Internals:**
- `Dictionary<TKey1, int> m_expireControl` — expiry timestamps (int offset from `m_startTS`)
- `Dictionary<TKey1, TValue1> m_values` — values keyed same as above
- `ReaderWriterLockSlim m_rwLock` — all public methods take read or write lock
- Purge timer fires every `m_expire` ms, removes entries whose offset is past

**Public API:**
- `Add(key, val)` / `Add(key, val, expireMS)` / `AddOrUpdate(key, val)` — write lock
- `TryGetValue(key, out val)` — read lock, non-sliding
- `TryGetValue(key, expireMS, out val)` — upgradeable read lock, refreshes TTL on hit
- `Remove(key)` — write lock
- `Clear()` — write lock, disposes timer
- `Count` → `m_expireControl.Count` (O(1))
- `Values` → array snapshot (`TValue1[]`), cached via `valuesArrayCache`
- **No `MaxCount`, no `MaxBytes`, no LRU ordering, no eviction callback**

**Critical implication for M-2:** ExpiringCacheOS cannot be given a size limit without modifying this shared framework type. The count-based guard must live entirely in FlotsamAssetCache, external to ExpiringCacheOS.

### Q3 — File Tier On-Disk Structure

**Directory sharding** (configurable, default: 1 tier, 3 chars per tier):
```
assetcache/
  abc/          ← first 3 chars of UUID
    abc12345-6789-abcd-ef01-234567890abc
  def/
    def99887-...
```
With default settings: 16^3 = 4096 possible shard directories. At 100K files → ~24 files/shard (fast per-directory iteration). At 1M files → ~244 files/shard (still manageable).

**File naming:** UUID string directly, no extension. `GetFileName` handles UUID sanitization and path construction.

**Cleanup loop** (`CleanExpiredFiles`, lines 853–945):
- Recursive: visits each tier directory, then files within
- For each file: checks `m_defaultAssets`, `gids` (scene-gathered assets) — **skips deletion if either matches**
- Deletes if `File.GetLastAccessTime(file) < purgeTimeline`
- Throttled with `Thread.Sleep(60–120ms)` every ~10–20 files (prevents I/O saturation)
- Scene asset protection: `GatherSceneAssets()` (lines 1154–1256) recursively gathers all UUID references from scene objects and avatars before cleanup runs

**Timer state:** `m_CacheCleanTimer` (AutoReset = false, restart after each run). **Default in FlotsamCache.ini: `FileCleanupTimer = 0.0` — timer DISABLED.**

**Scale cost:** With cleanup enabled, a full scan of 1M files across 4096 shard dirs reads filesystem metadata for each file (atime). On a typical Linux ext4/xfs with kernel caching, this is ~1–5 minutes. Already mitigated by sleep throttling.

### What's Correctly Bounded Today

- **File tier: TTL-based** — files older than `FileCacheTimeout` (48h) are deleted when cleanup runs. This is correct time-based eviction.
- **Memory tier: TTL-based** — ExpiringCacheOS purges entries after `MemoryCacheTimeout` (default ≈58 seconds). This naturally bounds memory if TTL is low.
- **Weak ref tier: GC-bounded** — entries evicted when no other reference holds the object.
- **Negative cache** (`ExpiringKey<string>`): TTL-based, inherently bounded.

### What's Unbounded

- **Memory tier (M-2):** No item count or byte limit. If `MemoryCacheTimeout` is raised (e.g., to 30 min) and the grid fetches diverse assets continuously, `m_MemoryCache` grows proportionally to (unique assets in window × asset size). No eviction fires until TTL expires or `Clear()` is called.
- **File tier (M-13):** No byte or count limit on the cache directory. If `FileCleanupTimer = 0` (default), files NEVER expire and accumulate indefinitely. Even with cleanup enabled, there is no `MaxFileCacheSizeMB` — the only deletion criterion is age.

**Secondary finding (not in triage):** The production config has `FileCleanupTimer = 0.0` (cleanup disabled). Without cleanup, all cached files persist until disk fills or the operator runs `fcache expire`. The M-13 fix is only useful when cleanup is enabled. Consider changing the default to `FileCleanupTimer = 1.0` (1 hour) as part of the M-13 commit — this is a pure config change with no code risk.

---

## Proposed M-2 Fix — Memory Tier Count Limit

### Config Option

```ini
; Maximum number of assets to hold in the memory cache (0 = unlimited, default).
; When this limit is reached, new assets are not added to memory but remain
; accessible via the file cache. TTL expiry will eventually free slots.
; Only used when MemoryCacheEnabled = true.
MaxMemoryCacheCount = 0
```

Default 0 = unlimited = current behavior. No operator action required on upgrade.

### Eviction Strategy: Drop-on-Full (count-based)

**Why not LRU:** ExpiringCacheOS has no ordered iteration and no eviction callback. Adding LRU eviction would require either (a) modifying ExpiringCacheOS (shared framework type, wider scope), (b) a parallel tracking structure in FlotsamAssetCache (duplication, same problems as Http3AssetService's dual-dictionary), or (c) calling `m_MemoryCache.Clear()` (nuclear — evicts everything).

**Recommended strategy: skip insert when full.** When `m_MemoryCache.Count >= m_MaxMemoryCacheCount`, do not add to memory. The asset is still written to the file tier and remains accessible via disk. When TTL expires existing entries, `Count` drops and new inserts succeed.

**Rationale:**
- ExpiringCacheOS will purge expired entries on its own timer. After each purge cycle (`m_expire` interval), slots free up naturally.
- The file tier is the primary persistent cache; memory is a fast-access overlay. Missing a memory insert is low cost.
- This matches the existing "approximate" philosophy: FlotsamAssetCache already uses `CacheWarnAt` as a soft warning rather than a hard limit.
- Zero new locks, zero changes to ExpiringCacheOS.

### Code Shape

Fields to add (class level):
```csharp
private int m_MaxMemoryCacheCount = 0; // 0 = unlimited
```

In `Initialise` (after existing config reads):
```csharp
m_MaxMemoryCacheCount = assetConfig.GetInt("MaxMemoryCacheCount", m_MaxMemoryCacheCount);
```

`UpdateMemoryCache` becomes:
```csharp
private void UpdateMemoryCache(string key, AssetBase asset)
{
    if (m_MaxMemoryCacheCount > 0 && m_MemoryCache.Count >= m_MaxMemoryCacheCount)
        return;
    m_MemoryCache.AddOrUpdate(key, asset, m_MemoryExpiration);
}
```

`fcache status` output — add one line to the memory-cache section:
```csharp
if (m_MaxMemoryCacheCount > 0)
    con.Output("[FLOTSAM ASSET CACHE] Memory Cache limit: {0} items", m_MaxMemoryCacheCount);
```

**Edit count:** ~5 lines new code, ~1 line config.

---

## Proposed M-13 Fix — File Tier Disk Limit

### Config Options

```ini
; Maximum total disk space used by the file cache, in MB (0 = unlimited, default).
; When this limit is exceeded during cleanup, files are deleted oldest-accessed first
; until the cache is back under the limit. Scene assets in use are protected.
; Only effective when FileCleanupTimer > 0 (cleanup is enabled).
MaxFileCacheSizeMB = 0
```

Also recommend (separate config-only change):
```ini
FileCleanupTimer = 1.0  ; was 0.0 — changed to enable 1-hour cleanup by default
```

### Eviction Strategy: Oldest-Accessed Files First, After TTL Pass

**Where:** In `DoCleanExpiredFiles`, AFTER the TTL-based cleanup pass completes. This order matters:
1. TTL pass removes obviously expired files (cheap, already implemented).
2. Size check: sum total cache bytes. If under limit, done.
3. If over limit: delete oldest-accessed files until under limit, respecting the `gids` scene-asset exclusion.

**Why not integrate into the existing TTL loop:** The existing `CleanExpiredFiles` loop combines:
- Recursive traversal
- Scene-asset exclusion lookup (`gids` dict)
- TTL decision + delete
- Cooldown sleeps

Adding size accounting to this loop would make it significantly more complex (need to track a running total across the recursive call stack, decide to delete differently based on size vs. TTL, etc.). A separate post-TTL pass is cleaner and easier to reason about.

**Why oldest-accessed first:** `File.GetLastAccessTime` is already read during the TTL pass. Files with old access times are the best candidates for eviction (they're the ones that haven't been re-used recently). This is equivalent to LRU eviction on the file tier.

### Code Shape

Field to add:
```csharp
private long m_MaxFileCacheSizeMB = 0; // 0 = unlimited
```

In `Initialise`:
```csharp
m_MaxFileCacheSizeMB = assetConfig.GetLong("MaxFileCacheSizeMB", m_MaxFileCacheSizeMB);
```

Call site in `DoCleanExpiredFiles` (append to bottom, before `lock (timerLock)` restart):
```csharp
if (m_MaxFileCacheSizeMB > 0 && m_cleanupRunning)
    EnforceFileCacheSizeLimit(gids);
```

New method:
```csharp
private void EnforceFileCacheSizeLimit(Dictionary<UUID, sbyte> gids)
{
    long limitBytes = m_MaxFileCacheSizeMB * 1024L * 1024L;

    // Collect all cache files with their size and last-access time
    var files = new List<(string path, long size, DateTime lastAccess)>();
    long totalBytes = 0;

    try
    {
        foreach (string file in Directory.EnumerateFiles(
            m_CacheDirectory, "*", SearchOption.AllDirectories))
        {
            if (!m_cleanupRunning) return;
            try
            {
                var info = new FileInfo(file);
                totalBytes += info.Length;
                files.Add((file, info.Length, info.LastAccessTime));
            }
            catch { }
        }
    }
    catch (Exception e)
    {
        m_log.Warn($"[FLOTSAM ASSET CACHE]: Error enumerating cache for size limit: {e.Message}");
        return;
    }

    if (totalBytes <= limitBytes)
        return; // Within limit — nothing to do

    m_log.Info($"[FLOTSAM ASSET CACHE]: Cache size {totalBytes / (1024 * 1024)}MB exceeds limit {m_MaxFileCacheSizeMB}MB — evicting oldest files");

    // Sort by last-access time ascending (oldest first)
    files.Sort((a, b) => a.lastAccess.CompareTo(b.lastAccess));

    long target = (long)(limitBytes * 0.90); // Evict to 90% to reduce frequency

    foreach (var (path, size, _) in files)
    {
        if (!m_cleanupRunning || totalBytes <= target) break;

        // Respect scene-asset exclusion
        string id = Path.GetFileName(path);
        if (m_defaultAssets.Contains(id)) continue;
        if (UUID.TryParse(id, out UUID uid) && gids.ContainsKey(uid)) continue;

        try
        {
            File.Delete(path);
            totalBytes -= size;
        }
        catch { }
    }
}
```

**Edit count:** ~55 lines new code, ~2 lines config.

---

## Risk Assessment

### Risk 1 — Memory cache thrashing under count limit

If `MaxMemoryCacheCount` is set too low (e.g., 100) on a grid with a large active asset library, the memory cache will be perpetually full and every insert will be a no-op drop. Assets continue to be served from the file tier (functionally correct), but the memory speedup is lost entirely.

**Mitigation:** Document a recommended starting value in FlotsamCache.ini.example (e.g., `; recommended: 5000 for typical grids`). Log at startup when the limit is configured. Add the current count vs. limit to `fcache status` output. Operators can tune based on observed behavior.

### Risk 2 — File size scan takes longer than cleanup interval

`EnforceFileCacheSizeLimit` calls `Directory.EnumerateFiles(..., SearchOption.AllDirectories)` on potentially millions of files, then sorts in memory. At 1M files on a spinning disk, enumeration alone can take 2–5 minutes. If `FileCleanupTimer = 1.0` (1 hour) and the scan takes 3 minutes, this is acceptable (5% overhead). But if cleanup itself also takes a long time, the two can overlap or starve each other.

**Mitigation:** The `m_cleanupRunning` boolean prevents re-entrant cleanup (`CleanupExpiredFiles` returns early if already running). The size-limit scan's inner loop already checks `!m_cleanupRunning` to allow abort. Keep the sort in-memory (the file list at 1M entries is ~50MB of strings + metadata — acceptable). If the sort proves too costly, replace with a partial selection (nth_element-style) in a future pass.

### Risk 3 — Scene-asset exclusion not passed to size-limit eviction

`DoCleanExpiredFiles` calls `GatherSceneAssets()` early to build `gids`. This collection is available when `EnforceFileCacheSizeLimit(gids)` is called (since it's passed as a parameter). However, `gids` is a point-in-time snapshot taken at the start of cleanup. Assets uploaded or rezzed DURING the cleanup run are not in `gids` and could be deleted by the size-limit pass.

**Mitigation:** This is the same race that exists in the current TTL cleanup — it's inherent to the design. The asset service will re-fetch and re-cache any asset that gets prematurely evicted. Accept this as existing behavior and document it.

---

## Implementation Sequence

**Single commit** — M-2 and M-13 are in the same file, neither depends on the other, and they're small enough to review together.

**Files to touch:**
- `OpenSim/Region/CoreModules/Asset/FlotsamAssetCache.cs` — ~60 new lines
- `bin/config-include/FlotsamCache.ini` — new key blocks + change `FileCleanupTimer` default
- `bin/config-include/FlotsamCache.ini.example` — same

**Estimated lines:** 60 in `.cs`, 8 in `.ini`.

**Edit sites in FlotsamAssetCache.cs:**
1. Class fields: add `m_MaxMemoryCacheCount` and `m_MaxFileCacheSizeMB`
2. `Initialise`: read both from config (after existing config block)
3. `UpdateMemoryCache`: add count guard at top
4. `DoCleanExpiredFiles`: add `EnforceFileCacheSizeLimit(gids)` call
5. New method: `EnforceFileCacheSizeLimit(Dictionary<UUID, sbyte> gids)`
6. `HandleConsoleCommand` `status` case: show memory limit if configured

---

## Verification Plan

### Memory tier (M-2)

1. Enable memory cache in config: `MemoryCacheEnabled = true`
2. Set a low limit: `MaxMemoryCacheCount = 10`
3. Start grid. `fcache status` should show "Memory Cache: 0 assets" and "Memory Cache limit: 10 items"
4. Log in with avatar. `fcache status` should show memory count capped at ≤ 10 entries
5. Verify avatar loads correctly (assets fall through to file tier)
6. Check OpenSim.log — no errors related to asset fetch

### File tier (M-13)

1. Enable cleanup: `FileCleanupTimer = 0.016` (≈ 1 minute for testing)
2. Set small limit: `MaxFileCacheSizeMB = 1` (1 MB)
3. Start grid and trigger some asset fetches (log in, load textures)
4. Wait for cleanup timer to fire. Check log for `Cache size ... exceeds limit ... evicting oldest files`
5. Verify `du -sh ./assetcache` stays near or below the configured limit after cleanup
6. Verify re-logging or re-rezzing works (assets re-fetched and re-cached)

### Regression check

```bash
grep -i "exception\|error.*cache\|flotsam" OpenSim.log | grep -v "^[[:space:]]*at " | tail -30
```

No new cache exceptions since startup. Hit rate (`fcache status`) should be plausible (>0% file hits after first minute).
