// ============================================================================
// SakuraEDL - Spreadtrum BSL Commands | 展讯 BSL 命令
// ============================================================================
// [ZH] BSL/FDL 命令定义 - 展讯下载协议命令常量
// [EN] BSL/FDL Commands - Spreadtrum download protocol command constants
// [JA] BSL/FDLコマンド - Spreadtrumダウンロードプロトコルコマンド
// [KO] BSL/FDL 명령어 - Spreadtrum 다운로드 프로토콜 명령 상수
// [RU] Команды BSL/FDL - Константы команд протокола загрузки Spreadtrum
// [ES] Comandos BSL/FDL - Constantes de comandos de protocolo Spreadtrum
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;

namespace SakuraEDL.Spreadtrum.Protocol
{
    /// <summary>
    /// BSL 命令类型 (参考 spd_cmd.h)
    /// </summary>
    public enum BslCommand : byte
    {
        // ========================================
        // 连接/握手命令
        // ========================================
        
        /// <summary>连接命令</summary>
        BSL_CMD_CONNECT = 0x00,
        
        // ========================================
        // 数据传输命令
        // ========================================
        
        
        /// <summary>开始数据传输 (发送地址和大小)</summary>
        BSL_CMD_START_DATA = 0x01,
        
        /// <summary>数据块传输</summary>
        BSL_CMD_MIDST_DATA = 0x02,
        
        /// <summary>结束数据传输</summary>
        BSL_CMD_END_DATA = 0x03,
        
        /// <summary>执行已下载的代码</summary>
        BSL_CMD_EXEC_DATA = 0x04,
        
        /// <summary>正常重启 / 复位设备</summary>
        BSL_CMD_NORMAL_RESET = 0x05,
        BSL_CMD_RESET = 0x05,  // 别名
        
        /// <summary>读取 Flash 内容</summary>
        BSL_CMD_READ_FLASH = 0x06,
        
        /// <summary>读取芯片类型</summary>
        BSL_CMD_READ_CHIP_TYPE = 0x07,
        
        /// <summary>读取 NV 项</summary>
        BSL_CMD_READ_NVITEM = 0x08,
        
        /// <summary>改变波特率 / 设置波特率</summary>
        BSL_CMD_CHANGE_BAUD = 0x09,
        BSL_CMD_SET_BAUD = 0x09,  // 别名
        
        /// <summary>擦除 Flash</summary>
        BSL_CMD_ERASE_FLASH = 0x0A,
        
        /// <summary>重新分区 NAND Flash</summary>
        BSL_CMD_REPARTITION = 0x0B,
        
        /// <summary>读取 Flash 类型</summary>
        BSL_CMD_READ_FLASH_TYPE = 0x0C,
        
        /// <summary>读取 Flash 信息</summary>
        BSL_CMD_READ_FLASH_INFO = 0x0D,
        
        /// <summary>读取 NOR Flash 扇区大小</summary>
        BSL_CMD_READ_SECTOR_SIZE = 0x0F,
        
        // ========================================
        // 分区读取命令 (正确值!)
        // ========================================
        
        /// <summary>读取 Flash 开始</summary>
        BSL_CMD_READ_START = 0x10,
        
        /// <summary>读取 Flash 中间数据</summary>
        BSL_CMD_READ_MIDST = 0x11,
        
        /// <summary>读取 Flash 结束</summary>
        BSL_CMD_READ_END = 0x12,
        
        // ========================================
        // 设备控制命令
        // ========================================
        
        /// <summary>保持充电</summary>
        BSL_CMD_KEEP_CHARGE = 0x13,
        
        /// <summary>设置扩展表</summary>
        BSL_CMD_EXTTABLE = 0x14,
        
        /// <summary>读取 Flash UID</summary>
        BSL_CMD_READ_FLASH_UID = 0x15,
        
        /// <summary>读取软 SIM EID</summary>
        BSL_CMD_READ_SOFTSIM_EID = 0x16,
        
        /// <summary>关机</summary>
        BSL_CMD_POWER_OFF = 0x17,
        
        /// <summary>检查 Root</summary>
        BSL_CMD_CHECK_ROOT = 0x19,
        
        /// <summary>读取芯片 UID</summary>
        BSL_CMD_READ_CHIP_UID = 0x1A,
        
        /// <summary>使能 Flash 写入</summary>
        BSL_CMD_ENABLE_WRITE_FLASH = 0x1B,
        
        /// <summary>使能安全启动 / 读取版本 (用于获取版本信息)</summary>
        BSL_CMD_ENABLE_SECUREBOOT = 0x1C,
        BSL_CMD_READ_VERSION = 0x1C,  // 别名，部分 FDL 使用此命令读取版本
        
        /// <summary>识别开始</summary>
        BSL_CMD_IDENTIFY_START = 0x1D,
        
        /// <summary>识别结束</summary>
        BSL_CMD_IDENTIFY_END = 0x1E,
        
        /// <summary>读取 CU Ref</summary>
        BSL_CMD_READ_CU_REF = 0x1F,
        
        /// <summary>读取 Ref 信息</summary>
        BSL_CMD_READ_REFINFO = 0x20,
        
        /// <summary>禁用转码 (FDL2 必需)</summary>
        BSL_CMD_DISABLE_TRANSCODE = 0x21,
        
        /// <summary>写入 NV 项</summary>
        BSL_CMD_WRITE_NVITEM = 0x22,
        
        /// <summary>写入日期时间到 miscdata</summary>
        BSL_CMD_WRITE_DATETIME = 0x22,
        
        /// <summary>自定义 Dummy</summary>
        BSL_CMD_CUST_DUMMY = 0x23,
        
        /// <summary>读取 RF 收发器类型</summary>
        BSL_CMD_READ_RF_TRANSCEIVER_TYPE = 0x24,
        
        /// <summary>设置调试信息</summary>
        BSL_CMD_SET_DEBUGINFO = 0x25,
        
        /// <summary>DDR 检查</summary>
        BSL_CMD_DDR_CHECK = 0x26,
        
        /// <summary>自刷新</summary>
        BSL_CMD_SELF_REFRESH = 0x27,
        
        /// <summary>使能原始数据写入 (用于 0x31 和 0x33)</summary>
        BSL_CMD_WRITE_RAW_DATA_ENABLE = 0x28,
        
        /// <summary>读取 NAND 块信息</summary>
        BSL_CMD_READ_NAND_BLOCK_INFO = 0x29,
        
