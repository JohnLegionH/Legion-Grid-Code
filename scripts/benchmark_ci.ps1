# OpenSim Region Crossing Benchmark CI/CD Integration Script
# Automated performance testing for continuous integration pipelines

param(
    [string]$ConfigFile = "opensim.ini",
    [string]$TestType = "quick",
    [int]$Avatars = 10,
    [int]$Crossings = 5,
    [string]$OutputDir = "benchmark-results",
    [int]$TimeoutMinutes = 15,
    [double]$RegressionThreshold = 10.0,
    [bool]$FailOnRegression = $true,
    [string]$BaselineFile = "baseline_performance.json"
)

Write-Host "==== OpenSim Region Crossing Benchmark CI/CD Script ====" -ForegroundColor Green
Write-Host "Test Configuration:" -ForegroundColor Cyan
Write-Host "  Type: $TestType" -ForegroundColor White
Write-Host "  Virtual Avatars: $Avatars" -ForegroundColor White
Write-Host "  Crossings per Avatar: $Crossings" -ForegroundColor White
Write-Host "  Timeout: $TimeoutMinutes minutes" -ForegroundColor White
Write-Host "  Regression Threshold: $RegressionThreshold%" -ForegroundColor White
Write-Host ""

# Create output directory
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Write-Host "Created output directory: $OutputDir" -ForegroundColor Yellow
}

# Function to check if OpenSim is responsive
function Test-OpenSimConnection {
    param([int]$Port = 9000)
    
    try {
        $tcpClient = New-Object System.Net.Sockets.TcpClient
        $asyncResult = $tcpClient.BeginConnect("localhost", $Port, $null, $null)
        $success = $asyncResult.AsyncWaitHandle.WaitOne(3000)
        $tcpClient.Close()
        return $success
    }
    catch {
        return $false
    }
}

# Function to wait for OpenSim to be ready
function Wait-ForOpenSim {
    param([int]$TimeoutMinutes = 5)
    
    Write-Host "Waiting for OpenSim to be ready..." -ForegroundColor Yellow
    $timeout = (Get-Date).AddMinutes($TimeoutMinutes)
    
    while ((Get-Date) -lt $timeout) {
        if (Test-OpenSimConnection) {
            Write-Host "OpenSim is ready!" -ForegroundColor Green
            Start-Sleep -Seconds 5  # Additional time for full initialization
            return $true
        }
        Start-Sleep -Seconds 2
    }
    
    Write-Host "Timeout waiting for OpenSim to be ready" -ForegroundColor Red
    return $false
}

