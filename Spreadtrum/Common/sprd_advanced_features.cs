// ============================================================================
// SakuraEDL - Spreadtrum Advanced Features | 展讯高级功能
// ============================================================================
// [ZH] 展讯高级功能 - A/B 槽位、DM-Verity、Bootloader 解锁等
// [EN] Spreadtrum Advanced - A/B slot, DM-Verity, Bootloader unlock, etc.
// [JA] Spreadtrum高度な機能 - A/Bスロット、DM-Verity、ブートローダー解除
// [KO] Spreadtrum 고급 기능 - A/B 슬롯, DM-Verity, 부트로더 잠금 해제
// [RU] Расширенные функции Spreadtrum - A/B слот, DM-Verity, разблокировка
// [ES] Funciones avanzadas Spreadtrum - A/B slot, DM-Verity, desbloqueo
// ============================================================================
// Reference: iReverseSPRDClient
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace SakuraEDL.Spreadtrum.Common
{
    #region 枚举定义

    /// <summary>
    /// Bootloader 锁定状态命令
    /// </summary>
    public enum BootloaderCommand : ushort
    {
        /// <summary>解锁 Bootloader</summary>
        YCC_CMD_UNLOCK_BOOTLOADER = 0x19,
        
        /// <summary>锁定 Bootloader</summary>
        YCC_CMD_LOCK_BOOTLOADER = 0x1A,
        
        /// <summary>设置 Bootloader 成功响应</summary>
        YCC_REP_SET_BOOTLOADER_SUCCESS = 0xCC,
    }

    /// <summary>
    /// 重启到指定模式
    /// </summary>
    public enum ResetToMode
    {
        /// <summary>正常模式</summary>
        Normal,
        
        /// <summary>Recovery 模式</summary>
        Recovery,
        
        /// <summary>Fastboot 模式</summary>
        Fastboot,
        
        /// <summary>恢复出厂设置</summary>
        FactoryReset,
        
        /// <summary>擦除 FRP (仅擦除 frp/persistent)</summary>
        EraseFrp,
    }

    /// <summary>
    /// A/B 分区槽位
    /// </summary>
    public enum ActiveSlot
    {
        /// <summary>槽位 A</summary>
        SlotA,
        
        /// <summary>槽位 B</summary>
        SlotB,
    }

    /// <summary>
    /// 诊断模式切换方法
    /// </summary>
    public enum DiagSwitchMethod
    {
        /// <summary>通用模式 (发送 cali 一次，发送 dl_diag 两次)</summary>
        CommonMode,
        
        /// <summary>自定义一次性模式 (旧设备可能不支持)</summary>
        CustomOneTimeMode,
    }

    /// <summary>
    /// 目标诊断模式
    /// </summary>
    public enum DiagTargetMode : byte
    {
        /// <summary>校准诊断模式</summary>
        CaliDiagnostic = 0,
        
        /// <summary>下载诊断模式</summary>
        DlDiagnostic = 1,
    }

    /// <summary>
    /// 分区获取方式
    /// </summary>
    public enum PartitionGetMethod
    {
        /// <summary>从 GPT 表转换 (读取 user_partition)</summary>
        ConvertGptTable = 0,
        
        /// <summary>发送 BSL_CMD_READ_PARTITION 命令</summary>
        SendReadPartitionCommand = 1,
        
        /// <summary>遍历常见分区名</summary>
        TraverseCommonPartitions = 2,
    }

    #endregion

    #region 数据结构

    /// <summary>
    /// A/B 槽位 Payload
    /// </summary>
    public static class SlotPayloads
    {
        /// <summary>
        /// 槽位 A 的 misc 分区 payload
        /// </summary>
        public static readonly byte[] PayloadSlotA = new byte[]
        {
            0x5F, 0x61, 0x00, 0x00, 0x42, 0x43, 0x41, 0x42,
            0x01, 0x02, 0x00, 0x00, 0x6F, 0x00, 0x1E, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xE6, 0xBF, 0xEA, 0xC5
        };

        /// <summary>
        /// 槽位 B 的 misc 分区 payload
        /// </summary>
        public static readonly byte[] PayloadSlotB = new byte[]
        {
            0x5F, 0x62, 0x00, 0x00, 0x42, 0x43, 0x41, 0x42,
            0x01, 0x02, 0x00, 0x00, 0x1E, 0x00, 0x6F, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x9E, 0xE2, 0x10, 0x70
        };
        
        /// <summary>
        /// 获取指定槽位的 payload
        /// </summary>
        public static byte[] GetPayload(ActiveSlot slot)
        {
            return slot == ActiveSlot.SlotA ? PayloadSlotA : PayloadSlotB;
        }
    }

    /// <summary>
    /// misc 分区 Recovery 命令
    /// </summary>
    public static class MiscCommands
    {
        /// <summary>
        /// 生成 misc 分区数据用于重启到指定模式
        /// </summary>
        public static byte[] CreateMiscData(ResetToMode mode)
        {
            byte[] data = new byte[0x800];
            
            if (mode == ResetToMode.Normal || mode == ResetToMode.EraseFrp)
            {
                // 正常模式或仅擦除 FRP 不需要写入 misc
                return null;
            }

            // 写入 "boot-recovery" 标识
            byte[] bootRecovery = Encoding.ASCII.GetBytes("boot-recovery");
            Array.Copy(bootRecovery, 0, data, 0, bootRecovery.Length);

            // 在偏移 0x40 处写入命令
            byte[] command;
            switch (mode)
            {
                case ResetToMode.Recovery:
                    command = new byte[] { 0 };  // 空命令进入 recovery
                    break;
                case ResetToMode.Fastboot:
                    command = Encoding.ASCII.GetBytes("recovery\n--fastboot\n");
                    break;
                case ResetToMode.FactoryReset:
                    command = Encoding.ASCII.GetBytes("recovery\n--wipe_data\n");
                    break;
                default:
                    command = new byte[] { 0 };
                    break;
            }
            
            Array.Copy(command, 0, data, 0x40, command.Length);
            
            return data;
        }
    }

    /// <summary>
    /// 常见分区名列表
    /// </summary>
    public static class CommonPartitions
    {
        /// <summary>
        /// A/B 分区系统的常见分区名
        /// </summary>
        public static readonly string[] AbPartitions = new[]
        {
            "avbmeta_rs_a", "avbmeta_rs_b", "blackbox", "boot_a", "boot_b", "cache", "calinv",
            "chargelogo", "chargeremind", "common_rs1_a", "common_rs1_b", "common_rs2_a",
            "common_rs2_b", "cover", "devicelog", "dtb_a", "dtb_b", "dtbo_a", "dtbo_b", "frp",
            "fbootlogo", "gnssmodem_a", "gnssmodem_b", "gpsbd_a", "gpsbd_b", "gpsgl_a",
            "gpsgl_b", "hypervsior_a", "hypervsior_b", "itel", "init_boot_a", "init_boot_b",
            "journey_persist", "lkcharge", "logo", "l_agdsp_a", "l_agdsp_b", "l_cdsp_a",
            "l_cdsp_b", "l_deltanv_a", "l_deltanv_b", "l_modem_a", "l_modem_b", "l_gdsp_a",
            "l_ldsp_a", "metadata", "misc", "miscdata", "my_preload", "ocdt_a", "ocdt_b",
            "odmko_a", "odmko_b", "persist", "persistent", "pm_sys_a", "pm_sys_b", "prodnv",
            "sml_a", "sml_b", "socko_a", "socko_b", "splloader", "super", "sysdumpdb",
            "teecfg_a", "teecfg_b", "tkv_a", "tkv_b", "trustos_a", "trustos_b", "uboot_a",
            "uboot_b", "uboot_log", "vbmeta_a", "vbmeta_b", "vbmeta_odm_a", "vbmeta_odm_b",
            "vbmeta_product_a", "vbmeta_product_b", "vbmeta_system_a", "vbmeta_system_b",
            "vbmeta_system_ext_a", "vbmeta_system_ext_b", "vbmeta_vendor_a", "vbmeta_vendor_b",
            "vendor_boot_a", "vendor_boot_b", "wcnmodem_a", "wcnmodem_b"
        };

        /// <summary>
        /// 非 A/B 分区系统的常见分区名
        /// </summary>
        public static readonly string[] NonAbPartitions = new[]
        {
            "cache", "chargelogo", "chargeremind", "common", "cover", "devicelog", "dtb",
            "dtbo", "frp", "fbootlogo", "gpsbd", "gpsgl", "hypervsior", "itel", "journey_persist",
            "lkcharge", "logo", "l_deltanv", "l_fixnv1", "l_gdsp", "l_modem", "metadata",
            "misc", "miscdata", "my_preload", "odmko", "persist", "persistent", "pm_sys",
            "prodnv", "product", "recovery", "sml", "sml_bak", "socko", "splloader", "system",
            "teecfg", "teecfg_bak", "tranfs", "trustos", "trustos_bak", "uboot", "uboot_bak",
            "vbmeta", "vbmeta_bak", "vbmeta_system", "vbmeta_vendor", "vendor", "warninglogo",
            "wcnfdl", "wcnmodem", "w_deltanv", "w_fixnv1", "w_fixnv2", "w_gdsp", "w_modem",
            "w_runtimenv1", "w_runtimenv2"
        };

        /// <summary>
        /// 关键系统分区 (不建议直接覆写)
        /// </summary>
        public static readonly string[] CriticalPartitions = new[]
        {
            "splloader", "sml", "sml_bak", "trustos", "trustos_bak", "uboot", "uboot_bak",
            "teecfg", "teecfg_bak", "vbmeta", "vbmeta_bak"
        };

        /// <summary>
        /// NV 相关分区 (需要特殊处理)
        /// </summary>
        public static readonly string[] NvPartitions = new[]
        {
            "l_fixnv1", "l_fixnv2", "l_deltanv", "l_runtimenv1", "l_runtimenv2",
            "w_fixnv1", "w_fixnv2", "w_deltanv", "w_runtimenv1", "w_runtimenv2",
            "fixnv1", "fixnv2", "runtimenv", "runtimenv1", "runtimenv2",
            "prodnv", "calinv"
        };

        /// <summary>
        /// 检查是否为 A/B 系统
        /// </summary>
        public static bool IsAbSystem(IEnumerable<string> partitionNames)
        {
            foreach (var name in partitionNames)
            {
                if (name.EndsWith("_a") || name.EndsWith("_b"))
                    return true;
                if (name == "boot_a" || name == "boot_b")
                    return true;
            }
            return false;
        }
    }

    #endregion

    #region 诊断模式切换

    /// <summary>
    /// 诊断模式切换数据包
    /// </summary>
    public static class DiagPackets
    {
        /// <summary>
        /// 基础切换数据包
        /// </summary>
        public static byte[] CreateSwitchPacket(DiagSwitchMethod method, DiagTargetMode targetMode)
        {
            byte[] packet = new byte[] { 0x7e, 0, 0, 0, 0, 8, 0, 0xfe, 0, 0x7e };
            
            switch (method)
            {
                case DiagSwitchMethod.CommonMode:
                    packet[8] = 0x82;
                    break;
                case DiagSwitchMethod.CustomOneTimeMode:
                    packet[8] = (byte)(0x80 + (byte)targetMode);
                    break;
            }
            
            return packet;
        }

        /// <summary>
        /// Autoloader 切换数据包
        /// </summary>
        public static readonly byte[] AutoloaderPacket = new byte[]
        {
            0x7e, 0, 0, 0, 0, 0x20, 0, 0x68, 0, 0x41, 0x54, 0x2b, 0x53, 0x50, 0x52, 0x45,
            0x46, 0x3d, 0x22, 0x41, 0x55, 0x54, 0x4f, 0x44, 0x4c, 0x4f, 0x41, 0x44, 0x45,
            0x52, 0x22, 0xd, 0xa, 0x7e
        };

        /// <summary>
        /// 检查响应是否为下载模式
        /// </summary>
        public static bool IsInDownloadMode(byte[] response)
        {
            if (response == null || response.Length < 6)
                return false;
                
            // 检查响应类型
            byte responseType = response[2];
            return responseType == 0x81 ||  // BSL_REP_VER
                   responseType == 0x8B ||  // BSL_REP_VERIFY_ERROR
                   responseType == 0xFE;    // BSL_REP_UNSUPPORTED_COMMAND
        }

        /// <summary>
        /// 检查是否为 SPRD4 (Autoloader) 模式
        /// </summary>
        public static bool IsSprd4Mode(byte[] response)
        {
            if (response == null || response.Length < 8)
                return false;

            if (response[2] != 0x81) // BSL_REP_VER
                return false;

            // 检查版本字符串是否包含 "autod"
            try
            {
                string versionStr = Encoding.ASCII.GetString(response, 5, response.Length - 8);
                return versionStr.ToLower().Contains("autod");
            }
            catch
            {
                return false;
            }
        }
    }

    #endregion

    #region DM-Verity 控制

    /// <summary>
    /// DM-Verity 控制
    /// </summary>
    public static class DmVerityControl
    {
        /// <summary>
        /// vbmeta 分区中 DM-Verity 标志位的偏移
        /// </summary>
        public const int VbmetaFlagOffset = 0x7B;

        /// <summary>
        /// DM-Verity 启用值
        /// </summary>
        public static readonly byte[] EnableValue = new byte[] { 0 };

        /// <summary>
        /// DM-Verity 禁用值
        /// </summary>
        public static readonly byte[] DisableValue = new byte[] { 1 };

        /// <summary>
        /// 获取 DM-Verity 设置数据
        /// </summary>
        public static byte[] GetVerityData(bool enable)
        {
            return enable ? EnableValue : DisableValue;
        }
    }

    #endregion

    #region 绕过签名写入

    /// <summary>
    /// 绕过签名写入辅助类
    /// </summary>
    public static class SkipVerifyHelper
    {
        /// <summary>
        /// 临时分区名 (用于绕过签名验证)
        /// </summary>
        public const string SkipVerifyPartitionName = "skip_verify";

        /// <summary>
        /// 不能使用 skip_verify 的分区
        /// </summary>
        public static readonly string[] ExcludedPartitions = new[]
        {
            "splloader", "ubipac", "sml"
        };

        /// <summary>
        /// 检查分区是否可以使用 skip_verify
        /// </summary>
        public static bool CanUseSkipVerify(string partitionName)
        {
            if (string.IsNullOrEmpty(partitionName))
                return false;

            string lowerName = partitionName.ToLower();
            foreach (var excluded in ExcludedPartitions)
            {
                if (lowerName == excluded)
                    return false;
            }
            return true;
        }
    }

    #endregion

    #region FDL DA Info

    /// <summary>
    /// FDL2 DA 信息结构
    /// </summary>
    public class DaInfo
    {
        /// <summary>版本号</summary>
        public uint Version { get; set; }
        
        /// <summary>是否禁用 HDLC 转码 (0: 启用, 1: 禁用)</summary>
        public uint DisableHdlc { get; set; }
        
        /// <summary>是否为旧内存类型</summary>
        public byte IsOldMemory { get; set; }
        
        /// <summary>是否支持原始数据模式</summary>
        public byte SupportRawData { get; set; }
        
        /// <summary>刷新大小 (KB)</summary>
        public uint FlushSize { get; set; }
        
        /// <summary>存储类型 (0x103 = UFS, 其他 = eMMC)</summary>
        public uint StorageType { get; set; }

        /// <summary>
        /// 是否应禁用 HDLC 转码
        /// </summary>
        public bool ShouldDisableTranscode => DisableHdlc > 0;

        /// <summary>
        /// 是否为 UFS 存储
        /// </summary>
        public bool IsUfs => StorageType == 0x103;

        /// <summary>
        /// 获取存储类型字符串
        /// </summary>
        public string StorageTypeString => IsUfs ? "UFS" : "eMMC";

        /// <summary>
        /// 解析 DA 信息
        /// </summary>
        public static DaInfo Parse(byte[] data)
        {
            var info = new DaInfo();
            
            if (data == null || data.Length < 4)
                return info;

            // 检查新格式 (TLV)
            if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 0x7477656E) // "newt"
            {
                ParseTlvFormat(data, info);
            }
            else
            {
                // 旧格式 (固定结构)
                ParseLegacyFormat(data, info);
            }

            return info;
        }

        private static void ParseTlvFormat(byte[] data, DaInfo info)
        {
            int offset = 4;
            
            while (offset + 4 <= data.Length)
            {
                ushort type = BitConverter.ToUInt16(data, offset);
                ushort length = BitConverter.ToUInt16(data, offset + 2);
                offset += 4;

                if (offset + length > data.Length)
                    break;

                switch (type)
                {
                    case 0: // bDisableHDLC
                        if (length >= 4)
                            info.DisableHdlc = BitConverter.ToUInt32(data, offset);
                        break;
                    case 2: // bSupportRawData
                        if (length >= 1)
                            info.SupportRawData = data[offset];
                        break;
                    case 3: // dwFlushSize
                        if (length >= 4)
                            info.FlushSize = BitConverter.ToUInt32(data, offset);
                        break;
                    case 6: // dwStorageType
                        if (length >= 4)
                            info.StorageType = BitConverter.ToUInt32(data, offset);
                        break;
                }

                offset += length;
            }
        }

        private static void ParseLegacyFormat(byte[] data, DaInfo info)
        {
            int offset = 0;

            if (offset + 4 <= data.Length)
            {
                info.Version = BitConverter.ToUInt32(data, offset);
                offset += 4;
            }
            if (offset + 4 <= data.Length)
            {
                info.DisableHdlc = BitConverter.ToUInt32(data, offset);
                offset += 4;
            }
            if (offset < data.Length)
            {
                info.IsOldMemory = data[offset++];
            }
            if (offset < data.Length)
            {
                info.SupportRawData = data[offset++];
            }
            // 跳过 2 字节保留
            offset += 2;
            if (offset + 4 <= data.Length)
            {
                info.FlushSize = BitConverter.ToUInt32(data, offset);
                offset += 4;
            }
            if (offset + 4 <= data.Length)
            {
                info.StorageType = BitConverter.ToUInt32(data, offset);
            }
        }
    }

    #endregion

    #region NV 校验和

    /// <summary>
    /// NV 分区专用 CRC-16 校验
    /// </summary>
    public class Crc16NvChecksum
    {
        private static readonly ushort[] Crc16Table = new ushort[]
        {
            0x0000, 0xC0C1, 0xC181, 0x0140, 0xC301, 0x03C0, 0x0280, 0xC241,
            0xC601, 0x06C0, 0x0780, 0xC741, 0x0500, 0xC5C1, 0xC481, 0x0440,
            0xCC01, 0x0CC0, 0x0D80, 0xCD41, 0x0F00, 0xCFC1, 0xCE81, 0x0E40,
            0x0A00, 0xCAC1, 0xCB81, 0x0B40, 0xC901, 0x09C0, 0x0880, 0xC841,
            0xD801, 0x18C0, 0x1980, 0xD941, 0x1B00, 0xDBC1, 0xDA81, 0x1A40,
            0x1E00, 0xDEC1, 0xDF81, 0x1F40, 0xDD01, 0x1DC0, 0x1C80, 0xDC41,
            0x1400, 0xD4C1, 0xD581, 0x1540, 0xD701, 0x17C0, 0x1680, 0xD641,
            0xD201, 0x12C0, 0x1380, 0xD341, 0x1100, 0xD1C1, 0xD081, 0x1040,
            0xF001, 0x30C0, 0x3180, 0xF141, 0x3300, 0xF3C1, 0xF281, 0x3240,
            0x3600, 0xF6C1, 0xF781, 0x3740, 0xF501, 0x35C0, 0x3480, 0xF441,
            0x3C00, 0xFCC1, 0xFD81, 0x3D40, 0xFF01, 0x3FC0, 0x3E80, 0xFE41,
            0xFA01, 0x3AC0, 0x3B80, 0xFB41, 0x3900, 0xF9C1, 0xF881, 0x3840,
            0x2800, 0xE8C1, 0xE981, 0x2940, 0xEB01, 0x2BC0, 0x2A80, 0xEA41,
            0xEE01, 0x2EC0, 0x2F80, 0xEF41, 0x2D00, 0xEDC1, 0xEC81, 0x2C40,
            0xE401, 0x24C0, 0x2580, 0xE541, 0x2700, 0xE7C1, 0xE681, 0x2640,
            0x2200, 0xE2C1, 0xE381, 0x2340, 0xE101, 0x21C0, 0x2080, 0xE041,
            0xA001, 0x60C0, 0x6180, 0xA141, 0x6300, 0xA3C1, 0xA281, 0x6240,
            0x6600, 0xA6C1, 0xA781, 0x6740, 0xA501, 0x65C0, 0x6480, 0xA441,
            0x6C00, 0xACC1, 0xAD81, 0x6D40, 0xAF01, 0x6FC0, 0x6E80, 0xAE41,
            0xAA01, 0x6AC0, 0x6B80, 0xAB41, 0x6900, 0xA9C1, 0xA881, 0x6840,
            0x7800, 0xB8C1, 0xB981, 0x7940, 0xBB01, 0x7BC0, 0x7A80, 0xBA41,
            0xBE01, 0x7EC0, 0x7F80, 0xBF41, 0x7D00, 0xBDC1, 0xBC81, 0x7C40,
            0xB401, 0x74C0, 0x7580, 0xB541, 0x7700, 0xB7C1, 0xB681, 0x7640,
            0x7200, 0xB2C1, 0xB381, 0x7340, 0xB101, 0x71C0, 0x7080, 0xB041,
            0x5000, 0x90C1, 0x9181, 0x5140, 0x9301, 0x53C0, 0x5280, 0x9241,
            0x9601, 0x56C0, 0x5780, 0x9741, 0x5500, 0x95C1, 0x9481, 0x5440,
            0x9C01, 0x5CC0, 0x5D80, 0x9D41, 0x5F00, 0x9FC1, 0x9E81, 0x5E40,
            0x5A00, 0x9AC1, 0x9B81, 0x5B40, 0x9901, 0x59C0, 0x5880, 0x9841,
            0x8801, 0x48C0, 0x4980, 0x8941, 0x4B00, 0x8BC1, 0x8A81, 0x4A40,
            0x4E00, 0x8EC1, 0x8F81, 0x4F40, 0x8D01, 0x4DC0, 0x4C80, 0x8C41,
            0x4400, 0x84C1, 0x8581, 0x4540, 0x8701, 0x47C0, 0x4680, 0x8641,
            0x8201, 0x42C0, 0x4380, 0x8341, 0x4100, 0x81C1, 0x8081, 0x4040
        };

        /// <summary>
        /// 计算 CRC-16 校验和 (NV 格式)
        /// </summary>
        public ushort Compute(byte[] data)
        {
            return Compute(data, 0, data.Length);
        }

        /// <summary>
        /// 计算 CRC-16 校验和 (NV 格式)
        /// </summary>
        public ushort Compute(byte[] data, int offset, int length)
        {
            ushort crc = 0;
            
            for (int i = offset; i < offset + length && i < data.Length; i++)
            {
                crc = (ushort)(crc >> 8 ^ Crc16Table[(crc ^ data[i]) & 0xFF]);
            }
            
            return crc;
        }
    }

    #endregion
}
