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
using System.Collections.Concurrent;
using System.Threading;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Interface for objects that can be pooled
    /// </summary>
    public interface IPoolable
    {
        /// <summary>
        /// Reset the object to initial state for reuse
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// Thread-safe object pool for reducing GC pressure
    /// </summary>
    /// <typeparam name="T">Type of objects to pool</typeparam>
    public class ObjectPool<T> where T : class, IPoolable
    {
        private readonly ConcurrentQueue<T> m_objects;
        private readonly Func<T> m_objectFactory;
        private readonly int m_maxSize;
        private int m_currentCount;

        /// <summary>
        /// Create an object pool
        /// </summary>
        /// <param name="objectFactory">Factory function to create new objects</param>
        /// <param name="maxSize">Maximum number of objects to keep in pool</param>
        public ObjectPool(Func<T> objectFactory, int maxSize = 100)
        {
            m_objects = new ConcurrentQueue<T>();
            m_objectFactory = objectFactory ?? throw new ArgumentNullException(nameof(objectFactory));
            m_maxSize = maxSize;
            m_currentCount = 0;
        }

        /// <summary>
        /// Get an object from the pool or create a new one
        /// </summary>
        /// <returns>Object ready for use</returns>
        public T Get()
        {
            if (m_objects.TryDequeue(out T item))
            {
                Interlocked.Decrement(ref m_currentCount);
                item.Reset();
                return item;
            }

            return m_objectFactory();
        }

        /// <summary>
        /// Return an object to the pool
        /// </summary>
        /// <param name="item">Object to return to pool</param>
        public void Return(T item)
        {
            if (item == null)
                return;

            if (m_currentCount < m_maxSize)
            {
                m_objects.Enqueue(item);
                Interlocked.Increment(ref m_currentCount);
            }
            // If pool is full, just let the object be garbage collected
        }

        /// <summary>
        /// Current number of objects in the pool
        /// </summary>
        public int Count => m_currentCount;

        /// <summary>
        /// Maximum pool size
        /// </summary>
        public int MaxSize => m_maxSize;

        /// <summary>
        /// Clear all objects from the pool
        /// </summary>
        public void Clear()
        {
            while (m_objects.TryDequeue(out T item))
            {
                if (item is IDisposable disposable)
                    disposable.Dispose();
            }
            m_currentCount = 0;
        }
    }
}