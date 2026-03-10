// ============================================================================
// SakuraEDL - Main Window (WPF)
// ============================================================================


using OPFlashTool.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Microsoft.VisualBasic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SakuraEDL.Qualcomm.UI;
using SakuraEDL.Qualcomm.Common;
using SakuraEDL.Qualcomm.Models;
using SakuraEDL.Qualcomm.Services;
using SakuraEDL.Fastboot.UI;
using SakuraEDL.Fastboot.Common;
using SakuraEDL.Qualcomm.Database;
using SakuraEDL.Common;
using DrawColor = System.Drawing.Color;
using Color = System.Drawing.Color;
using DrawingImage = System.Drawing.Image;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using DialogResult = System.Windows.Forms.DialogResult;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using FormsListViewItem = System.Windows.Forms.ListViewItem;
using FormsMessageBox = System.Windows.Forms.MessageBox;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;

namespace SakuraEDL
{
    public partial class MainWindow : Window
    {
        private const int MaxUiLogBlocks = 1200;
        private const int MaxUiLogsPerFlush = 80;
        private string logFilePath;
        private string selectedLocalImagePath = "";
        #pragma warning disable CS0414
        private string input8OriginalText = "";
        #pragma warning restore CS0414
        private bool isEnglish = false;
 
        // 图片URL历史记录
        private List<string> urlHistory = new List<string>();

        // 图片预览缓存
        private List<DrawingImage> previewImages = new List<DrawingImage>();
        private const int MAX_PREVIEW_IMAGES = 5; // 最多保存5个预览

        // 系统信息（用于恢复 uiLabel4）
        private string _systemInfoText = "Computer: Unknown";

        // 原始控件位置和大小

        // 高通 UI 控制器
        private QualcommUIController _qualcommController;
        private DispatcherTimer _portRefreshTimer;
        private string _lastPortList = "";
        private int _lastEdlCount = 0;
        private bool _isOnFastbootTab = false;  // 当前是否在 Fastboot 标签页
        private string _selectedXmlDirectory = "";  // 存储选择的 XML 文件所在目录

        // Fastboot UI 控制器
        private FastbootUIController _fastbootController;

        // 云端 Loader 列表
        private List<SakuraEDL.Qualcomm.Services.CloudLoaderInfo> _cloudLoaders = new List<SakuraEDL.Qualcomm.Services.CloudLoaderInfo>();
        private bool _cloudLoadersLoaded = false;
        private readonly ConcurrentQueue<(string Message, MediaColor Color)> _uiLogQueue = new();
        private readonly ConcurrentQueue<string> _fileLogQueue = new();
        private readonly object _logFileLock = new();
        private DispatcherTimer _logFlushTimer;
        private int _fileFlushInProgress;
        private int _uiFlushScheduled;
        private bool _eventsBound;
        private bool _startupModulesInitialized;
        private string _authMode = "none";
        private string _cloudLoaderAuthType = "none";
        private readonly WpfListViewAdapter _qualcommListViewAdapter;
        private readonly WpfListViewAdapter _fastbootListViewAdapter;

        private sealed class WpfListViewRow : INotifyPropertyChanged
        {
            private readonly FormsListViewItem _sourceItem;

