// ============================================================================
// SakuraEDL - Fastboot Protocol | Fastboot 协议
// ============================================================================
// [ZH] Fastboot 协议定义 - Android Fastboot 通信协议实现
// [EN] Fastboot Protocol - Android Fastboot communication protocol
// [JA] Fastbootプロトコル - Android Fastboot通信プロトコル
// [KO] Fastboot 프로토콜 - Android Fastboot 통신 프로토콜
// [RU] Протокол Fastboot - Протокол связи Android Fastboot
// [ES] Protocolo Fastboot - Protocolo de comunicación Android Fastboot
// ============================================================================
// Reference: Google AOSP platform/system/core/fastboot
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Text;

namespace SakuraEDL.Fastboot.Protocol
{
    /// <summary>
    /// Fastboot 协议定义
    /// 基于 Google AOSP platform/system/core/fastboot 源码分析
    /// 
    /// 协议格式：
    /// - 命令：ASCII 字符串，最大 4096 字节
    /// - 响应：4 字节前缀 + 可选数据
    ///   - "OKAY" - 命令成功
    ///   - "FAIL" - 命令失败，后跟错误信息
    ///   - "DATA" - 准备接收数据，后跟 8 字节十六进制长度
    ///   - "INFO" - 信息消息，后跟文本
    /// </summary>
    public static class FastbootProtocol
    {
        // 协议常量 (根据 Google 官方 README.md)
        public const int MAX_COMMAND_LENGTH = 4096;
        public const int MAX_RESPONSE_LENGTH = 256;
        public const int RESPONSE_PREFIX_LENGTH = 4;
        public const int DEFAULT_TIMEOUT_MS = 30000;
        public const int DATA_TIMEOUT_MS = 60000;
        public const string PROTOCOL_VERSION = "0.4";
        
        // USB 协议常量
        public const int USB_CLASS_FASTBOOT = 0xFF;
        public const int USB_SUBCLASS_FASTBOOT = 0x42;
        public const int USB_PROTOCOL_FASTBOOT = 0x03;
        
        #region 厂商 USB Vendor ID
        
        public const int USB_VID_GOOGLE = 0x18D1;       // Google / Pixel
        public const int USB_VID_XIAOMI = 0x2717;       // 小米
        public const int USB_VID_OPPO = 0x22D9;         // OPPO
        public const int USB_VID_ONEPLUS = 0x2A70;      // 一加
        public const int USB_VID_QUALCOMM = 0x05C6;     // 高通
        public const int USB_VID_SAMSUNG = 0x04E8;      // 三星
        public const int USB_VID_HUAWEI = 0x12D1;       // 华为
        public const int USB_VID_MOTOROLA = 0x22B8;     // 摩托罗拉
        public const int USB_VID_SONY = 0x0FCE;         // 索尼
        public const int USB_VID_LG = 0x1004;           // LG
        public const int USB_VID_HTC = 0x0BB4;          // HTC
        public const int USB_VID_ASUS = 0x0B05;         // 华硕
        public const int USB_VID_LENOVO = 0x17EF;       // 联想
        public const int USB_VID_VIVO = 0x2D95;         // VIVO
        public const int USB_VID_MEIZU = 0x2A45;        // 魅族
        public const int USB_VID_ZTE = 0x19D2;          // 中兴/努比亚
        public const int USB_VID_REALME = 0x22D9;       // Realme (同 OPPO)
        public const int USB_VID_NOTHING = 0x2970;      // Nothing Phone
        public const int USB_VID_FAIRPHONE = 0x2AE5;    // Fairphone
        public const int USB_VID_ESSENTIAL = 0x2E17;    // Essential
        public const int USB_VID_NVIDIA = 0x0955;       // NVIDIA Shield
        public const int USB_VID_MTK = 0x0E8D;          // 联发科 MTK
        
