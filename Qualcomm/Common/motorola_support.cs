// ============================================================================
// SakuraEDL - Motorola Support | Motorola 支持
// ============================================================================
// [ZH] Motorola 固件支持 - 解析 SINGLE_N_LONELY 格式固件包
// [EN] Motorola Firmware Support - Parse SINGLE_N_LONELY format packages
// [JA] Motorolaファームウェアサポート - SINGLE_N_LONELY形式解析
// [KO] Motorola 펌웨어 지원 - SINGLE_N_LONELY 형식 패키지 분석
// [RU] Поддержка Motorola - Разбор пакетов формата SINGLE_N_LONELY
// [ES] Soporte Motorola - Análisis de paquetes formato SINGLE_N_LONELY
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace SakuraEDL.Qualcomm.Common
{
    #region Data Models
    
    /// <summary>
    /// Motorola 固件包信息
    /// </summary>
    public class MotorolaPackageInfo
    {
        public string PackagePath { get; set; }
        public string BoardId { get; set; }
        public string BoardName { get; set; }
        public string StorageType { get; set; }
        public string ProgrammerFile { get; set; }
        public string RecipeFile { get; set; }
        public string GptFile { get; set; }
        public string ActiveSlot { get; set; }
        public int ActiveBoot { get; set; }
        public List<MotorolaFileEntry> Files { get; set; } = new List<MotorolaFileEntry>();
        public MotorolaRecipe Recipe { get; set; }
    }
    
    /// <summary>
    /// 文件条目
    /// </summary>
    public class MotorolaFileEntry
    {
        public string Name { get; set; }
        public long Offset { get; set; }
        public ulong Size { get; set; }
    }
    
    #region Recipe Models
    
    [XmlRoot("recipe")]
    public class MotorolaRecipe
    {
        [XmlElement("backup")]
        public List<RecipeBackup> Backups { get; set; } = new List<RecipeBackup>();
        
        [XmlElement("configure")]
        public List<RecipeConfigure> Configurations { get; set; } = new List<RecipeConfigure>();
        
        [XmlElement("ufs")]
        public List<RecipeUfs> UfsItems { get; set; } = new List<RecipeUfs>();
        
        [XmlElement("setbootablestoragedrive")]
        public RecipeBootDrive SetBootableStorageDrive { get; set; }
        
        [XmlElement("flash")]
        public List<RecipeFlash> Flashes { get; set; } = new List<RecipeFlash>();
        
        [XmlElement("wipe")]
        public List<RecipeWipe> Wipes { get; set; } = new List<RecipeWipe>();
    }
    
    public class RecipeBackup
    {
        [XmlAttribute("name")]
        public string Name { get; set; }
        
        [XmlAttribute("skip")]
        public bool Skip { get; set; }
    }
    
    public class RecipeConfigure
    {
        [XmlAttribute("MemoryName")]
        public string MemoryName { get; set; }
        
        [XmlAttribute("SkipStorageInit")]
        public int SkipStorageInit { get; set; }
    }
    
    public class RecipeUfs
    {
        [XmlAttribute("bNumberLU")]
        public int NumberLU { get; set; }
        
        [XmlAttribute("bBootEnable")]
        public int BootEnable { get; set; }
        
        [XmlAttribute("LUNum")]
        public int LUNum { get; set; }
        
        [XmlAttribute("bLUEnable")]
        public int LUEnable { get; set; }
        
        [XmlAttribute("size_in_kb")]
        public int SizeInKb { get; set; }
        
        [XmlAttribute("desc")]
        public string Description { get; set; }
    }
    
    public class RecipeBootDrive
    {
        [XmlAttribute("value")]
        public int Value { get; set; }
    }
    
    public class RecipeFlash
    {
        [XmlAttribute("partition")]
        public string Partition { get; set; }
        
        [XmlAttribute("filename")]
        public string Filename { get; set; }
        
        [XmlAttribute("verbose")]
        public bool Verbose { get; set; }
    }
    
    public class RecipeWipe
    {
        [XmlAttribute("partition")]
        public string Partition { get; set; }
        
        [XmlAttribute("verbose")]
        public bool Verbose { get; set; }
    }
    
    #endregion
    
    #endregion

    /// <summary>
    /// Motorola 固件包支持
    /// 支持 SINGLE_N_LONELY 格式固件包解析和提取
    /// </summary>
    public class MotorolaSupport
    {
        #region Constants
        
        private const string MAGIC_HEADER = "SINGLE_N_LONELY";
        private const string MAGIC_FOOTER = "LONELY_N_SINGLE";
        private const int HEADER_SIZE = 256;
        private const int ENTRY_NAME_SIZE = 248;
        private const int ALIGNMENT = 4096;
        
        #endregion

        #region Events
        
        public event Action<string> OnLog;
        public event Action<int> OnProgress;
        
        #endregion

        #region Package Parsing
        
        /// <summary>
        /// 检查是否为 Motorola 固件包
        /// </summary>
        public static bool IsMotorolaPackage(string filePath)
        {
            if (!File.Exists(filePath))
                return false;
            
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    var header = new byte[15];
                    fs.Read(header, 0, 15);
                    return Encoding.ASCII.GetString(header) == MAGIC_HEADER;
                }
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// 解析 Motorola 固件包
        /// </summary>
        public async Task<MotorolaPackageInfo> ParsePackageAsync(string filePath)
        {
            if (!IsMotorolaPackage(filePath))
                throw new InvalidOperationException("Not a valid Motorola package file");
            
            var info = new MotorolaPackageInfo
            {
                PackagePath = filePath
            };
            
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                // 跳过头部
                long seekOffset = HEADER_SIZE;
                
                // 解析文件条目
                for (int i = 0; i < 64; i++)
                {
                    fs.Seek(seekOffset, SeekOrigin.Begin);
                    
                    // 读取文件名
                    var nameBytes = new byte[ENTRY_NAME_SIZE];
                    await fs.ReadAsync(nameBytes, 0, ENTRY_NAME_SIZE);
                    string name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                    
                    // 检查结束标记
                    if (name == MAGIC_FOOTER)
                        break;
                    
                    // 读取文件大小
                    var sizeBytes = new byte[8];
                    await fs.ReadAsync(sizeBytes, 0, 8);
                    ulong length = BitConverter.ToUInt64(sizeBytes, 0);
                    
                    // 计算偏移
                    long offset = seekOffset + 256;
                    long pad = (long)(length % ALIGNMENT);
                    if (pad != 0) pad = ALIGNMENT - pad;
                    
                    // 添加文件条目
                    info.Files.Add(new MotorolaFileEntry
                    {
                        Name = name,
                        Offset = offset,
                        Size = length
                    });
                    
                    // 识别特殊文件
                    if (name.ToLower().Contains("gpt"))
                        info.GptFile = name;
                    
                    // 更新偏移
                    seekOffset = offset + (long)length + pad;
                    
                    Log($"Found: {name} ({length} bytes)");
                }
            }
            
            return info;
        }
        
        /// <summary>
        /// 提取固件包
        /// </summary>
        public async Task<string> ExtractPackageAsync(string filePath, string outputDir = null)
        {
            var info = await ParsePackageAsync(filePath);
            
            // 设置输出目录
            if (string.IsNullOrEmpty(outputDir))
            {
                outputDir = Path.Combine(Path.GetDirectoryName(filePath), "output");
            }
            
            // 创建目录
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
            Directory.CreateDirectory(outputDir);
            
            // 提取文件
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                int count = 0;
                foreach (var entry in info.Files)
                {
                    var outputPath = Path.Combine(outputDir, entry.Name);
                    await ExtractFileAsync(fs, entry, outputPath);
                    
                    count++;
                    Progress(count * 100 / info.Files.Count);
                    Log($"Extracted: {entry.Name}");
                }
            }
            
            // 解析元数据
            await ParseMetadataAsync(info, outputDir);
            
            return outputDir;
        }
        
        /// <summary>
        /// 提取单个文件
        /// </summary>
        private async Task ExtractFileAsync(FileStream source, MotorolaFileEntry entry, string outputPath)
        {
            source.Seek(entry.Offset, SeekOrigin.Begin);
            
            using (var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[ALIGNMENT];
                long remaining = (long)entry.Size;
                
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = await source.ReadAsync(buffer, 0, toRead);
                    if (read == 0) break;
                    
                    await outFs.WriteAsync(buffer, 0, read);
                    remaining -= read;
                }
            }
        }
        
        /// <summary>
        /// 解析元数据
        /// </summary>
        private async Task ParseMetadataAsync(MotorolaPackageInfo info, string outputDir)
        {
            // 查找 index.xml
            var indexFile = info.Files.FirstOrDefault(f => f.Name.ToLower().Contains("index"));
            if (indexFile != null)
            {
                var indexPath = Path.Combine(outputDir, indexFile.Name);
                if (File.Exists(indexPath))
                {
                    ParseIndexXml(indexPath, info);
                }
            }
            
            // 查找 pkg.xml
            if (!string.IsNullOrEmpty(info.ProgrammerFile))
            {
                var pkgPath = Path.Combine(outputDir, info.ProgrammerFile);
                pkgPath = Path.ChangeExtension(pkgPath, null); // 移除扩展名
                
                // 搜索 pkg 文件
                foreach (var file in info.Files)
                {
                    if (file.Name.ToLower().Contains("pkg") && file.Name.ToLower().EndsWith(".xml"))
                    {
                        var path = Path.Combine(outputDir, file.Name);
                        if (File.Exists(path))
                        {
                            ParsePkgXml(path, info);
                            break;
                        }
                    }
                }
            }
            
            // 加载 Recipe
            if (!string.IsNullOrEmpty(info.RecipeFile))
            {
                var recipePath = Path.Combine(outputDir, info.RecipeFile);
                if (File.Exists(recipePath))
                {
                    info.Recipe = LoadRecipe(recipePath);
                    DetectSlotFromRecipe(info);
                }
            }
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// 解析 index.xml
        /// </summary>
        private void ParseIndexXml(string path, MotorolaPackageInfo info)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                
                // 读取 board 信息
                var boardNode = doc.SelectSingleNode("//board");
                if (boardNode != null)
                {
                    info.BoardId = boardNode.Attributes?["id"]?.Value;
                    info.BoardName = boardNode.Attributes?["name"]?.Value;
                    info.StorageType = boardNode.Attributes?["storage.type"]?.Value;
                }
                
                // 读取 package 信息
                var packageNode = doc.SelectSingleNode("//package");
                if (packageNode != null)
                {
                    var pkgFile = packageNode.Attributes?["filename"]?.Value;
                    if (!string.IsNullOrEmpty(pkgFile))
                    {
                        info.ProgrammerFile = pkgFile;
                    }
                }
                
                Log($"Board: {info.BoardName} ({info.BoardId}), Storage: {info.StorageType}");
            }
            catch (Exception ex)
            {
                Log($"Failed to parse index.xml: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 解析 pkg.xml
        /// </summary>
        private void ParsePkgXml(string path, MotorolaPackageInfo info)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                
                // 读取 programmer
                var programmerNode = doc.SelectSingleNode("//programmer");
                if (programmerNode != null)
                {
                    info.ProgrammerFile = programmerNode.Attributes?["filename"]?.Value;
                }
                
                // 读取 recipe
                var recipeNode = doc.SelectSingleNode("//recipe");
                if (recipeNode != null)
                {
                    info.RecipeFile = recipeNode.Attributes?["filename"]?.Value;
                }
                
                Log($"Programmer: {info.ProgrammerFile}, Recipe: {info.RecipeFile}");
            }
            catch (Exception ex)
            {
                Log($"Failed to parse pkg.xml: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 加载 Recipe
        /// </summary>
        private MotorolaRecipe LoadRecipe(string path)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(MotorolaRecipe));
                using (var fs = new FileStream(path, FileMode.Open))
                {
                    return (MotorolaRecipe)serializer.Deserialize(fs);
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to load recipe: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// 从 Recipe 检测 Slot
        /// </summary>
        private void DetectSlotFromRecipe(MotorolaPackageInfo info)
        {
            if (info.Recipe == null)
                return;
            
            // 从 flash 项检测 slot
            foreach (var flash in info.Recipe.Flashes)
            {
                if (!string.IsNullOrEmpty(flash.Partition))
                {
                    var partition = flash.Partition;
                    if (partition.EndsWith("_a") || partition.EndsWith("_b"))
                    {
                        info.ActiveSlot = partition.Substring(partition.Length - 1);
                        break;
                    }
                }
            }
            
            // 从 setbootablestoragedrive 获取 boot drive
            if (info.Recipe.SetBootableStorageDrive != null)
            {
                info.ActiveBoot = info.Recipe.SetBootableStorageDrive.Value;
            }
            
            Log($"Active Slot: {info.ActiveSlot ?? "N/A"}, Boot Drive: {info.ActiveBoot}");
        }
        
        #endregion

        #region GPT Processing
        
        /// <summary>
        /// 清理 GPT 数据 (移除 Motorola 特殊头)
        /// </summary>
        public byte[] CleanGptData(byte[] gptData, int sectorSize = 4096)
        {
            if (gptData == null || gptData.Length < sectorSize * 2)
                return gptData;
            
            // 查找 EFI PART 签名
            var signature = new byte[] { 0x45, 0x46, 0x49, 0x20, 0x50, 0x41, 0x52, 0x54 };
            int offset = FindPattern(gptData, signature, sectorSize * 10);
            
            if (offset > 0 && offset > sectorSize)
            {
                // 移除 Motorola 头部，保留标准 GPT
                int newStart = offset - sectorSize;
                var cleanData = new byte[gptData.Length - newStart];
                Array.Copy(gptData, newStart, cleanData, 0, cleanData.Length);
                return cleanData;
            }
            
            return gptData;
        }
        
        /// <summary>
        /// 查找模式
        /// </summary>
        private int FindPattern(byte[] data, byte[] pattern, int maxSearch = -1)
        {
            if (maxSearch < 0) maxSearch = data.Length;
            maxSearch = Math.Min(maxSearch, data.Length - pattern.Length);
            
            for (int i = 0; i <= maxSearch; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                
                if (match) return i;
            }
            
            return -1;
        }
        
        #endregion

        #region RawProgram Generation
        
        /// <summary>
        /// 生成 rawprogram.xml
        /// </summary>
        public async Task GenerateRawProgramXmlAsync(
            MotorolaPackageInfo info, 
            string outputDir, 
            List<PartitionInfo> partitions)
        {
            // 按 LUN 分组
            var lunGroups = partitions.GroupBy(p => p.PhysicalPartitionNumber)
                                       .ToDictionary(g => g.Key, g => g.ToList());
            
            foreach (var group in lunGroups)
            {
                int lun = group.Key;
                var xmlPath = Path.Combine(outputDir, $"rawprogram{lun}.xml");
                
                using (var writer = new StreamWriter(xmlPath, false, Encoding.UTF8))
                {
                    await writer.WriteLineAsync("<?xml version=\"1.0\" ?>");
                    await writer.WriteLineAsync("<data>");
                    await writer.WriteLineAsync("<!--** NOTE: Generated by SakuraEDL **-->");
                    
                    foreach (var partition in group.Value)
                    {
                        string filename = GetPartitionFilename(partition.Name, info);
                        
                        await writer.WriteLineAsync(
                            $"<program SECTOR_SIZE_IN_BYTES=\"{partition.SectorSize}\" " +
                            $"file_sector_offset=\"0\" filename=\"{filename}\" " +
                            $"label=\"{partition.Name}\" " +
                            $"num_partition_sectors=\"{partition.NumSectors}\" " +
                            $"physical_partition_number=\"{lun}\" " +
                            $"start_sector=\"{partition.StartSector}\" />");
                    }
                    
                    await writer.WriteLineAsync("</data>");
                }
                
                Log($"Generated: rawprogram{lun}.xml");
            }
        }
        
        /// <summary>
        /// 获取分区对应的文件名
        /// </summary>
        private string GetPartitionFilename(string partitionName, MotorolaPackageInfo info)
        {
            // 查找匹配的文件
            foreach (var file in info.Files)
            {
                if (file.Name.Equals(partitionName, StringComparison.OrdinalIgnoreCase))
                    return file.Name;
                
                // 检查 slot 匹配
                if (!string.IsNullOrEmpty(info.ActiveSlot))
                {
                    // 分区名带 slot 后缀，查找不带后缀的文件
                    if (partitionName.EndsWith($"_{info.ActiveSlot}"))
                    {
                        var baseName = partitionName.Substring(0, partitionName.Length - 2);
                        var match = info.Files.FirstOrDefault(f => 
                            f.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                            return match.Name;
                    }
                }
            }
            
            return partitionName;
        }
        
        #endregion

        #region Helper Classes
        
        /// <summary>
        /// 分区信息 (简化版)
        /// </summary>
        public class PartitionInfo
        {
            public string Name { get; set; }
            public long StartSector { get; set; }
            public long NumSectors { get; set; }
            public int SectorSize { get; set; } = 4096;
            public int PhysicalPartitionNumber { get; set; }
        }
        
        #endregion

        #region Logging
        
        private void Log(string message) => OnLog?.Invoke(message);
        private void Progress(int percent) => OnProgress?.Invoke(percent);
        
        #endregion
    }
}
