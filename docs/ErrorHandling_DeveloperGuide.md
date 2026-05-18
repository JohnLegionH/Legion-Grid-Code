# OpenSim Error Handling System - Developer Guide

## Overview

This guide provides comprehensive technical documentation for developers working with the OpenSim Error Handling System. It covers module architecture, extension points, integration patterns, and implementation details for maintaining and extending the system.

## Architecture Overview

### Module Hierarchy

```
ErrorHandlingModule (Core Coordinator)
├── PhysicsStabilityModule
├── ScriptEngineStabilityModule  
├── DatabaseStabilityModule
├── AssetStabilityModule
└── ErrorDiagnosticsModule (Central Reporting)
```

### Design Patterns

**ISharedRegionModule Pattern**
- All modules implement OpenSim's standard `ISharedRegionModule` interface
- Consistent lifecycle management (Initialize, AddRegion, RegionLoaded, RemoveRegion, Close)
- Scene-aware operation with multi-region support

**Observer Pattern**
- Event-driven architecture for error detection and handling
- Loose coupling between modules through event subscriptions
- Centralized event coordination through ErrorHandlingModule

**Strategy Pattern**
- Different recovery strategies for different error types
- Pluggable error handlers for extensibility
- Configurable behavior through strategy selection

**Singleton Pattern**
- Module instances shared across regions
- Thread-safe singleton implementation
- Resource sharing and coordination

## Core Module Implementation

### ErrorHandlingModule.cs

**Location**: `OpenSim/Region/CoreModules/Framework/ErrorHandling/ErrorHandlingModule.cs`

#### Class Structure

```csharp
public class ErrorHandlingModule : ISharedRegionModule, ICommandModule
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    
    // Configuration
    private IConfigSource m_config;
    private bool m_enabled = true;
    private int m_errorThreshold = 50;
    private int m_errorWindowSeconds = 300;
    
    // State management
    private readonly Dictionary<UUID, Scene> m_scenes = new Dictionary<UUID, Scene>();
    private readonly Dictionary<string, int> m_errorCounts = new Dictionary<string, int>();
    private readonly object m_lock = new object();
    
    // Error handling
    private Timer m_healthCheckTimer;
    private bool m_emergencyMode = false;
}
```

#### Key Methods

**Error Detection Wrapper**
```csharp
public T SafeExecute<T>(string operation, Func<T> action, T defaultValue = default(T))
{
    try
    {
        return action();
    }
    catch (Exception e)
    {
        RecordError(operation, e);
        return defaultValue;
    }
}
```

**Error Recording**
```csharp
private void RecordError(string operation, Exception exception)
{
    lock (m_lock)
    {
        string errorKey = $"{operation}:{exception.GetType().Name}";
        m_errorCounts.TryGetValue(errorKey, out int count);
        m_errorCounts[errorKey] = count + 1;
        
        // Check thresholds and trigger recovery if needed
        if (GetTotalErrors() > m_errorThreshold)
        {
            TriggerEmergencyProcedures();
        }
    }
}
```

**Health Monitoring**
```csharp
private void OnHealthCheckTimer(object sender, ElapsedEventArgs e)
{
    foreach (var scene in m_scenes.Values)
    {
        var health = CalculateSceneHealth(scene);
        if (health < 60)
        {
            m_log.WarnFormat("[ERROR HANDLING] Scene {0} health critical: {1}%", 
                scene.Name, health);
            InitiateSceneRecovery(scene);
        }
    }
}
```

### Module Communication

#### Event System

**Error Events**
```csharp
public class ErrorEventArgs : EventArgs
{
    public string Module { get; set; }
    public string Operation { get; set; }
    public Exception Exception { get; set; }
    public DateTime Timestamp { get; set; }
    public UUID RegionID { get; set; }
}

public event EventHandler<ErrorEventArgs> OnError;
```

**Recovery Events**
```csharp
public class RecoveryEventArgs : EventArgs
{
    public string Module { get; set; }
    public string RecoveryAction { get; set; }
    public bool Success { get; set; }
    public UUID RegionID { get; set; }
}

public event EventHandler<RecoveryEventArgs> OnRecovery;
```

#### Module Registration

```csharp
public interface IErrorHandlingModule
{
    void RegisterErrorHandler(string errorType, IErrorHandler handler);
    void ReportError(string module, string operation, Exception exception);
    void ReportRecovery(string module, string action, bool success);
    int GetErrorCount(string module, TimeSpan window);
}
```

## Specialized Module Implementations

### PhysicsStabilityModule.cs

#### Physics Monitoring

