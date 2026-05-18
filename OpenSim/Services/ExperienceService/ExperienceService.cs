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

        public ExperienceService(string connectionString)
        {
            m_connectionString = connectionString;
            m_log.Info("[ExperienceService]: Initialized with MySQL backend");
        }

        private MySqlConnection GetConnection()
        {
            var conn = new MySqlConnection(m_connectionString);
            conn.Open();
            return conn;
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
            var results = new List<ExperienceInfo>();
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand("SELECT * FROM experiences WHERE name LIKE @q LIMIT 50", conn))
                {
                    cmd.Parameters.AddWithValue("@q", "%" + (query ?? "") + "%");
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
            try
            {
                using (var conn = GetConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT granted FROM experience_permissions WHERE experience_id=@eid AND agent_id=@aid", conn))
                {
                    cmd.Parameters.AddWithValue("@eid", experienceId.ToString());
                    cmd.Parameters.AddWithValue("@aid", agentId.ToString());
                    var result = cmd.ExecuteScalar();
                    return result != null && Convert.ToInt32(result) == 0;
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

        public bool AllowExperience(UUID regionId, UUID experienceId)
        {
            // Remove from blocked if present, add to allowed
            RemoveRegionExperience(regionId, experienceId, "experience_blocked");
            return AddRegionExperience(regionId, experienceId, "experience_allowed");
        }

        public bool RemoveAllowedExperience(UUID regionId, UUID experienceId)
        {
            return RemoveRegionExperience(regionId, experienceId, "experience_allowed");
        }

        public bool BlockExperience(UUID regionId, UUID experienceId)
        {
            // Remove from allowed if present, add to blocked
            RemoveRegionExperience(regionId, experienceId, "experience_allowed");
            return AddRegionExperience(regionId, experienceId, "experience_blocked");
        }

        public bool RemoveBlockedExperience(UUID regionId, UUID experienceId)
        {
            return RemoveRegionExperience(regionId, experienceId, "experience_blocked");
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
