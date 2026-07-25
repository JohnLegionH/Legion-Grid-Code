// Legion Grid - Jolt physics as an OpenSim region module (PhysicsScene).
//
// ============================ READ THIS FIRST ============================
// M6.1 SKELETON ONLY. This is the seam between OpenSim's PhysicsScene contract and the
// engine-agnostic ILegionPhysicsBackend (whose Jolt implementation we proved across M1-M4.5 in a
// clean-room harness). This slice proves ONE thing: the module registers, boots under
// `physics = Jolt`, steps an empty world, and shuts down cleanly. It has ZERO physics behaviour:
//   - AddPrimShape / AddAvatar return PhysicsActor.Null (accept-and-ignore, so a region with
//     content still boots).
//   - SetTerrain accepts-and-ignores (real terrain is M6.2).
//   - Simulate steps the backend over an empty active set and returns.
// The batched-buffer drain (StepResult -> per-actor RequestPhysicsterseUpdate / collision dispatch)
// is M6.4/M6.6 and is deliberately NOT here.
//
// Registration mirrors BSScene: a Mono.Addins region module that self-selects when [Startup]
// physics == Name. No [Startup] edit - the operator picks `physics = Jolt`; this module recognises
// its own name.
// =========================================================================

using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using Nini.Config;
using log4net;
using OpenMetaverse;
using Mono.Addins;

using Legion.Physics;
using LegionJoltBackend = Legion.Physics.Jolt.JoltPhysicsBackend;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LegionJoltPhysicsScene")]
    public sealed class LegionJoltScene : PhysicsScene, INonSharedRegionModule
    {
        internal static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        internal const string LogHeader = "[LEGION JOLT]";

        private bool m_Enabled = false;
        private IConfigSource m_Config;

        // The engine-agnostic backend (the deliverable proven in the clean-room harness).
        private ILegionPhysicsBackend _backend;

        // Held for M6.3 shape cooking; NOT used this slice.
        private IMesher m_mesher;

        public string RegionName { get; private set; }

        // Caller-owned step buffers (M1 contract: nothing allocates per frame). Empty world drains
        // nothing; sized modestly for the skeleton and revisited when real actors arrive (M6.4).
        private BodyState[] _bodyBuf = new BodyState[1024];
        private CharacterState[] _charBuf = new CharacterState[256];
        private ContactReport[] _contactBuf = new ContactReport[2048];

        // ---------------------------------------------------------------------
        // INonSharedRegionModule
        // ---------------------------------------------------------------------

        public string Name => "Jolt";

        public System.Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            // Self-selection: only enable when the operator chose us. Mirrors BSScene - we do NOT
            // hard-enable, and we never touch [Startup] ourselves.
            IConfig config = source.Configs["Startup"];
            if (config != null)
            {
                string physics = config.GetString("physics", string.Empty);
                if (physics == Name)
                {
                    string mesher = config.GetString("meshing", string.Empty);
                    if (string.IsNullOrEmpty(mesher) || !mesher.Equals("Meshmerizer"))
                    {
                        m_log.Error($"{LogHeader} [Startup] meshing must be set to \"Meshmerizer\" for the Jolt physics module.");
                        throw new System.Exception("Invalid physics meshing option for Jolt");
                    }

                    m_Enabled = true;
                    m_Config = source;
                    m_log.Info($"{LogHeader} enabled (physics = {Name}).");
                }
            }
        }

        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            RegionName = scene.RegionInfo.RegionName;
            PhysicsSceneName = Name + "/" + RegionName;

            scene.RegisterModuleInterface<PhysicsScene>(this);

            uint sizeX = scene.RegionInfo.RegionSizeX;
            uint sizeY = scene.RegionInfo.RegionSizeY;

            var settings = PhysicsBackendSettings.Default;
            settings.MaxBodies = ComputeMaxBodies(sizeX, sizeY);   // decision #3: 65536 / 256 m, scaled by area

            _backend = new LegionJoltBackend();
            _backend.Initialize(settings);

            EngineType = Name;                              // osGetPhysicsEngineType
            EngineName = $"{_backend.Name} {_backend.Version}"; // osGetPhysicsEngineName

            // Terrain/water are accepted-and-ignored this slice (real terrain is M6.2). The base
            // Initialise wires the request-asset delegate and calls our (stub) SetTerrain/SetWaterLevel.
            base.Initialise(scene.PhysicsRequestAsset,
                (scene.Heightmap != null ? scene.Heightmap.GetFloatsSerialised() : new float[sizeX * sizeY]),
                (float)scene.RegionInfo.RegionSettings.WaterHeight);

            m_log.Info($"{LogHeader} region '{RegionName}' {sizeX}x{sizeY}m: backend initialised, MaxBodies={settings.MaxBodies}. {EngineName}");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            // Held for M6.3 shape cooking; unused this slice.
            m_mesher = scene.RequestModuleInterface<IMesher>();
            if (m_mesher == null)
                m_log.Warn($"{LogHeader} no IMesher available - shape cooking (M6.3) will need it.");

            scene.PhysicsEnabled = true;
        }

        // Decision #3: MaxBodies ceiling tracks TOTAL prim count (every prim is a body), default
        // 65536 for a standard 256 m region, scaling with region AREA for varregions.
        private static int ComputeMaxBodies(uint sizeX, uint sizeY)
        {
            const long baseBodies = 65536;
            const long baseArea = 256 * 256;
            long area = (long)sizeX * sizeY;
            long scaled = baseBodies * System.Math.Max(area, baseArea) / baseArea;
            return (int)System.Math.Min(scaled, int.MaxValue);
        }

        // ---------------------------------------------------------------------
        // PhysicsScene - M6.1 stubs (accept-and-ignore so a populated region still boots)
        // ---------------------------------------------------------------------

        public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 velocity, Vector3 size, bool isFlying)
            => PhysicsActor.Null; // M6.5

        public override void RemoveAvatar(PhysicsActor actor) { /* M6.5 */ }

        public override void RemovePrim(PhysicsActor prim) { /* M6.3 */ }

        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                                  Vector3 size, Quaternion rotation, bool isPhysical, uint localid)
            => PhysicsActor.Null; // M6.3

        public override float Simulate(float timeStep)
        {
            // Step the empty world: no actors, so the active-set drain fills nothing. This proves the
            // per-frame path runs without throwing. Returns one simulated frame.
            _backend?.Step(timeStep, _bodyBuf, _charBuf, _contactBuf);
            return 1f;
        }

        public override void SetTerrain(float[] heightMap) { /* accept-and-ignore; real terrain is M6.2 */ }

        public override void SetWaterLevel(float baseheight) { /* M6.2 */ }

        public override void DeleteTerrain() { /* M6.2 */ }

        public override Dictionary<uint, float> GetTopColliders() => new Dictionary<uint, float>();

        public override void Dispose()
        {
            _backend?.Dispose();   // backend teardown -> Foundation.Shutdown
            _backend = null;
        }
    }
}