**Velocity Monitoring**
```csharp
private void CheckObjectVelocities(Scene scene)
{
    foreach (var group in scene.GetSceneObjectGroups())
    {
        var velocity = group.RootPart.Velocity;
        if (velocity.Length() > m_maxVelocity)
        {
            // Clamp velocity
            var clampedVelocity = Vector3.Normalize(velocity) * m_maxVelocity;
            group.RootPart.Velocity = clampedVelocity;
            
            m_log.WarnFormat("[PHYSICS STABILITY] Clamped velocity for object {0} in {1}", 
                group.UUID, scene.Name);
        }
    }
}
```

**Boundary Checking**
```csharp
private void CheckObjectBoundaries(Scene scene)
{
    var regionSize = scene.RegionInfo.RegionSizeX;
    
    foreach (var group in scene.GetSceneObjectGroups())
    {
        var pos = group.AbsolutePosition;
        bool needsCorrection = false;
        
        if (pos.X < 0 || pos.X > regionSize || 
            pos.Y < 0 || pos.Y > regionSize ||
            pos.Z < m_minPositionZ || pos.Z > m_maxPositionZ)
        {
            needsCorrection = true;
        }
        
        if (needsCorrection)
        {
            CorrectObjectPosition(group, scene);
        }
    }
}
```

### ScriptEngineStabilityModule.cs

#### Script Error Tracking

**Error Event Handling**
```csharp
private void OnScriptError(UUID itemID, string error)
{
    lock (m_scriptErrors)
    {
        if (!m_scriptErrors.ContainsKey(itemID))
        {
            m_scriptErrors[itemID] = new List<DateTime>();
        }
        
        m_scriptErrors[itemID].Add(DateTime.UtcNow);
        
        // Clean old errors outside time window
        var cutoff = DateTime.UtcNow.AddSeconds(-m_scriptErrorWindow);
        m_scriptErrors[itemID].RemoveAll(dt => dt < cutoff);
        
        // Check if script should be suspended
        if (m_scriptErrors[itemID].Count > m_scriptErrorThreshold)
        {
            SuspendScript(itemID);
        }
    }
}
```

**Performance Monitoring**
```csharp
private void MonitorScriptPerformance()
{
    var scenes = m_scenes.Values.ToList();
    
    foreach (var scene in scenes)
    {
        var scriptEngine = scene.RequestModuleInterface<IScriptModule>();
        if (scriptEngine != null)
        {
            var stats = scriptEngine.GetStats();
            
            if (stats.ExecutionTime > m_executionTimeThreshold)
            {
                m_log.WarnFormat("[SCRIPT STABILITY] High execution time in {0}: {1}ms", 
                    scene.Name, stats.ExecutionTime);
                
                OptimizeScriptExecution(scene);
            }
        }
    }
}
```

### DatabaseStabilityModule.cs

#### Connection Monitoring

**Connection Health Check**
```csharp
private void CheckDatabaseConnections()
{
    foreach (var scene in m_scenes.Values)
    {
        try
        {
            // Test connection with simple query
            var result = scene.SimulationDataService.TestConnection();
            
            if (!result)
            {
                m_log.WarnFormat("[DATABASE STABILITY] Connection test failed for {0}", 
                    scene.Name);
                AttemptReconnection(scene);
            }
        }
        catch (Exception e)
        {
            m_log.ErrorFormat("[DATABASE STABILITY] Error testing connection for {0}: {1}", 
                scene.Name, e.Message);
            RecordConnectionError(scene, e);
        }
    }
}
```

**Progressive Retry Logic**
```csharp
private bool AttemptReconnection(Scene scene)
{
    int delay = m_baseRetryDelay;
    
    for (int i = 0; i < m_maxRetryAttempts; i++)
    {
        try
        {
            Thread.Sleep(delay);
            
            // Attempt reconnection
            scene.SimulationDataService.Reconnect();
            
            m_log.InfoFormat("[DATABASE STABILITY] Reconnection successful for {0} after {1} attempts", 
                scene.Name, i + 1);
            return true;
        }
        catch (Exception e)
        {
            m_log.WarnFormat("[DATABASE STABILITY] Reconnection attempt {0} failed for {1}: {2}", 
                i + 1, scene.Name, e.Message);
            
            delay = (int)(delay * m_retryMultiplier);
        }
    }
    
    return false;
}
```

## Extension Points

### Custom Error Handlers

#### IErrorHandler Interface

```csharp
public interface IErrorHandler
{
    string HandlerName { get; }
    string[] SupportedErrorTypes { get; }
    bool CanHandle(Exception exception);
    RecoveryResult HandleError(string operation, Exception exception, Scene scene);
}

public class RecoveryResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public bool RequiresManualIntervention { get; set; }
    public TimeSpan RetryDelay { get; set; }
}
```

#### Example Custom Handler

