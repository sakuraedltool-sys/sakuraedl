// ============================================================================
// SakuraEDL - Qualcomm Service | 高通服务
// ============================================================================
// [ZH] 高通刷写服务 - 整合 Sahara 和 Firehose 协议的高层 API
// [EN] Qualcomm Flash Service - High-level API integrating Sahara and Firehose
// [JA] Qualcommフラッシュサービス - SaharaとFirehoseを統合した高レベルAPI
// [KO] Qualcomm 플래싱 서비스 - Sahara와 Firehose를 통합한 고수준 API
// [RU] Сервис прошивки Qualcomm - Высокоуровневый API для Sahara и Firehose
// [ES] Servicio de flasheo Qualcomm - API de alto nivel para Sahara y Firehose
// ============================================================================
// Features: Device connection, partition R/W, flash workflow management
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SakuraEDL.Common;
using SakuraEDL.Qualcomm.Common;
using SakuraEDL.Qualcomm.Database;
using SakuraEDL.Qualcomm.Models;
using SakuraEDL.Qualcomm.Protocol;
using SakuraEDL.Qualcomm.Authentication;
// 已合并到 SakuraEDL.Qualcomm.Common 和 SakuraEDL.Qualcomm.Protocol

namespace SakuraEDL.Qualcomm.Services
{
    /// <summary>
    /// 连接状态
    /// </summary>
    public enum QualcommConnectionState
    {
        Disconnected,
        Connecting,
        SaharaMode,
        FirehoseMode,
        Ready,
        Error
    }

    /// <summary>
    /// 高通刷写服务
    /// </summary>
    public class QualcommService : IDisposable
    {
        private SerialPortManager _portManager;
        private SaharaClient _sahara;
        private FirehoseClient _firehose;
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;  // 详细调试日志 (只写入文件)
        private readonly Action<long, long> _progress;
        private readonly OplusSuperFlashManager _oplusSuperManager;
        private readonly DeviceInfoService _deviceInfoService;
        private bool _disposed;
        
        // 看门狗机制
        private Watchdog _watchdog;

        // 状态
        public QualcommConnectionState State { get; private set; }
        public QualcommChipInfo ChipInfo { get { return _sahara != null ? _sahara.ChipInfo : null; } }
        public uint SaharaProtocolVersion { get { return _sahara != null ? _sahara.ProtocolVersion : 0; } }
        public bool IsVipDevice { get; private set; }
        public string StorageType { get { return _firehose != null ? _firehose.StorageType : "ufs"; } }
        public int SectorSize { get { return _firehose != null ? _firehose.SectorSize : 4096; } }
        public string CurrentSlot { get { return _firehose != null ? _firehose.CurrentSlot : "nonexistent"; } }
        
        // 最后使用的连接参数 (用于状态显示)
        public string LastPortName { get; private set; }
        public string LastStorageType { get; private set; }

        // 分区缓存
        private Dictionary<int, List<PartitionInfo>> _partitionCache;
        
        // 端口管理标志位 (用于操作完成后释放端口)
        private bool _portClosed = false;          // 端口是否已关闭
        private bool _keepPortOpen = false;        // 是否保持端口打开 (用于连续操作)
        private QualcommChipInfo _cachedChipInfo;  // 缓存的芯片信息 (端口关闭后保留)
        
        // 新增: Diag 客户端、Loader 检测器、Motorola 支持
        private DiagClient _diagClient;
        private LoaderFeatureDetector _loaderDetector;
        private MotorolaSupport _motorolaSupport;
        private LoaderFeatures _loaderFeatures;

        /// <summary>
        /// 状态变化事件
        /// </summary>
        public event EventHandler<QualcommConnectionState> StateChanged;
        
        /// <summary>
        /// 端口断开事件 (设备自己断开时触发)
        /// </summary>
        public event EventHandler PortDisconnected;
        
        /// <summary>
        /// 小米授权令牌事件 (内置签名失败时触发，需要弹窗显示令牌)
        /// Token 格式: VQ 开头的 Base64 字符串
        /// </summary>
        public event Action<string> XiaomiAuthTokenRequired;
        
        /// <summary>
        /// 检查是否真正连接 (会验证端口状态)
        /// </summary>
        public bool IsConnected 
        { 
            get 
            { 
                if (State != QualcommConnectionState.Ready)
                    return false;
                    
                // 验证端口是否真正可用
                if (_portManager == null || !_portManager.ValidateConnection())
                {
                    // 端口已断开，更新状态
                    HandlePortDisconnected();
                    return false;
                }
                return true;
            } 
        }
        
        /// <summary>
        /// 快速检查连接状态 (不验证端口，用于UI高频显示)
        /// </summary>
        public bool IsConnectedFast
        {
            get { return State == QualcommConnectionState.Ready && _portManager != null && _portManager.IsOpen; }
        }
        
        /// <summary>
        /// 验证连接是否有效
        /// </summary>
        public bool ValidateConnection()
        {
            if (State != QualcommConnectionState.Ready)
                return false;
                
            if (_portManager == null)
                return false;
                
            // 检查端口是否在系统中
            if (!_portManager.IsPortAvailable())
            {
                _logDetail("[高通] 端口已从系统中移除");
                HandlePortDisconnected();
                return false;
            }
            
            // 验证端口连接
            if (!_portManager.ValidateConnection())
            {
                _logDetail("[高通] 端口连接验证失败");
                HandlePortDisconnected();
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// 处理端口断开 (设备自己断开)
        /// </summary>
        private void HandlePortDisconnected()
        {
            if (State == QualcommConnectionState.Disconnected)
                return;
                
            _log("[高通] 检测到设备断开");
            
            // 清理资源 (忽略释放异常，确保完整清理)
            if (_portManager != null)
            {
                try { _portManager.Close(); } 
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QualcommService] 关闭端口异常: {ex.Message}"); }
                try { _portManager.Dispose(); } 
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QualcommService] 释放端口异常: {ex.Message}"); }
                _portManager = null;
            }
            
            if (_firehose != null)
            {
                try { _firehose.Dispose(); } 
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QualcommService] 释放 Firehose 异常: {ex.Message}"); }
                _firehose = null;
            }
            
            // 清空分区缓存 (设备断开后缓存无效)
            _partitionCache.Clear();
            
            SetState(QualcommConnectionState.Disconnected);
            PortDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public QualcommService(Action<string> log = null, Action<long, long> progress = null, Action<string> logDetail = null)
        {
            _log = log ?? delegate { };
            _logDetail = logDetail ?? delegate { };
            _progress = progress;
            _oplusSuperManager = new OplusSuperFlashManager(_log);
            _deviceInfoService = new DeviceInfoService(_log, _logDetail);
            _partitionCache = new Dictionary<int, List<PartitionInfo>>();
            State = QualcommConnectionState.Disconnected;
            
            // 初始化看门狗
            _watchdog = new Watchdog("Qualcomm", WatchdogManager.DefaultTimeouts.Qualcomm, _logDetail);
            _watchdog.OnTimeout += OnWatchdogTimeout;
        }
        
        /// <summary>
        /// 看门狗超时处理
        /// </summary>
        private void OnWatchdogTimeout(object sender, WatchdogTimeoutEventArgs e)
        {
            _log($"[高通] 看门狗超时: {e.OperationName} (等待 {e.ElapsedTime.TotalSeconds:F1}秒)");
            
            // 超时次数过多时尝试重置
            if (e.TimeoutCount >= 3)
            {
                _log("[高通] 多次超时，尝试重置连接...");
                e.ShouldReset = false; // 停止看门狗
                
                // 触发端口断开事件
                HandlePortDisconnected();
            }
        }
        
        /// <summary>
        /// 喂狗 - 在长时间操作中调用以重置看门狗计时器
        /// </summary>
        public void FeedWatchdog()
        {
            _watchdog?.Feed();
        }
        
        /// <summary>
        /// 启动看门狗
        /// </summary>
        public void StartWatchdog(string operation)
        {
            _watchdog?.Start(operation);
        }
        
        /// <summary>
        /// 停止看门狗
        /// </summary>
        public void StopWatchdog()
        {
            _watchdog?.Stop();
        }

        #region 连接管理

