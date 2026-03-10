// ============================================================================
// SakuraEDL - Slot Detector | A/B 槽位检测器
// ============================================================================
// [ZH] 槽位检测 - 检测设备当前活动的 A/B 槽位
// [EN] Slot Detector - Detect current active A/B partition slot
// [JA] スロット検出 - 現在アクティブなA/Bパーティションスロットを検出
// [KO] 슬롯 탐지기 - 현재 활성 A/B 파티션 슬롯 감지
// [RU] Детектор слотов - Обнаружение активного A/B слота раздела
// [ES] Detector de slot - Detectar slot de partición A/B activo
// ============================================================================
// Reference: Android bootctrl HAL, gpttool
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SakuraEDL.Qualcomm.Models;

namespace SakuraEDL.Qualcomm.Common
{
    /// <summary>
    /// A/B 槽位属性位定义 (GPT Partition Attributes)
    /// 参考: Android bootctrl HAL / AOSP
    /// </summary>
    public static class SlotAttributes
    {
        // GPT 属性字段中 A/B 相关位的位置
        // Attributes 是 64 位，A/B 信息存储在 Byte 6 (bit 48-55)
        
        /// <summary>A/B 属性字节偏移 (从 bit 0 开始计算的字节偏移)</summary>
        public const int AB_FLAG_BYTE_OFFSET = 6;
        
        /// <summary>Priority 掩码 (bit 0-1 in byte 6 = bit 48-49)</summary>
        public const byte PRIORITY_MASK = 0x03;
        
        /// <summary>Active 标志 (bit 2 in byte 6 = bit 50)</summary>
        public const byte ACTIVE_FLAG = 0x04;
        
        /// <summary>Successful 标志 (bit 3 in byte 6 = bit 51)</summary>
        public const byte SUCCESSFUL_FLAG = 0x08;
        
        /// <summary>Unbootable 标志 (bit 4 in byte 6 = bit 52)</summary>
        public const byte UNBOOTABLE_FLAG = 0x10;
        
        /// <summary>
        /// 从 64 位属性中提取 A/B 标志字节
        /// </summary>
        public static byte GetAbFlagByte(ulong attributes)
        {
            return (byte)((attributes >> (AB_FLAG_BYTE_OFFSET * 8)) & 0xFF);
        }
        
        /// <summary>获取优先级 (0-3, 3 最高)</summary>
        public static int GetPriority(ulong attributes)
        {
            return GetAbFlagByte(attributes) & PRIORITY_MASK;
        }
        
        /// <summary>检查是否激活</summary>
        public static bool IsActive(ulong attributes)
        {
            return (GetAbFlagByte(attributes) & ACTIVE_FLAG) != 0;
        }
        
        /// <summary>检查是否启动成功</summary>
        public static bool IsSuccessful(ulong attributes)
        {
            return (GetAbFlagByte(attributes) & SUCCESSFUL_FLAG) != 0;
        }
        
        /// <summary>检查是否不可启动</summary>
        public static bool IsUnbootable(ulong attributes)
        {
            return (GetAbFlagByte(attributes) & UNBOOTABLE_FLAG) != 0;
        }
    }

    /// <summary>
    /// 槽位状态枚举
    /// </summary>
    public enum SlotState
    {
        /// <summary>不存在 A/B 分区</summary>
        NonExistent,
        
        /// <summary>槽位 A 激活</summary>
        SlotA,
        
        /// <summary>槽位 B 激活</summary>
        SlotB,
        
        /// <summary>无法确定 (两个槽位状态相同)</summary>
        Unknown,
        
        /// <summary>未定义 (有 A/B 分区但无激活标志)</summary>
        Undefined
    }

    /// <summary>
    /// 槽位信息 (重构版)
    /// </summary>
    public class SlotInfoV2
    {
        /// <summary>槽位状态</summary>
        public SlotState State { get; set; } = SlotState.NonExistent;
        
        /// <summary>当前槽位 ("a", "b", "unknown", "undefined", "nonexistent")</summary>
        public string CurrentSlot { get; set; } = "nonexistent";
        
        /// <summary>另一个槽位</summary>
        public string OtherSlot { get; set; } = "nonexistent";
        
        /// <summary>是否有 A/B 分区</summary>
        public bool HasAbPartitions { get; set; }
        
