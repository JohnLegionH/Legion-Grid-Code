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
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework.Http
{
    /// <summary>
    /// Modern async HTTP service for OpenSim communication
    /// Provides high-performance, async HTTP operations with proper resource management
    /// </summary>
    public interface IAsyncHttpService
    {
        /// <summary>
        /// Perform an async POST request with OSD data
        /// </summary>
        /// <param name="url">Target URL</param>
        /// <param name="data">Data to send</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <param name="compressed">Whether to compress the data</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Response data</returns>
        Task<OSDMap> PostAsync(string url, OSDMap data, int timeout, bool compressed = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Perform an async PUT request with OSD data
        /// </summary>
        /// <param name="url">Target URL</param>
        /// <param name="data">Data to send</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <param name="compressed">Whether to compress the data</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Response data</returns>
        Task<OSDMap> PutAsync(string url, OSDMap data, int timeout, bool compressed = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Perform an async GET request
        /// </summary>
        /// <param name="url">Target URL</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Response data</returns>
        Task<OSDMap> GetAsync(string url, int timeout, CancellationToken cancellationToken = default);

        /// <summary>
        /// Perform an async DELETE request
        /// </summary>
        /// <param name="url">Target URL</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Response data</returns>
        Task<OSDMap> DeleteAsync(string url, int timeout, CancellationToken cancellationToken = default);
    }
}