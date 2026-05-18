/*
 * Compatibility shim for OpenMetaverse OSDMap extension methods.
 * These methods exist in newer versions of OpenMetaverse but not in the
 * version used by this OpenSim build. This file provides equivalent
 * functionality using the APIs available in the older version.
 */

using System;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace osWebRtcVoice
{
    /// <summary>
    /// Extension methods for OSDMap to provide compatibility with newer
    /// OpenMetaverse API that includes TryGet* methods.
    /// </summary>
    public static class OSDMapExtensions
    {
        public static bool TryGetString(this OSDMap map, string key, out string value)
        {
            if (map != null && map.ContainsKey(key))
            {
                value = map[key].AsString();
                return true;
            }
            value = null;
            return false;
        }

        public static bool TryGetOSDMap(this OSDMap map, string key, out OSDMap value)
        {
            if (map != null && map.ContainsKey(key) && map[key] is OSDMap result)
            {
                value = result;
                return true;
            }
            value = null;
            return false;
        }

        public static bool TryGetOSDArray(this OSDMap map, string key, out OSDArray value)
        {
            if (map != null && map.ContainsKey(key) && map[key] is OSDArray result)
            {
                value = result;
                return true;
            }
            value = null;
            return false;
        }

        public static bool TryGetUUID(this OSDMap map, string key, out UUID value)
        {
            if (map != null && map.ContainsKey(key))
            {
                if (UUID.TryParse(map[key].AsString(), out UUID result))
                {
                    value = result;
                    return true;
                }
            }
            value = UUID.Zero;
            return false;
        }

        public static bool TryGetBool(this OSDMap map, string key, out bool value)
        {
            if (map != null && map.ContainsKey(key))
            {
                value = map[key].AsBoolean();
                return true;
            }
            value = false;
            return false;
        }

        public static bool TryGetInt(this OSDMap map, string key, out int value)
        {
            if (map != null && map.ContainsKey(key))
            {
                value = map[key].AsInteger();
                return true;
            }
            value = 0;
            return false;
        }
    }

    /// <summary>
    /// OSDLong exists in newer OpenMetaverse but not older versions.
    /// Provide it here as a simple wrapper around OSD with long support.
    /// </summary>
    public class OSDLong : OSD
    {
        private long m_value;

        public OSDLong(long value)
        {
            m_value = value;
        }

        public override long AsLong() => m_value;
        public override ulong AsULong() => (ulong)m_value;
        public override int AsInteger() => (int)m_value;
        public override double AsReal() => m_value;
        public override bool AsBoolean() => m_value != 0;
        public override string AsString() => m_value.ToString();

        public override string ToString() => m_value.ToString();
    }
}
