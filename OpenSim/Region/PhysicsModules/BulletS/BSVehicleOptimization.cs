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
using System.Reflection;
using log4net;
using OMV = OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Vehicle Physics Optimization System
    /// Provides advanced optimizations for vehicle physics calculations
    /// </summary>
    public static class BSVehicleOptimization
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[VEHICLE OPTIMIZATION]";

        #region Configuration

        private static readonly float VehicleStationaryThreshold = 0.1f;
        private static readonly float VehicleStationaryTimeThreshold = 2.0f;
        private static readonly int VehicleUpdateSkipFrames = 3;
        private static readonly float VehicleDistanceUpdateThreshold = 50.0f;

        #endregion

        #region Data Structures

        /// <summary>
        /// Vehicle optimization context for each vehicle
        /// </summary>
        public class VehicleOptimizationContext
        {
            public uint VehicleID;
            public Vehicle VehicleType;
            public OMV.Vector3 LastPosition;
            public OMV.Vector3 LastVelocity;
            public float StationaryTime;
            public int FramesSinceUpdate;
            public bool IsStationary;
            public bool IsOptimized;
            public DateTime LastUpdateTime;
            public float DistanceFromViewer;
        }

        /// <summary>
        /// Vehicle optimization statistics
        /// </summary>
        public struct VehicleOptimizationStats
        {
            public int TotalVehicles;
            public int OptimizedVehicles;
            public int StationaryVehicles;
            public int DistantVehicles;
            public float OptimizationRatio;
            public float PerformanceGain;
        }

        #endregion

        #region State Management

        private static readonly Dictionary<uint, VehicleOptimizationContext> vehicleContexts = 
            new Dictionary<uint, VehicleOptimizationContext>();
        private static VehicleOptimizationStats currentStats = new VehicleOptimizationStats();

        #endregion

        #region Public API

        /// <summary>
        /// Initialize vehicle optimization system
        /// </summary>
        public static void Initialize()
        {
            vehicleContexts.Clear();
            currentStats = new VehicleOptimizationStats();
            m_log.InfoFormat("{0}: Vehicle optimization system initialized", LogHeader);
        }

        /// <summary>
        /// Register a vehicle for optimization tracking
        /// </summary>
        public static void RegisterVehicle(uint vehicleID, Vehicle vehicleType, OMV.Vector3 position)
        {
            var context = new VehicleOptimizationContext
            {
                VehicleID = vehicleID,
                VehicleType = vehicleType,
                LastPosition = position,
                LastVelocity = OMV.Vector3.Zero,
                StationaryTime = 0f,
                FramesSinceUpdate = 0,
                IsStationary = false,
                IsOptimized = false,
                LastUpdateTime = DateTime.UtcNow,
                DistanceFromViewer = 0f
            };

            vehicleContexts[vehicleID] = context;
        }

        /// <summary>
        /// Unregister a vehicle from optimization tracking
        /// </summary>
        public static void UnregisterVehicle(uint vehicleID)
        {
            vehicleContexts.Remove(vehicleID);
        }

        /// <summary>
        /// Check if vehicle should be optimized this frame
        /// </summary>
        public static bool ShouldOptimizeVehicle(uint vehicleID, OMV.Vector3 currentPosition, 
            OMV.Vector3 currentVelocity, float deltaTime)
        {
            if (!vehicleContexts.TryGetValue(vehicleID, out VehicleOptimizationContext context))
                return false;

            // Update position and velocity
            OMV.Vector3 positionDelta = currentPosition - context.LastPosition;
            float velocityMagnitude = currentVelocity.Length();
            float positionChange = positionDelta.Length();

            // Check if vehicle is stationary
            bool isCurrentlyStationary = velocityMagnitude < VehicleStationaryThreshold && 
                                         positionChange < VehicleStationaryThreshold * deltaTime;

            if (isCurrentlyStationary)
            {
                context.StationaryTime += deltaTime;
                context.IsStationary = context.StationaryTime > VehicleStationaryTimeThreshold;
            }
            else
            {
                context.StationaryTime = 0f;
                context.IsStationary = false;
            }

            // Update distance-based optimization
            context.DistanceFromViewer = GetDistanceFromNearestViewer(currentPosition);
            bool isDistant = context.DistanceFromViewer > VehicleDistanceUpdateThreshold;

            // Determine if should optimize
            bool shouldOptimize = false;
            
            if (context.IsStationary)
            {
                // Stationary vehicles can be updated less frequently
                context.FramesSinceUpdate++;
                shouldOptimize = context.FramesSinceUpdate >= VehicleUpdateSkipFrames * 2;
            }
            else if (isDistant)
            {
                // Distant vehicles can be updated less frequently
                context.FramesSinceUpdate++;
                shouldOptimize = context.FramesSinceUpdate >= VehicleUpdateSkipFrames;
            }
            else
            {
                // Active, nearby vehicles should be updated every frame
                shouldOptimize = false;
                context.FramesSinceUpdate = 0;
            }

            if (shouldOptimize)
            {
                context.FramesSinceUpdate = 0;
            }

            // Update context
            context.LastPosition = currentPosition;
            context.LastVelocity = currentVelocity;
            context.IsOptimized = shouldOptimize;
            context.LastUpdateTime = DateTime.UtcNow;

            return !shouldOptimize; // Return false if we should skip this frame
        }

        /// <summary>
        /// Get optimized vehicle update rate based on state
        /// </summary>
        public static float GetOptimizedUpdateRate(uint vehicleID)
        {
            if (!vehicleContexts.TryGetValue(vehicleID, out VehicleOptimizationContext context))
                return 1.0f;

            if (context.IsStationary)
                return 0.25f; // Update 4x less frequently
            else if (context.DistanceFromViewer > VehicleDistanceUpdateThreshold)
                return 0.5f;  // Update 2x less frequently
            else
                return 1.0f;  // Normal update rate
        }

        /// <summary>
        /// Check if vehicle is in an optimized state
        /// </summary>
        public static bool IsVehicleOptimized(uint vehicleID)
        {
            return vehicleContexts.TryGetValue(vehicleID, out VehicleOptimizationContext context) && 
                   context.IsOptimized;
        }

        /// <summary>
        /// Get vehicle optimization statistics
        /// </summary>
        public static VehicleOptimizationStats GetOptimizationStats()
        {
            var stats = new VehicleOptimizationStats
            {
                TotalVehicles = vehicleContexts.Count
            };

            int optimizedCount = 0;
            int stationaryCount = 0;
            int distantCount = 0;

            foreach (var context in vehicleContexts.Values)
            {
                if (context.IsOptimized) optimizedCount++;
                if (context.IsStationary) stationaryCount++;
                if (context.DistanceFromViewer > VehicleDistanceUpdateThreshold) distantCount++;
            }

            stats.OptimizedVehicles = optimizedCount;
            stats.StationaryVehicles = stationaryCount;
            stats.DistantVehicles = distantCount;
            stats.OptimizationRatio = stats.TotalVehicles > 0 ? 
                (float)optimizedCount / stats.TotalVehicles : 0f;
            stats.PerformanceGain = stats.OptimizationRatio * 60f; // Estimate performance gain

            return stats;
        }

        /// <summary>
        /// Clean up old vehicle contexts
        /// </summary>
        public static void CleanupOldContexts(float maxAge = 300f)
        {
            var now = DateTime.UtcNow;
            var toRemove = new List<uint>();

            foreach (var kvp in vehicleContexts)
            {
                if ((now - kvp.Value.LastUpdateTime).TotalSeconds > maxAge)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (uint id in toRemove)
            {
                vehicleContexts.Remove(id);
            }

            if (toRemove.Count > 0)
            {
                m_log.DebugFormat("{0}: Cleaned up {1} old vehicle contexts", LogHeader, toRemove.Count);
            }
        }

        #endregion

        #region Vehicle-Specific Optimizations

        /// <summary>
        /// Optimize linear motor calculations
        /// </summary>
        public static class LinearMotorOptimizer
        {
            private static readonly Dictionary<uint, OMV.Vector3> cachedLinearMotorValues = 
                new Dictionary<uint, OMV.Vector3>();

            public static OMV.Vector3 GetOptimizedLinearMotor(uint vehicleID, OMV.Vector3 currentMotor, 
                float decay, float timeStep)
            {
                // Use cached values for minimal changes
                if (cachedLinearMotorValues.TryGetValue(vehicleID, out OMV.Vector3 cached))
                {
                    OMV.Vector3 delta = currentMotor - cached;
                    if (delta.LengthSquared() < 0.01f) // Very small change
                    {
                        return cached; // Return cached value
                    }
                }

                // Calculate new value and cache it
                OMV.Vector3 optimizedMotor = currentMotor * (1f - (decay * timeStep));
                cachedLinearMotorValues[vehicleID] = optimizedMotor;
                return optimizedMotor;
            }

            public static void RemoveVehicle(uint vehicleID)
            {
                cachedLinearMotorValues.Remove(vehicleID);
            }
        }

        /// <summary>
        /// Optimize angular motor calculations
        /// </summary>
        public static class AngularMotorOptimizer
        {
            private static readonly Dictionary<uint, OMV.Vector3> cachedAngularMotorValues = 
                new Dictionary<uint, OMV.Vector3>();

            public static OMV.Vector3 GetOptimizedAngularMotor(uint vehicleID, OMV.Vector3 currentMotor, 
                float decay, float timeStep)
            {
                // Use cached values for minimal changes
                if (cachedAngularMotorValues.TryGetValue(vehicleID, out OMV.Vector3 cached))
                {
                    OMV.Vector3 delta = currentMotor - cached;
                    if (delta.LengthSquared() < 0.01f) // Very small change
                    {
                        return cached; // Return cached value
                    }
                }

                // Calculate new value and cache it
                OMV.Vector3 optimizedMotor = currentMotor * (1f - (decay * timeStep));
                cachedAngularMotorValues[vehicleID] = optimizedMotor;
                return optimizedMotor;
            }

            public static void RemoveVehicle(uint vehicleID)
            {
                cachedAngularMotorValues.Remove(vehicleID);
            }
        }

        /// <summary>
        /// Optimize hover calculations
        /// </summary>
        public static class HoverOptimizer
        {
            private static readonly Dictionary<uint, float> cachedHoverHeights = 
                new Dictionary<uint, float>();
            private static readonly Dictionary<uint, DateTime> lastHeightCheck = 
                new Dictionary<uint, DateTime>();

            public static float GetOptimizedHoverHeight(uint vehicleID, OMV.Vector3 position, 
                BSScene scene, bool forceUpdate = false)
            {
                var now = DateTime.UtcNow;
                
                // Check if we have a recent cached value
                if (!forceUpdate && 
                    cachedHoverHeights.TryGetValue(vehicleID, out float cachedHeight) &&
                    lastHeightCheck.TryGetValue(vehicleID, out DateTime lastCheck))
                {
                    // Use cached value if it's less than 1 second old
                    if ((now - lastCheck).TotalSeconds < 1.0)
                    {
                        return cachedHeight;
                    }
                }

                // Calculate new height and cache it
                float terrainHeight = scene.TerrainManager.GetTerrainHeightAtXYZ(position);
                cachedHoverHeights[vehicleID] = terrainHeight;
                lastHeightCheck[vehicleID] = now;
                
                return terrainHeight;
            }

            public static void RemoveVehicle(uint vehicleID)
            {
                cachedHoverHeights.Remove(vehicleID);
                lastHeightCheck.Remove(vehicleID);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get distance to nearest viewer (simplified)
        /// </summary>
        private static float GetDistanceFromNearestViewer(OMV.Vector3 position)
        {
            // This is a simplified implementation
            // In reality, this would check distance to actual avatars/viewers
            // For now, we'll use a placeholder that returns 0 (always "nearby")
            // This ensures vehicles are not optimized due to distance until properly implemented
            return 0f;
        }

        #endregion
    }

    /// <summary>
    /// Vehicle physics optimization extension for BSDynamics
    /// </summary>
    public partial class BSDynamics
    {
        #region Optimization Integration

        /// <summary>
        /// Check if this vehicle step should be optimized
        /// </summary>
        private bool ShouldOptimizeThisStep(float timeStep)
        {
            if (!BSParam.UseVehicleOptimization)
                return false;

            return BSVehicleOptimization.ShouldOptimizeVehicle(
                ControllingPrim.LocalID,
                VehiclePosition,
                VehicleVelocity,
                timeStep);
        }

        /// <summary>
        /// Get optimized motor value
        /// </summary>
        private OMV.Vector3 GetOptimizedLinearMotor(OMV.Vector3 currentMotor, float decay, float timeStep)
        {
            if (!BSParam.UseVehicleOptimization)
                return currentMotor * (1f - (decay * timeStep));

            return BSVehicleOptimization.LinearMotorOptimizer.GetOptimizedLinearMotor(
                ControllingPrim.LocalID, currentMotor, decay, timeStep);
        }

        /// <summary>
        /// Get optimized angular motor value
        /// </summary>
        private OMV.Vector3 GetOptimizedAngularMotor(OMV.Vector3 currentMotor, float decay, float timeStep)
        {
            if (!BSParam.UseVehicleOptimization)
                return currentMotor * (1f - (decay * timeStep));

            return BSVehicleOptimization.AngularMotorOptimizer.GetOptimizedAngularMotor(
                ControllingPrim.LocalID, currentMotor, decay, timeStep);
        }

        /// <summary>
        /// Get optimized hover height
        /// </summary>
        private float GetOptimizedHoverHeight(OMV.Vector3 position)
        {
            if (!BSParam.UseVehicleOptimization)
                return m_physicsScene.TerrainManager.GetTerrainHeightAtXYZ(position);

            return BSVehicleOptimization.HoverOptimizer.GetOptimizedHoverHeight(
                ControllingPrim.LocalID, position, m_physicsScene);
        }

        #endregion
    }
}