using OPFlashTool.Services;
using Sunny.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using SakuraEDL.Qualcomm.UI;
using SakuraEDL.Qualcomm.Common;
using SakuraEDL.Qualcomm.Models;
using SakuraEDL.Qualcomm.Services;
using SakuraEDL.Fastboot.UI;
using SakuraEDL.Fastboot.Common;
using SakuraEDL.Qualcomm.Database;
using SakuraEDL.Common;

namespace SakuraEDL
{
    public partial class Form1 : AntdUI.Window
    {
        private string logFilePath;
        private string selectedLocalImagePath = "";
        #pragma warning disable CS0414
        private string input8OriginalText = "";
        #pragma warning restore CS0414
        private bool isEnglish = false;
 
        // 图片URL历史记录
        private List<string> urlHistory = new List<string>();

        // 图片预览缓存
        private List<Image> previewImages = new List<Image>();
        private const int MAX_PREVIEW_IMAGES = 5; // 最多保存5个预览

        // 系统信息（用于恢复 uiLabel4）
        private string _systemInfoText = "Computer: Unknown";

        // 原始控件位置和大小
        private Point originalinput6Location;
        private Point originalbutton4Location;
        private Point originalcheckbox13Location;
        private Point originalinput7Location;
        private Point originalinput9Location;
        private Point originallistView2Location;
        private Size originallistView2Size;
        private Point originaluiGroupBox4Location;
        private Size originaluiGroupBox4Size;

        // 高通 UI 控制器
        private QualcommUIController _qualcommController;
        private System.Windows.Forms.Timer _portRefreshTimer;
        private string _lastPortList = "";
        private int _lastEdlCount = 0;
        private bool _isOnFastbootTab = false;  // 当前是否在 Fastboot 标签页
        private string _selectedXmlDirectory = "";  // 存储选择的 XML 文件所在目录

        // Fastboot UI 控制器
        private FastbootUIController _fastbootController;

        // 云端 Loader 列表
        private List<SakuraEDL.Qualcomm.Services.CloudLoaderInfo> _cloudLoaders = new List<SakuraEDL.Qualcomm.Services.CloudLoaderInfo>();
        private bool _cloudLoadersLoaded = false;

        public Form1()
        {
            InitializeComponent();
            
            // 启用双缓冲减少闪烁 (针对低配电脑优化)
            if (PerformanceConfig.EnableDoubleBuffering)
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | 
                         ControlStyles.AllPaintingInWmPaint | 
                         ControlStyles.UserPaint, true);
                UpdateStyles();
            }
            
            // 初始化日志系统
            InitializeLogSystem();
            
            checkbox14.Checked = true;
            radio3.Checked = true;
            
            // 初始化语言（同步，不阻塞）
            LanguageManager.Initialize();
            
            // 加载系统信息（使用预加载数据）
            this.Load += async (sender, e) =>
            {
                try
                {
                    // 应用当前语言
                    ApplyLanguage();
                    
                    // 使用预加载的系统信息
                    string sysInfo = PreloadManager.SystemInfo ?? "Unknown";
                    _systemInfoText = LanguageManager.T("status.computer") + ": " + sysInfo;
                    uiLabel4.Text = _systemInfoText;
                    
                    // 写入系统信息到日志头部
                    WriteLogHeader(sysInfo);
                    AppendLog(LanguageManager.T("log.loaded"), Color.Green);
                    
                    // 加载一言
                    await LoadHitokotoAsync();
                    
                }
                catch (Exception ex)
                {
                    uiLabel4.Text = LanguageManager.TranslateLegacyText($"系统信息错误: {ex.Message}");
                    AppendLog($"初始化失败: {ex.Message}", Color.Red);
                }
            };

            // 绑定按钮事件
            button2.Click += Button2_Click;
            button3.Click += Button3_Click;
            slider1.ValueChanged += Slider1_ValueChanged;
            
            // 添加 select3 事件绑定
            select3.SelectedIndexChanged += Select3_SelectedIndexChanged;
            
            // 保存原始控件位置和大小
            SaveOriginalPositions();
            
            // 添加 checkbox17 和checkbox19 事件绑定
            checkbox17.CheckedChanged += Checkbox17_CheckedChanged;
            checkbox19.CheckedChanged += Checkbox19_CheckedChanged;

            // 初始化URL下拉框
            InitializeUrlComboBox();

            // 初始化图片预览控件
            InitializeImagePreview();

            // 默认调整控件布局
            ApplyCompactLayout();

            label4.Visible = false;
            uiComboBox4.Visible = false;
            uiComboBox4.SelectedIndexChanged -= UiComboBox4_SelectedIndexChanged;

            // 初始化高通模块
            InitializeQualcommModule();

            // 初始化 Fastboot 模块
            InitializeFastbootModule();
            
            // 初始化 EDL Loader 选择列表
            InitializeEdlLoaderList();

            // 初始化展讯模块
            InitializeSpreadtrumModule();

