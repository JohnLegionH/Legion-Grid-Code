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

            // cleanup
            backend.RemoveBody(boxBody);
            backend.ReleaseShape(box);
            backend.ReleaseShape(flat);
            backend.ReleaseShape(raised);
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

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "=== M1 HARNESS: PASS ==="
            : $"=== M1 HARNESS: FAIL ({_fails} failed check(s)) ===");
        return _fails == 0 ? 0 : 1;
    }
}
