# OpenSim Error Handling System - Configuration Reference

## Overview

This comprehensive configuration reference provides detailed documentation for all configuration options in the OpenSim Error Handling System. Each parameter is explained with its purpose, default values, valid ranges, and impact on system behavior.

## Configuration File Structure

The error handling system uses six configuration files located in `bin/config-include/`:

- `ErrorHandling.ini` - Core error handling module
- `PhysicsStability.ini` - Physics engine monitoring
- `ScriptEngineStability.ini` - Script performance management
- `DatabaseStability.ini` - Database connection resilience
- `AssetStability.ini` - Asset loading recovery
- `ErrorDiagnostics.ini` - Monitoring and reporting

## ErrorHandling.ini - Core Configuration

**Location**: `bin/config-include/ErrorHandling.ini`

```ini
[ErrorHandling]
    ; Master enable/disable switch for the entire error handling system
    ; Type: Boolean
    ; Default: true
    ; Impact: Disabling removes all error handling protection
    Enabled = true
    
    ; Number of errors in time window before emergency procedures activate
    ; Type: Integer
    ; Default: 50
    ; Range: 10-200
    ; Impact: Lower = more sensitive detection, Higher = fewer false alarms
    ErrorThreshold = 50
    
    ; Time window for counting errors towards threshold (seconds)
    ; Type: Integer
    ; Default: 300 (5 minutes)
    ; Range: 60-1800 (1-30 minutes)
    ; Impact: Shorter = rapid response, Longer = trend analysis
    ErrorWindowSeconds = 300
    
    ; Enable automatic error recovery attempts
    ; Type: Boolean
    ; Default: true
    ; Impact: When false, only manual recovery commands work
    AutoRecovery = true
    
    ; Maximum automatic recovery attempts before requiring manual intervention
    ; Type: Integer
    ; Default: 3
    ; Range: 1-10
    ; Impact: Higher = more persistent recovery, Lower = faster manual escalation
    MaxRecoveryAttempts = 3
    
    ; Logging verbosity level for error handling messages
    ; Type: String
    ; Default: Info
    ; Options: Debug, Info, Warn, Error
    ; Impact: Debug = verbose logs, Error = minimal logs
    LogLevel = Info
    
    ; Timeout for emergency recovery procedures (seconds)
    ; Type: Integer
    ; Default: 30
    ; Range: 10-120
    ; Impact: Longer timeouts allow more complete recovery but delay response
    EmergencyTimeout = 30
    
    ; Interval between health checks (seconds)
    ; Type: Integer
    ; Default: 60
    ; Range: 15-300
    ; Impact: Shorter = more responsive monitoring, Longer = less overhead
    HealthCheckInterval = 60
    
    ; Enable detailed error logging with stack traces
    ; Type: Boolean
    ; Default: true
    ; Impact: Provides more debugging information but increases log size
    DetailedLogging = true
    
    ; Threshold for triggering emergency mode across all modules
    ; Type: Integer
    ; Default: 20
    ; Range: 5-50
    ; Impact: Lower = more aggressive emergency response
    GlobalEmergencyThreshold = 20
    
    ; Enable cross-module error correlation
    ; Type: Boolean
    ; Default: true
    ; Impact: Helps identify cascading failures but adds processing overhead
    EnableErrorCorrelation = true
```

### ErrorHandling.ini Parameter Details

#### Enabled
**Purpose**: Master control for the entire error handling system
**Default**: `true`
**When to Change**: 
- Set to `false` for debugging without error handling interference
- Disable temporarily if error handling is causing performance issues

#### ErrorThreshold
**Purpose**: Controls sensitivity of error detection
**Tuning Guidelines**:
- **Development**: 100-200 (less sensitive for testing)
- **Staging**: 50-75 (balanced for pre-production)
- **Production**: 25-50 (sensitive for live users)
- **High-Traffic**: 75-100 (account for normal error rates)

#### ErrorWindowSeconds
**Purpose**: Time period for error accumulation
**Tuning Guidelines**:
- **Rapid Response**: 60-180 seconds
- **Balanced**: 300-600 seconds (default range)
- **Trend Analysis**: 900-1800 seconds