        /// <summary>设置首次模式</summary>
        BSL_CMD_SET_FIRST_MODE = 0x2A,
        
        /// <summary>读取分区列表</summary>
        BSL_CMD_READ_PARTITION = 0x2D,
        
        /// <summary>解锁</summary>
        BSL_CMD_UNLOCK = 0x30,
        
        /// <summary>读取公钥 / 原始数据包</summary>
        BSL_CMD_READ_PUBKEY = 0x31,
        BSL_CMD_DLOAD_RAW_START = 0x31,
        
        /// <summary>写入刷新数据 / 发送签名</summary>
        BSL_CMD_WRITE_FLUSH_DATA = 0x32,
        BSL_CMD_SEND_SIGNATURE = 0x32,
        
        /// <summary>完整原始文件</summary>
        BSL_CMD_DLOAD_RAW_START2 = 0x33,
        
        /// <summary>读取日志</summary>
        BSL_CMD_READ_LOG = 0x35,
        
        /// <summary>读取 eFuse</summary>
        BSL_CMD_READ_EFUSE = 0x60,
        
        /// <summary>波特率检测 (内部使用) - 发送多个 0x7E</summary>
        BSL_CMD_CHECK_BAUD = 0x7E,
        
        /// <summary>结束刷机过程</summary>
        BSL_CMD_END_PROCESS = 0x7F,
        
        // ========================================
        // 响应类型
        // ========================================
        
        /// <summary>确认响应</summary>
        BSL_REP_ACK = 0x80,
        
        /// <summary>版本响应</summary>
        BSL_REP_VER = 0x81,
        
        /// <summary>无效命令</summary>
        BSL_REP_INVALID_CMD = 0x82,
        
        /// <summary>未知命令</summary>
        BSL_REP_UNKNOWN_CMD = 0x83,
        
        /// <summary>操作失败</summary>
        BSL_REP_OPERATION_FAILED = 0x84,
        
        /// <summary>不支持的波特率</summary>
        BSL_REP_NOT_SUPPORT_BAUDRATE = 0x85,
        
        /// <summary>下载未开始</summary>
        BSL_REP_DOWN_NOT_START = 0x86,
        
        /// <summary>重复开始下载</summary>
        BSL_REP_DOWN_MULTI_START = 0x87,
        
        /// <summary>下载提前结束</summary>
        BSL_REP_DOWN_EARLY_END = 0x88,
        
        /// <summary>下载目标地址错误</summary>
        BSL_REP_DOWN_DEST_ERROR = 0x89,
        
        /// <summary>下载大小错误</summary>
        BSL_REP_DOWN_SIZE_ERROR = 0x8A,
        
        /// <summary>校验错误 (数据校验失败)</summary>
        BSL_REP_VERIFY_ERROR = 0x8B,
        
        /// <summary>未校验</summary>
        BSL_REP_NOT_VERIFY = 0x8C,
        
        /// <summary>内存不足</summary>
        BSL_PHONE_NOT_ENOUGH_MEMORY = 0x8D,
        
        /// <summary>等待输入超时</summary>
        BSL_PHONE_WAIT_INPUT_TIMEOUT = 0x8E,
        
        /// <summary>操作成功 (内部)</summary>
        BSL_PHONE_SUCCEED = 0x8F,
        
        /// <summary>有效波特率</summary>
        BSL_PHONE_VALID_BAUDRATE = 0x90,
        
        /// <summary>重复继续</summary>
        BSL_PHONE_REPEAT_CONTINUE = 0x91,
        
        /// <summary>重复中断</summary>
        BSL_PHONE_REPEAT_BREAK = 0x92,
        
        /// <summary>读取 Flash 响应 / 数据响应</summary>
        BSL_REP_READ_FLASH = 0x93,
        BSL_REP_DATA = 0x93,
        
        /// <summary>芯片类型响应</summary>
        BSL_REP_CHIP_TYPE = 0x94,
        
        /// <summary>NV 项响应</summary>
        BSL_REP_READ_NVITEM = 0x95,
        
        /// <summary>分区不兼容</summary>
        BSL_REP_INCOMPATIBLE_PARTITION = 0x96,
        
        /// <summary>签名校验失败</summary>
        BSL_REP_SIGN_VERIFY_ERROR = 0xA6,
        
        /// <summary>检查 Root 为真</summary>
        BSL_REP_CHECK_ROOT_TRUE = 0xA7,
        
        /// <summary>芯片 UID 响应</summary>
        BSL_REP_READ_CHIP_UID = 0xAB,
        
        /// <summary>分区表响应</summary>
        BSL_REP_PARTITION = 0xBA,
        
        /// <summary>日志响应</summary>
        BSL_REP_READ_LOG = 0xBB,
        
        /// <summary>不支持的命令</summary>
        BSL_REP_UNSUPPORTED_COMMAND = 0xFE,
        
        /// <summary>日志输出</summary>
        BSL_REP_LOG = 0xFF,
        
        // ========================================
        // 兼容定义
        // ========================================
        
        /// <summary>Flash 信息响应</summary>
        BSL_REP_FLASH_INFO = 0x92,
    }

    /// <summary>
    /// BSL 错误码
    /// </summary>
    public enum BslError : ushort
    {
        SUCCESS = 0x0000,
        VERIFY_ERROR = 0x0001,
        CHECKSUM_ERROR = 0x0002,
        PACKET_ERROR = 0x0003,
        SIZE_ERROR = 0x0004,
        WAIT_TIMEOUT = 0x0005,
        DEVICE_ERROR = 0x0006,
        WRITE_ERROR = 0x0007,
        READ_ERROR = 0x0008,
        ERASE_ERROR = 0x0009,
        FLASH_ERROR = 0x000A,
        UNSUPPORTED = 0x000B,
        INVALID_CMD = 0x000C,
        SECURITY_ERROR = 0x000D,
        UNLOCK_ERROR = 0x000E,
    }

    /// <summary>
    /// FDL 阶段
    /// </summary>
    public enum FdlStage
    {
        /// <summary>未加载</summary>
        None,
        
        /// <summary>FDL1 - 第一阶段引导</summary>
        FDL1,
        
        /// <summary>FDL2 - 第二阶段刷机</summary>
        FDL2
    }

    /// <summary>
    /// 展讯设备状态
    /// </summary>
    public enum SprdDeviceState
    {
        /// <summary>断开</summary>
        Disconnected,
        
        /// <summary>已连接 (ROM 模式)</summary>
        Connected,
        
