// Legion.Physics.TestHarness - M1 DEFINITION-OF-DONE harness
//
// Proves Milestone 1 with ZERO OpenSim: it drives ILegionPhysicsBackend (Jolt) directly through
// terrain cook + placement, terrain extent, Z-up orientation, a stable static box, and a raycast.
// Self-contained: `dotnet run` from this project, no external setup, no files.
//
// Replaces the Task 2 binding smoke test. Single overall PASS/FAIL with per-check numbers.

using System;
using System.Numerics;
using Legion.Physics;
using Legion.Physics.Jolt;

internal static class Program
{
    private const int N = 256;                 // 256x256 region field (square, power-of-two)
    private const float S = 1.0f;              // 1 metre sample spacing / unit height scale
    private static int _fails;

    private static void Check(bool ok, string msg)
    {
        Console.WriteLine((ok ? "  [ok]   " : "  [FAIL] ") + msg);
        if (!ok) _fails++;
    }

    private static float[] FlatField() => new float[N * N]; // all zero

    // Raise the CENTRAL grid block [64,192)^2 to `height`. Centred, so it maps to the centre of the
    // world terrain under ANY in-plane axis convention - the orientation check then depends only on
    // height landing on +Z, which is the thing under test.
    private static float[] CentralPlateauField(float height)
    {
        var f = new float[N * N];
        for (int row = 64; row < 192; row++)
            for (int col = 64; col < 192; col++)
                f[row * N + col] = height;
        return f;
    }

    // Convention: heights[y*N + x] = height at grid (x, y); the cooked shape must place it at world (x, y).
    private static void SetH(float[] f, int x, int y, float h) => f[y * N + x] = h;

