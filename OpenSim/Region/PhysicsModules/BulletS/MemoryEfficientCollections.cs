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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Memory-efficient array pool for reducing allocations
    /// </summary>
    public static class PhysicsArrayPool<T>
    {
        private static readonly Dictionary<int, Queue<T[]>> s_pools = new Dictionary<int, Queue<T[]>>();
        private static readonly object s_lock = new object();
        private const int MaxArraySize = 1024;
        private const int MaxPoolSize = 50;

        public static T[] Rent(int size)
        {
            if (size <= 0 || size > MaxArraySize)
                return new T[size];

            lock (s_lock)
            {
                if (s_pools.TryGetValue(size, out Queue<T[]> pool) && pool.Count > 0)
                {
                    return pool.Dequeue();
                }
            }

            return new T[size];
        }

        public static void Return(T[] array)
        {
            if (array == null || array.Length <= 0 || array.Length > MaxArraySize)
                return;

            lock (s_lock)
            {
                if (!s_pools.TryGetValue(array.Length, out Queue<T[]> pool))
                {
                    pool = new Queue<T[]>();
                    s_pools[array.Length] = pool;
                }

                if (pool.Count < MaxPoolSize)
                {
                    Array.Clear(array, 0, array.Length);
                    pool.Enqueue(array);
                }
            }
        }

        public static void Clear()
        {
            lock (s_lock)
            {
                s_pools.Clear();
            }
        }
    }

    /// <summary>
    /// Reusable list that minimizes allocations
    /// </summary>
    public class PooledList<T> : IDisposable
    {
        private T[] m_array;
        private int m_count;
        private bool m_disposed;

        public PooledList(int capacity = 16)
        {
            m_array = PhysicsArrayPool<T>.Rent(capacity);
            m_count = 0;
        }

        public int Count => m_count;
        public int Capacity => m_array.Length;

        public T this[int index]
        {
            get
            {
                if (index >= m_count) throw new IndexOutOfRangeException();
                return m_array[index];
            }
            set
            {
                if (index >= m_count) throw new IndexOutOfRangeException();
                m_array[index] = value;
            }
        }

        public void Add(T item)
        {
            EnsureCapacity(m_count + 1);
            m_array[m_count++] = item;
        }

        public void Clear()
        {
            Array.Clear(m_array, 0, m_count);
            m_count = 0;
        }

        public bool Contains(T item)
        {
            for (int i = 0; i < m_count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(m_array[i], item))
                    return true;
            }
            return false;
        }

        public void RemoveAt(int index)
        {
            if (index >= m_count) throw new IndexOutOfRangeException();
            
            for (int i = index; i < m_count - 1; i++)
            {
                m_array[i] = m_array[i + 1];
            }
            m_count--;
            m_array[m_count] = default(T);
        }

        public T[] ToArray()
        {
            var result = new T[m_count];
            Array.Copy(m_array, result, m_count);
            return result;
        }

        private void EnsureCapacity(int capacity)
        {
            if (m_array.Length >= capacity)
                return;

            int newCapacity = Math.Max(capacity, m_array.Length * 2);
            var newArray = PhysicsArrayPool<T>.Rent(newCapacity);
            Array.Copy(m_array, newArray, m_count);
            
            PhysicsArrayPool<T>.Return(m_array);
            m_array = newArray;
        }

        public void Dispose()
        {
            if (!m_disposed)
            {
                PhysicsArrayPool<T>.Return(m_array);
                m_array = null;
                m_disposed = true;
            }
        }
    }

    /// <summary>
    /// Struct-based vector for reduced allocations in physics calculations
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PhysicsVector3
    {
        public readonly float X, Y, Z;

        public PhysicsVector3(float x, float y, float z)
        {
            X = x; Y = y; Z = z;
        }

        public PhysicsVector3(OMV.Vector3 v)
        {
            X = v.X; Y = v.Y; Z = v.Z;
        }

        public static implicit operator OMV.Vector3(PhysicsVector3 v) => new OMV.Vector3(v.X, v.Y, v.Z);
        public static implicit operator PhysicsVector3(OMV.Vector3 v) => new PhysicsVector3(v.X, v.Y, v.Z);

        public static PhysicsVector3 operator +(PhysicsVector3 a, PhysicsVector3 b) => new PhysicsVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static PhysicsVector3 operator -(PhysicsVector3 a, PhysicsVector3 b) => new PhysicsVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static PhysicsVector3 operator *(PhysicsVector3 v, float s) => new PhysicsVector3(v.X * s, v.Y * s, v.Z * s);
        public static PhysicsVector3 operator /(PhysicsVector3 v, float s) => new PhysicsVector3(v.X / s, v.Y / s, v.Z / s);

        public float LengthSquared => X * X + Y * Y + Z * Z;
        public float Length => (float)Math.Sqrt(LengthSquared);

        public PhysicsVector3 Normalized
        {
            get
            {
                float len = Length;
                return len > 0 ? this / len : Zero;
            }
        }

        public static readonly PhysicsVector3 Zero = new PhysicsVector3(0, 0, 0);
        public static readonly PhysicsVector3 UnitX = new PhysicsVector3(1, 0, 0);
        public static readonly PhysicsVector3 UnitY = new PhysicsVector3(0, 1, 0);
        public static readonly PhysicsVector3 UnitZ = new PhysicsVector3(0, 0, 1);
    }

    /// <summary>
    /// Memory-efficient dictionary with object pooling for physics data
    /// </summary>
    public class PooledDictionary<TKey, TValue> : IDisposable where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> m_dictionary;
        private readonly Queue<KeyValuePair<TKey, TValue>> m_pool;
        private bool m_disposed;

        public PooledDictionary(int capacity = 16)
        {
            m_dictionary = new Dictionary<TKey, TValue>(capacity);
            m_pool = new Queue<KeyValuePair<TKey, TValue>>();
        }

        public int Count => m_dictionary.Count;

        public TValue this[TKey key]
        {
            get => m_dictionary[key];
            set => m_dictionary[key] = value;
        }

        public bool TryGetValue(TKey key, out TValue value) => m_dictionary.TryGetValue(key, out value);

        public void Add(TKey key, TValue value) => m_dictionary.Add(key, value);

        public bool Remove(TKey key)
        {
            if (m_dictionary.TryGetValue(key, out TValue value))
            {
                m_dictionary.Remove(key);
                
                // Pool the removed pair if value is reusable
                if (value is IPhysicsPoolable)
                {
                    m_pool.Enqueue(new KeyValuePair<TKey, TValue>(key, value));
                }
                return true;
            }
            return false;
        }

        public void Clear()
        {
            // Pool all values that support it
            foreach (var kvp in m_dictionary)
            {
                if (kvp.Value is IPhysicsPoolable)
                {
                    m_pool.Enqueue(kvp);
                }
            }
            m_dictionary.Clear();
        }

        public bool ContainsKey(TKey key) => m_dictionary.ContainsKey(key);

        public IEnumerable<TKey> Keys => m_dictionary.Keys;
        public IEnumerable<TValue> Values => m_dictionary.Values;

        public void Dispose()
        {
            if (!m_disposed)
            {
                Clear();
                m_disposed = true;
            }
        }
    }

    /// <summary>
    /// Interface for physics objects that can be pooled and reused
    /// </summary>
    public interface IPhysicsPoolable
    {
        void Reset();
    }

    /// <summary>
    /// Circular buffer for physics data with minimal allocations
    /// </summary>
    public class PhysicsCircularBuffer<T> : IDisposable
    {
        private T[] m_buffer;
        private int m_head;
        private int m_tail;
        private int m_count;
        private readonly int m_capacity;
        private bool m_disposed;

        public PhysicsCircularBuffer(int capacity)
        {
            m_capacity = capacity;
            m_buffer = PhysicsArrayPool<T>.Rent(capacity);
            m_head = 0;
            m_tail = 0;
            m_count = 0;
        }

        public int Count => m_count;
        public int Capacity => m_capacity;
        public bool IsFull => m_count == m_capacity;
        public bool IsEmpty => m_count == 0;

        public void Enqueue(T item)
        {
            m_buffer[m_tail] = item;
            m_tail = (m_tail + 1) % m_capacity;

            if (m_count < m_capacity)
            {
                m_count++;
            }
            else
            {
                // Buffer is full, advance head (overwrite oldest)
                m_head = (m_head + 1) % m_capacity;
            }
        }

        public bool TryDequeue(out T item)
        {
            if (m_count == 0)
            {
                item = default(T);
                return false;
            }

            item = m_buffer[m_head];
            m_buffer[m_head] = default(T); // Clear reference
            m_head = (m_head + 1) % m_capacity;
            m_count--;
            return true;
        }

        public bool TryPeek(out T item)
        {
            if (m_count == 0)
            {
                item = default(T);
                return false;
            }

            item = m_buffer[m_head];
            return true;
        }

        public void Clear()
        {
            Array.Clear(m_buffer, 0, m_capacity);
            m_head = 0;
            m_tail = 0;
            m_count = 0;
        }

        public T[] ToArray()
        {
            var result = new T[m_count];
            int index = 0;
            
            for (int i = 0; i < m_count; i++)
            {
                result[index++] = m_buffer[(m_head + i) % m_capacity];
            }
            
            return result;
        }

        public void Dispose()
        {
            if (!m_disposed)
            {
                PhysicsArrayPool<T>.Return(m_buffer);
                m_buffer = null;
                m_disposed = true;
            }
        }
    }

    /// <summary>
    /// Stack-allocated span for small physics calculations
    /// </summary>
    public ref struct PhysicsSpan<T>
    {
        private readonly Span<T> m_span;

        public PhysicsSpan(Span<T> span)
        {
            m_span = span;
        }

        public int Length => m_span.Length;

        public ref T this[int index] => ref m_span[index];

        public void Clear() => m_span.Clear();

        public void Fill(T value) => m_span.Fill(value);

        public static implicit operator Span<T>(PhysicsSpan<T> physicsSpan) => physicsSpan.m_span;
        public static implicit operator ReadOnlySpan<T>(PhysicsSpan<T> physicsSpan) => physicsSpan.m_span;
    }

    /// <summary>
    /// Utility class for memory-efficient physics calculations
    /// </summary>
    public static class PhysicsMemoryUtils
    {
        /// <summary>
        /// Rent a temporary span for physics calculations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsSpan<T> RentSpan<T>(int length)
        {
            var array = PhysicsArrayPool<T>.Rent(length);
            return new PhysicsSpan<T>(array.AsSpan(0, length));
        }

        /// <summary>
        /// Calculate distance squared without allocations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float DistanceSquared(in PhysicsVector3 a, in PhysicsVector3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
        /// Calculate dot product without allocations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Dot(in PhysicsVector3 a, in PhysicsVector3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        /// <summary>
        /// Calculate cross product without allocations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 Cross(in PhysicsVector3 a, in PhysicsVector3 b)
        {
            return new PhysicsVector3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X
            );
        }

        /// <summary>
        /// Linear interpolation without allocations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 Lerp(in PhysicsVector3 a, in PhysicsVector3 b, float t)
        {
            return new PhysicsVector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }
    }
}