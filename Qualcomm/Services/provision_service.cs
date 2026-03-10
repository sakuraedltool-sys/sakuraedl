// ============================================================================
// SakuraEDL - Provision Service | 存储配置服务
// ============================================================================
// [ZH] 存储配置 - 解析和生成 UFS/eMMC provision.xml 配置
// [EN] Provision Service - Parse and generate UFS/eMMC provision.xml
// [JA] プロビジョニング - UFS/eMMC provision.xmlの解析と生成
// [KO] 프로비저닝 서비스 - UFS/eMMC provision.xml 분석 및 생성
// [RU] Сервис Provision - Разбор и генерация provision.xml для UFS/eMMC
// [ES] Servicio Provision - Análisis y generación de provision.xml UFS/eMMC
// ============================================================================
// ⚠️ Warning: Provisioning is irreversible! | 警告: 操作不可逆!
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace SakuraEDL.Qualcomm.Services
{
    #region 数据模型

    /// <summary>
    /// UFS 全局配置
    /// </summary>
    public class UfsGlobalConfig
    {
        /// <summary>
        /// 启动启用 (bBootEnable)
        /// </summary>
        public bool BootEnable { get; set; } = true;

        /// <summary>
        /// 写保护类型
        /// 0 = None, 1 = Power On Write Protect, 2 = Permanent Write Protect
        /// </summary>
        public int WriteProtect { get; set; } = 0;

        /// <summary>
        /// 启动 LUN (bDescrAccessEn)
        /// </summary>
        public int BootLun { get; set; } = 1;

        /// <summary>
        /// 扩展配置 (qWriteBoosterBufferPreserveUserSpaceEn)
        /// </summary>
        public bool WriteBoosterPreserveUserSpace { get; set; } = true;

        /// <summary>
        /// Write Booster 缓冲区大小 (dNumSharedWriteBoosterBufferAllocUnits)
        /// </summary>
        public long WriteBoosterBufferSize { get; set; } = 0;
    }

    /// <summary>
    /// UFS LUN 配置
    /// </summary>
    public class UfsLunConfig
    {
        /// <summary>
        /// LUN 编号
        /// </summary>
        public int LunNumber { get; set; }

        /// <summary>
        /// 是否可启动 (bBootLunID)
        /// </summary>
        public bool Bootable { get; set; } = false;

        /// <summary>
        /// 大小 (以扇区为单位)
        /// </summary>
        public long SizeInSectors { get; set; }

        /// <summary>
        /// 大小 (以 KB 为单位)
        /// </summary>
        public long SizeInKB { get; set; }

        /// <summary>
        /// 扇区大小 (默认 4096)
        /// </summary>
        public int SectorSize { get; set; } = 4096;

        /// <summary>
        /// 内存类型 (0=Normal, 1=System Code, 2=Non-Persistent, 3=Enhanced1)
        /// </summary>
        public int MemoryType { get; set; } = 0;

        /// <summary>
        /// 写保护组数
        /// </summary>
        public int WriteProtectGroupNum { get; set; } = 0;

        /// <summary>
        /// 数据可靠性 (bDataReliability)
        /// </summary>
        public int DataReliability { get; set; } = 0;

        /// <summary>
        /// 逻辑块大小
        /// </summary>
        public int LogicalBlockSize { get; set; } = 4096;

        /// <summary>
        /// 配置模式
        /// </summary>
        public int ProvisioningType { get; set; } = 0;
    }

    /// <summary>
    /// eMMC 配置
    /// </summary>
    public class EmmcConfig
    {
        /// <summary>
        /// 启动分区 1 大小 (以 128KB 为单位)
        /// </summary>
        public int BootPartition1Size { get; set; } = 0;

        /// <summary>
        /// 启动分区 2 大小 (以 128KB 为单位)
        /// </summary>
        public int BootPartition2Size { get; set; } = 0;

        /// <summary>
        /// RPMB 分区大小 (以 128KB 为单位)
        /// </summary>
        public int RpmbSize { get; set; } = 0;

        /// <summary>
        /// GP 分区大小 (以扇区为单位)
        /// </summary>
        public long[] GpPartitionSizes { get; set; } = new long[4];

        /// <summary>
        /// 增强型 User Area 大小 (以扇区为单位)
        /// </summary>
        public long EnhancedUserAreaSize { get; set; } = 0;

        /// <summary>
        /// 增强型 User Area 起始地址 (以扇区为单位)
        /// </summary>
        public long EnhancedUserAreaStart { get; set; } = 0;
    }

    /// <summary>
    /// 完整 Provision 配置
    /// </summary>
    public class ProvisionConfig
    {
        /// <summary>
        /// 存储类型 (UFS 或 eMMC)
        /// </summary>
        public string StorageType { get; set; } = "UFS";

        /// <summary>
        /// UFS 全局配置
        /// </summary>
        public UfsGlobalConfig UfsGlobal { get; set; } = new UfsGlobalConfig();

        /// <summary>
        /// UFS LUN 配置列表
        /// </summary>
        public List<UfsLunConfig> UfsLuns { get; set; } = new List<UfsLunConfig>();

        /// <summary>
        /// eMMC 配置
        /// </summary>
        public EmmcConfig Emmc { get; set; } = new EmmcConfig();
    }

    #endregion

    /// <summary>
    /// UFS/eMMC Provisioning 服务
    /// 解析和生成 provision.xml 配置文件
    /// </summary>
    public class ProvisionService
    {
        private readonly Action<string> _log;

        public ProvisionService(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        #region 解析 provision.xml

        /// <summary>
        /// 解析 provision.xml 文件
        /// </summary>
        public ProvisionConfig ParseProvisionXml(string xmlPath)
        {
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException("Provision XML 文件不存在", xmlPath);

            string xmlContent = File.ReadAllText(xmlPath);
            return ParseProvisionXmlContent(xmlContent);
        }

        /// <summary>
        /// 解析 provision.xml 内容
        /// </summary>
        public ProvisionConfig ParseProvisionXmlContent(string xmlContent)
        {
            var config = new ProvisionConfig();

            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;

                if (root == null || root.Name.LocalName != "data")
                {
                    _log("[Provision] XML 格式错误: 缺少 data 根元素");
                    return config;
                }

                // 解析 UFS 配置
                var ufsConfig = root.Element("ufs");
                if (ufsConfig != null)
                {
                    config.StorageType = "UFS";
                    ParseUfsConfig(ufsConfig, config);
                }

                // 解析 eMMC 配置
                var emmcConfig = root.Element("emmc");
                if (emmcConfig != null)
                {
                    config.StorageType = "eMMC";
                    ParseEmmcConfig(emmcConfig, config);
                }

                _log(string.Format("[Provision] 解析完成: {0}, {1} 个 LUN", 
                    config.StorageType, config.UfsLuns.Count));
            }
            catch (Exception ex)
            {
                _log(string.Format("[Provision] 解析失败: {0}", ex.Message));
            }

            return config;
        }

        private void ParseUfsConfig(XElement ufsElement, ProvisionConfig config)
        {
            // 解析全局配置
            var global = ufsElement.Element("global");
            if (global != null)
            {
                config.UfsGlobal.BootEnable = GetBoolAttribute(global, "bBootEnable", true);
                config.UfsGlobal.WriteProtect = GetIntAttribute(global, "bSecureWriteProtectEn", 0);
                config.UfsGlobal.BootLun = GetIntAttribute(global, "bDescrAccessEn", 1);
                config.UfsGlobal.WriteBoosterPreserveUserSpace = GetBoolAttribute(global, "qWriteBoosterBufferPreserveUserSpaceEn", true);
                config.UfsGlobal.WriteBoosterBufferSize = GetLongAttribute(global, "dNumSharedWriteBoosterBufferAllocUnits", 0);
            }

            // 解析 LUN 配置
            foreach (var lunElement in ufsElement.Elements("lun"))
            {
                var lun = new UfsLunConfig
                {
                    LunNumber = GetIntAttribute(lunElement, "physical_partition_number", 0),
                    Bootable = GetBoolAttribute(lunElement, "bBootLunID", false),
                    SizeInSectors = GetLongAttribute(lunElement, "num_partition_sectors", 0),
                    SizeInKB = GetLongAttribute(lunElement, "size_in_KB", 0),
                    SectorSize = GetIntAttribute(lunElement, "SECTOR_SIZE_IN_BYTES", 4096),
                    MemoryType = GetIntAttribute(lunElement, "bMemoryType", 0),
                    WriteProtectGroupNum = GetIntAttribute(lunElement, "bProvisioningType", 0),
                    DataReliability = GetIntAttribute(lunElement, "bDataReliability", 0),
                    LogicalBlockSize = GetIntAttribute(lunElement, "bLogicalBlockSize", 4096)
                };

                config.UfsLuns.Add(lun);
            }
        }

        private void ParseEmmcConfig(XElement emmcElement, ProvisionConfig config)
        {
            config.Emmc.BootPartition1Size = GetIntAttribute(emmcElement, "BOOT_SIZE_MULTI1", 0);
            config.Emmc.BootPartition2Size = GetIntAttribute(emmcElement, "BOOT_SIZE_MULTI2", 0);
            config.Emmc.RpmbSize = GetIntAttribute(emmcElement, "RPMB_SIZE_MULT", 0);
            config.Emmc.EnhancedUserAreaSize = GetLongAttribute(emmcElement, "ENH_SIZE_MULT", 0);
            config.Emmc.EnhancedUserAreaStart = GetLongAttribute(emmcElement, "ENH_START_ADDR", 0);

            // 解析 GP 分区
            for (int i = 0; i < 4; i++)
            {
                config.Emmc.GpPartitionSizes[i] = GetLongAttribute(emmcElement, $"GP_SIZE_MULT{i + 1}", 0);
            }
        }

        #endregion

        #region 生成 provision.xml

        /// <summary>
        /// 生成 UFS provision.xml 内容
        /// </summary>
        public string GenerateUfsProvisionXml(ProvisionConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" ?>");
            sb.AppendLine("<data>");
            sb.AppendLine("  <!--NOTE: This is an ** Alarm Auto Generated file ** and target specific-->");
            sb.AppendLine("  <!--NOTE: Modify at your own risk-->");
            sb.AppendLine();
            sb.AppendLine("  <ufs>");

            // 全局配置
            sb.AppendFormat("    <global bBootEnable=\"{0}\" bDescrAccessEn=\"{1}\" " +
                "bInitPowerMode=\"1\" bInitActiveICCLevel=\"0\" " +
                "bSecureRemovalType=\"0\" bConfigDescrLock=\"0\" " +
                "bSecureWriteProtectEn=\"{2}\" " +
                "qWriteBoosterBufferPreserveUserSpaceEn=\"{3}\" " +
                "dNumSharedWriteBoosterBufferAllocUnits=\"{4}\" />\n",
                config.UfsGlobal.BootEnable ? 1 : 0,
                config.UfsGlobal.BootLun,
                config.UfsGlobal.WriteProtect,
                config.UfsGlobal.WriteBoosterPreserveUserSpace ? 1 : 0,
                config.UfsGlobal.WriteBoosterBufferSize);
            sb.AppendLine();

            // LUN 配置
            foreach (var lun in config.UfsLuns)
            {
                sb.AppendFormat("    <lun physical_partition_number=\"{0}\" " +
                    "bBootLunID=\"{1}\" " +
                    "num_partition_sectors=\"{2}\" " +
                    "size_in_KB=\"{3}\" " +
                    "SECTOR_SIZE_IN_BYTES=\"{4}\" " +
                    "bMemoryType=\"{5}\" " +
                    "bProvisioningType=\"{6}\" " +
                    "bDataReliability=\"{7}\" " +
                    "bLogicalBlockSize=\"{8}\" />\n",
                    lun.LunNumber,
                    lun.Bootable ? 1 : 0,
                    lun.SizeInSectors,
                    lun.SizeInKB,
                    lun.SectorSize,
                    lun.MemoryType,
                    lun.ProvisioningType,
                    lun.DataReliability,
                    lun.LogicalBlockSize);
            }

            sb.AppendLine("  </ufs>");
            sb.AppendLine("</data>");

            return sb.ToString();
        }

        /// <summary>
        /// 生成 eMMC provision.xml 内容
        /// </summary>
        public string GenerateEmmcProvisionXml(ProvisionConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" ?>");
            sb.AppendLine("<data>");
            sb.AppendLine("  <!--NOTE: This is an ** Alarm Auto Generated file ** and target specific-->");
            sb.AppendLine();
            sb.AppendFormat("  <emmc BOOT_SIZE_MULTI1=\"{0}\" BOOT_SIZE_MULTI2=\"{1}\" " +
                "RPMB_SIZE_MULT=\"{2}\" " +
                "ENH_SIZE_MULT=\"{3}\" ENH_START_ADDR=\"{4}\" " +
                "GP_SIZE_MULT1=\"{5}\" GP_SIZE_MULT2=\"{6}\" " +
                "GP_SIZE_MULT3=\"{7}\" GP_SIZE_MULT4=\"{8}\" />\n",
                config.Emmc.BootPartition1Size,
                config.Emmc.BootPartition2Size,
                config.Emmc.RpmbSize,
                config.Emmc.EnhancedUserAreaSize,
                config.Emmc.EnhancedUserAreaStart,
                config.Emmc.GpPartitionSizes[0],
                config.Emmc.GpPartitionSizes[1],
                config.Emmc.GpPartitionSizes[2],
                config.Emmc.GpPartitionSizes[3]);
            sb.AppendLine("</data>");

            return sb.ToString();
        }

        /// <summary>
        /// 保存 provision.xml 到文件
        /// </summary>
        public void SaveProvisionXml(ProvisionConfig config, string outputPath)
        {
            string content;
            if (config.StorageType == "UFS")
                content = GenerateUfsProvisionXml(config);
            else
                content = GenerateEmmcProvisionXml(config);

            File.WriteAllText(outputPath, content, Encoding.UTF8);
            _log(string.Format("[Provision] 已保存: {0}", outputPath));
        }

        #endregion

        #region 默认配置

        /// <summary>
        /// 创建默认 UFS 配置 (典型 8 LUN 布局)
        /// </summary>
        public static ProvisionConfig CreateDefaultUfsConfig(long totalSizeGB = 256)
        {
            var config = new ProvisionConfig
            {
                StorageType = "UFS",
                UfsGlobal = new UfsGlobalConfig
                {
                    BootEnable = true,
                    BootLun = 1,
                    WriteProtect = 0,
                    WriteBoosterPreserveUserSpace = true,
                    WriteBoosterBufferSize = 0x200000 // 4GB Write Booster
                }
            };

            // 典型 LUN 布局
            // LUN 0: 主启动 (xbl, xbl_config)
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 0, Bootable = true, SizeInKB = 8192, MemoryType = 3 });
            // LUN 1: 备份启动
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 1, Bootable = true, SizeInKB = 8192, MemoryType = 3 });
            // LUN 2: 系统相关
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 2, Bootable = false, SizeInKB = 4096, MemoryType = 0 });
            // LUN 3: 持久化数据
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 3, Bootable = false, SizeInKB = 512, MemoryType = 0 });
            // LUN 4: 主系统分区 (super, userdata 等)
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 4, Bootable = false, SizeInKB = totalSizeGB * 1024 * 1024 - 30000, MemoryType = 0 });
            // LUN 5-7: 保留
            for (int i = 5; i <= 7; i++)
                config.UfsLuns.Add(new UfsLunConfig { LunNumber = i, Bootable = false, SizeInKB = 0, MemoryType = 0 });

            return config;
        }

        /// <summary>
        /// 创建默认 eMMC 配置
        /// </summary>
        public static ProvisionConfig CreateDefaultEmmcConfig()
        {
            return new ProvisionConfig
            {
                StorageType = "eMMC",
                Emmc = new EmmcConfig
                {
                    BootPartition1Size = 32, // 4MB
                    BootPartition2Size = 32, // 4MB
                    RpmbSize = 8,            // 1MB
                    GpPartitionSizes = new long[4]
                }
            };
        }

        #endregion

        #region 辅助方法

        private static int GetIntAttribute(XElement element, string name, int defaultValue)
        {
            var attr = element.Attribute(name);
            if (attr == null) return defaultValue;
            
            string value = attr.Value;
            
            // 支持 16 进制
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out int hexResult))
                    return hexResult;
            }
            
            if (int.TryParse(value, out int result))
                return result;
                
            return defaultValue;
        }

        private static long GetLongAttribute(XElement element, string name, long defaultValue)
        {
            var attr = element.Attribute(name);
            if (attr == null) return defaultValue;
            
            string value = attr.Value;
            
            // 支持 16 进制
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out long hexResult))
                    return hexResult;
            }
            
            if (long.TryParse(value, out long result))
                return result;
                
            return defaultValue;
        }

        private static bool GetBoolAttribute(XElement element, string name, bool defaultValue)
        {
            var attr = element.Attribute(name);
            if (attr == null) return defaultValue;
            
            string value = attr.Value.ToLowerInvariant();
            
            if (value == "1" || value == "true" || value == "yes")
                return true;
            if (value == "0" || value == "false" || value == "no")
                return false;
                
            return defaultValue;
        }

        #endregion

        #region 分析报告

        /// <summary>
        /// 生成配置分析报告
        /// </summary>
        public string GenerateAnalysisReport(ProvisionConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("       Provision 配置分析报告");
            sb.AppendLine("========================================");
            sb.AppendLine();

            sb.AppendLine(string.Format("存储类型: {0}", config.StorageType));
            sb.AppendLine();

            if (config.StorageType == "UFS")
            {
                sb.AppendLine("【全局配置】");
                sb.AppendLine(string.Format("  启动使能: {0}", config.UfsGlobal.BootEnable ? "是" : "否"));
                sb.AppendLine(string.Format("  启动 LUN: {0}", config.UfsGlobal.BootLun));
                sb.AppendLine(string.Format("  写保护: {0}", GetWriteProtectDescription(config.UfsGlobal.WriteProtect)));
                sb.AppendLine(string.Format("  Write Booster: {0}", 
                    config.UfsGlobal.WriteBoosterBufferSize > 0 
                        ? string.Format("{0:F2} GB", config.UfsGlobal.WriteBoosterBufferSize * 4096.0 / 1024 / 1024 / 1024) 
                        : "未配置"));
                sb.AppendLine();

                sb.AppendLine("【LUN 配置】");
                sb.AppendLine(string.Format("  共 {0} 个 LUN", config.UfsLuns.Count));
                sb.AppendLine();

                long totalSize = 0;
                foreach (var lun in config.UfsLuns.OrderBy(l => l.LunNumber))
                {
                    string sizeStr;
                    if (lun.SizeInKB >= 1024 * 1024)
                        sizeStr = string.Format("{0:F2} GB", lun.SizeInKB / 1024.0 / 1024.0);
                    else if (lun.SizeInKB >= 1024)
                        sizeStr = string.Format("{0:F2} MB", lun.SizeInKB / 1024.0);
                    else
                        sizeStr = string.Format("{0} KB", lun.SizeInKB);

                    sb.AppendLine(string.Format("  LUN {0}: {1,-12} {2} {3}",
                        lun.LunNumber,
                        sizeStr,
                        lun.Bootable ? "[启动]" : "      ",
                        GetMemoryTypeDescription(lun.MemoryType)));

                    totalSize += lun.SizeInKB;
                }

                sb.AppendLine();
                sb.AppendLine(string.Format("  总容量: {0:F2} GB", totalSize / 1024.0 / 1024.0));
            }
            else
            {
                sb.AppendLine("【eMMC 配置】");
                sb.AppendLine(string.Format("  Boot 分区 1: {0} MB", config.Emmc.BootPartition1Size * 128 / 1024));
                sb.AppendLine(string.Format("  Boot 分区 2: {0} MB", config.Emmc.BootPartition2Size * 128 / 1024));
                sb.AppendLine(string.Format("  RPMB: {0} MB", config.Emmc.RpmbSize * 128 / 1024));
            }

            sb.AppendLine();
            sb.AppendLine("========================================");

            return sb.ToString();
        }

        private static string GetWriteProtectDescription(int wp)
        {
            switch (wp)
            {
                case 0: return "无保护";
                case 1: return "上电写保护";
                case 2: return "永久写保护";
                default: return string.Format("未知 ({0})", wp);
            }
        }

        private static string GetMemoryTypeDescription(int memType)
        {
            switch (memType)
            {
                case 0: return "Normal";
                case 1: return "System Code";
                case 2: return "Non-Persistent";
                case 3: return "Enhanced (SLC)";
                default: return string.Format("Unknown ({0})", memType);
            }
        }

        #endregion
    }
}