        /// <summary>FDL1 已加载</summary>
        Fdl1Loaded,
        
        /// <summary>FDL2 已加载 (可刷机)</summary>
        Fdl2Loaded,
        
        /// <summary>错误状态</summary>
        Error
    }

    /// <summary>
    /// 展讯芯片平台 (综合 spd_dump, SPRDClientCore, iReverse 等开源项目)
    /// </summary>
    public static class SprdPlatform
    {
        // ========== SC6xxx 功能机系列 ==========
        public const uint SC6500 = 0x6500;
        public const uint SC6530 = 0x6530;
        public const uint SC6531 = 0x6531;
        public const uint SC6531E = 0x6531;
        public const uint SC6531EFM = 0x65310001;
        public const uint SC6531DA = 0x65310002;
        public const uint SC6531H = 0x65310003;
        public const uint SC6533G = 0x6533;
        public const uint SC6533GF = 0x65330001;
        public const uint SC6600 = 0x6600;
        public const uint SC6600L = 0x66000001;
        public const uint SC6610 = 0x6610;
        public const uint SC6620 = 0x6620;
        public const uint SC6800H = 0x6800;
        
        // ========== SC77xx 系列 (传统 3G/4G) ==========
        public const uint SC7701 = 0x7701;
        public const uint SC7702 = 0x7702;
        public const uint SC7710 = 0x7710;
        public const uint SC7715 = 0x7715;
        public const uint SC7715A = 0x77150001;
        public const uint SC7720 = 0x7720;
        public const uint SC7727S = 0x7727;
        public const uint SC7730 = 0x7730;
        public const uint SC7730A = 0x77300001;
        public const uint SC7730S = 0x77300002;
        public const uint SC7731 = 0x7731;
        public const uint SC7731C = 0x77310001;
        public const uint SC7731E = 0x77310002;
        public const uint SC7731G = 0x77310003;
        public const uint SC7731GF = 0x77310004;
        public const uint SC7731EF = 0x77310005;
        
        // ========== SC85xx 系列 ==========
        public const uint SC8521E = 0x8521;
        public const uint SC8541 = 0x8541;
        public const uint SC8541E = 0x8541;
        public const uint SC8541EF = 0x85410001;
        public const uint SC8551 = 0x8551;
        public const uint SC8551E = 0x85510001;
        public const uint SC8581 = 0x8581;
        public const uint SC8581A = 0x85810001;
        
        // ========== SC96xx/SC98xx 系列 ==========
        public const uint SC9600 = 0x9600;
        public const uint SC9610 = 0x9610;
        public const uint SC9620 = 0x9620;
        public const uint SC9630 = 0x9630;
        public const uint SC9820 = 0x9820;
        public const uint SC9820A = 0x98200001;
        public const uint SC9820E = 0x98200002;
        public const uint SC9830 = 0x9830;
        public const uint SC9830A = 0x98300001;
        public const uint SC9830I = 0x98300002;
        public const uint SC9830IA = 0x98300003;
        public const uint SC9832 = 0x9832;
        public const uint SC9832A = 0x98320001;
        public const uint SC9832E = 0x98320002;
        public const uint SC9832EP = 0x98320003;
        public const uint SC9850 = 0x9850;
        public const uint SC9850KA = 0x98500001;
        public const uint SC9850KH = 0x98500002;
        public const uint SC9850S = 0x98500003;
        public const uint SC9853I = 0x9853;
        public const uint SC9860 = 0x9860;
        public const uint SC9860G = 0x98600001;
        public const uint SC9860GV = 0x98600002;
        public const uint SC9861 = 0x9861;
        public const uint SC9863 = 0x9863;
        public const uint SC9863A = 0x98630001;
        
        // ========== Unisoc T 系列 (4G) ==========
        public const uint T310 = 0x0310;
        public const uint T606 = 0x0606;
        public const uint T610 = 0x0610;
        public const uint T612 = 0x0612;
        public const uint T616 = 0x0616;
        public const uint T618 = 0x0618;
        public const uint T700 = 0x0700;
        public const uint T760 = 0x0760;
        public const uint T770 = 0x0770;
        public const uint T820 = 0x0820;
        public const uint T900 = 0x0900;
        
        // ========== Unisoc T 系列 (5G) ==========
        public const uint T740 = 0x0740;
        public const uint T750 = 0x0750;
        public const uint T765 = 0x0765;
        public const uint T7510 = 0x7510;
        public const uint T7520 = 0x7520;
        public const uint T7525 = 0x7525;
        public const uint T7530 = 0x7530;
        public const uint T7560 = 0x7560;
        public const uint T7570 = 0x7570;
        public const uint T8000 = 0x8000;
        public const uint T8200 = 0x8200;
        
        // ========== UMS 系列 (Unisoc Mobile SOC) ==========
        public const uint UMS312 = 0x0312;      // T310 变体
        public const uint UMS512 = 0x0512;      // T618 变体
        public const uint UMS9230 = 0x9230;     // T606 变体
        public const uint UMS9620 = 0x96200001; // T740 变体 (区分于 SC9620)
        public const uint UMS9621 = 0x96210001;
        
        // ========== UIS 系列 (Unisoc IoT SOC) ==========
        public const uint UIS7862 = 0x78620001;
        public const uint UIS7863 = 0x78630001;
        public const uint UIS7870 = 0x78700001;
        public const uint UIS7885 = 0x78850001;
        public const uint UIS8581 = 0x85810002; // 区分于 SC8581
        public const uint UIS8910DM = 0x89100001;
        
        // ========== T1xx 系列 (4G 功能机) - 参考 spreadtrum_flash ==========
        public const uint T107 = 0x0107;      // UMS9107
        public const uint T117 = 0x0117;      // UMS9117 (最常见)
        public const uint T127 = 0x0127;      // UMS9127
        public const uint UMS9107 = 0x9107;   // T107 别名
        public const uint UMS9117 = 0x9117;   // T117 别名
        public const uint UMS9127 = 0x9127;   // T127 别名
        
        // ========== W 系列 (功能机/IoT) ==========
        public const uint W117 = 0x01170001;  // 使用不同的 ID 避免与 T117 冲突
        public const uint W217 = 0x0217;
        public const uint W307 = 0x0307;
        
        // ========== UWS 系列 (Wearable) ==========
        public const uint UWS6121 = 0x6121;
        public const uint UWS6122 = 0x6122;
        public const uint UWS6131 = 0x6131;
        public const uint UWS6152 = 0x6152;     // 用户询问的芯片
        
