# OpenSim Error Handling System - User Guide

## Overview

The OpenSim Error Handling System provides comprehensive stability monitoring and automated recovery for your virtual world server. This system continuously monitors critical components and automatically recovers from common failure scenarios, ensuring your users experience a stable, reliable virtual environment.

## What This System Does For You

### Automatic Problem Detection
- **Physics Issues**: Detects stuck objects, velocity explosions, and out-of-bounds content
- **Script Problems**: Monitors runaway scripts, error loops, and resource abuse
- **Database Failures**: Identifies connection issues and transaction failures
- **Asset Loading**: Catches missing textures, meshes, and broken content
- **System Health**: Tracks overall server performance and stability

### Automatic Recovery
- **Self-Healing**: Most problems are fixed automatically without user intervention
- **Graceful Degradation**: When issues can't be fixed, the system prevents crashes
- **Emergency Procedures**: Provides manual recovery options for severe problems
- **Detailed Logging**: Records all issues for later analysis

## Console Commands Reference

All error handling commands are available through the OpenSim console. Type these commands at the console prompt to monitor and manage your server's health.

### System Health Overview

```
system health
```
**Purpose**: Shows overall system health for all regions
**Output**: Health percentage (0-100%) and brief status summary
**Example Output**:
```
Region: MainLand - Health: 85% (Good)
Region: Sandbox - Health: 92% (Excellent)
Overall Grid Health: 88% (Good)
```

### Global Error Handling

#### Check Error Status
```
error status
```
**Purpose**: Shows overall error handling system status
**What to look for**:
- Module status (Enabled/Disabled)
- Recent error counts
- Recovery actions taken
- System uptime since last major issue

#### Test Error Handling
```
error test null
```
**Purpose**: Safely tests error handling with a controlled null reference
**When to use**: During maintenance to verify error handling is working
**Note**: This is safe to run - it creates a test error that is immediately caught

#### Force Recovery
```
error recovery
```
**Purpose**: Manually triggers error recovery procedures
**When to use**: After experiencing problems, to ensure all systems are reset
**Effect**: Runs cleanup routines across all error handling modules

### Physics System Health

#### Physics Status
```
physics stability
```
**Purpose**: Shows physics engine health for all regions
**What to look for**:
- Frame time (should be under 20ms)
- Object velocity warnings
- Out-of-bounds objects
- Stuck collision counts

#### Force Physics Stabilization
```
physics stabilize [region-name]
```
**Purpose**: Manually stabilizes physics for a specific region
**When to use**: When objects are behaving erratically
**Example**: `physics stabilize MainLand`
**Note**: Leave region blank to stabilize all regions

#### Emergency Physics Reset
```
physics emergency
```
**Purpose**: Emergency reset of physics for all regions
**When to use**: Only when physics system is completely unstable
**Warning**: This will reset all physics objects - use sparingly

### Script Engine Management

#### Script Engine Status
```
script stability
```
**Purpose**: Shows script engine health and performance
**What to look for**:
- Scripts with high error rates
- Suspended scripts
- Script engine load
- Resource usage warnings

#### Suspend Problem Scripts
```
script suspend <script-id>
```
**Purpose**: Temporarily suspends a problematic script
**When to use**: When a specific script is causing problems
**Example**: `script suspend 12345678-1234-1234-1234-123456789abc`
**Note**: Suspended scripts can be resumed later

#### Resume Scripts
```
script resume <script-id>
```
**Purpose**: Resumes a previously suspended script
**Example**: `script resume 12345678-1234-1234-1234-123456789abc`

#### Reset Script Engine
```
script reset engine [region-name]
```
**Purpose**: Restarts the script engine for a region
**When to use**: When script engine becomes unresponsive
**Example**: `script reset engine MainLand`
**Warning**: This will stop all scripts temporarily

### Database Connection Health

#### Database Status
```
database status
```
**Purpose**: Shows database connection health
**What to look for**:
- Connection status (Connected/Disconnected)
- Recent connection errors
- Query performance
- Retry attempts

#### Test Database
```
database test [region-name]
```
**Purpose**: Tests database connectivity for a region
**Example**: `database test MainLand`
**Note**: Leave region blank to test all connections

#### Force Reconnection
```
database reconnect [region-name]
```
**Purpose**: Forces database reconnection
**When to use**: When experiencing database connectivity issues
**Example**: `database reconnect MainLand`

#### Emergency Database Recovery
```
database emergency
```
**Purpose**: Emergency database recovery for all regions
**When to use**: When database connections are completely lost
**Effect**: Attempts to restore all database connections

### Asset Loading Management

#### Asset Status
```
asset status
```
**Purpose**: Shows asset loading statistics
**What to look for**:
- Failed asset loads
- Cache hit rates
- Default asset usage (indicates missing content)
- Recovery attempts

