// ============================================================================
// SakuraEDL - SPRD PAK Manager | 展讯资源包管理器
// ============================================================================
// [ZH] 资源包管理 - 整合 Exploit、FDL、配置等资源的打包/加载
// [EN] PAK Manager - Integrate Exploit, FDL, config resources packing/loading
// [JA] PAK管理 - Exploit、FDL、設定リソースのパック/ロード統合
// [KO] PAK 관리자 - Exploit, FDL, 설정 리소스 패킹/로딩 통합
// [RU] Менеджер PAK - Интеграция упаковки/загрузки Exploit, FDL, конфигов
// [ES] Gestor PAK - Integrar empaquetado/carga de Exploit, FDL, config
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SakuraEDL.Spreadtrum.Resources
{
    /// <summary>
    /// 展讯统一资源包管理器
    /// 格式: SPAK v2
    /// 
    /// +------------------+
    /// | Header (32 B)    | 魔数 "SPAK", 版本, 条目数, 标志, 校验和
    /// +------------------+
    /// | Entry Table      | 每条目 128 字节
    /// | (N × 128 B)      |
    /// +------------------+
    /// | Compressed Data  | GZip 压缩的资源数据
    /// +------------------+
    /// </summary>
    public static class SprdPakManager
    {
        #region 常量定义

        private const uint PAK_MAGIC = 0x4B415053;  // "SPAK"
        private const uint PAK_VERSION = 0x0200;     // v2.0
        private const int HEADER_SIZE = 32;
        private const int ENTRY_SIZE = 128;

        #endregion

        #region 资源类型

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
            Script = 6,         // 脚本文件
            Firmware = 7        // 固件文件
        }

        #endregion

        #region 数据结构

        /// <summary>
        /// PAK 文件头
        /// </summary>
        public class PakHeader
        {
            public uint Magic { get; set; }
            public uint Version { get; set; }
            public uint EntryCount { get; set; }
            public uint Flags { get; set; }
            public uint Checksum { get; set; }
            public uint DataOffset { get; set; }
            public byte[] Reserved { get; set; } = new byte[8];

            public byte[] ToBytes()
            {
                var data = new byte[HEADER_SIZE];
                BitConverter.GetBytes(Magic).CopyTo(data, 0);
                BitConverter.GetBytes(Version).CopyTo(data, 4);
                BitConverter.GetBytes(EntryCount).CopyTo(data, 8);
                BitConverter.GetBytes(Flags).CopyTo(data, 12);
                BitConverter.GetBytes(Checksum).CopyTo(data, 16);
                BitConverter.GetBytes(DataOffset).CopyTo(data, 20);
                Array.Copy(Reserved, 0, data, 24, 8);
                return data;
            }

            public static PakHeader FromBytes(byte[] data)
            {
                return new PakHeader
                {
                    Magic = BitConverter.ToUInt32(data, 0),
                    Version = BitConverter.ToUInt32(data, 4),
                    EntryCount = BitConverter.ToUInt32(data, 8),
                    Flags = BitConverter.ToUInt32(data, 12),
                    Checksum = BitConverter.ToUInt32(data, 16),
                    DataOffset = BitConverter.ToUInt32(data, 20),
                    Reserved = data.Skip(24).Take(8).ToArray()
                };
            }
        }

        /// <summary>
        /// PAK 条目
        /// </summary>
        public class PakEntry
        {
            public string Name { get; set; }          // 32: 资源名称
            public string Category { get; set; }      // 16: 分类 (芯片名/设备名)
            public string SubCategory { get; set; }   // 16: 子分类
            public uint DataOffset { get; set; }      // 4: 数据偏移
            public uint CompressedSize { get; set; }  // 4: 压缩后大小
            public uint OriginalSize { get; set; }    // 4: 原始大小
            public uint Checksum { get; set; }        // 4: CRC32
            public ResourceType Type { get; set; }    // 4: 资源类型
            public uint Flags { get; set; }           // 4: 标志
            public uint Address { get; set; }         // 4: 加载地址 (FDL 专用)
            public byte[] Reserved { get; set; } = new byte[36]; // 36: 保留

            public bool IsCompressed => (Flags & 0x01) != 0;
            public string Key => $"{Category}/{SubCategory}/{Name}".ToLower();

            public byte[] ToBytes()
            {
                var data = new byte[ENTRY_SIZE];
                WriteString(data, 0, Name, 32);
                WriteString(data, 32, Category, 16);
                WriteString(data, 48, SubCategory, 16);
                BitConverter.GetBytes(DataOffset).CopyTo(data, 64);
                BitConverter.GetBytes(CompressedSize).CopyTo(data, 68);
                BitConverter.GetBytes(OriginalSize).CopyTo(data, 72);
                BitConverter.GetBytes(Checksum).CopyTo(data, 76);
                BitConverter.GetBytes((uint)Type).CopyTo(data, 80);
                BitConverter.GetBytes(Flags).CopyTo(data, 84);
                BitConverter.GetBytes(Address).CopyTo(data, 88);
                Array.Copy(Reserved, 0, data, 92, 36);
                return data;
            }

            public static PakEntry FromBytes(byte[] data)
            {
                return new PakEntry
                {
                    Name = ReadString(data, 0, 32),
                    Category = ReadString(data, 32, 16),
                    SubCategory = ReadString(data, 48, 16),
                    DataOffset = BitConverter.ToUInt32(data, 64),
                    CompressedSize = BitConverter.ToUInt32(data, 68),
                    OriginalSize = BitConverter.ToUInt32(data, 72),
                    Checksum = BitConverter.ToUInt32(data, 76),
                    Type = (ResourceType)BitConverter.ToUInt32(data, 80),
                    Flags = BitConverter.ToUInt32(data, 84),
                    Address = BitConverter.ToUInt32(data, 88),
                    Reserved = data.Skip(92).Take(36).ToArray()
                };
            }

            private static void WriteString(byte[] data, int offset, string value, int maxLen)
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? "");
                Array.Copy(bytes, 0, data, offset, Math.Min(bytes.Length, maxLen - 1));
            }

            private static string ReadString(byte[] data, int offset, int maxLen)
            {
                int end = offset;
                while (end < offset + maxLen && data[end] != 0) end++;
                return Encoding.UTF8.GetString(data, offset, end - offset);
            }
        }

        #endregion

        #region 状态管理

        private static readonly object _lock = new object();
        private static Dictionary<string, PakEntry> _entries = new Dictionary<string, PakEntry>();
        private static Dictionary<string, byte[]> _cache = new Dictionary<string, byte[]>();
        private static string _loadedPakPath;
        private static readonly Assembly _assembly = Assembly.GetExecutingAssembly();

        /// <summary>
        /// 是否已加载资源包
        /// </summary>
        public static bool IsLoaded => _loadedPakPath != null;

        /// <summary>
        /// 已加载的条目数量
        /// </summary>
        public static int EntryCount => _entries.Count;

        /// <summary>
        /// 默认资源包路径
        /// </summary>
        public static string DefaultPakPath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "SprdResources", "sprd.pak");

        #endregion

        #region 加载资源包

        /// <summary>
        /// 加载资源包
        /// </summary>
        public static bool LoadPak(string pakPath = null)
        {
            pakPath = pakPath ?? DefaultPakPath;
            if (!File.Exists(pakPath))
                return false;

            lock (_lock)
            {
                if (_loadedPakPath == pakPath)
                    return true;

                try
                {
                    using (var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var br = new BinaryReader(fs))
                    {
                        // 读取头部
                        var headerData = br.ReadBytes(HEADER_SIZE);
                        var header = PakHeader.FromBytes(headerData);

                        if (header.Magic != PAK_MAGIC)
                            throw new InvalidDataException("Invalid SPAK magic");

                        // 读取条目
                        _entries.Clear();
                        _cache.Clear();

                        for (int i = 0; i < header.EntryCount; i++)
                        {
                            var entryData = br.ReadBytes(ENTRY_SIZE);
                            var entry = PakEntry.FromBytes(entryData);
                            _entries[entry.Key] = entry;
                        }

                        _loadedPakPath = pakPath;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 卸载资源包
        /// </summary>
        public static void UnloadPak()
        {
            lock (_lock)
            {
                _entries.Clear();
                _cache.Clear();
                _loadedPakPath = null;
            }
        }

        #endregion

        #region 获取资源

        /// <summary>
        /// 获取资源数据
        /// </summary>
        public static byte[] GetResource(string category, string subCategory, string name)
        {
            var key = $"{category}/{subCategory}/{name}".ToLower();
            return GetResourceByKey(key);
        }

        /// <summary>
        /// 获取 FDL 数据
        /// </summary>
        public static byte[] GetFdlData(string chipName, string deviceName, bool isFdl1)
        {
            // 尝试多种命名格式
            string[] names = isFdl1
                ? new[] { "fdl1-sign.bin", "fdl1.bin", "fdl1" }
                : new[] { "fdl2-sign.bin", "fdl2.bin", "fdl2" };

            foreach (var name in names)
            {
                var data = GetResource(chipName, deviceName, name);
                if (data != null)
                    return data;
            }

            // 尝试通用设备
            foreach (var name in names)
            {
                var data = GetResource(chipName, "generic", name);
                if (data != null)
                    return data;
            }

            // 从嵌入资源加载
            return null;
        }

        /// <summary>
        /// 获取 Exploit 数据
        /// </summary>
        public static byte[] GetExploitData(string exploitId)
        {
            // 从资源包
            var data = GetResource("exploit", "payload", $"exploit_{exploitId}.bin");
            if (data != null)
                return data;

            // 从嵌入资源
            return LoadEmbeddedResource($"exploit_{exploitId}.bin");
        }

        /// <summary>
        /// 根据键值获取数据
        /// </summary>
        private static byte[] GetResourceByKey(string key)
        {
            lock (_lock)
            {
                // 检查缓存
                if (_cache.TryGetValue(key, out var cached))
                    return cached;

                // 检查条目
                if (!_entries.TryGetValue(key, out var entry))
                    return null;

                // 读取数据
                if (string.IsNullOrEmpty(_loadedPakPath))
                    return null;

                try
                {
                    using (var fs = new FileStream(_loadedPakPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        fs.Seek(entry.DataOffset, SeekOrigin.Begin);
                        var compressedData = new byte[entry.CompressedSize];
                        fs.Read(compressedData, 0, (int)entry.CompressedSize);

                        byte[] data = entry.IsCompressed
                            ? Decompress(compressedData)
                            : compressedData;

                        // 验证校验和
                        if (CalculateCrc32(data) != entry.Checksum)
                            return null;

                        _cache[key] = data;
                        return data;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 从嵌入资源加载
        /// </summary>
        private static byte[] LoadEmbeddedResource(string resourceName)
        {
            string prefix = "SakuraEDL.Spreadtrum.Resources.";
            try
            {
                using (var stream = _assembly.GetManifestResourceStream(prefix + resourceName))
                {
                    if (stream == null)
                    {
                        // 尝试其他名称
                        foreach (var name in _assembly.GetManifestResourceNames())
                        {
                            if (name.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
                            {
                                using (var s = _assembly.GetManifestResourceStream(name))
                                {
                                    if (s != null)
                                    {
                                        using (var ms = new MemoryStream())
                                        {
                                            s.CopyTo(ms);
                                            return ms.ToArray();
                                        }
                                    }
                                }
                            }
                        }
                        return null;
                    }

                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region 查询方法

        /// <summary>
        /// 获取所有芯片名称
        /// </summary>
        public static string[] GetChipNames()
        {
            lock (_lock)
            {
                return _entries.Values
                    .Where(e => e.Type == ResourceType.Fdl1 || e.Type == ResourceType.Fdl2)
                    .Select(e => e.Category)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取芯片的所有设备
        /// </summary>
        public static string[] GetDeviceNames(string chipName)
        {
            lock (_lock)
            {
                return _entries.Values
                    .Where(e => e.Category.Equals(chipName, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.SubCategory)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取所有 Exploit 名称
        /// </summary>
        public static string[] GetExploitNames()
        {
            lock (_lock)
            {
                var names = _entries.Values
                    .Where(e => e.Type == ResourceType.Exploit)
                    .Select(e => e.Name)
                    .ToList();

                // 添加嵌入的 exploit
                names.AddRange(new[] { "exploit_4ee8.bin", "exploit_65015f08.bin", "exploit_65015f48.bin" });
                return names.Distinct().ToArray();
            }
        }

        /// <summary>
        /// 检查是否有指定资源
        /// </summary>
        public static bool HasResource(string category, string subCategory, string name)
        {
            var key = $"{category}/{subCategory}/{name}".ToLower();
            lock (_lock)
            {
                return _entries.ContainsKey(key);
            }
        }

        /// <summary>
        /// 获取 FDL 条目信息
        /// </summary>
        public static PakEntry GetFdlEntry(string chipName, string deviceName, bool isFdl1)
        {
            string[] names = isFdl1
                ? new[] { "fdl1-sign.bin", "fdl1.bin" }
                : new[] { "fdl2-sign.bin", "fdl2.bin" };

            lock (_lock)
            {
                foreach (var name in names)
                {
                    var key = $"{chipName}/{deviceName}/{name}".ToLower();
                    if (_entries.TryGetValue(key, out var entry))
                        return entry;
                }
            }
            return null;
        }

        #endregion

        #region 构建资源包

        /// <summary>
        /// 从 FDL 目录构建资源包
        /// </summary>
        public static void BuildPak(string fdlSourceDir, string outputPath, bool compress = true)
        {
            var entries = new List<PakEntry>();
            var dataBlocks = new List<byte[]>();
            uint dataOffset = 0;

            // 遍历 FDL 目录
            if (Directory.Exists(fdlSourceDir))
            {
                foreach (var file in Directory.GetFiles(fdlSourceDir, "*.bin", SearchOption.AllDirectories))
                {
                    var relativePath = file.Substring(fdlSourceDir.Length).TrimStart('\\', '/');
                    var parts = relativePath.Split('\\', '/');
                    if (parts.Length < 2) continue;

                    var fileName = Path.GetFileName(file);
                    var chipName = parts[0];
                    var deviceName = parts.Length >= 3 ? parts[parts.Length - 2] : "generic";

                    // 读取文件
                    var originalData = File.ReadAllBytes(file);
                    var checksum = CalculateCrc32(originalData);

                    // 压缩
                    byte[] compressedData = compress ? Compress(originalData) : originalData;

                    // 判断类型
                    var type = fileName.ToLower().Contains("fdl1") ? ResourceType.Fdl1
                             : fileName.ToLower().Contains("fdl2") ? ResourceType.Fdl2
                             : fileName.ToLower().Contains("exploit") ? ResourceType.Exploit
                             : ResourceType.Unknown;

                    entries.Add(new PakEntry
                    {
                        Name = fileName,
                        Category = chipName,
                        SubCategory = deviceName,
                        DataOffset = dataOffset,
                        CompressedSize = (uint)compressedData.Length,
                        OriginalSize = (uint)originalData.Length,
                        Checksum = checksum,
                        Type = type,
                        Flags = compress ? 0x01u : 0u
                    });

                    dataBlocks.Add(compressedData);
                    dataOffset += (uint)compressedData.Length;
                }
            }

            // 计算偏移
            uint entryTableStart = HEADER_SIZE;
            uint dataStart = entryTableStart + (uint)(entries.Count * ENTRY_SIZE);

            // 更新偏移
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].DataOffset += dataStart;
            }

            // 写入文件
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                // 头部
                var header = new PakHeader
                {
                    Magic = PAK_MAGIC,
                    Version = PAK_VERSION,
                    EntryCount = (uint)entries.Count,
                    Flags = compress ? 1u : 0u,
                    DataOffset = dataStart
                };
                bw.Write(header.ToBytes());

                // 条目表
                foreach (var entry in entries)
                {
                    bw.Write(entry.ToBytes());
                }

                // 数据
                foreach (var data in dataBlocks)
                {
                    bw.Write(data);
                }
            }
        }

        /// <summary>
        /// 解包到目录
        /// </summary>
        public static void ExtractPak(string pakPath, string outputDir)
        {
            using (var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                var headerData = br.ReadBytes(HEADER_SIZE);
                var header = PakHeader.FromBytes(headerData);

                if (header.Magic != PAK_MAGIC)
                    throw new InvalidDataException("Invalid SPAK magic");

                var entries = new List<PakEntry>();
                for (int i = 0; i < header.EntryCount; i++)
                {
                    var entryData = br.ReadBytes(ENTRY_SIZE);
                    entries.Add(PakEntry.FromBytes(entryData));
                }

                foreach (var entry in entries)
                {
                    var outputPath = Path.Combine(outputDir, entry.Category, entry.SubCategory, entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

                    fs.Seek(entry.DataOffset, SeekOrigin.Begin);
                    var compressedData = br.ReadBytes((int)entry.CompressedSize);

                    var data = entry.IsCompressed ? Decompress(compressedData) : compressedData;
                    File.WriteAllBytes(outputPath, data);
                }
            }
        }

        #endregion

        #region 临时文件

        private static string _tempDir;

        /// <summary>
        /// 提取 FDL 到临时文件
        /// </summary>
        public static string ExtractFdlToTemp(string chipName, string deviceName, bool isFdl1)
        {
            var data = GetFdlData(chipName, deviceName, isFdl1);
            if (data == null)
                return null;

            if (_tempDir == null)
            {
                _tempDir = Path.Combine(Path.GetTempPath(), "SprdPak_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tempDir);
            }

            var fileName = isFdl1 ? "fdl1.bin" : "fdl2.bin";
            var filePath = Path.Combine(_tempDir, chipName, deviceName, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            File.WriteAllBytes(filePath, data);
            return filePath;
        }

        /// <summary>
        /// 清理临时文件
        /// </summary>
        public static void CleanupTemp()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
                _tempDir = null;
            }
        }

        #endregion

        #region 压缩/解压/CRC

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

        private static byte[] Decompress(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        private static uint[] _crc32Table;

        private static uint CalculateCrc32(byte[] data)
        {
            if (_crc32Table == null)
                InitCrc32Table();

            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
                crc = _crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        private static void InitCrc32Table()
        {
            _crc32Table = new uint[256];
            const uint poly = 0xEDB88320;
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) == 1 ? (crc >> 1) ^ poly : crc >> 1;
                _crc32Table[i] = crc;
            }
        }

        #endregion
    }
}
