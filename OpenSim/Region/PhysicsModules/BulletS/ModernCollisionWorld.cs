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
using System.Linq;
using System.Reflection;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Modern collision world implementation providing enhanced spatial queries
    /// Supports ray testing, overlap tests, and sweep tests with improved performance
    /// </summary>
    public class ModernCollisionWorld : ICollisionWorld, IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MODERN COLLISION WORLD]";

        #region Private Fields

        private LegacyApiAdapter m_adapter;
        private BSScene m_scene;
        private bool m_disposed;

        // Enhanced collision detection
        private readonly Dictionary<uint, CollisionObjectInfo> m_collisionObjects;
        private readonly object m_collisionObjectsLock;

        // Performance optimization
        private DateTime m_lastCacheUpdate;
        private readonly float CacheUpdateInterval = 0.1f; // Update cache every 100ms
        private readonly int MaxRayTestDistance = 1000; // Maximum ray test distance in meters

        #endregion

        #region Nested Classes

        private class CollisionObjectInfo
        {
            public uint LocalID;
            public OMV.Vector3 Position;
            public OMV.Quaternion Rotation;
            public OMV.Vector3 Size;
            public BSPhysicsShapeType ShapeType;
            public CollisionType CollisionGroup;
            public CollisionType CollisionMask;
            public DateTime LastUpdate;
            public BoundingBox AABB;
        }

        private struct BoundingBox
        {
            public OMV.Vector3 Min;
            public OMV.Vector3 Max;

            public BoundingBox(OMV.Vector3 center, OMV.Vector3 extents)
            {
                Min = center - extents * 0.5f;
                Max = center + extents * 0.5f;
            }

            public bool Contains(OMV.Vector3 point)
            {
                return point.X >= Min.X && point.X <= Max.X &&
                       point.Y >= Min.Y && point.Y <= Max.Y &&
                       point.Z >= Min.Z && point.Z <= Max.Z;
            }

            public bool Intersects(BoundingBox other)
            {
                return !(Min.X > other.Max.X || Max.X < other.Min.X ||
                        Min.Y > other.Max.Y || Max.Y < other.Min.Y ||
                        Min.Z > other.Max.Z || Max.Z < other.Min.Z);
            }

            public bool IntersectsSphere(OMV.Vector3 center, float radius)
            {
                float dx = Math.Max(0, Math.Max(Min.X - center.X, center.X - Max.X));
                float dy = Math.Max(0, Math.Max(Min.Y - center.Y, center.Y - Max.Y));
                float dz = Math.Max(0, Math.Max(Min.Z - center.Z, center.Z - Max.Z));
                return (dx * dx + dy * dy + dz * dz) <= (radius * radius);
            }
        }

        #endregion

        #region Constructor

        public ModernCollisionWorld(BSAPITemplate legacyAPI, BSScene scene)
        {
            m_adapter = new LegacyApiAdapter(legacyAPI, scene);
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            
            m_collisionObjects = new Dictionary<uint, CollisionObjectInfo>();
            m_collisionObjectsLock = new object();
            m_lastCacheUpdate = DateTime.UtcNow;

            m_log.InfoFormat("{0}: Modern collision world initialized", LogHeader);
        }

        #endregion

        #region ICollisionWorld Implementation

        public bool RayTest(OMV.Vector3 from, OMV.Vector3 to, out OMV.Vector3 hitPoint, out OMV.Vector3 hitNormal, out uint hitObject)
        {
            hitPoint = OMV.Vector3.Zero;
            hitNormal = OMV.Vector3.Zero;
            hitObject = 0;

            try
            {
                // Validate input parameters
                if (from == to)
                {
                    m_log.WarnFormat("{0}: Ray test with identical start and end points", LogHeader);
                    return false;
                }

                OMV.Vector3 direction = to - from;
                float distance = direction.Length();
                
                if (distance > MaxRayTestDistance)
                {
                    m_log.WarnFormat("{0}: Ray test distance {1} exceeds maximum {2}", 
                        LogHeader, distance, MaxRayTestDistance);
                    // Clamp to maximum distance
                    to = from + (direction / distance) * MaxRayTestDistance;
                    distance = MaxRayTestDistance;
                }

                // Update collision object cache if needed
                UpdateCollisionObjectCache();

                // Perform enhanced ray test
                bool hit = PerformEnhancedRayTest(from, to, out hitPoint, out hitNormal, out hitObject);

                if (hit)
                {
                    m_log.DebugFormat("{0}: Ray hit object {1} at {2}, normal {3}", 
                        LogHeader, hitObject, hitPoint, hitNormal);
                }

                return hit;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during ray test: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        public List<uint> OverlapTest(OMV.Vector3 position, float radius)
        {
            List<uint> overlappingObjects = new List<uint>();

            try
            {
                // Validate input parameters
                if (radius <= 0)
                {
                    m_log.WarnFormat("{0}: Invalid radius for overlap test: {1}", LogHeader, radius);
                    return overlappingObjects;
                }

                // Update collision object cache if needed
                UpdateCollisionObjectCache();

                // Perform enhanced overlap test
                lock (m_collisionObjectsLock)
                {
                    foreach (var kvp in m_collisionObjects)
                    {
                        var obj = kvp.Value;
                        
                        // Check if object's AABB intersects with the test sphere
                        if (obj.AABB.IntersectsSphere(position, radius))
                        {
                            // More precise sphere-to-shape intersection test
                            if (TestSphereToShape(position, radius, obj))
                            {
                                overlappingObjects.Add(obj.LocalID);
                            }
                        }
                    }
                }

                m_log.DebugFormat("{0}: Overlap test found {1} objects within radius {2} at {3}", 
                    LogHeader, overlappingObjects.Count, radius, position);

                return overlappingObjects;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during overlap test: {1}", LogHeader, ex.Message);
                return overlappingObjects;
            }
        }

        public List<uint> BoxOverlapTest(OMV.Vector3 center, OMV.Vector3 extents)
        {
            List<uint> overlappingObjects = new List<uint>();

            try
            {
                // Validate input parameters
                if (extents.X <= 0 || extents.Y <= 0 || extents.Z <= 0)
                {
                    m_log.WarnFormat("{0}: Invalid extents for box overlap test: {1}", LogHeader, extents);
                    return overlappingObjects;
                }

                // Update collision object cache if needed
                UpdateCollisionObjectCache();

                // Create test bounding box
                BoundingBox testBox = new BoundingBox(center, extents);

                // Perform enhanced box overlap test
                lock (m_collisionObjectsLock)
                {
                    foreach (var kvp in m_collisionObjects)
                    {
                        var obj = kvp.Value;
                        
                        // Check if object's AABB intersects with the test box
                        if (obj.AABB.Intersects(testBox))
                        {
                            overlappingObjects.Add(obj.LocalID);
                        }
                    }
                }

                m_log.DebugFormat("{0}: Box overlap test found {1} objects within box at {2} with extents {3}", 
                    LogHeader, overlappingObjects.Count, center, extents);

                return overlappingObjects;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during box overlap test: {1}", LogHeader, ex.Message);
                return overlappingObjects;
            }
        }

        public bool SweepTest(OMV.Vector3 from, OMV.Vector3 to, OMV.Vector3 extents, out OMV.Vector3 hitPoint)
        {
            hitPoint = OMV.Vector3.Zero;

            try
            {
                // Validate input parameters
                if (from == to)
                {
                    m_log.WarnFormat("{0}: Sweep test with identical start and end points", LogHeader);
                    return false;
                }

                if (extents.X <= 0 || extents.Y <= 0 || extents.Z <= 0)
                {
                    m_log.WarnFormat("{0}: Invalid extents for sweep test: {1}", LogHeader, extents);
                    return false;
                }

                // Update collision object cache if needed
                UpdateCollisionObjectCache();

                // Perform enhanced sweep test
                return PerformEnhancedSweepTest(from, to, extents, out hitPoint);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during sweep test: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        #endregion

        #region Private Methods

        private void UpdateCollisionObjectCache()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - m_lastCacheUpdate).TotalSeconds < CacheUpdateInterval)
                return;

            try
            {
                lock (m_collisionObjectsLock)
                {
                    // Clear old cache
                    m_collisionObjects.Clear();

                    // Rebuild cache from current physics objects
                    foreach (var kvp in m_scene.PhysObjects)
                    {
                        var physObj = kvp.Value;
                        if (physObj == null || !physObj.IsPhysicallyActive)
                            continue;

                        var collisionInfo = new CollisionObjectInfo
                        {
                            LocalID = physObj.LocalID,
                            Position = physObj.RawPosition,
                            Rotation = physObj.RawOrientation,
                            Size = physObj.Size,
                            ShapeType = physObj.PhysicalActors.TryGetActor(BSPrim.LockedAxisActorName, out BSActor _) ? 
                                       BSPhysicsShapeType.SHAPE_BOX : BSPhysicsShapeType.SHAPE_UNKNOWN,
                            CollisionGroup = CollisionType.Dynamic, // Default group
                            CollisionMask = CollisionType.Dynamic,  // Default mask
                            LastUpdate = now
                        };

                        // Calculate AABB
                        collisionInfo.AABB = new BoundingBox(collisionInfo.Position, collisionInfo.Size);

                        m_collisionObjects[physObj.LocalID] = collisionInfo;
                    }
                }

                m_lastCacheUpdate = now;
                m_log.DebugFormat("{0}: Updated collision object cache with {1} objects", 
                    LogHeader, m_collisionObjects.Count);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error updating collision object cache: {1}", LogHeader, ex.Message);
            }
        }

        private bool PerformEnhancedRayTest(OMV.Vector3 from, OMV.Vector3 to, out OMV.Vector3 hitPoint, out OMV.Vector3 hitNormal, out uint hitObject)
        {
            hitPoint = OMV.Vector3.Zero;
            hitNormal = OMV.Vector3.Zero;
            hitObject = 0;

            try
            {
                // Use the adapter for the primary ray test
                bool legacyHit = m_adapter.CollisionWorld_RayCast(from, to, out hitPoint, out hitNormal, out hitObject);

                if (legacyHit)
                {
                    // The adapter now returns the hitObject directly
                    return true;
                }

                // If legacy ray test didn't work, perform manual intersection tests
                return PerformManualRayTest(from, to, out hitPoint, out hitNormal, out hitObject);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error in enhanced ray test: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        private bool PerformManualRayTest(OMV.Vector3 from, OMV.Vector3 to, out OMV.Vector3 hitPoint, out OMV.Vector3 hitNormal, out uint hitObject)
        {
            hitPoint = OMV.Vector3.Zero;
            hitNormal = OMV.Vector3.Zero;
            hitObject = 0;

            OMV.Vector3 direction = to - from;
            float maxDistance = direction.Length();
            direction.Normalize();

            float closestDistance = float.MaxValue;
            bool found = false;

            lock (m_collisionObjectsLock)
            {
                foreach (var kvp in m_collisionObjects)
                {
                    var obj = kvp.Value;
                    
                    // Test ray against object's AABB first (quick rejection)
                    if (RayIntersectsAABB(from, direction, maxDistance, obj.AABB, out float distance))
                    {
                        if (distance < closestDistance)
                        {
                            // More precise intersection test would go here
                            // For now, use the AABB intersection point
                            closestDistance = distance;
                            hitPoint = from + direction * distance;
                            hitNormal = CalculateAABBNormal(hitPoint, obj.AABB);
                            hitObject = obj.LocalID;
                            found = true;
                        }
                    }
                }
            }

            return found;
        }

        private bool PerformEnhancedSweepTest(OMV.Vector3 from, OMV.Vector3 to, OMV.Vector3 extents, out OMV.Vector3 hitPoint)
        {
            hitPoint = OMV.Vector3.Zero;

            try
            {
                OMV.Vector3 direction = to - from;
                float maxDistance = direction.Length();
                direction.Normalize();

                // Create swept bounding box
                BoundingBox sweptBox = new BoundingBox(from, extents);
                float closestDistance = float.MaxValue;
                bool found = false;

                lock (m_collisionObjectsLock)
                {
                    foreach (var kvp in m_collisionObjects)
                    {
                        var obj = kvp.Value;
                        
                        // Test if the swept box would intersect with this object
                        if (SweptBoxIntersects(from, to, extents, obj.AABB, out float distance))
                        {
                            if (distance < closestDistance)
                            {
                                closestDistance = distance;
                                hitPoint = from + direction * distance;
                                found = true;
                            }
                        }
                    }
                }

                return found;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error in enhanced sweep test: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        private bool TestSphereToShape(OMV.Vector3 sphereCenter, float radius, CollisionObjectInfo obj)
        {
            try
            {
                // For now, use simplified sphere-to-AABB test
                return obj.AABB.IntersectsSphere(sphereCenter, radius);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error in sphere-to-shape test: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        private uint IdentifyHitObject(OMV.Vector3 hitPoint)
        {
            try
            {
                // Find the closest object to the hit point
                uint closestObject = 0;
                float closestDistance = float.MaxValue;

                lock (m_collisionObjectsLock)
                {
                    foreach (var kvp in m_collisionObjects)
                    {
                        var obj = kvp.Value;
                        
                        if (obj.AABB.Contains(hitPoint))
                        {
                            float distance = (obj.Position - hitPoint).Length();
                            if (distance < closestDistance)
                            {
                                closestDistance = distance;
                                closestObject = obj.LocalID;
                            }
                        }
                    }
                }

                return closestObject;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error identifying hit object: {1}", LogHeader, ex.Message);
                return 0;
            }
        }

        private bool RayIntersectsAABB(OMV.Vector3 origin, OMV.Vector3 direction, float maxDistance, BoundingBox aabb, out float distance)
        {
            distance = 0;

            try
            {
                // Ray-AABB intersection test using slab method
                float tmin = (aabb.Min.X - origin.X) / direction.X;
                float tmax = (aabb.Max.X - origin.X) / direction.X;

                if (tmin > tmax)
                {
                    float temp = tmin;
                    tmin = tmax;
                    tmax = temp;
                }

                float tymin = (aabb.Min.Y - origin.Y) / direction.Y;
                float tymax = (aabb.Max.Y - origin.Y) / direction.Y;

                if (tymin > tymax)
                {
                    float temp = tymin;
                    tymin = tymax;
                    tymax = temp;
                }

                if (tmin > tymax || tymin > tmax)
                    return false;

                if (tymin > tmin)
                    tmin = tymin;

                if (tymax < tmax)
                    tmax = tymax;

                float tzmin = (aabb.Min.Z - origin.Z) / direction.Z;
                float tzmax = (aabb.Max.Z - origin.Z) / direction.Z;

                if (tzmin > tzmax)
                {
                    float temp = tzmin;
                    tzmin = tzmax;
                    tzmax = temp;
                }

                if (tmin > tzmax || tzmin > tmax)
                    return false;

                if (tzmin > tmin)
                    tmin = tzmin;

                if (tzmax < tmax)
                    tmax = tzmax;

                distance = tmin >= 0 ? tmin : tmax;
                return distance >= 0 && distance <= maxDistance;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error in ray-AABB intersection test: {1}", LogHeader, ex.Message);
                return false;
            }
        }

        private OMV.Vector3 CalculateAABBNormal(OMV.Vector3 hitPoint, BoundingBox aabb)
        {
            try
            {
                // Calculate which face of the AABB was hit
                OMV.Vector3 center = (aabb.Min + aabb.Max) * 0.5f;
                OMV.Vector3 extents = (aabb.Max - aabb.Min) * 0.5f;
                OMV.Vector3 local = hitPoint - center;

                // Normalize to unit cube
                local.X /= extents.X;
                local.Y /= extents.Y;
                local.Z /= extents.Z;

                // Find the axis with the largest absolute value
                float absX = Math.Abs(local.X);
                float absY = Math.Abs(local.Y);
                float absZ = Math.Abs(local.Z);

                if (absX >= absY && absX >= absZ)
                    return new OMV.Vector3(local.X > 0 ? 1 : -1, 0, 0);
                else if (absY >= absZ)
                    return new OMV.Vector3(0, local.Y > 0 ? 1 : -1, 0);
                else
                    return new OMV.Vector3(0, 0, local.Z > 0 ? 1 : -1);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error calculating AABB normal: {1}", LogHeader, ex.Message);
                return OMV.Vector3.UnitZ;
            }
        }

        private bool SweptBoxIntersects(OMV.Vector3 from, OMV.Vector3 to, OMV.Vector3 extents, BoundingBox targetAABB, out float distance)
        {
            distance = 0;

            try
            {
                // Expand the target AABB by the swept box extents
                BoundingBox expandedAABB = new BoundingBox(
                    (targetAABB.Min + targetAABB.Max) * 0.5f,
                    (targetAABB.Max - targetAABB.Min) + extents
                );

                // Perform ray test against expanded AABB
                OMV.Vector3 direction = to - from;
                float maxDistance = direction.Length();
                direction.Normalize();

                return RayIntersectsAABB(from, direction, maxDistance, expandedAABB, out distance);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error in swept box intersection test: {1}", LogHeader, ex.Message);
                return false;
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
                lock (m_collisionObjectsLock)
                {
                    m_collisionObjects.Clear();
                }

                m_adapter = null;
                m_scene = null;
                m_disposed = true;

                m_log.InfoFormat("{0}: Modern collision world disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion
    }
}