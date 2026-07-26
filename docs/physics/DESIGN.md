# Legion Physics Backend — design notes

## Standing constraints (M6.7+)

- **Portability: LegionJolt must run unchanged in BOTH Legion AND Tranquillity** (the Mike/NGC
  OpenSim-derived tree). Discipline: the region module uses ONLY the STANDARD OpenSim physics surface —
  `PhysicsScene` / `PhysicsActor` / `ScenePresence` / `SceneObjectPart` / `SceneObjectGroup` — never a
  Legion-only extension or hook. The `Legion.Physics` backend is already OpenSim-agnostic (no OpenSim
  types at all); keep it that way. Decision: ASSUME PORTABLE (shared OpenSim contract), VERIFY AT DEPLOY
  — no audit now, but FLAG any fix that would need a Legion-specific hook so it can be reworked to the
  standard surface before it lands.
- **Phlox is the script engine.** Legion runs InWorldz/Halcyon **Phlox** (`DefaultScriptEngine =
  "InWorldz.Phlox"`), so script-facing physics (`llSensor`, `llCastRay`, `llSitTarget`, collision
  events) is validated on Phlox, not XEngine/YEngine — we do not certify on an engine John will never
  run.
- **Phlox runtime closure (beyond the base OpenSim `bin/` set)** — what a Phlox-enabled scratch OR
  linux-x64 production deploy must carry, and NuGet-only (not in the OpenSim `bin/`):
  - **SQLite** (script-state persistence): `Microsoft.Data.Sqlite` 10.0.7 + `SQLitePCLRaw.core` /
    `.provider.e_sqlite3` / `.batteries_v2` 2.1.11 + the **native** `e_sqlite3` (per-RID:
    `runtimes/win-x64|linux-x64/native/e_sqlite3`).
  - **Scheduler / compiler**: `C5` **3.0.0.0**, `Antlr4.Runtime.Standard` 4.13.1, `Antlr4.StringTemplate`
    4.0.7, `protobuf-net` + `protobuf-net.Core` 3.0.0.
  These are LAZY deps (only throw when the scheduler/persistence path first fires), so a Phlox deploy
  must be validated by RUNNING a script + persistence, not just booting.
- **⚠ Source-tree C5 inconsistency (flag for the production deploy).** In the current tree the Phlox
  binaries disagree on C5: `InWorldz.Phlox.dll` (a stale build) references **C5 3.0.0.0** while the newer
  `Phlox.ScriptEngine.dll` and `bin/C5.dll` are **C5 1.1.0.0** — so a Phlox boot from this `bin/` crashes
  (`Could not load C5, Version=3.0.0.0`). The scratch fix ships C5 **3.0.0.0** (satisfies the stale
  `InWorldz.Phlox`; C5 is not strong-named and API-stable, so `Phlox.ScriptEngine` loads it by name). The
  PROPER fix is to rebuild `InWorldz.Phlox` against the tree's current C5 so all Phlox binaries agree —
  needed before Phlox+Jolt ship together.

## Layering

```
OpenSim Scene
      │
      ▼
PhysicsScene / PhysicsActor          ← OpenSim's contract. Unchanged.
      │
      ▼
LegionPhysicsScene                   ← translation layer. SL semantics live HERE.
      │                                 vehicle model, llCastRay filter rules,
      │                                 collision event dispatch, permissions,
      │                                 avatar movement/animation coupling
      ▼
ILegionPhysicsBackend                ← the seam. Rigid bodies only, no SL.
      │
      ├── JoltPhysicsBackend         ← first implementation
      └── (BulletSimBackend)         ← optional shim for A/B parity testing
```

The rule that keeps this honest: **nothing below `ILegionPhysicsBackend` may
reference an OpenSim type, and nothing above it may reference a Jolt type.**
If you find yourself wanting to leak one through, the abstraction is wrong —
fix the interface rather than punching a hole.

## Why handles instead of objects

A busy region holds tens of thousands of prims. An object-per-body design
allocates a managed wrapper for each, which means GC pressure proportional to
region content and a pointer chase on every access. `BodyId` is a 4-byte
blittable struct that maps onto Jolt's own `BodyID`.

Handles are generation-tagged (24 bits index, 8 bits generation). A handle to a
removed body fails validation rather than addressing whatever was allocated into
that slot afterwards. This costs one comparison and eliminates an entire class of
irreproducible crash — worth it on day one, painful to retrofit later.

## Why there is no taint list

Every existing OpenSim physics plugin queues body changes and applies them at a
safe point in the step, because ODE and Bullet cannot tolerate concurrent
modification. That machinery is pure workaround.

Jolt's `BodyInterface` is designed for concurrent access — bodies can be created,
removed and modified from other threads while the simulation runs, and queries can
run in parallel with the step. So `LegionPhysicsScene` can call
`CreateBody` straight from the rez path.

This is written into the interface contract deliberately: a backend that cannot
honour it must serialise internally rather than pushing a taint queue back up into
Legion. That keeps the abstraction from being quietly shaped by the weakest
possible implementation.

## Why the step returns into caller-owned buffers

`Step` fills `Span<BodyState>`, `Span<CharacterState>` and `Span<ContactReport>`
that the caller allocates once at region start. Nothing allocates per frame.

The overflow flags are not decoration. If a physics storm produces more contacts
than the buffer holds, you need to know you dropped them — a silently truncated
contact stream shows up much later as scripts that mysteriously miss collisions.
Surface it in the region console and in stats.

## Where the SL vehicle model goes

**Above** the backend, not inside it.

