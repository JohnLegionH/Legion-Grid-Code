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

using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Physics constants and default values for BulletSim advanced physics systems
    /// </summary>
    public static class PhysicsConstants
    {
        #region Universal Physics Constants
        
        /// <summary>
        /// Standard gravity acceleration in m/s²
        /// </summary>
        public const float GRAVITY_ACCELERATION = 9.81f;
        
        /// <summary>
        /// Speed of light in m/s (for relativistic calculations if needed)
        /// </summary>
        public const float SPEED_OF_LIGHT = 299792458.0f;
        
        #endregion

        #region Simulation Parameters
        
        /// <summary>
        /// Default physics simulation timestep in seconds
        /// </summary>
        public const float DEFAULT_TIMESTEP = 1.0f / 45.0f; // 45 FPS
        
        /// <summary>
        /// Target frame rate for physics simulation
        /// </summary>
        public const float TARGET_FRAME_RATE = 45.0f;
        
        /// <summary>
        /// Maximum physics timestep to prevent instability
        /// </summary>
        public const float MAX_TIMESTEP = 1.0f / 10.0f; // 10 FPS minimum
        
        /// <summary>
        /// Minimum physics timestep for high precision
        /// </summary>
        public const float MIN_TIMESTEP = 1.0f / 120.0f; // 120 FPS maximum
        
        #endregion

        #region Spatial and Size Constants
        
        /// <summary>
        /// Default extent size for physics objects in meters
        /// </summary>
        public const float DEFAULT_EXTENT_SIZE = 1.0f;
        
        /// <summary>
        /// Minimum object size for physics calculations
        /// </summary>
        public const float MIN_OBJECT_SIZE = 0.01f;
        
        /// <summary>
        /// Maximum object size for physics calculations
        /// </summary>
        public const float MAX_OBJECT_SIZE = 256.0f;
        
        /// <summary>
        /// Default collision margin for objects
        /// </summary>
        public const float DEFAULT_COLLISION_MARGIN = 0.04f;
        
        /// <summary>
        /// Spatial index bucket size for collision detection
        /// </summary>
        public const float SPATIAL_INDEX_BUCKET_SIZE = 10.0f;
        
        #endregion

        #region Performance and Threading
        
        /// <summary>
        /// Default number of physics worker threads
        /// </summary>
        public const int DEFAULT_WORKER_THREADS = 4;
        
        /// <summary>
        /// Maximum number of physics worker threads
        /// </summary>
        public const int MAX_WORKER_THREADS = 16;
        
        /// <summary>
        /// Default batch size for multithreaded operations
        /// </summary>
        public const int DEFAULT_BATCH_SIZE = 100;
        
        /// <summary>
        /// Maximum CPU usage percentage for advanced physics
        /// </summary>
        public const float MAX_CPU_USAGE_PERCENT = 25.0f;
        
        /// <summary>
        /// Performance monitoring interval in seconds
        /// </summary>
        public const float PERFORMANCE_MONITOR_INTERVAL = 5.0f;
        
        #endregion

        #region Object Limits
        
        /// <summary>
        /// Maximum number of physics objects per scene
        /// </summary>
        public const int MAX_PHYSICS_OBJECTS = 10000;
        
        /// <summary>
        /// Maximum number of constraints per scene
        /// </summary>
        public const int MAX_CONSTRAINTS = 1000;
        
        /// <summary>
        /// Maximum number of collision pairs to process per frame
        /// </summary>
        public const int MAX_COLLISION_PAIRS_PER_FRAME = 500;
        
        #endregion

        #region Fluid Dynamics Constants
        
        /// <summary>
        /// Water density at standard conditions (kg/m³)
        /// </summary>
        public const float WATER_DENSITY = 1000.0f;
        
        /// <summary>
        /// Air density at standard conditions (kg/m³)
        /// </summary>
        public const float AIR_DENSITY = 1.225f;
        
        /// <summary>
        /// Water viscosity at 20°C (Pa·s)
        /// </summary>
        public const float WATER_VISCOSITY = 0.001f;
        
        /// <summary>
        /// Air viscosity at 20°C (Pa·s)
        /// </summary>
        public const float AIR_VISCOSITY = 0.0000181f;
        
        /// <summary>
        /// Standard atmospheric pressure (Pa)
        /// </summary>
        public const float STANDARD_PRESSURE = 101325.0f;
        
        /// <summary>
        /// Standard temperature (K) - 20°C
        /// </summary>
        public const float STANDARD_TEMPERATURE = 293.15f;
        
        #endregion

        #region Material Properties
        
        /// <summary>
        /// Default material density (kg/m³)
        /// </summary>
        public const float DEFAULT_MATERIAL_DENSITY = 1000.0f;
        
        /// <summary>
        /// Default material friction coefficient
        /// </summary>
        public const float DEFAULT_FRICTION = 0.5f;
        
        /// <summary>
        /// Default material restitution (bounciness)
        /// </summary>
        public const float DEFAULT_RESTITUTION = 0.0f;
        
        /// <summary>
        /// Glass fracture stress (Pa)
        /// </summary>
        public const float GLASS_FRACTURE_STRESS = 50e6f;
        
        /// <summary>
        /// Steel fracture stress (Pa)
        /// </summary>
        public const float STEEL_FRACTURE_STRESS = 400e6f;
        
        /// <summary>
        /// Wood fracture stress (Pa)
        /// </summary>
        public const float WOOD_FRACTURE_STRESS = 40e6f;
        
        #endregion

        #region Advanced Physics Systems
        
        /// <summary>
        /// Maximum number of fluid volumes per scene
        /// </summary>
        public const int MAX_FLUID_VOLUMES = 50;
        
        /// <summary>
        /// Maximum number of soft bodies per scene
        /// </summary>
        public const int MAX_SOFT_BODIES = 25;
        
        /// <summary>
        /// Maximum number of particle systems per scene
        /// </summary>
        public const int MAX_PARTICLE_SYSTEMS = 30;
        
        /// <summary>
        /// Maximum particles per particle system
        /// </summary>
        public const int MAX_PARTICLES_PER_SYSTEM = 2000;
        
        /// <summary>
        /// Maximum number of destructible objects per scene
        /// </summary>
        public const int MAX_DESTRUCTIBLE_OBJECTS = 100;
        
        /// <summary>
        /// Maximum fragments per destructible object
        /// </summary>
        public const int MAX_FRAGMENTS_PER_OBJECT = 50;
        
        /// <summary>
        /// Default fragment lifetime in seconds
        /// </summary>
        public const float DEFAULT_FRAGMENT_LIFETIME = 30.0f;
        
        #endregion

        #region Vector Constants
        
        /// <summary>
        /// Zero vector constant
        /// </summary>
        public static readonly OMV.Vector3 VECTOR_ZERO = OMV.Vector3.Zero;
        
        /// <summary>
        /// Unit X vector constant
        /// </summary>
        public static readonly OMV.Vector3 VECTOR_UNIT_X = OMV.Vector3.UnitX;
        
        /// <summary>
        /// Unit Y vector constant
        /// </summary>
        public static readonly OMV.Vector3 VECTOR_UNIT_Y = OMV.Vector3.UnitY;
        
        /// <summary>
        /// Unit Z vector constant
        /// </summary>
        public static readonly OMV.Vector3 VECTOR_UNIT_Z = OMV.Vector3.UnitZ;
        
        /// <summary>
        /// Default gravity vector
        /// </summary>
        public static readonly OMV.Vector3 GRAVITY_VECTOR = new OMV.Vector3(0, 0, -GRAVITY_ACCELERATION);
        
        /// <summary>
        /// Default extent vector for objects
        /// </summary>
        public static readonly OMV.Vector3 DEFAULT_EXTENT_VECTOR = new OMV.Vector3(DEFAULT_EXTENT_SIZE, DEFAULT_EXTENT_SIZE, DEFAULT_EXTENT_SIZE);
        
        #endregion

        #region Tolerance and Precision
        
        /// <summary>
        /// Floating point comparison tolerance
        /// </summary>
        public const float FLOAT_TOLERANCE = 1e-6f;
        
        /// <summary>
        /// Very small value for avoiding division by zero
        /// </summary>
        public const float EPSILON = 1e-8f;
        
        /// <summary>
        /// Large value for "infinity" comparisons
        /// </summary>
        public const float LARGE_VALUE = 1e30f;
        
        #endregion

        #region Quality and Performance Scaling
        
        /// <summary>
        /// Quality multipliers for different simulation quality levels
        /// </summary>
        public static readonly float[] QUALITY_MULTIPLIERS = { 0.2f, 0.4f, 1.0f, 2.0f, 4.0f };
        
        /// <summary>
        /// Performance scaling factors for adaptive quality
        /// </summary>
        public static readonly float[] PERFORMANCE_SCALING_FACTORS = { 0.1f, 0.25f, 0.5f, 0.75f, 1.0f };
        
        #endregion

        #region Timeouts and Intervals
        
        /// <summary>
        /// Default timeout for async operations in milliseconds
        /// </summary>
        public const int DEFAULT_ASYNC_TIMEOUT_MS = 5000;
        
        /// <summary>
        /// Performance report interval in seconds
        /// </summary>
        public const float PERFORMANCE_REPORT_INTERVAL = 60.0f;
        
        /// <summary>
        /// Object cleanup interval in seconds
        /// </summary>
        public const float OBJECT_CLEANUP_INTERVAL = 30.0f;
        
        /// <summary>
        /// Memory garbage collection interval in seconds
        /// </summary>
        public const float GC_INTERVAL = 60.0f;
        
        #endregion
    }
}