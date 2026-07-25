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
                    "jolt terraintest | terrainslope | terrainhill | hilltest | probe <x> <y> | rezprims | rayprims | clearprims",
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

            if (cmd.Length >= 2 && cmd[1] == "clearprims")
            {
                int n = ClearTestPrims();
                MainConsole.Instance.Output($"{LogHeader} deleted {n} test prims (scene delete -> RemovePrim). `jolt rayprims` should now miss.");
                return;
            }

            MainConsole.Instance.Output("Usage: jolt terraintest | terrainslope | terrainhill | hilltest | probe <x> <y> | rezprims | rayprims | clearprims");
        }

        // Build one basic prim with a CANONICAL PrimitiveBaseShape (a real viewer/OAR prim's values,
        // not the quirky CreateCylinder factory) and rez it through the genuine scene path so OpenSim -
        // not us - calls AddPrimShape. Non-physical, non-phantom by default => a static Jolt body.
        private void RezTestPrim(string kind, Vector3 pos, Vector3 size)
        {
            PrimitiveBaseShape pbs;
            switch (kind)
            {
                case "sphere":   pbs = PrimitiveBaseShape.CreateSphere(); break;              // HalfCircle + Curve1
                case "cylinder": pbs = PrimitiveBaseShape.CreateBox();                        // start from Square+Straight (no-cut, scale 100)
                                 pbs.ProfileShape = ProfileShape.Circle; break;               // -> canonical cylinder: Circle + Straight
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

            var prim = new JoltPrim(this, _backend, localid, primName, pbs, position, size, rotation, isPhysical);
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
        internal ShapeId CookPrimShape(PrimitiveBaseShape pbs, Vector3 size, out SQuaternion axisCorrection, out string kind)
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

            // Fallback until the IMesher path lands (M6.3 Task 2): a conservative solid bounding box.
            kind = "bbox(fallback)";
            m_log.Debug($"{LogHeader} prim shape is not a basic un-cut box/sphere/cylinder - bounding-box fallback until the mesher (M6.3 Task 2).");
            return _backend.CreateBoxShape(new SVector3(hx, hy, hz));
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
            // Step the empty world: no actors, so the active-set drain fills nothing. This proves the
            // per-frame path runs without throwing. Returns one simulated frame.
            _backend?.Step(timeStep, _bodyBuf, _charBuf, _contactBuf);
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
