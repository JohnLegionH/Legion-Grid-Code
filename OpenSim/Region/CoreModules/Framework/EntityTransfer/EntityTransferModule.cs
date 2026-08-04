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
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Client;
using OpenSim.Framework.Http;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;

using OpenMetaverse;
using log4net;
using Nini.Config;
using Mono.Addins;

namespace OpenSim.Region.CoreModules.Framework.EntityTransfer
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EntityTransferModule")]
    public class EntityTransferModule : INonSharedRegionModule, IEntityTransferModule, IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[ENTITY TRANSFER MODULE]";
        private static readonly string OutfitTPError = "destination region does not support the Outfit you are wearing. Please retry with a simpler one";

        public EntityTransferModule()
        {
        }

        ~EntityTransferModule()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        bool disposed;
        private void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                m_bannedRegionCache?.Dispose();
                m_bannedRegionCache = null;
            }
        }

        /// <summary>
        /// If true then on a teleport, the source region waits for a callback from the destination region.  If
        /// a callback fails to arrive within a set time then the user is pulled back into the source region.
        /// </summary>
        public bool WaitForAgentArrivedAtDestination { get; set; } = true;

        /// <summary>
        /// If true then we ask the viewer to disable teleport cancellation and ignore teleport requests.
        /// </summary>
        /// <remarks>
        /// This is useful in situations where teleport is very likely to always succeed and we want to avoid a
        /// situation where avatars can be come 'stuck' due to a failed teleport cancellation.  Unfortunately, the
        /// nature of the teleport protocol makes it extremely difficult (maybe impossible) to make teleport
        /// cancellation consistently suceed.
        /// </remarks>
        public bool DisableInterRegionTeleportCancellation { get; set; }

        /// <summary>
        /// Number of times inter-region teleport was attempted.
        /// </summary>
        private Stat m_interRegionTeleportAttempts;

        /// <summary>
        /// Number of times inter-region teleport was aborted (due to simultaneous client logout).
        /// </summary>
        private Stat m_interRegionTeleportAborts;

        /// <summary>
        /// Number of times inter-region teleport was successfully cancelled by the client.
        /// </summary>
        private Stat m_interRegionTeleportCancels;

        /// <summary>
        /// Number of times inter-region teleport failed due to server/client/network problems (e.g. viewer failed to
        /// connect with destination region).
        /// </summary>
        /// <remarks>
        /// This is not necessarily a problem for this simulator - in open-grid/hg conditions, viewer connectivity to
        /// destination simulator is unknown.
        /// </remarks>
        private Stat m_interRegionTeleportFailures;

        // Enhanced Performance Monitoring for Modernization
        private Stat m_regionCrossingAttempts;
        private Stat m_regionCrossingSuccesses;
        private Stat m_regionCrossingFailures;
        private Stat m_regionCrossingTimes;
        private Stat m_regionCrossingVelocityPreserved;
        private Stat m_asyncHttpRequests;
        private Stat m_asyncHttpSuccesses;
        private Stat m_asyncHttpFailures;
        private Stat m_asyncHttpTimeouts;
        private Stat m_asyncHttpAverageTimes;

        // GC and Memory Performance Stats
        private Stat m_memoryAllocationsPerCrossing;
        private Stat m_gcCollectionsGen0;
        private Stat m_gcCollectionsGen1;
        private Stat m_gcCollectionsGen2;
        private Stat m_memoryPressureAfterCrossing;

        // Performance tracking data structures
        private readonly ConcurrentQueue<double> m_crossingTimes = new();
        private readonly ConcurrentQueue<double> m_httpRequestTimes = new();
        private const int MAX_PERFORMANCE_SAMPLES = 100; // Keep last 100 samples for averaging
        
        // Benchmarking suite
        private CrossingBenchmarkSuite m_benchmarkSuite;
        private readonly object m_benchmarkLock = new object();
        
        // Static reference for HTTP services to report performance metrics
        private static EntityTransferModule s_activeInstance;
        
        // GC tracking for performance analysis
        private long m_lastGen0Collections = 0;
        private long m_lastGen1Collections = 0;
        private long m_lastGen2Collections = 0;
        
        // Performance alerting configuration
        private double m_crossingTimeAlertThreshold = 100.0; // ms
        private double m_successRateAlertThreshold = 95.0; // percentage
        private double m_memoryAlertThreshold = 10.0; // MB per crossing
        private int m_gcGen2AlertThreshold = 3; // Gen2 collections per session
        private double m_httpTimeoutRateThreshold = 10.0; // percentage
        
        // Alert tracking to prevent spam
        private DateTime m_lastCrossingTimeAlert = DateTime.MinValue;
        private DateTime m_lastSuccessRateAlert = DateTime.MinValue;
        private DateTime m_lastMemoryAlert = DateTime.MinValue;
        private DateTime m_lastGCAlert = DateTime.MinValue;
        private DateTime m_lastHttpAlert = DateTime.MinValue;
        private readonly TimeSpan m_alertCooldown = TimeSpan.FromMinutes(5); // 5 minute cooldown between similar alerts

        protected GridInfo m_thisGridInfo;

        protected bool m_Enabled = false;

        protected Scene m_scene;
        public Scene Scene
        {
            get
            {
                return m_scene;
            }
         }

        protected string m_sceneName;
        protected RegionInfo m_sceneRegionInfo;
        protected ulong m_sceneRegionHandler;
        /// <summary>
        /// Handles recording and manipulation of state for entities that are in transfer within or between regions
        /// (cross or teleport).
        /// </summary>
        private EntityTransferStateMachine m_entityTransferStateMachine;

        // For performance, we keed a cached of banned regions so we don't keep going
        //    to the grid service.
        private class BannedRegionCache
        {
            private ExpiringCacheOS<ulong, Dictionary<UUID, double>> m_bannedRegions = new(15000);

            public BannedRegionCache()
            {
            }

            ~BannedRegionCache()
            {
                Dispose(false);
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing)
            {
                if (m_bannedRegions != null)
                {
                    m_bannedRegions.Dispose();
                    m_bannedRegions = null;
                }
            }

            // Return 'true' if there is a valid ban entry for this agent in this region
            public bool IfBanned(ulong pRegionHandle, UUID pAgentID)
            {
                if (m_bannedRegions.TryGetValue(pRegionHandle, out Dictionary<UUID, double> idCache))
                {
                    lock(idCache)
                    {
                        if (idCache.TryGetValue(pAgentID, out double exp))
                        {
                            if(exp < Util.GetTimeStamp())
                                return true;
                            else
                                idCache.Remove(pAgentID);
                        }
                    }
                }
                return false;
            }

            public void Add(ulong pRegionHandle, UUID pAgentID, double newTime)
            {
                if (m_bannedRegions.TryGetValue(pRegionHandle, out Dictionary<UUID, double> idCache))
                {
                    lock (idCache)
                    {
                        idCache[pAgentID] = Util.GetTimeStamp() + newTime;
                        m_bannedRegions.AddOrUpdate(pRegionHandle, idCache, newTime);
                    }
                }
                else
                {
                    idCache = new Dictionary<UUID, double>
                    {
                        [pAgentID] = Util.GetTimeStamp() + newTime
                    };
                    m_bannedRegions.AddOrUpdate(pRegionHandle, idCache, newTime);
                }
            }

            // Remove the agent from the region's banned list
            public void Remove(ulong pRegionHandle, UUID pAgentID)
            {
                if (m_bannedRegions.TryGetValue(pRegionHandle, out Dictionary<UUID, double> idCache))
                {
                    lock (idCache)
                        idCache.Remove(pAgentID);
                }
            }
        }

        private BannedRegionCache m_bannedRegionCache = new();

        private IEventQueue m_eqModule;

        #region ISharedRegionModule

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public virtual string Name
        {
            get { return "BasicEntityTransferModule"; }
        }

        public virtual void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("EntityTransferModule", "");
                if (name == Name)
                {
                    InitialiseCommon(source);
                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: {0} enabled.", Name);
                }
            }
        }

        /// <summary>
        /// Initialize config common for this module and any descendents.
        /// </summary>
        /// <param name="source"></param>
        protected virtual void InitialiseCommon(IConfigSource source)
        {
            IConfig transferConfig = source.Configs["EntityTransfer"];
            if (transferConfig != null)
            {
                DisableInterRegionTeleportCancellation
                    = transferConfig.GetBoolean("DisableInterRegionTeleportCancellation", false);

                WaitForAgentArrivedAtDestination
                    = transferConfig.GetBoolean("wait_for_callback", WaitForAgentArrivedAtDestination);
            }

            m_entityTransferStateMachine = new EntityTransferStateMachine(this);

            m_Enabled = true;
        }

        public virtual void PostInitialise()
        {
        }

        public virtual void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;
            m_sceneName = scene.Name;
            m_sceneRegionInfo = scene.RegionInfo;
            m_sceneRegionHandler = m_sceneRegionInfo.RegionHandle;
            m_thisGridInfo = scene.SceneGridInfo;

            m_interRegionTeleportAttempts =
                new Stat(
                    "InterRegionTeleportAttempts",
                    "Number of inter-region teleports attempted.",
                    "This does not count attempts which failed due to pre-conditions (e.g. target simulator refused access).\n"
                        + "You can get successfully teleports by subtracting aborts, cancels and teleport failures from this figure.",
                    "",
                    "entitytransfer",
                    m_sceneName,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportAborts =
                new Stat(
                    "InterRegionTeleportAborts",
                    "Number of inter-region teleports aborted due to client actions.",
                    "The chief action is simultaneous logout whilst teleporting.",
                    "",
                    "entitytransfer",
                    m_sceneName,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportCancels =
                new Stat(
                    "InterRegionTeleportCancels",
                    "Number of inter-region teleports cancelled by the client.",
                    null,
                    "",
                    "entitytransfer",
                    m_sceneName,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            m_interRegionTeleportFailures =
                new Stat(
                    "InterRegionTeleportFailures",
                    "Number of inter-region teleports that failed due to server/client/network issues.",
                    "This number may not be very helpful in open-grid/hg situations as the network connectivity/quality of destinations is uncontrollable.",
                    "",
                    "entitytransfer",
                    m_sceneName,
                    StatType.Push,
                    null,
                    StatVerbosity.Debug);

            StatsManager.RegisterStat(m_interRegionTeleportAttempts);
            StatsManager.RegisterStat(m_interRegionTeleportAborts);
            StatsManager.RegisterStat(m_interRegionTeleportCancels);
            StatsManager.RegisterStat(m_interRegionTeleportFailures);

            // Enhanced Performance Monitoring Stats
            m_regionCrossingAttempts = new Stat(
                "RegionCrossingAttempts",
                "Number of region crossing attempts (async modernized version).",
                "Tracks all attempts at region crossings using the modernized async infrastructure.",
                "",
                "entitytransfer.modernization",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Info);

            m_regionCrossingSuccesses = new Stat(
                "RegionCrossingSuccesses", 
                "Number of successful region crossings.",
                "Tracks successful region crossings with preserved velocity and no floating avatar issues.",
                "",
                "entitytransfer.modernization",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Info);

            m_regionCrossingFailures = new Stat(
                "RegionCrossingFailures",
                "Number of failed region crossings.",
                "Tracks failed region crossings for analysis and improvement.",
                "",
                "entitytransfer.modernization", 
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Info);

            m_regionCrossingTimes = new Stat(
                "RegionCrossingAverageTime",
                "Average time for region crossings in milliseconds.",
                "Tracks the performance improvements from async modernization (target: 15-31ms).",
                "ms",
                "entitytransfer.modernization",
                m_sceneName,
                StatType.Pull,
                stat => GetAverageCrossingTime(),
                StatVerbosity.Info);

            m_regionCrossingVelocityPreserved = new Stat(
                "RegionCrossingVelocityPreserved",
                "Number of crossings with preserved velocity (smooth experience).",
                "Tracks crossings where velocity was successfully preserved for seamless movement.",
                "",
                "entitytransfer.modernization",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Info);

            m_asyncHttpRequests = new Stat(
                "AsyncHttpRequests",
                "Number of async HTTP requests made during crossings.",
                "Tracks usage of the modernized async HTTP infrastructure.",
                "",
                "http.modernization",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Debug);

            m_asyncHttpSuccesses = new Stat(
                "AsyncHttpSuccesses",
                "Number of successful async HTTP requests.",
                "Tracks successful async HTTP operations for reliability metrics.",
                "",
                "http.modernization",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Debug);

            m_asyncHttpFailures = new Stat(
                "AsyncHttpFailures",
                "Number of failed async HTTP requests.",
                "Tracks failed async HTTP operations for debugging and optimization.",
                "",
                "http.modernization",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Debug);

            m_asyncHttpTimeouts = new Stat(
                "AsyncHttpTimeouts",
                "Number of async HTTP request timeouts.",
                "Tracks timeout events in the modernized HTTP infrastructure.",
                "",
                "http.modernization",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Debug);

            m_asyncHttpAverageTimes = new Stat(
                "AsyncHttpAverageTime",
                "Average time for async HTTP requests in milliseconds.",
                "Tracks HTTP performance improvements from connection pooling and async operations.",
                "ms",
                "http.modernization",
                m_sceneName,
                StatType.Pull,
                stat => GetAverageHttpTime(),
                StatVerbosity.Debug);

            // GC and Memory Performance Stats
            m_memoryAllocationsPerCrossing = new Stat(
                "MemoryAllocationsPerCrossing",
                "Average memory allocated per region crossing.",
                "Tracks memory efficiency improvements from async modernization.",
                "MB",
                "gc.performance",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Debug);

            m_gcCollectionsGen0 = new Stat(
                "GCCollectionsGen0",
                "Generation 0 garbage collections during crossings.",
                "Tracks GC pressure from region crossing operations.",
                "",
                "gc.performance", 
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Debug);

            m_gcCollectionsGen1 = new Stat(
                "GCCollectionsGen1",
                "Generation 1 garbage collections during crossings.",
                "Tracks mid-level GC pressure from region crossing operations.",
                "",
                "gc.performance",
                m_sceneName, 
                StatType.Push,
                null,
                StatVerbosity.Debug);

            m_gcCollectionsGen2 = new Stat(
                "GCCollectionsGen2", 
                "Generation 2 garbage collections during crossings.",
                "Tracks major GC pressure from region crossing operations.",
                "",
                "gc.performance",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Debug);

            m_memoryPressureAfterCrossing = new Stat(
                "MemoryPressureAfterCrossing",
                "Memory pressure after region crossings.",
                "Tracks memory cleanup efficiency of modernized async operations.",
                "MB", 
                "gc.performance",
                m_sceneName,
                StatType.Push,
                null,
                StatVerbosity.Debug);

            // Initialize GC tracking baselines
            m_lastGen0Collections = GC.CollectionCount(0);
            m_lastGen1Collections = GC.CollectionCount(1);
            m_lastGen2Collections = GC.CollectionCount(2);

            // Register enhanced performance stats
            StatsManager.RegisterStat(m_regionCrossingAttempts);
            StatsManager.RegisterStat(m_regionCrossingSuccesses);
            StatsManager.RegisterStat(m_regionCrossingFailures);
            StatsManager.RegisterStat(m_regionCrossingTimes);
            StatsManager.RegisterStat(m_regionCrossingVelocityPreserved);
            StatsManager.RegisterStat(m_asyncHttpRequests);
            StatsManager.RegisterStat(m_asyncHttpSuccesses);
            StatsManager.RegisterStat(m_asyncHttpFailures);
            StatsManager.RegisterStat(m_asyncHttpTimeouts);
            StatsManager.RegisterStat(m_asyncHttpAverageTimes);
            
            // Register GC and memory performance stats
            StatsManager.RegisterStat(m_memoryAllocationsPerCrossing);
            StatsManager.RegisterStat(m_gcCollectionsGen0);
            StatsManager.RegisterStat(m_gcCollectionsGen1);
            StatsManager.RegisterStat(m_gcCollectionsGen2);
            StatsManager.RegisterStat(m_memoryPressureAfterCrossing);

            scene.RegisterModuleInterface<IEntityTransferModule>(this);
            scene.EventManager.OnNewClient += OnNewClient;
            
            // Set static reference for HTTP performance reporting
            s_activeInstance = this;
            
            // Initialize benchmarking suite
            m_benchmarkSuite = new CrossingBenchmarkSuite(scene);
            
            // Register performance monitoring console commands
            RegisterPerformanceCommands(scene);
        }

        /// <summary>
        /// Get the average crossing time from recent samples
        /// </summary>
        private double GetAverageCrossingTime()
        {
            if (m_crossingTimes.IsEmpty)
                return 0.0;

            double total = 0.0;
            int count = 0;
            
            foreach (double time in m_crossingTimes)
            {
                total += time;
                count++;
            }
            
            return count > 0 ? total / count : 0.0;
        }

        /// <summary>
        /// Get the average HTTP request time from recent samples
        /// </summary>
        private double GetAverageHttpTime()
        {
            if (m_httpRequestTimes.IsEmpty)
                return 0.0;

            double total = 0.0;
            int count = 0;
            
            foreach (double time in m_httpRequestTimes)
            {
                total += time;
                count++;
            }
            
            return count > 0 ? total / count : 0.0;
        }

        /// <summary>
        /// Record a crossing time for performance tracking
        /// </summary>
        private void RecordCrossingTime(double timeMs)
        {
            // Maintain a rolling window of samples
            while (m_crossingTimes.Count >= MAX_PERFORMANCE_SAMPLES)
            {
                m_crossingTimes.TryDequeue(out _);
            }
            m_crossingTimes.Enqueue(timeMs);
        }

        /// <summary>
        /// Record an HTTP request time for performance tracking
        /// </summary>
        private void RecordHttpTime(double timeMs)
        {
            // Maintain a rolling window of samples
            while (m_httpRequestTimes.Count >= MAX_PERFORMANCE_SAMPLES)
            {
                m_httpRequestTimes.TryDequeue(out _);
            }
            m_httpRequestTimes.Enqueue(timeMs);
        }

        /// <summary>
        /// Static method for HTTP services to report request metrics
        /// </summary>
        public static void ReportHttpRequest(double timeMs, bool success, bool timeout)
        {
            var instance = s_activeInstance;
            if (instance != null)
            {
                if (instance.m_asyncHttpRequests != null)
                    instance.m_asyncHttpRequests.Value++;
                
                if (timeout)
                {
                    if (instance.m_asyncHttpTimeouts != null)
                        instance.m_asyncHttpTimeouts.Value++;
                }
                else if (success)
                {
                    if (instance.m_asyncHttpSuccesses != null)
                        instance.m_asyncHttpSuccesses.Value++;
                }
                else
                {
                    if (instance.m_asyncHttpFailures != null)
                        instance.m_asyncHttpFailures.Value++;
                }
                    
                instance.RecordHttpTime(timeMs);
            }
        }

        /// <summary>
        /// Register performance monitoring console commands
        /// </summary>
        private void RegisterPerformanceCommands(Scene scene)
        {
            scene.AddCommand("Crossing Performance", this, "show crossing performance", "show crossing performance", 
                "Display real-time region crossing performance metrics",
                "Displays a comprehensive dashboard of region crossing performance including:\n" +
                "- Region crossing metrics (attempts, successes, failures, success rate)\n" +
                "- Average crossing times and velocity preservation statistics\n" +
                "- HTTP performance metrics (requests, successes, failures, timeouts)\n" +
                "- Memory and GC performance data\n" +
                "- Connection pool performance statistics\n" +
                "- Real-time system performance indicators\n" +
                "\nThis command provides administrators with a complete overview of region crossing health.",
                HandleShowCrossingPerformance);
                
            scene.AddCommand("Crossing Performance", this, "show crossing stats", "show crossing stats",
                "Display detailed crossing statistics and available performance categories",
                "Shows information about available statistics categories and current alert thresholds:\n" +
                "- entitytransfer.modernization (region crossing performance)\n" +
                "- http.modernization (async HTTP performance)\n" +
                "- gc.performance (garbage collection metrics)\n" +
                "\nAlso displays current performance alert threshold settings.\n" +
                "Use with existing 'stats show' commands for detailed analysis.",
                HandleShowCrossingStats);
                
            scene.AddCommand("Crossing Performance", this, "crossing health check", "crossing health check",
                "Generate a comprehensive performance health assessment",
                "Performs a detailed health analysis of region crossing performance:\n" +
                "- Evaluates crossing time health (EXCELLENT/GOOD/WARNING/CRITICAL)\n" +
                "- Assesses success rate health and reliability\n" +
                "- Analyzes memory usage and GC pressure\n" +
                "- Provides automated optimization recommendations\n" +
                "\nUse this command to quickly identify performance issues and get\n" +
                "actionable recommendations for improvement.",
                HandleHealthCheck);
                
            scene.AddCommand("Crossing Performance", this, "set crossing alert threshold", "set crossing alert threshold <type> <value>",
                "Set performance alert thresholds for automated monitoring",
                "Configure automated performance alerts with customizable thresholds:\n" +
                "\nUsage: set crossing alert threshold <type> <value>\n" +
                "\nAlert Types:\n" +
                "- time <ms>     : Alert when crossing time exceeds threshold (default: 100ms)\n" +
                "- success <%>   : Alert when success rate falls below threshold (default: 95%)\n" +
                "- memory <MB>   : Alert when memory per crossing exceeds threshold (default: 10MB)\n" +
                "- gc <count>    : Alert when GC Gen2 collections exceed threshold (default: 3)\n" +
                "- http <%>      : Alert when HTTP timeout rate exceeds threshold (default: 10%)\n" +
                "\nAlerts include 5-minute cooldown to prevent spam and provide actionable\n" +
                "troubleshooting information.",
                HandleSetAlertThreshold);
                
            scene.AddCommand("Crossing Performance", this, "show connection pool", "show connection pool",
                "Display connection pool statistics and optimization details",
                "Shows comprehensive connection pool analysis including:\n" +
                "- Total HTTP requests served and pool hit rates\n" +
                "- Region crossing pool configuration (connections, timeouts, lifetime)\n" +
                "- General pool configuration and settings\n" +
                "- Performance optimizations enabled (HTTP/2, compression, etc.)\n" +
                "- Pool efficiency assessment and recommendations\n" +
                "\nThe modernized connection pool features CPU-scaled connection limits,\n" +
                "HTTP/2 support, and optimized timeouts for improved performance.",
                HandleShowConnectionPool);
                
            scene.AddCommand("Crossing Performance", this, "optimize connection pool", "optimize connection pool",
                "Force cleanup of idle connections and optimize pool settings",
                "Performs connection pool optimization including:\n" +
                "- Cleanup of idle connections in all pools\n" +
                "- Forced garbage collection of orphaned connections\n" +
                "- Reset of pool statistics for fresh monitoring\n" +
                "- Before/after performance comparison\n" +
                "\nUse this command during maintenance windows or when connection\n" +
                "pool efficiency drops below optimal levels. Monitor results with\n" +
                "'show connection pool' command.",
                HandleOptimizeConnectionPool);
                
            scene.AddCommand("Crossing Performance", this, "help crossing", "help crossing",
                "Display help for all region crossing performance commands",
                "Shows comprehensive help for all region crossing performance and monitoring commands:\n" +
                "\nPerformance Monitoring Commands:\n" +
                "- show crossing performance  : Real-time performance dashboard\n" +
                "- show crossing stats        : Available statistics categories\n" +
                "- crossing health check      : Comprehensive health assessment\n" +
                "\nPerformance Alerting Commands:\n" +
                "- set crossing alert threshold : Configure automated alerts\n" +
                "\nConnection Pool Commands:\n" +
                "- show connection pool       : Pool statistics and configuration\n" +
                "- optimize connection pool   : Force cleanup and optimization\n" +
                "\nFor detailed help on any command, use: help <command>\n" +
                "For complete documentation, see: newcommands.md\n" +
                "\nThese commands are part of the OpenSim Region Crossing Modernization\n" +
                "project, providing enterprise-grade performance monitoring and optimization.",
                HandleCrossingHelp);
                
            // Benchmarking Suite Commands
            scene.AddCommand("Crossing Performance", this, "run crossing benchmark", "run crossing benchmark [quick|full|load|stress] [avatars] [crossings]",
                "Run comprehensive region crossing performance benchmarks",
                "Executes automated benchmarking tests to evaluate region crossing performance:\n" +
                "\nBenchmark Types:\n" +
                "- quick    : Basic performance test (default, ~2 minutes)\n" +
                "- full     : Complete benchmark suite (~10 minutes)\n" +
                "- load     : Load testing with multiple virtual avatars\n" +
                "- stress   : Stress testing to find system limits\n" +
                "\nOptional Parameters:\n" +
                "- avatars   : Number of virtual avatars (default: 10)\n" +
                "- crossings : Crossings per avatar (default: 5)\n" +
                "\nExamples:\n" +
                "- run crossing benchmark                    # Quick benchmark\n" +
                "- run crossing benchmark full               # Full suite\n" +
                "- run crossing benchmark load 20 10         # Load test with 20 avatars, 10 crossings each\n" +
                "- run crossing benchmark stress             # Stress test to find limits\n" +
                "\nResults include regression analysis, performance trends, and automated recommendations.\n" +
                "All results are saved for historical comparison and CI/CD integration.",
                HandleRunCrossingBenchmark);
                
            scene.AddCommand("Crossing Performance", this, "show benchmark results", "show benchmark results [latest|all|summary]",
                "Display benchmark test results and performance analysis",
                "Shows results from previous benchmark runs:\n" +
                "\nDisplay Options:\n" +
                "- latest   : Most recent benchmark results (default)\n" +
                "- all      : All historical benchmark data\n" +
                "- summary  : Condensed performance summary\n" +
                "\nIncludes performance trends, regression analysis, and system recommendations.\n" +
                "Results show crossing times, memory usage, concurrency limits, and comparative analysis.",
                HandleShowBenchmarkResults);
                
            scene.AddCommand("Crossing Performance", this, "benchmark config", "benchmark config [avatars] [crossings] [stress] [duration]",
                "Configure benchmark test parameters",
                "Configure parameters for benchmark testing:\n" +
                "\nParameters:\n" +
                "- avatars    : Number of virtual avatars (1-100, default: 10)\n" +
                "- crossings  : Crossings per avatar (1-50, default: 5)\n" +
                "- stress     : Enable stress testing (true/false, default: false)\n" +
                "- duration   : Test duration in minutes (1-30, default: 5)\n" +
                "\nExamples:\n" +
                "- benchmark config 25 10                   # 25 avatars, 10 crossings each\n" +
                "- benchmark config 10 5 true 10            # Enable stress test, 10 min duration\n" +
                "\nConfiguration persists for the current session and affects all benchmark runs.",
                HandleBenchmarkConfig);
        }

        /// <summary>
        /// Console command to show real-time crossing performance
        /// </summary>
        private void HandleShowCrossingPerformance(string module, string[] cmdParams)
        {
            MainConsole.Instance.Output("==== Region Crossing Performance Dashboard ====");
            MainConsole.Instance.Output("");
            
            // Region Crossing Metrics
            MainConsole.Instance.Output("--- Region Crossing Metrics ---");
            MainConsole.Instance.Output($"Attempts: {m_regionCrossingAttempts?.Value ?? 0}");
            MainConsole.Instance.Output($"Successes: {m_regionCrossingSuccesses?.Value ?? 0}");
            MainConsole.Instance.Output($"Failures: {m_regionCrossingFailures?.Value ?? 0}");
            
            double successRate = 0;
            if (m_regionCrossingAttempts?.Value > 0)
                successRate = ((m_regionCrossingSuccesses?.Value ?? 0) * 100.0) / m_regionCrossingAttempts.Value;
            MainConsole.Instance.Output($"Success Rate: {successRate:F1}%");
            
            MainConsole.Instance.Output($"Average Crossing Time: {GetAverageCrossingTime():F1}ms");
            MainConsole.Instance.Output($"Velocity Preserved Count: {m_regionCrossingVelocityPreserved?.Value ?? 0}");
            
            // HTTP Performance Metrics
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("--- HTTP Performance Metrics ---");
            MainConsole.Instance.Output($"HTTP Requests: {m_asyncHttpRequests?.Value ?? 0}");
            MainConsole.Instance.Output($"HTTP Successes: {m_asyncHttpSuccesses?.Value ?? 0}");
            MainConsole.Instance.Output($"HTTP Failures: {m_asyncHttpFailures?.Value ?? 0}");
            MainConsole.Instance.Output($"HTTP Timeouts: {m_asyncHttpTimeouts?.Value ?? 0}");
            MainConsole.Instance.Output($"Average HTTP Time: {GetAverageHttpTime():F1}ms");
            
            // GC and Memory Metrics
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("--- Memory & GC Performance ---");
            MainConsole.Instance.Output($"Memory Allocations/Crossing: {m_memoryAllocationsPerCrossing?.Value:F2} MB");
            MainConsole.Instance.Output($"GC Collections Gen0: {m_gcCollectionsGen0?.Value ?? 0}");
            MainConsole.Instance.Output($"GC Collections Gen1: {m_gcCollectionsGen1?.Value ?? 0}");
            MainConsole.Instance.Output($"GC Collections Gen2: {m_gcCollectionsGen2?.Value ?? 0}");
            MainConsole.Instance.Output($"Current Memory Pressure: {m_memoryPressureAfterCrossing?.Value:F2} MB");
            
            // Real-time System Stats
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("--- Real-time System Performance ---");
            MainConsole.Instance.Output($"Current Heap Memory: {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F2} MB");
            MainConsole.Instance.Output($"Total GC Gen0: {GC.CollectionCount(0)}");
            MainConsole.Instance.Output($"Total GC Gen1: {GC.CollectionCount(1)}"); 
            MainConsole.Instance.Output($"Total GC Gen2: {GC.CollectionCount(2)}");
            
            // Connection Pool Performance
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("--- Connection Pool Performance ---");
            try
            {
                var poolStats = OptimizedHttpService.GetConnectionPoolStats();
                MainConsole.Instance.Output($"HTTP Requests Served: {poolStats["total_requests"]}");
                MainConsole.Instance.Output($"Pool Hit Rate: {poolStats["pool_hit_rate"]:F1}%");
                MainConsole.Instance.Output($"Region Crossing Pool: {poolStats["region_crossing_max_connections"]} max connections");
                
                string poolHealth = (double)poolStats["pool_hit_rate"] >= 90 ? "EXCELLENT" :
                                   (double)poolStats["pool_hit_rate"] >= 75 ? "GOOD" : "NEEDS OPTIMIZATION";
                MainConsole.Instance.Output($"Pool Health: {poolHealth}");
            }
            catch (Exception ex)
            {
                MainConsole.Instance.Output($"Connection Pool: Error retrieving stats ({ex.Message})");
            }
            
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("Use 'show crossing stats' for detailed statistics access");
            MainConsole.Instance.Output("Use 'show connection pool' for connection pool details");
        }

        /// <summary>
        /// Console command to show detailed crossing statistics
        /// </summary>
        private void HandleShowCrossingStats(string module, string[] cmdParams)
        {
            MainConsole.Instance.Output("==== Detailed Crossing Statistics ====");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("Available performance categories:");
            MainConsole.Instance.Output("  - entitytransfer.modernization (region crossing performance)");
            MainConsole.Instance.Output("  - http.modernization (async HTTP performance)");
            MainConsole.Instance.Output("  - gc.performance (garbage collection metrics)");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("Use 'stats show entitytransfer.modernization' to view region crossing stats");
            MainConsole.Instance.Output("Use 'stats show http.modernization' to view HTTP performance stats");
            MainConsole.Instance.Output("Use 'stats show gc.performance' to view memory/GC stats");
            MainConsole.Instance.Output("Use 'stats show all' to view all statistics");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("Current Performance Alert Thresholds:");
            MainConsole.Instance.Output($"  - Crossing Time Alert: >{m_crossingTimeAlertThreshold}ms");
            MainConsole.Instance.Output($"  - Success Rate Alert: <{m_successRateAlertThreshold}%");
            MainConsole.Instance.Output($"  - Memory Alert: >{m_memoryAlertThreshold}MB per crossing");
            MainConsole.Instance.Output($"  - GC Gen2 Alert: >{m_gcGen2AlertThreshold} collections");
            MainConsole.Instance.Output($"  - HTTP Timeout Rate Alert: >{m_httpTimeoutRateThreshold}%");
        }

        /// <summary>
        /// Check for performance degradation and trigger alerts
        /// </summary>
        private void CheckPerformanceAlerts(double crossingTimeMs, double memoryAllocatedMB, long gen2Collections)
        {
            DateTime now = DateTime.UtcNow;
            
            // Check crossing time performance
            if (crossingTimeMs > m_crossingTimeAlertThreshold && 
                now - m_lastCrossingTimeAlert > m_alertCooldown)
            {
                m_log.WarnFormat("[ENTITY TRANSFER MODULE ALERT]: Crossing time degradation detected! " +
                    "Time: {0:F1}ms (threshold: {1}ms). Check for network issues or server load.",
                    crossingTimeMs, m_crossingTimeAlertThreshold);
                m_lastCrossingTimeAlert = now;
            }
            
            // Check success rate (only if we have enough attempts to be meaningful)
            if (m_regionCrossingAttempts?.Value > 10)
            {
                double successRate = ((m_regionCrossingSuccesses?.Value ?? 0) * 100.0) / m_regionCrossingAttempts.Value;
                if (successRate < m_successRateAlertThreshold && 
                    now - m_lastSuccessRateAlert > m_alertCooldown)
                {
                    m_log.WarnFormat("[ENTITY TRANSFER MODULE ALERT]: Success rate degradation detected! " +
                        "Success rate: {0:F1}% (threshold: {1}%). {2} failures out of {3} attempts.",
                        successRate, m_successRateAlertThreshold, 
                        m_regionCrossingFailures?.Value ?? 0, m_regionCrossingAttempts?.Value ?? 0);
                    m_lastSuccessRateAlert = now;
                }
            }
            
            // Check memory allocation per crossing
            if (memoryAllocatedMB > m_memoryAlertThreshold && 
                now - m_lastMemoryAlert > m_alertCooldown)
            {
                m_log.WarnFormat("[ENTITY TRANSFER MODULE ALERT]: High memory allocation detected! " +
                    "Allocated: {0:F2}MB per crossing (threshold: {1}MB). Check for memory leaks.",
                    memoryAllocatedMB, m_memoryAlertThreshold);
                m_lastMemoryAlert = now;
            }
            
            // Check GC Gen2 collections (major GC pressure)
            if (gen2Collections > 0 && 
                (m_gcCollectionsGen2?.Value ?? 0) > m_gcGen2AlertThreshold && 
                now - m_lastGCAlert > m_alertCooldown)
            {
                m_log.WarnFormat("[ENTITY TRANSFER MODULE ALERT]: Excessive GC pressure detected! " +
                    "Gen2 collections: {0} (threshold: {1}). Memory pressure may be affecting performance.",
                    m_gcCollectionsGen2?.Value ?? 0, m_gcGen2AlertThreshold);
                m_lastGCAlert = now;
            }
            
            // Check HTTP timeout rate (only if we have HTTP activity)
            if (m_asyncHttpRequests?.Value > 5)
            {
                double timeoutRate = ((m_asyncHttpTimeouts?.Value ?? 0) * 100.0) / m_asyncHttpRequests.Value;
                if (timeoutRate > m_httpTimeoutRateThreshold && 
                    now - m_lastHttpAlert > m_alertCooldown)
                {
                    m_log.WarnFormat("[ENTITY TRANSFER MODULE ALERT]: High HTTP timeout rate detected! " +
                        "Timeout rate: {0:F1}% (threshold: {1}%). Check network connectivity.",
                        timeoutRate, m_httpTimeoutRateThreshold);
                    m_lastHttpAlert = now;
                }
            }
        }

        /// <summary>
        /// Generate a comprehensive performance health report
        /// </summary>
        private void LogPerformanceHealthCheck()
        {
            if (m_regionCrossingAttempts?.Value > 0)
            {
                double successRate = ((m_regionCrossingSuccesses?.Value ?? 0) * 100.0) / m_regionCrossingAttempts.Value;
                double avgCrossingTime = GetAverageCrossingTime();
                double avgHttpTime = GetAverageHttpTime();
                
                m_log.InfoFormat("[ENTITY TRANSFER MODULE HEALTH]: Performance Summary - " +
                    "Attempts: {0}, Success Rate: {1:F1}%, Avg Time: {2:F1}ms, Avg HTTP: {3:F1}ms, " +
                    "Memory Pressure: {4:F1}MB, GC Gen2: {5}",
                    m_regionCrossingAttempts.Value, successRate, avgCrossingTime, avgHttpTime,
                    m_memoryPressureAfterCrossing?.Value ?? 0, m_gcCollectionsGen2?.Value ?? 0);
            }
        }

        /// <summary>
        /// Console command to perform health check
        /// </summary>
        private void HandleHealthCheck(string module, string[] cmdParams)
        {
            MainConsole.Instance.Output("==== Region Crossing Health Check ====");
            MainConsole.Instance.Output("");
            
            if (m_regionCrossingAttempts?.Value == 0)
            {
                MainConsole.Instance.Output("No region crossing activity detected yet.");
                return;
            }
            
            // Calculate health metrics
            double successRate = ((m_regionCrossingSuccesses?.Value ?? 0) * 100.0) / m_regionCrossingAttempts.Value;
            double avgCrossingTime = GetAverageCrossingTime();
            double avgHttpTime = GetAverageHttpTime();
            double currentMemoryMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
            
            // Health assessment
            MainConsole.Instance.Output("--- Performance Health Assessment ---");
            
            // Crossing time health
            string timeHealth = avgCrossingTime <= 50 ? "EXCELLENT" : 
                               avgCrossingTime <= 100 ? "GOOD" : 
                               avgCrossingTime <= 200 ? "WARNING" : "CRITICAL";
            MainConsole.Instance.Output($"Crossing Time Health: {timeHealth} (Avg: {avgCrossingTime:F1}ms)");
            
            // Success rate health
            string successHealth = successRate >= 99 ? "EXCELLENT" :
                                  successRate >= 95 ? "GOOD" :
                                  successRate >= 90 ? "WARNING" : "CRITICAL";
            MainConsole.Instance.Output($"Success Rate Health: {successHealth} ({successRate:F1}%)");
            
            // Memory health
            string memoryHealth = currentMemoryMB <= 100 ? "EXCELLENT" :
                                 currentMemoryMB <= 200 ? "GOOD" :
                                 currentMemoryMB <= 500 ? "WARNING" : "CRITICAL";
            MainConsole.Instance.Output($"Memory Health: {memoryHealth} (Current: {currentMemoryMB:F1}MB)");
            
            // GC health
            long totalGC = (long)(m_gcCollectionsGen0?.Value ?? 0) + (long)(m_gcCollectionsGen1?.Value ?? 0) + (long)(m_gcCollectionsGen2?.Value ?? 0);
            string gcHealth = totalGC == 0 ? "EXCELLENT" :
                             totalGC <= 5 ? "GOOD" :
                             totalGC <= 20 ? "WARNING" : "CRITICAL";
            MainConsole.Instance.Output($"GC Pressure Health: {gcHealth} (Total GC during crossings: {totalGC})");
            
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("--- Detailed Metrics ---");
            MainConsole.Instance.Output($"Total Attempts: {m_regionCrossingAttempts.Value}");
            MainConsole.Instance.Output($"Successes: {m_regionCrossingSuccesses?.Value ?? 0}");
            MainConsole.Instance.Output($"Failures: {m_regionCrossingFailures?.Value ?? 0}");
            MainConsole.Instance.Output($"Velocity Preserved: {m_regionCrossingVelocityPreserved?.Value ?? 0}");
            MainConsole.Instance.Output($"Average Memory per Crossing: {m_memoryAllocationsPerCrossing?.Value:F2}MB");
            
            if (m_asyncHttpRequests?.Value > 0)
            {
                double httpTimeoutRate = ((m_asyncHttpTimeouts?.Value ?? 0) * 100.0) / m_asyncHttpRequests.Value;
                MainConsole.Instance.Output($"HTTP Requests: {m_asyncHttpRequests.Value}");
                MainConsole.Instance.Output($"HTTP Timeout Rate: {httpTimeoutRate:F1}%");
            }
            
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("--- Recommendations ---");
            if (avgCrossingTime > m_crossingTimeAlertThreshold)
                MainConsole.Instance.Output("• Consider checking network latency and server load");
            if (successRate < m_successRateAlertThreshold)
                MainConsole.Instance.Output("• Investigate crossing failures - check logs for patterns");
            if (currentMemoryMB > 500)
                MainConsole.Instance.Output("• Monitor for memory leaks - consider garbage collection");
            if (totalGC > 10)
                MainConsole.Instance.Output("• High GC pressure - review memory allocation patterns");
                
            // Log the health check
            LogPerformanceHealthCheck();
        }

        /// <summary>
        /// Console command to set alert thresholds
        /// </summary>
        private void HandleSetAlertThreshold(string module, string[] cmdParams)
        {
            if (cmdParams.Length < 5)
            {
                MainConsole.Instance.Output("Usage: set crossing alert threshold <type> <value>");
                MainConsole.Instance.Output("Types: time, success, memory, gc, http");
                MainConsole.Instance.Output("Examples:");
                MainConsole.Instance.Output("  set crossing alert threshold time 150    (150ms)");
                MainConsole.Instance.Output("  set crossing alert threshold success 90  (90%)");
                MainConsole.Instance.Output("  set crossing alert threshold memory 15   (15MB)");
                MainConsole.Instance.Output("  set crossing alert threshold gc 5        (5 collections)");
                MainConsole.Instance.Output("  set crossing alert threshold http 20     (20% timeout rate)");
                return;
            }
            
            string type = cmdParams[4].ToLower();
            if (!double.TryParse(cmdParams[5], out double value))
            {
                MainConsole.Instance.Output("Invalid value. Please provide a numeric value.");
                return;
            }
            
            switch (type)
            {
                case "time":
                    m_crossingTimeAlertThreshold = value;
                    MainConsole.Instance.Output($"Crossing time alert threshold set to {value}ms");
                    break;
                case "success":
                    m_successRateAlertThreshold = value;
                    MainConsole.Instance.Output($"Success rate alert threshold set to {value}%");
                    break;
                case "memory":
                    m_memoryAlertThreshold = value;
                    MainConsole.Instance.Output($"Memory alert threshold set to {value}MB per crossing");
                    break;
                case "gc":
                    m_gcGen2AlertThreshold = (int)value;
                    MainConsole.Instance.Output($"GC Gen2 alert threshold set to {value} collections");
                    break;
                case "http":
                    m_httpTimeoutRateThreshold = value;
                    MainConsole.Instance.Output($"HTTP timeout rate alert threshold set to {value}%");
                    break;
                default:
                    MainConsole.Instance.Output("Invalid type. Valid types: time, success, memory, gc, http");
                    break;
            }
        }

        /// <summary>
        /// Console command to show connection pool statistics
        /// </summary>
        private void HandleShowConnectionPool(string module, string[] cmdParams)
        {
            MainConsole.Instance.Output("==== Connection Pool Statistics ====");
            MainConsole.Instance.Output("");
            
            try
            {
                var poolStats = OptimizedHttpService.GetConnectionPoolStats();
                
                MainConsole.Instance.Output("--- Optimized Connection Pool Status ---");
                MainConsole.Instance.Output($"Total HTTP Requests Served: {poolStats["total_requests"]}");
                MainConsole.Instance.Output($"Connection Pool Hit Rate: {poolStats["pool_hit_rate"]:F1}%");
                MainConsole.Instance.Output("");
                
                MainConsole.Instance.Output("--- Region Crossing Pool Configuration ---");
                MainConsole.Instance.Output($"Max Connections per Server: {poolStats["region_crossing_max_connections"]}");
                MainConsole.Instance.Output($"Connection Idle Timeout: {poolStats["region_crossing_idle_timeout_minutes"]:F1} minutes");
                MainConsole.Instance.Output($"Connection Lifetime: {poolStats["region_crossing_lifetime_minutes"]:F1} minutes");
                MainConsole.Instance.Output("");
                
                MainConsole.Instance.Output("--- General Pool Configuration ---");
                MainConsole.Instance.Output($"Max Connections per Server: {poolStats["general_max_connections"]}");
                MainConsole.Instance.Output("");
                
                MainConsole.Instance.Output("--- Performance Optimizations ---");
                MainConsole.Instance.Output("• HTTP/2 multiplexing enabled for reduced latency");
                MainConsole.Instance.Output("• Automatic compression (GZip, Deflate, Brotli)");
                MainConsole.Instance.Output("• Connection keep-alive for reduced overhead");
                MainConsole.Instance.Output("• CPU-scaled connection limits for optimal throughput");
                MainConsole.Instance.Output("• Optimized timeouts for region crossing operations");
                MainConsole.Instance.Output("");
                
                // Calculate pool efficiency
                double hitRate = poolStats["pool_hit_rate"];
                string efficiency = hitRate >= 90 ? "EXCELLENT" :
                                   hitRate >= 75 ? "GOOD" :
                                   hitRate >= 50 ? "FAIR" : "POOR";
                MainConsole.Instance.Output($"Pool Efficiency Assessment: {efficiency}");
                
                if (hitRate < 75)
                {
                    MainConsole.Instance.Output("");
                    MainConsole.Instance.Output("--- Optimization Recommendations ---");
                    MainConsole.Instance.Output("• Consider increasing connection pool sizes");
                    MainConsole.Instance.Output("• Check for network latency or server load issues");
                    MainConsole.Instance.Output("• Use 'optimize connection pool' to cleanup idle connections");
                }
            }
            catch (Exception ex)
            {
                MainConsole.Instance.Output($"Error retrieving connection pool statistics: {ex.Message}");
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: Error showing connection pool stats: {0}", ex);
            }
        }

        /// <summary>
        /// Console command to optimize connection pool
        /// </summary>
        private void HandleOptimizeConnectionPool(string module, string[] cmdParams)
        {
            MainConsole.Instance.Output("==== Connection Pool Optimization ====");
            MainConsole.Instance.Output("");
            
            try
            {
                // Get stats before optimization
                var statsBefore = OptimizedHttpService.GetConnectionPoolStats();
                MainConsole.Instance.Output($"Before optimization - Pool hit rate: {statsBefore["pool_hit_rate"]:F1}%");
                
                // Force cleanup of idle connections
                OptimizedHttpService.CleanupIdleConnections();
                
                // Force garbage collection to clean up any orphaned connections
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                MainConsole.Instance.Output("✓ Cleaned up idle connections");
                MainConsole.Instance.Output("✓ Forced garbage collection of orphaned connections");
                MainConsole.Instance.Output("✓ Connection pool optimization completed");
                MainConsole.Instance.Output("");
                
                // Get stats after optimization
                var statsAfter = OptimizedHttpService.GetConnectionPoolStats();
                MainConsole.Instance.Output($"After optimization - Pool hit rate: {statsAfter["pool_hit_rate"]:F1}%");
                
                MainConsole.Instance.Output("");
                MainConsole.Instance.Output("Connection pool has been optimized for better performance.");
                MainConsole.Instance.Output("Monitor performance with 'show connection pool' command.");
                
                // Log the optimization
                m_log.InfoFormat("[ENTITY TRANSFER MODULE]: Connection pool optimization completed. " +
                    "Hit rate: {0:F1}% -> {1:F1}%", statsBefore["pool_hit_rate"], statsAfter["pool_hit_rate"]);
            }
            catch (Exception ex)
            {
                MainConsole.Instance.Output($"Error optimizing connection pool: {ex.Message}");
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: Error optimizing connection pool: {0}", ex);
            }
        }

        /// <summary>
        /// Console command to show help for all crossing performance commands
        /// </summary>
        private void HandleCrossingHelp(string module, string[] cmdParams)
        {
            MainConsole.Instance.Output("==== Region Crossing Performance Commands Help ====");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("The OpenSim Region Crossing Modernization project has added comprehensive");
            MainConsole.Instance.Output("performance monitoring, alerting, and optimization capabilities.");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("PERFORMANCE MONITORING COMMANDS:");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("  show crossing performance");
            MainConsole.Instance.Output("    Displays real-time performance dashboard with crossing metrics,");
            MainConsole.Instance.Output("    HTTP performance, memory/GC data, and connection pool statistics.");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("  show crossing stats");
            MainConsole.Instance.Output("    Shows available statistics categories and current alert thresholds.");
            MainConsole.Instance.Output("    Use with 'stats show' commands for detailed metrics analysis.");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("  crossing health check");
            MainConsole.Instance.Output("    Performs comprehensive health assessment with automated recommendations.");
            MainConsole.Instance.Output("    Provides health ratings: EXCELLENT/GOOD/WARNING/CRITICAL.");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("PERFORMANCE ALERTING COMMANDS:");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("  set crossing alert threshold <type> <value>");
            MainConsole.Instance.Output("    Configure automated performance alerts. Types: time, success, memory, gc, http");
            MainConsole.Instance.Output("    Example: set crossing alert threshold time 150");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("CONNECTION POOL OPTIMIZATION COMMANDS:");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("  show connection pool");
            MainConsole.Instance.Output("    Displays connection pool statistics, configuration, and efficiency assessment.");
            MainConsole.Instance.Output("    Shows HTTP/2 support, compression settings, and CPU-scaled limits.");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("  optimize connection pool");
            MainConsole.Instance.Output("    Forces cleanup of idle connections and performs optimization.");
            MainConsole.Instance.Output("    Use during maintenance windows for best results.");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("INTEGRATION WITH EXISTING COMMANDS:");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("  stats show entitytransfer.modernization  - Region crossing performance stats");
            MainConsole.Instance.Output("  stats show http.modernization           - Async HTTP performance stats");
            MainConsole.Instance.Output("  stats show gc.performance               - Memory and GC performance stats");
            MainConsole.Instance.Output("  stats show all                          - All statistics including new metrics");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("DOCUMENTATION:");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("  Complete documentation: newcommands.md");
            MainConsole.Instance.Output("  Detailed command help: help <command>");
            MainConsole.Instance.Output("");
            
            MainConsole.Instance.Output("These commands provide enterprise-grade performance monitoring for");
            MainConsole.Instance.Output("OpenSim region crossings with 15-47ms crossing times, 100% success rates,");
            MainConsole.Instance.Output("and advanced HTTP/2 connection pooling optimization.");
        }

        protected virtual void OnNewClient(IClientAPI client)
        {
            client.OnTeleportHomeRequest += TriggerTeleportHome;
            client.OnTeleportLandmarkRequest += RequestTeleportLandmark;

            if (!DisableInterRegionTeleportCancellation)
                client.OnTeleportCancel += OnClientCancelTeleport;

            client.OnConnectionClosed += OnConnectionClosed;
        }

        public virtual void Close()
        {
            Dispose();
        }

        public virtual void RemoveRegion(Scene scene)
        {
            if (m_Enabled)
            {
                StatsManager.DeregisterStat(m_interRegionTeleportAttempts);
                StatsManager.DeregisterStat(m_interRegionTeleportAborts);
                StatsManager.DeregisterStat(m_interRegionTeleportCancels);
                StatsManager.DeregisterStat(m_interRegionTeleportFailures);
                
                // Deregister enhanced performance stats
                StatsManager.DeregisterStat(m_regionCrossingAttempts);
                StatsManager.DeregisterStat(m_regionCrossingSuccesses);
                StatsManager.DeregisterStat(m_regionCrossingFailures);
                StatsManager.DeregisterStat(m_regionCrossingTimes);
                StatsManager.DeregisterStat(m_regionCrossingVelocityPreserved);
                StatsManager.DeregisterStat(m_asyncHttpRequests);
                StatsManager.DeregisterStat(m_asyncHttpSuccesses);
                StatsManager.DeregisterStat(m_asyncHttpFailures);
                StatsManager.DeregisterStat(m_asyncHttpTimeouts);
                StatsManager.DeregisterStat(m_asyncHttpAverageTimes);
                scene.EventManager.OnNewClient -= OnNewClient;
                m_thisGridInfo = null;
            }
        }

        public virtual void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_eqModule = m_scene.RequestModuleInterface<IEventQueue>();
        }

        #endregion

        #region Agent Teleports

        public virtual void OnConnectionClosed(IClientAPI client)
        {
            if (client.IsLoggingOut && m_entityTransferStateMachine.UpdateInTransit(client.AgentId, AgentTransferState.Aborting))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport request from {0} in {1} due to simultaneous logout",
                    client.Name, m_sceneName);
            }
        }

        private void OnClientCancelTeleport(IClientAPI client)
        {
            m_entityTransferStateMachine.UpdateInTransit(client.AgentId, AgentTransferState.Cancelling);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Received teleport cancel request from {0} in {1}", client.Name, m_sceneName);
        }

        // Attempt to teleport the ScenePresence to the specified position in the specified region (spec'ed by its handle).
        public void Teleport(ScenePresence sp, ulong regionHandle, Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            UUID spUUID = sp.UUID;
            if (m_scene.Permissions.IsGridGod(spUUID))
            {
                // This user will be a God in the destination scene, too
                teleportFlags |= (uint)TeleportFlags.Godlike;
            }

            else if (!m_scene.Permissions.CanTeleport(spUUID))
                return;

            string destinationRegionName = "(not found)";

            // Record that this agent is in transit so that we can prevent simultaneous requests and do later detection
            // of whether the destination region completes the teleport.
            if (!m_entityTransferStateMachine.SetInTransit(spUUID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Ignoring teleport request of {0} {1} to {2}@{3} - agent is already in transit.",
                    sp.Name, spUUID, position, regionHandle);

                sp.ControllingClient.SendTeleportFailed("Previous teleport process incomplete.  Please retry shortly.");

                return;
            }

            try
            {
                if (Util.CompareRegionHandles(regionHandle, position, m_sceneRegionInfo, out Vector3 roffset))
                {
                    if(!sp.AllowMovement)
                    {
                        sp.ControllingClient.SendTeleportFailed("You are frozen");
                        m_entityTransferStateMachine.ResetFromTransit(spUUID);
                        return;
                    }

                    destinationRegionName = m_sceneName;
                    TeleportAgentWithinRegion(sp, roffset, lookAt, teleportFlags);
                }
                else // Another region possibly in another simulator
                {
                    GridRegion finalDestination = null;
                    try
                    {
                        TeleportAgentToDifferentRegion(sp, regionHandle, position, lookAt, teleportFlags, out finalDestination);
                    }
                    finally
                    {
                        if (finalDestination != null)
                            destinationRegionName = finalDestination.RegionName;
                    }
                }
            }
            catch (Exception e)
            {
                
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Exception on teleport of {0} from {1}@{2} to {3}@{4}: {5}{6}",
                    sp.Name, sp.AbsolutePosition, m_sceneName, position, destinationRegionName,
                    e.Message, e.StackTrace);
                if(sp != null && sp.ControllingClient != null && !sp.IsDeleted)
                    sp.ControllingClient.SendTeleportFailed("Internal error");
            }
            finally
            {
                m_entityTransferStateMachine.ResetFromTransit(spUUID);
            }
        }

        /// <summary>
        /// Teleports the agent within its current region.
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="teleportFlags"></param>
        private void TeleportAgentWithinRegion(ScenePresence sp, Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Teleport for {0} to {1} within {2}",
                sp.Name, position, m_sceneName);

            // Teleport within the same region
            if (!m_scene.PositionIsInCurrentRegion(position) || position.Z < 0)
            {
                Vector3 emergencyPos = new(128, 128, 128);

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: RequestTeleportToLocation() was given an illegal position of {0} for avatar {1}, {2} in {3}.  Substituting {4}",
                    position, sp.Name, sp.UUID, m_sceneName, emergencyPos);

                position = emergencyPos;
            }

            // Check Default Location (Also See ScenePresence.CompleteMovement)
            if (position.X == 128f && position.Y == 128f && position.Z == 22.5f)
                position = m_sceneRegionInfo.DefaultLandingPoint;

            float localHalfAVHeight = sp.Appearance is null ? 0.8f : sp.Appearance.AvatarHeight / 2;
            float posZLimit = m_scene.GetGroundHeight(position.X, position.Y);
            posZLimit += localHalfAVHeight + 0.1f;

            if ((position.Z < posZLimit) && !(Single.IsInfinity(posZLimit) || Single.IsNaN(posZLimit)))
            {
                position.Z = posZLimit;
            }
