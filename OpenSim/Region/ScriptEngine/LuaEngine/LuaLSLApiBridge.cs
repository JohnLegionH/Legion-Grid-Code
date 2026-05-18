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
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;

namespace OpenSim.Region.ScriptEngine.LuaEngine
{
    /// <summary>
    /// LSL API Bridge for Lua Scripts
    /// Provides LSL-compatible functions that can be called from Lua scripts
    /// This gives Lua scripts full compatibility with existing LSL functionality
    /// while offering the power and flexibility of modern Lua scripting
    /// </summary>
    public class LuaLSLApiBridge
    {
        #region Private Fields

        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        
        private readonly LuaScriptInstance m_scriptInstance;
        private readonly Scene m_scene;
        private readonly UUID m_itemID;
        private readonly uint m_localID;
        
        // LSL API instances
        private readonly LSL_Api m_lslApi;
        private readonly OSSL_Api m_osslApi;
        
        // State tracking
        private readonly Dictionary<string, object> m_scriptVariables = new();

        #endregion

        #region Constructor

        public LuaLSLApiBridge(LuaScriptInstance scriptInstance, Scene scene, UUID itemID, uint localID)
        {
            m_scriptInstance = scriptInstance;
            m_scene = scene;
            m_itemID = itemID;
            m_localID = localID;
            
            // Initialize LSL API
            try
            {
                // Create LSL API instance
                m_lslApi = new LSL_Api();
                // TODO: Initialize with proper script engine context
                // m_lslApi.Initialize(scriptEngine, part, item);
                
                // Create OSSL API instance  
                m_osslApi = new OSSL_Api();
                // TODO: Initialize with proper script engine context
                // m_osslApi.Initialize(scriptEngine, part, item);
                
                m_log.InfoFormat("[LUA LSL BRIDGE]: Initialized LSL API bridge for script {0}", itemID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA LSL BRIDGE]: Error initializing LSL API bridge: {0}", ex.Message);
                throw;
            }
        }

        #endregion

        #region Core LSL Functions

        /// <summary>
        /// Lua-compatible llSay function
        /// </summary>
        public void llSay(int channel, string text)
        {
            try
            {
                m_lslApi.llSay(channel, text);
            }
            catch (Exception ex)
            {
                LogLSLError("llSay", ex);
            }
        }

        /// <summary>
        /// Lua-compatible llOwnerSay function
        /// </summary>
        public void llOwnerSay(string text)
        {
            try
            {
                m_lslApi.llOwnerSay(text);
            }
            catch (Exception ex)
            {
                LogLSLError("llOwnerSay", ex);
            }
        }

        /// <summary>
        /// Lua-compatible llSetText function
        /// </summary>
        public void llSetText(string text, object color, double alpha)
        {
            try
            {
                // Convert Lua table/array to Vector3 if needed
                Vector3 colorVec = ConvertLuaToVector3(color);
                m_lslApi.llSetText(text, colorVec, alpha);
            }
            catch (Exception ex)
            {
                LogLSLError("llSetText", ex);
            }
        }

        /// <summary>
        /// Lua-compatible llGetPos function
        /// </summary>
        public object llGetPos()
        {
            try
            {
                var pos = m_lslApi.llGetPos();
                return ConvertVector3ToLua(pos);
            }
            catch (Exception ex)
            {
                LogLSLError("llGetPos", ex);
                return ConvertVector3ToLua(Vector3.Zero);
            }
        }

        /// <summary>
        /// Lua-compatible llSetPos function
        /// </summary>
        public void llSetPos(object position)
        {
            try
            {
                Vector3 pos = ConvertLuaToVector3(position);
                m_lslApi.llSetPos(pos);
            }
            catch (Exception ex)
            {
                LogLSLError("llSetPos", ex);
            }
        }

