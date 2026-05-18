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
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Garbage collection optimization and monitoring for physics systems
    /// </summary>
    public class PhysicsGCOptimizer : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[PHYSICS GC OPTIMIZER]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly Timer m_monitoringTimer;
        private readonly Timer m_optimizationTimer;
        private bool m_disposed;
        private bool m_enabled;

        // GC Monitoring
        private long m_lastGen0Collections;
        private long m_lastGen1Collections;
        private long m_lastGen2Collections;
        private long m_lastTotalMemory;
        private DateTime m_lastGCTime;
        private readonly TimeSpan MonitoringInterval = TimeSpan.FromSeconds(30);

        // GC Optimization Settings
        private readonly TimeSpan OptimizationInterval = TimeSpan.FromMinutes(5);
        private readonly long MaxMemoryBeforeOptimization = 100 * 1024 * 1024; // 100MB
        private readonly int MaxGen0CollectionsPerMinute = 10;
        private readonly int MaxGen1CollectionsPerMinute = 5;
        private readonly bool EnableLargeObjectHeapCompaction = true;
        private readonly bool EnableServerGCMode = true;

        // Performance Metrics
        private long m_totalOptimizations;
        private long m_memoryReclaimed;
        private TimeSpan m_totalGCTime;
        private float m_averageGCPause;

        // Allocation Tracking
        private readonly object m_allocationLock;
        private long m_physicsAllocations;
        private long m_pooledObjectsReused;
        private long m_temporaryAllocations;

        #endregion

        #region Constructor

        public PhysicsGCOptimizer(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_allocationLock = new object();

            // Initialize GC monitoring
            m_lastGen0Collections = GC.CollectionCount(0);
            m_lastGen1Collections = GC.CollectionCount(1);
            m_lastGen2Collections = GC.CollectionCount(2);
            m_lastTotalMemory = GC.GetTotalMemory(false);
            m_lastGCTime = DateTime.UtcNow;

            // Configure GC settings for optimal physics performance
            ConfigureGCSettings();

            // Setup monitoring and optimization timers
            m_monitoringTimer = new Timer(MonitorGCPerformance, null, MonitoringInterval, MonitoringInterval);
            m_optimizationTimer = new Timer(PerformGCOptimization, null, OptimizationInterval, OptimizationInterval);

            m_enabled = true;
            m_log.InfoFormat("{0}: Physics GC optimizer initialized", LogHeader);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the GC optimizer
        /// </summary>
        public void Initialize()
        {
            if (m_disposed || m_enabled)
                return;

            m_enabled = true;
            m_log.InfoFormat("{0}: GC optimizer started", LogHeader);
        }

        /// <summary>
        /// Request immediate garbage collection with optimization
        /// </summary>
        public void RequestOptimizedGC(bool force = false)
        {
            if (!m_enabled || m_disposed)
                return;

            Task.Run(() => PerformOptimizedGarbageCollection(force));
        }

        /// <summary>
        /// Track physics object allocation for monitoring
        /// </summary>
        public void TrackPhysicsAllocation(int size = 1)
        {
            if (!m_enabled)
                return;

            lock (m_allocationLock)
            {
                m_physicsAllocations += size;
            }
        }

        /// <summary>
        /// Track pooled object reuse for monitoring
        /// </summary>
        public void TrackPooledObjectReuse(int count = 1)
        {
            if (!m_enabled)
                return;

            lock (m_allocationLock)
            {
                m_pooledObjectsReused += count;
            }
        }

        /// <summary>
        /// Track temporary allocation for monitoring
        /// </summary>
        public void TrackTemporaryAllocation(int size = 1)
        {
            if (!m_enabled)
                return;

            lock (m_allocationLock)
            {
                m_temporaryAllocations += size;
            }
        }

        /// <summary>
        /// Get comprehensive GC performance report
        /// </summary>
        public string GetGCPerformanceReport()
        {
            if (!m_enabled || m_disposed)
                return "GC optimizer not active";

            var currentMemory = GC.GetTotalMemory(false);
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);

            var report = $"Physics GC Optimizer Performance Report:\\n";
            report += $"  Current Memory Usage: {currentMemory / 1024 / 1024:F1} MB\\n";
            report += $"  GC Collections: Gen0={gen0}, Gen1={gen1}, Gen2={gen2}\\n";
            report += $"  Total Optimizations: {m_totalOptimizations}\\n";
            report += $"  Memory Reclaimed: {m_memoryReclaimed / 1024 / 1024:F1} MB\\n";
            report += $"  Total GC Time: {m_totalGCTime.TotalMilliseconds:F0} ms\\n";
            report += $"  Average GC Pause: {m_averageGCPause:F2} ms\\n";

            lock (m_allocationLock)
            {
                report += $"  Physics Allocations: {m_physicsAllocations}\\n";
                report += $"  Pooled Objects Reused: {m_pooledObjectsReused}\\n";
                report += $"  Temporary Allocations: {m_temporaryAllocations}\\n";
                
                var poolEfficiency = m_physicsAllocations > 0 ? 
                    (float)m_pooledObjectsReused / (m_physicsAllocations + m_pooledObjectsReused) * 100f : 0f;
                report += $"  Pool Efficiency: {poolEfficiency:F1}%\\n";
            }

            // GC Mode Information
            report += $"  GC Mode: {(GCSettings.IsServerGC ? "Server" : "Workstation")}\\n";
            report += $"  GC Latency Mode: {GCSettings.LatencyMode}\\n";

            return report;
        }

        /// <summary>
        /// Optimize memory usage for physics simulation
        /// </summary>
        public void OptimizeMemoryUsage()
        {
            if (!m_enabled || m_disposed)
                return;

            Task.Run(() =>
            {
                try
                {
                    // Clear array pools
                    PhysicsArrayPool<float>.Clear();
                    PhysicsArrayPool<int>.Clear();
                    PhysicsArrayPool<PhysicsWorkItem>.Clear();

                    // Request optimized garbage collection
                    PerformOptimizedGarbageCollection(true);

                    m_log.InfoFormat("{0}: Memory optimization completed", LogHeader);
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("{0}: Error during memory optimization: {1}", LogHeader, ex.Message);
                }
            });
        }

        #endregion

        #region Private Methods

        private void ConfigureGCSettings()
        {
            try
            {
                // Configure for low-latency physics processing
                if (GCSettings.LatencyMode == GCLatencyMode.Interactive || 
                    GCSettings.LatencyMode == GCLatencyMode.Batch)
                {
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    m_log.InfoFormat("{0}: Set GC latency mode to SustainedLowLatency", LogHeader);
                }

                // Enable large object heap compaction if supported
                if (EnableLargeObjectHeapCompaction)
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    m_log.InfoFormat("{0}: Enabled large object heap compaction", LogHeader);
                }

                m_log.InfoFormat("{0}: GC configuration completed - Server GC: {1}, Latency Mode: {2}", 
                    LogHeader, GCSettings.IsServerGC, GCSettings.LatencyMode);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to configure GC settings: {1}", LogHeader, ex.Message);
            }
        }

        private void MonitorGCPerformance(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                var now = DateTime.UtcNow;
                var currentGen0 = GC.CollectionCount(0);
                var currentGen1 = GC.CollectionCount(1);
                var currentGen2 = GC.CollectionCount(2);
                var currentMemory = GC.GetTotalMemory(false);

                var timeDelta = now - m_lastGCTime;
                var gen0Delta = currentGen0 - m_lastGen0Collections;
                var gen1Delta = currentGen1 - m_lastGen1Collections;
                var gen2Delta = currentGen2 - m_lastGen2Collections;

                // Calculate collection rates per minute
                var minutesFactor = (float)timeDelta.TotalMinutes;
                if (minutesFactor > 0)
                {
                    var gen0Rate = gen0Delta / minutesFactor;
                    var gen1Rate = gen1Delta / minutesFactor;
                    var gen2Rate = gen2Delta / minutesFactor;

                    // Check if we need optimization
                    bool needsOptimization = false;
                    var reasons = new System.Collections.Generic.List<string>();

                    if (currentMemory > MaxMemoryBeforeOptimization)
                    {
                        needsOptimization = true;
                        reasons.Add($"High memory usage ({currentMemory / 1024 / 1024} MB)");
                    }

                    if (gen0Rate > MaxGen0CollectionsPerMinute)
                    {
                        needsOptimization = true;
                        reasons.Add($"High Gen0 collection rate ({gen0Rate:F1}/min)");
                    }

                    if (gen1Rate > MaxGen1CollectionsPerMinute)
                    {
                        needsOptimization = true;
                        reasons.Add($"High Gen1 collection rate ({gen1Rate:F1}/min)");
                    }

                    if (gen2Delta > 0)
                    {
                        needsOptimization = true;
                        reasons.Add("Gen2 collections detected");
                    }

                    if (needsOptimization)
                    {
                        m_log.InfoFormat("{0}: GC optimization triggered - Reasons: {1}", 
                            LogHeader, string.Join(", ", reasons));
                        Task.Run(() => PerformOptimizedGarbageCollection(false));
                    }
                }

                // Update last values
                m_lastGen0Collections = currentGen0;
                m_lastGen1Collections = currentGen1;
                m_lastGen2Collections = currentGen2;
                m_lastTotalMemory = currentMemory;
                m_lastGCTime = now;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error monitoring GC performance: {1}", LogHeader, ex.Message);
            }
        }

        private void PerformGCOptimization(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            Task.Run(() => PerformOptimizedGarbageCollection(false));
        }

        private void PerformOptimizedGarbageCollection(bool force)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                var startTime = DateTime.UtcNow;
                var memoryBefore = GC.GetTotalMemory(false);
                var stopwatch = Stopwatch.StartNew();

                // Perform garbage collection with optimization
                if (force || memoryBefore > MaxMemoryBeforeOptimization / 2)
                {
                    // Clear weak references first
                    GC.Collect(0, GCCollectionMode.Optimized, false);
                    GC.WaitForPendingFinalizers();

                    // Full collection if needed
                    if (force || memoryBefore > MaxMemoryBeforeOptimization)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, true);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(2, GCCollectionMode.Forced, true);
                    }

                    // Compact large object heap if configured
                    if (EnableLargeObjectHeapCompaction && (force || memoryBefore > MaxMemoryBeforeOptimization))
                    {
                        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, true);
                    }
                }

                stopwatch.Stop();
                var memoryAfter = GC.GetTotalMemory(false);
                var memoryReclaimed = memoryBefore - memoryAfter;
                var gcTime = stopwatch.Elapsed;

                // Update statistics
                Interlocked.Increment(ref m_totalOptimizations);
                Interlocked.Add(ref m_memoryReclaimed, memoryReclaimed);
                m_totalGCTime = m_totalGCTime.Add(gcTime);
                
                // Update average GC pause time (exponential smoothing)
                float newPause = (float)gcTime.TotalMilliseconds;
                m_averageGCPause = m_averageGCPause * 0.9f + newPause * 0.1f;

                if (memoryReclaimed > 1024 * 1024) // Only log if significant memory was reclaimed
                {
                    m_log.InfoFormat("{0}: GC optimization completed - Reclaimed {1:F1} MB in {2:F0} ms", 
                        LogHeader, memoryReclaimed / 1024.0 / 1024.0, gcTime.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during GC optimization: {1}", LogHeader, ex.Message);
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
                m_monitoringTimer?.Dispose();
                m_optimizationTimer?.Dispose();

                // Final cleanup
                PhysicsArrayPool<float>.Clear();
                PhysicsArrayPool<int>.Clear();
                PhysicsArrayPool<PhysicsWorkItem>.Clear();

                m_disposed = true;
                m_log.InfoFormat("{0}: Physics GC optimizer disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public long TotalOptimizations => m_totalOptimizations;
        public long MemoryReclaimed => m_memoryReclaimed;
        public TimeSpan TotalGCTime => m_totalGCTime;
        public float AverageGCPause => m_averageGCPause;
        public long PhysicsAllocations => m_physicsAllocations;
        public long PooledObjectsReused => m_pooledObjectsReused;

        #endregion
    }
}