# Function to run benchmark via console commands
function Invoke-BenchmarkTest {
    param(
        [string]$Type,
        [int]$AvatarCount,
        [int]$CrossingCount,
        [int]$TimeoutMin
    )
    
    try {
        # Prepare console commands
        $commands = @(
            "benchmark config $AvatarCount $CrossingCount",
            "run crossing benchmark $Type $AvatarCount $CrossingCount"
        )
        
        Write-Host "Executing benchmark commands..." -ForegroundColor Yellow
        
        # Execute commands via console (this would need to be adapted based on your setup)
        # For now, we'll create a command file that can be processed
        $commandFile = Join-Path $OutputDir "benchmark_commands.txt"
        $commands | Out-File -FilePath $commandFile -Encoding UTF8
        
        Write-Host "Benchmark commands saved to: $commandFile" -ForegroundColor Cyan
        Write-Host "Commands to execute:" -ForegroundColor White
        foreach ($cmd in $commands) {
            Write-Host "  $cmd" -ForegroundColor Gray
        }
        
        # Wait for benchmark completion (polling for results file)
        $resultsFile = "benchmark_results.json"
        $pollTimeout = (Get-Date).AddMinutes($TimeoutMin)
        $initialFileTime = $null
        
        if (Test-Path $resultsFile) {
            $initialFileTime = (Get-Item $resultsFile).LastWriteTime
        }
        
        Write-Host "Waiting for benchmark completion (timeout: $TimeoutMin minutes)..." -ForegroundColor Yellow
        
        while ((Get-Date) -lt $pollTimeout) {
            if (Test-Path $resultsFile) {
                $currentFileTime = (Get-Item $resultsFile).LastWriteTime
                if ($initialFileTime -eq $null -or $currentFileTime -gt $initialFileTime) {
                    # File has been updated, wait a bit more to ensure completion
                    Start-Sleep -Seconds 30
                    return $true
                }
            }
            Start-Sleep -Seconds 10
        }
        
        Write-Host "Benchmark timeout reached" -ForegroundColor Red
        return $false
    }
    catch {
        Write-Host "Error executing benchmark: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Function to analyze benchmark results
function Analyze-BenchmarkResults {
    param(
        [string]$ResultsFile,
        [string]$BaselineFile,
        [double]$Threshold
    )
    
    if (-not (Test-Path $ResultsFile)) {
        Write-Host "Results file not found: $ResultsFile" -ForegroundColor Red
        return @{ Success = $false; Message = "Results file not found" }
    }
    
    try {
        $results = Get-Content $ResultsFile | ConvertFrom-Json
        
        Write-Host ""
        Write-Host "==== Benchmark Results Analysis ====" -ForegroundColor Green
        
        # Basic performance metrics
        if ($results.basicPerformance) {
            $bp = $results.basicPerformance
            Write-Host "Basic Performance Results:" -ForegroundColor Cyan
            Write-Host "  Average Crossing Time: $($bp.averageCrossingTime.ToString('F1'))ms" -ForegroundColor White
            Write-Host "  Success Rate: $($bp.successRate.ToString('F1'))%" -ForegroundColor White
            Write-Host "  Memory Impact: $($bp.memoryImpactMB.ToString('F2')) MB" -ForegroundColor White
            Write-Host "  Standard Deviation: $($bp.standardDeviation.ToString('F1'))ms" -ForegroundColor White
        }
        
        # Load test results
        if ($results.loadTest) {
            $lt = $results.loadTest
            Write-Host "Load Test Results:" -ForegroundColor Cyan
            Write-Host "  Total Crossings: $($lt.totalCrossings)" -ForegroundColor White
            Write-Host "  Crossings/Second: $($lt.crossingsPerSecond.ToString('F1'))" -ForegroundColor White
            Write-Host "  P95 Response Time: $($lt.p95CrossingTime.ToString('F1'))ms" -ForegroundColor White
        }
        
        # Regression analysis
        $hasRegression = $false
        $regressionMessage = "No regression analysis available"
        
        if ($results.regressionAnalysis) {
            $ra = $results.regressionAnalysis
            Write-Host "Regression Analysis:" -ForegroundColor Cyan
            
            if ($ra.hasBaseline) {
                $change = $ra.performanceChange
                Write-Host "  Performance Change: $($change.ToString('F1'))%" -ForegroundColor White
                Write-Host "  Baseline Date: $($ra.baselineDate)" -ForegroundColor White
                Write-Host "  Message: $($ra.message)" -ForegroundColor White
                
                if ($ra.isRegression) {
                    Write-Host "  ⚠️  REGRESSION DETECTED!" -ForegroundColor Red
                    $hasRegression = $true
                    $regressionMessage = "Performance regression: $($change.ToString('F1'))% slower"
                }
                elseif ($ra.isImprovement) {
                    Write-Host "  ✅ PERFORMANCE IMPROVEMENT!" -ForegroundColor Green
                    $regressionMessage = "Performance improvement: $([Math]::Abs($change).ToString('F1'))% faster"
                }
                else {
                    Write-Host "  ✅ Performance within acceptable range" -ForegroundColor Green
                    $regressionMessage = "Performance stable: $($change.ToString('F1'))% change"
                }
            }
            else {
                Write-Host "  No baseline available - establishing new baseline" -ForegroundColor Yellow
                $regressionMessage = "Baseline established for future comparisons"
            }
        }
        
        # Memory analysis
        if ($results.memoryTest) {
            $mt = $results.memoryTest
            Write-Host "Memory Analysis:" -ForegroundColor Cyan
            Write-Host "  Memory Growth: $($mt.memoryGrowthMB.ToString('F2')) MB" -ForegroundColor White
            Write-Host "  Average per Crossing: $($mt.averageMemoryPerCrossingKB.ToString('F1')) KB" -ForegroundColor White
            
            if ($mt.potentialMemoryLeakMB -gt 1.0) {
                Write-Host "  ⚠️  Potential Memory Leak: $($mt.potentialMemoryLeakMB.ToString('F2')) MB" -ForegroundColor Red
            }
            else {
                Write-Host "  ✅ No significant memory leaks detected" -ForegroundColor Green
            }
        }
        
        Write-Host ""
        
        return @{
            Success = $true
            HasRegression = $hasRegression
            Message = $regressionMessage
            Results = $results
        }
    }
    catch {
        Write-Host "Error analyzing results: $($_.Exception.Message)" -ForegroundColor Red
        return @{ Success = $false; Message = "Error analyzing results: $($_.Exception.Message)" }
    }
}

# Function to copy results to output directory
function Copy-BenchmarkArtifacts {
    param([string]$OutputDirectory)
    
    $artifacts = @(
        "benchmark_results.json",
        "baseline_performance.json"
    )
    
    foreach ($artifact in $artifacts) {
        if (Test-Path $artifact) {
            $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
            $destination = Join-Path $OutputDirectory "$($timestamp)_$artifact"
            Copy-Item $artifact $destination -Force
            Write-Host "Copied $artifact to $destination" -ForegroundColor Green
        }
    }
}

# Main execution
try {
    Write-Host "Starting OpenSim benchmark CI/CD process..." -ForegroundColor Green
    
    # Check if OpenSim is running
    if (-not (Wait-ForOpenSim -TimeoutMinutes 5)) {
        Write-Host "OpenSim is not responsive. Ensure OpenSim is running before executing benchmarks." -ForegroundColor Red
        exit 1
    }
    
    # Execute benchmark
    Write-Host "Executing $TestType benchmark..." -ForegroundColor Cyan
    $benchmarkSuccess = Invoke-BenchmarkTest -Type $TestType -AvatarCount $Avatars -CrossingCount $Crossings -TimeoutMin $TimeoutMinutes
    
    if (-not $benchmarkSuccess) {
        Write-Host "Benchmark execution failed or timed out" -ForegroundColor Red
        exit 1
    }
    
    # Analyze results
    Write-Host "Analyzing benchmark results..." -ForegroundColor Cyan
    $analysis = Analyze-BenchmarkResults -ResultsFile "benchmark_results.json" -BaselineFile $BaselineFile -Threshold $RegressionThreshold
    
    if (-not $analysis.Success) {
        Write-Host "Failed to analyze benchmark results: $($analysis.Message)" -ForegroundColor Red
        exit 1
    }
    
    # Copy artifacts
    Copy-BenchmarkArtifacts -OutputDirectory $OutputDir
    
    # Generate CI/CD summary
    $summary = @"
## OpenSim Region Crossing Benchmark Results

**Test Configuration:**
- Type: $TestType
- Virtual Avatars: $Avatars
- Crossings per Avatar: $Crossings
- Date: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss") UTC

**Analysis:**
$($analysis.Message)

**Status:** $($analysis.HasRegression ? "⚠️ REGRESSION DETECTED" : "✅ PASSED")

"@
    
    $summaryFile = Join-Path $OutputDir "benchmark_summary.md"
    $summary | Out-File -FilePath $summaryFile -Encoding UTF8
    Write-Host "Summary saved to: $summaryFile" -ForegroundColor Green
    
    # Set exit code based on regression detection
    if ($analysis.HasRegression -and $FailOnRegression) {
        Write-Host ""
        Write-Host "❌ BENCHMARK FAILED: Performance regression detected" -ForegroundColor Red
        Write-Host "Regression threshold: $RegressionThreshold%" -ForegroundColor Red
        Write-Host "Set FailOnRegression to false to treat regressions as warnings" -ForegroundColor Yellow
        exit 1
    }
    else {
        Write-Host ""
        Write-Host "✅ BENCHMARK PASSED: Performance within acceptable limits" -ForegroundColor Green
        exit 0
    }
}
catch {
    Write-Host ""
    Write-Host "❌ BENCHMARK CI/CD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    exit 1
}