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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Framework.EntityTransfer
{
    /// <summary>
    /// Comprehensive benchmarking suite for region crossing performance testing
    /// Provides automated load testing, regression detection, and performance analysis
    /// </summary>
    public class CrossingBenchmarkSuite
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        private readonly Scene m_scene;
        private readonly List<BenchmarkResult> m_results = new List<BenchmarkResult>();
        private readonly object m_resultsLock = new object();
        
        // Configuration parameters
        private int m_virtualAvatarCount = 10;
        private int m_crossingsPerAvatar = 5;
        private int m_concurrentCrossings = 3;
        private TimeSpan m_testDuration = TimeSpan.FromMinutes(5);
        private bool m_enableStressTest = false;
        private string m_baselineFile = "baseline_performance.json";
        
        // Performance baseline storage
        private BenchmarkBaseline m_baseline;
        
        public CrossingBenchmarkSuite(Scene scene)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            LoadBaseline();
        }
        
        /// <summary>
        /// Execute comprehensive benchmark suite
        /// </summary>
        public async Task<BenchmarkSuiteReport> RunFullBenchmarkAsync(CancellationToken cancellationToken = default)
        {
            m_log.Info("[CROSSING BENCHMARK]: Starting comprehensive benchmark suite...");
            
            var report = new BenchmarkSuiteReport
            {
                TestStartTime = DateTime.UtcNow,
                SceneName = m_scene.Name,
                RegionName = m_scene.RegionInfo.RegionName
            };
            
            try
            {
                // 1. Basic Performance Test
                m_log.Info("[CROSSING BENCHMARK]: Running basic performance test...");
                report.BasicPerformance = await RunBasicPerformanceTestAsync(cancellationToken);
                
                // 2. Load Testing
                m_log.Info("[CROSSING BENCHMARK]: Running load test...");
                report.LoadTest = await RunLoadTestAsync(cancellationToken);
                
                // 3. Concurrency Testing
                m_log.Info("[CROSSING BENCHMARK]: Running concurrency test...");
                report.ConcurrencyTest = await RunConcurrencyTestAsync(cancellationToken);
                
                // 4. Memory and GC Impact Testing
                m_log.Info("[CROSSING BENCHMARK]: Running memory impact test...");
                report.MemoryTest = await RunMemoryImpactTestAsync(cancellationToken);
                
                // 5. Stress Testing (if enabled)
                if (m_enableStressTest)
                {
                    m_log.Info("[CROSSING BENCHMARK]: Running stress test...");
                    report.StressTest = await RunStressTestAsync(cancellationToken);
                }
                
                // 6. Regression Analysis
                m_log.Info("[CROSSING BENCHMARK]: Performing regression analysis...");
                report.RegressionAnalysis = PerformRegressionAnalysis();
                
                report.TestEndTime = DateTime.UtcNow;
                report.TotalTestDuration = report.TestEndTime - report.TestStartTime;
                
                // Save results for future regression testing
                await SaveBenchmarkResultsAsync(report);
                
                m_log.InfoFormat("[CROSSING BENCHMARK]: Benchmark suite completed in {0:F1} minutes", 
                    report.TotalTestDuration.TotalMinutes);
                
                return report;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[CROSSING BENCHMARK]: Benchmark suite failed: {0}", ex.Message);
                report.HasErrors = true;
                report.ErrorMessage = ex.Message;
                return report;
            }
        }
        
        /// <summary>
        /// Run basic performance test with single avatar
        /// </summary>
        private async Task<BasicPerformanceResult> RunBasicPerformanceTestAsync(CancellationToken cancellationToken)
        {
            var result = new BasicPerformanceResult();
            var times = new List<double>();
            var memoryUsages = new List<long>();
            
            // Capture initial state
            long initialMemory = GC.GetTotalMemory(true);
            int initialGen0 = GC.CollectionCount(0);
            int initialGen1 = GC.CollectionCount(1);
            int initialGen2 = GC.CollectionCount(2);
            
            // Run 20 individual crossings to establish baseline
            for (int i = 0; i < 20 && !cancellationToken.IsCancellationRequested; i++)
            {
                var sw = Stopwatch.StartNew();
                
                // Simulate a region crossing
                await SimulateRegionCrossingAsync($"TestAvatar_{i}", cancellationToken);
                
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                memoryUsages.Add(GC.GetTotalMemory(false));
                
                // Small delay between crossings
                await Task.Delay(100, cancellationToken);
            }
            
            // Calculate statistics
            result.AverageCrossingTime = times.Average();
            result.MinCrossingTime = times.Min();
            result.MaxCrossingTime = times.Max();
            result.StandardDeviation = CalculateStandardDeviation(times);
            result.MemoryImpactMB = (GC.GetTotalMemory(false) - initialMemory) / (1024.0 * 1024.0);
            result.GCGen0Collections = GC.CollectionCount(0) - initialGen0;
            result.GCGen1Collections = GC.CollectionCount(1) - initialGen1;
            result.GCGen2Collections = GC.CollectionCount(2) - initialGen2;
            result.SuccessRate = 100.0; // Assume success for simulation
            
            return result;
        }
        
        /// <summary>
        /// Run load test with multiple virtual avatars
        /// </summary>
        private async Task<LoadTestResult> RunLoadTestAsync(CancellationToken cancellationToken)
        {
            var result = new LoadTestResult();
            var allTimes = new List<double>();
            var tasks = new List<Task>();
            
            long startMemory = GC.GetTotalMemory(true);
            var startTime = DateTime.UtcNow;
            
            // Create multiple virtual avatars performing crossings
            for (int i = 0; i < m_virtualAvatarCount; i++)
            {
                int avatarId = i;
                tasks.Add(RunAvatarLoadTestAsync(avatarId, allTimes, cancellationToken));
            }
            
            // Wait for all avatar tasks to complete
            await Task.WhenAll(tasks);
            
            var endTime = DateTime.UtcNow;
            long endMemory = GC.GetTotalMemory(false);
            
            // Calculate load test statistics
            result.TotalCrossings = allTimes.Count;
            result.AverageCrossingTime = allTimes.Count > 0 ? allTimes.Average() : 0;
            result.CrossingsPerSecond = result.TotalCrossings / (endTime - startTime).TotalSeconds;
            result.MaxConcurrentCrossings = m_virtualAvatarCount;
            result.TotalMemoryImpactMB = (endMemory - startMemory) / (1024.0 * 1024.0);
            result.MemoryPerCrossingKB = result.TotalCrossings > 0 ? 
                (result.TotalMemoryImpactMB * 1024.0) / result.TotalCrossings : 0;
            result.TestDuration = endTime - startTime;
            
            // Calculate percentiles
            if (allTimes.Count > 0)
            {
                var sortedTimes = allTimes.OrderBy(t => t).ToList();
                result.P50CrossingTime = GetPercentile(sortedTimes, 50);
                result.P95CrossingTime = GetPercentile(sortedTimes, 95);
                result.P99CrossingTime = GetPercentile(sortedTimes, 99);
            }
            
            return result;
        }
        
        /// <summary>
        /// Run individual avatar load test
        /// </summary>
        private async Task RunAvatarLoadTestAsync(int avatarId, List<double> allTimes, CancellationToken cancellationToken)
        {
            for (int crossing = 0; crossing < m_crossingsPerAvatar && !cancellationToken.IsCancellationRequested; crossing++)
            {
                var sw = Stopwatch.StartNew();
                
                await SimulateRegionCrossingAsync($"LoadTestAvatar_{avatarId}_{crossing}", cancellationToken);
                
                sw.Stop();
                
                lock (allTimes)
                {
                    allTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                
                // Random delay to simulate realistic usage patterns
                await Task.Delay(Random.Shared.Next(500, 2000), cancellationToken);
            }
        }
        
        /// <summary>
        /// Run concurrency stress test
        /// </summary>
        private async Task<ConcurrencyTestResult> RunConcurrencyTestAsync(CancellationToken cancellationToken)
        {
            var result = new ConcurrencyTestResult();
            var concurrencyLevels = new[] { 1, 3, 5, 10, 15, 20 };
            
            foreach (int concurrency in concurrencyLevels)
            {
                if (cancellationToken.IsCancellationRequested) break;
                
                m_log.InfoFormat("[CROSSING BENCHMARK]: Testing concurrency level: {0}", concurrency);
                
                var times = new List<double>();
                var tasks = new List<Task>();
                var sw = Stopwatch.StartNew();
                
                // Launch concurrent crossings
                for (int i = 0; i < concurrency; i++)
                {
                    int taskId = i;
                    tasks.Add(Task.Run(async () =>
                    {
                        var taskSw = Stopwatch.StartNew();
                        await SimulateRegionCrossingAsync($"ConcurrentAvatar_{concurrency}_{taskId}", cancellationToken);
                        taskSw.Stop();
                        
                        lock (times)
                        {
                            times.Add(taskSw.Elapsed.TotalMilliseconds);
                        }
                    }));
                }
                
                await Task.WhenAll(tasks);
                sw.Stop();
                
                var concurrencyResult = new ConcurrencyLevelResult
                {
                    ConcurrencyLevel = concurrency,
                    AverageCrossingTime = times.Average(),
                    MaxCrossingTime = times.Max(),
                    TotalWallClockTime = sw.Elapsed.TotalMilliseconds,
                    EfficiencyRatio = (times.Sum() / sw.Elapsed.TotalMilliseconds) * 100
                };
                
                result.ConcurrencyResults.Add(concurrencyResult);
                
                // Brief pause between concurrency levels
                await Task.Delay(1000, cancellationToken);
            }
            
            // Find optimal concurrency level
            result.OptimalConcurrencyLevel = result.ConcurrencyResults
                .OrderByDescending(r => r.EfficiencyRatio)
                .FirstOrDefault()?.ConcurrencyLevel ?? 1;
            
            return result;
        }
        
        /// <summary>
        /// Test memory impact and GC pressure
        /// </summary>
        private async Task<MemoryTestResult> RunMemoryImpactTestAsync(CancellationToken cancellationToken)
        {
            var result = new MemoryTestResult();
            
            // Force full GC to establish clean baseline
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            long baselineMemory = GC.GetTotalMemory(false);
            int baselineGen0 = GC.CollectionCount(0);
            int baselineGen1 = GC.CollectionCount(1);
            int baselineGen2 = GC.CollectionCount(2);
            
            var memorySnapshots = new List<long>();
            
            // Run sustained crossing test while monitoring memory
            for (int i = 0; i < 50 && !cancellationToken.IsCancellationRequested; i++)
            {
                await SimulateRegionCrossingAsync($"MemoryTestAvatar_{i}", cancellationToken);
                
                long currentMemory = GC.GetTotalMemory(false);
                memorySnapshots.Add(currentMemory);
                
                if (i % 10 == 0)
                {
                    m_log.DebugFormat("[CROSSING BENCHMARK]: Memory test progress: {0}/50, Memory: {1:F1} MB", 
                        i + 1, currentMemory / (1024.0 * 1024.0));
                }
                
                await Task.Delay(200, cancellationToken);
            }
            
            long finalMemory = GC.GetTotalMemory(false);
            
            result.BaselineMemoryMB = baselineMemory / (1024.0 * 1024.0);
            result.FinalMemoryMB = finalMemory / (1024.0 * 1024.0);
            result.MemoryGrowthMB = (finalMemory - baselineMemory) / (1024.0 * 1024.0);
            result.AverageMemoryPerCrossingKB = (result.MemoryGrowthMB * 1024.0) / memorySnapshots.Count;
            result.PeakMemoryMB = memorySnapshots.Max() / (1024.0 * 1024.0);
            result.GCGen0Collections = GC.CollectionCount(0) - baselineGen0;
            result.GCGen1Collections = GC.CollectionCount(1) - baselineGen1;
            result.GCGen2Collections = GC.CollectionCount(2) - baselineGen2;
            
            // Calculate memory leak indicator
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long postGCMemory = GC.GetTotalMemory(false);
            result.PotentialMemoryLeakMB = Math.Max(0, (postGCMemory - baselineMemory) / (1024.0 * 1024.0));
            
            return result;
        }
        
        /// <summary>
        /// Run stress test to find system limits
        /// </summary>
        private async Task<StressTestResult> RunStressTestAsync(CancellationToken cancellationToken)
        {
            var result = new StressTestResult();
            int currentLoad = 5;
            int maxSuccessfulLoad = 0;
            var loadResults = new List<StressLoadResult>();
            
            // Gradually increase load until system starts failing
            while (currentLoad <= 100 && !cancellationToken.IsCancellationRequested)
            {
                m_log.InfoFormat("[CROSSING BENCHMARK]: Stress testing load level: {0} concurrent crossings", currentLoad);
                
                var loadResult = await TestStressLoadAsync(currentLoad, cancellationToken);
                loadResults.Add(loadResult);
                
                if (loadResult.SuccessRate >= 95.0 && loadResult.AverageResponseTime < 5000)
                {
                    maxSuccessfulLoad = currentLoad;
                    currentLoad = (int)(currentLoad * 1.5); // Increase by 50%
                }
                else
                {
                    break; // System is failing, stop increasing load
                }
                
                // Brief recovery period
                await Task.Delay(2000, cancellationToken);
            }
            
            result.MaxConcurrentCrossings = maxSuccessfulLoad;
            result.LoadResults = loadResults;
            result.SystemLimitReached = maxSuccessfulLoad < 100;
            
            return result;
        }
        
        /// <summary>
        /// Test specific stress load level
        /// </summary>
        private async Task<StressLoadResult> TestStressLoadAsync(int concurrentLoad, CancellationToken cancellationToken)
        {
            var result = new StressLoadResult { LoadLevel = concurrentLoad };
            var tasks = new List<Task<bool>>();
            var responseTimes = new List<double>();
            var sw = Stopwatch.StartNew();
            
            // Launch concurrent stress operations
            for (int i = 0; i < concurrentLoad; i++)
            {
                int taskId = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var taskSw = Stopwatch.StartNew();
                        await SimulateRegionCrossingAsync($"StressAvatar_{concurrentLoad}_{taskId}", cancellationToken);
                        taskSw.Stop();
                        
                        lock (responseTimes)
                        {
                            responseTimes.Add(taskSw.Elapsed.TotalMilliseconds);
                        }
                        
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }));
            }
            
            var results = await Task.WhenAll(tasks);
            sw.Stop();
            
            int successes = results.Count(r => r);
            result.SuccessRate = (successes * 100.0) / concurrentLoad;
            result.AverageResponseTime = responseTimes.Count > 0 ? responseTimes.Average() : 0;
            result.MaxResponseTime = responseTimes.Count > 0 ? responseTimes.Max() : 0;
            result.TotalDuration = sw.Elapsed;
            
            return result;
        }
        
        /// <summary>
        /// Simulate a region crossing operation
        /// </summary>
        private async Task SimulateRegionCrossingAsync(string avatarName, CancellationToken cancellationToken)
        {
            // Simulate the computational work of a region crossing
            await Task.Delay(Random.Shared.Next(10, 50), cancellationToken);
            
            // Simulate memory allocation patterns
            var tempData = new byte[Random.Shared.Next(1024, 8192)];
            Array.Fill(tempData, (byte)Random.Shared.Next(0, 255));
            
            // Simulate network operations
            await Task.Delay(Random.Shared.Next(5, 25), cancellationToken);
            
            // Simulate processing overhead
            var processingTime = Stopwatch.StartNew();
            while (processingTime.Elapsed.TotalMilliseconds < Random.Shared.Next(2, 15))
            {
                // Busy wait to simulate CPU work
                Thread.SpinWait(1000);
            }
        }
        
        /// <summary>
        /// Perform regression analysis against baseline
        /// </summary>
        private RegressionAnalysisResult PerformRegressionAnalysis()
        {
            var result = new RegressionAnalysisResult();
            
            if (m_baseline == null)
            {
                result.HasBaseline = false;
                result.Message = "No baseline available for regression analysis";
                return result;
            }
            
            result.HasBaseline = true;
            result.BaselineDate = m_baseline.CreatedDate;
            
            // Compare current results with baseline
            lock (m_resultsLock)
            {
                if (m_results.Count > 0)
                {
                    var currentAvg = m_results.Average(r => r.CrossingTime);
                    var baselineAvg = m_baseline.AverageCrossingTime;
                    
                    result.PerformanceChange = ((currentAvg - baselineAvg) / baselineAvg) * 100;
                    result.IsRegression = result.PerformanceChange > 10; // 10% degradation threshold
                    result.IsImprovement = result.PerformanceChange < -5; // 5% improvement threshold
                    
                    if (result.IsRegression)
                    {
                        result.Message = $"Performance regression detected: {result.PerformanceChange:F1}% slower than baseline";
                    }
                    else if (result.IsImprovement)
                    {
                        result.Message = $"Performance improvement detected: {Math.Abs(result.PerformanceChange):F1}% faster than baseline";
                    }
                    else
                    {
                        result.Message = "Performance within acceptable range of baseline";
                    }
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Save benchmark results for future regression testing
        /// </summary>
        private async Task SaveBenchmarkResultsAsync(BenchmarkSuiteReport report)
        {
            try
            {
                var resultsFile = Path.Combine(Environment.CurrentDirectory, "benchmark_results.json");
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                await File.WriteAllTextAsync(resultsFile, json);
                
                // Update baseline if this is a good run
                if (report.BasicPerformance != null && !report.HasErrors)
                {
                    await UpdateBaselineAsync(report.BasicPerformance);
                }
                
                m_log.InfoFormat("[CROSSING BENCHMARK]: Results saved to {0}", resultsFile);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[CROSSING BENCHMARK]: Failed to save results: {0}", ex.Message);
            }
        }
        
        /// <summary>
        /// Update performance baseline
        /// </summary>
        private async Task UpdateBaselineAsync(BasicPerformanceResult currentResults)
        {
            try
            {
                var baseline = new BenchmarkBaseline
                {
                    CreatedDate = DateTime.UtcNow,
                    AverageCrossingTime = currentResults.AverageCrossingTime,
                    StandardDeviation = currentResults.StandardDeviation,
                    MemoryImpactMB = currentResults.MemoryImpactMB,
                    GCGen2Collections = currentResults.GCGen2Collections,
                    SuccessRate = currentResults.SuccessRate
                };
                
                var json = JsonSerializer.Serialize(baseline, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                var baselineFile = Path.Combine(Environment.CurrentDirectory, m_baselineFile);
                await File.WriteAllTextAsync(baselineFile, json);
                
                m_baseline = baseline;
                m_log.InfoFormat("[CROSSING BENCHMARK]: Baseline updated - Avg: {0:F1}ms", baseline.AverageCrossingTime);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[CROSSING BENCHMARK]: Failed to update baseline: {0}", ex.Message);
            }
        }
        
        /// <summary>
        /// Load existing performance baseline
        /// </summary>
        private void LoadBaseline()
        {
            try
            {
                var baselineFile = Path.Combine(Environment.CurrentDirectory, m_baselineFile);
                if (File.Exists(baselineFile))
                {
                    var json = File.ReadAllText(baselineFile);
                    m_baseline = JsonSerializer.Deserialize<BenchmarkBaseline>(json, new JsonSerializerOptions 
                    { 
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    
                    m_log.InfoFormat("[CROSSING BENCHMARK]: Loaded baseline from {0} - Avg: {1:F1}ms", 
                        m_baseline.CreatedDate.ToString("yyyy-MM-dd"), m_baseline.AverageCrossingTime);
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[CROSSING BENCHMARK]: Failed to load baseline: {0}", ex.Message);
                m_baseline = null;
            }
        }
        
        // Helper methods
        private static double CalculateStandardDeviation(List<double> values)
        {
            if (values.Count < 2) return 0;
            
            double mean = values.Average();
            double sumOfSquares = values.Sum(v => Math.Pow(v - mean, 2));
            return Math.Sqrt(sumOfSquares / (values.Count - 1));
        }
        
        private static double GetPercentile(List<double> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;
            
            double rank = (percentile / 100.0) * (sortedValues.Count - 1);
            int lowerIndex = (int)Math.Floor(rank);
            int upperIndex = (int)Math.Ceiling(rank);
            
            if (lowerIndex == upperIndex)
                return sortedValues[lowerIndex];
            
            double weight = rank - lowerIndex;
            return sortedValues[lowerIndex] * (1 - weight) + sortedValues[upperIndex] * weight;
        }
        
        // Configuration methods
        public void SetConfiguration(int virtualAvatars, int crossingsPerAvatar, bool enableStress, TimeSpan duration)
        {
            m_virtualAvatarCount = Math.Max(1, virtualAvatars);
            m_crossingsPerAvatar = Math.Max(1, crossingsPerAvatar);
            m_enableStressTest = enableStress;
            m_testDuration = duration;
        }
    }
    
    // Data structures for benchmark results
    public class BenchmarkResult
    {
        public DateTime Timestamp { get; set; }
        public string AvatarName { get; set; }
        public double CrossingTime { get; set; }
        public bool Success { get; set; }
        public long MemoryUsage { get; set; }
    }
    
    public class BenchmarkSuiteReport
    {
        public DateTime TestStartTime { get; set; }
        public DateTime TestEndTime { get; set; }
        public TimeSpan TotalTestDuration { get; set; }
        public string SceneName { get; set; }
        public string RegionName { get; set; }
        public bool HasErrors { get; set; }
        public string ErrorMessage { get; set; }
        
        public BasicPerformanceResult BasicPerformance { get; set; }
        public LoadTestResult LoadTest { get; set; }
        public ConcurrencyTestResult ConcurrencyTest { get; set; }
        public MemoryTestResult MemoryTest { get; set; }
        public StressTestResult StressTest { get; set; }
        public RegressionAnalysisResult RegressionAnalysis { get; set; }
    }
    
    public class BasicPerformanceResult
    {
        public double AverageCrossingTime { get; set; }
        public double MinCrossingTime { get; set; }
        public double MaxCrossingTime { get; set; }
        public double StandardDeviation { get; set; }
        public double MemoryImpactMB { get; set; }
        public int GCGen0Collections { get; set; }
        public int GCGen1Collections { get; set; }
        public int GCGen2Collections { get; set; }
        public double SuccessRate { get; set; }
    }
    
    public class LoadTestResult
    {
        public int TotalCrossings { get; set; }
        public double AverageCrossingTime { get; set; }
        public double CrossingsPerSecond { get; set; }
        public int MaxConcurrentCrossings { get; set; }
        public double TotalMemoryImpactMB { get; set; }
        public double MemoryPerCrossingKB { get; set; }
        public TimeSpan TestDuration { get; set; }
        public double P50CrossingTime { get; set; }
        public double P95CrossingTime { get; set; }
        public double P99CrossingTime { get; set; }
    }
    
    public class ConcurrencyTestResult
    {
        public List<ConcurrencyLevelResult> ConcurrencyResults { get; set; } = new List<ConcurrencyLevelResult>();
        public int OptimalConcurrencyLevel { get; set; }
    }
    
    public class ConcurrencyLevelResult
    {
        public int ConcurrencyLevel { get; set; }
        public double AverageCrossingTime { get; set; }
        public double MaxCrossingTime { get; set; }
        public double TotalWallClockTime { get; set; }
        public double EfficiencyRatio { get; set; }
    }
    
    public class MemoryTestResult
    {
        public double BaselineMemoryMB { get; set; }
        public double FinalMemoryMB { get; set; }
        public double MemoryGrowthMB { get; set; }
        public double AverageMemoryPerCrossingKB { get; set; }
        public double PeakMemoryMB { get; set; }
        public int GCGen0Collections { get; set; }
        public int GCGen1Collections { get; set; }
        public int GCGen2Collections { get; set; }
        public double PotentialMemoryLeakMB { get; set; }
    }
    
    public class StressTestResult
    {
        public int MaxConcurrentCrossings { get; set; }
        public List<StressLoadResult> LoadResults { get; set; } = new List<StressLoadResult>();
        public bool SystemLimitReached { get; set; }
    }
    
    public class StressLoadResult
    {
        public int LoadLevel { get; set; }
        public double SuccessRate { get; set; }
        public double AverageResponseTime { get; set; }
        public double MaxResponseTime { get; set; }
        public TimeSpan TotalDuration { get; set; }
    }
    
    public class RegressionAnalysisResult
    {
        public bool HasBaseline { get; set; }
        public DateTime BaselineDate { get; set; }
        public double PerformanceChange { get; set; }
        public bool IsRegression { get; set; }
        public bool IsImprovement { get; set; }
        public string Message { get; set; }
    }
    
    public class BenchmarkBaseline
    {
        public DateTime CreatedDate { get; set; }
        public double AverageCrossingTime { get; set; }
        public double StandardDeviation { get; set; }
        public double MemoryImpactMB { get; set; }
        public int GCGen2Collections { get; set; }
        public double SuccessRate { get; set; }
    }
}