    // A step in X: height jumps from lo to hi at column `stepCol`. (Feature is in X only, so the
    // in-plane row axis does not affect it - a clean silent-mis-cook detector.)
    private static float[] StepFieldX(float lo, float hi, int stepCol)
    {
        var f = new float[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                f[y * N + x] = x >= stepCol ? hi : lo;
        return f;
    }

    // Four DISTINCT quadrant heights - asymmetric in BOTH axes, so it detects an X- or Y-mirror.
    private static float[] QuadrantField(float sw, float se, float nw, float ne)
    {
        var f = new float[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                f[y * N + x] = x < N / 2 ? (y < N / 2 ? sw : nw) : (y < N / 2 ? se : ne);
        return f;
    }

    // Cast straight down from high Z; returns true on hit and reports the world-Z of the surface.
    private static bool RayDown(ILegionPhysicsBackend b, float x, float y, out float surfaceZ, out RayHit hit)
    {
        bool ok = b.RayCast(new Vector3(x, y, 1000f), new Vector3(0, 0, -1), 2000f, QueryFilter.All, out hit);
        surfaceZ = ok ? hit.Point.Z : float.NaN;
        return ok;
    }

    private static int Main()
    {
        Console.WriteLine("=== Legion.Physics M1 harness (Jolt backend, no OpenSim) ===");
        var backend = new JoltPhysicsBackend();
        backend.Initialize(PhysicsBackendSettings.Default);

        try
        {
            // ---- 1. TERRAIN: cook a flat 256x256 field and place it. ----
            Console.WriteLine("\n[1] Terrain cook + placement");
            ShapeId flat = backend.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
            Check(flat.IsValid, $"flat {N}x{N} heightfield cooked -> {flat}");
            backend.SetTerrain(flat, Vector3.Zero);
            Check(true, "SetTerrain(flat, origin) placed, no throw");

            // ---- 2. EXTENT: does N samples at scale s span (N-1)*s or N*s? ----
            Console.WriteLine("\n[2] Terrain extent (raycast down, terrain surface should be Z~0)");
            (float x, float y)[] probes = { (0, 0), (0.5f, 0.5f), (1, 1), (128, 128), (254, 254), (255, 255), (255.5f, 255.5f), (256, 256) };
            foreach (var (px, py) in probes)
            {
                bool hit = RayDown(backend, px, py, out float z, out _);
                Console.WriteLine($"      ({px,6:0.0},{py,6:0.0}) -> {(hit ? $"HIT  z={z,7:0.000}" : "miss")}");
            }
            // The interior + far edge define the span; the exact outer VERTEX (0,0)/(255,255) can graze
            // due to floating point and is not a coverage claim either way.
            Check(RayDown(backend, 1, 1, out _, out _), "hits near origin (1,1)");
            Check(RayDown(backend, 128, 128, out _, out _), "hits at centre (128,128)");
            Check(RayDown(backend, 254, 254, out _, out _), "hits at (254,254) - inside far edge");
            Check(!RayDown(backend, 255.5f, 255.5f, out _, out _), "MISS at (255.5,255.5) - beyond far edge");
            Check(!RayDown(backend, 256, 256, out _, out _), "MISS at (256,256) - beyond far edge");
            Console.WriteLine($"      => EXTENT: a {N}-sample field at scale s={S} spans [0, {(N - 1) * S:0}] = (N-1)*s = {(N - 1) * S:0} m");
            Console.WriteLine($"         (covers up to sample index {N - 1}; a {N}m region would fall 1m short at the far edge -> caller must scale by N/(N-1) or accept the gap)");

            // ---- 3. ORIENTATION: raised samples must read HIGH in world Z. ----
            Console.WriteLine("\n[3] Orientation (Y-up cook must become Z-up; height on +Z)");
            const float PlateauH = 20f;
            ShapeId raised = backend.CreateHeightFieldShape(CentralPlateauField(PlateauH), N, N, new Vector3(S, S, S));
            backend.SetTerrain(raised, Vector3.Zero);
            bool cHit = RayDown(backend, 127.5f, 127.5f, out float centreZ, out RayHit cHitInfo);
            bool kHit = RayDown(backend, 10f, 10f, out float cornerZ, out _);
            Console.WriteLine($"      centre (127.5,127.5) -> {(cHit ? $"z={centreZ:0.000}" : "miss")}   corner (10,10) -> {(kHit ? $"z={cornerZ:0.000}" : "miss")}");
            Check(cHit && kHit, "both centre and corner hit (terrain lies flat in XY, not a Y-up wall)");
            Check(cHit && MathF.Abs(centreZ - PlateauH) < 1.0f, $"raised centre reads world-Z ~{PlateauH} (got {centreZ:0.000})");
            Check(kHit && MathF.Abs(cornerZ) < 1.0f, $"flat corner reads world-Z ~0 (got {cornerZ:0.000})");
            Check(cHit && kHit && (centreZ - cornerZ) > 10f, $"raised is HIGHER than flat by {centreZ - cornerZ:0.000} m in +Z (wrong sign/axis would fail)");
            Check(cHit && cHitInfo.Normal.Z > 0.9f, $"terrain surface normal points +Z up (n.z={cHitInfo.Normal.Z:0.000})");

            // restore flat terrain for the box tests
            backend.SetTerrain(flat, Vector3.Zero);

            // ---- 4. STATIC BOX: placed on terrain, must not drift across 60 steps. ----
            Console.WriteLine("\n[4] Static box stability (60 steps, must be bit-stable)");
            ShapeId box = backend.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));
            var desc = BodyDesc.Default;
            desc.Shape = box;
            desc.Position = new Vector3(10f, 10f, 0.5f);  // resting on flat terrain (bottom at Z=0)
            desc.Orientation = Quaternion.Identity;
            desc.Layer = PhysicsLayer.Static;
            desc.MotionType = BodyMotionType.Static;
            desc.UserData = 7777u;
            BodyId boxBody = backend.CreateBody(desc);
            Check(boxBody.IsValid, $"static box created -> {boxBody}");

            backend.TryGetBodyState(boxBody, out BodyState before);
            var buf = new BodyState[8];
            var chars = new CharacterState[4];
            var contacts = new ContactReport[16];
            int totalActive = 0;
            for (int i = 0; i < 60; i++)
            {
                StepResult r = backend.Step(1f / 60f, buf, chars, contacts);
                totalActive += r.ActiveBodyCount;
            }
            backend.TryGetBodyState(boxBody, out BodyState after);
            Check(totalActive == 0, $"0 active bodies across 60 steps (static-only) - sum={totalActive}");
            bool stable = before.Position == after.Position && before.Orientation == after.Orientation;
            Check(stable, $"box transform bit-stable: {before.Position} == {after.Position}");

            // ---- 5. RAYCAST: down onto the box. ----
            Console.WriteLine("\n[5] Raycast onto the box");
            bool rc = backend.RayCast(new Vector3(10f, 10f, 50f), new Vector3(0, 0, -1), 100f, QueryFilter.All, out RayHit bh);
            Check(rc, "downward ray from above the box hits something");
            Check(rc && bh.Body.Equals(boxBody), $"hit body is the box ({bh.Body})");
            Check(rc && bh.UserData == 7777u, $"hit UserData == 7777 (got {bh.UserData})");
            // box top = 0.5 (centre) + 0.5 (half-extent) = 1.0; ray starts at Z=50 -> distance 49.0
            Check(rc && MathF.Abs(bh.Distance - 49.0f) < 0.05f, $"distance ~49.0 (got {bh.Distance:0.000})");
            Check(rc && MathF.Abs(bh.Point.Z - 1.0f) < 0.05f, $"hit point Z ~1.0 (box top) (got {bh.Point.Z:0.000})");
            Check(rc && bh.Normal.Z > 0.9f, $"hit normal points +Z up (n.z={bh.Normal.Z:0.000})");

            // ---- 6. GEOMETRY FIDELITY: cooked surface must match input samples (silent mis-cook guard). ----
            Console.WriteLine("\n[6] Geometry fidelity (step at col 200; surface must match input incl. far edge)");
            ShapeId step = backend.CreateHeightFieldShape(StepFieldX(0f, 20f, 200), N, N, new Vector3(S, S, S));
            backend.SetTerrain(step, Vector3.Zero);
            foreach (var (x, exp) in new (float x, float exp)[] { (100, 0), (199, 0), (200, 20), (201, 20), (255, 20) })
            {
                bool h = RayDown(backend, x, 128f, out float z, out _);
                Check(h && MathF.Abs(z - exp) < 0.6f, $"step x={x,3}: surface Z ~{exp,2} (got {(h ? z.ToString("0.00") : "MISS")})");
            }

            // ---- 7. ASYMMETRIC PLACEMENT: distinct per-quadrant heights must land where the INPUT put them. ----
            //         Centre-symmetric fields (like [3]) cannot see an in-plane mirror; this can.
            Console.WriteLine("\n[7] Asymmetric placement (input SW=5 SE=10 NW=15 NE=20 -> must read at those WORLD quadrants)");
            ShapeId quad = backend.CreateHeightFieldShape(QuadrantField(5f, 10f, 15f, 20f), N, N, new Vector3(S, S, S));
            backend.SetTerrain(quad, Vector3.Zero);
            foreach (var (x, y, exp, name) in new (float x, float y, float exp, string name)[]
                { (64, 64, 5, "SW"), (192, 64, 10, "SE"), (64, 192, 15, "NW"), (192, 192, 20, "NE") })
            {
                bool h = RayDown(backend, x, y, out float z, out _);
                Check(h && MathF.Abs(z - exp) < 0.6f, $"{name} world({x,3},{y,3}): height ~{exp,2} (got {(h ? z.ToString("0.00") : "MISS")})");
            }

            // ================= MILESTONE 2 - DYNAMICS =================

            // ---- 8. DYNAMIC DROP: box falls under gravity, lands, comes to REST and sleeps. ----
            Console.WriteLine("\n[8] Dynamic box: fall -> land -> sleep (active-set + JustDeactivated)");
            DropMetrics dm = RunDropScenario(backend, null);
            Console.WriteLine($"      steps-to-sleep={dm.StepsToSleep}  maxActive={dm.MaxActive}  finalActive={dm.FinalActive}  " +
                              $"JustActivated={dm.JustActivatedCount}  JustDeactivated={dm.JustDeactivatedCount}");
            Console.WriteLine($"      resting transform: pos={dm.RestPos}  orient={dm.RestOrient}");
            Check(dm.MaxActive == 1, $"ActiveBodyCount rose to 1 while falling (max={dm.MaxActive})");
            Check(dm.FinalActive == 0, $"ActiveBodyCount back to 0 after sleep (final={dm.FinalActive})");
            Check(dm.JustActivatedCount == 1, $"JustActivated emitted exactly once on activation (got {dm.JustActivatedCount})");
            // THE check that matters most: exactly one final update carrying the resting transform, or the
            // viewer keeps interpolating and settled objects visibly drift.
            Check(dm.JustDeactivatedCount == 1, $"JustDeactivated emitted EXACTLY once on sleep (got {dm.JustDeactivatedCount}, must be 1 not >=1)");
            Check(dm.GotFinal && MathF.Abs(dm.RestPos.Z - 0.5f) < 0.1f,
                $"box rests with bottom on terrain: centre Z ~0.5 (got {dm.RestPos.Z:0.000})");
            Check(dm.GotFinal && MathF.Abs(dm.RestPos.X - 20f) < 0.05f && MathF.Abs(dm.RestPos.Y - 20f) < 0.05f,
                $"box did not drift in XY while falling (got X={dm.RestPos.X:0.000} Y={dm.RestPos.Y:0.000})");

            // ---- 9. GRAVITY FACTOR: 0 hovers, negative rises. ----
            Console.WriteLine("\n[9] GravityFactor (0 = hover, -1 = rise)");
            ShapeId dynBox = backend.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));
            var buf9 = new BodyState[8];
            var chars9 = new CharacterState[2];
            var contacts9 = new ContactReport[16];

