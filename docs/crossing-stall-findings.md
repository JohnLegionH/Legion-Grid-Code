# Region-crossing "stall" — findings (2026-08-03)

Investigation into the felt "hard stop then restart" when **driving a physical
vehicle across a region border**. Two distinct problems were found; one is
fixed, one remains open with a queued next move.

## TL;DR

| # | Problem | Status |
|---|---------|--------|
| 1 | **Border-bounce loop** — car ping-ponged across the seam, ~16 crossings in 90s | **FIXED** (#1a) |
| 2 | **~78ms per-crossing dead-stop** — one deferred-activation frame, client snap-back | **OPEN** — see "Queued next move" |

A separate red herring was cleared first: a phantom `[ATTACHMENTS] Enhanced
script state restoration` log line that appeared ~1020ms after every crossing.
It was a `Task.Delay(1000)` in front of a **no-op TODO stub** in
`AttachmentsModule` — it did nothing, blocked nothing, and its consistent
timing sent diagnosis chasing it twice. The whole "Enhanced script state"
stub system was **deleted** (capture + restore stubs, the `Task.Delay`, and a
latent `enhancedTask.Wait(5000)` that would have run *under*
`AttachmentsSyncLock` if ever implemented). The real state transfer is the
legacy `GetStateSnapshot()` / `SetState()` path, which is fast and inline.

## 1. The border-bounce loop (FIXED — #1a)

**Mechanism.** Exit was triggered with **zero margin** (the `AbsolutePosition`
setter fires the instant the root passes the boundary), and arrival was clamped
to a flat **`enterDistance = 0.2f`** inside the destination
(`EntityTransferModule.GetObjectDestination`). Total hysteresis was 0.2m for a
>2m vehicle — its root landed essentially *on* the seam. Once the car was
near-stationary at the seam (after the first hop spent its momentum), any 0.2m
of seam-ward movement (motor remnants, steering deflection, solver settle, a
terrain-height lip) re-triggered a crossing. Each hop cost a deferred-activation
dead frame and re-planted the car 0.2m inside the *other* region — a loop.

**Fix (#1a, kept).** Replace the flat 0.2m clamp with a **velocity-proportional
entry offset**: `enter = clamp(|v_axis| * 0.25s, 0.2m, 4.0m)`. A driving vehicle
lands ~2.6m inside — clear of the re-cross band — while slow/stationary objects
keep the old 0.2m floor (unchanged behaviour), and a cap bounds parcel-check
skipping at extreme speed. This mirrors Halcyon's dead-reckoned arrival
(place the object *ahead* by its travel), and **confirmed in the log**: the
repeated-hop cluster is gone; crossings are now single, clean events with exact
momentum transfer.

This is the analogue of InWorldz/Halcyon's approach — see
`InWorldz.PhysxPhysics.PhysxPrim.DoResumeInterpolation` (`position += velocity *
transit-elapsed`) in the Halcyon reference tree.

## 2. The ~78ms per-crossing dead-stop (OPEN)

**Symptom.** With the loop gone, a single ~78ms freeze of *only the car*
remains at each crossing (the viewer/avatar stay smooth). It is **one heartbeat
frame** (~90ms at 11fps).

**Mechanism.** LegionJolt creates every physical body **inert** ("deferred
activation" — the BulletSim configure-before-step barrier, so a reloaded
vehicle can't free-fall before its gravity is cancelled) and activates it at
the top of the *next* `Simulate` (`DrainPendingActivation`). A crossed body is
created partway through frame N and activated at the top of frame N+1 — one
frame later.

Crucially, the body is **not** at zero velocity during that frame: `ApplyPhysics`
sets `pa.Velocity` synchronously at add
(`SceneObjectPart.AddToPhysics`, `applyDynamics = isPhysical = true`), which
pushes the exit velocity onto both `JoltPrim._velocity` *and* the backend body.
The body simply **sleeps** (is not stepped) for that one frame, so it does not
integrate its velocity — its position is frozen.

**Why it feels like a wall.** The viewer client-side extrapolates the moving
car forward during the ~78ms. The delayed wake terse-update then reports the
car at its *arrival* position (behind where the client extrapolated), so the
client **snaps the car back** — that snap-back is the felt "hit a wall, then
resume."

**What was tried and did NOT cure it (#1b, reverted).** #1b dead-reckoned the
wake position forward by `velocity * dt` in `JoltPrim.ActivatePending`, so the
wake-update would land where the client already extrapolated (no snap-back) —
Halcyon's `DoResumeInterpolation` analogue. A first version silently no-op'd
because its terrain-embed guard *skipped* when the reckoned body-bottom was
below ground (the common case: a car resting on the surface, a small -vZ over
the window drops it a few cm under). That was fixed to **lift** above terrain
instead of skipping (matching Halcyon `EnsureObjectAboveGround`), after which
the dead-reckon **fired every crossing** (confirmed in the log) — **but the
felt stop remained**. So either the client-snap-back model is incomplete, or a
one-time position jump at wake is the wrong compensation (the body is still
un-stepped for the frame; jumping position once does not make it *move*
smoothly through that frame). #1b was **reverted** — it added complexity in the
hot path without curing the feel.

## Queued next move (fix #2 — create crossed bodies ALREADY ACTIVE)

The root cause is the **frozen frame itself**, not where the body wakes. The
direct fix is to eliminate the dead frame for live crossings: create the crossed
body **active with its velocity** (`StartActive = true` / immediate activate)
instead of deferring, so it integrates from frame N and there is *no* frozen
frame to snap back from.

Constraints / risks to handle:

- **Preserve the configure-before-step barrier.** The deferral exists so a
  reloaded/loaded vehicle never steps under full gravity before its
  gravity-cancellation/buoyancy is applied. Creating-active must be **gated**:
  only when `!IsRegionLoading` (a live crossing, not region load/reload) **and**
  the body has meaningful velocity, and only after the vehicle params are
  applied (`SetVehicle` runs before `Velocity` in `AddToPhysics`, so the config
  is present by the time the body is created-active — verify this ordering
  holds for the create-active path).
- Needs its **own test pass**: multi-region drive-across at speed and slow, a
  reload/boot with physical vehicles present (must still not free-fall), and the
  boat cases from the M8 vehicle work.

This touches the free-fall barrier that a lot of prior reload work hardened, so
it is deliberately a separate change, not bundled with #1a.

## Instrumentation shape (for when #2 is picked up)

Temporary `[TEMP xing]`-tagged, one-shot-per-crossing logging (all stripped
after this round — re-add when measuring #2). Log at Info; grep `[TEMP xing]`:

- **SRC exit** (`SceneObjectGroup.CrossAsync`, sitters path, before
  `CrossPrimGroupIntoNewRegion`): vehicle UUID, exit `|v|` + vector, sitter
  count, tick.
- **DST body create** (`JoltPrim.CreateBodyInternal`, physical branch): id,
  `|v|`, whether the un-bury guard fired, tick.
- **DST body activate** (`JoltPrim.ActivatePending`, after `ActivateBody`):
  id, backend `|v|`, active flag, tick. (The create→activate tick gap is the
  ~78ms dead frame.)
- Optional: **DST reseat t0** (`ScenePresence.MakeRootAgent` re-seat block) and
  **first control event** routed to the seated vehicle's script
  (`SendControlsToScripts`, one-shot armed at reseat) — only needed if control
  latency is back in scope; the current finding is physics-side, so the three
  body lines above are the core set.

Keep it one line per event, never per-frame (a per-frame flood was a repeated
lesson this round).

## Files

- **Kept:** `EntityTransferModule.GetObjectDestination` (#1a entry offset);
  `AttachmentsModule` (Enhanced-stub-system removal).
- **Reverted:** #1b (dead-reckon in `JoltPrim.ActivatePending`); #2 re-cross
  suppression (`SceneObjectGroup.NoteCrossingArrival` + `AbsolutePosition`
  guard) — #1a alone fixes the loop, so the belt-and-braces was unneeded.
- Halcyon reference: `InWorldz.PhysxPhysics.PhysxPrim`
  (`SuspendPhysicsSync` / `ResumePhysicsSync` / `DoResumeInterpolation` /
  `EnsureObjectAboveGround` / `CrossingFailure`).
