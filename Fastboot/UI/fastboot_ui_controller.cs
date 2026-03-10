// ============================================================================
// SakuraEDL - Fastboot UI Controller | Fastboot UI 控制器
// ============================================================================
// [ZH] Fastboot UI 控制器 - 管理 Fastboot 刷机界面交互
// [EN] Fastboot UI Controller - Manage Fastboot flashing interface
// [JA] Fastboot UIコントローラー - Fastbootフラッシュインターフェース管理
// [KO] Fastboot UI 컨트롤러 - Fastboot 플래싱 인터페이스 관리
// [RU] Контроллер UI Fastboot - Управление интерфейсом прошивки Fastboot
// [ES] Controlador UI Fastboot - Gestión de interfaz de flasheo Fastboot
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SakuraEDL.Fastboot.Common;
using SakuraEDL.Fastboot.Models;
using SakuraEDL.Fastboot.Payload;
using SakuraEDL.Fastboot.Services;
using SakuraEDL.Qualcomm.Common;

namespace SakuraEDL.Fastboot.UI
{
    /// <summary>
    /// Fastboot UI 控制器
    /// 负责连接 UI 控件与 Fastboot 服务
    /// </summary>
    public class FastbootUIController : IDisposable
    {
        private readonly Action<string, Color?> _log;
        private readonly Action<string> _logDetail;

        private FastbootService _service;
        private CancellationTokenSource _cts;
        private System.Windows.Forms.Timer _deviceRefreshTimer;
        private bool _disposed;

        // UI 控件绑定
        private dynamic _deviceComboBox;      // 设备选择下拉框（独立）
        private dynamic _partitionListView;   // 分区列表
        private dynamic _progressBar;         // 总进度条
        private dynamic _subProgressBar;      // 子进度条
        private dynamic _commandComboBox;     // 快捷命令下拉框
        private dynamic _payloadTextBox;      // Payload 路径
        private dynamic _outputPathTextBox;   // 输出路径

        // 设备信息标签 (右上角信息区域)
        private dynamic _brandLabel;          // 品牌
        private dynamic _chipLabel;           // 芯片/平台
        private dynamic _modelLabel;          // 设备型号
        private dynamic _serialLabel;         // 序列号
        private dynamic _storageLabel;        // 存储类型
        private dynamic _unlockLabel;         // 解锁状态
        private dynamic _slotLabel;           // 当前槽位

        // 时间/速度/操作状态标签
        private dynamic _timeLabel;           // 时间标签
        private dynamic _speedLabel;          // 速度标签
        private dynamic _operationLabel;      // 当前操作标签
        private dynamic _deviceCountLabel;    // 设备数量标签

        // Checkbox 控件
        private dynamic _autoRebootCheckbox;      // 自动重启
        private dynamic _switchSlotCheckbox;      // 切换A槽
        private dynamic _eraseGoogleLockCheckbox; // 擦除谷歌锁
        private dynamic _keepDataCheckbox;        // 保留数据
        private dynamic _fbdFlashCheckbox;        // FBD刷写
        private dynamic _unlockBlCheckbox;        // 解锁BL
        private dynamic _lockBlCheckbox;          // 锁定BL

        // 计时器和速度计算
        private Stopwatch _operationStopwatch;
        private long _lastBytes;
        private DateTime _lastSpeedUpdate;
        private double _currentSpeed; // 当前速度 (bytes/s)
        #pragma warning disable CS0414
        private long _totalOperationBytes;
        private long _completedBytes;
        #pragma warning restore CS0414
        private string _currentOperationName;
        
        // 多分区刷写进度跟踪
        private int _flashTotalPartitions;
        private int _flashCurrentPartitionIndex;
        
        // 进度更新节流
        private DateTime _lastProgressUpdate = DateTime.MinValue;
        private double _lastSubProgressValue = -1;
        private double _lastMainProgressValue = -1;
        private const int ProgressUpdateIntervalMs = 16; // 约60fps

        // 设备列表缓存
        private List<FastbootDeviceListItem> _cachedDevices = new List<FastbootDeviceListItem>();

        // Payload 服务
        private PayloadService _payloadService;
        private RemotePayloadService _remotePayloadService;

        // 状态
        public bool IsBusy { get; private set; }
        public bool IsConnected => _service?.IsConnected ?? false;
        public FastbootDeviceInfo DeviceInfo => _service?.DeviceInfo;
        public List<FastbootPartitionInfo> Partitions => _service?.DeviceInfo?.GetPartitions();
        public int DeviceCount => _cachedDevices?.Count ?? 0;
        
        // Payload 状态
        public bool IsPayloadLoaded => (_payloadService?.IsLoaded ?? false) || (_remotePayloadService?.IsLoaded ?? false);
        public IReadOnlyList<PayloadPartition> PayloadPartitions => _payloadService?.Partitions;
        public PayloadSummary PayloadSummary => _payloadService?.GetSummary();
        
        // 远程 Payload 状态
        public bool IsRemotePayloadLoaded => _remotePayloadService?.IsLoaded ?? false;
        public IReadOnlyList<RemotePayloadPartition> RemotePayloadPartitions => _remotePayloadService?.Partitions;
        public RemotePayloadSummary RemotePayloadSummary => _remotePayloadService?.GetSummary();

        // 事件
        public event EventHandler<bool> ConnectionStateChanged;
        public event EventHandler<List<FastbootPartitionInfo>> PartitionsLoaded;
        public event EventHandler<List<FastbootDeviceListItem>> DevicesRefreshed;
        public event EventHandler<PayloadSummary> PayloadLoaded;
        public event EventHandler<PayloadExtractProgress> PayloadExtractProgress;

        public FastbootUIController(Action<string, Color?> log, Action<string> logDetail = null)
        {
            _log = log ?? ((msg, color) => { });
            _logDetail = logDetail ?? (msg => { });

            // 初始化计时器
            _operationStopwatch = new Stopwatch();
            _lastSpeedUpdate = DateTime.Now;

            // 初始化设备刷新定时器
            _deviceRefreshTimer = new System.Windows.Forms.Timer();
            _deviceRefreshTimer.Interval = 2000; // 每 2 秒刷新一次
            _deviceRefreshTimer.Tick += async (s, e) => await RefreshDeviceListAsync();
        }

        #region 日志方法

        private void Log(string message, Color? color = null)
        {
            _log(message, color);
        }

        #endregion

        #region 控件绑定

        /// <summary>
        /// 绑定 UI 控件
        /// </summary>
        public void BindControls(
            object deviceComboBox = null,
            object partitionListView = null,
            object progressBar = null,
            object subProgressBar = null,
            object commandComboBox = null,
            object payloadTextBox = null,
            object outputPathTextBox = null,
            // 设备信息标签
            object brandLabel = null,
            object chipLabel = null,
            object modelLabel = null,
            object serialLabel = null,
            object storageLabel = null,
            object unlockLabel = null,
            object slotLabel = null,
            // 时间/速度/操作标签
            object timeLabel = null,
            object speedLabel = null,
            object operationLabel = null,
            object deviceCountLabel = null,
            // Checkbox 控件
            object autoRebootCheckbox = null,
            object switchSlotCheckbox = null,
            object eraseGoogleLockCheckbox = null,
            object keepDataCheckbox = null,
            object fbdFlashCheckbox = null,
            object unlockBlCheckbox = null,
            object lockBlCheckbox = null)
        {
            _deviceComboBox = deviceComboBox;
            _partitionListView = partitionListView;
            _progressBar = progressBar;
            _subProgressBar = subProgressBar;
            _commandComboBox = commandComboBox;
            _payloadTextBox = payloadTextBox;
            _outputPathTextBox = outputPathTextBox;

            // 设备信息标签
            _brandLabel = brandLabel;
            _chipLabel = chipLabel;
            _modelLabel = modelLabel;
            _serialLabel = serialLabel;
            _storageLabel = storageLabel;
            _unlockLabel = unlockLabel;
            _slotLabel = slotLabel;

            // 时间/速度/操作标签
            _timeLabel = timeLabel;
            _speedLabel = speedLabel;
            _operationLabel = operationLabel;
            _deviceCountLabel = deviceCountLabel;

            // Checkbox
            _autoRebootCheckbox = autoRebootCheckbox;
            _switchSlotCheckbox = switchSlotCheckbox;
            _eraseGoogleLockCheckbox = eraseGoogleLockCheckbox;
            _keepDataCheckbox = keepDataCheckbox;
            _fbdFlashCheckbox = fbdFlashCheckbox;
            _unlockBlCheckbox = unlockBlCheckbox;
            _lockBlCheckbox = lockBlCheckbox;

            // 初始化分区列表
            if (_partitionListView != null)
            {
                try
                {
                    _partitionListView.CheckBoxes = true;
                    _partitionListView.FullRowSelect = true;
                    _partitionListView.MultiSelect = true;
                }
                catch { }
            }

            // 初始化快捷命令下拉框
            InitializeCommandComboBox();

            // 初始化设备信息显示
            ResetDeviceInfoLabels();
        }

        /// <summary>
        /// 初始化快捷命令下拉框（自动补齐）
        /// </summary>
        private void InitializeCommandComboBox()
        {
            if (_commandComboBox == null) return;

            try
            {
                // 标准 Fastboot 命令列表
                var commands = new string[]
                {
                    // 设备信息
                    "devices",
                    "getvar all",
                    "getvar product",
                    "getvar serialno",
                    "getvar version",
                    "getvar secure",
                    "getvar unlocked",
                    "getvar current-slot",
                    "getvar slot-count",
                    "getvar max-download-size",
                    "getvar is-userspace",
                    "getvar hw-revision",
                    "getvar variant",
                    
                    // 重启命令
                    "reboot",
                    "reboot-bootloader",
                    "reboot-recovery",
                    "reboot-fastboot",
                    
                    // 解锁/锁定
                    "flashing unlock",
                    "flashing lock",
                    "flashing unlock_critical",
                    "flashing get_unlock_ability",
                    
                    // 槽位操作
                    "set_active a",
                    "set_active b",
                    
                    // OEM 命令
                    "oem device-info",
                    "oem unlock",
                    "oem lock",
                    "oem get_unlock_ability",
                    
                    // 擦除
                    "erase frp",
                    "erase userdata",
                    "erase cache",
                    "erase metadata",
                };

                // 设置下拉框数据源
                _commandComboBox.Items.Clear();
                foreach (var cmd in commands)
                {
                    _commandComboBox.Items.Add(cmd);
                }

                // 设置自动补齐
                _commandComboBox.AutoCompleteMode = System.Windows.Forms.AutoCompleteMode.SuggestAppend;
                _commandComboBox.AutoCompleteSource = System.Windows.Forms.AutoCompleteSource.ListItems;
            }
            catch { }
        }

        /// <summary>
        /// 重置设备信息标签为默认值
        /// </summary>
        public void ResetDeviceInfoLabels()
        {
            UpdateLabelSafe(_brandLabel, "品牌：等待连接");
            UpdateLabelSafe(_chipLabel, "芯片：等待连接");
            UpdateLabelSafe(_modelLabel, "型号：等待连接");
            UpdateLabelSafe(_serialLabel, "序列号：等待连接");
            UpdateLabelSafe(_storageLabel, "存储：等待连接");
            UpdateLabelSafe(_unlockLabel, "解锁：等待连接");
            UpdateLabelSafe(_slotLabel, "槽位：等待连接");
            UpdateLabelSafe(_timeLabel, "时间：00:00");
            UpdateLabelSafe(_speedLabel, "速度：0 KB/s");
            UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            UpdateLabelSafe(_deviceCountLabel, "FB设备：0");
        }

        /// <summary>
        /// 更新设备信息标签
        /// </summary>
        public void UpdateDeviceInfoLabels()
        {
            if (DeviceInfo == null)
            {
                ResetDeviceInfoLabels();
                return;
            }

            // 品牌/厂商
            string brand = DeviceInfo.GetVariable("ro.product.brand") 
                ?? DeviceInfo.GetVariable("manufacturer") 
                ?? "未知";
            UpdateLabelSafe(_brandLabel, $"品牌：{brand}");

            // 芯片/平台 - 优先使用 variant，然后映射 hw-revision
            string chip = DeviceInfo.GetVariable("variant");
            if (string.IsNullOrEmpty(chip) || chip == "未知")
            {
                string hwRev = DeviceInfo.GetVariable("hw-revision");
                chip = MapChipId(hwRev);
            }
            if (string.IsNullOrEmpty(chip) || chip == "未知")
            {
                chip = DeviceInfo.GetVariable("ro.boot.hardware") ?? "未知";
            }
            UpdateLabelSafe(_chipLabel, $"芯片：{chip}");

            // 型号
            string model = DeviceInfo.GetVariable("product") 
                ?? DeviceInfo.GetVariable("ro.product.model") 
                ?? "未知";
            UpdateLabelSafe(_modelLabel, $"型号：{model}");

            // 序列号
            string serial = DeviceInfo.Serial ?? "未知";
            UpdateLabelSafe(_serialLabel, $"序列号：{serial}");

            // 存储类型
            string storage = DeviceInfo.GetVariable("partition-type:userdata") ?? "未知";
            if (storage.Contains("ext4") || storage.Contains("f2fs"))
                storage = "eMMC/UFS";
            UpdateLabelSafe(_storageLabel, $"存储：{storage}");

            // 解锁状态
            string unlocked = DeviceInfo.GetVariable("unlocked");
            string secureState = DeviceInfo.GetVariable("secure");
            string unlockStatus = "未知";
            if (!string.IsNullOrEmpty(unlocked))
            {
                unlockStatus = unlocked.ToLower() == "yes" || unlocked == "1" ? "已解锁" : "已锁定";
            }
            else if (!string.IsNullOrEmpty(secureState))
            {
                unlockStatus = secureState.ToLower() == "no" || secureState == "0" ? "已解锁" : "已锁定";
            }
            UpdateLabelSafe(_unlockLabel, $"解锁：{unlockStatus}");

            // 当前槽位 - 支持多种变量名
            string slot = DeviceInfo.GetVariable("current-slot") 
                ?? DeviceInfo.CurrentSlot;
            string slotCount = DeviceInfo.GetVariable("slot-count");
            
            if (string.IsNullOrEmpty(slot))
            {
                // 检查是否支持 A/B 分区
                if (!string.IsNullOrEmpty(slotCount) && slotCount != "0")
                    slot = "未知";
                else
                    slot = "N/A";
            }
            else if (!slot.StartsWith("_"))
            {
                slot = "_" + slot;
            }
            UpdateLabelSafe(_slotLabel, $"槽位：{slot}");
        }

        /// <summary>
        /// 映射高通芯片 ID 到名称
        /// </summary>
        private string MapChipId(string hwRevision)
        {
            if (string.IsNullOrEmpty(hwRevision)) return "未知";
            
            // 高通芯片 ID 映射表
            var chipMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Snapdragon 8xx 系列
                { "20001", "SDM845" },
                { "20002", "SDM845" },
                { "339", "SDM845" },
                { "321", "SDM835" },
                { "318", "SDM835" },
                { "360", "SDM855" },
                { "356", "SM8150" },
                { "415", "SM8250" },
                { "457", "SM8350" },
                { "530", "SM8450" },
                { "536", "SM8550" },
                { "591", "SM8650" },
                
                // Snapdragon 7xx 系列
                { "365", "SDM730" },
                { "366", "SDM730G" },
                { "400", "SDM765G" },
                { "434", "SM7250" },
                { "475", "SM7325" },
                
                // Snapdragon 6xx 系列
                { "317", "SDM660" },
                { "324", "SDM670" },
                { "345", "SDM675" },
                { "355", "SDM690" },
                
                // Snapdragon 4xx 系列
                { "293", "SDM450" },
                { "353", "SM4250" },
                
                // MTK 系列
                { "mt6893", "Dimensity 1200" },
                { "mt6885", "Dimensity 1000+" },
                { "mt6853", "Dimensity 720" },
                { "mt6873", "Dimensity 800" },
                { "mt6983", "Dimensity 9000" },
                { "mt6895", "Dimensity 8100" },
            };
            
