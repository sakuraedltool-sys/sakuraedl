// ============================================================================
// SakuraEDL - Qualcomm UI Controller | 高通 UI 控制器
// ============================================================================
// [ZH] 高通 UI 控制器 - 管理高通刷机界面交互
// [EN] Qualcomm UI Controller - Manage Qualcomm flashing interface interactions
// [JA] Qualcomm UIコントローラー - Qualcommフラッシュインターフェース管理
// [KO] Qualcomm UI 컨트롤러 - Qualcomm 플래싱 인터페이스 관리
// [RU] Контроллер UI Qualcomm - Управление интерфейсом прошивки Qualcomm
// [ES] Controlador UI Qualcomm - Gestión de interfaz de flasheo Qualcomm
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SakuraEDL.Qualcomm.Common;
using SakuraEDL.Qualcomm.Database;
using SakuraEDL.Qualcomm.Models;
using SakuraEDL.Qualcomm.Services;

namespace SakuraEDL.Qualcomm.UI
{
    public class QualcommUIController : IDisposable
    {
        private QualcommService _service;
        private CancellationTokenSource _cts;
        private readonly Action<string, Color?> _log;
        private readonly Action<string> _logDetail;  // 详细调试日志 (只写入文件)
        private bool _disposed;

        // UI 控件引用 - 使用 dynamic 或反射来处理不同类型的控件
        private dynamic _portComboBox;
        private ListView _partitionListView;
        private dynamic _progressBar;        // 总进度条 (长)
        private dynamic _subProgressBar;     // 子进度条 (短)
        private dynamic _statusLabel;
        private dynamic _skipSaharaCheckbox;
        private dynamic _protectPartitionsCheckbox;
        private dynamic _programmerPathTextbox;
        private dynamic _outputPathTextbox;
        
        // 时间/速度/操作状态标签
        private dynamic _timeLabel;
        private dynamic _speedLabel;
        private dynamic _operationLabel;
        
        // 设备信息标签
        private dynamic _brandLabel;         // 品牌
        private dynamic _chipLabel;          // 芯片
        private dynamic _modelLabel;         // 设备型号
        private dynamic _serialLabel;        // 序列号
        private dynamic _storageLabel;       // 存储类型
        private dynamic _unlockLabel;        // 设备型号2 (第二个型号标签)
        private dynamic _otaVersionLabel;    // OTA版本
        
        // 计时器和速度计算
        private Stopwatch _operationStopwatch;
        private long _lastBytes;
        private DateTime _lastSpeedUpdate;
        private double _currentSpeed; // 当前速度 (bytes/s)
        
        // 端口状态监控定时器
        private System.Windows.Forms.Timer _portMonitorTimer;
        private string _connectedPortName; // 当前连接的端口名称
        
        // 总进度追踪
        private int _totalSteps;
        private int _currentStep;
        private long _totalOperationBytes;    // 当前总任务的总字节数
        private long _completedStepBytes;     // 已完成步骤的总字节数
        private long _currentStepBytes;       // 当前步骤的字节数 (用于准确速度计算)
        private string _currentOperationName; // 当前操作名称保存

        /// <summary>
        /// 快速检查连接状态（不触发端口验证，避免意外断开）
        /// </summary>
        public bool IsConnected { get { return _service != null && _service.IsConnectedFast; } }
        
        /// <summary>
        /// 检查是否可以快速重连（端口已释放但 Firehose 仍可用）
        /// </summary>
        public bool CanQuickReconnect { get { return _service != null && _service.IsPortReleased && _service.State == QualcommConnectionState.Ready; } }
        
        /// <summary>
        /// 是否有已读取的分区数据（GPT 已成功解析）
        /// </summary>
        public bool HasPartitions { get { return Partitions != null && Partitions.Count > 0; } }
        
        /// <summary>
        /// 是否可以进行分区操作（已连接或可以快速重连，且有分区数据）
        /// </summary>
        public bool CanOperatePartitions { get { return (IsConnected || CanQuickReconnect) && HasPartitions; } }
        
        public bool IsBusy { get; private set; }
        public List<PartitionInfo> Partitions { get; private set; }

        /// <summary>
        /// 获取当前槽位 ("a", "b", "undefined", "nonexistent")
        /// </summary>
        public string GetCurrentSlot()
        {
            if (_service == null) return "nonexistent";
            return _service.CurrentSlot ?? "nonexistent";
        }

        public event EventHandler<bool> ConnectionStateChanged;
        public event EventHandler<List<PartitionInfo>> PartitionsLoaded;
        
        /// <summary>
        /// 小米授权令牌事件 (内置签名失败时触发，需要弹窗显示令牌)
        /// Token 格式: VQ 开头的 Base64 字符串
        /// </summary>
        public event Action<string> XiaomiAuthTokenRequired;
        
        /// <summary>
        /// 小米授权令牌事件处理 (内置签名失败时触发)
        /// </summary>
        private void OnXiaomiAuthTokenRequired(string token)
        {
            Log("[小米授权] 内置签名无效，需要在线授权", Color.Orange);
            Log(string.Format("[小米授权] 令牌: {0}", token), Color.Cyan);
            
            // 触发公开事件，让 Form1 弹窗显示
            XiaomiAuthTokenRequired?.Invoke(token);
        }
        
        /// <summary>
        /// 端口断开事件处理 (设备自己断开时触发)
        /// </summary>
        private void OnServicePortDisconnected(object sender, EventArgs e)
        {
            // 确保在 UI 线程上执行
            if (_partitionListView != null && _partitionListView.InvokeRequired)
            {
                _partitionListView.BeginInvoke(new Action(() => OnServicePortDisconnected(sender, e)));
                return;
            }
            
            // 停止端口监控
            StopPortMonitor();
            
            Log("设备已断开连接，需要重新完整配置", Color.Red);
            
            // 取消正在进行的操作
            CancelOperation();
            
            // 断开服务连接并释放资源
            if (_service != null)
            {
                try
                {
                    _service.PortDisconnected -= OnServicePortDisconnected;
                    _service.Disconnect();
                    _service.Dispose();
                }
                catch (Exception ex) 
                { 
                    _logDetail?.Invoke($"[UI] 断开服务异常: {ex.Message}"); 
                }
                _service = null;
            }
            
            // 清空分区列表
            Partitions?.Clear();
            if (_partitionListView != null)
            {
                _partitionListView.BeginUpdate();
                _partitionListView.Items.Clear();
                _partitionListView.EndUpdate();
            }
            
            // 重置进度条
            UpdateProgressBarDirect(_progressBar, 0);
            UpdateProgressBarDirect(_subProgressBar, 0);
            
            // 自动取消勾选"跳过引导"，设备断开后需要重新完整配置
            SetSkipSaharaChecked(false);
            
            // 更新 UI 状态
            ConnectionStateChanged?.Invoke(this, false);
            ClearDeviceInfoLabels();
            
            // 刷新端口列表，等待设备重新连接
            RefreshPorts();
            
            Log("请等待设备重新进入 EDL 模式后重新连接", Color.Orange);
        }
        
        /// <summary>
        /// 验证连接状态 (在操作前调用)
        /// </summary>
        public bool ValidateConnection()
        {
            if (_service == null)
            {
                Log("未连接设备", Color.Red);
                return false;
            }
            
            if (!_service.ValidateConnection())
            {
                Log("设备连接已失效，需要重新完整配置", Color.Red);
                // 取消勾选"跳过引导"，需要重新完整配置
                SetSkipSaharaChecked(false);
                ConnectionStateChanged?.Invoke(this, false);
                ClearDeviceInfoLabels();
                RefreshPorts();
                return false;
            }
            
            return true;
        }

        public QualcommUIController(Action<string, Color?> log = null, Action<string> logDetail = null)
        {
            _log = log ?? delegate { };
            _logDetail = logDetail ?? delegate { };
            Partitions = new List<PartitionInfo>();
        }

        public void BindControls(
            object portComboBox = null,
            ListView partitionListView = null,
            object progressBar = null,
            object statusLabel = null,
            object skipSaharaCheckbox = null,
            object protectPartitionsCheckbox = null,
            object programmerPathTextbox = null,
            object outputPathTextbox = null,
            object timeLabel = null,
            object speedLabel = null,
            object operationLabel = null,
            object subProgressBar = null,
            // 设备信息标签
            object brandLabel = null,
            object chipLabel = null,
            object modelLabel = null,
            object serialLabel = null,
            object storageLabel = null,
            object unlockLabel = null,
            object otaVersionLabel = null)
        {
            _portComboBox = portComboBox;
            _partitionListView = partitionListView;
            _progressBar = progressBar;
            _subProgressBar = subProgressBar;
            _statusLabel = statusLabel;
            _skipSaharaCheckbox = skipSaharaCheckbox;
            _protectPartitionsCheckbox = protectPartitionsCheckbox;
            _programmerPathTextbox = programmerPathTextbox;
            _outputPathTextbox = outputPathTextbox;
            _timeLabel = timeLabel;
            _speedLabel = speedLabel;
            _operationLabel = operationLabel;
            
            // 设备信息标签绑定
            _brandLabel = brandLabel;
            _chipLabel = chipLabel;
            _modelLabel = modelLabel;
            _serialLabel = serialLabel;
            _storageLabel = storageLabel;
            _unlockLabel = unlockLabel;
            _otaVersionLabel = otaVersionLabel;
            
            // 初始化端口状态监控定时器 (每 2 秒检查一次)
            _portMonitorTimer = new System.Windows.Forms.Timer();
            _portMonitorTimer.Interval = 2000;
            _portMonitorTimer.Tick += OnPortMonitorTick;
        }
        
        /// <summary>
        /// 端口状态监控定时器回调
        /// </summary>
        private void OnPortMonitorTick(object sender, EventArgs e)
        {
            // 如果没有连接，不需要检查
            if (string.IsNullOrEmpty(_connectedPortName) || _service == null)
                return;
            
            // 检查端口是否还存在于设备管理器中
            var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
            bool portExists = Array.Exists(availablePorts, p => 
                p.Equals(_connectedPortName, StringComparison.OrdinalIgnoreCase));
            
            if (!portExists)
            {
                // 端口已从设备管理器中消失 - 显示在主日志中
                Log(string.Format("检测到端口 {0} 已断开", _connectedPortName), Color.Orange);
                
                // 停止定时器
                _portMonitorTimer.Stop();
                
                // 触发断开处理
                OnServicePortDisconnected(this, EventArgs.Empty);
            }
        }
        
        /// <summary>
        /// 启动端口监控
        /// </summary>
        private void StartPortMonitor(string portName)
        {
            _connectedPortName = portName;
            if (_portMonitorTimer != null && !_portMonitorTimer.Enabled)
            {
                _portMonitorTimer.Start();
                _logDetail(string.Format("[端口监控] 开始监控端口: {0}", portName));
            }
        }
        
        /// <summary>
        /// 停止端口监控
        /// </summary>
        private void StopPortMonitor()
        {
            if (_portMonitorTimer != null && _portMonitorTimer.Enabled)
            {
                _portMonitorTimer.Stop();
                _logDetail("[端口监控] 停止监控");
            }
            _connectedPortName = null;
        }

        /// <summary>
        /// 刷新端口列表
        /// </summary>
        /// <param name="silent">静默模式，不输出日志</param>
        /// <returns>检测到的EDL端口数量</returns>
        public int RefreshPorts(bool silent = false)
        {
            if (_portComboBox == null) return 0;

            try
            {
                var ports = PortDetector.DetectAllPorts();
                var edlPorts = PortDetector.DetectEdlPorts();
                
                // 保存当前选择的端口名称
                string previousSelectedPort = GetSelectedPortName();
                
                _portComboBox.Items.Clear();

                if (ports.Count == 0)
                {
                    // 没有设备时显示默认文本
                    _portComboBox.Text = "设备状态：未连接任何设备";
                }
                else
                {
                    foreach (var port in ports)
                    {
                        string display = port.IsEdl
                            ? string.Format("{0} - {1} [EDL]", port.PortName, port.Description)
                            : string.Format("{0} - {1}", port.PortName, port.Description);
                        _portComboBox.Items.Add(display);
                    }

                    // 简单的选择逻辑：
                    // 1. 优先恢复之前的选择（如果存在）
                    // 2. 否则选择第一个 EDL 端口
                    // 3. 否则选择第一个端口
                    
                    int selectedIndex = -1;
                    
                    // 尝试恢复之前的选择
                    if (!string.IsNullOrEmpty(previousSelectedPort))
                    {
                        for (int i = 0; i < _portComboBox.Items.Count; i++)
                        {
                            if (_portComboBox.Items[i].ToString().Contains(previousSelectedPort))
                            {
                                selectedIndex = i;
                                break;
                            }
                        }
                    }
                    
                    // 如果之前没有选择或选择的端口不存在了，选择 EDL 端口
                    if (selectedIndex < 0 && edlPorts.Count > 0)
                    {
                        for (int i = 0; i < _portComboBox.Items.Count; i++)
                        {
                            if (_portComboBox.Items[i].ToString().Contains(edlPorts[0].PortName))
                            {
                                selectedIndex = i;
                                break;
                            }
                        }
                    }
                    
                    // 默认选择第一个
                    if (selectedIndex < 0 && _portComboBox.Items.Count > 0)
                    {
                        selectedIndex = 0;
                    }
                    
                    // 设置选择
                    if (selectedIndex >= 0)
                    {
                        _portComboBox.SelectedIndex = selectedIndex;
                    }
                }

                return edlPorts.Count;
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    Log(string.Format("刷新端口失败: {0}", ex.Message), Color.Red);
                }
                return 0;
            }
        }

        public async Task<bool> ConnectAsync()
        {
            return await ConnectWithOptionsAsync("", "ufs", IsSkipSaharaEnabled(), "none");
        }
        
