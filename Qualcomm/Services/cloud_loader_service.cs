// ============================================================================
// SakuraEDL - Cloud Loader Service | 云端 Loader 服务
// ============================================================================
// [ZH] 云端 Loader 服务 - 自动匹配和下载高通 Loader
// [EN] Cloud Loader Service - Auto-match and download Qualcomm Loaders
// [JA] クラウドLoaderサービス - Qualcomm Loaderの自動マッチング
// [KO] 클라우드 Loader 서비스 - Qualcomm Loader 자동 매칭
// [RU] Облачный сервис Loader - Автоматический поиск Qualcomm Loader
// [ES] Servicio Cloud Loader - Coincidencia automática de Qualcomm Loader
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SakuraEDL.Qualcomm.Services
{
    public class CloudLoaderService
    {
        #region Singleton
        private static CloudLoaderService _instance;
        private static readonly object _lock = new object();
        
        public static CloudLoaderService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new CloudLoaderService();
                    }
                }
                return _instance;
            }
        }
        #endregion

        #region Configuration
        
        // 当前使用的 API 地址
        public string ApiBase { get; set; } = "https://api.sakuraedl.org/api";
        
        // 本地缓存目录
        public string CacheDirectory { get; set; }
        
        // 是否启用云端匹配
        public bool EnableCloudMatch { get; set; } = true;
        
        // 是否启用本地缓存
        public bool EnableCache { get; set; } = true;
        
        // 超时时间 (秒)
        public int TimeoutSeconds { get; set; } = 15;
        
        #endregion

        #region Fields
        
        private readonly HttpClient _httpClient;
        private Action<string> _log;
        private Action<string> _logDetail;
        
        #endregion

        #region Constructor
        
        private CloudLoaderService()
        {
            // 绕过系统代理，避免 VPN/代理软件干扰
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                // 允许自签名证书（可选，调试用）
                // ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SakuraEDL/2.0");
            // 在构造函数中设置超时（HttpClient 启动后不能再修改 Timeout）
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
            
            // 默认缓存目录
            CacheDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, 
                "cache", 
                "loaders"
            );
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// 设置日志回调
        /// </summary>
        public void SetLogger(Action<string> log, Action<string> logDetail = null)
        {
            _log = log;
            _logDetail = logDetail;
        }
        
        /// <summary>
        /// 根据设备信息自动匹配 Loader
        /// </summary>
        public async Task<LoaderResult> MatchLoaderAsync(
            string msmId, 
            string pkHash = null, 
            string oemId = null,
            string storageType = "ufs")
        {
            if (!EnableCloudMatch)
            {
                Log("云端匹配已禁用");
                return null;
            }

            try
            {
                // 1. 检查本地缓存
                if (EnableCache && !string.IsNullOrEmpty(pkHash))
                {
                    var cached = LoadFromCache(pkHash);
                    if (cached != null)
                    {
                        Log(string.Format("使用本地缓存: {0}", cached.Filename));
                        return cached;
                    }
                }
                
                // 2. 调用云端 API 匹配
                Log("正在云端匹配 Loader...");
                LogDetail(string.Format("MSM ID: {0}, PK Hash: {1}...", msmId, 
                    pkHash != null && pkHash.Length >= 16 ? pkHash.Substring(0, 16) : pkHash));
                
                // 构建请求 JSON
                var json = BuildMatchRequestJson(msmId, pkHash, oemId, storageType);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(ApiBase + "/loaders/match", content);
                var resultJson = await response.Content.ReadAsStringAsync();
                
                // 解析响应
                int code = ParseJsonInt(resultJson, "code");
                if (code != 0)
                {
                    string message = ParseJsonString(resultJson, "message");
                    Log(string.Format("云端无匹配: {0}", message ?? "未知错误"));
                    return null;
                }
                
                // 解析 loader 数据
                string loaderJson = ExtractJsonObject(resultJson, "loader");
                if (string.IsNullOrEmpty(loaderJson))
                {
                    Log("云端无匹配: 无 loader 数据");
                    return null;
                }
                
                int loaderId = ParseJsonInt(loaderJson, "id");
                string filename = ParseJsonString(loaderJson, "filename");
                string vendor = ParseJsonString(loaderJson, "vendor");
                string chip = ParseJsonString(loaderJson, "chip");
                string authType = ParseJsonString(loaderJson, "auth_type");
                string loaderStorageType = ParseJsonString(loaderJson, "storage_type");
                string hwId = ParseJsonString(loaderJson, "hw_id");
                
                // 解析匹配信息
                string dataJson = ExtractJsonObject(resultJson, "data");
                int score = ParseJsonInt(dataJson, "score");
                string matchType = ParseJsonString(dataJson, "match_type");
                
                Log(string.Format("云端匹配成功: {0}", filename));
                LogDetail(string.Format("厂商: {0}, 芯片: {1}, 认证: {2}", vendor, chip, authType));
                LogDetail(string.Format("匹配类型: {0}, 置信度: {1}%", matchType, score));
                
                // 3. 下载 Loader 文件
                byte[] loaderData = null;
                if (loaderId > 0)
                {
                    loaderData = await DownloadLoaderAsync(loaderId);
                }
                
                var loaderResult = new LoaderResult
                {
                    Id = loaderId,
                    Filename = filename,
                    Vendor = vendor,
                    Chip = chip,
                    AuthType = authType,
                    StorageType = loaderStorageType,
                    HwId = hwId,
                    PkHash = pkHash,
                    MatchType = matchType,
                    Confidence = score,
                    Data = loaderData
                };
                
                // 4. 保存到缓存
                if (EnableCache && loaderData != null && !string.IsNullOrEmpty(pkHash))
                {
                    SaveToCache(pkHash, loaderResult);
                }
                
                return loaderResult;
            }
            catch (TaskCanceledException)
            {
                Log("云端匹配超时");
                return null;
            }
            catch (HttpRequestException ex)
            {
                Log(string.Format("云端匹配失败: {0}", ex.Message));
                return null;
            }
            catch (Exception ex)
            {
                Log(string.Format("云端匹配异常: {0}", ex.Message));
                return null;
            }
        }
        
        /// <summary>
        /// 从云端下载 Loader 文件
        /// </summary>
        public async Task<byte[]> DownloadLoaderAsync(int loaderId)
        {
            return await DownloadLoaderAsync(loaderId, null);
        }
        
        /// <summary>
        /// 从云端下载 Loader 文件 (带进度回调)
        /// </summary>
        /// <param name="loaderId">Loader ID</param>
        /// <param name="progressCallback">进度回调: (已下载字节, 总字节, 速度KB/s)</param>
        public async Task<byte[]> DownloadLoaderAsync(int loaderId, Action<long, long, double> progressCallback)
        {
            try
            {
                var url = string.Format("{0}/loaders/{1}/download", ApiBase, loaderId);
                
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Log(string.Format("下载失败: HTTP {0}", (int)response.StatusCode));
                        return null;
                    }
                    
                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var memoryStream = new System.IO.MemoryStream())
                    {
                        var buffer = new byte[8192];
                        long downloadedBytes = 0;
                        int bytesRead;
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        long lastReportTime = 0;
                        long lastReportBytes = 0;
                        double speed = 0;
                        
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            memoryStream.Write(buffer, 0, bytesRead);
                            downloadedBytes += bytesRead;
                            
                            // 每 100ms 更新一次进度
                            if (stopwatch.ElapsedMilliseconds - lastReportTime >= 100)
                            {
                                // 计算下载速度 (KB/s)
                                var timeDiff = (stopwatch.ElapsedMilliseconds - lastReportTime) / 1000.0;
                                if (timeDiff > 0)
                                {
                                    speed = (downloadedBytes - lastReportBytes) / 1024.0 / timeDiff;
                                }
                                
                                lastReportTime = stopwatch.ElapsedMilliseconds;
                                lastReportBytes = downloadedBytes;
                                
                                progressCallback?.Invoke(downloadedBytes, totalBytes, speed);
                            }
                        }
                        
                        // 最终进度
                        progressCallback?.Invoke(downloadedBytes, totalBytes, speed);
                        
                        return memoryStream.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("下载异常: {0}", ex.Message));
                return null;
            }
        }
        
        /// <summary>
        /// 下载 VIP Digest 文件
        /// </summary>
        /// <param name="loaderId">Loader ID</param>
        public async Task<byte[]> DownloadDigestAsync(int loaderId)
        {
            try
            {
                var url = string.Format("{0}/loaders/{1}/digest", ApiBase, loaderId);
                Log(string.Format("[云端] 下载 Digest (ID={0})...", loaderId));
                
                using (var response = await _httpClient.GetAsync(url))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Log(string.Format("[云端] Digest 下载失败: HTTP {0}", (int)response.StatusCode));
                        return null;
                    }
                    
                    var data = await response.Content.ReadAsByteArrayAsync();
                    Log(string.Format("[云端] Digest 下载完成 ({0} 字节)", data.Length));
                    return data;
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("[云端] Digest 下载异常: {0}", ex.Message));
                return null;
            }
        }
        
        /// <summary>
        /// 下载 VIP Sign 文件
        /// </summary>
        /// <param name="loaderId">Loader ID</param>
        public async Task<byte[]> DownloadSignatureAsync(int loaderId)
        {
            try
            {
                var url = string.Format("{0}/loaders/{1}/sign", ApiBase, loaderId);
                Log(string.Format("[云端] 下载 Sign (ID={0})...", loaderId));
                
                using (var response = await _httpClient.GetAsync(url))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Log(string.Format("[云端] Sign 下载失败: HTTP {0}", (int)response.StatusCode));
                        return null;
                    }
                    
                    var data = await response.Content.ReadAsByteArrayAsync();
                    Log(string.Format("[云端] Sign 下载完成 ({0} 字节)", data.Length));
                    return data;
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("[云端] Sign 下载异常: {0}", ex.Message));
                return null;
            }
        }
        
        /// <summary>
        /// 下载 VIP 验证所需的所有文件
        /// </summary>
        /// <param name="loaderId">Loader ID</param>
        /// <returns>元组: (digestData, signData)，任一为 null 表示下载失败</returns>
        public async Task<(byte[] digest, byte[] sign)> DownloadVipFilesAsync(int loaderId)
        {
            Log("[云端] 下载 VIP 验证文件...");
            
            var digestTask = DownloadDigestAsync(loaderId);
            var signTask = DownloadSignatureAsync(loaderId);
            
            await Task.WhenAll(digestTask, signTask);
            
            var digest = digestTask.Result;
            var sign = signTask.Result;
            
            if (digest == null || sign == null)
            {
                Log("[云端] VIP 验证文件下载不完整");
                return (null, null);
            }
            
            Log(string.Format("[云端] VIP 文件下载完成 (Digest={0}B, Sign={1}B)", digest.Length, sign.Length));
            return (digest, sign);
        }
        
        /// <summary>
        /// 获取云端 Loader 列表 (供用户选择)
        /// </summary>
        public async Task<List<CloudLoaderInfo>> GetLoaderListAsync(string storageType = null)
        {
            var result = new List<CloudLoaderInfo>();
            
            try
            {
                // 添加时间戳防止缓存
                string cacheBuster = "_t=" + DateTime.UtcNow.Ticks;
                string url = ApiBase + "/loaders/list?" + cacheBuster;
                if (!string.IsNullOrEmpty(storageType))
                {
                    url += "&storage_type=" + storageType;
                }
                
                // 创建请求并禁用缓存
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                var response = await _httpClient.SendAsync(request);
                var resultJson = await response.Content.ReadAsStringAsync();
                
                int code = ParseJsonInt(resultJson, "code");
                if (code != 0)
                {
                    Log("获取列表失败");
                    return result;
                }
                
                // 解析 loaders 数组
                string loadersArray = ExtractJsonArray(resultJson, "loaders");
                if (string.IsNullOrEmpty(loadersArray))
                {
                    Log("未找到 loaders 数组");
                    return result;
                }
                
                // 简单解析 JSON 数组中的每个对象
                var items = ParseJsonArrayItems(loadersArray);
                
                foreach (var itemJson in items)
                {
                    var info = new CloudLoaderInfo
                    {
                        Id = ParseJsonInt(itemJson, "id"),
                        Filename = ParseJsonString(itemJson, "filename"),
                        Vendor = ParseJsonString(itemJson, "vendor"),
                        Chip = ParseJsonString(itemJson, "chip"),
                        HwId = ParseJsonString(itemJson, "hw_id"),
                        AuthType = ParseJsonString(itemJson, "auth_type"),
                        StorageType = ParseJsonString(itemJson, "storage_type"),
                        FileSize = ParseJsonInt(itemJson, "file_size"),
                        DisplayName = ParseJsonString(itemJson, "display_name"),
                        HasDigest = ParseJsonBool(itemJson, "has_digest"),
                        HasSign = ParseJsonBool(itemJson, "has_sign")
                    };
                    
                    if (info.Id > 0)
                    {
                        result.Add(info);
                    }
                }
                
            }
            catch (Exception ex)
            {
                // 输出详细错误信息用于调试
                string errorDetail = ex.Message;
                if (ex.InnerException != null)
                {
                    errorDetail += " -> " + ex.InnerException.Message;
                    if (ex.InnerException.InnerException != null)
                    {
                        errorDetail += " -> " + ex.InnerException.InnerException.Message;
                    }
                }
                Log(string.Format("获取列表异常: {0}", errorDetail));
                LogDetail(string.Format("API地址: {0}", ApiBase + "/loaders/list"));
            }
            
            return result;
        }
        
        /// <summary>
        /// 上报设备日志 (异步，不阻塞主流程) - 简化版本
        /// </summary>
        public void ReportDeviceLog(
            string msmId,
            string pkHash,
            string oemId,
            string storageType,
            string matchResult)
        {
            ReportDeviceLogEx(0, msmId, pkHash, oemId, "", "", "", "", "", storageType, matchResult);
        }
        
        /// <summary>
        /// 上报设备日志 (异步，不阻塞主流程) - 完整版本
        /// </summary>
        /// <param name="saharaVersion">Sahara 协议版本 (1/2/3)</param>
        /// <param name="msmId">MSM ID</param>
        /// <param name="pkHash">PK Hash</param>
        /// <param name="oemId">OEM ID</param>
        /// <param name="modelId">Model ID</param>
        /// <param name="hwId">完整 HWID</param>
        /// <param name="serialNumber">设备序列号</param>
        /// <param name="chipName">芯片名称 (如 SM8550)</param>
        /// <param name="vendor">厂商名称 (如 Xiaomi)</param>
        /// <param name="storageType">存储类型</param>
        /// <param name="matchResult">匹配结果</param>
        public void ReportDeviceLogEx(
            int saharaVersion,
            string msmId,
            string pkHash,
            string oemId,
            string modelId,
            string hwId,
            string serialNumber,
            string chipName,
            string vendor,
            string storageType,
            string matchResult)
        {
            if (!EnableCloudMatch) return;
            
            Task.Run(async () =>
            {
                try
                {
                    var json = BuildDeviceLogJsonEx(saharaVersion, msmId, pkHash, oemId, modelId, 
                        hwId, serialNumber, chipName, vendor, storageType, matchResult);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await _httpClient.PostAsync(ApiBase + "/device-logs", content);
                }
                catch
                {
                    // 静默失败，不影响主流程
                }
            });
        }
        
        /// <summary>
        /// 清除本地缓存
        /// </summary>
        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, true);
                    Log("缓存已清除");
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("清除缓存失败: {0}", ex.Message));
            }
        }
        
        /// <summary>
        /// 获取缓存大小
        /// </summary>
        public long GetCacheSize()
        {
            if (!Directory.Exists(CacheDirectory))
                return 0;
                
            long size = 0;
            foreach (var file in Directory.GetFiles(CacheDirectory, "*", SearchOption.AllDirectories))
            {
                size += new FileInfo(file).Length;
            }
            return size;
        }
        
        #endregion

        #region Private Methods - JSON Helpers
        
        private string BuildMatchRequestJson(string msmId, string pkHash, string oemId, string storageType)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.AppendFormat("\"msm_id\":\"{0}\"", EscapeJson(msmId ?? ""));
            if (!string.IsNullOrEmpty(pkHash))
                sb.AppendFormat(",\"pk_hash\":\"{0}\"", EscapeJson(pkHash));
            if (!string.IsNullOrEmpty(oemId))
                sb.AppendFormat(",\"oem_id\":\"{0}\"", EscapeJson(oemId));
            sb.AppendFormat(",\"storage_type\":\"{0}\"", EscapeJson(storageType ?? "ufs"));
            sb.Append(",\"client_version\":\"SakuraEDL/2.0\"");
            sb.Append("}");
            return sb.ToString();
        }
        
        private string BuildDeviceLogJson(string msmId, string pkHash, string oemId, string storageType, string matchResult)
        {
            return BuildDeviceLogJsonEx(0, msmId, pkHash, oemId, "", "", "", "", "", storageType, matchResult);
        }
        
        private string BuildDeviceLogJsonEx(int saharaVersion, string msmId, string pkHash, string oemId, 
            string modelId, string hwId, string serialNumber, string chipName, string vendor, 
            string storageType, string matchResult)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"platform\":\"qualcomm\"");
            sb.AppendFormat(",\"sahara_version\":{0}", saharaVersion);
            sb.AppendFormat(",\"msm_id\":\"{0}\"", EscapeJson(msmId ?? ""));
            if (!string.IsNullOrEmpty(pkHash))
                sb.AppendFormat(",\"pk_hash\":\"{0}\"", EscapeJson(pkHash));
            if (!string.IsNullOrEmpty(oemId))
                sb.AppendFormat(",\"oem_id\":\"{0}\"", EscapeJson(oemId));
            if (!string.IsNullOrEmpty(modelId))
                sb.AppendFormat(",\"model_id\":\"{0}\"", EscapeJson(modelId));
            if (!string.IsNullOrEmpty(hwId))
                sb.AppendFormat(",\"hw_id\":\"{0}\"", EscapeJson(hwId));
            if (!string.IsNullOrEmpty(serialNumber))
                sb.AppendFormat(",\"serial_number\":\"{0}\"", EscapeJson(serialNumber));
            if (!string.IsNullOrEmpty(chipName))
                sb.AppendFormat(",\"chip_name\":\"{0}\"", EscapeJson(chipName));
            if (!string.IsNullOrEmpty(vendor))
                sb.AppendFormat(",\"vendor\":\"{0}\"", EscapeJson(vendor));
            sb.AppendFormat(",\"storage_type\":\"{0}\"", EscapeJson(storageType ?? "ufs"));
            sb.AppendFormat(",\"match_result\":\"{0}\"", EscapeJson(matchResult ?? ""));
            sb.Append(",\"client_version\":\"SakuraEDL/2.0\"");
            sb.Append("}");
            return sb.ToString();
        }
        
        private string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
        
        private string ParseJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = string.Format("\"{0}\"\\s*:\\s*\"([^\"]*)\"", Regex.Escape(key));
            var match = Regex.Match(json, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }
        
        private int ParseJsonInt(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            var pattern = string.Format("\"{0}\"\\s*:\\s*(-?\\d+)", Regex.Escape(key));
            var match = Regex.Match(json, pattern);
            if (match.Success)
            {
                int result;
                if (int.TryParse(match.Groups[1].Value, out result))
                    return result;
            }
            return 0;
        }
        
        private bool ParseJsonBool(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return false;
            var pattern = string.Format("\"{0}\"\\s*:\\s*(true|false)", Regex.Escape(key));
            var match = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.ToLower() == "true";
            }
            return false;
        }
        
        private string ExtractJsonObject(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = string.Format("\"{0}\"\\s*:\\s*\\{{", Regex.Escape(key));
            var match = Regex.Match(json, pattern);
            if (!match.Success) return null;
            
            int start = match.Index + match.Length - 1;
            int depth = 0;
            bool inString = false;
            bool escape = false;
            
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                
                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\' && inString)
                {
                    escape = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }
                
                if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                    if (depth == 0)
                        return json.Substring(start, i - start + 1);
                }
            }
            return null;
        }
        
        private string ExtractJsonArray(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var pattern = string.Format("\"{0}\"\\s*:\\s*\\[", Regex.Escape(key));
            var match = Regex.Match(json, pattern);
            if (!match.Success) return null;
            
            int start = match.Index + match.Length - 1;
            int depth = 0;
            bool inString = false;
            bool escape = false;
            
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                
                // 处理转义字符
                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\' && inString)
                {
                    escape = true;
                    continue;
                }
                
                // 处理字符串边界
                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }
                
                // 只在字符串外部计算括号深度
                if (!inString)
                {
                    if (c == '[') depth++;
                    else if (c == ']') depth--;
                    if (depth == 0)
                        return json.Substring(start, i - start + 1);
                }
            }
            return null;
        }
        
        private List<string> ParseJsonArrayItems(string arrayJson)
        {
            var items = new List<string>();
            if (string.IsNullOrEmpty(arrayJson) || arrayJson.Length < 2) return items;
            
            // 移除外层括号
            arrayJson = arrayJson.Substring(1, arrayJson.Length - 2).Trim();
            if (string.IsNullOrEmpty(arrayJson)) return items;
            
            int depth = 0;
            int start = 0;
            bool inString = false;
            bool escape = false;
            
            for (int i = 0; i < arrayJson.Length; i++)
            {
                char c = arrayJson[i];
                
                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\' && inString)
                {
                    escape = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }
                
                if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        var item = arrayJson.Substring(start, i - start).Trim();
                        if (!string.IsNullOrEmpty(item))
                            items.Add(item);
                        start = i + 1;
                    }
                }
            }
            // 最后一个元素
            if (start < arrayJson.Length)
            {
                var item = arrayJson.Substring(start).Trim();
                if (!string.IsNullOrEmpty(item))
                    items.Add(item);
            }
            return items;
        }
        
        #endregion

        #region Private Methods - Cache
        
        private LoaderResult LoadFromCache(string pkHash)
        {
            try
            {
                var cacheFile = GetCachePath(pkHash);
                var metaFile = cacheFile + ".meta";
                
                if (!File.Exists(cacheFile) || !File.Exists(metaFile))
                    return null;
                
                var metaJson = File.ReadAllText(metaFile);
                var result = new LoaderResult
                {
                    Id = ParseJsonInt(metaJson, "Id"),
                    Filename = ParseJsonString(metaJson, "Filename"),
                    Vendor = ParseJsonString(metaJson, "Vendor"),
                    Chip = ParseJsonString(metaJson, "Chip"),
                    AuthType = ParseJsonString(metaJson, "AuthType"),
                    StorageType = ParseJsonString(metaJson, "StorageType"),
                    HwId = ParseJsonString(metaJson, "HwId"),
                    PkHash = ParseJsonString(metaJson, "PkHash"),
                    MatchType = ParseJsonString(metaJson, "MatchType"),
                    Confidence = ParseJsonInt(metaJson, "Confidence"),
                    Data = File.ReadAllBytes(cacheFile)
                };
                
                return result;
            }
            catch
            {
                return null;
            }
        }
        
        private void SaveToCache(string pkHash, LoaderResult result)
        {
            try
            {
                var cacheFile = GetCachePath(pkHash);
                var metaFile = cacheFile + ".meta";
                
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                
                // 保存 Loader 数据
                if (result.Data != null)
                {
                    File.WriteAllBytes(cacheFile, result.Data);
                }
                
                // 保存元数据
                var sb = new StringBuilder();
                sb.Append("{");
                sb.AppendFormat("\"Id\":{0}", result.Id);
                sb.AppendFormat(",\"Filename\":\"{0}\"", EscapeJson(result.Filename ?? ""));
                sb.AppendFormat(",\"Vendor\":\"{0}\"", EscapeJson(result.Vendor ?? ""));
                sb.AppendFormat(",\"Chip\":\"{0}\"", EscapeJson(result.Chip ?? ""));
                sb.AppendFormat(",\"AuthType\":\"{0}\"", EscapeJson(result.AuthType ?? ""));
                sb.AppendFormat(",\"StorageType\":\"{0}\"", EscapeJson(result.StorageType ?? ""));
                sb.AppendFormat(",\"HwId\":\"{0}\"", EscapeJson(result.HwId ?? ""));
                sb.AppendFormat(",\"PkHash\":\"{0}\"", EscapeJson(result.PkHash ?? ""));
                sb.AppendFormat(",\"MatchType\":\"{0}\"", EscapeJson(result.MatchType ?? ""));
                sb.AppendFormat(",\"Confidence\":{0}", result.Confidence);
                sb.Append("}");
                
                File.WriteAllText(metaFile, sb.ToString());
                
                LogDetail(string.Format("已缓存: {0}", result.Filename));
            }
            catch (Exception ex)
            {
                LogDetail(string.Format("缓存保存失败: {0}", ex.Message));
            }
        }
        
        private string GetCachePath(string pkHash)
        {
            // 使用 PK Hash 前 8 位作为子目录
            var subDir = pkHash.Length >= 8 ? pkHash.Substring(0, 8) : pkHash;
            return Path.Combine(CacheDirectory, subDir, pkHash + ".bin");
        }
        
        private void Log(string message)
        {
            if (_log != null)
                _log(message);
        }
        
        private void LogDetail(string message)
        {
            System.Diagnostics.Debug.WriteLine("[CloudLoader] " + message);
            if (_logDetail != null)
                _logDetail(message);
        }
        
        #endregion
    }

    #region Data Models
    
    /// <summary>
    /// Loader 匹配结果
    /// </summary>
    public class LoaderResult
    {
        public int Id { get; set; }
        public string Filename { get; set; }
        public string Vendor { get; set; }
        public string Chip { get; set; }
        public string AuthType { get; set; }
        public string StorageType { get; set; }
        public string HwId { get; set; }
        public string PkHash { get; set; }
        public string MatchType { get; set; }
        public int Confidence { get; set; }
        public byte[] Data { get; set; }
    }
    
    /// <summary>
    /// 云端 Loader 信息 (用于列表显示)
    /// </summary>
    public class CloudLoaderInfo
    {
        public int Id { get; set; }
        public string Filename { get; set; }
        public string Vendor { get; set; }
        public string Chip { get; set; }
        public string HwId { get; set; }
        public string AuthType { get; set; }
        public string StorageType { get; set; }
        public int FileSize { get; set; }
        public string DisplayName { get; set; }
        
        /// <summary>
        /// 是否有 Digest 文件 (VIP 验证需要)
        /// </summary>
        public bool HasDigest { get; set; }
        
        /// <summary>
        /// 是否有 Sign 文件 (VIP 验证需要)
        /// </summary>
        public bool HasSign { get; set; }
        
        /// <summary>
        /// 是否需要 VIP 验证
        /// </summary>
        public bool IsVip => AuthType?.ToLower() == "vip";
        
        /// <summary>
        /// 是否需要一加验证
        /// </summary>
        public bool IsOnePlus => AuthType?.ToLower() == "demacia";
        
        /// <summary>
        /// 是否需要小米验证
        /// </summary>
        public bool IsXiaomi => AuthType?.ToLower() == "miauth";
        
        /// <summary>
        /// VIP 验证文件是否完整
        /// </summary>
        public bool HasVipFiles => HasDigest && HasSign;
        
        /// <summary>
        /// 获取显示名称
        /// </summary>
        public override string ToString()
        {
            return string.IsNullOrEmpty(DisplayName) 
                ? string.Format("[{0}] {1} - {2}", Vendor, Chip, Filename)
                : DisplayName;
        }
    }
    
    #endregion
}
