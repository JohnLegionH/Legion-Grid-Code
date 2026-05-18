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
using System.Reflection;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Modern rigid body implementation with enhanced features
    /// Provides object pooling support and modern physics capabilities
    /// </summary>
    public class ModernRigidBody : IPhysicsBody, IPoolable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MODERN RIGID BODY]";

        #region Private Fields

        private BSPrimLinkable m_legacyPrim;
        private LegacyApiAdapter m_adapter;
        private BSScene m_scene;
        private RigidBodyDefinition m_definition;
        private bool m_initialized;
        private bool m_disposed;

        // Enhanced physics properties
        private OMV.Vector3 m_lastPosition;
        private OMV.Quaternion m_lastRotation;
        private OMV.Vector3 m_lastLinearVelocity;
        private OMV.Vector3 m_lastAngularVelocity;
        private DateTime m_lastUpdateTime;
        private bool m_ccdEnabled;

        #endregion

        #region Constructor

        public ModernRigidBody()
        {
            m_lastUpdateTime = DateTime.UtcNow;
        }

        #endregion

        #region Initialization

        public void Initialize(RigidBodyDefinition definition, BSAPITemplate legacyAPI, BSScene scene)
        {
            if (m_initialized)
                throw new InvalidOperationException("ModernRigidBody already initialized");

            try
            {
                m_definition = definition ?? throw new ArgumentNullException(nameof(definition));
                m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
                m_adapter = new LegacyApiAdapter(legacyAPI, scene);

                // Find or create the corresponding legacy primitive
                BSPrimLinkable legacyPrim = null;
                if (scene.PhysObjects.TryGetValue(definition.LocalID, out BSPhysObject physObj))
                {
                    legacyPrim = physObj as BSPrimLinkable;
                }

                if (legacyPrim == null)
                {
                    m_log.WarnFormat("{0}: No existing legacy primitive found for LocalID {1}, creating wrapper", 
                        LogHeader, definition.LocalID);
                    
                    // In a full implementation, we would create a new BSPrimLinkable here
                    // For now, we'll track that we don't have a legacy object
                }

                m_legacyPrim = legacyPrim;
                
                // Initialize enhanced properties
                m_lastPosition = definition.Position;
                m_lastRotation = definition.Rotation;
                m_lastLinearVelocity = OMV.Vector3.Zero;
                m_lastAngularVelocity = OMV.Vector3.Zero;
                m_ccdEnabled = definition.EnableCCD;

                m_initialized = true;
                m_log.DebugFormat("{0}: Initialized ModernRigidBody for LocalID {1}", LogHeader, definition.LocalID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to initialize ModernRigidBody: {1}", LogHeader, ex.Message);
                throw;
            }
        }

        #endregion

        #region IPhysicsBody Implementation

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
            set
            {
                if (m_legacyPrim != null)
                {
                    m_legacyPrim.RawVelocity = value;
                }
                m_lastLinearVelocity = value;
            }
        }

        public OMV.Vector3 AngularVelocity
        {
            get
            {
                if (m_legacyPrim != null)
                    return m_adapter.RigidBody_GetAngularVelocity(m_legacyPrim);
                return m_lastAngularVelocity;
            }
            set
            {
                if (m_legacyPrim != null)
                {
                    m_adapter.RigidBody_SetAngularVelocity(m_legacyPrim, value);
                }
                m_lastAngularVelocity = value;
            }
        }

        public float Mass
        {
            get
            {
                if (m_legacyPrim != null)
                    return m_legacyPrim.Mass;
                return m_definition?.Mass ?? 1.0f;
            }
            set
            {
                if (m_definition != null)
                    m_definition.Mass = value;

                if (m_legacyPrim != null)
                {
                    m_log.WarnFormat("{0}: The mass of a legacy prim cannot be changed after creation. The new mass value is stored but not applied to the physics engine.", LogHeader);
                }
            }
        }

        public bool IsActive
        {
            get
            {
                if (m_legacyPrim != null)
                    return m_legacyPrim.IsPhysicallyActive;
                return true;
            }
            set
            {
                if (m_legacyPrim != null)
                {
                    if (value)
                        m_legacyPrim.ActivateIfPhysical(false);
                    else
                        m_adapter.RigidBody_Deactivate(m_legacyPrim);
                }
            }
        }

        public bool IsStatic
        {
            get
            {
                return m_definition?.IsStatic ?? false;
            }
        }

        public object UserData { get; set; }

        public void ApplyForce(OMV.Vector3 force)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                if (m_legacyPrim != null)
                {
                    m_adapter.RigidBody_ApplyForce(m_legacyPrim, force, isImpulse: false);
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to apply force: {1}", LogHeader, ex.Message);
            }
        }

        public void ApplyForceAtPosition(OMV.Vector3 force, OMV.Vector3 position)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                if (m_legacyPrim != null)
                {
                    // Calculate relative position
                    OMV.Vector3 relativePos = position - Position;
                    
                    // Apply force
                    m_adapter.RigidBody_ApplyForce(m_legacyPrim, force, isImpulse: false);
                    
                    // Apply resulting torque
                    OMV.Vector3 torque = OMV.Vector3.Cross(relativePos, force);
                    m_adapter.RigidBody_ApplyAngularForce(m_legacyPrim, torque, isImpulse: false);
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to apply force at position: {1}", LogHeader, ex.Message);
            }
        }

        public void ApplyTorque(OMV.Vector3 torque)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                if (m_legacyPrim != null)
                {
                    m_adapter.RigidBody_ApplyAngularForce(m_legacyPrim, torque, isImpulse: false);
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to apply torque: {1}", LogHeader, ex.Message);
            }
        }

        public void ApplyImpulse(OMV.Vector3 impulse)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                if (m_legacyPrim != null)
                {
                    m_adapter.RigidBody_ApplyForce(m_legacyPrim, impulse, isImpulse: true);
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to apply impulse: {1}", LogHeader, ex.Message);
            }
        }

        public void SetMaterial(float friction, float restitution)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                if (m_legacyPrim != null)
                {
                    m_legacyPrim.Friction = friction;
                    m_legacyPrim.Restitution = restitution;
                    
                    // Update the definition
                    if (m_definition != null)
                    {
                        m_definition.Friction = friction;
                        m_definition.Restitution = restitution;
                    }
                    
                    // Force update of physics properties
                    m_legacyPrim.ForceBodyShapeRebuild(false);
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to set material: {1}", LogHeader, ex.Message);
            }
        }

        public void SetDamping(float linear, float angular)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                if (m_legacyPrim != null)
                {
                    m_adapter.RigidBody_SetDamping(m_legacyPrim, linear, angular);
                    
                    // Update the definition
                    if (m_definition != null)
                    {
                        m_definition.LinearDamping = linear;
                        m_definition.AngularDamping = angular;
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to set damping: {1}", LogHeader, ex.Message);
            }
        }

        public void EnableCCD(bool enable)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                m_ccdEnabled = enable;
                
                if (m_legacyPrim != null)
                {
                    // Enable continuous collision detection
                    if (enable)
                    {
                        float ccdThreshold = 0.1f; // Small threshold for CCD activation
                        float ccdRadius = Math.Max(m_legacyPrim.Size.X, Math.Max(m_legacyPrim.Size.Y, m_legacyPrim.Size.Z)) * 0.5f;
                        m_adapter.RigidBody_SetCcd(m_legacyPrim, ccdThreshold, ccdRadius);
                    }
                    else
                    {
                        m_adapter.RigidBody_SetCcd(m_legacyPrim, 0f, 0f);
                    }
                }
                
                // Update the definition
                if (m_definition != null)
                    m_definition.EnableCCD = enable;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to set CCD: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region IPoolable Implementation

        public void Reset()
        {
            if (m_disposed)
                return;

            try
            {
                // Reset to default state for object pooling
                m_legacyPrim = null;
                m_adapter = null;
                m_scene = null;
                m_definition = null;
                m_initialized = false;
                
                m_lastPosition = OMV.Vector3.Zero;
                m_lastRotation = OMV.Quaternion.Identity;
                m_lastLinearVelocity = OMV.Vector3.Zero;
                m_lastAngularVelocity = OMV.Vector3.Zero;
                m_lastUpdateTime = DateTime.UtcNow;
                m_ccdEnabled = false;
                
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
                m_adapter = null;
                m_scene = null;
                m_definition = null;
                UserData = null;
                
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