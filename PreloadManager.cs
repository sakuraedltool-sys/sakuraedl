using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SakuraEDL.Qualcomm.Database;
using OPFlashTool.Services;

namespace SakuraEDL
{
    /// <summary>
    /// 预加载管理器 - 优化版，支持懒加载模式减少内存占用
    /// EDL Loader 已改为云端自动匹配，不再预加载本地 PAK
    /// </summary>
    public static class PreloadManager
    {
        // 预加载状态
        public static bool IsPreloadComplete { get; private set; } = false;
        public static string CurrentStatus { get; private set; } = "In preparation...";
        public static int Progress { get; private set; } = 0;

        // 懒加载数据 - 按需加载
        #pragma warning disable CS0414 // 未使用的字段 - 保留用于未来兼容
        private static List<string> _edlLoaderItems = null;
        private static List<string> _vipLoaderItems = null;
        private static bool _edlLoaderItemsLoaded = false;
        private static bool _vipLoaderItemsLoaded = false;
        #pragma warning restore CS0414
        private static readonly object _loaderLock = new object();

        /// <summary>
        /// EDL Loader 列表 (已废弃，使用云端匹配)
        /// </summary>
        [Obsolete("Use cloud-based automatic matching")]
        public static List<string> EdlLoaderItems
        {
            get
            {
                // 返回空列表，EDL Loader 现在使用云端匹配
                return new List<string>();
            }
        }
        
        /// <summary>
        /// VIP Loader 列表 (仍从本地 PAK 加载)
        /// </summary>
        public static List<string> VipLoaderItems
        {
            get
            {
                if (!_vipLoaderItemsLoaded)
                {
                    lock (_loaderLock)
                    {
                        if (!_vipLoaderItemsLoaded)
                        {
                            _vipLoaderItems = BuildVipLoaderItems();
                            _vipLoaderItemsLoaded = true;
                        }
                    }
                }
                return _vipLoaderItems;
            }
        }

        private static string _systemInfo = null;
        private static volatile bool _systemInfoLoading = false;
        
        public static string SystemInfo
        {
            get
            {
                if (_systemInfo == null)
                {
                    // 如果正在加载，返回默认值避免阻塞
                    if (_systemInfoLoading)
                        return "loading...";
                    
                    _systemInfoLoading = true;
                    try 
                    { 
                        // 使用带超时的异步操作，避免长时间阻塞
                        var task = Task.Run(async () => 
                            await WindowsInfo.GetSystemInfoAsync().ConfigureAwait(false)
                        );
                        
                        // 最多等待 2 秒，超时则返回默认值
                        if (task.Wait(2000))
                        {
                            _systemInfo = task.Result;
                        }
                        else
                        {
                            _systemInfo = "unknown";
                            System.Diagnostics.Debug.WriteLine("[PreloadManager] System information retrieval timed out");
                        }
                    }
                    catch (Exception ex)
                    { 
                        System.Diagnostics.Debug.WriteLine($"[PreloadManager] Failed to retrieve system information: {ex.Message}");
                        _systemInfo = "unknown"; 
                    }
                    finally
                    {
                        _systemInfoLoading = false;
                    }
                }
                return _systemInfo;
            }
        }

        #pragma warning disable CS0414
        private static bool? _edlPakAvailable = null;
        #pragma warning restore CS0414
        
        /// <summary>
        /// EDL PAK 是否可用 (已废弃，使用云端匹配)
        /// </summary>
        [Obsolete("Use cloud-based automatic matching")]
        public static bool EdlPakAvailable
        {
            get
            {
                // 始终返回 false，强制使用云端匹配
                return false;
            }
        }
        
        private static bool? _vipPakAvailable = null;
        public static bool VipPakAvailable
        {
            get
            {
                if (!_vipPakAvailable.HasValue)
                {
                    _vipPakAvailable = ChimeraSignDatabase.IsLoaderPackAvailable();
                }
                return _vipPakAvailable.Value;
            }
        }

        // 预加载任务
        private static Task _preloadTask = null;

        // 是否启用懒加载模式 (使用 PerformanceConfig)
        private static bool EnableLazyLoading => Common.PerformanceConfig.EnableLazyLoading;

