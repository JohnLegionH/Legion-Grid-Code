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

        private SQuaternion BodyOrientation()
            => SQuaternion.Multiply(_axisCorrection, ToS(_orientation));   // correction first, then prim

        private void Build()
        {
            _shape = _module.CookPrimShape(_pbs, _size, out _axisCorrection, out _shapeKind);

            BodyDesc desc = BodyDesc.Default;
            desc.Shape = _shape;
            desc.Position = ToS(_position);
            desc.Orientation = BodyOrientation();
            desc.Layer = PhysicsLayer.Static;          // M6.3: non-physical prim = static collision citizen
            desc.MotionType = BodyMotionType.Static;
            desc.Mass = 0f;
            desc.StartActive = false;                  // never wake on insert (startup-stall guard)
            desc.UserData = LocalID;                   // echoed back in every RayHit/contact - no lookup
            _body = _backend.CreateBody(desc);
        }

        // Re-cook the shape (resize / shape swap) keeping the same body. Release order mirrors the
        // terrain path: swap the body onto the new shape first, then release the old handle-ref.
        private void Rebuild()
        {
            if (!_body.IsValid) { Build(); return; }
            ShapeId old = _shape;
            _shape = _module.CookPrimShape(_pbs, _size, out _axisCorrection, out _shapeKind);
            _backend.SetBodyShape(_body, _shape, recomputeMass: false);
            _backend.SetBodyTransform(_body, ToS(_position), BodyOrientation(), false);
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
                _position = value;
                if (_body.IsValid)
                    _backend.SetBodyTransform(_body, ToS(_position), BodyOrientation(), false);
            }
        }

        public override Quaternion Orientation
        {
            get => _orientation;
            set
            {
                _orientation = value;
                if (_body.IsValid)
                    _backend.SetBodyTransform(_body, ToS(_position), BodyOrientation(), false);
            }
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
        public override bool IsPhysical { get => _isPhysical; set { _isPhysical = value; } }   // M6.4 acts on it
        public override float Mass => 0f;              // static
        public override bool Stopped => true;

        public override Vector3 GeometricCenter => _position;
        public override Vector3 CenterOfMass => _position;

        // Inert dynamics state (static prim; M6.4 motion, M6.6 collisions).
        public override Vector3 Velocity { get => Vector3.Zero; set { } }
        public override Vector3 Torque { get => Vector3.Zero; set { } }
        public override Vector3 Force { get => Vector3.Zero; set { } }
        public override Vector3 Acceleration { get => Vector3.Zero; set { } }
        public override Vector3 RotationalVelocity { get => Vector3.Zero; set { } }
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