        // ========== S 系列 (平板/IoT) ==========
        public const uint S9863A1H10 = 0x9863;
        
        // ========== 特殊芯片 ID ==========
        public const uint CHIP_UNKNOWN = 0x0000;
        public const uint CHIP_SC6800H = 0x6800;
        public const uint CHIP_SC8800G = 0x8800;
        
        /// <summary>
        /// 获取平台名称
        /// </summary>
        public static string GetPlatformName(uint chipId)
        {
            // 处理带子型号的芯片 ID (高 16 位为基础 ID)
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            
            switch (chipId)
            {
                // ========== SC6xxx 功能机系列 ==========
                case SC6500: return "SC6500";
                case SC6530: return "SC6530";
                case SC6531E: return "SC6531E";
                case SC6531EFM: return "SC6531E-FM";
                case SC6531DA: return "SC6531DA";
                case SC6531H: return "SC6531H";
                case SC6533G: return "SC6533G";
                case SC6533GF: return "SC6533GF";
                case SC6600: return "SC6600";
                case SC6600L: return "SC6600L";
                case SC6610: return "SC6610";
                case SC6620: return "SC6620";
                case SC6800H: return "SC6800H";
                
                // ========== SC77xx 系列 ==========
                case SC7701: return "SC7701";
                case SC7702: return "SC7702";
                case SC7710: return "SC7710";
                case SC7715: return "SC7715";
                case SC7715A: return "SC7715A";
                case SC7720: return "SC7720";
                case SC7727S: return "SC7727S";
                case SC7730: return "SC7730";
                case SC7730A: return "SC7730A";
                case SC7730S: return "SC7730S";
                case SC7731: return "SC7731";
                case SC7731C: return "SC7731C";
                case SC7731E: return "SC7731E";
                case SC7731G: return "SC7731G";
                case SC7731GF: return "SC7731GF";
                case SC7731EF: return "SC7731EF";
                
                // ========== SC85xx 系列 ==========
                case SC8521E: return "SC8521E";
                case SC8541E: return "SC8541E";
                case SC8541EF: return "SC8541EF";
                case SC8551: return "SC8551";
                case SC8551E: return "SC8551E";
                case SC8581: return "SC8581";
                case SC8581A: return "SC8581A";
                
                // ========== SC96xx/SC98xx 系列 ==========
                case SC9600: return "SC9600";
                case SC9610: return "SC9610";
                case SC9620: return "SC9620";
                case SC9630: return "SC9630";
                case SC9820: return "SC9820";
                case SC9820A: return "SC9820A";
                case SC9820E: return "SC9820E";
                case SC9830: return "SC9830";
                case SC9830A: return "SC9830A";
                case SC9830I: return "SC9830i";
                case SC9830IA: return "SC9830iA";
                case SC9832: return "SC9832";
                case SC9832A: return "SC9832A";
                case SC9832E: return "SC9832E";
                case SC9832EP: return "SC9832EP";
                case SC9850: return "SC9850";
                case SC9850KA: return "SC9850KA";
                case SC9850KH: return "SC9850KH";
                case SC9850S: return "SC9850S";
                case SC9853I: return "SC9853i";
                case SC9860: return "SC9860";
                case SC9860G: return "SC9860G";
                case SC9860GV: return "SC9860GV";
                case SC9861: return "SC9861";
                case SC9863: return "SC9863A";
                case SC9863A: return "SC9863A";
                
                // ========== T 系列 4G ==========
                case T310: return "T310";
                case T606: return "T606";
                case T610: return "T610";
                case T612: return "T612";
                case T616: return "T616";
                case T618: return "T618";
                case T700: return "T700";
                case T760: return "T760";
                case T770: return "T770";
                case T820: return "T820";
                case T900: return "T900";
                
                // ========== T 系列 5G ==========
                case T740: return "T740 (5G)";
                case T750: return "T750 (5G)";
                case T765: return "T765 (5G)";
                case T7510: return "T7510 (5G)";
                case T7520: return "T7520 (5G)";
                case T7525: return "T7525 (5G)";
                case T7530: return "T7530 (5G)";
                case T7560: return "T7560 (5G)";
                case T7570: return "T7570 (5G)";
                case T8000: return "T8000 (5G)";
                case T8200: return "T8200 (5G)";
                
                // ========== T1xx 系列 (4G 功能机) ==========
                case T107: return "T107/UMS9107 (4G 功能机)";
                case T117: return "T117/UMS9117 (4G 功能机)";
                case T127: return "T127/UMS9127 (4G 功能机)";
                case UMS9107: return "UMS9107 (T107)";
                case UMS9117: return "UMS9117 (T117)";
                case UMS9127: return "UMS9127 (T127)";
                
                // ========== UMS 系列 ==========
                case UMS312: return "UMS312 (T310)";
                case UMS512: return "UMS512 (T618)";
                case UMS9230: return "UMS9230 (T606)";
                case UMS9620: return "UMS9620 (T740)";
                case UMS9621: return "UMS9621";
                
                // ========== UIS 系列 ==========
                case UIS7862: return "UIS7862";
                case UIS7863: return "UIS7863";
                case UIS7870: return "UIS7870";
                case UIS7885: return "UIS7885";
                case UIS8581: return "UIS8581";
                case UIS8910DM: return "UIS8910DM";
                
                // ========== W 系列 ==========
                case W117: return "W117";
                case W217: return "W217";
                case W307: return "W307";
                
                // ========== UWS 系列 ==========
                case UWS6121: return "UWS6121";
                case UWS6122: return "UWS6122";
                case UWS6131: return "UWS6131";
                case UWS6152: return "UWS6152";
                
                default:
                    // 尝试匹配基础 ID
                    if (baseId == 0x6531) return string.Format("SC6531 (0x{0:X})", chipId);
                    if (baseId == 0x6533) return string.Format("SC6533 (0x{0:X})", chipId);
                    if (baseId == 0x7731) return string.Format("SC7731 (0x{0:X})", chipId);
                    if (baseId == 0x7730) return string.Format("SC7730 (0x{0:X})", chipId);
                    if (baseId == 0x8541) return string.Format("SC8541 (0x{0:X})", chipId);
                    if (baseId == 0x9820) return string.Format("SC9820 (0x{0:X})", chipId);
                    if (baseId == 0x9830) return string.Format("SC9830 (0x{0:X})", chipId);
                    if (baseId == 0x9832) return string.Format("SC9832 (0x{0:X})", chipId);
                    if (baseId == 0x9850) return string.Format("SC9850 (0x{0:X})", chipId);
                    if (baseId == 0x9860) return string.Format("SC9860 (0x{0:X})", chipId);
                    if (baseId == 0x9863) return string.Format("SC9863 (0x{0:X})", chipId);
                    return string.Format("Unknown (0x{0:X})", chipId);
            }
        }

