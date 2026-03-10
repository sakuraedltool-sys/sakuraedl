// ============================================================================
// SakuraEDL - Fastboot Device Info | Fastboot 设备信息
// ============================================================================
// [ZH] 设备信息模型 - Fastboot 设备属性和状态
// [EN] Device Info Model - Fastboot device properties and status
// [JA] デバイス情報モデル - Fastbootデバイスのプロパティと状態
// [KO] 기기 정보 모델 - Fastboot 기기 속성 및 상태
// [RU] Модель информации устройства - Свойства и состояние Fastboot
// [ES] Modelo de info del dispositivo - Propiedades y estado Fastboot
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SakuraEDL.Fastboot.Models
{
    /// <summary>
    /// Fastboot 设备信息
    /// </summary>
    public class FastbootDeviceInfo
    {
        /// <summary>
        /// 设备序列号
        /// </summary>
        public string Serial { get; set; }

        /// <summary>
        /// 设备状态（fastboot/fastbootd）
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// 产品名称
        /// </summary>
        public string Product { get; set; }

        /// <summary>
        /// 是否启用安全启动
        /// </summary>
        public bool SecureBoot { get; set; }

        /// <summary>
        /// 当前槽位（A/B 分区）
        /// </summary>
        public string CurrentSlot { get; set; }

        /// <summary>
        /// 是否处于 fastbootd 用户空间模式
        /// </summary>
        public bool IsFastbootd { get; set; }

        /// <summary>
        /// 最大下载大小
        /// </summary>
        public long MaxDownloadSize { get; set; } = -1;

        /// <summary>
        /// 快照更新状态
        /// </summary>
        public string SnapshotUpdateStatus { get; set; }

        /// <summary>
        /// 解锁状态
        /// </summary>
        public bool? Unlocked { get; set; }

        /// <summary>
        /// Bootloader 版本
        /// </summary>
        public string BootloaderVersion { get; set; }

        /// <summary>
        /// Baseband 版本
        /// </summary>
        public string BasebandVersion { get; set; }

        /// <summary>
        /// 硬件版本
        /// </summary>
        public string HardwareVersion { get; set; }

        /// <summary>
        /// 变体 (variant)
        /// </summary>
        public string Variant { get; set; }

        /// <summary>
        /// 分区大小字典
        /// </summary>
        public Dictionary<string, long> PartitionSizes { get; private set; } = new Dictionary<string, long>();

        /// <summary>
        /// 分区是否为逻辑分区字典
        /// </summary>
        public Dictionary<string, bool?> PartitionIsLogical { get; private set; } = new Dictionary<string, bool?>();

        /// <summary>
        /// 所有原始变量
        /// </summary>
        public Dictionary<string, string> RawVariables { get; private set; } = new Dictionary<string, string>();

        /// <summary>
        /// 是否支持 A/B 分区
        /// </summary>
        public bool HasABPartition => !string.IsNullOrEmpty(CurrentSlot);

        /// <summary>
        /// 获取指定变量的值
        /// </summary>
        /// <param name="key">变量名（不区分大小写）</param>
        /// <returns>变量值，如果不存在返回 null</returns>
        public string GetVariable(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            
            string lowKey = key.ToLowerInvariant();
            if (RawVariables.TryGetValue(lowKey, out string value))
                return value;
            
            return null;
        }

        /// <summary>
        /// 从 getvar all 输出解析设备信息
        /// </summary>
        public static FastbootDeviceInfo ParseFromGetvarAll(string rawData)
        {
            var info = new FastbootDeviceInfo();

            if (string.IsNullOrEmpty(rawData))
                return info;

            foreach (string line in rawData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // fastboot 输出格式: (bootloader) key: value 或 key: value
                string processedLine = line.Trim();
                
                // 移除 (bootloader) 前缀
                if (processedLine.StartsWith("(bootloader)"))
                {
                    processedLine = processedLine.Substring(12).Trim();
                }

                // 解析 key: value 格式
                int colonIndex = processedLine.IndexOf(':');
                if (colonIndex <= 0) continue;

                string key = processedLine.Substring(0, colonIndex).Trim().ToLowerInvariant();
                string value = processedLine.Substring(colonIndex + 1).Trim();

                // 保存原始变量
                info.RawVariables[key] = value;

                // 解析分区大小: partition-size:boot_a: 0x4000000
                if (key.StartsWith("partition-size:"))
                {
                    string partName = key.Substring("partition-size:".Length);
                    if (TryParseHexOrDecimal(value, out long size))
                    {
                        info.PartitionSizes[partName] = size;
                    }
                    continue;
                }

                // 解析逻辑分区: is-logical:system_a: yes
                if (key.StartsWith("is-logical:"))
                {
                    string partName = key.Substring("is-logical:".Length);
                    info.PartitionIsLogical[partName] = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                // 解析其他常用变量
                switch (key)
                {
                    case "product":
                        info.Product = value;
                        break;
                    case "secure":
                        info.SecureBoot = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "current-slot":
                        info.CurrentSlot = value;
                        break;
                    case "is-userspace":
                        info.IsFastbootd = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "max-download-size":
                        if (TryParseHexOrDecimal(value, out long maxSize))
                            info.MaxDownloadSize = maxSize;
                        break;
                    case "snapshot-update-status":
                        info.SnapshotUpdateStatus = value;
                        break;
                    case "unlocked":
                        info.Unlocked = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "version-bootloader":
                        info.BootloaderVersion = value;
                        break;
                    case "version-baseband":
                        info.BasebandVersion = value;
                        break;
                    case "hw-revision":
                        info.HardwareVersion = value;
                        break;
                    case "variant":
                        info.Variant = value;
                        break;
                }
            }

            return info;
        }

        /// <summary>
        /// 尝试解析十六进制或十进制数字
        /// </summary>
        private static bool TryParseHexOrDecimal(string value, out long result)
        {
            result = 0;
            if (string.IsNullOrEmpty(value)) return false;

            value = value.Trim();
            
            try
            {
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    result = Convert.ToInt64(value.Substring(2), 16);
                    return true;
                }
                else
                {
                    return long.TryParse(value, out result);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取分区列表
        /// </summary>
        public List<FastbootPartitionInfo> GetPartitions()
        {
            var partitions = new List<FastbootPartitionInfo>();

            foreach (var kv in PartitionSizes.OrderBy(x => x.Key))
            {
                bool? isLogical = null;
                PartitionIsLogical.TryGetValue(kv.Key, out isLogical);

                partitions.Add(new FastbootPartitionInfo
                {
                    Name = kv.Key,
                    Size = kv.Value,
                    IsLogical = isLogical
                });
            }

            return partitions;
        }
    }

    /// <summary>
    /// Fastboot 分区信息
    /// </summary>
    public class FastbootPartitionInfo
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public bool? IsLogical { get; set; }

        /// <summary>
        /// 格式化大小显示
        /// </summary>
        public string SizeFormatted
        {
            get
            {
                if (Size < 0) return "未知";
                if (Size >= 1024L * 1024 * 1024)
                    return $"{Size / (1024.0 * 1024 * 1024):F2} GB";
                if (Size >= 1024 * 1024)
                    return $"{Size / (1024.0 * 1024):F2} MB";
                if (Size >= 1024)
                    return $"{Size / 1024.0:F2} KB";
                return $"{Size} B";
            }
        }

        /// <summary>
        /// 是否为逻辑分区的显示文本
        /// </summary>
        public string IsLogicalText
        {
            get
            {
                if (IsLogical == null) return "-";
                return IsLogical.Value ? "是" : "否";
            }
        }
    }

    /// <summary>
    /// Fastboot 设备列表项
    /// </summary>
    public class FastbootDeviceListItem
    {
        public string Serial { get; set; }
        public string Status { get; set; }

        public override string ToString()
        {
            return $"{Serial} ({Status})";
        }
    }
}
