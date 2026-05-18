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

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Types of particle systems with different physics behaviors
    /// </summary>
    public enum ParticleSystemType
    {
        Fire = 0,           // Heat-based particles with convection
        Smoke = 1,          // Gas particles affected by wind
        Water = 2,          // Liquid particles with surface tension
        Snow = 3,           // Crystalline particles with wind effects
        Sparks = 4,         // Conductive particles with electrical effects
        Dust = 5,           // Fine particles with air resistance
        Magic = 6,          // Fantasy particles with custom physics
        Debris = 7,         // Solid fragments with realistic physics
        Liquid = 8,         // Fluid simulation particles
        Gas = 9,            // Gaseous particles with expansion
        Plasma = 10,        // High-energy ionized particles
        Energy = 11         // Pure energy effects
    }

    /// <summary>
    /// Particle behavior modes
    /// </summary>
    public enum ParticleBehavior
    {
        Static = 0,         // No physics simulation
        Kinematic = 1,      // Position-based movement only
        Dynamic = 2,        // Full physics simulation
        Hybrid = 3,         // Selective physics based on conditions
        Instanced = 4       // GPU-based instanced rendering
    }

    /// <summary>
    /// Individual particle with full physics properties
    /// </summary>
    public class PhysicsParticle
    {
        public uint ID { get; set; }
        public OMV.Vector3 Position { get; set; }
        public OMV.Vector3 PreviousPosition { get; set; }
        public OMV.Vector3 Velocity { get; set; }
        public OMV.Vector3 Acceleration { get; set; }
        public OMV.Vector3 Force { get; set; }
        
        // Physical properties
        public float Mass { get; set; } = 0.001f;
        public float InverseMass { get; set; } = 1000.0f;
        public float Radius { get; set; } = 0.01f;
        public float Density { get; set; } = 1.0f;
        public float Restitution { get; set; } = 0.3f;
        public float Friction { get; set; } = 0.5f;
        public float Drag { get; set; } = 0.1f;
        
        // Particle-specific properties
        public float Temperature { get; set; } = 293.15f;    // Kelvin
        public float Energy { get; set; } = 1.0f;
        public float Charge { get; set; } = 0.0f;            // Electrical charge
        public OMV.Vector3 MagneticField { get; set; } = OMV.Vector3.Zero;
        public float Pressure { get; set; } = 101325.0f;    // Pascal
        
        // Lifecycle properties
        public float Age { get; set; } = 0.0f;
        public float Lifetime { get; set; } = 5.0f;
        public float LifeRemaining => Math.Max(0.0f, Lifetime - Age);
        public bool IsAlive => Age < Lifetime;
        public bool IsActive { get; set; } = true;
        
        // Interaction properties
        public bool HasCollided { get; set; } = false;
        public OMV.Vector3 CollisionNormal { get; set; }
        public float CollisionPenetration { get; set; } = 0.0f;
        public uint CollidedWith { get; set; } = 0;
        public int InteractionCount { get; set; } = 0;
        
        // Visual properties
        public OMV.Vector3 Color { get; set; } = OMV.Vector3.One;
        public float Alpha { get; set; } = 1.0f;
        public float Size { get; set; } = 1.0f;
        public float Rotation { get; set; } = 0.0f;
        public float AngularVelocity { get; set; } = 0.0f;

        public PhysicsParticle(uint id, OMV.Vector3 position)
        {
            ID = id;
            Position = position;
            PreviousPosition = position;
            Velocity = OMV.Vector3.Zero;
            Acceleration = OMV.Vector3.Zero;
            Force = OMV.Vector3.Zero;
        }

        public void ResetForces()
        {
            Force = OMV.Vector3.Zero;
            Acceleration = OMV.Vector3.Zero;
        }

        public void ApplyForce(OMV.Vector3 force)
        {
            Force = SIMDPhysicsMath.Add(Force, force);
        }

        public void UpdatePhysics(float deltaTime, OMV.Vector3 gravity)
        {
            if (!IsActive || !IsAlive)
                return;

            // Add gravity
            ApplyForce(SIMDPhysicsMath.Multiply(gravity, Mass));

            // Apply drag force
            if (Velocity.LengthSquared() > 0.001f)
            {
                var dragForce = SIMDPhysicsMath.Multiply(Velocity, -Drag * Velocity.LengthSquared());
                ApplyForce(dragForce);
            }

            // Calculate acceleration
            Acceleration = SIMDPhysicsMath.Multiply(Force, InverseMass);

            // Verlet integration
            var newPosition = SIMDPhysicsMath.Add(
                SIMDPhysicsMath.Subtract(
                    SIMDPhysicsMath.Add(Position, Position), 
                    PreviousPosition),
                SIMDPhysicsMath.Multiply(Acceleration, deltaTime * deltaTime));

            PreviousPosition = Position;
            Position = newPosition;

            // Update velocity for other calculations
            Velocity = SIMDPhysicsMath.Multiply(SIMDPhysicsMath.Subtract(Position, PreviousPosition), 1.0f / deltaTime);

            // Update age
            Age += deltaTime;

            // Update rotation
            Rotation += AngularVelocity * deltaTime;
        }
    }

    /// <summary>
    /// Particle emitter configuration
    /// </summary>
    public class ParticleEmitter
    {
        public uint EmitterID { get; set; }
        public string Name { get; set; }
        public ParticleSystemType SystemType { get; set; }
        public OMV.Vector3 Position { get; set; }
        public OMV.Vector3 Direction { get; set; } = OMV.Vector3.UnitZ;
        public bool IsActive { get; set; } = true;
        
        // Emission properties
        public float EmissionRate { get; set; } = 10.0f;     // Particles per second
        public float EmissionSpeed { get; set; } = 5.0f;     // Initial velocity magnitude
        public float EmissionSpread { get; set; } = 0.2f;    // Cone angle in radians
        public OMV.Vector3 EmissionArea { get; set; } = OMV.Vector3.Zero; // Area of emission
        
        // Particle properties
        public float ParticleLifetime { get; set; } = 5.0f;
        public float ParticleLifetimeVariation { get; set; } = 1.0f;
        public float ParticleMass { get; set; } = 0.001f;
        public float ParticleMassVariation { get; set; } = 0.0002f;
        public float ParticleSize { get; set; } = 0.1f;
        public float ParticleSizeVariation { get; set; } = 0.05f;
        
        // Physics properties
        public float Temperature { get; set; } = 293.15f;
        public float TemperatureVariation { get; set; } = 50.0f;
        public float InitialEnergy { get; set; } = 1.0f;
        public float Drag { get; set; } = 0.1f;
        public float Restitution { get; set; } = 0.3f;
        
        // Visual properties
        public OMV.Vector3 StartColor { get; set; } = OMV.Vector3.One;
        public OMV.Vector3 EndColor { get; set; } = OMV.Vector3.Zero;
        public float StartAlpha { get; set; } = 1.0f;
        public float EndAlpha { get; set; } = 0.0f;
        
        // Timing
        public float NextEmissionTime { get; set; } = 0.0f;
        public float AccumulatedTime { get; set; } = 0.0f;
        public DateTime LastEmission { get; set; } = DateTime.UtcNow;
        
        // Performance
        public int MaxParticles { get; set; } = 1000;
        public int EmittedParticles { get; set; } = 0;
        public bool BurstMode { get; set; } = false;
        public int BurstCount { get; set; } = 50;

        public ParticleEmitter(uint id, string name, ParticleSystemType type)
        {
            EmitterID = id;
            Name = name;
            SystemType = type;
        }

        public bool ShouldEmit(float deltaTime)
        {
            if (!IsActive || EmittedParticles >= MaxParticles)
                return false;

            AccumulatedTime += deltaTime;
            
            if (BurstMode)
            {
                // Burst emission
                if (AccumulatedTime >= NextEmissionTime)
                {
                    NextEmissionTime = AccumulatedTime + (1.0f / EmissionRate);
                    return true;
                }
            }
            else
            {
                // Continuous emission
                if (AccumulatedTime >= (1.0f / EmissionRate))
                {
                    AccumulatedTime = 0.0f;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Force field affecting particles
    /// </summary>
    public class ParticleForceField
    {
        public uint FieldID { get; set; }
        public string Name { get; set; }
        public ForceFieldType Type { get; set; }
        public OMV.Vector3 Position { get; set; }
        public float Strength { get; set; } = 1.0f;
        public float Range { get; set; } = 10.0f;
        public bool IsActive { get; set; } = true;
        public OMV.Vector3 Direction { get; set; } = OMV.Vector3.UnitZ;
        
        // Field-specific properties
        public float Frequency { get; set; } = 1.0f;        // For oscillating fields
        public float Phase { get; set; } = 0.0f;
        public bool Turbulent { get; set; } = false;
        public float TurbulenceScale { get; set; } = 1.0f;

        public enum ForceFieldType
        {
            Gravity,        // Attractive force towards center
            Repulsion,      // Repulsive force from center
            Directional,    // Constant directional force (wind)
            Vortex,         // Circular force around axis
            Turbulence,     // Random chaotic forces
            Magnetic,       // Magnetic field effects
            Electric,       // Electric field effects
            Thermal,        // Temperature-based convection
            Oscillating     // Sine wave-based forces
        }

        public OMV.Vector3 CalculateForce(PhysicsParticle particle, float deltaTime)
        {
            if (!IsActive)
                return OMV.Vector3.Zero;

            var toParticle = SIMDPhysicsMath.Subtract(particle.Position, Position);
            var distance = SIMDPhysicsMath.Length(toParticle);
            
            if (distance > Range || distance < 0.001f)
                return OMV.Vector3.Zero;

            var normalizedDistance = distance / Range;
            var falloff = 1.0f - normalizedDistance;
            
            switch (Type)
            {
                case ForceFieldType.Gravity:
                    var direction = SIMDPhysicsMath.Normalize(SIMDPhysicsMath.Multiply(toParticle, -1.0f));
                    return SIMDPhysicsMath.Multiply(direction, Strength * falloff * particle.Mass);

                case ForceFieldType.Repulsion:
                    var repulseDirection = SIMDPhysicsMath.Normalize(toParticle);
                    return SIMDPhysicsMath.Multiply(repulseDirection, Strength * falloff * particle.Mass);

                case ForceFieldType.Directional:
                    return SIMDPhysicsMath.Multiply(Direction, Strength * falloff);

                case ForceFieldType.Vortex:
                    var axis = SIMDPhysicsMath.Normalize(Direction);
                    var radial = SIMDPhysicsMath.Subtract(toParticle, SIMDPhysicsMath.Multiply(axis, SIMDPhysicsMath.Dot(toParticle, axis)));
                    var tangent = SIMDPhysicsMath.Cross(axis, radial);
                    return SIMDPhysicsMath.Multiply(SIMDPhysicsMath.Normalize(tangent), Strength * falloff);

                case ForceFieldType.Turbulence:
                    var random = new OMV.Vector3(
                        (float)(Math.Sin(particle.Position.X * TurbulenceScale + Phase) * Math.Cos(particle.Position.Y * TurbulenceScale)),
                        (float)(Math.Cos(particle.Position.Y * TurbulenceScale + Phase) * Math.Sin(particle.Position.Z * TurbulenceScale)),
                        (float)(Math.Sin(particle.Position.Z * TurbulenceScale + Phase) * Math.Cos(particle.Position.X * TurbulenceScale))
                    );
                    return SIMDPhysicsMath.Multiply(random, Strength * falloff);

                case ForceFieldType.Thermal:
                    // Convection based on temperature difference
                    var tempDiff = particle.Temperature - 293.15f; // Difference from room temperature
                    var convectionForce = new OMV.Vector3(0, 0, tempDiff * 0.01f);
                    return SIMDPhysicsMath.Multiply(convectionForce, Strength * falloff);

                case ForceFieldType.Oscillating:
                    var time = (float)(DateTime.UtcNow.Ticks / 10000000.0); // Seconds
                    var oscillation = MathF.Sin(time * Frequency + Phase);
                    return SIMDPhysicsMath.Multiply(Direction, Strength * falloff * oscillation);

                default:
                    return OMV.Vector3.Zero;
            }
        }
    }

    /// <summary>
    /// Complete particle system with physics integration
    /// </summary>
    public class ParticleSystem
    {
        public uint SystemID { get; set; }
        public string Name { get; set; }
        public ParticleSystemType Type { get; set; }
        public ParticleBehavior Behavior { get; set; } = ParticleBehavior.Dynamic;
        public bool IsActive { get; set; } = true;
        
        // Particles and components
        public ConcurrentDictionary<uint, PhysicsParticle> Particles { get; set; }
        public List<ParticleEmitter> Emitters { get; set; }
        public List<ParticleForceField> ForceFields { get; set; }
        
        // System properties
        public int MaxParticles { get; set; } = 5000;
        public float GlobalTimeScale { get; set; } = 1.0f;
        public OMV.Vector3 SystemBounds { get; set; } = new OMV.Vector3(100, 100, 100);
        public OMV.Vector3 SystemCenter { get; set; } = OMV.Vector3.Zero;
        
        // Performance tracking
        public DateTime LastUpdate { get; set; }
        public float AverageUpdateTime { get; set; }
        public int UpdateCount { get; set; }
        public long ParticlesCreated { get; set; }
        public long ParticlesDestroyed { get; set; }
        public long CollisionCount { get; set; }
        
        // Interaction settings
        public bool EnableCollisions { get; set; } = true;
        public bool EnableInterParticleForces { get; set; } = false;
        public bool EnableFluidDynamics { get; set; } = false;
        public float InteractionRadius { get; set; } = 0.1f;

        public ParticleSystem(uint id, string name, ParticleSystemType type)
        {
            SystemID = id;
            Name = name;
            Type = type;
            Particles = new ConcurrentDictionary<uint, PhysicsParticle>();
            Emitters = new List<ParticleEmitter>();
            ForceFields = new List<ParticleForceField>();
            LastUpdate = DateTime.UtcNow;
        }

        public int GetActiveParticleCount()
        {
            return Particles.Values.Count(p => p.IsActive && p.IsAlive);
        }

        public bool IsWithinBounds(OMV.Vector3 position)
        {
            var relative = SIMDPhysicsMath.Subtract(position, SystemCenter);
            return Math.Abs(relative.X) <= SystemBounds.X * 0.5f &&
                   Math.Abs(relative.Y) <= SystemBounds.Y * 0.5f &&
                   Math.Abs(relative.Z) <= SystemBounds.Z * 0.5f;
        }
    }

    /// <summary>
    /// Advanced physics-aware particle system
    /// </summary>
    public class PhysicsParticleSystem : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[PHYSICS PARTICLES]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly ConcurrentDictionary<uint, ParticleSystem> m_particleSystems;
        private readonly Timer m_simulationTimer;
        private readonly object m_simulationLock;
        private bool m_disposed;
        private bool m_enabled;

        // Configuration
        private readonly TimeSpan m_simulationInterval = TimeSpan.FromMilliseconds(16); // ~60 FPS
        private readonly OMV.Vector3 m_gravity = new OMV.Vector3(0, 0, -9.81f);
        private readonly int m_maxSystemsPerFrame = 10;
        private readonly int m_maxParticlesPerFrame = 2000;

        // Performance tracking
        private long m_simulationSteps;
        private long m_particlesProcessed;
        private long m_emissionsProcessed;
        private long m_collisionsProcessed;
        private long m_forceCalculations;
        private float m_averageSimulationTime;
        private DateTime m_lastPerformanceReport;
        private readonly TimeSpan PerformanceReportInterval = TimeSpan.FromMinutes(2);

        // Global settings
        private float m_globalQualityMultiplier = 1.0f;
        private bool m_enableGlobalCollisions = true;
        private bool m_enableInterSystemInteractions = false;
        private uint m_nextParticleID = 1;

        #endregion

        #region Constructor

        public PhysicsParticleSystem(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_particleSystems = new ConcurrentDictionary<uint, ParticleSystem>();
            m_simulationLock = new object();

            m_lastPerformanceReport = DateTime.UtcNow;

            // Setup simulation timer
            m_simulationTimer = new Timer(PerformSimulationStep, null, m_simulationInterval, m_simulationInterval);

            m_enabled = true;

            m_log.InfoFormat("{0}: Physics particle system initialized - Simulation rate: {1} FPS", 
                LogHeader, 1.0f / m_simulationInterval.TotalSeconds);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the physics particle system
        /// </summary>
        public void Initialize()
        {
            if (m_disposed)
                return;

            m_enabled = true;
            m_log.InfoFormat("{0}: Physics particle system started", LogHeader);
        }

        /// <summary>
        /// Create a new particle system
        /// </summary>
        public uint CreateParticleSystem(string name, ParticleSystemType type, OMV.Vector3 center, OMV.Vector3 bounds)
        {
            if (!m_enabled || m_disposed)
                return 0;

            try
            {
                uint systemID = (uint)m_particleSystems.Count + 1;
                
                var particleSystem = new ParticleSystem(systemID, name, type)
                {
                    SystemCenter = center,
                    SystemBounds = bounds
                };

                // Apply type-specific defaults
                ApplySystemTypeDefaults(particleSystem);

                m_particleSystems.TryAdd(systemID, particleSystem);

                m_log.InfoFormat("{0}: Created particle system '{1}' (ID: {2}, Type: {3})", 
                    LogHeader, name, systemID, type);

                return systemID;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating particle system: {1}", LogHeader, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Add an emitter to a particle system
        /// </summary>
        public uint AddEmitter(uint systemID, string name, OMV.Vector3 position, OMV.Vector3 direction, ParticleSystemType type)
        {
            if (!m_enabled || m_disposed)
                return 0;

            if (m_particleSystems.TryGetValue(systemID, out ParticleSystem system))
            {
                uint emitterID = (uint)system.Emitters.Count + 1;
                
                var emitter = new ParticleEmitter(emitterID, name, type)
                {
                    Position = position,
                    Direction = SIMDPhysicsMath.Normalize(direction)
                };

                // Apply type-specific emitter defaults
                ApplyEmitterTypeDefaults(emitter);

                system.Emitters.Add(emitter);

                m_log.InfoFormat("{0}: Added emitter '{1}' to system {2}", LogHeader, name, systemID);
                return emitterID;
            }

            return 0;
        }

        /// <summary>
        /// Add a force field to a particle system
        /// </summary>
        public uint AddForceField(uint systemID, string name, ParticleForceField.ForceFieldType type, OMV.Vector3 position, float strength, float range)
        {
            if (!m_enabled || m_disposed)
                return 0;

            if (m_particleSystems.TryGetValue(systemID, out ParticleSystem system))
            {
                uint fieldID = (uint)system.ForceFields.Count + 1;
                
                var forceField = new ParticleForceField
                {
                    FieldID = fieldID,
                    Name = name,
                    Type = type,
                    Position = position,
                    Strength = strength,
                    Range = range
                };

                system.ForceFields.Add(forceField);

                m_log.InfoFormat("{0}: Added force field '{1}' to system {2}", LogHeader, name, systemID);
                return fieldID;
            }

            return 0;
        }

        /// <summary>
        /// Manually emit particles from an emitter
        /// </summary>
        public void EmitParticles(uint systemID, uint emitterID, int count)
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_particleSystems.TryGetValue(systemID, out ParticleSystem system))
            {
                var emitter = system.Emitters.FirstOrDefault(e => e.EmitterID == emitterID);
                if (emitter != null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        EmitParticle(system, emitter);
                    }
                }
            }
        }

        /// <summary>
        /// Set wind affecting particle systems
        /// </summary>
        public void SetGlobalWind(OMV.Vector3 windVelocity, float turbulence = 0.0f)
        {
            foreach (var system in m_particleSystems.Values)
            {
                // Add or update wind force field
                var windField = system.ForceFields.FirstOrDefault(f => f.Name == "GlobalWind");
                if (windField == null)
                {
                    windField = new ParticleForceField
                    {
                        FieldID = (uint)system.ForceFields.Count + 1,
                        Name = "GlobalWind",
                        Type = ParticleForceField.ForceFieldType.Directional,
                        Position = system.SystemCenter,
                        Range = system.SystemBounds.Length(),
                        Direction = SIMDPhysicsMath.Normalize(windVelocity),
                        Strength = windVelocity.Length()
                    };
                    system.ForceFields.Add(windField);
                }
                else
                {
                    windField.Direction = SIMDPhysicsMath.Normalize(windVelocity);
                    windField.Strength = windVelocity.Length();
                }

                // Update turbulence
                if (turbulence > 0.001f)
                {
                    var turbField = system.ForceFields.FirstOrDefault(f => f.Name == "GlobalTurbulence");
                    if (turbField == null)
                    {
                        turbField = new ParticleForceField
                        {
                            FieldID = (uint)system.ForceFields.Count + 1,
                            Name = "GlobalTurbulence",
                            Type = ParticleForceField.ForceFieldType.Turbulence,
                            Position = system.SystemCenter,
                            Range = system.SystemBounds.Length(),
                            Strength = turbulence,
                            TurbulenceScale = 0.1f
                        };
                        system.ForceFields.Add(turbField);
                    }
                    else
                    {
                        turbField.Strength = turbulence;
                    }
                }
            }
        }

        /// <summary>
        /// Get particle positions for rendering
        /// </summary>
        public OMV.Vector3[] GetParticlePositions(uint systemID)
        {
            if (!m_enabled || m_disposed)
                return new OMV.Vector3[0];

            if (m_particleSystems.TryGetValue(systemID, out ParticleSystem system))
            {
                return system.Particles.Values
                    .Where(p => p.IsActive && p.IsAlive)
                    .Select(p => p.Position)
                    .ToArray();
            }

            return new OMV.Vector3[0];
        }

        /// <summary>
        /// Get particle render data
        /// </summary>
        public ParticleRenderData[] GetParticleRenderData(uint systemID)
        {
            if (!m_enabled || m_disposed)
                return new ParticleRenderData[0];

            if (m_particleSystems.TryGetValue(systemID, out ParticleSystem system))
            {
                return system.Particles.Values
                    .Where(p => p.IsActive && p.IsAlive)
                    .Select(p => new ParticleRenderData
                    {
                        Position = p.Position,
                        Color = p.Color,
                        Alpha = p.Alpha,
                        Size = p.Size,
                        Rotation = p.Rotation
                    })
                    .ToArray();
            }

            return new ParticleRenderData[0];
        }

        /// <summary>
        /// Remove a particle system
        /// </summary>
        public void RemoveParticleSystem(uint systemID)
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_particleSystems.TryRemove(systemID, out ParticleSystem removedSystem))
            {
                m_log.InfoFormat("{0}: Removed particle system '{1}' (ID: {2})", LogHeader, removedSystem.Name, systemID);
            }
        }

        public void UpdateObjectPosition(uint objectID, OMV.Vector3 position, OMV.Vector3 velocity, OMV.Vector3 size, float mass)
        {
            // Placeholder for interaction with rigid bodies
        }

        public (OMV.Vector3 force, OMV.Vector3 torque) GetParticleForces(uint objectID)
        {
            // Placeholder for applying forces from particles to rigid bodies
            return (OMV.Vector3.Zero, OMV.Vector3.Zero);
        }

        public void RemoveObject(uint objectID)
        {
            // This method is called when a rigid body is removed from the scene.
            // We don't have interactions yet, so this is a placeholder.
        }

        /// <summary>
        /// Set simulation quality
        /// </summary>
        public void SetQuality(float qualityMultiplier)
        {
            m_globalQualityMultiplier = Math.Max(0.1f, Math.Min(2.0f, qualityMultiplier));
            m_log.InfoFormat("{0}: Simulation quality set to {1:F2}", LogHeader, m_globalQualityMultiplier);
        }

        /// <summary>
        /// Get comprehensive particle system performance report
        /// </summary>
        public string GetPerformanceReport()
        {
            if (!m_enabled || m_disposed)
                return "Physics particle system not available";

            var totalParticles = m_particleSystems.Values.Sum(s => s.GetActiveParticleCount());

            var report = $"Physics Particle System Performance:\\n";
            report += $"  Particle Systems: {m_particleSystems.Count}\\n";
            report += $"  Total Active Particles: {totalParticles}\\n";
            report += $"  Simulation Steps: {m_simulationSteps}\\n";
            report += $"  Particles Processed: {m_particlesProcessed}\\n";
            report += $"  Emissions Processed: {m_emissionsProcessed}\\n";
            report += $"  Collisions Processed: {m_collisionsProcessed}\\n";
            report += $"  Force Calculations: {m_forceCalculations}\\n";
            report += $"  Average Simulation Time: {m_averageSimulationTime:F2}ms\\n";
            report += $"  Quality Multiplier: {m_globalQualityMultiplier:F2}\\n";

            // System type distribution
            var systemTypes = m_particleSystems.Values
                .GroupBy(s => s.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            report += $"  System Type Distribution:\\n";
            foreach (var type in Enum.GetValues<ParticleSystemType>())
            {
                var count = systemTypes.GetValueOrDefault(type, 0);
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
                    var deltaTime = (float)m_simulationInterval.TotalSeconds;
                    var systemsProcessed = 0;

                    foreach (var system in m_particleSystems.Values)
                    {
                        if (systemsProcessed >= m_maxSystemsPerFrame)
                            break;

                        if (system.IsActive)
                        {
                            SimulateParticleSystem(system, deltaTime);
                            systemsProcessed++;
                        }
                    }

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
                m_log.ErrorFormat("{0}: Error during particle simulation step: {1}", LogHeader, ex.Message);
            }
        }

        private void SimulateParticleSystem(ParticleSystem system, float deltaTime)
        {
            var stepStartTime = DateTime.UtcNow;
            var adjustedDeltaTime = deltaTime * system.GlobalTimeScale * m_globalQualityMultiplier;

            // Process emissions
            ProcessEmissions(system, adjustedDeltaTime);

            // Update particles
            var particlesProcessed = 0;
            var particlesToRemove = new List<uint>();

            foreach (var particle in system.Particles.Values)
            {
                if (particlesProcessed >= m_maxParticlesPerFrame)
                    break;

                if (!particle.IsAlive)
                {
                    particlesToRemove.Add(particle.ID);
                    continue;
                }

                if (particle.IsActive)
                {
                    // Apply force fields
                    ApplyForceFields(system, particle, adjustedDeltaTime);

                    // Update particle physics
                    particle.UpdatePhysics(adjustedDeltaTime, m_gravity);

                    // Handle collisions
                    if (system.EnableCollisions && m_enableGlobalCollisions)
                    {
                        HandleParticleCollisions(system, particle);
                    }

                    // Check bounds
                    if (!system.IsWithinBounds(particle.Position))
                    {
                        HandleBoundaryCondition(system, particle);
                    }

                    // Update visual properties
                    UpdateParticleVisuals(particle);

                    particlesProcessed++;
                }

                m_particlesProcessed++;
            }

            // Remove dead particles
            foreach (var particleID in particlesToRemove)
            {
                if (system.Particles.TryRemove(particleID, out _))
                {
                    system.ParticlesDestroyed++;
                }
            }

            // Inter-particle forces (expensive, quality dependent)
            if (system.EnableInterParticleForces && m_globalQualityMultiplier > 0.5f)
            {
                ProcessInterParticleForces(system, adjustedDeltaTime);
            }

            // Update performance tracking
            var updateTime = (float)(DateTime.UtcNow - stepStartTime).TotalMilliseconds;
            system.AverageUpdateTime = system.AverageUpdateTime * 0.9f + updateTime * 0.1f;
            system.UpdateCount++;
            system.LastUpdate = DateTime.UtcNow;
        }

        private void ProcessEmissions(ParticleSystem system, float deltaTime)
        {
            foreach (var emitter in system.Emitters.Where(e => e.IsActive))
            {
                if (emitter.ShouldEmit(deltaTime))
                {
                    var particleCount = emitter.BurstMode ? emitter.BurstCount : 1;
                    
                    for (int i = 0; i < particleCount; i++)
                    {
                        if (system.Particles.Count >= system.MaxParticles)
                            break;

                        EmitParticle(system, emitter);
                    }

                    emitter.LastEmission = DateTime.UtcNow;
                    m_emissionsProcessed++;
                }
            }
        }

        private void EmitParticle(ParticleSystem system, ParticleEmitter emitter)
        {
            var particleID = m_nextParticleID++;
            
            // Calculate emission position
            var emissionOffset = new OMV.Vector3(
                (float)(Random.Shared.NextDouble() - 0.5) * emitter.EmissionArea.X,
                (float)(Random.Shared.NextDouble() - 0.5) * emitter.EmissionArea.Y,
                (float)(Random.Shared.NextDouble() - 0.5) * emitter.EmissionArea.Z
            );
            var position = SIMDPhysicsMath.Add(emitter.Position, emissionOffset);

            // Calculate emission velocity
            var spreadAngle = (float)(Random.Shared.NextDouble() - 0.5) * emitter.EmissionSpread;
            var rotatedDirection = RotateVector(emitter.Direction, spreadAngle);
            var speed = emitter.EmissionSpeed + ((float)(Random.Shared.NextDouble() - 0.5) * emitter.EmissionSpeed * 0.2f);
            var velocity = SIMDPhysicsMath.Multiply(rotatedDirection, speed);

            var particle = new PhysicsParticle(particleID, position)
            {
                Velocity = velocity,
                PreviousPosition = SIMDPhysicsMath.Subtract(position, SIMDPhysicsMath.Multiply(velocity, 0.016f)), // Assume 60fps for initial setup
                
                // Apply variations
                Mass = emitter.ParticleMass + ((float)(Random.Shared.NextDouble() - 0.5) * emitter.ParticleMassVariation),
                Lifetime = emitter.ParticleLifetime + ((float)(Random.Shared.NextDouble() - 0.5) * emitter.ParticleLifetimeVariation),
                Size = emitter.ParticleSize + ((float)(Random.Shared.NextDouble() - 0.5) * emitter.ParticleSizeVariation),
                Temperature = emitter.Temperature + ((float)(Random.Shared.NextDouble() - 0.5) * emitter.TemperatureVariation),
                Energy = emitter.InitialEnergy,
                Drag = emitter.Drag,
                Restitution = emitter.Restitution,
                
                // Visual properties
                Color = emitter.StartColor,
                Alpha = emitter.StartAlpha
            };

            particle.InverseMass = particle.Mass > 0.001f ? 1.0f / particle.Mass : 1000.0f;

            system.Particles.TryAdd(particleID, particle);
            system.ParticlesCreated++;
            emitter.EmittedParticles++;
        }

        private void ApplyForceFields(ParticleSystem system, PhysicsParticle particle, float deltaTime)
        {
            foreach (var forceField in system.ForceFields.Where(f => f.IsActive))
            {
                var force = forceField.CalculateForce(particle, deltaTime);
                particle.ApplyForce(force);
                m_forceCalculations++;
            }
        }

        private void HandleParticleCollisions(ParticleSystem system, PhysicsParticle particle)
        {
            // Simplified collision with ground plane
            var groundHeight = 0.0f;
            
            if (particle.Position.Z < groundHeight + particle.Radius)
            {
                particle.Position = new OMV.Vector3(particle.Position.X, particle.Position.Y, groundHeight + particle.Radius);
                particle.Velocity = new OMV.Vector3(
                    particle.Velocity.X * particle.Friction,
                    particle.Velocity.Y * particle.Friction,
                    -particle.Velocity.Z * particle.Restitution);
                
                particle.HasCollided = true;
                particle.CollisionNormal = OMV.Vector3.UnitZ;
                system.CollisionCount++;
                m_collisionsProcessed++;
            }
        }

        private void HandleBoundaryCondition(ParticleSystem system, PhysicsParticle particle)
        {
            // Simple boundary handling - mark particle as inactive if outside bounds
            particle.IsActive = false;
        }

        private void UpdateParticleVisuals(PhysicsParticle particle)
        {
            // Update color and alpha based on age
            var ageRatio = particle.Age / particle.Lifetime;
            
            // Simple alpha fade out
            particle.Alpha = 1.0f - ageRatio;
            
            // Simple size change
            particle.Size *= 1.0f + ageRatio * 0.5f;
        }

        private void ProcessInterParticleForces(ParticleSystem system, float deltaTime)
        {
            var particles = system.Particles.Values.Where(p => p.IsActive && p.IsAlive).ToArray();
            
            for (int i = 0; i < particles.Length; i++)
            {
                for (int j = i + 1; j < particles.Length; j++)
                {
                    var p1 = particles[i];
                    var p2 = particles[j];
                    
                    var distance = SIMDPhysicsMath.Distance(p1.Position, p2.Position);
                    
                    if (distance < system.InteractionRadius && distance > 0.001f)
                    {
                        // Simple repulsion force
                        var direction = SIMDPhysicsMath.Normalize(SIMDPhysicsMath.Subtract(p2.Position, p1.Position));
                        var force = SIMDPhysicsMath.Multiply(direction, 0.1f / (distance * distance));
                        
                        p1.ApplyForce(SIMDPhysicsMath.Multiply(force, -1.0f));
                        p2.ApplyForce(force);
                        
                        m_forceCalculations += 2;
                    }
                }
            }
        }

        private OMV.Vector3 RotateVector(OMV.Vector3 vector, float angle)
        {
            // Simple rotation around a perpendicular axis
            var cosAngle = MathF.Cos(angle);
            var sinAngle = MathF.Sin(angle);
            
            return new OMV.Vector3(
                vector.X * cosAngle - vector.Y * sinAngle,
                vector.X * sinAngle + vector.Y * cosAngle,
                vector.Z
            );
        }

        private void ApplySystemTypeDefaults(ParticleSystem system)
        {
            switch (system.Type)
            {
                case ParticleSystemType.Fire:
                    system.EnableInterParticleForces = false;
                    system.EnableCollisions = false;
                    break;
                    
                case ParticleSystemType.Water:
                    system.EnableInterParticleForces = true;
                    system.EnableFluidDynamics = true;
                    system.InteractionRadius = 0.05f;
                    break;
                    
                case ParticleSystemType.Debris:
                    system.EnableCollisions = true;
                    system.EnableInterParticleForces = false;
                    break;
                    
                case ParticleSystemType.Smoke:
                    system.EnableCollisions = false;
                    system.EnableInterParticleForces = false;
                    break;
            }
        }

        private void ApplyEmitterTypeDefaults(ParticleEmitter emitter)
        {
            switch (emitter.SystemType)
            {
                case ParticleSystemType.Fire:
                    emitter.EmissionRate = 20.0f;
                    emitter.ParticleLifetime = 2.0f;
                    emitter.EmissionSpeed = 3.0f;
                    emitter.Temperature = 800.0f;
                    break;
                    
                case ParticleSystemType.Water:
                    emitter.EmissionRate = 50.0f;
                    emitter.ParticleLifetime = 10.0f;
                    emitter.EmissionSpeed = 8.0f;
                    emitter.ParticleMass = 0.01f;
                    break;
                    
                case ParticleSystemType.Debris:
                    emitter.EmissionRate = 5.0f;
                    emitter.ParticleLifetime = 15.0f;
                    emitter.EmissionSpeed = 12.0f;
                    emitter.ParticleMass = 0.1f;
                    break;
            }
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
                m_particleSystems.Clear();

                m_disposed = true;
                m_log.InfoFormat("{0}: Physics particle system disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public int ParticleSystemCount => m_particleSystems.Count;
        public int TotalActiveParticles => m_particleSystems.Values.Sum(s => s.GetActiveParticleCount());
        public long SimulationSteps => m_simulationSteps;
        public float AverageSimulationTime => m_averageSimulationTime;
        public float QualityMultiplier => m_globalQualityMultiplier;

        #endregion
    }

    /// <summary>
    /// Render data for a particle
    /// </summary>
    public struct ParticleRenderData
    {
        public OMV.Vector3 Position;
        public OMV.Vector3 Color;
        public float Alpha;
        public float Size;
        public float Rotation;
    }
}