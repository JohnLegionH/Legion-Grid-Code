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
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Types of advanced constraints
    /// </summary>
    public enum AdvancedConstraintType
    {
        Rope = 0,           // Flexible rope/cable physics
        Chain = 1,          // Rigid chain links
        Spring = 2,         // Linear spring systems
        TorsionSpring = 3,  // Rotational spring systems
        Motor = 4,          // Powered rotation/translation
        Hydraulic = 5,      // Hydraulic actuators
        Pulley = 6,         // Pulley systems with mechanical advantage
        Gear = 7,           // Gear mechanisms with ratios
        Rack = 8,           // Rack and pinion systems
        Belt = 9,           // Belt drive systems
        Suspension = 10,    // Vehicle suspension systems
        Pendulum = 11       // Pendulum constraints
    }

    /// <summary>
    /// Constraint material properties affecting behavior
    /// </summary>
    public class ConstraintMaterial
    {
        public string Name { get; set; }
        public float Stiffness { get; set; } = 1000.0f;         // N/m for springs, N*m/rad for torsion
        public float Damping { get; set; } = 10.0f;             // Damping coefficient
        public float MaxTension { get; set; } = 10000.0f;       // Maximum force before breaking
        public float MaxCompression { get; set; } = 5000.0f;    // Maximum compression force
        public float Friction { get; set; } = 0.1f;             // Internal friction
        public float Elasticity { get; set; } = 0.8f;           // Elastic recovery (0-1)
        public float Density { get; set; } = 1000.0f;           // kg/m³
        public float Thickness { get; set; } = 0.01f;           // Physical thickness
        public bool CanBreak { get; set; } = true;              // Whether constraint can break
        public bool CanStretch { get; set; } = true;            // Whether constraint can stretch
        public float StretchLimit { get; set; } = 1.2f;         // Maximum stretch ratio

        public static ConstraintMaterial Steel => new ConstraintMaterial
        {
            Name = "Steel",
            Stiffness = 50000.0f,
            MaxTension = 50000.0f,
            MaxCompression = 40000.0f,
            Friction = 0.2f,
            Elasticity = 0.1f,
            Density = 7850.0f,
            CanStretch = false
        };

        public static ConstraintMaterial Rope => new ConstraintMaterial
        {
            Name = "Hemp Rope",
            Stiffness = 1000.0f,
            MaxTension = 5000.0f,
            MaxCompression = 0.0f,
            Friction = 0.3f,
            Elasticity = 0.9f,
            Density = 1200.0f,
            CanStretch = true,
            StretchLimit = 1.1f
        };

        public static ConstraintMaterial Rubber => new ConstraintMaterial
        {
            Name = "Rubber",
            Stiffness = 100.0f,
            MaxTension = 1000.0f,
            MaxCompression = 800.0f,
            Friction = 0.8f,
            Elasticity = 0.95f,
            Density = 920.0f,
            CanStretch = true,
            StretchLimit = 3.0f
        };
    }

    /// <summary>
    /// Advanced constraint with complex mechanical properties
    /// </summary>
    public class AdvancedConstraint
    {
        public uint ConstraintID { get; set; }
        public string Name { get; set; }
        public AdvancedConstraintType Type { get; set; }
        public ConstraintMaterial Material { get; set; }
        
        // Connected objects
        public uint ObjectA { get; set; }
        public uint ObjectB { get; set; }
        public OMV.Vector3 AnchorA { get; set; }        // Anchor point on object A
        public OMV.Vector3 AnchorB { get; set; }        // Anchor point on object B
        public OMV.Vector3 AxisA { get; set; } = OMV.Vector3.UnitZ;   // Constraint axis on object A
        public OMV.Vector3 AxisB { get; set; } = OMV.Vector3.UnitZ;   // Constraint axis on object B
        
        // Constraint parameters
        public float RestLength { get; set; } = 1.0f;   // Rest length/angle
        public float CurrentLength { get; set; } = 1.0f;
        public float CurrentForce { get; set; } = 0.0f;
        public float CurrentTorque { get; set; } = 0.0f;
        public float CurrentStress { get; set; } = 0.0f;
        
        // Limits and ranges
        public float MinLimit { get; set; } = -float.MaxValue;
        public float MaxLimit { get; set; } = float.MaxValue;
        public bool HasLimits { get; set; } = false;
        public float LimitStiffness { get; set; } = 10000.0f;
        public float LimitDamping { get; set; } = 100.0f;
        
        // Motor properties
        public bool IsMotorized { get; set; } = false;
        public float MotorTarget { get; set; } = 0.0f;   // Target position/velocity/angle
        public float MotorForce { get; set; } = 0.0f;    // Maximum motor force
        public float MotorVelocity { get; set; } = 0.0f; // Current motor velocity
        public MotorType MotorMode { get; set; } = MotorType.Position;
        
        // Mechanical advantage (for pulleys, gears, etc.)
        public float MechanicalAdvantage { get; set; } = 1.0f;
        public float EfficiencyFactor { get; set; } = 1.0f;
        
        // State tracking
        public bool IsActive { get; set; } = true;
        public bool IsBroken { get; set; } = false;
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdate { get; set; }
        public int SolverIterations { get; set; } = 3;
        
        // Performance tracking
        public long UpdateCount { get; set; }
        public float AverageUpdateTime { get; set; }
        public float MaxStressRecorded { get; set; }

        public enum MotorType
        {
            Position,   // Position control (servo)
            Velocity,   // Velocity control
            Force,      // Force/torque control
            PID         // PID control system
        }

        public AdvancedConstraint(uint id, string name, AdvancedConstraintType type)
        {
            ConstraintID = id;
            Name = name;
            Type = type;
            CreatedAt = DateTime.UtcNow;
            LastUpdate = DateTime.UtcNow;
            Material = GetDefaultMaterial(type);
        }

        private ConstraintMaterial GetDefaultMaterial(AdvancedConstraintType type)
        {
            return type switch
            {
                AdvancedConstraintType.Rope => ConstraintMaterial.Rope,
                AdvancedConstraintType.Spring => ConstraintMaterial.Rubber,
                AdvancedConstraintType.Motor => ConstraintMaterial.Steel,
                AdvancedConstraintType.Gear => ConstraintMaterial.Steel,
                _ => ConstraintMaterial.Steel
            };
        }

        public float GetStressRatio()
        {
            var maxStress = Type == AdvancedConstraintType.Spring ? Material.MaxCompression : Material.MaxTension;
            return Math.Abs(CurrentStress) / maxStress;
        }

        public bool ShouldBreak()
        {
            if (!Material.CanBreak || IsBroken)
                return false;

            return GetStressRatio() > 1.0f;
        }

        public TimeSpan Age => DateTime.UtcNow - CreatedAt;
    }

    /// <summary>
    /// Physical object state for constraint calculations
    /// </summary>
    public struct PhysicsObjectState
    {
        public OMV.Vector3 Position;
        public OMV.Quaternion Rotation;
        public OMV.Vector3 LinearVelocity;
        public OMV.Vector3 AngularVelocity;
        public float Mass;
        public float InverseMass;
        public OMV.Vector3 InertiaTensor;
        public bool IsStatic;
        public bool IsActive;
    }

    /// <summary>
    /// Constraint force/torque result
    /// </summary>
    public struct ConstraintForceResult
    {
        public OMV.Vector3 ForceA;      // Force applied to object A
        public OMV.Vector3 TorqueA;     // Torque applied to object A
        public OMV.Vector3 ForceB;      // Force applied to object B
        public OMV.Vector3 TorqueB;     // Torque applied to object B
        public float Energy;           // Energy stored/dissipated
        public bool WasLimited;        // Whether limits were engaged
        public bool MotorActive;       // Whether motor was active
    }

    /// <summary>
    /// Advanced constraint system with complex mechanical simulations
    /// </summary>
    public class AdvancedConstraintSystem : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[ADVANCED CONSTRAINTS]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly ConcurrentDictionary<uint, AdvancedConstraint> m_constraints;
        private readonly Timer m_solverTimer;
        private readonly object m_solverLock;
        private bool m_disposed;
        private bool m_enabled;

        // Object state cache for constraint solving
        private readonly ConcurrentDictionary<uint, PhysicsObjectState> m_objectStates;

        // Configuration
        private readonly TimeSpan m_solverInterval = TimeSpan.FromMilliseconds(16); // ~60 FPS
        private readonly int m_maxConstraintsPerFrame = 50;
        private readonly int m_globalSolverIterations = 5;

        // Performance tracking
        private long m_solverSteps;
        private long m_constraintsProcessed;
        private long m_motorUpdates;
        private long m_limitViolations;
        private long m_constraintBreaks;
        private float m_averageSolverTime;
        private DateTime m_lastPerformanceReport;
        private readonly TimeSpan PerformanceReportInterval = TimeSpan.FromMinutes(2);

        // Solver parameters
        private float m_globalStiffnessMultiplier = 1.0f;
        private float m_globalDampingMultiplier = 1.0f;
        private bool m_enableBreaking = true;
        private bool m_enableMotors = true;

        #endregion

        #region Constructor

        public AdvancedConstraintSystem(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_constraints = new ConcurrentDictionary<uint, AdvancedConstraint>();
            m_objectStates = new ConcurrentDictionary<uint, PhysicsObjectState>();
            m_solverLock = new object();

            m_lastPerformanceReport = DateTime.UtcNow;

            // Setup solver timer
            m_solverTimer = new Timer(PerformConstraintSolving, null, m_solverInterval, m_solverInterval);

            m_enabled = true;

            m_log.InfoFormat("{0}: Advanced constraint system initialized - Solver rate: {1} FPS", 
                LogHeader, 1.0f / m_solverInterval.TotalSeconds);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the advanced constraint system
        /// </summary>
        public void Initialize()
        {
            if (m_disposed)
                return;

            m_enabled = true;
            m_log.InfoFormat("{0}: Advanced constraint system started", LogHeader);
        }

        /// <summary>
        /// Create a rope constraint between two objects
        /// </summary>
        public uint CreateRope(string name, uint objectA, uint objectB, OMV.Vector3 anchorA, OMV.Vector3 anchorB, float length, ConstraintMaterial material = null)
        {
            if (!m_enabled || m_disposed)
                return 0;

            try
            {
                uint constraintID = (uint)m_constraints.Count + 1;
                material = material ?? ConstraintMaterial.Rope;

                var rope = new AdvancedConstraint(constraintID, name, AdvancedConstraintType.Rope)
                {
                    ObjectA = objectA,
                    ObjectB = objectB,
                    AnchorA = anchorA,
                    AnchorB = anchorB,
                    RestLength = length,
                    CurrentLength = length,
                    Material = material,
                    HasLimits = true,
                    MinLimit = 0.0f,
                    MaxLimit = length * material.StretchLimit
                };

                m_constraints.TryAdd(constraintID, rope);

                m_log.InfoFormat("{0}: Created rope '{1}' (ID: {2}, Length: {3:F2}m)", 
                    LogHeader, name, constraintID, length);

                return constraintID;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating rope constraint: {1}", LogHeader, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Create a spring constraint between two objects
        /// </summary>
        public uint CreateSpring(string name, uint objectA, uint objectB, OMV.Vector3 anchorA, OMV.Vector3 anchorB, float restLength, float stiffness, float damping)
        {
            if (!m_enabled || m_disposed)
                return 0;

            try
            {
                uint constraintID = (uint)m_constraints.Count + 1;

                var spring = new AdvancedConstraint(constraintID, name, AdvancedConstraintType.Spring)
                {
                    ObjectA = objectA,
                    ObjectB = objectB,
                    AnchorA = anchorA,
                    AnchorB = anchorB,
                    RestLength = restLength,
                    CurrentLength = restLength,
                    Material = new ConstraintMaterial
                    {
                        Name = "Custom Spring",
                        Stiffness = stiffness,
                        Damping = damping,
                        CanStretch = true,
                        StretchLimit = 2.0f
                    }
                };

                m_constraints.TryAdd(constraintID, spring);

                m_log.InfoFormat("{0}: Created spring '{1}' (ID: {2}, K: {3:F1} N/m)", 
                    LogHeader, name, constraintID, stiffness);

                return constraintID;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating spring constraint: {1}", LogHeader, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Create a motor constraint for powered motion
        /// </summary>
        public uint CreateMotor(string name, uint objectA, uint objectB, OMV.Vector3 anchorA, OMV.Vector3 anchorB, OMV.Vector3 axis, float maxForce)
        {
            if (!m_enabled || m_disposed)
                return 0;

            try
            {
                uint constraintID = (uint)m_constraints.Count + 1;

                var motor = new AdvancedConstraint(constraintID, name, AdvancedConstraintType.Motor)
                {
                    ObjectA = objectA,
                    ObjectB = objectB,
                    AnchorA = anchorA,
                    AnchorB = anchorB,
                    AxisA = SIMDPhysicsMath.Normalize(axis),
                    AxisB = SIMDPhysicsMath.Normalize(axis),
                    IsMotorized = true,
                    MotorForce = maxForce,
                    MotorMode = AdvancedConstraint.MotorType.Velocity,
                    Material = ConstraintMaterial.Steel
                };

                m_constraints.TryAdd(constraintID, motor);

                m_log.InfoFormat("{0}: Created motor '{1}' (ID: {2}, Max Force: {3:F1} N)", 
                    LogHeader, name, constraintID, maxForce);

                return constraintID;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating motor constraint: {1}", LogHeader, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Create a pulley system with mechanical advantage
        /// </summary>
        public uint CreatePulley(string name, uint objectA, uint objectB, OMV.Vector3 anchorA, OMV.Vector3 anchorB, OMV.Vector3 pulleyPoint, float mechanicalAdvantage)
        {
            if (!m_enabled || m_disposed)
                return 0;

            try
            {
                uint constraintID = (uint)m_constraints.Count + 1;

                var pulley = new AdvancedConstraint(constraintID, name, AdvancedConstraintType.Pulley)
                {
                    ObjectA = objectA,
                    ObjectB = objectB,
                    AnchorA = anchorA,
                    AnchorB = anchorB,
                    MechanicalAdvantage = mechanicalAdvantage,
                    EfficiencyFactor = 0.95f, // 95% efficiency
                    Material = ConstraintMaterial.Rope
                };

                // Calculate total rope length through pulley
                var lengthA = SIMDPhysicsMath.Distance(anchorA, pulleyPoint);
                var lengthB = SIMDPhysicsMath.Distance(anchorB, pulleyPoint);
                pulley.RestLength = lengthA + lengthB;

                m_constraints.TryAdd(constraintID, pulley);

                m_log.InfoFormat("{0}: Created pulley '{1}' (ID: {2}, Advantage: {3:F2})", 
                    LogHeader, name, constraintID, mechanicalAdvantage);

                return constraintID;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating pulley constraint: {1}", LogHeader, ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Set motor target for a motorized constraint
        /// </summary>
        public void SetMotorTarget(uint constraintID, float target, AdvancedConstraint.MotorType mode = AdvancedConstraint.MotorType.Velocity)
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_constraints.TryGetValue(constraintID, out AdvancedConstraint constraint))
            {
                if (constraint.IsMotorized)
                {
                    constraint.MotorTarget = target;
                    constraint.MotorMode = mode;
                    
                    m_log.DebugFormat("{0}: Set motor target for constraint {1} to {2:F2} ({3})", 
                        LogHeader, constraintID, target, mode);
                }
            }
        }

        /// <summary>
        /// Update object state for constraint calculations
        /// </summary>
        public void UpdateObjectState(uint objectID, OMV.Vector3 position, OMV.Quaternion rotation, 
            OMV.Vector3 linearVelocity, OMV.Vector3 angularVelocity, float mass, bool isStatic)
        {
            if (!m_enabled || m_disposed)
                return;

            var state = new PhysicsObjectState
            {
                Position = position,
                Rotation = rotation,
                LinearVelocity = linearVelocity,
                AngularVelocity = angularVelocity,
                Mass = mass,
                InverseMass = mass > 0.001f ? 1.0f / mass : 0.0f,
                InertiaTensor = new OMV.Vector3(mass * 0.1f), // Simplified inertia
                IsStatic = isStatic,
                IsActive = true
            };

            m_objectStates.AddOrUpdate(objectID, state, (id, existing) => state);
        }

        /// <summary>
        /// Remove object from constraint tracking
        /// </summary>
        public void RemoveObject(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return;

            m_objectStates.TryRemove(objectID, out _);

            // Remove constraints involving this object
            var constraintsToRemove = m_constraints.Values
                .Where(c => c.ObjectA == objectID || c.ObjectB == objectID)
                .Select(c => c.ConstraintID)
                .ToList();

            foreach (var constraintID in constraintsToRemove)
            {
                RemoveConstraint(constraintID);
            }
        }

        /// <summary>
        /// Remove a constraint
        /// </summary>
        public void RemoveConstraint(uint constraintID)
        {
            if (!m_enabled || m_disposed)
                return;

            if (m_constraints.TryRemove(constraintID, out AdvancedConstraint removedConstraint))
            {
                m_log.InfoFormat("{0}: Removed constraint '{1}' (ID: {2})", LogHeader, removedConstraint.Name, constraintID);
            }
        }

        /// <summary>
        /// Get constraint forces for integration with physics engine
        /// </summary>
        public (OMV.Vector3 force, OMV.Vector3 torque)? GetConstraintForces(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return null;

            var totalForce = OMV.Vector3.Zero;
            var totalTorque = OMV.Vector3.Zero;

            foreach (var constraint in m_constraints.Values.Where(c => c.IsActive && !c.IsBroken))
            {
                if (constraint.ObjectA == objectID || constraint.ObjectB == objectID)
                {
                    // Get the last calculated forces (simplified - in real implementation these would be cached)
                    var result = CalculateConstraintForces(constraint);
                    
                    if (constraint.ObjectA == objectID)
                    {
                        totalForce = SIMDPhysicsMath.Add(totalForce, result.ForceA);
                        totalTorque = SIMDPhysicsMath.Add(totalTorque, result.TorqueA);
                    }
                    else
                    {
                        totalForce = SIMDPhysicsMath.Add(totalForce, result.ForceB);
                        totalTorque = SIMDPhysicsMath.Add(totalTorque, result.TorqueB);
                    }
                }
            }

            return (totalForce, totalTorque);
        }

        /// <summary>
        /// Get comprehensive constraint system performance report
        /// </summary>
        public string GetPerformanceReport()
        {
            if (!m_enabled || m_disposed)
                return "Advanced constraint system not available";

            var report = $"Advanced Constraint System Performance:\\n";
            report += $"  Active Constraints: {m_constraints.Count(c => c.Value.IsActive && !c.Value.IsBroken)}\\n";
            report += $"  Total Constraints: {m_constraints.Count}\\n";
            report += $"  Tracked Objects: {m_objectStates.Count}\\n";
            report += $"  Solver Steps: {m_solverSteps}\\n";
            report += $"  Constraints Processed: {m_constraintsProcessed}\\n";
            report += $"  Motor Updates: {m_motorUpdates}\\n";
            report += $"  Limit Violations: {m_limitViolations}\\n";
            report += $"  Constraint Breaks: {m_constraintBreaks}\\n";
            report += $"  Average Solver Time: {m_averageSolverTime:F2}ms\\n";

            // Constraint type distribution
            var constraintTypes = m_constraints.Values
                .GroupBy(c => c.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            report += $"  Constraint Type Distribution:\\n";
            foreach (var type in Enum.GetValues<AdvancedConstraintType>())
            {
                var count = constraintTypes.GetValueOrDefault(type, 0);
                if (count > 0)
                    report += $"    {type}: {count}\\n";
            }

            // Broken constraints
            var brokenCount = m_constraints.Values.Count(c => c.IsBroken);
            report += $"  Broken Constraints: {brokenCount}\\n";

            return report;
        }

        #endregion

        #region Private Methods

        private void PerformConstraintSolving(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            var startTime = DateTime.UtcNow;

            try
            {
                lock (m_solverLock)
                {
                    var deltaTime = (float)m_solverInterval.TotalSeconds;
                    var constraintsProcessed = 0;

                    // Iterative constraint solving
                    for (int iteration = 0; iteration < m_globalSolverIterations; iteration++)
                    {
                        foreach (var constraint in m_constraints.Values)
                        {
                            if (constraintsProcessed >= m_maxConstraintsPerFrame)
                                break;

                            if (constraint.IsActive && !constraint.IsBroken)
                            {
                                SolveConstraint(constraint, deltaTime);
                                constraintsProcessed++;
                            }
                        }
                    }

                    m_solverSteps++;
                    m_constraintsProcessed += constraintsProcessed;

                    var solverTime = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
                    m_averageSolverTime = m_averageSolverTime * 0.9f + solverTime * 0.1f;

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
                m_log.ErrorFormat("{0}: Error during constraint solving: {1}", LogHeader, ex.Message);
            }
        }

        private void SolveConstraint(AdvancedConstraint constraint, float deltaTime)
        {
            var stepStartTime = DateTime.UtcNow;

            try
            {
                // Get object states
                if (!m_objectStates.TryGetValue(constraint.ObjectA, out PhysicsObjectState stateA) ||
                    !m_objectStates.TryGetValue(constraint.ObjectB, out PhysicsObjectState stateB))
                {
                    return; // Objects not found
                }

                // Calculate constraint forces
                var result = CalculateConstraintForces(constraint);

                // Update constraint state
                UpdateConstraintState(constraint, stateA, stateB, deltaTime);

                // Check for breaking
                if (m_enableBreaking && constraint.ShouldBreak())
                {
                    constraint.IsBroken = true;
                    m_constraintBreaks++;
                    m_log.InfoFormat("{0}: Constraint '{1}' (ID: {2}) broke due to stress ({3:F1})", 
                        LogHeader, constraint.Name, constraint.ConstraintID, constraint.CurrentStress);
                }

                // Apply forces to physics objects (this would integrate with the main physics system)
                ApplyConstraintForces(constraint, result);

                // Update performance tracking
                var updateTime = (float)(DateTime.UtcNow - stepStartTime).TotalMilliseconds;
                constraint.AverageUpdateTime = constraint.AverageUpdateTime * 0.9f + updateTime * 0.1f;
                constraint.UpdateCount++;
                constraint.LastUpdate = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error solving constraint {1}: {2}", LogHeader, constraint.ConstraintID, ex.Message);
            }
        }

        private ConstraintForceResult CalculateConstraintForces(AdvancedConstraint constraint)
        {
            var result = new ConstraintForceResult();

            if (!m_objectStates.TryGetValue(constraint.ObjectA, out PhysicsObjectState stateA) ||
                !m_objectStates.TryGetValue(constraint.ObjectB, out PhysicsObjectState stateB))
            {
                return result;
            }

            switch (constraint.Type)
            {
                case AdvancedConstraintType.Rope:
                    result = SolveRopeConstraint(constraint, stateA, stateB);
                    break;

                case AdvancedConstraintType.Spring:
                    result = SolveSpringConstraint(constraint, stateA, stateB);
                    break;

                case AdvancedConstraintType.Motor:
                    result = SolveMotorConstraint(constraint, stateA, stateB);
                    break;

                case AdvancedConstraintType.Pulley:
                    result = SolvePulleyConstraint(constraint, stateA, stateB);
                    break;

                default:
                    result = SolveGenericConstraint(constraint, stateA, stateB);
                    break;
            }

            return result;
        }

        private ConstraintForceResult SolveRopeConstraint(AdvancedConstraint constraint, PhysicsObjectState stateA, PhysicsObjectState stateB)
        {
            var result = new ConstraintForceResult();

            // Calculate world anchor positions
            var worldAnchorA = SIMDPhysicsMath.Add(stateA.Position, constraint.AnchorA);
            var worldAnchorB = SIMDPhysicsMath.Add(stateB.Position, constraint.AnchorB);

            // Calculate current distance
            var delta = SIMDPhysicsMath.Subtract(worldAnchorB, worldAnchorA);
            var currentLength = SIMDPhysicsMath.Length(delta);
            constraint.CurrentLength = currentLength;

            // Rope can only pull, not push
            if (currentLength <= constraint.RestLength)
                return result;

            var extension = currentLength - constraint.RestLength;
            var direction = SIMDPhysicsMath.Normalize(delta);

            // Calculate tension force
            var tensionMagnitude = extension * constraint.Material.Stiffness * m_globalStiffnessMultiplier;
            
            // Apply damping
            var relativeVelocity = SIMDPhysicsMath.Subtract(stateB.LinearVelocity, stateA.LinearVelocity);
            var velocityAlongRope = SIMDPhysicsMath.Dot(relativeVelocity, direction);
            var dampingForce = velocityAlongRope * constraint.Material.Damping * m_globalDampingMultiplier;
            
            tensionMagnitude += dampingForce;
            tensionMagnitude = Math.Max(0.0f, tensionMagnitude); // Rope can't push

            // Calculate forces
            var force = SIMDPhysicsMath.Multiply(direction, tensionMagnitude);
            result.ForceA = force;
            result.ForceB = SIMDPhysicsMath.Multiply(force, -1.0f);

            // Update constraint stress
            constraint.CurrentForce = tensionMagnitude;
            constraint.CurrentStress = tensionMagnitude;

            // Track maximum stress
            constraint.MaxStressRecorded = Math.Max(constraint.MaxStressRecorded, constraint.CurrentStress);

            return result;
        }

        private ConstraintForceResult SolveSpringConstraint(AdvancedConstraint constraint, PhysicsObjectState stateA, PhysicsObjectState stateB)
        {
            var result = new ConstraintForceResult();

            // Calculate world anchor positions
            var worldAnchorA = SIMDPhysicsMath.Add(stateA.Position, constraint.AnchorA);
            var worldAnchorB = SIMDPhysicsMath.Add(stateB.Position, constraint.AnchorB);

            // Calculate current distance and compression/extension
            var delta = SIMDPhysicsMath.Subtract(worldAnchorB, worldAnchorA);
            var currentLength = SIMDPhysicsMath.Length(delta);
            constraint.CurrentLength = currentLength;

            var displacement = currentLength - constraint.RestLength;
            var direction = currentLength > 0.001f ? SIMDPhysicsMath.Normalize(delta) : OMV.Vector3.UnitX;

            // Spring force: F = -k * x
            var springForceMagnitude = -displacement * constraint.Material.Stiffness * m_globalStiffnessMultiplier;

            // Damping force: F = -c * v
            var relativeVelocity = SIMDPhysicsMath.Subtract(stateB.LinearVelocity, stateA.LinearVelocity);
            var velocityAlongSpring = SIMDPhysicsMath.Dot(relativeVelocity, direction);
            var dampingForceMagnitude = -velocityAlongSpring * constraint.Material.Damping * m_globalDampingMultiplier;

            var totalForceMagnitude = springForceMagnitude + dampingForceMagnitude;

            // Calculate forces
            var force = SIMDPhysicsMath.Multiply(direction, totalForceMagnitude);
            result.ForceA = SIMDPhysicsMath.Multiply(force, -1.0f);
            result.ForceB = force;

            // Update constraint state
            constraint.CurrentForce = Math.Abs(totalForceMagnitude);
            constraint.CurrentStress = Math.Abs(displacement) > 0 ? 
                Math.Abs(springForceMagnitude) : Math.Abs(totalForceMagnitude);

            // Store energy in spring
            result.Energy = 0.5f * constraint.Material.Stiffness * displacement * displacement;

            return result;
        }

        private ConstraintForceResult SolveMotorConstraint(AdvancedConstraint constraint, PhysicsObjectState stateA, PhysicsObjectState stateB)
        {
            var result = new ConstraintForceResult();

            if (!m_enableMotors)
                return result;

            // Calculate relative motion along motor axis
            var relativeVelocity = SIMDPhysicsMath.Subtract(stateB.LinearVelocity, stateA.LinearVelocity);
            var velocityAlongAxis = SIMDPhysicsMath.Dot(relativeVelocity, constraint.AxisA);

            float motorForce = 0.0f;

            switch (constraint.MotorMode)
            {
                case AdvancedConstraint.MotorType.Velocity:
                    // Velocity control
                    var velocityError = constraint.MotorTarget - velocityAlongAxis;
                    motorForce = velocityError * constraint.MotorForce * 0.1f; // PD control simplified
                    break;

                case AdvancedConstraint.MotorType.Force:
                    // Direct force control
                    motorForce = constraint.MotorTarget;
                    break;

                case AdvancedConstraint.MotorType.Position:
                    // Position control (simplified)
                    var worldAnchorA = SIMDPhysicsMath.Add(stateA.Position, constraint.AnchorA);
                    var worldAnchorB = SIMDPhysicsMath.Add(stateB.Position, constraint.AnchorB);
                    var currentDistance = SIMDPhysicsMath.Distance(worldAnchorA, worldAnchorB);
                    var positionError = constraint.MotorTarget - currentDistance;
                    motorForce = positionError * constraint.MotorForce * 0.5f;
                    break;
            }

            // Limit motor force
            motorForce = Math.Max(-constraint.MotorForce, Math.Min(constraint.MotorForce, motorForce));

            // Apply motor force along axis
            var force = SIMDPhysicsMath.Multiply(constraint.AxisA, motorForce);
            result.ForceA = SIMDPhysicsMath.Multiply(force, -1.0f);
            result.ForceB = force;

            constraint.CurrentForce = Math.Abs(motorForce);
            constraint.MotorVelocity = velocityAlongAxis;
            result.MotorActive = Math.Abs(motorForce) > 0.1f;

            if (result.MotorActive)
                m_motorUpdates++;

            return result;
        }

        private ConstraintForceResult SolvePulleyConstraint(AdvancedConstraint constraint, PhysicsObjectState stateA, PhysicsObjectState stateB)
        {
            var result = new ConstraintForceResult();

            // Simplified pulley: apply mechanical advantage to forces
            var baseForce = 100.0f; // This would come from the rope tension calculation
            var forceA = baseForce;
            var forceB = baseForce * constraint.MechanicalAdvantage * constraint.EfficiencyFactor;

            // Apply forces vertically (simplified)
            result.ForceA = new OMV.Vector3(0, 0, forceA);
            result.ForceB = new OMV.Vector3(0, 0, -forceB);

            constraint.CurrentForce = Math.Max(forceA, forceB);

            return result;
        }

        private ConstraintForceResult SolveGenericConstraint(AdvancedConstraint constraint, PhysicsObjectState stateA, PhysicsObjectState stateB)
        {
            // Fallback to simple distance constraint
            return SolveRopeConstraint(constraint, stateA, stateB);
        }

        private void UpdateConstraintState(AdvancedConstraint constraint, PhysicsObjectState stateA, PhysicsObjectState stateB, float deltaTime)
        {
            // Check limit violations
            if (constraint.HasLimits)
            {
                if (constraint.CurrentLength < constraint.MinLimit || constraint.CurrentLength > constraint.MaxLimit)
                {
                    m_limitViolations++;
                }
            }

            // Update last update time
            constraint.LastUpdate = DateTime.UtcNow;
        }

        private void ApplyConstraintForces(AdvancedConstraint constraint, ConstraintForceResult result)
        {
            // In a real implementation, this would apply forces to the physics objects
            // For now, we just store the forces for the physics engine to retrieve
            
            // This is where the integration with BSScene would happen
            // m_scene.ApplyForce(constraint.ObjectA, result.ForceA, result.TorqueA);
            // m_scene.ApplyForce(constraint.ObjectB, result.ForceB, result.TorqueB);
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
                m_solverTimer?.Dispose();
                m_constraints.Clear();
                m_objectStates.Clear();

                m_disposed = true;
                m_log.InfoFormat("{0}: Advanced constraint system disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public int ConstraintCount => m_constraints.Count;
        public int ActiveConstraintCount => m_constraints.Values.Count(c => c.IsActive && !c.IsBroken);
        public int TrackedObjectCount => m_objectStates.Count;
        public long SolverSteps => m_solverSteps;
        public float AverageSolverTime => m_averageSolverTime;
        public bool EnableBreaking
        {
            get => m_enableBreaking;
            set => m_enableBreaking = value;
        }
        public bool EnableMotors
        {
            get => m_enableMotors;
            set => m_enableMotors = value;
        }

        #endregion
    }
}