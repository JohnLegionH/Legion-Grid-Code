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
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Motion prediction data for an object
    /// </summary>
    public class MotionPrediction
    {
        public uint ObjectID { get; set; }
        public OMV.Vector3 CurrentPosition { get; set; }
        public OMV.Vector3 CurrentVelocity { get; set; }
        public OMV.Vector3 CurrentAcceleration { get; set; }
        public OMV.Quaternion CurrentRotation { get; set; }
        public OMV.Vector3 CurrentAngularVelocity { get; set; }
        
        // Predicted states
        public OMV.Vector3[] PredictedPositions { get; set; }
        public OMV.Vector3[] PredictedVelocities { get; set; }
        public OMV.Quaternion[] PredictedRotations { get; set; }
        public float[] TimeSteps { get; set; }
        
        // Motion characteristics
        public MotionType MotionType { get; set; }
        public float PredictionAccuracy { get; set; }
        public float MotionComplexity { get; set; }
        public bool IsStable { get; set; }
        public DateTime LastUpdate { get; set; }
        
        // Historical data for learning
        public readonly Queue<MotionSample> MotionHistory;
        public readonly int MaxHistorySize = 50;
        
        public MotionPrediction(uint objectId)
        {
            ObjectID = objectId;
            MotionHistory = new Queue<MotionSample>();
            PredictedPositions = new OMV.Vector3[10]; // Predict 10 steps ahead
            PredictedVelocities = new OMV.Vector3[10];
            PredictedRotations = new OMV.Quaternion[10];
            TimeSteps = new float[10];
            LastUpdate = DateTime.UtcNow;
        }
        
        public void AddMotionSample(MotionSample sample)
        {
            MotionHistory.Enqueue(sample);
            if (MotionHistory.Count > MaxHistorySize)
            {
                MotionHistory.Dequeue();
            }
            LastUpdate = DateTime.UtcNow;
        }
        
        public TimeSpan Age => DateTime.UtcNow - LastUpdate;
    }

    /// <summary>
    /// Motion sample for historical analysis
    /// </summary>
    public struct MotionSample
    {
        public OMV.Vector3 Position;
        public OMV.Vector3 Velocity;
        public OMV.Vector3 Acceleration;
        public OMV.Quaternion Rotation;
        public OMV.Vector3 AngularVelocity;
        public DateTime Timestamp;
        public float DeltaTime;
    }

    /// <summary>
    /// Predicted collision information
    /// </summary>
    public class PredictedCollision
    {
        public uint Object1ID { get; set; }
        public uint Object2ID { get; set; }
        public float TimeToCollision { get; set; }
        public OMV.Vector3 PredictedContactPoint { get; set; }
        public OMV.Vector3 PredictedContactNormal { get; set; }
        public float PredictedImpactVelocity { get; set; }
        public float CollisionProbability { get; set; }
        public CollisionSeverity Severity { get; set; }
        public DateTime PredictedAt { get; set; }
        public bool RequiresPreventiveAction { get; set; }
        
        public PredictedCollision()
        {
            PredictedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Motion type classification for prediction algorithms
    /// </summary>
    public enum MotionType
    {
        Static,
        Linear,
        Circular,
        Oscillatory,
        Chaotic,
        Projectile,
        Falling,
        Accelerating,
        Decelerating
    }

    /// <summary>
    /// Collision severity classification
    /// </summary>
    public enum CollisionSeverity
    {
        None,
        Minor,
        Moderate,
        Severe,
        Critical
    }

    /// <summary>
    /// Advanced predictive collision detection system with motion extrapolation
    /// </summary>
    public class PredictiveCollisionDetection : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[PREDICTIVE COLLISION]";

        #region Private Fields

        private readonly BSScene m_scene;
        private readonly ConcurrentDictionary<uint, MotionPrediction> m_motionPredictions;
        private readonly Timer m_predictionTimer;
        private readonly object m_predictionLock;
        private bool m_disposed;
        private bool m_enabled;

        // Configuration
        private readonly float m_maxPredictionTime = 2.0f; // Predict up to 2 seconds ahead
        private readonly float m_predictionTimeStep = 0.1f; // 100ms prediction intervals
        private readonly float m_collisionDistanceThreshold = 0.5f; // Minimum distance for collision
        private readonly float m_minVelocityThreshold = 0.1f; // Minimum velocity to consider
        private readonly TimeSpan m_predictionInterval = TimeSpan.FromMilliseconds(50); // Update every 50ms

        // Performance tracking
        private long m_predictionsGenerated;
        private long m_collisionsPredicted;
        private long m_correctPredictions;
        private long m_falsePredictions;
        private float m_averagePredictionTime;
        private DateTime m_lastPerformanceReport;
        private readonly TimeSpan PerformanceReportInterval = TimeSpan.FromMinutes(3);

        // Adaptive parameters
        private float m_currentAccuracyThreshold = 0.7f;
        private float m_dynamicPredictionRange = 1.0f;
        private readonly Dictionary<MotionType, float> m_motionTypePredictionWeights;

        #endregion

        #region Constructor

        public PredictiveCollisionDetection(BSScene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_motionPredictions = new ConcurrentDictionary<uint, MotionPrediction>();
            m_predictionLock = new object();

            // Initialize motion type weights for prediction accuracy
            m_motionTypePredictionWeights = new Dictionary<MotionType, float>
            {
                { MotionType.Static, 0.95f },
                { MotionType.Linear, 0.90f },
                { MotionType.Projectile, 0.85f },
                { MotionType.Falling, 0.80f },
                { MotionType.Circular, 0.75f },
                { MotionType.Accelerating, 0.70f },
                { MotionType.Decelerating, 0.70f },
                { MotionType.Oscillatory, 0.60f },
                { MotionType.Chaotic, 0.30f }
            };

            m_lastPerformanceReport = DateTime.UtcNow;

            // Setup prediction timer
            m_predictionTimer = new Timer(PerformPredictionUpdate, null, m_predictionInterval, m_predictionInterval);

            m_enabled = true;

            m_log.InfoFormat("{0}: Predictive collision detection initialized - Max prediction time: {1}s, Time step: {2}s", 
                LogHeader, m_maxPredictionTime, m_predictionTimeStep);
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize the predictive collision detection system
        /// </summary>
        public void Initialize()
        {
            if (m_disposed)
                return;

            m_enabled = true;
            m_log.InfoFormat("{0}: Predictive collision detection system started", LogHeader);
        }

        /// <summary>
        /// Update object motion data for prediction
        /// </summary>
        public void UpdateObjectMotion(uint objectID, OMV.Vector3 position, OMV.Vector3 velocity, 
            OMV.Vector3 acceleration, OMV.Quaternion rotation, OMV.Vector3 angularVelocity, float deltaTime)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                var prediction = m_motionPredictions.GetOrAdd(objectID, id => new MotionPrediction(id));

                lock (prediction)
                {
                    // Create motion sample
                    var sample = new MotionSample
                    {
                        Position = position,
                        Velocity = velocity,
                        Acceleration = acceleration,
                        Rotation = rotation,
                        AngularVelocity = angularVelocity,
                        Timestamp = DateTime.UtcNow,
                        DeltaTime = deltaTime
                    };

                    prediction.AddMotionSample(sample);

                    // Update current state
                    prediction.CurrentPosition = position;
                    prediction.CurrentVelocity = velocity;
                    prediction.CurrentAcceleration = acceleration;
                    prediction.CurrentRotation = rotation;
                    prediction.CurrentAngularVelocity = angularVelocity;

                    // Analyze motion pattern
                    AnalyzeMotionPattern(prediction);

                    // Generate predictions
                    GenerateMotionPredictions(prediction, deltaTime);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error updating motion for object {1}: {2}", LogHeader, objectID, ex.Message);
            }
        }

        /// <summary>
        /// Get predicted collisions for all tracked objects
        /// </summary>
        public List<PredictedCollision> GetPredictedCollisions()
        {
            if (!m_enabled || m_disposed)
                return new List<PredictedCollision>();

            var startTime = DateTime.UtcNow;
            var predictions = new List<PredictedCollision>();

            try
            {
                lock (m_predictionLock)
                {
                    var activePredictions = m_motionPredictions.Values
                        .Where(p => p.Age < TimeSpan.FromSeconds(1) && 
                                   p.CurrentVelocity.Length() > m_minVelocityThreshold)
                        .ToList();

                    // Check all pairs for potential collisions
                    for (int i = 0; i < activePredictions.Count; i++)
                    {
                        for (int j = i + 1; j < activePredictions.Count; j++)
                        {
                            var collision = PredictCollisionBetweenObjects(activePredictions[i], activePredictions[j]);
                            if (collision != null)
                            {
                                predictions.Add(collision);
                            }
                        }
                    }

                    m_predictionsGenerated++;
                }

                var processingTime = (float)(DateTime.UtcNow - startTime).TotalMilliseconds;
                m_averagePredictionTime = m_averagePredictionTime * 0.9f + processingTime * 0.1f;

                UpdatePerformanceStatistics();
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error generating collision predictions: {1}", LogHeader, ex.Message);
            }

            return predictions;
        }

        /// <summary>
        /// Get predicted motion state for an object at a future time
        /// </summary>
        public (OMV.Vector3 position, OMV.Vector3 velocity)? GetPredictedState(uint objectID, float futureTime)
        {
            if (!m_enabled || m_disposed || !m_motionPredictions.TryGetValue(objectID, out MotionPrediction prediction))
                return null;

            if (futureTime <= 0 || futureTime > m_maxPredictionTime)
                return null;

            try
            {
                lock (prediction)
                {
                    // Find the appropriate prediction time step
                    int stepIndex = Math.Min((int)(futureTime / m_predictionTimeStep), prediction.TimeSteps.Length - 1);
                    
                    if (stepIndex >= 0 && stepIndex < prediction.PredictedPositions.Length)
                    {
                        return (prediction.PredictedPositions[stepIndex], prediction.PredictedVelocities[stepIndex]);
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error getting predicted state for object {1}: {2}", LogHeader, objectID, ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Remove motion prediction data for an object
        /// </summary>
        public void RemoveObjectPrediction(uint objectID)
        {
            if (!m_enabled || m_disposed)
                return;

            m_motionPredictions.TryRemove(objectID, out _);
        }

        /// <summary>
        /// Get performance report for predictive collision detection
        /// </summary>
        public string GetPerformanceReport()
        {
            if (!m_enabled || m_disposed)
                return "Predictive collision detection not available";

            var report = $"Predictive Collision Detection Performance:\\n";
            report += $"  Tracked Objects: {m_motionPredictions.Count}\\n";
            report += $"  Predictions Generated: {m_predictionsGenerated}\\n";
            report += $"  Collisions Predicted: {m_collisionsPredicted}\\n";
            report += $"  Correct Predictions: {m_correctPredictions}\\n";
            report += $"  False Predictions: {m_falsePredictions}\\n";
            report += $"  Prediction Accuracy: {GetPredictionAccuracy():P2}\\n";
            report += $"  Average Processing Time: {m_averagePredictionTime:F2}ms\\n";
            report += $"  Max Prediction Time: {m_maxPredictionTime}s\\n";
            report += $"  Current Accuracy Threshold: {m_currentAccuracyThreshold:P2}\\n";
            report += $"  Dynamic Prediction Range: {m_dynamicPredictionRange:F2}s\\n";

            return report;
        }

        #endregion

        #region Private Methods

        private void AnalyzeMotionPattern(MotionPrediction prediction)
        {
            if (prediction.MotionHistory.Count < 3)
            {
                prediction.MotionType = MotionType.Static;
                return;
            }

            var samples = prediction.MotionHistory.ToArray();
            var recentSamples = samples.Skip(Math.Max(0, samples.Length - 10)).ToArray();

            // Analyze velocity patterns
            var velocityMagnitudes = recentSamples.Select(s => s.Velocity.Length()).ToArray();
            var accelerationMagnitudes = recentSamples.Select(s => s.Acceleration.Length()).ToArray();

            // Determine motion type
            var avgVelocity = velocityMagnitudes.Average();
            var avgAcceleration = accelerationMagnitudes.Average();
            var velocityVariance = CalculateVariance(velocityMagnitudes);
            var accelerationVariance = CalculateVariance(accelerationMagnitudes);

            if (avgVelocity < m_minVelocityThreshold)
            {
                prediction.MotionType = MotionType.Static;
            }
            else if (avgAcceleration > 9.0f && prediction.CurrentVelocity.Z < -1.0f)
            {
                prediction.MotionType = MotionType.Falling;
            }
            else if (accelerationVariance < 0.1f && velocityVariance < 0.1f)
            {
                prediction.MotionType = MotionType.Linear;
            }
            else if (avgAcceleration > 2.0f)
            {
                prediction.MotionType = avgVelocity > velocityMagnitudes.First() ? MotionType.Accelerating : MotionType.Decelerating;
            }
            else if (IsCircularMotion(recentSamples))
            {
                prediction.MotionType = MotionType.Circular;
            }
            else if (IsOscillatoryMotion(recentSamples))
            {
                prediction.MotionType = MotionType.Oscillatory;
            }
            else if (velocityVariance > 1.0f || accelerationVariance > 1.0f)
            {
                prediction.MotionType = MotionType.Chaotic;
            }
            else
            {
                prediction.MotionType = MotionType.Linear;
            }

            // Calculate motion complexity and stability
            prediction.MotionComplexity = (velocityVariance + accelerationVariance) / 2.0f;
            prediction.IsStable = velocityVariance < 0.5f && accelerationVariance < 0.5f;

            // Update prediction accuracy based on motion type
            if (m_motionTypePredictionWeights.TryGetValue(prediction.MotionType, out float accuracy))
            {
                prediction.PredictionAccuracy = accuracy;
            }
        }

        private void GenerateMotionPredictions(MotionPrediction prediction, float deltaTime)
        {
            var position = prediction.CurrentPosition;
            var velocity = prediction.CurrentVelocity;
            var acceleration = prediction.CurrentAcceleration;
            var rotation = prediction.CurrentRotation;
            var angularVelocity = prediction.CurrentAngularVelocity;

            // Generate predictions based on motion type
            for (int i = 0; i < prediction.PredictedPositions.Length; i++)
            {
                float futureTime = (i + 1) * m_predictionTimeStep;
                prediction.TimeSteps[i] = futureTime;

                switch (prediction.MotionType)
                {
                    case MotionType.Static:
                        prediction.PredictedPositions[i] = position;
                        prediction.PredictedVelocities[i] = OMV.Vector3.Zero;
                        break;

                    case MotionType.Linear:
                        prediction.PredictedPositions[i] = PredictLinearMotion(position, velocity, futureTime);
                        prediction.PredictedVelocities[i] = velocity;
                        break;

                    case MotionType.Falling:
                    case MotionType.Projectile:
                        var gravityAcceleration = new OMV.Vector3(0, 0, -9.81f);
                        var totalAcceleration = acceleration + gravityAcceleration;
                        prediction.PredictedPositions[i] = PredictAcceleratedMotion(position, velocity, totalAcceleration, futureTime);
                        prediction.PredictedVelocities[i] = velocity + totalAcceleration * futureTime;
                        break;

                    case MotionType.Accelerating:
                    case MotionType.Decelerating:
                        prediction.PredictedPositions[i] = PredictAcceleratedMotion(position, velocity, acceleration, futureTime);
                        prediction.PredictedVelocities[i] = velocity + acceleration * futureTime;
                        break;

                    case MotionType.Circular:
                        prediction.PredictedPositions[i] = PredictCircularMotion(position, velocity, angularVelocity, futureTime);
                        prediction.PredictedVelocities[i] = RotateVector(velocity, angularVelocity * futureTime);
                        break;

                    case MotionType.Oscillatory:
                        prediction.PredictedPositions[i] = PredictOscillatoryMotion(position, velocity, futureTime);
                        prediction.PredictedVelocities[i] = velocity * MathF.Cos(futureTime * 2.0f);
                        break;

                    default:
                        // Default to linear prediction with some uncertainty
                        prediction.PredictedPositions[i] = PredictLinearMotion(position, velocity, futureTime);
                        prediction.PredictedVelocities[i] = velocity * (1.0f - futureTime * 0.1f); // Add some decay
                        break;
                }

                // Predict rotation
                if (angularVelocity.LengthSquared() > 0.001f)
                {
                    var rotationChange = CreateRotationFromAngularVelocity(angularVelocity, futureTime);
                    prediction.PredictedRotations[i] = rotation * rotationChange;
                }
                else
                {
                    prediction.PredictedRotations[i] = rotation;
                }
            }
        }

        private PredictedCollision PredictCollisionBetweenObjects(MotionPrediction obj1, MotionPrediction obj2)
        {
            // Check for potential collision across prediction timeline
            for (int i = 0; i < obj1.PredictedPositions.Length && i < obj2.PredictedPositions.Length; i++)
            {
                var pos1 = obj1.PredictedPositions[i];
                var pos2 = obj2.PredictedPositions[i];
                var distance = SIMDPhysicsMath.Distance(pos1, pos2);

                if (distance < m_collisionDistanceThreshold)
                {
                    // Potential collision found
                    var collision = new PredictedCollision
                    {
                        Object1ID = obj1.ObjectID,
                        Object2ID = obj2.ObjectID,
                        TimeToCollision = obj1.TimeSteps[i],
                        PredictedContactPoint = SIMDPhysicsMath.Add(pos1, SIMDPhysicsMath.Multiply(SIMDPhysicsMath.Subtract(pos2, pos1), 0.5f)),
                        PredictedContactNormal = SIMDPhysicsMath.Normalize(SIMDPhysicsMath.Subtract(pos2, pos1))
                    };

                    // Calculate impact velocity
                    var vel1 = obj1.PredictedVelocities[i];
                    var vel2 = obj2.PredictedVelocities[i];
                    var relativeVelocity = SIMDPhysicsMath.Subtract(vel1, vel2);
                    collision.PredictedImpactVelocity = SIMDPhysicsMath.Length(relativeVelocity);

                    // Calculate collision probability based on prediction accuracy
                    var combinedAccuracy = (obj1.PredictionAccuracy + obj2.PredictionAccuracy) / 2.0f;
                    var timeDecay = Math.Max(0.1f, 1.0f - collision.TimeToCollision * 0.5f);
                    collision.CollisionProbability = combinedAccuracy * timeDecay;

                    // Determine severity
                    collision.Severity = DetermineCollisionSeverity(collision.PredictedImpactVelocity);

                    // Check if preventive action is needed
                    collision.RequiresPreventiveAction = collision.CollisionProbability > 0.6f && 
                                                        collision.Severity >= CollisionSeverity.Moderate;

                    m_collisionsPredicted++;
                    return collision;
                }
            }

            return null;
        }

        #endregion

        #region Motion Prediction Algorithms

        private OMV.Vector3 PredictLinearMotion(OMV.Vector3 position, OMV.Vector3 velocity, float time)
        {
            return SIMDPhysicsMath.Add(position, SIMDPhysicsMath.Multiply(velocity, time));
        }

        private OMV.Vector3 PredictAcceleratedMotion(OMV.Vector3 position, OMV.Vector3 velocity, OMV.Vector3 acceleration, float time)
        {
            var linearComponent = SIMDPhysicsMath.Multiply(velocity, time);
            var accelerationComponent = SIMDPhysicsMath.Multiply(acceleration, 0.5f * time * time);
            return SIMDPhysicsMath.Add(position, SIMDPhysicsMath.Add(linearComponent, accelerationComponent));
        }

        private OMV.Vector3 PredictCircularMotion(OMV.Vector3 position, OMV.Vector3 velocity, OMV.Vector3 angularVelocity, float time)
        {
            // Simplified circular motion prediction
            var rotationAngle = SIMDPhysicsMath.Length(angularVelocity) * time;
            var rotatedVelocity = RotateVector(velocity, SIMDPhysicsMath.Normalize(angularVelocity) * rotationAngle);
            return SIMDPhysicsMath.Add(position, SIMDPhysicsMath.Multiply(rotatedVelocity, time));
        }

        private OMV.Vector3 PredictOscillatoryMotion(OMV.Vector3 position, OMV.Vector3 velocity, float time)
        {
            // Simplified harmonic oscillation
            var amplitude = SIMDPhysicsMath.Length(velocity) * 0.5f;
            var frequency = 2.0f; // Hz
            var oscillation = amplitude * MathF.Sin(frequency * time * 2.0f * MathF.PI);
            var direction = SIMDPhysicsMath.Normalize(velocity);
            return SIMDPhysicsMath.Add(position, SIMDPhysicsMath.Multiply(direction, oscillation));
        }

        private OMV.Vector3 RotateVector(OMV.Vector3 vector, OMV.Vector3 axis)
        {
            // Rodrigues' rotation formula for rotating vector around axis
            var angle = SIMDPhysicsMath.Length(axis);
            if (angle < 1e-6f) return vector;

            var normalizedAxis = SIMDPhysicsMath.Normalize(axis);
            var cosAngle = MathF.Cos(angle);
            var sinAngle = MathF.Sin(angle);

            var dotProduct = SIMDPhysicsMath.Dot(vector, normalizedAxis);
            var crossProduct = SIMDPhysicsMath.Cross(normalizedAxis, vector);

            return SIMDPhysicsMath.Add(
                SIMDPhysicsMath.Multiply(vector, cosAngle),
                SIMDPhysicsMath.Add(
                    SIMDPhysicsMath.Multiply(crossProduct, sinAngle),
                    SIMDPhysicsMath.Multiply(normalizedAxis, dotProduct * (1 - cosAngle))
                )
            );
        }

        private OMV.Quaternion CreateRotationFromAngularVelocity(OMV.Vector3 angularVelocity, float time)
        {
            var angle = SIMDPhysicsMath.Length(angularVelocity) * time;
            if (angle < 1e-6f) return OMV.Quaternion.Identity;

            var axis = SIMDPhysicsMath.Normalize(angularVelocity);
            var halfAngle = angle * 0.5f;
            var sinHalfAngle = MathF.Sin(halfAngle);
            var cosHalfAngle = MathF.Cos(halfAngle);

            return new OMV.Quaternion(
                axis.X * sinHalfAngle,
                axis.Y * sinHalfAngle,
                axis.Z * sinHalfAngle,
                cosHalfAngle
            );
        }

        #endregion

        #region Utility Methods

        private float CalculateVariance(float[] values)
        {
            if (values.Length < 2) return 0.0f;

            var mean = values.Average();
            var sumSquaredDifferences = values.Sum(x => (x - mean) * (x - mean));
            return sumSquaredDifferences / (values.Length - 1);
        }

        private bool IsCircularMotion(MotionSample[] samples)
        {
            if (samples.Length < 5) return false;

            // Check for consistent angular velocity and roughly constant distance from center
            var positions = samples.Select(s => s.Position).ToArray();
            var center = new OMV.Vector3(
                positions.Average(p => p.X),
                positions.Average(p => p.Y),
                positions.Average(p => p.Z)
            );

            var distances = positions.Select(p => SIMDPhysicsMath.Distance(p, center)).ToArray();
            var distanceVariance = CalculateVariance(distances);

            return distanceVariance < 1.0f; // Low variance in distance from center
        }

        private bool IsOscillatoryMotion(MotionSample[] samples)
        {
            if (samples.Length < 6) return false;

            // Check for velocity direction changes
            var velocities = samples.Select(s => s.Velocity).ToArray();
            int directionChanges = 0;

            for (int i = 1; i < velocities.Length - 1; i++)
            {
                var dot1 = SIMDPhysicsMath.Dot(velocities[i - 1], velocities[i]);
                var dot2 = SIMDPhysicsMath.Dot(velocities[i], velocities[i + 1]);
                
                if (dot1 < 0 || dot2 < 0) // Direction change
                {
                    directionChanges++;
                }
            }

            return directionChanges >= 2; // Multiple direction changes indicate oscillation
        }

        private CollisionSeverity DetermineCollisionSeverity(float impactVelocity)
        {
            if (impactVelocity < 1.0f) return CollisionSeverity.Minor;
            if (impactVelocity < 5.0f) return CollisionSeverity.Moderate;
            if (impactVelocity < 15.0f) return CollisionSeverity.Severe;
            return CollisionSeverity.Critical;
        }

        private float GetPredictionAccuracy()
        {
            var totalPredictions = m_correctPredictions + m_falsePredictions;
            return totalPredictions > 0 ? (float)m_correctPredictions / totalPredictions : 0.0f;
        }

        private void PerformPredictionUpdate(object state)
        {
            if (!m_enabled || m_disposed)
                return;

            try
            {
                // Clean up old predictions
                var cutoffTime = DateTime.UtcNow - TimeSpan.FromSeconds(5);
                var objectsToRemove = m_motionPredictions
                    .Where(kvp => kvp.Value.LastUpdate < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var objectId in objectsToRemove)
                {
                    m_motionPredictions.TryRemove(objectId, out _);
                }

                // Adaptive parameter adjustment
                AdaptPredictionParameters();
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during prediction update: {1}", LogHeader, ex.Message);
            }
        }

        private void AdaptPredictionParameters()
        {
            var accuracy = GetPredictionAccuracy();

            // Adjust accuracy threshold based on performance
            if (accuracy > 0.8f)
            {
                m_currentAccuracyThreshold = Math.Min(0.85f, m_currentAccuracyThreshold + 0.01f);
                m_dynamicPredictionRange = Math.Min(m_maxPredictionTime, m_dynamicPredictionRange * 1.05f);
            }
            else if (accuracy < 0.6f)
            {
                m_currentAccuracyThreshold = Math.Max(0.5f, m_currentAccuracyThreshold - 0.01f);
                m_dynamicPredictionRange = Math.Max(0.5f, m_dynamicPredictionRange * 0.95f);
            }
        }

        private void UpdatePerformanceStatistics()
        {
            DateTime now = DateTime.UtcNow;
            if (now - m_lastPerformanceReport >= PerformanceReportInterval)
            {
                m_log.InfoFormat("{0}: {1}", LogHeader, GetPerformanceReport().Replace("\\n", " "));
                m_lastPerformanceReport = now;
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
                m_predictionTimer?.Dispose();
                m_motionPredictions.Clear();

                m_disposed = true;
                m_log.InfoFormat("{0}: Predictive collision detection disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }

        #endregion

        #region Public Properties

        public bool IsEnabled => m_enabled && !m_disposed;
        public int TrackedObjectCount => m_motionPredictions.Count;
        public long PredictionsGenerated => m_predictionsGenerated;
        public long CollisionsPredicted => m_collisionsPredicted;
        public float PredictionAccuracy => GetPredictionAccuracy();
        public float AverageProcessingTime => m_averagePredictionTime;

        #endregion
    }
}