        /// <summary>槽位 A 激活的分区数量</summary>
        public int SlotAActiveCount { get; set; }
        
        /// <summary>槽位 B 激活的分区数量</summary>
        public int SlotBActiveCount { get; set; }
        
        /// <summary>槽位 A 的平均优先级</summary>
        public double SlotAPriority { get; set; }
        
        /// <summary>槽位 B 的平均优先级</summary>
        public double SlotBPriority { get; set; }
        
        /// <summary>检测方法描述</summary>
        public string DetectionMethod { get; set; } = "";
        
        /// <summary>
        /// 转换为旧版 SlotInfo (兼容性)
        /// </summary>
        public SlotInfo ToLegacy()
        {
            return new SlotInfo
            {
                CurrentSlot = CurrentSlot,
                OtherSlot = OtherSlot,
                HasAbPartitions = HasAbPartitions
            };
        }
    }

    /// <summary>
    /// A/B 槽位检测器
    /// </summary>
    public class SlotDetector
    {
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;
        
        // 关键分区名称 (用于判断槽位状态)
        private static readonly string[] KeyPartitions = 
        { 
            "boot", "system", "vendor", "abl", "xbl", "dtbo", 
            "vbmeta", "product", "odm", "system_ext"
        };
        
        // 排除的分区 (不参与槽位判断)
        private static readonly string[] ExcludedPartitions = 
        { 
            "vendor_boot", "init_boot"  // 这些可能有不同的激活状态
        };

        public SlotDetector(Action<string> log = null, Action<string> logDetail = null)
        {
            _log = log ?? (s => { });
            _logDetail = logDetail ?? _log;
        }

