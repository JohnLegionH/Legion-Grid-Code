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
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.ExperienceService;


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
            scene.UnregisterModuleInterface<IExperienceService>(m_Service);
            scene.UnregisterModuleInterface<ExperienceModule>(this);
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

        /// <summary>Get the experience UUID associated with a script, or UUID.Zero if none</summary>
        public UUID GetScriptExperience(UUID scriptItemId)
        {
            lock (m_ScriptExpLock)
            {
                return m_ScriptExperiences.TryGetValue(scriptItemId, out UUID expId) ? expId : UUID.Zero;
            }
        }

        /// <summary>Associate a script with an experience</summary>
        public void SetScriptExperience(UUID scriptItemId, UUID experienceId)
        {
            lock (m_ScriptExpLock)
            {
                if (experienceId == UUID.Zero)
                    m_ScriptExperiences.Remove(scriptItemId);
                else
                    m_ScriptExperiences[scriptItemId] = experienceId;
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
    }
}