        // 所有支持的 Vendor ID 列表
        public static readonly int[] SUPPORTED_VENDOR_IDS = {
            USB_VID_GOOGLE, USB_VID_XIAOMI, USB_VID_OPPO, USB_VID_ONEPLUS,
            USB_VID_QUALCOMM, USB_VID_SAMSUNG, USB_VID_HUAWEI, USB_VID_MOTOROLA,
            USB_VID_SONY, USB_VID_LG, USB_VID_HTC, USB_VID_ASUS, USB_VID_LENOVO,
            USB_VID_VIVO, USB_VID_MEIZU, USB_VID_ZTE, USB_VID_NOTHING,
            USB_VID_FAIRPHONE, USB_VID_ESSENTIAL, USB_VID_NVIDIA, USB_VID_MTK
        };
        
        #endregion
        
        // 响应前缀
        public const string RESPONSE_OKAY = "OKAY";
        public const string RESPONSE_FAIL = "FAIL";
        public const string RESPONSE_DATA = "DATA";
        public const string RESPONSE_INFO = "INFO";
        public const string RESPONSE_TEXT = "TEXT";
        
        #region Google 官方标准命令 (必须支持)
        
        // 基础命令
        public const string CMD_GETVAR = "getvar";              // 查询变量
        public const string CMD_DOWNLOAD = "download";          // 下载数据到设备内存
        public const string CMD_UPLOAD = "upload";              // 从设备上传数据
        public const string CMD_FLASH = "flash";                // 刷写分区
        public const string CMD_ERASE = "erase";                // 擦除分区
        public const string CMD_BOOT = "boot";                  // 从内存启动
        public const string CMD_CONTINUE = "continue";          // 继续启动流程
        
        // 重启命令
        public const string CMD_REBOOT = "reboot";
        public const string CMD_REBOOT_BOOTLOADER = "reboot-bootloader";
        public const string CMD_REBOOT_FASTBOOT = "reboot-fastboot";     // 重启到 fastbootd
        public const string CMD_REBOOT_RECOVERY = "reboot-recovery";
        public const string CMD_REBOOT_EDL = "reboot-edl";               // 重启到 EDL 模式
        public const string CMD_POWERDOWN = "powerdown";                 // 关机
        
        // A/B 槽位命令
        public const string CMD_SET_ACTIVE = "set_active";               // 设置活动槽位
        
        // 解锁/锁定命令
        public const string CMD_FLASHING_UNLOCK = "flashing unlock";
        public const string CMD_FLASHING_LOCK = "flashing lock";
        public const string CMD_FLASHING_UNLOCK_CRITICAL = "flashing unlock_critical";
        public const string CMD_FLASHING_LOCK_CRITICAL = "flashing lock_critical";
        public const string CMD_FLASHING_GET_UNLOCK_ABILITY = "flashing get_unlock_ability";
        
        // 动态分区命令 (Android 10+)
        public const string CMD_UPDATE_SUPER = "update-super";
        public const string CMD_CREATE_LOGICAL_PARTITION = "create-logical-partition";
        public const string CMD_DELETE_LOGICAL_PARTITION = "delete-logical-partition";
        public const string CMD_RESIZE_LOGICAL_PARTITION = "resize-logical-partition";
        public const string CMD_WIPE_SUPER = "wipe-super";
        
        // GSI/快照命令
        public const string CMD_GSI = "gsi";
        public const string CMD_GSI_WIPE = "gsi wipe";
        public const string CMD_GSI_DISABLE = "gsi disable";
        public const string CMD_GSI_STATUS = "gsi status";
        public const string CMD_SNAPSHOT_UPDATE = "snapshot-update";
        public const string CMD_SNAPSHOT_UPDATE_CANCEL = "snapshot-update cancel";
        public const string CMD_SNAPSHOT_UPDATE_MERGE = "snapshot-update merge";
        
        // 数据获取命令
        public const string CMD_FETCH = "fetch";                // 从设备获取分区数据
        
