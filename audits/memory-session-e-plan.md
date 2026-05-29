# Collision Allocation Reduction — Recon and Plan

**Finding:** M-8 from `memory-resource-triage.md`
**Target:** Per-frame `List<uint>` allocations in collision handling
**Files:** `SceneObjectPart.cs`, `ScenePresence.cs`

---

## Collision data flow (Q1)

```
┌─────────────────────────────────────────────────────────────────────────┐
│  BulletS physics step  (physics thread OR heartbeat, per BSParam)       │
│                                                                          │
│  BSPhysObject.RecordCollidingWith()                                      │
│    └─ CollisionCollection.AddCollider(collideeLocalID, contact)          │
│       (accumulates all contacts for this object this step)               │
└──────────────────────────────┬──────────────────────────────────────────┘
                               │
                               │  step boundary
                               ↓
┌─────────────────────────────────────────────────────────────────────────┐
│  BSScene.SendUpdatesToSimulator()  [always on HEARTBEAT thread]          │
│                                                                          │
│  foreach BSPhysObject in ObjectsWithCollisions (under CollisionLock):    │
│    bsp.SendCollisions()                                                  │
│      ├─ passes CollisionCollection to OnCollisionUpdate.Invoke(e)        │
│      ├─ CollisionsLastReported = CollisionCollection  (retain old ref)   │
│      └─ CollisionCollection = new CollisionEventUpdate()   [ALLOC-A]     │
│                                                                          │
│  Also: foreach avatar in AvatarsInScene (always called, even no contact) │
└──────────────────────────────┬──────────────────────────────────────────┘
                               │
                               │  OnCollisionUpdate event delegate
                               ↓
┌─────────────────────────────────────────────────────────────────────────┐
│  SOP.PhysicsCollision(CollisionEventUpdate e)  [heartbeat thread]        │
│                                                                          │
│  reads e.m_objCollisionList : Dictionary<uint,ContactPoint>              │
│                                                                          │
│  List<uint> thisHitColliders = new(ncollisions)          [ALLOC-B]       │
│  List<uint> endedColliders   = new(m_lastColliders.Count)[ALLOC-C]       │
│  List<uint> startedColliders = new(ncollisions)          [ALLOC-D]       │
│                                                                          │
│  if (!VolumeDetectActive && CollisionSoundType >= 0):                    │
│    List<CollisionForSoundInfo> soundinfolist = new()     [ALLOC-E]       │
│                                                                          │
│  foreach id in collisionswith.Keys:                                      │
│    thisHitColliders.Add(id)                                              │
│    if !m_lastColliders.Contains(id): startedColliders.Add(id)  [O(n)]   │
│                                                                          │
│  foreach id in m_lastColliders:                                          │
│    if !thisHitColliders.Contains(id): endedColliders.Add(id)   [O(n)]   │
│                                                                          │
│  if scripts subscribed to collision events:                              │
│    SendCollisionEvent(collision_start, startedColliders, Trigger*)        │
│      └─ CreateColliderArgs(this, colliders)                              │
│           new ColliderArgs()                             [ALLOC-F]       │
│           new List<DetectedObject>()                     [ALLOC-G]       │
│           new DetectedObject() per collider              [ALLOC-H]       │
│         → EventManager.TriggerScriptCollidingStart(localID, args)        │
│           → OnScriptColliderStart.Invoke(...)                            │
│             → PhloxEngine.OnScriptColliderStart(...)                     │
│                 new DetectParams[dc]                     [ALLOC-I]       │
│                 new DetectParams() per collider          [ALLOC-J]       │
│                 new EventParams("collision_start", ...)  [ALLOC-K]       │
│                 new object[] { dc }                      [ALLOC-L]       │
│                 → PostObjectEvent(localID, eventParams)                   │
│                   (enqueued to script thread)                             │
│                                                                          │
│  [repeat for collision_end, collision, land variants]                    │
│                                                                          │
│  m_lastColliders = thisHitColliders    ← old list GC'd; B survives      │
└─────────────────────────────────────────────────────────────────────────┘

ScenePresence.PhysicsCollisionUpdate(e) [same heartbeat thread, same frame]
  │  (always called — avatars are always in ObjectsWithCollisions or
  │   in AvatarsInScene; needed for CollisionPlane ground contact)
  ├─ updates CollisionPlane (no significant allocation)
  └─ RaiseCollisionScriptEvents(coldata) — for avatar attachments only
       Guard: returns early if !ParcelAllowThisAvatarSounds && nattachments==0
       Otherwise: same 3 List<uint> pattern as SOP (lines 6505–6507)
                  + soundinfolist (line 6511) when ParcelAllowThisAvatarSounds
```

