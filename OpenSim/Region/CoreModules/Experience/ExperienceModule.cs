/*
 * Legion Grid — Experience System
 * ExperienceModule.cs — Region module
 *
 * Place in: OpenSim/Region/CoreModules/Experience/ExperienceModule.cs
 *
 * This module:
 * - Initializes the ExperienceService with MySQL connection
 * - Registers it as IExperienceService for other modules to use
 * - Adds console commands for experience management
 * - Provides the bridge between scripts and the experience service
 *
 * Configuration in OpenSim.ini:
 *   [Experience]
 *   Enabled = true
 *   ConnectionString = "Data Source=localhost;Database=opensim;User ID=root;Password=xxx;"
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.ExperienceService;
using Caps = OpenSim.Framework.Capabilities.Caps;


namespace OpenSim.Region.CoreModules.Experience
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LegionExperienceModule")]
    public class ExperienceModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_Enabled = false;
        private string m_ConnectionString = string.Empty;
        private Scene m_Scene;
        private IExperienceService m_Service;

        // Parcel experience access-list flag: AL_BLOCK_EXPERIENCE = (1 << 4) = 16
        // (Firestorm indra/llinventory/llparcelflags.h). Stored raw in LandAccessEntry.Flags;
        // the experience UUID rides in LandAccessEntry.AgentID. Matches LandObject.IsExperienceBlocked.
        private const int AL_BLOCK_EXPERIENCE = 16;

        // In-memory permission cache for fast lookups during script execution
        private readonly Dictionary<string, bool> m_PermCache = new Dictionary<string, bool>();
        private readonly object m_PermCacheLock = new object();

        // Script-to-experience association: maps script ItemID → experience UUID
        private readonly Dictionary<UUID, UUID> m_ScriptExperiences = new Dictionary<UUID, UUID>();
        private readonly object m_ScriptExpLock = new object();

        public string Name => "LegionExperienceModule";
        public Type ReplaceableInterface => null;

        // ══════════════════════════════════════════════════════════════════
        // IRegionModuleBase
        // ══════════════════════════════════════════════════════════════════

        public void Initialise(IConfigSource config)
        {
            var experienceConfig = config.Configs["Experience"];
            if (experienceConfig == null)
            {
                m_log.Info("[ExperienceModule]: No [Experience] section in config, disabled");
                return;
            }

            m_Enabled = experienceConfig.GetBoolean("Enabled", false);
            if (!m_Enabled)
            {
                m_log.Info("[ExperienceModule]: Disabled in config");
                return;
            }

            m_ConnectionString = experienceConfig.GetString("ConnectionString", string.Empty);
            if (string.IsNullOrEmpty(m_ConnectionString))
            {
                m_log.Error("[ExperienceModule]: Enabled but no ConnectionString configured, disabling");
                m_Enabled = false;
                return;
            }

            m_log.Info("[ExperienceModule]: Initialized, will activate on region add");
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled) return;

            m_Scene = scene;

            // Create the service instance
            m_Service = new ExperienceService(m_ConnectionString);

            // Register as a scene module so scripts can find it
            scene.RegisterModuleInterface<IExperienceService>(m_Service);

            // Register the module itself for script-experience association lookups
            scene.RegisterModuleInterface<ExperienceModule>(this);

            // Register console commands
            RegisterConsoleCommands();

            // Advertise the experience capabilities the viewer probes (Slice 2). Their PRESENCE
            // makes Firestorm show the About Land > Experiences tab (FIRE-17280); the parcel
            // allow/block data itself still flows over the UDP ParcelAccessList path.
            scene.EventManager.OnRegisterCaps += OnRegisterCaps;

            m_log.InfoFormat("[ExperienceModule]: Experience system active for region '{0}'", scene.RegionInfo.RegionName);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled) return;
            // Could preload allowed/blocked experience lists here if needed
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled) return;
            scene.EventManager.OnRegisterCaps -= OnRegisterCaps;
            scene.UnregisterModuleInterface<IExperienceService>(m_Service);
            scene.UnregisterModuleInterface<ExperienceModule>(this);
        }

        // ══════════════════════════════════════════════════════════════════
        // Capabilities (Slice 2 — viewer-facing half)
        //   RegionExperiences     — presence gates the Land>Experiences tab; GET serves the
        //                           region allowed/blocked lists; POST is reject-shaped (below).
        //   GetExperienceInfo     — resolves experience UUID -> name/properties (so tab entries
        //                           show as NAMES); shape per Firestorm llexperiencecache.
        //   FindExperienceByName  — the add-picker's name search.
        // ══════════════════════════════════════════════════════════════════
        private void OnRegisterCaps(UUID agentID, Caps caps)
        {
            if (m_Service == null) return;

            caps.RegisterSimpleHandler("RegionExperiences",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (req, resp) => HandleRegionExperiences(req, resp)));

            caps.RegisterSimpleHandler("GetExperienceInfo",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (req, resp) => HandleGetExperienceInfo(req, resp)));

            caps.RegisterSimpleHandler("FindExperienceByName",
                new SimpleStreamHandler("/" + UUID.Random(),
                    (req, resp) => HandleFindExperienceByName(req, resp)));
        }

        private void WriteLLSD(IOSHttpResponse resp, OSD payload)
        {
            resp.RawBuffer = OSDParser.SerializeLLSDXmlToBytes(payload);
            resp.StatusCode = (int)HttpStatusCode.OK;
        }

        // Map our ExperienceInfo -> the viewer's experience_keys entry. The `properties` bitfield
        // is the VIEWER's namespace (llexperiencecache): PROPERTY_GRID=1<<4, PROPERTY_PRIVATE=1<<5,
        // PROPERTY_DISABLED=1<<6 — NOT our internal PROP_* bits. The add-picker filters on it, so
        // it must be accurate: set GRID when grid-wide, DISABLED only when not enabled.
        private OSDMap ExperienceToOSD(ExperienceInfo info)
        {
            const int VP_GRID = 1 << 4, VP_PRIVATE = 1 << 5, VP_DISABLED = 1 << 6;
            OSDMap m = new OSDMap();
            m["public_id"] = OSD.FromUUID(info.ExperienceId);
            m["name"] = OSD.FromString(info.Name ?? string.Empty);
            m["description"] = OSD.FromString(info.Description ?? string.Empty);
            int props = 0;
            if (info.IsGridWide) props |= VP_GRID;
            if (info.IsPrivate)  props |= VP_PRIVATE;
            if (!info.IsEnabled) props |= VP_DISABLED;
            m["properties"] = OSD.FromInteger(props);
            m["maturity"] = OSD.FromInteger(info.Maturity);
            m["quota"] = OSD.FromInteger(128);
            if (info.OwnerId != UUID.Zero) m["agent_id"] = OSD.FromUUID(info.OwnerId);
            if (info.GroupId != UUID.Zero) m["group_id"] = OSD.FromUUID(info.GroupId);
            if (!string.IsNullOrEmpty(info.Slurl)) m["slurl"] = OSD.FromString(info.Slurl);
            return m;
        }

        private OSDMap BuildRegionExperiencesLLSD(UUID regionId)
        {
            OSDMap m = new OSDMap();
            OSDArray allowed = new OSDArray();
            foreach (UUID id in m_Service.GetAllowedExperiences(regionId)) allowed.Add(OSD.FromUUID(id));
            OSDArray blocked = new OSDArray();
            foreach (UUID id in m_Service.GetBlockedExperiences(regionId)) blocked.Add(OSD.FromUUID(id));
            m["allowed"] = allowed;
            m["blocked"] = blocked;
            m["trusted"] = new OSDArray();   // estate-level "trusted" not modelled — honestly empty
            // "default" intentionally omitted (no default experience)
            return m;
        }

        private void HandleRegionExperiences(IOSHttpRequest req, IOSHttpResponse resp)
        {
            UUID regionId = m_Scene.RegionInfo.RegionID;
            if (req.HttpMethod == "POST")
            {
                // Region/estate experience MANAGEMENT (the Region-Info Experiences panel writing
                // allowed/blocked/trusted) is DEFERRED as its own future item — open permission
                // question: who may edit, estate owner only or also managers? We do NOT silently
                // accept-and-discard (that would lie to the panel: the edit appears to succeed then
                // vanishes — the DisplayNames silent-gate anti-pattern). Instead reject-shaped: echo
                // the CURRENT lists back so the panel re-renders the truth (the edit visibly reverts).
                m_log.DebugFormat(
                    "[ExperienceModule]: RegionExperiences POST rejected in '{0}' — region/estate experience management is deferred (Slice 2 target is the parcel Land tab).",
                    m_Scene.RegionInfo.RegionName);
                WriteLLSD(resp, BuildRegionExperiencesLLSD(regionId));
                return;
            }
            // GET — real region allowed/blocked (also makes the Region-Info panel read-correct).
            WriteLLSD(resp, BuildRegionExperiencesLLSD(regionId));
        }

        private void HandleGetExperienceInfo(IOSHttpRequest req, IOSHttpResponse resp)
        {
            // Viewer GETs <cap>/id/?page_size=N&<id>=uuid&<id>=uuid... — collect every query
            // value that parses as a UUID (robust to the exact param name), resolve each.
            OSDArray keys = new OSDArray();
            OSDArray errorIds = new OSDArray();
            var seen = new HashSet<UUID>();
            var qs = req.QueryString;
            if (qs != null)
            {
                foreach (string k in qs.AllKeys)
                {
                    if (k == null) continue;
                    string[] vals = qs.GetValues(k);
                    if (vals == null) continue;
                    foreach (string v in vals)
                    {
                        if (UUID.TryParse(v, out UUID id) && id != UUID.Zero && seen.Add(id))
                        {
                            ExperienceInfo info = m_Service.GetExperience(id);
                            if (info != null) keys.Add(ExperienceToOSD(info));
                            else errorIds.Add(OSD.FromUUID(id));
                        }
                    }
                }
            }
            OSDMap result = new OSDMap();
            result["experience_keys"] = keys;
            if (errorIds.Count > 0) result["error_ids"] = errorIds;
            WriteLLSD(resp, result);
        }

        private void HandleFindExperienceByName(IOSHttpRequest req, IOSHttpResponse resp)
        {
            var qs = req.QueryString;
            string query = (qs != null ? qs["query"] : null) ?? string.Empty;
            int page = 0, pageSize = 30;
            if (qs != null)
            {
                int.TryParse(qs["page"], out page);
                if (!int.TryParse(qs["page_size"], out pageSize) || pageSize <= 0) pageSize = 30;
            }

            List<ExperienceInfo> found = m_Service.FindExperiences(query) ?? new List<ExperienceInfo>();
            OSDArray keys = new OSDArray();
            int start = page * pageSize;
            for (int i = start; i < found.Count && i < start + pageSize; i++)
                keys.Add(ExperienceToOSD(found[i]));

            OSDMap result = new OSDMap();
            result["experience_keys"] = keys;
            WriteLLSD(resp, result);
        }

        public void Close() { }

        // ══════════════════════════════════════════════════════════════════
        // Permission Cache (for fast script-side lookups)
        // ══════════════════════════════════════════════════════════════════

        private string MakeCacheKey(UUID experienceId, UUID agentId)
            => $"{experienceId}:{agentId}";

        /// <summary>
        /// Check if agent has granted permission, using cache first.
        /// Called from script engine on every experience-gated operation.
        /// </summary>
        public bool CheckPermission(UUID experienceId, UUID agentId)
        {
            string key = MakeCacheKey(experienceId, agentId);
            lock (m_PermCacheLock)
            {
                if (m_PermCache.TryGetValue(key, out bool cached))
                    return cached;
            }

            // Cache miss — hit the database
            bool granted = m_Service.IsAgentGranted(experienceId, agentId);
            lock (m_PermCacheLock)
            {
                m_PermCache[key] = granted;
            }
            return granted;
        }

        /// <summary>Invalidate cache entry when permission changes</summary>
        public void InvalidatePermission(UUID experienceId, UUID agentId)
        {
            string key = MakeCacheKey(experienceId, agentId);
            lock (m_PermCacheLock)
            {
                m_PermCache.Remove(key);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Script-Experience Association
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Get the experience UUID associated with a script, or UUID.Zero if none.</summary>
        /// <remarks>
        /// On a dict miss (e.g. after a grid restart, when the in-memory cache is empty, or when a
        /// script crosses into this region) the association is restored from persistent storage and
        /// cached — INCLUDING a UUID.Zero "no experience" result, so non-experience scripts don't
        /// re-hit the DB on every experience call. A later SetScriptExperience overwrites the cache.
        /// </remarks>
        public UUID GetScriptExperience(UUID scriptItemId)
        {
            lock (m_ScriptExpLock)
            {
                if (m_ScriptExperiences.TryGetValue(scriptItemId, out UUID cached))
                    return cached;
            }

            // Dict miss — restore from persistent storage (durable across restart / region crossing).
            UUID restored = m_Service != null ? m_Service.GetScriptExperiencePersisted(scriptItemId) : UUID.Zero;

            lock (m_ScriptExpLock)
            {
                // If another thread populated/assigned while we queried, that value wins.
                if (m_ScriptExperiences.TryGetValue(scriptItemId, out UUID raced))
                    return raced;
                m_ScriptExperiences[scriptItemId] = restored;
                return restored;
            }
        }

        /// <summary>Associate a script with an experience (UUID.Zero removes/unassigns).</summary>
        /// <remarks>
        /// Write-through: updates the in-memory cache UNCONDITIONALLY and persists to storage so the
        /// association survives restart. The unconditional cache update is the negative-cache
        /// invalidation guarantee — an assign after a cached Zero takes effect immediately, no restart.
        /// </remarks>
        public void SetScriptExperience(UUID scriptItemId, UUID experienceId)
        {
            lock (m_ScriptExpLock)
            {
                if (experienceId == UUID.Zero)
                    m_ScriptExperiences.Remove(scriptItemId);
                else
                    m_ScriptExperiences[scriptItemId] = experienceId;
            }

            // Write through to persistent storage. Real id → upsert; UUID.Zero → delete (explicit
            // unassign only). NOTE: never call this on script-stop/region-shutdown — that would wipe
            // associations on every restart (the exact bug this fix targets).
            if (m_Service != null)
            {
                if (experienceId == UUID.Zero)
                    m_Service.RemoveScriptExperiencePersisted(scriptItemId);
                else
                    m_Service.SetScriptExperiencePersisted(scriptItemId, experienceId,
                        m_Scene != null ? m_Scene.RegionInfo.RegionID : UUID.Zero);
            }

            m_log.DebugFormat("[ExperienceModule]: Script {0} → experience {1}", scriptItemId, experienceId);
        }

        /// <summary>Get the underlying service for direct access from script engine</summary>
        public IExperienceService Service => m_Service;

        /// <summary>Whether the module is active</summary>
        public bool IsEnabled => m_Enabled;

        // ══════════════════════════════════════════════════════════════════
        // Console Commands
        // ══════════════════════════════════════════════════════════════════

        private void RegisterConsoleCommands()
        {
            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience create",
                "experience create <name> [description]",
                "Create a new experience owned by the estate owner",
                HandleCreateExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience list",
                "experience list",
                "List all experiences",
                HandleListExperiences);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience info",
                "experience info <name>",
                "Show details of an experience",
                HandleExperienceInfo);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience delete",
                "experience delete <name>",
                "Delete an experience and all its data",
                HandleDeleteExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience allow",
                "experience allow <name>",
                "Allow an experience in this region",
                HandleAllowExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience block",
                "experience block <name>",
                "Block an experience in this region",
                HandleBlockExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience kvlist",
                "experience kvlist <name>",
                "List all key-value pairs for an experience",
                HandleKvList);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience kvset",
                "experience kvset <experience-name> <key> <value>",
                "Set a key-value pair for an experience",
                HandleKvSet);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience assign",
                "experience assign <experience-name> <script-item-uuid>",
                "Associate a script with an experience by script item UUID",
                HandleAssignExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience unassign",
                "experience unassign <script-item-uuid>",
                "Remove a script's experience association (also deletes the persisted row)",
                HandleUnassignExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience block-parcel",
                "experience block-parcel <exp-name-or-uuid> <parcel-localID>",
                "BLOCK an experience on a specific parcel (block-wins; overrides region/grid allow)",
                HandleBlockParcel);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience unblock-parcel",
                "experience unblock-parcel <exp-name-or-uuid> <parcel-localID>",
                "Remove a parcel BLOCK for an experience",
                HandleUnblockParcel);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience list-parcels",
                "experience list-parcels",
                "List parcels in this region (local ID, name, area, # blocked experiences)",
                HandleListParcels);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience list-scripts",
                "experience list-scripts [object-name-filter]",
                "List scripts in the region with their item UUIDs (for 'experience assign')",
                HandleListScripts);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience unallow",
                "experience unallow <name>",
                "Remove an experience from THIS region's allow list (region admission off)",
                HandleUnallowExperience);

            MainConsole.Instance.Commands.AddCommand("Experience", false,
                "experience create-land",
                "experience create-land <name> [description]",
                "Create a NON-grid-wide, NOT-auto-allowed experience (test subject for parcel-ALLOW precedence)",
                HandleCreateLandExperience);
        }

        private void HandleCreateExperience(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience create <name> [description]");
                return;
            }

            string name = args[2];
            string desc = args.Length > 3 ? string.Join(" ", args, 3, args.Length - 3) : "";

            // Owner is the estate owner
            UUID ownerId = m_Scene.RegionInfo.EstateSettings.EstateOwner;

            var info = new ExperienceInfo
            {
                OwnerId = ownerId,
                Name = name,
                Description = desc,
                Properties = ExperienceInfo.PROP_ENABLED | ExperienceInfo.PROP_GRIDWIDE
            };

            var created = m_Service.CreateExperience(info);
            if (created != null)
            {
                // Auto-allow in this region
                m_Service.AllowExperience(m_Scene.RegionInfo.RegionID, created.ExperienceId);
                MainConsole.Instance.Output($"Created experience '{name}' ({created.ExperienceId})");
                MainConsole.Instance.Output($"  Owner: {ownerId}");
                MainConsole.Instance.Output($"  Auto-allowed in region '{m_Scene.RegionInfo.RegionName}'");
            }
            else
            {
                MainConsole.Instance.Output("Failed to create experience. Check logs.");
            }
        }

        private void HandleListExperiences(string module, string[] args)
        {
            var experiences = m_Service.FindExperiences("");
            if (experiences.Count == 0)
            {
                MainConsole.Instance.Output("No experiences found.");
                return;
            }

            MainConsole.Instance.Output($"{"Name",-30} {"ID",-38} {"Owner",-38} {"Enabled",-8}");
            MainConsole.Instance.Output(new string('-', 114));
            foreach (var exp in experiences)
            {
                MainConsole.Instance.Output(
                    $"{exp.Name,-30} {exp.ExperienceId,-38} {exp.OwnerId,-38} {exp.IsEnabled,-8}");
            }
            MainConsole.Instance.Output($"\nTotal: {experiences.Count} experience(s)");
        }

        private void HandleExperienceInfo(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience info <name>");
                return;
            }

            string name = string.Join(" ", args, 2, args.Length - 2);
            var exp = m_Service.GetExperienceByName(name);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{name}' not found.");
                return;
            }

            MainConsole.Instance.Output($"Experience: {exp.Name}");
            MainConsole.Instance.Output($"  ID:          {exp.ExperienceId}");
            MainConsole.Instance.Output($"  Owner:       {exp.OwnerId}");
            MainConsole.Instance.Output($"  Description: {exp.Description}");
            MainConsole.Instance.Output($"  Enabled:     {exp.IsEnabled}");
            MainConsole.Instance.Output($"  Grid-wide:   {exp.IsGridWide}");
            MainConsole.Instance.Output($"  KV Count:    {m_Service.KeyCountKeyValue(exp.ExperienceId)}");
            MainConsole.Instance.Output($"  KV Size:     {m_Service.DataSizeKeyValue(exp.ExperienceId)} bytes");
            MainConsole.Instance.Output($"  Created:     {exp.Created}");

            // Check if allowed/blocked in this region
            var allowed = m_Service.GetAllowedExperiences(m_Scene.RegionInfo.RegionID);
            var blocked = m_Service.GetBlockedExperiences(m_Scene.RegionInfo.RegionID);
            string status = "not configured";
            if (allowed.Contains(exp.ExperienceId)) status = "ALLOWED";
            else if (blocked.Contains(exp.ExperienceId)) status = "BLOCKED";
            MainConsole.Instance.Output($"  Region:      {status}");
        }

        private void HandleDeleteExperience(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience delete <name>");
                return;
            }

            string name = string.Join(" ", args, 2, args.Length - 2);
            var exp = m_Service.GetExperienceByName(name);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{name}' not found.");
                return;
            }

            if (m_Service.DeleteExperience(exp.ExperienceId))
                MainConsole.Instance.Output($"Deleted experience '{name}' and all associated data.");
            else
                MainConsole.Instance.Output("Failed to delete experience.");
        }

        private void HandleAllowExperience(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience allow <name>");
                return;
            }

            string name = string.Join(" ", args, 2, args.Length - 2);
            var exp = m_Service.GetExperienceByName(name);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{name}' not found.");
                return;
            }

            m_Service.AllowExperience(m_Scene.RegionInfo.RegionID, exp.ExperienceId);
            MainConsole.Instance.Output($"Experience '{name}' is now ALLOWED in region '{m_Scene.RegionInfo.RegionName}'.");
        }

        private void HandleBlockExperience(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience block <name>");
                return;
            }

            string name = string.Join(" ", args, 2, args.Length - 2);
            var exp = m_Service.GetExperienceByName(name);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{name}' not found.");
                return;
            }

            m_Service.BlockExperience(m_Scene.RegionInfo.RegionID, exp.ExperienceId);
            MainConsole.Instance.Output($"Experience '{name}' is now BLOCKED in region '{m_Scene.RegionInfo.RegionName}'.");
        }

        private void HandleKvList(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience kvlist <name>");
                return;
            }

            string name = string.Join(" ", args, 2, args.Length - 2);
            var exp = m_Service.GetExperienceByName(name);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{name}' not found.");
                return;
            }

            int count = m_Service.KeyCountKeyValue(exp.ExperienceId);
            if (count == 0)
            {
                MainConsole.Instance.Output("No key-value pairs stored.");
                return;
            }

            var keys = m_Service.KeysKeyValue(exp.ExperienceId, 0, 100);
            MainConsole.Instance.Output($"Key-value pairs for '{name}' ({count} total):");
            foreach (var key in keys)
            {
                string val = m_Service.ReadKeyValue(exp.ExperienceId, key);
                // Truncate display of long values
                string display = val != null && val.Length > 80 ? val.Substring(0, 80) + "..." : val ?? "(null)";
                MainConsole.Instance.Output($"  {key} = {display}");
            }
        }

        private void HandleKvSet(string module, string[] args)
        {
            if (args.Length < 5)
            {
                MainConsole.Instance.Output("Usage: experience kvset <experience-name> <key> <value>");
                return;
            }

            string name = args[2];
            string key = args[3];
            string value = string.Join(" ", args, 4, args.Length - 4);

            var exp = m_Service.GetExperienceByName(name);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{name}' not found.");
                return;
            }

            // Try create, fall back to update
            if (!m_Service.CreateKeyValue(exp.ExperienceId, key, value))
                m_Service.UpdateKeyValue(exp.ExperienceId, key, value, null);

            MainConsole.Instance.Output($"Set {key} = {value}");
        }

        private void HandleAssignExperience(string module, string[] args)
        {
            if (args.Length < 4)
            {
                MainConsole.Instance.Output("Usage: experience assign <experience-name> <script-item-uuid>");
                return;
            }

            string expName = args[2];
            string scriptIdStr = args[3];

            var exp = m_Service.GetExperienceByName(expName);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{expName}' not found.");
                return;
            }

            if (!UUID.TryParse(scriptIdStr, out UUID scriptItemId))
            {
                MainConsole.Instance.Output($"Invalid UUID: {scriptIdStr}");
                return;
            }

            SetScriptExperience(scriptItemId, exp.ExperienceId);
            MainConsole.Instance.Output(
                $"Script {scriptItemId} is now associated with experience '{expName}' ({exp.ExperienceId})");
        }

        private void HandleUnassignExperience(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience unassign <script-item-uuid>");
                return;
            }

            if (!UUID.TryParse(args[2], out UUID scriptItemId))
            {
                MainConsole.Instance.Output($"Invalid UUID: {args[2]}");
                return;
            }

            // UUID.Zero unassigns: removes from cache and deletes the persisted row (write-through).
            SetScriptExperience(scriptItemId, UUID.Zero);
            MainConsole.Instance.Output($"Script {scriptItemId} experience association removed.");
        }

        // ══════════════════════════════════════════════════════════════════
        // Parcel BLOCK commands (block-wins safety primitive; testable without the viewer cap)
        // ══════════════════════════════════════════════════════════════════

        // Resolve an experience by UUID (if the token parses) or by name.
        private ExperienceInfo ResolveExperience(string token)
        {
            if (UUID.TryParse(token, out UUID id))
            {
                var byId = m_Service.GetExperience(id);
                if (byId != null) return byId;
            }
            return m_Service.GetExperienceByName(token);
        }

        private void HandleBlockParcel(string module, string[] args)
        {
            // experience block-parcel <exp-name-or-uuid> <parcel-localID>
            if (args.Length < 4)
            {
                MainConsole.Instance.Output("Usage: experience block-parcel <exp-name-or-uuid> <parcel-localID>");
                MainConsole.Instance.Output("       (run 'experience list-parcels' to find the local ID)");
                return;
            }

            var exp = ResolveExperience(args[2]);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{args[2]}' not found.");
                return;
            }

            if (!int.TryParse(args[3], out int localID))
            {
                MainConsole.Instance.Output($"Invalid parcel local ID: {args[3]}");
                return;
            }

            ILandObject land = m_Scene.LandChannel.GetLandObject(localID);
            if (land == null)
            {
                MainConsole.Instance.Output($"No parcel with local ID {localID} in region '{m_Scene.RegionInfo.RegionName}'.");
                return;
            }

            var list = land.LandData.ParcelAccessList;
            bool already = list.Exists(e => (int)e.Flags == AL_BLOCK_EXPERIENCE && e.AgentID == exp.ExperienceId);
            if (already)
            {
                MainConsole.Instance.Output($"Experience '{exp.Name}' is already BLOCKED on parcel '{land.LandData.Name}' (localID {localID}).");
                return;
            }

            list.Add(new LandAccessEntry
            {
                AgentID = exp.ExperienceId,
                Flags = (AccessList)AL_BLOCK_EXPERIENCE,
                Expires = 0
            });

            // Persist: StoreLandObject (wired to OnLandObjectAdded) upserts the full access list
            // idempotently — replace-into land + delete/insert landaccesslist, no duplicate rows.
            m_Scene.EventManager.TriggerLandObjectAdded(land);
            land.SendLandUpdateToAvatars();

            MainConsole.Instance.Output(
                $"Experience '{exp.Name}' ({exp.ExperienceId}) is now BLOCKED on parcel '{land.LandData.Name}' (localID {localID}).");
        }

        private void HandleUnblockParcel(string module, string[] args)
        {
            // experience unblock-parcel <exp-name-or-uuid> <parcel-localID>
            if (args.Length < 4)
            {
                MainConsole.Instance.Output("Usage: experience unblock-parcel <exp-name-or-uuid> <parcel-localID>");
                return;
            }

            var exp = ResolveExperience(args[2]);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{args[2]}' not found.");
                return;
            }

            if (!int.TryParse(args[3], out int localID))
            {
                MainConsole.Instance.Output($"Invalid parcel local ID: {args[3]}");
                return;
            }

            ILandObject land = m_Scene.LandChannel.GetLandObject(localID);
            if (land == null)
            {
                MainConsole.Instance.Output($"No parcel with local ID {localID} in region '{m_Scene.RegionInfo.RegionName}'.");
                return;
            }

            int removed = land.LandData.ParcelAccessList.RemoveAll(
                e => (int)e.Flags == AL_BLOCK_EXPERIENCE && e.AgentID == exp.ExperienceId);
            if (removed == 0)
            {
                MainConsole.Instance.Output($"Experience '{exp.Name}' was not blocked on parcel '{land.LandData.Name}' (localID {localID}).");
                return;
            }

            m_Scene.EventManager.TriggerLandObjectAdded(land);
            land.SendLandUpdateToAvatars();

            MainConsole.Instance.Output(
                $"Experience '{exp.Name}' is no longer blocked on parcel '{land.LandData.Name}' (localID {localID}).");
        }

        private void HandleListParcels(string module, string[] args)
        {
            var parcels = m_Scene.LandChannel.AllParcels();
            if (parcels == null || parcels.Count == 0)
            {
                MainConsole.Instance.Output("No parcels in this region.");
                return;
            }

            MainConsole.Instance.Output($"Parcels in region '{m_Scene.RegionInfo.RegionName}':");
            MainConsole.Instance.Output($"{"LocalID",-8} {"Name",-30} {"Area",-8} {"BlockedExp",-10}");
            MainConsole.Instance.Output(new string('-', 60));
            foreach (var p in parcels)
            {
                var ld = p.LandData;
                int blocked = 0;
                foreach (var e in ld.ParcelAccessList)
                    if ((int)e.Flags == AL_BLOCK_EXPERIENCE) blocked++;
                MainConsole.Instance.Output($"{ld.LocalID,-8} {ld.Name,-30} {ld.Area,-8} {blocked,-10}");
            }
        }

        private void HandleListScripts(string module, string[] args)
        {
            // experience list-scripts [object-name-filter]
            string filter = args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : null;

            var groups = m_Scene.GetSceneObjectGroups();
            int count = 0;
            MainConsole.Instance.Output($"{"Script item UUID",-38} {"Script name",-24} {"Object",-24} Position");
            MainConsole.Instance.Output(new string('-', 100));
            foreach (var sog in groups)
            {
                if (filter != null && sog.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                foreach (var part in sog.Parts)
                {
                    foreach (var item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                    {
                        var pos = sog.AbsolutePosition;
                        MainConsole.Instance.Output(
                            $"{item.ItemID,-38} {item.Name,-24} {sog.Name,-24} <{(int)pos.X},{(int)pos.Y},{(int)pos.Z}>");
                        count++;
                    }
                }
            }
            if (count == 0)
                MainConsole.Instance.Output(filter != null
                    ? $"No scripts found in objects matching '{filter}'."
                    : "No scripts found in this region.");
            else
                MainConsole.Instance.Output($"\nTotal: {count} script item(s). Use the UUID with 'experience assign'.");
        }

        private void HandleUnallowExperience(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience unallow <name>");
                return;
            }
            string name = string.Join(" ", args, 2, args.Length - 2);
            var exp = m_Service.GetExperienceByName(name);
            if (exp == null)
            {
                MainConsole.Instance.Output($"Experience '{name}' not found.");
                return;
            }
            m_Service.RemoveAllowedExperience(m_Scene.RegionInfo.RegionID, exp.ExperienceId);
            MainConsole.Instance.Output(
                $"Experience '{name}' removed from region '{m_Scene.RegionInfo.RegionName}' allow list. " +
                "(Grid-wide experiences are still admitted everywhere unless blocked.)");
        }

        private void HandleCreateLandExperience(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: experience create-land <name> [description]");
                return;
            }
            string name = args[2];
            string desc = args.Length > 3 ? string.Join(" ", args, 3, args.Length - 3) : "";
            UUID ownerId = m_Scene.RegionInfo.EstateSettings.EstateOwner;

            // NON-grid-wide (PROP_ENABLED only) and — unlike 'experience create' — NOT auto-allowed
            // in the region. This is the clean test subject for parcel-ALLOW precedence: admitted
            // ONLY where a parcel explicitly ALLOWs it.
            var info = new ExperienceInfo
            {
                OwnerId = ownerId,
                Name = name,
                Description = desc,
                Properties = ExperienceInfo.PROP_ENABLED
            };
            var created = m_Service.CreateExperience(info);
            if (created != null)
                MainConsole.Instance.Output(
                    $"Created LAND-scoped experience '{name}' ({created.ExperienceId}) — non-grid-wide, NOT region-allowed. " +
                    "Admit it by adding a parcel ALLOW entry (About Land > Experiences, or a script test).");
            else
                MainConsole.Instance.Output("Failed to create experience. Check logs.");
        }
    }
}
