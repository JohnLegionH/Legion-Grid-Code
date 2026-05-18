/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Administrative console commands for the destruction repair system
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class RepairAdminCommands : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[REPAIR ADMIN COMMANDS]";

        private readonly Dictionary<string, Scene> m_scenes = new Dictionary<string, Scene>();
        private readonly Dictionary<string, DestructionRepairSystem> m_repairSystems = new Dictionary<string, DestructionRepairSystem>();
        private bool m_enabled = false;

        #region ISharedRegionModule Implementation

        public string Name => "RepairAdminCommands";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig destructionConfig = source.Configs["BulletSim"];
            if (destructionConfig != null)
            {
                m_enabled = destructionConfig.GetBoolean("EnableDestructiblePhysics", false) &&
                           destructionConfig.GetBoolean("EnableRepairSystem", true);
            }

            if (m_enabled)
            {
                m_log.InfoFormat("{0}: Repair admin commands initialized", LogHeader);
            }
        }

        public void PostInitialise() { }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
            {
                m_scenes[scene.RegionInfo.RegionName] = scene;
            }

            // Add console commands
            scene.AddCommand("Repair", this, "repair status", "repair status [<region>]",
                "Show repair system status and pending repairs", HandleRepairStatus);

            scene.AddCommand("Repair", this, "repair list", "repair list [<region>]",
                "List all objects awaiting repair", HandleRepairList);

            scene.AddCommand("Repair", this, "repair start", "repair start <object_id> [<quality>] [<region>]",
                "Manually start repair of a destroyed object", HandleRepairStart);

            scene.AddCommand("Repair", this, "repair cancel", "repair cancel <object_id> [<region>]",
                "Cancel an active repair", HandleRepairCancel);

            scene.AddCommand("Repair", this, "repair auto", "repair auto <enable|disable> [<time>] [<region>]",
                "Enable/disable automatic repairs with optional time setting", HandleRepairAuto);

            scene.AddCommand("Repair", this, "repair cleanup", "repair cleanup [<region>]",
                "Clean up old destruction records", HandleRepairCleanup);

            scene.AddCommand("Repair", this, "repair all", "repair all [<quality>] [<region>]",
                "Repair all objects awaiting repair", HandleRepairAll);

            scene.AddCommand("Repair", this, "repair config", "repair config [<param>] [<value>] [<region>]",
                "Get/set repair system configuration", HandleRepairConfig);

            scene.AddCommand("Repair", this, "repair save", "repair save [<region>]",
                "Manually save destruction records and repair jobs to persistent storage", HandleRepairSave);

            scene.AddCommand("Repair", this, "repair load", "repair load [<region>]",
                "Manually load destruction records and repair jobs from persistent storage", HandleRepairLoad);

            scene.AddCommand("Repair", this, "repair purge", "repair purge [<days>] [<region>]",
                "Clean up persistent data older than specified days (default: 7)", HandleRepairPurge);

            m_log.InfoFormat("{0}: Added repair admin commands for region {1}", LogHeader, scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
            {
                m_scenes.Remove(scene.RegionInfo.RegionName);
                if (m_repairSystems.TryGetValue(scene.RegionInfo.RegionName, out var repairSystem))
                {
                    repairSystem.Dispose();
                    m_repairSystems.Remove(scene.RegionInfo.RegionName);
                }
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            // Initialize repair system for this region after physics is loaded
            try
            {
                var physicsScene = scene.PhysicsScene as BSScene;
                if (physicsScene != null)
                {
                    var integration = GetAdvancedPhysicsIntegration(physicsScene);
                    if (integration?.DestructibleSystem != null)
                    {
                        var repairSystem = new DestructionRepairSystem(scene, integration.DestructibleSystem);
                        
                        lock (m_repairSystems)
                        {
                            m_repairSystems[scene.RegionInfo.RegionName] = repairSystem;
                        }
                        
                        m_log.InfoFormat("{0}: Repair system ready for region {1}", LogHeader, scene.RegionInfo.RegionName);
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error initializing repair system for region {1}: {2}", 
                    LogHeader, scene.RegionInfo.RegionName, ex.Message);
            }
        }

        public void Close() { }

        #endregion

        #region Command Handlers

        private void HandleRepairStatus(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var repairSystems = GetTargetRepairSystems(regionName);

            if (!repairSystems.Any())
            {
                MainConsole.Instance.Output("No repair systems available.");
                return;
            }

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                MainConsole.Instance.Output($"\n=== Repair System Status - {region} ===");
                MainConsole.Instance.Output($"Enabled: {repairSystem.Enabled}");
                MainConsole.Instance.Output($"Auto-Repair: {repairSystem.AutoRepairEnabled}");
                MainConsole.Instance.Output($"Default Repair Time: {repairSystem.DefaultRepairTime:F1} seconds");
                MainConsole.Instance.Output($"Resource Mode: {repairSystem.ResourceMode}");

                var activeRepairs = repairSystem.GetActiveRepairs();
                var awaitingRepair = repairSystem.GetObjectsAwaitingRepair();

                MainConsole.Instance.Output($"Active Repairs: {activeRepairs.Count}");
                MainConsole.Instance.Output($"Awaiting Repair: {awaitingRepair.Count}");

                if (activeRepairs.Any())
                {
                    MainConsole.Instance.Output("\nActive Repairs:");
                    foreach (var repair in activeRepairs)
                    {
                        var timeRemaining = repair.EstimatedCompletion - DateTime.UtcNow;
                        MainConsole.Instance.Output($"  • Object {repair.ObjectId}: {repair.Progress:P1} complete " +
                            $"(ETA: {Math.Max(0, timeRemaining.TotalSeconds):F0}s)");
                    }
                }
            }
        }

        private void HandleRepairList(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                var awaitingRepair = repairSystem.GetObjectsAwaitingRepair();

                MainConsole.Instance.Output($"\n=== Objects Awaiting Repair - {region} ===");
                
                if (!awaitingRepair.Any())
                {
                    MainConsole.Instance.Output("No objects awaiting repair.");
                    continue;
                }

                foreach (var record in awaitingRepair.OrderBy(r => r.DestructionTime))
                {
                    var timeSinceDestruction = DateTime.UtcNow - record.DestructionTime;
                    MainConsole.Instance.Output($"Object {record.ObjectId}: '{record.OriginalName}'");
                    MainConsole.Instance.Output($"  Material: {record.Material}");
                    MainConsole.Instance.Output($"  Position: {record.Position}");
                    MainConsole.Instance.Output($"  Destroyed: {timeSinceDestruction.TotalMinutes:F1} minutes ago");
                    MainConsole.Instance.Output($"  Cause: {record.Cause}");
                    MainConsole.Instance.Output($"  Owner: {record.OwnerId}");
                    MainConsole.Instance.Output("");
                }
            }
        }

        private void HandleRepairStart(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3)
            {
                MainConsole.Instance.Output("Usage: repair start <object_id> [<quality>] [<region>]");
                MainConsole.Instance.Output("Quality: 0.1 to 1.0 (default: 1.0)");
                return;
            }

            if (!uint.TryParse(cmdparams[2], out uint objectId))
            {
                MainConsole.Instance.Output("Invalid object ID. Must be a number.");
                return;
            }

            float quality = 1.0f;
            if (cmdparams.Length > 3 && !float.TryParse(cmdparams[3], out quality))
            {
                MainConsole.Instance.Output("Invalid quality value. Must be between 0.1 and 1.0.");
                return;
            }

            quality = Math.Max(0.1f, Math.Min(1.0f, quality));

            string regionName = GetRegionFromParams(cmdparams, cmdparams.Length > 4 ? 4 : 3);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                var options = new RepairOptions
                {
                    RepairQuality = quality,
                    MakeDestructible = true,
                    PreserveName = false
                };

                if (repairSystem.StartRepair(objectId, OMV.UUID.Zero, options)) // Admin repair
                {
                    MainConsole.Instance.Output($"Started repair of object {objectId} in {region} (quality: {quality:P0})");
                }
                else
                {
                    MainConsole.Instance.Output($"Failed to start repair of object {objectId} in {region}");
                }
            }
        }

        private void HandleRepairCancel(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3)
            {
                MainConsole.Instance.Output("Usage: repair cancel <object_id> [<region>]");
                return;
            }

            if (!uint.TryParse(cmdparams[2], out uint objectId))
            {
                MainConsole.Instance.Output("Invalid object ID. Must be a number.");
                return;
            }

            string regionName = GetRegionFromParams(cmdparams, 3);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                if (repairSystem.CancelRepair(objectId, OMV.UUID.Zero)) // Admin cancel
                {
                    MainConsole.Instance.Output($"Cancelled repair of object {objectId} in {region}");
                }
                else
                {
                    MainConsole.Instance.Output($"Could not cancel repair of object {objectId} in {region} (not found or not active)");
                }
            }
        }

        private void HandleRepairAuto(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3)
            {
                MainConsole.Instance.Output("Usage: repair auto <enable|disable> [<time>] [<region>]");
                MainConsole.Instance.Output("Time: repair delay in seconds (default: 60)");
                return;
            }

            bool enable = cmdparams[2].ToLower() == "enable";
            
            float repairTime = 60.0f;
            if (cmdparams.Length > 3 && !float.TryParse(cmdparams[3], out repairTime))
            {
                MainConsole.Instance.Output("Invalid time value. Must be a number in seconds.");
                return;
            }

            string regionName = GetRegionFromParams(cmdparams, cmdparams.Length > 4 ? 4 : 3);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                repairSystem.AutoRepairEnabled = enable;
                if (repairTime > 0)
                {
                    repairSystem.DefaultRepairTime = repairTime;
                }

                MainConsole.Instance.Output($"Region {region}: Auto-repair {(enable ? "enabled" : "disabled")}");
                if (enable && repairTime > 0)
                {
                    MainConsole.Instance.Output($"  Repair time set to {repairTime:F1} seconds");
                }
            }
        }

        private void HandleRepairCleanup(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                repairSystem.CleanupDestructionHistory();
                MainConsole.Instance.Output($"Cleaned up old destruction records in {region}");
            }
        }

        private void HandleRepairAll(string module, string[] cmdparams)
        {
            float quality = 0.9f; // Default to slightly imperfect mass repairs
            if (cmdparams.Length > 2 && !float.TryParse(cmdparams[2], out quality))
            {
                MainConsole.Instance.Output("Invalid quality value. Must be between 0.1 and 1.0.");
                return;
            }

            quality = Math.Max(0.1f, Math.Min(1.0f, quality));

            string regionName = GetRegionFromParams(cmdparams, cmdparams.Length > 3 ? 3 : 2);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                var awaitingRepair = repairSystem.GetObjectsAwaitingRepair();
                int repaired = 0;

                var options = new RepairOptions
                {
                    RepairQuality = quality,
                    MakeDestructible = true,
                    PreserveName = false
                };

                foreach (var record in awaitingRepair)
                {
                    if (repairSystem.StartRepair(record.ObjectId, OMV.UUID.Zero, options))
                    {
                        repaired++;
                    }
                }

                MainConsole.Instance.Output($"Region {region}: Started repair of {repaired}/{awaitingRepair.Count} objects (quality: {quality:P0})");
            }
        }

        private void HandleRepairConfig(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3)
            {
                ShowRepairConfigParameters();
                return;
            }

            string paramName = cmdparams[2];
            string regionName = GetRegionFromParams(cmdparams, 4);
            var repairSystems = GetTargetRepairSystems(regionName);

            if (cmdparams.Length == 3)
            {
                // Get parameter value
                foreach (var kvp in repairSystems)
                {
                    var region = kvp.Key;
                    var repairSystem = kvp.Value;
                    
                    string value = GetConfigValue(repairSystem, paramName);
                    MainConsole.Instance.Output($"Region {region}: {paramName} = {value}");
                }
            }
            else if (cmdparams.Length >= 4)
            {
                // Set parameter value
                string paramValue = cmdparams[3];
                foreach (var kvp in repairSystems)
                {
                    var region = kvp.Key;
                    var repairSystem = kvp.Value;

                    if (SetConfigValue(repairSystem, paramName, paramValue))
                    {
                        MainConsole.Instance.Output($"Region {region}: Set {paramName} = {paramValue}");
                    }
                    else
                    {
                        MainConsole.Instance.Output($"Region {region}: Failed to set {paramName}");
                    }
                }
            }
        }

        private void HandleRepairSave(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                try
                {
                    // Use reflection to access the SavePersistentDataAsync method
                    var method = repairSystem.GetType().GetMethod("SavePersistentDataAsync", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (method != null)
                    {
                        var result = method.Invoke(repairSystem, null);
                        if (result is Task task)
                        {
                            task.GetAwaiter().GetResult(); // Safer than Wait()
                            MainConsole.Instance.Output($"Region {region}: Destruction records and repair jobs saved to persistent storage");
                        }
                        else
                        {
                            MainConsole.Instance.Output($"Region {region}: Save method did not return a Task");
                        }
                    }
                    else
                    {
                        MainConsole.Instance.Output($"Region {region}: Save method not found");
                    }
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Region {region}: Error saving persistent data: {ex.Message}");
                }
            }
        }

        private void HandleRepairLoad(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                try
                {
                    // Use reflection to access the LoadPersistentDataAsync method
                    var method = repairSystem.GetType().GetMethod("LoadPersistentDataAsync", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (method != null)
                    {
                        var result = method.Invoke(repairSystem, null);
                        if (result is Task task)
                        {
                            task.GetAwaiter().GetResult();
                            MainConsole.Instance.Output($"Region {region}: Destruction records and repair jobs loaded from persistent storage");
                        }
                        else
                        {
                            MainConsole.Instance.Output($"Region {region}: Load completed (non-async)");
                        }
                    }
                    else
                    {
                        MainConsole.Instance.Output($"Region {region}: Load method not found");
                    }
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Region {region}: Error loading persistent data: {ex.Message}");
                }
            }
        }

        private void HandleRepairPurge(string module, string[] cmdparams)
        {
            int days = 7; // Default
            if (cmdparams.Length > 2 && !int.TryParse(cmdparams[2], out days))
            {
                MainConsole.Instance.Output("Invalid days value. Must be a number.");
                return;
            }

            days = Math.Max(1, days); // Minimum 1 day

            string regionName = GetRegionFromParams(cmdparams, cmdparams.Length > 3 ? 3 : 2);
            var repairSystems = GetTargetRepairSystems(regionName);

            foreach (var kvp in repairSystems)
            {
                var region = kvp.Key;
                var repairSystem = kvp.Value;

                try
                {
                    var maxAge = TimeSpan.FromDays(days);
                    
                    // Use reflection to access the CleanupOldPersistentData method
                    var method = repairSystem.GetType().GetMethod("CleanupOldPersistentData", 
                        BindingFlags.Public | BindingFlags.Instance);
                    if (method != null)
                    {
                        var result = method.Invoke(repairSystem, new object[] { maxAge });
                        if (result is Task task)
                        {
                            task.GetAwaiter().GetResult(); // Safer than Wait()
                            MainConsole.Instance.Output($"Region {region}: Purged persistent data older than {days} days");
                        }
                        else
                        {
                            MainConsole.Instance.Output($"Region {region}: Purge method did not return a Task");
                        }
                    }
                    else
                    {
                        MainConsole.Instance.Output($"Region {region}: Purge method not found");
                    }
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Region {region}: Error purging persistent data: {ex.Message}");
                }
            }
        }

        #endregion

        #region Helper Methods

        private string GetRegionFromParams(string[] cmdparams, int index)
        {
            return cmdparams.Length > index ? cmdparams[index] : null;
        }

        private Dictionary<string, DestructionRepairSystem> GetTargetRepairSystems(string regionName)
        {
            var targetSystems = new Dictionary<string, DestructionRepairSystem>();
            
            lock (m_repairSystems)
            {
                if (string.IsNullOrEmpty(regionName))
                {
                    foreach (var kvp in m_repairSystems)
                    {
                        targetSystems[kvp.Key] = kvp.Value;
                    }
                }
                else
                {
                    if (m_repairSystems.TryGetValue(regionName, out var repairSystem))
                    {
                        targetSystems[regionName] = repairSystem;
                    }
                }
            }

            return targetSystems;
        }

        private AdvancedPhysicsIntegration GetAdvancedPhysicsIntegration(BSScene physicsScene)
        {
            try
            {
                var field = physicsScene.GetType().GetField("m_advancedPhysics", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                return field?.GetValue(physicsScene) as AdvancedPhysicsIntegration;
            }
            catch
            {
                return null;
            }
        }

        private void ShowRepairConfigParameters()
        {
            MainConsole.Instance.Output("Available repair system parameters:");
            MainConsole.Instance.Output("  Enabled - Enable/disable the repair system");
            MainConsole.Instance.Output("  AutoRepairEnabled - Enable/disable automatic repairs");
            MainConsole.Instance.Output("  DefaultRepairTime - Default repair time in seconds");
            MainConsole.Instance.Output("  ResourceMode - Resource requirements (None, Material, Energy, Both)");
            MainConsole.Instance.Output("Usage: repair config <parameter> [<value>] [<region>]");
        }

        private string GetConfigValue(DestructionRepairSystem repairSystem, string paramName)
        {
            switch (paramName.ToLower())
            {
                case "enabled":
                    return repairSystem.Enabled.ToString();
                case "autorepairenabled":
                    return repairSystem.AutoRepairEnabled.ToString();
                case "defaultrepairtime":
                    return repairSystem.DefaultRepairTime.ToString("F1");
                case "resourcemode":
                    return repairSystem.ResourceMode.ToString();
                default:
                    return "Unknown parameter";
            }
        }

        private bool SetConfigValue(DestructionRepairSystem repairSystem, string paramName, string value)
        {
            try
            {
                switch (paramName.ToLower())
                {
                    case "enabled":
                        if (bool.TryParse(value, out bool enabled))
                        {
                            repairSystem.Enabled = enabled;
                            return true;
                        }
                        break;

                    case "autorepairenabled":
                        if (bool.TryParse(value, out bool autoEnabled))
                        {
                            repairSystem.AutoRepairEnabled = autoEnabled;
                            return true;
                        }
                        break;

                    case "defaultrepairtime":
                        if (float.TryParse(value, out float repairTime))
                        {
                            repairSystem.DefaultRepairTime = repairTime;
                            return true;
                        }
                        break;

                    case "resourcemode":
                        if (Enum.TryParse<RepairResourceMode>(value, true, out var resourceMode))
                        {
                            repairSystem.ResourceMode = resourceMode;
                            return true;
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error setting config value {1}: {2}", LogHeader, paramName, ex.Message);
            }

            return false;
        }

        #endregion
    }
}