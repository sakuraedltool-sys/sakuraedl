// ============================================================================
// SakuraEDL - Sparse Image | Sparse 镜像处理
// ============================================================================
// [ZH] Sparse 镜像处理 - 解析和转换 Android Sparse 格式
// [EN] Sparse Image Handler - Parse and convert Android Sparse format
// [JA] Sparseイメージ処理 - Android Sparse形式の解析と変換
// [KO] Sparse 이미지 처리 - Android Sparse 형식 분석 및 변환
// [RU] Обработка Sparse образов - Разбор и конвертация Android Sparse
// [ES] Manejo de imagen Sparse - Análisis y conversión de formato Sparse
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SakuraEDL.Fastboot.Image
{
    /// <summary>
    /// Android Sparse 镜像解析器
    /// 基于 AOSP libsparse 实现
    /// 
    /// Sparse 镜像格式：
    /// - Header (28 bytes)
    /// - Chunk[] 
    ///   - Chunk Header (12 bytes)
    ///   - Chunk Data (variable)
    /// </summary>
    public class SparseImage : IDisposable
    {
        // Sparse 魔数
        public const uint SPARSE_HEADER_MAGIC = 0xED26FF3A;
        
        // Chunk 类型
        public const ushort CHUNK_TYPE_RAW = 0xCAC1;
        public const ushort CHUNK_TYPE_FILL = 0xCAC2;
        public const ushort CHUNK_TYPE_DONT_CARE = 0xCAC3;
        public const ushort CHUNK_TYPE_CRC32 = 0xCAC4;
        
        private Stream _stream;
        private SparseHeader _header;
        private List<SparseChunk> _chunks;
        private bool _isSparse;
        private bool _disposed;
        
        /// <summary>
        /// 是否是 Sparse 镜像
        /// </summary>
        public bool IsSparse => _isSparse;
        
        /// <summary>
        /// 原始文件大小（解压后）
        /// </summary>
        public long OriginalSize => _isSparse ? (long)_header.TotalBlocks * _header.BlockSize : _stream.Length;
        
        /// <summary>
        /// Sparse 文件大小
        /// </summary>
        public long SparseSize => _stream.Length;
        
        /// <summary>
        /// 块大小
        /// </summary>
        public uint BlockSize => _isSparse ? _header.BlockSize : 4096;
        
        /// <summary>
        /// 总块数
        /// </summary>
        public uint TotalBlocks => _isSparse ? _header.TotalBlocks : (uint)((_stream.Length + BlockSize - 1) / BlockSize);
        
        /// <summary>
        /// Chunk 数量
        /// </summary>
        public int ChunkCount => _chunks?.Count ?? 0;
        
        /// <summary>
        /// Sparse Header
        /// </summary>
        public SparseHeader Header => _header;
        
        /// <summary>
        /// 所有 Chunks
        /// </summary>
        public IReadOnlyList<SparseChunk> Chunks => _chunks;
        
        public SparseImage(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _chunks = new List<SparseChunk>();
            
            ParseHeader();
        }
        
        public SparseImage(string filePath)
            : this(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
        }
        
        private void ParseHeader()
        {
            _stream.Position = 0;
            
            // 读取魔数
            byte[] magicBytes = new byte[4];
            if (_stream.Read(magicBytes, 0, 4) != 4)
            {
                _isSparse = false;
                return;
            }
            
            uint magic = BitConverter.ToUInt32(magicBytes, 0);
            if (magic != SPARSE_HEADER_MAGIC)
            {
                _isSparse = false;
                return;
            }
            
            _isSparse = true;
            _stream.Position = 0;
            
            // 读取完整 header
            byte[] headerBytes = new byte[28];
            _stream.Read(headerBytes, 0, 28);
            
            _header = new SparseHeader
            {
                Magic = BitConverter.ToUInt32(headerBytes, 0),
                MajorVersion = BitConverter.ToUInt16(headerBytes, 4),
                MinorVersion = BitConverter.ToUInt16(headerBytes, 6),
                FileHeaderSize = BitConverter.ToUInt16(headerBytes, 8),
                ChunkHeaderSize = BitConverter.ToUInt16(headerBytes, 10),
                BlockSize = BitConverter.ToUInt32(headerBytes, 12),
                TotalBlocks = BitConverter.ToUInt32(headerBytes, 16),
                TotalChunks = BitConverter.ToUInt32(headerBytes, 20),
                ImageChecksum = BitConverter.ToUInt32(headerBytes, 24)
            };
            
            // 跳过额外的 header 数据
            if (_header.FileHeaderSize > 28)
            {
                _stream.Position = _header.FileHeaderSize;
            }
            
            // 解析所有 chunks
            ParseChunks();
        }
        
        private void ParseChunks()
        {
            _chunks.Clear();
            
            for (uint i = 0; i < _header.TotalChunks; i++)
            {
                byte[] chunkHeader = new byte[12];
                if (_stream.Read(chunkHeader, 0, 12) != 12)
                    break;
                
                var chunk = new SparseChunk
                {
                    Type = BitConverter.ToUInt16(chunkHeader, 0),
                    Reserved = BitConverter.ToUInt16(chunkHeader, 2),
                    ChunkBlocks = BitConverter.ToUInt32(chunkHeader, 4),
                    TotalSize = BitConverter.ToUInt32(chunkHeader, 8),
                    DataOffset = _stream.Position
                };
                
                // 计算数据大小
                uint dataSize = chunk.TotalSize - _header.ChunkHeaderSize;
                chunk.DataSize = dataSize;
                
                // 跳过数据部分
                _stream.Position += dataSize;
                
                _chunks.Add(chunk);
            }
        }
        
        /// <summary>
        /// 将 Sparse 镜像转换为原始数据流
        /// </summary>
        public Stream ToRawStream()
        {
            if (!_isSparse)
            {
                _stream.Position = 0;
                return _stream;
            }
            
            return new SparseToRawStream(this, _stream);
        }
        
        /// <summary>
        /// 分割为多个 Sparse 块用于传输
        /// - Raw 镜像：转换为 Sparse 格式（带偏移量），确保正确写入
        /// - Sparse 镜像：resparse 成多个独立的 Sparse 文件
        /// </summary>
        /// <param name="maxSize">每块最大大小</param>
        public IEnumerable<SparseChunkData> SplitForTransfer(long maxSize)
        {
            if (!_isSparse)
            {
                // 非 Sparse 镜像：转换为 Sparse 格式发送（关键修复）
                // 这样 Bootloader 才能知道每块数据应该写入的偏移量
                foreach (var chunk in RawToSparseSplitTransfer(maxSize))
                {
                    yield return chunk;
                }
            }
            else
            {
                // Sparse 镜像：如果小于 maxSize，直接发送整个文件
                if (_stream.Length <= maxSize)
                {
                    _stream.Position = 0;
                    byte[] data = new byte[_stream.Length];
                    _stream.Read(data, 0, data.Length);
                    
                    yield return new SparseChunkData
                    {
                        Index = 0,
                        TotalChunks = 1,
                        Data = data,
                        Size = data.Length
                    };
                }
                else
                {
                    // Sparse 镜像太大，需要 resparse
                    // 将 chunks 分组，每组生成一个独立的 Sparse 文件
                    foreach (var sparseChunk in ResparseSplitTransfer(maxSize))
                    {
                        yield return sparseChunk;
                    }
                }
            }
        }
        
        /// <summary>
        /// 将 Raw 镜像转换为 Sparse 格式分块发送（关键修复）
        /// 每个分块都是一个完整的 Sparse 文件，包含正确的偏移量信息
        /// </summary>
        private IEnumerable<SparseChunkData> RawToSparseSplitTransfer(long maxSize)
        {
            const int SPARSE_HEADER_SIZE = 28;
            const int CHUNK_HEADER_SIZE = 12;
            uint blockSize = 4096;  // 标准块大小
            
            // 计算每个分片的最大数据量（预留 header 空间）
            long maxDataPerChunk = maxSize - SPARSE_HEADER_SIZE - CHUNK_HEADER_SIZE;
            // 对齐到块大小
            maxDataPerChunk = (maxDataPerChunk / blockSize) * blockSize;
            
            _stream.Position = 0;
            long totalSize = _stream.Length;
            uint totalBlocks = (uint)((totalSize + blockSize - 1) / blockSize);
            int totalChunks = (int)((totalSize + maxDataPerChunk - 1) / maxDataPerChunk);
            
            long remaining = totalSize;
            int chunkIndex = 0;
            uint currentBlockOffset = 0;
            
            while (remaining > 0)
            {
                // 计算本次传输的数据量
                long dataSize = Math.Min(remaining, maxDataPerChunk);
                uint blocksInChunk = (uint)((dataSize + blockSize - 1) / blockSize);
                
                // Sparse 格式要求：RAW chunk 的数据大小必须等于 ChunkBlocks * BlockSize
                // 所以 actualDataSize 必须对齐到块大小，不足部分补零
                long actualDataSize = (long)blocksInChunk * blockSize;
                
                // 分配缓冲区
                int sparseSize = SPARSE_HEADER_SIZE + CHUNK_HEADER_SIZE + (int)actualDataSize;
                byte[] sparseData = new byte[sparseSize];
                int offset = 0;
                
                // 写入 Sparse Header (28 bytes)
                Buffer.BlockCopy(BitConverter.GetBytes(SPARSE_HEADER_MAGIC), 0, sparseData, offset, 4); offset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)1), 0, sparseData, offset, 2); offset += 2;  // major
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)0), 0, sparseData, offset, 2); offset += 2;  // minor
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)SPARSE_HEADER_SIZE), 0, sparseData, offset, 2); offset += 2;
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)CHUNK_HEADER_SIZE), 0, sparseData, offset, 2); offset += 2;
                Buffer.BlockCopy(BitConverter.GetBytes(blockSize), 0, sparseData, offset, 4); offset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes(totalBlocks), 0, sparseData, offset, 4); offset += 4;  // 总块数
                Buffer.BlockCopy(BitConverter.GetBytes(1u), 0, sparseData, offset, 4); offset += 4;  // chunk count = 1
                Buffer.BlockCopy(BitConverter.GetBytes(0u), 0, sparseData, offset, 4); offset += 4;  // checksum
                
                // 如果不是第一块，先写入 DONT_CARE chunk 来跳过前面的块
                if (currentBlockOffset > 0)
                {
                    // 需要重新分配更大的缓冲区以容纳 DONT_CARE chunk
                    int newSize = sparseSize + CHUNK_HEADER_SIZE;
                    byte[] newData = new byte[newSize];
                    Buffer.BlockCopy(sparseData, 0, newData, 0, SPARSE_HEADER_SIZE);
                    sparseData = newData;
                    offset = SPARSE_HEADER_SIZE;
                    
                    // 更新 chunk count
                    Buffer.BlockCopy(BitConverter.GetBytes(2u), 0, sparseData, 20, 4);  // chunk count = 2
                    
                    // 写入 DONT_CARE Chunk Header (跳过前面的块)
                    Buffer.BlockCopy(BitConverter.GetBytes(CHUNK_TYPE_DONT_CARE), 0, sparseData, offset, 2); offset += 2;
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)0), 0, sparseData, offset, 2); offset += 2;  // reserved
                    Buffer.BlockCopy(BitConverter.GetBytes(currentBlockOffset), 0, sparseData, offset, 4); offset += 4;  // blocks to skip
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)CHUNK_HEADER_SIZE), 0, sparseData, offset, 4); offset += 4;  // total size
                    
                    sparseSize = newSize;
                }
                
                // 写入 RAW Chunk Header (12 bytes)
                Buffer.BlockCopy(BitConverter.GetBytes(CHUNK_TYPE_RAW), 0, sparseData, offset, 2); offset += 2;
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)0), 0, sparseData, offset, 2); offset += 2;  // reserved
                Buffer.BlockCopy(BitConverter.GetBytes(blocksInChunk), 0, sparseData, offset, 4); offset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes((uint)(CHUNK_HEADER_SIZE + actualDataSize)), 0, sparseData, offset, 4); offset += 4;
                
                // 读取并写入数据
                int readSize = (int)Math.Min(actualDataSize, remaining);
                _stream.Read(sparseData, offset, readSize);
                
                // 如果读取的数据不够块对齐，填充零
                if (readSize < actualDataSize)
                {
                    Array.Clear(sparseData, offset + readSize, (int)(actualDataSize - readSize));
                }
                
                yield return new SparseChunkData
                {
                    Index = chunkIndex++,
                    TotalChunks = totalChunks,
                    Data = sparseData,
                    Size = sparseSize
                };
                
                remaining -= readSize;
                currentBlockOffset += blocksInChunk;
                sparseData = null;  // 帮助 GC
            }
        }
        
        /// <summary>
        /// Resparse：将大的 Sparse 镜像分割成多个小的 Sparse 镜像
        /// 支持拆分过大的单个 Chunk（关键修复）
        /// </summary>
        private IEnumerable<SparseChunkData> ResparseSplitTransfer(long maxSize)
        {
            int headerSize = _header.FileHeaderSize;
            int chunkHeaderSize = _header.ChunkHeaderSize;
            uint blockSize = _header.BlockSize;
            
            // 计算每个传输包的最大数据量
            // 预留空间：Sparse Header (28) + DONT_CARE chunk header (12) + RAW chunk header (12)
            long maxDataPerPacket = maxSize - headerSize - chunkHeaderSize * 2;
            uint maxBlocksPerPacket = (uint)(maxDataPerPacket / blockSize);
            
            // 展平所有 chunks，处理需要拆分的 RAW chunks
            var flatChunks = new List<FlatChunkInfo>();
            uint cumulativeBlockOffset = 0;
            
            foreach (var chunk in _chunks)
            {
                if (chunk.Type == CHUNK_TYPE_RAW && chunk.DataSize > maxDataPerPacket)
                {
                    // RAW chunk 过大，需要拆分
                    uint remainingBlocks = chunk.ChunkBlocks;
                    long dataOffset = chunk.DataOffset;
                    uint splitBlockOffset = cumulativeBlockOffset;
                    
                    while (remainingBlocks > 0)
                    {
                        uint blocksThisSplit = Math.Min(remainingBlocks, maxBlocksPerPacket);
                        uint dataSizeThisSplit = blocksThisSplit * blockSize;
                        
                        flatChunks.Add(new FlatChunkInfo
                        {
                            Type = CHUNK_TYPE_RAW,
                            ChunkBlocks = blocksThisSplit,
                            DataSize = dataSizeThisSplit,
                            DataOffset = dataOffset,
                            BlockOffset = splitBlockOffset
                        });
                        
                        remainingBlocks -= blocksThisSplit;
                        dataOffset += dataSizeThisSplit;
                        splitBlockOffset += blocksThisSplit;
                    }
                }
                else
                {
                    // 其他类型或大小合适的 chunk，直接添加
                    flatChunks.Add(new FlatChunkInfo
                    {
                        Type = chunk.Type,
                        ChunkBlocks = chunk.ChunkBlocks,
                        DataSize = chunk.DataSize,
                        DataOffset = chunk.DataOffset,
                        BlockOffset = cumulativeBlockOffset,
                        OriginalChunk = chunk
                    });
                }
                
                cumulativeBlockOffset += chunk.ChunkBlocks;
            }
            
            // 分组 chunks
            // 每个组需要：Sparse Header + (可能的 DONT_CARE) + 数据 chunks
            var groups = new List<List<FlatChunkInfo>>();
            var currentGroup = new List<FlatChunkInfo>();
            long currentGroupSize = headerSize;
            
            foreach (var fchunk in flatChunks)
            {
                // 计算此 chunk 需要的空间
                long chunkTotalSize = chunkHeaderSize + (fchunk.Type == CHUNK_TYPE_RAW ? fchunk.DataSize : 
                                      fchunk.Type == CHUNK_TYPE_FILL ? 4 : 0);
                
                // 计算如果开始新组需要多少空间（包括可能的 DONT_CARE 块）
                long newGroupBaseSize = headerSize;
                if (fchunk.BlockOffset > 0)
                {
                    newGroupBaseSize += chunkHeaderSize;  // DONT_CARE header
                }
                
                if (currentGroup.Count > 0 && currentGroupSize + chunkTotalSize > maxSize)
                {
                    groups.Add(currentGroup);
                    currentGroup = new List<FlatChunkInfo>();
                    currentGroupSize = newGroupBaseSize;  // 新组的基础大小（含可能的 DONT_CARE）
                }
                else if (currentGroup.Count == 0 && fchunk.BlockOffset > 0)
                {
                    // 第一个组且需要 DONT_CARE，更新基础大小
                    currentGroupSize = newGroupBaseSize;
                }
                
                currentGroup.Add(fchunk);
                currentGroupSize += chunkTotalSize;
            }
            
            if (currentGroup.Count > 0)
            {
                groups.Add(currentGroup);
            }
            
            // 为每个组生成独立的 Sparse 文件
            int totalGroups = groups.Count;
            
            for (int groupIndex = 0; groupIndex < totalGroups; groupIndex++)
            {
                var group = groups[groupIndex];
                
                // 计算此组的总大小和 DONT_CARE 需求
                long groupDataSize = headerSize;
                uint groupTotalBlocks = 0;
                int chunkCount = 0;
                uint firstBlockOffset = group[0].BlockOffset;
                
                // 如果不是从 0 开始，需要添加 DONT_CARE
                if (firstBlockOffset > 0)
                {
                    groupDataSize += chunkHeaderSize;  // DONT_CARE header
                    chunkCount++;
                }
                
                foreach (var fchunk in group)
                {
                    long chunkSize = chunkHeaderSize;
                    if (fchunk.Type == CHUNK_TYPE_RAW)
                        chunkSize += fchunk.DataSize;
                    else if (fchunk.Type == CHUNK_TYPE_FILL)
                        chunkSize += 4;
                    
                    groupDataSize += chunkSize;
                    groupTotalBlocks += fchunk.ChunkBlocks;
                    chunkCount++;
                }
                
                // 预分配缓冲区
                byte[] sparseData = new byte[groupDataSize];
                int writeOffset = 0;
                
                // 写入 Sparse header
                Buffer.BlockCopy(BitConverter.GetBytes(SPARSE_HEADER_MAGIC), 0, sparseData, writeOffset, 4); writeOffset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes(_header.MajorVersion), 0, sparseData, writeOffset, 2); writeOffset += 2;
                Buffer.BlockCopy(BitConverter.GetBytes(_header.MinorVersion), 0, sparseData, writeOffset, 2); writeOffset += 2;
                Buffer.BlockCopy(BitConverter.GetBytes(_header.FileHeaderSize), 0, sparseData, writeOffset, 2); writeOffset += 2;
                Buffer.BlockCopy(BitConverter.GetBytes(_header.ChunkHeaderSize), 0, sparseData, writeOffset, 2); writeOffset += 2;
                Buffer.BlockCopy(BitConverter.GetBytes(_header.BlockSize), 0, sparseData, writeOffset, 4); writeOffset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes(_header.TotalBlocks), 0, sparseData, writeOffset, 4); writeOffset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes((uint)chunkCount), 0, sparseData, writeOffset, 4); writeOffset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes(0u), 0, sparseData, writeOffset, 4); writeOffset += 4;
                
                // 如果需要，写入 DONT_CARE chunk 跳过前面的块
                if (firstBlockOffset > 0)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(CHUNK_TYPE_DONT_CARE), 0, sparseData, writeOffset, 2); writeOffset += 2;
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)0), 0, sparseData, writeOffset, 2); writeOffset += 2;
                    Buffer.BlockCopy(BitConverter.GetBytes(firstBlockOffset), 0, sparseData, writeOffset, 4); writeOffset += 4;
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)chunkHeaderSize), 0, sparseData, writeOffset, 4); writeOffset += 4;
                }
                
                // 写入每个 chunk
                foreach (var fchunk in group)
                {
                    // Chunk header
                    Buffer.BlockCopy(BitConverter.GetBytes(fchunk.Type), 0, sparseData, writeOffset, 2); writeOffset += 2;
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)0), 0, sparseData, writeOffset, 2); writeOffset += 2;
                    Buffer.BlockCopy(BitConverter.GetBytes(fchunk.ChunkBlocks), 0, sparseData, writeOffset, 4); writeOffset += 4;
                    
                    uint totalSize = (uint)chunkHeaderSize;
                    if (fchunk.Type == CHUNK_TYPE_RAW)
                        totalSize += fchunk.DataSize;
                    else if (fchunk.Type == CHUNK_TYPE_FILL)
                        totalSize += 4;
                    // DONT_CARE: totalSize = chunkHeaderSize (no data)
                    
                    Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, sparseData, writeOffset, 4); writeOffset += 4;
                    
                    // Chunk data
                    if (fchunk.Type == CHUNK_TYPE_RAW)
                    {
                        _stream.Position = fchunk.DataOffset;
                        int bytesRead = _stream.Read(sparseData, writeOffset, (int)fchunk.DataSize);
                        if (bytesRead != (int)fchunk.DataSize)
                            throw new InvalidOperationException($"Sparse read error: expected {fchunk.DataSize}, got {bytesRead}");
                        writeOffset += (int)fchunk.DataSize;
                    }
                    else if (fchunk.Type == CHUNK_TYPE_FILL)
                    {
                        // FILL 块需要读取 4 字节的填充值
                        if (fchunk.OriginalChunk != null)
                        {
                            _stream.Position = fchunk.DataOffset;
                            _stream.Read(sparseData, writeOffset, 4);
                        }
                        else
                        {
                            // 如果没有原始块引用，使用 0 作为填充值
                            Buffer.BlockCopy(BitConverter.GetBytes(0u), 0, sparseData, writeOffset, 4);
                        }
                        writeOffset += 4;
                    }
                    // DONT_CARE: 不需要写入数据
                }
                
                // 验证写入的字节数是否正确
                if (writeOffset != groupDataSize)
                    throw new InvalidOperationException($"Sparse size mismatch: expected {groupDataSize}, wrote {writeOffset}");
                
                yield return new SparseChunkData
                {
                    Index = groupIndex,
                    TotalChunks = totalGroups,
                    Data = sparseData,
                    Size = (int)groupDataSize
                };
                
                sparseData = null;
            }
        }
        
        /// <summary>
        /// 展平后的 Chunk 信息（用于 Resparse）
        /// </summary>
        private class FlatChunkInfo
        {
            public ushort Type;
            public uint ChunkBlocks;
            public uint DataSize;
            public long DataOffset;
            public uint BlockOffset;  // 在原始镜像中的块偏移
            public SparseChunk OriginalChunk;  // 原始 chunk（如果有）
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                _stream?.Dispose();
                _disposed = true;
            }
        }
    }
    
    /// <summary>
    /// Sparse Header
    /// </summary>
    public struct SparseHeader
    {
        public uint Magic;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ushort FileHeaderSize;
        public ushort ChunkHeaderSize;
        public uint BlockSize;
        public uint TotalBlocks;
        public uint TotalChunks;
        public uint ImageChecksum;
    }
    
    /// <summary>
    /// Sparse Chunk
    /// </summary>
    public class SparseChunk
    {
        public ushort Type;
        public ushort Reserved;
        public uint ChunkBlocks;
        public uint TotalSize;
        public uint DataSize;
        public long DataOffset;
        
        public string TypeName
        {
            get
            {
                switch (Type)
                {
                    case SparseImage.CHUNK_TYPE_RAW: return "RAW";
                    case SparseImage.CHUNK_TYPE_FILL: return "FILL";
                    case SparseImage.CHUNK_TYPE_DONT_CARE: return "DONT_CARE";
                    case SparseImage.CHUNK_TYPE_CRC32: return "CRC32";
                    default: return $"UNKNOWN({Type:X4})";
                }
            }
        }
    }
    
    /// <summary>
    /// 用于传输的 Chunk 数据
    /// </summary>
    public class SparseChunkData
    {
        public int Index;
        public int TotalChunks;
        public byte[] Data;
        public int Size;
        public ushort ChunkType;
        public uint ChunkBlocks;
    }
    
    /// <summary>
    /// Sparse 到 Raw 的流转换器
    /// </summary>
    internal class SparseToRawStream : Stream
    {
        private readonly SparseImage _sparse;
        private readonly Stream _source;
        private long _position;
        private readonly long _length;
        
        public SparseToRawStream(SparseImage sparse, Stream source)
        {
            _sparse = sparse;
            _source = source;
            _length = sparse.OriginalSize;
        }
        
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => _position = value;
        }
        
        public override int Read(byte[] buffer, int offset, int count)
        {
            // 简化实现：遍历 chunks 找到对应位置的数据
            int totalRead = 0;
            long currentBlockOffset = 0;
            
            foreach (var chunk in _sparse.Chunks)
            {
                long chunkStartOffset = currentBlockOffset * _sparse.BlockSize;
                long chunkEndOffset = (currentBlockOffset + chunk.ChunkBlocks) * _sparse.BlockSize;
                
                if (_position >= chunkStartOffset && _position < chunkEndOffset)
                {
                    long posInChunk = _position - chunkStartOffset;
                    int toRead = (int)Math.Min(count - totalRead, chunkEndOffset - _position);
                    
                    switch (chunk.Type)
                    {
                        case SparseImage.CHUNK_TYPE_RAW:
                            _source.Position = chunk.DataOffset + posInChunk;
                            int read = _source.Read(buffer, offset + totalRead, toRead);
                            totalRead += read;
                            _position += read;
                            break;
                            
                        case SparseImage.CHUNK_TYPE_FILL:
                            _source.Position = chunk.DataOffset;
                            byte[] fillValue = new byte[4];
                            _source.Read(fillValue, 0, 4);
                            for (int i = 0; i < toRead; i++)
                            {
                                buffer[offset + totalRead + i] = fillValue[i % 4];
                            }
                            totalRead += toRead;
                            _position += toRead;
                            break;
                            
                        case SparseImage.CHUNK_TYPE_DONT_CARE:
                            Array.Clear(buffer, offset + totalRead, toRead);
                            totalRead += toRead;
                            _position += toRead;
                            break;
                    }
                    
                    if (totalRead >= count)
                        break;
                }
                
                currentBlockOffset += chunk.ChunkBlocks;
            }
            
            return totalRead;
        }
        
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: _position = offset; break;
                case SeekOrigin.Current: _position += offset; break;
                case SeekOrigin.End: _position = _length + offset; break;
            }
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