            public WpfListViewRow(FormsListViewItem sourceItem)
            {
                _sourceItem = sourceItem;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public FormsListViewItem SourceItem => _sourceItem;

            public bool IsChecked
            {
                get => _sourceItem.Checked;
                set
                {
                    if (_sourceItem.Checked == value)
                    {
                        return;
                    }

                    _sourceItem.Checked = value;
                    OnPropertyChanged();
                }
            }

            public string Column0 => GetColumnText(0);
            public string Column1 => GetColumnText(1);
            public string Column2 => GetColumnText(2);
            public string Column3 => GetColumnText(3);
            public string Column4 => GetColumnText(4);
            public string Column5 => GetColumnText(5);
            public string Column6 => GetColumnText(6);
            public string Column7 => GetColumnText(7);
            public string Column8 => GetColumnText(8);
            public MediaBrush RowBackground => ToBrush(_sourceItem.BackColor, System.Windows.Media.Brushes.Transparent);
            public MediaBrush RowForeground => ToBrush(_sourceItem.ForeColor, System.Windows.SystemColors.ControlTextBrush);

            public void Refresh()
            {
                OnPropertyChanged(nameof(IsChecked));
                OnPropertyChanged(nameof(Column0));
                OnPropertyChanged(nameof(Column1));
                OnPropertyChanged(nameof(Column2));
                OnPropertyChanged(nameof(Column3));
                OnPropertyChanged(nameof(Column4));
                OnPropertyChanged(nameof(Column5));
                OnPropertyChanged(nameof(Column6));
                OnPropertyChanged(nameof(Column7));
                OnPropertyChanged(nameof(Column8));
                OnPropertyChanged(nameof(RowBackground));
                OnPropertyChanged(nameof(RowForeground));
            }

            private string GetColumnText(int index)
            {
                if (index == 0)
                {
                    return _sourceItem.Text ?? string.Empty;
                }

                return _sourceItem.SubItems.Count > index
                    ? _sourceItem.SubItems[index].Text ?? string.Empty
                    : string.Empty;
            }

            private static MediaBrush ToBrush(DrawColor color, MediaBrush fallback)
            {
                if (color.IsEmpty)
                {
                    return fallback;
                }

                return new SolidColorBrush(MediaColor.FromArgb(color.A, color.R, color.G, color.B));
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private sealed class WpfListViewAdapter
        {
            private sealed class AdapterItemCollection : Collection<FormsListViewItem>
            {
                private readonly WpfListViewAdapter _owner;

                public AdapterItemCollection(WpfListViewAdapter owner)
                {
                    _owner = owner;
                }

                protected override void ClearItems()
                {
                    base.ClearItems();
                    _owner.Refresh();
                }

                protected override void InsertItem(int index, FormsListViewItem item)
                {
                    base.InsertItem(index, item);
                    _owner.Refresh();
                }

                protected override void RemoveItem(int index)
                {
                    base.RemoveItem(index);
                    _owner.Refresh();
                }

                protected override void SetItem(int index, FormsListViewItem item)
                {
                    base.SetItem(index, item);
                    _owner.Refresh();
                }
            }

            private readonly System.Windows.Controls.ListView _view;
            private readonly ObservableCollection<WpfListViewRow> _rows = new();
            private readonly AdapterItemCollection _items;
            private int _updateDepth;
            private bool _syncingSelection;
            private bool _refreshPending;

            public WpfListViewAdapter(System.Windows.Controls.ListView view)
            {
                _view = view;
                _items = new AdapterItemCollection(this);
                _view.ItemsSource = _rows;
                _view.SelectionChanged += OnSelectionChanged;
                MultiSelect = true;
            }

            public bool CheckBoxes { get; set; }
            public bool FullRowSelect { get; set; }

            public bool MultiSelect
            {
                get => _view.SelectionMode != SelectionMode.Single;
                set => _view.SelectionMode = value ? SelectionMode.Extended : SelectionMode.Single;
            }

            public bool InvokeRequired => !_view.Dispatcher.CheckAccess();

            public IList<FormsListViewItem> CheckedItems => _items.Where(item => item.Checked).ToList();

            public IList<FormsListViewItem> SelectedItems => _view.SelectedItems
                .Cast<WpfListViewRow>()
                .Select(row => row.SourceItem)
                .ToList();

            public Collection<FormsListViewItem> Items => _items;

            public void BeginInvoke(Action action)
            {
                _view.Dispatcher.BeginInvoke(action);
            }

            public void Invoke(Action action)
            {
                _view.Dispatcher.Invoke(action);
            }

            public void BeginUpdate()
            {
                _updateDepth++;
            }

            public void EndUpdate()
            {
                if (_updateDepth > 0)
                {
                    _updateDepth--;
                }

                if (_updateDepth == 0)
                {
                    Refresh();
                }
            }

            public void Refresh()
            {
                if (_updateDepth > 0)
                {
                    return;
                }

                if (_refreshPending)
                {
                    return;
                }

                _refreshPending = true;
                _view.Dispatcher.BeginInvoke(new Action(RefreshCore), DispatcherPriority.Background);
            }

            private void RefreshCore()
            {
                _refreshPending = false;

                if (_updateDepth > 0)
                {
                    return;
                }

                _syncingSelection = true;
                try
                {
                    _rows.Clear();
                    foreach (FormsListViewItem item in _items)
                    {
                        _rows.Add(new WpfListViewRow(item));
                    }

                    _view.SelectedItems.Clear();
                    foreach (WpfListViewRow row in _rows.Where(row => row.SourceItem.Selected))
                    {
                        _view.SelectedItems.Add(row);
                    }
                }
                finally
                {
                    _syncingSelection = false;
                }
            }

            public void ScrollIntoView(FormsListViewItem item)
            {
                var row = _rows.FirstOrDefault(candidate => ReferenceEquals(candidate.SourceItem, item));
                if (row != null)
                {
                    _view.ScrollIntoView(row);
                }
            }

            private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
            {
                if (_syncingSelection)
                {
                    return;
                }

                foreach (WpfListViewRow row in _rows)
                {
                    row.SourceItem.Selected = _view.SelectedItems.Contains(row);
                }
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            _qualcommListViewAdapter = new WpfListViewAdapter(listView2);
            _fastbootListViewAdapter = new WpfListViewAdapter(listView5);
            InitializeLogQueue();
            InitializeLogSystem();
            LanguageManager.Initialize();

            Loaded += async (_, __) => await OnWindowLoadedAsync();
            Closing += Form1_Closing;

            checkbox14.IsChecked = true;
            radio3.IsChecked = true;
             
            // 绑定按钮事件
            button2.Click += Button2_Click;
            button3.Click += Button3_Click;
            slider1.ValueChanged += Slider1_ValueChanged;
              
            // 添加 select3 事件绑定
            select3.SelectionChanged += Select3_SelectedIndexChanged;

            // 添加 checkbox17 和checkbox19 事件绑定
            checkbox17.Checked += Checkbox17_CheckedChanged;
            checkbox17.Unchecked += Checkbox17_CheckedChanged;
            checkbox19.Checked += Checkbox19_CheckedChanged;
            checkbox19.Unchecked += Checkbox19_CheckedChanged;
        }

        private async Task OnWindowLoadedAsync()
        {
            BindCommonEvents();
            HideLanguageSelector();
            await InitializeWindowStateAsync();
            await InitializeStartupModulesAsync();
            WriteLogHeader($"Computer: {Environment.MachineName}");
            AppendLog("Main window loaded", DrawColor.LimeGreen);
            await LoadHitokotoAsync();
        }

        private async Task InitializeStartupModulesAsync()
        {
            if (_startupModulesInitialized)
            {
                return;
            }

            _startupModulesInitialized = true;

            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            InitializeQualcommModule();

            await Dispatcher.Yield(DispatcherPriority.Background);
            InitializeFastbootModule();

            await Dispatcher.Yield(DispatcherPriority.Background);
            InitializeEdlLoaderList();

            await Dispatcher.Yield(DispatcherPriority.Background);
            InitializeSpreadtrumModule();

            await Dispatcher.Yield(DispatcherPriority.Background);
            InitializeMediaTekModule();
        }

        private async Task InitializeWindowStateAsync()
        {
            try
            {
                ApplyLanguage();

                string sysInfo = PreloadManager.SystemInfo ?? "Unknown";
                _systemInfoText = LanguageManager.T("status.computer") + ": " + sysInfo;
                uiLabel4.Content = _systemInfoText;

                WriteLogHeader(sysInfo);
                AppendLog(LanguageManager.T("log.loaded"), DrawColor.Green);
            }
            catch (Exception ex)
            {
                uiLabel4.Content = LanguageManager.TranslateLegacyText($"系统信息错误: {ex.Message}");
                AppendLog($"初始化失败: {ex.Message}", DrawColor.Red);
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
        private void InitializeLogQueue()
        {
            _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _logFlushTimer.Tick += (_, __) => FlushUiLogs();
            _logFlushTimer.Start();
        }

        private void InitializeLogSystem()
        {
            try
            {
                string logFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                Directory.CreateDirectory(logFolderPath);
                string logFileName = $"{DateTime.Now:yyyy-MM-dd_HH.mm.ss}_log.txt";
                logFilePath = Path.Combine(logFolderPath, logFileName);
            }
            catch
            {
                logFilePath = Path.Combine(Path.GetTempPath(), $"SakuraEDL_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }
        }

        private async Task LoadHitokotoAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var response = await client.GetAsync("https://hitokoto.cn/text");
                if (response.IsSuccessStatusCode)
                {
                    string quote = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(quote))
                    {
                        SetContentText(uiLabel2, quote.Trim());
                    }
                }
            }
            catch
            {
            }
        }

        
        private void WriteLogHeader(string systemInfo)
        {
            try
            {
                systemInfo = LanguageManager.TranslateLegacyText(systemInfo);
                if (string.IsNullOrWhiteSpace(logFilePath))
                {
                    return;
                }

                File.AppendAllLines(logFilePath, new[]
                {
                    "========== SakuraEDL Log ==========",
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"System: {systemInfo}",
                    "Version: v3.0",
                    "==================================="
                }, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private void AppendLog(string message, Color? color = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            message = LanguageManager.TranslateLegacyText(message);
            _uiLogQueue.Enqueue((message, ConvertColor(color) ?? MediaColor.FromRgb(255, 255, 255)));
            _fileLogQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            ScheduleFileLogFlush();
            RequestUiLogFlush();
        }

        private void RequestUiLogFlush()
        {
            if (_logFlushTimer == null)
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                if (!_logFlushTimer.IsEnabled)
                {
                    _logFlushTimer.Start();
                }

                if (_uiLogQueue.Count >= MaxUiLogsPerFlush)
                {
                    FlushUiLogs();
                }

                return;
            }

            if (Interlocked.Exchange(ref _uiFlushScheduled, 1) != 0)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _uiFlushScheduled, 0);

                if (!_logFlushTimer.IsEnabled)
                {
                    _logFlushTimer.Start();
                }

                if (_uiLogQueue.Count >= MaxUiLogsPerFlush)
                {
                    FlushUiLogs();
                }
            }), DispatcherPriority.Background);
        }

        private void FlushUiLogs()
        {
            if (uiRichTextBox1 == null) return;
            uiRichTextBox1.Document ??= new FlowDocument();

            int flushed = 0;
            while (flushed < MaxUiLogsPerFlush && _uiLogQueue.TryDequeue(out var entry))
            {
                var run = new Run($"[{DateTime.Now:HH:mm:ss}] {entry.Message}")
                {
                    Foreground = new SolidColorBrush(entry.Color)
                };
                uiRichTextBox1.Document.Blocks.Add(new Paragraph(run) { Margin = new Thickness(0) });
                flushed++;
            }

            while (uiRichTextBox1.Document.Blocks.Count > MaxUiLogBlocks)
            {
                uiRichTextBox1.Document.Blocks.Remove(uiRichTextBox1.Document.Blocks.FirstBlock);
            }

            if (flushed > 0) uiRichTextBox1.ScrollToEnd();
        }

        private void ScheduleFileLogFlush()
        {
            if (Interlocked.CompareExchange(ref _fileFlushInProgress, 1, 0) != 0) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var sb = new StringBuilder(4096);
                    while (_fileLogQueue.TryDequeue(out string line)) sb.Append(line);
                    if (sb.Length > 0)
                    {
                        lock (_logFileLock)
                        {
                            File.AppendAllText(logFilePath, sb.ToString(), Encoding.UTF8);
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _fileFlushInProgress, 0);
                    if (!_fileLogQueue.IsEmpty) ScheduleFileLogFlush();
                }
            });
        }

        private void AppendLogDetail(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _fileLogQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] [DEBUG] {message}{Environment.NewLine}");
            ScheduleFileLogFlush();
        }

        private void BindCommonEvents()
        {
            if (_eventsBound) return;

            if (uiButton1 != null) uiButton1.Click += StopButton_Click;
            HookMenuItem("设备管理器ToolStripMenuItem", OpenDeviceManager);
            HookMenuItem("cMD命令行ToolStripMenuItem", OpenCommandPrompt);
            HookMenuItem("查看日志ToolStripMenuItem", OpenLogFolder);

            _eventsBound = true;
        }

        private void HookMenuItem(string name, Action action)
        {
            if (FindName(name) is MenuItem item)
            {
                item.Click += (_, __) => action();
            }
        }

