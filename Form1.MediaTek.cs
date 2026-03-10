// ============================================================================
// SakuraEDL - Form1.MediaTek.cs
// MediaTek 平台 UI 集成
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SakuraEDL.MediaTek.Common;
using SakuraEDL.MediaTek.Database;
using SakuraEDL.MediaTek.Models;
using SakuraEDL.MediaTek.Protocol;
using SakuraEDL.MediaTek.Services;
using SakuraEDL.MediaTek.UI;

namespace SakuraEDL
{
    public partial class Form1
    {
        private MediatekUIController _mtkController;
        private MediatekService _mtkService;
        private CancellationTokenSource _mtkCts;
        private bool _mtkIsConnected;
        
        // MTK 日志级别: 0=关闭, 1=基本, 2=详细, 3=调试
        private int _mtkLogLevel = 2;

        #region MTK 日志辅助

        /// <summary>
        /// MTK 日志输出 (带前缀和颜色)
        /// </summary>
        private void MtkLog(string message, Color? color = null, int level = 1)
        {
            if (level > _mtkLogLevel) return;
            AppendLog($"[MTK] {message}", color ?? Color.White);
        }

        /// <summary>
        /// MTK 信息日志
        /// </summary>
        private void MtkLogInfo(string message) => MtkLog(message, Color.Cyan, 1);

        /// <summary>
        /// MTK 成功日志
        /// </summary>
        private void MtkLogSuccess(string message) => MtkLog(message, Color.Green, 1);

        /// <summary>
        /// MTK 警告日志
        /// </summary>
        private void MtkLogWarning(string message) => MtkLog(message, Color.Orange, 1);

        /// <summary>
        /// MTK 错误日志
        /// </summary>
        private void MtkLogError(string message) => MtkLog(message, Color.Red, 1);

        /// <summary>
        /// MTK 详细日志 (需要 level >= 2)
        /// </summary>
        private void MtkLogDetail(string message) => MtkLog(message, Color.Gray, 2);

        /// <summary>
        /// MTK 调试日志 (需要 level >= 3)
        /// </summary>
        private void MtkLogDebug(string message) => MtkLog(message, Color.DarkGray, 3);

        /// <summary>
        /// MTK 协议日志 (十六进制数据)
        /// </summary>
        private void MtkLogHex(string label, byte[] data, int maxLen = 32)
        {
            if (_mtkLogLevel < 3 || data == null) return;
            
            string hex = BitConverter.ToString(data, 0, Math.Min(data.Length, maxLen)).Replace("-", " ");
            if (data.Length > maxLen) hex += $" ... ({data.Length} bytes)";
            MtkLog($"{label}: {hex}", Color.DarkGray, 3);
        }

        #endregion

        #region MTK 初始化