#### Test Asset Loading
```
asset test <asset-id>
```
**Purpose**: Tests loading of a specific asset
**Example**: `asset test 89556747-24cb-43ed-920b-47caed15465f`
**When to use**: To verify problematic assets can be loaded

#### Manage Asset Cache
```
asset cache status
asset cache clear
```
**Purpose**: Manage emergency asset cache
**Commands**:
- `status`: Shows cache statistics
- `clear`: Clears the emergency cache

#### Asset Recovery
```
asset recovery
```
**Purpose**: Attempts to recover failed assets
**When to use**: After resolving asset server issues

### Comprehensive Diagnostics

#### Diagnostics Status
```
diagnostics status
```
**Purpose**: Shows comprehensive system diagnostics
**Output**: Detailed breakdown of all error handling modules

#### Generate Reports
```
diagnostics report
diagnostics report physics
diagnostics report scripts
diagnostics report database
diagnostics report assets
```
**Purpose**: Generates detailed reports for specific categories
**When to use**: For troubleshooting or performance analysis

#### Clear Diagnostic History
```
diagnostics clear
```
**Purpose**: Clears diagnostic history (not recommended during troubleshooting)
**When to use**: After resolving major issues to start fresh tracking

#### Export Diagnostics
```
diagnostics export [filename]
```
**Purpose**: Exports diagnostic data to a file
**Example**: `diagnostics export health_report_2023-12-01.json`
**Note**: Exports to the bin directory if no path specified

## Understanding Health Scores

The system provides health scores from 0-100% for easy assessment:

### Health Score Ranges
- **90-100%**: Excellent - System running optimally
- **80-89%**: Good - Minor issues present but stable
- **70-79%**: Fair - Some problems, monitor closely
- **60-69%**: Poor - Multiple issues, intervention recommended
- **Below 60%**: Critical - Immediate attention required

### What Affects Health Scores
- **Error Frequency**: More errors = lower health
- **Recovery Success**: Successful recoveries improve health
- **Performance Metrics**: Frame rates, response times
- **Resource Usage**: Memory, CPU, disk space
- **System Stability**: Uptime, crash frequency

## Daily Monitoring Routine

### Quick Health Check (30 seconds)
1. Run `system health` - Check overall status
2. Look for any regions below 80% health
3. If issues found, run specific diagnostics

### Weekly Detailed Check (5 minutes)
1. Run `diagnostics status` - Full system overview
2. Run `diagnostics report` - Generate detailed report
3. Check logs for recurring issues
4. Export diagnostics for record keeping

### Monthly Maintenance (15 minutes)
1. Run all status commands to baseline performance
2. Clear diagnostic history if no ongoing issues
3. Review trends in health scores
4. Update any configuration as needed

## When to Take Action

### Immediate Action Required
- Health score below 60% on any region
- Multiple error types occurring simultaneously
- Physics or script engine unresponsive
- Database connection failures

### Monitor Closely
- Health score 60-79%
- Increasing error trends
- Single error type recurring
- Performance degradation

### Investigate When Convenient
- Health score 80-89% with stable trend
- Occasional isolated errors
- Minor performance fluctuations

## Getting Help

### Log Files
Error handling logs are written to:
- Main log: `OpenSim.log`
- Diagnostic log: `OpenSimDiagnostics.log`
- Error details: `OpenSimErrors.log`

### Configuration Files
If you need to modify settings, configuration files are in:
- `bin/config-include/ErrorHandling.ini`
- `bin/config-include/PhysicsStability.ini`
- `bin/config-include/ScriptEngineStability.ini`
- `bin/config-include/DatabaseStability.ini`
- `bin/config-include/AssetStability.ini`
- `bin/config-include/ErrorDiagnostics.ini`

### Support Information
When seeking help, always include:
1. Output from `system health`
2. Output from `diagnostics status`
3. Recent log file entries
4. Description of user-visible problems
5. Recent changes to your setup

## Best Practices

### Daily Operations
- Check system health at least once daily
- Address any health scores below 80%
- Monitor trends rather than single incidents
- Keep diagnostic exports for historical tracking

### During Problems
- Run diagnostics before manual interventions
- Use specific recovery commands rather than full restarts
- Document what works for future reference
- Export diagnostics before and after fixes

### Preventive Maintenance
- Review weekly diagnostic reports
- Update configurations based on your usage patterns
- Test error handling during low-usage periods
- Keep system updated and properly configured

---

*This user guide covers the essential commands and procedures for monitoring and managing the OpenSim Error Handling System. For detailed configuration options, see the Administrator Guide. For technical implementation details, see the Developer Documentation.*