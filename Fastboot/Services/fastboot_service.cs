// ============================================================================
// SakuraEDL - Fastboot Service | Fastboot 服务
// ============================================================================
// [ZH] Fastboot 刷机服务 - 提供 Android Fastboot 模式的完整功能
// [EN] Fastboot Flash Service - Complete functionality for Android Fastboot mode
// [JA] Fastbootフラッシュサービス - Android Fastbootモードの完全な機能
// [KO] Fastboot 플래싱 서비스 - Android Fastboot 모드의 완전한 기능
// [RU] Сервис Fastboot - Полная функциональность для режима Android Fastboot
// [ES] Servicio Fastboot - Funcionalidad completa para modo Android Fastboot
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SakuraEDL.Common;
using SakuraEDL.Fastboot.Common;
using SakuraEDL.Fastboot.Models;
using SakuraEDL.Fastboot.Protocol;
using SakuraEDL.Fastboot.Transport;
using SakuraEDL.Fastboot.Vendor;

namespace SakuraEDL.Fastboot.Services
{
    /// <summary>
    /// Fastboot 服务层
    /// 使用原生 C# 协议实现，不依赖外部 fastboot.exe
    /// </summary>
    public class FastbootService : IDisposable
    {
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;
        private readonly Action<int, int> _progress;

        private FastbootNativeService _nativeService;
        private bool _disposed;
        
        // 看门狗
        private Watchdog _watchdog;

        /// <summary>
        /// 当前连接的设备序列号
        /// </summary>
        public string CurrentSerial => _nativeService?.CurrentSerial;

        /// <summary>
        /// 当前设备信息
        /// </summary>
        public FastbootDeviceInfo DeviceInfo => _nativeService?.DeviceInfo;

        /// <summary>
        /// 是否已连接设备
        /// </summary>
        public bool IsConnected => _nativeService?.IsConnected ?? false;
        
        /// <summary>
        /// 刷写进度事件
        /// </summary>
        public event Action<FlashProgress> FlashProgressChanged;

        public FastbootService(Action<string> log, Action<int, int> progress = null, Action<string> logDetail = null)
        {
            _log = log ?? (msg => { });
            _progress = progress;
            _logDetail = logDetail ?? (msg => { });
            
            // 初始化看门狗
            _watchdog = new Watchdog("Fastboot", WatchdogManager.DefaultTimeouts.Fastboot, _logDetail);
            _watchdog.OnTimeout += OnWatchdogTimeout;
        }
        
        /// <summary>
        /// 看门狗超时处理
        /// </summary>
        private void OnWatchdogTimeout(object sender, WatchdogTimeoutEventArgs e)
        {
            _log($"[Fastboot] 看门狗超时: {e.OperationName}");
            
            if (e.TimeoutCount >= 2)
            {
                _log("[Fastboot] 多次超时，断开连接");
                e.ShouldReset = false;
                Disconnect();
            }
        }
        
        /// <summary>
        /// 喂狗
        /// </summary>
        public void FeedWatchdog() => _watchdog?.Feed();
        
        /// <summary>
        /// 启动看门狗
        /// </summary>
        public void StartWatchdog(string operation) => _watchdog?.Start(operation);
        
        /// <summary>
        /// 停止看门狗
        /// </summary>
        public void StopWatchdog() => _watchdog?.Stop();

        #region 设备检测

        /// <summary>
        /// 获取 Fastboot 设备列表（使用原生协议）
        /// </summary>
        public Task<List<FastbootDeviceListItem>> GetDevicesAsync(CancellationToken ct = default)
        {
            var devices = new List<FastbootDeviceListItem>();

            try
            {
                // 使用原生 USB 枚举
                var nativeDevices = FastbootClient.GetDevices();
                
                foreach (var device in nativeDevices)
                {
                    devices.Add(new FastbootDeviceListItem
                    {
                        Serial = device.Serial ?? $"{device.VendorId:X4}:{device.ProductId:X4}",
                        Status = "fastboot"
                    });
                }
            }
            catch (Exception ex)
            {
                _logDetail($"[Fastboot] 获取设备列表失败: {ex.Message}");
            }

            return Task.FromResult(devices);
        }

