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
 *       derived from this software without specific written permission.
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
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Modern Bullet Physics Engine Implementation
    /// Provides enhanced physics capabilities with multithreading, object pooling, and modern features
    /// while maintaining backward compatibility with existing BulletSim
    /// </summary>
    public class ModernBulletPhysics : IModernPhysicsEngine
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MODERN BULLET PHYSICS]";

        #region Private Fields

        private BSScene m_scene;
        private BSAPITemplate m_legacyAPI;
        private PhysicsConfig m_config;
        private bool m_initialized;
        private bool m_disposed;
        private bool m_paused;

        // Threading infrastructure
        private readonly SemaphoreSlim m_simulationSemaphore;
        private readonly CancellationTokenSource m_shutdownTokenSource;
        private Task m_simulationTask;

        // Object management
        private readonly ConcurrentDictionary<uint, IPhysicsBody> m_rigidBodies;
        private readonly ConcurrentDictionary<uint, ICharacterController> m_characterControllers;
        private readonly ConcurrentDictionary<uint, IVehicleController> m_vehicleControllers;

        // Object pooling
        private readonly ObjectPool<ModernRigidBody> m_rigidBodyPool;
        private readonly ObjectPool<ModernCharacterController> m_characterPool;
        private readonly ObjectPool<ModernVehicleController> m_vehiclePool;

        // Advanced features
        private ModernCollisionWorld m_collisionWorld;
        private ModernSpatialIndex m_spatialIndex;
        private ModernConstraintSolver m_constraintSolver;
        private IDebugDrawer m_debugDrawer;

        // Performance monitoring
        private PhysicsStatistics m_statistics;
        private readonly Stopwatch m_performanceTimer;
        private DateTime m_lastStatsUpdate;
        private bool m_profilingEnabled;

        #endregion

        #region Events

        public event Action<uint, uint, OMV.Vector3> OnCollision;
        public event Action<uint, uint> OnSeparation;
        public event Action<PhysicsStatistics> OnPerformanceUpdate;

        #endregion

        #region Constructor

        public ModernBulletPhysics(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_legacyAPI = scene.PE;
            
            // Initialize collections
            m_rigidBodies = new ConcurrentDictionary<uint, IPhysicsBody>();
            m_characterControllers = new ConcurrentDictionary<uint, ICharacterController>();
            m_vehicleControllers = new ConcurrentDictionary<uint, IVehicleController>();

            // Initialize threading
            m_simulationSemaphore = new SemaphoreSlim(1, 1);
            m_shutdownTokenSource = new CancellationTokenSource();

            // Initialize object pools
            m_rigidBodyPool = new ObjectPool<ModernRigidBody>(() => new ModernRigidBody());
            m_characterPool = new ObjectPool<ModernCharacterController>(() => new ModernCharacterController());
            m_vehiclePool = new ObjectPool<ModernVehicleController>(() => new ModernVehicleController());

            // Initialize performance monitoring
            m_performanceTimer = new Stopwatch();
            m_statistics = new PhysicsStatistics();
            m_lastStatsUpdate = DateTime.UtcNow;

            m_log.InfoFormat("{0}: Modern Bullet Physics engine created", LogHeader);
        }

        #endregion

        #region IModernPhysicsEngine Implementation

        public string EngineName => "Modern BulletSim";
        public string EngineVersion => "1.0.0 (Bullet " + (m_legacyAPI?.BulletEngineVersion ?? "Unknown") + ")";
        public bool IsInitialized => m_initialized && !m_disposed;
        public bool IsPaused => m_paused;

        public async Task InitializeAsync(PhysicsConfig config)
        {
            if (m_disposed)
                throw new ObjectDisposedException(nameof(ModernBulletPhysics));

            if (m_initialized)
            {
                m_log.WarnFormat("{0}: Already initialized", LogHeader);
                return;
            }

            try
            {
                m_config = config ?? new PhysicsConfig();
                
                m_log.InfoFormat("{0}: Initializing with config - Multithreading: {1}, Object Pooling: {2}", 
                    LogHeader, m_config.EnableMultithreading, m_config.EnableObjectPooling);

                // Initialize advanced features
                if (m_config.EnableSpatialOptimization)
                {
                    m_spatialIndex = new ModernSpatialIndex();
                    m_log.InfoFormat("{0}: Spatial optimization enabled", LogHeader);
                }

                m_collisionWorld = new ModernCollisionWorld(m_legacyAPI, m_scene);
                m_constraintSolver = new ModernConstraintSolver(m_legacyAPI);

                // Initialize multithreading if enabled
                if (m_config.EnableMultithreading)
                {
                    await InitializeMultithreadingAsync();
                }

                m_initialized = true;
                m_profilingEnabled = m_config.EnablePerformanceMonitoring;

                m_log.InfoFormat("{0}: Modern Bullet Physics initialized successfully", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to initialize: {1}", LogHeader, ex.Message);
                throw;
            }
        }

        public async Task ShutdownAsync()
        {
            if (m_disposed)
                return;

            try
            {
                m_log.InfoFormat("{0}: Shutting down Modern Bullet Physics", LogHeader);

                // Signal shutdown
                m_shutdownTokenSource.Cancel();

                // Wait for simulation task to complete with timeout
                if (m_simulationTask != null && !m_simulationTask.IsCompleted)
                {
                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await m_simulationTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        m_log.WarnFormat("{0}: Simulation task shutdown timeout - forcing termination", LogHeader);
                    }
                    catch (OperationCanceledException)
                    {
                        m_log.InfoFormat("{0}: Simulation task shutdown cancelled", LogHeader);
                    }
                }

                // Cleanup objects
                await CleanupObjectsAsync();

                m_initialized = false;
                m_log.InfoFormat("{0}: Shutdown complete", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during shutdown: {1}", LogHeader, ex.Message);
            }
        }

        public async Task<bool> StepAsync(float deltaTime)
        {
            if (!IsInitialized || m_paused)
                return false;

            if (!m_config.EnableMultithreading)
                return Step(deltaTime);

            try
            {
                await m_simulationSemaphore.WaitAsync(m_shutdownTokenSource.Token);
                
                try
                {
                    return await Task.Run(() => InternalStep(deltaTime), m_shutdownTokenSource.Token);
                }
                finally
                {
                    m_simulationSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public bool Step(float deltaTime)
        {
            if (!IsInitialized || m_paused)
                return false;

            return InternalStep(deltaTime);
        }

        public void SetPaused(bool paused)
        {
            if (m_paused != paused)
            {
                m_paused = paused;
                m_log.InfoFormat("{0}: Simulation {1}", LogHeader, paused ? "paused" : "resumed");
            }
        }

        public IPhysicsBody CreateRigidBody(RigidBodyDefinition definition)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Engine not initialized");

            try
            {
                var rigidBody = m_config.EnableObjectPooling ? 
                    m_rigidBodyPool.Get() : 
                    new ModernRigidBody();

                rigidBody.Initialize(definition, m_legacyAPI, m_scene);
                
                if (m_rigidBodies.TryAdd(definition.LocalID, rigidBody))
                {
                    // Add to spatial index
                    m_spatialIndex?.AddObject(definition.LocalID, definition.Position, definition.Scale);
                    
                    m_log.DebugFormat("{0}: Created rigid body {1}", LogHeader, definition.LocalID);
                    return rigidBody;
                }
                else
                {
                    // Return to pool if failed to add
                    if (m_config.EnableObjectPooling)
                        m_rigidBodyPool.Return(rigidBody);
                    
                    throw new InvalidOperationException($"Rigid body with ID {definition.LocalID} already exists");
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to create rigid body {1}: {2}", 
                    LogHeader, definition.LocalID, ex.Message);
                throw;
            }
        }

        public ICharacterController CreateCharacterController(CharacterDefinition definition)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Engine not initialized");

            try
            {
                var character = m_config.EnableObjectPooling ?
                    m_characterPool.Get() :
                    new ModernCharacterController();

                character.Initialize(definition, m_legacyAPI, m_scene);

                if (m_characterControllers.TryAdd(definition.LocalID, character))
                {
                    // Add to spatial index
                    var extents = new OMV.Vector3(definition.Radius, definition.Radius, definition.Height);
                    m_spatialIndex?.AddObject(definition.LocalID, definition.Position, extents);

                    m_log.DebugFormat("{0}: Created character controller {1}", LogHeader, definition.LocalID);
                    return character;
                }
                else
                {
                    if (m_config.EnableObjectPooling)
                        m_characterPool.Return(character);

                    throw new InvalidOperationException($"Character controller with ID {definition.LocalID} already exists");
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to create character controller {1}: {2}",
                    LogHeader, definition.LocalID, ex.Message);
                throw;
            }
        }

        public IVehicleController CreateVehicleController(VehicleDefinition definition)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Engine not initialized");

            try
            {
                var vehicle = m_config.EnableObjectPooling ?
                    m_vehiclePool.Get() :
                    new ModernVehicleController();

                vehicle.Initialize(definition, m_legacyAPI, m_scene);

                if (m_vehicleControllers.TryAdd(definition.LocalID, vehicle))
                {
                    m_log.DebugFormat("{0}: Created vehicle controller {1}", LogHeader, definition.LocalID);
                    return vehicle;
                }
                else
                {
                    if (m_config.EnableObjectPooling)
                        m_vehiclePool.Return(vehicle);

                    throw new InvalidOperationException($"Vehicle controller with ID {definition.LocalID} already exists");
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to create vehicle controller {1}: {2}",
                    LogHeader, definition.LocalID, ex.Message);
                throw;
            }
        }

        public void RemoveObject(uint localID)
        {
            if (!IsInitialized)
                return;

            bool removed = false;

            // Try to remove from rigid bodies
            if (m_rigidBodies.TryRemove(localID, out IPhysicsBody rigidBody))
            {
                try
                {
                    rigidBody.Dispose();
                    if (m_config.EnableObjectPooling && rigidBody is ModernRigidBody modernRigid)
                        m_rigidBodyPool.Return(modernRigid);
                    removed = true;
                }
                catch (ObjectDisposedException)
                {
                    // Object was already disposed, this is safe to ignore
                    removed = true;
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("{0}: Error disposing rigid body {1}: {2}", LogHeader, localID, ex.Message);
                    removed = true; // Still consider it removed to prevent stuck objects
                }
            }

            // Try to remove from character controllers
            if (m_characterControllers.TryRemove(localID, out ICharacterController character))
            {
                try
                {
                    character.Dispose();
                    if (m_config.EnableObjectPooling && character is ModernCharacterController modernChar)
                        m_characterPool.Return(modernChar);
                    removed = true;
                }
                catch (ObjectDisposedException)
                {
                    removed = true;
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("{0}: Error disposing character controller {1}: {2}", LogHeader, localID, ex.Message);
                    removed = true;
                }
            }

            // Try to remove from vehicle controllers
            if (m_vehicleControllers.TryRemove(localID, out IVehicleController vehicle))
            {
                try
                {
                    vehicle.Dispose();
                    if (m_config.EnableObjectPooling && vehicle is ModernVehicleController modernVehicle)
                        m_vehiclePool.Return(modernVehicle);
                    removed = true;
                }
                catch (ObjectDisposedException)
                {
                    removed = true;
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("{0}: Error disposing vehicle controller {1}: {2}", LogHeader, localID, ex.Message);
                    removed = true;
                }
            }

            if (removed)
            {
                // Remove from spatial index
                m_spatialIndex?.RemoveObject(localID);
                m_log.DebugFormat("{0}: Removed object {1}", LogHeader, localID);
            }
        }

        public IPhysicsBody GetPhysicsBody(uint localID)
        {
            return m_rigidBodies.TryGetValue(localID, out IPhysicsBody body) ? body : null;
        }

        public ICharacterController GetCharacterController(uint localID)
        {
            return m_characterControllers.TryGetValue(localID, out ICharacterController controller) ? controller : null;
        }

        public IVehicleController GetVehicleController(uint localID)
        {
            return m_vehicleControllers.TryGetValue(localID, out IVehicleController controller) ? controller : null;
        }

        public ICollisionWorld GetCollisionWorld() => m_collisionWorld;
        public ISpatialIndex GetSpatialIndex() => m_spatialIndex;
        public IConstraintSolver GetConstraintSolver() => m_constraintSolver;
        public BSAPITemplate GetLegacyAPI() => m_legacyAPI;

        public void SetDebugDrawer(IDebugDrawer drawer)
        {
            m_debugDrawer = drawer;
        }

        public PhysicsStatistics GetStatistics()
        {
            UpdateStatistics();
            return m_statistics;
        }

        public void SetProfilingEnabled(bool enabled)
        {
            m_profilingEnabled = enabled;
        }

        public async Task OptimizeAsync()
        {
            if (!IsInitialized)
                return;

            try
            {
                m_log.InfoFormat("{0}: Starting optimization", LogHeader);

                await Task.Run(() =>
                {
                    // Optimize spatial index
                    m_spatialIndex?.Optimize();

                    // Force garbage collection
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });

                m_log.InfoFormat("{0}: Optimization completed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Optimization failed: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Private Methods

        private async Task InitializeMultithreadingAsync()
        {
            // Initialize thread pool for physics tasks
            ThreadPool.SetMinThreads(m_config.MaxThreads, m_config.MaxThreads);
            
            m_log.InfoFormat("{0}: Multithreading initialized with {1} threads", 
                LogHeader, m_config.MaxThreads);
            
            await Task.CompletedTask;
        }

        private bool InternalStep(float deltaTime)
        {
            if (m_profilingEnabled)
                m_performanceTimer.Restart();

            try
            {
                // Update spatial index if needed
                if (m_spatialIndex != null && m_scene.m_simulationStep % 60 == 0)
                {
                    UpdateSpatialIndex();
                }

                // Update statistics periodically
                if (m_profilingEnabled && (DateTime.UtcNow - m_lastStatsUpdate).TotalSeconds >= 1.0)
                {
                    UpdateStatistics();
                    OnPerformanceUpdate?.Invoke(m_statistics);
                    m_lastStatsUpdate = DateTime.UtcNow;
                }

                return true;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during simulation step: {1}", LogHeader, ex.Message);
                return false;
            }
            finally
            {
                if (m_profilingEnabled)
                {
                    m_performanceTimer.Stop();
                }
            }
        }

        private void UpdateSpatialIndex()
        {
            if (m_spatialIndex == null)
                return;

            try
            {
                // Update rigid bodies in spatial index
                foreach (var kvp in m_rigidBodies)
                {
                    var body = kvp.Value;
                    // Estimate extents based on type - would need actual shape data
                    var extents = PhysicsConstants.DEFAULT_EXTENT_VECTOR; 
                    m_spatialIndex.UpdateObject(kvp.Key, body.Position, extents);
                }

                // Update character controllers
                foreach (var kvp in m_characterControllers)
                {
                    var character = kvp.Value;
                    var extents = new OMV.Vector3(0.5f, 0.5f, 2.0f); // Typical character size
                    m_spatialIndex.UpdateObject(kvp.Key, character.Position, extents);
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error updating spatial index: {1}", LogHeader, ex.Message);
            }
        }

        private void UpdateStatistics()
        {
            try
            {
                m_statistics.ActiveRigidBodies = m_rigidBodies.Count;
                m_statistics.ActiveCharacters = m_characterControllers.Count;
                m_statistics.ActiveVehicles = m_vehicleControllers.Count;
                m_statistics.MemoryUsageBytes = GC.GetTotalMemory(false);
                m_statistics.ThreadCount = Process.GetCurrentProcess().Threads.Count;
                m_statistics.LastUpdate = DateTime.UtcNow;

                if (m_performanceTimer.IsRunning)
                {
                    m_statistics.AverageStepTime = (float)m_performanceTimer.Elapsed.TotalMilliseconds;
                    m_statistics.SimulationFPS = m_statistics.AverageStepTime > 0 ? 
                        1000.0f / m_statistics.AverageStepTime : 0f;
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error updating statistics: {1}", LogHeader, ex.Message);
            }
        }

        private async Task CleanupObjectsAsync()
        {
            await Task.Run(() =>
            {
                // Cleanup all objects
                foreach (var body in m_rigidBodies.Values)
                    body.Dispose();
                m_rigidBodies.Clear();

                foreach (var character in m_characterControllers.Values)
                    character.Dispose();
                m_characterControllers.Clear();

                foreach (var vehicle in m_vehicleControllers.Values)
                    vehicle.Dispose();
                m_vehicleControllers.Clear();

                // Cleanup advanced features
                m_collisionWorld?.Dispose();
                m_spatialIndex?.Dispose();
                m_constraintSolver?.Dispose();
            });
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (m_disposed)
                return;

            try
            {
                ShutdownAsync().Wait(5000); // Wait up to 5 seconds for graceful shutdown

                m_shutdownTokenSource?.Dispose();
                m_simulationSemaphore?.Dispose();
                m_performanceTimer?.Stop();

                m_disposed = true;
                m_log.InfoFormat("{0}: Modern Bullet Physics disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion
    }
}