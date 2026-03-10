// ============================================================================
// SakuraEDL - CPIO Parser | CPIO 解析器
// ============================================================================
// [ZH] CPIO 解析器 - 解析 Ramdisk 中的 CPIO 归档格式
// [EN] CPIO Parser - Parse CPIO archive format in Ramdisk
// [JA] CPIO解析器 - RamdiskのCPIOアーカイブ形式を解析
// [KO] CPIO 파서 - Ramdisk의 CPIO 아카이브 형식 분석
// [RU] Парсер CPIO - Разбор формата архива CPIO в Ramdisk
// [ES] Analizador CPIO - Análisis de formato de archivo CPIO en Ramdisk
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SakuraEDL.Spreadtrum.Common
{
    /// <summary>
    /// CPIO 归档解析器 - 用于解析 Android Boot 镜像的 ramdisk
    /// 支持 newc (070701) 和 crc (070702) 格式
    /// </summary>
    public class CpioParser
    {
        // CPIO 魔数
        private const string CPIO_MAGIC_NEWC = "070701";  // New ASCII format
        private const string CPIO_MAGIC_CRC = "070702";   // New ASCII format with CRC
        private const string CPIO_TRAILER = "TRAILER!!!";

        private readonly Action<string> _log;

        public CpioParser(Action<string> log = null)
        {
            _log = log;
        }

        /// <summary>
        /// 检测数据是否为 CPIO 归档
        /// </summary>
        public static bool IsCpioArchive(byte[] data)
        {
            if (data == null || data.Length < 6)
                return false;

            string magic = Encoding.ASCII.GetString(data, 0, 6);
            return magic == CPIO_MAGIC_NEWC || magic == CPIO_MAGIC_CRC;
        }

        /// <summary>
        /// 解析 CPIO 归档
        /// </summary>
        public List<CpioEntry> Parse(byte[] data)
        {
            var entries = new List<CpioEntry>();

            if (data == null || data.Length < 6)
                return entries;

            int offset = 0;

            while (offset < data.Length - 110)  // 最小头部大小
            {
                // 检查魔数
                string magic = Encoding.ASCII.GetString(data, offset, 6);
                if (magic != CPIO_MAGIC_NEWC && magic != CPIO_MAGIC_CRC)
                {
                    _log?.Invoke($"[CPIO] 无效魔数: {magic} at offset {offset}");
                    break;
                }

                // 解析头部 (newc 格式, 110 bytes)
                var entry = ParseHeader(data, offset);
                if (entry == null)
                    break;

                // 检查是否为结束标记
                if (entry.FileName == CPIO_TRAILER)
                {
                    _log?.Invoke("[CPIO] 到达归档结尾");
                    break;
                }

                // 计算数据偏移 (头部 + 文件名 + 填充)
                int headerSize = 110;
                int nameEnd = offset + headerSize + (int)entry.NameSize;
                int dataStart = Align4(nameEnd);

                // 读取文件内容
                if (entry.FileSize > 0 && dataStart + entry.FileSize <= data.Length)
                {
                    entry.Data = new byte[entry.FileSize];
                    Array.Copy(data, dataStart, entry.Data, 0, (int)entry.FileSize);
                }

                entries.Add(entry);

                // 移动到下一个条目 (数据 + 填充)
                offset = Align4(dataStart + (int)entry.FileSize);
            }

            _log?.Invoke($"[CPIO] 解析完成, 共 {entries.Count} 个文件");
            return entries;
        }

        /// <summary>
        /// 解析 CPIO 头部
        /// </summary>
        private CpioEntry ParseHeader(byte[] data, int offset)
        {
            try
            {
                var entry = new CpioEntry();

                // newc 格式头部 (所有字段都是 8 字节 hex ASCII)
                entry.Inode = ParseHex(data, offset + 6, 8);
                entry.Mode = ParseHex(data, offset + 14, 8);
                entry.Uid = ParseHex(data, offset + 22, 8);
                entry.Gid = ParseHex(data, offset + 30, 8);
                entry.Nlink = ParseHex(data, offset + 38, 8);
                entry.Mtime = ParseHex(data, offset + 46, 8);
                entry.FileSize = ParseHex(data, offset + 54, 8);
                entry.DevMajor = ParseHex(data, offset + 62, 8);
                entry.DevMinor = ParseHex(data, offset + 70, 8);
                entry.RdevMajor = ParseHex(data, offset + 78, 8);
                entry.RdevMinor = ParseHex(data, offset + 86, 8);
                entry.NameSize = ParseHex(data, offset + 94, 8);
                entry.Checksum = ParseHex(data, offset + 102, 8);

                // 读取文件名
                int nameOffset = offset + 110;
                if (entry.NameSize > 0 && nameOffset + entry.NameSize <= data.Length)
                {
                    entry.FileName = Encoding.ASCII.GetString(data, nameOffset, (int)entry.NameSize - 1);
                }

                return entry;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[CPIO] 解析头部失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析 hex 字符串
        /// </summary>
        private uint ParseHex(byte[] data, int offset, int length)
        {
            string hex = Encoding.ASCII.GetString(data, offset, length);
            return Convert.ToUInt32(hex, 16);
        }

        /// <summary>
        /// 4 字节对齐
        /// </summary>
        private int Align4(int value)
        {
            return (value + 3) & ~3;
        }

        /// <summary>
        /// 提取文件到目录
        /// </summary>
        public void ExtractToDirectory(List<CpioEntry> entries, string outputDir)
        {
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.FileName) || entry.FileName == ".")
                    continue;

                string fullPath = Path.Combine(outputDir, entry.FileName.TrimStart('/'));

                // 创建目录
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 写入文件
                if (entry.IsRegularFile && entry.Data != null)
                {
                    File.WriteAllBytes(fullPath, entry.Data);
                    _log?.Invoke($"[CPIO] 提取: {entry.FileName}");
                }
            }
        }

        /// <summary>
        /// 查找文件
        /// </summary>
        public CpioEntry FindFile(List<CpioEntry> entries, string fileName)
        {
            return entries.Find(e => 
                e.FileName != null && 
                (e.FileName == fileName || 
                 e.FileName == "/" + fileName ||
                 e.FileName.EndsWith("/" + fileName)));
        }

        /// <summary>
        /// 读取文本文件内容
        /// </summary>
        public string ReadTextFile(List<CpioEntry> entries, string fileName)
        {
            var entry = FindFile(entries, fileName);
            if (entry == null || entry.Data == null)
                return null;

            return Encoding.UTF8.GetString(entry.Data);
        }

        /// <summary>
        /// 获取所有属性文件
        /// </summary>
        public Dictionary<string, string> GetAllProperties(List<CpioEntry> entries)
        {
            var props = new Dictionary<string, string>();

            // 查找所有 .prop 文件
            var propFiles = new[] 
            { 
                "default.prop",
                "prop.default", 
                "system/build.prop",
                "vendor/build.prop",
                "odm/build.prop",
                "product/build.prop"
            };

            foreach (var propFile in propFiles)
            {
                string content = ReadTextFile(entries, propFile);
                if (!string.IsNullOrEmpty(content))
                {
                    ParseProperties(content, props);
                }
            }

            return props;
        }

        /// <summary>
        /// 解析属性文件
        /// </summary>
        private void ParseProperties(string content, Dictionary<string, string> props)
        {
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;

                    int eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        string key = line.Substring(0, eqIndex).Trim();
                        string value = line.Substring(eqIndex + 1).Trim();
                        
                        if (!props.ContainsKey(key))
                            props[key] = value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// CPIO 条目
    /// </summary>
    public class CpioEntry
    {
        public uint Inode { get; set; }
        public uint Mode { get; set; }
        public uint Uid { get; set; }
        public uint Gid { get; set; }
        public uint Nlink { get; set; }
        public uint Mtime { get; set; }
        public uint FileSize { get; set; }
        public uint DevMajor { get; set; }
        public uint DevMinor { get; set; }
        public uint RdevMajor { get; set; }
        public uint RdevMinor { get; set; }
        public uint NameSize { get; set; }
        public uint Checksum { get; set; }
        public string FileName { get; set; }
        public byte[] Data { get; set; }

        // 文件类型判断
        public bool IsDirectory => (Mode & 0xF000) == 0x4000;
        public bool IsRegularFile => (Mode & 0xF000) == 0x8000;
        public bool IsSymlink => (Mode & 0xF000) == 0xA000;

        public override string ToString()
        {
            return $"{FileName} ({FileSize} bytes, mode=0x{Mode:X4})";
        }
    }
}