```csharp
public class CustomNetworkErrorHandler : IErrorHandler
{
    public string HandlerName => "CustomNetworkErrorHandler";
    public string[] SupportedErrorTypes => new[] { "System.Net.WebException", "System.TimeoutException" };
    
    public bool CanHandle(Exception exception)
    {
        return exception is WebException || exception is TimeoutException;
    }
    
    public RecoveryResult HandleError(string operation, Exception exception, Scene scene)
    {
        // Custom recovery logic
        if (exception is WebException webEx)
        {
            return HandleWebException(webEx, scene);
        }
        
        return new RecoveryResult
        {
            Success = false,
            RequiresManualIntervention = true,
            Message = "Unknown network error"
        };
    }
    
    private RecoveryResult HandleWebException(WebException webEx, Scene scene)
    {
        // Implementation specific logic
        return new RecoveryResult { Success = true };
    }
}
```

### Module Registration

```csharp
// In your module's Initialize method
var errorHandling = scene.RequestModuleInterface<IErrorHandlingModule>();
if (errorHandling != null)
{
    errorHandling.RegisterErrorHandler("NetworkErrors", new CustomNetworkErrorHandler());
}
```

### Configuration Extensions

#### Custom Configuration Sections

```csharp
public class CustomModuleConfig
{
    public static CustomModuleConfig Load(IConfigSource config)
    {
        var cfg = new CustomModuleConfig();
        var section = config.Configs["CustomErrorHandling"];
        
        if (section != null)
        {
            cfg.CustomThreshold = section.GetInt("CustomThreshold", 10);
            cfg.CustomEnabled = section.GetBoolean("CustomEnabled", true);
            cfg.CustomHandlers = section.GetString("CustomHandlers", "").Split(',');
        }
        
        return cfg;
    }
}
```

## Database Schema Integration

### Error Tracking Tables

```sql
CREATE TABLE error_tracking (
    id INT AUTO_INCREMENT PRIMARY KEY,
    region_id CHAR(36) NOT NULL,
    module_name VARCHAR(100) NOT NULL,
    operation_name VARCHAR(200) NOT NULL,
    error_type VARCHAR(100) NOT NULL,
    error_message TEXT,
    stack_trace TEXT,
    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_region_time (region_id, timestamp),
    INDEX idx_module_type (module_name, error_type)
);

CREATE TABLE recovery_actions (
    id INT AUTO_INCREMENT PRIMARY KEY,
    region_id CHAR(36) NOT NULL,
    module_name VARCHAR(100) NOT NULL,
    action_name VARCHAR(200) NOT NULL,
    success BOOLEAN NOT NULL,
    message TEXT,
    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_region_time (region_id, timestamp)
);
```

### Data Access Layer

```csharp
public interface IErrorTrackingData
{
    void RecordError(UUID regionId, string module, string operation, 
                    string errorType, string message, string stackTrace);
    void RecordRecovery(UUID regionId, string module, string action, 
                       bool success, string message);
    List<ErrorRecord> GetErrors(UUID regionId, DateTime since);
    Dictionary<string, int> GetErrorCounts(UUID regionId, TimeSpan window);
}
```

## Testing Framework

### Unit Testing Base Classes

```csharp
public abstract class ErrorHandlingTestBase : TestBase
{
    protected MockScene m_scene;
    protected MockErrorHandlingModule m_errorHandler;
    
    [SetUp]
    public virtual void Setup()
    {
        m_scene = new MockScene();
        m_errorHandler = new MockErrorHandlingModule();
        m_errorHandler.AddRegion(m_scene);
    }
    
    protected void SimulateError(string operation, Exception exception)
    {
        m_errorHandler.ReportError("TestModule", operation, exception);
    }
    
    protected void AssertErrorRecorded(string operation, Type exceptionType)
    {
        Assert.IsTrue(m_errorHandler.HasError(operation, exceptionType));
    }
}
```

### Integration Testing

```csharp
[TestFixture]
public class PhysicsStabilityIntegrationTests : ErrorHandlingTestBase
{
    private PhysicsStabilityModule m_physicsModule;
    
    [SetUp]
    public override void Setup()
    {
        base.Setup();
        m_physicsModule = new PhysicsStabilityModule();
        m_physicsModule.AddRegion(m_scene);
    }
    
    [Test]
    public void TestVelocityClamping()
    {
        // Create test object with excessive velocity
        var testObject = CreateTestObject();
        testObject.RootPart.Velocity = new Vector3(1000, 0, 0);
        
        // Trigger physics check
        m_physicsModule.CheckPhysicsStability(m_scene);
        
        // Verify velocity was clamped
        Assert.Less(testObject.RootPart.Velocity.Length(), 100.1f);
    }
}
```

## Performance Considerations

### Memory Management

