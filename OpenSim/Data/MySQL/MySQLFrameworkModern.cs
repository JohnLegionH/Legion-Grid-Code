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
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Framework;
using MySql.Data.MySqlClient;

namespace OpenSim.Data.MySQL
{
    /// <summary>
    /// Modernized MySQL framework with connection pooling and async support.
    /// Backward compatible with existing MySqlFramework while providing
    /// significant performance improvements through connection pooling.
    /// </summary>
    public class MySQLFrameworkModern : IDisposable
    {
        private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MYSQL MODERN]";
        
        // Static connection pool shared across all instances
        private static readonly Dictionary<string, MySQLConnectionPool> s_connectionPools = 
            new Dictionary<string, MySQLConnectionPool>();
        private static readonly object s_poolLock = new object();
        
        protected readonly MySQLConnectionPool m_connectionPool;
        protected readonly string m_connectionString;
        protected MySqlTransaction m_trans = null;

        // Constructor using a connection string with connection pooling
        protected MySQLFrameworkModern(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));
                
            m_connectionString = connectionString;
            m_connectionPool = GetOrCreateConnectionPool(connectionString);
        }

        // Constructor using a connection object for transaction support
        protected MySQLFrameworkModern(MySqlTransaction trans)
        {
            m_trans = trans ?? throw new ArgumentNullException(nameof(trans));
            m_connectionString = trans.Connection.ConnectionString;
            m_connectionPool = GetOrCreateConnectionPool(m_connectionString);
        }

        /// <summary>
        /// Get or create a connection pool for the given connection string
        /// </summary>
        private static MySQLConnectionPool GetOrCreateConnectionPool(string connectionString)
        {
            lock (s_poolLock)
            {
                if (!s_connectionPools.TryGetValue(connectionString, out var pool))
                {
                    // Extract pool configuration from connection string or use defaults
                    var maxPoolSize = ExtractPoolSizeFromConnectionString(connectionString, "Maximum Pool Size", 100);
                    var minPoolSize = Math.Min(10, maxPoolSize / 2);
                    
                    pool = new MySQLConnectionPool(connectionString, maxPoolSize, minPoolSize);
                    s_connectionPools[connectionString] = pool;
                    
                    m_log.InfoFormat("{0}: Created new connection pool for database", LogHeader);
                }
                
                return pool;
            }
        }

        /// <summary>
        /// Extract pool size configuration from connection string
        /// </summary>
        private static int ExtractPoolSizeFromConnectionString(string connectionString, string parameter, int defaultValue)
        {
            try
            {
                var builder = new MySqlConnectionStringBuilder(connectionString);
                var value = builder.GetType().GetProperty(parameter.Replace(" ", ""))?.GetValue(builder);
                if (value is uint uintValue)
                    return (int)uintValue;
                if (value is int intValue)
                    return intValue;
            }
            catch
            {
                // Ignore errors, use default
            }
            
            return defaultValue;
        }

        /// <summary>
        /// Execute non-query with modern connection pooling
        /// </summary>
        protected int ExecuteNonQuery(MySqlCommand cmd)
        {
            if (m_trans != null)
            {
                return ExecuteNonQueryWithTransaction(cmd, m_trans);
            }
            
            return m_connectionPool.ExecuteWithConnection(connection =>
            {
                return ExecuteNonQueryWithConnection(cmd, connection);
            });
        }

        /// <summary>
        /// Execute non-query asynchronously with connection pooling
        /// </summary>
        protected async Task<int> ExecuteNonQueryAsync(MySqlCommand cmd)
        {
            if (m_trans != null)
            {
                return ExecuteNonQueryWithTransaction(cmd, m_trans);
            }
            
            return await m_connectionPool.ExecuteWithConnectionAsync(async connection =>
            {
                return await ExecuteNonQueryWithConnectionAsync(cmd, connection);
            });
        }

        /// <summary>
        /// Execute reader with connection pooling
        /// </summary>
        protected IDataReader ExecuteReader(MySqlCommand cmd)
        {
            if (m_trans != null)
            {
                return ExecuteReaderWithTransaction(cmd, m_trans);
            }
            
            // Note: For readers, we need to manage the connection lifecycle differently
            // since the reader needs the connection to remain open
            var connection = m_connectionPool.GetConnection();
            try
            {
                cmd.Connection = connection;
                var reader = cmd.ExecuteReader();
                
                // Wrap the reader to return connection to pool when disposed
                return new PooledMySqlDataReader(reader as MySqlDataReader, connection, m_connectionPool);
            }
            catch
            {
                m_connectionPool.ReturnConnection(connection);
                cmd.Connection = null;
                throw;
            }
        }

        /// <summary>
        /// Execute reader asynchronously with connection pooling
        /// </summary>
        protected async Task<IDataReader> ExecuteReaderAsync(MySqlCommand cmd)
        {
            if (m_trans != null)
            {
                return ExecuteReaderWithTransaction(cmd, m_trans);
            }
            
            var connection = await m_connectionPool.GetConnectionAsync();
            try
            {
                cmd.Connection = connection;
                var reader = await cmd.ExecuteReaderAsync();
                
                // Wrap the reader to return connection to pool when disposed
                return new PooledMySqlDataReader(reader as MySqlDataReader, connection, m_connectionPool);
            }
            catch
            {
                m_connectionPool.ReturnConnection(connection);
                cmd.Connection = null;
                throw;
            }
        }

        /// <summary>
        /// Execute scalar query with connection pooling
        /// </summary>
        protected object ExecuteScalar(MySqlCommand cmd)
        {
            if (m_trans != null)
            {
                return ExecuteScalarWithTransaction(cmd, m_trans);
            }
            
            return m_connectionPool.ExecuteWithConnection(connection =>
            {
                return ExecuteScalarWithConnection(cmd, connection);
            });
        }

        /// <summary>
        /// Execute scalar query asynchronously with connection pooling
        /// </summary>
        protected async Task<object> ExecuteScalarAsync(MySqlCommand cmd)
        {
            if (m_trans != null)
            {
                return ExecuteScalarWithTransaction(cmd, m_trans);
            }
            
            return await m_connectionPool.ExecuteWithConnectionAsync(async connection =>
            {
                return await ExecuteScalarWithConnectionAsync(cmd, connection);
            });
        }

        /// <summary>
        /// Get pool statistics for monitoring
        /// </summary>
        public PoolStatistics GetPoolStatistics()
        {
            return m_connectionPool?.GetStatistics();
        }

        #region Transaction Support Methods

        private int ExecuteNonQueryWithTransaction(MySqlCommand cmd, MySqlTransaction trans)
        {
            cmd.Transaction = trans;
            return ExecuteNonQueryWithConnection(cmd, trans.Connection);
        }

        private MySqlDataReader ExecuteReaderWithTransaction(MySqlCommand cmd, MySqlTransaction trans)
        {
            cmd.Transaction = trans;
            cmd.Connection = trans.Connection;
            return cmd.ExecuteReader();
        }

        private object ExecuteScalarWithTransaction(MySqlCommand cmd, MySqlTransaction trans)
        {
            cmd.Transaction = trans;
            return ExecuteScalarWithConnection(cmd, trans.Connection);
        }

        #endregion

        #region Connection-Specific Execution Methods

        private int ExecuteNonQueryWithConnection(MySqlCommand cmd, MySqlConnection connection)
        {
            try
            {
                cmd.Connection = connection;
                var result = cmd.ExecuteNonQuery();
                cmd.Connection = null;
                return result;
            }
            catch (Exception e)
            {
                m_log.Error($"{LogHeader}: Database error during ExecuteNonQuery: {e.Message}", e);
                cmd.Connection = null;
                return 0;
            }
        }

        private async Task<int> ExecuteNonQueryWithConnectionAsync(MySqlCommand cmd, MySqlConnection connection)
        {
            try
            {
                cmd.Connection = connection;
                var result = await cmd.ExecuteNonQueryAsync();
                cmd.Connection = null;
                return result;
            }
            catch (Exception e)
            {
                m_log.Error($"{LogHeader}: Database error during ExecuteNonQueryAsync: {e.Message}", e);
                cmd.Connection = null;
                return 0;
            }
        }

        private object ExecuteScalarWithConnection(MySqlCommand cmd, MySqlConnection connection)
        {
            try
            {
                cmd.Connection = connection;
                var result = cmd.ExecuteScalar();
                cmd.Connection = null;
                return result;
            }
            catch (Exception e)
            {
                m_log.Error($"{LogHeader}: Database error during ExecuteScalar: {e.Message}", e);
                cmd.Connection = null;
                return null;
            }
        }

        private async Task<object> ExecuteScalarWithConnectionAsync(MySqlCommand cmd, MySqlConnection connection)
        {
            try
            {
                cmd.Connection = connection;
                var result = await cmd.ExecuteScalarAsync();
                cmd.Connection = null;
                return result;
            }
            catch (Exception e)
            {
                m_log.Error($"{LogHeader}: Database error during ExecuteScalarAsync: {e.Message}", e);
                cmd.Connection = null;
                return null;
            }
        }

        #endregion

        #region Disposal

        /// <summary>
        /// Dispose of resources (connection pools are shared and managed globally)
        /// </summary>
        public virtual void Dispose()
        {
            // Individual instances don't dispose the shared connection pool
            // Connection pools are disposed when the application shuts down
        }

        /// <summary>
        /// Dispose all connection pools (call during application shutdown)
        /// </summary>
        public static void DisposeAllPools()
        {
            lock (s_poolLock)
            {
                foreach (var pool in s_connectionPools.Values)
                {
                    pool.Dispose();
                }
                s_connectionPools.Clear();
                
                m_log.InfoFormat("{0}: All connection pools disposed", LogHeader);
            }
        }

        #endregion
    }

    /// <summary>
    /// Wrapper for MySqlDataReader that returns connection to pool when disposed.
    /// Uses composition instead of inheritance since MySqlDataReader is sealed.
    /// </summary>
    internal class PooledMySqlDataReader : IDataReader, IDisposable
    {
        private readonly MySqlDataReader m_innerReader;
        private readonly MySqlConnection m_connection;
        private readonly MySQLConnectionPool m_pool;
        private bool m_disposed;

        public PooledMySqlDataReader(MySqlDataReader innerReader, MySqlConnection connection, MySQLConnectionPool pool)
        {
            m_innerReader = innerReader ?? throw new ArgumentNullException(nameof(innerReader));
            m_connection = connection ?? throw new ArgumentNullException(nameof(connection));
            m_pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        // Delegate all operations to inner reader
        public bool Read() => m_innerReader.Read();
        public object GetValue(int ordinal) => m_innerReader.GetValue(ordinal);
        public string GetString(int ordinal) => m_innerReader.GetString(ordinal);
        public int GetInt32(int ordinal) => m_innerReader.GetInt32(ordinal);
        public bool GetBoolean(int ordinal) => m_innerReader.GetBoolean(ordinal);
        public DateTime GetDateTime(int ordinal) => m_innerReader.GetDateTime(ordinal);
        public bool IsDBNull(int ordinal) => m_innerReader.IsDBNull(ordinal);
        public int FieldCount => m_innerReader.FieldCount;
        public string GetName(int ordinal) => m_innerReader.GetName(ordinal);
        public Type GetFieldType(int ordinal) => m_innerReader.GetFieldType(ordinal);
        public object this[int ordinal] => m_innerReader[ordinal];
        public object this[string name] => m_innerReader[name];

        // Additional IDataReader members
        public int Depth => m_innerReader.Depth;
        public bool IsClosed => m_innerReader.IsClosed;
        public int RecordsAffected => m_innerReader.RecordsAffected;
        public void Close() => m_innerReader.Close();
        public DataTable GetSchemaTable() => m_innerReader.GetSchemaTable();
        public bool NextResult() => m_innerReader.NextResult();
        
        // Additional data access methods
        public byte GetByte(int ordinal) => m_innerReader.GetByte(ordinal);
        public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) 
            => m_innerReader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public char GetChar(int ordinal) => m_innerReader.GetChar(ordinal);
        public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
            => m_innerReader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public IDataReader GetData(int ordinal) => m_innerReader.GetData(ordinal);
        public string GetDataTypeName(int ordinal) => m_innerReader.GetDataTypeName(ordinal);
        public decimal GetDecimal(int ordinal) => m_innerReader.GetDecimal(ordinal);
        public double GetDouble(int ordinal) => m_innerReader.GetDouble(ordinal);
        public float GetFloat(int ordinal) => m_innerReader.GetFloat(ordinal);
        public Guid GetGuid(int ordinal) => m_innerReader.GetGuid(ordinal);
        public short GetInt16(int ordinal) => m_innerReader.GetInt16(ordinal);
        public long GetInt64(int ordinal) => m_innerReader.GetInt64(ordinal);
        public int GetOrdinal(string name) => m_innerReader.GetOrdinal(name);
        public int GetValues(object[] values) => m_innerReader.GetValues(values);

        public void Dispose()
        {
            if (!m_disposed)
            {
                m_disposed = true;
                
                try
                {
                    m_innerReader?.Dispose();
                }
                finally
                {
                    // Return connection to pool
                    m_pool?.ReturnConnection(m_connection);
                }
            }
        }
    }
}