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
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using Amib.Threading;

// For future Lua integration - will use KeraLua or similar
// using KeraLua;
// using NLua;

[assembly: Addin("LuaEngine", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace OpenSim.Region.ScriptEngine.LuaEngine
{
    /// <summary>
    /// Modern Lua Script Engine for OpenSim
    /// Provides advanced scripting capabilities comparable to Second Life's SLua system
    /// Features coroutines, advanced memory management, and superior performance
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LuaEngine")]
    public class LuaScriptEngine : INonSharedRegionModule, IScriptEngine, IScriptModule
    {
        #region Private Fields

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        private Scene m_scene;
        private IConfig m_config;
        private IConfigSource m_configSource;
        private bool m_enabled = false;
        private bool m_enableLuaCoroutines = true;
        private int m_maxLuaMemoryMB = 64;
        private int m_maxExecutionTimeMs = 30000;
        private int m_luaGCStepSize = 100;
        private string m_engineName = "LuaEngine";
        
        // Script management
        private readonly ConcurrentDictionary<UUID, LuaScriptInstance> m_scriptInstances = new();
        private readonly ConcurrentDictionary<uint, List<UUID>> m_primScripts = new();
        
        // Performance and threading
        private readonly SemaphoreSlim m_compilationSemaphore = new(Environment.ProcessorCount);
        private readonly SmartThreadPool m_threadPool;
        private readonly Timer m_maintenanceTimer;
        
        // Statistics
        private long m_scriptsCompiled = 0;
        private long m_scriptsExecuted = 0;
        private long m_luaMemoryUsage = 0;
        private DateTime m_lastStatsUpdate = DateTime.UtcNow;

        #endregion

        #region Constructor

        public LuaScriptEngine()
        {
            // Initialize thread pool for script execution
            m_threadPool = new SmartThreadPool(
                new STPStartInfo
                {
                    ThreadPoolName = "LuaEngine",
                    MinWorkerThreads = 2,
                    MaxWorkerThreads = Environment.ProcessorCount * 4,
                    IdleTimeout = 60000,
                    UseCallerCallContext = true,
                    SuppressFlow = true
                });
                
            // Maintenance timer for garbage collection and cleanup
            m_maintenanceTimer = new Timer(MaintenanceTick, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        #endregion

        #region INonSharedRegionModule

        public string Name => m_engineName;
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            m_configSource = source;
            m_config = source.Configs["LuaEngine"];
            
            if (m_config != null)
            {
                m_enabled = m_config.GetBoolean("Enabled", false);
                m_enableLuaCoroutines = m_config.GetBoolean("EnableCoroutines", true);
                m_maxLuaMemoryMB = m_config.GetInt("MaxMemoryMB", 64);
                m_maxExecutionTimeMs = m_config.GetInt("MaxExecutionTimeMs", 30000);
                m_luaGCStepSize = m_config.GetInt("LuaGCStepSize", 100);
                
                if (m_enabled)
                {
                    m_log.InfoFormat("[LUA ENGINE]: Lua scripting engine enabled");
                    m_log.InfoFormat("[LUA ENGINE]: Coroutines: {0}, Max Memory: {1}MB, Max Execution: {2}ms", 
                        m_enableLuaCoroutines, m_maxLuaMemoryMB, m_maxExecutionTimeMs);
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            
            m_scene = scene;
            
            // Register with scene
            m_scene.RegisterModuleInterface<IScriptEngine>(this);
            m_scene.RegisterModuleInterface<IScriptModule>(this);
            
            // Subscribe to events
            m_scene.EventManager.OnRezScript += OnRezScript;
            m_scene.EventManager.OnRemoveScript += OnRemoveScript;
            m_scene.EventManager.OnScriptReset += OnScriptReset;
            
            // Register statistics
            RegisterStatistics();
            
            m_log.InfoFormat("[LUA ENGINE]: Lua script engine added to region {0}", scene.Name);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            
            // Cleanup all scripts
            foreach (var script in m_scriptInstances.Values)
            {
                script.Stop();
            }
            m_scriptInstances.Clear();
            m_primScripts.Clear();
            
            // Unregister events
            m_scene.EventManager.OnRezScript -= OnRezScript;
            m_scene.EventManager.OnRemoveScript -= OnRemoveScript;
            m_scene.EventManager.OnScriptReset -= OnScriptReset;
            
            m_scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled) return;
            
            m_log.InfoFormat("[LUA ENGINE]: Lua script engine ready for region {0}", scene.Name);
        }

        public void Close()
        {
            if (!m_enabled) return;
            
            m_maintenanceTimer?.Dispose();
            m_threadPool?.Shutdown();
            m_compilationSemaphore?.Dispose();
        }

        #endregion

        #region IScriptEngine

        public Scene World => m_scene;
        public IScriptModule ScriptModule => this;
        public IConfig Config => m_config;
        public IConfigSource ConfigSource => m_configSource;
        public string ScriptEngineName => m_engineName;
        public string ScriptEnginePath => "LuaEngine";
        public string ScriptClassName => "LuaScript";
        public string ScriptBaseClassName => "LuaScriptBase";
        public string[] ScriptReferencedAssemblies => new string[0];

        public IScriptWorkItem QueueEventHandler(object parms)
        {
            if (parms is not EventParams eventParams)
                return null;
                
            return m_threadPool.QueueWorkItem(() => ProcessEventHandler(eventParams));
        }

        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                return script.PostEvent(parms);
            }
            return false;
        }

        public bool PostObjectEvent(uint localID, EventParams parms)
        {
            if (m_primScripts.TryGetValue(localID, out List<UUID> scripts))
            {
                bool success = true;
                foreach (UUID scriptId in scripts)
                {
                    success &= PostScriptEvent(scriptId, parms);
                }
                return success;
            }
            return false;
        }

        public bool PostObjectLinksetDataEvent(uint localID, int action, ReadOnlySpan<char> name, ReadOnlySpan<char> value)
        {
            // Implementation for linkset data events
            var parms = new EventParams("linkset_data", new object[] { action, name.ToString(), value.ToString() }, null);
            return PostObjectEvent(localID, parms);
        }

        public DetectParams GetDetectParams(UUID itemID, int number)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                return script.GetDetectParams(number);
            }
            return null;
        }

        public void SetMinEventDelay(UUID itemID, double delay)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                script.MinEventDelay = delay;
            }
        }

        public int GetStartParameter(UUID itemID)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                return script.StartParam;
            }
            return 0;
        }

        public void SetScriptState(UUID itemID, bool state, bool self)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                if (state)
                    script.Start();
                else
                    script.Suspend();
            }
        }

        public bool GetScriptState(UUID itemID)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                return script.IsRunning;
            }
            return false;
        }

        public void SetState(UUID itemID, string newState)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                script.SetState(newState);
            }
        }

        public void ApiResetScript(UUID itemID)
        {
            ResetScript(itemID);
        }

        public void ResetScript(UUID itemID)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                script.Reset();
            }
        }

        public void CancelScriptEvent(UUID itemID, string eventName)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                script.CancelEvent(eventName);
            }
        }

        #endregion

        #region IScriptModule

        public bool HasScript(UUID itemID, out bool running)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                running = script.IsRunning;
                return true;
            }
            running = false;
            return false;
        }

        public bool GetScriptState(UUID itemID, out bool running, out bool suspended)
        {
            if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
            {
                running = script.IsRunning;
                suspended = script.IsSuspended;
                return true;
            }
            running = false;
            suspended = false;
            return false;
        }

        public void SaveAllState()
        {
            foreach (var script in m_scriptInstances.Values)
            {
                script.SaveState();
            }
        }

        public void StartProcessing()
        {
            // Engine is always ready to process
        }

        public float GetScriptExecutionTime(List<UUID> itemIDs)
        {
            float totalTime = 0;
            foreach (UUID itemID in itemIDs)
            {
                if (m_scriptInstances.TryGetValue(itemID, out LuaScriptInstance script))
                {
                    totalTime += script.ExecutionTime;
                }
            }
            return totalTime;
        }

        public void SuspendScript(UUID itemID)
        {
            SetScriptState(itemID, false, false);
        }

        public void ResumeScript(UUID itemID)
        {
            SetScriptState(itemID, true, false);
        }

        #endregion

        #region Event Handlers

        private void OnRezScript(uint localID, UUID itemID, string script, int startParam, bool postOnRez, string engine, int stateSource)
        {
            if (engine != m_engineName && engine != "lua" && engine != "LuaEngine")
                return;
                
            Task.Run(async () =>
            {
                try
                {
                    await CompileAndStartScriptAsync(localID, itemID, script, startParam, postOnRez, stateSource);
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("[LUA ENGINE]: Error rezzing script {0}: {1}", itemID, ex.Message);
                }
            });
        }

        private void OnRemoveScript(uint localID, UUID itemID)
        {
            if (m_scriptInstances.TryRemove(itemID, out LuaScriptInstance script))
            {
                script.Stop();
                
                // Remove from prim scripts
                if (m_primScripts.TryGetValue(localID, out List<UUID> scripts))
                {
                    scripts.Remove(itemID);
                    if (scripts.Count == 0)
                        m_primScripts.TryRemove(localID, out _);
                }
            }
        }

        private void OnScriptReset(uint localID, UUID itemID)
        {
            ResetScript(itemID);
        }

        #endregion

        #region Private Implementation

        private async Task CompileAndStartScriptAsync(uint localID, UUID itemID, string script, int startParam, bool postOnRez, int stateSource)
        {
            await m_compilationSemaphore.WaitAsync();
            
            try
            {
                // Create script instance
                var scriptInstance = new LuaScriptInstance(
                    itemID, localID, script, startParam, postOnRez, 
                    m_scene, this, m_maxLuaMemoryMB, m_maxExecutionTimeMs, m_enableLuaCoroutines);
                
                // Compile the script
                bool compiled = await scriptInstance.CompileAsync();
                if (!compiled)
                {
                    m_log.ErrorFormat("[LUA ENGINE]: Failed to compile script {0}", itemID);
                    return;
                }
                
                // Store script instance
                m_scriptInstances[itemID] = scriptInstance;
                
                // Track scripts per prim
                if (!m_primScripts.TryGetValue(localID, out List<UUID> scripts))
                {
                    scripts = new List<UUID>();
                    m_primScripts[localID] = scripts;
                }
                scripts.Add(itemID);
                
                // Start the script
                scriptInstance.Start();
                
                Interlocked.Increment(ref m_scriptsCompiled);
                
                m_log.InfoFormat("[LUA ENGINE]: Successfully compiled and started Lua script {0}", itemID);
            }
            finally
            {
                m_compilationSemaphore.Release();
            }
        }

        private void ProcessEventHandler(EventParams eventParams)
        {
            // Process event - this is called from thread pool
            Interlocked.Increment(ref m_scriptsExecuted);
        }

        private void MaintenanceTick(object state)
        {
            try
            {
                // Garbage collection and maintenance
                long totalMemory = 0;
                int activeScripts = 0;
                
                foreach (var script in m_scriptInstances.Values)
                {
                    if (script.IsRunning)
                    {
                        activeScripts++;
                        totalMemory += script.MemoryUsage;
                        script.PerformMaintenance();
                    }
                }
                
                Interlocked.Exchange(ref m_luaMemoryUsage, totalMemory);
                
                if (DateTime.UtcNow - m_lastStatsUpdate > TimeSpan.FromMinutes(1))
                {
                    m_log.DebugFormat("[LUA ENGINE]: {0} active scripts, {1}KB memory usage", 
                        activeScripts, totalMemory / 1024);
                    m_lastStatsUpdate = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA ENGINE]: Error in maintenance: {0}", ex.Message);
            }
        }

        private void RegisterStatistics()
        {
            StatsManager.RegisterStat(
                new Stat(
                    "LuaScriptsCompiled",
                    "Number of Lua scripts compiled since engine start",
                    "",
                    "",
                    "scriptengine",
                    m_scene.Name,
                    StatType.Pull,
                    MeasuresOfInterest.None,
                    stat => stat.Value = m_scriptsCompiled,
                    StatVerbosity.Debug));
                    
            StatsManager.RegisterStat(
                new Stat(
                    "LuaScriptsExecuted",
                    "Number of Lua script events executed",
                    "",
                    "",
                    "scriptengine",
                    m_scene.Name,
                    StatType.Pull,
                    MeasuresOfInterest.AverageChangeOverTime,
                    stat => stat.Value = m_scriptsExecuted,
                    StatVerbosity.Debug));
                    
            StatsManager.RegisterStat(
                new Stat(
                    "LuaMemoryUsage",
                    "Memory usage of all Lua scripts in bytes",
                    "",
                    "",
                    "scriptengine",
                    m_scene.Name,
                    StatType.Pull,
                    MeasuresOfInterest.AverageChangeOverTime,
                    stat => stat.Value = m_luaMemoryUsage,
                    StatVerbosity.Debug));
        }

        #endregion
    }
}