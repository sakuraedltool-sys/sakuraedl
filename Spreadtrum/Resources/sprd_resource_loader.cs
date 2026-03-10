// ============================================================================
// SakuraEDL - SPRD Resource Loader | 展讯资源加载器
// ============================================================================
// [ZH] 资源加载器 - 优先从资源包加载，后备使用嵌入资源
// [EN] Resource Loader - Load from pak first, fallback to embedded resources
// [JA] リソースローダー - pakを優先、組み込みリソースにフォールバック
// [KO] 리소스 로더 - pak 우선 로드, 임베디드 리소스로 폴백
// [RU] Загрузчик ресурсов - Приоритет pak, резервные встроенные ресурсы
// [ES] Cargador de recursos - Prioridad pak, recursos embebidos de reserva
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.IO;
using System.Reflection;

namespace SakuraEDL.Spreadtrum.Resources
{
    /// <summary>
    /// 展讯模块资源加载器
    /// 加载优先级: 资源包 (sprd_resources.pak) > 嵌入资源
    /// </summary>
    public static class SprdResourceLoader
    {
        private static readonly Assembly _assembly = Assembly.GetExecutingAssembly();
        private static SprdResourcePak _pak;
        private static bool _pakChecked;
        private static readonly object _lock = new object();

        // 资源包文件名
        private const string PAK_FILENAME = "sprd_resources.pak";
        
        // 嵌入资源名称前缀
        private const string EMBEDDED_PREFIX = "SakuraEDL.Spreadtrum.Resources.";
        
        /// <summary>
        /// Exploit payload 文件名
        /// </summary>
        public static class ExploitPayloads
        {
            public const string Exploit_4ee8 = "exploit_4ee8.bin";
            public const string Exploit_65015f08 = "exploit_65015f08.bin";
            public const string Exploit_65015f48 = "exploit_65015f48.bin";
        }

        #region 资源包管理

        /// <summary>
        /// 确保资源包已加载
        /// </summary>
        private static void EnsurePak()
        {
            if (_pakChecked) return;

            lock (_lock)
            {
                if (_pakChecked) return;

                string pakPath = GetPakPath();
                if (File.Exists(pakPath))
                {
                    try
                    {
                        _pak = new SprdResourcePak(pakPath);
                    }
                    catch
                    {
                        _pak = null;
                    }
                }
                _pakChecked = true;
            }
        }

