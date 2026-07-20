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

            // The Region/Estate > Experiences panel edits arrive on TWO wires: the caps
            // POST (full lists, sent when the user clicks Apply) AND the per-item
            // EstateOwnerMessage "estateexperiencedelta" fired immediately on each
            // dialog-confirmed add/remove. SL applies on the delta — a user who confirms
            // the dialog and closes the floater without Apply would silently lose the
            // edit if we only handled the POST. Handle both.
            scene.EventManager.OnNewClient += OnNewClient;

            m_log.InfoFormat("[ExperienceModule]: Experience system active for region '{0}'", scene.RegionInfo.RegionName);
        }

        private void OnNewClient(IClientAPI client)
        {
            client.OnEstateExperienceDelta += HandleEstateExperienceDelta;
        }

        // Flags per the viewer (llregionflags.h): TRUSTED_ADD=1<<2, TRUSTED_REMOVE=1<<3,
        // ALLOWED_ADD=1<<4, ALLOWED_REMOVE=1<<5, BLOCKED_ADD=1<<6, BLOCKED_REMOVE=1<<7;
        // ESTATE_ACCESS_NO_REPLY=1<<10 suppresses the "setexperience" echo (the viewer
        // sets it on all but the last item of a multi-select batch). The apply-to-all/
        // managed-estates bits (1<<0, 1<<1) are accepted as this-estate: Legion runs one
        // estate per region here. LLClientView already gated on CanIssueEstateCommand.
        private void HandleEstateExperienceDelta(IClientAPI client, UUID invoice, uint flags, UUID experienceID)
        {
            if (m_Service == null || experienceID == UUID.Zero) return;
            UUID regionId = m_Scene.RegionInfo.RegionID;

            const uint TRUSTED_ADD = 1u << 2, TRUSTED_REMOVE = 1u << 3;
            const uint ALLOWED_ADD = 1u << 4, ALLOWED_REMOVE = 1u << 5;
            const uint BLOCKED_ADD = 1u << 6, BLOCKED_REMOVE = 1u << 7;
            const uint NO_REPLY = 1u << 10;

            if ((flags & ALLOWED_ADD) != 0)
                m_Service.AllowExperience(regionId, experienceID);
            else if ((flags & ALLOWED_REMOVE) != 0)
                m_Service.RemoveAllowedExperience(regionId, experienceID);
            else if ((flags & BLOCKED_ADD) != 0)
                m_Service.BlockExperience(regionId, experienceID);
            else if ((flags & BLOCKED_REMOVE) != 0)
                m_Service.RemoveBlockedExperience(regionId, experienceID);
            else if ((flags & (TRUSTED_ADD | TRUSTED_REMOVE)) != 0)
            {
                // Trusted is not implemented (consent-bypass semantics deferred — see the
                // POST handler note). The authoritative echo below reports trusted as
                // empty, so the panel visibly reverts the entry — honest, not silent.
                m_log.DebugFormat(
                    "[ExperienceModule]: estateexperiencedelta TRUSTED edit for {0} by {1} in '{2}' not applied — trusted list not implemented.",
                    experienceID, client.AgentId, m_Scene.RegionInfo.RegionName);
            }
            else
            {
                m_log.DebugFormat(
                    "[ExperienceModule]: estateexperiencedelta with unrecognized flags {0} from {1} — ignored.",
                    flags, client.AgentId);
                return;
            }

            m_log.DebugFormat(
                "[ExperienceModule]: estateexperiencedelta flags={0} exp={1} by {2} in '{3}' applied.",
                flags, experienceID, client.AgentId, m_Scene.RegionInfo.RegionName);

            // Authoritative echo (unless suppressed): the updated lists, which the panel
            // renders directly — what it shows is exactly what the DB now holds.
            if ((flags & NO_REPLY) == 0)
            {
                client.SendEstateExperienceList(invoice,
                    m_Scene.RegionInfo.EstateSettings.EstateID,
                    m_Service.GetBlockedExperiences(regionId).ToArray(),
                    Array.Empty<UUID>(), // trusted — honestly empty
                    m_Service.GetAllowedExperiences(regionId).ToArray());
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled) return;
            // Could preload allowed/blocked experience lists here if needed
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled) return;
            scene.EventManager.OnNewClient -= OnNewClient;
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

            try
            {
                caps.RegisterSimpleHandler("RegionExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleRegionExperiences(req, resp, agentID)));

                // GetExperienceInfo: the viewer appends a "/id/" subpath to this cap URL
                // (llexperiencecache: <cap>/id/?page_size=N&...). RegisterSimpleHandler registers
                // EXACT-match only (m_simpleStreamHandlers), so "<cap>/id/..." never routed and the
                // cap was never called -> Firestorm rendered "(untitled experience)". Register it as
                // a VAR-PATH handler (matched by the keyword before the second slash): advertise the
                // cap URL (addToListener:false), then add to the listener with varPath = true.
                var infoHandler = new SimpleStreamHandler("/" + UUID.Random(),
                    (req, resp) => HandleGetExperienceInfo(req, resp));
                caps.RegisterSimpleHandler("GetExperienceInfo", infoHandler, addToListener: false);
                caps.HttpListener.AddSimpleStreamHandler(infoHandler, true);

                caps.RegisterSimpleHandler("FindExperienceByName",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleFindExperienceByName(req, resp)));

                // GetCreatorExperiences (Slice 1): the experiences this agent may contribute
                // scripts to. Un-greys the script editor's "Experience" dropdown
                // (llpreviewscript.cpp:2039-2057) and fills the floater Contributor tab
                // (llfloaterexperiences.cpp:153). GET, bare URL; agent from the cap context.
                caps.RegisterSimpleHandler("GetCreatorExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleGetCreatorExperiences(req, resp, agentID)));

                // GetMetadata (Slice 1): which experience a given task-inventory SCRIPT is
                // associated with. POST { object-id, item-id, fields:["experience"] } ->
                // { experience: <uuid> } (llexperiencecache.cpp:581-627). Powers the script
                // editor / item-props "associated experience" display.
                caps.RegisterSimpleHandler("GetMetadata",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleGetMetadata(req, resp)));

                // AgentExperiences (Slice 2): the agent's OWNED experiences -> floater Owned tab.
                // GET bare URL -> { experience_ids:[…] }; the viewer maps experience_ids to the
                // Owned tab (llfloaterexperiences.cpp:148,155). POST (acquire) is deferred
                // (Slice 5/DEC-3) — handled as a no-op read, never auto-creates.
                caps.RegisterSimpleHandler("AgentExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleAgentExperiences(req, resp, agentID)));

                // GetExperiences (Slice 2): the agent's per-agent allowed/blocked permission
                // lists -> floater Allowed/Blocked tabs. GET bare URL ->
                // { experiences:[granted…], blocked:[blocked…] } (tabMap experiences->Allowed,
                // blocked->Blocked, llfloaterexperiences.cpp:146-147).
                caps.RegisterSimpleHandler("GetExperiences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleGetExperiences(req, resp, agentID)));

                // ExperiencePreferences (Slice 2): view/set the agent's per-experience Allow/Block
                // preference. GET "?<exp_id>" reads; PUT { "<exp_id>":{permission:"Allow"|"Block"} }
                // sets; DELETE "?<exp_id>" forgets (llexperiencecache.cpp:762-839). All return
                // { experiences, blocked }. The consent dialog's "Block Experience" button PUTs
                // here (llviewermessage.cpp script_question_cb -> setExperiencePermission "Block").
                caps.RegisterSimpleHandler("ExperiencePreferences",
                    new SimpleStreamHandler("/" + UUID.Random(),
                        (req, resp) => HandleExperiencePreferences(req, resp, agentID)));
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceModule]: cap registration FAILED for region '{0}': {1}",
                    m_Scene?.RegionInfo.RegionName, e);
            }
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
            m["maturity"] = OSD.FromInteger(MaturityToSimAccess(info.Maturity));
            m["quota"] = OSD.FromInteger(128);
            // Relative TTL in seconds — the viewer converts it to absolute on receipt
            // (llexperiencecache.cpp:230-232 `row[EXPIRES].asReal() + getTotalSeconds()`).
            // Without it a row NEVER expires from the viewer cache. 600 = the viewer's own
            // DEFAULT_EXPIRATION (llexperiencecache.cpp:85).
            m["expiration"] = OSD.FromInteger(600);
            m["extended_metadata"] = OSD.FromString(BuildExtendedMetadata(info));
            if (info.OwnerId != UUID.Zero) m["agent_id"] = OSD.FromUUID(info.OwnerId);
            if (info.GroupId != UUID.Zero) m["group_id"] = OSD.FromUUID(info.GroupId);
            if (!string.IsNullOrEmpty(info.Slurl)) m["slurl"] = OSD.FromString(info.Slurl);
            return m;
        }

        // Wire maturity is the viewer's SIM_ACCESS namespace — 13/21/42 (Firestorm 7.2.2
        // indra_constants.h:163-167) — NOT our internal 0/1/2. The profile floater classifies
        // with `maturity <= SIM_ACCESS_*` (llfloaterexperienceprofile.cpp:267-288), so raw
        // 0/1/2 rendered EVERYTHING (including Adult) as General. Unknown values map to
        // Adult: over-restrict, never under-rate.
        private static int MaturityToSimAccess(int maturity)
        {
            switch (maturity)
            {
                case 0: return 13;   // PG      -> SIM_ACCESS_PG
                case 1: return 21;   // Mature  -> SIM_ACCESS_MATURE
                case 2: return 42;   // Adult   -> SIM_ACCESS_ADULT
                default: return 42;
            }
        }

        // extended_metadata is an LLSD-XML document serialized INTO A STRING: the profile
        // floater feeds it to LLSDXMLParser and reads keys "logo" and "marketplace"
        // (llfloaterexperienceprofile.cpp:58,66,419-467). Always emitted — a zero-UUID logo
        // and an empty marketplace hide their panels viewer-side (:437-448, :454-465), which
        // is the correct display for "not set".
        private static string BuildExtendedMetadata(ExperienceInfo info)
        {
            OSDMap meta = new OSDMap();
            meta["logo"] = OSD.FromUUID(info.Logo);
            meta["marketplace"] = OSD.FromString(info.Marketplace ?? string.Empty);
            return OSDParser.SerializeLLSDXmlString(meta);
        }

        private OSDMap BuildRegionExperiencesLLSD(UUID regionId)
        {
            OSDMap m = new OSDMap();
            OSDArray allowed = new OSDArray();
            foreach (UUID id in m_Service.GetAllowedExperiences(regionId)) allowed.Add(OSD.FromUUID(id));
            OSDArray blocked = new OSDArray();
            foreach (UUID id in m_Service.GetBlockedExperiences(regionId)) blocked.Add(OSD.FromUUID(id));
            OSDArray trusted = new OSDArray();
            foreach (UUID id in m_Service.GetTrustedExperiences(regionId)) trusted.Add(OSD.FromUUID(id));
            m["allowed"] = allowed;
            m["blocked"] = blocked;
            // Wire key "trusted" — the Region/Estate > Experiences panel reads
            // content["trusted"] into its Trusted list editor (Firestorm 7.2.2
            // llfloaterregioninfo.cpp:3274, LLPanelRegionExperiences::processResponse) and
            // POSTs it back via addIds(mTrusted) (:3428). EXP-SLICE-0.5 (DEC-4/Option A):
            // the list is now real; trusted-bypasses-consent ENFORCEMENT is deferred to the
            // consent slice (DEC-1).
            m["trusted"] = trusted;
            // "default" omitted: the Region panel reads it CONDITIONALLY
            // (llfloaterregioninfo.cpp:3266-3268 `if(content.has("default"))`), so omission
            // is the correct wire encoding of "no default experience" — a concept Legion
            // doesn't model. Verified against Firestorm 7.2.2, 2026-07-19.
            return m;
        }

        private void HandleRegionExperiences(IOSHttpRequest req, IOSHttpResponse resp, UUID agentID)
        {
            UUID regionId = m_Scene.RegionInfo.RegionID;
            if (req.HttpMethod == "POST")
            {
                HandleRegionExperiencesPost(req, resp, agentID, regionId);
                return;
            }
            // GET — real region allowed/blocked (also makes the Region-Info panel read-correct).
            WriteLLSD(resp, BuildRegionExperiencesLLSD(regionId));
        }

        // Region/Estate > Experiences panel write. Wire contract (llfloaterregioninfo.cpp
        // LLPanelRegionExperiences::sendUpdate): the panel POSTs FULL lists every time —
        // { allowed:[uuid...], blocked:[uuid...], trusted:[uuid...] } — and expects the
        // updated lists back in the same shape as GET (infoCallback -> processResponse).
        // An emptied list arrives as an undefined/absent key (the viewer builds it by
        // appending to a fresh LLSD, so zero items = undef, not an empty array) — treat
        // missing keys as empty. Rejections are reject-shaped: echo the CURRENT lists so
        // the panel visibly reverts (no silent gate).
        //
        // Permission: match SL and the rest of the estate machinery — the viewer gates the
        // panel on isGodlike || canManageEstate (refreshFromRegion), and every estate
        // method in LLClientView gates on CanIssueEstateCommand(agentId, false) (= admin
        // OR estate manager/owner); we use exactly that check.
        //
        // TRUSTED: list is persisted as of EXP-SLICE-0.5 (DEC-4/Option A) — trust/untrust
        // edits round-trip through TrustExperience/RemoveTrustedExperience below, same as
        // allowed/blocked. The trusted-bypasses-per-agent-consent ENFORCEMENT is still
        // deferred to the consent slice (DEC-1): storing the list has no runtime effect on
        // script permission grants yet (see the seam comments in LSLSystemAPI.cs).
        private void HandleRegionExperiencesPost(IOSHttpRequest req, IOSHttpResponse resp, UUID agentID, UUID regionId)
        {
            bool authorized = m_Scene.Permissions.CanIssueEstateCommand(agentID, false);
            if (!authorized)
            {
                m_log.DebugFormat(
                    "[ExperienceModule]: RegionExperiences POST by {0} in '{1}' REJECTED — not estate owner/manager/admin (reject-shaped echo).",
                    agentID, m_Scene.RegionInfo.RegionName);
                WriteLLSD(resp, BuildRegionExperiencesLLSD(regionId));
                return;
            }

            OSDMap body = null;
            try
            {
                body = OSDParser.DeserializeLLSDXml(req.InputStream) as OSDMap;
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[ExperienceModule]: RegionExperiences POST parse error: {0}", e.Message);
            }
            if (body is null)
            {
                WriteLLSD(resp, BuildRegionExperiencesLLSD(regionId)); // reject-shaped
                return;
            }

            static HashSet<UUID> ReadIdList(OSDMap m, string key)
            {
                var set = new HashSet<UUID>();
                if (m.TryGetValue(key, out OSD osd) && osd is OSDArray arr)
                    foreach (OSD e in arr)
                    {
                        UUID id = e.AsUUID();
                        if (id != UUID.Zero) set.Add(id);
                    }
                return set; // missing/undef key (viewer's emptied list) => empty
            }

            var postedAllowed = ReadIdList(body, "allowed");
            var postedBlocked = ReadIdList(body, "blocked");
            var postedTrusted = ReadIdList(body, "trusted");

            // Blocked wins over both permit-family lists (matching block-wins enforcement
            // precedence). Allowed and trusted may coexist, so they are NOT de-duped against
            // each other — an id the estate owner placed in both editors stays in both.
            postedAllowed.ExceptWith(postedBlocked);
            postedTrusted.ExceptWith(postedBlocked);

            var currentAllowed = new HashSet<UUID>(m_Service.GetAllowedExperiences(regionId));
            var currentBlocked = new HashSet<UUID>(m_Service.GetBlockedExperiences(regionId));
            var currentTrusted = new HashSet<UUID>(m_Service.GetTrustedExperiences(regionId));

            int changes = 0;
            foreach (UUID id in postedAllowed)
                if (!currentAllowed.Contains(id) && m_Service.AllowExperience(regionId, id)) changes++;
            foreach (UUID id in currentAllowed)
                if (!postedAllowed.Contains(id) && m_Service.RemoveAllowedExperience(regionId, id)) changes++;
            // Blocked before trusted: BlockExperience clears the trusted table for that id, so
            // applying blocks first keeps a re-blocked id out of trusted. Posted sets are
            // disjoint from blocked (ExceptWith above), so this never fights the trusted adds.
            foreach (UUID id in postedBlocked)
                if (!currentBlocked.Contains(id) && m_Service.BlockExperience(regionId, id)) changes++;
            foreach (UUID id in currentBlocked)
                if (!postedBlocked.Contains(id) && m_Service.RemoveBlockedExperience(regionId, id)) changes++;
            foreach (UUID id in postedTrusted)
                if (!currentTrusted.Contains(id) && m_Service.TrustExperience(regionId, id)) changes++;
            foreach (UUID id in currentTrusted)
                if (!postedTrusted.Contains(id) && m_Service.RemoveTrustedExperience(regionId, id)) changes++;

            m_log.DebugFormat(
                "[ExperienceModule]: RegionExperiences POST by {0} in '{1}': {2} change(s) applied (allowed={3}, blocked={4}, trusted={5}).",
                agentID, m_Scene.RegionInfo.RegionName, changes, postedAllowed.Count, postedBlocked.Count, postedTrusted.Count);

            // Respond with the fresh persisted state in GET shape — the panel re-renders
            // from this, so what it shows is exactly what the DB now holds.
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

            // The viewer's page parameter is 1-BASED: the picker sends page=1 for the first
            // search (llpanelexperiencepicker.cpp mCurrentPage=1) and onPage() clamps to >=1
            // (:443-446). Treating it as 0-based made start = 30 and dropped EVERY result
            // (the "returned=0 for all queries" bug). Clamp <=1 to page one.
            if (page < 1) page = 1;

            // Page in SQL (the old service call was hard-capped at 50 rows, making results
            // beyond ~2 pages unreachable). Fetch one extra row to detect a next page.
            int start = (page - 1) * pageSize;
            List<ExperienceInfo> found = m_Service.FindExperiences(query, start, pageSize + 1)
                ?? new List<ExperienceInfo>();

            bool hasNext = found.Count > pageSize;
            OSDArray keys = new OSDArray();
            for (int i = 0; i < found.Count && i < pageSize; i++)
                keys.Add(ExperienceToOSD(found[i]));

            OSDMap result = new OSDMap();
            result["experience_keys"] = keys;
            // The picker enables its page buttons purely on the PRESENCE of these keys
            // (llpanelexperiencepicker.cpp:248-249) and re-requests via ?page=N±1
            // (onPage :443-446 -> findExperienceByNameCoro's ?page=&page_size=&query= URL);
            // the values are never dereferenced, but we emit real re-query URLs for honesty.
            if (hasNext) result["next_page_url"] = OSD.FromString(PageUrl(req, query, page + 1, pageSize));
            if (page > 1) result["previous_page_url"] = OSD.FromString(PageUrl(req, query, page - 1, pageSize));
            WriteLLSD(resp, result);
        }

        private static string PageUrl(IOSHttpRequest req, string query, int page, int pageSize)
        {
            string path = req.RawUrl ?? string.Empty;
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            return path + "?page=" + page + "&page_size=" + pageSize +
                   "&query=" + Uri.EscapeDataString(query ?? string.Empty);
        }

        // GetCreatorExperiences (Slice 1). GET, bare URL — no request params; the agent is the
        // one this cap was seeded for. The viewer reads the response's "experience_ids" array
        // (llpreviewscript.cpp:2057 setExperienceIds(result["experience_ids"]);
        // llfloaterexperiences.cpp:251 content["experience_ids"]). SL's full semantic is
        // owner ∪ groups where the agent holds ExperienceCreator power; Legion serves the
        // owner-only core here (GetExperiencesByOwner). The group union arrives with the
        // GetExperiencesByGroup service query in Slice 4 — noted, not stubbed.
        private void HandleGetCreatorExperiences(IOSHttpRequest req, IOSHttpResponse resp, UUID agentID)
        {
            OSDArray ids = new OSDArray();
            foreach (ExperienceInfo info in m_Service.GetExperiencesByOwner(agentID))
                ids.Add(OSD.FromUUID(info.ExperienceId));
            OSDMap result = new OSDMap();
            result["experience_ids"] = ids;
            WriteLLSD(resp, result);
        }

        // GetMetadata (Slice 1). POST body { object-id, item-id, fields:[...] }; the viewer only
        // ever requests the "experience" field (llexperiencecache.cpp:596-601) and reads back
        // result["experience"] (:608 has-check, :626 asUUID). We resolve the script item's
        // associated experience via the same lookup the script engine uses
        // (GetScriptExperience: in-memory map, then persisted script_experiences). No association
        // -> omit the key, which the viewer treats as a benign "no experience" (:608-623).
        private void HandleGetMetadata(IOSHttpRequest req, IOSHttpResponse resp)
        {
            UUID itemId = UUID.Zero;
            try
            {
                if (OSDParser.DeserializeLLSDXml(req.InputStream) is OSDMap body &&
                    body.TryGetValue("item-id", out OSD itemOsd))
                    itemId = itemOsd.AsUUID();
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[ExperienceModule]: GetMetadata parse error: {0}", e.Message);
            }

            OSDMap result = new OSDMap();
            if (itemId != UUID.Zero)
            {
                UUID experienceId = GetScriptExperience(itemId);
                if (experienceId != UUID.Zero)
                    result["experience"] = OSD.FromUUID(experienceId);
            }
            WriteLLSD(resp, result);
        }

        // AgentExperiences (Slice 2). GET -> the agent's OWNED experiences as experience_ids
        // (viewer Owned tab). SL's Owned list is what the agent created/acquired; Legion serves
        // GetExperiencesByOwner. POST is the "Acquire an Experience" path — DEFERRED (Slice 5/
        // DEC-3): we never auto-create; a POST just returns the current owned list (the viewer's
        // Acquire button stays disabled because we omit the "purchase" key), so nothing breaks.
        private void HandleAgentExperiences(IOSHttpRequest req, IOSHttpResponse resp, UUID agentID)
        {
            OSDArray ids = new OSDArray();
            foreach (ExperienceInfo info in m_Service.GetExperiencesByOwner(agentID))
                ids.Add(OSD.FromUUID(info.ExperienceId));
            OSDMap result = new OSDMap();
            result["experience_ids"] = ids;
            WriteLLSD(resp, result);
        }

        // GetExperiences (Slice 2). GET -> the agent's per-agent Allowed(granted)/Blocked lists
        // as { experiences, blocked } (viewer Allowed/Blocked tabs, tabMap
        // llfloaterexperiences.cpp:146-147).
        private void HandleGetExperiences(IOSHttpRequest req, IOSHttpResponse resp, UUID agentID)
        {
            WriteLLSD(resp, BuildAgentPrefsLLSD(agentID));
        }

        // Shared { experiences:[granted], blocked:[blocked] } document for GetExperiences and
        // every ExperiencePreferences verb. The viewer scans these two arrays for a given
        // experience id to decide Allow/Block/Forget (llfloaterexperienceprofile.cpp:487-488,
        // 892-894), and fills the floater Allowed/Blocked tabs from them.
        private OSDMap BuildAgentPrefsLLSD(UUID agentID)
        {
            OSDArray allowed = new OSDArray();
            foreach (UUID id in m_Service.GetAgentExperiences(agentID)) allowed.Add(OSD.FromUUID(id));
            OSDArray blocked = new OSDArray();
            foreach (UUID id in m_Service.GetAgentBlockedExperiences(agentID)) blocked.Add(OSD.FromUUID(id));
            OSDMap m = new OSDMap();
            m["experiences"] = allowed;
            m["blocked"] = blocked;
            return m;
        }

        // ExperiencePreferences (Slice 2) — GET/PUT/DELETE (llexperiencecache.cpp:762-839).
        //   GET    "?<exp_id>"                              -> read (no mutation)
        //   PUT    { "<exp_id>":{ permission:"Allow"|"Block" } } -> Grant / Deny
        //   DELETE "?<exp_id>"                              -> Forget
        // All return the shared { experiences, blocked } document. The consent dialog's Block
        // button reaches here as PUT permission="Block" (llviewermessage.cpp script_question_cb),
        // so a block persists to experience_permissions (granted=0) and the NEXT
        // llRequestExperiencePermissions denies via IsAgentBlocked (code 4) — closing the D1 loop.
        private void HandleExperiencePreferences(IOSHttpRequest req, IOSHttpResponse resp, UUID agentID)
        {
            string method = req.HttpMethod;
            if (method == "PUT")
            {
                try
                {
                    if (OSDParser.DeserializeLLSDXml(req.InputStream) is OSDMap body)
                    {
                        foreach (string key in body.Keys)
                        {
                            if (!UUID.TryParse(key, out UUID expId) || expId == UUID.Zero) continue;
                            string permission = (body[key] as OSDMap)?["permission"].AsString();
                            // Allow and Block are mutually exclusive so the experience appears in
                            // exactly one of the GET response's {experiences, blocked} arrays.
                            if (permission == "Allow")
                            {
                                m_Service.UnblockExperienceForAgent(agentID, expId);
                                m_Service.GrantPermission(expId, agentID);
                            }
                            else if (permission == "Block")
                            {
                                m_Service.BlockExperienceForAgent(agentID, expId);
                                m_Service.ForgetPermission(expId, agentID); // block revokes any grant
                            }
                            InvalidatePermission(expId, agentID); // keep the script-side cache honest
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[ExperienceModule]: ExperiencePreferences PUT parse error: {0}", e.Message);
                }
            }
            else if (method == "DELETE")
            {
                // Forget -> back to undecided: clear BOTH the grant and the personal block.
                UUID expId = ParseQueryUuid(req);
                if (expId != UUID.Zero)
                {
                    m_Service.ForgetPermission(expId, agentID);
                    m_Service.UnblockExperienceForAgent(agentID, expId);
                    InvalidatePermission(expId, agentID);
                }
            }
            // GET falls through to the read below. All verbs return the fresh prefs document.
            WriteLLSD(resp, BuildAgentPrefsLLSD(agentID));
        }

        // The permission caps address a single experience as a RAW UUID query string ("?<uuid>",
        // no key=value) — llexperiencecache.cpp:770,824. Parse it out of the URL query.
        private static UUID ParseQueryUuid(IOSHttpRequest req)
        {
            string q = req.Url?.Query;
            if (string.IsNullOrEmpty(q)) return UUID.Zero;
            q = q.TrimStart('?').Trim();
            return UUID.TryParse(q, out UUID id) ? id : UUID.Zero;
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

        // Region-scoped console commands must act only on the console-selected region. This module
        // is INonSharedRegionModule → one instance per region → each instance registered the same
        // command, so a bare invocation runs the handler N times (the estate-reload triple-echo).
        // Matches the stock LandManagementModule / RegionCommandsModule guard: proceed only when no
        // region is selected (root) or the selected region is THIS instance's scene.
        private bool WrongConsoleScene()
        {
            return !(MainConsole.Instance.ConsoleScene is null
                     || MainConsole.Instance.ConsoleScene == m_Scene);
        }

        private void HandleCreateExperience(string module, string[] args)
        {
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
            if (WrongConsoleScene()) return;
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