            var hoverDesc = BodyDesc.Default;
            hoverDesc.Shape = dynBox;
            hoverDesc.Position = new Vector3(30f, 30f, 25f);
            hoverDesc.MotionType = BodyMotionType.Dynamic;
            hoverDesc.Layer = PhysicsLayer.Dynamic;
            hoverDesc.GravityFactor = 0f;
            hoverDesc.StartActive = true;
            BodyId hover = backend.CreateBody(hoverDesc);

            var riseDesc = BodyDesc.Default;
            riseDesc.Shape = dynBox;
            riseDesc.Position = new Vector3(35f, 35f, 25f);
            riseDesc.MotionType = BodyMotionType.Dynamic;
            riseDesc.Layer = PhysicsLayer.Dynamic;
            riseDesc.GravityFactor = -1f;
            riseDesc.StartActive = true;
            BodyId riser = backend.CreateBody(riseDesc);

            for (int i = 0; i < 90; i++)
                backend.Step(1f / 60f, buf9, chars9, contacts9);

            backend.TryGetBodyState(hover, out BodyState hoverState);
            backend.TryGetBodyState(riser, out BodyState riseState);
            Console.WriteLine($"      hover (gf=0)  Z: 25.0 -> {hoverState.Position.Z:0.000}    riser (gf=-1) Z: 25.0 -> {riseState.Position.Z:0.000}");
            Check(MathF.Abs(hoverState.Position.Z - 25f) < 0.1f, $"gravityFactor 0 hovers in place (Z {hoverState.Position.Z:0.000} ~ 25.0)");
            Check(riseState.Position.Z > 26f, $"gravityFactor -1 RISES against gravity (Z {riseState.Position.Z:0.000} > 26)");

