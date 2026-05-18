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
using System.Collections.Generic;
using System.Threading.Tasks;
using OMV = OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Modern Physics Engine Configuration for enhanced features
    /// </summary>
    public class PhysicsConfig
    {
        public bool EnableMultithreading { get; set; } = true;
        public int MaxThreads { get; set; } = Environment.ProcessorCount;
        public bool EnableObjectPooling { get; set; } = true;
        public bool EnableSpatialOptimization { get; set; } = true;
        public bool EnableContinuousCollisionDetection { get; set; } = true;
        public bool EnableGPUAcceleration { get; set; } = false;
        public float UpdateFrequencyHz { get; set; } = 60.0f;
        public int MaxObjectsPerThread { get; set; } = 1000;
        public bool EnablePerformanceMonitoring { get; set; } = true;
    }

    /// <summary>
    /// Enhanced physics body definition with modern features
    /// </summary>
    public class RigidBodyDefinition
    {
        public uint LocalID { get; set; }
        public OMV.Vector3 Position { get; set; }
        public OMV.Quaternion Rotation { get; set; }
        public OMV.Vector3 Scale { get; set; }
        public float Mass { get; set; }
        public bool IsStatic { get; set; }
        public bool IsKinematic { get; set; }
        public BSPhysicsShapeType ShapeType { get; set; }
        public CollisionType CollisionGroup { get; set; }
        public CollisionType CollisionMask { get; set; }
        public float Friction { get; set; } = 0.5f;
        public float Restitution { get; set; } = 0.0f;
        public float LinearDamping { get; set; } = 0.0f;
        public float AngularDamping { get; set; } = 0.0f;
        public bool EnableCCD { get; set; } = false;
        public object UserData { get; set; }
    }

    /// <summary>
    /// Enhanced character controller definition
    /// </summary>
    public class CharacterDefinition
    {
        public uint LocalID { get; set; }
        public OMV.Vector3 Position { get; set; }
        public float Height { get; set; }
        public float Radius { get; set; }
        public float StepHeight { get; set; } = 0.5f;
        public float SlopeLimit { get; set; } = 45.0f;
        public float Mass { get; set; }
        public bool UseAdvancedGroundDetection { get; set; } = true;
        public bool EnableMovementPrediction { get; set; } = true;
        public float ResponseTime { get; set; } = 0.1f;
        public object UserData { get; set; }
    }

    /// <summary>
    /// Vehicle controller definition for advanced vehicle physics
    /// </summary>
    public class VehicleDefinition
    {
        public uint LocalID { get; set; }
        public OMV.Vector3 Position { get; set; }
        public OMV.Quaternion Rotation { get; set; }
        public Vehicle VehicleType { get; set; }
        public float Mass { get; set; }
        public bool EnableAdvancedSuspension { get; set; } = true;
        public bool EnableTireSimulation { get; set; } = true;
        public bool EnableAerodynamics { get; set; } = false;
        public object UserData { get; set; }
    }

    /// <summary>
    /// Advanced physics statistics and monitoring
    /// </summary>
    public class PhysicsStatistics
    {
        public float SimulationFPS { get; set; }
        public float AverageStepTime { get; set; }
        public int ActiveRigidBodies { get; set; }
        public int ActiveCharacters { get; set; }
        public int ActiveVehicles { get; set; }
        public int ActiveConstraints { get; set; }
        public long MemoryUsageBytes { get; set; }
        public int ThreadCount { get; set; }
        public float ThreadUtilization { get; set; }
        public int CollisionChecks { get; set; }
        public int SpatialQueries { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    /// <summary>
    /// Modern physics body interface with enhanced capabilities
    /// </summary>
    public interface IPhysicsBody : IDisposable
    {
        uint LocalID { get; }
        OMV.Vector3 Position { get; set; }
        OMV.Quaternion Rotation { get; set; }
        OMV.Vector3 LinearVelocity { get; set; }
        OMV.Vector3 AngularVelocity { get; set; }
        float Mass { get; set; }
        bool IsActive { get; set; }
        bool IsStatic { get; }
        object UserData { get; set; }

        void ApplyForce(OMV.Vector3 force);
        void ApplyForceAtPosition(OMV.Vector3 force, OMV.Vector3 position);
        void ApplyTorque(OMV.Vector3 torque);
        void ApplyImpulse(OMV.Vector3 impulse);
        void SetMaterial(float friction, float restitution);
        void SetDamping(float linear, float angular);
        void EnableCCD(bool enable);
    }

    /// <summary>
    /// Enhanced character controller interface
    /// </summary>
    public interface ICharacterController : IDisposable
    {
        uint LocalID { get; }
        OMV.Vector3 Position { get; set; }
        OMV.Vector3 LinearVelocity { get; }
        bool IsOnGround { get; }
        bool IsMoving { get; }
        object UserData { get; set; }

        void Move(OMV.Vector3 displacement, float deltaTime);
        void Jump(float force);
        void SetGravity(float gravity);
        bool CanStepUp(float height);
        float GetGroundDistance();
        OMV.Vector3 GetGroundNormal();
    }

    /// <summary>
    /// Advanced vehicle controller interface
    /// </summary>
    public interface IVehicleController : IDisposable
    {
        uint LocalID { get; }
        OMV.Vector3 Position { get; set; }
        OMV.Quaternion Rotation { get; set; }
        OMV.Vector3 LinearVelocity { get; }
        OMV.Vector3 AngularVelocity { get; }
        Vehicle VehicleType { get; set; }
        object UserData { get; set; }

        void SetVehicleParameter(int param, float value);
        void SetVehicleVectorParameter(int param, OMV.Vector3 value);
        void SetVehicleRotationParameter(int param, OMV.Quaternion value);
        void SetVehicleFloatParameter(int param, float value);
        void ProcessVehicleFlags(int flags, bool remove);
        void Reset();
    }

    /// <summary>
    /// Collision world interface for spatial queries
    /// </summary>
    public interface ICollisionWorld
    {
        bool RayTest(OMV.Vector3 from, OMV.Vector3 to, out OMV.Vector3 hitPoint, out OMV.Vector3 hitNormal, out uint hitObject);
        List<uint> OverlapTest(OMV.Vector3 position, float radius);
        List<uint> BoxOverlapTest(OMV.Vector3 center, OMV.Vector3 extents);
        bool SweepTest(OMV.Vector3 from, OMV.Vector3 to, OMV.Vector3 extents, out OMV.Vector3 hitPoint);
    }

    /// <summary>
    /// Spatial indexing interface for optimization
    /// </summary>
    public interface ISpatialIndex
    {
        void AddObject(uint id, OMV.Vector3 position, OMV.Vector3 extents);
        void UpdateObject(uint id, OMV.Vector3 position, OMV.Vector3 extents);
        void RemoveObject(uint id);
        List<uint> Query(OMV.Vector3 position, float radius);
        List<uint> QueryBox(OMV.Vector3 center, OMV.Vector3 extents);
        void Optimize();
    }

    /// <summary>
    /// Constraint solver interface for advanced physics
    /// </summary>
    public interface IConstraintSolver
    {
        void SetIterations(int iterations);
        void SetTimeStep(float timeStep);
        void AddConstraint(object constraint);
        void RemoveConstraint(object constraint);
        void SolveConstraints(float timeStep);
    }

    /// <summary>
    /// Debug drawing interface for physics visualization
    /// </summary>
    public interface IDebugDrawer
    {
        void DrawLine(OMV.Vector3 from, OMV.Vector3 to, OMV.Vector3 color);
        void DrawBox(OMV.Vector3 center, OMV.Vector3 extents, OMV.Vector3 color);
        void DrawSphere(OMV.Vector3 center, float radius, OMV.Vector3 color);
        void DrawText(OMV.Vector3 position, string text);
    }

    /// <summary>
    /// Modern Physics Engine Interface
    /// Provides advanced physics simulation capabilities with modern features
    /// </summary>
    public interface IModernPhysicsEngine : IDisposable
    {
        #region Core Engine Management

        /// <summary>
        /// Engine name and version information
        /// </summary>
        string EngineName { get; }
        string EngineVersion { get; }
        
        /// <summary>
        /// Initialize the physics engine with modern configuration
        /// </summary>
        Task InitializeAsync(PhysicsConfig config);
        
        /// <summary>
        /// Shutdown the physics engine
        /// </summary>
        Task ShutdownAsync();
        
        /// <summary>
        /// Check if the engine is initialized and ready
        /// </summary>
        bool IsInitialized { get; }

        #endregion

        #region Simulation Control

        /// <summary>
        /// Step the physics simulation (potentially async/multithreaded)
        /// </summary>
        Task<bool> StepAsync(float deltaTime);
        
        /// <summary>
        /// Synchronous step for compatibility
        /// </summary>
        bool Step(float deltaTime);
        
        /// <summary>
        /// Pause/resume simulation
        /// </summary>
        void SetPaused(bool paused);
        bool IsPaused { get; }

        #endregion

        #region Object Management

        /// <summary>
        /// Create rigid body with enhanced definition
        /// </summary>
        IPhysicsBody CreateRigidBody(RigidBodyDefinition definition);
        
        /// <summary>
        /// Create character controller with modern features
        /// </summary>
        ICharacterController CreateCharacterController(CharacterDefinition definition);
        
        /// <summary>
        /// Create vehicle controller with advanced physics
        /// </summary>
        IVehicleController CreateVehicleController(VehicleDefinition definition);
        
        /// <summary>
        /// Remove object from simulation
        /// </summary>
        void RemoveObject(uint localID);
        
        /// <summary>
        /// Get object by ID
        /// </summary>
        IPhysicsBody GetPhysicsBody(uint localID);
        ICharacterController GetCharacterController(uint localID);
        IVehicleController GetVehicleController(uint localID);

        #endregion

        #region Advanced Features

        /// <summary>
        /// Get collision world for spatial queries
        /// </summary>
        ICollisionWorld GetCollisionWorld();
        
        /// <summary>
        /// Get spatial index for optimization
        /// </summary>
        ISpatialIndex GetSpatialIndex();
        
        /// <summary>
        /// Get constraint solver
        /// </summary>
        IConstraintSolver GetConstraintSolver();
        
        /// <summary>
        /// Set debug drawer for visualization
        /// </summary>
        void SetDebugDrawer(IDebugDrawer drawer);

        #endregion

        #region Performance & Monitoring

        /// <summary>
        /// Get current performance statistics
        /// </summary>
        PhysicsStatistics GetStatistics();
        
        /// <summary>
        /// Enable/disable performance profiling
        /// </summary>
        void SetProfilingEnabled(bool enabled);
        
        /// <summary>
        /// Optimize performance (cleanup, defragmentation, etc.)
        /// </summary>
        Task OptimizeAsync();

        #endregion

        #region Backward Compatibility

        /// <summary>
        /// Get the underlying legacy API for compatibility
        /// </summary>
        BSAPITemplate GetLegacyAPI();

        #endregion

        #region Events

        /// <summary>
        /// Events for collision detection
        /// </summary>
        event Action<uint, uint, OMV.Vector3> OnCollision;
        event Action<uint, uint> OnSeparation;
        
        /// <summary>
        /// Performance monitoring events
        /// </summary>
        event Action<PhysicsStatistics> OnPerformanceUpdate;

        #endregion
    }
}