**Key conditional gate** (important for impact assessment):

`SOP.PhysicsCollision` is subscribed via `pa.OnCollisionUpdate += PhysicsCollision` only when
`UpdatePhysicsSubscribedEvents()` determines:
```
hassound  = (!VolumeDetectActive && CollisionSoundType >= 0 && flags.HasFlag(Physics))
OR
CombinedEvents has any collision/land_collision bit
```
`CollisionSoundType` defaults to `0` (≥ 0), so **any physical object plays collision sounds unless
explicitly disabled**, and pays ALLOC-B/C/D/E every frame it contacts something.
For non-physical objects with no collision scripts, no callback is registered at all — they pay
nothing.

---

## Allocation inventory (Q2)

| ID | File : line | What | Lifetime | Frequency |
|----|-------------|------|----------|-----------|
| A | BSPhysObject.cs:630 | `new CollisionEventUpdate()` (wraps `new Dict<uint,ContactPoint>`) | Held in `CollisionsLastReported` for one frame | 1 per active-collision BSPhysObject per frame |
| **B** | SOP:2802 | `new List<uint>(ncollisions)` — `thisHitColliders` | Survives as `m_lastColliders` for next frame | **1 per subscribed-physical SOP per frame** |
| **C** | SOP:2803 | `new List<uint>(m_lastColliders.Count)` — `endedColliders` | Scratch — discarded after SendCollisionEvent | **1 per subscribed-physical SOP per frame** |
| **D** | SOP:2804 | `new List<uint>(ncollisions)` — `startedColliders` | Scratch — discarded after SendCollisionEvent | **1 per subscribed-physical SOP per frame** |
| E | SOP:2810 | `new List<CollisionForSoundInfo>()` — `soundinfolist` | Scratch — discarded after CollisionSounds call | 1 per sound-enabled physical SOP per frame (when ncollisions > 0) |
| SP-B | ScenePresence:6505 | same as B | same | 1 per avatar per frame (if ParcelAllowThisAvatarSounds or has attachments) |
| SP-C | ScenePresence:6506 | same as C | same | same |
| SP-D | ScenePresence:6507 | same as D | same | same |
| SP-E | ScenePresence:6511 | `new List<CollisionForSoundInfo>()` | Scratch | 1 per avatar per frame (if ParcelAllowThisAvatarSounds) |
| F | SOP:2692 | `new ColliderArgs()` | Scratch — to script engine | Per SendCollisionEvent call when scripts subscribed |
| G | SOP:2693 | `new List<DetectedObject>()` | Scratch — to script engine | Same |
| H | SOP:2704/2712 | `new DetectedObject()` per collider | Held by ColliderArgs until consumed | Per collider per event with scripts subscribed |
| I | PhloxEngine:413/429/445 | `new DetectParams[dc]` + `new DetectParams()` per collider | Held in EventParams | Per Phlox collision event posted |
| J | PhloxEngine:422/438/454 | `new EventParams(...)` + `new object[]{dc}` | Held in script queue | Per Phlox event posted |

**B, C, D = the "3 List\<uint\>" cited by M-8 triage.** SP-B/C/D are the mirror in ScenePresence.

**Allocation A** (BulletS) — the comment at BSPhysObject:626 explains this cannot be cleared in
place because `CollisionCollection` is a reference passed around the simulator, and calling
`Clear()` would race with other users of the same instance. Fixing A is out of scope (BulletS
internals) and higher risk.