#### MaxRecoveryAttempts
**Purpose**: Limits automatic recovery loops
**Recommendations**:
- **Aggressive Recovery**: 5-7 attempts
- **Conservative**: 2-3 attempts
- **Debug Mode**: 1 attempt (fail fast for analysis)

## PhysicsStability.ini - Physics Configuration

**Location**: `bin/config-include/PhysicsStability.ini`

```ini
[PhysicsStability]
    ; Enable physics stability monitoring and correction
    ; Type: Boolean
    ; Default: true
    Enabled = true
    
    ; Maximum linear velocity before automatic clamping (meters/second)
    ; Type: Float
    ; Default: 100.0
    ; Range: 10.0-500.0
    ; Impact: Lower = more restrictive physics, Higher = more freedom
    MaxVelocity = 100.0
    
    ; Maximum angular velocity before clamping (radians/second)
    ; Type: Float
    ; Default: 50.0
    ; Range: 5.0-200.0
    ; Impact: Controls rotational speed limits
    MaxAngularVelocity = 50.0
    
    ; Maximum X coordinate before object repositioning
    ; Type: Float
    ; Default: 1000.0
    ; Range: Region size to 10000.0
    ; Impact: Must account for region size and neighboring regions
    MaxPositionX = 1000.0
    
    ; Maximum Y coordinate before object repositioning
    ; Type: Float
    ; Default: 1000.0
    ; Range: Region size to 10000.0
    MaxPositionY = 1000.0
    
    ; Maximum Z coordinate (height) before object repositioning
    ; Type: Float
    ; Default: 4000.0
    ; Range: 1000.0-10000.0
    ; Impact: Should accommodate reasonable building heights
    MaxPositionZ = 4000.0
    
    ; Minimum Z coordinate before object repositioning
    ; Type: Float
    ; Default: -100.0
    ; Range: -1000.0 to 0.0
    ; Impact: Allows for underground areas and water features
    MinPositionZ = -100.0
    
    ; Physics frame time threshold for performance warnings (milliseconds)
    ; Type: Float
    ; Default: 20.0
    ; Range: 10.0-100.0
    ; Impact: Lower = more sensitive performance detection
    PhysicsFrameTimeThreshold = 20.0
    
    ; Time before considering an object "stuck" in collision (seconds)
    ; Type: Integer
    ; Default: 30
    ; Range: 10-300
    ; Impact: Shorter = faster stuck object detection
    StuckObjectTimeout = 30
    
    ; Interval between collision cleanup operations (seconds)
    ; Type: Integer
    ; Default: 60
    ; Range: 30-300
    ; Impact: More frequent = better cleanup, more overhead
    CollisionCleanupInterval = 60
    
    ; Enable velocity dampening for unstable objects
    ; Type: Boolean
    ; Default: true
    ; Impact: Reduces physics instability but may affect intentional high speeds
    VelocityDampening = true
    
    ; Factor applied to dampen excessive velocities (0.0-1.0)
    ; Type: Float
    ; Default: 0.95
    ; Range: 0.5-0.99
    ; Impact: Lower = more aggressive dampening
    DampeningFactor = 0.95
    
    ; Enable automatic physics engine restart on critical errors
    ; Type: Boolean
    ; Default: false
    ; Impact: Can resolve severe physics issues but causes temporary disruption
    EnablePhysicsRestart = false
    
    ; Number of physics errors before automatic restart
    ; Type: Integer
    ; Default: 50
    ; Range: 10-200
    PhysicsRestartThreshold = 50
```

### PhysicsStability.ini Tuning by Environment

#### High-Performance Racing/Combat Regions
```ini
MaxVelocity = 200.0
MaxAngularVelocity = 100.0
PhysicsFrameTimeThreshold = 15.0
VelocityDampening = false
```

#### Casual Building/Social Regions
```ini
MaxVelocity = 50.0
MaxAngularVelocity = 25.0
PhysicsFrameTimeThreshold = 30.0
VelocityDampening = true
DampeningFactor = 0.90
```

#### Educational/Stable Environments
```ini
MaxVelocity = 75.0
MaxAngularVelocity = 40.0
StuckObjectTimeout = 60
EnablePhysicsRestart = true
PhysicsRestartThreshold = 25
```

## ScriptEngineStability.ini - Script Configuration

