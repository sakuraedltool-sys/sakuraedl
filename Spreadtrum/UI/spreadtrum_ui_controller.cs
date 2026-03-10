// ============================================================================
// SakuraEDL - Spreadtrum UI Controller | 展讯 UI 控制器
// ============================================================================
// [ZH] 展讯 UI 控制器 - 管理展讯刷机界面交互
// [EN] Spreadtrum UI Controller - Manage Spreadtrum flashing interface
// [JA] Spreadtrum UIコントローラー - Spreadtrumフラッシュインターフェース管理
// [KO] Spreadtrum UI 컨트롤러 - Spreadtrum 플래싱 인터페이스 관리
// [RU] Контроллер UI Spreadtrum - Управление интерфейсом прошивки
// [ES] Controlador UI Spreadtrum - Gestión de interfaz de flasheo
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SakuraEDL.Spreadtrum.Common;
using SakuraEDL.Spreadtrum.Database;
using SakuraEDL.Spreadtrum.Exploit;
using SakuraEDL.Spreadtrum.Protocol;
using SakuraEDL.Spreadtrum.Services;

namespace SakuraEDL.Spreadtrum.UI
{
    /// <summary>
    /// 展讯 UI 控制器
    /// </summary>
    public class SpreadtrumUIController : IDisposable
    {
        private readonly SpreadtrumService _service;
        private readonly Action<string, Color> _logCallback;
        private readonly Action<string> _detailLogCallback;
        private CancellationTokenSource _operationCts;

        // 事件
        public event Action<int, int> OnProgress;
        public event Action<SprdDeviceState> OnStateChanged;
        public event Action<SprdDeviceInfo> OnDeviceConnected;
        public event Action<SprdDeviceInfo> OnDeviceDisconnected;
        public event Action<PacInfo> OnPacLoaded;
        public event Action<List<SprdPartitionInfo>> OnPartitionTableLoaded;

        // 属性
        public bool IsConnected => _service.IsConnected;
        public bool IsBromMode => _service.IsBromMode;
        public FdlStage CurrentStage => _service.CurrentStage;
        public SprdDeviceState State => _service.State;
        public PacInfo CurrentPac => _service.CurrentPac;

        public SpreadtrumUIController(Action<string, Color> logCallback, Action<string> detailLogCallback = null)
        {
            _logCallback = logCallback;
            _detailLogCallback = detailLogCallback;

            _service = new SpreadtrumService();
            _service.OnLog += Log;
            _service.OnProgress += (c, t) => OnProgress?.Invoke(c, t);
            _service.OnStateChanged += state => OnStateChanged?.Invoke(state);
            _service.OnDeviceConnected += dev => OnDeviceConnected?.Invoke(dev);
            _service.OnDeviceDisconnected += dev => OnDeviceDisconnected?.Invoke(dev);
        }

        #region 芯片配置

        /// <summary>
        /// 设置芯片 ID (0 表示自动检测)
        /// </summary>
        public void SetChipId(uint chipId)
        {
            _service.SetChipId(chipId);
            if (chipId > 0)
            {
                string platform = SprdPlatform.GetPlatformName(chipId);
                Log(string.Format("[展讯] 芯片配置: {0}", platform), Color.Gray);
            }
        }

        /// <summary>
        /// 获取当前芯片 ID
        /// </summary>
        public uint GetChipId()
        {
            return _service.ChipId;
        }

        #endregion

        #region 自定义 FDL 配置

        /// <summary>
        /// 设置自定义 FDL1 文件和地址
        /// </summary>
        public void SetCustomFdl1(string filePath, uint address)
        {
            _service.SetCustomFdl1(filePath, address);
            if (!string.IsNullOrEmpty(filePath) || address > 0)
            {
                string addrStr = address > 0 ? string.Format("0x{0:X}", address) : "默认";
                string fileStr = !string.IsNullOrEmpty(filePath) ? System.IO.Path.GetFileName(filePath) : "PAC内置";
                Log(string.Format("[展讯] FDL1 配置: {0} @ {1}", fileStr, addrStr), Color.Gray);
            }
        }

        /// <summary>
        /// 设置自定义 FDL2 文件和地址
        /// </summary>
        public void SetCustomFdl2(string filePath, uint address)
        {
            _service.SetCustomFdl2(filePath, address);
            if (!string.IsNullOrEmpty(filePath) || address > 0)
            {
                string addrStr = address > 0 ? string.Format("0x{0:X}", address) : "默认";
                string fileStr = !string.IsNullOrEmpty(filePath) ? System.IO.Path.GetFileName(filePath) : "PAC内置";
                Log(string.Format("[展讯] FDL2 配置: {0} @ {1}", fileStr, addrStr), Color.Gray);
            }
        }

