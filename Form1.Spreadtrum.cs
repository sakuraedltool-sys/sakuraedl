// ============================================================================
// SakuraEDL - Form1 展讯模块部分类
// Spreadtrum/Unisoc Module Partial Class
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SakuraEDL.Spreadtrum.UI;
using SakuraEDL.Spreadtrum.Common;
using SakuraEDL.Spreadtrum.Protocol;
using SakuraEDL.Spreadtrum.Exploit;

namespace SakuraEDL
{
    public partial class Form1
    {
        // ========== 展讯控制器 ==========
        private SpreadtrumUIController _spreadtrumController;
        private uint _selectedChipId = 0;
        
        /// <summary>
        /// 安全调用 UI 更新（处理窗口已关闭的情况）
        /// </summary>
        private void SafeInvoke(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated)
                    return;
                    
                if (InvokeRequired)
                    Invoke(action);
                else
                    action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        
        // 自定义 FDL 配置
        private string _customFdl1Path = null;
        private string _customFdl2Path = null;
        private uint _customFdl1Addr = 0;
        private uint _customFdl2Addr = 0;
        
        // 检测到的设备
        private string _detectedSprdPort = null;
        private SakuraEDL.Spreadtrum.Common.SprdDeviceMode _detectedSprdMode = SakuraEDL.Spreadtrum.Common.SprdDeviceMode.Unknown;

        // 芯片列表 - 从数据库动态加载
        private static Dictionary<string, uint> _sprdChipList;
        private static Dictionary<string, uint> SprdChipList
        {
            get
            {
                if (_sprdChipList == null)
                {
                    _sprdChipList = new Dictionary<string, uint>();
                    _sprdChipList.Add("自动检测", 0);
                    
                    // 从数据库按系列加载芯片
                    var chipsBySeries = SakuraEDL.Spreadtrum.Database.SprdFdlDatabase.GetChipsBySeries();
                    foreach (var series in chipsBySeries.OrderBy(s => s.Key))
                    {
                        // 添加系列分隔符
                        _sprdChipList.Add($"── {series.Key} ──", 0xFFFF);
                        
                        // 添加该系列的芯片
                        foreach (var chip in series.Value.OrderBy(c => c.ChipName))
                        {
                            string displayName = chip.HasExploit 
                                ? $"{chip.ChipName} ★" 
                                : chip.ChipName;
                            _sprdChipList.Add(displayName, chip.ChipId);
                        }
                    }
                }
                return _sprdChipList;
            }
        }

        /// <summary>
        /// 初始化展讯模块
        /// </summary>
        private void InitializeSpreadtrumModule()
        {
            try
            {
                // 初始化芯片选择列表
                InitializeChipSelector();

                // 创建展讯控制器
                _spreadtrumController = new SpreadtrumUIController(
                    (msg, color) => AppendLog(msg, color),
                    msg => AppendLogDetail(msg));

                // 绑定事件
                BindSpreadtrumEvents();

                // 注意: 设备监听在切换到展讯标签页时启动，避免与其他模块冲突

                AppendLog("[展讯] 模块初始化完成", Color.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"[展讯] 初始化失败: {ex.Message}", Color.Red);
            }
        }
        
        /// <summary>
        /// 扫描并显示设备管理器中的所有 COM 端口 (调试用)
        /// </summary>
        private void SprdScanAllComPorts()
        {
            try
            {
                var detector = new SprdPortDetector();
                detector.OnLog += msg => AppendLog(msg, Color.Gray);
                
                AppendLog("[设备管理器] 扫描所有 COM 端口...", Color.Cyan);
                var ports = detector.ScanAllComPorts();
                
                if (ports.Count == 0)
                {
                    AppendLog("[设备管理器] 未发现任何 COM 端口", Color.Orange);
                    return;
                }
                
                AppendLog(string.Format("[设备管理器] 发现 {0} 个 COM 端口:", ports.Count), Color.Green);
                
                foreach (var port in ports)
                {
                    string sprdFlag = port.IsSprdDetected ? " ★展讯★" : "";
                    Color color = port.IsSprdDetected ? Color.Lime : Color.White;
                    
                    AppendLog(string.Format("  {0}: VID={1:X4} PID={2:X4}{3}", 
                        port.ComPort, port.Vid, port.Pid, sprdFlag), color);
                    AppendLog(string.Format("    名称: {0}", port.Name), Color.Gray);
                    
                    if (!string.IsNullOrEmpty(port.HardwareId))
                    {
                        AppendLog(string.Format("    HW ID: {0}", port.HardwareId), Color.DarkGray);
                    }
                }
                
                // 统计
                int sprdCount = 0;
                foreach (var p in ports)
                {
                    if (p.IsSprdDetected) sprdCount++;
                }
                
                AppendLog(string.Format("[设备管理器] 总计: {0} 个端口, {1} 个识别为展讯", 
                    ports.Count, sprdCount), Color.Cyan);
            }
            catch (Exception ex)
            {
                AppendLog(string.Format("[设备管理器] 扫描异常: {0}", ex.Message), Color.Red);
            }
        }

        /// <summary>
        /// 初始化芯片选择器
        /// </summary>
        private void InitializeChipSelector()
        {
            // 填充芯片列表
            var items = new List<object>();
            foreach (var chip in SprdChipList)
            {
                items.Add(chip.Key);
            }
            sprdSelectChip.Items.Clear();
            sprdSelectChip.Items.AddRange(items.ToArray());
            sprdSelectChip.SelectedIndex = 0; // 默认"自动检测"
            
            // 初始化设备选择为空
            sprdSelectDevice.Items.Clear();
            sprdSelectDevice.Items.Add("自动检测");
            sprdSelectDevice.SelectedIndex = 0;
        }

