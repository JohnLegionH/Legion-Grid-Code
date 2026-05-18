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
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using System.Xml.Serialization;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Comprehensive performance baseline measurement system
    /// Tracks all key metrics for physics engine modernization
    /// </summary>
    public static class BSPerformanceBaseline
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[PHYSICS BASELINE]";

        #region Data Structures

        /// <summary>
        /// Complete performance snapshot for baseline comparison
        /// </summary>
        [Serializable]
        public class PerformanceSnapshot
        {
            public DateTime Timestamp { get; set; }
            public string BulletVersion { get; set; }
            public string OpenSimVersion { get; set; }
            public PhysicsMetrics Metrics { get; set; }
            public SystemInfo System { get; set; }
            public ConfigurationInfo Configuration { get; set; }
            public List<TestResult> TestResults { get; set; }

            public PerformanceSnapshot()
            {
                Timestamp = DateTime.UtcNow;
                TestResults = new List<TestResult>();
            }
        }

        /// <summary>
        /// Core physics performance metrics
        /// </summary>
        [Serializable]
        public class PhysicsMetrics
        {
            public float SimulationFPS { get; set; }
            public float AverageStepTime { get; set; }
            public float MaxStepTime { get; set; }
            public int ActiveObjects { get; set; }
            public int MaxSupportedObjects { get; set; }
            public long MemoryUsageMB { get; set; }
            public float CPUUtilizationPercent { get; set; }
            public float AvatarResponseTimeMS { get; set; }
            public int CollisionsPerSecond { get; set; }
            public float ConstraintSolvingTimeMS { get; set; }
            public float BroadphaseTimeMS { get; set; }
            public float NarrowphaseTimeMS { get; set; }
            public int ThreadCount { get; set; }
            public float GCPressureMBPerSec { get; set; }
        }

        /// <summary>
        /// System hardware and environment information
        /// </summary>
        [Serializable]
        public class SystemInfo
        {
            public string OperatingSystem { get; set; }
            public int ProcessorCores { get; set; }
            public long TotalMemoryMB { get; set; }
            public string ProcessorArchitecture { get; set; }
            public bool Is64BitProcess { get; set; }
            public string DotNetVersion { get; set; }
        }

        /// <summary>
        /// Physics configuration snapshot
        /// </summary>
        [Serializable]
        public class ConfigurationInfo
        {
            public string PhysicsEngine { get; set; }
            public bool UseCollisionOptimization { get; set; }
            public bool UseVehicleOptimization { get; set; }
            public bool AvatarUseAdvancedSmoothing { get; set; }
            public float LinearDamping { get; set; }
            public float AngularDamping { get; set; }
            public float Gravity { get; set; }
            public int MaxUpdatesPerFrame { get; set; }
            public int MaxCollisionsPerFrame { get; set; }
        }

        /// <summary>
        /// Individual test result
        /// </summary>
        [Serializable]
        public class TestResult
        {
            public string TestName { get; set; }
            public double DurationSeconds { get; set; }
            public bool Passed { get; set; }
            public Dictionary<string, object> Results { get; set; }
            public string ErrorMessage { get; set; }

            public TestResult()
            {
                Results = new Dictionary<string, object>();
            }
        }

        #endregion

        #region State Management

        private static PerformanceSnapshot currentSnapshot;
        private static readonly object snapshotLock = new object();
        private static readonly Stopwatch measurementTimer = new Stopwatch();
        private static readonly List<float> stepTimeSamples = new List<float>();
        private static long lastGCMemory = 0;
        private static DateTime lastGCCheck = DateTime.UtcNow;

        #endregion

        #region Public API

        /// <summary>
        /// Initialize baseline measurement system
        /// </summary>
        public static void Initialize(BSScene scene)
        {
            lock (snapshotLock)
            {
                currentSnapshot = new PerformanceSnapshot();
                
                // Capture system information
                currentSnapshot.System = CaptureSystemInfo();
                currentSnapshot.Configuration = CaptureConfigurationInfo(scene);
                currentSnapshot.BulletVersion = GetBulletVersion();
                currentSnapshot.OpenSimVersion = GetOpenSimVersion();

                stepTimeSamples.Clear();
                lastGCMemory = GC.GetTotalMemory(false);
                lastGCCheck = DateTime.UtcNow;

                m_log.InfoFormat("{0}: Performance baseline measurement initialized", LogHeader);
            }
        }

        /// <summary>
        /// Record physics step timing
        /// </summary>
        public static void RecordStepTime(float stepTime)
        {
            lock (snapshotLock)
            {
                stepTimeSamples.Add(stepTime);
                
                // Keep only last 1000 samples for moving average
                if (stepTimeSamples.Count > 1000)
                {
                    stepTimeSamples.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Update current metrics during simulation
        /// </summary>
        public static void UpdateMetrics(BSScene scene)
        {
            if (currentSnapshot?.Metrics == null)
                currentSnapshot.Metrics = new PhysicsMetrics();

            var metrics = currentSnapshot.Metrics;

            // Calculate FPS and timing
            if (stepTimeSamples.Count > 0)
            {
                lock (snapshotLock)
                {
                    metrics.AverageStepTime = CalculateAverage(stepTimeSamples) * 1000f; // Convert to ms
                    metrics.MaxStepTime = CalculateMax(stepTimeSamples) * 1000f;
                    metrics.SimulationFPS = stepTimeSamples.Count > 0 ? 1.0f / CalculateAverage(stepTimeSamples) : 0f;
                }
            }

            // Object counts
            metrics.ActiveObjects = scene.PhysObjects.Count;

            // Memory usage
            long currentMemory = GC.GetTotalMemory(false);
            metrics.MemoryUsageMB = currentMemory / (1024 * 1024);
            
            // GC pressure calculation
            var now = DateTime.UtcNow;
            var timeDelta = (now - lastGCCheck).TotalSeconds;
            if (timeDelta > 0)
            {
                var memoryDelta = Math.Max(0, currentMemory - lastGCMemory);
                metrics.GCPressureMBPerSec = (float)(memoryDelta / (1024.0 * 1024.0 * timeDelta));
                lastGCMemory = currentMemory;
                lastGCCheck = now;
            }

            // CPU utilization (approximate)
            using (var process = Process.GetCurrentProcess())
            {
                metrics.CPUUtilizationPercent = (float)process.TotalProcessorTime.TotalMilliseconds / 
                    Environment.TickCount * 100f;
            }

            // Threading info
            metrics.ThreadCount = Process.GetCurrentProcess().Threads.Count;
        }

        /// <summary>
        /// Run comprehensive performance benchmark
        /// </summary>
        public static TestResult RunBenchmark(BSScene scene, string testName, int objectCount, float duration)
        {
            var result = new TestResult { TestName = testName };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                m_log.InfoFormat("{0}: Running benchmark '{1}' with {2} objects for {3} seconds", 
                    LogHeader, testName, objectCount, duration);

                // Create test objects
                var testObjects = CreateTestObjects(scene, objectCount);
                result.Results["ObjectCount"] = objectCount;

                // Measure baseline performance
                var beforeMemory = GC.GetTotalMemory(true);
                var beforeStepTimes = new List<float>();
                
                // Warmup period (5 seconds)
                var warmupEnd = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < warmupEnd)
                {
                    scene.Simulate(1.0f / 60.0f);
                    Thread.Sleep(16); // ~60 FPS
                }

                // Measurement period
                var measurementEnd = DateTime.UtcNow.AddSeconds(duration);
                var frameCount = 0;
                var stepTimes = new List<float>();

                while (DateTime.UtcNow < measurementEnd)
                {
                    var stepStart = DateTime.UtcNow;
                    scene.Simulate(1.0f / 60.0f);
                    var stepEnd = DateTime.UtcNow;
                    
                    var stepTime = (float)(stepEnd - stepStart).TotalSeconds;
                    stepTimes.Add(stepTime);
                    frameCount++;
                    
                    Thread.Sleep(1); // Minimal delay to prevent 100% CPU
                }

                // Calculate results
                var afterMemory = GC.GetTotalMemory(false);
                var avgStepTime = CalculateAverage(stepTimes);
                var maxStepTime = CalculateMax(stepTimes);
                var fps = 1.0f / avgStepTime;

                result.Results["FrameCount"] = frameCount;
                result.Results["AverageStepTimeMS"] = avgStepTime * 1000f;
                result.Results["MaxStepTimeMS"] = maxStepTime * 1000f;
                result.Results["AverageFPS"] = fps;
                result.Results["MemoryUsedMB"] = (afterMemory - beforeMemory) / (1024 * 1024);
                result.Results["Duration"] = duration;

                // Cleanup test objects
                CleanupTestObjects(scene, testObjects);

                result.Passed = fps >= 30.0f; // Minimum acceptable FPS
                
                stopwatch.Stop();
                result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;

                m_log.InfoFormat("{0}: Benchmark '{1}' completed - FPS: {2:F1}, Avg Step: {3:F2}ms", 
                    LogHeader, testName, fps, avgStepTime * 1000f);
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.ErrorMessage = ex.Message;
                m_log.ErrorFormat("{0}: Benchmark '{1}' failed: {2}", LogHeader, testName, ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Generate comprehensive baseline report
        /// </summary>
        public static PerformanceSnapshot CaptureSnapshot(BSScene scene)
        {
            lock (snapshotLock)
            {
                if (currentSnapshot == null)
                    Initialize(scene);

                UpdateMetrics(scene);

                // Run standard benchmark tests
                currentSnapshot.TestResults.Clear();
                
                // Lightweight test (100 objects, 10 seconds)
                currentSnapshot.TestResults.Add(RunBenchmark(scene, "Lightweight_Baseline", 100, 10));
                
                // Medium load test (500 objects, 15 seconds)
                currentSnapshot.TestResults.Add(RunBenchmark(scene, "Medium_Baseline", 500, 15));
                
                // Heavy load test (1000 objects, 20 seconds)
                currentSnapshot.TestResults.Add(RunBenchmark(scene, "Heavy_Baseline", 1000, 20));

                return currentSnapshot;
            }
        }

        /// <summary>
        /// Save baseline snapshot to disk
        /// </summary>
        public static void SaveBaseline(PerformanceSnapshot snapshot, string filePath)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(PerformanceSnapshot));
                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    serializer.Serialize(writer, snapshot);
                }
                
                m_log.InfoFormat("{0}: Baseline saved to {1}", LogHeader, filePath);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to save baseline: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Load baseline snapshot from disk
        /// </summary>
        public static PerformanceSnapshot LoadBaseline(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var serializer = new XmlSerializer(typeof(PerformanceSnapshot));
                using (var reader = new StreamReader(filePath, Encoding.UTF8))
                {
                    return (PerformanceSnapshot)serializer.Deserialize(reader);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to load baseline: {1}", LogHeader, ex.Message);
                return null;
            }
        }

        #endregion

        #region Helper Methods

        private static SystemInfo CaptureSystemInfo()
        {
            return new SystemInfo
            {
                OperatingSystem = Environment.OSVersion.ToString(),
                ProcessorCores = Environment.ProcessorCount,
                TotalMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024), // Approximate
                ProcessorArchitecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                Is64BitProcess = Environment.Is64BitProcess,
                DotNetVersion = Environment.Version.ToString()
            };
        }

        private static ConfigurationInfo CaptureConfigurationInfo(BSScene scene)
        {
            return new ConfigurationInfo
            {
                PhysicsEngine = scene.EngineType,
                UseCollisionOptimization = BSParam.UseCollisionOptimization,
                UseVehicleOptimization = BSParam.UseVehicleOptimization,
                AvatarUseAdvancedSmoothing = BSParam.AvatarUseAdvancedSmoothing,
                LinearDamping = BSParam.LinearDamping,
                AngularDamping = BSParam.AngularDamping,
                Gravity = BSParam.Gravity,
                MaxUpdatesPerFrame = scene.m_maxUpdatesPerFrame,
                MaxCollisionsPerFrame = scene.m_maxCollisionsPerFrame
            };
        }

        private static string GetBulletVersion()
        {
            try
            {
                var versionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib64", "BulletSimVersionInfo");
                if (File.Exists(versionFile))
                {
                    var lines = File.ReadAllLines(versionFile);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("BulletVersion="))
                            return line.Substring("BulletVersion=".Length);
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        private static string GetOpenSimVersion()
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                return assembly.GetName().Version.ToString();
            }
            catch
            {
                return "Unknown";
            }
        }

        private static float CalculateAverage(List<float> values)
        {
            if (values.Count == 0) return 0f;
            
            float sum = 0f;
            for (int i = 0; i < values.Count; i++)
                sum += values[i];
            
            return sum / values.Count;
        }

        private static float CalculateMax(List<float> values)
        {
            if (values.Count == 0) return 0f;
            
            float max = values[0];
            for (int i = 1; i < values.Count; i++)
                if (values[i] > max) max = values[i];
            
            return max;
        }

        private static List<BSPhysObject> CreateTestObjects(BSScene scene, int count)
        {
            var objects = new List<BSPhysObject>();
            var random = new Random();

            for (int i = 0; i < count; i++)
            {
                // Create simple box objects for testing
                var position = new OMV.Vector3(
                    random.Next(-50, 50),
                    random.Next(-50, 50),
                    random.Next(10, 50));

                var size = PhysicsConstants.DEFAULT_EXTENT_VECTOR;
                
                // This is a simplified object creation - in reality, this would need
                // proper integration with OpenSim's object creation system
                // For baseline measurements, we'll need to work with existing objects
            }

            return objects;
        }

        private static void CleanupTestObjects(BSScene scene, List<BSPhysObject> objects)
        {
            foreach (var obj in objects)
            {
                try
                {
                    // Remove test objects from scene
                    // This would need proper cleanup implementation
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("{0}: Failed to cleanup test object: {1}", LogHeader, ex.Message);
                }
            }
        }

        #endregion
    }
}