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

using System;
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
// The backend speaks System.Numerics.Vector3; OpenSim speaks OpenMetaverse.Vector3 (the unqualified
// Vector3 here). Alias the numerics one so backend calls are unambiguous.
using SVector3 = System.Numerics.Vector3;
using SQuaternion = System.Numerics.Quaternion;

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

        // Terrain (M6.2): the current cooked heightfield ShapeId (released + replaced on each SetTerrain),
        // and the region dimensions needed to interpret the flat float[] heightmap OpenSim hands us.
        private ShapeId _terrainShape = ShapeId.Invalid;
        private int _regionSizeX;
        private int _regionSizeY;
        private Scene _scene;

        // M6.2 Task 2: radial-cone hill parameters (set by `jolt terrainhill`) so `jolt hilltest` can
        // print hand-computable expected Z. z = base + amp*max(0, 1 - dist((x,y),(cx,cy))/R).
        private float _hillCx, _hillCy, _hillBase, _hillAmp, _hillR;
        private bool _hillSet;

        private float HillZ(float x, float y)
        {
            float dx = x - _hillCx, dy = y - _hillCy;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);
            return _hillBase + _hillAmp * Math.Max(0f, 1f - d / _hillR);
        }

        // M6.3: live prims by SceneObjectPart.LocalId. RemovePrim looks up here; also the future
        // Step-drain target for physical (M6.4) actors. Guarded because Add/RemovePrim can arrive off
        // the heartbeat thread (the backend permits concurrent Create/Remove with Step).
        private readonly Dictionary<uint, JoltPrim> _prims = new Dictionary<uint, JoltPrim>();

        // M6.3 Task 2 proof bookkeeping: the console-rezzed test prims (so `jolt rayprims` can state
        // expected hits and `jolt clearprims` can delete them through the real scene-delete path).
        private struct TestPrim { public uint LocalId; public UUID Sog; public string Kind; public Vector3 Pos; public Vector3 Size; }
        private readonly List<TestPrim> _testPrims = new List<TestPrim>();

        // M6.3 Task 2: collision-mesh LOD (matches BulletSim's BSParam.MeshLOD default), and the
        // characterization of the last RAW mesher output cooked (verts/tris/degenerate/duplicate/AABB).
        private const float MeshLod = 32f;
        private struct MeshStats
        {
            public int Verts, Tris, DegenerateTris, DuplicateVerts, OutOfRangeIndices;
            public SVector3 Min, Max;
            public float Volume;   // enclosed volume of the (closed) mesh; == convex-hull volume for a convex prim
        }
        private MeshStats _lastMeshStats;

        // M6.4 dynamics: per-frame step counter + latest active-body count (drop asserts read these), and
        // the tracked physical drops for `jolt droptest`/`dropmesh`/`dropstatus`. _lastBoxRestZ/_lastMeshRestZ
        // persist across drops so a re-run can report determinism (same rest height).
        private long _stepCount;
        private int _lastActiveBodyCount;
        private float _lastBoxRestZ = float.NaN, _lastMeshRestZ = float.NaN;
        private sealed class DropTrack
        {
            public uint LocalId;
            public string Kind;                 // "box" or "mesh"
            public float StartZ;
            public long StartStep;
            public float MinZ = float.MaxValue;
            public float LastZ, LastSpeed;
            public int JustDeactivatedCount;
            public float RestZ = float.NaN;
            public long RestStep = -1;
            public float ExpectedMass;          // box: volume*density; mesh: hull(=mesh)volume*density
            public float ExpectedRestZ;         // terrain Z + half-height
        }
        private readonly List<DropTrack> _drops = new List<DropTrack>();
        private long _logStepsUntil = -1;   // window: log per-frame dt/ActiveBodyCount/liveZ after a drop

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

            // Stored BEFORE base.Initialise, because that calls SetTerrain(heightMap) - which needs the
            // region dims to interpret the flat float[] and build the (N+1) field.
            _scene = scene;
            _regionSizeX = (int)sizeX;
            _regionSizeY = (int)sizeY;

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

            // M6.2 proof hook: a console command that raycasts straight down onto the cooked terrain and
            // reports the hit Z - the rigorous, viewer-free gate. Registered once (global console).
            if (MainConsole.Instance != null && !_consoleRegistered)
            {
                _consoleRegistered = true;
                MainConsole.Instance.Commands.AddCommand("Physics", false, "jolt",
                    "jolt terraintest | terrainslope | terrainhill | hilltest | probe <x> <y> | rezprims | rayprims | rezmesh | rezmeshn <count> | raymesh | droptest | dropmesh | dropstatus | heights <x> <y> | clearprims",
                    "Legion Jolt proofs (M6.2 terrain / M6.3 prims): raycast the cooked collision surfaces and report hits.",
                    HandleJoltConsole);
            }
        }

        private static bool _consoleRegistered;

        // Raycast straight down at XY (from well above the region) and report the hit Z - proves the
        // heightfield's ACTUAL collision surface, not "it booted". `jolt terraintest` sweeps the extent
        // probes (interior + the far edge that the (N+1) field must now cover); `jolt probe x y` is ad hoc.
        private void HandleJoltConsole(string module, string[] cmd)
        {
            if (_backend == null) { MainConsole.Instance.Output($"{LogHeader} no backend."); return; }

            if (cmd.Length >= 2 && cmd[1] == "terraintest")
            {
                int n = _regionSizeX;
                // Interior probes + the FAR METRE (n-0.5): the (N+1) field spans [0,n], so (n-0.5) - which
                // an old N-sample field would MISS (it only reached n-1) - must now HIT. (n) is the exact
                // outer vertex and may graze (float); (n+0.5) is beyond the region and must miss.
                var pts = new (float x, float y)[]
                { (1f, 1f), (n / 2f, n / 2f), (n - 1f, n - 1f), (n - 0.5f, n - 0.5f), (n, n), (n + 0.5f, n + 0.5f) };
                MainConsole.Instance.Output($"{LogHeader} terrain raycast probes (region {n}x{_regionSizeY}; (N+1) field spans [0,{n}] m):");
                foreach (var (px, py) in pts)
                {
                    bool hit = _backend.RayCast(new SVector3(px, py, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.All, out RayHit h);
                    MainConsole.Instance.Output(hit
                        ? $"  ({px,7:0.0},{py,7:0.0}) -> HIT  z={h.Point.Z:0.000}  n.z={h.Normal.Z:0.00}"
                        : $"  ({px,7:0.0},{py,7:0.0}) -> miss");
                }
                MainConsole.Instance.Output($"  interior + (n-0.5) must HIT at the flat Z; (n) exact vertex may graze; (n+0.5) beyond region misses.");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "terrainslope")
            {
                // Push a KNOWN X-gradient (z rises with X, independent of Y) through the real SetTerrain
                // path to prove orientation + the row-mirror fix on real-shaped data: a raycast at (x,y)
                // must read z = base + x*slope. A transpose would make z depend on Y; a mirror would
                // invert it. base+slope chosen so probes are unambiguous.
                const float baseZ = 10f, slope = 0.1f;
                var hm = new float[_regionSizeX * _regionSizeY];
                for (int gy = 0; gy < _regionSizeY; gy++)
                    for (int gx = 0; gx < _regionSizeX; gx++)
                        hm[gy * _regionSizeX + gx] = baseZ + gx * slope;
                SetTerrain(hm);
                MainConsole.Instance.Output($"{LogHeader} set X-gradient terrain: z = {baseZ} + x*{slope} (independent of y).");
                MainConsole.Instance.Output($"  confirm orientation: jolt probe 50 200 -> z~15 ; jolt probe 200 50 -> z~30 (z tracks X, not Y).");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "terrainhill")
            {
                // A KNOWN radial cone: elevation varies in BOTH axes (a transpose/single-axis bug shows),
                // exact closed form, and equal-distance symmetry for the 2D-orientation check. R is large
                // enough that the slope reaches the region edges (no flat base to hide behind).
                _hillCx = _regionSizeX / 2f; _hillCy = _regionSizeY / 2f;
                _hillBase = 20f; _hillAmp = 40f; _hillR = 200f; _hillSet = true;

                // Write into the SCENE heightmap (not just physics), so the taint propagates to the
                // VIEWER (patch send) as well; then push to physics immediately so `jolt hilltest`
                // works this instant instead of waiting for the ~5 s terrain tick.
                for (int gy = 0; gy < _regionSizeY; gy++)
                    for (int gx = 0; gx < _regionSizeX; gx++)
                        _scene.Heightmap[gx, gy] = HillZ(gx, gy);
                SetTerrain(_scene.Heightmap.GetFloatsSerialised());

                MainConsole.Instance.Output($"{LogHeader} radial cone set (scene + physics): z = {_hillBase} + {_hillAmp}*max(0, 1 - dist((x,y),({_hillCx},{_hillCy}))/{_hillR})");
                MainConsole.Instance.Output($"  peak ({_hillCx},{_hillCy}) z={_hillBase + _hillAmp:0.00}; hand-check any XY with that formula. Run: jolt hilltest");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "hilltest")
            {
                if (!_hillSet) { MainConsole.Instance.Output($"{LogHeader} run `jolt terrainhill` first."); return; }
                float cx = _hillCx, cy = _hillCy;
                // (peak; two equal-distance points at +X vs +Y - MUST match; a mid-slope; the non-flat
                // EDGE at (255.5,255.5); the last real edge sample; a low corner).
                var pts = new (float x, float y)[]
                { (cx, cy), (cx + 50f, cy), (cx, cy + 50f), (cx + 72f, cy + 72f),
                  (_regionSizeX - 0.5f, _regionSizeY - 0.5f), (_regionSizeX - 1f, _regionSizeY - 1f), (10f, 10f) };
                MainConsole.Instance.Output($"{LogHeader} hill raycast probes (expected = cone formula; small interp/edge-strip deltas OK):");
                MainConsole.Instance.Output($"     x       y   |  expected |  actual  |  delta   |  dist");
                foreach (var (px, py) in pts)
                {
                    float exp = HillZ(px, py);
                    float dd = (float)Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    bool hit = _backend.RayCast(new SVector3(px, py, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.All, out RayHit h);
                    string act = hit ? $"{h.Point.Z,8:0.000}" : "  miss  ";
                    string del = hit ? $"{h.Point.Z - exp,8:0.000}" : "   -    ";
                    MainConsole.Instance.Output($"  ({px,6:0.0},{py,6:0.0}) | {exp,8:0.000} | {act} | {del} | {dd,6:0.0}");
                }
                MainConsole.Instance.Output($"  ({cx + 50f:0},{cy}) and ({cx},{cy + 50f:0}) are equal-distance -> MUST read the same Z (2D orientation).");
                return;
            }

            if (cmd.Length >= 4 && cmd[1] == "probe"
                && float.TryParse(cmd[2], out float x) && float.TryParse(cmd[3], out float y))
            {
                bool hit = _backend.RayCast(new SVector3(x, y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.All, out RayHit h);
                MainConsole.Instance.Output(hit
                    ? $"{LogHeader} ({x:0.0},{y:0.0}) -> HIT z={h.Point.Z:0.000} normal=({h.Normal.X:0.00},{h.Normal.Y:0.00},{h.Normal.Z:0.00})"
                    : $"{LogHeader} ({x:0.0},{y:0.0}) -> miss");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "rezprims")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                ClearTestPrims();   // idempotent: re-rez from a clean slate

                // Three basic shapes at z=100 (above any terrain/hill), spread 8 m in X so they don't
                // overlap. Sizes chosen so the raycast proofs are unambiguous: the cylinder is tall+thin
                // (halfHeight 2, radius 0.5) so a Z-axis (correct) top-cap hit at 102 is nowhere near a
                // Y-axis (wrong) curved-side hit at 100.5.
                RezTestPrim("box", new Vector3(120f, 128f, 100f), new Vector3(2f, 3f, 4f));
                RezTestPrim("sphere", new Vector3(128f, 128f, 100f), new Vector3(2f, 2f, 2f));
                RezTestPrim("cylinder", new Vector3(136f, 128f, 100f), new Vector3(1f, 1f, 4f));

                MainConsole.Instance.Output($"{LogHeader} rezzed {_testPrims.Count} test prims via the real AddNewSceneObject -> ApplyPhysics -> AddPrimShape path:");
                foreach (var tp in _testPrims)
                {
                    string via = "?";
                    lock (_prims)
                        if (_prims.TryGetValue(tp.LocalId, out JoltPrim jp)) via = jp.ShapeKind;
                    MainConsole.Instance.Output($"  id={tp.LocalId,-6} {tp.Kind,-9} pos=({tp.Pos.X:0.0},{tp.Pos.Y:0.0},{tp.Pos.Z:0.0}) size=({tp.Size.X:0.0},{tp.Size.Y:0.0},{tp.Size.Z:0.0}) -> jolt shape: {via}");
                }
                MainConsole.Instance.Output($"  now run: jolt rayprims  (casts through Scene.RayCastFiltered - the exact llCastRay pipeline).");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "rayprims")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                RayPrims();   // runs with prims (expect hits) OR after clearprims (expect all miss)
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "rezmesh")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                ClearTestPrims();

                // A triangular PRISM forces the mesher (not a fast-path shape) and needs NO asset (unlike
                // a sculpt, which can't mesh synchronously headless). size (4,4,3): a flat triangular top
                // at z=101.5, inscribed in a 4x4 bbox - the bbox corners are EMPTY (the tetra-vs-bbox test).
                var pos = new Vector3(120f, 128f, 100f);
                var size = new Vector3(4f, 4f, 3f);
                RezTestPrim("prism", pos, size);

                uint id = _testPrims.Count > 0 ? _testPrims[0].LocalId : 0u;
                string kind = "?";
                lock (_prims)
                    if (_prims.TryGetValue(id, out JoltPrim jp)) kind = jp.ShapeKind;
                MainConsole.Instance.Output($"{LogHeader} rezzed prism id={id} via the real AddPrimShape path -> jolt shape: {kind}  (expect 'mesh(mesher)', NOT basic/bbox).");

                MeshStats s = _lastMeshStats;
                MainConsole.Instance.Output($"  REAL mesher geometry: verts={s.Verts} tris={s.Tris} degenerate={s.DegenerateTris} duplicateVerts={s.DuplicateVerts} outOfRangeIdx={s.OutOfRangeIndices}");
                MainConsole.Instance.Output($"    local AABB min=({s.Min.X:0.00},{s.Min.Y:0.00},{s.Min.Z:0.00}) max=({s.Max.X:0.00},{s.Max.Y:0.00},{s.Max.Z:0.00})");

                // Decision-point check (physical -> convex hull, delta #31): cook the SAME prism physical,
                // inline, purely to confirm routing (cook+release, no body). Real physical dynamics is M6.4.
                ShapeId hull = CookPrimShape(GetPrismPbs(), size, true, out _, out string hullKind);
                MainConsole.Instance.Output($"  decision-point: physical prism cooks to '{hullKind}' (expect 'hull(mesher)' - a mesh's Volume=0 would rez a physical prim mass-0; hull avoids it).");
                if (hull.IsValid) _backend.ReleaseShape(hull);

                MainConsole.Instance.Output($"  now run: jolt raymesh  (grid cast - triangle top HITs ~101.5, empty bbox corners MISS; a box would hit all).");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "raymesh")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                RayMesh();
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "rezmeshn")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                int count = 4;
                if (cmd.Length >= 3 && int.TryParse(cmd[2], out int c)) count = c;
                count = Math.Max(1, Math.Min(12, count));
                RezMeshN(count);
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "droptest")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                ClearTestPrims();
                DropOne("box", new Vector3(2f, 2f, 2f), 120f, 128f);
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "dropmesh")
            {
                if (_scene == null) { MainConsole.Instance.Output($"{LogHeader} no scene."); return; }
                ClearTestPrims();
                DropOne("prism", new Vector3(2f, 2f, 2f), 136f, 128f);
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "dropstatus")
            {
                DropStatus();
                return;
            }

            if (cmd.Length >= 4 && cmd[1] == "heights"
                && float.TryParse(cmd[2], out float hx) && float.TryParse(cmd[3], out float hy))
            {
                // Line up the four heights at one XY so a "box rests at the wrong Z" is unambiguous:
                // (a) what Jolt actually collides at (heightfield raycast), (b) what OpenSim's scene
                // heightmap says, (c) where the dropped box actually is, (d) the water plane. Water and
                // buoyancy are non-colliding, so the box MUST rest on (a); if (a)!=(b) the cook is wrong,
                // if (c)!=(a) the box isn't resting on terrain.
                bool hit = _backend.RayCast(new SVector3(hx, hy, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit rh);
                float sceneH = float.NaN;
                int gx = (int)Math.Round(hx), gy = (int)Math.Round(hy);
                if (_scene?.Heightmap != null && gx >= 0 && gx < _regionSizeX && gy >= 0 && gy < _regionSizeY)
                    sceneH = (float)_scene.Heightmap[gx, gy];
                float water = (float)(_scene?.RegionInfo?.RegionSettings?.WaterHeight ?? 0.0);

                MainConsole.Instance.Output($"{LogHeader} heights at ({hx:0.0},{hy:0.0}):");
                MainConsole.Instance.Output($"  (a) Jolt heightfield raycast : {(hit ? $"HIT z={rh.Point.Z:0.000} (n.z={rh.Normal.Z:0.00})" : "MISS - NO terrain collision here")}");
                MainConsole.Instance.Output($"  (b) OpenSim scene heightmap  : {sceneH:0.000}");
                MainConsole.Instance.Output($"  (d) region water height      : {water:0.000}");
                foreach (DropTrack t in _drops)
                {
                    lock (_prims)
                        if (_prims.TryGetValue(t.LocalId, out JoltPrim jp) && _backend.TryGetBodyState(jp.BodyHandle, out BodyState st))
                            MainConsole.Instance.Output($"  (c) drop {t.Kind} id={t.LocalId} : liveZ={st.Position.Z:0.000} joltActive={(((st.Flags & BodyStateFlags.Active) != 0) ? "Y" : "N")} startZ={t.StartZ:0.00}");
                }
                MainConsole.Instance.Output($"  read: (a)==(b) => cook matches OpenSim; box rest (c) should ~= (a)+halfHeight. (c)~water while (a)!=water => box not on terrain.");
                return;
            }

            if (cmd.Length >= 2 && cmd[1] == "clearprims")
            {
                int n = ClearTestPrims();
                MainConsole.Instance.Output($"{LogHeader} deleted {n} test prims (scene delete -> RemovePrim). `jolt rayprims` should now miss.");
                return;
            }

            MainConsole.Instance.Output("Usage: jolt terraintest | terrainslope | terrainhill | hilltest | probe <x> <y> | rezprims | rayprims | rezmesh | rezmeshn <count> | raymesh | droptest | dropmesh | dropstatus | heights <x> <y> | clearprims");
        }

        // Build one basic prim with a CANONICAL PrimitiveBaseShape (a real viewer/OAR prim's values,
        // not the quirky CreateCylinder factory) and rez it through the genuine scene path so OpenSim -
        // not us - calls AddPrimShape. Non-physical, non-phantom by default => a static Jolt body.
        private SceneObjectGroup RezTestPrim(string kind, Vector3 pos, Vector3 size)
        {
            PrimitiveBaseShape pbs;
            switch (kind)
            {
                case "sphere":   pbs = PrimitiveBaseShape.CreateSphere(); break;              // HalfCircle + Curve1
                case "cylinder": pbs = PrimitiveBaseShape.CreateBox();                        // start from Square+Straight (no-cut, scale 100)
                                 pbs.ProfileShape = ProfileShape.Circle; break;               // -> canonical cylinder: Circle + Straight
                case "prism":    pbs = PrimitiveBaseShape.CreateBox();                        // triangular section -> forces the mesher
                                 pbs.ProfileShape = ProfileShape.EquilateralTriangle; break;  // EquilateralTriangle + Straight, no asset
                default:         pbs = PrimitiveBaseShape.CreateBox(); break;                 // Square + Straight
            }

            UUID owner = _scene.RegionInfo.EstateSettings.EstateOwner;
            var sog = new SceneObjectGroup(owner, pos, Quaternion.Identity, pbs);
            sog.RootPart.Scale = size;   // AddPrimShape receives this as `size` (== SceneObjectPart.Scale)

            // attachToBackup:false -> ephemeral (no region-DB residue), but still physics-wired and
            // viewer-visible this session. AttachToScene calls ApplyPhysics synchronously here.
            _scene.AddNewSceneObject(sog, false);

            _testPrims.Add(new TestPrim
            {
                LocalId = sog.RootPart.LocalId,
                Sog = sog.UUID,
                Kind = kind,
                Pos = pos,
                Size = size,
            });
            return sog;
        }

        // Delete every console-rezzed test prim through the real scene-delete path (-> RemovePrim ->
        // backend RemoveBody/ReleaseShape). Returns how many were removed.
        private int ClearTestPrims()
        {
            int n = 0;
            foreach (var tp in _testPrims)
            {
                SceneObjectGroup sog = _scene?.GetSceneObjectGroup(tp.Sog);
                if (sog != null)
                {
                    _scene.DeleteSceneObject(sog, false);
                    n++;
                }
            }
            _testPrims.Clear();
            _drops.Clear();
            return n;
        }

        // Cast the proof rays through Scene.RayCastFiltered - the SAME call llCastRay makes - so this
        // is Jolt answering a real SL-facing raycast, just triggered from the console (no viewer/chat
        // dependency). Each row prints expected-vs-actual-vs-delta and which prim id was struck.
        private void RayPrims()
        {
            // Resolve the three ids for readability.
            uint boxId = 0, sphId = 0, cylId = 0;
            foreach (var tp in _testPrims)
            {
                if (tp.Kind == "box") boxId = tp.LocalId;
                else if (tp.Kind == "sphere") sphId = tp.LocalId;
                else if (tp.Kind == "cylinder") cylId = tp.LocalId;
            }

            const float sq75 = 0.8660254f;   // sqrt(1 - 0.5^2), the sphere offset-surface height
            const float cy = 128f;
            bool haveP = _testPrims.Count > 0;   // false after clearprims -> every row should MISS

            // label, origin, expectedZ (NaN = expect a MISS), expected prim id (0 = n/a), filter
            var rays = new (string label, Vector3 origin, float expZ, uint expId, RayFilterFlags filter)[]
            {
                ("box   top (face)",     new Vector3(120f,  cy, 107f), 102f,      boxId, RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("sphere top (centre)",  new Vector3(128f,  cy, 106f), 101f,      sphId, RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("sphere +0.5 (CURVE)",  new Vector3(128.5f,cy, 106f), 100f+sq75, sphId, RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("cyl top cap (AXIS)",   new Vector3(136f,   cy,    107f), 102f,      cylId, RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("cyl diag .4,.4 ROUND", new Vector3(136.4f, cy+0.4f,107f), float.NaN, 0u,    RayFilterFlags.land | RayFilterFlags.nonphysical),
                ("box, STATIC excluded", new Vector3(120f,  cy, 107f), float.NaN, 0u,    RayFilterFlags.land),
            };

            MainConsole.Instance.Output($"{LogHeader} rayprims via Scene.RayCastFiltered (the llCastRay pipeline) - {_testPrims.Count} test prim(s) live{(haveP ? "" : " -> EVERY row should MISS")}.");
            MainConsole.Instance.Output($"  CURVE proves sphere-surface-not-bbox; AXIS proves the cylinder Z-height correction. (NaN exp = expect miss.)");
            MainConsole.Instance.Output($"     label            |   exp z  |  act z   |  delta  | hit id | note");
            foreach (var r in rays)
            {
                var dir = new Vector3(0f, 0f, -1f);
                var hits = _scene.RayCastFiltered(r.origin, dir, 10f, 4, r.filter) as List<ContactResult>;
                ContactResult? best = null;
                if (hits != null)
                    foreach (var h in hits)
                        if (best == null || h.Depth < best.Value.Depth) best = h;

                // After clearprims there are no prims, so a hit-expecting row should now miss.
                bool expMiss = float.IsNaN(r.expZ) || !haveP;
                if (best == null)
                {
                    string ok = expMiss ? "OK (miss)" : "MISS (expected hit!)";
                    MainConsole.Instance.Output($"  {r.label,-20} | {(float.IsNaN(r.expZ) ? "  miss  " : r.expZ.ToString("0.000")),8} |   miss   |    -    |   -    | {ok}");
                }
                else
                {
                    float az = best.Value.Pos.Z;
                    string del = float.IsNaN(r.expZ) ? "   -    " : $"{az - r.expZ,7:0.000}";
                    string note = expMiss ? "hit (expected MISS!)"
                                 : (best.Value.ConsumerID == r.expId ? "OK" : $"WRONG id (want {r.expId})");
                    MainConsole.Instance.Output($"  {r.label,-20} | {(float.IsNaN(r.expZ) ? "  miss  " : r.expZ.ToString("0.000")),8} | {az,8:0.000} | {del} | {best.Value.ConsumerID,6} | {note}");
                }
            }
            MainConsole.Instance.Output($"  CURVE row exp {100f + sq75:0.000} (bbox would read 101.000); AXIS row exp 102.000 (wrong Y-axis cylinder -> 100.500);");
            MainConsole.Instance.Output($"  ROUND row exp miss (offset 0.566 > radius 0.5; a bbox fallback would instead HIT ~102.000, proving the cross-section is circular).");
        }

        // Closest hit of a single downward-ish cast through the real llCastRay pipeline. null = miss.
        private ContactResult? CastOne(Vector3 origin, Vector3 dir, float length, RayFilterFlags filter)
        {
            var res = _scene.RayCastFiltered(origin, dir, length, 4, filter) as List<ContactResult>;
            ContactResult? best = null;
            if (res != null)
                foreach (var h in res)
                    if (best == null || h.Depth < best.Value.Depth) best = h;
            return best;
        }

        // PERMANENT REGRESSION GUARD for the mesher cache-poisoning bug (delta #38). Rezzes N SEPARATE
        // identical prisms back-to-back: same size/lod -> same Meshmerizer cache key -> the exact
        // repeated-content path that a region with N copies of one mesh asset hits at M6.5. Pre-fix,
        // prim 2..N would get the poisoned shared Mesh and cook to bbox(fallback) (the NotSupportedException
        // now caught by the guard); post-fix, every prim cooks to mesh(mesher). Each is also cast-verified
        // as a real triangle (centre HIT, corner MISS) - not a bbox.
        private void RezMeshN(int count)
        {
            ClearTestPrims();
            var size = new Vector3(4f, 4f, 3f);
            for (int k = 0; k < count; k++)
                RezTestPrim("prism", new Vector3(120f + k * 8f, 128f, 100f), size);

            MainConsole.Instance.Output($"{LogHeader} rezzed {count} IDENTICAL prisms (size {size.X}x{size.Y}x{size.Z} -> same Meshmerizer cache key). Per-prim cook + cast:");
            var filter = RayFilterFlags.land | RayFilterFlags.nonphysical;
            var dir = new Vector3(0f, 0f, -1f);
            int clean = 0, realMesh = 0;
            for (int k = 0; k < _testPrims.Count; k++)
            {
                TestPrim tp = _testPrims[k];
                string kind = "?";
                lock (_prims)
                    if (_prims.TryGetValue(tp.LocalId, out JoltPrim jp)) kind = jp.ShapeKind;
                if (kind == "mesh(mesher)") clean++;

                ContactResult? cHit = CastOne(new Vector3(tp.Pos.X, tp.Pos.Y, 106f), dir, 10f, filter);
                ContactResult? kMiss = CastOne(new Vector3(tp.Pos.X + 1.8f, tp.Pos.Y + 1.8f, 106f), dir, 10f, filter);
                bool triProven = cHit.HasValue && !kMiss.HasValue;   // centre hit + corner miss => real triangle
                if (triProven) realMesh++;

                string centre = cHit.HasValue ? $"HIT@{cHit.Value.Pos.Z:0.00}" : "miss";
                string corner = kMiss.HasValue ? $"HIT@{kMiss.Value.Pos.Z:0.00}" : "miss";
                MainConsole.Instance.Output($"  prim {k + 1,-2} id={tp.LocalId,-6} kind={kind,-13} centre={centre,-11} corner={corner,-11} {(triProven ? "real-triangle" : "NOT-triangle")}");
            }

            bool pass = clean == count && realMesh == count;
            MainConsole.Instance.Output($"  {clean}/{count} cooked clean (mesh(mesher), cache NOT poisoned); {realMesh}/{count} cast-verified real triangle (centre hit + corner miss).");
            MainConsole.Instance.Output(pass
                ? $"  PASS: {count}/{count} repeated identical mesh cooks are clean - delta #38 (cache poisoning) stays fixed."
                : $"  FAIL: a prim fell to bbox/failed - cache poisoning or cook regression. Investigate before shipping.");
            MainConsole.Instance.Output($"  (jolt clearprims then jolt raymesh -> all miss.)");
        }

        // M6.4: rez a prim NON-physical via the real path, then flip it physical through OpenSim's own
        // ScriptSetPhysicsStatus (-> the actor's IsPhysical setter -> recreate Dynamic, delta #15) so it
        // FALLS. Probes terrain at the drop XY to pick a modest drop height (no tunnelling) and the
        // expected rest Z. A physical MESH (prism) recreates to a convex HULL - the load-bearing case.
        private void DropOne(string kind, Vector3 size, float x, float y)
        {
            float terrainZ = 20f;
            if (_backend.RayCast(new SVector3(x, y, 5000f), new SVector3(0f, 0f, -1f), 10000f, QueryFilter.Terrain, out RayHit th))
                terrainZ = th.Point.Z;
            float dropZ = terrainZ + 15f;

            SceneObjectGroup sog = RezTestPrim(kind, new Vector3(x, y, dropZ), size);
            if (sog == null) { MainConsole.Instance.Output($"{LogHeader} rez failed."); return; }

            sog.ScriptSetPhysicsStatus(true);   // OpenSim's real physics toggle -> IsPhysical setter -> Dynamic

            uint id = sog.RootPart.LocalId;
            string shapeNow = "?";
            lock (_prims)
                if (_prims.TryGetValue(id, out JoltPrim jp)) shapeNow = jp.ShapeKind;

            bool isBox = kind == "box";
            // Mass basis: box = exact box volume; mesh hull = enclosed mesh volume (== convex-hull volume
            // for the convex prism), captured from the cook. Both x density 1000 (BodyDesc.Default).
            float volume = isBox ? size.X * size.Y * size.Z : _lastMeshStats.Volume;
            float expMass = volume * 1000f;

            _drops.Add(new DropTrack
            {
                LocalId = id,
                Kind = isBox ? "box" : "mesh",
                StartZ = dropZ,
                StartStep = _stepCount,
                ExpectedMass = expMass,
                ExpectedRestZ = terrainZ + size.Z * 0.5f,
            });

            _logStepsUntil = _stepCount + 25;   // log the next ~25 Simulate frames (dt / active / liveZ)

            MainConsole.Instance.Output($"{LogHeader} dropped physical {kind} id={id} shape={shapeNow} from z={dropZ:0.00} (terrain {terrainZ:0.00}) at ({x:0},{y:0}).");
            MainConsole.Instance.Output($"  expected: mass~={expMass:0} kg (volume {volume:0.000} x 1000), rest z~={terrainZ + size.Z * 0.5f:0.00}. WATCH the viewer, then: jolt dropstatus");
        }

        // Update a tracked drop from a drained BodyState (called in the Simulate drain).
        private void UpdateDropTelemetry(in BodyState bs)
        {
            foreach (DropTrack t in _drops)
            {
                if (t.LocalId != bs.UserData) continue;
                float z = bs.Position.Z;
                t.LastZ = z;
                if (z < t.MinZ) t.MinZ = z;
                t.LastSpeed = bs.LinearVelocity.Length();
                if ((bs.Flags & BodyStateFlags.JustDeactivated) != 0)
                {
                    t.JustDeactivatedCount++;    // must be EXACTLY 1 at rest (the settle update)
                    t.RestZ = z;
                    t.RestStep = _stepCount;
                }
                break;
            }
        }

        // Report each tracked drop: fell / rested / JustDeactivated-exactly-once / steps-to-rest / rest Z
        // vs expected / mass / determinism vs the previous same-kind drop. This is the rigorous console gate
        // behind the viewer watch.
        private void DropStatus()
        {
            if (_drops.Count == 0) { MainConsole.Instance.Output($"{LogHeader} no active drops - run jolt droptest / jolt dropmesh first."); return; }

            // dropstatus is a SINGLE-INSTANT snapshot. Read once, right after `droptest`, it can catch the
            // body still spawning/mid-air and (M6.4 delta) mislabel a healthy fall as a stall. The sim thread
            // keeps stepping and updating each DropTrack while this console-thread handler blocks, so auto-wait
            // until every tracked drop has rested (JustDeactivated -> RestZ set) or a hard timeout elapses,
            // BEFORE printing PASS/FAIL. [dropframe] remains the honest continuous per-frame trace.
            const int settleTimeoutMs = 4000, pollMs = 100;
            int waitedMs = 0;
            while (waitedMs < settleTimeoutMs && _drops.Exists(d => float.IsNaN(d.RestZ)))
            {
                System.Threading.Thread.Sleep(pollMs);
                waitedMs += pollMs;
            }
            if (waitedMs > 0)
                MainConsole.Instance.Output($"{LogHeader} waited {waitedMs} ms for drops to settle before reading.");

            MainConsole.Instance.Output($"{LogHeader} drop status (step {_stepCount}, ActiveBodyCount now={_lastActiveBodyCount}):");
            foreach (DropTrack t in _drops)
            {
                string shapeNow = "?";
                string live = "no body";
                bool haveLive = false;
                float liveZ = float.NaN, liveVz = float.NaN;
                lock (_prims)
                    if (_prims.TryGetValue(t.LocalId, out JoltPrim jp))
                    {
                        shapeNow = jp.ShapeKind;
                        // Ground truth from Jolt: is the body active, and where is it NOW? Distinguishes
                        // Static/asleep (active=N, liveZ==startZ) from active-falling (active=Y, liveZ<startZ)
                        // from fell-through-terrain (liveZ << terrain).
                        if (_backend.TryGetBodyState(jp.BodyHandle, out BodyState st))
                        {
                            haveLive = true;
                            liveZ = st.Position.Z;
                            liveVz = st.LinearVelocity.Z;
                            live = $"joltActive={(((st.Flags & BodyStateFlags.Active) != 0) ? "Y" : "N")} liveZ={liveZ:0.000}";
                        }
                    }

                bool fell = (t.StartZ - t.MinZ) > 0.5f;
                bool rested = t.JustDeactivatedCount >= 1;
                long steps = t.RestStep >= 0 ? t.RestStep - t.StartStep : -1;
                float restErr = float.IsNaN(t.RestZ) ? float.NaN : t.RestZ - t.ExpectedRestZ;

                float prevRest = t.Kind == "box" ? _lastBoxRestZ : _lastMeshRestZ;
                // Not yet rested after the settle wait is NOT a failure - it means the body is still in
                // motion. Report it as such (live Z + vertical velocity) so an early/incomplete read can
                // never be misread as "hung". Only a truly rested drop feeds the determinism compare.
                string det = float.IsNaN(t.RestZ)
                    ? (haveLive ? $"still falling (liveZ={liveZ:0.000}, vZ={liveVz:0.000})" : "still falling (no live body)")
                    : (float.IsNaN(prevRest) ? "first drop (re-run to compare)" : $"det dZ={t.RestZ - prevRest:0.0000} vs previous {t.Kind}");

                MainConsole.Instance.Output($"  {t.Kind,-4} id={t.LocalId,-6} shape={shapeNow,-12} startZ={t.StartZ:0.00} [{live}]");
                MainConsole.Instance.Output($"        fell={(fell ? "Y" : "N")} rested={(rested ? "Y" : "N")} JustDeactivated={t.JustDeactivatedCount}(want 1) steps-to-rest={steps}");
                MainConsole.Instance.Output($"        restZ={t.RestZ:0.000} exp={t.ExpectedRestZ:0.000} dErr={restErr:0.000} speed={t.LastSpeed:0.000} mass~={t.ExpectedMass:0} kg  [{det}]");
            }
            // Record rest Z for the next-run determinism compare.
            foreach (DropTrack t in _drops)
                if (!float.IsNaN(t.RestZ))
                {
                    if (t.Kind == "box") _lastBoxRestZ = t.RestZ;
                    else _lastMeshRestZ = t.RestZ;
                }
            MainConsole.Instance.Output($"  PASS/row: fell=Y, rested=Y, JustDeactivated=1 (exactly once), dErr~0, mass>0. Re-run droptest+dropstatus -> det dZ ~ 0 (determinism).");
        }

        // The canonical triangular-prism PrimitiveBaseShape (EquilateralTriangle + Straight) used by the
        // mesh proof - shared by the real rez and the inline decision-point check.
        private static PrimitiveBaseShape GetPrismPbs()
        {
            PrimitiveBaseShape pbs = PrimitiveBaseShape.CreateBox();
            pbs.ProfileShape = ProfileShape.EquilateralTriangle;
            return pbs;
        }

        // Grid-cast the meshed prism at (120,128,100) size (4,4,3): a triangular top face at z=101.5 that
        // does NOT fill its 4x4 bbox. Centre is inside the triangle (HIT ~101.5); at least one bbox corner
        // is empty (MISS). A bounding box (or basic fallback) would HIT all five - so a corner miss with a
        // centre hit proves Jolt is colliding the ACTUAL triangle surface, not the bbox.
        private void RayMesh()
        {
            const float cx = 120f, cy = 128f;
            bool haveP = _testPrims.Count > 0;
            var pts = new (string label, float x, float y)[]
            {
                ("centre",       cx,        cy       ),
                ("corner +X+Y",  cx + 1.8f, cy + 1.8f),
                ("corner -X+Y",  cx - 1.8f, cy + 1.8f),
                ("corner +X-Y",  cx + 1.8f, cy - 1.8f),
                ("corner -X-Y",  cx - 1.8f, cy - 1.8f),
            };
            MainConsole.Instance.Output($"{LogHeader} raymesh grid on the prism (via Scene.RayCastFiltered) - {_testPrims.Count} test prim(s) live{(haveP ? "" : " -> expect all MISS")}. bbox top would be z=101.5 everywhere:");
            MainConsole.Instance.Output($"     point        |  act z   | hit id | note");
            int hits = 0, misses = 0;
            var dir = new Vector3(0f, 0f, -1f);
            var filter = RayFilterFlags.land | RayFilterFlags.nonphysical;
            foreach (var p in pts)
            {
                var origin = new Vector3(p.x, p.y, 106f);
                var res = _scene.RayCastFiltered(origin, dir, 10f, 4, filter) as List<ContactResult>;
                ContactResult? best = null;
                if (res != null)
                    foreach (var h in res)
                        if (best == null || h.Depth < best.Value.Depth) best = h;
                if (best == null)
                {
                    misses++;
                    MainConsole.Instance.Output($"  {p.label,-12} |   miss   |   -    | empty here (bbox would HIT 101.5)");
                }
                else
                {
                    hits++;
                    MainConsole.Instance.Output($"  {p.label,-12} | {best.Value.Pos.Z,8:0.000} | {best.Value.ConsumerID,6} | hit real surface");
                }
            }
            MainConsole.Instance.Output($"  -> {hits} hit / {misses} miss. PASS = centre HITs ~101.5 AND corner(s) MISS. A triangle cannot cover all 4 bbox corners");
            MainConsole.Instance.Output($"     (>=2 always empty, orientation-independent), so a box/bbox fallback would hit all 5 - corner misses prove the real triangle surface.");
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

        public override void RemovePrim(PhysicsActor prim)
        {
            if (prim is JoltPrim jp)
            {
                jp.Destroy();
                lock (_prims)
                    _prims.Remove(jp.LocalID);
            }
        }

        // The real OpenSim delivery boundary: SceneObjectPart.AddToPhysics -> (via the base
        // isPhantom/shapetype overloads) -> this. A non-physical, non-phantom prim becomes a STATIC
        // Jolt body. (Pure phantoms never reach here - ApplyPhysics skips them; physical dynamics is M6.4.)
        public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                                  Vector3 size, Quaternion rotation, bool isPhysical, uint localid)
        {
            if (_backend == null || pbs == null)
                return PhysicsActor.Null;

            // Defence in depth: the cook path is throw-free (CookPrimShape always returns a valid shape -
            // fast-path, mesh/hull, or bbox fallback), but if body creation ever throws we accept-and-ignore
            // so one bad prim can never abort a whole region load. Returns PhysicsActor.Null on failure.
            JoltPrim prim;
            try
            {
                prim = new JoltPrim(this, _backend, localid, primName, pbs, position, size, rotation, isPhysical);
            }
            catch (Exception e)
            {
                m_log.Warn($"{LogHeader} AddPrimShape failed for '{primName}' (localid {localid}): {e.GetType().Name}: {e.Message}; prim has no physics.");
                return PhysicsActor.Null;
            }
            lock (_prims)
                _prims[localid] = prim;
            return prim;
        }

        // Fixed-shape fast path (M6.3 Task 1): an UN-CUT box / sphere / cylinder cooks straight to a
        // Jolt primitive with NO meshmerizer. Classification matches what a real viewer/OAR prim
        // carries (canonical ProfileShape+Extrusion), NOT PrimitiveBaseShape.CreateCylinder() - whose
        // factory emits Square+Curve1 (an SL "tube"), a known OpenSim quirk. Anything else (cut/hollow/
        // twisted, sculpt/mesh, non-uniform sphere/cylinder) falls back to a bounding box for now; the
        // real IMesher path is M6.3 Task 2. `axisCorrection` (System.Numerics) is folded into the body
        // orientation by JoltPrim; `kind` is for the proof read-out.
        internal ShapeId CookPrimShape(PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out SQuaternion axisCorrection, out string kind)
        {
            axisCorrection = SQuaternion.Identity;
            float hx = size.X * 0.5f, hy = size.Y * 0.5f, hz = size.Z * 0.5f;

            if (pbs != null && PrimHasNoCuts(pbs))
            {
                byte path = pbs.PathCurve;
                ProfileShape profile = pbs.ProfileShape;

                // BOX: square profile, straight extrusion. Half-extents = size/2.
                if (profile == ProfileShape.Square && path == (byte)Extrusion.Straight)
                {
                    kind = "box";
                    return _backend.CreateBoxShape(new SVector3(hx, hy, hz));
                }

                // SPHERE: half-circle profile, curve1 extrusion. Native sphere only when uniform - a
                // non-uniform "sphere" is an ellipsoid and must go through the mesher (Task 2).
                if (profile == ProfileShape.HalfCircle && path == (byte)Extrusion.Curve1
                    && Approx(size.X, size.Y) && Approx(size.Y, size.Z))
                {
                    kind = "sphere";
                    return _backend.CreateSphereShape(hx);
                }

                // CYLINDER: circle profile, straight extrusion. SL cylinders are Z-height; Jolt's
                // CylinderShape axis is Y, so correct +90 deg about X (local Y -> local Z) before the
                // prim's own rotation. Circular cross-section only (X==Y); elliptical -> mesher.
                if (profile == ProfileShape.Circle && path == (byte)Extrusion.Straight
                    && Approx(size.X, size.Y))
                {
                    kind = "cylinder";
                    axisCorrection = SQuaternion.CreateFromAxisAngle(SVector3.UnitX, MathF.PI * 0.5f);
                    return _backend.CreateCylinderShape(hz, hx);   // halfHeight=Z/2, radius=X/2
                }
            }

            // Not a basic fast-path shape (cut/hollow/twisted, prism, torus, sculpt, mesh): go through
            // the meshmerizer (M6.3 Task 2). The convex-vs-mesh decision lives HERE - our equivalent of
            // BulletSim's BSShapeCollection.CreateGeomMeshOrHull (physical && ShouldUseHulls -> hull;
            // else mesh). Contract (delta #31): a triangle MeshShape has Volume 0, so a PHYSICAL prim
            // MUST use the convex hull or it would rez with mass 0 at M6.4 - hence physical -> hull here.
            ShapeId cooked = CookMeshShape(pbs, size, isPhysical, out kind);
            if (cooked.IsValid)
                return cooked;

            // Mesher unavailable / returned nothing usable / cook threw: conservative solid bounding box.
            kind = "bbox(fallback)";
            return _backend.CreateBoxShape(new SVector3(hx, hy, hz));
        }

        // The IMesher path: PrimitiveBaseShape -> IMesher.CreateMesh -> getVertexListAsFloat /
        // getIndexListAsInt -> CreateMeshShape (non-physical triangle mesh) or CreateConvexHullShape
        // (physical hull). Returns ShapeId.Invalid on any failure so the caller can fall back. Also
        // stashes a characterization of the RAW mesher output (_lastMeshStats) for the proof read-out.
        private ShapeId CookMeshShape(PrimitiveBaseShape pbs, Vector3 size, bool isPhysical, out string kind)
        {
            kind = "bbox(fallback)";
            if (m_mesher == null)
            {
                m_log.Warn($"{LogHeader} no IMesher - cannot cook mesh; bounding-box fallback.");
                return ShapeId.Invalid;
            }

            // ---- Extract geometry. CRITICAL: the Meshmerizer CACHES and SHARES the Mesh object,
            // keyed on GetMeshKey(size, lod), and returns the SAME instance for every identical prim
            // (key ignores isPhysical/convex). getIndexListAsInt()/getVertexListAsFloat() throw
            // NotSupportedException once m_triangles/m_vertices are null, and releaseSourceMeshData()
            // nulls exactly those - so calling it POISONS the cache and makes the NEXT identical prim's
            // extraction throw. Both accessors already return FRESH COPIES, so we own the arrays and must
            // NOT mutate/release the shared mesh (ReleaseMesh is a no-op anyway; the mesher owns eviction).
            // Everything the mesher/extraction can throw is inside ONE guard -> a clean bbox fallback,
            // never a propagating exception that could abort a prim rez or (at 6.5) a whole region load.
            SVector3[] points;
            int[] indices;
            try
            {
                // isPhysical:false to the mesher = "do not substitute a bounding box for tiny prims" -
                // we always want the real triangle soup (BulletSim passes false here for the same reason).
                IMesh mesh = m_mesher.CreateMesh("legionjolt-prim", pbs, size, MeshLod, false, false, false);
                if (mesh == null)
                {
                    // A sculpt whose asset (texture) has not been fetched meshes to null - it needs the
                    // async asset path (M6 request-asset delegate) first. Bounding box for now.
                    m_log.Debug($"{LogHeader} IMesher returned null (unfetched sculpt asset or empty geometry); bounding-box fallback.");
                    return ShapeId.Invalid;
                }

                indices = mesh.getIndexListAsInt();          // fresh copy - do NOT release the shared mesh
                float[] verts = mesh.getVertexListAsFloat(); // fresh copy (flattened x,y,z,...)
                if (verts == null || indices == null || verts.Length < 12 || indices.Length < 3 || (indices.Length % 3) != 0)
                {
                    m_log.Warn($"{LogHeader} mesher geometry unusable (verts={verts?.Length ?? 0}, indices={indices?.Length ?? 0}); bounding-box fallback.");
                    return ShapeId.Invalid;
                }

                points = new SVector3[verts.Length / 3];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new SVector3(verts[3 * i], verts[3 * i + 1], verts[3 * i + 2]);
            }
            catch (Exception e)
            {
                m_log.Warn($"{LogHeader} mesher geometry extraction threw ({e.GetType().Name}: {e.Message}); bounding-box fallback.");
                return ShapeId.Invalid;
            }

            _lastMeshStats = CharacterizeMesh(points, indices);   // honest read-out of REAL mesher output

            // Cook the Jolt shape. No shape/body exists until one of these RETURNS a handle, so a throw
            // here creates nothing to leak - caller falls back to a full bbox.
            try
            {
                ShapeId shape = isPhysical
                    ? _backend.CreateConvexHullShape(points)   // physical: hull (mesh Volume=0 -> mass 0; delta #31)
                    : _backend.CreateMeshShape(points, indices); // non-physical: real triangle mesh
                kind = isPhysical ? "hull(mesher)" : "mesh(mesher)";
                return shape;
            }
            catch (Exception e)
            {
                m_log.Warn($"{LogHeader} backend cook of mesher output threw ({e.GetType().Name}: {e.Message}); bounding-box fallback.");
                return ShapeId.Invalid;   // kind stays "bbox(fallback)"
            }
        }

        // Characterize RAW mesher output: what real geometry looks like vs the clean-room synthetic
        // tetra. Duplicate-vertex count uses mm-quantized coords (O(n)); degenerate = topological
        // (shared index) or near-zero area.
        private static MeshStats CharacterizeMesh(SVector3[] points, int[] indices)
        {
            var s = new MeshStats { Verts = points.Length, Tris = indices.Length / 3 };
            var min = new SVector3(float.MaxValue); var max = new SVector3(float.MinValue);
            foreach (var p in points) { min = SVector3.Min(min, p); max = SVector3.Max(max, p); }
            s.Min = min; s.Max = max;

            var seen = new HashSet<(int, int, int)>();
            foreach (var p in points)
                seen.Add(((int)MathF.Round(p.X * 1000f), (int)MathF.Round(p.Y * 1000f), (int)MathF.Round(p.Z * 1000f)));
            s.DuplicateVerts = points.Length - seen.Count;

            double vol6 = 0.0;   // 6x the signed enclosed volume: sum of dot(v0, cross(v1,v2)) over tris
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                bool bad = a < 0 || b < 0 || c < 0 || a >= points.Length || b >= points.Length || c >= points.Length;
                if (bad) { s.OutOfRangeIndices++; continue; }
                if (a == b || b == c || a == c) { s.DegenerateTris++; continue; }
                float area2 = SVector3.Cross(points[b] - points[a], points[c] - points[a]).Length();
                if (area2 < 1e-9f) s.DegenerateTris++;
                vol6 += SVector3.Dot(points[a], SVector3.Cross(points[b], points[c]));
            }
            // For a closed, consistently-wound mesh (prim mesher output) this is the exact enclosed volume,
            // which equals the convex-hull volume for a convex shape (prism) - i.e. the physical hull mass basis.
            s.Volume = (float)(Math.Abs(vol6) / 6.0);
            return s;
        }

        // BulletSim's cut test, verbatim: an un-cut basic shape has no profile/path cut, hollow, twist,
        // taper, non-100 path scale, or shear. (PathScaleX/Y are stored as 100 = "1.0".)
        private static bool PrimHasNoCuts(PrimitiveBaseShape p) =>
            p.ProfileBegin == 0 && p.ProfileEnd == 0 && p.ProfileHollow == 0 &&
            p.PathTwist == 0 && p.PathTwistBegin == 0 && p.PathBegin == 0 && p.PathEnd == 0 &&
            p.PathTaperX == 0 && p.PathTaperY == 0 && p.PathScaleX == 100 && p.PathScaleY == 100 &&
            p.PathShearX == 0 && p.PathShearY == 0;

        private static bool Approx(float a, float b) =>
            Math.Abs(a - b) <= 1e-4f * Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));

        // ---------------------------------------------------------------------
        // Query wiring pulled forward for the M6.3 proof: this is the path a SCRIPT llCastRay takes.
        // llCastRay -> Scene.RayCastFiltered -> PhysicsScene.RaycastWorld (here) -> backend.RayCast.
        // Returning true from SupportsRaycastWorldFiltered flips llCastRay onto the physics engine
        // instead of OpenSim's own geometry intersection, so a script ray genuinely tests Jolt's
        // shapes. (Full query family - RaycastActor, Sphere/BoxProbe for llSensor - remains M6.7.)
        // ---------------------------------------------------------------------

        public override bool SupportsRaycastWorldFiltered() => true;

        public override object RaycastWorld(Vector3 position, Vector3 direction, float length, int Count, RayFilterFlags filter)
        {
            var results = new List<ContactResult>();
            if (_backend == null)
                return results;

            QueryFilter qf = ToQueryFilter(filter);
            if (qf == QueryFilter.None)
                return results;

            Vector3 dn = direction;
            dn.Normalize();
            var origin = new SVector3(position.X, position.Y, position.Z);
            var dir = new SVector3(dn.X, dn.Y, dn.Z);

            int want = Count > 0 ? Count : 1;
            var hits = new RayHit[want];
            int n = _backend.RayCastAll(origin, dir, length, qf, hits);
            for (int i = 0; i < n; i++)
            {
                results.Add(new ContactResult
                {
                    ConsumerID = hits[i].UserData,           // SceneObjectPart.LocalId
                    Pos = new Vector3(hits[i].Point.X, hits[i].Point.Y, hits[i].Point.Z),
                    Normal = new Vector3(hits[i].Normal.X, hits[i].Normal.Y, hits[i].Normal.Z),
                    Depth = hits[i].Distance,
                });
            }
            return results;   // boxed as object; llCastRay casts back to List<ContactResult>
        }

        // llCastRay's reject-type flags -> our layer filter. water has no body; phantom/volumedetect
        // map to the Sensor layer (M6.6).
        private static QueryFilter ToQueryFilter(RayFilterFlags f)
        {
            QueryFilter q = QueryFilter.None;
            if ((f & RayFilterFlags.land) != 0) q |= QueryFilter.Terrain;
            if ((f & RayFilterFlags.nonphysical) != 0) q |= QueryFilter.Static;
            if ((f & RayFilterFlags.physical) != 0) q |= QueryFilter.Dynamic;
            if ((f & RayFilterFlags.agent) != 0) q |= QueryFilter.Avatar;
            if ((f & (RayFilterFlags.phantom | RayFilterFlags.volumedtc)) != 0) q |= QueryFilter.Sensor;
            return q;
        }

        public override float Simulate(float timeStep)
        {
            if (_backend == null)
                return 1f;

            // Step, then DRAIN: the backend fills _bodyBuf with a BodyState per ACTIVE body (moving prims)
            // plus one final JustDeactivated state per body that slept this step. For each, push the new
            // transform/velocity into the matching actor (by UserData = LocalID) and fire its terse update
            // so the viewer sees motion; the JustDeactivated state is the settle update that stops a rested
            // object drifting. Sleeping bodies aren't reported, so idle prims cost nothing.
            StepResult r = _backend.Step(timeStep, _bodyBuf, _charBuf, _contactBuf);
            _stepCount++;
            _lastActiveBodyCount = r.ActiveBodyCount;

            // Windowed per-frame diagnostic (set by a drop): is Step advancing with a REAL dt, is the
            // just-dropped body in our active set, and is its Z actually changing? This is the definitive
            // read on the "1st drop works, 2nd hangs" pattern - dt=0 => idle-step stall; active=1 but
            // liveZ frozen => body active-but-not-integrated (deeper); active=0 => activation lost.
            if (_stepCount <= _logStepsUntil && _drops.Count > 0)
            {
                DropTrack td = _drops[_drops.Count - 1];
                float lz = float.NaN, vz = float.NaN; bool ja = false;
                lock (_prims)
                    if (_prims.TryGetValue(td.LocalId, out JoltPrim jd) && _backend.TryGetBodyState(jd.BodyHandle, out BodyState sd))
                    { lz = sd.Position.Z; vz = sd.LinearVelocity.Z; ja = (sd.Flags & BodyStateFlags.Active) != 0; }
                m_log.Debug($"{LogHeader} [dropframe] step={_stepCount} dt={timeStep:0.0000} active={r.ActiveBodyCount} updates={r.BodyUpdateCount} box(id={td.LocalId}) liveZ={lz:0.000} vZ={vz:0.000} joltActive={ja}");
            }

            if (r.BodyBufferOverflowed)
                m_log.Warn($"{LogHeader} body update buffer overflowed ({_bodyBuf.Length}); some terse updates dropped this step.");

            int n = r.BodyUpdateCount;
            for (int i = 0; i < n; i++)
            {
                BodyState bs = _bodyBuf[i];
                JoltPrim p;
                lock (_prims)
                    _prims.TryGetValue(bs.UserData, out p);
                p?.ApplyStepState(in bs);
                if (_drops.Count > 0)
                    UpdateDropTelemetry(in bs);
            }
            return 1f;
        }

        public override void SetTerrain(float[] heightMap)
        {
            if (_backend == null || heightMap == null)
                return;
            int sx = _regionSizeX, sy = _regionSizeY;
            if (sx <= 0 || sy <= 0 || heightMap.Length < sx * sy)
            {
                m_log.Warn($"{LogHeader} SetTerrain: heightMap length {heightMap?.Length ?? 0} < {sx}x{sy}; ignoring.");
                return;
            }

            // Build the (N+1)-square sample field (resolved varregion decision). A region of N metres ->
            // N+1 samples at 1 m spacing spans exactly [0, N] metres, so the far EDGE is covered (the
            // clean-room (N-1)*s finding: an N-sample field would fall 1 m short). The extra row/column
            // duplicate the last real sample (fetching the neighbour region's row 0 is the later
            // refinement). Non-square regions pad to max(sx,sy) square by edge replication.
            // OpenSim serialises heightMap[y*sx + x] = height at (x,y) - the SAME convention as
            // CreateHeightFieldShape, so it feeds through with no transpose (the Z-up wrapper + row-mirror
            // fix inside the backend do the rest).
            int m = Math.Max(sx, sy) + 1;
            float[] field = new float[m * m];
            for (int y = 0; y < m; y++)
            {
                int srcRow = Math.Min(y, sy - 1) * sx;
                int dstRow = y * m;
                for (int x = 0; x < m; x++)
                    field[dstRow + x] = heightMap[srcRow + Math.Min(x, sx - 1)];
            }

            // 1 m sample spacing, heights already in metres (unit height scale), origin at the region
            // corner (physics runs in region-local coords - decision #2).
            ShapeId newShape = _backend.CreateHeightFieldShape(field, m, m, new SVector3(1f, 1f, 1f));
            _backend.SetTerrain(newShape, SVector3.Zero);

            // Release the previous terrain shape: SetTerrain already replaced its body (dropping that
            // native ref), so releasing our handle frees it.
            if (_terrainShape.IsValid)
                _backend.ReleaseShape(_terrainShape);
            _terrainShape = newShape;

            m_log.Info($"{LogHeader} terrain set: {sx}x{sy} region -> {m}x{m} heightfield (spans {m - 1} m/side).");
        }

        public override void SetWaterLevel(float baseheight)
        {
            _backend?.SetWaterHeight(baseheight);
        }

        public override void DeleteTerrain()
        {
            if (_backend != null && _terrainShape.IsValid)
                _backend.ReleaseShape(_terrainShape);
            _terrainShape = ShapeId.Invalid;
        }

        public override Dictionary<uint, float> GetTopColliders() => new Dictionary<uint, float>();

        public override void Dispose()
        {
            _backend?.Dispose();   // backend teardown -> Foundation.Shutdown
            _backend = null;
        }
    }
}
