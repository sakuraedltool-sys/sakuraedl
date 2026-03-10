// ============================================================================
// SakuraEDL - Spreadtrum Port Detector | 展讯端口检测器
// ============================================================================
// [ZH] 展讯端口检测 - 自动检测 SPD/Unisoc 设备端口
// [EN] Spreadtrum Port Detector - Auto-detect SPD/Unisoc device ports
// [JA] Spreadtrumポート検出 - SPD/Unisocデバイスポートの自動検出
// [KO] Spreadtrum 포트 탐지 - SPD/Unisoc 기기 포트 자동 감지
// [RU] Детектор портов Spreadtrum - Автообнаружение портов устройств
// [ES] Detector de puertos Spreadtrum - Detección automática de puertos
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SakuraEDL.Spreadtrum.Protocol;

namespace SakuraEDL.Spreadtrum.Common
{
    /// <summary>
    /// 展讯设备端口检测器
    /// </summary>
    public class SprdPortDetector : IDisposable
    {
        private ManagementEventWatcher _deviceWatcher;
        private readonly object _lock = new object();
        private bool _isWatching = false;
        
        // 防抖控制
        private DateTime _lastEventTime = DateTime.MinValue;
        private const int DebounceMs = 1000; // 1秒防抖
        private bool _isProcessingEvent = false;

        // 事件
        public event Action<SprdDeviceInfo> OnDeviceConnected;
        public event Action<SprdDeviceInfo> OnDeviceDisconnected;
        public event Action<string> OnLog;

        // 已知设备列表
        private readonly List<SprdDeviceInfo> _connectedDevices = new List<SprdDeviceInfo>();

        /// <summary>
        /// 已连接设备列表
        /// </summary>
        public IReadOnlyList<SprdDeviceInfo> ConnectedDevices
        {
            get
            {
                lock (_lock)
                {
                    return _connectedDevices.ToArray();
                }
            }
        }

        /// <summary>
        /// 开始监听设备插拔
        /// </summary>
        public void StartWatching()
        {
            if (_isWatching)
                return;

            try
            {
                // 初始扫描并更新设备列表
                var devices = ScanDevices(silent: false);
                UpdateDeviceList(devices);

                // 监听设备变化
                var query = new WqlEventQuery(
                    "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3");
                
                _deviceWatcher = new ManagementEventWatcher(query);
                _deviceWatcher.EventArrived += OnDeviceChanged;
                _deviceWatcher.Start();
                
                _isWatching = true;
                Log("[端口检测] 开始监听设备变化");
            }
            catch (Exception ex)
            {
                Log("[端口检测] 启动监听失败: {0}", ex.Message);
            }
        }

        /// <summary>
        /// 停止监听
        /// </summary>
        public void StopWatching()
        {
            if (!_isWatching)
                return;

            try
            {
                _deviceWatcher?.Stop();
                _deviceWatcher?.Dispose();
                _deviceWatcher = null;
                _isWatching = false;
                Log("[端口检测] 停止监听");
            }
            catch { }
        }

