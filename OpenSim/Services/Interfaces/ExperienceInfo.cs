/*
 * Legion Grid — Experience System
 * ExperienceInfo.cs — Data class for experience records
 *
 * Place in: OpenSim/Services/Interfaces/ExperienceInfo.cs
 * (or alongside IExperienceService.cs)
 */

using System;
using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    public class ExperienceInfo
    {
        public UUID ExperienceId = UUID.Zero;
        public UUID OwnerId = UUID.Zero;
        public UUID GroupId = UUID.Zero;
        public string Name = string.Empty;
        public string Description = string.Empty;
        public int Maturity = 0;        // 0=PG, 1=Mature, 2=Adult
        public int Properties = PROP_ENABLED; // bitfield — enabled by default
        public UUID Logo = UUID.Zero;
        public string Marketplace = string.Empty;
        public string Slurl = string.Empty;
        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;

        // Property flags
        public const int PROP_ENABLED   = 1;
        public const int PROP_GRIDWIDE  = 2;
        public const int PROP_PRIVATE   = 4;  // only owner can add scripts

        public bool IsEnabled => (Properties & PROP_ENABLED) != 0;
        public bool IsGridWide => (Properties & PROP_GRIDWIDE) != 0;
        public bool IsPrivate => (Properties & PROP_PRIVATE) != 0;

        // SL experience error codes
        public const int XP_ERROR_NONE = 0;
        public const int XP_ERROR_THROTTLED = 1;
        public const int XP_ERROR_EXPERIENCES_DISABLED = 2;
        public const int XP_ERROR_INVALID_PARAMETERS = 3;
        public const int XP_ERROR_NOT_PERMITTED = 4;
        public const int XP_ERROR_NO_EXPERIENCE = 5;
        public const int XP_ERROR_NOT_FOUND = 6;
        public const int XP_ERROR_INVALID_EXPERIENCE = 7;
        public const int XP_ERROR_EXPERIENCE_DISABLED = 8;
        public const int XP_ERROR_EXPERIENCE_SUSPENDED = 9;
        public const int XP_ERROR_UNKNOWN_ERROR = 10;
        public const int XP_ERROR_QUOTA_EXCEEDED = 11;
        public const int XP_ERROR_STORE_DISABLED = 12;
        public const int XP_ERROR_STORAGE_EXCEPTION = 13;
        public const int XP_ERROR_KEY_NOT_FOUND = 14;
        public const int XP_ERROR_RETRY_UPDATE = 15;
        public const int XP_ERROR_MATURITY_EXCEEDED = 16;
        public const int XP_ERROR_NOT_PERMITTED_LAND = 17;
        public const int XP_ERROR_REQUEST_PERM_TIMEOUT = 18;

        // SL key-value limits
        public const int MAX_KEY_LENGTH = 1011;
        public const int MAX_VALUE_LENGTH = 4095;
        public const long MAX_DATA_QUOTA = 128L * 1024 * 1024; // 128 MiB

        public static string GetErrorMessage(int error)
        {
            switch (error)
            {
                case XP_ERROR_NONE: return "no error";
                case XP_ERROR_THROTTLED: return "exceeded throttle";
                case XP_ERROR_EXPERIENCES_DISABLED: return "experiences are disabled";
                case XP_ERROR_INVALID_PARAMETERS: return "invalid parameters";
                case XP_ERROR_NOT_PERMITTED: return "operation not permitted";
                case XP_ERROR_NO_EXPERIENCE: return "script not associated with an experience";
                case XP_ERROR_NOT_FOUND: return "not found";
                case XP_ERROR_INVALID_EXPERIENCE: return "invalid experience";
                case XP_ERROR_EXPERIENCE_DISABLED: return "experience is disabled";
                case XP_ERROR_EXPERIENCE_SUSPENDED: return "experience is suspended";
                case XP_ERROR_UNKNOWN_ERROR: return "unknown error";
                case XP_ERROR_QUOTA_EXCEEDED: return "experience data quota exceeded";
                case XP_ERROR_STORE_DISABLED: return "key-value store is disabled";
                case XP_ERROR_STORAGE_EXCEPTION: return "key-value store communication failed";
                case XP_ERROR_KEY_NOT_FOUND: return "key doesn't exist";
                case XP_ERROR_RETRY_UPDATE: return "retry update";
                case XP_ERROR_MATURITY_EXCEEDED: return "experience content rating too high";
                case XP_ERROR_NOT_PERMITTED_LAND: return "not allowed to run on this land";
                case XP_ERROR_REQUEST_PERM_TIMEOUT: return "experience permissions request timed out";
                default: return "unknown error";
            }
        }
    }
}
