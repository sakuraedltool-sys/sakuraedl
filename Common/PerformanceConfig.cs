// ============================================================================
// SakuraEDL - 性能配置管理器
// Performance Configuration - 用于优化低配电脑运行体验
// ============================================================================

using System;
using System.Configuration;
using System.Management;

namespace SakuraEDL.Common
{
    /// <summary>
    /// 性能配置管理器 - 统一管理性能相关配置
    /// </summary>
    public static class PerformanceConfig
    {
        private static bool? _lowPerformanceMode;
        private static int? _maxLogEntries;
        private static int? _uiRefreshInterval;
        private static bool? _enableDoubleBuffering;
        private static bool? _enableLazyLoading;
        private static bool _autoDetectDone = false;

        /// <summary>
        /// 低配模式 - 减少动画效果和刷新频率
        /// 自动检测: 内存 < 8GB 或 CPU 核心数 < 4 时自动启用
        /// </summary>
        public static bool LowPerformanceMode
        {
            get
            {
                if (!_lowPerformanceMode.HasValue)
                {
                    // 先读取配置
                    var configValue = GetBoolSettingNullable("LowPerformanceMode");
                    
                    if (configValue.HasValue)
                    {
                        // 用户显式配置
                        _lowPerformanceMode = configValue.Value;
                    }
                    else
                    {
                        // 自动检测硬件
                        _lowPerformanceMode = AutoDetectLowPerformance();
                    }
                }
                return _lowPerformanceMode.Value;
            }
        }
        
        /// <summary>
        /// 自动检测是否为低配机器
        /// </summary>
        private static bool AutoDetectLowPerformance()
        {
            if (_autoDetectDone) return _lowPerformanceMode ?? false;
            _autoDetectDone = true;
            
            try
            {
                // 检测物理内存 (低于 8GB 视为低配)
                ulong totalMemory = 0;
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            totalMemory = (ulong)obj["TotalPhysicalMemory"];
                            break;
                        }
                    }
                }
                catch { }
                
                ulong memoryGB = totalMemory / (1024 * 1024 * 1024);
                if (memoryGB < 8)
                {
                    System.Diagnostics.Debug.WriteLine($"[PerformanceConfig] Automatically enable low-performance mode: Memory {memoryGB}GB < 8GB");
                    return true;
                }
                
                // 检测 CPU 核心数 (低于 4 核视为低配)
                int cpuCores = Environment.ProcessorCount;
                if (cpuCores < 4)
                {
                    System.Diagnostics.Debug.WriteLine($"[PerformanceConfig] Automatically enable low-performance mode: CPU cores {cpuCores} < 4");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PerformanceConfig] Auto-detection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 最大日志条目数量
        /// </summary>
        public static int MaxLogEntries
        {
            get
            {
                if (!_maxLogEntries.HasValue)
                {
                    _maxLogEntries = GetIntSetting("MaxLogEntries", 1000);
                    // 低配模式下限制为更少的条目
                    if (LowPerformanceMode && _maxLogEntries > 500)
                    {
                        _maxLogEntries = 500;
                    }
                }
                return _maxLogEntries.Value;
            }
        }

        /// <summary>
        /// UI 刷新间隔 (毫秒)
        /// </summary>
        public static int UIRefreshInterval
        {
            get
            {
                if (!_uiRefreshInterval.HasValue)
                {
                    _uiRefreshInterval = GetIntSetting("UIRefreshInterval", 50);
                    // 低配模式下使用更长的刷新间隔
                    if (LowPerformanceMode && _uiRefreshInterval < 100)
                    {
                        _uiRefreshInterval = 100;
                    }
                }
                return _uiRefreshInterval.Value;
            }
        }

        /// <summary>
        /// 启用双缓冲减少闪烁
        /// </summary>
        public static bool EnableDoubleBuffering
        {
            get
            {
                if (!_enableDoubleBuffering.HasValue)
                {
                    _enableDoubleBuffering = GetBoolSetting("EnableDoubleBuffering", true);
                }
                return _enableDoubleBuffering.Value;
            }
        }

        /// <summary>
        /// 启用懒加载
        /// </summary>
        public static bool EnableLazyLoading
        {
            get
            {
                if (!_enableLazyLoading.HasValue)
                {
                    _enableLazyLoading = GetBoolSetting("EnableLazyLoading", true);
                }
                return _enableLazyLoading.Value;
            }
        }

        /// <summary>
        /// 动画帧率 (低配模式下降低)
        /// </summary>
        public static int AnimationFPS => LowPerformanceMode ? 15 : 30;

        /// <summary>
        /// 动画定时器间隔
        /// </summary>
        public static int AnimationInterval => 1000 / AnimationFPS;

        /// <summary>
        /// 日志批量刷新阈值
        /// </summary>
        public static int LogBatchSize => LowPerformanceMode ? 20 : 10;
        
        /// <summary>
        /// 端口刷新间隔 (毫秒) - 低配模式下减少刷新频率
        /// </summary>
        public static int PortRefreshInterval => LowPerformanceMode ? 3000 : 1500;
        
        /// <summary>
        /// 进度更新间隔 (毫秒) - 避免过于频繁的 UI 更新
        /// </summary>
        public static int ProgressUpdateInterval => LowPerformanceMode ? 200 : 100;

        /// <summary>
        /// 获取布尔配置值
        /// </summary>
        private static bool GetBoolSetting(string key, bool defaultValue)
        {
            return GetBoolSettingNullable(key) ?? defaultValue;
        }
        
        /// <summary>
        /// 获取可空布尔配置值 (用于区分未配置和显式配置)
        /// </summary>
        private static bool? GetBoolSettingNullable(string key)
        {
            try
            {
                var value = ConfigurationManager.AppSettings[key];
                if (string.IsNullOrEmpty(value))
                    return null;
                return value.ToLower() == "true" || value == "1";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 获取整数配置值
        /// </summary>
        private static int GetIntSetting(string key, int defaultValue)
        {
            try
            {
                var value = ConfigurationManager.AppSettings[key];
                if (string.IsNullOrEmpty(value))
                    return defaultValue;
                if (int.TryParse(value, out int result))
                    return result;
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// 重置缓存 (用于配置更新后刷新)
        /// </summary>
        public static void ResetCache()
        {
            _lowPerformanceMode = null;
            _maxLogEntries = null;
            _uiRefreshInterval = null;
            _enableDoubleBuffering = null;
            _enableLazyLoading = null;
        }
    }
}