            // ---- 10. MASS + IMPULSE: verify computed mass via impulse, and Δv = impulse / mass. ----
            Console.WriteLine("\n[10] Mass-from-density + ApplyImpulse (Δv = impulse / mass)");
            // A unit box (half-extent 0.5 -> Volume 1.0 m^3) at default Density 1000 -> mass 1000 kg.
            // Hand arithmetic: 8 * 0.5^3 * 1000 = 1000 kg. Mass is not exposed on the seam, so we
            // verify it through the public interface: apply a known impulse and read Δv, then
            // inferredMass = impulse / Δv. GravityFactor 0 isolates the measurement from gravity.
            const float HandMass = 8f * 0.5f * 0.5f * 0.5f * 1000f; // = 1000
            var impDesc = BodyDesc.Default;
            impDesc.Shape = dynBox;
            impDesc.Position = new Vector3(40f, 40f, 25f);
            impDesc.MotionType = BodyMotionType.Dynamic;
            impDesc.Layer = PhysicsLayer.Dynamic;
            impDesc.GravityFactor = 0f;
            impDesc.StartActive = true;
            BodyId impBody = backend.CreateBody(impDesc);

            backend.TryGetBodyState(impBody, out BodyState impBefore);
            const float ImpulseZ = 2000f;
            backend.ApplyImpulse(impBody, new Vector3(0f, 0f, ImpulseZ)); // AddImpulse changes velocity instantly
            backend.TryGetBodyState(impBody, out BodyState impAfter);
            float dv = impAfter.LinearVelocity.Z - impBefore.LinearVelocity.Z;
            float inferredMass = dv != 0f ? ImpulseZ / dv : float.NaN;
            Console.WriteLine($"      hand-arithmetic mass = {HandMass:0.0} kg (8 * 0.5^3 * 1000)");
            Console.WriteLine($"      impulse {ImpulseZ:0} Ns -> Δv = {dv:0.000} m/s -> inferred mass = {inferredMass:0.00} kg");
            Check(MathF.Abs(dv - ImpulseZ / HandMass) < 1e-3f, $"Δv == impulse/mass = {ImpulseZ / HandMass:0.000} (got {dv:0.000})");
            Check(MathF.Abs(inferredMass - HandMass) < 1f, $"computed mass matches hand arithmetic {HandMass:0.0} kg (inferred {inferredMass:0.00})");

