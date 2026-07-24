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
        // Shapes
        // =====================================================================

        public ShapeId CreateBoxShape(Vector3 halfExtents) => throw new NotImplementedException();
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
            // Jolt's HeightFieldShape wants a power-of-two square sample count.
            // 256x256 regions land exactly; varregions will need tiling or
            // padding. Worth deciding before you write the terrain path.
            throw new NotImplementedException();
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

        public void AddShapeRef(ShapeId shape) => throw new NotImplementedException();
        public void ReleaseShape(ShapeId shape) => throw new NotImplementedException();

        // =====================================================================
        // Bodies
        // =====================================================================

        public BodyId CreateBody(in BodyDesc desc)
        {
            // The important line in this whole method is the activation mode:
            //
            //   _bodyInterface.AddBody(id, desc.StartActive ? Activation.Activate
            //                                               : Activation.DontActivate);
            //
            // Jolt deliberately does not auto-wake on insert. Honour that. A
            // region loading 50k prims with Activate is a stall you will spend
            // a week chasing.
            //
            // Also: because BodyInterface is safe to call off the step thread,
            // this can be invoked straight from the scene's rez path. No taint
            // queue, no deferred-add list. That is the concurrency payoff.
            throw new NotImplementedException();
        }

        public void RemoveBody(BodyId body) => throw new NotImplementedException();
        public bool IsBodyValid(BodyId body) => _bodies.IsValid(body.Value);

        public void SetBodyShape(BodyId body, ShapeId shape, bool recomputeMass) => throw new NotImplementedException();
        public void SetBodyMotionType(BodyId body, BodyMotionType motionType, bool activate) => throw new NotImplementedException();
        public void SetBodyLayer(BodyId body, PhysicsLayer layer) => throw new NotImplementedException();

        public void SetBodyTransform(BodyId body, Vector3 position, Quaternion orientation, bool activate) => throw new NotImplementedException();
        public void SetBodyLinearVelocity(BodyId body, Vector3 velocity) => throw new NotImplementedException();
        public void SetBodyAngularVelocity(BodyId body, Vector3 velocity) => throw new NotImplementedException();

        public void SetBodyMass(BodyId body, float mass) => throw new NotImplementedException();
        public void SetBodyFriction(BodyId body, float friction) => throw new NotImplementedException();
        public void SetBodyRestitution(BodyId body, float restitution) => throw new NotImplementedException();
        public void SetBodyDamping(BodyId body, float linear, float angular) => throw new NotImplementedException();
        public void SetBodyGravityFactor(BodyId body, float factor) => throw new NotImplementedException();

        public void SetBodyAxisLocks(BodyId body, Vector3 allowedTranslation, Vector3 allowedRotation)
        {
            // Jolt: SixDOFConstraint to world, or MotionProperties mass/inertia
            // scaling. The constraint route is more predictable; the inertia
            // route is cheaper. Start with the constraint and measure.
            throw new NotImplementedException();
        }

        public void ApplyForce(BodyId body, Vector3 force) => throw new NotImplementedException();
        public void ApplyTorque(BodyId body, Vector3 torque) => throw new NotImplementedException();
        public void ApplyImpulse(BodyId body, Vector3 impulse) => throw new NotImplementedException();
        public void ApplyImpulseAtPoint(BodyId body, Vector3 impulse, Vector3 worldPoint) => throw new NotImplementedException();
        public void ApplyAngularImpulse(BodyId body, Vector3 angularImpulse) => throw new NotImplementedException();

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

        public void ActivateBody(BodyId body) => throw new NotImplementedException();
        public void DeactivateBody(BodyId body) => throw new NotImplementedException();
        public bool TryGetBodyState(BodyId body, out BodyState state) => throw new NotImplementedException();

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

        public void SetTerrain(ShapeId heightFieldShape, Vector3 position) => throw new NotImplementedException();
        public void SetWaterHeight(float height) => throw new NotImplementedException();

        // =====================================================================
        // Queries  (safe concurrent with Step - use the NarrowPhaseQuery)
        // =====================================================================

        public bool RayCast(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, out RayHit hit) => throw new NotImplementedException();
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

            // 3. (Task 4) Drain the ACTIVE bodies - NOT every body. 2.18.6 has no
            //    GetActiveBodies-returning-a-set; the drain will read from an active set we
            //    maintain via OnBodyActivated/OnBodyDeactivated. See the Task 4 scout notes.
            int bodyCount = 0;
            bool bodyOverflow = false;

            // 4. (Task 4) Drain character state.
            int charCount = 0;

            // 5. Drain contacts from the listener's ring buffer. Empty until the OnContact*
            //    events are wired (delta #7), but the plumbing is in place.
            int contactCount = _contactListener.Drain(contacts, out bool contactOverflow);

            _stepTimer.Stop();

            return new StepResult(
                bodyCount,
                charCount,
                contactCount,
                bodyOverflow,
                contactOverflow,
                activeBodyCount: bodyCount,
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
        public uint NativeBodyId;
        public ShapeId Shape;
        public PhysicsLayer Layer;
        public BodyMotionType MotionType;
        public uint UserData;
        public bool WantsContactEvents;   // gates Persist forwarding
    }

    internal sealed class JoltShapeRecord
    {
        public IntPtr Native;
        public int RefCount;
        public bool IsScaledWrapper;
        public ShapeId BaseShape;
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