**Efficient Collections**
```csharp
// Use appropriate collection types
private readonly ConcurrentDictionary<string, int> m_errorCounts = 
    new ConcurrentDictionary<string, int>();

// Implement cleanup for growing collections
private void CleanupOldErrors()
{
    var cutoff = DateTime.UtcNow.AddMinutes(-m_errorWindowMinutes);
    
    foreach (var key in m_errorCounts.Keys.ToList())
    {
        if (m_errorTimestamps[key] < cutoff)
        {
            m_errorCounts.TryRemove(key, out _);
            m_errorTimestamps.TryRemove(key, out _);
        }
    }
}
```

**Object Pooling**
```csharp
public class DiagnosticEventPool
{
    private readonly ConcurrentQueue<DiagnosticEvent> m_pool = 
        new ConcurrentQueue<DiagnosticEvent>();
    
    public DiagnosticEvent Get()
    {
        if (m_pool.TryDequeue(out var evt))
        {
            evt.Reset();
            return evt;
        }
        
        return new DiagnosticEvent();
    }
    
    public void Return(DiagnosticEvent evt)
    {
        m_pool.Enqueue(evt);
    }
}
```

### Threading Considerations

**Thread-Safe Operations**
```csharp
// Use concurrent collections where possible
private readonly ConcurrentDictionary<UUID, SceneHealth> m_sceneHealth = 
    new ConcurrentDictionary<UUID, SceneHealth>();

// Minimal locking with reader-writer locks
private readonly ReaderWriterLockSlim m_configLock = new ReaderWriterLockSlim();

public void UpdateConfiguration(ErrorHandlingConfig config)
{
    m_configLock.EnterWriteLock();
    try
    {
        m_config = config;
    }
    finally
    {
        m_configLock.ExitWriteLock();
    }
}
```

## Debugging and Diagnostics

### Diagnostic Output

**Structured Logging**
```csharp
private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

private void LogError(string operation, Exception exception, UUID regionId)
{
    m_log.ErrorFormat("[{0}] Operation '{1}' failed in region {2}: {3}\n{4}",
        GetType().Name, operation, regionId, exception.Message, exception.StackTrace);
}

private void LogRecovery(string action, bool success, UUID regionId)
{
    var level = success ? "INFO" : "WARN";
    m_log.InfoFormat("[{0}] Recovery action '{1}' {2} in region {3}",
        GetType().Name, action, success ? "succeeded" : "failed", regionId);
}
```

**Debug Commands**
```csharp
[Command("debug errors", "Shows detailed error debugging information")]
public void HandleDebugErrors(string module, string[] cmdparams)
{
    MainConsole.Instance.Output("=== Error Handling Debug Information ===");
    
    foreach (var scene in m_scenes.Values)
    {
        MainConsole.Instance.OutputFormat("Scene: {0}", scene.Name);
        MainConsole.Instance.OutputFormat("  Error Count: {0}", GetErrorCount(scene.RegionInfo.RegionID));
        MainConsole.Instance.OutputFormat("  Health Score: {0}%", CalculateSceneHealth(scene));
        MainConsole.Instance.OutputFormat("  Emergency Mode: {0}", IsInEmergencyMode(scene));
    }
}
```

## Deployment Considerations

### Module Loading Order

Ensure proper module loading sequence in `OpenSim.ini`:

```ini
[Modules]
    ; Load error handling modules early
    ErrorHandlingModule = "OpenSim.Region.CoreModules.dll:OpenSim.Region.CoreModules.Framework.ErrorHandling.ErrorHandlingModule"
    
    ; Other modules load after error handling is established
    PhysicsModule = "OpenSim.Region.PhysicsModule.ubOde.dll:OpenSim.Region.PhysicsModule.ubOde"
```

### Configuration Validation

```csharp
public bool ValidateConfiguration(IConfigSource config)
{
    var errors = new List<string>();
    
    // Validate required sections exist
    if (config.Configs["ErrorHandling"] == null)
    {
        errors.Add("Missing [ErrorHandling] configuration section");
    }
    
    // Validate parameter ranges
    var errorThreshold = config.Configs["ErrorHandling"].GetInt("ErrorThreshold", 50);
    if (errorThreshold < 1 || errorThreshold > 1000)
    {
        errors.Add("ErrorThreshold must be between 1 and 1000");
    }
    
    if (errors.Any())
    {
        m_log.ErrorFormat("[ERROR HANDLING] Configuration validation failed: {0}", 
            string.Join(", ", errors));
        return false;
    }
    
    return true;
}
```

---

*This developer guide provides comprehensive technical documentation for extending and maintaining the OpenSim Error Handling System. For user procedures, see the User Guide. For configuration details, see the Administrator Guide.*