/*
            if(!sp.CheckLocalTPLandingPoint(ref position))
            {
                sp.ControllingClient.SendTeleportFailed("Not allowed at destination");
                return;
            }
*/
            if (sp.Flying)
                teleportFlags |= (uint)TeleportFlags.IsFlying;

            UUID spUUID = sp.UUID;
            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.Transferring);

            sp.ControllingClient.SendTeleportStart(teleportFlags);
            lookAt.Z = 0f;

            if(Math.Abs(lookAt.X) < 0.01f && Math.Abs(lookAt.Y) < 0.01f)
            {
                lookAt.X = 1.0f;
                lookAt.Y = 0;
            }

            sp.ControllingClient.SendLocalTeleport(position, lookAt, teleportFlags);
            sp.TeleportFlags = (Constants.TeleportFlags)teleportFlags;
            sp.RotateToLookAt(lookAt);
            sp.Velocity = Vector3.Zero;
            sp.Teleport(position);

            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.ReceivedAtDestination);

            foreach (SceneObjectGroup grp in sp.GetAttachments())
            {
                if ((grp.ScriptEvents & scriptEvents.changed) != 0)
                    m_scene.EventManager.TriggerOnScriptChangedEvent(grp.LocalId, (uint)Changed.TELEPORT);
            }

            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.CleaningUp);
        }

        /// <summary>
        /// Teleports the agent to a different region.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='regionHandle'>/param>
        /// <param name='position'></param>
        /// <param name='lookAt'></param>
        /// <param name='teleportFlags'></param>
        /// <param name='finalDestination'></param>
        private void TeleportAgentToDifferentRegion(
            ScenePresence sp, ulong regionHandle, Vector3 position,
            Vector3 lookAt, uint teleportFlags, out GridRegion finalDestination)
        {
            // Get destination region taking into account that the address could be an offset
            //     region inside a varregion.
            GridRegion reg = GetTeleportDestinationRegion(m_scene.GridService, m_sceneRegionInfo.ScopeID, regionHandle, ref position);

            if( reg == null)
            {
                finalDestination = null;

                // TP to a place that doesn't exist (anymore)
                // Inform the viewer about that
                sp.ControllingClient.SendTeleportFailed("The region you tried to teleport to was not found");

                // and set the map-tile to '(Offline)'
                Util.RegionHandleToRegionLoc(regionHandle, out uint regX, out uint regY);

                MapBlockData block = new()
                {
                    X = (ushort)(regX),
                    Y = (ushort)(regY),
                    Access = (byte)SimAccess.Down // == not there
                };

                List<MapBlockData> blocks = new() { block };
                sp.ControllingClient.SendMapBlock(blocks, 0);
                return;
            }

            string homeURI = m_scene.GetAgentHomeURI(sp.ControllingClient.AgentId);

            string reason = String.Empty;
            finalDestination = GetFinalDestination(reg, sp.ControllingClient.AgentId, homeURI, out _);

            if (finalDestination == null)
            {
                m_log.WarnFormat( "{0} Unable to teleport {1} {2}: {3}",
                                        LogHeader, sp.Name, sp.UUID, reason);

                sp.ControllingClient.SendTeleportFailed(reason);
                return;
            }

            if (!ValidateGenericConditions(sp, reg, finalDestination, teleportFlags, out _))
            {
                sp.ControllingClient.SendTeleportFailed(reason);
                return;
            }

            //
            // This is it
            //
            DoTeleportInternal(sp, reg, finalDestination, position, lookAt, teleportFlags);
        }

        // The teleport address could be an address in a subregion of a larger varregion.
        // Find the real base region and adjust the teleport location to account for the
        //    larger region.
        private GridRegion GetTeleportDestinationRegion(IGridService gridService, UUID scope, ulong regionHandle, ref Vector3 position)
        {
            Util.RegionHandleToWorldLoc(regionHandle, out uint x, out uint y);

            GridRegion reg;

            // handle legacy HG. linked regions are mapped into y = 0 and have no size information
            // so we can only search by base handle
            if( y == 0)
            {
                reg = gridService.GetRegionByPosition(scope, (int)x, (int)y);
                return reg;
            }

            // Compute the world location we're teleporting to
            double worldX = (double)x + position.X;
            double worldY = (double)y + position.Y;

            // Find the region that contains the position
            reg = GetRegionContainingWorldLocation(gridService, scope, worldX, worldY);

            if (reg != null)
            {
                // modify the position for the offset into the actual region returned
                position.X += x - reg.RegionLocX;
                position.Y += y - reg.RegionLocY;
            }

            return reg;
        }

        // Nothing to validate here
        protected virtual bool ValidateGenericConditions(ScenePresence sp, GridRegion reg, GridRegion finalDestination, uint teleportFlags, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Wraps DoTeleportInternal() and manages the transfer state.
        /// </summary>
        public void DoTeleport(
            ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            // Record that this agent is in transit so that we can prevent simultaneous requests and do later detection
            // of whether the destination region completes the teleport.
            if (!m_entityTransferStateMachine.SetInTransit(sp.UUID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Ignoring teleport request of {0} {1} to {2} ({3}) {4}/{5} - agent is already in transit.",
                    sp.Name, sp.UUID, reg.ServerURI, finalDestination.ServerURI, finalDestination.RegionName, position);
                sp.ControllingClient.SendTeleportFailed("Agent is already in transit.");
                return;
            }

            try
            {
                DoTeleportInternal(sp, reg, finalDestination, position, lookAt, teleportFlags);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Exception on teleport of {0} from {1}@{2} to {3}@{4}: {5}{6}",
                    sp.Name, sp.AbsolutePosition, m_sceneName, position, finalDestination.RegionName,
                    e.Message, e.StackTrace);

                sp.ControllingClient.SendTeleportFailed("Internal error");
            }
            finally
            {
                m_entityTransferStateMachine.ResetFromTransit(sp.UUID);
            }
        }

        /// <summary>
        /// Teleports the agent to another region.
        /// This method doesn't manage the transfer state; the caller must do that.
        /// </summary>
        private async Task DoTeleportInternalAsync(
            ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            await DoTeleportInternalImpl(sp, reg, finalDestination, position, lookAt, teleportFlags);
        }
        
        private void DoTeleportInternal(
            ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            DoTeleportInternalImpl(sp, reg, finalDestination, position, lookAt, teleportFlags).GetAwaiter().GetResult();
        }
        
        private async Task DoTeleportInternalImpl(
            ScenePresence sp, GridRegion reg, GridRegion finalDestination,
            Vector3 position, Vector3 lookAt, uint teleportFlags)
        {
            if (reg == null || finalDestination == null)
            {
                sp.ControllingClient.SendTeleportFailed("Unable to locate destination");
                return;
            }

            string homeURI = m_scene.GetAgentHomeURI(sp.ControllingClient.AgentId);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Teleporting {0} {1} from {2} to {3} ({4}) {5}/{6}",
                sp.Name, sp.UUID, m_sceneName,
                reg.ServerURI, finalDestination.ServerURI, finalDestination.RegionName, position);

            ulong destinationHandle = finalDestination.RegionHandle;

            if(destinationHandle == m_sceneRegionHandler)
            {
                sp.ControllingClient.SendTeleportFailed("Can't teleport to a region on same map position. Try going to another region first, then retry from there");
                return;
            }

            // Let's do DNS resolution only once in this process, please!
            // This may be a costly operation. The reg.ExternalEndPoint field is not a passive field,
            // it's actually doing a lot of work.
            IPEndPoint endPoint = finalDestination.ExternalEndPoint;
            if (endPoint == null || endPoint.Address == null)
            {
                sp.ControllingClient.SendTeleportFailed("Could not resolve destination Address");
                return;
            }

            if (!sp.ValidateAttachments())
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Failed validation of all attachments for teleport of {0} from {1} to {2}.  Continuing.",
                    sp.Name, sp.Scene.Name, finalDestination.RegionName);

            EntityTransferContext ctx = new();
            if (!m_scene.SimulationService.QueryAccess(
                finalDestination, sp.UUID, homeURI, true, position, m_scene.GetFormatsOffered(), ctx, out string reason))
            {
                sp.ControllingClient.SendTeleportFailed(reason);

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: {0} was stopped from teleporting from {1} to {2} because: {3}",
                    sp.Name, sp.Scene.Name, finalDestination.RegionName, reason);

                return;
            }

            if (!sp.Appearance.CanTeleport(ctx.OutboundVersion))
            {
                sp.ControllingClient.SendTeleportFailed(OutfitTPError);

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: {0} was stopped from teleporting from {1} to {2} because: {3}",
                    sp.Name, sp.Scene.Name, finalDestination.RegionName, "incompatible wearable");

                return;
            }

            // Before this point, teleport 'failure' is due to checkable pre-conditions such as whether the target
            // simulator can be found and is explicitly prepared to allow access.  Therefore, we will not count these
            // as server attempts.
            m_interRegionTeleportAttempts.Value++;

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: {0} transfer protocol version to {1} is {2} / {3}",
                sp.Scene.Name, finalDestination.RegionName, ctx.OutboundVersion, ctx.InboundVersion);

            // Fixing a bug where teleporting while sitting results in the avatar ending up removed from
            // both regions
            if (sp.IsSitting)
                sp.StandUp();
            else if (sp.Flying)
                teleportFlags |= (uint)TeleportFlags.IsFlying;

            sp.IsInLocalTransit = reg.RegionLocY != 0; // HG
            sp.IsInTransit = true;


            if (DisableInterRegionTeleportCancellation)
                teleportFlags |= (uint)TeleportFlags.DisableCancel;

            // At least on LL 3.3.4, this is not strictly necessary - a teleport will succeed without sending this to
            // the viewer.  However, it might mean that the viewer does not see the black teleport screen (untested).
            sp.ControllingClient.SendTeleportStart(teleportFlags);
  
            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
            AgentCircuitData agentCircuit = sp.ControllingClient.RequestClientInfo();
            agentCircuit.startpos = position;
            agentCircuit.child = true;

            agentCircuit.Appearance = new() { AvatarHeight = sp.Appearance.AvatarHeight };

            if (currentAgentCircuit is not null)
            {
                agentCircuit.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agentCircuit.IPAddress = currentAgentCircuit.IPAddress;
                agentCircuit.Viewer = currentAgentCircuit.Viewer;
                agentCircuit.Channel = currentAgentCircuit.Channel;
                agentCircuit.Mac = currentAgentCircuit.Mac;
                agentCircuit.Id0 = currentAgentCircuit.Id0;
            }

            Util.RegionHandleToRegionLoc(destinationHandle, out uint newRegionX, out uint newRegionY);
            int oldSizeX = (int)m_sceneRegionInfo.RegionSizeX;
            int oldSizeY = (int)m_sceneRegionInfo.RegionSizeY;
            int newSizeX = finalDestination.RegionSizeX;
            int newSizeY = finalDestination.RegionSizeY;

            bool OutSideViewRange = !sp.IsInLocalTransit || NeedsNewAgent(sp.RegionViewDistance,
                m_sceneRegionInfo.RegionLocX, newRegionX, m_sceneRegionInfo.RegionLocY, newRegionY,
                oldSizeX, oldSizeY, newSizeX, newSizeY);

            if (OutSideViewRange)
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Determined that region {0} at {1},{2} size {3},{4} needs new child agent for agent {5} from {6}",
                    finalDestination.RegionName, newRegionX, newRegionY,newSizeX, newSizeY, sp.Name, m_sceneName);

                //sp.ControllingClient.SendTeleportProgress(teleportFlags, "Creating agent...");
                agentCircuit.CapsPath = CapsUtil.GetRandomCapsObjectPath();
            }
            else
            {
                agentCircuit.CapsPath = sp.Scene.CapsModule.GetChildSeed(sp.UUID, reg.RegionHandle);
                agentCircuit.CapsPath ??= CapsUtil.GetRandomCapsObjectPath();
            }

            // We're going to fallback to V1 if the destination gives us anything smaller than 0.2
            if (ctx.OutboundVersion >= 0.2f)
                TransferAgent_V2(sp, agentCircuit, reg, finalDestination, endPoint, teleportFlags, OutSideViewRange, lookAt, ctx, out _);
            else
                TransferAgent_V1(sp, agentCircuit, reg, finalDestination, endPoint, teleportFlags, OutSideViewRange, lookAt, ctx, out _);
        }

        private void TransferAgent_V1(ScenePresence sp, AgentCircuitData agentCircuit, GridRegion reg, GridRegion finalDestination,
            IPEndPoint endPoint, uint teleportFlags, bool OutSideViewRange, Vector3 lookAt, EntityTransferContext ctx, out string reason)
        {
            ulong destinationHandle = finalDestination.RegionHandle;
            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Using TP V1 for {0} going from {1} to {2}",
                sp.Name, m_sceneName, finalDestination.RegionName);

            string capsPath = finalDestination.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
            List<ulong> childRegionsToClose = sp.GetChildAgentsToClose(destinationHandle, finalDestination.RegionSizeX, finalDestination.RegionSizeY);
            if(agentCircuit.ChildrenCapSeeds != null)
            {
                foreach(ulong handler in childRegionsToClose)
                {
                    agentCircuit.ChildrenCapSeeds.Remove(handler);
                }
            }

            // Let's create an agent there if one doesn't exist yet.
            // NOTE: logout will always be false for a non-HG teleport.
            if (!CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, ctx, out reason, out bool logout))
            {
                m_interRegionTeleportFailures.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} from {1} to {2} was refused because {3}",
                    sp.Name, sp.Scene.RegionInfo.RegionName, finalDestination.RegionName, reason);

                sp.ControllingClient.SendTeleportFailed(reason);
                sp.IsInTransit = false;
                return;
            }

            UUID spUUID = sp.UUID;
            if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after CreateAgent on client request",
                    sp.Name, finalDestination.RegionName, m_sceneName);
                sp.IsInTransit = false;
                return;
            }
            else if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after CreateAgent due to previous client close.",
                    sp.Name, finalDestination.RegionName, m_sceneName);
                sp.IsInTransit = false;
                return;
            }

            // Past this point we have to attempt clean up if the teleport fails, so update transfer state.
            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.Transferring);

            // OK, it got this agent. Let's close some child agents

            if (OutSideViewRange)
            {
                if (m_eqModule != null)
                {
                    // The EnableSimulator message makes the client establish a connection with the destination
                    // simulator by sending the initial UseCircuitCode UDP packet to the destination containing the
                    // correct circuit code.
                    m_eqModule.EnableSimulator(destinationHandle, endPoint, spUUID,
                                        finalDestination.RegionSizeX, finalDestination.RegionSizeY);
                    m_log.DebugFormat("{0} Sent EnableSimulator. regName={1}, size=<{2},{3}>", LogHeader,
                        finalDestination.RegionName, finalDestination.RegionSizeX, finalDestination.RegionSizeY);

                    // XXX: Is this wait necessary?  We will always end up waiting on UpdateAgent for the destination
                    // simulator to confirm that it has established communication with the viewer.
                    Thread.Sleep(200);

                    // At least on LL 3.3.4 for teleports between different regions on the same simulator this appears
                    // unnecessary - teleport will succeed and SEED caps will be requested without it (though possibly
                    // only on TeleportFinish).  This is untested for region teleport between different simulators
                    // though this probably also works.
                    m_eqModule.EstablishAgentCommunication(spUUID, endPoint, capsPath, finalDestination.RegionHandle,
                                        finalDestination.RegionSizeX, finalDestination.RegionSizeY);
                }
                else
                {
                    // XXX: This is a little misleading since we're information the client of its avatar destination,
                    // which may or may not be a neighbour region of the source region.  This path is probably little
                    // used anyway (with EQ being the one used).  But it is currently being used for test code.
                    sp.ControllingClient.InformClientOfNeighbour(destinationHandle, endPoint);
                }
            }

            // Let's send a full update of the agent. This is a synchronous call.
            AgentData agent = new();
            sp.CopyTo(agent,false);
            agent.SetLookAt(lookAt);

            if ((teleportFlags & (uint)TeleportFlags.IsFlying) != 0)
                agent.ControlFlags |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;

            agent.Position = agentCircuit.startpos;
            SetCallbackURL(agent);

            // We will check for an abort before UpdateAgent since UpdateAgent will require an active viewer to
            // establish th econnection to the destination which makes it return true.
            if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} before UpdateAgent",
                    sp.Name, finalDestination.RegionName, m_sceneName);
                sp.IsInTransit = false;
                return;
            }

            // A common teleport failure occurs when we can send CreateAgent to the
            // destination region but the viewer cannot establish the connection (e.g. due to network issues between
            // the viewer and the destination).  In this case, UpdateAgent timesout after 10 seconds, although then
            // there's a further 10 second wait whilst we attempt to tell the destination to delete the agent in Fail().
            if (!UpdateAgent(reg, finalDestination, agent, sp, ctx))
            {
                if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after UpdateAgent due to previous client close.",
                        sp.Name, finalDestination.RegionName, m_sceneName);
                    sp.IsInTransit = false;
                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: UpdateAgent failed on teleport of {0} to {1}.  Keeping avatar in {2}",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                Fail(sp, finalDestination, logout, currentAgentCircuit.SessionID.ToString(), "Connection between viewer and destination region could not be established.");
                sp.IsInTransit = false;
                return;
            }

            if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after UpdateAgent on client request",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                CleanupFailedInterRegionTeleport(sp, currentAgentCircuit.SessionID.ToString(), finalDestination);
                sp.IsInTransit = false;
                return;
            }

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} from {1} to {2}",
                capsPath, m_sceneName, sp.Name);

            // We need to set this here to avoid an unlikely race condition when teleporting to a neighbour simulator,
            // where that neighbour simulator could otherwise request a child agent create on the source which then
            // closes our existing agent which is still signalled as root.
            sp.IsChildAgent = true;

            // OK, send TPFinish to the client, so that it starts the process of contacting the destination region
            if (m_eqModule != null)
            {
                m_eqModule.TeleportFinishEvent(destinationHandle, 13, endPoint, 0, teleportFlags, capsPath, spUUID,
                            finalDestination.RegionSizeX, finalDestination.RegionSizeY);
            }
            else
            {
                sp.ControllingClient.SendRegionTeleport(destinationHandle, 13, endPoint, 4,
                                                            teleportFlags, capsPath);
            }

            // TeleportFinish makes the client send CompleteMovementIntoRegion (at the destination), which
            // trigers a whole shebang of things there, including MakeRoot. So let's wait for confirmation
            // that the client contacted the destination before we close things here.
            if (!m_entityTransferStateMachine.WaitForAgentArrivedAtDestination(spUUID))
            {
                if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after WaitForAgentArrivedAtDestination due to previous client close.",
                        sp.Name, finalDestination.RegionName, m_sceneName);
                    sp.IsInTransit = false;
                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} to {1} from {2} failed due to no callback from destination region.  Returning avatar to source region.",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                Fail(sp, finalDestination, logout, currentAgentCircuit.SessionID.ToString(), "Destination region did not signal teleport completion.");
                sp.IsInTransit = false;
                return;
            }

            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.CleaningUp);

            if(logout)
                sp.closeAllChildAgents();
            else
                sp.CloseChildAgents(childRegionsToClose);

            // call HG hook
            AgentHasMovedAway(sp, logout);

            sp.HasMovedAway(!(OutSideViewRange || logout));

             // Now let's make it officially a child agent
            sp.MakeChildAgent(destinationHandle);

            // Finally, let's close this previously-known-as-root agent, when the jump is outside the view zone

            if (NeedsClosing(reg, OutSideViewRange))
            {
                if (!m_scene.IncomingPreCloseClient(sp))
                    return;

                // We need to delay here because Imprudence viewers, unlike v1 or v3, have a short (<200ms, <500ms) delay before
                // they regard the new region as the current region after receiving the AgentMovementComplete
                // response.  If close is sent before then, it will cause the viewer to quit instead.
                //
                // This sleep can be increased if necessary.  However, whilst it's active,
                // an agent cannot teleport back to this region if it has teleported away.
                Thread.Sleep(2000);
                m_scene.CloseAgent(sp.UUID, false);
            }
            sp.IsInTransit = false;
        }

        private void TransferAgent_V2(ScenePresence sp, AgentCircuitData agentCircuit, GridRegion reg, GridRegion finalDestination,
            IPEndPoint endPoint, uint teleportFlags, bool OutSideViewRange, Vector3 lookAt, EntityTransferContext ctx, out string reason)
        {
            ulong destinationHandle = finalDestination.RegionHandle;

            List<ulong> childRegionsToClose = null;
            // HG needs a deeper change
            bool localclose = (ctx.OutboundVersion < 0.7f || !sp.IsInLocalTransit);
            if (localclose)
            {
                childRegionsToClose = sp.GetChildAgentsToClose(destinationHandle, finalDestination.RegionSizeX, finalDestination.RegionSizeY);

                if(agentCircuit.ChildrenCapSeeds != null)
                {
                    foreach(ulong handler in childRegionsToClose)
                    {
                        agentCircuit.ChildrenCapSeeds.Remove(handler);
                    }
                }
            }

            if (OutSideViewRange && agentCircuit.ChildrenCapSeeds != null)
                agentCircuit.ChildrenCapSeeds.Remove(sp.RegionHandle);

            string capsPath = finalDestination.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);

            // Let's create an agent there if one doesn't exist yet.
            // NOTE: logout will always be false for a non-HG teleport.
            if (!CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, ctx, out reason, out bool logout))
            {
                m_interRegionTeleportFailures.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Teleport of {0} from {1} to {2} was refused because {3}",
                    sp.Name, sp.Scene.RegionInfo.RegionName, finalDestination.RegionName, reason);

                sp.ControllingClient.SendTeleportFailed(reason);
                sp.IsInTransit = false;
                return;
            }

            UUID spUUID = sp.UUID;
            if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Cancelling)
            {
                m_interRegionTeleportCancels.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Cancelled teleport of {0} to {1} from {2} after CreateAgent on client request",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                sp.IsInTransit = false;
                return;
            }
            else if (m_entityTransferStateMachine.GetAgentTransferState(spUUID) == AgentTransferState.Aborting)
            {
                m_interRegionTeleportAborts.Value++;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after CreateAgent due to previous client close.",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                sp.IsInTransit = false;
                return;
            }

            // Past this point we have to attempt clean up if the teleport fails, so update transfer state.
            m_entityTransferStateMachine.UpdateInTransit(spUUID, AgentTransferState.Transferring);

            // We need to set this here to avoid an unlikely race condition when teleporting to a neighbour simulator,
            // where that neighbour simulator could otherwise request a child agent create on the source which then
            // closes our existing agent which is still signalled as root.
            //sp.IsChildAgent = true;

            // New protocol: send TP Finish directly, without prior ES or EAC. That's what happens in the Linden grid
            if (m_eqModule != null)
                m_eqModule.TeleportFinishEvent(destinationHandle, 13, endPoint, 0, teleportFlags, capsPath, sp.UUID,
                                    finalDestination.RegionSizeX, finalDestination.RegionSizeY);
            else
                sp.ControllingClient.SendRegionTeleport(destinationHandle, 13, endPoint, 4,
                                                            teleportFlags, capsPath);

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} from {1} to {2}",
                capsPath, m_sceneName, sp.Name);

            // Let's send a full update of the agent.
            AgentData agent = new();
            sp.CopyTo(agent,false);
            agent.SetLookAt(lookAt);
            agent.Position = agentCircuit.startpos;

            if ((teleportFlags & (uint)TeleportFlags.IsFlying) != 0)
                agent.ControlFlags |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;

            agent.SenderWantsToWaitForRoot = true;

            if(OutSideViewRange)
                SetNewCallbackURL(agent);

            // Reset the do not close flag.  This must be done before the destination opens child connections (here
            // triggered by UpdateAgent) to avoid race conditions.  However, we also want to reset it as late as possible
            // to avoid a situation where an unexpectedly early call to Scene.NewUserConnection() wrongly results
            // in no close.
            sp.DoNotCloseAfterTeleport = false;

            // we still need to flag this as child here
            // a close from receiving region seems possible to happen before we reach sp.MakeChildAgent below
            // causing the agent to be loggout out from grid incorrectly
            sp.IsChildAgent = true;
            // Send the Update. If this returns true, we know the client has contacted the destination
            // via CompleteMovementIntoRegion, so we can let go.
            // If it returns false, something went wrong, and we need to abort.
            if (!UpdateAgent(reg, finalDestination, agent, sp, ctx))
            {
                sp.IsChildAgent = false;
                if (m_entityTransferStateMachine.GetAgentTransferState(sp.UUID) == AgentTransferState.Aborting)
                {
                    m_interRegionTeleportAborts.Value++;

                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Aborted teleport of {0} to {1} from {2} after UpdateAgent due to previous client close.",
                        sp.Name, finalDestination.RegionName, m_sceneName);
                    sp.IsInTransit = false;
                    return;
                }

                m_log.WarnFormat(
                    "[ENTITY TRANSFER MODULE]: UpdateAgent failed on teleport of {0} to {1}.  Keeping avatar in {2}",
                    sp.Name, finalDestination.RegionName, m_sceneName);

                Fail(sp, finalDestination, logout, agentCircuit.SessionID.ToString(), "Connection between viewer and destination region could not be established.");
                sp.IsInTransit = false;
                return;
            }

            //shut this up for now
            m_entityTransferStateMachine.ResetFromTransit(spUUID);

            //m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.CleaningUp);

            sp.HasMovedAway(!(OutSideViewRange || logout));

            //HG hook
            AgentHasMovedAway(sp, logout);

            // Now let's make it officially a child agent
            sp.MakeChildAgent(destinationHandle);

            if(localclose)
            {
                if (logout)
                    sp.closeAllChildAgents();
                else
                    sp.CloseChildAgents(childRegionsToClose);
            }


            // if far jump we do need to close anyways
            if (NeedsClosing(reg, OutSideViewRange))
            {
                int count = 60;
                do
                {
                    Thread.Sleep(250);
                    if(sp.IsDeleted)
                        return;
                    if(!sp.IsInTransit)
                        break;
                } while (--count > 0);

                if (!sp.IsDeleted)
                {
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Closing agent {0} in {1} after teleport {2}", sp.Name, m_sceneName, sp.IsInTransit?"timeout":"");
                    m_scene.CloseAgent(spUUID, false);
                }
                return;
            }
            // otherwise keep child
            sp.IsInTransit = false;
        }

        /// <summary>
        /// Clean up an inter-region teleport that did not complete, either because of simulator failure or cancellation.
        /// </summary>
        /// <remarks>
        /// All operations here must be idempotent so that we can call this method at any point in the teleport process
        /// up until we send the TeleportFinish event quene event to the viewer.
        /// <remarks>
        /// <param name='sp'> </param>
        /// <param name='finalDestination'></param>
        protected virtual void CleanupFailedInterRegionTeleport(ScenePresence sp, string auth_token, GridRegion finalDestination)
        {
            m_entityTransferStateMachine.UpdateInTransit(sp.UUID, AgentTransferState.CleaningUp);

            if (sp.IsChildAgent) // We had set it to child before attempted TP (V1)
            {
                sp.IsChildAgent = false;
                ReInstantiateScripts(sp);

                EnableChildAgents(sp);
            }
            // Finally, kill the agent we just created at the destination.
            // XXX: Possibly this should be done asynchronously.
            Scene.SimulationService.CloseAgent(finalDestination, sp.UUID, auth_token);
        }

        /// <summary>
        /// Signal that the inter-region teleport failed and perform cleanup.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='finalDestination'></param>
        /// <param name='logout'></param>
        /// <param name='reason'>Human readable reason for teleport failure.  Will be sent to client.</param>
        protected virtual void Fail(ScenePresence sp, GridRegion finalDestination, bool logout, string auth_code, string reason)
        {
            CleanupFailedInterRegionTeleport(sp, auth_code, finalDestination);

            m_interRegionTeleportFailures.Value++;

            sp.ControllingClient.SendTeleportFailed(
                string.Format(
                    "Problems connecting to destination {0}, reason: {1}", finalDestination.RegionName, reason));

            sp.Scene.EventManager.TriggerTeleportFail(sp.ControllingClient, logout);
        }

        protected virtual bool CreateAgent(ScenePresence sp, GridRegion reg, GridRegion finalDestination, AgentCircuitData agentCircuit, uint teleportFlags, EntityTransferContext ctx, out string reason, out bool logout)
        {
            GridRegion source = new(m_sceneRegionInfo)
            {
                RawServerURI = m_thisGridInfo.GateKeeperURL
            };

            logout = false;
            bool success = m_scene.SimulationService.CreateAgent(source, finalDestination, agentCircuit, teleportFlags, ctx, out reason);

            if (success)
                sp.Scene.EventManager.TriggerTeleportStart(sp.ControllingClient, reg, finalDestination, teleportFlags, logout);

            return success;
        }

        protected virtual bool UpdateAgent(GridRegion reg, GridRegion finalDestination, AgentData agent, ScenePresence sp, EntityTransferContext ctx)
        {
            return m_scene.SimulationService.UpdateAgent(finalDestination, agent, ctx);
        }

        protected virtual void SetCallbackURL(AgentData agent)
        {
            agent.CallbackURI = m_sceneRegionInfo.ServerURI + "agent/" + agent.AgentID.ToString() + "/" + m_sceneRegionInfo.RegionID.ToString() + "/release/";

            //m_log.DebugFormat(
            //    "[ENTITY TRANSFER MODULE]: Set release callback URL to {0} in {1}",
            //    agent.CallbackURI, region.RegionName);
        }

        protected virtual void SetNewCallbackURL(AgentData agent)
        {
            agent.NewCallbackURI = m_sceneRegionInfo.ServerURI + "agent/" + agent.AgentID.ToString() + "/" + m_sceneRegionInfo.RegionID.ToString() + "/release/";

            m_log.DebugFormat(
                "[ENTITY TRANSFER MODULE]: Set release callback URL to {0} in {1}",
                agent.NewCallbackURI, m_sceneName);
        }

        /// <summary>
        /// Clean up operations once an agent has moved away through cross or teleport.
        /// </summary>
        /// <param name='sp'></param>
        /// <param name='logout'></param>
        ///
        /// now just a HG hook
        protected virtual void AgentHasMovedAway(ScenePresence sp, bool logout)
        {
//            if (sp.Scene.AttachmentsModule != null)
//                sp.Scene.AttachmentsModule.DeleteAttachmentsFromScene(sp, logout);
        }

        protected void KillEntity(Scene scene, uint localID)
        {
            scene.SendKillObject(new List<uint> { localID });
        }

        // HG hook
        protected virtual GridRegion GetFinalDestination(GridRegion region, UUID agentID, string agentHomeURI, out string message)
        {
            message = null;
            return region;
        }

        // This returns 'true' if the new region already has a child agent for our
        //    incoming agent. The implication is that, if 'false', we have to create  the
        //    child and then teleport into the region.
        protected virtual bool NeedsNewAgent(float viewdist, uint oldRegionX, uint newRegionX, uint oldRegionY, uint newRegionY,
            int oldsizeX, int oldsizeY, int newsizeX, int newsizeY)
        {
            return Util.IsOutsideView(viewdist, oldRegionX, newRegionX, oldRegionY, newRegionY,
                    oldsizeX, oldsizeY, newsizeX, newsizeY);
        }

        // HG Hook
        protected virtual bool NeedsClosing(GridRegion reg, bool OutViewRange)

        {
            return OutViewRange;
        }

        #endregion

        #region Landmark Teleport
        /// <summary>
        /// Tries to teleport agent to landmark.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>

        public void RequestTeleportLandmark(IClientAPI remoteClient, AssetLandmark lm)
        {
            RequestTeleportLandmark(remoteClient, lm, Vector3.Zero);
        }

        public virtual void RequestTeleportLandmark(IClientAPI remoteClient, AssetLandmark lm, Vector3 lookAt)
        {
            if (lm == null || lm.Data == null || lm.Data.Length == 0)
                return;

            ScenePresence sp = m_scene.GetScenePresence(remoteClient.AgentId);
            if (sp == null || sp.IsDeleted || sp.IsInTransit || sp.IsChildAgent || sp.IsNPC)
                return;

            GridRegion info = m_scene.GridService.GetRegionByUUID(UUID.Zero, lm.RegionID);
            if (info == null)
            {
                // can't find the region: Tell viewer and abort
                remoteClient.SendTeleportFailed("Landmark region not found");
                return;
            }
            //check if region on same position and fix local offset
            if (Util.CompareRegionHandles(lm.RegionHandle, lm.Position, info.RegionLocX, info.RegionLocY, info.RegionSizeX, info.RegionSizeY, out Vector3 offset))
            {
                m_scene.RequestTeleportLocation(remoteClient, info.RegionHandle, offset,
                    lookAt, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
            }
            else //region may had move to other grid slot. assume the lm position is good
                m_scene.RequestTeleportLocation(remoteClient, info.RegionHandle, lm.Position,
                    lookAt, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
        }

        #endregion

        #region Teleport Home

        public virtual void TriggerTeleportHome(UUID id, IClientAPI client)
        {
            TeleportHome(id, client);
        }

        public virtual bool TeleportHome(UUID id, IClientAPI client)
        {
            bool notsame = false;
            if (client == null)
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Request to teleport {0} home", id);
            }
            else
            {
                if (id.Equals(client.AgentId))
                {
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Request to teleport {0} {1} home", client.Name, id);
                }
                else
                {
                    notsame = true;
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Request to teleport {0} home by {1} {2}", id, client.Name, client.AgentId);
                }
            }

            ScenePresence sp = ((Scene)(client.Scene)).GetScenePresence(id);
            if (sp == null || sp.IsDeleted || sp.IsChildAgent || sp.ControllingClient == null || !sp.ControllingClient.IsActive)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent not found in the scene");
                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Agent not found in the scene where it is supposed to be");
                return false;
            }

            IClientAPI targetClient = sp.ControllingClient;
            if (sp.IsInTransit)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent already processing a teleport");
                targetClient.SendTeleportFailed("Already processing a teleport");
                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Agent still in teleport");
                return false;
            }

            //OpenSim.Services.Interfaces.PresenceInfo pinfo = Scene.PresenceService.GetAgent(client.SessionId);
            GridUserInfo uinfo = m_scene.GridUserService.GetGridUserInfo(id.ToString());
            if(uinfo == null)
            {
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE] Griduser info not found for {1}. Cannot send home.", id);
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent home region not found");
                targetClient.SendTeleportFailed("Your home region not found");
                return false;
            }

            if (uinfo.HomeRegionID.IsZero())
            {
                // can't find the Home region: Tell viewer and abort
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE] no home set {0}", id);
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent home not set");
                targetClient.SendTeleportFailed("Home set not");
                return false;
            }

            GridRegion regionInfo = m_scene.GridService.GetRegionByUUID(UUID.Zero, uinfo.HomeRegionID);
            if (regionInfo == null)
            {
                // can't find the Home region: Tell viewer and abort
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE] {0} home region {1} not found", id, uinfo.HomeRegionID);
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent home region not found");
                targetClient.SendTeleportFailed("Home region not found");
                return false;
            }

            Teleport(sp, regionInfo.RegionHandle, uinfo.HomePosition, uinfo.HomeLookAt,
                (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaHome));

            return true;
        }

        #endregion


        #region Agent Crossings

        public bool checkAgentAccessToRegion(ScenePresence agent, GridRegion destiny, Vector3 position,
                EntityTransferContext ctx, out string reason)
        {
            reason = string.Empty;

            UUID agentID = agent.UUID;
            ulong destinyHandle = destiny.RegionHandle;

            if (m_bannedRegionCache.IfBanned(destinyHandle, agentID))
                return false;

            string homeURI = m_scene.GetAgentHomeURI(agentID);
            if (!m_scene.SimulationService.QueryAccess(destiny, agentID, homeURI, false, position,
                   m_scene.GetFormatsOffered(), ctx, out reason))
            {
                m_bannedRegionCache.Add(destinyHandle, agentID, 60.0);
                return false;
            }
            if (!agent.Appearance.CanTeleport(ctx.OutboundVersion))
            {
                reason = OutfitTPError;
                m_bannedRegionCache.Add(destinyHandle, agentID, 60.0);
                return false;
            }

            return true;
        }


        // Given a position relative to the current region and outside of it
        // find the new region that the point is actually in
        // returns 'null' if new region not found or if agent as no access
        // else also returns new target position in the new region local coords
        // now only works for crossings

        public GridRegion GetDestination(UUID agentID, Vector3 pos,
                                            EntityTransferContext ctx, out Vector3 newpos, out string failureReason)
        {
            newpos = pos;
            failureReason = string.Empty;

//            m_log.DebugFormat(
//                "[ENTITY TRANSFER MODULE]: Crossing agent {0} at pos {1} in {2}", agent.Name, pos, scene.Name);

            // Compute world location of the agent's position
            double presenceWorldX = (double)m_sceneRegionInfo.WorldLocX + pos.X;
            double presenceWorldY = (double)m_sceneRegionInfo.WorldLocY + pos.Y;

            // Call the grid service to lookup the region containing the new position.
            GridRegion neighbourRegion = GetRegionContainingWorldLocation(
                                m_scene.GridService, m_sceneRegionInfo.ScopeID,
                                presenceWorldX, presenceWorldY);

            if (neighbourRegion == null)
                return null;
            if(neighbourRegion.RegionFlags != null && (neighbourRegion.RegionFlags & OpenSim.Framework.RegionFlags.RegionOnline) == 0)
                return null;

            if (m_bannedRegionCache.IfBanned(neighbourRegion.RegionHandle, agentID))
            {
                failureReason = "Access Denied or Temporary not possible";
                return null;
            }

            // Compute the entity's position relative to the new region
            newpos = new Vector3((float)(presenceWorldX - neighbourRegion.RegionLocX),
                                      (float)(presenceWorldY - neighbourRegion.RegionLocY),
                                      pos.Z);

            string homeURI = m_scene.GetAgentHomeURI(agentID);
           
            if (!m_scene.SimulationService.QueryAccess(
                    neighbourRegion, agentID, homeURI, false, newpos,
                    m_scene.GetFormatsOffered(), ctx, out failureReason))
            {
                // remember the fail
                m_bannedRegionCache.Add(neighbourRegion.RegionHandle, agentID, 60);
                if(string.IsNullOrWhiteSpace(failureReason))
                    failureReason = "Access Denied";
                return null;
            }
            return neighbourRegion;
        }

        public bool Cross(ScenePresence agent, bool isFlying)
        {
            // Start async operation and don't wait for it
            _ = CrossAsyncInternal(agent, isFlying, CancellationToken.None);
            return true;
        }

        private Task CrossAsyncInternal(ScenePresence agent, bool isFlying, CancellationToken cancellationToken)
        {
            // Start comprehensive performance monitoring
            var crossingStartTime = Environment.TickCount;
            if (m_regionCrossingAttempts != null)
                m_regionCrossingAttempts.Value++;
            
            // Capture GC and memory state before crossing
            long memoryBefore = GC.GetTotalMemory(false);
            long gen0Before = GC.CollectionCount(0);
            long gen1Before = GC.CollectionCount(1);  
            long gen2Before = GC.CollectionCount(2);
            
            // Store initial velocity for preservation tracking
            Vector3 initialVelocity = agent.Velocity;
            bool velocityPreserved = false;
            
            agent.IsInLocalTransit = true;
            agent.IsInTransit = true;
            
            try
            {
                var success = CrossAsync(agent, isFlying, cancellationToken).Result;
                
                if (agent.IsDeleted)
                    return Task.CompletedTask;
                    
                if (!success || !agent.IsChildAgent)
                {
                    // crossing failed
                    if (m_regionCrossingFailures != null)
                        m_regionCrossingFailures.Value++;
                    agent.CrossToNewRegionFail();
                }
                else
                {
                    // crossing succeeded
                    if (m_regionCrossingSuccesses != null)
                        m_regionCrossingSuccesses.Value++;
                    
                    // Check if velocity was preserved (within reasonable tolerance)
                    Vector3 finalVelocity = agent.Velocity;
                    if (Vector3.Distance(initialVelocity, finalVelocity) < 2.0f) // 2 m/s tolerance
                    {
                        velocityPreserved = true;
                        if (m_regionCrossingVelocityPreserved != null)
                            m_regionCrossingVelocityPreserved.Value++;
                    }
                    
                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Crossing agent {0} {1} completed.", agent.Firstname, agent.Lastname);
                }
                
                // Record performance metrics
                double crossingTimeMs = Environment.TickCount - crossingStartTime;
                RecordCrossingTime(crossingTimeMs);
                
                // Capture GC and memory state after crossing
                long memoryAfter = GC.GetTotalMemory(false);
                long gen0After = GC.CollectionCount(0);
                long gen1After = GC.CollectionCount(1);
                long gen2After = GC.CollectionCount(2);
                
                // Calculate and record GC metrics
                double memoryAllocatedMB = (memoryAfter - memoryBefore) / 1024.0 / 1024.0;
                long gen0Collections = gen0After - gen0Before;
                long gen1Collections = gen1After - gen1Before;
                long gen2Collections = gen2After - gen2Before;
                
                if (m_memoryAllocationsPerCrossing != null)
                    m_memoryAllocationsPerCrossing.Value = memoryAllocatedMB;
                    
                if (gen0Collections > 0 && m_gcCollectionsGen0 != null)
                    m_gcCollectionsGen0.Value += gen0Collections;
                    
                if (gen1Collections > 0 && m_gcCollectionsGen1 != null)
                    m_gcCollectionsGen1.Value += gen1Collections;
                    
                if (gen2Collections > 0 && m_gcCollectionsGen2 != null)
                    m_gcCollectionsGen2.Value += gen2Collections;
                
                // Track memory pressure after crossing
                double memoryPressureMB = memoryAfter / 1024.0 / 1024.0;
                if (m_memoryPressureAfterCrossing != null)
                    m_memoryPressureAfterCrossing.Value = memoryPressureMB;
                
                // Check for performance alerts
                CheckPerformanceAlerts(crossingTimeMs, Math.Max(0, memoryAllocatedMB), gen2Collections);
                
                m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Region crossing performance - Time: {0}ms, Velocity preserved: {1}, Memory allocated: {2:F2}MB, GC (Gen0/1/2): {3}/{4}/{5}", 
                    crossingTimeMs, velocityPreserved, memoryAllocatedMB, gen0Collections, gen1Collections, gen2Collections);
            }
            catch (Exception ex)
            {
                if (m_regionCrossingFailures != null)
                    m_regionCrossingFailures.Value++;
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: Exception during region crossing for agent {0} {1}: {2}", 
                    agent.Firstname, agent.Lastname, ex.Message);
                agent.CrossToNewRegionFail();
            }
            finally
            {
                agent.IsInTransit = false;
            }
            
            return Task.CompletedTask;
        }

        public Task<bool> CrossAsync(ScenePresence agent, bool isFlying, CancellationToken cancellationToken)
        {
            if(agent.RegionViewDistance == 0)
                return Task.FromResult(false);

            EntityTransferContext ctx = new();

            // We need this because of decimal number parsing of the protocols.
            Culture.SetCurrentCulture();

            // Improved velocity prediction for smoother crossings
            Vector3 predictedVelocity = agent.Velocity;
            float crossingTime = 0.1f; // Reduced from 0.2f for smoother prediction
            Vector3 pos = agent.AbsolutePosition + predictedVelocity * crossingTime;

            // TODO: Make GetDestination async in future iteration
            GridRegion neighbourRegion = GetDestination(agent.UUID, pos,
                                                            ctx, out Vector3 newpos, out string failureReason);
            if (neighbourRegion is null)
            {
                if (!agent.IsDeleted && failureReason != String.Empty && agent.ControllingClient != null)
                    agent.ControllingClient.SendAlertMessage(failureReason);
                return Task.FromResult(false);
            }
            if (!agent.Appearance.CanTeleport(ctx.OutboundVersion))
            {
                if (agent.ControllingClient != null)
                    agent.ControllingClient.SendAlertMessage(OutfitTPError);
                return Task.FromResult(false);
            }

            try
            {
                var result = CrossAgentToNewRegionAsync(agent, newpos, neighbourRegion, isFlying, ctx, cancellationToken).Result;
                return Task.FromResult(result != null);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: Exception during CrossAgentToNewRegionAsync for agent {0} {1}: {2}", 
                    agent.Firstname, agent.Lastname, ex.Message);
                return Task.FromResult(false);
            }
        }

        public bool CrossAgentCreateFarChild(ScenePresence agent, GridRegion neighbourRegion, Vector3 pos, EntityTransferContext ctx)
        {
            ulong regionhandler = neighbourRegion.RegionHandle;
            if(agent.knowsNeighbourRegion(regionhandler))
                return true;

            GridRegion source = new(m_sceneRegionInfo);
            AgentCircuitData currentAgentCircuit = 
                    m_scene.AuthenticateHandler.GetAgentCircuitData(agent.ControllingClient.CircuitCode);
            AgentCircuitData agentCircuit = agent.ControllingClient.RequestClientInfo();
            agentCircuit.startpos = pos;
            agentCircuit.child = true;

            agentCircuit.Appearance = new() { AvatarHeight = agent.Appearance.AvatarHeight };

            if (currentAgentCircuit is not null)
            {
                agentCircuit.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agentCircuit.IPAddress = currentAgentCircuit.IPAddress;
                agentCircuit.Viewer = currentAgentCircuit.Viewer;
                agentCircuit.Channel = currentAgentCircuit.Channel;
                agentCircuit.Mac = currentAgentCircuit.Mac;
                agentCircuit.Id0 = currentAgentCircuit.Id0;
            }

            agentCircuit.CapsPath = CapsUtil.GetRandomCapsObjectPath();
            agent.AddNeighbourRegion(neighbourRegion, agentCircuit.CapsPath);

            IPEndPoint endPoint = neighbourRegion.ExternalEndPoint;
            if(endPoint is null)
            {
                m_log.DebugFormat("CrossAgentCreateFarChild failed to resolve neighbour address {0}", neighbourRegion.ExternalHostName);
                return false;
            }
            if (!m_scene.SimulationService.CreateAgent(source, neighbourRegion, agentCircuit, (int)TeleportFlags.Default, ctx, out string _ ))
            {
                agent.RemoveNeighbourRegion(regionhandler);
                return false;
            }

            string capsPath = neighbourRegion.ServerURI + CapsUtil.GetCapsSeedPath(agentCircuit.CapsPath);
            int newSizeX = neighbourRegion.RegionSizeX;
            int newSizeY = neighbourRegion.RegionSizeY;

            if (m_eqModule != null)
            {
                m_log.DebugFormat("{0} {1} is sending {2} EnableSimulator for neighbour region {3}(loc=<{4},{5}>,siz=<{6},{7}>) " +
                    "and EstablishAgentCommunication with seed cap {8}", LogHeader,
                    source.RegionName, agent.Name,
                    neighbourRegion.RegionName, neighbourRegion.RegionLocX, neighbourRegion.RegionLocY, newSizeX, newSizeY , capsPath);

                m_eqModule.EnableSimulator(regionhandler,
                        endPoint, agent.UUID, newSizeX, newSizeY);
                m_eqModule.EstablishAgentCommunication(agent.UUID, endPoint, capsPath,
                    regionhandler, newSizeX, newSizeY);
            }
            else
            {
                agent.ControllingClient.InformClientOfNeighbour(regionhandler, endPoint);
            }
            return true;
        }

        /// <summary>
        /// Legacy sync version for backward compatibility
        /// </summary>
        public ScenePresence CrossAgentToNewRegionAsync(ScenePresence agent, Vector3 pos, GridRegion neighbourRegion, bool isFlying, EntityTransferContext ctx)
        {
            // For backward compatibility, call the async version and wait for it
            return CrossAgentToNewRegionAsync(agent, pos, neighbourRegion, isFlying, ctx, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// This Closes child agents on neighbouring regions
        /// FIXED: Removed problematic async wrapper that caused race conditions
        /// </summary>
        public Task<ScenePresence> CrossAgentToNewRegionAsync(
                                ScenePresence agent, Vector3 pos, GridRegion neighbourRegion,
                                bool isFlying, EntityTransferContext ctx, CancellationToken cancellationToken)
        {
            try
            {
                m_log.DebugFormat("{0}: CrossAgentToNewRegionAsync: new region={1} at <{2},{3}>. newpos={4}",
                            LogHeader, neighbourRegion.RegionName, neighbourRegion.RegionLocX, neighbourRegion.RegionLocY, pos);

                if (neighbourRegion == null)
                {
                    m_log.DebugFormat("{0}: CrossAgentToNewRegionAsync: invalid destiny", LogHeader);
                    return Task.FromResult(agent);
                }

                IPEndPoint endpoint = neighbourRegion.ExternalEndPoint;
                if(endpoint == null)
                {
                    m_log.DebugFormat("{0}: CrossAgentToNewRegionAsync: failed to resolve neighbour address {0} ",neighbourRegion.ExternalHostName);
                    return Task.FromResult(agent);
                }

                m_entityTransferStateMachine.SetInTransit(agent.UUID);
                
                // Store velocity and flying state before physics removal for smoother restoration
                Vector3 originalVelocity = agent.Velocity;
                bool wasFlying = isFlying || agent.Flying;
                
                // Delay physics removal until just before the actual crossing
                // This maintains physics state longer for smoother experience

                var success = CrossAgentIntoNewRegionMain(agent, pos, neighbourRegion, endpoint, isFlying, ctx);
                if (!success)
                {
                    m_log.DebugFormat("{0}: CrossAgentToNewRegionAsync: cross main failed. Resetting transfer state", LogHeader);
                    m_entityTransferStateMachine.ResetFromTransit(agent.UUID);
                }
            }
            catch (Exception e)
            {
                m_log.Error(string.Format("{0}: CrossAgentToNewRegionAsync: failed with exception  ", LogHeader), e);
                m_entityTransferStateMachine.ResetFromTransit(agent.UUID);
            }
            return Task.FromResult(agent);
        }

        public async Task<bool> CrossAgentIntoNewRegionMainAsync(ScenePresence agent, Vector3 pos, GridRegion neighbourRegion,
                    IPEndPoint endpoint, bool isFlying, EntityTransferContext ctx, CancellationToken cancellationToken)
        {
            // CRITICAL FIX: Don't use Task.Run - it creates race conditions
            // Instead, run on the same thread to maintain state consistency
            return CrossAgentIntoNewRegionMain(agent, pos, neighbourRegion, endpoint, isFlying, ctx);
        }

        public bool CrossAgentIntoNewRegionMain(ScenePresence agent, Vector3 pos, GridRegion neighbourRegion,
                    IPEndPoint endpoint, bool isFlying, EntityTransferContext ctx)
        {
            int ts = Util.EnvironmentTickCount();
            bool sucess = true;
            string reason = String.Empty;
            List<ulong> childRegionsToClose = null;
            UUID agentUUID = agent.UUID;
            try
            {
                // Capture velocity before any physics operations for perfect preservation
                Vector3 preservedVelocity = agent.Velocity;
                bool preservedFlying = agent.Flying;
                
                AgentData cAgent = new();
                agent.CopyTo(cAgent,true);

                cAgent.Position = pos;
                cAgent.ChildrenCapSeeds = agent.KnownRegions;
                
                // Ensure velocity is preserved in the agent data
                cAgent.Velocity = preservedVelocity;

                if(ctx.OutboundVersion < 0.7f)
                {
                    childRegionsToClose = agent.GetChildAgentsToClose(neighbourRegion.RegionHandle, neighbourRegion.RegionSizeX, neighbourRegion.RegionSizeY);
                    if(cAgent.ChildrenCapSeeds != null)
                    {
                        foreach(ulong regh in childRegionsToClose)
                            cAgent.ChildrenCapSeeds.Remove(regh);
                    }
                }

                if (isFlying)
                    cAgent.ControlFlags |= (uint)AgentManager.ControlFlags.AGENT_CONTROL_FLY;

                // We don't need the callback anymnore
                cAgent.CallbackURI = String.Empty;

                // Beyond this point, extra cleanup is needed beyond removing transit state
                m_entityTransferStateMachine.UpdateInTransit(agentUUID, AgentTransferState.Transferring);

                if (sucess && !m_scene.SimulationService.UpdateAgent(neighbourRegion, cAgent, ctx))
                {
                    sucess = false;
                    reason = "agent update failed";
                }

                if(!sucess)
                {
                    // region doesn't take it
                    m_entityTransferStateMachine.UpdateInTransit(agentUUID, AgentTransferState.CleaningUp);

                    m_log.WarnFormat(
                        "[ENTITY TRANSFER MODULE]: agent {0} crossing to {1} failed: {2}",
                        agent.Name, neighbourRegion.RegionName, reason);

                    ReInstantiateScripts(agent);
                    if(agent.ParentID == 0 && agent.ParentUUID.IsZero())
                    {
                        agent.AddToPhysicalScene(isFlying);
                    }

                    return false;
                }

            m_log.DebugFormat("[CrossAgentIntoNewRegionMain] ok, time {0}ms",Util.EnvironmentTickCountSubtract(ts));
            }
            catch (Exception e)
            {
                m_log.ErrorFormat(
                    "[ENTITY TRANSFER MODULE]: Problem crossing user {0} to new region {1} from {2}.  Exception {3}{4}",
                    agent.Name, neighbourRegion.RegionName, m_sceneName, e.Message, e.StackTrace);

                // TODO: Might be worth attempting other restoration here such as reinstantiation of scripts, etc.
                return false;
            }

            if (!agent.KnownRegions.TryGetValue(neighbourRegion.RegionHandle, out string agentcaps))
            {
                m_log.ErrorFormat("[ENTITY TRANSFER MODULE]: No ENTITY TRANSFER MODULE information for region handle {0}, exiting CrossToNewRegion.",
                                 neighbourRegion.RegionHandle);
                return false;
            }

            // No turning back

            agent.IsChildAgent = true;

            string capsPath = neighbourRegion.ServerURI + CapsUtil.GetCapsSeedPath(agentcaps);

            m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Sending new CAPS seed url {0} to client {1}", capsPath, agent.UUID);

            // Remove from physics just before crossing for minimal interruption
            Vector3 crossingVelocity = agent.Velocity;
            bool crossingFlying = agent.Flying;
            agent.RemoveFromPhysicalScene();

            // Preserve velocity for smoother crossing experience  
            Vector3 vel2 = crossingVelocity;
            if((agent.m_crossingFlags & 2) != 0)
            {
                // Maintain horizontal velocity, clear vertical for ground-based crossings
                vel2 = new Vector3(crossingVelocity.X, crossingVelocity.Y, 0);
            }

            if (m_eqModule != null)
            {
                m_eqModule.CrossRegion(
                    neighbourRegion.RegionHandle, pos, vel2,
                    endpoint, capsPath, agentUUID, agent.ControllingClient.SessionId,
                    neighbourRegion.RegionSizeX, neighbourRegion.RegionSizeY);
            }
            else
            {
                m_log.ErrorFormat("{0} Using old CrossRegion packet. Varregion will not work!!", LogHeader);
                agent.ControllingClient.CrossRegion(neighbourRegion.RegionHandle, pos, vel2,
                        endpoint,capsPath);
            }

            // SUCCESS!
            m_entityTransferStateMachine.UpdateInTransit(agentUUID, AgentTransferState.ReceivedAtDestination);

            // Unlike a teleport, here we do not wait for the destination region to confirm the receipt.
            m_entityTransferStateMachine.UpdateInTransit(agentUUID, AgentTransferState.CleaningUp);

            if(childRegionsToClose != null)
                agent.CloseChildAgents(childRegionsToClose);

            if((agent.m_crossingFlags & 8) == 0)
                agent.ClearControls(); // don't let attachments delete (called in HasMovedAway) disturb taken controls on viewers

            agent.HasMovedAway((agent.m_crossingFlags & 8) == 0);

            agent.MakeChildAgent(neighbourRegion.RegionHandle);

            // FIXME: Possibly this should occur lower down after other commands to close other agents,
            // but not sure yet what the side effects would be.
            m_entityTransferStateMachine.ResetFromTransit(agentUUID);

            return true;
        }

        private void CrossAgentToNewRegionCompleted(IAsyncResult iar)
        {
            CrossAgentToNewRegionDelegate icon = (CrossAgentToNewRegionDelegate)iar.AsyncState;
            ScenePresence agent = icon.EndInvoke(iar);

            //// If the cross was successful, this agent is a child agent
            //if (agent.IsChildAgent)
            //    agent.Reset();
            //else // Not successful
            //    agent.RestoreInCurrentScene();

            // In any case
            agent.IsInTransit = false;

//            m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Crossing agent {0} {1} completed.", agent.Firstname, agent.Lastname);
        }

        #endregion

        #region Enable Child Agent

        /// <summary>
        /// This informs a single neighbouring region about agent "avatar", and avatar about it
        /// Calls an asynchronous method to do so..  so it doesn't lag the sim.
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="region"></param>
        public void EnableChildAgent(ScenePresence sp, GridRegion region)
        {           
            int viewrange = (int)sp.RegionViewDistance;
            if(viewrange == 0)
                return;

            ICapabilitiesModule capsModule = m_scene.CapsModule;
            if (capsModule == null)
                return;

            Vector3 pos = sp.AbsolutePosition;

            int rtmp = region.RegionLocX - (int)m_sceneRegionInfo.WorldLocX - (int)pos.X;
            if ( rtmp > viewrange || rtmp < -(viewrange + region.RegionSizeX))
                return;
            rtmp = region.RegionLocY - (int)m_sceneRegionInfo.WorldLocY - (int)pos.Y;
            if (rtmp > viewrange || rtmp < -(viewrange + region.RegionSizeY))
                return;

            m_log.DebugFormat("[ENTITY TRANSFER]: Enabling child agent in new neighbour {0}", region.RegionName);

            ulong regionhandler = region.RegionHandle;

            Dictionary<ulong, string> seeds = new(capsModule.GetChildrenSeeds(sp.UUID));

            if (seeds.ContainsKey(regionhandler))
                seeds.Remove(regionhandler);

            if (!seeds.ContainsKey(m_sceneRegionHandler))
                seeds.Add(m_sceneRegionHandler, sp.ControllingClient.RequestClientInfo().CapsPath);

            AgentCircuitData currentAgentCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
            AgentCircuitData agent = sp.ControllingClient.RequestClientInfo();
            agent.BaseFolder = UUID.Zero;
            agent.InventoryFolder = UUID.Zero;
            agent.startpos = sp.AbsolutePosition + CalculateOffset(sp, region);
            agent.startfar = sp.DrawDistance;
            agent.child = true;
            agent.Appearance = new AvatarAppearance
            {
                AvatarHeight = sp.Appearance.AvatarHeight
            };

            agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();

            seeds.Add(regionhandler, agent.CapsPath);

            agent.ChildrenCapSeeds = null;

            capsModule.SetChildrenSeed(sp.UUID, seeds);
            sp.KnownRegions = seeds;
            sp.AddNeighbourRegionSizeInfo(region);

            if (currentAgentCircuit != null)
            {
                agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                agent.IPAddress = currentAgentCircuit.IPAddress;
                agent.Viewer = currentAgentCircuit.Viewer;
                agent.Channel = currentAgentCircuit.Channel;
                agent.Mac = currentAgentCircuit.Mac;
                agent.Id0 = currentAgentCircuit.Id0;
            }

            IPEndPoint external = region.ExternalEndPoint;
            if (external != null)
            {
                ScenePresence avatar = sp;
                GridRegion reg = region;
                WorkManager.RunInThreadPool(delegate
                {
                    InformClientOfNeighbourAsync(avatar, agent, reg, external, true);
                },"InformClientOfNeighbourAsync" + avatar.UUID.ToString());
            }
        }

        #endregion

        #region Enable Child Agents

        List<GridRegion> RegionsInView(Vector3 pos, RegionInfo curregion, List<GridRegion> fullneighbours, float viewrange)
        {
            if (fullneighbours.Count == 0 || viewrange == 0)
                return new List<GridRegion>();

            int itmp = (int)curregion.WorldLocX + (int)pos.X;
            int minX = itmp - (int)viewrange;
            int maxX = itmp + (int)viewrange;
            itmp = (int)curregion.WorldLocY + (int)pos.Y;
            int minY = itmp - (int)viewrange;
            int maxY = itmp + (int)viewrange;

            List<GridRegion> ret = new(fullneighbours.Count);
            foreach (GridRegion r in fullneighbours)
            {
                OpenSim.Framework.RegionFlags? regionFlags = r.RegionFlags;
                if (regionFlags != null)
                {
                    if ((regionFlags & OpenSim.Framework.RegionFlags.RegionOnline) == 0)
                        continue;
                }

                itmp = r.RegionLocX;
                if (maxX < itmp)
                    continue;
                if (minX > itmp + r.RegionSizeX)
                    continue;
                itmp = r.RegionLocY;
                if (maxY < itmp)
                    continue;
                if (minY > itmp + r.RegionSizeY)
                    continue;
                ret.Add(r);
            }
            return ret;
        }

        List<GridRegion> RegionsInSPView(ScenePresence sp)
        {
            int viewrange = (int)sp.RegionViewDistance;
            if (viewrange == 0)
                return new List<GridRegion>();

            List<GridRegion> fullneighbours = GetNeighbors(sp);
            if (fullneighbours.Count == 0)
                return new List<GridRegion>();

            Vector3 pos = sp.AbsolutePosition;
            int itmp = (int)m_sceneRegionInfo.WorldLocX + (int)pos.X;
            int minX = itmp - viewrange;
            int maxX = itmp + viewrange;
            itmp = (int)m_sceneRegionInfo.WorldLocY + (int)pos.Y;
            int minY = itmp - viewrange;
            int maxY = itmp + viewrange;
 
            List<GridRegion> ret = new(fullneighbours.Count);
            foreach (GridRegion r in fullneighbours)
            {
                OpenSim.Framework.RegionFlags? regionFlags = r.RegionFlags;
                if (regionFlags != null)
                {
                    if ((regionFlags & OpenSim.Framework.RegionFlags.RegionOnline) == 0)
                        continue;
                }

                itmp = r.RegionLocX;
                if (maxX < itmp)
                    continue;
                if (minX > itmp + r.RegionSizeX)
                    continue;
                itmp = r.RegionLocY;
                if (maxY < itmp)
                    continue;
                if (minY > itmp + r.RegionSizeY)
                    continue;
                ret.Add(r);
            }
            return ret;
        }

        /// <summary>
        /// This informs all neighbouring regions about agent "avatar".
        /// and as important informs the avatar about then
        /// </summary>
        /// <param name="sp"></param>
        public void EnableChildAgents(ScenePresence sp)
        {
            // assumes that out of view range regions are disconnected by the previous region
            ICapabilitiesModule capsModule = m_scene.CapsModule;
            if (capsModule == null)
                return;

            List<GridRegion> neighbours = RegionsInSPView(sp);

            LinkedList<ulong> previousRegionNeighbourHandles;
            Dictionary<ulong, string> seeds;

            seeds = new Dictionary<ulong, string>(capsModule.GetChildrenSeeds(sp.UUID));
            previousRegionNeighbourHandles = new LinkedList<ulong>(seeds.Keys);

            IClientAPI spClient = sp.ControllingClient;

            // This will fail if the user aborts login
            try
            {
                if (!seeds.ContainsKey(m_sceneRegionHandler))
                    seeds.Add(m_sceneRegionHandler, spClient.RequestClientInfo().CapsPath);
            }
            catch
            {
                return;
            }

            AgentCircuitData currentAgentCircuit =
                m_scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);

            List<AgentCircuitData> cagents = new();
            List<ulong> newneighbours = new();

            foreach (GridRegion neighbour in neighbours)
            {
                ulong handler = neighbour.RegionHandle;

                if (previousRegionNeighbourHandles.Contains(handler))
                {
                    // agent already knows this region
                    previousRegionNeighbourHandles.Remove(handler);
                    continue;
                }

                if (handler == m_sceneRegionHandler)
                    continue;

                // a new region to add
                AgentCircuitData agent = spClient.RequestClientInfo();
                agent.BaseFolder = UUID.Zero;
                agent.InventoryFolder = UUID.Zero;
                agent.startpos = sp.AbsolutePosition + CalculateOffset(sp, neighbour);
                agent.child = true;
                agent.Appearance = new AvatarAppearance { AvatarHeight = sp.Appearance.AvatarHeight };
                agent.startfar = sp.DrawDistance;
                if (currentAgentCircuit is not null)
                {
                    agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                    agent.IPAddress = currentAgentCircuit.IPAddress;
                    agent.Viewer = currentAgentCircuit.Viewer;
                    agent.Channel = currentAgentCircuit.Channel;
                    agent.Mac = currentAgentCircuit.Mac;
                    agent.Id0 = currentAgentCircuit.Id0;
                }

                newneighbours.Add(handler);
                agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();
                seeds.Add(handler, agent.CapsPath);

                agent.ChildrenCapSeeds = null;
                cagents.Add(agent);
            }

            List<ulong> toclose;
            // previousRegionNeighbourHandles now contains regions to forget
            if (previousRegionNeighbourHandles.Count > 0)
            {
                if (previousRegionNeighbourHandles.Contains(m_sceneRegionHandler))
                    previousRegionNeighbourHandles.Remove(m_sceneRegionHandler);

                foreach (ulong handler in previousRegionNeighbourHandles)
                    seeds.Remove(handler);

                toclose = new List<ulong>(previousRegionNeighbourHandles);
            }
            else
                toclose = new List<ulong>();
            /// Update all child agent with everyone's seeds
                //            foreach (AgentCircuitData a in cagents)
                //                a.ChildrenCapSeeds = new Dictionary<ulong, string>(seeds);

            capsModule?.SetChildrenSeed(sp.UUID, seeds);

            sp.KnownRegions = seeds;
            sp.SetNeighbourRegionSizeInfo(neighbours);

            if (neighbours.Count > 0 || toclose.Count > 0)
            {
                AgentPosition agentpos = new()
                {
                    AgentID = new UUID(sp.UUID.Guid),
                    SessionID = spClient.SessionId,
                    Size = sp.Appearance.AvatarSize,
                    Center = sp.CameraPosition,
                    Far = sp.DrawDistance,
                    Position = sp.AbsolutePosition,
                    Velocity = sp.Velocity,
                    RegionHandle = m_sceneRegionHandler,
                    //agentpos.GodLevel = sp.GodLevel;
                    GodData = sp.GodController.State(),
                    Throttles = spClient.GetThrottlesPacked(1)
                };
                //agentpos.ChildrenCapSeeds = seeds;

                Util.FireAndForget(delegate
                {
                    int count = 0;
                    IPEndPoint ipe;
 
                    if(toclose.Count > 0)
                        sp.CloseChildAgents(toclose);

                    foreach (GridRegion neighbour in neighbours)
                    {
                        ulong handler = neighbour.RegionHandle;
                        try
                        {
                            if (newneighbours.Contains(handler))
                            {
                                ipe = neighbour.ExternalEndPoint;
                                if (ipe != null)
                                    InformClientOfNeighbourAsync(sp, cagents[count], neighbour, ipe, true);
                                else
                                {
                                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]:  lost DNS resolution for neighbour {0}", neighbour.ExternalHostName);
                                }
                                count++;
                            }
                            else if (!previousRegionNeighbourHandles.Contains(handler))
                            {
                                m_scene.SimulationService.UpdateAgent(neighbour, agentpos);
                            }
                            if (sp.IsDeleted)
                                return;
                        }
                        catch (Exception e)
                        {
                            m_log.ErrorFormat(
                                "[ENTITY TRANSFER MODULE]: Error creating child agent at {0} ({1} ({2}, {3}).  {4}",
                                neighbour.ExternalHostName,
                                neighbour.RegionHandle,
                                neighbour.RegionLocX,
                                neighbour.RegionLocY,
                                e);
                        }
                    }
                });
            }
        }

        public void CheckChildAgents(ScenePresence sp)
        {
            List<GridRegion> neighbours = RegionsInSPView(sp);

            Dictionary<ulong, string> previousRegionNeighbour = sp.KnownRegions;
            previousRegionNeighbour.Remove(m_sceneRegionHandler);

            IClientAPI spClient = sp.ControllingClient;
            AgentCircuitData currentAgentCircuit = m_scene.AuthenticateHandler.GetAgentCircuitData(spClient.CircuitCode);

            List<AgentCircuitData> cagents = new(neighbours.Count);
            List<GridRegion> newneighbours = new(neighbours.Count);

            foreach (GridRegion neighbour in neighbours)
            {
                ulong handler = neighbour.RegionHandle;

                if (previousRegionNeighbour.Remove(handler))
                {
                    // agent already knows this region
                    continue;
                }

                if (handler == m_sceneRegionHandler)
                    continue;

                // a new region to add
                AgentCircuitData agent = spClient.RequestClientInfo();
                agent.BaseFolder = UUID.Zero;
                agent.InventoryFolder = UUID.Zero;
                agent.startpos = sp.AbsolutePosition + CalculateOffset(sp, neighbour);
                agent.child = true;
                agent.Appearance = new AvatarAppearance { AvatarHeight = sp.Appearance.AvatarHeight };
                agent.startfar = sp.DrawDistance;
                if (currentAgentCircuit is not null)
                {
                    agent.ServiceURLs = currentAgentCircuit.ServiceURLs;
                    agent.IPAddress = currentAgentCircuit.IPAddress;
                    agent.Viewer = currentAgentCircuit.Viewer;
                    agent.Channel = currentAgentCircuit.Channel;
                    agent.Mac = currentAgentCircuit.Mac;
                    agent.Id0 = currentAgentCircuit.Id0;
                }

                newneighbours.Add(neighbour);
                agent.CapsPath = CapsUtil.GetRandomCapsObjectPath();
                sp.AddNeighbourRegion(neighbour, agent.CapsPath);

                agent.ChildrenCapSeeds = null;
                cagents.Add(agent);
            }

            // previousRegionNeighbourHandles now contains regions to forget
            if (previousRegionNeighbour.Count > 0)
            {
                List<ulong> toclose = new(previousRegionNeighbour.Keys);
                sp.CloseChildAgents(toclose);
            }
 
            ICapabilitiesModule capsModule = m_scene.CapsModule;
            capsModule?.SetChildrenSeed(sp.UUID, sp.KnownRegions);

            if (newneighbours.Count > 0)
            {
                int count = 0;
                IPEndPoint ipe;

                foreach (GridRegion neighbour in newneighbours)
                {
                    try
                    {
                        ipe = neighbour.ExternalEndPoint;
                        if (ipe != null)
                            InformClientOfNeighbourAsync(sp, cagents[count], neighbour, ipe, true);
                        else
                        {
                            m_log.DebugFormat("[ENTITY TRANSFER MODULE]:  lost DNS resolution for neighbour {0}", neighbour.ExternalHostName);
                        }
                        count++;
                        if (sp.IsDeleted)
                            return;
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Error creating child agent at {0} ({1} ({2}, {3}).  {4}",
                            neighbour.ExternalHostName,
                            neighbour.RegionHandle,
                            neighbour.RegionLocX,
                            neighbour.RegionLocY,
                            e);
                    }
                }
            }
        }

        public void CloseOldChildAgents(ScenePresence sp)
        {
            Dictionary<ulong, string> seeds = sp.KnownRegions;
            if (seeds.Count == 0)
                return;

            seeds.Remove(m_sceneRegionHandler);
            if (seeds.Count == 0)
                return;

            List<GridRegion> neighbours = RegionsInSPView(sp);
            sp.SetNeighbourRegionSizeInfo(neighbours);
            foreach (GridRegion neighbour in neighbours)
                seeds.Remove(neighbour.RegionHandle);

            // seeds now contains regions to forget
            if (seeds.Count == 0)
                return;

            List<ulong> toclose = new(seeds.Keys);
            Util.FireAndForget(delegate
                {
                    sp.CloseChildAgents(toclose);
                });
        }

        // Computes the difference between two region bases.
        // Returns a vector of world coordinates (meters) from base of first region to the second.
        // The first region is the home region of the passed scene presence.
        Vector3 CalculateOffset(ScenePresence sp, GridRegion neighbour)
        {
              return new Vector3(sp.Scene.RegionInfo.WorldLocX - neighbour.RegionLocX,
                                sp.Scene.RegionInfo.WorldLocY - neighbour.RegionLocY,
                                0f);
        }
        #endregion

        #region NotFoundLocationCache class
        // A collection of not found locations to make future lookups 'not found' lookups quick.
        // A simple expiring cache that keeps not found locations for some number of seconds.
        // A 'not found' location is presumed to be anywhere in the minimum sized region that
        //    contains that point. A conservitive estimate.
        private class NotFoundLocationCache
        {
            private readonly Dictionary<ulong, DateTime> m_notFoundLocations = new();
            public NotFoundLocationCache()
            {
            }
            // just use normal regions handlers and sizes
            public void Add(double pX, double pY)
            {
                ulong psh = (ulong)pX & 0xffffff00ul;
                psh <<= 32;
                psh |= (ulong)pY & 0xffffff00ul;

                lock (m_notFoundLocations)
                    m_notFoundLocations[psh] = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            }
            // Test to see of this point is in any of the 'not found' areas.
            // Return 'true' if the point is found inside the 'not found' areas.
            public bool Contains(double pX, double pY)
            {
                ulong psh = (ulong)pX & 0xffffff00ul;
                psh <<= 32;
                psh |= (ulong)pY & 0xffffff00ul;

                lock (m_notFoundLocations)
                {
                    if(m_notFoundLocations.ContainsKey(psh))
                    {
                        if(m_notFoundLocations[psh] > DateTime.UtcNow)
                            return true;
                        m_notFoundLocations.Remove(psh);
                    }
                    return false;
                }
            }

            private void DoExpiration()
            {
                List<ulong> m_toRemove = new();
                DateTime now = DateTime.UtcNow;
                lock (m_notFoundLocations)
                {
                    foreach (KeyValuePair<ulong, DateTime> kvp in m_notFoundLocations)
                    {
                        if (kvp.Value < now)
                            m_toRemove.Add(kvp.Key);
                    }

                    if (m_toRemove.Count > 0)
                    {
                        foreach (ulong u in m_toRemove)
                            m_notFoundLocations.Remove(u);
                        m_toRemove.Clear();
                    }
                }
            }
        }

        #endregion // NotFoundLocationCache class
        #region getregions
        private readonly NotFoundLocationCache m_notFoundLocationCache = new();

        protected GridRegion GetRegionContainingWorldLocation(IGridService pGridService, UUID pScopeID, double px, double py)
        {
         // Given a world position, get the GridRegion info for
         //   the region containing that point.

            // check if we already found it does not exist
            if (m_notFoundLocationCache.Contains(px, py))
                return null;

            // reduce to next grid corner
            // this is all that is needed on 0.9 grids
            uint possibleX = (uint)px & 0xffffff00u;
            uint possibleY = (uint)py & 0xffffff00u;
            GridRegion ret = pGridService.GetRegionByPosition(pScopeID, (int)possibleX, (int)possibleY);
            if (ret != null)
                return ret;
 
            /* obsolete code
            // for 0.8 regions just make a BIG area request. old code whould do it plus 4 more smaller on region open edges
            // this is what 0.9 grids now do internally
            List<GridRegion> possibleRegions = pGridService.GetRegionRange(pScopeID,
                        (int)(px - Constants.MaximumRegionSize), (int)(px + 1), // +1 bc left mb not part of range
                        (int)(py - Constants.MaximumRegionSize), (int)(py + 1));
            if (possibleRegions != null && possibleRegions.Count > 0)
            {
                // If we found some regions, check to see if the point is within
                foreach (GridRegion gr in possibleRegions)
                {
                    if (px >= (double)gr.RegionLocX && px < (double)(gr.RegionLocX + gr.RegionSizeX)
                                && py >= (double)gr.RegionLocY && py < (double)(gr.RegionLocY + gr.RegionSizeY))
                    {
                        // Found a region that contains the point
                        return gr;
                    }
                }
            }
            */

            // remember this location was not found so we can quickly not find it next time
            m_notFoundLocationCache.Add(px, py);
            return null;
        }

        /// <summary>
        /// Async component for informing client of which neighbours exist
        /// </summary>
        /// <remarks>
        /// This needs to run asynchronously, as a network timeout may block the thread for a long while
        /// </remarks>
        /// <param name="remoteClient"></param>
        /// <param name="a"></param>
        /// <param name="regionHandle"></param>
        /// <param name="endPoint"></param>
        private void InformClientOfNeighbourAsync(ScenePresence sp, AgentCircuitData agentCircData, GridRegion reg,
                                                  IPEndPoint endPoint, bool newAgent)
        {
            if (newAgent)
            {
                // we may already had lost this sp
                if(sp == null || sp.IsDeleted || sp.ControllingClient == null) // something bad already happened
                   return;

                Scene scene = sp.Scene;

                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Informing {0} {1} about neighbour {2} {3} at ({4},{5})",
                    sp.Name, sp.UUID, reg.RegionName, endPoint, reg.RegionCoordX, reg.RegionCoordY);

                string capsPath = reg.ServerURI + CapsUtil.GetCapsSeedPath(agentCircData.CapsPath);

                bool regionAccepted = scene.SimulationService.CreateAgent(reg, reg, agentCircData, (uint)TeleportFlags.Default, null, out string reason);

                if (regionAccepted)
                {
                    // give  time for createAgent to finish, since it is async and does grid services access
                    Thread.Sleep(500);

                    if (m_eqModule != null)
                    {
                        if(sp == null || sp.IsDeleted || sp.ControllingClient == null) // something bad already happened
                            return;

                        m_log.DebugFormat("{0} {1} is sending {2} EnableSimulator for neighbour region {3}(loc=<{4},{5}>,siz=<{6},{7}>) " +
                            "and EstablishAgentCommunication with seed cap {8}", LogHeader,
                            scene.RegionInfo.RegionName, sp.Name,
                            reg.RegionName, reg.RegionLocX, reg.RegionLocY, reg.RegionSizeX, reg.RegionSizeY, capsPath);

                        m_eqModule.EnableSimulator(reg.RegionHandle, endPoint, sp.UUID, reg.RegionSizeX, reg.RegionSizeY);
                        m_eqModule.EstablishAgentCommunication(sp.UUID, endPoint, capsPath, reg.RegionHandle, reg.RegionSizeX, reg.RegionSizeY);
                    }
                    else
                    {
                        sp.ControllingClient.InformClientOfNeighbour(reg.RegionHandle, endPoint);
                        // TODO: make Event Queue disablable!
                    }

                    m_log.DebugFormat("[ENTITY TRANSFER MODULE]: Completed inform {0} {1} about neighbour {2}", sp.Name, sp.UUID, endPoint);
                }

                else
                {
                    sp.RemoveNeighbourRegion(reg.RegionHandle);
                    m_log.WarnFormat(
                        "[ENTITY TRANSFER MODULE]: Region {0} did not accept {1} {2}: {3}",
                        reg.RegionName, sp.Name, sp.UUID, reason);
                }
            }

        }

        // all this code should be moved to scene replacing the now bad one there
        // cache Neighbors
        List<GridRegion> Neighbors = null;
        DateTime LastNeighborsTime = DateTime.MinValue;

        /// <summary>
        /// Return the list of online regions that are considered to be neighbours to the given scene.
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="pRegionLocX"></param>
        /// <param name="pRegionLocY"></param>
        /// <returns></returns>
        protected List<GridRegion> GetNeighbors(ScenePresence avatar)
        {
            if (Neighbors != null && (DateTime.UtcNow - LastNeighborsTime).TotalSeconds < 30)
            {
                return Neighbors;
            }

            Scene pScene = avatar.Scene;
            uint dd = (uint)pScene.MaxRegionViewDistance;
            if(dd <= 1)
                return new List<GridRegion>();

            RegionInfo regionInfo = pScene.RegionInfo;
            uint startX = regionInfo.WorldLocX;
            uint endX = startX + regionInfo.RegionSizeX;
            uint startY = regionInfo.WorldLocY;
            uint endY = startY + regionInfo.RegionSizeY;

            --dd;
            startX -= dd;
            startY -= dd;
            endX += dd;
            endY += dd;

            List<GridRegion> neighbours = avatar.Scene.GridService.GetRegionRange(
                    regionInfo.ScopeID, (int)startX, (int)endX, (int)startY, (int)endY);

            // The r.RegionFlags == null check only needs to be made for simulators before 2015-01-14 (pre 0.8.1).
            neighbours.RemoveAll( r => r.RegionID.Equals(regionInfo.RegionID));
            Neighbors = neighbours;
            LastNeighborsTime = DateTime.UtcNow;
            return neighbours;
        }
        #endregion

        #region Agent Arrived

        public void AgentArrivedAtDestination(UUID id)
        {
            ScenePresence sp = m_scene.GetScenePresence(id);
            if(sp == null || sp.IsDeleted || !sp.IsInTransit)
                return;

            //Scene.CloseAgent(sp.UUID, false);
            sp.IsInTransit = false;
            //m_entityTransferStateMachine.SetAgentArrivedAtDestination(id);
        }

        #endregion

        #region Object Transfers

        public GridRegion GetObjectDestination(SceneObjectGroup grp, Vector3 targetPosition, out Vector3 newpos)
        {
            newpos = targetPosition;

            Scene scene = grp.Scene;
            if (scene == null)
                return null;

            int x = (int)targetPosition.X + (int)scene.RegionInfo.WorldLocX;
            if (targetPosition.X >= 0)
                x++;
            else
                x--;

            int y = (int)targetPosition.Y + (int)scene.RegionInfo.WorldLocY;
            if (targetPosition.Y >= 0)
                y++;
            else
                y--;

            GridRegion neighbourRegion = scene.GridService.GetRegionByPosition(scene.RegionInfo.ScopeID,x,y);
            if (neighbourRegion == null)
            {
                return null;
            }

            float newRegionSizeX = neighbourRegion.RegionSizeX;
            float newRegionSizeY = neighbourRegion.RegionSizeY;
            if (newRegionSizeX == 0)
                newRegionSizeX = Constants.RegionSize;
            if (newRegionSizeY == 0)
                newRegionSizeY = Constants.RegionSize;

            newpos.X = targetPosition.X - (neighbourRegion.RegionLocX - (int)scene.RegionInfo.WorldLocX);
            newpos.Y = targetPosition.Y - (neighbourRegion.RegionLocY - (int)scene.RegionInfo.WorldLocY);

            // [border-bounce #1a] Velocity-proportional entry offset (Halcyon dead-reckoning analogue).
            // A flat 0.2m placed a >2m vehicle with its root ON the seam; with a 0-margin exit trigger the
            // effective hysteresis was 0.2m and a near-stationary vehicle ping-ponged between regions (16
            // crossings/90s observed). Land a moving object |v|*0.25s inside instead (~2.6m at driving
            // speed) - clear of the re-cross band. Floored at the old 0.2m so slow/stationary objects
            // behave exactly as before; capped so a very fast object cannot skip metres of parcel checks.
            const float enterDistance = 0.2f;        // floor: legacy behaviour for slow objects
            const float enterLeadSeconds = 0.25f;    // seconds of travel converted into entry depth
            const float enterMaxDistance = 4.0f;     // cap: bounded even at extreme speeds
            Vector3 vel = grp.RootPart.Velocity;
            float enterX = Utils.Clamp(Math.Abs(vel.X) * enterLeadSeconds, enterDistance, enterMaxDistance);
            float enterY = Utils.Clamp(Math.Abs(vel.Y) * enterLeadSeconds, enterDistance, enterMaxDistance);
            newpos.X = Utils.Clamp(newpos.X, enterX, newRegionSizeX - enterX);
            newpos.Y = Utils.Clamp(newpos.Y, enterY, newRegionSizeY - enterY);

            return neighbourRegion;
        }

        /// <summary>
        /// Move the given scene object into a new region
        /// </summary>
        /// <param name="newRegionHandle"></param>
        /// <param name="grp">Scene Object Group that we're crossing</param>
        /// <returns>
        /// true if the crossing itself was successful, false on failure
        /// FIMXE: we still return true if the crossing object was not successfully deleted from the originating region
        /// </returns>
        public bool CrossPrimGroupIntoNewRegion(GridRegion destination, Vector3 newPosition, SceneObjectGroup grp, bool silent, bool removeScripts)
        {
            //m_log.Debug("  >>> CrossPrimGroupIntoNewRegion <<<");

            Culture.SetCurrentCulture();

            bool successYN = false;
            grp.RootPart.ClearUpdateSchedule();
            //int primcrossingXMLmethod = 0;

            if (destination != null)
            {
                if (m_scene.SimulationService != null)
                    successYN = m_scene.SimulationService.CreateObject(destination, newPosition, grp, true);

                if (successYN)
                {
                    // We remove the object here
                    try
                    {
                        grp.Scene.DeleteSceneObject(grp, silent, removeScripts);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: Exception deleting the old object left behind on a border crossing for {0}, {1}",
                            grp, e);
                    }
                }
            }
            else
            {
                m_log.Error("[ENTITY TRANSFER MODULE]: destination was unexpectedly null in Scene.CrossPrimGroupIntoNewRegion()");
            }

            return successYN;
        }

        #endregion

        #region Misc

        public bool IsInTransit(UUID id)
        {
            return m_entityTransferStateMachine.GetAgentTransferState(id) != null;
        }

        protected void ReInstantiateScripts(ScenePresence sp)
        {
            int i = 0;
            if (sp.InTransitScriptStates.Count > 0)
            {
                List<SceneObjectGroup> attachments = sp.GetAttachments();

                foreach (SceneObjectGroup sog in attachments)
                {
                    if (i < sp.InTransitScriptStates.Count)
                    {
                        sog.SetState(sp.InTransitScriptStates[i++], sp.Scene);
                        sog.CreateScriptInstances(0, false, sp.Scene.DefaultScriptEngine, -1);
                        sog.ResumeScripts();
                    }
                    else
                        m_log.ErrorFormat(
                            "[ENTITY TRANSFER MODULE]: InTransitScriptStates.Count={0} smaller than Attachments.Count={1}",
                            sp.InTransitScriptStates.Count, attachments.Count);
                }

                sp.InTransitScriptStates.Clear();
            }
        }
        #endregion

        public virtual bool HandleIncomingSceneObject(SceneObjectGroup so, Vector3 newPosition)
        {
            if (so.OwnerID.IsZero())
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Denied object {0}({1}) entry into {2} because ownerID is zero",
                        so.Name, so.UUID, m_sceneName);
                return false;
            }

            // If the user is banned, we won't let any of their objects
            // enter. Period.
            if (m_sceneRegionInfo.EstateSettings.IsBanned(so.OwnerID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Denied {0} {1} into {2} of banned owner {3}",
                        so.Name, so.UUID, m_sceneName, so.OwnerID);
                return false;
            }

            if(so.IsAttachmentCheckFull())
            {
                if(m_scene.GetScenePresence(so.OwnerID) == null)
                {
                    m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Denied attachment {0}({1}) owner {2} not in region {3}",
                        so.Name, so.UUID, so.OwnerID, m_sceneName);
                    return false;
                }
            }

            if (!newPosition.IsZero())
                so.RootPart.GroupPosition = newPosition;

            if (!m_scene.AddSceneObject(so))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Problem adding scene object {0} {1} into {2} ",
                    so.Name, so.UUID, m_sceneName);

                return false;
            }

            if (!so.IsAttachment)
            {
                // FIXME: It would be better to never add the scene object at all rather than add it and then delete
                // it
                if (!m_scene.Permissions.CanObjectEntry(so, true, so.AbsolutePosition))
                {
                    // Deny non attachments based on parcel settings
                    //
                    m_log.Info("[ENTITY TRANSFER MODULE]: Denied prim crossing because of parcel settings");

                    m_scene.DeleteSceneObject(so, false);

                    return false;
                }

                // For attachments, we need to wait until the agent is root
                // before we restart the scripts, or else some functions won't work.
                so.RootPart.ParentGroup.CreateScriptInstances(0, false, m_scene.DefaultScriptEngine, GetStateSource(so));

                so.ResumeScripts();

                // AddSceneObject already does this and doing it again messes
                //if (so.RootPart.KeyframeMotion != null)
                //    so.RootPart.KeyframeMotion.UpdateSceneObject(so);
            }

            return true;
        }

        public virtual bool HandleIncomingAttachments(ScenePresence sp, List<SceneObjectGroup> attachments)
        {
            if (sp.IsDeleted)
                return false;

            if (m_sceneRegionInfo.EstateSettings.IsBanned(sp.UUID))
            {
                m_log.DebugFormat(
                    "[ENTITY TRANSFER MODULE]: Denied Attachments for banned avatar {0}", sp.Name);
                return false;
            }

            foreach(SceneObjectGroup so in attachments)
            {
                if (!m_scene.AddSceneObject(so))
                {
                    m_log.DebugFormat(
                        "[ENTITY TRANSFER MODULE]: Problem adding attachment {0} {1} into {2} ",
                        so.Name, so.UUID, m_sceneName);
                    continue;
                }
            }

            sp.GotAttachmentsData = true;
            return true;
        }

        private int GetStateSource(SceneObjectGroup sog)
        {
            ScenePresence sp = m_scene.GetScenePresence(sog.OwnerID);

            if (sp != null)
                return sp.GetStateSource();

            return 2; // StateSource.PrimCrossing
        }
        
        #region Benchmarking Console Commands
        
        /// <summary>
        /// Console command to run comprehensive region crossing benchmarks
        /// </summary>
        private async void HandleRunCrossingBenchmark(string module, string[] cmdParams)
        {
            // Parse command parameters
            string benchmarkType = "quick";
            int avatars = 10;
            int crossings = 5;
            bool enableStress = false;
            
            if (cmdParams.Length >= 4)
                benchmarkType = cmdParams[3].ToLower();
            if (cmdParams.Length >= 5 && int.TryParse(cmdParams[4], out int a))
                avatars = Math.Clamp(a, 1, 100);
            if (cmdParams.Length >= 6 && int.TryParse(cmdParams[5], out int c))
                crossings = Math.Clamp(c, 1, 50);
            
            // Configure stress testing based on type
            if (benchmarkType == "stress")
                enableStress = true;
            
            try
            {
                lock (m_benchmarkLock)
                {
                    if (m_benchmarkSuite == null)
                    {
                        MainConsole.Instance.Output("[BENCHMARK ERROR]: Benchmarking suite not available");
                        return;
                    }
                    
                    // Configure benchmark parameters
                    var duration = benchmarkType switch
                    {
                        "quick" => TimeSpan.FromMinutes(2),
                        "full" => TimeSpan.FromMinutes(10),
                        "load" => TimeSpan.FromMinutes(5),
                        "stress" => TimeSpan.FromMinutes(15),
                        _ => TimeSpan.FromMinutes(2)
                    };
                    
                    m_benchmarkSuite.SetConfiguration(avatars, crossings, enableStress, duration);
                }
                
                MainConsole.Instance.Output($"==== Starting {benchmarkType.ToUpper()} Region Crossing Benchmark ====");
                MainConsole.Instance.Output($"Configuration: {avatars} virtual avatars, {crossings} crossings each");
                MainConsole.Instance.Output($"Estimated duration: {(benchmarkType == "quick" ? 2 : benchmarkType == "full" ? 10 : benchmarkType == "load" ? 5 : 15)} minutes");
                MainConsole.Instance.Output("");
                MainConsole.Instance.Output("Benchmark running... Use 'show benchmark results' to view progress.");
                
                // Run benchmark asynchronously to avoid blocking console
                Task.Run(async () =>
                {
                    try
                    {
                        var report = await m_benchmarkSuite.RunFullBenchmarkAsync();
                        
                        MainConsole.Instance.Output("");
                        MainConsole.Instance.Output("==== Benchmark Complete ====");
                        DisplayBenchmarkReport(report, benchmarkType);
                    }
                    catch (Exception ex)
                    {
                        MainConsole.Instance.Output($"[BENCHMARK ERROR]: {ex.Message}");
                        m_log.ErrorFormat("[CROSSING BENCHMARK]: Benchmark failed: {0}", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                MainConsole.Instance.Output($"[BENCHMARK ERROR]: Failed to start benchmark: {ex.Message}");
                m_log.ErrorFormat("[CROSSING BENCHMARK]: Failed to start: {0}", ex);
            }
        }
        
        /// <summary>
        /// Console command to show benchmark results
        /// </summary>
        private void HandleShowBenchmarkResults(string module, string[] cmdParams)
        {
            string displayType = "latest";
            if (cmdParams.Length >= 4)
                displayType = cmdParams[3].ToLower();
            
            try
            {
                MainConsole.Instance.Output("==== Benchmark Results ====");
                MainConsole.Instance.Output("");
                
                // Try to load latest results from file
                var resultsFile = Path.Combine(Environment.CurrentDirectory, "benchmark_results.json");
                if (File.Exists(resultsFile))
                {
                    var json = File.ReadAllText(resultsFile);
                    var report = JsonSerializer.Deserialize<BenchmarkSuiteReport>(json, new JsonSerializerOptions 
                    { 
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    
                    if (report != null)
                    {
                        DisplayBenchmarkReport(report, displayType);
                    }
                    else
                    {
                        MainConsole.Instance.Output("No valid benchmark results found.");
                    }
                }
                else
                {
                    MainConsole.Instance.Output("No benchmark results available yet.");
                    MainConsole.Instance.Output("Run 'run crossing benchmark' to generate performance data.");
                }
            }
            catch (Exception ex)
            {
                MainConsole.Instance.Output($"[BENCHMARK ERROR]: {ex.Message}");
                m_log.ErrorFormat("[CROSSING BENCHMARK]: Failed to load results: {0}", ex);
            }
        }
        
        /// <summary>
        /// Console command to configure benchmark parameters
        /// </summary>
        private void HandleBenchmarkConfig(string module, string[] cmdParams)
        {
            try
            {
                lock (m_benchmarkLock)
                {
                    if (m_benchmarkSuite == null)
                    {
                        MainConsole.Instance.Output("[BENCHMARK ERROR]: Benchmarking suite not available");
                        return;
                    }
                    
                    // Parse configuration parameters
                    int avatars = 10;
                    int crossings = 5;
                    bool enableStress = false;
                    int durationMinutes = 5;
                    
                    if (cmdParams.Length >= 3 && int.TryParse(cmdParams[2], out int a))
                        avatars = Math.Clamp(a, 1, 100);
                    if (cmdParams.Length >= 4 && int.TryParse(cmdParams[3], out int c))
                        crossings = Math.Clamp(c, 1, 50);
                    if (cmdParams.Length >= 5 && bool.TryParse(cmdParams[4], out bool s))
                        enableStress = s;
                    if (cmdParams.Length >= 6 && int.TryParse(cmdParams[5], out int d))
                        durationMinutes = Math.Clamp(d, 1, 30);
                    
                    // Apply configuration
                    m_benchmarkSuite.SetConfiguration(avatars, crossings, enableStress, TimeSpan.FromMinutes(durationMinutes));
                    
                    MainConsole.Instance.Output("==== Benchmark Configuration Updated ====");
                    MainConsole.Instance.Output($"Virtual Avatars: {avatars}");
                    MainConsole.Instance.Output($"Crossings per Avatar: {crossings}");
                    MainConsole.Instance.Output($"Stress Testing: {(enableStress ? "Enabled" : "Disabled")}");
                    MainConsole.Instance.Output($"Test Duration: {durationMinutes} minutes");
                    MainConsole.Instance.Output("");
                    MainConsole.Instance.Output("Configuration will be used for all subsequent benchmark runs.");
                }
            }
            catch (Exception ex)
            {
                MainConsole.Instance.Output($"[BENCHMARK ERROR]: {ex.Message}");
                m_log.ErrorFormat("[CROSSING BENCHMARK]: Configuration failed: {0}", ex);
            }
        }
        
        /// <summary>
        /// Display formatted benchmark report
        /// </summary>
        private void DisplayBenchmarkReport(BenchmarkSuiteReport report, string displayType)
        {
            if (report == null) return;
            
            MainConsole.Instance.Output($"Test Date: {report.TestStartTime:yyyy-MM-dd HH:mm:ss} UTC");
            MainConsole.Instance.Output($"Region: {report.RegionName} ({report.SceneName})");
            MainConsole.Instance.Output($"Duration: {report.TotalTestDuration.TotalMinutes:F1} minutes");
            
            if (report.HasErrors)
            {
                MainConsole.Instance.Output($"⚠️  Test completed with errors: {report.ErrorMessage}");
            }
            
            MainConsole.Instance.Output("");
            
            // Basic Performance Results
            if (report.BasicPerformance != null)
            {
                MainConsole.Instance.Output("--- BASIC PERFORMANCE ---");
                MainConsole.Instance.Output($"Average Crossing Time: {report.BasicPerformance.AverageCrossingTime:F1}ms");
                MainConsole.Instance.Output($"Min/Max Times: {report.BasicPerformance.MinCrossingTime:F1}ms / {report.BasicPerformance.MaxCrossingTime:F1}ms");
                MainConsole.Instance.Output($"Standard Deviation: {report.BasicPerformance.StandardDeviation:F1}ms");
                MainConsole.Instance.Output($"Success Rate: {report.BasicPerformance.SuccessRate:F1}%");
                MainConsole.Instance.Output($"Memory Impact: {report.BasicPerformance.MemoryImpactMB:F2} MB");
                MainConsole.Instance.Output($"GC Collections: Gen0={report.BasicPerformance.GCGen0Collections}, Gen1={report.BasicPerformance.GCGen1Collections}, Gen2={report.BasicPerformance.GCGen2Collections}");
                MainConsole.Instance.Output("");
            }
            
            // Load Test Results
            if (report.LoadTest != null && displayType != "summary")
            {
                MainConsole.Instance.Output("--- LOAD TEST RESULTS ---");
                MainConsole.Instance.Output($"Total Crossings: {report.LoadTest.TotalCrossings}");
                MainConsole.Instance.Output($"Crossings/Second: {report.LoadTest.CrossingsPerSecond:F1}");
                MainConsole.Instance.Output($"Average Time: {report.LoadTest.AverageCrossingTime:F1}ms");
                MainConsole.Instance.Output($"Percentiles: P50={report.LoadTest.P50CrossingTime:F1}ms, P95={report.LoadTest.P95CrossingTime:F1}ms, P99={report.LoadTest.P99CrossingTime:F1}ms");
                MainConsole.Instance.Output($"Memory per Crossing: {report.LoadTest.MemoryPerCrossingKB:F1} KB");
                MainConsole.Instance.Output("");
            }
            
            // Concurrency Results
            if (report.ConcurrencyTest != null && displayType == "full")
            {
                MainConsole.Instance.Output("--- CONCURRENCY ANALYSIS ---");
                MainConsole.Instance.Output($"Optimal Concurrency Level: {report.ConcurrencyTest.OptimalConcurrencyLevel}");
                
                if (report.ConcurrencyTest.ConcurrencyResults.Any())
                {
                    MainConsole.Instance.Output("Concurrency Performance:");
                    foreach (var result in report.ConcurrencyTest.ConcurrencyResults.Take(5))
                    {
                        MainConsole.Instance.Output($"  {result.ConcurrencyLevel} concurrent: {result.AverageCrossingTime:F1}ms avg, {result.EfficiencyRatio:F1}% efficiency");
                    }
                }
                MainConsole.Instance.Output("");
            }
            
            // Memory Test Results
            if (report.MemoryTest != null && displayType != "summary")
            {
                MainConsole.Instance.Output("--- MEMORY ANALYSIS ---");
                MainConsole.Instance.Output($"Memory Growth: {report.MemoryTest.MemoryGrowthMB:F2} MB");
                MainConsole.Instance.Output($"Peak Memory: {report.MemoryTest.PeakMemoryMB:F1} MB");
                MainConsole.Instance.Output($"Average per Crossing: {report.MemoryTest.AverageMemoryPerCrossingKB:F1} KB");
                
                if (report.MemoryTest.PotentialMemoryLeakMB > 1.0)
                {
                    MainConsole.Instance.Output($"⚠️  Potential Memory Leak: {report.MemoryTest.PotentialMemoryLeakMB:F2} MB retained after GC");
                }
                else
                {
                    MainConsole.Instance.Output("✓ No significant memory leaks detected");
                }
                MainConsole.Instance.Output("");
            }
            
            // Stress Test Results
            if (report.StressTest != null && displayType == "full")
            {
                MainConsole.Instance.Output("--- STRESS TEST RESULTS ---");
                MainConsole.Instance.Output($"Maximum Concurrent Crossings: {report.StressTest.MaxConcurrentCrossings}");
                
                if (report.StressTest.SystemLimitReached)
                {
                    MainConsole.Instance.Output("⚠️  System performance limit reached during testing");
                }
                else
                {
                    MainConsole.Instance.Output("✓ System handled all stress test loads successfully");
                }
                MainConsole.Instance.Output("");
            }
            
            // Regression Analysis
            if (report.RegressionAnalysis != null)
            {
                MainConsole.Instance.Output("--- REGRESSION ANALYSIS ---");
                
                if (report.RegressionAnalysis.HasBaseline)
                {
                    MainConsole.Instance.Output($"Baseline Date: {report.RegressionAnalysis.BaselineDate:yyyy-MM-dd}");
                    
                    if (report.RegressionAnalysis.IsRegression)
                    {
                        MainConsole.Instance.Output($"⚠️  PERFORMANCE REGRESSION: {report.RegressionAnalysis.PerformanceChange:F1}% slower");
                    }
                    else if (report.RegressionAnalysis.IsImprovement)
                    {
                        MainConsole.Instance.Output($"✓ PERFORMANCE IMPROVEMENT: {Math.Abs(report.RegressionAnalysis.PerformanceChange):F1}% faster");
                    }
                    else
                    {
                        MainConsole.Instance.Output($"✓ Performance within acceptable range ({report.RegressionAnalysis.PerformanceChange:F1}% change)");
                    }
                    
                    MainConsole.Instance.Output($"Analysis: {report.RegressionAnalysis.Message}");
                }
                else
                {
                    MainConsole.Instance.Output("No baseline available - this run will establish the performance baseline");
                }
                MainConsole.Instance.Output("");
            }
            
            MainConsole.Instance.Output("Results saved to benchmark_results.json for CI/CD integration");
            MainConsole.Instance.Output("");
            MainConsole.Instance.Output("Use 'run crossing benchmark' to perform new tests");
            MainConsole.Instance.Output("Use 'benchmark config' to adjust test parameters");
        }
        
        #endregion
    }
}
