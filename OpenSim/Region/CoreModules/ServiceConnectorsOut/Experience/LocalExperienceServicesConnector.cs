/*
 * Legion Grid — Experience System (grid-service topology)
 * LocalExperienceServicesConnector.cs — in-process (region-local) IExperienceService connector.
 *
 * G1 of the Experience grid-service rebuild: extract ExperienceService behind the standard
 * OpenSim Local/Remote service-connector seam. This Local connector is BEHAVIOR-IDENTICAL to
 * the previous inline `new ExperienceService(...)` in ExperienceModule — it holds a single
 * shared ExperienceService and forwards every IExperienceService call straight to it. No wire,
 * no Robust, no schema change. It is the seam on which G2 (Robust ServerConnector) and G3
 * (Remote connector) will build.
 *
 * Default-to-Local: when [Modules] ExperienceServices is unset, this connector activates from
 * the existing [Experience] section (Enabled + ConnectionString) exactly as the module did
 * before — so live grids need ZERO config change and behave identically.
 *
 * Connector TOPOLOGY / PATTERN credit: adapted from the OpenSim-NGC / OpenSim-Tranquillity
 * Experience service-connector stack (original scaffold by StolenRuby; integration by
 * Mike Dickson / OpenSim-NGC, Utopia Skye). The implementation here is Legion Grid's own,
 * targeting Legion's IExperienceService. Structurally mirrors the stock
 * LocalAgentPreferencesServicesConnector template.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.ExperienceService;

using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Experience
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LocalExperienceServicesConnector")]
    public class LocalExperienceServicesConnector : ISharedRegionModule, IExperienceService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // The wrapped service — a single shared instance for the whole simulator (constructed
        // once in Initialise). G1 delegates every call to it unchanged.
        private IExperienceService m_ExperienceService;
        private bool m_Enabled = false;

        #region ISharedRegionModule

        public Type ReplaceableInterface => null;

        public string Name => "LocalExperienceServicesConnector";

        public void Initialise(IConfigSource source)
        {
            // Connector selection. Stock services key strictly on [Modules] <Name>Services == this
            // connector's Name. Legion adds DEFAULT-TO-LOCAL: when the selector is absent, the Local
            // connector activates — so existing grids (which have no such config) keep working with
            // zero change. If the selector names a DIFFERENT connector (e.g. the future Remote one),
            // this Local connector stays dormant.
            string selected = string.Empty;
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
                selected = moduleConfig.GetString("ExperienceServices", string.Empty);

            if (!string.IsNullOrEmpty(selected) && selected != Name)
                return; // a different Experience connector was explicitly selected — not us.

            // Construction path is IDENTICAL to the previous ExperienceModule inline path:
            // read the same [Experience] section, honor Enabled, and require a ConnectionString.
            IConfig expConfig = source.Configs["Experience"];
            if (expConfig == null)
            {
                // No [Experience] section — Experience stays disabled, exactly as before.
                return;
            }

            if (!expConfig.GetBoolean("Enabled", false))
                return;

            string connectionString = expConfig.GetString("ConnectionString", string.Empty);
            if (string.IsNullOrEmpty(connectionString))
            {
                m_log.Error("[EXPERIENCE CONNECTOR]: [Experience] Enabled but no ConnectionString configured — Experience disabled.");
                return;
            }

            // Same constructor, same connection string, region-side (Local mode). No Robust here.
            m_ExperienceService = new OpenSim.Services.ExperienceService.ExperienceService(connectionString);

            m_Enabled = true;
            m_log.Info("[EXPERIENCE CONNECTOR]: Local experience connector enabled (in-process ExperienceService).");
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            // Register the CONNECTOR as the region's IExperienceService. Consumers
            // (ExperienceModule caps, the Phlox script layer) resolve it via
            // RequestModuleInterface<IExperienceService>() and reach the wrapped service
            // through this pass-through — the seam that G2/G3 later swap for a remote path.
            scene.RegisterModuleInterface<IExperienceService>(this);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.UnregisterModuleInterface<IExperienceService>(this);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        #endregion ISharedRegionModule

        #region IExperienceService (pass-through — behavior unchanged)

        // ── Experience CRUD ──
        public ExperienceInfo GetExperience(UUID experienceId) => m_ExperienceService.GetExperience(experienceId);
        public ExperienceInfo GetExperienceByName(string name) => m_ExperienceService.GetExperienceByName(name);
        public ExperienceInfo CreateExperience(ExperienceInfo info) => m_ExperienceService.CreateExperience(info);
        public bool UpdateExperience(ExperienceInfo info) => m_ExperienceService.UpdateExperience(info);
        public bool DeleteExperience(UUID experienceId) => m_ExperienceService.DeleteExperience(experienceId);
        public List<ExperienceInfo> GetExperiencesByOwner(UUID ownerId) => m_ExperienceService.GetExperiencesByOwner(ownerId);
        public List<ExperienceInfo> GetExperiencesByGroup(UUID groupId) => m_ExperienceService.GetExperiencesByGroup(groupId);
        public bool IsExperienceContributor(UUID experienceId, UUID agentId) => m_ExperienceService.IsExperienceContributor(experienceId, agentId);
        public bool IsExperienceAdmin(UUID experienceId, UUID agentId) => m_ExperienceService.IsExperienceAdmin(experienceId, agentId);
        public List<ExperienceInfo> FindExperiences(string query) => m_ExperienceService.FindExperiences(query);
        public List<ExperienceInfo> FindExperiences(string query, int offset, int limit) => m_ExperienceService.FindExperiences(query, offset, limit);

        // ── Permission Grants ──
        public bool IsAgentGranted(UUID experienceId, UUID agentId) => m_ExperienceService.IsAgentGranted(experienceId, agentId);
        public bool IsAgentBlocked(UUID experienceId, UUID agentId) => m_ExperienceService.IsAgentBlocked(experienceId, agentId);
        public bool GrantPermission(UUID experienceId, UUID agentId) => m_ExperienceService.GrantPermission(experienceId, agentId);
        public bool DenyPermission(UUID experienceId, UUID agentId) => m_ExperienceService.DenyPermission(experienceId, agentId);
        public bool ForgetPermission(UUID experienceId, UUID agentId) => m_ExperienceService.ForgetPermission(experienceId, agentId);
        public List<UUID> GetAgentExperiences(UUID agentId) => m_ExperienceService.GetAgentExperiences(agentId);
        public List<UUID> GetAgentBlockedExperiences(UUID agentId) => m_ExperienceService.GetAgentBlockedExperiences(agentId);
        public bool BlockExperienceForAgent(UUID agentId, UUID experienceId) => m_ExperienceService.BlockExperienceForAgent(agentId, experienceId);
        public bool UnblockExperienceForAgent(UUID agentId, UUID experienceId) => m_ExperienceService.UnblockExperienceForAgent(agentId, experienceId);

        // ── Key-Value Store ──
        public string ReadKeyValue(UUID experienceId, string key) => m_ExperienceService.ReadKeyValue(experienceId, key);
        public bool CreateKeyValue(UUID experienceId, string key, string value) => m_ExperienceService.CreateKeyValue(experienceId, key, value);
        public bool UpdateKeyValue(UUID experienceId, string key, string value, string check) => m_ExperienceService.UpdateKeyValue(experienceId, key, value, check);
        public bool UpdateKeyValue(UUID experienceId, string key, string value, string check, bool conditional) => m_ExperienceService.UpdateKeyValue(experienceId, key, value, check, conditional);
        public bool DeleteKeyValue(UUID experienceId, string key) => m_ExperienceService.DeleteKeyValue(experienceId, key);
        public int KeyCountKeyValue(UUID experienceId) => m_ExperienceService.KeyCountKeyValue(experienceId);
        public List<string> KeysKeyValue(UUID experienceId, int start, int count) => m_ExperienceService.KeysKeyValue(experienceId, start, count);
        public long DataSizeKeyValue(UUID experienceId) => m_ExperienceService.DataSizeKeyValue(experienceId);

        // ── Region Allow/Block/Trust Lists ──
        public List<UUID> GetAllowedExperiences(UUID regionId) => m_ExperienceService.GetAllowedExperiences(regionId);
        public List<UUID> GetBlockedExperiences(UUID regionId) => m_ExperienceService.GetBlockedExperiences(regionId);
        public List<UUID> GetTrustedExperiences(UUID regionId) => m_ExperienceService.GetTrustedExperiences(regionId);
        public bool AllowExperience(UUID regionId, UUID experienceId) => m_ExperienceService.AllowExperience(regionId, experienceId);
        public bool RemoveAllowedExperience(UUID regionId, UUID experienceId) => m_ExperienceService.RemoveAllowedExperience(regionId, experienceId);
        public bool BlockExperience(UUID regionId, UUID experienceId) => m_ExperienceService.BlockExperience(regionId, experienceId);
        public bool RemoveBlockedExperience(UUID regionId, UUID experienceId) => m_ExperienceService.RemoveBlockedExperience(regionId, experienceId);
        public bool TrustExperience(UUID regionId, UUID experienceId) => m_ExperienceService.TrustExperience(regionId, experienceId);
        public bool RemoveTrustedExperience(UUID regionId, UUID experienceId) => m_ExperienceService.RemoveTrustedExperience(regionId, experienceId);

        // ── Script ↔ Experience association persistence ──
        public void SetScriptExperiencePersisted(UUID itemId, UUID experienceId, UUID regionId) => m_ExperienceService.SetScriptExperiencePersisted(itemId, experienceId, regionId);
        public UUID GetScriptExperiencePersisted(UUID itemId) => m_ExperienceService.GetScriptExperiencePersisted(itemId);
        public void RemoveScriptExperiencePersisted(UUID itemId) => m_ExperienceService.RemoveScriptExperiencePersisted(itemId);

        #endregion IExperienceService
    }
}