        private void HideLanguageSelector()
        {
            if (label4 != null)
            {
                label4.Visibility = Visibility.Collapsed;
            }

            if (uiComboBox4 != null)
            {
                uiComboBox4.Visibility = Visibility.Collapsed;
                uiComboBox4.Items.Clear();
                uiComboBox4.SelectedIndex = -1;
                uiComboBox4.SelectionChanged -= LanguageComboBox_SelectionChanged;
                uiComboBox4.SelectionChanged -= UiComboBox4_SelectedIndexChanged;
            }
        }

        private static void SetContentText(object control, string text)
        {
            text = LanguageManager.TranslateLegacyText(text);
            switch (control)
            {
                case ContentControl contentControl:
                    contentControl.Content = text;
                    break;
                case TextBox textBox:
                    textBox.Text = text;
                    break;
            }
        }

        private static void SetHeaderText(object control, string text)
        {
            text = LanguageManager.TranslateLegacyText(text);
            switch (control)
            {
                case HeaderedContentControl headeredContentControl:
                    headeredContentControl.Header = text;
                    break;
                case HeaderedItemsControl headeredItemsControl:
                    headeredItemsControl.Header = text;
                    break;
            }
        }

        private static void SetListViewColumnHeader(System.Windows.Controls.ListView listView, int index, string text)
        {
            if (listView?.View is GridView gridView && index >= 0 && index < gridView.Columns.Count)
            {
                gridView.Columns[index].Header = text;
            }
        }

        private static void SetHint(Control control, string text)
        {
            if (control != null)
            {
                text = LanguageManager.TranslateLegacyText(text);
                control.Tag = text;
                ToolTipService.SetToolTip(control, text);
            }
        }

        private static string GetComboItemText(ComboBox comboBox)
        {
            if (comboBox?.SelectedItem is ComboBoxItem comboBoxItem)
            {
                return comboBoxItem.Content?.ToString() ?? string.Empty;
            }

            return comboBox?.Text ?? string.Empty;
        }

        private void InitializeQualcommModule()
        {
            try
            {
                _qualcommController ??= new QualcommUIController(
                    (msg, color) => AppendLog(msg, color),
                    msg => AppendLogDetail(msg));

                _qualcommController.XiaomiAuthTokenRequired -= OnXiaomiAuthTokenRequired;
                _qualcommController.XiaomiAuthTokenRequired += OnXiaomiAuthTokenRequired;

                uiButton6.Click -= QualcommReadPartitionTable_Click;
                uiButton6.Click += QualcommReadPartitionTable_Click;
                uiButton7.Click -= QualcommReadPartition_Click;
                uiButton7.Click += QualcommReadPartition_Click;
                uiButton8.Click -= QualcommWritePartition_Click;
                uiButton8.Click += QualcommWritePartition_Click;
                uiButton9.Click -= QualcommErasePartition_Click;
                uiButton9.Click += QualcommErasePartition_Click;
                button4.Click -= QualcommSelectRawprogramXml_Click;
                button4.Click += QualcommSelectRawprogramXml_Click;

                _lastEdlCount = _qualcommController.RefreshPorts(silent: true);

                _portRefreshTimer ??= new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(Common.PerformanceConfig.PortRefreshInterval)
                };
                _portRefreshTimer.Tick -= PortRefreshTimer_Tick;
                _portRefreshTimer.Tick += PortRefreshTimer_Tick;
                _portRefreshTimer.Start();

                AppendLog("高通模块初始化完成", DrawColor.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"高通模块初始化失败: {ex.Message}", DrawColor.Red);
            }
        }

        private string _storageType = "ufs";

