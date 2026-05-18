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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using log4net;
using OpenSim.Framework;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Handles persistence of destruction records and repair jobs across server restarts
    /// </summary>
    public class DestructionPersistence : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION PERSISTENCE]";

        private readonly string m_regionName;
        private readonly string m_dataDirectory;
        private readonly string m_destructionRecordsFile;
        private readonly string m_repairJobsFile;
        private readonly object m_fileLock = new object();

        private bool m_enabled = true;
        private TimeSpan m_saveInterval = TimeSpan.FromMinutes(5);
        private DateTime m_lastSave = DateTime.MinValue;

        public DestructionPersistence(string regionName, string dataDirectory = null)
        {
            m_regionName = regionName ?? "Unknown";
            
            // Use provided directory or default to ../data/DestructionRecords/
            m_dataDirectory = dataDirectory ?? Path.Combine(
                Directory.GetCurrentDirectory(), "..", "data", "DestructionRecords");
            
            // Ensure directory exists
            if (!Directory.Exists(m_dataDirectory))
            {
                Directory.CreateDirectory(m_dataDirectory);
            }

            // Set file paths
            m_destructionRecordsFile = Path.Combine(m_dataDirectory, $"{m_regionName}_destructions.json");
            m_repairJobsFile = Path.Combine(m_dataDirectory, $"{m_regionName}_repairs.json");

            m_log.InfoFormat("{0}: Destruction persistence initialized for region {1}", 
                LogHeader, m_regionName);
            m_log.InfoFormat("{0}: Data directory: {1}", LogHeader, m_dataDirectory);
        }

        #region Public Interface

        /// <summary>
        /// Enable or disable persistence
        /// </summary>
        public bool Enabled
        {
            get => m_enabled;
            set => m_enabled = value;
        }

        /// <summary>
        /// How often to auto-save data
        /// </summary>
        public TimeSpan SaveInterval
        {
            get => m_saveInterval;
            set => m_saveInterval = value;
        }

        /// <summary>
        /// Save destruction records to persistent storage
        /// </summary>
        public async Task SaveDestructionRecordsAsync(Dictionary<uint, DestructionRecord> records)
        {
            if (!m_enabled || records == null)
                return;

            try
            {
                var persistentRecords = records.Values
                    .Where(record => ShouldPersist(record))
                    .Select(record => ToPersistentRecord(record))
                    .ToList();

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var jsonData = JsonSerializer.Serialize(persistentRecords, jsonOptions);

                // Atomic file write using temporary file
                var tempFile = m_destructionRecordsFile + ".tmp";
                lock (m_fileLock)
                {
                    File.WriteAllText(tempFile, jsonData);
                    if (File.Exists(m_destructionRecordsFile))
                    {
                        File.Replace(tempFile, m_destructionRecordsFile, m_destructionRecordsFile + ".bak");
                    }
                    else
                    {
                        File.Move(tempFile, m_destructionRecordsFile);
                    }
                }

                m_lastSave = DateTime.UtcNow;

                m_log.InfoFormat("{0}: Saved {1} destruction records for region {2}", 
                    LogHeader, persistentRecords.Count, m_regionName);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error saving destruction records: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Load destruction records from persistent storage
        /// </summary>
        public async Task<Dictionary<uint, DestructionRecord>> LoadDestructionRecordsAsync()
        {
            var records = new Dictionary<uint, DestructionRecord>();

            if (!m_enabled || !File.Exists(m_destructionRecordsFile))
                return records;

            try
            {
                string jsonData;
                lock (m_fileLock)
                {
                    jsonData = File.ReadAllText(m_destructionRecordsFile);
                }

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var persistentRecords = JsonSerializer.Deserialize<List<PersistentDestructionRecord>>(jsonData, jsonOptions);

                if (persistentRecords != null)
                {
                    foreach (var persistentRecord in persistentRecords)
                    {
                        var record = FromPersistentRecord(persistentRecord);
                        if (record != null)
                        {
                            records[record.ObjectId] = record;
                        }
                    }
                }

                m_log.InfoFormat("{0}: Loaded {1} destruction records for region {2}", 
                    LogHeader, records.Count, m_regionName);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error loading destruction records: {1}", LogHeader, ex.Message);
            }

            return records;
        }

        /// <summary>
        /// Save active repair jobs to persistent storage
        /// </summary>
        public async Task SaveRepairJobsAsync(Dictionary<uint, RepairJob> repairJobs)
        {
            if (!m_enabled || repairJobs == null)
                return;

            try
            {
                var persistentJobs = repairJobs.Values
                    .Where(job => ShouldPersist(job))
                    .Select(job => ToPersistentRepairJob(job))
                    .ToList();

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var jsonData = JsonSerializer.Serialize(persistentJobs, jsonOptions);

                // Atomic file write using temporary file
                var tempFile = m_repairJobsFile + ".tmp";
                lock (m_fileLock)
                {
                    File.WriteAllText(tempFile, jsonData);
                    if (File.Exists(m_repairJobsFile))
                    {
                        File.Replace(tempFile, m_repairJobsFile, m_repairJobsFile + ".bak");
                    }
                    else
                    {
                        File.Move(tempFile, m_repairJobsFile);
                    }
                }

                m_log.InfoFormat("{0}: Saved {1} repair jobs for region {2}", 
                    LogHeader, persistentJobs.Count, m_regionName);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error saving repair jobs: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Load active repair jobs from persistent storage
        /// </summary>
        public async Task<Dictionary<uint, RepairJob>> LoadRepairJobsAsync()
        {
            var repairJobs = new Dictionary<uint, RepairJob>();

            if (!m_enabled || !File.Exists(m_repairJobsFile))
                return repairJobs;

            try
            {
                string jsonData;
                lock (m_fileLock)
                {
                    jsonData = File.ReadAllText(m_repairJobsFile);
                }

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var persistentJobs = JsonSerializer.Deserialize<List<PersistentRepairJob>>(jsonData, jsonOptions);

                if (persistentJobs != null)
                {
                    foreach (var persistentJob in persistentJobs)
                    {
                        var repairJob = FromPersistentRepairJob(persistentJob);
                        if (repairJob != null)
                        {
                            repairJobs[repairJob.ObjectId] = repairJob;
                        }
                    }
                }

                m_log.InfoFormat("{0}: Loaded {1} repair jobs for region {2}", 
                    LogHeader, repairJobs.Count, m_regionName);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error loading repair jobs: {1}", LogHeader, ex.Message);
            }

            return repairJobs;
        }

        /// <summary>
        /// Check if it's time to auto-save and perform save if needed
        /// </summary>
        public async Task<bool> CheckAutoSave(Dictionary<uint, DestructionRecord> records, Dictionary<uint, RepairJob> repairJobs)
        {
            if (!m_enabled)
                return false;

            if (DateTime.UtcNow - m_lastSave < m_saveInterval)
                return false;

            await SaveDestructionRecordsAsync(records);
            await SaveRepairJobsAsync(repairJobs);

            return true;
        }

        /// <summary>
        /// Clean up old data files and records
        /// </summary>
        public async Task CleanupOldData(TimeSpan maxAge)
        {
            if (!m_enabled)
                return;

            try
            {
                // Clean destruction records
                var records = await LoadDestructionRecordsAsync();
                var cleanedRecords = records.Where(kvp => 
                    DateTime.UtcNow - kvp.Value.DestructionTime < maxAge)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                if (cleanedRecords.Count != records.Count)
                {
                    await SaveDestructionRecordsAsync(cleanedRecords);
                    m_log.InfoFormat("{0}: Cleaned up {1} old destruction records", 
                        LogHeader, records.Count - cleanedRecords.Count);
                }

                // Clean repair jobs (remove completed/failed jobs older than maxAge)
                var repairJobs = await LoadRepairJobsAsync();
                var cleanedJobs = repairJobs.Where(kvp => 
                    kvp.Value.Status == RepairStatus.InProgress || 
                    DateTime.UtcNow - kvp.Value.StartTime < maxAge)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                if (cleanedJobs.Count != repairJobs.Count)
                {
                    await SaveRepairJobsAsync(cleanedJobs);
                    m_log.InfoFormat("{0}: Cleaned up {1} old repair jobs", 
                        LogHeader, repairJobs.Count - cleanedJobs.Count);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during cleanup: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Helper Methods

        private bool ShouldPersist(DestructionRecord record)
        {
            // Don't persist very old records
            if (DateTime.UtcNow - record.DestructionTime > TimeSpan.FromDays(7))
                return false;

            // Don't persist test objects
            if (record.OriginalName.ToLower().Contains("test") || 
                record.OriginalName.ToLower().Contains("temp"))
                return false;

            return true;
        }

        private bool ShouldPersist(RepairJob job)
        {
            // Always persist in-progress jobs
            if (job.Status == RepairStatus.InProgress)
                return true;

            // Don't persist old completed/failed jobs
            if (DateTime.UtcNow - job.StartTime > TimeSpan.FromHours(1))
                return false;

            return true;
        }

        private PersistentDestructionRecord ToPersistentRecord(DestructionRecord record)
        {
            return new PersistentDestructionRecord
            {
                ObjectId = record.ObjectId,
                OriginalName = record.OriginalName,
                OriginalDescription = record.OriginalDescription,
                Position = new PersistentVector3 { X = record.Position.X, Y = record.Position.Y, Z = record.Position.Z },
                Rotation = new PersistentQuaternion { X = record.Rotation.X, Y = record.Rotation.Y, Z = record.Rotation.Z, W = record.Rotation.W },
                Scale = new PersistentVector3 { X = record.Scale.X, Y = record.Scale.Y, Z = record.Scale.Z },
                Material = record.Material.ToString(),
                DestructionTime = record.DestructionTime,
                DestroyerId = record.DestroyerId.ToString(),
                Cause = record.Cause.ToString(),
                OwnerId = record.OwnerId.ToString(),
                GroupId = record.GroupId.ToString(),
                OriginalData = record.OriginalData
            };
        }

        private DestructionRecord FromPersistentRecord(PersistentDestructionRecord persistent)
        {
            try
            {
                return new DestructionRecord
                {
                    ObjectId = persistent.ObjectId,
                    OriginalName = persistent.OriginalName,
                    OriginalDescription = persistent.OriginalDescription,
                    Position = new OMV.Vector3(persistent.Position.X, persistent.Position.Y, persistent.Position.Z),
                    Rotation = new OMV.Quaternion(persistent.Rotation.X, persistent.Rotation.Y, persistent.Rotation.Z, persistent.Rotation.W),
                    Scale = new OMV.Vector3(persistent.Scale.X, persistent.Scale.Y, persistent.Scale.Z),
                    Material = Enum.Parse<DestructibleMaterial>(persistent.Material),
                    DestructionTime = persistent.DestructionTime,
                    DestroyerId = OMV.UUID.Parse(persistent.DestroyerId),
                    Cause = Enum.Parse<DestructionCause>(persistent.Cause),
                    OwnerId = OMV.UUID.Parse(persistent.OwnerId),
                    GroupId = OMV.UUID.Parse(persistent.GroupId),
                    OriginalData = persistent.OriginalData ?? new Dictionary<string, object>()
                };
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error converting persistent record: {1}", LogHeader, ex.Message);
                return null;
            }
        }

        private PersistentRepairJob ToPersistentRepairJob(RepairJob job)
        {
            return new PersistentRepairJob
            {
                ObjectId = job.ObjectId,
                RequesterId = job.RequesterId.ToString(),
                StartTime = job.StartTime,
                EstimatedCompletion = job.EstimatedCompletion,
                Status = job.Status.ToString(),
                Record = ToPersistentRecord(job.Record),
                Options = new PersistentRepairOptions
                {
                    RepairQuality = job.Options.RepairQuality,
                    MakeDestructible = job.Options.MakeDestructible,
                    PreserveName = job.Options.PreserveName,
                    ResourceMode = job.Options.ResourceMode.ToString(),
                    CustomOptions = job.Options.CustomOptions
                }
            };
        }

        private RepairJob FromPersistentRepairJob(PersistentRepairJob persistent)
        {
            try
            {
                var record = FromPersistentRecord(persistent.Record);
                if (record == null)
                    return null;

                return new RepairJob
                {
                    ObjectId = persistent.ObjectId,
                    RequesterId = OMV.UUID.Parse(persistent.RequesterId),
                    StartTime = persistent.StartTime,
                    EstimatedCompletion = persistent.EstimatedCompletion,
                    Status = Enum.Parse<RepairStatus>(persistent.Status),
                    Record = record,
                    Options = new RepairOptions
                    {
                        RepairQuality = persistent.Options.RepairQuality,
                        MakeDestructible = persistent.Options.MakeDestructible,
                        PreserveName = persistent.Options.PreserveName,
                        ResourceMode = Enum.Parse<RepairResourceMode>(persistent.Options.ResourceMode),
                        CustomOptions = persistent.Options.CustomOptions ?? new Dictionary<string, object>()
                    }
                };
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error converting persistent repair job: {1}", LogHeader, ex.Message);
                return null;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            // Perform final save on disposal
            try
            {
                // Note: Cannot use async in Dispose, but this is final cleanup
                m_log.InfoFormat("{0}: Destruction persistence disposed for region {1}", LogHeader, m_regionName);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion
    }

    #region Persistent Data Structures

    public class PersistentDestructionRecord
    {
        public uint ObjectId { get; set; }
        public string OriginalName { get; set; }
        public string OriginalDescription { get; set; }
        public PersistentVector3 Position { get; set; }
        public PersistentQuaternion Rotation { get; set; }
        public PersistentVector3 Scale { get; set; }
        public string Material { get; set; }
        public DateTime DestructionTime { get; set; }
        public string DestroyerId { get; set; }
        public string Cause { get; set; }
        public string OwnerId { get; set; }
        public string GroupId { get; set; }
        public Dictionary<string, object> OriginalData { get; set; }
    }

    public class PersistentRepairJob
    {
        public uint ObjectId { get; set; }
        public string RequesterId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EstimatedCompletion { get; set; }
        public string Status { get; set; }
        public PersistentDestructionRecord Record { get; set; }
        public PersistentRepairOptions Options { get; set; }
    }

    public class PersistentRepairOptions
    {
        public float RepairQuality { get; set; }
        public bool MakeDestructible { get; set; }
        public bool PreserveName { get; set; }
        public string ResourceMode { get; set; }
        public Dictionary<string, object> CustomOptions { get; set; }
    }

    public class PersistentVector3
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    public class PersistentQuaternion
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }
    }

    #endregion
}