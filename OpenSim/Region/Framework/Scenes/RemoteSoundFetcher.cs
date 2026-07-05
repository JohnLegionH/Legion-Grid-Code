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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.Framework.Scenes
{
    /// <summary>
    /// Shared fetch/validate/play helper behind the osPlaySoundURL script function.
    /// Engine-agnostic: both the YEngine OSSL API and the Phlox LSLSystemAPI call the
    /// same <see cref="Play"/> so security and behavior are identical across engines.
    ///
    /// Security model: synchronous validation (URL shape, scheme, domain policy, SSRF,
    /// rate limits) runs on the caller thread and its result is the return string. The
    /// network fetch (HEAD/GET, content-type/size enforcement, asset store, playback)
    /// runs on the thread pool so the script scheduler is never blocked; async failures
    /// are reported through the caller-supplied error callback and the log.
    /// </summary>
    public class RemoteSoundFetcher
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ConcurrentDictionary<UUID, RemoteSoundFetcher> s_perScene = new();

        // Per-request pinned endpoint is stashed here so the shared handler's
        // ConnectCallback dials the exact IP we validated (closes the DNS-rebinding /
        // TOCTOU window between validation and connect).
        private static readonly HttpRequestOptionsKey<IPEndPoint> PinnedEndpointKey =
            new("RemoteSound.PinnedEndpoint");

        private readonly Scene m_scene;
        private readonly OutboundUrlFilter m_filter;
        private readonly HttpClient m_http;

        private readonly bool m_enabled;
        private readonly bool m_allowHttp;
        private readonly long m_maxSizeBytes;
        private readonly double m_perObjectPerMinute;
        private readonly double m_perRegionPerMinute;
        private readonly int m_cacheSeconds;
        private readonly HashSet<string> m_allowedDomains;   // optional policy on top of the filter
        private readonly HashSet<string> m_deniedDomains;

        private const int RedirectCap = 5;
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

        private static readonly HashSet<string> OggContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "audio/ogg", "application/ogg"
        };

        // url -> (assetId, expiryTicks)
        private readonly ConcurrentDictionary<string, (UUID id, long expires)> m_cache = new();
        // rate buckets
        private readonly ConcurrentDictionary<UUID, RateBucket> m_objectBuckets = new();
        private readonly RateBucket m_regionBucket;

        public bool Enabled => m_enabled;

        public static RemoteSoundFetcher GetForScene(Scene scene, IConfigSource config)
        {
            return s_perScene.GetOrAdd(scene.RegionInfo.RegionID, _ => new RemoteSoundFetcher(scene, config));
        }

        private RemoteSoundFetcher(Scene scene, IConfigSource config)
        {
            m_scene = scene;

            IConfig cfg = config.Configs["RemoteSound"];
            m_enabled            = cfg != null && cfg.GetBoolean("Enabled", false);
            m_allowHttp          = cfg != null && cfg.GetBoolean("AllowHttp", false);
            m_maxSizeBytes       = cfg != null ? cfg.GetLong("MaxSizeBytes", 10L * 1024 * 1024) : 10L * 1024 * 1024;
            m_perObjectPerMinute = cfg != null ? cfg.GetDouble("PerObjectRatePerMinute", 6.0) : 6.0;
            m_perRegionPerMinute = cfg != null ? cfg.GetDouble("PerRegionRatePerMinute", 60.0) : 60.0;
            m_cacheSeconds       = cfg != null ? cfg.GetInt("CacheSeconds", 300) : 300;
            m_allowedDomains     = ParseDomains(cfg?.GetString("AllowedDomains", string.Empty));
            m_deniedDomains      = ParseDomains(cfg?.GetString("DeniedDomains", string.Empty));

            // Same SSRF floor llHTTPRequest uses: default blacklist (loopback, RFC1918,
            // link-local incl. 169.254.169.254 metadata, multicast, ...) plus any
            // [Network] OutboundDisallowForUserScripts additions.
            m_filter = new OutboundUrlFilter("RemoteSound outbound filter", config);

            m_regionBucket = new RateBucket(m_perRegionPerMinute);

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,               // we follow manually and re-validate each hop
                AutomaticDecompression = DecompressionMethods.None,
                ConnectCallback = PinnedConnectAsync,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            m_http = new HttpClient(handler) { Timeout = FetchTimeout };
        }

        /// <summary>
        /// Synchronous security validation + (on accept) background fetch/play.
        /// Returns empty string on accept, otherwise a script-visible reason.
        /// </summary>
        public string Play(SceneObjectPart host, UUID target, string url, double volume, Action<string> reportAsyncError)
        {
            if (!m_enabled)
                return "osPlaySoundURL is disabled on this region";
            if (host == null)
                return "no host";

            volume = Math.Clamp(volume, 0.0, 1.0);

            if (string.IsNullOrEmpty(url) || url.Length > 2048)
                return "Invalid URL";
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                return "Invalid URL";

            if (uri.Scheme == Uri.UriSchemeHttps) { /* ok */ }
            else if (uri.Scheme == Uri.UriSchemeHttp)
            {
                if (!m_allowHttp)
                    return "HTTPS is required";
            }
            else
                return "Unsupported URL scheme";

            if (!DomainAllowed(uri.DnsSafeHost))
                return "Domain not permitted";

            // SSRF: resolve ONCE, validate that exact IP, and remember it so the fetch
            // connects to the same address we approved (no re-resolve, no rebinding).
            if (!TryResolveAndValidate(uri, out IPEndPoint pinned, out string ssrfError))
                return ssrfError;

            // Rate limits (per-object then per-region).
            RateBucket objBucket = m_objectBuckets.GetOrAdd(host.UUID, _ => new RateBucket(m_perObjectPerMinute));
            if (!objBucket.TryConsume())
                return "Per-object remote sound rate exceeded";
            if (!m_regionBucket.TryConsume())
                return "Region remote sound rate exceeded";

            // Cache hit: play immediately, no fetch.
            if (m_cache.TryGetValue(url, out var cached) && cached.expires > DateTime.UtcNow.Ticks)
            {
                PlayAsset(cached.id, host, volume);
                return string.Empty;
            }

            // Accepted. The network fetch runs off the scheduler thread; async failures
            // (content-type, size, network) surface through reportAsyncError + log.
            Uri capturedUri = uri;
            IPEndPoint capturedPin = pinned;
            SceneObjectPart capturedHost = host;
            string capturedUrl = url;
            double capturedVolume = volume;

            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    FetchAndPlay(capturedUrl, capturedUri, capturedPin, capturedHost, capturedVolume, reportAsyncError);
                }
                catch (Exception e)
                {
                    // Never let an exception escape a thread-pool work item (process kill on .NET Core+).
                    m_log.Warn($"[REMOTE SOUND]: fetch failed for {capturedUrl}: {e.Message}");
                    SafeReport(reportAsyncError, "Remote sound fetch failed");
                }
            }, null);

            return string.Empty;
        }

        // ---- fetch pipeline (background thread) ----------------------------------

        private void FetchAndPlay(string cacheKey, Uri uri, IPEndPoint pinned, SceneObjectPart host,
            double volume, Action<string> reportAsyncError)
        {
            int redirects = 0;
            while (true)
            {
                using var head = BuildRequest(HttpMethod.Head, uri, pinned);
                using HttpResponseMessage headResp = m_http.Send(head, HttpCompletionOption.ResponseHeadersRead);

                if (IsRedirect(headResp.StatusCode))
                {
                    if (++redirects > RedirectCap) { SafeReport(reportAsyncError, "Too many redirects"); return; }
                    if (!FollowRedirect(headResp, ref uri, out pinned, out string rErr)) { SafeReport(reportAsyncError, rErr); return; }
                    continue;
                }

                if (!headResp.IsSuccessStatusCode) { SafeReport(reportAsyncError, $"Remote returned {(int)headResp.StatusCode}"); return; }

                string ctype = headResp.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrEmpty(ctype) && !OggContentTypes.Contains(ctype))
                { SafeReport(reportAsyncError, $"Unsupported content-type: {ctype}; only Ogg Vorbis is accepted"); return; }

                long? len = headResp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > m_maxSizeBytes)
                { SafeReport(reportAsyncError, "Remote audio exceeds size limit"); return; }
                break;
            }

            using var get = BuildRequest(HttpMethod.Get, uri, pinned);
            using HttpResponseMessage resp = m_http.Send(get, HttpCompletionOption.ResponseHeadersRead);

            if (IsRedirect(resp.StatusCode))
            {
                if (++redirects > RedirectCap) { SafeReport(reportAsyncError, "Too many redirects"); return; }
                if (!FollowRedirect(resp, ref uri, out pinned, out string rErr)) { SafeReport(reportAsyncError, rErr); return; }
                FetchAndPlay(cacheKey, uri, pinned, host, volume, reportAsyncError);
                return;
            }
            if (!resp.IsSuccessStatusCode) { SafeReport(reportAsyncError, $"Remote returned {(int)resp.StatusCode}"); return; }

            string getCtype = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(getCtype) || !OggContentTypes.Contains(getCtype))
            { SafeReport(reportAsyncError, $"Unsupported content-type: {getCtype ?? "(none)"}; only Ogg Vorbis is accepted"); return; }

            byte[] data = ReadCapped(resp, m_maxSizeBytes, out bool overLimit);
            if (overLimit) { SafeReport(reportAsyncError, "Remote audio exceeds size limit"); return; }
            if (!IsOggVorbis(data)) { SafeReport(reportAsyncError, "Data is not an Ogg Vorbis file"); return; }

            // AssetType.Sound (1) is Ogg Vorbis in SL/OpenSim — the only sound format the
            // viewer decodes for playback. The bytes are Ogg, so this is the correct type
            // (SoundWAV/17 would only be served for a snd_wav request the viewer never makes).
            AssetBase asset = new(UUID.Random(), "osPlaySoundURL", (sbyte)AssetType.Sound, host.OwnerID.ToString())
            {
                Data = data,
                Temporary = true
            };
            m_scene.AssetService.Store(asset);

            long expiry = DateTime.UtcNow.AddSeconds(m_cacheSeconds).Ticks;
            m_cache[cacheKey] = (asset.FullID, expiry);

            PlayAsset(asset.FullID, host, volume);
        }

        private void PlayAsset(UUID soundId, SceneObjectPart host, double volume)
        {
            ISoundModule sound = m_scene.RequestModuleInterface<ISoundModule>();
            if (sound == null) return;
            SceneObjectGroup grp = host.ParentGroup;
            sound.TriggerSound(soundId, host.OwnerID, host.UUID,
                grp != null ? grp.UUID : host.UUID,
                volume, host.AbsolutePosition, m_scene.RegionInfo.RegionHandle);
        }

        // ---- SSRF: resolve once, validate the exact IP, pin it -------------------

        private bool TryResolveAndValidate(Uri uri, out IPEndPoint pinned, out string error)
        {
            pinned = null;
            error = string.Empty;

            IPAddress[] addrs;
            try { addrs = Dns.GetHostAddresses(uri.DnsSafeHost); }
            catch { error = "Host could not be resolved"; return false; }

            // OutboundUrlFilter is IPv4-only; IPv6-only hosts fail closed (acceptable).
            IPAddress ipv4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 == null) { error = "Host has no permitted IPv4 address"; return false; }

            // Validate the resolved literal against the same blacklist llHTTPRequest uses.
            // Feeding an IP literal makes the filter's internal Dns.GetHostAddresses return
            // that exact IP, so we approve precisely the address we will connect to.
            var ipUri = new UriBuilder(uri) { Host = ipv4.ToString() }.Uri;
            if (!m_filter.CheckAllowed(ipUri)) { error = $"Request to {uri.Host} disallowed by filter"; return false; }

            pinned = new IPEndPoint(ipv4, uri.Port);
            return true;
        }

        private bool FollowRedirect(HttpResponseMessage resp, ref Uri uri, out IPEndPoint pinned, out string error)
        {
            pinned = null;
            error = string.Empty;
            Uri loc = resp.Headers.Location;
            if (loc == null) { error = "Redirect without Location"; return false; }
            if (!loc.IsAbsoluteUri) loc = new Uri(uri, loc);

            if (loc.Scheme != Uri.UriSchemeHttps && !(loc.Scheme == Uri.UriSchemeHttp && m_allowHttp))
            { error = "Redirect to unsupported scheme"; return false; }
            if (!DomainAllowed(loc.DnsSafeHost)) { error = "Redirect domain not permitted"; return false; }
            if (!TryResolveAndValidate(loc, out pinned, out error)) return false;

            uri = loc;
            return true;
        }

        private ValueTask<Stream> PinnedConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
        {
            IPEndPoint ep = null;
            ctx.InitialRequestMessage.Options.TryGetValue(PinnedEndpointKey, out ep);
            // No pinned endpoint should never happen (we always set it); fail closed.
            if (ep == null)
                throw new HttpRequestException("Remote sound connect had no pinned endpoint");
            return ConnectTcpAsync(ep, ct);
        }

        private static async ValueTask<Stream> ConnectTcpAsync(IPEndPoint ep, CancellationToken ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(ep, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static HttpRequestMessage BuildRequest(HttpMethod method, Uri uri, IPEndPoint pinned)
        {
            // RequestUri keeps the hostname (correct Host header + TLS SNI + cert check),
            // but the connection is dialed to the pinned IP via ConnectCallback.
            var req = new HttpRequestMessage(method, uri);
            req.Options.Set(PinnedEndpointKey, pinned);
            req.Headers.TryAddWithoutValidation("User-Agent", "LegionGrid-osPlaySoundURL/1.0");
            return req;
        }

        // ---- helpers -------------------------------------------------------------

        private bool DomainAllowed(string host)
        {
            if (m_deniedDomains.Contains(host)) return false;
            if (m_allowedDomains.Count > 0 && !m_allowedDomains.Contains(host)) return false;
            return true;
        }

        private static bool IsRedirect(HttpStatusCode c)
            => c == HttpStatusCode.MovedPermanently || c == HttpStatusCode.Found
            || c == HttpStatusCode.SeeOther || c == HttpStatusCode.TemporaryRedirect
            || c == HttpStatusCode.PermanentRedirect;

        private static byte[] ReadCapped(HttpResponseMessage resp, long cap, out bool overLimit)
        {
            overLimit = false;
            using Stream s = resp.Content.ReadAsStream();
            using var ms = new MemoryStream();
            byte[] buf = new byte[8192];
            int n;
            long total = 0;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > cap) { overLimit = true; return Array.Empty<byte>(); }
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }

        // Ogg bitstream starts with the capture pattern "OggS" (0x4F 0x67 0x67 0x53).
        private static bool IsOggVorbis(byte[] d)
            => d != null && d.Length >= 4
               && d[0] == (byte)'O' && d[1] == (byte)'g' && d[2] == (byte)'g' && d[3] == (byte)'S';

        private static HashSet<string> ParseDomains(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(csv)) return set;
            foreach (string part in csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                set.Add(part.Trim());
            return set;
        }

        private static void SafeReport(Action<string> report, string msg)
        {
            try { report?.Invoke(msg); } catch { /* engine chat can throw; never propagate */ }
            m_log.Debug($"[REMOTE SOUND]: {msg}");
        }

        /// <summary>Simple per-minute token bucket.</summary>
        private sealed class RateBucket
        {
            private readonly double m_ratePerSec;
            private readonly double m_burst;
            private double m_tokens;
            private double m_lastSec;
            private readonly object m_lock = new();

            public RateBucket(double perMinute)
            {
                m_ratePerSec = perMinute / 60.0;
                m_burst = Math.Max(1.0, perMinute);
                m_tokens = m_burst;
                m_lastSec = Util.GetTimeStamp();
            }

            public bool TryConsume()
            {
                lock (m_lock)
                {
                    double now = Util.GetTimeStamp();
                    m_tokens = Math.Min(m_burst, m_tokens + (now - m_lastSec) * m_ratePerSec);
                    m_lastSec = now;
                    if (m_tokens < 1.0) return false;
                    m_tokens -= 1.0;
                    return true;
                }
            }
        }
    }
}
