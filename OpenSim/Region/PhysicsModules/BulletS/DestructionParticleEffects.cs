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
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OMV = OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Manages particle effects for destruction events
    /// Creates material-appropriate visual effects when objects are destroyed
    /// </summary>
    public class DestructionParticleEffects
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static string LogHeader = "[DESTRUCTION PARTICLE EFFECTS]";

        private readonly Scene m_scene;
        private readonly Dictionary<MaterialType, ParticleEffectConfig> m_materialEffects;
        
        // Environmental effects tracking
        private readonly Dictionary<OMV.UUID, EnvironmentalEffect> m_activeEnvironmentalEffects;
        private readonly object m_environmentLock = new object();
        private DateTime m_lastEnvironmentUpdate = DateTime.UtcNow;
        
        public DestructionParticleEffects(Scene scene)
        {
            m_scene = scene;
            m_materialEffects = new Dictionary<MaterialType, ParticleEffectConfig>();
            m_activeEnvironmentalEffects = new Dictionary<OMV.UUID, EnvironmentalEffect>();
            InitializeMaterialEffects();
            
            // Start environmental effects update timer
            System.Threading.Timer environmentTimer = new System.Threading.Timer(_ => UpdateEnvironmentalEffects(), 
                null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Initialize particle effect configurations for different materials
        /// </summary>
        private void InitializeMaterialEffects()
        {
            // Glass - Fine shards and sparkles
            m_materialEffects[MaterialType.Glass] = new ParticleEffectConfig
            {
                ParticleTexture = OMV.UUID.Parse("89556747-24cb-43ed-920b-47caed15465f"), // Default particle texture
                ParticleColor = new OMV.Vector3(0.9f, 0.95f, 1.0f), // Bright blue-white
                ParticleColorAlpha = 0.9f,
                ParticleCount = 200, // Much more particles for dramatic effect
                ParticleMaxAge = 12.0f, // Much longer lasting
                ParticleStartScale = new OMV.Vector2(0.05f, 0.05f), // Tiny shards
                ParticleEndScale = new OMV.Vector2(0.01f, 0.01f),
                BurstSpeedMin = 8.0f, // Faster, more violent
                BurstSpeedMax = 20.0f,
                Description = "Glass destruction - dramatic shards and sparkles"
            };

            // Metal - Sparks and metallic fragments
            m_materialEffects[MaterialType.Metal] = new ParticleEffectConfig
            {
                ParticleTexture = OMV.UUID.Parse("89556747-24cb-43ed-920b-47caed15465f"),
                ParticleColor = new OMV.Vector3(1.0f, 0.6f, 0.1f), // Bright orange-gold sparks
                ParticleColorAlpha = 1.0f,
                ParticleCount = 300, // Lots more sparks
                ParticleMaxAge = 8.0f, // Longer sparks
                ParticleStartScale = new OMV.Vector2(0.08f, 0.08f), // Bigger sparks
                ParticleEndScale = new OMV.Vector2(0.02f, 0.02f),
                BurstSpeedMin = 12.0f, // Very fast sparks
                BurstSpeedMax = 25.0f,
                Description = "Metal destruction - dramatic sparks and metallic fragments"
            };

            // Wood - Splinters and sawdust
            m_materialEffects[MaterialType.Wood] = new ParticleEffectConfig
            {
                ParticleTexture = OMV.UUID.Parse("89556747-24cb-43ed-920b-47caed15465f"),
                ParticleColor = new OMV.Vector3(0.7f, 0.5f, 0.2f), // Rich brown wood color
                ParticleColorAlpha = 0.9f,
                ParticleCount = 160, // More wood chunks
                ParticleMaxAge = 15.0f, // Much longer floating
                ParticleStartScale = new OMV.Vector2(0.2f, 0.2f), // Bigger splinters
                ParticleEndScale = new OMV.Vector2(0.1f, 0.1f),
                BurstSpeedMin = 3.0f, // Medium speed chunks
                BurstSpeedMax = 12.0f,
                Description = "Wood destruction - dramatic splinters and sawdust"
            };

            // Stone - Dust and rock chips
            m_materialEffects[MaterialType.Stone] = new ParticleEffectConfig
            {
                ParticleTexture = OMV.UUID.Parse("89556747-24cb-43ed-920b-47caed15465f"),
                ParticleColor = new OMV.Vector3(0.6f, 0.6f, 0.5f), // Dusty stone color
                ParticleColorAlpha = 0.8f,
                ParticleCount = 250, // Lots more dust
                ParticleMaxAge = 20.0f, // Very long-lingering dust cloud
                ParticleStartScale = new OMV.Vector2(0.3f, 0.3f), // Big dust clouds
                ParticleEndScale = new OMV.Vector2(0.15f, 0.15f),
                BurstSpeedMin = 2.0f, // Slow floating dust
                BurstSpeedMax = 8.0f,
                Description = "Stone destruction - dramatic dust and rock chips"
            };

            // Concrete - Heavy dust and debris
            m_materialEffects[MaterialType.Concrete] = new ParticleEffectConfig
            {
                ParticleTexture = OMV.UUID.Parse("89556747-24cb-43ed-920b-47caed15465f"),
                ParticleColor = new OMV.Vector3(0.5f, 0.5f, 0.5f), // Dark concrete color
                ParticleColorAlpha = 0.7f,
                ParticleCount = 400, // Massive dust cloud
                ParticleMaxAge = 25.0f, // Extremely long-lasting dust
                ParticleStartScale = new OMV.Vector2(0.4f, 0.4f), // Huge dust clouds
                ParticleEndScale = new OMV.Vector2(0.2f, 0.2f),
                BurstSpeedMin = 1.0f, // Heavy, slow debris
                BurstSpeedMax = 6.0f,
                Description = "Concrete destruction - massive dust and debris"
            };

            m_log.InfoFormat("{0}: Initialized particle effects for {1} material types", LogHeader, m_materialEffects.Count);
        }

        /// <summary>
        /// Create particle effects and sound effects for a destruction event
        /// </summary>
        public void CreateDestructionEffect(OMV.Vector3 position, MaterialType materialType, float impactForce, OMV.Vector3 impactDirection, 
            float particleIntensity = 1.0f, float soundIntensity = 1.0f, bool enableSound = true)
        {
            try
            {
                if (!m_materialEffects.ContainsKey(materialType))
                {
                    m_log.WarnFormat("{0}: No particle effect configured for material type {1}", LogHeader, materialType);
                    materialType = MaterialType.Stone; // Default fallback
                }

                var effectConfig = m_materialEffects[materialType];
                
                // Scale effect intensity based on impact force and intensity multipliers
                float forceMultiplier = Math.Min(impactForce / 50.0f, 3.0f); // Cap at 3x intensity
                int particleCount = (int)(effectConfig.ParticleCount * forceMultiplier * particleIntensity);
                
                // Create the particle system with intensity scaling
                CreateParticleEffect(position, effectConfig, particleCount, impactDirection);
                
                // Create the sound effect using OpenSim default sounds as placeholders (if enabled)
                if (enableSound)
                {
                    CreateSoundEffect(position, materialType, impactForce * soundIntensity);
                }
                
                // Create lingering environmental effects
                CreateEnvironmentalEffects(position, materialType, impactForce, particleIntensity);
                
                m_log.DebugFormat("{0}: Created {1} destruction effect at {2} with {3} particles and sound (force: {4:F1})",
                    LogHeader, materialType, position, particleCount, impactForce);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating destruction particle effect: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Create the actual particle effect in the scene
        /// </summary>
        private void CreateParticleEffect(OMV.Vector3 position, ParticleEffectConfig config, int particleCount, OMV.Vector3 impactDirection)
        {
            try
            {
                // Create a temporary prim to host the particle system
                SceneObjectGroup particleHost = CreateParticleHostObject(position);
                if (particleHost == null)
                {
                    m_log.WarnFormat("{0}: Failed to create particle host object", LogHeader);
                    return;
                }

                // Configure particle system
                OMV.Primitive.ParticleSystem particleSystem = new OMV.Primitive.ParticleSystem();
                
                // Basic particle system properties
                particleSystem.PartDataFlags = OMV.Primitive.ParticleSystem.ParticleDataFlags.InterpColor |
                                              OMV.Primitive.ParticleSystem.ParticleDataFlags.InterpScale |
                                              OMV.Primitive.ParticleSystem.ParticleDataFlags.Emissive |
                                              OMV.Primitive.ParticleSystem.ParticleDataFlags.FollowSrc;

                particleSystem.PartStartColor = new OMV.Color4(config.ParticleColor.X, config.ParticleColor.Y, config.ParticleColor.Z, config.ParticleColorAlpha);
                particleSystem.PartEndColor = new OMV.Color4(config.ParticleColor.X, config.ParticleColor.Y, config.ParticleColor.Z, 0.0f);
                
                particleSystem.PartStartScaleX = config.ParticleStartScale.X;
                particleSystem.PartStartScaleY = config.ParticleStartScale.Y;
                particleSystem.PartEndScaleX = config.ParticleEndScale.X;
                particleSystem.PartEndScaleY = config.ParticleEndScale.Y;
                
                particleSystem.PartMaxAge = config.ParticleMaxAge;
                particleSystem.PartAcceleration = new OMV.Vector3(0.0f, 0.0f, -9.8f); // Gravity
                
                // Burst pattern for explosion effect
                particleSystem.Pattern = OMV.Primitive.ParticleSystem.SourcePattern.Explode;
                particleSystem.MaxAge = 0.5f; // Short burst
                particleSystem.BurstRate = 0.01f; // Quick burst
                particleSystem.BurstPartCount = (byte)Math.Min(particleCount, 255);
                particleSystem.BurstRadius = 0.5f;
                particleSystem.BurstSpeedMin = config.BurstSpeedMin;
                particleSystem.BurstSpeedMax = config.BurstSpeedMax;
                
                // Texture
                particleSystem.Texture = config.ParticleTexture;
                
                // Apply particle system to the host object
                particleHost.RootPart.ParticleSystem = particleSystem.GetBytes();
                particleHost.HasGroupChanged = true;
                
                // Schedule cleanup of the particle host
                ScheduleParticleHostCleanup(particleHost, config.ParticleMaxAge + 1.0f);
                
                m_log.DebugFormat("{0}: Applied particle system to host object {1}", LogHeader, particleHost.UUID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating particle effect: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Create a temporary invisible object to host the particle system
        /// </summary>
        private SceneObjectGroup CreateParticleHostObject(OMV.Vector3 position)
        {
            try
            {
                // Create a small invisible cube to host particles
                OMV.UUID ownerID = OMV.UUID.Zero; // System-owned
                PrimitiveBaseShape shape = PrimitiveBaseShape.CreateBox();
                
                SceneObjectGroup particleHost = new SceneObjectGroup(ownerID, position, shape);
                particleHost.RootPart.Scale = new OMV.Vector3(0.01f, 0.01f, 0.01f); // Very small
                particleHost.RootPart.SetText("", OMV.Vector3.Zero, 0.0f); // No text
                
                // Make it invisible and phantom
                particleHost.RootPart.SetFaceColorAlpha(SceneObjectPart.ALL_SIDES, OMV.Vector3.Zero, 0.0f);
                particleHost.RootPart.UpdatePrimFlags(false, false, true, false, false); // phantom = true
                particleHost.RootPart.ScheduleUpdate(PrimUpdateFlags.PrimFlags);
                
                // Add to scene
                m_scene.AddNewSceneObject(particleHost, false);
                
                m_log.DebugFormat("{0}: Created particle host object at {1}", LogHeader, position);
                return particleHost;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating particle host object: {1}", LogHeader, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Schedule cleanup of particle host object after effect completes
        /// </summary>
        private void ScheduleParticleHostCleanup(SceneObjectGroup particleHost, float delay)
        {
            System.Threading.Timer cleanupTimer = null;
            cleanupTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (particleHost != null && !particleHost.IsDeleted && m_scene.GetSceneObjectGroup(particleHost.UUID) != null)
                    {
                        m_scene.DeleteSceneObject(particleHost, false);
                        m_log.DebugFormat("{0}: Cleaned up particle host object {1}", LogHeader, particleHost.UUID);
                    }
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("{0}: Error cleaning up particle host: {1}", LogHeader, ex.Message);
                }
                finally
                {
                    cleanupTimer?.Dispose();
                }
            }, null, TimeSpan.FromSeconds(delay), TimeSpan.FromMilliseconds(-1));
        }
        /// <summary>
        /// Create sound effects for destruction based on material type
        /// </summary>
        private void CreateSoundEffect(OMV.Vector3 position, MaterialType materialType, float impactForce)
        {
            try
            {
                // Get material-specific sound UUID and volume
                OMV.UUID soundUUID;
                float volume = Math.Min(impactForce / 20.0f, 1.0f); // Scale volume with force
                
                switch (materialType)
                {
                    case MaterialType.Glass:
                        // Glass breaking sound - using alert sound as placeholder
                        soundUUID = OMV.UUID.Parse("ed124764-705d-d497-167a-182cd9fa2e6c"); // UISndAlert
                        break;
                        
                    case MaterialType.Metal:
                        // Metal impact sound - using click sound as placeholder
                        soundUUID = OMV.UUID.Parse("4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6"); // UISndClick
                        break;
                        
                    case MaterialType.Wood:
                        // Wood breaking sound - using object create sound as placeholder
                        soundUUID = OMV.UUID.Parse("f4a0660f-5446-dea2-80b7-6482a082803c"); // UISndObjectCreate
                        break;
                        
                    case MaterialType.Stone:
                        // Stone cracking sound - using object delete sound as placeholder
                        soundUUID = OMV.UUID.Parse("0cb7b00a-4c10-6948-84de-a93c09af2ba9"); // UISndObjectDelete
                        break;
                        
                    case MaterialType.Concrete:
                        // Concrete breaking sound - using teleport sound as placeholder
                        soundUUID = OMV.UUID.Parse("d7a9a565-a013-2a69-797d-5332baa1a947"); // UISndTeleportOut
                        break;
                        
                    default:
                        // Default breaking sound
                        soundUUID = OMV.UUID.Parse("4c8c3c77-de8d-bde2-b9b8-32635e0fd4a6"); // UISndClick
                        break;
                }
                
                // Create sound effect using the particle host
                CreateSoundAtPosition(position, soundUUID, volume, materialType);
                
                m_log.DebugFormat("{0}: Created {1} sound effect at {2} with volume {3:F2}",
                    LogHeader, materialType, position, volume);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating destruction sound effect: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Create a sound effect at a specific position
        /// </summary>
        private void CreateSoundAtPosition(OMV.Vector3 position, OMV.UUID soundUUID, float volume, MaterialType materialType)
        {
            try
            {
                // Create a temporary object to host the sound
                SceneObjectGroup soundHost = CreateParticleHostObject(position);
                if (soundHost == null)
                {
                    m_log.WarnFormat("{0}: Failed to create sound host object for {1}", LogHeader, materialType);
                    return;
                }

                // Play the sound from the host object
                soundHost.RootPart.SendCollisionSound(soundUUID, volume, position);
                
                // Clean up the sound host after a short delay
                ScheduleParticleHostCleanup(soundHost, 2.0f); // Quick cleanup for sound host
                
                m_log.DebugFormat("{0}: Played {1} sound from host object {2}", LogHeader, materialType, soundHost.UUID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating sound at position: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Create lingering environmental effects like dust clouds and smoke
        /// </summary>
        private void CreateEnvironmentalEffects(OMV.Vector3 position, MaterialType materialType, float impactForce, float intensity)
        {
            try
            {
                // Create material-specific environmental effects
                switch (materialType)
                {
                    case MaterialType.Glass:
                        CreateShimmeringDustEffect(position, impactForce, intensity);
                        break;
                        
                    case MaterialType.Metal:
                        CreateSparkShowersEffect(position, impactForce, intensity);
                        break;
                        
                    case MaterialType.Wood:
                        CreateSawdustCloudEffect(position, impactForce, intensity);
                        break;
                        
                    case MaterialType.Stone:
                        CreateDustPlumeEffect(position, impactForce, intensity);
                        break;
                        
                    case MaterialType.Concrete:
                        CreateMassiveDustCloudEffect(position, impactForce, intensity);
                        break;
                }
                
                m_log.DebugFormat("{0}: Created environmental effects for {1} at {2}", LogHeader, materialType, position);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating environmental effects: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Create shimmering glass dust effect
        /// </summary>
        private void CreateShimmeringDustEffect(OMV.Vector3 position, float force, float intensity)
        {
            var effect = new EnvironmentalEffect
            {
                EffectID = OMV.UUID.Random(),
                Position = position,
                EffectType = EnvironmentalEffectType.ShimmeringDust,
                Intensity = intensity * Math.Min(force / 20.0f, 2.0f),
                Duration = 45.0f + (force * 2.0f), // 45-145 seconds
                StartTime = DateTime.UtcNow,
                Color = new OMV.Vector3(0.9f, 0.95f, 1.0f), // Bright blue-white
                Radius = 2.0f + (force * 0.1f)
            };
            
            lock (m_environmentLock)
            {
                m_activeEnvironmentalEffects[effect.EffectID] = effect;
            }
            
            CreateLingeringParticleEffect(effect);
        }

        /// <summary>
        /// Create spark showers effect
        /// </summary>
        private void CreateSparkShowersEffect(OMV.Vector3 position, float force, float intensity)
        {
            var effect = new EnvironmentalEffect
            {
                EffectID = OMV.UUID.Random(),
                Position = position,
                EffectType = EnvironmentalEffectType.SparkShowers,
                Intensity = intensity * Math.Min(force / 30.0f, 1.5f),
                Duration = 25.0f + (force * 1.5f), // 25-100 seconds
                StartTime = DateTime.UtcNow,
                Color = new OMV.Vector3(1.0f, 0.6f, 0.1f), // Orange sparks
                Radius = 1.5f + (force * 0.05f)
            };
            
            lock (m_environmentLock)
            {
                m_activeEnvironmentalEffects[effect.EffectID] = effect;
            }
            
            CreateLingeringParticleEffect(effect);
        }

        /// <summary>
        /// Create sawdust cloud effect
        /// </summary>
        private void CreateSawdustCloudEffect(OMV.Vector3 position, float force, float intensity)
        {
            var effect = new EnvironmentalEffect
            {
                EffectID = OMV.UUID.Random(),
                Position = position,
                EffectType = EnvironmentalEffectType.SawdustCloud,
                Intensity = intensity * Math.Min(force / 15.0f, 2.5f),
                Duration = 60.0f + (force * 3.0f), // 60-210 seconds
                StartTime = DateTime.UtcNow,
                Color = new OMV.Vector3(0.7f, 0.5f, 0.2f), // Brown wood
                Radius = 3.0f + (force * 0.15f)
            };
            
            lock (m_environmentLock)
            {
                m_activeEnvironmentalEffects[effect.EffectID] = effect;
            }
            
            CreateLingeringParticleEffect(effect);
        }

        /// <summary>
        /// Create stone dust plume effect
        /// </summary>
        private void CreateDustPlumeEffect(OMV.Vector3 position, float force, float intensity)
        {
            var effect = new EnvironmentalEffect
            {
                EffectID = OMV.UUID.Random(),
                Position = position,
                EffectType = EnvironmentalEffectType.DustPlume,
                Intensity = intensity * Math.Min(force / 25.0f, 3.0f),
                Duration = 90.0f + (force * 4.0f), // 90-290 seconds
                StartTime = DateTime.UtcNow,
                Color = new OMV.Vector3(0.6f, 0.6f, 0.5f), // Gray stone
                Radius = 4.0f + (force * 0.2f)
            };
            
            lock (m_environmentLock)
            {
                m_activeEnvironmentalEffects[effect.EffectID] = effect;
            }
            
            CreateLingeringParticleEffect(effect);
        }

        /// <summary>
        /// Create massive concrete dust cloud effect
        /// </summary>
        private void CreateMassiveDustCloudEffect(OMV.Vector3 position, float force, float intensity)
        {
            var effect = new EnvironmentalEffect
            {
                EffectID = OMV.UUID.Random(),
                Position = position,
                EffectType = EnvironmentalEffectType.MassiveDustCloud,
                Intensity = intensity * Math.Min(force / 40.0f, 4.0f),
                Duration = 120.0f + (force * 5.0f), // 120-420 seconds (up to 7 minutes!)
                StartTime = DateTime.UtcNow,
                Color = new OMV.Vector3(0.5f, 0.5f, 0.5f), // Dark concrete
                Radius = 6.0f + (force * 0.25f)
            };
            
            lock (m_environmentLock)
            {
                m_activeEnvironmentalEffects[effect.EffectID] = effect;
            }
            
            CreateLingeringParticleEffect(effect);
        }

        /// <summary>
        /// Create a lingering particle effect that persists in the environment
        /// </summary>
        private void CreateLingeringParticleEffect(EnvironmentalEffect effect)
        {
            try
            {
                // Create host object for the environmental effect
                SceneObjectGroup environmentHost = CreateEnvironmentalHostObject(effect.Position, effect.EffectType);
                if (environmentHost == null)
                    return;

                effect.HostObjectUUID = environmentHost.UUID;

                // Create persistent particle system
                var particleSystem = new OMV.Primitive.ParticleSystem();
                
                // Configure for environmental effects (longer lasting, slower moving)
                particleSystem.PartDataFlags = OMV.Primitive.ParticleSystem.ParticleDataFlags.InterpColor |
                                              OMV.Primitive.ParticleSystem.ParticleDataFlags.InterpScale |
                                              OMV.Primitive.ParticleSystem.ParticleDataFlags.Emissive;

                particleSystem.PartStartColor = new OMV.Color4(effect.Color.X, effect.Color.Y, effect.Color.Z, 0.4f); // Semi-transparent
                particleSystem.PartEndColor = new OMV.Color4(effect.Color.X, effect.Color.Y, effect.Color.Z, 0.0f); // Fade out
                
                // Smaller, longer-lasting particles for environmental effects
                particleSystem.PartStartScaleX = 0.02f;
                particleSystem.PartStartScaleY = 0.02f;
                particleSystem.PartEndScaleX = 0.1f + (effect.Intensity * 0.1f);
                particleSystem.PartEndScaleY = 0.1f + (effect.Intensity * 0.1f);
                
                particleSystem.PartMaxAge = 30.0f + (effect.Intensity * 10.0f); // Very long particle life
                particleSystem.PartAcceleration = new OMV.Vector3(0.0f, 0.0f, -0.5f); // Slow settling
                
                // Continuous emission for environmental effects
                particleSystem.Pattern = OMV.Primitive.ParticleSystem.SourcePattern.Drop;
                particleSystem.MaxAge = effect.Duration; // Run for full effect duration
                particleSystem.BurstRate = 2.0f; // Slow, continuous emission
                particleSystem.BurstPartCount = (byte)Math.Min(5 + (int)(effect.Intensity * 3), 20);
                particleSystem.BurstRadius = effect.Radius;
                particleSystem.BurstSpeedMin = 0.1f;
                particleSystem.BurstSpeedMax = 0.5f + (effect.Intensity * 0.3f);
                
                particleSystem.Texture = OMV.UUID.Parse("89556747-24cb-43ed-920b-47caed15465f");
                
                // Apply to host
                environmentHost.RootPart.ParticleSystem = particleSystem.GetBytes();
                environmentHost.HasGroupChanged = true;
                
                m_log.DebugFormat("{0}: Created lingering {1} effect lasting {2:F0} seconds",
                    LogHeader, effect.EffectType, effect.Duration);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating lingering particle effect: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Create host object for environmental effects
        /// </summary>
        private SceneObjectGroup CreateEnvironmentalHostObject(OMV.Vector3 position, EnvironmentalEffectType effectType)
        {
            try
            {
                PrimitiveBaseShape shape = PrimitiveBaseShape.CreateBox();
                SceneObjectGroup environmentHost = new SceneObjectGroup(OMV.UUID.Zero, position, shape);
                
                // Make it very small and invisible
                environmentHost.RootPart.Scale = new OMV.Vector3(0.001f, 0.001f, 0.001f);
                environmentHost.RootPart.SetFaceColorAlpha(SceneObjectPart.ALL_SIDES, OMV.Vector3.Zero, 0.0f);
                environmentHost.RootPart.UpdatePrimFlags(false, false, true, false, false); // phantom
                
                // Name it for identification
                environmentHost.Name = $"EnvEffect_{effectType}_{DateTime.UtcNow.Ticks}";
                
                m_scene.AddNewSceneObject(environmentHost, false);
                
                return environmentHost;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error creating environmental host object: {1}", LogHeader, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Update and cleanup environmental effects
        /// </summary>
        private void UpdateEnvironmentalEffects()
        {
            try
            {
                var currentTime = DateTime.UtcNow;
                var expiredEffects = new List<OMV.UUID>();

                lock (m_environmentLock)
                {
                    foreach (var kvp in m_activeEnvironmentalEffects)
                    {
                        var effect = kvp.Value;
                        var elapsed = (currentTime - effect.StartTime).TotalSeconds;
                        
                        if (elapsed >= effect.Duration)
                        {
                            expiredEffects.Add(kvp.Key);
                            
                            // Clean up host object
                            if (effect.HostObjectUUID != OMV.UUID.Zero)
                            {
                                var hostObject = m_scene.GetSceneObjectGroup(effect.HostObjectUUID);
                                if (hostObject != null)
                                {
                                    m_scene.DeleteSceneObject(hostObject, false);
                                }
                            }
                        }
                    }

                    // Remove expired effects
                    foreach (var expiredId in expiredEffects)
                    {
                        m_activeEnvironmentalEffects.Remove(expiredId);
                    }
                }

                if (expiredEffects.Count > 0)
                {
                    m_log.DebugFormat("{0}: Cleaned up {1} expired environmental effects", LogHeader, expiredEffects.Count);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error updating environmental effects: {1}", LogHeader, ex.Message);
            }
        }

        /// <summary>
        /// Get count of active environmental effects
        /// </summary>
        public int GetActiveEnvironmentalEffectCount()
        {
            lock (m_environmentLock)
            {
                return m_activeEnvironmentalEffects.Count;
            }
        }
    }

    /// <summary>
    /// Configuration for particle effects for a specific material type
    /// </summary>
    public class ParticleEffectConfig
    {
        public OMV.UUID ParticleTexture { get; set; }
        public OMV.Vector3 ParticleColor { get; set; }
        public float ParticleColorAlpha { get; set; }
        public int ParticleCount { get; set; }
        public float ParticleMaxAge { get; set; }
        public OMV.Vector2 ParticleStartScale { get; set; }
        public OMV.Vector2 ParticleEndScale { get; set; }
        public float BurstSpeedMin { get; set; }
        public float BurstSpeedMax { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Material types for particle effects
    /// </summary>
    public enum MaterialType
    {
        Glass = 0,
        Metal = 1,
        Wood = 2,
        Stone = 3,
        Concrete = 4
    }

    /// <summary>
    /// Types of environmental effects
    /// </summary>
    public enum EnvironmentalEffectType
    {
        ShimmeringDust,
        SparkShowers, 
        SawdustCloud,
        DustPlume,
        MassiveDustCloud
    }

    /// <summary>
    /// Persistent environmental effect from destruction
    /// </summary>
    public class EnvironmentalEffect
    {
        public OMV.UUID EffectID { get; set; }
        public OMV.Vector3 Position { get; set; }
        public EnvironmentalEffectType EffectType { get; set; }
        public float Intensity { get; set; }
        public float Duration { get; set; }
        public DateTime StartTime { get; set; }
        public OMV.Vector3 Color { get; set; }
        public float Radius { get; set; }
        public OMV.UUID HostObjectUUID { get; set; }
    }
}