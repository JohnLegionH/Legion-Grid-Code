// Legion Grid - Jolt implementation of ILegionPhysicsBackend
//
// ============================ READ THIS FIRST ============================
// The ILegionPhysicsBackend interface is the deliverable. THIS is the real
// backend. As of M1 Task 3 only the LIFECYCLE + LAYER/BROAD-PHASE wiring is
// live (Initialize / Dispose / the filter tables); every other member is still
// a NotImplementedException stub, exactly as scoped. The design sketch lives at
// docs/physics/JoltPhysicsBackend.cs and stays there as the mapping reference.
//
// Jolt binding: JoltPhysicsSharp 2.18.6 (newest still shipping lib/net8.0/),
// single precision (Foundation.Init(false) -> joltc.dll). The Jolt calls below
// are the REAL 2.18.6 surface, verified by reflection against the shipped
// assembly - not the sketch's "shapes to verify". See the MILESTONE1 notes for
// the reference-vs-real API deltas.
//
// The parts worth reading carefully are the ones that are easy to get wrong and
// expensive to discover later:
//   - broad phase / object layer filtering  (BroadPhase region + Initialize)
//   - DontActivate on insert                (CreateBody - Task 4+)
//   - ScaledShape for prim resize           (CreateScaledShape - later)
//   - contact ring buffer                   (LegionContactListener - later)
//   - CharacterVirtual stepping order       (Step - Task 4)
// =========================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using JoltPhysicsSharp;

namespace Legion.Physics.Jolt
{
    public sealed class JoltPhysicsBackend : ILegionPhysicsBackend
    {
        public string Name => "Jolt";
        public string Version => "5.x";

        private PhysicsBackendSettings _settings;
        private readonly Stopwatch _stepTimer = new Stopwatch();

        // Handle tables. Jolt hands back its own ids; we keep our own dense
        // tables so a stale Legion handle can never index live Jolt memory.
        private readonly HandleTable<JoltBodyRecord> _bodies = new HandleTable<JoltBodyRecord>();
        private readonly HandleTable<JoltShapeRecord> _shapes = new HandleTable<JoltShapeRecord>();
        private readonly HandleTable<JoltCharacterRecord> _characters = new HandleTable<JoltCharacterRecord>();
        private readonly HandleTable<JoltConstraintRecord> _constraints = new HandleTable<JoltConstraintRecord>();

        private LegionContactListener _contactListener = null!;

        // Native Jolt handles. Nullable + disposed in Dispose() in strict reverse
        // order (delta #6): the PhysicsSystem retains the filter interfaces and the
        // job system for its lifetime, so the system MUST be torn down first.
        private PhysicsSystem? _system;
        private JobSystemThreadPool? _jobSystem;
        private ObjectLayerPairFilterTable? _objectLayerPairFilter;
        private BroadPhaseLayerInterfaceTable? _broadPhaseInterface;
        private ObjectVsBroadPhaseLayerFilterTable? _objectVsBroadPhaseFilter;
        // NOTE (delta #4): 2.18.6 has NO TempAllocator - temp allocation is internal
        // to PhysicsSystem.Update. There is deliberately no _tempAllocator field.

        // Cached LOCKING BodyInterface - safe to call from any thread. This is what lets Legion
        // drop the taint-queue pattern (Create/Remove/Set* run straight from the scene thread).
        // Valid for the PhysicsSystem's lifetime.
        private BodyInterface _bodyInterface;

        // --- Active-body tracking (Task 4; delta #8 mechanism) ---
        // OnBodyActivated/OnBodyDeactivated fire from Jolt WORKER threads during Update(), and
        // activation can also flip from the SCENE thread (SetBodyTransform activate:true - no
        // taint queue). A plain shared HashSet would tear. So the event handlers only ENQUEUE;
        // the HashSet is owned SOLELY by the Step thread. Zero cross-thread set mutation; zero
        // per-frame allocation (the scratch collections are Clear()ed and refilled, not realloc'd;
        // foreach over a concrete HashSet/List uses a struct enumerator).
        private readonly ConcurrentQueue<ActivationDelta> _activationQueue = new ConcurrentQueue<ActivationDelta>();
        private readonly HashSet<uint> _activeBodies = new HashSet<uint>();   // step-thread only
        private readonly HashSet<uint> _justActivated = new HashSet<uint>();  // scratch, per-step
        private readonly List<uint> _justDeactivated = new List<uint>();      // scratch, per-step
        private readonly List<uint> _staleActive = new List<uint>();          // scratch, per-step

        // Reverse map: Jolt BodyID.ID -> our record. Written on Create/Remove (scene thread),
        // read from Step and from the contact/activation callbacks (worker threads).
        private readonly ConcurrentDictionary<uint, JoltBodyRecord> _joltToRecord =
            new ConcurrentDictionary<uint, JoltBodyRecord>();

        // Current terrain body (SetTerrain replaces it). BodyId.Invalid = none.
        private BodyId _terrainBody = BodyId.Invalid;

        // Box convex radius is clamped to min(this, 0.1 * smallest half-extent) so Jolt never
        // asserts "convex radius larger than shape".
        private const float DefaultConvexRadius = 0.05f;

        private readonly struct ActivationDelta
        {
            public readonly uint BodyId;
            public readonly bool Activated;
            public ActivationDelta(uint bodyId, bool activated) { BodyId = bodyId; Activated = activated; }
        }

        // Number of ObjectLayers = number of PhysicsLayer members. Derived from the
        // enum so the filter tables never silently drift if a layer is added.
        private static readonly uint ObjectLayerCount = (uint)Enum.GetValues(typeof(PhysicsLayer)).Length;

        // =====================================================================
        // Broad phase / object layers
        //
        // Jolt has two filtering tiers and conflating them is the classic
        // first-integration mistake:
        //
        //   ObjectLayer     - fine grained, per body, decides "can A and B ever
        //                     collide". This is our PhysicsLayer, 1:1.
        //   BroadPhaseLayer - coarse buckets the AABB tree is partitioned by.
        //                     Keep this to 3. More buckets = more tree walks.
        //
        // The win from getting this right: NON_MOVING is a separate tree that is
        // never rebuilt during normal operation. A region with 50k static prims
        // and 200 physical ones only ever re-walks the small tree.
        // =====================================================================
        private static class BroadPhase
        {
            public const byte NonMoving = 0;  // Terrain, Static
            public const byte Moving = 1;     // Dynamic, Avatar, Debris
            public const byte Sensor = 2;     // Sensor
            public const uint Count = 3;
        }

        private static byte ToBroadPhase(PhysicsLayer layer) => layer switch
        {
            PhysicsLayer.Terrain => BroadPhase.NonMoving,
            PhysicsLayer.Static => BroadPhase.NonMoving,
            PhysicsLayer.Dynamic => BroadPhase.Moving,
            PhysicsLayer.Avatar => BroadPhase.Moving,
            PhysicsLayer.Debris => BroadPhase.Moving,
            PhysicsLayer.Sensor => BroadPhase.Sensor,
            _ => BroadPhase.Moving,
        };

