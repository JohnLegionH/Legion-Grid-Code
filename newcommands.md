# OpenSim Modernization - New Console Commands

This document describes the new console commands added as part of the OpenSim region crossing modernization project.

## Overview

The modernization project has added comprehensive performance monitoring, alerting, and optimization capabilities to OpenSim. These new commands provide administrators with powerful tools to monitor, analyze, and optimize region crossing performance.

## Performance Monitoring Commands

### `show crossing performance`

**Description**: Display real-time region crossing performance metrics dashboard

**Usage**: `show crossing performance`

**Output**: Comprehensive performance dashboard including:
- Region crossing metrics (attempts, successes, failures, success rate)
- Average crossing times and velocity preservation statistics
- HTTP performance metrics (requests, successes, failures, timeouts)
- Memory and GC performance data
- Connection pool performance statistics
- Real-time system performance indicators

**Example**:
```
==== Region Crossing Performance Dashboard ====

--- Region Crossing Metrics ---
Attempts: 15
Successes: 15
Failures: 0
Success Rate: 100.0%
Average Crossing Time: 32.5ms
Velocity Preserved Count: 15

--- HTTP Performance Metrics ---
HTTP Requests: 45
HTTP Successes: 45
HTTP Failures: 0
HTTP Timeouts: 0
Average HTTP Time: 18.2ms

--- Memory & GC Performance ---
Memory Allocations/Crossing: 1.02 MB
GC Collections Gen0: 0
GC Collections Gen1: 0
GC Collections Gen2: 0
Current Memory Pressure: 54.52 MB

--- Connection Pool Performance ---
HTTP Requests Served: 45
Pool Hit Rate: 96.7%
Region Crossing Pool: 24 max connections
Pool Health: EXCELLENT
```

### `show crossing stats`

**Description**: Display detailed crossing statistics and available performance categories

**Usage**: `show crossing stats`

**Output**: Information about available statistics categories and how to access detailed metrics

**Categories Available**:
- `entitytransfer.modernization` - Region crossing performance
- `http.modernization` - Async HTTP performance
- `gc.performance` - Garbage collection metrics

**Performance Alert Thresholds**: Also displays current alert threshold settings

### `crossing health check`

**Description**: Generate a comprehensive performance health assessment

**Usage**: `crossing health check`

**Output**: Detailed health analysis including:
- Performance health assessment (EXCELLENT/GOOD/WARNING/CRITICAL)
- Detailed metrics breakdown
- Automated recommendations based on performance data
- Health status for crossing time, success rate, memory, and GC pressure

**Health Levels**:
- **EXCELLENT**: Optimal performance
- **GOOD**: Acceptable performance
- **WARNING**: Performance degrading
- **CRITICAL**: Serious performance issues

**Example**:
```
==== Region Crossing Health Check ====

--- Performance Health Assessment ---
Crossing Time Health: EXCELLENT (Avg: 32.5ms)
Success Rate Health: EXCELLENT (100.0%)
Memory Health: EXCELLENT (Current: 54.5MB)
GC Pressure Health: EXCELLENT (Total GC during crossings: 0)

--- Recommendations ---
All systems operating optimally. No recommendations needed.
```

## Performance Alerting Commands

### `set crossing alert threshold <type> <value>`

**Description**: Configure performance alert thresholds for automated monitoring

**Usage**: `set crossing alert threshold <type> <value>`

**Parameters**:
- `type`: Alert type (time|success|memory|gc|http)
- `value`: Threshold value (numeric)

**Types and Default Values**:
- `time` - Crossing time threshold in milliseconds (default: 100ms)
- `success` - Success rate threshold in percentage (default: 95%)
- `memory` - Memory allocation threshold in MB per crossing (default: 10MB)
- `gc` - GC Gen2 collection threshold (default: 3 collections)
- `http` - HTTP timeout rate threshold in percentage (default: 10%)

**Examples**:
```
set crossing alert threshold time 150    # Alert if crossing time > 150ms
set crossing alert threshold success 90  # Alert if success rate < 90%
set crossing alert threshold memory 15   # Alert if memory > 15MB per crossing
set crossing alert threshold gc 5        # Alert if Gen2 GC > 5 collections
set crossing alert threshold http 20     # Alert if HTTP timeout rate > 20%
```

