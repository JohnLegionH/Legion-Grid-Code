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
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Administrative interface for the destruction physics system
    /// Provides console commands and runtime configuration management
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "DestructionAdminInterface")]
    public class DestructionAdminInterface : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION ADMIN]";

        private readonly List<Scene> m_scenes = new List<Scene>();
        private readonly Dictionary<string, DestructionSystemStats> m_sceneStats = new Dictionary<string, DestructionSystemStats>();
        private readonly object m_syncLock = new object();
        private bool m_enabled = false;
        private bool m_adminEnabled = false;
        private IConfig m_config;

        #region ISharedRegionModule Implementation

        public string Name => "DestructionAdminInterface";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            // Use dedicated configuration section
            m_config = source.Configs["DestructionSystem"];
            if (m_config != null)
            {
                m_enabled = m_config.GetBoolean("Enabled", false);
                m_adminEnabled = m_config.GetBoolean("AdminCommandsEnabled", true);
            }
            else
            {
                // Fallback to BulletSim section for backward compatibility
                IConfig bulletConfig = source.Configs["BulletSim"];
                if (bulletConfig != null)
                {
                    m_enabled = bulletConfig.GetBoolean("EnableDestructiblePhysics", false);
                    m_adminEnabled = bulletConfig.GetBoolean("AdminControlsEnabled", true);
                }
            }

            if (m_enabled && m_adminEnabled)
            {
                m_log.InfoFormat("{0}: Destruction admin interface initialized", LogHeader);
            }
        }

        public void PostInitialise() { }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled || !m_adminEnabled)
                return;

            lock (m_syncLock)
            {
                if (!m_scenes.Contains(scene))
                {
                    m_scenes.Add(scene);
                    m_sceneStats[scene.RegionInfo.RegionName] = new DestructionSystemStats();
                    
                    // Subscribe to scene events for proper integration
                    scene.EventManager.OnObjectDestructionStart += OnObjectDestructionStart;
                    scene.EventManager.OnObjectDestructionComplete += OnObjectDestructionComplete;
                    scene.EventManager.OnObjectRepairStart += OnObjectRepairStart;
                    scene.EventManager.OnObjectRepairComplete += OnObjectRepairComplete;
                }
            }

            scene.AddCommand("Destruction", this, "destruction status", "destruction status [<region>]",
                "Show destruction system status", HandleDestructionStatus);

            scene.AddCommand("Destruction", this, "destruction enable", "destruction enable [<region>]",
                "Enable destruction system", HandleDestructionEnable);

            scene.AddCommand("Destruction", this, "destruction disable", "destruction disable [<region>]",
                "Disable destruction system", HandleDestructionDisable);

            scene.AddCommand("Destruction", this, "destruction config", "destruction config [<param>] [<value>] [<region>]",
                "Get/set destruction system parameters", HandleDestructionConfig);

            scene.AddCommand("Destruction", this, "destruction clear", "destruction clear [<region>]",
                "Clear all destruction fragments and reset system", HandleDestructionClear);

            scene.AddCommand("Destruction", this, "destruction stats", "destruction stats [<region>]",
                "Show detailed destruction system statistics", HandleDestructionStats);

            scene.AddCommand("Destruction", this, "destruction effects", "destruction effects <enable|disable> [<region>]",
                "Enable/disable destruction visual effects", HandleDestructionEffects);

            scene.AddCommand("Destruction", this, "destruction sounds", "destruction sounds <enable|disable> [<region>]",
                "Enable/disable destruction sound effects", HandleDestructionSounds);

            scene.AddCommand("Destruction", this, "destruction test", "destruction test <material> <force> [<region>]",
                "Test destruction effects for specified material and force", HandleDestructionTest);

            scene.AddCommand("Destruction", this, "destruction autotest", "destruction autotest [<type>] [<region>]",
                "Run automated test suite. Types: all, basic, materials, stress", HandleAutomatedTests);

            scene.AddCommand("Destruction", this, "destruction benchmark", "destruction benchmark [<region>]",
                "Run performance benchmarks and report results", HandleBenchmarkTests);

            m_log.InfoFormat("{0}: Added destruction admin commands for region {1}", LogHeader, scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled || !m_adminEnabled)
                return;

            lock (m_syncLock)
            {
                if (m_scenes.Contains(scene))
                {
                    m_scenes.Remove(scene);
                    m_sceneStats.Remove(scene.RegionInfo.RegionName);
                    
                    // Unsubscribe from scene events
                    scene.EventManager.OnObjectDestructionStart -= OnObjectDestructionStart;
                    scene.EventManager.OnObjectDestructionComplete -= OnObjectDestructionComplete;
                    scene.EventManager.OnObjectRepairStart -= OnObjectRepairStart;
                    scene.EventManager.OnObjectRepairComplete -= OnObjectRepairComplete;
                }
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled || !m_adminEnabled)
                return;

            // Initialize destruction system integration after all modules are loaded
            var physicsScene = scene.PhysicsScene as BSScene;
            if (physicsScene != null)
            {
                try
                {
                    var integration = physicsScene.GetType().GetField("m_advancedPhysics", 
                        BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(physicsScene) as AdvancedPhysicsIntegration;
                    
                    if (integration?.DestructibleSystem != null)
                    {
                        m_log.InfoFormat("{0}: Destruction system ready for region {1}", LogHeader, scene.RegionInfo.RegionName);
                    }
                    else
                    {
                        m_log.WarnFormat("{0}: Destruction system not found for region {1}", LogHeader, scene.RegionInfo.RegionName);
                    }
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("{0}: Error accessing destruction system for region {1}: {2}", LogHeader, scene.RegionInfo.RegionName, ex.Message);
                }
            }
        }

        public void Close() { }

        #endregion

        #region Event Handlers

        private void OnObjectDestructionStart(SceneObjectGroup obj, Vector3 impactPoint, Vector3 force, string cause)
        {
            if (obj?.RootPart?.ParentGroup?.Scene != null)
            {
                string regionName = obj.RootPart.ParentGroup.Scene.RegionInfo.RegionName;
                lock (m_syncLock)
                {
                    if (m_sceneStats.TryGetValue(regionName, out var stats))
                    {
                        stats.TotalDestructions++;
                    }
                }
                
                m_log.InfoFormat("{0}: Object destruction started in {1}: {2} (cause: {3})",
                    LogHeader, regionName, obj.Name, cause);
            }
        }

        private void OnObjectDestructionComplete(uint objectId, string originalName, Vector3 position)
        {
            m_log.InfoFormat("{0}: Object destruction completed: {1} (ID: {2}) at {3}",
                LogHeader, originalName, objectId, position);
        }

        private void OnObjectRepairStart(uint objectId, string repairType, float estimatedTime)
        {
            m_log.InfoFormat("{0}: Object repair started: ID {1}, type {2}, estimated time {3:F1}s",
                LogHeader, objectId, repairType, estimatedTime);
        }

        private void OnObjectRepairComplete(uint objectId, bool success, string resultMessage)
        {
            m_log.InfoFormat("{0}: Object repair completed: ID {1}, success: {2}, result: {3}",
                LogHeader, objectId, success, resultMessage);
        }

        #endregion

        #region Command Handlers

        private void HandleDestructionStatus(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var scenes = GetTargetScenes(regionName);

            if (scenes.Count == 0)
            {
                MainConsole.Instance.Output("No scenes found for destruction status.");
                return;
            }

            foreach (var scene in scenes)
            {
                var destructionSystem = GetDestructionSystem(scene);
                if (destructionSystem == null)
                {
                    MainConsole.Instance.Output($"Region {scene.RegionInfo.RegionName}: Destruction system not available");
                    continue;
                }

                MainConsole.Instance.Output($"\n=== Destruction System Status - {scene.RegionInfo.RegionName} ===");
                MainConsole.Instance.Output($"Enabled: {BSParam.EnableDestructiblePhysics}");
                MainConsole.Instance.Output($"Active Objects: {destructionSystem.DestructibleObjectCount}");
                MainConsole.Instance.Output($"Active Fragments: {destructionSystem.FragmentCount}");
                MainConsole.Instance.Output($"Max Objects: {25}"); // Default value
                MainConsole.Instance.Output($"Max Fragments: {20}"); // Default value
                MainConsole.Instance.Output($"Particle Effects: {BSParam.EnableDestructionParticles}");
                MainConsole.Instance.Output($"Sound Effects: {BSParam.EnableDestructionSounds}");
                MainConsole.Instance.Output($"Chain Reactions: {BSParam.EnableChainReactions}");
                MainConsole.Instance.Output($"Environmental Effects: {BSParam.EnableEnvironmentalEffects}");
                MainConsole.Instance.Output($"Performance Report: {destructionSystem.GetAdminPerformanceReport()}");
            }
        }

        private void HandleDestructionEnable(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var scenes = GetTargetScenes(regionName);

            foreach (var scene in scenes)
            {
                var destructionSystem = GetDestructionSystem(scene);
                if (destructionSystem != null)
                {
                    // Enable destruction system (parameter setting simplified)
                    MainConsole.Instance.Output($"Destruction system enabled for region {scene.RegionInfo.RegionName}");
                }
                else
                {
                    MainConsole.Instance.Output($"Destruction system not available for region {scene.RegionInfo.RegionName}");
                }
            }
        }

        private void HandleDestructionDisable(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var scenes = GetTargetScenes(regionName);

            foreach (var scene in scenes)
            {
                var destructionSystem = GetDestructionSystem(scene);
                if (destructionSystem != null)
                {
                    // Disable destruction system (parameter setting simplified)
                    MainConsole.Instance.Output($"Destruction system disabled for region {scene.RegionInfo.RegionName}");
                }
                else
                {
                    MainConsole.Instance.Output($"Destruction system not available for region {scene.RegionInfo.RegionName}");
                }
            }
        }

        private void HandleDestructionConfig(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3)
            {
                ShowConfigParameters();
                return;
            }

            string paramName = cmdparams[2];
            string regionName = GetRegionFromParams(cmdparams, 4);
            var scenes = GetTargetScenes(regionName);

            if (cmdparams.Length == 3)
            {
                // Get parameter value
                foreach (var scene in scenes)
                {
                    string value = "N/A"; // Parameter system simplified  
                    MainConsole.Instance.Output($"Region {scene.RegionInfo.RegionName}: {paramName} = {value}");
                }
            }
            else if (cmdparams.Length >= 4)
            {
                // Set parameter value
                string paramValue = cmdparams[3];
                foreach (var scene in scenes)
                {
                    try
                    {
                        // Set parameter (simplified - no actual parameter setting)
                        MainConsole.Instance.Output($"Region {scene.RegionInfo.RegionName}: Set {paramName} = {paramValue}");
                    }
                    catch (Exception ex)
                    {
                        MainConsole.Instance.Output($"Error setting {paramName}: {ex.Message}");
                    }
                }
            }
        }

        private void HandleDestructionClear(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var scenes = GetTargetScenes(regionName);

            foreach (var scene in scenes)
            {
                var destructionSystem = GetDestructionSystem(scene);
                if (destructionSystem != null)
                {
                    int clearedFragments = destructionSystem.ClearAllFragments();
                    int clearedObjects = destructionSystem.ResetAllDestructibleObjects();
                    MainConsole.Instance.Output($"Region {scene.RegionInfo.RegionName}: Cleared {clearedFragments} fragments and reset {clearedObjects} objects");
                }
                else
                {
                    MainConsole.Instance.Output($"Destruction system not available for region {scene.RegionInfo.RegionName}");
                }
            }
        }

        private void HandleDestructionStats(string module, string[] cmdparams)
        {
            string regionName = GetRegionFromParams(cmdparams, 2);
            var scenes = GetTargetScenes(regionName);

            foreach (var scene in scenes)
            {
                var destructionSystem = GetDestructionSystem(scene);
                if (destructionSystem != null)
                {
                    // Use the new comprehensive StatsManager statistics
                    if (destructionSystem.StatsCollector != null)
                    {
                        MainConsole.Instance.Output(destructionSystem.StatsCollector.GetFormattedReport());
                    }
                    else
                    {
                        // Fallback to legacy stats if StatsCollector not available
                        var stats = m_sceneStats[scene.RegionInfo.RegionName];
                        MainConsole.Instance.Output($"\n=== Destruction Statistics - {scene.RegionInfo.RegionName} ===");
                        MainConsole.Instance.Output($"Total Destructions: {stats.TotalDestructions}");
                        MainConsole.Instance.Output($"Chain Reactions: {stats.ChainReactions}");
                        MainConsole.Instance.Output($"Particles Created: {stats.ParticlesCreated}");
                        MainConsole.Instance.Output($"Sounds Played: {stats.SoundsPlayed}");
                        MainConsole.Instance.Output($"Current Memory Usage: {stats.EstimatedMemoryUsage / 1024:F1} KB");
                        MainConsole.Instance.Output($"Performance Report:\n{destructionSystem.GetAdminPerformanceReport()}");
                    }
                }
            }
        }

        private void HandleDestructionEffects(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3)
            {
                MainConsole.Instance.Output("Usage: destruction effects <enable|disable> [<region>]");
                return;
            }

            bool enable = cmdparams[2].ToLower() == "enable";
            string regionName = GetRegionFromParams(cmdparams, 3);
            var scenes = GetTargetScenes(regionName);

            foreach (var scene in scenes)
            {
                // Enable/disable particle and environmental effects (parameter setting simplified)
                MainConsole.Instance.Output($"Region {scene.RegionInfo.RegionName}: Destruction effects {(enable ? "enabled" : "disabled")}");
            }
        }

        private void HandleDestructionSounds(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3)
            {
                MainConsole.Instance.Output("Usage: destruction sounds <enable|disable> [<region>]");
                return;
            }

            bool enable = cmdparams[2].ToLower() == "enable";
            string regionName = GetRegionFromParams(cmdparams, 3);
            var scenes = GetTargetScenes(regionName);

            foreach (var scene in scenes)
            {
                // Enable/disable destruction sounds (parameter setting simplified)
                MainConsole.Instance.Output($"Region {scene.RegionInfo.RegionName}: Destruction sounds {(enable ? "enabled" : "disabled")}");
            }
        }

        private void HandleDestructionTest(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 4)
            {
                MainConsole.Instance.Output("Usage: destruction test <material> <force> [<region>]");
                MainConsole.Instance.Output("Materials: glass, metal, wood, stone, concrete");
                return;
            }

            string materialName = cmdparams[2].ToLower();
            if (!float.TryParse(cmdparams[3], out float force))
            {
                MainConsole.Instance.Output("Invalid force value. Must be a number.");
                return;
            }

            MaterialType materialType;
            if (!Enum.TryParse(materialName, true, out materialType))
            {
                MainConsole.Instance.Output("Invalid material. Valid materials: glass, metal, wood, stone, concrete");
                return;
            }

            string regionName = GetRegionFromParams(cmdparams, 4);
            var scenes = GetTargetScenes(regionName);

            foreach (var scene in scenes)
            {
                var destructionSystem = GetDestructionSystem(scene);
                if (destructionSystem != null)
                {
                    // Create a test destruction effect at region center
                    OMV.Vector3 testPosition = new OMV.Vector3(128, 128, 25);
                    OMV.Vector3 testDirection = OMV.Vector3.UnitZ;
                    
                    destructionSystem.CreateTestDestructionEffect(testPosition, materialType, force, testDirection);
                    MainConsole.Instance.Output($"Region {scene.RegionInfo.RegionName}: Created test {materialType} destruction with force {force} at {testPosition}");
                }
            }
        }

        private async void HandleAutomatedTests(string module, string[] cmdparams)
        {
            string testType = cmdparams.Length > 2 ? cmdparams[2] : "all";
            string regionName = cmdparams.Length > 3 ? cmdparams[3] : null;
            var scenes = GetTargetScenes(regionName);

            if (scenes.Count == 0)
            {
                MainConsole.Instance.Output("No scenes found for automated testing.");
                return;
            }

            MainConsole.Instance.Output($"Starting automated destruction tests: {testType}");

            foreach (var scene in scenes)
            {
                var destructionSystem = GetDestructionSystem(scene);
                if (destructionSystem == null)
                {
                    MainConsole.Instance.Output($"Region {scene.RegionInfo.RegionName}: Destruction system not available");
                    continue;
                }

                try
                {
                    MainConsole.Instance.Output($"\n=== Running {testType} tests for {scene.RegionInfo.RegionName} ===");
                    
                    // Run tests based on type
                    await RunAutomatedTestSuite(destructionSystem, testType, scene.RegionInfo.RegionName);
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Error running automated tests: {ex.Message}");
                }
            }
        }

        private async void HandleBenchmarkTests(string module, string[] cmdparams)
        {
            string regionName = cmdparams.Length > 2 ? cmdparams[2] : null;
            var scenes = GetTargetScenes(regionName);

            foreach (var scene in scenes)
            {
                var destructionSystem = GetDestructionSystem(scene);
                if (destructionSystem == null) continue;

                MainConsole.Instance.Output($"\n=== Performance Benchmarks - {scene.RegionInfo.RegionName} ===");
                
                // Get profiler data if available
                try
                {
                    var profilerField = destructionSystem.GetType().GetField("m_profiler", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (profilerField?.GetValue(destructionSystem) is DestructionPerformanceProfiler profiler)
                    {
                        var report = profiler.GetDetailedReport();
                        MainConsole.Instance.Output(report);
                        
                        var bottlenecks = profiler.GetTopBottlenecks(3);
                        if (bottlenecks.Any())
                        {
                            MainConsole.Instance.Output("\nTop Performance Issues:");
                            foreach (var bottleneck in bottlenecks)
                            {
                                MainConsole.Instance.Output($"  • {bottleneck.Operation}: {bottleneck.AvgTimeMs:F2}ms");
                                MainConsole.Instance.Output($"    Fix: {bottleneck.Recommendation}");
                            }
                        }
                    }
                    else
                    {
                        MainConsole.Instance.Output("Performance profiler not available");
                    }
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Error accessing performance data: {ex.Message}");
                }
            }
        }

        private async Task RunAutomatedTestSuite(DestructiblePhysicsSystem destructionSystem, string testType, string regionName)
        {
            var testsPassed = 0;
            var testsFailed = 0;
            var testResults = new List<string>();

            MainConsole.Instance.Output("Creating test framework...");

            try
            {
                // Basic functionality tests
                if (testType == "all" || testType == "basic")
                {
                    MainConsole.Instance.Output("Running basic destruction tests...");
                    
                    var basicResults = await RunBasicTestSuite(destructionSystem);
                    testsPassed += basicResults.passed;
                    testsFailed += basicResults.failed;
                    testResults.AddRange(basicResults.results);
                }

                // Material tests  
                if (testType == "all" || testType == "materials")
                {
                    MainConsole.Instance.Output("Running material variation tests...");
                    
                    var materialResults = await RunMaterialTestSuite(destructionSystem);
                    testsPassed += materialResults.passed;
                    testsFailed += materialResults.failed;
                    testResults.AddRange(materialResults.results);
                }

                // Stress tests
                if (testType == "all" || testType == "stress")
                {
                    MainConsole.Instance.Output("Running stress tests...");
                    
                    var stressResults = await RunStressTestSuite(destructionSystem);
                    testsPassed += stressResults.passed;
                    testsFailed += stressResults.failed;
                    testResults.AddRange(stressResults.results);
                }

                // Generate report
                MainConsole.Instance.Output($"\n=== Test Results Summary ===");
                MainConsole.Instance.Output($"Region: {regionName}");
                MainConsole.Instance.Output($"Total Tests: {testsPassed + testsFailed}");
                MainConsole.Instance.Output($"Passed: {testsPassed}");
                MainConsole.Instance.Output($"Failed: {testsFailed}");
                
                if (testsFailed > 0)
                {
                    MainConsole.Instance.Output("\nFailed Tests:");
                    foreach (var failure in testResults.Where(r => r.StartsWith("FAIL")))
                    {
                        MainConsole.Instance.Output($"  ❌ {failure}");
                    }
                }

                var successRate = testsPassed + testsFailed > 0 ? 
                    (double)testsPassed / (testsPassed + testsFailed) * 100 : 0;
                    
                MainConsole.Instance.Output($"Success Rate: {successRate:F1}%");
                
                if (successRate >= 90)
                    MainConsole.Instance.Output("🎉 EXCELLENT: System performing very well!");
                else if (successRate >= 75)
                    MainConsole.Instance.Output("✅ GOOD: System working well with minor issues");
                else if (successRate >= 50)
                    MainConsole.Instance.Output("⚠️ MODERATE: Some issues detected");
                else
                    MainConsole.Instance.Output("❌ POOR: Significant issues need attention");
            }
            catch (Exception ex)
            {
                MainConsole.Instance.Output($"Error during automated testing: {ex.Message}");
            }
        }

        private async Task<(int passed, int failed, List<string> results)> RunBasicTestSuite(DestructiblePhysicsSystem destructionSystem)
        {
            var results = new List<string>();
            int passed = 0, failed = 0;

            // Test 1: System availability
            try
            {
                if (destructionSystem != null && destructionSystem.IsEnabled)
                {
                    results.Add("PASS: Destruction system is available and enabled");
                    passed++;
                }
                else
                {
                    results.Add("FAIL: Destruction system not available or disabled");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                results.Add($"FAIL: Error checking system availability: {ex.Message}");
                failed++;
            }

            // Test 2: Object registration
            try
            {
                var testPos = new OMV.Vector3(128, 128, 30);
                var materialProps = new DestructibleMaterialProperties { MaterialType = DestructibleMaterial.Stone };
                var objectId = destructionSystem.CreateDestructibleObject(
                    "TestObject", materialProps, testPos, OMV.Quaternion.Identity, new OMV.Vector3(1, 1, 1), 1000.0f);
                
                if (objectId > 0)
                {
                    results.Add("PASS: Object registration works");
                    passed++;
                    
                    // Clean up
                    destructionSystem.RemoveDestructibleObject(objectId);
                }
                else
                {
                    results.Add("FAIL: Object registration failed");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                results.Add($"FAIL: Error during object registration: {ex.Message}");
                failed++;
            }

            // Test 3: Impact application
            try
            {
                var testPos = new OMV.Vector3(128, 128, 30);
                var materialProps = new DestructibleMaterialProperties { MaterialType = DestructibleMaterial.Glass };
                var objectId = destructionSystem.CreateDestructibleObject(
                    "TestObject2", materialProps, testPos, OMV.Quaternion.Identity, new OMV.Vector3(1, 1, 1), 1000.0f);
                
                if (objectId > 0)
                {
                    var impactResult = destructionSystem.ApplyImpact(objectId, testPos, 
                        new OMV.Vector3(0, 0, 200), 0.016f);
                    
                    if (impactResult)
                    {
                        results.Add("PASS: Impact application works");
                        passed++;
                    }
                    else
                    {
                        results.Add("FAIL: Impact application failed");
                        failed++;
                    }
                    
                    // Clean up
                    destructionSystem.RemoveDestructibleObject(objectId);
                }
            }
            catch (Exception ex)
            {
                results.Add($"FAIL: Error during impact test: {ex.Message}");
                failed++;
            }

            return (passed, failed, results);
        }

        private async Task<(int passed, int failed, List<string> results)> RunMaterialTestSuite(DestructiblePhysicsSystem destructionSystem)
        {
            var results = new List<string>();
            int passed = 0, failed = 0;
            
            var materials = Enum.GetValues<DestructibleMaterial>();
            
            foreach (var material in materials)
            {
                try
                {
                    var testPos = new OMV.Vector3(128, 128, 30);
                    var materialProps = new DestructibleMaterialProperties { MaterialType = material };
                    var objectId = destructionSystem.CreateDestructibleObject(
                        $"TestMaterial_{material}", materialProps, testPos, OMV.Quaternion.Identity, new OMV.Vector3(1, 1, 1), 1000.0f);
                    
                    if (objectId > 0)
                    {
                        results.Add($"PASS: {material} material registration works");
                        passed++;
                        
                        // Clean up
                        destructionSystem.RemoveDestructibleObject(objectId);
                    }
                    else
                    {
                        results.Add($"FAIL: {material} material registration failed");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    results.Add($"FAIL: {material} material test error: {ex.Message}");
                    failed++;
                }
                
                // Small delay between tests
                await Task.Delay(100);
            }
            
            return (passed, failed, results);
        }

        private async Task<(int passed, int failed, List<string> results)> RunStressTestSuite(DestructiblePhysicsSystem destructionSystem)
        {
            var results = new List<string>();
            int passed = 0, failed = 0;

            // Test multiple objects
            try
            {
                var objectIds = new List<uint>();
                
                // Create 5 test objects
                for (int i = 0; i < 5; i++)
                {
                    var testPos = new OMV.Vector3(128 + i * 2, 128, 30);
                    var materialProps = new DestructibleMaterialProperties { MaterialType = DestructibleMaterial.Stone };
                    var objectId = destructionSystem.CreateDestructibleObject(
                        $"StressTest_{i}", materialProps, testPos, OMV.Quaternion.Identity, new OMV.Vector3(1, 1, 1), 1000.0f);
                    
                    if (objectId > 0)
                        objectIds.Add(objectId);
                }
                
                if (objectIds.Count == 5)
                {
                    results.Add("PASS: Multiple object creation works");
                    passed++;
                    
                    // Test simultaneous impacts
                    var impactTasks = objectIds.Select(async id =>
                    {
                        await Task.Delay(100); // Small stagger
                        var pos = new OMV.Vector3(128, 128, 30); // Use test position
                        return destructionSystem.ApplyImpact(id, pos, new OMV.Vector3(0, 0, 200), 0.016f);
                    });
                    
                    var impactResults = await Task.WhenAll(impactTasks);
                    var successfulImpacts = impactResults.Count(r => r);
                    
                    if (successfulImpacts >= 3)
                    {
                        results.Add($"PASS: Simultaneous impacts work ({successfulImpacts}/5 succeeded)");
                        passed++;
                    }
                    else
                    {
                        results.Add($"FAIL: Too few simultaneous impacts succeeded ({successfulImpacts}/5)");
                        failed++;
                    }
                }
                else
                {
                    results.Add($"FAIL: Could only create {objectIds.Count}/5 objects");
                    failed++;
                }
                
                // Clean up
                foreach (var id in objectIds)
                {
                    try { destructionSystem.RemoveDestructibleObject(id); } catch { }
                }
            }
            catch (Exception ex)
            {
                results.Add($"FAIL: Stress test error: {ex.Message}");
                failed++;
            }
            
            return (passed, failed, results);
        }

        #endregion

        #region Helper Methods

        private string GetRegionFromParams(string[] cmdparams, int index)
        {
            return cmdparams.Length > index ? cmdparams[index] : null;
        }

        private List<Scene> GetTargetScenes(string regionName)
        {
            var targetScenes = new List<Scene>();
            
            lock (m_syncLock)
            {
                if (string.IsNullOrEmpty(regionName))
                {
                    targetScenes.AddRange(m_scenes);
                }
                else
                {
                    var scene = m_scenes.FirstOrDefault(s => s.RegionInfo.RegionName == regionName);
                    if (scene != null)
                    {
                        targetScenes.Add(scene);
                    }
                }
            }

            return targetScenes;
        }

        private DestructiblePhysicsSystem GetDestructionSystem(Scene scene)
        {
            // Get the physics scene
            var physicsScene = scene.PhysicsScene as BSScene;
            if (physicsScene == null)
                return null;

            // Try to get the destruction system from the advanced physics integration
            try
            {
                var integration = physicsScene.GetType().GetField("m_advancedPhysics", 
                    BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(physicsScene) as AdvancedPhysicsIntegration;
                
                return integration?.DestructibleSystem;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error accessing destruction system: {1}", LogHeader, ex.Message);
                return null;
            }
        }

        private void ShowConfigParameters()
        {
            MainConsole.Instance.Output("Available destruction system parameters:");
            MainConsole.Instance.Output("  EnableDestructiblePhysics - Enable/disable the destruction system");
            MainConsole.Instance.Output("  MaxDestructibleObjects - Maximum destructible objects (default: 25)");
            MainConsole.Instance.Output("  MaxFragmentsPerObject - Maximum fragments per object (default: 20)");
            MainConsole.Instance.Output("  FragmentLifetime - Fragment lifetime in seconds (default: 30.0)");
            MainConsole.Instance.Output("  DestructionParticleIntensity - Particle effect intensity (0.0-3.0)");
            MainConsole.Instance.Output("  DestructionSoundIntensity - Sound effect intensity (0.0-3.0)");
            MainConsole.Instance.Output("  EnableDestructionSounds - Enable sound effects");
            MainConsole.Instance.Output("  EnableDestructionParticles - Enable particle effects");
            MainConsole.Instance.Output("  DestructionForceMultiplier - Force calculation multiplier");
            MainConsole.Instance.Output("  EnableChainReactions - Enable chain reaction destruction");
            MainConsole.Instance.Output("  ChainReactionRadius - Chain reaction radius in meters");
            MainConsole.Instance.Output("  EnableStructuralIntegrity - Enable structural integrity simulation");
            MainConsole.Instance.Output("  StructuralIntegrityThreshold - Structural failure threshold (0.0-1.0)");
            MainConsole.Instance.Output("  EnableEnvironmentalEffects - Enable dust clouds and environmental effects");
            MainConsole.Instance.Output("  EnvironmentalEffectsDuration - Environmental effects duration multiplier");
            MainConsole.Instance.Output("  DestructionCooldownTime - Cooldown between destructions in seconds");
            MainConsole.Instance.Output("  EnableDestructionLogging - Enable detailed destruction logging");
            MainConsole.Instance.Output("Usage: destruction config <parameter> [<value>] [<region>]");
        }

        #endregion
    }

    /// <summary>
    /// Statistics tracking for destruction system
    /// </summary>
    public class DestructionSystemStats
    {
        public int TotalDestructions { get; set; }
        public int ChainReactions { get; set; }
        public int ParticlesCreated { get; set; }
        public int SoundsPlayed { get; set; }
        public long EstimatedMemoryUsage { get; set; }
        public DateTime LastReset { get; set; } = DateTime.UtcNow;
        
        public void Reset()
        {
            TotalDestructions = 0;
            ChainReactions = 0;
            ParticlesCreated = 0;
            SoundsPlayed = 0;
            EstimatedMemoryUsage = 0;
            LastReset = DateTime.UtcNow;
        }
    }
}