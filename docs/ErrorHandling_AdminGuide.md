# OpenSim Error Handling System - Administrator Guide

## Overview

This guide provides comprehensive information for administrators to configure, tune, and manage the OpenSim Error Handling System. It covers installation verification, configuration options, performance tuning, integration with existing systems, and advanced troubleshooting procedures.

## System Architecture

### Module Structure
The error handling system consists of six interconnected modules:

1. **ErrorHandlingModule** - Core coordination and global exception handling
2. **PhysicsStabilityModule** - Physics engine monitoring and stabilization
3. **ScriptEngineStabilityModule** - Script performance and error management
4. **DatabaseStabilityModule** - Database connection resilience
5. **AssetStabilityModule** - Asset loading recovery and caching
6. **ErrorDiagnosticsModule** - Centralized monitoring and reporting

### Integration Points
- **Scene Management**: Hooks into scene initialization and shutdown
- **Physics Engines**: Integrates with all supported physics engines (ubOde, BulletSim, BasicPhysics)
- **Script Engines**: Works with YEngine and other script engines
- **Database Layers**: Monitors all database operations (MySQL, SQLite, PostgreSQL)
- **Asset Services**: Integrates with local and remote asset services

## Installation Verification

### Module Loading Check
After server startup, verify all modules are loaded:

```bash
# Check OpenSim.log for module initialization
grep "ErrorHandling.*Module.*Initialized" OpenSim.log
grep "PhysicsStability.*Module.*Initialized" OpenSim.log
grep "ScriptEngineStability.*Module.*Initialized" OpenSim.log
grep "DatabaseStability.*Module.*Initialized" OpenSim.log
grep "AssetStability.*Module.*Initialized" OpenSim.log
grep "ErrorDiagnostics.*Module.*Initialized" OpenSim.log
```

### Configuration Validation
Verify configuration files are loaded correctly:

```bash
# Check for configuration loading messages
grep "Error handling configuration loaded" OpenSim.log
grep "Configuration validation.*passed" OpenSim.log
```

### Console Command Test
Test that console commands are available:
```
help error
help physics
help script
help database
help asset
help diagnostics
help system
```

## Configuration Reference

### Core Error Handling Configuration
**File**: `bin/config-include/ErrorHandling.ini`

```ini
[ErrorHandling]
    ; Enable/disable the error handling module
    Enabled = true
    
    ; Global error threshold before emergency procedures
    ErrorThreshold = 50
    
    ; Time window for error counting (seconds)
    ErrorWindowSeconds = 300
    
    ; Enable automatic recovery attempts
    AutoRecovery = true
    
    ; Maximum recovery attempts before manual intervention required
    MaxRecoveryAttempts = 3
    
    ; Log level for error handling (Debug, Info, Warn, Error)
    LogLevel = Info
    
    ; Emergency procedures timeout (seconds)
    EmergencyTimeout = 30
    
    ; Health check interval (seconds)
    HealthCheckInterval = 60
    
    ; Enable detailed error logging
    DetailedLogging = true
```

#### Configuration Parameter Details

**Enabled**
- Default: `true`
- Purpose: Master switch for error handling
- Impact: Disabling removes all error handling protection

**ErrorThreshold**
- Default: `50`
- Purpose: Number of errors in time window before emergency procedures
- Tuning: Lower for more sensitive detection, higher for noisy environments
- Range: 10-200

**ErrorWindowSeconds**
- Default: `300` (5 minutes)
- Purpose: Time window for counting errors
- Tuning: Shorter for rapid response, longer for trend analysis
- Range: 60-1800

**AutoRecovery**
- Default: `true`
- Purpose: Enable automatic recovery attempts
- Impact: When false, only manual recovery available

**MaxRecoveryAttempts**
- Default: `3`
- Purpose: Limit recovery attempts to prevent infinite loops
- Tuning: Higher for persistent problems, lower for fast failure
- Range: 1-10

### Physics Stability Configuration
**File**: `bin/config-include/PhysicsStability.ini`

