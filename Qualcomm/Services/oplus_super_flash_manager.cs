// ============================================================================
// SakuraEDL - OPlus Super Flash Manager | OPlus Super 刷写管理器
// ============================================================================
// [ZH] OPlus Super 刷写 - OPPO/OnePlus/Realme Super 分区智能刷写
// [EN] OPlus Super Flash - Smart flashing for OPPO/OnePlus/Realme Super partition
// [JA] OPlus Superフラッシュ - OPPO/OnePlus/Realme Superパーティションの刷写
// [KO] OPlus Super 플래시 - OPPO/OnePlus/Realme Super 파티션 스마트 플래싱
// [RU] OPlus Super Flash - Умная прошивка Super разделов OPPO/OnePlus/Realme
// [ES] OPlus Super Flash - Flasheo inteligente de partición Super OPlus
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using SakuraEDL.Qualcomm.Common;
using SakuraEDL.Qualcomm.Models;

namespace SakuraEDL.Qualcomm.Services
{
    /// <summary>
    /// OPLUS (OPPO/Realme/OnePlus) Super 分区拆解写入管理器
    /// </summary>
    public class OplusSuperFlashManager
    {
        private readonly Action<string> _log;
        private readonly LpMetadataParser _lpParser;

        public OplusSuperFlashManager(Action<string> log)
        {
            _log = log;
            _lpParser = new LpMetadataParser();
        }

        public class FlashTask
        {
            public string PartitionName { get; set; }
            public string FilePath { get; set; }
            public long PhysicalSector { get; set; }
            public long SizeInBytes { get; set; }
            public long ExtentSizeInLp { get; set; }  // LP Metadata 中定义的大小（用于校验）
        }
        
        /// <summary>
        /// 校验结果
        /// </summary>
        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
            public long TotalDataSize { get; set; }
            public int PartitionCount { get; set; }
        }