        /// <summary>
        /// 选择设备并获取设备信息
        /// </summary>
        public async Task<bool> SelectDeviceAsync(string serial, CancellationToken ct = default)
        {
            _log($"[Fastboot] 选择设备: {serial}");
            
            // 断开旧连接
            Disconnect();
            
            // 创建新的原生服务
            _nativeService = new FastbootNativeService(_log, _logDetail);
            _nativeService.ProgressChanged += OnNativeProgressChanged;
            
            // 连接设备
            bool success = await _nativeService.ConnectAsync(serial, ct);
            
            if (success)
            {
                _log($"[Fastboot] 设备: {DeviceInfo?.Product ?? "未知"}");
                _log($"[Fastboot] 安全启动: {(DeviceInfo?.SecureBoot == true ? "启用" : "禁用")}");
                
                if (DeviceInfo?.HasABPartition == true)
                {
                    _log($"[Fastboot] 当前槽位: {DeviceInfo.CurrentSlot}");
                }
                
                _log($"[Fastboot] Fastbootd 模式: {(DeviceInfo?.IsFastbootd == true ? "是" : "否")}");
                _log($"[Fastboot] 分区数量: {DeviceInfo?.PartitionSizes?.Count ?? 0}");
            }
            
            return success;
        }
        
        /// <summary>
        /// 原生进度回调
        /// </summary>
        private void OnNativeProgressChanged(object sender, FastbootNativeProgressEventArgs e)
        {
            // 转换为 FlashProgress 并触发事件
            var progress = new FlashProgress
            {
                PartitionName = e.Partition,
                Phase = e.Stage,
                CurrentChunk = e.CurrentChunk,
                TotalChunks = e.TotalChunks,
                SizeKB = e.TotalBytes / 1024,
                SpeedKBps = e.SpeedBps / 1024.0,
                Percent = e.Percent  // 传递实际进度值
            };
            
            FlashProgressChanged?.Invoke(progress);
        }

        /// <summary>
        /// 刷新设备信息
        /// </summary>
        public async Task<bool> RefreshDeviceInfoAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在读取设备信息...");
                bool result = await _nativeService.RefreshDeviceInfoAsync(ct);
                
