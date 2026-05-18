# OpenSim 0.9.3.0 Modernization Project Guide

## Project Overview

This document outlines the comprehensive modernization of OpenSim 0.9.3.0 to fully utilize .NET 8 capabilities while improving performance, stability, and maintainability. The project focuses on creating a robust test environment for safe development and systematic updates.

### Current State
- OpenSim 0.9.3.0 running on production grid
- Partial .NET 8 implementation (needs completion)
- Nini.dll requires update to version 1.1.0
- Known issues: region crossing inefficiencies, script performance, crash stability

### Primary Goals
1. Complete .NET 8 modernization
2. Establish isolated test grid environment
3. Improve region crossing performance
4. Enhance script execution efficiency and stability
5. Future: Physics engine improvements

---

## License and Attribution Management

### Preserving Original Licenses
The OpenSim project uses the **BSD 3-Clause License** (see LICENSE.txt). All original files maintain their copyright headers and attributions. When modifying existing files:

1. **Preserve original copyright headers** - Never remove existing copyright notices
2. **Add modification notes** - Add comments indicating changes made
3. **Maintain license compatibility** - Ensure any new dependencies are BSD-compatible

### Adding New Files
For completely new files created during modernization:

```csharp
/*
 * Copyright (c) 2025 Contributors, http://opensimulator.org/
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
```

### Third-Party Dependencies
- Check `ThirdPartyLicenses/` directory for existing license compatibility
- Document any new dependencies in both code and LICENSE.txt if needed
- Ensure all dependencies are compatible with BSD 3-Clause License

---

## Project Structure

### Directory Organization
```
D:\Opensim-TestGrid/           # Main project directory (existing OpenSim 0.9.3.0 structure)
├── addon-modules/             # OpenSim addon modules
├── bin/                       # Compiled executables and runtime files
├── doc/                       # OpenSim documentation
├── share/                     # Shared resources and assets
├── ThirdPartyLicenses/        # Third-party license information
├── CONTRIBUTORS.txt           # OpenSim contributors
├── LICENSE.txt                # BSD 3-Clause License
├── README.md                  # OpenSim readme
├── OpensimRefactor.md         # This project documentation
├── OpenSim/                   # Core OpenSim source code
├── OpenSim.sln                # Visual Studio solution file
├── testing/                   # Our testing framework (new)
│   ├── unit-tests/            # Automated test suite
│   ├── integration-tests/     # Grid functionality tests
│   └── performance-tests/     # Benchmarking and profiling
├── development/               # Development utilities (new)
│   ├── backup/                # Incremental development backups
│   ├── patches/               # Custom patches and modifications
│   └── migration-notes/       # Version upgrade documentation
├── tools/                     # Development tools (new)
│   ├── build-scripts/         # Automated build processes
│   ├── deployment/            # Deployment automation
│   └── monitoring/            # Performance monitoring tools
└── releases/                  # Milestone releases (new)
    └── archives/              # Historical releases
```

---

## Development Environment Setup

### Prerequisites
- Windows 11
- Visual Studio 2022 (Community or higher)
- .NET 8 SDK
- Git for Windows (with bash support)
- MySQL/MariaDB (for database backend)

### Initial Setup Commands

#### 1. Repository Initialization
```bash
# Create main project directory
mkdir "Opensim-Testgrid"
cd "Opensim-Testgrid"

# Initialize git repository
git init
git branch -m main

# Create .gitignore for OpenSim/C# projects
curl -o .gitignore https://raw.githubusercontent.com/github/gitignore/main/VisualStudio.gitignore

# Add OpenSim-specific ignores
echo "" >> .gitignore
echo "# OpenSim specific" >> .gitignore
echo "runtime/bin/OpenSim.log*" >> .gitignore
echo "runtime/bin/Robust.log*" >> .gitignore
echo "runtime/bin/ScriptEngines/" >> .gitignore
echo "runtime/bin/j2kDecodeCache/" >> .gitignore
echo "runtime/bin/assetcache/" >> .gitignore
echo "runtime/bin/UserAssets/" >> .gitignore
echo "runtime/logs/*" >> .gitignore
echo "runtime/data/cache/" >> .gitignore
echo "development/backup/*" >> .gitignore
```

#### 2. Initial Source Setup
```powershell
# Create directory structure
New-Item -ItemType Directory -Path "source", "development\current", "development\backup", "development\patches" -Force
New-Item -ItemType Directory -Path "testing\unit-tests", "testing\integration-tests", "testing\performance-tests" -Force
New-Item -ItemType Directory -Path "documentation\migration-notes", "documentation\testing-protocols", "documentation\configuration" -Force
New-Item -ItemType Directory -Path "tools\build-scripts", "tools\deployment", "tools\monitoring" -Force
New-Item -ItemType Directory -Path "runtime\bin", "runtime\config", "runtime\logs", "runtime\data" -Force
New-Item -ItemType Directory -Path "releases\archives" -Force

# Extract OpenSim 0.9.3.0 source zip to source directory
# (Manual step: Extract your opensim-0.9.3.0.zip to source/ folder)

# Copy pristine source to development/current for initial work
Copy-Item -Path "source\*" -Destination "development\current\" -Recurse -Force
```