            if (chipMap.TryGetValue(hwRevision, out string chipName))
                return chipName;
            
            // 如果映射表中没有，检查是否是纯数字（可能是未知的高通 ID）
            if (int.TryParse(hwRevision, out _))
                return $"QC-{hwRevision}";
            
            return hwRevision;
        }

        /// <summary>
        /// 安全更新 Label 文本
        /// </summary>
        private void UpdateLabelSafe(dynamic label, string text)
        {
            if (label == null) return;

            try
            {
                if (label.InvokeRequired)
                {
                    label.BeginInvoke(new Action(() =>
                    {
                        try { label.Text = text; } catch { }
                    }));
                }
                else
                {
                    label.Text = text;
                }
            }
            catch { }
        }

        /// <summary>
        /// 启动设备监控
        /// </summary>
        public void StartDeviceMonitoring()
        {
            _deviceRefreshTimer.Start();
            Task.Run(() => RefreshDeviceListAsync());
        }

        /// <summary>
        /// 停止设备监控
        /// </summary>
        public void StopDeviceMonitoring()
        {
            _deviceRefreshTimer.Stop();
        }

        #endregion

        #region 设备操作

        /// <summary>
        /// 刷新设备列表
        /// </summary>
        public async Task RefreshDeviceListAsync()
        {
            try
            {
                using (var tempService = new FastbootService(msg => { }))
                {
                    var devices = await tempService.GetDevicesAsync();
                    _cachedDevices = devices ?? new List<FastbootDeviceListItem>();
                    
                    // 在 UI 线程更新
                    if (_deviceComboBox != null)
                    {
                        try
                        {
                            if (_deviceComboBox.InvokeRequired)
                            {
                                _deviceComboBox.BeginInvoke(new Action(() => UpdateDeviceComboBox(devices)));
                            }
                            else
                            {
                                UpdateDeviceComboBox(devices);
                            }
                        }
                        catch { }
                    }

                    // 更新设备数量显示
                    UpdateDeviceCountLabel();

                    DevicesRefreshed?.Invoke(this, devices);
                }
            }
            catch (Exception ex)
            {
                _logDetail($"[Fastboot] 刷新设备列表异常: {ex.Message}");
            }
        }

        private void UpdateDeviceComboBox(List<FastbootDeviceListItem> devices)
        {
            if (_deviceComboBox == null) return;

            try
            {
                string currentSelection = null;
                try { currentSelection = _deviceComboBox.SelectedItem?.ToString(); } catch { }

                _deviceComboBox.Items.Clear();
                foreach (var device in devices)
                {
                    _deviceComboBox.Items.Add(device.ToString());
                }

                // 尝试恢复之前的选择
                if (!string.IsNullOrEmpty(currentSelection) && _deviceComboBox.Items.Contains(currentSelection))
                {
                    _deviceComboBox.SelectedItem = currentSelection;
                }
                else if (_deviceComboBox.Items.Count > 0)
                {
                    _deviceComboBox.SelectedIndex = 0;
                }
            }
            catch { }
        }

        /// <summary>
        /// 更新设备数量显示标签
        /// </summary>
        private void UpdateDeviceCountLabel()
        {
            int count = _cachedDevices?.Count ?? 0;
            string text = count == 0 ? "FB设备：0" 
                : count == 1 ? $"FB设备：{_cachedDevices[0].Serial}" 
                : $"FB设备：{count}个";
            
            UpdateLabelSafe(_deviceCountLabel, text);
        }

        /// <summary>
        /// 连接选中的设备
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            if (IsBusy)
            {
                Log("操作进行中", Color.Orange);
                return false;
            }

            string selectedDevice = GetSelectedDevice();
            if (string.IsNullOrEmpty(selectedDevice))
            {
                Log("请选择 Fastboot 设备", Color.Red);
                return false;
            }

            // 从 "serial (status)" 格式提取序列号
            string serial = selectedDevice.Split(' ')[0];

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("连接设备");
                UpdateProgressBar(0);
                UpdateLabelSafe(_operationLabel, "当前操作：连接设备");

                _service = new FastbootService(
                    msg => Log(msg, null),
                    (current, total) => UpdateProgressWithSpeed(current, total),
                    _logDetail
                );
                
                // 订阅刷写进度事件
                _service.FlashProgressChanged += OnFlashProgressChanged;

                UpdateProgressBar(30);
                bool success = await _service.SelectDeviceAsync(serial, _cts.Token);

                if (success)
                {
                    UpdateProgressBar(70);
                    Log("Fastboot 设备连接成功", Color.Green);
                    
                    // 更新设备信息标签
                    UpdateDeviceInfoLabels();
                    
                    // 更新分区列表
                    UpdatePartitionListView();
                    
                    UpdateProgressBar(100);
                    ConnectionStateChanged?.Invoke(this, true);
                    PartitionsLoaded?.Invoke(this, Partitions);
                }
                else
                {
                    Log("Fastboot 设备连接失败", Color.Red);
                    ResetDeviceInfoLabels();
                    UpdateProgressBar(0);
                }