            backend.RemoveBody(hover);
            backend.RemoveBody(riser);
            backend.RemoveBody(impBody);
            backend.ReleaseShape(dynBox);

            // ---- 12. CONTACT LIFECYCLE: Begin on landing, Persist while settling, End on removal. ----
            Console.WriteLine("\n[12] Contact lifecycle (Begin/Persist/End, UserData both sides, normal A->B, impulse)");
            ContactTally ct = RunContactDrop(backend, 10f, wantsEvents: true, boxUD: 4242u, contactsBufLen: 16);
            Console.WriteLine($"      Begin={ct.Begin} Persist={ct.Persist} End={ct.End}  maxImpulse={ct.MaxImpulse:0} Ns  landingNormal={ct.LandingNormal}");
            Console.WriteLine($"      persistBeforeSleep={ct.PersistBeforeSleep} persistAfterSleep={ct.PersistAfterSleep} sleepStep={ct.SleepStep} overflowSeen={ct.OverflowSeen}");
            Check(ct.Begin >= 1, $"Begin fired on landing (got {ct.Begin})");
            Check(ct.Persist >= 1, $"Persist fired while settling (got {ct.Persist})");
            Check(ct.End >= 1, $"End fired when the box was removed (got {ct.End})");
            Check(ct.AnyReport && ct.AllBothSidesResolved, "both BodyA and BodyB resolved on every contact report");
            Check(ct.AllBoxSideOk, "box side UserData == 4242 and maps to the box body");
            Check(ct.AllTerrainSideOk, "terrain side UserData == 0 and is a valid body");
            // Contract: Normal points A->B. The box lands ON TOP of the terrain, so A->B is vertical;
            // its sign depends on which body Jolt made A. If box is A (upper), A->B points DOWN; if the
            // terrain is A (lower), A->B points UP. Either is correct - assert consistency, not a fixed sign.
            Check(ct.GotLandingNormal && MathF.Abs(ct.LandingNormal.Z) > 0.9f
                  && (ct.LandingBoxIsA ? ct.LandingNormal.Z < 0f : ct.LandingNormal.Z > 0f),
                $"landing normal is vertical and points A->B (box is {(ct.LandingBoxIsA ? "A->down" : "B->up")}, n.z={ct.LandingNormal.Z:0.000})");
            Check(ct.MaxImpulse > 0f, $"impulse non-zero on impact (peak {ct.MaxImpulse:0} Ns)");
            Check(!ct.OverflowSeen, "contact ring overflow flag FALSE in normal operation");
            // decision #4 input, baked in as a permanent regression: sleep silences Persist.
            Check(ct.PersistAfterSleep == 0, $"Persist STOPS once the body sleeps (after-sleep Persist = {ct.PersistAfterSleep})  [#4 input]");

            // ---- 13. IMPULSE SCALES WITH DROP HEIGHT. ----
            Console.WriteLine("\n[13] Impulse scales with drop height (harder landing -> larger impulse)");
            ContactTally lo = RunContactDrop(backend, 2f, wantsEvents: true, boxUD: 11u, contactsBufLen: 16);
            ContactTally hi = RunContactDrop(backend, 30f, wantsEvents: true, boxUD: 22u, contactsBufLen: 16);
            Console.WriteLine($"      drop 2m -> peak {lo.MaxImpulse:0} Ns    drop 30m -> peak {hi.MaxImpulse:0} Ns");
            Check(lo.MaxImpulse > 0f && hi.MaxImpulse > lo.MaxImpulse,
                $"higher drop yields larger peak impulse ({hi.MaxImpulse:0} > {lo.MaxImpulse:0})");

