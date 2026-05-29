# Memory Session B — Http3AssetService Cache Refactor Plan

Date: 2026-05-27  
File: `OpenSim/Services/AssetService/Http3AssetService.cs`  
Scope: Unified CacheEntry (M-10, M-11, M-12), M-1 (m_OwnerRequestsThrottle) triage  
No code changes in this document.

---

## Q1 — Why are m_assetCache and m_accessTimes split?

**Short answer: the split is structural redundancy with no design benefit.**

`m_assetCache` (line 64) stores `Http3CachedAsset`, which already contains:
- `CachedAt` (DateTime)
- `Size` (long)
- `AccessCount` (int)

`m_accessTimes` (line 65) stores `DateTime` — a separate copy of the last-access timestamp for LRU sorting.

The split was presumably introduced to allow `EvictLeastRecentlyUsedAsync` to sort access times without iterating the full cache value objects. But since `Http3CachedAsset` already carries `CachedAt` (used as the LRU timestamp in practice via `m_accessTimes`), the second dictionary is pure duplication. There is no advantage; the split only creates cross-dictionary consistency hazards (see Q3).

---

## Q2 — Full Access Site Inventory

### m_assetCache access sites

| Site | Line | Operation | Semaphore? | Also updates m_accessTimes? |
|------|------|-----------|------------|----------------------------|
| `GetFromCacheAsync` | 626 | `TryGetValue` | No (read) | Yes (AddOrUpdate) |
| `GetFromCacheAsync` (expiry) | 641 | `TryRemove` | Yes | **No — ghost left** |
| `GetMetadata` | 265 | `TryGetValue` | No | **No** |
| `GetCached` | 294 | `TryGetValue` | No | **No** |
| `GetCached` (expiry) | 304 | `TryRemove` | No | **No — ghost left** |
| `CacheAssetAsync` (count check) | 669 | `.Count` | Yes | — |
| `CacheAssetAsync` (size check) | 669 | `GetCacheSizeMB()` iterates `.Values` | Yes | — |
| `CacheAssetAsync` (add) | 674 | `TryAdd` | Yes | Yes (TryAdd) |
| `EvictLeastRecentlyUsedAsync` | 697 | `TryRemove` (per evicted key) | Yes (caller) | Yes (TryRemove) |
| `UpdateContent` | 436 | `TryRemove` | No | **No — ghost left** |
| `Delete` | 461 | `TryRemove` | No | **No — ghost left** |
| `AssetsExist` | 366 | `ContainsKey` | No | No |
| `GetCacheSizeMB` | 703–709 | iterate `.Values` | No | — |
| `PerformMaintenance` (scan) | 788–791 | iterate `.Values` | No | — |
| `PerformMaintenance` (remove) | 803 | `TryRemove` | Yes | **No — ghost left** |

### m_accessTimes access sites

| Site | Line | Operation | Note |
|------|------|-----------|------|
| `GetFromCacheAsync` | 632 | `AddOrUpdate` | **Outside** semaphore |
| `CacheAssetAsync` | 675 | `TryAdd` | Under semaphore |
| `EvictLeastRecentlyUsedAsync` | 688 | `OrderBy().Take().Select().ToList()` | O(n log n) — full sort |
| `EvictLeastRecentlyUsedAsync` | 699 | `TryRemove` per key | Under semaphore (caller) |

**Key observations:**
- Five code paths remove from `m_assetCache` without removing the matching entry from `m_accessTimes`, leaving orphan/ghost entries that bloat the LRU sort over time.
- `GetFromCacheAsync` updates `m_accessTimes` outside the semaphore (line 632 before line 638–646).
- `cached.AccessCount++` (line 634) is a non-atomic read-modify-write on `int`.
- `GetCacheSizeMB()` does an O(n) iteration on every `CacheAssetAsync` call (called at store time and again in maintenance stats).

---

## Q3 — Atomicity Guarantees: Current vs. Gaps

### What the semaphore does