        /// <summary>
        /// 扫描固件目录，生成 Super 拆解写入任务列表
        /// </summary>
        public async Task<List<FlashTask>> PrepareSuperTasksAsync(string firmwareRoot, long superStartSector, int sectorSize, string activeSlot = "a", string nvId = "", long superPartitionSize = 0)
        {
            var tasks = new List<FlashTask>();
            
            // 1. 查找关键文件
            string imagesDir = Path.Combine(firmwareRoot, "IMAGES");
            string metaDir = Path.Combine(firmwareRoot, "META");
            
            _log(string.Format("  [调试] 固件根目录: {0}", firmwareRoot));
            _log(string.Format("  [调试] IMAGES 目录: {0} (存在: {1})", imagesDir, Directory.Exists(imagesDir)));
            _log(string.Format("  [调试] META 目录: {0} (存在: {1})", metaDir, Directory.Exists(metaDir)));
            
            if (!Directory.Exists(imagesDir)) imagesDir = firmwareRoot;
            
            // 优先查找带 NV_ID 的 Metadata: super_meta.{nvId}.raw
            string superMetaPath = null;
            if (!string.IsNullOrEmpty(nvId))
            {
                superMetaPath = Directory.GetFiles(imagesDir, $"super_meta.{nvId}.raw").FirstOrDefault();
            }
            
            if (string.IsNullOrEmpty(superMetaPath))
            {
                superMetaPath = Directory.GetFiles(imagesDir, "super_meta*.raw").FirstOrDefault();
                
                // [关键] 如果设备无法读取 NV_ID，则从固件包文件名自动提取
                if (!string.IsNullOrEmpty(superMetaPath) && string.IsNullOrEmpty(nvId))
                {
                    nvId = ExtractNvIdFromFilename(superMetaPath);
                }
            }

            // 优先查找带 NV_ID 的映射表: super_def.{nvId}.json
            string superDefPath = null;
            if (!string.IsNullOrEmpty(nvId))
            {
                superDefPath = Path.Combine(metaDir, $"super_def.{nvId}.json");
            }
            if (string.IsNullOrEmpty(superDefPath) || !File.Exists(superDefPath))
            {
                superDefPath = Path.Combine(metaDir, "super_def.json");
            }
            
            if (string.IsNullOrEmpty(superMetaPath) || !File.Exists(superMetaPath))
            {
                // 如果没有 super_meta.raw，尝试寻找 super.img 本身 (如果是完整镜像)
                string fullSuperPath = Path.Combine(imagesDir, "super.img");
                if (File.Exists(fullSuperPath))
                {
                    _log("发现全量 super.img");
                    tasks.Add(new FlashTask { 
                        PartitionName = "super", 
                        FilePath = fullSuperPath, 
                        PhysicalSector = superStartSector, 
                        SizeInBytes = new FileInfo(fullSuperPath).Length 
                    });
                    return tasks;
                }
                
                _log("未找到 super_meta.raw 或 super.img");
                return tasks;
            }

            // 2. 解析 LP Metadata
            byte[] metaData = File.ReadAllBytes(superMetaPath);
            var lpPartitions = _lpParser.ParseMetadata(metaData);
            _log(string.Format("解析 Super 布局: {0} 个逻辑卷{1}", lpPartitions.Count, string.IsNullOrEmpty(nvId) ? "" : string.Format(" (NV: {0})", nvId)));

            // 3. 读取映射
            Dictionary<string, string> nameToPathMap = LoadPartitionMapManual(superDefPath, imagesDir);
            
            // 调试：显示 map 中的键
            if (nameToPathMap.Count > 0)
            {
                _log(string.Format("  [调试] Map 键: {0}", string.Join(", ", nameToPathMap.Keys.Take(5))));
            }

            // 4. 构建任务 - LP Metadata 写入 super+1 (主) 和 super+2 (备)
            tasks.Add(new FlashTask
            {
                PartitionName = "super",
                FilePath = superMetaPath,
                PhysicalSector = superStartSector + 1,
                SizeInBytes = metaData.Length
            });
            tasks.Add(new FlashTask
            {
                PartitionName = "super",
                FilePath = superMetaPath,
                PhysicalSector = superStartSector + 2,
                SizeInBytes = metaData.Length
            });

            string suffix = "_" + activeSlot.ToLower();
            var missingPartitions = new List<string>();
            var sizeWarnings = new List<string>();
            
            _log(string.Format("  [调试] 遍历 {0} 个 LP 分区 (筛选槽位: {1})", lpPartitions.Count, suffix));
            _log(string.Format("  [调试] LP 分区名: {0}", string.Join(", ", lpPartitions.Take(5).Select(p => p.Name))));
            
            foreach (var lp in lpPartitions)
            {
                // 跳过没有 LINEAR Extent 的分区或非当前槽位
                if (!lp.HasLinearExtent)
                {
                    _log(string.Format("  [调试] 跳过 {0}: 无 LINEAR Extent", lp.Name));
                    continue;
                }
                if ((lp.Name.EndsWith("_a") || lp.Name.EndsWith("_b")) && !lp.Name.EndsWith(suffix))
                {
                    continue;  // 跳过另一个槽位的分区（正常情况，不记录）
                }

                string imgPath = FindImagePath(lp.Name, nameToPathMap, imagesDir, nvId);
                _log(string.Format("  [调试] FindImagePath({0}) -> {1}", lp.Name, imgPath ?? "null"));

                if (imgPath != null)
                {
                    long realSize = GetImageRealSize(imgPath);
                    long expandedSize = GetImageExpandedSize(imgPath);
                    long lpExtentSize = lp.TotalSizeLpSectors * 512;  // LP 扇区是 512 字节
                    long deviceSectorOffset = lp.GetDeviceSectorOffset(sectorSize);
                    if (deviceSectorOffset < 0)
                    {
                        _log(string.Format("  [调试] 跳过 {0}: deviceSectorOffset={1} (无效)", lp.Name, deviceSectorOffset));
                        continue;
                    }
                    
                    // 校验: 镜像展开后的大小不能超过 LP Metadata 中定义的大小
                    if (expandedSize > lpExtentSize)
                    {
                        sizeWarnings.Add(string.Format("{0}: 镜像 {1} MB > LP定义 {2} MB", 
                            lp.Name, expandedSize / 1024 / 1024, lpExtentSize / 1024 / 1024));
                    }
                    
                    long physicalSector = superStartSector + deviceSectorOffset;
                    
                    // 校验: 写入位置不能超出 super 分区
                    if (superPartitionSize > 0)
                    {
                        long endSector = deviceSectorOffset + (expandedSize / sectorSize);
                        long superSectors = superPartitionSize / sectorSize;
                        if (endSector > superSectors)
                        {
                            sizeWarnings.Add(string.Format("{0}: 写入位置超出 super 边界 (结束扇区 {1} > {2})", 
                                lp.Name, endSector, superSectors));
                        }
                    }
                    
                    tasks.Add(new FlashTask
                    {
                        PartitionName = lp.Name,
                        FilePath = imgPath,
                        PhysicalSector = physicalSector,
                        SizeInBytes = realSize,
                        ExtentSizeInLp = lpExtentSize
                    });
                    _log(string.Format("  {0} -> 扇区 {1} ({2} MB)", lp.Name, physicalSector, realSize / 1024 / 1024));
                }
                else
                {
                    missingPartitions.Add(lp.Name);
                }
            }
            
            // 报告缺失的分区（但不阻止继续）
            if (missingPartitions.Count > 0)
            {
                _log(string.Format("  [提示] {0} 个逻辑分区无镜像文件: {1}", 
                    missingPartitions.Count, string.Join(", ", missingPartitions.Take(5))));
            }
            
            // 报告大小警告（但不阻止继续，因为 Sparse 镜像实际写入可能更小）
            if (sizeWarnings.Count > 0)
            {
                foreach (var w in sizeWarnings)
                {
                    _log(string.Format("  [警告] {0}", w));
                }
            }

            // 按物理扇区偏移排序，确保顺序写入（与官方工具行为一致，提高写入效率）
            tasks = tasks.OrderBy(t => t.PhysicalSector).ToList();
            
            return tasks;
        }
        