        // OEM 通用命令
        public const string CMD_OEM = "oem";
        
        #endregion
        
        #region 厂商专属命令 (OEM Commands)
        
        // ========== 小米/红米 (Xiaomi/Redmi) ==========
        public const string OEM_XIAOMI_DEVICE_INFO = "oem device-info";
        public const string OEM_XIAOMI_REBOOT_EDL = "oem edl";
        public const string OEM_XIAOMI_LOCK = "oem lock";
        public const string OEM_XIAOMI_UNLOCK = "oem unlock";
        public const string OEM_XIAOMI_LKSTATE = "oem lks";
        public const string OEM_XIAOMI_GET_TOKEN = "oem get_token";
        public const string OEM_XIAOMI_WRITE_PERSIST = "oem write_persist";
        public const string OEM_XIAOMI_BATTERY = "oem battery";
        public const string OEM_XIAOMI_REBOOT_FTMW = "oem ftmw";           // 工厂模式
        public const string OEM_XIAOMI_CDMS = "oem cdms";
        
        // ========== 一加 (OnePlus) ==========
        public const string OEM_ONEPLUS_DEVICE_INFO = "oem device-info";
        public const string OEM_ONEPLUS_UNLOCK = "oem unlock";
        public const string OEM_ONEPLUS_LOCK = "oem lock";
        public const string OEM_ONEPLUS_ENABLE_DM_VERITY = "oem enable_dm_verity";
        public const string OEM_ONEPLUS_DISABLE_DM_VERITY = "oem disable_dm_verity";
        public const string OEM_ONEPLUS_SN = "oem sn";                     // 获取序列号
        public const string OEM_ONEPLUS_4K = "oem 4k-video-supported";
        public const string OEM_ONEPLUS_REBOOT_FTMW = "oem ftmw";
        
        // ========== OPPO/Realme ==========
        public const string OEM_OPPO_DEVICE_INFO = "oem device-info";
        public const string OEM_OPPO_UNLOCK = "oem unlock";
        public const string OEM_OPPO_LOCK = "oem lock";
        public const string OEM_OPPO_GET_UNLOCK_CODE = "oem get-unlock-code";
        public const string OEM_OPPO_RW_FLAG = "oem rw_flag";
        public const string OEM_OPPO_DM_VERITY = "oem dm-verity";
        
        // ========== 三星 (Samsung) - Odin 模式不同，部分支持 ==========
        public const string OEM_SAMSUNG_UNLOCK = "oem unlock";
        public const string OEM_SAMSUNG_FRPRESET = "oem frpreset";         // FRP 重置
        
        // ========== 华为 (Huawei) ==========
        public const string OEM_HUAWEI_UNLOCK = "oem unlock";
        public const string OEM_HUAWEI_GET_IDENTIFIER = "oem get-identifier-token";
        public const string OEM_HUAWEI_CHECK_ROOTINFO = "oem check-rootinfo";
        
        // ========== 摩托罗拉 (Motorola) ==========
        public const string OEM_MOTO_UNLOCK = "oem unlock";
        public const string OEM_MOTO_LOCK = "oem lock";
        public const string OEM_MOTO_GET_UNLOCK_DATA = "oem get_unlock_data";
        public const string OEM_MOTO_BP_TOOLS_ON = "oem bp_tools_on";
        public const string OEM_MOTO_CONFIG_CARRIER = "oem config carrier";
        
        // ========== 索尼 (Sony) ==========
        public const string OEM_SONY_UNLOCK = "oem unlock";
        public const string OEM_SONY_GET_KEY = "oem key";
        public const string OEM_SONY_TA_BACKUP = "oem ta_backup";
        
