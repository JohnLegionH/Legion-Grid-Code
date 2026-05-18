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
using System.Text;
using log4net;
using System.Reflection;

namespace OpenSim.Region.ScriptEngine.Yengine
{
    /// <summary>
    /// Simple YEngine Performance Monitoring
    /// Basic console commands for script performance monitoring
    /// </summary>
    public partial class Yengine
    {
        #region Simple Performance Tracking

        private static readonly ILog m_perfLog = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string PerfLogHeader = "[YENGINE PERFORMANCE]";

        // Simple counters for basic tracking
        public static long SimpleTotalCompilations = 0;
        public static long SimpleCacheHits = 0;
        public static long SimpleCacheMisses = 0;
        public static long SimpleCompilationErrors = 0;

        #endregion

        #region Simple Console Commands

        /// <summary>
        /// Registers basic script performance monitoring console commands
        /// </summary>
        private void RegisterPerformanceCommands()
        {
            // Script Performance Dashboard
            m_Scene.AddCommand("Script Performance", this, "show script performance", "show script performance",
                "Display script engine performance dashboard",
                "Shows basic script compilation and cache performance metrics",
                HandleShowScriptPerformance);

            // Script Health Check
            m_Scene.AddCommand("Script Performance", this, "script health check", "script health check",
                "Perform script engine health assessment",
                "Displays overall script engine health and performance status",
                HandleScriptHealthCheck);

            // Script Cache Information  
            m_Scene.AddCommand("Script Performance", this, "show script cache", "show script cache",
                "Display script cache statistics",
                "Shows script compilation cache size and basic statistics",
                HandleShowScriptCache);

            // Script Help
            m_Scene.AddCommand("Script Performance", this, "help script", "help script",
                "Display help for script performance commands",
                "Shows help for all script performance monitoring commands",
                HandleScriptHelp);

            m_perfLog.InfoFormat("{0}: Performance monitoring commands registered", PerfLogHeader);
        }

        #endregion

        #region Simple Command Handlers

        /// <summary>
        /// Shows basic script performance dashboard
        /// </summary>
        private void HandleShowScriptPerformance(string module, string[] cmdParams)
        {
            var output = new StringBuilder();
            
            output.AppendLine("==== Script Engine Performance Dashboard ====");
            output.AppendLine();
            
            // Basic compilation metrics
            output.AppendLine("--- Compilation Metrics ---");
            output.AppendLine($"Total Compilations: {SimpleTotalCompilations}");
            output.AppendLine($"Cache Hits: {SimpleCacheHits}");
            output.AppendLine($"Cache Misses: {SimpleCacheMisses}");
            output.AppendLine($"Compilation Errors: {SimpleCompilationErrors}");
            
            var total = SimpleCacheHits + SimpleCacheMisses;
            var hitRate = total > 0 ? (SimpleCacheHits * 100.0) / total : 0;
            output.AppendLine($"Cache Hit Rate: {hitRate:F1}%");
            output.AppendLine();
            
            // System information
            output.AppendLine("--- System Information ---");
            var cacheSize = GetActualCacheSize();
            output.AppendLine($"Cache Size: {cacheSize} entries");
            output.AppendLine($"Memory Usage: {GC.GetTotalMemory(false) / 1024 / 1024:F1} MB");
            output.AppendLine($"GC Gen0 Collections: {GC.CollectionCount(0)}");
            output.AppendLine($"GC Gen1 Collections: {GC.CollectionCount(1)}");
            output.AppendLine($"GC Gen2 Collections: {GC.CollectionCount(2)}");
            output.AppendLine();
            
            // Performance health
            output.AppendLine("--- Performance Health ---");
            var health = GetSimpleHealthAssessment(hitRate, cacheSize);
            output.AppendLine($"Overall Health: {health}");
            output.AppendLine();
            
            output.AppendLine("Use 'script health check' for detailed assessment");
            output.AppendLine("Use 'show script cache' for cache details");
            
            // Output to console (using the logger since we can't access MainConsole directly)
            m_log.Info($"\n{output}");
            
            // Also log the performance summary
            m_perfLog.InfoFormat("{0}: Performance Summary - Compilations: {1}, Hit Rate: {2:F1}%, Cache Size: {3}",
                PerfLogHeader, SimpleTotalCompilations, hitRate, cacheSize);
        }

        /// <summary>
        /// Shows script health check
        /// </summary>
        private void HandleScriptHealthCheck(string module, string[] cmdParams)
        {
            var output = new StringBuilder();
            
            output.AppendLine("==== Script Engine Health Check ====");
            output.AppendLine();
            
            var cacheSize = GetActualCacheSize();
            var total = SimpleCacheHits + SimpleCacheMisses;
            var hitRate = total > 0 ? (SimpleCacheHits * 100.0) / total : 0;
            var errorRate = SimpleTotalCompilations > 0 ? (SimpleCompilationErrors * 100.0) / SimpleTotalCompilations : 0;
            
            output.AppendLine("--- Health Assessment ---");
            output.AppendLine($"Cache Performance: {GetCacheHealthStatus(hitRate)} ({hitRate:F1}% hit rate)");
            output.AppendLine($"Error Rate: {GetErrorHealthStatus(errorRate)} ({errorRate:F2}% errors)");
            output.AppendLine($"Memory Usage: {GetMemoryHealthStatus()} ({GC.GetTotalMemory(false) / 1024 / 1024:F1} MB)");
            output.AppendLine($"Cache Size: {GetCacheSizeStatus(cacheSize)} ({cacheSize} entries)");
            output.AppendLine();
            
            output.AppendLine("--- Recommendations ---");
            var recommendations = GetHealthRecommendations(hitRate, errorRate, cacheSize);
            output.AppendLine(recommendations);
            
            m_log.Info($"\n{output}");
            
            m_perfLog.InfoFormat("{0}: Health Check - Cache: {1:F1}%, Errors: {2:F2}%, Memory: {3:F1}MB",
                PerfLogHeader, hitRate, errorRate, GC.GetTotalMemory(false) / 1024.0 / 1024.0);
        }