        /// <summary>
        /// 校验 Super 刷写任务
        /// </summary>
        public ValidationResult ValidateTasks(List<FlashTask> tasks, long superPartitionSize, int sectorSize)
        {
            var result = new ValidationResult { IsValid = true };
            
            if (tasks == null || tasks.Count == 0)
            {
                result.IsValid = false;
                result.Errors.Add("没有可执行的刷写任务");
                return result;
            }
            
            result.PartitionCount = tasks.Count;
            result.TotalDataSize = tasks.Sum(t => t.SizeInBytes);
            
            foreach (var task in tasks)
            {
                // 1. 检查文件存在性
                if (!File.Exists(task.FilePath))
                {
                    result.IsValid = false;
                    result.Errors.Add(string.Format("文件不存在: {0}", task.FilePath));
                    continue;
                }
                
                // 2. 检查写入位置
                if (superPartitionSize > 0 && task.PartitionName != "super_meta")
                {
                    // 计算相对于 super 起始位置的偏移
                    // PhysicalSector 是绝对扇区号，需要减去 super 起始扇区
                }
            }
            
            // 3. 整体大小检查
            if (superPartitionSize > 0 && result.TotalDataSize > superPartitionSize)
            {
                result.Warnings.Add(string.Format("总数据量 {0} MB 可能超过 super 分区 {1} MB (Sparse 镜像实际更小)",
                    result.TotalDataSize / 1024 / 1024, superPartitionSize / 1024 / 1024));
            }
            
            return result;
        }