            // ---- 14. PERSIST GATE: WantsContactEvents=false suppresses Persist; Begin/End still fire. ----
            Console.WriteLine("\n[14] Persist gate (WantsContactEvents=false suppresses Persist, edge events survive)");
            ContactTally gated = RunContactDrop(backend, 10f, wantsEvents: false, boxUD: 55u, contactsBufLen: 16);
            Console.WriteLine($"      wants=false: Begin={gated.Begin} Persist={gated.Persist} End={gated.End}");
            Check(gated.Begin >= 1, $"Begin still fires when gated (edge event, got {gated.Begin})");
            Check(gated.End >= 1, $"End still fires when gated (edge event, got {gated.End})");
            Check(gated.Persist == 0, $"Persist SUPPRESSED when WantsContactEvents=false (got {gated.Persist}, want 0)");

            // ---- 15. OVERFLOW must SURFACE, not silently eat: tiny buffer + many simultaneous contacts. ----
            Console.WriteLine("\n[15] Contact overflow trips the flag and drains safely (tiny buffer, many contacts)");
            ContactTally of = RunManyContactsOverflow(backend);
            Console.WriteLine($"      overflowSeen={of.OverflowSeen} drainSafe={of.DrainSafe}");
            Check(of.OverflowSeen, "ContactBufferOverflowed reported TRUE when contacts exceed the buffer");
            Check(of.DrainSafe, "drain stayed safe under overflow (ContactCount never exceeded the buffer)");

