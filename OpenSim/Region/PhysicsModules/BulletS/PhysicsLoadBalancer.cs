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
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Load balancing strategy for physics work distribution
    /// </summary>
    public enum LoadBalancingStrategy
    {
        RoundRobin,        // Simple round-robin distribution
        LeastLoaded,       // Assign to thread with lowest load
        WorkStealing,      // Allow threads to steal work from others
        Adaptive,          // Dynamically adjust based on performance
        PriorityBased      // Distribute based on work priority
    }

    /// <summary>
    /// Thread workload metrics for load balancing decisions
    /// </summary>
    public class ThreadWorkloadMetrics
    {
        public int ThreadId { get; set; }
        public int QueuedItems { get; set; }
        public float AverageProcessingTime { get; set; }
        public float Utilization { get; set; }
        public DateTime LastUpdate { get; set; }
        public Dictionary<PhysicsWorkType, int> WorkTypeDistribution { get; set; }
        public bool IsOverloaded { get; set; }
        public bool IsIdle { get; set; }
        public int WorkItemsStolen { get; set; }
        public int WorkItemsGiven { get; set; }

        public ThreadWorkloadMetrics()
        {
            WorkTypeDistribution = new Dictionary<PhysicsWorkType, int>();
            LastUpdate = DateTime.UtcNow;
        }

        /// <summary>
        /// Calculate overall load score for this thread
        /// </summary>
        public float GetLoadScore()
        {
            float queueWeight = QueuedItems * 0.4f;
            float timeWeight = AverageProcessingTime * 0.3f;
            float utilizationWeight = Utilization * 0.3f;
            
            return queueWeight + timeWeight + utilizationWeight;
        }
    }

    /// <summary>
    /// Work stealing request for overloaded threads
    /// </summary>
    public class WorkStealingRequest
    {
        public int RequestingThreadId { get; set; }
        public int TargetThreadId { get; set; }
        public PhysicsWorkType PreferredWorkType { get; set; }
        public int RequestedItems { get; set; }
        public DateTime RequestTime { get; set; }
        public TaskCompletionSource<List<PhysicsWorkItem>> CompletionSource { get; set; }

        public WorkStealingRequest()
        {
            RequestTime = DateTime.UtcNow;
            CompletionSource = new TaskCompletionSource<List<PhysicsWorkItem>>();
        }
    }

    /// <summary>
    /// Advanced physics load balancer for optimal work distribution
    /// </summary>
    public class PhysicsLoadBalancer : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[PHYSICS LOAD BALANCER]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly ModernPhysicsThreadManager m_threadManager;
        private bool m_disposed;
        private bool m_enabled;

        // Load balancing configuration
        private LoadBalancingStrategy m_strategy;
        private readonly float OverloadThreshold = 0.8f;     // 80% utilization triggers rebalancing
        private readonly float IdleThreshold = 0.2f;        // 20% utilization considered idle
        private readonly int MaxWorkStealingItems = 5;      // Maximum items to steal per request
        private readonly TimeSpan RebalancingInterval = TimeSpan.FromMilliseconds(100);

        // Thread monitoring
        private readonly ConcurrentDictionary<int, ThreadWorkloadMetrics> m_threadMetrics;
        private readonly ConcurrentQueue<WorkStealingRequest> m_stealingRequests;
        private readonly Timer m_rebalancingTimer;
        private readonly object m_strategyLock;

        // Work distribution
        private volatile int m_nextThreadIndex;
        private readonly Dictionary<PhysicsWorkType, Queue<int>> m_specializedThreads;
        private readonly List<int> m_availableThreads;

        // Performance tracking
        private long m_totalWorkItemsDistributed;
        private long m_workStealingOperations;
        private float m_averageSystemLoad;
        private DateTime m_lastRebalancing;
        private DateTime m_lastLogTime;

        // Adaptive scaling
        private int m_optimalThreadCount;
        private readonly Dictionary<int, float> m_threadEfficiency;
        private bool m_adaptiveScaling;

        #endregion

        #region Constructor

        public PhysicsLoadBalancer(BSScene scene, ModernPhysicsThreadManager threadManager)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_threadManager = threadManager ?? throw new ArgumentNullException(nameof(threadManager));

            m_threadMetrics = new ConcurrentDictionary<int, ThreadWorkloadMetrics>();
            m_stealingRequests = new ConcurrentQueue<WorkStealingRequest>();
            m_strategyLock = new object();
            m_specializedThreads = new Dictionary<PhysicsWorkType, Queue<int>>();
            m_availableThreads = new List<int>();
            m_threadEfficiency = new Dictionary<int, float>();

            m_strategy = LoadBalancingStrategy.Adaptive;
            m_optimalThreadCount = Math.Max(2, Environment.ProcessorCount / 2);
            m_adaptiveScaling = false; // Disabled by default to prevent excessive scaling
            m_lastRebalancing = DateTime.UtcNow;
            m_lastLogTime = DateTime.UtcNow;

            // Initialize specialized thread queues
            foreach (PhysicsWorkType workType in Enum.GetValues<PhysicsWorkType>())
            {
                m_specializedThreads[workType] = new Queue<int>();
            }

            // Setup rebalancing timer
            m_rebalancingTimer = new Timer(PerformRebalancing, null, RebalancingInterval, RebalancingInterval);

            m_log.InfoFormat("{0}: Physics load balancer initialized with {1} strategy", 
                LogHeader, m_strategy);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the load balancer
        /// </summary>
        public void Initialize()
        {
            if (m_enabled || m_disposed)
                return;

            try
            {
                // Initialize thread metrics for all available threads
                UpdateAvailableThreads();
                
                m_enabled = true;
                m_log.InfoFormat("{0}: Load balancer started with {1} threads", 
                    LogHeader, m_availableThreads.Count);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to initialize load balancer: {1}", LogHeader, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Select the optimal thread for a work item
        /// </summary>
        public int SelectOptimalThread(PhysicsWorkItem workItem)
        {
            if (!m_enabled || m_disposed)
                return GetNextRoundRobinThread();

            lock (m_strategyLock)
            {
                return m_strategy switch
                {
                    LoadBalancingStrategy.RoundRobin => GetNextRoundRobinThread(),
                    LoadBalancingStrategy.LeastLoaded => GetLeastLoadedThread(),
                    LoadBalancingStrategy.WorkStealing => GetOptimalThreadWithStealing(workItem),
                    LoadBalancingStrategy.Adaptive => GetAdaptiveThread(workItem),
                    LoadBalancingStrategy.PriorityBased => GetPriorityBasedThread(workItem),
                    _ => GetNextRoundRobinThread()
                };
            }
        }

        /// <summary>
        /// Update thread workload metrics
        /// </summary>
        public void UpdateThreadMetrics(int threadId, PhysicsThreadStats stats)
        {
            if (!m_enabled || m_disposed)
                return;

            var metrics = m_threadMetrics.GetOrAdd(threadId, _ => new ThreadWorkloadMetrics { ThreadId = threadId });
            
            metrics.QueuedItems = stats.WorkItemsQueued;
            metrics.AverageProcessingTime = stats.AverageProcessingTime;
            metrics.Utilization = stats.ThreadUtilization;
            metrics.WorkTypeDistribution = new Dictionary<PhysicsWorkType, int>(stats.WorkTypeCount);
            metrics.IsOverloaded = metrics.Utilization > OverloadThreshold;
            metrics.IsIdle = metrics.Utilization < IdleThreshold;
            metrics.LastUpdate = DateTime.UtcNow;

            // Update thread efficiency tracking
            m_threadEfficiency[threadId] = CalculateThreadEfficiency(metrics);

            Interlocked.Increment(ref m_totalWorkItemsDistributed);
        }

        /// <summary>
        /// Request work stealing from overloaded threads
        /// </summary>
        public async Task<List<PhysicsWorkItem>> RequestWorkStealingAsync(int requestingThreadId, PhysicsWorkType workType)
        {
            if (!m_enabled || m_disposed)
                return new List<PhysicsWorkItem>();

            var overloadedThreads = m_threadMetrics.Values
                .Where(m => m.IsOverloaded && m.ThreadId != requestingThreadId)
                .OrderByDescending(m => m.GetLoadScore())
                .Take(3)
                .ToList();

            if (!overloadedThreads.Any())
                return new List<PhysicsWorkItem>();

            var request = new WorkStealingRequest
            {
                RequestingThreadId = requestingThreadId,
                TargetThreadId = overloadedThreads.First().ThreadId,
                PreferredWorkType = workType,
                RequestedItems = Math.Min(MaxWorkStealingItems, overloadedThreads.First().QueuedItems / 2)
            };

            m_stealingRequests.Enqueue(request);
            Interlocked.Increment(ref m_workStealingOperations);

            try
            {
                return await request.CompletionSource.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Work stealing request failed: {1}", LogHeader, ex.Message);
                return new List<PhysicsWorkItem>();
            }
        }

        /// <summary>
        /// Change load balancing strategy
        /// </summary>
        public void SetLoadBalancingStrategy(LoadBalancingStrategy strategy)
        {
            lock (m_strategyLock)
            {
                if (m_strategy != strategy)
                {
                    m_strategy = strategy;
                    m_log.InfoFormat("{0}: Load balancing strategy changed to {1}", LogHeader, strategy);
                    
                    // Reset metrics when changing strategy
                    ResetMetrics();
                }
            }
        }

        /// <summary>
        /// Get current load balancing statistics
        /// </summary>
        public string GetLoadBalancingReport()
        {
            if (!m_enabled || m_disposed)
                return "Load balancer not active";

            var report = $"Physics Load Balancer Report:\\n";
            report += $"  Strategy: {m_strategy}\\n";
            report += $"  Total Work Items: {m_totalWorkItemsDistributed}\\n";
            report += $"  Work Stealing Operations: {m_workStealingOperations}\\n";
            report += $"  Average System Load: {m_averageSystemLoad:F2}%\\n";
            report += $"  Optimal Thread Count: {m_optimalThreadCount}\\n";
            report += $"  Available Threads: {m_availableThreads.Count}\\n";

            report += $"\\nThread Metrics:\\n";
            foreach (var metric in m_threadMetrics.Values.OrderBy(m => m.ThreadId))
            {
                var efficiency = m_threadEfficiency.GetValueOrDefault(metric.ThreadId, 0f);
                report += $"  Thread {metric.ThreadId}: Load={metric.GetLoadScore():F1}, " +
                         $"Queue={metric.QueuedItems}, Util={metric.Utilization:F1}%, " +
                         $"Efficiency={efficiency:F2}\\n";
            }

            return report;
        }

        #endregion

        #region Private Methods

        private void UpdateAvailableThreads()
        {
            m_availableThreads.Clear();
            var threadStats = m_threadManager.GetThreadStatistics();
            
            foreach (var stats in threadStats)
            {
                m_availableThreads.Add(stats.ThreadId);
                
                if (!m_threadMetrics.ContainsKey(stats.ThreadId))
                {
                    m_threadMetrics[stats.ThreadId] = new ThreadWorkloadMetrics 
                    { 
                        ThreadId = stats.ThreadId 
                    };
                }
            }

            // Only log thread updates every 30 seconds to prevent spam
            if (DateTime.UtcNow - m_lastLogTime > TimeSpan.FromSeconds(30))
            {
                m_log.DebugFormat("{0}: Updated available threads: {1}", 
                    LogHeader, string.Join(", ", m_availableThreads));
                m_lastLogTime = DateTime.UtcNow;
            }
        }

        private int GetNextRoundRobinThread()
        {
            if (m_availableThreads.Count == 0)
                return Thread.CurrentThread.ManagedThreadId;

            int index = Interlocked.Increment(ref m_nextThreadIndex) % m_availableThreads.Count;
            return m_availableThreads[index];
        }

        private int GetLeastLoadedThread()
        {
            if (m_availableThreads.Count == 0)
                return Thread.CurrentThread.ManagedThreadId;

            var leastLoaded = m_threadMetrics.Values
                .Where(m => m_availableThreads.Contains(m.ThreadId))
                .OrderBy(m => m.GetLoadScore())
                .FirstOrDefault();

            return leastLoaded?.ThreadId ?? m_availableThreads[0];
        }

        private int GetOptimalThreadWithStealing(PhysicsWorkItem workItem)
        {
            // First try to find a non-overloaded thread
            var availableThread = m_threadMetrics.Values
                .Where(m => m_availableThreads.Contains(m.ThreadId) && !m.IsOverloaded)
                .OrderBy(m => m.GetLoadScore())
                .FirstOrDefault();

            if (availableThread != null)
                return availableThread.ThreadId;

            // If all threads are overloaded, trigger work stealing
            TriggerWorkStealing();
            
            // Return least loaded thread as fallback
            return GetLeastLoadedThread();
        }

        private int GetAdaptiveThread(PhysicsWorkItem workItem)
        {
            // Use efficiency-based selection for adaptive strategy
            var bestThread = m_threadMetrics.Values
                .Where(m => m_availableThreads.Contains(m.ThreadId))
                .OrderByDescending(m => m_threadEfficiency.GetValueOrDefault(m.ThreadId, 0.5f))
                .ThenBy(m => m.GetLoadScore())
                .FirstOrDefault();

            return bestThread?.ThreadId ?? GetLeastLoadedThread();
        }

        private int GetPriorityBasedThread(PhysicsWorkItem workItem)
        {
            // Check if we have specialized threads for this work type
            if (m_specializedThreads.TryGetValue(workItem.WorkType, out Queue<int> specializedQueue) && 
                specializedQueue.Count > 0)
            {
                return specializedQueue.Dequeue();
            }

            // For high-priority work, prefer less loaded threads
            if (workItem.Priority > 0.8f)
            {
                return GetLeastLoadedThread();
            }

            // For normal priority, use round-robin
            return GetNextRoundRobinThread();
        }

        private float CalculateThreadEfficiency(ThreadWorkloadMetrics metrics)
        {
            // Efficiency based on utilization vs processing time
            if (metrics.AverageProcessingTime <= 0)
                return 0.5f; // Default efficiency

            float utilizationFactor = Math.Min(1.0f, metrics.Utilization / 100f);
            float speedFactor = Math.Max(0.1f, 1.0f / (metrics.AverageProcessingTime + 1));
            float queueFactor = Math.Max(0.1f, 1.0f / (metrics.QueuedItems + 1));

            return (utilizationFactor * 0.4f + speedFactor * 0.4f + queueFactor * 0.2f);
        }

        private void TriggerWorkStealing()
        {
            Task.Run(async () =>
            {
                var idleThreads = m_threadMetrics.Values.Where(m => m.IsIdle).ToList();
                var overloadedThreads = m_threadMetrics.Values.Where(m => m.IsOverloaded).ToList();

                foreach (var idleThread in idleThreads.Take(2)) // Limit concurrent stealing
                {
                    var targetThread = overloadedThreads.OrderByDescending(m => m.GetLoadScore()).FirstOrDefault();
                    if (targetThread != null)
                    {
                        await RequestWorkStealingAsync(idleThread.ThreadId, PhysicsWorkType.RigidBodyUpdate);
                    }
                }
            });
        }

        private void PerformRebalancing(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                DateTime now = DateTime.UtcNow;
                if (now - m_lastRebalancing < RebalancingInterval)
                    return;

                // Update system-wide metrics
                UpdateSystemMetrics();

                // Process work stealing requests
                ProcessWorkStealingRequests();

                // Perform adaptive scaling if enabled
                if (m_adaptiveScaling)
                {
                    PerformAdaptiveScaling();
                }

                m_lastRebalancing = now;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error during rebalancing: {1}", LogHeader, ex.Message);
            }
        }

        private void UpdateSystemMetrics()
        {
            if (m_threadMetrics.IsEmpty)
                return;

            float totalLoad = m_threadMetrics.Values.Sum(m => m.Utilization);
            m_averageSystemLoad = totalLoad / m_threadMetrics.Count;

            // Update thread availability
            UpdateAvailableThreads();
        }

        private void ProcessWorkStealingRequests()
        {
            int processedRequests = 0;
            const int maxRequestsPerCycle = 5;

            while (processedRequests < maxRequestsPerCycle && 
                   m_stealingRequests.TryDequeue(out WorkStealingRequest request))
            {
                try
                {
                    // Simulate work stealing (in real implementation, this would coordinate with thread manager)
                    var stolenWork = new List<PhysicsWorkItem>();
                    
                    if (m_threadMetrics.TryGetValue(request.TargetThreadId, out ThreadWorkloadMetrics targetMetrics) &&
                        targetMetrics.IsOverloaded)
                    {
                        // Mark successful stealing operation
                        if (m_threadMetrics.TryGetValue(request.RequestingThreadId, out ThreadWorkloadMetrics requestingMetrics))
                        {
                            requestingMetrics.WorkItemsStolen += request.RequestedItems;
                        }
                        
                        targetMetrics.WorkItemsGiven += request.RequestedItems;
                    }

                    request.CompletionSource.SetResult(stolenWork);
                    processedRequests++;
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("{0}: Error processing work stealing request: {1}", LogHeader, ex.Message);
                    request.CompletionSource.SetException(ex);
                }
            }
        }

        private void PerformAdaptiveScaling()
        {
            // Analyze whether we need more or fewer threads
            float averageEfficiency = m_threadEfficiency.Values.DefaultIfEmpty(0.5f).Average();
            
            if (m_averageSystemLoad > 90f && averageEfficiency > 0.7f)
            {
                // System is highly loaded but efficient - consider adding threads
                if (m_optimalThreadCount < Environment.ProcessorCount)
                {
                    m_optimalThreadCount++;
                    // Only log scaling changes every 10 seconds to prevent spam
                    if (DateTime.UtcNow - m_lastLogTime > TimeSpan.FromSeconds(10))
                    {
                        m_log.InfoFormat("{0}: Adaptive scaling increased optimal thread count to {1}", 
                            LogHeader, m_optimalThreadCount);
                        m_lastLogTime = DateTime.UtcNow;
                    }
                }
            }
            else if (m_averageSystemLoad < 30f && m_optimalThreadCount > 2)
            {
                // System is lightly loaded - consider reducing threads
                m_optimalThreadCount--;
                // Only log scaling changes every 10 seconds to prevent spam
                if (DateTime.UtcNow - m_lastLogTime > TimeSpan.FromSeconds(10))
                {
                    m_log.InfoFormat("{0}: Adaptive scaling reduced optimal thread count to {1}", 
                        LogHeader, m_optimalThreadCount);
                    m_lastLogTime = DateTime.UtcNow;
                }
            }
        }

        private void ResetMetrics()
        {
            m_threadMetrics.Clear();
            m_threadEfficiency.Clear();
            m_totalWorkItemsDistributed = 0;
            m_workStealingOperations = 0;
            m_averageSystemLoad = 0;
            
            UpdateAvailableThreads();
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
                m_rebalancingTimer?.Dispose();

                // Complete any pending work stealing requests
                while (m_stealingRequests.TryDequeue(out WorkStealingRequest request))
                {
                    request.CompletionSource.SetCanceled();
                }

                m_disposed = true;
                m_log.InfoFormat("{0}: Physics load balancer disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public LoadBalancingStrategy Strategy => m_strategy;
        public float AverageSystemLoad => m_averageSystemLoad;
        public int OptimalThreadCount => m_optimalThreadCount;
        public long TotalWorkItemsDistributed => m_totalWorkItemsDistributed;
        public long WorkStealingOperations => m_workStealingOperations;

        #endregion
    }
}