/*
 * Legion Grid — Experience System (grid-service topology)
 * ExperienceServerPostHandler.cs — Robust POST /experience dispatch.
 *
 * G2 of the Experience grid-service rebuild. Dispatches a METHOD-verb POST to Legion's
 * IExperienceService and serializes results back over the ServerUtils XML envelope (the same
 * form-in / XML-out convention as GridUserServerPostHandler and every other Robust service).
 * Serves the FULL IExperienceService surface so the G3 Remote connector can implement the
 * interface completely. Server side only — no region behavior changes here.
 *
 * Wire values use ExperienceInfo's LOSSLESS ToDictionary/FromDictionary (raw fields), NOT the
 * viewer-shaped caps OSD — this is a service↔service wire; the region caps do the viewer transform.
 *
 * Handler TOPOLOGY / wire-protocol shape credit: adapted from the OpenSim-NGC /
 * OpenSim-Tranquillity Experience server handler (orig. StolenRuby; integ. Mike Dickson /
 * OpenSim-NGC, Utopia Skye). Implementation is Legion Grid's own.
 */

using Nini.Config;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;

namespace OpenSim.Server.Handlers.Experience
{
    public class ExperienceServerPostHandler : BaseStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IExperienceService m_ExperienceService;

        public ExperienceServerPostHandler(IExperienceService service, IServiceAuth auth) :
                base("POST", "/experience", auth)
        {
            m_ExperienceService = service;
        }

        protected override byte[] ProcessRequest(string path, Stream requestData,
                IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            string body;
            using (StreamReader sr = new StreamReader(requestData))
                body = sr.ReadToEnd();
            body = body.Trim();

            string method = string.Empty;
            try
            {
                Dictionary<string, object> request = ServerUtils.ParseQueryString(body);

                if (!request.ContainsKey("METHOD"))
                    return FailureResult();

                method = request["METHOD"].ToString();

                switch (method)
                {
                    // ── Experience CRUD ──
                    case "getexperience":            return GetExperience(request);
                    case "getexperiencebyname":      return GetExperienceByName(request);
                    case "createexperience":         return CreateExperience(request);
                    case "updateexperience":         return UpdateExperience(request);
                    case "deleteexperience":         return DeleteExperience(request);
                    case "getexperiencesbyowner":    return GetExperiencesByOwner(request);
                    case "getexperiencesbygroup":    return GetExperiencesByGroup(request);
                    case "isexperiencecontributor":  return IsExperienceContributor(request);
                    case "isexperienceadmin":        return IsExperienceAdmin(request);
                    case "findexperiences":          return FindExperiences(request);
                    case "findexperiencespaged":     return FindExperiencesPaged(request);

                    // ── Permission Grants ──
                    case "isagentgranted":           return IsAgentGranted(request);
                    case "isagentblocked":           return IsAgentBlocked(request);
                    case "grantpermission":          return GrantPermission(request);
                    case "denypermission":           return DenyPermission(request);
                    case "forgetpermission":         return ForgetPermission(request);
                    case "getagentexperiences":      return GetAgentExperiences(request);
                    case "getagentblockedexperiences": return GetAgentBlockedExperiences(request);
                    case "blockexperienceforagent":  return BlockExperienceForAgent(request);
                    case "unblockexperienceforagent": return UnblockExperienceForAgent(request);

                    // ── Key-Value Store ──
                    case "readkeyvalue":             return ReadKeyValue(request);
                    case "createkeyvalue":           return CreateKeyValue(request);
                    case "updatekeyvalue":           return UpdateKeyValue(request);
                    case "updatekeyvalueconditional": return UpdateKeyValueConditional(request);
                    case "deletekeyvalue":           return DeleteKeyValue(request);
                    case "keycountkeyvalue":         return KeyCountKeyValue(request);
                    case "keyskeyvalue":             return KeysKeyValue(request);
                    case "datasizekeyvalue":         return DataSizeKeyValue(request);

                    // ── Region Allow/Block/Trust Lists ──
                    case "getallowedexperiences":    return GetAllowedExperiences(request);
                    case "getblockedexperiences":    return GetBlockedExperiences(request);
                    case "gettrustedexperiences":    return GetTrustedExperiences(request);
                    case "allowexperience":          return AllowExperience(request);
                    case "removeallowedexperience":  return RemoveAllowedExperience(request);
                    case "blockexperience":          return BlockExperience(request);
                    case "removeblockedexperience":  return RemoveBlockedExperience(request);
                    case "trustexperience":          return TrustExperience(request);
                    case "removetrustedexperience":  return RemoveTrustedExperience(request);

                    // ── Script ↔ Experience association ──
                    case "setscriptexperience":      return SetScriptExperience(request);
                    case "getscriptexperience":      return GetScriptExperience(request);
                    case "removescriptexperience":   return RemoveScriptExperience(request);
                }

                m_log.DebugFormat("[EXPERIENCE HANDLER]: unknown method request: {0}", method);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[EXPERIENCE HANDLER]: Exception in method {0}: {1}", method, e);
            }

            return FailureResult();
        }

