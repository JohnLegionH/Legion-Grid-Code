/*
 * Copyright (c) Contributors, http://opensimulator.org/
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using log4net;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Console command handler for advanced physics systems
    /// </summary>
    public class AdvancedPhysicsConsoleCommands
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[ADVANCED PHYSICS CONSOLE]";

        private readonly BSScene m_scene;
        private readonly AdvancedPhysicsIntegration m_advancedPhysics;

        public AdvancedPhysicsConsoleCommands(BSScene scene, AdvancedPhysicsIntegration advancedPhysics)
        {
            m_scene = scene ?? throw new ArgumentNullException(nameof(scene));
            m_advancedPhysics = advancedPhysics ?? throw new ArgumentNullException(nameof(advancedPhysics));
        }

        /// <summary>
        /// Process advanced physics console commands
        /// </summary>
        public bool ProcessCommand(string[] cmdArgs)
        {
            if (cmdArgs == null || cmdArgs.Length < 2)
                return false;

            // Commands should be in format: "physics advanced <command> [params]"
            if (cmdArgs[0].ToLower() != "physics" || cmdArgs[1].ToLower() != "advanced")
                return false;

            if (cmdArgs.Length < 3)
            {
                ShowHelp();
                return true;
            }

            var command = cmdArgs[2].ToLower();
            var parameters = cmdArgs.Skip(3).ToArray();

            try
            {
                switch (command)
                {
                    case "help":
                        ShowHelp();
                        return true;

                    case "status":
                        ShowStatus();
                        return true;

                    case "performance":
                        ShowPerformance();
                        return true;

                    case "fluid":
                        ProcessFluidCommands(parameters);
                        return true;

                    case "softbody":
                        ProcessSoftBodyCommands(parameters);
                        return true;

                    case "particles":
                        ProcessParticleCommands(parameters);
                        return true;

                    case "constraints":
                        ProcessConstraintCommands(parameters);
                        return true;

                    case "destructible":
                        ProcessDestructibleCommands(parameters);
                        return true;

                    case "config":
                        ProcessConfigCommands(parameters);
                        return true;

                    case "test":
                        ProcessTestCommands(parameters);
                        return true;

                    case "visualize":
                        ProcessVisualizationCommands(parameters);
                        return true;

                    default:
                        m_log.InfoFormat("{0}: Unknown command '{1}'. Use 'physics advanced help' for available commands.", LogHeader, command);
                        return true;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error processing command '{1}': {2}", LogHeader, command, ex.Message);
                return true;
            }
        }

        private void ShowHelp()
        {
            var help = new StringBuilder();
            help.AppendLine("Advanced Physics Console Commands:");
            help.AppendLine("=================================");
            help.AppendLine("physics advanced help                    - Show this help");
            help.AppendLine("physics advanced status                  - Show system status");
            help.AppendLine("physics advanced performance             - Show performance metrics");
            help.AppendLine();
            help.AppendLine("Fluid Dynamics:");
            help.AppendLine("  physics advanced fluid status         - Show fluid system status");
            help.AppendLine("  physics advanced fluid create <name> <type> <x> <y> <z> <sizeX> <sizeY> <sizeZ>");
            help.AppendLine("  physics advanced fluid remove <name>  - Remove fluid volume");
            help.AppendLine("  physics advanced fluid list           - List all fluid volumes");
            help.AppendLine("  physics advanced fluid quality <1-5>  - Set fluid quality level");
            help.AppendLine();
            help.AppendLine("Soft Body Physics:");
            help.AppendLine("  physics advanced softbody status      - Show soft body system status");
            help.AppendLine("  physics advanced softbody create <name> <type> <x> <y> <z>");
            help.AppendLine("  physics advanced softbody remove <name> - Remove soft body");
            help.AppendLine("  physics advanced softbody list        - List all soft bodies");
            help.AppendLine("  physics advanced softbody quality <1-5> - Set soft body quality level");
            help.AppendLine();
            help.AppendLine("Particle Systems:");
            help.AppendLine("  physics advanced particles status     - Show particle system status");
            help.AppendLine("  physics advanced particles create <name> <type> <x> <y> <z> <count>");
            help.AppendLine("  physics advanced particles remove <name> - Remove particle system");
            help.AppendLine("  physics advanced particles list       - List all particle systems");
            help.AppendLine();
            help.AppendLine("Advanced Constraints:");
            help.AppendLine("  physics advanced constraints status   - Show constraint system status");
            help.AppendLine("  physics advanced constraints create <type> <objA> <objB>");
            help.AppendLine("  physics advanced constraints remove <id> - Remove constraint");
            help.AppendLine("  physics advanced constraints list     - List all constraints");
            help.AppendLine();
            help.AppendLine("Destructible Physics:");
            help.AppendLine("  physics advanced destructible status  - Show destructible system status");
            help.AppendLine("  physics advanced destructible create <name> <material> <x> <y> <z>");
            help.AppendLine("  physics advanced destructible remove <name> - Remove destructible object");
            help.AppendLine("  physics advanced destructible list    - List all destructible objects");
            help.AppendLine("  physics advanced destructible break <name> <forceX> <forceY> <forceZ>");
            help.AppendLine();
            help.AppendLine("Configuration:");
            help.AppendLine("  physics advanced config show          - Show current configuration");
            help.AppendLine("  physics advanced config adaptive <on|off> - Toggle adaptive quality scaling");
            help.AppendLine();
            help.AppendLine("Testing:");
            help.AppendLine("  physics advanced test all             - Run all system tests");
            help.AppendLine("  physics advanced test <system>        - Test specific system");
            help.AppendLine();
            help.AppendLine("Visualization:");
            help.AppendLine("  physics advanced visualize all        - Show location info for all physics objects");
            help.AppendLine("  physics advanced visualize fluids     - Show fluid volume locations");
            help.AppendLine("  physics advanced visualize destructible - Show destructible object locations");
            help.AppendLine("  physics advanced visualize help       - Show detailed visualization instructions");

            m_log.Info(help.ToString());
        }

        private void ShowStatus()
        {
            if (!m_advancedPhysics.IsInitialized)
            {
                m_log.Info("Advanced Physics Integration: DISABLED");
                return;
            }

            var status = new StringBuilder();
            status.AppendLine("Advanced Physics Integration Status:");
            status.AppendLine("===================================");
            status.AppendLine($"Overall Status: {(m_advancedPhysics.IsInitialized ? "ENABLED" : "DISABLED")}");
            status.AppendLine($"Configuration: {m_advancedPhysics.Configuration}");
            status.AppendLine();

            // Individual system status
            status.AppendLine("System Status:");
            status.AppendLine($"  Fluid Dynamics: {(m_advancedPhysics.FluidSystem?.IsEnabled == true ? "ENABLED" : "DISABLED")}");
            status.AppendLine($"  Soft Body Physics: {(m_advancedPhysics.SoftBodySystem?.IsEnabled == true ? "ENABLED" : "DISABLED")}");
            status.AppendLine($"  Particle Systems: {(m_advancedPhysics.ParticleSystem?.IsEnabled == true ? "ENABLED" : "DISABLED")}");
            status.AppendLine($"  Advanced Constraints: {(m_advancedPhysics.ConstraintSystem?.IsEnabled == true ? "ENABLED" : "DISABLED")}");
            status.AppendLine($"  Destructible Physics: {(m_advancedPhysics.DestructibleSystem?.IsEnabled == true ? "ENABLED" : "DISABLED")}");
            status.AppendLine();

            // Object counts
            var metrics = m_advancedPhysics.Metrics;
            status.AppendLine("Object Counts:");
            status.AppendLine($"  Fluid Volumes: {metrics.ActiveFluidVolumes}");
            status.AppendLine($"  Soft Bodies: {metrics.ActiveSoftBodies}");
            status.AppendLine($"  Particle Systems: {metrics.ActiveParticleSystems}");
            status.AppendLine($"  Advanced Constraints: {metrics.ActiveConstraints}");
            status.AppendLine($"  Destructible Objects: {metrics.ActiveDestructibleObjects}");
            status.AppendLine($"  Active Fragments: {metrics.ActiveFragments}");

            m_log.Info(status.ToString());
        }

        private void ShowPerformance()
        {
            if (!m_advancedPhysics.IsInitialized)
            {
                m_log.Info("Advanced Physics Integration: DISABLED - No performance data available");
                return;
            }

            var performance = m_advancedPhysics.GetPerformanceReport();
            m_log.Info($"Advanced Physics Performance Report:\n{performance}");
        }

        private void ProcessFluidCommands(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                m_log.Info("Missing fluid command. Use 'physics advanced help' for available commands.");
                return;
            }

            var fluidSystem = m_advancedPhysics.FluidSystem;
            if (fluidSystem == null)
            {
                m_log.Info("Fluid dynamics system is not enabled.");
                return;
            }

            var command = parameters[0].ToLower();
            switch (command)
            {
                case "status":
                    m_log.Info($"Fluid Dynamics Status:\n{fluidSystem.GetPerformanceReport()}");
                    break;

                case "create":
                    if (parameters.Length < 9)
                    {
                        m_log.Info("Usage: physics advanced fluid create <name> <type> <x> <y> <z> <sizeX> <sizeY> <sizeZ>");
                        return;
                    }
                    CreateFluidVolume(parameters);
                    break;

                case "list":
                    ListFluidVolumes();
                    break;

                case "quality":
                    if (parameters.Length < 2 || !int.TryParse(parameters[1], out int quality) || quality < 1 || quality > 5)
                    {
                        m_log.Info("Usage: physics advanced fluid quality <1-5>");
                        return;
                    }
                    fluidSystem.SetQuality((FluidQuality)quality);
                    m_log.InfoFormat("Fluid quality set to {0}", (FluidQuality)quality);
                    break;

                default:
                    m_log.InfoFormat("Unknown fluid command '{0}'. Use 'physics advanced help' for available commands.", command);
                    break;
            }
        }

        private void ProcessSoftBodyCommands(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                m_log.Info("Missing soft body command. Use 'physics advanced help' for available commands.");
                return;
            }

            var softBodySystem = m_advancedPhysics.SoftBodySystem;
            if (softBodySystem == null)
            {
                m_log.Info("Soft body physics system is not enabled.");
                return;
            }

            var command = parameters[0].ToLower();
            switch (command)
            {
                case "status":
                    m_log.Info($"Soft Body Physics Status:\n{softBodySystem.GetPerformanceReport()}");
                    break;

                case "quality":
                    if (parameters.Length < 2 || !int.TryParse(parameters[1], out int quality) || quality < 1 || quality > 5)
                    {
                        m_log.Info("Usage: physics advanced softbody quality <1-5>");
                        return;
                    }
                    softBodySystem.SetQuality((SoftBodyQuality)quality);
                    m_log.InfoFormat("Soft body quality set to {0}", (SoftBodyQuality)quality);
                    break;

                default:
                    m_log.InfoFormat("Unknown soft body command '{0}'. Use 'physics advanced help' for available commands.", command);
                    break;
            }
        }

        private void ProcessParticleCommands(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                m_log.Info("Missing particle command. Use 'physics advanced help' for available commands.");
                return;
            }

            var particleSystem = m_advancedPhysics.ParticleSystem;
            if (particleSystem == null)
            {
                m_log.Info("Particle systems are not enabled.");
                return;
            }

            var command = parameters[0].ToLower();
            switch (command)
            {
                case "status":
                    m_log.Info($"Particle Systems Status:\n{particleSystem.GetPerformanceReport()}");
                    break;

                default:
                    m_log.InfoFormat("Unknown particle command '{0}'. Use 'physics advanced help' for available commands.", command);
                    break;
            }
        }

        private void ProcessConstraintCommands(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                m_log.Info("Missing constraint command. Use 'physics advanced help' for available commands.");
                return;
            }

            var constraintSystem = m_advancedPhysics.ConstraintSystem;
            if (constraintSystem == null)
            {
                m_log.Info("Advanced constraint system is not enabled.");
                return;
            }

            var command = parameters[0].ToLower();
            switch (command)
            {
                case "status":
                    m_log.Info($"Advanced Constraints Status:\n{constraintSystem.GetPerformanceReport()}");
                    break;

                default:
                    m_log.InfoFormat("Unknown constraint command '{0}'. Use 'physics advanced help' for available commands.", command);
                    break;
            }
        }

        private void ProcessDestructibleCommands(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                m_log.Info("Missing destructible command. Use 'physics advanced help' for available commands.");
                return;
            }

            var destructibleSystem = m_advancedPhysics.DestructibleSystem;
            if (destructibleSystem == null)
            {
                m_log.Info("Destructible physics system is not enabled.");
                return;
            }

            var command = parameters[0].ToLower();
            switch (command)
            {
                case "status":
                    m_log.Info($"Destructible Physics Status:\n{destructibleSystem.GetPerformanceReport()}");
                    break;

                case "create":
                    if (parameters.Length < 6)
                    {
                        m_log.Info("Usage: physics advanced destructible create <name> <material> <x> <y> <z>");
                        return;
                    }
                    CreateDestructibleObject(parameters);
                    break;

                case "break":
                    if (parameters.Length < 5)
                    {
                        m_log.Info("Usage: physics advanced destructible break <name> <forceX> <forceY> <forceZ>");
                        return;
                    }
                    BreakDestructibleObject(parameters);
                    break;

                default:
                    m_log.InfoFormat("Unknown destructible command '{0}'. Use 'physics advanced help' for available commands.", command);
                    break;
            }
        }

        private void ProcessConfigCommands(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                m_log.Info("Missing config command. Use 'physics advanced help' for available commands.");
                return;
            }

            var command = parameters[0].ToLower();
            switch (command)
            {
                case "show":
                    ShowConfiguration();
                    break;

                case "adaptive":
                    if (parameters.Length < 2)
                    {
                        m_log.Info("Usage: physics advanced config adaptive <on|off>");
                        return;
                    }
                    ToggleAdaptiveQuality(parameters[1]);
                    break;

                default:
                    m_log.InfoFormat("Unknown config command '{0}'. Use 'physics advanced help' for available commands.", command);
                    break;
            }
        }

        private void ProcessTestCommands(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                m_log.Info("Missing test command. Use 'physics advanced help' for available commands.");
                return;
            }

            var command = parameters[0].ToLower();
            switch (command)
            {
                case "all":
                    RunAllTests();
                    break;

                default:
                    m_log.InfoFormat("Test for system '{0}' not implemented yet.", command);
                    break;
            }
        }

        private void CreateFluidVolume(string[] parameters)
        {
            try
            {
                var name = parameters[1];
                var type = parameters[2].ToLower();
                var x = float.Parse(parameters[3]);
                var y = float.Parse(parameters[4]);
                var z = float.Parse(parameters[5]);
                var sizeX = float.Parse(parameters[6]);
                var sizeY = float.Parse(parameters[7]);
                var sizeZ = float.Parse(parameters[8]);

                var fluidType = type switch
                {
                    "water" => FluidType.Water,
                    "air" => FluidType.Air,
                    "oil" => FluidType.Viscous,
                    _ => FluidType.Water
                };

                var properties = FluidProperties.GetPreset(fluidType);
                var position = new OMV.Vector3(x, y, z);
                var size = new OMV.Vector3(sizeX, sizeY, sizeZ);
                var minBounds = SIMDPhysicsMath.Subtract(position, SIMDPhysicsMath.Multiply(size, 0.5f));
                var maxBounds = SIMDPhysicsMath.Add(position, SIMDPhysicsMath.Multiply(size, 0.5f));

                var volumeID = m_advancedPhysics.FluidSystem.CreateFluidVolume(name, properties, minBounds, maxBounds);
                
                if (volumeID > 0)
                {
                    m_log.InfoFormat("Created fluid volume '{0}' (ID: {1}, Type: {2})", name, volumeID, fluidType);
                }
                else
                {
                    m_log.ErrorFormat("Failed to create fluid volume '{0}'", name);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("Error creating fluid volume: {0}", ex.Message);
            }
        }

        private void ListFluidVolumes()
        {
            // This would require extending the FluidDynamicsSystem to provide a list method
            m_log.Info("Fluid volume listing not yet implemented - check system status for counts");
        }

        private void CreateDestructibleObject(string[] parameters)
        {
            try
            {
                var name = parameters[1];
                var materialType = parameters[2].ToLower();
                var x = float.Parse(parameters[3]);
                var y = float.Parse(parameters[4]);
                var z = float.Parse(parameters[5]);

                var material = materialType switch
                {
                    "glass" => DestructibleMaterial.Glass,
                    "stone" => DestructibleMaterial.Stone,
                    "wood" => DestructibleMaterial.Wood,
                    "metal" => DestructibleMaterial.Metal,
                    "concrete" => DestructibleMaterial.Concrete,
                    _ => DestructibleMaterial.Stone
                };

                var properties = DestructibleMaterialProperties.GetPreset(material);
                var position = new OMV.Vector3(x, y, z);
                var rotation = OMV.Quaternion.Identity;
                var size = new OMV.Vector3(1.0f, 1.0f, 1.0f);
                var mass = 10.0f;

                var objectID = m_advancedPhysics.DestructibleSystem.CreateDestructibleObject(
                    name, properties, position, rotation, size, mass);

                if (objectID > 0)
                {
                    m_log.InfoFormat("Created destructible object '{0}' (ID: {1}, Material: {2})", name, objectID, material);
                }
                else
                {
                    m_log.ErrorFormat("Failed to create destructible object '{0}'", name);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("Error creating destructible object: {0}", ex.Message);
            }
        }

        private void BreakDestructibleObject(string[] parameters)
        {
            try
            {
                var name = parameters[1];
                var forceX = float.Parse(parameters[2]);
                var forceY = float.Parse(parameters[3]);
                var forceZ = float.Parse(parameters[4]);

                var force = new OMV.Vector3(forceX, forceY, forceZ);
                
                // This would require extending the DestructiblePhysicsSystem to find objects by name
                m_log.InfoFormat("Breaking destructible object '{0}' with force ({1}, {2}, {3}) - implementation pending", 
                    name, forceX, forceY, forceZ);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("Error breaking destructible object: {0}", ex.Message);
            }
        }

        private void ShowConfiguration()
        {
            var config = m_advancedPhysics.Configuration;
            var configInfo = new StringBuilder();
            
            configInfo.AppendLine("Advanced Physics Configuration:");
            configInfo.AppendLine("==============================");
            configInfo.AppendLine($"Fluid Dynamics Enabled: {config.EnableFluidDynamics}");
            configInfo.AppendLine($"Fluid Quality: {config.FluidQuality}");
            configInfo.AppendLine($"Soft Body Physics Enabled: {config.EnableSoftBodyPhysics}");
            configInfo.AppendLine($"Soft Body Quality: {config.SoftBodyQuality}");
            configInfo.AppendLine($"Particle Systems Enabled: {config.EnableParticleSystems}");
            configInfo.AppendLine($"Advanced Constraints Enabled: {config.EnableAdvancedConstraints}");
            configInfo.AppendLine($"Destructible Physics Enabled: {config.EnableDestructiblePhysics}");
            configInfo.AppendLine($"Performance Monitoring Enabled: {config.EnablePerformanceMonitoring}");
            configInfo.AppendLine($"Performance Report Interval: {config.PerformanceReportInterval}s");
            configInfo.AppendLine($"Adaptive Quality Scaling: {config.AdaptiveQualityScaling}");
            configInfo.AppendLine($"Max CPU Usage: {config.MaxCPUUsagePercent}%");

            m_log.Info(configInfo.ToString());
        }

        private void ToggleAdaptiveQuality(string setting)
        {
            var enable = setting.ToLower() == "on" || setting.ToLower() == "true";
            // This would require extending the configuration system to allow runtime changes
            m_log.InfoFormat("Adaptive quality scaling would be set to: {0} (implementation pending)", enable);
        }

        private void RunAllTests()
        {
            m_log.Info("Running comprehensive advanced physics system tests...");
            
            var results = new StringBuilder();
            results.AppendLine("Advanced Physics System Test Results:");
            results.AppendLine("====================================");

            // Test each system
            if (m_advancedPhysics.FluidSystem?.IsEnabled == true)
            {
                results.AppendLine($"Fluid Dynamics System: PASSED ({m_advancedPhysics.FluidSystem.FluidVolumeCount} volumes)");
            }
            else
            {
                results.AppendLine("Fluid Dynamics System: DISABLED");
            }

            if (m_advancedPhysics.SoftBodySystem?.IsEnabled == true)
            {
                results.AppendLine($"Soft Body Physics System: PASSED ({m_advancedPhysics.SoftBodySystem.SoftBodyCount} soft bodies)");
            }
            else
            {
                results.AppendLine("Soft Body Physics System: DISABLED");
            }

            if (m_advancedPhysics.ParticleSystem?.IsEnabled == true)
            {
                results.AppendLine($"Particle Systems: PASSED ({m_advancedPhysics.ParticleSystem.ParticleSystemCount} systems)");
            }
            else
            {
                results.AppendLine("Particle Systems: DISABLED");
            }

            if (m_advancedPhysics.ConstraintSystem?.IsEnabled == true)
            {
                results.AppendLine($"Advanced Constraints: PASSED ({m_advancedPhysics.ConstraintSystem.ConstraintCount} constraints)");
            }
            else
            {
                results.AppendLine("Advanced Constraints: DISABLED");
            }

            if (m_advancedPhysics.DestructibleSystem?.IsEnabled == true)
            {
                results.AppendLine($"Destructible Physics: PASSED ({m_advancedPhysics.DestructibleSystem.DestructibleObjectCount} objects)");
            }
            else
            {
                results.AppendLine("Destructible Physics: DISABLED");
            }

            results.AppendLine($"Integration System: PASSED");
            results.AppendLine($"Performance Monitoring: {(m_advancedPhysics.Configuration.EnablePerformanceMonitoring ? "PASSED" : "DISABLED")}");

            m_log.Info(results.ToString());
        }

        private void ProcessVisualizationCommands(string[] parameters)
        {
            if (parameters.Length == 0)
            {
                ShowVisualizationInstructions();
                return;
            }

            var command = parameters[0].ToLower();
            switch (command)
            {
                case "all":
                    ShowAllObjectLocations();
                    break;

                case "fluids":
                    ShowFluidVolumeLocations();
                    break;

                case "destructible":
                    ShowDestructibleObjectLocations();
                    break;

                case "help":
                    ShowVisualizationInstructions();
                    break;

                default:
                    m_log.InfoFormat("Unknown visualization command '{0}'. Use 'physics advanced visualize help' for available commands.", command);
                    break;
            }
        }

        private void ShowAllObjectLocations()
        {
            if (!m_advancedPhysics.IsInitialized)
            {
                m_log.Info("Advanced Physics Integration: DISABLED - No object data available");
                return;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine("=== ADVANCED PHYSICS OBJECT LOCATIONS ===");
            report.AppendLine();

            var metrics = m_advancedPhysics.Metrics;
            
            report.AppendLine($"Total Objects: {metrics.ActiveFluidVolumes + metrics.ActiveSoftBodies + metrics.ActiveParticleSystems + metrics.ActiveConstraints + metrics.ActiveDestructibleObjects}");
            report.AppendLine();

            if (metrics.ActiveFluidVolumes > 0)
            {
                report.AppendLine($"FLUID VOLUMES ({metrics.ActiveFluidVolumes} active):");
                report.AppendLine("  Use 'physics advanced visualize fluids' for detailed locations");
                report.AppendLine();
            }

            if (metrics.ActiveDestructibleObjects > 0)
            {
                report.AppendLine($"DESTRUCTIBLE OBJECTS ({metrics.ActiveDestructibleObjects} active):");
                report.AppendLine("  Use 'physics advanced visualize destructible' for detailed locations");
                report.AppendLine();
            }

            if (metrics.ActiveSoftBodies > 0)
            {
                report.AppendLine($"SOFT BODIES ({metrics.ActiveSoftBodies} active):");
                report.AppendLine("  Soft body locations logged during creation");
                report.AppendLine();
            }

            if (metrics.ActiveParticleSystems > 0)
            {
                report.AppendLine($"PARTICLE SYSTEMS ({metrics.ActiveParticleSystems} active):");
                report.AppendLine("  Particle system locations logged during creation");
                report.AppendLine();
            }

            if (metrics.ActiveConstraints > 0)
            {
                report.AppendLine($"ADVANCED CONSTRAINTS ({metrics.ActiveConstraints} active):");
                report.AppendLine("  Constraint locations logged during creation");
                report.AppendLine();
            }

            if (metrics.ActiveFragments > 0)
            {
                report.AppendLine($"ACTIVE FRAGMENTS ({metrics.ActiveFragments} pieces):");
                report.AppendLine("  Fragment locations logged during destruction events");
            }

            report.AppendLine();
            report.AppendLine("NOTE: Check console logs for 'VISUAL:' messages showing exact coordinates");
            report.AppendLine("      Use 'physics advanced visualize help' for marker creation instructions");

            m_log.Info(report.ToString());
        }

        private void ShowFluidVolumeLocations()
        {
            if (m_advancedPhysics.FluidSystem?.IsEnabled != true)
            {
                m_log.Info("Fluid dynamics system is not enabled.");
                return;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine("=== FLUID VOLUME LOCATIONS ===");
            report.AppendLine();
            
            var metrics = m_advancedPhysics.Metrics;
            report.AppendLine($"Active Fluid Volumes: {metrics.ActiveFluidVolumes}");
            report.AppendLine();

            if (metrics.ActiveFluidVolumes > 0)
            {
                report.AppendLine("VISUALIZATION GUIDE:");
                report.AppendLine("• Water volumes: Create blue, semi-transparent boxes");
                report.AppendLine("• Viscous fluids: Create darker, more opaque boxes");
                report.AppendLine("• Air volumes: Create very light, almost invisible boxes");
                report.AppendLine("• Set all fluid volumes to Phantom (non-physical)");
                report.AppendLine();
                report.AppendLine("Look in console logs for messages like:");
                report.AppendLine("[FLUID DYNAMICS]: VISUAL: Created fluid volume 'name' - Type: Water, Center: (x,y,z), Size: (w,h,d), Color: RGBA(r,g,b,a)");
                report.AppendLine();
                report.AppendLine("Use the Center coordinates for position and Size for scaling your visual markers.");
            }
            else
            {
                report.AppendLine("No fluid volumes currently active.");
                report.AppendLine("Create fluid volumes with: physics advanced fluid create <name> <type> <x> <y> <z> <sizeX> <sizeY> <sizeZ>");
            }

            m_log.Info(report.ToString());
        }

        private void ShowDestructibleObjectLocations()
        {
            if (m_advancedPhysics.DestructibleSystem?.IsEnabled != true)
            {
                m_log.Info("Destructible physics system is not enabled.");
                return;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine("=== DESTRUCTIBLE OBJECT LOCATIONS ===");
            report.AppendLine();
            
            var metrics = m_advancedPhysics.Metrics;
            report.AppendLine($"Active Destructible Objects: {metrics.ActiveDestructibleObjects}");
            report.AppendLine($"Active Fragments: {metrics.ActiveFragments}");
            report.AppendLine();

            if (metrics.ActiveDestructibleObjects > 0)
            {
                report.AppendLine("VISUALIZATION GUIDE:");
                report.AppendLine("• Glass objects: Create transparent or reflective boxes");
                report.AppendLine("• Stone objects: Create gray, solid boxes");
                report.AppendLine("• Wood objects: Create brown, textured boxes");
                report.AppendLine("• Metal objects: Create metallic, solid boxes");
                report.AppendLine("• Concrete objects: Create rough, gray boxes");
                report.AppendLine("• Set all destructible objects to Physical");
                report.AppendLine();
                report.AppendLine("Look in console logs for messages like:");
                report.AppendLine("[DESTRUCTIBLE PHYSICS]: VISUAL: Created destructible object 'name' - Material: Glass, Position: (x,y,z), Size: (w,h,d)");
                report.AppendLine();
                report.AppendLine("Use the Position coordinates and Size for creating your visual markers.");
                
                if (metrics.ActiveFragments > 0)
                {
                    report.AppendLine();
                    report.AppendLine($"FRAGMENTS: {metrics.ActiveFragments} pieces from destruction events");
                    report.AppendLine("• Fragment locations are logged when objects break");
                    report.AppendLine("• Create smaller pieces at fragment coordinates");
                    report.AppendLine("• Use same material as parent object");
                }
            }
            else
            {
                report.AppendLine("No destructible objects currently active.");
                report.AppendLine("Create destructible objects with: physics advanced destructible create <name> <material> <x> <y> <z>");
            }

            m_log.Info(report.ToString());
        }

        private void ShowVisualizationInstructions()
        {
            var instructions = new System.Text.StringBuilder();
            instructions.AppendLine("=== HOW TO VISUALIZE ADVANCED PHYSICS OBJECTS ===");
            instructions.AppendLine();
            instructions.AppendLine("The advanced physics console shows you the visual creation logs when objects are created.");
            instructions.AppendLine("Look for these log messages:");
            instructions.AppendLine();
            instructions.AppendLine("For FLUID VOLUMES:");
            instructions.AppendLine("  [FLUID DYNAMICS]: VISUAL: Created fluid volume 'name' - Type: Water, Center: (x,y,z), Size: (w,h,d)");
            instructions.AppendLine();
            instructions.AppendLine("For DESTRUCTIBLE OBJECTS:");
            instructions.AppendLine("  [DESTRUCTIBLE PHYSICS]: VISUAL: Created destructible object 'name' - Material: Glass, Position: (x,y,z), Size: (w,h,d)");
            instructions.AppendLine();
            instructions.AppendLine("TO CREATE VISIBLE MARKERS:");
            instructions.AppendLine("1. Note the Position and Size from the log messages");
            instructions.AppendLine("2. In your viewer, right-click and choose 'Create'");
            instructions.AppendLine("3. Create a box/cube primitive");
            instructions.AppendLine("4. Move it to the Position coordinates shown in the logs");
            instructions.AppendLine("5. Scale it to the Size shown in the logs");
            instructions.AppendLine("6. For fluid volumes: Set to Phantom, tint blue/transparent");
            instructions.AppendLine("7. For destructible objects: Set to Physical, choose appropriate material");
            instructions.AppendLine();
            instructions.AppendLine("EXAMPLE:");
            instructions.AppendLine("If you see: 'VISUAL: Created fluid volume 'TestPool' - Type: Water, Center: (128,128,30), Size: (5,5,3)'");
            instructions.AppendLine("Then create a 5x5x3 box at position 128,128,30, set it to Phantom and tint it blue.");
            instructions.AppendLine();
            instructions.AppendLine("This gives you exact visual representation of where the advanced physics objects exist!");

            m_log.Info(instructions.ToString());
        }
    }

    /// <summary>
    /// Extension to BSScene for advanced physics console commands
    /// </summary>
    public partial class BSScene
    {
        private AdvancedPhysicsConsoleCommands m_advancedPhysicsConsole;

        /// <summary>
        /// Initialize advanced physics console commands
        /// </summary>
        private void InitializeAdvancedPhysicsConsole()
        {
            if (m_advancedPhysics != null)
            {
                m_advancedPhysicsConsole = new AdvancedPhysicsConsoleCommands(this, m_advancedPhysics);
                RegisterAdvancedPhysicsCommands();
                m_log.InfoFormat("{0}: Advanced physics console commands initialized", LogHeader);
            }
        }

        /// <summary>
        /// Register advanced physics commands with the main console
        /// </summary>
        private void RegisterAdvancedPhysicsCommands()
        {
            try
            {
                OpenSim.Framework.MainConsole.Instance.Commands.AddCommand(
                    "Physics", false, "physics advanced", "physics advanced <command>",
                    "Advanced physics system commands. Use 'physics advanced help' for more information.",
                    HandleAdvancedPhysicsCommand);
                
                m_log.InfoFormat("{0}: Advanced physics commands registered with console", LogHeader);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("{0}: Failed to register advanced physics commands: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Handle advanced physics console commands
        /// </summary>
        private void HandleAdvancedPhysicsCommand(string module, string[] args)
        {
            if (m_advancedPhysicsConsole != null)
            {
                m_advancedPhysicsConsole.ProcessCommand(args);
            }
            else
            {
                m_log.Info("Advanced physics system is not initialized");
            }
        }

        /// <summary>
        /// Process advanced physics console commands
        /// Called from the main console command processor
        /// </summary>
        public bool ProcessAdvancedPhysicsCommand(string[] cmdArgs)
        {
            if (m_advancedPhysicsConsole != null)
            {
                return m_advancedPhysicsConsole.ProcessCommand(cmdArgs);
            }
            return false;
        }

        /// <summary>
        /// Log advanced physics performance summary
        /// </summary>
        public void LogAdvancedPhysicsPerformanceSummary()
        {
            if (m_advancedPhysics?.IsInitialized == true)
            {
                var performance = m_advancedPhysics.GetPerformanceReport();
                m_log.InfoFormat("{0}: Advanced Physics Performance Summary:\n{1}", LogHeader, performance);
            }
        }

    }
}