        /// <summary>
        /// 更新设备列表（根据选择的芯片，直接扫描有 FDL 文件的目录）
        /// </summary>
        private void UpdateDeviceList(string chipName)
        {
            // 确保在 UI 线程执行
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateDeviceList(chipName)));
                return;
            }
            
            // 准备设备列表
            var items = new List<object>();
            items.Add("自动检测");
            
            if (!string.IsNullOrEmpty(chipName) && chipName != "自动检测")
            {
                // 直接从文件系统扫描有 FDL 的设备目录
                var deviceDirs = ScanFdlDeviceDirectories(chipName);
                
                foreach (var deviceName in deviceDirs.OrderBy(d => d))
                {
                    items.Add(deviceName);
                }
                
                if (deviceDirs.Count > 0)
                {
                    AppendLog($"[展讯] 可选设备: {deviceDirs.Count} 个", Color.Gray);
                }
                else
                {
                    AppendLog($"[展讯] {chipName} 暂无可用设备 FDL", Color.Orange);
                }
            }
            
            // 一次性更新
            sprdSelectDevice.Items.Clear();
            sprdSelectDevice.Items.AddRange(items.ToArray());
            
            // 设置默认选择
            if (sprdSelectDevice.Items.Count > 0)
            {
                sprdSelectDevice.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// 扫描芯片目录下有 FDL 文件的设备目录
        /// </summary>
        private List<string> ScanFdlDeviceDirectories(string chipName)
        {
            var result = new List<string>();
            string baseDir = GetSprdResourcesBasePath();
            
            // 根据芯片获取搜索路径
            var searchPaths = GetChipSearchPaths(chipName);
            
            foreach (var searchPath in searchPaths)
            {
                string fullPath = Path.Combine(baseDir, searchPath);
                if (!Directory.Exists(fullPath))
                    continue;
                
                // 遍历子目录
                foreach (var dir in Directory.GetDirectories(fullPath))
                {
                    // 检查该目录是否有 fdl1 或 fdl2 文件
                    var fdlFiles = Directory.GetFiles(dir, "fdl*.bin", SearchOption.AllDirectories);
                    if (fdlFiles.Length > 0)
                    {
                        string deviceName = Path.GetFileName(dir);
                        // 排除纯数字或特殊目录
                        if (!string.IsNullOrEmpty(deviceName) && 
                            !deviceName.All(char.IsDigit) &&
                            deviceName != "max" && deviceName != "1" && deviceName != "2")
                        {
                            result.Add(deviceName);
                        }
                    }
                }
            }
            
            return result.Distinct().ToList();
        }

        /// <summary>
        /// 获取芯片的 FDL 搜索路径列表
        /// </summary>
        private List<string> GetChipSearchPaths(string chipName)
        {
            var paths = new List<string>();
            
            switch (chipName.ToUpper())
            {
                case "SC8541E":
                case "SC9832E":
                    paths.Add(@"sc_sp_sl\98xx_85xx\9832E_8541E");
                    break;
                case "SC9863A":
                case "SC8581A":
                    paths.Add(@"sc_sp_sl\98xx_85xx\9863A_8581A");
                    break;
                case "SC7731E":
                    paths.Add(@"sc_sp_sl\old\7731e");
                    break;
                case "SC9850K":
                    paths.Add(@"sc_sp_sl\98xx_85xx\9850K");
                    break;
                case "UMS512":
                    paths.Add(@"ums\ums512");
                    break;
                case "UMS9230":
                case "T606":
                    paths.Add(@"ums\ums9230");
                    break;
                case "UMS312":
                    paths.Add(@"ums\ums312");
                    break;
                case "UWS6152":
                    paths.Add(@"uws\uws6152");
                    paths.Add(@"uws\uws6152E");
                    break;
                case "UWS6131":
                    paths.Add(@"uws\uws6131");
                    break;
                case "UWS6137E":
                    paths.Add(@"uws\uws6137E");
                    break;
                case "UDX710":
                    paths.Add(@"other\udx710");
                    break;
                default:
                    // 尝试通用搜索
                    paths.Add(chipName.ToLower());
                    break;
            }
            
            return paths;
        }

        /// <summary>
        /// 获取 SPD 资源基础路径
        /// </summary>
        private string GetSprdResourcesBasePath()
        {
            // 尝试多个可能的路径
            var candidates = new[]
            {
                // 1. 当前目录下的 SprdResources
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SprdResources", "sprd_fdls"),
                // 2. 项目根目录（调试时）
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "SprdResources", "sprd_fdls"),
                // 3. 上级目录
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "SprdResources", "sprd_fdls")
            };
            
            foreach (var path in candidates)
            {
                string fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath))
                    return fullPath;
            }
            
            // 默认返回第一个（可能不存在）
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SprdResources", "sprd_fdls");
        }

        /// <summary>
        /// 查找设备的 FDL 文件
        /// </summary>
        private (string fdl1, string fdl2) FindDeviceFdlFiles(string chipName, string deviceName)
        {
            string fdl1 = null;
            string fdl2 = null;
            
            string baseDir = GetSprdResourcesBasePath();
            var searchPaths = GetChipSearchPaths(chipName);
            
            foreach (var searchPath in searchPaths)
            {
                string deviceDir = Path.Combine(baseDir, searchPath, deviceName);
                if (!Directory.Exists(deviceDir))
                    continue;
                
                // 查找 FDL1
                var fdl1Files = Directory.GetFiles(deviceDir, "fdl1*.bin", SearchOption.AllDirectories);
                if (fdl1Files.Length > 0)
                {
                    // 优先选择签名版本
                    fdl1 = fdl1Files.FirstOrDefault(f => f.Contains("-sign")) ?? fdl1Files[0];
                }
                
                // 查找 FDL2
                var fdl2Files = Directory.GetFiles(deviceDir, "fdl2*.bin", SearchOption.AllDirectories);
                if (fdl2Files.Length > 0)
                {
                    fdl2 = fdl2Files.FirstOrDefault(f => f.Contains("-sign")) ?? fdl2Files[0];
                }
                
                if (fdl1 != null || fdl2 != null)
                    break;
            }
            
            return (fdl1, fdl2);
        }

        /// <summary>
        /// 绑定展讯事件
        /// </summary>
        private void BindSpreadtrumEvents()
        {
            // 芯片选择变化
            sprdSelectChip.SelectedIndexChanged += (s, e) =>
            {
                string selected = sprdSelectChip.SelectedValue?.ToString() ?? "";
                
                // 去掉 ★ 标记
                string chipName = selected.Replace(" ★", "").Trim();
                
                // 跳过分隔符
                if (selected.StartsWith("──"))
                    return;
                
                if (SprdChipList.TryGetValue(selected, out uint chipId) && chipId != 0xFFFF)
                {
                    _selectedChipId = chipId;
                    if (chipId > 0)
                    {
                        // 从数据库获取芯片详细信息
                        var chipInfo = SakuraEDL.Spreadtrum.Database.SprdFdlDatabase.GetChipById(chipId);
                        if (chipInfo != null)
                        {
                            string exploitInfo = chipInfo.HasExploit ? $" [Exploit: {chipInfo.ExploitId}]" : "";
                            AppendLog($"[展讯] 选择芯片: {chipInfo.DisplayName}{exploitInfo}", Color.Cyan);
                                                          AppendLog($"[展讯] 默认地址 - FDL1: {chipInfo.Fdl1AddressHex}, FDL2: {chipInfo.Fdl2AddressHex}", Color.Gray);
                            AppendLog($"[展讯] 提示: 可在下方自行选择 FDL 文件覆盖默认配置", Color.Gray);
                            
                            _spreadtrumController?.SetChipId(chipId);
                            
                            // 设置 FDL 默认地址 (保留已选择的文件路径)
                            _customFdl1Addr = chipInfo.Fdl1Address;
                            _customFdl2Addr = chipInfo.Fdl2Address;
                            _spreadtrumController?.SetCustomFdl1(_customFdl1Path, _customFdl1Addr);
                            _spreadtrumController?.SetCustomFdl2(_customFdl2Path, _customFdl2Addr);
                            
                            // 保持 FDL 文件输入启用，地址使用芯片默认值
                            SetFdlInputsEnabled(false, clearPaths: false);
                            
                            // 自动填充地址显示 (仅当地址框为空时)
                            if (string.IsNullOrEmpty(input5.Text))
                                input5.Text = chipInfo.Fdl1AddressHex;
                            if (string.IsNullOrEmpty(input10.Text))
                                input10.Text = chipInfo.Fdl2AddressHex;
                            
                            // 更新设备列表
                            UpdateDeviceList(chipInfo.ChipName);
                        }
                        else
                        {
                            // 回退到旧方式
                            uint fdl1Addr = SprdPlatform.GetFdl1Address(chipId);
                            uint fdl2Addr = SprdPlatform.GetFdl2Address(chipId);
                            AppendLog($"[展讯] 选择芯片: {chipName}", Color.Cyan);
                            AppendLog($"[展讯] 默认地址 - FDL1: 0x{fdl1Addr:X}, FDL2: 0x{fdl2Addr:X}", Color.Gray);
                            AppendLog($"[展讯] 提示: 可在下方自行选择 FDL 文件覆盖默认配置", Color.Gray);
                            _spreadtrumController?.SetChipId(chipId);
                            
                            // 设置 FDL 默认地址 (保留已选择的文件路径)
                            _customFdl1Addr = fdl1Addr;
                            _customFdl2Addr = fdl2Addr;
                            _spreadtrumController?.SetCustomFdl1(_customFdl1Path, _customFdl1Addr);
                            _spreadtrumController?.SetCustomFdl2(_customFdl2Path, _customFdl2Addr);
                            
                            SetFdlInputsEnabled(false, clearPaths: false);
                            if (string.IsNullOrEmpty(input5.Text))
                                input5.Text = $"0x{fdl1Addr:X}";
                            if (string.IsNullOrEmpty(input10.Text))
                                input10.Text = $"0x{fdl2Addr:X}";
                            
                            // 更新设备列表
                            UpdateDeviceList(chipName);
                        }
                    }
                    else
                    {
                        // 自动检测模式，完全启用自定义 FDL 输入
                        AppendLog("[展讯] 芯片设置为自动检测，请自行配置 FDL", Color.Gray);
                        _spreadtrumController?.SetChipId(0);
                        
                        // 启用所有 FDL 输入
                        SetFdlInputsEnabled(true, clearPaths: true);
                        
                        // 清空地址
                        input5.Text = "";
                        input10.Text = "";
                        
                        // 清空设备列表
                        UpdateDeviceList(null);
                    }
                }
            };
            
            // 设备选择变化
            // 双击设备选择器 = 扫描所有 COM 端口 (调试)
            sprdSelectDevice.DoubleClick += (s, e) => SprdScanAllComPorts();
            
            sprdSelectDevice.SelectedIndexChanged += (s, e) =>
            {
                string selected = sprdSelectDevice.SelectedValue?.ToString() ?? "";
                
                if (selected == "自动检测" || string.IsNullOrEmpty(selected))
                {
                    _customFdl1Path = null;
                    _customFdl2Path = null;
                    input2.Text = "";
                    input4.Text = "";
                    AppendLog("[展讯] 设备设置为自动检测", Color.Gray);
                    return;
                }
                
                // 设备名就是目录名
                string deviceName = selected;
                
                // 获取当前选中的芯片名称
                string chipSelected = sprdSelectChip.SelectedValue?.ToString() ?? "";
                string chipName = chipSelected.Replace(" ★", "").Trim();
                
                // 直接从文件系统查找 FDL 文件
                var fdlPaths = FindDeviceFdlFiles(chipName, deviceName);
                
                if (fdlPaths.fdl1 != null || fdlPaths.fdl2 != null)
                {
                    AppendLog($"[展讯] 选择设备: {deviceName}", Color.Cyan);
                    
                    if (fdlPaths.fdl1 != null)
                    {
                        _customFdl1Path = fdlPaths.fdl1;
                        input2.Text = Path.GetFileName(fdlPaths.fdl1);
                        AppendLog($"[展讯] FDL1: {Path.GetFileName(fdlPaths.fdl1)}", Color.Gray);
                    }
                    
                    if (fdlPaths.fdl2 != null)
                    {
                        _customFdl2Path = fdlPaths.fdl2;
                        input4.Text = Path.GetFileName(fdlPaths.fdl2);
                        AppendLog($"[展讯] FDL2: {Path.GetFileName(fdlPaths.fdl2)}", Color.Gray);
                    }
                    
                    // 更新控制器
                    _spreadtrumController?.SetCustomFdl1(_customFdl1Path, _customFdl1Addr);
                    _spreadtrumController?.SetCustomFdl2(_customFdl2Path, _customFdl2Addr);
                }
                else
                {
                    AppendLog($"[展讯] 未找到设备 FDL: {deviceName}", Color.Orange);
                }
            };

            // PAC 文件输入框双击浏览
            sprdInputPac.DoubleClick += (s, e) => SprdBrowsePac();

            // ========== FDL 自定义配置 ==========
            
            // FDL1 文件浏览 (双击 input2)
            input2.DoubleClick += (s, e) =>
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = "选择 FDL1 文件";
                    ofd.Filter = "FDL 文件 (*.bin)|*.bin|所有文件 (*.*)|*.*";
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        input2.Text = ofd.FileName;
                        _customFdl1Path = ofd.FileName;
                        AppendLog($"[展讯] FDL1 文件: {Path.GetFileName(ofd.FileName)}", Color.Cyan);
                        _spreadtrumController?.SetCustomFdl1(_customFdl1Path, _customFdl1Addr);
                    }
                }
            };

            // FDL2 文件浏览 (双击 input4)
            input4.DoubleClick += (s, e) =>
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = "选择 FDL2 文件";
                    ofd.Filter = "FDL 文件 (*.bin)|*.bin|所有文件 (*.*)|*.*";
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        input4.Text = ofd.FileName;
                        _customFdl2Path = ofd.FileName;
                        AppendLog($"[展讯] FDL2 文件: {Path.GetFileName(ofd.FileName)}", Color.Cyan);
                        _spreadtrumController?.SetCustomFdl2(_customFdl2Path, _customFdl2Addr);
                    }
                }
            };

            // FDL1 地址输入 (input5)
            input5.TextChanged += (s, e) =>
            {
                string text = input5.Text.Trim();
                if (TryParseHexAddress(text, out uint addr))
                {
                    _customFdl1Addr = addr;
                    _spreadtrumController?.SetCustomFdl1(_customFdl1Path, _customFdl1Addr);
                }
            };

            // FDL2 地址输入 (input10)
            input10.TextChanged += (s, e) =>
            {
                string text = input10.Text.Trim();
                if (TryParseHexAddress(text, out uint addr))
                {
                    _customFdl2Addr = addr;
                    _spreadtrumController?.SetCustomFdl2(_customFdl2Path, _customFdl2Addr);
                }
            };

            // 写入分区 (支持单个/多个/整个PAC)
            sprdBtnWritePartition.Click += async (s, e) => await SprdWritePartitionAsync();

            // 读取分区 (支持单个/多个)
            sprdBtnReadPartition.Click += async (s, e) => await SprdReadPartitionAsync();

            // 擦除分区
            sprdBtnErasePartition.Click += async (s, e) => await SprdErasePartitionAsync();

            // 提取 PAC
            sprdBtnExtract.Click += async (s, e) => await SprdExtractPacAsync();

            // 重启设备
            sprdBtnReboot.Click += async (s, e) => await _spreadtrumController.RebootDeviceAsync();

            // 读取分区表
            sprdBtnReadGpt.Click += async (s, e) => await SprdReadPartitionTableAsync();

            // 全选
            sprdChkSelectAll.CheckedChanged += (s, e) =>
            {
                foreach (ListViewItem item in sprdListPartitions.Items)
                {
                    item.Checked = sprdChkSelectAll.Checked;
                }
            };

            // ========== 第二行操作按钮 ==========
            sprdBtnReadImei.Click += async (s, e) => await SprdReadImeiAsync();
            sprdBtnWriteImei.Click += async (s, e) => await SprdWriteImeiAsync();
            sprdBtnBackupCalib.Click += async (s, e) => await SprdBackupCalibrationAsync();
            sprdBtnRestoreCalib.Click += async (s, e) => await SprdRestoreCalibrationAsync();
            sprdBtnFactoryReset.Click += async (s, e) => await SprdFactoryResetAsync();
            sprdBtnUnlockBL.Click += async (s, e) => await SprdUnlockBootloaderAsync();
            sprdBtnNvManager.Click += async (s, e) => await SprdOpenNvManagerAsync();

            // 设备连接事件 - 检测到设备时只显示信息，不自动连接
            _spreadtrumController.OnDeviceConnected += dev =>
            {
                SafeInvoke(() =>
                {
                    AppendLog($"[展讯] 检测到设备: {dev.ComPort} ({dev.Mode})", Color.Green);
                    
                    // 保存检测到的端口
                    _detectedSprdPort = dev.ComPort;
                    _detectedSprdMode = dev.Mode;
                    
                    // 更新右侧信息面板
                    UpdateSprdInfoPanel();
                    
                    if (dev.Mode == SakuraEDL.Spreadtrum.Common.SprdDeviceMode.Download)
                    {
                        AppendLog($"[展讯] 设备已进入下载模式", Color.Cyan);
                        AppendLog("[展讯] 请选择芯片型号或加载 PAC，然后点击[读取分区表]", Color.Yellow);
                    }
                });
            };

            _spreadtrumController.OnDeviceDisconnected += dev =>
            {
                SafeInvoke(() =>
                {
                    AppendLog($"[展讯] 设备断开: {dev.ComPort}", Color.Orange);
                    
                    // 清空检测到的端口
                    _detectedSprdPort = null;
                    _detectedSprdMode = SakuraEDL.Spreadtrum.Common.SprdDeviceMode.Unknown;
                    
                    // 更新右侧信息面板
                    UpdateSprdInfoPanel();
                });
            };

            // PAC 加载事件
            _spreadtrumController.OnPacLoaded += pac =>
            {
                SafeInvoke(() =>
                {
                    // 更新分区列表标题
                    sprdGroupPartitions.Text = $"分区列表 - {pac.Header.ProductName} ({pac.Files.Count} 个文件)";

                    // 更新分区列表
                    sprdListPartitions.Items.Clear();
                    foreach (var file in pac.Files)
                    {
                        if (file.Size == 0 || string.IsNullOrEmpty(file.FileName))
                            continue;

                        var item = new ListViewItem(file.PartitionName);
                        item.SubItems.Add(file.FileName);
                        item.SubItems.Add(FormatSize(file.Size));
                        item.SubItems.Add(file.Type.ToString());
                        item.SubItems.Add(file.Address > 0 ? $"0x{file.Address:X}" : "--");
                        item.SubItems.Add($"0x{file.DataOffset:X}");
                        item.SubItems.Add(file.IsSparse ? "是" : "否");
                        item.Tag = file;

                        // 默认选中非 FDL/XML/Userdata 文件
                        bool shouldCheck = file.Type != PacFileType.FDL1 && 
                                          file.Type != PacFileType.FDL2 &&
                                          file.Type != PacFileType.XML;
                        
                        // 如果勾选了跳过 Userdata
                        if (sprdChkSkipUserdata.Checked && file.Type == PacFileType.UserData)
                            shouldCheck = false;

                        item.Checked = shouldCheck;
                        sprdListPartitions.Items.Add(item);
                    }
                });
            };

            // 状态变化事件 - 使用右侧面板显示
            _spreadtrumController.OnStateChanged += state =>
            {
                SafeInvoke(() =>
                {
                    string statusText = "";
                    switch (state)
                    {
                        case SprdDeviceState.Connected:
                            statusText = "[展讯] 设备已连接 (ROM)";
                            uiLabel8.Text = "当前操作：展讯 ROM 模式";
                            break;
                        case SprdDeviceState.Fdl1Loaded:
                            statusText = "[展讯] FDL1 已加载";
                            uiLabel8.Text = "当前操作：FDL1 已加载";
                            break;
                        case SprdDeviceState.Fdl2Loaded:
                            statusText = "[展讯] FDL2 已加载 (可刷机)";
                            uiLabel8.Text = "当前操作：展讯 FDL2 就绪";
                            break;
                        case SprdDeviceState.Disconnected:
                            statusText = "[展讯] 设备未连接";
                            uiLabel8.Text = "当前操作：等待设备";
                            SprdClearDeviceInfo();
                            break;
                        case SprdDeviceState.Error:
                            statusText = "[展讯] 设备错误";
                            uiLabel8.Text = "当前操作：设备错误";
                            break;
                    }
                    if (!string.IsNullOrEmpty(statusText))
                        AppendLog(statusText, state == SprdDeviceState.Error ? Color.Red : Color.Cyan);
                });

                // FDL2 加载后，不再自动读取分区表（由用户操作触发）
                // 这样可以避免多次重复调用导致卡死
            };

            // 分区表加载事件
            _spreadtrumController.OnPartitionTableLoaded += partitions =>
            {
                SafeInvoke(() =>
                {
                    // 更新分区列表标题
                    sprdGroupPartitions.Text = $"分区表 (设备) - {partitions.Count} 个分区";

                    // 清空并填充分区列表
                    sprdListPartitions.Items.Clear();
                    foreach (var part in partitions)
                    {
                        var item = new ListViewItem(part.Name);
                        item.SubItems.Add("--");  // 文件名 (设备分区没有文件名)
                        item.SubItems.Add(FormatSize(part.Size));
                        item.SubItems.Add("Partition");
                        item.SubItems.Add($"0x{part.Offset:X}");  // 偏移作为地址
                        item.SubItems.Add($"0x{part.Offset:X}");
                        item.SubItems.Add("--");  // Sparse
                        item.Tag = part;
                        item.Checked = false;  // 默认不选中
                        sprdListPartitions.Items.Add(item);
                    }
                });
            };

            // 进度事件
            _spreadtrumController.OnProgress += (current, total) =>
            {
                SafeInvoke(() =>
                {
                    int percent = total > 0 ? (int)(current * 100 / total) : 0;
                    uiProcessBar1.Value = percent;
                });
            };

            // 分区搜索
            sprdSelectSearch.TextChanged += (s, e) =>
            {
                string search = sprdSelectSearch.Text.ToLower();
                foreach (ListViewItem item in sprdListPartitions.Items)
                {
                    item.BackColor = item.Text.ToLower().Contains(search) && !string.IsNullOrEmpty(search)
                        ? Color.LightYellow
                        : Color.White;
                }
            };

            // ========== 双击分区选择外部镜像刷写 ==========
            sprdListPartitions.DoubleClick += async (s, e) =>
            {
                if (sprdListPartitions.SelectedItems.Count == 0)
                    return;

                // 获取选中的分区
                var selectedPartitions = new List<string>();
                foreach (ListViewItem item in sprdListPartitions.SelectedItems)
                {
                    selectedPartitions.Add(item.Text);
                }

                if (selectedPartitions.Count == 1)
                {
                    // 单个分区 - 选择单个镜像
                    await SprdFlashSinglePartitionAsync(selectedPartitions[0]);
                }
                else
                {
                    // 多个分区 - 选择文件夹
                    await SprdFlashMultiplePartitionsAsync(selectedPartitions);
                }
            };
        }

        /// <summary>
        /// 刷写单个分区 (选择外部镜像)
        /// </summary>
        private async Task SprdFlashSinglePartitionAsync(string partitionName)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = $"选择 {partitionName} 分区镜像";
                ofd.Filter = "镜像文件 (*.img;*.bin)|*.img;*.bin|所有文件 (*.*)|*.*";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    var result = MessageBox.Show(
                        $"确定要将 {Path.GetFileName(ofd.FileName)} 刷写到 {partitionName} 分区吗？\n\n此操作不可撤销！",
                        "确认刷写",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (result == DialogResult.Yes)
                    {
                        AppendLog($"[展讯] 刷写分区: {partitionName} <- {Path.GetFileName(ofd.FileName)}", Color.Cyan);
                        bool success = await _spreadtrumController.FlashImageFileAsync(partitionName, ofd.FileName);
                        
                        if (success)
                        {
                            MessageBox.Show($"分区 {partitionName} 刷写成功！", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 刷写多个分区 (从文件夹匹配)
        /// </summary>
        private async Task SprdFlashMultiplePartitionsAsync(List<string> partitionNames)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = $"选择包含镜像文件的文件夹\n将自动匹配: {string.Join(", ", partitionNames)}";

                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    // 查找匹配的文件
                    var matchedFiles = new Dictionary<string, string>();

                    foreach (var partName in partitionNames)
                    {
                        // 尝试多种文件名格式
                        string[] patterns = new[]
                        {
                            $"{partName}.img",
                            $"{partName}.bin",
                            $"{partName}_a.img",
                            $"{partName}_b.img"
                        };

                        foreach (var pattern in patterns)
                        {
                            string filePath = Path.Combine(fbd.SelectedPath, pattern);
                            if (File.Exists(filePath))
                            {
                                matchedFiles[partName] = filePath;
                                break;
                            }
                        }
                    }

                    if (matchedFiles.Count == 0)
                    {
                        MessageBox.Show("未找到匹配的镜像文件！\n\n文件名应为: 分区名.img 或 分区名.bin", "未找到文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // 显示匹配结果
                    var msg = $"找到 {matchedFiles.Count}/{partitionNames.Count} 个匹配文件:\n\n";
                    foreach (var kvp in matchedFiles)
                    {
                        msg += $"  {kvp.Key} <- {Path.GetFileName(kvp.Value)}\n";
                    }
                    msg += "\n确定要刷写吗？此操作不可撤销！";

                    var result = MessageBox.Show(msg, "确认刷写", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                    if (result == DialogResult.Yes)
                    {
                        await _spreadtrumController.FlashMultipleImagesAsync(matchedFiles);
                    }
                }
            }
        }

        /// <summary>
        /// 浏览 PAC 文件
        /// </summary>
        private void SprdBrowsePac()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择展讯 PAC 固件包";
                ofd.Filter = "PAC 固件包 (*.pac)|*.pac|所有文件 (*.*)|*.*";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    sprdInputPac.Text = ofd.FileName;
                    _spreadtrumController.LoadPacFirmware(ofd.FileName);
                }
            }
        }

        /// <summary>
        /// 写入分区 - 简化版，直接从分区表选择
        /// </summary>
        private async System.Threading.Tasks.Task SprdWritePartitionAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            // 获取选中的分区名
            var partitions = GetSprdSelectedPartitions();
            
            // 没有选中分区时，提供选择
            if (partitions.Count == 0)
            {
                // 如果有 PAC，询问是否刷写整个 PAC
                if (_spreadtrumController.CurrentPac != null)
                {
                    var confirm = MessageBox.Show(
                        "未选择分区。是否刷写整个 PAC 固件包？",
                        "写入分区",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    
                    if (confirm == DialogResult.Yes)
                    {
                        await SprdWriteEntirePacAsync();
                    }
                    return;
                }
                
                AppendLog("[展讯] 请在分区表中选择要写入的分区", Color.Orange);
                return;
            }

            // 单个分区 - 选择单个文件
            if (partitions.Count == 1)
            {
                string partName = partitions[0].name;
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = $"选择要写入 {partName} 分区的文件";
                    ofd.Filter = "镜像文件 (*.img;*.bin)|*.img;*.bin|所有文件 (*.*)|*.*";

                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        AppendLog($"[展讯] 写入 {partName}...", Color.Cyan);
                        await _spreadtrumController.FlashPartitionAsync(partName, ofd.FileName);
                    }
                }
                return;
            }

            // 多个分区 - 选择目录，自动匹配文件名
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = $"选择包含 {partitions.Count} 个分区镜像的目录\n(文件名需与分区名匹配，如 boot.img)";
                
                if (fbd.ShowDialog() != DialogResult.OK) return;

                string inputDir = fbd.SelectedPath;
                int success = 0, fail = 0, skip = 0;

                foreach (var (partName, _) in partitions)
                {
                    // 自动查找匹配的文件
                    string imgPath = System.IO.Path.Combine(inputDir, $"{partName}.img");
                    string binPath = System.IO.Path.Combine(inputDir, $"{partName}.bin");
                    string filePath = System.IO.File.Exists(imgPath) ? imgPath : 
                                     (System.IO.File.Exists(binPath) ? binPath : null);

                    if (filePath == null)
                    {
                        AppendLog($"[展讯] 跳过 {partName} (未找到文件)", Color.Gray);
                        skip++;
                        continue;
                    }

                    AppendLog($"[展讯] 写入 {partName}...", Color.Cyan);
                    if (await _spreadtrumController.FlashPartitionAsync(partName, filePath))
                        success++;
                    else
                        fail++;
                }

                AppendLog($"[展讯] 写入完成: {success} 成功, {fail} 失败, {skip} 跳过", 
                    fail > 0 ? Color.Orange : Color.Green);
            }
        }

        private enum WriteMode { Cancel, SingleImage, MultipleImages, EntirePac }

        /// <summary>
        /// 显示写入模式选择对话框 (已弃用，保留兼容)
        /// </summary>
        private WriteMode ShowWriteModeDialog(int selectedCount)
        {
            // 简化后不再使用此对话框
            return WriteMode.Cancel;
        }

        /// <summary>
        /// 写入单个镜像文件到选中的分区
        /// </summary>
        private async System.Threading.Tasks.Task SprdWriteSingleImageAsync(List<string> selectedPartitions)
        {
            // 如果没有选中分区，让用户选择
            string targetPartition;
            if (selectedPartitions.Count == 0)
            {
                // 让用户输入分区名
                targetPartition = Microsoft.VisualBasic.Interaction.InputBox(
                    "请输入目标分区名称:",
                    "选择分区",
                    "boot",
                    -1, -1);
                if (string.IsNullOrEmpty(targetPartition))
                    return;
            }
            else if (selectedPartitions.Count == 1)
            {
                targetPartition = selectedPartitions[0];
            }
            else
            {
                // 多个选中，依次写入
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = "选择镜像文件";
                    ofd.Filter = "镜像文件 (*.img;*.bin)|*.img;*.bin|所有文件 (*.*)|*.*";
                    ofd.Multiselect = true;

                    if (ofd.ShowDialog() != DialogResult.OK)
                        return;

                    if (ofd.FileNames.Length != selectedPartitions.Count)
                    {
                        MessageBox.Show(
                            $"选择的文件数量 ({ofd.FileNames.Length}) 与分区数量 ({selectedPartitions.Count}) 不匹配！\n\n" +
                            "请确保文件数量与选中的分区数量一致。",
                            "错误",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }

                    var confirm = MessageBox.Show(
                        $"确定要将 {ofd.FileNames.Length} 个文件写入对应分区吗？\n\n此操作不可撤销！",
                        "确认写入",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (confirm != DialogResult.Yes)
                        return;

                    // 按顺序写入
                    for (int i = 0; i < selectedPartitions.Count; i++)
                    {
                        AppendLog($"[展讯] 写入 {selectedPartitions[i]}...", Color.Cyan);
                        bool success = await _spreadtrumController.FlashImageFileAsync(
                            selectedPartitions[i], ofd.FileNames[i]);
                        if (!success)
                        {
                            AppendLog($"[展讯] 写入 {selectedPartitions[i]} 失败", Color.Red);
                            return;
                        }
                    }
                    AppendLog("[展讯] 所有分区写入完成", Color.Green);
                    return;
                }
            }

            // 单个分区写入
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = $"选择要写入到 {targetPartition} 的镜像文件";
                ofd.Filter = "镜像文件 (*.img;*.bin)|*.img;*.bin|所有文件 (*.*)|*.*";

                if (ofd.ShowDialog() != DialogResult.OK)
                    return;

                var confirm = MessageBox.Show(
                    $"确定要将 \"{System.IO.Path.GetFileName(ofd.FileName)}\" 写入到分区 \"{targetPartition}\" 吗？\n\n此操作不可撤销！",
                    "确认写入",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirm != DialogResult.Yes)
                    return;

                AppendLog($"[展讯] 写入 {targetPartition}...", Color.Cyan);
                bool success = await _spreadtrumController.FlashImageFileAsync(targetPartition, ofd.FileName);
                if (success)
                    AppendLog($"[展讯] {targetPartition} 写入完成", Color.Green);
            }
        }

        /// <summary>
        /// 批量写入多个镜像文件 (自动匹配分区名)
        /// </summary>
        private async System.Threading.Tasks.Task SprdWriteMultipleImagesAsync()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择镜像文件 (文件名应为分区名)";
                ofd.Filter = "镜像文件 (*.img;*.bin)|*.img;*.bin|所有文件 (*.*)|*.*";
                ofd.Multiselect = true;

                if (ofd.ShowDialog() != DialogResult.OK)
                    return;

                // 构建分区-文件映射
                var partitionFiles = new Dictionary<string, string>();
                foreach (var file in ofd.FileNames)
                {
                    string partName = System.IO.Path.GetFileNameWithoutExtension(file);
                    partitionFiles[partName] = file;
                }

                string fileList = string.Join("\n", partitionFiles.Keys);
                var confirm = MessageBox.Show(
                    $"将要写入以下 {partitionFiles.Count} 个分区:\n\n{fileList}\n\n确定继续吗？",
                    "确认批量写入",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (confirm != DialogResult.Yes)
                    return;

                AppendLog($"[展讯] 开始批量写入 {partitionFiles.Count} 个分区...", Color.Cyan);
                bool success = await _spreadtrumController.FlashMultipleImagesAsync(partitionFiles);
                if (success)
                    AppendLog("[展讯] 批量写入完成", Color.Green);
            }
        }

        /// <summary>
        /// 刷写整个 PAC 固件包
        /// </summary>
        private async System.Threading.Tasks.Task SprdWriteEntirePacAsync()
        {
            if (_spreadtrumController.CurrentPac == null)
            {
                AppendLog("[展讯] 请先选择 PAC 固件包", Color.Orange);
                return;
            }

            // 获取选中的分区 (勾选的)
            var selectedPartitions = new List<string>();
            foreach (ListViewItem item in sprdListPartitions.CheckedItems)
            {
                selectedPartitions.Add(item.Text);
            }

            if (selectedPartitions.Count == 0)
            {
                // 没有勾选，刷写所有分区
                var result = MessageBox.Show(
                    "没有选中任何分区，将刷写 PAC 中的所有分区。\n\n确定继续吗？",
                    "确认刷机",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes)
                    return;

                // 添加所有分区
                foreach (ListViewItem item in sprdListPartitions.Items)
                {
                    selectedPartitions.Add(item.Text);
                }
            }
            else
            {
                var result = MessageBox.Show(
                    $"确定要刷写 {selectedPartitions.Count} 个分区吗？\n\n此操作不可撤销！",
                    "确认刷机",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes)
                    return;
            }

            // 如果未连接，先等待连接
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 等待设备连接，请将设备连接到电脑 (按住音量下键)...", Color.Yellow);
                bool connected = await _spreadtrumController.WaitAndConnectAsync(60);
                if (!connected)
                {
                    AppendLog("[展讯] 设备连接超时", Color.Red);
                    return;
                }
            }

            bool success = await _spreadtrumController.StartFlashAsync(selectedPartitions);

            // 刷机后重启
            if (success && sprdChkRebootAfter.Checked)
            {
                await _spreadtrumController.RebootDeviceAsync();
            }
        }

        /// <summary>
        /// 读取分区表 - 自动完成连接、FDL下载、读取全流程
        /// </summary>
        private async System.Threading.Tasks.Task SprdReadPartitionTableAsync()
        {
            // 1. 检查连接状态，未连接则尝试连接检测到的设备
            if (!_spreadtrumController.IsConnected)
            {
                if (string.IsNullOrEmpty(_detectedSprdPort))
                {
                    AppendLog("[展讯] 未检测到设备，请将设备连接到电脑并进入下载模式", Color.Orange);
                    AppendLog("[展讯] (关机状态下按住音量下键，然后连接 USB)", Color.Gray);
                    return;
                }
                
                AppendLog($"[展讯] 正在连接设备: {_detectedSprdPort}...", Color.Cyan);
                bool connected = await _spreadtrumController.ConnectDeviceAsync(_detectedSprdPort);
                if (!connected)
                {
                    AppendLog("[展讯] 设备连接失败", Color.Red);
                    return;
                }
                AppendLog("[展讯] 设备连接成功", Color.Green);
            }

            // 2. 如果已在 FDL2 模式，直接读取分区表
            if (_spreadtrumController.CurrentStage == FdlStage.FDL2)
            {
                AppendLog("[展讯] 正在读取分区表...", Color.Cyan);
                await _spreadtrumController.ReadPartitionTableAsync();
                return;
            }

            // 3. BROM 模式需要下载 FDL
            if (_spreadtrumController.IsBromMode)
            {
                AppendLog("[展讯] 设备处于 BROM 模式，需要下载 FDL...", Color.Yellow);
                
                // 检查 FDL 来源优先级：PAC > 自定义 FDL > 数据库芯片配置
                bool hasFdlConfig = false;
                
                // 方式1：PAC 中的 FDL (最高优先级)
                if (_spreadtrumController.CurrentPac != null)
                {
                    AppendLog("[展讯] 使用 PAC 中的 FDL 初始化设备...", Color.Cyan);
                    hasFdlConfig = true;
                }
                // 方式2：用户自定义 FDL 文件 (第二优先级)
                else if (!string.IsNullOrEmpty(_customFdl1Path) && System.IO.File.Exists(_customFdl1Path) &&
                         !string.IsNullOrEmpty(_customFdl2Path) && System.IO.File.Exists(_customFdl2Path))
                {
                    AppendLog("[展讯] 使用自定义 FDL 文件...", Color.Cyan);
                    AppendLog($"[展讯] FDL1: {System.IO.Path.GetFileName(_customFdl1Path)}", Color.Gray);
                    AppendLog($"[展讯] FDL2: {System.IO.Path.GetFileName(_customFdl2Path)}", Color.Gray);
                    
                    // 如果选择了芯片，使用芯片的地址配置
                    if (_selectedChipId > 0 && _selectedChipId != 0xFFFF)
                    {
                        var chipInfo = SakuraEDL.Spreadtrum.Database.SprdFdlDatabase.GetChipById(_selectedChipId);
                        if (chipInfo != null)
                        {
                            AppendLog($"[展讯] 使用芯片地址配置: {chipInfo.ChipName}", Color.Gray);
                            _spreadtrumController.SetCustomFdl1(_customFdl1Path, chipInfo.Fdl1Address);
                            _spreadtrumController.SetCustomFdl2(_customFdl2Path, chipInfo.Fdl2Address);
                        }
                        else
                        {
                            _spreadtrumController.SetCustomFdl1(_customFdl1Path, _customFdl1Addr);
                            _spreadtrumController.SetCustomFdl2(_customFdl2Path, _customFdl2Addr);
                        }
                    }
                    else
                    {
                        _spreadtrumController.SetCustomFdl1(_customFdl1Path, _customFdl1Addr);
                        _spreadtrumController.SetCustomFdl2(_customFdl2Path, _customFdl2Addr);
                    }
                    hasFdlConfig = true;
                }
                // 方式3：数据库芯片配置 (第三优先级)
                else if (_selectedChipId > 0 && _selectedChipId != 0xFFFF)
                {
                    var chipInfo = SakuraEDL.Spreadtrum.Database.SprdFdlDatabase.GetChipById(_selectedChipId);
                    if (chipInfo != null)
                    {
                        AppendLog($"[展讯] 使用芯片配置: {chipInfo.ChipName}", Color.Cyan);
                        AppendLog($"[展讯] FDL1 地址: {chipInfo.Fdl1AddressHex}, FDL2 地址: {chipInfo.Fdl2AddressHex}", Color.Gray);
                        
                        // 检查是否有该芯片的设备 FDL 文件
                        var devices = SakuraEDL.Spreadtrum.Database.SprdFdlDatabase.GetDeviceNames(chipInfo.ChipName);
                        if (devices.Length > 0)
                        {
                            // 使用第一个设备的 FDL（或让用户选择）
                            var deviceFdl = SakuraEDL.Spreadtrum.Database.SprdFdlDatabase.GetDeviceFdlsByChip(chipInfo.ChipName).FirstOrDefault();
                            if (deviceFdl != null)
                            {
                                string fdl1Path = SakuraEDL.Spreadtrum.Database.SprdFdlDatabase.GetFdlPath(deviceFdl, true);
                                string fdl2Path = SakuraEDL.Spreadtrum.Database.SprdFdlDatabase.GetFdlPath(deviceFdl, false);
                                
                                if (System.IO.File.Exists(fdl1Path) && System.IO.File.Exists(fdl2Path))
                                {
                                    AppendLog($"[展讯] 使用设备 FDL: {deviceFdl.DeviceName}", Color.Gray);
                                    _spreadtrumController.SetCustomFdl1(fdl1Path, chipInfo.Fdl1Address);
                                    _spreadtrumController.SetCustomFdl2(fdl2Path, chipInfo.Fdl2Address);
                                    hasFdlConfig = true;
                                }
                            }
                        }
                        
                        if (!hasFdlConfig)
                        {
                            // 数据库没有 FDL，提示用户手动选择
                            AppendLog("[展讯] 数据库中未找到该芯片的 FDL 文件", Color.Orange);
                            AppendLog("[展讯] 请双击 FDL1/FDL2 输入框选择文件", Color.Orange);
                            return;
                        }
                    }
                }
                
                if (!hasFdlConfig)
                {
                    AppendLog("[展讯] 错误: 没有可用的 FDL 配置", Color.Red);
                    AppendLog("[展讯] 请执行以下任一操作:", Color.Orange);
                    AppendLog("[展讯]   1. 加载 PAC 固件包", Color.Gray);
                    AppendLog("[展讯]   2. 双击 FDL1/FDL2 输入框选择文件", Color.Gray);
                    AppendLog("[展讯]   3. 选择芯片型号 (数据库中需要有对应的 FDL)", Color.Gray);
                    return;
                }
                
                // 初始化设备（下载 FDL1 和 FDL2）
                AppendLog("[展讯] 正在初始化设备 (下载 FDL)...", Color.Yellow);
                bool initialized = await _spreadtrumController.InitializeDeviceAsync();
                if (!initialized)
                {
                    AppendLog("[展讯] 设备初始化失败", Color.Red);
                    return;
                }
                AppendLog("[展讯] 设备初始化完成", Color.Green);
            }

            // 4. 读取分区表
            AppendLog("[展讯] 正在读取分区表...", Color.Cyan);
            await _spreadtrumController.ReadPartitionTableAsync();
        }

        /// <summary>
        /// 读取分区 - 简化版，直接从分区表选择
        /// </summary>
        private async System.Threading.Tasks.Task SprdReadPartitionAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            // 获取选中的分区 (勾选优先，否则使用蓝色选中)
            var partitions = GetSprdSelectedPartitions();
            if (partitions.Count == 0)
            {
                AppendLog("[展讯] 请在分区表中选择要读取的分区", Color.Orange);
                return;
            }

            // 单个分区 - 选择保存文件
            if (partitions.Count == 1)
            {
                var (partName, size) = partitions[0];
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Title = $"保存 {partName}";
                    sfd.FileName = $"{partName}.img";
                    sfd.Filter = "镜像文件 (*.img)|*.img|所有文件 (*.*)|*.*";

                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        AppendLog($"[展讯] 读取分区 {partName}...", Color.Cyan);
                        await _spreadtrumController.ReadPartitionToFileAsync(partName, sfd.FileName, size);
                    }
                }
                return;
            }

            // 多个分区 - 选择保存目录
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = $"选择保存 {partitions.Count} 个分区的目录";
                
                if (fbd.ShowDialog() != DialogResult.OK) return;

                string outputDir = fbd.SelectedPath;
                int success = 0, fail = 0;

                foreach (var (partName, size) in partitions)
                {
                    string outputPath = System.IO.Path.Combine(outputDir, $"{partName}.img");
                    AppendLog($"[展讯] 读取 {partName}...", Color.Cyan);
                    
                    if (await _spreadtrumController.ReadPartitionToFileAsync(partName, outputPath, size))
                        success++;
                    else
                        fail++;
                }

                AppendLog($"[展讯] 读取完成: {success} 成功, {fail} 失败", 
                    fail > 0 ? Color.Orange : Color.Green);
            }
        }

        /// <summary>
        /// 获取展讯分区表中选中的分区 (勾选优先，否则使用选中)
        /// </summary>
        private List<(string name, uint size)> GetSprdSelectedPartitions()
        {
            var result = new List<(string name, uint size)>();
            
            // 勾选优先，否则使用蓝色选中
            var items = sprdListPartitions.CheckedItems.Count > 0 
                ? (System.Collections.IEnumerable)sprdListPartitions.CheckedItems 
                : sprdListPartitions.SelectedItems;

            foreach (ListViewItem item in items)
            {
                string partName = item.Text;
                
                // 获取分区大小 (优先从控制器获取真实大小)
                uint size = _spreadtrumController.GetPartitionSize(partName);
                
                // 如果没有，从列表解析
                if (size == 0 && item.SubItems.Count > 2)
                    TryParseSize(item.SubItems[2].Text, out size);
                
                // 默认 100MB
                if (size == 0)
                    size = 100 * 1024 * 1024;
                
                result.Add((partName, size));
            }
            
            return result;
        }

        /// <summary>
        /// 解析大小字符串 (如 "100 MB", "1.5 GB")
        /// </summary>
        private bool TryParseSize(string sizeText, out uint size)
        {
            size = 0;
            if (string.IsNullOrEmpty(sizeText))
                return false;

            sizeText = sizeText.Trim().ToUpper();
            
            try
            {
                if (sizeText.EndsWith("GB"))
                {
                    double gb = double.Parse(sizeText.Replace("GB", "").Trim());
                    size = (uint)(gb * 1024 * 1024 * 1024);
                    return true;
                }
                else if (sizeText.EndsWith("MB"))
                {
                    double mb = double.Parse(sizeText.Replace("MB", "").Trim());
                    size = (uint)(mb * 1024 * 1024);
                    return true;
                }
                else if (sizeText.EndsWith("KB"))
                {
                    double kb = double.Parse(sizeText.Replace("KB", "").Trim());
                    size = (uint)(kb * 1024);
                    return true;
                }
                else if (sizeText.EndsWith("B"))
                {
                    size = uint.Parse(sizeText.Replace("B", "").Trim());
                    return true;
                }
                else
                {
                    // 尝试直接解析为数字
                    size = uint.Parse(sizeText);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 擦除分区 - 简化版，支持批量擦除
        /// </summary>
        private async System.Threading.Tasks.Task SprdErasePartitionAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            var partitions = GetSprdSelectedPartitions();
            if (partitions.Count == 0)
            {
                AppendLog("[展讯] 请在分区表中选择要擦除的分区", Color.Orange);
                return;
            }

            // 确认擦除
            string partNames = string.Join(", ", partitions.ConvertAll(p => p.name));
            string message = partitions.Count == 1 
                ? $"确定要擦除分区 \"{partitions[0].name}\" 吗？"
                : $"确定要擦除 {partitions.Count} 个分区吗？\n\n{partNames}";

            var result = MessageBox.Show(
                message + "\n\n此操作不可撤销！",
                "确认擦除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            int success = 0, fail = 0;
            foreach (var (partName, _) in partitions)
            {
                AppendLog($"[展讯] 擦除 {partName}...", Color.Yellow);
                if (await _spreadtrumController.ErasePartitionAsync(partName))
                    success++;
                else
                    fail++;
            }

            if (partitions.Count > 1)
            {
                AppendLog($"[展讯] 擦除完成: {success} 成功, {fail} 失败", 
                    fail > 0 ? Color.Orange : Color.Green);
            }
        }

        /// <summary>
        /// 提取 PAC 文件
        /// </summary>
        private async System.Threading.Tasks.Task SprdExtractPacAsync()
        {
            if (_spreadtrumController.CurrentPac == null)
            {
                AppendLog("[展讯] 请先选择 PAC 固件包", Color.Orange);
                return;
            }

            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择提取目录";
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    await _spreadtrumController.ExtractPacAsync(fbd.SelectedPath);
                    AppendLog($"[展讯] PAC 提取完成: {fbd.SelectedPath}", Color.Green);
                }
            }
        }

        /// <summary>
        /// 格式化文件大小
        /// </summary>
        private string FormatSize(ulong size)
        {
            if (size >= 1024UL * 1024 * 1024)
                return $"{size / (1024.0 * 1024 * 1024):F2} GB";
            if (size >= 1024 * 1024)
                return $"{size / (1024.0 * 1024):F2} MB";
            if (size >= 1024)
                return $"{size / 1024.0:F2} KB";
            return $"{size} B";
        }

        /// <summary>
        /// 解析十六进制地址 (支持 0x 前缀)
        /// </summary>
        private bool TryParseHexAddress(string text, out uint address)
        {
            address = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim();
            
            // 移除 0x 或 0X 前缀
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);

            return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out address);
        }

        /// <summary>
        /// 设置 FDL 输入控件的启用状态
        /// </summary>
        /// <param name="enabled">是否启用</param>
        /// <param name="clearPaths">是否清除已选择的文件路径</param>
        private void SetFdlInputsEnabled(bool enabled, bool clearPaths = false)
        {
            // FDL1 文件 - 始终启用，允许用户覆盖
            input2.Enabled = true;
            // FDL2 文件 - 始终启用，允许用户覆盖
            input4.Enabled = true;
            // FDL1 地址 - 根据参数决定
            input5.Enabled = enabled;
            // FDL2 地址 - 根据参数决定
            input10.Enabled = enabled;

            if (clearPaths)
            {
                // 仅在明确要求时清除文件路径
                _customFdl1Path = null;
                _customFdl2Path = null;
                input2.Text = "";
                input4.Text = "";
            }
        }

        /// <summary>
        /// 自动检测安全信息和漏洞
        /// </summary>
        private async Task SprdAutoDetectSecurityAsync()
        {
            try
            {
                AppendLog("[展讯] 自动检测安全信息...", Color.Gray);

                // 1. 读取安全信息
                var secInfo = await _spreadtrumController.GetSecurityInfoAsync();
                if (secInfo != null)
                {
                    // 显示安全状态
                    if (!secInfo.IsSecureBootEnabled)
                    {
                        AppendLog("[展讯] ✓ 安全启动: 未启用 (Unfused) - 可刷写任意固件", Color.Green);
                    }
                    else
                    {
                        AppendLog("[展讯] 安全启动: 已启用", Color.Yellow);
                        
                        if (secInfo.IsEfuseLocked)
                            AppendLog("[展讯]   eFuse: 已锁定", Color.Gray);
                        
                        if (secInfo.IsAntiRollbackEnabled)
                            AppendLog($"[展讯]   防回滚: 已启用 (版本 {secInfo.SecurityVersion})", Color.Gray);
                    }
                }

                // 2. 自动检测漏洞
                var vulnResult = _spreadtrumController.CheckVulnerability();
                if (vulnResult != null && vulnResult.HasVulnerability)
                {
                    AppendLog($"[展讯] ✓ 检测到 {vulnResult.AvailableExploits.Count} 个可用漏洞", Color.Yellow);
                    AppendLog($"[展讯]   推荐: {vulnResult.RecommendedExploit}", Color.Gray);
                }

                // 3. 读取 Flash 信息
                var flashInfo = await _spreadtrumController.GetFlashInfoAsync();
                if (flashInfo != null)
                {
                    AppendLog($"[展讯] Flash: {flashInfo}", Color.Gray);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[展讯] 安全检测异常: {ex.Message}", Color.Orange);
            }
        }

        /// <summary>
        /// 更新右上角设备信息
        /// </summary>
        private async Task SprdUpdateDeviceInfoAsync()
        {
            try
            {
                // 读取芯片信息
                string chipName = await _spreadtrumController.ReadChipInfoAsync();
                if (!string.IsNullOrEmpty(chipName))
                {
                    SafeInvoke(() =>
                    {
                        uiLabel9.Text = $"品牌：Spreadtrum/Unisoc";
                        uiLabel11.Text = $"芯片：{chipName}";
                    });
                }

                // 读取 Flash 信息
                var flashInfo = await _spreadtrumController.GetFlashInfoAsync();
                if (flashInfo != null)
                {
                    SafeInvoke(() =>
                    {
                        uiLabel13.Text = $"存储：{flashInfo}";
                    });
                }

                // 尝试读取 IMEI
                string imei = await _spreadtrumController.ReadImeiAsync();
                if (!string.IsNullOrEmpty(imei))
                {
                    SafeInvoke(() =>
                    {
                        uiLabel10.Text = $"IMEI：{imei}";
                    });
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[展讯] 读取设备信息异常: {ex.Message}", Color.Orange);
            }
        }

        /// <summary>
        /// 清空设备信息显示
        /// </summary>
        private void SprdClearDeviceInfo()
        {
            uiLabel9.Text = "平台：Spreadtrum";
            uiLabel11.Text = "芯片：等待连接";
            uiLabel13.Text = "阶段：--";
            uiLabel10.Text = "IMEI：--";
            uiLabel3.Text = "端口：等待连接";
            uiLabel12.Text = "模式：--";
            uiLabel14.Text = "状态：未连接";
        }

        /// <summary>
        /// 更新右侧信息面板为展讯专用显示
        /// </summary>
        private void UpdateSprdInfoPanel()
        {
            if (_spreadtrumController != null && _spreadtrumController.IsConnected)
            {
                // 已连接状态
                string chipName = "未知";
                uint chipId = _spreadtrumController.GetChipId();
                if (chipId > 0)
                {
                    chipName = SakuraEDL.Spreadtrum.Protocol.SprdPlatform.GetPlatformName(chipId);
                }
                
                string stageStr = _spreadtrumController.CurrentStage.ToString();
                
                uiLabel9.Text = "平台：Spreadtrum";
                uiLabel11.Text = $"芯片：{chipName}";
                uiLabel13.Text = $"阶段：{stageStr}";
                uiLabel10.Text = "IMEI：--";
                uiLabel3.Text = $"端口：{_detectedSprdPort ?? "--"}";
                uiLabel12.Text = $"模式：{_detectedSprdMode}";
                uiLabel14.Text = "状态：已连接";
            }
            else if (!string.IsNullOrEmpty(_detectedSprdPort))
            {
                // 检测到设备但未连接
                uiLabel9.Text = "平台：Spreadtrum";
                uiLabel11.Text = "芯片：等待初始化";
                uiLabel13.Text = "阶段：--";
                uiLabel10.Text = "IMEI：--";
                uiLabel3.Text = $"端口：{_detectedSprdPort}";
                uiLabel12.Text = $"模式：{_detectedSprdMode}";
                uiLabel14.Text = "状态：待连接";
            }
            else
            {
                // 未检测到设备
                SprdClearDeviceInfo();
            }
        }

        #region IMEI 读写

        /// <summary>
        /// 备份校准数据
        /// </summary>
        private async Task SprdBackupCalibrationAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择校准数据备份目录";
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    AppendLog("[展讯] 开始备份校准数据...", Color.Cyan);
                    bool success = await _spreadtrumController.BackupCalibrationDataAsync(fbd.SelectedPath);
                    if (success)
                    {
                        System.Diagnostics.Process.Start("explorer.exe", fbd.SelectedPath);
                    }
                }
            }
        }

        /// <summary>
        /// 恢复校准数据
        /// </summary>
        private async Task SprdRestoreCalibrationAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择包含校准数据备份的目录";
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    var result = MessageBox.Show(
                        "确定要恢复校准数据吗？\n\n此操作将覆盖设备当前的校准数据！",
                        "确认恢复",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (result == DialogResult.Yes)
                    {
                        AppendLog("[展讯] 开始恢复校准数据...", Color.Cyan);
                        await _spreadtrumController.RestoreCalibrationDataAsync(fbd.SelectedPath);
                    }
                }
            }
        }

        /// <summary>
        /// 恢复出厂设置
        /// </summary>
        private async Task SprdFactoryResetAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            var result = MessageBox.Show(
                "确定要恢复出厂设置吗？\n\n此操作将擦除以下数据：\n- 用户数据 (userdata)\n- 缓存 (cache)\n- 元数据 (metadata)\n\n此操作不可撤销！",
                "确认恢复出厂",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                AppendLog("[展讯] 执行恢复出厂设置...", Color.Yellow);
                bool success = await _spreadtrumController.FactoryResetAsync();
                if (success)
                {
                    MessageBox.Show("恢复出厂设置完成！\n\n设备将自动重启。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    await _spreadtrumController.RebootDeviceAsync();
                }
            }
        }

        /// <summary>
        /// 读取 IMEI
        /// </summary>
        private async Task SprdReadImeiAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            AppendLog("[展讯] 读取 IMEI...", Color.Cyan);
            
            string imei = await _spreadtrumController.ReadImeiAsync();
            if (!string.IsNullOrEmpty(imei))
            {
                AppendLog($"[展讯] IMEI: {imei}", Color.Green);
                SafeInvoke(() =>
                {
                    uiLabel10.Text = $"IMEI：{imei}";
                });
                
                // 复制到剪贴板
                Clipboard.SetText(imei);
                AppendLog("[展讯] IMEI 已复制到剪贴板", Color.Gray);
            }
            else
            {
                AppendLog("[展讯] 读取 IMEI 失败", Color.Red);
            }
        }

        /// <summary>
        /// 写入 IMEI
        /// </summary>
        private async Task SprdWriteImeiAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            // 弹出输入框
            string newImei = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入新的 IMEI (15位数字):",
                "写入 IMEI",
                "",
                -1, -1);

            if (string.IsNullOrEmpty(newImei))
            {
                AppendLog("[展讯] 取消写入 IMEI", Color.Gray);
                return;
            }

            // 验证 IMEI 格式
            newImei = newImei.Trim();
            if (newImei.Length != 15 || !newImei.All(char.IsDigit))
            {
                AppendLog("[展讯] IMEI 格式错误，应为15位数字", Color.Red);
                MessageBox.Show("IMEI 格式错误！\n\nIMEI 应为15位数字", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 确认
            var result = MessageBox.Show(
                $"确定要将 IMEI 写入为:\n\n{newImei}\n\n此操作可能影响设备网络功能！",
                "确认写入 IMEI",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            AppendLog($"[展讯] 写入 IMEI: {newImei}...", Color.Yellow);

            bool success = await _spreadtrumController.WriteImeiAsync(newImei);
            if (success)
            {
                AppendLog("[展讯] IMEI 写入成功", Color.Green);
                SafeInvoke(() =>
                {
                    uiLabel10.Text = $"IMEI：{newImei}";
                });
                MessageBox.Show("IMEI 写入成功！\n\n建议重启设备使更改生效。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                AppendLog("[展讯] IMEI 写入失败", Color.Red);
                MessageBox.Show("IMEI 写入失败！\n\n请检查设备连接状态。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region NV 读写

        /// <summary>
        /// 打开 NV 管理器对话框
        /// </summary>
        private Task SprdOpenNvManagerAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return Task.CompletedTask;
            }

            // 显示 NV 操作选择菜单
            var menu = new ContextMenuStrip();
            menu.Items.Add("读取蓝牙地址", null, async (s, e) => await SprdReadNvAsync(SakuraEDL.Spreadtrum.Protocol.SprdNvItems.NV_BT_ADDR, "蓝牙地址"));
            menu.Items.Add("读取WiFi地址", null, async (s, e) => await SprdReadNvAsync(SakuraEDL.Spreadtrum.Protocol.SprdNvItems.NV_WIFI_ADDR, "WiFi地址"));
            menu.Items.Add("读取序列号", null, async (s, e) => await SprdReadNvAsync(SakuraEDL.Spreadtrum.Protocol.SprdNvItems.NV_SERIAL_NUMBER, "序列号"));
            menu.Items.Add("-");
            menu.Items.Add("写入蓝牙地址...", null, async (s, e) => await SprdWriteNvAsync(SakuraEDL.Spreadtrum.Protocol.SprdNvItems.NV_BT_ADDR, "蓝牙地址", 6));
            menu.Items.Add("写入WiFi地址...", null, async (s, e) => await SprdWriteNvAsync(SakuraEDL.Spreadtrum.Protocol.SprdNvItems.NV_WIFI_ADDR, "WiFi地址", 6));
            menu.Items.Add("-");
            menu.Items.Add("读取自定义NV项...", null, async (s, e) => await SprdReadCustomNvAsync());
            menu.Items.Add("写入自定义NV项...", null, async (s, e) => await SprdWriteCustomNvAsync());
            
            // 在按钮位置显示菜单
            menu.Show(Cursor.Position);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 读取指定 NV 项
        /// </summary>
        private async Task SprdReadNvAsync(ushort itemId, string itemName)
        {
            AppendLog($"[展讯] 读取 NV 项: {itemName} (ID={itemId})...", Color.Cyan);

            var data = await _spreadtrumController.ReadNvItemAsync(itemId);
            if (data != null && data.Length > 0)
            {
                string hexStr = BitConverter.ToString(data).Replace("-", ":");
                AppendLog($"[展讯] {itemName}: {hexStr}", Color.Green);
                
                // 复制到剪贴板
                Clipboard.SetText(hexStr);
                AppendLog("[展讯] 已复制到剪贴板", Color.Gray);
                
                MessageBox.Show($"{itemName}:\n\n{hexStr}\n\n已复制到剪贴板", "NV 读取", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                AppendLog($"[展讯] 读取 {itemName} 失败", Color.Red);
            }
        }

        /// <summary>
        /// 写入指定 NV 项 (MAC 地址格式)
        /// </summary>
        private async Task SprdWriteNvAsync(ushort itemId, string itemName, int expectedLength)
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                $"请输入 {itemName}:\n\n格式: XX:XX:XX:XX:XX:XX (6字节十六进制)",
                $"写入 {itemName}",
                "",
                -1, -1);

            if (string.IsNullOrEmpty(input))
                return;

            // 解析 MAC 地址格式
            input = input.Trim().ToUpper().Replace("-", ":").Replace(" ", ":");
            string[] parts = input.Split(':');
            
            if (parts.Length != expectedLength)
            {
                MessageBox.Show($"格式错误！\n\n应为 {expectedLength} 字节", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            byte[] data = new byte[expectedLength];
            try
            {
                for (int i = 0; i < expectedLength; i++)
                {
                    data[i] = Convert.ToByte(parts[i], 16);
                }
            }
            catch
            {
                MessageBox.Show("格式错误！\n\n请使用十六进制格式", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var confirm = MessageBox.Show(
                $"确定要写入 {itemName}?\n\n{input}",
                "确认写入",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
                return;

            AppendLog($"[展讯] 写入 NV 项: {itemName}...", Color.Yellow);

            bool success = await _spreadtrumController.WriteNvItemAsync(itemId, data);
            if (success)
            {
                AppendLog($"[展讯] {itemName} 写入成功", Color.Green);
                MessageBox.Show($"{itemName} 写入成功！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                AppendLog($"[展讯] {itemName} 写入失败", Color.Red);
                MessageBox.Show($"{itemName} 写入失败！", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 读取自定义 NV 项
        /// </summary>
        private async Task SprdReadCustomNvAsync()
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入 NV 项 ID (0-65535):",
                "读取自定义 NV",
                "0",
                -1, -1);

            if (string.IsNullOrEmpty(input))
                return;

            ushort itemId;
            if (!ushort.TryParse(input.Trim(), out itemId))
            {
                // 尝试解析十六进制
                if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        itemId = Convert.ToUInt16(input.Substring(2), 16);
                    }
                    catch
                    {
                        MessageBox.Show("ID 格式错误！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    MessageBox.Show("ID 格式错误！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            AppendLog($"[展讯] 读取 NV 项 ID={itemId}...", Color.Cyan);

            var data = await _spreadtrumController.ReadNvItemAsync(itemId);
            if (data != null && data.Length > 0)
            {
                string hexStr = BitConverter.ToString(data).Replace("-", " ");
                AppendLog($"[展讯] NV[{itemId}]: {hexStr}", Color.Green);
                
                // 尝试解码为字符串
                string asciiStr = "";
                try
                {
                    asciiStr = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0');
                }
                catch { }

                Clipboard.SetText(hexStr);
                
                string msg = $"NV[{itemId}] 长度: {data.Length} 字节\n\n";
                msg += $"HEX: {hexStr}\n\n";
                if (!string.IsNullOrEmpty(asciiStr) && asciiStr.All(c => c >= 0x20 && c < 0x7F))
                    msg += $"ASCII: {asciiStr}\n\n";
                msg += "HEX 已复制到剪贴板";
                
                MessageBox.Show(msg, "NV 读取", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                AppendLog($"[展讯] 读取 NV[{itemId}] 失败", Color.Red);
                MessageBox.Show($"读取 NV[{itemId}] 失败！\n\n该项可能不存在", "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 写入自定义 NV 项
        /// </summary>
        private async Task SprdWriteCustomNvAsync()
        {
            string idInput = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入 NV 项 ID (0-65535):",
                "写入自定义 NV",
                "0",
                -1, -1);

            if (string.IsNullOrEmpty(idInput))
                return;

            ushort itemId;
            if (!ushort.TryParse(idInput.Trim(), out itemId))
            {
                if (idInput.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    try { itemId = Convert.ToUInt16(idInput.Substring(2), 16); }
                    catch { MessageBox.Show("ID 格式错误！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                }
                else
                {
                    MessageBox.Show("ID 格式错误！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            string dataInput = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入数据 (十六进制，空格分隔):\n\n例如: 01 02 03 04 05 06",
                $"写入 NV[{itemId}]",
                "",
                -1, -1);

            if (string.IsNullOrEmpty(dataInput))
                return;

            // 解析十六进制数据
            byte[] data;
            try
            {
                string[] parts = dataInput.Trim().Split(new char[] { ' ', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
                data = parts.Select(p => Convert.ToByte(p, 16)).ToArray();
            }
            catch
            {
                MessageBox.Show("数据格式错误！\n\n请使用十六进制格式，空格分隔", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var confirm = MessageBox.Show(
                $"确定要写入 NV[{itemId}]?\n\n长度: {data.Length} 字节\n数据: {BitConverter.ToString(data).Replace("-", " ")}",
                "确认写入",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
                return;

            AppendLog($"[展讯] 写入 NV[{itemId}]...", Color.Yellow);

            bool success = await _spreadtrumController.WriteNvItemAsync(itemId, data);
            if (success)
            {
                AppendLog($"[展讯] NV[{itemId}] 写入成功", Color.Green);
                MessageBox.Show($"NV[{itemId}] 写入成功！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                AppendLog($"[展讯] NV[{itemId}] 写入失败", Color.Red);
                MessageBox.Show($"NV[{itemId}] 写入失败！", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region Bootloader 解锁

        /// <summary>
        /// 解锁 Bootloader
        /// </summary>
        private async Task SprdUnlockBootloaderAsync()
        {
            if (!_spreadtrumController.IsConnected)
            {
                AppendLog("[展讯] 请先连接设备", Color.Orange);
                return;
            }

            // 获取当前锁定状态
            AppendLog("[展讯] 检查 Bootloader 状态...", Color.Cyan);
            var blStatus = await _spreadtrumController.GetBootloaderStatusAsync();
            
            if (blStatus == null)
            {
                AppendLog("[展讯] 无法获取 Bootloader 状态", Color.Red);
                return;
            }

            if (blStatus.IsUnlocked)
            {
                AppendLog("[展讯] Bootloader 已经解锁", Color.Green);
                MessageBox.Show("Bootloader 已经解锁！\n\n无需重复操作。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 显示警告
            var result = MessageBox.Show(
                "⚠️ 警告：解锁 Bootloader 将导致以下后果：\n\n" +
                "1. 设备所有数据将被清除\n" +
                "2. 设备将失去保修资格\n" +
                "3. 部分支付/银行类应用可能无法使用\n" +
                "4. OTA 更新可能失效\n\n" +
                $"设备型号: {blStatus.DeviceModel}\n" +
                $"安全版本: {blStatus.SecurityVersion}\n" +
                $"Unfused: {(blStatus.IsUnfused ? "是" : "否")}\n\n" +
                "确定要继续解锁吗？",
                "解锁 Bootloader",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
            {
                AppendLog("[展讯] 取消解锁操作", Color.Gray);
                return;
            }

            // 二次确认
            string confirmCode = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入 \"UNLOCK\" 确认解锁:",
                "确认解锁",
                "",
                -1, -1);

            if (confirmCode?.ToUpper() != "UNLOCK")
            {
                AppendLog("[展讯] 确认码错误，取消操作", Color.Orange);
                return;
            }

            AppendLog("[展讯] 开始解锁 Bootloader...", Color.Yellow);

            // 检查是否可以利用漏洞解锁
            if (blStatus.IsUnfused)
            {
                AppendLog("[展讯] 检测到 Unfused 设备，使用签名绕过解锁", Color.Cyan);
                bool success = await _spreadtrumController.UnlockBootloaderAsync(true);
                if (success)
                {
                    AppendLog("[展讯] Bootloader 解锁成功！", Color.Green);
                    MessageBox.Show("Bootloader 解锁成功！\n\n设备将重启到 Fastboot 模式。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    AppendLog("[展讯] 解锁失败", Color.Red);
                    MessageBox.Show("Bootloader 解锁失败！\n\n请检查设备支持情况。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                // 需要厂商解锁码
                string unlockCode = Microsoft.VisualBasic.Interaction.InputBox(
                    "此设备需要厂商解锁码。\n\n请输入解锁码 (16位十六进制):\n\n提示: 可从厂商官网申请",
                    "输入解锁码",
                    "",
                    -1, -1);

                if (string.IsNullOrEmpty(unlockCode))
                {
                    AppendLog("[展讯] 取消解锁操作", Color.Gray);
                    return;
                }

                unlockCode = unlockCode.Trim().ToUpper();
                
                // 验证格式
                if (unlockCode.Length != 16 || !System.Text.RegularExpressions.Regex.IsMatch(unlockCode, "^[0-9A-F]+$"))
                {
                    AppendLog("[展讯] 解锁码格式错误", Color.Red);
                    MessageBox.Show("解锁码格式错误！\n\n应为16位十六进制字符", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                bool success = await _spreadtrumController.UnlockBootloaderWithCodeAsync(unlockCode);
                if (success)
                {
                    AppendLog("[展讯] Bootloader 解锁成功！", Color.Green);
                    MessageBox.Show("Bootloader 解锁成功！\n\n设备将重启。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    AppendLog("[展讯] 解锁失败，解锁码可能不正确", Color.Red);
                    MessageBox.Show("Bootloader 解锁失败！\n\n解锁码可能不正确，或设备不支持解锁。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        #endregion

        /// <summary>
        /// 备份选中的分区 (支持多个)
        /// </summary>
        private async Task SprdBackupSelectedPartitionsAsync()
        {
            if (sprdListPartitions.CheckedItems.Count == 0)
            {
                AppendLog("[展讯] 请勾选要备份的分区", Color.Orange);
                return;
            }

            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择备份保存目录";

                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    int total = sprdListPartitions.CheckedItems.Count;
                    int success = 0;

                    AppendLog($"[展讯] 开始备份 {total} 个分区...", Color.Cyan);

                    foreach (ListViewItem item in sprdListPartitions.CheckedItems)
                    {
                        string partName = item.Text;
                        string outputPath = Path.Combine(fbd.SelectedPath, $"{partName}.img");

                        AppendLog($"[展讯] 备份: {partName}...", Color.White);

                        // 获取分区大小
                        uint size = 0;
                        if (item.Tag is SprdPartitionInfo partInfo)
                        {
                            size = partInfo.Size;
                        }

                        bool result = await _spreadtrumController.ReadPartitionToFileAsync(partName, outputPath, size);
                        if (result)
                        {
                            success++;
                            AppendLog($"[展讯] {partName} 备份成功", Color.Gray);
                        }
                        else
                        {
                            AppendLog($"[展讯] {partName} 备份失败", Color.Orange);
                        }
                    }

                    AppendLog($"[展讯] 备份完成: {success}/{total} 成功", success == total ? Color.Green : Color.Orange);

                    if (success > 0)
                    {
                        // 打开备份目录
                        System.Diagnostics.Process.Start("explorer.exe", fbd.SelectedPath);
                    }
                }
            }
        }
    }
}