        /// <summary>
        /// The collision matrix. This table IS the behaviour contract - most
        /// "why does my object fall through X" bugs are a wrong cell here.
        /// Terrain/Static never test against each other: that alone removes the
        /// dominant pair count in a built-up region.
        /// </summary>
        private static bool ShouldCollide(PhysicsLayer a, PhysicsLayer b)
        {
            // Normalise so we only fill the lower triangle.
            if (a > b) (a, b) = (b, a);

            return (a, b) switch
            {
                (PhysicsLayer.Terrain, PhysicsLayer.Terrain) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Static) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Terrain, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Terrain, PhysicsLayer.Sensor) => false,
                (PhysicsLayer.Terrain, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Static, PhysicsLayer.Static) => false,
                (PhysicsLayer.Static, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Static, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Static, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Static, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Dynamic, PhysicsLayer.Dynamic) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Dynamic, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Avatar, PhysicsLayer.Avatar) => true,
                (PhysicsLayer.Avatar, PhysicsLayer.Sensor) => true,
                (PhysicsLayer.Avatar, PhysicsLayer.Debris) => true,

                (PhysicsLayer.Sensor, PhysicsLayer.Sensor) => false,
                (PhysicsLayer.Sensor, PhysicsLayer.Debris) => false,

                // Debris vs Debris deliberately off - that is the whole point
                // of the tier. Turning it on silently reintroduces the O(n^2).
                (PhysicsLayer.Debris, PhysicsLayer.Debris) => false,