        /// <summary>
        /// 获取资源包路径
        /// </summary>
        private static string GetPakPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PAK_FILENAME);
        }

        /// <summary>
        /// 检查资源包是否可用
        /// </summary>
        public static bool IsPakAvailable()
        {
            EnsurePak();
            return _pak != null;
        }

        /// <summary>
        /// 获取资源包版本
        /// </summary>
        public static int GetPakVersion()
        {
            EnsurePak();
            return _pak?.Version ?? 0;
        }

        /// <summary>
        /// 获取资源包中的资源数量
        /// </summary>
        public static int GetPakResourceCount()
        {
            EnsurePak();
            return _pak?.Count ?? 0;
        }

        #endregion

        #region Exploit 加载

        /// <summary>
        /// 根据 FDL1 地址获取对应的 exploit payload
        /// </summary>
        /// <param name="fdl1Address">FDL1 加载地址</param>
        /// <returns>Exploit payload 数据，如果没有匹配返回 null</returns>
        public static byte[] GetExploitPayload(uint fdl1Address)
        {
            string exploitName = GetExploitNameByAddress(fdl1Address);
            if (string.IsNullOrEmpty(exploitName))
                return null;

            string fileName = GetExploitFileName(exploitName);
            if (string.IsNullOrEmpty(fileName))
                return null;

            return LoadResource(fileName);
        }

        /// <summary>
        /// 根据 exploit 名称获取 payload
        /// </summary>
        /// <param name="exploitName">例如 "0x4ee8", "0x65015f08"</param>
        /// <returns>Exploit payload 数据</returns>
        public static byte[] GetExploitPayloadByName(string exploitName)
        {
            string fileName = GetExploitFileName(exploitName);
            if (string.IsNullOrEmpty(fileName))
                return null;

            return LoadResource(fileName);
        }

        /// <summary>
        /// 根据 FDL1 地址获取 exploit 名称
        /// </summary>
        private static string GetExploitNameByAddress(uint fdl1Address)
        {
            // 基于 iReverse 项目的 Prepare_Exploit 函数逻辑
            if (fdl1Address == 0x5000 || fdl1Address == 0x00005000)
                return "0x4ee8";
            
            if (fdl1Address == 0x65000800)
                return "0x65015f08";
            
            if (fdl1Address == 0x65000000)
                return "0x65015f48";
            
            return null;
        }

        /// <summary>
        /// 获取 exploit 文件名
        /// </summary>
        private static string GetExploitFileName(string exploitName)
        {
            switch (exploitName?.ToLower())
            {
                case "0x4ee8":
                    return ExploitPayloads.Exploit_4ee8;
                case "0x65015f08":
                    return ExploitPayloads.Exploit_65015f08;
                case "0x65015f48":
                    return ExploitPayloads.Exploit_65015f48;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 检查是否有可用的 exploit payload
        /// </summary>
        public static bool HasExploitPayload(uint fdl1Address)
        {
            return !string.IsNullOrEmpty(GetExploitNameByAddress(fdl1Address));
        }

        /// <summary>
        /// 检查是否有可用的 exploit (别名)
        /// </summary>
        public static bool HasExploitForAddress(uint fdl1Address)
        {
            return HasExploitPayload(fdl1Address);
        }

        /// <summary>
        /// 获取 exploit 地址 ID
        /// </summary>
        public static string GetExploitAddressId(uint fdl1Address)
        {
            return GetExploitNameByAddress(fdl1Address);
        }

        #endregion

        #region 通用资源加载

        /// <summary>
        /// 加载资源 (优先从资源包，后备嵌入资源)
        /// </summary>
        /// <param name="resourceName">资源文件名</param>
        /// <returns>资源数据</returns>
        public static byte[] LoadResource(string resourceName)
        {
            // 1. 尝试从资源包加载
            EnsurePak();
            if (_pak != null)
            {
                byte[] pakData = _pak.GetResource(resourceName);
                if (pakData != null)
                    return pakData;
            }

            // 2. 后备: 从嵌入资源加载
            return LoadEmbeddedResource(resourceName);
        }

        /// <summary>
        /// 从嵌入资源加载
        /// </summary>
        private static byte[] LoadEmbeddedResource(string resourceName)
        {
            string fullName = EMBEDDED_PREFIX + resourceName;
            
            try
            {
                using (Stream stream = _assembly.GetManifestResourceStream(fullName))
                {
                    if (stream == null)
                    {
                        // 尝试其他可能的名称格式
                        foreach (string name in _assembly.GetManifestResourceNames())
                        {
                            if (name.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
                            {
                                using (Stream altStream = _assembly.GetManifestResourceStream(name))
                                {
                                    if (altStream != null)
                                    {
                                        return ReadAllBytes(altStream);
                                    }
                                }
                            }
                        }
                        return null;
                    }

                    return ReadAllBytes(stream);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从流读取所有字节
        /// </summary>
        private static byte[] ReadAllBytes(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        #endregion

        #region Exploit 信息

        /// <summary>
        /// 获取所有可用的 exploit 信息
        /// </summary>
        public static ExploitInfo[] GetAvailableExploits()
        {
            return new ExploitInfo[]
            {
                new ExploitInfo
                {
                    Name = "0x4ee8",
                    FileName = ExploitPayloads.Exploit_4ee8,
                    Description = "SC77xx 系列 BSL 溢出漏洞",
                    SupportedAddresses = new uint[] { 0x5000, 0x00005000 },
                    SupportedChips = "SC7731, SC7730, SC9830"
                },
                new ExploitInfo
                {
                    Name = "0x65015f08",
                    FileName = ExploitPayloads.Exploit_65015f08,
                    Description = "SC98xx/T 系列签名绕过漏洞",
                    SupportedAddresses = new uint[] { 0x65000800 },
                    SupportedChips = "SC9863A, T610, T618"
                },
                new ExploitInfo
                {
                    Name = "0x65015f48",
                    FileName = ExploitPayloads.Exploit_65015f48,
                    Description = "SC98xx 系列签名绕过漏洞 (变体)",
                    SupportedAddresses = new uint[] { 0x65000000 },
                    SupportedChips = "SC9850, SC9860"
                }
            };
        }

        #endregion

        #region 临时文件提取

        /// <summary>
        /// 将 exploit payload 提取到临时目录
        /// </summary>
        /// <param name="exploitName">Exploit 名称</param>
        /// <returns>提取后的文件路径，失败返回 null</returns>
        public static string ExtractExploitToTemp(string exploitName)
        {
            byte[] payload = GetExploitPayloadByName(exploitName);
            if (payload == null)
                return null;

            string fileName = GetExploitFileName(exploitName);
            string tempPath = Path.Combine(Path.GetTempPath(), "SakuraEDL_Sprd", fileName);

            try
            {
                string dir = Path.GetDirectoryName(tempPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(tempPath, payload);
                return tempPath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 清理临时提取的文件
        /// </summary>
        public static void CleanupTemp()
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "SakuraEDL_Sprd");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // 忽略清理错误
            }
        }

        #endregion

        #region 资源包打包工具

        /// <summary>
        /// 从指定目录创建资源包
        /// </summary>
        /// <param name="sourceDir">源目录</param>
        /// <param name="outputPath">输出路径 (可选，默认为程序目录下的 sprd_resources.pak)</param>
        public static void CreateResourcePak(string sourceDir, string outputPath = null)
        {
            if (string.IsNullOrEmpty(outputPath))
                outputPath = GetPakPath();

            SprdResourcePak.CreatePakFromDirectory(sourceDir, outputPath);
        }

        /// <summary>
        /// 从嵌入资源创建资源包
        /// </summary>
        /// <param name="outputPath">输出路径</param>
        public static void CreateResourcePakFromEmbedded(string outputPath = null)
        {
            if (string.IsNullOrEmpty(outputPath))
                outputPath = GetPakPath();

            var resources = new System.Collections.Generic.List<(string Name, byte[] Data, SprdResourcePak.ResourceType Type)>();

            // 添加所有 exploit 资源
            foreach (var exploit in GetAvailableExploits())
            {
                byte[] data = LoadEmbeddedResource(exploit.FileName);
                if (data != null)
                {
                    resources.Add((exploit.FileName, data, SprdResourcePak.ResourceType.Exploit));
                }
            }

            if (resources.Count > 0)
            {
                SprdResourcePak.CreatePak(outputPath, resources);
            }
        }

        #endregion
    }

    /// <summary>
    /// Exploit 信息结构
    /// </summary>
    public class ExploitInfo
    {
        public string Name { get; set; }
        public string FileName { get; set; }
        public string Description { get; set; }
        public uint[] SupportedAddresses { get; set; }
        public string SupportedChips { get; set; }
    }
}