**Location**: `bin/config-include/ScriptEngineStability.ini`

```ini
[ScriptEngineStability]
    ; Enable script engine monitoring and management
    ; Type: Boolean
    ; Default: true
    Enabled = true
    
    ; Number of script errors before automatic suspension
    ; Type: Integer
    ; Default: 10
    ; Range: 3-50
    ; Impact: Lower = stricter script management
    ScriptErrorThreshold = 10
    
    ; Time window for counting script errors (seconds)
    ; Type: Integer
    ; Default: 60
    ; Range: 30-300
    ; Impact: Shorter windows are more sensitive to error bursts
    ScriptErrorWindow = 60
    
    ; Maximum number of scripts allowed per object
    ; Type: Integer
    ; Default: 20
    ; Range: 5-100
    ; Impact: Prevents resource abuse but may limit complex objects
    MaxScriptsPerObject = 20
    
    ; Interval between script performance checks (seconds)
    ; Type: Integer
    ; Default: 30
    ; Range: 15-120
    ; Impact: More frequent = better monitoring, more overhead
    PerformanceCheckInterval = 30
    
    ; Script execution time threshold for warnings (milliseconds)
    ; Type: Float
    ; Default: 100.0
    ; Range: 50.0-1000.0
    ; Impact: Lower = stricter performance requirements
    ExecutionTimeThreshold = 100.0
    
    ; Memory usage threshold per script (megabytes)
    ; Type: Float
    ; Default: 10.0
    ; Range: 5.0-100.0
    ; Impact: Controls memory consumption per script
    MemoryThreshold = 10.0
    
    ; Enable automatic suspension of problematic scripts
    ; Type: Boolean
    ; Default: true
    ; Impact: Prevents runaway scripts but may affect user experience
    AutoSuspend = true
    
    ; Number of script engine errors before automatic restart
    ; Type: Integer
    ; Default: 100
    ; Range: 25-500
    ; Impact: Lower = more aggressive engine restarts
    EngineRestartThreshold = 100
    
    ; Enable script compile time monitoring
    ; Type: Boolean
    ; Default: true
    ; Impact: Helps identify slow-compiling scripts
    MonitorCompileTime = true
    
    ; Compile time threshold for warnings (milliseconds)
    ; Type: Float
    ; Default: 5000.0
    ; Range: 1000.0-30000.0
    CompileTimeThreshold = 5000.0
```

### ScriptEngineStability.ini Environment Presets

#### Development/Testing Environment
```ini
ScriptErrorThreshold = 25
AutoSuspend = false
ExecutionTimeThreshold = 200.0
MemoryThreshold = 25.0
```

#### Production Grid
```ini
ScriptErrorThreshold = 5
AutoSuspend = true
ExecutionTimeThreshold = 75.0
MemoryThreshold = 8.0
MaxScriptsPerObject = 15
```

#### Educational Environment
```ini
ScriptErrorThreshold = 15
ExecutionTimeThreshold = 150.0
MonitorCompileTime = true
CompileTimeThreshold = 10000.0
```

## DatabaseStability.ini - Database Configuration

**Location**: `bin/config-include/DatabaseStability.ini`