        private void PortRefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshPortsIfIdle();
        }

        private async void QualcommReadPartitionTable_Click(object sender, RoutedEventArgs e)
        {
            await QualcommReadPartitionTableAsync();
        }

        private async void QualcommReadPartition_Click(object sender, RoutedEventArgs e)
        {
            await QualcommReadPartitionAsync();
        }

        private async void QualcommWritePartition_Click(object sender, RoutedEventArgs e)
        {
            await QualcommWritePartitionAsync();
        }

        private async void QualcommErasePartition_Click(object sender, RoutedEventArgs e)
        {
            await QualcommErasePartitionAsync();
        }

        private void QualcommSelectRawprogramXml_Click(object sender, RoutedEventArgs e)
        {
            QualcommSelectRawprogramXml();
        }

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
            bool oldOnePlus = checkbox17.IsChecked == true;
            bool oplus = checkbox19.IsChecked == true;

            if (oldOnePlus && oplus)
            {
                checkbox17.IsChecked = false;
                oldOnePlus = false;
            }

            _authMode = oldOnePlus ? "demacia" : oplus ? "vip" : "none";
        }

        private void QualcommSelectProgrammer()
        {
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = "Select a programmer file (Programmer/Firehose)";
                ofd.Filter = "引导文件|*.mbn;*.elf|所有文件|*.*";
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    input8.Text = ofd.FileName;
                    AppendLog($"已选择引导文件: {Path.GetFileName(ofd.FileName)}", Color.Green);
                }
            }
        }

        private async void QualcommSelectDigest()
        {
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = "Select a Digest file (VIP authentication)";
                ofd.Filter = "Digest Files|*.elf;*.bin;*.mbn|All Files|*.*";
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = "Select a Signature file (VIP authentication)";
                ofd.Filter = "Signature Files|*.bin;signature*|All Files|*.*";
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = "Select Rawprogram XML files";
                ofd.Filter = "XML文件|rawprogram*.xml;*.xml|所有文件|*.*";
                ofd.Multiselect = true;  // 支持多选
                
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
                        input6.Text = $"Selected {ofd.FileNames.Length} files";
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
            _qualcommListViewAdapter.BeginUpdate();
            _qualcommListViewAdapter.Items.Clear();
            
            var itemsWithPaths = new List<Tuple<FormsListViewItem, string>>();
            
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
                var item = new FormsListViewItem(task.Label);                      // 分区
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

                _qualcommListViewAdapter.Items.Add(item);
                
                // 记录需要检查文件的项
                if (!string.IsNullOrEmpty(task.FilePath) && !Qualcomm.Common.RawprogramParser.IsSensitivePartition(task.Label))
                {
                    itemsWithPaths.Add(Tuple.Create(item, task.FilePath));
                }
            }

            _qualcommListViewAdapter.EndUpdate();
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
                            RunOnUiThread(() =>
                            {
                                tuple.Item1.Checked = true;
                                _qualcommListViewAdapter.Refresh();
                            });
                        }
                    }
                });
                AppendLog($"自动选中 {checkedCount} 个有效分区（文件存在）", Color.Green);
            }
        }

        private async Task QualcommReadPartitionTableAsync()
        {
            if (_qualcommController == null) return;

            bool skipSahara = checkbox12.IsChecked == true;

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
            bool skipSahara = checkbox12.IsChecked == true;

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
                        RunOnUiThread(() => UpdateDownloadProgress(downloaded, total, speed));
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
                    uiLabel7.Content = LanguageManager.T("status.speed") + ": " + speedText;
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
                uiLabel7.Content = LanguageManager.T("status.speed") + ": 0KB/s";
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
                using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
                {
                    fbd.Description = "Choose a folder to save the XML files (rawprogram and patch files will be generated per LUN)";
                    
                    if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = checkedItems.Count == 1
                    ? "Choose a save location"
                    : $"Choose a folder to save {checkedItems.Count} partitions";
                
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
                    if (checkbox11.IsChecked == true && checkedItems.Count > 0)
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
            foreach (FormsListViewItem item in _qualcommListViewAdapter.CheckedItems)
            {
                var p = item.Tag as PartitionInfo;
                if (p != null) result.Add(p);
            }
            
            // 如果没有勾选，使用选中的项
            if (result.Count == 0)
            {
                foreach (FormsListViewItem item in _qualcommListViewAdapter.SelectedItems)
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
                foreach (FormsListViewItem item in _qualcommListViewAdapter.Items)
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
                    if (checkbox18.IsChecked == true && partition.Name.Equals("super", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
                        {
                            fbd.Description = "MetaSuper is enabled. Choose the OPLUS firmware root folder (contains IMAGES and META)";
                            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            {
                                await _qualcommController.FlashOplusSuperAsync(fbd.SelectedPath);
                                return;
                            }
                        }
                    }

                    using (var ofd = new System.Windows.Forms.OpenFileDialog())
                    {
                        ofd.Title = $"Select an image file to write to {partition.Name}";
                        ofd.Filter = "镜像文件|*.img;*.bin|所有文件|*.*";

                        if (ofd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                            return;

                        filePath = ofd.FileName;
                    }
                }
                else
                {
                    // 即使路径存在，如果开启了 MetaSuper 且是 super 分区，也执行拆解逻辑
                    if (checkbox18.IsChecked == true && partition.Name.Equals("super", StringComparison.OrdinalIgnoreCase))
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
                            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
                            {
                                fbd.Description = "MetaSuper is enabled. Choose the OPLUS firmware root folder (contains IMAGES and META)";
                                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
                
                if (success && checkbox15.IsChecked == true)
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
                foreach (FormsListViewItem item in _qualcommListViewAdapter.CheckedItems)
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
                bool metaSuperEnabled = checkbox18.IsChecked == true;
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
                            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
                            {
                                fbd.Description = "Choose the OPLUS firmware root folder (contains IMAGES and META)";
                                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
                    
                    if (success > 0 && checkbox15.IsChecked == true)
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
                ? $"Erase partition {checkedItems[0].Name}?\n\nThis action cannot be undone."
                : $"Erase {checkedItems.Count} partitions?\n\nPartitions: {string.Join(", ", checkedItems.ConvertAll(p => p.Name))}\n\nThis action cannot be undone.";

            var result = FormsMessageBox.Show(
                message,
                "Confirm Erase",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == System.Windows.Forms.DialogResult.Yes)
            {
                if (checkedItems.Count == 1)
                {
                    // 单个擦除
                    bool success = await _qualcommController.ErasePartitionAsync(checkedItems[0].Name);
                    
                    if (success && checkbox15.IsChecked == true)
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
                    
                    if (success > 0 && checkbox15.IsChecked == true)
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
            var result = FormsMessageBox.Show("Switch to slot A?\n\nChoose Yes for slot A.\nChoose No for slot B.",
                "Switch Slot", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (result == System.Windows.Forms.DialogResult.Yes)
                await _qualcommController.SwitchSlotAsync("a");
            else if (result == System.Windows.Forms.DialogResult.No)
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
            if (_qualcommListViewAdapter.Items.Count == 0) return;

            _qualcommListViewAdapter.BeginUpdate();
            foreach (FormsListViewItem item in _qualcommListViewAdapter.Items)
            {
                item.Checked = selectAll;
            }
            _qualcommListViewAdapter.EndUpdate();

            AppendLog(selectAll ? "已全选分区" : "已取消全选", Color.Blue);
        }

        /// <summary>
        /// 双击分区列表项，选择对应的镜像文件
        /// </summary>
        private void QualcommPartitionDoubleClick()
        {
            if (_qualcommListViewAdapter.SelectedItems.Count == 0) return;

            var item = _qualcommListViewAdapter.SelectedItems[0];
            var partition = item.Tag as PartitionInfo;
            if (partition == null)
            {
                // 如果没有 Tag，尝试从名称获取
                string partitionName = item.Text;
                if (string.IsNullOrEmpty(partitionName)) return;

                using (var ofd = new System.Windows.Forms.OpenFileDialog())
                {
                    ofd.Title = $"Select an image file for partition {partitionName}";
                    ofd.Filter = $"镜像文件|{partitionName}.img;{partitionName}.bin;*.img;*.bin|所有文件|*.*";

                    if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        // 更新文件路径列 (最后一列)
                        int lastCol = item.SubItems.Count - 1;
                        if (lastCol >= 0)
                        {
                            item.SubItems[lastCol].Text = ofd.FileName;
                            item.Checked = true; // 自动勾选
                            _qualcommListViewAdapter.Refresh();
                            AppendLog($"已为分区 {partitionName} 选择文件: {Path.GetFileName(ofd.FileName)}", Color.Blue);
                        }
                    }
                }
                return;
            }

            // 有 PartitionInfo 的情况
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = $"Select an image file for partition {partition.Name}";
                ofd.Filter = $"镜像文件|{partition.Name}.img;{partition.Name}.bin;*.img;*.bin|所有文件|*.*";

                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // 更新文件路径列 (最后一列)
                    int lastCol = item.SubItems.Count - 1;
                    if (lastCol >= 0)
                    {
                        item.SubItems[lastCol].Text = ofd.FileName;
                        item.Checked = true; // 自动勾选
                        _qualcommListViewAdapter.Refresh();
                        AppendLog($"已为分区 {partition.Name} 选择文件: {Path.GetFileName(ofd.FileName)}", Color.Blue);
                    }
                }
            }
        }

        private string _lastSearchKeyword = "";
        private List<FormsListViewItem> _searchMatches = new List<FormsListViewItem>();
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
            
            _qualcommListViewAdapter.BeginUpdate();
            
                foreach (FormsListViewItem item in _qualcommListViewAdapter.Items)
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
            
            _qualcommListViewAdapter.EndUpdate();
             
            // 更新下拉建议列表
            UpdateSearchSuggestions(suggestions);
            
            // 滚动到第一个匹配项
            if (_searchMatches.Count > 0)
            {
                _searchMatches[0].Selected = true;
                _qualcommListViewAdapter.Refresh();
                _qualcommListViewAdapter.ScrollIntoView(_searchMatches[0]);
                
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
            _qualcommListViewAdapter.Refresh();
            _qualcommListViewAdapter.ScrollIntoView(_searchMatches[_currentMatchIndex]);
        }

        private void ResetPartitionHighlights()
        {
            _qualcommListViewAdapter.BeginUpdate();
            foreach (FormsListViewItem item in _qualcommListViewAdapter.Items)
            {
                item.BackColor = Color.Transparent;
            }
            _qualcommListViewAdapter.EndUpdate();
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
            
            foreach (FormsListViewItem item in _qualcommListViewAdapter.Items)
            {
                if (item.Text?.ToLower() == partitionName)
                    {
                    item.BackColor = Color.Gold;
                        item.Selected = true;
                        _qualcommListViewAdapter.Refresh();
                        _qualcommListViewAdapter.ScrollIntoView(item);
                        listView2.Focus();
                        break;
                }
            }
        }

        private void CleanupWindowResources()
        {
            if (_portRefreshTimer != null)
            {
                _portRefreshTimer.Stop();
                _portRefreshTimer = null;
            }

            if (_qualcommController != null)
            {
                try { _qualcommController.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] 释放高通控制器异常: {ex.Message}"); }
                _qualcommController = null;
            }

            if (_spreadtrumController != null)
            {
                try { _spreadtrumController.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] 释放展讯控制器异常: {ex.Message}"); }
                _spreadtrumController = null;
            }

            if (_fastbootController != null)
            {
                try { _fastbootController.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] 释放 Fastboot 控制器异常: {ex.Message}"); }
                _fastbootController = null;
            }

            CleanupMediaTekModule();
            ClearImagePreview();
            SakuraEDL.Common.WatchdogManager.DisposeAll();
            GC.Collect(0, GCCollectionMode.Optimized);
        }
        
        /// <summary>
        /// 小米授权令牌事件处理 - 弹窗显示令牌供用户复制
        /// </summary>
        private void OnXiaomiAuthTokenRequired(string token)
        {
            RunOnUiThread(() =>
            {
                try
                {
                    Clipboard.SetText(token);
                    System.Windows.MessageBox.Show("The token has been copied to the clipboard.", "Xiaomi Authorization");
                }
                catch (Exception ex)
                {
                    AppendLog($"显示小米授权令牌失败: {ex.Message}", DrawColor.Red);
                }
            });
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

            pictureBox1.Stretch = Stretch.Uniform;
            pictureBox1.Source = null;
        }

        private void SaveOriginalPositions()
        {
            // WPF layout is container-driven; there is no direct Location/Size state to preserve here.
        }

#if false
        // 日志计数器，用于限制条目数量
        private int _logEntryCount = 0;
        private readonly object _logLock = new object();
        private System.Collections.Concurrent.ConcurrentQueue<string> _logFileQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private volatile bool _logFileWriterRunning = false;
        private DateTime _lastLogFlush = DateTime.MinValue;

        private void AppendLog(string message, Color? color = null)
        {
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
                        Invoke(new Action(() => SetContentText(uiLabel1, displayText)));
                    }
                    else
                    {
                        SetContentText(uiLabel1, displayText);
                    }
                }
            }
            catch
            {
                // 一言加载失败，使用默认文本
                string defaultText = "\"Have the courage to move an inch forward, and the calm to step a foot back.\"";
                if (InvokeRequired)
                {
                    Invoke(new Action(() => SetContentText(uiLabel1, defaultText)));
                }
                else
                {
                    SetContentText(uiLabel1, defaultText);
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
                FormsMessageBox.Show($"Unable to open the log folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            openFileDialog.Title = "Select a local image";
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
#endif

        private void Button2_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select a local image",
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*"
            };

            if (openFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                selectedLocalImagePath = openFileDialog.FileName;
                AppendLog($"已选择本地文件：{selectedLocalImagePath}", DrawColor.Green);
                Task.Run(() => LoadLocalImage(selectedLocalImagePath));
            }
        }

        private void Button3_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(selectedLocalImagePath))
            {
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

                SafeInvoke(() =>
                {
                    input8OriginalText = filePath;
                    UpdatePreviewLabel();
                    AppendLog($"本地图片设置成功: {Path.GetFileName(filePath)}", DrawColor.Green);
                });
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

        private void SafeInvoke(Action action)
        {
            RunOnUiThread(action);
        }

        private Bitmap ResizeImageToFitWithLowMemory(Bitmap original, DrawingSize targetSize)
        {
            return original == null ? null : new Bitmap(original);
        }

        private void ClearImagePreview()
        {
            previewImages.Clear();
            UpdatePreviewLabel();
        }

        private void UpdatePreviewLabel()
        {
            SetContentText(label3, string.IsNullOrWhiteSpace(selectedLocalImagePath)
                ? LanguageManager.T("settings.preview")
                : $"Preview: {Path.GetFileName(selectedLocalImagePath)}");
        }

        private Bitmap LoadImageWithLowQuality(string filePath)
        {
            try
            {
                // 使用最小内存的方式加载图片
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // 读取图片信息但不加载全部数据
                    using (DrawingImage img = DrawingImage.FromStream(fs, false, false))
                    {
                        // 如果图片很大，先创建缩略图
                        if (img.Width > 2000 || img.Height > 2000)
                        {
                            int newWidth = Math.Min(img.Width / 4, 800);
                            int newHeight = Math.Min(img.Height / 4, 600);

                            Bitmap thumbnail = new Bitmap(newWidth, newHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
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

        private void UiComboBox4_SelectedIndexChanged(object sender, SelectionChangedEventArgs e)
        {
            // English-only build. Language switching is intentionally disabled.
        }

        /// <summary>
        /// 应用当前语言到界面
        /// </summary>
#if false
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
            label4.Text = LanguageManager.T("settings.language");
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
            string colon = LanguageManager.CurrentLanguage == "zh" ? "：" : ": ";
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

            string langName = LanguageManager.CurrentLanguageInfo.NativeName;
            AppendLog(string.Format(LanguageManager.T("log.langChanged"), langName), Color.Green);
        }
#endif

        private void ApplyLanguage()
        {
            string lang = LanguageManager.CurrentLanguage;
            isEnglish = (lang == "en");
            string waiting = LanguageManager.T("device.waiting");
            string colon = ": ";

            SetHeaderText(tabPage1, LanguageManager.T("tab.autoRoot"));
            SetHeaderText(tabPage2, LanguageManager.T("tab.qualcomm"));
            SetHeaderText(tabPage3, LanguageManager.T("tab.fastboot"));
            SetHeaderText(tabPage4, LanguageManager.T("tab.mtk"));
            SetHeaderText(tabPage5, LanguageManager.T("tab.spd"));
            SetHeaderText(tabPage6, LanguageManager.T("tab.settings"));
            SetHeaderText(tabPage7, LanguageManager.T("tab.partManage"));
            SetHeaderText(tabPage8, LanguageManager.T("tab.fileManage"));

            SetHeaderText(快捷重启ToolStripMenuItem, LanguageManager.T("menu.quickRestart"));
            SetHeaderText(toolStripMenuItem1, LanguageManager.T("menu.edlOps"));
            SetHeaderText(其他ToolStripMenuItem, LanguageManager.T("menu.other"));
            SetHeaderText(toolStripMenuItem2, LanguageManager.T("menu.rebootSystem"));
            SetHeaderText(toolStripMenuItem6, LanguageManager.T("menu.rebootBootloader"));
            SetHeaderText(toolStripMenuItem7, LanguageManager.T("menu.rebootFastbootd"));
            SetHeaderText(重启恢复ToolStripMenuItem, LanguageManager.T("menu.rebootRecovery"));
            SetHeaderText(mIToolStripMenuItem, LanguageManager.T("menu.miKickEdl"));
            SetHeaderText(联想或安卓踢EDLToolStripMenuItem, LanguageManager.T("menu.lenovoKickEdl"));
            SetHeaderText(擦除谷歌锁ToolStripMenuItem, LanguageManager.T("menu.eraseFrp"));
            SetHeaderText(切换槽位ToolStripMenuItem, LanguageManager.T("menu.switchSlot"));
            SetHeaderText(合并SuperToolStripMenuItem, LanguageManager.T("menu.mergeSuper"));
            SetHeaderText(提取PayloadToolStripMenuItem, LanguageManager.T("menu.extractPayload"));
            SetHeaderText(toolStripMenuItem4, LanguageManager.T("menu.edlToEdl"));
            SetHeaderText(toolStripMenuItem5, LanguageManager.T("menu.edlToFbd"));
            SetHeaderText(eDL擦除谷歌锁ToolStripMenuItem, LanguageManager.T("menu.edlEraseFrp"));
            SetHeaderText(eDL切换槽位ToolStripMenuItem, LanguageManager.T("menu.edlSwitchSlot"));
            SetHeaderText(激活LUNToolStripMenuItem, LanguageManager.T("menu.activateLun"));
            SetHeaderText(设备管理器ToolStripMenuItem, LanguageManager.T("menu.deviceManager"));
            SetHeaderText(cMD命令行ToolStripMenuItem, LanguageManager.T("menu.cmdPrompt"));
            SetHeaderText(安卓驱动ToolStripMenuItem, LanguageManager.T("menu.androidDriver"));
            SetHeaderText(mTK驱动ToolStripMenuItem, LanguageManager.T("menu.mtkDriver"));
            SetHeaderText(高通驱动ToolStripMenuItem, LanguageManager.T("menu.qualcommDriver"));
            SetHeaderText(展讯驱动ToolStripMenuItem, LanguageManager.T("menu.spdDriver"));
            SetHeaderText(查看日志ToolStripMenuItem, LanguageManager.T("menu.viewLog"));
            SetHeaderText(eDL到EDLToolStripMenuItem, LanguageManager.T("menu.edlToEdl"));
            SetHeaderText(eDL到FBDToolStripMenuItem, LanguageManager.T("menu.edlToFbd"));
            SetHeaderText(eDLToolStripMenuItem, LanguageManager.T("menu.edlFactoryReset"));

            SetContentText(label1, LanguageManager.T("settings.blur"));
            SetContentText(label2, LanguageManager.T("settings.wallpaper"));
            SetContentText(label3, LanguageManager.T("settings.preview"));
            SetContentText(button2, LanguageManager.T("settings.localWallpaper"));
            SetContentText(button3, LanguageManager.T("settings.apply"));
            SetContentText(button7, LanguageManager.T("settings.clearCache"));

            SetContentText(uiButton6, LanguageManager.T("qualcomm.readPartTable"));
            SetContentText(uiButton7, LanguageManager.T("qualcomm.readPart"));
            SetContentText(uiButton8, LanguageManager.T("qualcomm.writePart"));
            SetContentText(uiButton9, LanguageManager.T("qualcomm.erasePart"));
            SetContentText(uiButton1, LanguageManager.T("qualcomm.stop"));
            SetContentText(button4, LanguageManager.T("qualcomm.browse"));
            SetContentText(checkbox12, LanguageManager.T("option.skipBoot"));
            SetContentText(checkbox16, LanguageManager.T("option.protectPart"));
            SetContentText(checkbox11, LanguageManager.T("option.generateXml"));
            SetContentText(checkbox15, LanguageManager.T("option.autoReboot"));
            SetContentText(checkbox14, LanguageManager.T("qualcomm.autoDetect"));
            SetHint(input8, LanguageManager.T("qualcomm.selectProgrammer"));
            SetHint(input6, LanguageManager.T("qualcomm.selectRawXml"));
            SetHint(select4, LanguageManager.T("qualcomm.findPart"));
            checkbox13.ToolTip = LanguageManager.T("option.selectAll");
            SetHeaderText(uiGroupBox4, LanguageManager.T("qualcomm.partTable"));
            SetListViewColumnHeader(listView2, 0, LanguageManager.T("qualcomm.partition"));
            SetListViewColumnHeader(listView2, 1, LanguageManager.T("qualcomm.lun"));
            SetListViewColumnHeader(listView2, 2, LanguageManager.T("qualcomm.size"));
            SetListViewColumnHeader(listView2, 3, LanguageManager.T("qualcomm.startSector"));
            SetListViewColumnHeader(listView2, 4, LanguageManager.T("qualcomm.endSector"));
            SetListViewColumnHeader(listView2, 5, LanguageManager.T("qualcomm.sectorCount"));
            SetListViewColumnHeader(listView2, 6, LanguageManager.T("qualcomm.startAddr"));
            SetListViewColumnHeader(listView2, 7, LanguageManager.T("qualcomm.endAddr"));
            SetListViewColumnHeader(listView2, 8, LanguageManager.T("qualcomm.filePath"));

            SetHeaderText(uiGroupBox3, LanguageManager.T("device.info"));
            SetHeaderText(uiGroupBox1, LanguageManager.T("device.log"));
            SetContentText(uiLabel9, LanguageManager.T("device.brand") + colon + waiting);
            SetContentText(uiLabel11, LanguageManager.T("device.chip") + colon + waiting);
            SetContentText(uiLabel12, LanguageManager.T("device.ota") + colon + waiting);
            SetContentText(uiLabel10, LanguageManager.T("device.serial") + colon + waiting);
            SetContentText(uiLabel3, LanguageManager.T("device.model") + colon + waiting);
            SetContentText(uiLabel14, LanguageManager.T("device.model") + colon + waiting);
            SetContentText(uiLabel13, LanguageManager.T("device.storage") + colon + waiting);
            SetContentText(uiLabel8, LanguageManager.T("status.operation") + colon + LanguageManager.T("status.idle"));
            SetContentText(uiLabel7, LanguageManager.T("status.speed") + colon + "0 KB/s");
            SetContentText(uiLabel6, LanguageManager.T("status.time") + colon + "00:00");
            SetContentText(linkLabelDev, LanguageManager.T("status.contactDev"));

            SetContentText(checkbox20, LanguageManager.T("option.keepData"));
            SetContentText(uiButton10, LanguageManager.T("fastboot.execute"));
            SetContentText(uiButton11, LanguageManager.T("fastboot.readInfo"));
            SetContentText(uiButton22, LanguageManager.T("fastboot.fixFbd"));
            SetContentText(checkbox7, LanguageManager.T("fastboot.oplusFlash"));
            SetContentText(checkbox21, LanguageManager.T("fastboot.lockBl"));
            SetContentText(checkbox22, LanguageManager.T("fastboot.clearData"));
            SetContentText(checkbox43, LanguageManager.T("menu.eraseFrp"));
            SetContentText(checkbox41, LanguageManager.T("fastboot.switchSlotA"));
            SetContentText(button8, LanguageManager.T("qualcomm.browse"));
            SetContentText(button9, LanguageManager.T("qualcomm.browse"));
            SetHeaderText(uiGroupBox7, LanguageManager.T("qualcomm.partTable"));
            SetContentText(uiButton18, LanguageManager.T("qualcomm.readPartTable"));
            SetContentText(uiButton19, LanguageManager.T("fastboot.extractImage"));
            SetContentText(uiButton20, LanguageManager.T("qualcomm.writePart"));
            SetContentText(uiButton21, LanguageManager.T("qualcomm.erasePart"));
            SetContentText(checkbox44, LanguageManager.T("option.autoReboot"));
            SetContentText(checkbox45, LanguageManager.T("fastboot.fbdFlash"));
            SetContentText(checkbox50, LanguageManager.T("option.keepData"));
            SetHint(input1, LanguageManager.T("fastboot.selectFlashBat"));
            SetHint(select5, LanguageManager.T("qualcomm.findPart"));

            SetContentText(mtkBtnReadGpt, LanguageManager.T("qualcomm.readPartTable"));
            SetContentText(mtkBtnWritePartition, LanguageManager.T("qualcomm.writePart"));
            SetContentText(mtkBtnReadPartition, LanguageManager.T("qualcomm.readPart"));
            SetContentText(mtkBtnErasePartition, LanguageManager.T("qualcomm.erasePart"));
            SetContentText(mtkBtnReboot, LanguageManager.T("mtk.rebootDevice"));
            SetContentText(mtkBtnReadImei, LanguageManager.T("mtk.readImei"));
            SetContentText(mtkBtnWriteImei, LanguageManager.T("mtk.writeImei"));
            SetContentText(mtkBtnBackupNvram, LanguageManager.T("mtk.backupNvram"));
            SetContentText(mtkBtnRestoreNvram, LanguageManager.T("mtk.restoreNvram"));
            SetContentText(mtkBtnFormatData, LanguageManager.T("mtk.formatData"));
            SetContentText(mtkBtnUnlockBl, LanguageManager.T("mtk.unlockBl"));
            SetContentText(mtkBtnExploit, LanguageManager.T("mtk.exploit"));
            SetContentText(mtkBtnConnect, LanguageManager.T("mtk.connect"));
            SetContentText(mtkBtnDisconnect, LanguageManager.T("mtk.disconnect"));
            SetContentText(mtkChkAutoStorage, LanguageManager.T("mtk.auto"));
            SetHeaderText(mtkGrpPartitions, LanguageManager.T("qualcomm.partTable"));

            SetContentText(sprdBtnReadGpt, LanguageManager.T("qualcomm.readPartTable"));
            SetContentText(sprdBtnWritePartition, LanguageManager.T("qualcomm.writePart"));
            SetContentText(sprdBtnReadPartition, LanguageManager.T("qualcomm.readPart"));
            SetContentText(sprdBtnErasePartition, LanguageManager.T("qualcomm.erasePart"));
            SetContentText(sprdBtnReboot, LanguageManager.T("mtk.rebootDevice"));
            SetContentText(sprdBtnReadImei, LanguageManager.T("mtk.readImei"));
            SetContentText(sprdBtnWriteImei, LanguageManager.T("mtk.writeImei"));
            SetContentText(sprdBtnBackupCalib, LanguageManager.T("spd.backupCalib"));
            SetContentText(sprdBtnRestoreCalib, LanguageManager.T("spd.restoreCalib"));
            SetContentText(sprdBtnFactoryReset, LanguageManager.T("spd.factoryReset"));
            SetContentText(sprdBtnUnlockBL, LanguageManager.T("mtk.unlockBl"));
            SetContentText(sprdBtnExtract, LanguageManager.T("spd.extractPac"));
            SetContentText(sprdBtnNvManager, LanguageManager.T("spd.nvManager"));
            SetContentText(sprdChkRebootAfter, LanguageManager.T("spd.rebootAfter"));
            SetContentText(sprdChkSkipUserdata, LanguageManager.T("spd.skipUserdata"));
            SetHeaderText(sprdGroupPartitions, LanguageManager.T("qualcomm.partTable"));
            SetHint(sprdSelectChip, LanguageManager.T("spd.chipModel"));
            SetHint(sprdSelectDevice, LanguageManager.T("spd.deviceModel"));

            SetContentText(labelDevRoot, LanguageManager.T("dev.inProgress"));
            SetHeaderText(uiGroupBox2, LanguageManager.T("qualcomm.partTable"));
            SetContentText(uiButton2, LanguageManager.T("qualcomm.readPart"));
            SetContentText(uiButton5, LanguageManager.T("qualcomm.readPartTable"));
            SetContentText(uiButton13, LanguageManager.T("qualcomm.writePart"));
            SetContentText(uiButton14, LanguageManager.T("qualcomm.erasePart"));
            SetContentText(checkbox3, LanguageManager.T("menu.eraseFrp"));
            SetContentText(checkbox6, LanguageManager.T("option.autoReboot"));
            SetContentText(button1, LanguageManager.T("qualcomm.browse"));

            SetContentText(uiButton3, LanguageManager.T("scrcpy.execute"));
            SetContentText(uiButton4, LanguageManager.T("scrcpy.startMirror"));
            SetContentText(uiButton12, LanguageManager.T("scrcpy.fixMirror"));
            SetContentText(uiButton24, LanguageManager.T("scrcpy.flashZip"));
            SetContentText(checkbox1, LanguageManager.T("scrcpy.screenOn"));
            SetContentText(checkbox2, LanguageManager.T("scrcpy.audioForward"));
            SetContentText(checkbox4, LanguageManager.T("scrcpy.autoReconnect"));
            SetContentText(uiLabel15, LanguageManager.T("scrcpy.battery"));
            SetContentText(uiLabel16, LanguageManager.T("scrcpy.refreshRate"));
            SetContentText(uiLabel17, LanguageManager.T("scrcpy.resolution"));
            SetContentText(uiLabel18, LanguageManager.T("device.storage"));
            SetContentText(uiButton15, LanguageManager.T("scrcpy.powerKey"));
            SetContentText(uiButton16, LanguageManager.T("scrcpy.recentKey"));
            SetContentText(uiButton17, LanguageManager.T("scrcpy.homeKey"));
            SetContentText(uiButton23, LanguageManager.T("scrcpy.backKey"));
            SetContentText(uiLabel1, LanguageManager.T("status.loading"));
            SetContentText(uiLabel5, LanguageManager.T("app.version"));

        }

        // 保留旧方法以兼容
        private void SwitchLanguage(string language)
        {
            ApplyLanguage();
        }

        private void Checkbox17_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (checkbox17.IsChecked == true)
            {
                checkbox19.IsChecked = false;
                ApplyCompactLayout();
            }

            UpdateAuthMode();
        }

        private void Checkbox19_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (checkbox19.IsChecked == true)
            {
                checkbox17.IsChecked = false;
                RestoreOriginalLayout();
            }
            else
            {
                ApplyCompactLayout();
            }

            UpdateAuthMode();
        }

        private void ApplyCompactLayout()
        {
            input9.Visibility = Visibility.Collapsed;
            input7.Visibility = Visibility.Collapsed;
        }

        private void RestoreOriginalLayout()
        {
            input9.Visibility = Visibility.Visible;
            input7.Visibility = Visibility.Visible;
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

        private void Select3_SelectedIndexChanged(object sender, SelectionChangedEventArgs e)
        {
            string selectedItem = GetComboItemText(select3);
            
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
                    input9.IsEnabled = true;
                    input8.IsEnabled = true;
                    input7.IsEnabled = true;
                    input8.Text = "";
                    input9.Text = "";
                    input7.Text = "";
                    _cloudLoaderAuthType = "none";
                    checkbox17.IsChecked = false;
                    checkbox19.IsChecked = false;
                    _authMode = "none";
                    AppendLog("[本地] 已切换到本地选择模式，请浏览选择引导文件", DrawColor.Blue);
                }
                // 其他分隔符忽略
                return;
            }
            
            // 处理刷新云端列表
            if (isRefresh)
            {
                AppendLog("[云端] 正在刷新 Loader 列表...", DrawColor.Cyan);
                _cloudLoadersLoaded = false;  // 重置加载标志
                LoadCloudLoadersAsync(true);  // 强制刷新
                select3.SelectedIndex = 0;    // 重置选择
                return;
            }
            
            // 处理默认模式（提示用户选择 Loader）
            if (isDefaultAutoMatch)
            {
                // 启用自定义引导文件输入（可选本地文件）
                input9.IsEnabled = true;
                input8.IsEnabled = true;
                input7.IsEnabled = true;
                
                // 显示提示
                input8.Text = "";
                input9.Text = "";
                input7.Text = "";
                
                // 重置选择的云端 Loader
                _selectedCloudLoaderId = 0;
                _cloudLoaderAuthType = "none";
                checkbox17.IsChecked = false;
                checkbox19.IsChecked = false;
                _authMode = "none";
                
                // 清除预下载缓存
                ClearLoaderCache();
                
                AppendLog("[提示] 请从下拉列表选择云端 Loader 或浏览本地引导文件", DrawColor.Blue);
            }
            // 处理云端 Loader 选择（从下拉列表中选择具体的 Loader）
            else
            {
                var cloudLoader = GetSelectedCloudLoader();
                if (cloudLoader != null)
                {
                    // 禁用自定义引导文件输入 (使用云端 Loader)
                    input9.IsEnabled = false;
                    input8.IsEnabled = false;
                    input7.IsEnabled = false;
                    
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
                        checkbox17.IsChecked = false;
                        checkbox19.IsChecked = true;
                    }
                    // OnePlus/demacia 验证 -> 勾选 oldoneplus checkbox
                    // 仅当 auth_type 明确是 demacia/oneplus 时才勾选，不能仅凭厂商名称
                    else if (cloudLoader.IsOnePlus)
                    {
                        AppendLog(string.Format("[云端] {0}", cloudLoader.DisplayName), Color.FromArgb(255, 100, 100));
                        checkbox19.IsChecked = false;
                        checkbox17.IsChecked = true;
                    }
                    // 小米设备
                    else if (isXiaomiVendor || cloudLoader.IsXiaomi)
                    {
                        AppendLog(string.Format("[云端] {0}", cloudLoader.DisplayName), Color.FromArgb(255, 165, 0));
                        _cloudLoaderAuthType = "miauth";
                        checkbox17.IsChecked = false;
                        checkbox19.IsChecked = false;
                        _authMode = "xiaomi";
                    }
                    // 无验证
                    else
                    {
                        AppendLog(string.Format("[云端] {0}", cloudLoader.DisplayName), Color.Green);
                        _cloudLoaderAuthType = "none";
                        checkbox17.IsChecked = false;
                        checkbox19.IsChecked = false;
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
                            radio4.IsChecked = true;
                            radio3.IsChecked = false;
                        }
                        else
                        {
                            _storageType = "ufs";
                            radio3.IsChecked = true;
                            radio4.IsChecked = false;
                        }
                    }
                }
            }
        }
        
        // 选择的云端 Loader ID
        private int _selectedCloudLoaderId = 0;
        
        // 预下载缓存 (选择 Loader 时立即下载，避免 EDL 看门狗超时)
        private byte[]? _cachedLoaderData = null;
        private byte[]? _cachedDigestData = null;
        private byte[]? _cachedSignatureData = null;
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
                    RunOnUiThread(() => UpdateDownloadProgress(downloaded, total, speed));
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
        private List<string>? _edlLoaderItems = null;
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
                    
                    RunOnUiThread(UpdateCloudLoaderList);
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

        #if false
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
                uiTextBox1.Watermark = "Select a payload/folder or enter a cloud URL (right-click Browse to choose a folder)";
                uiTextBox1.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        FastbootParsePayloadInput(uiTextBox1.Text);
                    }
                };

                // Keep the initial Fastboot action labels in sync with the active language.
                uiButton11.Text = LanguageManager.T("fastboot.readInfo");
                uiButton18.Text = LanguageManager.T("qualcomm.readPartTable");
                uiButton19.Text = LanguageManager.T("fastboot.extractImage");

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
                        string waiting = LanguageManager.T("device.waiting");
                        uiLabel9.Text = LanguageManager.T("device.brand") + ": " + waiting;
                        uiLabel11.Text = LanguageManager.T("device.chip") + ": " + waiting;
                        uiLabel3.Text = LanguageManager.T("device.model") + ": " + waiting;
                        uiLabel10.Text = LanguageManager.T("device.serial") + ": " + waiting;
                        uiLabel13.Text = LanguageManager.T("device.storage") + ": " + waiting;
                        uiLabel14.Text = LanguageManager.T("device.model") + ": " + waiting;
                        uiLabel12.Text = LanguageManager.T("device.ota") + ": " + waiting;
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
        #endif

        private void InitializeFastbootModule()
        {
            try
            {
                string fastbootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fastboot.exe");
                FastbootCommand.SetFastbootPath(fastbootPath);
                _fastbootController ??= new FastbootUIController(
                    (msg, color) => AppendLog(msg, color),
                    msg => AppendLogDetail(msg));
                _fastbootController.BindControls(partitionListView: _fastbootListViewAdapter);

                tabs1.SelectionChanged -= OnTabPageChanged;
                tabs1.SelectionChanged += OnTabPageChanged;
                AppendLog("Fastboot 模块初始化完成", DrawColor.Gray);
            }
            catch (Exception ex)
            {
                AppendLog($"Fastboot 模块初始化失败: {ex.Message}", DrawColor.Red);
            }
        }

        private void OnTabPageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (tabs1.SelectedItem == tabPage3)
            {
                _isOnFastbootTab = true;
                _portRefreshTimer?.Stop();
            }
            else
            {
                _isOnFastbootTab = false;
                _portRefreshTimer?.Start();
            }

            uiLabel4.Content = _systemInfoText;
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
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Choose a folder for extracted partitions";
                // 如果之前有选过目录，作为默认路径
                if (!string.IsNullOrEmpty(input1.Text) && Directory.Exists(input1.Text))
                {
                    fbd.SelectedPath = input1.Text;
                }
                
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
            bool useOugaMode = checkbox7.IsChecked == true;
            bool usePureFbdMode = checkbox45.IsChecked == true;
            bool switchSlotA = checkbox41.IsChecked == true;
            bool clearData = checkbox50.IsChecked != true;
            bool eraseFrp = checkbox43.IsChecked == true;
            bool autoReboot = checkbox44.IsChecked == true;

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
                bool keepData = checkbox50.IsChecked == true;
                bool lockBl = checkbox21.IsChecked == true;

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
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Choose an output folder";
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
            var result = FormsMessageBox.Show(
                "Choose what to load:\n\n" +
                "\"Yes\" selects a Payload/script file.\n" +
                "\"No\" selects an already extracted folder.",
                "Select Input Type",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == System.Windows.Forms.DialogResult.Yes)
            {
                // 选择文件
                using (var ofd = new System.Windows.Forms.OpenFileDialog())
                {
                    ofd.Title = "Select a Payload or flash script";
                    ofd.Filter = "Payload|*.bin;*.zip|Flash Scripts|*.bat;*.sh;*.cmd|All Files|*.*";
                    if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Choose an extracted firmware folder (contains .img files)";
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
            using (var ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Title = "Select a flash script (flash_all.bat)";
                ofd.Filter = "刷机脚本|*.bat;*.sh;*.cmd|所有文件|*.*";
                
                if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
            checkbox50.IsChecked = false;
            checkbox21.IsChecked = false;

            // 根据脚本名称判断类型
            if (fileName.Contains("except_storage") || fileName.Contains("except-storage") || 
                fileName.Contains("keep_data") || fileName.Contains("keepdata"))
            {
                // 保留数据刷机脚本
                checkbox50.IsChecked = true;
                AppendLog("检测到保留数据脚本，已勾选「保留数据」", Color.Blue);
            }
            else if (fileName.Contains("_lock") || fileName.Contains("-lock") || 
                     fileName.EndsWith("lock.bat") || fileName.EndsWith("lock.sh"))
            {
                // 锁定BL刷机脚本
                checkbox21.IsChecked = true;
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
            foreach (FormsListViewItem item in _fastbootListViewAdapter.Items)
            {
                item.Checked = selectAll;
            }
            _fastbootListViewAdapter.Refresh();
        }

        /// <summary>
        /// Fastboot 分区双击选择镜像
        /// </summary>
        private void FastbootPartitionDoubleClick()
        {
            if (_fastbootListViewAdapter.SelectedItems.Count == 0) return;
            
            var selectedItem = _fastbootListViewAdapter.SelectedItems[0];
            _fastbootController?.SelectImageForPartition(selectedItem);
            _fastbootListViewAdapter.Refresh();
        }

        // Fastboot 分区搜索相关变量
        private string _fbLastSearchKeyword = "";
        private List<FormsListViewItem> _fbSearchMatches = new List<FormsListViewItem>();
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
            
            _fastbootListViewAdapter.BeginUpdate();
            
            foreach (FormsListViewItem item in _fastbootListViewAdapter.Items)
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
            
            _fastbootListViewAdapter.EndUpdate();
            
            // 更新下拉建议列表
            FastbootUpdateSearchSuggestions(suggestions);
            
            // 滚动到第一个匹配项
            if (_fbSearchMatches.Count > 0)
            {
                _fbSearchMatches[0].Selected = true;
                _fastbootListViewAdapter.Refresh();
                _fastbootListViewAdapter.ScrollIntoView(_fbSearchMatches[0]);
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
            _fastbootListViewAdapter.Refresh();
            _fastbootListViewAdapter.ScrollIntoView(_fbSearchMatches[_fbCurrentMatchIndex]);
        }

        private void FastbootResetPartitionHighlights()
        {
            _fastbootListViewAdapter.BeginUpdate();
            foreach (FormsListViewItem item in _fastbootListViewAdapter.Items)
            {
                item.BackColor = Color.Transparent;
            }
            _fastbootListViewAdapter.EndUpdate();
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
            
            foreach (FormsListViewItem item in _fastbootListViewAdapter.Items)
            {
                if (item.SubItems[0].Text.Equals(partitionName, StringComparison.OrdinalIgnoreCase))
                {
                    item.BackColor = Color.LightYellow;
                    item.Selected = true;
                    _fastbootListViewAdapter.Refresh();
                    _fastbootListViewAdapter.ScrollIntoView(item);
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
                        driverName = "Qualcomm 9008 Driver";
                        break;
                    case "mtk":
                        type = DriverType.MediaTek;
                        driverName = "MTK USB Driver";
                        break;
                    case "spreadtrum":
                        type = DriverType.Spreadtrum;
                        driverName = "Unisoc USB Driver";
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
                        RunOnUiThread(() => uiProcessBar1.Value = progress);
                    },
                    detailProgress => {
                        RunOnUiThread(() => UpdateDriverDownloadProgress(detailProgress));
                    }
                );

                // 直接从云端下载驱动 (始终下载最新版本)
                uiButton1.IsEnabled = false;
                AppendLog($"正在从云端下载 {driverName}，请稍候...", Color.Cyan);

                try
                {
                    // 下载驱动
                    bool downloaded = await driverService.DownloadDriverAsync(type.Value);
                    
                    if (!downloaded)
                    {
                        AppendLog($"{driverName} 下载失败", Color.Red);
                        FormsMessageBox.Show(
                            $"{driverName} download failed.\n\nCheck your network connection, or download it manually from:\nhttps://sakuraedl.org/download",
                            "Download Failed",
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
                    uiButton1.IsEnabled = true;
                    uiProcessBar1.Value = 0;
                    uiLabel7.Content = LanguageManager.T("status.speed") + ": --";
                    uiLabel8.Content = LanguageManager.T("status.operation") + ": " + LanguageManager.T("status.idle");
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
                    AppendLog("Android driver installer not found. Install it manually.", Color.Orange);
                    FormsMessageBox.Show("Android driver installer was not found.\n\nDownload the correct driver from the official website.",
                        "Driver Not Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            uiLabel7.Content = LanguageManager.T("status.speed") + ": " + progress.SpeedText;
            
            // 更新当前操作标签 (uiLabel8) - 紧凑格式避免截断
            uiLabel8.Content = $"Downloading driver [{progress.Percentage}%]";
        }

        #endregion

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopCurrentOperation();
        }

        private void WindowMinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void WindowCloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void PageHeader2_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // English-only build. Language switching is intentionally disabled.
        }

        #if false
        private void StopCurrentOperation()
        {
            _mtkCts?.Cancel();
            _sprdController?.CancelOperation();
            _qualcommController?.CancelOperation();
            AppendLog("Stop request sent.", Colors.Orange);
        }

        private async Task LoadHitokotoAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var response = await client.GetAsync("https://hitokoto.cn/text");
                if (response.IsSuccessStatusCode)
                {
                    string quote = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(quote))
                    {
                        uiLabel2.Content = quote.Trim();
                    }
                }
            }
            catch
            {
            }
        }
        #endif

        private void Form1_Closing(object sender, CancelEventArgs e)
        {
            _logFlushTimer?.Stop();
            _mtkCts?.Cancel();
            _mtkService?.Dispose();
            CleanupWindowResources();
        }

        private static MediaColor? ConvertColor(DrawColor? color)
        {
            if (color == null) return null;
            return MediaColor.FromArgb(color.Value.A, color.Value.R, color.Value.G, color.Value.B);
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F2} MiB";
            if (bytes >= 1024L) return $"{bytes / 1024.0:F2} KiB";
            return $"{bytes} B";
        }

        private static void BrowseFileToTextBox(TextBox textBox, string filter)
        {
            if (textBox == null || !textBox.IsEnabled) return;
            var open = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            if (open.ShowDialog() == true)
            {
                textBox.Text = open.FileName;
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                Process.Start(new ProcessStartInfo(logDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to open log folder: {ex.Message}", DrawColor.Red);
            }
        }
    }
}

