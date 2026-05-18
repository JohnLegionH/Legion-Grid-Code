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
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OMV = OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Priority levels for destruction processing
    /// </summary>
    public enum DestructionPriority
    {
        Low = 0,        // Background processing, not time-critical
        Normal = 1,     // Standard destruction events
        High = 2,       // Player-initiated or important destructions
        Critical = 3    // Emergency processing, blocking
    }

    /// <summary>
    /// Status of a destruction job
    /// </summary>
    public enum DestructionJobStatus
    {
        Queued,         // Waiting to be processed
        Processing,     // Currently being processed
        AwaitingSync,   // Waiting for main thread sync
        Completed,      // Successfully completed
        Failed,         // Processing failed
        Cancelled       // Job was cancelled
    }

    /// <summary>
    /// A destruction job that can be processed asynchronously
    /// </summary>
    public class DestructionJob
    {
        public Guid JobId { get; set; } = Guid.NewGuid();
        public uint ObjectId { get; set; }
        public OMV.Vector3 ImpactPoint { get; set; }
        public OMV.Vector3 Force { get; set; }
        public DestructionPriority Priority { get; set; } = DestructionPriority.Normal;
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
        public DateTime? StartedTime { get; set; }
        public DateTime? CompletedTime { get; set; }
        public DestructionJobStatus Status { get; set; } = DestructionJobStatus.Queued;
        public string ErrorMessage { get; set; }
        public DestructibleObject SourceObject { get; set; }
        
        // Pre-computed results from background processing
        public DestructionResults Results { get; set; }
        
        public TimeSpan ProcessingTime => (CompletedTime ?? DateTime.UtcNow) - (StartedTime ?? CreatedTime);
        public TimeSpan QueueTime => (StartedTime ?? DateTime.UtcNow) - CreatedTime;
    }

    /// <summary>
    /// Results from background destruction processing
    /// </summary>
    public class DestructionResults
    {
        public DestructionFragment[] Fragments { get; set; } = new DestructionFragment[0];
        public ParticleEffectData[] ParticleEffects { get; set; } = new ParticleEffectData[0];
        public SoundEffectData[] SoundEffects { get; set; } = new SoundEffectData[0];
        public EnvironmentalEffectData[] EnvironmentalEffects { get; set; } = new EnvironmentalEffectData[0];
        public ChainReactionData[] ChainReactions { get; set; } = new ChainReactionData[0];
        public bool Success { get; set; }
        public double ProcessingTimeMs { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Pre-computed particle effect data
    /// </summary>
    public class ParticleEffectData
    {
        public OMV.Vector3 Position { get; set; }
        public MaterialType MaterialType { get; set; }
        public float Intensity { get; set; }
        public OMV.Vector3 Direction { get; set; }
    }

    /// <summary>
    /// Pre-computed sound effect data
    /// </summary>
    public class SoundEffectData
    {
        public OMV.Vector3 Position { get; set; }
        public OMV.UUID SoundId { get; set; }
        public float Volume { get; set; }
        public MaterialType MaterialType { get; set; }
    }

    /// <summary>
    /// Pre-computed environmental effect data
    /// </summary>
    public class EnvironmentalEffectData
    {
        public OMV.Vector3 Position { get; set; }
        public MaterialType MaterialType { get; set; }
        public float Duration { get; set; }
        public float Radius { get; set; }
        public OMV.Vector3 Color { get; set; }
    }

    /// <summary>
    /// Pre-computed chain reaction data
    /// </summary>
    public class ChainReactionData
    {
        public uint TargetObjectId { get; set; }
        public OMV.Vector3 ImpactPoint { get; set; }
        public OMV.Vector3 Force { get; set; }
        public float Delay { get; set; }
    }

    /// <summary>
    /// Asynchronous destruction processor that handles time-consuming operations
    /// in background threads to avoid blocking the main simulation thread
    /// </summary>
    public class AsyncDestructionProcessor : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[ASYNC DESTRUCTION]";

        private readonly Scene m_scene;
        private readonly DestructionPerformanceProfiler m_profiler;
        private readonly DestructiblePhysicsSystem m_physicsSystem;

        // Job queues by priority
        private readonly ConcurrentQueue<DestructionJob>[] m_jobQueues;
        private readonly ConcurrentDictionary<Guid, DestructionJob> m_activeJobs;
        private readonly ConcurrentQueue<DestructionJob> m_completedJobs;
        
        // Processing threads
        private readonly Task[] m_processingTasks;
        private readonly CancellationTokenSource m_cancellationTokenSource;
        private readonly SemaphoreSlim m_jobSemaphore;
        
        // Main thread sync timer
        private readonly Timer m_syncTimer;
        
        // Statistics
        private long m_totalJobsProcessed;
        private long m_totalJobsFailed;
        private double m_averageProcessingTime;
        private int m_activeJobCount;
        
        // Configuration
        private readonly int m_maxConcurrentJobs;
        private readonly int m_maxQueueSize;
        private readonly TimeSpan m_jobTimeout;
        private readonly bool m_enablePriorityProcessing;

        public AsyncDestructionProcessor(Scene scene, DestructiblePhysicsSystem physicsSystem, 
            DestructionPerformanceProfiler profiler, int maxConcurrentJobs = 4)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_physicsSystem = physicsSystem ?? throw new ArgumentNullException(nameof(physicsSystem));
            m_profiler = profiler;
            
            m_maxConcurrentJobs = Math.Max(1, Math.Min(maxConcurrentJobs, Environment.ProcessorCount));
            m_maxQueueSize = m_maxConcurrentJobs * 50; // Allow 50 jobs per worker thread
            m_jobTimeout = TimeSpan.FromSeconds(30);
            m_enablePriorityProcessing = true;

            // Initialize collections
            m_jobQueues = new ConcurrentQueue<DestructionJob>[Enum.GetValues<DestructionPriority>().Length];
            for (int i = 0; i < m_jobQueues.Length; i++)
            {
                m_jobQueues[i] = new ConcurrentQueue<DestructionJob>();
            }
            
            m_activeJobs = new ConcurrentDictionary<Guid, DestructionJob>();
            m_completedJobs = new ConcurrentQueue<DestructionJob>();
            m_cancellationTokenSource = new CancellationTokenSource();
            m_jobSemaphore = new SemaphoreSlim(m_maxConcurrentJobs, m_maxConcurrentJobs);

            // Start processing tasks
            m_processingTasks = new Task[m_maxConcurrentJobs];
            for (int i = 0; i < m_maxConcurrentJobs; i++)
            {
                m_processingTasks[i] = Task.Factory.StartNew(ProcessJobsAsync, 
                    m_cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            // Start main thread sync timer (process completed jobs)
            m_syncTimer = new Timer(ProcessCompletedJobs, null, TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16)); // ~60 FPS

            m_log.InfoFormat("{0}: Async destruction processor initialized with {1} worker threads", LogHeader, m_maxConcurrentJobs);
        }

        /// <summary>
        /// Queue a destruction job for asynchronous processing
        /// </summary>
        public bool QueueDestruction(uint objectId, OMV.Vector3 impactPoint, OMV.Vector3 force, 
            DestructibleObject sourceObject, DestructionPriority priority = DestructionPriority.Normal)
        {
            // Check queue capacity
            var totalQueuedJobs = GetTotalQueuedJobs();
            if (totalQueuedJobs >= m_maxQueueSize)
            {
                m_log.WarnFormat("{0}: Job queue full ({1} jobs), dropping destruction request for object {2}",
                    LogHeader, totalQueuedJobs, objectId);
                return false;
            }

            var job = new DestructionJob
            {
                ObjectId = objectId,
                ImpactPoint = impactPoint,
                Force = force,
                Priority = priority,
                SourceObject = sourceObject
            };

            // Queue by priority
            var queueIndex = (int)priority;
            m_jobQueues[queueIndex].Enqueue(job);
            
            m_log.DebugFormat("{0}: Queued destruction job {1} for object {2} with priority {3}",
                LogHeader, job.JobId, objectId, priority);

            return true;
        }

        /// <summary>
        /// Get the number of jobs currently queued
        /// </summary>
        public int GetQueuedJobCount(DestructionPriority? priority = null)
        {
            if (priority.HasValue)
            {
                return m_jobQueues[(int)priority.Value].Count;
            }

            return GetTotalQueuedJobs();
        }

        /// <summary>
        /// Get the number of jobs currently being processed
        /// </summary>
        public int GetActiveJobCount()
        {
            return m_activeJobs.Count;
        }

        /// <summary>
        /// Get performance statistics
        /// </summary>
        public (long totalProcessed, long totalFailed, double avgProcessingTime, int activeJobs, int queuedJobs) GetStatistics()
        {
            return (m_totalJobsProcessed, m_totalJobsFailed, m_averageProcessingTime, 
                   m_activeJobs.Count, GetTotalQueuedJobs());
        }

        /// <summary>
        /// Process jobs asynchronously in background thread
        /// </summary>
        private async Task ProcessJobsAsync()
        {
            var cancellationToken = m_cancellationTokenSource.Token;
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for available job slot
                    await m_jobSemaphore.WaitAsync(cancellationToken);
                    
                    // Get next job to process
                    var job = GetNextJob();
                    if (job == null)
                    {
                        m_jobSemaphore.Release();
                        await Task.Delay(10, cancellationToken); // Brief delay if no jobs available
                        continue;
                    }

                    // Process the job
                    await ProcessJobAsync(job, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break; // Expected when shutting down
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("{0}: Error in job processing loop: {1}", LogHeader, ex.Message);
                    await Task.Delay(1000, cancellationToken); // Brief delay before retrying
                }
            }
        }

        /// <summary>
        /// Get the next job to process, respecting priority
        /// </summary>
        private DestructionJob GetNextJob()
        {
            // Process jobs by priority (highest first)
            if (m_enablePriorityProcessing)
            {
                for (int priority = m_jobQueues.Length - 1; priority >= 0; priority--)
                {
                    if (m_jobQueues[priority].TryDequeue(out var job))
                    {
                        return job;
                    }
                }
            }
            else
            {
                // Round-robin processing for fairness
                for (int i = 0; i < m_jobQueues.Length; i++)
                {
                    if (m_jobQueues[i].TryDequeue(out var job))
                    {
                        return job;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Process a single job asynchronously
        /// </summary>
        private async Task ProcessJobAsync(DestructionJob job, CancellationToken cancellationToken)
        {
            try
            {
                // Track active job
                job.Status = DestructionJobStatus.Processing;
                job.StartedTime = DateTime.UtcNow;
                m_activeJobs[job.JobId] = job;

                using (var measurement = m_profiler?.StartMeasurement(ProfileOperation.TotalDestruction, 1, 
                    $"Job {job.JobId} Priority {job.Priority}"))
                {
                    // Process the job with timeout
                    using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutCts.CancelAfter(m_jobTimeout);
                        
                        try
                        {
                            job.Results = await ProcessDestructionAsync(job, timeoutCts.Token);
                            job.Status = DestructionJobStatus.AwaitingSync;
                            
                            // Queue for main thread processing
                            m_completedJobs.Enqueue(job);
                        }
                        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            // Job timed out
                            job.Status = DestructionJobStatus.Failed;
                            job.ErrorMessage = $"Job timed out after {m_jobTimeout.TotalSeconds} seconds";
                            m_log.WarnFormat("{0}: Job {1} timed out", LogHeader, job.JobId);
                            Interlocked.Increment(ref m_totalJobsFailed);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                job.Status = DestructionJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                m_log.ErrorFormat("{0}: Error processing job {1}: {2}", LogHeader, job.JobId, ex.Message);
                Interlocked.Increment(ref m_totalJobsFailed);
            }
            finally
            {
                // Remove from active jobs and release semaphore
                m_activeJobs.TryRemove(job.JobId, out _);
                m_jobSemaphore.Release();
                
                job.CompletedTime = DateTime.UtcNow;
                
                // Update statistics
                Interlocked.Increment(ref m_totalJobsProcessed);
                var processingTime = job.ProcessingTime.TotalMilliseconds;
                var currentAvg = m_averageProcessingTime;
                var newAvg = (currentAvg * 0.95) + (processingTime * 0.05); // Weighted average
                Interlocked.Exchange(ref m_averageProcessingTime, newAvg);
            }
        }

        /// <summary>
        /// Perform the actual destruction calculations in background
        /// </summary>
        private async Task<DestructionResults> ProcessDestructionAsync(DestructionJob job, CancellationToken cancellationToken)
        {
            var results = new DestructionResults();
            var startTime = DateTime.UtcNow;

            try
            {
                await Task.Yield(); // Ensure we're running on thread pool thread

                // Generate fragments (CPU intensive)
                using (var fragmentMeasurement = m_profiler?.StartMeasurement(ProfileOperation.FragmentGeneration))
                {
                    results.Fragments = await GenerateFragmentsAsync(job, cancellationToken);
                }

                // Pre-compute particle effects
                using (var particleMeasurement = m_profiler?.StartMeasurement(ProfileOperation.ParticleEffects))
                {
                    results.ParticleEffects = await GenerateParticleEffectsAsync(job, cancellationToken);
                }

                // Pre-compute sound effects
                using (var soundMeasurement = m_profiler?.StartMeasurement(ProfileOperation.SoundEffects))
                {
                    results.SoundEffects = await GenerateSoundEffectsAsync(job, cancellationToken);
                }

                // Pre-compute environmental effects
                using (var envMeasurement = m_profiler?.StartMeasurement(ProfileOperation.EnvironmentalEffects))
                {
                    results.EnvironmentalEffects = await GenerateEnvironmentalEffectsAsync(job, cancellationToken);
                }

                // Pre-compute chain reactions
                using (var chainMeasurement = m_profiler?.StartMeasurement(ProfileOperation.ChainReactions))
                {
                    results.ChainReactions = await GenerateChainReactionsAsync(job, cancellationToken);
                }

                results.Success = true;
            }
            catch (OperationCanceledException)
            {
                results.Success = false;
                results.ErrorMessage = "Destruction processing was cancelled";
                m_log.InfoFormat("{0}: Destruction processing cancelled for object {1}", LogHeader, job.ObjectId);
                // Don't rethrow cancellation exceptions
            }
            catch (Exception ex)
            {
                results.Success = false;
                results.ErrorMessage = ex.Message;
                m_log.ErrorFormat("{0}: Error processing destruction for object {1}: {2}", LogHeader, job.ObjectId, ex);
                // Return failed results instead of throwing to prevent crash
            }
            finally
            {
                results.ProcessingTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            }

            return results;
        }

        /// <summary>
        /// Generate fragments asynchronously
        /// </summary>
        private async Task<DestructionFragment[]> GenerateFragmentsAsync(DestructionJob job, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            
            // Use the physics system's fragment generation but run async
            return await Task.Run(() => 
            {
                // This calls the existing GenerateFragments method from the physics system
                // but runs it in a background thread
                var fragments = m_physicsSystem.GenerateFragmentsForObject(job.SourceObject, job.ImpactPoint, job.Force);
                return fragments.ToArray();
            }, cancellationToken);
        }

        // Placeholder methods for other async operations
        private async Task<ParticleEffectData[]> GenerateParticleEffectsAsync(DestructionJob job, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            
            // Pre-compute particle effect data
            var effectData = new ParticleEffectData
            {
                Position = job.ImpactPoint,
                MaterialType = (MaterialType)job.SourceObject.Material.MaterialType,
                Intensity = Math.Min(job.Force.Length() / 50.0f, 3.0f),
                Direction = job.Force.Length() > 0 ? job.Force / job.Force.Length() : OMV.Vector3.UnitZ
            };
            
            return new[] { effectData };
        }

        private async Task<SoundEffectData[]> GenerateSoundEffectsAsync(DestructionJob job, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            
            var soundData = new SoundEffectData
            {
                Position = job.ImpactPoint,
                MaterialType = (MaterialType)job.SourceObject.Material.MaterialType,
                Volume = Math.Min(job.Force.Length() / 20.0f, 1.0f),
                SoundId = GetMaterialSoundId((MaterialType)job.SourceObject.Material.MaterialType)
            };
            
            return new[] { soundData };
        }

        private async Task<EnvironmentalEffectData[]> GenerateEnvironmentalEffectsAsync(DestructionJob job, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            
            var envData = new EnvironmentalEffectData
            {
                Position = job.ImpactPoint,
                MaterialType = (MaterialType)job.SourceObject.Material.MaterialType,
                Duration = 45.0f + (job.Force.Length() * 2.0f),
                Radius = 2.0f + (job.Force.Length() * 0.1f),
                Color = GetMaterialColor((MaterialType)job.SourceObject.Material.MaterialType)
            };
            
            return new[] { envData };
        }

        private async Task<ChainReactionData[]> GenerateChainReactionsAsync(DestructionJob job, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            
            // For now, return empty array - chain reactions would be computed here
            return new ChainReactionData[0];
        }

        /// <summary>
        /// Process completed jobs on main thread (called by timer)
        /// </summary>
        private void ProcessCompletedJobs(object state)
        {
            try
            {
                int processedCount = 0;
                const int maxProcessPerFrame = 5; // Limit to avoid blocking main thread

                while (m_completedJobs.TryDequeue(out var job) && processedCount < maxProcessPerFrame)
                {
                    if (job.Results != null && job.Results.Success)
                    {
                        ApplyDestructionResults(job);
                    }
                    processedCount++;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing completed jobs: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Apply the pre-computed destruction results to the scene (main thread only)
        /// </summary>
        private void ApplyDestructionResults(DestructionJob job)
        {
            try
            {
                using (var measurement = m_profiler?.StartMeasurement(ProfileOperation.SceneObjectCreation, 
                    job.Results.Fragments.Length, "Applying async results"))
                {
                    // Apply fragments, effects, sounds etc. to the scene
                    m_physicsSystem.ApplyAsyncDestructionResults(job.ObjectId, job.Results);
                    
                    job.Status = DestructionJobStatus.Completed;
                    
                    m_log.DebugFormat("{0}: Applied destruction results for job {1} - {2} fragments, {3} effects",
                        LogHeader, job.JobId, job.Results.Fragments.Length, 
                        job.Results.ParticleEffects.Length + job.Results.SoundEffects.Length);
                }
            }
            catch (Exception ex)
            {
                job.Status = DestructionJobStatus.Failed;
                job.ErrorMessage = $"Failed to apply results: {ex.Message}";
                m_log.ErrorFormat("{0}: Error applying destruction results for job {1}: {2}", 
                    LogHeader, job.JobId, ex.Message);
            }
        }

        private int GetTotalQueuedJobs()
        {
            int total = 0;
            for (int i = 0; i < m_jobQueues.Length; i++)
            {
                total += m_jobQueues[i].Count;
            }
            return total;
        }

        private OMV.UUID GetMaterialSoundId(MaterialType materialType)
        {
            return materialType switch
            {
                MaterialType.Glass => OMV.UUID.Parse("ed124764-705d-d497-167a-182cd9fa2e6c"),
                MaterialType.Metal => OMV.UUID.Parse("4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6"),
                MaterialType.Wood => OMV.UUID.Parse("f4a0660f-5446-dea2-80b7-6482a082803c"),
                MaterialType.Stone => OMV.UUID.Parse("0cb7b00a-4c10-6948-84de-a93c09af2ba9"),
                MaterialType.Concrete => OMV.UUID.Parse("d7a9a565-a013-2a69-797d-5332baa1a947"),
                _ => OMV.UUID.Parse("4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6")
            };
        }

        private OMV.Vector3 GetMaterialColor(MaterialType materialType)
        {
            return materialType switch
            {
                MaterialType.Glass => new OMV.Vector3(0.9f, 0.95f, 1.0f),
                MaterialType.Metal => new OMV.Vector3(1.0f, 0.6f, 0.1f),
                MaterialType.Wood => new OMV.Vector3(0.7f, 0.5f, 0.2f),
                MaterialType.Stone => new OMV.Vector3(0.6f, 0.6f, 0.5f),
                MaterialType.Concrete => new OMV.Vector3(0.5f, 0.5f, 0.5f),
                _ => new OMV.Vector3(0.6f, 0.6f, 0.6f)
            };
        }

        public void Dispose()
        {
            try
            {
                m_cancellationTokenSource.Cancel();
                
                // Wait for all processing tasks to complete
                if (m_processingTasks != null)
                {
                    Task.WaitAll(m_processingTasks, TimeSpan.FromSeconds(5));
                }
                
                m_syncTimer?.Dispose();
                m_jobSemaphore?.Dispose();
                m_cancellationTokenSource?.Dispose();
                
                m_log.InfoFormat("{0}: Async destruction processor disposed", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error during disposal: {1}", LogHeader, ex.Message);
            }
        }
    }
}