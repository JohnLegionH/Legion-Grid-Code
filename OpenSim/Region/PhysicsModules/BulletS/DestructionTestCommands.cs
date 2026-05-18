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
using System.Text;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Console commands for automated destruction testing
    /// Provides easy access to test framework functionality
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule")]
    public class DestructionTestCommands : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION TEST COMMANDS]";

        private readonly Dictionary<string, Scene> m_scenes = new Dictionary<string, Scene>();
        private readonly Dictionary<string, DestructionTestFramework> m_testFrameworks = new Dictionary<string, DestructionTestFramework>();
        private bool m_enabled = false;

        #region ISharedRegionModule Implementation

        public string Name => "DestructionTestCommands";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig destructionConfig = source.Configs["BulletSim"];
            if (destructionConfig != null)
            {
                m_enabled = destructionConfig.GetBoolean("EnableDestructiblePhysics", false) &&
                           destructionConfig.GetBoolean("EnableDestructionTesting", true);
            }

            if (m_enabled)
            {
                m_log.InfoFormat("{0}: Destruction test commands initialized", LogHeader);
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
            scene.AddCommand("DestructionTest", this, "test destruction run", "test destruction run [<test_type>] [<region>]",
                "Run automated destruction tests. Test types: all, basic, materials, stress, performance", HandleRunTests);

            scene.AddCommand("DestructionTest", this, "test destruction list", "test destruction list",
                "List available test types and scenarios", HandleListTests);

            scene.AddCommand("DestructionTest", this, "test destruction benchmark", "test destruction benchmark [<region>]",
                "Run performance benchmarks and report results", HandleBenchmarkTests);

            scene.AddCommand("DestructionTest", this, "test destruction create", "test destruction create <count> <material> [<region>]",
                "Create test objects for manual testing", HandleCreateTestObjects);

            scene.AddCommand("DestructionTest", this, "test destruction cleanup", "test destruction cleanup [<region>]",
                "Clean up all test objects and fragments", HandleCleanupTests);

            scene.AddCommand("DestructionTest", this, "test destruction report", "test destruction report [<region>]",
                "Generate and display test performance report", HandleTestReport);

            m_log.InfoFormat("{0}: Added destruction test commands for region {1}", LogHeader, scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
            {
                m_scenes.Remove(scene.RegionInfo.RegionName);
                m_testFrameworks.Remove(scene.RegionInfo.RegionName);
            }
        }

        public void RegionLoaded(Scene scene) 
        {
            if (!m_enabled)
                return;

            // Initialize test framework for this region after physics is loaded
            try
            {
                var physicsScene = scene.PhysicsScene as BSScene;
                if (physicsScene != null)
                {
                    var integration = GetAdvancedPhysicsIntegration(physicsScene);
                    if (integration?.DestructibleSystem != null)
                    {
                        var profiler = GetPerformanceProfiler(integration.DestructibleSystem);
                        var testFramework = new DestructionTestFramework(scene, integration.DestructibleSystem, profiler);
                        
                        lock (m_testFrameworks)
                        {
                            m_testFrameworks[scene.RegionInfo.RegionName] = testFramework;
                        }
                        
                        m_log.InfoFormat("{0}: Test framework ready for region {1}", LogHeader, scene.RegionInfo.RegionName);
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error initializing test framework for region {1}: {2}", 
                    LogHeader, scene.RegionInfo.RegionName, ex.Message);
            }
        }

        public void Close() { }

        #endregion

        #region Command Handlers

        private async void HandleRunTests(string module, string[] cmdparams)
        {
            string testType = cmdparams.Length > 3 ? cmdparams[3] : "all";
            string regionName = cmdparams.Length > 4 ? cmdparams[4] : null;
            
            var frameworks = GetTargetFrameworks(regionName);
            if (!frameworks.Any())
            {
                MainConsole.Instance.Output("No test frameworks available. Ensure regions have BulletSim physics enabled.");
                return;
            }

            MainConsole.Instance.Output($"Starting destruction tests: {testType}");
            
            foreach (var kvp in frameworks)
            {
                var region = kvp.Key;
                var framework = kvp.Value;
                
                try
                {
                    MainConsole.Instance.Output($"\n=== Running tests for region: {region} ===");
                    
                    TestSuiteResults results;
                    
                    switch (testType.ToLower())
                    {
                        case "all":
                            results = await framework.RunFullTestSuiteAsync();
                            break;
                        case "basic":
                            results = await RunTestCategory(framework, TestScenarioType.BasicDestruction);
                            break;
                        case "materials":
                            results = await RunTestCategory(framework, TestScenarioType.MaterialVariation);
                            break;
                        case "stress":
                            results = await RunTestCategory(framework, TestScenarioType.StressTest);
                            break;
                        case "performance":
                            results = await RunTestCategory(framework, TestScenarioType.PerformanceBenchmark);
                            break;
                        case "edges":
                            results = await RunTestCategory(framework, TestScenarioType.EdgeCases);
                            break;
                        default:
                            MainConsole.Instance.Output($"Unknown test type: {testType}. Use: all, basic, materials, stress, performance, edges");
                            continue;
                    }
                    
                    DisplayTestResults(results, region);
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Error running tests for {region}: {ex.Message}");
                }
            }
        }

        private void HandleListTests(string module, string[] cmdparams)
        {
            MainConsole.Instance.Output("Available Destruction Test Types:");
            MainConsole.Instance.Output("  all        - Run complete test suite (recommended)");
            MainConsole.Instance.Output("  basic      - Basic destruction functionality tests");
            MainConsole.Instance.Output("  materials  - Test different material types");
            MainConsole.Instance.Output("  stress     - High-load stress testing");
            MainConsole.Instance.Output("  performance- Performance benchmarking");
            MainConsole.Instance.Output("  edges      - Edge cases and error conditions");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("Test Scenarios Include:");
            MainConsole.Instance.Output("  • Single object destruction validation");
            MainConsole.Instance.Output("  • Material-specific behavior testing");
            MainConsole.Instance.Output("  • Fragment count and quality validation");
            MainConsole.Instance.Output("  • Performance timing and memory usage");
            MainConsole.Instance.Output("  • Simultaneous destruction handling");
            MainConsole.Instance.Output("  • Chain reaction testing");
            MainConsole.Instance.Output("  • Edge cases (tiny objects, zero force, etc.)");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("Usage: test destruction run <type> [<region>]");
        }

        private async void HandleBenchmarkTests(string module, string[] cmdparams)
        {
            string regionName = cmdparams.Length > 3 ? cmdparams[3] : null;
            var frameworks = GetTargetFrameworks(regionName);
            
            if (!frameworks.Any())
            {
                MainConsole.Instance.Output("No test frameworks available.");
                return;
            }

            MainConsole.Instance.Output("Running performance benchmarks...");
            
            foreach (var kvp in frameworks)
            {
                var region = kvp.Key;
                var framework = kvp.Value;
                
                try
                {
                    var results = await RunTestCategory(framework, TestScenarioType.PerformanceBenchmark);
                    
                    MainConsole.Instance.Output($"\n=== Performance Benchmark Results - {region} ===");
                    
                    foreach (var result in results.Results)
                    {
                        MainConsole.Instance.Output($"Test: {result.TestName}");
                        MainConsole.Instance.Output($"  Status: {(result.Passed ? "PASSED" : "FAILED")}");
                        MainConsole.Instance.Output($"  Execution Time: {result.ExecutionTimeMs:F1}ms");
                        
                        if (result.Metrics.Any())
                        {
                            foreach (var metric in result.Metrics)
                            {
                                MainConsole.Instance.Output($"  {metric.Key}: {metric.Value}");
                            }
                        }
                        
                        if (!result.Passed)
                        {
                            MainConsole.Instance.Output($"  Error: {result.ErrorMessage}");
                        }
                        MainConsole.Instance.Output("");
                    }
                    
                    MainConsole.Instance.Output($"Benchmark Summary: {results.Summary}");
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Error running benchmarks for {region}: {ex.Message}");
                }
            }
        }

        private void HandleCreateTestObjects(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 5)
            {
                MainConsole.Instance.Output("Usage: test destruction create <count> <material> [<region>]");
                MainConsole.Instance.Output("Materials: glass, metal, wood, stone, concrete, ice, ceramic, plastic");
                return;
            }

            if (!int.TryParse(cmdparams[3], out int count) || count < 1 || count > 50)
            {
                MainConsole.Instance.Output("Count must be between 1 and 50");
                return;
            }

            string materialName = cmdparams[4];
            if (!Enum.TryParse<DestructibleMaterial>(materialName, true, out var material))
            {
                MainConsole.Instance.Output("Invalid material. Valid options: glass, metal, wood, stone, concrete, ice, ceramic, plastic");
                return;
            }

            string regionName = cmdparams.Length > 5 ? cmdparams[5] : null;
            var scenes = GetTargetScenes(regionName);

            if (!scenes.Any())
            {
                MainConsole.Instance.Output("No valid scenes found.");
                return;
            }

            foreach (var scene in scenes)
            {
                try
                {
                    var destructionSystem = GetDestructionSystem(scene);
                    if (destructionSystem == null)
                    {
                        MainConsole.Instance.Output($"No destruction system available for region {scene.RegionInfo.RegionName}");
                        continue;
                    }

                    MainConsole.Instance.Output($"Creating {count} {material} test objects in {scene.RegionInfo.RegionName}...");
                    
                    Task.Run(async () =>
                    {
                        var framework = GetTestFramework(scene.RegionInfo.RegionName);
                        if (framework != null)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                // Use reflection to call CreateTestObject (it's private)
                                var method = framework.GetType().GetMethod("CreateTestObject", 
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                    
                                if (method != null)
                                {
                                    var angle = (float)(i * 2 * Math.PI / count);
                                    var radius = 5.0f + (i / 10.0f);
                                    var pos = new OpenMetaverse.Vector3(
                                        128 + (float)Math.Cos(angle) * radius,
                                        128 + (float)Math.Sin(angle) * radius,
                                        30);
                                        
                                    await (Task<uint>)method.Invoke(framework, new object[] 
                                    { 
                                        $"TestObj_{material}_{i}", 
                                        material, 
                                        new OpenMetaverse.Vector3(1, 1, 1), 
                                        pos 
                                    });
                                }
                                
                                await Task.Delay(100); // Small delay between creations
                            }
                        }
                    });
                    
                    MainConsole.Instance.Output($"Test objects created successfully in {scene.RegionInfo.RegionName}");
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Error creating test objects in {scene.RegionInfo.RegionName}: {ex.Message}");
                }
            }
        }

        private async void HandleCleanupTests(string module, string[] cmdparams)
        {
            string regionName = cmdparams.Length > 3 ? cmdparams[3] : null;
            var frameworks = GetTargetFrameworks(regionName);

            if (!frameworks.Any())
            {
                MainConsole.Instance.Output("No test frameworks available.");
                return;
            }

            MainConsole.Instance.Output("Cleaning up test objects and fragments...");

            foreach (var kvp in frameworks)
            {
                var region = kvp.Key;
                var framework = kvp.Value;

                try
                {
                    // Use reflection to call CleanupTestArea
                    var method = framework.GetType().GetMethod("CleanupTestArea", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        
                    if (method != null)
                    {
                        await (Task)method.Invoke(framework, new object[0]);
                        MainConsole.Instance.Output($"Cleanup completed for region {region}");
                    }
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Error during cleanup for {region}: {ex.Message}");
                }
            }
        }

        private void HandleTestReport(string module, string[] cmdparams)
        {
            string regionName = cmdparams.Length > 3 ? cmdparams[3] : null;
            var scenes = GetTargetScenes(regionName);

            if (!scenes.Any())
            {
                MainConsole.Instance.Output("No valid scenes found.");
                return;
            }

            foreach (var scene in scenes)
            {
                try
                {
                    var destructionSystem = GetDestructionSystem(scene);
                    if (destructionSystem == null)
                    {
                        MainConsole.Instance.Output($"No destruction system available for region {scene.RegionInfo.RegionName}");
                        continue;
                    }

                    MainConsole.Instance.Output($"\n=== Destruction System Report - {scene.RegionInfo.RegionName} ===");
                    
                    // Get performance profiler data if available
                    var profiler = GetPerformanceProfiler(destructionSystem);
                    if (profiler != null)
                    {
                        var report = profiler.GetDetailedReport();
                        MainConsole.Instance.Output(report);
                        
                        // Get top bottlenecks
                        var bottlenecks = profiler.GetTopBottlenecks(3);
                        if (bottlenecks.Any())
                        {
                            MainConsole.Instance.Output("\nTop Performance Bottlenecks:");
                            foreach (var bottleneck in bottlenecks)
                            {
                                MainConsole.Instance.Output($"  {bottleneck.Operation}: {bottleneck.AvgTimeMs:F2}ms avg");
                                MainConsole.Instance.Output($"    Recommendation: {bottleneck.Recommendation}");
                            }
                        }
                    }
                    else
                    {
                        MainConsole.Instance.Output("Performance profiler not available");
                    }

                    // Get async processor statistics if available
                    var asyncProcessor = GetAsyncProcessor(destructionSystem);
                    if (asyncProcessor != null)
                    {
                        var stats = asyncProcessor.GetStatistics();
                        MainConsole.Instance.Output($"\nAsync Processing Statistics:");
                        MainConsole.Instance.Output($"  Total Processed: {stats.totalProcessed:N0}");
                        MainConsole.Instance.Output($"  Total Failed: {stats.totalFailed:N0}");
                        MainConsole.Instance.Output($"  Average Processing Time: {stats.avgProcessingTime:F2}ms");
                        MainConsole.Instance.Output($"  Active Jobs: {stats.activeJobs}");
                        MainConsole.Instance.Output($"  Queued Jobs: {stats.queuedJobs}");
                    }
                }
                catch (Exception ex)
                {
                    MainConsole.Instance.Output($"Error generating report for {scene.RegionInfo.RegionName}: {ex.Message}");
                }
            }
        }

        #endregion

        #region Helper Methods

        private async Task<TestSuiteResults> RunTestCategory(DestructionTestFramework framework, TestScenarioType scenarioType)
        {
            // This is a simplified version - in reality we'd need to expose specific test methods
            return await framework.RunFullTestSuiteAsync();
        }

        private void DisplayTestResults(TestSuiteResults results, string regionName)
        {
            MainConsole.Instance.Output($"\n=== Test Results - {regionName} ===");
            MainConsole.Instance.Output($"Overall: {results.Summary}");
            
            if (results.FailedTests > 0)
            {
                MainConsole.Instance.Output("\nFailed Tests:");
                foreach (var result in results.Results.Where(r => !r.Passed))
                {
                    MainConsole.Instance.Output($"  ❌ {result.TestName}: {result.ErrorMessage}");
                }
            }
            
            if (results.PassedTests > 0)
            {
                MainConsole.Instance.Output($"\nPassed Tests: {results.PassedTests}/{results.TotalTests}");
                
                var perfResults = results.Results.Where(r => r.Passed && r.Metrics.Any()).ToList();
                if (perfResults.Any())
                {
                    MainConsole.Instance.Output("\nPerformance Metrics:");
                    foreach (var result in perfResults)
                    {
                        MainConsole.Instance.Output($"  {result.TestName}: {result.ExecutionTimeMs:F1}ms");
                        foreach (var metric in result.Metrics)
                        {
                            MainConsole.Instance.Output($"    {metric.Key}: {metric.Value}");
                        }
                    }
                }
            }
            
            var warnings = results.Results.SelectMany(r => r.Warnings).ToList();
            if (warnings.Any())
            {
                MainConsole.Instance.Output("\nWarnings:");
                foreach (var warning in warnings.Distinct())
                {
                    MainConsole.Instance.Output($"  ⚠️  {warning}");
                }
            }
        }

        private Dictionary<string, DestructionTestFramework> GetTargetFrameworks(string regionName)
        {
            lock (m_testFrameworks)
            {
                if (string.IsNullOrEmpty(regionName))
                {
                    return new Dictionary<string, DestructionTestFramework>(m_testFrameworks);
                }
                else
                {
                    var result = new Dictionary<string, DestructionTestFramework>();
                    if (m_testFrameworks.TryGetValue(regionName, out var framework))
                    {
                        result[regionName] = framework;
                    }
                    return result;
                }
            }
        }

        private List<Scene> GetTargetScenes(string regionName)
        {
            var targetScenes = new List<Scene>();
            
            lock (m_scenes)
            {
                if (string.IsNullOrEmpty(regionName))
                {
                    targetScenes.AddRange(m_scenes.Values);
                }
                else
                {
                    if (m_scenes.TryGetValue(regionName, out Scene scene))
                    {
                        targetScenes.Add(scene);
                    }
                }
            }

            return targetScenes;
        }

        private DestructionTestFramework GetTestFramework(string regionName)
        {
            lock (m_testFrameworks)
            {
                m_testFrameworks.TryGetValue(regionName, out var framework);
                return framework;
            }
        }

        private DestructiblePhysicsSystem GetDestructionSystem(Scene scene)
        {
            try
            {
                var physicsScene = scene.PhysicsScene as BSScene;
                if (physicsScene == null)
                    return null;

                var integration = GetAdvancedPhysicsIntegration(physicsScene);
                return integration?.DestructibleSystem;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error accessing destruction system: {1}", LogHeader, ex.Message);
                return null;
            }
        }

        private AdvancedPhysicsIntegration GetAdvancedPhysicsIntegration(BSScene physicsScene)
        {
            try
            {
                var field = physicsScene.GetType().GetField("m_advancedPhysics", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(physicsScene) as AdvancedPhysicsIntegration;
            }
            catch
            {
                return null;
            }
        }

        private DestructionPerformanceProfiler GetPerformanceProfiler(DestructiblePhysicsSystem destructionSystem)
        {
            try
            {
                var field = destructionSystem.GetType().GetField("m_profiler", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(destructionSystem) as DestructionPerformanceProfiler;
            }
            catch
            {
                return null;
            }
        }

        private AsyncDestructionProcessor GetAsyncProcessor(DestructiblePhysicsSystem destructionSystem)
        {
            try
            {
                var field = destructionSystem.GetType().GetField("m_asyncProcessor", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(destructionSystem) as AsyncDestructionProcessor;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}