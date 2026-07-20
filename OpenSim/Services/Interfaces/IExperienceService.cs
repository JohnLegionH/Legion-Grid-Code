/*
 * Legion Grid — Experience System
 * IExperienceService.cs — Grid service interface
 *
 * Place in: OpenSim/Services/Interfaces/IExperienceService.cs
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    public interface IExperienceService
    {
        // ── Experience CRUD ──
        ExperienceInfo GetExperience(UUID experienceId);
        ExperienceInfo GetExperienceByName(string name);
        ExperienceInfo CreateExperience(ExperienceInfo info);
        bool UpdateExperience(ExperienceInfo info);
        bool DeleteExperience(UUID experienceId);
        List<ExperienceInfo> GetExperiencesByOwner(UUID ownerId);
        List<ExperienceInfo> FindExperiences(string query);
        /// <summary>Paged name search (stable order) — offset/limit window for the
        /// FindExperienceByName cap's viewer-driven pagination.</summary>
        List<ExperienceInfo> FindExperiences(string query, int offset, int limit);

        // ── Permission Grants ──
        /// <summary>Returns true if agent has granted permission to this experience</summary>
        bool IsAgentGranted(UUID experienceId, UUID agentId);
        /// <summary>Returns true if agent has explicitly blocked this experience</summary>
        bool IsAgentBlocked(UUID experienceId, UUID agentId);
        bool GrantPermission(UUID experienceId, UUID agentId);
        bool DenyPermission(UUID experienceId, UUID agentId);
        bool ForgetPermission(UUID experienceId, UUID agentId);
        List<UUID> GetAgentExperiences(UUID agentId);

        // ── Key-Value Store ──
        /// <summary>Read a value. Returns null if key not found.</summary>
        string ReadKeyValue(UUID experienceId, string key);
        /// <summary>Create a new key. Returns false if key already exists.</summary>
        bool CreateKeyValue(UUID experienceId, string key, string value);
        /// <summary>Update existing key. If check is non-empty, only update if current value matches check.</summary>
        bool UpdateKeyValue(UUID experienceId, string key, string value, string check);
        /// <summary>Delete a key. Returns false if not found.</summary>
        bool DeleteKeyValue(UUID experienceId, string key);
        int KeyCountKeyValue(UUID experienceId);
        List<string> KeysKeyValue(UUID experienceId, int start, int count);
        /// <summary>Returns total bytes used by this experience's KV store.</summary>
        long DataSizeKeyValue(UUID experienceId);

        // ── Region Allow/Block Lists ──
        List<UUID> GetAllowedExperiences(UUID regionId);
        List<UUID> GetBlockedExperiences(UUID regionId);
        bool AllowExperience(UUID regionId, UUID experienceId);
        bool RemoveAllowedExperience(UUID regionId, UUID experienceId);
        bool BlockExperience(UUID regionId, UUID experienceId);
        bool RemoveBlockedExperience(UUID regionId, UUID experienceId);

        // ── Script ↔ Experience association persistence (EXP-PERSIST-1) ──
        /// <summary>Persist (or update) a script's experience association, keyed by script ItemID.</summary>
        void SetScriptExperiencePersisted(UUID itemId, UUID experienceId, UUID regionId);
        /// <summary>Read a script's persisted experience association. Returns UUID.Zero if none.</summary>
        UUID GetScriptExperiencePersisted(UUID itemId);
        /// <summary>Delete a script's persisted experience association.</summary>
        void RemoveScriptExperiencePersisted(UUID itemId);
    }
}