        // ══════════════════════════════════════════════════════════════════
        // Experience CRUD
        // ══════════════════════════════════════════════════════════════════

        byte[] GetExperience(Dictionary<string, object> r)
            => InfoResult(m_ExperienceService.GetExperience(GetUUID(r, "EXPERIENCEID")));

        byte[] GetExperienceByName(Dictionary<string, object> r)
            => InfoResult(m_ExperienceService.GetExperienceByName(GetStr(r, "NAME")));

        byte[] CreateExperience(Dictionary<string, object> r)
            => InfoResult(m_ExperienceService.CreateExperience(ExperienceInfo.FromDictionary(r)));

        byte[] UpdateExperience(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.UpdateExperience(ExperienceInfo.FromDictionary(r)));

        byte[] DeleteExperience(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.DeleteExperience(GetUUID(r, "EXPERIENCEID")));

        byte[] GetExperiencesByOwner(Dictionary<string, object> r)
            => InfoListResult(m_ExperienceService.GetExperiencesByOwner(GetUUID(r, "OWNERID")));

        byte[] GetExperiencesByGroup(Dictionary<string, object> r)
            => InfoListResult(m_ExperienceService.GetExperiencesByGroup(GetUUID(r, "GROUPID")));

        byte[] IsExperienceContributor(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.IsExperienceContributor(GetUUID(r, "EXPERIENCEID"), GetUUID(r, "AGENTID")));

        byte[] IsExperienceAdmin(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.IsExperienceAdmin(GetUUID(r, "EXPERIENCEID"), GetUUID(r, "AGENTID")));

        byte[] FindExperiences(Dictionary<string, object> r)
            => InfoListResult(m_ExperienceService.FindExperiences(GetStr(r, "QUERY")));

        byte[] FindExperiencesPaged(Dictionary<string, object> r)
            => InfoListResult(m_ExperienceService.FindExperiences(GetStr(r, "QUERY"), GetInt(r, "OFFSET"), GetInt(r, "LIMIT")));

        // ══════════════════════════════════════════════════════════════════
        // Permission Grants
        // ══════════════════════════════════════════════════════════════════

        byte[] IsAgentGranted(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.IsAgentGranted(GetUUID(r, "EXPERIENCEID"), GetUUID(r, "AGENTID")));

        byte[] IsAgentBlocked(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.IsAgentBlocked(GetUUID(r, "EXPERIENCEID"), GetUUID(r, "AGENTID")));

        byte[] GrantPermission(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.GrantPermission(GetUUID(r, "EXPERIENCEID"), GetUUID(r, "AGENTID")));

        byte[] DenyPermission(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.DenyPermission(GetUUID(r, "EXPERIENCEID"), GetUUID(r, "AGENTID")));

        byte[] ForgetPermission(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.ForgetPermission(GetUUID(r, "EXPERIENCEID"), GetUUID(r, "AGENTID")));

        byte[] GetAgentExperiences(Dictionary<string, object> r)
            => UUIDListResult(m_ExperienceService.GetAgentExperiences(GetUUID(r, "AGENTID")));

        byte[] GetAgentBlockedExperiences(Dictionary<string, object> r)
            => UUIDListResult(m_ExperienceService.GetAgentBlockedExperiences(GetUUID(r, "AGENTID")));

