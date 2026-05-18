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
using System.Reflection;
using log4net;
using Nini.Config;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Configuration management for the destruction physics system
    /// Provides centralized configuration with validation and defaults
    /// </summary>
    public class DestructionSystemConfig
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION CONFIG]";

        #region Core Settings
        
        /// <summary>
        /// Master enable/disable for the destruction system
        /// </summary>
        public bool Enabled { get; private set; } = false;
        
        /// <summary>
        /// Enable admin console commands
        /// </summary>
        public bool AdminCommandsEnabled { get; private set; } = true;
        
        /// <summary>
        /// Enable LSL destruction extensions
        /// </summary>
        public bool LSLExtensionsEnabled { get; private set; } = true;
        
        #endregion

        #region Performance Settings
        
        /// <summary>
        /// Update rate in Hz (1-60)
        /// </summary>
        public float UpdateRate { get; private set; } = 10.0f;
        
        /// <summary>
        /// Maximum destructible objects per region (1-1000)
        /// </summary>
        public int MaxDestructibleObjects { get; private set; } = 100;
        
        /// <summary>
        /// Maximum fragments per object (1-50)
        /// </summary>
        public int MaxFragmentsPerObject { get; private set; } = 15;
        
        /// <summary>
        /// Maximum total fragments in region (1-1000)
        /// </summary>
        public int MaxTotalFragments { get; private set; } = 200;
        
        /// <summary>
        /// Fragment lifetime in seconds (1-300)
        /// </summary>
        public float FragmentLifetime { get; private set; } = 60.0f;
        
        /// <summary>
        /// Enable adaptive performance scaling
        /// </summary>
        public bool EnableAdaptivePerformance { get; private set; } = true;
        
        /// <summary>
        /// Target frame time in milliseconds (10-100)
        /// </summary>
        public float TargetFrameTime { get; private set; } = 22.0f;
        
        /// <summary>
        /// Performance batch size for processing (1-20)
        /// </summary>
        public int PerformanceBatchSize { get; private set; } = 4;
        
        #endregion

        #region Visual Effects Settings
        
        /// <summary>
        /// Enable particle effects
        /// </summary>
        public bool EnableParticleEffects { get; private set; } = true;
        
        /// <summary>
        /// Particle effect intensity multiplier (0.0-3.0)
        /// </summary>
        public float ParticleIntensity { get; private set; } = 1.0f;
        
        /// <summary>
        /// Enable crack animations
        /// </summary>
        public bool EnableCrackAnimations { get; private set; } = true;
        
        /// <summary>
        /// Crack animation intensity multiplier (0.0-3.0)
        /// </summary>
        public float CrackIntensity { get; private set; } = 1.0f;
        
        /// <summary>
        /// Enable environmental effects (dust clouds, etc)
        /// </summary>
        public bool EnableEnvironmentalEffects { get; private set; } = true;
        
        /// <summary>
        /// Environmental effects duration multiplier (0.1-5.0)
        /// </summary>
        public float EnvironmentalEffectsDuration { get; private set; } = 1.0f;
        
        #endregion

        #region Audio Settings
        
        /// <summary>
        /// Enable sound effects
        /// </summary>
        public bool EnableSoundEffects { get; private set; } = true;
        
        /// <summary>
        /// Sound effect intensity multiplier (0.0-3.0)
        /// </summary>
        public float SoundIntensity { get; private set; } = 1.0f;
        
        /// <summary>
        /// Maximum simultaneous sounds (1-20)
        /// </summary>
        public int MaxSimultaneousSounds { get; private set; } = 10;
        
        #endregion

        #region Physics Settings
        
        /// <summary>
        /// Force calculation multiplier (0.1-10.0)
        /// </summary>
        public float ForceMultiplier { get; private set; } = 1.0f;
        
        /// <summary>
        /// Enable chain reactions
        /// </summary>
        public bool EnableChainReactions { get; private set; } = false;
        
        /// <summary>
        /// Chain reaction radius in meters (1.0-20.0)
        /// </summary>
        public float ChainReactionRadius { get; private set; } = 5.0f;
        
        /// <summary>
        /// Chain reaction force transfer efficiency (0.1-1.0)
        /// </summary>
        public float ChainReactionForceMultiplier { get; private set; } = 0.7f;
        
        /// <summary>
        /// Maximum chain reaction depth (1-10)
        /// </summary>
        public int MaxChainReactionDepth { get; private set; } = 3;
        
        /// <summary>
        /// Enable structural integrity simulation
        /// </summary>
        public bool EnableStructuralIntegrity { get; private set; } = true;
        
        /// <summary>
        /// Structural failure threshold (0.1-1.0)
        /// </summary>
        public float StructuralIntegrityThreshold { get; private set; } = 0.3f;
        
        #endregion

        #region Persistence Settings
        
        /// <summary>
        /// Enable persistent destruction state
        /// </summary>
        public bool EnablePersistence { get; private set; } = true;
        
        /// <summary>
        /// Persistence save interval in seconds (30-600)
        /// </summary>
        public float PersistenceSaveInterval { get; private set; } = 300.0f;
        
        /// <summary>
        /// Maximum destruction records to keep (100-10000)
        /// </summary>
        public int MaxDestructionRecords { get; private set; } = 1000;
        
        /// <summary>
        /// Enable automatic repair system
        /// </summary>
        public bool EnableRepairSystem { get; private set; } = true;
        
        /// <summary>
        /// Default repair time in seconds (10-3600)
        /// </summary>
        public float DefaultRepairTime { get; private set; } = 300.0f;
        
        #endregion

        #region Debugging Settings
        
        /// <summary>
        /// Enable detailed destruction logging
        /// </summary>
        public bool EnableDestructionLogging { get; private set; } = false;
        
        /// <summary>
        /// Enable performance profiling
        /// </summary>
        public bool EnablePerformanceProfiling { get; private set; } = true;
        
        /// <summary>
        /// Performance report interval in seconds (10-600)
        /// </summary>
        public float PerformanceReportInterval { get; private set; } = 60.0f;
        
        #endregion

        /// <summary>
        /// Load configuration from OpenSim config source
        /// </summary>
        public static DestructionSystemConfig LoadConfig(IConfigSource configSource)
        {
            var config = new DestructionSystemConfig();
            
            // Try to load from dedicated [DestructionSystem] section
            IConfig destructionConfig = configSource.Configs["DestructionSystem"];
            
            // Fall back to [BulletSim] section for backward compatibility
            if (destructionConfig == null)
            {
                IConfig bulletConfig = configSource.Configs["BulletSim"];
                if (bulletConfig != null)
                {
                    m_log.InfoFormat("{0}: No [DestructionSystem] section found, using [BulletSim] settings", LogHeader);
                    config.LoadFromBulletSimConfig(bulletConfig);
                }
                else
                {
                    m_log.WarnFormat("{0}: No configuration found, using defaults", LogHeader);
                }
            }
            else
            {
                config.LoadFromConfig(destructionConfig);
            }
            
            config.ValidateSettings();
            config.LogConfiguration();
            
            return config;
        }

        /// <summary>
        /// Load settings from dedicated [DestructionSystem] section
        /// </summary>
        private void LoadFromConfig(IConfig config)
        {
            // Core Settings
            Enabled = config.GetBoolean("Enabled", Enabled);
            AdminCommandsEnabled = config.GetBoolean("AdminCommandsEnabled", AdminCommandsEnabled);
            LSLExtensionsEnabled = config.GetBoolean("LSLExtensionsEnabled", LSLExtensionsEnabled);
            
            // Performance Settings
            UpdateRate = config.GetFloat("UpdateRate", UpdateRate);
            MaxDestructibleObjects = config.GetInt("MaxDestructibleObjects", MaxDestructibleObjects);
            MaxFragmentsPerObject = config.GetInt("MaxFragmentsPerObject", MaxFragmentsPerObject);
            MaxTotalFragments = config.GetInt("MaxTotalFragments", MaxTotalFragments);
            FragmentLifetime = config.GetFloat("FragmentLifetime", FragmentLifetime);
            EnableAdaptivePerformance = config.GetBoolean("EnableAdaptivePerformance", EnableAdaptivePerformance);
            TargetFrameTime = config.GetFloat("TargetFrameTime", TargetFrameTime);
            PerformanceBatchSize = config.GetInt("PerformanceBatchSize", PerformanceBatchSize);
            
            // Visual Effects Settings
            EnableParticleEffects = config.GetBoolean("EnableParticleEffects", EnableParticleEffects);
            ParticleIntensity = config.GetFloat("ParticleIntensity", ParticleIntensity);
            EnableCrackAnimations = config.GetBoolean("EnableCrackAnimations", EnableCrackAnimations);
            CrackIntensity = config.GetFloat("CrackIntensity", CrackIntensity);
            EnableEnvironmentalEffects = config.GetBoolean("EnableEnvironmentalEffects", EnableEnvironmentalEffects);
            EnvironmentalEffectsDuration = config.GetFloat("EnvironmentalEffectsDuration", EnvironmentalEffectsDuration);
            
            // Audio Settings
            EnableSoundEffects = config.GetBoolean("EnableSoundEffects", EnableSoundEffects);
            SoundIntensity = config.GetFloat("SoundIntensity", SoundIntensity);
            MaxSimultaneousSounds = config.GetInt("MaxSimultaneousSounds", MaxSimultaneousSounds);
            
            // Physics Settings
            ForceMultiplier = config.GetFloat("ForceMultiplier", ForceMultiplier);
            EnableChainReactions = config.GetBoolean("EnableChainReactions", EnableChainReactions);
            ChainReactionRadius = config.GetFloat("ChainReactionRadius", ChainReactionRadius);
            ChainReactionForceMultiplier = config.GetFloat("ChainReactionForceMultiplier", ChainReactionForceMultiplier);
            MaxChainReactionDepth = config.GetInt("MaxChainReactionDepth", MaxChainReactionDepth);
            EnableStructuralIntegrity = config.GetBoolean("EnableStructuralIntegrity", EnableStructuralIntegrity);
            StructuralIntegrityThreshold = config.GetFloat("StructuralIntegrityThreshold", StructuralIntegrityThreshold);
            
            // Persistence Settings
            EnablePersistence = config.GetBoolean("EnablePersistence", EnablePersistence);
            PersistenceSaveInterval = config.GetFloat("PersistenceSaveInterval", PersistenceSaveInterval);
            MaxDestructionRecords = config.GetInt("MaxDestructionRecords", MaxDestructionRecords);
            EnableRepairSystem = config.GetBoolean("EnableRepairSystem", EnableRepairSystem);
            DefaultRepairTime = config.GetFloat("DefaultRepairTime", DefaultRepairTime);
            
            // Debugging Settings
            EnableDestructionLogging = config.GetBoolean("EnableDestructionLogging", EnableDestructionLogging);
            EnablePerformanceProfiling = config.GetBoolean("EnablePerformanceProfiling", EnablePerformanceProfiling);
            PerformanceReportInterval = config.GetFloat("PerformanceReportInterval", PerformanceReportInterval);
        }

        /// <summary>
        /// Load settings from [BulletSim] section for backward compatibility
        /// </summary>
        private void LoadFromBulletSimConfig(IConfig config)
        {
            // Map old BSParam settings to new config
            Enabled = config.GetBoolean("EnableDestructiblePhysics", false);
            AdminCommandsEnabled = config.GetBoolean("AdminControlsEnabled", true);
            EnableParticleEffects = config.GetBoolean("EnableDestructionParticles", true);
            EnableSoundEffects = config.GetBoolean("EnableDestructionSounds", true);
            EnableChainReactions = config.GetBoolean("EnableChainReactions", false);
            EnableEnvironmentalEffects = config.GetBoolean("EnableEnvironmentalEffects", true);
            
            // Load any other backward-compatible settings
            ParticleIntensity = config.GetFloat("DestructionParticleIntensity", 1.0f);
            SoundIntensity = config.GetFloat("DestructionSoundIntensity", 1.0f);
            ForceMultiplier = config.GetFloat("DestructionForceMultiplier", 1.0f);
            ChainReactionRadius = config.GetFloat("ChainReactionRadius", 5.0f);
            StructuralIntegrityThreshold = config.GetFloat("StructuralIntegrityThreshold", 0.3f);
        }

        /// <summary>
        /// Validate all settings and clamp to valid ranges
        /// </summary>
        private void ValidateSettings()
        {
            // Performance Settings
            UpdateRate = Math.Max(1.0f, Math.Min(60.0f, UpdateRate));
            MaxDestructibleObjects = Math.Max(1, Math.Min(1000, MaxDestructibleObjects));
            MaxFragmentsPerObject = Math.Max(1, Math.Min(50, MaxFragmentsPerObject));
            MaxTotalFragments = Math.Max(1, Math.Min(1000, MaxTotalFragments));
            FragmentLifetime = Math.Max(1.0f, Math.Min(300.0f, FragmentLifetime));
            TargetFrameTime = Math.Max(10.0f, Math.Min(100.0f, TargetFrameTime));
            PerformanceBatchSize = Math.Max(1, Math.Min(20, PerformanceBatchSize));
            
            // Visual Effects Settings
            ParticleIntensity = Math.Max(0.0f, Math.Min(3.0f, ParticleIntensity));
            CrackIntensity = Math.Max(0.0f, Math.Min(3.0f, CrackIntensity));
            EnvironmentalEffectsDuration = Math.Max(0.1f, Math.Min(5.0f, EnvironmentalEffectsDuration));
            
            // Audio Settings
            SoundIntensity = Math.Max(0.0f, Math.Min(3.0f, SoundIntensity));
            MaxSimultaneousSounds = Math.Max(1, Math.Min(20, MaxSimultaneousSounds));
            
            // Physics Settings
            ForceMultiplier = Math.Max(0.1f, Math.Min(10.0f, ForceMultiplier));
            ChainReactionRadius = Math.Max(1.0f, Math.Min(20.0f, ChainReactionRadius));
            ChainReactionForceMultiplier = Math.Max(0.1f, Math.Min(1.0f, ChainReactionForceMultiplier));
            MaxChainReactionDepth = Math.Max(1, Math.Min(10, MaxChainReactionDepth));
            StructuralIntegrityThreshold = Math.Max(0.1f, Math.Min(1.0f, StructuralIntegrityThreshold));
            
            // Persistence Settings
            PersistenceSaveInterval = Math.Max(30.0f, Math.Min(600.0f, PersistenceSaveInterval));
            MaxDestructionRecords = Math.Max(100, Math.Min(10000, MaxDestructionRecords));
            DefaultRepairTime = Math.Max(10.0f, Math.Min(3600.0f, DefaultRepairTime));
            
            // Debugging Settings
            PerformanceReportInterval = Math.Max(10.0f, Math.Min(600.0f, PerformanceReportInterval));
            
            // Logical validations
            if (MaxTotalFragments < MaxFragmentsPerObject)
            {
                m_log.WarnFormat("{0}: MaxTotalFragments ({1}) is less than MaxFragmentsPerObject ({2}), adjusting",
                    LogHeader, MaxTotalFragments, MaxFragmentsPerObject);
                MaxTotalFragments = MaxFragmentsPerObject * 2;
            }
        }

        /// <summary>
        /// Log the current configuration
        /// </summary>
        private void LogConfiguration()
        {
            if (!Enabled)
            {
                m_log.InfoFormat("{0}: Destruction system is DISABLED", LogHeader);
                return;
            }
            
            m_log.InfoFormat("{0}: === Destruction System Configuration ===", LogHeader);
            m_log.InfoFormat("{0}: Core: Enabled={1}, AdminCommands={2}, LSLExtensions={3}",
                LogHeader, Enabled, AdminCommandsEnabled, LSLExtensionsEnabled);
            m_log.InfoFormat("{0}: Performance: UpdateRate={1}Hz, MaxObjects={2}, MaxFragments={3}/{4}",
                LogHeader, UpdateRate, MaxDestructibleObjects, MaxFragmentsPerObject, MaxTotalFragments);
            m_log.InfoFormat("{0}: Effects: Particles={1}x{2:F1}, Cracks={3}x{4:F1}, Environmental={5}x{6:F1}",
                LogHeader, EnableParticleEffects, ParticleIntensity, EnableCrackAnimations, CrackIntensity,
                EnableEnvironmentalEffects, EnvironmentalEffectsDuration);
            m_log.InfoFormat("{0}: Audio: Sounds={1}x{2:F1}, MaxSimultaneous={3}",
                LogHeader, EnableSoundEffects, SoundIntensity, MaxSimultaneousSounds);
            m_log.InfoFormat("{0}: Physics: ForceMultiplier={1:F1}, ChainReactions={2}, Structural={3}",
                LogHeader, ForceMultiplier, EnableChainReactions, EnableStructuralIntegrity);
            m_log.InfoFormat("{0}: Persistence: Enabled={1}, SaveInterval={2}s, RepairSystem={3}",
                LogHeader, EnablePersistence, PersistenceSaveInterval, EnableRepairSystem);
            m_log.InfoFormat("{0}: Debug: Logging={1}, Profiling={2}",
                LogHeader, EnableDestructionLogging, EnablePerformanceProfiling);
        }

        /// <summary>
        /// Generate example configuration text for OpenSim.ini
        /// </summary>
        public static string GenerateExampleConfig()
        {
            return @"
;; Destruction Physics System Configuration
;; Add this section to your OpenSim.ini or OpenSimDefaults.ini

[DestructionSystem]
    ;; Master enable/disable for the destruction system
    ; Enabled = false
    
    ;; Enable admin console commands for destruction system control
    ; AdminCommandsEnabled = true
    
    ;; Enable LSL script extensions for destruction physics
    ; LSLExtensionsEnabled = true
    
    ;; === Performance Settings ===
    
    ;; Update rate in Hz (1-60, higher = smoother but more CPU)
    ; UpdateRate = 10.0
    
    ;; Maximum destructible objects per region (1-1000)
    ; MaxDestructibleObjects = 100
    
    ;; Maximum fragments per destroyed object (1-50)
    ; MaxFragmentsPerObject = 15
    
    ;; Maximum total fragments in region (1-1000)
    ; MaxTotalFragments = 200
    
    ;; Fragment lifetime in seconds before cleanup (1-300)
    ; FragmentLifetime = 60.0
    
    ;; Enable adaptive performance scaling to maintain frame rate
    ; EnableAdaptivePerformance = true
    
    ;; Target frame time in milliseconds (10-100)
    ; TargetFrameTime = 22.0
    
    ;; === Visual Effects ===
    
    ;; Enable particle effects for destructions
    ; EnableParticleEffects = true
    
    ;; Particle effect intensity multiplier (0.0-3.0)
    ; ParticleIntensity = 1.0
    
    ;; Enable crack animations before destruction
    ; EnableCrackAnimations = true
    
    ;; Enable environmental effects (dust clouds, debris)
    ; EnableEnvironmentalEffects = true
    
    ;; === Audio Settings ===
    
    ;; Enable sound effects for destructions
    ; EnableSoundEffects = true
    
    ;; Sound effect intensity multiplier (0.0-3.0)
    ; SoundIntensity = 1.0
    
    ;; === Physics Settings ===
    
    ;; Force calculation multiplier (0.1-10.0)
    ; ForceMultiplier = 1.0
    
    ;; Enable chain reaction destructions
    ; EnableChainReactions = false
    
    ;; Chain reaction trigger radius in meters (1.0-20.0)
    ; ChainReactionRadius = 5.0
    
    ;; Enable structural integrity simulation
    ; EnableStructuralIntegrity = true
    
    ;; === Persistence Settings ===
    
    ;; Enable persistent destruction state across restarts
    ; EnablePersistence = true
    
    ;; Save interval for destruction state in seconds (30-600)
    ; PersistenceSaveInterval = 300.0
    
    ;; Enable automatic repair system for destroyed objects
    ; EnableRepairSystem = true
    
    ;; Default repair time in seconds (10-3600)
    ; DefaultRepairTime = 300.0
    
    ;; === Debug Settings ===
    
    ;; Enable detailed destruction logging
    ; EnableDestructionLogging = false
    
    ;; Enable performance profiling and reporting
    ; EnablePerformanceProfiling = true
    
    ;; Performance report interval in seconds (10-600)
    ; PerformanceReportInterval = 60.0
";
        }
    }
}