/*
 * Legion Grid — Phlox Script Engine
 * StateManager.cs — Script runtime state persistence
 *
 * Ported from halcyon-reference/InWorldz/InWorldz.Phlox.Engine/StateManager.cs
 * Adapted for .NET 8: System.Data.SQLite → Microsoft.Data.Sqlite
 *                     IndexedPriorityQueue → SortedDictionary
 *                     ThreadTracker → plain Thread
 *
 * Saves/restores LSL global variable state, current LSL state name,
 * timer interval, and event queue across region restarts.
 *
 * Concurrency model (see the "state-save race" fix):
 *   Serialization of a script's RuntimeState happens ONLY on the scheduler
 *   thread, at a safe point where the script is quiescent (between bytecode
 *   ops, event queue drained) — i.e. from ScriptChanged/ScriptUnloaded, which
 *   the scheduler calls at its run-state transitions. At that instant the
 *   operand/call stacks are not being mutated, so the serialized blob is
 *   internally consistent WITHOUT any lock on the VM state. The finished
 *   byte[] blob is queued; the background thread does ONLY the SQLite write of
 *   pre-made blobs and never touches live RuntimeState. This is the same
 *   approach the event-queue race fix (commit 85ad91cdf7) used for one
 *   collection, generalized to all VM stacks.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using log4net;
using Microsoft.Data.Sqlite;
using OpenMetaverse;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Serialization;

namespace Phlox.ScriptEngine
{
    internal class StateManager : IDisposable
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string DB_DIR  = "ScriptEngines/Phlox/state";
        private const string DB_FILE = "ScriptEngines/Phlox/state/script_state.db";
        private const int FLUSH_INTERVAL_MS = 2500;

        private readonly PhloxEngine m_Engine;

        // item_id -> latest pre-serialized blob awaiting a disk write. Guarded by
        // m_Lock. Capture (scheduler thread) overwrites; flush (bg thread) drains.
        private readonly Dictionary<UUID, PendingSave> m_Pending = new Dictionary<UUID, PendingSave>();
        private readonly object m_Lock = new object();

        // Serializes the actual SQLite writes so the background flush and a
        // scheduler-thread unload write never collide (SQLite allows one writer).
        private readonly object m_DbLock = new object();

        private Thread m_Thread;
        private volatile bool m_Stop;
        private readonly ManualResetEventSlim m_WakeEvent = new ManualResetEventSlim(false);

        private sealed class PendingSave
        {
            public UUID ItemId;
            public UUID AssetId;
            public byte[] Blob;
        }

        public StateManager(PhloxEngine engine)
        {
            m_Engine = engine;
            EnsureDatabase();
        }

        public void Start()
        {
            m_Thread = new Thread(FlushLoop)
            {
                Name = "PhloxStateManager",
                IsBackground = true,
                Priority = ThreadPriority.Lowest
            };
            m_Thread.Start();
        }

        public void Stop()
        {
            m_Stop = true;
            m_WakeEvent.Set();
            m_Thread?.Join(5000);
            FlushPending();
        }

        public void Dispose()
        {
            if (!m_Stop) Stop();
            m_WakeEvent.Dispose();
        }

        /// <summary>
        /// Called on the SCHEDULER thread when a script reaches a safe point
        /// (quiescent — event handler finished, stacks at a clean boundary).
        /// Serializes the consistent state to a blob NOW and queues it for the
        /// background writer. Race-free by construction: nothing is mutating the
        /// VM stacks at this instant.
        /// </summary>
        public void ScriptChanged(Interpreter interp)
        {
            byte[] blob;
            try
            {
                blob = SerializeState(interp);
            }
            catch (Exception e)
            {
                // No masking of a race here — serialization runs on stable state,
                // so a throw is a genuine bug, logged loudly, not silently skipped.
                m_log.WarnFormat("[PhloxState]: Failed to serialize {0}: {1}", interp.ItemId, e.Message);
                return;
            }

            lock (m_Lock)
            {
                m_Pending[interp.ItemId] = new PendingSave
                {
                    ItemId  = interp.ItemId,
                    AssetId = interp.Script.AssetId,
                    Blob    = blob
                };
            }
            m_WakeEvent.Set();
        }

        /// <summary>
        /// Final save at unload (scheduler thread, safe point). Serializes the
        /// last state and writes it synchronously so it is persisted before the
        /// script object goes away.
        /// </summary>
        public void ScriptUnloaded(Interpreter interp)
        {
            PendingSave save;
            try
            {
                save = new PendingSave
                {
                    ItemId  = interp.ItemId,
                    AssetId = interp.Script.AssetId,
                    Blob    = SerializeState(interp)
                };
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[PhloxState]: Failed to serialize {0} at unload: {1}", interp.ItemId, e.Message);
                return;
            }

            // Drop any queued (now-superseded) blob and write the final one.
            lock (m_Lock)
                m_Pending.Remove(interp.ItemId);

            WriteBatch(new List<PendingSave> { save });
        }

        /// <summary>
        /// Loads saved state for a script, validating that the asset ID matches.
        /// Returns null if no state exists or the script has been modified since last save.
        /// </summary>
        public SerializedRuntimeState LoadState(UUID itemId, UUID assetId)
        {
            try
            {
                using var conn = OpenConnection();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText = "SELECT asset_id, state_data FROM script_state WHERE item_id = @id";
                cmd.Parameters.AddWithValue("@id", itemId.ToString());

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                string savedAssetId = reader.GetString(0);
                if (savedAssetId != assetId.ToString())
                {
                    m_log.DebugFormat("[PhloxState]: Discarding stale state for {0} (saved asset {1}, current {2})",
                        itemId, savedAssetId, assetId);
                    return null;
                }

                byte[] blob = (byte[])reader[1];
                using var ms = new MemoryStream(blob);
                return ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[PhloxState]: Failed to load state for {0}: {1}", itemId, e.Message);
                return null;
            }
        }

        public void DeleteState(UUID itemId)
        {
            // Drop any queued blob first so a pending write can't resurrect the
            // state we are about to delete.
            lock (m_Lock)
                m_Pending.Remove(itemId);

            try
            {
                lock (m_DbLock)
                {
                    using var conn = OpenConnection();
                    using var cmd  = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM script_state WHERE item_id = @id";
                    cmd.Parameters.AddWithValue("@id", itemId.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[PhloxState]: Failed to delete state for {0}: {1}", itemId, e.Message);
            }
        }

        private void FlushLoop()
        {
            while (!m_Stop)
            {
                m_WakeEvent.Wait(FLUSH_INTERVAL_MS);
                m_WakeEvent.Reset();
                if (m_Stop) break;
                FlushPending();
            }
        }

        private void FlushPending()
        {
            List<PendingSave> batch;
            lock (m_Lock)
            {
                if (m_Pending.Count == 0) return;
                batch = new List<PendingSave>(m_Pending.Values);
                m_Pending.Clear();
            }
            WriteBatch(batch);
        }

        private void WriteBatch(List<PendingSave> batch)
        {
            lock (m_DbLock)
            {
                try
                {
                    using var conn = OpenConnection();
                    using var tx   = conn.BeginTransaction();
                    foreach (var save in batch)
                    {
                        try { WriteOne(conn, save); }
                        catch (Exception e)
                        {
                            m_log.WarnFormat("[PhloxState]: Failed to write {0}: {1}", save.ItemId, e.Message);
                        }
                    }
                    tx.Commit();
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[PhloxState]: Batch flush failed: {0}", e.Message);
                }
            }
        }

        private static byte[] SerializeState(Interpreter interp)
        {
            SerializedRuntimeState srs = SerializedRuntimeState.FromRuntimeState(interp.ScriptState);
            using var ms = new MemoryStream();
            ProtoBuf.Serializer.Serialize(ms, srs);
            return ms.ToArray();
        }

        private static void WriteOne(SqliteConnection conn, PendingSave save)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO script_state (item_id, asset_id, state_data, saved_at)
                  VALUES (@id, @assetid, @data, @ts)
                  ON CONFLICT(item_id) DO UPDATE SET
                      asset_id   = excluded.asset_id,
                      state_data = excluded.state_data,
                      saved_at   = excluded.saved_at";
            cmd.Parameters.AddWithValue("@id",      save.ItemId.ToString());
            cmd.Parameters.AddWithValue("@assetid", save.AssetId.ToString());
            cmd.Parameters.AddWithValue("@data",    save.Blob);
            cmd.Parameters.AddWithValue("@ts",      DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }

        private void EnsureDatabase()
        {
            SQLitePCL.Batteries_V2.Init();
            try
            {
                Directory.CreateDirectory(DB_DIR);
                using var conn = OpenConnection();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText =
                    @"CREATE TABLE IF NOT EXISTS script_state (
                        item_id    TEXT    PRIMARY KEY,
                        asset_id   TEXT    NOT NULL DEFAULT '',
                        state_data BLOB    NOT NULL,
                        saved_at   INTEGER NOT NULL
                    )";
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[PhloxState]: Failed to initialize state database: {0}", e.Message);
            }
        }

        private static SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={DB_FILE};Mode=ReadWriteCreate");
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            pragma.ExecuteNonQuery();
            return conn;
        }
    }
}