        byte[] BlockExperienceForAgent(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.BlockExperienceForAgent(GetUUID(r, "AGENTID"), GetUUID(r, "EXPERIENCEID")));

        byte[] UnblockExperienceForAgent(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.UnblockExperienceForAgent(GetUUID(r, "AGENTID"), GetUUID(r, "EXPERIENCEID")));

        // ══════════════════════════════════════════════════════════════════
        // Key-Value Store
        // ══════════════════════════════════════════════════════════════════

        byte[] ReadKeyValue(Dictionary<string, object> r)
            => StringResult(m_ExperienceService.ReadKeyValue(GetUUID(r, "EXPERIENCEID"), GetStr(r, "KEY")));

        byte[] CreateKeyValue(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.CreateKeyValue(GetUUID(r, "EXPERIENCEID"), GetStr(r, "KEY"), GetStr(r, "VALUE")));

        byte[] UpdateKeyValue(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.UpdateKeyValue(GetUUID(r, "EXPERIENCEID"), GetStr(r, "KEY"), GetStr(r, "VALUE"), GetStr(r, "CHECK")));

        byte[] UpdateKeyValueConditional(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.UpdateKeyValue(GetUUID(r, "EXPERIENCEID"), GetStr(r, "KEY"), GetStr(r, "VALUE"), GetStr(r, "CHECK"), GetBool(r, "CONDITIONAL")));

        byte[] DeleteKeyValue(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.DeleteKeyValue(GetUUID(r, "EXPERIENCEID"), GetStr(r, "KEY")));

        byte[] KeyCountKeyValue(Dictionary<string, object> r)
            => IntResult(m_ExperienceService.KeyCountKeyValue(GetUUID(r, "EXPERIENCEID")));

        byte[] KeysKeyValue(Dictionary<string, object> r)
            => StringListResult(m_ExperienceService.KeysKeyValue(GetUUID(r, "EXPERIENCEID"), GetInt(r, "START"), GetInt(r, "COUNT")));

        byte[] DataSizeKeyValue(Dictionary<string, object> r)
            => IntResult(m_ExperienceService.DataSizeKeyValue(GetUUID(r, "EXPERIENCEID")));

        // ══════════════════════════════════════════════════════════════════
        // Region Allow/Block/Trust Lists
        // ══════════════════════════════════════════════════════════════════

        byte[] GetAllowedExperiences(Dictionary<string, object> r)
            => UUIDListResult(m_ExperienceService.GetAllowedExperiences(GetUUID(r, "REGIONID")));

        byte[] GetBlockedExperiences(Dictionary<string, object> r)
            => UUIDListResult(m_ExperienceService.GetBlockedExperiences(GetUUID(r, "REGIONID")));

        byte[] GetTrustedExperiences(Dictionary<string, object> r)
            => UUIDListResult(m_ExperienceService.GetTrustedExperiences(GetUUID(r, "REGIONID")));

        byte[] AllowExperience(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.AllowExperience(GetUUID(r, "REGIONID"), GetUUID(r, "EXPERIENCEID")));

        byte[] RemoveAllowedExperience(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.RemoveAllowedExperience(GetUUID(r, "REGIONID"), GetUUID(r, "EXPERIENCEID")));

        byte[] BlockExperience(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.BlockExperience(GetUUID(r, "REGIONID"), GetUUID(r, "EXPERIENCEID")));

        byte[] RemoveBlockedExperience(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.RemoveBlockedExperience(GetUUID(r, "REGIONID"), GetUUID(r, "EXPERIENCEID")));

        byte[] TrustExperience(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.TrustExperience(GetUUID(r, "REGIONID"), GetUUID(r, "EXPERIENCEID")));

        byte[] RemoveTrustedExperience(Dictionary<string, object> r)
            => BoolResult(m_ExperienceService.RemoveTrustedExperience(GetUUID(r, "REGIONID"), GetUUID(r, "EXPERIENCEID")));

        // ══════════════════════════════════════════════════════════════════
        // Script ↔ Experience association
        // ══════════════════════════════════════════════════════════════════