        /// <summary>
        /// 获取 FDL1 默认加载地址
        /// 参考: spd_dump.c, SPRDClientCore, iReverse 项目
        /// </summary>
        public static uint GetFdl1Address(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            
            switch (baseId)
            {
                // ========== 需要 Exploit 的新平台 (0x65000800) ==========
                case 0x9863:  // SC9863A
                case 0x8581:  // SC8581A
                case 0x9853:  // SC9853i
                    return 0x65000800;
                
                // ========== SC9850/SC9860/SC9861 系列 (0x65000000) ==========
                case 0x9850:  // SC9850K
                case 0x9860:  // SC9860G
                case 0x9861:  // SC9861
                    return 0x65000000;
                    
                // ========== T6xx/T7xx 系列需要 Exploit (0x65000800) ==========
                case 0x0610:  // T610
                case 0x0612:  // T612
                case 0x0616:  // T616
                case 0x0618:  // T618
                case 0x0512:  // UMS512
                case 0x0700:  // T700
                case 0x0760:  // T760
                case 0x0770:  // T770
                    return 0x65000800;
                    
                // ========== 标准新平台 (0x5500) ==========
                case 0x8521:  // SC8521E
                case 0x8541:  // SC8541E
                case 0x8551:  // SC8551
                case 0x9832:  // SC9832E
                case 0x0310:  // T310
                case 0x0312:  // UMS312
                case 0x0606:  // T606
                case 0x9230:  // UMS9230
                case 0x0820:  // T820
                case 0x0900:  // T900
                case 0x0740:  // T740
                case 0x0750:  // T750
                case 0x0765:  // T765
                case 0x7510:  // T7510
                case 0x7520:  // T7520
                case 0x7525:  // T7525
                case 0x7530:  // T7530
                case 0x7560:  // T7560
                case 0x7570:  // T7570
                case 0x8000:  // T8000
                case 0x8200:  // T8200
                    return 0x5500;
                    
                // ========== 功能机平台 (0x40004000) ==========
                case 0x6500:  // SC6500
                case 0x6530:  // SC6530
                case 0x6531:  // SC6531E
                case 0x6533:  // SC6533G
                case 0x6600:  // SC6600
                case 0x6610:  // SC6610
                case 0x6620:  // SC6620
                case 0x6800:  // SC6800H
                    return 0x40004000;  // 功能机 FDL1 地址
                    
                // ========== W 系列功能机 (0x40004000) ==========
                // 注意: W117 实际上就是 T117，使用 T1xx 系列地址
                case 0x0217:  // W217
                case 0x0307:  // W307
                    return 0x40004000;
                    
                // ========== T1xx 4G 功能机 (0x6200) - 参考 spreadtrum_flash ==========
                case 0x0107:  // T107/UMS9107
                case 0x0117:  // T117/UMS9117 (也叫 W117)
                case 0x0127:  // T127/UMS9127
                case 0x9107:  // UMS9107
                case 0x9117:  // UMS9117
                case 0x9127:  // UMS9127
                    return 0x6200;
                    
                // ========== UWS 可穿戴系列 (0x5500) ==========
                case 0x6121:  // UWS6121
                case 0x6122:  // UWS6122
                case 0x6131:  // UWS6131
                case 0x6152:  // UWS6152
                    return 0x5500;
                    
                // ========== UIS IoT 系列 (0x5500) ==========
                case 0x7862:  // UIS7862
                case 0x7863:  // UIS7863
                case 0x7870:  // UIS7870
                case 0x7885:  // UIS7885
                case 0x8910:  // UIS8910DM
                    return 0x5500;
                    
                // ========== 传统平台 (0x5000) ==========
                case 0x7701:  // SC7701
                case 0x7702:  // SC7702
                case 0x7710:  // SC7710
                case 0x7715:  // SC7715
                case 0x7720:  // SC7720
                case 0x7727:  // SC7727S
                case 0x7730:  // SC7730
                case 0x7731:  // SC7731
                case 0x9600:  // SC9600
                case 0x9610:  // SC9610
                case 0x9620:  // SC9620
                case 0x9630:  // SC9630
                case 0x9820:  // SC9820
                case 0x9830:  // SC9830
                default:
                    return 0x5000;  // 传统平台
            }
        }