            // cleanup
            backend.RemoveBody(boxBody);
            backend.ReleaseShape(box);
            backend.ReleaseShape(flat);
            backend.ReleaseShape(raised);
            backend.ReleaseShape(step);
            backend.ReleaseShape(quad);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            _fails++;
        }
        finally
        {
            backend.Dispose();
        }

        // ---- 11. DETERMINISM: the same drop, twice, in DeterministicMode, must match bit-for-bit. ----
        // Runs with FRESH backends AFTER the main one is disposed - Foundation.Init/Shutdown is global,
        // so the two deterministic runs go strictly one-after-another. This is the seed of the A/B
        // parity harness DESIGN.md calls for.
        RunDeterminismCheck();

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "=== M1+M2 HARNESS: PASS ==="
            : $"=== M1+M2 HARNESS: FAIL ({_fails} failed check(s)) ===");
        return _fails == 0 ? 0 : 1;
    }

    // Metrics collected from one drop of a dynamic box onto flat terrain.
    private struct DropMetrics
    {
        public int StepsToSleep;
        public int JustActivatedCount;
        public int JustDeactivatedCount;
        public int MaxActive;
        public int FinalActive;
        public Vector3 RestPos;
        public Quaternion RestOrient;
        public bool GotFinal;
    }

    // Drops a Dynamic unit box from Z=10 onto flat terrain and steps until it sleeps (or a hard cap).
    // Self-contained on the given backend: creates its own terrain + shape + body and cleans them up.
    // If posLog != null, records the body's position on every step it appears in the active drain -
    // the trajectory two deterministic runs are compared on.
    private static DropMetrics RunDropScenario(ILegionPhysicsBackend b, System.Collections.Generic.List<Vector3> posLog)
    {
        ShapeId flat = b.CreateHeightFieldShape(new float[N * N], N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);
        ShapeId box = b.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));

        var d = BodyDesc.Default;
        d.Shape = box;
        d.Position = new Vector3(20f, 20f, 10f);
        d.MotionType = BodyMotionType.Dynamic;
        d.Layer = PhysicsLayer.Dynamic;
        d.StartActive = true;
        d.UserData = 4242u;
        d.WantsContactEvents = true;   // exercise the full contact path (Persist + impulse estimate)
                                       // during the drop, incl. the determinism runs in [11].
        BodyId body = b.CreateBody(d);

        var m = new DropMetrics();
        var buf = new BodyState[8];
        var chars = new CharacterState[2];
        var contacts = new ContactReport[64];
        const int MaxSteps = 1200;
        int sleptAt = -1;

        for (int i = 0; i < MaxSteps; i++)
        {
            StepResult r = b.Step(1f / 60f, buf, chars, contacts);
            if (r.ActiveBodyCount > m.MaxActive) m.MaxActive = r.ActiveBodyCount;
            m.FinalActive = r.ActiveBodyCount;

            for (int k = 0; k < r.BodyUpdateCount; k++)
            {
                if (!buf[k].Body.Equals(body)) continue;
                if ((buf[k].Flags & BodyStateFlags.JustActivated) != 0) m.JustActivatedCount++;
                if ((buf[k].Flags & BodyStateFlags.JustDeactivated) != 0)
                {
                    m.JustDeactivatedCount++;
                    if (!m.GotFinal)
                    {
                        m.RestPos = buf[k].Position;
                        m.RestOrient = buf[k].Orientation;
                        m.StepsToSleep = i + 1;
                        m.GotFinal = true;
                        sleptAt = i;
                    }
                }
                posLog?.Add(buf[k].Position);
            }

            // Settled: run a few more frames to prove it does NOT re-activate, then stop.
            if (sleptAt >= 0 && i > sleptAt + 5)
                break;
        }

        b.RemoveBody(body);
        b.ReleaseShape(box);
        b.ReleaseShape(flat);
        return m;
    }

    // Tally of contact reports observed across one drop.
    private struct ContactTally
    {
        public int Begin, Persist, End;
        public int PersistBeforeSleep, PersistAfterSleep;
        public float MaxImpulse;
        public Vector3 LandingNormal;
        public bool LandingBoxIsA;         // at the landing contact, was the box BodyA? (fixes normal sign expectation)
        public bool GotLandingNormal;
        public bool AnyReport;              // saw at least one Begin/Persist (makes the *Ok flags meaningful)
        public bool AllBothSidesResolved;   // every Begin/Persist had BodyA AND BodyB valid
        public bool AllBoxSideOk;           // box side UserData matched boxUD and mapped to the box body
        public bool AllTerrainSideOk;       // other side had UserData 0 and a valid body
        public bool OverflowSeen;
        public bool DrainSafe;
        public int SleepStep;
    }

    private static void ClassifyPair(ref ContactTally t, in ContactReport c, BodyId box, uint boxUD)
    {
        t.AnyReport = true;
        if (!(c.BodyA.IsValid && c.BodyB.IsValid)) t.AllBothSidesResolved = false;
        bool aIsBox = c.BodyA.Equals(box);
        bool bIsBox = c.BodyB.Equals(box);
        if (aIsBox ^ bIsBox)
        {
            uint boxSideUD = aIsBox ? c.UserDataA : c.UserDataB;
            uint terrSideUD = aIsBox ? c.UserDataB : c.UserDataA;
            BodyId terrSide = aIsBox ? c.BodyB : c.BodyA;
            if (boxSideUD != boxUD) t.AllBoxSideOk = false;
            if (!(terrSideUD == 0u && terrSide.IsValid)) t.AllTerrainSideOk = false;
        }
        else
        {
            t.AllBoxSideOk = false; // neither or both side is the box -> unexpected pairing
        }
    }

    // Drops one Dynamic box onto flat terrain and tallies the contact reports Begin/Persist/End,
    // then removes the box and steps once more to catch End. contactsBufLen sizes the per-step
    // contact buffer (small values deliberately probe the overflow path).
    private static ContactTally RunContactDrop(ILegionPhysicsBackend b, float height, bool wantsEvents, uint boxUD, int contactsBufLen)
    {
        ShapeId flat = b.CreateHeightFieldShape(new float[N * N], N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);
        ShapeId box = b.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));

        var d = BodyDesc.Default;
        d.Shape = box;
        d.Position = new Vector3(20f, 20f, height);
        d.MotionType = BodyMotionType.Dynamic;
        d.Layer = PhysicsLayer.Dynamic;
        d.StartActive = true;
        d.UserData = boxUD;
        d.WantsContactEvents = wantsEvents;
        BodyId body = b.CreateBody(d);

        var t = new ContactTally { AllBothSidesResolved = true, AllBoxSideOk = true, AllTerrainSideOk = true, DrainSafe = true, SleepStep = -1 };
        var buf = new BodyState[8];
        var chars = new CharacterState[2];
        var contacts = new ContactReport[Math.Max(1, contactsBufLen)];
        const int MaxSteps = 1200;

        for (int i = 0; i < MaxSteps; i++)
        {
            StepResult r = b.Step(1f / 60f, buf, chars, contacts);
            if (r.ContactBufferOverflowed) t.OverflowSeen = true;
            if (r.ContactCount > contacts.Length) t.DrainSafe = false;

            for (int k = 0; k < r.ContactCount; k++)
            {
                ContactReport c = contacts[k];
                switch (c.Phase)
                {
                    case ContactPhase.Begin:
                        t.Begin++;
                        ClassifyPair(ref t, in c, body, boxUD);
                        if (!t.GotLandingNormal) { t.LandingNormal = c.Normal; t.LandingBoxIsA = c.BodyA.Equals(body); t.GotLandingNormal = true; }
                        if (c.Impulse > t.MaxImpulse) t.MaxImpulse = c.Impulse;
                        break;
                    case ContactPhase.Persist:
                        t.Persist++;
                        ClassifyPair(ref t, in c, body, boxUD);
                        if (c.Impulse > t.MaxImpulse) t.MaxImpulse = c.Impulse;
                        if (t.SleepStep < 0) t.PersistBeforeSleep++; else t.PersistAfterSleep++;
                        break;
                    case ContactPhase.End:
                        t.End++;
                        break;
                }
            }

            if (t.SleepStep < 0 && r.ActiveBodyCount == 0 && i > 0)
                t.SleepStep = i;
            if (t.SleepStep >= 0 && i > t.SleepStep + 30) // observe post-sleep Persist for a while, then stop
                break;
        }

        // Remove the box -> End should fire on the next step for the resting pair.
        b.RemoveBody(body);
        StepResult rEnd = b.Step(1f / 60f, buf, chars, contacts);
        for (int k = 0; k < rEnd.ContactCount; k++)
            if (contacts[k].Phase == ContactPhase.End) t.End++;

        b.ReleaseShape(box);
        b.ReleaseShape(flat);
        return t;
    }

    // Rests many Dynamic boxes on terrain (all wanting contact events) and drains through a
    // deliberately tiny 2-slot contact buffer, forcing the overflow flag to trip. Proves the
    // overflow is SURFACED (flag true) and the drain stays safe (never writes past the buffer).
    private static ContactTally RunManyContactsOverflow(ILegionPhysicsBackend b)
    {
        ShapeId flat = b.CreateHeightFieldShape(new float[N * N], N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);
        ShapeId box = b.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));

        var bodies = new System.Collections.Generic.List<BodyId>();
        const int Count = 24;
        for (int i = 0; i < Count; i++)
        {
            var d = BodyDesc.Default;
            d.Shape = box;
            d.Position = new Vector3(10f + i * 2f, 10f, 0.5f); // bottom on terrain, 2 m apart (no mutual overlap)
            d.MotionType = BodyMotionType.Dynamic;
            d.Layer = PhysicsLayer.Dynamic;
            d.StartActive = true;
            d.WantsContactEvents = true;
            d.UserData = (uint)(1000 + i);
            bodies.Add(b.CreateBody(d));
        }

        var t = new ContactTally { DrainSafe = true };
        var buf = new BodyState[64];
        var chars = new CharacterState[2];
        var contacts = new ContactReport[2]; // TINY on purpose

        for (int i = 0; i < 30; i++)
        {
            StepResult r = b.Step(1f / 60f, buf, chars, contacts);
            if (r.ContactBufferOverflowed) t.OverflowSeen = true;
            if (r.ContactCount > contacts.Length) t.DrainSafe = false;
        }

        foreach (BodyId bd in bodies) b.RemoveBody(bd);
        b.ReleaseShape(box);
        b.ReleaseShape(flat);
        return t;
    }

    private static void RunDeterminismCheck()
    {
        Console.WriteLine("\n[11] Determinism (two identical DeterministicMode runs, bit-for-bit)");
        var settings = PhysicsBackendSettings.Default;
        settings.DeterministicMode = true;

        var logA = new System.Collections.Generic.List<Vector3>();
        var logB = new System.Collections.Generic.List<Vector3>();

        var a = new JoltPhysicsBackend();
        a.Initialize(settings);
        try { RunDropScenario(a, logA); }
        finally { a.Dispose(); }

        var b = new JoltPhysicsBackend();
        b.Initialize(settings);
        try { RunDropScenario(b, logB); }
        finally { b.Dispose(); }

        Check(logA.Count > 0, $"run A recorded a trajectory ({logA.Count} states)");
        Check(logA.Count == logB.Count, $"both runs recorded the same number of states (A={logA.Count} B={logB.Count})");

        bool identical = logA.Count == logB.Count;
        int firstDiff = -1;
        for (int i = 0; identical && i < logA.Count; i++)
            if (logA[i] != logB[i]) { identical = false; firstDiff = i; }

        Check(identical, identical
            ? $"all {logA.Count} recorded transforms bit-identical across runs"
            : $"DIVERGED at state {firstDiff}: {logA[firstDiff]} vs {logB[firstDiff]}");
    }
}