        byte[] SetScriptExperience(Dictionary<string, object> r)
        {
            m_ExperienceService.SetScriptExperiencePersisted(GetUUID(r, "ITEMID"), GetUUID(r, "EXPERIENCEID"), GetUUID(r, "REGIONID"));
            return BoolResult(true);
        }

        byte[] GetScriptExperience(Dictionary<string, object> r)
            => UUIDResult(m_ExperienceService.GetScriptExperiencePersisted(GetUUID(r, "ITEMID")));

        byte[] RemoveScriptExperience(Dictionary<string, object> r)
        {
            m_ExperienceService.RemoveScriptExperiencePersisted(GetUUID(r, "ITEMID"));
            return BoolResult(true);
        }

        // ══════════════════════════════════════════════════════════════════
        // Request-param helpers
        // ══════════════════════════════════════════════════════════════════

        private static UUID GetUUID(Dictionary<string, object> r, string key)
        {
            UUID u = UUID.Zero;
            if (r.TryGetValue(key, out object v) && v != null)
                UUID.TryParse(v.ToString(), out u);
            return u;
        }

        private static string GetStr(Dictionary<string, object> r, string key)
            => (r.TryGetValue(key, out object v) && v != null) ? v.ToString() : string.Empty;

        private static int GetInt(Dictionary<string, object> r, string key)
        {
            int i = 0;
            if (r.TryGetValue(key, out object v) && v != null)
                int.TryParse(v.ToString(), out i);
            return i;
        }

        private static bool GetBool(Dictionary<string, object> r, string key)
        {
            bool b = false;
            if (r.TryGetValue(key, out object v) && v != null)
                bool.TryParse(v.ToString(), out b);
            return b;
        }

        // ══════════════════════════════════════════════════════════════════
        // Response builders (ServerUtils XML envelope; matches the region-side connector parse)
        // ══════════════════════════════════════════════════════════════════

        private static byte[] Resp(Dictionary<string, object> result)
            => Util.UTF8NoBomEncoding.GetBytes(ServerUtils.BuildXmlResponse(result));

        private static byte[] BoolResult(bool value)
            => Resp(new Dictionary<string, object> { ["RESULT"] = value ? "True" : "False" });

        private static byte[] IntResult(long value)
            => Resp(new Dictionary<string, object> { ["RESULT"] = value.ToString() });

        private static byte[] UUIDResult(UUID value)
            => Resp(new Dictionary<string, object> { ["RESULT"] = value.ToString() });

        // ReadKeyValue distinguishes a missing key (null) from a stored empty string.
        private static byte[] StringResult(string value)
        {
            var r = new Dictionary<string, object>();
            if (value == null)
                r["NULL"] = "True";
            else
                r["RESULT"] = value;
            return Resp(r);
        }

        private static byte[] InfoResult(ExperienceInfo info)
        {
            var r = new Dictionary<string, object>();
            if (info == null)
                r["NULL"] = "True";
            else
                r["RESULT"] = info.ToDictionary();
            return Resp(r);
        }

        private static byte[] InfoListResult(List<ExperienceInfo> list)
        {
            var r = new Dictionary<string, object>();
            if (list != null)
            {
                int i = 0;
                foreach (ExperienceInfo e in list)
                {
                    if (e == null) continue;
                    r["exp" + i] = e.ToDictionary();
                    i++;
                }
            }
            return Resp(r);
        }

        private static byte[] UUIDListResult(List<UUID> list)
        {
            var r = new Dictionary<string, object>();
            if (list != null)
            {
                int i = 0;
                foreach (UUID u in list)
                {
                    r["uuid" + i] = u.ToString();
                    i++;
                }
            }
            return Resp(r);
        }

        private static byte[] StringListResult(List<string> list)
        {
            var r = new Dictionary<string, object>();
            if (list != null)
            {
                int i = 0;
                foreach (string s in list)
                {
                    r["item" + i] = s;
                    i++;
                }
            }
            return Resp(r);
        }

        private static byte[] FailureResult()
            => Resp(new Dictionary<string, object> { ["RESULT"] = "Failure" });
    }
}