**Alert Features**:
- 5-minute cooldown between similar alerts to prevent spam
- Automatic logging with detailed diagnostic information
- Actionable troubleshooting suggestions
- Integration with existing OpenSim logging system

## Benchmarking Suite Commands

### `run crossing benchmark [type] [avatars] [crossings]`

**Description**: Execute comprehensive region crossing performance benchmarks

**Usage**: `run crossing benchmark [quick|full|load|stress] [avatars] [crossings]`

**Parameters**:
- `type`: Benchmark type (quick, full, load, stress - default: quick)
- `avatars`: Number of virtual avatars (1-100, default: 10)
- `crossings`: Crossings per avatar (1-50, default: 5)

**Benchmark Types**:
- **quick**: Basic performance test (~2 minutes)
- **full**: Complete benchmark suite (~10 minutes)
- **load**: Load testing with multiple virtual avatars
- **stress**: Stress testing to find system limits

**Output**: Comprehensive performance analysis including:
- Basic performance metrics (crossing times, success rates)
- Load test results (throughput, percentiles)
- Concurrency analysis (optimal concurrency levels)
- Memory impact testing (leak detection, GC pressure)
- Regression analysis (comparison with baseline)

**Examples**:
```
run crossing benchmark                      # Quick benchmark with defaults
run crossing benchmark full                 # Complete suite
run crossing benchmark load 20 10           # Load test: 20 avatars, 10 crossings each
run crossing benchmark stress               # Stress test to find limits
```

**Features**:
- Automated virtual avatar simulation
- Performance regression detection
- Baseline establishment and comparison
- CI/CD integration support
- JSON results export for analysis

### `show benchmark results [display_type]`

**Description**: Display benchmark test results and performance analysis

**Usage**: `show benchmark results [latest|all|summary]`

**Display Options**:
- `latest`: Most recent benchmark results (default)
- `all`: Complete historical benchmark data
- `summary`: Condensed performance overview

**Output**: Formatted benchmark analysis including:
- Test configuration and execution details
- Performance metrics with trend analysis
- Regression detection and recommendations
- Memory usage and GC impact analysis
- Historical comparison data

**Example**:
```
==== Benchmark Results ====
Test Date: 2024-12-18 15:30:22 UTC
Region: TestRegion (TestScene)
Duration: 2.1 minutes

--- BASIC PERFORMANCE ---
Average Crossing Time: 35.2ms
Min/Max Times: 22.1ms / 58.7ms
Standard Deviation: 8.3ms
Success Rate: 100.0%
Memory Impact: 2.10 MB

--- REGRESSION ANALYSIS ---
Performance within acceptable range (2.3% change)
Analysis: Performance stable compared to baseline
```

### `benchmark config [avatars] [crossings] [stress] [duration]`

**Description**: Configure benchmark test parameters

**Usage**: `benchmark config [avatars] [crossings] [stress] [duration]`

**Parameters**:
- `avatars`: Number of virtual avatars (1-100, default: 10)
- `crossings`: Crossings per avatar (1-50, default: 5)
- `stress`: Enable stress testing (true/false, default: false)
- `duration`: Test duration in minutes (1-30, default: 5)

**Examples**:
```
benchmark config 25 10                     # 25 avatars, 10 crossings each
benchmark config 10 5 true 10              # Enable stress test, 10 min duration
benchmark config 5 3 false 3               # Light testing for development
```

**Configuration Persistence**:
- Settings apply to current session only
- Used for all subsequent benchmark runs
- Can be overridden by command parameters

## Connection Pool Optimization Commands

### `show connection pool`

**Description**: Display connection pool statistics and optimization details

**Usage**: `show connection pool`

**Output**: Comprehensive connection pool analysis including:
- Total HTTP requests served and pool hit rates
- Region crossing pool configuration (max connections, timeouts, lifetime)
- General pool configuration
- Performance optimizations enabled
- Pool efficiency assessment and recommendations

**Example**:
```
==== Connection Pool Statistics ====

--- Optimized Connection Pool Status ---
Total HTTP Requests Served: 150
Connection Pool Hit Rate: 95.3%

--- Region Crossing Pool Configuration ---
Max Connections per Server: 24
Connection Idle Timeout: 3.0 minutes
Connection Lifetime: 15.0 minutes

--- Performance Optimizations ---
• HTTP/2 multiplexing enabled for reduced latency
• Automatic compression (GZip, Deflate, Brotli)
• Connection keep-alive for reduced overhead
• CPU-scaled connection limits for optimal throughput
• Optimized timeouts for region crossing operations

Pool Efficiency Assessment: EXCELLENT
```

