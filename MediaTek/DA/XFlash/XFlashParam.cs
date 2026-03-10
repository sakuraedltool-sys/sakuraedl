// mtkclient port: DA/xflash/xflash_param.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;

namespace SakuraEDL.MediaTek.DA.XFlash
{
    public static class XFlashCmd
    {
        public const uint MAGIC = 0xFEEEEEEF;
        public const uint SYNC_SIGNAL = 0x434E5953; // "SYNC"

        // Data operations (0x01xxxx)
        public const uint UNKNOWN = 0x010000;
        public const uint DOWNLOAD = 0x010001;
        public const uint UPLOAD = 0x010002;
        public const uint FORMAT = 0x010003;
        public const uint WRITE_DATA = 0x010004;
        public const uint READ_DATA = 0x010005;
        public const uint FORMAT_PARTITION = 0x010006;
        public const uint SHUTDOWN = 0x010007;
        public const uint BOOT_TO = 0x010008;
        public const uint DEVICE_CTRL = 0x010009;
        public const uint INIT_EXT_RAM = 0x01000A;
        public const uint SWITCH_USB_SPEED = 0x01000B;
        public const uint READ_OTP_ZONE = 0x01000C;
        public const uint WRITE_OTP_ZONE = 0x01000D;
        public const uint WRITE_EFUSE = 0x01000E;
        public const uint READ_EFUSE = 0x01000F;
        public const uint NAND_BMT_REMARK = 0x010010;
        public const uint SETUP_ENVIRONMENT = 0x010100;
        public const uint SETUP_HW_INIT_PARAMS = 0x010101;

        // Settings (0x02xxxx)
        public const uint SET_BMT_PERCENTAGE = 0x020001;
        public const uint SET_BATTERY_OPT = 0x020002;
        public const uint SET_CHECKSUM_LEVEL = 0x020003;
        public const uint SET_RESET_KEY = 0x020004;
        public const uint SET_HOST_INFO = 0x020005;
        public const uint SET_META_BOOT_MODE = 0x020006;
        public const uint SET_EMMC_HWRESET_PIN = 0x020007;
        public const uint SET_GENERATE_GPX = 0x020008;
        public const uint SET_REGISTER_VALUE = 0x020009;
        public const uint SET_EXTERNAL_SIG = 0x02000A;
        public const uint SET_REMOTE_SEC_POLICY = 0x02000B;
        public const uint SET_ALL_IN_ONE_SIG = 0x02000C;
        public const uint SET_RSC_INFO = 0x02000D;
        public const uint SET_UPDATE_FW = 0x020010;
        public const uint SET_UFS_CONFIG = 0x020011;

        // Get info (0x04xxxx)
        public const uint GET_EMMC_INFO = 0x040001;
        public const uint GET_NAND_INFO = 0x040002;
        public const uint GET_NOR_INFO = 0x040003;
        public const uint GET_UFS_INFO = 0x040004;
        public const uint GET_DA_VERSION = 0x040005;
        public const uint GET_EXPIRE_DATA = 0x040006;
        public const uint GET_PACKET_LENGTH = 0x040007;
        public const uint GET_RANDOM_ID = 0x040008;
        public const uint GET_PARTITION_TBL_CATA = 0x040009;
        public const uint GET_CONNECTION_AGENT = 0x04000A;
        public const uint GET_USB_SPEED = 0x04000B;
        public const uint GET_RAM_INFO = 0x04000C;
        public const uint GET_CHIP_ID = 0x04000D;
        public const uint GET_OTP_LOCK_STATUS = 0x04000E;
        public const uint GET_BATTERY_VOLTAGE = 0x04000F;
        public const uint GET_RPMB_STATUS = 0x040010;
        public const uint GET_EXPIRE_DATE = 0x040011;
        public const uint GET_DRAM_TYPE = 0x040012;
        public const uint GET_DEV_FW_INFO = 0x040013;
        public const uint GET_HRID = 0x040014;
        public const uint GET_ERROR_DETAIL = 0x040015;
        public const uint SLA_ENABLED_STATUS = 0x040016;

        // Action (0x08xxxx)
        public const uint START_DL_INFO = 0x080001;
        public const uint END_DL_INFO = 0x080002;
        public const uint ACT_LOCK_OTP_ZONE = 0x080003;
        public const uint DISABLE_EMMC_HWRESET_PIN = 0x080004;
        public const uint CC_OPTIONAL_DOWNLOAD_ACT = 0x800005;
        public const uint DA_STOR_LIFE_CYCLE_CHECK = 0x080007;

        // Control (0x0Exxxx)
        public const uint UNKNOWN_CTRL_CODE = 0x0E0000;
        public const uint CTRL_STORAGE_TEST = 0x0E0001;
        public const uint CTRL_RAM_TEST = 0x0E0002;
        public const uint DEVICE_CTRL_READ_REGISTER = 0x0E0003;
    }

    public enum ChecksumAlgorithm
    {
        PLAIN = 0,
        CRC32 = 1,
        MD5 = 2
    }

    public enum FtSystemOSE
    {
        OS_WIN = 0,
        OS_LINUX = 1
    }

    public enum XFlashDataType : uint
    {
        DT_PROTOCOL_FLOW = 1,
        DT_MESSAGE = 2
    }
}
