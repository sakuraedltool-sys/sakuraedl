// ============================================================================
// SakuraEDL - GPT Parser | GPT 分区表解析器
// ============================================================================
// [ZH] GPT 分区表解析 - 支持自动扇区检测和 CRC 校验
// [EN] GPT Partition Table Parser - Auto sector detection and CRC verification
// [JA] GPTパーティションテーブル解析 - 自動セクタ検出とCRC検証
// [KO] GPT 파티션 테이블 파서 - 자동 섹터 감지 및 CRC 검증
// [RU] Парсер таблицы разделов GPT - Автоопределение секторов и CRC
// [ES] Analizador de tabla GPT - Detección automática y verificación CRC
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SakuraEDL.Qualcomm.Models;

namespace SakuraEDL.Qualcomm.Common
{
    /// <summary>
    /// GPT Header 信息
    /// </summary>
    public class GptHeaderInfo
    {
        public string Signature { get; set; }           // "EFI PART"
        public uint Revision { get; set; }              // 版本 (通常 0x00010000)
        public uint HeaderSize { get; set; }            // Header 大小 (通常 92)
        public uint HeaderCrc32 { get; set; }           // Header CRC32
        public ulong MyLba { get; set; }                // 当前 Header LBA
        public ulong AlternateLba { get; set; }         // 备份 Header LBA
        public ulong FirstUsableLba { get; set; }       // 第一个可用 LBA
        public ulong LastUsableLba { get; set; }        // 最后可用 LBA
        public string DiskGuid { get; set; }            // 磁盘 GUID
        public ulong PartitionEntryLba { get; set; }    // 分区条目起始 LBA
        public uint NumberOfPartitionEntries { get; set; }  // 分区条目数量
        public uint SizeOfPartitionEntry { get; set; }  // 每条目大小 (通常 128)
        public uint PartitionEntryCrc32 { get; set; }   // 分区条目 CRC32
        
        public bool IsValid { get; set; }
        public bool CrcValid { get; set; }
        public string GptType { get; set; }             // "gptmain" 或 "gptbackup"
        public int SectorSize { get; set; }             // 扇区大小 (512 或 4096)
        
        /// <summary>是否为备份 GPT (Header 在分区条目之后)</summary>
        public bool IsBackupGpt => GptType == "gptbackup";
    }

    /// <summary>
    /// 槽位信息 (保留兼容性)
    /// </summary>
    public class SlotInfo
    {
        public string CurrentSlot { get; set; }         // "a", "b", "undefined", "nonexistent"
        public string OtherSlot { get; set; }
        public bool HasAbPartitions { get; set; }
    }

    /// <summary>
    /// GPT 解析结果
    /// </summary>
    public class GptParseResult
    {
        public GptHeaderInfo Header { get; set; }
        public List<PartitionInfo> Partitions { get; set; }
        public SlotInfo SlotInfo { get; set; }
        public SlotInfoV2 SlotInfoV2 { get; set; }      // 新版槽位信息
        public int Lun { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        public GptParseResult()
        {
            Partitions = new List<PartitionInfo>();
            SlotInfo = new SlotInfo { CurrentSlot = "nonexistent", OtherSlot = "nonexistent" };
        }
    }

    /// <summary>
    /// GPT 分区表解析器 (重构版)
    /// </summary>
    public class GptParser
    {
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;
        private readonly SlotDetector _slotDetector;
        
        // GPT 签名
        private static readonly byte[] GPT_SIGNATURE = Encoding.ASCII.GetBytes("EFI PART");

        // 静态 CRC32 表 (避免每次重新生成)
        private static readonly uint[] CRC32_TABLE = GenerateStaticCrc32Table();

        public GptParser(Action<string> log = null, Action<string> logDetail = null)
        {
            _log = log ?? (s => { });
            _logDetail = logDetail ?? _log;
            _slotDetector = new SlotDetector(_log, _logDetail);
        }

        #region 主要解析方法

