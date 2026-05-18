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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Performance profiling operation types
    /// </summary>
    public enum ProfileOperation
    {
        FragmentGeneration,
        SceneObjectCreation,
        PhysicsBodyCreation,
        ParticleEffects,
        SoundEffects,
        EnvironmentalEffects,
        PhysicsIntegration,
        ChainReactions,
        TotalDestruction,
        StructuralIntegrity,
        MaterialCalculations
    }

    /// <summary>
    /// Individual performance measurement
    /// </summary>
    public class PerformanceMeasurement
    {
        public ProfileOperation Operation { get; set; }
        public long ElapsedTicks { get; set; }
        public double ElapsedMilliseconds => ElapsedTicks / (double)TimeSpan.TicksPerMillisecond;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public int ObjectCount { get; set; } = 1;
        public string Details { get; set; } = "";
    }

    /// <summary>
    /// Aggregated performance statistics for a specific operation
    /// </summary>
    public class PerformanceStatistics
    {
        public ProfileOperation Operation { get; set; }
        public int SampleCount { get; set; }
        public double TotalMilliseconds { get; set; }
        public double AverageMilliseconds { get; set; }
        public double MinMilliseconds { get; set; } = double.MaxValue;
        public double MaxMilliseconds { get; set; } = double.MinValue;
        public double MedianMilliseconds { get; set; }
        public double PercentileP95Milliseconds { get; set; }
        public int TotalObjectsProcessed { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public void Update(IEnumerable<PerformanceMeasurement> measurements)
        {
            var measurementList = measurements.Where(m => m.Operation == Operation).ToList();
            if (!measurementList.Any()) return;

            SampleCount = measurementList.Count;
            TotalMilliseconds = measurementList.Sum(m => m.ElapsedMilliseconds);
            AverageMilliseconds = TotalMilliseconds / SampleCount;
            MinMilliseconds = measurementList.Min(m => m.ElapsedMilliseconds);
            MaxMilliseconds = measurementList.Max(m => m.ElapsedMilliseconds);
            TotalObjectsProcessed = measurementList.Sum(m => m.ObjectCount);

            var sortedTimes = measurementList.Select(m => m.ElapsedMilliseconds).OrderBy(t => t).ToList();
            MedianMilliseconds = sortedTimes[sortedTimes.Count / 2];
            PercentileP95Milliseconds = sortedTimes[(int)(sortedTimes.Count * 0.95)];
            
            LastUpdated = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// High-performance profiler for destruction system operations
    /// Identifies actual bottlenecks without impacting performance
    /// </summary>
    public class DestructionPerformanceProfiler
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION PROFILER]";

        private readonly ConcurrentQueue<PerformanceMeasurement> m_measurements;
        private readonly Dictionary<ProfileOperation, PerformanceStatistics> m_statistics;
        private readonly object m_statisticsLock = new object();
        
        private readonly Timer m_analysisTimer;
        private readonly Timer m_reportTimer;
        
        private bool m_isEnabled;
        private int m_maxMeasurements = 10000;
        private TimeSpan m_analysisInterval = TimeSpan.FromSeconds(5);
        private TimeSpan m_reportInterval = TimeSpan.FromMinutes(2);
        
        // Real-time performance tracking
        private long m_totalDestructions;
        private double m_averageDestructionTime;
        private DateTime m_lastBottleneckAlert = DateTime.MinValue;
        
        // Bottleneck detection thresholds
        private const double BOTTLENECK_THRESHOLD_MS = 50.0; // Operations taking > 50ms are bottlenecks
        private const double CRITICAL_THRESHOLD_MS = 100.0;  // Operations taking > 100ms are critical
        private const int MIN_SAMPLES_FOR_ANALYSIS = 10;     // Minimum samples before analysis

        public DestructionPerformanceProfiler(bool enabled = true)
        {
            m_measurements = new ConcurrentQueue<PerformanceMeasurement>();
            m_statistics = new Dictionary<ProfileOperation, PerformanceStatistics>();
            m_isEnabled = enabled;

            if (m_isEnabled)
            {
                // Initialize statistics for all operations
                foreach (ProfileOperation operation in Enum.GetValues<ProfileOperation>())
                {
                    m_statistics[operation] = new PerformanceStatistics { Operation = operation };
                }

                // Start analysis and reporting timers
                m_analysisTimer = new Timer(AnalyzePerformance, null, m_analysisInterval, m_analysisInterval);
                m_reportTimer = new Timer(GeneratePerformanceReport, null, m_reportInterval, m_reportInterval);
                
                m_log.InfoFormat("{0}: Performance profiler initialized", LogHeader);
            }
        }

        /// <summary>
        /// Start measuring a specific operation
        /// </summary>
        public IDisposable StartMeasurement(ProfileOperation operation, int objectCount = 1, string details = "")
        {
            if (!m_isEnabled) return new NoOpMeasurement();
            
            return new PerformanceTimer(this, operation, objectCount, details);
        }

        /// <summary>
        /// Record a completed measurement
        /// </summary>
        internal void RecordMeasurement(PerformanceMeasurement measurement)
        {
            if (!m_isEnabled) return;

            m_measurements.Enqueue(measurement);
            
            // Update real-time tracking
            if (measurement.Operation == ProfileOperation.TotalDestruction)
            {
                Interlocked.Increment(ref m_totalDestructions);
                
                // Update rolling average
                var currentAvg = m_averageDestructionTime;
                var newAvg = (currentAvg * 0.9) + (measurement.ElapsedMilliseconds * 0.1);
                Interlocked.Exchange(ref m_averageDestructionTime, newAvg);
            }

            // Trim old measurements if we exceed max count
            while (m_measurements.Count > m_maxMeasurements)
            {
                m_measurements.TryDequeue(out _);
            }

            // Check for immediate bottlenecks
            CheckForBottlenecks(measurement);
        }

        /// <summary>
        /// Get current performance statistics for a specific operation
        /// </summary>
        public PerformanceStatistics GetStatistics(ProfileOperation operation)
        {
            lock (m_statisticsLock)
            {
                return m_statistics.TryGetValue(operation, out var stats) ? stats : null;
            }
        }

        /// <summary>
        /// Get comprehensive performance report
        /// </summary>
        public string GetDetailedReport()
        {
            if (!m_isEnabled) return "Performance profiling is disabled";

            lock (m_statisticsLock)
            {
                var report = $"=== Destruction System Performance Report ===\n";
                report += $"Total Destructions: {m_totalDestructions:N0}\n";
                report += $"Average Destruction Time: {m_averageDestructionTime:F2}ms\n";
                report += $"Total Measurements: {m_measurements.Count:N0}\n\n";

                // Sort operations by average time (worst first)
                var sortedStats = m_statistics.Values
                    .Where(s => s.SampleCount >= MIN_SAMPLES_FOR_ANALYSIS)
                    .OrderByDescending(s => s.AverageMilliseconds)
                    .ToList();

                if (!sortedStats.Any())
                {
                    report += "No performance data available yet.\n";
                    return report;
                }

                report += "Performance Breakdown (worst first):\n";
                report += "Operation                | Avg(ms) | Max(ms) | P95(ms) | Samples | Objects | Status\n";
                report += "-------------------------|---------|---------|---------|---------|---------|----------\n";

                foreach (var stats in sortedStats)
                {
                    var status = GetPerformanceStatus(stats.AverageMilliseconds);
                    report += $"{stats.Operation,-24} | {stats.AverageMilliseconds,7:F2} | " +
                             $"{stats.MaxMilliseconds,7:F2} | {stats.PercentileP95Milliseconds,7:F2} | " +
                             $"{stats.SampleCount,7:N0} | {stats.TotalObjectsProcessed,7:N0} | {status}\n";
                }

                // Identify bottlenecks
                var bottlenecks = sortedStats.Where(s => s.AverageMilliseconds > BOTTLENECK_THRESHOLD_MS).ToList();
                if (bottlenecks.Any())
                {
                    report += "\n🔴 PERFORMANCE BOTTLENECKS DETECTED:\n";
                    foreach (var bottleneck in bottlenecks)
                    {
                        var impact = CalculatePerformanceImpact(bottleneck);
                        report += $"  • {bottleneck.Operation}: {bottleneck.AverageMilliseconds:F2}ms avg ({impact}% of total time)\n";
                    }
                }

                // Recommendations
                var recommendations = GenerateRecommendations(sortedStats);
                if (recommendations.Any())
                {
                    report += "\n💡 OPTIMIZATION RECOMMENDATIONS:\n";
                    foreach (var recommendation in recommendations)
                    {
                        report += $"  • {recommendation}\n";
                    }
                }

                return report;
            }
        }

        /// <summary>
        /// Get the top performance bottlenecks
        /// </summary>
        public List<(ProfileOperation Operation, double AvgTimeMs, string Recommendation)> GetTopBottlenecks(int count = 3)
        {
            lock (m_statisticsLock)
            {
                return m_statistics.Values
                    .Where(s => s.SampleCount >= MIN_SAMPLES_FOR_ANALYSIS && s.AverageMilliseconds > BOTTLENECK_THRESHOLD_MS)
                    .OrderByDescending(s => s.AverageMilliseconds)
                    .Take(count)
                    .Select(s => (s.Operation, s.AverageMilliseconds, GetOptimizationRecommendation(s.Operation)))
                    .ToList();
            }
        }

        /// <summary>
        /// Check if the system is currently experiencing performance issues
        /// </summary>
        public bool IsPerformanceCritical()
        {
            return m_averageDestructionTime > CRITICAL_THRESHOLD_MS;
        }

        /// <summary>
        /// Get current real-time performance metrics
        /// </summary>
        public (long totalDestructions, double avgTimeMs, bool isCritical) GetRealTimeMetrics()
        {
            return (m_totalDestructions, m_averageDestructionTime, IsPerformanceCritical());
        }

        private void CheckForBottlenecks(PerformanceMeasurement measurement)
        {
            if (measurement.ElapsedMilliseconds > CRITICAL_THRESHOLD_MS)
            {
                var timeSinceLastAlert = DateTime.UtcNow - m_lastBottleneckAlert;
                if (timeSinceLastAlert.TotalSeconds > 30) // Throttle alerts to every 30 seconds
                {
                    m_log.WarnFormat("{0}: CRITICAL PERFORMANCE: {1} took {2:F2}ms (threshold: {3}ms) - {4}",
                        LogHeader, measurement.Operation, measurement.ElapsedMilliseconds, CRITICAL_THRESHOLD_MS, measurement.Details);
                    m_lastBottleneckAlert = DateTime.UtcNow;
                }
            }
        }

        private void AnalyzePerformance(object state)
        {
            if (!m_isEnabled) return;

            try
            {
                var measurements = m_measurements.ToArray();
                if (measurements.Length < MIN_SAMPLES_FOR_ANALYSIS) return;

                lock (m_statisticsLock)
                {
                    // Update statistics for each operation
                    foreach (var operation in Enum.GetValues<ProfileOperation>())
                    {
                        m_statistics[operation].Update(measurements);
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during performance analysis: {1}", LogHeader, ex.Message);
            }
        }

        private void GeneratePerformanceReport(object state)
        {
            if (!m_isEnabled) return;

            try
            {
                var report = GetDetailedReport();
                m_log.InfoFormat("{0}: Performance Analysis Report:\n{1}", LogHeader, report);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error generating performance report: {1}", LogHeader, ex.Message);
            }
        }

        private string GetPerformanceStatus(double avgTimeMs)
        {
            if (avgTimeMs > CRITICAL_THRESHOLD_MS) return "CRITICAL";
            if (avgTimeMs > BOTTLENECK_THRESHOLD_MS) return "SLOW";
            if (avgTimeMs > 10.0) return "MODERATE";
            return "GOOD";
        }

        private double CalculatePerformanceImpact(PerformanceStatistics stats)
        {
            var totalTime = m_statistics.Values.Sum(s => s.TotalMilliseconds);
            if (totalTime == 0) return 0;
            return (stats.TotalMilliseconds / totalTime) * 100;
        }

        private List<string> GenerateRecommendations(List<PerformanceStatistics> sortedStats)
        {
            var recommendations = new List<string>();

            foreach (var stats in sortedStats.Take(3)) // Top 3 bottlenecks
            {
                if (stats.AverageMilliseconds > BOTTLENECK_THRESHOLD_MS)
                {
                    recommendations.Add(GetOptimizationRecommendation(stats.Operation));
                }
            }

            return recommendations;
        }

        private string GetOptimizationRecommendation(ProfileOperation operation)
        {
            return operation switch
            {
                ProfileOperation.SceneObjectCreation => "Consider object pooling and batch creation of scene objects",
                ProfileOperation.PhysicsBodyCreation => "Implement async physics body creation to avoid blocking main thread",
                ProfileOperation.FragmentGeneration => "Use pre-computed fragment templates and reduce calculation complexity",
                ProfileOperation.ParticleEffects => "Batch particle system updates and reduce particle counts",
                ProfileOperation.EnvironmentalEffects => "Implement environmental effect LOD system and culling",
                ProfileOperation.PhysicsIntegration => "Move physics integration to background thread with result caching",
                ProfileOperation.ChainReactions => "Limit chain reaction depth and use spatial partitioning for efficiency",
                ProfileOperation.TotalDestruction => "Implement full async destruction pipeline with progress tracking",
                ProfileOperation.StructuralIntegrity => "Use cached connectivity graphs and update incrementally",
                ProfileOperation.MaterialCalculations => "Pre-compute material property lookups and cache results",
                ProfileOperation.SoundEffects => "Implement sound pooling and distance-based culling",
                _ => "Profile this operation in more detail to identify specific optimizations"
            };
        }

        public void Dispose()
        {
            m_isEnabled = false;
            m_analysisTimer?.Dispose();
            m_reportTimer?.Dispose();
            m_log.InfoFormat("{0}: Performance profiler disposed", LogHeader);
        }

        /// <summary>
        /// Performance timer that automatically records measurements
        /// </summary>
        private class PerformanceTimer : IDisposable
        {
            private readonly DestructionPerformanceProfiler m_profiler;
            private readonly ProfileOperation m_operation;
            private readonly int m_objectCount;
            private readonly string m_details;
            private readonly Stopwatch m_stopwatch;

            public PerformanceTimer(DestructionPerformanceProfiler profiler, ProfileOperation operation, int objectCount, string details)
            {
                m_profiler = profiler;
                m_operation = operation;
                m_objectCount = objectCount;
                m_details = details;
                m_stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                m_stopwatch.Stop();
                m_profiler.RecordMeasurement(new PerformanceMeasurement
                {
                    Operation = m_operation,
                    ElapsedTicks = m_stopwatch.ElapsedTicks,
                    ObjectCount = m_objectCount,
                    Details = m_details
                });
            }
        }

        /// <summary>
        /// No-op measurement for when profiling is disabled
        /// </summary>
        private class NoOpMeasurement : IDisposable
        {
            public void Dispose() { }
        }
    }
}