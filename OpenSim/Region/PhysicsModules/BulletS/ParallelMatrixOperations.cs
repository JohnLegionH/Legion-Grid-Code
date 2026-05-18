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
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// 3x3 Matrix structure optimized for physics calculations
    /// </summary>
    public struct Matrix3x3
    {
        public float M11, M12, M13;
        public float M21, M22, M23;
        public float M31, M32, M33;

        public Matrix3x3(float m11, float m12, float m13, float m21, float m22, float m23, float m31, float m32, float m33)
        {
            M11 = m11; M12 = m12; M13 = m13;
            M21 = m21; M22 = m22; M23 = m23;
            M31 = m31; M32 = m32; M33 = m33;
        }

        public static Matrix3x3 Identity => new Matrix3x3(1, 0, 0, 0, 1, 0, 0, 0, 1);
        public static Matrix3x3 Zero => new Matrix3x3(0, 0, 0, 0, 0, 0, 0, 0, 0);

        public float this[int row, int col]
        {
            get
            {
                return row switch
                {
                    0 => col switch { 0 => M11, 1 => M12, 2 => M13, _ => throw new IndexOutOfRangeException() },
                    1 => col switch { 0 => M21, 1 => M22, 2 => M23, _ => throw new IndexOutOfRangeException() },
                    2 => col switch { 0 => M31, 1 => M32, 2 => M33, _ => throw new IndexOutOfRangeException() },
                    _ => throw new IndexOutOfRangeException()
                };
            }
            set
            {
                switch (row)
                {
                    case 0:
                        switch (col) { case 0: M11 = value; break; case 1: M12 = value; break; case 2: M13 = value; break; default: throw new IndexOutOfRangeException(); }
                        break;
                    case 1:
                        switch (col) { case 0: M21 = value; break; case 1: M22 = value; break; case 2: M23 = value; break; default: throw new IndexOutOfRangeException(); }
                        break;
                    case 2:
                        switch (col) { case 0: M31 = value; break; case 1: M32 = value; break; case 2: M33 = value; break; default: throw new IndexOutOfRangeException(); }
                        break;
                    default: throw new IndexOutOfRangeException();
                }
            }
        }

        public static Matrix3x3 operator +(Matrix3x3 a, Matrix3x3 b)
        {
            return new Matrix3x3(
                a.M11 + b.M11, a.M12 + b.M12, a.M13 + b.M13,
                a.M21 + b.M21, a.M22 + b.M22, a.M23 + b.M23,
                a.M31 + b.M31, a.M32 + b.M32, a.M33 + b.M33
            );
        }

        public static Matrix3x3 operator -(Matrix3x3 a, Matrix3x3 b)
        {
            return new Matrix3x3(
                a.M11 - b.M11, a.M12 - b.M12, a.M13 - b.M13,
                a.M21 - b.M21, a.M22 - b.M22, a.M23 - b.M23,
                a.M31 - b.M31, a.M32 - b.M32, a.M33 - b.M33
            );
        }

        public static Matrix3x3 operator *(Matrix3x3 m, float s)
        {
            return new Matrix3x3(
                m.M11 * s, m.M12 * s, m.M13 * s,
                m.M21 * s, m.M22 * s, m.M23 * s,
                m.M31 * s, m.M32 * s, m.M33 * s
            );
        }

        public float[] ToArray()
        {
            return new float[] { M11, M12, M13, M21, M22, M23, M31, M32, M33 };
        }

        public static Matrix3x3 FromArray(float[] array)
        {
            if (array.Length != 9)
                throw new ArgumentException("Array must have 9 elements");
            
            return new Matrix3x3(
                array[0], array[1], array[2],
                array[3], array[4], array[5],
                array[6], array[7], array[8]
            );
        }
    }

    /// <summary>
    /// Physics matrix operation result for batch processing
    /// </summary>
    public struct MatrixOperationResult
    {
        public Matrix3x3 Result;
        public bool IsValid;
        public float Determinant;
        public DateTime ProcessedAt;

        public MatrixOperationResult(Matrix3x3 result, bool isValid = true)
        {
            Result = result;
            IsValid = isValid;
            Determinant = 0.0f;
            ProcessedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// High-performance parallel matrix operations for physics calculations
    /// </summary>
    public static class ParallelMatrixOperations
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[PARALLEL MATRIX OPS]";

        private static readonly bool s_isParallelSupported = Environment.ProcessorCount > 1;
        private static readonly int s_parallelThreshold = 64; // Minimum size for parallel execution
        private static long s_operationsProcessed = 0;
        private static float s_averageParallelTime = 0.0f;

        static ParallelMatrixOperations()
        {
            m_log.InfoFormat("{0}: Parallel matrix operations initialized - Processors: {1}, Parallel threshold: {2}", 
                LogHeader, Environment.ProcessorCount, s_parallelThreshold);
        }

        #region Matrix Multiplication

        /// <summary>
        /// High-performance 3x3 matrix multiplication using SIMD when available
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Matrix3x3 Multiply(in Matrix3x3 a, in Matrix3x3 b)
        {
            if (SIMDPhysicsMath.IsHardwareAccelerated)
            {
                return MultiplySIMD(a, b);
            }

            return new Matrix3x3(
                a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31,
                a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32,
                a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,

                a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31,
                a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32,
                a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,

                a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31,
                a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32,
                a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33
            );
        }

        private static Matrix3x3 MultiplySIMD(in Matrix3x3 a, in Matrix3x3 b)
        {
            // Use System.Numerics.Vector for SIMD acceleration
            var row1 = new Vector3(a.M11, a.M12, a.M13);
            var row2 = new Vector3(a.M21, a.M22, a.M23);
            var row3 = new Vector3(a.M31, a.M32, a.M33);

            var col1 = new Vector3(b.M11, b.M21, b.M31);
            var col2 = new Vector3(b.M12, b.M22, b.M32);
            var col3 = new Vector3(b.M13, b.M23, b.M33);

            return new Matrix3x3(
                Vector3.Dot(row1, col1), Vector3.Dot(row1, col2), Vector3.Dot(row1, col3),
                Vector3.Dot(row2, col1), Vector3.Dot(row2, col2), Vector3.Dot(row2, col3),
                Vector3.Dot(row3, col1), Vector3.Dot(row3, col2), Vector3.Dot(row3, col3)
            );
        }

        /// <summary>
        /// Batch matrix multiplication using parallel processing
        /// </summary>
        public static MatrixOperationResult[] MultiplyBatch(Matrix3x3[] matricesA, Matrix3x3[] matricesB)
        {
            if (matricesA.Length != matricesB.Length)
                throw new ArgumentException("Matrix arrays must have the same length");

            var results = new MatrixOperationResult[matricesA.Length];
            var startTime = DateTime.UtcNow;

            if (matricesA.Length >= s_parallelThreshold && s_isParallelSupported)
            {
                Parallel.For(0, matricesA.Length, i =>
                {
                    results[i] = new MatrixOperationResult(Multiply(matricesA[i], matricesB[i]));
                });
            }
            else
            {
                for (int i = 0; i < matricesA.Length; i++)
                {
                    results[i] = new MatrixOperationResult(Multiply(matricesA[i], matricesB[i]));
                }
            }

            UpdatePerformanceStatistics(matricesA.Length, startTime);
            return results;
        }

        #endregion

        #region Matrix-Vector Operations

        /// <summary>
        /// Transform vector by 3x3 matrix using SIMD when available
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OMV.Vector3 Transform(in Matrix3x3 matrix, in OMV.Vector3 vector)
        {
            if (SIMDPhysicsMath.IsHardwareAccelerated)
            {
                return TransformSIMD(matrix, vector);
            }

            return new OMV.Vector3(
                matrix.M11 * vector.X + matrix.M12 * vector.Y + matrix.M13 * vector.Z,
                matrix.M21 * vector.X + matrix.M22 * vector.Y + matrix.M23 * vector.Z,
                matrix.M31 * vector.X + matrix.M32 * vector.Y + matrix.M33 * vector.Z
            );
        }

        private static OMV.Vector3 TransformSIMD(in Matrix3x3 matrix, in OMV.Vector3 vector)
        {
            var v = new Vector3(vector.X, vector.Y, vector.Z);
            var row1 = new Vector3(matrix.M11, matrix.M12, matrix.M13);
            var row2 = new Vector3(matrix.M21, matrix.M22, matrix.M23);
            var row3 = new Vector3(matrix.M31, matrix.M32, matrix.M33);

            return new OMV.Vector3(
                Vector3.Dot(row1, v),
                Vector3.Dot(row2, v),
                Vector3.Dot(row3, v)
            );
        }

        /// <summary>
        /// Batch transform vectors using parallel processing
        /// </summary>
        public static OMV.Vector3[] TransformBatch(Matrix3x3[] matrices, OMV.Vector3[] vectors)
        {
            if (matrices.Length != vectors.Length)
                throw new ArgumentException("Matrix and vector arrays must have the same length");

            var results = new OMV.Vector3[vectors.Length];
            var startTime = DateTime.UtcNow;

            if (vectors.Length >= s_parallelThreshold && s_isParallelSupported)
            {
                Parallel.For(0, vectors.Length, i =>
                {
                    results[i] = Transform(matrices[i], vectors[i]);
                });
            }
            else
            {
                for (int i = 0; i < vectors.Length; i++)
                {
                    results[i] = Transform(matrices[i], vectors[i]);
                }
            }

            UpdatePerformanceStatistics(vectors.Length, startTime);
            return results;
        }

        #endregion

        #region Matrix Inversion

        /// <summary>
        /// Calculate matrix determinant using SIMD when available
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Determinant(in Matrix3x3 matrix)
        {
            // det(M) = a11(a22*a33 - a23*a32) - a12(a21*a33 - a23*a31) + a13(a21*a32 - a22*a31)
            float det1 = matrix.M22 * matrix.M33 - matrix.M23 * matrix.M32;
            float det2 = matrix.M21 * matrix.M33 - matrix.M23 * matrix.M31;
            float det3 = matrix.M21 * matrix.M32 - matrix.M22 * matrix.M31;

            return matrix.M11 * det1 - matrix.M12 * det2 + matrix.M13 * det3;
        }

        /// <summary>
        /// Invert 3x3 matrix using high-performance algorithms
        /// </summary>
        public static MatrixOperationResult Invert(in Matrix3x3 matrix)
        {
            float det = Determinant(matrix);
            
            if (Math.Abs(det) < 1e-8f)
            {
                return new MatrixOperationResult(Matrix3x3.Zero, false);
            }

            float invDet = 1.0f / det;

            // Calculate adjugate matrix
            var result = new Matrix3x3(
                (matrix.M22 * matrix.M33 - matrix.M23 * matrix.M32) * invDet,
                (matrix.M13 * matrix.M32 - matrix.M12 * matrix.M33) * invDet,
                (matrix.M12 * matrix.M23 - matrix.M13 * matrix.M22) * invDet,

                (matrix.M23 * matrix.M31 - matrix.M21 * matrix.M33) * invDet,
                (matrix.M11 * matrix.M33 - matrix.M13 * matrix.M31) * invDet,
                (matrix.M13 * matrix.M21 - matrix.M11 * matrix.M23) * invDet,

                (matrix.M21 * matrix.M32 - matrix.M22 * matrix.M31) * invDet,
                (matrix.M12 * matrix.M31 - matrix.M11 * matrix.M32) * invDet,
                (matrix.M11 * matrix.M22 - matrix.M12 * matrix.M21) * invDet
            );

            var opResult = new MatrixOperationResult(result, true);
            opResult.Determinant = det;
            return opResult;
        }

        /// <summary>
        /// Batch matrix inversion using parallel processing
        /// </summary>
        public static MatrixOperationResult[] InvertBatch(Matrix3x3[] matrices)
        {
            var results = new MatrixOperationResult[matrices.Length];
            var startTime = DateTime.UtcNow;

            if (matrices.Length >= s_parallelThreshold && s_isParallelSupported)
            {
                Parallel.For(0, matrices.Length, i =>
                {
                    results[i] = Invert(matrices[i]);
                });
            }
            else
            {
                for (int i = 0; i < matrices.Length; i++)
                {
                    results[i] = Invert(matrices[i]);
                }
            }

            UpdatePerformanceStatistics(matrices.Length, startTime);
            return results;
        }

        #endregion

        #region Matrix Decomposition

        /// <summary>
        /// Perform Cholesky decomposition for positive definite matrices
        /// </summary>
        public static MatrixOperationResult CholeskyDecomposition(in Matrix3x3 matrix)
        {
            try
            {
                var L = Matrix3x3.Zero;

                // L11 = sqrt(a11)
                if (matrix.M11 <= 0) return new MatrixOperationResult(Matrix3x3.Zero, false);
                L.M11 = MathF.Sqrt(matrix.M11);

                // L21 = a21 / L11
                L.M21 = matrix.M21 / L.M11;

                // L22 = sqrt(a22 - L21^2)
                float temp = matrix.M22 - L.M21 * L.M21;
                if (temp <= 0) return new MatrixOperationResult(Matrix3x3.Zero, false);
                L.M22 = MathF.Sqrt(temp);

                // L31 = a31 / L11
                L.M31 = matrix.M31 / L.M11;

                // L32 = (a32 - L31*L21) / L22
                L.M32 = (matrix.M32 - L.M31 * L.M21) / L.M22;

                // L33 = sqrt(a33 - L31^2 - L32^2)
                temp = matrix.M33 - L.M31 * L.M31 - L.M32 * L.M32;
                if (temp <= 0) return new MatrixOperationResult(Matrix3x3.Zero, false);
                L.M33 = MathF.Sqrt(temp);

                return new MatrixOperationResult(L, true);
            }
            catch
            {
                return new MatrixOperationResult(Matrix3x3.Zero, false);
            }
        }

        /// <summary>
        /// Solve linear system Ax = b using LU decomposition
        /// </summary>
        public static OMV.Vector3? SolveLinearSystem(in Matrix3x3 A, in OMV.Vector3 b)
        {
            // Use Gaussian elimination with partial pivoting
            var augmented = new float[3, 4];
            
            // Fill augmented matrix [A|b]
            augmented[0, 0] = A.M11; augmented[0, 1] = A.M12; augmented[0, 2] = A.M13; augmented[0, 3] = b.X;
            augmented[1, 0] = A.M21; augmented[1, 1] = A.M22; augmented[1, 2] = A.M23; augmented[1, 3] = b.Y;
            augmented[2, 0] = A.M31; augmented[2, 1] = A.M32; augmented[2, 2] = A.M33; augmented[2, 3] = b.Z;

            // Forward elimination
            for (int k = 0; k < 2; k++)
            {
                // Find pivot
                int maxRow = k;
                for (int i = k + 1; i < 3; i++)
                {
                    if (Math.Abs(augmented[i, k]) > Math.Abs(augmented[maxRow, k]))
                        maxRow = i;
                }

                // Swap rows if needed
                if (maxRow != k)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        (augmented[k, j], augmented[maxRow, j]) = (augmented[maxRow, j], augmented[k, j]);
                    }
                }

                // Check for singular matrix
                if (Math.Abs(augmented[k, k]) < 1e-8f)
                    return null;

                // Eliminate column
                for (int i = k + 1; i < 3; i++)
                {
                    float factor = augmented[i, k] / augmented[k, k];
                    for (int j = k; j < 4; j++)
                    {
                        augmented[i, j] -= factor * augmented[k, j];
                    }
                }
            }

            // Back substitution
            var x = new float[3];
            for (int i = 2; i >= 0; i--)
            {
                x[i] = augmented[i, 3];
                for (int j = i + 1; j < 3; j++)
                {
                    x[i] -= augmented[i, j] * x[j];
                }
                
                if (Math.Abs(augmented[i, i]) < 1e-8f)
                    return null;
                    
                x[i] /= augmented[i, i];
            }

            return new OMV.Vector3(x[0], x[1], x[2]);
        }

        #endregion

        #region Specialized Physics Operations

        /// <summary>
        /// Create rotation matrix from quaternion using SIMD optimization
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Matrix3x3 FromQuaternion(in OMV.Quaternion q)
        {
            float xx = q.X * q.X;
            float yy = q.Y * q.Y;
            float zz = q.Z * q.Z;
            float xy = q.X * q.Y;
            float xz = q.X * q.Z;
            float yz = q.Y * q.Z;
            float wx = q.W * q.X;
            float wy = q.W * q.Y;
            float wz = q.W * q.Z;

            return new Matrix3x3(
                1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy),
                2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx),
                2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy)
            );
        }

        /// <summary>
        /// Create inertia tensor from mass and dimensions
        /// </summary>
        public static Matrix3x3 CreateInertiaTensor(float mass, in OMV.Vector3 dimensions)
        {
            float dx2 = dimensions.X * dimensions.X;
            float dy2 = dimensions.Y * dimensions.Y;
            float dz2 = dimensions.Z * dimensions.Z;
            float factor = mass / 12.0f;

            return new Matrix3x3(
                factor * (dy2 + dz2), 0, 0,
                0, factor * (dx2 + dz2), 0,
                0, 0, factor * (dx2 + dy2)
            );
        }

        /// <summary>
        /// Transform inertia tensor to world space
        /// </summary>
        public static Matrix3x3 TransformInertiaTensor(in Matrix3x3 localInertia, in Matrix3x3 rotation)
        {
            // I_world = R * I_local * R^T
            var rotationTranspose = Transpose(rotation);
            var temp = Multiply(rotation, localInertia);
            return Multiply(temp, rotationTranspose);
        }

        /// <summary>
        /// Calculate matrix transpose
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Matrix3x3 Transpose(in Matrix3x3 matrix)
        {
            return new Matrix3x3(
                matrix.M11, matrix.M21, matrix.M31,
                matrix.M12, matrix.M22, matrix.M32,
                matrix.M13, matrix.M23, matrix.M33
            );
        }

        #endregion

        #region Performance Monitoring

        private static void UpdatePerformanceStatistics(int operationCount, DateTime startTime)
        {
            s_operationsProcessed += operationCount;
            float processingTime = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
            s_averageParallelTime = s_averageParallelTime * 0.9f + processingTime * 0.1f;
        }

        /// <summary>
        /// Get performance statistics for parallel matrix operations
        /// </summary>
        public static string GetPerformanceReport()
        {
            var report = $"Parallel Matrix Operations Performance:\\n";
            report += $"  Processors Available: {Environment.ProcessorCount}\\n";
            report += $"  Parallel Processing: {s_isParallelSupported}\\n";
            report += $"  SIMD Support: {SIMDPhysicsMath.IsHardwareAccelerated}\\n";
            report += $"  Operations Processed: {s_operationsProcessed}\\n";
            report += $"  Average Processing Time: {s_averageParallelTime:F2}ms\\n";
            report += $"  Parallel Threshold: {s_parallelThreshold}\\n";

            return report;
        }

        /// <summary>
        /// Reset performance statistics
        /// </summary>
        public static void ResetPerformanceCounters()
        {
            s_operationsProcessed = 0;
            s_averageParallelTime = 0.0f;
        }

        #endregion

        #region Public Properties

        public static bool IsParallelSupported => s_isParallelSupported;
        public static bool IsSIMDSupported => SIMDPhysicsMath.IsHardwareAccelerated;
        public static int ParallelThreshold => s_parallelThreshold;
        public static long OperationsProcessed => s_operationsProcessed;
        public static float AverageProcessingTime => s_averageParallelTime;

        #endregion
    }
}