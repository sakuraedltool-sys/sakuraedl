// mtkclient port: DA/storage.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;

namespace SakuraEDL.MediaTek.DA
{
    public enum DaStorage : int
    {
        MTK_DA_STORAGE_EMMC = 1,
        MTK_DA_STORAGE_SDMMC = 2,
        MTK_DA_STORAGE_UFS = 3,
        MTK_DA_STORAGE_NAND = 4,
        MTK_DA_STORAGE_NOR = 5,
        MTK_DA_STORAGE_NAND_SLC = 0x0101,
        MTK_DA_STORAGE_NAND_MLC = 0x0102,
        MTK_DA_STORAGE_NAND_TLC = 0x0103,
        MTK_DA_STORAGE_NAND_AMLC = 0x0104,
        MTK_DA_STORAGE_NAND_SPI = 0x0105,
        MTK_DA_STORAGE_NOR_SERIAL = 0x0201,
        MTK_DA_STORAGE_NOR_PARALLEL = 0x0202
    }

    public enum EmmcPartitionType : int
    {
        MTK_DA_EMMC_PART_BOOT1 = 1,
        MTK_DA_EMMC_PART_BOOT2 = 2,
        MTK_DA_EMMC_PART_RPMB = 3,
        MTK_DA_EMMC_PART_GP1 = 4,
        MTK_DA_EMMC_PART_GP2 = 5,
        MTK_DA_EMMC_PART_GP3 = 6,
        MTK_DA_EMMC_PART_GP4 = 7,
        MTK_DA_EMMC_PART_USER = 8
    }

    public enum UFSPartitionType : int
    {
        LU0 = 0,
        LU1 = 1,
        LU2 = 2,
        RPMB = 3,
        USER = 2,  // = LU2
        BOOT1 = 0, // = LU0
        BOOT2 = 1  // = LU1
    }

    public class EmmcInfo
    {
        public uint Type;
        public uint BlockSize;
        public ulong Boot1Size;
        public ulong Boot2Size;
        public ulong RpmbSize;
        public ulong Gp1Size;
        public ulong Gp2Size;
        public ulong Gp3Size;
        public ulong Gp4Size;
        public ulong UserSize;
        public byte[] Cid;
        public ulong FwVer;
        public string Name = "";
    }

    public class UfsInfo
    {
        public uint Type;
        public uint BlockSize;
        public ulong Lu0Size;
        public ulong Lu1Size;
        public ulong Lu2Size;
        public ulong Lu3Size;
        public byte[] Cid;
        public ulong FwVer;
        public uint SerialNumber;
        public string Name = "";
    }

    public class NandInfo
    {
        public uint Type;
        public uint PageSize;
        public uint BlockSize;
        public uint SpareSize;
        public ulong TotalSize;
        public byte[] Id;
        public string Name = "";
    }

    public class NorInfo
    {
        public uint Type;
        public uint PageSize;
        public ulong TotalSize;
        public ulong AvailableSize;
        public string Name = "";
    }

    public class RamInfo
    {
        public uint Type;
        public ulong BaseAddr;
        public ulong Size;
    }

    public class StorageInfo
    {
        public string FlashType = "emmc";
        public ulong FlashSize;
        public EmmcInfo Emmc;
        public UfsInfo Ufs;
        public NandInfo Nand;
        public NorInfo Nor;
        public RamInfo Sram;
        public RamInfo Dram;

        public void SetFlashSize()
        {
            if (FlashType == "emmc" && Emmc != null)
            {
                FlashSize = Math.Max(Math.Max(Math.Max(Math.Max(
                    Emmc.Gp1Size, Emmc.Gp2Size), Emmc.Gp3Size), Emmc.Gp4Size), Emmc.UserSize);
            }
            else if (FlashType == "ufs" && Ufs != null)
            {
                FlashSize = Math.Max(Math.Max(Math.Max(
                    Ufs.Lu0Size, Ufs.Lu1Size), Ufs.Lu2Size), Ufs.Lu3Size);
            }
            else if (FlashType == "nand" && Nand != null)
            {
                FlashSize = Nand.TotalSize;
            }
            else if (FlashType == "nor" && Nor != null)
            {
                FlashSize = Nor.AvailableSize;
            }
        }
    }
}
