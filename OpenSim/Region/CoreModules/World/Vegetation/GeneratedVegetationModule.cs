/*
 * Legion Grid — procedural content generation (vegetation rezzer).
 *
 * Reads a vegetation plan JSON produced by tools/terrain-gen/vegetation_plan.py and
 * rezzes each tree into the console's CURRENT region via IVegetationModule.AddTree.
 * Every generated tree is stamped with a dedicated GroupID (GENERATED_VEG_GROUP) so
 * `vegetation clear-generated` can remove ONLY generator-placed content and can never
 * touch hand-placed objects.
 *
 * Console commands (operate on the `change region`-selected region):
 *   vegetation plant <planfile.json>   — rez the plan's trees (paced, progress/500)
 *   vegetation clear-generated         — delete only trees in GENERATED_VEG_GROUP
 *
 * Idempotence: plant twice = duplicates (each rez gets fresh UUIDs). The workflow is
 * clear-generated -> plant. See README "Vegetation pass".
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Vegetation
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule",
               Id = "GeneratedVegetationModule")]
    public class GeneratedVegetationModule : ISharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Dedicated group stamped on every generated tree. MUST match the planner's
        // --group-uuid default so plans read as generated content. clear-generated
        // targets THIS group only, so it can never remove hand-placed objects.
        private static readonly UUID GENERATED_VEG_GROUP =
            new UUID("a7e91d0c-9e00-4c11-8ecb-9a11e6470000");
        private const string NAME_PREFIX = "[genveg] ";

        // Pace the rez so thousands of AddNewSceneObject calls don't starve the
        // region heartbeat; report progress every PROGRESS_EVERY trees.
        private const int PACE_EVERY = 200;
        private const int PACE_SLEEP_MS = 40;
        private const int PROGRESS_EVERY = 500;

        private bool m_registered;

        #region ISharedRegionModule
        public string Name => "GeneratedVegetationModule";
        public Type ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void PostInitialise() { }
        public void Close() { }
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }

        public void AddRegion(Scene scene)
        {
            // Register the console commands once (shared module, single global command
            // table); the handlers act on SceneManager.Instance.CurrentScene.
            if (m_registered)
                return;
            m_registered = true;

            scene.AddCommand("Vegetation", this, "vegetation plant",
                "vegetation plant <planfile.json>",
                "Rez the trees in a vegetation plan JSON into the current region "
                + "(stamped as generated content).", HandlePlant);

            scene.AddCommand("Vegetation", this, "vegetation clear-generated",
                "vegetation clear-generated",
                "Remove ONLY generator-placed trees (group " + GENERATED_VEG_GROUP
                + ") from the current region. Hand-placed content is untouched.",
                HandleClear);
        }
        #endregion

        private static Scene CurrentScene()
        {
            return SceneManager.Instance == null ? null : SceneManager.Instance.CurrentScene;
        }

        private void HandlePlant(string module, string[] cmd)
        {
            Scene scene = CurrentScene();
            if (scene == null)
            {
                MainConsole.Instance.Output("No current region — use 'change region <name>' first.");
                return;
            }
            if (cmd.Length < 3)
            {
                MainConsole.Instance.Output("Usage: vegetation plant <planfile.json>");
                return;
            }

            string path = cmd[2];
            if (!File.Exists(path))
            {
                MainConsole.Instance.Output("Plan file not found: " + path);
                return;
            }

            IVegetationModule veg = scene.RequestModuleInterface<IVegetationModule>();
            if (veg == null)
            {
                MainConsole.Instance.Output("No IVegetationModule on this region.");
                return;
            }

            OSDMap plan;
            OSDArray trees;
            try
            {
                OSD root = OSDParser.DeserializeJson(File.ReadAllText(path));
                plan = (OSDMap)root;
                trees = (OSDArray)plan["trees"];
            }
            catch (Exception e)
            {
                MainConsole.Instance.Output("Failed to parse plan JSON: " + e.Message);
                return;
            }

            // Advisory: the plan's group_uuid should match ours (clear uses OURS).
            if (plan.ContainsKey("meta") && plan["meta"] is OSDMap meta
                && meta.ContainsKey("group_uuid"))
            {
                if (!UUID.TryParse(meta["group_uuid"].AsString(), out UUID g) || g != GENERATED_VEG_GROUP)
                    m_log.WarnFormat(
                        "[GENVEG]: plan group_uuid {0} != module GENERATED_VEG_GROUP {1}; "
                        + "planting under the module group so clear-generated still works.",
                        meta.ContainsKey("group_uuid") ? meta["group_uuid"].AsString() : "(none)",
                        GENERATED_VEG_GROUP);
            }

            UUID owner = scene.RegionInfo.EstateSettings.EstateOwner;
            float sx = scene.RegionInfo.RegionSizeX;
            float sy = scene.RegionInfo.RegionSizeY;

            int planted = 0, skipped = 0, total = trees.Count;
            MainConsole.Instance.Output(string.Format(
                "[GENVEG]: planting {0} trees into {1} …", total, scene.Name));

            for (int i = 0; i < total; i++)
            {
                OSDMap t = (OSDMap)trees[i];
                int code = t["code"].AsInteger();
                if (code < 0 || code > 20)                       // valid OpenMetaverse.Tree range
                {
                    skipped++;
                    continue;
                }

                float x = (float)t["x"].AsReal();
                float y = (float)t["y"].AsReal();
                float z = (float)t["z"].AsReal();
                if (x < 0f || x >= sx || y < 0f || y >= sy)      // out of region
                {
                    skipped++;
                    continue;
                }

                Vector3 scale = new Vector3(
                    (float)t["sx"].AsReal(), (float)t["sy"].AsReal(), (float)t["sz"].AsReal());
                float a = (float)t["rot"].AsReal();
                Quaternion rot = new Quaternion(       // rotation about +Z by angle a
                    0f, 0f, (float)Math.Sin(a * 0.5), (float)Math.Cos(a * 0.5));
                Vector3 pos = new Vector3(x, y, z);

                try
                {
                    SceneObjectGroup sog = veg.AddTree(
                        owner, GENERATED_VEG_GROUP, scale, rot, pos, (Tree)code, true);
                    if (sog != null)
                        sog.Name = NAME_PREFIX + (Tree)code;
                    else
                        skipped++;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[GENVEG]: AddTree failed at {0}: {1}", pos, e.Message);
                    skipped++;
                    continue;
                }

                planted++;
                if (planted % PROGRESS_EVERY == 0)
                    MainConsole.Instance.Output(string.Format(
                        "[GENVEG]:   {0}/{1} planted…", planted, total));
                if (planted % PACE_EVERY == 0)
                    Thread.Sleep(PACE_SLEEP_MS);                 // let the heartbeat breathe
            }

            MainConsole.Instance.Output(string.Format(
                "[GENVEG]: done — {0} planted, {1} skipped, into {2}. "
                + "(plant is NOT idempotent: clear-generated before re-planting.)",
                planted, skipped, scene.Name));
        }

        private void HandleClear(string module, string[] cmd)
        {
            Scene scene = CurrentScene();
            if (scene == null)
            {
                MainConsole.Instance.Output("No current region — use 'change region <name>' first.");
                return;
            }

            List<SceneObjectGroup> doomed = new List<SceneObjectGroup>();
            foreach (SceneObjectGroup g in scene.GetSceneObjectGroups())
            {
                if (g != null && !g.IsDeleted && g.GroupID == GENERATED_VEG_GROUP)
                    doomed.Add(g);
            }

            if (doomed.Count == 0)
            {
                MainConsole.Instance.Output(
                    "[GENVEG]: no generator-placed trees found in " + scene.Name + ".");
                return;
            }

            MainConsole.Instance.Output(string.Format(
                "[GENVEG]: removing {0} generated trees from {1} …", doomed.Count, scene.Name));
            int removed = 0;
            foreach (SceneObjectGroup g in doomed)
            {
                try
                {
                    scene.DeleteSceneObject(g, false);
                    removed++;
                    if (removed % PACE_EVERY == 0)
                        Thread.Sleep(PACE_SLEEP_MS);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[GENVEG]: delete failed for {0}: {1}", g.UUID, e.Message);
                }
            }
            MainConsole.Instance.Output(string.Format(
                "[GENVEG]: cleared {0} generated trees from {1}.", removed, scene.Name));
        }
    }
}
