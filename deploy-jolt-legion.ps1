# =====================================================================================
# deploy-jolt-legion.ps1
#
# Deploys the FULL matched assembly set from the merged Legion source build into
# Legion Grid's live shared bin, so Jolt can be enabled as the physics engine.
#
# Mirrors the proven deploy-fixes.ps1 pattern:
#   - REFUSES to run while the grid is up (loaded DLLs are locked = the silent-stale trap)
#   - copies ONLY .dll files (never configs, region data, assets, or the DB)
#   - backs up every replaced DLL OUTSIDE the bin (Mono.Addins scans bin; stray copies
#     inside it risk duplicate module registration)
#   - prints src/dst hash pairs and fails loudly on any mismatch
#
# ---------------------------------------------------------------------------
# WHY THIS SCRIPT IS SHAPED THE WAY IT IS  (read before editing)
#
# v1 of this script globbed *.dll out of the shared bin and copied whatever it found.
# That SILENTLY missed Legion.Physics.dll and Legion.Vehicles.dll, which do NOT build
# to the shared bin - they build to their own project output dirs. The deploy then
# reported "verified" (every file it chose to copy did hash-match), the grid booted,
# and Mono.Addins failed with the misleading:
#
#     Type 'OpenSim.Region.PhysicsModules.LegionJolt.LegionJoltScene, ...'
#     not found in add-in 'OpenSim.Region.PhysicsModule.LegionJolt,0.9.3.0'
#
# ...which is what a MISSING TRANSITIVE DEPENDENCY looks like: Mono.Addins loads the
# assembly to resolve the type, the CLR can't resolve Legion.Physics/Legion.Vehicles,
# the load throws, and the type is reported "not found".
#
# This has now bitten three times in one day (Legion.Physics, InWorldz.Phlox, and
# Legion.Physics+Legion.Vehicles again). The structural fixes below make it impossible:
#
#   1. $explicitSources - a per-file source map for every assembly that does NOT
#      build to the shared bin. Never inferred, always stated.
#   2. $required        - files that MUST reach the target. Checked BEFORE any copy
#      (source must exist) and AFTER every copy (target must exist + hash-match).
#      A missing entry is a hard, loud FAILURE - never a silent skip.
#   3. Freshness check  - warns when a compiled artifact is much older than the rest
#      of the set, which is how the stale InWorldz.Phlox slipped through.
#
# If you add a project whose output does not land in the shared bin, add it to BOTH
# $explicitSources and $required. That is the whole contract.
# ---------------------------------------------------------------------------
#
# Run AFTER stopping the grid, BEFORE booting it:
#     powershell -File D:\legion-grid-source\deploy-jolt-legion.ps1
#
# Then set the physics engine in "D:\opensim - Use this december 2025\bin\OpenSim.ini":
#     [Startup]
#         physics = Jolt          # CASE-SENSITIVE. Revert = delete this line.
#
# Built from: /d/legion-grid-source on branch slua-tier2-tables (Jolt merged).
# =====================================================================================

$ErrorActionPreference = "Stop"

$repo = "D:\legion-grid-source"
$src  = Join-Path $repo "bin"
$dst  = "D:\opensim - Use this december 2025\bin"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = "D:\legion-grid-backups\jolt-deploy-$stamp"

# ---------------------------------------------------------------------------
# Per-file source map: assemblies that do NOT build to the shared bin.
# These are the ones a glob-the-bin approach silently misses.
# ---------------------------------------------------------------------------
$explicitSources = [ordered]@{
    "Legion.Physics.dll"  = Join-Path $repo "OpenSim\Addons\LegionPhysics\Legion.Physics\bin\Debug\net8.0"
    "Legion.Vehicles.dll" = Join-Path $repo "OpenSim\Addons\LegionPhysics\Legion.Vehicles\bin\Debug\net8.0"

    # C5: the repo's bin\C5.dll is STALE (1.1.0.0). InWorldz.Phlox compiles against the NuGet
    # C5 3.0.0 and .NET refuses to load a LOWER version than a reference demands, so shipping the
    # bin copy breaks Phlox at load ("Type ... not found in add-in" - it bit us on 2026-08-01).
    # Source it from the NuGet cache so the deploy actively installs the CORRECT version rather
    # than merely avoiding the wrong one. Phlox.ScriptEngine asks for 1.1.0.0 and is satisfied by
    # 3.0.0.0 (higher is allowed); the reverse is not. Do NOT switch this back to the repo bin.
    "C5.dll"              = "C:\Users\jarno\.nuget\packages\c5\3.0.0\lib\net8.0"
}

