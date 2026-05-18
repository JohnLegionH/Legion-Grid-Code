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
 *       derived from this software without specific written permission.
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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Work item types for physics thread processing
    /// </summary>
    public enum PhysicsWorkType
    {
        CollisionDetection,
        RigidBodyUpdate,
        CharacterUpdate,
        VehicleUpdate,
        ConstraintSolving,
        SpatialIndexUpdate,
        ForceApplication,
        PostStepProcessing
    }

    /// <summary>
    /// Represents a unit of physics work that can be executed on a thread
    /// </summary>
    public class PhysicsWorkItem
    {
        public uint ObjectID { get; set; }
        public PhysicsWorkType WorkType { get; set; }
        public object WorkData { get; set; }
        public float Priority { get; set; }
        public DateTime SubmittedAt { get; set; }
        public TaskCompletionSource<bool> CompletionSource { get; set; }

        public PhysicsWorkItem()
        {
            SubmittedAt = DateTime.UtcNow;
            CompletionSource = new TaskCompletionSource<bool>();
            Priority = 1.0f;
        }
    }

    /// <summary>
    /// Physics thread performance statistics
    /// </summary>
    public class PhysicsThreadStats
    {
        public int ThreadId { get; set; }
        public int WorkItemsProcessed { get; set; }
        public int WorkItemsQueued { get; set; }
        public float AverageProcessingTime { get; set; }
        public float ThreadUtilization { get; set; }
        public DateTime LastUpdate { get; set; }
        public Dictionary<PhysicsWorkType, int> WorkTypeCount { get; set; }

        public PhysicsThreadStats()
        {
            WorkTypeCount = new Dictionary<PhysicsWorkType, int>();
            LastUpdate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Modern multithreaded physics pipeline manager
    /// Coordinates physics work across multiple threads for optimal performance
    /// </summary>
    public class ModernPhysicsThreadManager : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MODERN PHYSICS THREAD MANAGER]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly int m_maxThreads;
        private readonly int m_maxQueueSize;
        private bool m_disposed;
        private bool m_enabled;

        // Threading infrastructure
        private readonly ConcurrentQueue<PhysicsWorkItem> m_workQueue;
        private readonly ConcurrentDictionary<int, PhysicsThreadStats> m_threadStats;
        private readonly List<Thread> m_workerThreads;
        private readonly CancellationTokenSource m_cancellationTokenSource;
        private readonly SemaphoreSlim m_workAvailableSemaphore;
        private readonly object m_shutdownLock;

        // Load balancing
        private readonly ConcurrentDictionary<PhysicsWorkType, int> m_workTypeAffinity;
        private volatile int m_nextThreadIndex;
        private readonly float[] m_threadLoadFactors;

        // Performance monitoring
        private readonly Stopwatch m_performanceTimer;
        private DateTime m_lastStatsUpdate;
        private readonly TimeSpan StatsUpdateInterval = TimeSpan.FromSeconds(5);

        // Work distribution strategy
        private Dictionary<PhysicsWorkType, float> m_workTypePriorities;
        
        // Load balancing
        private PhysicsLoadBalancer m_loadBalancer;
        
        // Object pooling
        private PhysicsObjectPoolManager m_poolManager;

        #endregion

        #region Constructor

        public ModernPhysicsThreadManager(BSScene scene, int maxThreads = 0, int maxQueueSize = 10000)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_maxThreads = maxThreads > 0 ? maxThreads : Math.Max(2, Environment.ProcessorCount - 1);
            m_maxQueueSize = maxQueueSize;

            m_workQueue = new ConcurrentQueue<PhysicsWorkItem>();
            m_threadStats = new ConcurrentDictionary<int, PhysicsThreadStats>();
            m_workerThreads = new List<Thread>();
            m_cancellationTokenSource = new CancellationTokenSource();
            m_workAvailableSemaphore = new SemaphoreSlim(0, m_maxQueueSize);
            m_shutdownLock = new object();

            m_workTypeAffinity = new ConcurrentDictionary<PhysicsWorkType, int>();
            m_threadLoadFactors = new float[m_maxThreads];
            m_performanceTimer = new Stopwatch();
            m_lastStatsUpdate = DateTime.UtcNow;

            InitializeWorkTypePriorities();
            
            // Initialize load balancer
            m_loadBalancer = new PhysicsLoadBalancer(m_scene, this);
            
            // Initialize object pool manager
            m_poolManager = new PhysicsObjectPoolManager(m_scene);
            
            m_enabled = false;
            
            m_log.InfoFormat("{0}: Physics thread manager created with {1} threads, queue size {2}", 
                LogHeader, m_maxThreads, m_maxQueueSize);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Start the physics thread manager
        /// </summary>
        public void Start()
        {
            if (m_disposed || m_enabled)
                return;

            try
            {
                lock (m_shutdownLock)
                {
                    if (m_enabled)
                        return;

                    // Create and start worker threads
                    for (int i = 0; i < m_maxThreads; i++)
                    {
                        var thread = new Thread(WorkerThreadMain)
                        {
                            Name = $"PhysicsWorker_{i}",
                            IsBackground = true
                        };

                        m_threadStats[thread.ManagedThreadId] = new PhysicsThreadStats
                        {
                            ThreadId = thread.ManagedThreadId
                        };

                        m_workerThreads.Add(thread);
                        thread.Start(i);
                    }

                    m_enabled = true;
                    m_performanceTimer.Start();
                    
                    // Initialize load balancer
                    m_loadBalancer.Initialize();

                    m_log.InfoFormat("{0}: Started {1} physics worker threads", LogHeader, m_maxThreads);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to start physics threads: {1}", LogHeader, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Stop the physics thread manager
        /// </summary>
        public void Stop()
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                lock (m_shutdownLock)
                {
                    if (!m_enabled)
                        return;

                    m_enabled = false;
                    m_cancellationTokenSource.Cancel();

                    // Release semaphore to wake up waiting threads
                    for (int i = 0; i < m_maxThreads; i++)
                    {
                        m_workAvailableSemaphore.Release();
                    }

                    // Wait for threads to finish
                    foreach (var thread in m_workerThreads)
                    {
                        if (thread.IsAlive)
                        {
                            thread.Join(5000); // 5 second timeout
                        }
                    }

                    m_workerThreads.Clear();
                    m_performanceTimer.Stop();

                    m_log.InfoFormat("{0}: Stopped physics thread manager", LogHeader);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error stopping physics threads: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Submit work item for physics processing
        /// </summary>
        public async Task<bool> SubmitWorkAsync(PhysicsWorkItem workItem)
        {
            if (!m_enabled || m_disposed || workItem == null)
                return false;

            try
            {
                // Check queue size limit
                if (m_workQueue.Count >= m_maxQueueSize)
                {
                    m_log.WarnFormat("{0}: Work queue full ({1} items), dropping work item for object {2}", 
                        LogHeader, m_workQueue.Count, workItem.ObjectID);
                    return false;
                }

                // Set priority based on work type
                if (m_workTypePriorities.ContainsKey(workItem.WorkType))
                {
                    workItem.Priority = m_workTypePriorities[workItem.WorkType];
                }

                m_workQueue.Enqueue(workItem);
                m_workAvailableSemaphore.Release();

                return await workItem.CompletionSource.Task;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error submitting work: {1}", LogHeader, ex.Message);
                workItem.CompletionSource.SetResult(false);
                return false;
            }
        }

        /// <summary>
        /// Submit work item synchronously
        /// </summary>
        public bool SubmitWork(PhysicsWorkItem workItem)
        {
            if (!m_enabled || m_disposed || workItem == null)
                return false;

            try
            {
                if (m_workQueue.Count >= m_maxQueueSize)
                    return false;

                if (m_workTypePriorities.ContainsKey(workItem.WorkType))
                {
                    workItem.Priority = m_workTypePriorities[workItem.WorkType];
                }

                m_workQueue.Enqueue(workItem);
                m_workAvailableSemaphore.Release();
                return true;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error submitting work: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Get current performance statistics
        /// </summary>
        public List<PhysicsThreadStats> GetThreadStatistics()
        {
            UpdateStatistics();
            return m_threadStats.Values.ToList();
        }

        /// <summary>
        /// Get work queue status
        /// </summary>
        public (int QueuedItems, int MaxQueueSize, float LoadPercentage) GetQueueStatus()
        {
            int queued = m_workQueue.Count;
            float loadPercentage = (float)queued / m_maxQueueSize * 100f;
            return (queued, m_maxQueueSize, loadPercentage);
        }

        /// <summary>
        /// Set load balancing strategy
        /// </summary>
        public void SetLoadBalancingStrategy(LoadBalancingStrategy strategy)
        {
            m_loadBalancer?.SetLoadBalancingStrategy(strategy);
        }

        /// <summary>
        /// Get load balancing performance report
        /// </summary>
        public string GetLoadBalancingReport()
        {
            return m_loadBalancer?.GetLoadBalancingReport() ?? "Load balancer not available";
        }

        /// <summary>
        /// Request work stealing from overloaded threads
        /// </summary>
        public async Task<List<PhysicsWorkItem>> RequestWorkStealingAsync(int threadId, PhysicsWorkType workType)
        {
            if (m_loadBalancer != null)
                return await m_loadBalancer.RequestWorkStealingAsync(threadId, workType);
            
            return new List<PhysicsWorkItem>();
        }

        /// <summary>
        /// Create a pooled work item for physics processing
        /// </summary>
        public PhysicsWorkItem CreatePooledWorkItem(uint objectID, PhysicsWorkType workType, object workData = null)
        {
            var workItem = m_poolManager.RentWorkItem();
            workItem.ObjectID = objectID;
            workItem.WorkType = workType;
            workItem.WorkData = workData;
            workItem.SubmittedAt = DateTime.UtcNow;
            workItem.CompletionSource = new TaskCompletionSource<bool>();
            
            // Set priority based on work type
            if (m_workTypePriorities.ContainsKey(workType))
            {
                workItem.Priority = m_workTypePriorities[workType];
            }
            
            return workItem;
        }

        /// <summary>
        /// Submit pooled work item for physics processing
        /// </summary>
        public async Task<bool> SubmitPooledWorkAsync(uint objectID, PhysicsWorkType workType, object workData = null)
        {
            if (!m_enabled || m_disposed)
                return false;

            var workItem = CreatePooledWorkItem(objectID, workType, workData);
            
            try
            {
                return await SubmitWorkAsync(workItem);
            }
            finally
            {
                // Return work item to pool after completion
                m_poolManager.ReturnWorkItem(workItem);
            }
        }

        /// <summary>
        /// Get object pool manager statistics
        /// </summary>
        public string GetPoolStatistics()
        {
            return m_poolManager?.GetPoolStatistics() ?? "Pool manager not available";
        }

        #endregion

        #region Private Methods

        private void InitializeWorkTypePriorities()
        {
            m_workTypePriorities = new Dictionary<PhysicsWorkType, float>
            {
                { PhysicsWorkType.CharacterUpdate, 1.0f },      // Highest priority for avatars
                { PhysicsWorkType.CollisionDetection, 0.9f },   // Critical for safety
                { PhysicsWorkType.VehicleUpdate, 0.8f },        // Important for vehicles
                { PhysicsWorkType.RigidBodyUpdate, 0.7f },      // Standard objects
                { PhysicsWorkType.ConstraintSolving, 0.6f },    // Physics constraints
                { PhysicsWorkType.ForceApplication, 0.5f },     // Force/impulse applications
                { PhysicsWorkType.SpatialIndexUpdate, 0.4f },   // Spatial optimization
                { PhysicsWorkType.PostStepProcessing, 0.3f }    // Cleanup work
            };
        }

        private void WorkerThreadMain(object threadIndexObj)
        {
            int threadIndex = (int)threadIndexObj;
            int threadId = Thread.CurrentThread.ManagedThreadId;
            
            m_log.InfoFormat("{0}: Worker thread {1} (index {2}) started", LogHeader, threadId, threadIndex);

            try
            {
                while (!m_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Wait for work to become available
                        m_workAvailableSemaphore.Wait(m_cancellationTokenSource.Token);

                        if (m_cancellationTokenSource.Token.IsCancellationRequested)
                            break;

                        // Try to get work from queue
                        if (m_workQueue.TryDequeue(out PhysicsWorkItem workItem))
                        {
                            ProcessWorkItem(workItem, threadId);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        m_log.ErrorFormat("{0}: Error in worker thread {1}: {2}", LogHeader, threadId, ex.Message);
                    }
                }
            }
            finally
            {
                m_log.InfoFormat("{0}: Worker thread {1} (index {2}) stopping", LogHeader, threadId, threadIndex);
            }
        }

        private void ProcessWorkItem(PhysicsWorkItem workItem, int threadId)
        {
            var stopwatch = Stopwatch.StartNew();
            bool success = false;

            try
            {
                success = ExecuteWorkItem(workItem);
                
                // Update thread statistics
                if (m_threadStats.TryGetValue(threadId, out PhysicsThreadStats stats))
                {
                    stats.WorkItemsProcessed++;
                    
                    if (!stats.WorkTypeCount.ContainsKey(workItem.WorkType))
                        stats.WorkTypeCount[workItem.WorkType] = 0;
                    stats.WorkTypeCount[workItem.WorkType]++;

                    // Update average processing time
                    float processingTime = (float)stopwatch.Elapsed.TotalMilliseconds;
                    float alpha = 0.1f; // Smoothing factor
                    stats.AverageProcessingTime = stats.AverageProcessingTime * (1.0f - alpha) + processingTime * alpha;
                    stats.LastUpdate = DateTime.UtcNow;
                    
                    // Update load balancer metrics
                    m_loadBalancer?.UpdateThreadMetrics(threadId, stats);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing work item {1} for object {2}: {3}", 
                    LogHeader, workItem.WorkType, workItem.ObjectID, ex.Message);
                success = false;
            }
            finally
            {
                stopwatch.Stop();
                workItem.CompletionSource.SetResult(success);
            }
        }

        private bool ExecuteWorkItem(PhysicsWorkItem workItem)
        {
            switch (workItem.WorkType)
            {
                case PhysicsWorkType.CollisionDetection:
                    return ProcessCollisionDetection(workItem);
                
                case PhysicsWorkType.RigidBodyUpdate:
                    return ProcessRigidBodyUpdate(workItem);
                
                case PhysicsWorkType.CharacterUpdate:
                    return ProcessCharacterUpdate(workItem);
                
                case PhysicsWorkType.VehicleUpdate:
                    return ProcessVehicleUpdate(workItem);
                
                case PhysicsWorkType.ConstraintSolving:
                    return ProcessConstraintSolving(workItem);
                
                case PhysicsWorkType.SpatialIndexUpdate:
                    return ProcessSpatialIndexUpdate(workItem);
                
                case PhysicsWorkType.ForceApplication:
                    return ProcessForceApplication(workItem);
                
                case PhysicsWorkType.PostStepProcessing:
                    return ProcessPostStepProcessing(workItem);
                
                default:
                    m_log.WarnFormat("{0}: Unknown work type: {1}", LogHeader, workItem.WorkType);
                    return false;
            }
        }

        private bool ProcessCollisionDetection(PhysicsWorkItem workItem)
        {
            try
            {
                if (workItem.WorkData is List<BSPhysObject> objectBatch)
                {
                    // Process collision detection for a batch of objects
                    int collisionsProcessed = 0;
                    
                    foreach (var physObj in objectBatch)
                    {
                        if (physObj.PhysBody.HasPhysicalBody)
                        {
                            // Simplified collision processing - just ensure objects are responsive
                            m_scene.PE.Activate(physObj.PhysBody, true);
                            
                            // Check if object is moving fast enough to warrant collision monitoring
                            OMV.Vector3 velocity = m_scene.PE.GetLinearVelocity(physObj.PhysBody);
                            if (velocity.LengthSquared() > 0.01f)
                            {
                                // Object is moving, ensure collision detection is active
                                collisionsProcessed++;
                            }
                        }
                    }
                    
                    return collisionsProcessed > 0;
                }
                return false;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing collision detection: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        private bool ProcessRigidBodyUpdate(PhysicsWorkItem workItem)
        {
            try
            {
                if (workItem.WorkData is BSPhysObject physObj && physObj is BSPrimLinkable prim)
                {
                    // Update rigid body physics state
                    if (physObj.PhysBody.HasPhysicalBody)
                    {
                        // Simple rigid body update - just ensure the object is active
                        m_scene.PE.Activate(physObj.PhysBody, true);
                        
                        // Update object position and rotation for interpolation  
                        OMV.Vector3 currentPos = m_scene.PE.GetPosition(physObj.PhysBody);
                        OMV.Quaternion currentRot = m_scene.PE.GetOrientation(physObj.PhysBody);
                        
                        // Only update if significantly different to avoid unnecessary work
                        if ((currentPos - physObj.RawPosition).LengthSquared() > 0.001f ||
                            Math.Abs(OMV.Quaternion.Dot(currentRot, physObj.RawOrientation)) < 0.999f)
                        {
                            physObj.ForcePosition = currentPos;
                            physObj.ForceOrientation = currentRot;
                        }
                        
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing rigid body update for object {1}: {2}", 
                    LogHeader, workItem.ObjectID, ex.Message);
                return false;
            }
        }

        private bool ProcessCharacterUpdate(PhysicsWorkItem workItem)
        {
            try
            {
                if (workItem.WorkData is BSCharacter character)
                {
                    // Update character controller state
                    if (character.PhysBody.HasPhysicalBody)
                    {
                        // Simple character update - ensure character is active and responsive
                        m_scene.PE.Activate(character.PhysBody, true);
                        
                        // Update character position for avatar tracking
                        OMV.Vector3 currentPos = m_scene.PE.GetPosition(character.PhysBody);
                        if ((currentPos - character.RawPosition).LengthSquared() > 0.001f)
                        {
                            character.ForcePosition = currentPos;
                        }
                        
                        // Ensure character stays upright (basic stability)
                        OMV.Quaternion currentRot = m_scene.PE.GetOrientation(character.PhysBody);
                        OMV.Vector3 up = OMV.Vector3.UnitZ;
                        OMV.Vector3 characterUp = up * currentRot;
                        
                        // If character is too tilted, apply corrective torque
                        if (OMV.Vector3.Dot(characterUp, up) < 0.8f)
                        {
                            OMV.Vector3 correctionTorque = OMV.Vector3.Cross(characterUp, up) * 10.0f;
                            m_scene.PE.ApplyTorque(character.PhysBody, correctionTorque);
                        }
                        
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing character update for object {1}: {2}", 
                    LogHeader, workItem.ObjectID, ex.Message);
                return false;
            }
        }

        private bool ProcessVehicleUpdate(PhysicsWorkItem workItem)
        {
            try
            {
                if (workItem.WorkData is BSPhysObject physObj && physObj is BSPrimLinkable prim)
                {
                    // Get the vehicle actor for this primitive
                    BSDynamics vehicleActor = prim.GetVehicleActor(false);
                    if (vehicleActor != null && physObj.PhysBody.HasPhysicalBody)
                    {
                        // Vehicle physics update - simplified for threading
                        // The actual vehicle step will be handled by the main physics loop
                        // Here we just ensure the vehicle object is active
                        m_scene.PE.Activate(physObj.PhysBody, true);
                        
                        // Update vehicle position tracking
                        OMV.Vector3 currentPos = m_scene.PE.GetPosition(physObj.PhysBody);
                        OMV.Quaternion currentRot = m_scene.PE.GetOrientation(physObj.PhysBody);
                        
                        // Update position if significantly changed
                        if ((currentPos - physObj.RawPosition).LengthSquared() > 0.001f)
                        {
                            physObj.ForcePosition = currentPos;
                        }
                        
                        if (Math.Abs(OMV.Quaternion.Dot(currentRot, physObj.RawOrientation)) < 0.999f)
                        {
                            physObj.ForceOrientation = currentRot;
                        }
                        
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing vehicle update for object {1}: {2}", 
                    LogHeader, workItem.ObjectID, ex.Message);
                return false;
            }
        }

        private bool ProcessConstraintSolving(PhysicsWorkItem workItem)
        {
            try
            {
                // Simplified constraint processing
                // In the current implementation, constraints are handled by the main physics engine
                // This thread work would be for additional constraint processing or custom constraints
                
                if (workItem.WorkData is BSPhysObject physObj && physObj.PhysBody.HasPhysicalBody)
                {
                    // Ensure constrained objects remain active
                    m_scene.PE.Activate(physObj.PhysBody, true);
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing constraint solving for object {1}: {2}", 
                    LogHeader, workItem.ObjectID, ex.Message);
                return false;
            }
        }

        private bool ProcessSpatialIndexUpdate(PhysicsWorkItem workItem)
        {
            // Placeholder for spatial index update work
            // In full implementation, this would update spatial indexing structures
            return true;
        }

        private bool ProcessForceApplication(PhysicsWorkItem workItem)
        {
            try
            {
                if (workItem.WorkData is BSPhysObject physObj && physObj.PhysBody.HasPhysicalBody)
                {
                    // Simple force application - just ensure object is active for forces to take effect
                    m_scene.PE.Activate(physObj.PhysBody, true);
                    
                    // Apply a small wake-up force to ensure objects respond to physics
                    OMV.Vector3 velocity = m_scene.PE.GetLinearVelocity(physObj.PhysBody);
                    if (velocity.LengthSquared() < 0.01f)
                    {
                        // Apply tiny force to keep object responsive
                        OMV.Vector3 wakeForce = new OMV.Vector3(0, 0, 0.001f);
                        m_scene.PE.ApplyCentralForce(physObj.PhysBody, wakeForce);
                    }
                    
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing force application for object {1}: {2}", 
                    LogHeader, workItem.ObjectID, ex.Message);
                return false;
            }
        }

        private bool ProcessPostStepProcessing(PhysicsWorkItem workItem)
        {
            try
            {
                if (workItem.WorkData is BSPhysObject physObj)
                {
                    // Perform post-step cleanup and state updates
                    if (physObj.PhysBody.HasPhysicalBody)
                    {
                        // Update object state from physics engine
                        OMV.Vector3 newPosition = m_scene.PE.GetPosition(physObj.PhysBody);
                        OMV.Quaternion newRotation = m_scene.PE.GetOrientation(physObj.PhysBody);
                        OMV.Vector3 newVelocity = m_scene.PE.GetLinearVelocity(physObj.PhysBody);
                        
                        // Check for significant changes that require updates
                        bool positionChanged = (newPosition - physObj.RawPosition).LengthSquared() > 0.001f;
                        bool rotationChanged = Math.Abs(OMV.Quaternion.Dot(newRotation, physObj.RawOrientation)) < 0.999f;
                        bool velocityChanged = (newVelocity - physObj.RawVelocity).LengthSquared() > 0.001f;
                        
                        if (positionChanged || rotationChanged || velocityChanged)
                        {
                            // Queue update for main thread
                            physObj.RequestPhysicsterseUpdate();
                        }
                        
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing post-step for object {1}: {2}", 
                    LogHeader, workItem.ObjectID, ex.Message);
                return false;
            }
        }

        private void UpdateStatistics()
        {
            DateTime now = DateTime.UtcNow;
            if (now - m_lastStatsUpdate < StatsUpdateInterval)
                return;

            try
            {
                foreach (var stats in m_threadStats.Values)
                {
                    stats.WorkItemsQueued = m_workQueue.Count;
                    // Calculate thread utilization based on work processed vs time elapsed
                    var timeSinceLastUpdate = (now - stats.LastUpdate).TotalSeconds;
                    if (timeSinceLastUpdate > 0)
                    {
                        stats.ThreadUtilization = Math.Min(100f, 
                            (stats.WorkItemsProcessed / (float)timeSinceLastUpdate) * 100f);
                    }
                }

                m_lastStatsUpdate = now;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error updating statistics: {1}", LogHeader, ex.Message);
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
                Stop();

                m_loadBalancer?.Dispose();
                m_poolManager?.Dispose();
                m_cancellationTokenSource?.Dispose();
                m_workAvailableSemaphore?.Dispose();
                m_performanceTimer?.Stop();

                m_disposed = true;
                m_log.InfoFormat("{0}: Physics thread manager disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public int ThreadCount => m_maxThreads;
        public int QueueSize => m_workQueue.Count;
        public int MaxQueueSize => m_maxQueueSize;
        public PhysicsLoadBalancer LoadBalancer => m_loadBalancer;
        public PhysicsObjectPoolManager PoolManager => m_poolManager;

        #endregion
    }
}