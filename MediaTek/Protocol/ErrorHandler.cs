// mtkclient port: error.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Collections.Generic;

namespace SakuraEDL.MediaTek.Protocol
{
    /// <summary>
    /// MTK error code handler — ported from mtkclient/Library/error.py
    /// </summary>
    public class ErrorHandler
    {
        private static readonly Dictionary<uint, string> ErrorCodes = new Dictionary<uint, string>
        {
            // OK
            { 0x0, "OK" },
            // COMMON (0x3E8 - 0x412)
            { 0x3E8, "STOP" },
            { 0x3E9, "UNDEFINED_ERROR" },
            { 0x3EA, "INVALID_ARGUMENTS" },
            { 0x3EB, "INVALID_BBCHIP_TYPE" },
            { 0x3EC, "INVALID_EXT_CLOCK" },
            { 0x3ED, "INVALID_BMTSIZE" },
            { 0x3EE, "GET_DLL_VER_FAIL" },
            { 0x3EF, "INVALID_BUF" },
            { 0x3F0, "BUF_IS_NULL" },
            { 0x3F1, "BUF_LEN_IS_ZERO" },
            { 0x3F2, "BUF_SIZE_TOO_SMALL" },
            { 0x3F3, "NOT_ENOUGH_STORAGE_SPACE" },
            { 0x3F4, "NOT_ENOUGH_MEMORY" },
            { 0x3F5, "COM_PORT_OPEN_FAIL" },
            { 0x3F6, "COM_PORT_SET_TIMEOUT_FAIL" },
            { 0x3F7, "COM_PORT_SET_STATE_FAIL" },
            { 0x3F8, "COM_PORT_PURGE_FAIL" },
            { 0x3F9, "FILEPATH_NOT_SPECIFIED_YET" },
            { 0x3FA, "UNKNOWN_TARGET_BBCHIP" },
            { 0x3FB, "SKIP_BBCHIP_HW_VER_CHECK" },
            { 0x3FC, "UNSUPPORTED_VER_OF_BOOT_ROM" },
            { 0x3FD, "UNSUPPORTED_VER_OF_BOOTLOADER" },
            { 0x3FE, "UNSUPPORTED_VER_OF_DA" },
            { 0x3FF, "UNSUPPORTED_VER_OF_SEC_INFO" },
            { 0x400, "UNSUPPORTED_VER_OF_ROM_INFO" },
            { 0x401, "SEC_INFO_NOT_FOUND" },
            { 0x402, "ROM_INFO_NOT_FOUND" },
            { 0x403, "CUST_PARA_NOT_SUPPORTED" },
            { 0x404, "CUST_PARA_WRITE_LEN_INCONSISTENT" },
            { 0x405, "SEC_RO_NOT_SUPPORTED" },
            { 0x406, "SEC_RO_WRITE_LEN_INCONSISTENT" },
            { 0x407, "ADDR_N_LEN_NOT_32BITS_ALIGNMENT" },
            { 0x408, "UART_CHKSUM_ERROR" },
            { 0x409, "EMMC_FLASH_BOOT" },
            { 0x40A, "NOR_FLASH_BOOT" },
            { 0x40B, "NAND_FLASH_BOOT" },
            { 0x40C, "UNSUPPORTED_VER_OF_EMI_INFO" },
            { 0x40D, "PART_NO_VALID_TABLE" },
            { 0x40E, "PART_NO_SPACE_FOUND" },
            { 0x40F, "UNSUPPORTED_VER_OF_SEC_CFG" },
            { 0x410, "UNSUPPORTED_OPERATION" },
            { 0x411, "CHKSUM_ERROR" },
            { 0x412, "TIMEOUT" },
            // BROM (0x7D0 - 0x800)
            { 0x7D0, "SET_META_REG_FAIL" },
            { 0x7D1, "SET_FLASHTOOL_REG_FAIL" },
            { 0x7D2, "SET_REMAP_REG_FAIL" },
            { 0x7D3, "SET_EMI_FAIL" },
            { 0x7D4, "DOWNLOAD_DA_FAIL" },
            { 0x7D5, "CMD_STARTCMD_FAIL" },
            { 0x7D6, "CMD_STARTCMD_TIMEOUT" },
            { 0x7D7, "CMD_JUMP_FAIL" },
            { 0x7D8, "CMD_WRITE16_MEM_FAIL" },
            { 0x7D9, "CMD_READ16_MEM_FAIL" },
            { 0x7DA, "CMD_WRITE16_REG_FAIL" },
            { 0x7DB, "CMD_READ16_REG_FAIL" },
            { 0x7DC, "CMD_CHKSUM16_MEM_FAIL" },
            { 0x7DD, "CMD_WRITE32_MEM_FAIL" },
            { 0x7DE, "CMD_READ32_MEM_FAIL" },
            { 0x7DF, "CMD_WRITE32_REG_FAIL" },
            { 0x7E0, "CMD_READ32_REG_FAIL" },
            { 0x7E1, "CMD_CHKSUM32_MEM_FAIL" },
            { 0x7E2, "JUMP_TO_META_MODE_FAIL" },
            { 0x7E3, "WR16_RD16_MEM_RESULT_DIFF" },
            { 0x7E4, "CHKSUM16_MEM_RESULT_DIFF" },
            { 0x7E5, "BBCHIP_HW_VER_INCORRECT" },
            { 0x7E6, "FAIL_TO_GET_BBCHIP_HW_VER" },
            { 0x7E7, "AUTOBAUD_FAIL" },
            { 0x7E8, "SPEEDUP_BAUDRATE_FAIL" },
            { 0x7E9, "LOCK_POWERKEY_FAIL" },
            { 0x7EA, "WM_APP_MSG_OUT_OF_RANGE" },
            { 0x7EB, "NOT_SUPPORT_MT6205B" },
            { 0x7EC, "EXCEED_MAX_DATA_BLOCKS" },
            { 0x7ED, "EXTERNAL_SRAM_DETECTION_FAIL" },
            { 0x7EE, "EXTERNAL_DRAM_DETECTION_FAIL" },
            { 0x7EF, "GET_FW_VER_FAIL" },
            { 0x7F0, "CONNECT_TO_BOOTLOADER_FAIL" },
            { 0x7F1, "CMD_SEND_DA_FAIL" },
            { 0x7F2, "CMD_SEND_DA_CHKSUM_DIFF" },
            { 0x7F3, "CMD_JUMP_DA_FAIL" },
            { 0x7F4, "CMD_JUMP_BL_FAIL" },
            { 0x7F5, "EFUSE_REG_NO_MATCH_WITH_TARGET" },
            { 0x7F6, "EFUSE_WRITE_TIMEOUT" },
            { 0x7F7, "EFUSE_DATA_PROCESS_ERROR" },
            { 0x7F8, "EFUSE_BLOW_ERROR" },
            { 0x7F9, "EFUSE_ALREADY_BROKEN" },
            { 0x7FA, "EFUSE_BLOW_PARTIAL" },
            { 0x7FB, "SEC_VER_FAIL" },
            { 0x7FC, "PL_SEC_VER_FAIL" },
            { 0x7FD, "SET_WATCHDOG_FAIL" },
            { 0x7FE, "EFUSE_VALUE_IS_NOT_ZERO" },
            { 0x7FF, "EFUSE_WRITE_TIMEOUT_WITHOUT_EFUSE_VERIFY" },
            { 0x800, "EFUSE_UNKNOW_EXCEPTION_WITHOUT_EFUSE_VERIFY" },
            // DA error (0xBB8+)
            { 0xBB8, "INT_RAM_ERROR" },
            { 0xBB9, "EXT_RAM_ERROR" },
            { 0xBBA, "SETUP_DRAM_FAIL" },
            { 0xBBB, "SETUP_PLL_ERR" },
            { 0xBBC, "SETUP_EMI_PLL_ERR" },
            { 0xBBD, "DRAM_ABNORMAL_TYPE_SETTING" },
            { 0xBBE, "DRAMC_RANK0_CALIBRATION_FAILED" },
            { 0xBBF, "DRAMC_RANK1_CALIBRATION_FAILED" },
            { 0xBC0, "DRAM_NOT_SUPPORT" },
            { 0xBC1, "RAM_FLOARTING" },
            { 0xBC2, "RAM_UNACCESSABLE" },
            { 0xBC3, "RAM_ERROR" },
            { 0xBC4, "DEVICE_NOT_FOUND" },
            { 0xBC5, "NOR_UNSUPPORTED_DEV_ID" },
            { 0xBC6, "NAND_UNSUPPORTED_DEV_ID" },
            { 0xBC7, "NOR_FLASH_NOT_FOUND" },
            { 0xBC8, "NAND_FLASH_NOT_FOUND" },
            { 0xBC9, "SOC_CHECK_FAIL" },
            { 0xBCA, "NOR_PROGRAM_FAILED" },
            { 0xBCB, "NOR_ERASE_FAILED" },
            // DA SLA / Security
            { 0x7015, "DA_SLA_SIGNATURE_VERIFY_FAIL" },
            { 0x7017, "DA_SLA_SECURITY_ERROR" },
        };

        public string StatusToString(uint status)
        {
            if (ErrorCodes.TryGetValue(status, out string msg))
                return msg;
            return $"UNKNOWN_ERROR_0x{status:X}";
        }

        public bool IsOk(uint status)
        {
            return status == 0;
        }
    }
}
