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

1. **Varregion terrain.** Jolt's `HeightFieldShape` wants a power-of-two square
   sample count. 256×256 lands exactly; anything else needs tiling into multiple
   height fields or padding. Tiling is more work but scales; padding is simpler
   and wastes memory quadratically. Decide before writing the terrain path.

2. **Double precision.** ~~Jolt has an optional double-precision mode for large
   worlds. It changes the ABI, so it must be decided at build time and matched by
   the binding. If varregions or hypergrid coordinates ever push past float
   precision, you want this — but it is not a switch you flip late.~~
   **RESOLVED (single precision, permanent — `joltc.dll`):** physics runs in
   region-LOCAL coordinates, so the solver never sees magnitudes larger than the
   region size; even an 8×8 varregion (~2048 m) resolves to ~0.24 mm in float —
   far finer than prims, stacking, or vehicles need. Jolt's double mode targets
   unpartitioned mega-worlds; OpenSim partitions into regions, so it does not apply.

3. **`MaxBodies` sizing.** Jolt preallocates. Too low and rez fails at a hard
   ceiling; too high and every region pays the memory. Probably wants to be a
   region config value derived from prim limits rather than a constant.

4. **Persist-contact filtering.** An avatar standing still generates a contact
   event every step forever. The `WantsContactEvents` flag on the body record
   gates this, but something has to set it from whether the object has a
   `collision` handler registered. That plumbing crosses into the script engine
   and is worth designing alongside Phlox rather than bolting on.

5. **JoltPhysicsSharp version pin.** Current releases target net9.0/net10.0.
   Either pin around 2.15.0 for net8.0 or bump Legion's target. Worth deciding
   early since it affects the whole build.

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
