# OpenSim Error Handling System - Troubleshooting Guide

## Overview

This troubleshooting guide provides step-by-step solutions for common issues with the OpenSim Error Handling System. It includes diagnostic procedures, error resolution steps, and preventive measures to maintain system stability.

## Quick Diagnostic Checklist

When experiencing issues, run through this checklist first:

### 1. Basic System Check (2 minutes)
```bash
# Check if modules are loaded
grep "ErrorHandling.*Module.*Initialized" OpenSim.log
grep "Module.*failed to load" OpenSim.log

# Check overall system health
system health

# Check for recent critical errors
tail -n 50 OpenSim.log | grep -i error
```

### 2. Configuration Verification (1 minute)
```bash
# Verify config files exist
ls -la config-include/Error*.ini

# Check for configuration syntax errors
grep -i "error.*config" OpenSim.log
```

### 3. Resource Check (30 seconds)
```bash
# Check disk space
df -h

# Check memory usage
free -h

# Check process status
ps aux | grep OpenSim
```

## Common Issues and Solutions

### Module Loading Issues

#### Problem: Error Handling Modules Not Loading

**Symptoms:**
- Console commands not available (`help error` shows no commands)
- No error handling initialization messages in logs
- System behaves as if error handling is disabled

**Diagnostic Steps:**
```bash
# Check if module files exist
ls -la OpenSim/Region/CoreModules/Framework/ErrorHandling/
ls -la bin/config-include/Error*.ini

# Look for loading errors
grep -i "errorhandling" OpenSim.log
grep -i "failed to load" OpenSim.log
```

**Common Causes and Solutions:**

1. **Missing Module Files**
   ```bash
   # Verify all module files are present
   ErrorHandlingModule.cs
   PhysicsStabilityModule.cs
   ScriptEngineStabilityModule.cs
   DatabaseStabilityModule.cs
   AssetStabilityModule.cs
   ErrorDiagnosticsModule.cs
   ```

2. **Configuration File Issues**
   ```ini
   # Check ErrorHandling.ini syntax
   [ErrorHandling]
   Enabled = true  # No extra spaces, correct syntax
   ```

3. **Permission Problems**
   ```bash
   # Fix file permissions
   chmod 644 OpenSim/Region/CoreModules/Framework/ErrorHandling/*.cs
   chmod 644 bin/config-include/Error*.ini
   ```

#### Problem: Partial Module Loading

**Symptoms:**
- Some error handling commands work, others don't
- Only certain modules show in diagnostic output

**Solution:**
```bash
# Check which modules loaded successfully
grep "Module.*Initialized" OpenSim.log | grep -E "(Error|Physics|Script|Database|Asset)"

# Look for specific module errors
grep -E "(Physics|Script|Database|Asset).*error" OpenSim.log
```

Fix by checking individual configuration files and module dependencies.

### Configuration Issues

#### Problem: Invalid Configuration Values

**Symptoms:**
- Modules load but behave incorrectly
- Configuration validation errors in logs
- Unexpected threshold behaviors

**Diagnostic Steps:**
```bash
# Check for configuration warnings
grep -i "configuration.*warn\|config.*error" OpenSim.log

# Validate configuration syntax
# Check each .ini file for syntax errors
```

**Common Configuration Errors:**

1. **Invalid Threshold Values**
   ```ini
   # Wrong: Negative or extremely high values
   ErrorThreshold = -1
   ErrorThreshold = 999999
   
   # Correct: Reasonable ranges
   ErrorThreshold = 50
   ```

2. **Invalid Time Windows**
   ```ini
   # Wrong: Too short or too long
   ErrorWindowSeconds = 5
   ErrorWindowSeconds = 86400
   
   # Correct: Practical ranges
   ErrorWindowSeconds = 300
   ```

3. **Boolean Value Errors**
   ```ini
   # Wrong: Incorrect boolean syntax
   Enabled = yes
   Enabled = 1
   
   # Correct: Proper boolean values
   Enabled = true
   Enabled = false
   ```

#### Problem: Configuration Not Taking Effect

