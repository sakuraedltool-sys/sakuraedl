// ============================================================================
// SakuraEDL - SPRD Resource PAK | 展讯资源包
// ============================================================================
// [ZH] 资源包读取 - 从 sprd_resources.pak 加载 Exploit/FDL 等资源
// [EN] Resource PAK Reader - Load Exploit/FDL resources from pak file
// [JA] リソースPAK読み取り - pakファイルからExploit/FDLリソースをロード
// [KO] 리소스 PAK 리더 - pak 파일에서 Exploit/FDL 리소스 로드
// [RU] Чтение PAK ресурсов - Загрузка Exploit/FDL из файла pak
// [ES] Lector de PAK - Cargar recursos Exploit/FDL desde archivo pak
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SakuraEDL.Spreadtrum.Resources
{
    /// <summary>
    /// 展讯资源包读取器 (SPAK 格式)
    /// 
    /// SPAK v1 格式:
    /// Header: Magic(4 "SPAK") + Version(4) + Count(4)
    /// Entry: Name(64) + Offset(8) + CompSize(4) + OrigSize(4) + Type(4) + Reserved(4)
    /// Data: GZip 压缩的资源数据
    /// </summary>
    public class SprdResourcePak : IDisposable
    {
        private const string MAGIC = "SPAK";
        private const int CURRENT_VERSION = 1;
        private const int ENTRY_NAME_SIZE = 64;
        private const int ENTRY_SIZE = 88; // 64 + 8 + 4 + 4 + 4 + 4

        private readonly string _pakPath;
        private readonly Dictionary<string, PakEntry> _index;
        private FileStream _fileStream;
        private bool _disposed;

        /// <summary>
        /// 资源包版本
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// 资源条目数
        /// </summary>
        public int Count => _index.Count;

        /// <summary>
        /// 资源类型
        /// </summary>
        public enum ResourceType : uint
        {
            Unknown = 0,
            Exploit = 1,        // Exploit payload
            Fdl1 = 2,           // FDL1 文件
            Fdl2 = 3,           // FDL2 文件
            ChipData = 4,       // 芯片数据
            Config = 5,         // 配置文件
            Script = 6          // 脚本文件
        }

        private struct PakEntry
        {
            public string Name;
            public long Offset;
            public int CompressedSize;
            public int OriginalSize;
            public ResourceType Type;
        }

        /// <summary>
        /// 创建资源包读取器
        /// </summary>
        public SprdResourcePak(string pakPath)
        {
            _pakPath = pakPath;
            _index = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
            LoadIndex();
        }

        /// <summary>
        /// 加载资源包索引
        /// </summary>
        private void LoadIndex()
        {
            _fileStream = new FileStream(_pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (var br = new BinaryReader(_fileStream, Encoding.UTF8, true))
            {
                // 读取魔数
                byte[] magic = br.ReadBytes(4);
                if (Encoding.ASCII.GetString(magic) != MAGIC)
                    throw new InvalidDataException("无效的 SPAK 文件");

                // 版本
                Version = (int)br.ReadUInt32();
                if (Version > CURRENT_VERSION)
                    throw new InvalidDataException($"不支持的 SPAK 版本: {Version}");

                // 条目数
                uint count = br.ReadUInt32();

                // 读取索引
                for (int i = 0; i < count; i++)
                {
                    byte[] nameBytes = br.ReadBytes(ENTRY_NAME_SIZE);
                    string name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');

                    var entry = new PakEntry
                    {
                        Name = name,
                        Offset = br.ReadInt64(),
                        CompressedSize = br.ReadInt32(),
                        OriginalSize = br.ReadInt32(),
                        Type = (ResourceType)br.ReadUInt32()
                    };

                    br.ReadInt32(); // Reserved

                    _index[name] = entry;
                }
            }
        }

        /// <summary>
        /// 获取资源数据
        /// </summary>
        public byte[] GetResource(string name)
        {
            if (!_index.TryGetValue(name, out var entry))
                return null;

            return ReadAndDecompress(entry.Offset, entry.CompressedSize, entry.OriginalSize);
        }

        /// <summary>
        /// 获取指定类型的所有资源名
        /// </summary>
        public string[] GetResourcesByType(ResourceType type)
        {
            var names = new List<string>();
            foreach (var kvp in _index)
            {
                if (kvp.Value.Type == type)
                    names.Add(kvp.Key);
            }
            return names.ToArray();
        }

        /// <summary>
        /// 获取所有 Exploit 资源
        /// </summary>
        public string[] GetExploitNames()
        {
            return GetResourcesByType(ResourceType.Exploit);
        }

        /// <summary>
        /// 检查资源是否存在
        /// </summary>
        public bool HasResource(string name)
        {
            return _index.ContainsKey(name);
        }

        /// <summary>
        /// 获取资源类型
        /// </summary>
        public ResourceType GetResourceType(string name)
        {
            if (_index.TryGetValue(name, out var entry))
                return entry.Type;
            return ResourceType.Unknown;
        }

        /// <summary>
        /// 获取所有资源名称
        /// </summary>
        public string[] GetAllResourceNames()
        {
            var names = new string[_index.Count];
            _index.Keys.CopyTo(names, 0);
            return names;
        }

        /// <summary>
        /// 读取并解压数据
        /// </summary>
        private byte[] ReadAndDecompress(long offset, int compSize, int origSize)
        {
            lock (_fileStream)
            {
                _fileStream.Seek(offset, SeekOrigin.Begin);
                byte[] compressed = new byte[compSize];
                _fileStream.Read(compressed, 0, compSize);

                // 如果压缩后和原始大小相同，说明未压缩
                if (compSize == origSize)
                    return compressed;

                // GZip 解压
                using (var input = new MemoryStream(compressed))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gzip.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _fileStream?.Dispose();
                }
                _disposed = true;
            }
        }

        ~SprdResourcePak()
        {
            Dispose(false);
        }

        #region 静态打包方法

        /// <summary>
        /// 创建资源包
        /// </summary>
        /// <param name="outputPath">输出文件路径</param>
        /// <param name="resources">资源列表 (名称, 数据, 类型)</param>
        public static void CreatePak(string outputPath, List<(string Name, byte[] Data, ResourceType Type)> resources)
        {
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                // 写入头部
                bw.Write(Encoding.ASCII.GetBytes(MAGIC));
                bw.Write((uint)CURRENT_VERSION);
                bw.Write((uint)resources.Count);

                // 计算数据起始偏移
                long headerSize = 12; // Magic(4) + Version(4) + Count(4)
                long indexSize = resources.Count * ENTRY_SIZE;
                long dataOffset = headerSize + indexSize;

                // 准备压缩数据和索引
                var compressedData = new List<byte[]>();
                var entries = new List<PakEntry>();

                foreach (var res in resources)
                {
                    byte[] compressed = Compress(res.Data);
                    compressedData.Add(compressed);

                    entries.Add(new PakEntry
                    {
                        Name = res.Name,
                        Offset = dataOffset,
                        CompressedSize = compressed.Length,
                        OriginalSize = res.Data.Length,
                        Type = res.Type
                    });

                    dataOffset += compressed.Length;
                }

                // 写入索引
                foreach (var entry in entries)
                {
                    byte[] nameBytes = new byte[ENTRY_NAME_SIZE];
                    byte[] nameUtf8 = Encoding.UTF8.GetBytes(entry.Name);
                    Array.Copy(nameUtf8, nameBytes, Math.Min(nameUtf8.Length, ENTRY_NAME_SIZE - 1));
                    bw.Write(nameBytes);

                    bw.Write(entry.Offset);
                    bw.Write(entry.CompressedSize);
                    bw.Write(entry.OriginalSize);
                    bw.Write((uint)entry.Type);
                    bw.Write((uint)0); // Reserved
                }

                // 写入数据
                foreach (var data in compressedData)
                {
                    bw.Write(data);
                }
            }
        }

        /// <summary>
        /// GZip 压缩
        /// </summary>
        private static byte[] Compress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                {
                    gzip.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// 从目录创建资源包
        /// </summary>
        /// <param name="sourceDir">源目录</param>
        /// <param name="outputPath">输出文件路径</param>
        public static void CreatePakFromDirectory(string sourceDir, string outputPath)
        {
            var resources = new List<(string Name, byte[] Data, ResourceType Type)>();

            foreach (string file in Directory.GetFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                byte[] data = File.ReadAllBytes(file);
                ResourceType type = InferResourceType(name);

                resources.Add((name, data, type));
            }

            CreatePak(outputPath, resources);
        }

        /// <summary>
        /// 根据文件名推断资源类型
        /// </summary>
        private static ResourceType InferResourceType(string fileName)
        {
            string lower = fileName.ToLower();

            if (lower.StartsWith("exploit_") || lower.Contains("exploit"))
                return ResourceType.Exploit;
            if (lower.StartsWith("fdl1") || lower.Contains("fdl1"))
                return ResourceType.Fdl1;
            if (lower.StartsWith("fdl2") || lower.Contains("fdl2"))
                return ResourceType.Fdl2;
            if (lower.EndsWith(".json") || lower.EndsWith(".xml") || lower.EndsWith(".ini"))
                return ResourceType.Config;
            if (lower.EndsWith(".bat") || lower.EndsWith(".sh") || lower.EndsWith(".ps1"))
                return ResourceType.Script;

            return ResourceType.Unknown;
        }

        #endregion
    }

    /// <summary>
    /// 资源包信息
    /// </summary>
    public class SprdPakResourceInfo
    {
        public string Name { get; set; }
        public int OriginalSize { get; set; }
        public int CompressedSize { get; set; }
        public SprdResourcePak.ResourceType Type { get; set; }
        public double CompressionRatio => CompressedSize > 0 ? (double)OriginalSize / CompressedSize : 1.0;
    }
}