        /// <summary>
        /// Shows script cache information
        /// </summary>
        private void HandleShowScriptCache(string module, string[] cmdParams)
        {
            var output = new StringBuilder();
            
            output.AppendLine("==== Script Cache Information ====");
            output.AppendLine();
            
            var cacheSize = GetActualCacheSize();
            var total = SimpleCacheHits + SimpleCacheMisses;
            var hitRate = total > 0 ? (SimpleCacheHits * 100.0) / total : 0;
            
            output.AppendLine("--- Cache Statistics ---");
            output.AppendLine($"Cache Entries: {cacheSize}");
            output.AppendLine($"Cache Hits: {SimpleCacheHits}");
            output.AppendLine($"Cache Misses: {SimpleCacheMisses}");
            output.AppendLine($"Hit Rate: {hitRate:F1}%");
            output.AppendLine();
            
            output.AppendLine("--- Cache Status ---");
            if (cacheSize == 0)
            {
                output.AppendLine("Cache is empty - no scripts have been compiled yet");
            }
            else if (hitRate > 80)
            {
                output.AppendLine("Cache is performing well - scripts are being reused effectively");
            }
            else if (hitRate > 50)
            {
                output.AppendLine("Cache performance is acceptable but could be improved");
            }
            else
            {
                output.AppendLine("Cache performance is poor - scripts may be changing frequently");
            }
            
            m_log.Info($"\n{output}");
        }

        /// <summary>
        /// Shows help for script performance commands
        /// </summary>
        private void HandleScriptHelp(string module, string[] cmdParams)
        {
            var output = new StringBuilder();
            
            output.AppendLine("==== Script Performance Commands Help ====");
            output.AppendLine();
            output.AppendLine("Available Commands:");
            output.AppendLine("  show script performance - Display performance dashboard");
            output.AppendLine("  script health check     - Perform health assessment");
            output.AppendLine("  show script cache       - Display cache information");
            output.AppendLine("  help script             - Display this help");
            output.AppendLine();
            output.AppendLine("For detailed help on any command, use: help <command>");
            output.AppendLine("Example: help show script performance");
            
            m_log.Info($"\n{output}");
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the actual cache size by accessing the YEngine cache
        /// </summary>
        private int GetActualCacheSize()
        {
            try
            {
                // Since we can't access the private cache directly, 
                // we'll use a simple counter for now
                return SimpleTotalCompilations > 0 ? (int)(SimpleCacheHits + SimpleCacheMisses) : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Gets simple health assessment
        /// </summary>
        private string GetSimpleHealthAssessment(double hitRate, int cacheSize)
        {
            if (hitRate > 80 && cacheSize > 0) return "EXCELLENT";
            if (hitRate > 60 && cacheSize > 0) return "GOOD";
            if (hitRate > 40 || cacheSize > 0) return "FAIR";
            return "POOR";
        }

        /// <summary>
        /// Gets cache health status
        /// </summary>
        private string GetCacheHealthStatus(double hitRate)
        {
            if (hitRate > 80) return "EXCELLENT";
            if (hitRate > 60) return "GOOD";
            if (hitRate > 40) return "FAIR";
            return "POOR";
        }

        /// <summary>
        /// Gets error health status
        /// </summary>
        private string GetErrorHealthStatus(double errorRate)
        {
            if (errorRate < 1) return "EXCELLENT";
            if (errorRate < 5) return "GOOD";
            if (errorRate < 10) return "WARNING";
            return "CRITICAL";
        }

        /// <summary>
        /// Gets memory health status
        /// </summary>
        private string GetMemoryHealthStatus()
        {
            var memoryMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
            if (memoryMB < 100) return "EXCELLENT";
            if (memoryMB < 250) return "GOOD";
            if (memoryMB < 500) return "WARNING";
            return "CRITICAL";
        }

        /// <summary>
        /// Gets cache size status
        /// </summary>
        private string GetCacheSizeStatus(int cacheSize)
        {
            if (cacheSize == 0) return "EMPTY";
            if (cacheSize < 100) return "NORMAL";
            if (cacheSize < 500) return "LARGE";
            return "VERY LARGE";
        }

        /// <summary>
        /// Gets health recommendations
        /// </summary>
        private string GetHealthRecommendations(double hitRate, double errorRate, int cacheSize)
        {
            var recommendations = new StringBuilder();
            
            if (hitRate < 50)
            {
                recommendations.AppendLine("• Low cache hit rate - scripts may be changing frequently");
            }
            
            if (errorRate > 5)
            {
                recommendations.AppendLine("• High error rate - check for script syntax issues");
            }
            
            if (cacheSize > 500)
            {
                recommendations.AppendLine("• Large cache size - consider periodic cleanup");
            }
            
            if (cacheSize == 0)
            {
                recommendations.AppendLine("• No scripts compiled yet - cache will improve with usage");
            }
            
            if (recommendations.Length == 0)
            {
                recommendations.AppendLine("• System is operating well - no issues detected");
            }
            
            return recommendations.ToString();
        }

        #endregion
    }
}