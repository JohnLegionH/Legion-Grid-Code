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
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Manages rebuilding and repair mechanics for destructible objects
    /// Provides automatic restoration, manual reconstruction, and resource-based rebuilding
    /// </summary>
    public class DestructionRepairSystem : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION REPAIR]";

        private readonly Scene m_scene;
        private readonly DestructiblePhysicsSystem m_destructionSystem;
        private readonly Dictionary<uint, DestructionRecord> m_destructionHistory = new Dictionary<uint, DestructionRecord>();
        private readonly Dictionary<uint, RepairJob> m_activeRepairs = new Dictionary<uint, RepairJob>();
        private readonly Timer m_repairTimer;
        private readonly Timer m_persistenceTimer;
        private readonly object m_lockObject = new object();
        private readonly DestructionPersistence m_persistence;

        private bool m_enabled = true;
        private bool m_autoRepairEnabled = true;
        private float m_defaultRepairTime = 60.0f; // seconds
        private int m_maxConcurrentRepairs = 5;
        private RepairResourceMode m_resourceMode = RepairResourceMode.None;

        public DestructionRepairSystem(Scene scene, DestructiblePhysicsSystem destructionSystem)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_destructionSystem = destructionSystem ?? throw new ArgumentNullException(nameof(destructionSystem));

            // Initialize persistence system
            m_persistence = new DestructionPersistence(scene.RegionInfo.RegionName);

            // Subscribe to destruction events
            if (m_destructionSystem != null)
            {
                m_destructionSystem.OnObjectDestroyed += OnObjectDestroyed;
            }

            // Start repair timer (check every 10 seconds)
            m_repairTimer = new Timer(ProcessRepairJobs, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            // Start persistence timer (auto-save every 2 minutes)
            m_persistenceTimer = new Timer(AutoSavePersistentData, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

            // Load existing data asynchronously
            _ = Task.Run(LoadPersistentDataAsync);

            m_log.InfoFormat("{0}: Destruction repair system initialized for region {1}", 
                LogHeader, scene.RegionInfo.RegionName);
        }

        #region Public Interface

        /// <summary>
        /// Enable or disable the repair system
        /// </summary>
        public bool Enabled
        {
            get => m_enabled;
            set => m_enabled = value;
        }

        /// <summary>
        /// Enable or disable automatic repairs
        /// </summary>
        public bool AutoRepairEnabled
        {
            get => m_autoRepairEnabled;
            set => m_autoRepairEnabled = value;
        }

        /// <summary>
        /// Default time for auto-repair in seconds
        /// </summary>
        public float DefaultRepairTime
        {
            get => m_defaultRepairTime;
            set => m_defaultRepairTime = Math.Max(5.0f, value);
        }

        /// <summary>
        /// Resource requirements for repairs
        /// </summary>
        public RepairResourceMode ResourceMode
        {
            get => m_resourceMode;
            set => m_resourceMode = value;
        }

        /// <summary>
        /// Manually start repair of a destroyed object
        /// </summary>
        public bool StartRepair(uint objectId, OMV.UUID requesterId, RepairOptions options = null)
        {
            if (!m_enabled)
                return false;

            lock (m_lockObject)
            {
                if (!m_destructionHistory.TryGetValue(objectId, out var record))
                {
                    m_log.WarnFormat("{0}: Cannot repair object {1} - no destruction record found", LogHeader, objectId);
                    return false;
                }

                if (m_activeRepairs.ContainsKey(objectId))
                {
                    m_log.WarnFormat("{0}: Object {1} is already being repaired", LogHeader, objectId);
                    return false;
                }

                if (m_activeRepairs.Count >= m_maxConcurrentRepairs)
                {
                    m_log.WarnFormat("{0}: Cannot start repair - maximum concurrent repairs ({1}) reached", 
                        LogHeader, m_maxConcurrentRepairs);
                    return false;
                }

                // Check repair permissions
                if (!CheckRepairPermissions(record, requesterId))
                {
                    m_log.WarnFormat("{0}: User {1} does not have permission to repair object {2}", 
                        LogHeader, requesterId, objectId);
                    return false;
                }

                // Check resource requirements
                if (!CheckRepairResources(record, requesterId, options))
                {
                    m_log.WarnFormat("{0}: Insufficient resources to repair object {1}", LogHeader, objectId);
                    return false;
                }

                // Create repair job
                var repairJob = new RepairJob
                {
                    ObjectId = objectId,
                    RequesterId = requesterId,
                    Record = record,
                    Options = options ?? new RepairOptions(),
                    StartTime = DateTime.UtcNow,
                    EstimatedCompletion = DateTime.UtcNow.AddSeconds(CalculateRepairTime(record, options)),
                    Status = RepairStatus.InProgress
                };

                m_activeRepairs[objectId] = repairJob;

                m_log.InfoFormat("{0}: Started repair of object {1} (estimated completion: {2})", 
                    LogHeader, objectId, repairJob.EstimatedCompletion);

                return true;
            }
        }

        /// <summary>
        /// Cancel an active repair
        /// </summary>
        public bool CancelRepair(uint objectId, OMV.UUID requesterId)
        {
            lock (m_lockObject)
            {
                if (!m_activeRepairs.TryGetValue(objectId, out var repairJob))
                    return false;

                // Check if requester has permission to cancel
                if (repairJob.RequesterId != requesterId && !IsAdmin(requesterId))
                    return false;

                m_activeRepairs.Remove(objectId);
                m_log.InfoFormat("{0}: Cancelled repair of object {1}", LogHeader, objectId);
                return true;
            }
        }

        /// <summary>
        /// Get repair status for an object
        /// </summary>
        public RepairStatus GetRepairStatus(uint objectId)
        {
            lock (m_lockObject)
            {
                if (m_activeRepairs.TryGetValue(objectId, out var repairJob))
                    return repairJob.Status;

                if (m_destructionHistory.ContainsKey(objectId))
                    return RepairStatus.AwaitingRepair;

                return RepairStatus.NotDestroyed;
            }
        }

        /// <summary>
        /// Get all objects awaiting repair
        /// </summary>
        public List<DestructionRecord> GetObjectsAwaitingRepair()
        {
            lock (m_lockObject)
            {
                return m_destructionHistory.Values
                    .Where(record => !m_activeRepairs.ContainsKey(record.ObjectId))
                    .ToList();
            }
        }

        /// <summary>
        /// Get currently active repairs
        /// </summary>
        public List<RepairJob> GetActiveRepairs()
        {
            lock (m_lockObject)
            {
                return m_activeRepairs.Values.ToList();
            }
        }

        /// <summary>
        /// Clear destruction history for objects that no longer exist
        /// </summary>
        public void CleanupDestructionHistory()
        {
            lock (m_lockObject)
            {
                var toRemove = new List<uint>();
                
                foreach (var kvp in m_destructionHistory)
                {
                    var record = kvp.Value;
                    
                    // Remove old records (older than 24 hours by default)
                    if (DateTime.UtcNow - record.DestructionTime > TimeSpan.FromHours(24))
                    {
                        toRemove.Add(kvp.Key);
                    }
                }

                foreach (var objectId in toRemove)
                {
                    m_destructionHistory.Remove(objectId);
                    m_activeRepairs.Remove(objectId); // Also remove any stale repair jobs
                }

                if (toRemove.Count > 0)
                {
                    m_log.InfoFormat("{0}: Cleaned up {1} old destruction records", LogHeader, toRemove.Count);
                }
            }
        }

        #endregion

        #region Event Handlers

        private void OnObjectDestroyed(uint objectId, OMV.Vector3 position, DestructibleMaterial material, 
            OMV.UUID destroyerId, DestructionCause cause)
        {
            if (!m_enabled)
                return;

            lock (m_lockObject)
            {
                // Get object information from scene
                var sceneObject = m_scene.GetSceneObjectPart(objectId);
                if (sceneObject == null)
                {
                    m_log.WarnFormat("{0}: Cannot create destruction record for object {1} - not found in scene", 
                        LogHeader, objectId);
                    return;
                }

                // Create destruction record
                var record = new DestructionRecord
                {
                    ObjectId = objectId,
                    OriginalName = sceneObject.Name,
                    OriginalDescription = sceneObject.Description,
                    Position = position,
                    Rotation = sceneObject.RotationOffset,
                    Scale = sceneObject.Scale,
                    Material = material,
                    DestructionTime = DateTime.UtcNow,
                    DestroyerId = destroyerId,
                    Cause = cause,
                    OwnerId = sceneObject.OwnerID,
                    GroupId = sceneObject.GroupID,
                    OriginalData = CaptureObjectData(sceneObject)
                };

                lock (m_lockObject)
                {
                    m_destructionHistory[objectId] = record;
                }

                m_log.InfoFormat("{0}: Recorded destruction of object {1} '{2}' at {3}", 
                    LogHeader, objectId, record.OriginalName, position);

                // Schedule auto-repair if enabled
                if (m_autoRepairEnabled && ShouldAutoRepair(record))
                {
                    ScheduleAutoRepair(record);
                }
            }
        }

        #endregion

        #region Repair Processing

        private void ProcessRepairJobs(object state)
        {
            if (!m_enabled)
                return;

            lock (m_lockObject)
            {
                var completedJobs = new List<uint>();

                foreach (var kvp in m_activeRepairs)
                {
                    var objectId = kvp.Key;
                    var repairJob = kvp.Value;

                    if (DateTime.UtcNow >= repairJob.EstimatedCompletion)
                    {
                        if (CompleteRepair(repairJob))
                        {
                            completedJobs.Add(objectId);
                        }
                        else
                        {
                            // Repair failed, mark as failed and remove
                            repairJob.Status = RepairStatus.Failed;
                            completedJobs.Add(objectId);
                        }
                    }
                }

                // Remove completed jobs
                foreach (var objectId in completedJobs)
                {
                    m_activeRepairs.Remove(objectId);
                }
            }
        }

        private bool CompleteRepair(RepairJob repairJob)
        {
            try
            {
                var record = repairJob.Record;

                // Recreate the object in the scene
                var newObject = RestoreObject(record, repairJob.Options);
                if (newObject == null)
                {
                    m_log.ErrorFormat("{0}: Failed to restore object {1}", LogHeader, record.ObjectId);
                    return false;
                }

                // Re-register with destruction system if needed
                if (repairJob.Options.MakeDestructible)
                {
                    var materialProps = new DestructibleMaterialProperties { MaterialType = record.Material };
                    m_destructionSystem.CreateDestructibleObject(
                        newObject.Name, materialProps, newObject.AbsolutePosition, 
                        newObject.RotationOffset, newObject.Scale, 1000.0f);
                }

                // Remove from destruction history
                lock (m_lockObject)
                {
                    m_destructionHistory.Remove(record.ObjectId);
                }

                m_log.InfoFormat("{0}: Successfully repaired object {1} '{2}'", 
                    LogHeader, record.ObjectId, record.OriginalName);

                return true;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error completing repair for object {1}: {2}", 
                    LogHeader, repairJob.Record.ObjectId, ex.Message);
                return false;
            }
        }

        private SceneObjectPart RestoreObject(DestructionRecord record, RepairOptions options)
        {
            try
            {
                // Create new scene object based on original data
                var primitive = new PrimitiveBaseShape();
                primitive.PCode = (byte)OMV.PCode.Prim; // Default to primitive
                primitive.Scale = record.Scale;

                var newObject = new SceneObjectPart(
                    record.OwnerId, 
                    primitive, 
                    record.Position, 
                    record.Rotation, 
                    OMV.Vector3.Zero);

                newObject.Name = options.PreserveName ? record.OriginalName : $"{record.OriginalName} (Repaired)";
                newObject.Description = record.OriginalDescription;
                newObject.GroupID = record.GroupId;

                // Apply repair quality settings
                if (options.RepairQuality < 1.0f)
                {
                    // Reduce scale slightly for imperfect repairs
                    var qualityScale = 0.9f + (options.RepairQuality * 0.1f);
                    newObject.Scale = record.Scale * qualityScale;
                }

                // Create scene object group
                var group = new SceneObjectGroup(newObject);
                
                // Add to scene
                if (m_scene.AddNewSceneObject(group, true))
                {
                    return newObject;
                }
                else
                {
                    m_log.ErrorFormat("{0}: Failed to add repaired object to scene", LogHeader);
                    return null;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error restoring object: {1}", LogHeader, ex.Message);
                return null;
            }
        }

        #endregion

        #region Helper Methods

        private bool ShouldAutoRepair(DestructionRecord record)
        {
            // Don't auto-repair if object was intentionally destroyed by owner
            if (record.DestroyerId == record.OwnerId && record.Cause == DestructionCause.Manual)
                return false;

            // Don't auto-repair temporary objects
            if (record.OriginalName.ToLower().Contains("temp") || record.OriginalName.ToLower().Contains("test"))
                return false;

            return true;
        }

        private void ScheduleAutoRepair(DestructionRecord record)
        {
            var options = new RepairOptions
            {
                RepairQuality = 0.9f, // Slightly imperfect auto-repairs
                MakeDestructible = true,
                PreserveName = false
            };

            // Schedule repair after default delay with proper error handling
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(m_defaultRepairTime));
                    StartRepair(record.ObjectId, OMV.UUID.Zero, options); // System repair
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("{0}: Error in auto-repair scheduling for object {1}: {2}", 
                        LogHeader, record.ObjectId, ex.Message);
                }
            });
        }

        private bool CheckRepairPermissions(DestructionRecord record, OMV.UUID requesterId)
        {
            // System repairs (UUID.Zero) are always allowed
            if (requesterId == OMV.UUID.Zero)
                return true;

            // Object owner can always repair
            if (requesterId == record.OwnerId)
                return true;

            // Group members can repair if object was group-owned
            if (record.GroupId != OMV.UUID.Zero)
            {
                // TODO: Check group membership
                return true; // Simplified for now
            }

            // Admins can repair anything
            if (IsAdmin(requesterId))
                return true;

            return false;
        }

        private bool CheckRepairResources(DestructionRecord record, OMV.UUID requesterId, RepairOptions options)
        {
            if (m_resourceMode == RepairResourceMode.None)
                return true;

            // TODO: Implement resource checking based on mode
            // For now, always allow repairs
            return true;
        }

        private float CalculateRepairTime(DestructionRecord record, RepairOptions options)
        {
            float baseTime = m_defaultRepairTime;

            // Adjust based on material
            switch (record.Material)
            {
                case DestructibleMaterial.Glass:
                    baseTime *= 0.5f;
                    break;
                case DestructibleMaterial.Metal:
                    baseTime *= 1.5f;
                    break;
                case DestructibleMaterial.Stone:
                    baseTime *= 2.0f;
                    break;
                case DestructibleMaterial.Concrete:
                    baseTime *= 1.8f;
                    break;
            }

            // Adjust based on repair quality
            baseTime *= options.RepairQuality;

            // Adjust based on object size
            var volume = record.Scale.X * record.Scale.Y * record.Scale.Z;
            baseTime *= Math.Max(0.5f, Math.Min(3.0f, volume / 8.0f)); // Normalize around 2x2x2

            return Math.Max(5.0f, baseTime);
        }

        private bool IsAdmin(OMV.UUID userId)
        {
            // TODO: Check if user is admin/estate manager
            return false; // Simplified for now
        }

        private Dictionary<string, object> CaptureObjectData(SceneObjectPart obj)
        {
            // Capture essential object data for restoration
            return new Dictionary<string, object>
            {
                ["Name"] = obj.Name,
                ["Description"] = obj.Description,
                ["Position"] = obj.AbsolutePosition,
                ["Rotation"] = obj.RotationOffset,
                ["Scale"] = obj.Scale,
                ["OwnerID"] = obj.OwnerID,
                ["GroupID"] = obj.GroupID,
                ["CreationDate"] = obj.CreationDate,
                ["Material"] = obj.Material
            };
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            // Stop timers first to prevent callbacks during disposal
            m_repairTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            m_persistenceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            
            // Unsubscribe from events to prevent memory leaks
            if (m_destructionSystem != null)
            {
                m_destructionSystem.OnObjectDestroyed -= OnObjectDestroyed;
            }
            
            // Final save before disposal (synchronous for safety)
            try
            {
                SavePersistentDataAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during final save: {1}", LogHeader, ex.Message);
            }
            
            m_repairTimer?.Dispose();
            m_persistenceTimer?.Dispose();
            m_persistence?.Dispose();

            m_log.InfoFormat("{0}: Destruction repair system disposed", LogHeader);
        }

        #endregion

        #region Persistence Methods

        private async Task LoadPersistentDataAsync()
        {
            try
            {
                var loadedRecords = await m_persistence.LoadDestructionRecordsAsync();
                var loadedRepairs = await m_persistence.LoadRepairJobsAsync();

                lock (m_lockObject)
                {
                    foreach (var kvp in loadedRecords)
                    {
                        m_destructionHistory[kvp.Key] = kvp.Value;
                    }

                    foreach (var kvp in loadedRepairs)
                    {
                        m_activeRepairs[kvp.Key] = kvp.Value;
                    }
                }

                m_log.InfoFormat("{0}: Loaded {1} destruction records and {2} repair jobs from persistent storage",
                    LogHeader, loadedRecords.Count, loadedRepairs.Count);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error loading persistent data: {1}", LogHeader, ex.Message);
            }
        }

        private async Task SavePersistentDataAsync()
        {
            try
            {
                Dictionary<uint, DestructionRecord> recordsCopy;
                Dictionary<uint, RepairJob> repairsCopy;

                lock (m_lockObject)
                {
                    recordsCopy = new Dictionary<uint, DestructionRecord>(m_destructionHistory);
                    repairsCopy = new Dictionary<uint, RepairJob>(m_activeRepairs);
                }

                await m_persistence.SaveDestructionRecordsAsync(recordsCopy);
                await m_persistence.SaveRepairJobsAsync(repairsCopy);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error saving persistent data: {1}", LogHeader, ex.Message);
            }
        }

        private void AutoSavePersistentData(object state)
        {
            if (!m_enabled)
                return;

            Task.Run(async () =>
            {
                try
                {
                    Dictionary<uint, DestructionRecord> recordsCopy;
                    Dictionary<uint, RepairJob> repairsCopy;

                    lock (m_lockObject)
                    {
                        recordsCopy = new Dictionary<uint, DestructionRecord>(m_destructionHistory);
                        repairsCopy = new Dictionary<uint, RepairJob>(m_activeRepairs);
                    }

                    await m_persistence.CheckAutoSave(recordsCopy, repairsCopy);
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("{0}: Error during auto-save: {1}", LogHeader, ex.Message);
                }
            });
        }

        /// <summary>
        /// Clean up old persistent data files
        /// </summary>
        public async Task CleanupOldPersistentData(TimeSpan maxAge)
        {
            try
            {
                await m_persistence.CleanupOldData(maxAge);
                m_log.InfoFormat("{0}: Completed cleanup of old persistent data", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during persistent data cleanup: {1}", LogHeader, ex.Message);
            }
        }

        #endregion
    }

    #region Data Structures

    public class DestructionRecord
    {
        public uint ObjectId { get; set; }
        public string OriginalName { get; set; }
        public string OriginalDescription { get; set; }
        public OMV.Vector3 Position { get; set; }
        public OMV.Quaternion Rotation { get; set; }
        public OMV.Vector3 Scale { get; set; }
        public DestructibleMaterial Material { get; set; }
        public DateTime DestructionTime { get; set; }
        public OMV.UUID DestroyerId { get; set; }
        public DestructionCause Cause { get; set; }
        public OMV.UUID OwnerId { get; set; }
        public OMV.UUID GroupId { get; set; }
        public Dictionary<string, object> OriginalData { get; set; }
    }

    public class RepairJob
    {
        public uint ObjectId { get; set; }
        public OMV.UUID RequesterId { get; set; }
        public DestructionRecord Record { get; set; }
        public RepairOptions Options { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EstimatedCompletion { get; set; }
        public RepairStatus Status { get; set; }
        public float Progress => CalculateProgress();

        private float CalculateProgress()
        {
            var totalTime = (EstimatedCompletion - StartTime).TotalSeconds;
            var elapsedTime = (DateTime.UtcNow - StartTime).TotalSeconds;
            return Math.Min(1.0f, (float)(elapsedTime / totalTime));
        }
    }

    public class RepairOptions
    {
        public float RepairQuality { get; set; } = 1.0f; // 0.0 to 1.0
        public bool MakeDestructible { get; set; } = true;
        public bool PreserveName { get; set; } = true;
        public RepairResourceMode ResourceMode { get; set; } = RepairResourceMode.None;
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    public enum RepairStatus
    {
        NotDestroyed,
        AwaitingRepair,
        InProgress,
        Completed,
        Failed,
        Cancelled
    }

    public enum RepairResourceMode
    {
        None,           // No resources required
        Material,       // Requires materials based on object type
        Energy,         // Requires energy/power
        Both            // Requires both materials and energy
    }


    #endregion
}