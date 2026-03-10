// ============================================================================
// SakuraEDL - FDL Index Manager | FDL 索引管理器
// ============================================================================
// [ZH] FDL 索引 - 管理 FDL 文件与设备型号的对应关系
// [EN] FDL Index Manager - Map FDL files to device models
// [JA] FDLインデックス - FDLファイルとデバイスモデルの対応を管理
// [KO] FDL 인덱스 관리자 - FDL 파일과 기기 모델 매핑
// [RU] Менеджер индекса FDL - Соответствие FDL файлов и моделей устройств
// [ES] Gestor de índice FDL - Mapear archivos FDL a modelos de dispositivo
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SakuraEDL.Spreadtrum.Resources
{
    /// <summary>
    /// FDL 索引管理器 - 管理 FDL 与设备型号的对应关系
    /// </summary>
    public static class FdlIndex
    {
        #region 数据结构

        /// <summary>
        /// FDL 索引条目
        /// </summary>
        public class FdlIndexEntry
        {
            /// <summary>芯片名称 (如 SC8541E)</summary>
            public string ChipName { get; set; }

            /// <summary>芯片 ID</summary>
            public uint ChipId { get; set; }

            /// <summary>设备型号 (如 A23-Pro)</summary>
            public string DeviceModel { get; set; }

            /// <summary>品牌 (如 Samsung)</summary>
            public string Brand { get; set; }

            /// <summary>市场名称 (如 Galaxy A23)</summary>
            public string MarketName { get; set; }

            /// <summary>FDL1 文件名</summary>
            public string Fdl1File { get; set; }

            /// <summary>FDL2 文件名</summary>
            public string Fdl2File { get; set; }

            /// <summary>FDL1 加载地址</summary>
            public uint Fdl1Address { get; set; }

            /// <summary>FDL2 加载地址</summary>
            public uint Fdl2Address { get; set; }

            /// <summary>FDL1 文件哈希 (用于校验)</summary>
            public string Fdl1Hash { get; set; }

            /// <summary>FDL2 文件哈希</summary>
            public string Fdl2Hash { get; set; }

            /// <summary>备注</summary>
            public string Notes { get; set; }

            /// <summary>是否已验证可用</summary>
            public bool Verified { get; set; }

            /// <summary>唯一键</summary>
            public string Key => $"{ChipName}/{DeviceModel}".ToLower();
        }

        /// <summary>
        /// FDL 索引文件
        /// </summary>
        public class FdlIndexFile
        {
            public int Version { get; set; } = 1;
            public string UpdateTime { get; set; }
            public int TotalDevices { get; set; }
            public List<FdlIndexEntry> Entries { get; set; } = new List<FdlIndexEntry>();
        }

        #endregion

        #region 状态

        private static Dictionary<string, FdlIndexEntry> _index = new Dictionary<string, FdlIndexEntry>();
        private static readonly object _lock = new object();
        private static bool _initialized;

        /// <summary>
        /// 索引条目数量
        /// </summary>
        public static int Count => _index.Count;

        #endregion

        #region 初始化

        /// <summary>
        /// 从 JSON 文件加载索引
        /// </summary>
        public static bool LoadIndex(string jsonPath)
        {
            if (!File.Exists(jsonPath))
                return false;

            try
            {
                var json = File.ReadAllText(jsonPath, Encoding.UTF8);
                var indexFile = JsonSerializer.Deserialize<FdlIndexFile>(json);

                lock (_lock)
                {
                    _index.Clear();
                    foreach (var entry in indexFile.Entries)
                    {
                        _index[entry.Key] = entry;
                    }
                    _initialized = true;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从数据库初始化索引
        /// </summary>
        public static void InitializeFromDatabase()
        {
            lock (_lock)
            {
                if (_initialized)
                    return;

                _index.Clear();

                // 从 SprdFdlDatabase 加载
                var chips = Database.SprdFdlDatabase.Chips;
                var devices = Database.SprdFdlDatabase.DeviceFdls;

                foreach (var device in devices)
                {
                    var chip = chips.FirstOrDefault(c => 
                        c.ChipName.Equals(device.ChipName, StringComparison.OrdinalIgnoreCase));

                    var entry = new FdlIndexEntry
                    {
                        ChipName = device.ChipName,
                        ChipId = chip?.ChipId ?? 0,
                        DeviceModel = device.DeviceName,
                        Brand = device.Brand,
                        Fdl1File = device.Fdl1FileName,
                        Fdl2File = device.Fdl2FileName,
                        Fdl1Address = chip?.Fdl1Address ?? 0x5000,
                        Fdl2Address = chip?.Fdl2Address ?? 0x9EFFFE00
                    };

                    _index[entry.Key] = entry;
                }

                _initialized = true;
            }
        }

        #endregion

        #region 查询

        /// <summary>
        /// 获取设备的 FDL 信息
        /// </summary>
        public static FdlIndexEntry GetEntry(string chipName, string deviceModel)
        {
            EnsureInitialized();
            var key = $"{chipName}/{deviceModel}".ToLower();
            lock (_lock)
            {
                return _index.TryGetValue(key, out var entry) ? entry : null;
            }
        }

        /// <summary>
        /// 获取芯片的所有设备
        /// </summary>
        public static FdlIndexEntry[] GetDevicesForChip(string chipName)
        {
            EnsureInitialized();
            lock (_lock)
            {
                return _index.Values
                    .Where(e => e.ChipName.Equals(chipName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Brand)
                    .ThenBy(e => e.DeviceModel)
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取品牌的所有设备
        /// </summary>
        public static FdlIndexEntry[] GetDevicesForBrand(string brand)
        {
            EnsureInitialized();
            lock (_lock)
            {
                return _index.Values
                    .Where(e => e.Brand.Equals(brand, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.ChipName)
                    .ThenBy(e => e.DeviceModel)
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取所有芯片名称
        /// </summary>
        public static string[] GetAllChipNames()
        {
            EnsureInitialized();
            lock (_lock)
            {
                return _index.Values
                    .Select(e => e.ChipName)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取所有品牌
        /// </summary>
        public static string[] GetAllBrands()
        {
            EnsureInitialized();
            lock (_lock)
            {
                return _index.Values
                    .Select(e => e.Brand)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();
            }
        }

        /// <summary>
        /// 搜索设备
        /// </summary>
        public static FdlIndexEntry[] Search(string keyword)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(keyword))
                return new FdlIndexEntry[0];

            keyword = keyword.ToLower();
            lock (_lock)
            {
                return _index.Values
                    .Where(e =>
                        e.ChipName.ToLower().Contains(keyword) ||
                        e.DeviceModel.ToLower().Contains(keyword) ||
                        e.Brand.ToLower().Contains(keyword) ||
                        (e.MarketName != null && e.MarketName.ToLower().Contains(keyword)))
                    .OrderBy(e => e.Brand)
                    .ThenBy(e => e.DeviceModel)
                    .ToArray();
            }
        }

        /// <summary>
        /// 获取所有条目
        /// </summary>
        public static FdlIndexEntry[] GetAllEntries()
        {
            EnsureInitialized();
            lock (_lock)
            {
                return _index.Values.ToArray();
            }
        }

        private static void EnsureInitialized()
        {
            if (!_initialized)
            {
                // 尝试从默认位置加载
                var indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, 
                    "SprdResources", "fdl_index.json");
                
                if (!LoadIndex(indexPath))
                {
                    InitializeFromDatabase();
                }
            }
        }

        #endregion

        #region 导出

        /// <summary>
        /// 导出索引到 JSON 文件
        /// </summary>
        public static void ExportIndex(string outputPath)
        {
            EnsureInitialized();

            var indexFile = new FdlIndexFile
            {
                Version = 1,
                UpdateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                TotalDevices = _index.Count,
                Entries = _index.Values.OrderBy(e => e.ChipName).ThenBy(e => e.DeviceModel).ToList()
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(indexFile, options);

            File.WriteAllText(outputPath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 导出为 CSV 格式
        /// </summary>
        public static void ExportCsv(string outputPath)
        {
            EnsureInitialized();

            var sb = new StringBuilder();
            sb.AppendLine("芯片,芯片ID,设备型号,品牌,FDL1地址,FDL2地址,FDL1文件,FDL2文件,已验证");

            foreach (var entry in _index.Values.OrderBy(e => e.ChipName).ThenBy(e => e.DeviceModel))
            {
                sb.AppendLine(string.Format("{0},0x{1:X},\"{2}\",{3},0x{4:X8},0x{5:X8},{6},{7},{8}",
                    entry.ChipName,
                    entry.ChipId,
                    entry.DeviceModel,
                    entry.Brand,
                    entry.Fdl1Address,
                    entry.Fdl2Address,
                    entry.Fdl1File,
                    entry.Fdl2File,
                    entry.Verified ? "是" : "否"));
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// 格式化 JSON
        /// </summary>
        private static string FormatJson(string json)
        {
            var indent = 0;
            var quoted = false;
            var sb = new StringBuilder();

            for (var i = 0; i < json.Length; i++)
            {
                var ch = json[i];

                switch (ch)
                {
                    case '{':
                    case '[':
                        sb.Append(ch);
                        if (!quoted)
                        {
                            sb.AppendLine();
                            sb.Append(new string(' ', ++indent * 2));
                        }
                        break;

                    case '}':
                    case ']':
                        if (!quoted)
                        {
                            sb.AppendLine();
                            sb.Append(new string(' ', --indent * 2));
                        }
                        sb.Append(ch);
                        break;

                    case '"':
                        sb.Append(ch);
                        if (i > 0 && json[i - 1] != '\\')
                            quoted = !quoted;
                        break;

                    case ',':
                        sb.Append(ch);
                        if (!quoted)
                        {
                            sb.AppendLine();
                            sb.Append(new string(' ', indent * 2));
                        }
                        break;

                    case ':':
                        sb.Append(ch);
                        if (!quoted)
                            sb.Append(" ");
                        break;

                    default:
                        sb.Append(ch);
                        break;
                }
            }

            return sb.ToString();
        }

        #endregion

        #region 统计

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public static FdlStatistics GetStatistics()
        {
            EnsureInitialized();

            lock (_lock)
            {
                var stats = new FdlStatistics
                {
                    TotalDevices = _index.Count,
                    TotalChips = _index.Values.Select(e => e.ChipName).Distinct().Count(),
                    TotalBrands = _index.Values.Select(e => e.Brand).Distinct().Count(),
                    VerifiedCount = _index.Values.Count(e => e.Verified)
                };

                // 按芯片统计
                stats.DevicesByChip = _index.Values
                    .GroupBy(e => e.ChipName)
                    .ToDictionary(g => g.Key, g => g.Count());

                // 按品牌统计
                stats.DevicesByBrand = _index.Values
                    .GroupBy(e => e.Brand)
                    .ToDictionary(g => g.Key, g => g.Count());

                return stats;
            }
        }

        /// <summary>
        /// FDL 统计信息
        /// </summary>
        public class FdlStatistics
        {
            public int TotalDevices { get; set; }
            public int TotalChips { get; set; }
            public int TotalBrands { get; set; }
            public int VerifiedCount { get; set; }
            public Dictionary<string, int> DevicesByChip { get; set; }
            public Dictionary<string, int> DevicesByBrand { get; set; }

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== FDL 统计 ===");
                sb.AppendLine($"设备总数: {TotalDevices}");
                sb.AppendLine($"芯片类型: {TotalChips}");
                sb.AppendLine($"品牌数量: {TotalBrands}");
                sb.AppendLine($"已验证: {VerifiedCount}");
                sb.AppendLine();
                sb.AppendLine("按芯片:");
                foreach (var kv in DevicesByChip.OrderByDescending(x => x.Value))
                {
                    sb.AppendLine($"  {kv.Key}: {kv.Value}");
                }
                sb.AppendLine();
                sb.AppendLine("按品牌:");
                foreach (var kv in DevicesByBrand.OrderByDescending(x => x.Value))
                {
                    sb.AppendLine($"  {kv.Key}: {kv.Value}");
                }
                return sb.ToString();
            }
        }

        #endregion
    }
}