        /// <summary>
        /// 解析 GPT 数据
        /// </summary>
        public GptParseResult Parse(byte[] gptData, int lun, int defaultSectorSize = 4096)
        {
            var result = new GptParseResult { Lun = lun };

            try
            {
                if (gptData == null || gptData.Length < 512)
                {
                    result.ErrorMessage = "GPT 数据过小";
                    return result;
                }

                // 1. 查找 GPT Header 并自动检测扇区大小
                int headerOffset = FindGptHeader(gptData);
                if (headerOffset < 0)
                {
                    result.ErrorMessage = "未找到 GPT 签名";
                    return result;
                }

                // 2. 解析 GPT Header
                var header = ParseGptHeader(gptData, headerOffset, defaultSectorSize);
                if (!header.IsValid)
                {
                    result.ErrorMessage = "GPT Header 无效";
                    return result;
                }
                result.Header = header;

                // 3. 自动检测扇区大小 (参考 gpttool)
                // Disk_SecSize_b_Dec = HeaderArea_Start_InF_b_Dec / HeaderArea_Start_Sec_Dec
                if (header.MyLba > 0 && headerOffset > 0)
                {
                    int detectedSectorSize = headerOffset / (int)header.MyLba;
                    if (detectedSectorSize == 512 || detectedSectorSize == 4096)
                    {
                        header.SectorSize = detectedSectorSize;
                        _logDetail(string.Format("[GPT] 自动检测扇区大小: {0} 字节 (Header偏移={1}, MyLBA={2})", 
                            detectedSectorSize, headerOffset, header.MyLba));
                    }
                    else
                    {
                        // 尝试根据分区条目 LBA 推断
                        if (header.PartitionEntryLba == 2)
                        {
                            // 标准情况: 分区条目紧跟 Header
                            header.SectorSize = defaultSectorSize;
                            _logDetail(string.Format("[GPT] 使用默认扇区大小: {0} 字节", defaultSectorSize));
                        }
                        else
                        {
                            header.SectorSize = defaultSectorSize;
                        }
                    }
                }
                else
                {
                    header.SectorSize = defaultSectorSize;
                    _logDetail(string.Format("[GPT] MyLBA=0，使用默认扇区大小: {0} 字节", defaultSectorSize));
                }

                // 4. 验证 CRC (可选)
                header.CrcValid = VerifyCrc32(gptData, headerOffset, header);

                // 5. 解析分区条目 (使用简化逻辑)
                result.Partitions = ParsePartitionEntriesSimplified(gptData, headerOffset, header, lun);

                // 6. 检测 A/B 槽位 (使用新的 SlotDetector)
                result.SlotInfoV2 = _slotDetector.Detect(result.Partitions);
                result.SlotInfo = result.SlotInfoV2.ToLegacy();

                result.Success = true;
                _logDetail(string.Format("[GPT] LUN{0}: {1} 个分区, 槽位: {2} ({3})",
                    lun, result.Partitions.Count, result.SlotInfo.CurrentSlot, 
                    result.SlotInfoV2?.DetectionMethod ?? ""));
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                _log(string.Format("[GPT] 解析异常: {0}", ex.Message));
            }

            return result;
        }

        #endregion

        #region GPT Header 解析

