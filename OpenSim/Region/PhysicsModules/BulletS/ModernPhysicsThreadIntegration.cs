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
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Integration layer connecting modern multithreaded physics with legacy BSScene
    /// Provides smooth transition and backward compatibility
    /// </summary>
    public class ModernPhysicsThreadIntegration : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MODERN PHYSICS INTEGRATION]";

        #region Private Fields

        private readonly BSScene m_scene;
        private ModernPhysicsThreadManager m_threadManager;
        private bool m_enabled;
        private bool m_disposed;

        // Performance tracking
        private DateTime m_lastPerformanceReport;
        private readonly TimeSpan PerformanceReportInterval = TimeSpan.FromMinutes(1);

        #endregion

        #region Constructor

        public ModernPhysicsThreadIntegration(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_lastPerformanceReport = DateTime.UtcNow;
            m_enabled = false;

            m_log.InfoFormat("{0}: Modern physics integration created for scene {1}", LogHeader, scene.RegionName);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the multithreaded physics system
        /// </summary>
        public void Initialize()
        {
            if (m_disposed || m_enabled)
                return;

            try
            {
                // Check if multithreading is enabled in configuration
                if (!BSParam.EnableMultithreadedPhysics)
                {
                    m_log.InfoFormat("{0}: Multithreaded physics disabled in configuration", LogHeader);
                    return;
                }

                // Create thread manager with scene-appropriate settings
                int threadCount = CalculateOptimalThreadCount();
                int queueSize = CalculateOptimalQueueSize();

                m_threadManager = new ModernPhysicsThreadManager(m_scene, threadCount, queueSize);
                m_threadManager.Start();

                m_enabled = true;

                m_log.InfoFormat("{0}: Modern physics integration initialized with {1} threads", 
                    LogHeader, threadCount);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to initialize modern physics integration: {1}", LogHeader, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Shutdown the multithreaded physics system
        /// </summary>
        public void Shutdown()
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                m_threadManager?.Stop();
                m_enabled = false;

                m_log.InfoFormat("{0}: Modern physics integration shutdown complete", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during shutdown: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Submit physics work for multithreaded processing
        /// </summary>
        public async Task<bool> SubmitPhysicsWorkAsync(uint objectID, PhysicsWorkType workType, object workData = null)
        {
            if (!m_enabled || m_threadManager == null)
                return false;

            try
            {
                var workItem = new PhysicsWorkItem
                {
                    ObjectID = objectID,
                    WorkType = workType,
                    WorkData = workData
                };

                return await m_threadManager.SubmitWorkAsync(workItem);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error submitting physics work: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Submit physics work synchronously (non-blocking)
        /// </summary>
        public bool SubmitPhysicsWork(uint objectID, PhysicsWorkType workType, object workData = null)
        {
            if (!m_enabled || m_threadManager == null)
                return false;

            try
            {
                var workItem = new PhysicsWorkItem
                {
                    ObjectID = objectID,
                    WorkType = workType,
                    WorkData = workData
                };

                return m_threadManager.SubmitWork(workItem);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error submitting physics work: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Process physics step with multithreading support
        /// </summary>
        public void ProcessPhysicsStep(float timeStep)
        {
            if (!m_enabled)
                return;

            try
            {
                // Submit work items for different types of physics objects
                SubmitRigidBodyWork();
                SubmitCharacterWork();
                SubmitVehicleWork();
                SubmitCollisionWork();

                // Update performance statistics periodically
                UpdatePerformanceStatistics();
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing physics step: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Get current performance statistics
        /// </summary>
        public string GetPerformanceReport()
        {
            if (!m_enabled || m_threadManager == null)
                return "Multithreaded physics not enabled";

            try
            {
                var stats = m_threadManager.GetThreadStatistics();
                var queueStatus = m_threadManager.GetQueueStatus();

                var report = $"Physics Threading Performance:\n";
                report += $"  Active Threads: {stats.Count}\n";
                report += $"  Queue Status: {queueStatus.QueuedItems}/{queueStatus.MaxQueueSize} ({queueStatus.LoadPercentage:F1}%)\n";

                foreach (var threadStat in stats)
                {
                    report += $"  Thread {threadStat.ThreadId}: {threadStat.WorkItemsProcessed} items, ";
                    report += $"{threadStat.AverageProcessingTime:F2}ms avg, {threadStat.ThreadUtilization:F1}% util\n";
                }

                return report;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error generating performance report: {1}", LogHeader, ex.Message);
                return "Error generating performance report";
            }
        }

        #endregion

        #region Private Methods

        private int CalculateOptimalThreadCount()
        {
            // Conservative approach: use half of available cores, minimum 2, maximum 8
            int coreCount = Environment.ProcessorCount;
            int threadCount = Math.Max(2, Math.Min(8, coreCount / 2));

            // Adjust based on scene size and complexity
            int objectCount = m_scene.PhysObjects.Count;
            if (objectCount > 1000)
                threadCount = Math.Min(threadCount + 2, 8);
            else if (objectCount < 100)
                threadCount = Math.Max(threadCount - 1, 2);

            return threadCount;
        }

        private int CalculateOptimalQueueSize()
        {
            // Base queue size on number of physics objects
            int objectCount = m_scene.PhysObjects.Count;
            int baseQueueSize = Math.Max(1000, objectCount * 2);
            
            // Cap at reasonable maximum
            return Math.Min(baseQueueSize, 50000);
        }

        private void SubmitRigidBodyWork()
        {
            if (m_threadManager == null)
                return;

            foreach (var physObj in m_scene.PhysObjects.Values)
            {
                if (physObj is BSPrimLinkable && physObj.PhysBody.HasPhysicalBody)
                {
                    SubmitPhysicsWork(physObj.LocalID, PhysicsWorkType.RigidBodyUpdate, physObj);
                }
            }
        }

        private void SubmitCharacterWork()
        {
            if (m_threadManager == null)
                return;

            foreach (var physObj in m_scene.PhysObjects.Values)
            {
                if (physObj is BSCharacter)
                {
                    SubmitPhysicsWork(physObj.LocalID, PhysicsWorkType.CharacterUpdate, physObj);
                }
            }
        }

        private void SubmitVehicleWork()
        {
            if (m_threadManager == null)
                return;

            foreach (var physObj in m_scene.PhysObjects.Values)
            {
                if (physObj is BSPrimLinkable prim && prim.GetVehicleActor(false) != null)
                {
                    SubmitPhysicsWork(physObj.LocalID, PhysicsWorkType.VehicleUpdate, physObj);
                }
            }
        }

        private void SubmitCollisionWork()
        {
            if (m_threadManager == null)
                return;

            // Submit collision detection work for objects that need it
            // This is a simplified version - full implementation would be more sophisticated
            var activeObjects = new List<BSPhysObject>();
            foreach (var physObj in m_scene.PhysObjects.Values)
            {
                if (physObj.PhysBody.HasPhysicalBody && !physObj.IsStationary)
                {
                    activeObjects.Add(physObj);
                }
            }

            // Group objects for collision detection work
            for (int i = 0; i < activeObjects.Count; i += 10) // Process in batches of 10
            {
                var batch = activeObjects.GetRange(i, Math.Min(10, activeObjects.Count - i));
                SubmitPhysicsWork(0, PhysicsWorkType.CollisionDetection, batch);
            }
        }

        private void UpdatePerformanceStatistics()
        {
            DateTime now = DateTime.UtcNow;
            if (now - m_lastPerformanceReport >= PerformanceReportInterval)
            {
                if (m_threadManager != null)
                {
                    var queueStatus = m_threadManager.GetQueueStatus();
                    
                    m_log.InfoFormat("{0}: Performance - Queue: {1}/{2} ({3:F1}%), Threads: {4}", 
                        LogHeader, queueStatus.QueuedItems, queueStatus.MaxQueueSize, 
                        queueStatus.LoadPercentage, m_threadManager.ThreadCount);
                }

                m_lastPerformanceReport = now;
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
                Shutdown();
                m_threadManager?.Dispose();
                m_threadManager = null;

                m_disposed = true;
                m_log.InfoFormat("{0}: Modern physics integration disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public bool IsDisposed => m_disposed;
        public ModernPhysicsThreadManager ThreadManager => m_threadManager;

        #endregion
    }
}