        /// <summary>
        /// 扫描当前连接的设备
        /// </summary>
        public List<SprdDeviceInfo> ScanDevices(bool silent = false)
        {
            var devices = new List<SprdDeviceInfo>();

            try
            {
                // 搜索串口设备
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Status='OK'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            string name = obj["Name"]?.ToString() ?? "";
                            string deviceId = obj["DeviceID"]?.ToString() ?? "";
                            var hardwareIds = obj["HardwareID"] as string[];

                            // 检查是否是展讯设备
                            if (IsSprdDevice(name, deviceId, hardwareIds))
                            {
                                string comPort = ExtractComPort(name);
                                if (!string.IsNullOrEmpty(comPort))
                                {
                                    var info = ParseDeviceInfo(name, deviceId, hardwareIds, comPort);
                                    if (info != null)
                                    {
                                        devices.Add(info);
                                        if (!silent)
                                        {
                                            Log("[端口检测] 发现设备: {0} ({1})", info.Name, info.ComPort);
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[端口检测] 扫描设备失败: {0}", ex.Message);
            }

            return devices;
        }
        
        /// <summary>
        /// 扫描所有 COM 端口设备 (用于调试)
        /// </summary>
        public List<ComPortInfo> ScanAllComPorts()
        {
            var ports = new List<ComPortInfo>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Status='OK'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            string name = obj["Name"]?.ToString() ?? "";
                            
                            // 只处理有 COM 端口的设备
                            string comPort = ExtractComPort(name);
                            if (string.IsNullOrEmpty(comPort))
                                continue;
                            
                            string deviceId = obj["DeviceID"]?.ToString() ?? "";
                            var hardwareIds = obj["HardwareID"] as string[];
                            string hwIdStr = hardwareIds != null && hardwareIds.Length > 0 ? hardwareIds[0] : "";
                            
                            // 解析 VID/PID
                            int vid = 0, pid = 0;
                            var vidMatch = Regex.Match(hwIdStr, @"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                            if (vidMatch.Success)
                                vid = Convert.ToInt32(vidMatch.Groups[1].Value, 16);
                            var pidMatch = Regex.Match(hwIdStr, @"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                            if (pidMatch.Success)
                                pid = Convert.ToInt32(pidMatch.Groups[1].Value, 16);
                            
                            // 判断是否被识别为展讯
                            bool isSprd = IsSprdDevice(name, deviceId, hardwareIds);
                            
                            ports.Add(new ComPortInfo
                            {
                                ComPort = comPort,
                                Name = name,
                                DeviceId = deviceId,
                                HardwareId = hwIdStr,
                                Vid = vid,
                                Pid = pid,
                                IsSprdDetected = isSprd
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return ports;
        }
        
        /// <summary>
        /// 打印所有 COM 端口设备信息 (调试用)
        /// </summary>
        public void PrintAllComPorts()
        {
            Log("[设备管理器] 扫描所有 COM 端口...");
            var ports = ScanAllComPorts();
            
            if (ports.Count == 0)
            {
                Log("[设备管理器] 未发现任何 COM 端口");
                return;
            }
            
            Log("[设备管理器] 发现 {0} 个 COM 端口:", ports.Count);
            foreach (var port in ports)
            {
                string sprdFlag = port.IsSprdDetected ? " [展讯]" : "";
                Log("  {0}: VID={1:X4} PID={2:X4}{3}", port.ComPort, port.Vid, port.Pid, sprdFlag);
                Log("    名称: {0}", port.Name);
                Log("    HW ID: {0}", port.HardwareId);
            }
        }
        
        /// <summary>
        /// 更新内部设备列表
        /// </summary>
        private void UpdateDeviceList(List<SprdDeviceInfo> devices)
        {
            lock (_lock)
            {
                _connectedDevices.Clear();
                _connectedDevices.AddRange(devices);
            }
        }

        /// <summary>
        /// 等待设备连接
        /// </summary>
        public async Task<SprdDeviceInfo> WaitForDeviceAsync(int timeoutMs = 30000, CancellationToken cancellationToken = default)
        {
            Log("[端口检测] 等待设备连接...");

            var startTime = DateTime.Now;
            var previousDevices = new HashSet<string>();

            // 记录当前设备（静默模式）
            var initialDevices = ScanDevices(silent: true);
            UpdateDeviceList(initialDevices);
            foreach (var dev in initialDevices)
            {
                previousDevices.Add(dev.ComPort);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                if ((DateTime.Now - startTime).TotalMilliseconds > timeoutMs)
                {
                    Log("[端口检测] 等待超时");
                    return null;
                }

                await Task.Delay(500, cancellationToken);

                var currentDevices = ScanDevices(silent: true);
                UpdateDeviceList(currentDevices);
                
                foreach (var dev in currentDevices)
                {
                    if (!previousDevices.Contains(dev.ComPort))
                    {
                        Log("[端口检测] 新设备连接: {0}", dev.ComPort);
                        return dev;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 检查是否为展讯设备 - 双重验证 (VID + 设备名称)
        /// </summary>
        private bool IsSprdDevice(string name, string deviceId, string[] hardwareIds)
        {
            string nameUpper = name.ToUpper();
            string deviceIdUpper = deviceId.ToUpper();
            string hwIdStr = hardwareIds != null && hardwareIds.Length > 0 ? hardwareIds[0].ToUpper() : "";

            // ========== 第一步: 硬排除其他平台 ==========
            
            // 排除 MTK 设备 (VID 0x0E8D)
            if (deviceIdUpper.Contains("VID_0E8D") || hwIdStr.Contains("VID_0E8D"))
                return false;
            
            // 排除 MTK 设备名称关键字
            string[] mtkKeywords = { "MEDIATEK", "MTK", "PRELOADER", "DA USB" };
            foreach (var kw in mtkKeywords)
            {
                if (nameUpper.Contains(kw))
                    return false;
            }
            
            // 排除高通设备 (VID 0x05C6)
            if (deviceIdUpper.Contains("VID_05C6") || hwIdStr.Contains("VID_05C6"))
                return false;
            
            // 排除高通设备名称关键字 (但保留可能的展讯 DIAG)
            string[] qcKeywords = { "QUALCOMM", "QDL", "QHSUSB", "QDLOADER", "9008", "EDL" };
            foreach (var kw in qcKeywords)
            {
                if (nameUpper.Contains(kw))
                    return false;
            }
            
            // 排除 ADB/Fastboot (但允许展讯的 ADB)
            if ((nameUpper.Contains("ADB") || nameUpper.Contains("ANDROID DEBUG")) && 
                !nameUpper.Contains("SPRD") && !nameUpper.Contains("UNISOC"))
                return false;

            // ========== 第二步: 展讯 VID 检测 ==========
            
            // 展讯专属 VID (0x1782)
            bool hasSprdVid = deviceIdUpper.Contains("VID_1782") || hwIdStr.Contains("VID_1782");
            
            // VID_1782 = 确认是展讯
            if (hasSprdVid)
                return true;
            
            // ========== 第三步: 设备名称关键字检测 ==========
            
            // 展讯专属设备名称关键字 (扩展列表)
            string[] sprdKeywords = {
                "SPRD", "SPREADTRUM", "UNISOC",
                "U2S DIAG", "U2S_DIAG", "SCI USB2SERIAL",
                "SPRD U2S", "UNISOC U2S",
                "USB2SERIAL", "SPRD SERIAL",
                "DOWNLOAD", "BROM",  // 下载模式关键字
                "SC9", "SC8", "SC7",  // 芯片型号前缀
                "UMS", "UDX", "UWS",  // 新平台前缀
                "T606", "T610", "T616", "T618", "T700", "T760", "T770"  // 常见芯片
            };
            
            foreach (var kw in sprdKeywords)
            {
                if (nameUpper.Contains(kw))
                    return true;
            }
            
            // ========== 第四步: 检查特定厂商组合 (VID + PID) ==========
            
            // Samsung SPRD (VID_04E8 + 特定 PID)
            if (deviceIdUpper.Contains("VID_04E8"))
            {
                string[] samsungSprdPids = { "PID_685D", "PID_6860", "PID_6862", "PID_685C" };
                foreach (var pid in samsungSprdPids)
                {
                    if (deviceIdUpper.Contains(pid) || hwIdStr.Contains(pid))
                        return true;
                }
                return false;
            }
            
            // ZTE SPRD (VID_19D2 + 特定 PID)
            if (deviceIdUpper.Contains("VID_19D2"))
            {
                string[] zteSprdPids = { "PID_0016", "PID_0117", "PID_0076", "PID_0034", "PID_1403" };
                foreach (var pid in zteSprdPids)
                {
                    if (deviceIdUpper.Contains(pid) || hwIdStr.Contains(pid))
                        return true;
                }
                return false;
            }
            
            // Alcatel/TCL SPRD (VID_1BBB + 特定 PID)
            if (deviceIdUpper.Contains("VID_1BBB"))
            {
                string[] alcatelSprdPids = { "PID_0536", "PID_0530", "PID_0510" };
                foreach (var pid in alcatelSprdPids)
                {
                    if (deviceIdUpper.Contains(pid) || hwIdStr.Contains(pid))
                        return true;
                }
                return false;
            }
            
            // Huawei SPRD (VID_12D1 + 特定 PID)
            if (deviceIdUpper.Contains("VID_12D1"))
            {
                string[] huaweiSprdPids = { "PID_1001", "PID_1035", "PID_1C05" };
                foreach (var pid in huaweiSprdPids)
                {
                    if (deviceIdUpper.Contains(pid) || hwIdStr.Contains(pid))
                        return true;
                }
                return false;
            }
            
            // Realme/OPPO SPRD (VID_22D9)
            if (deviceIdUpper.Contains("VID_22D9"))
            {
                string[] realmeSprdPids = { "PID_2762", "PID_2763", "PID_2764" };
                foreach (var pid in realmeSprdPids)
                {
                    if (deviceIdUpper.Contains(pid) || hwIdStr.Contains(pid))
                        return true;
                }
                return false;
            }
            
            // Nokia SPRD (VID_0421)
            if (deviceIdUpper.Contains("VID_0421"))
            {
                string[] nokiaSprdPids = { "PID_0600", "PID_0601", "PID_0602" };
                foreach (var pid in nokiaSprdPids)
                {
                    if (deviceIdUpper.Contains(pid) || hwIdStr.Contains(pid))
                        return true;
                }
                return false;
            }
            
            // Infinix/Tecno/Itel (VID_2A47)
            if (deviceIdUpper.Contains("VID_2A47"))
            {
                // Transsion 设备可能使用展讯芯片
                return true;
            }
            
            // ========== 第五步: DIAG 模式宽松检测 ==========
            // 如果设备名包含 DIAG 且不是已知的其他平台
            if (nameUpper.Contains("DIAG") && !nameUpper.Contains("QUALCOMM"))
            {
                // 检查是否有 COM 端口
                if (nameUpper.Contains("(COM"))
                    return true;
            }
            
            // 不符合任何展讯设备特征
            return false;
        }

        /// <summary>
        /// 从设备名称提取 COM 端口号
        /// </summary>
        private string ExtractComPort(string name)
        {
            var match = Regex.Match(name, @"\(COM(\d+)\)");
            if (match.Success)
            {
                return "COM" + match.Groups[1].Value;
            }
            return null;
        }

        /// <summary>
        /// 解析设备信息
        /// </summary>
        private SprdDeviceInfo ParseDeviceInfo(string name, string deviceId, string[] hardwareIds, string comPort)
        {
            var info = new SprdDeviceInfo
            {
                Name = name,
                DeviceId = deviceId,
                ComPort = comPort
            };

            // 解析 VID/PID
            string hwId = hardwareIds != null && hardwareIds.Length > 0 ? hardwareIds[0] : deviceId;
            
            var vidMatch = Regex.Match(hwId, @"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
            if (vidMatch.Success)
            {
                info.Vid = Convert.ToInt32(vidMatch.Groups[1].Value, 16);
            }

            var pidMatch = Regex.Match(hwId, @"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
            if (pidMatch.Success)
            {
                info.Pid = Convert.ToInt32(pidMatch.Groups[1].Value, 16);
            }

            // 判断设备模式
            info.Mode = DetermineDeviceMode(info.Pid, name);

            return info;
        }

        /// <summary>
        /// 判断设备模式
        /// </summary>
        private SprdDeviceMode DetermineDeviceMode(int pid, string name)
        {
            string nameUpper = name.ToUpper();

            // ========== 根据 PID 判断 ==========
            // 下载模式 PID
            if (SprdUsbIds.IsDownloadPid(pid))
                return SprdDeviceMode.Download;
                
            // 诊断模式 PID
            if (SprdUsbIds.IsDiagPid(pid))
                return SprdDeviceMode.Diag;
            
            // 其他已知 PID
            switch (pid)
            {
                case SprdUsbIds.PID_ADB:
                case SprdUsbIds.PID_ADB_2:
                    return SprdDeviceMode.Adb;
                case SprdUsbIds.PID_MTP:
                case SprdUsbIds.PID_MTP_2:
                    return SprdDeviceMode.Mtp;
                case SprdUsbIds.PID_FASTBOOT:
                    return SprdDeviceMode.Fastboot;
            }

            // ========== 根据名称关键字判断 ==========
            // 下载模式关键字
            string[] downloadKeywords = {
                "DOWNLOAD", "BOOT", "BROM", "U2S DIAG", "U2S_DIAG",
                "SPRD U2S", "SCI USB2SERIAL", "UNISOC U2S"
            };
            foreach (var keyword in downloadKeywords)
            {
                if (nameUpper.Contains(keyword))
                    return SprdDeviceMode.Download;
            }
            
            // 诊断模式关键字
            string[] diagKeywords = { "DIAG", "DIAGNOSTIC", "CP" };
            foreach (var keyword in diagKeywords)
            {
                if (nameUpper.Contains(keyword) && !nameUpper.Contains("U2S"))
                    return SprdDeviceMode.Diag;
            }
            
            // ADB 模式关键字
            if (nameUpper.Contains("ADB") || nameUpper.Contains("ANDROID DEBUG"))
                return SprdDeviceMode.Adb;
            
            // MTP 模式关键字
            if (nameUpper.Contains("MTP") || nameUpper.Contains("MEDIA TRANSFER"))
                return SprdDeviceMode.Mtp;
            
            // CDC/ACM 通常也是下载模式
            if (nameUpper.Contains("CDC") || nameUpper.Contains("ACM") || nameUpper.Contains("SERIAL"))
                return SprdDeviceMode.Download;

            return SprdDeviceMode.Unknown;
        }

        /// <summary>
        /// 设备变化事件
        /// </summary>
        private void OnDeviceChanged(object sender, EventArrivedEventArgs e)
        {
            // 防抖：忽略短时间内的重复事件
            lock (_lock)
            {
                var now = DateTime.Now;
                if ((now - _lastEventTime).TotalMilliseconds < DebounceMs)
                {
                    return; // 忽略重复事件
                }
                
                if (_isProcessingEvent)
                {
                    return; // 已有事件在处理中
                }
                
                _lastEventTime = now;
                _isProcessingEvent = true;
            }
            
            // 延迟扫描，等待设备稳定
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(800); // 增加延迟等待设备稳定

                    // 获取之前的设备列表
                    var previousDevices = new HashSet<string>();
                    lock (_lock)
                    {
                        foreach (var dev in _connectedDevices)
                            previousDevices.Add(dev.ComPort);
                    }

                    // 静默扫描，不打印日志
                    var currentDevices = ScanDevices(silent: true);
                    
                    // 更新设备列表
                    UpdateDeviceList(currentDevices);

                    // 检测新连接
                    foreach (var dev in currentDevices)
                    {
                        if (!previousDevices.Contains(dev.ComPort))
                        {
                            Log("[端口检测] 新设备: {0} ({1})", dev.Name, dev.ComPort);
                            OnDeviceConnected?.Invoke(dev);
                        }
                    }

                    // 检测断开
                    var currentPorts = new HashSet<string>();
                    foreach (var dev in currentDevices)
                        currentPorts.Add(dev.ComPort);

                    foreach (var port in previousDevices)
                    {
                        if (!currentPorts.Contains(port))
                        {
                            Log("[端口检测] 设备断开: {0}", port);
                            OnDeviceDisconnected?.Invoke(new SprdDeviceInfo { ComPort = port });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 防止后台任务异常导致程序崩溃
                    System.Diagnostics.Debug.WriteLine($"[端口检测] 设备变化处理异常: {ex.Message}");
                }
                finally
                {
                    lock (_lock)
                    {
                        _isProcessingEvent = false;
                    }
                }
            });
        }

        private void Log(string format, params object[] args)
        {
            OnLog?.Invoke(string.Format(format, args));
        }

        public void Dispose()
        {
            StopWatching();
        }
    }

    /// <summary>
    /// 展讯设备信息
    /// </summary>
    public class SprdDeviceInfo
    {
        public string Name { get; set; }
        public string DeviceId { get; set; }
        public string ComPort { get; set; }
        public int Vid { get; set; }
        public int Pid { get; set; }
        public SprdDeviceMode Mode { get; set; }

        /// <summary>
        /// COM 端口号 (数字)
        /// </summary>
        public int ComPortNumber
        {
            get
            {
                if (ComPort != null && ComPort.StartsWith("COM"))
                {
                    int num;
                    if (int.TryParse(ComPort.Substring(3), out num))
                        return num;
                }
                return 0;
            }
        }

        public override string ToString()
        {
            return string.Format("{0} ({1}) - {2}", Name, ComPort, Mode);
        }
    }

    /// <summary>
    /// 展讯设备模式
    /// </summary>
    public enum SprdDeviceMode
    {
        Unknown,
        Download,   // 下载模式 (可刷机)
        Diag,       // 诊断模式
        Adb,        // ADB 模式
        Mtp,        // MTP 模式
        Fastboot,   // Fastboot 模式
        Normal      // 正常模式
    }
    
    /// <summary>
    /// COM 端口信息 (调试用)
    /// </summary>
    public class ComPortInfo
    {
        public string ComPort { get; set; }
        public string Name { get; set; }
        public string DeviceId { get; set; }
        public string HardwareId { get; set; }
        public int Vid { get; set; }
        public int Pid { get; set; }
        public bool IsSprdDetected { get; set; }
        
        public override string ToString()
        {
            return string.Format("{0}: {1} (VID={2:X4} PID={3:X4}) {4}",
                ComPort, Name, Vid, Pid, IsSprdDetected ? "[展讯]" : "");
        }
    }
}