```ini
[DatabaseStability]
    ; Enable database connection monitoring and recovery
    ; Type: Boolean
    ; Default: true
    Enabled = true
    
    ; Database connection timeout (seconds)
    ; Type: Integer
    ; Default: 30
    ; Range: 10-120
    ; Impact: Shorter timeouts fail faster, longer timeouts more patient
    ConnectionTimeout = 30
    
    ; Individual query timeout (seconds)
    ; Type: Integer
    ; Default: 60
    ; Range: 15-300
    ; Impact: Prevents hanging on slow queries
    QueryTimeout = 60
    
    ; Maximum reconnection attempts before manual intervention
    ; Type: Integer
    ; Default: 5
    ; Range: 3-15
    ; Impact: More attempts = more persistent recovery
    MaxRetryAttempts = 5
    
    ; Initial delay between retry attempts (seconds)
    ; Type: Integer
    ; Default: 2
    ; Range: 1-10
    ; Impact: Longer delays reduce database server load
    BaseRetryDelay = 2
    
    ; Multiplier for progressive retry delays
    ; Type: Float
    ; Default: 2.0
    ; Range: 1.5-5.0
    ; Impact: Higher values create longer delays for later retries
    RetryMultiplier = 2.0
    
    ; Interval between database health checks (seconds)
    ; Type: Integer
    ; Default: 60
    ; Range: 30-300
    ; Impact: More frequent = better monitoring, more overhead
    HealthCheckInterval = 60
    
    ; Enable connection pool monitoring (if supported)
    ; Type: Boolean
    ; Default: true
    ; Impact: Provides better connection management but adds overhead
    MonitorConnectionPool = true
    
    ; Minimum connections to maintain during emergencies
    ; Type: Integer
    ; Default: 5
    ; Range: 1-20
    ; Impact: Ensures basic functionality during database issues
    EmergencyConnectionLimit = 5
    
    ; Enable automatic database failover (if configured)
    ; Type: Boolean
    ; Default: false
    ; Impact: Requires secondary database configuration
    EnableFailover = false
    
    ; Failover timeout before declaring primary database dead (seconds)
    ; Type: Integer
    ; Default: 180
    ; Range: 60-600
    FailoverTimeout = 180
```

### DatabaseStability.ini by Database Type

#### Local SQLite
```ini
ConnectionTimeout = 15
QueryTimeout = 30
MaxRetryAttempts = 3
HealthCheckInterval = 120
```

#### Local MySQL/PostgreSQL
```ini
ConnectionTimeout = 30
QueryTimeout = 60
MaxRetryAttempts = 5
HealthCheckInterval = 60
MonitorConnectionPool = true
```

#### Remote Database
```ini
ConnectionTimeout = 60
QueryTimeout = 120
MaxRetryAttempts = 8
BaseRetryDelay = 5
RetryMultiplier = 1.5
HealthCheckInterval = 90
```

#### High-Availability Setup
```ini
ConnectionTimeout = 45
MaxRetryAttempts = 10
EnableFailover = true
FailoverTimeout = 120
EmergencyConnectionLimit = 10
```

## AssetStability.ini - Asset Configuration

**Location**: `bin/config-include/AssetStability.ini`

```ini
[AssetStability]
    ; Enable asset loading monitoring and recovery
    ; Type: Boolean
    ; Default: true
    Enabled = true
    
    ; Timeout for individual asset loading operations (seconds)
    ; Type: Integer
    ; Default: 30
    ; Range: 10-120
    ; Impact: Longer timeouts more patient with slow asset servers
    AssetTimeout = 30
    
    ; Number of retry attempts for failed asset loads
    ; Type: Integer
    ; Default: 3
    ; Range: 1-10
    ; Impact: More retries = better recovery, slower failure detection
    AssetRetryAttempts = 3
    
    ; Delay between asset retry attempts (seconds)
    ; Type: Integer
    ; Default: 5
    ; Range: 1-30
    ; Impact: Longer delays reduce asset server load
    AssetRetryDelay = 5
    
    ; Size of emergency asset cache (megabytes)
    ; Type: Integer
    ; Default: 100
    ; Range: 50-1000
    ; Impact: Larger cache provides more fallback assets
    EmergencyCacheSize = 100
    
    ; File system path for emergency asset cache
    ; Type: String
    ; Default: "./assetcache/emergency"
    ; Impact: Must be writable location with sufficient space
    EmergencyCachePath = "./assetcache/emergency"
    
    ; Monitor usage of default/fallback assets
    ; Type: Boolean
    ; Default: true
    ; Impact: Helps identify missing asset problems
    MonitorDefaultAssets = true
    
    ; Number of asset failures before activating emergency cache
    ; Type: Integer
    ; Default: 10
    ; Range: 5-50
    ; Impact: Lower = more aggressive emergency cache activation
    FailureThreshold = 10
    
    ; Preload common assets into emergency cache on startup
    ; Type: Boolean
    ; Default: false
    ; Impact: Improves emergency response but increases startup time
    PreloadCache = false
    
    ; Enable asset integrity checking
    ; Type: Boolean
    ; Default: true
    ; Impact: Detects corrupted assets but adds processing overhead
    EnableIntegrityCheck = true
    
    ; Maximum asset size for integrity checking (megabytes)
    ; Type: Integer
    ; Default: 50
    ; Range: 10-500
    ; Impact: Large assets skip integrity checking for performance
    IntegrityCheckMaxSize = 50
```

