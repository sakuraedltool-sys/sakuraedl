// ============================================================================
// SakuraEDL - Boot Image Parser | Boot 镜像解析器
// ============================================================================
// [ZH] Boot 镜像解析 - 解析 Android boot.img 结构
// [EN] Boot Image Parser - Parse Android boot.img structure
// [JA] Bootイメージ解析 - Android boot.img構造の解析
// [KO] Boot 이미지 파서 - Android boot.img 구조 분석
// [RU] Парсер Boot образа - Разбор структуры Android boot.img
// [ES] Analizador de imagen Boot - Análisis de estructura boot.img
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace SakuraEDL.Spreadtrum.Common
{
    /// <summary>
    /// Android Boot 镜像解析器
    /// 支持 Boot.img / Recovery.img 解析
    /// 支持 SPRD-SECUREFLAG 头部
    /// </summary>
    public class BootParser
    {
        // Boot 镜像魔数
        private const string BOOT_MAGIC = "ANDROID!";
        private const string SPRD_SECURE_FLAG = "SPRD-SECUREFLAG";

        // GZip 魔数
        private const byte GZIP_MAGIC_1 = 0x1F;
        private const byte GZIP_MAGIC_2 = 0x8B;

        private readonly Action<string> _log;

        public BootParser(Action<string> log = null)
        {
            _log = log;
        }

        /// <summary>
        /// 解析 Boot 镜像
        /// </summary>
        public BootImageInfo Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Boot 镜像不存在", filePath);

            byte[] data = File.ReadAllBytes(filePath);
            return Parse(data);
        }

        /// <summary>
        /// 解析 Boot 镜像
        /// </summary>
        public BootImageInfo Parse(byte[] data)
        {
            if (data == null || data.Length < 1024)
                throw new ArgumentException("数据太短，不是有效的 Boot 镜像");

            var info = new BootImageInfo();
            int offset = 0;

            // 检查 SPRD Secure Flag
            string header = Encoding.ASCII.GetString(data, 0, Math.Min(15, data.Length));
            if (header.StartsWith(SPRD_SECURE_FLAG))
            {
                _log?.Invoke("[Boot] 检测到 SPRD Secure Flag，跳过安全头部");
                info.HasSprdSecureHeader = true;

                // 查找 ANDROID! 魔数
                offset = FindMagic(data, Encoding.ASCII.GetBytes(BOOT_MAGIC));
                if (offset < 0)
                    throw new InvalidDataException("未找到 ANDROID! 魔数");

                _log?.Invoke($"[Boot] ANDROID! 魔数位于偏移 0x{offset:X}");
            }

            // 验证魔数
            string magic = Encoding.ASCII.GetString(data, offset, 8);
            if (magic != BOOT_MAGIC)
                throw new InvalidDataException($"无效的 Boot 魔数: {magic}");

            // 解析头部
            info.Header = ParseHeader(data, offset);
            _log?.Invoke($"[Boot] 内核大小: {info.Header.KernelSize} bytes");
            _log?.Invoke($"[Boot] Ramdisk 大小: {info.Header.RamdiskSize} bytes");
            _log?.Invoke($"[Boot] 页大小: {info.Header.PageSize}");

            // 计算各部分偏移
            int pageSize = info.Header.PageSize > 0 ? (int)info.Header.PageSize : 4096;
            int headerPages = 1;
            int kernelPages = GetNumberOfPages(info.Header.KernelSize, pageSize);
            int ramdiskPages = GetNumberOfPages(info.Header.RamdiskSize, pageSize);

            info.KernelOffset = offset + pageSize * headerPages;
            info.RamdiskOffset = info.KernelOffset + pageSize * kernelPages;
            info.SecondOffset = info.RamdiskOffset + pageSize * ramdiskPages;

            // 提取内核
            if (info.Header.KernelSize > 0 && info.KernelOffset + info.Header.KernelSize <= data.Length)
            {
                info.Kernel = new byte[info.Header.KernelSize];
                Array.Copy(data, info.KernelOffset, info.Kernel, 0, (int)info.Header.KernelSize);
                _log?.Invoke($"[Boot] 提取内核: {info.Header.KernelSize} bytes");
            }

            // 提取 Ramdisk
            if (info.Header.RamdiskSize > 0 && info.RamdiskOffset + info.Header.RamdiskSize <= data.Length)
            {
                info.Ramdisk = new byte[info.Header.RamdiskSize];
                Array.Copy(data, info.RamdiskOffset, info.Ramdisk, 0, (int)info.Header.RamdiskSize);
                _log?.Invoke($"[Boot] 提取 Ramdisk: {info.Header.RamdiskSize} bytes");

                // 检测 Ramdisk 压缩格式
                info.RamdiskFormat = DetectCompressionFormat(info.Ramdisk);
                _log?.Invoke($"[Boot] Ramdisk 格式: {info.RamdiskFormat}");
            }

            // 提取 Second Stage
            if (info.Header.SecondSize > 0 && info.SecondOffset + info.Header.SecondSize <= data.Length)
            {
                info.Second = new byte[info.Header.SecondSize];
                Array.Copy(data, info.SecondOffset, info.Second, 0, (int)info.Header.SecondSize);
                _log?.Invoke($"[Boot] 提取 Second: {info.Header.SecondSize} bytes");
            }

            return info;
        }

        /// <summary>
        /// 解压并解析 Ramdisk
        /// </summary>
        public List<CpioEntry> ExtractRamdisk(BootImageInfo bootInfo)
        {
            if (bootInfo.Ramdisk == null || bootInfo.Ramdisk.Length == 0)
            {
                _log?.Invoke("[Boot] Ramdisk 为空");
                return new List<CpioEntry>();
            }

            byte[] decompressed = null;

            switch (bootInfo.RamdiskFormat)
            {
                case CompressionFormat.GZip:
                    decompressed = DecompressGZip(bootInfo.Ramdisk);
                    break;

                case CompressionFormat.LZ4:
                case CompressionFormat.LZ4_Legacy:
                    decompressed = Lz4Decompressor.Decompress(bootInfo.Ramdisk);
                    break;

                case CompressionFormat.None:
                case CompressionFormat.CPIO:
                    decompressed = bootInfo.Ramdisk;
                    break;

                default:
                    _log?.Invoke($"[Boot] 不支持的压缩格式: {bootInfo.RamdiskFormat}");
                    return new List<CpioEntry>();
            }

            if (decompressed == null)
            {
                _log?.Invoke("[Boot] Ramdisk 解压失败");
                return new List<CpioEntry>();
            }

            _log?.Invoke($"[Boot] Ramdisk 解压后大小: {decompressed.Length} bytes");

            // 解析 CPIO
            var cpioParser = new CpioParser(_log);
            return cpioParser.Parse(decompressed);
        }

        /// <summary>
        /// 解析头部结构
        /// </summary>
        private BootHeader ParseHeader(byte[] data, int offset)
        {
            var header = new BootHeader();

            using (var ms = new MemoryStream(data, offset, Math.Min(1632, data.Length - offset)))
            using (var reader = new BinaryReader(ms))
            {
                // Magic (8 bytes)
                header.Magic = Encoding.ASCII.GetString(reader.ReadBytes(8));

                // Sizes and addresses (little-endian)
                header.KernelSize = reader.ReadUInt32();
                header.KernelAddr = reader.ReadUInt32();
                header.RamdiskSize = reader.ReadUInt32();
                header.RamdiskAddr = reader.ReadUInt32();
                header.SecondSize = reader.ReadUInt32();
                header.SecondAddr = reader.ReadUInt32();
                header.TagsAddr = reader.ReadUInt32();
                header.PageSize = reader.ReadUInt32();

                // Header version (for boot image v1+)
                header.HeaderVersion = reader.ReadUInt32();

                // OS version
                header.OsVersion = reader.ReadUInt32();

                // Name (16 bytes)
                header.Name = ReadNullTerminatedString(reader.ReadBytes(16));

                // Cmdline (512 bytes)
                header.Cmdline = ReadNullTerminatedString(reader.ReadBytes(512));

                // ID (32 bytes / 8 uint)
                header.Id = new uint[8];
                for (int i = 0; i < 8; i++)
                    header.Id[i] = reader.ReadUInt32();

                // Extra cmdline (1024 bytes)
                header.ExtraCmdline = ReadNullTerminatedString(reader.ReadBytes(1024));
            }

            // 计算基地址
            if (header.KernelAddr > 0x8000)
                header.BaseAddr = header.KernelAddr - 0x8000;

            return header;
        }

        /// <summary>
        /// 检测压缩格式
        /// </summary>
        private CompressionFormat DetectCompressionFormat(byte[] data)
        {
            if (data == null || data.Length < 4)
                return CompressionFormat.Unknown;

            // GZip: 1F 8B 08
            if (data[0] == GZIP_MAGIC_1 && data[1] == GZIP_MAGIC_2)
                return CompressionFormat.GZip;

            // LZ4 Frame: 04 22 4D 18
            if (data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
                return CompressionFormat.LZ4;

            // LZ4 Legacy: 02 21 4C 18
            if (data[0] == 0x02 && data[1] == 0x21 && data[2] == 0x4C && data[3] == 0x18)
                return CompressionFormat.LZ4_Legacy;

            // CPIO: 070701 或 070702
            if (data.Length >= 6)
            {
                string magic = Encoding.ASCII.GetString(data, 0, 6);
                if (magic == "070701" || magic == "070702")
                    return CompressionFormat.CPIO;
            }

            // Bzip2: 42 5A 68 (BZh)
            if (data[0] == 0x42 && data[1] == 0x5A && data[2] == 0x68)
                return CompressionFormat.BZip2;

            // XZ: FD 37 7A 58 5A 00
            if (data[0] == 0xFD && data[1] == 0x37 && data[2] == 0x7A && data[3] == 0x58)
                return CompressionFormat.XZ;

            // LZMA: 5D 00 00
            if (data[0] == 0x5D && data[1] == 0x00 && data[2] == 0x00)
                return CompressionFormat.LZMA;

            return CompressionFormat.Unknown;
        }

        /// <summary>
        /// 解压 GZip 数据
        /// </summary>
        private byte[] DecompressGZip(byte[] data)
        {
            try
            {
                using (var inputStream = new MemoryStream(data))
                using (var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress))
                using (var outputStream = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = gzipStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        outputStream.Write(buffer, 0, bytesRead);
                    }
                    return outputStream.ToArray();
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Boot] GZip 解压失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 查找魔数位置
        /// </summary>
        private int FindMagic(byte[] data, byte[] magic)
        {
            for (int i = 0; i <= data.Length - magic.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < magic.Length; j++)
                {
                    if (data[i + j] != magic[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return i;
            }
            return -1;
        }

        /// <summary>
        /// 计算所需页数
        /// </summary>
        private int GetNumberOfPages(uint size, int pageSize)
        {
            return (int)((size + pageSize - 1) / pageSize);
        }

        /// <summary>
        /// 读取 null 结尾字符串
        /// </summary>
        private string ReadNullTerminatedString(byte[] data)
        {
            int length = Array.IndexOf(data, (byte)0);
            if (length < 0) length = data.Length;
            return Encoding.ASCII.GetString(data, 0, length);
        }
    }

    /// <summary>
    /// Boot 镜像信息
    /// </summary>
    public class BootImageInfo
    {
        public BootHeader Header { get; set; }
        public bool HasSprdSecureHeader { get; set; }
        
        // 各部分偏移
        public int KernelOffset { get; set; }
        public int RamdiskOffset { get; set; }
        public int SecondOffset { get; set; }

        // 各部分数据
        public byte[] Kernel { get; set; }
        public byte[] Ramdisk { get; set; }
        public byte[] Second { get; set; }

        // Ramdisk 压缩格式
        public CompressionFormat RamdiskFormat { get; set; }
    }

    /// <summary>
    /// Boot 头部结构
    /// </summary>
    public class BootHeader
    {
        public string Magic { get; set; }
        public uint KernelSize { get; set; }
        public uint KernelAddr { get; set; }
        public uint RamdiskSize { get; set; }
        public uint RamdiskAddr { get; set; }
        public uint SecondSize { get; set; }
        public uint SecondAddr { get; set; }
        public uint TagsAddr { get; set; }
        public uint PageSize { get; set; }
        public uint HeaderVersion { get; set; }
        public uint OsVersion { get; set; }
        public string Name { get; set; }
        public string Cmdline { get; set; }
        public uint[] Id { get; set; }
        public string ExtraCmdline { get; set; }
        public uint BaseAddr { get; set; }

        /// <summary>
        /// 解析 OS 版本
        /// </summary>
        public string GetAndroidVersion()
        {
            if (OsVersion == 0) return "Unknown";
            
            int major = (int)((OsVersion >> 25) & 0x7F);
            int minor = (int)((OsVersion >> 18) & 0x7F);
            int patch = (int)((OsVersion >> 11) & 0x7F);
            int year = (int)((OsVersion >> 4) & 0x7F) + 2000;
            int month = (int)(OsVersion & 0x0F);

            return $"Android {major}.{minor}.{patch} ({year}-{month:D2})";
        }
    }

    /// <summary>
    /// 压缩格式
    /// </summary>
    public enum CompressionFormat
    {
        Unknown,
        None,
        GZip,
        LZ4,
        LZ4_Legacy,
        BZip2,
        XZ,
        LZMA,
        CPIO
    }
}
