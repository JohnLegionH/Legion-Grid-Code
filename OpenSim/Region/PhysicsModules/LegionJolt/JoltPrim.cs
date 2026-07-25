// Legion Grid - a prim as a Jolt rigid body (M6.3).
//
// This is the PhysicsActor OpenSim hands back from AddPrimShape. M6.3 Task 1 scope: NON-PHYSICAL
// prims become STATIC Jolt bodies (collision citizens that never move) via the fixed-shape fast path
// (box / sphere / cylinder cook straight to a Jolt primitive - no meshmerizer). Physical dynamics
// (motion updates, forces, mass) are M6.4; collision-event dispatch is M6.6; the IMesher path is
// M6.3 Task 2. Everything dynamics-related below is therefore deliberately inert.
//
// Types: PhysicsActor speaks OpenMetaverse.Vector3/Quaternion (unqualified here); the backend speaks
// System.Numerics (SVector3/SQuaternion). Body orientation is composed in System.Numerics because the
// cylinder axis-correction ordering must be unambiguous (left operand applied first).

using System;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenMetaverse;
using Legion.Physics;
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    internal sealed class JoltPrim : PhysicsActor
    {
        private readonly LegionJoltScene _module;
        private readonly ILegionPhysicsBackend _backend;

        private PrimitiveBaseShape _pbs;
        private Vector3 _position;
        private Vector3 _size;
        private Quaternion _orientation;
        private bool _isPhysical;
        private Vector3 _velocity;              // last drained linear velocity (the SOP reads this for terse updates)
        private Vector3 _rotationalVelocity;    // last drained angular velocity

        private ShapeId _shape = ShapeId.Invalid;   // one handle-ref held for the prim's life
        private BodyId _body = BodyId.Invalid;

        // Maps the cooked shape's local axis onto SL's convention. Identity for box/sphere; a +90 deg
        // rotation about X for a cylinder (Jolt's CylinderShape axis is Y, SL cylinders are Z-height).
        // Composed as (correction * primOrientation) so the prim's own rotation still applies.
        private SQuaternion _axisCorrection = SQuaternion.Identity;
        private string _shapeKind = "?";

        private int _subscribedMs;   // collision-event subscription window; stored for M6.6, inert now

        internal BodyId BodyHandle => _body;
        internal string ShapeKind => _shapeKind;

        internal JoltPrim(LegionJoltScene module, ILegionPhysicsBackend backend, uint localid, string name,
                          PrimitiveBaseShape pbs, Vector3 position, Vector3 size, Quaternion rotation, bool isPhysical)
        {
            _module = module;
            _backend = backend;
            LocalID = localid;
            Name = name;
            _pbs = pbs;
            _position = position;
            _size = size;
            _orientation = rotation;
            _isPhysical = isPhysical;

            Build();
        }

        private SQuaternion BodyOrientationOf(Quaternion primRot)
            => SQuaternion.Multiply(_axisCorrection, ToS(primRot));   // correction first, then prim

        private void Build()
        {
            _shape = _module.CookPrimShape(_pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            CreateBodyInternal();
        }

        // Create the Jolt body for the CURRENT _isPhysical / _shape / _axisCorrection and cached
        // transform + velocity. Non-physical -> Static (no MotionProperties: the 65k-prim startup guard).
        // Physical -> Dynamic + StartActive (wakes so it falls); mass computed Volume*Density (Density
        // from BodyDesc.Default = 1000). A body that may go physical is created movable ONLY when it is
        // physical (delta #15: Static-born can't be promoted - the toggle recreates instead).
        private void CreateBodyInternal()
        {
            BodyDesc desc = BodyDesc.Default;
            desc.Shape = _shape;
            desc.Position = ToS(_position);
            desc.Orientation = BodyOrientationOf(_orientation);
            desc.LinearVelocity = ToS(_velocity);
            desc.AngularVelocity = ToS(_rotationalVelocity);
            desc.UserData = LocalID;                   // echoed back in every RayHit/contact/update - no lookup
            if (_isPhysical)
            {
                desc.Layer = PhysicsLayer.Dynamic;
                desc.MotionType = BodyMotionType.Dynamic;
                desc.Mass = 0f;                        // <=0 -> backend computes Volume*Density
                desc.StartActive = true;               // wake so it falls immediately on going physical
            }
            else
            {
                desc.Layer = PhysicsLayer.Static;      // non-physical prim = static collision citizen
                desc.MotionType = BodyMotionType.Static;
                desc.Mass = 0f;
                desc.StartActive = false;              // never wake on insert (startup-stall guard)
            }
            _body = _backend.CreateBody(desc);

            if (_isPhysical)
            {
                // Force the activation LISTENER to fire so the backend's active-set (what the drain and
                // ActiveBodyCount track) picks up this body. A body created via AddBody(Activate) on a
                // NON-step thread does not reliably reach the step-thread active-set on its own; an
                // explicit ActivateBody enqueues the activation delta the next Step drains. Without this
                // a freshly-physical prim sits untracked and never streams to the viewer (M6.4 finding).
                _backend.ActivateBody(_body);
                if (_backend.TryGetBodyState(_body, out BodyState st))
                    LegionJoltScene.m_log.Debug(
                        $"{LegionJoltScene.LogHeader} physical body id={LocalID} created: active={((st.Flags & BodyStateFlags.Active) != 0)} posZ={st.Position.Z:0.00} shape={_shapeKind}");
            }
        }

        // Drain: the backend reports this body's post-step transform + velocity. Update the cached values
        // the SOP reads (Position/Orientation/Velocity/RotationalVelocity) and fire the terse update so the
        // viewer sees the motion. The drained Orientation is the BODY orientation (= axisCorrection *
        // primOrientation); undo the correction to hand OpenSim the PRIM orientation (identity for
        // box/sphere/mesh - only cylinders carry a correction). Called once per active body per step, plus
        // one final time when the body sleeps (JustDeactivated) - which is the settle update that stops the
        // viewer interpolating a rested object.
        internal void ApplyStepState(in BodyState s)
        {
            _position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            SQuaternion prim = SQuaternion.Multiply(SQuaternion.Conjugate(_axisCorrection), s.Orientation);
            _orientation = new Quaternion(prim.X, prim.Y, prim.Z, prim.W);
            _velocity = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            _rotationalVelocity = new Vector3(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z);
            RequestPhysicsterseUpdate();
        }

        // Re-cook the shape (resize / shape swap) keeping the same body. Release order mirrors the
        // terrain path: swap the body onto the new shape first, then release the old handle-ref.
        private void Rebuild()
        {
            if (!_body.IsValid) { Build(); return; }
            ShapeId old = _shape;
            _shape = _module.CookPrimShape(_pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            _backend.SetBodyShape(_body, _shape, recomputeMass: false);   // keeps the body at its current transform
            if (old.IsValid)
                _backend.ReleaseShape(old);
        }

        // Called by LegionJoltScene.RemovePrim. RemoveBody drops the body's native shape ref; releasing
        // our handle-ref then frees the shape - no leak, no premature free.
        internal void Destroy()
        {
            if (_body.IsValid)
                _backend.RemoveBody(_body);
            _body = BodyId.Invalid;
            if (_shape.IsValid)
                _backend.ReleaseShape(_shape);
            _shape = ShapeId.Invalid;
        }

        private static SVector3 ToS(Vector3 v) => new SVector3(v.X, v.Y, v.Z);
        private static SQuaternion ToS(Quaternion q) => new SQuaternion(q.X, q.Y, q.Z, q.W);

        // ---------------------------------------------------------------------
        // PhysicsActor contract. Real state: Position / Orientation / Size (pushed to the body).
        // The rest is inert this slice (static body).
        // ---------------------------------------------------------------------

        public override Vector3 Position
        {
            get => _position;
            set
            {
                if (_position == value) return;   // the drain writes _position directly; only a real move recreates
                _position = value;
                if (_body.IsValid) RepositionBody();
            }
        }

        public override Quaternion Orientation
        {
            get => _orientation;
            set
            {
                if (_orientation == value) return;
                _orientation = value;
                if (_body.IsValid) RepositionBody();
            }
        }

        // Move the body to the current cached transform. The backend's SetBodyTransform is unimplemented
        // (throws NotImplementedException) in 2.18.6, so the only way to reposition is remove + recreate
        // (CreateBody sets the transform in its BodyCreationSettings). Reuses the existing shape handle -
        // no re-cook, no shape-ref leak. Guarded by the setters so a passive physics-driven update (which
        // writes _position via the drain, not the setter) never triggers a recreate.
        private void RepositionBody()
        {
            if (_body.IsValid) _backend.RemoveBody(_body);
            CreateBodyInternal();
        }

        public override Vector3 Size
        {
            get => _size;
            set
            {
                if (_size == value) return;
                _size = value;
                Rebuild();
            }
        }

        public override PrimitiveBaseShape Shape
        {
            set
            {
                _pbs = value;
                Rebuild();
            }
        }

        public override int PhysicsActorType { get => (int)ActorTypes.Prim; set { } }

        public override bool IsPhysical
        {
            get => _isPhysical;
            set
            {
                if (_isPhysical == value) return;
                _isPhysical = value;
                // Delta #15: a Static-born body has no MotionProperties and CANNOT be promoted
                // (SetBodyMotionType throws), so the toggle RECREATES the body. It also re-cooks the shape:
                // a physical MESH must become a convex hull (mesh Volume=0 -> mass 0), non-physical reverts
                // to a triangle mesh. Transform + velocity carry over. No taint (Jolt is concurrent).
                RecreateBody();
            }
        }

        // Recreate the body for a changed _isPhysical (mesh<->hull, static<->dynamic), preserving
        // transform + velocity. Order mirrors Rebuild: cook new shape, drop old body, create new body,
        // release old shape handle - leak-free.
        private void RecreateBody()
        {
            ShapeId old = _shape;
            _shape = _module.CookPrimShape(_pbs, _size, _isPhysical, out _axisCorrection, out _shapeKind);
            if (_body.IsValid)
                _backend.RemoveBody(_body);
            CreateBodyInternal();
            if (old.IsValid)
                _backend.ReleaseShape(old);
        }

        public override float Mass => 0f;              // reported to OpenSim; the real dynamic mass lives in Jolt
        public override bool Stopped => true;

        public override Vector3 GeometricCenter => _position;
        public override Vector3 CenterOfMass => _position;

        // Linear/angular velocity: cached from the drain (SOP reads these for terse updates); a set on a
        // live physical body pushes through so a script llSetVelocity takes effect.
        public override Vector3 Velocity
        {
            get => _velocity;
            set { _velocity = value; if (_body.IsValid && _isPhysical) _backend.SetBodyLinearVelocity(_body, ToS(value)); }
        }
        public override Vector3 RotationalVelocity
        {
            get => _rotationalVelocity;
            set { _rotationalVelocity = value; if (_body.IsValid && _isPhysical) _backend.SetBodyAngularVelocity(_body, ToS(value)); }
        }
        public override Vector3 Torque { get => Vector3.Zero; set { } }
        public override Vector3 Force { get => Vector3.Zero; set { } }
        public override Vector3 Acceleration { get => Vector3.Zero; set { } }
        public override float CollisionScore { get; set; }
        public override bool Kinematic { get => false; set { } }
        public override float Buoyancy { get => 0f; set { } }
        public override bool Flying { get => false; set { } }
        public override bool SetAlwaysRun { get => false; set { } }
        public override bool ThrottleUpdates { get => false; set { } }
        public override bool IsColliding { get; set; }
        public override bool CollidingGround { get; set; }
        public override bool CollidingObj { get; set; }
        public override bool Grabbed { set { } }
        public override bool Selected { set { } }

        public override void CrossingFailure() { }
        public override void link(PhysicsActor obj) { }     // linksets: M6.x
        public override void delink() { }
        public override void LockAngularMotion(byte axislocks) { }

        public override void AddForce(Vector3 force, bool pushforce) { }
        public override void AddAngularForce(Vector3 force, bool pushforce) { }
        public override void AvatarJump(float forceZ) { }
        public override void SetMomentum(Vector3 momentum) { }

        public override void SetVolumeDetect(int param) { }   // VolumeDetect / phantom-events: M6.6

        // Collision-event subscription: stored so M6.6 can gate Persist forwarding; inert now.
        public override void SubscribeEvents(int ms) { _subscribedMs = ms; }
        public override void UnSubscribeEvents() { _subscribedMs = 0; }
        public override bool SubscribedEvents() => _subscribedMs > 0;

        // Vehicles - not applicable to a static prim (M7).
        public override int VehicleType { get => 0; set { } }
        public override void VehicleFloatParam(int param, float value) { }
        public override void VehicleVectorParam(int param, Vector3 value) { }
        public override void VehicleRotationParam(int param, Quaternion rotation) { }
        public override void VehicleFlags(int param, bool remove) { }

        // PID / hover / RotLookAt - physical-motion features (M6.4+).
        public override Vector3 PIDTarget { set { } }
        public override bool PIDActive { get => false; set { } }
        public override float PIDTau { set { } }
        public override bool PIDHoverActive { get => false; set { } }
        public override float PIDHoverHeight { set { } }
        public override PIDHoverType PIDHoverType { set { } }
        public override float PIDHoverTau { set { } }
        public override Quaternion APIDTarget { set { } }
        public override bool APIDActive { set { } }
        public override float APIDStrength { set { } }
        public override float APIDDamping { set { } }
    }
}