```ini
[PhysicsStability]
    ; Enable physics stability monitoring
    Enabled = true
    
    ; Maximum object velocity before clamping (m/s)
    MaxVelocity = 100.0
    
    ; Maximum angular velocity before clamping (rad/s)
    MaxAngularVelocity = 50.0
    
    ; Boundary limits for object positions
    MaxPositionX = 1000.0
    MaxPositionY = 1000.0
    MaxPositionZ = 4000.0
    MinPositionZ = -100.0
    
    ; Physics frame time threshold (ms)
    PhysicsFrameTimeThreshold = 20.0
    
    ; Stuck object detection timeout (seconds)
    StuckObjectTimeout = 30
    
    ; Collision cleanup interval (seconds)
    CollisionCleanupInterval = 60
    
    ; Enable velocity dampening for unstable objects
    VelocityDampening = true
    
    ; Dampening factor (0.0-1.0)
    DampeningFactor = 0.95
```

#### Physics Tuning Guidelines

**MaxVelocity/MaxAngularVelocity**
- Conservative: 50/25 (strict control)
- Default: 100/50 (balanced)
- Permissive: 200/100 (loose control)

**Boundary Limits**
- Adjust based on your region sizes
- Consider neighboring regions for seamless crossing
- Account for building heights and underground areas

**PhysicsFrameTimeThreshold**
- Target: Under 20ms for smooth operation
- Acceptable: 20-30ms
- Problem: Over 30ms (investigate physics load)

### Script Engine Configuration
**File**: `bin/config-include/ScriptEngineStability.ini`

```ini
[ScriptEngineStability]
    ; Enable script stability monitoring
    Enabled = true
    
    ; Script error threshold before suspension
    ScriptErrorThreshold = 10
    
    ; Script error time window (seconds)
    ScriptErrorWindow = 60
    
    ; Maximum scripts per object
    MaxScriptsPerObject = 20
    
    ; Script performance monitoring interval (seconds)
    PerformanceCheckInterval = 30
    
    ; Script execution time threshold (ms)
    ExecutionTimeThreshold = 100
    
    ; Memory usage threshold per script (MB)
    MemoryThreshold = 10.0
    
    ; Enable automatic script suspension
    AutoSuspend = true
    
    ; Script engine restart threshold
    EngineRestartThreshold = 100
```

#### Script Management Strategies

**Error Thresholds**
- Development environments: Higher thresholds (20+ errors)
- Production environments: Lower thresholds (5-10 errors)
- Public grids: Strict thresholds (3-5 errors)

**Performance Limits**
- High-performance hardware: Higher limits
- Shared hosting: Conservative limits
- Public access: Restrictive limits

### Database Stability Configuration
**File**: `bin/config-include/DatabaseStability.ini`

```ini
[DatabaseStability]
    ; Enable database stability monitoring
    Enabled = true
    
    ; Database connection timeout (seconds)
    ConnectionTimeout = 30
    
    ; Query timeout (seconds)
    QueryTimeout = 60
    
    ; Connection retry attempts
    MaxRetryAttempts = 5
    
    ; Base retry delay (seconds)
    BaseRetryDelay = 2
    
    ; Progressive retry multiplier
    RetryMultiplier = 2.0
    
    ; Connection health check interval (seconds)
    HealthCheckInterval = 60
    
    ; Connection pool monitoring
    MonitorConnectionPool = true
    
    ; Emergency connection limit
    EmergencyConnectionLimit = 5
```

#### Database Tuning by Type

**Local SQLite**
- Shorter timeouts (10-20 seconds)
- Fewer retry attempts (3-5)
- More frequent health checks

**Local MySQL/PostgreSQL**
- Standard timeouts (30-60 seconds)
- Standard retry attempts (5-7)
- Regular health checks

**Remote Database**
- Longer timeouts (60-120 seconds)
- More retry attempts (7-10)
- Less frequent health checks (to reduce network load)

### Asset Stability Configuration
**File**: `bin/config-include/AssetStability.ini`

