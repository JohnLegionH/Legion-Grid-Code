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
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework.Http
{
    /// <summary>
    /// Modern async HTTP service implementation
    /// Leverages existing WebUtil infrastructure while providing true async operations
    /// </summary>
    public class AsyncHttpService : IAsyncHttpService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        private static int RequestNumber = 0;
        private const string OSHeaderRequestID = "opensim-request-id";

        public async Task<OSDMap> PostAsync(string url, OSDMap data, int timeout, bool compressed = false, CancellationToken cancellationToken = default)
        {
            return await ServiceOSDRequestAsync(url, data, "POST", timeout, compressed, false, cancellationToken);
        }

        public async Task<OSDMap> PutAsync(string url, OSDMap data, int timeout, bool compressed = false, CancellationToken cancellationToken = default)
        {
            return await ServiceOSDRequestAsync(url, data, "PUT", timeout, compressed, false, cancellationToken);
        }

        public async Task<OSDMap> GetAsync(string url, int timeout, CancellationToken cancellationToken = default)
        {
            return await ServiceOSDRequestAsync(url, null, "GET", timeout, false, false, cancellationToken);
        }

        public async Task<OSDMap> DeleteAsync(string url, int timeout, CancellationToken cancellationToken = default)
        {
            return await ServiceOSDRequestAsync(url, null, "DELETE", timeout, false, false, cancellationToken);
        }

        private async Task<OSDMap> ServiceOSDRequestAsync(string url, OSDMap data, string method, int timeout, bool compressed, bool rpc, CancellationToken cancellationToken)
        {
            int reqnum = Interlocked.Increment(ref RequestNumber);

            if (WebUtil.DebugLevel >= 3)
                m_log.Debug($"[ASYNC HTTP]: HTTP OUT {reqnum} JSON-RPC {method} to {url}");

            string errorMessage = "unknown error";
            int ticks = Util.EnvironmentTickCount();
            int sendlen = 0;
            int rcvlen = 0;

            // Use a timeout that respects cancellation
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout > 0 ? timeout : 30000);

            HttpResponseMessage responseMessage = null;
            HttpRequestMessage request = null;
            HttpClient client = null;

            try
            {
                // Use optimized connection pool for region crossing operations
                client = OptimizedHttpService.GetRegionCrossingClient(timeout);
                request = new HttpRequestMessage(new HttpMethod(method), url);

                if (data != null)
                {
                    byte[] buffer;
                    if (WebUtil.DebugLevel >= 5)
                    {
                        string strBuffer = OSDParser.SerializeJsonString(data);
                        LogOutgoingDetail(method, reqnum, strBuffer);
                        buffer = Util.UTF8Getbytes(strBuffer);
                    }
                    else
                        buffer = OSDParser.SerializeJsonToBytes(data);

                    if (buffer.Length > 0)
                    {
                        if (compressed)
                        {
                            using var ms = new MemoryStream();
                            using (var comp = new GZipStream(ms, CompressionMode.Compress, true))
                            {
                                await comp.WriteAsync(buffer, 0, buffer.Length, timeoutCts.Token);
                            }
                            buffer = ms.ToArray();
                            request.Headers.TryAddWithoutValidation("X-Content-Encoding", "gzip");
                        }

                        sendlen = buffer.Length;
                        request.Content = new ByteArrayContent(buffer);
                        request.Content.Headers.TryAddWithoutValidation("Content-Type",
                                rpc ? "application/json-rpc" : "application/json");
                        request.Content.Headers.TryAddWithoutValidation("Content-Length", sendlen.ToString());
                    }
                }

                request.Headers.ExpectContinue = false;
                request.Headers.TransferEncodingChunked = false;
                request.Headers.TryAddWithoutValidation("Connection", "close");
                request.Headers.TryAddWithoutValidation(OSHeaderRequestID, reqnum.ToString());

                // Use the modern async SendAsync instead of sync Send
                responseMessage = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                responseMessage.EnsureSuccessStatusCode();

                var resStream = await responseMessage.Content.ReadAsStreamAsync(timeoutCts.Token);
                if (resStream != null)
                {
                    using var reader = new StreamReader(resStream);
                    string responseStr = await reader.ReadToEndAsync(timeoutCts.Token);
                    
                    if (WebUtil.DebugLevel >= 5)
                        LogResponseDetail(reqnum, responseStr);
                    
                    rcvlen = responseStr.Length;
                    return WebUtil.CanonicalizeResults(responseStr);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                errorMessage = "Operation was cancelled";
                m_log.WarnFormat("[ASYNC HTTP]: Request {0} to {1} was cancelled", reqnum, url);
                throw;
            }
            catch (OperationCanceledException)
            {
                errorMessage = "Request timed out";
                m_log.WarnFormat("[ASYNC HTTP]: Request {0} to {1} timed out after {2}ms", reqnum, url, timeout);
                throw new TimeoutException($"HTTP request to {url} timed out after {timeout}ms");
            }
            catch (HttpRequestException e)
            {
                int Status = e.StatusCode is null ? 499 : (int)e.StatusCode;
                errorMessage = $"[{Status}] {e.Message}";
                m_log.WarnFormat("[ASYNC HTTP]: Request {0} failed: {1}", reqnum, errorMessage);
                throw;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                m_log.ErrorFormat("[ASYNC HTTP]: Exception making request {0}: {1}", reqnum, errorMessage);
                throw;
            }
            finally
            {
                request?.Dispose();
                responseMessage?.Dispose();
                client?.Dispose();

                ticks = Util.EnvironmentTickCountSubtract(ticks);
                
                // Report performance metrics to EntityTransferModule if available
                bool success = responseMessage?.IsSuccessStatusCode == true;
                bool wasTimeout = cancellationToken.IsCancellationRequested;
                
                // Use reflection to avoid hard dependency
                try
                {
                    var etmType = Type.GetType("OpenSim.Region.CoreModules.Framework.EntityTransfer.EntityTransferModule, OpenSim.Region.CoreModules");
                    if (etmType != null)
                    {
                        var reportMethod = etmType.GetMethod("ReportHttpRequest", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        reportMethod?.Invoke(null, new object[] { (double)ticks, success, wasTimeout });
                    }
                }
                catch (Exception ex)
                {
                    // Silently ignore reflection errors to avoid breaking HTTP functionality
                    if (WebUtil.DebugLevel >= 1)
                        m_log.DebugFormat("[ASYNC HTTP]: Could not report performance metrics: {0}", ex.Message);
                }
                
                if (ticks > WebUtil.LongCallTime)
                {
                    m_log.InfoFormat(
                        "[ASYNC HTTP]: Slow HTTP request {0} {1} to {2} took {3}ms, {4}/{5} bytes",
                        reqnum, method, url, ticks, sendlen, rcvlen);
                }
                else if (WebUtil.DebugLevel >= 4)
                {
                    m_log.DebugFormat(
                        "[ASYNC HTTP]: HTTP {0} {1} to {2} took {3}ms, {4}/{5} bytes",
                        reqnum, method, url, ticks, sendlen, rcvlen);
                }
            }

            return new OSDMap();
        }

        private static void LogOutgoingDetail(string method, int reqnum, string data)
        {
            if (WebUtil.DebugLevel < 5) return;
            
            string truncated = data.Length > WebUtil.MaxRequestDiagLength ? 
                data.Substring(0, WebUtil.MaxRequestDiagLength) + "..." : data;
            m_log.DebugFormat("[ASYNC HTTP]: HTTP OUT {0} {1} detail:\n{2}", reqnum, method, truncated);
        }

        private static void LogResponseDetail(int reqnum, string data)
        {
            if (WebUtil.DebugLevel < 5) return;
            
            string truncated = data.Length > WebUtil.MaxRequestDiagLength ?
                data.Substring(0, WebUtil.MaxRequestDiagLength) + "..." : data;
            m_log.DebugFormat("[ASYNC HTTP]: HTTP IN {0} response detail:\n{1}", reqnum, truncated);
        }
    }
}