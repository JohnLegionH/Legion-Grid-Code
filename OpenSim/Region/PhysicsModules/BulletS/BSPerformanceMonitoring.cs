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
using System.Reflection;
using log4net;
using OpenSim.Framework.Console;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// BulletSim Physics Performance Monitoring Extension
    /// Provides comprehensive physics engine performance monitoring and diagnostics
    /// </summary>
    public partial class BSScene
    {
        #region Performance Monitoring Infrastructure

        private static readonly ILog m_perfLog = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string PerfLogHeader = "[BULLETSIM PERFORMANCE]";

        // Performance tracking collections
        private readonly Queue<PhysicsStepMetric> m_stepMetrics = new Queue<PhysicsStepMetric>();
        private readonly Queue<CollisionMetric> m_collisionMetrics = new Queue<CollisionMetric>();
        private const int MaxSamples = 100;

        // Performance counters
        private long m_totalPhysicsSteps = 0;
        private double m_totalSimulationTime = 0;
        private long m_totalCollisions = 0;
        private long m_totalUpdates = 0;
        private long m_totalTaints = 0;

        // Alert thresholds
        private double m_stepTimeThresholdMs = 20.0; // 20ms for 50 FPS
        private int m_collisionCountThreshold = 100;
        private int m_updateCountThreshold = 200;
        private DateTime m_lastAlertTime = DateTime.MinValue;
        private readonly TimeSpan m_alertCooldown = TimeSpan.FromMinutes(2);

        #endregion

        #region Performance Data Structures

        /// <summary>
        /// Represents a single physics step performance metric
        /// </summary>
        private struct PhysicsStepMetric
        {
            public DateTime Timestamp;
            public float TimeStep;
            public int SimulationTime;
            public int NumTaints;
            public int NumSubSteps;
            public int UpdatedEntityCount;
            public int CollidersCount;
            public long MemoryUsageKB;
            public int ActiveObjects;
        }

        /// <summary>
        /// Represents collision detection performance metrics
        /// </summary>
        private struct CollisionMetric
        {
            public DateTime Timestamp;
            public int CollisionCount;
            public float CollisionTime;
            public int ActiveColliders;
        }

        /// <summary>
        /// Comprehensive physics performance statistics
        /// </summary>
        public struct PhysicsPerformanceStats
        {
            public long TotalSteps;
            public double AverageStepTimeMs;
            public double RecentAverageStepTimeMs;
            public double MaxStepTimeMs;
            public double MinStepTimeMs;
            public long TotalCollisions;
            public double AverageCollisionsPerStep;
            public long TotalUpdates;
            public double AverageUpdatesPerStep;
            public long TotalTaints;
            public double AverageTaintsPerStep;
            public int ActivePhysicsObjects;
            public long MemoryUsageKB;
            public float PhysicsFPS;
            public bool UsingSeparateThread;
            public string PhysicsEngine;
        }

        #endregion

        #region Performance Monitoring Methods

        /// <summary>
        /// Records physics step performance metrics
        /// Called from DoPhysicsStep method
        /// </summary>
        private void RecordPhysicsStepMetric(float timeStep, int simTime, int numTaints, 
            int numSubSteps, int updatedEntityCount, int collidersCount)
        {
            var metric = new PhysicsStepMetric
            {
                Timestamp = DateTime.UtcNow,
                TimeStep = timeStep,
                SimulationTime = simTime,
                NumTaints = numTaints,
                NumSubSteps = numSubSteps,
                UpdatedEntityCount = updatedEntityCount,
                CollidersCount = collidersCount,
                MemoryUsageKB = GC.GetTotalMemory(false) / 1024,
                ActiveObjects = PhysObjects.Count
            };

            lock (m_stepMetrics)
            {
                m_stepMetrics.Enqueue(metric);

                // Update counters
                m_totalPhysicsSteps++;
                m_totalSimulationTime += simTime;
                m_totalCollisions += collidersCount;
                m_totalUpdates += updatedEntityCount;
                m_totalTaints += numTaints;

                // Maintain rolling window
                if (m_stepMetrics.Count > MaxSamples)
                    m_stepMetrics.Dequeue();
            }

            // Check for performance alerts
            CheckPhysicsPerformanceAlerts(metric);

            // Log performance data
            if (PhysicsLogging.Enabled)
            {
                m_perfLog.InfoFormat("{0}: Step - Time: {1}ms, Substeps: {2}, Collisions: {3}, Updates: {4}, Objects: {5}",
                    PerfLogHeader, simTime, numSubSteps, collidersCount, updatedEntityCount, PhysObjects.Count);
            }
        }

        /// <summary>
        /// Records collision detection performance metrics
        /// </summary>
        private void RecordCollisionMetric(int collisionCount, float collisionTime)
        {
            var metric = new CollisionMetric
            {
                Timestamp = DateTime.UtcNow,
                CollisionCount = collisionCount,
                CollisionTime = collisionTime,
                ActiveColliders = ObjectsWithCollisions.Count
            };

            lock (m_collisionMetrics)
            {
                m_collisionMetrics.Enqueue(metric);

                if (m_collisionMetrics.Count > MaxSamples)
                    m_collisionMetrics.Dequeue();
            }
        }

        /// <summary>
        /// Checks for physics performance alerts
        /// </summary>
        private void CheckPhysicsPerformanceAlerts(PhysicsStepMetric metric)
        {
            var now = DateTime.UtcNow;
            if (now - m_lastAlertTime < m_alertCooldown)
                return;

            bool alertTriggered = false;
            var alertMessages = new List<string>();

            // Check step time threshold
            if (metric.SimulationTime > m_stepTimeThresholdMs)
            {
                alertTriggered = true;
                alertMessages.Add($"Slow physics step detected! Time: {metric.SimulationTime}ms (threshold: {m_stepTimeThresholdMs}ms). Consider reducing physics load or optimizing.");
            }

            // Check collision count threshold
            if (metric.CollidersCount > m_collisionCountThreshold)
            {
                alertTriggered = true;
                alertMessages.Add($"High collision count detected! Collisions: {metric.CollidersCount} (threshold: {m_collisionCountThreshold}). May impact performance.");
            }

            // Check update count threshold
            if (metric.UpdatedEntityCount > m_updateCountThreshold)
            {
                alertTriggered = true;
                alertMessages.Add($"High update count detected! Updates: {metric.UpdatedEntityCount} (threshold: {m_updateCountThreshold}). Consider physics optimization.");
            }

            if (alertTriggered)
            {
                m_lastAlertTime = now;
                foreach (var message in alertMessages)
                {
                    m_perfLog.WarnFormat("{0} ALERT: {1}", PerfLogHeader, message);
                }
            }
        }

        /// <summary>
        /// Gets current physics performance statistics
        /// </summary>
        public PhysicsPerformanceStats GetPhysicsPerformanceStats()
        {
            lock (m_stepMetrics)
            {
                var recentMetrics = m_stepMetrics.ToArray();
                var recentStepTimes = recentMetrics.Select(m => (double)m.SimulationTime).ToArray();

                return new PhysicsPerformanceStats
                {
                    TotalSteps = m_totalPhysicsSteps,
                    AverageStepTimeMs = m_totalPhysicsSteps > 0 ? m_totalSimulationTime / m_totalPhysicsSteps : 0,
                    RecentAverageStepTimeMs = recentStepTimes.Length > 0 ? recentStepTimes.Average() : 0,
                    MaxStepTimeMs = recentStepTimes.Length > 0 ? recentStepTimes.Max() : 0,
                    MinStepTimeMs = recentStepTimes.Length > 0 ? recentStepTimes.Min() : 0,
                    TotalCollisions = m_totalCollisions,
                    AverageCollisionsPerStep = m_totalPhysicsSteps > 0 ? (double)m_totalCollisions / m_totalPhysicsSteps : 0,
                    TotalUpdates = m_totalUpdates,
                    AverageUpdatesPerStep = m_totalPhysicsSteps > 0 ? (double)m_totalUpdates / m_totalPhysicsSteps : 0,
                    TotalTaints = m_totalTaints,
                    AverageTaintsPerStep = m_totalPhysicsSteps > 0 ? (double)m_totalTaints / m_totalPhysicsSteps : 0,
                    ActivePhysicsObjects = PhysObjects.Count,
                    MemoryUsageKB = GC.GetTotalMemory(false) / 1024,
                    PhysicsFPS = m_totalSimulationTime > 0 ? (float)(m_totalPhysicsSteps * 1000.0 / m_totalSimulationTime) : 0,
                    UsingSeparateThread = BSParam.UseSeparatePhysicsThread,
                    PhysicsEngine = "BulletSim"
                };
            }
        }

        #endregion

        #region Physics Console Commands

        /// <summary>
        /// Registers physics performance monitoring console commands
        /// Called during scene initialization
        /// </summary>
        private void RegisterPhysicsPerformanceCommands()
        {
            try
            {
                // For now, just log that monitoring is active
                // Console commands will be added when we can properly access scene console
                m_perfLog.InfoFormat("{0}: Physics performance monitoring initialized for region {1}", 
                    PerfLogHeader, RegionName);
                m_perfLog.InfoFormat("{0}: Use 'show physics performance' command will be available in future update", PerfLogHeader);
            }
            catch (Exception ex)
            {
                m_perfLog.WarnFormat("{0}: Failed to initialize physics performance monitoring: {1}", PerfLogHeader, ex.Message);
            }
        }

        #endregion
    }
}