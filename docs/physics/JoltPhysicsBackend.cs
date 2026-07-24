// Legion Grid - Jolt implementation of ILegionPhysicsBackend
//
// ============================ READ THIS FIRST ============================
// The ILegionPhysicsBackend interface is the deliverable. THIS file is a
// mapping sketch: it shows where each concept lands in Jolt and which parts
// are non-obvious. The JoltPhysicsSharp API surface has moved across 1.x -> 2.x
// (and 2.2x targets net9.0/net10.0), so treat every Jolt-side call below as a
// shape to verify against whichever version you pin, not as copy-paste code.
//
// The parts worth reading carefully are the ones that are easy to get wrong and
// expensive to discover later:
//   - broad phase / object layer filtering  (BroadPhaseLayers region)
//   - DontActivate on insert                (CreateBody)
//   - ScaledShape for prim resize           (CreateScaledShape)
//   - contact ring buffer                   (LegionContactListener)
//   - CharacterVirtual stepping order       (Step)
// =========================================================================

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

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

        // TODO: PhysicsSystem, BodyInterface, JobSystem, TempAllocator handles.
        // private PhysicsSystem _system;
        // private BodyInterface _bodyInterface;      // locking; safe cross-thread
        // private BodyInterface _bodyInterfaceNoLock; // step-thread only, faster

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
            public const int Count = 3;
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

            // TODO:
            //   Foundation.Init();
            //   _system = new PhysicsSystem(new PhysicsSystemSettings {
            //       MaxBodies              = settings.MaxBodies,
            //       MaxBodyPairs           = settings.MaxBodyPairs,
            //       MaxContactConstraints  = settings.MaxContactConstraints,
            //       ObjectLayerPairFilter    = <ShouldCollide>,
            //       BroadPhaseLayerInterface = <ToBroadPhase, BroadPhase.Count>,
            //   });
            //   _system.Gravity = settings.Gravity;

            _contactListener = new LegionContactListener(_settings.MaxContactConstraints * 2);
            // _system.SetContactListener(_contactListener);
        }

        public void Dispose()
        {
            // Order matters: constraints -> characters -> bodies -> shapes -> system.
            _constraints.Clear();
            _characters.Clear();
            _bodies.Clear();
            _shapes.Clear();
            // _system?.Dispose();
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

        public void SetGravity(Vector3 gravity) => throw new NotImplementedException();
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

            // 1. Step every CharacterVirtual BEFORE the physics update. They are
            //    not part of the solve, so they must see the world as it was at
            //    the start of the frame or avatars jitter against moving prims.
            //
            //    foreach (var ch in _characters.Live)
            //        ch.Handle.ExtendedUpdate(deltaTime, gravity, updateSettings, ...);

            // 2. Advance the simulation.
            //
            //    _system.Update(deltaTime, _settings.CollisionSteps, _tempAllocator, _jobSystem);

            // 3. Drain active bodies. Do NOT iterate every body - ask Jolt for
            //    the active set only. That is the difference between O(active)
            //    and O(total) per frame, and in a normal region those differ by
            //    three orders of magnitude.
            int bodyCount = 0;
            bool bodyOverflow = false;
            //
            //    var active = _system.GetActiveBodies(BodyType.RigidBody);
            //    foreach (var bodyId in active) { ... fill bodyUpdates ... }
            //
            // Bodies that went to sleep this step need one final update with
            // JustDeactivated set, or the viewer keeps the last interpolated
            // position and objects visibly drift after settling.

            // 4. Drain character state.
            int charCount = 0;

            // 5. Drain contacts from the listener's ring buffer.
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

        public LegionContactListener(int capacity) => _ring = new ContactReport[capacity];

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