### AssetStability.ini by Asset Service Type

#### Local Asset Service
```ini
AssetTimeout = 15
AssetRetryAttempts = 2
AssetRetryDelay = 2
EmergencyCacheSize = 50
```

#### Remote Asset Service
```ini
AssetTimeout = 60
AssetRetryAttempts = 5
AssetRetryDelay = 10
EmergencyCacheSize = 200
PreloadCache = true
```

#### CDN/High-Performance Asset Service
```ini
AssetTimeout = 45
AssetRetryAttempts = 4
EmergencyCacheSize = 300
EnableIntegrityCheck = true
IntegrityCheckMaxSize = 100
```

## ErrorDiagnostics.ini - Monitoring Configuration

**Location**: `bin/config-include/ErrorDiagnostics.ini`

```ini
[ErrorDiagnostics]
    ; Enable comprehensive diagnostic monitoring
    ; Type: Boolean
    ; Default: true
    Enabled = true
    
    ; Interval for collecting diagnostic data (seconds)
    ; Type: Integer
    ; Default: 60
    ; Range: 30-300
    ; Impact: More frequent = better granularity, more overhead
    CollectionInterval = 60
    
    ; Weight factor for errors in health score calculation (0.0-1.0)
    ; Type: Float
    ; Default: 0.4
    ; Range: 0.1-0.8
    ; Impact: Higher weight makes errors more influential in health scores
    ErrorWeight = 0.4
    
    ; Weight factor for performance metrics in health scores (0.0-1.0)
    ; Type: Float
    ; Default: 0.3
    ; Range: 0.1-0.6
    PerformanceWeight = 0.3
    
    ; Weight factor for resource usage in health scores (0.0-1.0)
    ; Type: Float
    ; Default: 0.2
    ; Range: 0.1-0.5
    ResourceWeight = 0.2
    
    ; Weight factor for system stability in health scores (0.0-1.0)
    ; Type: Float
    ; Default: 0.1
    ; Range: 0.05-0.3
    StabilityWeight = 0.1
    
    ; Enable automatic log file rotation
    ; Type: Boolean
    ; Default: true
    ; Impact: Prevents log files from growing too large
    LogRotationEnabled = true
    
    ; Maximum size before log rotation
    ; Type: String
    ; Default: 50MB
    ; Options: Size in KB, MB, or GB
    LogMaxSize = 50MB
    
    ; Maximum number of rotated log files to keep
    ; Type: Integer
    ; Default: 10
    ; Range: 3-50
    LogMaxFiles = 10
    
    ; Format for diagnostic data exports
    ; Type: String
    ; Default: JSON
    ; Options: JSON, XML, CSV
    ; Impact: JSON most flexible, CSV easiest for analysis
    ExportFormat = JSON
    
    ; Enable FPS (frames per second) tracking
    ; Type: Boolean
    ; Default: true
    ; Impact: Important for performance monitoring
    TrackFPS = true
    
    ; Enable memory usage tracking
    ; Type: Boolean
    ; Default: true
    ; Impact: Critical for detecting memory leaks
    TrackMemory = true
    
    ; Enable CPU usage tracking
    ; Type: Boolean
    ; Default: true
    ; Impact: Useful but may add overhead on some systems
    TrackCPU = true
    
    ; Enable automatic report generation
    ; Type: Boolean
    ; Default: true
    ; Impact: Provides scheduled health reports
    AutoReportGeneration = true
    
    ; Interval between automatic reports (seconds)
    ; Type: Integer
    ; Default: 3600 (1 hour)
    ; Range: 900-86400 (15 minutes to 1 day)
    ReportInterval = 3600
    
    ; Enable trend analysis
    ; Type: Boolean
    ; Default: true
    ; Impact: Provides historical performance insights
    EnableTrendAnalysis = true
    
    ; Number of data points for trend analysis
    ; Type: Integer
    ; Default: 100
    ; Range: 50-1000
    TrendAnalysisPoints = 100
```

### ErrorDiagnostics.ini Performance Profiles