        /// <summary>
        /// 初始化 MediaTek 模块
        /// </summary>
        private void InitializeMediaTekModule()
        {
            try
            {
                // 加载芯片列表
                LoadMtkChipList();
                
                // 加载漏洞类型列表
                LoadMtkExploitTypes();

                // 绑定按钮事件
                // 注意: 连接/断开按钮已隐藏，连接逻辑改为读取分区表时自动执行
                mtkBtnReadGpt.Click += MtkBtnReadGpt_Click;
                mtkInputScatterFile.SuffixClick += MtkInputScatterFile_SuffixClick;
                mtkBtnWritePartition.Click += MtkBtnWritePartition_Click;
                mtkBtnReadPartition.Click += MtkBtnReadPartition_Click;
                mtkBtnErasePartition.Click += MtkBtnErasePartition_Click;
                mtkBtnReboot.Click += MtkBtnReboot_Click;
                mtkBtnReadImei.Click += MtkBtnReadImei_Click;
                mtkBtnWriteImei.Click += MtkBtnWriteImei_Click;
                mtkBtnBackupNvram.Click += MtkBtnBackupNvram_Click;
                mtkBtnRestoreNvram.Click += MtkBtnRestoreNvram_Click;
                mtkBtnFormatData.Click += MtkBtnFormatData_Click;
                mtkBtnUnlockBl.Click += MtkBtnUnlockBl_Click;
                mtkBtnExploit.Click += MtkBtnExploit_Click;
                mtkChkSelectAll.CheckedChanged += MtkChkSelectAll_CheckedChanged;
                mtkInputDaFile.SuffixClick += MtkInputDaFile_SuffixClick;
                mtkSelectChip.SelectedIndexChanged += MtkSelectChip_SelectedIndexChanged;

                // 设置初始状态
                MtkSetConnectionState(false);
                mtkBtnExploit.Enabled = false;

                // 创建 UI 控制器 (用于后台任务)
                _mtkController = new MediatekUIController(
                    (msg, color) => SafeInvoke(() => AppendLog(msg, color)),
                    msg => SafeInvoke(() => AppendLog(msg, Color.Gray))
                );

                // 绑定控制器事件
                _mtkController.OnProgress += MtkController_OnProgress;
                _mtkController.OnStateChanged += MtkController_OnStateChanged;
                _mtkController.OnDeviceConnected += MtkController_OnDeviceConnected;
                _mtkController.OnDeviceDisconnected += MtkController_OnDeviceDisconnected;
                _mtkController.OnPartitionTableLoaded += MtkController_OnPartitionTableLoaded;

                MtkLogInfo("联发科模块已初始化");
            }
            catch (Exception ex)
            {
                MtkLogError($"初始化错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载芯片列表
        /// </summary>
        private void LoadMtkChipList()
        {
            mtkSelectChip.Items.Clear();

            // 从数据库加载芯片列表
            var chips = MtkChipDatabase.GetAllChips()
                .OrderBy(c => c.ChipName)
                .ToList();

            foreach (var chip in chips)
            {
                string displayName = $"{chip.ChipName} (0x{chip.HwCode:X4})";
                if (MtkDaDatabase.SupportsExploit(chip.HwCode))
                {
                    displayName += " [漏洞]";
                }
                mtkSelectChip.Items.Add(new AntdUI.SelectItem(displayName) { Tag = chip.HwCode });
            }

            // 添加自动检测选项
            mtkSelectChip.Items.Insert(0, new AntdUI.SelectItem("自动检测") { Tag = (ushort)0 });
            mtkSelectChip.SelectedIndex = 0;
        }

        /// <summary>
        /// 加载漏洞类型列表
        /// </summary>
        private void LoadMtkExploitTypes()
        {
            mtkSelectExploitType.Items.Clear();

            // 添加漏洞类型选项
            mtkSelectExploitType.Items.Add(new AntdUI.SelectItem("自动") { Tag = "Auto" });
            mtkSelectExploitType.Items.Add(new AntdUI.SelectItem("Carbonara") { Tag = "Carbonara" });
            mtkSelectExploitType.Items.Add(new AntdUI.SelectItem("AllInOne签名") { Tag = "AllinoneSignature" });
            mtkSelectExploitType.Items.Add(new AntdUI.SelectItem("无") { Tag = "None" });

            mtkSelectExploitType.SelectedIndex = 0;
        }

        /// <summary>
        /// 芯片选择改变时更新漏洞类型
        /// </summary>
        private void MtkSelectChip_SelectedIndexChanged(object sender, AntdUI.IntEventArgs e)
        {
            UpdateExploitTypeForSelectedChip();
        }

        /// <summary>
        /// 根据选择的芯片更新漏洞类型
        /// </summary>
        private void UpdateExploitTypeForSelectedChip()
        {
            if (mtkSelectChip.SelectedIndex < 0 || mtkSelectChip.SelectedValue == null)
                return;

            var selectedItem = mtkSelectChip.SelectedValue as AntdUI.SelectItem;
            if (selectedItem?.Tag == null)
                return;

            ushort hwCode = (ushort)selectedItem.Tag;
            if (hwCode == 0)
            {
                // 自动检测模式，使用自动漏洞类型
                mtkSelectExploitType.SelectedIndex = 0;  // Auto
                return;
            }

            // 获取芯片的漏洞类型
            string exploitType = MtkChipDatabase.GetExploitType(hwCode);
            
            // 根据漏洞类型自动选择
            for (int i = 0; i < mtkSelectExploitType.Items.Count; i++)
            {
                var item = mtkSelectExploitType.Items[i] as AntdUI.SelectItem;
                if (item?.Tag?.ToString() == exploitType)
                {
                    mtkSelectExploitType.SelectedIndex = i;
                    break;
                }
            }

            // 更新漏洞按钮状态
            bool hasExploit = MtkChipDatabase.GetChip(hwCode)?.HasExploit ?? false;
            bool isAllinone = MtkChipDatabase.IsAllinoneSignatureSupported(hwCode);
            
            if (isAllinone)
            {
                mtkBtnExploit.Text = "AllInOne漏洞";
                mtkBtnExploit.Enabled = _mtkIsConnected;
                AppendLog($"[MTK] 选择芯片支持 ALLINONE-SIGNATURE 漏洞", Color.Cyan);
            }
            else if (hasExploit)
            {
                mtkBtnExploit.Text = "执行漏洞";
                mtkBtnExploit.Enabled = _mtkIsConnected;
            }
            else
            {
                mtkBtnExploit.Text = "无漏洞";
                mtkBtnExploit.Enabled = false;
            }
        }

        /// <summary>
        /// 清理 MediaTek 模块
        /// </summary>
        private void CleanupMediaTekModule()
        {
            MtkDisconnect();
            _mtkController?.Dispose();
            _mtkController = null;
            
            // 释放 CancellationTokenSource
            _mtkCts?.Dispose();
            _mtkCts = null;
        }
        
        /// <summary>
        /// 安全重置 MTK CancellationTokenSource
        /// </summary>
        private void MtkResetCancellationToken()
        {
            _mtkCts?.Cancel();
            _mtkCts?.Dispose();
            _mtkCts = new CancellationTokenSource();
        }

        #endregion

        #region MTK 按钮事件

        private async void MtkBtnConnect_Click(object sender, EventArgs e)
        {
            if (_mtkIsConnected)
            {
                AppendLog("[MTK] 已经连接", Color.Orange);
                return;
            }

            MtkResetCancellationToken();
            MtkStartTimer();

            try
            {
                MtkSetButtonsEnabled(false);
                MtkUpdateProgress(0, 0, "等待设备...");

                // 创建服务
                _mtkService = new MediatekService();

                // 绑定事件 - 只转发进度，日志由 MtkLog 统一处理
                _mtkService.OnProgress += (current, total) => SafeInvoke(() =>
                {
                    MtkUpdateProgress(current, total);
                });

                _mtkService.OnStateChanged += state => SafeInvoke(() =>
                {
                    MtkUpdateStateDisplay(state);
                });

                // 服务层日志 - 只显示重要信息
                _mtkService.OnLog += (msg, color) => SafeInvoke(() =>
                {
                    // 过滤掉重复的 [MTK] 前缀日志
                    if (_mtkLogLevel >= 2 || color == Color.Red || color == Color.Green)
                        AppendLog(msg, color);
                });

                // 等待设备连接
                MtkLogInfo("等待设备连接...");

                MtkUpdateProgress(0, 0, "请进入 BROM 模式");
                string comPort = await MtkWaitForDeviceAsync(_mtkCts.Token);

                if (string.IsNullOrEmpty(comPort))
                {
                    MtkUpdateProgress(0, 0, "未检测到设备");
                    MtkLogWarning("未检测到设备");
                    return;
                }

                // 连接设备
                MtkLogInfo($"发现设备: {comPort}");
                MtkUpdateProgress(0, 0, "连接中...");
                bool success = await _mtkService.ConnectAsync(comPort, 115200, _mtkCts.Token);

                if (success)
                {
                    _mtkIsConnected = true;
                    MtkSetConnectionState(true);

                    // 更新设备信息
                    if (_mtkService.CurrentDevice != null)
                    {
                        MtkUpdateDeviceInfo(_mtkService.CurrentDevice);
                    }

                    // 设置 DA 文件路径
                    if (_mtkUseSeparateDa)
                    {
                        // 使用分离的 DA1 + DA2 文件
                        if (!string.IsNullOrEmpty(_mtkCustomDa1Path) && File.Exists(_mtkCustomDa1Path))
                        {
                            _mtkService.SetCustomDa1(_mtkCustomDa1Path);
                            AppendLog($"[MTK] 使用自定义 DA1: {Path.GetFileName(_mtkCustomDa1Path)}", Color.Cyan);
                        }
                        
                        if (!string.IsNullOrEmpty(_mtkCustomDa2Path) && File.Exists(_mtkCustomDa2Path))
                        {
                            _mtkService.SetCustomDa2(_mtkCustomDa2Path);
                            AppendLog($"[MTK] 使用自定义 DA2: {Path.GetFileName(_mtkCustomDa2Path)}", Color.Cyan);
                        }
                    }
                    else
                    {
                        // 使用 AllInOne DA 文件
                        string daPath = mtkInputDaFile.Text?.Trim();
                        if (!string.IsNullOrEmpty(daPath) && File.Exists(daPath))
                        {
                            _mtkService.SetDaFilePath(daPath);
                        }
                        
                        // 兼容: 也检查单独设置的 DA2
                        if (!string.IsNullOrEmpty(_mtkCustomDa2Path) && File.Exists(_mtkCustomDa2Path))
                        {
                            _mtkService.SetCustomDa2(_mtkCustomDa2Path);
                        }
                    }

                    // 加载 DA (如果使用漏洞利用)
                    if (mtkChkExploit.Checked)
                    {
                        MtkUpdateProgress(0, 0, "加载 DA...");
                        bool daLoaded = await _mtkService.LoadDaAsync(_mtkCts.Token);
                        if (!daLoaded)
                        {
                            MtkUpdateProgress(0, 0, "DA 加载失败");
                            MtkLogError("DA 加载失败，设备可能需要签名的 DA");
                            return;
                        }
                    }

                    MtkUpdateProgress(100, 100, "已连接");
                    MtkLogSuccess("设备连接成功");

                    // 更新右侧面板
                    UpdateMtkInfoPanel();
                }
                else
                {
                    MtkUpdateProgress(0, 0, "连接失败");
                    MtkLogError("设备连接失败");
                }
            }
            catch (OperationCanceledException)
            {
                MtkUpdateProgress(0, 0, "已取消");
                MtkLogWarning("操作已取消");
            }
            catch (Exception ex)
            {
                MtkUpdateProgress(0, 0, "连接错误");
                MtkLogError($"连接错误: {ex.Message}");
            }
            finally
            {
                MtkSetButtonsEnabled(true);
            }
        }

        /// <summary>
        /// 等待 MTK 设备连接
        /// </summary>
        private async Task<string> MtkWaitForDeviceAsync(CancellationToken ct)
        {
            // 使用端口检测器等待 MTK 设备
            using (var detector = new MtkPortDetector())
            {
                var portInfo = await detector.WaitForDeviceAsync(30000, ct);
                return portInfo?.ComPort;
            }
        }

        private void MtkBtnDisconnect_Click(object sender, EventArgs e)
        {
            MtkDisconnect();
        }

        private void MtkDisconnect()
        {
            _mtkCts?.Cancel();
            _mtkService?.Dispose();
            _mtkService = null;
            _mtkIsConnected = false;

            MtkSetConnectionState(false);
            MtkClearDeviceInfo();
            mtkListPartitions.Items.Clear();

            MtkUpdateProgress(0, 0, "已断开");
            MtkLogDetail("已断开连接");
        }

        private async void MtkBtnReadGpt_Click(object sender, EventArgs e)
        {
            MtkResetCancellationToken();
            MtkStartTimer();

            try
            {
                MtkSetButtonsEnabled(false);

                // 如果未连接，先自动连接设备
                if (!_mtkIsConnected || _mtkService == null)
                {
                    bool connected = await MtkAutoConnectAsync();
                    if (!connected)
                    {
                        return;
                    }
                }

                // 读取分区表
                MtkUpdateProgress(0, 0, "读取分区表...");
                MtkLogInfo("读取分区表...");

                var partitions = await _mtkService.ReadPartitionTableAsync(_mtkCts.Token);

                if (partitions != null && partitions.Count > 0)
                {
                    mtkListPartitions.Items.Clear();

                    foreach (var p in partitions)
                    {
                        var item = new ListViewItem(p.Name);
                        item.SubItems.Add(p.Type);
                        item.SubItems.Add(MtkFormatSize(p.Size));
                        item.SubItems.Add($"0x{p.StartSector * 512:X}");
                        item.SubItems.Add("--");
                        item.Tag = p;
                        mtkListPartitions.Items.Add(item);
                    }

                    string elapsed = MtkGetElapsedTime();
                    MtkUpdateProgress(100, 100, $"{partitions.Count} 个分区 [{elapsed}]");
                    MtkLogSuccess($"读取到 {partitions.Count} 个分区 ({elapsed})");
                }
                else
                {
                    MtkUpdateProgress(0, 0, "无分区数据");
                    MtkLogWarning("未读取到分区信息");
                }
            }
            catch (OperationCanceledException)
            {
                MtkUpdateProgress(0, 0, "已取消");
                MtkLogWarning("操作已取消");
            }
            catch (Exception ex)
            {
                MtkUpdateProgress(0, 0, "读取失败");
                MtkLogError($"读取分区表失败: {ex.Message}");
            }
            finally
            {
                MtkSetButtonsEnabled(true);
            }
        }

        /// <summary>
        /// 自动连接 MTK 设备
        /// </summary>
        private async Task<bool> MtkAutoConnectAsync()
        {
            MtkStartTimer();
            MtkUpdateProgress(0, 0, "等待设备...");
            MtkLogInfo("等待设备 (请进入 BROM 模式)");

            // 创建服务
            _mtkService = new MediatekService();

            // 绑定事件 - 简化日志输出
            _mtkService.OnProgress += (current, total) => SafeInvoke(() =>
            {
                MtkUpdateProgress(current, total);
            });

            _mtkService.OnStateChanged += state => SafeInvoke(() =>
            {
                MtkUpdateStateDisplay(state);
            });

            _mtkService.OnLog += (msg, color) => SafeInvoke(() =>
            {
                // 只显示重要日志
                if (_mtkLogLevel >= 2 || color == Color.Red || color == Color.Green)
                    AppendLog(msg, color);
            });

            // 等待设备连接
            string comPort = await MtkWaitForDeviceAsync(_mtkCts.Token);

            if (string.IsNullOrEmpty(comPort))
            {
                MtkUpdateProgress(0, 0, "超时");
                MtkLogWarning("未检测到设备");
                return false;
            }

            // 连接设备
            MtkLogInfo($"发现: {comPort}");
            MtkUpdateProgress(0, 0, "连接中...");
            bool success = await _mtkService.ConnectAsync(comPort, 115200, _mtkCts.Token);

            if (!success)
            {
                MtkUpdateProgress(0, 0, "连接失败");
                MtkLogError("设备连接失败");
                return false;
            }

            _mtkIsConnected = true;
            MtkSetConnectionState(true);

            // 更新设备信息
            if (_mtkService.CurrentDevice != null)
            {
                MtkUpdateDeviceInfo(_mtkService.CurrentDevice);
            }

            // 设置 DA 文件路径
            MtkApplyDaSettings();

            // 加载 DA (如果需要)
            if (mtkChkExploit.Checked)
            {
                MtkUpdateProgress(0, 0, "加载 DA...");
                bool daLoaded = await _mtkService.LoadDaAsync(_mtkCts.Token);
                if (!daLoaded)
                {
                    AppendLog("[MTK] DA 加载失败，设备可能需要签名的 DA", Color.Red);
                    return false;
                }
            }

            AppendLog("[MTK] 设备连接成功", Color.Green);
            UpdateMtkInfoPanel();
            return true;
        }

        /// <summary>
        /// 应用 DA 设置
        /// </summary>
        private void MtkApplyDaSettings()
        {
            if (_mtkService == null) return;

            if (_mtkUseSeparateDa)
            {
                // 使用分离的 DA1 + DA2 文件
                if (!string.IsNullOrEmpty(_mtkCustomDa1Path) && File.Exists(_mtkCustomDa1Path))
                {
                    _mtkService.SetCustomDa1(_mtkCustomDa1Path);
                    AppendLog($"[MTK] 使用自定义 DA1: {Path.GetFileName(_mtkCustomDa1Path)}", Color.Cyan);
                }
                
                if (!string.IsNullOrEmpty(_mtkCustomDa2Path) && File.Exists(_mtkCustomDa2Path))
                {
                    _mtkService.SetCustomDa2(_mtkCustomDa2Path);
                    AppendLog($"[MTK] 使用自定义 DA2: {Path.GetFileName(_mtkCustomDa2Path)}", Color.Cyan);
                }
            }
            else
            {
                // 使用 AllInOne DA 文件
                string daPath = mtkInputDaFile.Text?.Trim();
                if (!string.IsNullOrEmpty(daPath) && File.Exists(daPath))
                {
                    _mtkService.SetDaFilePath(daPath);
                }
                
                // 兼容: 也检查单独设置的 DA2
                if (!string.IsNullOrEmpty(_mtkCustomDa2Path) && File.Exists(_mtkCustomDa2Path))
                {
                    _mtkService.SetCustomDa2(_mtkCustomDa2Path);
                }
            }
        }

        private async void MtkBtnWritePartition_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;

            var selectedPartitions = MtkGetSelectedPartitions();
            if (selectedPartitions.Length == 0)
            {
                MtkLogWarning("请选择要写入的分区");
                return;
            }

            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "选择包含分区镜像的文件夹";
                if (folderDialog.ShowDialog() != DialogResult.OK)
                    return;

                MtkStartTimer();
                int current = 0;
                int total = selectedPartitions.Length;
                int success = 0;

                try
                {
                    MtkSetButtonsEnabled(false);

                    foreach (var partition in selectedPartitions)
                    {
                        current++;
                        
                        if (mtkChkSkipUserdata.Checked &&
                            (partition.Name.ToLower() == "userdata" || partition.Name.ToLower() == "data"))
                        {
                            MtkLogDetail($"跳过 {partition.Name}");
                            continue;
                        }

                        string filePath = MtkFindPartitionFile(folderDialog.SelectedPath, partition.Name);
                        if (string.IsNullOrEmpty(filePath))
                        {
                            MtkLogWarning($"未找到 {partition.Name} 镜像");
                            continue;
                        }

                        MtkUpdateProgress(current, total, $"写入 {partition.Name} ({current}/{total})");
                        MtkLogInfo($"写入: {partition.Name}");

                        bool result = await _mtkService.WritePartitionAsync(partition.Name, filePath, _mtkCts.Token);
                        if (result) success++;
                    }

                    string elapsed = MtkGetElapsedTime();
                    MtkUpdateProgress(100, 100, $"完成 {success}/{total} [{elapsed}]");
                    MtkLogSuccess($"写入完成 {success}/{total} ({elapsed})");

                    if (mtkChkRebootAfter.Checked)
                    {
                        MtkLogInfo("正在重启设备...");
                        await _mtkService.RebootAsync(_mtkCts.Token);
                    }
                }
                catch (Exception ex)
                {
                    MtkUpdateProgress(0, 0, "写入失败");
                    MtkLogError($"写入失败: {ex.Message}");
                }
                finally
                {
                    MtkSetButtonsEnabled(true);
                }
            }
        }

        private async void MtkBtnReadPartition_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;

            var selectedPartitions = MtkGetSelectedPartitions();
            if (selectedPartitions.Length == 0)
            {
                MtkLogWarning("请选择要读取的分区");
                return;
            }

            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "选择保存位置";
                if (folderDialog.ShowDialog() != DialogResult.OK)
                    return;

                MtkStartTimer();
                int current = 0;
                int total = selectedPartitions.Length;
                int success = 0;

                try
                {
                    MtkSetButtonsEnabled(false);

                    foreach (var partition in selectedPartitions)
                    {
                        current++;
                        MtkUpdateProgress(current, total, $"读取 {partition.Name} ({current}/{total})");
                        MtkLogInfo($"读取: {partition.Name} ({MtkFormatSize(partition.Size)})");

                        string fileName = $"{partition.Name}.img";
                        string outputPath = Path.Combine(folderDialog.SelectedPath, fileName);

                        bool result = await _mtkService.ReadPartitionAsync(
                            partition.Name,
                            outputPath,
                            partition.Size,
                            _mtkCts.Token);

                        if (result)
                        {
                            MtkLogSuccess($"已保存: {fileName}");
                            success++;
                        }
                    }

                    string elapsed = MtkGetElapsedTime();
                    MtkUpdateProgress(100, 100, $"完成 {success}/{total} [{elapsed}]");
                    MtkLogSuccess($"读取完成 {success}/{total} ({elapsed})");
                }
                catch (Exception ex)
                {
                    MtkUpdateProgress(0, 0, "读取失败");
                    MtkLogError($"读取失败: {ex.Message}");
                }
                finally
                {
                    MtkSetButtonsEnabled(true);
                }
            }
        }

