// ============================================================================
// SakuraEDL - Loader Feature Detector | Loader 功能检测器
// ============================================================================
// [ZH] Loader 功能检测 - 检测 Firehose Loader 支持的特性
// [EN] Loader Feature Detector - Detect Firehose Loader supported features
// [JA] Loader機能検出 - Firehose Loaderサポート機能の検出
// [KO] Loader 기능 탐지 - Firehose Loader 지원 기능 감지
// [RU] Детектор функций Loader - Обнаружение функций Firehose Loader
// [ES] Detector de funciones Loader - Detectar funciones de Firehose Loader
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SakuraEDL.Qualcomm.Common
{
    /// <summary>
    /// Loader 功能特性
    /// </summary>
    public class LoaderFeatures
    {
        // 基础功能
        public bool Configure { get; set; }
        public bool Program { get; set; }
        public bool Read { get; set; }
        public bool Erase { get; set; }
        public bool Patch { get; set; }
        public bool Nop { get; set; }
        public bool Power { get; set; }
        
        // 存储类型
        public bool Emmc { get; set; }
        public bool Ufs { get; set; }
        
        // 高级功能
        public bool Peek { get; set; }
        public bool Poke { get; set; }
        public bool Benchmark { get; set; }
        public bool FirmwareWrite { get; set; }
        public bool GetStorageInfo { get; set; }
        public bool SetBootableStorageDrive { get; set; }
        public bool GetCrc16Digest { get; set; }
        public bool GetSha256Digest { get; set; }
        public bool Xml { get; set; }
        
        // 安全状态
        public bool IsRestricted { get; set; }
        
        // 厂商特性
        public bool IsXiaomi { get; set; }
        public bool XiaomiEdlVerification { get; set; }
        public bool XiaomiEdlInternal { get; set; }
        public bool IsMotorola { get; set; }
        public bool IsOppo { get; set; }
        public bool IsVivo { get; set; }
        public bool IsOnePlus { get; set; }
        
        // Loader 信息
        public string LoaderName { get; set; }
        public string LoaderVersion { get; set; }
        public string ChipName { get; set; }
        public string BuildDate { get; set; }
        
        /// <summary>
        /// 推荐的内存类型
        /// </summary>
        public string RecommendedMemoryType
        {
            get
            {
                if (Ufs && !Emmc) return "ufs";
                if (Emmc && !Ufs) return "emmc";
                return Ufs ? "ufs" : "emmc";
            }
        }
        
        /// <summary>
        /// 是否需要认证
        /// </summary>
        public bool RequiresAuth => XiaomiEdlVerification || IsRestricted;
        
        /// <summary>
        /// 是否可以漏洞利用
        /// </summary>
        public bool ExploitPossible => IsXiaomi && XiaomiEdlInternal;
        
        /// <summary>
        /// 获取支持的功能列表
        /// </summary>
        public List<string> GetSupportedFeatures()
        {
            var features = new List<string>();
            
            if (Configure) features.Add("configure");
            if (Program) features.Add("program");
            if (Read) features.Add("read");
            if (Erase) features.Add("erase");
            if (Patch) features.Add("patch");
            if (Nop) features.Add("nop");
            if (Power) features.Add("power");
            if (Peek) features.Add("peek");
            if (Poke) features.Add("poke");
            if (Benchmark) features.Add("benchmark");
            if (FirmwareWrite) features.Add("firmwarewrite");
            if (GetStorageInfo) features.Add("getstorageinfo");
            if (SetBootableStorageDrive) features.Add("setbootablestoragedrive");
            if (GetCrc16Digest) features.Add("getcrc16digest");
            if (GetSha256Digest) features.Add("getsha256digest");
            if (Xml) features.Add("xml");
            if (Emmc) features.Add("emmc");
            if (Ufs) features.Add("ufs");
            
            return features;
        }
        
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Loader Features ===");
            
            if (!string.IsNullOrEmpty(LoaderName))
                sb.AppendLine($"Name: {LoaderName}");
            if (!string.IsNullOrEmpty(ChipName))
                sb.AppendLine($"Chip: {ChipName}");
            if (!string.IsNullOrEmpty(LoaderVersion))
                sb.AppendLine($"Version: {LoaderVersion}");
            
            sb.AppendLine($"Memory: {RecommendedMemoryType}");
            sb.AppendLine($"Restricted: {IsRestricted}");
            sb.AppendLine($"Features: {string.Join(", ", GetSupportedFeatures())}");
            
            if (IsXiaomi)
            {
                sb.AppendLine("--- Xiaomi Info ---");
                sb.AppendLine($"EDL Verification: {XiaomiEdlVerification}");
                sb.AppendLine($"EDL Internal: {XiaomiEdlInternal}");
                sb.AppendLine($"Exploit Possible: {ExploitPossible}");
            }
            
            return sb.ToString();
        }
    }
    
    /// <summary>
    /// Loader 功能检测器
    /// 通过分析 Loader 二进制文件检测其支持的功能
    /// </summary>
    public class LoaderFeatureDetector
    {
        #region Feature Keywords
        
        private static readonly string[] FeatureKeywords = {
            "benchmark", "configure", "emmc", "erase", "firmwarewrite",
            "getcrc16digest", "getsha256digest", "getstorageinfo", "nop",
            "patch", "peek", "poke", "power", "program", "read",
            "setbootablestoragedrive", "ufs", "xml"
        };
        
        private static readonly Dictionary<string, string[]> VendorPatterns = new Dictionary<string, string[]>
        {
            { "xiaomi", new[] { "XIAOMI", "Xiaomi", "xiaomi", "MI ", "Redmi", "POCO" } },
            { "motorola", new[] { "MOTOROLA", "Motorola", "motorola", "MOTO", "moto" } },
            { "oppo", new[] { "OPPO", "Oppo", "oppo", "ColorOS" } },
            { "vivo", new[] { "VIVO", "Vivo", "vivo", "FuntouchOS" } },
            { "oneplus", new[] { "OnePlus", "ONEPLUS", "oneplus", "OxygenOS" } },
            { "realme", new[] { "Realme", "REALME", "realme" } },
            { "samsung", new[] { "SAMSUNG", "Samsung", "samsung" } },
            { "huawei", new[] { "HUAWEI", "Huawei", "huawei", "Honor" } },
        };
        
        #endregion

        #region Detection Methods
        
        /// <summary>
        /// 检测 Loader 功能
        /// </summary>
        public LoaderFeatures DetectFeatures(byte[] loaderData)
        {
            if (loaderData == null || loaderData.Length == 0)
                return null;
            
            var features = new LoaderFeatures();
            var loaderString = Encoding.ASCII.GetString(loaderData);
            
            // 检测基础功能
            DetectBasicFeatures(loaderData, loaderString, features);
            
            // 检测厂商
            DetectVendor(loaderString, features);
            
            // 检测安全状态
            DetectSecurityStatus(loaderString, features);
            
            // 提取 Loader 信息
            ExtractLoaderInfo(loaderString, features);
            
            return features;
        }
        
        /// <summary>
        /// 检测基础功能
        /// </summary>
        private void DetectBasicFeatures(byte[] data, string str, LoaderFeatures features)
        {
            // 搜索功能关键字
            var offsets = FindPatternOffsets(data, "setbootablestoragedrive");
            offsets.AddRange(FindPatternOffsets(data, "getstorageinfo"));
            
            foreach (var offset in offsets)
            {
                // 读取周围的数据
                int start = Math.Max(0, (int)offset - 400);
                int length = Math.Min(400, data.Length - start);
                var nearbyData = new byte[length];
                Array.Copy(data, start, nearbyData, 0, length);
                
                // 转换为字符串进行匹配
                var nearbyStr = CleanString(Encoding.ASCII.GetString(nearbyData));
                
                CheckFeatureKeywords(nearbyStr, features);
            }
            
            // 直接检测
            features.Configure |= str.Contains("configure");
            features.Program |= str.Contains("program");
            features.Read |= str.Contains("read");
            features.Erase |= str.Contains("erase");
            features.Patch |= str.Contains("patch");
            features.Nop |= str.Contains("nop");
            features.Power |= str.Contains("power");
            features.Peek |= str.Contains("peek");
            features.Poke |= str.Contains("poke");
            features.Emmc |= str.Contains("emmc") || str.Contains("eMMC");
            features.Ufs |= str.Contains("ufs") || str.Contains("UFS");
            features.GetStorageInfo |= str.Contains("getstorageinfo");
            features.SetBootableStorageDrive |= str.Contains("setbootablestoragedrive");
        }
        
        /// <summary>
        /// 检测厂商
        /// </summary>
        private void DetectVendor(string str, LoaderFeatures features)
        {
            foreach (var vendor in VendorPatterns)
            {
                foreach (var pattern in vendor.Value)
                {
                    if (str.Contains(pattern))
                    {
                        switch (vendor.Key)
                        {
                            case "xiaomi":
                                features.IsXiaomi = true;
                                break;
                            case "motorola":
                                features.IsMotorola = true;
                                break;
                            case "oppo":
                                features.IsOppo = true;
                                break;
                            case "vivo":
                                features.IsVivo = true;
                                break;
                            case "oneplus":
                                features.IsOnePlus = true;
                                break;
                        }
                        break;
                    }
                }
            }
            
            // Xiaomi 特殊检测
            if (features.IsXiaomi)
            {
                features.XiaomiEdlVerification = str.Contains("Signature Verification") || 
                                                  str.Contains("EDL Authenticated");
                features.XiaomiEdlInternal = !str.Contains("Blob should init first");
            }
        }
        
        /// <summary>
        /// 检测安全状态
        /// </summary>
        private void DetectSecurityStatus(string str, LoaderFeatures features)
        {
            features.IsRestricted = str.Contains("is restricted") || 
                                    str.Contains("restricted mode") ||
                                    str.Contains("secure boot");
        }
        
        /// <summary>
        /// 提取 Loader 信息
        /// </summary>
        private void ExtractLoaderInfo(string str, LoaderFeatures features)
        {
            // 尝试提取芯片名称
            var chipPatterns = new[] {
                @"(SDM\d{3})",
                @"(MSM\d{4})",
                @"(SM\d{4})",
                @"(QCS\d{3,4})",
                @"(APQ\d{4})",
                @"(MDM\d{4})",
                @"(SDX\d{2})"
            };
            
            foreach (var pattern in chipPatterns)
            {
                var match = Regex.Match(str, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    features.ChipName = match.Groups[1].Value.ToUpper();
                    break;
                }
            }
            
            // 尝试提取构建日期
            var dateMatch = Regex.Match(str, @"((?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}\s+\d{4})", RegexOptions.IgnoreCase);
            if (dateMatch.Success)
            {
                features.BuildDate = dateMatch.Groups[1].Value;
            }
            
            // 尝试提取版本
            var versionMatch = Regex.Match(str, @"Version\s*[:=]?\s*(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (versionMatch.Success)
            {
                features.LoaderVersion = versionMatch.Groups[1].Value;
            }
        }
        
        /// <summary>
        /// 检查功能关键字
        /// </summary>
        private void CheckFeatureKeywords(string str, LoaderFeatures features)
        {
            if (str.Contains("benchmark")) features.Benchmark = true;
            if (str.Contains("configure")) features.Configure = true;
            if (str.Contains("emmc")) features.Emmc = true;
            if (str.Contains("erase")) features.Erase = true;
            if (str.Contains("firmwarewrite")) features.FirmwareWrite = true;
            if (str.Contains("getcrc16digest")) features.GetCrc16Digest = true;
            if (str.Contains("getsha256digest")) features.GetSha256Digest = true;
            if (str.Contains("getstorageinfo")) features.GetStorageInfo = true;
            if (str.Contains("nop")) features.Nop = true;
            if (str.Contains("patch")) features.Patch = true;
            if (str.Contains("peek")) features.Peek = true;
            if (str.Contains("poke")) features.Poke = true;
            if (str.Contains("power")) features.Power = true;
            if (str.Contains("program")) features.Program = true;
            if (str.Contains("read")) features.Read = true;
            if (str.Contains("setbootablestoragedrive")) features.SetBootableStorageDrive = true;
            if (str.Contains("ufs")) features.Ufs = true;
            if (str.Contains("xml")) features.Xml = true;
        }
        
        #endregion

        #region Helper Methods
        
        /// <summary>
        /// 查找模式偏移量
        /// </summary>
        private List<long> FindPatternOffsets(byte[] data, string pattern)
        {
            var offsets = new List<long>();
            var patternBytes = Encoding.ASCII.GetBytes(pattern);
            
            for (int i = 0; i <= data.Length - patternBytes.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < patternBytes.Length; j++)
                {
                    if (data[i + j] != patternBytes[j])
                    {
                        found = false;
                        break;
                    }
                }
                
                if (found)
                {
                    offsets.Add(i);
                }
            }
            
            return offsets;
        }
        
        /// <summary>
        /// 清理字符串
        /// </summary>
        private string CleanString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            
            // 移除非字母数字字符
            var result = new StringBuilder();
            foreach (char c in input)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    result.Append(c);
            }
            
            return result.ToString().ToLower();
        }
        
        #endregion

        #region Static Methods
        
        /// <summary>
        /// 验证 Loader 是否有效
        /// </summary>
        public static bool IsValidLoader(byte[] data)
        {
            if (data == null || data.Length < 1024)
                return false;
            
            // 检查 ELF 头
            if (data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F')
                return true;
            
            // 检查 Qualcomm MBN 头
            if (data[0] == 0x05 || data[0] == 0x06 || data[0] == 0x07)
                return true;
            
            // 检查是否包含 Firehose 关键字
            var str = Encoding.ASCII.GetString(data);
            return str.Contains("firehose") || str.Contains("Firehose") || 
                   str.Contains("DEVPRG") || str.Contains("sahara");
        }
        
        /// <summary>
        /// 检测 Loader 目标芯片
        /// </summary>
        public static string DetectTargetChip(byte[] data)
        {
            var detector = new LoaderFeatureDetector();
            var features = detector.DetectFeatures(data);
            return features?.ChipName;
        }
        
        #endregion
    }
}