#### Minimal Overhead Profile
```ini
CollectionInterval = 120
TrackCPU = false
EnableTrendAnalysis = false
LogMaxFiles = 5
AutoReportGeneration = false
```

#### Detailed Monitoring Profile
```ini
CollectionInterval = 30
TrackFPS = true
TrackMemory = true
TrackCPU = true
EnableTrendAnalysis = true
TrendAnalysisPoints = 200
ReportInterval = 1800
```

#### Production Balanced Profile
```ini
CollectionInterval = 60
ErrorWeight = 0.5
PerformanceWeight = 0.3
ResourceWeight = 0.2
LogRotationEnabled = true
LogMaxSize = 25MB
```

## Environment-Specific Configuration Examples

### Development Environment
**Focus**: Detailed debugging information with relaxed thresholds

```ini
# ErrorHandling.ini
[ErrorHandling]
ErrorThreshold = 100
LogLevel = Debug
DetailedLogging = true

# PhysicsStability.ini
[PhysicsStability]
MaxVelocity = 200.0
VelocityDampening = false

# ScriptEngineStability.ini
[ScriptEngineStability]
ScriptErrorThreshold = 50
AutoSuspend = false
ExecutionTimeThreshold = 500.0
```

### Production Grid Environment
**Focus**: Stability and user experience protection

```ini
# ErrorHandling.ini
[ErrorHandling]
ErrorThreshold = 25
AutoRecovery = true
MaxRecoveryAttempts = 5

# PhysicsStability.ini
[PhysicsStability]
MaxVelocity = 75.0
VelocityDampening = true
EnablePhysicsRestart = true

# ScriptEngineStability.ini
[ScriptEngineStability]
ScriptErrorThreshold = 5
AutoSuspend = true
MaxScriptsPerObject = 15
```

### Educational Environment
**Focus**: Balanced learning environment with safety nets

```ini
# ErrorHandling.ini
[ErrorHandling]
ErrorThreshold = 50
AutoRecovery = true

# PhysicsStability.ini
[PhysicsStability]
MaxVelocity = 100.0
StuckObjectTimeout = 60

# ScriptEngineStability.ini
[ScriptEngineStability]
ScriptErrorThreshold = 15
MonitorCompileTime = true
CompileTimeThreshold = 10000.0
```

### High-Performance Environment
**Focus**: Maximum performance with minimal overhead

```ini
# ErrorHandling.ini
[ErrorHandling]
HealthCheckInterval = 120
DetailedLogging = false

# ErrorDiagnostics.ini
[ErrorDiagnostics]
CollectionInterval = 120
TrackCPU = false
EnableTrendAnalysis = false
```

## Configuration Validation

### Syntax Validation
Ensure proper INI file syntax:
- Section headers in square brackets: `[SectionName]`
- Key-value pairs with equals sign: `Key = Value`
- Boolean values: `true` or `false` (case insensitive)
- Numeric values without quotes
- String values may be quoted but not required

### Range Validation
Check that numeric values fall within documented ranges:
```bash
# Example validation script snippet
if [ "$ErrorThreshold" -lt 10 ] || [ "$ErrorThreshold" -gt 200 ]; then
    echo "ErrorThreshold must be between 10 and 200"
fi
```

### Dependency Validation
Ensure dependent settings are compatible:
- `ErrorWindowSeconds` should be at least 2x `HealthCheckInterval`
- `MaxRetryAttempts` × `BaseRetryDelay` should not exceed emergency timeouts
- Weight factors in ErrorDiagnostics should sum to approximately 1.0

## Configuration Migration

### Upgrading from Default Configuration
When upgrading or customizing configuration:

1. **Backup existing configuration**
2. **Start with production-safe defaults**
3. **Gradually adjust thresholds based on monitoring**
4. **Test configuration changes in development first**
5. **Document changes and reasoning**

### Configuration Backup Strategy
```bash
#!/bin/bash
# Backup current configuration
DATE=$(date +%Y%m%d_%H%M%S)
mkdir -p config-backups/$DATE
cp config-include/Error*.ini config-backups/$DATE/
echo "Configuration backed up to config-backups/$DATE/"
```

---

*This configuration reference provides comprehensive documentation for all error handling system settings. For usage instructions, see the User Guide. For implementation details, see the Developer Guide.*