        private async void MtkBtnErasePartition_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;

            var selectedPartitions = MtkGetSelectedPartitions();
            if (selectedPartitions.Length == 0)
            {
                MtkLogWarning("请选择要擦除的分区");
                return;
            }

            var result = MessageBox.Show(
                $"确定要擦除选中的 {selectedPartitions.Length} 个分区吗？\n此操作不可恢复！",
                "确认擦除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (result != DialogResult.Yes)
                return;

            MtkStartTimer();
            int current = 0;
            int total = selectedPartitions.Length;

            try
            {
                MtkSetButtonsEnabled(false);

                foreach (var partition in selectedPartitions)
                {
                    current++;
                    MtkUpdateProgress(current, total, $"擦除 {partition.Name} ({current}/{total})");
                    MtkLogInfo($"擦除: {partition.Name}");

                    await _mtkService.ErasePartitionAsync(partition.Name, _mtkCts.Token);
                }

                string elapsed = MtkGetElapsedTime();
                MtkUpdateProgress(100, 100, $"完成 [{elapsed}]");
                MtkLogSuccess($"擦除完成 {total} 个分区 ({elapsed})");
            }
            catch (Exception ex)
            {
                MtkUpdateProgress(0, 0, "擦除失败");
                MtkLogError($"擦除失败: {ex.Message}");
            }
            finally
            {
                MtkSetButtonsEnabled(true);
            }
        }

