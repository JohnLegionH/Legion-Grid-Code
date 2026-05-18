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
    /// Modern spatial indexing implementation using hierarchical grid structure
    /// Provides efficient spatial queries for large numbers of physics objects
    /// </summary>
    public class ModernSpatialIndex : ISpatialIndex, IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[MODERN SPATIAL INDEX]";

        #region Private Fields

        private readonly Dictionary<uint, SpatialObject> m_objects;
        private readonly SpatialGrid m_grid;
        private readonly object m_objectsLock;
        private bool m_disposed;

        // Configuration
        private readonly float m_cellSize;
        private readonly int m_maxObjectsPerCell;
        private readonly int m_maxDepth;

        // Statistics
        private int m_queryCount;
        private int m_updateCount;
        private DateTime m_lastOptimization;
        private readonly TimeSpan OptimizationInterval = TimeSpan.FromMinutes(5);

        #endregion

        #region Nested Classes

        private class SpatialObject
        {
            public uint ID;
            public OMV.Vector3 Position;
            public OMV.Vector3 Extents;
            public BoundingBox AABB;
            public GridCell CurrentCell;
            public DateTime LastUpdate;
            
            public void UpdateAABB()
            {
                AABB = new BoundingBox(Position, Extents);
            }
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

        private class GridCell
        {
            public int X, Y, Z;
            public BoundingBox Bounds;
            public List<SpatialObject> Objects;
            public GridCell[] Children;
            public GridCell Parent;
            public int Depth;
            public bool IsLeaf => Children == null;

            public GridCell(int x, int y, int z, BoundingBox bounds, int depth, GridCell parent = null)
            {
                X = x;
                Y = y;
                Z = z;
                Bounds = bounds;
                Depth = depth;
                Parent = parent;
                Objects = new List<SpatialObject>();
            }

            public void Subdivide(int maxDepth)
            {
                if (Depth >= maxDepth || !IsLeaf)
                    return;

                Children = new GridCell[8];
                OMV.Vector3 center = (Bounds.Min + Bounds.Max) * 0.5f;
                OMV.Vector3 halfExtent = (Bounds.Max - Bounds.Min) * 0.25f;

                for (int i = 0; i < 8; i++)
                {
                    int dx = (i & 1) == 0 ? -1 : 1;
                    int dy = (i & 2) == 0 ? -1 : 1;
                    int dz = (i & 4) == 0 ? -1 : 1;

                    OMV.Vector3 childCenter = center + new OMV.Vector3(dx * halfExtent.X, dy * halfExtent.Y, dz * halfExtent.Z);
                    BoundingBox childBounds = new BoundingBox(childCenter, halfExtent * 2);
                    
                    Children[i] = new GridCell(X * 2 + (dx + 1) / 2, Y * 2 + (dy + 1) / 2, Z * 2 + (dz + 1) / 2, 
                                             childBounds, Depth + 1, this);
                }
            }

            public void AddObject(SpatialObject obj)
            {
                Objects.Add(obj);
                obj.CurrentCell = this;
            }

            public void RemoveObject(SpatialObject obj)
            {
                Objects.Remove(obj);
                if (obj.CurrentCell == this)
                    obj.CurrentCell = null;
            }

            public GridCell FindBestChild(SpatialObject obj)
            {
                if (IsLeaf)
                    return this;

                foreach (var child in Children)
                {
                    if (child.Bounds.Contains(obj.Position))
                        return child.FindBestChild(obj);
                }

                // If no child fully contains the object, keep it at this level
                return this;
            }

            public void GetObjectsInRadius(OMV.Vector3 center, float radius, List<uint> results)
            {
                if (!Bounds.IntersectsSphere(center, radius))
                    return;

                // Check objects in this cell
                foreach (var obj in Objects)
                {
                    if (obj.AABB.IntersectsSphere(center, radius))
                    {
                        if (!results.Contains(obj.ID))
                            results.Add(obj.ID);
                    }
                }

                // Recursively check children
                if (!IsLeaf)
                {
                    foreach (var child in Children)
                    {
                        child.GetObjectsInRadius(center, radius, results);
                    }
                }
            }

            public void GetObjectsInBox(BoundingBox box, List<uint> results)
            {
                if (!Bounds.Intersects(box))
                    return;

                // Check objects in this cell
                foreach (var obj in Objects)
                {
                    if (obj.AABB.Intersects(box))
                    {
                        if (!results.Contains(obj.ID))
                            results.Add(obj.ID);
                    }
                }

                // Recursively check children
                if (!IsLeaf)
                {
                    foreach (var child in Children)
                    {
                        child.GetObjectsInBox(box, results);
                    }
                }
            }
        }

        private class SpatialGrid
        {
            private readonly Dictionary<string, GridCell> m_cells;
            private readonly float m_cellSize;
            private readonly int m_maxObjectsPerCell;
            private readonly int m_maxDepth;
            private readonly BoundingBox m_worldBounds;

            public SpatialGrid(float cellSize, int maxObjectsPerCell, int maxDepth, BoundingBox worldBounds)
            {
                m_cells = new Dictionary<string, GridCell>();
                m_cellSize = cellSize;
                m_maxObjectsPerCell = maxObjectsPerCell;
                m_maxDepth = maxDepth;
                m_worldBounds = worldBounds;
            }

            public GridCell GetOrCreateCell(OMV.Vector3 position)
            {
                int x = (int)Math.Floor(position.X / m_cellSize);
                int y = (int)Math.Floor(position.Y / m_cellSize);
                int z = (int)Math.Floor(position.Z / m_cellSize);

                string key = $"{x},{y},{z}";
                
                if (!m_cells.TryGetValue(key, out GridCell cell))
                {
                    OMV.Vector3 cellMin = new OMV.Vector3(x * m_cellSize, y * m_cellSize, z * m_cellSize);
                    OMV.Vector3 cellMax = cellMin + new OMV.Vector3(m_cellSize, m_cellSize, m_cellSize);
                    BoundingBox cellBounds = new BoundingBox((cellMin + cellMax) * 0.5f, 
                                                           new OMV.Vector3(m_cellSize, m_cellSize, m_cellSize));
                    
                    cell = new GridCell(x, y, z, cellBounds, 0);
                    m_cells[key] = cell;
                }

                return cell;
            }

            public void AddObject(SpatialObject obj)
            {
                GridCell cell = GetOrCreateCell(obj.Position);
                
                // Subdivide if necessary
                if (cell.Objects.Count >= m_maxObjectsPerCell && cell.Depth < m_maxDepth)
                {
                    cell.Subdivide(m_maxDepth);
                    
                    // Redistribute objects to children
                    var objectsToRedistribute = cell.Objects.ToList();
                    cell.Objects.Clear();
                    
                    foreach (var existingObj in objectsToRedistribute)
                    {
                        GridCell bestChild = cell.FindBestChild(existingObj);
                        bestChild.AddObject(existingObj);
                    }
                }

                GridCell targetCell = cell.FindBestChild(obj);
                targetCell.AddObject(obj);
            }

            public void RemoveObject(SpatialObject obj)
            {
                obj.CurrentCell?.RemoveObject(obj);
            }

            public void UpdateObject(SpatialObject obj, OMV.Vector3 newPosition, OMV.Vector3 newExtents)
            {
                // Remove from current cell
                RemoveObject(obj);
                
                // Update object
                obj.Position = newPosition;
                obj.Extents = newExtents;
                obj.UpdateAABB();
                obj.LastUpdate = DateTime.UtcNow;
                
                // Add to new cell
                AddObject(obj);
            }

            public List<uint> QueryRadius(OMV.Vector3 center, float radius)
            {
                List<uint> results = new List<uint>();
                
                // Find all cells that might intersect with the query sphere
                BoundingBox queryBox = new BoundingBox(center, new OMV.Vector3(radius * 2, radius * 2, radius * 2));
                
                foreach (var cell in m_cells.Values)
                {
                    cell.GetObjectsInRadius(center, radius, results);
                }
                
                return results;
            }

            public List<uint> QueryBox(OMV.Vector3 center, OMV.Vector3 extents)
            {
                List<uint> results = new List<uint>();
                BoundingBox queryBox = new BoundingBox(center, extents);
                
                foreach (var cell in m_cells.Values)
                {
                    cell.GetObjectsInBox(queryBox, results);
                }
                
                return results;
            }

            public void OptimizeStructure()
            {
                // Remove empty cells and consolidate sparse areas
                var emptyCells = m_cells.Where(kvp => kvp.Value.Objects.Count == 0 && kvp.Value.IsLeaf).ToList();
                
                foreach (var emptyCell in emptyCells)
                {
                    m_cells.Remove(emptyCell.Key);
                }
            }

            public int GetCellCount() => m_cells.Count;
            
            public int GetTotalObjectCount()
            {
                return m_cells.Values.Sum(cell => cell.Objects.Count);
            }
        }

        #endregion

        #region Constructor

        public ModernSpatialIndex(float cellSize = 64.0f, int maxObjectsPerCell = 10, int maxDepth = 6)
        {
            m_cellSize = cellSize;
            m_maxObjectsPerCell = maxObjectsPerCell;
            m_maxDepth = maxDepth;
            
            m_objects = new Dictionary<uint, SpatialObject>();
            m_objectsLock = new object();
            
            // Create world bounds - typical OpenSim region is 256x256, but allow for larger worlds
            BoundingBox worldBounds = new BoundingBox(
                new OMV.Vector3(0, 0, 0),
                new OMV.Vector3(2048, 2048, 2048)
            );
            
            m_grid = new SpatialGrid(cellSize, maxObjectsPerCell, maxDepth, worldBounds);
            m_lastOptimization = DateTime.UtcNow;

            m_log.InfoFormat("{0}: Modern spatial index initialized - Cell Size: {1}, Max Objects/Cell: {2}, Max Depth: {3}", 
                LogHeader, cellSize, maxObjectsPerCell, maxDepth);
        }

        #endregion

        #region ISpatialIndex Implementation

        public void AddObject(uint id, OMV.Vector3 position, OMV.Vector3 extents)
        {
            if (m_disposed)
                return;

            try
            {
                lock (m_objectsLock)
                {
                    if (m_objects.ContainsKey(id))
                    {
                        m_log.WarnFormat("{0}: Object {1} already exists in spatial index, updating instead", LogHeader, id);
                        UpdateObject(id, position, extents);
                        return;
                    }

                    var spatialObj = new SpatialObject
                    {
                        ID = id,
                        Position = position,
                        Extents = extents,
                        LastUpdate = DateTime.UtcNow
                    };
                    spatialObj.UpdateAABB();

                    m_objects[id] = spatialObj;
                    m_grid.AddObject(spatialObj);
                    m_updateCount++;

                    m_log.DebugFormat("{0}: Added object {1} at position {2} with extents {3}", 
                        LogHeader, id, position, extents);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error adding object {1}: {2}", LogHeader, id, ex.Message);
            }
        }

        public void UpdateObject(uint id, OMV.Vector3 position, OMV.Vector3 extents)
        {
            if (m_disposed)
                return;

            try
            {
                lock (m_objectsLock)
                {
                    if (!m_objects.TryGetValue(id, out SpatialObject obj))
                    {
                        m_log.WarnFormat("{0}: Object {1} not found for update, adding instead", LogHeader, id);
                        AddObject(id, position, extents);
                        return;
                    }

                    m_grid.UpdateObject(obj, position, extents);
                    m_updateCount++;

                    m_log.DebugFormat("{0}: Updated object {1} to position {2} with extents {3}", 
                        LogHeader, id, position, extents);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error updating object {1}: {2}", LogHeader, id, ex.Message);
            }
        }

        public void RemoveObject(uint id)
        {
            if (m_disposed)
                return;

            try
            {
                lock (m_objectsLock)
                {
                    if (!m_objects.TryGetValue(id, out SpatialObject obj))
                    {
                        m_log.WarnFormat("{0}: Object {1} not found for removal", LogHeader, id);
                        return;
                    }

                    m_grid.RemoveObject(obj);
                    m_objects.Remove(id);
                    m_updateCount++;

                    m_log.DebugFormat("{0}: Removed object {1}", LogHeader, id);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error removing object {1}: {2}", LogHeader, id, ex.Message);
            }
        }

        public List<uint> Query(OMV.Vector3 position, float radius)
        {
            if (m_disposed)
                return new List<uint>();

            try
            {
                List<uint> results;
                
                lock (m_objectsLock)
                {
                    results = m_grid.QueryRadius(position, radius);
                    m_queryCount++;
                }

                m_log.DebugFormat("{0}: Query at {1} with radius {2} returned {3} objects", 
                    LogHeader, position, radius, results.Count);

                return results;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during radius query: {1}", LogHeader, ex.Message);
                return new List<uint>();
            }
        }

        public List<uint> QueryBox(OMV.Vector3 center, OMV.Vector3 extents)
        {
            if (m_disposed)
                return new List<uint>();

            try
            {
                List<uint> results;
                
                lock (m_objectsLock)
                {
                    results = m_grid.QueryBox(center, extents);
                    m_queryCount++;
                }

                m_log.DebugFormat("{0}: Box query at {1} with extents {2} returned {3} objects", 
                    LogHeader, center, extents, results.Count);

                return results;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during box query: {1}", LogHeader, ex.Message);
                return new List<uint>();
            }
        }

        public void Optimize()
        {
            if (m_disposed)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - m_lastOptimization < OptimizationInterval)
                return;

            try
            {
                lock (m_objectsLock)
                {
                    int cellCountBefore = m_grid.GetCellCount();
                    m_grid.OptimizeStructure();
                    int cellCountAfter = m_grid.GetCellCount();

                    m_lastOptimization = now;

                    m_log.InfoFormat("{0}: Optimization complete - Cells: {1} -> {2}, Objects: {3}, Queries: {4}, Updates: {5}", 
                        LogHeader, cellCountBefore, cellCountAfter, m_objects.Count, m_queryCount, m_updateCount);

                    // Reset counters
                    m_queryCount = 0;
                    m_updateCount = 0;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during optimization: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public int ObjectCount
        {
            get
            {
                lock (m_objectsLock)
                {
                    return m_objects.Count;
                }
            }
        }

        public int CellCount
        {
            get
            {
                lock (m_objectsLock)
                {
                    return m_grid.GetCellCount();
                }
            }
        }

        public float CellSize => m_cellSize;
        public int MaxObjectsPerCell => m_maxObjectsPerCell;
        public int MaxDepth => m_maxDepth;

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (m_disposed)
                return;

            try
            {
                lock (m_objectsLock)
                {
                    m_objects.Clear();
                }

                m_disposed = true;
                
                m_log.InfoFormat("{0}: Modern spatial index disposed - Final stats: Objects: {1}, Queries: {2}, Updates: {3}", 
                    LogHeader, ObjectCount, m_queryCount, m_updateCount);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion
    }
}