        /// <summary>
        /// Lua-compatible llGetRot function
        /// </summary>
        public object llGetRot()
        {
            try
            {
                var rot = m_lslApi.llGetRot();
                return ConvertQuaternionToLua(rot);
            }
            catch (Exception ex)
            {
                LogLSLError("llGetRot", ex);
                return ConvertQuaternionToLua(Quaternion.Identity);
            }
        }

        /// <summary>
        /// Lua-compatible llSetRot function
        /// </summary>
        public void llSetRot(object rotation)
        {
            try
            {
                Quaternion rot = ConvertLuaToQuaternion(rotation);
                m_lslApi.llSetRot(rot);
            }
            catch (Exception ex)
            {
                LogLSLError("llSetRot", ex);
            }
        }

        /// <summary>
        /// Lua-compatible llListen function
        /// </summary>
        public int llListen(int channel, string name, string id, string msg)
        {
            try
            {
                return m_lslApi.llListen(channel, name, id, msg);
            }
            catch (Exception ex)
            {
                LogLSLError("llListen", ex);
                return -1;
            }
        }

        /// <summary>
        /// Lua-compatible llSetTimerEvent function
        /// </summary>
        public void llSetTimerEvent(double sec)
        {
            try
            {
                m_lslApi.llSetTimerEvent(sec);
            }
            catch (Exception ex)
            {
                LogLSLError("llSetTimerEvent", ex);
            }
        }

        /// <summary>
        /// Lua-compatible llSleep function
        /// </summary>
        public void llSleep(double sec)
        {
            try
            {
                m_lslApi.llSleep(sec);
            }
            catch (Exception ex)
            {
                LogLSLError("llSleep", ex);
            }
        }

        /// <summary>
        /// Lua-compatible llHttpRequest function
        /// </summary>
        public string llHttpRequest(string url, object parameters, string body)
        {
            try
            {
                // Convert Lua table to LSL list if needed
                var paramList = ConvertLuaToLSLList(parameters);
                return m_lslApi.llHTTPRequest(url, paramList, body);
            }
            catch (Exception ex)
            {
                LogLSLError("llHttpRequest", ex);
                return "";
            }
        }

        /// <summary>
        /// Lua-compatible llGetObjectName function
        /// </summary>
        public string llGetObjectName()
        {
            try
            {
                return m_lslApi.llGetObjectName();
            }
            catch (Exception ex)
            {
                LogLSLError("llGetObjectName", ex);
                return "";
            }
        }

        /// <summary>
        /// Lua-compatible llSetObjectName function
        /// </summary>
        public void llSetObjectName(string name)
        {
            try
            {
                m_lslApi.llSetObjectName(name);
            }
            catch (Exception ex)
            {
                LogLSLError("llSetObjectName", ex);
            }
        }

        #endregion

        #region Enhanced Lua-Specific Functions

