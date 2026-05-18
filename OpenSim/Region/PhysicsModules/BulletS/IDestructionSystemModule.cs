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
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Interface for the destruction physics system module.
    /// Allows other modules to interact with the destruction system safely.
    /// </summary>
    public interface IDestructionSystemModule : IRegionModuleBase
    {
        /// <summary>
        /// Is the destruction system enabled and ready
        /// </summary>
        bool IsEnabled { get; }
        
        /// <summary>
        /// Current version of the destruction system
        /// </summary>
        string Version { get; }
        
        /// <summary>
        /// Register an object as destructible with specific parameters
        /// </summary>
        /// <param name="objectID">Object ID to make destructible</param>
        /// <param name="material">Material type for destruction behavior</param>
        /// <param name="threshold">Destruction threshold</param>
        /// <returns>True if successfully registered</returns>
        bool RegisterDestructibleObject(uint objectID, DestructibleMaterial material, float threshold);
        
        /// <summary>
        /// Unregister an object from destruction system
        /// </summary>
        /// <param name="objectID">Object ID to remove</param>
        /// <returns>True if successfully unregistered</returns>
        bool UnregisterDestructibleObject(uint objectID);
        
        /// <summary>
        /// Trigger destruction of an object manually
        /// </summary>
        /// <param name="objectID">Object to destroy</param>
        /// <param name="impactPoint">Point of impact</param>
        /// <param name="force">Force applied</param>
        /// <param name="cause">Cause of destruction</param>
        /// <returns>True if destruction was triggered</returns>
        bool TriggerDestruction(uint objectID, Vector3 impactPoint, Vector3 force, DestructionCause cause);
        
        /// <summary>
        /// Get destruction system statistics
        /// </summary>
        /// <returns>Dictionary of statistics</returns>
        Dictionary<string, object> GetStatistics();
        
        /// <summary>
        /// Get list of currently destructible objects
        /// </summary>
        /// <returns>List of object IDs</returns>
        List<uint> GetDestructibleObjects();
        
        /// <summary>
        /// Event fired when an object is destroyed
        /// </summary>
        event Action<uint, Vector3, DestructibleMaterial, UUID, DestructionCause> OnObjectDestroyed;
        
        /// <summary>
        /// Event fired when fragments are created
        /// </summary>
        event Action<uint, List<DestructionFragment>> OnFragmentsCreated;
        
        /// <summary>
        /// Check if an object is currently registered as destructible
        /// </summary>
        /// <param name="objectID">Object ID to check</param>
        /// <returns>True if object is destructible</returns>
        bool IsObjectDestructible(uint objectID);
        
        /// <summary>
        /// Get destruction system performance metrics
        /// </summary>
        /// <returns>Performance report string</returns>
        string GetPerformanceReport();
    }
}