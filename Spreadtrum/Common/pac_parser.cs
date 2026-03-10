// ============================================================================
// SakuraEDL - PAC Parser | PAC 固件解析器
// ============================================================================
// [ZH] PAC 固件解析器 - 解析展讯/紫光展锐固件包
// [EN] PAC Firmware Parser - Parse Spreadtrum/Unisoc firmware packages
// [JA] PACファームウェア解析 - Spreadtrum/Unisocパッケージ解析
// [KO] PAC 펌웨어 파서 - Spreadtrum/Unisoc 펌웨어 패키지 분석
// [RU] Парсер прошивки PAC - Разбор пакетов Spreadtrum/Unisoc
// [ES] Analizador de firmware PAC - Análisis de paquetes Spreadtrum/Unisoc
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SakuraEDL.Spreadtrum.Common
{
    /// <summary>
    /// PAC 固件包解析器
    /// 支持 BP_R1.0.0 和 BP_R2.0.1 格式
    /// </summary>
    public class PacParser
    {
        private readonly Action<string> _log;

        // PAC 版本
        public const string VERSION_BP_R1 = "BP_R1.0.0";
        public const string VERSION_BP_R2 = "BP_R2.0.1";

        public PacParser(Action<string> log = null)
        {
            _log = log;
        }

        /// <summary>
        /// 解析 PAC 文件
        /// </summary>
        public PacInfo Parse(string pacFilePath)
        {
            if (!File.Exists(pacFilePath))
                throw new FileNotFoundException("PAC 文件不存在", pacFilePath);

            using (var fs = new FileStream(pacFilePath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                // 解析 PAC 头
                var header = ParseHeader(reader);
                
                // 验证文件大小
                var fileInfo = new FileInfo(pacFilePath);
                ulong expectedSize = ((ulong)header.HiSize << 32) + header.LoSize;
                if (expectedSize != (ulong)fileInfo.Length)
                {
                    Log("[PAC] 警告: 文件大小不匹配 (期望: {0}, 实际: {1})", expectedSize, fileInfo.Length);
                }

                // 解析文件条目
                reader.BaseStream.Seek(header.PartitionsListStart, SeekOrigin.Begin);
                var files = new List<PacFileEntry>();

                for (int i = 0; i < header.PartitionCount; i++)
                {
                    var entry = ParseFileEntry(reader, header.Version);
                    if (entry != null)
                    {
                        files.Add(entry);
                        Log("[PAC] 文件: {0}, 大小: {1}, 偏移: 0x{2:X}",
                            entry.FileName, FormatSize(entry.Size), entry.DataOffset);
                    }
                }

                return new PacInfo
                {
                    FilePath = pacFilePath,
                    Header = header,
                    Files = files
                };
            }
        }

        /// <summary>
        /// 解析 PAC 头
        /// </summary>
        private PacHeader ParseHeader(BinaryReader reader)
        {
            var header = new PacHeader();

            // 版本 (44 bytes, Unicode)
            header.Version = ReadUnicodeString(reader, 44);
            Log("[PAC] 版本: {0}", header.Version);

            if (header.Version != VERSION_BP_R1 && header.Version != VERSION_BP_R2)
            {
                throw new NotSupportedException("不支持的 PAC 版本: " + header.Version);
            }

            // 文件大小
            header.HiSize = reader.ReadUInt32();
            header.LoSize = reader.ReadUInt32();

            // 产品名 (512 bytes, Unicode)
            header.ProductName = ReadUnicodeString(reader, 512);
            Log("[PAC] 产品名: {0}", header.ProductName);

            // 固件名 (512 bytes, Unicode)
            header.FirmwareName = ReadUnicodeString(reader, 512);
            Log("[PAC] 固件名: {0}", header.FirmwareName);

            // 分区数量和偏移
            header.PartitionCount = reader.ReadUInt32();
            header.PartitionsListStart = reader.ReadUInt32();
            Log("[PAC] 分区数量: {0}, 列表偏移: 0x{1:X}", header.PartitionCount, header.PartitionsListStart);

            // 其他字段
            header.Mode = reader.ReadUInt32();
            header.FlashType = reader.ReadUInt32();
            header.NandStrategy = reader.ReadUInt32();
            header.IsNvBackup = reader.ReadUInt32();
            header.NandPageType = reader.ReadUInt32();

            // 产品别名 (996 bytes, Unicode)
            header.ProductAlias = ReadUnicodeString(reader, 996);

            header.OmaDmProductFlag = reader.ReadUInt32();
            header.IsOmaDM = reader.ReadUInt32();
            header.IsPreload = reader.ReadUInt32();
            header.Reserved = reader.ReadUInt32();
            header.Magic = reader.ReadUInt32();
            header.Crc1 = reader.ReadUInt32();
            header.Crc2 = reader.ReadUInt32();

            return header;
        }

        /// <summary>
        /// 解析文件条目
        /// </summary>
        private PacFileEntry ParseFileEntry(BinaryReader reader, string version)
        {
            var entry = new PacFileEntry();

            // 条目长度
            entry.HeaderLength = reader.ReadUInt32();

            // 分区名 (512 bytes, Unicode)
            entry.PartitionName = ReadUnicodeString(reader, 512);

            // 文件名 (512 bytes, Unicode)
            entry.FileName = ReadUnicodeString(reader, 512);

            // 原始文件名 (508 bytes, Unicode)
            entry.OriginalFileName = ReadUnicodeString(reader, 508);

            if (version == VERSION_BP_R1)
            {
                // BP_R1 格式
                entry.HiDataOffset = reader.ReadUInt32();
                entry.HiSize = reader.ReadUInt32();
                reader.ReadUInt32(); // reserved1
                reader.ReadUInt32(); // reserved2
                entry.LoDataOffset = reader.ReadUInt32();
                entry.LoSize = reader.ReadUInt32();
                entry.FileFlag = reader.ReadUInt16();
                entry.CheckFlag = reader.ReadUInt16();
                reader.ReadUInt32(); // reserved3
                entry.CanOmitFlag = reader.ReadUInt32();
                entry.AddrNum = reader.ReadUInt32();
                entry.Address = reader.ReadUInt32();
                reader.ReadUInt32(); // reserved4
                reader.ReadBytes(996); // reserved data
            }
            else if (version == VERSION_BP_R2)
            {
                // BP_R2 格式 - 使用 szPartitionInfo
                byte[] partitionInfo = reader.ReadBytes(24);
                
                // 解析分区信息 (Little-endian reversed)
                entry.HiSize = ParseReversedUInt32(partitionInfo, 0);
                entry.LoSize = ParseReversedUInt32(partitionInfo, 4);
                // bytes 8-15 包含额外信息
                entry.HiDataOffset = ParseReversedUInt32(partitionInfo, 16);
                entry.LoDataOffset = ParseReversedUInt32(partitionInfo, 20);

                reader.ReadUInt32(); // reserved2
                entry.FileFlag = reader.ReadUInt16();
                entry.CheckFlag = reader.ReadUInt16();
                reader.ReadUInt32(); // reserved3
                entry.CanOmitFlag = reader.ReadUInt32();
                entry.AddrNum = reader.ReadUInt32();
                entry.Address = reader.ReadUInt32();
                reader.ReadBytes(996); // reserved data
            }

            // 计算实际偏移和大小
            entry.DataOffset = CombineHiLo(entry.HiDataOffset, entry.LoDataOffset);
            entry.Size = CombineHiLo(entry.HiSize, entry.LoSize);

            // 判断类型
            entry.Type = DetermineFileType(entry);

            return entry;
        }

        /// <summary>
        /// 提取文件
        /// </summary>
        public void ExtractFile(string pacFilePath, PacFileEntry entry, string outputPath, Action<long, long> progress = null)
        {
            using (var fs = new FileStream(pacFilePath, FileMode.Open, FileAccess.Read))
            {
                fs.Seek((long)entry.DataOffset, SeekOrigin.Begin);

                using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[65536];
                    long remaining = (long)entry.Size;
                    long written = 0;

                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = fs.Read(buffer, 0, toRead);
                        
                        if (read == 0)
                            break;

                        // 检查是否为 Sparse Image
                        if (written == 0 && IsSparseImage(buffer))
                        {
                            entry.IsSparse = true;
                        }

                        output.Write(buffer, 0, read);
                        remaining -= read;
                        written += read;

                        progress?.Invoke(written, (long)entry.Size);
                    }
                }
            }

            Log("[PAC] 提取完成: {0}", outputPath);
        }

        /// <summary>
        /// 提取所有文件
        /// </summary>
        public void ExtractAll(PacInfo pac, string outputDir, Action<int, int, string> progress = null)
        {
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            for (int i = 0; i < pac.Files.Count; i++)
            {
                var entry = pac.Files[i];
                
                if (string.IsNullOrEmpty(entry.FileName) || entry.Size == 0)
                    continue;

                string outputPath = Path.Combine(outputDir, entry.FileName);
                progress?.Invoke(i + 1, pac.Files.Count, entry.FileName);

                ExtractFile(pac.FilePath, entry, outputPath);
            }
        }

        /// <summary>
        /// 获取 FDL1 条目
        /// </summary>
        public PacFileEntry GetFdl1(PacInfo pac)
        {
            return pac.Files.Find(f => 
                f.PartitionName.Equals("FDL", StringComparison.OrdinalIgnoreCase) ||
                f.Type == PacFileType.FDL1);
        }

        /// <summary>
        /// 获取 FDL2 条目
        /// </summary>
        public PacFileEntry GetFdl2(PacInfo pac)
        {
            return pac.Files.Find(f => 
                f.PartitionName.Equals("FDL2", StringComparison.OrdinalIgnoreCase) ||
                f.Type == PacFileType.FDL2);
        }

        /// <summary>
        /// 解析并集成 XML 配置
        /// </summary>
        public void ParseXmlConfigs(PacInfo pac)
        {
            if (pac == null || pac.Files == null)
                return;

            var xmlParser = new XmlConfigParser(msg => _log?.Invoke(msg));

            // 查找所有 XML 文件
            var xmlFiles = pac.Files.Where(f => 
                f.Type == PacFileType.XML || 
                f.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var xmlFile in xmlFiles)
            {
                try
                {
                    // 读取 XML 数据
                    byte[] xmlData = ExtractFileData(pac.FilePath, xmlFile);
                    if (xmlData != null && xmlData.Length > 0)
                    {
                        var config = xmlParser.Parse(xmlData);
                        if (config != null)
                        {
                            config.ProductionSettings = config.ProductionSettings ?? new Dictionary<string, string>();
                            config.ProductionSettings["SourceFile"] = xmlFile.FileName;
                            
                            pac.AllXmlConfigs.Add(config);
                            Log("[PAC] 解析 XML 配置: {0} ({1})", xmlFile.FileName, config.ConfigType);

                            // 设置主配置 (优先 BmaConfig)
                            if (pac.XmlConfig == null || config.ConfigType == SprdXmlConfigType.BmaConfig)
                            {
                                pac.XmlConfig = config;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("[PAC] XML 解析失败 ({0}): {1}", xmlFile.FileName, ex.Message);
                }
            }

            // 如果有 XML 配置，更新文件属性
            if (pac.XmlConfig != null)
            {
                UpdateFilesFromXmlConfig(pac);
            }
        }

        /// <summary>
        /// 从 XML 配置更新文件信息
        /// </summary>
        private void UpdateFilesFromXmlConfig(PacInfo pac)
        {
            if (pac.XmlConfig == null)
                return;

            // 更新 FDL 配置
            if (pac.XmlConfig.Fdl1Config != null)
            {
                var fdl1 = GetFdl1(pac);
                if (fdl1 != null && pac.XmlConfig.Fdl1Config.Address > 0)
                {
                    fdl1.Address = (uint)pac.XmlConfig.Fdl1Config.Address;
                    Log("[PAC] 从 XML 更新 FDL1 地址: 0x{0:X}", fdl1.Address);
                }
            }

            if (pac.XmlConfig.Fdl2Config != null)
            {
                var fdl2 = GetFdl2(pac);
                if (fdl2 != null && pac.XmlConfig.Fdl2Config.Address > 0)
                {
                    fdl2.Address = (uint)pac.XmlConfig.Fdl2Config.Address;
                    Log("[PAC] 从 XML 更新 FDL2 地址: 0x{0:X}", fdl2.Address);
                }
            }

            // 更新文件地址
            foreach (var xmlFile in pac.XmlConfig.Files)
            {
                var pacFile = pac.Files.Find(f =>
                    f.PartitionName.Equals(xmlFile.Name, StringComparison.OrdinalIgnoreCase) ||
                    f.FileName.Equals(xmlFile.FileName, StringComparison.OrdinalIgnoreCase));

                if (pacFile != null && xmlFile.Address > 0)
                {
                    pacFile.Address = (uint)xmlFile.Address;
                }
            }
        }

        /// <summary>
        /// 提取文件数据到内存
        /// </summary>
        public byte[] ExtractFileData(string pacFilePath, PacFileEntry entry)
        {
            if (entry.Size == 0)
                return null;

            try
            {
                using (var fs = new FileStream(pacFilePath, FileMode.Open, FileAccess.Read))
                {
                    fs.Seek((long)entry.DataOffset, SeekOrigin.Begin);
                    
                    byte[] data = new byte[entry.Size];
                    int read = fs.Read(data, 0, (int)entry.Size);
                    
                    if (read < (int)entry.Size)
                    {
                        Array.Resize(ref data, read);
                    }
                    
                    return data;
                }
            }
            catch (Exception ex)
            {
                Log("[PAC] 提取文件数据失败 ({0}): {1}", entry.FileName, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 获取 XML 配置文件
        /// </summary>
        public List<PacFileEntry> GetXmlFiles(PacInfo pac)
        {
            return pac.Files.Where(f => 
                f.Type == PacFileType.XML || 
                f.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        #region 辅助方法

        private string ReadUnicodeString(BinaryReader reader, int byteLength)
        {
            byte[] bytes = reader.ReadBytes(byteLength);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }

        private uint ParseReversedUInt32(byte[] data, int offset)
        {
            if (offset + 4 > data.Length)
                return 0;

            // Reverse bytes
            byte[] reversed = new byte[4];
            reversed[0] = data[offset + 3];
            reversed[1] = data[offset + 2];
            reversed[2] = data[offset + 1];
            reversed[3] = data[offset];

            return BitConverter.ToUInt32(reversed, 0);
        }

        private ulong CombineHiLo(uint hi, uint lo)
        {
            if (hi > 2)
                return hi;
            if (lo > 2)
                return lo;
            return ((ulong)hi << 32) | lo;
        }

        private PacFileType DetermineFileType(PacFileEntry entry)
        {
            string name = entry.PartitionName.ToLower();
            string fileName = entry.FileName.ToLower();

            if (name == "fdl" || fileName.Contains("fdl1"))
                return PacFileType.FDL1;
            if (name == "fdl2" || fileName.Contains("fdl2"))
                return PacFileType.FDL2;
            if (fileName.EndsWith(".xml"))
                return PacFileType.XML;
            if (name.Contains("nv") || name.Contains("nvitem"))
                return PacFileType.NV;
            if (name.Contains("boot"))
                return PacFileType.Boot;
            if (name.Contains("system") || name.Contains("super"))
                return PacFileType.System;
            if (name.Contains("userdata"))
                return PacFileType.UserData;

            return PacFileType.Partition;
        }

        private bool IsSparseImage(byte[] header)
        {
            if (header.Length < 4)
                return false;

            // Sparse magic: 0xED26FF3A
            uint magic = BitConverter.ToUInt32(header, 0);
            return magic == 0xED26FF3A;
        }

        private string FormatSize(ulong size)
        {
            if (size >= 1024UL * 1024 * 1024)
                return string.Format("{0:F2} GB", size / (1024.0 * 1024 * 1024));
            if (size >= 1024 * 1024)
                return string.Format("{0:F2} MB", size / (1024.0 * 1024));
            if (size >= 1024)
                return string.Format("{0:F2} KB", size / 1024.0);
            return string.Format("{0} B", size);
        }

        private void Log(string format, params object[] args)
        {
            _log?.Invoke(string.Format(format, args));
        }

        #endregion
    }

    #region 数据结构

    /// <summary>
    /// PAC 信息
    /// </summary>
    public class PacInfo
    {
        public string FilePath { get; set; }
        public PacHeader Header { get; set; }
        public List<PacFileEntry> Files { get; set; }

        /// <summary>
        /// XML 配置 (如果 PAC 包含)
        /// </summary>
        public SprdXmlConfig XmlConfig { get; set; }

        /// <summary>
        /// 所有 XML 配置 (可能有多个)
        /// </summary>
        public List<SprdXmlConfig> AllXmlConfigs { get; set; } = new List<SprdXmlConfig>();

        /// <summary>
        /// 获取总大小
        /// </summary>
        public ulong TotalSize => ((ulong)Header.HiSize << 32) + Header.LoSize;

        /// <summary>
        /// 获取按刷机顺序排列的文件列表
        /// </summary>
        public List<PacFileEntry> GetFlashOrder()
        {
            var order = new List<PacFileEntry>();

            // 1. FDL1
            var fdl1 = Files.Find(f => f.Type == PacFileType.FDL1);
            if (fdl1 != null) order.Add(fdl1);

            // 2. FDL2
            var fdl2 = Files.Find(f => f.Type == PacFileType.FDL2);
            if (fdl2 != null) order.Add(fdl2);

            // 3. 如果有 XML 配置，按配置顺序
            if (XmlConfig != null && XmlConfig.Files.Count > 0)
            {
                foreach (var xmlFile in XmlConfig.Files)
                {
                    if (xmlFile.Type == SprdXmlFileType.FDL1 || xmlFile.Type == SprdXmlFileType.FDL2)
                        continue;

                    var pacFile = Files.Find(f => 
                        f.PartitionName.Equals(xmlFile.Name, StringComparison.OrdinalIgnoreCase) ||
                        f.FileName.Equals(xmlFile.FileName, StringComparison.OrdinalIgnoreCase));

                    if (pacFile != null && !order.Contains(pacFile))
                        order.Add(pacFile);
                }
            }

            // 4. 添加剩余文件
            foreach (var file in Files)
            {
                if (!order.Contains(file) && 
                    file.Type != PacFileType.FDL1 && 
                    file.Type != PacFileType.FDL2 &&
                    file.Type != PacFileType.XML &&
                    file.Size > 0)
                {
                    order.Add(file);
                }
            }

            return order;
        }
    }

    /// <summary>
    /// PAC 头
    /// </summary>
    public class PacHeader
    {
        public string Version { get; set; }
        public uint HiSize { get; set; }
        public uint LoSize { get; set; }
        public string ProductName { get; set; }
        public string FirmwareName { get; set; }
        public uint PartitionCount { get; set; }
        public uint PartitionsListStart { get; set; }
        public uint Mode { get; set; }
        public uint FlashType { get; set; }
        public uint NandStrategy { get; set; }
        public uint IsNvBackup { get; set; }
        public uint NandPageType { get; set; }
        public string ProductAlias { get; set; }
        public uint OmaDmProductFlag { get; set; }
        public uint IsOmaDM { get; set; }
        public uint IsPreload { get; set; }
        public uint Reserved { get; set; }
        public uint Magic { get; set; }
        public uint Crc1 { get; set; }
        public uint Crc2 { get; set; }
    }

    /// <summary>
    /// PAC 文件条目
    /// </summary>
    public class PacFileEntry
    {
        public uint HeaderLength { get; set; }
        public string PartitionName { get; set; }
        public string FileName { get; set; }
        public string OriginalFileName { get; set; }
        public uint HiDataOffset { get; set; }
        public uint LoDataOffset { get; set; }
        public uint HiSize { get; set; }
        public uint LoSize { get; set; }
        public ushort FileFlag { get; set; }
        public ushort CheckFlag { get; set; }
        public uint CanOmitFlag { get; set; }
        public uint AddrNum { get; set; }
        public uint Address { get; set; }

        // 计算值
        public ulong DataOffset { get; set; }
        public ulong Size { get; set; }
        public PacFileType Type { get; set; }
        public bool IsSparse { get; set; }

        public override string ToString()
        {
            return string.Format("{0} ({1}, {2} bytes)", PartitionName, FileName, Size);
        }
    }

    /// <summary>
    /// PAC 文件类型
    /// </summary>
    public enum PacFileType
    {
        Unknown,
        FDL1,
        FDL2,
        XML,
        NV,
        Boot,
        System,
        UserData,
        Partition
    }

    #endregion
}