### `optimize connection pool`

**Description**: Force cleanup of idle connections and optimize pool settings

**Usage**: `optimize connection pool`

**Actions Performed**:
- Cleanup idle connections in both region crossing and general pools
- Force garbage collection to clean up orphaned connections
- Reset pool statistics for fresh monitoring
- Log optimization results

**Output**: Before/after statistics showing optimization results

**Example**:
```
==== Connection Pool Optimization ====

Before optimization - Pool hit rate: 94.2%
✓ Cleaned up idle connections
✓ Forced garbage collection of orphaned connections
✓ Connection pool optimization completed
After optimization - Pool hit rate: 96.1%

Connection pool has been optimized for better performance.
Monitor performance with 'show connection pool' command.
```

## Integration with Existing Commands

### Enhanced `stats show` Commands

The modernization project integrates with OpenSim's existing statistics system:

**New Statistics Categories**:
- `stats show entitytransfer.modernization` - Region crossing performance stats
- `stats show http.modernization` - Async HTTP performance stats  
- `stats show gc.performance` - Memory and GC performance stats
- `stats show all` - All statistics including new performance metrics

### Existing Command Enhancements

**`show crossing performance`** enhances the existing performance monitoring by adding:
- Connection pool performance section
- Advanced memory and GC tracking
- Real-time system performance indicators
- Health assessment indicators

## Performance Alert System

### Automated Monitoring

The system continuously monitors performance and automatically generates alerts when thresholds are exceeded:

**Alert Types**:
1. **Crossing Time Degradation**: When crossing times exceed configured threshold
2. **Success Rate Degradation**: When success rates drop below configured threshold
3. **High Memory Allocation**: When memory usage per crossing exceeds threshold
4. **GC Pressure**: When Generation 2 garbage collections exceed threshold
5. **HTTP Timeout Rate**: When HTTP timeout rates exceed threshold

**Alert Example**:
```
[ENTITY TRANSFER MODULE ALERT]: Crossing time degradation detected! 
Time: 125.3ms (threshold: 100ms). Check for network issues or server load.
```

### Alert Management Features

- **Cooldown Protection**: 5-minute cooldown between similar alerts
- **Intelligent Thresholds**: Only alert on meaningful sample sizes
- **Actionable Information**: Each alert includes troubleshooting suggestions
- **Log Integration**: All alerts are logged with detailed diagnostic data

## Technical Implementation Notes

### Performance Metrics Collection

- **Real-time Tracking**: Metrics collected during each region crossing
- **Rolling Window Averages**: Last 100 samples for trend analysis
- **Memory Efficient**: Minimal overhead for production environments
- **Thread Safe**: Concurrent access protection for multi-threaded operations

### Connection Pool Optimization

- **CPU-Aware Scaling**: Connection limits automatically scale with CPU cores
- **HTTP/2 Support**: Modern protocol support for improved performance
- **Operation-Specific Pools**: Different pools optimized for different operations
- **Smart Resource Management**: Automatic cleanup and lifecycle management

### Monitoring Integration

- **StatsManager Integration**: Works with existing OpenSim statistics system
- **Console Integration**: Easy access via OpenSim console commands
- **Health Assessment**: Automated evaluation of system performance
- **Extensible Design**: Easy to add new metrics and alert types

## Troubleshooting Common Issues

### Performance Degradation

1. **Use `crossing health check`** to identify problem areas
2. **Check `show crossing performance`** for detailed metrics
3. **Review alert thresholds** with `show crossing stats`
4. **Optimize connections** with `optimize connection pool`

### Alert Configuration

1. **Review current thresholds** with `show crossing stats`
2. **Adjust thresholds** using `set crossing alert threshold`
3. **Monitor effectiveness** with performance commands
4. **Fine-tune based on environment** specific requirements

### Connection Pool Issues

1. **Check pool statistics** with `show connection pool`
2. **Look for low hit rates** or efficiency warnings
3. **Use `optimize connection pool`** to cleanup
4. **Monitor improvement** with subsequent checks