**Symptoms:**
- Changes to .ini files don't affect behavior
- System uses default values despite configuration

**Solution:**
1. **Restart OpenSim** - Configuration is loaded at startup
2. **Check file syntax** - Ensure no syntax errors
3. **Verify file location** - Files must be in `bin/config-include/`

### Performance Issues

#### Problem: High CPU Usage from Error Handling

**Symptoms:**
- High CPU usage when error handling is enabled
- Server lag or slow response times
- High memory usage growth

**Diagnostic Steps:**
```bash
# Check error handling resource usage
diagnostics status
diagnostics report

# Monitor resource usage
top -p $(pgrep OpenSim)
```

**Solutions:**

1. **Reduce Monitoring Frequency**
   ```ini
   [ErrorHandling]
   HealthCheckInterval = 120  # Increase from 60
   
   [ErrorDiagnostics]
   CollectionInterval = 120   # Increase from 60
   ```

2. **Disable Intensive Features**
   ```ini
   [ErrorDiagnostics]
   DetailedLogging = false
   TrackCPU = false
   
   [DatabaseStability]
   MonitorConnectionPool = false
   ```

3. **Adjust Thresholds**
   ```ini
   [ErrorHandling]
   ErrorThreshold = 100  # Increase to reduce sensitivity
   ```

#### Problem: Memory Leaks

**Symptoms:**
- Gradual memory usage increase
- Server becomes slow over time
- Eventually runs out of memory

**Diagnostic Steps:**
```bash
# Monitor memory usage over time
while true; do
    ps aux | grep OpenSim | awk '{print $6}'
    sleep 300
done

# Check diagnostic log sizes
ls -lh *Diagnostics.log *Errors.log
```

**Solutions:**

1. **Enable Log Rotation**
   ```ini
   [ErrorDiagnostics]
   LogRotationEnabled = true
   LogMaxSize = 50MB
   LogMaxFiles = 5
   ```

2. **Reduce History Retention**
   ```ini
   [ErrorHandling]
   ErrorWindowSeconds = 180  # Reduce from 300
   
   [ErrorDiagnostics]
   CollectionInterval = 120  # Reduce frequency
   ```

3. **Clear Diagnostic History**
   ```bash
   # In OpenSim console
   diagnostics clear
   ```

### Health Score Issues

#### Problem: Persistently Low Health Scores

**Symptoms:**
- Health scores consistently below 70%
- No obvious performance problems
- False positive alerts

**Diagnostic Steps:**
```bash
# Get detailed health breakdown
diagnostics report
diagnostics status

# Check recent errors
diagnostics report errors
```

**Common Causes and Solutions:**

1. **Too Sensitive Thresholds**
   ```ini
   # Increase thresholds for your environment
   [ErrorHandling]
   ErrorThreshold = 75  # Increase from 50
   
   [PhysicsStability]
   PhysicsFrameTimeThreshold = 30.0  # Increase from 20.0
   ```

2. **Expected Errors Being Counted**
   ```bash
   # Review error types in logs
   grep "ERROR.*HANDLING" OpenSim.log | sort | uniq -c
   
   # Adjust configuration to exclude known issues
   ```

3. **Hardware-Specific Issues**
   ```ini
   # For slower hardware
   [PhysicsStability]
   PhysicsFrameTimeThreshold = 40.0
   
   [ScriptEngineStability]
   ExecutionTimeThreshold = 200
   ```

#### Problem: Health Scores Not Updating

**Symptoms:**
- Health scores remain static
- No diagnostic data collection
- System health shows "Unknown"

**Solution:**
```bash
# Check if diagnostic module is running
grep "ErrorDiagnostics.*Initialized" OpenSim.log

# Restart health monitoring
system health
diagnostics status

# Force diagnostic collection
diagnostics report
```

### Physics Stability Issues

#### Problem: Objects Flying Away Despite Physics Stability

**Symptoms:**
- Objects still achieve excessive velocities
- Physics instability continues
- Velocity clamping not working

**Diagnostic Steps:**
```bash
# Check physics stability status
physics stability

# Look for physics errors
grep -i "physics.*error\|physics.*warn" OpenSim.log
```

