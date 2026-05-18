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
using System.Reflection;
using log4net;
using OMV = OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Modern vehicle controller implementation with enhanced vehicle physics
    /// Provides advanced suspension, tire simulation, and aerodynamics
    /// </summary>
    public class ModernVehicleController : IVehicleController, IPoolable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MODERN VEHICLE CONTROLLER]";

        #region Private Fields

        private BSPrimLinkable m_legacyPrim;
        private BSDynamics m_legacyVehicle;
        private LegacyApiAdapter m_adapter;
        private BSScene m_scene;
        private VehicleDefinition m_definition;
        private bool m_initialized;
        private bool m_disposed;

        // Enhanced vehicle properties
        private OMV.Vector3 m_lastPosition;
        private OMV.Quaternion m_lastRotation;
        private OMV.Vector3 m_lastLinearVelocity;
        private OMV.Vector3 m_lastAngularVelocity;
        private Vehicle m_vehicleType;

        // Advanced vehicle features
        private bool m_advancedSuspensionEnabled;
        private bool m_tireSimulationEnabled;
        private bool m_aerodynamicsEnabled;
        
        // Vehicle parameters storage
        private Dictionary<int, float> m_floatParameters;
        private Dictionary<int, OMV.Vector3> m_vectorParameters;
        private Dictionary<int, OMV.Quaternion> m_rotationParameters;
        private int m_vehicleFlags;

        // Advanced physics simulation
        private readonly List<WheelInfo> m_wheels;
        private AerodynamicsInfo m_aerodynamics;
        private DateTime m_lastUpdate;
        private float m_enginePower;
        private float m_brakeForce;
        private float m_steeringAngle;

        #endregion

        #region Nested Classes

        private class WheelInfo
        {
            public OMV.Vector3 Position;
            public float Radius;
            public float SuspensionStiffness;
            public float SuspensionDamping;
            public float SuspensionRestLength;
            public float MaxSuspensionForce;
            public float FrictionSlip;
            public bool IsSteering;
            public bool IsPowered;
            public bool HasBrake;
            public float SuspensionLength;
            public OMV.Vector3 SuspensionForce;
            public float AngularVelocity;
        }

        private class AerodynamicsInfo
        {
            public float DragCoefficient;
            public float LiftCoefficient;
            public float FrontalArea;
            public OMV.Vector3 CenterOfPressure;
            public float AirDensity;
        }

        #endregion

        #region Constructor

        public ModernVehicleController()
        {
            m_floatParameters = new Dictionary<int, float>();
            m_vectorParameters = new Dictionary<int, OMV.Vector3>();
            m_rotationParameters = new Dictionary<int, OMV.Quaternion>();
            m_wheels = new List<WheelInfo>();
            m_aerodynamics = new AerodynamicsInfo();
            m_lastUpdate = DateTime.UtcNow;
            
            InitializeDefaultAerodynamics();
        }

        #endregion

        #region Initialization

        public void Initialize(VehicleDefinition definition, BSAPITemplate legacyAPI, BSScene scene)
        {
            if (m_initialized)
                throw new InvalidOperationException("ModernVehicleController already initialized");

            try
            {
                m_definition = definition ?? throw new ArgumentNullException(nameof(definition));
                m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
                m_adapter = new LegacyApiAdapter(legacyAPI, scene);

                // Find the corresponding legacy primitive and vehicle
                BSPrimLinkable legacyPrim = null;
                if (scene.PhysObjects.TryGetValue(definition.LocalID, out BSPhysObject physObj))
                {
                    legacyPrim = physObj as BSPrimLinkable;
                }

                if (legacyPrim == null)
                {
                    m_log.WarnFormat("{0}: No existing legacy primitive found for LocalID {1}", 
                        LogHeader, definition.LocalID);
                }
                else
                {
                    // Get the vehicle from the primitive
                    m_legacyVehicle = legacyPrim.GetVehicleActor(false);
                    if (m_legacyVehicle == null)
                    {
                        m_log.WarnFormat("{0}: Legacy primitive {1} has no vehicle actor", 
                            LogHeader, definition.LocalID);
                    }
                }

                m_legacyPrim = legacyPrim;
                
                // Initialize enhanced properties
                m_lastPosition = definition.Position;
                m_lastRotation = definition.Rotation;
                m_lastLinearVelocity = OMV.Vector3.Zero;
                m_lastAngularVelocity = OMV.Vector3.Zero;
                m_vehicleType = definition.VehicleType;

                // Initialize advanced features
                m_advancedSuspensionEnabled = definition.EnableAdvancedSuspension;
                m_tireSimulationEnabled = definition.EnableTireSimulation;
                m_aerodynamicsEnabled = definition.EnableAerodynamics;

                // Setup default wheels based on vehicle type
                SetupDefaultWheels();

                m_initialized = true;
                m_log.DebugFormat("{0}: Initialized ModernVehicleController for LocalID {1}, Type: {2}", 
                    LogHeader, definition.LocalID, definition.VehicleType);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to initialize ModernVehicleController: {1}", LogHeader, ex.Message);
                throw;
            }
        }

        #endregion

        #region IVehicleController Implementation

        public uint LocalID => m_definition?.LocalID ?? 0;

        public OMV.Vector3 Position
        {
            get
            {
                if (m_legacyPrim != null)
                    return m_legacyPrim.RawPosition;
                return m_lastPosition;
            }
            set
            {
                if (m_legacyPrim != null)
                {
                    m_legacyPrim.RawPosition = value;
                }
                m_lastPosition = value;
            }
        }

        public OMV.Quaternion Rotation
        {
            get
            {
                if (m_legacyPrim != null)
                    return m_legacyPrim.RawOrientation;
                return m_lastRotation;
            }
            set
            {
                if (m_legacyPrim != null)
                {
                    m_legacyPrim.RawOrientation = value;
                }
                m_lastRotation = value;
            }
        }

        public OMV.Vector3 LinearVelocity
        {
            get
            {
                if (m_legacyPrim != null)
                    return m_legacyPrim.RawVelocity;
                return m_lastLinearVelocity;
            }
        }

        public OMV.Vector3 AngularVelocity
        {
            get
            {
                if (m_legacyPrim != null)
                    return m_legacyPrim.RawRotationalVelocity;
                return m_lastAngularVelocity;
            }
        }

        public Vehicle VehicleType
        {
            get => m_vehicleType;
            set
            {
                if (m_vehicleType != value)
                {
                    m_vehicleType = value;
                    if (m_legacyVehicle != null)
                    {
                        m_adapter.Vehicle_SetType(m_legacyVehicle, value);
                    }
                    SetupDefaultWheels(); // Reconfigure wheels for new vehicle type
                }
            }
        }

        public object UserData { get; set; }

        public void SetVehicleParameter(int param, float value)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                m_floatParameters[param] = value;
                
                if (m_legacyVehicle != null)
                {
                    m_adapter.Vehicle_SetFloatParam(m_legacyVehicle, (Vehicle)param, value);
                }

                // Handle advanced parameters
                ProcessAdvancedFloatParameter(param, value);
                
                m_log.DebugFormat("{0}: Set vehicle float parameter {1} = {2}", LogHeader, param, value);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to set vehicle parameter: {1}", LogHeader, ex.Message);
            }
        }

        public void SetVehicleVectorParameter(int param, OMV.Vector3 value)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                m_vectorParameters[param] = value;
                
                if (m_legacyVehicle != null)
                {
                    m_adapter.Vehicle_SetVectorParam(m_legacyVehicle, (Vehicle)param, value);
                }

                // Handle advanced parameters
                ProcessAdvancedVectorParameter(param, value);
                
                m_log.DebugFormat("{0}: Set vehicle vector parameter {1} = {2}", LogHeader, param, value);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to set vehicle vector parameter: {1}", LogHeader, ex.Message);
            }
        }

        public void SetVehicleRotationParameter(int param, OMV.Quaternion value)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                m_rotationParameters[param] = value;
                
                if (m_legacyVehicle != null)
                {
                    m_adapter.Vehicle_SetRotationParam(m_legacyVehicle, (Vehicle)param, value);
                }
                
                m_log.DebugFormat("{0}: Set vehicle rotation parameter {1} = {2}", LogHeader, param, value);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to set vehicle rotation parameter: {1}", LogHeader, ex.Message);
            }
        }

        public void SetVehicleFloatParameter(int param, float value)
        {
            SetVehicleParameter(param, value);
        }

        public void ProcessVehicleFlags(int flags, bool remove)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                if (remove)
                    m_vehicleFlags &= ~flags;
                else
                    m_vehicleFlags |= flags;

                if (m_legacyVehicle != null)
                {
                    m_adapter.Vehicle_ProcessFlags(m_legacyVehicle, flags, remove);
                }
                
                m_log.DebugFormat("{0}: {1} vehicle flags {2}", LogHeader, remove ? "Removed" : "Added", flags);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to process vehicle flags: {1}", LogHeader, ex.Message);
            }
        }

        public void Reset()
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                // Clear all parameters
                m_floatParameters.Clear();
                m_vectorParameters.Clear();
                m_rotationParameters.Clear();
                m_vehicleFlags = 0;
                
                // Reset vehicle type to none
                m_vehicleType = Vehicle.TYPE_NONE;
                
                // Reset advanced features
                m_wheels.Clear();
                InitializeDefaultAerodynamics();
                
                if (m_legacyVehicle != null)
                {
                    m_adapter.Vehicle_Reset(m_legacyVehicle);
                }
                
                m_log.DebugFormat("{0}: Reset vehicle controller for LocalID {1}", LogHeader, LocalID);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to reset vehicle: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Advanced Vehicle Features

        private void ProcessAdvancedFloatParameter(int param, float value)
        {
            try
            {
                // Handle engine and brake parameters for tire simulation
                if (m_tireSimulationEnabled && m_legacyVehicle != null)
                {
                    switch (param)
                    {
                        case (int)Vehicle.ANGULAR_DEFLECTION_EFFICIENCY: // Using existing Vehicle enum
                            m_enginePower = value;
                            m_adapter.Vehicle_SetEnginePower(m_legacyVehicle, value);
                            break;
                        case (int)Vehicle.ANGULAR_DEFLECTION_TIMESCALE: // Using existing Vehicle enum
                            m_brakeForce = value;
                            m_adapter.Vehicle_SetBrakeForce(m_legacyVehicle, value);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error processing advanced float parameter: {1}", LogHeader, ex.Message);
            }
        }

        private void ProcessAdvancedVectorParameter(int param, OMV.Vector3 value)
        {
            try
            {
                // Handle aerodynamics parameters
                if (m_aerodynamicsEnabled && m_legacyVehicle != null)
                {
                    switch (param)
                    {
                        case (int)Vehicle.LINEAR_FRICTION_TIMESCALE: // Using existing Vehicle enum
                            // Use this as drag coefficient
                            m_aerodynamics.DragCoefficient = value.Length();
                            m_adapter.Vehicle_SetDragCoefficient(m_legacyVehicle, value);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error processing advanced vector parameter: {1}", LogHeader, ex.Message);
            }
        }

        private void SetupDefaultWheels()
        {
            try
            {
                m_wheels.Clear();
                
                switch (m_vehicleType)
                {
                    case Vehicle.TYPE_CAR:
                        SetupCarWheels();
                        break;
                    case Vehicle.TYPE_BOAT:
                        // Boats don't need wheels
                        break;
                    case Vehicle.TYPE_AIRPLANE:
                        SetupAirplaneWheels();
                        break;
                    case Vehicle.TYPE_BALLOON:
                        // Balloons don't need wheels
                        break;
                    default:
                        // Generic vehicle setup
                        SetupGenericWheels();
                        break;
                }
                
                m_log.DebugFormat("{0}: Setup {1} wheels for vehicle type {2}", 
                    LogHeader, m_wheels.Count, m_vehicleType);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error setting up wheels: {1}", LogHeader, ex.Message);
            }
        }

        private void SetupCarWheels()
        {
            // Setup typical 4-wheel car configuration
            float wheelRadius = 0.35f;
            float suspensionStiffness = 20.0f;
            float suspensionDamping = 2.3f;
            float suspensionRestLength = 0.6f;
            float maxSuspensionForce = 4000.0f;
            float frictionSlip = 10.5f;

            // Front wheels (steering)
            m_wheels.Add(new WheelInfo
            {
                Position = new OMV.Vector3(-1.0f, 0.8f, -0.3f),
                Radius = wheelRadius,
                SuspensionStiffness = suspensionStiffness,
                SuspensionDamping = suspensionDamping,
                SuspensionRestLength = suspensionRestLength,
                MaxSuspensionForce = maxSuspensionForce,
                FrictionSlip = frictionSlip,
                IsSteering = true,
                IsPowered = false,
                HasBrake = true
            });

            m_wheels.Add(new WheelInfo
            {
                Position = new OMV.Vector3(-1.0f, -0.8f, -0.3f),
                Radius = wheelRadius,
                SuspensionStiffness = suspensionStiffness,
                SuspensionDamping = suspensionDamping,
                SuspensionRestLength = suspensionRestLength,
                MaxSuspensionForce = maxSuspensionForce,
                FrictionSlip = frictionSlip,
                IsSteering = true,
                IsPowered = false,
                HasBrake = true
            });

            // Rear wheels (powered)
            m_wheels.Add(new WheelInfo
            {
                Position = new OMV.Vector3(1.0f, 0.8f, -0.3f),
                Radius = wheelRadius,
                SuspensionStiffness = suspensionStiffness,
                SuspensionDamping = suspensionDamping,
                SuspensionRestLength = suspensionRestLength,
                MaxSuspensionForce = maxSuspensionForce,
                FrictionSlip = frictionSlip,
                IsSteering = false,
                IsPowered = true,
                HasBrake = true
            });

            m_wheels.Add(new WheelInfo
            {
                Position = new OMV.Vector3(1.0f, -0.8f, -0.3f),
                Radius = wheelRadius,
                SuspensionStiffness = suspensionStiffness,
                SuspensionDamping = suspensionDamping,
                SuspensionRestLength = suspensionRestLength,
                MaxSuspensionForce = maxSuspensionForce,
                FrictionSlip = frictionSlip,
                IsSteering = false,
                IsPowered = true,
                HasBrake = true
            });
        }

        private void SetupAirplaneWheels()
        {
            // Setup landing gear
            float wheelRadius = 0.4f;
            float suspensionStiffness = 30.0f;
            float suspensionDamping = 3.0f;
            float suspensionRestLength = 0.8f;
            float maxSuspensionForce = 6000.0f;
            float frictionSlip = 8.0f;

            // Main landing gear
            m_wheels.Add(new WheelInfo
            {
                Position = new OMV.Vector3(1.0f, 1.5f, -1.0f),
                Radius = wheelRadius,
                SuspensionStiffness = suspensionStiffness,
                SuspensionDamping = suspensionDamping,
                SuspensionRestLength = suspensionRestLength,
                MaxSuspensionForce = maxSuspensionForce,
                FrictionSlip = frictionSlip,
                IsSteering = false,
                IsPowered = false,
                HasBrake = true
            });

            m_wheels.Add(new WheelInfo
            {
                Position = new OMV.Vector3(1.0f, -1.5f, -1.0f),
                Radius = wheelRadius,
                SuspensionStiffness = suspensionStiffness,
                SuspensionDamping = suspensionDamping,
                SuspensionRestLength = suspensionRestLength,
                MaxSuspensionForce = maxSuspensionForce,
                FrictionSlip = frictionSlip,
                IsSteering = false,
                IsPowered = false,
                HasBrake = true
            });

            // Nose wheel (steerable)
            m_wheels.Add(new WheelInfo
            {
                Position = new OMV.Vector3(-2.0f, 0.0f, -1.0f),
                Radius = 0.3f,
                SuspensionStiffness = suspensionStiffness,
                SuspensionDamping = suspensionDamping,
                SuspensionRestLength = suspensionRestLength * 0.8f,
                MaxSuspensionForce = maxSuspensionForce * 0.5f,
                FrictionSlip = frictionSlip,
                IsSteering = true,
                IsPowered = false,
                HasBrake = true
            });
        }

        private void SetupGenericWheels()
        {
            // Simple 4-wheel setup for generic vehicles
            for (int i = 0; i < 4; i++)
            {
                float x = (i < 2) ? -0.8f : 0.8f;
                float y = ((i % 2) == 0) ? 0.6f : -0.6f;
                
                m_wheels.Add(new WheelInfo
                {
                    Position = new OMV.Vector3(x, y, -0.3f),
                    Radius = 0.3f,
                    SuspensionStiffness = 15.0f,
                    SuspensionDamping = 2.0f,
                    SuspensionRestLength = 0.5f,
                    MaxSuspensionForce = 3000.0f,
                    FrictionSlip = 10.0f,
                    IsSteering = (i < 2),
                    IsPowered = (i >= 2),
                    HasBrake = true
                });
            }
        }

        private void InitializeDefaultAerodynamics()
        {
            m_aerodynamics.DragCoefficient = 0.3f;
            m_aerodynamics.LiftCoefficient = 0.1f;
            m_aerodynamics.FrontalArea = 2.0f;
            m_aerodynamics.CenterOfPressure = OMV.Vector3.Zero;
            m_aerodynamics.AirDensity = 1.225f; // kg/m³ at sea level
        }

        #endregion

        #region IPoolable Implementation

        void IPoolable.Reset()
        {
            if (m_disposed)
                return;

            try
            {
                // Reset to default state for object pooling
                m_legacyPrim = null;
                m_legacyVehicle = null;
                m_adapter = null;
                m_scene = null;
                m_definition = null;
                m_initialized = false;
                
                m_lastPosition = OMV.Vector3.Zero;
                m_lastRotation = OMV.Quaternion.Identity;
                m_lastLinearVelocity = OMV.Vector3.Zero;
                m_lastAngularVelocity = OMV.Vector3.Zero;
                m_vehicleType = Vehicle.TYPE_NONE;
                
                m_advancedSuspensionEnabled = false;
                m_tireSimulationEnabled = false;
                m_aerodynamicsEnabled = false;
                
                m_floatParameters.Clear();
                m_vectorParameters.Clear();
                m_rotationParameters.Clear();
                m_vehicleFlags = 0;
                
                m_wheels.Clear();
                InitializeDefaultAerodynamics();
                
                m_enginePower = 0;
                m_brakeForce = 0;
                m_steeringAngle = 0;
                m_lastUpdate = DateTime.UtcNow;
                
                UserData = null;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error during reset: {1}", LogHeader, ex.Message);
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
                // Clean up resources
                m_legacyPrim = null;
                m_legacyVehicle = null;
                m_adapter = null;
                m_scene = null;
                m_definition = null;
                UserData = null;
                
                m_floatParameters?.Clear();
                m_vectorParameters?.Clear();
                m_rotationParameters?.Clear();
                m_wheels?.Clear();
                
                m_initialized = false;
                m_disposed = true;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion
    }
}