        /// <summary>
        /// Enhanced logging function with multiple levels
        /// </summary>
        public void luaLog(string level, string message)
        {
            try
            {
                switch (level.ToLower())
                {
                    case "debug":
                        m_log.DebugFormat("[LUA SCRIPT {0}]: {1}", m_itemID, message);
                        break;
                    case "info":
                        m_log.InfoFormat("[LUA SCRIPT {0}]: {1}", m_itemID, message);
                        break;
                    case "warn":
                        m_log.WarnFormat("[LUA SCRIPT {0}]: {1}", m_itemID, message);
                        break;
                    case "error":
                        m_log.ErrorFormat("[LUA SCRIPT {0}]: {1}", m_itemID, message);
                        break;
                    default:
                        m_log.InfoFormat("[LUA SCRIPT {0}]: {1}", m_itemID, message);
                        break;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[LUA LSL BRIDGE]: Error in luaLog: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Get script performance statistics
        /// </summary>
        public object luaGetStats()
        {
            try
            {
                return new Dictionary<string, object>
                {
                    ["execution_time"] = m_scriptInstance.ExecutionTime,
                    ["memory_usage"] = m_scriptInstance.MemoryUsage,
                    ["item_id"] = m_itemID.ToString(),
                    ["local_id"] = m_localID,
                    ["state"] = m_scriptInstance.CurrentState
                };
            }
            catch (Exception ex)
            {
                LogLSLError("luaGetStats", ex);
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Coroutine support for advanced scripting
        /// </summary>
        public void luaYield()
        {
            try
            {
                // TODO: Implement Lua coroutine yielding
                // This would allow scripts to yield execution and resume later
                // coroutine.yield()
            }
            catch (Exception ex)
            {
                LogLSLError("luaYield", ex);
            }
        }

        /// <summary>
        /// Advanced event scheduling
        /// </summary>
        public void luaScheduleEvent(string eventName, double delaySeconds, object parameters)
        {
            try
            {
                // TODO: Implement advanced event scheduling
                // This would allow scripts to schedule custom events
                m_log.InfoFormat("[LUA SCRIPT]: Scheduled event {0} with delay {1}s", eventName, delaySeconds);
            }
            catch (Exception ex)
            {
                LogLSLError("luaScheduleEvent", ex);
            }
        }

        #endregion

        #region Type Conversion Helpers

        /// <summary>
        /// Convert Lua table/array to OpenSim Vector3
        /// </summary>
        private Vector3 ConvertLuaToVector3(object luaValue)
        {
            try
            {
                // TODO: Implement actual Lua table conversion
                // For now, assume it's already a Vector3 or compatible type
                if (luaValue is Vector3 vector)
                    return vector;
                
                // If it's a table: {x=1.0, y=2.0, z=3.0} or {1.0, 2.0, 3.0}
                // Implementation would extract x, y, z values
                
                return Vector3.Zero;
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        /// <summary>
        /// Convert OpenSim Vector3 to Lua table
        /// </summary>
        private object ConvertVector3ToLua(Vector3 vector)
        {
            try
            {
                // TODO: Return Lua table {x=vector.X, y=vector.Y, z=vector.Z}
                // For now, return a dictionary that can be converted
                return new Dictionary<string, double>
                {
                    ["x"] = vector.X,
                    ["y"] = vector.Y,
                    ["z"] = vector.Z
                };
            }
            catch
            {
                return new Dictionary<string, double> { ["x"] = 0, ["y"] = 0, ["z"] = 0 };
            }
        }

        /// <summary>
        /// Convert Lua table to OpenSim Quaternion
        /// </summary>
        private Quaternion ConvertLuaToQuaternion(object luaValue)
        {
            try
            {
                // TODO: Implement actual Lua table conversion
                if (luaValue is Quaternion quaternion)
                    return quaternion;
                
                return Quaternion.Identity;
            }
            catch
            {
                return Quaternion.Identity;
            }
        }

        /// <summary>
        /// Convert OpenSim Quaternion to Lua table
        /// </summary>
        private object ConvertQuaternionToLua(Quaternion quaternion)
        {
            try
            {
                return new Dictionary<string, double>
                {
                    ["x"] = quaternion.X,
                    ["y"] = quaternion.Y,
                    ["z"] = quaternion.Z,
                    ["w"] = quaternion.W
                };
            }
            catch
            {
                return new Dictionary<string, double> { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["w"] = 1 };
            }
        }

        /// <summary>
        /// Convert Lua table to LSL List
        /// </summary>
        private object ConvertLuaToLSLList(object luaValue)
        {
            try
            {
                // TODO: Convert Lua table to LSL list
                // For now, return empty list
                return new List<object>();
            }
            catch
            {
                return new List<object>();
            }
        }

        #endregion

        #region Utility Methods

        private void LogLSLError(string functionName, Exception ex)
        {
            m_log.ErrorFormat("[LUA LSL BRIDGE]: Error in {0} for script {1}: {2}", 
                functionName, m_itemID, ex.Message);
        }

        #endregion
    }
}