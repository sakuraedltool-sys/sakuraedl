// ============================================================================
// SakuraEDL - Remote Payload Service | 远程 Payload 服务
// ============================================================================
// [ZH] 远程 Payload 服务 - 从云端获取和处理 OTA payload.bin
// [EN] Remote Payload Service - Fetch and process OTA payload.bin from cloud
// [JA] リモートPayloadサービス - クラウドからOTA payload.binを取得
// [KO] 원격 Payload 서비스 - 클라우드에서 OTA payload.bin 가져오기
// [RU] Удаленный сервис Payload - Получение OTA payload.bin из облака
// [ES] Servicio Payload remoto - Obtener payload.bin OTA de la nube
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Fastboot.Payload
{
    /// <summary>
    /// 云端 Payload 服务
    /// 支持从远程 URL 直接解析和提取 OTA 包中的分区
    /// 参考 oplus_ota_tool_gui.py 实现
    /// </summary>
    public class RemotePayloadService : IDisposable
    {
        #region Constants

        private const uint ZIP_LOCAL_FILE_HEADER_SIG = 0x04034B50;
        private const uint ZIP_CENTRAL_DIR_SIG = 0x02014B50;
        private const uint ZIP_EOCD_SIG = 0x06054B50;
        private const uint ZIP64_EOCD_SIG = 0x06064B50;
        private const uint ZIP64_EOCD_LOCATOR_SIG = 0x07064B50;

        private const uint PAYLOAD_MAGIC = 0x43724155; // "CrAU" in big-endian

        // InstallOperation Types
        private const int OP_REPLACE = 0;
        private const int OP_REPLACE_BZ = 1;
        private const int OP_REPLACE_XZ = 8;
        private const int OP_ZERO = 6;

        #endregion

        #region Fields

        private HttpClient _httpClient;
        private string _currentUrl;
        private long _totalSize;
        private long _payloadDataOffset;
        private long _dataStartOffset;
        private uint _blockSize = 4096;
        private List<RemotePayloadPartition> _partitions = new List<RemotePayloadPartition>();
        private bool _disposed;

        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;
        private readonly Action<long, long> _progress;
        
        // 多线程下载配置 (类似 IDM/NDM)
        private int _maxConnections = 8;           // 最大并发连接数
        private long _minChunkSize = 512 * 1024;   // 最小分块大小 (512KB)
        private bool _enableMultiThread = true;    // 是否启用多线程下载

        #endregion

        #region Properties

        public bool IsLoaded { get; private set; }
        public string CurrentUrl => _currentUrl;
        public long TotalSize => _totalSize;
        public IReadOnlyList<RemotePayloadPartition> Partitions => _partitions;
        public uint BlockSize => _blockSize;
        
        /// <summary>
        /// 最大并发连接数 (1-32, 默认8)
        /// </summary>
        public int MaxConnections
        {
            get => _maxConnections;
            set => _maxConnections = Math.Max(1, Math.Min(32, value));
        }
        
        /// <summary>
        /// 是否启用多线程下载 (默认true)
        /// </summary>
        public bool EnableMultiThread
        {
            get => _enableMultiThread;
            set => _enableMultiThread = value;
        }

        #endregion

        #region Events

        public event EventHandler<RemoteExtractProgress> ExtractProgressChanged;

        #endregion

        #region Constructor

        public RemotePayloadService(Action<string> log = null, Action<long, long> progress = null, Action<string> logDetail = null)
        {
            _log = log ?? (msg => { });
            _progress = progress;
            _logDetail = logDetail ?? (msg => { });

            // 创建 HttpClientHandler 来处理自动重定向和解压缩
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false, // 手动处理重定向
                AutomaticDecompression = DecompressionMethods.None // 不自动解压缩
            };
            
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
            SetUserAgent(null); // 使用默认 User-Agent
        }

        /// <summary>
        /// 设置自定义 User-Agent
        /// </summary>
        public void SetUserAgent(string userAgent)
        {
            _httpClient.DefaultRequestHeaders.Clear();
            
            string ua = string.IsNullOrEmpty(userAgent)
                ? "SakuraEDL/2.0 (Payload Extractor)"
                : userAgent;
            
            _httpClient.DefaultRequestHeaders.Add("User-Agent", ua);
            _httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        }
        
        /// <summary>
        /// 重建 HttpClient (解决连接复用导致的卡死问题)
        /// </summary>
        private void RecreateHttpClient()
        {
            // 释放旧的 HttpClient
            _httpClient?.Dispose();
            
            // 创建新的 HttpClient
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None
            };
            
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
            SetUserAgent(null);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 获取真实下载链接 (处理重定向)
        /// </summary>
        public async Task<(string RealUrl, DateTime? ExpiresTime)> GetRedirectUrlAsync(string url, CancellationToken ct = default)
        {
            // 如果不是 downloadCheck 链接，直接返回
            if (!url.Contains("downloadCheck?"))
            {
                return (url, ParseExpiresTime(url));
            }

            _log("正在获取真实下载链接...");

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                    // 检查重定向
                    if ((int)response.StatusCode >= 301 && (int)response.StatusCode <= 308)
                    {
                        var location = response.Headers.Location?.ToString();
                        if (!string.IsNullOrEmpty(location))
                        {
                            _log("✓ 成功获取真实链接");
                            return (location, ParseExpiresTime(location));
                        }
                    }
                    else if (response.IsSuccessStatusCode)
                    {
                        return (url, ParseExpiresTime(url));
                    }
                }
                catch (TaskCanceledException)
                {
                    _log($"超时，重试 {attempt + 1}/3...");
                }
                catch (Exception ex)
                {
                    _log($"请求失败: {ex.Message}");
                }
            }

            return (null, null);
        }

        /// <summary>
        /// 从 URL 加载 Payload 信息 (不下载整个文件)
        /// </summary>
        public async Task<bool> LoadFromUrlAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url))
            {
                _log("URL 不能为空");
                return false;
            }

            try
            {
                // 重置状态
                _currentUrl = url;
                _partitions.Clear();
                IsLoaded = false;
                _totalSize = 0;
                _payloadDataOffset = 0;
                _dataStartOffset = 0;
                
                // 重建 HttpClient (避免连接复用问题导致卡死)
                RecreateHttpClient();

                _log($"正在连接: {GetUrlHost(url)}");

                // 1. 获取文件总大小 (使用 HEAD 请求)
                var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                
                if (!headResponse.IsSuccessStatusCode)
                {
                    // HEAD 请求失败，尝试用 GET 请求只读取头部
                    _logDetail("HEAD 请求失败，尝试 GET 请求...");
                    var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    
                    if (getResponse.Content.Headers.ContentRange?.Length != null)
                    {
                        _totalSize = getResponse.Content.Headers.ContentRange.Length.Value;
                    }
                    else if (!getResponse.IsSuccessStatusCode)
                    {
                        _log($"无法访问 URL: HTTP {(int)headResponse.StatusCode}");
                        return false;
                    }
                    else
                    {
                        _totalSize = getResponse.Content.Headers.ContentLength ?? 0;
                    }
                }
                else
                {
                    _totalSize = headResponse.Content.Headers.ContentLength ?? 0;
                }
                if (_totalSize == 0)
                {
                    _log("无法获取文件大小");
                    return false;
                }

                _log($"文件大小: {FormatSize(_totalSize)}");

                // 2. 判断是 ZIP 还是直接的 payload.bin
                string urlPath = url.Split('?')[0].ToLowerInvariant();
                bool isZip = urlPath.EndsWith(".zip") || urlPath.Contains("ota");

                if (isZip)
                {
                    _log("解析 ZIP 结构...");
                    await ParseZipStructureAsync(url, ct);
                }
                else
                {
                    // 直接是 payload.bin
                    _payloadDataOffset = 0;
                }

                // 3. 解析 Payload 头部和 Manifest
                await ParsePayloadHeaderAsync(url, ct);

                IsLoaded = true;
                _log($"✓ 成功解析: {_partitions.Count} 个分区");

                return true;
            }
            catch (Exception ex)
            {
                _log($"加载失败: {ex.Message}");
                _logDetail($"加载错误: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 从云端提取分区到本地文件
        /// </summary>
        public async Task<bool> ExtractPartitionAsync(string partitionName, string outputPath,
            CancellationToken ct = default)
        {
            if (!IsLoaded)
            {
                _log("请先加载 Payload");
                return false;
            }

            var partition = _partitions.FirstOrDefault(p =>
                p.Name.Equals(partitionName, StringComparison.OrdinalIgnoreCase));

            if (partition == null)
            {
                _log($"未找到分区: {partitionName}");
                return false;
            }

            try
            {
                _log($"开始提取 '{partitionName}' ({FormatSize((long)partition.Size)})");

                string outputDir = Path.GetDirectoryName(outputPath);
                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                int totalOps = partition.Operations.Count;
                int processedOps = 0;
                long downloadedBytes = 0;
                
                // 速度计算相关
                var startTime = DateTime.Now;
                var lastSpeedUpdateTime = startTime;
                long lastSpeedUpdateBytes = 0;
                double currentSpeed = 0;

                using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    // 预分配文件大小
                    outputStream.SetLength((long)partition.Size);

                    foreach (var operation in partition.Operations)
                    {
                        ct.ThrowIfCancellationRequested();

                        byte[] decompressedData = null;

                        if (operation.DataLength > 0)
                        {
                            // 下载操作数据 (大文件使用多线程加速)
                            long absStart = _dataStartOffset + (long)operation.DataOffset;
                            long absEnd = absStart + (long)operation.DataLength - 1;
                            long dataLen = absEnd - absStart + 1;

                            // 大于 2MB 的数据块使用多线程下载
                            byte[] compressedData = dataLen > 2 * 1024 * 1024
                                ? await FetchRangeMultiThreadAsync(_currentUrl, absStart, absEnd, ct)
                                : await FetchRangeAsync(_currentUrl, absStart, absEnd, ct);
                            downloadedBytes += compressedData.Length;

                            // 解压数据
                            decompressedData = DecompressData(operation.Type, compressedData,
                                (long)operation.DstNumBlocks * _blockSize);
                        }
                        else if (operation.Type == OP_ZERO)
                        {
                            // ZERO 操作
                            long totalBlocks = (long)operation.DstNumBlocks;
                            decompressedData = new byte[totalBlocks * _blockSize];
                        }

                        if (decompressedData != null)
                        {
                            // 写入目标位置
                            long dstOffset = (long)operation.DstStartBlock * _blockSize;
                            outputStream.Seek(dstOffset, SeekOrigin.Begin);
                            outputStream.Write(decompressedData, 0, 
                                Math.Min(decompressedData.Length, (int)((long)operation.DstNumBlocks * _blockSize)));
                        }

                        processedOps++;
                        double percent = 100.0 * processedOps / totalOps;
                        
                        // 计算速度 (每秒更新一次)
                        var now = DateTime.Now;
                        var timeSinceLastUpdate = (now - lastSpeedUpdateTime).TotalSeconds;
                        if (timeSinceLastUpdate >= 1.0)
                        {
                            long bytesSinceLastUpdate = downloadedBytes - lastSpeedUpdateBytes;
                            currentSpeed = bytesSinceLastUpdate / timeSinceLastUpdate;
                            lastSpeedUpdateTime = now;
                            lastSpeedUpdateBytes = downloadedBytes;
                        }
                        else if (currentSpeed == 0 && downloadedBytes > 0)
                        {
                            // 初始速度估算
                            var elapsed = (now - startTime).TotalSeconds;
                            if (elapsed > 0.1)
                            {
                                currentSpeed = downloadedBytes / elapsed;
                            }
                        }
                        
                        var elapsedTime = now - startTime;
                        
                        _progress?.Invoke(processedOps, totalOps);
                        ExtractProgressChanged?.Invoke(this, new RemoteExtractProgress
                        {
                            PartitionName = partitionName,
                            CurrentOperation = processedOps,
                            TotalOperations = totalOps,
                            DownloadedBytes = downloadedBytes,
                            Percent = percent,
                            SpeedBytesPerSecond = currentSpeed,
                            ElapsedTime = elapsedTime
                        });

                        if (processedOps % 50 == 0)
                        {
                            _logDetail($"进度: {processedOps}/{totalOps} ({percent:F1}%)");
                        }
                    }
                }

                var totalTime = DateTime.Now - startTime;
                double avgSpeed = downloadedBytes / Math.Max(totalTime.TotalSeconds, 0.1);
                _log($"✓ 提取完成: {Path.GetFileName(outputPath)}");
                _log($"下载数据: {FormatSize(downloadedBytes)}, 用时: {totalTime.TotalSeconds:F1}秒, 平均速度: {FormatSize((long)avgSpeed)}/s");

                return true;
            }
            catch (OperationCanceledException)
            {
                _log("提取已取消");
                return false;
            }
            catch (Exception ex)
            {
                _log($"提取失败: {ex.Message}");
                _logDetail($"提取错误: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 流式刷写事件参数
        /// </summary>
        public class StreamFlashProgressEventArgs : EventArgs
        {
            public string PartitionName { get; set; }
            public StreamFlashPhase Phase { get; set; }
            public double Percent { get; set; }
            public long DownloadedBytes { get; set; }
            public long TotalBytes { get; set; }
            public double DownloadSpeedBytesPerSecond { get; set; }
            public double FlashSpeedBytesPerSecond { get; set; }
            
            public string DownloadSpeedFormatted => FormatSpeed(DownloadSpeedBytesPerSecond);
            public string FlashSpeedFormatted => FormatSpeed(FlashSpeedBytesPerSecond);
            
            private static string FormatSpeed(double speed)
            {
                if (speed <= 0) return "计算中...";
                string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
                int unitIndex = 0;
                while (speed >= 1024 && unitIndex < units.Length - 1)
                {
                    speed /= 1024;
                    unitIndex++;
                }
                return $"{speed:F2} {units[unitIndex]}";
            }
        }
        
        public enum StreamFlashPhase
        {
            Downloading,
            Flashing,
            Completed
        }
        
        /// <summary>
        /// 流式刷写进度事件
        /// </summary>
        public event EventHandler<StreamFlashProgressEventArgs> StreamFlashProgressChanged;

        /// <summary>
        /// 从云端提取分区并直接刷写到设备
        /// </summary>
        /// <param name="partitionName">分区名</param>
        /// <param name="flashCallback">刷写回调，参数：临时文件路径，返回：是否成功，已刷写字节数，用时</param>
        /// <param name="ct">取消令牌</param>
        public async Task<bool> ExtractAndFlashPartitionAsync(
            string partitionName, 
            Func<string, Task<(bool success, long bytesFlashed, double elapsedSeconds)>> flashCallback,
            CancellationToken ct = default)
        {
            if (!IsLoaded)
            {
                _log("请先加载 Payload");
                return false;
            }

            var partition = _partitions.FirstOrDefault(p =>
                p.Name.Equals(partitionName, StringComparison.OrdinalIgnoreCase));

            if (partition == null)
            {
                _log($"未找到分区: {partitionName}");
                return false;
            }

            // 创建临时文件
            string tempPath = Path.Combine(Path.GetTempPath(), $"payload_{partitionName}_{Guid.NewGuid():N}.img");
            
            try
            {
                _log($"开始下载 '{partitionName}' ({FormatSize((long)partition.Size)})");

                // 下载阶段
                var downloadStartTime = DateTime.Now;
                long downloadedBytes = 0;
                int totalOps = partition.Operations.Count;
                int processedOps = 0;
                double downloadSpeed = 0;
                var lastSpeedUpdateTime = downloadStartTime;
                long lastSpeedUpdateBytes = 0;

                using (var outputStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    outputStream.SetLength((long)partition.Size);

                    foreach (var operation in partition.Operations)
                    {
                        ct.ThrowIfCancellationRequested();

                        byte[] decompressedData = null;

                        if (operation.DataLength > 0)
                        {
                            // 下载操作数据 (大文件使用多线程加速)
                            long absStart = _dataStartOffset + (long)operation.DataOffset;
                            long absEnd = absStart + (long)operation.DataLength - 1;
                            long dataLen = absEnd - absStart + 1;

                            // 大于 2MB 的数据块使用多线程下载
                            byte[] compressedData = dataLen > 2 * 1024 * 1024
                                ? await FetchRangeMultiThreadAsync(_currentUrl, absStart, absEnd, ct)
                                : await FetchRangeAsync(_currentUrl, absStart, absEnd, ct);
                            downloadedBytes += compressedData.Length;

                            decompressedData = DecompressData(operation.Type, compressedData,
                                (long)operation.DstNumBlocks * _blockSize);
                        }
                        else if (operation.Type == OP_ZERO)
                        {
                            long totalBlocks = (long)operation.DstNumBlocks;
                            decompressedData = new byte[totalBlocks * _blockSize];
                        }

                        if (decompressedData != null)
                        {
                            long dstOffset = (long)operation.DstStartBlock * _blockSize;
                            outputStream.Seek(dstOffset, SeekOrigin.Begin);
                            outputStream.Write(decompressedData, 0, 
                                Math.Min(decompressedData.Length, (int)((long)operation.DstNumBlocks * _blockSize)));
                        }

                        processedOps++;
                        
                        // 计算下载速度
                        var now = DateTime.Now;
                        var timeSinceLastUpdate = (now - lastSpeedUpdateTime).TotalSeconds;
                        if (timeSinceLastUpdate >= 1.0)
                        {
                            long bytesSinceLastUpdate = downloadedBytes - lastSpeedUpdateBytes;
                            downloadSpeed = bytesSinceLastUpdate / timeSinceLastUpdate;
                            lastSpeedUpdateTime = now;
                            lastSpeedUpdateBytes = downloadedBytes;
                        }
                        else if (downloadSpeed == 0 && downloadedBytes > 0)
                        {
                            var elapsed = (now - downloadStartTime).TotalSeconds;
                            if (elapsed > 0.1) downloadSpeed = downloadedBytes / elapsed;
                        }
                        
                        double downloadPercent = 50.0 * processedOps / totalOps; // 下载占 50%
                        
                        StreamFlashProgressChanged?.Invoke(this, new StreamFlashProgressEventArgs
                        {
                            PartitionName = partitionName,
                            Phase = StreamFlashPhase.Downloading,
                            Percent = downloadPercent,
                            DownloadedBytes = downloadedBytes,
                            TotalBytes = (long)partition.Size,
                            DownloadSpeedBytesPerSecond = downloadSpeed,
                            FlashSpeedBytesPerSecond = 0
                        });
                    }
                }

                var downloadTime = DateTime.Now - downloadStartTime;
                _log($"下载完成: {FormatSize(downloadedBytes)}, 用时: {downloadTime.TotalSeconds:F1}秒");

                // 刷写阶段
                _log($"开始刷写 '{partitionName}'...");
                
                StreamFlashProgressChanged?.Invoke(this, new StreamFlashProgressEventArgs
                {
                    PartitionName = partitionName,
                    Phase = StreamFlashPhase.Flashing,
                    Percent = 50,
                    DownloadedBytes = downloadedBytes,
                    TotalBytes = (long)partition.Size,
                    DownloadSpeedBytesPerSecond = downloadSpeed,
                    FlashSpeedBytesPerSecond = 0
                });

                var (flashSuccess, bytesFlashed, flashElapsed) = await flashCallback(tempPath);
                
                double flashSpeed = flashElapsed > 0 ? bytesFlashed / flashElapsed : 0;
                
                StreamFlashProgressChanged?.Invoke(this, new StreamFlashProgressEventArgs
                {
                    PartitionName = partitionName,
                    Phase = StreamFlashPhase.Completed,
                    Percent = 100,
                    DownloadedBytes = downloadedBytes,
                    TotalBytes = (long)partition.Size,
                    DownloadSpeedBytesPerSecond = downloadSpeed,
                    FlashSpeedBytesPerSecond = flashSpeed
                });

                if (flashSuccess)
                {
                    _log($"✓ 刷写成功: {partitionName} (Fastboot 速度: {FormatSize((long)flashSpeed)}/s)");
                }
                else
                {
                    _log($"✗ 刷写失败: {partitionName}");
                }

                return flashSuccess;
            }
            catch (OperationCanceledException)
            {
                _log("操作已取消");
                return false;
            }
            catch (Exception ex)
            {
                _log($"操作失败: {ex.Message}");
                _logDetail($"错误详情: {ex}");
                return false;
            }
            finally
            {
                // 清理临时文件
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
            }
        }

        /// <summary>
        /// 获取摘要信息
        /// </summary>
        public RemotePayloadSummary GetSummary()
        {
            if (!IsLoaded) return null;

            return new RemotePayloadSummary
            {
                Url = _currentUrl,
                TotalSize = _totalSize,
                BlockSize = _blockSize,
                PartitionCount = _partitions.Count,
                Partitions = _partitions.ToList()
            };
        }

        /// <summary>
        /// 关闭
        /// </summary>
        public void Close()
        {
            _currentUrl = null;
            _totalSize = 0;
            _partitions.Clear();
            IsLoaded = false;
        }

        #endregion

        #region Private Methods - ZIP Parsing

        private async Task ParseZipStructureAsync(string url, CancellationToken ct)
        {
            // 读取文件尾部查找 EOCD
            int readSize = (int)Math.Min(65536, _totalSize);
            long startOffset = _totalSize - readSize;
            byte[] tailData = await FetchRangeAsync(url, startOffset, _totalSize - 1, ct);

            // 查找 EOCD 签名
            int eocdPos = -1;
            for (int i = tailData.Length - 22; i >= 0; i--)
            {
                if (BitConverter.ToUInt32(tailData, i) == ZIP_EOCD_SIG)
                {
                    eocdPos = i;
                    break;
                }
            }

            if (eocdPos < 0)
                throw new Exception("无法找到 ZIP EOCD 记录");

            long eocdOffset = startOffset + eocdPos;
            bool isZip64 = false;
            long zip64EocdOffset = 0;

            // 检查 ZIP64
            if (eocdPos >= 20)
            {
                int locatorStart = eocdPos - 20;
                if (BitConverter.ToUInt32(tailData, locatorStart) == ZIP64_EOCD_LOCATOR_SIG)
                {
                    zip64EocdOffset = (long)BitConverter.ToUInt64(tailData, locatorStart + 8);
                    isZip64 = true;
                }
            }

            // 解析 EOCD 获取中央目录位置
            long centralDirOffset, centralDirSize;

            if (isZip64)
            {
                byte[] zip64EocdData = await FetchRangeAsync(url, zip64EocdOffset, zip64EocdOffset + 100, ct);
                if (BitConverter.ToUInt32(zip64EocdData, 0) != ZIP64_EOCD_SIG)
                    throw new Exception("ZIP64 EOCD 签名不匹配");

                centralDirSize = (long)BitConverter.ToUInt64(zip64EocdData, 40);
                centralDirOffset = (long)BitConverter.ToUInt64(zip64EocdData, 48);
            }
            else
            {
                centralDirSize = BitConverter.ToUInt32(tailData, eocdPos + 12);
                centralDirOffset = BitConverter.ToUInt32(tailData, eocdPos + 16);
            }

            // 下载中央目录
            byte[] centralDirData = await FetchRangeAsync(url, centralDirOffset,
                centralDirOffset + centralDirSize - 1, ct);

            // 查找 payload.bin
            int pos = 0;
            while (pos < centralDirData.Length - 4)
            {
                if (BitConverter.ToUInt32(centralDirData, pos) != ZIP_CENTRAL_DIR_SIG)
                    break;

                uint compressedSize = BitConverter.ToUInt32(centralDirData, pos + 20);
                uint uncompressedSize = BitConverter.ToUInt32(centralDirData, pos + 24);
                ushort filenameLen = BitConverter.ToUInt16(centralDirData, pos + 28);
                ushort extraLen = BitConverter.ToUInt16(centralDirData, pos + 30);
                ushort commentLen = BitConverter.ToUInt16(centralDirData, pos + 32);
                uint localHeaderOffset = BitConverter.ToUInt32(centralDirData, pos + 42);

                string filename = Encoding.UTF8.GetString(centralDirData, pos + 46, filenameLen);

                // 处理 ZIP64 扩展字段
                if (uncompressedSize == 0xFFFFFFFF || compressedSize == 0xFFFFFFFF || localHeaderOffset == 0xFFFFFFFF)
                {
                    int extraStart = pos + 46 + filenameLen;
                    int extraEnd = extraStart + extraLen;
                    int extraPos = extraStart;

                    while (extraPos + 4 <= extraEnd)
                    {
                        ushort headerId = BitConverter.ToUInt16(centralDirData, extraPos);
                        ushort dataSize = BitConverter.ToUInt16(centralDirData, extraPos + 2);

                        if (headerId == 0x0001) // ZIP64 extra field
                        {
                            int fieldPos = extraPos + 4;
                            if (uncompressedSize == 0xFFFFFFFF && fieldPos + 8 <= extraPos + 4 + dataSize)
                            {
                                uncompressedSize = (uint)BitConverter.ToUInt64(centralDirData, fieldPos);
                                fieldPos += 8;
                            }
                            if (compressedSize == 0xFFFFFFFF && fieldPos + 8 <= extraPos + 4 + dataSize)
                            {
                                compressedSize = (uint)BitConverter.ToUInt64(centralDirData, fieldPos);
                                fieldPos += 8;
                            }
                            if (localHeaderOffset == 0xFFFFFFFF && fieldPos + 8 <= extraPos + 4 + dataSize)
                            {
                                localHeaderOffset = (uint)BitConverter.ToUInt64(centralDirData, fieldPos);
                            }
                        }
                        extraPos += 4 + dataSize;
                    }
                }

                if (filename.Equals("payload.bin", StringComparison.OrdinalIgnoreCase))
                {
                    // 读取本地文件头
                    byte[] lfhData = await FetchRangeAsync(url, localHeaderOffset, localHeaderOffset + 30, ct);

                    if (BitConverter.ToUInt32(lfhData, 0) != ZIP_LOCAL_FILE_HEADER_SIG)
                        throw new Exception("本地文件头签名不匹配");

                    ushort lfhFilenameLen = BitConverter.ToUInt16(lfhData, 26);
                    ushort lfhExtraLen = BitConverter.ToUInt16(lfhData, 28);

                    _payloadDataOffset = localHeaderOffset + 30 + lfhFilenameLen + lfhExtraLen;
                    _logDetail($"payload.bin 数据偏移: 0x{_payloadDataOffset:X}");
                    return;
                }

                pos += 46 + filenameLen + extraLen + commentLen;
            }

            throw new Exception("ZIP 中未找到 payload.bin");
        }

        #endregion

        #region Private Methods - Payload Parsing

        private async Task ParsePayloadHeaderAsync(string url, CancellationToken ct)
        {
            // 读取 Payload 头部 (24 bytes for v2)
            byte[] headerData = await FetchRangeAsync(url, _payloadDataOffset, _payloadDataOffset + 23, ct);

            // 验证 Magic
            uint magic = ReadBigEndianUInt32(headerData, 0);
            if (magic != PAYLOAD_MAGIC)
                throw new Exception($"无效的 Payload 魔数: 0x{magic:X8}");

            ulong version = ReadBigEndianUInt64(headerData, 4);
            ulong manifestLen = ReadBigEndianUInt64(headerData, 12);
            uint metadataSignatureLen = version >= 2 ? ReadBigEndianUInt32(headerData, 20) : 0;
            int payloadHeaderLen = version >= 2 ? 24 : 20;

            _logDetail($"Payload 版本: {version}");
            _logDetail($"Manifest 大小: {manifestLen} bytes");

            // 下载 Manifest
            long manifestOffset = _payloadDataOffset + payloadHeaderLen;
            _log($"下载 Manifest ({FormatSize((long)manifestLen)})...");
            byte[] manifestData = await FetchRangeAsync(url, manifestOffset, 
                manifestOffset + (long)manifestLen - 1, ct);

            // 解析 Manifest
            ParseManifest(manifestData);

            // 计算数据起始位置
            _dataStartOffset = _payloadDataOffset + payloadHeaderLen + (long)manifestLen + metadataSignatureLen;
            _logDetail($"数据起始偏移: 0x{_dataStartOffset:X}");
        }

        private void ParseManifest(byte[] data)
        {
            int pos = 0;

            while (pos < data.Length)
            {
                var (fieldNumber, wireType, value, newPos) = ReadProtobufField(data, pos);
                if (fieldNumber == 0) break;
                pos = newPos;

                switch (fieldNumber)
                {
                    case 3: // block_size
                        _blockSize = (uint)(ulong)value;
                        break;
                    case 13: // partitions
                        if (wireType == 2)
                        {
                            var partition = ParsePartitionUpdate((byte[])value);
                            if (partition != null)
                                _partitions.Add(partition);
                        }
                        break;
                }
            }
        }

        private RemotePayloadPartition ParsePartitionUpdate(byte[] data)
        {
            var partition = new RemotePayloadPartition();
            int pos = 0;

            while (pos < data.Length)
            {
                var (fieldNumber, wireType, value, newPos) = ReadProtobufField(data, pos);
                if (fieldNumber == 0) break;
                pos = newPos;

                switch (fieldNumber)
                {
                    case 1: // partition_name
                        if (wireType == 2)
                            partition.Name = Encoding.UTF8.GetString((byte[])value);
                        break;
                    case 7: // new_partition_info
                        if (wireType == 2)
                            ParsePartitionInfo((byte[])value, partition);
                        break;
                    case 8: // operations
                        if (wireType == 2)
                        {
                            var op = ParseInstallOperation((byte[])value);
                            if (op != null)
                                partition.Operations.Add(op);
                        }
                        break;
                }
            }

            return string.IsNullOrEmpty(partition.Name) ? null : partition;
        }

        private void ParsePartitionInfo(byte[] data, RemotePayloadPartition partition)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                var (fieldNumber, wireType, value, newPos) = ReadProtobufField(data, pos);
                if (fieldNumber == 0) break;
                pos = newPos;

                if (fieldNumber == 1) // size
                    partition.Size = (ulong)value;
                else if (fieldNumber == 2 && wireType == 2) // hash
                    partition.Hash = (byte[])value;
            }
        }

        private RemotePayloadOperation ParseInstallOperation(byte[] data)
        {
            var op = new RemotePayloadOperation();
            int pos = 0;

            while (pos < data.Length)
            {
                var (fieldNumber, wireType, value, newPos) = ReadProtobufField(data, pos);
                if (fieldNumber == 0) break;
                pos = newPos;

                switch (fieldNumber)
                {
                    case 1: // type
                        op.Type = (int)(ulong)value;
                        break;
                    case 2: // data_offset
                        op.DataOffset = (ulong)value;
                        break;
                    case 3: // data_length
                        op.DataLength = (ulong)value;
                        break;
                    case 6: // dst_extents
                        if (wireType == 2)
                            ParseExtent((byte[])value, op);
                        break;
                }
            }

            return op;
        }

        private void ParseExtent(byte[] data, RemotePayloadOperation op)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                var (fieldNumber, wireType, value, newPos) = ReadProtobufField(data, pos);
                if (fieldNumber == 0) break;
                pos = newPos;

                if (fieldNumber == 1) // start_block
                    op.DstStartBlock = (ulong)value;
                else if (fieldNumber == 2) // num_blocks
                    op.DstNumBlocks = (ulong)value;
            }
        }

        #endregion

        #region Private Methods - Helpers

        private async Task<byte[]> FetchRangeAsync(string url, long start, long end, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

            // 使用 ResponseHeadersRead 避免预先缓冲整个响应
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            
            if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
                throw new Exception($"HTTP {(int)response.StatusCode}");

            // 计算需要读取的字节数
            long bytesToRead = end - start + 1;
            
            // 如果服务器支持 Range 请求 (206)，直接读取内容
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    byte[] buffer = new byte[bytesToRead];
                    int totalRead = 0;
                    while (totalRead < bytesToRead)
                    {
                        int read = await stream.ReadAsync(buffer, totalRead, (int)Math.Min(bytesToRead - totalRead, 81920), ct);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    if (totalRead < bytesToRead)
                    {
                        Array.Resize(ref buffer, totalRead);
                    }
                    return buffer;
                }
            }
            else
            {
                // 服务器返回 200 OK (不支持 Range)，需要跳过并只读取指定范围
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    // 跳过 start 之前的字节
                    byte[] skipBuffer = new byte[81920];
                    long skipped = 0;
                    while (skipped < start)
                    {
                        int toSkip = (int)Math.Min(start - skipped, skipBuffer.Length);
                        int read = await stream.ReadAsync(skipBuffer, 0, toSkip, ct);
                        if (read == 0) throw new Exception("无法跳过到指定位置");
                        skipped += read;
                    }

                    // 读取指定范围的数据
                    byte[] buffer = new byte[bytesToRead];
                    int totalRead = 0;
                    while (totalRead < bytesToRead)
                    {
                        int read = await stream.ReadAsync(buffer, totalRead, (int)Math.Min(bytesToRead - totalRead, 81920), ct);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    if (totalRead < bytesToRead)
                    {
                        Array.Resize(ref buffer, totalRead);
                    }
                    return buffer;
                }
            }
        }
        
        /// <summary>
        /// 多线程分块下载 (类似 IDM/NDM)
        /// 将大文件分成多个块并行下载，最大化带宽利用率
        /// </summary>
        private async Task<byte[]> FetchRangeMultiThreadAsync(string url, long start, long end, CancellationToken ct)
        {
            long totalBytes = end - start + 1;
            
            // 小文件或禁用多线程时，使用单线程下载
            if (!_enableMultiThread || totalBytes < _minChunkSize * 2 || _maxConnections <= 1)
            {
                return await FetchRangeAsync(url, start, end, ct);
            }
            
            // 计算最优分块大小和数量
            int numChunks = Math.Min(_maxConnections, (int)Math.Ceiling((double)totalBytes / _minChunkSize));
            long chunkSize = totalBytes / numChunks;
            
            // 创建结果缓冲区
            byte[] result = new byte[totalBytes];
            
            // 创建下载任务
            var downloadTasks = new List<Task>();
            var chunkInfos = new List<(long chunkStart, long chunkEnd, int resultOffset)>();
            
            long currentStart = start;
            int resultOffset = 0;
            
            for (int i = 0; i < numChunks; i++)
            {
                long chunkEnd = (i == numChunks - 1) ? end : (currentStart + chunkSize - 1);
                int chunkLen = (int)(chunkEnd - currentStart + 1);
                
                chunkInfos.Add((currentStart, chunkEnd, resultOffset));
                
                resultOffset += chunkLen;
                currentStart = chunkEnd + 1;
            }
            
            // 使用信号量限制并发数
            var semaphore = new SemaphoreSlim(_maxConnections);
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            
            foreach (var (chunkStart, chunkEnd, offset) in chunkInfos)
            {
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var chunkData = await FetchChunkAsync(url, chunkStart, chunkEnd, ct);
                        if (chunkData != null)
                        {
                            lock (result)
                            {
                                Buffer.BlockCopy(chunkData, 0, result, offset, chunkData.Length);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);
                
                downloadTasks.Add(task);
            }
            
            await Task.WhenAll(downloadTasks);
            
            if (exceptions.Count > 0)
            {
                throw new AggregateException("多线程下载失败", exceptions);
            }
            
            return result;
        }
        
        /// <summary>
        /// 下载单个分块 (使用独立的 HttpClient 避免连接复用限制)
        /// </summary>
        private async Task<byte[]> FetchChunkAsync(string url, long start, long end, CancellationToken ct)
        {
            // 为每个分块创建独立的请求
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None
            };
            
            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.Add("User-Agent", "SakuraEDL/2.0 (Payload Extractor)");
                client.DefaultRequestHeaders.Add("Accept", "*/*");
                
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                
                if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception($"分块下载失败: HTTP {(int)response.StatusCode}");
                }
                
                long bytesToRead = end - start + 1;
                
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    byte[] buffer = new byte[bytesToRead];
                    int totalRead = 0;
                    
                    while (totalRead < bytesToRead)
                    {
                        int read = await stream.ReadAsync(buffer, totalRead, 
                            (int)Math.Min(bytesToRead - totalRead, 131072), ct); // 128KB 块
                        if (read == 0) break;
                        totalRead += read;
                    }
                    
                    if (totalRead < bytesToRead)
                    {
                        Array.Resize(ref buffer, totalRead);
                    }
                    
                    return buffer;
                }
            }
        }

        private (int fieldNumber, int wireType, object value, int newPos) ReadProtobufField(byte[] data, int pos)
        {
            if (pos >= data.Length)
                return (0, 0, null, pos);

            ulong tag = ReadVarint(data, ref pos);
            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            object value = null;

            switch (wireType)
            {
                case 0: // Varint
                    value = ReadVarint(data, ref pos);
                    break;
                case 1: // 64-bit
                    value = BitConverter.ToUInt64(data, pos);
                    pos += 8;
                    break;
                case 2: // Length-delimited
                    int length = (int)ReadVarint(data, ref pos);
                    value = new byte[length];
                    Array.Copy(data, pos, (byte[])value, 0, length);
                    pos += length;
                    break;
                case 5: // 32-bit
                    value = BitConverter.ToUInt32(data, pos);
                    pos += 4;
                    break;
                default:
                    throw new Exception($"Unknown wire type: {wireType}");
            }

            return (fieldNumber, wireType, value, pos);
        }

        private ulong ReadVarint(byte[] data, ref int pos)
        {
            ulong result = 0;
            int shift = 0;

            while (pos < data.Length)
            {
                byte b = data[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
            }

            return result;
        }

        private uint ReadBigEndianUInt32(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                          (data[offset + 2] << 8) | data[offset + 3]);
        }

        private ulong ReadBigEndianUInt64(byte[] data, int offset)
        {
            return ((ulong)data[offset] << 56) | ((ulong)data[offset + 1] << 48) |
                   ((ulong)data[offset + 2] << 40) | ((ulong)data[offset + 3] << 32) |
                   ((ulong)data[offset + 4] << 24) | ((ulong)data[offset + 5] << 16) |
                   ((ulong)data[offset + 6] << 8) | data[offset + 7];
        }

        private byte[] DecompressData(int opType, byte[] data, long expectedLength)
        {
            switch (opType)
            {
                case OP_REPLACE:
                    return data;

                case OP_REPLACE_XZ:
                    // XZ 解压
                    try
                    {
                        using (var input = new MemoryStream(data))
                        using (var output = new MemoryStream())
                        {
                            // 使用简单的 LZMA 解压（需要 System.IO.Compression 或第三方库）
                            // 这里返回原始数据，实际使用需要实现 XZ 解压
                            _logDetail("XZ 解压暂未实现，返回原始数据");
                            return data;
                        }
                    }
                    catch
                    {
                        return data;
                    }

                case OP_REPLACE_BZ:
                    // BZip2 解压
                    _logDetail("BZip2 解压暂未实现，返回原始数据");
                    return data;

                case OP_ZERO:
                    return new byte[expectedLength];

                default:
                    return data;
            }
        }

        private DateTime? ParseExpiresTime(string url)
        {
            try
            {
                var uri = new Uri(url);
                var queryParams = ParseQueryString(uri.Query);

                string expiresStr = null;
                if (queryParams.TryGetValue("Expires", out string expires))
                    expiresStr = expires;
                else if (queryParams.TryGetValue("x-oss-expires", out string ossExpires))
                    expiresStr = ossExpires;

                if (!string.IsNullOrEmpty(expiresStr) && long.TryParse(expiresStr, out long timestamp))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 简单的 URL 查询字符串解析
        /// </summary>
        private Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return result;

            // 去掉开头的 ?
            if (query.StartsWith("?"))
                query = query.Substring(1);

            foreach (var pair in query.Split('&'))
            {
                var parts = pair.Split(new[] { '=' }, 2);
                if (parts.Length == 2)
                {
                    string key = Uri.UnescapeDataString(parts[0]);
                    string value = Uri.UnescapeDataString(parts[1]);
                    result[key] = value;
                }
                else if (parts.Length == 1 && !string.IsNullOrEmpty(parts[0]))
                {
                    result[Uri.UnescapeDataString(parts[0])] = "";
                }
            }
            return result;
        }

        private string GetUrlHost(string url)
        {
            try
            {
                return new Uri(url).Host;
            }
            catch
            {
                return url;
            }
        }

        private string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return $"{size:F2} {units[unitIndex]}";
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _disposed = true;
            }
        }

        #endregion
    }

    #region Data Classes

    /// <summary>
    /// 远程 Payload 分区信息
    /// </summary>
    public class RemotePayloadPartition
    {
        public string Name { get; set; }
        public ulong Size { get; set; }
        public byte[] Hash { get; set; }
        public List<RemotePayloadOperation> Operations { get; set; } = new List<RemotePayloadOperation>();

        public string SizeFormatted
        {
            get
            {
                string[] units = { "B", "KB", "MB", "GB" };
                double size = Size;
                int unitIndex = 0;
                while (size >= 1024 && unitIndex < units.Length - 1)
                {
                    size /= 1024;
                    unitIndex++;
                }
                return $"{size:F2} {units[unitIndex]}";
            }
        }
    }

    /// <summary>
    /// 远程 Payload 操作
    /// </summary>
    public class RemotePayloadOperation
    {
        public int Type { get; set; }
        public ulong DataOffset { get; set; }
        public ulong DataLength { get; set; }
        public ulong DstStartBlock { get; set; }
        public ulong DstNumBlocks { get; set; }
    }

    /// <summary>
    /// 远程提取进度
    /// </summary>
    public class RemoteExtractProgress
    {
        public string PartitionName { get; set; }
        public int CurrentOperation { get; set; }
        public int TotalOperations { get; set; }
        public long DownloadedBytes { get; set; }
        public double Percent { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        
        /// <summary>
        /// 格式化的速度显示
        /// </summary>
        public string SpeedFormatted
        {
            get
            {
                if (SpeedBytesPerSecond <= 0) return "计算中...";
                
                string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
                double speed = SpeedBytesPerSecond;
                int unitIndex = 0;
                while (speed >= 1024 && unitIndex < units.Length - 1)
                {
                    speed /= 1024;
                    unitIndex++;
                }
                return $"{speed:F2} {units[unitIndex]}";
            }
        }
    }

    /// <summary>
    /// 远程 Payload 摘要
    /// </summary>
    public class RemotePayloadSummary
    {
        public string Url { get; set; }
        public long TotalSize { get; set; }
        public uint BlockSize { get; set; }
        public int PartitionCount { get; set; }
        public List<RemotePayloadPartition> Partitions { get; set; }

        public string TotalSizeFormatted
        {
            get
            {
                string[] units = { "B", "KB", "MB", "GB" };
                double size = TotalSize;
                int unitIndex = 0;
                while (size >= 1024 && unitIndex < units.Length - 1)
                {
                    size /= 1024;
                    unitIndex++;
                }
                return $"{size:F2} {units[unitIndex]}";
            }
        }
    }

    #endregion
}
