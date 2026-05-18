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
using OMV = OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Adapter to translate calls from modern physics classes to the legacy BulletSim API.
    /// This class isolates the compatibility logic, allowing the modern classes to remain clean.
    /// </summary>
    public class LegacyApiAdapter
    {
        private readonly BSAPITemplate m_legacyAPI;
        private readonly BSScene m_scene;

        public LegacyApiAdapter(BSAPITemplate legacyAPI, BSScene scene)
        {
            m_legacyAPI = legacyAPI;
            m_scene = scene;
        }

        #region Rigid Body Adapters

        public void RigidBody_ApplyForce(BSPrimLinkable prim, OMV.Vector3 force, bool isImpulse)
        {
            if (isImpulse)
                m_legacyAPI.ApplyCentralImpulse(prim.PhysBody, force);
            else
                m_legacyAPI.ApplyCentralForce(prim.PhysBody, force);
        }

        public void RigidBody_ApplyAngularForce(BSPrimLinkable prim, OMV.Vector3 torque, bool isImpulse)
        {
            if (isImpulse)
                m_legacyAPI.ApplyTorqueImpulse(prim.PhysBody, torque);
            else
                m_legacyAPI.ApplyTorque(prim.PhysBody, torque);
        }

        public void RigidBody_SetDamping(BSPrimLinkable prim, float linear, float angular)
        {
            m_legacyAPI.SetDamping(prim.PhysBody, linear, angular);
        }

        public void RigidBody_SetCcd(BSPrimLinkable prim, float motionThreshold, float sweptRadius)
        {
            m_legacyAPI.SetCcdMotionThreshold(prim.PhysBody, motionThreshold);
            m_legacyAPI.SetCcdSweptSphereRadius(prim.PhysBody, sweptRadius);
        }

        public OMV.Vector3 RigidBody_GetAngularVelocity(BSPrimLinkable prim)
        {
            return m_legacyAPI.GetAngularVelocity(prim.PhysBody);
        }

        public void RigidBody_SetAngularVelocity(BSPrimLinkable prim, OMV.Vector3 angularVelocity)
        {
            m_legacyAPI.SetAngularVelocity(prim.PhysBody, angularVelocity);
        }

        public void RigidBody_Deactivate(BSPrimLinkable prim)
        {
            m_legacyAPI.ForceActivationState(prim.PhysBody, ActivationState.ISLAND_SLEEPING);
        }

        #endregion

        #region Character Controller Adapters

        public void Character_ApplyJump(BSCharacter character, float force)
        {
            OMV.Vector3 jumpImpulse = new OMV.Vector3(0, 0, force);
            m_legacyAPI.ApplyCentralImpulse(character.PhysBody, jumpImpulse);
        }

        public void Character_SetGravity(BSCharacter character, float gravity)
        {
            OMV.Vector3 gravityVector = new OMV.Vector3(0, 0, gravity);
            m_legacyAPI.SetGravity(character.PhysBody, gravityVector);
        }

        public bool Character_RayCast(OMV.Vector3 from, OMV.Vector3 to, out OMV.Vector3 hitPoint, out OMV.Vector3 hitNormal)
        {
            RaycastHit hitInfo = m_legacyAPI.RayTest2(m_scene.World, from, to, (uint)CollisionFilterGroups.BCharacterGroup, (uint)(CollisionFilterGroups.BStaticGroup | CollisionFilterGroups.BDefaultGroup));
            if (hitInfo.hasHit())
            {
                hitPoint = hitInfo.Point;
                hitNormal = hitInfo.Normal;
                return true;
            }
            hitPoint = OMV.Vector3.Zero;
            hitNormal = OMV.Vector3.Zero;
            return false;
        }

        #endregion

        #region Collision World Adapters

        public bool CollisionWorld_RayCast(OMV.Vector3 from, OMV.Vector3 to, out OMV.Vector3 hitPoint, out OMV.Vector3 hitNormal, out uint hitObject)
        {
            RaycastHit hitInfo = m_legacyAPI.RayTest2(m_scene.World, from, to, (uint)CollisionFilterGroups.BAllGroup, (uint)CollisionFilterGroups.BAllGroup);
            if (hitInfo.hasHit())
            {
                hitPoint = hitInfo.Point;
                hitNormal = hitInfo.Normal;
                hitObject = hitInfo.ID;
                return true;
            }
            hitPoint = OMV.Vector3.Zero;
            hitNormal = OMV.Vector3.Zero;
            hitObject = 0;
            return false;
        }

        #endregion

        #region Vehicle Controller Adapters

        public void Vehicle_SetEnginePower(BSDynamics vehicle, float power)
        {
            vehicle.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_EFFICIENCY, power);
        }

        public void Vehicle_SetBrakeForce(BSDynamics vehicle, float force)
        {
            vehicle.ProcessFloatVehicleParam(Vehicle.ANGULAR_DEFLECTION_TIMESCALE, force);
        }

        public void Vehicle_SetDragCoefficient(BSDynamics vehicle, OMV.Vector3 coefficient)
        {
            vehicle.ProcessVectorVehicleParam(Vehicle.LINEAR_FRICTION_TIMESCALE, coefficient);
        }

        public void Vehicle_ProcessFlags(BSDynamics vehicle, int flags, bool remove)
        {
            vehicle.ProcessVehicleFlags(flags, remove);
        }

        public void Vehicle_Reset(BSDynamics vehicle)
        {
            vehicle.ProcessTypeChange(Vehicle.TYPE_NONE);
        }

        public void Vehicle_SetType(BSDynamics vehicle, Vehicle type)
        {
            vehicle.ProcessTypeChange(type);
        }

        public void Vehicle_SetFloatParam(BSDynamics vehicle, Vehicle param, float value)
        {
            vehicle.ProcessFloatVehicleParam(param, value);
        }

        public void Vehicle_SetVectorParam(BSDynamics vehicle, Vehicle param, OMV.Vector3 value)
        {
            vehicle.ProcessVectorVehicleParam(param, value);
        }

        public void Vehicle_SetRotationParam(BSDynamics vehicle, Vehicle param, OMV.Quaternion value)
        {
            vehicle.ProcessRotationVehicleParam(param, value);
        }

        #endregion
    }
}
