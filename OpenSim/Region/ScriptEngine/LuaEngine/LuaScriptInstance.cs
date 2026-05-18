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
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;

// Future Lua integration - placeholder for actual Lua implementation
// using KeraLua;
// using NLua;

namespace OpenSim.Region.ScriptEngine.LuaEngine
{
    /// <summary>
    /// Individual Lua script instance with advanced execution capabilities
    /// Supports coroutines, state preservation, and memory management
    /// </summary>
    public class LuaScriptInstance : IDisposable
    {
        #region Private Fields

        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        
        // Script identification
        private readonly UUID m_itemID;
        private readonly uint m_localID;
        private readonly string m_sourceCode;
        private readonly Scene m_scene;
        private readonly LuaScriptEngine m_engine;
        
        // Lua runtime (placeholder for actual implementation)
        // private Lua m_luaState;
        // private LuaFunction m_eventHandler;
        private object m_luaState; // Placeholder
        
        // Script state
        private ScriptInstanceState m_state = ScriptInstanceState.Stopped;
        private string m_currentState = "default";
        private int m_startParam;
        private bool m_postOnRez;
        private double m_minEventDelay = 0.0;
        
        // Event handling
        private readonly ConcurrentQueue<EventParams> m_eventQueue = new();
        private readonly SemaphoreSlim m_eventSemaphore = new(0);
        private readonly CancellationTokenSource m_cancellationTokenSource = new();
        private Task m_eventProcessingTask;
        
        // Performance tracking
        private long m_executionTime = 0;
        private long m_memoryUsage = 0;
        private DateTime m_lastEventTime = DateTime.MinValue;
        private int m_eventsProcessed = 0;
        
        // Configuration
        private readonly int m_maxMemoryMB;
        private readonly int m_maxExecutionTimeMs;
        private readonly bool m_enableCoroutines;
        
        // Detect parameters for collision/touch events
        private DetectParams[] m_detectParams = null;
        
        // State preservation data
        private Dictionary<string, object> m_savedVariables = new();
        private List<string> m_savedCoroutines = new();

        #endregion

        #region Properties

        public UUID ItemID => m_itemID;
        public uint LocalID => m_localID;
        public bool IsRunning => m_state == ScriptInstanceState.Running;
        public bool IsSuspended => m_state == ScriptInstanceState.Suspended;
        public int StartParam => m_startParam;
        public double MinEventDelay { get; set; } = 0.0;
        public float ExecutionTime => m_executionTime / 1000.0f; // Convert to seconds
        public long MemoryUsage => m_memoryUsage;
        public string CurrentState => m_currentState;

        #endregion

        #region Constructor