**Solutions:**

1. **Lower Velocity Limits**
   ```ini
   [PhysicsStability]
   MaxVelocity = 50.0      # Reduce from 100.0
   MaxAngularVelocity = 25.0  # Reduce from 50.0
   ```

2. **Enable More Aggressive Monitoring**
   ```ini
   [PhysicsStability]
   VelocityDampening = true
   DampeningFactor = 0.90   # More aggressive dampening
   ```

3. **Check Physics Engine Compatibility**
   ```bash
   # Verify physics engine in use
   grep -i "physics.*engine\|using.*physics" OpenSim.log
   ```

#### Problem: Physics Emergency Mode Triggering Frequently

**Symptoms:**
- Frequent "physics emergency" alerts
- Physics resets happening often
- Poor physics performance

**Solution:**
```ini
# Adjust emergency thresholds
[PhysicsStability]
PhysicsFrameTimeThreshold = 35.0  # Less sensitive
StuckObjectTimeout = 60           # Longer timeout

# Reduce monitoring frequency
PerformanceCheckInterval = 60     # Less frequent checks
```

### Script Engine Issues

#### Problem: Scripts Being Suspended Incorrectly

**Symptoms:**
- Well-behaved scripts get suspended
- Too many false positives
- User complaints about script reliability

**Diagnostic Steps:**
```bash
# Check script suspension activity
script stability

# Review suspended scripts
grep "script.*suspend" OpenSim.log
```

**Solutions:**

1. **Adjust Error Thresholds**
   ```ini
   [ScriptEngineStability]
   ScriptErrorThreshold = 15  # Increase from 10
   ScriptErrorWindow = 120    # Longer window
   ```

2. **Review Error Types**
   ```bash
   # Check what errors are causing suspensions
   grep "script.*error" OpenSim.log | sort | uniq -c
   ```

3. **Disable Auto-Suspension Temporarily**
   ```ini
   [ScriptEngineStability]
   AutoSuspend = false
   ```

#### Problem: Script Engine Performance Monitoring False Positives

**Symptoms:**
- Performance warnings for normal operation
- Script engine restart alerts
- No actual performance problems

**Solution:**
```ini
# Adjust performance thresholds for your hardware
[ScriptEngineStability]
ExecutionTimeThreshold = 150   # Increase from 100
MemoryThreshold = 15.0         # Increase from 10.0
EngineRestartThreshold = 150   # Increase from 100
```

### Database Connection Issues

#### Problem: Frequent Database Reconnection Attempts

**Symptoms:**
- Constant "database reconnection" messages
- Database performance warnings
- Users experiencing lag

**Diagnostic Steps:**
```bash
# Check database connectivity
database status
database test

# Review connection errors
grep -i "database.*error\|database.*reconnect" OpenSim.log
```

**Solutions:**

1. **Adjust Connection Settings**
   ```ini
   [DatabaseStability]
   ConnectionTimeout = 45      # Increase timeout
   MaxRetryAttempts = 3       # Reduce retries
   HealthCheckInterval = 120   # Less frequent checks
   ```

2. **Check Database Server Performance**
   ```bash
   # Test database connectivity outside OpenSim
   mysql -h [host] -u [user] -p[password] [database] -e "SELECT 1"
   ```

3. **Network Issues**
   ```bash
   # Check network latency to database server
   ping [database-server]
   traceroute [database-server]
   ```

#### Problem: Database Emergency Mode

**Symptoms:**
- "Database emergency mode" alerts
- Database operations failing
- Data not saving properly

**Immediate Actions:**
```bash
# Check database server status
systemctl status mysql  # or postgresql

# Test basic connectivity
database test

# Attempt manual reconnection
database reconnect
```

**Resolution:**
1. **Verify Database Server** - Ensure database server is running
2. **Check Credentials** - Verify connection strings are correct
3. **Network Connectivity** - Test network path to database
4. **Emergency Recovery** - Use `database emergency` if needed

### Asset Loading Issues

#### Problem: High Default Asset Usage

