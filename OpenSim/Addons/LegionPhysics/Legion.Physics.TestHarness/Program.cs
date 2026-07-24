// Legion.Physics.TestHarness - M1 Task 2 SMOKE TEST
//
// Purpose: prove the JoltPhysicsSharp 2.18.6 binding loads its native library on
// win-x64 and that a PhysicsSystem can be initialized, stepped once, and torn down
// cleanly. This is a binding/native-load smoke test ONLY - it implements no
// ILegionPhysicsBackend member. The real milestone-1 harness (heightfield + static
// box + raycast) replaces this file at Task 5.
//
// Decision #2 is closed on SINGLE precision: Foundation.Init(false) selects joltc.dll
// (the double variant, joltc_double.dll, would be Init(true)). This is the precision
// selector in 2.18.6 - not a build flag, not a separate package.

using System;
using System.Numerics;
using JoltPhysicsSharp;

namespace Legion.Physics.TestHarness
{
    internal static class Program
    {
        private static int Main()
        {
            Console.WriteLine("[smoke] JoltPhysicsSharp 2.18.6 init/step/dispose smoke test (M1 Task 2)");

            if (!Foundation.Init(false)) // false => single precision (joltc.dll)
            {
                Console.WriteLine("[smoke] FAIL: Foundation.Init(false) returned false");
                return 1;
            }
            Console.WriteLine("[smoke] Foundation.Init(false) ok - single precision (joltc.dll)");

            PhysicsSystem? system = null;
            JobSystemThreadPool? jobs = null;
            ObjectLayerPairFilterTable? objFilter = null;
            BroadPhaseLayerInterfaceTable? bpInterface = null;
            ObjectVsBroadPhaseLayerFilterTable? objVsBp = null;

            try
            {
                // Minimal 1-object-layer / 1-broadphase-layer filter set. The real M1
                // layer/broad-phase matrix is Task 3; here we only need a VALID settings
                // object so the native PhysicsSystem::Init does not fault.
                objFilter = new ObjectLayerPairFilterTable(1);
                objFilter.EnableCollision(0u, 0u);

                bpInterface = new BroadPhaseLayerInterfaceTable(1, 1);
                bpInterface.MapObjectToBroadPhaseLayer(0u, (BroadPhaseLayer)(byte)0);

                objVsBp = new ObjectVsBroadPhaseLayerFilterTable(bpInterface, 1, objFilter, 1);

                var settings = new PhysicsSystemSettings
                {
                    MaxBodies = 1024,
                    MaxBodyPairs = 1024,
                    MaxContactConstraints = 1024,
                    ObjectLayerPairFilter = objFilter,
                    BroadPhaseLayerInterface = bpInterface,
                    ObjectVsBroadPhaseLayerFilter = objVsBp,
                };

                system = new PhysicsSystem(settings);
                system.Gravity = new Vector3(0f, 0f, -9.80665f);
                Console.WriteLine(
                    $"[smoke] PhysicsSystem constructed. MaxBodies={system.MaxBodies}, Gravity={system.Gravity}");

                // One empty step through the real native update path (proves the job
                // system + native step resolve). No bodies, so this is trivial work.
                jobs = new JobSystemThreadPool();
                PhysicsUpdateError err = system.Update(1f / 60f, 1, jobs);
                uint active = system.GetNumActiveBodies(BodyType.Rigid);
                Console.WriteLine($"[smoke] Update() -> {err}; active rigid bodies = {active}");

                if (err != PhysicsUpdateError.None)
                {
                    Console.WriteLine("[smoke] FAIL: Update reported error");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[smoke] FAIL: threw {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                // Teardown: the PhysicsSystem holds the filter interfaces for its lifetime,
                // so dispose the system FIRST, then the job system and filters, then shut
                // the foundation down.
                system?.Dispose();
                jobs?.Dispose();
                objVsBp?.Dispose();
                bpInterface?.Dispose();
                objFilter?.Dispose();
                Foundation.Shutdown();
            }

            Console.WriteLine("[smoke] PASS: native loaded, init + step + dispose clean, no throw. Exit 0.");
            return 0;
        }
    }
}
