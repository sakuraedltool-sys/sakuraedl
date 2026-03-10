// ============================================================================
// SakuraEDL - Form1.MediaTek.UI.cs
// MediaTek 平台新 UI 功能对接
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SakuraEDL.MediaTek.Auth;
using SakuraEDL.MediaTek.Common;
using SakuraEDL.MediaTek.Database;
using SakuraEDL.MediaTek.Models;
using SakuraEDL.Views.Controls;

namespace SakuraEDL.Views
{
    public partial class Form1
    {
        // MTK 引导模式
        private enum MtkBootModeType { Auto, Cloud, Local }
        private MtkBootModeType _mtkCurrentBootMode = MtkBootModeType.Auto;
        
        // MTK 文件路径
        private string _mtkDaFilePath;
        private string _mtkScatterFilePath;
        private string _mtkAuthFilePath;
        
        // MTK 选项 (替代缺失的 UI 控件)
        #pragma warning disable CS0414
        private bool _mtkUseExploit = true;
        private bool _mtkSkipUserdata = false;
        private bool _mtkRebootAfter = false;
        #pragma warning restore CS0414

        #region MTK 引导模式切换

        /// <summary>
        /// 引导模式选择变更
        /// </summary>
        private void MtkSelectBootMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (mtkSelectBootMode.SelectedIndex)
            {
                case 0: _mtkCurrentBootMode = MtkBootModeType.Auto; break;
                case 1: _mtkCurrentBootMode = MtkBootModeType.Cloud; break;
                case 2: _mtkCurrentBootMode = MtkBootModeType.Local; break;
            }
            MtkUpdateBootModeUI();
            MtkLogInfo($"切换引导模式: {mtkSelectBootMode.Text}");
        }

        /// <summary>
        /// 更新引导模式 UI 状态
        /// </summary>
        private void MtkUpdateBootModeUI()
        {
            bool isLocal = _mtkCurrentBootMode == MtkBootModeType.Local;
            bool isCloud = _mtkCurrentBootMode == MtkBootModeType.Cloud;
            
            // 本地模式：启用所有选择控件
            // 云端模式：禁用 DA/Auth/配置文件选择 (云端自动提供)
            mtkInputDA.Enabled = isLocal;
            mtkInputScatter.Enabled = isLocal;
            mtkInputAuth.Enabled = isLocal;
            mtkSelectAuthMethod.Enabled = isLocal;
            
            if (isCloud)
            {
                mtkInputDA.PlaceholderText = "云端自动提供";
                mtkInputScatter.PlaceholderText = "云端自动提供";
                mtkInputAuth.PlaceholderText = "云端自动提供";
            }
            else if (isLocal)
            {
                mtkInputDA.PlaceholderText = "双击选择DA";
                mtkInputScatter.PlaceholderText = "双击选择配置文件";
                mtkInputAuth.PlaceholderText = "双击选择auth文件";
            }
            else
            {
                mtkInputDA.PlaceholderText = "等待设备连接...";
                mtkInputScatter.PlaceholderText = "等待设备连接...";
                mtkInputAuth.PlaceholderText = "等待设备连接...";
            }
        }

        /// <summary>
        /// 自动存储检测复选框变更
        /// </summary>
        private void MtkChkAutoStorage_CheckedChanged(object sender, EventArgs e)
        {
            bool autoDetect = mtkChkAutoStorage.IsChecked == true;
            mtkRadioUFS.Enabled = !autoDetect;
            mtkRadioEMMC.Enabled = !autoDetect;
            if (autoDetect)
            {
                mtkRadioUFS.IsChecked = false;
                mtkRadioEMMC.IsChecked = false;
            }
        }

