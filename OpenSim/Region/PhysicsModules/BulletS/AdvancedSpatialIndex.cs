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
    /// Spatial object representation for indexing
    /// </summary>
    public class SpatialObject
    {
        public uint ID { get; set; }
        public OMV.Vector3 Position { get; set; }
        public OMV.Vector3 Size { get; set; }
        public OMV.Vector3 Velocity { get; set; }
        public float Mass { get; set; }
        public string ObjectType { get; set; }
        public DateTime LastUpdate { get; set; }
        public object UserData { get; set; }

        public SpatialObject()
        {
            LastUpdate = DateTime.UtcNow;
            Size = OMV.Vector3.One;
        }

        public OMV.Vector3 MinBounds => Position - Size * 0.5f;
        public OMV.Vector3 MaxBounds => Position + Size * 0.5f;

        public bool IntersectsWith(SpatialObject other)
        {
            var thisMin = MinBounds;
            var thisMax = MaxBounds;
            var otherMin = other.MinBounds;
            var otherMax = other.MaxBounds;

            return !(thisMin.X > otherMax.X || thisMax.X < otherMin.X ||
                    thisMin.Y > otherMax.Y || thisMax.Y < otherMin.Y ||
                    thisMin.Z > otherMax.Z || thisMax.Z < otherMin.Z);
        }

        public bool IntersectsWith(OMV.Vector3 min, OMV.Vector3 max)
        {
            var thisMin = MinBounds;
            var thisMax = MaxBounds;

            return !(thisMin.X > max.X || thisMax.X < min.X ||
                    thisMin.Y > max.Y || thisMax.Y < min.Y ||
                    thisMin.Z > max.Z || thisMax.Z < min.Z);
        }
    }

    /// <summary>
    /// Octree node for advanced spatial partitioning
    /// </summary>
    public class OctreeNode
    {
        public OMV.Vector3 Center { get; private set; }
        public OMV.Vector3 HalfSize { get; private set; }
        public int Depth { get; private set; }
        public bool IsLeaf => Children == null;

        private readonly List<SpatialObject> m_objects;
        private OctreeNode[] m_children;
        private readonly int m_maxObjectsPerNode;
        private readonly int m_maxDepth;
        private readonly object m_lock;

        public OctreeNode(OMV.Vector3 center, OMV.Vector3 halfSize, int depth = 0, int maxObjectsPerNode = 10, int maxDepth = 8)
        {
            Center = center;
            HalfSize = halfSize;
            Depth = depth;
            m_maxObjectsPerNode = maxObjectsPerNode;
            m_maxDepth = maxDepth;
            m_objects = new List<SpatialObject>();
            m_lock = new object();
        }

        public List<SpatialObject> Objects
        {
            get
            {
                lock (m_lock)
                {
                    return new List<SpatialObject>(m_objects);
                }
            }
        }

        public OctreeNode[] Children
        {
            get
            {
                lock (m_lock)
                {
                    return m_children;
                }
            }
        }

        public bool Insert(SpatialObject obj)
        {
            lock (m_lock)
            {
                if (!ContainsBounds(obj.MinBounds, obj.MaxBounds))
                    return false;

                // If we're at max depth or have few objects, store here
                if (Depth >= m_maxDepth || m_objects.Count < m_maxObjectsPerNode)
                {
                    m_objects.Add(obj);
                    return true;
                }

                // Need to subdivide if we haven't already
                if (m_children == null)
                {
                    Subdivide();
                }

                // Try to insert into children
                foreach (var child in m_children)
                {
                    if (child.Insert(obj))
                        return true;
                }

                // If object doesn't fit in any child, store in this node
                m_objects.Add(obj);
                return true;
            }
        }

        public bool Remove(SpatialObject obj)
        {
            lock (m_lock)
            {
                if (m_objects.Remove(obj))
                    return true;

                if (m_children != null)
                {
                    foreach (var child in m_children)
                    {
                        if (child.Remove(obj))
                            return true;
                    }
                }

                return false;
            }
        }

        public List<SpatialObject> Query(OMV.Vector3 min, OMV.Vector3 max)
        {
            var results = new List<SpatialObject>();
            QueryInternal(min, max, results);
            return results;
        }

        public List<SpatialObject> QueryRadius(OMV.Vector3 center, float radius)
        {
            var min = center - new OMV.Vector3(radius);
            var max = center + new OMV.Vector3(radius);
            var candidates = Query(min, max);
            var results = new List<SpatialObject>();

            foreach (var obj in candidates)
            {
                float distance = (obj.Position - center).Length();
                if (distance <= radius)
                {
                    results.Add(obj);
                }
            }

            return results;
        }

        public void Clear()
        {
            lock (m_lock)
            {
                m_objects.Clear();
                if (m_children != null)
                {
                    foreach (var child in m_children)
                    {
                        child.Clear();
                    }
                    m_children = null;
                }
            }
        }

        public int GetObjectCount()
        {
            lock (m_lock)
            {
                int count = m_objects.Count;
                if (m_children != null)
                {
                    foreach (var child in m_children)
                    {
                        count += child.GetObjectCount();
                    }
                }
                return count;
            }
        }

        public int GetNodeCount()
        {
            lock (m_lock)
            {
                int count = 1;
                if (m_children != null)
                {
                    foreach (var child in m_children)
                    {
                        count += child.GetNodeCount();
                    }
                }
                return count;
            }
        }

        private void QueryInternal(OMV.Vector3 min, OMV.Vector3 max, List<SpatialObject> results)
        {
            lock (m_lock)
            {
                if (!IntersectsBounds(min, max))
                    return;

                // Check objects in this node
                foreach (var obj in m_objects)
                {
                    if (obj.IntersectsWith(min, max))
                    {
                        results.Add(obj);
                    }
                }

                // Check children
                if (m_children != null)
                {
                    foreach (var child in m_children)
                    {
                        child.QueryInternal(min, max, results);
                    }
                }
            }
        }

        private bool ContainsBounds(OMV.Vector3 min, OMV.Vector3 max)
        {
            var nodeMin = Center - HalfSize;
            var nodeMax = Center + HalfSize;

            return min.X >= nodeMin.X && max.X <= nodeMax.X &&
                   min.Y >= nodeMin.Y && max.Y <= nodeMax.Y &&
                   min.Z >= nodeMin.Z && max.Z <= nodeMax.Z;
        }

        private bool IntersectsBounds(OMV.Vector3 min, OMV.Vector3 max)
        {
            var nodeMin = Center - HalfSize;
            var nodeMax = Center + HalfSize;

            return !(nodeMin.X > max.X || nodeMax.X < min.X ||
                    nodeMin.Y > max.Y || nodeMax.Y < min.Y ||
                    nodeMin.Z > max.Z || nodeMax.Z < min.Z);
        }

        private void Subdivide()
        {
            var quarterSize = HalfSize * 0.5f;
            m_children = new OctreeNode[8];

            // Create 8 child nodes
            m_children[0] = new OctreeNode(Center + new OMV.Vector3(-quarterSize.X, -quarterSize.Y, -quarterSize.Z), quarterSize, Depth + 1, m_maxObjectsPerNode, m_maxDepth);
            m_children[1] = new OctreeNode(Center + new OMV.Vector3(quarterSize.X, -quarterSize.Y, -quarterSize.Z), quarterSize, Depth + 1, m_maxObjectsPerNode, m_maxDepth);
            m_children[2] = new OctreeNode(Center + new OMV.Vector3(-quarterSize.X, quarterSize.Y, -quarterSize.Z), quarterSize, Depth + 1, m_maxObjectsPerNode, m_maxDepth);
            m_children[3] = new OctreeNode(Center + new OMV.Vector3(quarterSize.X, quarterSize.Y, -quarterSize.Z), quarterSize, Depth + 1, m_maxObjectsPerNode, m_maxDepth);
            m_children[4] = new OctreeNode(Center + new OMV.Vector3(-quarterSize.X, -quarterSize.Y, quarterSize.Z), quarterSize, Depth + 1, m_maxObjectsPerNode, m_maxDepth);
            m_children[5] = new OctreeNode(Center + new OMV.Vector3(quarterSize.X, -quarterSize.Y, quarterSize.Z), quarterSize, Depth + 1, m_maxObjectsPerNode, m_maxDepth);
            m_children[6] = new OctreeNode(Center + new OMV.Vector3(-quarterSize.X, quarterSize.Y, quarterSize.Z), quarterSize, Depth + 1, m_maxObjectsPerNode, m_maxDepth);
            m_children[7] = new OctreeNode(Center + new OMV.Vector3(quarterSize.X, quarterSize.Y, quarterSize.Z), quarterSize, Depth + 1, m_maxObjectsPerNode, m_maxDepth);
        }
    }

    /// <summary>
    /// Advanced spatial indexing system using octree and spatial hashing
    /// </summary>
    public class AdvancedSpatialIndex : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[ADVANCED SPATIAL INDEX]";

        #region Private Fields

        private readonly BSScene m_scene;
        private OctreeNode m_octree;
        private readonly ConcurrentDictionary<uint, SpatialObject> m_trackedObjects;
        private readonly Timer m_optimizationTimer;
        private bool m_disposed;
        private bool m_enabled;

        // Spatial hash grid for fast queries
        private readonly Dictionary<long, HashSet<uint>> m_spatialHashGrid;
        private readonly float m_gridCellSize;
        private readonly object m_gridLock;

        // Performance tracking
        private long m_totalQueries;
        private long m_totalInsertions;
        private long m_totalRemovals;
        private float m_averageQueryTime;
        private float m_averageInsertTime;
        private DateTime m_lastOptimization;

        // Configuration
        private readonly OMV.Vector3 WorldSize = new OMV.Vector3(512, 512, 4096); // Large world support
        private readonly int MaxObjectsPerNode = 15;
        private readonly int MaxOctreeDepth = 10;
        private readonly TimeSpan OptimizationInterval = TimeSpan.FromMinutes(5);

        #endregion

        #region Constructor

        public AdvancedSpatialIndex(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_trackedObjects = new ConcurrentDictionary<uint, SpatialObject>();
            m_spatialHashGrid = new Dictionary<long, HashSet<uint>>();
            m_gridCellSize = 32.0f; // 32m grid cells
            m_gridLock = new object();
            m_lastOptimization = DateTime.UtcNow;

            // Initialize octree covering the world
            var worldCenter = WorldSize * 0.5f;
            var worldHalfSize = WorldSize * 0.5f;
            m_octree = new OctreeNode(worldCenter, worldHalfSize, 0, MaxObjectsPerNode, MaxOctreeDepth);

            // Setup optimization timer
            m_optimizationTimer = new Timer(PerformOptimization, null, OptimizationInterval, OptimizationInterval);

            m_log.InfoFormat("{0}: Advanced spatial index initialized - World size: {1}, Grid cell size: {2}m", 
                LogHeader, WorldSize, m_gridCellSize);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the spatial index
        /// </summary>
        public void Initialize()
        {
            if (m_enabled || m_disposed)
                return;

            m_enabled = true;
            m_log.InfoFormat("{0}: Advanced spatial index started", LogHeader);
        }

        /// <summary>
        /// Add or update an object in the spatial index
        /// </summary>
        public void UpdateObject(uint objectID, OMV.Vector3 position, OMV.Vector3 size, OMV.Vector3 velocity, string objectType = "")
        {
            if (!m_enabled || m_disposed)
                return;

            var startTime = DateTime.UtcNow;

            try
            {
                var spatialObj = m_trackedObjects.GetOrAdd(objectID, _ => new SpatialObject { ID = objectID });
                
                // Update spatial hash grid if position changed significantly
                bool positionChanged = (spatialObj.Position - position).LengthSquared() > 0.01f;
                
                if (positionChanged)
                {
                    RemoveFromSpatialGrid(spatialObj);
                }

                // Update object properties
                spatialObj.Position = position;
                spatialObj.Size = size;
                spatialObj.Velocity = velocity;
                spatialObj.ObjectType = objectType;
                spatialObj.LastUpdate = DateTime.UtcNow;

                if (positionChanged)
                {
                    // Remove from octree and re-insert
                    m_octree.Remove(spatialObj);
                    m_octree.Insert(spatialObj);

                    // Add to spatial hash grid
                    AddToSpatialGrid(spatialObj);
                }

                Interlocked.Increment(ref m_totalInsertions);
                
                var insertTime = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
                m_averageInsertTime = m_averageInsertTime * 0.95f + insertTime * 0.05f;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error updating object {1}: {2}", LogHeader, objectID, ex.Message);
            }
        }

        /// <summary>
        /// Remove an object from the spatial index
        /// </summary>
        public void RemoveObject(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                if (m_trackedObjects.TryRemove(objectID, out SpatialObject spatialObj))
                {
                    m_octree.Remove(spatialObj);
                    RemoveFromSpatialGrid(spatialObj);
                    Interlocked.Increment(ref m_totalRemovals);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error removing object {1}: {2}", LogHeader, objectID, ex.Message);
            }
        }

        /// <summary>
        /// Query objects within a bounding box
        /// </summary>
        public List<SpatialObject> QueryRegion(OMV.Vector3 min, OMV.Vector3 max)
        {
            if (!m_enabled || m_disposed)
                return new List<SpatialObject>();

            var startTime = DateTime.UtcNow;

            try
            {
                var results = m_octree.Query(min, max);
                
                Interlocked.Increment(ref m_totalQueries);
                var queryTime = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
                m_averageQueryTime = m_averageQueryTime * 0.95f + queryTime * 0.05f;

                return results;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error querying region: {1}", LogHeader, ex.Message);
                return new List<SpatialObject>();
            }
        }

        /// <summary>
        /// Query objects within a radius of a point
        /// </summary>
        public List<SpatialObject> QueryRadius(OMV.Vector3 center, float radius)
        {
            if (!m_enabled || m_disposed)
                return new List<SpatialObject>();

            var startTime = DateTime.UtcNow;

            try
            {
                var results = m_octree.QueryRadius(center, radius);
                
                Interlocked.Increment(ref m_totalQueries);
                var queryTime = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
                m_averageQueryTime = m_averageQueryTime * 0.95f + queryTime * 0.05f;

                return results;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error querying radius: {1}", LogHeader, ex.Message);
                return new List<SpatialObject>();
            }
        }

        /// <summary>
        /// Fast query using spatial hash grid for nearby objects
        /// </summary>
        public List<SpatialObject> QueryNearby(OMV.Vector3 position, float radius)
        {
            if (!m_enabled || m_disposed)
                return new List<SpatialObject>();

            var results = new List<SpatialObject>();

            try
            {
                lock (m_gridLock)
                {
                    var cellRadius = (int)Math.Ceiling(radius / m_gridCellSize);
                    var centerCell = GetGridCell(position);

                    for (int x = -cellRadius; x <= cellRadius; x++)
                    {
                        for (int y = -cellRadius; y <= cellRadius; y++)
                        {
                            for (int z = -cellRadius; z <= cellRadius; z++)
                            {
                                var cellKey = GetCellKey(centerCell.X + x, centerCell.Y + y, centerCell.Z + z);
                                
                                if (m_spatialHashGrid.TryGetValue(cellKey, out HashSet<uint> objectIDs))
                                {
                                    foreach (var objectID in objectIDs)
                                    {
                                        if (m_trackedObjects.TryGetValue(objectID, out SpatialObject obj))
                                        {
                                            float distance = (obj.Position - position).Length();
                                            if (distance <= radius)
                                            {
                                                results.Add(obj);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                Interlocked.Increment(ref m_totalQueries);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error querying nearby objects: {1}", LogHeader, ex.Message);
            }

            return results;
        }

        /// <summary>
        /// Get potential collision pairs using broad-phase detection
        /// </summary>
        public List<(SpatialObject, SpatialObject)> GetPotentialCollisionPairs()
        {
            if (!m_enabled || m_disposed)
                return new List<(SpatialObject, SpatialObject)>();

            var pairs = new List<(SpatialObject, SpatialObject)>();

            try
            {
                // Use spatial hash grid for efficient broad-phase
                lock (m_gridLock)
                {
                    foreach (var cellObjects in m_spatialHashGrid.Values)
                    {
                        var objectList = cellObjects.ToList();
                        
                        for (int i = 0; i < objectList.Count; i++)
                        {
                            for (int j = i + 1; j < objectList.Count; j++)
                            {
                                if (m_trackedObjects.TryGetValue(objectList[i], out SpatialObject obj1) &&
                                    m_trackedObjects.TryGetValue(objectList[j], out SpatialObject obj2))
                                {
                                    if (obj1.IntersectsWith(obj2))
                                    {
                                        pairs.Add((obj1, obj2));
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error getting collision pairs: {1}", LogHeader, ex.Message);
            }

            return pairs;
        }

        /// <summary>
        /// Get spatial index performance statistics
        /// </summary>
        public string GetPerformanceReport()
        {
            if (!m_enabled || m_disposed)
                return "Spatial index not active";

            var report = $"Advanced Spatial Index Performance:\\n";
            report += $"  Tracked Objects: {m_trackedObjects.Count}\\n";
            report += $"  Octree Nodes: {m_octree.GetNodeCount()}\\n";
            report += $"  Grid Cells Used: {m_spatialHashGrid.Count}\\n";
            report += $"  Total Queries: {m_totalQueries}\\n";
            report += $"  Total Insertions: {m_totalInsertions}\\n";
            report += $"  Total Removals: {m_totalRemovals}\\n";
            report += $"  Average Query Time: {m_averageQueryTime:F2}ms\\n";
            report += $"  Average Insert Time: {m_averageInsertTime:F2}ms\\n";
            report += $"  Last Optimization: {(DateTime.UtcNow - m_lastOptimization).TotalMinutes:F1} minutes ago\\n";

            return report;
        }

        /// <summary>
        /// Clear all objects from the spatial index
        /// </summary>
        public void Clear()
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                m_octree.Clear();
                m_trackedObjects.Clear();
                
                lock (m_gridLock)
                {
                    m_spatialHashGrid.Clear();
                }

                m_log.InfoFormat("{0}: Spatial index cleared", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error clearing spatial index: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Private Methods

        private (int X, int Y, int Z) GetGridCell(OMV.Vector3 position)
        {
            return (
                (int)Math.Floor(position.X / m_gridCellSize),
                (int)Math.Floor(position.Y / m_gridCellSize),
                (int)Math.Floor(position.Z / m_gridCellSize)
            );
        }

        private long GetCellKey(int x, int y, int z)
        {
            // Hash 3D coordinates to a single long value
            const long prime1 = 73856093;
            const long prime2 = 19349663;
            const long prime3 = 83492791;
            
            return (x * prime1) ^ (y * prime2) ^ (z * prime3);
        }

        private void AddToSpatialGrid(SpatialObject obj)
        {
            lock (m_gridLock)
            {
                var cell = GetGridCell(obj.Position);
                var cellKey = GetCellKey(cell.X, cell.Y, cell.Z);
                
                if (!m_spatialHashGrid.TryGetValue(cellKey, out HashSet<uint> objectSet))
                {
                    objectSet = new HashSet<uint>();
                    m_spatialHashGrid[cellKey] = objectSet;
                }
                
                objectSet.Add(obj.ID);
            }
        }

        private void RemoveFromSpatialGrid(SpatialObject obj)
        {
            lock (m_gridLock)
            {
                var cell = GetGridCell(obj.Position);
                var cellKey = GetCellKey(cell.X, cell.Y, cell.Z);
                
                if (m_spatialHashGrid.TryGetValue(cellKey, out HashSet<uint> objectSet))
                {
                    objectSet.Remove(obj.ID);
                    
                    if (objectSet.Count == 0)
                    {
                        m_spatialHashGrid.Remove(cellKey);
                    }
                }
            }
        }

        private void PerformOptimization(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                // Rebuild octree if it's getting too deep or unbalanced
                var nodeCount = m_octree.GetNodeCount();
                var objectCount = m_octree.GetObjectCount();
                
                if (nodeCount > objectCount * 2) // Too many empty nodes
                {
                    RebuildOctree();
                }

                // Clean up empty grid cells
                CleanupSpatialGrid();

                m_lastOptimization = DateTime.UtcNow;
                
                m_log.DebugFormat("{0}: Spatial index optimization completed - Nodes: {1}, Objects: {2}", 
                    LogHeader, nodeCount, objectCount);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Error during spatial optimization: {1}", LogHeader, ex.Message);
            }
        }

        private void RebuildOctree()
        {
            var allObjects = m_trackedObjects.Values.ToList();
            
            // Create new octree
            var worldCenter = WorldSize * 0.5f;
            var worldHalfSize = WorldSize * 0.5f;
            var newOctree = new OctreeNode(worldCenter, worldHalfSize, 0, MaxObjectsPerNode, MaxOctreeDepth);
            
            // Re-insert all objects
            foreach (var obj in allObjects)
            {
                newOctree.Insert(obj);
            }
            
            // Replace old octree
            m_octree.Clear();
            m_octree = newOctree;
        }

        private void CleanupSpatialGrid()
        {
            lock (m_gridLock)
            {
                var keysToRemove = new List<long>();
                
                foreach (var kvp in m_spatialHashGrid)
                {
                    if (kvp.Value.Count == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var key in keysToRemove)
                {
                    m_spatialHashGrid.Remove(key);
                }
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
                m_optimizationTimer?.Dispose();
                Clear();

                m_disposed = true;
                m_log.InfoFormat("{0}: Advanced spatial index disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public int TrackedObjectCount => m_trackedObjects.Count;
        public int OctreeNodeCount => m_octree.GetNodeCount();
        public int GridCellCount => m_spatialHashGrid.Count;
        public long TotalQueries => m_totalQueries;
        public float AverageQueryTime => m_averageQueryTime;

        #endregion
    }
}