        /// <summary>
        /// 获取 FDL2 默认加载地址
        /// 参考: spd_dump.c, SPRDClientCore, iReverse 项目
        /// </summary>
        public static uint GetFdl2Address(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            
            switch (baseId)
            {
                // ========== SC8541E / SC9832E / SC9863A / T6xx 系列 (0x9EFFFE00) ==========
                case 0x8521:  // SC8521E
                case 0x8541:  // SC8541E
                case 0x8551:  // SC8551
                case 0x8581:  // SC8581A
                case 0x9832:  // SC9832E
                case 0x9853:  // SC9853i
                case 0x9863:  // SC9863A
                case 0x0310:  // T310
                case 0x0312:  // UMS312
                case 0x0606:  // T606
                case 0x9230:  // UMS9230
                case 0x0610:  // T610
                case 0x0612:  // T612
                case 0x0616:  // T616
                case 0x0618:  // T618
                case 0x0512:  // UMS512
                    return 0x9EFFFE00;
                    
                // ========== T7xx 需要 Exploit 系列 (0xB4FFFE00) ==========
                case 0x0700:  // T700
                case 0x0760:  // T760
                case 0x0770:  // T770
                    return 0xB4FFFE00;
                    
                // ========== T8xx / T9xx / 5G 系列 (0x9F000000) ==========
                case 0x0820:  // T820
                case 0x0900:  // T900
                case 0x0740:  // T740
                case 0x0750:  // T750
                case 0x0765:  // T765
                case 0x7510:  // T7510
                case 0x7520:  // T7520
                case 0x7525:  // T7525
                case 0x7530:  // T7530
                case 0x7560:  // T7560
                case 0x7570:  // T7570
                case 0x8000:  // T8000
                case 0x8200:  // T8200
                    return 0x9F000000;
                    
                // ========== SC9850/SC9860/SC9861 系列 (0x8C800000) ==========
                case 0x9850:  // SC9850K
                case 0x9860:  // SC9860G
                case 0x9861:  // SC9861
                    return 0x8C800000;
                    
                // ========== 传统平台 SC77xx / SC98xx (0x8A800000) ==========
                case 0x7701:  // SC7701
                case 0x7702:  // SC7702
                case 0x7710:  // SC7710
                case 0x7715:  // SC7715
                case 0x7720:  // SC7720
                case 0x7727:  // SC7727S
                case 0x7730:  // SC7730
                case 0x7731:  // SC7731
                case 0x9600:  // SC9600
                case 0x9610:  // SC9610
                case 0x9630:  // SC9630
                case 0x9820:  // SC9820
                case 0x9830:  // SC9830
                    return 0x8A800000;
                    
                // ========== 功能机平台 (0x14000000) ==========
                case 0x6500:  // SC6500
                case 0x6530:  // SC6530
                case 0x6531:  // SC6531E
                case 0x6533:  // SC6533G
                case 0x6600:  // SC6600
                case 0x6610:  // SC6610
                case 0x6620:  // SC6620
                case 0x6800:  // SC6800H
                // 注意: W117 实际上就是 T117，使用 T1xx 系列地址
                case 0x0217:  // W217
                case 0x0307:  // W307
                    return 0x14000000;
                    
                // ========== T1xx 4G 功能机 (0x80100000) - 参考 spreadtrum_flash ==========
                case 0x0107:  // T107/UMS9107
                case 0x0117:  // T117/UMS9117 (也叫 W117)
                case 0x0127:  // T127/UMS9127
                case 0x9107:  // UMS9107
                case 0x9117:  // UMS9117
                case 0x9127:  // UMS9127
                    return 0x80100000;
                    
                // ========== UWS 可穿戴系列 (0x9EFFFE00) ==========
                case 0x6121:  // UWS6121
                case 0x6122:  // UWS6122
                case 0x6131:  // UWS6131
                case 0x6152:  // UWS6152
                    return 0x9EFFFE00;
                    
                // ========== UIS IoT 系列 (0x9EFFFE00) ==========
                case 0x7862:  // UIS7862
                case 0x7863:  // UIS7863
                case 0x7870:  // UIS7870
                case 0x7885:  // UIS7885
                case 0x8910:  // UIS8910DM
                    return 0x9EFFFE00;
                    
                default:
                    return 0x9EFFFE00;  // 默认地址 (适用于大多数新平台)
            }
        }
        
        /// <summary>
        /// 获取 exec_addr (用于绕过签名验证)
        /// 参考: spd_dump 源码中的 custom_exec_no_verify 机制
        /// 这些地址是 BROM 中验证函数的地址，通过写入返回成功的代码来绕过验证
        /// </summary>
        public static uint GetExecAddress(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            
            switch (baseId)
            {
                // ========== SC9863A 系列 ==========
                // 参考: spd_dump exec_addr 0x65012f48
                case 0x9863:  // SC9863A
                    return 0x65012f48;
                    
                // ========== T6xx 系列 (UMS512) ==========
                // 这些芯片使用类似的 BROM，exec_addr 相同
                case 0x0610:  // T610
                case 0x0612:  // T612
                case 0x0616:  // T616
                case 0x0618:  // T618
                case 0x0512:  // UMS512
                    return 0x65012f48;  // 与 SC9863A 相同
                    
                // ========== SC9853i ==========
                case 0x9853:  // SC9853i
                    return 0x65012f48;
                    
                // ========== SC8581A ==========
                case 0x8581:  // SC8581A
                    return 0x65012f48;
                    
                // ========== SC9850/SC9860 系列 ==========
                // 这些较旧的芯片可能使用不同的地址
                case 0x9850:  // SC9850K
                case 0x9860:  // SC9860G
                case 0x9861:  // SC9861
                    return 0x65012000;  // 需要验证
                    
                // ========== T7xx 系列 (需要 Exploit) ==========
                // 已验证: T760 使用 exec_addr 0x65012f48
                case 0x0700:  // T700
                case 0x0760:  // T760 ✓ 已验证
                case 0x0770:  // T770
                    return 0x65012f48;
                    
                // ========== 5G 系列 ==========
                case 0x0740:  // T740
                case 0x0750:  // T750
                case 0x0765:  // T765
                case 0x7510:  // T7510
                case 0x7520:  // T7520
                case 0x7525:  // T7525
                case 0x7530:  // T7530
                case 0x7560:  // T7560
                case 0x7570:  // T7570
                case 0x8000:  // T8000
                case 0x8200:  // T8200
                    return 0;  // 5G 平台可能不需要 exec bypass
                    
                // ========== 功能机平台 (不需要) ==========
                case 0x6500:  // SC6500
                case 0x6530:  // SC6530
                case 0x6531:  // SC6531E
                case 0x6533:  // SC6533G
                case 0x6600:  // SC6600
                case 0x6610:  // SC6610
                case 0x6620:  // SC6620
                case 0x6800:  // SC6800H
                    return 0;  // 功能机不需要 exec bypass
                    
                // ========== T1xx 4G 功能机 ==========
                case 0x0107:  // T107
                case 0x0117:  // T117
                case 0x0127:  // T127
                case 0x9107:  // UMS9107
                case 0x9117:  // UMS9117
                case 0x9127:  // UMS9127
                    return 0;  // 4G 功能机通常不需要
                    
                // ========== 传统平台 (不需要) ==========
                case 0x7701:  // SC7701
                case 0x7702:  // SC7702
                case 0x7710:  // SC7710
                case 0x7715:  // SC7715
                case 0x7720:  // SC7720
                case 0x7727:  // SC7727S
                case 0x7730:  // SC7730
                case 0x7731:  // SC7731
                case 0x9600:  // SC9600
                case 0x9610:  // SC9610
                case 0x9620:  // SC9620
                case 0x9630:  // SC9630
                case 0x9820:  // SC9820
                case 0x9830:  // SC9830
                case 0x9832:  // SC9832E
                    return 0;  // 传统平台不需要
                    
                // ========== UWS 可穿戴 ==========
                case 0x6121:  // UWS6121
                case 0x6122:  // UWS6122
                case 0x6131:  // UWS6131
                case 0x6152:  // UWS6152
                    return 0;  // 可穿戴平台不需要
                    
                default:
                    // 对于未知芯片，如果 FDL1 地址是 0x65000800，则可能需要 exec bypass
                    if (GetFdl1Address(chipId) == 0x65000800)
                    {
                        return 0x65012f48;  // 默认尝试
                    }
                    return 0;  // 默认不使用 exec bypass
            }
        }
        