        // ========== 高通通用 (Qualcomm Generic) ==========
        public const string OEM_QC_DEVICE_INFO = "oem device-info";
        public const string OEM_QC_ENABLE_CHARGER_SCREEN = "oem enable-charger-screen";
        public const string OEM_QC_DISABLE_CHARGER_SCREEN = "oem disable-charger-screen";
        public const string OEM_QC_OFF_MODE_CHARGE = "oem off-mode-charge";
        public const string OEM_QC_SELECT_DISPLAY_PANEL = "oem select-display-panel";
        
        // ========== MTK 联发科通用 ==========
        public const string OEM_MTK_REBOOT_META = "oem reboot-meta";
        public const string OEM_MTK_LOG_ENABLE = "oem log_enable";
        public const string OEM_MTK_P2U = "oem p2u";
        
        // ========== Google Pixel ==========
        public const string OEM_PIXEL_UNLOCK = "flashing unlock";          // Pixel 使用标准命令
        public const string OEM_PIXEL_LOCK = "flashing lock";
        public const string OEM_PIXEL_GET_UNLOCK_ABILITY = "flashing get_unlock_ability";
        public const string OEM_PIXEL_OFF_MODE_CHARGE = "oem off-mode-charge";
        
        #endregion
        
        #region 标准变量名 (getvar)
        
        // 协议/版本信息
        public const string VAR_VERSION = "version";                       // 协议版本 (0.4)
        public const string VAR_VERSION_BOOTLOADER = "version-bootloader";
        public const string VAR_VERSION_BASEBAND = "version-baseband";
        public const string VAR_VERSION_OS = "version-os";
        public const string VAR_VERSION_VNDK = "version-vndk";
        
        // 设备信息
        public const string VAR_PRODUCT = "product";
        public const string VAR_SERIALNO = "serialno";
        public const string VAR_VARIANT = "variant";
        public const string VAR_HW_REVISION = "hw-revision";
        
        // 安全状态
        public const string VAR_SECURE = "secure";
        public const string VAR_UNLOCKED = "unlocked";
        public const string VAR_DEVICE_STATE = "device-state";             // locked/unlocked
        
        // 容量限制
        public const string VAR_MAX_DOWNLOAD_SIZE = "max-download-size";
        public const string VAR_MAX_FETCH_SIZE = "max-fetch-size";
        
        // A/B 槽位
        public const string VAR_CURRENT_SLOT = "current-slot";
        public const string VAR_SLOT_COUNT = "slot-count";
        public const string VAR_HAS_SLOT = "has-slot";
        public const string VAR_SLOT_SUCCESSFUL = "slot-successful";
        public const string VAR_SLOT_UNBOOTABLE = "slot-unbootable";
        public const string VAR_SLOT_RETRY_COUNT = "slot-retry-count";
        
        // 分区信息
        public const string VAR_PARTITION_SIZE = "partition-size";
        public const string VAR_PARTITION_TYPE = "partition-type";
        public const string VAR_IS_LOGICAL = "is-logical";
        
        // Fastbootd / 动态分区
        public const string VAR_IS_USERSPACE = "is-userspace";
        public const string VAR_SUPER_PARTITION_NAME = "super-partition-name";
        public const string VAR_SNAPSHOT_UPDATE_STATUS = "snapshot-update-status";
        
        // 电池信息
        public const string VAR_BATTERY_VOLTAGE = "battery-voltage";
        public const string VAR_BATTERY_SOC_OK = "battery-soc-ok";
        public const string VAR_CHARGER_SCREEN_ENABLED = "charger-screen-enabled";
        public const string VAR_OFF_MODE_CHARGE = "off-mode-charge";
        
        // 通用
        public const string VAR_ALL = "all";                               // 获取所有变量
        
        #endregion
        
        #region 厂商特有变量
        
        // 小米
        public const string VAR_XIAOMI_ANTI = "anti";
        public const string VAR_XIAOMI_TOKEN = "token";
        public const string VAR_XIAOMI_PRODUCT_TYPE = "product_type";
        
