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
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Module wrapper for the destruction physics system that properly integrates 
    /// with OpenSim's module dependency system
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "DestructionSystemModule")]
    public class DestructionSystemModule : IDestructionSystemModule, ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION MODULE]";
        
        private readonly Dictionary<Scene, DestructiblePhysicsSystem> m_systems = new Dictionary<Scene, DestructiblePhysicsSystem>();
        private readonly object m_systemsLock = new object();
        
        private bool m_enabled = false;
        private IConfigSource m_configSource;
        private DestructionSystemConfig m_config;
        
        // Module dependencies - track physics scenes
        private readonly Dictionary<Scene, BSScene> m_physicsScenes = new Dictionary<Scene, BSScene>();
        
        #region ISharedRegionModule Implementation
        
        public string Name => "DestructionSystemModule";
        
        public Type ReplaceableInterface => typeof(IDestructionSystemModule);
        
        public void Initialise(IConfigSource source)
        {
            m_configSource = source;
            
            try
            {
                // Load configuration to check if enabled
                m_config = DestructionSystemConfig.LoadConfig(source);
                m_enabled = m_config.Enabled;
                
                if (m_enabled)
                {
                    m_log.InfoFormat("{0}: Destruction system module enabled", LogHeader);
                }
                else
                {
                    m_log.InfoFormat("{0}: Destruction system module disabled by configuration", LogHeader);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to initialize destruction system module: {1}", LogHeader, ex.Message);
                m_enabled = false;
            }
        }
        
        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;
                
            try
            {
                lock (m_systemsLock)
                {
                    if (m_systems.ContainsKey(scene))
                    {
                        m_log.WarnFormat("{0}: Destruction system already added to scene {1}", 
                            LogHeader, scene.RegionInfo.RegionName);
                        return;
                    }
                    
                    // Wait for physics module to be available
                    // This ensures proper initialization order
                    scene.RegisterModuleInterface<IDestructionSystemModule>(this);
                    
                    m_log.InfoFormat("{0}: Added destruction system module to scene {1}", 
                        LogHeader, scene.RegionInfo.RegionName);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to add destruction system to scene {1}: {2}", 
                    LogHeader, scene.RegionInfo.RegionName, ex.Message);
            }
        }
        
        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;
                
            try
            {
                lock (m_systemsLock)
                {
                    if (m_systems.TryGetValue(scene, out var system))
                    {
                        system?.Dispose();
                        m_systems.Remove(scene);
                    }
                    
                    if (m_physicsScenes.ContainsKey(scene))
                    {
                        m_physicsScenes.Remove(scene);
                    }
                    
                    scene.UnregisterModuleInterface<IDestructionSystemModule>(this);
                    
                    m_log.InfoFormat("{0}: Removed destruction system from scene {1}", 
                        LogHeader, scene.RegionInfo.RegionName);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to remove destruction system from scene {1}: {2}", 
                    LogHeader, scene.RegionInfo.RegionName, ex.Message);
            }
        }
        
        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;
                
            try
            {
                // Now that all modules are loaded, we can safely initialize the destruction system
                // This ensures all dependencies (physics, scripts, etc.) are available
                
                // Check if physics scene is BulletSim - our only dependency
                if (!(scene.PhysicsScene is BSScene bsScene))
                {
                    m_log.InfoFormat("{0}: Scene {1} is not using BulletSim physics, destruction system disabled", 
                        LogHeader, scene.RegionInfo.RegionName);
                    return;
                }
                
                lock (m_systemsLock)
                {
                    if (!m_systems.ContainsKey(scene))
                    {
                        // Initialize the destruction physics system now that dependencies are resolved
                        var destructionSystem = new DestructiblePhysicsSystem(bsScene);
                        m_systems[scene] = destructionSystem;
                        m_physicsScenes[scene] = bsScene;
                        
                        // Subscribe to destruction events for module interface
                        destructionSystem.OnObjectDestroyed += (objectId, impactPoint, material, destroyer, cause) =>
                            OnObjectDestroyed?.Invoke(objectId, impactPoint, material, destroyer, cause);
                        
                        m_log.InfoFormat("{0}: Destruction physics system initialized for scene {1}", 
                            LogHeader, scene.RegionInfo.RegionName);
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to initialize destruction system for scene {1}: {2}", 
                    LogHeader, scene.RegionInfo.RegionName, ex.Message);
            }
        }
        
        public void PostInitialise()
        {
            if (m_enabled)
            {
                m_log.InfoFormat("{0}: Destruction system module post-initialization complete", LogHeader);
            }
        }
        
        public void Close()
        {
            lock (m_systemsLock)
            {
                foreach (var system in m_systems.Values)
                {
                    system?.Dispose();
                }
                m_systems.Clear();
                m_physicsScenes.Clear();
            }
            
            m_log.InfoFormat("{0}: Destruction system module closed", LogHeader);
        }
        
        #endregion
        
        #region IDestructionSystemModule Implementation
        
        public bool IsEnabled => m_enabled;
        
        public string Version => "2.0.0";
        
        public event Action<uint, Vector3, DestructibleMaterial, UUID, DestructionCause> OnObjectDestroyed;
        public event Action<uint, List<DestructionFragment>> OnFragmentsCreated;
        
        public bool RegisterDestructibleObject(uint objectID, DestructibleMaterial material, float threshold)
        {
            try
            {
                var system = GetDestructionSystemForObject(objectID);
                if (system == null)
                    return false;
                    
                // Get the SceneObjectPart for the objectID
                var scene = GetSceneForObject(objectID);
                if (scene == null)
                    return false;
                    
                var part = scene.GetSceneObjectPart(objectID);
                if (part == null)
                    return false;
                    
                return system.RegisterLSLDestructibleObject(part);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to register destructible object {1}: {2}", LogHeader, objectID, ex.Message);
                return false;
            }
        }
        
        public bool UnregisterDestructibleObject(uint objectID)
        {
            try
            {
                var system = GetDestructionSystemForObject(objectID);
                if (system == null)
                    return false;
                    
                system.RemoveDestructibleObject(objectID);
                return true;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to unregister destructible object {1}: {2}", LogHeader, objectID, ex.Message);
                return false;
            }
        }
        
        public bool TriggerDestruction(uint objectID, Vector3 impactPoint, Vector3 force, DestructionCause cause)
        {
            try
            {
                var system = GetDestructionSystemForObject(objectID);
                if (system == null)
                    return false;
                    
                return system.TriggerManualDestruction(objectID, impactPoint, force);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to trigger destruction for object {1}: {2}", LogHeader, objectID, ex.Message);
                return false;
            }
        }
        
        public Dictionary<string, object> GetStatistics()
        {
            var allStats = new Dictionary<string, object>();
            
            lock (m_systemsLock)
            {
                foreach (var kvp in m_systems)
                {
                    var scene = kvp.Key;
                    var system = kvp.Value;
                    
                    if (system?.StatsCollector != null)
                    {
                        var sceneStats = system.StatsCollector.GetAllStatistics();
                        foreach (var stat in sceneStats)
                        {
                            allStats[$"{scene.RegionInfo.RegionName}.{stat.Key}"] = stat.Value;
                        }
                    }
                }
            }
            
            return allStats;
        }
        
        public List<uint> GetDestructibleObjects()
        {
            var allObjects = new List<uint>();
            
            lock (m_systemsLock)
            {
                foreach (var system in m_systems.Values)
                {
                    allObjects.AddRange(system.GetDestructibleObjectIds());
                }
            }
            
            return allObjects;
        }
        
        public bool IsObjectDestructible(uint objectID)
        {
            var system = GetDestructionSystemForObject(objectID);
            return system?.IsObjectDestructible(objectID) ?? false;
        }
        
        public string GetPerformanceReport()
        {
            var reports = new List<string>();
            
            lock (m_systemsLock)
            {
                foreach (var kvp in m_systems)
                {
                    var scene = kvp.Key;
                    var system = kvp.Value;
                    
                    if (system?.StatsCollector != null)
                    {
                        reports.Add($"=== {scene.RegionInfo.RegionName} ===");
                        reports.Add(system.StatsCollector.GetFormattedReport());
                    }
                }
            }
            
            return string.Join("\\n\\n", reports);
        }
        
        #endregion
        
        #region Helper Methods
        
        private DestructiblePhysicsSystem GetDestructionSystemForObject(uint objectID)
        {
            lock (m_systemsLock)
            {
                foreach (var kvp in m_systems)
                {
                    var scene = kvp.Key;
                    var system = kvp.Value;
                    
                    // Check if object exists in this scene
                    var part = scene.GetSceneObjectPart(objectID);
                    if (part != null)
                    {
                        return system;
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Get destruction system for a specific scene
        /// </summary>
        public DestructiblePhysicsSystem GetDestructionSystem(Scene scene)
        {
            lock (m_systemsLock)
            {
                return m_systems.TryGetValue(scene, out var system) ? system : null;
            }
        }
        
        /// <summary>
        /// Get the scene containing a specific object
        /// </summary>
        private Scene GetSceneForObject(uint objectID)
        {
            lock (m_systemsLock)
            {
                foreach (var scene in m_systems.Keys)
                {
                    var part = scene.GetSceneObjectPart(objectID);
                    if (part != null)
                    {
                        return scene;
                    }
                }
            }
            
            return null;
        }
        
        #endregion
    }
}