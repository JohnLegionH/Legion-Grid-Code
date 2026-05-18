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
using System.Reflection;
using log4net;
using OpenSim.Framework;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Manages object pools for the destruction system using OpenSim's Pool<T> pattern.
    /// Reduces GC pressure by reusing objects instead of constant allocation/deallocation.
    /// </summary>
    public class DestructionObjectPoolManager : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION POOL]";
        
        // Object pools using OpenSim's Pool<T> implementation
        private readonly Pool<DestructionFragment> m_fragmentPool;
        private readonly Pool<DestructionEventArgs> m_eventArgsPool;
        private readonly Pool<ChainReactionInfo> m_chainReactionPool;
        private readonly Pool<List<DestructionFragment>> m_fragmentListPool;
        private readonly Pool<Dictionary<uint, float>> m_stressDictionaryPool;
        
        // Particle effect object pools
        private readonly Pool<PooledParticleEffectData> m_particleEffectPool;
        private readonly Pool<List<Vector3>> m_vectorListPool;
        
        // Statistics for monitoring pool efficiency
        private long m_fragmentsAllocated;
        private long m_fragmentsReturned;
        private long m_eventArgsAllocated;
        private long m_eventArgsReturned;
        private long m_chainReactionsAllocated;
        private long m_chainReactionsReturned;
        private long m_particleEffectsAllocated;
        private long m_particleEffectsReturned;
        
        // Configuration
        private readonly int m_maxFragmentPoolSize;
        private readonly int m_maxEventArgsPoolSize;
        private readonly int m_maxChainReactionPoolSize;
        private readonly int m_maxParticleEffectPoolSize;
        private readonly int m_maxListPoolSize;
        
        private bool m_disposed;
        
        public DestructionObjectPoolManager(DestructionSystemConfig config)
        {
            // Initialize pool sizes based on configuration
            m_maxFragmentPoolSize = config?.MaxTotalFragments ?? 200;
            m_maxEventArgsPoolSize = config?.MaxDestructibleObjects ?? 100;
            m_maxChainReactionPoolSize = 50; // Reasonable default for chain reactions
            m_maxParticleEffectPoolSize = 100; // Reasonable default for particle effects
            m_maxListPoolSize = 20; // Lists are larger objects, keep pool smaller
            
            // Create pools using OpenSim's Pool<T> pattern
            m_fragmentPool = new Pool<DestructionFragment>(
                CreateFragment, 
                m_maxFragmentPoolSize);
                
            m_eventArgsPool = new Pool<DestructionEventArgs>(
                CreateEventArgs, 
                m_maxEventArgsPoolSize);
                
            m_chainReactionPool = new Pool<ChainReactionInfo>(
                CreateChainReactionInfo, 
                m_maxChainReactionPoolSize);
                
            m_fragmentListPool = new Pool<List<DestructionFragment>>(
                () => new List<DestructionFragment>(15), // Pre-size for typical fragment count
                m_maxListPoolSize);
                
            m_stressDictionaryPool = new Pool<Dictionary<uint, float>>(
                () => new Dictionary<uint, float>(),
                10);
                
            m_particleEffectPool = new Pool<PooledParticleEffectData>(
                CreateParticleEffectData,
                m_maxParticleEffectPoolSize);
                
            m_vectorListPool = new Pool<List<Vector3>>(
                () => new List<Vector3>(10),
                m_maxListPoolSize);
                
            m_log.InfoFormat("{0}: Object pool manager initialized with fragment pool size {1}",
                LogHeader, m_maxFragmentPoolSize);
        }
        
        #region Fragment Pool Management
        
        /// <summary>
        /// Get a fragment from the pool or create a new one
        /// </summary>
        public DestructionFragment GetFragment()
        {
            var fragment = m_fragmentPool.GetObject();
            m_fragmentsAllocated++;
            return fragment;
        }
        
        /// <summary>
        /// Return a fragment to the pool after resetting its state
        /// </summary>
        public void ReturnFragment(DestructionFragment fragment)
        {
            if (fragment == null || m_disposed)
                return;
                
            // Reset fragment state before returning to pool
            ResetFragment(fragment);
            m_fragmentPool.ReturnObject(fragment);
            m_fragmentsReturned++;
        }
        
        /// <summary>
        /// Return multiple fragments to the pool
        /// </summary>
        public void ReturnFragments(IEnumerable<DestructionFragment> fragments)
        {
            if (fragments == null || m_disposed)
                return;
                
            foreach (var fragment in fragments)
            {
                ReturnFragment(fragment);
            }
        }
        
        private DestructionFragment CreateFragment()
        {
            return new DestructionFragment
            {
                FragmentID = 0,
                ParentObjectID = 0,
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                Size = Vector3.One,
                Velocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
                Mass = 1.0f,
                Material = null,
                CreationTime = DateTime.UtcNow,
                LifeTime = 30.0f,
                IsStatic = false
            };
        }
        
        private void ResetFragment(DestructionFragment fragment)
        {
            fragment.FragmentID = 0;
            fragment.ParentObjectID = 0;
            fragment.Position = Vector3.Zero;
            fragment.Rotation = Quaternion.Identity;
            fragment.Size = Vector3.One;
            fragment.Velocity = Vector3.Zero;
            fragment.AngularVelocity = Vector3.Zero;
            fragment.Mass = 1.0f;
            fragment.Material = null;
            fragment.CreationTime = DateTime.UtcNow;
            fragment.LifeTime = 30.0f;
            fragment.IsStatic = false;
        }
        
        #endregion
        
        #region Event Args Pool Management
        
        /// <summary>
        /// Get event args from the pool
        /// </summary>
        public DestructionEventArgs GetEventArgs()
        {
            var args = m_eventArgsPool.GetObject();
            m_eventArgsAllocated++;
            return args;
        }
        
        /// <summary>
        /// Return event args to the pool
        /// </summary>
        public void ReturnEventArgs(DestructionEventArgs args)
        {
            if (args == null || m_disposed)
                return;
                
            // Reset state
            args.DestroyedObject = null;
            args.Fragments?.Clear();
            args.ImpactPoint = Vector3.Zero;
            args.ImpactForce = Vector3.Zero;
            args.Pattern = FracturePattern.Random;
            args.Timestamp = DateTime.UtcNow;
            
            m_eventArgsPool.ReturnObject(args);
            m_eventArgsReturned++;
        }
        
        private DestructionEventArgs CreateEventArgs()
        {
            return new DestructionEventArgs
            {
                Fragments = new List<DestructionFragment>()
            };
        }
        
        #endregion
        
        #region Chain Reaction Pool Management
        
        /// <summary>
        /// Get chain reaction info from the pool
        /// </summary>
        public ChainReactionInfo GetChainReactionInfo()
        {
            var info = m_chainReactionPool.GetObject();
            m_chainReactionsAllocated++;
            return info;
        }
        
        /// <summary>
        /// Return chain reaction info to the pool
        /// </summary>
        public void ReturnChainReactionInfo(ChainReactionInfo info)
        {
            if (info == null || m_disposed)
                return;
                
            // Reset state
            info.TriggerObjectID = 0;
            info.TargetObjectID = 0;
            info.Position = Vector3.Zero;
            info.Force = Vector3.Zero;
            info.TriggerTime = DateTime.UtcNow;
            info.ChainDepth = 0;
            info.IsProcessed = false;
            
            m_chainReactionPool.ReturnObject(info);
            m_chainReactionsReturned++;
        }
        
        private ChainReactionInfo CreateChainReactionInfo()
        {
            return new ChainReactionInfo();
        }
        
        #endregion
        
        #region Particle Effect Pool Management
        
        /// <summary>
        /// Get particle effect data from the pool
        /// </summary>
        public PooledParticleEffectData GetParticleEffectData()
        {
            var data = m_particleEffectPool.GetObject();
            m_particleEffectsAllocated++;
            return data;
        }
        
        /// <summary>
        /// Return particle effect data to the pool
        /// </summary>
        public void ReturnParticleEffectData(PooledParticleEffectData data)
        {
            if (data == null || m_disposed)
                return;
                
            // Reset state
            data.Position = Vector3.Zero;
            data.Velocity = Vector3.Zero;
            data.Color = Vector3.One;
            data.Size = 1.0f;
            data.Lifetime = 1.0f;
            data.EmissionRate = 10;
            
            m_particleEffectPool.ReturnObject(data);
            m_particleEffectsReturned++;
        }
        
        private PooledParticleEffectData CreateParticleEffectData()
        {
            return new PooledParticleEffectData();
        }
        
        #endregion
        
        #region Collection Pool Management
        
        /// <summary>
        /// Get a fragment list from the pool
        /// </summary>
        public List<DestructionFragment> GetFragmentList()
        {
            var list = m_fragmentListPool.GetObject();
            list.Clear(); // Ensure it's empty
            return list;
        }
        
        /// <summary>
        /// Return a fragment list to the pool
        /// </summary>
        public void ReturnFragmentList(List<DestructionFragment> list)
        {
            if (list == null || m_disposed)
                return;
                
            list.Clear();
            m_fragmentListPool.ReturnObject(list);
        }
        
        /// <summary>
        /// Get a vector list from the pool
        /// </summary>
        public List<Vector3> GetVectorList()
        {
            var list = m_vectorListPool.GetObject();
            list.Clear();
            return list;
        }
        
        /// <summary>
        /// Return a vector list to the pool
        /// </summary>
        public void ReturnVectorList(List<Vector3> list)
        {
            if (list == null || m_disposed)
                return;
                
            list.Clear();
            m_vectorListPool.ReturnObject(list);
        }
        
        /// <summary>
        /// Get a stress dictionary from the pool
        /// </summary>
        public Dictionary<uint, float> GetStressDictionary()
        {
            var dict = m_stressDictionaryPool.GetObject();
            dict.Clear();
            return dict;
        }
        
        /// <summary>
        /// Return a stress dictionary to the pool
        /// </summary>
        public void ReturnStressDictionary(Dictionary<uint, float> dict)
        {
            if (dict == null || m_disposed)
                return;
                
            dict.Clear();
            m_stressDictionaryPool.ReturnObject(dict);
        }
        
        #endregion
        
        #region Statistics and Monitoring
        
        /// <summary>
        /// Get pool usage statistics
        /// </summary>
        public PoolStatistics GetStatistics()
        {
            return new PoolStatistics
            {
                FragmentsInPool = m_fragmentPool.Count,
                FragmentsAllocated = m_fragmentsAllocated,
                FragmentsReturned = m_fragmentsReturned,
                FragmentPoolEfficiency = CalculateEfficiency(m_fragmentsAllocated, m_fragmentsReturned),
                
                EventArgsInPool = m_eventArgsPool.Count,
                EventArgsAllocated = m_eventArgsAllocated,
                EventArgsReturned = m_eventArgsReturned,
                EventArgsPoolEfficiency = CalculateEfficiency(m_eventArgsAllocated, m_eventArgsReturned),
                
                ChainReactionsInPool = m_chainReactionPool.Count,
                ChainReactionsAllocated = m_chainReactionsAllocated,
                ChainReactionsReturned = m_chainReactionsReturned,
                ChainReactionPoolEfficiency = CalculateEfficiency(m_chainReactionsAllocated, m_chainReactionsReturned),
                
                ParticleEffectsInPool = m_particleEffectPool.Count,
                ParticleEffectsAllocated = m_particleEffectsAllocated,
                ParticleEffectsReturned = m_particleEffectsReturned,
                ParticleEffectPoolEfficiency = CalculateEfficiency(m_particleEffectsAllocated, m_particleEffectsReturned),
                
                ListsInPool = m_fragmentListPool.Count + m_vectorListPool.Count,
                DictionariesInPool = m_stressDictionaryPool.Count
            };
        }
        
        private float CalculateEfficiency(long allocated, long returned)
        {
            if (allocated == 0)
                return 0;
            return (float)returned / allocated * 100.0f;
        }
        
        /// <summary>
        /// Update stats collector with pool statistics
        /// </summary>
        public void UpdateStatsCollector(DestructionStatsCollector statsCollector)
        {
            if (statsCollector == null)
                return;
                
            var stats = GetStatistics();
            statsCollector.UpdatePooledObjectCounts(
                (int)(m_fragmentsAllocated - m_fragmentsReturned), // In use
                m_fragmentPool.Count); // Available
        }
        
        #endregion
        
        #region Disposal
        
        public void Dispose()
        {
            if (m_disposed)
                return;
                
            m_disposed = true;
            
            // Log final statistics
            var stats = GetStatistics();
            m_log.InfoFormat("{0}: Pool manager disposing. Fragment efficiency: {1:F1}%, EventArgs efficiency: {2:F1}%",
                LogHeader, stats.FragmentPoolEfficiency, stats.EventArgsPoolEfficiency);
                
            // Pools will be garbage collected
        }
        
        #endregion
        
        /// <summary>
        /// Statistics about pool usage
        /// </summary>
        public class PoolStatistics
        {
            public int FragmentsInPool { get; set; }
            public long FragmentsAllocated { get; set; }
            public long FragmentsReturned { get; set; }
            public float FragmentPoolEfficiency { get; set; }
            
            public int EventArgsInPool { get; set; }
            public long EventArgsAllocated { get; set; }
            public long EventArgsReturned { get; set; }
            public float EventArgsPoolEfficiency { get; set; }
            
            public int ChainReactionsInPool { get; set; }
            public long ChainReactionsAllocated { get; set; }
            public long ChainReactionsReturned { get; set; }
            public float ChainReactionPoolEfficiency { get; set; }
            
            public int ParticleEffectsInPool { get; set; }
            public long ParticleEffectsAllocated { get; set; }
            public long ParticleEffectsReturned { get; set; }
            public float ParticleEffectPoolEfficiency { get; set; }
            
            public int ListsInPool { get; set; }
            public int DictionariesInPool { get; set; }
        }
    }
    
    /// <summary>
    /// Data structure for particle effects (pooled)
    /// </summary>
    public class PooledParticleEffectData
    {
        public Vector3 Position { get; set; }
        public Vector3 Velocity { get; set; }
        public Vector3 Color { get; set; }
        public float Size { get; set; }
        public float Lifetime { get; set; }
        public int EmissionRate { get; set; }
    }
}