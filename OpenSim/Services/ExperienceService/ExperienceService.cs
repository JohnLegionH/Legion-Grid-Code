/*
 * Legion Grid — Experience System
 * ExperienceService.cs — MySQL-backed implementation
 *
 * Place in: OpenSim/Services/ExperienceService/ExperienceService.cs
 *
 * Add to a new project ExperienceService.csproj, or add directly
 * to an existing Services project like InventoryService.
 *
 * Connection string comes from [ExperienceService] in OpenSim.ini:
 *   ConnectionString = "Data Source=localhost;Database=opensim;User ID=root;Password=xxx;"
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.ExperienceService
{
    public class ExperienceService : IExperienceService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly string m_connectionString;

        // Schema bootstrap guard: the idempotent CREATE TABLE IF NOT EXISTS pass runs
        // ONCE PER PROCESS (not once per region), even though a service instance is
        // constructed per region in ExperienceModule.AddRegion.
        private static bool s_schemaEnsured = false;
        private static readonly object s_schemaLock = new object();

        public ExperienceService(string connectionString)
        {
            m_connectionString = connectionString;
            EnsureSchema();
            m_log.Info("[ExperienceService]: Initialized with MySQL backend");
        }

        private MySqlConnection GetConnection()
        {
            var conn = new MySqlConnection(m_connectionString);
            conn.Open();
            return conn;
        }

        // ══════════════════════════════════════════════════════════════════
        // Schema bootstrap (idempotent, once per process)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Create the Experience tables if they don't already exist. Safe to run against a
        /// grid where the tables were created by hand (CREATE TABLE IF NOT EXISTS no-ops).
        /// Runs once per process; failures are logged loudly but do not crash region load.
        /// </summary>
        private void EnsureSchema()
        {
            lock (s_schemaLock)
            {
                if (s_schemaEnsured)
                    return;

                try
                {
                    using (var conn = GetConnection())
                    {
                        foreach (var ddl in SchemaStatements)
                        {
                            using (var cmd = new MySqlCommand(ddl, conn))
                                cmd.ExecuteNonQuery();
                        }
                    }
                    s_schemaEnsured = true;
                    m_log.Info("[ExperienceService]: Schema ensured (8x CREATE TABLE IF NOT EXISTS)");
                }
                catch (Exception e)
                {
                    // Surface the failure in the log; leave the guard false so a later region
                    // add retries. Do NOT rethrow — a schema hiccup must not crash region load.
                    m_log.ErrorFormat("[ExperienceService]: EnsureSchema failed: {0}", e.Message);
                }
            }
        }

        // DDL kept verbatim from the live schema, normalized to IF NOT EXISTS, ENGINE=InnoDB,
        // DEFAULT CHARSET=utf8mb4. The explicit COLLATE is intentionally omitted (the live
        // utf8mb4_0900_ai_ci is MySQL-8-only; letting the server pick its utf8mb4 default
        // keeps a fresh grid portable to MariaDB / older MySQL).
        private static readonly string[] SchemaStatements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS `experiences` (
                `experience_id` char(36) NOT NULL,
                `owner_id` char(36) NOT NULL,
                `group_id` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                `name` varchar(64) NOT NULL,
                `description` varchar(256) NOT NULL DEFAULT '',
                `maturity` tinyint NOT NULL DEFAULT '0',
                `properties` int NOT NULL DEFAULT '1',
                `logo` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                `marketplace` varchar(256) NOT NULL DEFAULT '',
                `slurl` varchar(256) NOT NULL DEFAULT '',
                `created` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`experience_id`),
                KEY `idx_exp_owner` (`owner_id`),
                KEY `idx_exp_name` (`name`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            @"CREATE TABLE IF NOT EXISTS `experience_permissions` (
                `experience_id` char(36) NOT NULL,
                `agent_id` char(36) NOT NULL,
                `granted` tinyint NOT NULL DEFAULT '1',
                `created` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (`experience_id`,`agent_id`),
                KEY `idx_expperm_agent` (`agent_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            @"CREATE TABLE IF NOT EXISTS `experience_keyvalue` (
                `experience_id` char(36) NOT NULL,
                `kv_key` varchar(255) NOT NULL,
                `kv_value` text NOT NULL,
                `created` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`experience_id`,`kv_key`),
                KEY `idx_expkv_experience` (`experience_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            @"CREATE TABLE IF NOT EXISTS `experience_allowed` (
                `region_id` char(36) NOT NULL,
                `experience_id` char(36) NOT NULL,
                PRIMARY KEY (`region_id`,`experience_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            @"CREATE TABLE IF NOT EXISTS `experience_blocked` (
                `region_id` char(36) NOT NULL,
                `experience_id` char(36) NOT NULL,
                PRIMARY KEY (`region_id`,`experience_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            // Region trusted list (EXP-SLICE-0.5, DEC-4/Option A). Structurally identical to
            // experience_allowed / experience_blocked — same char(36) columns, composite PK,
            // InnoDB/utf8mb4 — so the three region lists stay consistent. Additive CREATE IF
            // NOT EXISTS: a live grid is untouched (existing tables not ALTERed); a fresh
            // bootstrap gets it. Enforcement (trusted bypasses per-agent consent) is deferred
            // to the consent slice (DEC-1); this table only stores the list.
            @"CREATE TABLE IF NOT EXISTS `experience_trusted` (
                `region_id` char(36) NOT NULL,
                `experience_id` char(36) NOT NULL,
                PRIMARY KEY (`region_id`,`experience_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            // Per-AGENT experience block list (EXP-SLICE-2 CAP-EPREF, John-approved). The
            // resident's personal ""never this experience"" set, keyed by agent (vs the region
            // experience_blocked table keyed by region). Structurally identical to
            // experience_trusted — same char(36) columns, composite PK, InnoDB/utf8mb4.
            // Additive CREATE IF NOT EXISTS: a live grid is untouched (no ALTER of existing
            // tables); a fresh bootstrap gets it. Backs GetAgentBlockedExperiences and the D1
            // consent Block button.
            @"CREATE TABLE IF NOT EXISTS `experience_agent_blocked` (
                `agent_id` char(36) NOT NULL,
                `experience_id` char(36) NOT NULL,
                PRIMARY KEY (`agent_id`,`experience_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            @"CREATE TABLE IF NOT EXISTS `script_experiences` (
                `item_id` char(36) NOT NULL,
                `experience_id` char(36) NOT NULL,
                `region_id` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                `created` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`item_id`),
                KEY `idx_se_experience` (`experience_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4"
        };

        // ══════════════════════════════════════════════════════════════════
        // Script ↔ Experience association persistence (EXP-PERSIST-1)
        // ══════════════════════════════════════════════════════════════════

        public void SetScriptExperiencePersisted(UUID itemId, UUID experienceId, UUID regionId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(@"
                    INSERT INTO script_experiences (item_id, experience_id, region_id)
                    VALUES (@item, @exp, @region)
                    ON DUPLICATE KEY UPDATE experience_id=VALUES(experience_id), region_id=VALUES(region_id)", conn))
                {
                    cmd.Parameters.AddWithValue("@item", itemId.ToString());
                    cmd.Parameters.AddWithValue("@exp", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@region", regionId.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: SetScriptExperiencePersisted error: {0}", e.Message);
            }
        }

        public UUID GetScriptExperiencePersisted(UUID itemId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT experience_id FROM script_experiences WHERE item_id=@item", conn))
                {
                    cmd.Parameters.AddWithValue("@item", itemId.ToString());
                    var result = cmd.ExecuteScalar();
                    if (result != null && UUID.TryParse(result.ToString(), out UUID expId))
                        return expId;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: GetScriptExperiencePersisted error: {0}", e.Message);
            }
            return UUID.Zero;
        }

        public void RemoveScriptExperiencePersisted(UUID itemId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "DELETE FROM script_experiences WHERE item_id=@item", conn))
                {
                    cmd.Parameters.AddWithValue("@item", itemId.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: RemoveScriptExperiencePersisted error: {0}", e.Message);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Experience CRUD
        // ══════════════════════════════════════════════════════════════════

        public ExperienceInfo GetExperience(UUID experienceId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT * FROM experiences WHERE experience_id = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", experienceId.ToString());
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadExperience(reader);
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: GetExperience error: {0}", e.Message);
            }
            return null;
        }

        public ExperienceInfo GetExperienceByName(string name)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT * FROM experiences WHERE name = @name LIMIT 1", conn))
                {
                    cmd.Parameters.AddWithValue("@name", name);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadExperience(reader);
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: GetExperienceByName error: {0}", e.Message);
            }
            return null;
        }

        public ExperienceInfo CreateExperience(ExperienceInfo info)
        {
            // Check name uniqueness first
            if (!string.IsNullOrEmpty(info.Name))
            {
                var existing = GetExperienceByName(info.Name);
                if (existing != null)
                {
                    m_log.WarnFormat("[ExperienceService]: Experience '{0}' already exists ({1}), returning existing",
                        info.Name, existing.ExperienceId);
                    return existing;
                }
            }

            if (info.ExperienceId == UUID.Zero)
                info.ExperienceId = UUID.Random();

            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(@"
                    INSERT INTO experiences (experience_id, owner_id, group_id, name, description,
                        maturity, properties, logo, marketplace, slurl)
                    VALUES (@eid, @oid, @gid, @name, @desc, @mat, @props, @logo, @mp, @slurl)", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", info.ExperienceId.ToString());
                    cmd.Parameters.AddWithValue("@oid", info.OwnerId.ToString());
                    cmd.Parameters.AddWithValue("@gid", info.GroupId.ToString());
                    cmd.Parameters.AddWithValue("@name", info.Name ?? string.Empty);
                    cmd.Parameters.AddWithValue("@desc", info.Description ?? string.Empty);
                    cmd.Parameters.AddWithValue("@mat", info.Maturity);
                    cmd.Parameters.AddWithValue("@props", info.Properties);
                    cmd.Parameters.AddWithValue("@logo", info.Logo.ToString());
                    cmd.Parameters.AddWithValue("@mp", info.Marketplace ?? string.Empty);
                    cmd.Parameters.AddWithValue("@slurl", info.Slurl ?? string.Empty);
                    cmd.ExecuteNonQuery();
                }
                m_log.InfoFormat("[ExperienceService]: Created experience '{0}' ({1})", info.Name, info.ExperienceId);
                return info;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: CreateExperience error: {0}", e.Message);
                return null;
            }
        }

        public bool UpdateExperience(ExperienceInfo info)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(@"
                    UPDATE experiences SET owner_id=@oid, group_id=@gid, name=@name,
                        description=@desc, maturity=@mat, properties=@props, logo=@logo,
                        marketplace=@mp, slurl=@slurl
                    WHERE experience_id=@eid", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", info.ExperienceId.ToString());
                    cmd.Parameters.AddWithValue("@oid", info.OwnerId.ToString());
                    cmd.Parameters.AddWithValue("@gid", info.GroupId.ToString());
                    cmd.Parameters.AddWithValue("@name", info.Name ?? string.Empty);
                    cmd.Parameters.AddWithValue("@desc", info.Description ?? string.Empty);
                    cmd.Parameters.AddWithValue("@mat", info.Maturity);
                    cmd.Parameters.AddWithValue("@props", info.Properties);
                    cmd.Parameters.AddWithValue("@logo", info.Logo.ToString());
                    cmd.Parameters.AddWithValue("@mp", info.Marketplace ?? string.Empty);
                    cmd.Parameters.AddWithValue("@slurl", info.Slurl ?? string.Empty);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: UpdateExperience error: {0}", e.Message);
                return false;
            }
        }

        public bool DeleteExperience(UUID experienceId)
        {
            try
            {
                using (var conn = GetConnection())
                {
                    // Delete KV data, permissions, and the experience itself
                    using (var cmd = new MySqlCommand("DELETE FROM experience_keyvalue WHERE experience_id=@eid", conn))
                    {
                        cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new MySqlCommand("DELETE FROM experience_permissions WHERE experience_id=@eid", conn))
                    {
                        cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new MySqlCommand("DELETE FROM experience_allowed WHERE experience_id=@eid", conn))
                    {
                        cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new MySqlCommand("DELETE FROM experience_blocked WHERE experience_id=@eid", conn))
                    {
                        cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new MySqlCommand("DELETE FROM experiences WHERE experience_id=@eid", conn))
                    {
                        cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                        return cmd.ExecuteNonQuery() > 0;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: DeleteExperience error: {0}", e.Message);
                return false;
            }
        }

        public bool IsExperienceContributor(UUID experienceId, UUID agentId)
        {
            // Owner-only: an agent may contribute scripts to experiences they OWN. This matches
            // the owner-only GetCreatorExperiences list the viewer populates the "Use Experience"
            // dropdown from — so the agent can only pick, and thus only associate, experiences
            // they own. The group-ExperienceCreator union (for group-owned experiences) needs
            // group-power access (module/client layer, not this data service) and lands in Slice 4
            // alongside GetCreatorExperiences' group union — deferred, NOT stubbed-false.
            if (agentId.IsZero()) return false;
            ExperienceInfo info = GetExperience(experienceId);
            return info != null && info.OwnerId == agentId;
        }

        public bool IsExperienceAdmin(UUID experienceId, UUID agentId)
        {
            // Owner-only at the data layer — EXACTLY parallel to IsExperienceContributor above.
            // The group GP_EXPERIENCE_ADMIN (bit 49) union needs group-power access (the groups
            // module / client layer, not this data service), so the module's admin gate ORs this
            // owner check with the group-power check. See ExperienceModule.IsAgentExperienceAdmin.
            if (agentId.IsZero()) return false;
            ExperienceInfo info = GetExperience(experienceId);
            return info != null && info.OwnerId == agentId;
        }

        public List<ExperienceInfo> GetExperiencesByOwner(UUID ownerId)
        {
            var results = new List<ExperienceInfo>();
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand("SELECT * FROM experiences WHERE owner_id=@oid", conn))
                {
                    cmd.Parameters.AddWithValue("@oid", ownerId.ToString());
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadExperience(reader));
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: GetExperiencesByOwner error: {0}", e.Message);
            }
            return results;
        }

        public List<ExperienceInfo> FindExperiences(string query)
        {
            // Legacy unpaged form — first 50 matches, as before.
            return FindExperiences(query, 0, 50);
        }

        public List<ExperienceInfo> FindExperiences(string query, int offset, int limit)
        {
            var results = new List<ExperienceInfo>();
            if (offset < 0) offset = 0;
            if (limit <= 0) return results;
            try
            {
                using (var conn = GetConnection())
                // ORDER BY is load-bearing: without a stable order, LIMIT windows can
                // overlap or skip rows between pages. experience_id breaks name ties.
                using (var cmd = new MySqlCommand(
                    "SELECT * FROM experiences WHERE name LIKE @q ORDER BY name, experience_id LIMIT @off, @lim", conn))
                {
                    cmd.Parameters.AddWithValue("@q", "%" + (query ?? "") + "%");
                    cmd.Parameters.AddWithValue("@off", offset);
                    cmd.Parameters.AddWithValue("@lim", limit);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(ReadExperience(reader));
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: FindExperiences error: {0}", e.Message);
            }
            return results;
        }

        // ══════════════════════════════════════════════════════════════════
        // Permission Grants
        // ══════════════════════════════════════════════════════════════════

        public bool IsAgentGranted(UUID experienceId, UUID agentId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT granted FROM experience_permissions WHERE experience_id=@eid AND agent_id=@aid", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    var result = cmd.ExecuteScalar();
                    return result != null && Convert.ToInt32(result) == 1;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: IsAgentGranted error: {0}", e.Message);
                return false;
            }
        }

        public bool IsAgentBlocked(UUID experienceId, UUID agentId)
        {
            // The agent's personal block now lives in the dedicated experience_agent_blocked
            // table (John-approved), NOT experience_permissions.granted=0 — the block list is
            // decoupled from grant records.
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT 1 FROM experience_agent_blocked WHERE agent_id=@aid AND experience_id=@eid", conn))
                {
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    return cmd.ExecuteScalar() != null;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: IsAgentBlocked error: {0}", e.Message);
                return false;
            }
        }

        public bool GrantPermission(UUID experienceId, UUID agentId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(@"
                    INSERT INTO experience_permissions (experience_id, agent_id, granted)
                    VALUES (@eid, @aid, 1)
                    ON DUPLICATE KEY UPDATE granted=1", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: GrantPermission error: {0}", e.Message);
                return false;
            }
        }

        public bool DenyPermission(UUID experienceId, UUID agentId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(@"
                    INSERT INTO experience_permissions (experience_id, agent_id, granted)
                    VALUES (@eid, @aid, 0)
                    ON DUPLICATE KEY UPDATE granted=0", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: DenyPermission error: {0}", e.Message);
                return false;
            }
        }

        public bool ForgetPermission(UUID experienceId, UUID agentId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "DELETE FROM experience_permissions WHERE experience_id=@eid AND agent_id=@aid", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: ForgetPermission error: {0}", e.Message);
                return false;
            }
        }

        public List<UUID> GetAgentExperiences(UUID agentId)
        {
            var results = new List<UUID>();
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT experience_id FROM experience_permissions WHERE agent_id=@aid AND granted=1", conn))
                {
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (UUID.TryParse(reader.GetString("experience_id"), out UUID eid))
                                results.Add(eid);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: GetAgentExperiences error: {0}", e.Message);
            }
            return results;
        }

        public List<UUID> GetAgentBlockedExperiences(UUID agentId)
        {
            // The agent's per-agent BLOCKED list from the dedicated experience_agent_blocked
            // table (distinct from the region experience_blocked table). Backs the
            // ExperiencePreferences / GetExperiences "blocked" array.
            var results = new List<UUID>();
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT experience_id FROM experience_agent_blocked WHERE agent_id=@aid", conn))
                {
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (UUID.TryParse(reader.GetString("experience_id"), out UUID eid))
                                results.Add(eid);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: GetAgentBlockedExperiences error: {0}", e.Message);
            }
            return results;
        }

        // Agent-scoped block/unblock — mirrors the region AddRegionExperience/RemoveRegionExperience
        // pattern (INSERT IGNORE / DELETE on a two-column table), but keyed by agent_id instead of
        // region_id. The block list is the resident's personal "never this experience" set.
        public bool BlockExperienceForAgent(UUID agentId, UUID experienceId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "INSERT IGNORE INTO experience_agent_blocked (agent_id, experience_id) VALUES (@aid, @eid)", conn))
                {
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: BlockExperienceForAgent error: {0}", e.Message);
                return false;
            }
        }

        public bool UnblockExperienceForAgent(UUID agentId, UUID experienceId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "DELETE FROM experience_agent_blocked WHERE agent_id=@aid AND experience_id=@eid", conn))
                {
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: UnblockExperienceForAgent error: {0}", e.Message);
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Key-Value Store
        // ══════════════════════════════════════════════════════════════════

        public string ReadKeyValue(UUID experienceId, string key)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT kv_value FROM experience_keyvalue WHERE experience_id=@eid AND kv_key=@key", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@key", key);
                    var result = cmd.ExecuteScalar();
                    return result?.ToString();
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: ReadKeyValue error: {0}", e.Message);
                return null;
            }
        }

        public bool CreateKeyValue(UUID experienceId, string key, string value)
        {
            if (string.IsNullOrEmpty(key) || key.Length > ExperienceInfo.MAX_KEY_LENGTH)
                return false;
            if (value != null && value.Length > ExperienceInfo.MAX_VALUE_LENGTH)
                return false;

            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(@"
                    INSERT INTO experience_keyvalue (experience_id, kv_key, kv_value)
                    VALUES (@eid, @key, @val)", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.Parameters.AddWithValue("@val", value ?? string.Empty);
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (MySqlException ex) when (ex.Number == 1062) // Duplicate key
            {
                return false;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: CreateKeyValue error: {0}", e.Message);
                return false;
            }
        }

        public bool UpdateKeyValue(UUID experienceId, string key, string value, string check)
        {
            if (string.IsNullOrEmpty(key) || key.Length > ExperienceInfo.MAX_KEY_LENGTH)
                return false;
            if (value != null && value.Length > ExperienceInfo.MAX_VALUE_LENGTH)
                return false;

            try
            {
                using (var conn = GetConnection())
                {
                    string sql;
                    if (!string.IsNullOrEmpty(check))
                    {
                        // Conditional update — only if current value matches check
                        sql = @"UPDATE experience_keyvalue SET kv_value=@val
                                WHERE experience_id=@eid AND kv_key=@key AND kv_value=@check";
                    }
                    else
                    {
                        // Unconditional update (or insert)
                        sql = @"INSERT INTO experience_keyvalue (experience_id, kv_key, kv_value)
                                VALUES (@eid, @key, @val)
                                ON DUPLICATE KEY UPDATE kv_value=@val";
                    }

                    using (var cmd = new MySqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                        cmd.Parameters.AddWithValue("@key", key);
                        cmd.Parameters.AddWithValue("@val", value ?? string.Empty);
                        if (!string.IsNullOrEmpty(check))
                            cmd.Parameters.AddWithValue("@check", check);
                        return cmd.ExecuteNonQuery() > 0;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: UpdateKeyValue error: {0}", e.Message);
                return false;
            }
        }

        public bool DeleteKeyValue(UUID experienceId, string key)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "DELETE FROM experience_keyvalue WHERE experience_id=@eid AND kv_key=@key", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@key", key);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: DeleteKeyValue error: {0}", e.Message);
                return false;
            }
        }

        public int KeyCountKeyValue(UUID experienceId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM experience_keyvalue WHERE experience_id=@eid", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: KeyCountKeyValue error: {0}", e.Message);
                return 0;
            }
        }

        public List<string> KeysKeyValue(UUID experienceId, int start, int count)
        {
            var results = new List<string>();
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT kv_key FROM experience_keyvalue WHERE experience_id=@eid ORDER BY kv_key LIMIT @start, @count", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@start", Math.Max(0, start));
                    cmd.Parameters.AddWithValue("@count", Math.Clamp(count, 1, 1000));
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            results.Add(reader.GetString(0));
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: KeysKeyValue error: {0}", e.Message);
            }
            return results;
        }

        public long DataSizeKeyValue(UUID experienceId)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(@"
                    SELECT COALESCE(SUM(LENGTH(kv_key) + LENGTH(kv_value)), 0)
                    FROM experience_keyvalue WHERE experience_id=@eid", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: DataSizeKeyValue error: {0}", e.Message);
                return 0;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Region Allow/Block Lists
        // ══════════════════════════════════════════════════════════════════

        public List<UUID> GetAllowedExperiences(UUID regionId)
        {
            return GetRegionExperienceList(regionId, "experience_allowed");
        }

        public List<UUID> GetBlockedExperiences(UUID regionId)
        {
            return GetRegionExperienceList(regionId, "experience_blocked");
        }

        public List<UUID> GetTrustedExperiences(UUID regionId)
        {
            return GetRegionExperienceList(regionId, "experience_trusted");
        }

        public bool AllowExperience(UUID regionId, UUID experienceId)
        {
            // Remove from blocked if present, add to allowed. Allowed and trusted are the
            // "permit" family (both let the experience run) and may coexist, so allowing does
            // NOT clear trusted — only the contradictory blocked state is cleared.
            RemoveRegionExperience(regionId, experienceId, "experience_blocked");
            return AddRegionExperience(regionId, experienceId, "experience_allowed");
        }

        public bool RemoveAllowedExperience(UUID regionId, UUID experienceId)
        {
            return RemoveRegionExperience(regionId, experienceId, "experience_allowed");
        }

        public bool BlockExperience(UUID regionId, UUID experienceId)
        {
            // Block is the absolute deny state: it must clear BOTH permit-family lists
            // (allowed AND trusted) so a blocked experience can never remain permitted.
            RemoveRegionExperience(regionId, experienceId, "experience_allowed");
            RemoveRegionExperience(regionId, experienceId, "experience_trusted");
            return AddRegionExperience(regionId, experienceId, "experience_blocked");
        }

        public bool RemoveBlockedExperience(UUID regionId, UUID experienceId)
        {
            return RemoveRegionExperience(regionId, experienceId, "experience_blocked");
        }

        public bool TrustExperience(UUID regionId, UUID experienceId)
        {
            // Trusted is a stronger allow; it must NOT coexist with blocked (contradiction),
            // so trusting clears the blocked state. It does not clear allowed — {allowed,
            // trusted} are compatible permit states (see AllowExperience).
            RemoveRegionExperience(regionId, experienceId, "experience_blocked");
            return AddRegionExperience(regionId, experienceId, "experience_trusted");
        }

        public bool RemoveTrustedExperience(UUID regionId, UUID experienceId)
        {
            return RemoveRegionExperience(regionId, experienceId, "experience_trusted");
        }

        // ══════════════════════════════════════════════════════════════════
        // Private Helpers
        // ══════════════════════════════════════════════════════════════════

        private ExperienceInfo ReadExperience(MySqlDataReader reader)
        {
            return new ExperienceInfo
            {
                ExperienceId = UUID.Parse(reader.GetString("experience_id")),
                OwnerId = UUID.Parse(reader.GetString("owner_id")),
                GroupId = UUID.Parse(reader.GetString("group_id")),
                Name = reader.GetString("name"),
                Description = reader.GetString("description"),
                Maturity = reader.GetInt32("maturity"),
                Properties = reader.GetInt32("properties"),
                Logo = UUID.Parse(reader.GetString("logo")),
                Marketplace = reader.GetString("marketplace"),
                Slurl = reader.GetString("slurl"),
                Created = reader.GetDateTime("created"),
                Updated = reader.GetDateTime("updated")
            };
        }

        private List<UUID> GetRegionExperienceList(UUID regionId, string table)
        {
            var results = new List<UUID>();
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    $"SELECT experience_id FROM {table} WHERE region_id=@rid", conn))
                {
                    cmd.Parameters.AddWithValue("@rid", regionId.ToString());
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (UUID.TryParse(reader.GetString(0), out UUID eid))
                                results.Add(eid);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: GetRegionExperienceList error: {0}", e.Message);
            }
            return results;
        }

        private bool AddRegionExperience(UUID regionId, UUID experienceId, string table)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    $"INSERT IGNORE INTO {table} (region_id, experience_id) VALUES (@rid, @eid)", conn))
                {
                    cmd.Parameters.AddWithValue("@rid", regionId.ToString());
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: AddRegionExperience error: {0}", e.Message);
                return false;
            }
        }

        private bool RemoveRegionExperience(UUID regionId, UUID experienceId, string table)
        {
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    $"DELETE FROM {table} WHERE region_id=@rid AND experience_id=@eid", conn))
                {
                    cmd.Parameters.AddWithValue("@rid", regionId.ToString());
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[ExperienceService]: RemoveRegionExperience error: {0}", e.Message);
                return false;
            }
        }
    }
}
