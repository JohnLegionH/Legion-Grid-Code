# Balpien-Reported Bugs & Known Issues

(Running log of issues Balpien Hammerer has flagged — long-standing OpenSim/vehicle
problems he knows from InWorldz/Halcyon experience. Captured for future work; not
necessarily started. His framing: several are long-reported upstream and never
addressed by core.)

> **Status:** capture/reference only. Current priority is the vehicle fork-push
> (Commits A/B/C) and M8 vehicle types — do **not** start these items yet.
> Append new reports as Balpien voices them; keep entries dated where possible.

---

## Vehicle / physics

1. **Vehicle ramp/decay dynamics lost on region crossing** — when a vehicle crosses
   region boundaries, the motor ramp-up/decay dynamics (the deflection/timescale
   state) don't pass through to the destination region. Long-standing, reported
   multiple times upstream.
   - _Note:_ adjacent to the reload-persistence family we just fixed (reload = time
     boundary; crossing = space boundary) — natural fit for the vehicle work.

2. **Avatar kinetic↔physical transition finesse** — SL smoothly transitions an avatar
   between kinematic and physical states under several important circumstances;
   OpenSim lacks this finesse. A physics-behavior gap (separate from persistence).

## Persistence

3. **"Several persistence bugs"** — Balpien indicates the vehicle-persistence bug we
   fixed (Phlox bypass + missing `HasGroupChanged`) is one of several persistence
   issues in the tree. Others TBD as he specifies — watch for them while working in
   persistence/serialization code.

## Phlox / script engine

4. **Phlox int→float coercion missing on vector component assignment** — assigning an int
   literal to a vector/rotation component in expression-statement position (e.g.
   `vec.z = 10;`) throws "Unable to cast System.Int32 to System.Single" instead of coercing.
   Every OTHER float context coerces correctly (float vars, function args, vector LITERALS
   like `<0,0,10>`); only the VectorAssignmentToComponent path is missing the coercion.
   Narrow trigger (component = int, statement position) but a hard crash when hit. Present in
   Legion, Tranquillity, AND original InWorldz Halcyon Phlox — an inherited defect, not a
   regression. Fix: add the standard int→float coercion to the component-assignment path.
   Workaround: use float literals (`10.0`). Found via the balloon vehicle script.
   Upstream-relevant (Balpien's engine).
   - _Concrete site (from the read-only diagnosis):_ the runtime VM hard-casts —
     `InWorldz.Phlox/VM/Interpreter.Actions.cs`, the subscript store/load helper
     (`_LoadSub`/`_StoreSub`), lines ~219–306: `(float)subScriptValue` at 7 sites (vector
     x/y/z + rotation x/y/z/w). Fix = swap those for the file's existing safe helper
     `ConvToFloat(subScriptValue)`. Vector/rotation LITERAL builders (`Op_BuildVec`/
     `Op_BuildRot`) already use `ConvToFloat`, which is why literals are fine. Same file +
     line numbers on Tranquillity `develop:Source/InWorldz.Phlox/VM/Interpreter.Actions.cs`.
   - _Found:_ 2026-08-01.

## Meta

- Balpien's recurring theme: these are long-reported upstream and core "just doesn't
  care." He's glad to see them actually getting fixed. Worth capturing everything he
  mentions — he'll voice more as we go.

---

<!-- APPEND NEW REPORTS BELOW THIS LINE -->
<!-- Template:
N. **Short title** — description of the issue as Balpien framed it.
   - _Context/notes:_ related code, adjacency to known work, repro hints.
   - _Reported:_ <date>
-->
