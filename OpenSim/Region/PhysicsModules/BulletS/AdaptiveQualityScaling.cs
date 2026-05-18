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
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Physics quality levels with associated performance characteristics
    /// </summary>
    public enum PhysicsQualityLevel
    {
        Ultra = 5,      // Maximum quality, highest performance cost
        High = 4,       // High quality with good performance
        Medium = 3,     // Balanced quality and performance
        Low = 2,        // Reduced quality for better performance
        Minimal = 1     // Minimal quality for maximum performance
    }

    /// <summary>
    /// Physics quality settings for different subsystems
    /// </summary>
    public class PhysicsQualitySettings
    {
        public PhysicsQualityLevel OverallQuality { get; set; } = PhysicsQualityLevel.Medium;
        
        // Collision detection settings
        public int CollisionIterations { get; set; } = 5;
        public float CollisionMargin { get; set; } = 0.04f;
        public bool UseAdvancedCollisionDetection { get; set; } = true;
        public int BroadPhaseAlgorithm { get; set; } = 1; // 0=Simple, 1=SAP, 2=Compound
        
        // Constraint solver settings
        public int ConstraintSolverIterations { get; set; } = 10;
        public float ConstraintTolerance { get; set; } = 1e-6f;
        public bool UseParallelConstraintSolver { get; set; } = true;
        
        // Integration settings
        public float TimeStep { get; set; } = 1.0f / 60.0f; // 60 FPS
        public int SubSteps { get; set; } = 1;
        public bool UseAdaptiveTimeStep { get; set; } = false;
        
        // Spatial indexing settings
        public int SpatialIndexDepth { get; set; } = 8;
        public int ObjectsPerSpatialNode { get; set; } = 15;
        public bool UseDynamicSpatialOptimization { get; set; } = true;
        
        // Memory management settings
        public bool UseObjectPooling { get; set; } = true;
        public int MaxPooledObjects { get; set; } = 1000;
        public bool UseMemoryOptimization { get; set; } = true;
        
        // SIMD and parallel processing
        public bool UseSIMD { get; set; } = true;
        public bool UseParallelProcessing { get; set; } = true;
        public int MaxWorkerThreads { get; set; } = Environment.ProcessorCount;
        
        // Quality-specific overrides
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();

        public PhysicsQualitySettings()
        {
            ApplyQualityLevel(OverallQuality);
        }

        public void ApplyQualityLevel(PhysicsQualityLevel level)
        {
            OverallQuality = level;
            
            switch (level)
            {
                case PhysicsQualityLevel.Ultra:
                    CollisionIterations = 10;
                    CollisionMargin = 0.02f;
                    ConstraintSolverIterations = 20;
                    ConstraintTolerance = 1e-8f;
                    TimeStep = 1.0f / 120.0f; // 120 FPS
                    SubSteps = 2;
                    SpatialIndexDepth = 10;
                    ObjectsPerSpatialNode = 10;
                    MaxWorkerThreads = Environment.ProcessorCount;
                    break;
                    
                case PhysicsQualityLevel.High:
                    CollisionIterations = 7;
                    CollisionMargin = 0.03f;
                    ConstraintSolverIterations = 15;
                    ConstraintTolerance = 1e-7f;
                    TimeStep = 1.0f / 90.0f; // 90 FPS
                    SubSteps = 1;
                    SpatialIndexDepth = 9;
                    ObjectsPerSpatialNode = 12;
                    MaxWorkerThreads = Math.Max(2, Environment.ProcessorCount - 1);
                    break;
                    
                case PhysicsQualityLevel.Medium:
                    CollisionIterations = 5;
                    CollisionMargin = 0.04f;
                    ConstraintSolverIterations = 10;
                    ConstraintTolerance = 1e-6f;
                    TimeStep = 1.0f / 60.0f; // 60 FPS
                    SubSteps = 1;
                    SpatialIndexDepth = 8;
                    ObjectsPerSpatialNode = 15;
                    MaxWorkerThreads = Math.Max(2, Environment.ProcessorCount / 2);
                    break;
                    
                case PhysicsQualityLevel.Low:
                    CollisionIterations = 3;
                    CollisionMargin = 0.06f;
                    ConstraintSolverIterations = 5;
                    ConstraintTolerance = 1e-5f;
                    TimeStep = 1.0f / 45.0f; // 45 FPS
                    SubSteps = 1;
                    SpatialIndexDepth = 6;
                    ObjectsPerSpatialNode = 20;
                    MaxWorkerThreads = Math.Max(1, Environment.ProcessorCount / 4);
                    UseAdvancedCollisionDetection = false;
                    break;
                    
                case PhysicsQualityLevel.Minimal:
                    CollisionIterations = 1;
                    CollisionMargin = 0.08f;
                    ConstraintSolverIterations = 3;
                    ConstraintTolerance = 1e-4f;
                    TimeStep = 1.0f / 30.0f; // 30 FPS
                    SubSteps = 1;
                    SpatialIndexDepth = 4;
                    ObjectsPerSpatialNode = 30;
                    MaxWorkerThreads = 1;
                    UseAdvancedCollisionDetection = false;
                    UseParallelConstraintSolver = false;
                    UseDynamicSpatialOptimization = false;
                    break;
            }
        }

        public PhysicsQualitySettings Clone()
        {
            var clone = new PhysicsQualitySettings();
            clone.CopyFrom(this);
            return clone;
        }

        public void CopyFrom(PhysicsQualitySettings other)
        {
            OverallQuality = other.OverallQuality;
            CollisionIterations = other.CollisionIterations;
            CollisionMargin = other.CollisionMargin;
            UseAdvancedCollisionDetection = other.UseAdvancedCollisionDetection;
            BroadPhaseAlgorithm = other.BroadPhaseAlgorithm;
            ConstraintSolverIterations = other.ConstraintSolverIterations;
            ConstraintTolerance = other.ConstraintTolerance;
            UseParallelConstraintSolver = other.UseParallelConstraintSolver;
            TimeStep = other.TimeStep;
            SubSteps = other.SubSteps;
            UseAdaptiveTimeStep = other.UseAdaptiveTimeStep;
            SpatialIndexDepth = other.SpatialIndexDepth;
            ObjectsPerSpatialNode = other.ObjectsPerSpatialNode;
            UseDynamicSpatialOptimization = other.UseDynamicSpatialOptimization;
            UseObjectPooling = other.UseObjectPooling;
            MaxPooledObjects = other.MaxPooledObjects;
            UseMemoryOptimization = other.UseMemoryOptimization;
            UseSIMD = other.UseSIMD;
            UseParallelProcessing = other.UseParallelProcessing;
            MaxWorkerThreads = other.MaxWorkerThreads;
            CustomSettings = new Dictionary<string, object>(other.CustomSettings);
        }
    }

    /// <summary>
    /// Performance metrics for adaptive quality scaling decisions
    /// </summary>
    public struct PerformanceMetrics
    {
        public float FrameTime { get; set; }
        public float PhysicsTime { get; set; }
        public float CollisionTime { get; set; }
        public float ConstraintTime { get; set; }
        public float MemoryUsageMB { get; set; }
        public float CPUUsagePercent { get; set; }
        public int ActiveObjects { get; set; }
        public int ActiveConstraints { get; set; }
        public int CollisionPairs { get; set; }
        public DateTime Timestamp { get; set; }

        public float PhysicsLoad => PhysicsTime / FrameTime;
        public float MemoryPressure => MemoryUsageMB / (GC.GetTotalMemory(false) / 1024f / 1024f);
    }

    /// <summary>
    /// Adaptive quality scaling decision
    /// </summary>
    public struct QualityScalingDecision
    {
        public PhysicsQualityLevel PreviousQuality { get; set; }
        public PhysicsQualityLevel NewQuality { get; set; }
        public string Reason { get; set; }
        public PerformanceMetrics Metrics { get; set; }
        public DateTime DecisionTime { get; set; }
        public float ConfidenceLevel { get; set; }
    }

    /// <summary>
    /// Adaptive quality scaling system that automatically adjusts physics quality based on performance
    /// </summary>
    public class AdaptiveQualityScaling : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[ADAPTIVE QUALITY SCALING]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly Timer m_adaptationTimer;
        private readonly object m_metricsLock;
        private bool m_disposed;
        private bool m_enabled;

        // Current quality settings
        private PhysicsQualitySettings m_currentSettings;
        private PhysicsQualityLevel m_baselineQuality;
        
        // Performance tracking
        private readonly Queue<PerformanceMetrics> m_performanceHistory;
        private readonly List<QualityScalingDecision> m_decisionHistory;
        private readonly int m_maxHistorySize = 100;
        private readonly int m_maxDecisionHistory = 50;

        // Adaptation configuration
        private readonly float m_targetFrameTime = 1.0f / 60.0f; // Target 60 FPS
        private readonly float m_frameTimeToleranceUp = 0.1f; // 10% tolerance for upgrading quality
        private readonly float m_frameTimeToleranceDown = 0.2f; // 20% tolerance for downgrading quality
        private readonly float m_memoryPressureThreshold = 0.8f; // 80% memory pressure threshold
        private readonly float m_cpuUsageThreshold = 85.0f; // 85% CPU usage threshold
        private readonly TimeSpan m_adaptationInterval = TimeSpan.FromSeconds(2); // Check every 2 seconds
        private readonly TimeSpan m_stabilizationPeriod = TimeSpan.FromSeconds(10); // Wait 10s after changes

        // Learning and stability
        private DateTime m_lastQualityChange;
        private int m_consecutiveUpgrades;
        private int m_consecutiveDowngrades;
        private readonly int m_maxConsecutiveChanges = 3;
        private readonly Dictionary<PhysicsQualityLevel, float> m_qualityPerformanceMapping;

        // Statistics
        private long m_totalAdaptations;
        private long m_qualityUpgrades;
        private long m_qualityDowngrades;
        private DateTime m_lastPerformanceReport;
        private readonly TimeSpan PerformanceReportInterval = TimeSpan.FromMinutes(5);

        #endregion

        #region Constructor

        public AdaptiveQualityScaling(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_metricsLock = new object();
            m_performanceHistory = new Queue<PerformanceMetrics>();
            m_decisionHistory = new List<QualityScalingDecision>();

            // Initialize with medium quality as baseline
            m_baselineQuality = PhysicsQualityLevel.Medium;
            m_currentSettings = new PhysicsQualitySettings();
            m_currentSettings.ApplyQualityLevel(m_baselineQuality);

            m_lastQualityChange = DateTime.UtcNow;
            m_lastPerformanceReport = DateTime.UtcNow;

            // Initialize performance mapping (learned over time)
            m_qualityPerformanceMapping = new Dictionary<PhysicsQualityLevel, float>
            {
                { PhysicsQualityLevel.Ultra, 2.0f },
                { PhysicsQualityLevel.High, 1.5f },
                { PhysicsQualityLevel.Medium, 1.0f },
                { PhysicsQualityLevel.Low, 0.7f },
                { PhysicsQualityLevel.Minimal, 0.4f }
            };

            // Setup adaptation timer
            m_adaptationTimer = new Timer(PerformAdaptation, null, m_adaptationInterval, m_adaptationInterval);

            m_enabled = true;

            m_log.InfoFormat("{0}: Adaptive quality scaling initialized - Baseline: {1}, Target frame time: {2}ms", 
                LogHeader, m_baselineQuality, m_targetFrameTime * 1000);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the adaptive quality scaling system
        /// </summary>
        public void Initialize()
        {
            if (m_disposed)
                return;

            m_enabled = true;
            m_log.InfoFormat("{0}: Adaptive quality scaling system started", LogHeader);
        }

        /// <summary>
        /// Update performance metrics for adaptation decisions
        /// </summary>
        public void UpdatePerformanceMetrics(PerformanceMetrics metrics)
        {
            if (!m_enabled || m_disposed)
                return;

            lock (m_metricsLock)
            {
                metrics.Timestamp = DateTime.UtcNow;
                m_performanceHistory.Enqueue(metrics);

                if (m_performanceHistory.Count > m_maxHistorySize)
                {
                    m_performanceHistory.Dequeue();
                }
            }
        }

        /// <summary>
        /// Get current physics quality settings
        /// </summary>
        public PhysicsQualitySettings GetCurrentSettings()
        {
            return m_currentSettings?.Clone();
        }

        /// <summary>
        /// Set baseline quality level (user preference)
        /// </summary>
        public void SetBaselineQuality(PhysicsQualityLevel quality)
        {
            m_baselineQuality = quality;
            m_log.InfoFormat("{0}: Baseline quality set to {1}", LogHeader, quality);
        }

        /// <summary>
        /// Enable or disable adaptive scaling
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            m_enabled = enabled;
            
            if (!enabled)
            {
                // Return to baseline quality
                ApplyQualityLevel(m_baselineQuality, "Adaptive scaling disabled");
            }
            
            m_log.InfoFormat("{0}: Adaptive scaling {1}", LogHeader, enabled ? "enabled" : "disabled");
        }

        /// <summary>
        /// Force a specific quality level (overrides adaptation temporarily)
        /// </summary>
        public void ForceQualityLevel(PhysicsQualityLevel quality, TimeSpan duration)
        {
            ApplyQualityLevel(quality, $"Forced quality level for {duration.TotalSeconds}s");
            
            // Disable adaptation temporarily
            m_enabled = false;
            
            // Re-enable after duration
            Task.Delay(duration).ContinueWith(_ => 
            {
                m_enabled = true;
                m_log.InfoFormat("{0}: Forced quality period ended, resuming adaptive scaling", LogHeader);
            });
        }

        /// <summary>
        /// Get comprehensive performance and adaptation report
        /// </summary>
        public string GetPerformanceReport()
        {
            if (!m_enabled || m_disposed)
                return "Adaptive quality scaling not available";

            lock (m_metricsLock)
            {
                var currentMetrics = GetCurrentAverageMetrics();
                var report = $"Adaptive Quality Scaling Performance:\\n";
                report += $"  Current Quality: {m_currentSettings.OverallQuality}\\n";
                report += $"  Baseline Quality: {m_baselineQuality}\\n";
                report += $"  Target Frame Time: {m_targetFrameTime * 1000:F1}ms\\n";
                report += $"  Current Frame Time: {currentMetrics.FrameTime * 1000:F1}ms\\n";
                report += $"  Physics Load: {currentMetrics.PhysicsLoad:P2}\\n";
                report += $"  Memory Usage: {currentMetrics.MemoryUsageMB:F1}MB\\n";
                report += $"  CPU Usage: {currentMetrics.CPUUsagePercent:F1}%\\n";
                report += $"  Active Objects: {currentMetrics.ActiveObjects}\\n";
                report += $"  Total Adaptations: {m_totalAdaptations}\\n";
                report += $"  Quality Upgrades: {m_qualityUpgrades}\\n";
                report += $"  Quality Downgrades: {m_qualityDowngrades}\\n";
                report += $"  Last Quality Change: {(DateTime.UtcNow - m_lastQualityChange).TotalSeconds:F1}s ago\\n";
                report += $"  Performance History: {m_performanceHistory.Count} samples\\n";

                return report;
            }
        }

        /// <summary>
        /// Get recent adaptation decisions for analysis
        /// </summary>
        public List<QualityScalingDecision> GetRecentDecisions(int count = 10)
        {
            return m_decisionHistory.TakeLast(count).ToList();
        }

        #endregion

        #region Private Methods

        private void PerformAdaptation(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                // Check if enough time has passed since last change for stability
                if (DateTime.UtcNow - m_lastQualityChange < m_stabilizationPeriod)
                    return;

                // Get recent performance metrics
                var currentMetrics = GetCurrentAverageMetrics();
                if (currentMetrics.Timestamp == DateTime.MinValue)
                    return; // No metrics available

                // Analyze performance and make adaptation decision
                var decision = AnalyzePerformanceAndDecide(currentMetrics);
                
                if (decision.NewQuality != decision.PreviousQuality)
                {
                    ApplyQualityLevel(decision.NewQuality, decision.Reason);
                    RecordDecision(decision);
                    m_totalAdaptations++;
                    
                    if (decision.NewQuality > decision.PreviousQuality)
                    {
                        m_qualityUpgrades++;
                        m_consecutiveUpgrades++;
                        m_consecutiveDowngrades = 0;
                    }
                    else
                    {
                        m_qualityDowngrades++;
                        m_consecutiveDowngrades++;
                        m_consecutiveUpgrades = 0;
                    }
                    
                    m_lastQualityChange = DateTime.UtcNow;
                }

                // Update performance mapping based on experience
                UpdatePerformanceLearning(currentMetrics);

                // Performance reporting
                if (DateTime.UtcNow - m_lastPerformanceReport >= PerformanceReportInterval)
                {
                    m_log.InfoFormat("{0}: {1}", LogHeader, GetPerformanceReport().Replace("\\n", " "));
                    m_lastPerformanceReport = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during adaptation: {1}", LogHeader, ex.Message);
            }
        }

        private PerformanceMetrics GetCurrentAverageMetrics()
        {
            lock (m_metricsLock)
            {
                if (m_performanceHistory.Count == 0)
                    return new PerformanceMetrics { Timestamp = DateTime.MinValue };

                // Average recent metrics (last 5 samples)
                var recentMetrics = m_performanceHistory.TakeLast(5).ToArray();
                
                return new PerformanceMetrics
                {
                    FrameTime = recentMetrics.Average(m => m.FrameTime),
                    PhysicsTime = recentMetrics.Average(m => m.PhysicsTime),
                    CollisionTime = recentMetrics.Average(m => m.CollisionTime),
                    ConstraintTime = recentMetrics.Average(m => m.ConstraintTime),
                    MemoryUsageMB = recentMetrics.Average(m => m.MemoryUsageMB),
                    CPUUsagePercent = recentMetrics.Average(m => m.CPUUsagePercent),
                    ActiveObjects = (int)recentMetrics.Average(m => m.ActiveObjects),
                    ActiveConstraints = (int)recentMetrics.Average(m => m.ActiveConstraints),
                    CollisionPairs = (int)recentMetrics.Average(m => m.CollisionPairs),
                    Timestamp = DateTime.UtcNow
                };
            }
        }

        private QualityScalingDecision AnalyzePerformanceAndDecide(PerformanceMetrics metrics)
        {
            var decision = new QualityScalingDecision
            {
                PreviousQuality = m_currentSettings.OverallQuality,
                NewQuality = m_currentSettings.OverallQuality,
                Metrics = metrics,
                DecisionTime = DateTime.UtcNow,
                ConfidenceLevel = 0.5f
            };

            var currentQuality = m_currentSettings.OverallQuality;
            var proposedQuality = currentQuality;
            var reasons = new List<string>();

            // Performance analysis
            float frameTimeRatio = metrics.FrameTime / m_targetFrameTime;
            float memoryPressure = metrics.MemoryPressure;
            bool cpuOverloaded = metrics.CPUUsagePercent > m_cpuUsageThreshold;

            // Decision logic based on multiple factors
            if (frameTimeRatio > (1.0f + m_frameTimeToleranceDown))
            {
                // Performance is poor, consider downgrading
                if (currentQuality > PhysicsQualityLevel.Minimal)
                {
                    proposedQuality = (PhysicsQualityLevel)((int)currentQuality - 1);
                    reasons.Add($"High frame time ({metrics.FrameTime * 1000:F1}ms)");
                    decision.ConfidenceLevel += 0.3f;
                }
            }
            else if (frameTimeRatio < (1.0f - m_frameTimeToleranceUp))
            {
                // Performance is good, consider upgrading
                if (currentQuality < m_baselineQuality && !cpuOverloaded && memoryPressure < m_memoryPressureThreshold)
                {
                    proposedQuality = (PhysicsQualityLevel)((int)currentQuality + 1);
                    reasons.Add($"Good frame time ({metrics.FrameTime * 1000:F1}ms)");
                    decision.ConfidenceLevel += 0.2f;
                }
            }

            // Memory pressure analysis
            if (memoryPressure > m_memoryPressureThreshold && currentQuality > PhysicsQualityLevel.Minimal)
            {
                proposedQuality = (PhysicsQualityLevel)Math.Min((int)proposedQuality, (int)currentQuality - 1);
                reasons.Add($"High memory pressure ({memoryPressure:P1})");
                decision.ConfidenceLevel += 0.2f;
            }

            // CPU usage analysis
            if (cpuOverloaded && currentQuality > PhysicsQualityLevel.Minimal)
            {
                proposedQuality = (PhysicsQualityLevel)Math.Min((int)proposedQuality, (int)currentQuality - 1);
                reasons.Add($"High CPU usage ({metrics.CPUUsagePercent:F1}%)");
                decision.ConfidenceLevel += 0.2f;
            }

            // Object count analysis
            if (metrics.ActiveObjects > 500 && currentQuality > PhysicsQualityLevel.Low)
            {
                proposedQuality = (PhysicsQualityLevel)Math.Min((int)proposedQuality, (int)PhysicsQualityLevel.Low);
                reasons.Add($"High object count ({metrics.ActiveObjects})");
                decision.ConfidenceLevel += 0.1f;
            }

            // Stability checks - prevent oscillations
            if (proposedQuality != currentQuality)
            {
                // Check for consecutive changes in same direction
                if ((proposedQuality > currentQuality && m_consecutiveUpgrades >= m_maxConsecutiveChanges) ||
                    (proposedQuality < currentQuality && m_consecutiveDowngrades >= m_maxConsecutiveChanges))
                {
                    proposedQuality = currentQuality;
                    reasons.Clear();
                    reasons.Add("Stability check - too many consecutive changes");
                    decision.ConfidenceLevel = 0.1f;
                }
            }

            decision.NewQuality = proposedQuality;
            decision.Reason = string.Join(", ", reasons);
            
            if (reasons.Count == 0)
            {
                decision.Reason = "No adaptation needed";
                decision.ConfidenceLevel = 0.8f;
            }

            return decision;
        }

        private void ApplyQualityLevel(PhysicsQualityLevel quality, string reason)
        {
            var oldQuality = m_currentSettings.OverallQuality;
            m_currentSettings.ApplyQualityLevel(quality);

            // Apply settings to physics systems
            ApplySettingsToScene(m_currentSettings);

            m_log.InfoFormat("{0}: Quality changed from {1} to {2} - {3}", LogHeader, oldQuality, quality, reason);
        }

        private void ApplySettingsToScene(PhysicsQualitySettings settings)
        {
            // This would apply the settings to the actual physics scene
            // In a real implementation, this would call into BSScene and related systems
            
            try
            {
                // Apply collision detection settings
                if (!m_scene.InSimulationTime)
                {
                    // Example of applying collision iteration settings
                    // m_scene.PE.SetSolverIterations(m_scene.World, settings.ConstraintSolverIterations);
                }

                // Apply time step settings
                m_scene.SetTimeStep(settings.TimeStep);

                // Apply threading settings
                // This would configure the multithreaded systems based on MaxWorkerThreads

                // Apply memory management settings
                // This would configure object pooling and memory optimization

                m_log.DebugFormat("{0}: Applied quality settings - Iterations: {1}, TimeStep: {2}, Workers: {3}", 
                    LogHeader, settings.ConstraintSolverIterations, settings.TimeStep, settings.MaxWorkerThreads);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error applying quality settings: {1}", LogHeader, ex.Message);
            }
        }

        private void RecordDecision(QualityScalingDecision decision)
        {
            m_decisionHistory.Add(decision);
            if (m_decisionHistory.Count > m_maxDecisionHistory)
            {
                m_decisionHistory.RemoveAt(0);
            }
        }

        private void UpdatePerformanceLearning(PerformanceMetrics metrics)
        {
            // Update the performance mapping based on actual experience
            var currentQuality = m_currentSettings.OverallQuality;
            var performanceRatio = metrics.FrameTime / m_targetFrameTime;

            if (m_qualityPerformanceMapping.ContainsKey(currentQuality))
            {
                // Exponential moving average for learning
                var currentMapping = m_qualityPerformanceMapping[currentQuality];
                var learningRate = 0.1f;
                m_qualityPerformanceMapping[currentQuality] = currentMapping * (1 - learningRate) + performanceRatio * learningRate;
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (m_disposed)
                return;

            try
            {
                m_enabled = false;
                m_adaptationTimer?.Dispose();

                lock (m_metricsLock)
                {
                    m_performanceHistory.Clear();
                    m_decisionHistory.Clear();
                }

                m_disposed = true;
                m_log.InfoFormat("{0}: Adaptive quality scaling disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public PhysicsQualityLevel CurrentQuality => m_currentSettings?.OverallQuality ?? PhysicsQualityLevel.Medium;
        public PhysicsQualityLevel BaselineQuality => m_baselineQuality;
        public long TotalAdaptations => m_totalAdaptations;
        public float TargetFrameTime => m_targetFrameTime;
        public TimeSpan TimeSinceLastChange => DateTime.UtcNow - m_lastQualityChange;

        #endregion
    }
}