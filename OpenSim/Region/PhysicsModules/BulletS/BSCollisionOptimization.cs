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
using System.Collections.Concurrent;
using System.Reflection;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Advanced collision detection optimization system
    /// Provides batched collision processing, spatial optimization, and reduced overhead
    /// </summary>
    public static class BSCollisionOptimization
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[COLLISION OPTIMIZATION]";

        #region Configuration

        private static readonly int MaxCollisionCacheSize = 1000;
        private static readonly float CollisionDistanceThreshold = 100.0f;
        private static readonly int CollisionCacheTimeoutFrames = 30;

        #endregion

        #region Data Structures

        /// <summary>
        /// Optimized collision data for batched processing
        /// </summary>
        public struct OptimizedCollisionData
        {
            public uint ObjectA;
            public uint ObjectB;
            public OMV.Vector3 ContactPoint;
            public OMV.Vector3 ContactNormal;
            public float Penetration;
            public int FrameNumber;

            public OptimizedCollisionData(uint a, uint b, OMV.Vector3 point, OMV.Vector3 normal, float penetration, int frame)
            {
                ObjectA = a;
                ObjectB = b;
                ContactPoint = point;
                ContactNormal = normal;
                Penetration = penetration;
                FrameNumber = frame;
            }
        }

        /// <summary>
        /// Collision processing statistics
        /// </summary>
        public struct CollisionStats
        {
            public int TotalCollisions;
            public int BatchedCollisions;
            public int CachedCollisions;
            public int FilteredCollisions;
            public float ProcessingTimeMs;
            public float OptimizationRatio;
        }

        /// <summary>
        /// Spatial collision cache for reducing redundant collision processing
        /// </summary>
        private class SpatialCollisionCache
        {
            private readonly Dictionary<ulong, int> collisionCache = new Dictionary<ulong, int>();
            private readonly Queue<ulong> cacheTimeouts = new Queue<ulong>();
            
            public bool HasRecentCollision(uint objectA, uint objectB, int currentFrame)
            {
                ulong key = GenerateCollisionKey(objectA, objectB);
                if (collisionCache.TryGetValue(key, out int lastFrame))
                {
                    return (currentFrame - lastFrame) < CollisionCacheTimeoutFrames;
                }
                return false;
            }

            public void RecordCollision(uint objectA, uint objectB, int currentFrame)
            {
                ulong key = GenerateCollisionKey(objectA, objectB);
                collisionCache[key] = currentFrame;
                cacheTimeouts.Enqueue(key);

                // Cleanup old entries
                if (collisionCache.Count > MaxCollisionCacheSize)
                {
                    CleanupOldEntries(currentFrame);
                }
            }

            private void CleanupOldEntries(int currentFrame)
            {
                int removed = 0;
                while (cacheTimeouts.Count > 0 && removed < CollisionCacheTimeoutFrames)
                {
                    ulong key = cacheTimeouts.Dequeue();
                    if (collisionCache.TryGetValue(key, out int frame))
                    {
                        if (currentFrame - frame >= CollisionCacheTimeoutFrames)
                        {
                            collisionCache.Remove(key);
                            removed++;
                        }
                        else
                        {
                            // Put it back since it's still valid
                            cacheTimeouts.Enqueue(key);
                            break;
                        }
                    }
                }
            }

            private static ulong GenerateCollisionKey(uint objectA, uint objectB)
            {
                // Ensure consistent ordering for bidirectional collisions
                if (objectA > objectB)
                {
                    uint temp = objectA;
                    objectA = objectB;
                    objectB = temp;
                }
                return ((ulong)objectA << 32) | objectB;
            }
        }

        #endregion

        #region State Management

        private static SpatialCollisionCache spatialCache = new SpatialCollisionCache();
        private static List<OptimizedCollisionData> collisionBatch = new List<OptimizedCollisionData>();
        private static CollisionStats currentStats = new CollisionStats();
        private static int currentFrameNumber = 0;

        #endregion

        #region Public API

        /// <summary>
        /// Initialize collision optimization system
        /// </summary>
        public static void Initialize()
        {
            spatialCache = new SpatialCollisionCache();
            collisionBatch.Clear();
            currentStats = new CollisionStats();
            currentFrameNumber = 0;
            
            m_log.InfoFormat("{0}: Collision optimization system initialized", LogHeader);
        }

        /// <summary>
        /// Process collision with optimizations
        /// </summary>
        public static bool ProcessOptimizedCollision(uint objectA, uint objectB, OMV.Vector3 point, 
            OMV.Vector3 normal, float penetration, OMV.Vector3 positionA, OMV.Vector3 positionB)
        {
            currentStats.TotalCollisions++;

            // Check spatial cache for recent collisions
            if (spatialCache.HasRecentCollision(objectA, objectB, currentFrameNumber))
            {
                currentStats.CachedCollisions++;
                return false; // Skip redundant collision
            }

            // Distance-based filtering for performance
            float distance = OMV.Vector3.Distance(positionA, positionB);
            if (distance > CollisionDistanceThreshold)
            {
                currentStats.FilteredCollisions++;
                return false; // Skip distant collisions
            }

            // Add to batch for processing
            collisionBatch.Add(new OptimizedCollisionData(objectA, objectB, point, normal, penetration, currentFrameNumber));
            currentStats.BatchedCollisions++;

            // Record in spatial cache
            spatialCache.RecordCollision(objectA, objectB, currentFrameNumber);

            return true;
        }

        /// <summary>
        /// Process batched collisions for better performance
        /// </summary>
        public static List<OptimizedCollisionData> FlushCollisionBatch()
        {
            var batchToProcess = new List<OptimizedCollisionData>(collisionBatch);
            collisionBatch.Clear();
            return batchToProcess;
        }

        /// <summary>
        /// Advance frame counter and update statistics
        /// </summary>
        public static void AdvanceFrame()
        {
            currentFrameNumber++;
            
            // Calculate optimization ratio
            if (currentStats.TotalCollisions > 0)
            {
                int processedCollisions = currentStats.TotalCollisions - currentStats.CachedCollisions - currentStats.FilteredCollisions;
                currentStats.OptimizationRatio = 1.0f - ((float)processedCollisions / currentStats.TotalCollisions);
            }
        }

        /// <summary>
        /// Get current collision processing statistics
        /// </summary>
        public static CollisionStats GetCollisionStats()
        {
            return currentStats;
        }

        /// <summary>
        /// Reset collision statistics
        /// </summary>
        public static void ResetStats()
        {
            currentStats = new CollisionStats();
        }

        #endregion

        #region Avatar Collision Optimization

        /// <summary>
        /// Optimized avatar collision processing
        /// Reduces the overhead from the avatar collision kludge
        /// </summary>
        public static class AvatarCollisionOptimizer
        {
            private static readonly Dictionary<uint, int> lastAvatarCollisionFrame = new Dictionary<uint, int>();
            private static readonly int AvatarCollisionSkipFrames = 3; // Send avatar collisions every 3 frames instead of every frame

            /// <summary>
            /// Check if avatar should send collision update this frame
            /// </summary>
            public static bool ShouldSendAvatarCollision(uint avatarID, int currentFrame)
            {
                if (!lastAvatarCollisionFrame.TryGetValue(avatarID, out int lastFrame))
                {
                    lastAvatarCollisionFrame[avatarID] = currentFrame;
                    return true;
                }

                if (currentFrame - lastFrame >= AvatarCollisionSkipFrames)
                {
                    lastAvatarCollisionFrame[avatarID] = currentFrame;
                    return true;
                }

                return false;
            }

            /// <summary>
            /// Remove avatar from tracking when it leaves the scene
            /// </summary>
            public static void RemoveAvatar(uint avatarID)
            {
                lastAvatarCollisionFrame.Remove(avatarID);
            }

            /// <summary>
            /// Get optimization statistics for avatar collisions
            /// </summary>
            public static AvatarCollisionStats GetAvatarStats()
            {
                return new AvatarCollisionStats
                {
                    TrackedAvatars = lastAvatarCollisionFrame.Count,
                    SkipFrames = AvatarCollisionSkipFrames,
                    OptimizationRatio = 1.0f - (1.0f / AvatarCollisionSkipFrames)
                };
            }
        }

        /// <summary>
        /// Avatar collision optimization statistics
        /// </summary>
        public struct AvatarCollisionStats
        {
            public int TrackedAvatars;
            public int SkipFrames;
            public float OptimizationRatio;
        }

        #endregion

        #region Spatial Optimization

        /// <summary>
        /// Spatial grid for collision culling based on proximity
        /// </summary>
        public static class SpatialGrid
        {
            private static readonly float GridCellSize = 25.0f;
            private static readonly Dictionary<long, HashSet<uint>> spatialGrid = new Dictionary<long, HashSet<uint>>();

            /// <summary>
            /// Update object position in spatial grid
            /// </summary>
            public static void UpdateObjectPosition(uint objectID, OMV.Vector3 position)
            {
                long gridKey = GetGridKey(position);
                
                // Remove from old cell if exists
                RemoveFromAllCells(objectID);
                
                // Add to new cell
                if (!spatialGrid.TryGetValue(gridKey, out HashSet<uint> cellObjects))
                {
                    cellObjects = new HashSet<uint>();
                    spatialGrid[gridKey] = cellObjects;
                }
                cellObjects.Add(objectID);
            }

            /// <summary>
            /// Remove object from spatial grid
            /// </summary>
            public static void RemoveObject(uint objectID)
            {
                RemoveFromAllCells(objectID);
            }

            /// <summary>
            /// Check if two objects are in nearby grid cells for collision culling
            /// </summary>
            public static bool AreInNearbyGridCells(OMV.Vector3 posA, OMV.Vector3 posB)
            {
                long gridKeyA = GetGridKey(posA);
                long gridKeyB = GetGridKey(posB);
                
                // Check if in same cell or adjacent cells
                return Math.Abs((gridKeyA >> 32) - (gridKeyB >> 32)) <= 1 &&
                       Math.Abs((gridKeyA & 0xFFFFFFFF) - (gridKeyB & 0xFFFFFFFF)) <= 1;
            }

            private static long GetGridKey(OMV.Vector3 position)
            {
                int x = (int)(position.X / GridCellSize);
                int y = (int)(position.Y / GridCellSize);
                return ((long)x << 32) | (uint)y;
            }

            private static void RemoveFromAllCells(uint objectID)
            {
                var cellsToClean = new List<long>();
                foreach (var kvp in spatialGrid)
                {
                    if (kvp.Value.Remove(objectID) && kvp.Value.Count == 0)
                    {
                        cellsToClean.Add(kvp.Key);
                    }
                }
                
                // Clean empty cells
                foreach (long key in cellsToClean)
                {
                    spatialGrid.Remove(key);
                }
            }
        }

        #endregion
    }
}