#### 3. Baseline Commit
```bash
# Add pristine source to repository
git add source/
git commit -m "initial: Add pristine OpenSim 0.9.3.0 source from official zip

- Unmodified source from opensim-0.9.3.0.zip
- Establishes baseline for modernization work
- Serves as reference for all future changes"

# Add initial development copy
git add development/current/
git commit -m "setup: Initialize development workspace

- Copy of pristine source for active development  
- Isolated from production environment
- Ready for .NET 8 modernization work"

# Create initial development branch
git checkout -b develop
```

---

## Git Workflow and Version Control

### Branch Strategy
- **main**: Stable, tested code ready for production
- **develop**: Integration branch for feature development
- **feature/[feature-name]**: Individual feature branches
- **hotfix/[issue-name]**: Critical fixes
- **release/[version]**: Release preparation

### Commit Guidelines
```bash
# Feature development workflow
git checkout -b feature/nini-upgrade
# Make changes
git add .
git commit -m "feat: upgrade Nini.dll to version 1.1.0

- Updated Nini reference in all projects
- Resolved configuration compatibility issues
- Updated unit tests for new Nini API"

# Push feature branch
git push -u origin feature/nini-upgrade

# Merge workflow (after testing)
git checkout develop
git merge feature/nini-upgrade
git push origin develop
```

### Release Tagging
```bash
# Create release tags
git tag -a v0.9.3.1-beta -m "Beta release: .NET 8 modernization phase 1"
git push origin v0.9.3.1-beta
```

---

## Build and Testing Procedures

### Build Configuration

#### Visual Studio 2022 Setup
1. Open `OpenSim.sln` in Visual Studio 2022 (located in root directory)
2. Ensure target framework is set to `.NET 8.0`
3. Build Configuration: `Debug` for development, `Release` for testing
4. Platform: `x64` (recommended for performance)

#### Command Line Build
```powershell
# Navigate to project root directory
cd "D:\Opensim-TestGrid"

# Clean previous builds
dotnet clean OpenSim.sln

# Restore NuGet packages
dotnet restore OpenSim.sln

# Build entire solution
dotnet build OpenSim.sln --configuration Debug --verbosity normal

# For release builds
dotnet build OpenSim.sln --configuration Release --no-restore

# Verify build outputs are in bin/ directory
Get-ChildItem -Path "bin" -Filter "*.exe"
```

### Testing Framework

#### Unit Test Structure
```
testing/unit-tests/
├── Core/
│   ├── ConfigurationTests.cs     # Nini configuration tests
│   ├── DatabaseTests.cs          # Database layer tests
│   └── UtilityTests.cs          # Helper function tests
├── Regions/
│   ├── RegionCrossingTests.cs    # Region crossing logic
│   └── SceneObjectTests.cs      # Scene management tests
├── Scripts/
│   ├── LSLEngineTests.cs        # Script engine tests
│   └── ScriptPerformanceTests.cs # Performance benchmarks
└── Physics/
    └── PhysicsEngineTests.cs     # Physics simulation tests
```

#### Running Tests
```powershell
# Run all unit tests
dotnet test testing/unit-tests/ --logger "console;verbosity=detailed"

# Run specific test category
dotnet test testing/unit-tests/Core/ --filter "Category=Configuration"

# Run with coverage (requires coverlet)
dotnet test --collect:"XPlat Code Coverage"
```

#### Integration Testing Protocol
1. **Database Initialization**: Fresh test database for each run
2. **Grid Startup**: Automated grid startup with test configuration
3. **Avatar Login**: Test avatar creation and login process
4. **Region Operations**: Test region crossing, teleporting, object rezzing
5. **Script Execution**: Test LSL script compilation and execution
6. **Performance Metrics**: Collect timing and memory usage data

---

## Modernization Tasks and Progress Tracking

### Phase 1: Core Modernization (In Progress)
- [ ] **Nini.dll Upgrade**
  - [ ] Update to Nini 1.1.0
  - [ ] Test configuration loading
  - [ ] Verify backward compatibility
  - [ ] Update documentation

- [ ] **.NET 8 Compliance Audit**
  - [ ] Review all project files for .NET 8 target
  - [ ] Update deprecated API calls
  - [ ] Test with .NET 8 runtime optimizations
  - [ ] Benchmark performance improvements

- [ ] **Dependency Updates**
  - [ ] Audit all NuGet packages
  - [ ] Update to .NET 8 compatible versions
  - [ ] Test compatibility matrix
  - [ ] Document breaking changes

### Phase 2: Performance Improvements (Planned)
- [ ] **Region Crossing Enhancement**
  - [ ] Profile current crossing performance
  - [ ] Identify bottlenecks
  - [ ] Implement optimized crossing logic
  - [ ] Stress test with multiple avatars

