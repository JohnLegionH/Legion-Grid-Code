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
using log4net;

namespace OpenSim.Region.ScriptEngine.Yengine
{
    /// <summary>
    /// XMRInstance Memory Optimization Extension
    /// Provides memory leak prevention and cache cleanup
    /// </summary>
    public partial class XMRInstance
    {
        #region Memory Optimization Infrastructure

        private static readonly ILog m_memLog = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string MemLogHeader = "[YENGINE MEMORY]";

        // Memory optimization configuration
        private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromMinutes(15);
        private static readonly int MaxCacheSize = 500;
        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(1);
        
        private static DateTime m_lastCacheCleanup = DateTime.MinValue;

        #endregion

        #region Memory Optimization Methods

        /// <summary>
        /// Performs script compilation cache cleanup
        /// Should be called periodically to prevent memory leaks
        /// </summary>
        public static void OptimizeScriptCache()
        {
            try
            {
                var now = DateTime.UtcNow;
                
                // Only run cleanup if enough time has passed
                if (now - m_lastCacheCleanup < CacheCleanupInterval)
                    return;

                lock (m_CompileLock)
                {
                    m_lastCacheCleanup = now;

                    int beforeCount = m_CompiledScriptObjCode.Count;
                    if (beforeCount == 0) return;

                    long memoryBefore = GC.GetTotalMemory(false);
                    var keysToRemove = new List<string>();

                    // Find entries to remove
                    foreach (var kvp in m_CompiledScriptObjCode.ToArray())
                    {
                        var objCode = kvp.Value;
                        
                        // Remove entries with zero or negative reference counts
                        if (objCode.refCount <= 0)
                        {
                            keysToRemove.Add(kvp.Key);
                        }
                        // If cache is too large, remove entries with very low reference counts
                        else if (beforeCount > MaxCacheSize && objCode.refCount <= 1)
                        {
                            // Remove entries with low usage (we can't check compile time)
                            keysToRemove.Add(kvp.Key);
                        }
                    }

                    // Remove identified entries
                    foreach (string key in keysToRemove)
                    {
                        m_CompiledScriptObjCode.Remove(key);
                    }

                    int afterCount = m_CompiledScriptObjCode.Count;
                    int removedCount = beforeCount - afterCount;

                    // Force garbage collection if we removed entries
                    if (removedCount > 0)
                    {
                        GC.Collect(0, GCCollectionMode.Optimized);
                        GC.WaitForPendingFinalizers();
                    }

                    long memoryAfter = GC.GetTotalMemory(false);
                    long memoryFreed = memoryBefore - memoryAfter;

                    // Log results if significant cleanup occurred
                    if (removedCount > 0 || memoryFreed > 1024 * 1024) // Log if entries removed or >1MB freed
                    {
                        m_memLog.InfoFormat("{0}: Script cache cleanup completed. Entries: {1} -> {2} (removed {3}), Memory freed: {4:F1}MB",
                            MemLogHeader, beforeCount, afterCount, removedCount, memoryFreed / 1024.0 / 1024.0);
                    }
                }
            }
            catch (Exception ex)
            {
                m_memLog.WarnFormat("{0}: Script cache cleanup failed: {1}", MemLogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Checks if cache cleanup should be triggered based on size or memory pressure
        /// </summary>
        public static void CheckCacheCleanup()
        {
            try
            {
                // Quick check without locking
                int cacheSize = m_CompiledScriptObjCode.Count;
                
                // Trigger cleanup if cache is getting large
                if (cacheSize > MaxCacheSize)
                {
                    OptimizeScriptCache();
                    return;
                }

                // Check memory usage periodically
                long memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
                if (memoryMB > 300) // More than 300MB
                {
                    OptimizeScriptCache();
                }
            }
            catch
            {
                // Don't let cleanup interfere with script operations
            }
        }

        /// <summary>
        /// Gets script cache statistics for monitoring
        /// </summary>
        public static ScriptCacheStats GetCacheStats()
        {
            try
            {
                lock (m_CompileLock)
                {
                    var stats = new ScriptCacheStats
                    {
                        TotalEntries = m_CompiledScriptObjCode.Count,
                        TotalMemoryMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0,
                        LastCleanupTime = m_lastCacheCleanup
                    };

                    // Calculate statistics
                    int unusedEntries = 0;
                    int totalRefCount = 0;

                    foreach (var objCode in m_CompiledScriptObjCode.Values)
                    {
                        if (objCode.refCount <= 0)
                            unusedEntries++;
                        totalRefCount += objCode.refCount;
                    }

                    stats.OldEntries = 0; // We can't determine age without compile time
                    stats.UnusedEntries = unusedEntries;
                    stats.AverageRefCount = stats.TotalEntries > 0 ? (double)totalRefCount / stats.TotalEntries : 0;

                    return stats;
                }
            }
            catch
            {
                return new ScriptCacheStats
                {
                    TotalMemoryMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0
                };
            }
        }

        #endregion

        #region Cache Stats Structure

        /// <summary>
        /// Script cache statistics
        /// </summary>
        public struct ScriptCacheStats
        {
            public int TotalEntries;
            public double TotalMemoryMB;
            public DateTime LastCleanupTime;
            public int OldEntries;
            public int UnusedEntries;
            public double AverageRefCount;
        }

        #endregion
    }
}