**Allocations F–J** — conditional on script subscription and are per-event, not per-frame. Out of
scope for M-8 (different fix: pool DetectedObject / DetectParams — separate audit).

**Contains() O(n) calls** (not allocations, but related perf):
- SOP:2838, 2872 — `m_lastColliders.Contains(id)`: O(n) per current collider
- SOP:2881 — `thisHitColliders.Contains(localID)`: O(n) per last collider
- ScenePresence:6540, 6572, 6581 — same pattern
- For ≤5 simultaneous colliders (typical): negligible. For dense vehicle contact (10+ contact
  points): measurable. Not fixed in M-8 but documented for a follow-up (change `m_lastColliders`
  to `HashSet<uint>` + update SendCollisionEvent signature).

---

## Necessary vs. avoidable (Q3)

| ID | Verdict | Reasoning |
|----|---------|-----------|
| A | Necessary (as-is) | BulletS correctness constraint: can't Clear() in place, see BSPhysObject:626 comment |
| **B** | Avoidable | Content must persist, but the List object needn't be reallocated — ping-pong two pre-allocated lists |
| **C** | Avoidable | Pure scratch. Reused per-SOP field, cleared each call |
| **D** | Avoidable | Same as C |
| E | Avoidable | `CollisionForSoundInfo` is a struct (no per-item alloc). Only the `List<>` container is wasted. Reused field |
| SP-B/C/D/E | Avoidable | Identical reasoning, same fix in ScenePresence |
| F–J | Deferred | Script-path allocations; separate concern |

**Structural observation for B:**
`m_lastColliders = thisHitColliders` (SOP:2914, ScenePresence:6613) assigns the locally-built
list to the persistent field, GC-ing the old m_lastColliders. With two persistent fields that
swap references each frame, zero allocation is needed:
```
(m_lastColliders, m_thisHitBuffer) = (m_thisHitBuffer, m_lastColliders)
```
Both lists remain allocated indefinitely; their contents are swapped. This is the same pattern
used by double-buffering render targets.

---

## Concurrency model (Q4)

**Which threads call PhysicsCollision / PhysicsCollisionUpdate?**

`SOP.PhysicsCollision` and `ScenePresence.PhysicsCollisionUpdate` are both called from
`BSScene.SendUpdatesToSimulator()`, which is called from `BSScene.Simulate()`, which is called
from the Scene heartbeat thread.

Two BulletS configurations exist:
- `UseSeparatePhysicsThread = false` (default): heartbeat calls `DoPhysicsStep()` then
  `SendUpdatesToSimulator()` — both on heartbeat.
- `UseSeparatePhysicsThread = true`: BulletSPluginPhysicsThread calls `DoPhysicsStep()`;
  heartbeat still calls `Simulate()` → `SendUpdatesToSimulator()` — so SOP callbacks still
  on heartbeat.

**Is it re-entrant?**
No. The foreach loop in `SendUpdatesToSimulator` at BSScene:806 iterates `ObjectsWithCollisions`
sequentially — no task/thread parallelism. One SOP's `PhysicsCollision` fully returns before the
next starts.

**Are there other threads accessing `m_lastColliders`?**
`m_lastColliders` is only read/written in:
- `PhysicsCollision` — heartbeat
- `UpdatePhysicsSubscribedEvents` — heartbeat (called from `aggregateScriptEvents`, which is
  called from the heartbeat during property updates)

No script thread accesses `m_lastColliders` directly.

**Conclusion: Per-SOP reused buffer fields are safe without any locking.**

Any future parallelization of `SendUpdatesToSimulator` (e.g., parallel foreach) would invalidate
this conclusion and require revisiting. The current code has no such parallelism.

---

## Proposed fix (Q5)

### Option A — Per-object ping-pong + scratch buffers (Recommended)

**Concept:** Add 3 new `List<T>` fields to `SceneObjectPart` (and `ScenePresence`). Two lists
ping-pong the "current colliders / last colliders" role by swapping references. Two lists act as
persistent scratch for ended/started sets, cleared at start of each call.