**Symptoms:**
- Many "default asset" warnings
- Missing textures/objects for users
- Asset recovery attempts failing

**Diagnostic Steps:**
```bash
# Check asset loading statistics
asset status

# Test specific problematic assets
asset test [asset-id]
```

**Solutions:**

1. **Check Asset Server Connectivity**
   ```bash
   # Test asset server access
   curl -I [asset-server-url]
   ```

2. **Adjust Asset Timeouts**
   ```ini
   [AssetStability]
   AssetTimeout = 45          # Increase from 30
   AssetRetryAttempts = 5     # Increase retries
   AssetRetryDelay = 10       # Longer delays
   ```

3. **Enable Emergency Caching**
   ```ini
   [AssetStability]
   EmergencyCacheSize = 200   # Increase cache size
   PreloadCache = true        # Preload common assets
   ```

## Log Analysis

### Key Log Messages to Monitor

**Startup Issues:**
```bash
grep -E "(Module.*failed|Configuration.*error|Unable to load)" OpenSim.log
```

**Runtime Errors:**
```bash
grep -E "(ERROR.*HANDLING|PHYSICS.*STABILITY|SCRIPT.*STABILITY)" OpenSim.log
```

**Performance Issues:**
```bash
grep -E "(High.*usage|Performance.*threshold|Frame.*time)" OpenSim.log
```

**Recovery Actions:**
```bash
grep -E "(Recovery.*initiated|Emergency.*mode|Automatic.*recovery)" OpenSim.log
```

### Log File Locations and Purposes

| Log File | Purpose | Key Information |
|----------|---------|-----------------|
| `OpenSim.log` | Main application log | Module loading, general errors, system status |
| `OpenSimErrors.log` | Error handling specific | Detailed error tracking and recovery actions |
| `OpenSimDiagnostics.log` | Diagnostic data | Health scores, performance metrics, trends |
| `OpenSimStats.log` | Performance statistics | FPS, memory usage, resource metrics |

## Emergency Procedures

### Complete System Recovery

When multiple systems are failing:

1. **Immediate Assessment**
   ```bash
   system health
   diagnostics status
   ```

2. **Emergency Recovery**
   ```bash
   error recovery
   physics emergency
   database emergency
   asset recovery
   ```

3. **Verify Recovery**
   ```bash
   system health
   diagnostics report
   ```

### Selective Recovery

For specific subsystem issues:

**Physics Issues:**
```bash
physics stabilize
physics emergency  # If severe
```

**Script Problems:**
```bash
script stability
script reset engine  # If needed
```

**Database Issues:**
```bash
database reconnect
database emergency  # If severe
```

**Asset Problems:**
```bash
asset recovery
asset cache clear  # If cache corrupted
```

## Prevention and Maintenance

### Daily Monitoring

```bash
#!/bin/bash
# Daily health check script

echo "=== Daily OpenSim Health Check ==="
echo "Date: $(date)"

# Basic health check
echo -e "\n--- System Health ---"
echo "system health" | nc localhost 8003

# Check for recent errors
echo -e "\n--- Recent Errors ---"
tail -n 100 OpenSim.log | grep -i error | tail -n 10

# Disk space check
echo -e "\n--- Disk Space ---"
df -h | grep -E "/$|/var|/home"

# Log file sizes
echo -e "\n--- Log File Sizes ---"
ls -lh *.log | awk '{print $5, $9}'
```

### Weekly Maintenance

```bash
#!/bin/bash
# Weekly maintenance script

# Generate comprehensive report
echo "diagnostics export weekly_report_$(date +%Y%m%d).json" | nc localhost 8003

# Clean up old diagnostic data
echo "diagnostics clear" | nc localhost 8003

# Archive old logs
find . -name "*.log.*" -mtime +30 -exec rm {} \;

# Check configuration files
for file in config-include/Error*.ini; do
    echo "Checking $file"
    # Add configuration validation here
done
```

---

*This troubleshooting guide covers the most common issues encountered with the OpenSim Error Handling System. For basic usage, see the User Guide. For advanced configuration, see the Administrator Guide.*