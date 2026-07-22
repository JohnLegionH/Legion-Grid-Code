/*
 * Legion Grid — Experience System (grid-service topology)
 * RemoteExperienceServicesConnector.cs — region module: remote (HTTP) IExperienceService (G3).
 *
 * The region-side selector for the grid-service path. When [Modules] ExperienceServices names
 * this connector, the region resolves IExperienceService to the ExperienceServicesConnector HTTP
 * client (talking to the G2 Robust endpoint) instead of the in-process ExperienceService.
 *
 * DEFAULT-TO-LOCAL: this connector activates ONLY when explicitly selected. When
 * [Modules] ExperienceServices is unset (or names the Local connector), this stays dormant and
 * the G1 LocalExperienceServicesConnector serves — so existing grids are UNCHANGED. Remote and
 * Local are behaviorally identical (same central ExperienceService, same DB, reached differently).
 *
 * Connector TOPOLOGY / PATTERN credit: adapted from the OpenSim-NGC / OpenSim-Tranquillity
 * Experience service-connector stack (orig. StolenRuby; integ. Mike Dickson / OpenSim-NGC,
 * Utopia Skye). Implementation is Legion Grid's own; mirrors the stock
 * RemoteGridUserServicesConnector.
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
using OpenSim.Services.Connectors;

using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Experience
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RemoteExperienceServicesConnector")]
    public class RemoteExperienceServicesConnector : ISharedRegionModule, IExperienceService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IExperienceService m_RemoteConnector;
        private bool m_Enabled = false;

        #region ISharedRegionModule

        public Type ReplaceableInterface => null;

        public string Name => "RemoteExperienceServicesConnector";

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig == null)
                return;

            // Opt-in only: activate solely when explicitly selected. Unset => G1 Local serves.
            string name = moduleConfig.GetString("ExperienceServices", string.Empty);
            if (name != Name)
                return;

            m_RemoteConnector = new ExperienceServicesConnector(source);
            m_Enabled = true;
            m_log.Info("[EXPERIENCE CONNECTOR]: Remote experience connector enabled (HTTP grid-service path).");
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

        #region IExperienceService (delegates to the HTTP client)

        // ── Experience CRUD ──
        public ExperienceInfo GetExperience(UUID experienceId) => m_RemoteConnector.GetExperience(experienceId);
        public ExperienceInfo GetExperienceByName(string name) => m_RemoteConnector.GetExperienceByName(name);
        public ExperienceInfo CreateExperience(ExperienceInfo info) => m_RemoteConnector.CreateExperience(info);
        public bool UpdateExperience(ExperienceInfo info) => m_RemoteConnector.UpdateExperience(info);
        public bool DeleteExperience(UUID experienceId) => m_RemoteConnector.DeleteExperience(experienceId);
        public List<ExperienceInfo> GetExperiencesByOwner(UUID ownerId) => m_RemoteConnector.GetExperiencesByOwner(ownerId);
        public List<ExperienceInfo> GetExperiencesByGroup(UUID groupId) => m_RemoteConnector.GetExperiencesByGroup(groupId);
        public bool IsExperienceContributor(UUID experienceId, UUID agentId) => m_RemoteConnector.IsExperienceContributor(experienceId, agentId);
        public bool IsExperienceAdmin(UUID experienceId, UUID agentId) => m_RemoteConnector.IsExperienceAdmin(experienceId, agentId);
        public List<ExperienceInfo> FindExperiences(string query) => m_RemoteConnector.FindExperiences(query);
        public List<ExperienceInfo> FindExperiences(string query, int offset, int limit) => m_RemoteConnector.FindExperiences(query, offset, limit);

        // ── Permission Grants ──
        public bool IsAgentGranted(UUID experienceId, UUID agentId) => m_RemoteConnector.IsAgentGranted(experienceId, agentId);
        public bool IsAgentBlocked(UUID experienceId, UUID agentId) => m_RemoteConnector.IsAgentBlocked(experienceId, agentId);
        public bool GrantPermission(UUID experienceId, UUID agentId) => m_RemoteConnector.GrantPermission(experienceId, agentId);
        public bool DenyPermission(UUID experienceId, UUID agentId) => m_RemoteConnector.DenyPermission(experienceId, agentId);
        public bool ForgetPermission(UUID experienceId, UUID agentId) => m_RemoteConnector.ForgetPermission(experienceId, agentId);
        public List<UUID> GetAgentExperiences(UUID agentId) => m_RemoteConnector.GetAgentExperiences(agentId);
        public List<UUID> GetAgentBlockedExperiences(UUID agentId) => m_RemoteConnector.GetAgentBlockedExperiences(agentId);
        public bool BlockExperienceForAgent(UUID agentId, UUID experienceId) => m_RemoteConnector.BlockExperienceForAgent(agentId, experienceId);
        public bool UnblockExperienceForAgent(UUID agentId, UUID experienceId) => m_RemoteConnector.UnblockExperienceForAgent(agentId, experienceId);

        // ── Key-Value Store ──
        public string ReadKeyValue(UUID experienceId, string key) => m_RemoteConnector.ReadKeyValue(experienceId, key);
        public bool CreateKeyValue(UUID experienceId, string key, string value) => m_RemoteConnector.CreateKeyValue(experienceId, key, value);
        public bool UpdateKeyValue(UUID experienceId, string key, string value, string check) => m_RemoteConnector.UpdateKeyValue(experienceId, key, value, check);
        public bool UpdateKeyValue(UUID experienceId, string key, string value, string check, bool conditional) => m_RemoteConnector.UpdateKeyValue(experienceId, key, value, check, conditional);
        public bool DeleteKeyValue(UUID experienceId, string key) => m_RemoteConnector.DeleteKeyValue(experienceId, key);
        public int KeyCountKeyValue(UUID experienceId) => m_RemoteConnector.KeyCountKeyValue(experienceId);
        public List<string> KeysKeyValue(UUID experienceId, int start, int count) => m_RemoteConnector.KeysKeyValue(experienceId, start, count);
        public long DataSizeKeyValue(UUID experienceId) => m_RemoteConnector.DataSizeKeyValue(experienceId);

        // ── Region Allow/Block/Trust Lists ──
        public List<UUID> GetAllowedExperiences(UUID regionId) => m_RemoteConnector.GetAllowedExperiences(regionId);
        public List<UUID> GetBlockedExperiences(UUID regionId) => m_RemoteConnector.GetBlockedExperiences(regionId);
        public List<UUID> GetTrustedExperiences(UUID regionId) => m_RemoteConnector.GetTrustedExperiences(regionId);
        public bool AllowExperience(UUID regionId, UUID experienceId) => m_RemoteConnector.AllowExperience(regionId, experienceId);
        public bool RemoveAllowedExperience(UUID regionId, UUID experienceId) => m_RemoteConnector.RemoveAllowedExperience(regionId, experienceId);
        public bool BlockExperience(UUID regionId, UUID experienceId) => m_RemoteConnector.BlockExperience(regionId, experienceId);
        public bool RemoveBlockedExperience(UUID regionId, UUID experienceId) => m_RemoteConnector.RemoveBlockedExperience(regionId, experienceId);
        public bool TrustExperience(UUID regionId, UUID experienceId) => m_RemoteConnector.TrustExperience(regionId, experienceId);
        public bool RemoveTrustedExperience(UUID regionId, UUID experienceId) => m_RemoteConnector.RemoveTrustedExperience(regionId, experienceId);

        // ── Script ↔ Experience association ──
        public void SetScriptExperiencePersisted(UUID itemId, UUID experienceId, UUID regionId) => m_RemoteConnector.SetScriptExperiencePersisted(itemId, experienceId, regionId);
        public UUID GetScriptExperiencePersisted(UUID itemId) => m_RemoteConnector.GetScriptExperiencePersisted(itemId);
        public void RemoveScriptExperiencePersisted(UUID itemId) => m_RemoteConnector.RemoveScriptExperiencePersisted(itemId);

        #endregion IExperienceService
    }
}