        /// <summary>
        /// 清除自定义 FDL 配置
        /// </summary>
        public void ClearCustomFdl()
        {
            _service.ClearCustomFdl();
            Log("[展讯] 已清除自定义 FDL 配置", Color.Gray);
        }

        #endregion

        #region 设备管理

        /// <summary>
        /// 开始监听设备
        /// </summary>
        public void StartDeviceMonitor()
        {
            Log("[展讯] 开始监听设备...", Color.Gray);
            _service.StartDeviceMonitor();
        }

        /// <summary>
        /// 停止监听设备
        /// </summary>
        public void StopDeviceMonitor()
        {
            _service.StopDeviceMonitor();
        }

        /// <summary>
        /// 获取设备列表
        /// </summary>
        public IReadOnlyList<SprdDeviceInfo> GetDeviceList()
        {
            return _service.GetConnectedDevices();
        }

        /// <summary>
        /// 手动刷新设备列表（扫描端口）
        /// </summary>
        public IReadOnlyList<SprdDeviceInfo> RefreshDevices()
        {
            Log("[展讯] 扫描端口...", Color.Gray);
            _service.StartDeviceMonitor(); // 重新启动监控会触发扫描
            var devices = _service.GetConnectedDevices();
            if (devices.Count > 0)
            {
                foreach (var dev in devices)
                {
                    Log($"[展讯] 发现设备: {dev.ComPort} ({dev.Mode})", Color.Cyan);
                    OnDeviceConnected?.Invoke(dev);
                }
            }
            else
            {
                Log("[展讯] 未检测到展讯设备，请确保设备已进入下载模式", Color.Orange);
            }
            return devices;
        }

        /// <summary>
        /// 连接设备
        /// </summary>
        public async Task<bool> ConnectDeviceAsync(string comPort)
        {
            Log(string.Format("[展讯] 连接设备: {0}", comPort), Color.Cyan);
            return await _service.ConnectAsync(comPort);
        }

        /// <summary>
        /// 初始化设备 (下载 FDL1/FDL2)
        /// </summary>
        public async Task<bool> InitializeDeviceAsync()
        {
            return await _service.InitializeDeviceAsync();
        }

        /// <summary>
        /// 连接并初始化设备 (一键操作)
        /// </summary>
        public async Task<bool> ConnectAndInitializeAsync(string comPort)
        {
            Log(string.Format("[展讯] 连接设备: {0}", comPort), Color.Cyan);
            return await _service.ConnectAndInitializeAsync(comPort);
        }

        /// <summary>
        /// 等待设备并自动连接
        /// </summary>
        public async Task<bool> WaitAndConnectAsync(int timeoutSeconds = 30)
        {
            Log("[展讯] 等待设备连接...", Color.Yellow);
            Log("[展讯] 请将设备连接到电脑 (按住音量下键进入下载模式)", Color.Yellow);
            return await _service.WaitAndConnectAsync(timeoutSeconds * 1000);
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            _operationCts?.Cancel();
            _service.Disconnect();
            Log("[展讯] 已断开连接", Color.Gray);
        }

        #endregion

        #region PAC 操作