**Fields to add to SOP** (near `m_lastColliders` at line 291):
```csharp
private List<uint> m_thisHitBuffer     = new List<uint>(); // ping-pong partner for m_lastColliders
private List<uint> m_endedScratch      = new List<uint>(); // reusable scratch for endedColliders
private List<uint> m_startedScratch    = new List<uint>(); // reusable scratch for startedColliders
private List<CollisionForSoundInfo> m_soundInfoScratch = new List<CollisionForSoundInfo>(); // optional E fix
```

**PhysicsCollision changes** (replacing local-var declarations at lines 2802–2804):
```csharp
// BEFORE (allocates each call):
List<uint> thisHitColliders = new List<uint>(ncollisions);
List<uint> endedColliders   = new List<uint>(m_lastColliders.Count);
List<uint> startedColliders = new List<uint>(ncollisions);

// AFTER (no allocation):
m_thisHitBuffer.Clear();
m_endedScratch.Clear();
m_startedScratch.Clear();
// (rename all uses of thisHitColliders→m_thisHitBuffer, endedColliders→m_endedScratch,
//  startedColliders→m_startedScratch throughout the method)
```

For soundinfolist (line 2810):
```csharp
// BEFORE:
List<CollisionForSoundInfo> soundinfolist = new List<CollisionForSoundInfo>();
// AFTER:
m_soundInfoScratch.Clear();
// (rename soundinfolist→m_soundInfoScratch)
```

**End-of-method swap** (replacing line 2914):
```csharp
// BEFORE:
m_lastColliders = thisHitColliders;

// AFTER (reference swap, zero allocation):
(m_lastColliders, m_thisHitBuffer) = (m_thisHitBuffer, m_lastColliders);
// m_lastColliders now holds this frame's colliders.
// m_thisHitBuffer holds the former m_lastColliders — will be Clear()d at next call start.
```

The zero-collision early-return path (lines 2780–2796) already calls `m_lastColliders.Clear()` —
no change needed there; `m_thisHitBuffer` is not involved in that path.

**Same pattern in ScenePresence** — in `RaiseCollisionScriptEvents` at lines 6505–6507 and 6613:
```csharp
private List<uint> m_thisHitBuffer  = new List<uint>();
private List<uint> m_endedScratch   = new List<uint>();
private List<uint> m_startedScratch = new List<uint>();
private List<CollisionForSoundInfo> m_soundInfoScratch = new List<CollisionForSoundInfo>();
```

**Memory cost:**
In .NET 8, `new List<T>()` with no elements uses `Array.Empty<T>()` internally — no backing array
until first `Add()`. List object header is ~40 bytes. 4 new fields per SOP = ~160 bytes per SOP.
For 50,000 SOPs (large scene, mostly static): ~8 MB overhead. However, these lists grow backing
arrays only for physical SOPs that actually receive collisions. Non-physical SOPs have these
fields but they're never populated.

**Option: lazy init** — only allocate in `UpdatePhysicsSubscribedEvents` on subscription and set
to null on unsubscription. Saves the 8MB overhead but adds null-checks in `PhysicsCollision`.
Given the existing empty-list cost is already paid for `m_lastColliders` (line 291), and
`Array.Empty<T>` backing is a shared singleton, the eager approach is acceptable.

### Option B — ArrayPool for scratch lists only