        /// <summary>
        /// 检查芯片是否需要 exec_no_verify 绕过
        /// </summary>
        public static bool NeedsExecBypass(uint chipId)
        {
            return GetExecAddress(chipId) > 0;
        }
        
        /// <summary>
        /// 根据芯片 ID 判断是否为 5G 平台
        /// </summary>
        public static bool Is5GPlatform(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            switch (baseId)
            {
                case 0x0740:  // T740
                case 0x0750:  // T750
                case 0x0765:  // T765
                case 0x7510:  // T7510
                case 0x7520:  // T7520
                case 0x7525:  // T7525
                case 0x7530:  // T7530
                case 0x7560:  // T7560
                case 0x7570:  // T7570
                case 0x8000:  // T8000
                case 0x8200:  // T8200
                case 0x9620:  // UMS9620
                case 0x9621:  // UMS9621
                    return true;
                default:
                    return false;
            }
        }
        
        /// <summary>
        /// 根据芯片 ID 判断存储类型
        /// </summary>
        public static string GetStorageType(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            
            switch (baseId)
            {
                // ========== UFS 存储 (高端 5G 平台) ==========
                case 0x0820:  // T820
                case 0x0900:  // T900
                case 0x0740:  // T740
                case 0x0750:  // T750
                case 0x0765:  // T765
                case 0x7510:  // T7510
                case 0x7520:  // T7520
                case 0x7525:  // T7525
                case 0x7530:  // T7530
                case 0x7560:  // T7560
                case 0x7570:  // T7570
                case 0x8000:  // T8000
                case 0x8200:  // T8200
                case 0x9620:  // UMS9620
                case 0x9621:  // UMS9621
                    return "UFS";
                    
                // ========== NOR/NAND 存储 (功能机) ==========
                case 0x6500:  // SC6500
                case 0x6530:  // SC6530
                case 0x6531:  // SC6531E
                case 0x6533:  // SC6533G
                case 0x6600:  // SC6600
                case 0x6610:  // SC6610
                case 0x6620:  // SC6620
                case 0x6800:  // SC6800H
                case 0x0117:  // W117
                case 0x0217:  // W217
                case 0x0307:  // W307
                    return "NOR/NAND";
                    
                // ========== SPI NOR (可穿戴设备) ==========
                case 0x6121:  // UWS6121
                case 0x6122:  // UWS6122
                case 0x6131:  // UWS6131
                case 0x6152:  // UWS6152
                    return "SPI NOR";
                    
                // ========== eMMC 存储 (默认) ==========
                default:
                    return "eMMC";
            }
        }
        
        /// <summary>
        /// 判断是否为功能机平台
        /// </summary>
        public static bool IsFeaturePhonePlatform(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            switch (baseId)
            {
                case 0x6500:  // SC6500
                case 0x6530:  // SC6530
                case 0x6531:  // SC6531E
                case 0x6533:  // SC6533G
                case 0x6600:  // SC6600
                case 0x6610:  // SC6610
                case 0x6620:  // SC6620
                case 0x6800:  // SC6800H
                case 0x0117:  // W117
                case 0x0217:  // W217
                case 0x0307:  // W307
                    return true;
                default:
                    return false;
            }
        }
        
        /// <summary>
        /// 判断是否为可穿戴平台
        /// </summary>
        public static bool IsWearablePlatform(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            switch (baseId)
            {
                case 0x6121:  // UWS6121
                case 0x6122:  // UWS6122
                case 0x6131:  // UWS6131
                case 0x6152:  // UWS6152
                    return true;
                default:
                    return false;
            }
        }
        
        /// <summary>
        /// 判断是否为 IoT 平台
        /// </summary>
        public static bool IsIoTPlatform(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            switch (baseId)
            {
                case 0x7862:  // UIS7862
                case 0x7863:  // UIS7863
                case 0x7870:  // UIS7870
                case 0x7885:  // UIS7885
                case 0x8910:  // UIS8910DM
                    return true;
                default:
                    return false;
            }
        }
        
