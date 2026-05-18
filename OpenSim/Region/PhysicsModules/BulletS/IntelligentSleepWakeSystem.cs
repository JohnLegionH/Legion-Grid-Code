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
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Physics object sleep state
    /// </summary>
    public enum PhysicsSleepState
    {
        Awake = 0,          // Actively participating in physics simulation
        Drowsy = 1,         // Low activity, candidate for sleeping
        LightSleep = 2,     // Minimal physics processing
        DeepSleep = 3,      // Suspended from physics simulation
        Hibernation = 4     // Long-term inactive, minimal memory footprint
    }

    /// <summary>
    /// Sleep/wake criteria for intelligent decision making
    /// </summary>
    public class SleepWakeCriteria
    {
        public float VelocityThreshold { get; set; } = 0.1f;
        public float AngularVelocityThreshold { get; set; } = 0.1f;
        public float AccelerationThreshold { get; set; } = 0.05f;
        public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan LightSleepTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan DeepSleepTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan HibernationTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public float DistanceToAvatar { get; set; } = 100.0f;
        public float InteractionProximity { get; set; } = 10.0f;
        public bool ConsiderMass { get; set; } = true;
        public bool ConsiderHeight { get; set; } = true;
        public float MinMassForSleep { get; set; } = 0.1f;
        public float MaxHeightForSleep { get; set; } = 1000.0f;
    }

    /// <summary>
    /// Sleep/wake event data
    /// </summary>
    public struct SleepWakeEvent
    {
        public uint ObjectID;
        public PhysicsSleepState OldState;
        public PhysicsSleepState NewState;
        public string Reason;
        public DateTime EventTime;
        public float EnergySaved;
        public bool WasForced;
    }

    /// <summary>
    /// Sleeping object data
    /// </summary>
    public class SleepingObjectData
    {
        public uint ObjectID { get; set; }
        public PhysicsSleepState SleepState { get; set; }
        public DateTime LastActiveTime { get; set; }
        public DateTime SleepStartTime { get; set; }
        public OMV.Vector3 LastKnownPosition { get; set; }
        public OMV.Vector3 LastKnownVelocity { get; set; }
        public OMV.Vector3 LastKnownAngularVelocity { get; set; }
        public OMV.Quaternion LastKnownRotation { get; set; }
        public float Mass { get; set; }
        public string ObjectType { get; set; }
        
        // Activity tracking
        public float ActivityScore { get; set; }
        public int WakeUpCount { get; set; }
        public TimeSpan TotalSleepTime { get; set; }
        public float AverageActivityLevel { get; set; }
        
        // Proximity tracking
        public float DistanceToNearestAvatar { get; set; } = float.MaxValue;
        public uint NearestAvatarID { get; set; }
        public bool IsInInteractionRange { get; set; }
        
        // Sleep criteria satisfaction
        public bool MeetsVelocityCriteria { get; set; }
        public bool MeetsTimeCriteria { get; set; }
        public bool MeetsProximityCriteria { get; set; }
        public bool CanEnterLightSleep { get; set; }
        public bool CanEnterDeepSleep { get; set; }
        public bool CanEnterHibernation { get; set; }
        
        public SleepingObjectData(uint objectId)
        {
            ObjectID = objectId;
            SleepState = PhysicsSleepState.Awake;
            LastActiveTime = DateTime.UtcNow;
            ObjectType = "Unknown";
        }
        
        public TimeSpan TimeSinceActive => DateTime.UtcNow - LastActiveTime;
        public TimeSpan CurrentSleepDuration => SleepState != PhysicsSleepState.Awake ? DateTime.UtcNow - SleepStartTime : TimeSpan.Zero;
        public bool IsAsleep => SleepState != PhysicsSleepState.Awake;
    }

    /// <summary>
    /// Intelligent sleeping and waking system for physics objects
    /// </summary>
    public class IntelligentSleepWakeSystem : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[INTELLIGENT SLEEP WAKE]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly ConcurrentDictionary<uint, SleepingObjectData> m_sleepingObjects;
        private readonly ConcurrentDictionary<uint, OMV.Vector3> m_avatarPositions;
        private readonly Timer m_sleepWakeTimer;
        private readonly object m_sleepLock;
        private bool m_disposed;
        private bool m_enabled;

        // Configuration
        private SleepWakeCriteria m_criteria;
        private readonly TimeSpan m_evaluationInterval = TimeSpan.FromSeconds(1); // Evaluate every second
        private readonly int m_maxObjectsPerCycle = 50; // Process at most 50 objects per cycle

        // Performance tracking
        private long m_objectsEvaluated;
        private long m_sleepTransitions;
        private long m_wakeTransitions;
        private long m_forcedWakeUps;
        private float m_energySavings;
        private int m_currentAwakeObjects;
        private int m_currentSleepingObjects;
        private DateTime m_lastPerformanceReport;
        private readonly TimeSpan PerformanceReportInterval = TimeSpan.FromMinutes(2);

        // Adaptive parameters
        private float m_currentCpuLoad = 0.5f;
        private int m_targetSleepingRatio = 30; // Target 30% of objects sleeping
        private readonly Queue<SleepWakeEvent> m_recentEvents;
        private readonly int m_maxEventHistory = 200;

        #endregion

        #region Constructor

        public IntelligentSleepWakeSystem(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_sleepingObjects = new ConcurrentDictionary<uint, SleepingObjectData>();
            m_avatarPositions = new ConcurrentDictionary<uint, OMV.Vector3>();
            m_sleepLock = new object();
            m_recentEvents = new Queue<SleepWakeEvent>();

            // Initialize default criteria
            m_criteria = new SleepWakeCriteria();

            m_lastPerformanceReport = DateTime.UtcNow;

            // Setup evaluation timer
            m_sleepWakeTimer = new Timer(EvaluateSleepWake, null, m_evaluationInterval, m_evaluationInterval);

            m_enabled = true;

            m_log.InfoFormat("{0}: Intelligent sleep/wake system initialized - Evaluation interval: {1}s", 
                LogHeader, m_evaluationInterval.TotalSeconds);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the sleep/wake system
        /// </summary>
        public void Initialize()
        {
            if (m_disposed)
                return;

            m_enabled = true;
            m_log.InfoFormat("{0}: Intelligent sleep/wake system started", LogHeader);
        }

        /// <summary>
        /// Register an object for sleep/wake management
        /// </summary>
        public void RegisterObject(uint objectID, string objectType, OMV.Vector3 position, float mass)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                var sleepData = new SleepingObjectData(objectID)
                {
                    ObjectType = objectType,
                    LastKnownPosition = position,
                    Mass = mass
                };

                m_sleepingObjects.AddOrUpdate(objectID, sleepData, (id, existing) => 
                {
                    existing.LastActiveTime = DateTime.UtcNow;
                    existing.LastKnownPosition = position;
                    return existing;
                });

                m_currentAwakeObjects++;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error registering object {1}: {2}", LogHeader, objectID, ex.Message);
            }
        }

        /// <summary>
        /// Update object physics state for sleep/wake evaluation
        /// </summary>
        public void UpdateObjectState(uint objectID, OMV.Vector3 position, OMV.Vector3 velocity, 
            OMV.Vector3 angularVelocity, OMV.Quaternion rotation)
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_sleepingObjects.TryGetValue(objectID, out SleepingObjectData sleepData))
            {
                lock (sleepData)
                {
                    var wasActive = IsObjectActive(velocity, angularVelocity);
                    
                    sleepData.LastKnownPosition = position;
                    sleepData.LastKnownVelocity = velocity;
                    sleepData.LastKnownAngularVelocity = angularVelocity;
                    sleepData.LastKnownRotation = rotation;

                    if (wasActive)
                    {
                        sleepData.LastActiveTime = DateTime.UtcNow;
                        
                        // Wake up if currently sleeping
                        if (sleepData.IsAsleep)
                        {
                            WakeUpObject(sleepData, "Object became active");
                        }
                    }

                    // Update activity score
                    UpdateActivityScore(sleepData, velocity, angularVelocity);
                    
                    // Update sleep criteria
                    UpdateSleepCriteria(sleepData);
                }
            }
        }

        /// <summary>
        /// Update avatar position for proximity calculations
        /// </summary>
        public void UpdateAvatarPosition(uint avatarID, OMV.Vector3 position)
        {
            if (!m_enabled || m_disposed)
                return;

            m_avatarPositions.AddOrUpdate(avatarID, position, (id, oldPos) => position);
        }

        /// <summary>
        /// Remove avatar from tracking
        /// </summary>
        public void RemoveAvatar(uint avatarID)
        {
            if (!m_enabled || m_disposed)
                return;

            m_avatarPositions.TryRemove(avatarID, out _);
        }

        /// <summary>
        /// Force an object to wake up
        /// </summary>
        public void ForceWakeUp(uint objectID, string reason = "Forced wake up")
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_sleepingObjects.TryGetValue(objectID, out SleepingObjectData sleepData))
            {
                lock (sleepData)
                {
                    if (sleepData.IsAsleep)
                    {
                        WakeUpObject(sleepData, reason, true);
                        m_forcedWakeUps++;
                    }
                }
            }
        }

        /// <summary>
        /// Force an object to sleep
        /// </summary>
        public void ForceSleep(uint objectID, PhysicsSleepState sleepState = PhysicsSleepState.LightSleep, string reason = "Forced sleep")
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_sleepingObjects.TryGetValue(objectID, out SleepingObjectData sleepData))
            {
                lock (sleepData)
                {
                    if (!sleepData.IsAsleep)
                    {
                        PutObjectToSleep(sleepData, sleepState, reason, true);
                    }
                }
            }
        }

        /// <summary>
        /// Get current sleep state of an object
        /// </summary>
        public PhysicsSleepState GetSleepState(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return PhysicsSleepState.Awake;

            if (m_sleepingObjects.TryGetValue(objectID, out SleepingObjectData sleepData))
            {
                return sleepData.SleepState;
            }

            return PhysicsSleepState.Awake;
        }

        /// <summary>
        /// Check if object should be processed this frame
        /// </summary>
        public bool ShouldProcessObject(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return true;

            if (m_sleepingObjects.TryGetValue(objectID, out SleepingObjectData sleepData))
            {
                return sleepData.SleepState == PhysicsSleepState.Awake || 
                       sleepData.SleepState == PhysicsSleepState.Drowsy;
            }

            return true;
        }

        /// <summary>
        /// Unregister an object from sleep/wake management
        /// </summary>
        public void UnregisterObject(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_sleepingObjects.TryRemove(objectID, out SleepingObjectData sleepData))
            {
                if (sleepData.IsAsleep)
                    m_currentSleepingObjects--;
                else
                    m_currentAwakeObjects--;
            }
        }

        /// <summary>
        /// Get comprehensive sleep/wake performance report
        /// </summary>
        public string GetPerformanceReport()
        {
            if (!m_enabled || m_disposed)
                return "Intelligent sleep/wake system not available";

            var report = $"Intelligent Sleep/Wake System Performance:\\n";
            report += $"  Tracked Objects: {m_sleepingObjects.Count}\\n";
            report += $"  Awake Objects: {m_currentAwakeObjects}\\n";
            report += $"  Sleeping Objects: {m_currentSleepingObjects}\\n";
            report += $"  Sleep Ratio: {GetSleepRatio():P1}\\n";
            report += $"  Objects Evaluated: {m_objectsEvaluated}\\n";
            report += $"  Sleep Transitions: {m_sleepTransitions}\\n";
            report += $"  Wake Transitions: {m_wakeTransitions}\\n";
            report += $"  Forced Wake Ups: {m_forcedWakeUps}\\n";
            report += $"  Energy Savings: {m_energySavings:F2}\\n";

            // Sleep state distribution
            var stateDistribution = m_sleepingObjects.Values
                .GroupBy(data => data.SleepState)
                .ToDictionary(g => g.Key, g => g.Count());

            report += $"  Sleep State Distribution:\\n";
            foreach (var state in Enum.GetValues<PhysicsSleepState>())
            {
                var count = stateDistribution.GetValueOrDefault(state, 0);
                report += $"    {state}: {count}\\n";
            }

            return report;
        }

        /// <summary>
        /// Get recent sleep/wake events
        /// </summary>
        public List<SleepWakeEvent> GetRecentEvents(int count = 20)
        {
            lock (m_sleepLock)
            {
                return m_recentEvents.TakeLast(count).ToList();
            }
        }

        /// <summary>
        /// Update sleep/wake criteria
        /// </summary>
        public void UpdateCriteria(SleepWakeCriteria newCriteria)
        {
            m_criteria = newCriteria ?? throw new ArgumentNullException(nameof(newCriteria));
            m_log.InfoFormat("{0}: Sleep/wake criteria updated", LogHeader);
        }

        #endregion

        #region Private Methods

        private void EvaluateSleepWake(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                lock (m_sleepLock)
                {
                    var objectsToEvaluate = m_sleepingObjects.Values.ToList();
                    var evaluatedThisCycle = 0;

                    // Update proximity information
                    UpdateProximityData();

                    foreach (var sleepData in objectsToEvaluate)
                    {
                        if (evaluatedThisCycle >= m_maxObjectsPerCycle)
                            break;

                        lock (sleepData)
                        {
                            EvaluateObjectSleepWake(sleepData);
                            evaluatedThisCycle++;
                        }

                        m_objectsEvaluated++;
                    }

                    // Adaptive parameter adjustment
                    AdaptSleepParameters();

                    // Performance reporting
                    if (DateTime.UtcNow - m_lastPerformanceReport >= PerformanceReportInterval)
                    {
                        m_log.InfoFormat("{0}: {1}", LogHeader, GetPerformanceReport().Replace("\\n", " "));
                        m_lastPerformanceReport = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during sleep/wake evaluation: {1}", LogHeader, ex.Message);
            }
        }

        private void EvaluateObjectSleepWake(SleepingObjectData sleepData)
        {
            switch (sleepData.SleepState)
            {
                case PhysicsSleepState.Awake:
                    EvaluateForSleep(sleepData);
                    break;

                case PhysicsSleepState.Drowsy:
                    EvaluateForLightSleep(sleepData);
                    break;

                case PhysicsSleepState.LightSleep:
                    EvaluateForDeepSleep(sleepData);
                    break;

                case PhysicsSleepState.DeepSleep:
                    EvaluateForHibernation(sleepData);
                    break;

                case PhysicsSleepState.Hibernation:
                    EvaluateForWakeUp(sleepData);
                    break;
            }

            // Always check for wake up conditions
            if (sleepData.IsAsleep && ShouldWakeUp(sleepData))
            {
                WakeUpObject(sleepData, "Wake up conditions met");
            }
        }

        private void EvaluateForSleep(SleepingObjectData sleepData)
        {
            if (sleepData.CanEnterLightSleep && sleepData.TimeSinceActive >= m_criteria.InactivityTimeout)
            {
                PutObjectToSleep(sleepData, PhysicsSleepState.Drowsy, "Object inactive for extended period");
            }
        }

        private void EvaluateForLightSleep(SleepingObjectData sleepData)
        {
            if (sleepData.CurrentSleepDuration >= TimeSpan.FromSeconds(10) && sleepData.CanEnterLightSleep)
            {
                PutObjectToSleep(sleepData, PhysicsSleepState.LightSleep, "Transitioning to light sleep");
            }
        }

        private void EvaluateForDeepSleep(SleepingObjectData sleepData)
        {
            if (sleepData.CurrentSleepDuration >= m_criteria.LightSleepTimeout && sleepData.CanEnterDeepSleep)
            {
                PutObjectToSleep(sleepData, PhysicsSleepState.DeepSleep, "Transitioning to deep sleep");
            }
        }

        private void EvaluateForHibernation(SleepingObjectData sleepData)
        {
            if (sleepData.CurrentSleepDuration >= m_criteria.DeepSleepTimeout && sleepData.CanEnterHibernation)
            {
                PutObjectToSleep(sleepData, PhysicsSleepState.Hibernation, "Transitioning to hibernation");
            }
        }

        private void EvaluateForWakeUp(SleepingObjectData sleepData)
        {
            if (ShouldWakeUp(sleepData))
            {
                WakeUpObject(sleepData, "Wake up conditions met during hibernation");
            }
        }

        private bool ShouldWakeUp(SleepingObjectData sleepData)
        {
            // Wake up if avatar is nearby
            if (sleepData.IsInInteractionRange)
                return true;

            // Wake up if object type requires special attention
            if (sleepData.ObjectType == "Character" || sleepData.ObjectType == "Vehicle")
                return true;

            // Wake up if there's been external interaction
            if (sleepData.ActivityScore > 0.5f)
                return true;

            return false;
        }

        private void PutObjectToSleep(SleepingObjectData sleepData, PhysicsSleepState newState, string reason, bool forced = false)
        {
            var oldState = sleepData.SleepState;
            sleepData.SleepState = newState;
            sleepData.SleepStartTime = DateTime.UtcNow;

            // Update counters
            if (oldState == PhysicsSleepState.Awake)
            {
                m_currentAwakeObjects--;
                m_currentSleepingObjects++;
            }

            // Calculate energy savings
            var energySaved = CalculateEnergySavings(sleepData, newState);
            m_energySavings += energySaved;

            // Record event
            RecordSleepWakeEvent(sleepData.ObjectID, oldState, newState, reason, energySaved, forced);

            m_sleepTransitions++;

            m_log.DebugFormat("{0}: Object {1} transitioned to {2} - {3}", 
                LogHeader, sleepData.ObjectID, newState, reason);
        }

        private void WakeUpObject(SleepingObjectData sleepData, string reason, bool forced = false)
        {
            var oldState = sleepData.SleepState;
            
            // Update total sleep time
            if (sleepData.IsAsleep)
            {
                sleepData.TotalSleepTime += sleepData.CurrentSleepDuration;
            }

            sleepData.SleepState = PhysicsSleepState.Awake;
            sleepData.LastActiveTime = DateTime.UtcNow;
            sleepData.WakeUpCount++;

            // Update counters
            if (oldState != PhysicsSleepState.Awake)
            {
                m_currentSleepingObjects--;
                m_currentAwakeObjects++;
            }

            // Record event
            RecordSleepWakeEvent(sleepData.ObjectID, oldState, PhysicsSleepState.Awake, reason, 0.0f, forced);

            m_wakeTransitions++;

            m_log.DebugFormat("{0}: Object {1} woke up from {2} - {3}", 
                LogHeader, sleepData.ObjectID, oldState, reason);
        }

        private void UpdateProximityData()
        {
            foreach (var sleepData in m_sleepingObjects.Values)
            {
                var minDistance = float.MaxValue;
                uint nearestAvatarID = 0;

                foreach (var kvp in m_avatarPositions)
                {
                    var distance = SIMDPhysicsMath.Distance(sleepData.LastKnownPosition, kvp.Value);
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        nearestAvatarID = kvp.Key;
                    }
                }

                sleepData.DistanceToNearestAvatar = minDistance;
                sleepData.NearestAvatarID = nearestAvatarID;
                sleepData.IsInInteractionRange = minDistance <= m_criteria.InteractionProximity;
            }
        }

        private void UpdateSleepCriteria(SleepingObjectData sleepData)
        {
            var velocity = sleepData.LastKnownVelocity.Length();
            var angularVelocity = sleepData.LastKnownAngularVelocity.Length();

            sleepData.MeetsVelocityCriteria = velocity <= m_criteria.VelocityThreshold && 
                                             angularVelocity <= m_criteria.AngularVelocityThreshold;

            sleepData.MeetsTimeCriteria = sleepData.TimeSinceActive >= m_criteria.InactivityTimeout;
            
            sleepData.MeetsProximityCriteria = sleepData.DistanceToNearestAvatar >= m_criteria.DistanceToAvatar;

            // Determine what sleep levels are available
            sleepData.CanEnterLightSleep = sleepData.MeetsVelocityCriteria && 
                                          sleepData.MeetsTimeCriteria &&
                                          !sleepData.IsInInteractionRange;

            sleepData.CanEnterDeepSleep = sleepData.CanEnterLightSleep && 
                                         sleepData.MeetsProximityCriteria &&
                                         sleepData.Mass >= m_criteria.MinMassForSleep;

            sleepData.CanEnterHibernation = sleepData.CanEnterDeepSleep && 
                                           sleepData.LastKnownPosition.Z <= m_criteria.MaxHeightForSleep &&
                                           sleepData.ObjectType != "Character" &&
                                           sleepData.ObjectType != "Vehicle";
        }

        private void UpdateActivityScore(SleepingObjectData sleepData, OMV.Vector3 velocity, OMV.Vector3 angularVelocity)
        {
            var velocityActivity = Math.Min(1.0f, velocity.Length() / 10.0f);
            var angularActivity = Math.Min(1.0f, angularVelocity.Length() / 5.0f);
            var currentActivity = Math.Max(velocityActivity, angularActivity);

            // Exponential moving average
            sleepData.ActivityScore = sleepData.ActivityScore * 0.9f + currentActivity * 0.1f;
            sleepData.AverageActivityLevel = sleepData.AverageActivityLevel * 0.95f + currentActivity * 0.05f;
        }

        private bool IsObjectActive(OMV.Vector3 velocity, OMV.Vector3 angularVelocity)
        {
            return velocity.Length() > m_criteria.VelocityThreshold || 
                   angularVelocity.Length() > m_criteria.AngularVelocityThreshold;
        }

        private float CalculateEnergySavings(SleepingObjectData sleepData, PhysicsSleepState sleepState)
        {
            // Rough calculation of computational energy saved
            float baseCost = 1.0f;
            
            switch (sleepState)
            {
                case PhysicsSleepState.Drowsy:
                    return baseCost * 0.2f;
                case PhysicsSleepState.LightSleep:
                    return baseCost * 0.5f;
                case PhysicsSleepState.DeepSleep:
                    return baseCost * 0.8f;
                case PhysicsSleepState.Hibernation:
                    return baseCost * 0.95f;
                default:
                    return 0.0f;
            }
        }

        private void RecordSleepWakeEvent(uint objectID, PhysicsSleepState oldState, PhysicsSleepState newState, 
            string reason, float energySaved, bool forced)
        {
            var sleepWakeEvent = new SleepWakeEvent
            {
                ObjectID = objectID,
                OldState = oldState,
                NewState = newState,
                Reason = reason,
                EventTime = DateTime.UtcNow,
                EnergySaved = energySaved,
                WasForced = forced
            };

            lock (m_sleepLock)
            {
                m_recentEvents.Enqueue(sleepWakeEvent);
                if (m_recentEvents.Count > m_maxEventHistory)
                {
                    m_recentEvents.Dequeue();
                }
            }
        }

        private void AdaptSleepParameters()
        {
            var currentSleepRatio = GetSleepRatio();

            // Adjust criteria based on current performance
            if (currentSleepRatio < m_targetSleepingRatio * 0.01f)
            {
                // Too few objects sleeping, make criteria more lenient
                m_criteria.InactivityTimeout = TimeSpan.FromSeconds(Math.Max(2, m_criteria.InactivityTimeout.TotalSeconds * 0.95));
                m_criteria.VelocityThreshold *= 1.05f;
            }
            else if (currentSleepRatio > m_targetSleepingRatio * 0.01f * 1.5f)
            {
                // Too many objects sleeping, make criteria more strict
                m_criteria.InactivityTimeout = TimeSpan.FromSeconds(Math.Min(10, m_criteria.InactivityTimeout.TotalSeconds * 1.05));
                m_criteria.VelocityThreshold *= 0.95f;
            }
        }

        private float GetSleepRatio()
        {
            var totalObjects = m_currentAwakeObjects + m_currentSleepingObjects;
            return totalObjects > 0 ? (float)m_currentSleepingObjects / totalObjects * 100.0f : 0.0f;
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
                m_sleepWakeTimer?.Dispose();
                m_sleepingObjects.Clear();
                m_avatarPositions.Clear();

                lock (m_sleepLock)
                {
                    m_recentEvents.Clear();
                }

                m_disposed = true;
                m_log.InfoFormat("{0}: Intelligent sleep/wake system disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public int TrackedObjectCount => m_sleepingObjects.Count;
        public int AwakeObjectCount => m_currentAwakeObjects;
        public int SleepingObjectCount => m_currentSleepingObjects;
        public float SleepRatio => GetSleepRatio();
        public long SleepTransitions => m_sleepTransitions;
        public long WakeTransitions => m_wakeTransitions;
        public float TotalEnergySavings => m_energySavings;

        #endregion
    }
}