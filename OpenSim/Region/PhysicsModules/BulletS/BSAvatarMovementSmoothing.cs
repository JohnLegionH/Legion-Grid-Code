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

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Enhanced avatar movement smoothing system
    /// Provides advanced velocity interpolation and movement prediction
    /// </summary>
    public static class BSAvatarMovementSmoothing
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[AVATAR SMOOTHING]";

        #region Smoothing Configuration

        private static readonly float VelocitySmoothingFactor = 0.85f;
        private static readonly float PositionSmoothingFactor = 0.75f;
        private static readonly float AccelerationSmoothingFactor = 0.9f;
        private static readonly float MinVelocityThreshold = 0.001f;
        private static readonly float MaxVelocityJumpThreshold = 2.0f;
        private static readonly int SmoothingHistorySize = 5;

        #endregion

        #region Smoothing Data Structures

        /// <summary>
        /// Movement sample for smoothing calculations
        /// </summary>
        public struct MovementSample
        {
            public OMV.Vector3 Position;
            public OMV.Vector3 Velocity;
            public OMV.Vector3 Acceleration;
            public float TimeStamp;
            public bool IsValid;

            public MovementSample(OMV.Vector3 pos, OMV.Vector3 vel, OMV.Vector3 accel, float time)
            {
                Position = pos;
                Velocity = vel;
                Acceleration = accel;
                TimeStamp = time;
                IsValid = true;
            }
        }

        /// <summary>
        /// Smoothing context for each avatar
        /// </summary>
        public class AvatarSmoothingContext
        {
            public Queue<MovementSample> MovementHistory = new Queue<MovementSample>();
            public OMV.Vector3 SmoothedVelocity = OMV.Vector3.Zero;
            public OMV.Vector3 SmoothedAcceleration = OMV.Vector3.Zero;
            public OMV.Vector3 PredictedPosition = OMV.Vector3.Zero;
            public float LastUpdateTime = 0f;
            public bool IsInitialized = false;
            public int StabilityCounter = 0;
        }

        private static readonly Dictionary<uint, AvatarSmoothingContext> smoothingContexts = 
            new Dictionary<uint, AvatarSmoothingContext>();

        #endregion

        #region Public API

        /// <summary>
        /// Get or create smoothing context for an avatar
        /// </summary>
        public static AvatarSmoothingContext GetSmoothingContext(uint localID)
        {
            if (!smoothingContexts.TryGetValue(localID, out AvatarSmoothingContext context))
            {
                context = new AvatarSmoothingContext();
                smoothingContexts[localID] = context;
            }
            return context;
        }

        /// <summary>
        /// Remove smoothing context for an avatar
        /// </summary>
        public static void RemoveSmoothingContext(uint localID)
        {
            smoothingContexts.Remove(localID);
        }

        /// <summary>
        /// Apply advanced velocity smoothing
        /// </summary>
        public static OMV.Vector3 SmoothVelocity(uint localID, OMV.Vector3 rawVelocity, OMV.Vector3 targetVelocity, 
            OMV.Vector3 currentPosition, float timeStep)
        {
            var context = GetSmoothingContext(localID);
            float currentTime = GetCurrentTimeSeconds();

            // Initialize context if needed
            if (!context.IsInitialized)
            {
                context.SmoothedVelocity = rawVelocity;
                context.LastUpdateTime = currentTime;
                context.IsInitialized = true;
                return rawVelocity;
            }

            // Calculate time delta
            float deltaTime = currentTime - context.LastUpdateTime;
            if (deltaTime <= 0f) deltaTime = timeStep;

            // Calculate acceleration
            OMV.Vector3 acceleration = (rawVelocity - context.SmoothedVelocity) / deltaTime;

            // Add sample to history
            AddMovementSample(context, currentPosition, rawVelocity, acceleration, currentTime);

            // Apply velocity smoothing
            OMV.Vector3 smoothedVelocity = ApplyVelocitySmoothing(context, rawVelocity, targetVelocity);

            // Update context
            context.SmoothedVelocity = smoothedVelocity;
            context.SmoothedAcceleration = acceleration * AccelerationSmoothingFactor + 
                context.SmoothedAcceleration * (1f - AccelerationSmoothingFactor);
            context.LastUpdateTime = currentTime;

            return smoothedVelocity;
        }

        /// <summary>
        /// Predict future position based on current velocity and acceleration
        /// </summary>
        public static OMV.Vector3 PredictPosition(uint localID, OMV.Vector3 currentPosition, float predictionTime)
        {
            var context = GetSmoothingContext(localID);
            if (!context.IsInitialized)
                return currentPosition;

            // Use kinematic equations: s = ut + 0.5at²
            OMV.Vector3 predictedPosition = currentPosition + 
                (context.SmoothedVelocity * predictionTime) + 
                (context.SmoothedAcceleration * (0.5f * predictionTime * predictionTime));

            context.PredictedPosition = predictedPosition;
            return predictedPosition;
        }

        /// <summary>
        /// Apply advanced position smoothing for network updates
        /// </summary>
        public static OMV.Vector3 SmoothPosition(uint localID, OMV.Vector3 rawPosition, OMV.Vector3 lastPosition, float deltaTime)
        {
            var context = GetSmoothingContext(localID);
            
            // Calculate position delta
            OMV.Vector3 positionDelta = rawPosition - lastPosition;
            float deltaLength = positionDelta.Length();

            // If large jump, don't smooth (teleport/crossing)
            if (deltaLength > MaxVelocityJumpThreshold * deltaTime)
            {
                context.StabilityCounter = 0;
                return rawPosition;
            }

            // Apply smoothing based on stability
            float smoothingFactor = CalculateAdaptiveSmoothingFactor(context, deltaLength, deltaTime);
            OMV.Vector3 smoothedPosition = lastPosition + (positionDelta * smoothingFactor);

            return smoothedPosition;
        }

        /// <summary>
        /// Check if avatar movement is stable
        /// </summary>
        public static bool IsMovementStable(uint localID)
        {
            var context = GetSmoothingContext(localID);
            return context.StabilityCounter > 10;
        }

        #endregion

        #region Internal Methods

        private static void AddMovementSample(AvatarSmoothingContext context, OMV.Vector3 position, 
            OMV.Vector3 velocity, OMV.Vector3 acceleration, float time)
        {
            // Add new sample
            context.MovementHistory.Enqueue(new MovementSample(position, velocity, acceleration, time));

            // Maintain history size
            while (context.MovementHistory.Count > SmoothingHistorySize)
            {
                context.MovementHistory.Dequeue();
            }
        }

        private static OMV.Vector3 ApplyVelocitySmoothing(AvatarSmoothingContext context, OMV.Vector3 rawVelocity, OMV.Vector3 targetVelocity)
        {
            // If velocity is very small, treat as stationary
            if (rawVelocity.LengthSquared() < MinVelocityThreshold * MinVelocityThreshold)
            {
                context.StabilityCounter++;
                return OMV.Vector3.Zero;
            }

            // Calculate velocity difference
            OMV.Vector3 velocityDelta = rawVelocity - context.SmoothedVelocity;
            float deltaLength = velocityDelta.Length();

            // Adaptive smoothing based on velocity change magnitude
            float adaptiveFactor = CalculateAdaptiveVelocitySmoothingFactor(deltaLength);

            // Apply smoothing
            OMV.Vector3 smoothedVelocity = context.SmoothedVelocity + (velocityDelta * adaptiveFactor);

            // Consider target velocity for intentional movement
            if (targetVelocity.LengthSquared() > MinVelocityThreshold * MinVelocityThreshold)
            {
                // Blend with target velocity for responsive movement
                float targetInfluence = 0.3f;
                smoothedVelocity = smoothedVelocity * (1f - targetInfluence) + targetVelocity * targetInfluence;
            }

            // Update stability counter
            if (deltaLength < 0.1f)
                context.StabilityCounter++;
            else
                context.StabilityCounter = Math.Max(0, context.StabilityCounter - 1);

            return smoothedVelocity;
        }

        private static float CalculateAdaptiveVelocitySmoothingFactor(float velocityDeltaMagnitude)
        {
            // More smoothing for small changes, less for large changes
            if (velocityDeltaMagnitude < 0.1f)
                return VelocitySmoothingFactor * 0.5f; // Heavy smoothing
            else if (velocityDeltaMagnitude < 1.0f)
                return VelocitySmoothingFactor; // Normal smoothing
            else
                return Math.Min(1.0f, VelocitySmoothingFactor * 1.5f); // Light smoothing
        }

        private static float CalculateAdaptiveSmoothingFactor(AvatarSmoothingContext context, float deltaLength, float deltaTime)
        {
            // Base smoothing factor
            float baseFactor = PositionSmoothingFactor;

            // Increase smoothing for stable movement
            if (context.StabilityCounter > 5)
            {
                baseFactor *= 0.8f;
            }

            // Decrease smoothing for rapid changes
            float expectedDelta = context.SmoothedVelocity.Length() * deltaTime;
            if (deltaLength > expectedDelta * 2f)
            {
                baseFactor *= 1.2f;
            }

            return Math.Clamp(baseFactor, 0.1f, 1.0f);
        }

        private static float GetCurrentTimeSeconds()
        {
            return (float)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);
        }

        #endregion

        #region Performance Monitoring

        /// <summary>
        /// Get smoothing statistics for monitoring
        /// </summary>
        public static SmoothingStats GetSmoothingStats()
        {
            var stats = new SmoothingStats
            {
                ActiveContexts = smoothingContexts.Count,
                TotalMemoryUsageKB = smoothingContexts.Count * 0.5f // Rough estimate
            };

            int stableAvatars = 0;
            foreach (var context in smoothingContexts.Values)
            {
                if (context.StabilityCounter > 10)
                    stableAvatars++;
            }

            stats.StableAvatars = stableAvatars;
            stats.SmoothingEfficiency = smoothingContexts.Count > 0 ? 
                (float)stableAvatars / smoothingContexts.Count * 100f : 100f;

            return stats;
        }

        /// <summary>
        /// Cleanup old smoothing contexts
        /// </summary>
        public static void CleanupOldContexts(float maxAge = 300f)
        {
            float currentTime = GetCurrentTimeSeconds();
            var toRemove = new List<uint>();

            foreach (var kvp in smoothingContexts)
            {
                if (currentTime - kvp.Value.LastUpdateTime > maxAge)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (uint id in toRemove)
            {
                smoothingContexts.Remove(id);
            }

            if (toRemove.Count > 0)
            {
                m_log.DebugFormat("{0}: Cleaned up {1} old smoothing contexts", LogHeader, toRemove.Count);
            }
        }

        #endregion

        #region Statistics Structure

        /// <summary>
        /// Avatar movement smoothing statistics
        /// </summary>
        public struct SmoothingStats
        {
            public int ActiveContexts;
            public int StableAvatars;
            public float SmoothingEfficiency;
            public float TotalMemoryUsageKB;
        }

        #endregion
    }
}