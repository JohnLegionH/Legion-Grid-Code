# Legion Physics Backend — design notes

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

- **ACCEPTED characteristics (documented, not open items):**
  - *Characters are not lock-free like bodies (M3 #2).* `CharacterVirtual` create/remove/set/step are
    serialised to the step thread via a gate. The taint-free "call from any thread" property is
    **bodies-only**; this asymmetry is accepted.
  - *`CharacterDesc.Friction` is unused (M3 #5).* `CharacterVirtual` has no body-style friction; an
    avatar's ground friction is the M6 movement model's accel/decel business, not a backend knob.

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
