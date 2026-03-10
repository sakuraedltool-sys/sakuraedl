// ============================================================================
// SakuraEDL - Payload Service | Payload 服务
// ============================================================================
// [ZH] Payload 提取服务 - 解析 Android OTA payload.bin
// [EN] Payload Extract Service - Parse Android OTA payload.bin
// [JA] Payload抽出サービス - Android OTA payload.bin解析
// [KO] Payload 추출 서비스 - Android OTA payload.bin 분석
// [RU] Сервис извлечения Payload - Разбор Android OTA payload.bin
// [ES] Servicio de extracción Payload - Análisis de Android OTA payload.bin
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Fastboot.Payload
{
    /// <summary>
    /// Payload 服务
    /// 提供 payload.bin 解析、分区提取、直接刷写等高级功能
    /// </summary>
    public class PayloadService : IDisposable
    {
        #region Fields
        
        private PayloadParser _parser;
        private string _currentPayloadPath;
        private bool _disposed;
        
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;
        private readonly Action<int, int> _progress;
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// 是否已加载 Payload
        /// </summary>
        public bool IsLoaded => _parser?.IsInitialized ?? false;
        
        /// <summary>
        /// 当前 Payload 路径
        /// </summary>
        public string CurrentPayloadPath => _currentPayloadPath;
        
        /// <summary>
        /// 分区列表
        /// </summary>
        public IReadOnlyList<PayloadPartition> Partitions => _parser?.Partitions ?? new List<PayloadPartition>();
        
        /// <summary>
        /// 文件格式版本
        /// </summary>
        public ulong FileFormatVersion => _parser?.FileFormatVersion ?? 0;
        
        /// <summary>
        /// Block 大小
        /// </summary>
        public uint BlockSize => _parser?.BlockSize ?? 4096;
        
        #endregion
        
        #region Events
        
        /// <summary>
        /// 提取进度事件
        /// </summary>
        public event EventHandler<PayloadExtractProgress> ExtractProgressChanged;
        
        #endregion
        
        #region Constructor
        
        public PayloadService(Action<string> log = null, Action<int, int> progress = null, Action<string> logDetail = null)
        {
            _log = log ?? (msg => { });
            _progress = progress;
            _logDetail = logDetail ?? (msg => { });
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// 加载 Payload 文件
        /// </summary>
        public async Task<bool> LoadPayloadAsync(string filePath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _log("文件路径不能为空");
                return false;
            }
            
            if (!File.Exists(filePath))
            {
                _log($"文件不存在: {filePath}");
                return false;
            }
            
            // 清理之前的解析器
            _parser?.Dispose();
            _parser = new PayloadParser(_log, _logDetail);
            
            _log($"正在加载 Payload: {Path.GetFileName(filePath)}...");
            
            bool result = await _parser.LoadAsync(filePath, ct);
            
            if (result)
            {
                _currentPayloadPath = filePath;
                LogPayloadInfo();
            }
            
            return result;
        }
        
        /// <summary>
        /// 提取单个分区
        /// </summary>
        public async Task<bool> ExtractPartitionAsync(string partitionName, string outputPath, CancellationToken ct = default)
        {
            if (!IsLoaded)
            {
                _log("请先加载 Payload 文件");
                return false;
            }
            
            var progress = new Progress<PayloadExtractProgress>(p =>
            {
                ExtractProgressChanged?.Invoke(this, p);
                _progress?.Invoke((int)p.Percent, 100);
            });
            
            return await _parser.ExtractPartitionAsync(partitionName, outputPath, progress, ct);
        }
        
        /// <summary>
        /// 提取所有分区
        /// </summary>
        public async Task<int> ExtractAllPartitionsAsync(string outputDir, CancellationToken ct = default)
        {
            if (!IsLoaded)
            {
                _log("请先加载 Payload 文件");
                return 0;
            }
            
            var progress = new Progress<PayloadExtractProgress>(p =>
            {
                ExtractProgressChanged?.Invoke(this, p);
                _progress?.Invoke((int)p.Percent, 100);
            });
            
            return await _parser.ExtractAllPartitionsAsync(outputDir, progress, ct);
        }
        
        /// <summary>
        /// 提取选定的分区
        /// </summary>
        public async Task<int> ExtractSelectedPartitionsAsync(IEnumerable<string> partitionNames, string outputDir, CancellationToken ct = default)
        {
            if (!IsLoaded)
            {
                _log("请先加载 Payload 文件");
                return 0;
            }
            
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            
            var names = partitionNames.ToList();
            int successCount = 0;
            int total = names.Count;
            
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                
                var name = names[i];
                var outputPath = Path.Combine(outputDir, $"{name}.img");
                
                _log($"正在提取 {name} ({i + 1}/{total})...");
                
                if (await _parser.ExtractPartitionAsync(name, outputPath, null, ct))
                {
                    successCount++;
                }
                
                _progress?.Invoke(i + 1, total);
            }
            
            _log($"提取完成: {successCount}/{total} 个分区");
            return successCount;
        }
        
        /// <summary>
        /// 获取分区信息
        /// </summary>
        public PayloadPartition GetPartitionInfo(string partitionName)
        {
            return Partitions.FirstOrDefault(p => 
                p.Name.Equals(partitionName, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// 检查分区是否存在
        /// </summary>
        public bool HasPartition(string partitionName)
        {
            return Partitions.Any(p => 
                p.Name.Equals(partitionName, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// 获取所有分区名称
        /// </summary>
        public IEnumerable<string> GetPartitionNames()
        {
            return Partitions.Select(p => p.Name);
        }
        
        /// <summary>
        /// 获取 Payload 摘要信息
        /// </summary>
        public PayloadSummary GetSummary()
        {
            if (!IsLoaded)
                return null;
            
            return new PayloadSummary
            {
                FilePath = _currentPayloadPath,
                FileName = Path.GetFileName(_currentPayloadPath),
                FileFormatVersion = FileFormatVersion,
                BlockSize = BlockSize,
                PartitionCount = Partitions.Count,
                TotalSize = (ulong)Partitions.Sum(p => (long)p.Size),
                TotalCompressedSize = (ulong)Partitions.Sum(p => (long)p.CompressedSize),
                Partitions = Partitions.ToList()
            };
        }
        
        /// <summary>
        /// 关闭 Payload
        /// </summary>
        public void Close()
        {
            _parser?.Dispose();
            _parser = null;
            _currentPayloadPath = null;
        }
        
        #endregion
        
        #region Private Methods
        
        private void LogPayloadInfo()
        {
            _log($"[Payload] 格式版本: {FileFormatVersion}");
            _log($"[Payload] Block 大小: {BlockSize} bytes");
            _log($"[Payload] 分区数量: {Partitions.Count}");
            
            // 输出分区列表
            _logDetail("分区列表:");
            foreach (var partition in Partitions)
            {
                _logDetail($"  - {partition.Name}: {partition.SizeFormatted} (压缩: {partition.CompressedSizeFormatted})");
            }
        }
        
        #endregion
        
        #region IDisposable
        
        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _disposed = true;
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// Payload 摘要信息
    /// </summary>
    public class PayloadSummary
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public ulong FileFormatVersion { get; set; }
        public uint BlockSize { get; set; }
        public int PartitionCount { get; set; }
        public ulong TotalSize { get; set; }
        public ulong TotalCompressedSize { get; set; }
        public List<PayloadPartition> Partitions { get; set; }
        
        public string TotalSizeFormatted => FormatSize(TotalSize);
        public string TotalCompressedSizeFormatted => FormatSize(TotalCompressedSize);
        public double CompressionRatio => TotalSize > 0 ? (double)TotalCompressedSize / TotalSize : 1;
        
        private static string FormatSize(ulong bytes)
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
    }
}
