// ============================================================================
// SakuraEDL - eMMC GPT Parser | eMMC GPT 解析器
// ============================================================================
// [ZH] eMMC GPT 解析 - 解析 eMMC 设备的 GPT 分区表
// [EN] eMMC GPT Parser - Parse GPT partition table of eMMC devices
// [JA] eMMC GPT解析 - eMMCデバイスのGPTパーティションテーブル解析
// [KO] eMMC GPT 파서 - eMMC 기기의 GPT 파티션 테이블 분석
// [RU] Парсер GPT eMMC - Разбор таблицы разделов GPT устройств eMMC
// [ES] Analizador GPT eMMC - Análisis de tabla de particiones GPT eMMC
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SakuraEDL.Spreadtrum.ISP
{
    /// <summary>
    /// GPT 分区信息
    /// </summary>
    public class EmmcPartitionInfo
    {
        public Guid TypeGuid { get; set; }
        public Guid UniqueGuid { get; set; }
        public string Name { get; set; }
        public long StartLba { get; set; }
        public long EndLba { get; set; }
        public long Attributes { get; set; }
        
        /// <summary>
        /// 分区大小 (扇区数)
        /// </summary>
        public long SectorCount => EndLba - StartLba + 1;
        
        /// <summary>
        /// 分区大小 (字节，假设 512 字节扇区)
        /// </summary>
        public long Size => SectorCount * 512;
        
        /// <summary>
        /// 分区大小 (字节，指定扇区大小)
        /// </summary>
        public long GetSize(int sectorSize) => SectorCount * sectorSize;
        
        /// <summary>
        /// 起始偏移 (字节)
        /// </summary>
        public long GetStartOffset(int sectorSize) => StartLba * sectorSize;
        
        /// <summary>
        /// 是否为系统分区
        /// </summary>
        public bool IsSystem => TypeGuid == GptGuids.EfiSystemPartition;
        
        /// <summary>
        /// 是否为 Linux 文件系统
        /// </summary>
        public bool IsLinux => TypeGuid == GptGuids.LinuxFilesystem;
        
        public override string ToString()
        {
            return $"{Name}: LBA {StartLba}-{EndLba} ({SectorCount} sectors, {Size / 1024 / 1024} MB)";
        }
    }

    /// <summary>
    /// GPT 头信息
    /// </summary>
    public class GptHeaderInfo
    {
        public string Signature { get; set; }
        public uint Revision { get; set; }
        public uint HeaderSize { get; set; }
        public uint HeaderCrc32 { get; set; }
        public long CurrentLba { get; set; }
        public long BackupLba { get; set; }
        public long FirstUsableLba { get; set; }
        public long LastUsableLba { get; set; }
        public Guid DiskGuid { get; set; }
        public long PartitionEntryLba { get; set; }
        public uint PartitionEntryCount { get; set; }
        public uint PartitionEntrySize { get; set; }
        public uint PartitionArrayCrc32 { get; set; }
        
        public bool IsValid => Signature == "EFI PART";
    }

    /// <summary>
    /// 常用 GPT GUID
    /// </summary>
    public static class GptGuids
    {
        public static readonly Guid Unused = Guid.Empty;
        public static readonly Guid EfiSystemPartition = new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
        public static readonly Guid MicrosoftBasicData = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
        public static readonly Guid LinuxFilesystem = new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
        public static readonly Guid LinuxSwap = new Guid("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");
        public static readonly Guid LinuxLvm = new Guid("E6D6D379-F507-44C2-A23C-238F2A3DF928");
        public static readonly Guid AndroidBootloader = new Guid("2568845D-2332-4675-BC39-8FA5A4748D15");
        public static readonly Guid AndroidBoot = new Guid("49A4D17F-93A3-45C1-A0DE-F50B2EBE2599");
        public static readonly Guid AndroidRecovery = new Guid("4177C722-9E92-4AAB-8644-43502BFD5506");
        public static readonly Guid AndroidMisc = new Guid("EF32A33B-A409-486C-9141-9FFB711F6266");
        public static readonly Guid AndroidMetadata = new Guid("20AC26BE-20B7-11E3-84C5-6CFDB94711E9");
        public static readonly Guid AndroidSystem = new Guid("38F428E6-D326-425D-9140-6E0EA133647C");
        public static readonly Guid AndroidCache = new Guid("A893EF21-E428-470A-9E55-0668FD91A2D9");
        public static readonly Guid AndroidData = new Guid("DC76DDA9-5AC1-491C-AF42-A82591580C0D");
        public static readonly Guid AndroidPersistent = new Guid("EBC597D0-2053-4B15-8B64-E0AAC75F4DB1");
        public static readonly Guid AndroidVendor = new Guid("C5A0AEEC-13EA-11E5-A1B1-001E67CA0C3C");
        public static readonly Guid AndroidConfig = new Guid("BD59408B-4514-490D-BF12-9878D963F378");
        public static readonly Guid AndroidFactory = new Guid("8F68CC74-C5E5-48DA-BE91-A0C8C15E9C80");
        public static readonly Guid AndroidFactoryAlt = new Guid("9FDAA6EF-4B3F-40D2-BA8D-BFF16BFB887B");
        public static readonly Guid AndroidFastboot = new Guid("767941D0-2085-11E3-AD3B-6CFDB94711E9");
        public static readonly Guid AndroidTertiaryCache = new Guid("55D7E039-64D6-4956-8E87-3D0B53F8F97A");
        public static readonly Guid AndroidOem = new Guid("AC6D7924-EB71-4DF8-B48D-E267B27148FF");
    }

    /// <summary>
    /// eMMC GPT 解析器
    /// </summary>
    public class EmmcGptParser
    {
        #region Constants
        
        private const string GPT_SIGNATURE = "EFI PART";
        private const int GPT_HEADER_SIZE = 92;
        private const int GPT_ENTRY_SIZE = 128;
        private const int MAX_PARTITION_COUNT = 128;
        
        #endregion

        #region Properties
        
        /// <summary>
        /// GPT 头信息
        /// </summary>
        public GptHeaderInfo Header { get; private set; }
        
        /// <summary>
        /// 分区列表
        /// </summary>
        public List<EmmcPartitionInfo> Partitions { get; private set; } = new List<EmmcPartitionInfo>();
        
        /// <summary>
        /// 扇区大小
        /// </summary>
        public int SectorSize { get; set; } = 512;
        
        /// <summary>
        /// 是否为有效 GPT
        /// </summary>
        public bool IsValid => Header?.IsValid ?? false;
        
        #endregion

        #region Parse Methods
        
        /// <summary>
        /// 解析 GPT
        /// </summary>
        /// <param name="gptData">GPT 数据 (包括 MBR + GPT Header + Partition Table)</param>
        public bool Parse(byte[] gptData)
        {
            if (gptData == null || gptData.Length < SectorSize * 2)
                return false;

            try
            {
                // 解析 GPT 头 (LBA 1)
                Header = ParseGptHeader(gptData, SectorSize);
                
                if (!Header.IsValid)
                    return false;

                // 解析分区表
                int partitionTableOffset = (int)(Header.PartitionEntryLba * SectorSize);
                if (partitionTableOffset >= gptData.Length)
                {
                    // 分区表可能紧跟在头后面
                    partitionTableOffset = SectorSize * 2;
                }

                Partitions = ParsePartitionEntries(
                    gptData, 
                    partitionTableOffset, 
                    (int)Header.PartitionEntryCount, 
                    (int)Header.PartitionEntrySize);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从 eMMC 设备解析 GPT
        /// </summary>
        public bool ParseFromDevice(EmmcDevice device)
        {
            if (device == null || !device.IsOpen)
                return false;

            SectorSize = device.SectorSize;

            // 读取 GPT 头
            var headerResult = device.ReadSectors(1, 1);
            if (!headerResult.Success)
                return false;

            Header = ParseGptHeader(headerResult.Data, 0);
            if (!Header.IsValid)
                return false;

            // 读取分区表
            int tableSectors = (int)Math.Ceiling((double)(Header.PartitionEntryCount * Header.PartitionEntrySize) / SectorSize);
            var tableResult = device.ReadSectors(Header.PartitionEntryLba, tableSectors);
            if (!tableResult.Success)
                return false;

            Partitions = ParsePartitionEntries(
                tableResult.Data, 
                0, 
                (int)Header.PartitionEntryCount, 
                (int)Header.PartitionEntrySize);

            return true;
        }

        /// <summary>
        /// 解析 GPT 头
        /// </summary>
        private GptHeaderInfo ParseGptHeader(byte[] data, int offset)
        {
            var header = new GptHeaderInfo();

            header.Signature = Encoding.ASCII.GetString(data, offset, 8);
            header.Revision = BitConverter.ToUInt32(data, offset + 8);
            header.HeaderSize = BitConverter.ToUInt32(data, offset + 12);
            header.HeaderCrc32 = BitConverter.ToUInt32(data, offset + 16);
            // Reserved 4 bytes at offset 20
            header.CurrentLba = BitConverter.ToInt64(data, offset + 24);
            header.BackupLba = BitConverter.ToInt64(data, offset + 32);
            header.FirstUsableLba = BitConverter.ToInt64(data, offset + 40);
            header.LastUsableLba = BitConverter.ToInt64(data, offset + 48);
            header.DiskGuid = new Guid(data.Skip(offset + 56).Take(16).ToArray());
            header.PartitionEntryLba = BitConverter.ToInt64(data, offset + 72);
            header.PartitionEntryCount = BitConverter.ToUInt32(data, offset + 80);
            header.PartitionEntrySize = BitConverter.ToUInt32(data, offset + 84);
            header.PartitionArrayCrc32 = BitConverter.ToUInt32(data, offset + 88);

            return header;
        }

        /// <summary>
        /// 解析分区表项
        /// </summary>
        private List<EmmcPartitionInfo> ParsePartitionEntries(byte[] data, int offset, int count, int entrySize)
        {
            var partitions = new List<EmmcPartitionInfo>();

            for (int i = 0; i < count && i < MAX_PARTITION_COUNT; i++)
            {
                int entryOffset = offset + (i * entrySize);
                if (entryOffset + entrySize > data.Length)
                    break;

                var partition = ParsePartitionEntry(data, entryOffset);
                
                // 跳过空分区
                if (partition.TypeGuid != Guid.Empty)
                {
                    partitions.Add(partition);
                }
            }

            return partitions;
        }

        /// <summary>
        /// 解析单个分区项
        /// </summary>
        private EmmcPartitionInfo ParsePartitionEntry(byte[] data, int offset)
        {
            var partition = new EmmcPartitionInfo();

            partition.TypeGuid = new Guid(data.Skip(offset).Take(16).ToArray());
            partition.UniqueGuid = new Guid(data.Skip(offset + 16).Take(16).ToArray());
            partition.StartLba = BitConverter.ToInt64(data, offset + 32);
            partition.EndLba = BitConverter.ToInt64(data, offset + 40);
            partition.Attributes = BitConverter.ToInt64(data, offset + 48);

            // 名称 (UTF-16LE, 72 bytes = 36 characters)
            byte[] nameBytes = new byte[72];
            Array.Copy(data, offset + 56, nameBytes, 0, 72);
            partition.Name = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');

            return partition;
        }

        #endregion

        #region Utility Methods
        
        /// <summary>
        /// 按名称查找分区
        /// </summary>
        public EmmcPartitionInfo FindPartition(string name)
        {
            return Partitions.FirstOrDefault(p => 
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 按 GUID 查找分区
        /// </summary>
        public EmmcPartitionInfo FindPartitionByGuid(Guid typeGuid)
        {
            return Partitions.FirstOrDefault(p => p.TypeGuid == typeGuid);
        }

        /// <summary>
        /// 获取所有 Android 分区
        /// </summary>
        public IEnumerable<EmmcPartitionInfo> GetAndroidPartitions()
        {
            var androidGuids = new[]
            {
                GptGuids.AndroidBoot, GptGuids.AndroidRecovery, GptGuids.AndroidSystem,
                GptGuids.AndroidCache, GptGuids.AndroidData, GptGuids.AndroidVendor,
                GptGuids.AndroidMisc, GptGuids.AndroidMetadata, GptGuids.AndroidPersistent,
                GptGuids.AndroidBootloader, GptGuids.AndroidConfig, GptGuids.AndroidFactory,
                GptGuids.AndroidFastboot, GptGuids.AndroidOem
            };

            return Partitions.Where(p => androidGuids.Contains(p.TypeGuid));
        }

        /// <summary>
        /// 打印分区表
        /// </summary>
        public string GetPartitionTableString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== GPT Partition Table ===");
            
            if (Header != null)
            {
                sb.AppendLine($"Disk GUID: {Header.DiskGuid}");
                sb.AppendLine($"First Usable LBA: {Header.FirstUsableLba}");
                sb.AppendLine($"Last Usable LBA: {Header.LastUsableLba}");
                sb.AppendLine($"Partition Count: {Partitions.Count}");
                sb.AppendLine();
            }

            sb.AppendLine($"{"#",-3} {"Name",-20} {"Start LBA",-12} {"End LBA",-12} {"Size (MB)",-10}");
            sb.AppendLine(new string('-', 60));

            int index = 0;
            foreach (var p in Partitions)
            {
                long sizeMb = p.GetSize(SectorSize) / 1024 / 1024;
                sb.AppendLine($"{index,-3} {p.Name,-20} {p.StartLba,-12} {p.EndLba,-12} {sizeMb,-10}");
                index++;
            }

            return sb.ToString();
        }

        #endregion

        #region Static Methods
        
        /// <summary>
        /// 检查是否为有效的 GPT 数据
        /// </summary>
        public static bool IsValidGpt(byte[] data, int sectorSize = 512)
        {
            if (data == null || data.Length < sectorSize * 2)
                return false;

            // 检查 GPT 签名 (在 LBA 1)
            string signature = Encoding.ASCII.GetString(data, sectorSize, 8);
            return signature == GPT_SIGNATURE;
        }

        /// <summary>
        /// 检查是否有保护性 MBR
        /// </summary>
        public static bool HasProtectiveMbr(byte[] data)
        {
            if (data == null || data.Length < 512)
                return false;

            // 检查 MBR 签名
            if (data[510] != 0x55 || data[511] != 0xAA)
                return false;

            // 检查第一个分区类型是否为 0xEE (GPT Protective)
            return data[450] == 0xEE;
        }

        #endregion
    }
}