        /// <summary>
        /// 仅获取 Sahara 设备信息 (不上传 Loader)
        /// 用于云端自动匹配
        /// </summary>
        /// <param name="portName">COM 端口名</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>设备信息，失败返回 null</returns>
        public async Task<QualcommChipInfo> GetSaharaDeviceInfoOnlyAsync(string portName, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                _log("[云端] 获取设备信息...");

                // 初始化串口
                _portManager = new SerialPortManager();

                // Sahara 模式必须保留初始 Hello 包，不清空缓冲区
                bool opened = await _portManager.OpenAsync(portName, 3, false, ct);
                if (!opened)
                {
                    _log("[云端] 无法打开端口");
                    return null;
                }

                // 创建 Sahara 客户端
                _sahara = new SaharaClient(_portManager, _log, _logDetail, null);

                // 仅执行握手获取设备信息 (不上传 Loader)
                bool infoOk = await _sahara.GetDeviceInfoOnlyAsync(ct);
                
                if (!infoOk || _sahara.ChipInfo == null)
                {
                    _log("[云端] 无法获取设备信息");
                    _portManager.Close();
                    return null;
                }

                _log("[云端] 设备信息获取成功");
                
                // 保存芯片信息
                _cachedChipInfo = _sahara.ChipInfo;
                
                // 保持端口打开，后续会继续使用
                return _sahara.ChipInfo;
            }
            catch (Exception ex)
            {
                _log($"[云端] 获取设备信息异常: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// 使用已获取的设备信息继续连接 (上传 Loader)
        /// </summary>
        /// <param name="loaderData">Loader 数据</param>
        /// <param name="storageType">存储类型</param>
        /// <param name="authMode">认证模式</param>
        /// <param name="ct">取消令牌</param>
        public async Task<bool> ContinueConnectWithLoaderAsync(byte[] loaderData, string storageType = "ufs", 
            string authMode = "none", CancellationToken ct = default(CancellationToken))
        {
            try
            {
                if (_sahara == null || _portManager == null)
                {
                    _log("[云端] 请先调用 GetSaharaDeviceInfoOnlyAsync");
                    return false;
                }

                SetState(QualcommConnectionState.SaharaMode);
                
                // 使用已有的 Sahara 客户端继续上传 Loader
                bool uploadOk = await _sahara.UploadLoaderAsync(loaderData, ct);
                if (!uploadOk)
                {
                    _log("[云端] Loader 上传失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 根据用户选择的认证模式设置标志
                IsVipDevice = (authMode.ToLowerInvariant() == "vip" || authMode.ToLowerInvariant() == "oplus");

                // 等待 Firehose 就绪
                _log("正在发送 Firehose 引导文件 : 成功");
                await Task.Delay(1000, ct);

                // 重新打开端口 (Firehose 模式)
                string portName = _portManager.PortName;
                _portManager.Close();
                await Task.Delay(500, ct);

                bool opened = await _portManager.OpenAsync(portName, 5, true, ct);
                if (!opened)
                {
                    _log("[云端] 无法重新打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // Firehose 配置
                SetState(QualcommConnectionState.FirehoseMode);
                _firehose = new FirehoseClient(_portManager, _log, _progress, _logDetail);

                // 传递芯片信息
                if (ChipInfo != null)
                {
                    _firehose.ChipSerial = ChipInfo.SerialHex;
                    _firehose.ChipHwId = ChipInfo.HwIdHex;
                    _firehose.ChipPkHash = ChipInfo.PkHash;
                }

                // 执行认证 (如需要)
                string authModeLower = authMode.ToLowerInvariant();
                if (authModeLower == "xiaomi" || (authModeLower == "none" && IsXiaomiDevice()))
                {
                    _log("[云端] 执行小米认证...");
                    var xiaomi = new XiaomiAuthStrategy(_log);
                    xiaomi.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                    bool authOk = await xiaomi.AuthenticateAsync(_firehose, null, ct);
                    if (authOk)
                        _log("[云端] 小米认证成功");
                    else
                        _log("[云端] 小米认证失败");
                }
                else if (authModeLower == "oneplus")
                {
                    _log("[云端] 执行 OnePlus 认证...");
                    var oneplus = new OnePlusAuthStrategy(_log);
                    bool authOk = await oneplus.AuthenticateAsync(_firehose, null, ct);
                    if (authOk)
                        _log("[云端] OnePlus 认证成功");
                    else
                        _log("[云端] OnePlus 认证失败");
                }

                _log("正在配置 Firehose...");
                bool configOk = await _firehose.ConfigureAsync(storageType, 0, ct);
                if (!configOk)
                {
                    _log("配置 Firehose : 失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }
                _log("配置 Firehose : 成功");

                SetState(QualcommConnectionState.Ready);
                return true;
            }
            catch (Exception ex)
            {
                _log($"[云端] 连接异常: {ex.Message}");
                SetState(QualcommConnectionState.Error);
                return false;
            }
        }

        /// <summary>
        /// 连接设备
        /// </summary>
        /// <param name="portName">COM 端口名</param>
        /// <param name="programmerPath">Programmer 文件路径</param>
        /// <param name="storageType">存储类型 (ufs/emmc)</param>
        /// <param name="authMode">认证模式: none, vip, oneplus, xiaomi</param>
        /// <param name="digestPath">VIP Digest 文件路径</param>
        /// <param name="signaturePath">VIP Signature 文件路径</param>
        /// <param name="ct">取消令牌</param>
        public async Task<bool> ConnectAsync(string portName, string programmerPath, string storageType = "ufs", 
            string authMode = "none", string digestPath = "", string signaturePath = "",
            CancellationToken ct = default(CancellationToken))
        {
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log("等待高通 EDL USB 设备 : 成功");
                _log(string.Format("USB 端口 : {0}", portName));
                _log("正在连接设备 : 成功");

                // 验证 Programmer 文件
                if (!File.Exists(programmerPath))
                {
                    _log("[高通] Programmer 文件不存在: " + programmerPath);
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 初始化串口
                _portManager = new SerialPortManager();

                // Sahara 模式必须保留初始 Hello 包，不清空缓冲区
                bool opened = await _portManager.OpenAsync(portName, 3, false, ct);
                if (!opened)
                {
                    _log("[高通] 无法打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // Sahara 握手
                SetState(QualcommConnectionState.SaharaMode);
                
                // 创建 Sahara 客户端并传递进度回调
                Action<double> saharaProgress = null;
                if (_progress != null)
                {
                    saharaProgress = percent => _progress((long)percent, 100);
                }
                _sahara = new SaharaClient(_portManager, _log, _logDetail, saharaProgress);

                bool saharaOk = await _sahara.HandshakeAndUploadAsync(programmerPath, ct);
                if (!saharaOk)
                {
                    _log("[高通] Sahara 握手失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 根据用户选择的认证模式设置标志 (不再自动检测)
                IsVipDevice = (authMode.ToLowerInvariant() == "vip" || authMode.ToLowerInvariant() == "oplus");

                // 等待 Firehose 就绪
                _log("正在发送 Firehose 引导文件 : 成功");
                await Task.Delay(1000, ct);

                // 重新打开端口 (Firehose 模式)
                _portManager.Close();
                await Task.Delay(500, ct);

                opened = await _portManager.OpenAsync(portName, 5, true, ct);
                if (!opened)
                {
                    _log("[高通] 无法重新打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // Firehose 配置
                SetState(QualcommConnectionState.FirehoseMode);
                _firehose = new FirehoseClient(_portManager, _log, _progress, _logDetail);

                // 传递芯片信息
                if (ChipInfo != null)
                {
                    _firehose.ChipSerial = ChipInfo.SerialHex;
                    _firehose.ChipHwId = ChipInfo.HwIdHex;
                    _firehose.ChipPkHash = ChipInfo.PkHash;
                }

                // 根据用户选择执行认证 (配置前认证)
                string authModeLower = authMode.ToLowerInvariant();
                bool preConfigAuth = (authModeLower == "vip" || authModeLower == "oplus" || authModeLower == "xiaomi");
                
                // 小米设备自动认证：即使用户选择 none，也自动执行小米认证
                bool isXiaomi = IsXiaomiDevice();
                if (authModeLower == "none" && isXiaomi)
                {
                    _log("[高通] 检测到小米设备 (SecBoot)，自动执行 MiAuth 认证...");
                    var xiaomi = new XiaomiAuthStrategy(_log);
                    xiaomi.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                    bool authOk = await xiaomi.AuthenticateAsync(_firehose, programmerPath, ct);
                    if (authOk)
                        _log("[高通] 小米认证成功");
                    else
                        _log("[高通] 小米认证失败，设备可能需要官方授权");
                }
                else if (preConfigAuth && authModeLower != "none")
                {
                    _log(string.Format("[高通] 执行 {0} 认证 (配置前)...", authMode.ToUpper()));
                    bool authOk = false;
                    
                    if (authModeLower == "vip" || authModeLower == "oplus")
                    {
                        // VIP 认证必须在配置前
                        if (!string.IsNullOrEmpty(digestPath) && !string.IsNullOrEmpty(signaturePath))
                        {
                            authOk = await PerformVipAuthManualAsync(digestPath, signaturePath, ct);
                        }
                        else
                        {
                            _log("[高通] VIP 认证需要 Digest 和 Signature 文件，将回退到普通模式");
                            // 没有认证文件，回退到普通模式
                            IsVipDevice = false;
                        }
                    }
                    else if (authModeLower == "xiaomi")
                    {
                        var xiaomi = new XiaomiAuthStrategy(_log);
                        xiaomi.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                        authOk = await xiaomi.AuthenticateAsync(_firehose, programmerPath, ct);
                    }
                    
                    if (authOk)
                    {
                        _log(string.Format("[高通] {0} 认证成功", authMode.ToUpper()));
                    }
                    else if (IsVipDevice)
                    {
                        // VIP 认证失败但有文件，回退到普通模式
                        _log(string.Format("[高通] {0} 认证失败，回退到普通读取模式", authMode.ToUpper()));
                        IsVipDevice = false;
                    }
                }

                _log("正在配置 Firehose...");
                bool configOk = await _firehose.ConfigureAsync(storageType, 0, ct);
                if (!configOk)
                {
                    _log("配置 Firehose : 失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }
                _log("配置 Firehose : 成功");

                // 配置后认证 (OnePlus)
                if (!preConfigAuth && authModeLower != "none")
                {
                    _log(string.Format("[高通] 执行 {0} 认证 (配置后)...", authMode.ToUpper()));
                    bool authOk = false;
                    
                    if (authModeLower == "oneplus")
                    {
                        var oneplus = new OnePlusAuthStrategy(_log);
                        authOk = await oneplus.AuthenticateAsync(_firehose, programmerPath, ct);
                    }
                    
                    if (authOk)
                        _log(string.Format("[高通] {0} 认证成功", authMode.ToUpper()));
                    else
                        _log(string.Format("[高通] {0} 认证失败", authMode.ToUpper()));
                }

                // 保存连接参数
                LastPortName = portName;
                LastStorageType = storageType;
                
                // 注册端口断开事件
                if (_portManager != null)
                {
                    _portManager.PortDisconnected += (s, e) => HandlePortDisconnected();
                }
                
                SetState(QualcommConnectionState.Ready);
                _log("[高通] 连接成功");

                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[高通] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
        }

        /// <summary>
        /// 使用内嵌 Loader 数据连接设备 (VIP 模式，不含认证)
        /// </summary>
        public async Task<bool> ConnectWithLoaderDataAsync(string portName, byte[] loaderData, string storageType = "ufs", CancellationToken ct = default(CancellationToken))
        {
            return await ConnectWithVipAuthAsync(portName, loaderData, "", "", storageType, ct);
        }

        /// <summary>
        /// 使用内嵌 Loader 数据连接并执行 VIP 认证 (使用文件路径方式)
        /// 重要：VIP 认证在 Loader 上传后、Firehose 配置前执行
        /// </summary>
        /// <param name="portName">端口名</param>
        /// <param name="loaderData">Loader 二进制数据</param>
        /// <param name="digestPath">VIP Digest 文件路径 (可选)</param>
        /// <param name="signaturePath">VIP Signature 文件路径 (可选)</param>
        /// <param name="storageType">存储类型</param>
        /// <param name="ct">取消令牌</param>
        public async Task<bool> ConnectWithVipAuthAsync(string portName, byte[] loaderData, string digestPath, string signaturePath, string storageType = "ufs", CancellationToken ct = default(CancellationToken))
        {
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log("[高通] 使用内嵌 Loader 连接...");
                _log(string.Format("USB 端口 : {0}", portName));

                if (loaderData == null || loaderData.Length == 0)
                {
                    _log("[高通] Loader 数据为空");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 初始化串口
                _portManager = new SerialPortManager();
                bool opened = await _portManager.OpenAsync(portName, 3, false, ct);
                if (!opened)
                {
                    _log("[高通] 无法打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // Sahara 握手并上传内嵌 Loader
                SetState(QualcommConnectionState.SaharaMode);
                Action<double> saharaProgress = null;
                if (_progress != null)
                {
                    saharaProgress = percent => _progress((long)percent, 100);
                }
                _sahara = new SaharaClient(_portManager, _log, _logDetail, saharaProgress);
                
                bool saharaOk = await _sahara.HandshakeAndUploadAsync(loaderData, "VIP_Loader", ct);
                if (!saharaOk)
                {
                    _log("[高通] Sahara 握手/Loader 上传失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 芯片信息已通过 _sahara.ChipInfo 保存，ChipInfo 属性会自动获取
                // 注意：IsVipDevice 将在 VIP 认证成功后设置

                // 等待 Firehose 就绪
                _log("正在发送 Firehose 引导文件 : 成功");
                await Task.Delay(1000, ct);

                // 重新打开端口 (Firehose 模式)
                _portManager.Close();
                await Task.Delay(500, ct);

                opened = await _portManager.OpenAsync(portName, 5, true, ct);
                if (!opened)
                {
                    _log("[高通] 无法重新打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 创建 Firehose 客户端
                SetState(QualcommConnectionState.FirehoseMode);
                _firehose = new FirehoseClient(_portManager, _log, _progress, _logDetail);

                // ========== VIP 认证 (关键：必须在 Firehose 配置之前执行) ==========
                // 使用文件路径方式发送二进制数据
                bool vipAuthOk = false;
                if (!string.IsNullOrEmpty(digestPath) && !string.IsNullOrEmpty(signaturePath) &&
                    System.IO.File.Exists(digestPath) && System.IO.File.Exists(signaturePath))
                {
                    var digestInfo = new System.IO.FileInfo(digestPath);
                    var sigInfo = new System.IO.FileInfo(signaturePath);
                    _log(string.Format("[高通] 执行 VIP 认证 (Digest={0}B, Sign={1}B)...", digestInfo.Length, sigInfo.Length));
                    
                    // 使用文件路径方式发送
                    vipAuthOk = await _firehose.PerformVipAuthAsync(digestPath, signaturePath, ct);
                    if (!vipAuthOk)
                    {
                        _log("[高通] VIP 认证失败，回退到普通模式...");
                        IsVipDevice = false;  // 重要：认证失败时使用普通读取模式
                    }
                    else
                    {
                        _log("[高通] VIP 认证成功，已激活高权限模式");
                        IsVipDevice = true;
                    }
                }
                else
                {
                    // 没有提供认证数据或文件不存在，使用普通模式
                    if (!string.IsNullOrEmpty(digestPath) || !string.IsNullOrEmpty(signaturePath))
                    {
                        _log("[高通] VIP 认证文件不存在，使用普通模式");
                    }
                    IsVipDevice = false;
                }

                // Firehose 配置
                _log("正在配置 Firehose...");
                bool configOk = await _firehose.ConfigureAsync(storageType, 0, ct);
                if (!configOk)
                {
                    _log("配置 Firehose : 失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }
                _log("配置 Firehose : 成功");

                // 保存连接参数
                LastPortName = portName;
                LastStorageType = storageType;

                // 注册端口断开事件
                if (_portManager != null)
                {
                    _portManager.PortDisconnected += (s, e) => HandlePortDisconnected();
                }

                SetState(QualcommConnectionState.Ready);
                _log("[高通] VIP Loader 连接成功");

                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[高通] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
        }

        /// <summary>
        /// 使用云端 Loader 数据连接设备 (支持各种认证模式)
        /// </summary>
        /// <param name="portName">端口名</param>
        /// <param name="loaderData">Loader 二进制数据</param>
        /// <param name="storageType">存储类型</param>
        /// <param name="authMode">认证模式: none, vip, oneplus, xiaomi</param>
        /// <param name="digestData">VIP 认证的 Digest 数据 (可选)</param>
        /// <param name="signatureData">VIP 认证的 Signature 数据 (可选)</param>
        /// <param name="ct">取消令牌</param>
        public async Task<bool> ConnectWithCloudLoaderAsync(string portName, byte[] loaderData, string storageType = "ufs", 
            string authMode = "none", byte[] digestData = null, byte[] signatureData = null, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log(string.Format("USB 端口 : {0}", portName));

                if (loaderData == null || loaderData.Length == 0)
                {
                    _log("Loader 数据为空");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 初始化串口
                _portManager = new SerialPortManager();
                bool opened = await _portManager.OpenAsync(portName, 3, false, ct);
                if (!opened)
                {
                    _log("无法打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // Sahara 握手并上传 Loader
                SetState(QualcommConnectionState.SaharaMode);
                Action<double> saharaProgress = null;
                if (_progress != null)
                {
                    saharaProgress = percent => _progress((long)percent, 100);
                }
                _sahara = new SaharaClient(_portManager, _log, _logDetail, saharaProgress);
                
                bool saharaOk = await _sahara.HandshakeAndUploadAsync(loaderData, "Cloud_Loader", ct);
                if (!saharaOk)
                {
                    _log("Sahara 握手/Loader 上传失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 保存芯片信息
                _cachedChipInfo = _sahara.ChipInfo;
                
                // 显示 Sahara 阶段收集的设备信息
                if (_cachedChipInfo != null)
                {
                    // 协议版本
                    uint protocolVersion = _sahara != null ? _sahara.ProtocolVersion : 0;
                    if (protocolVersion > 0)
                        _log($"[Sahara] 协议版本: V{protocolVersion}");
                    
                    // MSM ID 和 OEM ID
                    string msmHex = _cachedChipInfo.MsmId.ToString("X8");
                    string oemHex = _cachedChipInfo.OemId.ToString("X4");
                    _log($"[Sahara] 设备: MSM={msmHex}, OEM=0x{oemHex}");
                    
                    // 芯片名称
                    if (!string.IsNullOrEmpty(_cachedChipInfo.ChipName) && _cachedChipInfo.ChipName != "Unknown")
                        _log($"[Sahara] 芯片: {_cachedChipInfo.ChipName}");
                    
                    // 厂商
                    string vendor = QualcommDatabase.GetVendorByPkHash(_cachedChipInfo.PkHash);
                    if (!string.IsNullOrEmpty(vendor) && vendor != "Unknown")
                        _log($"[Sahara] 厂商: {vendor}");
                    
                    // PK Hash (显示前16位)
                    if (!string.IsNullOrEmpty(_cachedChipInfo.PkHash) && _cachedChipInfo.PkHash.Length >= 16)
                        _log($"[Sahara] PK Hash: {_cachedChipInfo.PkHash.Substring(0, 16)}...");
                    
                    // 序列号
                    if (!string.IsNullOrEmpty(_cachedChipInfo.SerialHex))
                        _log($"[Sahara] 序列号: {_cachedChipInfo.SerialHex}");
                }

                // 设置 VIP 标志
                string authModeLower = authMode.ToLowerInvariant();
                IsVipDevice = (authModeLower == "vip" || authModeLower == "oplus");

                // 等待 Firehose 就绪
                _log("正在发送 Firehose 引导文件 : 成功");
                await Task.Delay(1000, ct);

                // 重新打开端口 (Firehose 模式)
                _portManager.Close();
                await Task.Delay(500, ct);

                opened = await _portManager.OpenAsync(portName, 5, true, ct);
                if (!opened)
                {
                    _log("无法重新打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                // 创建 Firehose 客户端
                SetState(QualcommConnectionState.FirehoseMode);
                _firehose = new FirehoseClient(_portManager, _log, _progress, _logDetail);

                // 传递芯片信息
                if (ChipInfo != null)
                {
                    _firehose.ChipSerial = ChipInfo.SerialHex;
                    _firehose.ChipHwId = ChipInfo.HwIdHex;
                    _firehose.ChipPkHash = ChipInfo.PkHash;
                }

                // 执行认证 (根据模式) - 注意：VIP 认证必须在 Firehose 配置之前执行
                if (authModeLower == "vip" || authModeLower == "oplus")
                {
                    // VIP 认证 (OPLUS)
                    if (digestData != null && digestData.Length > 0 && signatureData != null && signatureData.Length > 0)
                    {
                        _log(string.Format("[高通] 执行 VIP 认证 (Digest={0}B, Sign={1}B)...", digestData.Length, signatureData.Length));
                        bool vipOk = await _firehose.PerformVipAuthAsync(digestData, signatureData, ct);
                        if (vipOk)
                        {
                            _log("[高通] VIP 认证成功，已激活高权限模式");
                            IsVipDevice = true;
                        }
                        else
                        {
                            _log("[高通] VIP 认证失败，回退到普通模式");
                            IsVipDevice = false;
                        }
                    }
                    else
                    {
                        _log("[高通] VIP 认证需要 Digest 和 Sign 文件，但未提供");
                        _log("[高通] 将以普通模式继续（某些操作可能受限）");
                        IsVipDevice = false;
                    }
                }
                else if (authModeLower == "xiaomi" || authModeLower == "miauth" || (authModeLower == "none" && IsXiaomiDevice()))
                {
                    _log("执行小米认证...");
                    var xiaomi = new XiaomiAuthStrategy(_log);
                    xiaomi.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                    bool authOk = await xiaomi.AuthenticateAsync(_firehose, null, ct);
                    if (authOk)
                        _log("小米认证成功");
                    else
                        _log("小米认证失败");
                }
                else if (authModeLower == "oneplus" || authModeLower == "demacia")
                {
                    _log("执行 OnePlus 认证...");
                    var oneplus = new OnePlusAuthStrategy(_log);
                    bool authOk = await oneplus.AuthenticateAsync(_firehose, null, ct);
                    if (authOk)
                        _log("OnePlus 认证成功");
                    else
                        _log("OnePlus 认证失败");
                }

                // Firehose 配置
                _log("正在配置 Firehose...");
                bool configOk = await _firehose.ConfigureAsync(storageType, 0, ct);
                if (!configOk)
                {
                    _log("配置 Firehose : 失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }
                _log("配置 Firehose : 成功");

                // 保存连接参数
                LastPortName = portName;
                LastStorageType = storageType;

                // 注册端口断开事件
                if (_portManager != null)
                {
                    _portManager.PortDisconnected += (s, e) => HandlePortDisconnected();
                }

                SetState(QualcommConnectionState.Ready);
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
        }

        /// <summary>
        /// 直接连接 Firehose (跳过 Sahara)
        /// </summary>
        public async Task<bool> ConnectFirehoseDirectAsync(string portName, string storageType = "ufs", CancellationToken ct = default(CancellationToken))
        {
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log(string.Format("[高通] 直接连接 Firehose: {0}...", portName));

                _portManager = new SerialPortManager();
                bool opened = await _portManager.OpenAsync(portName, 3, true, ct);
                if (!opened)
                {
                    _log("[高通] 无法打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                SetState(QualcommConnectionState.FirehoseMode);
                _firehose = new FirehoseClient(_portManager, _log, _progress, _logDetail);

                _log("正在配置 Firehose...");
                bool configOk = await _firehose.ConfigureAsync(storageType, 0, ct);
                if (!configOk)
                {
                    _log("配置 Firehose : 失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }
                _log("配置 Firehose : 成功");

                // 保存连接参数
                LastPortName = portName;
                LastStorageType = storageType;
                
                // 注册端口断开事件
                if (_portManager != null)
                {
                    _portManager.PortDisconnected += (s, e) => HandlePortDisconnected();
                }
                
                SetState(QualcommConnectionState.Ready);
                _log("[高通] Firehose 直连成功");
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[高通] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            _log("[高通] 断开连接");

            if (_portManager != null)
            {
                _portManager.Close();
                _portManager.Dispose();
                _portManager = null;
            }

            if (_sahara != null)
            {
                _sahara.Dispose();
                _sahara = null;
            }

            if (_firehose != null)
            {
                _firehose.Dispose();
                _firehose = null;
            }

            _partitionCache.Clear();
            IsVipDevice = false;
            _portClosed = false;
            _cachedChipInfo = null;

            SetState(QualcommConnectionState.Disconnected);
        }
        
        /// <summary>
        /// 释放端口 (操作完成后调用，保留设备对象和状态信息)
        /// </summary>
        /// <remarks>
        /// 根据EDL工具最佳实践：操作完成后应释放端口，让其他程序可以连接设备。
        /// 调用此方法后：
        /// - 端口关闭，串口资源释放
        /// - 设备对象保留 (ChipInfo, 分区缓存等)
        /// - 下次操作前会自动重新打开端口
        /// </remarks>
        public void ReleasePort()
        {
            // 如果设置了保持端口打开，则跳过释放
            if (_keepPortOpen)
            {
                _logDetail("[高通] 端口保持打开 (连续操作模式)");
                return;
            }
            
            if (_portManager == null || !_portManager.IsOpen)
                return;
                
            try
            {
                // 缓存芯片信息 (端口关闭后仍可访问)
                if (_sahara != null && _sahara.ChipInfo != null)
                {
                    _cachedChipInfo = _sahara.ChipInfo;
                }
                
                // 关闭端口但不销毁设备对象
                _portManager.Close();
                _portClosed = true;
                
                _logDetail("[高通] 端口已释放 (设备信息保留)");
            }
            catch (Exception ex)
            {
                _logDetail("[高通] 释放端口异常: " + ex.Message);
            }
        }
        
        /// <summary>
        /// 确保端口已打开 (操作前调用)
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>端口是否可用</returns>
        public async Task<bool> EnsurePortOpenAsync(CancellationToken ct = default(CancellationToken))
        {
            // 如果端口已打开且可用，直接返回
            if (_portManager != null && _portManager.IsOpen && !_portClosed)
                return true;
                
            // 如果没有记录端口名，无法重新打开
            if (string.IsNullOrEmpty(LastPortName))
            {
                _log("[高通] 无法重新打开端口: 未记录端口名");
                return false;
            }
            
            // 检查端口是否在系统中可用（使用 LastPortName 而不是 _portManager.IsPortAvailable()）
            // 因为 ReleasePort 会清除 _portManager._currentPortName
            var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
            bool portExists = Array.Exists(availablePorts, p => 
                p.Equals(LastPortName, StringComparison.OrdinalIgnoreCase));
            
            if (!portExists)
            {
                _log("[高通] 端口已从系统中移除，设备可能已断开");
                HandlePortDisconnected();
                return false;
            }
            
            // 重新打开端口
            try
            {
                _logDetail(string.Format("[高通] 重新打开端口: {0}", LastPortName));
                
                if (_portManager == null)
                {
                    _portManager = new SerialPortManager();
                }
                
                bool opened = await _portManager.OpenAsync(LastPortName, 3, true, ct);
                if (!opened)
                {
                    _log("[高通] 无法重新打开端口");
                    return false;
                }
                
                _portClosed = false;
                
                // 注意: Firehose 客户端保留，不需要重新创建
                // 如果 _firehose 为 null，说明连接本身有问题，需要重新完整连接
                if (_firehose == null)
                {
                    _log("[高通] Firehose 客户端丢失，需要重新完整连接");
                    return false;
                }
                
                _logDetail("[高通] 端口重新打开成功");
                return true;
            }
            catch (Exception ex)
            {
                _log("[高通] 重新打开端口失败: " + ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// 设置是否保持端口打开 (用于连续操作，如批量刷写)
        /// </summary>
        /// <param name="keepOpen">是否保持打开</param>
        public void SetKeepPortOpen(bool keepOpen)
        {
            _keepPortOpen = keepOpen;
            if (keepOpen)
                _logDetail("[高通] 设置: 保持端口打开");
            else
                _logDetail("[高通] 设置: 允许释放端口");
        }
        
        /// <summary>
        /// 获取芯片信息 (即使端口关闭也可访问缓存)
        /// </summary>
        public QualcommChipInfo GetChipInfo()
        {
            if (_sahara != null && _sahara.ChipInfo != null)
                return _sahara.ChipInfo;
            return _cachedChipInfo;
        }
        
        /// <summary>
        /// 端口是否已释放
        /// </summary>
        public bool IsPortReleased { get { return _portClosed; } }
        
        /// <summary>
        /// 重置卡住的 Sahara 状态
        /// 当设备因为其他软件或引导错误导致卡在 Sahara 模式时使用
        /// </summary>
        /// <param name="portName">端口名</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否成功重置</returns>
        public async Task<bool> ResetSaharaAsync(string portName, CancellationToken ct = default(CancellationToken))
        {
            _log("[高通] 尝试重置卡住的 Sahara 状态...");
            
            try
            {
                // 确保之前的连接已关闭
                Disconnect();
                await Task.Delay(200, ct);
                
                // 打开端口
                _portManager = new SerialPortManager();
                bool opened = await _portManager.OpenAsync(portName, 3, true, ct);
                if (!opened)
                {
                    _log("[高通] 无法打开端口");
                    return false;
                }
                
                // 创建临时 Sahara 客户端
                _sahara = new SaharaClient(_portManager, _log, _logDetail, null);
                
                // 尝试重置
                bool success = await _sahara.TryResetSaharaAsync(ct);
                
                if (success)
                {
                    _log("[高通] ✓ Sahara 状态已重置");
                    _log("[高通] 设备已准备好，请点击[连接]按钮重新连接");
                    
                    // 重置成功后断开连接，让用户可以正常重新连接
                    // 保留端口名以便后续连接
                    string savedPortName = portName;
                    
                    // 关闭当前连接（释放端口资源）
                    if (_portManager != null)
                    {
                        _portManager.Close();
                        _portManager.Dispose();
                        _portManager = null;
                    }
                    if (_sahara != null)
                    {
                        _sahara.Dispose();
                        _sahara = null;
                    }
                    
                    // 设置为断开状态，等待用户重新连接
                    SetState(QualcommConnectionState.Disconnected);
                    LastPortName = savedPortName;  // 保留端口名
                }
                else
                {
                    _log("[高通] ❌ 无法重置 Sahara，请尝试断电重启设备");
                    // 关闭连接
                    Disconnect();
                }
                
                return success;
            }
            catch (Exception ex)
            {
                _log("[高通] 重置 Sahara 异常: " + ex.Message);
                Disconnect();
                return false;
            }
        }
        
        /// <summary>
        /// 硬重置设备 (完全重启)
        /// </summary>
        /// <param name="portName">端口名</param>
        /// <param name="ct">取消令牌</param>
        public async Task<bool> HardResetDeviceAsync(string portName, CancellationToken ct = default(CancellationToken))
        {
            _log("[高通] 发送硬重置命令...");
            
            try
            {
                // 如果已连接 Firehose，通过 Firehose 重置
                if (_firehose != null && State == QualcommConnectionState.Ready)
                {
                    bool ok = await _firehose.ResetAsync("reset", ct);
                    Disconnect();
                    return ok;
                }
                
                // 否则尝试通过 Sahara 重置
                if (_portManager == null || !_portManager.IsOpen)
                {
                    _portManager = new SerialPortManager();
                    await _portManager.OpenAsync(portName, 3, true, ct);
                }
                
                if (_sahara == null)
                {
                    _sahara = new SaharaClient(_portManager, _log, _logDetail, null);
                }
                
                _sahara.SendHardReset();
                _log("[高通] 硬重置命令已发送，设备将重启");
                
                await Task.Delay(500, ct);
                Disconnect();
                return true;
            }
            catch (Exception ex)
            {
                _log("[高通] 硬重置异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 执行认证
        /// </summary>
        public async Task<bool> AuthenticateAsync(string authMode, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行认证");
                return false;
            }

            try
            {
                switch (authMode.ToLowerInvariant())
                {
                    case "oneplus":
                        _log("[高通] 执行 OnePlus 认证...");
                        var oneplusAuth = new Authentication.OnePlusAuthStrategy();
                        // OnePlus 认证不需要外部文件，使用空字符串
                        return await oneplusAuth.AuthenticateAsync(_firehose, "", ct);

                    case "vip":
                    case "oplus":
                        _log("[高通] 执行 VIP/OPPO 认证...");
                        // VIP 认证通常需要签名文件，这里使用默认路径
                        string vipDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vip");
                        string digestPath = System.IO.Path.Combine(vipDir, "digest.bin");
                        string signaturePath = System.IO.Path.Combine(vipDir, "signature.bin");
                        if (!System.IO.File.Exists(digestPath) || !System.IO.File.Exists(signaturePath))
                        {
                            _log("[高通] VIP 认证文件不存在，尝试无签名认证...");
                            // 如果没有签名文件，返回 true 继续（某些设备可能不需要认证）
                            return true;
                        }
                        bool ok = await _firehose.PerformVipAuthAsync(digestPath, signaturePath, ct);
                        if (ok) IsVipDevice = true;
                        return ok;

                    case "xiaomi":
                        _log("[高通] 执行小米认证...");
                        var xiaomiAuth = new Authentication.XiaomiAuthStrategy(_log);
                        xiaomiAuth.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                        return await xiaomiAuth.AuthenticateAsync(_firehose, "", ct);

                    default:
                        _log(string.Format("[高通] 未知认证模式: {0}", authMode));
                        return false;
                }
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 认证失败: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 执行 OnePlus 认证
        /// </summary>
        public async Task<bool> PerformOnePlusAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行 OnePlus 认证");
                return false;
            }

            try
            {
                _log("[高通] 执行 OnePlus 认证...");
                var oneplusAuth = new Authentication.OnePlusAuthStrategy(_log);
                bool ok = await oneplusAuth.AuthenticateAsync(_firehose, "", ct);
                if (ok)
                    _log("[高通] OnePlus 认证成功");
                else
                    _log("[高通] OnePlus 认证失败");
                return ok;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] OnePlus 认证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 执行小米认证
        /// </summary>
        public async Task<bool> PerformXiaomiAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行小米认证");
                return false;
            }

            try
            {
                _log("[高通] 执行小米认证...");
                var xiaomiAuth = new Authentication.XiaomiAuthStrategy(_log);
                xiaomiAuth.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                bool ok = await xiaomiAuth.AuthenticateAsync(_firehose, "", ct);
                if (ok)
                    _log("[高通] 小米认证成功");
                else
                    _log("[高通] 小米认证失败");
                return ok;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 小米认证异常: {0}", ex.Message));
                return false;
            }
        }

        private void SetState(QualcommConnectionState newState)
        {
            if (State != newState)
            {
                State = newState;
                if (StateChanged != null)
                    StateChanged(this, newState);
            }
        }

        #endregion

        #region 自动认证逻辑

        /// <summary>
        /// 自动认证 - 仅对小米设备自动执行
        /// 其他设备 (OnePlus/OPPO/Realme 等) 由用户手动选择认证方式
        /// </summary>
        private async Task<bool> AutoAuthenticateAsync(string programmerPath, CancellationToken ct)
        {
            if (_firehose == null) return true;

            // 只有小米设备自动认证
            if (IsXiaomiDevice())
            {
                _log("[高通] 检测到小米设备，自动执行 MiAuth 认证...");
                try
                {
                    var xiaomi = new XiaomiAuthStrategy(_log);
                    xiaomi.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                    bool result = await xiaomi.AuthenticateAsync(_firehose, programmerPath, ct);
                    if (result)
                    {
                        _log("[高通] 小米认证成功");
                    }
                    else
                    {
                        _log("[高通] 小米认证失败，设备可能需要官方授权");
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    _log(string.Format("[高通] 小米认证异常: {0}", ex.Message));
                    return false;
                }
            }

            // 其他设备不自动认证，由用户手动选择
            return true;
        }

        /// <summary>
        /// 检测是否为小米设备 (通过 OEM ID 或其他特征)
        /// </summary>
        public bool IsXiaomiDevice()
        {
            if (ChipInfo == null) return false;

            // 通过 OEM ID 检测 (0x0072 = Xiaomi 官方)
            if (ChipInfo.OemId == 0x0072) return true;

            // 通过 PK Hash 前缀检测 (小米常见 PK Hash)
            if (!string.IsNullOrEmpty(ChipInfo.PkHash))
            {
                string pkLower = ChipInfo.PkHash.ToLowerInvariant();
                // 小米设备 PK Hash 前缀列表 (持续更新)
                string[] xiaomiPkHashPrefixes = new[]
                {
                    "c924a35f",  // 常见小米设备
                    "3373d5c8",
                    "e07be28b",
                    "6f5c4e17",
                    "57158eaf",
                    "355d47f9",
                    "a7b8b825",
                    "1c845b80",
                    "58b4add1",
                    "dd0cba2f",
                    "1bebe386"
                };

                foreach (var prefix in xiaomiPkHashPrefixes)
                {
                    if (pkLower.StartsWith(prefix))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 手动执行 OPLUS VIP 认证 (基于 Digest 和 Signature)
        /// </summary>
        public async Task<bool> PerformVipAuthManualAsync(string digestPath, string signaturePath, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接设备");
                return false;
            }

            _log("[高通] 启动 OPLUS VIP 认证 (Digest + Sign)...");
            try
            {
                bool result = await _firehose.PerformVipAuthAsync(digestPath, signaturePath, ct);
                if (result)
                {
                    _log("[高通] VIP 认证成功，已进入高权限模式");
                    IsVipDevice = true; 
                }
                else
                {
                    _log("[高通] VIP 认证失败：校验未通过");
                }
                return result;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] VIP 认证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 手动执行 OPLUS VIP 认证 (基于 byte[] 数据)
        /// 支持在发送 Digest 后直接写入签名数据
        /// </summary>
        /// <param name="digestData">Digest 数据 (Hash Segment, ~20-30KB)</param>
        /// <param name="signatureData">签名数据 (256 字节 RSA-2048)</param>
        public async Task<bool> PerformVipAuthAsync(byte[] digestData, byte[] signatureData, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接设备");
                return false;
            }

            _log(string.Format("[高通] 启动 VIP 认证 (Digest={0}B, Sign={1}B)...", 
                digestData?.Length ?? 0, signatureData?.Length ?? 0));
            try
            {
                bool result = await _firehose.PerformVipAuthAsync(digestData, signatureData, ct);
                if (result)
                {
                    _log("[高通] VIP 认证成功，已进入高权限模式");
                    IsVipDevice = true;
                }
                else
                {
                    _log("[高通] VIP 认证失败：校验未通过");
                }
                return result;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] VIP 认证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 分步执行 VIP 认证 - Step 1: 发送 Digest
        /// </summary>
        public async Task<bool> SendVipDigestAsync(byte[] digestData, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.SendVipDigestAsync(digestData, ct);
        }

        /// <summary>
        /// 分步执行 VIP 认证 - Step 2-3: 准备 VIP 模式
        /// </summary>
        public async Task<bool> PrepareVipModeAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.PrepareVipModeAsync(ct);
        }

        /// <summary>
        /// 分步执行 VIP 认证 - Step 4: 发送签名 (256 字节)
        /// 这是核心方法：在发送 Digest 后写入签名
        /// </summary>
        public async Task<bool> SendVipSignatureAsync(byte[] signatureData, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.SendVipSignatureAsync(signatureData, ct);
        }

        /// <summary>
        /// 分步执行 VIP 认证 - Step 5: 完成认证
        /// </summary>
        public async Task<bool> FinalizeVipAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.FinalizeVipAuthAsync(ct);
        }

        /// <summary>
        /// 使用嵌入的奇美拉签名数据进行 VIP 认证
        /// </summary>
        /// <param name="platform">平台代号 (如 SM8550, SM8650 等)</param>
        public async Task<bool> PerformChimeraAuthAsync(string platform, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接设备");
                return false;
            }

            // 从嵌入数据库获取签名数据
            var signData = ChimeraSignDatabase.Get(platform);
            if (signData == null)
            {
                _log(string.Format("[高通] 不支持的平台: {0}", platform));
                _log("[高通] 支持的平台: " + string.Join(", ", ChimeraSignDatabase.GetSupportedPlatforms()));
                return false;
            }

            _log(string.Format("[高通] 使用奇美拉签名: {0} ({1})", signData.Name, signData.Platform));
            _log(string.Format("[高通] Digest: {0} 字节, Signature: {1} 字节", 
                signData.DigestSize, signData.SignatureSize));

            return await PerformVipAuthAsync(signData.Digest, signData.Signature, ct);
        }

        /// <summary>
        /// 自动检测平台并使用奇美拉签名认证
        /// </summary>
        public async Task<bool> PerformChimeraAuthAutoAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接设备");
                return false;
            }

            // 尝试从 Sahara 获取的芯片信息
            string platform = null;
            if (_sahara != null && _sahara.ChipInfo != null)
            {
                platform = _sahara.ChipInfo.ChipName;
                if (string.IsNullOrEmpty(platform) || platform == "Unknown")
                {
                    // 尝试从 MSM ID 推断
                    uint msmId = _sahara.ChipInfo.MsmId;
                    platform = QualcommDatabase.GetChipName(msmId);
                }
            }

            if (string.IsNullOrEmpty(platform) || platform == "Unknown")
            {
                _log("[高通] 无法自动检测平台，请手动指定");
                _log("[高通] 支持的平台: " + string.Join(", ", ChimeraSignDatabase.GetSupportedPlatforms()));
                return false;
            }

            _log(string.Format("[高通] 自动检测到平台: {0}", platform));
            return await PerformChimeraAuthAsync(platform, ct);
        }

        /// <summary>
        /// 获取支持的奇美拉平台列表
        /// </summary>
        public string[] GetSupportedChimeraPlatforms()
        {
            return ChimeraSignDatabase.GetSupportedPlatforms();
        }

        /// <summary>
        /// 检查平台是否支持奇美拉签名
        /// </summary>
        public bool IsChimeraSupported(string platform)
        {
            return ChimeraSignDatabase.IsSupported(platform);
        }

        /// <summary>
        /// 获取设备挑战码 (用于在线签名)
        /// </summary>
        public async Task<string> GetVipChallengeAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return null;
            return await _firehose.GetVipChallengeAsync(ct);
        }

        #endregion

        #region 分区操作

        /// <summary>
        /// 读取所有 LUN 的 GPT 分区表
        /// </summary>
        public async Task<List<PartitionInfo>> ReadAllGptAsync(int maxLuns = 6, CancellationToken ct = default(CancellationToken))
        {
            return await ReadAllGptAsync(maxLuns, null, null, ct);
        }

        /// <summary>
        /// 读取所有 LUN 的 GPT 分区表（带进度回调）
        /// </summary>
        /// <param name="maxLuns">最大 LUN 数量</param>
        /// <param name="totalProgress">总进度回调 (当前LUN, 总LUN)</param>
        /// <param name="subProgress">子进度回调 (0-100)</param>
        /// <param name="ct">取消令牌</param>
        public async Task<List<PartitionInfo>> ReadAllGptAsync(
            int maxLuns, 
            IProgress<Tuple<int, int>> totalProgress,
            IProgress<double> subProgress,
            CancellationToken ct = default(CancellationToken))
        {
            var allPartitions = new List<PartitionInfo>();

            if (_firehose == null)
                return allPartitions;

            _logDetail("正在读取 GUID 分区表...");

            // 报告开始
            if (totalProgress != null) totalProgress.Report(Tuple.Create(0, maxLuns));
            if (subProgress != null) subProgress.Report(0);

            // LUN 进度回调 - 实时更新进度
            var lunProgress = new Progress<int>(lun => {
                if (totalProgress != null) totalProgress.Report(Tuple.Create(lun, maxLuns));
                if (subProgress != null) subProgress.Report(100.0 * lun / maxLuns);
            });

            var partitions = await _firehose.ReadGptPartitionsAsync(IsVipDevice, ct, lunProgress);
            
            // 报告中间进度
            if (subProgress != null) subProgress.Report(80);
            
            if (partitions != null && partitions.Count > 0)
            {
                allPartitions.AddRange(partitions);
                _log(string.Format("读取 GUID 分区表 : 成功 [{0}]", partitions.Count));

                // 缓存分区
                _partitionCache.Clear();
                foreach (var p in partitions)
                {
                    if (!_partitionCache.ContainsKey(p.Lun))
                        _partitionCache[p.Lun] = new List<PartitionInfo>();
                    _partitionCache[p.Lun].Add(p);
                }
            }

            // 报告完成
            if (subProgress != null) subProgress.Report(100);
            if (totalProgress != null) totalProgress.Report(Tuple.Create(maxLuns, maxLuns));

            _log(string.Format("[高通] 共发现 {0} 个分区", allPartitions.Count));
            return allPartitions;
        }

        /// <summary>
        /// 获取指定 LUN 的分区列表
        /// </summary>
        public List<PartitionInfo> GetCachedPartitions(int lun = -1)
        {
            var result = new List<PartitionInfo>();

            if (lun == -1)
            {
                foreach (var kv in _partitionCache)
                    result.AddRange(kv.Value);
            }
            else
            {
                List<PartitionInfo> list;
                if (_partitionCache.TryGetValue(lun, out list))
                    result.AddRange(list);
            }

            return result;
        }

        /// <summary>
        /// 查找分区
        /// </summary>
        public PartitionInfo FindPartition(string name)
        {
            foreach (var kv in _partitionCache)
            {
                foreach (var p in kv.Value)
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
            }
            return null;
        }

        /// <summary>
        /// 读取分区到文件
        /// </summary>
        public async Task<bool> ReadPartitionAsync(string partitionName, string outputPath, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            var partition = FindPartition(partitionName);
            if (partition == null)
            {
                _log("[高通] 未找到分区 " + partitionName);
                return false;
            }

            _log(string.Format("[高通] 读取分区 {0} ({1})", partitionName, partition.FormattedSize));

            try
            {
                int sectorsPerChunk = _firehose.MaxPayloadSize / partition.SectorSize;
                long totalSectors = partition.NumSectors;
                long readSectors = 0;
                long totalBytes = partition.Size;
                long readBytes = 0;

                // 使用异步文件流，避免阻塞
                using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous))
                {
                    while (readSectors < totalSectors && !ct.IsCancellationRequested)
                    {
                        int toRead = (int)Math.Min(sectorsPerChunk, totalSectors - readSectors);
                        // ConfigureAwait(false) 避免回到 UI 线程
                        byte[] data = await _firehose.ReadSectorsAsync(
                            partition.Lun, partition.StartSector + readSectors, toRead, ct, IsVipDevice, partitionName).ConfigureAwait(false);

                        if (data == null)
                        {
                            _log("[高通] 读取失败");
                            return false;
                        }

                        // 使用异步写入
                        await fs.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
                        readSectors += toRead;
                        readBytes += data.Length;

                        // 调用字节级进度回调 (用于速度计算)
                        _firehose.ReportProgress(readBytes, totalBytes);

                        // 百分比进度 (使用 double)
                        if (progress != null)
                            progress.Report(100.0 * readBytes / totalBytes);
                    }
                }

                _log(string.Format("[高通] 分区 {0} 已保存到 {1}", partitionName, outputPath));
                return true;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 读取错误 - {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 写入分区
        /// </summary>
        public async Task<bool> WritePartitionAsync(string partitionName, string filePath, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            var partition = FindPartition(partitionName);
            if (partition == null)
            {
                _log("[高通] 未找到分区 " + partitionName);
                return false;
            }

            // OPLUS 某些分区需要 SHA256 校验环绕
            bool useSha256 = IsOplusDevice && (partitionName.ToLower() == "xbl" || partitionName.ToLower() == "abl" || partitionName.ToLower() == "imagefv");
            if (useSha256) await _firehose.Sha256InitAsync(ct).ConfigureAwait(false);

            // VIP 设备使用伪装模式写入
            // ConfigureAwait(false) 避免回到 UI 线程，提高 IO 性能
            bool success = await _firehose.FlashPartitionFromFileAsync(
                partitionName, filePath, partition.Lun, partition.StartSector, progress, ct, IsVipDevice).ConfigureAwait(false);

            if (useSha256) await _firehose.Sha256FinalAsync(ct).ConfigureAwait(false);

            return success;
        }

        private bool IsOplusDevice 
        { 
            get { 
                if (IsVipDevice) return true;
                if (ChipInfo != null && (ChipInfo.Vendor == "OPPO" || ChipInfo.Vendor == "Realme" || ChipInfo.Vendor == "OnePlus")) return true;
                return false;
            } 
        }

        /// <summary>
        /// 直接写入指定 LUN 和 StartSector (用于 PrimaryGPT/BackupGPT 等特殊分区)
        /// 支持官方 NUM_DISK_SECTORS-N 负扇区格式
        /// </summary>
        public async Task<bool> WriteDirectAsync(string label, string filePath, int lun, long startSector, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            // 负扇区使用官方格式直接发送给设备 (不依赖客户端 GPT 缓存)
            if (startSector < 0)
            {
                _logDetail(string.Format("[高通] 写入: {0} -> LUN{1} @ NUM_DISK_SECTORS{2}", label, lun, startSector));
                
                // 使用官方 NUM_DISK_SECTORS-N 格式，让设备计算绝对地址
                // ConfigureAwait(false) 避免回到 UI 线程
                return await _firehose.FlashPartitionWithNegativeSectorAsync(
                    label, filePath, lun, startSector, progress, ct).ConfigureAwait(false);
            }
            else
            {
                _logDetail(string.Format("[高通] 写入: {0} -> LUN{1} @ sector {2}", label, lun, startSector));

                // 正数扇区正常写入
                // ConfigureAwait(false) 避免回到 UI 线程
                return await _firehose.FlashPartitionFromFileAsync(
                    label, filePath, lun, startSector, progress, ct, IsVipDevice).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 擦除分区
        /// </summary>
        public async Task<bool> ErasePartitionAsync(string partitionName, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            var partition = FindPartition(partitionName);
            if (partition == null)
            {
                _log("[高通] 未找到分区 " + partitionName);
                return false;
            }

            // VIP 设备使用伪装模式擦除
            // ConfigureAwait(false) 避免回到 UI 线程
            return await _firehose.ErasePartitionAsync(partition, ct, IsVipDevice).ConfigureAwait(false);
        }

        /// <summary>
        /// 读取分区指定偏移处的数据
        /// </summary>
        /// <param name="partitionName">分区名称</param>
        /// <param name="offset">偏移 (字节)</param>
        /// <param name="size">大小 (字节)</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>读取的数据</returns>
        public async Task<byte[]> ReadPartitionDataAsync(string partitionName, long offset, int size, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return null;

            var partition = FindPartition(partitionName);
            if (partition == null)
            {
                _log("[高通] 未找到分区 " + partitionName);
                return null;
            }

            // 计算扇区位置
            int sectorSize = SectorSize > 0 ? SectorSize : 4096;
            long startSector = partition.StartSector + (offset / sectorSize);
            int numSectors = (size + sectorSize - 1) / sectorSize;

            // 只有 VIP 认证成功后才使用 VIP 模式读取
            // IsVipDevice = true 表示 VIP 认证已成功
            // IsOplusDevice 只用于判断是否需要 SHA256 校验，不用于读取模式
            bool useVipMode = IsVipDevice;

            // 读取数据
            byte[] data = await _firehose.ReadSectorsAsync(partition.Lun, startSector, numSectors, ct, useVipMode, partitionName);
            if (data == null) return null;

            // 如果有偏移对齐问题，截取正确的数据
            int offsetInSector = (int)(offset % sectorSize);
            if (offsetInSector > 0 || data.Length > size)
            {
                int actualSize = Math.Min(size, data.Length - offsetInSector);
                if (actualSize <= 0) return null;
                
                byte[] result = new byte[actualSize];
                Array.Copy(data, offsetInSector, result, 0, actualSize);
                return result;
            }

            return data;
        }

        /// <summary>
        /// 获取 Firehose 客户端 (供内部使用)
        /// </summary>
        internal Protocol.FirehoseClient GetFirehoseClient()
        {
            return _firehose;
        }

        #endregion

        #region 设备控制

        /// <summary>
        /// 重启设备
        /// </summary>
        public async Task<bool> RebootAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            bool result = await _firehose.ResetAsync("reset", ct);
            if (result)
                Disconnect();

            return result;
        }

        /// <summary>
        /// 关机
        /// </summary>
        public async Task<bool> PowerOffAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            bool result = await _firehose.PowerOffAsync(ct);
            if (result)
                Disconnect();

            return result;
        }

        /// <summary>
        /// 重启到 EDL 模式
        /// </summary>
        public async Task<bool> RebootToEdlAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            bool result = await _firehose.RebootToEdlAsync(ct);
            if (result)
                Disconnect();

            return result;
        }

        /// <summary>
        /// 设置活动 Slot
        /// </summary>
        public async Task<bool> SetActiveSlotAsync(string slot, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            return await _firehose.SetActiveSlotAsync(slot, ct);
        }

        /// <summary>
        /// 修复 GPT
        /// </summary>
        public async Task<bool> FixGptAsync(int lun = -1, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            return await _firehose.FixGptAsync(lun, true, ct);
        }

        /// <summary>
        /// 设置启动 LUN
        /// </summary>
        public async Task<bool> SetBootLunAsync(int lun, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            return await _firehose.SetBootLunAsync(lun, ct);
        }

        /// <summary>
        /// Ping 测试连接
        /// </summary>
        public async Task<bool> PingAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            return await _firehose.PingAsync(ct);
        }

        /// <summary>
        /// 应用 Patch XML 文件
        /// </summary>
        public async Task<int> ApplyPatchXmlAsync(string patchXmlPath, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return 0;

            return await _firehose.ApplyPatchXmlAsync(patchXmlPath, ct);
        }

        /// <summary>
        /// 应用多个 Patch XML 文件
        /// </summary>
        public async Task<int> ApplyPatchFilesAsync(IEnumerable<string> patchFiles, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return 0;

            int totalPatches = 0;
            foreach (var patchFile in patchFiles)
            {
                if (ct.IsCancellationRequested) break;
                totalPatches += await _firehose.ApplyPatchXmlAsync(patchFile, ct);
            }
            return totalPatches;
        }

        #endregion

        #region 批量刷写

        /// <summary>
        /// 批量刷写分区
        /// </summary>
        public async Task<bool> FlashMultipleAsync(IEnumerable<FlashPartitionInfo> partitions, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            var list = new List<FlashPartitionInfo>(partitions);
            int total = list.Count;
            int current = 0;
            bool allSuccess = true;

            foreach (var p in list)
            {
                if (ct.IsCancellationRequested)
                    break;

                _log(string.Format("[高通] 刷写 [{0}/{1}] {2}", current + 1, total, p.Name));

                bool ok = await WritePartitionAsync(p.Name, p.Filename, null, ct);
                if (!ok)
                {
                    allSuccess = false;
                    _log("[高通] 刷写失败 - " + p.Name);
                }

                current++;
                if (progress != null)
                    progress.Report(100.0 * current / total);
            }

            return allSuccess;
        }

        #endregion

        #region Diag 诊断功能
        
        /// <summary>
        /// 连接到 Diag 诊断端口
        /// </summary>
        public async Task<bool> ConnectDiagAsync(string portName, int baudRate = 115200)
        {
            try
            {
                if (_diagClient == null)
                    _diagClient = new DiagClient();
                
                _log($"[高通] 正在连接诊断端口 {portName}...");
                var result = await _diagClient.ConnectAsync(portName, baudRate);
                
                if (result)
                    _log("[高通] 诊断端口连接成功");
                else
                    _log("[高通] 诊断端口连接失败");
                
                return result;
            }
            catch (Exception ex)
            {
                _log($"[高通] 诊断端口连接异常: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 断开 Diag 诊断连接
        /// </summary>
        public void DisconnectDiag()
        {
            _diagClient?.Disconnect();
            _diagClient?.Dispose();
            _diagClient = null;
        }
        
        /// <summary>
        /// 发送 SPC 解锁
        /// </summary>
        public async Task<bool> SendSpcAsync(string spc = "000000")
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            _log("[高通] 正在发送 SPC 解锁...");
            var result = await _diagClient.SendSpcAsync(spc);
            _log(result ? "[高通] SPC 解锁成功" : "[高通] SPC 解锁失败");
            return result;
        }
        
        /// <summary>
        /// 读取 IMEI
        /// </summary>
        public async Task<string> ReadDiagImeiAsync(int slot = 1)
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return null;
            }
            
            _log($"[高通] 正在读取 IMEI (Slot {slot})...");
            var imei = await _diagClient.ReadImeiAsync(slot);
            
            if (!string.IsNullOrEmpty(imei))
                _log($"[高通] IMEI{slot}: {imei}");
            else
                _log($"[高通] 读取 IMEI{slot} 失败");
            
            return imei;
        }
        
        /// <summary>
        /// 写入 IMEI
        /// </summary>
        public async Task<bool> WriteDiagImeiAsync(string imei, int slot = 1)
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            if (string.IsNullOrEmpty(imei) || imei.Length != 15)
            {
                _log("[高通] IMEI 格式错误，必须为 15 位数字");
                return false;
            }
            
            _log($"[高通] 正在写入 IMEI (Slot {slot}): {imei}...");
            var result = await _diagClient.WriteImeiAsync(imei, slot);
            _log(result ? "[高通] IMEI 写入成功" : "[高通] IMEI 写入失败");
            return result;
        }
        
        /// <summary>
        /// 读取所有 IMEI
        /// </summary>
        public async Task<ImeiInfo> ReadAllDiagImeiAsync()
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return null;
            }
            
            _log("[高通] 正在读取所有 IMEI...");
            var info = await _diagClient.ReadAllImeiAsync();
            
            if (!string.IsNullOrEmpty(info?.Imei1))
                _log($"[高通] IMEI1: {info.Imei1}");
            if (!string.IsNullOrEmpty(info?.Imei2))
                _log($"[高通] IMEI2: {info.Imei2}");
            
            return info;
        }
        
        /// <summary>
        /// 读取 MEID
        /// </summary>
        public async Task<string> ReadDiagMeidAsync()
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return null;
            }
            
            _log("[高通] 正在读取 MEID...");
            var meid = await _diagClient.ReadMeidAsync();
            
            if (!string.IsNullOrEmpty(meid))
                _log($"[高通] MEID: {meid}");
            else
                _log("[高通] 读取 MEID 失败");
            
            return meid;
        }
        
        /// <summary>
        /// 读取 QCN 文件
        /// </summary>
        public async Task<bool> ReadQcnAsync(string filePath, IProgress<int> progress = null)
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            _log($"[高通] 正在读取 QCN 到 {filePath}...");
            var result = await _diagClient.ReadQcnAsync(filePath, progress);
            _log(result ? "[高通] QCN 读取成功" : "[高通] QCN 读取失败");
            return result;
        }
        
        /// <summary>
        /// 写入 QCN 文件
        /// </summary>
        public async Task<bool> WriteQcnAsync(string filePath, IProgress<int> progress = null)
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            if (!File.Exists(filePath))
            {
                _log($"[高通] QCN 文件不存在: {filePath}");
                return false;
            }
            
            _log($"[高通] 正在写入 QCN: {filePath}...");
            var result = await _diagClient.WriteQcnAsync(filePath, progress);
            _log(result ? "[高通] QCN 写入成功" : "[高通] QCN 写入失败");
            return result;
        }
        
        /// <summary>
        /// 通过 Diag 切换到下载模式 (EDL)
        /// </summary>
        public async Task<bool> SwitchToEdlModeAsync()
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            _log("[高通] 正在切换到下载模式 (EDL)...");
            var result = await _diagClient.SwitchToDownloadModeAsync();
            _log(result ? "[高通] 切换成功，设备即将进入 EDL" : "[高通] 切换失败");
            return result;
        }
        
        /// <summary>
        /// 通过 Diag 重启设备
        /// </summary>
        public async Task<bool> RebootDeviceAsync()
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            _log("[高通] 正在重启设备...");
            var result = await _diagClient.RebootAsync();
            return result;
        }
        
        #endregion

        #region Loader 功能检测
        
        /// <summary>
        /// 获取 Loader 功能特性
        /// </summary>
        public LoaderFeatures LoaderFeatures => _loaderFeatures;
        
        /// <summary>
        /// 检测 Loader 功能
        /// </summary>
        public LoaderFeatures DetectLoaderFeatures(byte[] loaderData)
        {
            if (_loaderDetector == null)
                _loaderDetector = new LoaderFeatureDetector();
            
            _loaderFeatures = _loaderDetector.DetectFeatures(loaderData);
            
            if (_loaderFeatures != null)
            {
                _log("[高通] Loader 功能检测完成:");
                _log($"  芯片: {_loaderFeatures.ChipName ?? "未知"}");
                _log($"  存储: {_loaderFeatures.RecommendedMemoryType}");
                _log($"  受限: {_loaderFeatures.IsRestricted}");
                _log($"  功能: {string.Join(", ", _loaderFeatures.GetSupportedFeatures())}");
                
                if (_loaderFeatures.IsXiaomi)
                {
                    _log($"  [小米] EDL 验证: {_loaderFeatures.XiaomiEdlVerification}");
                    _log($"  [小米] 可利用漏洞: {_loaderFeatures.ExploitPossible}");
                }
            }
            
            return _loaderFeatures;
        }
        
        /// <summary>
        /// 从文件检测 Loader 功能
        /// </summary>
        public LoaderFeatures DetectLoaderFeaturesFromFile(string loaderPath)
        {
            if (!File.Exists(loaderPath))
            {
                _log($"[高通] Loader 文件不存在: {loaderPath}");
                return null;
            }
            
            var loaderData = File.ReadAllBytes(loaderPath);
            return DetectLoaderFeatures(loaderData);
        }
        
        /// <summary>
        /// 验证 Loader 是否有效
        /// </summary>
        public bool IsValidLoader(byte[] loaderData)
        {
            return LoaderFeatureDetector.IsValidLoader(loaderData);
        }
        
        #endregion

        #region Motorola 支持
        
        /// <summary>
        /// 检查是否为 Motorola 固件包
        /// </summary>
        public bool IsMotorolaPackage(string filePath)
        {
            return MotorolaSupport.IsMotorolaPackage(filePath);
        }
        
        /// <summary>
        /// 解析 Motorola 固件包
        /// </summary>
        public async Task<MotorolaPackageInfo> ParseMotorolaPackageAsync(string filePath)
        {
            if (_motorolaSupport == null)
            {
                _motorolaSupport = new MotorolaSupport();
                _motorolaSupport.OnLog += msg => _log($"[Motorola] {msg}");
            }
            
            _log($"[高通] 正在解析 Motorola 固件包: {Path.GetFileName(filePath)}...");
            return await _motorolaSupport.ParsePackageAsync(filePath);
        }
        
        /// <summary>
        /// 提取 Motorola 固件包
        /// </summary>
        public async Task<string> ExtractMotorolaPackageAsync(string filePath, string outputDir = null, IProgress<int> progress = null)
        {
            if (_motorolaSupport == null)
            {
                _motorolaSupport = new MotorolaSupport();
                _motorolaSupport.OnLog += msg => _log($"[Motorola] {msg}");
            }
            
            if (progress != null)
                _motorolaSupport.OnProgress += percent => progress.Report(percent);
            
            _log($"[高通] 正在提取 Motorola 固件包: {Path.GetFileName(filePath)}...");
            var result = await _motorolaSupport.ExtractPackageAsync(filePath, outputDir);
            _log($"[高通] 提取完成: {result}");
            return result;
        }
        
        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Disconnect();
                    DisconnectDiag();
                }
                _disposed = true;
            }
        }

        ~QualcommService()
        {
            Dispose(false);
        }

        #endregion
        /// <summary>
        /// 刷写 OPLUS 固件包中的 Super 逻辑分区 (拆解写入)
        /// </summary>
        public async Task<bool> FlashOplusSuperAsync(string firmwareRoot, string nvId = "", IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;

            // 1. 查找 super 分区信息
            var superPart = FindPartition("super");
            if (superPart == null)
            {
                _log("[高通] 未在设备上找到 super 分区");
                return false;
            }

            // 2. 准备任务
            _log("[高通] 正在解析 OPLUS 固件 Super 布局...");
            string activeSlot = CurrentSlot;
            if (activeSlot == "nonexistent" || string.IsNullOrEmpty(activeSlot))
                activeSlot = "a";

            // 计算 super 分区总大小 (用于校验)
            long superPartitionSize = superPart.Size;
            _log(string.Format("[高通] Super 分区: 起始扇区={0}, 大小={1} MB", superPart.StartSector, superPartitionSize / 1024 / 1024));

            var tasks = await _oplusSuperManager.PrepareSuperTasksAsync(
                firmwareRoot, superPart.StartSector, (int)superPart.SectorSize, 
                activeSlot, nvId, superPartitionSize);
            
            if (tasks.Count == 0)
            {
                _log("[高通] 未找到可用的 Super 逻辑分区镜像");
                return false;
            }
            
            // 3. 校验任务
            var validation = _oplusSuperManager.ValidateTasks(tasks, superPartitionSize, (int)superPart.SectorSize);
            if (!validation.IsValid)
            {
                foreach (var err in validation.Errors)
                {
                    _log(string.Format("[MetaSuper] 错误: {0}", err));
                }
                _log("[高通] Super 刷写校验失败，已中止");
                return false;
            }
            
            // 显示警告但继续
            foreach (var warn in validation.Warnings)
            {
                _log(string.Format("[MetaSuper] 警告: {0}", warn));
            }

            // 4. 执行任务
            long totalBytes = tasks.Sum(t => t.SizeInBytes);
            long totalWritten = 0;

            _log(string.Format("[高通] 开始拆解写入 {0} 个逻辑镜像 (总计: {1} MB)...", tasks.Count, totalBytes / 1024 / 1024));

            foreach (var task in tasks)
            {
                if (ct.IsCancellationRequested) break;

                _log(string.Format("[高通] 写入 {0} [{1}] 到物理扇区 {2}...", task.PartitionName, Path.GetFileName(task.FilePath), task.PhysicalSector));
                
                // 嵌套进度计算
                var taskProgress = new Progress<double>(p => {
                    if (progress != null)
                    {
                        double currentTaskWeight = (double)task.SizeInBytes / totalBytes;
                        double overallPercent = ((double)totalWritten / totalBytes * 100) + (p * currentTaskWeight);
                        progress.Report(overallPercent);
                    }
                });

                bool success = await _firehose.FlashPartitionFromFileAsync(
                    task.PartitionName, 
                    task.FilePath, 
                    superPart.Lun, 
                    task.PhysicalSector, 
                    taskProgress, 
                    ct, 
                    IsVipDevice);

                if (!success)
                {
                    _log(string.Format("[高通] 写入 {0} 失败，流程中止", task.PartitionName));
                    return false;
                }

                totalWritten += task.SizeInBytes;
            }

            _log("[高通] OPLUS Super 拆解写入完成");
            return true;
        }
    }
}
