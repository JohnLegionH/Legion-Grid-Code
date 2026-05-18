# Test script to check if error handling modules load
Set-Location "D:\Opensim_Test_Grid\bin"

# Clear previous log
if (Test-Path "OpenSim.log") {
    Copy-Item "OpenSim.log" "OpenSim.log.backup"
}

Write-Host "Starting OpenSim to test error handling module loading..."

# Start OpenSim in background
$process = Start-Process -FilePath "OpenSim.exe" -PassThru -NoNewWindow

# Wait a few seconds for modules to load
Start-Sleep -Seconds 15

# Kill the process
if (!$process.HasExited) {
    $process.Kill()
    $process.WaitForExit()
    Write-Host "OpenSim stopped"
}

# Check for error handling modules in log
Write-Host "`nChecking for error handling modules in log..."
if (Test-Path "OpenSim.log") {
    $errorHandlingLines = Select-String -Path "OpenSim.log" -Pattern "ErrorHandling|PhysicsStability|ScriptEngineStability|DatabaseStability|AssetStability|ErrorDiagnostics"
    
    if ($errorHandlingLines) {
        Write-Host "SUCCESS: Error handling modules found in log:" -ForegroundColor Green
        $errorHandlingLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    } else {
        Write-Host "ERROR: No error handling modules found in log" -ForegroundColor Red
        Write-Host "Last 10 lines of log:"
        Get-Content "OpenSim.log" -Tail 10
    }
} else {
    Write-Host "ERROR: No log file found" -ForegroundColor Red
}