# Hard floors: post-flight fails if the deployed assembly is BELOW these versions.
# Catches the C5-class regression by analysis instead of by a boot crash.
$minVersions = @{
    "C5.dll" = [Version]"3.0.0.0"
}

# ---------------------------------------------------------------------------
# Files that MUST reach the target. Missing at source OR at target = hard failure.
# This is the backstop that turns a silent skip into a loud stop.
# ---------------------------------------------------------------------------
$required = @(
    # --- Jolt physics module + its dependency closure ---
    "OpenSim.Region.PhysicsModule.LegionJolt.dll",  # the module (Mono.Addins entry point)
    "Legion.Physics.dll",                           # backend      - NOT in shared bin
    "Legion.Vehicles.dll",                          # vehicles     - NOT in shared bin
    "JoltPhysicsSharp.dll",                         # managed binding (NuGet)
    "joltc.dll",                                    # native Jolt - MUST be the PATCHED build (see $patchedJoltcHash guard)
    # --- the fixes this deploy exists to carry ---
    "OpenSim.Region.Framework.dll",                 # Fix B: vehicle persistence (HasGroupChanged)
    "Phlox.ScriptEngine.dll",                       # Fix A: llSetVehicle* routing + int->float coercion
    "InWorldz.Phlox.dll",                           # Fix A dep - only rebuilt when built EXPLICITLY
    # --- core assemblies the module is compiled against (matched-set integrity) ---
    "OpenSim.Framework.dll",
    "OpenSim.Region.PhysicsModules.SharedBase.dll",
    "C5.dll"                                        # must be 3.0.0.0 - see $explicitSources
)

# Third-party / NuGet artifacts: legitimately older than the build, skip freshness check.
$thirdParty = @("joltc.dll", "JoltPhysicsSharp.dll", "C5.dll", "OpenMetaverse.Rendering.Meshmerizer.dll")

# ---------------------------------------------------------------------------
# The PATCHED joltc native (per-system TempAllocator). The per-INSTANCE _simLock
# in JoltPhysicsBackend REQUIRES it: stock JoltPhysics.Native 1.0.4 shares ONE
# TempAllocator across all physics systems, and with instance locks that means
# "TempAllocator: Freeing in the wrong order" -> std::abort() the moment two
# regions step at once. Provenance + rebuild recipe: native\joltc\README.md.
# ---------------------------------------------------------------------------
$patchedJoltcHash = "16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5"
$stockJoltcHash   = "67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910"
$vendoredJoltc    = Join-Path $repo "native\joltc\win-x64\joltc.dll"

function Assert-PatchedJoltc([string]$path, [string]$where) {
    $h = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($h -eq $patchedJoltcHash) {
        Write-Host ("  joltc.dll {0}: PATCHED build ({1}...) OK" -f $where, $h.Substring(0, 8)) -ForegroundColor Green
        return $true
    }
    $kind = if ($h -eq $stockJoltcHash) { "STOCK NuGet 1.0.4" } else { "UNKNOWN build ($($h.Substring(0,12))...)" }
    Write-Host ""
    Write-Host ("JOLTC GUARD FAIL ({0}): {1} joltc.dll detected -" -f $where, $kind) -ForegroundColor Red
    Write-Host "    $path" -ForegroundColor Red
    Write-Host "The per-instance _simLock requires the PATCHED native (per-system" -ForegroundColor Red
    Write-Host "TempAllocator); the stock DLL shares one allocator across regions and" -ForegroundColor Red
    Write-Host "WILL crash (TempAllocator: Freeing in the wrong order -> abort)." -ForegroundColor Red
    Write-Host "Restore from the vendored copy, then re-run:" -ForegroundColor Red
    Write-Host "    Copy-Item `"$vendoredJoltc`" `"$path`" -Force" -ForegroundColor Yellow
    return $false
}