SL vehicles are not an SDK vehicle. They are a custom model of linear and angular
motors with friction and decay timescales applied to a rigid body, which is how
Halcyon implemented it. Jolt's `VehicleConstraint` (wheeled/tracked/motorcycle) is
a genuine wheeled-vehicle simulation and is the wrong shape for SL parity.

So: implement `VEHICLE_*` parameters as a per-frame force/torque controller in
`LegionPhysicsScene` calling `ApplyForce`/`ApplyTorque`/`ApplyBuoyancy`. That code
is engine-independent and is the part of the Halcyon plugin genuinely worth
studying — the parameter mapping and timescale maths, not the PhysX calls
around it.

Keep `VehicleConstraint` in reserve for a future non-SL vehicle type if you ever
want one. Do not route SL vehicles through it.

## Open decisions

These need answers before implementation, not during:

1. **Varregion terrain.** ~~Jolt's `HeightFieldShape` wants a power-of-two square
   sample count. 256×256 lands exactly; anything else needs tiling into multiple
   height fields or padding. Tiling is more work but scales; padding is simpler
   and wastes memory quadratically. Decide before writing the terrain path.~~
   **RESOLVED (NO TILING — one heightfield per region):** one square
   `HeightFieldShape` per region, **(N+1) samples** on a side where N is the region
   size in metres — **257** for a standard 256 m region, **513** for a 512 m
   varregion. (N+1) samples at 1 m spacing span exactly N metres with samples on
   integer metres, aligned to the viewer's terrain mesh. The M1 Task 6 "257 result"
   proved block divisibility is a non-constraint in joltc 2.18.6, so every size we
   need cooks correctly — tiling would buy nothing but seams and bookkeeping.
   Non-square varregions: pad the square field to `max(SizeX, SizeY)` with edge
   replication (memory cost only; terrain outside the region is unreachable). The
   +1 row/column currently DUPLICATES row N-1; fetching the neighbour region's row 0
   is a later refinement (**note it, don't build it**) — the boundary error is then a
   flat 1 m strip, not a hole, which is the whole point. **The cook guard must
   eventually accept N+1 (odd) instead of power-of-two — do NOT change it yet; that
   is the M6 terrain-feed work.** (The M1 guard stays square + PoT + ≥4 for now.)

2. **Double precision.** ~~Jolt has an optional double-precision mode for large
   worlds. It changes the ABI, so it must be decided at build time and matched by
   the binding. If varregions or hypergrid coordinates ever push past float
   precision, you want this — but it is not a switch you flip late.~~
   **RESOLVED (single precision, permanent — `joltc.dll`):** physics runs in
   region-LOCAL coordinates, so the solver never sees magnitudes larger than the
   region size; even an 8×8 varregion (~2048 m) resolves to ~0.24 mm in float —
   far finer than prims, stacking, or vehicles need. Jolt's double mode targets
   unpartitioned mega-worlds; OpenSim partitions into regions, so it does not apply.

3. **`MaxBodies` sizing.** ~~Jolt preallocates. Too low and rez fails at a hard
   ceiling; too high and every region pays the memory. Probably wants to be a
   region config value derived from prim limits rather than a constant.~~
   **RESOLVED (config-driven, computed default):** a `[JoltPhysics] MaxBodies` ini
   key, default **65536** for a standard 256 m region, scaling with region AREA for
   varregions. Rationale: EVERY prim is a body, including non-physical ones (the
   Static layer), so the ceiling tracks TOTAL prim count, not physical-body count —
   a full region plus attachments and avatars needs headroom well past ~45k. Hard
   requirements attached: (a) exhausting `MaxBodies` must FAIL the rez with a clear
   log line and NEVER crash the sim (Jolt preallocates; the ceiling is real and
   hard); (b) log PEAK body count at region shutdown so operators can tune on
   evidence. The ini plumbing is M6 integration work — this records the decision
   only. Varregion TILING (decision #1) stays OPEN.

4. **Persist-contact filtering.** ~~An avatar standing still generates a contact
   event every step forever. The `WantsContactEvents` flag on the body record
   gates this, but something has to set it from whether the object has a
   `collision` handler registered. That plumbing crosses into the script engine
   and is worth designing alongside Phlox rather than bolting on.~~
   **RESOLVED (M2 Task 2, measured):** Jolt STOPS firing `OnContactPersisted` the
   moment a body sleeps — zero after-sleep Persist at every drop height tested. So
   **sleep is the filter for resting objects**; a settled prim generates no ongoing
   Persist and `WantsContactEvents` does NOT need to gate settled content. Its SOLE
   job is the **awake-but-touching** case — specifically an avatar standing still:
   `CharacterVirtual` never sleeps, so without the gate it would emit a Persist
   against the floor every step forever. The gate (Begin/End always forwarded;
   Persist forwarded only when a body in the pair wants events) is set from whether
   the object has a `collision` handler registered — a `BodyDesc.WantsContactEvents`
   creation flag today, with a runtime setter deferred to the M5 script surface.

5. **JoltPhysicsSharp version pin.** Current releases target net9.0/net10.0.
   Either pin around 2.15.0 for net8.0 or bump Legion's target. Worth deciding
   early since it affects the whole build.

## Backend behaviour contracts (established during M2)

These are backend semantics discovered while implementing dynamics. Recorded so they
are not silently "corrected" later — they are deliberate, not incidental.

- **Forces/impulses wake a sleeping body; setting velocity does not.** `ApplyForce`,
  `ApplyTorque`, `ApplyImpulse`, `ApplyImpulseAtPoint`, `ApplyAngularImpulse` all
  auto-activate a sleeping dynamic body (this is Jolt-native for `AddForce`/`AddImpulse`,
  verified). `SetBodyLinearVelocity`/`SetBodyAngularVelocity` deliberately do **not**
  activate. Rationale: this matches SL wake-on-impulse, and "should setting a velocity
  wake the object" is a policy that belongs ABOVE the seam (in `LegionPhysicsScene`),
  not baked into the rigid-body layer. Keep the split. (M2 delta #14.)

- **Body lifecycle: static-born bodies are not promotable to movable.** PROVISIONAL —
  **needs John's final sign-off at M6; do not build the transition path before then.**
  Promoting a body from Static to Dynamic/Kinematic requires `AllowDynamicOrKinematic`
  set at *creation*, which allocates `MotionProperties` per body. Creating every prim
  movable would pay that memory across a whole ~45k-prim region for a capability almost
  nothing uses. So the backend only sets `AllowDynamicOrKinematic` on bodies **created**
  Dynamic/Kinematic; `SetBodyMotionType` to a movable type on a static-born body throws.
  **Recommended lifecycle model (confirm or override at M6):** non-physical prims are
  created **Static** (cheap, `DontActivate`); the physical-checkbox transition
  **recreates** the body as movable. The recreation cost is paid only for the rare prim
  that actually goes physical; the ~99% that never do keep the cheap Static path. (M2
  delta #15.)

- **Contact impulse is read inside the contact callback — and that is within discipline.**
  The reported impulse comes from `Jolt.EstimateCollisionResponse`, Jolt's own in-callback
  helper, which reads the two `Body` refs Jolt **already locked and handed** to the callback.
  This is NOT a violation of the worker-thread contact discipline. The rule is precise: **no
  lock we take, no allocation, no scene-state access** — all three hold (we take no lock; it is
  measured allocation-free at ~0 bytes/call; it touches no Legion scene state). Impulse is
  physically unobtainable without the bodies' velocity/mass, and this is the sanctioned source.
  Do NOT "tighten" this later into "touch no bodies at all" — that misreads the rule and would
  throw away the only real impulse we can report. (M2 delta #18.)

- **Avatar collision citizenship is part of the M3 avatar model (NOT deferred).** M3 limitation #1
  (avatars generated no collision events) is RESOLVED. **How — and why not the inner body:** the
  literal `InnerBodyShape` path was tried and rejected on evidence in 2.18.6:
  (a) the kinematic inner body does **not** report contacts against **static/terrain** (Jolt disables
  kinematic-vs-non-dynamic by default), so it misses the most common avatar-collision content —
  scripted static floor prims (sit pads, pressure plates); (b) the fix, `CollideKinematicVsNonDynamic
  = true`, **HANGS the solver** when the avatar meets a dynamic body (reproduced); (c) a solid inner
  body **changes the M3 push behaviour** (2.34 m vs the movement-preserving 2.92 m), violating the
  bit-identical-movement requirement. Instead we forward the **CharacterVirtual's own contact events**
  (`OnContactAdded/Persisted/Removed` for bodies, `OnCharacterContact*` for avatar-avatar). They fire
  on the STEP thread during `ExtendedUpdate`, cover terrain/static/dynamic/**sensor**, and — verified —
  a standing avatar re-reports its floor contact **every step** (600 Persist / 100 steps), which is the
  real thing decision #4's gate suppresses. Because they are observational, **M3 movement stays
  bit-identical** ([16]-[24] unchanged; push still 2.92 m). Avatar reports carry the avatar's UserData
  on side A with an **Invalid BodyId** (an avatar is not a solver body). Avatar-avatar collision is ON
  by default (`CharacterVsCharacterCollisionSimple`: they push and block), matching SL's
  `[BulletSim]AvatarToAvatarCollisionsByDefault = true` — making that a config knob is M6.
  Known gap (**a real SL-parity gap, NOT intentional parity** — verified against the SL wiki: "avatars
  will collide with solid objects", and physical objects collide with avatars unless the object is
  Phantom/VolumeDetect): a thrown physical object does NOT bounce off a standing avatar here — a pure
  `CharacterVirtual` has no solid presence in the solve, so the avatar detects/reports the object and
  side-steps via penetration recovery instead of stopping/deflecting it. SL genuinely stops/deflects
  physical objects on avatars, so closing this is worth doing eventually; it needs a solid presence
  and must be weighed against the push-behaviour change in (c) above. Tracked, not closed. (M3.5;
  deltas #27-#30.)

- **Avatar QUERY-visibility is part of the avatar model too (M3 #35 RESOLVED via a marker body).** A
  pure `CharacterVirtual` is not in the broadphase, so RayCast/RayCastAll/Overlap/ShapeCast could not
  find an avatar at all — `llSensor`, sit-target search and `llCastRay`-at-avatar would silently miss.
  Fixed with a **kinematic marker body** carried by each avatar on the dedicated `PhysicsLayer.AvatarQuery`
  layer (UserData = avatar id), synced to the character's transform every step (after `ExtendedUpdate`,
  before `_system.Update`). **THE COLLISION-vs-QUERY DISTINCTION — read before touching this:** the
  contact inner body was rejected (#27) because `CollideKinematicVsNonDynamic` HANGS and a solid presence
  CHANGES PUSH — both are *simulation-collision* failures. The marker sidesteps both by colliding with
  **NOTHING**: `ShouldCollide(AvatarQuery, *) = false`, so it never enters the solve (no push, no
  contacts — verified: movement [16]-[24] bit-identical incl. push 2.92 m; the #4 gate still 600→0).
  A query still finds it because queries walk the broadphase and consult the *query* `ObjectLayerFilter`,
  NOT the simulation collision matrix — so a body that is toxic-in-the-solve is inert-and-findable for
  queries. **Do NOT** (a) re-attempt the contact inner body, nor (b) rip out the marker as "redundant
  with CharacterVirtual" — CharacterVirtual gives contacts (M3.5), the marker gives queries; they are
  different citizenships. The marker is backend-managed; `RemoveBody` on it is a no-op (owned by its
  character). **Query-boundary identity contract:** a query hands back the marker's `BodyId`; it IS a
  valid handle and `TryGetBodyState` returns the avatar's TRANSFORM (position/orientation) — but it is a
  kinematic marker with no dynamics, so identity is `UserData` (the avatar id), never the `BodyId`, and
  callers must not treat it as a physical prim. (M4.5; deltas #34-#37.)

- **M5 SCRIPT-DISPATCH CONTRACT for avatar contacts (delta #30) — build M5 to this, do not patch later.**
  An avatar is not a solver body, so its contact reports carry **`BodyId.Invalid` on the avatar side**
  and the **avatar's identity in `UserData`**. Consequences the M5 collision-dispatch layer MUST honour:
  (1) key avatar identity off **`UserData`**, NEVER off `BodyId` (which is Invalid for avatars — do not
  call `TryGetBodyState`/`IsBodyValid` on it); (2) **collision FORCE/impulse is unavailable for avatar
  contacts** (reported as 0 — the CharacterVirtual contact is controller-resolved, not solver-resolved),
  so any collision **sound-volume or damage magnitude** model must treat avatar hits as force-less
  (fall back to a fixed/typed value, do not read `ContactReport.Impulse` for them).

- **M7 LINKSET CONTRACT: `ContactReport` carries no child/link identity (delta #32).** Compound (linkset)
  child UserData is recoverable from a **raycast** (`RayHit.ChildUserData`, decoded from the hit
  SubShapeID's low bits) but NOT from a **contact** — `ContactReport` has only body-level `UserDataA/B`,
  no per-child field. Consequence: at M7, `llDetectedLinkNumber` will work for **cast/detection** on a
  linkset but NOT for **collision events** (a struck child prim's collision event cannot yet report its
  link number). Closing it means extending the contact path to carry the struck child — the manifold
  already exposes `SubShapeID1/2`, so the data is available; it needs a `ChildUserData` field on
  `ContactReport` (or A/B variants) plus decode in the contact handlers. Build M7 knowing this gap.

- **M6 PHYSICAL-MESH CONTRACT: a `MeshShape` has Volume 0 (delta #31).** Jolt does not integrate
  triangle-soup volume, so mass-from-density on a mesh yields **0** (the backend clamps to a tiny
  positive so it can't produce an infinite-acceleration body, but that is a fallback, not a mass). A
  prim must NEVER rez physical with mass 0. M6 must, for a physical mesh prim, supply mass another way:
  explicit `BodyDesc.Mass`, an AABB-volume × density fallback, OR — the natural answer, and what SL does
  anyway — **use the prim's CONVEX HULL as the physical shape** (mesh stays the visual/static-collision
  shape). Default to the convex hull for physical mesh unless a reason not to surfaces.

- **M6.3 CYLINDER AXIS CONVENTION (integration layer owns it).** SL cylinders are **Z-height** (the
  circular section lies in local XY, the axis is local Z); Jolt's `CylinderShape` axis is **Y**. The
  backend leaves this to the layer *by design* (`CreateCylinderShape` comment: "prim orientation is the
  layer's job"). So `JoltPrim` folds a **+90°-about-X** correction (maps local Y→Z) into the body
  orientation, composed in System.Numerics as **`axisCorrection * primOrientation`** (left operand
  applied first) so the prim's own rotation still composes correctly. Box/sphere need no correction
  (axis-agnostic / symmetric). Proven by the `AXIS` raycast row: top-cap at z=102 (correct) vs a curved
  side at z=100.5 (a wrong Y-axis cylinder). *Only cylinders* carry a non-identity correction.

- **M6.3 SHAPE DETECTION MUST USE CANONICAL Profile+Extrusion, NOT the factory (delta).**
  `PrimitiveBaseShape.CreateCylinder()` emits **Square + Curve1** (an SL *tube*), NOT **Circle +
  Straight** (a real viewer/OAR cylinder). Trusting the factory would mis-route real cylinders to the
  mesher. `CookPrimShape` therefore keys the fast path on canonical values — box = `Square+Straight`,
  sphere = `HalfCircle+Curve1` (uniform), cylinder = `Circle+Straight` (circular) — all gated by
  BulletSim's `PrimHasNoCuts`. Anything else falls to a bounding box until the mesher (M6.3 T2). When
  constructing a cylinder programmatically, build it canonically (start from `CreateBox()`, set
  `ProfileShape.Circle`) rather than calling `CreateCylinder()`.

- **M6.3 RAYCAST QUERY PULLED FORWARD FROM M6.7 (scope note, approved).** A script `llCastRay` only
  reaches the physics engine when the module returns **`SupportsRaycastWorldFiltered()`→true** and
  implements **`RaycastWorld(...)`**; otherwise `llCastRay` silently falls back to OpenSim's *own*
  geometry/mesh intersection and bypasses Jolt entirely (so a "surface-not-bbox" test would prove
  nothing about the engine). A thin slice landed in M6.3: `RaycastWorld` → `backend.RayCastAll`,
  `RayFilterFlags`→`QueryFilter` (`land`→Terrain, `nonphysical`→Static, `physical`→Dynamic,
  `agent`→Avatar, `phantom|volumedtc`→Sensor), `RayHit.UserData`→`ContactResult.ConsumerID` (the
  `SceneObjectPart.LocalId`). The rest of the query family — `SphereProbe`/`BoxProbe` (llSensor),
  `RaycastActor`, sit-avatar detection — remains **M6.7**.

- **M6.3 MESHER CACHE POISONING — `releaseSourceMeshData()` IS A TRAP (delta #38, load-bearing for
  M6.5).** The Meshmerizer **caches and shares** the `Mesh` object: `Meshmerizer.CreateMesh` stores it
  in `m_uniqueMeshes` keyed on `GetMeshKey(size, lod)` (the key ignores `isPhysical`/`convex`) and
  returns the **same instance** for every identical prim — and `shouldCache` defaults **true** on all
  reachable overloads (the 8-arg one that takes `shouldCache` is itself bugged and ignores it). `Mesh`
  extraction throws when its source is gone: `getIndexListAsInt()` / `getVertexListAsFloat()` do
  **`if (m_triangles/m_vertices == null) throw new NotSupportedException()`**, and
  **`releaseSourceMeshData()` nulls exactly those fields**. So calling `releaseSourceMeshData()` on a
  cooked mesh **poisons the shared cached instance** — the *next* prim with identical geometry gets the
  poisoned mesh and its extraction throws. This bites **every repeated mesh asset** (a region with N
  copies of one mesh: the first cooks, the rest throw), which is precisely M6.5-with-real-content.
  **Correct handling:** `getIndexListAsInt()`/`getVertexListAsFloat()` already return **fresh copies**,
  so extract and keep those; **never** call `releaseSourceMeshData()` (or otherwise mutate) a mesher-
  returned `Mesh`; `ReleaseMesh()` is a no-op (the mesher owns eviction). Wrap **all** mesher extraction
  in a guard with a bbox fallback so a `NotSupportedException` can never propagate out and abort a rez.
  BulletSim dodges this by passing `shouldCache=false` for a private throwaway mesh; we keep caching (it
  is desirable — cook once per asset) and simply never poison it.

- **ACCEPTED characteristics (documented, not open items):**
  - *Characters are not lock-free like bodies (M3 #2).* `CharacterVirtual` create/remove/set/step are
    serialised to the step thread via a gate. The taint-free "call from any thread" property is
    **bodies-only**; this asymmetry is accepted.
  - *`CharacterDesc.Friction` is unused (M3 #5) — ground-HOLD resolved in M6.5, coefficient still unused.*
    `CharacterVirtual` has no body-style friction. The ground-holding half of delta #5 is now done in the
    M6 movement model (`StepCharacter`): a character supported on WALKABLE ground (`GroundState.OnGround`,
    slope ≤ `MaxSlopeAngle`) and not jumping does **not** accumulate gravity, so a no-input avatar cannot
    creep down a walkable slope (the M6.5 "sliding down the cone" bug — the harness only tested FLAT ground
    so it never surfaced). Gravity resumes when airborne or on `OnSteepGround`, so ledges drop and
    over-steep slopes still slide (`IsSliding`). The `Friction` *coefficient* itself remains unused — the
    hold is a binary walkable/steep decision from `GroundState`, not a tunable friction force.

## M6.5 Task 1 — the avatar (live CharacterVirtual)

`AddAvatar` now returns a real `JoltCharacter` (a `CharacterVirtual`, not a solver body) seated ON the
terrain, movement-driven by ScenePresence (`TargetVelocity`/`Flying`/`AvatarJump`) and drained back each
frame (`[charframe]`). It holds on walkable slopes, slides on steep, rides platforms, is a collision
citizen (M3.5) and query-visible via the M4.5 marker. Deltas recorded here so they are not re-litigated:

- **#5 RESOLVED — GroundState ground-hold IS the M6 movement model.** Walkable (`OnGround`) → hold (no
  gravity accumulation); steep (`OnSteepGround`) → slide (`IsSliding`); jump → release (the jump frame has
  `JumpRequested`, so the hold is off and the impulse rises). It is a **binary** walkable/steep decision
  from `GroundState`, NOT the `Friction` coefficient — SL has no tunable slope-grip.

- **Jump is animation-gated for VISIBILITY, not broken.** `[charjump]` confirmed the physics fires
  (`AvatarJump fired` → `TAKEOFF`, `vZ>0`, Z rises). Without a jump animation the avatar reads as static,
  exactly as a default SL avatar does without an AO. Do **not** re-chase jump as a physics bug.

- **Feet-dip polish REVERTED — `CreateCharacter` stays at Jolt defaults (`PredictiveContactDistance` 0.1,
  `PenetrationRecoverySpeed` 1.0).** Raising the look-ahead to 0.15 hovered the capsule off the ground and
  fought recovery frame-to-frame on inclines (a walking hop). The minor feet-dip on step/slope transitions
  is ACCEPTED as cosmetic. Do not re-attempt without a way to *measure* slope-feel (the harness tests flat
  ground and cannot see hover/hop).

- **The diagnostic arc (the "underground" saga).** The apparent sink was the avatar **sliding down the
  cone** on a frictionless walkable slope (delta #5) — the character rode the descending terrain correctly
  (`feetAboveTerrain` constant ~0.005). It was **NOT** a dt units bug (`[dtproof]` = 0.0909 s), NOT the
  M4.5 marker (ground was `TERRAIN`, UserData 0), NOT the terrain moving (`terrainZ@centre` constant,
  `SetTerrain` fires ~5 s not per-frame), NOT a sub-step desync (harness: sub-stepped character amplitude
  0.000). Settled by `[charframe]`: `terrainZ@centre` constant while `XY` drifted with no input. Preserve.

## M6.6 — sit / unsit (the character lifecycle, and llSitTarget is not physics)

Sitting is a CHARACTER-LIFECYCLE transition, and it falls out of the 6.5 wiring + OpenSim's own model —
no new physics mechanism was needed:

- **Task 1 — sit suspends / unsit re-engages = REMOVE / RECREATE.** OpenSim sits by
  `ScenePresence.RemoveFromPhysicalScene` -> `RemoveAvatar` (the `CharacterVirtual` **and** its M4.5 marker
  are destroyed) and stands by `AddToPhysicalScene` -> `AddAvatar` (a fresh 6.5 character at the release
  pos). So "suspend" == the character is GONE (a seated avatar cannot fall or slide by construction - no
  character, no gravity/ground/movement; its position is driven by the prim via `ParentID`/scene-graph),
  and "re-engage" == the walking model rebuilt (incoming velocity zero -> no fling). Same remove/recreate
  pattern as the 6.4 `IsPhysical` toggle. Confirmed the scene does not override `PhysicsScene.SitAvatar`
  (base returns 0), so the remove/add sit path is the one in force. Harness [24c]: 6x sit/unsit cycles,
  each create -> 1 supported character, each remove -> 0 (no leak, always re-engages).

- **Moving / dynamic seat rides FREE via parenting.** While seated the character is gone, so a physical
  seat just simulates as a normal 6.4 dynamic body and its per-frame position drain moves the SOG, which
  the parented avatar tracks. Two proven systems composed (dynamic body + scene-graph parenting), no code.

- **Task 2 — llSitTarget offset/rotation is OpenSim ScenePresence math, NOT physics.**
  `HandleAgentSit` (`LegacySitOffsets`) seats the avatar at `SitTargetPosition` composed into the prim's
  local frame, plus a vertical `SIT_TARGET_ADJUSTMENT` of **+0.35 m** (`(0,0,0.4)` minus a `0.05` up-offset
  for an identity sit orientation - `ScenePresence.cs:185`). That +0.35 is the STANDARD SL sit offset
  (furniture creators expect it), is engine-INDEPENDENT (BulletSim / ubODE seat identically - the character
  is removed on sit, so there is no capsule term), and is NOT a Jolt bug. The `jolt sittarget` console
  gates all three axes against the SL-composed expected local position (chose `sitTargetPos.Z=0.30` ->
  expected `0.65 != standHalf 0.95` so a capsule leak could not masquerade as the SL offset). Leg-vs-seat
  fit (short avatar with no sit animation) is the sit-animation/content layer, not physics.

## M6.7 — the query family on Phlox (llSensor, llCastRay), SL-exact

The query family lands on **Phlox** (Legion's engine), routed to the backend query layer, and matched
to SL's documented flag semantics. Proven live in-viewer (John).

- **llCastRay routing — Phlox calls the 4-arg *filterless* `RaycastWorld`, XEngine calls the 5-arg.**
  `LSLSystemAPI.llCastRay` -> `World.PhysicsScene.RaycastWorld(start, dir, dist, count)` (no
  `RayFilterFlags`); OpenSim's own `LSL_Api.llCastRay` -> the 5-arg `RaycastWorld(..., RayFilterFlags)`.
  Both are STANDARD OpenSim `PhysicsScene` surface (Tranquillity-portable). We **override BOTH** and route
  each to `CastAll` -> `backend.RayCastAll` (`AllHitSorted`, distance-ordered): the 4-arg with
  `QueryFilter.Default` (Terrain|Static|Dynamic|Avatar - Phlox does its own reject-filtering script-side),
  the 5-arg via `ToQueryFilter(RayFilterFlags)`. **M6.7 regression cause:** only the 5-arg had been
  overridden, so under Phlox every cast fell through to the base (empty list) = 0 hits. `ContactResult`
  fields (`ConsumerID`=`SceneObjectPart.LocalId`, 0=terrain; `Pos`; `Normal`; `Depth`) set from `RayHit`.
  Proven: terrain hit + REAL normal `<0.129,0.163,0.978>`; `RC_REJECT_LAND` excludes land; `RC_REJECT_AGENTS`
  DEFAULT detects the avatar (SL-correct), set-flag drops it; multi-hit = distance-ordered real keys.

- **Two different agent queries, by design (do not conflate).** Phlox `llSensor` is **scene-graph**
  (`SenseEntities`) - it finds a **seated** avatar (whose character is gone, M6.6). The **physics**
  agent-query (`llCastRay`-at-avatar, overlap) uses the **M4.5 marker** body - which exists only while the
  avatar is a live `CharacterVirtual` (**walking only**; seated => marker gone, same as the character).
  So "llSensor finds the seated rider but llCastRay does not" is CORRECT, not a miss. Marker identity via
  `#30 UserData` (marker carries the avatar `LocalId`, so a physics hit resolves to the agent).

- **The Phlox llCastRay `RC_*` constant bug is NOT ours - and is general.** Phlox's `llCastRay` hardcoded
  its own `RC_*` constants (`LSLSystemAPI.cs`) *disagreeing* with Phlox's SL-correct `DefaultConstants.cs`,
  so option keys + `dataFlags`/reject bitmasks scripts send were mis-parsed: the normal was dropped (zeroed),
  link-num ignored, reject flags mangled (all hits dropped). Fixed in a **separate, standalone commit
  `28b61f9d1b`** (that file only). It is a **pre-existing Phlox port bug, engine-independent** - it breaks
  `llCastRay` flags for *every* Phlox script regardless of physics backend - and is **worth upstreaming to
  Legion production + Tranquillity**. Ruled out the tempting "two `ContactResult`/`SharedBase` copies =
  marshaling scramble" hypothesis by hard evidence: one `SharedBase v0.9.3.0` (identical assembly identity,
  `PublicKeyToken=null`, all three of module/Phlox/boot-bin), one DLL; the reflection-probe loader exception
  was an isolated-`LoadFile` artifact (missing `System.Runtime`/`OpenMetaverseTypes` in the probe context),
  not a runtime duplicate.

## M6.8 — A/B parity vs BulletSim (CORE: drop/rest, mass, friction)

The capstone: prove Jolt behaves like the engine Legion's content was tuned against (BulletSim). Two
engines cannot co-exist in one boot (physics selection is the global `[Startup] physics=`; both engines
self-select on it, no per-region override), so parity runs as **two sequential boots** in the scratch
dir (flip `physics=`, run the same driver, diff). The driver is an **engine-agnostic** console command
(`parity`, registered in this module BEFORE the `m_Enabled` gate so it exists under BulletSim too) that
drives ONLY the standard `Scene`/`SceneObjectGroup`/`PhysicsActor` surface - identical code on either
engine, which is what makes the comparison valid. Default rule: **match BulletSim** (content's reference)
UNLESS BulletSim is clearly wrong.

- **MASS - MATCHED (was 100x off).** Jolt computed mass = Volume x `BodyDesc.Default.Density`(1000) = 125 kg
  for a 0.5^3 box; BulletSim = 1.25 kg. Root: OpenSim stores `SceneObjectPart.Density` in *simulator*
  units (default 1000) and BulletSim scales it to *physical* density by `BSParam.DensityScaleFactor = 0.01`
  (mass = `Density x 0.01 x Volume`, BSPrim.cs:1613). `JoltPrim` ignored the SOP density entirely. FIX:
  `JoltPrim.Density` now forwards `SOP.Density x 0.01` to the backend's new `SetBodyDensity` (recomputes
  mass = shapeVolume x physicalDensity via `MassProperties.ScaleToMass`), and `JoltPrim.Mass` reads it back
  through the new `GetBodyMass` (was hardcoded 0 - also closes the M6.4 "mass behavioural-proof-only" gap).
  Result: box **1.2500**, prism **0.4059** - identical to BulletSim by the same formula. `llGetMass`/forces
  /vehicles now see BulletSim's numbers.

- **FRICTION / SLIDE - JUSTIFIED DIVERGENCE, Jolt is MORE correct (do NOT match BulletSim).** A box dropped
  on an 11.3 deg terrain flank: BulletSim slid ~32 m to the region edge and never rested in 34 s; Jolt
  stayed within 2 cm and settled in 2.2 s. 11.3 deg is well below the Coulomb threshold `atan(0.6)=31 deg`
  (all surfaces - terrain, box, ramp - carry friction 0.6: JoltPhysicsBackend.cs:1547 / :744), so a box
  should NOT slide there. Confirmed with an **at-rest ramp test** (box placed flush, tilted, zero initial
  velocity on a static ramp - isolates friction from drop-impact): Jolt STAYS at 20/30 deg (tan<0.6),
  SLIDES then RESTS above 31 deg - exact textbook Coulomb friction; BulletSim slides 6.7 m at 30 deg *from
  rest* and never settles at 35/45 deg (still 1.3-1.7 m/s at 10 s) - broken friction / terrain-creep,
  impact-independent. So Jolt's clean settle is an **UPGRADE**, not a regression: content that merely
  tolerated BulletSim's drift settles correctly on Jolt. The earlier drop-slide seen on a tilted prim ramp
  (1-5 m) was drop-**impact** (a box dropped onto a steep face gets a horizontal impulse a resting box does
  not); the at-rest box holds perfectly. Kept Jolt's behavior deliberately, with the ramp evidence above.
  (`parity ramp` reproduces the at-rest test; `parity terrain` proves the slope calc - the drop point
  128,128 is the cone APEX, ~0 gradient by definition, so drops target the scanned steepest flank / flattest
  cell instead.)

- **SETTLE TIME - keep Jolt.** Jolt 2.2 s vs BulletSim 34 s is the creep artifact (BulletSim was still
  sliding, not settling), not a real parity gap - a symptom of the friction defect above.

Standard-surface note (Tranquillity portability): the parity driver, mass-getter and density-forward all
use only the standard `PhysicsActor`/`PhysicsScene` contract (`Density`, `Mass`, `RaycastWorld`), so they
carry to any OpenSim-derived grid.

### M6.8 edge cases (deferred llCastRay / avatar-collision details)

- **llCastRay NULL_KEY on land - MATCH, no fix.** BulletSim's terrain body has LocalID 0
  (`BSScene.TERRAIN_ID = 0`, "OpenSim senses terrain with a localID of zero"), so a terrain raycast hit
  returns `ConsumerID = 0`; Jolt's heightfield body has `UserData = 0` -> same. Both -> NULL_KEY, the shared
  OpenSim "terrain has no key" convention. Identical.

- **Coincident terrain hits - DEDUPED to match BulletSim's single hit.** BulletSim's `RayTest2` is
  closest-hit (ONE hit, ignores count); Jolt's `RayCastAll` returns all hits. On a SLOPED heightfield a
  vertical ray crossing a shared grid edge strikes the two non-coplanar quad triangles at the SAME point on
  the SAME (terrain) body -> two coincident hits, which scripts counting `llCastRay` hits do not expect. Fix
  in the BACKEND `RayCastAll` (so `jolt raytest`, the module, and every caller stay consistent; the module
  boundary keeps the standard `ContactResult`): drop a hit only when it is the same body AND within 1 mm of
  the previous KEPT hit. Legitimate multi-hit is untouched - a ray through stacked prims (different
  bodies/points) or terrain-then-prim (different bodies) still returns every hit, distance-ordered. Proven
  in harness [34b]: with the collapse disabled the grid-edge ray returns 2, enabled returns 1; [34] still
  returns all 3 stacked boxes. A flat field is coplanar and never doubles - the slope is what exposes it.

- **Depth semantics (note, not a fix).** BulletSim puts the ray FRACTION (0-1) in `ContactResult.Depth`;
  Jolt puts the true distance. Not script-visible (Phlox sorts by Depth but does not return it; both are
  monotonic in distance, so ordering is identical either way).

- **Avatar-to-avatar collision default - MATCH, no fix.** BulletSim's `AvatarToAvatarCollisionsByDefault`
  defaults to **true** (BSParam.cs:658); `BSCharacter` then sets `collisionType = CollisionType.Avatar` so
  avatars block each other (a `false` setting uses `PhantomToOthersAvatar` = pass through). Jolt registers
  EVERY `CharacterVirtual` in a shared `CharacterVsCharacterCollisionSimple` at creation
  (`SetCharacterVsCharacterCollision`), so avatars collide by default too - harness: a1 walking into a
  stationary a2 pushes it 2.16 m, `passedThrough=False`. Default behaviour MATCHES. Nuance: BulletSim's is a
  toggle (settable false -> avatars phantom to each other); Jolt always collides characters with no such
  switch - a minor CONFIG-parity gap, not a default-behaviour gap (grids run the default true). Seated
  avatars are removed from the physical scene by OpenSim on BOTH engines (M6.6 remove-on-sit), so they never
  collision-participate as avatars either way - moot, MATCHED.

**M6.8 COMPLETE:** CORE (mass matched via density-forward; friction/slide a justified divergence where Jolt
is textbook-correct and BulletSim creeps; settle-time follows) + all 3 edge cases (NULL_KEY on land MATCH,
coincident terrain-hit dedupe, avatar-to-avatar MATCH). The one deliberate divergence from BulletSim
(slope friction) is the one where BulletSim is physically wrong; everything else matches BulletSim exactly.

## Terrain collision: heightfield now, terrain-mesh in reserve (M6.5)

We collide against the region terrain with a Jolt **`HeightFieldShape`** (cooked from the region
heightmap). At OpenSim's ~11 fps physics cadence (`Scene.FrameTime` ≈ 0.0908 s) a single Jolt
integration lets a fast body move ~1.5 m/frame, which the heightfield's **discrete narrowphase** can miss
— a dropped prim tunnels straight through (M6.5 finding #3). We fix that cheaply with
**`CollisionSteps = 6`** (Jolt sub-steps the rigid-body solver inside `_system.Update`, without
re-running the character step), keeping the avatar on the known-good 1-step-per-frame path. A global
`Simulate` sub-step was tried first and **reverted** — it 6×'d the whole character/drain/terse pipeline
and was a live *performance* regression (avatar bounce/jitter), not a physics one.

Evidence the heightfield **narrowphase has limits**: per-body CCD (`MotionQuality.LinearCast`) does **not**
catch the heightfield in the harness — a `LinearCast` box still tunnels — so CCD is not a usable escape
hatch for fast movers here. This is fine for gravity-driven prims (`CollisionSteps` handles them), but
could bite genuinely fast bodies later (vehicles, projectiles/`llCastRay`-speed objects).

**Reserve fix — terrain MESH collider.** Per Balpien Hammerer: InWorldz abandoned direct heightfield use
(PhysX 2.x heightfield support "was weird") and instead **baked a terrain mesh from the heightmap** for
collision; stock OpenSim (ubODE / BulletSim) uses heightfields directly. If heightfield collision shows
further problems, the escape hatch is to bake a `MeshShape` (or per-region tiled meshes) from the same
(N+1) height field and collide against that instead — a triangle-mesh narrowphase behaves differently for
fast bodies. Kept in reserve, not implemented: the heightfield + `CollisionSteps=6` is cheaper and proven
for the current (gravity-driven) workload.

## Build notes

- New `.cs` files need explicit `<Compile Include>` entries — `EnableDefaultItems`
  is off and there is no auto-glob.
- If this lands as a new Mono.Addins module, clear the addin-db directory on
  first deploy.
- Native Jolt binaries arrive as a NuGet dependency of JoltPhysicsSharp
  (`JoltPhysics.Native`), so a self-contained `linux-x64` publish carries them.
  That is the whole reason this path avoids the vendoring problem — no private
  feed, no build toolchain for downstream operators.

## Milestones

Ordered so that each one is independently demonstrable and each failure is cheap
to discover.

| # | Milestone | Proves |
|---|-----------|--------|
| 1 | Backend initialises, terrain heightfield, one static box | Layer/broad-phase wiring is right |
| 2 | Dynamic box falls, sleeps, reports state | Step loop and active-set drain |
| 3 | Avatar `CharacterVirtual` walks, climbs steps, stands on prims | The thing users actually feel |
| 4 | Mesh + convex shapes through the existing meshmerizer path | Asset pipeline integration |
| 5 | Contact reports drive `collision_start`/`collision`/`collision_end` | Script surface parity |
| 6 | `llCastRay`, sit targets, camera queries | Query path |
| 7 | Linksets as compound shapes | Compound + child UserData |
| 8 | SL vehicle model on rigid bodies | The long tail |
| 9 | Constraints exposed to SLua | The differentiator |

Stop after 3 and reassess. If the avatar does not feel right by milestone 3, no
amount of work at milestone 8 will rescue it — and that is a cheap place to find
out.

## A/B parity harness

Worth building early: run the same scripted scenario against BulletSim and the
Jolt backend, record body transforms per frame, diff. Jolt's deterministic mode
makes its side reproducible, so any divergence is real rather than noise.

This is how content parity stops being guesswork. It is also the only practical
way to answer "did that change break anything" once regions have real content.
