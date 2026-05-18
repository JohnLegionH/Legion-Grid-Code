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
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    /// <summary>
    /// Manages region-specific destruction policies and rules
    /// Provides different destruction behaviors based on location, parcel, and estate settings
    /// </summary>
    public class RegionDestructionPolicies
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[DESTRUCTION POLICIES]";

        private readonly Scene m_scene;
        private readonly Dictionary<string, DestructionZone> m_zones = new Dictionary<string, DestructionZone>();
        private readonly Dictionary<int, ParcelDestructionPolicy> m_parcelPolicies = new Dictionary<int, ParcelDestructionPolicy>();
        private RegionDestructionPolicy m_defaultPolicy;

        public RegionDestructionPolicies(Scene scene)
        {
            m_scene = scene;
            InitializeDefaultPolicy();
            LoadRegionZones();
        }

        #region Public Interface

        /// <summary>
        /// Check if destruction is allowed at the specified location
        /// </summary>
        public DestructionPermission CheckDestructionPermission(OMV.Vector3 location, OMV.UUID objectOwner, OMV.UUID requester)
        {
            try
            {
                // Check zone-specific rules first (highest priority)
                var zone = GetZoneAtLocation(location);
                if (zone != null)
                {
                    var zonePermission = zone.CheckPermission(location, objectOwner, requester);
                    if (zonePermission.IsExplicit)
                        return zonePermission;
                }

                // Check parcel-specific rules
                var parcel = m_scene.LandChannel.GetLandObject(location.X, location.Y);
                if (parcel != null && m_parcelPolicies.TryGetValue(parcel.LandData.LocalID, out var parcelPolicy))
                {
                    var parcelPermission = parcelPolicy.CheckPermission(location, objectOwner, requester, parcel);
                    if (parcelPermission.IsExplicit)
                        return parcelPermission;
                }

                // Fall back to default region policy
                return m_defaultPolicy.CheckPermission(location, objectOwner, requester);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("{0}: Error checking destruction permission: {1}", LogHeader, ex.Message);
                return new DestructionPermission { Allowed = false, Reason = "Error checking permissions" };
            }
        }

        /// <summary>
        /// Get destruction modifier for the specified location
        /// This affects damage multipliers, effect intensity, etc.
        /// </summary>
        public DestructionModifier GetDestructionModifier(OMV.Vector3 location)
        {
            var zone = GetZoneAtLocation(location);
            if (zone != null)
                return zone.Modifier;

            var parcel = m_scene.LandChannel.GetLandObject(location.X, location.Y);
            if (parcel != null && m_parcelPolicies.TryGetValue(parcel.LandData.LocalID, out var parcelPolicy))
                return parcelPolicy.Modifier;

            return m_defaultPolicy.Modifier;
        }

        /// <summary>
        /// Create a new destruction zone
        /// </summary>
        public void CreateZone(string name, DestructionZone zone)
        {
            m_zones[name] = zone;
            m_log.InfoFormat("{0}: Created destruction zone '{1}' of type {2}", LogHeader, name, zone.Type);
        }

        /// <summary>
        /// Remove a destruction zone
        /// </summary>
        public void RemoveZone(string name)
        {
            if (m_zones.Remove(name))
            {
                m_log.InfoFormat("{0}: Removed destruction zone '{1}'", LogHeader, name);
            }
        }

        /// <summary>
        /// Set parcel-specific destruction policy
        /// </summary>
        public void SetParcelPolicy(int parcelID, ParcelDestructionPolicy policy)
        {
            m_parcelPolicies[parcelID] = policy;
            m_log.InfoFormat("{0}: Set destruction policy for parcel {1}", LogHeader, parcelID);
        }

        /// <summary>
        /// Update default region policy
        /// </summary>
        public void SetDefaultPolicy(RegionDestructionPolicy policy)
        {
            m_defaultPolicy = policy;
            m_log.InfoFormat("{0}: Updated default region destruction policy", LogHeader);
        }

        #endregion

        #region Zone Management

        private DestructionZone GetZoneAtLocation(OMV.Vector3 location)
        {
            foreach (var zone in m_zones.Values)
            {
                if (zone.ContainsPoint(location))
                    return zone;
            }
            return null;
        }

        private void InitializeDefaultPolicy()
        {
            m_defaultPolicy = new RegionDestructionPolicy
            {
                AllowDestruction = true,
                RequireOwnership = false,
                RequireGroup = false,
                AllowPublicDestruction = true,
                Modifier = new DestructionModifier
                {
                    DamageMultiplier = 1.0f,
                    EffectIntensity = 1.0f,
                    FragmentMultiplier = 1.0f,
                    ChainReactionEnabled = true,
                    EnvironmentalEffectsEnabled = true
                }
            };
        }

        private void LoadRegionZones()
        {
            // Create example zones - these would typically be loaded from configuration
            
            // Safe zone example (no destruction allowed)
            CreateZone("SafeZone_Landing", new DestructionZone
            {
                Name = "Landing Area Safe Zone",
                Type = ZoneType.SafeZone,
                Bounds = new ZoneBounds
                {
                    Center = new OMV.Vector3(128, 128, 25),
                    Radius = 50.0f,
                    Shape = ZoneShape.Sphere
                },
                Policy = new ZoneDestructionPolicy
                {
                    AllowDestruction = false,
                    OverridePermissions = true
                },
                Modifier = new DestructionModifier
                {
                    DamageMultiplier = 0.0f,
                    EffectIntensity = 0.0f,
                    FragmentMultiplier = 0.0f,
                    ChainReactionEnabled = false,
                    EnvironmentalEffectsEnabled = false
                }
            });

            // High damage zone example
            CreateZone("WarZone_North", new DestructionZone
            {
                Name = "Northern War Zone",
                Type = ZoneType.HighDamageZone,
                Bounds = new ZoneBounds
                {
                    Min = new OMV.Vector3(200, 200, 0),
                    Max = new OMV.Vector3(256, 256, 100),
                    Shape = ZoneShape.Box
                },
                Policy = new ZoneDestructionPolicy
                {
                    AllowDestruction = true,
                    RequireOwnership = false,
                    AllowPublicDestruction = true
                },
                Modifier = new DestructionModifier
                {
                    DamageMultiplier = 2.5f,
                    EffectIntensity = 2.0f,
                    FragmentMultiplier = 1.5f,
                    ChainReactionEnabled = true,
                    EnvironmentalEffectsEnabled = true
                }
            });

            m_log.InfoFormat("{0}: Loaded {1} destruction zones for region {2}", 
                LogHeader, m_zones.Count, m_scene.RegionInfo.RegionName);
        }

        #endregion

        #region Admin Commands

        public void ListZones()
        {
            m_log.InfoFormat("{0}: === Destruction Zones in {1} ===", LogHeader, m_scene.RegionInfo.RegionName);
            foreach (var zone in m_zones.Values)
            {
                m_log.InfoFormat("{0}: Zone '{1}' ({2}): {3}", 
                    LogHeader, zone.Name, zone.Type, 
                    zone.Policy.AllowDestruction ? "Destruction Allowed" : "Safe Zone");
            }
        }

        public string GetZoneInfo(string zoneName)
        {
            if (!m_zones.TryGetValue(zoneName, out var zone))
                return $"Zone '{zoneName}' not found";

            return $"Zone: {zone.Name}\n" +
                   $"Type: {zone.Type}\n" +
                   $"Destruction Allowed: {zone.Policy.AllowDestruction}\n" +
                   $"Damage Multiplier: {zone.Modifier.DamageMultiplier:F1}x\n" +
                   $"Effect Intensity: {zone.Modifier.EffectIntensity:F1}x\n" +
                   $"Bounds: {zone.Bounds.Shape} at {zone.Bounds.Center}";
        }

        #endregion
    }

    #region Data Structures

    public class DestructionPermission
    {
        public bool Allowed { get; set; }
        public string Reason { get; set; } = "";
        public bool IsExplicit { get; set; } = true; // If false, falls through to next policy level
    }

    public class DestructionModifier
    {
        public float DamageMultiplier { get; set; } = 1.0f;
        public float EffectIntensity { get; set; } = 1.0f;
        public float FragmentMultiplier { get; set; } = 1.0f;
        public bool ChainReactionEnabled { get; set; } = true;
        public bool EnvironmentalEffectsEnabled { get; set; } = true;
    }

    public class DestructionZone
    {
        public string Name { get; set; }
        public ZoneType Type { get; set; }
        public ZoneBounds Bounds { get; set; }
        public ZoneDestructionPolicy Policy { get; set; }
        public DestructionModifier Modifier { get; set; }

        public bool ContainsPoint(OMV.Vector3 point)
        {
            switch (Bounds.Shape)
            {
                case ZoneShape.Sphere:
                    return (point - Bounds.Center).Length() <= Bounds.Radius;
                
                case ZoneShape.Box:
                    return point.X >= Bounds.Min.X && point.X <= Bounds.Max.X &&
                           point.Y >= Bounds.Min.Y && point.Y <= Bounds.Max.Y &&
                           point.Z >= Bounds.Min.Z && point.Z <= Bounds.Max.Z;
                
                case ZoneShape.Cylinder:
                    var distance2D = Math.Sqrt(Math.Pow(point.X - Bounds.Center.X, 2) + 
                                             Math.Pow(point.Y - Bounds.Center.Y, 2));
                    return distance2D <= Bounds.Radius && 
                           point.Z >= Bounds.Min.Z && point.Z <= Bounds.Max.Z;
                
                default:
                    return false;
            }
        }

        public DestructionPermission CheckPermission(OMV.Vector3 location, OMV.UUID objectOwner, OMV.UUID requester)
        {
            if (!Policy.AllowDestruction)
            {
                return new DestructionPermission 
                { 
                    Allowed = false, 
                    Reason = $"Destruction disabled in {Name}",
                    IsExplicit = Policy.OverridePermissions
                };
            }

            if (Policy.RequireOwnership && objectOwner != requester)
            {
                return new DestructionPermission 
                { 
                    Allowed = false, 
                    Reason = "Must own object to destroy it in this zone" 
                };
            }

            return new DestructionPermission { Allowed = true, Reason = "Zone permits destruction" };
        }
    }

    public class ZoneBounds
    {
        public ZoneShape Shape { get; set; }
        public OMV.Vector3 Center { get; set; }
        public OMV.Vector3 Min { get; set; }
        public OMV.Vector3 Max { get; set; }
        public float Radius { get; set; }
    }

    public class ZoneDestructionPolicy
    {
        public bool AllowDestruction { get; set; } = true;
        public bool RequireOwnership { get; set; } = false;
        public bool RequireGroup { get; set; } = false;
        public bool AllowPublicDestruction { get; set; } = true;
        public bool OverridePermissions { get; set; } = false;
    }

    public class RegionDestructionPolicy
    {
        public bool AllowDestruction { get; set; } = true;
        public bool RequireOwnership { get; set; } = false;
        public bool RequireGroup { get; set; } = false;
        public bool AllowPublicDestruction { get; set; } = true;
        public DestructionModifier Modifier { get; set; } = new DestructionModifier();

        public DestructionPermission CheckPermission(OMV.Vector3 location, OMV.UUID objectOwner, OMV.UUID requester)
        {
            if (!AllowDestruction)
                return new DestructionPermission { Allowed = false, Reason = "Destruction disabled in region" };

            if (RequireOwnership && objectOwner != requester)
                return new DestructionPermission { Allowed = false, Reason = "Must own object to destroy it" };

            return new DestructionPermission { Allowed = true, Reason = "Region permits destruction" };
        }
    }

    public class ParcelDestructionPolicy
    {
        public bool AllowDestruction { get; set; } = true;
        public bool RequireOwnership { get; set; } = false;
        public bool RequireGroup { get; set; } = false;
        public bool RequireLandRights { get; set; } = false;
        public DestructionModifier Modifier { get; set; } = new DestructionModifier();

        public DestructionPermission CheckPermission(OMV.Vector3 location, OMV.UUID objectOwner, OMV.UUID requester, ILandObject parcel)
        {
            if (!AllowDestruction)
                return new DestructionPermission { Allowed = false, Reason = "Destruction disabled on this parcel" };

            if (RequireOwnership && objectOwner != requester)
                return new DestructionPermission { Allowed = false, Reason = "Must own object to destroy it on this parcel" };

            if (RequireLandRights && !parcel.IsEitherBannedOrRestricted(requester))
            {
                return new DestructionPermission { Allowed = false, Reason = "Insufficient land rights for destruction" };
            }

            return new DestructionPermission { Allowed = true, Reason = "Parcel permits destruction", IsExplicit = false };
        }
    }

    public enum ZoneType
    {
        SafeZone,
        HighDamageZone,
        PvPZone,
        BuildingZone,
        CustomZone
    }

    public enum ZoneShape
    {
        Sphere,
        Box,
        Cylinder
    }

    #endregion
}