                if (result && DeviceInfo != null)
                {
                    _log($"[Fastboot] 设备: {DeviceInfo.Product ?? "未知"}");
                    _log($"[Fastboot] 解锁状态: {(DeviceInfo.Unlocked == true ? "已解锁" : DeviceInfo.Unlocked == false ? "已锁定" : "未知")}");
                    _log($"[Fastboot] Fastbootd: {(DeviceInfo.IsFastbootd ? "是" : "否")}");
                    if (!string.IsNullOrEmpty(DeviceInfo.CurrentSlot))
                        _log($"[Fastboot] 当前槽位: {DeviceInfo.CurrentSlot}");
                    _log($"[Fastboot] 变量数量: {DeviceInfo.RawVariables?.Count ?? 0}");
                    _log($"[Fastboot] 分区数量: {DeviceInfo.PartitionSizes?.Count ?? 0}");
                    
                    // 提示 bootloader 模式限制
                    if (!DeviceInfo.IsFastbootd && DeviceInfo.PartitionSizes?.Count == 0)
                    {
                        _log("[Fastboot] 提示: Bootloader 模式不支持读取分区列表，如需查看请进入 Fastbootd 模式");
                    }
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 读取设备信息失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 断开设备
        /// </summary>
        public void Disconnect()
        {
            _huaweiSupport = null;
            
            if (_nativeService != null)
            {
                _nativeService.ProgressChanged -= OnNativeProgressChanged;
                _nativeService.Disconnect();
                _nativeService.Dispose();
                _nativeService = null;
            }
        }

        #endregion

        #region 分区操作
        
        /// <summary>
        /// 刷写分区
        /// </summary>
        public async Task<bool> FlashPartitionAsync(string partitionName, string imagePath, 
            bool disableVerity = false, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            if (!File.Exists(imagePath))
            {
                _log($"[Fastboot] 镜像文件不存在: {imagePath}");
                return false;
            }

            try
            {
                var fileInfo = new FileInfo(imagePath);
                _log($"[Fastboot] 正在刷写 {partitionName} ({FormatSize(fileInfo.Length)})...");

                bool result = await _nativeService.FlashPartitionAsync(partitionName, imagePath, disableVerity, ct);
                
                if (result)
                {
                    _log($"[Fastboot] {partitionName} 刷写成功");
                }
                else
                {
                    _log($"[Fastboot] {partitionName} 刷写失败");
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 刷写异常: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 格式化文件大小
        /// </summary>
        private string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:F2} {units[unitIndex]}";
        }

        /// <summary>
        /// 擦除分区
        /// </summary>
        public async Task<bool> ErasePartitionAsync(string partitionName, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log($"[Fastboot] 正在擦除 {partitionName}...");

                bool result = await _nativeService.ErasePartitionAsync(partitionName, ct);

                if (result)
                {
                    _log($"[Fastboot] {partitionName} 擦除成功");
                }
                else
                {
                    _log($"[Fastboot] {partitionName} 擦除失败");
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 擦除异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 批量刷写分区
        /// </summary>
        public async Task<int> FlashPartitionsAsync(List<Tuple<string, string>> partitions, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return 0;
            }

            int success = 0;
            int total = partitions.Count;

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();

                var (partName, imagePath) = partitions[i];
                
                _progress?.Invoke(i, total);
                
                if (await FlashPartitionAsync(partName, imagePath, false, ct))
                {
                    success++;
                }
            }

            _progress?.Invoke(total, total);
            return success;
        }

        #endregion

        #region 重启操作

        /// <summary>
        /// 重启到系统
        /// </summary>
        public async Task<bool> RebootAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在重启...");
                return await _nativeService.RebootAsync(ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 重启失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重启到 Bootloader
        /// </summary>
        public async Task<bool> RebootBootloaderAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在重启到 Bootloader...");
                return await _nativeService.RebootBootloaderAsync(ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 重启失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重启到 Recovery
        /// </summary>
        public async Task<bool> RebootRecoveryAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在重启到 Recovery...");
                return await _nativeService.RebootRecoveryAsync(ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 重启失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重启到 Fastbootd
        /// </summary>
        public async Task<bool> RebootFastbootdAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在重启到 Fastbootd...");
                return await _nativeService.RebootFastbootdAsync(ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 重启失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Bootloader 解锁/锁定

        /// <summary>
        /// 解锁 Bootloader
        /// </summary>
        public async Task<bool> UnlockBootloaderAsync(string method = null, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在解锁 Bootloader...");
                return await _nativeService.UnlockBootloaderAsync(method, ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 解锁失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 锁定 Bootloader
        /// </summary>
        public async Task<bool> LockBootloaderAsync(string method = null, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在锁定 Bootloader...");
                return await _nativeService.LockBootloaderAsync(method, ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 锁定失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region A/B 槽位

        /// <summary>
        /// 设置活动槽位
        /// </summary>
        public async Task<bool> SetActiveSlotAsync(string slot, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log($"[Fastboot] 正在设置活动槽位: {slot}...");
                return await _nativeService.SetActiveSlotAsync(slot, ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 设置槽位失败: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 切换 A/B 槽位
        /// </summary>
        public async Task<bool> SwitchSlotAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                return await _nativeService.SwitchSlotAsync(ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 切换槽位失败: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 获取当前槽位
        /// </summary>
        public async Task<string> GetCurrentSlotAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return null;
            }

            try
            {
                return await _nativeService.GetCurrentSlotAsync(ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 获取槽位失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region OEM 命令

        /// <summary>
        /// 执行 OEM 命令
        /// </summary>
        public async Task<string> ExecuteOemCommandAsync(string command, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return null;
            }

            try
            {
                _log($"[Fastboot] 执行 OEM: {command}");
                return await _nativeService.ExecuteOemCommandAsync(command, ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] OEM 命令失败: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// OEM EDL - 小米踢EDL (fastboot oem edl)
        /// </summary>
        public async Task<bool> OemEdlAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 执行 OEM EDL...");
                string result = await _nativeService.ExecuteOemCommandAsync("edl", ct);
                return result != null;
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] OEM EDL 失败: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 擦除 FRP 分区 (谷歌锁)
        /// </summary>
        public async Task<bool> EraseFrpAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 擦除 FRP 分区...");
                return await _nativeService.ErasePartitionAsync("frp", ct);
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 擦除 FRP 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取变量值
        /// </summary>
        public async Task<string> GetVariableAsync(string name, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                return null;
            }

            try
            {
                return await _nativeService.GetVariableAsync(name, ct);
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// 执行任意命令（用于快捷命令功能）
        /// </summary>
        public async Task<string> ExecuteCommandAsync(string command, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return null;
            }

            try
            {
                _log($"[Fastboot] 执行: {command}");
                string result = null;
                
                // 解析命令
                if (command.StartsWith("getvar ", StringComparison.OrdinalIgnoreCase))
                {
                    string varName = command.Substring(7).Trim();
                    result = await _nativeService.GetVariableAsync(varName, ct);
                    _log($"[Fastboot] {varName}: {result ?? "(空)"}");
                }
                else if (command.StartsWith("oem ", StringComparison.OrdinalIgnoreCase))
                {
                    string oemCmd = command.Substring(4).Trim();
                    result = await _nativeService.ExecuteOemCommandAsync(oemCmd, ct);
                    _log($"[Fastboot] OEM 响应: {result ?? "OKAY"}");
                }
                else if (command == "reboot")
                {
                    await _nativeService.RebootAsync(ct);
                    _log("[Fastboot] 设备正在重启...");
                    return "OKAY";
                }
                else if (command == "reboot-bootloader" || command == "reboot bootloader")
                {
                    await _nativeService.RebootBootloaderAsync(ct);
                    _log("[Fastboot] 设备正在重启到 Bootloader...");
                    return "OKAY";
                }
                else if (command == "reboot-recovery" || command == "reboot recovery")
                {
                    await _nativeService.RebootRecoveryAsync(ct);
                    _log("[Fastboot] 设备正在重启到 Recovery...");
                    return "OKAY";
                }
                else if (command == "reboot-fastboot" || command == "reboot fastboot")
                {
                    await _nativeService.RebootFastbootdAsync(ct);
                    _log("[Fastboot] 设备正在重启到 Fastbootd...");
                    return "OKAY";
                }
                else if (command == "devices" || command == "device")
                {
                    // 显示当前连接的设备信息
                    var info = DeviceInfo;
                    if (info != null)
                    {
                        string deviceInfo = $"{info.Serial ?? "未知"}\tfastboot";
                        _log($"[Fastboot] {deviceInfo}");
                        return deviceInfo;
                    }
                    return "未连接设备";
                }
                else if (command.StartsWith("erase ", StringComparison.OrdinalIgnoreCase))
                {
                    string partition = command.Substring(6).Trim();
                    bool success = await _nativeService.ErasePartitionAsync(partition, ct);
                    result = success ? "OKAY" : "FAILED";
                    _log($"[Fastboot] 擦除 {partition}: {result}");
                }
                else if (command == "flashing unlock")
                {
                    result = await UnlockBootloaderAsync("flashing unlock", ct) ? "OKAY" : "FAILED";
                }
                else if (command == "flashing lock")
                {
                    result = await LockBootloaderAsync("flashing lock", ct) ? "OKAY" : "FAILED";
                }
                else if (command.StartsWith("set_active ", StringComparison.OrdinalIgnoreCase))
                {
                    string slot = command.Substring(11).Trim();
                    bool success = await SetActiveSlotAsync(slot, ct);
                    result = success ? "OKAY" : "FAILED";
                    _log($"[Fastboot] 设置活动槽位 {slot}: {result}");
                }
                else
                {
                    // 其他命令当作 OEM 命令执行
                    result = await _nativeService.ExecuteOemCommandAsync(command, ct);
                    _log($"[Fastboot] 响应: {result ?? "OKAY"}");
                }
                
                return result ?? "OKAY";
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 命令执行失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 华为/荣耀设备支持

        private HuaweiHonorSupport _huaweiSupport;

        /// <summary>
        /// 华为/荣耀设备支持
        /// </summary>
        public HuaweiHonorSupport HuaweiSupport
        {
            get
            {
                if (_huaweiSupport == null && _nativeService != null)
                {
                    _huaweiSupport = new HuaweiHonorSupport(_nativeService, _log);
                }
                return _huaweiSupport;
            }
        }

        /// <summary>
        /// 检测是否为华为/荣耀设备
        /// </summary>
        public async Task<bool> IsHuaweiHonorDeviceAsync(CancellationToken ct = default)
        {
            if (HuaweiSupport == null)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            return await HuaweiSupport.IsHuaweiHonorDeviceAsync(ct);
        }

        /// <summary>
        /// 读取华为/荣耀设备详细信息
        /// </summary>
        public async Task<HuaweiHonorDeviceInfo> ReadHuaweiHonorDeviceInfoAsync(CancellationToken ct = default)
        {
            if (HuaweiSupport == null)
            {
                _log("[Fastboot] 未连接设备");
                return null;
            }

            return await HuaweiSupport.ReadDeviceInfoAsync(ct);
        }

        /// <summary>
        /// 华为/荣耀 FRP 解锁
        /// </summary>
        /// <param name="frpKey">FRP 密钥 (通常为设备序列号)</param>
        public async Task<bool> HuaweiFrpUnlockAsync(string frpKey, CancellationToken ct = default)
        {
            if (HuaweiSupport == null)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            return await HuaweiSupport.UnlockFrpAsync(frpKey, ct);
        }

        /// <summary>
        /// 获取华为/荣耀 Device ID (用于解锁码计算)
        /// </summary>
        public async Task<string> GetHuaweiDeviceIdAsync(CancellationToken ct = default)
        {
            if (HuaweiSupport == null)
            {
                _log("[Fastboot] 未连接设备");
                return null;
            }

            return await HuaweiSupport.GetDeviceIdAsync(ct);
        }

        /// <summary>
        /// 使用解锁码解锁华为/荣耀 Bootloader
        /// </summary>
        public async Task<bool> UnlockHuaweiBootloaderAsync(string unlockCode, CancellationToken ct = default)
        {
            if (HuaweiSupport == null)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            return await HuaweiSupport.UnlockBootloaderWithCodeAsync(unlockCode, ct);
        }

        /// <summary>
        /// 重新锁定华为/荣耀 Bootloader
        /// </summary>
        public async Task<bool> RelockHuaweiBootloaderAsync(CancellationToken ct = default)
        {
            if (HuaweiSupport == null)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            return await HuaweiSupport.RelockBootloaderAsync(ct);
        }

        /// <summary>
        /// 重启华为/荣耀设备到 EDL 模式
        /// </summary>
        public async Task<bool> RebootHuaweiToEdlAsync(CancellationToken ct = default)
        {
            if (HuaweiSupport == null)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            return await HuaweiSupport.RebootToEdlAsync(ct);
        }

        /// <summary>
        /// 读取华为/荣耀 OEM 信息
        /// </summary>
        public async Task<string> ReadHuaweiOemInfoAsync(string infoName, CancellationToken ct = default)
        {
            if (HuaweiSupport == null)
            {
                _log("[Fastboot] 未连接设备");
                return null;
            }

            return await HuaweiSupport.ReadOemInfoAsync(infoName, ct);
        }

        #endregion

        #region OnePlus/OPPO 动态分区操作

        /// <summary>
        /// OnePlus/OPPO 逻辑分区列表
        /// </summary>
        public static readonly HashSet<string> LogicalPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "odm", "vendor", "product", "system_ext", "system_dlkm", "vendor_dlkm", "odm_dlkm",
            "my_bigball", "my_carrier", "my_company", "my_engineering", "my_heytap", "my_manifest",
            "my_preload", "my_product", "my_region", "my_stock"
        };

        /// <summary>
        /// 检测是否处于 FastbootD 模式
        /// </summary>
        public async Task<bool> IsFastbootdModeAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
                return false;

            try
            {
                // 检查 is-userspace 变量或 super-partition-name
                string isUserspace = await _nativeService.GetVariableAsync("is-userspace", ct);
                if (!string.IsNullOrEmpty(isUserspace) && isUserspace.ToLower() == "yes")
                    return true;

                string superPartition = await _nativeService.GetVariableAsync("super-partition-name", ct);
                return !string.IsNullOrEmpty(superPartition);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检测设备平台类型
        /// 高通设备: bootloader 包含 "abl"
        /// 联发科设备: bootloader 包含 "lk"
        /// </summary>
        public enum DevicePlatform
        {
            Unknown,
            Qualcomm,   // 高通 (abl)
            MediaTek    // 联发科 (lk)
        }

        /// <summary>
        /// 获取设备平台类型
        /// </summary>
        public async Task<DevicePlatform> GetDevicePlatformAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
                return DevicePlatform.Unknown;

            try
            {
                // 获取 bootloader 版本信息
                string bootloader = await _nativeService.GetVariableAsync("version-bootloader", ct);
                if (string.IsNullOrEmpty(bootloader))
                {
                    bootloader = await _nativeService.GetVariableAsync("bootloader-version", ct);
                }

                if (!string.IsNullOrEmpty(bootloader))
                {
                    string bl = bootloader.ToLower();
                    // 高通设备使用 ABL (Android Boot Loader)
                    if (bl.Contains("abl"))
                        return DevicePlatform.Qualcomm;
                    // 联发科设备使用 LK (Little Kernel)
                    if (bl.Contains("lk"))
                        return DevicePlatform.MediaTek;
                }

                // 备用检测：通过 product 或 hardware 信息
                string hardware = await _nativeService.GetVariableAsync("hw-revision", ct);
                string product = DeviceInfo?.Product ?? await _nativeService.GetVariableAsync("product", ct);
                
                if (!string.IsNullOrEmpty(product))
                {
                    string p = product.ToLower();
                    // 高通芯片常见前缀
                    if (p.Contains("sdm") || p.Contains("sm") || p.Contains("msm") || p.Contains("qcom") || p.Contains("snapdragon"))
                        return DevicePlatform.Qualcomm;
                    // 联发科芯片常见前缀
                    if (p.Contains("mt") || p.Contains("mtk") || p.Contains("mediatek") || p.Contains("helio") || p.Contains("dimensity"))
                        return DevicePlatform.MediaTek;
                }

                return DevicePlatform.Unknown;
            }
            catch
            {
                return DevicePlatform.Unknown;
            }
        }

        /// <summary>
        /// 检测是否为高通设备
        /// </summary>
        public async Task<bool> IsQualcommDeviceAsync(CancellationToken ct = default)
        {
            var platform = await GetDevicePlatformAsync(ct);
            return platform == DevicePlatform.Qualcomm;
        }

        /// <summary>
        /// 检测是否为联发科设备
        /// </summary>
        public async Task<bool> IsMediaTekDeviceAsync(CancellationToken ct = default)
        {
            var platform = await GetDevicePlatformAsync(ct);
            return platform == DevicePlatform.MediaTek;
        }

        /// <summary>
        /// 删除逻辑分区
        /// </summary>
        public async Task<bool> DeleteLogicalPartitionAsync(string partitionName, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log($"[Fastboot] 删除逻辑分区: {partitionName}");
                return await _nativeService.DeleteLogicalPartitionAsync(partitionName, ct);
            }
            catch (Exception ex)
            {
                _logDetail($"[Fastboot] 删除逻辑分区失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 创建逻辑分区
        /// </summary>
        public async Task<bool> CreateLogicalPartitionAsync(string partitionName, long size = 0, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log($"[Fastboot] 创建逻辑分区: {partitionName} (大小: {size})");
                return await _nativeService.CreateLogicalPartitionAsync(partitionName, size, ct);
            }
            catch (Exception ex)
            {
                _logDetail($"[Fastboot] 创建逻辑分区失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除 COW 快照分区 (用于 OTA 更新恢复)
        /// </summary>
        public async Task<bool> DeleteCowPartitionsAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在删除 COW 快照分区...");
                
                // COW 分区命名规则: 分区名_cow, 分区名_cow-img
                var cowSuffixes = new[] { "_cow", "_cow-img" };
                int deletedCount = 0;

                foreach (var basePart in LogicalPartitions)
                {
                    foreach (var suffix in new[] { "_a", "_b" })
                    {
                        foreach (var cowSuffix in cowSuffixes)
                        {
                            string cowPartName = $"{basePart}{suffix}{cowSuffix}";
                            try
                            {
                                bool deleted = await _nativeService.DeleteLogicalPartitionAsync(cowPartName, ct);
                                if (deleted)
                                {
                                    deletedCount++;
                                    _logDetail($"[Fastboot] 已删除 COW 分区: {cowPartName}");
                                }
                            }
                            catch
                            {
                                // 忽略不存在的分区
                            }
                        }
                    }
                }

                _log($"[Fastboot] COW 快照分区清理完成，删除 {deletedCount} 个");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 删除 COW 分区失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 刷写分区到指定槽位
        /// </summary>
        public async Task<bool> FlashPartitionToSlotAsync(string partitionName, string imagePath, string slot,
            Action<long, long> progressCallback = null, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            if (!File.Exists(imagePath))
            {
                _log($"[Fastboot] 镜像文件不存在: {imagePath}");
                return false;
            }

            try
            {
                // 构建带槽位的分区名
                string targetPartition = $"{partitionName}_{slot}";
                
                var fileInfo = new FileInfo(imagePath);
                _log($"[Fastboot] 刷写 {Path.GetFileName(imagePath)} -> {targetPartition} ({FormatSize(fileInfo.Length)})");

                // 订阅进度事件
                EventHandler<FastbootNativeProgressEventArgs> handler = null;
                if (progressCallback != null)
                {
                    handler = (s, e) => progressCallback(e.BytesSent, e.TotalBytes);
                    _nativeService.ProgressChanged += handler;
                }

                try
                {
                    bool result = await _nativeService.FlashPartitionAsync(targetPartition, imagePath, false, ct);
                    
                    if (result)
                    {
                        _logDetail($"[Fastboot] {targetPartition} 刷写成功");
                    }
                    else
                    {
                        _log($"[Fastboot] {targetPartition} 刷写失败");
                    }
                    
                    return result;
                }
                finally
                {
                    if (handler != null)
                    {
                        _nativeService.ProgressChanged -= handler;
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 刷写异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重建逻辑分区结构 (用于 AB 通刷)
        /// </summary>
        public async Task<bool> RebuildLogicalPartitionsAsync(string targetSlot, CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log($"[Fastboot] 重建逻辑分区结构 (目标槽位: {targetSlot})...");

                // 删除所有 A/B 逻辑分区
                foreach (var name in LogicalPartitions)
                {
                    await DeleteLogicalPartitionAsync($"{name}_a", ct);
                    await DeleteLogicalPartitionAsync($"{name}_b", ct);
                }

                // 只创建目标槽位的逻辑分区 (大小为 0，刷写时会自动调整)
                foreach (var name in LogicalPartitions)
                {
                    string targetName = $"{name}_{targetSlot}";
                    await CreateLogicalPartitionAsync(targetName, 0, ct);
                }

                _log("[Fastboot] 逻辑分区结构重建完成");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 重建逻辑分区失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清除用户数据 (userdata + metadata + -w)
        /// </summary>
        public async Task<bool> WipeDataAsync(CancellationToken ct = default)
        {
            if (_nativeService == null || !_nativeService.IsConnected)
            {
                _log("[Fastboot] 未连接设备");
                return false;
            }

            try
            {
                _log("[Fastboot] 正在清除用户数据...");

                // 1. erase userdata
                bool eraseUserdata = await _nativeService.ErasePartitionAsync("userdata", ct);
                _logDetail($"[Fastboot] 擦除 userdata: {(eraseUserdata ? "成功" : "失败")}");

                // 2. erase metadata
                bool eraseMetadata = await _nativeService.ErasePartitionAsync("metadata", ct);
                _logDetail($"[Fastboot] 擦除 metadata: {(eraseMetadata ? "成功" : "失败")}");

                // 3. 格式化 userdata (fastboot -w 等效)
                // 注意：原生协议可能不支持 -w，需要通过 format 命令实现
                
                bool success = eraseUserdata || eraseMetadata;
                _log(success ? "[Fastboot] 用户数据清除完成" : "[Fastboot] 用户数据清除失败");
                
                return success;
            }
            catch (Exception ex)
            {
                _log($"[Fastboot] 清除数据失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 判断分区是否为逻辑分区
        /// </summary>
        public static bool IsLogicalPartition(string partitionName)
        {
            // 移除槽位后缀
            string baseName = partitionName;
            if (baseName.EndsWith("_a") || baseName.EndsWith("_b"))
            {
                baseName = baseName.Substring(0, baseName.Length - 2);
            }
            return LogicalPartitions.Contains(baseName);
        }

        /// <summary>
        /// 判断是否为 Modem 分区 (高通设备特殊处理)
        /// </summary>
        public static bool IsModemPartition(string partitionName)
        {
            string name = partitionName.ToLower();
            return name.Contains("modem") || name == "radio";
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _disposed = true;
            }
        }

        #endregion
    }
}