# Build-time-only artifacts that must NOT be deployed. The live grid runs Phlox today
# with no Antlr3 in its bin, which proves these are compiler tooling, not runtime deps.
$exclude = @(
    "Antlr3.Runtime.dll",
    "Antlr3.Runtime.Debug.dll",
    "Antlr3.StringTemplate.dll"
)

Write-Host ""
Write-Host "Legion Grid - Jolt deploy (full matched assembly set)" -ForegroundColor Cyan
Write-Host "  source : $src  (+ per-project output dirs, see `$explicitSources)"
Write-Host "  target : $dst"
Write-Host "  backup : $backupRoot"
Write-Host ""

# ---------------------------------------------------------------------------
# 1. Refuse while the grid is running
# ---------------------------------------------------------------------------
$running = @()
foreach ($n in @("OpenSim", "Robust", "OpenSim.ConsoleClient")) {
    $p = Get-Process -Name $n -ErrorAction SilentlyContinue
    if ($p) { $running += "$n (PID $($p.Id -join ','))" }
}
# also catch the dotnet-launched form: dotnet OpenSim.dll / dotnet Robust.dll
$dn = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -like "*OpenSim.dll*" -or $_.CommandLine -like "*Robust.dll*" }
foreach ($d in $dn) { $running += "dotnet (PID $($d.ProcessId))" }

if ($running.Count -gt 0) {
    Write-Host "REFUSING TO DEPLOY: the grid is RUNNING -" -ForegroundColor Red
    foreach ($r in $running) { Write-Host "    $r" -ForegroundColor Red }
    Write-Host "Stop it cleanly (console 'quit'), then re-run this script, THEN boot."
    exit 1
}