        /// <summary>
        /// 启动预加载（在 SplashForm 中调用）
        /// 优化版：仅加载必要资源，其他按需加载
        /// EDL Loader 改为云端自动匹配，不再预加载
        /// </summary>
        public static void StartPreload()
        {
            if (_preloadTask != null) return;

            _preloadTask = Task.Run(async () =>
            {
                try
                {
                    // 阶段0: 提取嵌入的工具文件（必须）
                    CurrentStatus = "Extract tool files...";
                    Progress = 10;
                    EmbeddedResourceExtractor.ExtractAll();
                    Progress = 30;

                    // 阶段1: 检查 VIP PAK（快速）- EDL 已改为云端匹配
                    CurrentStatus = "Check resource pack...";
                    Progress = 40;
                    _vipPakAvailable = ChimeraSignDatabase.IsLoaderPackAvailable();
                    Progress = 50;

                    // 懒加载模式：跳过预加载系统信息
                    if (!EnableLazyLoading)
                    {
                        // 阶段2: 预加载 VIP Loader 列表 (如果可用)
                        if (_vipPakAvailable.Value)
                        {
                            CurrentStatus = "Load the VIP boot database...";
                            Progress = 60;
                            _vipLoaderItems = BuildVipLoaderItems();
                            _vipLoaderItemsLoaded = true;
                        }
                        Progress = 70;

                        // 阶段3: 预加载系统信息
                        CurrentStatus = "Get system information...";
                        Progress = 80;
                        try { _systemInfo = await WindowsInfo.GetSystemInfoAsync(); }
                        catch { _systemInfo = "unknown"; }
                    }
                    
                    Progress = 90;

                    // 阶段4: 预热常用类型（轻量级）
                    CurrentStatus = "Initialize component...";
                    PrewarmTypesLight();

                    // 完成
                    CurrentStatus = "Loading complete";
                    Progress = 100;
                    IsPreloadComplete = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Preloading failed: {ex.Message}");
                    CurrentStatus = "Loading complete";
                    Progress = 100;
                    IsPreloadComplete = true;
                }
            });
        }

        /// <summary>
        /// 释放缓存以减少内存占用
        /// </summary>
        public static void ClearCache()
        {
            lock (_loaderLock)
            {
                _edlLoaderItems?.Clear();
                _edlLoaderItems = null;
                _edlLoaderItemsLoaded = false;
                
                _vipLoaderItems?.Clear();
                _vipLoaderItems = null;
                _vipLoaderItemsLoaded = false;
            }
            GC.Collect(0, GCCollectionMode.Optimized);
        }

        /// <summary>
        /// 等待预加载完成
        /// </summary>
        public static async Task WaitForPreloadAsync()
        {
            if (_preloadTask != null)
            {
                await _preloadTask;
            }
        }

        /// <summary>
        /// 构建 EDL Loader 列表项 (已废弃，使用云端匹配)
        /// </summary>
        [Obsolete("Use cloud-based automatic matching")]
        private static List<string> BuildEdlLoaderItems()
        {
            // 不再构建本地 PAK 列表，EDL Loader 使用云端匹配
            return new List<string>();
        }
        
        /// <summary>
        /// 构建 VIP Loader 列表项 (OPLUS 签名认证设备)
        /// </summary>
        private static List<string> BuildVipLoaderItems()
        {
            var items = new List<string>();

            try
            {
                if (!ChimeraSignDatabase.IsLoaderPackAvailable())
                    return items;

                // 获取所有 VIP 平台
                var platforms = ChimeraSignDatabase.GetSupportedPlatforms();
                if (platforms == null || platforms.Length == 0)
                    return items;

                items.Add("─── VIP 签名设备 ───");
                
                foreach (var platform in platforms)
                {
                    if (ChimeraSignDatabase.TryGet(platform, out var signData))
                    {
                        items.Add($"[VIP] {signData.Name} ({platform})");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VIP Loader 构建失败: {ex.Message}");
            }

            return items;
        }

        /// <summary>
        /// 获取品牌中文显示名
        /// </summary>
        private static string GetBrandDisplayName(string brand)
        {
            switch (brand.ToLower())
            {
                case "huawei": return "Huawei/Honor";

                case "zte": return "ZTE/Nubia/Red Magic";

                case "xiaomi": return "Xiaomi/Redmi";

                case "blackshark": return "Black Shark";

                case "vivo": return "vivo/iQOO";

                case "meizu": return "Meizu";

                case "lenovo": return "Lenovo/Motorola";

                case "samsung": return "Samsung";

                case "nothing": return "Nothing";

                case "rog": return "ASUS ROG";

                case "lg": return "LG";

                case "smartisan": return "Smartisan";

                case "xtc": return "Xiaotiancai";

                case "360": return "360";

                case "bbk": return "BBK";

                case "royole": return "Royole";

                case "oplus": return "OPPO/OnePlus/Realme";

                default: return brand;
            }
        }

        /// <summary>
        /// 预热常用类型，避免首次使用时 JIT 编译延迟（轻量级版）
        /// </summary>
        private static void PrewarmTypesLight()
        {
            try
            {
                // 仅预热 WPF 和 IO 相关
                var _ = typeof(System.Windows.Controls.Button);
                var __ = typeof(System.IO.FileStream);
                var ___ = typeof(System.IO.MemoryStream);
                var ____ = typeof(System.Net.Http.HttpClient);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PreloadManager] Type preheating failed (non-critical): {ex.Message}");
            }
        }
    }
}