`m_cacheSemaphore` (SemaphoreSlim(1,1)) serializes the add + evict path in `CacheAssetAsync`. Within that critical section, the check-then-act for size/count limits is atomic with the subsequent `TryAdd`. That part is correct.

### Gaps

**Gap 1 — Cross-dictionary consistency on remove:**  
`UpdateContent`, `Delete`, `GetCached` (expiry), and `PerformMaintenance` all call `m_assetCache.TryRemove` without touching `m_accessTimes`. This leaves `m_accessTimes` with keys that no longer exist in `m_assetCache`. These ghost entries are sorted in eviction sweeps, selected as "least recently used", and then `m_assetCache.TryRemove` is a no-op (key already gone) — harmless but wasted work. Over time ghost entries accumulate, making the eviction sort increasingly expensive.

**Gap 2 — AccessCount non-atomic:**  
`cached.AccessCount++` (line 634) compiles to a read-modify-write sequence. Concurrent readers can lose increments. This is minor (AccessCount is currently unused for eviction decisions), but will be a bug if it's ever used for LFU.

**Gap 3 — m_accessTimes update outside semaphore on hit path:**  
`GetFromCacheAsync` updates `m_accessTimes` at line 632 before acquiring the semaphore (line 638). This creates a window where a concurrent eviction thread (which holds the semaphore) might choose this key for eviction, remove it from `m_assetCache`, then the hit-path finishes writing to `m_accessTimes` — leaving a ghost for an entry that was just evicted.

**Gap 4 — Size counter O(n) per store:**  
`GetCacheSizeMB()` iterates all `.Values` on every `CacheAssetAsync` call (line 669) and again on every maintenance tick's stats log (line 825). On a 10,000-entry cache this is 10,000 object reads per asset fetch that misses cache and triggers a store.

---

## Q4 — Cache-Entry Lifecycle

### Store
1. Caller: `GetAsync` (post HTTP/3 or fallback success) or `Store(IAssetService)`
2. `CacheAssetAsync` acquires semaphore
3. Checks `m_assetCache.Count >= m_maxCacheSize` OR `GetCacheSizeMB() >= m_maxCacheSizeMB` (O(n) scan)
4. If over limit: `EvictLeastRecentlyUsedAsync` — sorts `m_accessTimes` O(n log n), evicts 10%
5. `m_assetCache.TryAdd` + `m_accessTimes.TryAdd`
6. Release semaphore

### Cache Hit
- `GetFromCacheAsync`: `TryGetValue` → checks `CachedAt + cacheExpiry` → updates `m_accessTimes`, increments `AccessCount++` (non-atomic)
- `GetMetadata`: `TryGetValue` only — no LRU update, no expiry check
- `GetCached`: `TryGetValue` → checks `CachedAt + cacheExpiry` — no LRU update
- `AssetsExist`: `ContainsKey` — no LRU update, no expiry check

### Cache Miss
- Falls through to HTTP/3 then fallback service. On success, stores result.

### Eviction
- **Demand-driven** (in `CacheAssetAsync`): 10% batch, O(n log n) sort of `m_accessTimes`
- **Expiry-driven** (in `GetFromCacheAsync` and `GetCached`): removes from `m_assetCache` only → ghost in `m_accessTimes`
- **Maintenance** (every 5 min via Timer): iterates `m_assetCache` for expired entries, removes from `m_assetCache` under semaphore → ghost in `m_accessTimes`

### Shutdown
- `Dispose()` disposes semaphore, timer, HTTP client. Does NOT clear `m_assetCache` or `m_accessTimes`. No graceful drain of in-flight cache tasks (fire-and-forget `Task.Run` from `Store`).

---

## Q5 — Proposed Unified CacheEntry Design

### Unified struct

```csharp
private sealed class CacheEntry
{
    public AssetBase Asset;
    public long      SizeBytes;
    public long      CachedAtTicks;      // DateTime.UtcNow.Ticks at store time
    public long      LastAccessTicks;    // Interlocked.Exchange on every hit
    public int       AccessCount;        // Interlocked.Increment on every hit
}

private readonly ConcurrentDictionary<string, CacheEntry> m_cache = new();
private long m_cacheSizeBytes = 0;       // Interlocked-tracked total
private int  m_evicting = 0;             // CAS guard: 0=idle, 1=evicting
```

