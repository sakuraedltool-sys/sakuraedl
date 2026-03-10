// ============================================================================
// SakuraEDL - FDL PAK Manager | FDL 资源包管理器
// ============================================================================
// [ZH] FDL 资源包管理 - 管理 FDL 二进制文件的打包和加载
// [EN] FDL PAK Manager - Manage FDL binary file packing and loading
// [JA] FDL PAK管理 - FDLバイナリファイルのパックとロードを管理
// [KO] FDL PAK 관리자 - FDL 바이너리 파일 패킹 및 로딩 관리
// [RU] Менеджер FDL PAK - Управление упаковкой и загрузкой бинарников FDL
// [ES] Gestor FDL PAK - Gestionar empaquetado y carga de binarios FDL
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SakuraEDL.Spreadtrum.Resources
{
    /// <summary>
    /// FDL 资源包管理器 - 打包/解包/加载 FDL 文件
    /// PAK 格式: [Header][Entry Table][Compressed Data]
    /// </summary>
    public class FdlPakManager
    {
        // PAK 文件魔数
        private const uint PAK_MAGIC = 0x4B415046;  // "FPAK" (FDL PAK)
        private const uint PAK_VERSION = 0x0100;    // v1.0

        // 默认资源包路径
        private static readonly string DefaultPakPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "SprdResources", "fdl.pak");

        // 缓存已加载的资源
        private static Dictionary<string, byte[]> _cache = new Dictionary<string, byte[]>();
        private static Dictionary<string, FdlPakEntry> _entries = new Dictionary<string, FdlPakEntry>();
        private static string _loadedPakPath = null;
        private static readonly object _lock = new object();

        #region PAK 格式定义

        /// <summary>
        /// PAK 文件头 (固定 64 字节)
        /// </summary>
        public class FdlPakHeader
        {
            public uint Magic { get; set; }           // 4: 魔数 "FPAK"
            public uint Version { get; set; }         // 4: 版本号
            public uint EntryCount { get; set; }      // 4: 文件条目数
            public uint EntryTableOffset { get; set; }// 4: 条目表偏移
            public uint DataOffset { get; set; }      // 4: 数据区偏移
            public uint TotalSize { get; set; }       // 4: 总大小
            public uint Checksum { get; set; }        // 4: 校验和
            public uint Flags { get; set; }           // 4: 标志位 (压缩等)
            public byte[] Reserved { get; set; }      // 32: 保留

            public const int SIZE = 64;

            public FdlPakHeader()
            {
                Magic = PAK_MAGIC;
                Version = PAK_VERSION;
                Reserved = new byte[32];
            }

            public byte[] ToBytes()
            {
                var data = new byte[SIZE];
                BitConverter.GetBytes(Magic).CopyTo(data, 0);
                BitConverter.GetBytes(Version).CopyTo(data, 4);
                BitConverter.GetBytes(EntryCount).CopyTo(data, 8);
                BitConverter.GetBytes(EntryTableOffset).CopyTo(data, 12);
                BitConverter.GetBytes(DataOffset).CopyTo(data, 16);
                BitConverter.GetBytes(TotalSize).CopyTo(data, 20);
                BitConverter.GetBytes(Checksum).CopyTo(data, 24);
                BitConverter.GetBytes(Flags).CopyTo(data, 28);
                Array.Copy(Reserved, 0, data, 32, 32);
                return data;
            }

            public static FdlPakHeader FromBytes(byte[] data)
            {
                if (data.Length < SIZE)
                    throw new InvalidDataException("Invalid PAK header size");

                return new FdlPakHeader
                {
                    Magic = BitConverter.ToUInt32(data, 0),
                    Version = BitConverter.ToUInt32(data, 4),
                    EntryCount = BitConverter.ToUInt32(data, 8),
                    EntryTableOffset = BitConverter.ToUInt32(data, 12),
                    DataOffset = BitConverter.ToUInt32(data, 16),
                    TotalSize = BitConverter.ToUInt32(data, 20),
                    Checksum = BitConverter.ToUInt32(data, 24),
                    Flags = BitConverter.ToUInt32(data, 28),
                    Reserved = data.Skip(32).Take(32).ToArray()
                };
            }
        }

        /// <summary>
        /// PAK 文件条目 (固定 256 字节)
        /// </summary>
        public class FdlPakEntry
        {
            public string ChipName { get; set; }      // 32: 芯片名称
            public string DeviceName { get; set; }    // 64: 设备名称
            public string FileName { get; set; }      // 64: 文件名
            public uint DataOffset { get; set; }      // 4: 数据偏移
            public uint CompressedSize { get; set; }  // 4: 压缩后大小
            public uint OriginalSize { get; set; }    // 4: 原始大小
            public uint Checksum { get; set; }        // 4: CRC32 校验
            public uint Flags { get; set; }           // 4: 标志 (FDL1=1, FDL2=2, Compressed=0x100)
            public uint Fdl1Address { get; set; }     // 4: FDL1 加载地址
            public uint Fdl2Address { get; set; }     // 4: FDL2 加载地址
            public byte[] Reserved { get; set; }      // 68: 保留

            public const int SIZE = 256;

            // 标志位
            public const uint FLAG_FDL1 = 0x01;
            public const uint FLAG_FDL2 = 0x02;
            public const uint FLAG_COMPRESSED = 0x100;

            public bool IsFdl1 => (Flags & FLAG_FDL1) != 0;
            public bool IsFdl2 => (Flags & FLAG_FDL2) != 0;
            public bool IsCompressed => (Flags & FLAG_COMPRESSED) != 0;

            /// <summary>
            /// 获取唯一键值 (用于索引)
            /// </summary>
            public string Key => $"{ChipName}/{DeviceName}/{FileName}".ToLower();

            public FdlPakEntry()
            {
                Reserved = new byte[68];
            }

            public byte[] ToBytes()
            {
                var data = new byte[SIZE];
                WriteString(data, 0, ChipName, 32);
                WriteString(data, 32, DeviceName, 64);
                WriteString(data, 96, FileName, 64);
                BitConverter.GetBytes(DataOffset).CopyTo(data, 160);
                BitConverter.GetBytes(CompressedSize).CopyTo(data, 164);
                BitConverter.GetBytes(OriginalSize).CopyTo(data, 168);
                BitConverter.GetBytes(Checksum).CopyTo(data, 172);
                BitConverter.GetBytes(Flags).CopyTo(data, 176);
                BitConverter.GetBytes(Fdl1Address).CopyTo(data, 180);
                BitConverter.GetBytes(Fdl2Address).CopyTo(data, 184);
                Array.Copy(Reserved, 0, data, 188, 68);
                return data;
            }

            public static FdlPakEntry FromBytes(byte[] data)
            {
                if (data.Length < SIZE)
                    throw new InvalidDataException("Invalid PAK entry size");

                return new FdlPakEntry
                {
                    ChipName = ReadString(data, 0, 32),
                    DeviceName = ReadString(data, 32, 64),
                    FileName = ReadString(data, 96, 64),
                    DataOffset = BitConverter.ToUInt32(data, 160),
                    CompressedSize = BitConverter.ToUInt32(data, 164),
                    OriginalSize = BitConverter.ToUInt32(data, 168),
                    Checksum = BitConverter.ToUInt32(data, 172),
                    Flags = BitConverter.ToUInt32(data, 176),
                    Fdl1Address = BitConverter.ToUInt32(data, 180),
                    Fdl2Address = BitConverter.ToUInt32(data, 184),
                    Reserved = data.Skip(188).Take(68).ToArray()
                };
            }

            private static void WriteString(byte[] data, int offset, string value, int maxLen)
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? "");
                var len = Math.Min(bytes.Length, maxLen - 1);
                Array.Copy(bytes, 0, data, offset, len);
            }

            private static string ReadString(byte[] data, int offset, int maxLen)
            {
                int end = offset;
                while (end < offset + maxLen && data[end] != 0)
                    end++;
                return Encoding.UTF8.GetString(data, offset, end - offset);
            }
        }

        #endregion

        #region PAK 加载

        /// <summary>
        /// 加载 PAK 资源包
        /// </summary>
        public static bool LoadPak(string pakPath = null)
        {
            pakPath = pakPath ?? DefaultPakPath;

            if (!File.Exists(pakPath))
                return false;

            lock (_lock)
            {
                if (_loadedPakPath == pakPath)
                    return true;  // 已加载

                try
                {
                    using (var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var br = new BinaryReader(fs))
                    {
                        // 读取头部
                        var headerData = br.ReadBytes(FdlPakHeader.SIZE);
                        var header = FdlPakHeader.FromBytes(headerData);

                        if (header.Magic != PAK_MAGIC)
                            throw new InvalidDataException("Invalid PAK magic");

                        // 读取条目表
                        fs.Seek(header.EntryTableOffset, SeekOrigin.Begin);
                        _entries.Clear();
                        _cache.Clear();

                        for (int i = 0; i < header.EntryCount; i++)
                        {
                            var entryData = br.ReadBytes(FdlPakEntry.SIZE);
                            var entry = FdlPakEntry.FromBytes(entryData);
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
        /// 获取 FDL 文件数据
        /// </summary>
        public static byte[] GetFdlData(string chipName, string deviceName, bool isFdl1)
        {
            var fileName = isFdl1 ? "fdl1" : "fdl2";
            return GetFdlData(chipName, deviceName, fileName);
        }

        /// <summary>
        /// 获取 FDL 文件数据
        /// </summary>
        public static byte[] GetFdlData(string chipName, string deviceName, string fileName)
        {
            // 尝试多种文件名格式
            string[] tryNames = {
                fileName,
                fileName + ".bin",
                fileName + "-sign.bin",
                "fdl1-sign.bin",
                "fdl2-sign.bin",
                "fdl1.bin",
                "fdl2.bin"
            };

            foreach (var name in tryNames)
            {
                var key = $"{chipName}/{deviceName}/{name}".ToLower();
                var data = GetDataByKey(key);
                if (data != null)
                    return data;
            }

            // 尝试通用芯片 FDL
            foreach (var name in tryNames)
            {
                var key = $"{chipName}/generic/{name}".ToLower();
                var data = GetDataByKey(key);
                if (data != null)
                    return data;
            }

            return null;
        }

        /// <summary>
        /// 根据键值获取数据
        /// </summary>
        private static byte[] GetDataByKey(string key)
        {
            lock (_lock)
            {
                // 检查缓存
                if (_cache.TryGetValue(key, out var cached))
                    return cached;

                // 检查条目
                if (!_entries.TryGetValue(key, out var entry))
                    return null;

                // 从 PAK 读取
                if (string.IsNullOrEmpty(_loadedPakPath) || !File.Exists(_loadedPakPath))
                    return null;

                try
                {
                    using (var fs = new FileStream(_loadedPakPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        fs.Seek(entry.DataOffset, SeekOrigin.Begin);
                        var compressedData = new byte[entry.CompressedSize];
                        fs.Read(compressedData, 0, (int)entry.CompressedSize);

                        byte[] data;
                        if (entry.IsCompressed)
                        {
                            data = Decompress(compressedData, (int)entry.OriginalSize);
                        }
                        else
                        {
                            data = compressedData;
                        }

                        // 验证校验和
                        if (CalculateCrc32(data) != entry.Checksum)
                            return null;

                        // 缓存
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
        /// 获取所有芯片名称
        /// </summary>
        public static string[] GetChipNames()
        {
            lock (_lock)
            {
                return _entries.Values
                    .Select(e => e.ChipName)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取芯片的所有设备名称
        /// </summary>
        public static string[] GetDeviceNames(string chipName)
        {
            lock (_lock)
            {
                return _entries.Values
                    .Where(e => e.ChipName.Equals(chipName, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.DeviceName)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取 FDL 条目信息
        /// </summary>
        public static FdlPakEntry GetEntry(string chipName, string deviceName, bool isFdl1)
        {
            var fileName = isFdl1 ? "fdl1" : "fdl2";
            string[] tryNames = { fileName, fileName + ".bin", fileName + "-sign.bin" };

            lock (_lock)
            {
                foreach (var name in tryNames)
                {
                    var key = $"{chipName}/{deviceName}/{name}".ToLower();
                    if (_entries.TryGetValue(key, out var entry))
                        return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// 检查 PAK 是否已加载
        /// </summary>
        public static bool IsLoaded => _loadedPakPath != null;

        /// <summary>
        /// 获取加载的条目数量
        /// </summary>
        public static int EntryCount => _entries.Count;

        #endregion

        #region PAK 构建

        /// <summary>
        /// 从目录构建 PAK 资源包
        /// </summary>
        /// <param name="sourceDir">源目录 (包含 FDL 文件)</param>
        /// <param name="outputPath">输出 PAK 路径</param>
        /// <param name="compress">是否压缩</param>
        public static void BuildPak(string sourceDir, string outputPath, bool compress = true)
        {
            var entries = new List<FdlPakEntry>();
            var dataBlocks = new List<byte[]>();
            uint dataOffset = 0;

            // 遍历目录收集 FDL 文件
            foreach (var file in Directory.GetFiles(sourceDir, "*.bin", SearchOption.AllDirectories))
            {
                var relativePath = file.Substring(sourceDir.Length).TrimStart('\\', '/');
                var parts = relativePath.Split('\\', '/');

                if (parts.Length < 2)
                    continue;

                var fileName = Path.GetFileName(file);
                var deviceName = parts.Length >= 3 ? parts[parts.Length - 2] : "generic";
                var chipName = parts[0];

                // 读取文件
                var originalData = File.ReadAllBytes(file);
                var checksum = CalculateCrc32(originalData);

                // 压缩
                byte[] compressedData;
                if (compress)
                {
                    compressedData = Compress(originalData);
                }
                else
                {
                    compressedData = originalData;
                }

                // 判断 FDL 类型
                uint flags = compress ? FdlPakEntry.FLAG_COMPRESSED : 0;
                if (fileName.ToLower().Contains("fdl1"))
                    flags |= FdlPakEntry.FLAG_FDL1;
                else if (fileName.ToLower().Contains("fdl2"))
                    flags |= FdlPakEntry.FLAG_FDL2;

                var entry = new FdlPakEntry
                {
                    ChipName = chipName,
                    DeviceName = deviceName,
                    FileName = fileName,
                    DataOffset = dataOffset,
                    CompressedSize = (uint)compressedData.Length,
                    OriginalSize = (uint)originalData.Length,
                    Checksum = checksum,
                    Flags = flags
                };

                entries.Add(entry);
                dataBlocks.Add(compressedData);
                dataOffset += (uint)compressedData.Length;
            }

            // 计算偏移
            uint entryTableOffset = (uint)FdlPakHeader.SIZE;
            uint dataStartOffset = entryTableOffset + (uint)(entries.Count * FdlPakEntry.SIZE);

            // 更新条目的数据偏移
            uint currentOffset = dataStartOffset;
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].DataOffset = currentOffset;
                currentOffset += entries[i].CompressedSize;
            }

            // 写入文件
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                // 写入头部
                var header = new FdlPakHeader
                {
                    EntryCount = (uint)entries.Count,
                    EntryTableOffset = entryTableOffset,
                    DataOffset = dataStartOffset,
                    TotalSize = currentOffset,
                    Flags = compress ? 1u : 0u
                };
                bw.Write(header.ToBytes());

                // 写入条目表
                foreach (var entry in entries)
                {
                    bw.Write(entry.ToBytes());
                }

                // 写入数据
                foreach (var data in dataBlocks)
                {
                    bw.Write(data);
                }
            }
        }

        /// <summary>
        /// 解包 PAK 到目录
        /// </summary>
        public static void ExtractPak(string pakPath, string outputDir)
        {
            using (var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                // 读取头部
                var headerData = br.ReadBytes(FdlPakHeader.SIZE);
                var header = FdlPakHeader.FromBytes(headerData);

                if (header.Magic != PAK_MAGIC)
                    throw new InvalidDataException("Invalid PAK magic");

                // 读取条目
                fs.Seek(header.EntryTableOffset, SeekOrigin.Begin);
                var entries = new List<FdlPakEntry>();

                for (int i = 0; i < header.EntryCount; i++)
                {
                    var entryData = br.ReadBytes(FdlPakEntry.SIZE);
                    entries.Add(FdlPakEntry.FromBytes(entryData));
                }

                // 解包每个文件
                foreach (var entry in entries)
                {
                    var outputPath = Path.Combine(outputDir, entry.ChipName, entry.DeviceName, entry.FileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

                    fs.Seek(entry.DataOffset, SeekOrigin.Begin);
                    var compressedData = br.ReadBytes((int)entry.CompressedSize);

                    byte[] data;
                    if (entry.IsCompressed)
                    {
                        data = Decompress(compressedData, (int)entry.OriginalSize);
                    }
                    else
                    {
                        data = compressedData;
                    }

                    File.WriteAllBytes(outputPath, data);
                }
            }
        }

        #endregion

        #region 压缩/解压

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

        private static byte[] Decompress(byte[] data, int originalSize)
        {
            using (var input = new MemoryStream(data))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        #endregion

        #region CRC32

        private static uint[] _crc32Table;

        private static uint CalculateCrc32(byte[] data)
        {
            if (_crc32Table == null)
                InitCrc32Table();

            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc = _crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFF;
        }

        private static void InitCrc32Table()
        {
            _crc32Table = new uint[256];
            const uint polynomial = 0xEDB88320;

            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) == 1)
                        crc = (crc >> 1) ^ polynomial;
                    else
                        crc >>= 1;
                }
                _crc32Table[i] = crc;
            }
        }

        #endregion

        #region 临时文件提取

        private static string _tempDir;

        /// <summary>
        /// 将 FDL 提取到临时文件并返回路径
        /// </summary>
        public static string ExtractToTempFile(string chipName, string deviceName, bool isFdl1)
        {
            var data = GetFdlData(chipName, deviceName, isFdl1);
            if (data == null)
                return null;

            if (_tempDir == null)
            {
                _tempDir = Path.Combine(Path.GetTempPath(), "SprdFdl_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
        public static void CleanupTempFiles()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, true);
                }
                catch { }
                _tempDir = null;
            }
        }

        #endregion
    }
}