        /// <summary>
        /// 仅获取 Sahara 设备信息 (用于云端自动匹配)
        /// </summary>
        /// <returns>设备信息对象，失败返回 null</returns>
        public async Task<SakuraEDL.Qualcomm.Services.SaharaDeviceInfo> GetSaharaDeviceInfoAsync()
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return null; }

            string portName = GetSelectedPortName();
            if (string.IsNullOrEmpty(portName)) { Log("请选择端口", Color.Red); return null; }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                _service = new QualcommService(
                    msg => Log(msg, null),
                    null,
                    _logDetail
                );

                var chipInfo = await _service.GetSaharaDeviceInfoOnlyAsync(portName, _cts.Token);
                
                if (chipInfo == null)
                {
                    Log("无法获取设备信息", Color.Red);
                    return null;
                }

                // 转换为 SaharaDeviceInfo (包含完整字段)
                return new SakuraEDL.Qualcomm.Services.SaharaDeviceInfo
                {
                    SaharaVersion = (int)_service.SaharaProtocolVersion,  // Sahara 协议版本
                    MsmId = chipInfo.MsmId.ToString("X8"),
                    PkHash = chipInfo.PkHash ?? "",
                    OemId = "0x" + chipInfo.OemId.ToString("X4"),
                    ModelId = chipInfo.ModelId.ToString("X4"),
                    HwId = chipInfo.HwIdHex ?? "",
                    Serial = chipInfo.SerialHex ?? "",
                    ChipName = chipInfo.ChipName ?? "",
                    Vendor = chipInfo.Vendor ?? "",
                    IsUfs = true
                };
            }
            catch (Exception ex)
            {
                Log("获取设备信息异常: " + ex.Message, Color.Red);
                return null;
            }
            finally
            {
                IsBusy = false;
            }
        }
        
        /// <summary>
        /// 使用云端匹配的 Loader 继续连接
        /// </summary>
        public async Task<bool> ContinueConnectWithCloudLoaderAsync(byte[] loaderData, string storageType, string authMode)
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (_service == null) { Log("请先获取设备信息", Color.Red); return false; }

            try
            {
                IsBusy = true;

                bool success = await _service.ContinueConnectWithLoaderAsync(loaderData, storageType, authMode, _cts.Token);

                if (success)
                {
                    string portName = GetSelectedPortName();
                    Log("连接成功！", Color.Green);
                    UpdateDeviceInfoLabels();
                    
                    _service.PortDisconnected += OnServicePortDisconnected;
                    _service.XiaomiAuthTokenRequired += OnXiaomiAuthTokenRequired;
                    StartPortMonitor(portName);
                    SetSkipSaharaChecked(true);
                    
                    ConnectionStateChanged?.Invoke(this, true);
                }
                else
                {
                    Log("连接失败", Color.Red);
                }

                return success;
            }
            catch (Exception ex)
            {
                Log("连接异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }
        
        /// <summary>
        /// 使用云端 Loader 数据连接设备
        /// </summary>
        /// <param name="loaderData">Loader 二进制数据</param>
        /// <param name="storageType">存储类型</param>
        /// <param name="authMode">认证模式</param>
        /// <param name="digestData">VIP 认证的 Digest 数据 (可选)</param>
        /// <param name="signatureData">VIP 认证的 Signature 数据 (可选)</param>
        public async Task<bool> ConnectWithCloudLoaderDataAsync(byte[] loaderData, string storageType, string authMode, 
            byte[] digestData = null, byte[] signatureData = null)
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            string portName = GetSelectedPortName();
            if (string.IsNullOrEmpty(portName)) { Log("请选择端口", Color.Red); return false; }

            if (loaderData == null || loaderData.Length == 0)
            {
                Log("Loader 数据为空", Color.Red);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                // 启动进度条
                StartOperationTimer("连接设备", 100, 0);
                UpdateProgressBarDirect(_progressBar, 0);
                UpdateProgressBarDirect(_subProgressBar, 0);

                _service = new QualcommService(
                    msg => Log(msg, null),
                    (current, total) => {
                        if (total > 0)
                        {
                            double percent = 40.0 * current / total;
                            UpdateProgressBarDirect(_progressBar, percent);
                            UpdateProgressBarDirect(_subProgressBar, 100.0 * current / total);
                        }
                    },
                    _logDetail
                );

                Log(string.Format("连接设备 (存储: {0}, 认证: {1})...", storageType, authMode), Color.Blue);
                bool success = await _service.ConnectWithCloudLoaderAsync(portName, loaderData, storageType, authMode, digestData, signatureData, _cts.Token);
                UpdateProgressBarDirect(_progressBar, 80);
                
                if (success)
                    SetSkipSaharaChecked(true);

                if (success)
                {
                    Log("连接成功！", Color.Green);
                    UpdateProgressBarDirect(_progressBar, 100);
                    UpdateProgressBarDirect(_subProgressBar, 100);
                    UpdateDeviceInfoLabels();
                    
                    _service.PortDisconnected += OnServicePortDisconnected;
                    _service.XiaomiAuthTokenRequired += OnXiaomiAuthTokenRequired;
                    StartPortMonitor(portName);
                    
                    ConnectionStateChanged?.Invoke(this, true);
                }
                else
                {
                    Log("连接失败", Color.Red);
                    UpdateProgressBarDirect(_progressBar, 0);
                    UpdateProgressBarDirect(_subProgressBar, 0);
                }

                return success;
            }
            catch (Exception ex)
            {
                Log("连接异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<bool> ConnectWithOptionsAsync(string programmerPath, string storageType, bool skipSahara, string authMode, string digestPath = "", string signaturePath = "")
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            string portName = GetSelectedPortName();
            if (string.IsNullOrEmpty(portName)) { Log("请选择端口", Color.Red); return false; }

            if (!skipSahara && string.IsNullOrEmpty(programmerPath))
            {
                Log("请选择引导文件", Color.Red);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                // 启动进度条 - 连接过程分4个阶段: Sahara(40%) -> Firehose配置(20%) -> 认证(20%) -> 完成(20%)
                StartOperationTimer("连接设备", 100, 0);
                UpdateProgressBarDirect(_progressBar, 0);
                UpdateProgressBarDirect(_subProgressBar, 0);

                _service = new QualcommService(
                    msg => Log(msg, null),
                    (current, total) => {
                        // Sahara 阶段进度映射到 0-40%
                        if (total > 0)
                        {
                            double percent = 40.0 * current / total;
                            UpdateProgressBarDirect(_progressBar, percent);
                            UpdateProgressBarDirect(_subProgressBar, 100.0 * current / total);
                        }
                    },
                    _logDetail  // 传递详细调试日志委托
                );

                bool success;
                if (skipSahara)
                {
                    UpdateProgressBarDirect(_progressBar, 40); // 跳过 Sahara
                    success = await _service.ConnectFirehoseDirectAsync(portName, storageType, _cts.Token);
                    UpdateProgressBarDirect(_progressBar, 60);
                }
                else
                {
                    Log(string.Format("连接设备 (存储: {0}, 认证: {1})...", storageType, authMode), Color.Blue);
                    // 传递认证模式和文件路径给 ConnectAsync，认证在内部按正确顺序执行
                    success = await _service.ConnectAsync(portName, programmerPath, storageType, 
                        authMode, digestPath, signaturePath, _cts.Token);
                    UpdateProgressBarDirect(_progressBar, 80); // Sahara + 认证 + Firehose 配置完成
                    
                    if (success)
                        SetSkipSaharaChecked(true);
                }

                if (success)
                {
                    Log("连接成功！", Color.Green);
                    UpdateProgressBarDirect(_progressBar, 100);
                    UpdateProgressBarDirect(_subProgressBar, 100);
                    UpdateDeviceInfoLabels();
                    
                    // 注册端口断开事件 (设备自己断开时会触发)
                    _service.PortDisconnected += OnServicePortDisconnected;
                    
                    // 注册小米授权令牌事件
                    _service.XiaomiAuthTokenRequired += OnXiaomiAuthTokenRequired;
                    
                    // 启动端口监控 (检测设备管理器中的端口状态)
                    StartPortMonitor(portName);
                    
                    ConnectionStateChanged?.Invoke(this, true);
                }
                else
                {
                    Log("连接失败", Color.Red);
                    UpdateProgressBarDirect(_progressBar, 0);
                    UpdateProgressBarDirect(_subProgressBar, 0);
                }

                return success;
            }
            catch (Exception ex)
            {
                Log("连接异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 使用完整 VIP 数据连接设备 (Loader + Digest + Signature 文件路径)
        /// </summary>
        /// <param name="storageType">存储类型 (ufs/emmc)</param>
        /// <param name="platform">平台名称 (如 SM8550)</param>
        /// <param name="loaderData">Loader (Firehose) 数据</param>
        /// <param name="digestPath">Digest 文件路径</param>
        /// <param name="signaturePath">Signature 文件路径</param>
        public async Task<bool> ConnectWithVipDataAsync(string storageType, string platform, byte[] loaderData, string digestPath, string signaturePath)
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            string portName = GetSelectedPortName();
            if (string.IsNullOrEmpty(portName)) { Log("请选择端口", Color.Red); return false; }

            if (loaderData == null)
            {
                Log("Loader 数据无效", Color.Red);
                return false;
            }

            // 检查认证文件
            bool hasAuth = !string.IsNullOrEmpty(digestPath) && !string.IsNullOrEmpty(signaturePath) &&
                          File.Exists(digestPath) && File.Exists(signaturePath);

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                // 启动进度条
                StartOperationTimer("VIP 连接", 100, 0);
                UpdateProgressBarDirect(_progressBar, 0);
                UpdateProgressBarDirect(_subProgressBar, 0);

                Log(string.Format("[VIP] 平台: {0}", platform), Color.Blue);
                Log(string.Format("[VIP] Loader: {0} KB (资源包)", loaderData.Length / 1024), Color.Gray);
                
                if (hasAuth)
                {
                    var digestInfo = new FileInfo(digestPath);
                    var sigInfo = new FileInfo(signaturePath);
                    Log(string.Format("[VIP] Digest: {0} KB (资源包)", digestInfo.Length / 1024), Color.Gray);
                    Log(string.Format("[VIP] Signature: {0} 字节 (资源包)", sigInfo.Length), Color.Gray);
                }
                else
                {
                    Log("[VIP] 无认证文件，将使用普通模式", Color.Orange);
                }

                _service = new QualcommService(
                    msg => Log(msg, null),
                    (current, total) => {
                        if (total > 0)
                        {
                            // Sahara 阶段进度映射到 0-40%
                            double percent = 40.0 * current / total;
                            UpdateProgressBarDirect(_progressBar, percent);
                            UpdateProgressBarDirect(_subProgressBar, 100.0 * current / total);
                        }
                    },
                    _logDetail
                );

                // 一步完成：上传 Loader + VIP 认证 + Firehose 配置
                // 重要：VIP 认证必须在 Firehose 配置之前执行
                UpdateProgressBarDirect(_progressBar, 5);
                Log("[VIP] 开始连接 (Loader + 认证 + 配置)...", Color.Blue);
                
                // 使用文件路径方式进行 VIP 认证
                bool connectOk = await _service.ConnectWithVipAuthAsync(portName, loaderData, digestPath ?? "", signaturePath ?? "", storageType, _cts.Token);
                UpdateProgressBarDirect(_progressBar, 85);

                if (connectOk)
                {
                    Log("[VIP] 连接成功！高权限模式已激活", Color.Green);
                    UpdateProgressBarDirect(_progressBar, 100);
                    UpdateProgressBarDirect(_subProgressBar, 100);
                    UpdateDeviceInfoLabels();
                    
                    _service.PortDisconnected += OnServicePortDisconnected;
                    _service.XiaomiAuthTokenRequired += OnXiaomiAuthTokenRequired;
                    
                    // 启动端口监控 (检测设备管理器中的端口状态)
                    StartPortMonitor(portName);
                    
                    ConnectionStateChanged?.Invoke(this, true);
                    
                    // 自动勾选跳过 Sahara (下次可以直接连接)
                    SetSkipSaharaChecked(true);
                    
                    return true;
                }
                else
                {
                    Log("[VIP] 连接失败，请检查 Loader/签名是否匹配", Color.Red);
                    UpdateProgressBarDirect(_progressBar, 0);
                    UpdateProgressBarDirect(_subProgressBar, 0);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log("[VIP] 连接异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 使用内嵌 Loader 数据连接
        /// 适用于通用 EDL Loader，支持可选认证
        /// </summary>
        /// <param name="storageType">存储类型 (ufs/emmc)</param>
        /// <param name="loaderData">Loader 二进制数据</param>
        /// <param name="loaderName">Loader 名称 (用于日志)</param>
        /// <param name="authMode">认证模式: none, oneplus</param>
        public async Task<bool> ConnectWithLoaderDataAsync(string storageType, byte[] loaderData, string loaderName, string authMode = "none")
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            string portName = GetSelectedPortName();
            if (string.IsNullOrEmpty(portName)) { Log("请选择端口", Color.Red); return false; }

            if (loaderData == null || loaderData.Length < 100)
            {
                Log("Loader 数据无效", Color.Red);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                // 启动进度条
                StartOperationTimer("EDL 连接", 100, 0);
                UpdateProgressBarDirect(_progressBar, 0);
                UpdateProgressBarDirect(_subProgressBar, 0);

                string authInfo = authMode != "none" ? $", 认证: {authMode}" : "";
                Log(string.Format("[EDL] Loader: {0} ({1} KB{2})", loaderName, loaderData.Length / 1024, authInfo), Color.Cyan);

                _service = new QualcommService(
                    msg => Log(msg, null),
                    (current, total) => {
                        if (total > 0)
                        {
                            double percent = 70.0 * current / total;
                            UpdateProgressBarDirect(_progressBar, percent);
                            UpdateProgressBarDirect(_subProgressBar, 100.0 * current / total);
                        }
                    },
                    _logDetail
                );

                // 通过 Sahara 上传 Loader
                Log("[EDL] 上传 Loader (Sahara)...", Color.Cyan);
                
                bool success = await _service.ConnectWithLoaderDataAsync(portName, loaderData, storageType, _cts.Token);
                
                if (!success)
                {
                    Log("[EDL] Sahara 握手/Loader 上传失败", Color.Red);
                    UpdateProgressBarDirect(_progressBar, 0);
                    UpdateProgressBarDirect(_subProgressBar, 0);
                    return false;
                }
                
                UpdateProgressBarDirect(_progressBar, 75);
                Log("[EDL] Loader 上传成功，已进入 Firehose 模式", Color.Green);
                
                // 执行品牌特定认证
                string authLower = authMode.ToLowerInvariant();
                
                if (authLower == "oneplus")
                {
                    Log("[EDL] 执行 OnePlus 认证...", Color.Cyan);
                    bool authOk = await _service.PerformOnePlusAuthAsync(_cts.Token);
                    UpdateProgressBarDirect(_progressBar, 90);
                    
                    if (authOk)
                    {
                        Log("[EDL] OnePlus 认证成功", Color.Green);
                    }
                    else
                    {
                        Log("[EDL] OnePlus 认证失败，部分功能可能受限", Color.Orange);
                    }
                }
                else if (authLower == "xiaomi")
                {
                    Log("[EDL] 执行小米认证...", Color.Cyan);
                    bool authOk = await _service.PerformXiaomiAuthAsync(_cts.Token);
                    UpdateProgressBarDirect(_progressBar, 90);
                    
                    if (authOk)
                    {
                        Log("[EDL] 小米认证成功", Color.Green);
                    }
                    else
                    {
                        Log("[EDL] 小米认证失败，部分功能可能受限", Color.Orange);
                    }
                }
                else if (authLower == "none" && _service.IsXiaomiDevice())
                {
                    // 小米设备自动认证
                    Log("[EDL] 检测到小米设备，自动执行认证...", Color.Cyan);
                    bool authOk = await _service.PerformXiaomiAuthAsync(_cts.Token);
                    UpdateProgressBarDirect(_progressBar, 90);
                    
                    if (authOk)
                    {
                        Log("[EDL] 小米自动认证成功", Color.Green);
                    }
                    else
                    {
                        Log("[EDL] 小米自动认证失败，部分功能可能受限", Color.Orange);
                    }
                }
                
                Log("[EDL] 连接成功！", Color.Green);
                UpdateProgressBarDirect(_progressBar, 100);
                UpdateProgressBarDirect(_subProgressBar, 100);
                UpdateDeviceInfoLabels();
                
                _service.PortDisconnected += OnServicePortDisconnected;
                _service.XiaomiAuthTokenRequired += OnXiaomiAuthTokenRequired;
                
                // 启动端口监控 (检测设备管理器中的端口状态)
                StartPortMonitor(portName);
                
                ConnectionStateChanged?.Invoke(this, true);
                
                // 自动勾选跳过 Sahara
                SetSkipSaharaChecked(true);

                return true;
            }
            catch (Exception ex)
            {
                Log("[EDL] 连接异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void Disconnect()
        {
            // 停止端口监控
            StopPortMonitor();
            
            if (_service != null)
            {
                try
                {
                    _service.PortDisconnected -= OnServicePortDisconnected;
                }
                catch { }
                _service.Disconnect();
                _service.Dispose();
                _service = null;
            }
            CancelOperation();
            
            // 重置进度条
            UpdateProgressBarDirect(_progressBar, 0);
            UpdateProgressBarDirect(_subProgressBar, 0);
            
            // 清除端口显示
            if (_portComboBox != null)
            {
                try
                {
                    if (_portComboBox.InvokeRequired)
                        _portComboBox.BeginInvoke(new Action(() => { _portComboBox.Items.Clear(); _portComboBox.Text = "设备状态：未连接任何设备"; }));
                    else
                    {
                        _portComboBox.Items.Clear();
                        _portComboBox.Text = "设备状态：未连接任何设备";
                    }
                }
                catch { }
            }
            
            // 清空分区列表
            Partitions?.Clear();
            if (_partitionListView != null)
            {
                try
                {
                    _partitionListView.BeginUpdate();
                    _partitionListView.Items.Clear();
                    _partitionListView.EndUpdate();
                }
                catch { }
            }
            
            ConnectionStateChanged?.Invoke(this, false);
            ClearDeviceInfoLabels();
            Log("已断开连接", Color.Gray);
        }
        
        /// <summary>
        /// 重置卡住的 Sahara 状态
        /// 当设备因为其他软件或引导错误导致卡在 Sahara 模式时使用
        /// </summary>
        public async Task<bool> ResetSaharaAsync()
        {
            string portName = GetSelectedPortName();
            if (string.IsNullOrEmpty(portName))
            {
                Log("请选择端口", Color.Red);
                return false;
            }
            
            if (IsBusy)
            {
                Log("操作进行中", Color.Orange);
                return false;
            }
            
            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                Log("正在重置 Sahara 状态...", Color.Blue);
                
                // 确保 service 存在
                if (_service == null)
                {
                    _service = new QualcommService(
                        msg => Log(msg, null),
                        null,
                        _logDetail
                    );
                }
                
                bool success = await _service.ResetSaharaAsync(portName, _cts.Token);
                
                if (success)
                {
                    Log("------------------------------------------------", Color.Gray);
                    Log("✓ Sahara 状态重置成功！", Color.Green);
                    Log("请点击[连接]按钮重新连接设备", Color.Blue);
                    Log("------------------------------------------------", Color.Gray);
                    
                    // 取消勾选"跳过引导"，因为需要重新完整握手
                    SetSkipSaharaChecked(false);
                    // 刷新端口
                    RefreshPorts();
                    // 清除设备信息显示
                    ClearDeviceInfoLabels();
                    // 通知连接状态变化
                    ConnectionStateChanged?.Invoke(this, false);
                }
                else
                {
                    Log("------------------------------------------------", Color.Gray);
                    Log("❌ 无法重置 Sahara 状态", Color.Red);
                    Log("请尝试以下步骤：", Color.Orange);
                    Log("  1. 断开 USB 连接", Color.Orange);
                    Log("  2. 断电重启设备（拔电池或长按电源键）", Color.Orange);
                    Log("  3. 重新连接 USB", Color.Orange);
                    Log("------------------------------------------------", Color.Gray);
                }
                
                return success;
            }
            catch (OperationCanceledException)
            {
                Log("重置已取消", Color.Orange);
                return false;
            }
            catch (Exception ex)
            {
                Log("重置异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
                _cts = null;
            }
        }
        
        /// <summary>
        /// 硬重置设备 (完全重启)
        /// </summary>
        public async Task<bool> HardResetDeviceAsync()
        {
            string portName = GetSelectedPortName();
            if (string.IsNullOrEmpty(portName))
            {
                Log("请选择端口", Color.Red);
                return false;
            }
            
            if (IsBusy)
            {
                Log("操作进行中", Color.Orange);
                return false;
            }
            
            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                Log("正在发送硬重置命令...", Color.Blue);
                
                if (_service == null)
                {
                    _service = new QualcommService(
                        msg => Log(msg, null),
                        null,
                        _logDetail
                    );
                }
                
                bool success = await _service.HardResetDeviceAsync(portName, _cts.Token);
                
                if (success)
                {
                    Log("设备正在重启，请等待设备重新进入 EDL 模式", Color.Green);
                    ConnectionStateChanged?.Invoke(this, false);
                    ClearDeviceInfoLabels();
                    SetSkipSaharaChecked(false);
                    
                    // 等待一段时间后刷新端口
                    await Task.Delay(2000);
                    RefreshPorts();
                }
                else
                {
                    Log("硬重置失败", Color.Red);
                }
                
                return success;
            }
            catch (OperationCanceledException)
            {
                Log("操作已取消", Color.Orange);
                return false;
            }
            catch (Exception ex)
            {
                Log("硬重置异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
                _cts = null;
            }
        }

        #region 设备信息显示

        private DeviceInfoService _deviceInfoService;
        private DeviceFullInfo _currentDeviceInfo;

        /// <summary>
        /// 获取当前芯片信息
        /// </summary>
        public QualcommChipInfo ChipInfo
        {
            get { return _service != null ? _service.ChipInfo : null; }
        }

        /// <summary>
        /// 获取当前完整设备信息
        /// </summary>
        public DeviceFullInfo CurrentDeviceInfo
        {
            get { return _currentDeviceInfo; }
        }

        /// <summary>
        /// 更新设备信息标签 (Sahara + Firehose 模式获取的信息)
        /// </summary>
        public void UpdateDeviceInfoLabels()
        {
            if (_service == null) return;

            // 初始化设备信息服务
            if (_deviceInfoService == null)
            {
                _deviceInfoService = new DeviceInfoService(
                    msg => Log(msg, null),
                    msg => { } // 详细日志可选
                );
            }

            // 从 Qualcomm 服务获取设备信息
            _currentDeviceInfo = _deviceInfoService.GetInfoFromQualcommService(_service);

            var chipInfo = _service.ChipInfo;
            
            // Sahara 模式获取的信息
            if (chipInfo != null)
            {
                // 品牌 (从 PK Hash 或 OEM ID 识别)
                string brand = _currentDeviceInfo.Vendor;
                if (brand == "Unknown" && !string.IsNullOrEmpty(chipInfo.PkHash))
                {
                    brand = QualcommDatabase.GetVendorByPkHash(chipInfo.PkHash);
                    _currentDeviceInfo.Vendor = brand;
                }
                UpdateLabelSafe(_brandLabel, "品牌：" + (brand != "Unknown" ? brand : "正在识别..."));
                
                // 芯片型号 - 根据 Sahara 读取的 MSM ID 进行数据库映射
                string chipDisplay = "识别中...";
                
                // 优先使用数据库映射的芯片代号
                string chipCodename = QualcommDatabase.GetChipCodename(chipInfo.MsmId);
                if (!string.IsNullOrEmpty(chipCodename))
                {
                    chipDisplay = chipCodename;
                }
                else if (!string.IsNullOrEmpty(chipInfo.ChipName) && chipInfo.ChipName != "Unknown")
                {
                    // 使用已解析的芯片名称
                    int parenIndex = chipInfo.ChipName.IndexOf('(');
                    chipDisplay = parenIndex > 0 ? chipInfo.ChipName.Substring(0, parenIndex).Trim() : chipInfo.ChipName;
                }
                else if (chipInfo.MsmId != 0)
                {
                    // 显示 MSM ID (方便添加到数据库)
                    chipDisplay = string.Format("0x{0:X8}", chipInfo.MsmId);
                }
                else if (!string.IsNullOrEmpty(chipInfo.HwIdHex) && chipInfo.HwIdHex.Length >= 4)
                {
                    // 显示 HWID
                    chipDisplay = chipInfo.HwIdHex.StartsWith("0x") ? chipInfo.HwIdHex : "0x" + chipInfo.HwIdHex;
                }
                
                UpdateLabelSafe(_chipLabel, "芯片：" + chipDisplay);
                
                // 序列号 - 强制锁定为 Sahara 读取的芯片序列号
                UpdateLabelSafe(_serialLabel, "芯片序列号：" + (!string.IsNullOrEmpty(chipInfo.SerialHex) ? chipInfo.SerialHex : "未获取"));
                
                // 设备型号 - 需要从 Firehose 读取分区信息后才能获取
                UpdateLabelSafe(_modelLabel, "型号：待深度扫描");
            }
            else
            {
                // Sahara 未获取到芯片信息，显示默认值
                UpdateLabelSafe(_brandLabel, "品牌：未识别");
                UpdateLabelSafe(_chipLabel, "芯片：未识别");
                UpdateLabelSafe(_serialLabel, "芯片序列号：未获取");
                UpdateLabelSafe(_modelLabel, "型号：待深度扫描");
            }
            
            // Firehose 模式获取的信息
            string storageType = _service.StorageType ?? "UFS";
            int sectorSize = _service.SectorSize;
            UpdateLabelSafe(_storageLabel, string.Format("存储：{0} ({1}B)", storageType.ToUpper(), sectorSize));
            
            // 设备型号 (待深度扫描)
            UpdateLabelSafe(_unlockLabel, "型号：待深度扫描");
            
            // OTA版本
            UpdateLabelSafe(_otaVersionLabel, "版本：待深度扫描");
        }

        /// <summary>
        /// 读取分区表后更新更多设备信息
        /// </summary>
        public void UpdateDeviceInfoFromPartitions()
        {
            if (_service == null || Partitions == null || Partitions.Count == 0) return;

            if (_currentDeviceInfo == null)
            {
                _currentDeviceInfo = new DeviceFullInfo();
            }

            // 1. 尝试读取硬件分区 (devinfo, proinfo)
            Task.Run(async () => {
                // devinfo (通用/小米/OPPO)
                var devinfoPart = Partitions.FirstOrDefault(p => p.Name == "devinfo");
                if (devinfoPart != null)
                {
                    byte[] data = await _service.ReadPartitionDataAsync("devinfo", 0, 4096, _cts.Token);
                    if (data != null)
                    {
                        _deviceInfoService.ParseDevInfo(data, _currentDeviceInfo);
                    }
                }

                // proinfo (联想)
                var proinfoPart = Partitions.FirstOrDefault(p => p.Name == "proinfo");
                if (proinfoPart != null)
                {
                    byte[] data = await _service.ReadPartitionDataAsync("proinfo", 0, 4096, _cts.Token);
                    if (data != null)
                    {
                        _deviceInfoService.ParseProInfo(data, _currentDeviceInfo);
                    }
                }
            });

            // 2. 检查 A/B 分区结构
            bool hasAbSlot = Partitions.Exists(p => p.Name.EndsWith("_a") || p.Name.EndsWith("_b"));
            _currentDeviceInfo.IsAbDevice = hasAbSlot;
            
            // 更新基础描述
            string storageDesc = string.Format("存储：{0} ({1})", 
                _service.StorageType.ToUpper(), 
                hasAbSlot ? "A/B 分区" : "常规分区");
            UpdateLabelSafe(_storageLabel, storageDesc);

            // 如果已经有 brand 信息，不在这里覆盖
            if (string.IsNullOrEmpty(_currentDeviceInfo.Brand) || _currentDeviceInfo.Brand == "Unknown")
            {
                bool isOplus = Partitions.Exists(p => p.Name.StartsWith("my_") || p.Name.Contains("oplus") || p.Name.Contains("oppo"));
                bool isXiaomi = Partitions.Exists(p => p.Name == "cust" || p.Name == "persist");
                bool isLenovo = Partitions.Exists(p => p.Name.Contains("lenovo") || p.Name == "proinfo" || p.Name == "lenovocust");
                
                if (isOplus) _currentDeviceInfo.Brand = "OPPO/Realme";
                else if (isXiaomi) _currentDeviceInfo.Brand = "Xiaomi/Redmi";
                else if (isLenovo)
                {
                    // 检查是否为拯救者系列
                    bool isLegion = Partitions.Exists(p => p.Name.Contains("legion"));
                    _currentDeviceInfo.Brand = isLegion ? "Lenovo (Legion)" : "Lenovo";
                }
                
                if (!string.IsNullOrEmpty(_currentDeviceInfo.Brand))
                {
                    UpdateLabelSafe(_brandLabel, "品牌：" + _currentDeviceInfo.Brand);
                }
            }
            
            UpdateLabelSafe(_unlockLabel, "型号：分区表已读取");
        }

        /// <summary>
        /// 打印符合专业刷机工具格式的全量设备信息日志
        /// </summary>
        public void PrintFullDeviceLog()
        {
            if (_service == null || _currentDeviceInfo == null) return;

            var chip = _service.ChipInfo;
            var info = _currentDeviceInfo;

            Log("------------------------------------------------", Color.Gray);
            Log("读取设备信息 : 成功", Color.Green);

            // 1. 核心身份信息
            string marketName = !string.IsNullOrEmpty(info.MarketName) ? info.MarketName : 
                               (!string.IsNullOrEmpty(info.Brand) && !string.IsNullOrEmpty(info.Model) ? info.Brand + " " + info.Model : "未知");
            Log(string.Format("- 市场名称 : {0}", marketName), Color.Blue);
            
            // 产品名称 (Name)
            if (!string.IsNullOrEmpty(info.MarketNameEn) && info.MarketNameEn != marketName)
                Log(string.Format("- 产品名称 : {0}", info.MarketNameEn), Color.Blue);
            else if (!string.IsNullOrEmpty(info.DeviceCodename))
                Log(string.Format("- 产品名称 : {0}", info.DeviceCodename), Color.Blue);
            
            // 型号
            if (!string.IsNullOrEmpty(info.Model))
                Log(string.Format("- 设备型号 : {0}", info.Model), Color.Blue);
            
            // 生产厂商
            if (!string.IsNullOrEmpty(info.Brand))
                Log(string.Format("- 生产厂家 : {0}", info.Brand), Color.Blue);
            
            // 2. 系统版本信息
            if (!string.IsNullOrEmpty(info.AndroidVersion))
                Log(string.Format("- 安卓版本 : {0}{1}", info.AndroidVersion, 
                    !string.IsNullOrEmpty(info.SdkVersion) ? " [SDK:" + info.SdkVersion + "]" : ""), Color.Blue);
            
            if (!string.IsNullOrEmpty(info.SecurityPatch))
                Log(string.Format("- 安全补丁 : {0}", info.SecurityPatch), Color.Blue);
            
            // 3. 设备/产品信息
            if (!string.IsNullOrEmpty(info.DevProduct))
                Log(string.Format("- 芯片平台 : {0}", info.DevProduct), Color.Blue);
            
            if (!string.IsNullOrEmpty(info.Product))
                Log(string.Format("- 产品代号 : {0}", info.Product), Color.Blue);
            
            // 市场区域
            if (!string.IsNullOrEmpty(info.MarketRegion))
                Log(string.Format("- 市场区域 : {0}", info.MarketRegion), Color.Blue);
            
            // 区域代码
            if (!string.IsNullOrEmpty(info.Region))
                Log(string.Format("- 区域代码 : {0}", info.Region), Color.Blue);
            
            // 4. 构建信息
            if (!string.IsNullOrEmpty(info.BuildId))
                Log(string.Format("- 构建 ID : {0}", info.BuildId), Color.Blue);
            
            if (!string.IsNullOrEmpty(info.DisplayId))
                Log(string.Format("- 展示 ID : {0}", info.DisplayId), Color.Blue);
            
            if (!string.IsNullOrEmpty(info.BuiltDate))
                Log(string.Format("- 编译日期 : {0}", info.BuiltDate), Color.Blue);
            
            if (!string.IsNullOrEmpty(info.BuildTimestamp))
                Log(string.Format("- 时间戳 : {0}", info.BuildTimestamp), Color.Blue);
            
            // 5. OTA 版本信息 (高亮显示)
            if (!string.IsNullOrEmpty(info.OtaVersion))
                Log(string.Format("- OTA 版本 : {0}", info.OtaVersion), Color.Green);
            
            if (!string.IsNullOrEmpty(info.OtaVersionFull) && info.OtaVersionFull != info.OtaVersion)
                Log(string.Format("- 完整 OTA : {0}", info.OtaVersionFull), Color.Green);
            
            // 6. 完整构建指纹
            if (!string.IsNullOrEmpty(info.Fingerprint))
                Log(string.Format("- 构建指纹 : {0}", info.Fingerprint), Color.Blue);
            
            // 7. 厂商特有信息 (OPLUS)
            if (!string.IsNullOrEmpty(info.OplusProject))
                Log(string.Format("- OPLUS 项目 : {0}", info.OplusProject), Color.Blue);
            if (!string.IsNullOrEmpty(info.OplusNvId))
                Log(string.Format("- OPLUS NV ID : {0}", info.OplusNvId), Color.Blue);
            
            Log("------------------------------------------------", Color.Gray);
        }

        /// <summary>
        /// 从分区列表推断设备型号
        /// </summary>
        private string GetDeviceModelFromPartitions()
        {
            if (Partitions == null || Partitions.Count == 0) return null;

            // 基于芯片信息
            var chipInfo = ChipInfo;
            if (chipInfo != null)
            {
                string vendor = chipInfo.Vendor;
                if (vendor == "Unknown")
                    vendor = QualcommDatabase.GetVendorByPkHash(chipInfo.PkHash);
                
                if (vendor != "Unknown" && chipInfo.ChipName != "Unknown")
                {
                    return string.Format("{0} ({1})", vendor, chipInfo.ChipName);
                }
            }

            // 基于特征分区名推断设备类型
            bool isOnePlus = Partitions.Exists(p => p.Name.Contains("oem") && p.Name.Contains("op"));
            bool isXiaomi = Partitions.Exists(p => p.Name.Contains("cust") || p.Name == "persist");
            bool isOppo = Partitions.Exists(p => p.Name.Contains("oplus") || p.Name.Contains("my_"));

            if (isOnePlus) return "OnePlus";
            if (isXiaomi) return "Xiaomi";
            if (isOppo) return "OPPO/Realme";
            
            return null;
        }

        /// <summary>
        /// 内部方法：尝试读取 build.prop（不检查 IsBusy）
        /// 根据厂商自动选择对应的解析策略
        /// </summary>
        private async Task TryReadBuildPropInternalAsync()
        {
            // 创建总超时保护 (60 秒)
            using (var totalTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, totalTimeoutCts.Token))
            {
                try
                {
                    await TryReadBuildPropCoreAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (totalTimeoutCts.IsCancellationRequested)
                    {
                        Log("设备信息解析超时 (60秒)，已跳过", Color.Orange);
                    }
                    else
                    {
                        Log("设备信息解析已取消", Color.Orange);
                    }
                }
                catch (Exception ex)
                {
                    Log(string.Format("设备信息解析失败: {0}", ex.Message), Color.Orange);
                }
            }
        }

        /// <summary>
        /// 设备信息解析核心逻辑 (带取消令牌支持)
        /// </summary>
        private async Task TryReadBuildPropCoreAsync(CancellationToken ct)
        {
            try
            {
                // 检查是否有可用于读取设备信息的分区
                bool hasSuper = Partitions != null && Partitions.Exists(p => p.Name == "super");
                bool hasVendor = Partitions != null && Partitions.Exists(p => p.Name == "vendor" || p.Name.StartsWith("vendor_"));
                bool hasSystem = Partitions != null && Partitions.Exists(p => p.Name == "system" || p.Name.StartsWith("system_"));
                bool hasMyManifest = Partitions != null && Partitions.Exists(p => p.Name.StartsWith("my_manifest"));
                
                // 如果没有任何可用分区，直接返回
                if (!hasSuper && !hasVendor && !hasSystem && !hasMyManifest)
                {
                    Log("设备无 super/vendor/system 分区，跳过设备信息读取", Color.Orange);
                    return;
                }

                if (_deviceInfoService == null)
                {
                    _deviceInfoService = new DeviceInfoService(
                        msg => Log(msg, null),
                        msg => { }
                    );
                }

                // 创建带超时的分区读取委托 (使用传入的取消令牌)
                // 增加超时时间到 30 秒，因为 VIP 模式下读取较慢
                Func<string, long, int, Task<byte[]>> readPartition = async (partName, offset, size) =>
                {
                    // 检查取消
                    if (ct.IsCancellationRequested) return null;
                    
                    // 检查分区是否存在
                    if (Partitions == null || !Partitions.Exists(p => p.Name == partName || p.Name.StartsWith(partName + "_")))
                    {
                        return null;
                    }
                    
                    try
                    {
                        // 增加到 30 秒超时保护 (VIP 模式下需要更长时间)
                        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                        {
                            return await _service.ReadPartitionDataAsync(partName, offset, size, linkedCts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 如果是外部取消，返回 null 而不是抛出异常
                        if (ct.IsCancellationRequested) return null;
                        // 否则是超时，静默返回 null
                        _logDetail(string.Format("读取 {0} 超时", partName));
                        return null;
                    }
                    catch (Exception ex)
                    {
                        _logDetail(string.Format("读取 {0} 异常: {1}", partName, ex.Message));
                        return null;
                    }
                };

                // 获取当前状态
                string activeSlot = _service.CurrentSlot;
                long superStart = 0;
                if (hasSuper)
                {
                    var superPart = Partitions.Find(p => p.Name == "super");
                    if (superPart != null) superStart = (long)superPart.StartSector;
                }
                int sectorSize = _service.SectorSize > 0 ? _service.SectorSize : 512;

                // 自动识别厂商并选择对应的解析策略
                string detectedVendor = DetectDeviceVendor();
                Log(string.Format("检测到设备厂商: {0}", detectedVendor), Color.Blue);
                
                // 更新进度: 厂商识别完成 (85%)
                UpdateProgressBarDirect(_progressBar, 85);
                UpdateProgressBarDirect(_subProgressBar, 25);

                BuildPropInfo buildProp = null;

                // 根据厂商使用对应的读取策略
                switch (detectedVendor.ToLower())
                {
                    case "oppo":
                    case "realme":
                    case "oneplus":
                    case "oplus":
                        UpdateProgressBarDirect(_subProgressBar, 40);
                        buildProp = await ReadOplusBuildPropAsync(readPartition, activeSlot, hasSuper, superStart, sectorSize);
                        break;

                    case "xiaomi":
                    case "redmi":
                    case "poco":
                        UpdateProgressBarDirect(_subProgressBar, 40);
                        buildProp = await ReadXiaomiBuildPropAsync(readPartition, activeSlot, hasSuper, superStart, sectorSize);
                        break;

                    case "lenovo":
                    case "motorola":
                        UpdateProgressBarDirect(_subProgressBar, 40);
                        buildProp = await ReadLenovoBuildPropAsync(readPartition, activeSlot, hasSuper, superStart, sectorSize);
                        break;

                    case "zte":
                    case "nubia":
                        UpdateProgressBarDirect(_subProgressBar, 40);
                        buildProp = await ReadZteBuildPropAsync(readPartition, activeSlot, hasSuper, superStart, sectorSize);
                        break;

                    default:
                        // 通用策略 - 只在有 super 分区时尝试
                        if (hasSuper)
                        {
                            UpdateProgressBarDirect(_subProgressBar, 40);
                            buildProp = await _deviceInfoService.ReadBuildPropFromDevice(
                                readPartition, activeSlot, hasSuper, superStart, sectorSize);
                        }
                        break;
                }

                // 更新进度: 解析完成 (95%)
                UpdateProgressBarDirect(_progressBar, 95);
                UpdateProgressBarDirect(_subProgressBar, 80);

                if (buildProp != null)
                {
                    Log("成功读取设备 build.prop", Color.Green);
                    ApplyBuildPropInfo(buildProp);
                    
                    // 打印全量设备信息日志
                    PrintFullDeviceLog();
                }
                else
                {
                    Log("未能读取到设备信息（设备可能不支持或分区格式不兼容）", Color.Orange);
                }
            }
            catch (OperationCanceledException)
            {
                // 重新抛出，由外层处理
                throw;
            }
            catch (Exception ex)
            {
                Log(string.Format("读取设备信息失败: {0}", ex.Message), Color.Orange);
            }
        }

        /// <summary>
        /// 自动检测设备厂商 (综合 Sahara 芯片信息 + 分区特征)
        /// 注意：OEM ID 和 PK Hash 可能不准确，优先使用分区特征
        /// </summary>
        private string DetectDeviceVendor()
        {
            var chipInfo = _service?.ChipInfo;
            string detectedSource = "";
            string detectedVendor = "Unknown";

            // 1. 首先从设备信息获取 (如果已经有的话)
            if (_currentDeviceInfo != null && !string.IsNullOrEmpty(_currentDeviceInfo.Vendor) && 
                _currentDeviceInfo.Vendor != "Unknown" && !_currentDeviceInfo.Vendor.Contains("Unknown"))
            {
                detectedVendor = NormalizeVendorName(_currentDeviceInfo.Vendor);
                detectedSource = "设备信息";
                _logDetail(string.Format("厂商检测 [{0}]: {1}", detectedSource, detectedVendor));
                return detectedVendor;
            }

            // 2. 优先从分区特征识别 (最可靠的来源，因为 OEM ID 和 PK Hash 可能不准确)
            if (Partitions != null && Partitions.Count > 0)
            {
                // 收集所有分区名称用于调试
                var partNames = new List<string>();
                foreach (var p in Partitions) partNames.Add(p.Name);
                _logDetail(string.Format("检测到 {0} 个分区，开始特征分析...", Partitions.Count));

                // 联想系特有分区 (优先检测)
                bool hasLenovoMarker = Partitions.Exists(p => 
                    p.Name == "proinfo" || 
                    p.Name == "lenovocust" || 
                    p.Name.Contains("lenovo"));
                if (hasLenovoMarker)
                {
                    _logDetail("检测到联想特征分区 (proinfo/lenovocust)");
                    return "Lenovo";
                }

                // OPLUS 系 - 严格检测：必须有明确的 oplus/oppo 标记，或至少 2 个 OPLUS 特有分区
                bool hasOplusExplicit = Partitions.Exists(p => 
                    p.Name.Contains("oplus") || p.Name.Contains("oppo") || p.Name.Contains("realme"));
                int oplusSpecificCount = 0;
                foreach (var p in Partitions)
                {
                    // OPLUS 特有分区（小米设备不会有这些）
                    if (p.Name == "my_engineering" || p.Name == "my_carrier" || 
                        p.Name == "my_stock" || p.Name == "my_region" || 
                        p.Name == "my_custom" || p.Name == "my_bigball" ||
                        p.Name == "my_preload" || p.Name == "my_company" ||
                        p.Name == "reserve1" || p.Name == "reserve2" ||
                        p.Name.StartsWith("my_engineering") ||
                        p.Name.StartsWith("my_carrier") ||
                        p.Name.StartsWith("my_stock"))
                    {
                        oplusSpecificCount++;
                    }
                }
                // 需要明确标记或至少 2 个特有分区才能确定是 OPLUS
                if (hasOplusExplicit || oplusSpecificCount >= 2)
                {
                    _logDetail(string.Format("检测到 OPLUS 特征: 明确标记={0}, 特有分区数={1}", hasOplusExplicit, oplusSpecificCount));
                    return "OPLUS";
                }

                // 小米系检测（在 OPLUS 严格检测后）
                bool hasXiaomiMarker = Partitions.Exists(p => 
                    p.Name.Contains("xiaomi") || p.Name.Contains("miui") || p.Name.Contains("redmi"));
                bool hasCust = Partitions.Exists(p => p.Name == "cust");
                bool hasPersist = Partitions.Exists(p => p.Name == "persist");
                bool hasSpunvm = Partitions.Exists(p => p.Name == "spunvm"); // 小米常用基带分区
                
                // 小米特征: 有明确标记，或有 cust+persist 组合（且不是联想/OPLUS）
                if (hasXiaomiMarker || (hasCust && hasPersist))
                {
                    _logDetail(string.Format("检测到小米特征: 明确标记={0}, cust={1}, persist={2}", 
                        hasXiaomiMarker, hasCust, hasPersist));
                    return "Xiaomi";
                }

                // 中兴系 (ZTE/nubia/红魔)
                if (Partitions.Exists(p => p.Name.Contains("zte") || p.Name.Contains("nubia")))
                {
                    _logDetail("检测到中兴特征分区");
                    return "ZTE";
                }
            }

            // 3. 从芯片 OEM ID 识别 (Sahara 阶段获取) - 作为备用
            if (chipInfo != null && chipInfo.OemId > 0)
            {
                string vendorFromOem = QualcommDatabase.GetVendorName(chipInfo.OemId);
                if (!string.IsNullOrEmpty(vendorFromOem) && !vendorFromOem.Contains("Unknown"))
                {
                    detectedVendor = NormalizeVendorName(vendorFromOem);
                    _logDetail(string.Format("厂商检测 [OEM ID 0x{0:X4}]: {1}", chipInfo.OemId, detectedVendor));
                    return detectedVendor;
                }
            }

            // 4. 从芯片 Vendor 字段
            if (chipInfo != null && !string.IsNullOrEmpty(chipInfo.Vendor) && 
                chipInfo.Vendor != "Unknown" && !chipInfo.Vendor.Contains("Unknown"))
            {
                detectedVendor = NormalizeVendorName(chipInfo.Vendor);
                _logDetail(string.Format("厂商检测 [芯片Vendor]: {0}", detectedVendor));
                return detectedVendor;
            }

            // 5. 从 PK Hash 识别 (最后备用)
            if (chipInfo != null && !string.IsNullOrEmpty(chipInfo.PkHash))
            {
                string vendor = QualcommDatabase.GetVendorByPkHash(chipInfo.PkHash);
                if (!string.IsNullOrEmpty(vendor) && vendor != "Unknown")
                {
                    detectedVendor = NormalizeVendorName(vendor);
                    _logDetail(string.Format("厂商检测 [PK Hash]: {0}", detectedVendor));
                    return detectedVendor;
                }
            }

            _logDetail("无法确定设备厂商，将使用通用策略");
            return "Unknown";
        }

        /// <summary>
        /// 标准化厂商名称
        /// </summary>
        private string NormalizeVendorName(string vendor)
        {
            if (string.IsNullOrEmpty(vendor)) return "Unknown";
            
            string v = vendor.ToLower();
            if (v.Contains("oppo") || v.Contains("realme") || v.Contains("oneplus") || v.Contains("oplus"))
                return "OPLUS";
            if (v.Contains("xiaomi") || v.Contains("redmi") || v.Contains("poco"))
                return "Xiaomi";
            if (v.Contains("lenovo") || v.Contains("motorola") || v.Contains("moto"))
                return "Lenovo";
            if (v.Contains("zte") || v.Contains("nubia") || v.Contains("redmagic"))
                return "ZTE";
            if (v.Contains("vivo"))
                return "vivo";
            if (v.Contains("samsung"))
                return "Samsung";

            return vendor;
        }

        /// <summary>
        /// OPLUS (OPPO/Realme/OnePlus) 专用读取策略
        /// 直接使用 DeviceInfoService 的通用策略，它会按正确顺序读取 my_manifest
        /// 顺序: system -> system_ext -> product -> vendor -> odm -> my_manifest (高优先级覆盖低优先级)
        /// </summary>
        private async Task<BuildPropInfo> ReadOplusBuildPropAsync(Func<string, long, int, Task<byte[]>> readPartition, 
            string activeSlot, bool hasSuper, long superStart, int sectorSize)
        {
            Log("使用 OPLUS 专用解析策略...", Color.Blue);
            
            // OPLUS 设备的 my_manifest 是 EROFS 文件系统，不是纯文本
            // 使用 DeviceInfoService 的通用策略（会正确解析 EROFS 并按优先级合并属性）
            var result = await _deviceInfoService.ReadBuildPropFromDevice(readPartition, activeSlot, hasSuper, superStart, sectorSize, "OnePlus");
            
            if (result != null && !string.IsNullOrEmpty(result.MarketName))
            {
                Log("从 OPLUS 分区成功读取设备信息", Color.Green);
            }
            else
            {
                Log("OPLUS 设备信息解析不完整，部分字段可能缺失", Color.Orange);
            }

            return result;
        }

        /// <summary>
        /// 小米 (Xiaomi/Redmi/POCO) 专用读取策略
        /// 优先级: vendor > product > system
        /// </summary>
        private async Task<BuildPropInfo> ReadXiaomiBuildPropAsync(Func<string, long, int, Task<byte[]>> readPartition, 
            string activeSlot, bool hasSuper, long superStart, int sectorSize)
        {
            Log("使用 Xiaomi 专用解析策略...", Color.Blue);
            
            // 小米设备使用标准策略，但优先 vendor 分区
            var result = await _deviceInfoService.ReadBuildPropFromDevice(readPartition, activeSlot, hasSuper, superStart, sectorSize, "Xiaomi");
            
            // 小米特有属性增强：检测 MIUI/HyperOS 版本
            if (result != null)
            {
                // 修正品牌显示
                if (string.IsNullOrEmpty(result.Brand) || result.Brand.ToLower() == "xiaomi")
                {
                    // 从 OTA 版本判断系列
                    if (!string.IsNullOrEmpty(result.OtaVersion))
                    {
                        if (result.OtaVersion.Contains("OS3."))
                            result.Brand = "Xiaomi (HyperOS 3.0)";
                        else if (result.OtaVersion.Contains("OS"))
                            result.Brand = "Xiaomi (HyperOS)";
                        else if (result.OtaVersion.StartsWith("V"))
                            result.Brand = "Xiaomi (MIUI)";
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 联想 (Lenovo/Motorola) 专用读取策略
        /// 优先级: lenovocust > proinfo > vendor
        /// </summary>
        private async Task<BuildPropInfo> ReadLenovoBuildPropAsync(Func<string, long, int, Task<byte[]>> readPartition, 
            string activeSlot, bool hasSuper, long superStart, int sectorSize)
        {
            Log("使用 Lenovo 专用解析策略...", Color.Blue);

            BuildPropInfo result = null;

            // 联想特有分区：lenovocust
            var lenovoCustPart = Partitions?.FirstOrDefault(p => p.Name == "lenovocust");
            if (lenovoCustPart != null)
            {
                try
                {
                    Log("尝试从 lenovocust 读取...", Color.Gray);
                    byte[] data = await readPartition("lenovocust", 0, 512 * 1024);
                    if (data != null)
                    {
                        string content = System.Text.Encoding.UTF8.GetString(data);
                        result = _deviceInfoService.ParseBuildProp(content);
                    }
                }
                catch (Exception ex)
                {
                    _logDetail?.Invoke($"读取 lenovocust 分区异常: {ex.Message}");
                }
            }

            // 联想 proinfo 分区（包含序列号等信息）
            var proinfoPart = Partitions?.FirstOrDefault(p => p.Name == "proinfo");
            if (proinfoPart != null && _currentDeviceInfo != null)
            {
                try
                {
                    byte[] data = await readPartition("proinfo", 0, 4096);
                    if (data != null)
                    {
                        _deviceInfoService.ParseProInfo(data, _currentDeviceInfo);
                    }
                }
                catch (Exception ex)
                {
                    _logDetail?.Invoke($"读取 proinfo 分区异常: {ex.Message}");
                }
            }

            // 回落到通用策略
            if (result == null || string.IsNullOrEmpty(result.MarketName))
            {
                result = await _deviceInfoService.ReadBuildPropFromDevice(readPartition, activeSlot, hasSuper, superStart, sectorSize, "Lenovo");
            }

            // 联想特有处理：识别拯救者系列
            if (result != null)
            {
                string model = result.MarketName ?? result.Model ?? "";
                if (model.Contains("Y700") || model.Contains("Legion") || model.Contains("TB"))
                {
                    if (!model.Contains("拯救者"))
                        result.MarketName = "联想拯救者平板 " + model;
                    result.Brand = "Lenovo (Legion)";
                }
            }

            return result;
        }

        /// <summary>
        /// 中兴/努比亚 专用读取策略
        /// </summary>
        private async Task<BuildPropInfo> ReadZteBuildPropAsync(Func<string, long, int, Task<byte[]>> readPartition, 
            string activeSlot, bool hasSuper, long superStart, int sectorSize)
        {
            Log("使用 ZTE/nubia 专用解析策略...", Color.Blue);
            
            var result = await _deviceInfoService.ReadBuildPropFromDevice(readPartition, activeSlot, hasSuper, superStart, sectorSize, "ZTE");
            
            // 中兴/努比亚特有处理
            if (result != null)
            {
                string brand = result.Brand?.ToLower() ?? "";
                string ota = result.OtaVersion ?? "";

                // 识别红魔系列
                if (ota.Contains("RedMagic") || brand.Contains("nubia"))
                {
                    string model = result.MarketName ?? result.Model ?? "手机";
                    if (!model.Contains("红魔") && ota.Contains("RedMagic"))
                    {
                        result.MarketName = "努比亚 红魔 " + model;
                    }
                    result.Brand = "努比亚 (nubia)";
                }
                else if (brand.Contains("zte"))
                {
                    result.Brand = "中兴 (ZTE)";
                }
            }

            return result;
        }

        /// <summary>
        /// 应用 build.prop 信息到界面
        /// </summary>
        private void ApplyBuildPropInfo(BuildPropInfo buildProp)
        {
            if (buildProp == null) return;

            if (_currentDeviceInfo == null)
            {
                _currentDeviceInfo = new DeviceFullInfo();
            }

            // 品牌 (格式化显示)
            if (!string.IsNullOrEmpty(buildProp.Brand))
            {
                string displayBrand = FormatBrandForDisplay(buildProp.Brand);
                _currentDeviceInfo.Brand = displayBrand;
                UpdateLabelSafe(_brandLabel, "品牌：" + displayBrand);
            }

            // 型号与市场名称 (最高优先级)
            if (!string.IsNullOrEmpty(buildProp.MarketName))
            {
                // 通用增强逻辑：如果市场名包含关键代号，尝试格式化显示
                string finalMarket = buildProp.MarketName;
                
                // 通用联想修正
                if ((finalMarket.Contains("Y700") || finalMarket.Contains("Legion")) && !finalMarket.Contains("拯救者"))
                    finalMarket = "联想拯救者平板 " + finalMarket;

                _currentDeviceInfo.MarketName = finalMarket;
                UpdateLabelSafe(_modelLabel, "型号：" + finalMarket);
            }
            else if (!string.IsNullOrEmpty(buildProp.Model))
            {
                _currentDeviceInfo.Model = buildProp.Model;
                UpdateLabelSafe(_modelLabel, "型号：" + buildProp.Model);
            }

            // 版本信息 (OTA版本/region)
            // 优先级: OtaVersion > Incremental > DisplayId
            string otaVer = "";
            if (!string.IsNullOrEmpty(buildProp.OtaVersion))
                otaVer = buildProp.OtaVersion;
            else if (!string.IsNullOrEmpty(buildProp.Incremental))
                otaVer = buildProp.Incremental;
            else if (!string.IsNullOrEmpty(buildProp.DisplayId))
                otaVer = buildProp.DisplayId;

            if (!string.IsNullOrEmpty(otaVer))
            {
                // 获取品牌用于判断
                string brandLower = (buildProp.Brand ?? "").ToLowerInvariant();
                string manufacturerLower = (buildProp.Manufacturer ?? "").ToLowerInvariant();
                
                // OPLUS 设备 (OnePlus/OPPO/Realme) 版本号清理
                // 原始格式如: PJD110_14.0.0.801(CN01) -> 14.0.0.801(CN01)
                bool isOneplus = brandLower.Contains("oneplus") || manufacturerLower.Contains("oneplus");
                bool isOppo = brandLower.Contains("oppo") || manufacturerLower.Contains("oppo");
                bool isRealme = brandLower.Contains("realme") || manufacturerLower.Contains("realme");
                bool isOplus = isOneplus || isOppo || isRealme || brandLower.Contains("oplus");
                
                if (isOplus)
                {
                    // 提取版本号: "PJD110_14.0.0.801(CN01)" -> "14.0.0.801(CN01)"
                    // 或者 "A.70" 格式 -> 跳过
                    var versionMatch = Regex.Match(otaVer, @"(\d+\.\d+\.\d+\.\d+(?:\([A-Z]{2}\d+\))?)");
                    if (versionMatch.Success)
                    {
                        string cleanVersion = versionMatch.Groups[1].Value;
                        
                        // 根据品牌添加系统名前缀
                        if (isOneplus)
                            otaVer = "OxygenOS " + cleanVersion;
                        else if (isRealme)
                            otaVer = "realme UI " + cleanVersion;
                        else // OPPO
                            otaVer = "ColorOS " + cleanVersion;
                    }
                }
                // 联想 ZUI 版本: 提取 17.0.x.x 格式
                else if (brandLower.Contains("lenovo"))
                {
                    var zuiMatch = Regex.Match(otaVer, @"(\d+\.\d+\.\d+\.\d+)");
                    if (zuiMatch.Success && !otaVer.Contains("ZUI"))
                        otaVer = "ZUI " + zuiMatch.Groups[1].Value;
                }
                // 小米 HyperOS 3.0 (Android 16+)
                else if (otaVer.StartsWith("OS3.") && !otaVer.Contains("HyperOS"))
                {
                    otaVer = "HyperOS 3.0 " + otaVer;
                }
                // 小米 HyperOS 1.0/2.0
                else if (otaVer.StartsWith("OS") && !otaVer.Contains("HyperOS"))
                {
                    otaVer = "HyperOS " + otaVer;
                }
                // 小米 MIUI 时代
                else if (otaVer.StartsWith("V") && !otaVer.Contains("MIUI") && (brandLower.Contains("xiaomi") || brandLower.Contains("redmi")))
                {
                    otaVer = "MIUI " + otaVer;
                }
                // 红魔 RedMagicOS
                else if (otaVer.Contains("RedMagic") && !otaVer.StartsWith("RedMagicOS"))
                {
                    otaVer = otaVer.Replace("RedMagic", "RedMagicOS ");
                }
                // 中兴 NebulaOS/MiFavor
                else if (brandLower.Contains("zte") && !otaVer.Contains("NebulaOS"))
                {
                    otaVer = "NebulaOS " + otaVer;
                }

                _currentDeviceInfo.OtaVersion = otaVer;
                UpdateLabelSafe(_otaVersionLabel, "版本：" + otaVer);
            }

            // Android 版本
            if (!string.IsNullOrEmpty(buildProp.AndroidVersion))
            {
                _currentDeviceInfo.AndroidVersion = buildProp.AndroidVersion;
                _currentDeviceInfo.SdkVersion = buildProp.SdkVersion;
            }
            
            // 设备代号 (使用 unlockLabel 显示设备内部代号)
            // 优先级: Codename > Device > DeviceName
            string codename = "";
            if (!string.IsNullOrEmpty(buildProp.Codename))
                codename = buildProp.Codename;
            else if (!string.IsNullOrEmpty(buildProp.Device))
                codename = buildProp.Device;
            else if (!string.IsNullOrEmpty(buildProp.DeviceName))
                codename = buildProp.DeviceName;
            
            if (!string.IsNullOrEmpty(codename))
            {
                _currentDeviceInfo.DeviceCodename = codename;
                UpdateLabelSafe(_unlockLabel, "代号：" + codename);
            }
            else if (!string.IsNullOrEmpty(buildProp.Model))
            {
                // 如果没有代号，使用型号作为备选
                _currentDeviceInfo.Model = buildProp.Model;
                UpdateLabelSafe(_unlockLabel, "代号：" + buildProp.Model);
            }

            // 区域信息
            if (!string.IsNullOrEmpty(buildProp.Region))
                _currentDeviceInfo.Region = buildProp.Region;
            if (!string.IsNullOrEmpty(buildProp.MarketRegion))
                _currentDeviceInfo.MarketRegion = buildProp.MarketRegion;
            if (!string.IsNullOrEmpty(buildProp.DevProduct))
                _currentDeviceInfo.DevProduct = buildProp.DevProduct;
            if (!string.IsNullOrEmpty(buildProp.Product))
                _currentDeviceInfo.Product = buildProp.Product;
            
            // 构建信息
            if (!string.IsNullOrEmpty(buildProp.BuildId))
                _currentDeviceInfo.BuildId = buildProp.BuildId;
            if (!string.IsNullOrEmpty(buildProp.DisplayId))
                _currentDeviceInfo.DisplayId = buildProp.DisplayId;
            if (!string.IsNullOrEmpty(buildProp.Fingerprint))
                _currentDeviceInfo.Fingerprint = buildProp.Fingerprint;
            if (!string.IsNullOrEmpty(buildProp.BuildDate))
                _currentDeviceInfo.BuiltDate = buildProp.BuildDate;
            if (!string.IsNullOrEmpty(buildProp.BuildUtc))
                _currentDeviceInfo.BuildTimestamp = buildProp.BuildUtc;
            if (!string.IsNullOrEmpty(buildProp.SecurityPatch))
                _currentDeviceInfo.SecurityPatch = buildProp.SecurityPatch;
            if (!string.IsNullOrEmpty(buildProp.OtaVersionFull))
                _currentDeviceInfo.OtaVersionFull = buildProp.OtaVersionFull;

            // OPLUS/Realme 特有属性
            if (!string.IsNullOrEmpty(buildProp.OplusProject))
                _currentDeviceInfo.OplusProject = buildProp.OplusProject;
            if (!string.IsNullOrEmpty(buildProp.OplusNvId))
                _currentDeviceInfo.OplusNvId = buildProp.OplusNvId;

            // Lenovo 特有属性
            if (!string.IsNullOrEmpty(buildProp.LenovoSeries))
            {
                _currentDeviceInfo.LenovoSeries = buildProp.LenovoSeries;
                Log(string.Format("  联想系列: {0}", buildProp.LenovoSeries), Color.Blue);
                // 联想系列通常比型号更直观，如果 MarketName 为空，可用它替代
                if (string.IsNullOrEmpty(_currentDeviceInfo.MarketName))
                {
                    UpdateLabelSafe(_modelLabel, "型号：" + buildProp.LenovoSeries);
                }
            }

            // 中兴/努比亚/红魔 特殊处理 (型号名称修正)
            if (!string.IsNullOrEmpty(buildProp.Brand))
            {
                string b = buildProp.Brand.ToLower();
                if (b == "nubia" || b == "zte")
                {
                    // 如果是红魔系列，更新型号名称
                    string ota = buildProp.OtaVersion ?? buildProp.DisplayId ?? "";
                    if (ota.Contains("RedMagic"))
                    {
                        // 红魔系列型号格式化
                        string rmMarket = "";
                        if (buildProp.Model == "NX789J")
                            rmMarket = "红魔 10 Pro (NX789J)";
                        else if (!string.IsNullOrEmpty(buildProp.MarketName))
                            rmMarket = buildProp.MarketName.Contains("红魔") ? buildProp.MarketName : "红魔 " + buildProp.MarketName;
                        else
                            rmMarket = "红魔 " + buildProp.Model;
                        
                        _currentDeviceInfo.MarketName = rmMarket;
                        UpdateLabelSafe(_modelLabel, "型号：" + rmMarket);
                    }
                    else if (buildProp.Model == "NX789J")
                    {
                        _currentDeviceInfo.MarketName = "红魔 10 Pro";
                        UpdateLabelSafe(_modelLabel, "型号：红魔 10 Pro");
                    }
                }
            }
        }

        /// <summary>
        /// 从设备 Super 分区在线读取 build.prop 并更新设备信息（公开方法，可单独调用）
        /// </summary>
        public async Task<bool> ReadBuildPropFromDeviceAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            // 检查是否有 super 分区
            bool hasSuper = Partitions != null && Partitions.Exists(p => p.Name == "super");
            if (!hasSuper)
            {
                Log("未找到 super 分区，无法读取 build.prop", Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                StartOperationTimer("读取设备信息", 1, 0);
                Log("正在从设备读取 build.prop...", Color.Blue);

                await TryReadBuildPropInternalAsync();
                
                UpdateTotalProgress(1, 1);
                return _currentDeviceInfo != null && !string.IsNullOrEmpty(_currentDeviceInfo.MarketName);
            }
            catch (Exception ex)
            {
                Log("读取 build.prop 失败: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
                StopOperationTimer();
            }
        }

        /// <summary>
        /// 格式化品牌显示名称
        /// </summary>
        private string FormatBrandForDisplay(string brand)
        {
            if (string.IsNullOrEmpty(brand)) return "未知";
            
            string lower = brand.ToLowerInvariant();
            
            // OnePlus
            if (lower.Contains("oneplus"))
                return "一加 (OnePlus)";
            // OPPO
            if (lower == "oppo")
                return "OPPO";
            // realme
            if (lower.Contains("realme"))
                return "realme";
            // Xiaomi / MIUI
            if (lower == "xiaomi" || lower == "miui" || lower.Contains("xiaomi"))
                return "小米 (Xiaomi)";
            // Redmi
            if (lower == "redmi" || lower.Contains("redmi"))
                return "红米 (Redmi)";
            // POCO
            if (lower == "poco" || lower.Contains("poco"))
                return "POCO";
            // nubia
            if (lower == "nubia")
                return "努比亚 (nubia)";
            // ZTE
            if (lower == "zte")
                return "中兴 (ZTE)";
            // Lenovo
            if (lower.Contains("lenovo"))
                return "联想 (Lenovo)";
            // Motorola
            if (lower.Contains("motorola") || lower.Contains("moto"))
                return "摩托罗拉 (Motorola)";
            // Samsung
            if (lower.Contains("samsung"))
                return "三星 (Samsung)";
            // Meizu
            if (lower.Contains("meizu"))
                return "魅族 (Meizu)";
            // vivo
            if (lower == "vivo")
                return "vivo";
            // iQOO
            if (lower == "iqoo")
                return "iQOO";
            
            // 首字母大写返回
            return char.ToUpper(brand[0]) + brand.Substring(1).ToLower();
        }

        /// <summary>
        /// 清空设备信息标签
        /// </summary>
        public void ClearDeviceInfoLabels()
        {
            _currentDeviceInfo = null;
            UpdateLabelSafe(_brandLabel, "品牌：等待连接");
            UpdateLabelSafe(_chipLabel, "芯片：等待连接");
            UpdateLabelSafe(_modelLabel, "型号：等待连接");
            UpdateLabelSafe(_serialLabel, "芯片序列号：等待连接");
            UpdateLabelSafe(_storageLabel, "存储：等待连接");
            UpdateLabelSafe(_unlockLabel, "型号：等待连接");
            UpdateLabelSafe(_otaVersionLabel, "版本：等待连接");
        }

        #endregion

        public async Task<bool> ReadPartitionTableAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                // 读取分区表：分两阶段 - GPT读取(80%) + 设备信息解析(20%)
                StartOperationTimer("读取分区表", 100, 0);
                UpdateProgressBarDirect(_progressBar, 0);
                UpdateProgressBarDirect(_subProgressBar, 0);
                Log("正在读取分区表 (GPT)...", Color.Blue);

                // 进度回调 - GPT 读取映射到 0-80%
                int maxLuns = 6;
                var totalProgress = new Progress<Tuple<int, int>>(t => {
                    double percent = 80.0 * t.Item1 / t.Item2;
                    UpdateProgressBarDirect(_progressBar, percent);
                });
                var subProgress = new Progress<double>(p => UpdateProgressBarDirect(_subProgressBar, p));

                // 使用带进度的 ReadAllGptAsync
                var partitions = await _service.ReadAllGptAsync(maxLuns, totalProgress, subProgress, _cts.Token);
                
                UpdateProgressBarDirect(_progressBar, 80);
                UpdateProgressBarDirect(_subProgressBar, 100);

                if (partitions != null && partitions.Count > 0)
                {
                    Partitions = partitions;
                    UpdatePartitionListView(partitions);
                    UpdateDeviceInfoFromPartitions();  // 更新设备信息（从分区获取更多信息）
                    PartitionsLoaded?.Invoke(this, partitions);
                    Log(string.Format("成功读取 {0} 个分区", partitions.Count), Color.Green);
                    
                    // GPT 读取成功后自动勾选"跳过引导"，下次操作可以直接使用
                    SetSkipSaharaChecked(true);
                    
                    // 读取分区表后，尝试读取设备信息（build.prop）- 占 80-100%
                    var superPart = partitions.Find(p => p.Name.Equals("super", StringComparison.OrdinalIgnoreCase));
                    var systemPart = partitions.Find(p => p.Name.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                                                          p.Name.Equals("system_a", StringComparison.OrdinalIgnoreCase));
                    var vendorPart = partitions.Find(p => p.Name.Equals("vendor", StringComparison.OrdinalIgnoreCase) ||
                                                          p.Name.Equals("vendor_a", StringComparison.OrdinalIgnoreCase));
                    
                    if (superPart != null || systemPart != null || vendorPart != null)
                    {
                        string partType = superPart != null ? "super" : (systemPart != null ? "system" : "vendor");
                        Log(string.Format("检测到 {0} 分区，开始读取设备信息...", partType), Color.Blue);
                        UpdateProgressBarDirect(_subProgressBar, 0);
                        await TryReadBuildPropInternalAsync();
                    }
                    else
                    {
                        Log("未检测到 super/system/vendor 分区，跳过设备信息读取", Color.Orange);
                    }
                    
                    UpdateProgressBarDirect(_progressBar, 100);
                    UpdateProgressBarDirect(_subProgressBar, 100);
                    
                    return true;
                }
                else
                {
                    Log("未读取到分区", Color.Orange);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log("读取分区表失败: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                EndOperation(releasePort: true);  // 读取分区表后释放端口
            }
        }

        public async Task<bool> ReadPartitionAsync(string partitionName, string outputPath)
        {
            if (!await EnsureConnectedAsync()) return false;
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            var p = _service.FindPartition(partitionName);
            long totalBytes = p?.Size ?? 0;

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                StartOperationTimer("读取 " + partitionName, 1, 0, totalBytes);
                Log(string.Format("正在读取分区 {0}...", partitionName), Color.Blue);

                var progress = new Progress<double>(p => UpdateSubProgressFromPercent(p));
                bool success = await _service.ReadPartitionAsync(partitionName, outputPath, progress, _cts.Token);

                UpdateTotalProgress(1, 1, totalBytes);

                if (success) Log(string.Format("分区 {0} 已保存到 {1}", partitionName, outputPath), Color.Green);
                else Log(string.Format("读取 {0} 失败", partitionName), Color.Red);

                return success;
            }
            catch (Exception ex)
            {
                Log("读取分区失败: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                EndOperation(releasePort: true);  // 操作完成后释放端口
            }
        }

        public async Task<bool> WritePartitionAsync(string partitionName, string filePath)
        {
            if (!await EnsureConnectedAsync()) return false;
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (!File.Exists(filePath)) { Log("文件不存在: " + filePath, Color.Red); return false; }

            if (IsProtectPartitionsEnabled() && RawprogramParser.IsSensitivePartition(partitionName))
            {
                Log(string.Format("跳过敏感分区: {0}", partitionName), Color.Orange);
                return false;
            }

            long totalBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                StartOperationTimer("写入 " + partitionName, 1, 0, totalBytes);
                Log(string.Format("正在写入分区 {0}...", partitionName), Color.Blue);

                var progress = new Progress<double>(p => UpdateSubProgressFromPercent(p));
                bool success = await _service.WritePartitionAsync(partitionName, filePath, progress, _cts.Token);

                UpdateTotalProgress(1, 1, totalBytes);

                if (success) Log(string.Format("分区 {0} 写入成功", partitionName), Color.Green);
                else Log(string.Format("写入 {0} 失败", partitionName), Color.Red);

                return success;
            }
            catch (Exception ex)
            {
                Log("写入分区失败: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                EndOperation(releasePort: true);  // 操作完成后释放端口
            }
        }

        public async Task<bool> ErasePartitionAsync(string partitionName)
        {
            if (!await EnsureConnectedAsync()) return false;
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            if (IsProtectPartitionsEnabled() && RawprogramParser.IsSensitivePartition(partitionName))
            {
                Log(string.Format("跳过敏感分区: {0}", partitionName), Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                StartOperationTimer("擦除 " + partitionName, 1, 0);
                Log(string.Format("正在擦除分区 {0}...", partitionName), Color.Blue);
                UpdateLabelSafe(_speedLabel, "速度：擦除中...");

                // 擦除没有细粒度进度，模拟进度
                UpdateProgressBarDirect(_subProgressBar, 50);

                bool success = await _service.ErasePartitionAsync(partitionName, _cts.Token);

                UpdateProgressBarDirect(_subProgressBar, 100);
                UpdateTotalProgress(1, 1);

                if (success) Log(string.Format("分区 {0} 已擦除", partitionName), Color.Green);
                else Log(string.Format("擦除 {0} 失败", partitionName), Color.Red);

                return success;
            }
            catch (Exception ex)
            {
                Log("擦除分区失败: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                EndOperation(releasePort: true);  // 操作完成后释放端口
            }
        }

        #region 批量操作 (支持双进度条)

        /// <summary>
        /// 批量读取分区
        /// </summary>
        public async Task<int> ReadPartitionsBatchAsync(List<Tuple<string, string>> partitionsToRead)
        {
            if (!await EnsureConnectedAsync()) return 0;
            if (IsBusy) { Log("操作进行中", Color.Orange); return 0; }

            int total = partitionsToRead.Count;
            int success = 0;
            
            // 预先获取各分区的总大小，用于流畅进度条
            long totalBytes = 0;
            foreach (var item in partitionsToRead)
            {
                var p = _service.FindPartition(item.Item1);
                if (p != null) totalBytes += p.Size;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                StartOperationTimer("批量读取", total, 0, totalBytes);
                Log(string.Format("开始批量读取 {0} 个分区 (总计: {1:F2} MB)...", total, totalBytes / 1024.0 / 1024.0), Color.Blue);

                // 预先获取分区大小信息（在 UI 线程）
                var partitionSizes = new Dictionary<string, long>();
                foreach (var item in partitionsToRead)
                {
                    var p = _service.FindPartition(item.Item1);
                    partitionSizes[item.Item1] = p?.Size ?? 0;
                }

                // 整个读取循环在后台线程执行
                var ct = _cts.Token;
                var readResult = await Task.Run(async () =>
                {
                    int bgSuccess = 0;
                    long bgCompletedBytes = 0;
                    
                    var failedList = new List<string>();
                    
                    for (int i = 0; i < total; i++)
                    {
                        if (ct.IsCancellationRequested) break;

                        var item = partitionsToRead[i];
                        string partitionName = item.Item1;
                        string outputPath = item.Item2;
                        string fileName = System.IO.Path.GetFileName(outputPath);
                        long pSize = partitionSizes.ContainsKey(partitionName) ? partitionSizes[partitionName] : 0;

                        // 异步更新 UI（不重置子进度条，避免抽搐）
                        int idx = i;
                        long completed = bgCompletedBytes;
                        UpdateUIAsync(() => {
                            // 只更新标签和总进度，不调用 UpdateTotalProgress（它会重置子进度条）
                            UpdateLabelSafe(_operationLabel, string.Format("读取 {0} ({1}/{2})", partitionName, idx + 1, total));
                            // 更新总进度条（基于已完成字节）
                            if (totalBytes > 0)
                            {
                                double totalPercent = 100.0 * completed / totalBytes;
                                UpdateProgressBarDirect(_progressBar, totalPercent);
                            }
                            // 重置子进度条为 0（新分区开始）
                            UpdateProgressBarDirect(_subProgressBar, 0);
                            _currentStepBytes = pSize;
                        });

                        IProgress<double> progress = new LightweightProgress(p => UpdateUIAsync(() => UpdateSubProgressFromPercent(p)));
                        
                        bool ok;
                        string errorMsg = null;
                        try
                        {
                            ok = await _service.ReadPartitionAsync(partitionName, outputPath, progress, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            ok = false;
                            errorMsg = ex.Message;
                        }

                        if (ok)
                        {
                            bgSuccess++;
                            bgCompletedBytes += pSize;
                            UpdateUIAsync(() => Log(string.Format("[{0}/{1}] [成功] {2} -> {3}", idx + 1, total, partitionName, fileName), Color.Green));
                        }
                        else
                        {
                            failedList.Add(partitionName);
                            string errDetail = string.IsNullOrEmpty(errorMsg) ? "读取失败" : errorMsg;
                            UpdateUIAsync(() => Log(string.Format("[{0}/{1}] [失败] {2}: {3}", idx + 1, total, partitionName, errDetail), Color.Red));
                        }
                    }
                    
                    return Tuple.Create(bgSuccess, bgCompletedBytes, failedList);
                }).ConfigureAwait(false);
                
                success = readResult.Item1;
                var failedPartitions = readResult.Item3;

                UpdateTotalProgress(total, total, totalBytes, 0);
                Log("------------------------------------------------", Color.Gray);
                if (success == total)
                {
                    Log(string.Format("批量读取完成: {0} 个分区全部成功!", total), Color.Green);
                }
                else
                {
                    Log(string.Format("批量读取完成: {0}/{1} 成功, {2} 个失败", success, total, total - success), Color.Orange);
                    if (failedPartitions != null && failedPartitions.Count > 0)
                    {
                        Log(string.Format("失败分区: {0}", string.Join(", ", failedPartitions)), Color.Red);
                    }
                }
                return success;
            }
            catch (Exception ex)
            {
                Log("批量读取失败: " + ex.Message, Color.Red);
                return success;
            }
            finally
            {
                EndOperation(releasePort: true);  // 批量操作完成后释放端口
            }
        }

        /// <summary>
        /// 批量写入分区 (简单版本)
        /// </summary>
        public async Task<int> WritePartitionsBatchAsync(List<Tuple<string, string>> partitionsToWrite)
        {
            // 转换为新格式 (使用 LUN=0, StartSector=0 作为占位)
            var converted = partitionsToWrite.Select(t => Tuple.Create(t.Item1, t.Item2, 0, 0L)).ToList();
            return await WritePartitionsBatchAsync(converted, null, false);
        }

        /// <summary>
        /// 批量写入分区 (支持 Patch 和激活启动分区)
        /// </summary>
        /// <param name="partitionsToWrite">分区信息列表 (名称, 文件路径, LUN, StartSector)</param>
        /// <param name="patchFiles">Patch XML 文件列表 (可选)</param>
        /// <param name="activateBootLun">是否激活启动 LUN (UFS)</param>
        public async Task<int> WritePartitionsBatchAsync(List<Tuple<string, string, int, long>> partitionsToWrite, List<string> patchFiles, bool activateBootLun)
        {
            return await WritePartitionsBatchAsync(partitionsToWrite, patchFiles, activateBootLun, null);
        }
        
        /// <summary>
        /// 批量写入分区 (支持 Patch、激活启动分区和 MetaSuper)
        /// </summary>
        /// <param name="partitionsToWrite">分区信息列表 (名称, 文件路径, LUN, StartSector)</param>
        /// <param name="patchFiles">Patch XML 文件列表 (可选)</param>
        /// <param name="activateBootLun">是否激活启动 LUN (UFS)</param>
        /// <param name="metaSuperFirmwareRoot">MetaSuper 固件根目录 (包含 IMAGES 和 META)，为空则跳过</param>
        public async Task<int> WritePartitionsBatchAsync(List<Tuple<string, string, int, long>> partitionsToWrite, List<string> patchFiles, bool activateBootLun, string metaSuperFirmwareRoot)
        {
            if (!await EnsureConnectedAsync()) return 0;
            if (IsBusy) { Log("操作进行中", Color.Orange); return 0; }

            int total = partitionsToWrite.Count;
            int success = 0;
            bool hasPatch = patchFiles != null && patchFiles.Count > 0;

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                // MetaSuper 任务合并
                List<SakuraEDL.Qualcomm.Services.OplusSuperFlashManager.FlashTask> metaSuperTasks = null;
                var superPart = _service?.FindPartition("super");
                bool hasMetaSuper = !string.IsNullOrEmpty(metaSuperFirmwareRoot) && superPart != null;
                
                if (hasMetaSuper)
                {
                    Log("[MetaSuper] 开始解析 Super 逻辑分区布局...", Color.Blue);
                    string activeSlot = _service.CurrentSlot;
                    // 槽位未知或未定义时默认使用 a 槽位
                    if (activeSlot == "nonexistent" || activeSlot == "undefined" || activeSlot == "unknown" || string.IsNullOrEmpty(activeSlot)) 
                        activeSlot = "a";
                    
                    string nvId = _currentDeviceInfo?.OplusNvId ?? "";
                    long superPartitionSize = superPart.Size;
                    
                    Log(string.Format("[高通] Super 分区: 起始扇区={0}, 大小={1} MB", 
                        superPart.StartSector, superPartitionSize / 1024 / 1024), Color.Gray);
                    
                    var superManager = new SakuraEDL.Qualcomm.Services.OplusSuperFlashManager(s => Log(s, Color.Gray));
                    metaSuperTasks = await superManager.PrepareSuperTasksAsync(
                        metaSuperFirmwareRoot, superPart.StartSector, (int)superPart.SectorSize, activeSlot, nvId, superPartitionSize);
                    
                    if (metaSuperTasks.Count > 0)
                    {
                        // 校验任务
                        var validation = superManager.ValidateTasks(metaSuperTasks, superPartitionSize, (int)superPart.SectorSize);
                        if (!validation.IsValid)
                        {
                            foreach (var err in validation.Errors)
                                Log("[MetaSuper] 错误: " + err, Color.Red);
                            Log("[MetaSuper] 校验失败，将跳过 Super 逻辑分区写入", Color.Orange);
                            metaSuperTasks = null;
                        }
                        else
                        {
                            foreach (var warn in validation.Warnings)
                                Log("[MetaSuper] 警告: " + warn, Color.Orange);
                            Log(string.Format("[MetaSuper] 已加载 {0} 个 Super 逻辑分区任务", metaSuperTasks.Count), Color.Blue);
                        }
                    }
                    else
                    {
                        Log("[MetaSuper] 未找到可用的 Super 逻辑分区镜像", Color.Orange);
                        metaSuperTasks = null;
                    }
                }
                
                // 计算总步骤: 分区写入 + MetaSuper + Patch + 激活
                int metaSuperCount = metaSuperTasks?.Count ?? 0;
                int totalSteps = total + metaSuperCount + (hasPatch ? 1 : 0) + (activateBootLun ? 1 : 0);
                
                // 在后台线程预先获取所有文件大小，避免 UI 卡住
                Log("正在计算文件大小...", Color.Gray);
                var fileSizes = await Task.Run(() =>
                {
                    var sizes = new Dictionary<string, long>();
                    foreach (var item in partitionsToWrite)
                    {
                        string path = item.Item2;
                        try
                        {
                            if (File.Exists(path))
                            {
                                if (SparseStream.IsSparseFile(path))
                                {
                                    using (var ss = SparseStream.Open(path))
                                        sizes[path] = ss.GetRealDataSize();
                                }
                                else
                                {
                                    sizes[path] = new FileInfo(path).Length;
                                }
                            }
                            else
                            {
                                sizes[path] = 0;
                            }
                        }
                        catch
                        {
                            sizes[path] = 0;
                        }
                    }
                    // MetaSuper 文件大小
                    if (metaSuperTasks != null)
                    {
                        foreach (var task in metaSuperTasks)
                        {
                            if (!sizes.ContainsKey(task.FilePath))
                                sizes[task.FilePath] = task.SizeInBytes;
                        }
                    }
                    return sizes;
                });
                
                long totalBytes = fileSizes.Values.Sum();
                
                // 合并并按物理扇区排序所有任务
                // 任务格式: (分区名, 文件路径, LUN, StartSector, IsMetaSuper)
                var allTasks = new List<Tuple<string, string, int, long, bool>>();
                
                // 添加常规分区任务
                foreach (var item in partitionsToWrite)
                {
                    allTasks.Add(Tuple.Create(item.Item1, item.Item2, item.Item3, item.Item4, false));
                }
                
                // 添加 MetaSuper 任务
                if (metaSuperTasks != null && superPart != null)
                {
                    foreach (var task in metaSuperTasks)
                    {
                        allTasks.Add(Tuple.Create(
                            "[Super] " + task.PartitionName,  // 标记为 Super 逻辑分区
                            task.FilePath,
                            superPart.Lun,
                            task.PhysicalSector,
                            true  // 标记为 MetaSuper
                        ));
                    }
                }
                
                // 按物理扇区排序 (LUN 相同时按扇区排序，不同 LUN 按 LUN 号排序)
                allTasks = allTasks.OrderBy(t => t.Item3).ThenBy(t => t.Item4).ToList();
                
                total = allTasks.Count;
                totalSteps = total + (hasPatch ? 1 : 0) + (activateBootLun ? 1 : 0);

                StartOperationTimer("批量写入", totalSteps, 0, totalBytes);
                Log(string.Format("开始批量写入 {0} 个分区 (实际数据: {1:F2} MB)...", total, totalBytes / 1024.0 / 1024.0), Color.Blue);

                // 整个写入循环在后台线程执行，UI 线程只负责显示
                var ct = _cts.Token;
                var protectEnabled = IsProtectPartitionsEnabled();
                var failedPartitions = new System.Collections.Concurrent.ConcurrentBag<string>();
                
                var writeResult = await Task.Run(async () =>
                {
                    int bgSuccess = 0;
                    long bgCompletedBytes = 0;
                    
                    for (int i = 0; i < total; i++)
                    {
                        if (ct.IsCancellationRequested) break;

                        var item = allTasks[i];
                        string partitionName = item.Item1;
                        string filePath = item.Item2;
                        int lun = item.Item3;
                        long startSector = item.Item4;
                        bool isMetaSuper = item.Item5;
                        string fileName = System.IO.Path.GetFileName(filePath);
                        
                        // MetaSuper 任务去掉 "[Super] " 前缀用于显示
                        string displayName = isMetaSuper ? partitionName.Replace("[Super] ", "") : partitionName;
                        
                        long fSize = fileSizes.ContainsKey(filePath) ? fileSizes[filePath] : 0;

                        // 敏感分区保护（不对 MetaSuper 任务应用）
                        if (!isMetaSuper && protectEnabled && RawprogramParser.IsSensitivePartition(partitionName))
                        {
                            UpdateUIAsync(() => Log(string.Format("[{0}/{1}] [跳过] {2} (敏感分区保护)", i + 1, total, partitionName), Color.Orange));
                            bgCompletedBytes += fSize;
                            continue;
                        }

                        // 异步更新 UI 状态（不调用 UpdateTotalProgress，避免进度条抽搐）
                        int idx = i;
                        long completed = bgCompletedBytes;
                        long totalBytesLocal = totalBytes;
                        string displayPartName = displayName;
                        UpdateUIAsync(() => {
                            UpdateLabelSafe(_operationLabel, string.Format("写入 {0} ({1}/{2})", displayPartName, idx + 1, total));
                            // 更新总进度条（基于已完成字节）
                            if (totalBytesLocal > 0)
                            {
                                double totalPercent = 100.0 * completed / totalBytesLocal;
                                UpdateProgressBarDirect(_progressBar, totalPercent);
                            }
                            // 重置子进度条为 0（新分区开始）
                            UpdateProgressBarDirect(_subProgressBar, 0);
                            _currentStepBytes = fSize;
                        });

                        // 轻量级进度回调
                        IProgress<double> progress = new LightweightProgress(p => UpdateUIAsync(() => UpdateSubProgressFromPercent(p)));
                        
                        bool ok;
                        string errorMsg = null;
                        
                        // MetaSuper 任务总是使用 DirectWrite (指定 LUN 和扇区)
                        bool useDirectWrite = isMetaSuper || 
                            partitionName == "PrimaryGPT" || partitionName == "BackupGPT" || 
                            partitionName.StartsWith("gpt_main") || partitionName.StartsWith("gpt_backup") ||
                            startSector != 0;
                        
                        try
                        {
                            if (useDirectWrite)
                            {
                                // MetaSuper 任务使用 "super" 作为目标分区名
                                string targetPartName = isMetaSuper ? "super" : partitionName;
                                ok = await _service.WriteDirectAsync(targetPartName, filePath, lun, startSector, progress, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                ok = await _service.WritePartitionAsync(partitionName, filePath, progress, ct).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            ok = false;
                            errorMsg = ex.Message;
                        }

                        if (ok)
                        {
                            bgSuccess++;
                            bgCompletedBytes += fSize;
                            // 成功时显示简洁的确认
                            string msgPrefix = isMetaSuper ? "[Super]" : "";
                            UpdateUIAsync(() => Log(string.Format("[{0}/{1}] [成功] {2}{3} -> {4}", idx + 1, total, msgPrefix, fileName, displayPartName), Color.Green));
                        }
                        else
                        {
                            failedPartitions.Add(displayName);
                            // 失败时显示详细错误
                            string errDetail = string.IsNullOrEmpty(errorMsg) ? "写入被拒绝或超时" : errorMsg;
                            UpdateUIAsync(() => Log(string.Format("[{0}/{1}] [失败] {2} -> {3}: {4}", idx + 1, total, fileName, displayPartName, errDetail), Color.Red));
                        }
                    }
                    
                    return Tuple.Create(bgSuccess, bgCompletedBytes);
                }).ConfigureAwait(false);
                
                success = writeResult.Item1;
                long currentCompletedBytes = writeResult.Item2;

                // 汇总显示结果
                Log("------------------------------------------------", Color.Gray);
                if (success == total)
                {
                    Log(string.Format("分区写入完成: {0} 个分区全部成功!", total), Color.Green);
                }
                else
                {
                    Log(string.Format("分区写入完成: {0}/{1} 成功, {2} 个失败", success, total, total - success), Color.Orange);
                    if (failedPartitions.Count > 0)
                    {
                        Log(string.Format("失败分区: {0}", string.Join(", ", failedPartitions)), Color.Red);
                    }
                }

                // 2. 应用 Patch (如果有)
                if (hasPatch && !_cts.Token.IsCancellationRequested)
                {
                    UpdateTotalProgress(total, totalSteps, currentCompletedBytes, 0);
                    UpdateLabelSafe(_operationLabel, "应用补丁...");
                    Log(string.Format("开始应用 {0} 个 Patch 文件...", patchFiles.Count), Color.Blue);

                    int patchCount = await _service.ApplyPatchFilesAsync(patchFiles, _cts.Token);
                    Log(string.Format("成功应用 {0} 个补丁", patchCount), patchCount > 0 ? Color.Green : Color.Orange);
                }
                else if (!hasPatch)
                {
                    Log("无 Patch 文件，跳过补丁步骤", Color.Gray);
                }

                // 3. 修复 GPT (关键步骤！修复主备 GPT 和 CRC)
                if (!_cts.Token.IsCancellationRequested)
                {
                    UpdateLabelSafe(_operationLabel, "修复 GPT...");
                    Log("修复 GPT 分区表 (主备同步 + CRC)...", Color.Blue);
                    
                    // 修复所有 LUN 的 GPT (-1 表示所有 LUN)
                    bool fixOk = await _service.FixGptAsync(-1, _cts.Token);
                    if (fixOk)
                        Log("GPT 修复成功", Color.Green);
                    else
                        Log("GPT 修复失败 (可能导致无法启动)", Color.Orange);
                }

                // 4. 激活启动分区 (UFS 设备需要激活，eMMC 只有 LUN0)
                if (activateBootLun && !_cts.Token.IsCancellationRequested)
                {
                    UpdateTotalProgress(total + (hasPatch ? 1 : 0), totalSteps, currentCompletedBytes);
                    UpdateLabelSafe(_operationLabel, "回读分区表检测槽位...");
                    
                    // 回读 GPT 检测当前槽位
                    Log("回读 GPT 检测当前槽位...", Color.Blue);
                    var partitions = await _service.ReadAllGptAsync(6, _cts.Token);
                    
                    string currentSlot = _service.CurrentSlot;
                    Log(string.Format("检测到当前槽位: {0}", currentSlot), Color.Blue);

                    // 根据槽位确定启动 LUN - 严格按照 A/B 分区状态
                    int bootLun = -1;
                    string bootSlotName = "";
                    
                    if (currentSlot == "a")
                    {
                        bootLun = 1;  // slot_a -> LUN1
                        bootSlotName = "boot_a";
                    }
                    else if (currentSlot == "b")
                    {
                        bootLun = 2;  // slot_b -> LUN2
                        bootSlotName = "boot_b";
                    }
                    else if (currentSlot == "undefined" || currentSlot == "unknown")
                    {
                        // A/B 分区存在但未设置激活状态，尝试从写入的分区推断
                        // 检查是否写入了 _a 或 _b 后缀的分区
                        int slotACount = partitionsToWrite.Count(p => p.Item1.EndsWith("_a"));
                        int slotBCount = partitionsToWrite.Count(p => p.Item1.EndsWith("_b"));
                        
                        if (slotACount > slotBCount)
                        {
                            bootLun = 1;
                            bootSlotName = "boot_a (根据写入分区推断)";
                            Log("槽位未激活，根据写入的 _a 分区推断使用 LUN1", Color.Blue);
                        }
                        else if (slotBCount > slotACount)
                        {
                            bootLun = 2;
                            bootSlotName = "boot_b (根据写入分区推断)";
                            Log("槽位未激活，根据写入的 _b 分区推断使用 LUN2", Color.Blue);
                        }
                        else if (slotACount > 0 && slotBCount > 0)
                        {
                            // 全量刷机：同时刷写了 _a 和 _b 分区，默认激活 slot_a
                            bootLun = 1;
                            bootSlotName = "boot_a (全量刷机默认)";
                            Log("全量刷机模式，默认激活 slot_a (LUN1)", Color.Blue);
                        }
                        else
                        {
                            // 没有 A/B 分区，跳过激活
                            Log("未检测到 A/B 分区，跳过启动分区激活", Color.Gray);
                        }
                    }
                    else if (currentSlot == "nonexistent")
                    {
                        // 设备不支持 A/B 分区，跳过激活
                        Log("设备不支持 A/B 分区，跳过启动分区激活", Color.Gray);
                    }

                    // 只有在确定了 bootLun 后才执行激活
                    if (bootLun > 0)
                    {
                        UpdateLabelSafe(_operationLabel, string.Format("激活启动分区 LUN{0}...", bootLun));
                        Log(string.Format("激活 LUN{0} ({1})...", bootLun, bootSlotName), Color.Blue);

                        bool bootOk = await _service.SetBootLunAsync(bootLun, _cts.Token);
                        if (bootOk)
                            Log(string.Format("LUN{0} 激活成功", bootLun), Color.Green);
                        else
                            Log(string.Format("LUN{0} 激活失败 (部分设备可能不支持)", bootLun), Color.Orange);
                    }
                }

                UpdateTotalProgress(totalSteps, totalSteps);
                return success;
            }
            catch (Exception ex)
            {
                Log("批量写入失败: " + ex.Message, Color.Red);
                return success;
            }
            finally
            {
                IsBusy = false;
                StopOperationTimer();
            }
        }

        /// <summary>
        /// 应用 Patch 文件
        /// </summary>
        public async Task<int> ApplyPatchFilesAsync(List<string> patchFiles)
        {
            if (!await EnsureConnectedAsync()) return 0;
            if (IsBusy) { Log("操作进行中", Color.Orange); return 0; }
            if (patchFiles == null || patchFiles.Count == 0) return 0;

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                StartOperationTimer("应用补丁", patchFiles.Count, 0);
                Log(string.Format("开始应用 {0} 个 Patch 文件...", patchFiles.Count), Color.Blue);

                int totalPatches = 0;
                for (int i = 0; i < patchFiles.Count; i++)
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    UpdateTotalProgress(i, patchFiles.Count);
                    UpdateLabelSafe(_operationLabel, string.Format("Patch {0}/{1}", i + 1, patchFiles.Count));

                    int count = await _service.ApplyPatchXmlAsync(patchFiles[i], _cts.Token);
                    totalPatches += count;
                    Log(string.Format("[{0}/{1}] {2}: {3} 个补丁", i + 1, patchFiles.Count, 
                        Path.GetFileName(patchFiles[i]), count), Color.Green);
                }

                UpdateTotalProgress(patchFiles.Count, patchFiles.Count);
                Log(string.Format("Patch 完成: 共 {0} 个补丁", totalPatches), Color.Green);
                return totalPatches;
            }
            catch (Exception ex)
            {
                Log("应用 Patch 失败: " + ex.Message, Color.Red);
                return 0;
            }
            finally
            {
                IsBusy = false;
                StopOperationTimer();
            }
        }

        /// <summary>
        /// 批量擦除分区
        /// </summary>
        public async Task<int> ErasePartitionsBatchAsync(List<string> partitionNames)
        {
            if (!await EnsureConnectedAsync()) return 0;
            if (IsBusy) { Log("操作进行中", Color.Orange); return 0; }

            int total = partitionNames.Count;
            int success = 0;

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                StartOperationTimer("批量擦除", total, 0);
                Log(string.Format("开始批量擦除 {0} 个分区...", total), Color.Blue);
                UpdateLabelSafe(_speedLabel, "速度：擦除中...");

                for (int i = 0; i < total; i++)
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    string partitionName = partitionNames[i];

                    if (IsProtectPartitionsEnabled() && RawprogramParser.IsSensitivePartition(partitionName))
                    {
                        Log(string.Format("[{0}/{1}] 跳过敏感分区: {2}", i + 1, total, partitionName), Color.Orange);
                        continue;
                    }

                    UpdateTotalProgress(i, total);
                    UpdateLabelSafe(_operationLabel, string.Format("擦除 {0} ({1}/{2})", partitionName, i + 1, total));

                    // 擦除没有细粒度进度，直接更新子进度
                    UpdateProgressBarDirect(_subProgressBar, 50);
                    
                    bool ok = await _service.ErasePartitionAsync(partitionName, _cts.Token);

                    UpdateProgressBarDirect(_subProgressBar, 100);

                    if (ok)
                    {
                        success++;
                        Log(string.Format("[{0}/{1}] {2} 擦除成功", i + 1, total, partitionName), Color.Green);
                    }
                    else
                    {
                        Log(string.Format("[{0}/{1}] {2} 擦除失败", i + 1, total, partitionName), Color.Red);
                    }
                }

                UpdateTotalProgress(total, total);
                Log(string.Format("批量擦除完成: {0}/{1} 成功", success, total), success == total ? Color.Green : Color.Orange);
                return success;
            }
            catch (Exception ex)
            {
                Log("批量擦除失败: " + ex.Message, Color.Red);
                return success;
            }
            finally
            {
                EndOperation(releasePort: true);  // 批量操作完成后释放端口
            }
        }

        #endregion

        public async Task<bool> RebootToEdlAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            try
            {
                bool success = await _service.RebootToEdlAsync(_cts?.Token ?? CancellationToken.None);
                if (success)
                {
                    Log("已发送重启到 EDL 命令", Color.Green);
                    // 断开当前连接，等待设备重新进入 EDL
                    Disconnect();
                    Log("等待设备重新进入 EDL 模式...", Color.Orange);
                }
                return success;
            }
            catch (Exception ex) { Log("重启到 EDL 失败: " + ex.Message, Color.Red); return false; }
        }

        public async Task<bool> RebootToSystemAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            try
            {
                bool success = await _service.RebootAsync(_cts?.Token ?? CancellationToken.None);
                if (success) { Log("设备正在重启到系统", Color.Green); Disconnect(); }
                return success;
            }
            catch (Exception ex) { Log("重启失败: " + ex.Message, Color.Red); return false; }
        }

        public async Task<bool> SwitchSlotAsync(string slot)
        {
            if (!await EnsureConnectedAsync()) return false;
            try
            {
                bool success = await _service.SetActiveSlotAsync(slot, _cts?.Token ?? CancellationToken.None);
                if (success) Log(string.Format("已切换到槽位 {0}", slot), Color.Green);
                else Log("切换槽位失败", Color.Red);
                return success;
            }
            catch (Exception ex) { Log("切换槽位失败: " + ex.Message, Color.Red); return false; }
        }

        public async Task<bool> SetBootLunAsync(int lun)
        {
            if (!await EnsureConnectedAsync()) return false;
            try
            {
                bool success = await _service.SetBootLunAsync(lun, _cts?.Token ?? CancellationToken.None);
                if (success) Log(string.Format("LUN {0} 已激活", lun), Color.Green);
                else Log("激活 LUN 失败", Color.Red);
                return success;
            }
            catch (Exception ex) { Log("激活 LUN 失败: " + ex.Message, Color.Red); return false; }
        }

        public PartitionInfo FindPartition(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return null;
            foreach (var p in Partitions)
            {
                if (p.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return p;
            }
            return null;
        }

        private string GetSelectedPortName()
        {
            try
            {
                if (_portComboBox == null) return "";
                object selectedItem = _portComboBox.SelectedItem;
                if (selectedItem == null) return "";
                string item = selectedItem.ToString();
                int idx = item.IndexOf(" - ");
                return idx > 0 ? item.Substring(0, idx) : item;
            }
            catch { return ""; }
        }

        private bool IsProtectPartitionsEnabled()
        {
            try 
            { 
                if (_protectPartitionsCheckbox == null) return false;
                bool isChecked = _protectPartitionsCheckbox.Checked;
                return isChecked; 
            }
            catch { return false; }
        }

        private bool IsSkipSaharaEnabled()
        {
            try { return _skipSaharaCheckbox != null && (bool)_skipSaharaCheckbox.Checked; }
            catch { return false; }
        }

        private string GetProgrammerPath()
        {
            try { return _programmerPathTextbox != null ? (string)_programmerPathTextbox.Text : ""; }
            catch { return ""; }
        }

        private void SetSkipSaharaChecked(bool value)
        {
            try { if (_skipSaharaCheckbox != null) _skipSaharaCheckbox.Checked = value; }
            catch { /* UI 控件访问失败可忽略 */ }
        }

        /// <summary>
        /// 尝试快速重连（仅重新打开端口，不重新配置 Firehose）
        /// 适用于操作完成后端口被释放的情况
        /// </summary>
        /// <returns>是否成功重连</returns>
        public async Task<bool> QuickReconnectAsync()
        {
            if (_service == null)
            {
                Log("未连接设备", Color.Red);
                return false;
            }
            
            if (!_service.IsPortReleased)
            {
                // 端口未释放，检查是否仍然可用
                if (_service.IsConnectedFast)
                    return true;
            }
            
            _logDetail("[UI] 尝试快速重连...");
            
            // 尝试重新打开端口
            bool success = await _service.EnsurePortOpenAsync(CancellationToken.None);
            if (success)
            {
                _logDetail("[UI] 快速重连成功");
                return true;
            }
            
            _logDetail("[UI] 快速重连失败");
            return false;
        }
        
        private async Task<bool> EnsureConnectedAsync()
        {
            if (_service == null)
            {
                // 不输出错误日志，由调用者处理
                _logDetail("[UI] 服务未初始化");
                return false;
            }
            
            // 如果端口已释放，尝试重新打开
            if (_service.IsPortReleased)
            {
                _logDetail("[UI] 端口已释放，尝试重新打开...");
                if (!await _service.EnsurePortOpenAsync(CancellationToken.None))
                {
                    // 不立即报错和清理状态，让调用者决定如何处理
                    _logDetail("[UI] 端口重新打开失败");
                    return false;
                }
                _logDetail("[UI] 端口重新打开成功");
            }
            
            // 使用快速连接检查（不触发断开事件），避免意外取消用户的"跳过引导"选择
            if (!_service.IsConnectedFast)
            {
                _logDetail("[UI] 快速连接检查失败");
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// 确保连接可用，失败时显示错误并清理状态
        /// </summary>
        private async Task<bool> EnsureConnectedWithCleanupAsync()
        {
            if (await EnsureConnectedAsync())
                return true;
            
            // 连接失败，清理状态
            Log("设备连接已失效，需要重新完整配置", Color.Red);
            SetSkipSaharaChecked(false);
            ConnectionStateChanged?.Invoke(this, false);
            ClearDeviceInfoLabels();
            RefreshPorts();
            return false;
        }

        /// <summary>
        /// 取消当前操作
        /// </summary>
        public void CancelOperation()
        {
            if (_cts != null) 
            { 
                Log("正在取消操作...", Color.Orange);
                _cts.Cancel(); 
                _cts.Dispose(); 
                _cts = null; 
            }
        }

        /// <summary>
        /// 安全重置 CancellationTokenSource（释放旧实例后创建新实例）
        /// </summary>
        private void ResetCancellationToken()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } 
                catch (Exception ex) { _logDetail?.Invoke($"[UI] 取消令牌异常: {ex.Message}"); }
                try { _cts.Dispose(); } 
                catch (Exception ex) { _logDetail?.Invoke($"[UI] 释放令牌异常: {ex.Message}"); }
            }
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// 是否有操作正在进行
        /// </summary>
        public bool HasPendingOperation
        {
            get { return _cts != null && !_cts.IsCancellationRequested; }
        }

        private void Log(string message, Color? color)
        {
            _log(message, color);
        }

        private void UpdateProgress(long current, long total)
        {
            // 实时计算速度 (current=已传输字节, total=总字节)
            if (total > 0 && _operationStopwatch != null)
            {
                // 计算实时速度
                long bytesDelta = current - _lastBytes;
                double timeDelta = (DateTime.Now - _lastSpeedUpdate).TotalSeconds;
                
                if (timeDelta >= 0.2 && bytesDelta > 0) // 每200ms更新一次
                {
                    double instantSpeed = bytesDelta / timeDelta;
                    // 指数移动平均平滑速度
                    _currentSpeed = (_currentSpeed > 0) ? (_currentSpeed * 0.6 + instantSpeed * 0.4) : instantSpeed;
                    _lastBytes = current;
                    _lastSpeedUpdate = DateTime.Now;
                    
                    // 更新速度显示
                    UpdateSpeedDisplayInternal();
                    
                    // 更新时间
                    var elapsed = _operationStopwatch.Elapsed;
                    string timeText = string.Format("时间：{0:00}:{1:00}", (int)elapsed.TotalMinutes, elapsed.Seconds);
                    UpdateLabelSafe(_timeLabel, timeText);
                }
                
                // 1. 计算子进度 (带小数精度)
                double subPercent = (100.0 * current / total);
                subPercent = Math.Max(0, Math.Min(100, subPercent));
                UpdateProgressBarDirect(_subProgressBar, subPercent);
                
                // 2. 计算总进度 (极速流利版 - 基于字节总数)
                if (_totalOperationBytes > 0 && _progressBar != null)
                {
                    long totalProcessed = _completedStepBytes + current;
                    double totalPercent = (100.0 * totalProcessed / _totalOperationBytes);
                    totalPercent = Math.Max(0, Math.Min(100, totalPercent));
                    UpdateProgressBarDirect(_progressBar, totalPercent);

                    // 3. 在界面显示精确到两位小数的百分比
                    UpdateLabelSafe(_operationLabel, string.Format("{0} [{1:F2}%]", _currentOperationName, totalPercent));
                }
                else if (_totalSteps > 0 && _progressBar != null)
                {
                    // 退避方案：基于步骤
                    double totalProgress = (_currentStep + subPercent / 100.0) / _totalSteps * 100.0;
                    UpdateProgressBarDirect(_progressBar, totalProgress);
                    UpdateLabelSafe(_operationLabel, string.Format("{0} [{1:F2}%]", _currentOperationName, totalProgress));
                }
            }
        }
        
        /// <summary>
        /// 直接更新进度条 (支持 double 精度，防闪烁优化)
        /// </summary>
        private int _lastProgressValue = -1;
        private int _lastSubProgressValue = -1;
        
        private void UpdateProgressBarDirect(dynamic progressBar, double percent)
        {
            if (progressBar == null) return;
            try
            {
                // 将 0-100 映射到 0-10000 以获得更高精度
                int intValue = (int)Math.Max(0, Math.Min(10000, percent * 100));
                
                // 检查是哪个进度条，避免重复更新相同值
                bool isMainProgress = (progressBar == _progressBar);
                int lastValue = isMainProgress ? _lastProgressValue : _lastSubProgressValue;
                
                // 值未变化时跳过更新，防止闪烁
                if (intValue == lastValue) return;
                
                // 更新缓存值
                if (isMainProgress) _lastProgressValue = intValue;
                else _lastSubProgressValue = intValue;
                
                if (progressBar.InvokeRequired)
                {
                    progressBar.BeginInvoke(new Action(() => {
                        if (progressBar.Maximum != 10000) progressBar.Maximum = 10000;
                        if (progressBar.Value != intValue) progressBar.Value = intValue;
                    }));
                }
                else
                {
                    if (progressBar.Maximum != 10000) progressBar.Maximum = 10000;
                    if (progressBar.Value != intValue) progressBar.Value = intValue;
                }
            }
            catch { /* UI 进度条更新失败可忽略 */ }
        }
        
        private void UpdateSpeedDisplayInternal()
        {
            if (_speedLabel == null) return;
            
            string speedText;
            if (_currentSpeed >= 1024 * 1024)
                speedText = string.Format("速度：{0:F1} MB/s", _currentSpeed / (1024 * 1024));
            else if (_currentSpeed >= 1024)
                speedText = string.Format("速度：{0:F1} KB/s", _currentSpeed / 1024);
            else if (_currentSpeed > 0)
                speedText = string.Format("速度：{0:F0} B/s", _currentSpeed);
            else
                speedText = "速度：--";
            
            UpdateLabelSafe(_speedLabel, speedText);
        }
        
        /// <summary>
        /// 更新子进度条 (短) - 从百分比
        /// 改进：使用当前步骤的字节数计算真实速度
        /// </summary>
        private void UpdateSubProgressFromPercent(double percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            UpdateProgressBarDirect(_subProgressBar, percent);
            
            // 基于当前步骤的字节数估算已传输字节（不是总操作字节数！）
            long currentStepTransferred = 0;
            if (_currentStepBytes > 0)
            {
                // 使用当前步骤的字节数，而不是整个操作的总字节数
                currentStepTransferred = (long)(_currentStepBytes * percent / 100.0);
            }
            
            // 计算实时速度 (基于增量)
            if (_operationStopwatch != null)
            {
                // 更新时间显示
                var elapsed = _operationStopwatch.Elapsed;
                string timeText = string.Format("时间：{0:00}:{1:00}", (int)elapsed.TotalMinutes, elapsed.Seconds);
                UpdateLabelSafe(_timeLabel, timeText);
                
                // 实时速度计算 - 基于当前步骤的实际传输字节
                long bytesDelta = currentStepTransferred - _lastBytes;
                double timeDelta = (DateTime.Now - _lastSpeedUpdate).TotalSeconds;
                
                if (timeDelta >= 0.2 && bytesDelta > 0) // 每200ms更新一次
                {
                    double instantSpeed = bytesDelta / timeDelta;
                    // 指数移动平均平滑速度 (更重的历史权重以避免跳动)
                    _currentSpeed = (_currentSpeed > 0) ? (_currentSpeed * 0.7 + instantSpeed * 0.3) : instantSpeed;
                    _lastBytes = currentStepTransferred;
                    _lastSpeedUpdate = DateTime.Now;
                    
                    // 更新速度显示
                    UpdateSpeedDisplayInternal();
                }
            }
            
            // 更新操作标签（显示百分比）- 使用总操作字节数计算总进度
            if (_totalOperationBytes > 0)
            {
                long totalProcessed = _completedStepBytes + currentStepTransferred;
                double totalPercent = Math.Min(100, 100.0 * totalProcessed / _totalOperationBytes);
                UpdateProgressBarDirect(_progressBar, totalPercent);
                UpdateLabelSafe(_operationLabel, string.Format("{0} [{1:F2}%]", _currentOperationName, totalPercent));
            }
            else if (_totalSteps > 0)
            {
                // 基于步骤计算总进度
                double totalProgress = (_currentStep + percent / 100.0) / _totalSteps * 100.0;
                UpdateProgressBarDirect(_progressBar, totalProgress);
                UpdateLabelSafe(_operationLabel, string.Format("{0} [{1:F2}%]", _currentOperationName, totalProgress));
            }
        }
        
        /// <summary>
        /// 更新总进度条 (长) - 多步骤操作的总进度
        /// </summary>
        /// <param name="currentStep">当前步骤索引</param>
        /// <param name="totalSteps">总步骤数</param>
        /// <param name="completedBytes">已完成步骤的总字节数</param>
        /// <param name="currentStepBytes">当前步骤的字节数 (用于准确速度计算)</param>
        public void UpdateTotalProgress(int currentStep, int totalSteps, long completedBytes = 0, long currentStepBytes = 0)
        {
            _currentStep = currentStep;
            _totalSteps = totalSteps;
            _completedStepBytes = completedBytes;
            _currentStepBytes = currentStepBytes;
            
            // 重置速度计算变量（新步骤开始）
            _lastBytes = 0;
            _lastSpeedUpdate = DateTime.Now;
            UpdateProgressBarDirect(_subProgressBar, 0);
        }
        
        /// <summary>
        /// 轻量级进度报告器（不捕获 SynchronizationContext，不阻塞后台线程）
        /// </summary>
        private class LightweightProgress : IProgress<double>
        {
            private readonly Action<double> _handler;
            private DateTime _lastReport = DateTime.MinValue;
            
            public LightweightProgress(Action<double> handler)
            {
                _handler = handler;
            }
            
            public void Report(double value)
            {
                // 节流：最多每 100ms 报告一次
                var now = DateTime.Now;
                if ((now - _lastReport).TotalMilliseconds < 100) return;
                _lastReport = now;
                _handler?.Invoke(value);
            }
        }
        
        /// <summary>
        /// 异步更新 UI（非阻塞，fire-and-forget）
        /// 用于后台线程向 UI 线程发送更新请求
        /// </summary>
        private void UpdateUIAsync(Action action)
        {
            if (action == null) return;
            
            // 使用任意 UI 控件的 BeginInvoke（非阻塞）
            if (_progressBar != null)
            {
                try
                {
                    var ctrl = _progressBar as System.Windows.Forms.Control;
                    if (ctrl != null && ctrl.IsHandleCreated && !ctrl.IsDisposed)
                    {
                        ctrl.BeginInvoke(action);
                        return;
                    }
                }
                catch { }
            }
            
            // 备选：直接执行（可能在 UI 线程）
            try { action(); } catch { }
        }
        
        private void UpdateLabelSafe(dynamic label, string text)
        {
            if (label == null) return;
            try
            {
                if (label.InvokeRequired)
                    label.BeginInvoke(new Action(() => label.Text = text));
                else
                    label.Text = text;
            }
            catch { /* UI 标签更新失败可忽略 */ }
        }
        
        /// <summary>
        /// 开始计时 (单步操作)
        /// </summary>
        public void StartOperationTimer(string operationName)
        {
            StartOperationTimer(operationName, 0, 0, 0);
        }
        
        /// <summary>
        /// 开始计时 (多步操作)
        /// </summary>
        public void StartOperationTimer(string operationName, int totalSteps, int currentStep = 0, long totalBytes = 0)
        {
            _operationStopwatch = Stopwatch.StartNew();
            _lastBytes = 0;
            _lastSpeedUpdate = DateTime.Now;
            _currentSpeed = 0;
            _totalSteps = totalSteps;
            _currentStep = currentStep;
            _totalOperationBytes = totalBytes;
            _completedStepBytes = 0;
            _currentStepBytes = totalBytes; // 单文件操作时，当前步骤字节数 = 总字节数
            _currentOperationName = operationName;
            
            // 重置进度条缓存 (防闪烁)
            _lastProgressValue = -1;
            _lastSubProgressValue = -1;
            
            UpdateLabelSafe(_operationLabel, "当前操作：" + operationName);
            UpdateLabelSafe(_timeLabel, "时间：00:00");
            UpdateLabelSafe(_speedLabel, "速度：--");
            
            // 重置进度条为0 (使用高精度模式)
            UpdateProgressBarDirect(_progressBar, 0);
            UpdateProgressBarDirect(_subProgressBar, 0);
        }
        
        /// <summary>
        /// 重置子进度条 (单个操作开始前调用)
        /// </summary>
        public void ResetSubProgress()
        {
            _lastBytes = 0;
            _lastSpeedUpdate = DateTime.Now;
            _currentSpeed = 0;
            _lastSubProgressValue = -1; // 重置缓存，确保下次更新生效
            UpdateProgressBarDirect(_subProgressBar, 0);
        }
        
        /// <summary>
        /// 停止计时
        /// </summary>
        public void StopOperationTimer()
        {
            if (_operationStopwatch != null)
            {
                _operationStopwatch.Stop();
                _operationStopwatch = null;
            }
            _totalSteps = 0;
            _currentStep = 0;
            _currentSpeed = 0;
            UpdateLabelSafe(_operationLabel, "当前操作：完成");
            UpdateProgressBarDirect(_progressBar, 100);
            UpdateProgressBarDirect(_subProgressBar, 100);
        }
        
        /// <summary>
        /// 结束操作并释放端口 (操作完成后调用)
        /// </summary>
        /// <param name="releasePort">是否释放端口 (默认 true)</param>
        private void EndOperation(bool releasePort = true)
        {
            IsBusy = false;
            StopOperationTimer();
            
            // 释放端口让其他程序可以连接设备
            if (releasePort && _service != null)
            {
                _service.ReleasePort();
            }
        }
        
        /// <summary>
        /// 设置是否保持端口打开 (批量操作时使用)
        /// </summary>
        public void SetKeepPortOpen(bool keepOpen)
        {
            _service?.SetKeepPortOpen(keepOpen);
        }
        
        /// <summary>
        /// 重置所有进度显示
        /// </summary>
        public void ResetProgress()
        {
            _totalSteps = 0;
            _currentStep = 0;
            _lastBytes = 0;
            _currentSpeed = 0;
            _lastProgressValue = -1;    // 重置缓存
            _lastSubProgressValue = -1; // 重置缓存
            UpdateProgressBarDirect(_progressBar, 0);
            UpdateProgressBarDirect(_subProgressBar, 0);
            UpdateLabelSafe(_timeLabel, "时间：00:00");
            UpdateLabelSafe(_speedLabel, "速度：--");
            UpdateLabelSafe(_operationLabel, "当前操作：待命");
        }

        private void UpdatePartitionListView(List<PartitionInfo> partitions)
        {
            if (_partitionListView == null) return;
            if (_partitionListView.InvokeRequired)
            {
                _partitionListView.BeginInvoke(new Action(() => UpdatePartitionListView(partitions)));
                return;
            }

            _partitionListView.BeginUpdate();
            _partitionListView.Items.Clear();

            foreach (var p in partitions)
            {
                // 计算地址
                long startAddress = p.StartSector * p.SectorSize;
                long endSector = p.StartSector + p.NumSectors - 1;
                long endAddress = (endSector + 1) * p.SectorSize;

                // 列顺序: 分区, LUN, 大小, 起始扇区, 结束扇区, 扇区数, 起始地址, 结束地址, 文件路径
                var item = new ListViewItem(p.Name);                           // 分区
                item.SubItems.Add(p.Lun.ToString());                           // LUN
                item.SubItems.Add(p.FormattedSize);                            // 大小
                item.SubItems.Add(p.StartSector.ToString());                   // 起始扇区
                item.SubItems.Add(endSector.ToString());                       // 结束扇区
                item.SubItems.Add(p.NumSectors.ToString());                    // 扇区数
                item.SubItems.Add(string.Format("0x{0:X}", startAddress));     // 起始地址
                item.SubItems.Add(string.Format("0x{0:X}", endAddress));       // 结束地址
                item.SubItems.Add("");                                         // 文件路径 (GPT 读取时无文件)
                item.Tag = p;

                // 只有勾选"保护分区"时，敏感分区才显示灰色
                if (IsProtectPartitionsEnabled() && RawprogramParser.IsSensitivePartition(p.Name))
                    item.ForeColor = Color.Gray;

                _partitionListView.Items.Add(item);
            }

            _partitionListView.EndUpdate();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // 停止端口监控定时器
                StopPortMonitor();
                if (_portMonitorTimer != null)
                {
                    _portMonitorTimer.Dispose();
                    _portMonitorTimer = null;
                }
                
                CancelOperation();
                Disconnect();
                _disposed = true;
            }
        }
        /// <summary>
        /// 手动执行 VIP 认证 (基于 Digest 和 Signature)
        /// </summary>
        public async Task<bool> PerformVipAuthAsync(string digestPath, string signaturePath)
        {
            if (!await EnsureConnectedAsync()) return false;
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                StartOperationTimer("VIP 认证", 1, 0);
                Log("正在执行 OPLUS VIP 认证 (Digest + Sign)...", Color.Blue);

                bool success = await _service.PerformVipAuthManualAsync(digestPath, signaturePath, _cts.Token);
                
                UpdateTotalProgress(1, 1);

                if (success) Log("VIP 认证成功，高权限分区已解锁", Color.Green);
                else Log("VIP 认证失败，请检查文件是否匹配", Color.Red);

                return success;
            }
            catch (Exception ex)
            {
                Log("VIP 认证异常: " + ex.Message, Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
                StopOperationTimer();
            }
        }

        /// <summary>
        /// 获取 VIP 挑战码 (用于在线获取签名)
        /// </summary>
        public async Task<string> GetVipChallengeAsync()
        {
            if (!await EnsureConnectedAsync()) return null;
            return await _service.GetVipChallengeAsync(_cts?.Token ?? default(CancellationToken));
        }

        public bool IsVipDevice { get { return _service != null && _service.IsVipDevice; } }
        public string DeviceVendor { get { return _service != null ? QualcommDatabase.GetVendorByPkHash(_service.ChipInfo?.PkHash) : "Unknown"; } }

        public async Task<bool> FlashOplusSuperAsync(string firmwareRoot)
        {
            if (!await EnsureConnectedAsync()) return false;
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (!Directory.Exists(firmwareRoot)) { Log("固件目录不存在: " + firmwareRoot, Color.Red); return false; }

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                
                Log("[高通] 正在深度分析 OPLUS Super 布局...", Color.Blue);
                
                // 1. 获取 super 分区信息
                var superPart = _service.FindPartition("super");
                if (superPart == null)
                {
                    Log("未在设备上找到 super 分区", Color.Red);
                    return false;
                }

                // 2. 预分析任务以获取总字节数
                string activeSlot = _service.CurrentSlot;
                // 槽位未知或未定义时默认使用 a 槽位
                if (activeSlot == "nonexistent" || activeSlot == "undefined" || activeSlot == "unknown" || string.IsNullOrEmpty(activeSlot)) 
                    activeSlot = "a";
                
                string nvId = _currentDeviceInfo?.OplusNvId ?? "";
                long superPartitionSize = superPart.Size;
                
                Log(string.Format("[高通] Super 分区: 起始扇区={0}, 大小={1} MB", 
                    superPart.StartSector, superPartitionSize / 1024 / 1024), Color.Gray);
                
                var superManager = new SakuraEDL.Qualcomm.Services.OplusSuperFlashManager(s => Log(s, Color.Gray));
                var tasks = await superManager.PrepareSuperTasksAsync(
                    firmwareRoot, superPart.StartSector, (int)superPart.SectorSize, activeSlot, nvId, superPartitionSize);

                if (tasks.Count == 0)
                {
                    Log("未找到可用的 Super 逻辑分区镜像", Color.Red);
                    return false;
                }
                
                // 校验任务
                var validation = superManager.ValidateTasks(tasks, superPartitionSize, (int)superPart.SectorSize);
                if (!validation.IsValid)
                {
                    foreach (var err in validation.Errors)
                    {
                        Log("[MetaSuper] 错误: " + err, Color.Red);
                    }
                    Log("[高通] Super 刷写校验失败，已中止", Color.Red);
                    return false;
                }
                
                foreach (var warn in validation.Warnings)
                {
                    Log("[MetaSuper] 警告: " + warn, Color.Orange);
                }

                long totalBytes = tasks.Sum(t => t.SizeInBytes);

                StartOperationTimer("OPLUS Super 写入", 1, 0, totalBytes);
                Log(string.Format("[高通] 开始执行 OPLUS Super 拆解写入 (共 {0} 个镜像, 总计展开 {1:F2} MB)...", 
                    tasks.Count, totalBytes / 1024.0 / 1024.0), Color.Blue);

                var progress = new Progress<double>(p => UpdateSubProgressFromPercent(p));
                bool success = await _service.FlashOplusSuperAsync(firmwareRoot, nvId, progress, _cts.Token);

                UpdateTotalProgress(1, 1, totalBytes);

                if (success) Log("[高通] OPLUS Super 写入完成", Color.Green);
                else Log("[高通] OPLUS Super 写入失败", Color.Red);

                return success;
            }
            catch (Exception ex)
            {
                Log("OPLUS Super 写入异常: " + ex.Message, Color.Red);
                _logDetail?.Invoke("[MetaSuper] 异常详情: " + ex.ToString());
                return false;
            }
            finally
            {
                IsBusy = false;
                StopOperationTimer();
            }
        }
    }
}