```ini
[AssetStability]
    ; Enable asset stability monitoring
    Enabled = true
    
    ; Asset loading timeout (seconds)
    AssetTimeout = 30
    
    ; Asset retry attempts
    AssetRetryAttempts = 3
    
    ; Asset retry delay (seconds)
    AssetRetryDelay = 5
    
    ; Emergency cache size (MB)
    EmergencyCacheSize = 100
    
    ; Emergency cache location
    EmergencyCachePath = "./assetcache/emergency"
    
    ; Default asset usage monitoring
    MonitorDefaultAssets = true
    
    ; Asset failure threshold before emergency cache activation
    FailureThreshold = 10
    
    ; Cache preload on startup
    PreloadCache = false
```

### Diagnostics Configuration
**File**: `bin/config-include/ErrorDiagnostics.ini`

```ini
[ErrorDiagnostics]
    ; Enable diagnostic system
    Enabled = true
    
    ; Diagnostic data collection interval (seconds)
    CollectionInterval = 60
    
    ; Health scoring weight factors
    ErrorWeight = 0.4
    PerformanceWeight = 0.3
    ResourceWeight = 0.2
    StabilityWeight = 0.1
    
    ; Diagnostic log rotation
    LogRotationEnabled = true
    LogMaxSize = 50MB
    LogMaxFiles = 10
    
    ; Diagnostic export format (JSON, XML, CSV)
    ExportFormat = JSON
    
    ; Performance tracking
    TrackFPS = true
    TrackMemory = true
    TrackCPU = true
    
    ; Report generation schedule
    AutoReportGeneration = true
    ReportInterval = 3600
```

## Performance Tuning

### Low-Resource Environments

For servers with limited resources:

```ini
; Reduce monitoring frequency
HealthCheckInterval = 120
CollectionInterval = 120
PerformanceCheckInterval = 60

; Increase thresholds to reduce overhead
ErrorThreshold = 100
ScriptErrorThreshold = 20

; Disable intensive monitoring
DetailedLogging = false
MonitorConnectionPool = false
TrackCPU = false
```

### High-Performance Environments

For powerful dedicated servers:

```ini
; Increase monitoring frequency
HealthCheckInterval = 30
CollectionInterval = 30
PerformanceCheckInterval = 15

; Lower thresholds for early detection
ErrorThreshold = 25
ScriptErrorThreshold = 5

; Enable all monitoring features
DetailedLogging = true
MonitorConnectionPool = true
TrackCPU = true
```

### Production Grid Recommendations

```ini
; Balanced settings for production
ErrorThreshold = 50
ScriptErrorThreshold = 8
HealthCheckInterval = 60
AutoRecovery = true
AutoSuspend = true

; Conservative physics limits
MaxVelocity = 75.0
MaxAngularVelocity = 40.0

; Robust database settings
ConnectionTimeout = 45
MaxRetryAttempts = 7
```

## Integration with Existing Systems

### Log Management Integration

#### With logrotate (Linux)
Create `/etc/logrotate.d/opensim-errors`:

```bash
/path/to/opensim/bin/OpenSimErrors.log
/path/to/opensim/bin/OpenSimDiagnostics.log {
    daily
    rotate 30
    compress
    delaycompress
    missingok
    notifempty
    create 644 opensim opensim
    postrotate
        /bin/systemctl reload opensim || true
    endscript
}
```

#### With Windows Event Log
Add to ErrorHandling.ini:

```ini
[ErrorHandling]
    ; Enable Windows Event Log integration
    UseEventLog = true
    EventLogSource = OpenSim
```

### Monitoring System Integration

#### Nagios/Icinga Check Script
Create monitoring script:

```bash
#!/bin/bash
# /usr/local/bin/check_opensim_health.sh

HEALTH_OUTPUT=$(echo "system health" | nc localhost 8003 | grep "Overall Grid Health")
HEALTH_PERCENT=$(echo $HEALTH_OUTPUT | grep -o '[0-9]\+%' | grep -o '[0-9]\+')

if [ $HEALTH_PERCENT -ge 90 ]; then
    echo "OK - Grid Health: $HEALTH_PERCENT%"
    exit 0
elif [ $HEALTH_PERCENT -ge 70 ]; then
    echo "WARNING - Grid Health: $HEALTH_PERCENT%"
    exit 1
else
    echo "CRITICAL - Grid Health: $HEALTH_PERCENT%"
    exit 2
fi
```