        /// <summary>
        /// 解析 super_def.json 获取分区名到镜像路径的映射
        /// 支持两种路径格式:
        /// 1. IMAGES/system.img - 直接在 IMAGES 目录下
        /// 2. IMAGES/my_stock/my_stock.xxx.img - 在子目录下
        /// </summary>
        private Dictionary<string, string> LoadPartitionMapManual(string defPath, string imagesDir)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(defPath)) return map;

            try
            {
                string content = File.ReadAllText(defPath);
                
                // 找到 "partitions" 数组的开始位置
                int partitionsStart = content.IndexOf("\"partitions\"");
                if (partitionsStart < 0) return map;
                
                int arrayStart = content.IndexOf('[', partitionsStart);
                if (arrayStart < 0) return map;
                
                // 找到 partitions 数组的结束位置
                int depth = 1;
                int arrayEnd = arrayStart + 1;
                while (depth > 0 && arrayEnd < content.Length)
                {
                    if (content[arrayEnd] == '[') depth++;
                    else if (content[arrayEnd] == ']') depth--;
                    arrayEnd++;
                }
                
                string partitionsJson = content.Substring(arrayStart, arrayEnd - arrayStart);
                
                // 解析每个 partition 对象
                // 匹配 { ... } 块
                var blockMatches = Regex.Matches(partitionsJson, @"\{[^{}]*\}", RegexOptions.Singleline);
                foreach (Match block in blockMatches)
                {
                    string blockContent = block.Value;
                    
                    // 提取 name
                    var nameMatch = Regex.Match(blockContent, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    if (!nameMatch.Success) continue;
                    string name = nameMatch.Groups[1].Value;
                    
                    // 提取 path (注意: _b 槽位通常没有 path)
                    var pathMatch = Regex.Match(blockContent, "\"path\"\\s*:\\s*\"([^\"]+)\"");
                    if (!pathMatch.Success) continue;
                    string relPath = pathMatch.Groups[1].Value;
                    
                    try
                    {
                        // 先规范化路径分隔符 (JSON 中是 / ，Windows 需要 \)
                        relPath = relPath.Replace('/', Path.DirectorySeparatorChar);
                        
                        // 去掉开头的 IMAGES\ 或 IMAGES/
                        string prefix = "IMAGES" + Path.DirectorySeparatorChar;
                        if (relPath.StartsWith(prefix))
                        {
                            relPath = relPath.Substring(prefix.Length);
                        }
                        
                        // 构建完整路径
                        string fullPath = Path.Combine(imagesDir, relPath);
                        
                        if (File.Exists(fullPath)) 
                        {
                            map[name] = fullPath;
                        }
                        else
                        {
                            // 调试：报告找不到的文件
                            _log(string.Format("  [调试] 未找到: {0} -> {1}", name, fullPath));
                        }
                    }
                    catch (Exception pathEx)
                    {
                        _log(string.Format("  [警告] 无效路径 {0}: {1}", name, pathEx.Message));
                    }
                }
                
                _log(string.Format("  从 super_def.json 加载 {0} 个分区映射", map.Count));
            }
            catch (Exception ex) 
            { 
                _log(string.Format("  [警告] 解析 super_def.json 失败: {0}", ex.Message));
            }
            return map;
        }

        private string FindImagePath(string lpName, Dictionary<string, string> map, string imagesDir, string nvId = "")
        {
            try
            {
                // 1. Map 优先 (如果 super_def.json 中有明确路径)
                if (map.TryGetValue(lpName, out string path)) return path;

                // 2. 尝试带 NV_ID 的文件名匹配: {lpName}.{nvId}.img 或 {baseName}.{nvId}.img
                if (!string.IsNullOrEmpty(nvId))
                {
                    try
                    {
                        string nvPattern = string.Format("{0}.{1}.img", lpName, nvId);
                        var nvFiles = Directory.GetFiles(imagesDir, nvPattern);
                        if (nvFiles.Length > 0) return nvFiles[0];

                        // 去掉槽位再找
                        string baseName = lpName;
                        if (baseName.EndsWith("_a") || baseName.EndsWith("_b"))
                            baseName = baseName.Substring(0, baseName.Length - 2);
                        
                        nvPattern = string.Format("{0}.{1}.img", baseName, nvId);
                        nvFiles = Directory.GetFiles(imagesDir, nvPattern);
                        if (nvFiles.Length > 0) return nvFiles[0];
                    }
                    catch { }
                }

                // 3. 原有逻辑：去掉槽位名再找
                string searchName = lpName;
                if (searchName.EndsWith("_a") || searchName.EndsWith("_b"))
                    searchName = searchName.Substring(0, searchName.Length - 2);

                if (map.TryGetValue(searchName, out path)) return path;

                // 4. 通用磁盘扫描（子目录）
                try
                {
                    // 先检查子目录 (如 my_stock/my_stock.xxx.img)
                    string subDir = Path.Combine(imagesDir, searchName);
                    if (Directory.Exists(subDir))
                    {
                        var subFiles = Directory.GetFiles(subDir, searchName + "*.img");
                        if (subFiles.Length > 0) return subFiles[0];
                    }
                }
                catch { }

                // 5. 直接在 imagesDir 扫描
                string[] patterns = { searchName + ".img", lpName + ".img" };
                foreach (var pattern in patterns)
                {
                    try
                    {
                        var files = Directory.GetFiles(imagesDir, pattern);
                        if (files.Length > 0) return files[0];
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 获取镜像的实际数据大小（Sparse 镜像只计算有效数据）
        /// </summary>
        private long GetImageRealSize(string path)
        {
            if (SparseStream.IsSparseFile(path))
            {
                using (var ss = SparseStream.Open(path))
                {
                    // 返回实际数据大小，不含 DONT_CARE
                    return ss.GetRealDataSize();
                }
            }
            return new FileInfo(path).Length;
        }

        /// <summary>
        /// 获取镜像展开后的完整大小
        /// </summary>
        private long GetImageExpandedSize(string path)
        {
            if (SparseStream.IsSparseFile(path))
            {
                using (var ss = SparseStream.Open(path))
                {
                    return ss.Length;
                }
            }
            return new FileInfo(path).Length;
        }

        /// <summary>
        /// 从文件名中提取 NV_ID
        /// 例如: super_meta.10010111.raw -> 10010111
        /// </summary>
        private string ExtractNvIdFromFilename(string filePath)
        {
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath); // super_meta.10010111
                
                // 匹配格式: super_meta.{nvId} 或 super_def.{nvId}
                var match = Regex.Match(fileName, @"^super_(?:meta|def)\.(\d+)$");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
                
                // 备用匹配: 任意文件名中间的数字部分
                // 例如: system.10010111 -> 10010111
                var parts = fileName.Split('.');
                if (parts.Length >= 2)
                {
                    string potentialNvId = parts[parts.Length - 1];
                    // NV_ID 通常是 8 位或更长的数字
                    if (Regex.IsMatch(potentialNvId, @"^\d{6,}$"))
                    {
                        return potentialNvId;
                    }
                }
            }
            catch { }
            
            return null;
        }
    }
}
