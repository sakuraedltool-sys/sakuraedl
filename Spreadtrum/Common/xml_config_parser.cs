// ============================================================================
// SakuraEDL - XML Config Parser | XML 配置解析器
// ============================================================================
// [ZH] XML 配置解析 - 解析展讯设备 XML 配置文件
// [EN] XML Config Parser - Parse Spreadtrum device XML configuration files
// [JA] XML設定解析 - Spreadtrumデバイスの XML 設定ファイル解析
// [KO] XML 설정 파서 - Spreadtrum 기기 XML 설정 파일 분석
// [RU] Парсер XML конфигурации - Разбор конфигурационных файлов Spreadtrum
// [ES] Analizador de config XML - Análisis de archivos de configuración XML
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SakuraEDL.Spreadtrum.Common
{
    /// <summary>
    /// 展讯 XML 配置解析器
    /// 解析 PAC 包内的 XML 配置文件
    /// </summary>
    public class XmlConfigParser
    {
        private readonly Action<string> _log;

        public XmlConfigParser(Action<string> log = null)
        {
            _log = log ?? delegate { };
        }

        #region 主解析方法

        /// <summary>
        /// 解析 XML 配置数据
        /// </summary>
        public SprdXmlConfig Parse(byte[] xmlData)
        {
            if (xmlData == null || xmlData.Length == 0)
                return null;

            try
            {
                // 移除 BOM 和空字符
                string xmlContent = CleanXmlContent(xmlData);
                
                var doc = XDocument.Parse(xmlContent);
                var config = new SprdXmlConfig();

                // 获取根元素
                var root = doc.Root;
                if (root == null)
                    return null;

                Log("[XML] 解析配置: {0}", root.Name.LocalName);

                // 解析不同类型的 XML 配置
                if (root.Name.LocalName == "BMAConfig" || root.Name.LocalName == "BMFileWapper")
                {
                    // 标准刷机配置
                    ParseBmaConfig(root, config);
                }
                else if (root.Name.LocalName == "Partition")
                {
                    // 分区表配置
                    ParsePartitionConfig(root, config);
                }
                else if (root.Name.LocalName == "NVBackupRestore")
                {
                    // NV 备份恢复配置
                    ParseNvConfig(root, config);
                }
                else if (root.Name.LocalName == "ProductionConfig" || root.Name.LocalName == "Production")
                {
                    // 生产配置
                    ParseProductionConfig(root, config);
                }
                else
                {
                    // 尝试通用解析
                    ParseGenericConfig(root, config);
                }

                return config;
            }
            catch (Exception ex)
            {
                Log("[XML] 解析错误: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 从文件解析
        /// </summary>
        public SprdXmlConfig ParseFile(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            byte[] data = File.ReadAllBytes(filePath);
            return Parse(data);
        }

        #endregion

        #region BMA 配置解析

        /// <summary>
        /// 解析 BMA 刷机配置
        /// </summary>
        private void ParseBmaConfig(XElement root, SprdXmlConfig config)
        {
            config.ConfigType = SprdXmlConfigType.BmaConfig;

            // 解析产品信息
            var product = root.Element("Product") ?? root.Element("ProductInfo");
            if (product != null)
            {
                config.ProductName = GetAttributeOrElement(product, "name") ?? 
                                     GetAttributeOrElement(product, "Name");
                config.Version = GetAttributeOrElement(product, "version") ?? 
                                GetAttributeOrElement(product, "Version");
                Log("[XML] 产品: {0}, 版本: {1}", config.ProductName, config.Version);
            }

            // 解析刷机方案
            var scheme = root.Element("Scheme") ?? root.Element("DownloadScheme");
            if (scheme != null)
            {
                config.SchemeName = GetAttributeOrElement(scheme, "name");
                Log("[XML] 方案: {0}", config.SchemeName);
            }

            // 解析文件列表
            ParseFileList(root, config);

            // 解析 FDL 配置
            ParseFdlConfig(root, config);

            // 解析分区操作
            ParsePartitionOperations(root, config);

            // 解析擦除配置
            ParseEraseConfig(root, config);

            // 解析 NV 配置
            ParseNvOperations(root, config);
        }

        /// <summary>
        /// 解析文件列表
        /// </summary>
        private void ParseFileList(XElement root, SprdXmlConfig config)
        {
            // 查找文件列表节点
            var fileNodes = root.Descendants("File")
                .Concat(root.Descendants("BMFile"))
                .Concat(root.Descendants("DownLoadFile"));

            foreach (var fileNode in fileNodes)
            {
                var fileInfo = new SprdXmlFileInfo
                {
                    ID = GetAttributeOrElement(fileNode, "ID") ?? 
                         GetAttributeOrElement(fileNode, "id"),
                    Name = GetAttributeOrElement(fileNode, "Name") ?? 
                           GetAttributeOrElement(fileNode, "name") ??
                           GetAttributeOrElement(fileNode, "PARTITION_NAME"),
                    FileName = GetAttributeOrElement(fileNode, "IDAlias") ?? 
                               GetAttributeOrElement(fileNode, "FileName") ??
                               GetAttributeOrElement(fileNode, "File"),
                    Type = ParseFileType(GetAttributeOrElement(fileNode, "Type") ?? 
                                        GetAttributeOrElement(fileNode, "type")),
                    Block = GetAttributeOrElement(fileNode, "Block") ?? 
                            GetAttributeOrElement(fileNode, "block"),
                    Flag = ParseFlag(GetAttributeOrElement(fileNode, "Flag") ?? 
                                    GetAttributeOrElement(fileNode, "flag")),
                    CheckFlag = ParseCheckFlag(GetAttributeOrElement(fileNode, "CheckFlag")),
                    IsSelected = ParseBool(GetAttributeOrElement(fileNode, "Selected") ?? 
                                          GetAttributeOrElement(fileNode, "Use"), true)
                };

                // 解析地址
                string addrStr = GetAttributeOrElement(fileNode, "Base") ?? 
                                GetAttributeOrElement(fileNode, "Address") ??
                                GetAttributeOrElement(fileNode, "LoadAddr");
                if (!string.IsNullOrEmpty(addrStr))
                {
                    fileInfo.Address = ParseHexOrDecimal(addrStr);
                }

                // 解析大小
                string sizeStr = GetAttributeOrElement(fileNode, "Size") ?? 
                                GetAttributeOrElement(fileNode, "Length");
                if (!string.IsNullOrEmpty(sizeStr))
                {
                    fileInfo.Size = ParseHexOrDecimal(sizeStr);
                }

                if (!string.IsNullOrEmpty(fileInfo.Name) || !string.IsNullOrEmpty(fileInfo.FileName))
                {
                    config.Files.Add(fileInfo);
                    Log("[XML] 文件: {0} -> {1}, 地址: 0x{2:X}, 大小: {3}",
                        fileInfo.Name, fileInfo.FileName, fileInfo.Address, FormatSize(fileInfo.Size));
                }
            }
        }

        /// <summary>
        /// 解析 FDL 配置
        /// </summary>
        private void ParseFdlConfig(XElement root, SprdXmlConfig config)
        {
            // FDL1 配置
            var fdl1 = root.Descendants("FDL1")
                .Concat(root.Descendants("FDL"))
                .FirstOrDefault();

            if (fdl1 != null)
            {
                config.Fdl1Config = new SprdFdlConfig
                {
                    FileName = GetAttributeOrElement(fdl1, "File") ?? 
                               GetAttributeOrElement(fdl1, "FileName") ??
                               fdl1.Value,
                    Address = ParseHexOrDecimal(GetAttributeOrElement(fdl1, "Base") ?? 
                                               GetAttributeOrElement(fdl1, "Address") ?? "0"),
                    BaudRate = ParseInt(GetAttributeOrElement(fdl1, "Baud") ?? 
                                       GetAttributeOrElement(fdl1, "BaudRate"), 115200)
                };
                Log("[XML] FDL1: {0} @ 0x{1:X}", config.Fdl1Config.FileName, config.Fdl1Config.Address);
            }

            // FDL2 配置
            var fdl2 = root.Descendants("FDL2").FirstOrDefault();
            if (fdl2 != null)
            {
                config.Fdl2Config = new SprdFdlConfig
                {
                    FileName = GetAttributeOrElement(fdl2, "File") ?? 
                               GetAttributeOrElement(fdl2, "FileName") ??
                               fdl2.Value,
                    Address = ParseHexOrDecimal(GetAttributeOrElement(fdl2, "Base") ?? 
                                               GetAttributeOrElement(fdl2, "Address") ?? "0"),
                    BaudRate = ParseInt(GetAttributeOrElement(fdl2, "Baud") ?? 
                                       GetAttributeOrElement(fdl2, "BaudRate"), 921600)
                };
                Log("[XML] FDL2: {0} @ 0x{1:X}", config.Fdl2Config.FileName, config.Fdl2Config.Address);
            }
        }

        /// <summary>
        /// 解析分区操作
        /// </summary>
        private void ParsePartitionOperations(XElement root, SprdXmlConfig config)
        {
            var partOps = root.Descendants("PartitionOperation")
                .Concat(root.Descendants("Operation"));

            foreach (var op in partOps)
            {
                var operation = new SprdPartitionOperation
                {
                    PartitionName = GetAttributeOrElement(op, "Partition") ?? 
                                   GetAttributeOrElement(op, "Name"),
                    Operation = ParseOperationType(GetAttributeOrElement(op, "Op") ?? 
                                                  GetAttributeOrElement(op, "Operation") ?? 
                                                  GetAttributeOrElement(op, "Type")),
                    Priority = ParseInt(GetAttributeOrElement(op, "Priority"), 0),
                    IsEnabled = ParseBool(GetAttributeOrElement(op, "Enable"), true)
                };

                if (!string.IsNullOrEmpty(operation.PartitionName))
                {
                    config.PartitionOperations.Add(operation);
                }
            }

            // 按优先级排序
            config.PartitionOperations = config.PartitionOperations
                .OrderBy(p => p.Priority)
                .ToList();
        }

        /// <summary>
        /// 解析擦除配置
        /// </summary>
        private void ParseEraseConfig(XElement root, SprdXmlConfig config)
        {
            var eraseNode = root.Element("Erase") ?? root.Element("EraseConfig");
            if (eraseNode != null)
            {
                config.EraseConfig = new SprdEraseConfig
                {
                    EraseAll = ParseBool(GetAttributeOrElement(eraseNode, "EraseAll"), false),
                    EraseUserData = ParseBool(GetAttributeOrElement(eraseNode, "EraseUserData"), false),
                    EraseNv = ParseBool(GetAttributeOrElement(eraseNode, "EraseNV"), false),
                    FormatFlash = ParseBool(GetAttributeOrElement(eraseNode, "Format"), false)
                };

                // 解析排除分区
                var excludeNode = eraseNode.Element("Exclude");
                if (excludeNode != null)
                {
                    config.EraseConfig.ExcludePartitions = excludeNode.Value?
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToList() ?? new List<string>();
                }

                Log("[XML] 擦除配置: 全部={0}, 用户数据={1}, NV={2}",
                    config.EraseConfig.EraseAll, config.EraseConfig.EraseUserData, config.EraseConfig.EraseNv);
            }
        }

        /// <summary>
        /// 解析 NV 操作
        /// </summary>
        private void ParseNvOperations(XElement root, SprdXmlConfig config)
        {
            var nvNodes = root.Descendants("NV")
                .Concat(root.Descendants("NVItem"));

            foreach (var nv in nvNodes)
            {
                var nvOp = new SprdNvOperation
                {
                    ItemId = (ushort)ParseHexOrDecimal(GetAttributeOrElement(nv, "ID") ?? "0"),
                    Name = GetAttributeOrElement(nv, "Name"),
                    Operation = GetAttributeOrElement(nv, "Op") ?? "backup",
                    BackupFile = GetAttributeOrElement(nv, "BackupFile"),
                    RestoreFile = GetAttributeOrElement(nv, "RestoreFile")
                };

                if (nvOp.ItemId > 0 || !string.IsNullOrEmpty(nvOp.Name))
                {
                    config.NvOperations.Add(nvOp);
                }
            }
        }

        #endregion

        #region 分区配置解析

        /// <summary>
        /// 解析分区表配置
        /// </summary>
        private void ParsePartitionConfig(XElement root, SprdXmlConfig config)
        {
            config.ConfigType = SprdXmlConfigType.PartitionTable;

            var partitions = root.Descendants("Part")
                .Concat(root.Descendants("Partition"))
                .Concat(root.Descendants("Entry"));

            foreach (var part in partitions)
            {
                var partInfo = new SprdXmlPartitionInfo
                {
                    Name = GetAttributeOrElement(part, "id") ?? 
                           GetAttributeOrElement(part, "Name") ??
                           GetAttributeOrElement(part, "name"),
                    Size = ParseHexOrDecimal(GetAttributeOrElement(part, "size") ?? 
                                            GetAttributeOrElement(part, "Size") ?? "0"),
                    Offset = ParseHexOrDecimal(GetAttributeOrElement(part, "offset") ?? 
                                              GetAttributeOrElement(part, "Offset") ?? "0"),
                    Type = GetAttributeOrElement(part, "type") ?? 
                           GetAttributeOrElement(part, "Type") ?? "unknown",
                    FileSystem = GetAttributeOrElement(part, "fs") ?? 
                                GetAttributeOrElement(part, "FileSystem"),
                    IsReadOnly = ParseBool(GetAttributeOrElement(part, "readonly"), false)
                };

                if (!string.IsNullOrEmpty(partInfo.Name))
                {
                    config.PartitionTable.Add(partInfo);
                    Log("[XML] 分区: {0}, 大小: {1}, 偏移: 0x{2:X}",
                        partInfo.Name, FormatSize(partInfo.Size), partInfo.Offset);
                }
            }
        }

        #endregion

        #region NV 配置解析

        /// <summary>
        /// 解析 NV 备份恢复配置
        /// </summary>
        private void ParseNvConfig(XElement root, SprdXmlConfig config)
        {
            config.ConfigType = SprdXmlConfigType.NvConfig;

            var nvItems = root.Descendants("Item")
                .Concat(root.Descendants("NVItem"));

            foreach (var item in nvItems)
            {
                var nvOp = new SprdNvOperation
                {
                    ItemId = (ushort)ParseHexOrDecimal(GetAttributeOrElement(item, "ID") ?? "0"),
                    Name = GetAttributeOrElement(item, "Name"),
                    Operation = GetAttributeOrElement(item, "Operation") ?? "backup",
                    Category = GetAttributeOrElement(item, "Category") ?? "system"
                };

                if (nvOp.ItemId > 0)
                {
                    config.NvOperations.Add(nvOp);
                }
            }
        }

        #endregion

        #region 生产配置解析

        /// <summary>
        /// 解析生产配置
        /// </summary>
        private void ParseProductionConfig(XElement root, SprdXmlConfig config)
        {
            config.ConfigType = SprdXmlConfigType.ProductionConfig;

            // 解析生产参数
            config.ProductionSettings = new Dictionary<string, string>();

            foreach (var elem in root.Elements())
            {
                string key = elem.Name.LocalName;
                string value = elem.Value ?? GetAttributeOrElement(elem, "value");
                if (!string.IsNullOrEmpty(key))
                {
                    config.ProductionSettings[key] = value;
                }
            }

            // 也尝试解析文件列表
            ParseFileList(root, config);
        }

        #endregion

        #region 通用配置解析

        /// <summary>
        /// 通用配置解析
        /// </summary>
        private void ParseGenericConfig(XElement root, SprdXmlConfig config)
        {
            config.ConfigType = SprdXmlConfigType.Generic;

            // 尝试提取所有有用信息
            ParseFileList(root, config);
            ParseFdlConfig(root, config);
            ParsePartitionOperations(root, config);

            // 存储原始 XML 以便后续分析
            config.RawXml = root.ToString();
        }

        #endregion

        #region 辅助方法

        private string CleanXmlContent(byte[] data)
        {
            // 跳过 BOM
            int start = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                start = 3;
            }
            else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            {
                // UTF-16 LE BOM
                return System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2)
                    .Replace("\0", "");
            }
            else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            {
                // UTF-16 BE BOM
                return System.Text.Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2)
                    .Replace("\0", "");
            }

            string content = System.Text.Encoding.UTF8.GetString(data, start, data.Length - start);
            
            // 移除空字符
            content = content.Replace("\0", "");
            
            // 移除无效 XML 字符
            content = RemoveInvalidXmlChars(content);

            return content;
        }

        private string RemoveInvalidXmlChars(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (XmlConvert.IsXmlChar(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private string GetAttributeOrElement(XElement element, string name)
        {
            // 先查属性
            var attr = element.Attribute(name);
            if (attr != null)
                return attr.Value;

            // 再查子元素
            var child = element.Element(name);
            if (child != null)
                return child.Value;

            // 不区分大小写再查一次
            attr = element.Attributes()
                .FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (attr != null)
                return attr.Value;

            child = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            return child?.Value;
        }

        private ulong ParseHexOrDecimal(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            value = value.Trim();

            try
            {
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.ToUInt64(value.Substring(2), 16);
                }
                else if (value.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    // 尝试解析为十进制，如果失败则尝试十六进制
                    if (ulong.TryParse(value, out ulong dec))
                        return dec;
                    return Convert.ToUInt64(value, 16);
                }
                return ulong.Parse(value);
            }
            catch
            {
                return 0;
            }
        }

        private int ParseInt(string value, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;

            if (int.TryParse(value, out int result))
                return result;

            return defaultValue;
        }

        private bool ParseBool(string value, bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;

            value = value.Trim().ToLower();
            return value == "1" || value == "true" || value == "yes" || value == "on";
        }

        private SprdXmlFileType ParseFileType(string type)
        {
            if (string.IsNullOrEmpty(type))
                return SprdXmlFileType.Unknown;

            type = type.ToLower();

            if (type.Contains("fdl1") || type == "0")
                return SprdXmlFileType.FDL1;
            if (type.Contains("fdl2") || type == "1")
                return SprdXmlFileType.FDL2;
            if (type.Contains("code") || type.Contains("modem"))
                return SprdXmlFileType.Code;
            if (type.Contains("nv"))
                return SprdXmlFileType.NV;
            if (type.Contains("phase"))
                return SprdXmlFileType.PhaseCheck;
            if (type.Contains("user") || type.Contains("data"))
                return SprdXmlFileType.UserData;
            if (type.Contains("boot"))
                return SprdXmlFileType.Boot;
            if (type.Contains("system"))
                return SprdXmlFileType.System;
            if (type.Contains("recovery"))
                return SprdXmlFileType.Recovery;

            return SprdXmlFileType.Unknown;
        }

        private SprdXmlFileFlag ParseFlag(string flag)
        {
            if (string.IsNullOrEmpty(flag))
                return SprdXmlFileFlag.None;

            int flagValue = ParseInt(flag, 0);
            return (SprdXmlFileFlag)flagValue;
        }

        private SprdXmlCheckFlag ParseCheckFlag(string checkFlag)
        {
            if (string.IsNullOrEmpty(checkFlag))
                return SprdXmlCheckFlag.None;

            int checkValue = ParseInt(checkFlag, 0);
            return (SprdXmlCheckFlag)checkValue;
        }

        private SprdOperationType ParseOperationType(string op)
        {
            if (string.IsNullOrEmpty(op))
                return SprdOperationType.Write;

            op = op.ToLower();

            if (op.Contains("erase"))
                return SprdOperationType.Erase;
            if (op.Contains("read") || op.Contains("backup"))
                return SprdOperationType.Read;
            if (op.Contains("verify"))
                return SprdOperationType.Verify;
            if (op.Contains("format"))
                return SprdOperationType.Format;

            return SprdOperationType.Write;
        }

        private string FormatSize(ulong size)
        {
            if (size >= 1024 * 1024 * 1024)
                return string.Format("{0:F1} GB", size / (1024.0 * 1024 * 1024));
            if (size >= 1024 * 1024)
                return string.Format("{0:F1} MB", size / (1024.0 * 1024));
            if (size >= 1024)
                return string.Format("{0:F1} KB", size / 1024.0);
            return string.Format("{0} B", size);
        }

        private void Log(string format, params object[] args)
        {
            _log?.Invoke(string.Format(format, args));
        }

        #endregion
    }

    #region 数据模型

    /// <summary>
    /// XML 配置类型
    /// </summary>
    public enum SprdXmlConfigType
    {
        Unknown,
        BmaConfig,          // BMA 刷机配置
        PartitionTable,     // 分区表配置
        NvConfig,           // NV 配置
        ProductionConfig,   // 生产配置
        Generic             // 通用配置
    }

    /// <summary>
    /// 文件类型
    /// </summary>
    public enum SprdXmlFileType
    {
        Unknown,
        FDL1,
        FDL2,
        Code,
        NV,
        PhaseCheck,
        UserData,
        Boot,
        System,
        Recovery,
        Bootloader,
        Modem
    }

    /// <summary>
    /// 文件标志
    /// </summary>
    [Flags]
    public enum SprdXmlFileFlag
    {
        None = 0,
        OmitIfNotExist = 1,
        EnableNvBackup = 2,
        EraseFirst = 4,
        VerifyAfterWrite = 8
    }

    /// <summary>
    /// 校验标志
    /// </summary>
    [Flags]
    public enum SprdXmlCheckFlag
    {
        None = 0,
        Crc = 1,
        Md5 = 2,
        Sha1 = 4,
        Sha256 = 8
    }

    /// <summary>
    /// 操作类型
    /// </summary>
    public enum SprdOperationType
    {
        Write,
        Erase,
        Read,
        Verify,
        Format
    }

    /// <summary>
    /// XML 配置信息
    /// </summary>
    public class SprdXmlConfig
    {
        public SprdXmlConfigType ConfigType { get; set; }
        public string ProductName { get; set; }
        public string Version { get; set; }
        public string SchemeName { get; set; }
        public string RawXml { get; set; }

        // FDL 配置
        public SprdFdlConfig Fdl1Config { get; set; }
        public SprdFdlConfig Fdl2Config { get; set; }

        // 文件列表
        public List<SprdXmlFileInfo> Files { get; set; } = new List<SprdXmlFileInfo>();

        // 分区表
        public List<SprdXmlPartitionInfo> PartitionTable { get; set; } = new List<SprdXmlPartitionInfo>();

        // 分区操作
        public List<SprdPartitionOperation> PartitionOperations { get; set; } = new List<SprdPartitionOperation>();

        // 擦除配置
        public SprdEraseConfig EraseConfig { get; set; }

        // NV 操作
        public List<SprdNvOperation> NvOperations { get; set; } = new List<SprdNvOperation>();

        // 生产设置
        public Dictionary<string, string> ProductionSettings { get; set; }

        /// <summary>
        /// 获取按优先级排序的刷机顺序
        /// </summary>
        public List<SprdXmlFileInfo> GetFlashOrder()
        {
            // FDL1 -> FDL2 -> 其他文件
            var order = new List<SprdXmlFileInfo>();

            // 添加 FDL1
            var fdl1 = Files.FirstOrDefault(f => f.Type == SprdXmlFileType.FDL1);
            if (fdl1 != null) order.Add(fdl1);

            // 添加 FDL2
            var fdl2 = Files.FirstOrDefault(f => f.Type == SprdXmlFileType.FDL2);
            if (fdl2 != null) order.Add(fdl2);

            // 添加其他选中的文件
            foreach (var file in Files)
            {
                if (file.Type != SprdXmlFileType.FDL1 && 
                    file.Type != SprdXmlFileType.FDL2 && 
                    file.IsSelected)
                {
                    order.Add(file);
                }
            }

            return order;
        }
    }

    /// <summary>
    /// FDL 配置
    /// </summary>
    public class SprdFdlConfig
    {
        public string FileName { get; set; }
        public ulong Address { get; set; }
        public int BaudRate { get; set; }
    }

    /// <summary>
    /// XML 文件信息
    /// </summary>
    public class SprdXmlFileInfo
    {
        public string ID { get; set; }
        public string Name { get; set; }
        public string FileName { get; set; }
        public SprdXmlFileType Type { get; set; }
        public string Block { get; set; }
        public ulong Address { get; set; }
        public ulong Size { get; set; }
        public SprdXmlFileFlag Flag { get; set; }
        public SprdXmlCheckFlag CheckFlag { get; set; }
        public bool IsSelected { get; set; } = true;
    }

    /// <summary>
    /// XML 分区信息
    /// </summary>
    public class SprdXmlPartitionInfo
    {
        public string Name { get; set; }
        public ulong Size { get; set; }
        public ulong Offset { get; set; }
        public string Type { get; set; }
        public string FileSystem { get; set; }
        public bool IsReadOnly { get; set; }
    }

    /// <summary>
    /// 分区操作
    /// </summary>
    public class SprdPartitionOperation
    {
        public string PartitionName { get; set; }
        public SprdOperationType Operation { get; set; }
        public int Priority { get; set; }
        public bool IsEnabled { get; set; }
    }

    /// <summary>
    /// 擦除配置
    /// </summary>
    public class SprdEraseConfig
    {
        public bool EraseAll { get; set; }
        public bool EraseUserData { get; set; }
        public bool EraseNv { get; set; }
        public bool FormatFlash { get; set; }
        public List<string> ExcludePartitions { get; set; } = new List<string>();
    }

    /// <summary>
    /// NV 操作
    /// </summary>
    public class SprdNvOperation
    {
        public ushort ItemId { get; set; }
        public string Name { get; set; }
        public string Operation { get; set; }
        public string Category { get; set; }
        public string BackupFile { get; set; }
        public string RestoreFile { get; set; }
    }

    #endregion
}
