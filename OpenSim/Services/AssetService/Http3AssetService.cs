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
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Services.AssetService
{
    /// <summary>
    /// Modern HTTP/3 Asset Service with QUIC protocol support
    /// Provides high-performance asset delivery with multiplexing, compression, and caching
    /// Designed to work alongside existing asset services for gradual migration
    /// </summary>
    public class Http3AssetService : AssetServiceBase, IAssetService, IDisposable
    {
        #region Private Fields

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Core asset service for fallback compatibility
        private readonly IAssetService m_fallbackAssetService;
        
        // HTTP/3 client for modern asset delivery
        private readonly HttpClient m_http3Client;
        
        // High-performance asset cache with LRU eviction
        private readonly ConcurrentDictionary<string, Http3CachedAsset> m_assetCache = new();
        private readonly ConcurrentDictionary<string, DateTime> m_accessTimes = new();
        
        // Performance optimization components
        private readonly AssetPreloader m_preloader;
        private readonly AssetCompressor m_compressor;
        private readonly AssetMetrics m_metrics;
        
        // Configuration
        private bool m_enableHttp3 = true;
        private bool m_enableCompression = true;
        private bool m_enablePreloading = true;
        private bool m_enableMetrics = true;
        private int m_maxCacheSize = 10000;
        private int m_maxCacheSizeMB = 1024;
        private TimeSpan m_cacheExpiry = TimeSpan.FromHours(2);
        private string m_http3AssetUrl = "https://assets.opensim.local:8443";
        
        // Statistics
        private long m_cacheHits = 0;
        private long m_cacheMisses = 0;
        private long m_http3Requests = 0;
        private long m_fallbackRequests = 0;
        
        // Threading
        private readonly SemaphoreSlim m_cacheSemaphore = new(1, 1);
        private readonly Timer m_maintenanceTimer;
        private volatile bool m_disposed = false;

        #endregion

        #region Constructor

        public Http3AssetService(IConfigSource config, IAssetService fallbackService = null)
            : base(config)
        {
            // Load configuration
            LoadConfiguration(config);
            
            // Initialize fallback service
            m_fallbackAssetService = fallbackService ?? new AssetService(config);
            
            // Configure HTTP/3 client
            m_http3Client = CreateHttp3Client();
            
            // Initialize optimization components
            m_preloader = new AssetPreloader(this, m_enablePreloading);
            m_compressor = new AssetCompressor(m_enableCompression);
            m_metrics = new AssetMetrics(m_enableMetrics);
            
            // Start maintenance timer
            m_maintenanceTimer = new Timer(PerformMaintenance, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            
            m_log.InfoFormat("[HTTP3 ASSET SERVICE]: Initialized with HTTP/3 support: {0}, Cache size: {1} items / {2} MB", 
                m_enableHttp3, m_maxCacheSize, m_maxCacheSizeMB);
        }

        #endregion

        #region Configuration

        private void LoadConfiguration(IConfigSource config)
        {
            var assetConfig = config.Configs["Http3AssetService"];
            if (assetConfig != null)
            {
                m_enableHttp3 = assetConfig.GetBoolean("EnableHttp3", true);
                m_enableCompression = assetConfig.GetBoolean("EnableCompression", true);
                m_enablePreloading = assetConfig.GetBoolean("EnablePreloading", true);
                m_enableMetrics = assetConfig.GetBoolean("EnableMetrics", true);
                m_maxCacheSize = assetConfig.GetInt("MaxCacheSize", 10000);
                m_maxCacheSizeMB = assetConfig.GetInt("MaxCacheSizeMB", 1024);
                m_cacheExpiry = TimeSpan.FromMinutes(assetConfig.GetInt("CacheExpiryMinutes", 120));
                m_http3AssetUrl = assetConfig.GetString("Http3AssetUrl", "https://assets.opensim.local:8443");
            }
        }

        private HttpClient CreateHttp3Client()
        {
            var handler = new SocketsHttpHandler()
            {
                // Enable HTTP/3 (QUIC) support
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 100,
                
                // Enable compression
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                
                // Security and performance settings
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    // Configure for production SSL certificates
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true // TODO: Proper validation
                }
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30),
                DefaultRequestVersion = new Version(3, 0), // HTTP/3
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            
            // Set common headers for efficient asset delivery
            client.DefaultRequestHeaders.Add("Accept-Encoding", "br, gzip, deflate");
            client.DefaultRequestHeaders.Add("Cache-Control", "max-age=3600");
            client.DefaultRequestHeaders.Add("User-Agent", "OpenSim-Http3AssetService/1.0");
            
            return client;
        }

        #endregion

        #region IAssetService Implementation

        public AssetBase Get(string id)
        {
            try 
            {
                return GetAsync(id).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error in synchronous Get for {0}: {1}", id, ex.Message);
                return m_fallbackAssetService.Get(id);
            }
        }

        public AssetBase Get(string id, string ForeignAssetService, bool StoreOnLocalGrid)
        {
            // For foreign assets, fall back to existing service
            if (!string.IsNullOrEmpty(ForeignAssetService))
            {
                Interlocked.Increment(ref m_fallbackRequests);
                return m_fallbackAssetService.Get(id, ForeignAssetService, StoreOnLocalGrid);
            }
            
            return Get(id);
        }

        public async Task<AssetBase> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            try
            {
                // Try cache first
                var cachedAsset = await GetFromCacheAsync(id);
                if (cachedAsset != null)
                {
                    Interlocked.Increment(ref m_cacheHits);
                    m_metrics?.RecordCacheHit(id);
                    return cachedAsset;
                }

                Interlocked.Increment(ref m_cacheMisses);

                // Try HTTP/3 asset service if enabled
                if (m_enableHttp3)
                {
                    var asset = await GetFromHttp3ServiceAsync(id, cancellationToken);
                    if (asset != null)
                    {
                        Interlocked.Increment(ref m_http3Requests);
                        await CacheAssetAsync(id, asset);
                        m_metrics?.RecordHttp3Success(id, asset.Data?.Length ?? 0);
                        
                        // Trigger preloading of related assets
                        _ = Task.Run(() => m_preloader.PreloadRelatedAssets(asset), cancellationToken);
                        
                        return asset;
                    }
                }

                // Fallback to existing asset service
                Interlocked.Increment(ref m_fallbackRequests);
                var fallbackAsset = m_fallbackAssetService.Get(id);
                if (fallbackAsset != null)
                {
                    await CacheAssetAsync(id, fallbackAsset);
                    m_metrics?.RecordFallbackSuccess(id);
                }
                
                return fallbackAsset;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error retrieving asset {0}: {1}", id, ex.Message);
                m_metrics?.RecordError(id, ex);
                
                // Always try fallback on error
                return m_fallbackAssetService.Get(id);
            }
        }

        public AssetMetadata GetMetadata(string id)
        {
            // Check cache for metadata
            if (m_assetCache.TryGetValue(id, out var cached))
            {
                return new AssetMetadata
                {
                    ID = cached.Asset.ID,
                    Name = cached.Asset.Name,
                    Description = cached.Asset.Description,
                    Type = cached.Asset.Type,
                    ContentType = cached.Asset.Metadata.ContentType,
                    CreationDate = cached.Asset.Metadata.CreationDate,
                    Flags = cached.Asset.Metadata.Flags,
                    FullID = cached.Asset.FullID,
                    Local = cached.Asset.Local,
                    Temporary = cached.Asset.Temporary
                };
            }
            
            // Fallback to existing service
            return m_fallbackAssetService.GetMetadata(id);
        }

        public byte[] GetData(string id)
        {
            var asset = Get(id);
            return asset?.Data;
        }

        public AssetBase GetCached(string id)
        {
            if (m_assetCache.TryGetValue(id, out var cached))
            {
                // Check if cache entry is still valid
                if (DateTime.UtcNow - cached.CachedAt < m_cacheExpiry)
                {
                    Interlocked.Increment(ref m_cacheHits);
                    return cached.Asset;
                }
                
                // Remove expired entry
                m_assetCache.TryRemove(id, out _);
            }
            
            return null;
        }

        public bool Get(string id, object sender, AssetRetrieved handler)
        {
            if (string.IsNullOrEmpty(id) || handler == null)
                return false;

            // Execute async get operation
            _ = Task.Run(async () =>
            {
                try
                {
                    var asset = await GetAsync(id);
                    handler(id, sender, asset);
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error in async get for {0}: {1}", id, ex.Message);
                    handler(id, sender, null);
                }
            });

            return true;
        }

        public void Get(string id, string ForeignAssetService, bool StoreOnLocalGrid, SimpleAssetRetrieved callBack)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var asset = await GetAsync(id);
                    callBack?.Invoke(asset);
                }
                catch (Exception ex)
                {
                    m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error in async get for {0}: {1}", id, ex.Message);
                    callBack?.Invoke(null);
                }
            });
        }

        public bool[] AssetsExist(string[] ids)
        {
            if (ids == null)
                return new bool[0];

            var results = new bool[ids.Length];
            var tasks = new Task<bool>[ids.Length];

            // Check existence in parallel for better performance
            for (int i = 0; i < ids.Length; i++)
            {
                var index = i;
                var id = ids[i];
                tasks[i] = Task.Run(async () =>
                {
                    // Check cache first
                    if (m_assetCache.ContainsKey(id))
                        return true;
                    
                    // Quick HTTP/3 HEAD request
                    if (m_enableHttp3)
                    {
                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Head, $"{m_http3AssetUrl}/assets/{id}");
                            using var response = await m_http3Client.SendAsync(request);
                            return response.IsSuccessStatusCode;
                        }
                        catch
                        {
                            // Fall through to fallback service
                        }
                    }
                    
                    // Fallback to existing service
                    var metadata = m_fallbackAssetService.GetMetadata(id);
                    return metadata != null;
                });
            }

            Task.WaitAll(tasks, TimeSpan.FromSeconds(10));
            
            for (int i = 0; i < tasks.Length; i++)
            {
                results[i] = tasks[i].IsCompletedSuccessfully && tasks[i].Result;
            }

            return results;
        }

        public string Store(AssetBase asset)
        {
            if (asset == null)
                return string.Empty;

            try
            {
                // Store in fallback service first
                var assetId = m_fallbackAssetService.Store(asset);
                
                if (!string.IsNullOrEmpty(assetId))
                {
                    // Cache the newly stored asset
                    _ = Task.Run(() => CacheAssetAsync(assetId, asset));
                    
                    // TODO: Upload to HTTP/3 asset service for distribution
                    if (m_enableHttp3)
                    {
                        _ = Task.Run(() => UploadToHttp3ServiceAsync(asset));
                    }
                }
                
                return assetId;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error storing asset {0}: {1}", asset.ID, ex.Message);
                return string.Empty;
            }
        }

        public bool UpdateContent(string id, byte[] data)
        {
            try
            {
                // Remove from cache to force refresh
                m_assetCache.TryRemove(id, out _);
                
                // Update in fallback service
                var fallbackResult = m_fallbackAssetService.UpdateContent(id, data);
                
                // TODO: Update in HTTP/3 asset service
                if (m_enableHttp3 && fallbackResult)
                {
                    _ = Task.Run(() => UpdateContentInHttp3ServiceAsync(id, data));
                }
                
                return fallbackResult;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error updating asset content {0}: {1}", id, ex.Message);
                return false;
            }
        }

        public bool Delete(string id)
        {
            try
            {
                // Remove from cache
                m_assetCache.TryRemove(id, out _);
                
                // Delete from fallback service
                var fallbackResult = m_fallbackAssetService.Delete(id);
                
                // TODO: Delete from HTTP/3 asset service
                if (m_enableHttp3)
                {
                    _ = Task.Run(() => DeleteFromHttp3ServiceAsync(id));
                }
                
                return fallbackResult;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error deleting asset {0}: {1}", id, ex.Message);
                return false;
            }
        }

        #endregion

        #region HTTP/3 Asset Operations

        private async Task<AssetBase> GetFromHttp3ServiceAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                var requestUri = $"{m_http3AssetUrl}/assets/{id}";
                
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                using var response = await m_http3Client.SendAsync(request, cancellationToken);
                
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        m_log.WarnFormat("[HTTP3 ASSET SERVICE]: HTTP/3 request failed for asset {0}: {1}", 
                            id, response.StatusCode);
                    }
                    return null;
                }

                var assetData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                
                // Decompress if necessary
                if (m_enableCompression && assetData != null && assetData.Length > 0)
                {
                    assetData = await m_compressor.DecompressAsync(assetData);
                }

                // Parse asset from response headers and data
                var asset = new AssetBase(id, "Asset", (sbyte)GetAssetTypeFromHeaders(response), UUID.Zero.ToString())
                {
                    Data = assetData,
                    Local = false,
                    Temporary = false
                };

                // Set metadata from headers
                SetAssetMetadataFromHeaders(asset, response);

                return asset;
            }
            catch (TaskCanceledException)
            {
                m_log.DebugFormat("[HTTP3 ASSET SERVICE]: HTTP/3 request cancelled for asset {0}", id);
                return null;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: HTTP/3 request error for asset {0}: {1}", id, ex.Message);
                return null;
            }
        }

        private async Task UploadToHttp3ServiceAsync(AssetBase asset)
        {
            try
            {
                var requestUri = $"{m_http3AssetUrl}/assets/{asset.ID}";
                
                var assetData = asset.Data;
                if (m_enableCompression && assetData != null)
                {
                    assetData = await m_compressor.CompressAsync(assetData);
                }

                using var content = new ByteArrayContent(assetData ?? Array.Empty<byte>());
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    GetContentTypeFromAssetType(asset.Type));
                
                // Add asset metadata to headers
                AddAssetMetadataToHeaders(content, asset);

                using var response = await m_http3Client.PutAsync(requestUri, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    m_log.WarnFormat("[HTTP3 ASSET SERVICE]: Failed to upload asset {0} via HTTP/3: {1}", 
                        asset.ID, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error uploading asset {0} via HTTP/3: {1}", 
                    asset.ID, ex.Message);
            }
        }

        private async Task UpdateContentInHttp3ServiceAsync(string id, byte[] data)
        {
            try
            {
                var requestUri = $"{m_http3AssetUrl}/assets/{id}/content";
                
                var assetData = data;
                if (m_enableCompression && assetData != null)
                {
                    assetData = await m_compressor.CompressAsync(assetData);
                }

                using var content = new ByteArrayContent(assetData ?? Array.Empty<byte>());
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                using var response = await m_http3Client.PatchAsync(requestUri, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    m_log.WarnFormat("[HTTP3 ASSET SERVICE]: Failed to update asset content {0} via HTTP/3: {1}", 
                        id, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error updating asset content {0} via HTTP/3: {1}", 
                    id, ex.Message);
            }
        }

        private async Task DeleteFromHttp3ServiceAsync(string id)
        {
            try
            {
                var requestUri = $"{m_http3AssetUrl}/assets/{id}";
                using var response = await m_http3Client.DeleteAsync(requestUri);
                
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    m_log.WarnFormat("[HTTP3 ASSET SERVICE]: Failed to delete asset {0} via HTTP/3: {1}", 
                        id, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error deleting asset {0} via HTTP/3: {1}", id, ex.Message);
            }
        }

        #endregion

        #region Caching

        private async Task<AssetBase> GetFromCacheAsync(string id)
        {
            if (m_assetCache.TryGetValue(id, out var cached))
            {
                // Check if cache entry is still valid
                if (DateTime.UtcNow - cached.CachedAt < m_cacheExpiry)
                {
                    // Update access time for LRU
                    m_accessTimes.AddOrUpdate(id, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
                    cached.AccessCount++;
                    return cached.Asset;
                }
                
                // Remove expired entry
                await m_cacheSemaphore.WaitAsync();
                try
                {
                    m_assetCache.TryRemove(id, out _);
                }
                finally
                {
                    m_cacheSemaphore.Release();
                }
            }
            
            return null;
        }

        private async Task CacheAssetAsync(string id, AssetBase asset)
        {
            if (asset?.Data == null)
                return;

            var cachedAsset = new Http3CachedAsset
            {
                Asset = asset,
                CachedAt = DateTime.UtcNow,
                Size = asset.Data.Length,
                AccessCount = 1
            };

            await m_cacheSemaphore.WaitAsync();
            try
            {
                // Check cache size limits
                if (m_assetCache.Count >= m_maxCacheSize || GetCacheSizeMB() >= m_maxCacheSizeMB)
                {
                    await EvictLeastRecentlyUsedAsync();
                }

                m_assetCache.TryAdd(id, cachedAsset);
                m_accessTimes.TryAdd(id, DateTime.UtcNow);
            }
            finally
            {
                m_cacheSemaphore.Release();
            }
        }

        private async Task EvictLeastRecentlyUsedAsync()
        {
            // Evict 10% of cache size to avoid frequent evictions
            var evictCount = Math.Max(1, m_assetCache.Count / 10);
            
            // Find least recently accessed items
            var sortedByAccess = m_accessTimes
                .OrderBy(kvp => kvp.Value)
                .Take(evictCount)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in sortedByAccess)
            {
                m_assetCache.TryRemove(id, out _);
                m_accessTimes.TryRemove(id, out _);
            }
        }

        private int GetCacheSizeMB()
        {
            long totalBytes = 0;
            foreach (var cached in m_assetCache.Values)
            {
                totalBytes += cached.Size;
            }
            return (int)(totalBytes / (1024 * 1024));
        }

        #endregion

        #region Utility Methods

        private static int GetAssetTypeFromHeaders(HttpResponseMessage response)
        {
            if (response.Content.Headers.ContentType?.MediaType == null)
                return 0; // Unknown

            return response.Content.Headers.ContentType.MediaType switch
            {
                "image/jpeg" => 0,  // Texture
                "image/png" => 0,   // Texture
                "audio/wav" => 1,   // Sound
                "text/plain" => 3,  // Notecard
                "application/xml" => 6, // LSL Text
                _ => 0 // Unknown
            };
        }

        private static string GetContentTypeFromAssetType(sbyte assetType)
        {
            return assetType switch
            {
                0 => "image/jpeg",      // Texture
                1 => "audio/wav",       // Sound
                3 => "text/plain",      // Notecard
                6 => "application/xml", // LSL Text
                _ => "application/octet-stream"
            };
        }

        private static void SetAssetMetadataFromHeaders(AssetBase asset, HttpResponseMessage response)
        {
            // Extract metadata from response headers
            if (response.Headers.TryGetValues("X-Asset-Name", out var nameValues))
            {
                asset.Name = nameValues.FirstOrDefault() ?? string.Empty;
            }
            
            if (response.Headers.TryGetValues("X-Asset-Description", out var descValues))
            {
                asset.Description = descValues.FirstOrDefault() ?? string.Empty;
            }
        }

        private static void AddAssetMetadataToHeaders(ByteArrayContent content, AssetBase asset)
        {
            if (!string.IsNullOrEmpty(asset.Name))
                content.Headers.Add("X-Asset-Name", asset.Name);
            
            if (!string.IsNullOrEmpty(asset.Description))
                content.Headers.Add("X-Asset-Description", asset.Description);
            
            content.Headers.Add("X-Asset-Type", asset.Type.ToString());
            content.Headers.Add("X-Asset-Local", asset.Local.ToString());
            content.Headers.Add("X-Asset-Temporary", asset.Temporary.ToString());
        }

        #endregion

        #region Maintenance

        private void PerformMaintenance(object state)
        {
            if (m_disposed)
                return;

            try
            {
                // Clean expired cache entries
                _ = Task.Run(async () =>
                {
                    var expiredKeys = new List<string>();
                    var cutoff = DateTime.UtcNow - m_cacheExpiry;

                    foreach (var kvp in m_assetCache)
                    {
                        if (kvp.Value.CachedAt < cutoff)
                        {
                            expiredKeys.Add(kvp.Key);
                        }
                    }

                    if (expiredKeys.Count > 0)
                    {
                        await m_cacheSemaphore.WaitAsync();
                        try
                        {
                            foreach (var key in expiredKeys)
                            {
                                m_assetCache.TryRemove(key, out _);
                            }
                        }
                        finally
                        {
                            m_cacheSemaphore.Release();
                        }

                        m_log.DebugFormat("[HTTP3 ASSET SERVICE]: Cleaned {0} expired cache entries", expiredKeys.Count);
                    }
                });

                // Log statistics
                if (m_enableMetrics)
                {
                    var cacheHitRate = m_cacheHits + m_cacheMisses > 0 
                        ? (double)m_cacheHits / (m_cacheHits + m_cacheMisses) * 100 
                        : 0;

                    m_log.InfoFormat(
                        "[HTTP3 ASSET SERVICE]: Stats - Cache: {0} items ({1} MB), Hit rate: {2:F1}%, " +
                        "HTTP/3: {3} requests, Fallback: {4} requests",
                        m_assetCache.Count, GetCacheSizeMB(), cacheHitRate, m_http3Requests, m_fallbackRequests);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error during maintenance: {0}", ex.Message);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (m_disposed)
                return;

            m_disposed = true;

            try
            {
                m_maintenanceTimer?.Dispose();
                m_http3Client?.Dispose();
                m_cacheSemaphore?.Dispose();
                m_preloader?.Dispose();
                m_compressor?.Dispose();
                m_metrics?.Dispose();
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[HTTP3 ASSET SERVICE]: Error during disposal: {0}", ex.Message);
            }

            GC.SuppressFinalize(this);
        }

        #endregion

        #region Inner Classes

        private class Http3CachedAsset
        {
            public AssetBase Asset { get; set; }
            public DateTime CachedAt { get; set; }
            public long Size { get; set; }
            public int AccessCount { get; set; }
        }

        #endregion
    }

    #region Supporting Classes

    /// <summary>
    /// Asset preloader for predictive caching
    /// </summary>
    public class AssetPreloader : IDisposable
    {
        private readonly Http3AssetService m_assetService;
        private readonly bool m_enabled;
        private volatile bool m_disposed = false;

        public AssetPreloader(Http3AssetService assetService, bool enabled)
        {
            m_assetService = assetService;
            m_enabled = enabled;
        }

        public async Task PreloadRelatedAssets(AssetBase asset)
        {
            if (!m_enabled || m_disposed || asset?.Data == null)
                return;

            try
            {
                // TODO: Implement intelligent preloading based on asset relationships
                // For now, this is a placeholder for future enhancement
                await Task.Delay(1); // Placeholder
            }
            catch (Exception ex)
            {
                // Log but don't propagate preloading errors
                Console.WriteLine($"[ASSET PRELOADER]: Error preloading related assets: {ex.Message}");
            }
        }

        public void Dispose()
        {
            m_disposed = true;
        }
    }

    /// <summary>
    /// Asset compression for bandwidth optimization
    /// </summary>
    public class AssetCompressor : IDisposable
    {
        private readonly bool m_enabled;
        private volatile bool m_disposed = false;

        public AssetCompressor(bool enabled)
        {
            m_enabled = enabled;
        }

        public async Task<byte[]> CompressAsync(byte[] data)
        {
            if (!m_enabled || m_disposed || data == null || data.Length == 0)
                return data;

            try
            {
                using var output = new MemoryStream();
                using var brotli = new BrotliStream(output, CompressionLevel.Optimal);
                await brotli.WriteAsync(data, 0, data.Length);
                await brotli.FlushAsync();
                return output.ToArray();
            }
            catch
            {
                return data; // Return original data on compression failure
            }
        }

        public async Task<byte[]> DecompressAsync(byte[] compressedData)
        {
            if (!m_enabled || m_disposed || compressedData == null || compressedData.Length == 0)
                return compressedData;

            try
            {
                using var input = new MemoryStream(compressedData);
                using var brotli = new BrotliStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                await brotli.CopyToAsync(output);
                return output.ToArray();
            }
            catch
            {
                return compressedData; // Return original data on decompression failure
            }
        }

        public void Dispose()
        {
            m_disposed = true;
        }
    }

    /// <summary>
    /// Performance metrics collection
    /// </summary>
    public class AssetMetrics : IDisposable
    {
        private readonly bool m_enabled;
        private volatile bool m_disposed = false;

        public AssetMetrics(bool enabled)
        {
            m_enabled = enabled;
        }

        public void RecordCacheHit(string assetId)
        {
            if (!m_enabled || m_disposed) return;
            // TODO: Implement detailed metrics collection
        }

        public void RecordHttp3Success(string assetId, int dataSize)
        {
            if (!m_enabled || m_disposed) return;
            // TODO: Implement detailed metrics collection
        }

        public void RecordFallbackSuccess(string assetId)
        {
            if (!m_enabled || m_disposed) return;
            // TODO: Implement detailed metrics collection
        }

        public void RecordError(string assetId, Exception ex)
        {
            if (!m_enabled || m_disposed) return;
            // TODO: Implement detailed metrics collection
        }

        public void Dispose()
        {
            m_disposed = true;
        }
    }

    #endregion
}