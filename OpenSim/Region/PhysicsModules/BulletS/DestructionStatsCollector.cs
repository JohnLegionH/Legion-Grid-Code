using System;
using System.Collections.Generic;
using System.Threading;
using OpenSim.Framework.Monitoring;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Collects and manages performance statistics for the destruction system.
    /// Integrates with OpenSim's StatsManager for comprehensive monitoring.
    /// </summary>
    public class DestructionStatsCollector : IDisposable
    {
        private readonly string m_regionName;
        private readonly object m_statLock = new object();
        private bool m_disposed = false;
        private readonly List<Stat> m_registeredStats = new List<Stat>();
        
        // Performance Counters
        private int m_totalDestructions;
        private int m_totalFragments;
        private int m_activeDestructibleObjects;
        private int m_activeFragments;
        private int m_chainReactions;
        private int m_autoRepairs;
        
        // Timing Statistics
        private double m_totalUpdateTime;
        private double m_maxUpdateTime;
        private double m_avgUpdateTime;
        private int m_updateCount;
        private DateTime m_lastResetTime = DateTime.UtcNow;
        
        // Memory Statistics
        private long m_memoryUsage;
        private int m_pooledObjectsInUse;
        private int m_pooledObjectsAvailable;
        
        // Configuration Statistics
        private bool m_systemEnabled;
        private float m_currentUpdateRate;
        private int m_maxConfiguredObjects;
        private int m_maxConfiguredFragments;
        
        // Performance Quality Metrics
        private int m_adaptiveQualityReductions;
        private double m_averageFrameTime;
        private int m_skippedUpdates;
        
        public DestructionStatsCollector(string regionName)
        {
            m_regionName = regionName ?? "Unknown";
            RegisterStats();
        }
        
        private void RegisterStats()
        {
            // Register with OpenSim StatsManager for administrative visibility
            string categoryName = $"Destruction System ({m_regionName})";
            
            // Helper method to register and track stats
            void RegisterStat(string name, string description, string unitName, 
                MeasuresOfInterest measures, StatVerbosity verbosity, Func<object> valueGetter)
            {
                var stat = new Stat(name, description, unitName, "", categoryName, m_regionName,
                    StatType.Push, measures, s => {
                        var value = valueGetter();
                        s.Value = Convert.ToDouble(value);
                    }, verbosity);
                StatsManager.RegisterStat(stat);
                m_registeredStats.Add(stat);
            }
            
            // Core Statistics
            RegisterStat("TotalDestructions", "Total number of objects destroyed since startup", "", 
                MeasuresOfInterest.AverageChangeOverTime, StatVerbosity.Info, 
                () => m_totalDestructions);
                
            RegisterStat("TotalFragments", "Total number of fragments created since startup", "", 
                MeasuresOfInterest.AverageChangeOverTime, StatVerbosity.Info, 
                () => m_totalFragments);
                
            RegisterStat("ActiveDestructibleObjects", "Current number of destructible objects in region", "", 
                MeasuresOfInterest.None, StatVerbosity.Info, 
                () => m_activeDestructibleObjects);
                
            RegisterStat("ActiveFragments", "Current number of active fragments in region", "", 
                MeasuresOfInterest.None, StatVerbosity.Info, 
                () => m_activeFragments);
                
            RegisterStat("ChainReactions", "Total number of chain reactions triggered", "", 
                MeasuresOfInterest.AverageChangeOverTime, StatVerbosity.Info, 
                () => m_chainReactions);
                
            // Performance Statistics
            RegisterStat("AverageUpdateTime", "Average update processing time in milliseconds", "ms", 
                MeasuresOfInterest.None, StatVerbosity.Info, 
                () => Math.Round(m_avgUpdateTime, 2));
                
            RegisterStat("MaxUpdateTime", "Maximum update processing time since last reset", "ms", 
                MeasuresOfInterest.None, StatVerbosity.Info, 
                () => Math.Round(m_maxUpdateTime, 2));
                
            RegisterStat("UpdateRate", "Current update rate in Hz", "Hz", 
                MeasuresOfInterest.None, StatVerbosity.Info, 
                () => m_currentUpdateRate);
                
            RegisterStat("SkippedUpdates", "Number of updates skipped due to performance constraints", "", 
                MeasuresOfInterest.AverageChangeOverTime, StatVerbosity.Info, 
                () => m_skippedUpdates);
                
            // Memory Statistics  
            RegisterStat("MemoryUsage", "Estimated memory usage of destruction system", "bytes", 
                MeasuresOfInterest.None, StatVerbosity.Debug, 
                () => m_memoryUsage);
                
            RegisterStat("PooledObjectsInUse", "Number of pooled objects currently in use", "", 
                MeasuresOfInterest.None, StatVerbosity.Debug, 
                () => m_pooledObjectsInUse);
                
            // Configuration Statistics
            RegisterStat("SystemEnabled", "Whether destruction system is currently enabled (1=yes, 0=no)", "", 
                MeasuresOfInterest.None, StatVerbosity.Info, 
                () => m_systemEnabled ? 1 : 0);
                
            RegisterStat("MaxConfiguredObjects", "Maximum number of destructible objects allowed", "", 
                MeasuresOfInterest.None, StatVerbosity.Debug, 
                () => m_maxConfiguredObjects);
                
            // Quality Statistics
            RegisterStat("AdaptiveQualityReductions", "Number of times quality was automatically reduced", "", 
                MeasuresOfInterest.AverageChangeOverTime, StatVerbosity.Info, 
                () => m_adaptiveQualityReductions);
        }
        
        // Update Methods
        public void RecordDestruction()
        {
            lock (m_statLock)
            {
                m_totalDestructions++;
            }
        }
        
        public void RecordFragmentCreation(int count = 1)
        {
            lock (m_statLock)
            {
                m_totalFragments += count;
            }
        }
        
        public void RecordChainReaction()
        {
            lock (m_statLock)
            {
                m_chainReactions++;
            }
        }
        
        public void RecordAutoRepair()
        {
            lock (m_statLock)
            {
                m_autoRepairs++;
            }
        }
        
        public void RecordUpdateTime(double milliseconds)
        {
            lock (m_statLock)
            {
                m_totalUpdateTime += milliseconds;
                m_updateCount++;
                m_avgUpdateTime = m_totalUpdateTime / m_updateCount;
                
                if (milliseconds > m_maxUpdateTime)
                    m_maxUpdateTime = milliseconds;
            }
        }
        
        public void RecordSkippedUpdate()
        {
            lock (m_statLock)
            {
                m_skippedUpdates++;
            }
        }
        
        public void RecordAdaptiveQualityReduction()
        {
            lock (m_statLock)
            {
                m_adaptiveQualityReductions++;
            }
        }
        
        public void UpdateActiveObjectCounts(int destructibleObjects, int fragments)
        {
            lock (m_statLock)
            {
                m_activeDestructibleObjects = destructibleObjects;
                m_activeFragments = fragments;
            }
        }
        
        public void UpdateMemoryUsage(long bytes)
        {
            lock (m_statLock)
            {
                m_memoryUsage = bytes;
            }
        }
        
        public void UpdatePooledObjectCounts(int inUse, int available)
        {
            lock (m_statLock)
            {
                m_pooledObjectsInUse = inUse;
                m_pooledObjectsAvailable = available;
            }
        }
        
        public void UpdateConfiguration(bool enabled, float updateRate, int maxObjects, int maxFragments)
        {
            lock (m_statLock)
            {
                m_systemEnabled = enabled;
                m_currentUpdateRate = updateRate;
                m_maxConfiguredObjects = maxObjects;
                m_maxConfiguredFragments = maxFragments;
            }
        }
        
        public void ResetPerformanceStats()
        {
            lock (m_statLock)
            {
                m_totalUpdateTime = 0;
                m_maxUpdateTime = 0;
                m_avgUpdateTime = 0;
                m_updateCount = 0;
                m_skippedUpdates = 0;
                m_adaptiveQualityReductions = 0;
                m_lastResetTime = DateTime.UtcNow;
            }
        }
        
        // Reporting Methods
        public Dictionary<string, object> GetAllStatistics()
        {
            lock (m_statLock)
            {
                return new Dictionary<string, object>
                {
                    ["TotalDestructions"] = m_totalDestructions,
                    ["TotalFragments"] = m_totalFragments,
                    ["ActiveDestructibleObjects"] = m_activeDestructibleObjects,
                    ["ActiveFragments"] = m_activeFragments,
                    ["ChainReactions"] = m_chainReactions,
                    ["AutoRepairs"] = m_autoRepairs,
                    ["AverageUpdateTime"] = Math.Round(m_avgUpdateTime, 2),
                    ["MaxUpdateTime"] = Math.Round(m_maxUpdateTime, 2),
                    ["CurrentUpdateRate"] = m_currentUpdateRate,
                    ["SkippedUpdates"] = m_skippedUpdates,
                    ["MemoryUsage"] = m_memoryUsage,
                    ["PooledObjectsInUse"] = m_pooledObjectsInUse,
                    ["PooledObjectsAvailable"] = m_pooledObjectsAvailable,
                    ["SystemEnabled"] = m_systemEnabled,
                    ["MaxConfiguredObjects"] = m_maxConfiguredObjects,
                    ["MaxConfiguredFragments"] = m_maxConfiguredFragments,
                    ["AdaptiveQualityReductions"] = m_adaptiveQualityReductions,
                    ["UptimeHours"] = Math.Round((DateTime.UtcNow - m_lastResetTime).TotalHours, 1)
                };
            }
        }
        
        public string GetFormattedReport()
        {
            var stats = GetAllStatistics();
            var report = new System.Text.StringBuilder();
            
            report.AppendLine($"=== Destruction System Statistics ({m_regionName}) ===");
            report.AppendLine($"System Enabled: {stats["SystemEnabled"]}");
            report.AppendLine($"Uptime: {stats["UptimeHours"]} hours");
            report.AppendLine();
            
            report.AppendLine("=== Activity Statistics ===");
            report.AppendLine($"Total Destructions: {stats["TotalDestructions"]}");
            report.AppendLine($"Total Fragments: {stats["TotalFragments"]}");
            report.AppendLine($"Chain Reactions: {stats["ChainReactions"]}");
            report.AppendLine($"Auto Repairs: {stats["AutoRepairs"]}");
            report.AppendLine();
            
            report.AppendLine("=== Current State ===");
            report.AppendLine($"Active Destructible Objects: {stats["ActiveDestructibleObjects"]}/{stats["MaxConfiguredObjects"]}");
            report.AppendLine($"Active Fragments: {stats["ActiveFragments"]}/{stats["MaxConfiguredFragments"]}");
            report.AppendLine($"Update Rate: {stats["CurrentUpdateRate"]} Hz");
            report.AppendLine();
            
            report.AppendLine("=== Performance ===");
            report.AppendLine($"Average Update Time: {stats["AverageUpdateTime"]} ms");
            report.AppendLine($"Max Update Time: {stats["MaxUpdateTime"]} ms");
            report.AppendLine($"Skipped Updates: {stats["SkippedUpdates"]}");
            report.AppendLine($"Quality Reductions: {stats["AdaptiveQualityReductions"]}");
            report.AppendLine();
            
            report.AppendLine("=== Memory Usage ===");
            report.AppendLine($"Estimated Memory: {stats["MemoryUsage"]:N0} bytes");
            report.AppendLine($"Pooled Objects: {stats["PooledObjectsInUse"]} in use, {stats["PooledObjectsAvailable"]} available");
            
            return report.ToString();
        }
        
        public void Dispose()
        {
            if (!m_disposed)
            {
                // Unregister all statistics from StatsManager
                try
                {
                    foreach (var stat in m_registeredStats)
                    {
                        StatsManager.DeregisterStat(stat);
                    }
                    m_registeredStats.Clear();
                }
                catch (Exception)
                {
                    // Ignore errors during disposal
                }
                
                m_disposed = true;
            }
        }
    }
}