        /// <summary>
        /// 加载 PAC 固件
        /// </summary>
        public bool LoadPacFirmware(string pacFilePath)
        {
            var pac = _service.LoadPac(pacFilePath);
            if (pac != null)
            {
                OnPacLoaded?.Invoke(pac);
                Log(string.Format("[展讯] PAC 加载成功: {0} 个文件", pac.Files.Count), Color.Green);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 提取 PAC 固件
        /// </summary>
        public async Task ExtractPacAsync(string outputDir)
        {
            if (CurrentPac == null)
            {
                Log("[展讯] 请先加载 PAC 文件", Color.Orange);
                return;
            }

            ResetOperationCts();
            await _service.ExtractPacAsync(outputDir, _operationCts.Token);
        }

        #endregion

        #region 刷机操作

        /// <summary>
        /// 开始刷机
        /// </summary>
        public async Task<bool> StartFlashAsync(List<string> selectedPartitions = null)
        {
            if (CurrentPac == null)
            {
                Log("[展讯] 请先加载 PAC 文件", Color.Orange);
                return false;
            }

            if (!IsConnected)
            {
                Log("[展讯] 请先连接设备", Color.Orange);
                return false;
            }

            ResetOperationCts();

            Log("========================================", Color.White);
            Log("[展讯] 开始刷机流程", Color.Cyan);
            Log("========================================", Color.White);

            bool result = await _service.FlashPacAsync(selectedPartitions, _operationCts.Token);

            if (result)
            {
                Log("========================================", Color.Green);
                Log("[展讯] 刷机完成！", Color.Green);
                Log("========================================", Color.Green);
            }
            else
            {
                Log("========================================", Color.Red);
                Log("[展讯] 刷机失败", Color.Red);
                Log("========================================", Color.Red);
            }

            return result;
        }

        /// <summary>
        /// 刷写单个分区
        /// </summary>
        public async Task<bool> FlashPartitionAsync(string partitionName, string filePath)
        {
            return await _service.FlashPartitionAsync(partitionName, filePath);
        }

        /// <summary>
        /// 读取分区
        /// </summary>
        public async Task<bool> ReadPartitionAsync(string partitionName, string outputPath, uint size)
        {
            return await _service.ReadPartitionAsync(partitionName, outputPath, size);
        }

        /// <summary>
        /// 擦除分区
        /// </summary>
        public async Task<bool> ErasePartitionAsync(string partitionName)
        {
            return await _service.ErasePartitionAsync(partitionName);
        }

        /// <summary>
        /// 取消当前操作
        /// </summary>
        public void CancelOperation()
        {
            if (_operationCts != null)
            {
                _operationCts.Cancel();
                _operationCts.Dispose();
                _operationCts = null;
            }
            Log("[展讯] 操作已取消", Color.Orange);
        }

        /// <summary>
        /// 安全重置 CancellationTokenSource
        /// </summary>
        private void ResetOperationCts()
        {
            if (_operationCts != null)
            {
                try { _operationCts.Cancel(); } catch { /* 取消可能已完成，忽略 */ }
                try { _operationCts.Dispose(); } catch { /* 释放失败可忽略 */ }
            }
            _operationCts = new CancellationTokenSource();
        }

        #endregion

        #region 设备信息

        /// <summary>
        /// 读取分区表
        /// </summary>
        public async Task ReadPartitionTableAsync()
        {
            var partitions = await _service.ReadPartitionTableAsync();
            if (partitions != null)
            {
                OnPartitionTableLoaded?.Invoke(partitions);
                Log(string.Format("[展讯] 读取到 {0} 个分区", partitions.Count), Color.Green);
            }
        }

        /// <summary>
        /// 获取缓存的分区表
        /// </summary>
        public List<SprdPartitionInfo> CachedPartitions => _service.CachedPartitions;

        /// <summary>
        /// 获取分区大小 (从缓存)
        /// </summary>
        public uint GetPartitionSize(string partitionName)
        {
            return _service.GetPartitionSize(partitionName);
        }

        /// <summary>
        /// 读取芯片信息
        /// </summary>
        public async Task<string> ReadChipInfoAsync()
        {
            uint chipId = await _service.ReadChipTypeAsync();
            if (chipId != 0)
            {
                string platform = SprdPlatform.GetPlatformName(chipId);
                Log(string.Format("[展讯] 芯片: {0} (0x{1:X})", platform, chipId), Color.Cyan);
                return platform;
            }
            return null;
        }

        #endregion

        #region 设备控制

        /// <summary>
        /// 重启设备
        /// </summary>
        public async Task RebootDeviceAsync()
        {
            await _service.RebootAsync();
        }

        /// <summary>
        /// 关闭设备
        /// </summary>
        public async Task PowerOffDeviceAsync()
        {
            await _service.PowerOffAsync();
        }

        #endregion

        #region 安全功能

        /// <summary>
        /// 解锁设备
        /// </summary>
        public async Task<bool> UnlockAsync(byte[] unlockData = null)
        {
            Log("[展讯] 尝试解锁设备...", Color.Yellow);
            bool result = await _service.UnlockAsync(unlockData);
            if (result)
                Log("[展讯] 设备解锁成功", Color.Green);
            else
                Log("[展讯] 设备解锁失败", Color.Red);
            return result;
        }

        /// <summary>
        /// 读取公钥
        /// </summary>
        public async Task<byte[]> ReadPublicKeyAsync()
        {
            return await _service.ReadPublicKeyAsync();
        }

        /// <summary>
        /// 发送签名验证
        /// </summary>
        public async Task<bool> SendSignatureAsync(byte[] signature)
        {
            return await _service.SendSignatureAsync(signature);
        }

        /// <summary>
        /// 读取 eFuse
        /// </summary>
        public async Task<byte[]> ReadEfuseAsync(uint blockId = 0)
        {
            return await _service.ReadEfuseAsync(blockId);
        }

        #endregion

        #region NV 操作

        /// <summary>
        /// 读取 NV 项
        /// </summary>
        public async Task<byte[]> ReadNvItemAsync(ushort itemId)
        {
            return await _service.ReadNvItemAsync(itemId);
        }

        /// <summary>
        /// 写入 NV 项
        /// </summary>
        public async Task<bool> WriteNvItemAsync(ushort itemId, byte[] data)
        {
            return await _service.WriteNvItemAsync(itemId, data);
        }

        /// <summary>
        /// 读取 IMEI
        /// </summary>
        public async Task<string> ReadImeiAsync()
        {
            string imei = await _service.ReadImeiAsync();
            if (!string.IsNullOrEmpty(imei))
                Log(string.Format("[展讯] IMEI: {0}", imei), Color.Cyan);
            return imei;
        }

        /// <summary>
        /// 写入 IMEI
        /// </summary>
        public async Task<bool> WriteImeiAsync(string newImei)
        {
            Log(string.Format("[展讯] 写入 IMEI: {0}...", newImei), Color.Yellow);
            bool result = await _service.WriteImeiAsync(newImei);
            if (result)
                Log("[展讯] IMEI 写入成功", Color.Green);
            else
                Log("[展讯] IMEI 写入失败", Color.Red);
            return result;
        }

        #endregion

        #region Flash 信息

        /// <summary>
        /// 读取 Flash 信息
        /// </summary>
        public async Task<SprdFlashInfo> ReadFlashInfoAsync()
        {
            var info = await _service.ReadFlashInfoAsync();
            if (info != null)
                Log(string.Format("[展讯] Flash: {0}", info), Color.Cyan);
            return info;
        }

        /// <summary>
        /// 重新分区 (危险操作)
        /// </summary>
        public async Task<bool> RepartitionAsync(byte[] partitionTableData)
        {
            Log("[展讯] 警告: 正在执行重新分区...", Color.Red);
            return await _service.RepartitionAsync(partitionTableData);
        }

        #endregion

        #region 波特率

        /// <summary>
        /// 设置波特率
        /// </summary>
        public async Task<bool> SetBaudRateAsync(int baudRate)
        {
            Log(string.Format("[展讯] 切换波特率: {0}", baudRate), Color.Gray);
            return await _service.SetBaudRateAsync(baudRate);
        }

        #endregion

        #region 分区单独刷写

        /// <summary>
        /// 刷写单个镜像文件到分区
        /// </summary>
        public async Task<bool> FlashImageFileAsync(string partitionName, string imageFilePath)
        {
            return await _service.FlashImageFileAsync(partitionName, imageFilePath);
        }

        /// <summary>
        /// 批量刷写多个分区
        /// </summary>
        public async Task<bool> FlashMultipleImagesAsync(Dictionary<string, string> partitionFiles)
        {
            return await _service.FlashMultipleImagesAsync(partitionFiles);
        }

        #endregion

        #region 安全信息

        /// <summary>
        /// 获取安全信息
        /// </summary>
        public async Task<SprdSecurityInfo> GetSecurityInfoAsync()
        {
            return await _service.GetSecurityInfoAsync();
        }

        /// <summary>
        /// 获取 Flash 信息
        /// </summary>
        public async Task<SprdFlashInfo> GetFlashInfoAsync()
        {
            return await _service.GetFlashInfoAsync();
        }

        /// <summary>
        /// 检测漏洞
        /// </summary>
        public SprdVulnerabilityCheckResult CheckVulnerability()
        {
            return _service.CheckVulnerability();
        }

        #endregion

        #region 分区备份

        /// <summary>
        /// 读取分区到文件
        /// </summary>
        public async Task<bool> ReadPartitionToFileAsync(string partitionName, string outputPath, uint size)
        {
            return await _service.ReadPartitionAsync(partitionName, outputPath, size);
        }

        #endregion

        #region 校准数据和工厂重置

        /// <summary>
        /// 备份校准数据
        /// </summary>
        public async Task<bool> BackupCalibrationDataAsync(string outputDir)
        {
            Log("[展讯] 开始备份校准数据...", Color.Cyan);
            return await _service.BackupCalibrationDataAsync(outputDir);
        }

        /// <summary>
        /// 恢复校准数据
        /// </summary>
        public async Task<bool> RestoreCalibrationDataAsync(string inputDir)
        {
            Log("[展讯] 开始恢复校准数据...", Color.Yellow);
            return await _service.RestoreCalibrationDataAsync(inputDir);
        }

        /// <summary>
        /// 恢复出厂设置
        /// </summary>
        public async Task<bool> FactoryResetAsync()
        {
            Log("[展讯] 执行恢复出厂设置...", Color.Yellow);
            return await _service.FactoryResetAsync();
        }

        #endregion

        #region Bootloader 解锁

        /// <summary>
        /// 获取 Bootloader 状态
        /// </summary>
        public async Task<SprdBootloaderStatus> GetBootloaderStatusAsync()
        {
            Log("[展讯] 获取 Bootloader 状态...", Color.Cyan);
            return await _service.GetBootloaderStatusAsync();
        }

        /// <summary>
        /// 解锁 Bootloader (利用漏洞)
        /// </summary>
        public async Task<bool> UnlockBootloaderAsync(bool useExploit = false)
        {
            Log("[展讯] 尝试解锁 Bootloader...", Color.Yellow);
            return await _service.UnlockBootloaderAsync(useExploit);
        }

        /// <summary>
        /// 使用解锁码解锁 Bootloader
        /// </summary>
        public async Task<bool> UnlockBootloaderWithCodeAsync(string unlockCode)
        {
            Log("[展讯] 使用解锁码解锁 Bootloader...", Color.Yellow);
            return await _service.UnlockBootloaderWithCodeAsync(unlockCode);
        }

        /// <summary>
        /// 重新锁定 Bootloader
        /// </summary>
        public async Task<bool> RelockBootloaderAsync()
        {
            Log("[展讯] 重新锁定 Bootloader...", Color.Yellow);
            return await _service.RelockBootloaderAsync();
        }

        #endregion

        #region FDL 数据库

        /// <summary>
        /// 获取所有支持的芯片名称
        /// </summary>
        public string[] GetSupportedChips()
        {
            return Database.SprdFdlDatabase.GetChipNames();
        }

        /// <summary>
        /// 获取芯片的设备列表
        /// </summary>
        public string[] GetDevicesForChip(string chipName)
        {
            return Database.SprdFdlDatabase.GetDeviceNames(chipName);
        }

        /// <summary>
        /// 根据芯片名称获取芯片信息
        /// </summary>
        public Database.SprdChipInfo GetChipInfo(string chipName)
        {
            return Database.SprdFdlDatabase.GetChipByName(chipName);
        }

        /// <summary>
        /// 获取设备 FDL 信息
        /// </summary>
        public Database.SprdDeviceFdl GetDeviceFdl(string chipName, string deviceName)
        {
            var fdls = Database.SprdFdlDatabase.GetDeviceFdlsByChip(chipName);
            return fdls.Find(f => f.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 应用芯片配置
        /// </summary>
        public void ApplyChipConfig(string chipName)
        {
            if (chipName == "自动检测")
            {
                SetChipId(0);
                SetCustomFdl1(null, 0);
                SetCustomFdl2(null, 0);
                Log("[展讯] 使用自动检测模式", Color.Gray);
                return;
            }

            var chip = GetChipInfo(chipName);
            if (chip != null)
            {
                SetChipId(chip.ChipId);
                SetCustomFdl1(null, chip.Fdl1Address);
                SetCustomFdl2(null, chip.Fdl2Address);
                
                string exploitInfo = chip.HasExploit ? $" (有 Exploit: {chip.ExploitId})" : "";
                Log($"[展讯] 选择芯片: {chip.DisplayName}{exploitInfo}", Color.Cyan);
                Log($"[展讯] FDL1 地址: {chip.Fdl1AddressHex}, FDL2 地址: {chip.Fdl2AddressHex}", Color.Gray);
            }
        }

        /// <summary>
        /// 获取芯片数据库统计
        /// </summary>
        public (int chipCount, int deviceCount, int exploitCount) GetDatabaseStats()
        {
            var chips = Database.SprdFdlDatabase.Chips;
            var devices = Database.SprdFdlDatabase.DeviceFdls;
            var exploits = chips.Count(c => c.HasExploit);
            return (chips.Count, devices.Count, exploits);
        }

        #endregion

        #region 辅助方法

        private void Log(string message, Color color)
        {
            _logCallback?.Invoke(message, color);
            _detailLogCallback?.Invoke(message);
        }

        public void Dispose()
        {
            _operationCts?.Cancel();
            _service?.Dispose();
        }

        #endregion
    }
}