                StopOperationTimer();
                return success;
            }
            catch (Exception ex)
            {
                Log($"连接异常: {ex.Message}", Color.Red);
                ResetDeviceInfoLabels();
                UpdateProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            _service?.Disconnect();
            ResetDeviceInfoLabels();
            ConnectionStateChanged?.Invoke(this, false);
        }

        private string GetSelectedDevice()
        {
            try
            {
                if (_deviceComboBox == null) return null;
                return _deviceComboBox.SelectedItem?.ToString();
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region 分区操作

        /// <summary>
        /// 读取分区表（刷新设备信息）
        /// </summary>
        public async Task<bool> ReadPartitionTableAsync()
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (!await EnsureConnectedAsync()) return false;

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("读取分区表");
                UpdateProgressBar(0);
                UpdateLabelSafe(_operationLabel, "当前操作：读取分区表");
                UpdateLabelSafe(_speedLabel, "速度：读取中...");

                Log("正在读取 Fastboot 分区表...", Color.Blue);

                UpdateProgressBar(30);
                bool success = await _service.RefreshDeviceInfoAsync(_cts.Token);

                if (success)
                {
                    UpdateProgressBar(70);
                    
                    // 更新设备信息标签
                    UpdateDeviceInfoLabels();
                    
                    UpdatePartitionListView();
                    UpdateProgressBar(100);
                    
                    Log($"成功读取 {Partitions?.Count ?? 0} 个分区", Color.Green);
                    PartitionsLoaded?.Invoke(this, Partitions);
                }
                else
                {
                    UpdateProgressBar(0);
                }

                StopOperationTimer();
                return success;
            }
            catch (Exception ex)
            {
                Log($"读取分区表失败: {ex.Message}", Color.Red);
                UpdateProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 更新分区列表视图
        /// </summary>
        private void UpdatePartitionListView()
        {
            if (_partitionListView == null || Partitions == null) return;

            try
            {
                if (_partitionListView.InvokeRequired)
                {
                    _partitionListView.BeginInvoke(new Action(UpdatePartitionListViewInternal));
                }
                else
                {
                    UpdatePartitionListViewInternal();
                }
            }
            catch { }
        }

        private void UpdatePartitionListViewInternal()
        {
            try
            {
                _partitionListView.Items.Clear();

                foreach (var part in Partitions)
                {
                    var item = new ListViewItem(new string[]
                    {
                        part.Name,
                        "-",  // 操作列
                        part.SizeFormatted,
                        part.IsLogicalText
                    });
                    item.Tag = part;
                    _partitionListView.Items.Add(item);
                }
            }
            catch { }
        }

        /// <summary>
        /// 刷写选中的分区
        /// </summary>
        public async Task<bool> FlashSelectedPartitionsAsync()
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (!await EnsureConnectedAsync()) return false;

            var selectedItems = GetSelectedPartitionItems();
            if (selectedItems.Count == 0)
            {
                Log("请选择要刷写的分区", Color.Orange);
                return false;
            }

            // 检查是否有镜像文件
            var partitionsWithFiles = new List<Tuple<string, string>>();
            foreach (ListViewItem item in selectedItems)
            {
                string partName = item.SubItems[0].Text;
                string filePath = item.SubItems.Count > 3 ? item.SubItems[3].Text : "";
                
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    Log($"分区 {partName} 没有选择镜像文件", Color.Orange);
                    continue;
                }

                partitionsWithFiles.Add(Tuple.Create(partName, filePath));
            }

            if (partitionsWithFiles.Count == 0)
            {
                Log("没有可刷写的分区（请双击分区选择镜像文件）", Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("刷写分区");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                UpdateLabelSafe(_operationLabel, $"当前操作：刷写 {partitionsWithFiles.Count} 个分区");
                UpdateLabelSafe(_speedLabel, "速度：计算中...");

                Log($"开始刷写 {partitionsWithFiles.Count} 个分区...", Color.Blue);

                int successCount = 0;
                int total = partitionsWithFiles.Count;
                
                // 设置进度跟踪字段
                _flashTotalPartitions = total;

                for (int i = 0; i < total; i++)
                {
                    _flashCurrentPartitionIndex = i;
                    
                    var part = partitionsWithFiles[i];
                    UpdateLabelSafe(_operationLabel, $"当前操作：刷写 {part.Item1} ({i + 1}/{total})");
                    // 子进度：当前分区刷写开始
                    UpdateSubProgressBar(0);

                    var flashStart = DateTime.Now;
                    var fileSize = new FileInfo(part.Item2).Length;
                    
                    bool result = await _service.FlashPartitionAsync(part.Item1, part.Item2, false, _cts.Token);
                    
                    // 子进度：当前分区刷写完成
                    UpdateSubProgressBar(100);
                    // 更新总进度
                    UpdateProgressBar(((i + 1) * 100.0) / total);
                    
                    // 计算并显示速度
                    var elapsed = (DateTime.Now - flashStart).TotalSeconds;
                    if (elapsed > 0)
                    {
                        double speed = fileSize / elapsed;
                        UpdateSpeedLabel(FormatSpeed(speed));
                    }
                    
                    if (result)
                        successCount++;
                }
                
                // 重置进度跟踪
                _flashTotalPartitions = 0;
                _flashCurrentPartitionIndex = 0;

                UpdateProgressBar(100);
                UpdateSubProgressBar(100);
                StopOperationTimer();

                Log($"刷写完成: {successCount}/{total} 成功", 
                    successCount == total ? Color.Green : Color.Orange);

                // 执行刷写后附加操作（切换槽位、擦除谷歌锁等）
                if (successCount > 0)
                {
                    await ExecutePostFlashOperationsAsync();
                }

                // 自动重启
                if (IsAutoRebootEnabled() && successCount > 0)
                {
                    await _service.RebootAsync(_cts.Token);
                }

                return successCount == total;
            }
            catch (Exception ex)
            {
                Log($"刷写失败: {ex.Message}", Color.Red);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 擦除选中的分区
        /// </summary>
        public async Task<bool> EraseSelectedPartitionsAsync()
        {
            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (!await EnsureConnectedAsync()) return false;

            var selectedItems = GetSelectedPartitionItems();
            if (selectedItems.Count == 0)
            {
                Log("请选择要擦除的分区", Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("擦除分区");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                UpdateLabelSafe(_speedLabel, "速度：擦除中...");

                int success = 0;
                int total = selectedItems.Count;
                int current = 0;

                Log($"开始擦除 {total} 个分区...", Color.Blue);

                foreach (ListViewItem item in selectedItems)
                {
                    string partName = item.SubItems[0].Text;
                    UpdateLabelSafe(_operationLabel, $"当前操作：擦除 {partName} ({current + 1}/{total})");
                    // 总进度：基于已完成的分区数
                    UpdateProgressBar((current * 100.0) / total);
                    // 子进度：开始擦除
                    UpdateSubProgressBar(0);
                    
                    if (await _service.ErasePartitionAsync(partName, _cts.Token))
                    {
                        success++;
                    }
                    
                    // 子进度：当前分区擦除完成
                    UpdateSubProgressBar(100);
                    current++;
                }

                UpdateProgressBar(100);
                UpdateSubProgressBar(100);
                StopOperationTimer();

                Log($"擦除完成: {success}/{total} 成功", 
                    success == total ? Color.Green : Color.Orange);

                return success == total;
            }
            catch (Exception ex)
            {
                Log($"擦除失败: {ex.Message}", Color.Red);
                UpdateProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        private List<ListViewItem> GetSelectedPartitionItems()
        {
            var items = new List<ListViewItem>();
            if (_partitionListView == null) return items;

            try
            {
                foreach (ListViewItem item in _partitionListView.CheckedItems)
                {
                    items.Add(item);
                }
            }
            catch { }

            return items;
        }

        /// <summary>
        /// 检查是否有勾选的普通分区（非脚本任务，带镜像文件）
        /// </summary>
        public bool HasSelectedPartitionsWithFiles()
        {
            if (_partitionListView == null) return false;

            try
            {
                foreach (ListViewItem item in _partitionListView.CheckedItems)
                {
                    // 跳过脚本任务和 Payload 分区
                    if (item.Tag is BatScriptParser.FlashTask) continue;
                    if (item.Tag is PayloadPartition) continue;
                    if (item.Tag is RemotePayloadPartition) continue;

                    // 检查是否有镜像文件路径
                    string filePath = item.SubItems.Count > 3 ? item.SubItems[3].Text : "";
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 为分区选择镜像文件
        /// </summary>
        public void SelectImageForPartition(ListViewItem item)
        {
            if (item == null) return;

            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = $"选择 {item.SubItems[0].Text} 分区镜像";
                ofd.Filter = "镜像文件|*.img;*.bin;*.mbn|所有文件|*.*";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    // 更新操作列和文件路径列
                    if (item.SubItems.Count > 1)
                        item.SubItems[1].Text = "写入";
                    if (item.SubItems.Count > 3)
                        item.SubItems[3].Text = ofd.FileName;
                    else
                    {
                        while (item.SubItems.Count < 4)
                            item.SubItems.Add("");
                        item.SubItems[3].Text = ofd.FileName;
                    }

                    item.Checked = true;
                    Log($"已选择镜像: {Path.GetFileName(ofd.FileName)} -> {item.SubItems[0].Text}", Color.Blue);
                }
            }
        }

        /// <summary>
        /// 加载已提取的文件夹 (包含 .img 文件)
        /// 自动识别分区名并添加到列表，解析设备信息
        /// </summary>
        public void LoadExtractedFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                Log("文件夹不存在", Color.Red);
                return;
            }

            if (_partitionListView == null)
            {
                Log("分区列表未初始化", Color.Red);
                return;
            }

            // 扫描文件夹中的所有 .img 文件
            var imgFiles = Directory.GetFiles(folderPath, "*.img", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileNameWithoutExtension(f))
                .ToList();

            if (imgFiles.Count == 0)
            {
                Log($"文件夹中没有找到 .img 文件: {folderPath}", Color.Orange);
                return;
            }

            Log($"扫描到 {imgFiles.Count} 个镜像文件", Color.Blue);

            // 清空现有列表
            _partitionListView.Items.Clear();

            int addedCount = 0;
            long totalSize = 0;

            foreach (var imgPath in imgFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(imgPath);
                var fileInfo = new FileInfo(imgPath);
                
                // 判断分区类型
                bool isLogical = FastbootService.IsLogicalPartition(fileName);
                bool isModem = FastbootService.IsModemPartition(fileName);
                string partType = isLogical ? "逻辑" : (isModem ? "Modem" : "物理");

                // 创建列表项 (列顺序: 分区名、操作、大小、类型、文件路径)
                var item = new ListViewItem(new[]
                {
                    fileName,                           // 分区名
                    "flash",                            // 操作
                    FormatSize(fileInfo.Length),       // 大小
                    partType,                           // 类型
                    imgPath                             // 文件路径
                });

                // 存储文件信息到 Tag
                item.Tag = new ExtractedImageInfo
                {
                    PartitionName = fileName,
                    FilePath = imgPath,
                    FileSize = fileInfo.Length,
                    IsLogical = isLogical,
                    IsModem = isModem
                };

                item.Checked = true;  // 默认全选
                _partitionListView.Items.Add(item);
                addedCount++;
                totalSize += fileInfo.Length;
            }

            Log($"已加载 {addedCount} 个分区，总大小: {FormatSize(totalSize)}", Color.Green);
            Log($"来源文件夹: {folderPath}", Color.Blue);

            // 异步解析固件信息
            int partitionCount = addedCount;
            long size = totalSize;
            _ = Task.Run(async () =>
            {
                try
                {
                    Log("正在解析固件信息...", Color.Blue);
                    CurrentFirmwareInfo = await ParseFirmwareInfoAsync(folderPath);
                    CurrentFirmwareInfo.TotalPartitions = partitionCount;
                    CurrentFirmwareInfo.TotalSize = size;

                    // 在 UI 线程显示解析结果
                    if (_partitionListView?.InvokeRequired == true)
                    {
                        _partitionListView.Invoke(new Action(() => DisplayFirmwareInfo()));
                    }
                    else
                    {
                        DisplayFirmwareInfo();
                    }
                }
                catch (Exception ex)
                {
                    Log($"固件信息解析失败: {ex.Message}", Color.Red);
                    _logDetail($"固件信息解析异常: {ex}");
                }
            });
        }

        /// <summary>
        /// 显示固件信息
        /// </summary>
        private void DisplayFirmwareInfo()
        {
            if (CurrentFirmwareInfo == null)
            {
                Log("固件信息: 未找到 metadata 文件，无法识别设备信息", Color.Orange);
                return;
            }

            var info = CurrentFirmwareInfo;
            var sb = new System.Text.StringBuilder();
            sb.Append("固件信息: ");
            bool hasInfo = false;

            // 显示型号
            if (!string.IsNullOrEmpty(info.DeviceModel))
            {
                sb.Append($"型号={info.DeviceModel} ");
                hasInfo = true;
            }

            // 显示设备代号
            if (!string.IsNullOrEmpty(info.DeviceName))
            {
                sb.Append($"代号={info.DeviceName} ");
                hasInfo = true;
            }

            // Android 版本
            if (!string.IsNullOrEmpty(info.AndroidVersion))
            {
                sb.Append($"Android={info.AndroidVersion} ");
                hasInfo = true;
            }

            // OS 版本
            if (!string.IsNullOrEmpty(info.OsVersion))
            {
                sb.Append($"OS={info.OsVersion} ");
                hasInfo = true;
            }

            // 安全补丁
            if (!string.IsNullOrEmpty(info.SecurityPatch))
            {
                sb.Append($"安全补丁={info.SecurityPatch} ");
                hasInfo = true;
            }

            // OTA 类型
            if (!string.IsNullOrEmpty(info.OtaType))
            {
                sb.Append($"类型={info.OtaType} ");
                hasInfo = true;
            }

            if (hasInfo)
            {
                Log(sb.ToString().Trim(), Color.Cyan);
                
                // 如果有版本名，单独显示一行
                if (!string.IsNullOrEmpty(info.BuildNumber))
                {
                    Log($"版本: {info.BuildNumber}", Color.Gray);
                }
            }
            else
            {
                Log("固件信息: 未找到 metadata 文件", Color.Orange);
            }
        }

        /// <summary>
        /// 验证所有分区文件的哈希值
        /// </summary>
        public async Task<bool> VerifyPartitionHashesAsync(CancellationToken ct = default)
        {
            if (_partitionListView == null || _partitionListView.Items.Count == 0)
            {
                Log("没有分区可验证", Color.Orange);
                return false;
            }

            Log("开始验证分区完整性...", Color.Blue);
            UpdateLabelSafe(_operationLabel, "当前操作：验证文件");

            int total = _partitionListView.CheckedItems.Count;
            int current = 0;
            int verified = 0;
            int failed = 0;

            foreach (ListViewItem item in _partitionListView.CheckedItems)
            {
                ct.ThrowIfCancellationRequested();

                if (item.Tag is ExtractedImageInfo info && !string.IsNullOrEmpty(info.FilePath))
                {
                    current++;
                    UpdateProgressBar(current * 100.0 / total);
                    UpdateLabelSafe(_operationLabel, $"验证: {info.PartitionName} ({current}/{total})");

                    // 检查文件是否存在
                    if (!File.Exists(info.FilePath))
                    {
                        Log($"  ✗ {info.PartitionName}: 文件不存在", Color.Red);
                        failed++;
                        continue;
                    }

                    // 检查文件大小
                    var fileInfo = new FileInfo(info.FilePath);
                    if (fileInfo.Length != info.FileSize)
                    {
                        Log($"  ✗ {info.PartitionName}: 大小不匹配 (期望={FormatSize(info.FileSize)}, 实际={FormatSize(fileInfo.Length)})", Color.Red);
                        failed++;
                        continue;
                    }

                    // 计算 MD5 哈希
                    string hash = await Task.Run(() => CalculateMd5(info.FilePath), ct);
                    if (!string.IsNullOrEmpty(hash))
                    {
                        info.Md5Hash = hash;
                        info.HashVerified = true;
                        verified++;
                        _logDetail($"  ✓ {info.PartitionName}: MD5={hash}");
                    }
                    else
                    {
                        failed++;
                        Log($"  ✗ {info.PartitionName}: 哈希计算失败", Color.Red);
                    }
                }
            }

            UpdateProgressBar(100);
            UpdateLabelSafe(_operationLabel, "当前操作：空闲");

            if (failed == 0)
            {
                Log($"✓ 验证完成: {verified} 个分区全部通过", Color.Green);
                return true;
            }
            else
            {
                Log($"⚠ 验证完成: {verified} 通过, {failed} 失败", Color.Orange);
                return false;
            }
        }

        /// <summary>
        /// 已提取镜像文件信息
        /// </summary>
        public class ExtractedImageInfo
        {
            public string PartitionName { get; set; }
            public string FilePath { get; set; }
            public long FileSize { get; set; }
            public bool IsLogical { get; set; }
            public bool IsModem { get; set; }
            public string Md5Hash { get; set; }
            public string Sha256Hash { get; set; }
            public bool HashVerified { get; set; }
        }

        /// <summary>
        /// 固件包信息 (从 metadata 解析)
        /// </summary>
        public class FirmwareInfo
        {
            public string DeviceModel { get; set; }       // 设备型号 (如 PJD110)
            public string DeviceName { get; set; }        // 设备代号 (如 OP5929L1)
            public string AndroidVersion { get; set; }    // Android 版本
            public string OsVersion { get; set; }         // OS 版本 (ColorOS/OxygenOS)
            public string BuildNumber { get; set; }       // 构建号/版本名
            public string SecurityPatch { get; set; }     // 安全补丁日期
            public string Fingerprint { get; set; }       // 完整指纹
            public string BasebandVersion { get; set; }   // 基带版本
            public string OtaType { get; set; }           // OTA 类型 (AB/非AB)
            public string FolderPath { get; set; }        // 来源文件夹
            public int TotalPartitions { get; set; }      // 分区总数
            public long TotalSize { get; set; }           // 总大小
        }

        /// <summary>
        /// 当前加载的固件信息
        /// </summary>
        public FirmwareInfo CurrentFirmwareInfo { get; private set; }

        /// <summary>
        /// 计算文件的 MD5 哈希值
        /// </summary>
        private string CalculateMd5(string filePath)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 计算文件的 SHA256 哈希值
        /// </summary>
        private string CalculateSha256(string filePath)
        {
            try
            {
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 异步计算所有分区的哈希值
        /// </summary>
        public async Task CalculatePartitionHashesAsync(CancellationToken ct = default)
        {
            if (_partitionListView == null) return;

            Log("开始计算分区哈希值...", Color.Blue);
            UpdateLabelSafe(_operationLabel, "当前操作：计算哈希");
            int total = _partitionListView.Items.Count;
            int current = 0;

            foreach (ListViewItem item in _partitionListView.Items)
            {
                ct.ThrowIfCancellationRequested();
                
                if (item.Tag is ExtractedImageInfo info && !string.IsNullOrEmpty(info.FilePath))
                {
                    current++;
                    UpdateProgressBar(current * 100.0 / total);
                    UpdateLabelSafe(_operationLabel, $"计算哈希: {info.PartitionName} ({current}/{total})");

                    // 在后台线程计算哈希
                    info.Md5Hash = await Task.Run(() => CalculateMd5(info.FilePath), ct);
                    info.HashVerified = !string.IsNullOrEmpty(info.Md5Hash);
                }
            }

            UpdateProgressBar(100);
            UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            Log($"哈希计算完成，共 {total} 个分区", Color.Green);
        }

        /// <summary>
        /// 从固件文件夹解析设备信息
        /// 优先从 META-INF/com/android/metadata 读取
        /// </summary>
        private async Task<FirmwareInfo> ParseFirmwareInfoAsync(string folderPath)
        {
            var info = new FirmwareInfo { FolderPath = folderPath };

            try
            {
                // 获取父目录（固件包根目录）
                string parentDir = Directory.GetParent(folderPath)?.FullName ?? folderPath;

                // 1. 优先从 META-INF/com/android/metadata 读取 (标准 OTA 包格式)
                string[] metadataPaths = {
                    Path.Combine(parentDir, "META-INF", "com", "android", "metadata"),
                    Path.Combine(folderPath, "META-INF", "com", "android", "metadata"),
                    Path.Combine(parentDir, "metadata"),
                    Path.Combine(folderPath, "metadata")
                };

                foreach (var metaPath in metadataPaths)
                {
                    if (File.Exists(metaPath))
                    {
                        _logDetail($"从 metadata 文件解析: {metaPath}");
                        await ParseMetadataFileAsync(metaPath, info);
                        if (!string.IsNullOrEmpty(info.DeviceName) || !string.IsNullOrEmpty(info.OsVersion))
                            break;
                    }
                }

                // 2. 从 payload_properties.txt 读取补充信息
                string[] propPaths = {
                    Path.Combine(parentDir, "payload_properties.txt"),
                    Path.Combine(folderPath, "payload_properties.txt")
                };

                foreach (var propPath in propPaths)
                {
                    if (File.Exists(propPath))
                    {
                        _logDetail($"从 payload_properties.txt 解析: {propPath}");
                        await ParsePayloadPropertiesAsync(propPath, info);
                        break;
                    }
                }

                // 3. 尝试从 META 文件夹读取信息 (OPLUS 固件)
                string metaDir = Path.Combine(folderPath, "META");
                if (!Directory.Exists(metaDir))
                    metaDir = Path.Combine(parentDir, "META");

                if (Directory.Exists(metaDir))
                {
                    string miscInfo = Path.Combine(metaDir, "misc_info.txt");
                    if (File.Exists(miscInfo))
                    {
                        var lines = await Task.Run(() => File.ReadAllLines(miscInfo));
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("build_fingerprint="))
                                info.Fingerprint = info.Fingerprint ?? line.Substring(18);
                        }
                    }
                }

                // 4. 尝试读取 build.prop (如果存在解压后的文件)
                string[] buildPropPaths = {
                    Path.Combine(folderPath, "build.prop"),
                    Path.Combine(folderPath, "system_build.prop"),
                    Path.Combine(folderPath, "vendor_build.prop")
                };

                foreach (var propPath in buildPropPaths)
                {
                    if (File.Exists(propPath))
                    {
                        await ParseBuildPropAsync(propPath, info);
                        break;
                    }
                }

                // 5. 从 modem.img 推断基带信息
                string modemPath = Path.Combine(folderPath, "modem.img");
                if (File.Exists(modemPath))
                {
                    var modemInfo = new FileInfo(modemPath);
                    info.BasebandVersion = $"Modem ({FormatSize(modemInfo.Length)})";
                }
            }
            catch (Exception ex)
            {
                _logDetail($"解析固件信息失败: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// 解析 META-INF/com/android/metadata 文件
        /// </summary>
        private async Task ParseMetadataFileAsync(string metadataPath, FirmwareInfo info)
        {
            try
            {
                var lines = await Task.Run(() => File.ReadAllLines(metadataPath));
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("="))
                        continue;

                    int eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        string key = line.Substring(0, eqIndex).Trim();
                        string value = line.Substring(eqIndex + 1).Trim();
                        props[key] = value;
                    }
                }

                // 解析关键属性
                if (props.TryGetValue("product_name", out string product))
                    info.DeviceModel = product;

                if (props.TryGetValue("pre-device", out string device))
                    info.DeviceName = device;

                if (props.TryGetValue("android_version", out string android))
                    info.AndroidVersion = android;

                if (props.TryGetValue("os_version", out string os))
                    info.OsVersion = os;
                else if (props.TryGetValue("display_os_version", out os))
                    info.OsVersion = os;

                if (props.TryGetValue("version_name", out string version))
                    info.BuildNumber = version;
                else if (props.TryGetValue("version_name_show", out version))
                    info.BuildNumber = version;

                if (props.TryGetValue("security_patch", out string patch))
                    info.SecurityPatch = patch;
                else if (props.TryGetValue("post-security-patch-level", out patch))
                    info.SecurityPatch = patch;

                if (props.TryGetValue("post-build", out string fingerprint))
                    info.Fingerprint = fingerprint;

                if (props.TryGetValue("ota-type", out string otaType))
                    info.OtaType = otaType;

                _logDetail($"从 metadata 解析到 {props.Count} 个属性");
            }
            catch (Exception ex)
            {
                _logDetail($"解析 metadata 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析 payload_properties.txt 文件
        /// </summary>
        private async Task ParsePayloadPropertiesAsync(string propsPath, FirmwareInfo info)
        {
            try
            {
                var lines = await Task.Run(() => File.ReadAllLines(propsPath));

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("="))
                        continue;

                    int eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        string key = line.Substring(0, eqIndex).Trim();
                        string value = line.Substring(eqIndex + 1).Trim();

                        switch (key.ToLower())
                        {
                            case "android_version":
                                info.AndroidVersion = info.AndroidVersion ?? value;
                                break;
                            case "oplus_rom_version":
                                info.OsVersion = info.OsVersion ?? value;
                                break;
                            case "security_patch":
                                info.SecurityPatch = info.SecurityPatch ?? value;
                                break;
                            case "ota_target_version":
                                info.BuildNumber = info.BuildNumber ?? value;
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logDetail($"解析 payload_properties.txt 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 Payload 文件 (ZIP 或同目录) 解析固件信息
        /// </summary>
        private async Task ParseFirmwareInfoFromPayloadAsync(string payloadPath)
        {
            try
            {
                var info = new FirmwareInfo { FolderPath = Path.GetDirectoryName(payloadPath) };
                string ext = Path.GetExtension(payloadPath).ToLowerInvariant();
                string parentDir = Path.GetDirectoryName(payloadPath);

                // 如果是 ZIP 文件，尝试从内部读取 metadata
                if (ext == ".zip")
                {
                    await Task.Run(() => ParseFirmwareInfoFromZip(payloadPath, info));
                }

                // 如果 ZIP 内没找到，尝试从同目录下的文件读取
                if (string.IsNullOrEmpty(info.DeviceModel) && string.IsNullOrEmpty(info.DeviceName))
                {
                    // 查找同目录下的 metadata 文件
                    string[] metadataPaths = {
                        Path.Combine(parentDir, "META-INF", "com", "android", "metadata"),
                        Path.Combine(parentDir, "metadata")
                    };

                    foreach (var metaPath in metadataPaths)
                    {
                        if (File.Exists(metaPath))
                        {
                            await ParseMetadataFileAsync(metaPath, info);
                            break;
                        }
                    }

                    // 查找 payload_properties.txt
                    string propsPath = Path.Combine(parentDir, "payload_properties.txt");
                    if (File.Exists(propsPath))
                    {
                        await ParsePayloadPropertiesAsync(propsPath, info);
                    }
                }

                // 如果解析到了信息，显示
                if (!string.IsNullOrEmpty(info.DeviceModel) || !string.IsNullOrEmpty(info.DeviceName) ||
                    !string.IsNullOrEmpty(info.AndroidVersion) || !string.IsNullOrEmpty(info.OsVersion))
                {
                    CurrentFirmwareInfo = info;
                    DisplayFirmwareInfo();
                }
            }
            catch (Exception ex)
            {
                _logDetail($"从 Payload 解析固件信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 ZIP 文件内部读取固件信息
        /// </summary>
        private void ParseFirmwareInfoFromZip(string zipPath, FirmwareInfo info)
        {
            try
            {
                using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    // 查找 META-INF/com/android/metadata
                    var metadataEntry = archive.GetEntry("META-INF/com/android/metadata");
                    if (metadataEntry != null)
                    {
                        using (var stream = metadataEntry.Open())
                        using (var reader = new StreamReader(stream))
                        {
                            ParseMetadataContent(reader.ReadToEnd(), info);
                        }
                        _logDetail($"从 ZIP 内 metadata 解析成功");
                    }

                    // 查找 payload_properties.txt
                    var propsEntry = archive.GetEntry("payload_properties.txt");
                    if (propsEntry != null)
                    {
                        using (var stream = propsEntry.Open())
                        using (var reader = new StreamReader(stream))
                        {
                            ParsePayloadPropertiesContent(reader.ReadToEnd(), info);
                        }
                        _logDetail($"从 ZIP 内 payload_properties.txt 解析成功");
                    }
                }
            }
            catch (Exception ex)
            {
                _logDetail($"读取 ZIP 内固件信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析 metadata 内容字符串
        /// </summary>
        private void ParseMetadataContent(string content, FirmwareInfo info)
        {
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("=")) continue;

                int eqIndex = line.IndexOf('=');
                if (eqIndex > 0)
                {
                    string key = line.Substring(0, eqIndex).Trim();
                    string value = line.Substring(eqIndex + 1).Trim();
                    props[key] = value;
                }
            }

            if (props.TryGetValue("product_name", out string product))
                info.DeviceModel = product;
            if (props.TryGetValue("pre-device", out string device))
                info.DeviceName = device;
            if (props.TryGetValue("android_version", out string android))
                info.AndroidVersion = android;
            if (props.TryGetValue("os_version", out string os))
                info.OsVersion = os;
            else if (props.TryGetValue("display_os_version", out os))
                info.OsVersion = os;
            if (props.TryGetValue("version_name", out string version))
                info.BuildNumber = version;
            else if (props.TryGetValue("version_name_show", out version))
                info.BuildNumber = version;
            if (props.TryGetValue("security_patch", out string patch))
                info.SecurityPatch = patch;
            else if (props.TryGetValue("post-security-patch-level", out patch))
                info.SecurityPatch = patch;
            if (props.TryGetValue("post-build", out string fingerprint))
                info.Fingerprint = fingerprint;
            if (props.TryGetValue("ota-type", out string otaType))
                info.OtaType = otaType;
        }

        /// <summary>
        /// 解析 payload_properties.txt 内容字符串
        /// </summary>
        private void ParsePayloadPropertiesContent(string content, FirmwareInfo info)
        {
            foreach (var line in content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("=")) continue;

                int eqIndex = line.IndexOf('=');
                if (eqIndex > 0)
                {
                    string key = line.Substring(0, eqIndex).Trim();
                    string value = line.Substring(eqIndex + 1).Trim();

                    switch (key.ToLower())
                    {
                        case "android_version":
                            info.AndroidVersion = info.AndroidVersion ?? value;
                            break;
                        case "oplus_rom_version":
                            info.OsVersion = info.OsVersion ?? value;
                            break;
                        case "security_patch":
                            info.SecurityPatch = info.SecurityPatch ?? value;
                            break;
                        case "ota_target_version":
                            info.BuildNumber = info.BuildNumber ?? value;
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// 解析 build.prop 文件
        /// </summary>
        private async Task ParseBuildPropAsync(string propPath, FirmwareInfo info)
        {
            try
            {
                var lines = await Task.Run(() => File.ReadAllLines(propPath));
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                        continue;

                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    switch (key)
                    {
                        case "ro.product.model":
                        case "ro.product.system.model":
                            if (string.IsNullOrEmpty(info.DeviceName))
                                info.DeviceName = value;
                            break;
                        case "ro.product.device":
                        case "ro.product.system.device":
                            if (string.IsNullOrEmpty(info.DeviceModel))
                                info.DeviceModel = value;
                            break;
                        case "ro.build.version.release":
                            info.AndroidVersion = value;
                            break;
                        case "ro.build.display.id":
                        case "ro.system.build.version.incremental":
                            if (string.IsNullOrEmpty(info.BuildNumber))
                                info.BuildNumber = value;
                            break;
                        case "ro.build.version.security_patch":
                            info.SecurityPatch = value;
                            break;
                        case "ro.build.fingerprint":
                            info.Fingerprint = value;
                            break;
                        case "ro.oplus.version":
                        case "ro.build.version.ota":
                            info.OsVersion = value;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logDetail($"解析 build.prop 失败: {ex.Message}");
            }
        }

        #endregion

        #region 重启操作

        /// <summary>
        /// 重启到系统
        /// </summary>
        public async Task<bool> RebootToSystemAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            return await _service.RebootAsync();
        }

        /// <summary>
        /// 重启到 Bootloader
        /// </summary>
        public async Task<bool> RebootToBootloaderAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            return await _service.RebootBootloaderAsync();
        }

        /// <summary>
        /// 重启到 Fastbootd
        /// </summary>
        public async Task<bool> RebootToFastbootdAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            return await _service.RebootFastbootdAsync();
        }

        /// <summary>
        /// 重启到 Recovery
        /// </summary>
        public async Task<bool> RebootToRecoveryAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            return await _service.RebootRecoveryAsync();
        }
        
        // 别名方法 (供快捷操作使用)
        public Task<bool> RebootAsync() => RebootToSystemAsync();
        public Task<bool> RebootBootloaderAsync() => RebootToBootloaderAsync();
        public Task<bool> RebootFastbootdAsync() => RebootToFastbootdAsync();
        public Task<bool> RebootRecoveryAsync() => RebootToRecoveryAsync();
        
        /// <summary>
        /// OEM EDL - 小米踢EDL (fastboot oem edl)
        /// </summary>
        public async Task<bool> OemEdlAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            return await _service.OemEdlAsync();
        }
        
        /// <summary>
        /// 擦除 FRP (谷歌锁)
        /// </summary>
        public async Task<bool> EraseFrpAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            return await _service.EraseFrpAsync();
        }
        
        /// <summary>
        /// 获取当前槽位
        /// </summary>
        public async Task<string> GetCurrentSlotAsync()
        {
            if (!await EnsureConnectedAsync()) return null;
            return await _service.GetCurrentSlotAsync();
        }
        
        /// <summary>
        /// 设置活动槽位
        /// </summary>
        public async Task<bool> SetActiveSlotAsync(string slot)
        {
            if (!await EnsureConnectedAsync()) return false;
            return await _service.SetActiveSlotAsync(slot, _cts?.Token ?? CancellationToken.None);
        }

        #endregion

        #region 解锁/锁定

        /// <summary>
        /// 执行解锁操作
        /// </summary>
        public async Task<bool> UnlockBootloaderAsync()
        {
            if (!await EnsureConnectedAsync()) return false;

            string method = "flashing unlock";
            
            // 根据 checkbox 状态选择解锁方法
            // 可以从 _commandComboBox 获取选择的命令
            string selectedCmd = GetSelectedCommand();
            if (!string.IsNullOrEmpty(selectedCmd) && selectedCmd.Contains("unlock"))
            {
                method = selectedCmd;
            }

            return await _service.UnlockBootloaderAsync(method);
        }

        /// <summary>
        /// 执行锁定操作
        /// </summary>
        public async Task<bool> LockBootloaderAsync()
        {
            if (!await EnsureConnectedAsync()) return false;

            string method = "flashing lock";
            
            string selectedCmd = GetSelectedCommand();
            if (!string.IsNullOrEmpty(selectedCmd) && selectedCmd.Contains("lock"))
            {
                method = selectedCmd;
            }

            return await _service.LockBootloaderAsync(method);
        }

        #endregion

        #region A/B 槽位

        /// <summary>
        /// 切换 A/B 槽位
        /// </summary>
        public async Task<bool> SwitchSlotAsync()
        {
            if (!await EnsureConnectedAsync()) return false;
            
            bool success = await _service.SwitchSlotAsync();
            
            if (success)
            {
                await ReadPartitionTableAsync();
            }

            return success;
        }

        #endregion

        #region 快捷命令

        /// <summary>
        /// 执行选中的快捷命令
        /// </summary>
        public async Task<bool> ExecuteSelectedCommandAsync()
        {
            if (!await EnsureConnectedAsync()) return false;

            string command = GetSelectedCommand();
            if (string.IsNullOrEmpty(command))
            {
                Log("请选择要执行的命令", Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                Log($"执行命令: {command}", Color.Blue);
                var result = await _service.ExecuteCommandAsync(command, _cts.Token);
                
                if (!string.IsNullOrEmpty(result))
                {
                    // 显示命令执行结果
                    Log($"结果: {result}", Color.Green);
                    return true;
                }
                else
                {
                    Log("命令执行完成（无返回值）", Color.Gray);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"命令执行失败: {ex.Message}", Color.Red);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private string GetSelectedCommand()
        {
            try
            {
                if (_commandComboBox == null) return null;
                string cmd = _commandComboBox.SelectedItem?.ToString() ?? _commandComboBox.Text;
                
                if (string.IsNullOrEmpty(cmd)) return null;
                
                // 自动去掉 "fastboot " 前缀
                if (cmd.StartsWith("fastboot ", StringComparison.OrdinalIgnoreCase))
                {
                    cmd = cmd.Substring(9).Trim();
                }
                
                return cmd;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 检查是否有选中的快捷命令
        /// </summary>
        public bool HasSelectedCommand()
        {
            string cmd = GetSelectedCommand();
            return !string.IsNullOrWhiteSpace(cmd);
        }

        #endregion

        #region 辅助方法

        private bool EnsureConnected()
        {
            if (_service == null || !_service.IsConnected)
            {
                // 检查是否有可用设备，提示用户连接
                if (_cachedDevices != null && _cachedDevices.Count > 0)
                {
                    Log("请先点击「连接」按钮连接 Fastboot 设备", Color.Red);
                }
                else
                {
                    Log("未检测到 Fastboot 设备，请确保设备已进入 Fastboot 模式", Color.Red);
                }
                return false;
            }
            return true;
        }
        
        /// <summary>
        /// 确保设备已连接（异步版本，支持自动连接）
        /// </summary>
        private async Task<bool> EnsureConnectedAsync()
        {
            if (_service != null && _service.IsConnected)
                return true;
            
            // 自动尝试连接
            string selectedDevice = GetSelectedDevice();
            if (!string.IsNullOrEmpty(selectedDevice))
            {
                Log("自动连接 Fastboot 设备...", Color.Blue);
                return await ConnectAsync();
            }
            
            // 检查是否有可用设备
            if (_cachedDevices != null && _cachedDevices.Count > 0)
            {
                Log("请先选择并连接 Fastboot 设备", Color.Red);
            }
            else
            {
                Log("未检测到 Fastboot 设备，请确保设备已进入 Fastboot 模式", Color.Red);
            }
            return false;
        }

        /// <summary>
        /// 启动操作计时器
        /// </summary>
        private void StartOperationTimer(string operationName)
        {
            _currentOperationName = operationName;
            _operationStopwatch.Restart();
            _lastBytes = 0;
            _lastSpeedUpdate = DateTime.Now;
            _currentSpeed = 0;
            _completedBytes = 0;
            _totalOperationBytes = 0;
        }

        /// <summary>
        /// 停止操作计时器
        /// </summary>
        private void StopOperationTimer()
        {
            _operationStopwatch.Stop();
            UpdateTimeLabel();
        }

        /// <summary>
        /// 更新时间标签
        /// </summary>
        private void UpdateTimeLabel()
        {
            if (_timeLabel == null) return;

            var elapsed = _operationStopwatch.Elapsed;
            string timeText = elapsed.Hours > 0
                ? $"时间：{elapsed:hh\\:mm\\:ss}"
                : $"时间：{elapsed:mm\\:ss}";
            
            UpdateLabelSafe(_timeLabel, timeText);
        }

        /// <summary>
        /// 更新速度标签
        /// </summary>
        private void UpdateSpeedLabel()
        {
            if (_speedLabel == null) return;

            string speedText;
            if (_currentSpeed >= 1024 * 1024)
                speedText = $"速度：{_currentSpeed / (1024 * 1024):F1} MB/s";
            else if (_currentSpeed >= 1024)
                speedText = $"速度：{_currentSpeed / 1024:F1} KB/s";
            else
                speedText = $"速度：{_currentSpeed:F0} B/s";
            
            UpdateLabelSafe(_speedLabel, speedText);
        }
        
        /// <summary>
        /// 更新速度标签 (使用格式化的速度字符串)
        /// </summary>
        private void UpdateSpeedLabel(string formattedSpeed)
        {
            if (_speedLabel == null) return;
            UpdateLabelSafe(_speedLabel, $"速度：{formattedSpeed}");
        }
        
        /// <summary>
        /// 刷写进度回调
        /// </summary>
        private void OnFlashProgressChanged(FlashProgress progress)
        {
            if (progress == null) return;
            
            // 计算进度值
            double subProgress = progress.Percent;
            double mainProgress = _flashTotalPartitions > 0 
                ? (_flashCurrentPartitionIndex * 100.0 + progress.Percent) / _flashTotalPartitions 
                : 0;
            
            // 时间间隔检查
            var now = DateTime.Now;
            bool timeElapsed = (now - _lastProgressUpdate).TotalMilliseconds >= ProgressUpdateIntervalMs;
            bool forceUpdate = progress.Percent >= 95;
            
            // 无论如何都更新（保证流畅性）
            if (!forceUpdate && !timeElapsed)
                return;
            
            _lastProgressUpdate = now;
            
            // 更新子进度条（当前分区进度）
            _lastSubProgressValue = subProgress;
            UpdateSubProgressBar(subProgress);
            
            // 更新总进度条（多分区刷写时）
            if (_flashTotalPartitions > 0)
            {
                _lastMainProgressValue = mainProgress;
                UpdateProgressBar(mainProgress);
            }
            
            // 更新速度显示
            if (progress.SpeedKBps > 0)
            {
                UpdateSpeedLabel(progress.SpeedFormatted);
            }
            
            // 实时更新时间
            UpdateTimeLabel();
        }
        
        /// <summary>
        /// 格式化速度显示
        /// </summary>
        private string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "计算中...";
            
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            double speed = bytesPerSecond;
            int unitIndex = 0;
            while (speed >= 1024 && unitIndex < units.Length - 1)
            {
                speed /= 1024;
                unitIndex++;
            }
            return $"{speed:F2} {units[unitIndex]}";
        }

        /// <summary>
        /// 更新进度条 (百分比)
        /// </summary>
        private void UpdateProgressBar(double percent)
        {
            if (_progressBar == null) return;

            try
            {
                int value = Math.Min(100, Math.Max(0, (int)percent));
                
                if (_progressBar.InvokeRequired)
                {
                    _progressBar.BeginInvoke(new Action(() =>
                    {
                        try { _progressBar.Value = value; } catch { }
                    }));
                }
                else
                {
                    _progressBar.Value = value;
                }
            }
            catch { }
        }

        /// <summary>
        /// 更新子进度条
        /// </summary>
        private void UpdateSubProgressBar(double percent)
        {
            if (_subProgressBar == null) return;

            try
            {
                int value = Math.Min(100, Math.Max(0, (int)percent));
                
                if (_subProgressBar.InvokeRequired)
                {
                    _subProgressBar.BeginInvoke(new Action(() =>
                    {
                        try { _subProgressBar.Value = value; } catch { }
                    }));
                }
                else
                {
                    _subProgressBar.Value = value;
                }
            }
            catch { }
        }

        /// <summary>
        /// 带速度计算的进度更新 (用于文件传输)
        /// </summary>
        private void UpdateProgressWithSpeed(long current, long total)
        {
            // 计算进度
            if (total > 0)
            {
                double percent = 100.0 * current / total;
                UpdateSubProgressBar(percent);
            }

            // 计算速度
            long bytesDelta = current - _lastBytes;
            double timeDelta = (DateTime.Now - _lastSpeedUpdate).TotalSeconds;
            
            if (timeDelta >= 0.2 && bytesDelta > 0) // 每200ms更新一次
            {
                double instantSpeed = bytesDelta / timeDelta;
                // 指数移动平均平滑速度
                _currentSpeed = (_currentSpeed > 0) ? (_currentSpeed * 0.6 + instantSpeed * 0.4) : instantSpeed;
                _lastBytes = current;
                _lastSpeedUpdate = DateTime.Now;
                
                // 更新速度和时间显示
                UpdateSpeedLabel();
                UpdateTimeLabel();
            }
        }

        private void UpdateProgress(int current, int total)
        {
            if (_progressBar == null) return;

            try
            {
                int percent = total > 0 ? (current * 100 / total) : 0;
                UpdateProgressBar(percent);
            }
            catch { }
        }

        private bool IsAutoRebootEnabled()
        {
            try
            {
                return _autoRebootCheckbox?.Checked ?? false;
            }
            catch
            {
                return false;
            }
        }

        private bool IsSwitchSlotEnabled()
        {
            try
            {
                return _switchSlotCheckbox?.Checked ?? false;
            }
            catch
            {
                return false;
            }
        }

        private bool IsEraseGoogleLockEnabled()
        {
            try
            {
                return _eraseGoogleLockCheckbox?.Checked ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 根据脚本内容自动勾选复选框
        /// </summary>
        /// <param name="hasSetActive">脚本是否包含切换槽位命令</param>
        /// <param name="hasReboot">脚本是否包含重启命令</param>
        private void AutoCheckOptionsFromScript(bool hasSetActive, bool hasReboot)
        {
            try
            {
                // 自动勾选切换槽位（脚本包含 set_active 命令）
                if (_switchSlotCheckbox != null)
                {
                    if (_switchSlotCheckbox.InvokeRequired)
                    {
                        _switchSlotCheckbox.Invoke(new Action(() => {
                            _switchSlotCheckbox.Checked = hasSetActive;
                        }));
                    }
                    else
                    {
                        _switchSlotCheckbox.Checked = hasSetActive;
                    }
                    
                    if (hasSetActive)
                    {
                        Log("脚本包含槽位切换命令，已自动勾选 [切换A槽]", Color.Gray);
                    }
                }

                // 自动勾选自动重启（脚本包含 reboot 命令）
                if (_autoRebootCheckbox != null)
                {
                    if (_autoRebootCheckbox.InvokeRequired)
                    {
                        _autoRebootCheckbox.Invoke(new Action(() => {
                            _autoRebootCheckbox.Checked = hasReboot;
                        }));
                    }
                    else
                    {
                        _autoRebootCheckbox.Checked = hasReboot;
                    }
                    
                    if (hasReboot)
                    {
                        Log("脚本包含重启命令，已自动勾选 [自动重启]", Color.Gray);
                    }
                }
            }
            catch (Exception ex)
            {
                _logDetail($"自动勾选复选框失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷写完成后执行附加操作（切换槽位、擦除谷歌锁等）
        /// </summary>
        private async Task ExecutePostFlashOperationsAsync()
        {
            // 切换 A 槽位
            if (IsSwitchSlotEnabled())
            {
                Log("正在切换到 A 槽位...", Color.Blue);
                bool success = await _service.SetActiveSlotAsync("a", _cts?.Token ?? CancellationToken.None);
                Log(success ? "已切换到 A 槽位" : "切换槽位失败", success ? Color.Green : Color.Red);
            }

            // 擦除谷歌锁 (FRP)
            if (IsEraseGoogleLockEnabled())
            {
                Log("正在擦除谷歌锁 (FRP)...", Color.Blue);
                // 尝试擦除 frp 分区
                bool success = await _service.ErasePartitionAsync("frp", _cts?.Token ?? CancellationToken.None);
                if (!success)
                {
                    // 部分设备使用 config 或 persistent 分区
                    success = await _service.ErasePartitionAsync("config", _cts?.Token ?? CancellationToken.None);
                }
                Log(success ? "谷歌锁已擦除" : "擦除谷歌锁失败（分区可能不存在）", success ? Color.Green : Color.Orange);
            }
        }

        /// <summary>
        /// 取消当前操作
        /// </summary>
        public void CancelOperation()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            StopOperationTimer();
            UpdateLabelSafe(_operationLabel, "当前操作：已取消");
        }

        /// <summary>
        /// 安全重置 CancellationTokenSource（释放旧实例后创建新实例）
        /// </summary>
        private void ResetCancellationToken()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } 
                catch (Exception ex) { Debug.WriteLine($"[Fastboot] 取消令牌异常: {ex.Message}"); }
                try { _cts.Dispose(); } 
                catch (Exception ex) { Debug.WriteLine($"[Fastboot] 释放令牌异常: {ex.Message}"); }
            }
            _cts = new CancellationTokenSource();
        }

        #endregion

        #region Bat 脚本解析

        // 存储解析的刷机任务
        private List<BatScriptParser.FlashTask> _flashTasks;
        
        /// <summary>
        /// 获取当前加载的刷机任务
        /// </summary>
        public List<BatScriptParser.FlashTask> FlashTasks => _flashTasks;

        /// <summary>
        /// 加载 bat/sh 刷机脚本
        /// </summary>
        public bool LoadFlashScript(string scriptPath)
        {
            if (!File.Exists(scriptPath))
            {
                Log($"脚本文件不存在: {scriptPath}", Color.Red);
                return false;
            }

            try
            {
                Log($"正在解析脚本: {Path.GetFileName(scriptPath)}...", Color.Blue);

                string baseDir = Path.GetDirectoryName(scriptPath);
                var parser = new BatScriptParser(baseDir, msg => _logDetail(msg));

                _flashTasks = parser.ParseScript(scriptPath);

                if (_flashTasks.Count == 0)
                {
                    Log("脚本中未找到有效的刷机命令", Color.Orange);
                    return false;
                }

                // 统计信息
                int flashCount = _flashTasks.Count(t => t.Operation == "flash");
                int eraseCount = _flashTasks.Count(t => t.Operation == "erase");
                int setActiveCount = _flashTasks.Count(t => t.Operation == "set_active");
                int rebootCount = _flashTasks.Count(t => t.Operation == "reboot");
                int existCount = _flashTasks.Count(t => t.ImageExists);
                long totalSize = _flashTasks.Where(t => t.ImageExists).Sum(t => t.FileSize);

                Log($"解析完成: {flashCount} 个刷写, {eraseCount} 个擦除", Color.Green);
                Log($"镜像文件: {existCount} 个存在, 总大小 {FormatSize(totalSize)}", Color.Blue);

                // 根据脚本内容自动勾选复选框
                AutoCheckOptionsFromScript(setActiveCount > 0, rebootCount > 0);

                // 更新分区列表显示
                UpdatePartitionListFromScript();

                return true;
            }
            catch (Exception ex)
            {
                Log($"解析脚本失败: {ex.Message}", Color.Red);
                return false;
            }
        }

        /// <summary>
        /// 从脚本任务更新分区列表
        /// </summary>
        private void UpdatePartitionListFromScript()
        {
            if (_partitionListView == null || _flashTasks == null) return;

            try
            {
                if (_partitionListView.InvokeRequired)
                {
                    _partitionListView.BeginInvoke(new Action(UpdatePartitionListFromScriptInternal));
                }
                else
                {
                    UpdatePartitionListFromScriptInternal();
                }
            }
            catch { }
        }

        private void UpdatePartitionListFromScriptInternal()
        {
            try
            {
                _partitionListView.Items.Clear();

                foreach (var task in _flashTasks)
                {
                    // 根据操作类型设置不同显示
                    string operationText = task.Operation;
                    string sizeText = "-";
                    string filePathText = "";

                    if (task.Operation == "flash")
                    {
                        operationText = task.ImageExists ? "写入" : "写入 (缺失)";
                        sizeText = task.FileSizeFormatted;
                        filePathText = task.ImagePath;
                    }
                    else if (task.Operation == "erase")
                    {
                        operationText = "擦除";
                    }
                    else if (task.Operation == "set_active")
                    {
                        operationText = "激活槽位";
                    }
                    else if (task.Operation == "reboot")
                    {
                        operationText = "重启";
                    }

                    var item = new ListViewItem(new string[]
                    {
                        task.PartitionName,
                        operationText,
                        sizeText,
                        filePathText
                    });

                    item.Tag = task;

                    // 根据状态设置颜色
                    if (task.Operation == "flash" && !task.ImageExists)
                    {
                        item.ForeColor = Color.Red;
                    }
                    else if (task.Operation == "erase")
                    {
                        item.ForeColor = Color.Orange;
                    }
                    else if (task.Operation == "set_active" || task.Operation == "reboot")
                    {
                        item.ForeColor = Color.Gray;
                    }

                    // 默认勾选所有 flash 和 erase 操作
                    if ((task.Operation == "flash" && task.ImageExists) || task.Operation == "erase")
                    {
                        item.Checked = true;
                    }

                    _partitionListView.Items.Add(item);
                }
            }
            catch { }
        }

        /// <summary>
        /// 执行加载的刷机脚本
        /// </summary>
        /// <param name="keepData">是否保留数据（跳过 userdata 刷写）</param>
        /// <param name="lockBl">是否在刷机后锁定BL</param>
        public async Task<bool> ExecuteFlashScriptAsync(bool keepData = false, bool lockBl = false)
        {
            if (_flashTasks == null || _flashTasks.Count == 0)
            {
                Log("请先加载刷机脚本", Color.Orange);
                return false;
            }

            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (!await EnsureConnectedAsync()) return false;

            // 获取选中的任务
            var selectedTasks = new List<BatScriptParser.FlashTask>();
            
            try
            {
                foreach (ListViewItem item in _partitionListView.CheckedItems)
                {
                    if (item.Tag is BatScriptParser.FlashTask task)
                    {
                        selectedTasks.Add(task);
                    }
                }
            }
            catch { }

            if (selectedTasks.Count == 0)
            {
                Log("请选择要执行的刷机任务", Color.Orange);
                return false;
            }

            // 根据选项过滤任务
            if (keepData)
            {
                // 保留数据：跳过 userdata 相关分区
                int beforeCount = selectedTasks.Count;
                selectedTasks = selectedTasks.Where(t => 
                    !t.PartitionName.Equals("userdata", StringComparison.OrdinalIgnoreCase) &&
                    !t.PartitionName.Equals("userdata_ab", StringComparison.OrdinalIgnoreCase) &&
                    !t.PartitionName.Equals("metadata", StringComparison.OrdinalIgnoreCase)
                ).ToList();
                
                if (selectedTasks.Count < beforeCount)
                {
                    Log("保留数据模式：跳过 userdata/metadata 分区", Color.Blue);
                }
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("执行刷机脚本");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                UpdateLabelSafe(_speedLabel, "速度：准备中...");

                int total = selectedTasks.Count;
                int success = 0;
                int failed = 0;

                Log($"开始执行 {total} 个刷机任务...", Color.Blue);
                if (keepData) Log("模式: 保留数据", Color.Blue);
                if (lockBl) Log("模式: 刷机后锁定BL", Color.Blue);

                for (int i = 0; i < total; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var task = selectedTasks[i];
                    // 总进度：基于任务数
                    UpdateProgressBar((i * 100.0) / total);
                    // 子进度：当前任务开始
                    UpdateSubProgressBar(0);
                    UpdateLabelSafe(_operationLabel, $"当前操作：{task.Operation} {task.PartitionName} ({i + 1}/{total})");

                    bool taskSuccess = false;

                    switch (task.Operation)
                    {
                        case "flash":
                            if (task.ImageExists)
                            {
                                taskSuccess = await _service.FlashPartitionAsync(
                                    task.PartitionName, task.ImagePath, false, _cts.Token);
                            }
                            else
                            {
                                Log($"跳过 {task.PartitionName}: 镜像文件不存在", Color.Orange);
                            }
                            break;

                        case "erase":
                            // 保留数据模式下跳过 userdata 擦除
                            if (keepData && (task.PartitionName.Equals("userdata", StringComparison.OrdinalIgnoreCase) ||
                                             task.PartitionName.Equals("metadata", StringComparison.OrdinalIgnoreCase)))
                            {
                                Log($"跳过擦除 {task.PartitionName} (保留数据)", Color.Gray);
                                taskSuccess = true;
                            }
                            else
                            {
                                taskSuccess = await _service.ErasePartitionAsync(task.PartitionName, _cts.Token);
                            }
                            break;

                        case "set_active":
                            string slot = task.PartitionName.Replace("slot_", "");
                            taskSuccess = await _service.SetActiveSlotAsync(slot, _cts.Token);
                            break;

                        case "reboot":
                            // 重启操作放在最后执行
                            if (i == total - 1)
                            {
                                // 如果需要锁定BL，在重启前执行
                                if (lockBl)
                                {
                                    Log("正在锁定 Bootloader...", Color.Blue);
                                    await _service.LockBootloaderAsync("flashing lock", _cts.Token);
                                }

                                string target = task.PartitionName.Replace("reboot_", "");
                                if (target == "system" || string.IsNullOrEmpty(target))
                                {
                                    taskSuccess = await _service.RebootAsync(_cts.Token);
                                }
                                else if (target == "bootloader")
                                {
                                    taskSuccess = await _service.RebootBootloaderAsync(_cts.Token);
                                }
                                else if (target == "recovery")
                                {
                                    taskSuccess = await _service.RebootRecoveryAsync(_cts.Token);
                                }
                            }
                            else
                            {
                                Log("跳过中间的重启命令", Color.Gray);
                                taskSuccess = true;
                            }
                            break;
                    }

                    // 子进度：当前任务完成
                    UpdateSubProgressBar(100);
                    
                    if (taskSuccess)
                        success++;
                    else
                        failed++;
                }

                UpdateProgressBar(100);
                UpdateSubProgressBar(100);
                StopOperationTimer();

                // 执行刷写后附加操作（切换槽位、擦除谷歌锁等）
                if (success > 0)
                {
                    await ExecutePostFlashOperationsAsync();
                }

                // 如果没有重启命令但需要锁定BL，在这里执行
                bool hasReboot = selectedTasks.Any(t => t.Operation == "reboot");
                if (lockBl && !hasReboot)
                {
                    Log("正在锁定 Bootloader...", Color.Blue);
                    await _service.LockBootloaderAsync("flashing lock", _cts.Token);
                }

                Log($"刷机完成: {success} 成功, {failed} 失败", 
                    failed == 0 ? Color.Green : Color.Orange);

                return failed == 0;
            }
            catch (OperationCanceledException)
            {
                Log("刷机操作已取消", Color.Orange);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            catch (Exception ex)
            {
                Log($"刷机失败: {ex.Message}", Color.Red);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 扫描目录中的刷机脚本
        /// </summary>
        public List<string> ScanFlashScripts(string directory)
        {
            return BatScriptParser.FindFlashScripts(directory);
        }

        /// <summary>
        /// 格式化文件大小
        /// </summary>
        private string FormatSize(long size)
        {
            if (size >= 1024L * 1024 * 1024)
                return $"{size / (1024.0 * 1024 * 1024):F2} GB";
            if (size >= 1024 * 1024)
                return $"{size / (1024.0 * 1024):F2} MB";
            if (size >= 1024)
                return $"{size / 1024.0:F2} KB";
            return $"{size} B";
        }

        #endregion

        #region Payload 解析

        /// <summary>
        /// 从 URL 加载远程 Payload (云端解析)
        /// </summary>
        public async Task<bool> LoadPayloadFromUrlAsync(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                Log("请输入 URL", Color.Orange);
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

                StartOperationTimer("解析云端 Payload");
                UpdateProgressBar(0);
                UpdateLabelSafe(_operationLabel, "当前操作：解析云端 Payload");
                UpdateLabelSafe(_speedLabel, "速度：连接中...");

                Log($"正在解析云端 Payload...", Color.Blue);

                // 创建或重用 RemotePayloadService
                if (_remotePayloadService == null)
                {
                    _remotePayloadService = new RemotePayloadService(
                        msg => Log(msg, null),
                        (current, total) => UpdateProgressWithSpeed(current, total),
                        _logDetail
                    );

                    _remotePayloadService.ExtractProgressChanged += (s, e) =>
                    {
                        UpdateSubProgressBar(e.Percent);
                        // 更新速度显示
                        if (e.SpeedBytesPerSecond > 0)
                        {
                            UpdateSpeedLabel(e.SpeedFormatted);
                        }
                    };
                }

                // 先获取真实 URL (处理重定向)
                UpdateProgressBar(10);
                var (realUrl, expiresTime) = await _remotePayloadService.GetRedirectUrlAsync(url, _cts.Token);
                
                if (string.IsNullOrEmpty(realUrl))
                {
                    Log("无法获取下载链接", Color.Red);
                    UpdateProgressBar(0);
                    return false;
                }

                if (realUrl != url)
                {
                    Log("已获取真实下载链接", Color.Green);
                    if (expiresTime.HasValue)
                    {
                        Log($"链接过期时间: {expiresTime.Value:yyyy-MM-dd HH:mm:ss}", Color.Blue);
                    }
                }

                UpdateProgressBar(30);
                bool success = await _remotePayloadService.LoadFromUrlAsync(realUrl, _cts.Token);

                if (success)
                {
                    UpdateProgressBar(70);

                    var summary = _remotePayloadService.GetSummary();
                    Log($"云端 Payload 解析成功: {summary.PartitionCount} 个分区", Color.Green);
                    Log($"文件大小: {summary.TotalSizeFormatted}", Color.Blue);

                    // 更新分区列表显示
                    UpdatePartitionListFromRemotePayload();

                    UpdateProgressBar(100);
                }
                else
                {
                    Log("云端 Payload 解析失败", Color.Red);
                    UpdateProgressBar(0);
                }

                StopOperationTimer();
                return success;
            }
            catch (Exception ex)
            {
                Log($"云端 Payload 加载失败: {ex.Message}", Color.Red);
                _logDetail($"云端 Payload 加载错误: {ex}");
                UpdateProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 从远程 Payload 更新分区列表
        /// </summary>
        private void UpdatePartitionListFromRemotePayload()
        {
            if (_partitionListView == null || _remotePayloadService == null || !_remotePayloadService.IsLoaded) return;

            try
            {
                if (_partitionListView.InvokeRequired)
                {
                    _partitionListView.BeginInvoke(new Action(UpdatePartitionListFromRemotePayloadInternal));
                }
                else
                {
                    UpdatePartitionListFromRemotePayloadInternal();
                }
            }
            catch { }
        }

        private void UpdatePartitionListFromRemotePayloadInternal()
        {
            try
            {
                _partitionListView.Items.Clear();

                foreach (var partition in _remotePayloadService.Partitions)
                {
                    var item = new ListViewItem(new string[]
                    {
                        partition.Name,
                        "云端提取",  // 操作列
                        partition.SizeFormatted,
                        $"{partition.Operations.Count} ops"  // 操作数
                    });

                    item.Tag = partition;
                    item.Checked = true;  // 默认勾选

                    // 标记常用分区
                    string name = partition.Name.ToLowerInvariant();
                    if (name.Contains("system") || name.Contains("vendor") || name.Contains("product"))
                    {
                        item.ForeColor = Color.Blue;
                    }
                    else if (name.Contains("boot") || name.Contains("dtbo") || name.Contains("vbmeta"))
                    {
                        item.ForeColor = Color.DarkGreen;
                    }

                    _partitionListView.Items.Add(item);
                }
            }
            catch { }
        }

        /// <summary>
        /// 从云端提取选中的分区
        /// </summary>
        public async Task<bool> ExtractSelectedRemotePartitionsAsync(string outputDir)
        {
            if (_remotePayloadService == null || !_remotePayloadService.IsLoaded)
            {
                Log("请先加载云端 Payload", Color.Orange);
                return false;
            }

            if (string.IsNullOrEmpty(outputDir))
            {
                Log("请指定输出目录", Color.Orange);
                return false;
            }

            if (IsBusy)
            {
                Log("操作进行中", Color.Orange);
                return false;
            }

            // 获取选中的分区名称
            var selectedNames = new List<string>();
            try
            {
                foreach (ListViewItem item in _partitionListView.CheckedItems)
                {
                    if (item.Tag is RemotePayloadPartition partition)
                    {
                        selectedNames.Add(partition.Name);
                    }
                }
            }
            catch { }

            if (selectedNames.Count == 0)
            {
                Log("请选择要提取的分区", Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("云端提取分区");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                UpdateLabelSafe(_speedLabel, "速度：准备中...");

                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                Log($"开始从云端提取 {selectedNames.Count} 个分区到: {outputDir}", Color.Blue);

                int success = 0;
                int total = selectedNames.Count;
                int currentIndex = 0;

                // 注册进度事件处理器
                EventHandler<RemoteExtractProgress> progressHandler = (s, e) =>
                {
                    // 子进度条：当前分区的提取进度
                    UpdateSubProgressBar(e.Percent);
                    // 更新速度显示
                    if (e.SpeedBytesPerSecond > 0)
                    {
                        UpdateSpeedLabel(e.SpeedFormatted);
                    }
                };

                _remotePayloadService.ExtractProgressChanged += progressHandler;

                try
                {
                    for (int i = 0; i < total; i++)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        currentIndex = i;

                        string name = selectedNames[i];
                        string outputPath = Path.Combine(outputDir, $"{name}.img");

                        UpdateLabelSafe(_operationLabel, $"当前操作：云端提取 {name} ({i + 1}/{total})");
                        // 总进度：基于已完成的分区数
                        UpdateProgressBar((i * 100.0) / total);
                        // 子进度：开始提取
                        UpdateSubProgressBar(0);

                        if (await _remotePayloadService.ExtractPartitionAsync(name, outputPath, _cts.Token))
                        {
                            success++;
                            Log($"提取成功: {name}.img", Color.Green);
                        }
                        else
                        {
                            Log($"提取失败: {name}", Color.Red);
                        }
                        
                        // 子进度：当前分区提取完成
                        UpdateSubProgressBar(100);
                    }
                }
                finally
                {
                    _remotePayloadService.ExtractProgressChanged -= progressHandler;
                }

                UpdateProgressBar(100);
                UpdateSubProgressBar(100);
                StopOperationTimer();

                Log($"云端提取完成: {success}/{total} 成功", success == total ? Color.Green : Color.Orange);

                return success == total;
            }
            catch (OperationCanceledException)
            {
                Log("提取操作已取消", Color.Orange);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            catch (Exception ex)
            {
                Log($"提取失败: {ex.Message}", Color.Red);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 加载 Payload 文件 (支持 .bin 和 .zip)
        /// </summary>
        public async Task<bool> LoadPayloadAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                Log("请选择 Payload 文件", Color.Orange);
                return false;
            }

            if (!File.Exists(filePath))
            {
                Log($"文件不存在: {filePath}", Color.Red);
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

                StartOperationTimer("解析 Payload");
                UpdateProgressBar(0);
                UpdateLabelSafe(_operationLabel, "当前操作：解析 Payload");
                UpdateLabelSafe(_speedLabel, "速度：解析中...");

                Log($"正在加载 Payload: {Path.GetFileName(filePath)}...", Color.Blue);

                // 创建或重用 PayloadService
                if (_payloadService == null)
                {
                    _payloadService = new PayloadService(
                        msg => Log(msg, null),
                        (current, total) => UpdateProgressWithSpeed(current, total),
                        _logDetail
                    );

                    _payloadService.ExtractProgressChanged += (s, e) =>
                    {
                        PayloadExtractProgress?.Invoke(this, e);
                        UpdateSubProgressBar(e.Percent);
                    };
                }

                UpdateProgressBar(30);
                bool success = await _payloadService.LoadPayloadAsync(filePath, _cts.Token);

                if (success)
                {
                    UpdateProgressBar(70);

                    var summary = _payloadService.GetSummary();
                    Log($"Payload 解析成功: {summary.PartitionCount} 个分区", Color.Green);
                    Log($"总大小: {summary.TotalSizeFormatted}, 压缩后: {summary.TotalCompressedSizeFormatted}", Color.Blue);

                    // 更新分区列表显示
                    UpdatePartitionListFromPayload();

                    // 尝试从 ZIP 或同目录下解析固件信息
                    await ParseFirmwareInfoFromPayloadAsync(filePath);

                    UpdateProgressBar(100);
                    PayloadLoaded?.Invoke(this, summary);
                }
                else
                {
                    Log("Payload 解析失败", Color.Red);
                    UpdateProgressBar(0);
                }

                StopOperationTimer();
                return success;
            }
            catch (Exception ex)
            {
                Log($"Payload 加载失败: {ex.Message}", Color.Red);
                _logDetail($"Payload 加载错误: {ex}");
                UpdateProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 从 Payload 更新分区列表
        /// </summary>
        private void UpdatePartitionListFromPayload()
        {
            if (_partitionListView == null || _payloadService == null || !_payloadService.IsLoaded) return;

            try
            {
                if (_partitionListView.InvokeRequired)
                {
                    _partitionListView.BeginInvoke(new Action(UpdatePartitionListFromPayloadInternal));
                }
                else
                {
                    UpdatePartitionListFromPayloadInternal();
                }
            }
            catch { }
        }

        private void UpdatePartitionListFromPayloadInternal()
        {
            try
            {
                _partitionListView.Items.Clear();

                foreach (var partition in _payloadService.Partitions)
                {
                    var item = new ListViewItem(new string[]
                    {
                        partition.Name,
                        "提取",  // 操作列
                        partition.SizeFormatted,
                        partition.CompressedSizeFormatted  // 压缩大小
                    });

                    item.Tag = partition;
                    item.Checked = true;  // 默认勾选

                    // 标记常用分区
                    string name = partition.Name.ToLowerInvariant();
                    if (name.Contains("system") || name.Contains("vendor") || name.Contains("product"))
                    {
                        item.ForeColor = Color.Blue;
                    }
                    else if (name.Contains("boot") || name.Contains("dtbo") || name.Contains("vbmeta"))
                    {
                        item.ForeColor = Color.DarkGreen;
                    }

                    _partitionListView.Items.Add(item);
                }
            }
            catch { }
        }

        /// <summary>
        /// 提取选中的 Payload 分区
        /// </summary>
        public async Task<bool> ExtractSelectedPayloadPartitionsAsync(string outputDir)
        {
            if (_payloadService == null || !_payloadService.IsLoaded)
            {
                Log("请先加载 Payload 文件", Color.Orange);
                return false;
            }

            if (string.IsNullOrEmpty(outputDir))
            {
                Log("请指定输出目录", Color.Orange);
                return false;
            }

            if (IsBusy)
            {
                Log("操作进行中", Color.Orange);
                return false;
            }

            // 获取选中的分区名称
            var selectedNames = new List<string>();
            try
            {
                foreach (ListViewItem item in _partitionListView.CheckedItems)
                {
                    if (item.Tag is PayloadPartition partition)
                    {
                        selectedNames.Add(partition.Name);
                    }
                }
            }
            catch { }

            if (selectedNames.Count == 0)
            {
                Log("请选择要提取的分区", Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("提取 Payload 分区");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                UpdateLabelSafe(_speedLabel, "速度：准备中...");

                Log($"开始提取 {selectedNames.Count} 个分区到: {outputDir}", Color.Blue);

                int success = 0;
                int total = selectedNames.Count;

                for (int i = 0; i < total; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    string name = selectedNames[i];
                    string outputPath = Path.Combine(outputDir, $"{name}.img");

                    UpdateLabelSafe(_operationLabel, $"当前操作：提取 {name} ({i + 1}/{total})");
                    // 总进度：基于已完成的分区数
                    UpdateProgressBar((i * 100.0) / total);
                    // 子进度：开始提取
                    UpdateSubProgressBar(0);

                    if (await _payloadService.ExtractPartitionAsync(name, outputPath, _cts.Token))
                    {
                        success++;
                        Log($"提取成功: {name}.img", Color.Green);
                    }
                    else
                    {
                        Log($"提取失败: {name}", Color.Red);
                    }
                    
                    // 子进度：当前分区提取完成
                    UpdateSubProgressBar(100);
                }

                UpdateProgressBar(100);
                UpdateSubProgressBar(100);
                StopOperationTimer();

                Log($"提取完成: {success}/{total} 成功", success == total ? Color.Green : Color.Orange);

                return success == total;
            }
            catch (OperationCanceledException)
            {
                Log("提取操作已取消", Color.Orange);
                UpdateProgressBar(0);
                StopOperationTimer();
                return false;
            }
            catch (Exception ex)
            {
                Log($"提取失败: {ex.Message}", Color.Red);
                UpdateProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 提取 Payload 分区并直接刷写到设备
        /// </summary>
        public async Task<bool> FlashFromPayloadAsync()
        {
            if (_payloadService == null || !_payloadService.IsLoaded)
            {
                Log("请先加载 Payload 文件", Color.Orange);
                return false;
            }

            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (!await EnsureConnectedAsync()) return false;

            // 获取选中的分区
            var selectedPartitions = new List<PayloadPartition>();
            try
            {
                foreach (ListViewItem item in _partitionListView.CheckedItems)
                {
                    if (item.Tag is PayloadPartition partition)
                    {
                        selectedPartitions.Add(partition);
                    }
                }
            }
            catch { }

            if (selectedPartitions.Count == 0)
            {
                Log("请选择要刷写的分区", Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("Payload 刷写");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                UpdateLabelSafe(_speedLabel, "速度：准备中...");

                Log($"开始从 Payload 刷写 {selectedPartitions.Count} 个分区...", Color.Blue);

                int success = 0;
                int total = selectedPartitions.Count;
                string tempDir = Path.Combine(Path.GetTempPath(), $"payload_flash_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    for (int i = 0; i < total; i++)
                    {
                        _cts.Token.ThrowIfCancellationRequested();

                        var partition = selectedPartitions[i];
                        string tempPath = Path.Combine(tempDir, $"{partition.Name}.img");

                        UpdateLabelSafe(_operationLabel, $"当前操作：提取+刷写 {partition.Name} ({i + 1}/{total})");
                        // 总进度：基于已完成的分区数
                        UpdateProgressBar((i * 100.0) / total);
                        // 子进度：开始提取
                        UpdateSubProgressBar(0);

                        // 1. 提取分区
                        Log($"提取 {partition.Name}...", Color.Blue);
                        // 子进度：提取阶段 (0-50%)
                        UpdateSubProgressBar(10);
                        if (!await _payloadService.ExtractPartitionAsync(partition.Name, tempPath, _cts.Token))
                        {
                            Log($"提取 {partition.Name} 失败，跳过刷写", Color.Red);
                            continue;
                        }
                        
                        // 子进度：提取完成 (50%)
                        UpdateSubProgressBar(50);

                        // 2. 刷写分区
                        Log($"刷写 {partition.Name}...", Color.Blue);
                        var flashStart = DateTime.Now;
                        var fileSize = new FileInfo(tempPath).Length;
                        
                        if (await _service.FlashPartitionAsync(partition.Name, tempPath, false, _cts.Token))
                        {
                            success++;
                            Log($"刷写成功: {partition.Name}", Color.Green);
                            
                            // 计算并显示刷写速度
                            var elapsed = (DateTime.Now - flashStart).TotalSeconds;
                            if (elapsed > 0)
                            {
                                double speed = fileSize / elapsed;
                                UpdateSpeedLabel(FormatSpeed(speed));
                            }
                        }
                        else
                        {
                            Log($"刷写失败: {partition.Name}", Color.Red);
                        }
                        
                        // 子进度：刷写完成 (100%)
                        UpdateSubProgressBar(100);

                        // 3. 删除临时文件
                        try { File.Delete(tempPath); } catch { }
                    }
                }
                finally
                {
                    // 清理临时目录
                    try { Directory.Delete(tempDir, true); } catch { }
                }

                UpdateProgressBar(100);
                UpdateSubProgressBar(100);
                StopOperationTimer();

                Log($"Payload 刷写完成: {success}/{total} 成功", success == total ? Color.Green : Color.Orange);

                // 执行刷写后附加操作（切换槽位、擦除谷歌锁等）
                if (success > 0)
                {
                    await ExecutePostFlashOperationsAsync();
                }

                // 自动重启
                if (IsAutoRebootEnabled() && success > 0)
                {
                    await _service.RebootAsync(_cts.Token);
                }

                return success == total;
            }
            catch (OperationCanceledException)
            {
                Log("刷写操作已取消", Color.Orange);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            catch (Exception ex)
            {
                Log($"刷写失败: {ex.Message}", Color.Red);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 从云端 Payload 直接刷写分区到设备
        /// </summary>
        public async Task<bool> FlashFromRemotePayloadAsync()
        {
            if (_remotePayloadService == null || !_remotePayloadService.IsLoaded)
            {
                Log("请先解析云端 Payload", Color.Orange);
                return false;
            }

            if (IsBusy) { Log("操作进行中", Color.Orange); return false; }
            if (!await EnsureConnectedAsync()) return false;

            // 获取选中的分区
            var selectedPartitions = new List<RemotePayloadPartition>();
            try
            {
                foreach (ListViewItem item in _partitionListView.CheckedItems)
                {
                    if (item.Tag is RemotePayloadPartition partition)
                    {
                        selectedPartitions.Add(partition);
                    }
                }
            }
            catch { }

            if (selectedPartitions.Count == 0)
            {
                Log("请选择要刷写的分区", Color.Orange);
                return false;
            }

            try
            {
                IsBusy = true;
                ResetCancellationToken();

                StartOperationTimer("云端 Payload 刷写");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                UpdateLabelSafe(_speedLabel, "速度：准备中...");

                Log($"开始从云端刷写 {selectedPartitions.Count} 个分区...", Color.Blue);

                int success = 0;
                int total = selectedPartitions.Count;

                // 注册流式刷写进度事件
                EventHandler<RemotePayloadService.StreamFlashProgressEventArgs> progressHandler = (s, e) =>
                {
                    // 总进度：基于已完成的分区数 + 当前分区的进度
                    double overallPercent = ((success * 100.0) + e.Percent) / total;
                    UpdateProgressBar(overallPercent);
                    // 子进度：当前分区的操作进度
                    UpdateSubProgressBar(e.Percent);
                    
                    // 根据阶段显示不同的速度
                    if (e.Phase == RemotePayloadService.StreamFlashPhase.Downloading)
                    {
                        UpdateSpeedLabel($"{e.DownloadSpeedFormatted} (下载)");
                    }
                    else if (e.Phase == RemotePayloadService.StreamFlashPhase.Flashing)
                    {
                        UpdateSpeedLabel($"{e.FlashSpeedFormatted} (刷写)");
                    }
                    else if (e.Phase == RemotePayloadService.StreamFlashPhase.Completed && e.FlashSpeedBytesPerSecond > 0)
                    {
                        UpdateSpeedLabel($"{e.FlashSpeedFormatted} (Fastboot)");
                    }
                };

                _remotePayloadService.StreamFlashProgressChanged += progressHandler;

                try
                {
                    for (int i = 0; i < total; i++)
                    {
                        _cts.Token.ThrowIfCancellationRequested();

                        var partition = selectedPartitions[i];
                        
                        UpdateLabelSafe(_operationLabel, $"当前操作：下载+刷写 {partition.Name} ({i + 1}/{total})");

                        // 使用流式刷写
                        bool flashResult = await _remotePayloadService.ExtractAndFlashPartitionAsync(
                            partition.Name,
                            async (tempPath) =>
                            {
                                // 刷写回调 - 测量 Fastboot 通讯速度
                                var flashStartTime = DateTime.Now;
                                var fileInfo = new FileInfo(tempPath);
                                long fileSize = fileInfo.Length;
                                
                                bool flashSuccess = await _service.FlashPartitionAsync(
                                    partition.Name, tempPath, false, _cts.Token);
                                
                                var flashElapsed = (DateTime.Now - flashStartTime).TotalSeconds;
                                
                                return (flashSuccess, fileSize, flashElapsed);
                            },
                            _cts.Token
                        );

                        if (flashResult)
                        {
                            success++;
                            Log($"刷写成功: {partition.Name}", Color.Green);
                        }
                        else
                        {
                            Log($"刷写失败: {partition.Name}", Color.Red);
                        }
                    }
                }
                finally
                {
                    _remotePayloadService.StreamFlashProgressChanged -= progressHandler;
                }

                UpdateProgressBar(100);
                StopOperationTimer();

                if (success == total)
                {
                    Log($"✓ 全部 {total} 个分区刷写成功", Color.Green);
                }
                else
                {
                    Log($"刷写完成: {success}/{total} 成功", success > 0 ? Color.Orange : Color.Red);
                }

                return success == total;
            }
            catch (OperationCanceledException)
            {
                Log("刷写操作已取消", Color.Orange);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            catch (Exception ex)
            {
                Log($"刷写失败: {ex.Message}", Color.Red);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");
            }
        }

        /// <summary>
        /// 关闭 Payload
        /// </summary>
        public void ClosePayload()
        {
            _payloadService?.Close();
            Log("Payload 已关闭", Color.Gray);
        }

        #endregion

        #region OnePlus/OPPO 刷写流程

        /// <summary>
        /// 刷写配置选项
        /// </summary>
        public class OnePlusFlashOptions
        {
            /// <summary>是否启用 AB 通刷模式 (同时刷写 A/B 两个槽位)</summary>
            public bool ABFlashMode { get; set; } = false;
            
            /// <summary>是否启用强力线刷模式 (额外处理 Super 分区)</summary>
            public bool PowerFlashMode { get; set; } = false;
            
            /// <summary>是否启用纯 FBD 模式 (全部在 FastbootD 下刷写)</summary>
            public bool PureFBDMode { get; set; } = false;
            
            /// <summary>是否清除数据</summary>
            public bool ClearData { get; set; } = false;
            
            /// <summary>是否擦除 FRP (谷歌锁)</summary>
            public bool EraseFrp { get; set; } = true;
            
            /// <summary>是否自动重启</summary>
            public bool AutoReboot { get; set; } = false;
            
            /// <summary>目标槽位 (AB 通刷时使用，a 或 b)</summary>
            public string TargetSlot { get; set; } = "a";
        }

        /// <summary>
        /// 刷写分区信息
        /// </summary>
        public class OnePlusFlashPartition
        {
            public string PartitionName { get; set; }
            public string FilePath { get; set; }
            public long FileSize { get; set; }
            public bool IsLogical { get; set; }
            public bool IsModem { get; set; }
            
            /// <summary>是否来自 Payload.bin (需要先提取)</summary>
            public bool IsPayloadPartition { get; set; }
            /// <summary>Payload 分区信息 (用于提取)</summary>
            public PayloadPartition PayloadInfo { get; set; }
            /// <summary>远程 Payload 分区信息</summary>
            public RemotePayloadPartition RemotePayloadInfo { get; set; }
            
            public string FileSizeFormatted
            {
                get
                {
                    if (FileSize >= 1024L * 1024 * 1024)
                        return $"{FileSize / (1024.0 * 1024 * 1024):F2} GB";
                    if (FileSize >= 1024 * 1024)
                        return $"{FileSize / (1024.0 * 1024):F2} MB";
                    if (FileSize >= 1024)
                        return $"{FileSize / 1024.0:F2} KB";
                    return $"{FileSize} B";
                }
            }
        }

        /// <summary>
        /// 执行 OnePlus/OPPO 刷写流程
        /// </summary>
        public async Task<bool> ExecuteOnePlusFlashAsync(
            List<OnePlusFlashPartition> partitions,
            OnePlusFlashOptions options,
            CancellationToken ct = default)
        {
            if (partitions == null || partitions.Count == 0)
            {
                Log("错误：未选择任何分区进行刷写", Color.Red);
                return false;
            }

            if (IsBusy)
            {
                Log("操作进行中", Color.Orange);
                return false;
            }

            if (!await EnsureConnectedAsync())
                return false;

            // 临时提取目录 (用于 Payload 分区提取)
            string extractDir = null;

            try
            {
                IsBusy = true;
                ResetCancellationToken();
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
                ct = linkedCts.Token;

                StartOperationTimer("OnePlus 刷写");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);

                // 计算总刷写字节数
                long totalFlashBytes = partitions.Sum(p => p.FileSize);
                long currentFlashedBytes = 0;
                string totalSizeStr = FormatSize(totalFlashBytes);

                Log($"开始 OnePlus 刷写流程，共 {partitions.Count} 个分区...", Color.Blue);

                // 步骤 1: 检测设备状态
                Log("正在检测设备连接状态...", Color.Blue);
                UpdateLabelSafe(_operationLabel, "当前操作：检测设备");

                bool isFastbootd = await _service.IsFastbootdModeAsync(ct);
                Log($"设备模式: {(isFastbootd ? "FastbootD" : "Fastboot")}", Color.Green);

                // 步骤 2: 如果不在 FastbootD 模式，需要切换
                if (!isFastbootd)
                {
                    Log("正在重启到 FastbootD 模式...", Color.Blue);
                    UpdateLabelSafe(_operationLabel, "当前操作：重启到 FastbootD");

                    if (!await _service.RebootFastbootdAsync(ct))
                    {
                        Log("无法重启到 FastbootD 模式", Color.Red);
                        return false;
                    }

                    // 等待设备重新连接
                    Log("等待设备重新连接...", Color.Blue);
                    bool reconnected = await WaitForDeviceReconnectAsync(60, ct);
                    if (!reconnected)
                    {
                        Log("设备在 60 秒内未能重新连接", Color.Red);
                        return false;
                    }

                    Log("FastbootD 设备已连接", Color.Green);
                }

                // 步骤 3: 删除 COW 快照分区
                Log("正在解析 COW 快照分区...", Color.Blue);
                UpdateLabelSafe(_operationLabel, "当前操作：清理 COW 分区");
                await _service.DeleteCowPartitionsAsync(ct);
                Log("COW 分区清理完成", Color.Green);

                // 步骤 4: 获取当前槽位
                string currentSlot = await _service.GetCurrentSlotAsync(ct);
                if (string.IsNullOrEmpty(currentSlot)) currentSlot = "a";
                Log($"当前槽位: {currentSlot.ToUpper()}", Color.Blue);

                // 步骤 5: AB 通刷模式预处理
                if (options.ABFlashMode)
                {
                    Log($"AB 通刷模式：目标槽位 {options.TargetSlot.ToUpper()}", Color.Blue);

                    // 如果当前槽位与目标不同，需要切换并重建分区
                    if (!string.Equals(currentSlot, options.TargetSlot, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"切换槽位到 {options.TargetSlot.ToUpper()}...", Color.Blue);
                        await _service.SetActiveSlotAsync(options.TargetSlot, ct);

                        Log("重建逻辑分区结构...", Color.Blue);
                        await _service.RebuildLogicalPartitionsAsync(options.TargetSlot, ct);
                    }
                }

                await Task.Delay(2000, ct);  // 等待设备状态稳定

                // 步骤 6: 对分区排序 (按大小从小到大)
                var sortedPartitions = partitions
                    .Where(p => !string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
                    .OrderBy(p => p.FileSize)
                    .ToList();

                // 纯 FBD 模式：所有分区在 FastbootD 下刷写
                // 普通欧加模式：Modem 分区在 Fastboot 下刷写，其他在 FastbootD 下刷写
                List<OnePlusFlashPartition> fbdPartitions;
                List<OnePlusFlashPartition> modemPartitions;

                if (options.PureFBDMode)
                {
                    // 纯 FBD 模式：所有分区都在 FastbootD 下刷写
                    fbdPartitions = sortedPartitions;
                    modemPartitions = new List<OnePlusFlashPartition>();
                    Log("纯 FBD 模式：所有分区在 FastbootD 下刷写", Color.Blue);
                }
                else
                {
                    // 普通欧加模式：分离 Modem 分区
                    fbdPartitions = sortedPartitions.Where(p => !p.IsModem).ToList();
                    modemPartitions = sortedPartitions.Where(p => p.IsModem).ToList();
                    if (modemPartitions.Count > 0)
                    {
                        Log($"欧加模式：{fbdPartitions.Count} 个分区在 FastbootD，{modemPartitions.Count} 个 Modem 分区在 Fastboot", Color.Blue);
                    }
                }

                int totalPartitions = options.ABFlashMode 
                    ? fbdPartitions.Sum(p => p.IsLogical ? 1 : 2) + modemPartitions.Count * 2
                    : sortedPartitions.Count;
                int currentPartitionIndex = 0;

                // 步骤 6.5: 检查并提取 Payload 分区
                var payloadPartitions = sortedPartitions.Where(p => p.IsPayloadPartition).ToList();
                if (payloadPartitions.Count > 0)
                {
                    Log($"检测到 {payloadPartitions.Count} 个 Payload 分区，正在提取...", Color.Blue);
                    UpdateLabelSafe(_operationLabel, "当前操作：提取 Payload 分区");

                    // 创建临时提取目录
                    extractDir = Path.Combine(Path.GetTempPath(), $"payload_extract_{DateTime.Now:yyyyMMdd_HHmmss}");
                    Directory.CreateDirectory(extractDir);

                    foreach (var pp in payloadPartitions)
                    {
                        ct.ThrowIfCancellationRequested();

                        string extractedPath = Path.Combine(extractDir, $"{pp.PartitionName}.img");

                        if (pp.PayloadInfo != null && _payloadService != null)
                        {
                            // 本地 Payload 提取 (通过分区名)
                            Log($"  提取 {pp.PartitionName}...", null);
                            bool extracted = await _payloadService.ExtractPartitionAsync(
                                pp.PartitionName, extractedPath, ct);

                            if (extracted && File.Exists(extractedPath))
                            {
                                pp.FilePath = extractedPath;
                                Log($"  ✓ {pp.PartitionName} 提取完成", Color.Green);
                            }
                            else
                            {
                                Log($"  ✗ {pp.PartitionName} 提取失败", Color.Red);
                            }
                        }
                        else if (pp.RemotePayloadInfo != null && _remotePayloadService != null)
                        {
                            // 远程 Payload 提取 (通过分区名)
                            Log($"  下载并提取 {pp.PartitionName}...", null);
                            bool extracted = await _remotePayloadService.ExtractPartitionAsync(
                                pp.PartitionName, extractedPath, ct);

                            if (extracted && File.Exists(extractedPath))
                            {
                                pp.FilePath = extractedPath;
                                Log($"  ✓ {pp.PartitionName} 下载并提取完成", Color.Green);
                            }
                            else
                            {
                                Log($"  ✗ {pp.PartitionName} 下载或提取失败", Color.Red);
                            }
                        }
                    }

                    // 更新文件大小 (提取后的实际大小)
                    foreach (var pp in payloadPartitions)
                    {
                        if (!string.IsNullOrEmpty(pp.FilePath) && File.Exists(pp.FilePath))
                        {
                            pp.FileSize = new FileInfo(pp.FilePath).Length;
                        }
                    }

                    // 重新计算总字节数
                    totalFlashBytes = sortedPartitions
                        .Where(p => !string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
                        .Sum(p => p.FileSize);
                    totalSizeStr = FormatSize(totalFlashBytes);
                }

                Log($"开始刷写 {sortedPartitions.Count} 个分区 (总大小: {totalSizeStr})...", Color.Blue);

                // 步骤 7: 在 FastbootD 模式下刷写分区
                foreach (var partition in fbdPartitions)
                {
                    ct.ThrowIfCancellationRequested();

                    // 跳过没有文件的分区
                    if (string.IsNullOrEmpty(partition.FilePath) || !File.Exists(partition.FilePath))
                    {
                        Log($"  ⚠ {partition.PartitionName} 无有效文件，跳过", Color.Orange);
                        continue;
                    }

                    string fileName = Path.GetFileName(partition.FilePath);
                    string targetSlot = options.ABFlashMode ? options.TargetSlot : currentSlot;

                    if (options.ABFlashMode && !partition.IsLogical)
                    {
                        // 非逻辑分区在 AB 通刷模式下需要刷写两个槽位
                        foreach (var slot in new[] { "a", "b" })
                        {
                            string targetName = $"{partition.PartitionName}_{slot}";
                            UpdateLabelSafe(_operationLabel, $"当前操作：刷写 {targetName}");
                            Log($"[写入镜像] {fileName} -> {targetName}", null);

                            long bytesBeforeThis = currentFlashedBytes;
                            Action<long, long> progressCallback = (sent, total) =>
                            {
                                long globalBytes = bytesBeforeThis + sent;
                                double percent = totalFlashBytes > 0 ? globalBytes * 100.0 / totalFlashBytes : 0;
                                UpdateProgressBar(percent);
                                UpdateSubProgressBar(total > 0 ? sent * 100.0 / total : 0);
                                UpdateSpeedLabel(FormatSpeed(_currentSpeed));
                            };

                            bool ok = await _service.FlashPartitionToSlotAsync(
                                partition.PartitionName, partition.FilePath, slot, progressCallback, ct);

                            if (ok)
                            {
                                Log($"  ✓ {targetName} 成功", Color.Green);
                            }
                            else
                            {
                                Log($"  ✗ {targetName} 失败", Color.Red);
                            }

                            currentFlashedBytes += partition.FileSize / 2;  // AB 模式下每个槽位算一半
                            currentPartitionIndex++;
                        }
                    }
                    else
                    {
                        // 逻辑分区或普通模式只刷一个槽位
                        string targetName = $"{partition.PartitionName}_{targetSlot}";
                        UpdateLabelSafe(_operationLabel, $"当前操作：刷写 {targetName}");
                        Log($"[写入镜像] {fileName} -> {targetName}", null);

                        long bytesBeforeThis = currentFlashedBytes;
                        Action<long, long> progressCallback = (sent, total) =>
                        {
                            long globalBytes = bytesBeforeThis + sent;
                            double percent = totalFlashBytes > 0 ? globalBytes * 100.0 / totalFlashBytes : 0;
                            UpdateProgressBar(percent);
                            UpdateSubProgressBar(total > 0 ? sent * 100.0 / total : 0);
                        };

                        bool ok = await _service.FlashPartitionToSlotAsync(
                            partition.PartitionName, partition.FilePath, targetSlot, progressCallback, ct);

                        if (ok)
                        {
                            Log($"  ✓ {targetName} 成功", Color.Green);
                        }
                        else
                        {
                            Log($"  ✗ {targetName} 失败", Color.Red);
                        }

                        currentFlashedBytes += partition.FileSize;
                        currentPartitionIndex++;
                    }
                }

                // 步骤 8: 如果有 Modem 分区（普通欧加模式），重启到 Bootloader 刷写
                if (modemPartitions.Count > 0 && !options.PureFBDMode)
                {
                    Log("Modem 分区需要在 Fastboot 模式下刷写...", Color.Blue);
                    UpdateLabelSafe(_operationLabel, "当前操作：重启到 Fastboot");

                    if (!await _service.RebootBootloaderAsync(ct))
                    {
                        Log("无法重启到 Fastboot 模式", Color.Red);
                    }
                    else
                    {
                        // 等待设备重新连接
                        bool reconnected = await WaitForDeviceReconnectAsync(60, ct);
                        if (reconnected)
                        {
                            foreach (var modem in modemPartitions)
                            {
                                ct.ThrowIfCancellationRequested();

                                // 跳过没有文件的分区
                                if (string.IsNullOrEmpty(modem.FilePath) || !File.Exists(modem.FilePath))
                                {
                                    Log($"  ⚠ {modem.PartitionName} 无有效文件，跳过", Color.Orange);
                                    continue;
                                }

                                string fileName = Path.GetFileName(modem.FilePath);

                                // Modem 分区在 AB 通刷模式下也刷两个槽位
                                foreach (var slot in options.ABFlashMode ? new[] { "a", "b" } : new[] { currentSlot })
                                {
                                    string targetName = $"{modem.PartitionName}_{slot}";
                                    UpdateLabelSafe(_operationLabel, $"当前操作：刷写 {targetName}");
                                    Log($"[写入镜像] {fileName} -> {targetName}", null);

                                    bool ok = await _service.FlashPartitionToSlotAsync(
                                        modem.PartitionName, modem.FilePath, slot, null, ct);

                                    Log(ok ? $"  ✓ {targetName} 成功" : $"  ✗ {targetName} 失败",
                                        ok ? Color.Green : Color.Red);
                                }
                            }

                            // 刷完 Modem 后如果需要清数据，需要回到 FastbootD
                            if (options.ClearData || options.EraseFrp)
                            {
                                Log("重启到 FastbootD 继续后续操作...", Color.Blue);
                                await _service.RebootFastbootdAsync(ct);
                                await WaitForDeviceReconnectAsync(60, ct);
                            }
                        }
                    }
                }

                // 步骤 9: 擦除 FRP (谷歌锁) - 所有设备都可自动执行
                if (options.EraseFrp)
                {
                    Log("正在擦除 FRP...", Color.Blue);
                    UpdateLabelSafe(_operationLabel, "当前操作：擦除 FRP");
                    bool frpOk = await _service.EraseFrpAsync(ct);
                    Log(frpOk ? "FRP 擦除成功" : "FRP 擦除失败", frpOk ? Color.Green : Color.Orange);
                }

                // 步骤 10: 清除数据 - 仅高通设备自动执行，联发科需手动
                if (options.ClearData)
                {
                    // 检测设备平台：高通 (abl) vs 联发科 (lk)
                    var devicePlatform = await _service.GetDevicePlatformAsync(ct);
                    bool isQualcommDevice = devicePlatform == FastbootService.DevicePlatform.Qualcomm;
                    
                    if (isQualcommDevice)
                    {
                        Log("正在清除用户数据...", Color.Blue);
                        UpdateLabelSafe(_operationLabel, "当前操作：清除数据");
                        bool wipeOk = await _service.WipeDataAsync(ct);
                        Log(wipeOk ? "数据清除成功" : "数据清除失败", wipeOk ? Color.Green : Color.Orange);
                    }
                    else
                    {
                        // 联发科设备 (lk) 需要用户手动在 Recovery 清除数据
                        Log("⚠ 联发科设备请手动清除数据 (进入 Recovery -> Wipe data/factory reset)", Color.Orange);
                    }
                }

                // 步骤 11: 自动重启
                if (options.AutoReboot)
                {
                    Log("正在重启设备...", Color.Blue);
                    UpdateLabelSafe(_operationLabel, "当前操作：重启");
                    await _service.RebootAsync(ct);
                }

                UpdateProgressBar(100);
                UpdateSubProgressBar(100);
                StopOperationTimer();

                Log("✓ OnePlus 刷写流程完成", Color.Green);
                return true;
            }
            catch (OperationCanceledException)
            {
                Log("刷写操作已取消", Color.Orange);
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            catch (Exception ex)
            {
                Log($"刷写过程中发生错误: {ex.Message}", Color.Red);
                _logDetail($"OnePlus 刷写异常: {ex}");
                UpdateProgressBar(0);
                UpdateSubProgressBar(0);
                StopOperationTimer();
                return false;
            }
            finally
            {
                IsBusy = false;
                UpdateLabelSafe(_operationLabel, "当前操作：空闲");

                // 清理临时提取目录
                if (!string.IsNullOrEmpty(extractDir) && Directory.Exists(extractDir))
                {
                    try
                    {
                        Directory.Delete(extractDir, true);
                        _logDetail($"已清理临时目录: {extractDir}");
                    }
                    catch (Exception ex)
                    {
                        _logDetail($"清理临时目录失败: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 等待设备重新连接
        /// </summary>
        private async Task<bool> WaitForDeviceReconnectAsync(int timeoutSeconds, CancellationToken ct)
        {
            int attempts = timeoutSeconds / 5;
            for (int i = 0; i < attempts; i++)
            {
                await Task.Delay(5000, ct);
                
                // 刷新设备列表
                await RefreshDeviceListAsync();
                
                if (_cachedDevices != null && _cachedDevices.Count > 0)
                {
                    // 尝试自动连接第一个设备
                    var device = _cachedDevices[0];
                    _service = new FastbootService(
                        msg => Log(msg, null),
                        (current, total) => UpdateProgressWithSpeed(current, total),
                        _logDetail
                    );
                    _service.FlashProgressChanged += OnFlashProgressChanged;
                    
                    if (await _service.SelectDeviceAsync(device.Serial, ct))
                    {
                        return true;
                    }
                }
                
                Log($"等待设备... ({(i + 1) * 5}/{timeoutSeconds}s)", null);
            }
            return false;
        }

        /// <summary>
        /// 从当前选中的分区构建 OnePlus 刷写分区列表
        /// 支持: Payload 分区、解包文件夹、脚本任务、普通镜像
        /// </summary>
        public List<OnePlusFlashPartition> BuildOnePlusFlashPartitions()
        {
            var result = new List<OnePlusFlashPartition>();

            if (_partitionListView == null) return result;

            try
            {
                foreach (ListViewItem item in _partitionListView.CheckedItems)
                {
                    string partName = item.SubItems[0].Text;
                    string filePath = item.SubItems.Count > 3 ? item.SubItems[3].Text : "";

                    // 本地 Payload 分区 (需要先提取)
                    if (item.Tag is PayloadPartition payloadPart)
                    {
                        result.Add(new OnePlusFlashPartition
                        {
                            PartitionName = payloadPart.Name,
                            FilePath = null,  // 稍后提取时设置
                            FileSize = (long)payloadPart.Size,  // 解压后大小
                            IsLogical = FastbootService.IsLogicalPartition(payloadPart.Name),
                            IsModem = FastbootService.IsModemPartition(payloadPart.Name),
                            IsPayloadPartition = true,
                            PayloadInfo = payloadPart
                        });
                        continue;
                    }

                    // 远程 Payload 分区 (云端边下边刷)
                    if (item.Tag is RemotePayloadPartition remotePart)
                    {
                        result.Add(new OnePlusFlashPartition
                        {
                            PartitionName = remotePart.Name,
                            FilePath = null,
                            FileSize = (long)remotePart.Size,  // 解压后大小
                            IsLogical = FastbootService.IsLogicalPartition(remotePart.Name),
                            IsModem = FastbootService.IsModemPartition(remotePart.Name),
                            IsPayloadPartition = true,
                            RemotePayloadInfo = remotePart
                        });
                        continue;
                    }

                    // 已提取文件夹中的镜像
                    if (item.Tag is ExtractedImageInfo extractedInfo)
                    {
                        result.Add(new OnePlusFlashPartition
                        {
                            PartitionName = extractedInfo.PartitionName,
                            FilePath = extractedInfo.FilePath,
                            FileSize = extractedInfo.FileSize,
                            IsLogical = extractedInfo.IsLogical,
                            IsModem = extractedInfo.IsModem,
                            IsPayloadPartition = false
                        });
                        continue;
                    }

                    // 脚本任务 (flash_all.bat 解析)
                    if (item.Tag is BatScriptParser.FlashTask task)
                    {
                        if (task.Operation == "flash" && task.ImageExists)
                        {
                            result.Add(new OnePlusFlashPartition
                            {
                                PartitionName = task.PartitionName,
                                FilePath = task.ImagePath,
                                FileSize = task.FileSize,
                                IsLogical = FastbootService.IsLogicalPartition(task.PartitionName),
                                IsModem = FastbootService.IsModemPartition(task.PartitionName),
                                IsPayloadPartition = false
                            });
                        }
                        continue;
                    }

                    // 普通分区 (已有镜像文件)
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        var fileInfo = new FileInfo(filePath);
                        result.Add(new OnePlusFlashPartition
                        {
                            PartitionName = partName,
                            FilePath = filePath,
                            FileSize = fileInfo.Length,
                            IsLogical = FastbootService.IsLogicalPartition(partName),
                            IsModem = FastbootService.IsModemPartition(partName),
                            IsPayloadPartition = false
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logDetail($"构建刷写分区列表失败: {ex.Message}");
            }

            return result;
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                StopDeviceMonitoring();
                _deviceRefreshTimer?.Dispose();
                _service?.Dispose();
                _payloadService?.Dispose();
                _remotePayloadService?.Dispose();
                _cts?.Dispose();
                _disposed = true;
            }
        }
    }
}