        /// <summary>
        /// 检测 A/B 槽位状态
        /// </summary>
        /// <param name="partitions">分区列表</param>
        /// <returns>槽位信息</returns>
        public SlotInfoV2 Detect(List<PartitionInfo> partitions)
        {
            var result = new SlotInfoV2();
            
            if (partitions == null || partitions.Count == 0)
                return result;
            
            // 1. 查找 A/B 分区
            var abPartitions = partitions
                .Where(p => p.Name.EndsWith("_a") || p.Name.EndsWith("_b"))
                .ToList();
            
            if (abPartitions.Count == 0)
            {
                _logDetail("[Slot] 未检测到 A/B 分区");
                return result;
            }
            
            result.HasAbPartitions = true;
            _logDetail($"[Slot] 检测到 {abPartitions.Count} 个 A/B 分区");
            
            // 2. 筛选关键分区用于判断
            var keyAbPartitions = FilterKeyPartitions(abPartitions);
            
            // 如果没有关键分区，使用所有 A/B 分区
            if (keyAbPartitions.Count == 0)
            {
                keyAbPartitions = abPartitions
                    .Where(p => !ExcludedPartitions.Any(ex => 
                        p.Name.StartsWith(ex, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            
            _logDetail($"[Slot] 使用 {keyAbPartitions.Count} 个分区进行槽位判断");
            
            // 3. 统计各槽位状态
            var slotAPartitions = keyAbPartitions.Where(p => p.Name.EndsWith("_a")).ToList();
            var slotBPartitions = keyAbPartitions.Where(p => p.Name.EndsWith("_b")).ToList();
            
            // 统计激活状态
            result.SlotAActiveCount = slotAPartitions.Count(p => SlotAttributes.IsActive(p.Attributes));
            result.SlotBActiveCount = slotBPartitions.Count(p => SlotAttributes.IsActive(p.Attributes));
            
            // 统计优先级
            if (slotAPartitions.Count > 0)
                result.SlotAPriority = slotAPartitions.Average(p => SlotAttributes.GetPriority(p.Attributes));
            if (slotBPartitions.Count > 0)
                result.SlotBPriority = slotBPartitions.Average(p => SlotAttributes.GetPriority(p.Attributes));
            
            _logDetail($"[Slot] A激活={result.SlotAActiveCount}, B激活={result.SlotBActiveCount}");
            _logDetail($"[Slot] A优先级={result.SlotAPriority:F2}, B优先级={result.SlotBPriority:F2}");
            
            // 4. 判断当前槽位 (多重策略)
            DetermineCurrentSlot(result, slotAPartitions, slotBPartitions);
            
            // 5. 输出详细日志
            LogPartitionDetails(keyAbPartitions);
            
            return result;
        }

        /// <summary>
        /// 筛选关键分区
        /// </summary>
        private List<PartitionInfo> FilterKeyPartitions(List<PartitionInfo> abPartitions)
        {
            return abPartitions.Where(p =>
            {
                string baseName = GetBaseName(p.Name);
                return KeyPartitions.Contains(baseName.ToLower());
            }).ToList();
        }

        /// <summary>
        /// 获取分区基础名称 (去除 _a/_b 后缀)
        /// </summary>
        private string GetBaseName(string name)
        {
            if (name.EndsWith("_a") || name.EndsWith("_b"))
                return name.Substring(0, name.Length - 2);
            return name;
        }

        /// <summary>
        /// 判断当前槽位 (多重策略)
        /// </summary>
        private void DetermineCurrentSlot(SlotInfoV2 result, 
            List<PartitionInfo> slotAPartitions, 
            List<PartitionInfo> slotBPartitions)
        {
            // 策略1: 比较激活数量
            if (result.SlotAActiveCount != result.SlotBActiveCount)
            {
                if (result.SlotAActiveCount > result.SlotBActiveCount)
                {
                    SetSlot(result, SlotState.SlotA, "Active 标志");
                }
                else
                {
                    SetSlot(result, SlotState.SlotB, "Active 标志");
                }
                return;
            }
            
            // 策略2: 比较优先级 (当激活数量相同时)
            if (Math.Abs(result.SlotAPriority - result.SlotBPriority) > 0.1)
            {
                if (result.SlotAPriority > result.SlotBPriority)
                {
                    SetSlot(result, SlotState.SlotA, "Priority 优先级");
                }
                else
                {
                    SetSlot(result, SlotState.SlotB, "Priority 优先级");
                }
                return;
            }
            
            // 策略3: 比较 Successful 标志
            int slotASuccessful = slotAPartitions.Count(p => SlotAttributes.IsSuccessful(p.Attributes));
            int slotBSuccessful = slotBPartitions.Count(p => SlotAttributes.IsSuccessful(p.Attributes));
            
            _logDetail($"[Slot] A成功={slotASuccessful}, B成功={slotBSuccessful}");
            
            if (slotASuccessful != slotBSuccessful)
            {
                if (slotASuccessful > slotBSuccessful)
                {
                    SetSlot(result, SlotState.SlotA, "Successful 标志");
                }
                else
                {
                    SetSlot(result, SlotState.SlotB, "Successful 标志");
                }
                return;
            }
            
            // 策略4: 比较 Unbootable 标志 (反向逻辑)
            int slotAUnbootable = slotAPartitions.Count(p => SlotAttributes.IsUnbootable(p.Attributes));
            int slotBUnbootable = slotBPartitions.Count(p => SlotAttributes.IsUnbootable(p.Attributes));
            
            if (slotAUnbootable != slotBUnbootable)
            {
                // 不可启动的少的槽位更可能是当前槽位
                if (slotAUnbootable < slotBUnbootable)
                {
                    SetSlot(result, SlotState.SlotA, "Unbootable 标志 (反向)");
                }
                else
                {
                    SetSlot(result, SlotState.SlotB, "Unbootable 标志 (反向)");
                }
                return;
            }
            
            // 无法确定
            if (result.SlotAActiveCount > 0 && result.SlotBActiveCount > 0)
            {
                // 两个槽位都有激活标志，状态相同
                result.State = SlotState.Unknown;
                result.CurrentSlot = "unknown";
                result.OtherSlot = "unknown";
                result.DetectionMethod = "无法区分 (两槽位状态相同)";
            }
            else if (result.SlotAActiveCount == 0 && result.SlotBActiveCount == 0)
            {
                // 没有激活标志
                result.State = SlotState.Undefined;
                result.CurrentSlot = "undefined";
                result.OtherSlot = "undefined";
                result.DetectionMethod = "未定义 (无激活标志)";
            }
            else
            {
                result.State = SlotState.Unknown;
                result.CurrentSlot = "unknown";
                result.OtherSlot = "unknown";
                result.DetectionMethod = "无法确定";
            }
            
            // 显示详细的槽位信息
            LogUnknownSlotSummary(result);
        }

        /// <summary>
        /// 设置槽位结果
        /// </summary>
        private void SetSlot(SlotInfoV2 result, SlotState state, string method)
        {
            result.State = state;
            result.DetectionMethod = method;
            
            if (state == SlotState.SlotA)
            {
                result.CurrentSlot = "a";
                result.OtherSlot = "b";
            }
            else if (state == SlotState.SlotB)
            {
                result.CurrentSlot = "b";
                result.OtherSlot = "a";
            }
            
            // 显示详细的槽位信息
            LogSlotSummary(result);
        }
        
        /// <summary>
        /// 输出槽位摘要日志
        /// </summary>
        private void LogSlotSummary(SlotInfoV2 result)
        {
            string slotChar = result.CurrentSlot.ToUpper();
            string otherChar = result.OtherSlot.ToUpper();
            
            // 确定当前槽位和备用槽位的统计数据
            int currentActive, otherActive;
            double currentPri, otherPri;
            
            if (result.State == SlotState.SlotA)
            {
                currentActive = result.SlotAActiveCount;
                otherActive = result.SlotBActiveCount;
                currentPri = result.SlotAPriority;
                otherPri = result.SlotBPriority;
            }
            else
            {
                currentActive = result.SlotBActiveCount;
                otherActive = result.SlotAActiveCount;
                currentPri = result.SlotBPriority;
                otherPri = result.SlotAPriority;
            }
            
            // 主日志 - 显示在UI
            _log($"[Slot] 当前槽位: {slotChar} (Active={currentActive}, Priority={currentPri:F0})");
            _log($"[Slot] 备用槽位: {otherChar} (Active={otherActive}, Priority={otherPri:F0})");
            _log($"[Slot] 检测方法: {result.DetectionMethod}");
        }
        
        /// <summary>
        /// 输出未知槽位摘要日志
        /// </summary>
        private void LogUnknownSlotSummary(SlotInfoV2 result)
        {
            _log($"[Slot] 槽位状态: {result.CurrentSlot.ToUpper()}");
            _log($"[Slot] Slot A: Active={result.SlotAActiveCount}, Priority={result.SlotAPriority:F0}");
            _log($"[Slot] Slot B: Active={result.SlotBActiveCount}, Priority={result.SlotBPriority:F0}");
            _log($"[Slot] 原因: {result.DetectionMethod}");
        }

        /// <summary>
        /// 输出分区详细日志
        /// </summary>
        private void LogPartitionDetails(List<PartitionInfo> partitions)
        {
            // 分组显示 A 和 B 槽位
            var slotA = partitions.Where(p => p.Name.EndsWith("_a")).Take(6).ToList();
            var slotB = partitions.Where(p => p.Name.EndsWith("_b")).Take(6).ToList();
            
            _logDetail("[Slot] Slot A 分区:");
            foreach (var p in slotA)
            {
                LogPartitionStatus(p);
            }
            
            _logDetail("[Slot] Slot B 分区:");
            foreach (var p in slotB)
            {
                LogPartitionStatus(p);
            }
        }
        
        /// <summary>
        /// 输出单个分区状态
        /// </summary>
        private void LogPartitionStatus(PartitionInfo p)
        {
            int priority = SlotAttributes.GetPriority(p.Attributes);
            bool active = SlotAttributes.IsActive(p.Attributes);
            bool successful = SlotAttributes.IsSuccessful(p.Attributes);
            bool unbootable = SlotAttributes.IsUnbootable(p.Attributes);
            
            _logDetail($"[Slot]   {p.Name,-20} Pri={priority} Act={active} Suc={successful} Unb={unbootable}");
        }

        /// <summary>
        /// 快速检测是否有 A/B 分区 (不进行完整分析)
        /// </summary>
        public static bool HasAbPartitions(List<PartitionInfo> partitions)
        {
            return partitions?.Any(p => p.Name.EndsWith("_a") || p.Name.EndsWith("_b")) ?? false;
        }

        /// <summary>
        /// 获取指定槽位的分区名称
        /// </summary>
        public static string GetSlotPartitionName(string baseName, string slot)
        {
            if (string.IsNullOrEmpty(slot) || slot == "nonexistent")
                return baseName;
            return $"{baseName}_{slot}";
        }
    }
}