#### Prometheus Metrics Export
Add to ErrorDiagnostics.ini:

```ini
[ErrorDiagnostics]
    ; Enable Prometheus metrics
    PrometheusEnabled = true
    PrometheusPort = 9091
    PrometheusPath = /metrics
```

### Backup Integration

#### Pre-backup Health Check
```bash
#!/bin/bash
# Check system health before backup
HEALTH=$(echo "system health" | nc localhost 8003 | grep "Overall Grid Health" | grep -o '[0-9]\+')

if [ $HEALTH -lt 60 ]; then
    echo "System health too low for backup ($HEALTH%). Aborting."
    exit 1
fi

# Proceed with backup
./backup_opensim.sh
```

## Advanced Administration

### Custom Error Handlers

Create custom handlers in configuration:

```ini
[ErrorHandling]
    ; Custom error handler scripts
    CustomHandlerEnabled = true
    CustomHandlerScript = "./scripts/custom_error_handler.sh"
    CustomHandlerTimeout = 60
```

### Multi-Region Management

For large grids with multiple regions:

```ini
[ErrorHandling]
    ; Region-specific thresholds
    RegionSpecificThresholds = true
    
    ; High-traffic regions
    MainLand.ErrorThreshold = 30
    MainLand.HealthCheckInterval = 30
    
    ; Low-traffic regions
    Sandbox.ErrorThreshold = 75
    Sandbox.HealthCheckInterval = 120
```

### Load Balancer Health Checks

Configure load balancer to check OpenSim health:

```bash
# Health check endpoint
curl -s http://opensim-server:8003/health | grep "Grid Health" | grep -o '[0-9]\+' | awk '$1 >= 70 {exit 0} {exit 1}'
```

## Security Considerations

### Console Command Access

Restrict error handling commands to administrators:

```ini
[AccessControl]
    ; Require administrator level for error commands
    ErrorCommands.MinLevel = Administrator
    PhysicsCommands.MinLevel = Administrator
    DatabaseCommands.MinLevel = Administrator
```

### Log File Security

Protect diagnostic logs:

```bash
# Set appropriate permissions
chmod 640 OpenSimErrors.log
chmod 640 OpenSimDiagnostics.log
chown opensim:opensim-admins *.log
```

### Network Security

If using remote monitoring:

```ini
[ErrorDiagnostics]
    ; Restrict monitoring access
    MonitoringBindAddress = 127.0.0.1
    MonitoringAllowedIPs = 192.168.1.0/24,10.0.0.0/8
```

## Troubleshooting Admin Issues

### Modules Not Loading

1. Check module files exist in correct locations
2. Verify configuration file syntax
3. Check OpenSim.log for loading errors
4. Ensure proper file permissions

### High Resource Usage

1. Reduce monitoring frequency
2. Disable detailed logging
3. Increase thresholds
4. Check for log rotation issues

### False Positives

1. Adjust thresholds for your environment
2. Increase error time windows
3. Review error categories in logs
4. Consider environment-specific tuning

### Performance Impact

1. Monitor server resources before/after enabling
2. Use performance profiling tools
3. Adjust collection intervals
4. Disable non-essential features

## Maintenance Procedures

### Weekly Maintenance

1. Review diagnostic reports
2. Check log file sizes
3. Verify system health trends
4. Update configuration if needed

### Monthly Maintenance

1. Archive old diagnostic data
2. Review threshold effectiveness
3. Update emergency procedures
4. Test recovery mechanisms

### Quarterly Reviews

1. Analyze long-term trends
2. Optimize configuration parameters
3. Update integration scripts
4. Review security settings

---

*This administrator guide provides comprehensive coverage of configuration, tuning, and management procedures for the OpenSim Error Handling System. For user procedures, see the User Guide. For technical implementation details, see the Developer Documentation.*