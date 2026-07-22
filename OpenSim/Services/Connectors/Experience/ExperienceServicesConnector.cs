/*
 * Legion Grid — Experience System (grid-service topology)
 * ExperienceServicesConnector.cs — Robust HTTP client for IExperienceService (G3).
 *
 * The region-side client half of the grid-service rebuild. Implements IExperienceService by
 * POSTing METHOD-verb form requests to the G2 Robust endpoint (POST /experience) and parsing
 * the ServerUtils XML responses back into the same types the in-process (Local, G1) connector
 * would return. Reuses ExperienceInfo.FromDictionary — the exact inverse of the ToDictionary
 * the G2 handler serializes with — so the wire round-trips losslessly.
 *
 * Response shapes served by G2 (matched here):
 *   list   -> <exp0 type="List">..</exp0><exp1 ..>..   (indexed record maps)
 *   single -> <RESULT type="List">..fields..</RESULT>  or  <NULL>True</NULL>
 *   bool   -> <RESULT>True|False</RESULT>
 *   int    -> <RESULT>123</RESULT>
 *   uuid   -> <RESULT>uuid</RESULT>
 *   string -> <RESULT>value</RESULT>  or  <NULL>True</NULL>
 *   uuids  -> <uuid0>..</uuid0><uuid1>..   ;   strings -> <item0>..</item0>..
 *   error  -> <RESULT>Failure</RESULT>
 *
 * Connector TOPOLOGY / wire-protocol shape credit: adapted from the OpenSim-NGC /
 * OpenSim-Tranquillity Experience service-connector stack (orig. StolenRuby; integ. Mike
 * Dickson / OpenSim-NGC, Utopia Skye). Implementation is Legion Grid's own; mirrors the stock
 * GridUserServicesConnector client.
 */

using log4net;
using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;
using OpenMetaverse;

namespace OpenSim.Services.Connectors
{
    public class ExperienceServicesConnector : BaseServiceConnector, IExperienceService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_ServerURI = String.Empty;

        public ExperienceServicesConnector()
        {
        }

        public ExperienceServicesConnector(string serverURI)
        {
            m_ServerURI = serverURI.TrimEnd('/');
        }

        public ExperienceServicesConnector(IConfigSource source)
        {
            Initialise(source);
        }

        public virtual void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ExperienceService"];
            if (config == null)
            {
                m_log.Error("[EXPERIENCE CONNECTOR]: ExperienceService missing from configuration");
                throw new Exception("Experience connector init error");
            }

            string serviceURI = config.GetString("ExperienceServerURI", string.Empty);
            if (string.IsNullOrWhiteSpace(serviceURI))
            {
                m_log.Error("[EXPERIENCE CONNECTOR]: No ExperienceServerURI in section [ExperienceService]");
                throw new Exception("Experience connector init error");
            }

            OSHHTPHost tmp = new OSHHTPHost(serviceURI, true);
            if (!tmp.IsResolvedHost)
            {
                m_log.ErrorFormat("[EXPERIENCE CONNECTOR]: {0}", tmp.IsValidHost ? "Could not resolve ExperienceServerURI" : "ExperienceServerURI is an invalid host");
                throw new Exception("Experience connector init error");
            }

            m_ServerURI = tmp.URI.TrimEnd('/');

