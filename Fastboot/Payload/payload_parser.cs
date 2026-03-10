// ============================================================================
// SakuraEDL - Payload Parser | Payload 解析器
// ============================================================================
// [ZH] Payload 解析器 - 解析 Android OTA payload.bin 结构
// [EN] Payload Parser - Parse Android OTA payload.bin structure
// [JA] Payload解析器 - Android OTA payload.bin構造を解析
// [KO] Payload 파서 - Android OTA payload.bin 구조 분석
// [RU] Парсер Payload - Разбор структуры Android OTA payload.bin
// [ES] Analizador Payload - Análisis de estructura payload.bin OTA
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Fastboot.Payload
{
    /// <summary>
    /// Android OTA Payload.bin 解析器
    /// 支持解析 A/B OTA 更新包中的 payload.bin 文件
    /// 
    /// Payload.bin 文件格式 (Chrome OS Update Engine):
    /// - Magic: "CrAU" (4 bytes)
    /// - File format version: uint64 (big-endian)
    /// - Manifest size: uint64 (big-endian)
    /// - Metadata signature size: uint32 (big-endian) [version >= 2]
    /// - Manifest: protobuf (DeltaArchiveManifest)
    /// - Metadata signature
    /// - Data blocks
    /// - Payload signatures
    /// 
    /// 注意：此实现使用简化的 protobuf 解析，不依赖 Google.Protobuf 库
    /// </summary>
    public class PayloadParser : IDisposable
    {
        #region Constants
        
        private const string PAYLOAD_MAGIC = "CrAU";
        
        // Protobuf wire types
        private const int WIRE_TYPE_VARINT = 0;
        private const int WIRE_TYPE_FIXED64 = 1;
        private const int WIRE_TYPE_LENGTH_DELIMITED = 2;
        private const int WIRE_TYPE_FIXED32 = 5;
        
        // DeltaArchiveManifest field numbers
        private const int FIELD_BLOCK_SIZE = 3;
        private const int FIELD_SIGNATURES_OFFSET = 4;
        private const int FIELD_SIGNATURES_SIZE = 5;
        private const int FIELD_PARTITIONS = 13;
        
        // PartitionUpdate field numbers
        private const int FIELD_PARTITION_NAME = 1;
        private const int FIELD_OPERATIONS = 8;
        private const int FIELD_NEW_PARTITION_INFO = 7;
        
        // InstallOperation field numbers
        private const int FIELD_OP_TYPE = 1;
        private const int FIELD_OP_DATA_OFFSET = 2;
        private const int FIELD_OP_DATA_LENGTH = 3;
        private const int FIELD_OP_DST_EXTENTS = 6;
        
        // PartitionInfo field numbers
        private const int FIELD_INFO_SIZE = 1;
        private const int FIELD_INFO_HASH = 2;
        
        // Extent field numbers
        private const int FIELD_EXTENT_START_BLOCK = 1;
        private const int FIELD_EXTENT_NUM_BLOCKS = 2;
        
        // InstallOperation Types
        public const int OP_REPLACE = 0;
        public const int OP_REPLACE_BZ = 1;
        public const int OP_REPLACE_XZ = 8;
        public const int OP_ZERO = 6;
        
        #endregion
        
        #region Fields
        
        private BinaryReader _reader;
        private Stream _stream;
        private bool _ownsStream;
        private bool _disposed;
        private long _dataStartOffset;
        private string _tempFilePath;
        
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;
        
        #endregion
        
        #region Properties
        
        public ulong FileFormatVersion { get; private set; }
        public ulong ManifestSize { get; private set; }
        public uint MetadataSignatureSize { get; private set; }
        public uint BlockSize { get; private set; } = 4096;
        public ulong SignaturesOffset { get; private set; }
        public ulong SignaturesSize { get; private set; }
        public IReadOnlyList<PayloadPartition> Partitions { get; private set; }
        public bool IsInitialized { get; private set; }
        
        #endregion
        
        #region Constructor
        
        public PayloadParser(Action<string> log = null, Action<string> logDetail = null)
        {
            _log = log ?? (msg => { });
            _logDetail = logDetail ?? (msg => { });
            Partitions = new List<PayloadPartition>();
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// 从文件路径加载 Payload
        /// </summary>
        public async Task<bool> LoadAsync(string filePath, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));
            
            // 支持 ZIP 文件
            if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return await LoadFromZipAsync(filePath, ct);
            }
            
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ownsStream = true;
            return await LoadFromStreamAsync(stream, ct);
        }
        
        /// <summary>
        /// 从流加载 Payload
        /// </summary>
        public async Task<bool> LoadFromStreamAsync(Stream stream, CancellationToken ct = default)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            
            _stream = stream;
            _reader = new BinaryReader(stream, Encoding.ASCII, true);
            
            return await Task.Run(() => ParsePayload(ct), ct);
        }
        
        /// <summary>
        /// 从 ZIP 文件加载 Payload
        /// </summary>
        public async Task<bool> LoadFromZipAsync(string zipPath, CancellationToken ct = default)
        {
            _log("正在从 ZIP 文件提取 payload.bin...");
            
            try
            {
                using (var zipArchive = ZipFile.OpenRead(zipPath))
                {
                    var payloadEntry = zipArchive.Entries.FirstOrDefault(e => 
                        e.Name.Equals("payload.bin", StringComparison.OrdinalIgnoreCase));
                    
                    if (payloadEntry == null)
                    {
                        _log("ZIP 文件中未找到 payload.bin");
                        return false;
                    }
                    
                    // 提取到临时文件
                    _tempFilePath = Path.Combine(Path.GetTempPath(), $"payload_{Guid.NewGuid()}.bin");
                    payloadEntry.ExtractToFile(_tempFilePath, true);
                    
                    var stream = new FileStream(_tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _ownsStream = true;
                    
                    return await LoadFromStreamAsync(stream, ct);
                }
            }
            catch (Exception ex)
            {
                _log($"从 ZIP 加载失败: {ex.Message}");
                _logDetail($"ZIP 加载错误: {ex}");
                return false;
            }
        }
        
        /// <summary>
        /// 提取指定分区到文件
        /// </summary>
        public async Task<bool> ExtractPartitionAsync(string partitionName, string outputPath,
            IProgress<PayloadExtractProgress> progress = null, CancellationToken ct = default)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Payload 尚未初始化");
            
            var partition = Partitions.FirstOrDefault(p => 
                p.Name.Equals(partitionName, StringComparison.OrdinalIgnoreCase));
            
            if (partition == null)
            {
                _log($"未找到分区: {partitionName}");
                return false;
            }
            
            return await ExtractPartitionInternalAsync(partition, outputPath, progress, ct);
        }
        
        /// <summary>
        /// 提取所有分区到目录
        /// </summary>
        public async Task<int> ExtractAllPartitionsAsync(string outputDir,
            IProgress<PayloadExtractProgress> progress = null, CancellationToken ct = default)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Payload 尚未初始化");
            
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            
            int successCount = 0;
            int totalPartitions = Partitions.Count;
            
            for (int i = 0; i < totalPartitions; i++)
            {
                ct.ThrowIfCancellationRequested();
                
                var partition = Partitions[i];
                var outputPath = Path.Combine(outputDir, $"{partition.Name}.img");
                
                _log($"正在提取 {partition.Name} ({i + 1}/{totalPartitions})...");
                
                if (await ExtractPartitionInternalAsync(partition, outputPath, null, ct))
                {
                    successCount++;
                }
                
                progress?.Report(new PayloadExtractProgress
                {
                    PartitionName = partition.Name,
                    CurrentPartition = i + 1,
                    TotalPartitions = totalPartitions,
                    Percent = ((double)(i + 1) / totalPartitions) * 100
                });
            }
            
            _log($"提取完成: {successCount}/{totalPartitions} 个分区");
            return successCount;
        }
        
        #endregion
        
        #region Private Methods - Parsing
        
        private bool ParsePayload(CancellationToken ct)
        {
            try
            {
                _logDetail("开始解析 Payload 头部...");
                
                // 读取 Magic
                byte[] magicBytes = _reader.ReadBytes(4);
                string magic = Encoding.ASCII.GetString(magicBytes);
                
                if (magic != PAYLOAD_MAGIC)
                {
                    _log($"无效的 Payload 文件 (Magic: {magic}, 期望: {PAYLOAD_MAGIC})");
                    return false;
                }
                
                // 读取文件格式版本 (big-endian uint64)
                FileFormatVersion = ReadBigEndianUInt64();
                _logDetail($"文件格式版本: {FileFormatVersion}");
                
                if (FileFormatVersion < 2)
                {
                    _log($"不支持的 Payload 版本: {FileFormatVersion} (需要 >= 2)");
                    return false;
                }
                
                // 读取 Manifest 大小 (big-endian uint64)
                ManifestSize = ReadBigEndianUInt64();
                _logDetail($"Manifest 大小: {ManifestSize} bytes");
                
                // 读取 Metadata 签名大小 (big-endian uint32, version >= 2)
                MetadataSignatureSize = ReadBigEndianUInt32();
                _logDetail($"Metadata 签名大小: {MetadataSignatureSize} bytes");
                
                ct.ThrowIfCancellationRequested();
                
                // 解析 Manifest (protobuf)
                if (ManifestSize > int.MaxValue)
                {
                    _log("Manifest 大小超出限制");
                    return false;
                }
                
                byte[] manifestData = _reader.ReadBytes((int)ManifestSize);
                ParseManifest(manifestData);
                
                // 跳过 Metadata 签名
                if (MetadataSignatureSize > 0)
                {
                    _reader.ReadBytes((int)MetadataSignatureSize);
                }
                
                // 记录数据起始位置
                _dataStartOffset = _stream.Position;
                _logDetail($"数据起始偏移: 0x{_dataStartOffset:X}");
                
                IsInitialized = true;
                _log($"Payload 解析成功: {Partitions.Count} 个分区, Block Size: {BlockSize}");
                
                return true;
            }
            catch (Exception ex)
            {
                _log($"Payload 解析失败: {ex.Message}");
                _logDetail($"解析错误: {ex}");
                return false;
            }
        }
        
        private void ParseManifest(byte[] data)
        {
            var partitions = new List<PayloadPartition>();
            int pos = 0;
            
            while (pos < data.Length)
            {
                int tag = (int)ReadVarint(data, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;
                
                switch (fieldNumber)
                {
                    case FIELD_BLOCK_SIZE:
                        BlockSize = (uint)ReadVarint(data, ref pos);
                        break;
                    
                    case FIELD_SIGNATURES_OFFSET:
                        SignaturesOffset = ReadVarint(data, ref pos);
                        break;
                    
                    case FIELD_SIGNATURES_SIZE:
                        SignaturesSize = ReadVarint(data, ref pos);
                        break;
                    
                    case FIELD_PARTITIONS:
                        if (wireType == WIRE_TYPE_LENGTH_DELIMITED)
                        {
                            int length = (int)ReadVarint(data, ref pos);
                            byte[] partitionData = new byte[length];
                            Array.Copy(data, pos, partitionData, 0, length);
                            pos += length;
                            
                            var partition = ParsePartitionUpdate(partitionData);
                            if (partition != null)
                            {
                                partitions.Add(partition);
                            }
                        }
                        break;
                    
                    default:
                        SkipField(data, ref pos, wireType);
                        break;
                }
            }
            
            Partitions = partitions;
        }
        
        private PayloadPartition ParsePartitionUpdate(byte[] data)
        {
            var partition = new PayloadPartition();
            var operations = new List<PayloadOperation>();
            int pos = 0;
            
            while (pos < data.Length)
            {
                int tag = (int)ReadVarint(data, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;
                
                switch (fieldNumber)
                {
                    case FIELD_PARTITION_NAME:
                        if (wireType == WIRE_TYPE_LENGTH_DELIMITED)
                        {
                            int length = (int)ReadVarint(data, ref pos);
                            partition.Name = Encoding.UTF8.GetString(data, pos, length);
                            pos += length;
                        }
                        break;
                    
                    case FIELD_NEW_PARTITION_INFO:
                        if (wireType == WIRE_TYPE_LENGTH_DELIMITED)
                        {
                            int length = (int)ReadVarint(data, ref pos);
                            byte[] infoData = new byte[length];
                            Array.Copy(data, pos, infoData, 0, length);
                            pos += length;
                            ParsePartitionInfo(infoData, partition);
                        }
                        break;
                    
                    case FIELD_OPERATIONS:
                        if (wireType == WIRE_TYPE_LENGTH_DELIMITED)
                        {
                            int length = (int)ReadVarint(data, ref pos);
                            byte[] opData = new byte[length];
                            Array.Copy(data, pos, opData, 0, length);
                            pos += length;
                            
                            var op = ParseOperation(opData);
                            if (op != null)
                            {
                                operations.Add(op);
                            }
                        }
                        break;
                    
                    default:
                        SkipField(data, ref pos, wireType);
                        break;
                }
            }
            
            partition.Operations = operations;
            partition.CompressedSize = (ulong)operations.Sum(op => (long)op.DataLength);
            
            return string.IsNullOrEmpty(partition.Name) ? null : partition;
        }
        
        private void ParsePartitionInfo(byte[] data, PayloadPartition partition)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                int tag = (int)ReadVarint(data, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;
                
                switch (fieldNumber)
                {
                    case FIELD_INFO_SIZE:
                        partition.Size = ReadVarint(data, ref pos);
                        break;
                    
                    case FIELD_INFO_HASH:
                        if (wireType == WIRE_TYPE_LENGTH_DELIMITED)
                        {
                            int length = (int)ReadVarint(data, ref pos);
                            byte[] hash = new byte[length];
                            Array.Copy(data, pos, hash, 0, length);
                            partition.Hash = Convert.ToBase64String(hash);
                            pos += length;
                        }
                        break;
                    
                    default:
                        SkipField(data, ref pos, wireType);
                        break;
                }
            }
        }
        
        private PayloadOperation ParseOperation(byte[] data)
        {
            var op = new PayloadOperation();
            int pos = 0;
            
            while (pos < data.Length)
            {
                int tag = (int)ReadVarint(data, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;
                
                switch (fieldNumber)
                {
                    case FIELD_OP_TYPE:
                        op.Type = (int)ReadVarint(data, ref pos);
                        break;
                    
                    case FIELD_OP_DATA_OFFSET:
                        op.DataOffset = ReadVarint(data, ref pos);
                        break;
                    
                    case FIELD_OP_DATA_LENGTH:
                        op.DataLength = ReadVarint(data, ref pos);
                        break;
                    
                    case FIELD_OP_DST_EXTENTS:
                        if (wireType == WIRE_TYPE_LENGTH_DELIMITED)
                        {
                            int length = (int)ReadVarint(data, ref pos);
                            byte[] extentData = new byte[length];
                            Array.Copy(data, pos, extentData, 0, length);
                            pos += length;
                            ParseExtent(extentData, op);
                        }
                        break;
                    
                    default:
                        SkipField(data, ref pos, wireType);
                        break;
                }
            }
            
            return op;
        }
        
        private void ParseExtent(byte[] data, PayloadOperation op)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                int tag = (int)ReadVarint(data, ref pos);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;
                
                switch (fieldNumber)
                {
                    case FIELD_EXTENT_START_BLOCK:
                        op.DstStartBlock = ReadVarint(data, ref pos);
                        break;
                    
                    case FIELD_EXTENT_NUM_BLOCKS:
                        op.DstNumBlocks = ReadVarint(data, ref pos);
                        break;
                    
                    default:
                        SkipField(data, ref pos, wireType);
                        break;
                }
            }
        }
        
        #endregion
        
        #region Private Methods - Extraction
        
        private async Task<bool> ExtractPartitionInternalAsync(PayloadPartition partition, string outputPath,
            IProgress<PayloadExtractProgress> progress, CancellationToken ct)
        {
            try
            {
                long totalOps = partition.Operations.Count;
                long processedOps = 0;
                
                using (var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    foreach (var operation in partition.Operations)
                    {
                        ct.ThrowIfCancellationRequested();
                        
                        // 读取操作数据
                        _stream.Seek(_dataStartOffset + (long)operation.DataOffset, SeekOrigin.Begin);
                        byte[] rawData = _reader.ReadBytes((int)operation.DataLength);
                        
                        // 计算目标位置
                        long dstStart = (long)operation.DstStartBlock * BlockSize;
                        long dstLength = (long)operation.DstNumBlocks * BlockSize;
                        
                        outStream.Seek(dstStart, SeekOrigin.Begin);
                        
                        // 根据操作类型处理数据
                        byte[] outputData = await DecompressDataAsync(operation.Type, rawData, dstLength, ct);
                        
                        if (outputData != null)
                        {
                            await outStream.WriteAsync(outputData, 0, outputData.Length, ct);
                        }
                        
                        processedOps++;
                        progress?.Report(new PayloadExtractProgress
                        {
                            PartitionName = partition.Name,
                            CurrentBytes = processedOps,
                            TotalBytes = totalOps,
                            Percent = (double)processedOps * 100 / totalOps
                        });
                    }
                }
                
                _log($"分区 {partition.Name} 提取完成");
                return true;
            }
            catch (Exception ex)
            {
                _log($"提取分区 {partition.Name} 失败: {ex.Message}");
                _logDetail($"提取错误: {ex}");
                return false;
            }
        }
        
        private async Task<byte[]> DecompressDataAsync(int opType, byte[] data, long expectedLength, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                switch (opType)
                {
                    case OP_REPLACE:
                        return data;
                    
                    case OP_REPLACE_BZ:
                        return DecompressBzip2(data);
                    
                    case OP_REPLACE_XZ:
                        return DecompressXz(data);
                    
                    case OP_ZERO:
                        return new byte[expectedLength];
                    
                    default:
                        _logDetail($"未知操作类型: {opType}, 返回原始数据");
                        return data;
                }
            }, ct);
        }
        
        private byte[] DecompressBzip2(byte[] compressedData)
        {
            // 简化实现：BZip2 解压需要额外库
            // 这里返回原始数据，实际使用时需要添加 BZip2 支持
            _logDetail("BZip2 解压暂未实现，返回原始数据");
            return compressedData;
        }
        
        private byte[] DecompressXz(byte[] compressedData)
        {
            // 简化实现：XZ 解压需要额外库
            // 这里返回原始数据，实际使用时需要添加 XZ 支持
            _logDetail("XZ 解压暂未实现，返回原始数据");
            return compressedData;
        }
        
        #endregion
        
        #region Private Methods - Helpers
        
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
        
        private void SkipField(byte[] data, ref int pos, int wireType)
        {
            switch (wireType)
            {
                case WIRE_TYPE_VARINT:
                    ReadVarint(data, ref pos);
                    break;
                case WIRE_TYPE_FIXED64:
                    pos += 8;
                    break;
                case WIRE_TYPE_LENGTH_DELIMITED:
                    int length = (int)ReadVarint(data, ref pos);
                    pos += length;
                    break;
                case WIRE_TYPE_FIXED32:
                    pos += 4;
                    break;
            }
        }
        
        private ulong ReadBigEndianUInt64()
        {
            byte[] bytes = _reader.ReadBytes(8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt64(bytes, 0);
        }
        
        private uint ReadBigEndianUInt32()
        {
            byte[] bytes = _reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }
        
        #endregion
        
        #region IDisposable
        
        public void Dispose()
        {
            if (!_disposed)
            {
                _reader?.Dispose();
                if (_ownsStream)
                {
                    _stream?.Dispose();
                }
                
                // 清理临时文件
                if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath))
                {
                    try { File.Delete(_tempFilePath); } catch { }
                }
                
                _disposed = true;
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// Payload 分区信息
    /// </summary>
    public class PayloadPartition
    {
        public string Name { get; set; }
        public ulong Size { get; set; }
        public ulong CompressedSize { get; set; }
        public string Hash { get; set; }
        public List<PayloadOperation> Operations { get; set; } = new List<PayloadOperation>();
        
        public string SizeFormatted => FormatSize(Size);
        public string CompressedSizeFormatted => FormatSize(CompressedSize);
        public double CompressionRatio => Size > 0 ? (double)CompressedSize / Size : 1;
        
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
    
    /// <summary>
    /// Payload 操作
    /// </summary>
    public class PayloadOperation
    {
        public int Type { get; set; }
        public ulong DataOffset { get; set; }
        public ulong DataLength { get; set; }
        public ulong DstStartBlock { get; set; }
        public ulong DstNumBlocks { get; set; }
    }
    
    /// <summary>
    /// 提取进度
    /// </summary>
    public class PayloadExtractProgress
    {
        public string PartitionName { get; set; }
        public int CurrentPartition { get; set; }
        public int TotalPartitions { get; set; }
        public long CurrentBytes { get; set; }
        public long TotalBytes { get; set; }
        public double Percent { get; set; }
    }
}