        /// <summary>
        /// 双击选择 DA 文件
        /// </summary>
        private void MtkInputDA_DoubleClick(object sender, EventArgs e)
        {
            if (!mtkInputDA.Enabled) return;
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = "选择 Download Agent (DA) 文件";
                ofd.Filter = "DA文件|*.bin;*.da|所有文件|*.*";
                ofd.InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MediaTek", "DA");
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _mtkDaFilePath = ofd.FileName;
                    mtkInputDA.Text = Path.GetFileName(ofd.FileName);
                    MtkLogInfo($"已选择DA: {Path.GetFileName(ofd.FileName)}");
                }
            }
        }

        /// <summary>
        /// 双击选择配置文件 (scatter.txt)
        /// </summary>
        private void MtkInputScatter_DoubleClick(object sender, EventArgs e)
        {
            if (!mtkInputScatter.Enabled) return;
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = "选择配置文件 (scatter.txt)";
                ofd.Filter = "Scatter文件|*scatter*.txt;*.xml|所有文件|*.*";
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _mtkScatterFilePath = ofd.FileName;
                    mtkInputScatter.Text = Path.GetFileName(ofd.FileName);
                    MtkLogInfo($"已选择配置: {Path.GetFileName(ofd.FileName)}");
                    MtkParseScatterFileNew(ofd.FileName);
                }
            }
        }

        /// <summary>
        /// 双击选择 Auth 文件
        /// </summary>
        private void MtkInputAuth_DoubleClick(object sender, EventArgs e)
        {
            if (!mtkInputAuth.Enabled) return;
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = "选择认证文件 (Auth)";
                ofd.Filter = "Auth文件|*.auth;*.bin|所有文件|*.*";
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _mtkAuthFilePath = ofd.FileName;
                    mtkInputAuth.Text = Path.GetFileName(ofd.FileName);
                    MtkLogInfo($"已选择Auth: {Path.GetFileName(ofd.FileName)}");
                }
            }
        }

        /// <summary>
        /// 解析 Scatter 文件 (新版)
        /// </summary>
        private void MtkParseScatterFileNew(string filePath)
        {
            try
            {
                MtkLogDetail($"解析 Scatter 文件: {filePath}");
                string content = File.ReadAllText(filePath);
                var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                
                string currentPartition = null;
                var partitions = new System.Collections.Generic.List<(string name, long start, long size)>();
                
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("partition_name:"))
                    {
                        currentPartition = trimmed.Substring("partition_name:".Length).Trim();
                    }
                    else if (trimmed.StartsWith("partition_size:") && currentPartition != null)
                    {
                        string sizeStr = trimmed.Substring("partition_size:".Length).Trim();
                        if (sizeStr.StartsWith("0x") || sizeStr.StartsWith("0X"))
                        {
                            long size = Convert.ToInt64(sizeStr, 16);
                            partitions.Add((currentPartition, 0, size));
                        }
                    }
                }
                
                MtkLogSuccess($"从 Scatter 文件解析到 {partitions.Count} 个分区");
                
                if (partitions.Count > 0)
                {
                    SafeInvoke(() =>
                    {
                        mtkListPartitions.Items.Clear();
                        foreach (var (name, start, size) in partitions)
                        {
                            var item = new ListViewItem(new[]
                            {
                                name,
                                "Unknown",
                                FormatFileSizeNew(size),
                                $"0x{start:X}",
                                ""
                            });
                            item.Checked = false;
                            mtkListPartitions.Items.Add(item);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                MtkLogWarning($"解析 Scatter 文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 格式化文件大小 (新版)
        /// </summary>
        private static string FormatFileSizeNew(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
            if (bytes >= 1024L * 1024)
                return $"{bytes / (1024.0 * 1024):F2} MiB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F2} KiB";
            return $"{bytes} B";
        }

        /// <summary>
        /// 获取当前选择的验证方式
        /// </summary>
        private string GetSelectedAuthMethod()
        {
            if (mtkSelectAuthMethod.SelectedIndex <= 0)
                return "Auto";
            switch (mtkSelectAuthMethod.SelectedIndex)
            {
                case 1: return "Normal";       // 正常验证 (使用签名DA)
                case 2: return "RealmeCloud";  // Realme 云端签名
                case 3: return "Exploit";      // 绕过验证 (漏洞利用)
                default: return "Auto";
            }
        }

        /// <summary>
        /// 是否使用漏洞绕过验证
        /// </summary>
        private bool IsExploitMode()
        {
            return GetSelectedAuthMethod() == "Exploit";
        }

        /// <summary>
        /// 是否使用正常签名验证
        /// </summary>
        private bool IsNormalAuthMode()
        {
            return GetSelectedAuthMethod() == "Normal";
        }
        
        /// <summary>
        /// 是否使用 Realme 云端签名验证
        /// </summary>
        private bool IsRealmeCloudAuthMode()
        {
            return GetSelectedAuthMethod() == "RealmeCloud";
        }

        /// <summary>
        /// 获取当前选择的存储类型
        /// </summary>
        private string GetSelectedStorageType()
        {
            if (mtkChkAutoStorage.IsChecked == true)
                return "Auto";
            if (mtkRadioUFS.IsChecked == true)
                return "UFS";
            if (mtkRadioEMMC.IsChecked == true)
                return "EMMC";
            return "Auto";
        }

        #endregion

        #region MTK 设备信息更新 (右侧信息栏对接)

        /// <summary>
        /// 更新设备信息到右侧信息栏
        /// </summary>
        private void MtkUpdateInfoPanel(MtkDeviceInfo deviceInfo)
        {
            SafeInvoke(() =>
            {
                if (deviceInfo == null)
                {
                    MtkClearInfoPanel();
                    return;
                }

                var chipInfo = deviceInfo.ChipInfo;
                string chipName = chipInfo?.ChipName ?? $"MT{chipInfo?.HwCode:X4}";
                string brand = "MTK";
                string version = $"HW: 0x{chipInfo?.HwVer:X4}";
                string serial = deviceInfo.MeIdHex ?? "N/A";
                string model = "未知型号";
                string storage = chipInfo?.SupportsXFlash == true ? "UFS" : "eMMC";
                
                // 获取芯片别名
                ushort hwCode = chipInfo?.HwCode ?? 0;
                var aliases = MtkChipAliases.GetAliases(hwCode);
                if (aliases != null && aliases.Length > 0)
                {
                    chipName = $"{chipName} [{string.Join("/", aliases)}]";
                }

                // 更新右侧信息栏控件
                uiComboBox1.Text = $"设备状态：已连接 ({chipInfo?.ChipName ?? "MTK"})";
                uiLabel9.Text = $"品牌：{brand}";
                uiLabel11.Text = $"芯片：{chipName}";
                uiLabel12.Text = $"版本：{version}";
                uiLabel10.Text = $"芯片序列号：{serial}";
                uiLabel3.Text = $"型号：{model}";
                uiLabel13.Text = $"存储：{storage}";
                uiLabel14.Text = $"型号：{model}";

                // 同时输出到日志
                MtkLogInfo($"════════════════════════════════════");
                MtkLogInfo($"  芯片: {chipName}");
                MtkLogInfo($"  品牌: {brand}");
                MtkLogInfo($"  版本: {version}");
                MtkLogInfo($"  MEID: {serial}");
                MtkLogInfo($"  存储: {storage}");
                MtkLogInfo($"════════════════════════════════════");
            });
        }

        /// <summary>
        /// 清空右侧信息栏
        /// </summary>
        private void MtkClearInfoPanel()
        {
            SafeInvoke(() =>
            {
                uiComboBox1.Text = "设备状态：未连接任何设备";
                uiLabel9.Text = "品牌：等待连接";
                uiLabel11.Text = "芯片：等待连接";
                uiLabel12.Text = "版本：等待连接";
                uiLabel10.Text = "芯片序列号：等待连接";
                uiLabel3.Text = "型号：等待连接";
                uiLabel13.Text = "存储：等待连接";
                uiLabel14.Text = "型号：等待连接";
            });
        }

        /// <summary>
        /// 更新设备连接状态
        /// </summary>
        private void MtkUpdateConnectionStatus(bool connected, string statusText = null)
        {
            SafeInvoke(() =>
            {
                if (connected)
                {
                    uiComboBox1.Text = statusText ?? "设备状态：已连接";
                }
                else
                {
                    uiComboBox1.Text = statusText ?? "设备状态：未连接任何设备";
                    MtkClearInfoPanel();
                }
            });
        }

        #endregion

        #region MTK 本地 DA 路径获取

        /// <summary>
        /// 获取本地选择的 DA 文件路径
        /// </summary>
        private string GetLocalDaFilePath()
        {
            return _mtkDaFilePath;
        }

        /// <summary>
        /// 获取本地选择的 Scatter 文件路径
        /// </summary>
        private string GetLocalScatterFilePath()
        {
            return _mtkScatterFilePath;
        }

        /// <summary>
        /// 获取本地选择的 Auth 文件路径
        /// </summary>
        private string GetLocalAuthFilePath()
        {
            return _mtkAuthFilePath;
        }

        /// <summary>
        /// 检查是否使用云端模式
        /// </summary>
        private bool IsCloudMode()
        {
            return _mtkCurrentBootMode == MtkBootModeType.Cloud;
        }

        /// <summary>
        /// 检查是否使用本地模式
        /// </summary>
        private bool IsLocalMode()
        {
            return _mtkCurrentBootMode == MtkBootModeType.Local;
        }

        #endregion
        
        #region Realme 云端签名认证
        
        // Realme 签名配置 (从设置或界面获取)
        private string _realmeApiUrl = "";
        private string _realmeApiKey = "";
        private string _realmeAccount = "";
        private SignServerType _realmeServerType = SignServerType.Realme;
        
        // 固件包参数 (从固件包解析)
        private string _realmeFirmwarePath;
        private string _realmeProjectNo;
        private string _realmeNvCode;
        private string _realmeNewSwNameSign;
        #pragma warning disable CS0649 // 未赋值字段 - 将由设备读取时填充
        private string _realmeOldSwNameSign;
        #pragma warning restore CS0649
        private string _realmeDaPath;
        private string _realmeAllInOneSigPath;
        
        /// <summary>
        /// 验证方式选择变更事件
        /// </summary>
        private void MtkSelectAuthMethod_SelectedIndexChanged(object sender, EventArgs e)
        {
            string method = GetSelectedAuthMethod();
            MtkLogInfo($"Verification method: {method}");
            
            // 如果选择 Realme 云端签名 (索引 2)，弹出固件包选择
            if (mtkSelectAuthMethod.SelectedIndex == 2 || method == "RealmeCloud")
            {
                PromptSelectFirmwarePackage();
            }
        }
        
        /// <summary>
        /// 弹出固件包选择对话框
        /// </summary>
        private void PromptSelectFirmwarePackage()
        {
            MtkLogInfo("[Realme] Please select firmware package to get signing parameters...");
            
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select OPLUS/Realme firmware package folder";
                fbd.ShowNewFolderButton = false;
                
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    ParseFirmwarePackageForRealme(fbd.SelectedPath);
                }
                else
                {
                    MtkLogWarning("[Realme] No firmware package selected");
                }
            }
        }
        
        /// <summary>
        /// 解析固件包获取 Realme 签名参数
        /// </summary>
        private void ParseFirmwarePackageForRealme(string firmwarePath)
        {
            _realmeFirmwarePath = firmwarePath;
            MtkLogInfo($"[Realme] ═══════════════════════════════════════");
            MtkLogInfo($"[Realme] Parsing firmware: {Path.GetFileName(firmwarePath)}");
            MtkLogInfo($"[Realme] ═══════════════════════════════════════");
            
            try
            {
                // 1. 查找 Scatter 文件
                string scatterPath = FindScatterFile(firmwarePath);
                if (!string.IsNullOrEmpty(scatterPath))
                {
                    MtkLogInfo($"[Realme] Found Scatter: {Path.GetFileName(scatterPath)}");
                    ParseScatterForSignParams(scatterPath);
                }
                
                // 2. 查找 DA 文件
                _realmeDaPath = FindDaFile(firmwarePath);
                if (!string.IsNullOrEmpty(_realmeDaPath))
                {
                    MtkLogInfo($"[Realme] Found DA: {Path.GetFileName(_realmeDaPath)}");
                    _mtkDaFilePath = _realmeDaPath;
                    SafeInvoke(() => mtkInputDA.Text = Path.GetFileName(_realmeDaPath));
                }
                
                // 3. 查找 AllInOneSignature 文件
                _realmeAllInOneSigPath = RealmeAuthService.FindAllInOneSignature(firmwarePath);
                if (!string.IsNullOrEmpty(_realmeAllInOneSigPath))
                {
                    var sigInfo = new FileInfo(_realmeAllInOneSigPath);
                    MtkLogInfo($"[Realme] Found Signature: {Path.GetFileName(_realmeAllInOneSigPath)} ({sigInfo.Length} bytes)");
                }
                
                // 4. 输出解析结果
                MtkLogInfo($"[Realme] ─────────────────────────────────────");
                MtkLogInfo($"[Realme] ProjectNo: {_realmeProjectNo ?? "N/A"}");
                MtkLogInfo($"[Realme] NvCode: {_realmeNvCode ?? "N/A"}");
                if (!string.IsNullOrEmpty(_realmeNewSwNameSign))
                {
                    MtkLogInfo($"[Realme] NewSwNameSign: {_realmeNewSwNameSign.Substring(0, Math.Min(16, _realmeNewSwNameSign.Length))}...");
                }
                MtkLogInfo($"[Realme] DA: {(_realmeDaPath != null ? "Found" : "Not found")}");
                MtkLogInfo($"[Realme] Signature: {(_realmeAllInOneSigPath != null ? "Found" : "Not found")}");
                MtkLogInfo($"[Realme] ─────────────────────────────────────");
                
                // 6. 检查是否可以直接使用固件包签名
                if (!string.IsNullOrEmpty(_realmeAllInOneSigPath))
                {
                    MtkLogSuccess("[Realme] Firmware package signature available - can use directly without API");
                }
                else if (!string.IsNullOrEmpty(_realmeProjectNo) && !string.IsNullOrEmpty(_realmeNewSwNameSign))
                {
                    MtkLogInfo("[Realme] Parameters ready for cloud signing API");
                }
                else
                {
                    MtkLogWarning("[Realme] Missing some parameters, may need manual input");
                }
            }
            catch (Exception ex)
            {
                MtkLogError($"[Realme] Parse firmware error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 从 Scatter 文件解析签名参数
        /// </summary>
        private void ParseScatterForSignParams(string scatterPath)
        {
            try
            {
                string content = File.ReadAllText(scatterPath);
                string dir = Path.GetDirectoryName(scatterPath);
                
                // 解析项目号 - 多种格式
                // 格式1: project: 24825 或 project_no: 24825
                // 格式2: - project: 24825 (YAML格式)
                // 格式3: "project": "24825" (JSON格式)
                string[] projectPatterns = {
                    @"[-\s]project(?:_no)?[:\s]+[""']?(\d{4,6})[""']?",
                    @"""project(?:No|_no)?""[:\s]*[""']?(\d{4,6})[""']?",
                    @"project(?:No|Number)?[=:\s]+(\d{4,6})",
                    @"MT\d+_(\d{4,6})_"  // 从文件名提取 MT6989_24825_
                };
                foreach (var pattern in projectPatterns)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        content, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        _realmeProjectNo = match.Groups[1].Value;
                        break;
                    }
                }
                
                // 如果从内容中没找到，尝试从文件名提取
                if (string.IsNullOrEmpty(_realmeProjectNo))
                {
                    var fileNameMatch = System.Text.RegularExpressions.Regex.Match(
                        Path.GetFileName(scatterPath), @"MT\d+[_-](\d{4,6})[_-]");
                    if (fileNameMatch.Success)
                    {
                        _realmeProjectNo = fileNameMatch.Groups[1].Value;
                    }
                }
                
                // 解析 NvCode - 多种格式
                string[] nvCodePatterns = {
                    @"nv[_-]?code[:\s]*[""']?([01]{8,})[""']?",
                    @"nv[_-]?platform[:\s]*[""']?([a-zA-Z0-9]+)[""']?",
                    @"""nvCode""[:\s]*[""']?([01]{8,})[""']?"
                };
                foreach (var pattern in nvCodePatterns)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        content, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        _realmeNvCode = match.Groups[1].Value;
                        break;
                    }
                }
                
                // 解析 SwNameSign (SHA256 hash) - 64位十六进制
                string[] swNamePatterns = {
                    @"sw[_-]?name[_-]?sign[:\s]*[""']?([a-fA-F0-9]{64})[""']?",
                    @"""swNameSign""[:\s]*[""']?([a-fA-F0-9]{64})[""']?",
                    @"new[_-]?sw[_-]?name[_-]?sign[:\s]*[""']?([a-fA-F0-9]{64})[""']?"
                };
                foreach (var pattern in swNamePatterns)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        content, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        _realmeNewSwNameSign = match.Groups[1].Value.ToLower();
                        break;
                    }
                }
                
                // 尝试从固件包其他文件获取参数
                if (string.IsNullOrEmpty(_realmeProjectNo) || string.IsNullOrEmpty(_realmeNvCode))
                {
                    TryParseFromOtherFiles(dir);
                }
                
                // 设置到 Scatter 显示
                _mtkScatterFilePath = scatterPath;
                SafeInvoke(() => mtkInputScatter.Text = Path.GetFileName(scatterPath));
            }
            catch (Exception ex)
            {
                MtkLogWarning($"[Realme] Parse Scatter error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 尝试从固件包其他文件获取参数
        /// </summary>
        private void TryParseFromOtherFiles(string firmwareDir)
        {
            try
            {
                // 查找可能包含参数的文件
                string[] configFiles = {
                    "flash_config.xml",
                    "config.xml", 
                    "flash.xml",
                    "project_config.mk",
                    "build.prop",
                    "*.json"
                };
                
                foreach (var pattern in configFiles)
                {
                    var files = Directory.GetFiles(firmwareDir, pattern, SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        try
                        {
                            string content = File.ReadAllText(file);
                            
                            // 解析 project_no
                            if (string.IsNullOrEmpty(_realmeProjectNo))
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(
                                    content, @"[""']?project(?:_?no|Number)?[""']?[:\s=]+[""']?(\d{4,6})[""']?",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (match.Success)
                                {
                                    _realmeProjectNo = match.Groups[1].Value;
                                    MtkLogInfo($"[Realme] Found ProjectNo in {Path.GetFileName(file)}: {_realmeProjectNo}");
                                }
                            }
                            
                            // 解析 nv_code
                            if (string.IsNullOrEmpty(_realmeNvCode))
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(
                                    content, @"[""']?nv[_-]?code[""']?[:\s=]+[""']?([01]{8,})[""']?",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (match.Success)
                                {
                                    _realmeNvCode = match.Groups[1].Value;
                                    MtkLogInfo($"[Realme] Found NvCode in {Path.GetFileName(file)}: {_realmeNvCode}");
                                }
                            }
                            
                            // 解析 sw_name_sign
                            if (string.IsNullOrEmpty(_realmeNewSwNameSign))
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(
                                    content, @"[""']?sw[_-]?name[_-]?sign[""']?[:\s=]+[""']?([a-fA-F0-9]{64})[""']?",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (match.Success)
                                {
                                    _realmeNewSwNameSign = match.Groups[1].Value.ToLower();
                                    MtkLogInfo($"[Realme] Found SwNameSign in {Path.GetFileName(file)}");
                                }
                            }
                            
                            // 如果都找到了就退出
                            if (!string.IsNullOrEmpty(_realmeProjectNo) && 
                                !string.IsNullOrEmpty(_realmeNvCode) &&
                                !string.IsNullOrEmpty(_realmeNewSwNameSign))
                            {
                                return;
                            }
                        }
                        catch { /* 忽略单个文件错误 */ }
                    }
                }
            }
            catch { /* 忽略错误 */ }
        }
        
        /// <summary>
        /// 查找 Scatter 文件
        /// </summary>
        private string FindScatterFile(string dir)
        {
            string[] patterns = { "*scatter*.txt", "*scatter*.xml" };
            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
            return null;
        }
        
        /// <summary>
        /// 查找 DA 文件
        /// </summary>
        private string FindDaFile(string dir)
        {
            string[] names = { "DA_BR.bin", "DA.bin", "download_agent.bin" };
            foreach (var name in names)
            {
                var files = Directory.GetFiles(dir, name, SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
            // 通配符搜索
            var daFiles = Directory.GetFiles(dir, "*.bin", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).StartsWith("DA", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return daFiles.Length > 0 ? daFiles[0] : null;
        }
        
        /// <summary>
        /// 配置 Realme 云端签名
        /// </summary>
        public void ConfigureRealmeCloudAuth(string apiUrl, string apiKey = null, string account = null, SignServerType serverType = SignServerType.Realme)
        {
            _realmeApiUrl = apiUrl;
            _realmeApiKey = apiKey;
            _realmeAccount = account;
            _realmeServerType = serverType;
            
            MtkLogInfo($"[Realme] 已配置 {serverType} 云端签名服务");
        }
        
        /// <summary>
        /// 检查是否使用 Realme 云端认证
        /// </summary>
        private bool IsRealmeAuthMode()
        {
            // 验证方式选择 "Realme云端签名" 或 (正常验证 + 云端模式 + 有API配置)
            return IsRealmeCloudAuthMode() || 
                   (IsNormalAuthMode() && IsCloudMode() && !string.IsNullOrEmpty(_realmeApiUrl));
        }
        
        /// <summary>
        /// 获取选定的签名服务类型
        /// </summary>
        private SignServerType GetSelectedSignServerType()
        {
            return _realmeServerType;
        }
        
        /// <summary>
        /// 准备 Realme 签名请求 (设备信息)
        /// </summary>
        private RealmSignRequest PrepareRealmeSignRequest()
        {
            if (_mtkService == null)
            {
                MtkLogWarning("[Realme] 服务未初始化");
                return null;
            }
            
            var request = _mtkService.GetRealmeSignRequest();
            if (request == null)
            {
                MtkLogWarning("[Realme] 无法获取设备信息");
                return null;
            }
            
            MtkLogInfo($"[Realme] ═══════════════════════════════════════");
            MtkLogInfo($"[Realme] 🔔 REALME OPLUS SIGN INFO 🔔");
            MtkLogInfo($"[Realme] ═══════════════════════════════════════");
            MtkLogInfo($"[Realme] 📱 Platform: {request.Platform}");
            MtkLogInfo($"[Realme] 🔧 Chipset: {request.Chipset}");
            MtkLogInfo($"[Realme] 🔢 HW Code: {request.HwCode}");
            MtkLogInfo($"[Realme] 🖥️ Server: {_realmeServerType}");
            
            if (!string.IsNullOrEmpty(request.SerialNumber))
            {
                string sn = request.SerialNumber;
                if (sn.Length > 16)
                {
                    sn = sn.Substring(0, 16) + "...";
                }
                MtkLogInfo($"[Realme] 📋 Serial: {sn}");
            }
            
            return request;
        }
        
        /// <summary>
        /// 处理 Realme 云端签名响应
        /// </summary>
        private bool ProcessRealmeSignResponse(string responseJson)
        {
            if (string.IsNullOrEmpty(responseJson))
            {
                MtkLogError("[Realme] 签名响应为空");
                return false;
            }
            
            MtkLogDetail($"[Realme] 响应: {responseJson}");
            
            try
            {
                // 解析 signedDataStr
                string signedDataStr = ExtractJsonValue(responseJson, "signedDataStr");
                string code = ExtractJsonValue(responseJson, "code");
                string msg = ExtractJsonValue(responseJson, "msg");
                
                if (code != "000000")
                {
                    MtkLogError($"[Realme] ❌ 签名失败: {code} - {msg}");
                    return false;
                }
                
                if (string.IsNullOrEmpty(signedDataStr))
                {
                    MtkLogError("[Realme] ❌ 签名数据为空");
                    return false;
                }
                
                // Base64 解码
                byte[] signatureData = Convert.FromBase64String(signedDataStr);
                MtkLogSuccess($"[Realme] ✅ 获取签名成功: {signatureData.Length} bytes");
                
                // 设置到服务
                _mtkService.SignatureData = signatureData;
                
                return true;
            }
            catch (Exception ex)
            {
                MtkLogError($"[Realme] ❌ 解析签名响应失败: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// 从 JSON 中提取值
        /// </summary>
        private string ExtractJsonValue(string json, string key)
        {
            // 尝试 "key":"value" 格式
            string pattern1 = $"\"{key}\":\"";
            int idx = json.IndexOf(pattern1);
            if (idx >= 0)
            {
                int start = idx + pattern1.Length;
                int end = json.IndexOf("\"", start);
                if (end > start)
                {
                    return json.Substring(start, end - start);
                }
            }
            
            // 尝试 "key":value 格式
            string pattern2 = $"\"{key}\":";
            idx = json.IndexOf(pattern2);
            if (idx >= 0)
            {
                int start = idx + pattern2.Length;
                while (start < json.Length && json[start] == ' ') start++;
                
                int end = start;
                while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ' ')
                {
                    end++;
                }
                
                if (end > start)
                {
                    return json.Substring(start, end - start).Trim();
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// 执行 Realme 云端签名认证 (完整流程)
        /// 优先级:
        /// 1. 使用固件包中的 AllInOneSignature (如果有)
        /// 2. 调用云端 API (如果已配置)
        /// 3. 使用预设签名数据
        /// </summary>
        private async System.Threading.Tasks.Task<bool> ExecuteRealmeCloudAuthAsync(System.Threading.CancellationToken ct = default)
        {
            MtkLogInfo("[Realme] ═══════════════════════════════════════");
            MtkLogInfo("[Realme] Executing Realme authentication...");
            MtkLogInfo("[Realme] ═══════════════════════════════════════");
            
            if (_mtkService == null)
            {
                MtkLogError("[Realme] Service not initialized");
                return false;
            }
            
            // 优先级 1: 使用固件包中的 AllInOneSignature
            if (!string.IsNullOrEmpty(_realmeAllInOneSigPath) && File.Exists(_realmeAllInOneSigPath))
            {
                MtkLogInfo("[Realme] Using firmware package signature...");
                MtkLogInfo($"[Realme] File: {Path.GetFileName(_realmeAllInOneSigPath)}");
                
                return await _mtkService.AuthWithAllInOneSignatureAsync(_realmeAllInOneSigPath, ct);
            }
            
            // 配置 Realme 服务
            _mtkService.ConfigureRealmeAuth(_realmeApiUrl, _realmeApiKey, _realmeAccount, _realmeServerType);
            
            // 优先级 2: 使用云端 API (如果配置了 URL 和固件参数)
            if (!string.IsNullOrEmpty(_realmeApiUrl))
            {
                MtkLogInfo("[Realme] Using cloud API for signing...");
                
                // 如果有固件包参数，填充到请求中
                if (!string.IsNullOrEmpty(_realmeProjectNo) && !string.IsNullOrEmpty(_realmeNewSwNameSign))
                {
                    MtkLogInfo($"[Realme] ProjectNo: {_realmeProjectNo}");
                    MtkLogInfo($"[Realme] NvCode: {_realmeNvCode ?? "N/A"}");
                    
                    // 获取签名数据
                    byte[] signature = await _mtkService.GetGsmFutureSignatureAsync(
                        _realmeProjectNo,
                        _realmeNvCode ?? "",
                        _realmeNewSwNameSign,
                        _realmeOldSwNameSign ?? _realmeNewSwNameSign,
                        ct);
                    
                    if (signature != null && signature.Length > 0)
                    {
                        return await _mtkService.ExecuteRealmeAuthWithSignatureAsync(signature, ct);
                    }
                    
                    MtkLogWarning("[Realme] Cloud API returned empty signature");
                }
                else
                {
                    MtkLogWarning("[Realme] Missing firmware parameters for cloud signing");
                    MtkLogInfo("[Realme] Please select firmware package first");
                    
                    // 提示选择固件包
                    SafeInvoke(() => PromptSelectFirmwarePackage());
                }
            }
            
            // 优先级 3: 使用预设签名数据
            if (_mtkService.SignatureData != null && _mtkService.SignatureData.Length > 0)
            {
                MtkLogInfo("[Realme] Using pre-fetched signature data...");
                return await _mtkService.ExecuteRealmeAuthWithSignatureAsync(_mtkService.SignatureData, ct);
            }
            
            MtkLogWarning("[Realme] No signature available - authentication may fail");
            MtkLogInfo("[Realme] Options:");
            MtkLogInfo("[Realme]   1. Select firmware package with all-in-one-signature.bin");
            MtkLogInfo("[Realme]   2. Configure cloud API URL and token");
            return false;
        }
        
        /// <summary>
        /// 设置预获取的签名数据 (例如从外部 API 获取)
        /// </summary>
        public void SetPreFetchedSignature(byte[] signatureData)
        {
            if (_mtkService != null)
            {
                _mtkService.SignatureData = signatureData;
                MtkLogInfo($"[Realme] 已设置预获取签名: {signatureData?.Length ?? 0} bytes");
            }
        }
        
        /// <summary>
        /// 设置 Base64 格式的预获取签名
        /// </summary>
        public void SetPreFetchedSignatureBase64(string base64Signature)
        {
            if (string.IsNullOrEmpty(base64Signature))
            {
                MtkLogWarning("[Realme] Base64 签名为空");
                return;
            }
            
            try
            {
                byte[] data = Convert.FromBase64String(base64Signature);
                SetPreFetchedSignature(data);
            }
            catch (Exception ex)
            {
                MtkLogError($"[Realme] Base64 解码失败: {ex.Message}");
            }
        }
        
        #endregion
    }
}




