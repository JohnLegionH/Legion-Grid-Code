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
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Types of fluid media
    /// </summary>
    public enum FluidType
    {
        Water = 0,
        Air = 1,
        Gas = 2,
        Viscous = 3,    // Thick liquids like mud, oil
        Plasma = 4      // High-energy fluid state
    }

    /// <summary>
    /// Fluid simulation quality levels
    /// </summary>
    public enum FluidQuality
    {
        Minimal = 1,    // Basic buoyancy only
        Low = 2,        // Simple flow simulation
        Medium = 3,     // Moderate accuracy with viscosity
        High = 4,       // High accuracy with turbulence
        Ultra = 5       // Maximum accuracy with all effects
    }

    /// <summary>
    /// Fluid properties for different media types
    /// </summary>
    public class FluidProperties
    {
        public FluidType Type { get; set; }
        public float Density { get; set; } = 1000.0f;        // kg/m³ (water = 1000)
        public float Viscosity { get; set; } = 0.001f;       // Pa·s (water = 0.001)
        public float Temperature { get; set; } = 293.15f;    // Kelvin (20°C)
        public float Pressure { get; set; } = 101325.0f;     // Pascal (1 atm)
        public OMV.Vector3 FlowVelocity { get; set; } = OMV.Vector3.Zero;
        public float Turbulence { get; set; } = 0.0f;        // Turbulence intensity (0-1)
        public float SurfaceTension { get; set; } = 0.0728f;  // N/m (water = 0.0728)
        public bool SupportsWaves { get; set; } = true;
        public bool SupportsEvaporation { get; set; } = false;
        public OMV.Vector3 GravityModifier { get; set; } = OMV.Vector3.UnitZ; // Directional gravity

        public static FluidProperties Water => new FluidProperties
        {
            Type = FluidType.Water,
            Density = 1000.0f,
            Viscosity = 0.001f,
            SurfaceTension = 0.0728f,
            SupportsWaves = true
        };

        public static FluidProperties Air => new FluidProperties
        {
            Type = FluidType.Air,
            Density = 1.225f,
            Viscosity = 0.0000181f,
            SurfaceTension = 0.0f,
            SupportsWaves = false
        };

        public static FluidProperties Oil => new FluidProperties
        {
            Type = FluidType.Viscous,
            Density = 900.0f,
            Viscosity = 0.1f,
            SurfaceTension = 0.035f,
            SupportsWaves = true
        };

        public static FluidProperties GetPreset(FluidType type)
        {
            return type switch
            {
                FluidType.Water => Water,
                FluidType.Air => Air,
                FluidType.Viscous => Oil,
                _ => Water,
            };
        }
    }

    /// <summary>
    /// Fluid volume definition with spatial bounds
    /// </summary>
    public class FluidVolume
    {
        public uint VolumeID { get; set; }
        public string Name { get; set; }
        public FluidProperties Properties { get; set; }
        public OMV.Vector3 MinBounds { get; set; }
        public OMV.Vector3 MaxBounds { get; set; }
        public float Height { get; set; }
        public bool IsActive { get; set; } = true;
        public bool HasCurrent { get; set; } = false;
        public OMV.Vector3 CurrentDirection { get; set; } = OMV.Vector3.Zero;
        public float CurrentStrength { get; set; } = 0.0f;
        
        // Wave properties
        public bool HasWaves { get; set; } = false;
        public float WaveAmplitude { get; set; } = 0.5f;
        public float WaveFrequency { get; set; } = 0.5f;
        public float WavePhase { get; set; } = 0.0f;
        public OMV.Vector3 WaveDirection { get; set; } = OMV.Vector3.UnitX;
        
        // Environmental effects
        public float WindInfluence { get; set; } = 0.1f;
        public bool AffectedByWeather { get; set; } = true;
        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
        
        // Visual integration
        public OMV.UUID SceneObjectUUID { get; set; } = OMV.UUID.Zero;

        public OMV.Vector3 Center => SIMDPhysicsMath.Add(MinBounds, SIMDPhysicsMath.Multiply(SIMDPhysicsMath.Subtract(MaxBounds, MinBounds), 0.5f));
        public OMV.Vector3 Size => SIMDPhysicsMath.Subtract(MaxBounds, MinBounds);
        public float Volume => Size.X * Size.Y * Size.Z;

        public bool ContainsPoint(OMV.Vector3 point)
        {
            return point.X >= MinBounds.X && point.X <= MaxBounds.X &&
                   point.Y >= MinBounds.Y && point.Y <= MaxBounds.Y &&
                   point.Z >= MinBounds.Z && point.Z <= MaxBounds.Z + Height;
        }

        public float GetDepthAtPoint(OMV.Vector3 point)
        {
            if (!ContainsPoint(point))
                return 0.0f;

            float surfaceHeight = MaxBounds.Z + Height;
            if (HasWaves)
            {
                // Calculate wave displacement
                float time = (float)(DateTime.UtcNow - LastUpdate).TotalSeconds;
                float waveX = point.X * WaveDirection.X + point.Y * WaveDirection.Y;
                float waveOffset = WaveAmplitude * MathF.Sin(waveX * WaveFrequency + WavePhase + time);
                surfaceHeight += waveOffset;
            }

            return Math.Max(0.0f, surfaceHeight - point.Z);
        }
    }

    /// <summary>
    /// Object interaction with fluid
    /// </summary>
    public class FluidInteraction
    {
        public uint ObjectID { get; set; }
        public uint FluidVolumeID { get; set; }
        public float SubmergedVolume { get; set; }
        public float SubmergedFraction { get; set; }
        public OMV.Vector3 BuoyancyForce { get; set; }
        public OMV.Vector3 DragForce { get; set; }
        public OMV.Vector3 FlowForce { get; set; }
        public OMV.Vector3 ContactPoint { get; set; }
        public OMV.Vector3 ContactNormal { get; set; }
        public float RelativeVelocity { get; set; }
        public DateTime InteractionStart { get; set; }
        public bool IsFullySubmerged { get; set; }
        public bool WasFullySubmerged { get; set; }
        
        // Performance tracking
        public DateTime LastUpdate { get; set; }
        public int UpdateCount { get; set; }

        public FluidInteraction(uint objectId, uint fluidVolumeId)
        {
            ObjectID = objectId;
            FluidVolumeID = fluidVolumeId;
            InteractionStart = DateTime.UtcNow;
            LastUpdate = DateTime.UtcNow;
        }

        public TimeSpan InteractionDuration => DateTime.UtcNow - InteractionStart;
    }

    /// <summary>
    /// Advanced fluid dynamics simulation system
    /// </summary>
    public class FluidDynamicsSystem : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[FLUID DYNAMICS]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly ConcurrentDictionary<uint, FluidVolume> m_fluidVolumes;
        private readonly ConcurrentDictionary<uint, FluidInteraction> m_objectInteractions;
        private readonly Timer m_simulationTimer;
        private readonly object m_simulationLock;
        private bool m_disposed;
        private bool m_enabled;

        // Configuration
        private FluidQuality m_quality = FluidQuality.Medium;
        private readonly TimeSpan m_simulationInterval = TimeSpan.FromMilliseconds(50); // 20 Hz
        private readonly float m_gravityConstant = 9.81f;
        private readonly int m_maxInteractionsPerFrame = 100;

        // Performance tracking
        private long m_simulationSteps;
        private long m_fluidInteractions;
        private long m_buoyancyCalculations;
        private long m_dragCalculations;
        private float m_averageSimulationTime;
        private DateTime m_lastPerformanceReport;
        private readonly TimeSpan PerformanceReportInterval = TimeSpan.FromMinutes(2);

        // Simulation parameters
        private readonly Dictionary<FluidQuality, float> m_qualityMultipliers;
        private float m_currentPerformanceLoad = 0.5f;

        // Environmental factors
        private OMV.Vector3 m_globalWindVelocity = OMV.Vector3.Zero;
        private float m_globalWindTurbulence = 0.0f;
        private float m_globalTemperature = 293.15f; // 20°C
        private float m_globalPressure = 101325.0f;  // 1 atm

        #endregion

        #region Constructor

        public FluidDynamicsSystem(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_fluidVolumes = new ConcurrentDictionary<uint, FluidVolume>();
            m_objectInteractions = new ConcurrentDictionary<uint, FluidInteraction>();
            m_simulationLock = new object();

            // Initialize quality multipliers for performance scaling
            m_qualityMultipliers = new Dictionary<FluidQuality, float>
            {
                { FluidQuality.Minimal, 0.2f },
                { FluidQuality.Low, 0.4f },
                { FluidQuality.Medium, 1.0f },
                { FluidQuality.High, 2.0f },
                { FluidQuality.Ultra, 4.0f }
            };

            m_lastPerformanceReport = DateTime.UtcNow;

            // Setup simulation timer
            m_simulationTimer = new Timer(PerformSimulationStep, null, m_simulationInterval, m_simulationInterval);

            m_enabled = true;

            m_log.InfoFormat("{0}: Fluid dynamics system initialized - Quality: {1}, Simulation rate: {2} Hz", 
                LogHeader, m_quality, 1.0f / m_simulationInterval.TotalSeconds);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the fluid dynamics system
        /// </summary>
        public void Initialize()
        {
            if (m_disposed)
                return;

            m_enabled = true;
            m_log.InfoFormat("{0}: Fluid dynamics system started", LogHeader);
        }

        /// <summary>
        /// Create a new fluid volume
        /// </summary>
        public uint CreateFluidVolume(string name, FluidProperties properties, OMV.Vector3 minBounds, OMV.Vector3 maxBounds, float height = 0.0f)
        {
            if (!m_enabled || m_disposed)
                return 0;

            try
            {
                uint volumeID = (uint)m_fluidVolumes.Count + 1;
                
                var fluidVolume = new FluidVolume
                {
                    VolumeID = volumeID,
                    Name = name,
                    Properties = properties,
                    MinBounds = minBounds,
                    MaxBounds = maxBounds,
                    Height = height
                };

                m_fluidVolumes.TryAdd(volumeID, fluidVolume);

                // Create visual representation
                CreateFluidVolumeVisual(fluidVolume);

                m_log.InfoFormat("{0}: Created fluid volume '{1}' (ID: {2}, Type: {3}, Volume: {4:F2} m³)", 
                    LogHeader, name, volumeID, properties.Type, fluidVolume.Volume);

                return volumeID;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating fluid volume: {1}", LogHeader, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Remove a fluid volume
        /// </summary>
        public void RemoveFluidVolume(uint volumeID)
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_fluidVolumes.TryRemove(volumeID, out FluidVolume removedVolume))
            {
                // Remove all interactions with this volume
                var interactionsToRemove = m_objectInteractions.Values
                    .Where(interaction => interaction.FluidVolumeID == volumeID)
                    .Select(interaction => interaction.ObjectID)
                    .ToList();

                foreach (var objectID in interactionsToRemove)
                {
                    m_objectInteractions.TryRemove(objectID, out _);
                }

                m_log.InfoFormat("{0}: Removed fluid volume '{1}' (ID: {2})", LogHeader, removedVolume.Name, volumeID);
            }
        }

        /// <summary>
        /// Update object position for fluid interaction calculation
        /// </summary>
        public void UpdateObjectPosition(uint objectID, OMV.Vector3 position, OMV.Vector3 velocity, OMV.Vector3 size, float mass, bool hasPhysics = true)
        {
            if (!m_enabled || m_disposed || !hasPhysics)
                return;

            try
            {
                // Check for fluid volume interactions
                foreach (var fluidVolume in m_fluidVolumes.Values)
                {
                    if (!fluidVolume.IsActive)
                        continue;

                    var isInFluid = IsObjectInFluid(position, size, fluidVolume);
                    var hasExistingInteraction = m_objectInteractions.ContainsKey(objectID);

                    if (isInFluid && !hasExistingInteraction)
                    {
                        // Object entered fluid
                        var interaction = new FluidInteraction(objectID, fluidVolume.VolumeID);
                        m_objectInteractions.TryAdd(objectID, interaction);
                        m_log.DebugFormat("{0}: Object {1} entered fluid volume {2}", LogHeader, objectID, fluidVolume.VolumeID);
                    }
                    else if (!isInFluid && hasExistingInteraction)
                    {
                        // Object left fluid
                        var interaction = m_objectInteractions[objectID];
                        if (interaction.FluidVolumeID == fluidVolume.VolumeID)
                        {
                            m_objectInteractions.TryRemove(objectID, out _);
                            m_log.DebugFormat("{0}: Object {1} left fluid volume {2}", LogHeader, objectID, fluidVolume.VolumeID);
                        }
                    }
                }

                // Update existing interactions
                if (m_objectInteractions.TryGetValue(objectID, out FluidInteraction existingInteraction))
                {
                    if (m_fluidVolumes.TryGetValue(existingInteraction.FluidVolumeID, out FluidVolume volume))
                    {
                        CalculateFluidInteraction(existingInteraction, position, velocity, size, mass, volume);
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error updating object position for fluid interaction: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Get fluid forces acting on an object
        /// </summary>
        public (OMV.Vector3 force, OMV.Vector3 torque) GetFluidForces(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return (OMV.Vector3.Zero, OMV.Vector3.Zero);

            if (m_objectInteractions.TryGetValue(objectID, out FluidInteraction interaction))
            {
                var totalForce = SIMDPhysicsMath.Add(
                    SIMDPhysicsMath.Add(interaction.BuoyancyForce, interaction.DragForce),
                    interaction.FlowForce);

                // Calculate torque from fluid forces (simplified)
                var torque = SIMDPhysicsMath.Cross(interaction.ContactPoint, totalForce);
                torque = SIMDPhysicsMath.Multiply(torque, 0.1f); // Scale down torque

                return (totalForce, torque);
            }

            return (OMV.Vector3.Zero, OMV.Vector3.Zero);
        }

        /// <summary>
        /// Set global wind conditions
        /// </summary>
        public void SetWindConditions(OMV.Vector3 windVelocity, float turbulence = 0.0f)
        {
            m_globalWindVelocity = windVelocity;
            m_globalWindTurbulence = Math.Max(0.0f, Math.Min(1.0f, turbulence));
            
            // Update air fluid volumes with wind
            foreach (var volume in m_fluidVolumes.Values.Where(v => v.Properties.Type == FluidType.Air))
            {
                volume.Properties.FlowVelocity = windVelocity;
                volume.Properties.Turbulence = turbulence;
            }
        }

        /// <summary>
        /// Set fluid quality level
        /// </summary>
        public void SetQuality(FluidQuality quality)
        {
            m_quality = quality;
            m_log.InfoFormat("{0}: Fluid simulation quality set to {1}", LogHeader, quality);
        }

        /// <summary>
        /// Add waves to a fluid volume
        /// </summary>
        public void SetWaveProperties(uint volumeID, float amplitude, float frequency, OMV.Vector3 direction)
        {
            if (m_fluidVolumes.TryGetValue(volumeID, out FluidVolume volume))
            {
                volume.HasWaves = true;
                volume.WaveAmplitude = amplitude;
                volume.WaveFrequency = frequency;
                volume.WaveDirection = SIMDPhysicsMath.Normalize(direction);
                volume.WavePhase = 0.0f;
            }
        }

        /// <summary>
        /// Add current to a fluid volume
        /// </summary>
        public void SetCurrentProperties(uint volumeID, OMV.Vector3 direction, float strength)
        {
            if (m_fluidVolumes.TryGetValue(volumeID, out FluidVolume volume))
            {
                volume.HasCurrent = true;
                volume.CurrentDirection = SIMDPhysicsMath.Normalize(direction);
                volume.CurrentStrength = strength;
            }
        }

        /// <summary>
        /// Get water height at a specific point
        /// </summary>
        public float GetFluidHeightAtPoint(OMV.Vector3 point, FluidType fluidType = FluidType.Water)
        {
            if (!m_enabled || m_disposed)
                return 0.0f;

            float maxHeight = 0.0f;

            foreach (var volume in m_fluidVolumes.Values)
            {
                if (volume.Properties.Type == fluidType && volume.IsActive)
                {
                    var depth = volume.GetDepthAtPoint(point);
                    if (depth > 0.0f)
                    {
                        var fluidHeight = point.Z + depth;
                        maxHeight = Math.Max(maxHeight, fluidHeight);
                    }
                }
            }

            return maxHeight;
        }

        /// <summary>
        /// Remove object from fluid tracking
        /// </summary>
        public void RemoveObject(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return;

            m_objectInteractions.TryRemove(objectID, out _);
        }

        /// <summary>
        /// Get comprehensive fluid dynamics performance report
        /// </summary>
        public string GetPerformanceReport()
        {
            if (!m_enabled || m_disposed)
                return "Fluid dynamics system not available";

            var report = $"Fluid Dynamics System Performance:\\n";
            report += $"  Fluid Volumes: {m_fluidVolumes.Count}\\n";
            report += $"  Active Interactions: {m_objectInteractions.Count}\\n";
            report += $"  Simulation Quality: {m_quality}\\n";
            report += $"  Simulation Steps: {m_simulationSteps}\\n";
            report += $"  Fluid Interactions: {m_fluidInteractions}\\n";
            report += $"  Buoyancy Calculations: {m_buoyancyCalculations}\\n";
            report += $"  Drag Calculations: {m_dragCalculations}\\n";
            report += $"  Average Simulation Time: {m_averageSimulationTime:F2}ms\\n";
            report += $"  Global Wind: {m_globalWindVelocity.Length():F2} m/s\\n";

            // Fluid type distribution
            var fluidTypes = m_fluidVolumes.Values
                .GroupBy(v => v.Properties.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            report += $"  Fluid Type Distribution:\\n";
            foreach (var type in Enum.GetValues<FluidType>())
            {
                var count = fluidTypes.GetValueOrDefault(type, 0);
                if (count > 0)
                    report += $"    {type}: {count}\\n";
            }

            return report;
        }

        #endregion

        #region Private Methods

        private void PerformSimulationStep(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            var startTime = DateTime.UtcNow;

            try
            {
                lock (m_simulationLock)
                {
                    // Update fluid volumes
                    UpdateFluidVolumes();

                    // Process fluid interactions
                    ProcessFluidInteractions();

                    m_simulationSteps++;

                    var simulationTime = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
                    m_averageSimulationTime = m_averageSimulationTime * 0.9f + simulationTime * 0.1f;

                    // Performance reporting
                    if (DateTime.UtcNow - m_lastPerformanceReport >= PerformanceReportInterval)
                    {
                        m_log.InfoFormat("{0}: {1}", LogHeader, GetPerformanceReport().Replace("\\n", " "));
                        m_lastPerformanceReport = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during fluid simulation step: {1}", LogHeader, ex.Message);
            }
        }

        private void UpdateFluidVolumes()
        {
            var currentTime = DateTime.UtcNow;

            foreach (var volume in m_fluidVolumes.Values)
            {
                if (!volume.IsActive)
                    continue;

                var deltaTime = (float)(currentTime - volume.LastUpdate).TotalSeconds;
                volume.LastUpdate = currentTime;

                // Update wave phase
                if (volume.HasWaves)
                {
                    volume.WavePhase += volume.WaveFrequency * deltaTime * 2.0f * MathF.PI;
                    if (volume.WavePhase > 2.0f * MathF.PI)
                        volume.WavePhase -= 2.0f * MathF.PI;
                }

                // Update flow properties based on wind
                if (volume.Properties.Type == FluidType.Air || volume.AffectedByWeather)
                {
                    var windEffect = SIMDPhysicsMath.Multiply(m_globalWindVelocity, volume.WindInfluence);
                    volume.Properties.FlowVelocity = SIMDPhysicsMath.Add(volume.Properties.FlowVelocity, windEffect);
                    volume.Properties.Turbulence = Math.Max(volume.Properties.Turbulence, m_globalWindTurbulence * volume.WindInfluence);
                }
            }
        }

        private void ProcessFluidInteractions()
        {
            var interactionsProcessed = 0;
            
            foreach (var interaction in m_objectInteractions.Values)
            {
                if (interactionsProcessed >= m_maxInteractionsPerFrame)
                    break;

                if (m_fluidVolumes.TryGetValue(interaction.FluidVolumeID, out FluidVolume volume))
                {
                    // Apply quality-based processing frequency
                    var qualityMultiplier = m_qualityMultipliers[m_quality];
                    if (ShouldProcessInteraction(interaction, qualityMultiplier))
                    {
                        ApplyFluidForces(interaction, volume);
                        interactionsProcessed++;
                    }
                }
            }

            m_fluidInteractions += interactionsProcessed;
        }

        private bool ShouldProcessInteraction(FluidInteraction interaction, float qualityMultiplier)
        {
            // Process based on quality and performance load
            var processingChance = qualityMultiplier * (1.0f - m_currentPerformanceLoad * 0.5f);
            return DateTime.UtcNow.Ticks % 100 < processingChance * 100;
        }

        private bool IsObjectInFluid(OMV.Vector3 position, OMV.Vector3 size, FluidVolume fluidVolume)
        {
            // Check if object bounding box intersects with fluid volume
            var objectMin = SIMDPhysicsMath.Subtract(position, SIMDPhysicsMath.Multiply(size, 0.5f));
            var objectMax = SIMDPhysicsMath.Add(position, SIMDPhysicsMath.Multiply(size, 0.5f));

            var fluidMax = new OMV.Vector3(fluidVolume.MaxBounds.X, fluidVolume.MaxBounds.Y, fluidVolume.MaxBounds.Z + fluidVolume.Height);

            return objectMax.X >= fluidVolume.MinBounds.X && objectMin.X <= fluidMax.X &&
                   objectMax.Y >= fluidVolume.MinBounds.Y && objectMin.Y <= fluidMax.Y &&
                   objectMax.Z >= fluidVolume.MinBounds.Z && objectMin.Z <= fluidMax.Z;
        }

        private void CalculateFluidInteraction(FluidInteraction interaction, OMV.Vector3 position, OMV.Vector3 velocity, OMV.Vector3 size, float mass, FluidVolume volume)
        {
            // Calculate submerged volume
            var submergedVolume = CalculateSubmergedVolume(position, size, volume);
            var objectVolume = size.X * size.Y * size.Z;
            
            interaction.SubmergedVolume = submergedVolume;
            interaction.SubmergedFraction = objectVolume > 0 ? submergedVolume / objectVolume : 0.0f;
            interaction.IsFullySubmerged = interaction.SubmergedFraction >= 0.99f;
            
            // Calculate contact point (center of submerged volume)
            interaction.ContactPoint = CalculateSubmergedCenter(position, size, volume);
            
            // Calculate relative velocity
            var fluidVelocity = GetFluidVelocityAtPoint(interaction.ContactPoint, volume);
            var relativeVelocity = SIMDPhysicsMath.Subtract(velocity, fluidVelocity);
            interaction.RelativeVelocity = SIMDPhysicsMath.Length(relativeVelocity);

            interaction.LastUpdate = DateTime.UtcNow;
            interaction.UpdateCount++;
        }

        private void ApplyFluidForces(FluidInteraction interaction, FluidVolume volume)
        {
            if (interaction.SubmergedVolume <= 0.001f)
                return;

            // Calculate buoyancy force
            interaction.BuoyancyForce = CalculateBuoyancyForce(interaction, volume);
            m_buoyancyCalculations++;

            // Calculate drag force
            interaction.DragForce = CalculateDragForce(interaction, volume);
            m_dragCalculations++;

            // Calculate flow force (current effects)
            interaction.FlowForce = CalculateFlowForce(interaction, volume);
        }

        private OMV.Vector3 CalculateBuoyancyForce(FluidInteraction interaction, FluidVolume volume)
        {
            // Archimedes' principle: F_buoyancy = ρ_fluid * V_submerged * g
            var buoyancyMagnitude = volume.Properties.Density * interaction.SubmergedVolume * m_gravityConstant;
            var buoyancyDirection = SIMDPhysicsMath.Multiply(volume.Properties.GravityModifier, -1.0f); // Opposite to gravity
            return SIMDPhysicsMath.Multiply(SIMDPhysicsMath.Normalize(buoyancyDirection), buoyancyMagnitude);
        }

        private OMV.Vector3 CalculateDragForce(FluidInteraction interaction, FluidVolume volume)
        {
            if (interaction.RelativeVelocity < 0.01f)
                return OMV.Vector3.Zero;

            // Drag force: F_drag = 0.5 * ρ * v² * C_d * A
            var dragCoefficient = 0.47f; // Sphere approximation
            var crossSectionalArea = interaction.SubmergedVolume * 0.1f; // Approximation
            
            var dragMagnitude = 0.5f * volume.Properties.Density * interaction.RelativeVelocity * interaction.RelativeVelocity * 
                               dragCoefficient * crossSectionalArea;

            // Apply viscosity effect
            var viscosityEffect = volume.Properties.Viscosity * 1000.0f; // Scale viscosity
            dragMagnitude *= (1.0f + viscosityEffect);

            // Drag opposes relative motion
            var relativeVelocityDirection = SIMDPhysicsMath.Normalize(SIMDPhysicsMath.Subtract(OMV.Vector3.Zero, interaction.ContactPoint));
            return SIMDPhysicsMath.Multiply(relativeVelocityDirection, dragMagnitude);
        }

        private OMV.Vector3 CalculateFlowForce(FluidInteraction interaction, FluidVolume volume)
        {
            if (!volume.HasCurrent)
                return OMV.Vector3.Zero;

            // Flow force based on current strength and direction
            var flowMagnitude = volume.CurrentStrength * interaction.SubmergedFraction * volume.Properties.Density * 0.1f;
            return SIMDPhysicsMath.Multiply(volume.CurrentDirection, flowMagnitude);
        }

        private float CalculateSubmergedVolume(OMV.Vector3 position, OMV.Vector3 size, FluidVolume volume)
        {
            // Simplified calculation - assume rectangular object
            var objectMin = SIMDPhysicsMath.Subtract(position, SIMDPhysicsMath.Multiply(size, 0.5f));
            var objectMax = SIMDPhysicsMath.Add(position, SIMDPhysicsMath.Multiply(size, 0.5f));

            var fluidSurfaceHeight = volume.MaxBounds.Z + volume.Height;
            
            if (volume.HasWaves)
            {
                // Account for wave displacement at object center
                var depth = volume.GetDepthAtPoint(position);
                fluidSurfaceHeight = position.Z + depth;
            }

            // Calculate intersection
            var submergedTop = Math.Min(objectMax.Z, fluidSurfaceHeight);
            var submergedBottom = Math.Max(objectMin.Z, volume.MinBounds.Z);
            var submergedHeight = Math.Max(0.0f, submergedTop - submergedBottom);

            return size.X * size.Y * submergedHeight;
        }

        private OMV.Vector3 CalculateSubmergedCenter(OMV.Vector3 position, OMV.Vector3 size, FluidVolume volume)
        {
            // Simplified - return object center adjusted for submersion
            var depth = volume.GetDepthAtPoint(position);
            if (depth > size.Z * 0.5f)
            {
                return position; // Fully submerged
            }
            else
            {
                // Partially submerged - adjust center downward
                var adjustment = new OMV.Vector3(0, 0, -depth * 0.25f);
                return SIMDPhysicsMath.Add(position, adjustment);
            }
        }

        private OMV.Vector3 GetFluidVelocityAtPoint(OMV.Vector3 point, FluidVolume volume)
        {
            var velocity = volume.Properties.FlowVelocity;

            // Add current effects
            if (volume.HasCurrent)
            {
                var currentVelocity = SIMDPhysicsMath.Multiply(volume.CurrentDirection, volume.CurrentStrength);
                velocity = SIMDPhysicsMath.Add(velocity, currentVelocity);
            }

            // Add turbulence (simplified)
            if (volume.Properties.Turbulence > 0.0f)
            {
                var turbulenceOffset = new OMV.Vector3(
                    (float)(Math.Sin(point.X * 0.1f) * volume.Properties.Turbulence),
                    (float)(Math.Cos(point.Y * 0.1f) * volume.Properties.Turbulence),
                    0.0f
                );
                velocity = SIMDPhysicsMath.Add(velocity, turbulenceOffset);
            }

            return velocity;
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (m_disposed)
                return;

            try
            {
                m_enabled = false;
                m_simulationTimer?.Dispose();
                m_fluidVolumes.Clear();
                m_objectInteractions.Clear();

                m_disposed = true;
                m_log.InfoFormat("{0}: Fluid dynamics system disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public int FluidVolumeCount => m_fluidVolumes.Count;
        public int ActiveInteractionCount => m_objectInteractions.Count;
        public FluidQuality Quality => m_quality;
        public long SimulationSteps => m_simulationSteps;
        public float AverageSimulationTime => m_averageSimulationTime;
        public OMV.Vector3 GlobalWindVelocity => m_globalWindVelocity;

        #endregion

        #region Visual Integration

        /// <summary>
        /// Create a visual representation of a fluid volume in the scene
        /// </summary>
        private void CreateFluidVolumeVisual(FluidVolume fluidVolume)
        {
            try
            {
                var color = GetFluidColor(fluidVolume.Properties.Type);
                
                // Log the visual creation for manual marker reference
                m_log.InfoFormat("{0}: VISUAL: Created fluid volume '{1}' - Type: {2}, Center: {3}, Size: {4}, Color: {5}", 
                    LogHeader, fluidVolume.Name, fluidVolume.Properties.Type, 
                    fluidVolume.Center, fluidVolume.Size, color);

                // Create actual scene object if Scene access is available
                if (m_scene?.OSScene != null)
                {
                    CreateAutomaticFluidSceneObject(fluidVolume, color);
                }
                else
                {
                    m_log.WarnFormat("{0}: Scene access not available for automatic object creation", LogHeader);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating fluid volume visual: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Create an automatic scene object for a fluid volume
        /// </summary>
        private void CreateAutomaticFluidSceneObject(FluidVolume fluidVolume, OMV.Color4 color)
        {
            try
            {
                var osScene = m_scene.OSScene;
                
                // Get region owner UUID for proper object ownership
                var regionOwner = osScene.RegionInfo.EstateSettings.EstateOwner;
                if (regionOwner == OMV.UUID.Zero)
                {
                    // Fallback to first admin if no estate owner
                    var admins = osScene.RegionInfo.EstateSettings.EstateManagers;
                    regionOwner = admins.Length > 0 ? admins[0] : OMV.UUID.Zero;
                }
                
                m_log.DebugFormat("{0}: Creating fluid object with owner {1}", LogHeader, regionOwner);
                
                // Create a SceneObjectPart for the fluid volume
                var part = new SceneObjectPart(
                    regionOwner, // Owner - region owner for proper interaction
                    PrimitiveBaseShape.CreateBox(), // Box shape for fluid volume
                    fluidVolume.Center, // Position
                    OMV.Quaternion.Identity, // Rotation
                    fluidVolume.Size // Scale
                )
                {
                    Name = $"FluidVolume_{fluidVolume.Name}",
                    Description = $"Advanced Physics Fluid Volume: {fluidVolume.Properties.Type}",
                    UUID = OMV.UUID.Random()
                };

                // Set visual properties based on fluid type
                part.Shape.PCode = (byte)PCode.Prim;
                part.Shape.PathCurve = (byte)Extrusion.Straight;
                part.Shape.ProfileCurve = (byte)Extrusion.Straight;
                part.Shape.PathTaperX = part.Shape.PathTaperY = 0;
                part.Shape.PathRevolutions = 1;
                part.Shape.PathRadiusOffset = part.Shape.PathSkew = 0;
                part.Scale = fluidVolume.Size;

                // Set color and transparency through texture entry with solid texture
                var textureEntry = part.Shape.Textures;
                // Use blank/solid texture UUID instead of default wood texture
                textureEntry.DefaultTexture.TextureID = new OMV.UUID("f54a0c32-3cd1-d49a-5b4f-7b792bebc204"); // Blank texture
                textureEntry.DefaultTexture.RGBA = color;
                part.Shape.TextureEntry = textureEntry.GetBytes();
                
                // Also set the legacy color property
                part.Color = System.Drawing.Color.FromArgb((int)(color.A * 255), (int)(color.R * 255), (int)(color.G * 255), (int)(color.B * 255));
                
                // Set shape properties
                part.Shape.FlexiEntry = false;
                part.Shape.LightEntry = false;
                part.Shape.SculptEntry = false;
                
                // Set appropriate material type for the fluid volume
                switch (fluidVolume.Properties.Type)
                {
                    case FluidType.Water:
                        part.Material = (byte)OMV.Material.Glass; // Water volumes use glass for visual clarity
                        break;
                    case FluidType.Viscous:
                        part.Material = (byte)OMV.Material.Rubber; // Viscous fluids (oil, mud) use rubber material
                        break;
                    case FluidType.Gas:
                    case FluidType.Air:
                        part.Material = (byte)OMV.Material.Plastic; // Gas/air use plastic material
                        break;
                    case FluidType.Plasma:
                        part.Material = (byte)OMV.Material.Light; // Plasma uses light material
                        break;
                    default:
                        part.Material = (byte)OMV.Material.Glass; // Default to glass for transparency
                        break;
                }

                // Create SceneObjectGroup
                var sog = new SceneObjectGroup(part);
                
                // Set appropriate flags for phantom objects (fluids are non-collidable)
                part.Flags |= PrimFlags.Phantom;

                // Add to scene with persistence enabled
                m_log.DebugFormat("{0}: Adding fluid scene object to scene with persistence...", LogHeader);
                var success = osScene.AddNewSceneObject(sog, true); // true = persist to database
                
                if (success)
                {
                    // Store the scene object reference in the fluid volume
                    fluidVolume.SceneObjectUUID = sog.UUID;
                    
                    m_log.InfoFormat("{0}: AUTO-CREATED: Scene object for fluid volume '{1}' at {2} (UUID: {3}, Owner: {4})", 
                        LogHeader, fluidVolume.Name, fluidVolume.Center, sog.UUID, regionOwner);
                }
                else
                {
                    m_log.ErrorFormat("{0}: Failed to add fluid volume scene object to scene", LogHeader);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating automatic fluid scene object: {1}", LogHeader, ex.Message);
                m_log.ErrorFormat("{0}: Stack trace: {1}", LogHeader, ex.StackTrace);
            }
        }

        /// <summary>
        /// Get display color for different fluid types
        /// </summary>
        private OpenMetaverse.Color4 GetFluidColor(FluidType fluidType)
        {
            return fluidType switch
            {
                FluidType.Water => new OpenMetaverse.Color4(0.2f, 0.6f, 1.0f, 0.4f), // Blue, semi-transparent
                FluidType.Viscous => new OpenMetaverse.Color4(0.2f, 0.1f, 0.0f, 0.6f),   // Dark brown, more opaque  
                FluidType.Air => new OpenMetaverse.Color4(0.9f, 0.9f, 1.0f, 0.1f),   // Light blue, very transparent
                _ => new OpenMetaverse.Color4(0.5f, 0.5f, 0.5f, 0.3f) // Gray default
            };
        }

        #endregion
    }
}