        // 一加
        public const string VAR_ONEPLUS_BUILD_TYPE = "build-type";
        public const string VAR_ONEPLUS_CARRIER = "carrier";
        
        // 华为
        public const string VAR_HUAWEI_IDENTIFIER_TOKEN = "identifier-token";
        
        // 高通
        public const string VAR_QC_SECURESTATE = "securestate";
        
        #endregion
        
        /// <summary>
        /// 构建命令字节
        /// </summary>
        public static byte[] BuildCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
                throw new ArgumentNullException(nameof(command));
                
            if (command.Length > MAX_COMMAND_LENGTH)
                throw new ArgumentException($"命令长度超过 {MAX_COMMAND_LENGTH} 字节");
                
            return Encoding.ASCII.GetBytes(command);
        }
        
        /// <summary>
        /// 构建带参数的命令
        /// </summary>
        public static byte[] BuildCommand(string command, string argument)
        {
            return BuildCommand($"{command}:{argument}");
        }
        
        /// <summary>
        /// 构建下载命令（指定数据大小）
        /// </summary>
        public static byte[] BuildDownloadCommand(long size)
        {
            // 格式: download:XXXXXXXX (8位十六进制)
            return BuildCommand($"{CMD_DOWNLOAD}:{size:x8}");
        }
        
        /// <summary>
        /// 解析响应
        /// </summary>
        public static FastbootResponse ParseResponse(byte[] data, int length)
        {
            if (data == null || length < RESPONSE_PREFIX_LENGTH)
            {
                return new FastbootResponse
                {
                    Type = ResponseType.Unknown,
                    RawData = data,
                    Message = "响应数据无效"
                };
            }
            
            string response = Encoding.ASCII.GetString(data, 0, length);
            string prefix = response.Substring(0, RESPONSE_PREFIX_LENGTH);
            string payload = length > RESPONSE_PREFIX_LENGTH 
                ? response.Substring(RESPONSE_PREFIX_LENGTH) 
                : string.Empty;
            
            var result = new FastbootResponse
            {
                RawData = data,
                RawString = response,
                Message = payload
            };
            
            switch (prefix)
            {
                case RESPONSE_OKAY:
                    result.Type = ResponseType.Okay;
                    break;
                    
                case RESPONSE_FAIL:
                    result.Type = ResponseType.Fail;
                    break;
                    
                case RESPONSE_DATA:
                    result.Type = ResponseType.Data;
                    // 解析数据长度 (8位十六进制)
                    if (payload.Length >= 8)
                    {
                        try
                        {
                            result.DataSize = Convert.ToInt64(payload.Substring(0, 8), 16);
                        }
                        catch { }
                    }
                    break;
                    
                case RESPONSE_INFO:
                    result.Type = ResponseType.Info;
                    break;
                    
                case RESPONSE_TEXT:
                    result.Type = ResponseType.Text;
                    break;
                    
                default:
                    result.Type = ResponseType.Unknown;
                    result.Message = response;
                    break;
            }
            
            return result;
        }
    }
    
    /// <summary>
    /// 响应类型
    /// </summary>
    public enum ResponseType
    {
        Unknown,
        Okay,       // 命令成功
        Fail,       // 命令失败
        Data,       // 准备接收数据
        Info,       // 信息消息
        Text        // 文本消息
    }
    
    /// <summary>
    /// Fastboot 响应
    /// </summary>
    public class FastbootResponse
    {
        public ResponseType Type { get; set; }
        public string Message { get; set; }
        public string RawString { get; set; }
        public byte[] RawData { get; set; }
        public long DataSize { get; set; }
        
        public bool IsSuccess => Type == ResponseType.Okay;
        public bool IsFail => Type == ResponseType.Fail;
        public bool IsData => Type == ResponseType.Data;
        public bool IsInfo => Type == ResponseType.Info;
        
        public override string ToString()
        {
            return $"[{Type}] {Message}";
        }
    }
}