Rent `uint[]` from `ArrayPool<uint>.Shared` in PhysicsCollision, return in finally block. Only
fixes C and D (the pure scratch lists). B still allocates (thisHitColliders must survive the
frame as m_lastColliders, so it can't be returned to the pool).

Pro: No per-SOP memory cost.
Con: Fixes 2 of 3 target allocations. Requires array + count tracking instead of List API.
`try/finally` wrapping the whole method. Marginally more complex to read.

### Option C — HashSet for m_lastColliders

Change `m_lastColliders` from `List<uint>` to `HashSet<uint>`. Eliminates the O(n) Contains()
calls (lines 2838, 2872, 2879, 2881). Does NOT eliminate the List<uint> allocations — those
would remain (or also need fixing via A/B).

Pro: Fixes the O(n) lookup performance issue separately documented in Q2.
Con: `SendCollisionEvent` takes `List<uint>` — signature would need to change to
`IReadOnlyCollection<uint>` or `IEnumerable<uint>`. More interface surface change.

**Recommendation: Option A.**
- Eliminates all 3 target allocations (B, C, D) plus the bonus sound list (E)
- No interface changes
- Minimal code delta (~25 lines changed per file)
- Safe: heartbeat-thread-only, no races
- Clear() is the critical correctness gate — placing it at the very top of the method before any
  branching ensures stale data cannot leak

**Option C (HashSet) is worth a follow-up** but would increase scope of M-8 and is lower priority
than the allocation fix.

---

## Measurement strategy (Q6)

**Pre/post baseline:**
```
# during a 60-second stress test, poll GC counts:
Console> gc stats    # if available via console command
# or add temporary diagnostic log in Scene heartbeat:
m_log.InfoFormat("[PHYSICS BENCH] Gen0={0} Gen1={1} Gen2={2}",
    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
```

**dotnet-counters (preferred):**
```bash
dotnet-counters monitor --name OpenSim --counters System.Runtime[gen-0-gc-count,gen-0-size,alloc-rate]
```
Run for 60 seconds under load before patch, then same 60 seconds after.

**Test scenario:**
1. **Object pile:** Rez 200 physical prims, 0.5 kg each, in a 10m × 10m area so they settle into
   a heap. All prims have collision sounds enabled (default) and at least one has a collision_start
   script. Let them settle for 10 seconds (to exhaust started/ended events), then measure during
   steady ongoing collisions.
2. **Vehicle:** Place a physical vehicle on a mesh road with collision sound enabled and a
   collision script. Drive it 60 seconds. Vehicle body + wheel joints generate 4–6 contact points
   per frame.

**Success metric:**
- Gen0 collection count during a 60-second pile test drops by ≥ 20%.
- At 200 physical objects × 60 Hz × (3 × 28-byte List headers + backing arrays) ≈ 200 × 60 × ~200 bytes = ~2.4 MB/s of short-lived List allocation eliminated. This should produce a measurable Gen0 reduction.
- Correctness: run the pile test with a collision_start / collision / collision_end script that
  counts events and llSay reports. Verify started-count and ended-count are symmetric over a
  drop-and-settle cycle (no phantom starts or doubled ends).

---

## Risk assessment (Q7)

### Risk 1: Stale data from missed Clear() (Correctness — HIGHEST priority)

If `m_thisHitBuffer`, `m_endedScratch`, or `m_startedScratch` are not cleared at the top of
`PhysicsCollision` before any branching, the previous frame's data leaks into this frame.
- **Stale `m_endedScratch`**: would send spurious collision_end events for objects not actually
  ending collision — script gets false notification.
- **Stale `m_startedScratch`**: would send spurious collision_start events — objects appear to
  start colliding again every frame.
- **Stale `m_thisHitBuffer`**: would cause incorrect ended/started set-difference computation —
  cascading errors.

**Mitigation**: Place all four `.Clear()` calls unconditionally at the very top of
`PhysicsCollision`, before the `if (ncollisions == 0)` branch. The early-return path
(ncollisions == 0) never populates these buffers, so clearing them at the top costs one
`List.Count = 0` check per call when empty — negligible.

### Risk 2: Thread safety if SendUpdatesToSimulator is ever parallelized (Correctness — MEDIUM)

The per-SOP reused buffers are safe today (heartbeat thread, sequential). If a future contributor
parallelizes the foreach in `SendUpdatesToSimulator` (BSScene:806), the per-SOP fields would race
without locking. The collision path is already performance-critical, so parallelizing it is
plausible.

**Mitigation**: This risk exists for the existing `m_lastColliders` field too — it's not a new
hazard introduced by the fix. Add a comment to `PhysicsCollision` noting the heartbeat-thread-only
assumption, so any future parallelization decision surfaces this dependency.

### Risk 3: Ping-pong swap at wrong point (Correctness — LOW with careful placement)

The `(m_lastColliders, m_thisHitBuffer) = (m_thisHitBuffer, m_lastColliders)` swap must happen
AFTER all collision event dispatch. The `collision` event (ongoing) at SOP:2898 passes
`m_lastColliders` — which, at that point in the original code, still refers to the PREVIOUS
frame's colliders. If the swap were placed before line 2898, `m_lastColliders` would have the
CURRENT frame's colliders, which would double-report ongoing collisions.

**Mitigation**: Place the swap exactly where line 2914 (`m_lastColliders = thisHitColliders`)
currently appears — at the very end of the method, after all SendCollisionEvent calls. The
reference naming makes this explicit: `m_thisHitBuffer` is visibly "being built" above the swap,
and `m_lastColliders` is visibly "the old state" being read for collision_end and ongoing.

---

## Implementation sequence

### Files to change

| # | File | Change | Estimated lines |
|---|------|--------|-----------------|
| 1 | `OpenSim/Region/Framework/Scenes/SceneObjectPart.cs` | Add 4 field declarations near line 291; modify PhysicsCollision (~lines 2802–2804, 2810, 2914) | ~18 lines changed |
| 2 | `OpenSim/Region/Framework/Scenes/ScenePresence.cs` | Add 4 field declarations near line 350; modify RaiseCollisionScriptEvents (~lines 6505–6507, 6511, 6613) | ~18 lines changed |

**Total: ~36 lines changed, single commit.**

### Change checklist (per file)

1. Add field declarations (`m_thisHitBuffer`, `m_endedScratch`, `m_startedScratch`,
   `m_soundInfoScratch`) with initializers matching `m_lastColliders` pattern.
2. At top of PhysicsCollision (before ncollisions==0 branch): add four `.Clear()` calls.
3. Remove the three local `List<uint>` declarations (lines 2802–2804 / 6505–6507).
4. Remove the local `List<CollisionForSoundInfo>` declaration (line 2810 / 6511).
5. Rename all references to the local variables to use the buffer fields.
6. Replace `m_lastColliders = thisHitColliders` (line 2914 / 6613) with the reference swap.
7. Verify: grep for any remaining `thisHitColliders`, `endedColliders`, `startedColliders`,
   `soundinfolist` (local var names) to confirm no stragglers.

### Staging

Single commit. Both files implement the same pattern; testing one without the other would give
an incomplete picture (avatar collision path is half the allocation source in avatar-heavy regions).

### Do NOT touch

- `PhysicsActor.OnCollisionUpdate` — event delegate, no change
- `EventManager.TriggerScriptColliding*` — signatures unchanged
- `CollisionEventUpdate` — input to PhysicsCollision, unchanged
- `SendCollisionEvent` — takes `List<uint>` — unchanged (Option A avoids signature change)
- `BSPhysObject.SendCollisions` — physics layer, out of scope
- Phlox `PhloxEngine.cs` collision handlers — separate concern (allocations I, J)

---

## Summary

M-8 is a clean, low-risk allocation fix. The three `List<uint>` per subscribed-physical-SOP per
frame (B, C, D) eliminate to zero allocation with a 4-field addition and a reference swap. The
same pattern in `ScenePresence` covers the avatar path. Thread safety is confirmed: heartbeat
thread only, sequential, no concurrent access.

At 60 Hz with 100 active-collision objects plus 20 avatars, the fix eliminates approximately
(100 + 20) × 60 × 3 ≈ **21,600 `List<uint>` allocations per second**, plus 120 × 60 = 7,200
sound-info list allocations. Each `List<uint>` header is ~40 bytes in .NET; total saved:
~21,600 × 40 = **~864 KB/s** removed from the Gen0 allocation rate on the heartbeat thread.
This is a meaningful contributor to Gen0 frequency reduction on physics-heavy regions.

The fix is purely internal to `SceneObjectPart` and `ScenePresence`. No IScriptEngine interface
changes, no physics-actor interface changes, no event semantics changes.