## Best Practices

### Regular Monitoring

- **Daily**: Check `show crossing performance` for overall health
- **Weekly**: Run `crossing health check` for detailed analysis
- **Monthly**: Review and adjust alert thresholds as needed
- **As Needed**: Use `optimize connection pool` during maintenance windows

### Alert Management

- **Set Realistic Thresholds**: Based on your environment's normal performance
- **Monitor Alert Frequency**: Adjust thresholds if getting too many/few alerts
- **Act on Alerts**: Use provided troubleshooting suggestions
- **Document Changes**: Keep track of threshold adjustments and reasons

### Benchmarking Best Practices

- **Regular Testing**: Run benchmarks during development and before releases
- **Baseline Management**: Establish performance baselines for different environments
- **Regression Monitoring**: Use automated benchmarking in CI/CD pipelines
- **Environment Consistency**: Run benchmarks in consistent environments
- **Load Testing**: Test with realistic user loads and usage patterns

### Performance Optimization

- **Baseline Performance**: Establish normal performance metrics for your environment
- **Regular Optimization**: Use connection pool optimization during low-usage periods
- **Monitor Trends**: Watch for gradual performance degradation over time
- **Proactive Maintenance**: Address issues before they become critical

## CI/CD Integration

### Automated Benchmarking

The benchmarking suite includes comprehensive CI/CD integration capabilities:

#### PowerShell Script (`scripts/benchmark_ci.ps1`)
- **Automated Execution**: Runs benchmarks in CI/CD environments
- **Result Analysis**: Analyzes performance and detects regressions
- **Threshold Management**: Configurable regression thresholds
- **Artifact Generation**: Creates reports and summaries
- **Exit Codes**: Returns appropriate codes for pipeline control

**Usage**:
```powershell
# Basic benchmark execution
./scripts/benchmark_ci.ps1 -TestType "quick"

# Full benchmark with custom parameters
./scripts/benchmark_ci.ps1 -TestType "full" -Avatars 20 -Crossings 10 -RegressionThreshold 15.0

# Stress testing with failure on regression
./scripts/benchmark_ci.ps1 -TestType "stress" -FailOnRegression $true
```

#### GitHub Actions Workflow (`.github/workflows/benchmark.yml`)
- **Automated Triggers**: Runs on code changes, PRs, and scheduled intervals
- **Multi-Environment**: Supports different benchmark configurations
- **Regression Detection**: Automatically detects performance regressions
- **PR Comments**: Posts benchmark results directly to pull requests
- **Artifact Storage**: Preserves benchmark data for historical analysis

**Features**:
- Configurable benchmark types via workflow dispatch
- Baseline management with automatic updates
- Performance trend analysis
- Integration with GitHub status checks
- Artifact retention for long-term analysis

#### Integration Examples

**Jenkins Pipeline**:
```groovy
pipeline {
    agent any
    stages {
        stage('Benchmark') {
            steps {
                script {
                    powershell './scripts/benchmark_ci.ps1 -TestType "load" -Avatars 15 -Crossings 8'
                }
                archiveArtifacts artifacts: 'benchmark-results/*', allowEmptyArchive: true
                publishTestResults testResultsPattern: 'benchmark-results/benchmark_summary.xml'
            }
        }
    }
}
```

**Azure DevOps**:
```yaml
- task: PowerShell@2
  displayName: 'Run Performance Benchmarks'
  inputs:
    filePath: 'scripts/benchmark_ci.ps1'
    arguments: '-TestType "quick" -RegressionThreshold 10.0'
    
- task: PublishTestResults@2
  displayName: 'Publish Benchmark Results'
  inputs:
    testResultsFiles: 'benchmark-results/*.xml'
    testRunTitle: 'OpenSim Performance Benchmarks'
```

---

## Version Information

These commands were added as part of the OpenSim Region Crossing Modernization project, implementing:
- Async/await patterns for improved performance
- Modern HTTP client infrastructure with connection pooling
- Comprehensive performance monitoring and alerting
- Advanced optimization capabilities

For technical details about the implementation, see the source code in:
- `OpenSim.Region.CoreModules.Framework.EntityTransfer.EntityTransferModule`
- `OpenSim.Framework.Http.OptimizedHttpService`
- `OpenSim.Framework.Http.AsyncHttpService`