        /// <summary>
        /// 判断是否需要 Exploit 才能下载 FDL
        /// </summary>
        public static bool RequiresExploit(uint chipId)
        {
            uint baseId = chipId > 0xFFFF ? (chipId >> 16) : chipId;
            switch (baseId)
            {
                case 0x9850:  // SC9850K
                case 0x9853:  // SC9853i
                case 0x9860:  // SC9860G
                case 0x9861:  // SC9861
                case 0x9863:  // SC9863A
                case 0x8581:  // SC8581A
                case 0x0610:  // T610
                case 0x0612:  // T612
                case 0x0616:  // T616
                case 0x0618:  // T618
                case 0x0512:  // UMS512
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 展讯 USB VID/PID
    /// </summary>
    public static class SprdUsbIds
    {
        // Spreadtrum/Unisoc VID
        public const int VID_SPRD = 0x1782;
        public const int VID_UNISOC = 0x1782;
        
        // 下载模式 PID (标准)
        public const int PID_DOWNLOAD = 0x4D00;      // 标准下载模式
        public const int PID_DOWNLOAD_2 = 0x4D01;    // 下载模式变体
        public const int PID_DOWNLOAD_3 = 0x4D02;    // 下载模式变体2
        public const int PID_DOWNLOAD_4 = 0x4D03;    // 下载模式变体3
        public const int PID_U2S_DIAG = 0x4D00;      // U2S Diag (SPRD U2S Diag)
        
        // 下载模式 PID (新平台)
        public const int PID_UMS_DOWNLOAD = 0x5000;  // UMS 系列下载模式
        public const int PID_UWS_DOWNLOAD = 0x5001;  // UWS 系列下载模式
        public const int PID_T606_DOWNLOAD = 0x5002; // T606 下载模式
        
        // 诊断模式 PID
        public const int PID_DIAG = 0x4D10;          // 标准诊断模式
        public const int PID_DIAG_2 = 0x4D11;        // 诊断模式变体
        public const int PID_DIAG_3 = 0x4D14;        // 诊断模式变体2
        
        // ADB 模式 PID
        public const int PID_ADB = 0x4D12;           // ADB 模式
        public const int PID_ADB_2 = 0x4D13;         // ADB 模式变体
        
        // MTP 模式 PID
        public const int PID_MTP = 0x4D15;           // MTP 模式
        public const int PID_MTP_2 = 0x4D16;         // MTP 模式变体
        
        // CDC/ACM 模式 PID
        public const int PID_CDC = 0x4D20;           // CDC 模式
        public const int PID_ACM = 0x4D21;           // ACM 模式
        public const int PID_SERIAL = 0x4D22;        // 串口模式
        
        // 特殊 PID
        public const int PID_RNDIS = 0x4D30;         // RNDIS 网络模式
        public const int PID_FASTBOOT = 0x4D40;      // Fastboot 模式
        
        // ========== 其他厂商使用 Spreadtrum 芯片 ==========
        
        // Samsung
        public const int VID_SAMSUNG = 0x04E8;
        public const int PID_SAMSUNG_SPRD = 0x685D;  // Samsung Spreadtrum 下载
        public const int PID_SAMSUNG_SPRD_2 = 0x685C; // Samsung Spreadtrum 下载2
        public const int PID_SAMSUNG_DIAG = 0x6860;  // Samsung 诊断
        public const int PID_SAMSUNG_DIAG_2 = 0x6862; // Samsung 诊断2
        
        // Huawei
        public const int VID_HUAWEI = 0x12D1;
        public const int PID_HUAWEI_DOWNLOAD = 0x1001;
        public const int PID_HUAWEI_DOWNLOAD_2 = 0x1035;
        public const int PID_HUAWEI_DOWNLOAD_3 = 0x1C05;
        
        // ZTE
        public const int VID_ZTE = 0x19D2;
        public const int PID_ZTE_DOWNLOAD = 0x0016;
        public const int PID_ZTE_DOWNLOAD_2 = 0x0034;
        public const int PID_ZTE_DOWNLOAD_3 = 0x1403;
        public const int PID_ZTE_DIAG = 0x0117;
        public const int PID_ZTE_DIAG_2 = 0x0076;
        
        // Alcatel/TCL
        public const int VID_ALCATEL = 0x1BBB;
        public const int PID_ALCATEL_DOWNLOAD = 0x0536;
        public const int PID_ALCATEL_DOWNLOAD_2 = 0x0530;
        public const int PID_ALCATEL_DOWNLOAD_3 = 0x0510;
        
        // Lenovo
        public const int VID_LENOVO = 0x17EF;
        public const int PID_LENOVO_DOWNLOAD = 0x7890;
        
        // Realme/OPPO
        public const int VID_REALME = 0x22D9;
        public const int PID_REALME_DOWNLOAD = 0x2762;
        public const int PID_REALME_DOWNLOAD_2 = 0x2763;
        public const int PID_REALME_DOWNLOAD_3 = 0x2764;
        
        // Xiaomi (部分使用 Spreadtrum)
        public const int VID_XIAOMI = 0x2717;
        public const int PID_XIAOMI_DOWNLOAD = 0xFF48;
        
        // Nokia
        public const int VID_NOKIA = 0x0421;
        public const int PID_NOKIA_DOWNLOAD = 0x0600;
        public const int PID_NOKIA_DOWNLOAD_2 = 0x0601;
        public const int PID_NOKIA_DOWNLOAD_3 = 0x0602;
        
        // Infinix/Tecno/Itel (Transsion)
        public const int VID_TRANSSION = 0x2A47;
        public const int PID_TRANSSION_DOWNLOAD = 0x2012;
        public const int VID_TRANSSION_2 = 0x1782;
        
        /// <summary>
        /// 检查是否为展讯设备 VID
        /// </summary>
        public static bool IsSprdVid(int vid)
        {
            return vid == VID_SPRD ||
                   vid == VID_SAMSUNG ||
                   vid == VID_HUAWEI ||
                   vid == VID_ZTE ||
                   vid == VID_ALCATEL ||
                   vid == VID_LENOVO ||
                   vid == VID_REALME ||
                   vid == VID_XIAOMI ||
                   vid == VID_NOKIA;
        }
        
        /// <summary>
        /// 检查是否为下载模式 PID
        /// </summary>
        public static bool IsDownloadPid(int pid)
        {
            return pid == PID_DOWNLOAD ||
                   pid == PID_DOWNLOAD_2 ||
                   pid == PID_DOWNLOAD_3 ||
                   pid == PID_DOWNLOAD_4 ||
                   pid == PID_U2S_DIAG ||
                   pid == PID_UMS_DOWNLOAD ||
                   pid == PID_UWS_DOWNLOAD ||
                   pid == PID_T606_DOWNLOAD ||
                   pid == PID_CDC ||
                   pid == PID_ACM ||
                   pid == PID_SERIAL ||
                   pid == PID_SAMSUNG_SPRD ||
                   pid == PID_SAMSUNG_SPRD_2 ||
                   pid == PID_HUAWEI_DOWNLOAD ||
                   pid == PID_HUAWEI_DOWNLOAD_2 ||
                   pid == PID_HUAWEI_DOWNLOAD_3 ||
                   pid == PID_ZTE_DOWNLOAD ||
                   pid == PID_ZTE_DOWNLOAD_2 ||
                   pid == PID_ZTE_DOWNLOAD_3 ||
                   pid == PID_ALCATEL_DOWNLOAD ||
                   pid == PID_ALCATEL_DOWNLOAD_2 ||
                   pid == PID_ALCATEL_DOWNLOAD_3 ||
                   pid == PID_LENOVO_DOWNLOAD ||
                   pid == PID_REALME_DOWNLOAD ||
                   pid == PID_REALME_DOWNLOAD_2 ||
                   pid == PID_REALME_DOWNLOAD_3 ||
                   pid == PID_XIAOMI_DOWNLOAD ||
                   pid == PID_NOKIA_DOWNLOAD ||
                   pid == PID_NOKIA_DOWNLOAD_2 ||
                   pid == PID_NOKIA_DOWNLOAD_3 ||
                   pid == PID_TRANSSION_DOWNLOAD;
        }
        
        /// <summary>
        /// 检查是否为诊断模式 PID
        /// </summary>
        public static bool IsDiagPid(int pid)
        {
            return pid == PID_DIAG ||
                   pid == PID_DIAG_2 ||
                   pid == PID_DIAG_3 ||
                   pid == PID_SAMSUNG_DIAG ||
                   pid == PID_SAMSUNG_DIAG_2 ||
                   pid == PID_ZTE_DIAG ||
                   pid == PID_ZTE_DIAG_2;
        }
    }
}