### Concurrency model

**Reads** (hit path):  
`m_cache.TryGetValue` — no lock, ConcurrentDictionary provides thread-safe read.  
On hit: `Interlocked.Exchange(ref entry.LastAccessTicks, DateTime.UtcNow.Ticks)` and `Interlocked.Increment(ref entry.AccessCount)`.  
Expiry check: if expired, `m_cache.TryRemove` → `Interlocked.Add(ref m_cacheSizeBytes, -entry.SizeBytes)`.

**Writes** (store path):  
Drop the `SemaphoreSlim`. Instead:
1. `m_cache.TryAdd(id, entry)` — ConcurrentDictionary handles concurrent adds (first one wins, fine for cache)
2. `Interlocked.Add(ref m_cacheSizeBytes, entry.SizeBytes)` after successful add
3. After add, check count/size: if over limit, attempt eviction with CAS guard

**Eviction**:
```csharp
if (Interlocked.CompareExchange(ref m_evicting, 1, 0) == 0)
{
    try { EvictBatch(); }
    finally { Volatile.Write(ref m_evicting, 0); }
}
```
Only one thread evicts at a time. If the CAS fails (another thread already evicting), skip — the cache will be re-checked on the next store.

### LRU strategy: sample-based pseudo-LRU

Replace O(n log n) full sort with a **random sample approach**:
1. Collect random sample of 2× `evictCount` keys from `m_cache`
2. Among the sample, find the `evictCount` entries with smallest `LastAccessTicks`
3. Remove those entries

This is O(sample_size) — effectively O(1) for a fixed evictCount. Redis uses the same strategy (its default since 3.0). The approximation error is bounded and shrinks as sample size grows.

Sample implementation note: `ConcurrentDictionary` doesn't support random-index access. Use `m_cache.Skip(random_offset).Take(2 * evictCount)` — biased toward key ordering but acceptable for cache eviction.

### Size tracking

`GetCacheSizeMB()` becomes a single `Interlocked.Read(ref m_cacheSizeBytes) / (1024*1024)` — O(1). The method body and every call site that currently iterates `.Values` are eliminated.

---

## Q6 — M-1 (m_OwnerRequestsThrottle) Integration

### Current state (ScriptsHttpRequests.cs)

`m_OwnerRequestsThrottle` (line 92): `ConcurrentDictionary<UUID, ThrottleData>` keyed by `ownerID`.  
`m_RequestsThrottle` (line 91): `ConcurrentDictionary<uint, ThrottleData>` keyed by `localID`.

`StopHttpRequest(uint localID, UUID m_itemID)` (line 451):
- Cleans `m_pendingRequests` (locks `m_mainLock`, removes all requests for the item)
- Cleans `m_RequestsThrottle`: calls `TryRemove(localID)` if the throttle has recharged (line 467–471)
- Does **NOT** touch `m_OwnerRequestsThrottle` — no ownerID available at this call site

### Why this is NOT an Http3AssetService concern

`ScriptsHttpRequests.cs` is in `OpenSim.Region.CoreModules.Scripting.HttpRequest`. It is entirely independent of `Http3AssetService` (which is in `OpenSim.Services.AssetService`). The two modules do not share state. M-1 cannot be fixed by Session B's Http3 refactor.

### Recommended fix approach (standalone, not Session B)

**Option B** (add reverse-map): At `StartHttpRequest` time, store `localID → ownerID` in a new `ConcurrentDictionary<uint, UUID> m_localToOwner`. At `StopHttpRequest`, after removing from `m_RequestsThrottle`, also do:

```csharp
if (m_localToOwner.TryRemove(localID, out UUID ownerID))
    m_OwnerRequestsThrottle.TryRemove(ownerID, out _);
```

This requires that the `StartHttpRequest` call site passes ownerID (it already does — `CheckThrottle(uint localID, UUID ownerID)` is called just before, so ownerID is available in scope).

