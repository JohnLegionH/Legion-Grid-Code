/*
 * Legion Grid — Experience System (grid-service topology)
 * ExperienceServiceServerConnector.cs — Robust IN connector.
 *
 * G2 of the Experience grid-service rebuild: expose Legion's IExperienceService over HTTP
 * on the Robust server. Loads the ExperienceService plugin (via [ExperienceService]
 * LocalServiceModule) and registers the POST /experience stream handler. This is the SERVER
 * side only — it changes no region behavior; regions continue to use the G1 Local connector
 * until G3 adds the Remote connector.
 *
 * Handler TOPOLOGY / wire-protocol shape credit: adapted from the OpenSim-NGC /
 * OpenSim-Tranquillity Experience server handler (original scaffold by StolenRuby; integration
 * by Mike Dickson / OpenSim-NGC, Utopia Skye) — a METHOD-verb dispatch over POST /experience,
 * which is also Legion's native ServerUtils Robust convention (mirrors GridUserServiceConnector).
 * Implementation here is Legion Grid's own, serving Legion's ~40-method IExperienceService.
 */

using System;
using Nini.Config;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

namespace OpenSim.Server.Handlers.Experience
{
    public class ExperienceServiceServerConnector : ServiceConnector
    {
        private IExperienceService m_ExperienceService;
        private string m_ConfigName = "ExperienceService";

        public ExperienceServiceServerConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            if (!string.IsNullOrEmpty(configName))
                m_ConfigName = configName;

            IConfig serverConfig = config.Configs[m_ConfigName];
            if (serverConfig == null)
                throw new Exception(String.Format("No section {0} in config file", m_ConfigName));

            string service = serverConfig.GetString("LocalServiceModule", String.Empty);

            if (service.Length == 0)
                throw new Exception("No LocalServiceModule in config file");

            Object[] args = new Object[] { config };
            m_ExperienceService = ServerUtils.LoadPlugin<IExperienceService>(service, args);

            IServiceAuth auth = ServiceAuth.Create(config, m_ConfigName);

            server.AddStreamHandler(new ExperienceServerPostHandler(m_ExperienceService, auth));
        }
    }
}
