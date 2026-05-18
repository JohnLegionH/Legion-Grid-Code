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
    /// Modern character controller implementation with enhanced avatar physics
    /// Provides advanced ground detection, movement prediction, and smooth character control
    /// </summary>
    public class ModernCharacterController : ICharacterController, IPoolable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MODERN CHARACTER CONTROLLER]";

        #region Private Fields

        private BSCharacter m_legacyCharacter;
        private LegacyApiAdapter m_adapter;
        private BSScene m_scene;
        private CharacterDefinition m_definition;
        private bool m_initialized;
        private bool m_disposed;

        // Enhanced character properties
        private OMV.Vector3 m_lastPosition;
        private OMV.Vector3 m_lastVelocity;
        private OMV.Vector3 m_targetVelocity;
        private bool m_wasOnGround;
        private bool m_isOnGround;
        private bool m_isMoving;
        private float m_groundDistance;
        private OMV.Vector3 m_groundNormal;
        private DateTime m_lastGroundCheck;
        private DateTime m_lastMovement;

        // Movement prediction and smoothing
        private OMV.Vector3 m_predictedPosition;
        private float m_responseTime;
        private float m_currentGravity;

        // Ground detection enhancement
        private readonly float GroundCheckInterval = 0.1f; // Check ground every 100ms
        private readonly float MovementThreshold = 0.01f; // Minimum velocity to consider moving

        #endregion

        #region Constructor

        public ModernCharacterController()
        {
            m_lastGroundCheck = DateTime.UtcNow;
            m_lastMovement = DateTime.UtcNow;
            m_groundNormal = OMV.Vector3.UnitZ;
            m_currentGravity = -9.8f;
        }

        #endregion

        #region Initialization

        public void Initialize(CharacterDefinition definition, BSAPITemplate legacyAPI, BSScene scene)
        {
            if (m_initialized)
                throw new InvalidOperationException("ModernCharacterController already initialized");

            try
            {
                m_definition = definition ?? throw new ArgumentNullException(nameof(definition));
                m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
                m_adapter = new LegacyApiAdapter(legacyAPI, scene);

                // Find the corresponding legacy character
                BSCharacter legacyCharacter = null;
                if (scene.PhysObjects.TryGetValue(definition.LocalID, out BSPhysObject physObj))
                {
                    legacyCharacter = physObj as BSCharacter;
                }

                if (legacyCharacter == null)
                {
                    m_log.WarnFormat("{0}: No existing legacy character found for LocalID {1}", 
                        LogHeader, definition.LocalID);
                }

                m_legacyCharacter = legacyCharacter;

                // Initialize enhanced properties
                m_lastPosition = definition.Position;
                m_lastVelocity = OMV.Vector3.Zero;
                m_targetVelocity = OMV.Vector3.Zero;
                m_predictedPosition = definition.Position;
                m_responseTime = definition.ResponseTime;
                m_isOnGround = false;
                m_wasOnGround = false;
                m_isMoving = false;
                m_groundDistance = float.MaxValue;

                m_initialized = true;
                m_log.DebugFormat("{0}: Initialized ModernCharacterController for LocalID {1}", LogHeader, definition.LocalID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Failed to initialize ModernCharacterController: {1}", LogHeader, ex.Message);
                throw;
            }
        }

        #endregion

        #region ICharacterController Implementation

        public uint LocalID => m_definition?.LocalID ?? 0;

        public OMV.Vector3 Position
        {
            get
            {
                if (m_legacyCharacter != null)
                    return m_legacyCharacter.RawPosition;
                return m_lastPosition;
            }
            set
            {
                if (m_legacyCharacter != null)
                {
                    m_legacyCharacter.RawPosition = value;
                }
                m_lastPosition = value;
                UpdateGroundDetection();
            }
        }

        public OMV.Vector3 LinearVelocity
        {
            get
            {
                if (m_legacyCharacter != null)
                    return m_legacyCharacter.RawVelocity;
                return m_lastVelocity;
            }
        }

        public bool IsOnGround
        {
            get
            {
                UpdateGroundDetection();
                return m_isOnGround;
            }
        }

        public bool IsMoving
        {
            get
            {
                UpdateMovementState();
                return m_isMoving;
            }
        }

        public object UserData { get; set; }

        public void Move(OMV.Vector3 displacement, float deltaTime)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                m_lastMovement = DateTime.UtcNow;
                
                // Calculate target velocity from displacement
                OMV.Vector3 targetVelocity = displacement / deltaTime;
                
                // Apply movement prediction if enabled
                if (m_definition.EnableMovementPrediction)
                {
                    targetVelocity = PredictMovement(targetVelocity, deltaTime);
                }

                // Apply the movement
                if (m_legacyCharacter != null)
                {
                    // Use the legacy character's movement system
                    m_legacyCharacter.AddForce(displacement, false);
                    m_targetVelocity = targetVelocity;
                }
                else
                {
                    // Update position directly if no legacy character
                    Position += displacement;
                }

                // Update movement prediction
                UpdateMovementPrediction(deltaTime);
                
                m_log.DebugFormat("{0}: Character {1} moved by {2}, target velocity {3}", 
                    LogHeader, LocalID, displacement, targetVelocity);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to move character: {1}", LogHeader, ex.Message);
            }
        }

        public void Jump(float force)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                // Only allow jumping if on ground (with some tolerance)
                if (!IsOnGround && m_groundDistance > 0.5f)
                {
                    m_log.DebugFormat("{0}: Character {1} attempted to jump while not on ground (distance: {2})", 
                        LogHeader, LocalID, m_groundDistance);
                    return;
                }

                if (m_legacyCharacter != null)
                {
                    m_adapter.Character_ApplyJump(m_legacyCharacter, force);
                    m_log.DebugFormat("{0}: Character {1} jumping with force {2}", LogHeader, LocalID, force);
                }
                else
                {
                    // Apply jump velocity directly
                    m_lastVelocity = new OMV.Vector3(m_lastVelocity.X, m_lastVelocity.Y, force);
                }

                // Mark as no longer on ground immediately
                m_isOnGround = false;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to jump: {1}", LogHeader, ex.Message);
            }
        }

        public void SetGravity(float gravity)
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                m_currentGravity = gravity;

                if (m_legacyCharacter != null)
                {
                    m_adapter.Character_SetGravity(m_legacyCharacter, gravity);
                }

                m_log.DebugFormat("{0}: Set gravity for character {1} to {2}", LogHeader, LocalID, gravity);
            }
            catch (Exception ex)
            { 
                m_log.WarnFormat("{0}: Failed to set gravity: {1}", LogHeader, ex.Message);
            }
        }

        public bool CanStepUp(float height)
        {
            if (!m_initialized || m_disposed)
                return false;

            try
            {
                float maxStepHeight = m_definition?.StepHeight ?? 0.5f;
                return height <= maxStepHeight && IsOnGround;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error checking step up capability: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        public float GetGroundDistance()
        {
            UpdateGroundDetection();
            return m_groundDistance;
        }

        public OMV.Vector3 GetGroundNormal()
        {
            UpdateGroundDetection();
            return m_groundNormal;
        }

        #endregion

        #region Private Methods

        private void UpdateGroundDetection()
        {
            if (!m_initialized || m_disposed)
                return;

            DateTime now = DateTime.UtcNow;
            if ((now - m_lastGroundCheck).TotalSeconds < GroundCheckInterval)
                return;

            m_lastGroundCheck = now;

            try
            {
                m_wasOnGround = m_isOnGround;

                if (m_definition.UseAdvancedGroundDetection && m_adapter != null)
                {
                    // Perform ray cast downward to detect ground
                    OMV.Vector3 startPos = Position + new OMV.Vector3(0, 0, 0.1f); // Start slightly above character
                    OMV.Vector3 endPos = startPos - new OMV.Vector3(0, 0, 2.0f); // Cast down 2 meters

                    bool hit = m_adapter.Character_RayCast(startPos, endPos, out OMV.Vector3 hitPoint, out OMV.Vector3 hitNormal);

                    if (hit)
                    {
                        m_groundDistance = (startPos - hitPoint).Length();
                        m_groundNormal = hitNormal;
                        
                        // Consider on ground if within reasonable distance
                        m_isOnGround = m_groundDistance <= 0.5f;
                    }
                    else
                    {
                        m_groundDistance = float.MaxValue;
                        m_groundNormal = OMV.Vector3.UnitZ;
                        m_isOnGround = false;
                    }
                }
                else if (m_legacyCharacter != null)
                {
                    // Fall back to legacy character's ground detection
                    m_isOnGround = m_legacyCharacter.Flying == false; // Simplified check
                    m_groundDistance = m_isOnGround ? 0.0f : float.MaxValue;
                    m_groundNormal = OMV.Vector3.UnitZ;
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error updating ground detection: {1}", LogHeader, ex.Message);
                // Default to safe values
                m_isOnGround = true;
                m_groundDistance = 0.0f;
                m_groundNormal = OMV.Vector3.UnitZ;
            }
        }

        private void UpdateMovementState()
        {
            if (!m_initialized || m_disposed)
                return;

            try
            {
                OMV.Vector3 currentVelocity = LinearVelocity;
                float speed = currentVelocity.Length();
                
                m_isMoving = speed > MovementThreshold;
                m_lastVelocity = currentVelocity;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error updating movement state: {1}", LogHeader, ex.Message);
                m_isMoving = false;
            }
        }

        private OMV.Vector3 PredictMovement(OMV.Vector3 targetVelocity, float deltaTime)
        {
            try
            {
                // Simple movement prediction based on response time
                float alpha = Math.Min(deltaTime / m_responseTime, 1.0f);
                
                // Interpolate between current and target velocity
                OMV.Vector3 predictedVelocity = m_lastVelocity * (1.0f - alpha) + targetVelocity * alpha;
                
                return predictedVelocity;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error predicting movement: {1}", LogHeader, ex.Message);
                return targetVelocity;
            }
        }

        private void UpdateMovementPrediction(float deltaTime)
        {
            try
            {
                // Update predicted position
                m_predictedPosition = Position + LinearVelocity * deltaTime;
                
                // Apply gravity prediction if not on ground
                if (!IsOnGround)
                {
                    OMV.Vector3 gravityEffect = new OMV.Vector3(0, 0, m_currentGravity * deltaTime);
                    m_predictedPosition += gravityEffect * deltaTime * 0.5f; // Apply half gravity for prediction
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error updating movement prediction: {1}", LogHeader, ex.Message);
                m_predictedPosition = Position;
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
                m_legacyCharacter = null;
                m_adapter = null;
                m_scene = null;
                m_definition = null;
                m_initialized = false;
                
                m_lastPosition = OMV.Vector3.Zero;
                m_lastVelocity = OMV.Vector3.Zero;
                m_targetVelocity = OMV.Vector3.Zero;
                m_predictedPosition = OMV.Vector3.Zero;
                
                m_wasOnGround = false;
                m_isOnGround = false;
                m_isMoving = false;
                m_groundDistance = float.MaxValue;
                m_groundNormal = OMV.Vector3.UnitZ;
                
                m_responseTime = 0.1f;
                m_currentGravity = -9.8f;
                
                m_lastGroundCheck = DateTime.UtcNow;
                m_lastMovement = DateTime.UtcNow;
                
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
                m_legacyCharacter = null;
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