        public LuaScriptInstance(UUID itemID, uint localID, string sourceCode, int startParam, bool postOnRez,
            Scene scene, LuaScriptEngine engine, int maxMemoryMB, int maxExecutionTimeMs, bool enableCoroutines)
        {
            m_itemID = itemID;
            m_localID = localID;
            m_sourceCode = sourceCode;
            m_startParam = startParam;
            m_postOnRez = postOnRez;
            m_scene = scene;
            m_engine = engine;
            m_maxMemoryMB = maxMemoryMB;
            m_maxExecutionTimeMs = maxExecutionTimeMs;
            m_enableCoroutines = enableCoroutines;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Compile the Lua script
        /// </summary>
        public async Task<bool> CompileAsync()
        {
            try
            {
                m_state = ScriptInstanceState.Compiling;
                
                // Initialize Lua state (placeholder implementation)
                await InitializeLuaStateAsync();
                
                // Compile the script
                bool success = await CompileLuaScriptAsync();
                
                if (success)
                {
                    m_state = ScriptInstanceState.Compiled;
                    m_log.InfoFormat("[LUA SCRIPT]: Successfully compiled script {0}", m_itemID);
                }
                else
                {
                    m_state = ScriptInstanceState.CompileError;
                    m_log.ErrorFormat("[LUA SCRIPT]: Failed to compile script {0}", m_itemID);
                }
                
                return success;
            }
            catch (Exception ex)
            {
                m_state = ScriptInstanceState.CompileError;
                m_log.ErrorFormat("[LUA SCRIPT]: Exception compiling script {0}: {1}", m_itemID, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Start script execution
        /// </summary>
        public void Start()
        {
            if (m_state != ScriptInstanceState.Compiled && m_state != ScriptInstanceState.Suspended)
                return;
                
            m_state = ScriptInstanceState.Running;
            
            // Start event processing task
            m_eventProcessingTask = Task.Run(ProcessEventsAsync, m_cancellationTokenSource.Token);
            
            // Fire state_entry event if this is initial start
            if (m_postOnRez)
            {
                PostEvent(new EventParams("state_entry", new object[0], null));
            }
            
            m_log.InfoFormat("[LUA SCRIPT]: Started script {0}", m_itemID);
        }

        /// <summary>
        /// Suspend script execution
        /// </summary>
        public void Suspend()
        {
            if (m_state == ScriptInstanceState.Running)
            {
                m_state = ScriptInstanceState.Suspended;
                m_log.InfoFormat("[LUA SCRIPT]: Suspended script {0}", m_itemID);
            }
        }

        /// <summary>
        /// Stop script execution
        /// </summary>
        public void Stop()
        {
            m_state = ScriptInstanceState.Stopped;
            m_cancellationTokenSource.Cancel();
            
            try
            {
                m_eventProcessingTask?.Wait(5000);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[LUA SCRIPT]: Error stopping script {0}: {1}", m_itemID, ex.Message);
            }
            
            m_log.InfoFormat("[LUA SCRIPT]: Stopped script {0}", m_itemID);
        }

        /// <summary>
        /// Reset script to initial state
        /// </summary>
        public void Reset()
        {
            Stop();
            
            // Clear state
            m_currentState = "default";
            m_eventsProcessed = 0;
            m_executionTime = 0;
            
            // Clear event queue
            while (m_eventQueue.TryDequeue(out _)) { }
            
            // Reinitialize and restart
            Task.Run(async () =>
            {
                await InitializeLuaStateAsync();
                await CompileLuaScriptAsync();
                Start();
            });
            
            m_log.InfoFormat("[LUA SCRIPT]: Reset script {0}", m_itemID);
        }

        /// <summary>
        /// Post an event to the script
        /// </summary>
        public bool PostEvent(EventParams eventParams)
        {
            if (m_state != ScriptInstanceState.Running)
                return false;
                
            // Check event delay
            if (m_minEventDelay > 0)
            {
                var timeSinceLastEvent = DateTime.UtcNow - m_lastEventTime;
                if (timeSinceLastEvent.TotalSeconds < m_minEventDelay)
                    return false;
            }
            
            m_eventQueue.Enqueue(eventParams);
            m_eventSemaphore.Release();
            m_lastEventTime = DateTime.UtcNow;
            
            return true;
        }

        /// <summary>
        /// Cancel specific event type
        /// </summary>
        public void CancelEvent(string eventName)
        {
            // Implementation would remove events of specific type from queue
            m_log.DebugFormat("[LUA SCRIPT]: Cancelled events of type {0} for script {1}", eventName, m_itemID);
        }

        /// <summary>
        /// Get detect parameters for collision/touch events
        /// </summary>
        public DetectParams GetDetectParams(int number)
        {
            if (m_detectParams != null && number >= 0 && number < m_detectParams.Length)
                return m_detectParams[number];
            return null;
        }

        /// <summary>
        /// Set script state
        /// </summary>
        public void SetState(string newState)
        {
            if (m_currentState != newState)
            {
                var oldState = m_currentState;
                m_currentState = newState;
                
                // Fire state_exit and state_entry events
                PostEvent(new EventParams("state_exit", new object[0], null));
                PostEvent(new EventParams("state_entry", new object[0], null));
                
                m_log.InfoFormat("[LUA SCRIPT]: Script {0} changed state from {1} to {2}", m_itemID, oldState, newState);
            }
        }

        /// <summary>
        /// Save script state for persistence
        /// </summary>
        public void SaveState()
        {
            try
            {
                // Save Lua variables and coroutines
                m_savedVariables = ExtractLuaVariables();
                m_savedCoroutines = ExtractLuaCoroutines();
                
                m_log.DebugFormat("[LUA SCRIPT]: Saved state for script {0}", m_itemID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Error saving state for script {0}: {1}", m_itemID, ex.Message);
            }
        }

        /// <summary>
        /// Restore script state from persistence
        /// </summary>
        public void RestoreState(Dictionary<string, object> variables, List<string> coroutines)
        {
            try
            {
                m_savedVariables = variables ?? new Dictionary<string, object>();
                m_savedCoroutines = coroutines ?? new List<string>();
                
                // Restore to Lua state
                RestoreLuaVariables(m_savedVariables);
                RestoreLuaCoroutines(m_savedCoroutines);
                
                m_log.InfoFormat("[LUA SCRIPT]: Restored state for script {0}", m_itemID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Error restoring state for script {0}: {1}", m_itemID, ex.Message);
            }
        }

        /// <summary>
        /// Perform maintenance operations
        /// </summary>
        public void PerformMaintenance()
        {
            try
            {
                // Update memory usage
                UpdateMemoryUsage();
                
                // Perform Lua garbage collection if needed
                if (m_memoryUsage > (m_maxMemoryMB * 1024 * 1024 * 0.8)) // 80% threshold
                {
                    PerformLuaGarbageCollection();
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Error in maintenance for script {0}: {1}", m_itemID, ex.Message);
            }
        }

        #endregion

        #region Private Implementation

        private async Task InitializeLuaStateAsync()
        {
            try
            {
                // TODO: Initialize actual Lua state
                // m_luaState = new Lua();
                // m_luaState.LoadCLRPackage();
                
                // Placeholder implementation
                m_luaState = new object();
                
                // Set memory limits
                // m_luaState.SetMemoryLimit(m_maxMemoryMB * 1024 * 1024);
                
                // Register OpenSim API functions
                await RegisterOpenSimAPIAsync();
                
                m_log.DebugFormat("[LUA SCRIPT]: Initialized Lua state for script {0}", m_itemID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Error initializing Lua state for script {0}: {1}", m_itemID, ex.Message);
                throw;
            }
        }

        private async Task<bool> CompileLuaScriptAsync()
        {
            try
            {
                // TODO: Compile actual Lua script
                // var result = m_luaState.LoadString(m_sourceCode);
                // if (result != LuaStatus.OK)
                // {
                //     var error = m_luaState.ToString(-1);
                //     m_log.ErrorFormat("[LUA SCRIPT]: Compilation error: {0}", error);
                //     return false;
                // }
                
                // Placeholder - always succeed for now
                await Task.Delay(100); // Simulate compilation time
                
                return true;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Exception during compilation: {0}", ex.Message);
                return false;
            }
        }

        private async Task RegisterOpenSimAPIAsync()
        {
            try
            {
                // TODO: Register LSL-compatible functions
                // m_luaState["llSay"] = new Action<int, string>(LuaLSL_llSay);
                // m_luaState["llOwnerSay"] = new Action<string>(LuaLSL_llOwnerSay);
                // m_luaState["llSetText"] = new Action<string, Vector3, double>(LuaLSL_llSetText);
                // ... etc for all LSL functions
                
                await Task.CompletedTask;
                
                m_log.DebugFormat("[LUA SCRIPT]: Registered OpenSim API for script {0}", m_itemID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Error registering API for script {0}: {1}", m_itemID, ex.Message);
                throw;
            }
        }

        private async Task ProcessEventsAsync()
        {
            try
            {
                while (!m_cancellationTokenSource.Token.IsCancellationRequested && m_state == ScriptInstanceState.Running)
                {
                    // Wait for events
                    await m_eventSemaphore.WaitAsync(m_cancellationTokenSource.Token);
                    
                    if (m_eventQueue.TryDequeue(out EventParams eventParams))
                    {
                        await ProcessSingleEventAsync(eventParams);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Error in event processing for script {0}: {1}", m_itemID, ex.Message);
            }
        }

        private async Task ProcessSingleEventAsync(EventParams eventParams)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                
                // Store detect parameters
                m_detectParams = eventParams.detectParams;
                
                // TODO: Execute Lua event handler
                // var eventFunction = m_luaState[$"on_{eventParams.eventName}"];
                // if (eventFunction != null)
                // {
                //     var result = eventFunction.Call(eventParams.param);
                // }
                
                // Placeholder implementation
                await Task.Delay(1); // Simulate script execution
                
                var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                Interlocked.Add(ref m_executionTime, (long)executionTime);
                Interlocked.Increment(ref m_eventsProcessed);
                
                // Check execution time limits
                if (executionTime > m_maxExecutionTimeMs)
                {
                    m_log.WarnFormat("[LUA SCRIPT]: Script {0} exceeded execution time limit: {1}ms", 
                        m_itemID, executionTime);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Error processing event {0} for script {1}: {2}", 
                    eventParams.eventName, m_itemID, ex.Message);
            }
        }

        private Dictionary<string, object> ExtractLuaVariables()
        {
            // TODO: Extract variables from Lua state
            // Implementation would iterate through global variables and extract their values
            return new Dictionary<string, object>();
        }

        private List<string> ExtractLuaCoroutines()
        {
            // TODO: Extract coroutine states from Lua
            // Implementation would serialize active coroutines
            return new List<string>();
        }

        private void RestoreLuaVariables(Dictionary<string, object> variables)
        {
            // TODO: Restore variables to Lua state
            // Implementation would set global variables in Lua
        }

        private void RestoreLuaCoroutines(List<string> coroutines)
        {
            // TODO: Restore coroutines to Lua state
            // Implementation would deserialize and restore coroutines
        }

        private void UpdateMemoryUsage()
        {
            // TODO: Get actual memory usage from Lua state
            // m_memoryUsage = m_luaState.GetMemoryUsage();
            
            // Placeholder
            m_memoryUsage = 1024 * 1024; // 1MB placeholder
        }

        private void PerformLuaGarbageCollection()
        {
            try
            {
                // TODO: Perform Lua garbage collection
                // m_luaState.GC(LuaGCType.Collect, 0);
                
                m_log.DebugFormat("[LUA SCRIPT]: Performed garbage collection for script {0}", m_itemID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA SCRIPT]: Error during garbage collection for script {0}: {1}", m_itemID, ex.Message);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Stop();
            
            m_cancellationTokenSource?.Dispose();
            m_eventSemaphore?.Dispose();
            
            // TODO: Dispose Lua state
            // m_luaState?.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Script instance state enumeration
    /// </summary>
    public enum ScriptInstanceState
    {
        Stopped,
        Compiling,
        Compiled,
        Running,
        Suspended,
        CompileError,
        RuntimeError
    }
}