            // 初始化联发科模块
            InitializeMediaTekModule();
        }

        #region 高通模块

        private void InitializeQualcommModule()
        {
            try
            {
                // 创建高通 UI 控制器 (传入两个日志委托：UI日志 + 详细调试日志)
                _qualcommController = new QualcommUIController(
                    (msg, color) => AppendLog(msg, color),
                    msg => AppendLogDetail(msg));
                
                // 订阅小米授权令牌事件 (内置签名失败时弹窗显示令牌)
                _qualcommController.XiaomiAuthTokenRequired += OnXiaomiAuthTokenRequired;

                // 设置 listView2 支持多选和复选框
                listView2.MultiSelect = true;
                listView2.CheckBoxes = true;
                listView2.FullRowSelect = true;

                // 绑定控件 - tabPage2 上的高通控件
                // checkbox12 = 跳过引导, checkbox16 = 保护分区, input6 = 引导文件路径
                _qualcommController.BindControls(
                    portComboBox: uiComboBox1,           // 全局端口选择
                    partitionListView: listView2,        // 分区列表
                    progressBar: uiProcessBar1,          // 总进度条 (长) - 显示整体操作进度
                    statusLabel: null,
                    skipSaharaCheckbox: checkbox12,      // 跳过引导
                    protectPartitionsCheckbox: checkbox16, // 保护分区
                    programmerPathTextbox: null,         // input6 是 AntdUI.Input 类型，需要特殊处理
                    outputPathTextbox: null,
                    timeLabel: uiLabel6,                 // 时间标签
                    speedLabel: uiLabel7,                // 速度标签
                    operationLabel: uiLabel8,            // 当前操作标签
                    subProgressBar: uiProcessBar2,       // 子进度条 (短) - 显示单个操作实时进度
                    // 设备信息标签 (uiGroupBox3)
                    brandLabel: uiLabel9,                // 品牌
                    chipLabel: uiLabel11,                // 芯片
                    modelLabel: uiLabel3,                // 设备型号
                    serialLabel: uiLabel10,              // 序列号
                    storageLabel: uiLabel13,             // 存储类型
                    unlockLabel: uiLabel14,              // 设备型号2
                    otaVersionLabel: uiLabel12           // OTA版本
                );

                // ========== tabPage2 高通页面按钮事件 ==========
                // uiButton6 = 读取分区表, uiButton7 = 读取分区
                // uiButton8 = 写入分区, uiButton9 = 擦除分区
                uiButton6.Click += async (s, e) => await QualcommReadPartitionTableAsync();
                uiButton7.Click += async (s, e) => await QualcommReadPartitionAsync();
                uiButton8.Click += async (s, e) => await QualcommWritePartitionAsync();
                uiButton9.Click += async (s, e) => await QualcommErasePartitionAsync();

                // ========== 文件选择 ==========
                // input8 = 双击选择引导文件 (Programmer/Firehose)
                input8.DoubleClick += (s, e) => QualcommSelectProgrammer();
                
                // input9 = 双击选择 Digest 文件 (VIP认证用)
                input9.DoubleClick += (s, e) => QualcommSelectDigest();
                
                // input7 = 双击选择 Signature 文件 (VIP认证用)
                input7.DoubleClick += (s, e) => QualcommSelectSignature();
                
                // input6 = 双击选择 rawprogram.xml
                input6.DoubleClick += (s, e) => QualcommSelectRawprogramXml();
                
                // button4 = input6 右边的浏览按钮 (选择 Raw XML)
                button4.Click += (s, e) => QualcommSelectRawprogramXml();

                // 分区搜索 (select4 = 查找分区)
                select4.TextChanged += (s, e) => QualcommSearchPartition();
                select4.SelectedIndexChanged += (s, e) => { _isSelectingFromDropdown = true; };

                // 存储类型选择 (radio3 = UFS, radio4 = eMMC)
                radio3.CheckedChanged += (s, e) => { if (radio3.Checked) _storageType = "ufs"; };
                radio4.CheckedChanged += (s, e) => { if (radio4.Checked) _storageType = "emmc"; };

                // 注意: checkbox17/checkbox19 的事件已在构造函数中绑定 (Checkbox17_CheckedChanged / Checkbox19_CheckedChanged)
                // 那里会调用 UpdateAuthMode()，这里不再重复绑定

                // ========== checkbox13 全选/取消全选 ==========
                checkbox13.CheckedChanged += (s, e) => QualcommSelectAllPartitions(checkbox13.Checked);

                // ========== listView2 双击选择镜像文件 ==========
                listView2.DoubleClick += (s, e) => QualcommPartitionDoubleClick();

                // ========== checkbox11 生成XML 选项 ==========
                // 这只是一个开关，表示回读分区时是否同时生成 XML
                // 实际生成在回读完成后执行

                // ========== checkbox15 自动重启 (刷写完成后) ==========
                // 状态读取已在 QualcommErasePartitionAsync 等操作中检查

                // ========== EDL 操作菜单事件 ==========
                toolStripMenuItem4.Click += async (s, e) => await _qualcommController.RebootToEdlAsync();
                toolStripMenuItem5.Click += async (s, e) => await _qualcommController.RebootToSystemAsync();
                eDL切换槽位ToolStripMenuItem.Click += async (s, e) => await QualcommSwitchSlotAsync();
                激活LUNToolStripMenuItem.Click += async (s, e) => await QualcommSetBootLunAsync();
                
                // ========== 快捷操作菜单事件 (设备管理器) ==========
                // 重启系统 (ADB/Fastboot)
                toolStripMenuItem2.Click += async (s, e) => await QuickRebootSystemAsync();
                // 重启到 Fastboot (ADB/Fastboot)
                toolStripMenuItem6.Click += async (s, e) => await QuickRebootBootloaderAsync();
                // 重启到 Fastbootd (ADB/Fastboot)
                toolStripMenuItem7.Click += async (s, e) => await QuickRebootFastbootdAsync();
                // 重启到 Recovery (ADB/Fastboot)
                重启恢复ToolStripMenuItem.Click += async (s, e) => await QuickRebootRecoveryAsync();
                // MI踢EDL (Fastboot only)
                mIToolStripMenuItem.Click += async (s, e) => await QuickMiRebootEdlAsync();
                // 联想或安卓踢EDL (ADB only)
                联想或安卓踢EDLToolStripMenuItem.Click += async (s, e) => await QuickAdbRebootEdlAsync();
                // 擦除谷歌锁 (Fastboot only)
                擦除谷歌锁ToolStripMenuItem.Click += async (s, e) => await QuickEraseFrpAsync();
                // 切换槽位 (Fastboot only)
                切换槽位ToolStripMenuItem.Click += async (s, e) => await QuickSwitchSlotAsync();
                
                // ========== 其他菜单事件 ==========
                // 设备管理器
                设备管理器ToolStripMenuItem.Click += (s, e) => OpenDeviceManager();
                // CMD命令行
                cMD命令行ToolStripMenuItem.Click += (s, e) => OpenCommandPrompt();
                // 安卓驱动
                安卓驱动ToolStripMenuItem.Click += (s, e) => OpenDriverInstaller("android");
                // MTK驱动
                mTK驱动ToolStripMenuItem.Click += (s, e) => OpenDriverInstaller("mtk");
                // 高通驱动
                高通驱动ToolStripMenuItem.Click += (s, e) => OpenDriverInstaller("qualcomm");
                // 展讯驱动
                展讯驱动ToolStripMenuItem.Click += (s, e) => OpenDriverInstaller("spreadtrum");

                // ========== 停止按钮 ==========
                uiButton1.Click += (s, e) => StopCurrentOperation();

                // ========== 刷新端口 ==========
                // 初始化时刷新端口列表（静默模式）
                _lastEdlCount = _qualcommController.RefreshPorts(silent: true);
                
                // 启动端口自动检测定时器 (根据性能配置调整间隔)
                _portRefreshTimer = new System.Windows.Forms.Timer();
                _portRefreshTimer.Interval = Common.PerformanceConfig.PortRefreshInterval;
                _portRefreshTimer.Tick += (s, e) => RefreshPortsIfIdle();
                _portRefreshTimer.Start();

                AppendLog("高通模块初始化完成", Color.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"高通模块初始化失败: {ex.Message}", Color.Red);
            }
        }

        private string _storageType = "ufs";
        private string _authMode = "none";
        private string _cloudLoaderAuthType = "none";  // 当前云端 Loader 的验证类型 (vip/demacia/miauth/none)

        /// <summary>
        /// 空闲时刷新端口（检测设备连接/断开）
        /// </summary>
        private void RefreshPortsIfIdle()
        {
            try
            {
                // 如果当前在 Fastboot 标签页，不刷新高通端口
                if (_isOnFastbootTab)
                    return;
                    
                // 如果有正在进行的操作，不刷新
                if (_qualcommController != null && _qualcommController.HasPendingOperation)
                    return;

                // 获取当前端口列表用于变化检测
                var ports = SakuraEDL.Qualcomm.Common.PortDetector.DetectAllPorts();
                var edlPorts = SakuraEDL.Qualcomm.Common.PortDetector.DetectEdlPorts();
                string currentPortList = string.Join(",", ports.ConvertAll(p => p.PortName));
                
                // 只有端口列表变化时才刷新
                if (currentPortList != _lastPortList)
                {
                    bool hadEdl = _lastEdlCount > 0;
                    bool newEdlDetected = edlPorts.Count > 0 && !hadEdl;
                    _lastPortList = currentPortList;
                    
                    // 静默刷新，返回EDL端口数量
                    int edlCount = _qualcommController?.RefreshPorts(silent: true) ?? 0;
                    
                    // 新检测到EDL设备时提示
                    if (newEdlDetected && edlPorts.Count > 0)
                    {
                        AppendLog($"检测到 EDL 设备: {edlPorts[0].PortName} - {edlPorts[0].Description}", Color.LimeGreen);
                    }
                    
                    _lastEdlCount = edlCount;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EDL 端口检测异常: {ex.Message}");
            }
        }

        private void UpdateAuthMode()
        {
            if (checkbox17.Checked && checkbox19.Checked)
            {
                checkbox17.Checked = false; // 互斥，优先 VIP
            }
            
            if (checkbox17.Checked)
                _authMode = "demacia";  // oldoneplus = OnePlus/demacia 验证
            else if (checkbox19.Checked)
                _authMode = "vip";      // oplus = VIP 验证
            else
                _authMode = "none";
        }

        private void QualcommSelectProgrammer()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择引导文件 (Programmer/Firehose)";
                ofd.Filter = "引导文件|*.mbn;*.elf|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    input8.Text = ofd.FileName;
                    AppendLog($"已选择引导文件: {Path.GetFileName(ofd.FileName)}", Color.Green);
                }
            }
        }

        private async void QualcommSelectDigest()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择 Digest 文件 (VIP认证)";
                ofd.Filter = "Digest文件|*.elf;*.bin;*.mbn|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    input9.Text = ofd.FileName;
                    AppendLog($"已选择 Digest: {Path.GetFileName(ofd.FileName)}", Color.Green);

                    // 如果已连接设备且已选择 Signature，自动执行 VIP 认证
                    if (_qualcommController != null && _qualcommController.IsConnected)
                    {
                        string signaturePath = input7.Text;
                        if (!string.IsNullOrEmpty(signaturePath) && File.Exists(signaturePath))
                        {
                            try
                            {
                                AppendLog("已选择完整 VIP 认证文件，开始认证...", Color.Blue);
                                await QualcommPerformVipAuthAsync();
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"VIP 认证异常: {ex.Message}", Color.Red);
                            }
                        }
                    }
                }
            }
        }

        private async void QualcommSelectSignature()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择 Signature 文件 (VIP认证)";
                ofd.Filter = "Signature文件|*.bin;signature*|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    input7.Text = ofd.FileName;
                    AppendLog($"已选择 Signature: {Path.GetFileName(ofd.FileName)}", Color.Green);
                    
                    // 如果已连接设备且已选择 Digest，自动执行 VIP 认证
                    if (_qualcommController != null && _qualcommController.IsConnected)
                    {
                        string digestPath = input9.Text;
                        if (!string.IsNullOrEmpty(digestPath) && File.Exists(digestPath))
                        {
                            try
                            {
                                AppendLog("已选择完整 VIP 认证文件，开始认证...", Color.Blue);
                                await QualcommPerformVipAuthAsync();
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"VIP 认证异常: {ex.Message}", Color.Red);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 手动执行 VIP 认证 (OPPO/Realme)
        /// </summary>
        private async Task QualcommPerformVipAuthAsync()
        {
            if (_qualcommController == null || !_qualcommController.IsConnected)
            {
                AppendLog("请先连接设备", Color.Orange);
                return;
            }

            string digestPath = input9.Text;
            string signaturePath = input7.Text;

            if (string.IsNullOrEmpty(digestPath) || !File.Exists(digestPath))
            {
                AppendLog("请先选择 Digest 文件 (双击输入框选择)", Color.Orange);
                return;
            }

            if (string.IsNullOrEmpty(signaturePath) || !File.Exists(signaturePath))
            {
                AppendLog("请先选择 Signature 文件 (双击输入框选择)", Color.Orange);
                return;
            }

            bool success = await _qualcommController.PerformVipAuthAsync(digestPath, signaturePath);
            if (success)
            {
                AppendLog("VIP 认证成功，现在可以操作敏感分区", Color.Green);
            }
        }

        private void QualcommSelectRawprogramXml()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择 Rawprogram XML 文件 (可多选)";
                ofd.Filter = "XML文件|rawprogram*.xml;*.xml|所有文件|*.*";
                ofd.Multiselect = true;  // 支持多选
                
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    // 保存 XML 文件所在目录（用于后续搜索 patch 文件）
                    _selectedXmlDirectory = Path.GetDirectoryName(ofd.FileNames[0]) ?? "";
                    
                    if (ofd.FileNames.Length == 1)
                    {
                        input6.Text = ofd.FileName;
                        AppendLog($"已选择 XML: {Path.GetFileName(ofd.FileName)}", Color.Green);
                    }
                    else
                    {
                        input6.Text = $"已选择 {ofd.FileNames.Length} 个文件";
                        foreach (var file in ofd.FileNames)
                        {
                            AppendLog($"已选择 XML: {Path.GetFileName(file)}", Color.Green);
                        }
                    }
                    
                    // 解析所有选中的 XML 文件
                    LoadMultipleRawprogramXml(ofd.FileNames);
                }
            }
        }

        private async void LoadMultipleRawprogramXml(string[] xmlPaths)
        {
            AppendLog("正在解析 XML 文件...", Color.Blue);
            
            string selectedXmlDir = _selectedXmlDirectory;
            
            // 在后台线程执行 IO 密集型操作
            var result = await Task.Run(() =>
            {
                var allTasks = new List<Qualcomm.Common.FlashTask>();
                string programmerPath = "";
                string[] filesToLoad = xmlPaths;
                var logMessages = new List<Tuple<string, Color>>();

                // 如果用户只选择了一个文件，且文件名包含 rawprogram，则自动搜索同目录下的其他 LUN
                if (xmlPaths.Length == 1 && Path.GetFileName(xmlPaths[0]).Contains("rawprogram"))
                {
                    string dir = Path.GetDirectoryName(xmlPaths[0]);
                    // 只匹配 rawprogram0.xml, rawprogram1.xml 等数字后缀的文件
                    // 排除 _BLANK_GPT, _WIPE_PARTITIONS, _ERASE 等特殊文件
                    var siblingFiles = Directory.GetFiles(dir, "rawprogram*.xml")
                        .Where(f => {
                            string fileName = Path.GetFileNameWithoutExtension(f).ToLower();
                            if (fileName.Contains("blank") || fileName.Contains("wipe") || fileName.Contains("erase"))
                                return false;
                            return true;
                        })
                        .OrderBy(f => {
                            string name = Path.GetFileNameWithoutExtension(f);
                            var numStr = new string(name.Where(char.IsDigit).ToArray());
                            int num;
                            return int.TryParse(numStr, out num) ? num : 999;
                        })
                        .ToArray();
                    
                    if (siblingFiles.Length > 1)
                    {
                        filesToLoad = siblingFiles;
                        logMessages.Add(Tuple.Create($"检测到多个 LUN，已自动加载同目录下的 {siblingFiles.Length} 个 XML 文件", Color.Blue));
                    }
                }
                
                foreach (var xmlPath in filesToLoad)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(xmlPath);
                        var parser = new Qualcomm.Common.RawprogramParser(dir, msg => { });
                        
                        var tasks = parser.ParseRawprogramXml(xmlPath);
                        
                        foreach (var task in tasks)
                        {
                            if (!allTasks.Any(t => t.Lun == task.Lun && t.StartSector == task.StartSector && t.Label == task.Label))
                            {
                                allTasks.Add(task);
                            }
                        }

                        logMessages.Add(Tuple.Create($"解析 {Path.GetFileName(xmlPath)}: {tasks.Count} 个任务 (当前累计: {allTasks.Count})", Color.Blue));
                        
                        if (string.IsNullOrEmpty(programmerPath))
                        {
                            programmerPath = parser.FindProgrammer();
                        }
                    }
                    catch (Exception ex)
                    {
                        logMessages.Add(Tuple.Create($"解析 {Path.GetFileName(xmlPath)} 失败: {ex.Message}", Color.Red));
                    }
                }
                
                // 检查 patch 文件
                List<string> patchFiles = new List<string>();
                string xmlDir = !string.IsNullOrEmpty(selectedXmlDir) ? selectedXmlDir : Path.GetDirectoryName(xmlPaths[0]);
                if (!string.IsNullOrEmpty(xmlDir) && Directory.Exists(xmlDir))
                {
                    patchFiles = Directory.GetFiles(xmlDir, "patch*.xml", SearchOption.TopDirectoryOnly)
                        .Where(f => {
                            string fn = Path.GetFileName(f).ToLower();
                            return !fn.Contains("blank") && !fn.Contains("wipe") && !fn.Contains("erase");
                        })
                        .OrderBy(f => {
                            string name = Path.GetFileNameWithoutExtension(f);
                            var numStr = new string(name.Where(char.IsDigit).ToArray());
                            int num;
                            return int.TryParse(numStr, out num) ? num : 999;
                        })
                        .ToList();
                }
                
                return new {
                    Tasks = allTasks,
                    ProgrammerPath = programmerPath,
                    PatchFiles = patchFiles,
                    LogMessages = logMessages
                };
            });
            
            // 回到 UI 线程输出日志
            foreach (var log in result.LogMessages)
            {
                AppendLog(log.Item1, log.Item2);
            }
            
            if (result.Tasks.Count > 0)
            {
                AppendLog($"共加载 {result.Tasks.Count} 个刷机任务", Color.Green);
                
                if (!string.IsNullOrEmpty(result.ProgrammerPath))
                {
                    input8.Text = result.ProgrammerPath;
                    AppendLog($"自动识别引导文件: {Path.GetFileName(result.ProgrammerPath)}", Color.Green);
                }
                
                if (result.PatchFiles.Count > 0)
                {
                    AppendLog($"检测到 {result.PatchFiles.Count} 个 Patch 文件: {string.Join(", ", result.PatchFiles.Select(f => Path.GetFileName(f)))}", Color.Blue);
                }
                else
                {
                    AppendLog("未检测到 Patch 文件", Color.Gray);
                }
                
                // 将所有任务填充到分区列表（这个方法内部也是异步的）
                FillPartitionListFromTasks(result.Tasks);
            }
            else
            {
                AppendLog("未在 XML 中找到有效的刷机任务", Color.Orange);
            }
        }

        private string FindMatchingPatchFile(string rawprogramPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(rawprogramPath);
                string fileName = Path.GetFileName(rawprogramPath);
                
                // rawprogram0.xml -> patch0.xml, rawprogram_unsparse.xml -> patch_unsparse.xml
                string patchName = fileName.Replace("rawprogram", "patch");
                string patchPath = Path.Combine(dir, patchName);
                
                if (File.Exists(patchPath))
                    return patchPath;
                
                // 尝试其他 patch 文件
                var patchFiles = Directory.GetFiles(dir, "patch*.xml");
                if (patchFiles.Length > 0)
                    return patchFiles[0];
                
                return "";
            }
            catch
            {
                return "";
            }
        }

        private async void FillPartitionListFromTasks(List<Qualcomm.Common.FlashTask> tasks)
        {
            // 先快速填充列表，不检查文件存在性
            listView2.BeginUpdate();
            listView2.Items.Clear();
            
            var itemsWithPaths = new List<Tuple<ListViewItem, string>>();
            
            foreach (var task in tasks)
            {
                // 转换为 PartitionInfo 用于统一处理
                var partition = new PartitionInfo
                {
                    Name = task.Label,
                    Lun = task.Lun,
                    StartSector = task.StartSector,
                    NumSectors = task.NumSectors,
                    SectorSize = task.SectorSize
                };

                // 计算地址
                long startAddress = task.StartSector * task.SectorSize;
                long endSector = task.StartSector + task.NumSectors - 1;
                long endAddress = (endSector + 1) * task.SectorSize;

                // 列顺序: 分区, LUN, 大小, 起始扇区, 结束扇区, 扇区数, 起始地址, 结束地址, 文件路径
                var item = new ListViewItem(task.Label);                           // 分区
                item.SubItems.Add(task.Lun.ToString());                             // LUN
                item.SubItems.Add(task.FormattedSize);                              // 大小
                item.SubItems.Add(task.StartSector.ToString());                     // 起始扇区
                item.SubItems.Add(endSector.ToString());                            // 结束扇区
                item.SubItems.Add(task.NumSectors.ToString());                      // 扇区数
                item.SubItems.Add($"0x{startAddress:X}");                           // 起始地址
                item.SubItems.Add($"0x{endAddress:X}");                             // 结束地址
                item.SubItems.Add(string.IsNullOrEmpty(task.FilePath) ? task.Filename : task.FilePath);  // 文件路径
                item.Tag = partition;

                // 敏感分区标记为灰色
                if (Qualcomm.Common.RawprogramParser.IsSensitivePartition(task.Label))
                    item.ForeColor = Color.Gray;

                listView2.Items.Add(item);
                
                // 记录需要检查文件的项
                if (!string.IsNullOrEmpty(task.FilePath) && !Qualcomm.Common.RawprogramParser.IsSensitivePartition(task.Label))
                {
                    itemsWithPaths.Add(Tuple.Create(item, task.FilePath));
                }
            }

            listView2.EndUpdate();
            AppendLog($"分区列表已更新: {tasks.Count} 个分区", Color.Green);
            
            // 异步检查文件存在性并勾选
            if (itemsWithPaths.Count > 0)
            {
                int checkedCount = 0;
                await Task.Run(() =>
                {
                    foreach (var tuple in itemsWithPaths)
                    {
                        if (File.Exists(tuple.Item2))
                        {
                            Interlocked.Increment(ref checkedCount);
                            // 在 UI 线程更新勾选状态
                            listView2.BeginInvoke(new Action(() => tuple.Item1.Checked = true));
                        }
                    }
                });
                AppendLog($"自动选中 {checkedCount} 个有效分区（文件存在）", Color.Green);
            }
        }

        private async Task QualcommReadPartitionTableAsync()
        {
            if (_qualcommController == null) return;

            bool skipSahara = checkbox12.Checked;

            if (!_qualcommController.IsConnected)
            {
                // 如果用户勾选了"跳过引导"，优先使用跳过引导模式连接
                // 不要自动取消勾选，尊重用户的选择
                if (skipSahara)
                {
                    // 日志在 QualcommConnectAsync 中输出，这里不重复
                    bool connected = await QualcommConnectAsync();
                    if (!connected) return;
                }
                // 检查是否可以快速重连（端口已释放但 Firehose 仍可用）
                else if (_qualcommController.CanQuickReconnect)
                {
                    AppendLog("尝试快速重连...", Color.Blue);
                    bool reconnected = await _qualcommController.QuickReconnectAsync();
                    if (reconnected)
                    {
                        AppendLog("快速重连成功", Color.Green);
                        // 已有分区数据，不需要重新读取
                        if (_qualcommController.Partitions != null && _qualcommController.Partitions.Count > 0)
                        {
                            AppendLog($"已有 {_qualcommController.Partitions.Count} 个分区数据", Color.Gray);
                            return;
                        }
                    }
                    else
                    {
                        AppendLog("快速重连失败，需要重新完整配置", Color.Orange);
                        // 不自动取消勾选，让用户决定是否使用跳过引导模式
                    }
                }
                
                // 快速重连失败或不可用，尝试完整连接
                if (!_qualcommController.IsConnected)
                {
                    bool connected = await QualcommConnectAsync();
                    if (!connected) return;
                }
            }

            await _qualcommController.ReadPartitionTableAsync();
        }

        private async Task<bool> QualcommConnectAsync()
        {
            if (_qualcommController == null) return false;

            string selectedLoader = select3.Text;
            bool isCloudMatch = selectedLoader.Contains("云端自动匹配");
            bool skipSahara = checkbox12.Checked;

            // 跳过引导模式 - 直接连接 Firehose (设备已经在 Firehose 模式)
            if (skipSahara)
            {
                AppendLog("[高通] 跳过 Sahara，直接连接 Firehose...", Color.Blue);
                return await _qualcommController.ConnectWithOptionsAsync(
                    "", _storageType, true, _authMode,
                    input9.Text?.Trim() ?? "",
                    input7.Text?.Trim() ?? ""
                );
            }

            // ========== 云端 Loader 模式（用户已选择具体 Loader）==========
            if (_selectedCloudLoaderId > 0)
            {
                return await QualcommConnectWithCloudLoaderAsync();
            }

            // 本地文件模式
            string programmerPath = input8.Text?.Trim() ?? "";

            if (!skipSahara && string.IsNullOrEmpty(programmerPath))
            {
                AppendLog("请先选择云端 Loader 或本地引导文件", Color.Orange);
                return false;
            }

            // 使用自定义连接逻辑
            return await _qualcommController.ConnectWithOptionsAsync(
                programmerPath, 
                _storageType, 
                skipSahara,
                _authMode,
                input9.Text?.Trim() ?? "",
                input7.Text?.Trim() ?? ""
            );
        }
        
        /// <summary>
        /// 使用用户选择的云端 Loader 连接
        /// </summary>
        private async Task<bool> QualcommConnectWithCloudLoaderAsync()
        {
            var cloudService = SakuraEDL.Qualcomm.Services.CloudLoaderService.Instance;
            
            // 1. 检查端口是否已选择
            if (uiComboBox1.SelectedIndex < 0 || string.IsNullOrEmpty(uiComboBox1.Text))
            {
                AppendLog("[错误] 请先选择端口", Color.Red);
                return false;
            }
            
            byte[] loaderData = null;
            byte[] digestData = null;
            byte[] signatureData = null;
            var selectedLoader = GetSelectedCloudLoader();
            
            // 2. 优先使用预下载的缓存数据 (避免 EDL 看门狗超时)
            if (_cachedLoaderId == _selectedCloudLoaderId && _cachedLoaderData != null)
            {
                AppendLog(string.Format("[云端] 使用已缓存的 Loader ({0} KB)", _cachedLoaderData.Length / 1024), Color.Green);
                loaderData = _cachedLoaderData;
                digestData = _cachedDigestData;
                signatureData = _cachedSignatureData;
            }
            else
            {
                // 等待正在进行的预下载完成
                if (_isPredownloading)
                {
                    AppendLog("[云端] 等待预下载完成...", Color.Cyan);
                    int waitCount = 0;
                    while (_isPredownloading && waitCount < 300) // 最多等待 30 秒
                    {
                        await Task.Delay(100);
                        waitCount++;
                    }
                    
                    // 预下载完成后检查缓存
                    if (_cachedLoaderId == _selectedCloudLoaderId && _cachedLoaderData != null)
                    {
                        AppendLog(string.Format("[云端] 使用预下载的 Loader ({0} KB)", _cachedLoaderData.Length / 1024), Color.Green);
                        loaderData = _cachedLoaderData;
                        digestData = _cachedDigestData;
                        signatureData = _cachedSignatureData;
                    }
                }
                
                // 如果仍然没有缓存，立即下载
                if (loaderData == null)
                {
                    AppendLog("[云端] 正在下载 Loader...", Color.Cyan);
                    
                    // 进度回调
                    Action<long, long, double> progressCallback = (downloaded, total, speed) =>
                    {
                        if (this.InvokeRequired)
                        {
                            this.BeginInvoke(new Action(() => UpdateDownloadProgress(downloaded, total, speed)));
                        }
                        else
                        {
                            UpdateDownloadProgress(downloaded, total, speed);
                        }
                    };
                    
                    loaderData = await cloudService.DownloadLoaderAsync(_selectedCloudLoaderId, progressCallback);
                    ResetDownloadSpeed();
                    
                    if (loaderData == null || loaderData.Length == 0)
                    {
                        AppendLog("[云端] 下载 Loader 失败", Color.Red);
                        return false;
                    }
                    
                    AppendLog(string.Format("[云端] 下载完成 ({0} KB)", loaderData.Length / 1024), Color.Green);
                    
                    // 如果是 VIP 模式，下载 Digest 和 Sign 文件
                    if (selectedLoader != null && selectedLoader.IsVip && selectedLoader.HasVipFiles)
                    {
                        AppendLog("[云端] 下载 VIP 验证文件...", Color.Cyan);
                        var vipFiles = await cloudService.DownloadVipFilesAsync(_selectedCloudLoaderId);
                        digestData = vipFiles.digest;
                        signatureData = vipFiles.sign;
                        
                        if (digestData == null || signatureData == null)
                        {
                            AppendLog("[云端] VIP 验证文件下载失败，将以普通模式连接", Color.Orange);
                        }
                    }
                }
            }
            
            // 检查 VIP 文件
            if (_authMode == "vip" && (selectedLoader == null || !selectedLoader.HasVipFiles))
            {
                AppendLog("[云端] 该 Loader 没有 VIP 验证文件，将以普通模式连接", Color.Orange);
            }
            
            // 4. 连接设备 (正常 Sahara 通讯流程)
            bool success = await _qualcommController.ConnectWithCloudLoaderDataAsync(
                loaderData,
                _storageType,
                _authMode,
                digestData,
                signatureData
            );
            
            // 5. 连接成功后上报设备信息 (用于统计)
            if (success)
            {
                var chipInfo = _qualcommController.ChipInfo;
                if (chipInfo != null)
                {
                    cloudService.ReportDeviceLogEx(
                        0, // Sahara version
                        chipInfo.MsmId.ToString("X8"),
                        chipInfo.PkHash ?? "",
                        "0x" + chipInfo.OemId.ToString("X4"),
                        "", // ModelId
                        chipInfo.HwIdHex ?? "",
                        chipInfo.SerialHex ?? "",
                        chipInfo.ChipName ?? "Unknown",
                        chipInfo.Vendor ?? "Unknown",
                        _storageType,
                        "success"
                    );
                }
            }
            
            return success;
        }
        
        /// <summary>
        /// 更新下载进度条和速度显示
        /// </summary>
        private void UpdateDownloadProgress(long downloaded, long total, double speedKBps)
        {
            try
            {
                // 计算百分比
                int percent = total > 0 ? (int)(downloaded * 100 / total) : 0;
                
                // 更新进度条
                if (uiProcessBar2 != null)
                {
                    uiProcessBar2.Value = Math.Min(percent, 100);
                }
                
                // 格式化速度显示并更新速度标签
                string speedText;
                if (speedKBps >= 1024)
                {
                    speedText = string.Format("{0:F1}MB/s", speedKBps / 1024);
                }
                else
                {
                    speedText = string.Format("{0:F0}KB/s", speedKBps);
                }
                
                // 更新速度标签
                if (uiLabel7 != null)
                {
                    uiLabel7.Text = "速度：" + speedText;
                }
            }
            catch { }
        }
        
        /// <summary>
        /// 重置下载速度显示
        /// </summary>
        private void ResetDownloadSpeed()
        {
            if (uiLabel7 != null)
            {
                uiLabel7.Text = "速度：0KB/s";
            }
            if (uiProcessBar2 != null)
            {
                uiProcessBar2.Value = 0;
            }
        }

        private void GeneratePartitionXml()
        {
            try
            {
                if (_qualcommController.Partitions == null || _qualcommController.Partitions.Count == 0)
                {
                    AppendLog("请先读取分区表", Color.Orange);
                    return;
                }

                // 选择保存目录，因为我们要生成多个文件 (rawprogram0.xml, patch0.xml 等)
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "选择 XML 保存目录 (将根据 LUN 生成多个 rawprogram 和 patch 文件)";
                    
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        string saveDir = fbd.SelectedPath;
                        var parser = new SakuraEDL.Qualcomm.Common.GptParser();
                        int sectorSize = _qualcommController.Partitions.Count > 0 
                            ? _qualcommController.Partitions[0].SectorSize 
                            : 4096;

                        // 1. 生成 rawprogramX.xml
                        var rawprogramDict = parser.GenerateRawprogramXmls(_qualcommController.Partitions, sectorSize);
                        foreach (var kv in rawprogramDict)
                        {
                            string fileName = Path.Combine(saveDir, $"rawprogram{kv.Key}.xml");
                            File.WriteAllText(fileName, kv.Value);
                            AppendLog($"已生成: {Path.GetFileName(fileName)}", Color.Blue);
                        }

                        // 2. 生成 patchX.xml
                        var patchDict = parser.GeneratePatchXmls(_qualcommController.Partitions, sectorSize);
                        foreach (var kv in patchDict)
                        {
                            string fileName = Path.Combine(saveDir, $"patch{kv.Key}.xml");
                            File.WriteAllText(fileName, kv.Value);
                            AppendLog($"已生成: {Path.GetFileName(fileName)}", Color.Blue);
                        }

                        // 3. 生成单个合并的 partition.xml (可选)
                        string partitionXml = parser.GeneratePartitionXml(_qualcommController.Partitions, sectorSize);
                        string pFileName = Path.Combine(saveDir, "partition.xml");
                        File.WriteAllText(pFileName, partitionXml);
                        
                        AppendLog($"XML 集合已成功保存到: {saveDir}", Color.Green);
                        
                        // 显示槽位信息
                        string currentSlot = _qualcommController.GetCurrentSlot();
                        if (currentSlot != "nonexistent")
                        {
                            AppendLog($"当前槽位: {currentSlot}", Color.Blue);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"生成 XML 失败: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// 为指定分区生成 XML 文件到指定目录 (回读时调用)
        /// </summary>
        private void GenerateXmlForPartitions(List<PartitionInfo> partitions, string saveDir)
        {
            try
            {
                if (partitions == null || partitions.Count == 0)
                {
                    return;
                }

                var parser = new SakuraEDL.Qualcomm.Common.GptParser();
                int sectorSize = partitions[0].SectorSize > 0 ? partitions[0].SectorSize : 4096;

                // 按 LUN 分组生成 rawprogram XML
                var byLun = partitions.GroupBy(p => p.Lun).ToDictionary(g => g.Key, g => g.ToList());
                
                foreach (var kv in byLun)
                {
                    int lun = kv.Key;
                    var lunPartitions = kv.Value;
                    
                    // 生成该 LUN 的 rawprogram XML
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("<?xml version=\"1.0\" ?>");
                    sb.AppendLine("<data>");
                    sb.AppendLine("  <!-- 由 SakuraEDL 生成 - 回读分区 -->");
                    
                    foreach (var p in lunPartitions)
                    {
                        // 生成 program 条目 (用于刷写回读的分区)
                        sb.AppendFormat("  <program SECTOR_SIZE_IN_BYTES=\"{0}\" file_sector_offset=\"0\" " +
                            "filename=\"{1}.img\" label=\"{1}\" num_partition_sectors=\"{2}\" " +
                            "physical_partition_number=\"{3}\" start_sector=\"{4}\" />\n",
                            sectorSize, p.Name, p.NumSectors, lun, p.StartSector);
                    }
                    
                    sb.AppendLine("</data>");
                    
                    string fileName = Path.Combine(saveDir, $"rawprogram{lun}.xml");
                    File.WriteAllText(fileName, sb.ToString());
                    AppendLog($"已生成回读分区 XML: {Path.GetFileName(fileName)}", Color.Blue);
                }
                
                AppendLog($"回读分区 XML 已保存到: {saveDir}", Color.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"生成回读 XML 失败: {ex.Message}", Color.Orange);
            }
        }

        private async Task QualcommReadPartitionAsync()
        {
            if (_qualcommController == null || !_qualcommController.CanOperatePartitions)
            {
                AppendLog("请先连接设备并读取分区表", Color.Orange);
                return;
            }

            // 获取勾选的分区或选中的分区
            var checkedItems = GetCheckedOrSelectedPartitions();
            if (checkedItems.Count == 0)
            {
                AppendLog("请选择或勾选要读取的分区", Color.Orange);
                return;
            }

            // 选择保存目录
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = checkedItems.Count == 1 ? "选择保存位置" : $"选择保存目录 (将读取 {checkedItems.Count} 个分区)";
                
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    string saveDir = fbd.SelectedPath;
                    
                    if (checkedItems.Count == 1)
                    {
                        // 单个分区
                        var partition = checkedItems[0];
                        string savePath = Path.Combine(saveDir, partition.Name + ".img");
                        await _qualcommController.ReadPartitionAsync(partition.Name, savePath);
                    }
                    else
                    {
                        // 批量读取
                        var partitionsToRead = new List<Tuple<string, string>>();
                        foreach (var p in checkedItems)
                        {
                            string savePath = Path.Combine(saveDir, p.Name + ".img");
                            partitionsToRead.Add(Tuple.Create(p.Name, savePath));
                        }
                        await _qualcommController.ReadPartitionsBatchAsync(partitionsToRead);
                    }
                    
                    // 回读完成后，如果勾选了生成XML，则为回读的分区生成 XML
                    if (checkbox11.Checked && checkedItems.Count > 0)
                    {
                        GenerateXmlForPartitions(checkedItems, saveDir);
                    }
                }
            }
        }

        private List<PartitionInfo> GetCheckedOrSelectedPartitions()
        {
            var result = new List<PartitionInfo>();
            
            // 优先使用勾选的项
            foreach (ListViewItem item in listView2.CheckedItems)
            {
                var p = item.Tag as PartitionInfo;
                if (p != null) result.Add(p);
            }
            
            // 如果没有勾选，使用选中的项
            if (result.Count == 0)
            {
                foreach (ListViewItem item in listView2.SelectedItems)
                {
                    var p = item.Tag as PartitionInfo;
                    if (p != null) result.Add(p);
                }
            }
            
            return result;
        }

        private async Task QualcommWritePartitionAsync()
        {
            if (_qualcommController == null || !_qualcommController.CanOperatePartitions)
            {
                AppendLog("请先连接设备并读取分区表", Color.Orange);
                return;
            }

            // 获取勾选的分区或选中的分区
            var checkedItems = GetCheckedOrSelectedPartitions();
            if (checkedItems.Count == 0)
            {
                AppendLog("请选择或勾选要写入的分区", Color.Orange);
                return;
            }

            if (checkedItems.Count == 1)
            {
                // 单个分区写入
                var partition = checkedItems[0];
                string filePath = "";

                // 先检查是否已有文件路径（双击选择的或从XML解析的）
                foreach (ListViewItem item in listView2.Items)
                {
                    var p = item.Tag as PartitionInfo;
                    if (p != null && p.Name == partition.Name)
                    {
                        filePath = item.SubItems.Count > 8 ? item.SubItems[8].Text : "";
                        break;
                    }
                }

                // 如果没有文件路径或文件不存在，弹出选择对话框
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    // 如果勾选了 MetaSuper 且是 super 分区，引导用户选择固件目录
                    if (checkbox18.Checked && partition.Name.Equals("super", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var fbd = new FolderBrowserDialog())
                        {
                            fbd.Description = "已开启 MetaSuper！请选择 OPLUS 固件根目录 (包含 IMAGES 和 META)";
                            if (fbd.ShowDialog() == DialogResult.OK)
                            {
                                await _qualcommController.FlashOplusSuperAsync(fbd.SelectedPath);
                                return;
                            }
                        }
                    }

                    using (var ofd = new OpenFileDialog())
                    {
                        ofd.Title = $"选择要写入 {partition.Name} 的镜像文件";
                        ofd.Filter = "镜像文件|*.img;*.bin|所有文件|*.*";

                        if (ofd.ShowDialog() != DialogResult.OK)
                            return;

                        filePath = ofd.FileName;
                    }
                }
                else
                {
                    // 即使路径存在，如果开启了 MetaSuper 且是 super 分区，也执行拆解逻辑
                    if (checkbox18.Checked && partition.Name.Equals("super", StringComparison.OrdinalIgnoreCase))
                    {
                        // 尝试从文件路径推断固件根目录 (通常镜像在 IMAGES 文件夹下)
                        string firmwareRoot = Path.GetDirectoryName(Path.GetDirectoryName(filePath));
                        if (Directory.Exists(Path.Combine(firmwareRoot, "META")))
                        {
                            await _qualcommController.FlashOplusSuperAsync(firmwareRoot);
                            return;
                        }
                        else
                        {
                            // 如果推断失败，手动选择
                            using (var fbd = new FolderBrowserDialog())
                            {
                                fbd.Description = "已开启 MetaSuper！请选择 OPLUS 固件根目录 (包含 IMAGES 和 META)";
                                if (fbd.ShowDialog() == DialogResult.OK)
                                {
                                    await _qualcommController.FlashOplusSuperAsync(fbd.SelectedPath);
                                    return;
                                }
                            }
                        }
                    }
                }

                // 执行写入
                AppendLog($"开始写入 {Path.GetFileName(filePath)} -> {partition.Name}", Color.Blue);
                bool success = await _qualcommController.WritePartitionAsync(partition.Name, filePath);
                
                if (success && checkbox15.Checked)
                {
                    AppendLog("写入完成，自动重启设备...", Color.Blue);
                    await _qualcommController.RebootToSystemAsync();
                }
            }
            else
            {
                // 批量写入 - 从 XML 解析的任务中获取文件路径
                AppendLog("正在检查文件...", Color.Gray);
                
                // 收集所有勾选项的信息
                var checkedPartitions = new List<Tuple<PartitionInfo, string>>();
                foreach (ListViewItem item in listView2.CheckedItems)
                {
                    var partition = item.Tag as PartitionInfo;
                    if (partition == null) continue;
                    string filePath = item.SubItems.Count > 8 ? item.SubItems[8].Text : "";
                    checkedPartitions.Add(Tuple.Create(partition, filePath));
                }
                
                string inputXmlPath = input6.Text?.Trim() ?? "";
                
                // 在后台线程检查文件存在性
                var fileCheckResult = await Task.Run(() =>
                {
                    var partitionsToWrite = new List<Tuple<string, string, int, long>>();
                    var missingFiles = new List<string>();
                    
                    string xmlDir = "";
                    try
                    {
                        if (!string.IsNullOrEmpty(inputXmlPath))
                            xmlDir = Path.GetDirectoryName(inputXmlPath) ?? "";
                    }
                    catch { }
                    
                    foreach (var item in checkedPartitions)
                    {
                        var partition = item.Item1;
                        string filePath = item.Item2;
                        
                        // 尝试从当前目录或 XML 目录查找文件
                        if (!string.IsNullOrEmpty(filePath) && !File.Exists(filePath))
                        {
                            // 尝试从 XML 目录查找
                            if (!string.IsNullOrEmpty(xmlDir))
                            {
                                string altPath = Path.Combine(xmlDir, Path.GetFileName(filePath));
                                if (File.Exists(altPath))
                                    filePath = altPath;
                            }
                        }

                        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        {
                            partitionsToWrite.Add(Tuple.Create(partition.Name, filePath, partition.Lun, partition.StartSector));
                        }
                        else
                        {
                            missingFiles.Add(partition.Name);
                        }
                    }
                    
                    return new { PartitionsToWrite = partitionsToWrite, MissingFiles = missingFiles };
                });
                
                var partitionsToWrite = fileCheckResult.PartitionsToWrite;
                var missingFiles = fileCheckResult.MissingFiles;

                if (missingFiles.Count > 0)
                {
                    AppendLog($"以下分区缺少镜像文件: {string.Join(", ", missingFiles)}", Color.Orange);
                }

                // 检查 MetaSuper 功能
                // 1. 用户勾选了 checkbox18
                // 2. 或者自动检测到 META 目录中有 super_def.*.json
                bool metaSuperEnabled = checkbox18.Checked;
                string metaSuperFirmwareRoot = null;
                
                // 尝试从 XML 目录推断固件根目录
                string xmlDir = _selectedXmlDirectory;
                if (string.IsNullOrEmpty(xmlDir) && !string.IsNullOrEmpty(inputXmlPath))
                {
                    try { xmlDir = Path.GetDirectoryName(inputXmlPath); } catch { }
                }
                
                if (!string.IsNullOrEmpty(xmlDir))
                {
                    // OPLUS 固件结构: 根目录/IMAGES/rawprogram*.xml
                    // 尝试找到包含 META 文件夹的固件根目录
                    string possibleRoot = Path.GetDirectoryName(xmlDir); // IMAGES 的父目录
                    
                    // 检查是否有 META/super_def.*.json
                    string metaDir = null;
                    if (Directory.Exists(Path.Combine(possibleRoot, "META")))
                    {
                        metaDir = Path.Combine(possibleRoot, "META");
                        metaSuperFirmwareRoot = possibleRoot;
                    }
                    else if (Directory.Exists(Path.Combine(xmlDir, "META")))
                    {
                        metaDir = Path.Combine(xmlDir, "META");
                        metaSuperFirmwareRoot = xmlDir;
                    }
                    
                    // 自动检测 super_def.*.json 以启用 MetaSuper
                    if (!string.IsNullOrEmpty(metaDir))
                    {
                        try
                        {
                            var superDefFiles = Directory.GetFiles(metaDir, "super_def.*.json");
                            if (superDefFiles.Length > 0 && !metaSuperEnabled)
                            {
                                metaSuperEnabled = true;  // 自动启用
                                AppendLog($"[MetaSuper] 自动检测到 {Path.GetFileName(superDefFiles[0])}，启用 Super 逻辑分区刷写", Color.Blue);
                            }
                        }
                        catch { }
                    }
                    
                    if (metaSuperEnabled && !string.IsNullOrEmpty(metaSuperFirmwareRoot))
                    {
                        AppendLog($"[MetaSuper] 固件目录: {metaSuperFirmwareRoot}", Color.Gray);
                    }
                }
                
                // 从批量写入列表中移除 super 分区（如果有的话，将用 MetaSuper 方式刷写）
                if (metaSuperEnabled)
                {
                    partitionsToWrite.RemoveAll(p => 
                        p.Item1.Equals("super", StringComparison.OrdinalIgnoreCase));
                }
                
                if (partitionsToWrite.Count > 0)
                {
                    
                    // 在后台线程搜索 Patch 文件，避免 UI 卡住
                    AppendLog("正在搜索 Patch 文件...", Color.Gray);
                    
                    string selectedXmlDir = _selectedXmlDirectory;
                    string xmlPathForPatch = inputXmlPath; // 复用前面已获取的变量
                    
                    var patchSearchResult = await Task.Run(() =>
                    {
                        var patchFiles = new List<string>();
                        var logMessages = new List<Tuple<string, Color>>();
                        
                        // 优先使用存储的 XML 目录，如果为空则尝试从 input6.Text 解析
                        string xmlDir = selectedXmlDir;
                        if (string.IsNullOrEmpty(xmlDir))
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(xmlPathForPatch) && File.Exists(xmlPathForPatch))
                                {
                                    xmlDir = Path.GetDirectoryName(xmlPathForPatch) ?? "";
                                }
                            }
                            catch (ArgumentException) { }
                        }
                        
                        if (!string.IsNullOrEmpty(xmlDir) && Directory.Exists(xmlDir))
                        {
                            logMessages.Add(Tuple.Create($"正在目录中搜索 Patch 文件: {xmlDir}", Color.Gray));
                            
                            // 1. 先搜索当前目录
                            try
                            {
                                var sameDir = Directory.GetFiles(xmlDir, "patch*.xml", SearchOption.TopDirectoryOnly)
                                    .Where(f => {
                                        string fn = Path.GetFileName(f).ToLower();
                                        return !fn.Contains("blank") && !fn.Contains("wipe") && !fn.Contains("erase");
                                    })
                                    .ToList();
                                patchFiles.AddRange(sameDir);
                            }
                            catch { }
                            
                            // 2. 搜索子目录
                            if (patchFiles.Count == 0)
                            {
                                try
                                {
                                    var subDirs = Directory.GetFiles(xmlDir, "patch*.xml", SearchOption.AllDirectories)
                                        .Where(f => {
                                            string fn = Path.GetFileName(f).ToLower();
                                            return !fn.Contains("blank") && !fn.Contains("wipe") && !fn.Contains("erase");
                                        })
                                        .ToList();
                                    patchFiles.AddRange(subDirs);
                                }
                                catch { }
                            }

                            // 3. 搜索父目录
                            if (patchFiles.Count == 0)
                            {
                                try
                                {
                                    string parentDir = Path.GetDirectoryName(xmlDir);
                                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                                    {
                                        logMessages.Add(Tuple.Create($"当前目录未找到，搜索父目录: {parentDir}", Color.Gray));
                                        var parentPatches = Directory.GetFiles(parentDir, "patch*.xml", SearchOption.AllDirectories)
                                            .Where(f => {
                                                string fn = Path.GetFileName(f).ToLower();
                                                return !fn.Contains("blank") && !fn.Contains("wipe") && !fn.Contains("erase");
                                            })
                                            .ToList();
                                        patchFiles.AddRange(parentPatches);
                                    }
                                }
                                catch { }
                            }
                            
                            // 排序 patch 文件
                            patchFiles = patchFiles.Distinct().OrderBy(f => {
                                string name = Path.GetFileNameWithoutExtension(f);
                                var numStr = new string(name.Where(char.IsDigit).ToArray());
                                int num;
                                return int.TryParse(numStr, out num) ? num : 999;
                            }).ToList();
                        }
                        else
                        {
                            logMessages.Add(Tuple.Create($"无法获取 XML 目录路径", Color.Orange));
                        }
                        
                        return new { PatchFiles = patchFiles, LogMessages = logMessages };
                    });
                    
                    // 输出日志
                    foreach (var log in patchSearchResult.LogMessages)
                    {
                        AppendLog(log.Item1, log.Item2);
                    }

                    if (patchSearchResult.PatchFiles.Count > 0)
                    {
                        AppendLog($"检测到 {patchSearchResult.PatchFiles.Count} 个 Patch 文件:", Color.Blue);
                        foreach (var pf in patchSearchResult.PatchFiles)
                        {
                            AppendLog($"  - {Path.GetFileName(pf)}", Color.Gray);
                        }
                    }
                    else
                    {
                        AppendLog("未检测到 Patch 文件，跳过补丁步骤", Color.Gray);
                    }

                    // UFS 设备需要激活启动 LUN，eMMC 只有 LUN0 不需要
                    bool activateBootLun = _storageType == "ufs";
                    if (activateBootLun)
                    {
                        AppendLog("UFS 设备: 写入完成后将回读 GPT 并激活对应启动 LUN", Color.Blue);
                    }
                    else
                    {
                        AppendLog("eMMC 设备: 仅 LUN0，无需激活启动分区", Color.Gray);
                    }

                    int success = 0;
                    
                    // 如果有普通分区需要写入 (MetaSuper 任务会在批量写入中按扇区位置自动合并)
                    if (partitionsToWrite.Count > 0 || metaSuperEnabled)
                    {
                        // 如果启用 MetaSuper 但没找到固件目录，弹出选择对话框
                        if (metaSuperEnabled && string.IsNullOrEmpty(metaSuperFirmwareRoot))
                        {
                            using (var fbd = new FolderBrowserDialog())
                            {
                                fbd.Description = "请选择 OPLUS 固件根目录 (包含 IMAGES 和 META)";
                                if (fbd.ShowDialog() == DialogResult.OK)
                                {
                                    metaSuperFirmwareRoot = fbd.SelectedPath;
                                }
                            }
                        }
                        
                        // 批量写入 (包含 MetaSuper 任务，按物理扇区位置统一排序执行)
                        success = await _qualcommController.WritePartitionsBatchAsync(
                            partitionsToWrite, 
                            patchSearchResult.PatchFiles, 
                            activateBootLun,
                            metaSuperEnabled ? metaSuperFirmwareRoot : null);
                    }
                    
                    if (success > 0 && checkbox15.Checked)
                    {
                        AppendLog("批量写入完成，自动重启设备...", Color.Blue);
                        await _qualcommController.RebootToSystemAsync();
                    }
                }
                else
                {
                    AppendLog("没有找到有效的镜像文件，请确保 XML 解析正确或手动选择文件", Color.Orange);
                }
            }
        }

        private async Task QualcommErasePartitionAsync()
        {
            if (_qualcommController == null || !_qualcommController.CanOperatePartitions)
            {
                AppendLog("请先连接设备并读取分区表", Color.Orange);
                return;
            }

            // 获取勾选的分区或选中的分区
            var checkedItems = GetCheckedOrSelectedPartitions();
            if (checkedItems.Count == 0)
            {
                AppendLog("请选择或勾选要擦除的分区", Color.Orange);
                return;
            }

            // 擦除确认
            string message = checkedItems.Count == 1
                ? $"确定要擦除分区 {checkedItems[0].Name} 吗？\n\n此操作不可逆！"
                : $"确定要擦除 {checkedItems.Count} 个分区吗？\n\n分区: {string.Join(", ", checkedItems.ConvertAll(p => p.Name))}\n\n此操作不可逆！";

            var result = MessageBox.Show(
                message,
                "确认擦除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                if (checkedItems.Count == 1)
                {
                    // 单个擦除
                    bool success = await _qualcommController.ErasePartitionAsync(checkedItems[0].Name);
                    
                    if (success && checkbox15.Checked)
                    {
                        AppendLog("擦除完成，自动重启设备...", Color.Blue);
                        await _qualcommController.RebootToSystemAsync();
                    }
                }
                else
                {
                    // 批量擦除
                    var partitionNames = checkedItems.ConvertAll(p => p.Name);
                    int success = await _qualcommController.ErasePartitionsBatchAsync(partitionNames);
                    
                    if (success > 0 && checkbox15.Checked)
                    {
                        AppendLog("批量擦除完成，自动重启设备...", Color.Blue);
                        await _qualcommController.RebootToSystemAsync();
                    }
                }
            }
        }

        private async Task QualcommSwitchSlotAsync()
        {
            if (_qualcommController == null || !_qualcommController.IsConnected)
            {
                AppendLog("请先连接设备", Color.Orange);
                return;
            }

            // 询问槽位
            var result = MessageBox.Show("切换到槽位 A？\n\n选择 是 切换到 A\n选择 否 切换到 B",
                "切换槽位", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
                await _qualcommController.SwitchSlotAsync("a");
            else if (result == DialogResult.No)
                await _qualcommController.SwitchSlotAsync("b");
        }

        private async Task QualcommSetBootLunAsync()
        {
            if (_qualcommController == null || !_qualcommController.IsConnected)
            {
                AppendLog("请先连接设备", Color.Orange);
                return;
            }

            // UFS: 0, 1, 2, 4(Boot A), 5(Boot B)
            // eMMC: 0
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "输入 LUN 编号:\n\nUFS 支持: 0, 1, 2, 4(Boot A), 5(Boot B)\neMMC 仅支持: 0",
                "激活 LUN", "0");

            int lun;
            if (int.TryParse(input, out lun))
            {
                await _qualcommController.SetBootLunAsync(lun);
            }
        }

        private void StopCurrentOperation()
        {
            bool hasCancelled = false;
            
            // 获取当前标签页
            int currentTab = tabs1.SelectedIndex;
            
            // tabPage2 (index 1) = 高通
            // tabPage4 (index 3) = MTK
            // tabPage5 (index 4) = 展讯
            // tabPage1 (index 0) / tabPage3 (index 2) = Fastboot
            
            // 根据当前标签页取消对应操作
            switch (currentTab)
            {
                case 1: // 高通
                    if (_qualcommController != null && _qualcommController.HasPendingOperation)
                    {
                        _qualcommController.CancelOperation();
                        AppendLog("[高通] 操作已取消", Color.Orange);
                        hasCancelled = true;
                    }
                    break;
                    
                case 3: // MTK
                    if (MtkHasPendingOperation)
                    {
                        MtkCancelOperation();
                        hasCancelled = true;
                    }
                    break;
                    
                case 4: // 展讯
                    if (_spreadtrumController != null)
                    {
                        _spreadtrumController.CancelOperation();
                        hasCancelled = true;
                    }
                    break;
                    
                case 0: // Fastboot (tabPage1)
                case 2: // Fastboot (tabPage3)
                    if (_fastbootController != null)
                    {
                        try
                        {
                            _fastbootController.CancelOperation();
                            AppendLog("[Fastboot] 操作已取消", Color.Orange);
                            hasCancelled = true;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"取消 Fastboot 操作异常: {ex.Message}");
                        }
                    }
                    break;
            }

            if (hasCancelled)
            {
                // 重置进度条
                uiProcessBar1.Value = 0;
                uiProcessBar2.Value = 0;
                progress1.Value = 0;
                progress2.Value = 0;
            }
            else
            {
                AppendLog("当前没有进行中的操作", Color.Gray);
            }
        }

        private void QualcommSelectAllPartitions(bool selectAll)
        {
            if (listView2.Items.Count == 0) return;

            listView2.BeginUpdate();
            foreach (ListViewItem item in listView2.Items)
            {
                item.Checked = selectAll;
            }
            listView2.EndUpdate();

            AppendLog(selectAll ? "已全选分区" : "已取消全选", Color.Blue);
        }

        /// <summary>
        /// 双击分区列表项，选择对应的镜像文件
        /// </summary>
        private void QualcommPartitionDoubleClick()
        {
            if (listView2.SelectedItems.Count == 0) return;

            var item = listView2.SelectedItems[0];
            var partition = item.Tag as PartitionInfo;
            if (partition == null)
            {
                // 如果没有 Tag，尝试从名称获取
                string partitionName = item.Text;
                if (string.IsNullOrEmpty(partitionName)) return;

                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = $"选择 {partitionName} 分区的镜像文件";
                    ofd.Filter = $"镜像文件|{partitionName}.img;{partitionName}.bin;*.img;*.bin|所有文件|*.*";

                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        // 更新文件路径列 (最后一列)
                        int lastCol = item.SubItems.Count - 1;
                        if (lastCol >= 0)
                        {
                            item.SubItems[lastCol].Text = ofd.FileName;
                            item.Checked = true; // 自动勾选
                            AppendLog($"已为分区 {partitionName} 选择文件: {Path.GetFileName(ofd.FileName)}", Color.Blue);
                        }
                    }
                }
                return;
            }

            // 有 PartitionInfo 的情况
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = $"选择 {partition.Name} 分区的镜像文件";
                ofd.Filter = $"镜像文件|{partition.Name}.img;{partition.Name}.bin;*.img;*.bin|所有文件|*.*";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    // 更新文件路径列 (最后一列)
                    int lastCol = item.SubItems.Count - 1;
                    if (lastCol >= 0)
                    {
                        item.SubItems[lastCol].Text = ofd.FileName;
                        item.Checked = true; // 自动勾选
                        AppendLog($"已为分区 {partition.Name} 选择文件: {Path.GetFileName(ofd.FileName)}", Color.Blue);
                    }
                }
            }
        }

        private string _lastSearchKeyword = "";
        private List<ListViewItem> _searchMatches = new List<ListViewItem>();
        private int _currentMatchIndex = 0;
        private bool _isSelectingFromDropdown = false;

        private void QualcommSearchPartition()
        {
            // 如果是从下拉选择触发的，直接定位不更新下拉
            if (_isSelectingFromDropdown)
            {
                _isSelectingFromDropdown = false;
                string selectedName = select4.Text?.Trim()?.ToLower();
                if (!string.IsNullOrEmpty(selectedName))
                {
                    LocatePartitionByName(selectedName);
                }
                return;
            }

            string keyword = select4.Text?.Trim()?.ToLower();
            
            // 如果搜索框为空，重置所有高亮
            if (string.IsNullOrEmpty(keyword))
            {
                ResetPartitionHighlights();
                _lastSearchKeyword = "";
                _searchMatches.Clear();
                _currentMatchIndex = 0;
                return;
            }
            
            // 如果关键词相同，跳转到下一个匹配项
            if (keyword == _lastSearchKeyword && _searchMatches.Count > 1)
            {
                JumpToNextMatch();
                return;
            }
            
            _lastSearchKeyword = keyword;
            _searchMatches.Clear();
            _currentMatchIndex = 0;
            
            // 收集匹配的分区名称用于下拉建议
            var suggestions = new List<string>();
            
            listView2.BeginUpdate();
            
                foreach (ListViewItem item in listView2.Items)
                {
                string partitionName = item.Text?.ToLower() ?? "";
                string originalName = item.Text ?? "";
                bool isMatch = partitionName.Contains(keyword);
                
                if (isMatch)
                {
                    // 精确匹配用深色，模糊匹配用浅色
                    item.BackColor = (partitionName == keyword) ? Color.Gold : Color.LightYellow;
                    _searchMatches.Add(item);
                    
                    // 添加到下拉建议（最多显示10个）
                    if (suggestions.Count < 10)
                    {
                        suggestions.Add(originalName);
                    }
                }
                else
                {
                    item.BackColor = Color.Transparent;
                }
            }
            
            listView2.EndUpdate();
            
            // 更新下拉建议列表
            UpdateSearchSuggestions(suggestions);
            
            // 滚动到第一个匹配项
            if (_searchMatches.Count > 0)
            {
                _searchMatches[0].Selected = true;
                _searchMatches[0].EnsureVisible();
                
                // 显示匹配数量（不重复打日志）
                if (_searchMatches.Count > 1)
                {
                    // 在状态栏或其他地方显示，避免刷屏
                }
            }
            else if (keyword.Length >= 2)
            {
                // 只有输入2个以上字符才提示未找到
                AppendLog($"未找到分区: {keyword}", Color.Orange);
            }
        }

        private void JumpToNextMatch()
        {
            if (_searchMatches.Count == 0) return;
            
            // 取消当前选中
            if (_currentMatchIndex < _searchMatches.Count)
            {
                _searchMatches[_currentMatchIndex].Selected = false;
            }
            
            // 跳转到下一个
            _currentMatchIndex = (_currentMatchIndex + 1) % _searchMatches.Count;
            _searchMatches[_currentMatchIndex].Selected = true;
            _searchMatches[_currentMatchIndex].EnsureVisible();
        }

        private void ResetPartitionHighlights()
        {
            listView2.BeginUpdate();
            foreach (ListViewItem item in listView2.Items)
            {
                item.BackColor = Color.Transparent;
            }
            listView2.EndUpdate();
        }

        private void UpdateSearchSuggestions(List<string> suggestions)
        {
            // 保存当前输入的文本
            string currentText = select4.Text;
            
            // 更新下拉项
            select4.Items.Clear();
            foreach (var name in suggestions)
            {
                select4.Items.Add(name);
            }
            
            // 恢复输入的文本（防止被清空）
            select4.Text = currentText;
        }

        private void LocatePartitionByName(string partitionName)
        {
            ResetPartitionHighlights();
            
            foreach (ListViewItem item in listView2.Items)
            {
                if (item.Text?.ToLower() == partitionName)
                    {
                    item.BackColor = Color.Gold;
                        item.Selected = true;
                        item.EnsureVisible();
                        listView2.Focus();
                        break;
                }
            }
        }

        #endregion
        // 窗体关闭时清理资源
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            // 停止端口刷新定时器
            if (_portRefreshTimer != null)
            {
                _portRefreshTimer.Stop();
                _portRefreshTimer.Dispose();
                _portRefreshTimer = null;
            }

            // 释放高通控制器
            if (_qualcommController != null)
            {
                try { _qualcommController.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Form1] 释放高通控制器异常: {ex.Message}"); }
                _qualcommController = null;
            }

            // 释放展讯控制器
            if (_spreadtrumController != null)
            {
                try { _spreadtrumController.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Form1] 释放展讯控制器异常: {ex.Message}"); }
                _spreadtrumController = null;
            }
            
            // 释放 Fastboot 控制器
            if (_fastbootController != null)
            {
                try { _fastbootController.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Form1] 释放 Fastboot 控制器异常: {ex.Message}"); }
                _fastbootController = null;
            }

            // 释放联发科模块
            CleanupMediaTekModule();

            // 释放背景图片
            if (this.BackgroundImage != null)
            {
                this.BackgroundImage.Dispose();
                this.BackgroundImage = null;
            }

            // 清空预览
            ClearImagePreview();

            // 释放看门狗管理器
            SakuraEDL.Common.WatchdogManager.DisposeAll();

            // 优化的垃圾回收
            GC.Collect(0, GCCollectionMode.Optimized);
        }
        
        /// <summary>
        /// 小米授权令牌事件处理 - 弹窗显示令牌供用户复制
        /// </summary>
        private void OnXiaomiAuthTokenRequired(string token)
        {
            // 确保在 UI 线程上执行
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<string>(OnXiaomiAuthTokenRequired), token);
                return;
            }
            
            // 创建弹窗
            using (var form = new Form())
            {
                form.Text = "小米授权令牌";
                form.Size = new Size(500, 220);
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                
                // 说明文字
                var label = new Label
                {
                    Text = "内置签名验证失败，请复制以下令牌到小米授权服务获取签名：",
                    Location = new Point(15, 15),
                    Size = new Size(460, 20),
                    Font = new Font("Microsoft YaHei UI", 9F)
                };
                form.Controls.Add(label);
                
                // 令牌文本框
                var textBox = new TextBox
                {
                    Text = token,
                    Location = new Point(15, 45),
                    Size = new Size(455, 60),
                    Multiline = true,
                    ReadOnly = true,
                    Font = new Font("Consolas", 9F),
                    ScrollBars = ScrollBars.Vertical
                };
                form.Controls.Add(textBox);
                
                // 复制按钮
                var copyButton = new Button
                {
                    Text = "复制令牌",
                    Location = new Point(150, 115),
                    Size = new Size(90, 30),
                    Font = new Font("Microsoft YaHei UI", 9F)
                };
                copyButton.Click += (s, e) =>
                {
                    try
                    {
                        Clipboard.SetText(token);
                        MessageBox.Show("令牌已复制到剪贴板", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"复制失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                form.Controls.Add(copyButton);
                
                // 关闭按钮
                var closeButton = new Button
                {
                    Text = "关闭",
                    Location = new Point(260, 115),
                    Size = new Size(90, 30),
                    Font = new Font("Microsoft YaHei UI", 9F),
                    DialogResult = DialogResult.Cancel
                };
                form.Controls.Add(closeButton);
                
                // 提示信息
                var tipLabel = new Label
                {
                    Text = "提示: 令牌格式为 VQ 开头的 Base64 字符串",
                    Location = new Point(15, 155),
                    Size = new Size(460, 20),
                    ForeColor = Color.Gray,
                    Font = new Font("Microsoft YaHei UI", 8F)
                };
                form.Controls.Add(tipLabel);
                
                form.ShowDialog(this);
            }
        }

        private void InitializeUrlComboBox()
        {
            // 只保留已验证可用的API
            string[] defaultUrls = new[]
            {
                "https://img.xjh.me/random_img.php?return=302",
                "https://www.dmoe.cc/random.php",
                "https://www.loliapi.com/acg/",
                "https://t.alcy.cc/moe"
            };

            uiComboBox3.Items.Clear();
            foreach (string url in defaultUrls)
            {
                uiComboBox3.Items.Add(url);
            }

            if (uiComboBox3.Items.Count > 0)
            {
                uiComboBox3.SelectedIndex = 0;
            }
        }

        private void InitializeImagePreview()
        {
            // 清空预览控件
            ClearImagePreview();

            // 设置预览控件属性
            pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBox1.BorderStyle = BorderStyle.FixedSingle;
            pictureBox1.BackColor = Color.Black;

        }

        private void SaveOriginalPositions()
        {
            try
            {
                // 保存原始位置和大小
                originalinput6Location = input6.Location;
                originalbutton4Location = button4.Location;
                originalcheckbox13Location = checkbox13.Location;
                originalinput7Location = input7.Location;
                originalinput9Location = input9.Location;
                originallistView2Location = listView2.Location;
                originallistView2Size = listView2.Size;
                originaluiGroupBox4Location =uiGroupBox4.Location;
                originaluiGroupBox4Size =uiGroupBox4.Size;

            }
            catch (Exception ex)
            {
                AppendLog($"保存原始位置失败: {ex.Message}", Color.Red);
            }
        }

        // 日志计数器，用于限制条目数量
        private int _logEntryCount = 0;
        private readonly object _logLock = new object();
        private System.Collections.Concurrent.ConcurrentQueue<string> _logFileQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private volatile bool _logFileWriterRunning = false;
        private DateTime _lastLogFlush = DateTime.MinValue;

        private void AppendLog(string message, Color? color = null)
        {
            message = LanguageManager.TranslateLegacyText(message);

            // 先将日志排队到后台线程写入文件（避免阻塞 UI）
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logFileQueue.Enqueue(logLine);
            
            // 启动后台日志写入（如果尚未运行）
            if (!_logFileWriterRunning)
            {
                _logFileWriterRunning = true;
                Task.Run(async () => await FlushLogFileAsync());
            }

            if (uiRichTextBox1.InvokeRequired)
            {
                uiRichTextBox1.BeginInvoke(new Action<string, Color?>(AppendLogToUI), message, color);
                return;
            }

            AppendLogToUI(message, color);
        }
        
        private async Task FlushLogFileAsync()
        {
            try
            {
                while (!_logFileQueue.IsEmpty || (DateTime.Now - _lastLogFlush).TotalMilliseconds < 500)
                {
                    var lines = new System.Text.StringBuilder();
                    string line;
                    while (_logFileQueue.TryDequeue(out line))
                    {
                        lines.AppendLine(line);
                    }
                    
                    if (lines.Length > 0)
                    {
                        try
                        {
                            await Task.Run(() => File.AppendAllText(logFilePath, lines.ToString()));
                        }
                        catch { }
                        _lastLogFlush = DateTime.Now;
                    }
                    
                    await Task.Delay(100); // 批量写入，减少 IO 操作
                }
            }
            finally
            {
                _logFileWriterRunning = false;
            }
        }
        
        private void AppendLogToUI(string message, Color? color)
        {
            // 白色背景下的颜色映射 (使颜色更清晰)
            Color logColor = MapLogColor(color ?? Color.Black);

            // 检查并限制日志条目数量 (减少内存占用)
            int maxEntries = Common.PerformanceConfig.MaxLogEntries;
            bool needsCleanup = false;
            
            lock (_logLock)
            {
                _logEntryCount++;
                // 只在超过阈值的 1.5 倍时才清理，减少清理频率
                if (_logEntryCount > maxEntries * 1.5)
                {
                    needsCleanup = true;
                }
            }

            // 显示到 UI (减少重绘)
            uiRichTextBox1.SuspendLayout();
            try
            {
                // 清理日志 - 使用更高效的方式
                if (needsCleanup)
                {
                    try
                    {
                        // 直接删除前半部分文本，避免 Split/Join
                        int textLen = uiRichTextBox1.TextLength;
                        if (textLen > 0)
                        {
                            // 找到大约一半位置的换行符
                            int halfPos = textLen / 2;
                            int newlinePos = uiRichTextBox1.Text.IndexOf('\n', halfPos);
                            if (newlinePos > 0 && newlinePos < textLen - 1)
                            {
                                uiRichTextBox1.Select(0, newlinePos + 1);
                                uiRichTextBox1.SelectedText = "";
                                lock (_logLock)
                                {
                                    _logEntryCount = _logEntryCount / 2;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"日志清理异常: {ex.Message}");
                    }
                }
                
                uiRichTextBox1.SelectionStart = uiRichTextBox1.TextLength;
                uiRichTextBox1.SelectionColor = logColor;
                uiRichTextBox1.AppendText(message + "\n");
                uiRichTextBox1.ScrollToCaret();
            }
            finally
            {
                uiRichTextBox1.ResumeLayout();
            }
        }

        /// <summary>
        /// 将颜色映射为适合白色背景的版本 (更深更清晰)
        /// </summary>
        private Color MapLogColor(Color originalColor)
        {
            // 白色背景配色方案 - 使用更深的颜色
            if (originalColor == Color.White) return Color.Black;
            if (originalColor == Color.Blue) return Color.FromArgb(0, 80, 180);      // 深蓝
            if (originalColor == Color.Gray) return Color.FromArgb(100, 100, 100);   // 深灰
            if (originalColor == Color.Green) return Color.FromArgb(0, 140, 0);      // 深绿
            if (originalColor == Color.Red) return Color.FromArgb(200, 0, 0);        // 深红
            if (originalColor == Color.Orange) return Color.FromArgb(200, 120, 0);   // 深橙
            if (originalColor == Color.LimeGreen) return Color.FromArgb(0, 160, 0);  // 深黄绿
            if (originalColor == Color.Cyan) return Color.FromArgb(0, 140, 160);     // 深青
            if (originalColor == Color.Yellow) return Color.FromArgb(180, 140, 0);   // 深黄
            if (originalColor == Color.Magenta) return Color.FromArgb(160, 0, 160);  // 深紫
            
            // 其他颜色保持不变
            return originalColor;
        }

        /// <summary>
        /// 详细调试日志 - 只写入文件，不显示在 UI
        /// 使用批量写入队列，避免频繁 IO 导致卡顿
        /// </summary>
        private void AppendLogDetail(string message)
        {
            // 复用日志队列，避免频繁的同步 IO
            string logLine = $"[{DateTime.Now:HH:mm:ss}] [DEBUG] {message}";
            _logFileQueue.Enqueue(logLine);
            
            // 启动后台日志写入（如果尚未运行）
            if (!_logFileWriterRunning)
            {
                _logFileWriterRunning = true;
                Task.Run(async () => await FlushLogFileAsync());
            }
        }

        /// <summary>
        /// 初始化日志系统
        /// </summary>
        private void InitializeLogSystem()
        {
            try
            {
                // 使用应用程序目录下的 Logs 文件夹
                string logFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists(logFolderPath))
                {
                    Directory.CreateDirectory(logFolderPath);
                }

                // 清理 7 天前的旧日志
                CleanOldLogs(logFolderPath, 7);

                string logFileName = $"{DateTime.Now:yyyy-MM-dd_HH.mm.ss}_log.txt";
                logFilePath = Path.Combine(logFolderPath, logFileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"日志初始化失败: {ex.Message}");
                // 日志初始化失败时使用临时目录
                logFilePath = Path.Combine(Path.GetTempPath(), $"SakuraEDL_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }
        }

        /// <summary>
        /// 清理指定天数之前的旧日志
        /// </summary>
        private void CleanOldLogs(string logFolder, int daysToKeep)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-daysToKeep);
                var oldFiles = Directory.GetFiles(logFolder, "*_log.txt")
                    .Where(f => File.GetCreationTime(f) < cutoff)
                    .ToArray();

                foreach (var file in oldFiles)
                {
                    try { File.Delete(file); } catch { /* 删除旧日志失败可忽略 */ }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理旧日志异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 写入日志文件头部信息
        /// </summary>
        private void WriteLogHeader(string sysInfo)
        {
            try
            {
                var header = new StringBuilder();
                header.AppendLine($"启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                header.AppendLine($"系统: {sysInfo}");
                header.AppendLine($"版本: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
                header.AppendLine();

                File.WriteAllText(logFilePath, header.ToString());
            }
            catch { /* 日志头写入失败可忽略 */ }
        }

        /// <summary>
        /// 加载一言 (Hitokoto) API
        /// </summary>
        private async Task LoadHitokotoAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetStringAsync("https://v1.hitokoto.cn/?c=a&c=b&c=c&c=d&c=k&encode=json");
                    
                    // 使用正则解析 JSON (避免引入额外依赖)
                    var hitokotoMatch = System.Text.RegularExpressions.Regex.Match(response, "\"hitokoto\"\\s*:\\s*\"([^\"]+)\"");
                    var fromMatch = System.Text.RegularExpressions.Regex.Match(response, "\"from\"\\s*:\\s*\"([^\"]+)\"");
                    
                    string hitokoto = hitokotoMatch.Success ? hitokotoMatch.Groups[1].Value : null;
                    string from = fromMatch.Success ? fromMatch.Groups[1].Value : null;
                    
                    // 处理转义字符
                    if (hitokoto != null)
                    {
                        hitokoto = hitokoto.Replace("\\n", " ").Replace("\\r", "").Replace("\\\"", "\"");
                    }
                    
                    // 构建显示文本
                    string displayText = hitokoto ?? "";
                    
                    // 限制显示长度
                    if (displayText.Length > 35)
                    {
                        displayText = displayText.Substring(0, 32) + "...";
                    }
                    
                    // 添加来源
                    if (!string.IsNullOrEmpty(from) && displayText.Length + from.Length < 45)
                    {
                        displayText = $"「{displayText}」—— {from}";
                    }
                    else
                    {
                        displayText = $"「{displayText}」";
                    }
                    
                    // 更新 UI
                    if (InvokeRequired)
                    {
                        Invoke(new Action(() => uiLabel1.Text = displayText));
                    }
                    else
                    {
                        uiLabel1.Text = displayText;
                    }
                }
            }
            catch
            {
                // 一言加载失败，使用默认文本
                string defaultText = "「愿你有前进一寸的勇气，也有后退一尺的从容」";
                if (InvokeRequired)
                {
                    Invoke(new Action(() => uiLabel1.Text = defaultText));
                }
                else
                {
                    uiLabel1.Text = defaultText;
                }
            }
        }

        /// <summary>
        /// 查看日志菜单点击事件 - 打开日志文件夹并选中当前日志
        /// </summary>
        private void 查看日志ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                string logFolder = Path.GetDirectoryName(logFilePath);
                
                if (!Directory.Exists(logFolder))
                {
                    Directory.CreateDirectory(logFolder);
                }

                // 如果当前日志文件存在，使用 Explorer 打开并选中它
                if (File.Exists(logFilePath))
                {
                    // 使用 /select 参数打开资源管理器并选中文件
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{logFilePath}\"");
                    AppendLog($"已打开日志文件夹: {logFolder}", Color.Blue);
                }
                else
                {
                    // 文件不存在，直接打开文件夹
                    System.Diagnostics.Process.Start("explorer.exe", logFolder);
                    AppendLog($"已打开日志文件夹: {logFolder}", Color.Blue);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"打开日志失败: {ex.Message}", Color.Red);
                MessageBox.Show($"无法打开日志文件夹: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// GitHub 链接点击事件 - 打开项目主页
        /// </summary>
        private void linkLabelGithub_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("https://github.com/xiriovo/SakuraEDL");
            }
            catch (Exception ex)
            {
                AppendLog($"打开链接失败: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// 联系开发者链接点击事件 - 加QQ好友
        /// </summary>
        private void linkLabelDev_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("https://qm.qq.com/q/8U3zucpCU2");
            }
            catch (Exception ex)
            {
                AppendLog($"打开链接失败: {ex.Message}", Color.Red);
            }
        }

        private void Button2_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Title = "选择本地图片";
            openFileDialog.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*";
            openFileDialog.FilterIndex = 1;
            openFileDialog.RestoreDirectory = true;

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                selectedLocalImagePath = openFileDialog.FileName;
                AppendLog($"已选择本地文件：{selectedLocalImagePath}", Color.Green);

                // 使用异步加载避免UI卡死
                Task.Run(() => LoadLocalImage(selectedLocalImagePath));
            }
        }

        private void LoadLocalImage(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    SafeInvoke(() => AppendLog("文件不存在", Color.Red));
                    return;
                }

                // 检查文件大小
                FileInfo fi = new FileInfo(filePath);
                if (fi.Length > 50 * 1024 * 1024) // 50MB限制
                {
                    SafeInvoke(() => AppendLog($"文件过大（{fi.Length / 1024 / 1024}MB），请选择小于50MB的图片", Color.Red));
                    return;
                }

                // 方法1：使用超低质量加载
                using (Bitmap original = LoadImageWithLowQuality(filePath))
                {
                    if (original != null)
                    {
                        // 创建适合窗体大小的缩略图
                        Size targetSize = Size.Empty;
                        SafeInvoke(() => targetSize = this.ClientSize);
                        if (targetSize.IsEmpty) return;
                        
                        using (Bitmap resized = ResizeImageToFitWithLowMemory(original, targetSize))
                        {
                            if (resized != null)
                            {
                                SafeInvoke(() =>
                                {
                                    // 释放旧图片
                                    if (this.BackgroundImage != null)
                                    {
                                        this.BackgroundImage.Dispose();
                                        this.BackgroundImage = null;
                                    }

                                    // 设置新图片
                                    this.BackgroundImage = resized.Clone() as Bitmap;
                                    this.BackgroundImageLayout = ImageLayout.Stretch;

                                    // 添加到预览
                                    AddImageToPreview(resized.Clone() as Image, Path.GetFileName(filePath));

                                    AppendLog($"本地图片设置成功（{resized.Width}x{resized.Height}）", Color.Green);
                                });
                            }
                        }
                    }
                    else
                    {
                        SafeInvoke(() => AppendLog("无法加载图片，文件可能已损坏", Color.Red));
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                SafeInvoke(() =>
                {
                    AppendLog("内存严重不足，请尝试重启应用", Color.Red);
                    AppendLog("建议：关闭其他程序，释放内存", Color.Yellow);
                });
            }
            catch (Exception ex)
            {
                SafeInvoke(() => AppendLog($"图片加载失败：{ex.Message}", Color.Red));
            }
        }

        private Bitmap LoadImageWithLowQuality(string filePath)
        {
            try
            {
                // 使用最小内存的方式加载图片
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // 读取图片信息但不加载全部数据
                    using (Image img = Image.FromStream(fs, false, false))
                    {
                        // 如果图片很大，先创建缩略图
                        if (img.Width > 2000 || img.Height > 2000)
                        {
                            int newWidth = Math.Min(img.Width / 4, 800);
                            int newHeight = Math.Min(img.Height / 4, 600);

                            Bitmap thumbnail = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);
                            using (Graphics g = Graphics.FromImage(thumbnail))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
                                g.DrawImage(img, 0, 0, newWidth, newHeight);
                            }
                            return thumbnail;
                        }
                        else
                        {
                            // 直接返回新Bitmap
                            return new Bitmap(img);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"加载图片失败：{ex.Message}", Color.Red);
                return null;
            }
        }

        private Bitmap ResizeImageToFitWithLowMemory(Image original, Size targetSize)
        {
            try
            {
                // 限制预览图片尺寸
                int maxWidth = Math.Min(800, targetSize.Width);
                int maxHeight = Math.Min(600, targetSize.Height);

                int newWidth, newHeight;

                // 计算新尺寸
                double ratioX = (double)maxWidth / original.Width;
                double ratioY = (double)maxHeight / original.Height;
                double ratio = Math.Min(ratioX, ratioY);

                newWidth = (int)(original.Width * ratio);
                newHeight = (int)(original.Height * ratio);

                // 确保最小尺寸
                newWidth = Math.Max(100, newWidth);
                newHeight = Math.Max(100, newHeight);

                // 创建新Bitmap
                Bitmap result = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);

                using (Graphics g = Graphics.FromImage(result))
                {
                    // 使用最低质量设置节省内存
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;

                    g.DrawImage(original, 0, 0, newWidth, newHeight);
                }

                return result;
            }
            catch (Exception ex)
            {
                AppendLog($"调整图片大小失败：{ex.Message}", Color.Red);
                return null;
            }
        }

        private async void Button3_Click(object sender, EventArgs e)
        {
            string url = uiComboBox3.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                AppendLog("请输入或选择壁纸URL", Color.Red);
                return;
            }

            // 清理URL
            url = url.Trim('`', '\'');
            AppendLog($"正在从URL获取壁纸：{url}", Color.Blue);

            try
            {
                // 使用最简单的方式
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15); // 增加超时时间
                    client.DefaultRequestHeaders.UserAgent.TryParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("image/*"));
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/html"));

                    // 显示加载提示
                    AppendLog("正在下载图片...", Color.Blue);

                    byte[] imageData = null;

                    // 特殊处理某些API
                    if (url.Contains("picsum.photos"))
                    {
                        // 添加随机参数避免缓存
                        url += $"?random={DateTime.Now.Ticks}";
                    }
                    else if (url.Contains("loliapi.com"))
            {
                // 特殊处理loliapi.com API响应...
                AppendLog("正在处理loliapi.com API响应...", Color.Blue);
                // 注意：loliapi.com 直接返回图片二进制数据，不需要JSON参数
            }

                    // 发送请求并获取响应
                    using (HttpResponseMessage response = await client.GetAsync(url))
                    {
                        response.EnsureSuccessStatusCode();
                        
                        // 检查响应内容类型
                        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                        AppendLog($"响应内容类型: {contentType}", Color.Blue);
                        
                        // 检查是否是图片
                        if (contentType.StartsWith("image/"))
                        {
                            imageData = await response.Content.ReadAsByteArrayAsync();
                            AppendLog($"下载的图片大小: {imageData.Length} 字节", Color.Blue);
                        }
                        else if (contentType.Contains("json"))
                        {
                            // 处理JSON响应
                            string jsonContent = await response.Content.ReadAsStringAsync();
                            AppendLog($"JSON响应长度: {jsonContent.Length}", Color.Blue);
                            
                            // 尝试从JSON中提取图片URL
                            string imageUrl = ExtractImageUrlFromJson(jsonContent);
                            if (!string.IsNullOrEmpty(imageUrl))
                            {
                                AppendLog($"从JSON中提取到图片URL: {imageUrl}", Color.Blue);
                                // 下载提取到的图片
                                using (HttpResponseMessage imageResponse = await client.GetAsync(imageUrl))
                                {
                                    imageResponse.EnsureSuccessStatusCode();
                                    imageData = await imageResponse.Content.ReadAsByteArrayAsync();
                                    AppendLog($"下载的图片大小: {imageData.Length} 字节", Color.Blue);
                                }
                            }
                            else
                            {
                                AppendLog("无法从JSON响应中提取图片URL", Color.Red);
                                AppendLog($"JSON内容: {jsonContent.Substring(0, Math.Min(500, jsonContent.Length))}...", Color.Yellow);
                                return;
                            }
                        }
                        else
                        {
                            // 可能是重定向或HTML响应
                            string content = await response.Content.ReadAsStringAsync();
                            AppendLog($"响应不是图片，内容长度: {content.Length}", Color.Yellow);
                            
                            // 尝试从HTML中提取图片URL
                            string imageUrl = ExtractImageUrlFromHtml(content);
                            if (!string.IsNullOrEmpty(imageUrl))
                            {
                                AppendLog($"从HTML中提取到图片URL: {imageUrl}", Color.Blue);
                                // 下载提取到的图片
                                using (HttpResponseMessage imageResponse = await client.GetAsync(imageUrl))
                                {
                                    imageResponse.EnsureSuccessStatusCode();
                                    imageData = await imageResponse.Content.ReadAsByteArrayAsync();
                                    AppendLog($"下载的图片大小: {imageData.Length} 字节", Color.Blue);
                                }
                            }
                            else
                            {
                                AppendLog("无法从响应中提取图片URL", Color.Red);
                                // 显示部分响应内容用于调试
                                if (content.Length > 0)
                                {
                                    AppendLog($"响应内容预览: {content.Substring(0, Math.Min(500, content.Length))}...", Color.Yellow);
                                }
                                return;
                            }
                        }
                    }

                    if (imageData == null || imageData.Length < 1000)
                    {
                        AppendLog("下载的数据无效", Color.Red);
                        return;
                    }

                    // 直接从内存加载图片，避免文件扩展名问题
                    LoadAndSetBackgroundFromMemory(imageData, url);
                }
            }
            catch (HttpRequestException ex)
            {
                AppendLog($"网络请求失败：{ex.Message}", Color.Red);
                AppendLog("请检查网络连接或尝试其他网址", Color.Yellow);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("参数无效") || ex.Message.Contains("Invalid parameter"))
                {
                    AppendLog("图片格式可能不受完全支持，尝试使用其他图片URL", Color.Yellow);
                   // AppendLog($"错误详情：{ex.Message}", Color.Red);
                }
                else
                {
                    AppendLog($"壁纸获取失败：{ex.Message}", Color.Red);
                //    AppendLog($"错误详情：{ex.ToString()}", Color.Yellow);
                }
            }
        }

        private string ExtractImageUrlFromJson(string jsonContent)
        {
            try
            {
                // 尝试简单的JSON解析
                jsonContent = jsonContent.Trim();
                
                // 处理常见的JSON格式
                if (jsonContent.StartsWith("{") && jsonContent.EndsWith("}"))
                {
                    // 尝试提取url字段
                    int urlIndex = jsonContent.IndexOf("\"url\"", StringComparison.OrdinalIgnoreCase);
                    if (urlIndex >= 0)
                    {
                        int startIndex = jsonContent.IndexOf(":", urlIndex) + 1;
                        int endIndex = jsonContent.IndexOf("\"", startIndex + 1);
                        if (startIndex > 0 && endIndex > startIndex)
                        {
                            string url = jsonContent.Substring(startIndex, endIndex - startIndex).Trim('"', ' ', '\t', ',');
                            if (url.StartsWith("http"))
                            {
                                return url;
                            }
                        }
                    }
                    
                    // 尝试提取data字段
                    int dataIndex = jsonContent.IndexOf("\"data\"", StringComparison.OrdinalIgnoreCase);
                    if (dataIndex >= 0)
                    {
                        int startIndex = jsonContent.IndexOf(":", dataIndex) + 1;
                        int endIndex = jsonContent.IndexOf("\"", startIndex + 1);
                        if (startIndex > 0 && endIndex > startIndex)
                        {
                            string url = jsonContent.Substring(startIndex, endIndex - startIndex).Trim('"', ' ', '\t', ',');
                            if (url.StartsWith("http"))
                            {
                                return url;
                            }
                        }
                    }
                }
                else if (jsonContent.StartsWith("[") && jsonContent.EndsWith("]"))
                {
                    // 处理数组格式
                    int urlIndex = jsonContent.IndexOf("\"url\"", StringComparison.OrdinalIgnoreCase);
                    if (urlIndex >= 0)
                    {
                        int startIndex = jsonContent.IndexOf(":", urlIndex) + 1;
                        int endIndex = jsonContent.IndexOf("\"", startIndex + 1);
                        if (startIndex > 0 && endIndex > startIndex)
                        {
                            string url = jsonContent.Substring(startIndex, endIndex - startIndex).Trim('"', ' ', '\t', ',');
                            if (url.StartsWith("http"))
                            {
                                return url;
                            }
                        }
                    }
                }
                
                // 尝试使用正则表达式提取URL
                System.Text.RegularExpressions.Regex urlRegex = new System.Text.RegularExpressions.Regex(
                    @"https?://[^\s""'<>]+?\.(?:jpg|jpeg|png|gif|webp)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                
                System.Text.RegularExpressions.Match match = urlRegex.Match(jsonContent);
                if (match.Success)
                {
                    return match.Value;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                AppendLog($"解析JSON失败：{ex.Message}", Color.Red);
                return null;
            }
        }

        private string ExtractImageUrlFromHtml(string html)
        {
            try
            {
                // 简单的正则表达式提取图片URL
                System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(
                    @"https?://[^\s""'<>]+?\.(?:jpg|jpeg|png|gif|webp)", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                
                System.Text.RegularExpressions.Match match = regex.Match(html);
                if (match.Success)
                {
                    return match.Value;
                }
                
                // 尝试提取所有可能的URL
                System.Text.RegularExpressions.Regex urlRegex = new System.Text.RegularExpressions.Regex(
                    @"https?://[^\s""'<>]+", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                
                System.Text.RegularExpressions.MatchCollection matches = urlRegex.Matches(html);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string url = m.Value;
                    if (url.Contains(".jpg") || url.Contains(".jpeg") || url.Contains(".png") || 
                        url.Contains(".gif") || url.Contains(".webp"))
                    {
                        return url;
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                AppendLog($"提取图片URL失败：{ex.Message}", Color.Red);
                return null;
            }
        }

        private void LoadAndSetBackgroundFromMemory(byte[] imageData, string sourceUrl)
        {
            try
            {
                // 检查数据是否有效
                if (imageData == null || imageData.Length < 100)
                {
                    AppendLog("图片数据无效或过小", Color.Red);
                    return;
                }

                // 检查是否是有效的图片数据（通过文件头）
                string fileHeader = BitConverter.ToString(imageData, 0, Math.Min(8, imageData.Length)).ToLower();
                bool isImage = false;
                
                // 检查常见图片格式的文件头
                if (fileHeader.StartsWith("89-50-4e-47") || // PNG
                    fileHeader.StartsWith("ff-d8") || // JPEG
                    fileHeader.StartsWith("42-4d") || // BMP
                    fileHeader.StartsWith("47-49-46") || // GIF
                    fileHeader.StartsWith("52-49-46-46") || // WebP
                    fileHeader.StartsWith("00-00-00-1c") || // MP4
                    fileHeader.StartsWith("00-00-00-18")) // MP4
                {
                    isImage = true;
                }

                if (!isImage)
                {
                    AppendLog("文件不是有效的图片格式", Color.Red);
                    AppendLog($"文件头: {fileHeader}", Color.Yellow);
                    return;
                }

                // 特殊处理WebP格式
                bool isWebP = fileHeader.StartsWith("52-49-46-46");
                if (isWebP)
                {
                    AppendLog("检测到WebP格式图片，使用特殊处理...", Color.Blue);
                }

                // 创建内存流
                using (MemoryStream ms = new MemoryStream(imageData))
                {
                    ms.Position = 0; // 确保流位置在开始
                    
                    try
                    {
                        using (Image original = Image.FromStream(ms, false, false))
                        {
                            if (original != null)
                            {
                                AppendLog($"成功加载图片，尺寸: {original.Width}x{original.Height}", Color.Blue);
                                
                                Size targetSize = this.ClientSize;
                                using (Bitmap resized = ResizeImageToFitWithLowMemory(original, targetSize))
                                {
                                    if (resized != null)
                                    {
                                        // 释放旧图片
                                        if (this.BackgroundImage != null)
                                        {
                                            this.BackgroundImage.Dispose();
                                            this.BackgroundImage = null;
                                        }

                                        // 设置新图片
                                        this.BackgroundImage = resized.Clone() as Bitmap;
                                        this.BackgroundImageLayout = ImageLayout.Stretch;

                                        // 添加到预览
                                      //  AddImageToPreview(resized.Clone() as Image, "网络图片");

                                    //    AppendLog($"网络图片设置成功（{resized.Width}x{resized.Height}）", Color.Green);

                                        // 添加到历史记录
                                        if (!urlHistory.Contains(sourceUrl))
                                        {
                                            urlHistory.Add(sourceUrl);
                                        }

                                        // 更新下拉框
                                        UpdateUrlComboBox(sourceUrl);
                                    }
                                }
                            }
                            else
                            {
                                AppendLog("下载的文件不是有效图片", Color.Red);
                            }
                        }
                    }
                    catch (Exception ex) when (ex.Message.Contains("参数无效") || ex.Message.Contains("Invalid parameter"))
                    {
                        // 处理"参数无效"错误，这通常发生在WebP格式不被完全支持时
                        AppendLog("图片格式可能不受完全支持，尝试转换...", Color.Yellow);
                        
                        // 尝试保存为临时文件然后重新加载
                        string tempFile = Path.GetTempFileName() + (isWebP ? ".webp" : ".jpg");
                        try
                        {
                            File.WriteAllBytes(tempFile, imageData);
                         //   AppendLog($"已保存临时文件: {tempFile}", Color.Blue);
                            
                            // 尝试使用不同的方式加载
                            using (Image original = Image.FromFile(tempFile))
                            {
                                if (original != null)
                                {
                                  //  AppendLog($"成功从文件加载图片，尺寸: {original.Width}x{original.Height}", Color.Blue);
                                    
                                    Size targetSize = this.ClientSize;
                                    using (Bitmap resized = ResizeImageToFitWithLowMemory(original, targetSize))
                                    {
                                        if (resized != null)
                                        {
                                            // 释放旧图片
                                            if (this.BackgroundImage != null)
                                            {
                                                this.BackgroundImage.Dispose();
                                                this.BackgroundImage = null;
                                            }

                                            // 设置新图片
                                            this.BackgroundImage = resized.Clone() as Bitmap;
                                            this.BackgroundImageLayout = ImageLayout.Stretch;

                                            // 添加到预览
                                            AddImageToPreview(resized.Clone() as Image, "网络图片");

                                         //   AppendLog($"网络图片设置成功（{resized.Width}x{resized.Height}）", Color.Green);

                                            // 添加到历史记录
                                            if (!urlHistory.Contains(sourceUrl))
                                            {
                                                urlHistory.Add(sourceUrl);
                                            }

                                            // 更新下拉框
                                            UpdateUrlComboBox(sourceUrl);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // 尝试使用GDI+的其他方法
                            try
                            {
                            //    AppendLog("尝试使用GDI+直接绘制...", Color.Yellow);
                                
                                // 创建一个新的Bitmap并手动绘制
                                using (Bitmap tempBmp = new Bitmap(800, 600))
                                using (Graphics g = Graphics.FromImage(tempBmp))
                                {
                                    g.Clear(Color.White);
                                    
                                    // 尝试使用WebClient下载并绘制
                                    AppendLog("图片加载失败", Color.Yellow);
                                    AppendLog("请尝试使用其他图片URL", Color.Yellow);
                                }
                            }
                            catch (Exception)
                            {
                                AppendLog("无法处理此图片格式", Color.Red);
                            }
                        }
                        finally
                        {
                            // 清理临时文件
                            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* 临时文件删除失败可忽略 */ }
                        }
                    }
                }

                // 垃圾回收
                GC.Collect();
            }
            catch (OutOfMemoryException)
            {
                AppendLog("内存不足，无法处理图片", Color.Red);
            }
            catch (Exception ex)
            {
                AppendLog($"图片处理失败：{ex.Message}", Color.Red);
                // 输出更详细的错误信息
             //   AppendLog($"错误详情：{ex.ToString()}", Color.Yellow);
            }
        }

        private void AddImageToPreview(Image image, string description)
        {
            if (image == null) return;

            try
            {
                // 限制预览图片数量
                if (previewImages.Count >= MAX_PREVIEW_IMAGES)
                {
                    // 移除最旧的预览
                    Image oldImage = previewImages[0];
                    previewImages.RemoveAt(0);
                    oldImage.Dispose();
                }

                // 添加新预览
                previewImages.Add(image);

                // 更新预览控件
                UpdateImagePreview();

          //      AppendLog($"已添加到预览：{description}", Color.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"更新预览失败：{ex.Message}", Color.Red);
            }
        }

        private void UpdateImagePreview()
        {
            if (previewImages.Count == 0)
            {
                // 显示默认图片或清空
                pictureBox1.Image = null;
                pictureBox1.Invalidate();
                return;
            }

            try
            {
                // 显示最新的预览图片
                Image latestImage = previewImages[previewImages.Count - 1];
                pictureBox1.Image = latestImage;

                // 更新预览标签
                UpdatePreviewLabel();
            }
            catch (Exception ex)
            {
                AppendLog($"显示预览失败：{ex.Message}", Color.Red);
            }
        }

        private void UpdatePreviewLabel()
        {
            if (previewImages.Count > 0 && label3 != null)
            {
                Image currentImage = pictureBox1.Image;
                if (currentImage != null)
                {
                    label3.Text = $"Preview: {currentImage.Width}x{currentImage.Height} ({previewImages.Count} images)";
                }
            }
        }

        private void ClearImagePreview()
        {
            try
            {
                // 清空预览控件
                pictureBox1.Image = null;

                // 释放所有预览图片
                foreach (Image img in previewImages)
                {
                    img?.Dispose();
                }
                previewImages.Clear();

                // 重置标签
                label3.Text = "预览";
            }
            catch (Exception ex)
            {
                AppendLog($"清空预览失败：{ex.Message}", Color.Red);
            }
        }

        private void UpdateUrlComboBox(string newUrl)
        {
            if (!uiComboBox3.Items.Contains(newUrl))
            {
                uiComboBox3.Items.Add(newUrl);
            }
        }

        private void Slider1_ValueChanged(object sender, EventArgs e)
        {
            int value = (int)slider1.Value;
            float opacity = Math.Max(0.2f, value / 100.0f);
            this.Opacity = opacity;
        }

        private void UiComboBox4_SelectedIndexChanged(object sender, EventArgs e)
        {
            // English-only build. Language switching is intentionally disabled.
        }

        /// <summary>
        /// 应用当前语言到界面
        /// </summary>
        private void ApplyLanguage()
        {
            string lang = LanguageManager.CurrentLanguage;
            isEnglish = (lang == "en");

            // ========== 标签页 ==========
            tabPage1.Text = LanguageManager.T("tab.autoRoot");
            tabPage2.Text = LanguageManager.T("tab.qualcomm");
            tabPage3.Text = LanguageManager.T("tab.fastboot");
            tabPage4.Text = LanguageManager.T("tab.mtk");
            tabPage5.Text = LanguageManager.T("tab.spd");
            tabPage6.Text = LanguageManager.T("tab.settings");

            // ========== 菜单 ==========
            快捷重启ToolStripMenuItem.Text = LanguageManager.T("menu.quickRestart");
            toolStripMenuItem1.Text = LanguageManager.T("menu.edlOps");
            其他ToolStripMenuItem.Text = LanguageManager.T("menu.other");
            toolStripMenuItem2.Text = LanguageManager.T("menu.rebootSystem");
            toolStripMenuItem6.Text = LanguageManager.T("menu.rebootBootloader");
            toolStripMenuItem7.Text = LanguageManager.T("menu.rebootFastbootd");
            重启恢复ToolStripMenuItem.Text = LanguageManager.T("menu.rebootRecovery");
            mIToolStripMenuItem.Text = LanguageManager.T("menu.miKickEdl");
            联想或安卓踢EDLToolStripMenuItem.Text = LanguageManager.T("menu.lenovoKickEdl");
            擦除谷歌锁ToolStripMenuItem.Text = LanguageManager.T("menu.eraseFrp");
            切换槽位ToolStripMenuItem.Text = LanguageManager.T("menu.switchSlot");
            合并SuperToolStripMenuItem.Text = LanguageManager.T("menu.mergeSuper");
            提取PayloadToolStripMenuItem.Text = LanguageManager.T("menu.extractPayload");
            toolStripMenuItem4.Text = LanguageManager.T("menu.edlToEdl");
            toolStripMenuItem5.Text = LanguageManager.T("menu.edlToFbd");
            eDL擦除谷歌锁ToolStripMenuItem.Text = LanguageManager.T("menu.edlEraseFrp");
            eDL切换槽位ToolStripMenuItem.Text = LanguageManager.T("menu.edlSwitchSlot");
            激活LUNToolStripMenuItem.Text = LanguageManager.T("menu.activateLun");
            设备管理器ToolStripMenuItem.Text = LanguageManager.T("menu.deviceManager");
            cMD命令行ToolStripMenuItem.Text = LanguageManager.T("menu.cmdPrompt");
            安卓驱动ToolStripMenuItem.Text = LanguageManager.T("menu.androidDriver");
            mTK驱动ToolStripMenuItem.Text = LanguageManager.T("menu.mtkDriver");
            高通驱动ToolStripMenuItem.Text = LanguageManager.T("menu.qualcommDriver");
            展讯驱动ToolStripMenuItem.Text = LanguageManager.T("menu.spdDriver");
            查看日志ToolStripMenuItem.Text = LanguageManager.T("menu.viewLog");

            // ========== 设置页 ==========
            label1.Text = LanguageManager.T("settings.blur");
            label2.Text = LanguageManager.T("settings.wallpaper");
            label3.Text = LanguageManager.T("settings.preview");
            button2.Text = LanguageManager.T("settings.localWallpaper");
            button3.Text = LanguageManager.T("settings.apply");

            // ========== 高通页面 ==========
            // 引导模式选择
            if (select3.Items.Count >= 1)
            {
                select3.Items[0] = LanguageManager.T("qualcomm.autoDetectBoot");
            }
            if (!select3.Text.Contains("[VIP]") && !select3.Text.Contains("SM"))
            {
                select3.Text = LanguageManager.T("qualcomm.autoDetectBoot");
            }
            
            // 按钮
            uiButton6.Text = LanguageManager.T("qualcomm.readPartTable");
            uiButton7.Text = LanguageManager.T("qualcomm.readPart");
            uiButton8.Text = LanguageManager.T("qualcomm.writePart");
            uiButton9.Text = LanguageManager.T("qualcomm.erasePart");
            uiButton1.Text = LanguageManager.T("qualcomm.stop");
            button4.Text = LanguageManager.T("qualcomm.browse");
            
            // 输入框提示
            input8.PlaceholderText = LanguageManager.T("qualcomm.selectProgrammer");
            input6.PlaceholderText = LanguageManager.T("qualcomm.selectRawXml");
            select4.PlaceholderText = LanguageManager.T("qualcomm.findPart");
            
            // 选项
            checkbox12.Text = LanguageManager.T("option.skipBoot");
            checkbox16.Text = LanguageManager.T("option.protectPart");
            checkbox11.Text = LanguageManager.T("option.generateXml");
            checkbox15.Text = LanguageManager.T("option.autoReboot");
            checkbox13.Text = LanguageManager.T("option.selectAll");
            checkbox14.Text = LanguageManager.T("qualcomm.autoDetect");

            // 分区表列头
            if (listView2.Columns.Count >= 9)
            {
                listView2.Columns[0].Text = LanguageManager.T("qualcomm.partition");
                listView2.Columns[1].Text = LanguageManager.T("qualcomm.lun");
                listView2.Columns[2].Text = LanguageManager.T("qualcomm.size");
                listView2.Columns[3].Text = LanguageManager.T("qualcomm.startSector");
                listView2.Columns[4].Text = LanguageManager.T("qualcomm.endSector");
                listView2.Columns[5].Text = LanguageManager.T("qualcomm.sectorCount");
                listView2.Columns[6].Text = LanguageManager.T("qualcomm.startAddr");
                listView2.Columns[7].Text = LanguageManager.T("qualcomm.endAddr");
                listView2.Columns[8].Text = LanguageManager.T("qualcomm.filePath");
            }
            
            // 分区表标题
            uiGroupBox4.Text = LanguageManager.T("qualcomm.partTable");

            // ========== 设备信息面板 ==========
            uiComboBox1.Text = LanguageManager.T("device.status") + ": " + LanguageManager.T("device.noDevice");
            uiGroupBox3.Text = LanguageManager.T("device.info");
            uiGroupBox1.Text = LanguageManager.T("device.log");
            
            // 设备信息标签
            string waiting = LanguageManager.T("device.waiting");
            string colon = ": ";
            uiLabel9.Text = LanguageManager.T("device.brand") + colon + waiting;
            uiLabel11.Text = LanguageManager.T("device.chip") + colon + waiting;
            uiLabel12.Text = "OTA" + colon + waiting;
            uiLabel10.Text = LanguageManager.T("device.serial") + colon + waiting;
            uiLabel3.Text = LanguageManager.T("device.model") + colon + waiting;
            uiLabel14.Text = LanguageManager.T("device.model") + colon + waiting;
            uiLabel13.Text = LanguageManager.T("device.storage") + colon + waiting;

            // ========== 状态栏 ==========
            uiLabel8.Text = LanguageManager.T("status.operation") + colon + LanguageManager.T("status.idle");
            uiLabel7.Text = LanguageManager.T("status.speed") + colon + "0 KB/s";
            uiLabel6.Text = LanguageManager.T("status.time") + colon + "00:00";
            uiLabel4.Text = LanguageManager.T("status.computer") + colon + "Windows 11 64" + LanguageManager.T("status.bit");
            linkLabelDev.Text = LanguageManager.T("status.contactDev");
            
            // 保留数据选项
            checkbox20.Text = LanguageManager.T("option.keepData");
            
            // ========== Fastboot 页面 ==========
            tabPage3.Text = LanguageManager.T("tab.fastboot");
            uiButton10.Text = LanguageManager.T("fastboot.execute");
            uiButton11.Text = LanguageManager.T("fastboot.readInfo");
            uiButton22.Text = LanguageManager.T("fastboot.fixFbd");
            checkbox7.Text = LanguageManager.T("fastboot.oplusFlash");
            checkbox21.Text = LanguageManager.T("fastboot.lockBl");
            checkbox22.Text = LanguageManager.T("fastboot.clearData");
            checkbox43.Text = LanguageManager.T("menu.eraseFrp");
            checkbox41.Text = LanguageManager.T("fastboot.switchSlotA");
            button8.Text = LanguageManager.T("qualcomm.browse");
            button9.Text = LanguageManager.T("qualcomm.browse");
            uiGroupBox7.Text = LanguageManager.T("qualcomm.partTable");
            uiButton18.Text = LanguageManager.T("qualcomm.readPartTable");
            uiButton19.Text = LanguageManager.T("fastboot.extractImage");
            uiButton20.Text = LanguageManager.T("qualcomm.writePart");
            uiButton21.Text = LanguageManager.T("qualcomm.erasePart");
            checkbox44.Text = LanguageManager.T("option.autoReboot");
            checkbox45.Text = LanguageManager.T("fastboot.fbdFlash");
            checkbox50.Text = LanguageManager.T("option.keepData");
            
            // Fastboot 分区表列头
            if (listView1.Columns.Count >= 4)
            {
                listView1.Columns[0].Text = LanguageManager.T("qualcomm.partition");
                listView1.Columns[1].Text = LanguageManager.T("table.operation");
                listView1.Columns[2].Text = LanguageManager.T("qualcomm.size");
                listView1.Columns[3].Text = LanguageManager.T("qualcomm.filePath");
            }
            
            // ========== MTK 页面 ==========
            mtkBtnReadGpt.Text = LanguageManager.T("qualcomm.readPartTable");
            mtkBtnWritePartition.Text = LanguageManager.T("qualcomm.writePart");
            mtkBtnReadPartition.Text = LanguageManager.T("qualcomm.readPart");
            mtkBtnErasePartition.Text = LanguageManager.T("qualcomm.erasePart");
            mtkBtnReboot.Text = LanguageManager.T("mtk.rebootDevice");
            mtkBtnReadImei.Text = LanguageManager.T("mtk.readImei");
            mtkBtnWriteImei.Text = LanguageManager.T("mtk.writeImei");
            mtkBtnBackupNvram.Text = LanguageManager.T("mtk.backupNvram");
            mtkBtnRestoreNvram.Text = LanguageManager.T("mtk.restoreNvram");
            mtkBtnFormatData.Text = LanguageManager.T("mtk.formatData");
            mtkBtnUnlockBl.Text = LanguageManager.T("mtk.unlockBl");
            mtkBtnExploit.Text = LanguageManager.T("mtk.exploit");
            mtkBtnConnect.Text = LanguageManager.T("mtk.connect");
            mtkBtnDisconnect.Text = LanguageManager.T("mtk.disconnect");
            mtkChkAutoStorage.Text = LanguageManager.T("mtk.auto");
            mtkGrpPartitions.Text = LanguageManager.T("qualcomm.partTable");
            
            // MTK 引导模式选择
            if (mtkSelectBootMode.Items.Count >= 3)
            {
                mtkSelectBootMode.Items[0] = LanguageManager.T("mtk.autoDetectBoot");
                mtkSelectBootMode.Items[1] = LanguageManager.T("mtk.cloudMatch");
                mtkSelectBootMode.Items[2] = LanguageManager.T("mtk.localSelect");
            }
            
            // MTK 验证方式
            if (mtkSelectAuthMethod.Items.Count >= 4)
            {
                mtkSelectAuthMethod.Items[0] = LanguageManager.T("mtk.authMethod");
                mtkSelectAuthMethod.Items[1] = LanguageManager.T("mtk.normalAuth");
                mtkSelectAuthMethod.Items[2] = LanguageManager.T("mtk.realmeCloud");
                mtkSelectAuthMethod.Items[3] = LanguageManager.T("mtk.bypassAuth");
            }
            
            // MTK 分区表列头
            if (mtkListPartitions.Columns.Count >= 5)
            {
                mtkListPartitions.Columns[0].Text = LanguageManager.T("qualcomm.partition");
                mtkListPartitions.Columns[1].Text = LanguageManager.T("table.type");
                mtkListPartitions.Columns[2].Text = LanguageManager.T("qualcomm.size");
                mtkListPartitions.Columns[3].Text = LanguageManager.T("table.address");
                mtkListPartitions.Columns[4].Text = LanguageManager.T("table.fileName");
            }
            
            // ========== SPD 页面 ==========
            sprdBtnReadGpt.Text = LanguageManager.T("qualcomm.readPartTable");
            sprdBtnWritePartition.Text = LanguageManager.T("qualcomm.writePart");
            sprdBtnReadPartition.Text = LanguageManager.T("qualcomm.readPart");
            sprdBtnErasePartition.Text = LanguageManager.T("qualcomm.erasePart");
            sprdBtnReboot.Text = LanguageManager.T("mtk.rebootDevice");
            sprdBtnReadImei.Text = LanguageManager.T("mtk.readImei");
            sprdBtnWriteImei.Text = LanguageManager.T("mtk.writeImei");
            sprdBtnBackupCalib.Text = LanguageManager.T("spd.backupCalib");
            sprdBtnRestoreCalib.Text = LanguageManager.T("spd.restoreCalib");
            sprdBtnFactoryReset.Text = LanguageManager.T("spd.factoryReset");
            sprdBtnUnlockBL.Text = LanguageManager.T("mtk.unlockBl");
            sprdBtnExtract.Text = LanguageManager.T("spd.extractPac");
            sprdBtnNvManager.Text = LanguageManager.T("spd.nvManager");
            sprdChkRebootAfter.Text = LanguageManager.T("spd.rebootAfter");
            sprdChkSkipUserdata.Text = LanguageManager.T("spd.skipUserdata");
            sprdGroupPartitions.Text = LanguageManager.T("qualcomm.partTable");
            sprdSelectChip.PlaceholderText = LanguageManager.T("spd.chipModel");
            sprdSelectDevice.PlaceholderText = LanguageManager.T("spd.deviceModel");
            
            // SPD 分区表列头
            if (sprdListPartitions.Columns.Count >= 6)
            {
                sprdListPartitions.Columns[0].Text = LanguageManager.T("qualcomm.partition");
                sprdListPartitions.Columns[1].Text = LanguageManager.T("table.fileName");
                sprdListPartitions.Columns[2].Text = LanguageManager.T("qualcomm.size");
                sprdListPartitions.Columns[3].Text = LanguageManager.T("table.type");
                sprdListPartitions.Columns[4].Text = LanguageManager.T("table.loadAddr");
                sprdListPartitions.Columns[5].Text = LanguageManager.T("table.offset");
            }
            
            // ========== 设置页其他 ==========
            button7.Text = LanguageManager.T("settings.clearCache");
            
            // ========== 自动root页面 ==========
            labelDevRoot.Text = LanguageManager.T("dev.inProgress");
            
            // ========== 遗漏的菜单项 ==========
            eDL到EDLToolStripMenuItem.Text = LanguageManager.T("menu.edlToEdl");
            eDL到FBDToolStripMenuItem.Text = LanguageManager.T("menu.edlToFbd");
            eDLToolStripMenuItem.Text = LanguageManager.T("menu.edlFactoryReset");
            
            // ========== Fastboot 分区管理子页面 ==========
            tabPage7.Text = LanguageManager.T("tab.partManage");
            tabPage8.Text = LanguageManager.T("tab.fileManage");
            uiGroupBox2.Text = LanguageManager.T("qualcomm.partTable");
            uiButton2.Text = LanguageManager.T("qualcomm.readPart");
            uiButton5.Text = LanguageManager.T("qualcomm.readPartTable");
            uiButton13.Text = LanguageManager.T("qualcomm.writePart");
            uiButton14.Text = LanguageManager.T("qualcomm.erasePart");
            checkbox3.Text = LanguageManager.T("menu.eraseFrp");
            checkbox6.Text = LanguageManager.T("option.autoReboot");
            button1.Text = LanguageManager.T("qualcomm.browse");
            
            
            // ========== 投屏功能 (scrcpy) ==========
            uiButton3.Text = LanguageManager.T("scrcpy.execute");
            uiButton4.Text = LanguageManager.T("scrcpy.startMirror");
            uiButton12.Text = LanguageManager.T("scrcpy.fixMirror");
            uiButton24.Text = LanguageManager.T("scrcpy.flashZip");
            checkbox1.Text = LanguageManager.T("scrcpy.screenOn");
            checkbox2.Text = LanguageManager.T("scrcpy.audioForward");
            checkbox4.Text = LanguageManager.T("scrcpy.autoReconnect");
            uiLabel15.Text = LanguageManager.T("scrcpy.battery");
            uiLabel16.Text = LanguageManager.T("scrcpy.refreshRate");
            uiLabel17.Text = LanguageManager.T("scrcpy.resolution");
            uiLabel18.Text = LanguageManager.T("device.storage");
            uiButton15.Text = LanguageManager.T("scrcpy.powerKey");
            uiButton16.Text = LanguageManager.T("scrcpy.recentKey");
            uiButton17.Text = LanguageManager.T("scrcpy.homeKey");
            uiButton23.Text = LanguageManager.T("scrcpy.backKey");
            
            // ========== 其他 ==========
            uiLabel1.Text = LanguageManager.T("status.loading");
            uiLabel5.Text = LanguageManager.T("app.version");
            
            // ========== Fastboot 页面补充 ==========
            input1.PlaceholderText = LanguageManager.T("fastboot.selectFlashBat");
            uiTextBox1.Watermark = LanguageManager.T("fastboot.selectPayload");
            uiComboBox2.Watermark = LanguageManager.T("fastboot.quickCommand");
            select5.PlaceholderText = LanguageManager.T("qualcomm.findPart");
            
            // Fastboot 主页分区表列头 (listView5)
            if (listView5.Columns.Count >= 4)
            {
                listView5.Columns[0].Text = LanguageManager.T("qualcomm.partition");
                listView5.Columns[1].Text = LanguageManager.T("table.operation");
                listView5.Columns[2].Text = LanguageManager.T("qualcomm.size");
                listView5.Columns[3].Text = LanguageManager.T("qualcomm.filePath");
            }
            
            // ========== 投屏页面补充 ==========
            uiComboBox5.Watermark = LanguageManager.T("fastboot.quickCommand");
            select2.PlaceholderText = LanguageManager.T("qualcomm.findPart");

            // 更新预览标签
            UpdatePreviewLabel();

        }

        // 保留旧方法以兼容
        private void SwitchLanguage(string language)
        {
            ApplyLanguage();
        }

        private void Checkbox17_CheckedChanged(object sender, EventArgs e)
        {
            if (checkbox17.Checked)
            {
                // 自动取消勾选checkbox19
               checkbox19.Checked = false;
                
                // 检查当前是否已经是紧凑布局（通过 input7 的可见性判断）
                if (input7.Visible)
                {
                    // 如果 input7 可见，说明当前是默认布局，需要改为紧凑布局
                    ApplyCompactLayout();
                }
                // 如果 input7 不可见，说明已经是紧凑布局，不做改变
            }
            
            // 更新认证模式
            UpdateAuthMode();
        }

        private void Checkbox19_CheckedChanged(object sender, EventArgs e)
        {
            if (checkbox19.Checked)
            {
                // 自动取消勾选 checkbox17
                checkbox17.Checked = false;
                
                RestoreOriginalLayout();
            }
            else
            {
                ApplyCompactLayout();
            }
            
            // 更新认证模式
            UpdateAuthMode();
        }

        private void ApplyCompactLayout()
        {
            try
            {
                // 挂起布局更新，减少闪烁
                this.SuspendLayout();
               uiGroupBox4.SuspendLayout();
                listView2.SuspendLayout();

                // 移除 input9, input7
                input9.Visible = false;
                input7.Visible = false;

                // 上移 input6, button4 到 input7 和 input9 的位置
                input6.Location = new Point(input6.Location.X, input7.Location.Y);
                button4.Location = new Point(button4.Location.X, input9.Location.Y);

                // 上移 checkbox13 到固定位置
                checkbox13.Location = new Point(6, 25);

                // 向上调整uiGroupBox4 和 listView2 的位置并变长
                const int VERTICAL_ADJUSTMENT = 38; // 使用固定数值调整
               uiGroupBox4.Location = new Point(uiGroupBox4.Location.X,uiGroupBox4.Location.Y - VERTICAL_ADJUSTMENT);
               uiGroupBox4.Size = new Size(uiGroupBox4.Size.Width,uiGroupBox4.Size.Height + VERTICAL_ADJUSTMENT);
                listView2.Size = new Size(listView2.Size.Width, listView2.Size.Height + VERTICAL_ADJUSTMENT);

                // 恢复布局更新
                listView2.ResumeLayout(false);
               uiGroupBox4.ResumeLayout(false);
                this.ResumeLayout(false);
                this.PerformLayout();

            }
            catch (Exception ex)
            {
                AppendLog($"应用布局失败: {ex.Message}", Color.Red);
            }
        }

        private void RestoreOriginalLayout()
        {
            try
            {
                // 挂起布局更新，减少闪烁
                this.SuspendLayout();
               uiGroupBox4.SuspendLayout();
                listView2.SuspendLayout();

                // 恢复 input9, input7 的显示
                input9.Visible = true;
                input7.Visible = true;

                // 恢复原始位置
                input6.Location = originalinput6Location;
                button4.Location = originalbutton4Location;
                // 恢复 checkbox13 到固定位置 (6, 25)
                checkbox13.Location = new Point(6, 25);

                // 恢复原始大小和位置
               uiGroupBox4.Location = originaluiGroupBox4Location;
               uiGroupBox4.Size = originaluiGroupBox4Size;
                listView2.Size = originallistView2Size;

                // 恢复布局更新
                listView2.ResumeLayout(false);
               uiGroupBox4.ResumeLayout(false);
                this.ResumeLayout(false);
                this.PerformLayout();

            }
            catch (Exception ex)
            {
                AppendLog($"恢复布局失败: {ex.Message}", Color.Red);
            }
        }

        private void uiGroupBox1_Click(object sender, EventArgs e)
        {

        }

        private void toolStripMenuItem6_Click(object sender, EventArgs e)
        {

        }

        private void 重启恢复ToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void Select3_SelectedIndexChanged(object sender, EventArgs e)
        {
            string selectedItem = select3.Text;
            
            // 判断选项类型
            bool isDefaultAutoMatch = selectedItem.Contains("自动识别") || selectedItem.Contains("云端自动匹配");
            bool isLocalSelect = selectedItem.Contains("本地选择") && !selectedItem.StartsWith("──");
            bool isSeparator = selectedItem.StartsWith("──");
            bool isRefresh = selectedItem.Contains("刷新云端列表");
            
            // 处理分隔符 - 如果选择了"── 本地选择 ──"，切换到本地模式
            if (isSeparator)
            {
                if (selectedItem.Contains("本地选择"))
                {
                    // 选择了本地选择分隔符，启用本地选择模式
                    input9.Enabled = true;
                    input8.Enabled = true;
                    input7.Enabled = true;
                    input8.Text = "";
                    input9.Text = "";
                    input7.Text = "";
                    _cloudLoaderAuthType = "none";
                    checkbox17.Checked = false;
                    checkbox19.Checked = false;
                    _authMode = "none";
                    AppendLog("[本地] 已切换到本地选择模式，请浏览选择引导文件", Color.Blue);
                }
                // 其他分隔符忽略
                return;
            }
            
            // 处理刷新云端列表
            if (isRefresh)
            {
                AppendLog("[云端] 正在刷新 Loader 列表...", Color.Cyan);
                _cloudLoadersLoaded = false;  // 重置加载标志
                LoadCloudLoadersAsync(true);  // 强制刷新
                select3.SelectedIndex = 0;    // 重置选择
                return;
            }
            
            // 处理默认模式（提示用户选择 Loader）
            if (isDefaultAutoMatch)
            {
                // 启用自定义引导文件输入（可选本地文件）
                input9.Enabled = true;
                input8.Enabled = true;
                input7.Enabled = true;
                
                // 显示提示
                input8.Text = "";
                input9.Text = "";
                input7.Text = "";
                
                // 重置选择的云端 Loader
                _selectedCloudLoaderId = 0;
                _cloudLoaderAuthType = "none";
                checkbox17.Checked = false;
                checkbox19.Checked = false;
                _authMode = "none";
                
                // 清除预下载缓存
                ClearLoaderCache();
                
                AppendLog("[提示] 请从下拉列表选择云端 Loader 或浏览本地引导文件", Color.Blue);
            }
            // 处理云端 Loader 选择（从下拉列表中选择具体的 Loader）
            else
            {
                var cloudLoader = GetSelectedCloudLoader();
                if (cloudLoader != null)
                {
                    // 禁用自定义引导文件输入 (使用云端 Loader)
                    input9.Enabled = false;
                    input8.Enabled = false;
                    input7.Enabled = false;
                    
                    // 显示选择的 Loader 信息
                    input8.Text = string.Format("[云端] {0} - {1}", cloudLoader.Chip, cloudLoader.Filename);
                    
                    // 根据厂商和验证类型自动设置
                    string vendorLower = (cloudLoader.Vendor ?? "").ToLower();
                    bool isOplusVendor = vendorLower.Contains("oplus") || vendorLower.Contains("oneplus") || vendorLower.Contains("oppo") || vendorLower.Contains("realme");
                    bool isXiaomiVendor = vendorLower.Contains("xiaomi") || vendorLower.Contains("redmi") || vendorLower.Contains("poco");
                    
                    // 记录当前 Loader 的验证类型
                    _cloudLoaderAuthType = cloudLoader.AuthType ?? "none";
                    
                    // VIP 验证 (auth_type = vip) -> 勾选 oplus checkbox
                    if (cloudLoader.IsVip)
                    {
                        AppendLog(string.Format("[云端] {0}", cloudLoader.DisplayName), Color.FromArgb(255, 100, 100));
                        checkbox17.Checked = false;
                        checkbox19.Checked = true;   // oplus = VIP 验证
                    }
                    // OnePlus/demacia 验证 -> 勾选 oldoneplus checkbox
                    // 仅当 auth_type 明确是 demacia/oneplus 时才勾选，不能仅凭厂商名称
                    else if (cloudLoader.IsOnePlus)
                    {
                        AppendLog(string.Format("[云端] {0}", cloudLoader.DisplayName), Color.FromArgb(255, 100, 100));
                        checkbox19.Checked = false;
                        checkbox17.Checked = true;   // oldoneplus = demacia 验证
                    }
                    // 小米设备
                    else if (isXiaomiVendor || cloudLoader.IsXiaomi)
                    {
                        AppendLog(string.Format("[云端] {0}", cloudLoader.DisplayName), Color.FromArgb(255, 165, 0));
                        _cloudLoaderAuthType = "miauth";
                        checkbox17.Checked = false;
                        checkbox19.Checked = false;
                        _authMode = "xiaomi";
                    }
                    // 无验证
                    else
                    {
                        AppendLog(string.Format("[云端] {0}", cloudLoader.DisplayName), Color.Green);
                        _cloudLoaderAuthType = "none";
                        checkbox17.Checked = false;
                        checkbox19.Checked = false;
                        _authMode = "none";
                    }
                    
                    // 存储选择的云端 Loader ID
                    _selectedCloudLoaderId = cloudLoader.Id;
                    
                    // 立即开始预下载 Loader (避免 EDL 看门狗超时)
                    _ = PredownloadCloudLoaderAsync(cloudLoader);
                    
                    // 根据云端 Loader 的存储类型自动设置 UI
                    if (!string.IsNullOrEmpty(cloudLoader.StorageType))
                    {
                        string loaderStorageType = cloudLoader.StorageType.ToLower();
                        if (loaderStorageType == "emmc")
                        {
                            _storageType = "emmc";
                            radio4.Checked = true;  // eMMC
                            radio3.Checked = false; // UFS
                        }
                        else
                        {
                            _storageType = "ufs";
                            radio3.Checked = true;  // UFS
                            radio4.Checked = false; // eMMC
                        }
                    }
                }
            }
        }
        
        // 选择的云端 Loader ID
        private int _selectedCloudLoaderId = 0;
        
        // 预下载缓存 (选择 Loader 时立即下载，避免 EDL 看门狗超时)
        private byte[] _cachedLoaderData = null;
        private byte[] _cachedDigestData = null;
        private byte[] _cachedSignatureData = null;
        private int _cachedLoaderId = 0;
        private bool _isPredownloading = false;
        
        /// <summary>
        /// 预下载云端 Loader (选择时立即下载，避免 EDL 看门狗超时)
        /// </summary>
        private async Task PredownloadCloudLoaderAsync(SakuraEDL.Qualcomm.Services.CloudLoaderInfo loader)
        {
            if (loader == null || loader.Id <= 0) return;
            
            // 如果已经缓存了相同的 Loader，跳过
            if (_cachedLoaderId == loader.Id && _cachedLoaderData != null)
            {
                AppendLog("[云端] 使用已缓存的 Loader", Color.Gray);
                return;
            }
            
            // 如果正在下载，跳过
            if (_isPredownloading) return;
            
            _isPredownloading = true;
            
            try
            {
                var cloudService = SakuraEDL.Qualcomm.Services.CloudLoaderService.Instance;
                
                // 清除旧缓存
                _cachedLoaderData = null;
                _cachedDigestData = null;
                _cachedSignatureData = null;
                _cachedLoaderId = 0;
                
                AppendLog("[云端] 正在下载 Loader...", Color.Cyan);
                
                // 进度回调
                Action<long, long, double> progressCallback = (downloaded, total, speed) =>
                {
                    if (this.InvokeRequired)
                    {
                        this.BeginInvoke(new Action(() => UpdateDownloadProgress(downloaded, total, speed)));
                    }
                    else
                    {
                        UpdateDownloadProgress(downloaded, total, speed);
                    }
                };
                
                // 下载 Loader
                byte[] loaderData = await cloudService.DownloadLoaderAsync(loader.Id, progressCallback);
                ResetDownloadSpeed();
                
                if (loaderData == null || loaderData.Length == 0)
                {
                    AppendLog("[云端] 下载 Loader 失败", Color.Red);
                    return;
                }
                
                AppendLog(string.Format("[云端] 下载完成 ({0} KB)", loaderData.Length / 1024), Color.Green);
                
                // 如果是 VIP 模式，下载 Digest 和 Sign 文件
                byte[] digestData = null;
                byte[] signatureData = null;
                
                if (loader.IsVip && loader.HasVipFiles)
                {
                    AppendLog("[云端] 下载 VIP 验证文件...", Color.Cyan);
                    var vipFiles = await cloudService.DownloadVipFilesAsync(loader.Id);
                    digestData = vipFiles.digest;
                    signatureData = vipFiles.sign;
                    
                    if (digestData != null && signatureData != null)
                    {
                        AppendLog(string.Format("[云端] VIP 文件下载完成 (Digest={0}B, Sign={1}B)", digestData.Length, signatureData.Length), Color.Green);
                    }
                    else
                    {
                        AppendLog("[云端] VIP 验证文件下载失败", Color.Orange);
                    }
                }
                
                // 缓存数据
                _cachedLoaderData = loaderData;
                _cachedDigestData = digestData;
                _cachedSignatureData = signatureData;
                _cachedLoaderId = loader.Id;
                
                AppendLog("[云端] Loader 已缓存，等待连接设备...", Color.Green);
            }
            catch (Exception ex)
            {
                AppendLog(string.Format("[云端] 预下载失败: {0}", ex.Message), Color.Red);
            }
            finally
            {
                _isPredownloading = false;
            }
        }
        
        /// <summary>
        /// 清除 Loader 缓存
        /// </summary>
        private void ClearLoaderCache()
        {
            _cachedLoaderData = null;
            _cachedDigestData = null;
            _cachedSignatureData = null;
            _cachedLoaderId = 0;
        }
        
        /// <summary>
        /// 从 EDL 选择项中提取 Loader ID
        /// </summary>
        private string ExtractEdlLoaderIdFromSelection(string selection)
        {
            // "[Huawei] 888 (通用)" -> "Huawei_888"
            // "[Meizu] Meizu21Pro" -> "Meizu_Meizu21Pro"
            if (string.IsNullOrEmpty(selection)) return "";
            
            // 提取品牌和名称
            int bracketEnd = selection.IndexOf(']');
            if (bracketEnd < 0) return "";
            
            string brand = selection.Substring(1, bracketEnd - 1);
            string rest = selection.Substring(bracketEnd + 1).Trim();
            
            // 处理通用 loader
            if (rest.EndsWith("(通用)"))
            {
                string chip = rest.Replace("(通用)", "").Trim();
                return $"{brand}_{chip}";
            }
            
            // 处理专用 loader
            // 从 rest 中提取型号名
            string model = rest.Replace($"{brand} ", "").Trim();
            // 移除芯片信息 (括号部分)
            int parenIndex = model.IndexOf('(');
            if (parenIndex > 0)
            {
                model = model.Substring(0, parenIndex).Trim();
            }
            
            return $"{brand}_{model}";
        }

        /// <summary>
        /// 从 VIP 选择项中提取平台名称
        /// </summary>
        private string ExtractPlatformFromVipSelection(string selection)
        {
            // "[VIP] SM8550 - Snapdragon 8Gen2/8+Gen2" -> "SM8550"
            if (string.IsNullOrEmpty(selection)) return "";
            
            // 移除 "[VIP] " 前缀
            string trimmed = selection.Replace("[VIP] ", "");
            
            // 取 " - " 之前的部分
            int dashIndex = trimmed.IndexOf(" - ");
            if (dashIndex > 0)
            {
                return trimmed.Substring(0, dashIndex).Trim();
            }
            
            return trimmed.Trim();
        }

        #region EDL Loader 初始化
        
        // 缓存 EDL Loader 列表项
        #pragma warning disable CS0414
        private List<string> _edlLoaderItems = null;
        #pragma warning restore CS0414
        
        /// <summary>
        /// 初始化 EDL Loader 选择列表 - 云端自动匹配 + 本地选择
        /// </summary>
        private void InitializeEdlLoaderList()
        {
            try
            {
                // 清空 Designer 中的默认项
                select3.Items.Clear();
                
                // 添加选项
                select3.Items.Add("☁️ 云端自动匹配");
                select3.Items.Add("📁 本地选择");
                
                // 设置默认选中云端自动匹配
                select3.SelectedIndex = 0;
                
                // 初始化云端服务
                InitializeCloudLoaderService();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("加载 Loader 列表异常: {0}", ex.Message));
            }
        }
        
        /// <summary>
        /// 初始化云端 Loader 服务
        /// </summary>
        private void InitializeCloudLoaderService()
        {
            var cloudService = SakuraEDL.Qualcomm.Services.CloudLoaderService.Instance;
            cloudService.SetLogger(
                msg => AppendLog(msg, Color.Cyan),
                msg => AppendLog(msg, Color.Gray)
            );
            // 配置 API 地址 (生产环境)
            // cloudService.ApiBase = "https://api.xiriacg.top/api";
            
            // 异步加载云端 Loader 列表
            LoadCloudLoadersAsync();
        }
        
        /// <summary>
        /// 异步加载云端 Loader 列表
        /// </summary>
        /// <param name="forceRefresh">是否强制刷新</param>
        private async void LoadCloudLoadersAsync(bool forceRefresh = false)
        {
            try
            {
                // 如果不是强制刷新且已加载，则跳过
                if (!forceRefresh && _cloudLoadersLoaded) return;
                
                var cloudService = SakuraEDL.Qualcomm.Services.CloudLoaderService.Instance;
                var loaders = await cloudService.GetLoaderListAsync();
                
                if (loaders != null && loaders.Count > 0)
                {
                    _cloudLoaders = loaders;
                    _cloudLoadersLoaded = true;
                    
                    // 更新 UI (在 UI 线程执行)
                    if (this.InvokeRequired)
                    {
                        this.BeginInvoke(new Action(() => UpdateCloudLoaderList()));
                    }
                    else
                    {
                        UpdateCloudLoaderList();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("加载云端 Loader 列表失败: {0}", ex.Message));
            }
        }
        
        /// <summary>
        /// 更新 select3 下拉框中的云端 Loader 列表
        /// </summary>
        private void UpdateCloudLoaderList()
        {
            try
            {
                // 保存当前选择
                string currentSelection = select3.Text;
                
                // 清空并重建列表
                select3.Items.Clear();
                
                // 添加默认选项
                select3.Items.Add("自动识别或自选引导");
                select3.Items.Add("── 本地选择 ──");
                select3.Items.Add("🔄 刷新云端列表");
                
                // 添加云端 Loader (按厂商分组)
                if (_cloudLoaders.Count > 0)
                {
                    select3.Items.Add("── 云端 Loader ──");
                    
                    // 按厂商分组
                    var grouped = _cloudLoaders
                        .GroupBy(l => l.Vendor ?? "其他")
                        .OrderBy(g => g.Key);
                    
                    foreach (var group in grouped)
                    {
                        foreach (var loader in group.OrderBy(l => l.Chip))
                        {
                            // 使用 API 返回的 DisplayName
                            select3.Items.Add(loader.ToString());
                        }
                    }
                }
                
                // 恢复选择或默认
                if (!string.IsNullOrEmpty(currentSelection) && select3.Items.Contains(currentSelection))
                {
                    select3.Text = currentSelection;
                }
                else
                {
                    select3.SelectedIndex = 0;
                }
                
                // 静默加载，不显示日志
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("更新云端 Loader 列表失败: {0}", ex.Message));
            }
        }
        
        /// <summary>
        /// 根据选择的云端 Loader 获取 CloudLoaderInfo
        /// </summary>
        private SakuraEDL.Qualcomm.Services.CloudLoaderInfo GetSelectedCloudLoader()
        {
            string selected = select3.Text;
            if (string.IsNullOrEmpty(selected)) return null;
            
            // 查找匹配的 Loader (使用 DisplayName 匹配)
            foreach (var loader in _cloudLoaders)
            {
                if (selected == loader.ToString())
                {
                    return loader;
                }
            }
            return null;
        }
        
        /// <summary>
        /// 构建 EDL Loader 列表项 (已废弃 - 使用云端匹配)
        /// </summary>
        [Obsolete("使用云端自动匹配替代本地 PAK 资源")]
        private List<string> BuildEdlLoaderItems()
        {
            // 不再构建本地 PAK 列表，完全使用云端匹配
            return new List<string>();
        }
        
        /// <summary>
        /// 获取品牌中文显示名
        /// </summary>
        private string GetBrandDisplayName(string brand)
        {
            switch (brand.ToLowerInvariant())
            {
                case "huawei": return "Huawei / Honor";
                case "zte": return "ZTE / Nubia / RedMagic";
                case "xiaomi": return "Xiaomi / Redmi";
                case "blackshark": return "Black Shark";
                case "vivo": return "vivo / iQOO";
                case "meizu": return "Meizu";
                case "lenovo": return "Lenovo / Motorola";
                case "samsung": return "Samsung";
                case "nothing": return "Nothing";
                case "rog": return "ASUS ROG";
                case "lg": return "LG";
                case "smartisan": return "Smartisan";
                case "xtc": return "XTC";
                case "360": return "360";
                case "bbk": return "BBK";
                case "royole": return "Royole";
                case "oplus": return "OPPO / OnePlus / realme";
                default: return brand;
            }
        }
        
        #endregion

        #region Fastboot 模块

        private void InitializeFastbootModule()
        {
            try
            {
                // 设置 fastboot.exe 路径
                string fastbootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fastboot.exe");
                FastbootCommand.SetFastbootPath(fastbootPath);

                // 创建 Fastboot UI 控制器
                _fastbootController = new FastbootUIController(
                    (msg, color) => AppendLog(msg, color),
                    msg => AppendLogDetail(msg));

                // 设置 listView5 支持复选框
                listView5.MultiSelect = true;
                listView5.CheckBoxes = true;
                listView5.FullRowSelect = true;

                // 绑定控件 - tabPage3 上的 Fastboot 控件
                // 注意: 设备信息标签(uiGroupBox3内)与高通模块共用，通过标签页切换来更新
                _fastbootController.BindControls(
                    deviceComboBox: uiComboBox1,          // 使用全局端口选择下拉框 (共用)
                    partitionListView: listView5,         // 分区列表
                    progressBar: uiProcessBar1,           // 总进度条 (共用)
                    subProgressBar: uiProcessBar2,        // 子进度条 (共用)
                    commandComboBox: uiComboBox2,         // 快捷命令下拉框 (device, unlock 等)
                    payloadTextBox: uiTextBox1,           // Payload 路径
                    outputPathTextBox: input1,            // 输出路径
                    // 设备信息标签 (uiGroupBox3 - 共用)
                    brandLabel: uiLabel9,                 // 品牌
                    chipLabel: uiLabel11,                 // 芯片
                    modelLabel: uiLabel3,                 // 型号
                    serialLabel: uiLabel10,               // 序列号
                    storageLabel: uiLabel13,              // 存储
                    unlockLabel: uiLabel14,               // 解锁状态
                    slotLabel: uiLabel12,                 // 槽位 (复用OTA版本标签)
                    // 时间/速度/操作标签 (共用)
                    timeLabel: uiLabel6,                  // 时间
                    speedLabel: uiLabel7,                 // 速度
                    operationLabel: uiLabel8,             // 当前操作
                    deviceCountLabel: uiLabel4,           // 设备数量 (复用)
                    // Checkbox 控件
                    autoRebootCheckbox: checkbox44,       // 自动重启
                    switchSlotCheckbox: checkbox41,       // 切换A槽
                    eraseGoogleLockCheckbox: checkbox43,  // 擦除谷歌锁
                    keepDataCheckbox: checkbox50,         // 保留数据
                    fbdFlashCheckbox: checkbox45,         // FBD刷写
                    unlockBlCheckbox: checkbox22,         // 解锁BL
                    lockBlCheckbox: checkbox21            // 锁定BL
                );

                // ========== tabPage3 Fastboot 页面按钮事件 ==========
                
                // uiButton11 = 解析Payload (本地文件或云端URL)
                uiButton11.Click += (s, e) => FastbootOpenPayloadDialog();

                // uiButton18 = 读取分区表 (同时读取设备信息)
                uiButton18.Click += async (s, e) => await FastbootReadPartitionTableWithInfoAsync();

                // uiButton19 = 提取镜像 (支持从 Payload 提取，自定义或全部)
                uiButton19.Click += async (s, e) => await FastbootExtractPartitionsWithOptionsAsync();

                // uiButton20 = 写入分区
                uiButton20.Click += async (s, e) => await FastbootFlashPartitionsAsync();

                // uiButton21 = 擦除分区
                uiButton21.Click += async (s, e) => await FastbootErasePartitionsAsync();

                // uiButton22 = 修复FBD (后续实现)
                uiButton22.Click += (s, e) => AppendLog("FBD 修复功能开发中...", Color.Orange);

                // uiButton10 = 执行 (执行刷机脚本或快捷命令)
                uiButton10.Click += async (s, e) => await FastbootExecuteAsync();

                // button8 = 浏览 (选择刷机脚本)
                button8.Click += (s, e) => FastbootSelectScript();

                // button9 = 浏览 (左键选择文件，右键选择文件夹)
                button9.Click += (s, e) => FastbootSelectPayloadFile();
                button9.MouseUp += (s, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                        FastbootSelectPayloadFolder();
                };

                // uiTextBox1 = Payload/URL 输入框，支持回车键触发解析
                uiTextBox1.Watermark = "选择Payload/文件夹 或 输入云端链接 (右键浏览=选择文件夹)";
                uiTextBox1.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        FastbootParsePayloadInput(uiTextBox1.Text);
                    }
                };

                // 修改按钮文字
                uiButton11.Text = "云端解析";
                uiButton18.Text = "读取分区表";
                uiButton19.Text = "提取分区";

                // checkbox22 = 解锁BL (手动操作时执行，脚本执行时作为标志)
                // checkbox21 = 锁定BL (手动操作时执行，脚本执行时作为标志)
                // 注意：不再自动执行，而是在刷机完成后根据选项执行

                // checkbox41 = 切换A槽 (刷写完成后执行)
                // checkbox43 = 擦除谷歌锁 (刷写完成后执行)
                // 这些复选框只作为标记，不立即执行操作

                // checkbox42 = 分区全选
                checkbox42.CheckedChanged += (s, e) => FastbootSelectAllPartitions(checkbox42.Checked);

                // listView5 双击选择镜像文件
                listView5.DoubleClick += (s, e) => FastbootPartitionDoubleClick();

                // select5 = 分区搜索
                select5.TextChanged += (s, e) => FastbootSearchPartition();
                select5.SelectedIndexChanged += (s, e) => { _fbIsSelectingFromDropdown = true; };

                // 注意：不在初始化时启动 Fastboot 设备监控
                // 只有当用户切换到 Fastboot 标签页时才启动监控
                // 避免覆盖高通端口列表
                // _fastbootController.StartDeviceMonitoring();

                // 绑定标签页切换事件 - 更新右侧设备信息显示
                tabs1.SelectedIndexChanged += OnTabPageChanged;

                AppendLog("Fastboot 模块初始化完成", Color.Gray);
            }
            catch (Exception ex)
            {
                AppendLog($"Fastboot 模块初始化失败: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// 标签页切换事件 - 切换设备信息显示和设备列表
        /// </summary>
        private void OnTabPageChanged(object sender, EventArgs e)
        {
            try
            {
                // 获取当前选中的标签页
                int selectedIndex = tabs1.SelectedIndex;
                var selectedTab = tabs1.Pages[selectedIndex];

                // tabPage3 是引导模式 (Fastboot)
                if (selectedTab == tabPage3)
                {
                    // 切换到 Fastboot 标签页
                    _isOnFastbootTab = true;
                    
                    // 停止其他模块监控
                    _portRefreshTimer?.Stop();
                    _mtkController?.StopPortMonitoring();
                    _spreadtrumController?.StopDeviceMonitor();
                    
                    // 更新 Fastboot 设备信息
                    if (_fastbootController != null)
                    {
                        // 启动 Fastboot 设备监控
                        _fastbootController.StartDeviceMonitoring();
                        _fastbootController.UpdateDeviceInfoLabels();
                        // Fastboot 设备数量在右侧信息面板显示，不覆盖系统信息
                    }
                }
                // tabPage2 是高通平台 (EDL)
                else if (selectedTab == tabPage2)
                {
                    // 切换到高通标签页
                    _isOnFastbootTab = false;
                    
                    // 恢复系统信息显示
                    uiLabel4.Text = _systemInfoText;
                    
                    // 停止其他模块监控
                    _fastbootController?.StopDeviceMonitoring();
                    _mtkController?.StopPortMonitoring();
                    _spreadtrumController?.StopDeviceMonitor();
                    
                    // 启动高通端口刷新定时器
                    _portRefreshTimer?.Start();
                    
                    // 刷新高通端口列表到下拉框
                    _qualcommController?.RefreshPorts(silent: true);
                    
                    // 恢复高通设备信息
                    if (_qualcommController != null && _qualcommController.IsConnected)
                    {
                        // 高通控制器会自动更新，这里不需要额外操作
                    }
                    else
                    {
                        // 重置为等待连接状态
                        uiLabel9.Text = "品牌：等待连接";
                        uiLabel11.Text = "芯片：等待连接";
                        uiLabel3.Text = "型号：等待连接";
                        uiLabel10.Text = "序列号：等待连接";
                        uiLabel13.Text = "存储：等待连接";
                        uiLabel14.Text = "型号：等待连接";
                        uiLabel12.Text = "OTA：等待连接";
                    }
                }
                // tabPage4 是联发科平台 (MTK)
                else if (selectedTab == tabPage4)
                {
                    // 切换到 MTK 标签页
                    _isOnFastbootTab = false;
                    
                    // 恢复系统信息显示
                    uiLabel4.Text = _systemInfoText;
                    
                    // 停止其他模块监控
                    _fastbootController?.StopDeviceMonitoring();
                    _portRefreshTimer?.Stop();
                    _spreadtrumController?.StopDeviceMonitor();
                    
                    // 启动 MTK 端口监控
                    _mtkController?.StartPortMonitoring();
                    
                    // 更新右侧信息面板为 MTK 专用
                    UpdateMtkInfoPanel();
                }
                // tabPage5 是展讯平台 (Spreadtrum)
                else if (selectedTab == tabPage5)
                {
                    // 切换到展讯标签页
                    _isOnFastbootTab = false;
                    
                    // 恢复系统信息显示
                    uiLabel4.Text = _systemInfoText;
                    
                    // 停止其他模块监控
                    _fastbootController?.StopDeviceMonitoring();
                    _portRefreshTimer?.Stop();
                    _mtkController?.StopPortMonitoring();
                    
                    // 启动展讯设备监控并刷新设备列表
                    _spreadtrumController?.RefreshDevices();
                    
                    // 更新右侧信息面板为展讯专用
                    UpdateSprdInfoPanel();
                }
                else
                {
                    // 其他标签页
                    _isOnFastbootTab = false;
                    
                    // 恢复系统信息显示
                    uiLabel4.Text = _systemInfoText;
                    
                    // 停止所有模块监控
                    _fastbootController?.StopDeviceMonitoring();
                    _mtkController?.StopPortMonitoring();
                    _spreadtrumController?.StopDeviceMonitor();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"标签页切换异常: {ex.Message}");
            }
        }

        /// <summary>
        /// Fastboot 云端链接解析
        /// </summary>
        private void FastbootOpenPayloadDialog()
        {
            // 如果文本框中已有内容，直接解析
            if (!string.IsNullOrWhiteSpace(uiTextBox1.Text))
            {
                FastbootParsePayloadInput(uiTextBox1.Text.Trim());
                return;
            }

            // 文本框为空时，提示用户在输入框中输入链接
            AppendLog("[提示] 请在上方输入框中粘贴 OTA 下载链接，然后点击云端解析", Color.Blue);
            uiTextBox1.Focus();
        }

        /// <summary>
        /// Fastboot 读取分区表 (同时读取设备信息)
        /// </summary>
        private async Task FastbootReadPartitionTableWithInfoAsync()
        {
            if (_fastbootController == null) return;

            if (!_fastbootController.IsConnected)
            {
                AppendLog("正在连接 Fastboot 设备...", Color.Blue);
                bool connected = await _fastbootController.ConnectAsync();
                if (!connected)
                {
                    AppendLog("连接失败，请检查设备是否处于 Fastboot 模式", Color.Red);
                    return;
                }
            }

            // 读取分区表和设备信息
            await _fastbootController.ReadPartitionTableAsync();
        }

        /// <summary>
        /// Fastboot 提取分区 (提取已勾选的分区)
        /// </summary>
        private async Task FastbootExtractPartitionsWithOptionsAsync()
        {
            if (_fastbootController == null) return;

            // 检查是否已加载 Payload (本地或云端)
            bool hasLocalPayload = _fastbootController.PayloadSummary != null;
            bool hasRemotePayload = _fastbootController.IsRemotePayloadLoaded;

            if (!hasLocalPayload && !hasRemotePayload)
            {
                AppendLog("请先解析 Payload (本地文件或云端链接)", Color.Orange);
                return;
            }

            // 让用户选择保存目录
            string outputDir;
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择分区提取保存目录";
                // 如果之前有选过目录，作为默认路径
                if (!string.IsNullOrEmpty(input1.Text) && Directory.Exists(input1.Text))
                {
                    fbd.SelectedPath = input1.Text;
                }
                
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    outputDir = fbd.SelectedPath;
                    input1.Text = outputDir;
                }
                else
                {
                    return;
                }
            }

            // 根据加载的类型选择提取方法
            if (hasRemotePayload)
            {
                await _fastbootController.ExtractSelectedRemotePartitionsAsync(outputDir);
            }
            else
            {
                await _fastbootController.ExtractSelectedPayloadPartitionsAsync(outputDir);
            }
        }

        /// <summary>
        /// Fastboot 读取设备信息 (保留兼容)
        /// </summary>
        private async Task FastbootReadInfoAsync()
        {
            await FastbootReadPartitionTableWithInfoAsync();
        }

        /// <summary>
        /// Fastboot 读取分区表 (保留兼容)
        /// </summary>
        private async Task FastbootReadPartitionTableAsync()
        {
            await FastbootReadPartitionTableWithInfoAsync();
        }

        /// <summary>
        /// Fastboot 刷写分区
        /// 支持: Payload.bin / URL 解析 / 已提取文件夹 / 普通镜像
        /// 刷写模式: 欧加刷写 / 纯 FBD / AB 通刷 / 普通刷写
        /// </summary>
        private async Task FastbootFlashPartitionsAsync()
        {
            if (_fastbootController == null) return;

            if (!_fastbootController.IsConnected)
            {
                AppendLog("请先连接 Fastboot 设备", Color.Orange);
                return;
            }

            // 检查刷写模式选项
            bool useOugaMode = checkbox7.Checked;      // 欧加刷写 = OnePlus/OPPO 主流程
            bool usePureFbdMode = checkbox45.Checked;  // FBD刷写 = 纯 FBD 模式
            bool switchSlotA = checkbox41.Checked;     // 切换A槽 = 刷写完成后执行 set_active a
            bool clearData = !checkbox50.Checked;      // 保留数据的反义
            bool eraseFrp = checkbox43.Checked;        // 擦除谷歌锁
            bool autoReboot = checkbox44.Checked;      // 自动重启

            // 只有勾选"欧加刷写"或"FBD刷写"时，才使用 OnePlus 刷写流程
            // "切换A槽"不再触发 OnePlus 流程，而是在普通刷写完成后执行 set_active
            if (useOugaMode || usePureFbdMode)
            {
                // OnePlus/OPPO 刷写模式 (支持 Payload/文件夹/镜像)
                string modeDesc = usePureFbdMode ? "纯 FBD" : "欧加刷写";
                AppendLog($"使用 OnePlus/OPPO {modeDesc}模式", Color.Blue);
                
                // 构建刷写分区列表 (支持 Payload 分区、解包文件夹、脚本任务、普通镜像)
                var partitions = _fastbootController.BuildOnePlusFlashPartitions();
                if (partitions.Count == 0)
                {
                    AppendLog("没有可刷写的分区（请解析 Payload 或选择镜像文件）", Color.Orange);
                    return;
                }

                // 显示分区来源统计
                int payloadCount = partitions.Count(p => p.IsPayloadPartition);
                int fileCount = partitions.Count - payloadCount;
                if (payloadCount > 0)
                    AppendLog($"已选择 {partitions.Count} 个分区 (Payload: {payloadCount}, 文件: {fileCount})", Color.Blue);

                // 构建刷写选项 (欧加模式默认使用 A 槽位)
                var options = new SakuraEDL.Fastboot.UI.FastbootUIController.OnePlusFlashOptions
                {
                    ABFlashMode = false,  // 不再使用 AB 通刷模式
                    PureFBDMode = usePureFbdMode,
                    PowerFlashMode = false,
                    ClearData = clearData,
                    EraseFrp = eraseFrp,
                    AutoReboot = autoReboot,
                    TargetSlot = "a"
                };

                // 执行 OnePlus 刷写流程 (自动提取 Payload 分区)
                await _fastbootController.ExecuteOnePlusFlashAsync(partitions, options);
            }
            else
            {
                // 未勾选欧加/FBD 模式时，使用普通刷写流程
                bool hasLocalPayload = _fastbootController.PayloadSummary != null;
                bool hasRemotePayload = _fastbootController.IsRemotePayloadLoaded;

                if (hasRemotePayload)
                {
                    // 云端 Payload 刷写 (边下载边刷写)
                    AppendLog("使用云端 Payload 普通刷写模式", Color.Blue);
                    await _fastbootController.FlashFromRemotePayloadAsync();
                }
                else if (hasLocalPayload)
                {
                    // 本地 Payload 刷写
                    AppendLog("使用本地 Payload 普通刷写模式", Color.Blue);
                    await _fastbootController.FlashFromPayloadAsync();
                }
                else
                {
                    // 普通刷写 (需要选择镜像文件)
                    await _fastbootController.FlashSelectedPartitionsAsync();
                }
            }
        }

        /// <summary>
        /// Fastboot 擦除分区
        /// </summary>
        private async Task FastbootErasePartitionsAsync()
        {
            if (_fastbootController == null) return;

            if (!_fastbootController.IsConnected)
            {
                AppendLog("请先连接 Fastboot 设备", Color.Orange);
                return;
            }

            await _fastbootController.EraseSelectedPartitionsAsync();
        }

        /// <summary>
        /// Fastboot 执行刷机脚本或快捷命令
        /// </summary>
        private async Task FastbootExecuteAsync()
        {
            if (_fastbootController == null) return;

            if (!_fastbootController.IsConnected)
            {
                bool connected = await _fastbootController.ConnectAsync();
                if (!connected) return;
            }

            // 优先级 1: 如果选中了快捷命令，直接执行命令
            if (_fastbootController.HasSelectedCommand())
            {
                await _fastbootController.ExecuteSelectedCommandAsync();
            }
            // 优先级 2: 如果有加载的刷机脚本任务，执行刷机脚本 (优先于 Payload)
            else if (_fastbootController.FlashTasks != null && _fastbootController.FlashTasks.Count > 0)
            {
                // 读取用户选项
                bool keepData = checkbox50.Checked;   // 保留数据
                bool lockBl = checkbox21.Checked;     // 锁定BL

                await _fastbootController.ExecuteFlashScriptAsync(keepData, lockBl);
            }
            // 优先级 3: 如果有加载的 Payload，执行 Payload 刷写
            else if (_fastbootController.IsPayloadLoaded)
            {
                await _fastbootController.FlashFromPayloadAsync();
            }
            // 优先级 4: 如果勾选了分区且有镜像文件，直接写入分区
            else if (_fastbootController.HasSelectedPartitionsWithFiles())
            {
                await _fastbootController.FlashSelectedPartitionsAsync();
            }
            else
            {
                // 什么都没选，提示用户
                AppendLog("请选择快捷命令、加载刷机脚本或勾选分区后再执行", Color.Orange);
            }
        }

        /// <summary>
        /// Fastboot 执行快捷命令
        /// </summary>
        private async Task FastbootExecuteCommandAsync()
        {
            if (_fastbootController == null) return;

            if (!_fastbootController.IsConnected)
            {
                bool connected = await _fastbootController.ConnectAsync();
                if (!connected) return;
            }

            await _fastbootController.ExecuteSelectedCommandAsync();
        }

        /// <summary>
        /// Fastboot 提取分区 (从 Payload 提取，支持本地和云端)
        /// </summary>
        private async Task FastbootExtractPartitionsAsync()
        {
            // 直接调用带选项的方法
            await FastbootExtractPartitionsWithOptionsAsync();
        }

        /// <summary>
        /// Fastboot 选择输出路径
        /// </summary>
        private void FastbootSelectOutputPath()
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择输出目录";
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    input1.Text = fbd.SelectedPath;
                    AppendLog($"输出路径: {fbd.SelectedPath}", Color.Blue);
                }
            }
        }

        /// <summary>
        /// Fastboot 选择 Payload 文件或文件夹
        /// </summary>
        private void FastbootSelectPayloadFile()
        {
            // 弹出选择对话框
            var result = MessageBox.Show(
                "请选择加载类型：\n\n" +
                "「是」选择 Payload/脚本 文件\n" +
                "「否」选择已提取的文件夹",
                "选择类型",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                // 选择文件
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = "选择 Payload 或刷机脚本";
                    ofd.Filter = "Payload|*.bin;*.zip|刷机脚本|*.bat;*.sh;*.cmd|所有文件|*.*";
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        uiTextBox1.Text = ofd.FileName;
                        FastbootParsePayloadInput(ofd.FileName);
                    }
                }
            }
            else
            {
                // 选择文件夹
                FastbootSelectPayloadFolder();
            }
        }

        /// <summary>
        /// Fastboot 选择已提取的文件夹
        /// </summary>
        private void FastbootSelectPayloadFolder()
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择已提取的固件文件夹 (包含 .img 文件)";
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    uiTextBox1.Text = fbd.SelectedPath;
                    FastbootParsePayloadInput(fbd.SelectedPath);
                }
            }
        }

        /// <summary>
        /// 解析 Payload 输入 (支持本地文件、文件夹和 URL)
        /// </summary>
        private void FastbootParsePayloadInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;

            input = input.Trim();

            // 判断是 URL、文件还是文件夹
            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // URL - 云端解析
                AppendLog($"检测到云端 URL，开始解析...", Color.Blue);
                _ = FastbootLoadPayloadFromUrlAsync(input);
            }
            else if (Directory.Exists(input))
            {
                // 已提取的文件夹
                AppendLog($"已选择文件夹: {input}", Color.Blue);
                _fastbootController?.LoadExtractedFolder(input);
            }
            else if (File.Exists(input))
            {
                // 本地文件
                AppendLog($"已选择: {Path.GetFileName(input)}", Color.Blue);

                string ext = Path.GetExtension(input).ToLowerInvariant();
                string fileName = Path.GetFileName(input).ToLowerInvariant();

                if (ext == ".bat" || ext == ".sh" || ext == ".cmd")
                {
                    // 刷机脚本
                    FastbootLoadScript(input);
                }
                else if (ext == ".bin" || ext == ".zip" || fileName == "payload.bin")
                {
                    // Payload 文件
                    _ = FastbootLoadPayloadAsync(input);
                }
            }
            else
            {
                AppendLog($"无效的输入: 文件/文件夹不存在或 URL 格式错误", Color.Red);
            }
        }

        /// <summary>
        /// Fastboot 加载 Payload 文件
        /// </summary>
        private async Task FastbootLoadPayloadAsync(string payloadPath)
        {
            if (_fastbootController == null) return;

            bool success = await _fastbootController.LoadPayloadAsync(payloadPath);
            
            if (success)
            {
                // 更新输出路径为 Payload 所在目录
                input1.Text = Path.GetDirectoryName(payloadPath);
                
                // 显示 Payload 摘要信息
                var summary = _fastbootController.PayloadSummary;
                if (summary != null)
                {
                    AppendLog($"[Payload] 分区数: {summary.PartitionCount}, 总大小: {summary.TotalSizeFormatted}", Color.Blue);
                }
            }
        }

        /// <summary>
        /// Fastboot 从 URL 加载云端 Payload
        /// </summary>
        private async Task FastbootLoadPayloadFromUrlAsync(string url)
        {
            if (_fastbootController == null) return;

            bool success = await _fastbootController.LoadPayloadFromUrlAsync(url);
            
            if (success)
            {
                // 显示远程 Payload 摘要信息
                var summary = _fastbootController.RemotePayloadSummary;
                if (summary != null)
                {
                    AppendLog($"[云端Payload] 分区数: {summary.PartitionCount}, 文件大小: {summary.TotalSizeFormatted}", Color.Blue);
                }
            }
        }

        /// <summary>
        /// Fastboot 选择刷机脚本文件
        /// </summary>
        private void FastbootSelectScript()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择刷机脚本 (flash_all.bat)";
                ofd.Filter = "刷机脚本|*.bat;*.sh;*.cmd|所有文件|*.*";
                
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    input1.Text = ofd.FileName;
                    AppendLog($"已选择脚本: {Path.GetFileName(ofd.FileName)}", Color.Blue);
                    
                    // 加载脚本
                    FastbootLoadScript(ofd.FileName);
                }
            }
        }

        /// <summary>
        /// Fastboot 加载刷机脚本
        /// </summary>
        private void FastbootLoadScript(string scriptPath)
        {
            if (_fastbootController == null) return;

            bool success = _fastbootController.LoadFlashScript(scriptPath);
            
            if (success)
            {
                // 更新输出路径为脚本所在目录
                input1.Text = Path.GetDirectoryName(scriptPath);

                // 根据脚本类型自动勾选对应选项
                AutoSelectOptionsFromScript(scriptPath);
            }
        }

        /// <summary>
        /// 根据脚本类型自动勾选 UI 选项
        /// </summary>
        private void AutoSelectOptionsFromScript(string scriptPath)
        {
            string fileName = Path.GetFileName(scriptPath).ToLowerInvariant();

            // 重置所有相关选项
            checkbox50.Checked = false;  // 保留数据
            checkbox21.Checked = false;  // 锁定BL

            // 根据脚本名称判断类型
            if (fileName.Contains("except_storage") || fileName.Contains("except-storage") || 
                fileName.Contains("keep_data") || fileName.Contains("keepdata"))
            {
                // 保留数据刷机脚本
                checkbox50.Checked = true;
                AppendLog("检测到保留数据脚本，已勾选「保留数据」", Color.Blue);
            }
            else if (fileName.Contains("_lock") || fileName.Contains("-lock") || 
                     fileName.EndsWith("lock.bat") || fileName.EndsWith("lock.sh"))
            {
                // 锁定BL刷机脚本
                checkbox21.Checked = true;
                AppendLog("检测到锁定BL脚本，已勾选「锁定BL」", Color.Blue);
            }
            else
            {
                // 普通刷机脚本 (flash_all.bat)
                AppendLog("普通刷机脚本，将清除所有数据", Color.Orange);
            }
        }

        /// <summary>
        /// Fastboot 分区全选/取消全选
        /// </summary>
        private void FastbootSelectAllPartitions(bool selectAll)
        {
            foreach (ListViewItem item in listView5.Items)
            {
                item.Checked = selectAll;
            }
        }

        /// <summary>
        /// Fastboot 分区双击选择镜像
        /// </summary>
        private void FastbootPartitionDoubleClick()
        {
            if (listView5.SelectedItems.Count == 0) return;
            
            var selectedItem = listView5.SelectedItems[0];
            _fastbootController?.SelectImageForPartition(selectedItem);
        }

        // Fastboot 分区搜索相关变量
        private string _fbLastSearchKeyword = "";
        private List<ListViewItem> _fbSearchMatches = new List<ListViewItem>();
        private int _fbCurrentMatchIndex = 0;
        private bool _fbIsSelectingFromDropdown = false;

        /// <summary>
        /// Fastboot 分区搜索
        /// </summary>
        private void FastbootSearchPartition()
        {
            // 如果是从下拉选择触发的，直接定位
            if (_fbIsSelectingFromDropdown)
            {
                _fbIsSelectingFromDropdown = false;
                string selectedName = select5.Text?.Trim()?.ToLower();
                if (!string.IsNullOrEmpty(selectedName))
                {
                    FastbootLocatePartitionByName(selectedName);
                }
                return;
            }

            string keyword = select5.Text?.Trim()?.ToLower() ?? "";
            
            // 如果搜索框为空，重置所有高亮
            if (string.IsNullOrEmpty(keyword))
            {
                FastbootResetPartitionHighlights();
                _fbLastSearchKeyword = "";
                _fbSearchMatches.Clear();
                _fbCurrentMatchIndex = 0;
                return;
            }
            
            // 如果关键词相同，跳转到下一个匹配项
            if (keyword == _fbLastSearchKeyword && _fbSearchMatches.Count > 1)
            {
                FastbootJumpToNextMatch();
                return;
            }
            
            _fbLastSearchKeyword = keyword;
            _fbSearchMatches.Clear();
            _fbCurrentMatchIndex = 0;
            
            // 收集匹配的分区名称用于下拉建议
            var suggestions = new List<string>();
            
            listView5.BeginUpdate();
            
            foreach (ListViewItem item in listView5.Items)
            {
                string partName = item.SubItems[0].Text.ToLower();
                
                if (partName.Contains(keyword))
                {
                    // 高亮匹配的项
                    item.BackColor = Color.LightYellow;
                    _fbSearchMatches.Add(item);
                    
                    // 添加到建议列表
                    if (!suggestions.Contains(item.SubItems[0].Text))
                    {
                        suggestions.Add(item.SubItems[0].Text);
                    }
                }
                else
                {
                    item.BackColor = Color.Transparent;
                }
            }
            
            listView5.EndUpdate();
            
            // 更新下拉建议列表
            FastbootUpdateSearchSuggestions(suggestions);
            
            // 滚动到第一个匹配项
            if (_fbSearchMatches.Count > 0)
            {
                _fbSearchMatches[0].Selected = true;
                _fbSearchMatches[0].EnsureVisible();
                _fbCurrentMatchIndex = 0;
            }
        }

        private void FastbootJumpToNextMatch()
        {
            if (_fbSearchMatches.Count == 0) return;
            
            // 取消当前选中项的选中状态
            if (_fbCurrentMatchIndex < _fbSearchMatches.Count)
            {
                _fbSearchMatches[_fbCurrentMatchIndex].Selected = false;
            }
            
            // 移动到下一个
            _fbCurrentMatchIndex = (_fbCurrentMatchIndex + 1) % _fbSearchMatches.Count;
            
            // 选中并滚动到新的匹配项
            _fbSearchMatches[_fbCurrentMatchIndex].Selected = true;
            _fbSearchMatches[_fbCurrentMatchIndex].EnsureVisible();
        }

        private void FastbootResetPartitionHighlights()
        {
            listView5.BeginUpdate();
            foreach (ListViewItem item in listView5.Items)
            {
                item.BackColor = Color.Transparent;
            }
            listView5.EndUpdate();
        }

        private void FastbootUpdateSearchSuggestions(List<string> suggestions)
        {
            string currentText = select5.Text;
            
            select5.Items.Clear();
            foreach (var name in suggestions)
            {
                select5.Items.Add(name);
            }
            
            select5.Text = currentText;
        }

        private void FastbootLocatePartitionByName(string partitionName)
        {
            FastbootResetPartitionHighlights();
            
            foreach (ListViewItem item in listView5.Items)
            {
                if (item.SubItems[0].Text.Equals(partitionName, StringComparison.OrdinalIgnoreCase))
                {
                    item.BackColor = Color.LightYellow;
                    item.Selected = true;
                    item.EnsureVisible();
                    break;
                }
            }
        }

        #endregion

        #region 快捷操作 (设备管理器)
        
        /// <summary>
        /// 快捷重启系统 (优先 Fastboot，备选 ADB)
        /// </summary>
        private async Task QuickRebootSystemAsync()
        {
            AppendLog("执行: 重启系统...", Color.Cyan);
            
            // 优先尝试 Fastboot
            if (_fastbootController != null && _fastbootController.IsConnected)
            {
                bool ok = await _fastbootController.RebootAsync();
                if (ok)
                {
                    AppendLog("Fastboot: 重启成功", Color.Green);
                    return;
                }
            }
            
            // 备选 ADB
            var result = await SakuraEDL.Fastboot.Common.AdbHelper.RebootAsync();
            if (result.Success)
                AppendLog("ADB: 重启成功", Color.Green);
            else
                AppendLog($"重启失败: {result.Error}", Color.Red);
        }
        
        /// <summary>
        /// 快捷重启到 Bootloader (优先 Fastboot，备选 ADB)
        /// </summary>
        private async Task QuickRebootBootloaderAsync()
        {
            AppendLog("执行: 重启到 Fastboot...", Color.Cyan);
            
            // 优先尝试 Fastboot
            if (_fastbootController != null && _fastbootController.IsConnected)
            {
                bool ok = await _fastbootController.RebootBootloaderAsync();
                if (ok)
                {
                    AppendLog("Fastboot: 重启到 Bootloader 成功", Color.Green);
                    return;
                }
            }
            
            // 备选 ADB
            var result = await SakuraEDL.Fastboot.Common.AdbHelper.RebootBootloaderAsync();
            if (result.Success)
                AppendLog("ADB: 重启到 Bootloader 成功", Color.Green);
            else
                AppendLog($"重启失败: {result.Error}", Color.Red);
        }
        
        /// <summary>
        /// 快捷重启到 Fastbootd (优先 Fastboot，备选 ADB)
        /// </summary>
        private async Task QuickRebootFastbootdAsync()
        {
            AppendLog("执行: 重启到 Fastbootd...", Color.Cyan);
            
            // 优先尝试 Fastboot
            if (_fastbootController != null && _fastbootController.IsConnected)
            {
                bool ok = await _fastbootController.RebootFastbootdAsync();
                if (ok)
                {
                    AppendLog("Fastboot: 重启到 Fastbootd 成功", Color.Green);
                    return;
                }
            }
            
            // 备选 ADB
            var result = await SakuraEDL.Fastboot.Common.AdbHelper.RebootFastbootAsync();
            if (result.Success)
                AppendLog("ADB: 重启到 Fastbootd 成功", Color.Green);
            else
                AppendLog($"重启失败: {result.Error}", Color.Red);
        }
        
        /// <summary>
        /// 快捷重启到 Recovery (优先 Fastboot，备选 ADB)
        /// </summary>
        private async Task QuickRebootRecoveryAsync()
        {
            AppendLog("执行: 重启到 Recovery...", Color.Cyan);
            
            // 优先尝试 Fastboot
            if (_fastbootController != null && _fastbootController.IsConnected)
            {
                bool ok = await _fastbootController.RebootRecoveryAsync();
                if (ok)
                {
                    AppendLog("Fastboot: 重启到 Recovery 成功", Color.Green);
                    return;
                }
            }
            
            // 备选 ADB
            var result = await SakuraEDL.Fastboot.Common.AdbHelper.RebootRecoveryAsync();
            if (result.Success)
                AppendLog("ADB: 重启到 Recovery 成功", Color.Green);
            else
                AppendLog($"重启失败: {result.Error}", Color.Red);
        }
        
        /// <summary>
        /// MI踢EDL - Fastboot OEM EDL (仅限小米设备)
        /// </summary>
        private async Task QuickMiRebootEdlAsync()
        {
            AppendLog("执行: MI踢EDL (fastboot oem edl)...", Color.Cyan);
            
            if (_fastbootController == null || !_fastbootController.IsConnected)
            {
                AppendLog("请先连接 Fastboot 设备", Color.Orange);
                return;
            }
            
            bool ok = await _fastbootController.OemEdlAsync();
            if (ok)
                AppendLog("MI踢EDL: 成功，设备将进入 EDL 模式", Color.Green);
            else
                AppendLog("MI踢EDL: 失败，设备可能不支持此命令", Color.Red);
        }
        
        /// <summary>
        /// 联想或安卓踢EDL - ADB reboot edl
        /// </summary>
        private async Task QuickAdbRebootEdlAsync()
        {
            AppendLog("执行: 联想/安卓踢EDL (adb reboot edl)...", Color.Cyan);
            
            var result = await SakuraEDL.Fastboot.Common.AdbHelper.RebootEdlAsync();
            if (result.Success)
                AppendLog("ADB: 踢EDL成功，设备将进入 EDL 模式", Color.Green);
            else
                AppendLog($"踢EDL失败: {result.Error}", Color.Red);
        }
        
        /// <summary>
        /// 擦除谷歌锁 (Fastboot erase frp)
        /// </summary>
        private async Task QuickEraseFrpAsync()
        {
            AppendLog("执行: 擦除谷歌锁 (fastboot erase frp)...", Color.Cyan);
            
            if (_fastbootController == null || !_fastbootController.IsConnected)
            {
                AppendLog("请先连接 Fastboot 设备", Color.Orange);
                return;
            }
            
            bool ok = await _fastbootController.EraseFrpAsync();
            if (ok)
                AppendLog("擦除谷歌锁: 成功", Color.Green);
            else
                AppendLog("擦除谷歌锁: 失败，设备可能已锁定 Bootloader", Color.Red);
        }
        
        /// <summary>
        /// 切换槽位 (Fastboot set_active)
        /// </summary>
        private async Task QuickSwitchSlotAsync()
        {
            AppendLog("执行: 切换槽位...", Color.Cyan);
            
            if (_fastbootController == null || !_fastbootController.IsConnected)
            {
                AppendLog("请先连接 Fastboot 设备", Color.Orange);
                return;
            }
            
            // 获取当前槽位
            string currentSlot = await _fastbootController.GetCurrentSlotAsync();
            if (string.IsNullOrEmpty(currentSlot))
            {
                AppendLog("无法获取当前槽位，设备可能不支持 A/B 分区", Color.Orange);
                return;
            }
            
            // 切换到另一个槽位
            string targetSlot = currentSlot == "a" ? "b" : "a";
            AppendLog($"当前槽位: {currentSlot}，切换到: {targetSlot}", Color.White);
            
            bool ok = await _fastbootController.SetActiveSlotAsync(targetSlot);
            if (ok)
                AppendLog($"切换槽位成功: {currentSlot} -> {targetSlot}", Color.Green);
            else
                AppendLog("切换槽位失败", Color.Red);
        }
        
        #endregion
        
        #region 其他功能菜单
        
        /// <summary>
        /// 打开设备管理器
        /// </summary>
        private void OpenDeviceManager()
        {
            try
            {
                System.Diagnostics.Process.Start("devmgmt.msc");
                AppendLog("已打开设备管理器", Color.Blue);
            }
            catch (Exception ex)
            {
                AppendLog($"打开设备管理器失败: {ex.Message}", Color.Red);
            }
        }
        
        /// <summary>
        /// 打开 CMD 命令行 (在程序目录下，管理员权限)
        /// </summary>
        private void OpenCommandPrompt()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true,
                    Verb = "runas"  // 以管理员权限运行
                };
                System.Diagnostics.Process.Start(psi);
                AppendLog($"已打开管理员命令行: {psi.WorkingDirectory}", Color.Blue);
            }
            catch (Exception ex)
            {
                // 用户可能取消了 UAC 提示
                if (ex.Message.Contains("canceled") || ex.Message.Contains("取消"))
                    AppendLog("用户取消了管理员权限请求", Color.Orange);
                else
                    AppendLog($"打开命令行失败: {ex.Message}", Color.Red);
            }
        }
        
        /// <summary>
        /// 打开驱动安装程序 (支持云端下载)
        /// </summary>
        /// <param name="driverType">驱动类型: android, mtk, qualcomm, spreadtrum</param>
        private async void OpenDriverInstaller(string driverType)
        {
            try
            {
                // 转换为 DriverType 枚举
                DriverType? type = null;
                string driverName = null;
                
                switch (driverType.ToLower())
                {
                    case "qualcomm":
                        type = DriverType.Qualcomm;
                        driverName = "高通 9008 驱动";
                        break;
                    case "mtk":
                        type = DriverType.MediaTek;
                        driverName = "MTK USB 驱动";
                        break;
                    case "spreadtrum":
                        type = DriverType.Spreadtrum;
                        driverName = "展锐 USB 驱动";
                        break;
                    case "android":
                        // 安卓驱动暂不支持云端下载，使用旧逻辑
                        OpenAndroidDriver();
                        return;
                }

                if (type == null)
                {
                    AppendLog($"不支持的驱动类型: {driverType}", Color.Orange);
                    return;
                }

                // 创建驱动下载服务
                var driverService = new DriverDownloadService(
                    msg => AppendLog(msg, Color.Blue),
                    progress => {
                        // 更新进度条
                        if (uiProcessBar1.InvokeRequired)
                            uiProcessBar1.Invoke(new Action(() => uiProcessBar1.Value = progress));
                        else
                            uiProcessBar1.Value = progress;
                    },
                    detailProgress => {
                        // 更新详细进度 (速度、已下载大小等)
                        if (InvokeRequired)
                        {
                            Invoke(new Action(() => UpdateDriverDownloadProgress(detailProgress)));
                        }
                        else
                        {
                            UpdateDriverDownloadProgress(detailProgress);
                        }
                    }
                );

                // 直接从云端下载驱动 (始终下载最新版本)
                uiButton1.Enabled = false;
                AppendLog($"正在从云端下载 {driverName}，请稍候...", Color.Cyan);

                try
                {
                    // 下载驱动
                    bool downloaded = await driverService.DownloadDriverAsync(type.Value);
                    
                    if (!downloaded)
                    {
                        AppendLog($"{driverName} 下载失败", Color.Red);
                        MessageBox.Show(
                            $"{driverName} 下载失败。\n\n请检查网络连接，或前往官网手动下载：\nhttps://sakuraedl.org/download",
                            "下载失败",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );
                        return;
                    }

                    // 以管理员权限运行安装程序
                    bool success = driverService.RunDriverAsAdmin(type.Value);
                    
                    if (success)
                    {
                        AppendLog($"{driverName} 安装程序已启动", Color.Green);
                    }
                    else
                    {
                        AppendLog($"{driverName} 启动失败，请手动运行", Color.Orange);
                        // 打开驱动所在目录
                        string driverPath = driverService.GetLocalDriverPath(type.Value);
                        if (File.Exists(driverPath))
                        {
                            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{driverPath}\"");
                        }
                    }
                }
                finally
                {
                    uiButton1.Enabled = true;
                    uiProcessBar1.Value = 0;
                    uiLabel7.Text = "速度：--";
                    uiLabel8.Text = "当前操作：待命";
                    // 恢复一言
                    _ = LoadHitokotoAsync();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"驱动安装失败: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// 打开安卓驱动 (旧逻辑，不支持云端下载)
        /// </summary>
        private void OpenAndroidDriver()
        {
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] androidPaths = {
                    Path.Combine(appDir, "drivers", "android_usb_driver.exe"),
                    Path.Combine(appDir, "drivers", "adb_driver.exe"),
                    Path.Combine(appDir, "ADB_Driver.exe")
                };
                string driverPath = androidPaths.FirstOrDefault(File.Exists);

                if (string.IsNullOrEmpty(driverPath))
                {
                    AppendLog("安卓驱动安装程序未找到，请手动安装", Color.Orange);
                    MessageBox.Show("安卓驱动安装程序未找到。\n\n请前往官方网站下载对应驱动。",
                        "驱动未找到", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                System.Diagnostics.Process.Start(driverPath);
                AppendLog("已启动安卓驱动安装程序", Color.Blue);
            }
            catch (Exception ex)
            {
                AppendLog($"启动安卓驱动安装程序失败: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// 更新驱动下载进度显示
        /// </summary>
        private void UpdateDriverDownloadProgress(DownloadProgress progress)
        {
            // 更新进度条
            uiProcessBar1.Value = progress.Percentage;
            
            // 更新速度标签
            uiLabel7.Text = $"速度：{progress.SpeedText}";
            
            // 更新当前操作标签 (uiLabel8) - 紧凑格式避免截断
            uiLabel8.Text = $"下载驱动 [{progress.Percentage}%]";
        }

        #endregion

        private void checkbox22_CheckedChanged(object sender, AntdUI.BoolEventArgs e)
        {

        }

        private void mtkBtnConnect_Click_1(object sender, EventArgs e)
        {

        }
    }
}