                _ => true,
            };
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Initialize(in PhysicsBackendSettings settings)
        {
            _settings = settings;

            int threads = settings.ThreadCount > 0
                ? settings.ThreadCount
                : Math.Max(1, Environment.ProcessorCount - 1);

            if (settings.DeterministicMode)
                threads = 1;

            // Native boot. false => single precision (joltc.dll), decision #2 closed.
            // Foundation.Init is idempotent-safe to pair with Foundation.Shutdown in Dispose.
            if (!Foundation.Init(false))
                throw new InvalidOperationException("Jolt Foundation.Init(false) failed (native joltc.dll not loaded).");

            // --- Object-layer collision matrix (delta #3) ---
            // ObjectLayerPairFilterTable starts with EVERY pair disabled; we turn on
            // exactly the ShouldCollide cells. Driving the table from ShouldCollide (rather
            // than hand-listing pairs) keeps the matrix the single source of truth AND
            // guarantees every one of the 21 unordered pairs is decided explicitly.
            // EnableCollision is symmetric, so we only walk the lower triangle (a <= b).
            _objectLayerPairFilter = new ObjectLayerPairFilterTable(ObjectLayerCount);
            for (uint a = 0; a < ObjectLayerCount; a++)
            {
                for (uint b = a; b < ObjectLayerCount; b++)
                {
                    if (ShouldCollide((PhysicsLayer)a, (PhysicsLayer)b))
                        _objectLayerPairFilter.EnableCollision(new ObjectLayer(a), new ObjectLayer(b));
                }
            }

            // --- ObjectLayer -> BroadPhaseLayer map (delta #3) ---
            _broadPhaseInterface = new BroadPhaseLayerInterfaceTable(ObjectLayerCount, BroadPhase.Count);
            for (uint a = 0; a < ObjectLayerCount; a++)
            {
                _broadPhaseInterface.MapObjectToBroadPhaseLayer(
                    new ObjectLayer(a),
                    new BroadPhaseLayer(ToBroadPhase((PhysicsLayer)a)));
            }

            // --- Object-vs-broadphase filter (delta #3: the third table the sketch omitted) ---
            // Built FROM the two tables above; it answers "can an object in layer X ever
            // touch broad-phase bucket Y" and is what actually prunes tree walks.
            _objectVsBroadPhaseFilter = new ObjectVsBroadPhaseLayerFilterTable(
                _broadPhaseInterface, BroadPhase.Count, _objectLayerPairFilter, ObjectLayerCount);

            var systemSettings = new PhysicsSystemSettings
            {
                MaxBodies = settings.MaxBodies,
                MaxBodyPairs = settings.MaxBodyPairs,
                MaxContactConstraints = settings.MaxContactConstraints,
                ObjectLayerPairFilter = _objectLayerPairFilter,
                BroadPhaseLayerInterface = _broadPhaseInterface,
                ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhaseFilter,
            };

            _system = new PhysicsSystem(systemSettings);
            _system.Gravity = settings.Gravity;
            _bodyInterface = _system.BodyInterface;

            // Determinism (A/B parity harness, DESIGN.md): single-threaded ALONE is not enough - Jolt
            // also needs its DeterministicSimulation flag on to guarantee bit-identical re-runs. It
            // defaults true in 2.18.6, but we set it EXPLICITLY when asked rather than lean on a default
            // that a future lib bump could flip. (Left untouched otherwise, to keep the fast path fast.)
            if (settings.DeterministicMode)
            {
                PhysicsSettings physicsSettings = _system.Settings;
                physicsSettings.DeterministicSimulation = true;
                _system.Settings = physicsSettings;
            }

            // Contacts + body activation arrive as C# EVENTS in 2.18.6 (delta #7), not a
            // listener object. The handlers ONLY enqueue / push into the ring - they never touch
            // scene state, never allocate, and never mutate the active set (see the field notes).
            _system.OnBodyActivated += HandleBodyActivated;
            _system.OnBodyDeactivated += HandleBodyDeactivated;
            _system.OnContactAdded += HandleContactAdded;
            _system.OnContactPersisted += HandleContactPersisted;
            _system.OnContactRemoved += HandleContactRemoved;

            // Worker pool (delta #4: Update takes this JobSystem; no TempAllocator).
            // Jolt's canonical limits: 2048 jobs, 8 barriers. DeterministicMode / an
            // explicit ThreadCount collapse the pool to a single worker.
            var jobConfig = new JobSystemThreadPoolConfig
            {
                maxJobs = 2048,
                maxBarriers = 8,
                numThreads = threads,
            };
            _jobSystem = new JobSystemThreadPool(jobConfig);

            // Contact ring is allocated now; it is engine-agnostic. Wiring it to Jolt is
            // deferred: 2.18.6 exposes contacts as EVENTS on PhysicsSystem
            // (OnContactAdded/Persisted/Removed), NOT a SetContactListener object as the
            // sketch assumed (delta #7). Likewise the Task 4 active-body drain will subscribe
            // OnBodyActivated / OnBodyDeactivated to keep an O(active) set.
            _contactListener = new LegionContactListener(_settings.MaxContactConstraints * 2);
        }

        public void Dispose()
        {
            // Legion-side handle tables first (pure managed bookkeeping).
            _constraints.Clear();
            _characters.Clear();
            _bodies.Clear();
            _shapes.Clear();
            _joltToRecord.Clear();
            _activeBodies.Clear();
            while (_activationQueue.TryDequeue(out _)) { }

            // Unsubscribe before teardown so no worker-thread callback fires into a half-disposed
            // backend during the final Update-drain window.
            if (_system != null)
            {
                _system.OnBodyActivated -= HandleBodyActivated;
                _system.OnBodyDeactivated -= HandleBodyDeactivated;
                _system.OnContactAdded -= HandleContactAdded;
                _system.OnContactPersisted -= HandleContactPersisted;
                _system.OnContactRemoved -= HandleContactRemoved;
            }

            // Native teardown order (delta #6): system -> jobs -> filters -> Foundation.
            // The PhysicsSystem holds the filter interfaces and steps on the job system,
            // so it must go down first.
            _system?.Dispose();
            _system = null;

            _jobSystem?.Dispose();
            _jobSystem = null;

            _objectVsBroadPhaseFilter?.Dispose();
            _objectVsBroadPhaseFilter = null;
            _broadPhaseInterface?.Dispose();
            _broadPhaseInterface = null;
            _objectLayerPairFilter?.Dispose();
            _objectLayerPairFilter = null;

            Foundation.Shutdown();
        }

        // =====================================================================
        // Jolt event callbacks (delta #7). WORKER-THREAD context: enqueue / push only.
        // No allocation, no scene-state access, no mutation of _activeBodies.
        // =====================================================================

        private void HandleBodyActivated(PhysicsSystem system, in BodyID bodyID, ulong bodyUserData)
            => _activationQueue.Enqueue(new ActivationDelta(bodyID.ID, true));

        private void HandleBodyDeactivated(PhysicsSystem system, in BodyID bodyID, ulong bodyUserData)
            => _activationQueue.Enqueue(new ActivationDelta(bodyID.ID, false));

        private void HandleContactAdded(
            PhysicsSystem system, in Body body1, in Body body2,
            in ContactManifold manifold, ref ContactSettings settings)
            => PushContact(body1.ID.ID, body2.ID.ID, in manifold, ContactPhase.Begin);

        private void HandleContactPersisted(
            PhysicsSystem system, in Body body1, in Body body2,
            in ContactManifold manifold, ref ContactSettings settings)
            => PushContact(body1.ID.ID, body2.ID.ID, in manifold, ContactPhase.Persist);

        private void HandleContactRemoved(PhysicsSystem system, ref SubShapeIDPair pair)
            => _contactListener.Push(BuildContact(
                pair.Body1ID.ID, pair.Body2ID.ID, default, default, ContactPhase.End));

        private void PushContact(uint joltA, uint joltB, in ContactManifold manifold, ContactPhase phase)
        {
            // Impulse is a post-solve quantity; Added/Persisted fire pre-solve, so it is not
            // available here (left 0). Point/normal come straight off the manifold.
            Vector3 point = manifold.PointCount > 0 ? manifold.GetWorldSpaceContactPointOn1(0) : default;
            _contactListener.Push(BuildContact(joltA, joltB, point, manifold.WorldSpaceNormal, phase));
        }

        private ContactReport BuildContact(
            uint joltA, uint joltB, Vector3 point, Vector3 normal, ContactPhase phase)
        {
            _joltToRecord.TryGetValue(joltA, out JoltBodyRecord? ra);
            _joltToRecord.TryGetValue(joltB, out JoltBodyRecord? rb);
            return new ContactReport
            {
                BodyA = ra != null ? new BodyId(ra.Handle) : BodyId.Invalid,
                BodyB = rb != null ? new BodyId(rb.Handle) : BodyId.Invalid,
                UserDataA = ra != null ? ra.UserData : 0u,
                UserDataB = rb != null ? rb.UserData : 0u,
                Point = point,
                Normal = normal,
                Impulse = 0f,
                Phase = phase,
            };
        }

        // =====================================================================
        // Shapes
        // =====================================================================

        public ShapeId CreateBoxShape(Vector3 halfExtents)
        {
            float minHalf = MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z));
            float convexRadius = MathF.Max(0f, MathF.Min(DefaultConvexRadius, minHalf * 0.1f));
            var shape = new BoxShape(halfExtents, convexRadius);
            return RegisterShape(shape);
        }

        public ShapeId CreateSphereShape(float radius) => throw new NotImplementedException();
        public ShapeId CreateCapsuleShape(float halfHeight, float radius) => throw new NotImplementedException();
        public ShapeId CreateCylinderShape(float halfHeight, float radius) => throw new NotImplementedException();
        public ShapeId CreateConvexHullShape(ReadOnlySpan<Vector3> points) => throw new NotImplementedException();

        public ShapeId CreateMeshShape(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
        {
            // Cook once per ASSET, never per prim. Key the cache on the mesh
            // asset UUID plus LOD, and hand the same ShapeId to every prim that
            // references it. Cooking is the single most expensive operation here
            // and re-cooking per prim is how region startup gets slow.
            throw new NotImplementedException();
        }

        public ShapeId CreateCompoundShape(ReadOnlySpan<CompoundChild> children)
        {
            // Linksets. Prefer StaticCompoundShape when the linkset is rigid -
            // it builds a small internal tree and is markedly faster to query
            // than MutableCompoundShape. Use the mutable variant only where
            // link/unlink happens at runtime.
            throw new NotImplementedException();
        }

        public ShapeId CreateHeightFieldShape(
            ReadOnlySpan<float> heights, int sampleCountX, int sampleCountY, Vector3 scale)
        {
            // Jolt HeightFieldShape is SQUARE (one sample count) and Y-UP: a sample at grid
            // (col,row) sits at scale * (col, height, row) - the height axis is Jolt's Y and the
            // grid spans X and Z. Legion's world is Z-up (gravity -Z), so we HIDE the Jolt quirk
            // inside this method (nothing above ILegionPhysicsBackend knows Jolt exists): cook the
            // Y-up field, then wrap it in a RotatedTranslatedShape and return the WRAPPER's handle,
            // which is already Z-up-correct and self-consistent for any caller/query. See below.
            //
            // Sample-count constraint (verified empirically vs joltc 2.18.6 - see MILESTONE1
            // notes): the managed HeightFieldShapeSettings exposes NO block-size / bits-per-sample
            // setter, so the native default block size is used. joltc is a RELEASE build with
            // Jolt's asserts compiled out, so a bad count does NOT throw - it silently mis-cooks
            // (n>=3 incl. odd/non-PoT all return a non-null shape; only n<3 fails). We therefore
            // require a power-of-two count (>= 4): that is exactly what OpenSim terrain produces
            // (256) and is a safe multiple of any power-of-two block size. Loosening this for odd
            // varregion tile sizes needs a geometry-correctness test, not just a non-null Create -
            // input to the still-open varregion-tiling decision.
            if (sampleCountX != sampleCountY)
                throw new ArgumentException(
                    $"Jolt HeightFieldShape is square; got {sampleCountX}x{sampleCountY}. " +
                    "Non-square regions need padding/tiling (open varregion decision).");
            int n = sampleCountX;
            if (n < 4 || (n & (n - 1)) != 0)
                throw new ArgumentException($"HeightFieldShape sample count must be a power of two >= 4; got {n}.");
            if (heights.Length < n * n)
                throw new ArgumentException($"height buffer too small: need {n * n} samples, got {heights.Length}.");

            // The caller's `scale` is in Legion Z-up terms: (X spacing, Y spacing, height scale).
            // Jolt wants (X spacing, HEIGHT scale, Z spacing), so swap Y<->Z going in.
            Vector3 joltScale = new Vector3(scale.X, scale.Z, scale.Y);

            // Convention: heights[y*N + x] is the height at grid (x, y), and must land at world
            // (x, y). The RotatedTranslatedShape wrapper (below) maps Jolt grid-row r to world
            // Y = (N-1-r) - a north-south flip - so we ROW-REVERSE going in (input row y -> Jolt
            // row N-1-y) to cancel it. X is untouched (no X mirror). Verified by the harness's
            // asymmetric per-quadrant check. (settings copies into native storage, so this cook-time
            // temp array is fine - once per terrain asset, not per frame.)
            float[] samples = new float[n * n];
            for (int jy = 0; jy < n; jy++)
                heights.Slice((n - 1 - jy) * n, n).CopyTo(samples.AsSpan(jy * n, n));
            Vector3 offset = Vector3.Zero;
            Shape inner;
            var hfSettings = new HeightFieldShapeSettings(samples, offset, joltScale, n);
            try { inner = hfSettings.Create(); }
            finally { hfSettings.Dispose(); }

            try
            {
                // R_x(+90) sends Jolt's +Y (height) to world +Z (up). A proper rotation can't also
                // keep the row axis on +Y (that swap is a reflection), so it lands on -Y; the
                // (N-1)*Yspacing translation lifts the field back into the +Y quadrant. Net: the
                // shape, placed at the origin, occupies X in [0,(N-1)*sx], Y in [0,(N-1)*sy], with
                // height along +Z. The -Y row flip this introduces is cancelled by the row-reverse
                // when building `samples` above, so input (x,y) lands at world (x,y) - no mirror.
                Vector3 posW = new Vector3(0f, (n - 1) * scale.Y, 0f);
                Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

                Shape wrapper;
                using (var wrapSettings = new RotatedTranslatedShapeSettings(posW, rot, inner))
                    wrapper = wrapSettings.Create();

                // The wrapper OWNS the inner shape (private, not caller-visible): both are disposed
                // together when this handle's RefCount hits 0.
                var rec = new JoltShapeRecord
                {
                    NativeShape = wrapper,
                    InnerShape = inner,
                    RefCount = 1,
                    IsWrapper = true,
                };
                return new ShapeId(_shapes.Add(rec));
            }
            catch
            {
                inner.Dispose();
                throw;
            }
        }

        public ShapeId CreateScaledShape(ShapeId baseShape, Vector3 scale)
        {
            // The whole reason this is on the interface. Prim resize wraps the
            // cooked shape in a ScaledShape - cheap, shares the underlying
            // geometry, no re-cook. Non-uniform scale is supported for convex
            // and mesh shapes but NOT for spheres/capsules; the layer above must
            // degrade those to something else or clamp to uniform.
            throw new NotImplementedException();
        }

        public void AddShapeRef(ShapeId shape)
        {
            if (_shapes.TryGet(shape.Value, out JoltShapeRecord rec))
                Interlocked.Increment(ref rec.RefCount);
        }

        public void ReleaseShape(ShapeId shape)
        {
            if (!_shapes.TryGet(shape.Value, out JoltShapeRecord rec))
                return;
            if (Interlocked.Decrement(ref rec.RefCount) <= 0)
            {
                // Last Legion reference gone. Dispose our managed Shape wrapper (releases one
                // native ref). Any Body still using the shape holds its OWN native ref, so the
                // native RefTarget survives until that body is destroyed - no premature free.
                // A wrapper also owns its private inner shape (heightfield under the Z-up wrapper),
                // so dispose that too.
                rec.NativeShape?.Dispose();
                rec.NativeShape = null;
                rec.InnerShape?.Dispose();
                rec.InnerShape = null;
                _shapes.Remove(shape.Value);
            }
        }

        // Registers a freshly-created Jolt shape, RefCount = 1 (the creator's reference).
        private ShapeId RegisterShape(Shape shape)
        {
            var rec = new JoltShapeRecord { NativeShape = shape, RefCount = 1 };
            return new ShapeId(_shapes.Add(rec));
        }

        // =====================================================================
        // Bodies
        // =====================================================================

        public BodyId CreateBody(in BodyDesc desc)
        {
            if (_system == null)
                throw new InvalidOperationException("CreateBody before Initialize.");
            if (!_shapes.TryGet(desc.Shape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                throw new ArgumentException($"CreateBody: {desc.Shape} is not a live shape handle.");

            MotionType joltMotion = ToJoltMotion(desc.MotionType);
            bool movable = desc.MotionType != BodyMotionType.Static;

            var objectLayer = new ObjectLayer((uint)desc.Layer);
            var bcs = new BodyCreationSettings(
                shapeRec.NativeShape, desc.Position, desc.Orientation, joltMotion, objectLayer);
            float mass = 0f;
            try
            {
                bcs.Friction = desc.Friction;
                bcs.Restitution = desc.Restitution;
                bcs.IsSensor = desc.IsSensor;
                bcs.UserData = desc.UserData;

                if (movable)
                {
                    // Velocities, damping, gravity factor and CCD only mean anything for a body that
                    // actually moves; a Static body has no MotionProperties to hold them.
                    bcs.LinearVelocity = desc.LinearVelocity;
                    bcs.AngularVelocity = desc.AngularVelocity;
                    bcs.LinearDamping = MathF.Max(0f, desc.LinearDamping);
                    bcs.AngularDamping = MathF.Max(0f, desc.AngularDamping);
                    bcs.GravityFactor = desc.GravityFactor;
                    bcs.MotionQuality = desc.UseCcd ? MotionQuality.LinearCast : MotionQuality.Discrete;

                    // Let this body flip Dynamic<->Kinematic<->Static later (SetBodyMotionType). A body
                    // created Static deliberately does NOT get this: allocating MotionProperties for
                    // every one of a region's tens of thousands of non-physical prims is exactly the
                    // memory regression DESIGN.md's DontActivate note guards against. A prim that can
                    // go physical must therefore be CREATED movable, not created static and promoted.
                    bcs.AllowDynamicOrKinematic = true;
                }

                if (desc.MotionType == BodyMotionType.Dynamic)
                {
                    // Mass policy (DESIGN.md / BodyDesc): explicit Mass wins; else shape volume x
                    // Density. We ALWAYS override rather than trust the shape's baked density, because
                    // shapes are shared/refcounted across prims and carry Jolt's default 1000 kg/m^3 -
                    // the per-body Density lives in BodyDesc, not the shape. CalculateInertia keeps the
                    // inertia TENSOR derived from the real geometry, scaled to this mass (verified
                    // exact: asked 42 -> body mass 42.0000).
                    mass = ComputeMass(shapeRec, desc);
                    bcs.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
                    bcs.MassPropertiesOverride = new MassProperties { Mass = mass };
                }

                // The load-bearing line (DESIGN.md): do NOT wake on insert unless asked. A region
                // rezzing tens of thousands of prims with Activate is a pathological startup stall.
                Activation activation = desc.StartActive ? Activation.Activate : Activation.DontActivate;
                BodyID joltId = _bodyInterface.CreateAndAddBody(bcs, activation);

                var rec = new JoltBodyRecord
                {
                    NativeBodyId = joltId.ID,
                    Shape = desc.Shape,
                    Layer = desc.Layer,
                    MotionType = desc.MotionType,
                    UserData = desc.UserData,
                    WantsContactEvents = false,
                    Mass = mass,
                    AllowMotionChange = movable,
                };
                uint handle = _bodies.Add(rec);
                rec.Handle = handle;
                _joltToRecord[joltId.ID] = rec;
                return new BodyId(handle);
            }
            finally
            {
                // CreateAndAddBody copies the settings; the managed settings object is ours to free.
                bcs.Dispose();
            }
        }

        private static MotionType ToJoltMotion(BodyMotionType t) => t switch
        {
            BodyMotionType.Static => MotionType.Static,
            BodyMotionType.Kinematic => MotionType.Kinematic,
            BodyMotionType.Dynamic => MotionType.Dynamic,
            _ => MotionType.Static,
        };

        // Explicit mass wins; otherwise shape volume x density. Clamped to a small positive so a
        // degenerate (zero-volume) shape can never yield a zero/negative-mass dynamic body, whose
        // inverse mass would be infinite acceleration.
        private static float ComputeMass(JoltShapeRecord shapeRec, in BodyDesc desc)
        {
            if (desc.Mass > 0f)
                return desc.Mass;
            float volume = shapeRec.NativeShape != null ? shapeRec.NativeShape.Volume : 0f;
            float density = desc.Density > 0f ? desc.Density : 1000f;
            return MathF.Max(volume * density, 1e-3f);
        }

        // Body-handle -> live Jolt id. Returns false (idempotent no-op for callers) on a stale/invalid
        // handle, matching RemoveBody's contract.
        private bool TryResolve(BodyId body, out JoltBodyRecord rec, out BodyID jid)
        {
            if (_bodies.TryGet(body.Value, out rec))
            {
                jid = new BodyID(rec.NativeBodyId);
                return true;
            }
            jid = default;
            return false;
        }

        // Force/impulse resolution: only DYNAMIC bodies respond. Static bodies have no MotionProperties
        // (Add* would dereference null natively); kinematic bodies are script/animation-driven and
        // ignore forces. This mirrors SL, where llApplyImpulse et al. only affect physical objects.
        private bool TryResolveDynamic(BodyId body, out BodyID jid)
        {
            if (_bodies.TryGet(body.Value, out JoltBodyRecord rec) && rec.MotionType == BodyMotionType.Dynamic)
            {
                jid = new BodyID(rec.NativeBodyId);
                return true;
            }
            jid = default;
            return false;
        }

        public void RemoveBody(BodyId body)
        {
            if (!_bodies.TryGet(body.Value, out JoltBodyRecord rec))
                return; // stale/invalid handle - idempotent no-op.

            var joltId = new BodyID(rec.NativeBodyId);
            _bodyInterface.RemoveAndDestroyBody(joltId);
            _joltToRecord.TryRemove(rec.NativeBodyId, out _);
            _bodies.Remove(body.Value); // bumps the generation so the stale handle fails validation.
            // _activeBodies is step-thread-owned; if this body happened to be active, the stale
            // id is self-healed at the top of Step (it no longer resolves via _joltToRecord).
        }

        public bool IsBodyValid(BodyId body) => _bodies.IsValid(body.Value);

        public void SetBodyShape(BodyId body, ShapeId shape, bool recomputeMass) => throw new NotImplementedException();

        public void SetBodyMotionType(BodyId body, BodyMotionType motionType, bool activate)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            if (motionType != BodyMotionType.Static && !rec.AllowMotionChange)
                throw new InvalidOperationException(
                    "SetBodyMotionType to a movable type needs a body created Dynamic or Kinematic " +
                    "(a Static body has no MotionProperties to promote). Create it movable up front " +
                    "if it can ever go physical.");
            _bodyInterface.SetMotionType(jid, ToJoltMotion(motionType),
                activate ? Activation.Activate : Activation.DontActivate);
            rec.MotionType = motionType;
        }

        public void SetBodyLayer(BodyId body, PhysicsLayer layer) => throw new NotImplementedException();

        public void SetBodyTransform(BodyId body, Vector3 position, Quaternion orientation, bool activate) => throw new NotImplementedException();

        public void SetBodyLinearVelocity(BodyId body, Vector3 velocity)
        {
            // Thin seam: this does NOT wake a sleeping body (Jolt-native behaviour - only Apply*
            // impulses activate). A velocity set on a sleeping body takes effect only once something
            // else activates it; that activation policy belongs to the layer above, not here.
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetLinearVelocity(jid, velocity);
        }

        public void SetBodyAngularVelocity(BodyId body, Vector3 velocity)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetAngularVelocity(jid, velocity);
        }

        public void SetBodyMass(BodyId body, float mass)
        {
            if (mass <= 0f || !TryResolve(body, out JoltBodyRecord rec, out BodyID jid))
                return;
            rec.Mass = mass;
            if (rec.MotionType != BodyMotionType.Dynamic)
                return; // mass is inert for static/kinematic motion; recorded for a later flip to Dynamic.

            // No BodyInterface.SetMass in 2.18.6. Take the shape's geometry-correct mass properties,
            // scale them to the target mass (keeps the inertia tensor's SHAPE, changes only its
            // magnitude), and push them through a body write-lock.
            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                {
                    Body b = lockWrite.Body;
                    MassProperties mp = b.Shape.MassProperties;
                    mp.ScaleToMass(mass);
                    MotionProperties motion = b.MotionProperties;
                    motion.SetMassProperties(motion.AllowedDOFs, mp);
                }
            }
            finally { bli.UnlockWrite(lockWrite); }
        }

        public void SetBodyFriction(BodyId body, float friction)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetFriction(jid, friction);
        }

        public void SetBodyRestitution(BodyId body, float restitution)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.SetRestitution(jid, restitution);
        }

        public void SetBodyDamping(BodyId body, float linear, float angular)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType == BodyMotionType.Static)
                return; // no MotionProperties on a static body.

            BodyLockInterface bli = _system!.BodyLockInterface;
            bli.LockWrite(jid, out BodyLockWrite lockWrite);
            try
            {
                if (lockWrite.Succeeded)
                {
                    MotionProperties motion = lockWrite.Body.MotionProperties;
                    motion.LinearDamping = MathF.Max(0f, linear);
                    motion.AngularDamping = MathF.Max(0f, angular);
                }
            }
            finally { bli.UnlockWrite(lockWrite); }
        }

        public void SetBodyGravityFactor(BodyId body, float factor)
        {
            if (!TryResolve(body, out JoltBodyRecord rec, out BodyID jid) ||
                rec.MotionType == BodyMotionType.Static)
                return; // static bodies never feel gravity; SetGravityFactor would touch null motion props.
            _bodyInterface.SetGravityFactor(jid, factor);
        }

        public void SetBodyAxisLocks(BodyId body, Vector3 allowedTranslation, Vector3 allowedRotation)
        {
            // Jolt: SixDOFConstraint to world, or MotionProperties mass/inertia
            // scaling. The constraint route is more predictable; the inertia
            // route is cheaper. Start with the constraint and measure.
            throw new NotImplementedException();
        }

        // Apply* only act on DYNAMIC bodies (see TryResolveDynamic). All of these auto-activate a
        // sleeping body - AddForce and AddImpulse were both verified to wake it - which matches SL's
        // wake-on-impulse behaviour. AddForce/AddTorque accumulate and are consumed by the next Step;
        // AddImpulse/AddAngularImpulse change velocity instantly (delta v = impulse / mass).
        public void ApplyForce(BodyId body, Vector3 force)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddForce(jid, force);
        }

        public void ApplyTorque(BodyId body, Vector3 torque)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddTorque(jid, torque);
        }

        public void ApplyImpulse(BodyId body, Vector3 impulse)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddImpulse(jid, impulse);
        }

        public void ApplyImpulseAtPoint(BodyId body, Vector3 impulse, Vector3 worldPoint)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddImpulse(jid, impulse, worldPoint);
        }

        public void ApplyAngularImpulse(BodyId body, Vector3 angularImpulse)
        {
            if (TryResolveDynamic(body, out BodyID jid))
                _bodyInterface.AddAngularImpulse(jid, angularImpulse);
        }

        public void ApplyBuoyancy(
            BodyId body, float waterHeight, float buoyancy, float linearDrag, float angularDrag)
        {
            // Jolt: Body.ApplyBuoyancyImpulse(surfacePosition, surfaceNormal,
            //         buoyancy, linearDrag, angularDrag, fluidVelocity,
            //         gravity, deltaTime)
            // Must be called every step while submerged - it is an impulse, not
            // a persistent state. This should replace the hand-rolled lift in
            // the ported boat model.
            throw new NotImplementedException();
        }

        public void ActivateBody(BodyId body)
        {
            // Static bodies are never active; skip so we don't touch a body with no MotionProperties.
            if (TryResolve(body, out JoltBodyRecord rec, out BodyID jid) && rec.MotionType != BodyMotionType.Static)
                _bodyInterface.ActivateBody(jid);
        }

        public void DeactivateBody(BodyId body)
        {
            if (TryResolve(body, out _, out BodyID jid))
                _bodyInterface.DeactivateBody(jid);
        }

        public bool TryGetBodyState(BodyId body, out BodyState state)
        {
            if (!_bodies.TryGet(body.Value, out JoltBodyRecord rec))
            {
                state = default;
                return false;
            }
            var joltId = new BodyID(rec.NativeBodyId);
            state = new BodyState
            {
                Body = body,
                UserData = rec.UserData,
                Position = _bodyInterface.GetPosition(joltId),
                Orientation = _bodyInterface.GetRotation(joltId),
                LinearVelocity = _bodyInterface.GetLinearVelocity(joltId),
                AngularVelocity = _bodyInterface.GetAngularVelocity(joltId),
                Flags = _bodyInterface.IsActive(joltId) ? BodyStateFlags.Active : BodyStateFlags.None,
            };
            return true;
        }

        // =====================================================================
        // Characters
        // =====================================================================

        public CharacterId CreateCharacter(in CharacterDesc desc)
        {
            // Use CharacterVirtual, not Character.
            //
            // CharacterVirtual has no rigid body in the simulation and is
            // stepped OUTSIDE the physics update, which is exactly what an
            // avatar wants: the movement layer stays in control, and stair
            // stepping / slope handling / moving platform support come for free
            // rather than being reimplemented on top of a capsule.
            //
            // Cost: interaction with dynamic bodies is approximate. For pushing
            // physical prims around, pair it with an explicit push impulse in
            // the contact callback rather than expecting momentum transfer.
            throw new NotImplementedException();
        }

        public void RemoveCharacter(CharacterId character) => throw new NotImplementedException();
        public void SetCharacterTransform(CharacterId character, Vector3 position, Quaternion orientation) => throw new NotImplementedException();
        public void SetCharacterShape(CharacterId character, float capsuleHalfHeight, float capsuleRadius) => throw new NotImplementedException();
        public void SetCharacterMovement(CharacterId character, Vector3 desiredVelocity, bool jump, bool flying) => throw new NotImplementedException();
        public bool TryGetCharacterState(CharacterId character, out CharacterState state) => throw new NotImplementedException();

        // =====================================================================
        // Constraints
        // =====================================================================

        public ConstraintId CreateConstraint(in ConstraintDesc desc)
        {
            // ConstraintKind maps essentially 1:1 onto Jolt's set. The four with
            // no PhysX equivalent - Pulley, Gear, RackAndPinion, Path - are the
            // interesting ones for scripted content, and they are the reason
            // this section is worth exposing to SLua rather than keeping internal.
            throw new NotImplementedException();
        }

        public void RemoveConstraint(ConstraintId constraint) => throw new NotImplementedException();
        public void SetConstraintEnabled(ConstraintId constraint, bool enabled) => throw new NotImplementedException();
        public void SetConstraintMotor(ConstraintId constraint, MotorMode mode, float target, float maxForce) => throw new NotImplementedException();
        public void SetConstraintLimits(ConstraintId constraint, float min, float max) => throw new NotImplementedException();
        public bool IsConstraintBroken(ConstraintId constraint) => throw new NotImplementedException();

        // =====================================================================
        // World
        // =====================================================================

        public void SetGravity(Vector3 gravity)
        {
            if (_system != null)
                _system.Gravity = gravity;
            _settings.Gravity = gravity;
        }

        public void SetTerrain(ShapeId heightFieldShape, Vector3 position)
        {
            if (_system == null)
                throw new InvalidOperationException("SetTerrain before Initialize.");
            if (!_shapes.TryGet(heightFieldShape.Value, out JoltShapeRecord shapeRec) || shapeRec.NativeShape == null)
                throw new ArgumentException($"SetTerrain: {heightFieldShape} is not a live shape handle.");

            // Replace any existing terrain.
            if (_terrainBody.IsValid)
            {
                RemoveBody(_terrainBody);
                _terrainBody = BodyId.Invalid;
            }

            // Static body in the Terrain layer. The shape is already Z-up-correct (the
            // RotatedTranslatedShape wrapper from CreateHeightFieldShape), so no rotation here.
            var objectLayer = new ObjectLayer((uint)PhysicsLayer.Terrain);
            var bcs = new BodyCreationSettings(
                shapeRec.NativeShape, position, Quaternion.Identity, MotionType.Static, objectLayer);
            try
            {
                bcs.Friction = 0.6f;
                BodyID joltId = _bodyInterface.CreateAndAddBody(bcs, Activation.DontActivate);

                var rec = new JoltBodyRecord
                {
                    NativeBodyId = joltId.ID,
                    Shape = heightFieldShape,
                    Layer = PhysicsLayer.Terrain,
                    MotionType = BodyMotionType.Static,
                    UserData = 0u,
                    WantsContactEvents = false,
                };
                uint handle = _bodies.Add(rec);
                rec.Handle = handle;
                _joltToRecord[joltId.ID] = rec;
                _terrainBody = new BodyId(handle);
            }
            finally { bcs.Dispose(); }
        }

        public void SetWaterHeight(float height) => throw new NotImplementedException();

        // =====================================================================
        // Queries  (safe concurrent with Step - use the NarrowPhaseQuery)
        // =====================================================================

        public bool RayCast(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, out RayHit hit)
        {
            hit = default;
            if (_system == null)
                return false;

            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return false;

            // Jolt encodes the ray LENGTH in the direction vector's magnitude (not normalized).
            Vector3 rayDir = direction / len * maxDistance;
            var ray = new Ray(origin, rayDir);

            // NOTE: QueryFilter (the object-layer bitmask) is NOT yet applied - M1 raycast hits
            // every layer. Honouring it needs a custom ObjectLayerFilter callback; deferred (the
            // M1 harness - terrain + one box - does not need it). Passing null = no filtering.
            if (!_system.NarrowPhaseQuery.CastRay(ray, out RayCastResult result, null, null, null))
                return false;

            Vector3 point = origin + rayDir * result.Fraction;

            // Surface normal needs a read-lock on the hit body (RayCastResult carries only body id,
            // fraction, and sub-shape id).
            Vector3 normal = default;
            BodyLockInterface bli = _system.BodyLockInterface;
            bli.LockRead(result.BodyID, out BodyLockRead lockRead);
            try
            {
                Body? hitBody = lockRead.Succeeded ? lockRead.Body : null;
                if (hitBody != null)
                    normal = hitBody.GetWorldSpaceSurfaceNormal(new SubShapeID(result.subShapeID2), point);
            }
            finally { bli.UnlockRead(lockRead); }

            _joltToRecord.TryGetValue(result.BodyID.ID, out JoltBodyRecord? rec);
            hit = new RayHit
            {
                Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                UserData = rec != null ? rec.UserData : 0u,
                ChildUserData = 0u,
                Point = point,
                Normal = normal,
                Distance = maxDistance * result.Fraction,
            };
            return true;
        }
        public int RayCastAll(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, Span<RayHit> hits) => throw new NotImplementedException();
        public int OverlapSphere(Vector3 center, float radius, QueryFilter filter, Span<BodyId> results) => throw new NotImplementedException();
        public int OverlapBox(Vector3 center, Vector3 halfExtents, Quaternion orientation, QueryFilter filter, Span<BodyId> results) => throw new NotImplementedException();
        public bool ShapeCast(ShapeId shape, Vector3 origin, Quaternion orientation, Vector3 direction, float maxDistance, QueryFilter filter, out RayHit hit) => throw new NotImplementedException();

        // =====================================================================
        // Step
        // =====================================================================

        public StepResult Step(
            float deltaTime,
            Span<BodyState> bodyUpdates,
            Span<CharacterState> characterUpdates,
            Span<ContactReport> contacts)
        {
            _stepTimer.Restart();

            // 1. (Task 4) Step every CharacterVirtual BEFORE the physics update. They are
            //    not part of the solve, so they must see the world as it was at the start
            //    of the frame or avatars jitter against moving prims.

            // 2. Advance the simulation (delta #4: 3-arg Update, temp allocation internal).
            if (_system != null && _jobSystem != null)
            {
                int collisionSteps = Math.Max(1, _settings.CollisionSteps);
                _system.Update(deltaTime, collisionSteps, _jobSystem);
            }

            // 3. Fold this frame's queued activation deltas into the step-thread-owned active
            //    set. This is the ONLY place _activeBodies is mutated. Ordered drain so an
            //    activate-then-deactivate within one frame nets out correctly.
            _justActivated.Clear();
            _justDeactivated.Clear();
            _staleActive.Clear();
            while (_activationQueue.TryDequeue(out ActivationDelta delta))
            {
                if (delta.Activated)
                {
                    if (_activeBodies.Add(delta.BodyId))
                        _justActivated.Add(delta.BodyId);
                }
                else
                {
                    _activeBodies.Remove(delta.BodyId);
                    _justActivated.Remove(delta.BodyId);
                    _justDeactivated.Add(delta.BodyId);
                }
            }

            int bodyCount = 0;
            bool bodyOverflow = false;

            // Drain the ACTIVE set: O(active), NOT O(total). foreach over the concrete HashSet
            // uses a struct enumerator - no allocation. For static-only M1 this set is empty and
            // bodyCount stays 0, which is the correct result, not a failure.
            foreach (uint joltId in _activeBodies)
            {
                if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec))
                {
                    _staleActive.Add(joltId); // removed out from under us; clean up after the loop
                    continue;
                }
                if (bodyCount >= bodyUpdates.Length) { bodyOverflow = true; break; }

                var jid = new BodyID(joltId);
                BodyStateFlags flags = BodyStateFlags.Active;
                if (_justActivated.Contains(joltId)) flags |= BodyStateFlags.JustActivated;
                bodyUpdates[bodyCount++] = new BodyState
                {
                    Body = new BodyId(rec.Handle),
                    UserData = rec.UserData,
                    Position = _bodyInterface.GetPosition(jid),
                    Orientation = _bodyInterface.GetRotation(jid),
                    LinearVelocity = _bodyInterface.GetLinearVelocity(jid),
                    AngularVelocity = _bodyInterface.GetAngularVelocity(jid),
                    Flags = flags,
                };
            }
            for (int i = 0; i < _staleActive.Count; i++)
                _activeBodies.Remove(_staleActive[i]);

            // Bodies that slept THIS step get one final state with JustDeactivated set - without
            // it the viewer keeps interpolating and settled objects visibly drift.
            for (int i = 0; i < _justDeactivated.Count && !bodyOverflow; i++)
            {
                uint joltId = _justDeactivated[i];
                if (!_joltToRecord.TryGetValue(joltId, out JoltBodyRecord? rec))
                    continue; // deactivated AND removed same frame - nothing to emit.
                if (bodyCount >= bodyUpdates.Length) { bodyOverflow = true; break; }

                var jid = new BodyID(joltId);
                bodyUpdates[bodyCount++] = new BodyState
                {
                    Body = new BodyId(rec.Handle),
                    UserData = rec.UserData,
                    Position = _bodyInterface.GetPosition(jid),
                    Orientation = _bodyInterface.GetRotation(jid),
                    LinearVelocity = _bodyInterface.GetLinearVelocity(jid),
                    AngularVelocity = _bodyInterface.GetAngularVelocity(jid),
                    Flags = BodyStateFlags.JustDeactivated,
                };
            }

            // 4. (Task 5+) Drain character state.
            int charCount = 0;

            // 5. Drain contacts from the listener's ring buffer. Fed by the OnContact* handlers
            //    (delta #7); no contacts fire for static-only M1, so this drains empty.
            int contactCount = _contactListener.Drain(contacts, out bool contactOverflow);

            _stepTimer.Stop();

            return new StepResult(
                bodyCount,
                charCount,
                contactCount,
                bodyOverflow,
                contactOverflow,
                activeBodyCount: _activeBodies.Count,
                physicsMs: (float)_stepTimer.Elapsed.TotalMilliseconds);
        }
    }

    // =========================================================================
    // Contact listener
    //
    // Jolt fires contact callbacks FROM WORKER THREADS, mid-solve. Two rules:
    //   - never touch scene state here
    //   - never allocate here
    // Write into a preallocated ring and drain on the step thread. This is the
    // most likely place for a first integration to deadlock or tear.
    // =========================================================================
    internal sealed class LegionContactListener
    {
        private readonly ContactReport[] _ring;
        private int _writeIndex;
        private int _dropped;

        public LegionContactListener(int capacity) => _ring = new ContactReport[Math.Max(1, capacity)];

        // OnContactAdded  -> ContactPhase.Begin    -> LSL collision_start
        // OnContactPersisted -> ContactPhase.Persist -> LSL collision
        // OnContactRemoved -> ContactPhase.End     -> LSL collision_end
        //
        // Persist fires EVERY step for every touching pair. Filter here, not
        // above: a single avatar standing on a floor otherwise generates 45
        // events per second forever. Only forward Persist for pairs whose
        // owning object actually has a collision handler registered.
        internal void Push(in ContactReport report)
        {
            int index = Interlocked.Increment(ref _writeIndex) - 1;
            if (index >= _ring.Length)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _ring[index] = report;
        }

        internal int Drain(Span<ContactReport> destination, out bool overflowed)
        {
            int written = Math.Min(Volatile.Read(ref _writeIndex), _ring.Length);
            int count = Math.Min(written, destination.Length);

            _ring.AsSpan(0, count).CopyTo(destination);

            overflowed = Volatile.Read(ref _dropped) > 0 || written > destination.Length;
            Volatile.Write(ref _writeIndex, 0);
            Volatile.Write(ref _dropped, 0);
            return count;
        }
    }

    // =========================================================================
    // Handle table
    //
    // Generation-tagged slots. The low 24 bits index, the high 8 bits are a
    // generation counter bumped on free. A handle to a destroyed body fails
    // validation instead of silently addressing whatever got allocated in its
    // place - which is precisely the class of bug that makes physics crashes
    // impossible to reproduce.
    // =========================================================================
    internal sealed class HandleTable<T> where T : class
    {
        private const int IndexBits = 24;
        private const uint IndexMask = (1u << IndexBits) - 1u;

        private readonly object _gate = new object();
        private T?[] _slots = new T?[1024];
        private byte[] _generations = new byte[1024];
        private readonly ConcurrentQueue<int> _free = new ConcurrentQueue<int>();
        private int _highWater;

        public uint Add(T item)
        {
            lock (_gate)
            {
                if (!_free.TryDequeue(out int slot))
                {
                    if (_highWater == _slots.Length)
                    {
                        Array.Resize(ref _slots, _slots.Length * 2);
                        Array.Resize(ref _generations, _generations.Length * 2);
                    }
                    slot = _highWater++;
                }

                _slots[slot] = item;
                // Generation 0 is reserved so a zeroed handle is never valid.
                if (_generations[slot] == 0) _generations[slot] = 1;
                return ((uint)_generations[slot] << IndexBits) | (uint)slot;
            }
        }

        public bool TryGet(uint handle, out T item)
        {
            int slot = (int)(handle & IndexMask);
            byte generation = (byte)(handle >> IndexBits);

            if (generation == 0 || slot >= _slots.Length || _generations[slot] != generation)
            {
                item = null!;
                return false;
            }

            T? candidate = _slots[slot];
            item = candidate!;
            return candidate != null;
        }

        public bool IsValid(uint handle) => TryGet(handle, out _);

        public bool Remove(uint handle)
        {
            lock (_gate)
            {
                if (!TryGet(handle, out _)) return false;

                int slot = (int)(handle & IndexMask);
                _slots[slot] = null;
                _generations[slot] = (byte)(_generations[slot] == 255 ? 1 : _generations[slot] + 1);
                _free.Enqueue(slot);
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_slots, 0, _slots.Length);
                _highWater = 0;
                while (_free.TryDequeue(out _)) { }
            }
        }
    }

    // Records hold the native handles plus whatever Legion-side bookkeeping the
    // engine will not remember for us.
    internal sealed class JoltBodyRecord
    {
        public uint Handle;               // our Legion HandleTable handle (for jolt-id -> BodyId)
        public uint NativeBodyId;         // Jolt BodyID.ID
        public ShapeId Shape;
        public PhysicsLayer Layer;
        public BodyMotionType MotionType;
        public uint UserData;
        public bool WantsContactEvents;   // gates Persist forwarding
        public float Mass;                // explicit or Volume x Density; 0 where mass is unused (static)
        public bool AllowMotionChange;    // created movable (AllowDynamicOrKinematic) -> may flip motion type
    }

    internal sealed class JoltShapeRecord
    {
        public Shape? NativeShape;        // the shape this handle represents; disposed at RefCount 0
        public Shape? InnerShape;         // private inner shape OWNED by this wrapper (e.g. the Y-up
                                          // heightfield under a Z-up RotatedTranslatedShape); disposed with it
        public int RefCount;
        public bool IsWrapper;            // decorator wrapper (rotated/translated/scaled) over an inner shape
        public ShapeId BaseShape;         // caller-visible wrapped shape (future CreateScaledShape); Invalid otherwise
    }

    internal sealed class JoltCharacterRecord
    {
        public IntPtr Native;
        public uint UserData;
        public Vector3 DesiredVelocity;
        public bool JumpRequested;
        public bool Flying;
    }

    internal sealed class JoltConstraintRecord
    {
        public IntPtr Native;
        public ConstraintKind Kind;
        public BodyId BodyA;
        public BodyId BodyB;
        public float BreakForce;
        public bool Broken;
        public uint UserData;
    }
}