- [ ] **Script Engine Optimization**
  - [ ] Profile LSL script execution
  - [ ] Implement compilation caching
  - [ ] Optimize memory management
  - [ ] Reduce crash scenarios

### Phase 3: Stability and Reliability (Future)
- [ ] **Memory Management**
  - [ ] Implement proper disposal patterns
  - [ ] Fix memory leaks
  - [ ] Optimize garbage collection

- [ ] **Physics Integration** (Future Phase)
  - [ ] Evaluate physics engine options
  - [ ] Plan integration approach
  - [ ] Performance testing

---

## Testing Protocols

### Pre-Commit Testing Checklist
1. **Code Compilation**: Verify clean build with no warnings
2. **Unit Tests**: All existing tests must pass
3. **Basic Functionality**: Grid starts and accepts connections
4. **Configuration**: Test with default and custom configurations
5. **Memory Check**: Brief memory usage verification

### Integration Testing Schedule
- **Daily**: Automated unit test runs
- **Weekly**: Full integration test suite
- **Before Merge**: Complete regression testing
- **Release Candidate**: Performance benchmarking

### Test Data Management
```powershell
# Create test database backup
mysqldump opensim_testgrid > "D:\Opensim-TestGrid\development\backup\testgrid_$(Get-Date -Format "yyyyMMdd_HHmmss").sql"

# Restore clean test environment
mysql opensim_testgrid < "D:\Opensim-TestGrid\development\backup\clean_testgrid.sql"

# Backup current source state before major changes
$backupName = "source_backup_$(Get-Date -Format "yyyyMMdd_HHmmss")"
Copy-Item -Path "D:\Opensim-TestGrid" -Destination "D:\Opensim-TestGrid\development\backup\$backupName" -Recurse -Exclude @("development", "testing", "tools", "releases", ".git")

# Clean temporary files
Remove-Item -Path "bin\OpenSim.log*" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "bin\ScriptEngines\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "bin\j2kDecodeCache\*" -Recurse -Force -ErrorAction SilentlyContinue
```

---

## Configuration Management

### Environment-Specific Configurations
- **OpenSim.ini**: Main test grid configuration (in bin/ directory)
- **Robust.ini**: Robust services configuration for test grid
- **GridCommon.ini**: Grid-wide settings
- **config-include/**: Modular configuration files

### Key Configuration Areas
1. **Database Connection**: Separate test database (opensim_testgrid)
2. **Network Settings**: Different ports for test grid (avoid conflicts)
3. **Asset Storage**: Test asset storage in bin/assetcache/
4. **Logging**: Enhanced logging for debugging
5. **Performance**: Tuned for development/testing environment

### Configuration File Locations
```
bin/                           # Runtime configuration location
├── OpenSim.ini               # Main grid configuration
├── Robust.ini                # Robust services configuration  
├── GridCommon.ini            # Grid-wide settings
└── config-include/           # Modular configuration files
    ├── storage/              # Database configurations
    ├── network/              # Network and port settings
    └── modules/              # Module-specific configs
```

---

## Monitoring and Performance Tracking

### Key Metrics to Track
- **Startup Time**: Grid initialization duration
- **Memory Usage**: Peak and average memory consumption
- **CPU Utilization**: Processing efficiency
- **Database Performance**: Query execution times
- **Network Latency**: Client connection response times
- **Script Execution**: LSL script performance metrics

### Performance Testing Commands
```powershell
# Memory profiling (using dotnet tools)
dotnet-dump collect -p [opensim-process-id]

# Performance counters
typeperf "\Process(OpenSim.exe)\Working Set" "\Process(OpenSim.exe)\% Processor Time"
```

---

## Troubleshooting and Common Issues

### Build Issues
- **Nini Compatibility**: Check for API changes in new version
- **Missing References**: Verify all NuGet packages restored
- **Target Framework**: Ensure all projects target .NET 8

### Runtime Issues
- **Configuration Loading**: Verify Nini configuration syntax
- **Database Connections**: Check connection strings and permissions
- **Port Conflicts**: Ensure test grid uses different ports

### Performance Issues
- **Memory Leaks**: Use memory profiling tools
- **CPU Spikes**: Profile with Visual Studio diagnostic tools
- **Script Timeouts**: Check script engine configuration

---

## Next Steps and Future Considerations

### Immediate Actions
1. Set up development environment structure
2. Create initial git repository
3. Upgrade Nini.dll to version 1.1.0
4. Establish testing framework

### Medium-term Goals
1. Complete .NET 8 modernization audit
2. Implement performance improvements
3. Establish automated testing pipeline
4. Document migration procedures

### Long-term Vision
1. Physics engine integration
2. Advanced performance optimizations
3. Modern development practices adoption
4. Community contribution preparation

---

## Session Handoff Information

When starting a new development session, provide this document along with:
- Current phase and task status
- Recent changes made
- Any blocking issues encountered
- Specific areas requiring focus
- Test results from previous session

This ensures continuity and maintains project momentum across development sessions.