if (-not (Test-Path $src)) { Write-Host "MISSING SOURCE DIR: $src" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $dst)) { Write-Host "MISSING TARGET DIR: $dst" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------------------
# 2. Build the manifest: explicit sources first, then the shared bin.
#    Every entry carries the source path it came from - nothing is inferred later.
# ---------------------------------------------------------------------------
$manifest = [ordered]@{}   # name -> full source path

foreach ($name in $explicitSources.Keys) {
    $manifest[$name] = Join-Path $explicitSources[$name] $name
}
foreach ($f in (Get-ChildItem -Path $src -Filter *.dll -File | Sort-Object Name)) {
    if ($exclude -contains $f.Name) { continue }
    if ($manifest.Contains($f.Name)) { continue }   # explicit source wins over the shared bin
    $manifest[$f.Name] = $f.FullName
}

# ---------------------------------------------------------------------------
# 3. PRE-FLIGHT (the guard that v1 lacked)
#    (a) every required file must appear in the manifest
#    (b) every manifest entry must exist on disk at its stated source path
#    Nothing is copied until both hold.
# ---------------------------------------------------------------------------
$preflightFailed = $false

foreach ($r in $required) {
    if (-not $manifest.Contains($r)) {
        Write-Host "PRE-FLIGHT FAIL: required file '$r' is not in the manifest." -ForegroundColor Red
        Write-Host "                 Add it to `$explicitSources (if it builds to its own project bin)." -ForegroundColor Red
        $preflightFailed = $true
    }
}

foreach ($name in $manifest.Keys) {
    if (-not (Test-Path $manifest[$name])) {
        $isRequired = $required -contains $name
        if ($isRequired) {
            Write-Host "PRE-FLIGHT FAIL: REQUIRED source missing: $($manifest[$name])" -ForegroundColor Red
            Write-Host "                 Build it first - it may build to its own project output dir." -ForegroundColor Red
            $preflightFailed = $true
        }
        else {
            Write-Host "PRE-FLIGHT FAIL: source missing: $($manifest[$name])" -ForegroundColor Red
            $preflightFailed = $true
        }
    }
}

# joltc guard, SOURCE side: never CARRY a stock DLL to the live bin. This runs
# before any copy so a clobbered repo bin (NuGet restore/publish) stops the
# deploy instead of propagating.
if ($manifest.Contains("joltc.dll") -and (Test-Path $manifest["joltc.dll"])) {
    if (-not (Assert-PatchedJoltc $manifest["joltc.dll"] "deploy source")) { $preflightFailed = $true }
}

if ($preflightFailed) {
    Write-Host ""
    Write-Host "ABORTED before copying anything. Fix the above, then re-run." -ForegroundColor Red
    exit 1
}

Write-Host ("Pre-flight OK: {0} file(s) in manifest, all {1} required present at source." -f $manifest.Count, $required.Count) -ForegroundColor Green

# ---------------------------------------------------------------------------
# 4. Freshness check - catches the "forgot to rebuild it" class (stale InWorldz.Phlox)
# ---------------------------------------------------------------------------
$compiled = @()
foreach ($r in $required) { if ($thirdParty -notcontains $r) { $compiled += $r } }
$times = @()
foreach ($c in $compiled) { $times += (Get-Item $manifest[$c]).LastWriteTime }
if ($times.Count -gt 0) {
    $newest = ($times | Measure-Object -Maximum).Maximum
    foreach ($c in $compiled) {
        $t = (Get-Item $manifest[$c]).LastWriteTime
        if (($newest - $t).TotalHours -gt 2) {
            Write-Host ("WARNING: '{0}' is {1:N1}h older than the newest build output ({2:HH:mm} vs {3:HH:mm})." -f `
                $c, ($newest - $t).TotalHours, $t, $newest) -ForegroundColor Yellow
            Write-Host "         It may be STALE - rebuild it explicitly if its sources changed." -ForegroundColor Yellow
        }
    }
}
Write-Host ""

# ---------------------------------------------------------------------------
# 5. Work out what actually needs to move (new or changed only)
# ---------------------------------------------------------------------------
$toCopy = @()
foreach ($name in $manifest.Keys) {
    $s = $manifest[$name]
    $d = Join-Path $dst $name
    if (Test-Path $d) {
        if ((Get-FileHash $s -Algorithm SHA256).Hash -ne (Get-FileHash $d -Algorithm SHA256).Hash) {
            $toCopy += [pscustomobject]@{ Name = $name; Src = $s; Dst = $d; Kind = "REPLACE" }
        }
    }
    else {
        $toCopy += [pscustomobject]@{ Name = $name; Src = $s; Dst = $d; Kind = "NEW" }
    }
}

if ($toCopy.Count -eq 0) {
    Write-Host "Nothing to copy - target already matches source." -ForegroundColor Green
}
else {
    $nNew = @($toCopy | Where-Object { $_.Kind -eq "NEW" }).Count
    $nRep = @($toCopy | Where-Object { $_.Kind -eq "REPLACE" }).Count
    Write-Host ("Files to deploy: {0}  (new: {1}, replaced: {2})" -f $toCopy.Count, $nNew, $nRep)
    Write-Host ""

    # ----- back up everything we are about to replace (outside the bin) -----
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    foreach ($i in ($toCopy | Where-Object { $_.Kind -eq "REPLACE" })) {
        Copy-Item $i.Dst (Join-Path $backupRoot $i.Name) -Force
    }
    Write-Host ("Backed up {0} replaced DLL(s) to $backupRoot" -f $nRep) -ForegroundColor DarkGray
    Write-Host ""

    # ----- copy + hash-verify each file -----
    $failed = $false
    foreach ($i in $toCopy) {
        Copy-Item $i.Src $i.Dst -Force

        # carry the .pdb alongside when the build produced one (test grid: useful stacks)
        $pdb = [System.IO.Path]::ChangeExtension($i.Src, ".pdb")
        if (Test-Path $pdb) { Copy-Item $pdb ([System.IO.Path]::ChangeExtension($i.Dst, ".pdb")) -Force }

        $hs = (Get-FileHash $i.Src -Algorithm SHA256).Hash.Substring(0, 12)
        $hd = (Get-FileHash $i.Dst -Algorithm SHA256).Hash.Substring(0, 12)
        if ($hs -eq $hd) { $ok = "OK" } else { $ok = "HASH MISMATCH!"; $failed = $true }

        $colour = "Gray"
        if ($i.Kind -eq "NEW") { $colour = "Yellow" }
        if ($ok -ne "OK") { $colour = "Red" }
        Write-Host ("{0,-52} {1,-8} src={2} dst={3}  {4}" -f $i.Name, $i.Kind, $hs, $hd, $ok) -ForegroundColor $colour
    }

    if ($failed) {
        Write-Host ""
        Write-Host "DEPLOY INCOMPLETE - hash mismatch above. Do NOT boot; investigate first." -ForegroundColor Red
        Write-Host "Rollback: copy the DLLs from $backupRoot back into the bin."
        exit 1
    }
}

# ---------------------------------------------------------------------------
# 6. POST-FLIGHT: prove every REQUIRED file is now in the target and matches source.
#    This is what makes "verified" mean something.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Post-flight - required files in target:" -ForegroundColor Cyan
$postFailed = $false
foreach ($r in $required) {
    $d = Join-Path $dst $r
    if (-not (Test-Path $d)) {
        Write-Host ("  {0,-52} ** MISSING FROM TARGET **" -f $r) -ForegroundColor Red
        $postFailed = $true
        continue
    }
    $hs = (Get-FileHash $manifest[$r] -Algorithm SHA256).Hash.Substring(0, 12)
    $hd = (Get-FileHash $d -Algorithm SHA256).Hash.Substring(0, 12)
    if ($hs -eq $hd) {
        Write-Host ("  {0,-52} {1}  OK" -f $r, $hd) -ForegroundColor Green
    }
    else {
        Write-Host ("  {0,-52} src={1} dst={2}  MISMATCH" -f $r, $hs, $hd) -ForegroundColor Red
        $postFailed = $true
    }

    # Version floor (C5-class regression guard): a LOWER assembly version than a consumer
    # references fails at load, which surfaces as a misleading Mono.Addins "type not found".
    if ($minVersions.ContainsKey($r)) {
        try {
            $actual = [Reflection.AssemblyName]::GetAssemblyName($d).Version
            if ($actual -lt $minVersions[$r]) {
                Write-Host ("       ^ VERSION TOO LOW: {0} is {1}, need >= {2}" -f $r, $actual, $minVersions[$r]) -ForegroundColor Red
                $postFailed = $true
            }
            else {
                Write-Host ("       ^ version {0} (>= {1}) OK" -f $actual, $minVersions[$r]) -ForegroundColor DarkGray
            }
        }
        catch {
            Write-Host ("       ^ could not read version of {0}: {1}" -f $r, $_.Exception.Message) -ForegroundColor Red
            $postFailed = $true
        }
    }
}

# joltc guard, LIVE side: the bin the grid boots from must hold the patched
# native. Catches anything that touched the live bin outside this script.
Write-Host ""
if (-not (Assert-PatchedJoltc (Join-Path $dst "joltc.dll") "LIVE bin")) { $postFailed = $true }

if ($postFailed) {
    Write-Host ""
    Write-Host "POST-FLIGHT FAILED - the deploy is NOT complete. Do NOT boot." -ForegroundColor Red
    if (Test-Path $backupRoot) { Write-Host "Rollback: copy the DLLs from $backupRoot back into the bin." }
    exit 1
}

# ---------------------------------------------------------------------------
# 7. Next steps
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Deploy verified - all required files present and hash-matched." -ForegroundColor Green
Write-Host ""
Write-Host "NEXT:" -ForegroundColor Cyan
Write-Host "  1. Edit `"$dst\OpenSim.ini`" -> [Startup] -> add:   physics = Jolt"
Write-Host "     (CASE-SENSITIVE 'Jolt'. Do NOT edit OpenSimDefaults.ini - leave it pristine.)"
Write-Host "  2. Boot the grid and look for:  [LEGION JOLT] enabled (physics = Jolt)"
Write-Host "  3. Rollback if needed: restore DLLs from $backupRoot and delete the physics line."
Write-Host ""