        /// <summary>
        /// 查找 GPT Header 位置
        /// </summary>
        private int FindGptHeader(byte[] data)
        {
            // 常见偏移位置
            int[] searchOffsets = { 4096, 512, 0, 4096 * 2, 512 * 2 };

            foreach (int offset in searchOffsets)
            {
                if (offset + 92 <= data.Length && MatchSignature(data, offset))
                {
                    _logDetail(string.Format("[GPT] 在偏移 {0} 处找到 GPT Header", offset));
                    return offset;
                }
            }

            // 暴力搜索 (每 512 字节)
            for (int i = 0; i <= data.Length - 92; i += 512)
            {
                if (MatchSignature(data, i))
                {
                    _logDetail(string.Format("[GPT] 暴力搜索: 在偏移 {0} 处找到 GPT Header", i));
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 匹配 GPT 签名
        /// </summary>
        private bool MatchSignature(byte[] data, int offset)
        {
            if (offset + 8 > data.Length) return false;
            for (int i = 0; i < 8; i++)
            {
                if (data[offset + i] != GPT_SIGNATURE[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 解析 GPT Header
        /// </summary>
        private GptHeaderInfo ParseGptHeader(byte[] data, int offset, int defaultSectorSize)
        {
            var header = new GptHeaderInfo
            {
                SectorSize = defaultSectorSize
            };

            try
            {
                // 签名 (0-8)
                header.Signature = Encoding.ASCII.GetString(data, offset, 8);
                if (header.Signature != "EFI PART")
                {
                    header.IsValid = false;
                    return header;
                }

                // 版本 (8-12)
                header.Revision = BitConverter.ToUInt32(data, offset + 8);

                // Header 大小 (12-16)
                header.HeaderSize = BitConverter.ToUInt32(data, offset + 12);

                // Header CRC32 (16-20)
                header.HeaderCrc32 = BitConverter.ToUInt32(data, offset + 16);

                // 保留 (20-24)

                // MyLBA (24-32)
                header.MyLba = BitConverter.ToUInt64(data, offset + 24);

                // AlternateLBA (32-40)
                header.AlternateLba = BitConverter.ToUInt64(data, offset + 32);

                // FirstUsableLBA (40-48)
                header.FirstUsableLba = BitConverter.ToUInt64(data, offset + 40);

                // LastUsableLBA (48-56)
                header.LastUsableLba = BitConverter.ToUInt64(data, offset + 48);

                // DiskGUID (56-72)
                header.DiskGuid = FormatGuid(data, offset + 56);

                // PartitionEntryLBA (72-80)
                header.PartitionEntryLba = BitConverter.ToUInt64(data, offset + 72);

                // NumberOfPartitionEntries (80-84)
                header.NumberOfPartitionEntries = BitConverter.ToUInt32(data, offset + 80);

                // SizeOfPartitionEntry (84-88)
                header.SizeOfPartitionEntry = BitConverter.ToUInt32(data, offset + 84);

                // PartitionEntryCRC32 (88-92)
                header.PartitionEntryCrc32 = BitConverter.ToUInt32(data, offset + 88);

                // 判断 GPT 类型
                if (header.MyLba != 0 && header.AlternateLba != 0)
                {
                    header.GptType = header.MyLba < header.AlternateLba ? "gptmain" : "gptbackup";
                }
                else if (header.MyLba != 0)
                {
                    header.GptType = "gptmain";
                }
                else
                {
                    header.GptType = "gptbackup";
                }

                header.IsValid = true;
            }
            catch
            {
                header.IsValid = false;
            }

            return header;
        }

        #endregion

        #region 分区条目解析 (简化版)

        /// <summary>
        /// 解析分区条目 (简化版 - 只保留3种核心策略)
        /// </summary>
        private List<PartitionInfo> ParsePartitionEntriesSimplified(byte[] data, int headerOffset, GptHeaderInfo header, int lun)
        {
            var partitions = new List<PartitionInfo>();
            int sectorSize = header.SectorSize > 0 ? header.SectorSize : 4096;
            int entrySize = (int)header.SizeOfPartitionEntry;
            if (entrySize <= 0 || entrySize > 512) entrySize = 128;
            
            _logDetail($"[GPT] LUN{lun} 解析: Header@{headerOffset}, SectorSize={sectorSize}, EntrySize={entrySize}");
            _logDetail($"[GPT] Header: PartitionEntryLba={header.PartitionEntryLba}, Entries={header.NumberOfPartitionEntries}");
            
            // ========== 计算分区条目偏移 (3种核心策略) ==========
            int entryOffset = -1;
            string strategy = "";
            
            // 策略1: 使用 Header 中的 PartitionEntryLba (最标准)
            if (header.PartitionEntryLba > 0)
            {
                // 尝试当前扇区大小
                long offset = (long)header.PartitionEntryLba * sectorSize;
                if (offset > 0 && offset < data.Length - 128 && HasValidPartitionEntry(data, (int)offset))
                {
                    entryOffset = (int)offset;
                    strategy = $"PartitionEntryLba({header.PartitionEntryLba}) * {sectorSize}B";
                }
                else
                {
                    // 尝试另一种扇区大小
                    int altSectorSize = sectorSize == 4096 ? 512 : 4096;
                    offset = (long)header.PartitionEntryLba * altSectorSize;
                    if (offset > 0 && offset < data.Length - 128 && HasValidPartitionEntry(data, (int)offset))
                    {
                        entryOffset = (int)offset;
                        sectorSize = altSectorSize;
                        header.SectorSize = altSectorSize;
                        strategy = $"PartitionEntryLba({header.PartitionEntryLba}) * {altSectorSize}B (修正)";
                    }
                }
            }
            
            // 策略2: 标准偏移 (LBA 2)
            if (entryOffset < 0)
            {
                int[] standardOffsets = { 
                    1024,   // LBA 2 * 512B (eMMC 标准)
                    8192,   // LBA 2 * 4096B (UFS 标准)
                };
                foreach (int offset in standardOffsets)
                {
                    if (offset < data.Length - 128 && HasValidPartitionEntry(data, offset))
                    {
                        entryOffset = offset;
                        strategy = $"标准偏移 {offset}";
                        // 推断扇区大小
                        if (offset == 1024) sectorSize = 512;
                        else if (offset == 8192) sectorSize = 4096;
                        break;
                    }
                }
            }
            
            // 策略3: Header 后搜索 (备份 GPT 或非标准布局)
            if (entryOffset < 0)
            {
                // 向后搜索
                for (int offset = headerOffset + 92; offset < Math.Min(data.Length - 128, headerOffset + 32768); offset += 128)
                {
                    if (HasValidPartitionEntry(data, offset))
                    {
                        entryOffset = offset;
                        strategy = $"Header后搜索 @{offset}";
                        break;
                    }
                }
                
                // 向前搜索 (备份 GPT)
                if (entryOffset < 0 && header.IsBackupGpt && headerOffset > 16384)
                {
                    for (int offset = headerOffset - 128; offset >= Math.Max(0, headerOffset - 32768); offset -= 128)
                    {
                        if (HasValidPartitionEntry(data, offset))
                        {
                            // 继续向前找第一个条目
                            int firstEntry = offset;
                            while (firstEntry - 128 >= 0 && HasValidPartitionEntry(data, firstEntry - 128))
                                firstEntry -= 128;
                            entryOffset = firstEntry;
                            strategy = $"备份GPT向前搜索 @{firstEntry}";
                            break;
                        }
                    }
                }
            }
            
            if (entryOffset < 0)
            {
                _log($"[GPT] LUN{lun} 无法找到有效的分区条目偏移");
                return partitions;
            }
            
            _logDetail($"[GPT] 分区条目偏移: {entryOffset} ({strategy})");
            
            // ========== 计算扫描数量 ==========
            int maxEntries = (int)header.NumberOfPartitionEntries;
            if (maxEntries <= 0 || maxEntries > 512) maxEntries = 128;
            
            // 确保不超出数据范围
            int maxFromData = (data.Length - entryOffset) / entrySize;
            maxEntries = Math.Min(maxEntries, maxFromData);
            
            // ========== 解析分区条目 ==========
            int validCount = 0;
            for (int i = 0; i < maxEntries; i++)
            {
                int offset = entryOffset + i * entrySize;
                if (offset + 128 > data.Length) break;
                
                var partition = ParsePartitionEntry(data, offset, lun, sectorSize, i + 1);
                if (partition != null && !string.IsNullOrWhiteSpace(partition.Name))
                {
                    partitions.Add(partition);
                    validCount++;
                    
                    if (validCount <= 5 || validCount % 20 == 0)
                    {
                        _logDetail($"[GPT] #{validCount}: {partition.Name} @ LBA {partition.StartSector} ({partition.FormattedSize})");
                    }
                }
            }
            
            _logDetail($"[GPT] LUN{lun} 解析完成: {validCount} 个分区");
            return partitions;
        }

        /// <summary>
        /// 解析分区条目 (旧版，保留兼容性)
        /// </summary>
        private List<PartitionInfo> ParsePartitionEntries(byte[] data, int headerOffset, GptHeaderInfo header, int lun)
        {
            var partitions = new List<PartitionInfo>();

            try
            {
                int sectorSize = header.SectorSize > 0 ? header.SectorSize : 4096;
                
                _logDetail(string.Format("[GPT] LUN{0} 开始解析分区条目 (数据长度={1}, HeaderOffset={2}, SectorSize={3})", 
                    lun, data.Length, headerOffset, sectorSize));
                _logDetail(string.Format("[GPT] Header信息: PartitionEntryLba={0}, NumberOfEntries={1}, EntrySize={2}, FirstUsableLba={3}",
                    header.PartitionEntryLba, header.NumberOfPartitionEntries, header.SizeOfPartitionEntry, header.FirstUsableLba));

                // ========== 计算分区条目起始位置 - 多种策略 ==========
                int entryOffset = -1;
                string usedStrategy = "";
                
                // 策略1: 使用 Header 中指定的 PartitionEntryLba
                if (header.PartitionEntryLba > 0)
                {
                    long calcOffset = (long)header.PartitionEntryLba * sectorSize;
                    if (calcOffset > 0 && calcOffset < data.Length - 128)
                    {
                        // 验证该偏移是否有有效的分区条目
                        if (HasValidPartitionEntry(data, (int)calcOffset))
                        {
                            entryOffset = (int)calcOffset;
                            usedStrategy = string.Format("策略1 (PartitionEntryLba): {0} * {1} = {2}", 
                                header.PartitionEntryLba, sectorSize, entryOffset);
                        }
                        else
                        {
                            _logDetail(string.Format("[GPT] 策略1 计算偏移 {0} 无有效分区，尝试其他策略", calcOffset));
                        }
                    }
                }
                
                // 策略2: 尝试不同扇区大小计算
                if (entryOffset < 0 && header.PartitionEntryLba > 0)
                {
                    int[] trySectorSizes = { 512, 4096 };
                    foreach (int trySectorSize in trySectorSizes)
                    {
                        if (trySectorSize == sectorSize) continue; // 跳过已尝试的
                        
                        long calcOffset = (long)header.PartitionEntryLba * trySectorSize;
                        if (calcOffset > 0 && calcOffset < data.Length - 128 && HasValidPartitionEntry(data, (int)calcOffset))
                        {
                            entryOffset = (int)calcOffset;
                            sectorSize = trySectorSize; // 更新扇区大小
                            header.SectorSize = trySectorSize;
                            usedStrategy = string.Format("策略2 (尝试扇区大小{0}B): {1} * {0} = {2}", 
                                trySectorSize, header.PartitionEntryLba, entryOffset);
                            break;
                        }
                    }
                }
                
                // 策略3: 小米/OPPO 等设备使用 512B 扇区，分区条目通常在 LBA 2 = 1024
                if (entryOffset < 0)
                {
                    int xiaomiOffset = 1024; // LBA 2 * 512B
                    if (xiaomiOffset < data.Length - 128 && HasValidPartitionEntry(data, xiaomiOffset))
                    {
                        entryOffset = xiaomiOffset;
                        usedStrategy = string.Format("策略3 (512B扇区标准): 偏移 {0}", entryOffset);
                    }
                }
                
                // 策略4: 4KB 扇区，分区条目在 LBA 2 = 8192
                if (entryOffset < 0)
                {
                    int ufsOffset = 8192; // LBA 2 * 4096B
                    if (ufsOffset < data.Length - 128 && HasValidPartitionEntry(data, ufsOffset))
                    {
                        entryOffset = ufsOffset;
                        usedStrategy = string.Format("策略4 (4KB扇区标准): 偏移 {0}", entryOffset);
                    }
                }
                
                // 策略5: Header 后紧跟分区条目 (不同扇区大小)
                if (entryOffset < 0)
                {
                    int[] tryGaps = { 512, 4096, 1024, 2048 };
                    foreach (int gap in tryGaps)
                    {
                        int relativeOffset = headerOffset + gap;
                        if (relativeOffset < data.Length - 128 && HasValidPartitionEntry(data, relativeOffset))
                        {
                            entryOffset = relativeOffset;
                            usedStrategy = string.Format("策略5 (Header+{0}): {1} + {0} = {2}", 
                                gap, headerOffset, entryOffset);
                            break;
                        }
                    }
                }
                
                // 策略6: 暴力探测更多常见偏移
                if (entryOffset < 0)
                {
                    // 常见偏移：各种扇区大小和 LBA 组合
                    int[] commonOffsets = { 
                        1024, 8192, 4096, 2048, 512,           // 基本偏移
                        4096 * 2, 512 * 4, 512 * 6,            // LBA 2 变体
                        16384, 32768,                          // 大扇区/大偏移
                        headerOffset + 92,                      // Header 紧随（无填充）
                        headerOffset + 128                      // Header 紧随（128对齐）
                    };
                    foreach (int tryOffset in commonOffsets)
                    {
                        if (tryOffset > 0 && tryOffset < data.Length - 128 && HasValidPartitionEntry(data, tryOffset))
                        {
                            entryOffset = tryOffset;
                            usedStrategy = string.Format("策略6 (暴力探测): 偏移 {0}", entryOffset);
                            break;
                        }
                    }
                }
                
                // 策略7: 从 Header 后开始每 128 字节搜索第一个有效分区
                if (entryOffset < 0)
                {
                    for (int searchOffset = headerOffset + 92; searchOffset < data.Length - 128 && searchOffset < headerOffset + 32768; searchOffset += 128)
                    {
                        if (HasValidPartitionEntry(data, searchOffset))
                        {
                            entryOffset = searchOffset;
                            usedStrategy = string.Format("策略7 (向后搜索): 偏移 {0}", entryOffset);
                            break;
                        }
                    }
                }
                
                // 策略8: 备份 GPT - 向前搜索 (分区条目在 Header 之前)
                // 备份 GPT 布局: Partition Entries -> Header (在磁盘末尾)
                if (entryOffset < 0 && headerOffset > 128)
                {
                    // 计算可能的分区表大小 (通常 128 * 128 = 16KB)
                    int entriesSize = 128 * 128;  // 默认 128 个条目
                    int[] tryEntrySizes = { entriesSize, 128 * 64, 128 * 32, 128 * 256 };
                    
                    foreach (int trySize in tryEntrySizes)
                    {
                        int backwardOffset = headerOffset - trySize;
                        if (backwardOffset >= 0 && backwardOffset < headerOffset - 128)
                        {
                            // 验证这个位置是否有有效分区
                            if (HasValidPartitionEntry(data, backwardOffset))
                            {
                                entryOffset = backwardOffset;
                                usedStrategy = string.Format("策略8 (备份GPT向前搜索): Header({0}) - {1} = {2}", 
                                    headerOffset, trySize, entryOffset);
                                break;
                            }
                        }
                    }
                    
                    // 如果还没找到，从 Header 向前每 128 字节搜索
                    if (entryOffset < 0)
                    {
                        for (int searchOffset = headerOffset - 128; searchOffset >= 0 && searchOffset > headerOffset - 32768; searchOffset -= 128)
                        {
                            if (HasValidPartitionEntry(data, searchOffset))
                            {
                                // 找到了，继续向前找到第一个条目
                                int firstEntry = searchOffset;
                                while (firstEntry - 128 >= 0 && HasValidPartitionEntry(data, firstEntry - 128))
                                {
                                    firstEntry -= 128;
                                }
                                entryOffset = firstEntry;
                                usedStrategy = string.Format("策略8 (备份GPT搜索): 偏移 {0}", entryOffset);
                                break;
                            }
                        }
                    }
                }
                
                // 最终检查
                if (entryOffset < 0 || entryOffset >= data.Length - 128)
                {
                    _logDetail(string.Format("[GPT] 无法确定有效的分区条目偏移, 尝试的最后 entryOffset={0}, dataLen={1}", entryOffset, data.Length));
                    return partitions;
                }
                
                _logDetail(string.Format("[GPT] {0}", usedStrategy));

                int entrySize = (int)header.SizeOfPartitionEntry;
                if (entrySize <= 0 || entrySize > 512) entrySize = 128;

                // ========== 计算分区条目数量 ==========
                int headerEntries = (int)header.NumberOfPartitionEntries;
                
                // 验证 Header 指定的分区数量是否合理
                // 有些设备 Header 中的 NumberOfPartitionEntries 可能是 0 或不正确
                if (headerEntries <= 0 || headerEntries > 1024)
                {
                    headerEntries = 128; // 默认值
                    _logDetail(string.Format("[GPT] Header.NumberOfPartitionEntries 异常({0})，使用默认值 128", 
                        header.NumberOfPartitionEntries));
                }
                
                // gpttool 方式: ParEntriesArea_Size = (FirstUsableLba - PartitionEntryLba) * SectorSize
                int actualAvailableEntries = 0;
                if (header.FirstUsableLba > header.PartitionEntryLba && header.PartitionEntryLba > 0)
                {
                    long parEntriesAreaSize = (long)(header.FirstUsableLba - header.PartitionEntryLba) * sectorSize;
                    actualAvailableEntries = (int)(parEntriesAreaSize / entrySize);
                    _logDetail(string.Format("[GPT] gpttool方式: ({0}-{1})*{2}/{3}={4}", 
                        header.FirstUsableLba, header.PartitionEntryLba, sectorSize, entrySize, actualAvailableEntries));
                }
                
                // 从数据长度计算可扫描的最大条目数
                int maxFromData = Math.Max(0, (data.Length - entryOffset) / entrySize);
                
                // ========== 综合计算最大扫描数量 ==========
                // 1. 首先使用 Header 指定的数量
                // 2. 如果 gpttool 方式计算的数量更大，使用更大的值（某些设备 Header 信息不准确）
                // 3. 不超过数据容量
                // 4. 合理上限 1024（小米等设备可能有很多分区）
                int maxEntries = headerEntries;
                
                // 如果 gpttool 计算的数量显著大于 Header 指定的数量，使用 gpttool 的值
                if (actualAvailableEntries > headerEntries && actualAvailableEntries <= 1024)
                {
                    maxEntries = actualAvailableEntries;
                    _logDetail(string.Format("[GPT] 使用 gpttool 计算的条目数 {0} (大于 Header 指定的 {1})", 
                        actualAvailableEntries, headerEntries));
                }
                
                // 确保不超过数据容量
                maxEntries = Math.Min(maxEntries, maxFromData);
                
                // 合理上限
                maxEntries = Math.Min(maxEntries, 1024);
                
                // 确保至少扫描 128 个条目（标准值）
                maxEntries = Math.Max(maxEntries, Math.Min(128, maxFromData));

                _logDetail(string.Format("[GPT] 分区条目: 偏移={0}, 大小={1}, Header数量={2}, gpttool={3}, 数据容量={4}, 最终扫描={5}", 
                    entryOffset, entrySize, headerEntries, actualAvailableEntries, maxFromData, maxEntries));

                int parsedCount = 0;
                int totalEmptyCount = 0;
                
                // ========== 两遍扫描策略 ==========
                // 第一遍: 扫描所有条目，找出有效分区
                var validEntries = new List<int>();
                for (int i = 0; i < maxEntries; i++)
                {
                    int offset = entryOffset + i * entrySize;
                    if (offset + 128 > data.Length) break;

                    // 检查分区类型 GUID 是否为空
                    bool isEmpty = true;
                    for (int j = 0; j < 16; j++)
                    {
                        if (data[offset + j] != 0)
                        {
                            isEmpty = false;
                            break;
                        }
                    }
                    
                    if (!isEmpty)
                    {
                        validEntries.Add(i);
                    }
                    else
                    {
                        totalEmptyCount++;
                    }
                }
                
                _logDetail(string.Format("[GPT] 第一遍扫描: 找到 {0} 个非空条目, {1} 个空条目", 
                    validEntries.Count, totalEmptyCount));
                
                // 第二遍: 解析有效的分区条目
                foreach (int i in validEntries)
                {
                    int offset = entryOffset + i * entrySize;
                    
                    // 解析分区条目
                    var partition = ParsePartitionEntry(data, offset, lun, sectorSize, i + 1);
                    if (partition != null && !string.IsNullOrWhiteSpace(partition.Name))
                    {
                        partitions.Add(partition);
                        parsedCount++;
                        
                        // 详细日志：记录每个解析到的分区
                        if (parsedCount <= 10 || parsedCount % 20 == 0)
                        {
                            _logDetail(string.Format("[GPT] #{0}: {1} @ LBA {2}-{3} ({4})", 
                                parsedCount, partition.Name, partition.StartSector, 
                                partition.StartSector + partition.NumSectors - 1, partition.FormattedSize));
                        }
                    }
                }
                
                // 如果没有找到分区，尝试备用策略：不依赖 Header 信息，直接扫描整个数据
                if (parsedCount == 0 && data.Length > entryOffset + 128)
                {
                    _logDetail("[GPT] 标准解析失败，尝试备用策略：暴力扫描分区条目");
                    
                    // 从 entryOffset 开始，每 128 字节检查一次
                    for (int offset = entryOffset; offset + 128 <= data.Length; offset += 128)
                    {
                        // 检查是否有有效的分区名称
                        if (HasValidPartitionEntry(data, offset))
                        {
                            var partition = ParsePartitionEntry(data, offset, lun, sectorSize, parsedCount + 1);
                            if (partition != null && !string.IsNullOrWhiteSpace(partition.Name))
                            {
                                // 检查是否重复
                                if (!partitions.Any(p => p.Name == partition.Name && p.StartSector == partition.StartSector))
                                {
                                    partitions.Add(partition);
                                    parsedCount++;
                                    _logDetail(string.Format("[GPT] 备用策略找到: {0} @ offset {1}", partition.Name, offset));
                                }
                            }
                        }
                        
                        // 防止无限循环
                        if (parsedCount > 256) break;
                    }
                }
                
                _logDetail(string.Format("[GPT] LUN{0} 解析完成: {1} 个有效分区", lun, parsedCount));
            }
            catch (Exception ex)
            {
                _log(string.Format("[GPT] 解析分区条目异常: {0}", ex.Message));
            }

            return partitions;
        }
        
        /// <summary>
        /// 检查是否有有效的分区条目
        /// 修复: 仅检查 PartitionTypeGUID 是否有效，不强制要求名称
        /// （某些底层分区如 fsc, modemst1 可能没有名称）
        /// </summary>
        private bool HasValidPartitionEntry(byte[] data, int offset)
        {
            if (offset + 128 > data.Length) return false;

            // 仅检查分区类型 GUID 是否为非零（关键修复）
            // UEFI 规范: 全零 GUID 表示未使用的条目
            bool hasTypeGuid = false;
            for (int i = 0; i < 16; i++)
            {
                if (data[offset + i] != 0)
                {
                    hasTypeGuid = true;
                    break;
                }
            }
            if (!hasTypeGuid) return false;
            
            // 检查 LBA 是否合理（起始 LBA 应该大于 0）
            long startLba = BitConverter.ToInt64(data, offset + 32);
            long endLba = BitConverter.ToInt64(data, offset + 40);
            if (startLba <= 0 || endLba <= 0 || endLba < startLba)
                return false;

            // 名称可以为空，不再强制检查
            // 但验证偏移处数据可读取
            try
            {
                // 尝试读取名称（不作为判断依据）
                Encoding.Unicode.GetString(data, offset + 56, 72);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析单个分区条目
        /// 修复: 允许空名称的分区（某些底层分区只有 GUID）
        /// </summary>
        private PartitionInfo ParsePartitionEntry(byte[] data, int offset, int lun, int sectorSize, int index)
        {
            try
            {
                // 检查类型 GUID 是否全零（表示空条目）
                bool hasTypeGuid = false;
                for (int i = 0; i < 16; i++)
                {
                    if (data[offset + i] != 0) { hasTypeGuid = true; break; }
                }
                if (!hasTypeGuid) return null;
                
                // 分区类型 GUID (0-16)
                string typeGuid = FormatGuid(data, offset);

                // 分区唯一 GUID (16-32)
                string uniqueGuid = FormatGuid(data, offset + 16);

                // 起始 LBA (32-40)
                long startLba = BitConverter.ToInt64(data, offset + 32);

                // 结束 LBA (40-48)
                long endLba = BitConverter.ToInt64(data, offset + 40);
                
                // 验证 LBA 有效性
                if (startLba <= 0 || endLba <= 0 || endLba < startLba)
                    return null;

                // 属性 (48-56)
                ulong attributes = BitConverter.ToUInt64(data, offset + 48);

                // 分区名称 UTF-16LE (56-128)
                string name = Encoding.Unicode.GetString(data, offset + 56, 72).TrimEnd('\0');

                // 允许空名称: 如果名称为空，生成基于 GUID 的默认名称
                if (string.IsNullOrWhiteSpace(name))
                {
                    // 使用类型 GUID 或 Unique GUID 的前 8 位作为名称
                    string guidPart = uniqueGuid.Length >= 8 ? uniqueGuid.Substring(0, 8) : typeGuid.Substring(0, 8);
                    name = $"unnamed_{guidPart}";
                    _logDetail($"[GPT] 分区 {index} 无名称，使用默认名称: {name}");
                }

                return new PartitionInfo
                {
                    Name = name,
                    Lun = lun,
                    StartSector = startLba,
                    NumSectors = endLba - startLba + 1,
                    SectorSize = sectorSize,
                    TypeGuid = typeGuid,
                    UniqueGuid = uniqueGuid,
                    Attributes = attributes,
                    EntryIndex = index,
                    GptEntriesStartSector = 2  // GPT 条目通常从 LBA 2 开始
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region A/B 槽位检测 (已迁移到 SlotDetector)

        /// <summary>
        /// 检测 A/B 槽位状态 (兼容方法，调用 SlotDetector)
        /// </summary>
        private SlotInfo DetectSlot(List<PartitionInfo> partitions)
        {
            return _slotDetector.Detect(partitions).ToLegacy();
        }

        #endregion

        #region CRC32 校验

        /// <summary>
        /// 验证 CRC32
        /// </summary>
        private bool VerifyCrc32(byte[] data, int headerOffset, GptHeaderInfo header)
        {
            try
            {
                // 计算 Header CRC (需要先将 CRC 字段置零)
                byte[] headerData = new byte[header.HeaderSize];
                Array.Copy(data, headerOffset, headerData, 0, (int)header.HeaderSize);
                
                // 将 CRC 字段置零
                headerData[16] = 0;
                headerData[17] = 0;
                headerData[18] = 0;
                headerData[19] = 0;

                uint calculatedCrc = CalculateCrc32(headerData);
                
                if (calculatedCrc != header.HeaderCrc32)
                {
                    _logDetail(string.Format("[GPT] Header CRC 不匹配: 计算={0:X8}, 存储={1:X8}",
                        calculatedCrc, header.HeaderCrc32));
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// CRC32 计算 (使用静态表)
        /// </summary>
        private uint CalculateCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            
            foreach (byte b in data)
            {
                byte index = (byte)((crc ^ b) & 0xFF);
                crc = (crc >> 8) ^ CRC32_TABLE[index];
            }
            
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>
        /// 静态初始化 CRC32 表 (程序启动时只生成一次)
        /// </summary>
        private static uint[] GenerateStaticCrc32Table()
        {
            uint[] table = new uint[256];
            const uint polynomial = 0xEDB88320;
            
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 8; j > 0; j--)
                {
                    if ((crc & 1) == 1)
                        crc = (crc >> 1) ^ polynomial;
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }
            
            return table;
        }

        #endregion

        #region GUID 格式化

        /// <summary>
        /// 格式化 GUID (混合端序)
        /// </summary>
        private string FormatGuid(byte[] data, int offset)
        {
            // GPT GUID 格式: 前3部分小端序，后2部分大端序
            // 格式: XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX
            var sb = new StringBuilder();
            
            // 第1部分 (4字节, 小端序)
            for (int i = 3; i >= 0; i--)
                sb.AppendFormat("{0:X2}", data[offset + i]);
            sb.Append("-");
            
            // 第2部分 (2字节, 小端序)
            for (int i = 5; i >= 4; i--)
                sb.AppendFormat("{0:X2}", data[offset + i]);
            sb.Append("-");
            
            // 第3部分 (2字节, 小端序)
            for (int i = 7; i >= 6; i--)
                sb.AppendFormat("{0:X2}", data[offset + i]);
            sb.Append("-");
            
            // 第4部分 (2字节, 大端序)
            for (int i = 8; i <= 9; i++)
                sb.AppendFormat("{0:X2}", data[offset + i]);
            sb.Append("-");
            
            // 第5部分 (6字节, 大端序)
            for (int i = 10; i <= 15; i++)
                sb.AppendFormat("{0:X2}", data[offset + i]);
            
            return sb.ToString();
        }

        #endregion

        #region XML 生成

        /// <summary>
        /// 生成合并后的 rawprogram.xml 内容 (包含所有 LUN)
        /// </summary>
        public string GenerateRawprogramXml(List<PartitionInfo> partitions, int sectorSize)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" ?>");
            sb.AppendLine("<data>");

            foreach (var p in partitions.OrderBy(x => x.Lun).ThenBy(x => x.StartSector))
            {
                long sizeKb = (p.NumSectors * (long)sectorSize) / 1024;
                long startByte = p.StartSector * (long)sectorSize;

                string filename = p.Name;
                if (!filename.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && 
                    !filename.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    filename += ".img";
                }

                sb.AppendFormat("  <program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                    "file_sector_offset=\"0\" " +
                    "filename=\"{1}\" " +
                    "label=\"{2}\" " +
                    "num_partition_sectors=\"{3}\" " +
                    "partofsingleimage=\"false\" " +
                    "physical_partition_number=\"{4}\" " +
                    "readbackverify=\"false\" " +
                    "size_in_KB=\"{5}\" " +
                    "sparse=\"false\" " +
                    "start_byte_hex=\"0x{6:X}\" " +
                    "start_sector=\"{7}\" />\r\n",
                    sectorSize, filename, p.Name, p.NumSectors, p.Lun, sizeKb, startByte, p.StartSector);
            }

            sb.AppendLine("</data>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成 rawprogram.xml 内容 (分 LUN 生成)
        /// </summary>
        public Dictionary<int, string> GenerateRawprogramXmls(List<PartitionInfo> partitions, int sectorSize)
        {
            var results = new Dictionary<int, string>();
            var luns = partitions.Select(p => p.Lun).Distinct().OrderBy(l => l);

            foreach (var lun in luns)
            {
                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" ?>");
                sb.AppendLine("<data>");

                var lunPartitions = partitions.Where(p => p.Lun == lun).OrderBy(p => p.StartSector);
                foreach (var p in lunPartitions)
                {
                    long sizeKb = (p.NumSectors * (long)sectorSize) / 1024;
                    long startByte = p.StartSector * (long)sectorSize;

                    // 规范化文件名，如果有 .img 后缀则保留，没有则加上
                    string filename = p.Name;
                    if (!filename.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && 
                        !filename.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        filename += ".img";
                    }

                    sb.AppendFormat("  <program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                        "file_sector_offset=\"0\" " +
                        "filename=\"{1}\" " +
                        "label=\"{2}\" " +
                        "num_partition_sectors=\"{3}\" " +
                        "partofsingleimage=\"false\" " +
                        "physical_partition_number=\"{4}\" " +
                        "readbackverify=\"false\" " +
                        "size_in_KB=\"{5}\" " +
                        "sparse=\"false\" " +
                        "start_byte_hex=\"0x{6:X}\" " +
                        "start_sector=\"{7}\" />\r\n",
                        sectorSize, filename, p.Name, p.NumSectors, p.Lun, sizeKb, startByte, p.StartSector);
                }

                sb.AppendLine("</data>");
                results[lun] = sb.ToString();
            }

            return results;
        }

        /// <summary>
        /// 生成基础 patch.xml 内容 (分 LUN 生成)
        /// </summary>
        public Dictionary<int, string> GeneratePatchXmls(List<PartitionInfo> partitions, int sectorSize)
        {
            var results = new Dictionary<int, string>();
            var luns = partitions.Select(p => p.Lun).Distinct().OrderBy(l => l);

            foreach (var lun in luns)
            {
                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" ?>");
                sb.AppendLine("<data>");

                // 添加标准的 GPT 修复补丁模板 (实际值需要工具写入时动态计算，这里提供占位)
                sb.AppendLine(string.Format("  <!-- GPT Header CRC Patches for LUN {0} -->", lun));
                sb.AppendFormat("  <patch SECTOR_SIZE_IN_BYTES=\"{0}\" byte_offset=\"16\" filename=\"DISK\" physical_partition_number=\"{1}\" size_in_bytes=\"4\" start_sector=\"1\" value=\"0\" />\r\n", sectorSize, lun);
                sb.AppendFormat("  <patch SECTOR_SIZE_IN_BYTES=\"{0}\" byte_offset=\"88\" filename=\"DISK\" physical_partition_number=\"{1}\" size_in_bytes=\"4\" start_sector=\"1\" value=\"0\" />\r\n", sectorSize, lun);

                sb.AppendLine("</data>");
                results[lun] = sb.ToString();
            }

            return results;
        }

        /// <summary>
        /// 生成 partition.xml 内容
        /// </summary>
        public string GeneratePartitionXml(List<PartitionInfo> partitions, int sectorSize)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" ?>");
            sb.AppendLine("<partitions>");

            foreach (var p in partitions.OrderBy(x => x.Lun).ThenBy(x => x.StartSector))
            {
                long sizeKb = (p.NumSectors * sectorSize) / 1024;

                sb.AppendFormat("  <partition label=\"{0}\" " +
                    "size_in_kb=\"{1}\" " +
                    "type=\"{2}\" " +
                    "bootable=\"false\" " +
                    "readonly=\"true\" " +
                    "filename=\"{0}.img\" />\r\n",
                    p.Name, sizeKb, p.TypeGuid ?? "00000000-0000-0000-0000-000000000000");
            }

            sb.AppendLine("</partitions>");
            return sb.ToString();
        }

        #endregion
    }
}