            base.Initialise(source, "ExperienceService");
            m_log.InfoFormat("[EXPERIENCE CONNECTOR]: Remote experience service at {0}", m_ServerURI);
        }

        #region IExperienceService

        // ── Experience CRUD ──
        public ExperienceInfo GetExperience(UUID experienceId)
            => ParseInfo(DoRequest(Verb("getexperience", "EXPERIENCEID", experienceId.ToString())));

        public ExperienceInfo GetExperienceByName(string name)
            => ParseInfo(DoRequest(Verb("getexperiencebyname", "NAME", name)));

        public ExperienceInfo CreateExperience(ExperienceInfo info)
            => ParseInfo(DoRequest(WithInfo("createexperience", info)));

        public bool UpdateExperience(ExperienceInfo info)
            => ParseBool(DoRequest(WithInfo("updateexperience", info)));

        public bool DeleteExperience(UUID experienceId)
            => ParseBool(DoRequest(Verb("deleteexperience", "EXPERIENCEID", experienceId.ToString())));

        public List<ExperienceInfo> GetExperiencesByOwner(UUID ownerId)
            => ParseInfoList(DoRequest(Verb("getexperiencesbyowner", "OWNERID", ownerId.ToString())));

        public List<ExperienceInfo> GetExperiencesByGroup(UUID groupId)
            => ParseInfoList(DoRequest(Verb("getexperiencesbygroup", "GROUPID", groupId.ToString())));

        public bool IsExperienceContributor(UUID experienceId, UUID agentId)
            => ParseBool(DoRequest(Verb("isexperiencecontributor", "EXPERIENCEID", experienceId.ToString(), "AGENTID", agentId.ToString())));

        public bool IsExperienceAdmin(UUID experienceId, UUID agentId)
            => ParseBool(DoRequest(Verb("isexperienceadmin", "EXPERIENCEID", experienceId.ToString(), "AGENTID", agentId.ToString())));

        public List<ExperienceInfo> FindExperiences(string query)
            => ParseInfoList(DoRequest(Verb("findexperiences", "QUERY", query)));

        public List<ExperienceInfo> FindExperiences(string query, int offset, int limit)
            => ParseInfoList(DoRequest(Verb("findexperiencespaged", "QUERY", query, "OFFSET", offset.ToString(), "LIMIT", limit.ToString())));

        // ── Permission Grants ──
        public bool IsAgentGranted(UUID experienceId, UUID agentId)
            => ParseBool(DoRequest(Verb("isagentgranted", "EXPERIENCEID", experienceId.ToString(), "AGENTID", agentId.ToString())));

        public bool IsAgentBlocked(UUID experienceId, UUID agentId)
            => ParseBool(DoRequest(Verb("isagentblocked", "EXPERIENCEID", experienceId.ToString(), "AGENTID", agentId.ToString())));

        public bool GrantPermission(UUID experienceId, UUID agentId)
            => ParseBool(DoRequest(Verb("grantpermission", "EXPERIENCEID", experienceId.ToString(), "AGENTID", agentId.ToString())));

        public bool DenyPermission(UUID experienceId, UUID agentId)
            => ParseBool(DoRequest(Verb("denypermission", "EXPERIENCEID", experienceId.ToString(), "AGENTID", agentId.ToString())));

        public bool ForgetPermission(UUID experienceId, UUID agentId)
            => ParseBool(DoRequest(Verb("forgetpermission", "EXPERIENCEID", experienceId.ToString(), "AGENTID", agentId.ToString())));

        public List<UUID> GetAgentExperiences(UUID agentId)
            => ParseUUIDList(DoRequest(Verb("getagentexperiences", "AGENTID", agentId.ToString())));

        public List<UUID> GetAgentBlockedExperiences(UUID agentId)
            => ParseUUIDList(DoRequest(Verb("getagentblockedexperiences", "AGENTID", agentId.ToString())));

        public bool BlockExperienceForAgent(UUID agentId, UUID experienceId)
            => ParseBool(DoRequest(Verb("blockexperienceforagent", "AGENTID", agentId.ToString(), "EXPERIENCEID", experienceId.ToString())));

        public bool UnblockExperienceForAgent(UUID agentId, UUID experienceId)
            => ParseBool(DoRequest(Verb("unblockexperienceforagent", "AGENTID", agentId.ToString(), "EXPERIENCEID", experienceId.ToString())));

        // ── Key-Value Store ── (service-level calls; the async dataserver contract stays in the Phlox LSL layer above)
        public string ReadKeyValue(UUID experienceId, string key)
            => ParseString(DoRequest(Verb("readkeyvalue", "EXPERIENCEID", experienceId.ToString(), "KEY", key)));

        public bool CreateKeyValue(UUID experienceId, string key, string value)
            => ParseBool(DoRequest(Verb("createkeyvalue", "EXPERIENCEID", experienceId.ToString(), "KEY", key, "VALUE", value)));

        public bool UpdateKeyValue(UUID experienceId, string key, string value, string check)
            => ParseBool(DoRequest(Verb("updatekeyvalue", "EXPERIENCEID", experienceId.ToString(), "KEY", key, "VALUE", value, "CHECK", check)));

        public bool UpdateKeyValue(UUID experienceId, string key, string value, string check, bool conditional)
            => ParseBool(DoRequest(Verb("updatekeyvalueconditional", "EXPERIENCEID", experienceId.ToString(), "KEY", key, "VALUE", value, "CHECK", check, "CONDITIONAL", conditional.ToString())));

        public bool DeleteKeyValue(UUID experienceId, string key)
            => ParseBool(DoRequest(Verb("deletekeyvalue", "EXPERIENCEID", experienceId.ToString(), "KEY", key)));

        public int KeyCountKeyValue(UUID experienceId)
            => (int)ParseLong(DoRequest(Verb("keycountkeyvalue", "EXPERIENCEID", experienceId.ToString())));

        public List<string> KeysKeyValue(UUID experienceId, int start, int count)
            => ParseStringList(DoRequest(Verb("keyskeyvalue", "EXPERIENCEID", experienceId.ToString(), "START", start.ToString(), "COUNT", count.ToString())));

        public long DataSizeKeyValue(UUID experienceId)
            => ParseLong(DoRequest(Verb("datasizekeyvalue", "EXPERIENCEID", experienceId.ToString())));

        // ── Region Allow/Block/Trust Lists ──
        public List<UUID> GetAllowedExperiences(UUID regionId)
            => ParseUUIDList(DoRequest(Verb("getallowedexperiences", "REGIONID", regionId.ToString())));

        public List<UUID> GetBlockedExperiences(UUID regionId)
            => ParseUUIDList(DoRequest(Verb("getblockedexperiences", "REGIONID", regionId.ToString())));

        public List<UUID> GetTrustedExperiences(UUID regionId)
            => ParseUUIDList(DoRequest(Verb("gettrustedexperiences", "REGIONID", regionId.ToString())));

        public bool AllowExperience(UUID regionId, UUID experienceId)
            => ParseBool(DoRequest(Verb("allowexperience", "REGIONID", regionId.ToString(), "EXPERIENCEID", experienceId.ToString())));

        public bool RemoveAllowedExperience(UUID regionId, UUID experienceId)
            => ParseBool(DoRequest(Verb("removeallowedexperience", "REGIONID", regionId.ToString(), "EXPERIENCEID", experienceId.ToString())));

        public bool BlockExperience(UUID regionId, UUID experienceId)
            => ParseBool(DoRequest(Verb("blockexperience", "REGIONID", regionId.ToString(), "EXPERIENCEID", experienceId.ToString())));

        public bool RemoveBlockedExperience(UUID regionId, UUID experienceId)
            => ParseBool(DoRequest(Verb("removeblockedexperience", "REGIONID", regionId.ToString(), "EXPERIENCEID", experienceId.ToString())));

        public bool TrustExperience(UUID regionId, UUID experienceId)
            => ParseBool(DoRequest(Verb("trustexperience", "REGIONID", regionId.ToString(), "EXPERIENCEID", experienceId.ToString())));

        public bool RemoveTrustedExperience(UUID regionId, UUID experienceId)
            => ParseBool(DoRequest(Verb("removetrustedexperience", "REGIONID", regionId.ToString(), "EXPERIENCEID", experienceId.ToString())));

        // ── Script ↔ Experience association ──
        public void SetScriptExperiencePersisted(UUID itemId, UUID experienceId, UUID regionId)
            => DoRequest(Verb("setscriptexperience", "ITEMID", itemId.ToString(), "EXPERIENCEID", experienceId.ToString(), "REGIONID", regionId.ToString()));

        public UUID GetScriptExperiencePersisted(UUID itemId)
            => ParseUUID(DoRequest(Verb("getscriptexperience", "ITEMID", itemId.ToString())));

        public void RemoveScriptExperiencePersisted(UUID itemId)
            => DoRequest(Verb("removescriptexperience", "ITEMID", itemId.ToString()));

        #endregion IExperienceService

        // ══════════════════════════════════════════════════════════════════
        // Request build + transport
        // ══════════════════════════════════════════════════════════════════

        // Build a sendData dict from METHOD + alternating key/value param pairs.
        private static Dictionary<string, object> Verb(string method, params string[] kv)
        {
            var d = new Dictionary<string, object> { ["METHOD"] = method };
            for (int i = 0; i + 1 < kv.Length; i += 2)
                d[kv[i]] = kv[i + 1];
            return d;
        }

        // CreateExperience/UpdateExperience carry the ExperienceInfo fields at top level
        // (ExperienceId, OwnerId, ...) so the G2 handler reconstructs via FromDictionary(request).
        private static Dictionary<string, object> WithInfo(string method, ExperienceInfo info)
        {
            var d = info.ToDictionary();
            d["METHOD"] = method;
            return d;
        }

        private string DoRequest(Dictionary<string, object> sendData)
        {
            string reqString = ServerUtils.BuildQueryString(sendData);
            string uri = m_ServerURI + "/experience";
            try
            {
                return SynchronousRestFormsRequester.MakeRequest("POST", uri, reqString, m_Auth);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[EXPERIENCE CONNECTOR]: Exception contacting experience server at {0}: {1}", uri, e.Message);
                return string.Empty;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Response parsing (matches the G2 handler's ServerUtils XML shapes)
        // ══════════════════════════════════════════════════════════════════

        private static Dictionary<string, object> Parse(string reply)
            => string.IsNullOrEmpty(reply) ? null : ServerUtils.ParseXmlResponse(reply);

        private static ExperienceInfo ParseInfo(string reply)
        {
            Dictionary<string, object> d = Parse(reply);
            if (d != null && d.TryGetValue("RESULT", out object v) && v is Dictionary<string, object> map)
                return ExperienceInfo.FromDictionary(map);
            return null; // NULL or Failure
        }

        private static List<ExperienceInfo> ParseInfoList(string reply)
        {
            var list = new List<ExperienceInfo>();
            Dictionary<string, object> d = Parse(reply);
            if (d != null)
            {
                foreach (object val in d.Values)
                    if (val is Dictionary<string, object> map)
                        list.Add(ExperienceInfo.FromDictionary(map));
            }
            return list;
        }

        private static bool ParseBool(string reply)
        {
            Dictionary<string, object> d = Parse(reply);
            if (d != null && d.TryGetValue("RESULT", out object v) && v != null)
                return string.Equals(v.ToString(), "True", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static long ParseLong(string reply)
        {
            Dictionary<string, object> d = Parse(reply);
            if (d != null && d.TryGetValue("RESULT", out object v) && v != null && long.TryParse(v.ToString(), out long r))
                return r;
            return 0;
        }

        // ReadKeyValue — three states resolved STRUCTURALLY, never by sniffing value content:
        //   <RESULT>..</RESULT> => the stored value, returned VERBATIM (any string, incl. "Failure"
        //                          or "" — an empty element parses back as the present empty string);
        //   <NULL>True</NULL>   => key absent -> null;
        //   <error>..</error> / empty reply => handler/transport error (RESULT absent) -> null.
        // So a stored value of "Failure" round-trips as "Failure" and is never confused with an error.
        private static string ParseString(string reply)
        {
            Dictionary<string, object> d = Parse(reply);
            if (d == null || d.ContainsKey("NULL"))
                return null;
            if (d.TryGetValue("RESULT", out object v) && v != null)
                return v.ToString();      // verbatim payload — no sentinel interpretation
            return null;                  // no RESULT (error element) => not a value
        }

        private static UUID ParseUUID(string reply)
        {
            Dictionary<string, object> d = Parse(reply);
            if (d != null && d.TryGetValue("RESULT", out object v) && v != null && UUID.TryParse(v.ToString(), out UUID r))
                return r;
            return UUID.Zero;
        }

        private static List<UUID> ParseUUIDList(string reply)
        {
            var list = new List<UUID>();
            Dictionary<string, object> d = Parse(reply);
            if (d != null)
            {
                foreach (KeyValuePair<string, object> kvp in d)
                    if (kvp.Key.StartsWith("uuid") && kvp.Value != null && UUID.TryParse(kvp.Value.ToString(), out UUID u))
                        list.Add(u);
            }
            return list;
        }

        private static List<string> ParseStringList(string reply)
        {
            var list = new List<string>();
            Dictionary<string, object> d = Parse(reply);
            if (d != null)
            {
                foreach (KeyValuePair<string, object> kvp in d)
                    if (kvp.Key.StartsWith("item") && kvp.Value != null)
                        list.Add(kvp.Value.ToString());
            }
            return list;
        }
    }
}
