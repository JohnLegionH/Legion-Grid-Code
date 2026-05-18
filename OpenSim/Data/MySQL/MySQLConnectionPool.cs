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
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    /// <summary>
    /// High-performance connection pool for MySQL connections in OpenSim.
    /// Provides thread-safe connection pooling with automatic connection management,
    /// health monitoring, and performance optimizations.
    /// </summary>
    public class MySQLConnectionPool : IDisposable
    {
        private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MYSQL POOL]";
        
        // Pool configuration
        private readonly string m_connectionString;
        private readonly int m_maxPoolSize;
        private readonly int m_minPoolSize;
        private readonly TimeSpan m_connectionTimeout;
        private readonly TimeSpan m_idleTimeout;
        
        // Connection pool storage
        private readonly ConcurrentQueue<PooledConnection> m_availableConnections;
        private readonly ConcurrentDictionary<MySqlConnection, PooledConnection> m_activeConnections;
        
        // Pool management
        private readonly SemaphoreSlim m_poolSemaphore;
        private readonly Timer m_maintenanceTimer;
        private readonly object m_poolLock = new object();
        
        // Statistics
        private long m_connectionsCreated;
        private long m_connectionsReused;
        private long m_connectionsDisposed;
        private long m_activeConnectionCount;
        
        private bool m_disposed;

        /// <summary>
        /// Initialize a new MySQL connection pool
        /// </summary>
        /// <param name="connectionString">MySQL connection string</param>
        /// <param name="maxPoolSize">Maximum number of connections in pool</param>
        /// <param name="minPoolSize">Minimum number of connections to maintain</param>
        public MySQLConnectionPool(string connectionString, int maxPoolSize = 100, int minPoolSize = 10)
        {
            m_connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            m_maxPoolSize = Math.Max(1, maxPoolSize);
            m_minPoolSize = Math.Max(1, Math.Min(minPoolSize, maxPoolSize));
            m_connectionTimeout = TimeSpan.FromSeconds(30);
            m_idleTimeout = TimeSpan.FromMinutes(10);
            
            m_availableConnections = new ConcurrentQueue<PooledConnection>();
            m_activeConnections = new ConcurrentDictionary<MySqlConnection, PooledConnection>();
            m_poolSemaphore = new SemaphoreSlim(m_maxPoolSize, m_maxPoolSize);
            
            // Initialize minimum connections
            InitializePool();
            
            // Start maintenance timer (runs every 5 minutes)
            m_maintenanceTimer = new Timer(PerformMaintenance, null, 
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            
            m_log.InfoFormat("{0}: Connection pool initialized. Min: {1}, Max: {2}", 
                LogHeader, m_minPoolSize, m_maxPoolSize);
        }

        /// <summary>
        /// Get a connection from the pool asynchronously
        /// </summary>
        public async Task<MySqlConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
        {
            if (m_disposed)
                throw new ObjectDisposedException(nameof(MySQLConnectionPool));

            // Wait for available slot in pool
            await m_poolSemaphore.WaitAsync(m_connectionTimeout, cancellationToken);
            
            try
            {
                // Try to get existing connection
                if (m_availableConnections.TryDequeue(out var pooledConnection))
                {
                    if (IsConnectionValid(pooledConnection))
                    {
                        // Mark as active and return
                        m_activeConnections[pooledConnection.Connection] = pooledConnection;
                        pooledConnection.LastUsed = DateTime.UtcNow;
                        Interlocked.Increment(ref m_connectionsReused);
                        Interlocked.Increment(ref m_activeConnectionCount);
                        
                        return pooledConnection.Connection;
                    }
                    else
                    {
                        // Connection is invalid, dispose it
                        pooledConnection.Dispose();
                        Interlocked.Increment(ref m_connectionsDisposed);
                    }
                }
                
                // Create new connection
                var newConnection = await CreateNewConnectionAsync();
                var newPooledConnection = new PooledConnection(newConnection, DateTime.UtcNow);
                
                m_activeConnections[newConnection] = newPooledConnection;
                Interlocked.Increment(ref m_connectionsCreated);
                Interlocked.Increment(ref m_activeConnectionCount);
                
                return newConnection;
            }
            catch
            {
                // Release semaphore on error
                m_poolSemaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// Get a connection from the pool synchronously
        /// </summary>
        public MySqlConnection GetConnection()
        {
            return GetConnectionAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Return a connection to the pool
        /// </summary>
        public void ReturnConnection(MySqlConnection connection)
        {
            if (connection == null || m_disposed)
                return;

            if (m_activeConnections.TryRemove(connection, out var pooledConnection))
            {
                Interlocked.Decrement(ref m_activeConnectionCount);
                
                // Check if connection is still valid and pool has space
                if (IsConnectionValid(pooledConnection) && 
                    m_availableConnections.Count < m_maxPoolSize)
                {
                    // Return to pool
                    pooledConnection.LastUsed = DateTime.UtcNow;
                    m_availableConnections.Enqueue(pooledConnection);
                }
                else
                {
                    // Dispose connection
                    pooledConnection.Dispose();
                    Interlocked.Increment(ref m_connectionsDisposed);
                }
                
                // Release semaphore slot
                m_poolSemaphore.Release();
            }
        }

        /// <summary>
        /// Execute a function with a pooled connection
        /// </summary>
        public async Task<T> ExecuteWithConnectionAsync<T>(Func<MySqlConnection, Task<T>> operation)
        {
            var connection = await GetConnectionAsync();
            try
            {
                return await operation(connection);
            }
            finally
            {
                ReturnConnection(connection);
            }
        }

        /// <summary>
        /// Execute a function with a pooled connection (synchronous)
        /// </summary>
        public T ExecuteWithConnection<T>(Func<MySqlConnection, T> operation)
        {
            var connection = GetConnection();
            try
            {
                return operation(connection);
            }
            finally
            {
                ReturnConnection(connection);
            }
        }

        /// <summary>
        /// Get current pool statistics
        /// </summary>
        public PoolStatistics GetStatistics()
        {
            return new PoolStatistics
            {
                AvailableConnections = m_availableConnections.Count,
                ActiveConnections = (int)m_activeConnectionCount,
                TotalConnectionsCreated = m_connectionsCreated,
                TotalConnectionsReused = m_connectionsReused,
                TotalConnectionsDisposed = m_connectionsDisposed,
                PoolEfficiency = m_connectionsCreated > 0 ? 
                    (float)m_connectionsReused / (m_connectionsCreated + m_connectionsReused) * 100 : 0,
                MaxPoolSize = m_maxPoolSize,
                MinPoolSize = m_minPoolSize
            };
        }

        private void InitializePool()
        {
            for (int i = 0; i < m_minPoolSize; i++)
            {
                try
                {
                    var connection = CreateNewConnectionAsync().GetAwaiter().GetResult();
                    var pooledConnection = new PooledConnection(connection, DateTime.UtcNow);
                    m_availableConnections.Enqueue(pooledConnection);
                    Interlocked.Increment(ref m_connectionsCreated);
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("{0}: Failed to create initial connection {1}: {2}", 
                        LogHeader, i + 1, ex.Message);
                }
            }
        }

        private async Task<MySqlConnection> CreateNewConnectionAsync()
        {
            var connection = new MySqlConnection(m_connectionString);
            await connection.OpenAsync();
            return connection;
        }

        private bool IsConnectionValid(PooledConnection pooledConnection)
        {
            if (pooledConnection?.Connection == null)
                return false;

            // Check if connection is still open
            if (pooledConnection.Connection.State != ConnectionState.Open)
                return false;

            // Check if connection has been idle too long
            if (DateTime.UtcNow - pooledConnection.LastUsed > m_idleTimeout)
                return false;

            // Optionally ping the connection to verify it's still responsive
            try
            {
                pooledConnection.Connection.Ping();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void PerformMaintenance(object state)
        {
            if (m_disposed)
                return;

            try
            {
                var stats = GetStatistics();
                m_log.DebugFormat("{0}: Pool maintenance - Available: {1}, Active: {2}, Efficiency: {3:F1}%",
                    LogHeader, stats.AvailableConnections, stats.ActiveConnections, stats.PoolEfficiency);

                // Clean up idle connections
                var connectionsToRemove = new List<PooledConnection>();
                var tempConnections = new List<PooledConnection>();

                // Drain the queue temporarily
                while (m_availableConnections.TryDequeue(out var connection))
                {
                    if (IsConnectionValid(connection))
                    {
                        tempConnections.Add(connection);
                    }
                    else
                    {
                        connectionsToRemove.Add(connection);
                    }
                }

                // Dispose invalid connections
                foreach (var connection in connectionsToRemove)
                {
                    connection.Dispose();
                    Interlocked.Increment(ref m_connectionsDisposed);
                }

                // Re-queue valid connections
                foreach (var connection in tempConnections)
                {
                    m_availableConnections.Enqueue(connection);
                }

                // Ensure minimum pool size
                int currentAvailable = m_availableConnections.Count;
                if (currentAvailable < m_minPoolSize)
                {
                    int connectionsToCreate = m_minPoolSize - currentAvailable;
                    for (int i = 0; i < connectionsToCreate; i++)
                    {
                        try
                        {
                            var newConnection = CreateNewConnectionAsync().GetAwaiter().GetResult();
                            var pooledConnection = new PooledConnection(newConnection, DateTime.UtcNow);
                            m_availableConnections.Enqueue(pooledConnection);
                            Interlocked.Increment(ref m_connectionsCreated);
                        }
                        catch (Exception ex)
                        {
                            m_log.WarnFormat("{0}: Failed to create maintenance connection: {1}", LogHeader, ex.Message);
                            break; // Don't flood logs with errors
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during maintenance: {1}", LogHeader, ex.Message);
            }
        }

        public void Dispose()
        {
            if (m_disposed)
                return;

            m_disposed = true;

            m_log.InfoFormat("{0}: Disposing connection pool", LogHeader);

            // Stop maintenance timer
            m_maintenanceTimer?.Dispose();

            // Dispose all available connections
            while (m_availableConnections.TryDequeue(out var connection))
            {
                connection.Dispose();
            }

            // Dispose all active connections (shouldn't happen in normal operation)
            foreach (var connection in m_activeConnections.Values)
            {
                connection.Dispose();
            }

            m_poolSemaphore?.Dispose();

            var stats = GetStatistics();
            m_log.InfoFormat("{0}: Pool disposed. Created: {1}, Reused: {2}, Disposed: {3}, Efficiency: {4:F1}%",
                LogHeader, stats.TotalConnectionsCreated, stats.TotalConnectionsReused, 
                stats.TotalConnectionsDisposed, stats.PoolEfficiency);
        }

        /// <summary>
        /// Wrapper for pooled MySQL connections
        /// </summary>
        private class PooledConnection : IDisposable
        {
            public MySqlConnection Connection { get; }
            public DateTime Created { get; }
            public DateTime LastUsed { get; set; }

            public PooledConnection(MySqlConnection connection, DateTime created)
            {
                Connection = connection ?? throw new ArgumentNullException(nameof(connection));
                Created = created;
                LastUsed = created;
            }

            public void Dispose()
            {
                try
                {
                    Connection?.Close();
                    Connection?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }
            }
        }
    }

    /// <summary>
    /// Statistics about connection pool performance
    /// </summary>
    public class PoolStatistics
    {
        public int AvailableConnections { get; set; }
        public int ActiveConnections { get; set; }
        public long TotalConnectionsCreated { get; set; }
        public long TotalConnectionsReused { get; set; }
        public long TotalConnectionsDisposed { get; set; }
        public float PoolEfficiency { get; set; }
        public int MaxPoolSize { get; set; }
        public int MinPoolSize { get; set; }
    }
}