**Caveat**: The reverse map assumes one ownerID per localID. This is correct — a prim has one owner. Even if ownership changes between start and stop (edge case), the worst outcome is a stale throttle entry for the old owner, which is the current behavior anyway.

**Session B decision**: Mark M-1 as **standalone fix, not Session B**. Estimated 10–15 line change in ScriptsHttpRequests.cs. Can be done in its own small commit after Session B.

---

## Q7 — Backwards Compatibility

`Http3AssetService` is a Legion Grid addition with no deployed users yet. The cache is in-memory only — nothing persisted to disk, no serialization format to preserve. The unified `CacheEntry` class is private and internal to the service.

The `IAssetService` public interface (`Get`, `Store`, `Delete`, etc.) remains unchanged — all public method signatures are kept. The refactor only changes private fields and private methods.

**No backwards compatibility concerns for the cache refactor.** The maintenance timer callback's log format changes slightly (size from O(1) counter vs O(n) scan), but that is log output, not a contract.

---

## Q8 — Top 3 Migration Risks + Mitigation

### Risk 1 — Partial refactor leaves mixed state

If `m_assetCache`/`m_accessTimes` are replaced with unified `m_cache` but some access sites are missed (e.g., `GetMetadata`, `GetCached`, `AssetsExist`, `UpdateContent`, `Delete`), the refactor will compile and appear to work but still have ghost-entry or missed-LRU-update bugs.

**Mitigation**: Delete `m_assetCache`, `m_accessTimes`, and `m_cacheSemaphore` fields entirely in the first edit. Every reference to the old fields becomes a compile error, forcing all sites to be updated before the build passes. Complete replacement in one commit, not incremental.

### Risk 2 — Fire-and-forget cache tasks after Dispose

`Store()` (line 413) fires `Task.Run(() => CacheAssetAsync(...))` with no cancellation token. If `Dispose()` is called while a cache task is in flight, the task may call `m_cache.TryAdd` after `m_cacheSizeBytes` accounting has been abandoned. This is a latent bug in the current design too, but the refactor should not make it worse.

**Mitigation**: Add a `CancellationTokenSource m_cts` in the constructor; pass `m_cts.Token` to all fire-and-forget tasks; cancel in `Dispose()`. Low-risk addition since the tasks are short-lived and `ConcurrentDictionary.TryAdd` after dispose is harmless (no external side effects).

### Risk 3 — Sample-based eviction under-evicts on pathological access patterns

If all sampled keys have recent access times (e.g., the cache is very active and the 2×evictCount sample all happen to be hot entries), eviction removes the "least bad" of the hot set rather than truly cold entries. In the worst case the cache overshoots its size limit temporarily.

**Mitigation**: Set sample size to `4 × evictCount` (Redis uses 5). Additionally, keep the maintenance timer's full-scan expiry sweep as a backstop — expired entries (regardless of access recency) are always hard-evicted by the 5-minute timer, which bounds the absolute worst-case growth to 5 minutes × store rate.

---

## Implementation Sequence for Session B

1. **Define** unified `CacheEntry` sealed class with `Interlocked`-safe fields
2. **Delete** `m_assetCache`, `m_accessTimes`, `m_cacheSemaphore` fields → compile errors guide remaining work
3. **Add** `m_cache` + `m_cacheSizeBytes` + `m_evicting` fields
4. **Rewrite** `GetFromCacheAsync`, `CacheAssetAsync`, `EvictLeastRecentlyUsedAsync`, `GetCacheSizeMB`
5. **Update** all remaining access sites: `GetMetadata`, `GetCached`, `UpdateContent`, `Delete`, `AssetsExist`, `PerformMaintenance`
6. **Remove** `GetCacheSizeMB()` method after all call sites are updated to use `Interlocked.Read`
7. Add `CancellationTokenSource` for fire-and-forget task hygiene (optional but recommended)

**Estimated edit count**: ~15–20 targeted changes across the single file.  
**M-1**: Separate commit in ScriptsHttpRequests.cs after Session B is done.
