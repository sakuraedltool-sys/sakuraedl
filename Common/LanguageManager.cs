// ============================================================================
// SakuraEDL - 多语言管理器
// Multi-Language Manager - 支持 6 种语言 (ZH/EN/JA/KO/RU/ES)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace SakuraEDL.Common
{
    public class LanguageInfo
    {
        public string Code { get; set; }
        public string NativeName { get; set; }
        public string EnglishName { get; set; }
        public CultureInfo Culture { get; set; }
    }

    public static class LanguageManager
    {
        public static readonly List<LanguageInfo> SupportedLanguages = new List<LanguageInfo>
        {
            new LanguageInfo { Code = "en", NativeName = "English", EnglishName = "English", Culture = new CultureInfo("en-US") }
        };

        private static readonly Dictionary<string, string> CountryToLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "CN", "zh" }, { "TW", "zh" }, { "HK", "zh" }, { "SG", "zh" },
            { "US", "en" }, { "GB", "en" }, { "AU", "en" }, { "CA", "en" }, { "NZ", "en" }, { "IE", "en" }, { "IN", "en" },
            { "JP", "ja" },
            { "KR", "ko" }, { "KP", "ko" },
            { "RU", "ru" }, { "BY", "ru" }, { "KZ", "ru" }, { "UA", "ru" },
            { "ES", "es" }, { "MX", "es" }, { "AR", "es" }, { "CO", "es" }, { "CL", "es" }, { "PE", "es" }, { "VE", "es" }
        };

        private static string _currentLanguage = "en";
        private static Dictionary<string, Dictionary<string, string>> _translations;
        private static readonly object _lock = new object();
        private static string _settingsPath;
        private static readonly (string Source, string Target)[] LegacyEnglishReplacements = new[]
        {
            ("系统信息错误: ", "System info error: "),
            ("当前操作：", "Current operation: "),
            ("当前操作:", "Current operation: "),
            ("计算机：", "Computer: "),
            ("计算机:", "Computer: "),
            ("速度：", "Speed: "),
            ("速度:", "Speed: "),
            ("品牌：", "Brand: "),
            ("品牌:", "Brand: "),
            ("芯片：", "Chip: "),
            ("芯片:", "Chip: "),
            ("序列号：", "Serial: "),
            ("序列号:", "Serial: "),
            ("型号：", "Model: "),
            ("型号:", "Model: "),
            ("存储：", "Storage: "),
            ("存储:", "Storage: "),
            ("平台：", "Platform: "),
            ("平台:", "Platform: "),
            ("协议：", "Protocol: "),
            ("协议:", "Protocol: "),
            ("状态：", "Status: "),
            ("状态:", "Status: "),
            ("模式：", "Mode: "),
            ("模式:", "Mode: "),
            ("阶段：", "Stage: "),
            ("阶段:", "Stage: "),
            ("端口：", "Port: "),
            ("端口:", "Port: "),
            ("[设备管理器]", "[Device Manager]"),
            ("[展讯]", "[Spreadtrum]"),
            ("[高通]", "[Qualcomm]"),
            ("[云端]", "[Cloud]"),
            ("[本地]", "[Local]"),
            ("[提示]", "[Hint]"),
            ("[错误]", "[Error]"),
            ("高通模块初始化完成", "Qualcomm module initialized"),
            ("高通模块初始化失败: ", "Qualcomm module initialization failed: "),
            ("Fastboot 模块初始化完成", "Fastboot module initialized"),
            ("Fastboot 模块初始化失败: ", "Fastboot module initialization failed: "),
            ("模块初始化完成", "module initialized"),
            ("联发科模块已初始化", "MediaTek module initialized"),
            ("请从下拉列表选择云端 Loader 或浏览本地引导文件", "Select a cloud loader from the list or browse for a local programmer file."),
            ("已切换到本地选择模式，请浏览选择引导文件", "Switched to local selection mode. Browse for a programmer file."),
            ("VIP 验证文件下载失败，将以普通模式连接", "VIP authentication files failed to download. Continuing with standard mode."),
            ("VIP 验证文件下载失败", "VIP authentication file download failed"),
            ("芯片设置为自动检测，请自行配置 FDL", "Chip selection is set to auto detect. Configure the FDL files manually."),
            ("设备设置为自动检测", "Device selection is set to auto detect"),
            ("联发科", "MediaTek"),
            ("自动检测", "Auto Detect"),
            ("初始化失败: ", "Initialization failed: "),
            ("请先连接 Fastboot 设备", "Connect a Fastboot device first"),
            ("请先连接设备并读取分区表", "Connect the device and read the partition table first"),
            ("请选择或勾选要读取的分区", "Select or check the partitions to read"),
            ("请选择或勾选要写入的分区", "Select or check the partitions to write"),
            ("请选择或勾选要擦除的分区", "Select or check the partitions to erase"),
            ("请先连接设备", "Connect the device first"),
            ("请先读取分区表", "Read the partition table first"),
            ("当前没有进行中的操作", "No operation is currently running"),
            ("已全选分区", "All partitions selected"),
            ("已取消全选", "All partitions deselected"),
            ("未找到分区: ", "Partition not found: "),
            ("已打开日志文件夹: ", "Opened log folder: "),
            ("打开日志失败: ", "Failed to open the log folder: "),
            ("打开链接失败: ", "Failed to open the link: "),
            ("已选择本地文件：", "Selected local file: "),
            ("文件不存在", "File does not exist"),
            ("本地图片设置成功: ", "Local image applied: "),
            ("图片加载失败：", "Image load failed: "),
            ("加载图片失败：", "Failed to load image: "),
            ("内存严重不足，请尝试重启应用", "Severe low-memory condition. Try restarting the app."),
            ("建议：关闭其他程序，释放内存", "Suggestion: close other programs to free memory."),
            ("正在连接 Fastboot 设备...", "Connecting to Fastboot device..."),
            ("连接失败，请检查设备是否处于 Fastboot 模式", "Connection failed. Check that the device is in Fastboot mode."),
            ("请先解析 Payload (本地文件或云端链接)", "Parse the payload first (local file or cloud URL)."),
            ("检测到云端 URL，开始解析...", "Cloud URL detected. Starting parse..."),
            ("无效的输入: 文件/文件夹不存在或 URL 格式错误", "Invalid input: the file/folder does not exist or the URL format is incorrect."),
            ("普通刷机脚本，将清除所有数据", "Standard flash script detected. All data will be erased."),
            ("执行: 重启系统...", "Executing: reboot system..."),
            ("执行: 重启到 Fastboot...", "Executing: reboot to Fastboot..."),
            ("执行: 重启到 Fastbootd...", "Executing: reboot to Fastbootd..."),
            ("执行: 重启到 Recovery...", "Executing: reboot to Recovery..."),
            ("重启失败: ", "Reboot failed: "),
            ("切换槽位失败", "Slot switch failed"),
            ("已打开设备管理器", "Device Manager opened"),
            ("用户取消了管理员权限请求", "The administrator permission request was canceled by the user."),
            ("不支持的驱动类型: ", "Unsupported driver type: "),
            ("等待连接", "Waiting"),
            ("待连接", "Waiting"),
            ("待命", "Idle"),
            ("已连接", "Connected"),
            ("未连接", "Disconnected"),
            ("已断开", "Disconnected"),
            ("已取消", "Canceled"),
            ("：", ": "),
            ("（", " ("),
            ("）", ")"),
            ("，", ", "),
            ("。", "."),
            ("！", "!"),
            ("？", "?"),
            ("「", "\""),
            ("」", "\"")
        };

        public static string CurrentLanguage => _currentLanguage;
        public static LanguageInfo CurrentLanguageInfo => SupportedLanguages.Find(l => l.Code == _currentLanguage)
            ?? SupportedLanguages.Find(l => l.Code == "en")
            ?? SupportedLanguages[0];
        public static event Action<string> LanguageChanged;

        public static string[] GetLanguageDisplayNames()
        {
            var names = new string[SupportedLanguages.Count];
            for (int i = 0; i < SupportedLanguages.Count; i++)
                names[i] = SupportedLanguages[i].NativeName;
            return names;
        }

        public static int GetCurrentLanguageIndex()
        {
            for (int i = 0; i < SupportedLanguages.Count; i++)
                if (SupportedLanguages[i].Code == _currentLanguage) return i;
            return SupportedLanguages.FindIndex(l => l.Code == "en");
        }

        public static string GetLanguageCodeByIndex(int index)
        {
            if (index >= 0 && index < SupportedLanguages.Count)
                return SupportedLanguages[index].Code;
            return "en";
        }

        public static void SetLanguage(string langCode)
        {
            const string englishCode = "en";
            string normalized = englishCode;

            if (string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _currentLanguage = normalized;
            SaveLanguageSetting();
            LanguageChanged?.Invoke(_currentLanguage);
        }

        public static void Initialize()
        {
            _currentLanguage = "en";

            try
            {
                _settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SakuraEDL", "language.txt");

                // Ship the desktop app in English-first mode and persist that default.
                SaveLanguageSetting();
            }
            catch { }
        }

        public static async Task DetectLanguageByIPAsync()
        {
            await Task.CompletedTask;
        }

        private static void SaveLanguageSetting()
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_settingsPath, _currentLanguage);
            }
            catch { }
        }

        public static string T(string key)
        {
            EnsureTranslationsLoaded();
            if (_translations.TryGetValue(_currentLanguage, out var langDict))
                if (langDict.TryGetValue(key, out var value)) return value;
            if (_translations.TryGetValue("en", out var enDict))
                if (enDict.TryGetValue(key, out var value)) return value;
            return key;
        }

        public static string TranslateLegacyText(string text)
        {
            if (string.IsNullOrEmpty(text) || !string.Equals(_currentLanguage, "en", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            if (!ContainsCjk(text))
            {
                return text;
            }

            string translated = text;
            for (int i = 0; i < LegacyEnglishReplacements.Length; i++)
            {
                translated = translated.Replace(LegacyEnglishReplacements[i].Source, LegacyEnglishReplacements[i].Target);
            }

            return translated;
        }

        private static bool ContainsCjk(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch >= 0x4E00 && ch <= 0x9FFF)
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnsureTranslationsLoaded()
        {
            if (_translations != null) return;
            lock (_lock)
            {
                if (_translations != null) return;
                _translations = CreateTranslations();
            }
        }

        private static Dictionary<string, Dictionary<string, string>> CreateTranslations()
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                ["zh"] = new Dictionary<string, string>
                {
                    // 标签页
                    ["tab.autoRoot"] = "自动root",
                    ["tab.qualcomm"] = "高通",
                    ["tab.mtk"] = "联发科",
                    ["tab.spd"] = "展讯",
                    ["tab.fastboot"] = "Fastboot",
                    ["tab.settings"] = "设置",
                    
                    // 高通引导选择
                    ["qualcomm.autoDetectBoot"] = "云端自动匹配",
                    
                    // 菜单
                    ["menu.quickRestart"] = "快捷重启",
                    ["menu.edlOps"] = "EDL操作",
                    ["menu.other"] = "其他",
                    ["menu.rebootSystem"] = "重启系统",
                    ["menu.rebootBootloader"] = "重启到Bootloader",
                    ["menu.rebootFastbootd"] = "重启到Fastbootd",
                    ["menu.rebootRecovery"] = "重启到Recovery",
                    ["menu.miKickEdl"] = "小米踢EDL",
                    ["menu.lenovoKickEdl"] = "联想/安卓踢EDL",
                    ["menu.eraseFrp"] = "擦除谷歌锁",
                    ["menu.switchSlot"] = "切换槽位",
                    ["menu.mergeSuper"] = "合并Super",
                    ["menu.extractPayload"] = "提取Payload",
                    ["menu.edlToEdl"] = "EDL到EDL",
                    ["menu.edlToFbd"] = "EDL到FBD",
                    ["menu.edlEraseFrp"] = "EDL擦除谷歌锁",
                    ["menu.edlSwitchSlot"] = "EDL切换槽位",
                    ["menu.activateLun"] = "激活LUN",
                    ["menu.deviceManager"] = "设备管理器",
                    ["menu.cmdPrompt"] = "CMD命令行",
                    ["menu.androidDriver"] = "安卓驱动",
                    ["menu.mtkDriver"] = "MTK驱动",
                    ["menu.qualcommDriver"] = "高通驱动",
                    ["menu.spdDriver"] = "展讯驱动",
                    ["menu.viewLog"] = "查看日志",
                    
                    // 设置页
                    ["settings.blur"] = "背景模糊度",
                    ["settings.wallpaper"] = "壁纸",
                    ["settings.preview"] = "预览",
                    ["settings.language"] = "语言",
                    ["settings.localWallpaper"] = "本地壁纸",
                    ["settings.apply"] = "应用",
                    
                    // 高通页面
                    ["qualcomm.cloudAuto"] = "云端自动匹配",
                    ["qualcomm.autoDetect"] = "自动识别",
                    ["qualcomm.selectProgrammer"] = "双击选择引导文件",
                    ["qualcomm.selectRawXml"] = "选择Raw XML",
                    ["qualcomm.browse"] = "浏览",
                    ["qualcomm.partTable"] = "分区表",
                    ["qualcomm.partition"] = "分区",
                    ["qualcomm.lun"] = "LUN",
                    ["qualcomm.size"] = "大小",
                    ["qualcomm.startSector"] = "起始扇区",
                    ["qualcomm.endSector"] = "结束扇区",
                    ["qualcomm.sectorCount"] = "扇区数",
                    ["qualcomm.startAddr"] = "起始地址",
                    ["qualcomm.endAddr"] = "结束地址",
                    ["qualcomm.filePath"] = "文件路径",
                    ["qualcomm.readPartTable"] = "读取分区表",
                    ["qualcomm.readPart"] = "读取分区",
                    ["qualcomm.writePart"] = "写入分区",
                    ["qualcomm.erasePart"] = "擦除分区",
                    ["qualcomm.stop"] = "停止",
                    ["qualcomm.findPart"] = "查找分区",
                    
                    // 选项
                    ["option.skipBoot"] = "跳过引导",
                    ["option.protectPart"] = "保护分区",
                    ["option.generateXml"] = "生成XML",
                    ["option.autoReboot"] = "自动重启",
                    ["option.selectAll"] = "全选",
                    ["option.keepData"] = "保留数据",
                    
                    // 设备信息
                    ["device.status"] = "设备状态",
                    ["device.noDevice"] = "未连接任何设备",
                    ["device.info"] = "信息",
                    ["device.brand"] = "品牌",
                    ["device.chip"] = "芯片",
                    ["device.ota"] = "OTA",
                    ["device.serial"] = "序列号",
                    ["device.model"] = "型号",
                    ["device.storage"] = "存储",
                    ["device.waiting"] = "等待连接",
                    ["device.log"] = "日志",
                    
                    // 状态栏
                    ["status.ready"] = "当前操作：空闲",
                    ["status.operation"] = "当前操作",
                    ["status.idle"] = "空闲",
                    ["status.speed"] = "速度",
                    ["status.time"] = "时间",
                    ["status.computer"] = "计算机",
                    ["status.bit"] = "位",
                    ["status.contactDev"] = "联系开发者",
                    
                    // 日志
                    ["log.loaded"] = "加载完成",
                    ["log.langChanged"] = "界面语言已切换为：{0}",
                    ["log.qualcommInit"] = "高通模块初始化完成",
                    ["log.fastbootInit"] = "Fastboot 模块初始化完成",
                    ["log.mtkInit"] = "联发科模块已初始化",
                    ["log.spdInit"] = "展讯模块初始化完成",
                    ["log.selectLoader"] = "[提示] 请从下拉列表选择云端 Loader 或浏览本地引导文件",
                    
                    // MTK 页面
                    ["mtk.da"] = "DA文件",
                    ["mtk.scatter"] = "Scatter文件",
                    ["mtk.auth"] = "认证文件",
                    ["mtk.bootMode"] = "启动模式",
                    ["mtk.authMethod"] = "认证方式",
                    ["mtk.storageType"] = "存储类型",
                    ["mtk.readInfo"] = "读取信息",
                    ["mtk.formatAll"] = "格式化全盘",
                    ["mtk.flash"] = "刷写",
                    ["mtk.readBack"] = "回读",
                    
                    // SPD 页面
                    ["spd.fdl"] = "FDL文件",
                    ["spd.pac"] = "PAC文件",
                    ["spd.readFlash"] = "读取Flash",
                    ["spd.writeFlash"] = "写入Flash",
                    ["spd.eraseFlash"] = "擦除Flash",
                    
                    // Fastboot 页面
                    ["fastboot.device"] = "设备",
                    ["fastboot.flash"] = "刷写",
                    ["fastboot.erase"] = "擦除",
                    ["fastboot.reboot"] = "重启",
                    ["fastboot.unlock"] = "解锁",
                    ["fastboot.lock"] = "上锁",
                    ["fastboot.execute"] = "执行",
                    ["fastboot.readInfo"] = "读取信息",
                    ["fastboot.fixFbd"] = "修复FBD",
                    ["fastboot.oplusFlash"] = "欧加刷写",
                    ["fastboot.lockBl"] = "锁定BL",
                    ["fastboot.clearData"] = "清除数据",
                    ["fastboot.switchSlotA"] = "切换A槽",
                    ["fastboot.extractImage"] = "提取镜像",
                    ["fastboot.fbdFlash"] = "FBD刷写",
                    ["fastboot.selectFlashBat"] = "选择flash Bat或输出路径",
                    ["fastboot.selectPayload"] = "选择Payload或输入URL",
                    ["fastboot.quickCommand"] = "执行快捷命令",
                    
                    // MTK 页面扩展
                    ["mtk.rebootDevice"] = "重启设备",
                    ["mtk.readImei"] = "读取IMEI",
                    ["mtk.writeImei"] = "写入IMEI",
                    ["mtk.backupNvram"] = "备份NVRAM",
                    ["mtk.restoreNvram"] = "恢复NVRAM",
                    ["mtk.formatData"] = "格式化Data",
                    ["mtk.unlockBl"] = "解锁BL",
                    ["mtk.exploit"] = "执行漏洞",
                    ["mtk.connect"] = "连接",
                    ["mtk.disconnect"] = "断开",
                    ["mtk.auto"] = "自动",
                    ["mtk.autoDetectBoot"] = "自动识别或自选引导",
                    ["mtk.cloudMatch"] = "云端自动匹配 (推荐)",
                    ["mtk.localSelect"] = "本地手动选择",
                    ["mtk.authMethod"] = "验证方式",
                    ["mtk.normalAuth"] = "正常验证 (签名DA)",
                    ["mtk.realmeCloud"] = "Realme云端签名",
                    ["mtk.bypassAuth"] = "绕过验证 (漏洞利用)",
                    
                    // SPD 页面扩展
                    ["spd.backupCalib"] = "备份校准",
                    ["spd.restoreCalib"] = "恢复校准",
                    ["spd.factoryReset"] = "恢复出厂",
                    ["spd.extractPac"] = "提取PAC",
                    ["spd.nvManager"] = "NV管理",
                    ["spd.rebootAfter"] = "刷机后重启",
                    ["spd.skipUserdata"] = "跳过Userdata",
                    ["spd.chipModel"] = "芯片型号",
                    ["spd.deviceModel"] = "设备型号",
                    
                    // 表格列
                    ["table.operation"] = "操作",
                    ["table.type"] = "类型",
                    ["table.address"] = "地址",
                    ["table.fileName"] = "文件名",
                    ["table.loadAddr"] = "加载地址",
                    ["table.offset"] = "偏移",
                    
                    // 设置页扩展
                    ["settings.clearCache"] = "清理缓存日志",
                    
                    // 开发中
                    ["dev.inProgress"] = "开发中...",
                    
                    // 遗漏的菜单
                    ["menu.edlFactoryReset"] = "EDL通用恢复出厂",
                    
                    // 标签页扩展
                    ["tab.partManage"] = "分区管理",
                    ["tab.fileManage"] = "文件管理",
                    
                    // 投屏功能
                    ["scrcpy.execute"] = "执行",
                    ["scrcpy.startMirror"] = "开启投屏",
                    ["scrcpy.fixMirror"] = "修复投屏异常",
                    ["scrcpy.flashZip"] = "刷入卡刷包",
                    ["scrcpy.screenOn"] = "屏幕常亮",
                    ["scrcpy.audioForward"] = "音频转发",
                    ["scrcpy.autoReconnect"] = "自动重连",
                    ["scrcpy.battery"] = "电池",
                    ["scrcpy.refreshRate"] = "刷新率",
                    ["scrcpy.resolution"] = "分辨率",
                    ["scrcpy.powerKey"] = "电源键",
                    ["scrcpy.recentKey"] = "后台键",
                    ["scrcpy.homeKey"] = "主页键",
                    ["scrcpy.backKey"] = "返回键",
                    
                    // 其他
                    ["status.loading"] = "「加载中...」",
                    ["app.version"] = "v3.0 · 永久免费"
                },
                
                ["en"] = new Dictionary<string, string>
                {
                    ["tab.autoRoot"] = "Auto Root",
                    ["tab.qualcomm"] = "Qualcomm",
                    ["tab.mtk"] = "MediaTek",
                    ["tab.spd"] = "Spreadtrum",
                    ["tab.fastboot"] = "Fastboot",
                    ["tab.settings"] = "Settings",
                    
                    ["qualcomm.autoDetectBoot"] = "Cloud Auto Match",
                    
                    ["menu.quickRestart"] = "Quick Restart",
                    ["menu.edlOps"] = "EDL Operations",
                    ["menu.other"] = "Other",
                    ["menu.rebootSystem"] = "Reboot System",
                    ["menu.rebootBootloader"] = "Reboot to Bootloader",
                    ["menu.rebootFastbootd"] = "Reboot to Fastbootd",
                    ["menu.rebootRecovery"] = "Reboot to Recovery",
                    ["menu.miKickEdl"] = "Mi Kick EDL",
                    ["menu.lenovoKickEdl"] = "Lenovo/Android Kick EDL",
                    ["menu.eraseFrp"] = "Erase FRP",
                    ["menu.switchSlot"] = "Switch Slot",
                    ["menu.mergeSuper"] = "Merge Super",
                    ["menu.extractPayload"] = "Extract Payload",
                    ["menu.edlToEdl"] = "EDL to EDL",
                    ["menu.edlToFbd"] = "EDL to FBD",
                    ["menu.edlEraseFrp"] = "EDL Erase FRP",
                    ["menu.edlSwitchSlot"] = "EDL Switch Slot",
                    ["menu.activateLun"] = "Activate LUN",
                    ["menu.deviceManager"] = "Device Manager",
                    ["menu.cmdPrompt"] = "CMD Prompt",
                    ["menu.androidDriver"] = "Android Driver",
                    ["menu.mtkDriver"] = "MTK Driver",
                    ["menu.qualcommDriver"] = "Qualcomm Driver",
                    ["menu.spdDriver"] = "SPD Driver",
                    ["menu.viewLog"] = "View Log",
                    
                    ["settings.blur"] = "Background Blur",
                    ["settings.wallpaper"] = "Wallpaper",
                    ["settings.preview"] = "Preview",
                    ["settings.language"] = "Language",
                    ["settings.localWallpaper"] = "Local Wallpaper",
                    ["settings.apply"] = "Apply",
                    
                    ["qualcomm.cloudAuto"] = "Cloud Auto Match",
                    ["qualcomm.autoDetect"] = "Auto Detect",
                    ["qualcomm.selectProgrammer"] = "Double-click to select programmer",
                    ["qualcomm.selectRawXml"] = "Select Raw XML",
                    ["qualcomm.browse"] = "Browse",
                    ["qualcomm.partTable"] = "Partition Table",
                    ["qualcomm.partition"] = "Partition",
                    ["qualcomm.lun"] = "LUN",
                    ["qualcomm.size"] = "Size",
                    ["qualcomm.startSector"] = "Start Sector",
                    ["qualcomm.endSector"] = "End Sector",
                    ["qualcomm.sectorCount"] = "Sector Count",
                    ["qualcomm.startAddr"] = "Start Address",
                    ["qualcomm.endAddr"] = "End Address",
                    ["qualcomm.filePath"] = "File Path",
                    ["qualcomm.readPartTable"] = "Read Partition Table",
                    ["qualcomm.readPart"] = "Read Partition",
                    ["qualcomm.writePart"] = "Write Partition",
                    ["qualcomm.erasePart"] = "Erase Partition",
                    ["qualcomm.stop"] = "Stop",
                    ["qualcomm.findPart"] = "Find Partition",
                    
                    ["option.skipBoot"] = "Skip Boot",
                    ["option.protectPart"] = "Protect Partitions",
                    ["option.generateXml"] = "Generate XML",
                    ["option.autoReboot"] = "Auto Reboot",
                    ["option.selectAll"] = "Select All",
                    ["option.keepData"] = "Keep Data",
                    
                    ["device.status"] = "Device Status",
                    ["device.noDevice"] = "No device connected",
                    ["device.info"] = "Info",
                    ["device.brand"] = "Brand",
                    ["device.chip"] = "Chip",
                    ["device.ota"] = "OTA",
                    ["device.serial"] = "Serial",
                    ["device.model"] = "Model",
                    ["device.storage"] = "Storage",
                    ["device.waiting"] = "Waiting",
                    ["device.log"] = "Log",
                    
                    ["status.ready"] = "Operation: Idle",
                    ["status.operation"] = "Operation",
                    ["status.idle"] = "Idle",
                    ["status.speed"] = "Speed",
                    ["status.time"] = "Time",
                    ["status.computer"] = "Computer",
                    ["status.bit"] = "-bit",
                    ["status.contactDev"] = "Contact Developer",
                    
                    ["log.loaded"] = "Loaded",
                    ["log.langChanged"] = "Language changed to: {0}",
                    ["log.qualcommInit"] = "Qualcomm module initialized",
                    ["log.fastbootInit"] = "Fastboot module initialized",
                    ["log.mtkInit"] = "MediaTek module initialized",
                    ["log.spdInit"] = "Spreadtrum module initialized",
                    ["log.selectLoader"] = "[Hint] Select cloud loader from dropdown or browse local programmer",
                    
                    ["mtk.da"] = "DA File",
                    ["mtk.scatter"] = "Scatter File",
                    ["mtk.auth"] = "Auth File",
                    ["mtk.bootMode"] = "Boot Mode",
                    ["mtk.authMethod"] = "Auth Method",
                    ["mtk.storageType"] = "Storage Type",
                    ["mtk.readInfo"] = "Read Info",
                    ["mtk.formatAll"] = "Format All",
                    ["mtk.flash"] = "Flash",
                    ["mtk.readBack"] = "Read Back",
                    
                    ["spd.fdl"] = "FDL File",
                    ["spd.pac"] = "PAC File",
                    ["spd.readFlash"] = "Read Flash",
                    ["spd.writeFlash"] = "Write Flash",
                    ["spd.eraseFlash"] = "Erase Flash",
                    
                    ["fastboot.device"] = "Device",
                    ["fastboot.flash"] = "Flash",
                    ["fastboot.erase"] = "Erase",
                    ["fastboot.reboot"] = "Reboot",
                    ["fastboot.unlock"] = "Unlock",
                    ["fastboot.lock"] = "Lock",
                    ["fastboot.execute"] = "Execute",
                    ["fastboot.readInfo"] = "Read Info",
                    ["fastboot.fixFbd"] = "Fix FBD",
                    ["fastboot.oplusFlash"] = "Oplus Flash",
                    ["fastboot.lockBl"] = "Lock BL",
                    ["fastboot.clearData"] = "Clear Data",
                    ["fastboot.switchSlotA"] = "Switch to Slot A",
                    ["fastboot.extractImage"] = "Extract Image",
                    ["fastboot.fbdFlash"] = "FBD Flash",
                    ["fastboot.selectFlashBat"] = "Select flash Bat or output path",
                    ["fastboot.selectPayload"] = "Select Payload or enter URL",
                    ["fastboot.quickCommand"] = "Execute quick command",
                    
                    ["mtk.rebootDevice"] = "Reboot Device",
                    ["mtk.readImei"] = "Read IMEI",
                    ["mtk.writeImei"] = "Write IMEI",
                    ["mtk.backupNvram"] = "Backup NVRAM",
                    ["mtk.restoreNvram"] = "Restore NVRAM",
                    ["mtk.formatData"] = "Format Data",
                    ["mtk.unlockBl"] = "Unlock BL",
                    ["mtk.exploit"] = "Run Exploit",
                    ["mtk.connect"] = "Connect",
                    ["mtk.disconnect"] = "Disconnect",
                    ["mtk.auto"] = "Auto",
                    ["mtk.autoDetectBoot"] = "Auto detect or select boot",
                    ["mtk.cloudMatch"] = "Cloud Auto Match (Recommended)",
                    ["mtk.localSelect"] = "Local Manual Select",
                    ["mtk.authMethod"] = "Auth Method",
                    ["mtk.normalAuth"] = "Normal Auth (Signed DA)",
                    ["mtk.realmeCloud"] = "Realme Cloud Sign",
                    ["mtk.bypassAuth"] = "Bypass Auth (Exploit)",
                    
                    ["spd.backupCalib"] = "Backup Calibration",
                    ["spd.restoreCalib"] = "Restore Calibration",
                    ["spd.factoryReset"] = "Factory Reset",
                    ["spd.extractPac"] = "Extract PAC",
                    ["spd.nvManager"] = "NV Manager",
                    ["spd.rebootAfter"] = "Reboot After Flash",
                    ["spd.skipUserdata"] = "Skip Userdata",
                    ["spd.chipModel"] = "Chip Model",
                    ["spd.deviceModel"] = "Device Model",
                    
                    ["table.operation"] = "Operation",
                    ["table.type"] = "Type",
                    ["table.address"] = "Address",
                    ["table.fileName"] = "File Name",
                    ["table.loadAddr"] = "Load Address",
                    ["table.offset"] = "Offset",
                    
                    ["settings.clearCache"] = "Clear Cache & Logs",
                    
                    ["dev.inProgress"] = "In Development...",
                    
                    ["menu.edlFactoryReset"] = "EDL Factory Reset",
                    
                    ["tab.partManage"] = "Partition Manager",
                    ["tab.fileManage"] = "File Manager",
                    
                    ["scrcpy.execute"] = "Execute",
                    ["scrcpy.startMirror"] = "Start Mirroring",
                    ["scrcpy.fixMirror"] = "Fix Mirroring",
                    ["scrcpy.flashZip"] = "Flash ZIP",
                    ["scrcpy.screenOn"] = "Keep Screen On",
                    ["scrcpy.audioForward"] = "Audio Forward",
                    ["scrcpy.autoReconnect"] = "Auto Reconnect",
                    ["scrcpy.battery"] = "Battery",
                    ["scrcpy.refreshRate"] = "Refresh Rate",
                    ["scrcpy.resolution"] = "Resolution",
                    ["scrcpy.powerKey"] = "Power",
                    ["scrcpy.recentKey"] = "Recent",
                    ["scrcpy.homeKey"] = "Home",
                    ["scrcpy.backKey"] = "Back",
                    
                    ["status.loading"] = "Loading...",
                    ["app.version"] = "v3.0 · Free Forever"
                },
                
                ["ja"] = new Dictionary<string, string>
                {
                    ["tab.autoRoot"] = "自動Root",
                    ["tab.qualcomm"] = "Qualcomm",
                    ["tab.mtk"] = "MediaTek",
                    ["tab.spd"] = "Spreadtrum",
                    ["tab.fastboot"] = "Fastboot",
                    ["tab.settings"] = "設定",
                    
                    ["qualcomm.autoDetectBoot"] = "クラウド自動マッチ",
                    
                    ["menu.quickRestart"] = "クイック再起動",
                    ["menu.edlOps"] = "EDL操作",
                    ["menu.other"] = "その他",
                    ["menu.rebootSystem"] = "システム再起動",
                    ["menu.rebootBootloader"] = "Bootloaderへ再起動",
                    ["menu.rebootFastbootd"] = "Fastbootdへ再起動",
                    ["menu.rebootRecovery"] = "Recoveryへ再起動",
                    ["menu.miKickEdl"] = "Mi EDLキック",
                    ["menu.lenovoKickEdl"] = "Lenovo/Android EDLキック",
                    ["menu.eraseFrp"] = "FRP消去",
                    ["menu.switchSlot"] = "スロット切替",
                    ["menu.mergeSuper"] = "Super統合",
                    ["menu.extractPayload"] = "Payload抽出",
                    ["menu.deviceManager"] = "デバイスマネージャー",
                    ["menu.cmdPrompt"] = "コマンドプロンプト",
                    ["menu.viewLog"] = "ログを見る",
                    
                    ["settings.blur"] = "背景ぼかし",
                    ["settings.wallpaper"] = "壁紙",
                    ["settings.preview"] = "プレビュー",
                    ["settings.language"] = "言語",
                    ["settings.localWallpaper"] = "ローカル壁紙",
                    ["settings.apply"] = "適用",
                    
                    ["qualcomm.cloudAuto"] = "クラウド自動マッチ",
                    ["qualcomm.autoDetect"] = "自動検出",
                    ["qualcomm.selectProgrammer"] = "ダブルクリックでプログラマー選択",
                    ["qualcomm.selectRawXml"] = "Raw XML選択",
                    ["qualcomm.browse"] = "参照",
                    ["qualcomm.partTable"] = "パーティションテーブル",
                    ["qualcomm.partition"] = "パーティション",
                    ["qualcomm.readPartTable"] = "パーティションテーブル読取",
                    ["qualcomm.readPart"] = "パーティション読取",
                    ["qualcomm.writePart"] = "パーティション書込",
                    ["qualcomm.erasePart"] = "パーティション消去",
                    ["qualcomm.stop"] = "停止",
                    ["qualcomm.findPart"] = "パーティション検索",
                    
                    ["option.skipBoot"] = "ブートスキップ",
                    ["option.protectPart"] = "パーティション保護",
                    ["option.generateXml"] = "XML生成",
                    ["option.autoReboot"] = "自動再起動",
                    ["option.selectAll"] = "全選択",
                    ["option.keepData"] = "データ保持",
                    
                    ["device.status"] = "デバイス状態",
                    ["device.noDevice"] = "デバイス未接続",
                    ["device.info"] = "情報",
                    ["device.brand"] = "ブランド",
                    ["device.chip"] = "チップ",
                    ["device.waiting"] = "接続待ち",
                    ["device.log"] = "ログ",
                    
                    ["status.ready"] = "操作：待機中",
                    ["status.operation"] = "操作",
                    ["status.idle"] = "待機中",
                    ["status.speed"] = "速度",
                    ["status.time"] = "時間",
                    ["status.computer"] = "コンピュータ",
                    ["status.bit"] = "ビット",
                    ["status.contactDev"] = "開発者連絡",
                    
                    ["log.loaded"] = "読み込み完了",
                    ["log.langChanged"] = "言語を変更しました：{0}",
                    ["log.qualcommInit"] = "Qualcommモジュール初期化完了",
                    ["log.fastbootInit"] = "Fastbootモジュール初期化完了",
                    ["log.mtkInit"] = "MediaTekモジュール初期化完了",
                    ["log.spdInit"] = "Spreadtrumモジュール初期化完了",
                    ["log.selectLoader"] = "[ヒント] ドロップダウンからクラウドローダーを選択するか、ローカルファイルを参照",
                    
                    ["fastboot.execute"] = "実行",
                    ["fastboot.readInfo"] = "情報読取",
                    ["fastboot.fixFbd"] = "FBD修復",
                    ["fastboot.oplusFlash"] = "Oplus書込",
                    ["fastboot.lockBl"] = "BLロック",
                    ["fastboot.clearData"] = "データ消去",
                    ["fastboot.switchSlotA"] = "スロットA切替",
                    ["fastboot.extractImage"] = "イメージ抽出",
                    ["fastboot.fbdFlash"] = "FBD書込",
                    ["fastboot.selectFlashBat"] = "flash Batまたは出力パス選択",
                    ["fastboot.selectPayload"] = "PayloadまたはURL入力",
                    ["fastboot.quickCommand"] = "クイックコマンド実行",
                    
                    ["mtk.rebootDevice"] = "デバイス再起動",
                    ["mtk.readImei"] = "IMEI読取",
                    ["mtk.writeImei"] = "IMEI書込",
                    ["mtk.backupNvram"] = "NVRAMバックアップ",
                    ["mtk.restoreNvram"] = "NVRAM復元",
                    ["mtk.formatData"] = "Dataフォーマット",
                    ["mtk.unlockBl"] = "BLアンロック",
                    ["mtk.exploit"] = "エクスプロイト実行",
                    ["mtk.connect"] = "接続",
                    ["mtk.disconnect"] = "切断",
                    ["mtk.auto"] = "自動",
                    ["mtk.autoDetectBoot"] = "自動検出またはブート選択",
                    ["mtk.cloudMatch"] = "クラウド自動マッチ (推奨)",
                    ["mtk.localSelect"] = "ローカル手動選択",
                    ["mtk.authMethod"] = "認証方式",
                    ["mtk.normalAuth"] = "通常認証 (署名DA)",
                    ["mtk.realmeCloud"] = "Realmeクラウド署名",
                    ["mtk.bypassAuth"] = "認証バイパス (エクスプロイト)",
                    
                    ["spd.backupCalib"] = "キャリブレーションバックアップ",
                    ["spd.restoreCalib"] = "キャリブレーション復元",
                    ["spd.factoryReset"] = "出荷時リセット",
                    ["spd.extractPac"] = "PAC抽出",
                    ["spd.nvManager"] = "NVマネージャー",
                    ["spd.rebootAfter"] = "書込後再起動",
                    ["spd.skipUserdata"] = "Userdataスキップ",
                    ["spd.chipModel"] = "チップ型番",
                    ["spd.deviceModel"] = "デバイス型番",
                    
                    ["table.operation"] = "操作",
                    ["table.type"] = "タイプ",
                    ["table.address"] = "アドレス",
                    ["table.fileName"] = "ファイル名",
                    ["table.loadAddr"] = "ロードアドレス",
                    ["table.offset"] = "オフセット",
                    
                    ["settings.clearCache"] = "キャッシュとログを消去",
                    
                    ["dev.inProgress"] = "開発中...",
                    
                    ["menu.edlFactoryReset"] = "EDL工場出荷時リセット",
                    
                    ["tab.partManage"] = "パーティション管理",
                    ["tab.fileManage"] = "ファイル管理",
                    
                    ["scrcpy.execute"] = "実行",
                    ["scrcpy.startMirror"] = "ミラーリング開始",
                    ["scrcpy.fixMirror"] = "ミラーリング修復",
                    ["scrcpy.flashZip"] = "ZIP書込",
                    ["scrcpy.screenOn"] = "画面常時オン",
                    ["scrcpy.audioForward"] = "オーディオ転送",
                    ["scrcpy.autoReconnect"] = "自動再接続",
                    ["scrcpy.battery"] = "バッテリー",
                    ["scrcpy.refreshRate"] = "リフレッシュレート",
                    ["scrcpy.resolution"] = "解像度",
                    ["scrcpy.powerKey"] = "電源",
                    ["scrcpy.recentKey"] = "履歴",
                    ["scrcpy.homeKey"] = "ホーム",
                    ["scrcpy.backKey"] = "戻る",
                    
                    ["status.loading"] = "読み込み中...",
                    ["app.version"] = "v3.0 · 永久無料"
                },
                
                ["ko"] = new Dictionary<string, string>
                {
                    ["tab.autoRoot"] = "자동 Root",
                    ["tab.qualcomm"] = "퀄컴",
                    ["tab.mtk"] = "미디어텍",
                    ["tab.spd"] = "스프레드트럼",
                    ["tab.fastboot"] = "Fastboot",
                    ["tab.settings"] = "설정",
                    
                    ["qualcomm.autoDetectBoot"] = "클라우드 자동 매칭",
                    
                    ["menu.quickRestart"] = "빠른 재시작",
                    ["menu.edlOps"] = "EDL 작업",
                    ["menu.other"] = "기타",
                    ["menu.rebootSystem"] = "시스템 재시작",
                    ["menu.deviceManager"] = "장치 관리자",
                    ["menu.viewLog"] = "로그 보기",
                    
                    ["settings.blur"] = "배경 흐림",
                    ["settings.wallpaper"] = "배경화면",
                    ["settings.preview"] = "미리보기",
                    ["settings.language"] = "언어",
                    ["settings.localWallpaper"] = "로컬 배경화면",
                    ["settings.apply"] = "적용",
                    
                    ["qualcomm.cloudAuto"] = "클라우드 자동 매칭",
                    ["qualcomm.autoDetect"] = "자동 감지",
                    ["qualcomm.selectProgrammer"] = "더블클릭하여 프로그래머 선택",
                    ["qualcomm.selectRawXml"] = "Raw XML 선택",
                    ["qualcomm.browse"] = "찾아보기",
                    ["qualcomm.partTable"] = "파티션 테이블",
                    ["qualcomm.partition"] = "파티션",
                    ["qualcomm.readPartTable"] = "파티션 테이블 읽기",
                    ["qualcomm.readPart"] = "파티션 읽기",
                    ["qualcomm.writePart"] = "파티션 쓰기",
                    ["qualcomm.erasePart"] = "파티션 지우기",
                    ["qualcomm.stop"] = "중지",
                    ["qualcomm.findPart"] = "파티션 찾기",
                    
                    ["option.skipBoot"] = "부팅 건너뛰기",
                    ["option.protectPart"] = "파티션 보호",
                    ["option.generateXml"] = "XML 생성",
                    ["option.autoReboot"] = "자동 재부팅",
                    ["option.selectAll"] = "전체 선택",
                    ["option.keepData"] = "데이터 유지",
                    
                    ["device.status"] = "장치 상태",
                    ["device.noDevice"] = "연결된 장치 없음",
                    ["device.info"] = "정보",
                    ["device.brand"] = "브랜드",
                    ["device.chip"] = "칩",
                    ["device.waiting"] = "대기 중",
                    ["device.log"] = "로그",
                    
                    ["status.ready"] = "작업: 대기 중",
                    ["status.operation"] = "작업",
                    ["status.idle"] = "대기 중",
                    ["status.speed"] = "속도",
                    ["status.time"] = "시간",
                    ["status.computer"] = "컴퓨터",
                    ["status.bit"] = "비트",
                    ["status.contactDev"] = "개발자 연락",
                    
                    ["log.loaded"] = "로드 완료",
                    ["log.langChanged"] = "언어가 변경되었습니다: {0}",
                    ["log.qualcommInit"] = "퀄컴 모듈 초기화 완료",
                    ["log.fastbootInit"] = "Fastboot 모듈 초기화 완료",
                    ["log.mtkInit"] = "미디어텍 모듈 초기화 완료",
                    ["log.spdInit"] = "스프레드트럼 모듈 초기화 완료",
                    ["log.selectLoader"] = "[힌트] 드롭다운에서 클라우드 로더를 선택하거나 로컬 파일 찾아보기",
                    
                    ["fastboot.execute"] = "실행",
                    ["fastboot.readInfo"] = "정보 읽기",
                    ["fastboot.fixFbd"] = "FBD 수정",
                    ["fastboot.oplusFlash"] = "Oplus 플래시",
                    ["fastboot.lockBl"] = "BL 잠금",
                    ["fastboot.clearData"] = "데이터 삭제",
                    ["fastboot.switchSlotA"] = "슬롯 A 전환",
                    ["fastboot.extractImage"] = "이미지 추출",
                    ["fastboot.fbdFlash"] = "FBD 플래시",
                    ["fastboot.selectFlashBat"] = "flash Bat 또는 출력 경로 선택",
                    ["fastboot.selectPayload"] = "Payload 또는 URL 입력",
                    ["fastboot.quickCommand"] = "빠른 명령 실행",
                    
                    ["mtk.rebootDevice"] = "장치 재시작",
                    ["mtk.readImei"] = "IMEI 읽기",
                    ["mtk.writeImei"] = "IMEI 쓰기",
                    ["mtk.backupNvram"] = "NVRAM 백업",
                    ["mtk.restoreNvram"] = "NVRAM 복원",
                    ["mtk.formatData"] = "Data 포맷",
                    ["mtk.unlockBl"] = "BL 잠금해제",
                    ["mtk.exploit"] = "익스플로잇 실행",
                    ["mtk.connect"] = "연결",
                    ["mtk.disconnect"] = "연결 해제",
                    ["mtk.auto"] = "자동",
                    ["mtk.autoDetectBoot"] = "자동 감지 또는 부트 선택",
                    ["mtk.cloudMatch"] = "클라우드 자동 매칭 (권장)",
                    ["mtk.localSelect"] = "로컬 수동 선택",
                    ["mtk.authMethod"] = "인증 방식",
                    ["mtk.normalAuth"] = "일반 인증 (서명된 DA)",
                    ["mtk.realmeCloud"] = "Realme 클라우드 서명",
                    ["mtk.bypassAuth"] = "인증 우회 (익스플로잇)",
                    
                    ["spd.backupCalib"] = "캘리브레이션 백업",
                    ["spd.restoreCalib"] = "캘리브레이션 복원",
                    ["spd.factoryReset"] = "공장 초기화",
                    ["spd.extractPac"] = "PAC 추출",
                    ["spd.nvManager"] = "NV 관리자",
                    ["spd.rebootAfter"] = "플래시 후 재시작",
                    ["spd.skipUserdata"] = "Userdata 건너뛰기",
                    ["spd.chipModel"] = "칩 모델",
                    ["spd.deviceModel"] = "장치 모델",
                    
                    ["table.operation"] = "작업",
                    ["table.type"] = "유형",
                    ["table.address"] = "주소",
                    ["table.fileName"] = "파일명",
                    ["table.loadAddr"] = "로드 주소",
                    ["table.offset"] = "오프셋",
                    
                    ["settings.clearCache"] = "캐시 및 로그 삭제",
                    
                    ["dev.inProgress"] = "개발 중...",
                    
                    ["menu.edlFactoryReset"] = "EDL 공장 초기화",
                    
                    ["tab.partManage"] = "파티션 관리",
                    ["tab.fileManage"] = "파일 관리",
                    
                    ["scrcpy.execute"] = "실행",
                    ["scrcpy.startMirror"] = "미러링 시작",
                    ["scrcpy.fixMirror"] = "미러링 수정",
                    ["scrcpy.flashZip"] = "ZIP 플래시",
                    ["scrcpy.screenOn"] = "화면 켜짐 유지",
                    ["scrcpy.audioForward"] = "오디오 전달",
                    ["scrcpy.autoReconnect"] = "자동 재연결",
                    ["scrcpy.battery"] = "배터리",
                    ["scrcpy.refreshRate"] = "주사율",
                    ["scrcpy.resolution"] = "해상도",
                    ["scrcpy.powerKey"] = "전원",
                    ["scrcpy.recentKey"] = "최근",
                    ["scrcpy.homeKey"] = "홈",
                    ["scrcpy.backKey"] = "뒤로",
                    
                    ["status.loading"] = "로딩 중...",
                    ["app.version"] = "v3.0 · 영구 무료"
                },
                
                ["ru"] = new Dictionary<string, string>
                {
                    ["tab.autoRoot"] = "Авто Root",
                    ["tab.qualcomm"] = "Qualcomm",
                    ["tab.mtk"] = "MediaTek",
                    ["tab.spd"] = "Spreadtrum",
                    ["tab.fastboot"] = "Fastboot",
                    ["tab.settings"] = "Настройки",
                    
                    ["qualcomm.autoDetectBoot"] = "Облачный автоподбор",
                    
                    ["menu.quickRestart"] = "Быстрый перезапуск",
                    ["menu.edlOps"] = "Операции EDL",
                    ["menu.other"] = "Другое",
                    ["menu.rebootSystem"] = "Перезагрузка системы",
                    ["menu.deviceManager"] = "Диспетчер устройств",
                    ["menu.viewLog"] = "Просмотр журнала",
                    
                    ["settings.blur"] = "Размытие фона",
                    ["settings.wallpaper"] = "Обои",
                    ["settings.preview"] = "Предпросмотр",
                    ["settings.language"] = "Язык",
                    ["settings.localWallpaper"] = "Локальные обои",
                    ["settings.apply"] = "Применить",
                    
                    ["qualcomm.cloudAuto"] = "Облачное автосопоставление",
                    ["qualcomm.autoDetect"] = "Автоопределение",
                    ["qualcomm.selectProgrammer"] = "Дважды щелкните для выбора программатора",
                    ["qualcomm.selectRawXml"] = "Выбрать Raw XML",
                    ["qualcomm.browse"] = "Обзор",
                    ["qualcomm.partTable"] = "Таблица разделов",
                    ["qualcomm.partition"] = "Раздел",
                    ["qualcomm.readPartTable"] = "Чтение таблицы разделов",
                    ["qualcomm.readPart"] = "Чтение раздела",
                    ["qualcomm.writePart"] = "Запись раздела",
                    ["qualcomm.erasePart"] = "Стирание раздела",
                    ["qualcomm.stop"] = "Стоп",
                    ["qualcomm.findPart"] = "Найти раздел",
                    
                    ["option.skipBoot"] = "Пропустить загрузку",
                    ["option.protectPart"] = "Защита разделов",
                    ["option.generateXml"] = "Создать XML",
                    ["option.autoReboot"] = "Авто перезагрузка",
                    ["option.selectAll"] = "Выбрать все",
                    ["option.keepData"] = "Сохранить данные",
                    
                    ["device.status"] = "Статус устройства",
                    ["device.noDevice"] = "Устройство не подключено",
                    ["device.info"] = "Информация",
                    ["device.brand"] = "Бренд",
                    ["device.chip"] = "Чип",
                    ["device.waiting"] = "Ожидание",
                    ["device.log"] = "Журнал",
                    
                    ["status.ready"] = "Операция: Готов",
                    ["status.operation"] = "Операция",
                    ["status.idle"] = "Готов",
                    ["status.speed"] = "Скорость",
                    ["status.time"] = "Время",
                    ["status.computer"] = "Компьютер",
                    ["status.bit"] = "-бит",
                    ["status.contactDev"] = "Связаться с разработчиком",
                    
                    ["log.loaded"] = "Загружено",
                    ["log.langChanged"] = "Язык изменен на: {0}",
                    ["log.qualcommInit"] = "Модуль Qualcomm инициализирован",
                    ["log.fastbootInit"] = "Модуль Fastboot инициализирован",
                    ["log.mtkInit"] = "Модуль MediaTek инициализирован",
                    ["log.spdInit"] = "Модуль Spreadtrum инициализирован",
                    ["log.selectLoader"] = "[Подсказка] Выберите облачный загрузчик из списка или укажите локальный файл",
                    
                    ["fastboot.execute"] = "Выполнить",
                    ["fastboot.readInfo"] = "Чтение инфо",
                    ["fastboot.fixFbd"] = "Исправить FBD",
                    ["fastboot.oplusFlash"] = "Oplus прошивка",
                    ["fastboot.lockBl"] = "Заблокировать BL",
                    ["fastboot.clearData"] = "Очистить данные",
                    ["fastboot.switchSlotA"] = "Переключить на слот A",
                    ["fastboot.extractImage"] = "Извлечь образ",
                    ["fastboot.fbdFlash"] = "FBD прошивка",
                    ["fastboot.selectFlashBat"] = "Выбрать flash Bat или путь",
                    ["fastboot.selectPayload"] = "Выбрать Payload или ввести URL",
                    ["fastboot.quickCommand"] = "Выполнить быструю команду",
                    
                    ["mtk.rebootDevice"] = "Перезагрузить устройство",
                    ["mtk.readImei"] = "Чтение IMEI",
                    ["mtk.writeImei"] = "Запись IMEI",
                    ["mtk.backupNvram"] = "Резервная копия NVRAM",
                    ["mtk.restoreNvram"] = "Восстановление NVRAM",
                    ["mtk.formatData"] = "Форматировать Data",
                    ["mtk.unlockBl"] = "Разблокировать BL",
                    ["mtk.exploit"] = "Запустить эксплойт",
                    ["mtk.connect"] = "Подключить",
                    ["mtk.disconnect"] = "Отключить",
                    ["mtk.auto"] = "Авто",
                    ["mtk.autoDetectBoot"] = "Автоопределение или выбор загрузки",
                    ["mtk.cloudMatch"] = "Облачный автоподбор (Рекомендуется)",
                    ["mtk.localSelect"] = "Локальный ручной выбор",
                    ["mtk.authMethod"] = "Метод авторизации",
                    ["mtk.normalAuth"] = "Обычная авторизация (Подписанный DA)",
                    ["mtk.realmeCloud"] = "Realme облачная подпись",
                    ["mtk.bypassAuth"] = "Обход авторизации (Эксплойт)",
                    
                    ["spd.backupCalib"] = "Резервная копия калибровки",
                    ["spd.restoreCalib"] = "Восстановление калибровки",
                    ["spd.factoryReset"] = "Сброс до заводских",
                    ["spd.extractPac"] = "Извлечь PAC",
                    ["spd.nvManager"] = "NV Менеджер",
                    ["spd.rebootAfter"] = "Перезагрузка после прошивки",
                    ["spd.skipUserdata"] = "Пропустить Userdata",
                    ["spd.chipModel"] = "Модель чипа",
                    ["spd.deviceModel"] = "Модель устройства",
                    
                    ["table.operation"] = "Операция",
                    ["table.type"] = "Тип",
                    ["table.address"] = "Адрес",
                    ["table.fileName"] = "Имя файла",
                    ["table.loadAddr"] = "Адрес загрузки",
                    ["table.offset"] = "Смещение",
                    
                    ["settings.clearCache"] = "Очистить кэш и логи",
                    
                    ["dev.inProgress"] = "В разработке...",
                    
                    ["menu.edlFactoryReset"] = "EDL сброс до заводских",
                    
                    ["tab.partManage"] = "Управление разделами",
                    ["tab.fileManage"] = "Файловый менеджер",
                    
                    ["scrcpy.execute"] = "Выполнить",
                    ["scrcpy.startMirror"] = "Начать трансляцию",
                    ["scrcpy.fixMirror"] = "Исправить трансляцию",
                    ["scrcpy.flashZip"] = "Прошить ZIP",
                    ["scrcpy.screenOn"] = "Экран всегда включен",
                    ["scrcpy.audioForward"] = "Переадресация аудио",
                    ["scrcpy.autoReconnect"] = "Автопереподключение",
                    ["scrcpy.battery"] = "Батарея",
                    ["scrcpy.refreshRate"] = "Частота обновления",
                    ["scrcpy.resolution"] = "Разрешение",
                    ["scrcpy.powerKey"] = "Питание",
                    ["scrcpy.recentKey"] = "Недавние",
                    ["scrcpy.homeKey"] = "Домой",
                    ["scrcpy.backKey"] = "Назад",
                    
                    ["status.loading"] = "Загрузка...",
                    ["app.version"] = "v3.0 · Бесплатно навсегда"
                },
                
                ["es"] = new Dictionary<string, string>
                {
                    ["tab.autoRoot"] = "Auto Root",
                    ["tab.qualcomm"] = "Qualcomm",
                    ["tab.mtk"] = "MediaTek",
                    ["tab.spd"] = "Spreadtrum",
                    ["tab.fastboot"] = "Fastboot",
                    ["tab.settings"] = "Ajustes",
                    
                    ["qualcomm.autoDetectBoot"] = "Coincidencia automática en la nube",
                    
                    ["menu.quickRestart"] = "Reinicio rápido",
                    ["menu.edlOps"] = "Operaciones EDL",
                    ["menu.other"] = "Otros",
                    ["menu.rebootSystem"] = "Reiniciar sistema",
                    ["menu.deviceManager"] = "Administrador de dispositivos",
                    ["menu.viewLog"] = "Ver registro",
                    
                    ["settings.blur"] = "Desenfoque de fondo",
                    ["settings.wallpaper"] = "Fondo de pantalla",
                    ["settings.preview"] = "Vista previa",
                    ["settings.language"] = "Idioma",
                    ["settings.localWallpaper"] = "Fondo local",
                    ["settings.apply"] = "Aplicar",
                    
                    ["qualcomm.cloudAuto"] = "Coincidencia automática en la nube",
                    ["qualcomm.autoDetect"] = "Detección automática",
                    ["qualcomm.selectProgrammer"] = "Doble clic para seleccionar programador",
                    ["qualcomm.selectRawXml"] = "Seleccionar Raw XML",
                    ["qualcomm.browse"] = "Examinar",
                    ["qualcomm.partTable"] = "Tabla de particiones",
                    ["qualcomm.partition"] = "Partición",
                    ["qualcomm.readPartTable"] = "Leer tabla de particiones",
                    ["qualcomm.readPart"] = "Leer partición",
                    ["qualcomm.writePart"] = "Escribir partición",
                    ["qualcomm.erasePart"] = "Borrar partición",
                    ["qualcomm.stop"] = "Detener",
                    ["qualcomm.findPart"] = "Buscar partición",
                    
                    ["option.skipBoot"] = "Omitir arranque",
                    ["option.protectPart"] = "Proteger particiones",
                    ["option.generateXml"] = "Generar XML",
                    ["option.autoReboot"] = "Reinicio automático",
                    ["option.selectAll"] = "Seleccionar todo",
                    ["option.keepData"] = "Mantener datos",
                    
                    ["device.status"] = "Estado del dispositivo",
                    ["device.noDevice"] = "Ningún dispositivo conectado",
                    ["device.info"] = "Información",
                    ["device.brand"] = "Marca",
                    ["device.chip"] = "Chip",
                    ["device.waiting"] = "Esperando",
                    ["device.log"] = "Registro",
                    
                    ["status.ready"] = "Operación: Listo",
                    ["status.operation"] = "Operación",
                    ["status.idle"] = "Listo",
                    ["status.speed"] = "Velocidad",
                    ["status.time"] = "Tiempo",
                    ["status.computer"] = "Ordenador",
                    ["status.bit"] = " bits",
                    ["status.contactDev"] = "Contactar desarrollador",
                    
                    ["log.loaded"] = "Cargado",
                    ["log.langChanged"] = "Idioma cambiado a: {0}",
                    ["log.qualcommInit"] = "Módulo Qualcomm inicializado",
                    ["log.fastbootInit"] = "Módulo Fastboot inicializado",
                    ["log.mtkInit"] = "Módulo MediaTek inicializado",
                    ["log.spdInit"] = "Módulo Spreadtrum inicializado",
                    ["log.selectLoader"] = "[Sugerencia] Seleccione el cargador en la nube del menú o examine el archivo local",
                    
                    ["fastboot.execute"] = "Ejecutar",
                    ["fastboot.readInfo"] = "Leer Info",
                    ["fastboot.fixFbd"] = "Reparar FBD",
                    ["fastboot.oplusFlash"] = "Flash Oplus",
                    ["fastboot.lockBl"] = "Bloquear BL",
                    ["fastboot.clearData"] = "Borrar Datos",
                    ["fastboot.switchSlotA"] = "Cambiar a Slot A",
                    ["fastboot.extractImage"] = "Extraer Imagen",
                    ["fastboot.fbdFlash"] = "Flash FBD",
                    ["fastboot.selectFlashBat"] = "Seleccionar flash Bat o ruta",
                    ["fastboot.selectPayload"] = "Seleccionar Payload o introducir URL",
                    ["fastboot.quickCommand"] = "Ejecutar comando rápido",
                    
                    ["mtk.rebootDevice"] = "Reiniciar Dispositivo",
                    ["mtk.readImei"] = "Leer IMEI",
                    ["mtk.writeImei"] = "Escribir IMEI",
                    ["mtk.backupNvram"] = "Copia de NVRAM",
                    ["mtk.restoreNvram"] = "Restaurar NVRAM",
                    ["mtk.formatData"] = "Formatear Data",
                    ["mtk.unlockBl"] = "Desbloquear BL",
                    ["mtk.exploit"] = "Ejecutar Exploit",
                    ["mtk.connect"] = "Conectar",
                    ["mtk.disconnect"] = "Desconectar",
                    ["mtk.auto"] = "Auto",
                    ["mtk.autoDetectBoot"] = "Autodetectar o seleccionar arranque",
                    ["mtk.cloudMatch"] = "Coincidencia automática en la nube (Recomendado)",
                    ["mtk.localSelect"] = "Selección manual local",
                    ["mtk.authMethod"] = "Método de autenticación",
                    ["mtk.normalAuth"] = "Auth normal (DA firmado)",
                    ["mtk.realmeCloud"] = "Firma en la nube Realme",
                    ["mtk.bypassAuth"] = "Bypass de auth (Exploit)",
                    
                    ["spd.backupCalib"] = "Copia de calibración",
                    ["spd.restoreCalib"] = "Restaurar calibración",
                    ["spd.factoryReset"] = "Restablecer fábrica",
                    ["spd.extractPac"] = "Extraer PAC",
                    ["spd.nvManager"] = "Gestor NV",
                    ["spd.rebootAfter"] = "Reiniciar después de flash",
                    ["spd.skipUserdata"] = "Omitir Userdata",
                    ["spd.chipModel"] = "Modelo de chip",
                    ["spd.deviceModel"] = "Modelo de dispositivo",
                    
                    ["table.operation"] = "Operación",
                    ["table.type"] = "Tipo",
                    ["table.address"] = "Dirección",
                    ["table.fileName"] = "Nombre de archivo",
                    ["table.loadAddr"] = "Dirección de carga",
                    ["table.offset"] = "Desplazamiento",
                    
                    ["settings.clearCache"] = "Limpiar caché y registros",
                    
                    ["dev.inProgress"] = "En desarrollo...",
                    
                    ["menu.edlFactoryReset"] = "EDL restablecimiento de fábrica",
                    
                    ["tab.partManage"] = "Gestor de particiones",
                    ["tab.fileManage"] = "Gestor de archivos",
                    
                    ["scrcpy.execute"] = "Ejecutar",
                    ["scrcpy.startMirror"] = "Iniciar espejo",
                    ["scrcpy.fixMirror"] = "Reparar espejo",
                    ["scrcpy.flashZip"] = "Flash ZIP",
                    ["scrcpy.screenOn"] = "Pantalla siempre encendida",
                    ["scrcpy.audioForward"] = "Reenvío de audio",
                    ["scrcpy.autoReconnect"] = "Reconexión automática",
                    ["scrcpy.battery"] = "Batería",
                    ["scrcpy.refreshRate"] = "Tasa de refresco",
                    ["scrcpy.resolution"] = "Resolución",
                    ["scrcpy.powerKey"] = "Encendido",
                    ["scrcpy.recentKey"] = "Recientes",
                    ["scrcpy.homeKey"] = "Inicio",
                    ["scrcpy.backKey"] = "Atrás",
                    
                    ["status.loading"] = "Cargando...",
                    ["app.version"] = "v3.0 · Gratis para siempre"
                }
            };
        }
    }
}
