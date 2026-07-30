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
    private static float[] MoundField() { var f = new float[N * N]; int c = N / 2; float peak = 25f, rad = 100f; for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) { float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)); f[y * N + x] = MathF.Max(0f, peak * (1f - d / rad)); } return f; }

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

            // ================= MILESTONE 3 - THE AVATAR (CharacterVirtual) =================
            // Mechanism checks only - feel is John's call in a viewer at M6.
            backend.SetTerrain(flat, Vector3.Zero);
            var cbuf = new CharacterState[4];
            var abuf = new BodyState[16];
            var acont = new ContactReport[32];

            // ---- 16. SPAWN + REST: supported, no sink, no jitter. ----
            Console.WriteLine("\n[16] Character spawns on terrain: IsSupported, rests without sinking");
            var adesc = CharacterDesc.Default;   // capsule 0.45/0.30 -> centre rests at 0.75 on z=0
            adesc.Position = new Vector3(20f, 20f, 0.75f);
            adesc.UserData = 9001u;
            CharacterId avatar = backend.CreateCharacter(adesc);
            Check(avatar.IsValid, $"character created -> {avatar}");
            for (int i = 0; i < 60; i++) { backend.SetCharacterMovement(avatar, Vector3.Zero, false, false); backend.Step(1f / 60f, abuf, cbuf, acont); }
            backend.TryGetCharacterState(avatar, out CharacterState rest);
            Console.WriteLine($"      rest pos={rest.Position} IsSupported={rest.IsSupported} groundNormal={rest.GroundNormal}");
            Check(rest.IsSupported, "IsSupported true at rest");
            Check(MathF.Abs(rest.Position.Z - 0.75f) < 0.05f, $"rests without sinking: centre Z ~0.75 (got {rest.Position.Z:0.000})");
            Check(MathF.Abs(rest.Position.X - 20f) < 0.02f && MathF.Abs(rest.Position.Y - 20f) < 0.02f, "no horizontal drift at rest");
            Check(rest.GroundNormal.Z > 0.9f, $"ground normal points +Z (n.z={rest.GroundNormal.Z:0.000})");

            // ---- 17. WALK across flat terrain. ----
            Console.WriteLine("\n[17] Walk a horizontal velocity across flat terrain");
            float wx0 = rest.Position.X;
            for (int i = 0; i < 60; i++) { backend.SetCharacterMovement(avatar, new Vector3(2f, 0f, 0f), false, false); backend.Step(1f / 60f, abuf, cbuf, acont); }
            backend.TryGetCharacterState(avatar, out CharacterState walked);
            Console.WriteLine($"      X {wx0:0.00} -> {walked.Position.X:0.00} (1 s @ 2 m/s)  z={walked.Position.Z:0.00}");
            Check(MathF.Abs((walked.Position.X - wx0) - 2f) < 0.25f, $"advanced ~2 m in X (got {walked.Position.X - wx0:0.00})");
            Check(walked.IsSupported && MathF.Abs(walked.Position.Z - 0.75f) < 0.05f, "stayed on the ground while walking");

            // ---- 18. STEP UP below StepHeight; BLOCKED above it. Both sides of the threshold. ----
            Console.WriteLine("\n[18] Steps up a rise <= StepHeight (0.45); BLOCKED by a rise above it");
            (bool climbedLow, float zLow, float xLow) = WalkIntoStep(backend, 0.30f, 0);
            (bool climbedHigh, float zHigh, float xHigh) = WalkIntoStep(backend, 0.80f, 1);
            Console.WriteLine($"      low step 0.30: climbed={climbedLow} (z={zLow:0.00})   high step 0.80: climbed={climbedHigh} (z={zHigh:0.00})");
            Check(climbedLow, $"climbs a 0.30 m step (< StepHeight): ended z={zLow:0.00} (~1.05 expected)");
            Check(!climbedHigh, $"BLOCKED by a 0.80 m step (> StepHeight): stayed low z={zHigh:0.00} (~0.75 expected)");

            // ---- 19. SLOPE stable below MaxSlopeAngle; slides above it. Both sides. ----
            Console.WriteLine("\n[19] Slope: stable below MaxSlopeAngle (50 deg), IsSliding + slides above");
            (bool slid30, float drift30, bool sup30) = RunSlopeTest(backend, 30f, 0);
            (bool slid60, float drift60, bool sup60) = RunSlopeTest(backend, 60f, 1);
            Console.WriteLine($"      30 deg: IsSliding={slid30} drift={drift30:0.00} supported={sup30}   60 deg: IsSliding={slid60} drift={drift60:0.00} supported={sup60}");
            Check(!slid30 && drift30 < 0.6f, $"30 deg slope (< 50): stable, not sliding (drift {drift30:0.00})");
            Check(slid60 && drift60 > 0.6f, $"60 deg slope (> 50): IsSliding true and slides down (drift {drift60:0.00})");

            // ---- 20. MOVING PLATFORM: stands on a kinematic box and rides it. ----
            Console.WriteLine("\n[20] Rides a moving platform (kinematic box)");
            (float charDx, float boxDx, bool rode) = RunMovingPlatform(backend, 0);
            Console.WriteLine($"      platform moved {boxDx:0.00} m, character moved {charDx:0.00} m");
            Check(rode, $"character rode the platform (char {charDx:0.00} ~ platform {boxDx:0.00})");

            // ---- 21. PUSH a dynamic box; does NOT pass through. ----
            Console.WriteLine("\n[21] Pushes a dynamic box (PushStrength) and does not tunnel through it");
            (float pushBoxDx, bool noTunnel) = RunPushTest(backend, 0);
            Console.WriteLine($"      box pushed {pushBoxDx:0.00} m, tunnelled={( !noTunnel )}");
            Check(pushBoxDx > 0.3f, $"box was pushed forward (moved {pushBoxDx:0.00} m)");
            Check(noTunnel, "character did not pass through the box");

            // ---- 22. JUMP + FLYING. ----
            Console.WriteLine("\n[22] Jump initial velocity; flying disables ground gravity");
            for (int i = 0; i < 40; i++) { backend.SetCharacterMovement(avatar, Vector3.Zero, false, false); backend.Step(1f / 60f, abuf, cbuf, acont); }
            backend.SetCharacterMovement(avatar, Vector3.Zero, true, false);   // jump this frame
            backend.Step(1f / 60f, abuf, cbuf, acont);
            backend.TryGetCharacterState(avatar, out CharacterState jumped);
            float jumpVz = jumped.LinearVelocity.Z;
            float peak = jumped.Position.Z;
            for (int i = 0; i < 60; i++) { backend.SetCharacterMovement(avatar, Vector3.Zero, false, false); backend.Step(1f / 60f, abuf, cbuf, acont); backend.TryGetCharacterState(avatar, out CharacterState s); if (s.Position.Z > peak) peak = s.Position.Z; }
            Console.WriteLine($"      jump vz={jumpVz:0.00} (JumpSpeed 4.0), peak z={peak:0.00} (rest 0.75)");
            Check(jumpVz > 3.0f && jumpVz < 4.5f, $"jump initial vertical velocity ~JumpSpeed 4.0 (got {jumpVz:0.00})");
            Check(peak > 0.75f + 0.3f, $"jump left the ground (peak z {peak:0.00})");
            for (int i = 0; i < 60; i++) { backend.SetCharacterMovement(avatar, Vector3.Zero, false, false); backend.Step(1f / 60f, abuf, cbuf, acont); } // land
            for (int i = 0; i < 30; i++) { backend.SetCharacterMovement(avatar, new Vector3(0f, 0f, 2f), false, true); backend.Step(1f / 60f, abuf, cbuf, acont); }
            backend.TryGetCharacterState(avatar, out CharacterState flew);
            Console.WriteLine($"      flying up 2 m/s for 0.5 s -> z={flew.Position.Z:0.00}, supported={flew.IsSupported}");
            Check(flew.Position.Z > 0.75f + 0.5f, $"flying rises against gravity (z {flew.Position.Z:0.00})");
            Check(!flew.IsSupported, "flying: not ground-supported");

            // ---- 23. STATIONARY CHARACTER does not flood Persist (the #4 awake case). ----
            Console.WriteLine("\n[23] Stationary character does not flood Persist [#4]");
            for (int i = 0; i < 90; i++) { backend.SetCharacterMovement(avatar, Vector3.Zero, false, false); backend.Step(1f / 60f, abuf, cbuf, acont); } // fall back + settle
            int charPersist = 0;
            for (int i = 0; i < 100; i++)
            {
                backend.SetCharacterMovement(avatar, Vector3.Zero, false, false);
                StepResult r = backend.Step(1f / 60f, abuf, cbuf, acont);
                for (int k = 0; k < r.ContactCount; k++) if (acont[k].Phase == ContactPhase.Persist) charPersist++;
            }
            Console.WriteLine($"      Persist events over 100 rest steps = {charPersist} (avatar WantsContactEvents=false -> Persist GATED; Begin/End still fire)");
            Check(charPersist == 0, $"resting character produces no Persist flood when gated (got {charPersist})");

            backend.RemoveCharacter(avatar);

            // ---- 24. STAND ON A DYNAMIC BOX (M6.5 stand-on-box repro): the avatar should stand on a
            //         DYNAMIC rigid body the same way it rides the [20] kinematic platform. Diagnoses the
            //         live "resistance then fall through" John hit. A HEAVY box (~3375 kg) can't be shoved
            //         by the 80 kg character, so a sink-through here is a GROUND-DETECTION gap (c), not a
            //         push (b). The per-step log is the honest instrument (like [dropframe]).
            Console.WriteLine("\n[24] Stands on a DYNAMIC box (resistance-then-fall-through repro)");
            RunStandOnDynamicBox(backend);

            Console.WriteLine("\n[24b] Character SINK isolation: CollisionSteps + ground-body identity");
            RunCharacterGroundIdentity(6, 58f);           // standing, live-like config
            RunCharacterGroundIdentity(6, 58f, 1.5f);     // WALKING on flat terrain (John's action)

            Console.WriteLine("\n[24c] Sit/unsit cycle (M6.6): remove-on-sit / recreate-on-unsit, no leak over N cycles");
            RunSitUnsitCycle(backend);

            Console.WriteLine("\n[24d] RayCast vs RayCastAll surface normal (M6.7 Task 2 - llCastRay RC_GET_NORMAL)");
            RunRayCastNormal(backend);

            // ============ MILESTONE 3.5 - AVATAR AS COLLISION CITIZEN ============
            // Avatar contacts are reported via the CharacterVirtual's OWN contact events (not an inner
            // body). Side A of every avatar report is the avatar (Invalid BodyId, UserData = avatar id).

            // ---- 25. AVATAR generates contacts vs terrain, static, dynamic (UserData both sides). ----
            Console.WriteLine("\n[25] Avatar contacts vs terrain / static / dynamic (UserData both sides)");
            backend.SetTerrain(flat, Vector3.Zero);
            var seen = new System.Collections.Generic.HashSet<uint>();
            bool sideAok = true;
            int avBegin = 0, avPersist = 0, avEnd = 0;

            // Lane A (y=20): walk into a STATIC wall -> terrain + static.
            ShapeId wallShape = backend.CreateBoxShape(new Vector3(0.5f, 3f, 1f));
            var wallDesc = BodyDesc.Default; wallDesc.Shape = wallShape; wallDesc.Position = new Vector3(48f, 20f, 1f);
            wallDesc.MotionType = BodyMotionType.Static; wallDesc.Layer = PhysicsLayer.Static; wallDesc.UserData = 6001u;
            BodyId wall = backend.CreateBody(wallDesc);
            var av25 = CharacterDesc.Default; av25.Position = new Vector3(43f, 20f, 0.75f); av25.UserData = 6000u; av25.WantsContactEvents = true;
            CharacterId avatar25 = backend.CreateCharacter(av25);
            var b25 = new BodyState[16]; var c25 = new CharacterState[2]; var ct25 = new ContactReport[48];
            CollectAvatarContacts(backend, avatar25, 6000u, new Vector3(1.2f, 0f, 0f), 220, b25, c25, ct25, seen, ref avBegin, ref avPersist, ref avEnd, ref sideAok);

            // Lane B (y=25): walk into a DYNAMIC box -> dynamic (no wall in this lane).
            backend.SetCharacterTransform(avatar25, new Vector3(33f, 25f, 0.75f), System.Numerics.Quaternion.Identity);
            ShapeId dboxShape = backend.CreateBoxShape(new Vector3(0.4f, 0.4f, 0.4f));
            var dboxDesc = BodyDesc.Default; dboxDesc.Shape = dboxShape; dboxDesc.Position = new Vector3(36f, 25f, 0.4f);
            dboxDesc.MotionType = BodyMotionType.Dynamic; dboxDesc.Layer = PhysicsLayer.Dynamic; dboxDesc.Mass = 10f; dboxDesc.StartActive = true; dboxDesc.UserData = 6002u;
            BodyId dbox = backend.CreateBody(dboxDesc);
            CollectAvatarContacts(backend, avatar25, 6000u, new Vector3(1.2f, 0f, 0f), 150, b25, c25, ct25, seen, ref avBegin, ref avPersist, ref avEnd, ref sideAok);

            Console.WriteLine($"      avatar contacts Begin={avBegin} Persist={avPersist} End={avEnd}; other UserDatas seen: [{string.Join(",", seen)}]");
            Check(avBegin >= 1 && avPersist >= 1, $"avatar generates Begin + Persist (B={avBegin} P={avPersist})");
            Check(seen.Contains(0u), "saw TERRAIN contact (UserDataB=0)");
            Check(seen.Contains(6001u), "saw STATIC wall contact (UserDataB=6001)");
            Check(seen.Contains(6002u), "saw DYNAMIC box contact (UserDataB=6002)");
            Check(sideAok, "avatar side reports Invalid BodyId + avatar UserData (side A) on every report");
            backend.RemoveCharacter(avatar25); backend.RemoveBody(wall); backend.RemoveBody(dbox);
            backend.ReleaseShape(wallShape); backend.ReleaseShape(dboxShape);

            // ---- 26. THE REAL #4 GATE: stationary avatar, wants=false suppresses / wants=true floods. ----
            Console.WriteLine("\n[26] REAL #4 gate: stationary avatar wants=false suppressed vs wants=true floods");
            backend.SetTerrain(flat, Vector3.Zero);
            int gateOff = AvatarRestPersist(backend, false);
            int gateOn = AvatarRestPersist(backend, true);
            Console.WriteLine($"      Persist over 100 rest steps: wants=false -> {gateOff}    wants=true -> {gateOn}");
            Check(gateOff == 0, $"gate SUPPRESSES stationary-avatar Persist when wants=false (got {gateOff})");
            Check(gateOn > 50, $"stationary avatar FLOODS Persist when wants=true (got {gateOn}) - the gate's whole reason to exist");

            // ---- 27. SENSOR overlap: avatar walking through a sensor generates the overlap report. ----
            Console.WriteLine("\n[27] Sensor overlap (avatar walks through a VolumeDetect/Sensor body)");
            backend.SetTerrain(flat, Vector3.Zero);
            ShapeId senShape = backend.CreateBoxShape(new Vector3(0.6f, 0.6f, 1.2f));
            var senDesc = BodyDesc.Default; senDesc.Shape = senShape; senDesc.Position = new Vector3(63f, 20f, 1.0f);
            senDesc.MotionType = BodyMotionType.Static; senDesc.Layer = PhysicsLayer.Sensor; senDesc.IsSensor = true; senDesc.UserData = 6003u;
            BodyId sensor = backend.CreateBody(senDesc);
            var av27 = CharacterDesc.Default; av27.Position = new Vector3(60f, 20f, 0.75f); av27.UserData = 6100u; av27.WantsContactEvents = true;
            CharacterId avatar27 = backend.CreateCharacter(av27);
            var seen27 = new System.Collections.Generic.HashSet<uint>(); bool s27 = true; int b27c = 0, p27c = 0, e27c = 0;
            CollectAvatarContacts(backend, avatar27, 6100u, new Vector3(1.2f, 0f, 0f), 300, b25, c25, ct25, seen27, ref b27c, ref p27c, ref e27c, ref s27);
            Console.WriteLine($"      other UserDatas seen while crossing the sensor: [{string.Join(",", seen27)}]");
            Check(seen27.Contains(6003u), "avatar reported the SENSOR overlap (UserDataB=6003)");
            backend.RemoveCharacter(avatar27); backend.RemoveBody(sensor); backend.ReleaseShape(senShape);

            // ---- 28. AVATAR-AVATAR: collide (push/block) and report. ----
            Console.WriteLine("\n[28] Avatar-avatar collision (push/block) + report");
            backend.SetTerrain(flat, Vector3.Zero);
            var avA = CharacterDesc.Default; avA.Position = new Vector3(70f, 20f, 0.75f); avA.UserData = 7001u; avA.WantsContactEvents = true;
            var avB = CharacterDesc.Default; avB.Position = new Vector3(71.0f, 20f, 0.75f); avB.UserData = 7002u; avB.WantsContactEvents = true;
            CharacterId a1 = backend.CreateCharacter(avA);
            CharacterId a2 = backend.CreateCharacter(avB);
            var b28 = new BodyState[8]; var c28 = new CharacterState[4]; var ct28 = new ContactReport[48];
            int avAvContacts = 0;
            backend.TryGetCharacterState(a2, out CharacterState a2Start);
            for (int i = 0; i < 150; i++)
            {
                backend.SetCharacterMovement(a1, new Vector3(1.0f, 0f, 0f), false, false);
                backend.SetCharacterMovement(a2, System.Numerics.Vector3.Zero, false, false);
                StepResult r = backend.Step(1f / 60f, b28, c28, ct28);
                for (int k = 0; k < r.ContactCount; k++)
                {
                    ContactReport rep = ct28[k];
                    if ((rep.UserDataA == 7001u && rep.UserDataB == 7002u) || (rep.UserDataA == 7002u && rep.UserDataB == 7001u)) avAvContacts++;
                }
            }
            backend.TryGetCharacterState(a1, out CharacterState a1End);
            backend.TryGetCharacterState(a2, out CharacterState a2End);
            float a2Moved = a2End.Position.X - a2Start.Position.X;
            bool passedThrough = a1End.Position.X > a2End.Position.X + 0.1f;
            Console.WriteLine($"      a1 X->{a1End.Position.X:0.00}  a2 X {a2Start.Position.X:0.00}->{a2End.Position.X:0.00} (moved {a2Moved:0.00})  avatar-avatar reports={avAvContacts}  passedThrough={passedThrough}");
            Check(!passedThrough, "avatars do NOT pass through each other (blocked)");
            Check(a2Moved > 0.1f, $"the standing avatar was PUSHED (moved {a2Moved:0.00}) - matches AvatarToAvatarCollisionsByDefault=true");
            Console.WriteLine($"      NOTE avatar-avatar CONTACT REPORTS = {avAvContacts} (physical collision confirmed via push/block above)");
            backend.RemoveCharacter(a1); backend.RemoveCharacter(a2);

            // ================= MILESTONE 4 TASK 1 - SHAPES =================

            // ---- 29. SPHERE: cooks, rests on terrain, raycast hits real surface, mass spot-check. ----
            Console.WriteLine("\n[29] Sphere: cook, raycast top, mass = (4/3)pi r^3 * density");
            ShapeId sphere = backend.CreateSphereShape(0.5f);
            Check(sphere.IsValid, $"sphere cooked -> {sphere}");
            float sphereTop = RayTopOf(backend, sphere, new Vector3(10f, 10f, 5f), 5001u, out BodyId sphereBody);
            Check(MathF.Abs(sphereTop - 5.5f) < 0.05f, $"raycast hits sphere top ~5.5 (got {sphereTop:0.000})");
            // mass via impulse (gravityFactor 0 isolates): sphere volume 0.5236 m^3 * 1000 = 523.6 kg
            const float SphereHandMass = (float)(4.0 / 3.0 * Math.PI * 0.125) * 1000f;
            var sd = BodyDesc.Default; sd.Shape = sphere; sd.Position = new Vector3(12f, 12f, 20f);
            sd.MotionType = BodyMotionType.Dynamic; sd.Layer = PhysicsLayer.Dynamic; sd.GravityFactor = 0f; sd.StartActive = true;
            BodyId sphereDyn = backend.CreateBody(sd);
            backend.TryGetBodyState(sphereDyn, out BodyState sb0);
            backend.ApplyImpulse(sphereDyn, new Vector3(0f, 0f, SphereHandMass * 2f)); // Δv should be 2.0
            backend.TryGetBodyState(sphereDyn, out BodyState sb1);
            float sdv = sb1.LinearVelocity.Z - sb0.LinearVelocity.Z;
            float sphereInferred = sdv != 0f ? (SphereHandMass * 2f) / sdv : float.NaN;
            Console.WriteLine($"      sphere hand mass {SphereHandMass:0.0} kg; impulse -> Δv {sdv:0.000} -> inferred {sphereInferred:0.0} kg");
            Check(MathF.Abs(sphereInferred - SphereHandMass) < 1f, $"computed sphere mass matches (4/3)pi r^3*1000 = {SphereHandMass:0.0} (got {sphereInferred:0.0})");
            backend.RemoveBody(sphereBody); backend.RemoveBody(sphereDyn);

            // ---- 30. CAPSULE / CYLINDER / CONVEX HULL: cook + raycast real surface. ----
            Console.WriteLine("\n[30] Capsule / Cylinder / ConvexHull cook + raycast");
            ShapeId capsule = backend.CreateCapsuleShape(0.5f, 0.3f);   // total half-height 0.5+0.3=0.8
            ShapeId cylinder = backend.CreateCylinderShape(0.5f, 0.3f); // Y-axis cylinder, half-height 0.5
            System.Numerics.Vector3[] tetraPts = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) };
            ShapeId hull = backend.CreateConvexHullShape(tetraPts);
            Check(capsule.IsValid && cylinder.IsValid && hull.IsValid, "capsule, cylinder, hull all cooked");
            // Jolt's capsule AND cylinder axes are Y, so their Z half-extent is the radius (0.3) - both
            // rest with top at centre+0.3. Standing a capsule/cylinder prim up is the layer's orientation
            // job (like the avatar capsule we rotate Y->Z). Noted for M6.
            float capTop = RayTopOf(backend, capsule, new Vector3(14f, 14f, 5f), 5002u, out BodyId capB);
            Check(MathF.Abs(capTop - 5.3f) < 0.06f, $"capsule (Y-axis) top ~5.3 (radius 0.3) (got {capTop:0.000})");
            float cylTop = RayTopOf(backend, cylinder, new Vector3(16f, 16f, 5f), 5003u, out BodyId cylB);
            Check(MathF.Abs(cylTop - 5.3f) < 0.06f, $"cylinder (Y-axis) top ~5.3 (radius 0.3) (got {cylTop:0.000})");
            backend.RemoveBody(capB); backend.RemoveBody(cylB);

            // ---- 31. MESH: a tetrahedron - raycast hits the REAL surface, MISSES inside the bbox. ----
            Console.WriteLine("\n[31] Mesh (tetra): raycast hits real surface, misses the empty bbox corner");
            var mv = new System.Numerics.Vector3[] { new(0, 0, 0), new(2, 0, 0), new(0, 2, 0), new(0, 0, 2) };
            var mi = new int[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
            ShapeId mesh = backend.CreateMeshShape(mv, mi);
            Check(mesh.IsValid, $"mesh cooked -> {mesh}");
            var meshDesc = BodyDesc.Default; meshDesc.Shape = mesh; meshDesc.Position = new Vector3(40f, 40f, 0f);
            meshDesc.MotionType = BodyMotionType.Static; meshDesc.Layer = PhysicsLayer.Static; meshDesc.UserData = 5004u;
            BodyId meshBody = backend.CreateBody(meshDesc);
            bool inHit = backend.RayCast(new Vector3(40.2f, 40.2f, 20f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit meshIn);
            bool outHit = backend.RayCast(new Vector3(41.5f, 41.5f, 20f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit meshOut);
            bool outHitMesh = outHit && meshOut.Body.Equals(meshBody); // vs falling through to terrain below
            Console.WriteLine($"      inside footprint (40.2,40.2): {(inHit ? $"HIT z={meshIn.Point.Z:0.00}" : "miss")}   bbox-corner (41.5,41.5): {(outHitMesh ? "HIT mesh" : outHit ? $"through to terrain z={meshOut.Point.Z:0.00}" : "miss")}");
            Check(inHit && meshIn.Body.Equals(meshBody) && MathF.Abs(meshIn.Point.Z - 1.6f) < 0.1f, $"ray hits the tetra face (z~1.6, x+y+z=2 plane) (got {(inHit ? meshIn.Point.Z.ToString("0.00") : "miss")})");
            Check(!outHitMesh, "ray through the EMPTY bbox corner does NOT hit the mesh (real surface, not bounding box)");
            backend.RemoveBody(meshBody);

            // ---- 32. COMPOUND: two boxes with distinct child UserData; raycast resolves ChildUserData. ----
            Console.WriteLine("\n[32] Compound (linkset): raycast resolves the struck child's UserData");
            ShapeId childBox = backend.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));
            var kids = new CompoundChild[]
            {
                new CompoundChild { Shape = childBox, Position = new Vector3(-1f, 0f, 0f), Orientation = System.Numerics.Quaternion.Identity, UserData = 8001u },
                new CompoundChild { Shape = childBox, Position = new Vector3( 1f, 0f, 0f), Orientation = System.Numerics.Quaternion.Identity, UserData = 8002u },
            };
            ShapeId compound = backend.CreateCompoundShape(kids);
            Check(compound.IsValid, $"compound cooked -> {compound}");
            var compDesc = BodyDesc.Default; compDesc.Shape = compound; compDesc.Position = new Vector3(50f, 50f, 5f);
            compDesc.MotionType = BodyMotionType.Static; compDesc.Layer = PhysicsLayer.Static; compDesc.UserData = 8000u;
            BodyId compBody = backend.CreateBody(compDesc);
            backend.RayCast(new Vector3(49f, 50f, 20f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit hitL); // child at -1 -> world 49
            backend.RayCast(new Vector3(51f, 50f, 20f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit hitR); // child at +1 -> world 51
            Console.WriteLine($"      hit left child ChildUserData={hitL.ChildUserData} (want 8001); right ChildUserData={hitR.ChildUserData} (want 8002); body UserData={hitL.UserData}");
            Check(hitL.Body.Equals(compBody) && hitL.ChildUserData == 8001u, $"left child resolves ChildUserData 8001 (got {hitL.ChildUserData})");
            Check(hitR.Body.Equals(compBody) && hitR.ChildUserData == 8002u, $"right child resolves ChildUserData 8002 (got {hitR.ChildUserData})");
            Check(hitL.UserData == 8000u, $"compound body UserData is the linkset root 8000 (got {hitL.UserData})");
            backend.RemoveBody(compBody); backend.ReleaseShape(compound);

            // [32b] Compound as a DYNAMIC body (M7 Task 1 foundation): a linkset (root + 2 children) is ONE
            // rigid body - it falls and rests as one (children welded, not simulating apart), and its mass
            // is the SUM of the parts. This is what a physical linkset's compound body must do.
            Console.WriteLine("\n[32b] Compound DYNAMIC (linkset): falls + rests as one body; mass = sum of parts");
            ShapeId lcb = backend.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f)); // 1 m box, volume 1
            var lkids = new CompoundChild[]
            {
                new CompoundChild { Shape = lcb, Position = new Vector3(0f, 0f, 0f), Orientation = System.Numerics.Quaternion.Identity, UserData = 8100u },
                new CompoundChild { Shape = lcb, Position = new Vector3(1.5f, 0f, 0f), Orientation = System.Numerics.Quaternion.Identity, UserData = 8101u },
                new CompoundChild { Shape = lcb, Position = new Vector3(0f, 1.5f, 0f), Orientation = System.Numerics.Quaternion.Identity, UserData = 8102u },
            };
            ShapeId lcompound = backend.CreateCompoundShape(lkids);
            var lDesc = BodyDesc.Default; lDesc.Shape = lcompound; lDesc.Position = new Vector3(100f, 100f, 30f);
            lDesc.MotionType = BodyMotionType.Dynamic; lDesc.Layer = PhysicsLayer.Dynamic; lDesc.UserData = 8100u; lDesc.StartActive = true;
            BodyId lbody = backend.CreateBody(lDesc); backend.ActivateBody(lbody);
            float lmass = backend.GetBodyMass(lbody);
            float lsingle;
            { var sDesc = BodyDesc.Default; sDesc.Shape = lcb; sDesc.MotionType = BodyMotionType.Dynamic; sDesc.Layer = PhysicsLayer.Dynamic; sDesc.Position = new Vector3(120f, 120f, 30f); BodyId sb = backend.CreateBody(sDesc); lsingle = backend.GetBodyMass(sb); backend.RemoveBody(sb); }
            var lbuf = new BodyState[8]; var lchars = new CharacterState[2]; var lct = new ContactReport[16];
            for (int i = 0; i < 600; i++) backend.Step(1f / 60f, lbuf, lchars, lct);
            backend.TryGetBodyState(lbody, out BodyState lend);
            Console.WriteLine($"      compound mass={lmass:0.0} (single box {lsingle:0.0} x3 = {lsingle * 3f:0.0}); rest z={lend.Position.Z:0.00} (dropped from 30)");
            Check(lend.Position.Z < 29f && lend.Position.Z > 0f, $"compound FELL and rested as one body (z {lend.Position.Z:0.00})");
            Check(MathF.Abs(lmass - lsingle * 3f) < lsingle * 0.05f, $"compound mass = sum of 3 children ({lmass:0.0} ~ {lsingle * 3f:0.0})");
            backend.RemoveBody(lbody); backend.ReleaseShape(lcompound); backend.ReleaseShape(lcb);

            // [32c] Compound REBUILD cycles (M7 Task 2 backend foundation): create a compound, use it, then
            // release it and rebuild with a DIFFERENT child count - repeatedly. Mass must track the current
            // member count every cycle (a stale/leaked child would throw the mass off), and nothing crashes.
            // This is exactly the create-new + release-old the module's link/unlink RebuildCompound does.
            Console.WriteLine("\n[32c] Compound rebuild cycles: mass tracks member count, stable over cycles");
            ShapeId rcb = backend.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));
            float rcSingle;
            { var d = BodyDesc.Default; d.Shape = rcb; d.MotionType = BodyMotionType.Dynamic; d.Layer = PhysicsLayer.Dynamic; d.Position = new Vector3(140f, 140f, 30f); BodyId b = backend.CreateBody(d); rcSingle = backend.GetBodyMass(b); backend.RemoveBody(b); }
            bool rcOk = true; string rcSeq = "";
            for (int cycle = 0; cycle < 6; cycle++)
            {
                int childCount = 2 + (cycle % 2);   // 2,3,2,3,... (a linkset compound is always root + >=1 child = >=2 sub-shapes)
                var ck = new CompoundChild[childCount];
                for (int i = 0; i < childCount; i++) ck[i] = new CompoundChild { Shape = rcb, Position = new Vector3(i * 1.5f, 0f, 0f), Orientation = System.Numerics.Quaternion.Identity, UserData = (uint)(8200 + i) };
                ShapeId cs = backend.CreateCompoundShape(ck);
                var cd = BodyDesc.Default; cd.Shape = cs; cd.MotionType = BodyMotionType.Dynamic; cd.Layer = PhysicsLayer.Dynamic; cd.Position = new Vector3(140f, 140f, 30f);
                BodyId cbody = backend.CreateBody(cd);
                float m = backend.GetBodyMass(cbody);
                rcSeq += $" {childCount}:{m:0}";
                if (MathF.Abs(m - rcSingle * childCount) > rcSingle * 0.05f) rcOk = false;
                backend.RemoveBody(cbody); backend.ReleaseShape(cs);
            }
            Console.WriteLine($"      single box mass={rcSingle:0.0}; cycles (children:mass):{rcSeq}");
            Check(rcOk, "compound mass = single x member count on EVERY rebuild cycle (no stale/leak across create+release)");
            backend.ReleaseShape(rcb);

            // [32d] Compound placed PENETRATING the terrain (like a persisted linkset LOADED at its rest
            // position, not dropped from above) - repro of the boot-load heartbeat stall. Create a compound
            // whose sub-shapes overlap the heightfield, ACTIVE, and Step; measure per-step time. A hang /
            // pathological slowness here is the frame-0 stall that starves the region under a loaded linkset.
            Console.WriteLine("\n[32d] Compound penetrating terrain: per-step time (repro of loaded-linkset boot stall)");
            backend.SetTerrain(flat, Vector3.Zero);   // flat terrain at Z=0
            ShapeId pcb = backend.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));
            var pkids = new CompoundChild[] {
                new CompoundChild { Shape=pcb, Position=new Vector3(0,0,0), Orientation=System.Numerics.Quaternion.Identity, UserData=8300u },
                new CompoundChild { Shape=pcb, Position=new Vector3(1.5f,0,0), Orientation=System.Numerics.Quaternion.Identity, UserData=8301u },
                new CompoundChild { Shape=pcb, Position=new Vector3(0,1.5f,0), Orientation=System.Numerics.Quaternion.Identity, UserData=8302u },
            };
            ShapeId pcomp = backend.CreateCompoundShape(pkids);
            var pDesc = BodyDesc.Default; pDesc.Shape=pcomp; pDesc.Position=new Vector3(100f,100f,0.2f);  // boxes penetrate the Z=0 terrain
            pDesc.MotionType=BodyMotionType.Dynamic; pDesc.Layer=PhysicsLayer.Dynamic; pDesc.UserData=8300u; pDesc.StartActive=true;
            BodyId pbody = backend.CreateBody(pDesc); backend.ActivateBody(pbody);
            var pbuf=new BodyState[8]; var pch=new CharacterState[2]; var pct=new ContactReport[16];
            var psw=System.Diagnostics.Stopwatch.StartNew(); long maxStep=0;
            for (int i=0;i<30;i++){ long t0=psw.ElapsedMilliseconds; backend.Step(1f/60f, pbuf, pch, pct); long dt=psw.ElapsedMilliseconds-t0; if(dt>maxStep)maxStep=dt; if(i<3||dt>300) Console.WriteLine($"      step {i}: {dt}ms"); }
            Console.WriteLine($"      30 steps done, maxStep={maxStep}ms (a multi-second step = the stall)");
            Check(maxStep < 2000, $"compound penetrating terrain does NOT hang the step (maxStep {maxStep}ms)");
            backend.RemoveBody(pbody); backend.ReleaseShape(pcomp); backend.ReleaseShape(pcb);

            // [32e] Repro the module's LOADED-linkset frame-0 rebuild sequence: create 4 separate ACTIVE
            // dynamic bodies (root + 3 children) penetrating terrain (as loaded from persistence), then do
            // exactly what RebuildCompoundNow does - remove all 4, build a compound at the root position,
            // activate - then Step. A hang/crash here is the backend cause of the boot stall.
            Console.WriteLine("\n[32e] Loaded-linkset rebuild sequence (remove 4 active bodies -> compound -> step)");
            backend.SetTerrain(flat, Vector3.Zero);
            ShapeId ecb = backend.CreateBoxShape(new Vector3(0.5f,0.5f,0.5f));
            var epos = new Vector3(110f,110f,0.2f);
            var eoff = new Vector3[]{ new(0,0,0), new(1.5f,0,0), new(0,1.5f,0), new(1.5f,1.5f,0) };
            var ebodies = new BodyId[4];
            for(int i=0;i<4;i++){ var d=BodyDesc.Default; d.Shape=ecb; d.Position=epos+eoff[i]; d.MotionType=BodyMotionType.Dynamic; d.Layer=PhysicsLayer.Dynamic; d.UserData=(uint)(8400+i); d.StartActive=true; ebodies[i]=backend.CreateBody(d); backend.ActivateBody(ebodies[i]); }
            for(int i=0;i<4;i++) backend.RemoveBody(ebodies[i]);
            var ekids=new CompoundChild[4];
            for(int i=0;i<4;i++) ekids[i]=new CompoundChild{ Shape=ecb, Position=eoff[i], Orientation=System.Numerics.Quaternion.Identity, UserData=(uint)(8400+i) };
            ShapeId ecomp=backend.CreateCompoundShape(ekids);
            var erd=BodyDesc.Default; erd.Shape=ecomp; erd.Position=epos; erd.MotionType=BodyMotionType.Dynamic; erd.Layer=PhysicsLayer.Dynamic; erd.UserData=8400u; erd.StartActive=true;
            BodyId erb=backend.CreateBody(erd); backend.ActivateBody(erb);
            var eb=new BodyState[8]; var ec2=new CharacterState[2]; var ect=new ContactReport[16];
            var esw=System.Diagnostics.Stopwatch.StartNew(); long emax=0;
            for(int i=0;i<30;i++){ long t0=esw.ElapsedMilliseconds; backend.Step(1f/60f, eb, ec2, ect); long dt=esw.ElapsedMilliseconds-t0; if(dt>emax)emax=dt; }
            Console.WriteLine($"      30 steps after rebuild sequence, maxStep={emax}ms");
            Check(emax<2000, $"loaded-linkset rebuild sequence does not hang (maxStep {emax}ms)");
            backend.RemoveBody(erb); backend.ReleaseShape(ecomp); backend.ReleaseShape(ecb);

            // [32f] MODULE-ACCURATE repro: CollisionSteps=6 (the module setting; harness default is 1) +
            // mutually-OVERLAPPING loaded linkset parts. Before the deferred weld, the linkset's 4 parts are
            // INDIVIDUAL dynamic bodies ~0.6 m apart = overlapping ~0.4 m, penetrating terrain, stepped at
            // CollisionSteps=6. This is the combination [32d]/[32e] lacked. A hang here IS the boot stall
            // (heartbeat ThreadState=Running = a compute loop in the solver).
            Console.WriteLine("\n[32f] CollisionSteps=6 + overlapping penetrating linkset bodies (module-accurate repro)");
            var s6 = PhysicsBackendSettings.Default; s6.CollisionSteps = 6;
            var bk6 = new JoltPhysicsBackend(); bk6.Initialize(s6);
            ShapeId f6 = bk6.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S,S,S)); bk6.SetTerrain(f6, Vector3.Zero);
            ShapeId b6 = bk6.CreateBoxShape(new Vector3(0.5f,0.5f,0.5f));
            var off6 = new Vector3[]{ new(0,0,0), new(0.6f,0,0), new(0,0.6f,0), new(0.6f,0.6f,0) };
            var bd6 = new BodyId[4];
            for(int i=0;i<4;i++){ var d=BodyDesc.Default; d.Shape=b6; d.Position=new Vector3(100f,100f,0.3f)+off6[i]; d.MotionType=BodyMotionType.Dynamic; d.Layer=PhysicsLayer.Dynamic; d.UserData=(uint)(8500+i); d.StartActive=true; bd6[i]=bk6.CreateBody(d); bk6.ActivateBody(bd6[i]); }
            var b6buf=new BodyState[8]; var b6ch=new CharacterState[2]; var b6ct=new ContactReport[16];
            var sw6=System.Diagnostics.Stopwatch.StartNew(); long m6=0;
            for(int i=0;i<30;i++){ long t0=sw6.ElapsedMilliseconds; bk6.Step(1f/60f, b6buf, b6ch, b6ct); long dt=sw6.ElapsedMilliseconds-t0; if(dt>m6)m6=dt; if(dt>200)Console.WriteLine($"      step {i}: {dt}ms"); }
            Console.WriteLine($"      30 steps, maxStep={m6}ms (multi-second/hang = the boot stall)");
            Check(m6<2000, $"CollisionSteps=6 + overlapping linkset bodies does not hang (maxStep {m6}ms)");

            // [32g] LIVE-TERRAIN repro: compound at the PEAK of a MOUND heightfield (like the pinhead-island,
            // centre=25 sloping to 0) + CollisionSteps=6. The live weld-at-load boot hangs in backendStep on
            // the welded compound; earlier repros used FLAT terrain. If this hangs, it is the compound-vs-
            // sloped-heightfield step at CollisionSteps=6.
            Console.WriteLine("\n[32g] Compound on a MOUND heightfield peak + CollisionSteps=6 (live-terrain repro)");
            var sg = PhysicsBackendSettings.Default; sg.CollisionSteps = 6;
            var bkg = new JoltPhysicsBackend(); bkg.Initialize(sg);
            ShapeId fg = bkg.CreateHeightFieldShape(MoundField(), N, N, new Vector3(S,S,S)); bkg.SetTerrain(fg, Vector3.Zero);
            ShapeId cbg = bkg.CreateBoxShape(new Vector3(0.5f,0.5f,0.5f));
            var kg = new CompoundChild[]{
                new CompoundChild{Shape=cbg,Position=new Vector3(0,0,0),Orientation=System.Numerics.Quaternion.Identity,UserData=8600u},
                new CompoundChild{Shape=cbg,Position=new Vector3(0.6f,0,0),Orientation=System.Numerics.Quaternion.Identity,UserData=8601u},
                new CompoundChild{Shape=cbg,Position=new Vector3(0,0.6f,0),Orientation=System.Numerics.Quaternion.Identity,UserData=8602u},
                new CompoundChild{Shape=cbg,Position=new Vector3(0.6f,0.6f,0),Orientation=System.Numerics.Quaternion.Identity,UserData=8603u},
            };
            ShapeId compg = bkg.CreateCompoundShape(kg);
            var dg = BodyDesc.Default; dg.Shape=compg; dg.Position=new Vector3(128f,128f,25.3f); dg.MotionType=BodyMotionType.Dynamic; dg.Layer=PhysicsLayer.Dynamic; dg.UserData=8600u; dg.StartActive=true;
            BodyId bg = bkg.CreateBody(dg); bkg.ActivateBody(bg);
            var gbuf=new BodyState[8]; var gch=new CharacterState[2]; var gct=new ContactReport[16];
            var swg=System.Diagnostics.Stopwatch.StartNew(); long mg=0;
            for(int i=0;i<30;i++){ long t0=swg.ElapsedMilliseconds; bkg.Step(1f/60f, gbuf, gch, gct); long dt=swg.ElapsedMilliseconds-t0; if(dt>mg)mg=dt; if(dt>200)Console.WriteLine($"      step {i}: {dt}ms"); }
            Console.WriteLine($"      30 steps, maxStep={mg}ms");
            Check(mg<2000, $"compound on mound terrain does not hang (maxStep {mg}ms)");

            // [32h] EXACT LIVE GEOMETRY repro (from the boot's [linkrebuild]): root + 3 children at the live
            // offsets, welded compound loaded PENETRATING a flat-25 heightfield at rootPos Z=25.32,
            // CollisionSteps=6. The live native-Jolt Step spins here (all contact counters 0). Try box halves
            // 0.25 and 0.5 (unknown live prim size) - a hang at either = reproduced.
            foreach (float half in new[] { 0.25f, 0.5f })
            {
                Console.WriteLine($"\n[32h] EXACT-live compound (box half={half}) penetrating flat-25 terrain + CollisionSteps=6");
                var setH = PhysicsBackendSettings.Default; setH.CollisionSteps = 6;
                var bkh = new JoltPhysicsBackend(); bkh.Initialize(setH);
                var flat25 = new float[N * N]; for (int i = 0; i < flat25.Length; i++) flat25[i] = 25f;
                ShapeId fldH = bkh.CreateHeightFieldShape(flat25, N, N, new Vector3(S, S, S)); bkh.SetTerrain(fldH, Vector3.Zero);
                ShapeId cbh = bkh.CreateBoxShape(new Vector3(half, half, half));
                var offs = new[] { new Vector3(0,0,0), new Vector3(0.05f,1.47f,-0.24f), new Vector3(0.02f,1.49f,0.74f), new Vector3(0.03f,0.73f,0.40f) };
                // LIVE: root prim is TILTED ~26deg; children counter-rotated (invRoot) so they are world-upright.
                var rootQ = new System.Numerics.Quaternion(0.2284f, -0.0181f, -0.0111f, 0.9733f);
                var invQ = System.Numerics.Quaternion.Conjugate(rootQ);
                var kh = new CompoundChild[4];
                for (int i = 0; i < 4; i++) kh[i] = new CompoundChild { Shape = cbh, Position = offs[i], Orientation = i == 0 ? System.Numerics.Quaternion.Identity : invQ, UserData = (uint)(8700 + i) };
                ShapeId comph = bkh.CreateCompoundShape(kh);
                var descH = BodyDesc.Default; descH.Shape = comph; descH.Density = 10f; descH.Orientation = rootQ; descH.Position = new Vector3(126.86f, 128.26f, 25.32f); descH.MotionType = BodyMotionType.Dynamic; descH.Layer = PhysicsLayer.Dynamic; descH.UserData = 8700u; descH.StartActive = true;
                BodyId bodyH = bkh.CreateBody(descH); bkh.ActivateBody(bodyH);
                var hbuf = new BodyState[8]; var hch = new CharacterState[2]; var hct = new ContactReport[16];
                var swh = System.Diagnostics.Stopwatch.StartNew(); long mh = 0;
                for (int i = 0; i < 30; i++) { long t0 = swh.ElapsedMilliseconds; bkh.Step(0.0909f, hbuf, hch, hct); long dt = swh.ElapsedMilliseconds - t0; if (dt > mh) mh = dt; if (dt > 200) Console.WriteLine($"      step {i}: {dt}ms"); }
                Console.WriteLine($"      30 steps, maxStep={mh}ms");
                Check(mh < 2000, $"EXACT-live compound (half={half}) does not hang (maxStep {mh}ms)");
            }

            // ---- [32i] EXACT LIVE BOOT SEQUENCE: 4 active overlapping bodies -> remove -> weld -> step ----
            // The live boot creates root+3 children as individual ACTIVE, deeply-overlapping bodies at load,
            // then at frame 0 removes them (children first, root last) and welds the compound, THEN steps.
            // [32h] only ever created the compound fresh; it never exercised the create-4-active-then-remove
            // churn that precedes the weld. Removing active overlapping bodies can leave Jolt's broadphase /
            // island state inconsistent so the next Update spins. A native hang here reproduces the boot stall.
            Console.WriteLine("\n[32i] LIVE boot sequence: 4 active overlapping bodies -> remove -> weld -> step (CollisionSteps=6)");
            {
                const int Ni = 256; const float Si = 256f;
                var seti = PhysicsBackendSettings.Default; seti.CollisionSteps = 6;
                var bki = new JoltPhysicsBackend(); bki.Initialize(seti);
                var flatI = new float[Ni * Ni]; for (int i = 0; i < flatI.Length; i++) flatI[i] = 25f;
                ShapeId fldI = bki.CreateHeightFieldShape(flatI, Ni, Ni, new Vector3(Si, Si, Si)); bki.SetTerrain(fldI, Vector3.Zero);
                ShapeId boxI = bki.CreateBoxShape(new Vector3(0.25f, 0.25f, 0.25f));
                var rootQi = new System.Numerics.Quaternion(0.2284f, -0.0181f, -0.0111f, 0.9733f);
                var invQi = System.Numerics.Quaternion.Conjugate(rootQi);
                var rootW = new Vector3(126.86f, 128.26f, 25.32f);
                var offi = new[] { new Vector3(0,0,0), new Vector3(0.05f,1.47f,-0.24f), new Vector3(0.02f,1.49f,0.74f), new Vector3(0.03f,0.73f,0.40f) };
                // (1) create 4 ACTIVE individual bodies at world positions: root tilted, children world-upright, overlapping.
                var idsI = new BodyId[4];
                for (int i = 0; i < 4; i++)
                {
                    var d = BodyDesc.Default; d.Shape = boxI; d.Density = 10f;
                    d.Position = rootW + Vector3.Transform(offi[i], rootQi);
                    d.Orientation = i == 0 ? rootQi : System.Numerics.Quaternion.Identity;
                    d.MotionType = BodyMotionType.Dynamic; d.Layer = PhysicsLayer.Dynamic;
                    d.UserData = (uint)(9100 + i); d.StartActive = true;
                    idsI[i] = bki.CreateBody(d); bki.ActivateBody(idsI[i]);
                }
                // (2) weld order: remove the 3 children first...
                for (int i = 1; i < 4; i++) bki.RemoveBody(idsI[i]);
                // (3) build the compound (root@identity, children counter-rotated at root-frame offsets)...
                var kidsI = new CompoundChild[4];
                for (int i = 0; i < 4; i++) kidsI[i] = new CompoundChild { Shape = boxI, Position = offi[i], Orientation = i == 0 ? System.Numerics.Quaternion.Identity : invQi, UserData = (uint)(9100 + i) };
                ShapeId compI = bki.CreateCompoundShape(kidsI);
                // (4) ...then remove the root body LAST and create the compound body (as RebuildCompoundNow does).
                bki.RemoveBody(idsI[0]);
                var cd = BodyDesc.Default; cd.Shape = compI; cd.Density = 10f; cd.Orientation = rootQi; cd.Position = rootW;
                cd.MotionType = BodyMotionType.Dynamic; cd.Layer = PhysicsLayer.Dynamic; cd.UserData = 9100u; cd.StartActive = true;
                BodyId cbI = bki.CreateBody(cd); bki.ActivateBody(cbI);
                // (5) step at the live rate.
                var bbI = new BodyState[8]; var chI = new CharacterState[2]; var ctI = new ContactReport[16];
                var swi = System.Diagnostics.Stopwatch.StartNew(); long maxi = 0;
                for (int i = 0; i < 30; i++) { long t0 = swi.ElapsedMilliseconds; bki.Step(0.0909f, bbI, chI, ctI); long dt = swi.ElapsedMilliseconds - t0; if (dt > maxi) maxi = dt; }
                Console.WriteLine($"      30 steps, maxStep={maxi}ms");
                Check(maxi < 2000, $"LIVE boot sequence does not hang (maxStep {maxi}ms)");
            }

            // ---- [32j] REAL heightfield at CORRECT scale (module builds 257x257 @ 1m spacing) + exact compound ----
            // EVERY prior terrain repro passed scale (256,256,256) => 256m sample spacing, so the compound sat on
            // ONE giant flat quad (geometrically wrong - that is why nothing hung). The module builds an (N+1)=257
            // square field at scale (1,1,1) = 1m spacing: the REAL pinhead-island dome under the compound. This
            // exact terrain input (correct scale + real DB heights) was never tested. Load realterrain.f32.
            Console.WriteLine("\n[32j] REAL heightfield 257x257 @ 1m spacing + exact compound at (126.86,128.26,25.32)");
            {
                var terPath = @"D:\jolt-boot-test\bin\realterrain.f32";
                if (!System.IO.File.Exists(terPath)) { Console.WriteLine("      (realterrain.f32 missing; skipping [32j])"); }
                else
                {
                    var terBytes = System.IO.File.ReadAllBytes(terPath);
                    int src = 256; var hj = new float[src * src];
                    for (int k = 0; k < hj.Length; k++) hj[k] = BitConverter.ToSingle(terBytes, k * 4);
                    int mj = src + 1;                                   // module: N+1 = 257
                    var fieldj = new float[mj * mj];
                    for (int y = 0; y < mj; y++) { int sr = Math.Min(y, src - 1) * src; int dr = y * mj; for (int x = 0; x < mj; x++) fieldj[dr + x] = hj[sr + Math.Min(x, src - 1)]; }
                    var setj = PhysicsBackendSettings.Default; setj.CollisionSteps = 6;
                    var bkj = new JoltPhysicsBackend(); bkj.Initialize(setj);
                    ShapeId fldj = bkj.CreateHeightFieldShape(fieldj, mj, mj, new Vector3(1f, 1f, 1f)); bkj.SetTerrain(fldj, Vector3.Zero);
                    ShapeId boxj = bkj.CreateBoxShape(new Vector3(0.25f, 0.25f, 0.25f));
                    var rootQj = new System.Numerics.Quaternion(0.2284f, -0.0181f, -0.0111f, 0.9733f);
                    var invQj = System.Numerics.Quaternion.Conjugate(rootQj);
                    var offj = new[] { new Vector3(0,0,0), new Vector3(0.05f,1.47f,-0.24f), new Vector3(0.02f,1.49f,0.74f), new Vector3(0.03f,0.73f,0.40f) };
                    var kidsj = new CompoundChild[4];
                    for (int i = 0; i < 4; i++) kidsj[i] = new CompoundChild { Shape = boxj, Position = offj[i], Orientation = i == 0 ? System.Numerics.Quaternion.Identity : invQj, UserData = (uint)(9200 + i) };
                    ShapeId compj = bkj.CreateCompoundShape(kidsj);
                    var cdj = BodyDesc.Default; cdj.Shape = compj; cdj.Density = 10f; cdj.Orientation = rootQj; cdj.Position = new Vector3(126.86f, 128.26f, 25.32f);
                    cdj.LinearVelocity = new Vector3(0.01708f, 0.40312f, -0.13121f);   // persisted root velocity (DB)
                    cdj.AngularVelocity = new Vector3(-1.20333f, 0.05029f, -0.03555f); // persisted root angular velocity (DB)
                    cdj.MotionType = BodyMotionType.Dynamic; cdj.Layer = PhysicsLayer.Dynamic; cdj.UserData = 9200u; cdj.StartActive = true;
                    BodyId cbj = bkj.CreateBody(cdj); bkj.ActivateBody(cbj);
                    var bbj = new BodyState[8]; var chj = new CharacterState[2]; var ctj = new ContactReport[16];
                    var swj = System.Diagnostics.Stopwatch.StartNew(); long maxj = 0;
                    for (int i = 0; i < 30; i++) { long t0 = swj.ElapsedMilliseconds; bkj.Step(0.0909f, bbj, chj, ctj); long dt = swj.ElapsedMilliseconds - t0; if (dt > maxj) maxj = dt; }
                    Console.WriteLine($"      30 steps, maxStep={maxj}ms");
                    Check(maxj < 2000, $"REAL-terrain compound does not hang (maxStep {maxj}ms)");
                }
            }

            // ---- [32k] REAL terrain (257@1m) + the CREATE-4-ACTIVE-THEN-REMOVE churn + weld + persisted vel ----
            // The one combination never tested: [32i] had the churn on a wrong-scale flat quad; [32j] had the
            // real dome but a fresh compound (no churn). Creating 4 active bodies OVERLAPPING the REAL heightfield
            // then removing them can leave Jolt's broadphase quadtree with dangling nodes so the next Update spins
            // with 0 contacts - exactly the live boot signature. This is the faithful live boot on the real dome.
            Console.WriteLine("\n[32k] REAL terrain + create-4-active-overlapping -> remove -> weld -> step (live boot faithful)");
            {
                var terPath = @"D:\jolt-boot-test\bin\realterrain.f32";
                if (!System.IO.File.Exists(terPath)) { Console.WriteLine("      (realterrain.f32 missing; skipping [32k])"); }
                else
                {
                    var terBytes = System.IO.File.ReadAllBytes(terPath);
                    int src = 256; var hk = new float[src * src];
                    for (int q = 0; q < hk.Length; q++) hk[q] = BitConverter.ToSingle(terBytes, q * 4);
                    int mk = src + 1;
                    var fieldk = new float[mk * mk];
                    for (int y = 0; y < mk; y++) { int sr = Math.Min(y, src - 1) * src; int dr = y * mk; for (int x = 0; x < mk; x++) fieldk[dr + x] = hk[sr + Math.Min(x, src - 1)]; }
                    var setk = PhysicsBackendSettings.Default; setk.CollisionSteps = 6;
                    var bkk = new JoltPhysicsBackend(); bkk.Initialize(setk);
                    ShapeId fldk = bkk.CreateHeightFieldShape(fieldk, mk, mk, new Vector3(1f, 1f, 1f)); bkk.SetTerrain(fldk, Vector3.Zero);
                    if (bkk.RayCast(new Vector3(126.86f, 128.26f, 5000f), new Vector3(0, 0, -1f), 10000f, QueryFilter.All, out RayHit tprobe))
                        Console.WriteLine($"      terrain under compound XY: z={tprobe.Point.Z:0.###} (compound root at 25.32; penetration≈{25.32f - tprobe.Point.Z:0.###} m)");
                    else Console.WriteLine("      terrain probe MISSED under compound XY (compound not over terrain!)");
                    ShapeId boxk = bkk.CreateBoxShape(new Vector3(0.25f, 0.25f, 0.25f));
                    var rootQk = new System.Numerics.Quaternion(0.2284f, -0.0181f, -0.0111f, 0.9733f);
                    var invQk = System.Numerics.Quaternion.Conjugate(rootQk);
                    var rootWk = new Vector3(126.86f, 128.26f, 25.32f);
                    var offk = new[] { new Vector3(0,0,0), new Vector3(0.05f,1.47f,-0.24f), new Vector3(0.02f,1.49f,0.74f), new Vector3(0.03f,0.73f,0.40f) };
                    // (1) 4 ACTIVE individual bodies overlapping the REAL heightfield.
                    var idk = new BodyId[4];
                    for (int i = 0; i < 4; i++)
                    {
                        var d = BodyDesc.Default; d.Shape = boxk; d.Density = 10f;
                        d.Position = rootWk + Vector3.Transform(offk[i], rootQk);
                        d.Orientation = i == 0 ? rootQk : System.Numerics.Quaternion.Identity;
                        d.MotionType = BodyMotionType.Dynamic; d.Layer = PhysicsLayer.Dynamic;
                        d.UserData = (uint)(9300 + i); d.StartActive = true;
                        idk[i] = bkk.CreateBody(d); bkk.ActivateBody(idk[i]);
                    }
                    // (2) remove children, (3) build compound, (4) remove root, create compound body.
                    for (int i = 1; i < 4; i++) bkk.RemoveBody(idk[i]);
                    var kidsk = new CompoundChild[4];
                    for (int i = 0; i < 4; i++) kidsk[i] = new CompoundChild { Shape = boxk, Position = offk[i], Orientation = i == 0 ? System.Numerics.Quaternion.Identity : invQk, UserData = (uint)(9300 + i) };
                    ShapeId compk = bkk.CreateCompoundShape(kidsk);
                    bkk.RemoveBody(idk[0]);
                    var cdk = BodyDesc.Default; cdk.Shape = compk; cdk.Density = 10f; cdk.Orientation = rootQk; cdk.Position = rootWk;
                    cdk.LinearVelocity = new Vector3(0.01708f, 0.40312f, -0.13121f);
                    cdk.AngularVelocity = new Vector3(-1.20333f, 0.05029f, -0.03555f);
                    cdk.MotionType = BodyMotionType.Dynamic; cdk.Layer = PhysicsLayer.Dynamic; cdk.UserData = 9300u; cdk.StartActive = true;
                    BodyId cbk = bkk.CreateBody(cdk); bkk.ActivateBody(cbk);
                    // (5) step at the live rate.
                    var bbk = new BodyState[8]; var chk = new CharacterState[2]; var ctk = new ContactReport[16];
                    var swk = System.Diagnostics.Stopwatch.StartNew(); long maxk = 0; int lastActive = -1;
                    for (int i = 0; i < 30; i++) { long t0 = swk.ElapsedMilliseconds; var rk = bkk.Step(0.0909f, bbk, chk, ctk); long dt = swk.ElapsedMilliseconds - t0; if (dt > maxk) maxk = dt; lastActive = rk.ActiveBodyCount; }
                    bkk.TryGetBodyState(cbk, out BodyState fin);
                    Console.WriteLine($"      30 steps, maxStep={maxk}ms  | VALIDITY: lastActiveBodyCount={lastActive} compound finalPos=({fin.Position.X:0.##},{fin.Position.Y:0.##},{fin.Position.Z:0.##}) flags={fin.Flags} (started Z=25.32)");
                    Check(maxk < 2000, $"REAL-terrain + churn does not hang (maxStep {maxk}ms)");
                }
            }

            // ---- 33. SCALED SHAPE: box takes non-uniform scale; sphere non-uniform CLAMPS to uniform. ----
            Console.WriteLine("\n[33] CreateScaledShape: box non-uniform applied; sphere non-uniform clamped; SetBodyShape swap");
            ShapeId baseBox = backend.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));
            ShapeId scaledBox = backend.CreateScaledShape(baseBox, new Vector3(2f, 1f, 3f)); // half -> (1.0, 0.5, 1.5)
            var scDesc = BodyDesc.Default; scDesc.Shape = scaledBox; scDesc.Position = new Vector3(56f, 56f, 10f);
            scDesc.MotionType = BodyMotionType.Static; scDesc.Layer = PhysicsLayer.Static; scDesc.UserData = 5005u;
            BodyId scBody = backend.CreateBody(scDesc);
            backend.RayCast(new Vector3(56f, 56f, 30f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit scTop);
            bool scIn = backend.RayCast(new Vector3(56.9f, 56f, 30f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit scInH);
            bool scOut = backend.RayCast(new Vector3(57.1f, 56f, 30f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit scOutH);
            bool scInBox = scIn && scInH.Body.Equals(scBody);
            bool scOutBox = scOut && scOutH.Body.Equals(scBody); // false = fell through to terrain (X beyond 1.0)
            Console.WriteLine($"      scaled box top z={scTop.Point.Z:0.00} (want 11.5); x=56.9 {(scInBox ? "hits box" : "misses box")}, x=57.1 {(scOutBox ? "hits box" : "misses box")}");
            Check(MathF.Abs(scTop.Point.Z - 11.5f) < 0.06f, $"box scaled non-uniformly in Z (top 11.5) (got {scTop.Point.Z:0.00})");
            Check(scInBox && !scOutBox, "box X half-extent doubled to 1.0 (56.9 hits box, 57.1 does not = non-uniform X applied)");

            ShapeId baseSphere = backend.CreateSphereShape(0.5f);
            ShapeId scaledSphere = backend.CreateScaledShape(baseSphere, new Vector3(2f, 1f, 1f)); // invalid -> clamps uniform ~1.333
            var ssDesc = BodyDesc.Default; ssDesc.Shape = scaledSphere; ssDesc.Position = new Vector3(60f, 60f, 10f);
            ssDesc.MotionType = BodyMotionType.Static; ssDesc.Layer = PhysicsLayer.Static; ssDesc.UserData = 5006u;
            BodyId ssBody = backend.CreateBody(ssDesc);
            backend.RayCast(new Vector3(60f, 60f, 30f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit ssTop);
            bool ss09 = backend.RayCast(new Vector3(60.9f, 60f, 30f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit ss09H);
            bool ss09Sphere = ss09 && ss09H.Body.Equals(ssBody); // would hit if X had stretched to half 1.0
            Console.WriteLine($"      scaled-sphere top z={ssTop.Point.Z:0.00} (uniform clamp -> ~0.667 radius -> 10.667); x=60.9 {(ss09Sphere ? "hits sphere" : "misses sphere")}");
            Check(MathF.Abs(ssTop.Point.Z - 10.667f) < 0.06f, $"sphere non-uniform scale CLAMPED to uniform (top ~10.667, radius 0.667) (got {ssTop.Point.Z:0.00})");
            Check(!ss09Sphere, "sphere did NOT stretch to X half 1.0 (non-uniform clamped to uniform, not distorted)");

            // SetBodyShape: swap the scaled box body to the plain sphere and confirm the new surface.
            backend.SetBodyShape(scBody, sphere, recomputeMass: false);
            backend.RayCast(new Vector3(56f, 56f, 30f), new Vector3(0, 0, -1), 40f, QueryFilter.All, out RayHit swapTop);
            Console.WriteLine($"      after SetBodyShape(box->sphere r0.5): top z={swapTop.Point.Z:0.00} (want 10.5)");
            Check(MathF.Abs(swapTop.Point.Z - 10.5f) < 0.06f, $"SetBodyShape swapped the shape (top now 10.5) (got {swapTop.Point.Z:0.00})");

            backend.RemoveBody(scBody); backend.RemoveBody(ssBody);
            backend.ReleaseShape(scaledBox); backend.ReleaseShape(scaledSphere); backend.ReleaseShape(baseBox); backend.ReleaseShape(baseSphere);
            backend.ReleaseShape(capsule); backend.ReleaseShape(cylinder); backend.ReleaseShape(hull); backend.ReleaseShape(mesh); backend.ReleaseShape(childBox);

            // ================= MILESTONE 4 TASK 2 - QUERIES =================
            backend.SetTerrain(flat, Vector3.Zero);
            ShapeId qbox = backend.CreateBoxShape(new Vector3(0.5f, 0.5f, 0.5f));

            // ---- 34. RAYCASTALL: all hits, distance order, no dupes. ----
            Console.WriteLine("\n[34] RayCastAll: all hits in distance order, no dupes");
            BodyId qb1 = MakeStaticAt(backend, qbox, new Vector3(70f, 70f, 10f), 7101u, PhysicsLayer.Static, BodyMotionType.Static);
            BodyId qb2 = MakeStaticAt(backend, qbox, new Vector3(70f, 70f, 6f), 7102u, PhysicsLayer.Static, BodyMotionType.Static);
            BodyId qb3 = MakeStaticAt(backend, qbox, new Vector3(70f, 70f, 2f), 7103u, PhysicsLayer.Static, BodyMotionType.Static);
            var rhits = new RayHit[8];
            int nAll = backend.RayCastAll(new Vector3(70f, 70f, 20f), new Vector3(0, 0, -1), 19f, QueryFilter.All, rhits); // 19 m stops above terrain
            string order = ""; for (int i = 0; i < nAll; i++) order += $"[d{rhits[i].Distance:0.0} ud{rhits[i].UserData}]";
            Console.WriteLine($"      hits={nAll}: {order}");
            Check(nAll == 3, $"RayCastAll returns all 3 boxes, terrain excluded by range (got {nAll})");
            Check(nAll == 3 && rhits[0].Distance < rhits[1].Distance && rhits[1].Distance < rhits[2].Distance, "hits sorted by distance");
            Check(nAll == 3 && rhits[0].UserData == 7101u && rhits[1].UserData == 7102u && rhits[2].UserData == 7103u, "top->bottom order, no dupes (7101,7102,7103)");
            backend.RemoveBody(qb1); backend.RemoveBody(qb2); backend.RemoveBody(qb3);

            // [34b] Coincident terrain-hit collapse (M6.8 edge 2, matches BulletSim's single terrain hit).
            // A vertical ray at a heightfield QUAD CENTRE (k+0.5, k+0.5) lands exactly on the shared triangle
            // diagonal, so Jolt's RayCastAll reports two hits at the SAME point on the SAME (terrain) body.
            // The dedupe must collapse them to ONE - without touching the 3-box multi-hit above.
            // [34b] Coincident terrain-hit collapse (M6.8 edge 2, matches BulletSim's single terrain hit).
            // On a SLOPED heightfield the two triangles of adjacent quads are non-coplanar, so a vertical ray
            // crossing a shared grid edge (integer X here) reports TWO hits at the same point on the same
            // (terrain) body. The dedupe collapses them to ONE. (Verified: with the collapse disabled this
            // exact ray returns 2.) A flat field is coplanar and never doubles - the slope is required.
            float[] slopeF = new float[N * N];
            for (int yy = 0; yy < N; yy++) for (int xx = 0; xx < N; xx++) slopeF[yy * N + xx] = xx * 0.2f;
            ShapeId slopeShape = backend.CreateHeightFieldShape(slopeF, N, N, new Vector3(S, S, S));
            backend.SetTerrain(slopeShape, Vector3.Zero);
            var tHits = new RayHit[8];
            int nT = backend.RayCastAll(new Vector3(90f, 90.5f, 80f), new Vector3(0, 0, -1), 100f, QueryFilter.All, tHits);
            Console.WriteLine($"      grid-edge terrain hits={nT}{(nT > 0 ? $" ud{tHits[0].UserData} z{tHits[0].Point.Z:0.0}" : "")}");
            Check(nT == 1, $"coincident terrain double-triangle collapsed to 1 (got {nT})");
            Check(nT >= 1 && tHits[0].UserData == 0u, "surviving terrain hit is the terrain (UserData 0)");
            backend.SetTerrain(flat, Vector3.Zero);   // restore flat terrain for the following tests

            // ---- 35. QUERYFILTER RESTRICTION per layer (the exclusion tests). ----
            Console.WriteLine("\n[35] QueryFilter restriction: exclude by layer");
            BodyId sBox = MakeStaticAt(backend, qbox, new Vector3(74f, 74f, 10f), 7201u, PhysicsLayer.Static, BodyMotionType.Static);
            BodyId dBox = MakeStaticAt(backend, qbox, new Vector3(74f, 74f, 6f), 7202u, PhysicsLayer.Dynamic, BodyMotionType.Dynamic);
            bool hS = backend.RayCast(new Vector3(74f, 74f, 20f), new Vector3(0, 0, -1), 25f, QueryFilter.Static, out RayHit rS);
            bool hD = backend.RayCast(new Vector3(74f, 74f, 20f), new Vector3(0, 0, -1), 25f, QueryFilter.Dynamic, out RayHit rD);
            bool hAv = backend.RayCast(new Vector3(74f, 74f, 20f), new Vector3(0, 0, -1), 25f, QueryFilter.Avatar, out _);
            Check(hS && rS.UserData == 7201u, $"filter=Static hits the static box, EXCLUDES dynamic (ud {(hS ? rS.UserData : 0)})");
            Check(hD && rD.UserData == 7202u, $"filter=Dynamic hits the dynamic box, EXCLUDES static (ud {(hD ? rD.UserData : 0)})");
            Check(!hAv, "filter=Avatar EXCLUDES both boxes (no avatar bodies present)");
            bool tT = backend.RayCast(new Vector3(78f, 78f, 20f), new Vector3(0, 0, -1), 25f, QueryFilter.Terrain, out _);
            bool tD = backend.RayCast(new Vector3(78f, 78f, 20f), new Vector3(0, 0, -1), 25f, QueryFilter.Dynamic, out _);
            Check(tT, "filter=Terrain hits the terrain");
            Check(!tD, "filter=Dynamic EXCLUDES the terrain (empty ground -> miss)");
            backend.RemoveBody(sBox); backend.RemoveBody(dBox);

            // ---- 36. OVERLAP sphere/box + filter. ----
            Console.WriteLine("\n[36] OverlapSphere / OverlapBox + filter");
            BodyId oS = MakeStaticAt(backend, qbox, new Vector3(82f, 82f, 5f), 7301u, PhysicsLayer.Static, BodyMotionType.Static);
            BodyId oD = MakeStaticAt(backend, qbox, new Vector3(82.6f, 82f, 5f), 7302u, PhysicsLayer.Dynamic, BodyMotionType.Dynamic);
            var obuf = new BodyId[8];
            int nSph = backend.OverlapSphere(new Vector3(82.3f, 82f, 5f), 1.0f, QueryFilter.All, obuf);
            Check(nSph >= 2, $"OverlapSphere finds both nearby boxes (got {nSph})");
            int nSphS = backend.OverlapSphere(new Vector3(82.3f, 82f, 5f), 1.0f, QueryFilter.Static, obuf);
            Check(nSphS == 1 && obuf[0].Equals(oS), $"OverlapSphere filter=Static returns ONLY the static box (got {nSphS})");
            int nBox = backend.OverlapBox(new Vector3(82.3f, 82f, 5f), new Vector3(1.0f, 1.0f, 1.0f), System.Numerics.Quaternion.Identity, QueryFilter.All, obuf);
            Check(nBox >= 2, $"OverlapBox finds both boxes (got {nBox})");
            backend.RemoveBody(oS); backend.RemoveBody(oD);

            // ---- 37. SHAPECAST against a known obstacle: first-contact point + normal. ----
            Console.WriteLine("\n[37] ShapeCast against a known obstacle");
            BodyId scT = MakeStaticAt(backend, qbox, new Vector3(86f, 86f, 3f), 7401u, PhysicsLayer.Static, BodyMotionType.Static);
            ShapeId castSphere = backend.CreateSphereShape(0.3f);
            bool scHit = backend.ShapeCast(castSphere, new Vector3(86f, 86f, 20f), System.Numerics.Quaternion.Identity, new Vector3(0, 0, -1), 25f, QueryFilter.All, out RayHit scH);
            Console.WriteLine($"      shapecast hit={scHit} ud={(scHit ? scH.UserData : 0)} point={scH.Point} normal={scH.Normal} dist={scH.Distance:0.00}");
            Check(scHit && scH.UserData == 7401u, "shapecast hits the target box");
            Check(scHit && MathF.Abs(scH.Point.Z - 3.5f) < 0.2f, $"first-contact point on box top ~3.5 (got {scH.Point.Z:0.00})");
            Check(scHit && scH.Normal.Z > 0.8f, $"contact normal points up +Z (n.z={scH.Normal.Z:0.00})");
            backend.RemoveBody(scT); backend.ReleaseShape(castSphere);

            // ---- 38. AVATAR IS QUERY-VISIBLE via the marker body (#35 resolved). ----
            Console.WriteLine("\n[38] Avatar query-visibility via marker body [#35]");
            var avq = CharacterDesc.Default; avq.Position = new Vector3(90f, 90f, 0.75f); avq.UserData = 7500u;
            CharacterId avatarQ = backend.CreateCharacter(avq);
            for (int i = 0; i < 10; i++) { backend.SetCharacterMovement(avatarQ, Vector3.Zero, false, false); backend.Step(1f / 60f, abuf, cbuf, acont); }

            // RayCast filter=Avatar now HITS the avatar; filter=Static EXCLUDES it.
            bool avRay = backend.RayCast(new Vector3(90f, 90f, 20f), new Vector3(0, 0, -1), 25f, QueryFilter.Avatar, out RayHit avR);
            bool avStatic = backend.RayCast(new Vector3(90f, 90f, 20f), new Vector3(0, 0, -1), 25f, QueryFilter.Static, out RayHit avRS);
            Check(avRay && avR.UserData == 7500u, $"RayCast filter=Avatar now HITS the avatar (ud {(avRay ? avR.UserData : 0)})");
            Check(!(avStatic && avRS.UserData == 7500u), "RayCast filter=Static EXCLUDES the avatar marker");

            // RayCastAll includes the avatar.
            var avAll = new RayHit[8];
            int avN = backend.RayCastAll(new Vector3(90f, 90f, 20f), new Vector3(0, 0, -1), 25f, QueryFilter.All, avAll);
            bool allHasAvatar = false; for (int i = 0; i < avN; i++) if (avAll[i].UserData == 7500u) allHasAvatar = true;
            Check(allHasAvatar, "RayCastAll now includes the avatar");

            // OverlapSphere finds the avatar under filter=Avatar, excludes under filter=Static.
            int avOv = backend.OverlapSphere(new Vector3(90f, 90f, 0.9f), 2f, QueryFilter.Avatar, obuf);
            bool ovHasAvatar = false; BodyId markerBody = BodyId.Invalid;
            for (int i = 0; i < avOv; i++) { backend.TryGetBodyState(obuf[i], out BodyState bst); if (bst.UserData == 7500u) { ovHasAvatar = true; markerBody = obuf[i]; } }
            int avOvS = backend.OverlapSphere(new Vector3(90f, 90f, 0.9f), 2f, QueryFilter.Static, obuf);
            bool ovStaticHasAvatar = false; for (int i = 0; i < avOvS; i++) { backend.TryGetBodyState(obuf[i], out BodyState b2); if (b2.UserData == 7500u) ovStaticHasAvatar = true; }
            Check(ovHasAvatar, "OverlapSphere filter=Avatar now FINDS the avatar");
            Check(!ovStaticHasAvatar, "OverlapSphere filter=Static EXCLUDES the avatar");

            // #30 query-boundary contract: a query hands back the marker's BodyId. It IS a valid body -
            // TryGetBodyState returns the avatar's TRANSFORM (position/orientation) but it is a kinematic
            // marker with no dynamics. Identity is UserData (the avatar id), never the BodyId.
            backend.TryGetBodyState(markerBody, out BodyState markerState);
            Console.WriteLine($"      marker BodyId valid={backend.IsBodyValid(markerBody)}; TryGetBodyState -> pos={markerState.Position} ud={markerState.UserData} (avatar transform; kinematic)");
            Check(markerBody.IsValid && backend.IsBodyValid(markerBody), "marker BodyId is a valid body handle");
            Check(markerState.UserData == 7500u && MathF.Abs(markerState.Position.Z - 0.75f) < 0.05f, "marker BodyId resolves to the avatar transform (identity via UserData)");
            Console.WriteLine("      => #35 RESOLVED: avatars are query-visible (Avatar filter only). Movement/contacts unchanged (marker collides with nothing).");
            backend.RemoveCharacter(avatarQ);

            // ---- [39] TERRAIN UN-BURY: raising terrain under a standing avatar must lift it, not bury it. ----
            // The live papercut: SetTerrain swaps the heightfield body (remove old + add higher); the
            // CharacterVirtual keeps its old Z and ends below the new surface. Prove the burial (control),
            // then that ReGroundCharacter snaps it onto the raised surface and it RESTS (velocity zeroed).
            Console.WriteLine("\n[39] Terrain un-bury (raise terrain under a standing avatar)");
            {
                var ubB = new BodyState[8]; var ubC = new CharacterState[2]; var ubCt = new ContactReport[16];
                ShapeId ub_flat = backend.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
                backend.SetTerrain(ub_flat, Vector3.Zero);
                var ubDesc = CharacterDesc.Default;                       // capsule 0.45/0.30 -> rests ~0.75 on z=0
                ubDesc.Position = new Vector3(40f, 40f, 0.75f);
                CharacterId ub = backend.CreateCharacter(ubDesc);
                for (int i = 0; i < 30; i++) { backend.SetCharacterMovement(ub, Vector3.Zero, false, false); backend.Step(1f / 60f, ubB, ubC, ubCt); }
                backend.TryGetCharacterState(ub, out CharacterState ubRest);
                float restZ = ubRest.Position.Z;                          // seat offset above the surface (standHalf+feet)
                Check(ubRest.IsSupported && restZ > 0.4f && restZ < 1.2f, $"character rests on flat terrain (z={restZ:0.000}, supported)");

                // Raise terrain to Z=25 under it (the SetTerrain swap the live edit triggers).
                const float RAISE = 25f;
                float[] highField = new float[N * N];
                for (int i = 0; i < highField.Length; i++) highField[i] = RAISE;
                ShapeId ub_high = backend.CreateHeightFieldShape(highField, N, N, new Vector3(S, S, S));
                backend.SetTerrain(ub_high, Vector3.Zero);

                // CONTROL: step WITHOUT re-grounding -> buried, far below the raised surface (can't climb 24 m).
                for (int i = 0; i < 10; i++) { backend.SetCharacterMovement(ub, Vector3.Zero, false, false); backend.Step(1f / 60f, ubB, ubC, ubCt); }
                backend.TryGetCharacterState(ub, out CharacterState ubBuried);
                Check(ubBuried.Position.Z < RAISE - 1f, $"CONTROL: without un-bury the avatar is BURIED below the raised surface (z={ubBuried.Position.Z:0.000} << {RAISE})");

                // FIX: re-ground onto the raised surface (seatZ = raise + measured seat offset), velocity zeroed.
                float seatZ = RAISE + restZ;
                backend.ReGroundCharacter(ub, new Vector3(ubBuried.Position.X, ubBuried.Position.Y, seatZ));
                for (int i = 0; i < 40; i++) { backend.SetCharacterMovement(ub, Vector3.Zero, false, false); backend.Step(1f / 60f, ubB, ubC, ubCt); }
                backend.TryGetCharacterState(ub, out CharacterState ubLifted);
                Check(Math.Abs(ubLifted.Position.Z - seatZ) < 0.5f, $"FIX: avatar lifted onto the raised surface and RESTS there (z={ubLifted.Position.Z:0.000} ~ seatZ {seatZ:0.000})");
                Check(ubLifted.IsSupported, "FIX: lifted avatar is supported (standing on new terrain, not falling)");
                Check(ubLifted.Position.Z > RAISE, $"FIX: avatar is ABOVE the raised surface Z={RAISE} (not buried), z={ubLifted.Position.Z:0.000}");

                backend.RemoveCharacter(ub);
                backend.ReleaseShape(ub_flat);
                backend.ReleaseShape(ub_high);
            }

            backend.ReleaseShape(sphere); backend.ReleaseShape(qbox);

            // cleanup
            backend.RemoveBody(boxBody);

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
        RunCharacterDeterminismCheck();

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "=== M1..M4.5 HARNESS: PASS ==="
            : $"=== M1..M4.5 HARNESS: FAIL ({_fails} failed check(s)) ===");
        return _fails == 0 ? 0 : 1;
    }

    // Character walks +X into a static plateau whose top is at stepTopZ. Returns whether it climbed
    // onto it (rose ~stepTopZ and advanced past the front face) - true for a step <= StepHeight,
    // false for one above it (blocked). `region` spaces successive tests apart on the shared terrain.
    private static (bool climbed, float finalZ, float finalX) WalkIntoStep(ILegionPhysicsBackend b, float stepTopZ, uint region)
    {
        float baseX = 40f + region * 20f;
        ShapeId stepShape = b.CreateBoxShape(new Vector3(4f, 4f, MathF.Max(0.05f, stepTopZ / 2f)));
        var sd = BodyDesc.Default;
        sd.Shape = stepShape;
        sd.Position = new Vector3(baseX + 5f, 20f, stepTopZ / 2f); // top face at stepTopZ, front face at baseX+1
        sd.MotionType = BodyMotionType.Static;
        sd.Layer = PhysicsLayer.Static;
        BodyId step = b.CreateBody(sd);

        var cd = CharacterDesc.Default;
        cd.Position = new Vector3(baseX, 20f, 0.75f);
        cd.UserData = 8000u + region;
        CharacterId c = b.CreateCharacter(cd);

        var bb = new BodyState[8]; var cc = new CharacterState[2]; var ct = new ContactReport[16];
        for (int i = 0; i < 180; i++) { b.SetCharacterMovement(c, new Vector3(1.5f, 0f, 0f), false, false); b.Step(1f / 60f, bb, cc, ct); }
        b.TryGetCharacterState(c, out CharacterState st);

        bool climbed = st.Position.Z > 0.75f + stepTopZ - 0.15f && st.Position.X > baseX + 2f;
        b.RemoveCharacter(c);
        b.RemoveBody(step);
        b.ReleaseShape(stepShape);
        return (climbed, st.Position.Z, st.Position.X);
    }

    // Drops a character onto a static ramp tilted `angleDeg` about Y and lets it settle with no input.
    // Below MaxSlopeAngle it should stand (not sliding, little drift); above it, IsSliding and it slides.
    private static (bool sliding, float drift, bool supported) RunSlopeTest(ILegionPhysicsBackend b, float angleDeg, uint region)
    {
        float baseX = 90f + region * 25f;
        float th = angleDeg * MathF.PI / 180f;
        ShapeId rampShape = b.CreateBoxShape(new Vector3(10f, 10f, 0.25f));
        var rd = BodyDesc.Default;
        rd.Shape = rampShape;
        rd.Position = new Vector3(baseX, 20f, 4f);
        rd.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, th); // tilt about Y
        rd.MotionType = BodyMotionType.Static;
        rd.Layer = PhysicsLayer.Static;
        BodyId ramp = b.CreateBody(rd);

        var cd = CharacterDesc.Default;
        cd.Position = new Vector3(baseX, 20f, 4f + 0.25f + 0.75f + 0.4f); // just above the ramp top
        cd.UserData = 8500u + region;
        CharacterId c = b.CreateCharacter(cd);

        var bb = new BodyState[8]; var cc = new CharacterState[2]; var ct = new ContactReport[16];
        for (int i = 0; i < 40; i++) { b.SetCharacterMovement(c, Vector3.Zero, false, false); b.Step(1f / 60f, bb, cc, ct); } // land
        b.TryGetCharacterState(c, out CharacterState landed);
        Vector3 landedXY = new Vector3(landed.Position.X, landed.Position.Y, 0f);

        // Observe: IsSliding must be sampled DURING the descent - by the end a steep-slope character
        // has slid off onto flat terrain and reads OnGround again. Track whether it ever slid, and its
        // total horizontal drift.
        bool everSlid = false;
        bool supported = true;
        for (int i = 0; i < 120; i++)
        {
            b.SetCharacterMovement(c, Vector3.Zero, false, false);
            b.Step(1f / 60f, bb, cc, ct);
            b.TryGetCharacterState(c, out CharacterState s);
            if (s.IsSliding) everSlid = true;
            supported = s.IsSupported;
        }
        b.TryGetCharacterState(c, out CharacterState after);
        Vector3 afterXY = new Vector3(after.Position.X, after.Position.Y, 0f);

        float drift = (afterXY - landedXY).Length();
        bool sliding = everSlid;
        b.RemoveCharacter(c);
        b.RemoveBody(ramp);
        b.ReleaseShape(rampShape);
        return (sliding, drift, supported);
    }

    // Character stands on a kinematic box moving +X at constant velocity and should ride it.
    private static (float charDx, float boxDx, bool rode) RunMovingPlatform(ILegionPhysicsBackend b, uint region)
    {
        float baseX = 150f + region * 10f;
        ShapeId platShape = b.CreateBoxShape(new Vector3(1.5f, 1.5f, 0.25f));
        var pd = BodyDesc.Default;
        pd.Shape = platShape;
        pd.Position = new Vector3(baseX, 20f, 1.0f);           // top face at 1.25
        pd.MotionType = BodyMotionType.Kinematic;
        pd.Layer = PhysicsLayer.Dynamic;
        pd.StartActive = true;
        BodyId plat = b.CreateBody(pd);
        b.SetBodyLinearVelocity(plat, new Vector3(1f, 0f, 0f)); // 1 m/s +X
        b.ActivateBody(plat);

        var cd = CharacterDesc.Default;
        cd.Position = new Vector3(baseX, 20f, 2.0f);           // centre = platform top 1.25 + 0.75
        cd.UserData = 8600u + region;
        CharacterId c = b.CreateCharacter(cd);

        var bb = new BodyState[8]; var cc = new CharacterState[2]; var ct = new ContactReport[16];
        for (int i = 0; i < 120; i++) { b.SetCharacterMovement(c, Vector3.Zero, false, false); b.Step(1f / 60f, bb, cc, ct); }
        b.TryGetCharacterState(c, out CharacterState cs);
        b.TryGetBodyState(plat, out BodyState ps);
        float charDx = cs.Position.X - baseX, boxDx = ps.Position.X - baseX;
        bool rode = charDx > 1.0f && MathF.Abs(charDx - boxDx) < 0.5f;
        b.RemoveCharacter(c);
        b.RemoveBody(plat);
        b.ReleaseShape(platShape);
        return (charDx, boxDx, rode);
    }

    // Faithful repro of John's live `jolt droptest` + walk-onto: a 2x2x2 m (8000 kg) box DROPPED from
    // height so it settles AND SLEEPS on flat terrain, then the character (a) dropped straight on top,
    // and (b) walked horizontally into it. Run at BOTH the harness dt (1/60) and the LIVE dt (0.0908 s,
    // OpenSim's 11 fps physics) with the LIVE avatar capsule, because the live-vs-harness gap is exactly
    // the timestep: CharacterVirtual.ExtendedUpdate takes ONE collide-and-slide per Step, so a 5.4x larger
    // dt means 5.4x deeper penetration per step and is the prime suspect for "resistance then fall through".
    private static void RunStandOnDynamicBox(ILegionPhysicsBackend b)
    {
        // Live avatar capsule from AvatarBoxSize (0.45, 0.6, 1.9): radius 0.225, half-height 0.725.
        StandOnBoxAtDt(b, 1f / 60f, "harness 1/60", 0.725f, 0.225f);
        StandOnBoxAtDt(b, 0.0908f, "LIVE 0.0908 (11fps)", 0.725f, 0.225f);
        DropTunnelVsSubstep(b);
    }

    // The root cause of the live stand-on-box failure and the fix, side by side: a box DROPPED from height
    // and integrated in ONE 0.0908 s step (OpenSim's 11 fps) tunnels through the terrain; the SAME drop
    // integrated in 6 sub-slices of ~0.0151 s (what LegionJoltScene.Simulate now does) rests on the surface.
    private static void DropTunnelVsSubstep(ILegionPhysicsBackend b)
    {
        ShapeId flat = b.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);
        var bb = new BodyState[8]; var cc = new CharacterState[2]; var ct = new ContactReport[16];
        ShapeId boxShape = b.CreateBoxShape(new Vector3(1f, 1f, 1f));

        float DropAndSettle(float frameDt, int subSteps)
        {
            var bd = BodyDesc.Default;
            bd.Shape = boxShape; bd.Position = new Vector3(70f, 70f, 16f);
            bd.MotionType = BodyMotionType.Dynamic; bd.Layer = PhysicsLayer.Dynamic; bd.StartActive = true;
            BodyId box = b.CreateBody(bd); b.ActivateBody(box);
            float sub = frameDt / subSteps;
            for (int f = 0; f < 150; f++) for (int s = 0; s < subSteps; s++) b.Step(sub, bb, cc, ct);
            b.TryGetBodyState(box, out BodyState st);
            float z = st.Position.Z;
            b.RemoveBody(box);
            return z;
        }

        float raw = DropAndSettle(0.0908f, 1);       // the bug: one big step
        float fixedZ = DropAndSettle(0.0908f, 6);    // 6 sub-slices per 0.0908 s frame
        Console.WriteLine($"      --- drop-tunnel vs sub-step (box rest z should be ~1.0) ---");
        Console.WriteLine($"      raw 0.0908 x1  -> box z = {raw:0.000}  (tunnels through terrain if << 0)");
        Console.WriteLine($"      0.0908 /6 sub  -> box z = {fixedZ:0.000}  (rests on terrain)");

        // TARGETED alternative: CCD (LinearCast) on the DROPPED box, ONE step of 0.0908 (no sub-stepping).
        var cbd = BodyDesc.Default;
        cbd.Shape = boxShape; cbd.Position = new Vector3(70f, 70f, 16f);
        cbd.MotionType = BodyMotionType.Dynamic; cbd.Layer = PhysicsLayer.Dynamic; cbd.StartActive = true;
        cbd.UseCcd = true;   // <-- the decouple: fast body gets continuous collision, world stays 1 step/frame
        BodyId cbox = b.CreateBody(cbd); b.ActivateBody(cbox);
        for (int f = 0; f < 150; f++) b.Step(0.0908f, bb, cc, ct);
        b.TryGetBodyState(cbox, out BodyState cst);
        float ccdZ = cst.Position.Z; b.RemoveBody(cbox);
        Console.WriteLine($"      CCD 0.0908 x1  -> box z = {ccdZ:0.000}  (rests WITHOUT sub-stepping)");
        Check(raw < -5f, $"repro: single 0.0908 step tunnels a NON-CCD dropped box through terrain (z {raw:0.000})");

        // DECOUPLED fix candidate: raise the backend's CollisionSteps. Jolt subdivides the RIGID-BODY solve
        // into N sub-steps INSIDE _system.Update, WITHOUT re-running StepCharacter (the character is stepped
        // once per Step, before Update). So bodies get anti-tunnelling while the character stays at 1 step/
        // frame - the known-good avatar behaviour. Fresh backend so CollisionSteps is applied at Init.
        var settings = PhysicsBackendSettings.Default;
        settings.CollisionSteps = 6;
        var cbk = new JoltPhysicsBackend();
        cbk.Initialize(settings);
        ShapeId cflat = cbk.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
        cbk.SetTerrain(cflat, Vector3.Zero);
        ShapeId csBox = cbk.CreateBoxShape(new Vector3(1f, 1f, 1f));
        var csd = BodyDesc.Default;
        csd.Shape = csBox; csd.Position = new Vector3(70f, 70f, 16f);
        csd.MotionType = BodyMotionType.Dynamic; csd.Layer = PhysicsLayer.Dynamic; csd.StartActive = true;
        BodyId csbox = cbk.CreateBody(csd); cbk.ActivateBody(csbox);
        var bb2 = new BodyState[8]; var cc2 = new CharacterState[2]; var ct2 = new ContactReport[16];
        for (int f = 0; f < 150; f++) cbk.Step(0.0908f, bb2, cc2, ct2);   // ONE 0.0908 Step per frame
        cbk.TryGetBodyState(csbox, out BodyState csst);
        float csZ = csst.Position.Z;
        Console.WriteLine($"      CollisionSteps=6, 0.0908 x1 -> box z = {csZ:0.000}  (rests, character still 1 step/frame)");
        Check(MathF.Abs(csZ - 1.0f) < 0.15f, $"DECOUPLED fix: CollisionSteps=6 rests the box at 1 step/frame (z {csZ:0.000}) - bodies sub-step, character does not");

        b.ReleaseShape(boxShape);

        // --- Does the LIVE sub-step STRUCTURE bounce a standing character? Mimic Simulate exactly:
        //     movement set ONCE per frame, world stepped in 6 slices; vs the old 1-step-per-frame. ---
        Console.WriteLine("      --- standing character: 1-step/frame vs 6-substep/frame (bounce check) ---");
        (float min1, float max1) = StandBounce(b, 0.0908f, 1);
        (float min6, float max6) = StandBounce(b, 0.0908f, 6);
        Console.WriteLine($"      1-step/frame : charZ range [{min1:0.000}, {max1:0.000}] amplitude {max1 - min1:0.000}");
        Console.WriteLine($"      6-substep/fr : charZ range [{min6:0.000}, {max6:0.000}] amplitude {max6 - min6:0.000}");
        Check(max6 - min6 < 0.05f, $"6-substep standing character is stable, not bouncing (amplitude {max6 - min6:0.000})");

        // Jump at the LIVE dt, ONE step per frame (the reverted regime): does jump=true leave the ground?
        {
            ShapeId jflat = b.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
            b.SetTerrain(jflat, Vector3.Zero);
            var jbb = new BodyState[8]; var jcc = new CharacterState[2]; var jct = new ContactReport[16];
            var jcd = CharacterDesc.Default; jcd.CapsuleHalfHeight = 0.725f; jcd.CapsuleRadius = 0.225f;
            jcd.Position = new Vector3(55f, 55f, 0.96f); jcd.UserData = 9300u;
            CharacterId jc = b.CreateCharacter(jcd);
            for (int f = 0; f < 20; f++) { b.SetCharacterMovement(jc, Vector3.Zero, false, false); b.Step(0.0908f, jbb, jcc, jct); }
            b.SetCharacterMovement(jc, Vector3.Zero, true, false);   // jump this frame
            b.Step(0.0908f, jbb, jcc, jct);
            b.TryGetCharacterState(jc, out CharacterState jumped);
            float peak = jumped.Position.Z;
            for (int f = 0; f < 20; f++) { b.SetCharacterMovement(jc, Vector3.Zero, false, false); b.Step(0.0908f, jbb, jcc, jct); b.TryGetCharacterState(jc, out CharacterState s); if (s.Position.Z > peak) peak = s.Position.Z; }
            Console.WriteLine($"      jump @0.0908 x1: takeoff vZ={jumped.LinearVelocity.Z:0.00}, peak z={peak:0.000} (rest 0.950)");
            Check(peak > 0.950f + 0.3f, $"jump leaves the ground at live dt 1-step/frame (peak {peak:0.000})");
            b.RemoveCharacter(jc);
        }
    }

    // Isolate the live SINK: does CollisionSteps affect a standing character, and WHAT body is its ground
    // (UserData 0 = terrain, = char's own UserData = its M4.5 marker, = a prim id = a box)? Fresh backend
    // per CollisionSteps; terrain raised to ~58 to match the live region height in case coordinates matter.
    // M6.6 sit/unsit is character REMOVE (sit) / RECREATE (unsit). Prove the cycle leaves no residue over
    // many cycles: each create -> exactly 1 live character drained; each remove -> 0; and a fresh create
    // after a remove always re-engages (a stuck-seated leak would show as a growing/non-zero count).
    private static void RunSitUnsitCycle(ILegionPhysicsBackend b)
    {
        ShapeId flat = b.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);
        var bb = new BodyState[8]; var cc = new CharacterState[4]; var ct = new ContactReport[16];
        float standHalf = 0.45f + 0.30f;
        bool clean = true;
        for (int cycle = 0; cycle < 6; cycle++)
        {
            var cd = CharacterDesc.Default;                 // "unsit" -> recreate the walking character
            cd.Position = new Vector3(100f, 100f, standHalf + 0.01f);
            cd.UserData = 5000u + (uint)cycle;
            CharacterId c = b.CreateCharacter(cd);
            StepResult afterCreate = default;
            for (int s = 0; s < 15; s++) afterCreate = b.Step(1f / 60f, bb, cc, ct);   // settle onto terrain
            b.TryGetCharacterState(c, out CharacterState cs);
            b.RemoveCharacter(c);                            // "sit" -> remove the character
            StepResult afterRemove = b.Step(1f / 60f, bb, cc, ct);
            bool cycleOk = afterCreate.CharacterUpdateCount == 1 && afterRemove.CharacterUpdateCount == 0 && cs.IsSupported;
            Console.WriteLine($"        cycle {cycle}: create->live={afterCreate.CharacterUpdateCount} supported={cs.IsSupported}, remove->live={afterRemove.CharacterUpdateCount}  {(cycleOk ? "ok" : "LEAK")}");
            if (!cycleOk) clean = false;
        }
        Check(clean, "sit/unsit x6: every create -> 1 supported character, every remove -> 0 (no leak, always re-engages)");
    }

    // M6.7 Task 2: llCastRay RC_GET_NORMAL comes from RayHit.Normal. Single RayCast normals were proven at
    // 6.3 (jolt probe); RayCastAll (the multi-hit path llCastRay/RaycastWorld uses) was never checked for
    // the normal. Cast straight DOWN at flat terrain both ways and compare - a zero from RayCastAll is the
    // Phlox "zero normal" bug.
    private static void RunRayCastNormal(ILegionPhysicsBackend b)
    {
        ShapeId flat = b.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);

        var origin = new Vector3(90f, 90f, 50f);
        var down = new Vector3(0f, 0f, -1f);

        // A DYNAMIC box resting on the terrain, so the down-ray crosses box-top then terrain (like John's
        // leftover boxes). Filter Static|Dynamic|Terrain = what the 4-arg RaycastWorld (Phlox) uses.
        ShapeId boxShape = b.CreateBoxShape(new Vector3(1f, 1f, 1f));
        var bd = BodyDesc.Default; bd.Shape = boxShape; bd.Position = new Vector3(90f, 90f, 1f);
        bd.MotionType = BodyMotionType.Dynamic; bd.Layer = PhysicsLayer.Dynamic; bd.StartActive = true; bd.UserData = 4242u;
        BodyId box = b.CreateBody(bd); b.ActivateBody(box);
        var bb = new BodyState[4]; var cc = new CharacterState[1]; var ct = new ContactReport[4];
        for (int i = 0; i < 60; i++) b.Step(1f / 60f, bb, cc, ct);  // settle box

        bool single = b.RayCast(origin, down, 100f, QueryFilter.Terrain, out RayHit sh);
        var many = new RayHit[8];
        int nAll = b.RayCastAll(origin, down, 100f, QueryFilter.Terrain | QueryFilter.Static | QueryFilter.Dynamic, many);

        Console.WriteLine($"      single RayCast (Terrain): hit={single} normal=({sh.Normal.X:0.00},{sh.Normal.Y:0.00},{sh.Normal.Z:0.00}) pointZ={sh.Point.Z:0.000}");
        Console.WriteLine($"      RayCastAll (Terrain|Static|Dynamic): {nAll} hit(s):");
        for (int i = 0; i < nAll; i++)
            Console.WriteLine($"        [{i}] UserData={many[i].UserData} dist={many[i].Distance:0.000} pointZ={many[i].Point.Z:0.000} normal=({many[i].Normal.X:0.00},{many[i].Normal.Y:0.00},{many[i].Normal.Z:0.00})");

        bool anyZeroNormal = false;
        for (int i = 0; i < nAll; i++) if (many[i].Normal.LengthSquared() < 0.01f) anyZeroNormal = true;
        Check(single && sh.Normal.Z > 0.9f, $"single RayCast terrain normal ~ +Z (got {sh.Normal.Z:0.00})");
        Check(nAll > 0 && !anyZeroNormal, $"RayCastAll: every hit has a non-zero normal (llCastRay RC_GET_NORMAL) - {nAll} hits, anyZero={anyZeroNormal}");

        b.RemoveBody(box); b.ReleaseShape(boxShape);
    }

    private static void RunCharacterGroundIdentity(int collisionSteps, float terrainH)
        => RunCharacterGroundIdentity(collisionSteps, terrainH, 0f);

    private static void RunCharacterGroundIdentity(int collisionSteps, float terrainH, float walkSpeed)
    {
        var settings = PhysicsBackendSettings.Default;
        settings.CollisionSteps = collisionSteps;
        var bk = new JoltPhysicsBackend();
        bk.Initialize(settings);
        var field = new float[N * N];
        for (int i = 0; i < field.Length; i++) field[i] = terrainH;
        ShapeId ter = bk.CreateHeightFieldShape(field, N, N, new Vector3(S, S, S));
        bk.SetTerrain(ter, Vector3.Zero);

        var bb = new BodyState[8]; var cc = new CharacterState[2]; var ct = new ContactReport[16];
        const uint CHAR_UD = 7777u;
        float standHalf = 0.725f + 0.225f;
        var cd = CharacterDesc.Default; cd.CapsuleHalfHeight = 0.725f; cd.CapsuleRadius = 0.225f;
        cd.Position = new Vector3(90f, 90f, terrainH + standHalf + 0.01f); cd.UserData = CHAR_UD;
        CharacterId c = bk.CreateCharacter(cd);

        Console.WriteLine($"      --- CollisionSteps={collisionSteps}, terrainH={terrainH}, walkSpeed={walkSpeed} (char UserData={CHAR_UD}) ---");
        var desired = new Vector3(walkSpeed, 0f, 0f);
        float z0 = 0, zN = 0;
        for (int f = 0; f < 120; f++)
        {
            bk.SetCharacterMovement(c, desired, false, false);
            bk.Step(0.0908f, bb, cc, ct);
            bk.TryGetCharacterState(c, out CharacterState cs);
            if (f == 0) z0 = cs.Position.Z;
            zN = cs.Position.Z;
            if (f == 0 || f == 40 || f == 80 || f == 119)
            {
                uint gud = 999999; string what = "none";
                if (cs.GroundBody.IsValid && bk.TryGetBodyState(cs.GroundBody, out BodyState gb))
                {
                    gud = gb.UserData;
                    what = gud == 0 ? "TERRAIN" : gud == CHAR_UD ? "OWN-MARKER!" : $"body({gud})";
                }
                Console.WriteLine($"        f{f,3} Z={cs.Position.Z:0.000} sup={(cs.IsSupported ? "Y" : "N")} vZ={cs.LinearVelocity.Z:0.000} groundUserData={gud} => {what}");
            }
        }
        float drift = zN - z0;
        Console.WriteLine($"        DRIFT over 120 frames = {drift:0.000} m ({(MathF.Abs(drift) < 0.02f ? "STABLE" : "SINKING")})");
        bk.RemoveCharacter(c);
    }

    // Stand a character on flat terrain and report its Z range over 60 frames. subSteps>1 mimics the live
    // Simulate loop: SetCharacterMovement is called ONCE per frame, the world is stepped in subSteps slices.
    private static (float min, float max) StandBounce(ILegionPhysicsBackend b, float frameDt, int subSteps)
    {
        ShapeId flat = b.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);
        var bb = new BodyState[8]; var cc = new CharacterState[2]; var ct = new ContactReport[16];
        var cd = CharacterDesc.Default; cd.CapsuleHalfHeight = 0.725f; cd.CapsuleRadius = 0.225f;
        float standHalf = 0.725f + 0.225f;
        cd.Position = new Vector3(50f, 50f, standHalf + 0.01f);   // the live seat
        cd.UserData = 9200u;
        CharacterId c = b.CreateCharacter(cd);
        float sub = frameDt / subSteps;
        float min = float.MaxValue, max = float.MinValue;
        bool logged = false;
        for (int f = 0; f < 60; f++)
        {
            b.SetCharacterMovement(c, Vector3.Zero, false, false);   // ONCE per frame (as live does)
            for (int s = 0; s < subSteps; s++)
            {
                b.Step(sub, bb, cc, ct);
                b.TryGetCharacterState(c, out CharacterState cs);
                if (f >= 5) { min = MathF.Min(min, cs.Position.Z); max = MathF.Max(max, cs.Position.Z); }
                if (subSteps > 1 && f < 3 && !logged)
                    Console.WriteLine($"        [charframe] f{f} s{s} Z={cs.Position.Z:0.0000} sup={(cs.IsSupported ? "Y" : "N")} vZ={cs.LinearVelocity.Z:0.000} footAboveTerrain={cs.Position.Z - standHalf:0.0000}");
            }
        }
        Console.WriteLine($"        (dt={frameDt}/{subSteps})");
        b.RemoveCharacter(c);
        return (min, max);
    }

    private static void StandOnBoxAtDt(ILegionPhysicsBackend b, float dt, string label, float capHalf, float capRadius)
    {
        Console.WriteLine($"      --- dt={label}, capsule half={capHalf:0.000} r={capRadius:0.000} ---");
        ShapeId flat = b.CreateHeightFieldShape(FlatField(), N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);

        const float bx = 90f, by = 90f;
        ShapeId boxShape = b.CreateBoxShape(new Vector3(1f, 1f, 1f)); // HALF-extents -> 2x2x2 m, 8000 kg
        var bd = BodyDesc.Default;
        bd.Shape = boxShape;
        bd.Position = new Vector3(bx, by, 1.0f);   // placed ALREADY AT REST (bottom on terrain) - isolates
        bd.MotionType = BodyMotionType.Dynamic;    // the CHARACTER's behaviour from the drop-tunnel confound
        bd.Layer = PhysicsLayer.Dynamic;
        bd.StartActive = true;
        BodyId box = b.CreateBody(bd);
        b.ActivateBody(box);

        var bb = new BodyState[16]; var cc = new CharacterState[4]; var ct = new ContactReport[64];
        StepResult sr = default;
        for (int i = 0; i < 60; i++) sr = b.Step(dt, bb, cc, ct);   // brief settle (already resting)
        b.TryGetBodyState(box, out BodyState boxRest);
        bool boxAsleep = (boxRest.Flags & BodyStateFlags.Active) == 0;
        float boxTop = boxRest.Position.Z + 1f;
        float standHalf = capHalf + capRadius;
        Console.WriteLine($"      box settled: centreZ={boxRest.Position.Z:0.000} top={boxTop:0.000} asleep={boxAsleep} (ActiveBodies={sr.ActiveBodyCount})");

        // --- (a) drop straight onto the box top ---
        var cd = CharacterDesc.Default; cd.CapsuleHalfHeight = capHalf; cd.CapsuleRadius = capRadius;
        cd.Position = new Vector3(bx, by, boxTop + standHalf + 0.10f);
        cd.UserData = 9100u;
        CharacterId c = b.CreateCharacter(cd);
        for (int i = 0; i < 180; i++) { b.SetCharacterMovement(c, Vector3.Zero, false, false); b.Step(dt, bb, cc, ct); }
        b.TryGetCharacterState(c, out CharacterState dropFin);
        b.TryGetBodyState(box, out BodyState boxNow);
        float expectedStandZ = (boxNow.Position.Z + 1f) + standHalf;
        bool sankA = dropFin.Position.Z < (boxNow.Position.Z + 1f) - 0.1f;
        bool stoodDropped = dropFin.IsSupported && dropFin.GroundBody.IsValid && MathF.Abs(dropFin.Position.Z - expectedStandZ) < 0.3f;
        Console.WriteLine($"      (a) dropped-on-top: charZ={dropFin.Position.Z:0.000} exp={expectedStandZ:0.000} supported={dropFin.IsSupported} groundBody={(dropFin.GroundBody.IsValid ? "box" : "none/terrain")} sank={sankA}");
        Check(stoodDropped && !sankA, $"(a,{label}) stands when dropped ON the dynamic box (z {dropFin.Position.Z:0.000} ~ {expectedStandZ:0.000})");
        b.RemoveCharacter(c);

        // --- (b) walk horizontally into the box from the ground (John's action) ---
        var cd2 = CharacterDesc.Default; cd2.CapsuleHalfHeight = capHalf; cd2.CapsuleRadius = capRadius;
        cd2.Position = new Vector3(bx - 3.0f, by, standHalf);
        cd2.UserData = 9101u;
        CharacterId c2 = b.CreateCharacter(cd2);
        Console.WriteLine("      (b) walk +X into the box:  step | charX  | charZ  | sup | groundBody   | boxX   | boxActive");
        for (int i = 0; i < 300; i++)
        {
            b.SetCharacterMovement(c2, new Vector3(1.5f, 0f, 0f), false, false);
            b.Step(dt, bb, cc, ct);
            if (i < 2 || i % 40 == 39)
            {
                b.TryGetCharacterState(c2, out CharacterState cs);
                b.TryGetBodyState(box, out BodyState bs);
                string gb = cs.GroundBody.IsValid ? $"body({cs.GroundBody.Value})" : "none/terrain";
                bool act = (bs.Flags & BodyStateFlags.Active) != 0;
                Console.WriteLine($"                                 {i + 1,4} | {cs.Position.X,6:0.000} | {cs.Position.Z,6:0.000} |  {(cs.IsSupported ? "Y" : "N")}  | {gb,-12} | {bs.Position.X,6:0.000} | {(act ? "Y" : "N")}");
            }
        }
        b.TryGetCharacterState(c2, out CharacterState walkFin);
        b.TryGetBodyState(box, out BodyState boxFin);
        float boxFrontX = boxFin.Position.X - 1f;
        bool tunnelled = walkFin.Position.X > boxFrontX - capRadius + 0.25f;
        Console.WriteLine($"      (b) walked-into: charX={walkFin.Position.X:0.000} boxFrontX={boxFrontX:0.000} charZ={walkFin.Position.Z:0.000} supported={walkFin.IsSupported} groundBody={(walkFin.GroundBody.IsValid ? "box" : "none/terrain")} tunnelled={tunnelled}");
        Check(!tunnelled, $"(b,{label}) blocked by the box, did NOT tunnel through it (charX {walkFin.Position.X:0.000} vs frontX {boxFrontX:0.000})");

        b.RemoveCharacter(c2);
        b.RemoveBody(box);
        b.ReleaseShape(boxShape);
    }

    // Character walks +X into a light dynamic box: should push it and not tunnel through.
    private static (float boxDx, bool noTunnel) RunPushTest(ILegionPhysicsBackend b, uint region)
    {
        float baseX = 170f + region * 10f;
        ShapeId boxShape = b.CreateBoxShape(new Vector3(0.4f, 0.4f, 0.4f));
        var bd = BodyDesc.Default;
        bd.Shape = boxShape;
        bd.Position = new Vector3(baseX + 2f, 20f, 0.4f);
        bd.MotionType = BodyMotionType.Dynamic;
        bd.Layer = PhysicsLayer.Dynamic;
        bd.Mass = 10f;
        bd.StartActive = true;
        BodyId box = b.CreateBody(bd);

        var cd = CharacterDesc.Default;
        cd.Position = new Vector3(baseX, 20f, 0.75f);
        cd.UserData = 8700u + region;
        CharacterId c = b.CreateCharacter(cd);

        var bb = new BodyState[8]; var cc = new CharacterState[2]; var ct = new ContactReport[16];
        for (int i = 0; i < 180; i++) { b.SetCharacterMovement(c, new Vector3(1.5f, 0f, 0f), false, false); b.Step(1f / 60f, bb, cc, ct); }
        b.TryGetBodyState(box, out BodyState bs);
        b.TryGetCharacterState(c, out CharacterState cs);
        float boxDx = bs.Position.X - (baseX + 2f);
        bool noTunnel = cs.Position.X < bs.Position.X + 0.5f; // stayed behind/against the box
        b.RemoveCharacter(c);
        b.RemoveBody(box);
        b.ReleaseShape(boxShape);
        return (boxDx, noTunnel);
    }

    // Creates a body of the given shape/layer/motion at pos (not stepped, so it stays put for queries).
    private static BodyId MakeStaticAt(ILegionPhysicsBackend b, ShapeId shape, Vector3 pos, uint ud, PhysicsLayer layer, BodyMotionType motion)
    {
        var d = BodyDesc.Default;
        d.Shape = shape; d.Position = pos; d.Layer = layer; d.MotionType = motion; d.UserData = ud;
        d.StartActive = false; // never activated/stepped in the query sections, so it holds position
        return b.CreateBody(d);
    }

    // Places a static body with `shape` at `pos` and raycasts straight down onto it from above,
    // returning the surface Z (or NaN on miss). Leaves the body in place (caller removes it).
    private static float RayTopOf(ILegionPhysicsBackend b, ShapeId shape, Vector3 pos, uint ud, out BodyId body)
    {
        var d = BodyDesc.Default;
        d.Shape = shape; d.Position = pos; d.MotionType = BodyMotionType.Static; d.Layer = PhysicsLayer.Static; d.UserData = ud;
        body = b.CreateBody(d);
        bool hit = b.RayCast(new Vector3(pos.X, pos.Y, pos.Z + 50f), new Vector3(0, 0, -1), 100f, QueryFilter.All, out RayHit h);
        return hit && h.Body.Equals(body) ? h.Point.Z : float.NaN;
    }

    // Steps an avatar with `vel` for `steps`, tallying its contact reports (side A == avatarUd) by
    // phase, collecting the set of other-side UserDatas, and confirming side A is always the avatar
    // (Invalid BodyId). Accumulates into the ref params so it can be called across several walks.
    private static void CollectAvatarContacts(
        ILegionPhysicsBackend b, CharacterId c, uint avatarUd, Vector3 vel, int steps,
        BodyState[] bb, CharacterState[] cc, ContactReport[] ct,
        System.Collections.Generic.HashSet<uint> seen,
        ref int begin, ref int persist, ref int end, ref bool sideAok)
    {
        for (int i = 0; i < steps; i++)
        {
            b.SetCharacterMovement(c, vel, false, false);
            StepResult r = b.Step(1f / 60f, bb, cc, ct);
            for (int k = 0; k < r.ContactCount; k++)
            {
                ContactReport rep = ct[k];
                if (rep.UserDataA != avatarUd) continue; // avatar-side reports carry the avatar UserData on side A
                if (rep.BodyA.IsValid) sideAok = false;   // the avatar is not a body -> BodyA must be Invalid
                switch (rep.Phase)
                {
                    case ContactPhase.Begin: begin++; break;
                    case ContactPhase.Persist: persist++; break;
                    default: end++; break;
                }
                seen.Add(rep.UserDataB);
            }
        }
    }

    // Spawns a stationary avatar (WantsContactEvents = wants), lets it settle, and counts its terrain
    // Persist reports over 100 steps. The real #4 gate test: gated -> 0, ungated -> floods.
    private static int AvatarRestPersist(ILegionPhysicsBackend b, bool wants)
    {
        var cd = CharacterDesc.Default;
        cd.Position = new Vector3(25f, 25f, 0.75f);
        cd.UserData = wants ? 9101u : 9100u;
        cd.WantsContactEvents = wants;
        CharacterId c = b.CreateCharacter(cd);
        var bb = new BodyState[8]; var cc = new CharacterState[2]; var ct = new ContactReport[32];
        for (int i = 0; i < 40; i++) { b.SetCharacterMovement(c, Vector3.Zero, false, false); b.Step(1f / 60f, bb, cc, ct); }
        int persist = 0;
        for (int i = 0; i < 100; i++)
        {
            b.SetCharacterMovement(c, Vector3.Zero, false, false);
            StepResult r = b.Step(1f / 60f, bb, cc, ct);
            for (int k = 0; k < r.ContactCount; k++)
                if (ct[k].UserDataA == cd.UserData && ct[k].Phase == ContactPhase.Persist) persist++;
        }
        b.RemoveCharacter(c);
        return persist;
    }

    private static void RunCharacterWalk(ILegionPhysicsBackend b, System.Collections.Generic.List<Vector3> log)
    {
        ShapeId flat = b.CreateHeightFieldShape(new float[N * N], N, N, new Vector3(S, S, S));
        b.SetTerrain(flat, Vector3.Zero);
        var cd = CharacterDesc.Default;
        cd.Position = new Vector3(20f, 20f, 0.75f);
        CharacterId c = b.CreateCharacter(cd);
        var bb = new BodyState[4]; var cc = new CharacterState[2]; var ct = new ContactReport[8];
        for (int i = 0; i < 120; i++)
        {
            b.SetCharacterMovement(c, new Vector3(2f, 0.5f, 0f), false, false);
            b.Step(1f / 60f, bb, cc, ct);
            b.TryGetCharacterState(c, out CharacterState s);
            log.Add(s.Position);
        }
        b.RemoveCharacter(c);
        b.ReleaseShape(flat);
    }

    private static void RunCharacterDeterminismCheck()
    {
        Console.WriteLine("\n[24] Determinism with a character (two identical DeterministicMode runs)");
        var settings = PhysicsBackendSettings.Default;
        settings.DeterministicMode = true;
        var logA = new System.Collections.Generic.List<Vector3>();
        var logB = new System.Collections.Generic.List<Vector3>();

        var a = new JoltPhysicsBackend(); a.Initialize(settings);
        try { RunCharacterWalk(a, logA); } finally { a.Dispose(); }
        var b = new JoltPhysicsBackend(); b.Initialize(settings);
        try { RunCharacterWalk(b, logB); } finally { b.Dispose(); }

        Check(logA.Count > 0 && logA.Count == logB.Count, $"same character state count (A={logA.Count} B={logB.Count})");
        bool identical = logA.Count == logB.Count;
        int firstDiff = -1;
        for (int i = 0; identical && i < logA.Count; i++)
            if (logA[i] != logB[i]) { identical = false; firstDiff = i; }
        Check(identical, identical
            ? $"all {logA.Count} character transforms bit-identical across runs"
            : $"DIVERGED at state {firstDiff}: {logA[firstDiff]} vs {logB[firstDiff]}");
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
