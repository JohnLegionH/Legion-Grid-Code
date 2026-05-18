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
using System.Data;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    /// <summary>
    /// Modernized MySQL generic table handler with connection pooling and async support.
    /// Provides backward compatibility with existing MySQLGenericTableHandler while
    /// offering significant performance improvements through connection pooling.
    /// </summary>
    public class MySQLGenericTableHandlerModern<T> : MySQLFrameworkModern where T: class, new()
    {
        private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MYSQL MODERN TABLE]";

        protected Dictionary<string, FieldInfo> m_Fields = new Dictionary<string, FieldInfo>();
        protected List<string> m_ColumnNames = null;
        protected string m_Realm;
        protected FieldInfo m_DataField = null;
        protected string m_TableName;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        /// <summary>
        /// Constructor for transaction-based operations
        /// </summary>
        public MySQLGenericTableHandlerModern(MySqlTransaction trans, string realm, string storeName) 
            : base(trans)
        {
            m_Realm = realm;
            m_TableName = realm;
            CommonConstruct(storeName);
        }

        /// <summary>
        /// Constructor for connection pooling operations
        /// </summary>
        public MySQLGenericTableHandlerModern(string connectionString, string realm, string storeName) 
            : base(connectionString)
        {
            m_Realm = realm;
            m_TableName = realm;
            CommonConstruct(storeName);
        }

        /// <summary>
        /// Common construction logic with modern connection handling
        /// </summary>
        protected void CommonConstruct(string storeName)
        {
            // Handle database migrations with modern connection pooling
            if (!string.IsNullOrEmpty(storeName))
            {
                // Use connection pool for migrations
                using var connection = m_connectionPool.GetConnection();
                Migration m = new Migration(connection, Assembly, storeName);
                m.Update();
            }

            // Initialize field mappings using reflection
            Type t = typeof(T);
            FieldInfo[] fields = t.GetFields(BindingFlags.Public |
                                           BindingFlags.Instance |
                                           BindingFlags.DeclaredOnly);

            if (fields.Length == 0)
                return;

            foreach (FieldInfo f in fields)
            {
                if (f.Name != "Data")
                    m_Fields[f.Name] = f;
                else
                    m_DataField = f;
            }

            m_log.DebugFormat("{0}: Initialized table handler for {1} with {2} fields", 
                LogHeader, typeof(T).Name, m_Fields.Count);
        }

        /// <summary>
        /// Check and cache column names from database schema
        /// </summary>
        private void CheckColumnNames(IDataReader reader)
        {
            if (m_ColumnNames != null)
                return;

            List<string> columnNames = new List<string>();

            DataTable schemaTable = reader.GetSchemaTable();
            foreach (DataRow row in schemaTable.Rows)
            {
                if (row["ColumnName"] != null &&
                    (!row["IsHidden"].Equals(true)))
                {
                    columnNames.Add(row["ColumnName"].ToString());
                }
            }

            m_ColumnNames = columnNames;
        }

        /// <summary>
        /// Store a single object in the database
        /// </summary>
        public virtual bool Store(T row)
        {
            return StoreAsync(row).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Store a single object in the database asynchronously
        /// </summary>
        public virtual async Task<bool> StoreAsync(T row)
        {
            if (row == null)
                return false;

            try
            {
                // Build the SQL command for INSERT/UPDATE
                var (insertSQL, updateSQL, insertCommand, updateCommand) = BuildStoreCommands(row);

                // Try UPDATE first
                int rowsAffected = await ExecuteNonQueryAsync(updateCommand);
                
                if (rowsAffected == 0)
                {
                    // No rows updated, try INSERT
                    rowsAffected = await ExecuteNonQueryAsync(insertCommand);
                }

                return rowsAffected > 0;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: Error storing {1}: {2}", LogHeader, typeof(T).Name, e.Message);
                return false;
            }
        }

        /// <summary>
        /// Get multiple objects from database with conditions
        /// </summary>
        public virtual T[] Get(string field, string key)
        {
            return GetAsync(field, key).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Get multiple objects from database with conditions asynchronously
        /// </summary>
        public virtual async Task<T[]> GetAsync(string field, string key)
        {
            try
            {
                string query = $"SELECT * FROM {m_Realm} WHERE `{field}` = ?{field}";
                
                using var cmd = new MySqlCommand(query);
                cmd.Parameters.AddWithValue($"?{field}", key);

                using var reader = await ExecuteReaderAsync(cmd);
                return await ReadMultipleRowsAsync(reader);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: Error getting {1} by {2}={3}: {4}", 
                    LogHeader, typeof(T).Name, field, key, e.Message);
                return new T[0];
            }
        }

        /// <summary>
        /// Get multiple objects with multiple field conditions
        /// </summary>
        public virtual T[] Get(string[] fields, string[] keys)
        {
            return GetAsync(fields, keys).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Get multiple objects with multiple field conditions asynchronously
        /// </summary>
        public virtual async Task<T[]> GetAsync(string[] fields, string[] keys)
        {
            if (fields.Length != keys.Length)
                return new T[0];

            try
            {
                StringBuilder whereClause = new StringBuilder();
                for (int i = 0; i < fields.Length; i++)
                {
                    if (i > 0)
                        whereClause.Append(" AND ");
                    whereClause.Append($"`{fields[i]}` = ?{fields[i]}");
                }

                string query = $"SELECT * FROM {m_Realm} WHERE {whereClause}";
                
                using var cmd = new MySqlCommand(query);
                for (int i = 0; i < fields.Length; i++)
                {
                    cmd.Parameters.AddWithValue($"?{fields[i]}", keys[i]);
                }

                using var reader = await ExecuteReaderAsync(cmd);
                return await ReadMultipleRowsAsync(reader);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: Error getting {1} with multiple conditions: {2}", 
                    LogHeader, typeof(T).Name, e.Message);
                return new T[0];
            }
        }

        /// <summary>
        /// Delete objects from database
        /// </summary>
        public virtual bool Delete(string field, string key)
        {
            return DeleteAsync(field, key).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Delete objects from database asynchronously
        /// </summary>
        public virtual async Task<bool> DeleteAsync(string field, string key)
        {
            try
            {
                string query = $"DELETE FROM {m_Realm} WHERE `{field}` = ?{field}";
                
                using var cmd = new MySqlCommand(query);
                cmd.Parameters.AddWithValue($"?{field}", key);

                int rowsAffected = await ExecuteNonQueryAsync(cmd);
                return rowsAffected > 0;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: Error deleting {1} by {2}={3}: {4}", 
                    LogHeader, typeof(T).Name, field, key, e.Message);
                return false;
            }
        }

        /// <summary>
        /// Get count of records matching condition
        /// </summary>
        public virtual long GetCount(string field, string key)
        {
            return GetCountAsync(field, key).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Get count of records matching condition asynchronously
        /// </summary>
        public virtual async Task<long> GetCountAsync(string field, string key)
        {
            try
            {
                string query = string.IsNullOrEmpty(field) ? 
                    $"SELECT COUNT(*) FROM {m_Realm}" :
                    $"SELECT COUNT(*) FROM {m_Realm} WHERE `{field}` = ?{field}";
                
                using var cmd = new MySqlCommand(query);
                if (!string.IsNullOrEmpty(field))
                {
                    cmd.Parameters.AddWithValue($"?{field}", key);
                }

                var result = await ExecuteScalarAsync(cmd);
                return Convert.ToInt64(result);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: Error counting {1}: {2}", LogHeader, typeof(T).Name, e.Message);
                return 0;
            }
        }

        #region Helper Methods

        /// <summary>
        /// Build INSERT and UPDATE commands for Store operation
        /// </summary>
        protected virtual (string insertSQL, string updateSQL, MySqlCommand insertCmd, MySqlCommand updateCmd) 
            BuildStoreCommands(T row)
        {
            var fields = new List<string>();
            var values = new List<string>();
            var updatePairs = new List<string>();
            var keyFields = new List<string>();

            var insertCmd = new MySqlCommand();
            var updateCmd = new MySqlCommand();

            foreach (var field in m_Fields)
            {
                var fieldName = field.Key;
                var fieldInfo = field.Value;
                var value = fieldInfo.GetValue(row);

                fields.Add($"`{fieldName}`");
                values.Add($"?{fieldName}");
                
                // Add to both commands
                insertCmd.Parameters.AddWithValue($"?{fieldName}", value ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue($"?{fieldName}", value ?? DBNull.Value);

                // Determine if this is a key field (for WHERE clause in UPDATE)
                if (IsKeyField(fieldName))
                {
                    keyFields.Add($"`{fieldName}` = ?{fieldName}");
                }
                else
                {
                    updatePairs.Add($"`{fieldName}` = ?{fieldName}");
                }
            }

            // Handle Data field if it exists
            if (m_DataField != null)
            {
                var dataValue = m_DataField.GetValue(row);
                fields.Add("`Data`");
                values.Add("?Data");
                updatePairs.Add("`Data` = ?Data");
                
                insertCmd.Parameters.AddWithValue("?Data", dataValue ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("?Data", dataValue ?? DBNull.Value);
            }

            // Build SQL statements
            string insertSQL = $"INSERT INTO {m_Realm} ({string.Join(", ", fields)}) VALUES ({string.Join(", ", values)})";
            string updateSQL = keyFields.Count > 0 && updatePairs.Count > 0 ?
                $"UPDATE {m_Realm} SET {string.Join(", ", updatePairs)} WHERE {string.Join(" AND ", keyFields)}" :
                "";

            insertCmd.CommandText = insertSQL;
            updateCmd.CommandText = updateSQL;

            return (insertSQL, updateSQL, insertCmd, updateCmd);
        }

        /// <summary>
        /// Determine if a field is a key field (typically ID fields)
        /// </summary>
        protected virtual bool IsKeyField(string fieldName)
        {
            // Common key field patterns
            return fieldName.Equals("PrincipalID", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("UserID", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("ID", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("UUID", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.EndsWith("ID", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Read multiple rows from a data reader
        /// </summary>
        protected virtual async Task<T[]> ReadMultipleRowsAsync(IDataReader reader)
        {
            var results = new List<T>();
            
            CheckColumnNames(reader);
            
            while (reader.Read())
            {
                T row = new T();
                
                foreach (string name in m_ColumnNames)
                {
                    if (m_Fields.ContainsKey(name))
                    {
                        var fieldInfo = m_Fields[name];
                        var value = reader[name];
                        
                        if (value != DBNull.Value)
                        {
                            if (fieldInfo.FieldType == typeof(bool))
                            {
                                value = Convert.ToBoolean(value);
                            }
                            else if (fieldInfo.FieldType == typeof(UUID))
                            {
                                value = new UUID(value.ToString());
                            }
                            
                            fieldInfo.SetValue(row, value);
                        }
                    }
                    else if (name == "Data" && m_DataField != null)
                    {
                        m_DataField.SetValue(row, reader[name]);
                    }
                }
                
                results.Add(row);
            }
            
            return results.ToArray();
        }

        #endregion

        #region Performance Monitoring

        /// <summary>
        /// Get performance statistics for this table handler
        /// </summary>
        public PerformanceMetrics GetPerformanceMetrics()
        {
            var poolStats = GetPoolStatistics();
            
            return new PerformanceMetrics
            {
                TableName = m_Realm,
                EntityType = typeof(T).Name,
                PoolEfficiency = poolStats?.PoolEfficiency ?? 0,
                ActiveConnections = poolStats?.ActiveConnections ?? 0,
                AvailableConnections = poolStats?.AvailableConnections ?? 0
            };
        }

        #endregion
    }

    /// <summary>
    /// Performance metrics for table handlers
    /// </summary>
    public class PerformanceMetrics
    {
        public string TableName { get; set; }
        public string EntityType { get; set; }
        public float PoolEfficiency { get; set; }
        public int ActiveConnections { get; set; }
        public int AvailableConnections { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}