        private async void MtkBtnReboot_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;

            try
            {
                MtkUpdateProgress(0, 0, "重启设备...");
                await _mtkService.RebootAsync(_mtkCts.Token);
                MtkUpdateProgress(100, 100, "重启命令已发送");
                AppendLog("[MTK] 设备重启中...", Color.Green);

                MtkDisconnect();
            }
            catch (Exception ex)
            {
                AppendLog($"[MTK] 重启失败: {ex.Message}", Color.Red);
            }
        }

        private void MtkBtnReadImei_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;
            AppendLog("[MTK] IMEI 读取功能需要 NVRAM 访问，暂不支持", Color.Orange);
        }

        private void MtkBtnWriteImei_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;
            AppendLog("[MTK] IMEI 写入功能需要 NVRAM 访问，暂不支持", Color.Orange);
        }

        private void MtkBtnBackupNvram_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;
            AppendLog("[MTK] NVRAM 备份功能暂不支持", Color.Orange);
        }

        private void MtkBtnRestoreNvram_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;
            AppendLog("[MTK] NVRAM 恢复功能暂不支持", Color.Orange);
        }

        private async void MtkBtnFormatData_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;

            var result = MessageBox.Show(
                "确定要格式化 Data 分区吗？\n这将清除所有用户数据！",
                "确认格式化",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (result != DialogResult.Yes)
                return;

            try
            {
                MtkSetButtonsEnabled(false);
                MtkUpdateProgress(0, 0, "格式化 Data...");

                await _mtkService.ErasePartitionAsync("userdata", _mtkCts.Token);

                MtkUpdateProgress(100, 100, "格式化完成");
                AppendLog("[MTK] Data 分区已格式化", Color.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"[MTK] 格式化失败: {ex.Message}", Color.Red);
            }
            finally
            {
                MtkSetButtonsEnabled(true);
            }
        }

        private void MtkBtnUnlockBl_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;
            AppendLog("[MTK] Bootloader 解锁功能需要特殊权限，暂不支持", Color.Orange);
        }

        /// <summary>
        /// 执行漏洞利用按钮点击事件
        /// </summary>
        private async void MtkBtnExploit_Click(object sender, EventArgs e)
        {
            if (!MtkEnsureConnected()) return;

            // 获取选择的漏洞类型
            var selectedExploitItem = mtkSelectExploitType.SelectedValue as AntdUI.SelectItem;
            string exploitType = selectedExploitItem?.Tag?.ToString() ?? "Auto";

            // 如果是自动模式，根据芯片判断
            if (exploitType == "Auto")
            {
                ushort hwCode = _mtkService?.ChipInfo?.HwCode ?? 0;
                exploitType = MtkChipDatabase.GetExploitType(hwCode);
                
                if (exploitType == "None" || string.IsNullOrEmpty(exploitType))
                {
                    AppendLog("[MTK] 当前芯片不支持任何已知漏洞", Color.Orange);
                    return;
                }
            }

            try
            {
                MtkSetButtonsEnabled(false);

                if (exploitType == "AllinoneSignature")
                {
                    await ExecuteAllinoneSignatureExploitAsync();
                }
                else if (exploitType == "Carbonara")
                {
                    await ExecuteCarbonaraExploitAsync();
                }
                else
                {
                    AppendLog($"[MTK] 未知漏洞类型: {exploitType}", Color.Red);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[MTK] 漏洞利用异常: {ex.Message}", Color.Red);
            }
            finally
            {
                MtkSetButtonsEnabled(true);
            }
        }

        /// <summary>
        /// 执行 ALLINONE-SIGNATURE 漏洞
        /// 仅适用于 MT6989/MT6983/MT6985 等 Dimensity 9000 系列
        /// </summary>
        private async Task ExecuteAllinoneSignatureExploitAsync()
        {
            ushort hwCode = _mtkService?.ChipInfo?.HwCode ?? 0;
            string chipName = _mtkService?.ChipInfo?.ChipName ?? "Unknown";

            // 检查芯片是否支持
            if (!MtkChipDatabase.IsAllinoneSignatureSupported(hwCode))
            {
                AppendLog($"[MTK] 芯片 {chipName} (0x{hwCode:X4}) 不支持 ALLINONE-SIGNATURE 漏洞", Color.Red);
                AppendLog("[MTK] 此漏洞仅适用于以下芯片:", Color.Yellow);
                
                var supportedChips = MtkChipDatabase.GetAllinoneSignatureChips();
                foreach (var chip in supportedChips)
                {
                    AppendLog($"[MTK]   • {chip.ChipName} - {chip.Description} (0x{chip.HwCode:X4})", Color.Yellow);
                }
                return;
            }

            AppendLog("[MTK] ═══════════════════════════════════════", Color.Yellow);
            AppendLog($"[MTK] 执行 ALLINONE-SIGNATURE 漏洞利用", Color.Yellow);
            AppendLog($"[MTK] 目标芯片: {chipName} (0x{hwCode:X4})", Color.Yellow);
            AppendLog("[MTK] ═══════════════════════════════════════", Color.Yellow);

            MtkUpdateProgress(0, 0, "执行漏洞利用...");

            // 检查 DA2 是否已加载
            if (_mtkService.State != MtkDeviceState.Da2Loaded)
            {
                AppendLog("[MTK] 请先连接设备并加载 DA2", Color.Orange);
                return;
            }

            bool success = await _mtkService.RunAllinoneSignatureExploitAsync(
                null,  // 使用默认 shellcode
                null,  // 使用默认指针表
                _mtkCts.Token);

            if (success)
            {
                MtkUpdateProgress(100, 100, "漏洞利用成功");
                AppendLog("[MTK] ✓ ALLINONE-SIGNATURE 漏洞利用成功!", Color.Green);
                AppendLog("[MTK] 设备安全检查已禁用", Color.Green);
            }
            else
            {
                MtkUpdateProgress(0, 0, "漏洞利用失败");
                AppendLog("[MTK] ✗ ALLINONE-SIGNATURE 漏洞利用失败", Color.Red);
            }
        }

        /// <summary>
        /// 执行 Carbonara 漏洞
        /// 适用于大多数 MT67xx/MT68xx 芯片
        /// </summary>
        private async Task ExecuteCarbonaraExploitAsync()
        {
            ushort hwCode = _mtkService?.ChipInfo?.HwCode ?? 0;
            string chipName = _mtkService?.ChipInfo?.ChipName ?? "Unknown";

            AppendLog("[MTK] ═══════════════════════════════════════", Color.Yellow);
            AppendLog($"[MTK] 执行 Carbonara 漏洞利用", Color.Yellow);
            AppendLog($"[MTK] 目标芯片: {chipName} (0x{hwCode:X4})", Color.Yellow);
            AppendLog("[MTK] ═══════════════════════════════════════", Color.Yellow);

            MtkUpdateProgress(0, 0, "执行漏洞利用...");

            // Carbonara 漏洞在 LoadDaAsync 中自动执行
            // 这里只是显示信息
            AppendLog("[MTK] Carbonara 漏洞会在连接时自动执行", Color.Cyan);
            AppendLog("[MTK] 如果连接成功，表示漏洞已生效", Color.Cyan);

            MtkUpdateProgress(100, 100, "完成");
        }

        private void MtkChkSelectAll_CheckedChanged(object sender, AntdUI.BoolEventArgs e)
        {
            foreach (ListViewItem item in mtkListPartitions.Items)
            {
                item.Checked = e.Value;
            }
        }

        private void MtkInputDaFile_SuffixClick(object sender, EventArgs e)
        {
            using (var openDialog = new OpenFileDialog())
            {
                openDialog.Filter = "DA 文件|*.bin;*.da|所有文件|*.*";
                openDialog.Multiselect = true;  // 支持多选
                openDialog.Title = "选择 DA 文件 (可多选 DA1/DA2)";
                
                if (openDialog.ShowDialog() == DialogResult.OK)
                {
                    // 重置分离 DA 状态
                    _mtkCustomDa1Path = null;
                    _mtkCustomDa2Path = null;
                    _mtkUseSeparateDa = false;
                    
                    if (openDialog.FileNames.Length == 1)
                    {
                        // 单文件选择 - 可能是 AllInOne 或单独的 DA1
                        string fileName = Path.GetFileName(openDialog.FileName).ToLower();
                        
                        // 检查是否为 DA2 (如果用户只选了 DA2)
                        if (fileName.Contains("da2") || fileName.Contains("stage2"))
                        {
                            _mtkCustomDa2Path = openDialog.FileName;
                            mtkInputDaFile.Text = openDialog.FileName;
                            AppendLog($"[MTK] ⚠ 只选择了 DA2，请同时选择 DA1", Color.Orange);
                        }
                        else
                        {
                            mtkInputDaFile.Text = openDialog.FileName;
                            AppendLog($"[MTK] DA 文件: {Path.GetFileName(openDialog.FileName)}", Color.Cyan);
                        }
                    }
                    else
                    {
                        // 多文件选择 - 自动识别 DA1/DA2
                        var (da1Path, da2Path, allInOnePath) = AutoDetectDaFiles(openDialog.FileNames);
                        
                        if (!string.IsNullOrEmpty(allInOnePath))
                        {
                            // AllInOne DA 文件
                            mtkInputDaFile.Text = allInOnePath;
                            _mtkUseSeparateDa = false;
                            AppendLog($"[MTK] 检测到 AllInOne DA: {Path.GetFileName(allInOnePath)}", Color.Cyan);
                        }
                        else if (!string.IsNullOrEmpty(da1Path) && !string.IsNullOrEmpty(da2Path))
                        {
                            // 分离的 DA1 + DA2 文件 (完整)
                            _mtkCustomDa1Path = da1Path;
                            _mtkCustomDa2Path = da2Path;
                            _mtkUseSeparateDa = true;
                            
                            // 显示为 "DA1 + DA2" 格式
                            mtkInputDaFile.Text = $"{Path.GetFileName(da1Path)} + {Path.GetFileName(da2Path)}";
                            
                            AppendLog($"[MTK] 检测到 DA1: {Path.GetFileName(da1Path)}", Color.Cyan);
                            AppendLog($"[MTK] 检测到 DA2: {Path.GetFileName(da2Path)}", Color.Cyan);
                            AppendLog("[MTK] ✓ 已自动识别 DA1 + DA2 (分离格式)", Color.Green);
                        }
                        else if (!string.IsNullOrEmpty(da1Path))
                        {
                            // 只有 DA1
                            mtkInputDaFile.Text = da1Path;
                            AppendLog($"[MTK] 检测到 DA1: {Path.GetFileName(da1Path)}", Color.Cyan);
                            AppendLog("[MTK] ⚠ 未检测到 DA2，部分功能可能受限", Color.Orange);
                        }
                        else if (!string.IsNullOrEmpty(da2Path))
                        {
                            // 只有 DA2
                            _mtkCustomDa2Path = da2Path;
                            mtkInputDaFile.Text = da2Path;
                            AppendLog($"[MTK] 检测到 DA2: {Path.GetFileName(da2Path)}", Color.Cyan);
                            AppendLog("[MTK] ⚠ 未检测到 DA1，请同时选择 DA1", Color.Orange);
                        }
                    }
                }
            }
        }
        
        // 存储自动识别的 DA1/DA2 路径 (分离格式)
        private string _mtkCustomDa1Path;
        private string _mtkCustomDa2Path;
        private bool _mtkUseSeparateDa;  // 是否使用分离的 DA1/DA2
        private string _mtkScatterPath;  // Scatter 配置文件路径
        private List<MtkScatterEntry> _mtkScatterEntries;  // Scatter 分区配置
        
        /// <summary>
        /// 自动检测 DA 文件类型
        /// </summary>
        private (string da1Path, string da2Path, string allInOnePath) AutoDetectDaFiles(string[] filePaths)
        {
            string da1Path = null;
            string da2Path = null;
            string allInOnePath = null;
            
            foreach (var path in filePaths)
            {
                string fileName = Path.GetFileName(path).ToLower();
                
                // 检测 AllInOne DA
                if (fileName.Contains("allinone") || fileName.Contains("all_in_one") || 
                    fileName.Contains("all-in-one") || fileName == "mtk_da.bin")
                {
                    allInOnePath = path;
                    continue;
                }
                
                // 检测 DA1 (Stage1)
                if (fileName.Contains("da1") || fileName.Contains("stage1") || 
                    fileName.Contains("_1.") || fileName.Contains("-1.") ||
                    fileName.EndsWith("_da1.bin") || fileName.EndsWith("-da1.bin"))
                {
                    da1Path = path;
                    continue;
                }
                
                // 检测 DA2 (Stage2)
                if (fileName.Contains("da2") || fileName.Contains("stage2") || 
                    fileName.Contains("_2.") || fileName.Contains("-2.") ||
                    fileName.EndsWith("_da2.bin") || fileName.EndsWith("-da2.bin"))
                {
                    da2Path = path;
                    continue;
                }
                
                // 如果文件名不明确，检查文件大小
                try
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length > 500000 && fileInfo.Length < 700000)
                    {
                        // ~600KB 通常是 DA1
                        if (da1Path == null) da1Path = path;
                    }
                    else if (fileInfo.Length > 300000 && fileInfo.Length < 400000)
                    {
                        // ~350KB 通常是 DA2
                        if (da2Path == null) da2Path = path;
                    }
                    else if (fileInfo.Length > 1000000)
                    {
                        // > 1MB 可能是 AllInOne
                        if (allInOnePath == null) allInOnePath = path;
                    }
                }
                catch { }
            }
            
            return (da1Path, da2Path, allInOnePath);
        }

        #endregion

        #region MTK 控制器事件

        private void MtkController_OnProgress(int current, int total)
        {
            SafeInvoke(() =>
            {
                MtkUpdateProgress(current, total);
            });
        }

        private void MtkController_OnStateChanged(MtkDeviceState state)
        {
            SafeInvoke(() =>
            {
                MtkUpdateStateDisplay(state);
            });
        }

        private void MtkController_OnDeviceConnected(MtkDeviceInfo device)
        {
            SafeInvoke(() =>
            {
                // 简洁的设备信息日志
                string chipName = device.ChipInfo?.GetChipName() ?? "Unknown";
                string hwCode = device.ChipInfo != null ? $"0x{device.ChipInfo.HwCode:X4}" : "--";
                string protocol = _mtkService?.IsXFlashMode == true ? "XFlash" : "XML";
                MtkLogSuccess($"设备连接: {chipName} ({hwCode}) [{protocol}]");

                // 更新右侧信息面板
                UpdateMtkInfoPanelFromDevice(device);
            });
        }

        private void MtkController_OnDeviceDisconnected(MtkDeviceInfo device)
        {
            SafeInvoke(() =>
            {
                MtkLogWarning("设备已断开");
                UpdateMtkInfoPanel();
            });
        }

        private void MtkController_OnPartitionTableLoaded(List<MtkPartitionInfo> partitions)
        {
            SafeInvoke(() =>
            {
                MtkLogInfo($"加载 {partitions.Count} 个分区");
            });
        }

        #endregion

        #region MTK 右侧信息面板

        /// <summary>
        /// 更新右侧信息面板为 MTK 专用显示
        /// </summary>
        private void UpdateMtkInfoPanel()
        {
            if (_mtkIsConnected && _mtkService != null)
            {
                var deviceInfo = _mtkService.CurrentDevice;
                if (deviceInfo?.ChipInfo != null)
                {
                    string protocol = _mtkService.IsXFlashMode ? "XFlash" : "XML";
                    uiLabel9.Text = $"平台：MediaTek";
                    uiLabel11.Text = $"芯片：{deviceInfo.ChipInfo.GetChipName()}";
                    uiLabel12.Text = $"HW Code：0x{deviceInfo.ChipInfo.HwCode:X4}";
                    uiLabel10.Text = $"协议：{protocol}";
                    uiLabel3.Text = $"ME ID：{(deviceInfo.MeIdHex?.Length > 16 ? deviceInfo.MeIdHex.Substring(0, 16) + "..." : deviceInfo.MeIdHex ?? "--")}";
                    uiLabel13.Text = $"状态：已连接";
                    uiLabel14.Text = $"漏洞：{(MtkDaDatabase.SupportsExploit(deviceInfo.ChipInfo.HwCode) ? "可用" : "不可用")}";
                    return;
                }
            }

            // 未连接时显示等待状态
            uiLabel9.Text = "平台：MediaTek";
            uiLabel11.Text = "芯片：等待连接";
            uiLabel12.Text = "HW Code：--";
            uiLabel10.Text = "协议：--";
            uiLabel3.Text = "ME ID：--";
            uiLabel13.Text = "状态：未连接";
            uiLabel14.Text = "漏洞：--";
        }

        /// <summary>
        /// MTK 设备连接后更新右侧面板
        /// </summary>
        private void UpdateMtkInfoPanelFromDevice(MtkDeviceInfo deviceInfo)
        {
            SafeInvoke(() =>
            {
                if (deviceInfo?.ChipInfo != null)
                {
                    string protocol = _mtkService?.IsXFlashMode == true ? "XFlash" : "XML";
                    uiLabel9.Text = $"平台：MediaTek";
                    uiLabel11.Text = $"芯片：{deviceInfo.ChipInfo.GetChipName()}";
                    uiLabel12.Text = $"HW Code：0x{deviceInfo.ChipInfo.HwCode:X4}";
                    uiLabel10.Text = $"协议：{protocol}";
                    uiLabel3.Text = $"ME ID：{(deviceInfo.MeIdHex?.Length > 16 ? deviceInfo.MeIdHex.Substring(0, 16) + "..." : deviceInfo.MeIdHex ?? "--")}";
                    uiLabel13.Text = $"状态：已连接";
                    uiLabel14.Text = $"漏洞：{(MtkDaDatabase.SupportsExploit(deviceInfo.ChipInfo.HwCode) ? "可用" : "不可用")}";
                }
            });
        }

        #endregion

        #region MTK 辅助方法

        private bool MtkEnsureConnected()
        {
            if (!_mtkIsConnected || _mtkService == null)
            {
                AppendLog("[MTK] 请先连接设备", Color.Orange);
                return false;
            }
            return true;
        }

        private void MtkSetConnectionState(bool connected)
        {
            _mtkIsConnected = connected;
            // 连接/断开按钮已隐藏，不再控制它们的状态
            // mtkBtnConnect.Enabled = !connected;
            // mtkBtnDisconnect.Enabled = connected;
            
            // 读取分区表始终可用，会自动执行连接
            mtkBtnReadGpt.Enabled = true;
            mtkBtnWritePartition.Enabled = connected;
            mtkBtnReadPartition.Enabled = connected;
            mtkBtnErasePartition.Enabled = connected;
            mtkBtnReboot.Enabled = connected;
            mtkBtnReadImei.Enabled = connected;
            mtkBtnWriteImei.Enabled = connected;
            mtkBtnBackupNvram.Enabled = connected;
            mtkBtnRestoreNvram.Enabled = connected;
            mtkBtnFormatData.Enabled = connected;
            mtkBtnUnlockBl.Enabled = connected;
            
            // 更新漏洞按钮状态
            if (connected && _mtkService?.ChipInfo != null)
            {
                ushort hwCode = _mtkService.ChipInfo.HwCode;
                bool hasExploit = MtkChipDatabase.GetChip(hwCode)?.HasExploit ?? false;
                bool isAllinone = MtkChipDatabase.IsAllinoneSignatureSupported(hwCode);
                
                mtkBtnExploit.Enabled = hasExploit;
                
                if (isAllinone)
                {
                    mtkBtnExploit.Text = "AllInOne漏洞";
                }
                else if (hasExploit)
                {
                    mtkBtnExploit.Text = "执行漏洞";
                }
                else
                {
                    mtkBtnExploit.Text = "无漏洞";
                }
            }
            else
            {
                mtkBtnExploit.Enabled = false;
            }
        }

        private void MtkSetButtonsEnabled(bool enabled)
        {
            // 读取分区表按钮始终根据 enabled 状态控制（支持自动连接）
            mtkBtnReadGpt.Enabled = enabled;
            
            if (_mtkIsConnected)
            {
                mtkBtnWritePartition.Enabled = enabled;
                mtkBtnReadPartition.Enabled = enabled;
                mtkBtnErasePartition.Enabled = enabled;
                mtkBtnReboot.Enabled = enabled;
                mtkBtnReadImei.Enabled = enabled;
                mtkBtnWriteImei.Enabled = enabled;
                mtkBtnBackupNvram.Enabled = enabled;
                mtkBtnRestoreNvram.Enabled = enabled;
                mtkBtnFormatData.Enabled = enabled;
                mtkBtnUnlockBl.Enabled = enabled;
            }
            // 连接/断开按钮已隐藏，不再控制它们的状态
        }

        private void MtkUpdateDeviceInfo(MtkDeviceInfo info)
        {
            if (info.ChipInfo != null)
            {
                mtkLblHwCode.Text = $"HW: 0x{info.ChipInfo.HwCode:X4}";
                mtkLblChipName.Text = $"芯片: {info.ChipInfo.GetChipName()}";
                mtkLblDaMode.Text = $"模式: {info.ChipInfo.DaMode}";
            }
        }

        private void MtkUpdateStateDisplay(MtkDeviceState state)
        {
            string stateText = state switch
            {
                MtkDeviceState.Disconnected => "未连接",
                MtkDeviceState.Handshaking => "握手中...",
                MtkDeviceState.Brom => "BROM 模式",
                MtkDeviceState.Preloader => "Preloader 模式",
                MtkDeviceState.Da1Loaded => "DA1 已加载",
                MtkDeviceState.Da2Loaded => "DA2 已加载",
                MtkDeviceState.Error => "错误",
                _ => "未知"
            };

            mtkLblStatus.Text = $"状态: {stateText}";

            Color stateColor = state switch
            {
                MtkDeviceState.Da2Loaded => Color.Green,
                MtkDeviceState.Da1Loaded => Color.Cyan,
                MtkDeviceState.Brom => Color.Orange,
                MtkDeviceState.Preloader => Color.Orange,
                MtkDeviceState.Error => Color.Red,
                _ => Color.Gray
            };

            mtkLblStatus.ForeColor = stateColor;
        }

        private void MtkClearDeviceInfo()
        {
            mtkLblStatus.Text = "状态: 未连接";
            mtkLblStatus.ForeColor = Color.Gray;
            mtkLblHwCode.Text = "HW: --";
            mtkLblChipName.Text = "芯片: --";
            mtkLblDaMode.Text = "模式: --";
        }

        private MtkPartitionInfo[] MtkGetSelectedPartitions()
        {
            return mtkListPartitions.CheckedItems
                .Cast<ListViewItem>()
                .Where(item => item.Tag is MtkPartitionInfo)
                .Select(item => (MtkPartitionInfo)item.Tag)
                .ToArray();
        }

        private string MtkFindPartitionFile(string folder, string partitionName)
        {
            string[] extensions = { ".img", ".bin", ".dat", "" };
            foreach (var ext in extensions)
            {
                string path = Path.Combine(folder, partitionName + ext);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private string MtkFormatSize(ulong bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }

        // MTK 操作计时
        private DateTime _mtkOperationStartTime;
        private long _mtkLastBytes;
        private DateTime _mtkLastSpeedUpdate;
        
        /// <summary>
        /// 开始 MTK 操作计时
        /// </summary>
        private void MtkStartTimer()
        {
            _mtkOperationStartTime = DateTime.Now;
            _mtkLastBytes = 0;
            _mtkLastSpeedUpdate = DateTime.Now;
        }
        
        /// <summary>
        /// 获取操作已用时间
        /// </summary>
        private string MtkGetElapsedTime()
        {
            var elapsed = DateTime.Now - _mtkOperationStartTime;
            if (elapsed.TotalHours >= 1)
                return $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            return $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }
        
        /// <summary>
        /// 计算传输速度
        /// </summary>
        private string MtkCalculateSpeed(long currentBytes)
        {
            var now = DateTime.Now;
            var timeDiff = (now - _mtkLastSpeedUpdate).TotalSeconds;
            
            if (timeDiff < 0.5) return "";  // 至少 0.5 秒更新一次
            
            var bytesDiff = currentBytes - _mtkLastBytes;
            var speed = bytesDiff / timeDiff;
            
            _mtkLastBytes = currentBytes;
            _mtkLastSpeedUpdate = now;
            
            if (speed < 1024) return $"{speed:F0} B/s";
            if (speed < 1024 * 1024) return $"{speed / 1024:F1} KB/s";
            return $"{speed / 1024 / 1024:F1} MB/s";
        }
        
        /// <summary>
        /// 更新进度条 - 使用现有的双进度条
        /// </summary>
        private void MtkUpdateProgress(int current, int total, string statusText = null)
        {
            SafeInvoke(() =>
            {
                string timeInfo = "";
                string speedInfo = "";
                
                if (total > 0)
                {
                    int percentage = (int)((double)current / total * 100);
                    uiProcessBar1.Value = Math.Min(percentage, 100);
                    uiProcessBar2.Value = Math.Min(percentage, 100);
                    
                    // 更新圆形进度条
                    progress1.Value = (float)percentage / 100f;
                    progress2.Value = (float)percentage / 100f;
                    
                    // 计算时间和速度
                    timeInfo = MtkGetElapsedTime();
                    if (current > 0 && current < total)
                    {
                        // 估算剩余时间
                        var elapsed = DateTime.Now - _mtkOperationStartTime;
                        var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds / current * (total - current));
                        if (remaining.TotalHours >= 1)
                            timeInfo += $" / 剩余 {remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                        else if (remaining.TotalSeconds > 5)
                            timeInfo += $" / 剩余 {remaining.Minutes:D2}:{remaining.Seconds:D2}";
                        
                        // 计算速度 (假设 current 是字节数)
                        if (total > 1000)  // 大于 1KB 才计算速度
                        {
                            speedInfo = MtkCalculateSpeed(current);
                        }
                    }
                }
                else
                {
                    uiProcessBar1.Value = 0;
                    uiProcessBar2.Value = 0;
                    progress1.Value = 0;
                    progress2.Value = 0;
                }

                // 更新状态显示
                if (!string.IsNullOrEmpty(statusText))
                {
                    string fullStatus = statusText;
                    if (!string.IsNullOrEmpty(timeInfo))
                        fullStatus += $" [{timeInfo}]";
                    if (!string.IsNullOrEmpty(speedInfo))
                        fullStatus += $" {speedInfo}";
                    
                    mtkLblStatus.Text = $"状态: {fullStatus}";
                }
            });
        }
        
        /// <summary>
        /// 更新进度条 (带字节数，用于计算速度)
        /// </summary>
        private void MtkUpdateProgressWithBytes(long currentBytes, long totalBytes, string operation)
        {
            SafeInvoke(() =>
            {
                int percentage = totalBytes > 0 ? (int)((double)currentBytes / totalBytes * 100) : 0;
                uiProcessBar1.Value = Math.Min(percentage, 100);
                uiProcessBar2.Value = Math.Min(percentage, 100);
                progress1.Value = (float)percentage / 100f;
                progress2.Value = (float)percentage / 100f;
                
                // 计算速度和时间
                string timeInfo = MtkGetElapsedTime();
                string speedInfo = MtkCalculateSpeed(currentBytes);
                string sizeInfo = $"{MtkFormatSize((ulong)currentBytes)}/{MtkFormatSize((ulong)totalBytes)}";
                
                // 估算剩余时间
                if (currentBytes > 0 && currentBytes < totalBytes)
                {
                    var elapsed = DateTime.Now - _mtkOperationStartTime;
                    var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds / currentBytes * (totalBytes - currentBytes));
                    if (remaining.TotalSeconds > 5)
                    {
                        if (remaining.TotalHours >= 1)
                            timeInfo += $" 剩余{remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                        else
                            timeInfo += $" 剩余{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    }
                }
                
                mtkLblStatus.Text = $"{operation} {sizeInfo} [{timeInfo}] {speedInfo}";
            });
        }

        /// <summary>
        /// 检查是否有 MTK 操作正在进行 (包括等待设备阶段)
        /// </summary>
        public bool MtkHasPendingOperation => _mtkCts != null && !_mtkCts.IsCancellationRequested;

        /// <summary>
        /// 取消当前 MTK 操作
        /// </summary>
        public void MtkCancelOperation()
        {
            if (_mtkCts != null && !_mtkCts.IsCancellationRequested)
            {
                _mtkCts.Cancel();
                MtkLogWarning("操作已取消");
                MtkUpdateProgress(0, 0, "已取消");
            }
        }

        #endregion

        #region MTK Scatter 配置文件解析

        /// <summary>
        /// MTK Scatter 分区条目
        /// </summary>
        public class MtkScatterEntry
        {
            public string Name { get; set; }
            public string FileName { get; set; }
            public long StartAddr { get; set; }
            public long Length { get; set; }
            public string Type { get; set; }
            public bool IsDownload { get; set; }
            public string Operation { get; set; }
        }

        /// <summary>
        /// Scatter 文件选择按钮点击事件
        /// </summary>
        private void MtkInputScatterFile_SuffixClick(object sender, EventArgs e)
        {
            using (var openDialog = new OpenFileDialog())
            {
                openDialog.Filter = "Scatter 文件|*scatter*.txt;*scatter*|所有文件|*.*";
                openDialog.Title = "选择 MTK Scatter 配置文件";

                if (openDialog.ShowDialog() == DialogResult.OK)
                {
                    _mtkScatterPath = openDialog.FileName;
                    mtkInputScatterFile.Text = openDialog.FileName;
                    LoadScatterPartitions(_mtkScatterPath);
                }
            }
        }

        /// <summary>
        /// 从 Scatter 文件加载分区配置
        /// </summary>
        private void LoadScatterPartitions(string scatterPath)
        {
            try
            {
                _mtkScatterEntries = ParseScatterFile(scatterPath);

                if (_mtkScatterEntries != null && _mtkScatterEntries.Count > 0)
                {
                    AppendLog($"[MTK] 已加载 Scatter 配置: {_mtkScatterEntries.Count} 个分区", Color.Green);

                    // 更新分区列表显示
                    mtkListPartitions.Items.Clear();
                    foreach (var entry in _mtkScatterEntries)
                    {
                        var item = new ListViewItem(entry.Name);
                        item.SubItems.Add(entry.Type ?? "--");
                        item.SubItems.Add(MtkFormatSize((ulong)entry.Length));
                        item.SubItems.Add($"0x{entry.StartAddr:X}");
                        item.SubItems.Add(entry.FileName ?? "--");
                        item.Tag = entry;
                        item.Checked = entry.IsDownload;
                        mtkListPartitions.Items.Add(item);
                    }

                    // 显示配置文件路径
                    AppendLog($"[MTK] Scatter 文件: {Path.GetFileName(scatterPath)}", Color.Cyan);
                }
                else
                {
                    AppendLog("[MTK] Scatter 文件解析失败或为空", Color.Orange);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[MTK] 解析 Scatter 文件失败: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// 解析 MTK Scatter 文件
        /// 支持多种格式: 老式格式和新式 YAML 格式
        /// </summary>
        private List<MtkScatterEntry> ParseScatterFile(string filePath)
        {
            var entries = new List<MtkScatterEntry>();

            try
            {
                string content = File.ReadAllText(filePath);
                string[] lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                MtkScatterEntry currentEntry = null;

                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;

                    // 检测新条目开始 (partition_name: 或 - partition_name:)
                    if (line.StartsWith("- partition_name:") || line.StartsWith("partition_name:"))
                    {
                        // 保存前一个条目
                        if (currentEntry != null && !string.IsNullOrEmpty(currentEntry.Name))
                        {
                            entries.Add(currentEntry);
                        }

                        currentEntry = new MtkScatterEntry();
                        string value = ExtractScatterValue(line, "partition_name");
                        currentEntry.Name = value;
                        continue;
                    }

                    if (currentEntry == null)
                        continue;

                    // 解析各个字段
                    if (line.StartsWith("file_name:"))
                    {
                        currentEntry.FileName = ExtractScatterValue(line, "file_name");
                    }
                    else if (line.StartsWith("linear_start_addr:"))
                    {
                        string addr = ExtractScatterValue(line, "linear_start_addr");
                        currentEntry.StartAddr = ParseHexOrDecimal(addr);
                    }
                    else if (line.StartsWith("physical_start_addr:"))
                    {
                        // 优先使用 physical_start_addr
                        string addr = ExtractScatterValue(line, "physical_start_addr");
                        currentEntry.StartAddr = ParseHexOrDecimal(addr);
                    }
                    else if (line.StartsWith("partition_size:"))
                    {
                        string size = ExtractScatterValue(line, "partition_size");
                        currentEntry.Length = ParseHexOrDecimal(size);
                    }
                    else if (line.StartsWith("type:"))
                    {
                        currentEntry.Type = ExtractScatterValue(line, "type");
                    }
                    else if (line.StartsWith("is_download:"))
                    {
                        string val = ExtractScatterValue(line, "is_download").ToLower();
                        currentEntry.IsDownload = val == "true" || val == "yes" || val == "1";
                    }
                    else if (line.StartsWith("operation_type:"))
                    {
                        currentEntry.Operation = ExtractScatterValue(line, "operation_type");
                    }
                }

                // 添加最后一个条目
                if (currentEntry != null && !string.IsNullOrEmpty(currentEntry.Name))
                {
                    entries.Add(currentEntry);
                }

                AppendLog($"[MTK] 解析 Scatter 成功: {entries.Count} 个分区", Color.Gray);
            }
            catch (Exception ex)
            {
                AppendLog($"[MTK] 解析 Scatter 异常: {ex.Message}", Color.Red);
            }

            return entries;
        }

        /// <summary>
        /// 从 Scatter 行提取值
        /// </summary>
        private string ExtractScatterValue(string line, string key)
        {
            int colonIndex = line.IndexOf(':');
            if (colonIndex < 0)
                return "";

            string value = line.Substring(colonIndex + 1).Trim();

            // 移除可能的注释
            int commentIndex = value.IndexOf('#');
            if (commentIndex >= 0)
                value = value.Substring(0, commentIndex).Trim();

            // 移除引号
            value = value.Trim('"', '\'', ' ');

            return value;
        }

        /// <summary>
        /// 解析十六进制或十进制数值
        /// </summary>
        private long ParseHexOrDecimal(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            value = value.Trim();

            try
            {
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.ToInt64(value.Substring(2), 16);
                }
                return Convert.ToInt64(value);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 根据 Scatter 配置刷写分区
        /// </summary>
        private async Task MtkFlashFromScatterAsync(string imageFolder)
        {
            if (_mtkScatterEntries == null || _mtkScatterEntries.Count == 0)
            {
                AppendLog("[MTK] 未加载 Scatter 配置", Color.Orange);
                return;
            }

            var downloadEntries = _mtkScatterEntries.Where(e => e.IsDownload).ToList();
            if (downloadEntries.Count == 0)
            {
                AppendLog("[MTK] Scatter 中没有标记为下载的分区", Color.Orange);
                return;
            }

            int total = downloadEntries.Count;
            int current = 0;
            int success = 0;

            foreach (var entry in downloadEntries)
            {
                current++;
                MtkUpdateProgress(current, total, $"刷写 {entry.Name}...");

                // 查找镜像文件
                string imagePath = null;
                if (!string.IsNullOrEmpty(entry.FileName))
                {
                    imagePath = Path.Combine(imageFolder, entry.FileName);
                    if (!File.Exists(imagePath))
                    {
                        // 尝试其他常见扩展名
                        imagePath = MtkFindPartitionFile(imageFolder, entry.Name);
                    }
                }
                else
                {
                    imagePath = MtkFindPartitionFile(imageFolder, entry.Name);
                }

                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                {
                    AppendLog($"[MTK] 跳过 {entry.Name}: 未找到镜像文件", Color.Gray);
                    continue;
                }

                try
                {
                    AppendLog($"[MTK] 刷写 {entry.Name} <- {Path.GetFileName(imagePath)}", Color.Cyan);
                    await _mtkService.WritePartitionAsync(entry.Name, imagePath, _mtkCts.Token);
                    success++;
                }
                catch (Exception ex)
                {
                    AppendLog($"[MTK] 刷写 {entry.Name} 失败: {ex.Message}", Color.Red);
                }
            }

            MtkUpdateProgress(100, 100, $"完成 {success}/{total}");
            AppendLog($"[MTK] Scatter 刷写完成: {success}/{total} 成功", success == total ? Color.Green : Color.Orange);
        }

        #endregion
    }
}
