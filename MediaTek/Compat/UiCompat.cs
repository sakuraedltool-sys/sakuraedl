// Compatibility shims for Form1.MediaTek.cs and Form1.MediaTek.UI.cs
// These types bridge the old SakuraEDL UI layer to the new mtkclient-based backend.
// They will be fully wired up during UI integration (TODO item #15).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// ============================================================================
// SakuraEDL.MediaTek.Models
// ============================================================================
namespace SakuraEDL.MediaTek.Models
{
    public enum MtkDeviceState
    {
        Disconnected,
        Handshaking,
        Brom,
        Preloader,
        Da1Loaded,
        Da2Loaded,
        Error
    }

    public class MtkChipInfo
    {
        public ushort HwCode { get; set; }
        public ushort HwVer { get; set; }
        public ushort SwVer { get; set; }
        public string ChipName { get; set; }
        public string DaMode { get; set; }
        public bool SupportsXFlash { get; set; }
        public bool HasExploit { get; set; }

        public string GetChipName()
        {
            if (!string.IsNullOrEmpty(ChipName)) return ChipName;
            return $"MT{HwCode:X4}";
        }
    }

    public class MtkDeviceInfo
    {
        public MtkChipInfo ChipInfo { get; set; }
        public byte[] MeId { get; set; }
        public byte[] SocId { get; set; }
        public string MeIdHex => MeId != null ? BitConverter.ToString(MeId).Replace("-", "") : null;
        public string SocIdHex => SocId != null ? BitConverter.ToString(SocId).Replace("-", "") : null;
    }

    public class MtkPartitionInfo
    {
        public string Name { get; set; }
        public string Type { get; set; } = "";
        public ulong Offset { get; set; }
        public ulong Size { get; set; }
        public ulong StartSector { get; set; }

        public override string ToString() => $"{Name} (0x{Offset:X}-0x{Offset + Size:X})";
    }
}

// ============================================================================
// SakuraEDL.MediaTek.Common
// ============================================================================
namespace SakuraEDL.MediaTek.Common
{
    public static class MtkChipAliases
    {
        private static readonly Dictionary<ushort, string[]> Aliases = new Dictionary<ushort, string[]>
        {
            { 0x0717, new[] { "Helio A20", "Helio P22" } },
            { 0x0766, new[] { "Helio P35", "Helio G35" } },
            { 0x0788, new[] { "Helio P65", "Helio G85" } },
            { 0x0725, new[] { "Helio P60", "Helio P70" } },
            { 0x0813, new[] { "Helio G90", "Helio G95" } },
            { 0x0886, new[] { "Dimensity 800" } },
            { 0x0816, new[] { "Dimensity 1000" } },
            { 0x0950, new[] { "Dimensity 1200" } },
            { 0x0900, new[] { "Dimensity 9000" } },
            { 0x1296, new[] { "Dimensity 9200" } },
        };

        public static string[] GetAliases(ushort hwCode)
        {
            return Aliases.TryGetValue(hwCode, out var aliases) ? aliases : null;
        }
    }

    public class MtkPortInfo
    {
        public string ComPort { get; set; }
        public ushort Vid { get; set; }
        public ushort Pid { get; set; }
    }

    public class MtkPortDetector : IDisposable
    {
        // MediaTek USB VID
        private const ushort MTK_VID = 0x0E8D;
        // Known PIDs: BROM=0x0003, Preloader=0x2000/0x2001, DA=0x20FF
        private static readonly ushort[] MTK_PIDS = { 0x0003, 0x2000, 0x2001, 0x20FF };

        /// <summary>
        /// 轮询检测 MediaTek USB VCOM 端口，超时返回 null。
        /// </summary>
        public async Task<MtkPortInfo> WaitForDeviceAsync(int timeoutMs, CancellationToken ct)
        {
            int elapsed = 0;
            const int pollInterval = 500;

            while (elapsed < timeoutMs && !ct.IsCancellationRequested)
            {
                var port = ScanForMtkPort();
                if (port != null) return port;

                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                elapsed += pollInterval;
            }
            return null;
        }

        /// <summary>
        /// 通过 WMI 查询 Win32_PnPEntity 检测 MTK USB 串口。
        /// </summary>
        private MtkPortInfo ScanForMtkPort()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        string deviceId = obj["DeviceID"]?.ToString() ?? "";
                        string caption = obj["Caption"]?.ToString() ?? "";

                        // 检查是否为 MediaTek VID
                        string vidStr = $"VID_{MTK_VID:X4}";
                        if (deviceId.IndexOf(vidStr, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        // 提取 PID
                        ushort pid = 0;
                        int pidIdx = deviceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
                        if (pidIdx >= 0 && pidIdx + 8 <= deviceId.Length)
                        {
                            ushort.TryParse(deviceId.Substring(pidIdx + 4, 4),
                                System.Globalization.NumberStyles.HexNumber, null, out pid);
                        }

                        // 提取 COM 端口号
                        string comPort = null;
                        int comStart = caption.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
                        if (comStart >= 0)
                        {
                            int comEnd = caption.IndexOf(')', comStart);
                            if (comEnd > comStart)
                                comPort = caption.Substring(comStart + 1, comEnd - comStart - 1);
                        }

                        if (!string.IsNullOrEmpty(comPort))
                        {
                            return new MtkPortInfo
                            {
                                ComPort = comPort,
                                Vid = MTK_VID,
                                Pid = pid
                            };
                        }
                    }
                }
            }
            catch
            {
                // WMI 不可用时回退到串口枚举
                return ScanFallback();
            }
            return null;
        }

        /// <summary>
        /// WMI 不可用时的回退方案：枚举所有串口尝试握手。
        /// </summary>
        private MtkPortInfo ScanFallback()
        {
            foreach (string port in System.IO.Ports.SerialPort.GetPortNames())
            {
                try
                {
                    using (var sp = new System.IO.Ports.SerialPort(port, 115200))
                    {
                        sp.ReadTimeout = 200;
                        sp.WriteTimeout = 200;
                        sp.Open();
                        // 发送 MTK BROM 握手字节 0xA0
                        sp.Write(new byte[] { 0xA0 }, 0, 1);
                        byte[] buf = new byte[1];
                        int read = sp.Read(buf, 0, 1);
                        if (read == 1 && buf[0] == 0x5F) // BROM ACK
                        {
                            return new MtkPortInfo { ComPort = port, Vid = MTK_VID, Pid = 0x0003 };
                        }
                    }
                }
                catch { /* 端口不可用或超时 */ }
            }
            return null;
        }

        public void Dispose() { }
    }

    public static class RealmeAuthService
    {
        public static string FindAllInOneSignature(string firmwarePath)
        {
            if (string.IsNullOrEmpty(firmwarePath)) return null;
            string sigFile = System.IO.Path.Combine(firmwarePath, "all-in-one-signature.bin");
            return System.IO.File.Exists(sigFile) ? sigFile : null;
        }
    }
}

// ============================================================================
// SakuraEDL.MediaTek.Database
// ============================================================================
namespace SakuraEDL.MediaTek.Database
{
    public class MtkChipEntry
    {
        public ushort HwCode { get; set; }
        public string ChipName { get; set; }
        public string Description { get; set; } = "";
        public bool HasExploit { get; set; }
    }

    public static class MtkChipDatabase
    {
        public static List<MtkChipEntry> GetAllChips()
        {
            var chips = new List<MtkChipEntry>();
            foreach (var kv in Config.BromConfig.HwConfig)
            {
                chips.Add(new MtkChipEntry
                {
                    HwCode = kv.Key,
                    ChipName = kv.Value.Name,
                    HasExploit = kv.Value.Blacklist != null && kv.Value.Blacklist.Length > 0
                });
            }
            return chips;
        }

        public static MtkChipEntry GetChip(ushort hwCode)
        {
            if (Config.BromConfig.HwConfig.TryGetValue(hwCode, out var cfg))
            {
                return new MtkChipEntry
                {
                    HwCode = hwCode,
                    ChipName = cfg.Name,
                    HasExploit = cfg.Blacklist != null && cfg.Blacklist.Length > 0
                };
            }
            return null;
        }

        public static string GetExploitType(ushort hwCode)
        {
            if (Config.BromConfig.HwConfig.TryGetValue(hwCode, out var cfg))
            {
                if (cfg.DaMode == Config.DAmodes.XML) return "Carbonara";
                if (cfg.Blacklist != null && cfg.Blacklist.Length > 0) return "Kamakiri2";
            }
            return "None";
        }

        public static bool IsAllinoneSignatureSupported(ushort hwCode)
        {
            // AllinoneSignature was removed per user request
            return false;
        }

        public static List<MtkChipEntry> GetAllinoneSignatureChips()
        {
            return new List<MtkChipEntry>();
        }
    }

    public static class MtkDaDatabase
    {
        public static bool SupportsExploit(ushort hwCode)
        {
            if (Config.BromConfig.HwConfig.TryGetValue(hwCode, out var cfg))
                return cfg.Blacklist != null && cfg.Blacklist.Length > 0;
            return false;
        }
    }
}

// ============================================================================
// SakuraEDL.MediaTek.Services
// ============================================================================
namespace SakuraEDL.MediaTek.Services
{
    using SakuraEDL.MediaTek.Models;

    public class MediatekService
    {
        private MtkClass _mtk;
        private string _pendingDaPath;
        private string _pendingDa1Path;
        private string _pendingDa2Path;
        private string _pendingAuthPath;
        private string _verificationMode = "Auto";

        public MtkDeviceState State { get; set; } = MtkDeviceState.Disconnected;
        public MtkDeviceInfo CurrentDevice { get; set; }
        public MtkChipInfo ChipInfo => CurrentDevice?.ChipInfo;
        public bool IsXFlashMode => _mtk?.Config?.ChipConfig?.DaMode == Config.DAmodes.XFLASH;

        public event Action<int, int> OnProgress;
        public event Action<MtkDeviceState> OnStateChanged;
        public event Action<string, Color> OnLog;

        public void SetVerificationMode(string mode)
        {
            _verificationMode = string.IsNullOrWhiteSpace(mode) ? "Auto" : mode;
            if (_mtk != null)
            {
                if (_mtk.DaLoader != null)
                {
                    _mtk.DaLoader.Patch = IsExploitVerificationMode();
                }
                ApplyPendingDaConfiguration();
            }
        }

        public void SetDaFilePath(string path)
        {
            _pendingDaPath = path;
            ApplyPendingDaConfiguration();
        }

        public void SetCustomDa1(string path)
        {
            _pendingDa1Path = path;
            ApplyPendingDaConfiguration();
        }

        public void SetCustomDa2(string path)
        {
            _pendingDa2Path = path;
            ApplyPendingDaConfiguration();
        }

        public void SetAuthFilePath(string path)
        {
            _pendingAuthPath = path;
            ApplyPendingDaConfiguration();
        }
        public byte[] SignatureData { get; set; }

        private bool IsExploitVerificationMode()
        {
            return string.Equals(_verificationMode, "Exploit", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeSplitDaComponent(string daPath)
        {
            if (string.IsNullOrWhiteSpace(daPath))
            {
                return false;
            }

            string fileName = Path.GetFileName(daPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            return fileName.Equals("DA_BR.bin", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("DA_BR", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("DA_PL", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("download_agent", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldAvoidCustomDaInExploitMode(string daPath)
        {
            if (!IsExploitVerificationMode())
            {
                return false;
            }

            return LooksLikeSplitDaComponent(daPath);
        }

        private bool TryApplyPreferredExploitLoader()
        {
            foreach (string candidate in ResolveDefaultDaLoaders())
            {
                if (ApplyDaLoaderToRuntime(candidate))
                {
                    OnLog?.Invoke($"[MTK] Exploit mode using generic loader: {Path.GetFileName(candidate)}", Color.Orange);
                    return true;
                }
            }

            return false;
        }

        private void ApplyPendingDaConfiguration()
        {
            if (_mtk?.Config == null)
            {
                return;
            }

            if (_mtk.DaLoader != null)
            {
                _mtk.DaLoader.Patch = IsExploitVerificationMode();
            }

            if (!string.IsNullOrWhiteSpace(_pendingAuthPath) && File.Exists(_pendingAuthPath))
            {
                _mtk.Config.Auth = _pendingAuthPath;
                OnLog?.Invoke($"[MTK] Auth configured: {Path.GetFileName(_pendingAuthPath)}", Color.Cyan);
            }

            if (IsExploitVerificationMode())
            {
                // Most firmware DA_BR binaries are signed for official tools only.
                if (!string.IsNullOrWhiteSpace(_pendingDa1Path) || ShouldAvoidCustomDaInExploitMode(_pendingDaPath))
                {
                    if (TryApplyPreferredExploitLoader())
                    {
                        return;
                    }
                }
            }

            // Split DA1/DA2 is not natively supported in current backend format; fall back to DA1 path.
            if (!string.IsNullOrWhiteSpace(_pendingDa1Path) && !string.IsNullOrWhiteSpace(_pendingDa2Path))
            {
                if (File.Exists(_pendingDa1Path) && File.Exists(_pendingDa2Path))
                {
                    OnLog?.Invoke("[MTK] Split DA1/DA2 selected, falling back to DA1 loader parsing.", Color.Orange);
                    if (ApplyDaLoaderToRuntime(_pendingDa1Path))
                    {
                        return;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_pendingDaPath) && File.Exists(_pendingDaPath))
            {
                if (LooksLikeSplitDaComponent(_pendingDaPath))
                {
                    OnLog?.Invoke(
                        $"[MTK] {Path.GetFileName(_pendingDaPath)} looks like split DA stage file; selecting generic loader table.",
                        Color.Orange);
                    string autoDa = ResolveDefaultDaLoader();
                    if (!string.IsNullOrWhiteSpace(autoDa) && ApplyDaLoaderToRuntime(autoDa))
                    {
                        return;
                    }
                }

                ApplyDaLoaderToRuntime(_pendingDaPath);
            }
        }

        private bool EnsureDaLoaderReady()
        {
            if (_mtk?.DaLoader?.DaConfigInstance == null)
            {
                return false;
            }

            // Apply user-selected settings first.
            ApplyPendingDaConfiguration();

            var daCfg = _mtk.DaLoader.DaConfigInstance;
            if (daCfg.DaSetup != null && daCfg.DaSetup.Count > 0)
            {
                return true;
            }

            // Fallback to known bundled DA files.
            string autoDa = ResolveDefaultDaLoader();
            if (!string.IsNullOrWhiteSpace(autoDa))
            {
                OnLog?.Invoke($"[MTK] Auto-selected DA loader: {Path.GetFileName(autoDa)}", Color.Cyan);
                return ApplyDaLoaderToRuntime(autoDa);
            }

            OnLog?.Invoke("[MTK] No DA loader available. Select a valid DA file first.", Color.Red);
            return false;
        }

        private bool ApplyDaLoaderToRuntime(string daPath)
        {
            if (_mtk?.Config == null || _mtk?.DaLoader?.DaConfigInstance == null || string.IsNullOrWhiteSpace(daPath))
            {
                return false;
            }

            if (!File.Exists(daPath))
            {
                OnLog?.Invoke($"[MTK] DA file not found: {daPath}", Color.Red);
                return false;
            }

            var daCfg = _mtk.DaLoader.DaConfigInstance;
            _mtk.Config.Loader = daPath;

            // Rebuild DA table from the selected loader.
            daCfg.DaLoader = null;
            daCfg.LoaderPath = null;
            daCfg.DaSetup?.Clear();

            bool parsed = daCfg.ParseDaLoader(daPath);
            if (!parsed || daCfg.DaSetup == null || daCfg.DaSetup.Count == 0)
            {
                OnLog?.Invoke($"[MTK] Failed to parse DA loader: {Path.GetFileName(daPath)}", Color.Red);
                return false;
            }

            // Validate loader against current chip after handshake/chip detection.
            if (_mtk.Config.HwCode != 0)
            {
                daCfg.DaLoader = null;
                var selected = daCfg.Setup();
                if (selected == null)
                {
                    OnLog?.Invoke($"[MTK] Loader {Path.GetFileName(daPath)} has no entry for chip 0x{_mtk.Config.HwCode:X4}.", Color.Red);
                    return false;
                }

                OnLog?.Invoke(
                    $"[MTK] DA entry selected: chip=0x{selected.HwCode:X4}, regions={selected.Regions.Count}, entryIndex={selected.EntryRegionIndex}",
                    Color.Cyan);
            }

            OnLog?.Invoke($"[MTK] DA loader configured: {Path.GetFileName(daPath)}", Color.Cyan);
            return true;
        }

        private string ResolveDefaultDaLoader()
        {
            foreach (string candidate in ResolveDefaultDaLoaders())
            {
                return candidate;
            }

            return null;
        }

        private IEnumerable<string> ResolveDefaultDaLoaders()
        {
            var mode = _mtk?.Config?.ChipConfig?.DaMode ?? Config.DAmodes.XFLASH;
            string[] preferredFiles = mode switch
            {
                Config.DAmodes.XML => new[] { "MTK_DA_V6.bin", "MTK_DA_V5.bin", "MTK_AllInOne_DA_mt6590.bin", "MTK_AllInOne_DA_iot.bin" },
                Config.DAmodes.LEGACY => new[] { "MTK_AllInOne_DA_mt6590.bin", "MTK_DA_V5.bin", "MTK_DA_V6.bin", "MTK_AllInOne_DA_iot.bin" },
                _ => new[] { "MTK_DA_V5.bin", "MTK_DA_V6.bin", "MTK_AllInOne_DA_mt6590.bin", "MTK_AllInOne_DA_iot.bin" }
            };

            foreach (string fileName in preferredFiles)
            {
                string found = FindLoaderFileInKnownPaths(fileName);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    yield return found;
                }
            }
        }

        private static string FindLoaderFileInKnownPaths(string fileName)
        {
            foreach (string dir in EnumerateLoaderDirs())
            {
                try
                {
                    string path = Path.Combine(dir, fileName);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // Ignore bad directory entries.
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateLoaderDirs()
        {
            var roots = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Environment.CurrentDirectory
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string root in roots)
            {
                string current = root;
                for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
                {
                    yield return Path.Combine(current, "Loader");
                    yield return Path.Combine(current, "mtkclient", "mtkclient-main", "mtkclient", "Loader");

                    var parent = Directory.GetParent(current);
                    current = parent?.FullName;
                }
            }
        }

        public void ConfigureRealmeAuth(string apiUrl, string apiKey, string account, SakuraEDL.MediaTek.Auth.SignServerType serverType)
        {
            // TODO: Wire up Realme cloud auth configuration
        }

        public async Task<bool> AuthWithAllInOneSignatureAsync(string sigPath, CancellationToken ct)
        {
            // AllinoneSignature removed per user request
            return await Task.FromResult(false);
        }

        public async Task<byte[]> GetGsmFutureSignatureAsync(string projectNo, string nvCode, string newSw, string oldSw, CancellationToken ct)
        {
            // TODO: Implement cloud signing API call
            return await Task.FromResult<byte[]>(null);
        }

        public async Task<bool> ExecuteRealmeAuthWithSignatureAsync(byte[] signature, CancellationToken ct)
        {
            // TODO: Execute auth with pre-fetched signature
            return await Task.FromResult(false);
        }

        public async Task<bool> ConnectAsync(string comPort, int baudRate, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    _mtk = new MtkClass();
                    _mtk.OnInfo = msg => OnLog?.Invoke($"[MTK] {msg}", Color.Cyan);
                    _mtk.OnError = msg => OnLog?.Invoke($"[MTK] {msg}", Color.Red);
                    _mtk.OnWarning = msg => OnLog?.Invoke($"[MTK] {msg}", Color.Orange);

                    // Apply user-selected DA/Auth paths before init (needed for DAA auth flow).
                    ApplyPendingDaConfiguration();

                    State = MtkDeviceState.Handshaking;
                    OnStateChanged?.Invoke(State);

                    _mtk.Port.PortName = comPort;
                    if (!_mtk.Init(comPort))
                    {
                        OnLog?.Invoke(
                            $"[MTK] Preloader handshake failed on {comPort}. Reconnect in BROM mode (hold Vol+/Vol-) and retry.",
                            Color.Red);
                        _mtk?.Dispose();
                        _mtk = null;
                        State = MtkDeviceState.Error;
                        OnStateChanged?.Invoke(State);
                        return false;
                    }

                    // Ensure preselected DA/Auth is applied right after handshake/chip detection.
                    ApplyPendingDaConfiguration();

                    CurrentDevice = new MtkDeviceInfo
                    {
                        ChipInfo = new MtkChipInfo
                        {
                            HwCode = _mtk.Config.HwCode,
                            HwVer = _mtk.Config.HwVer,
                            ChipName = _mtk.Config.ChipConfig?.Name ?? $"MT{_mtk.Config.HwCode:X4}",
                            DaMode = _mtk.Config.ChipConfig?.DaMode.ToString() ?? "Unknown",
                            SupportsXFlash = _mtk.Config.ChipConfig?.DaMode == Config.DAmodes.XFLASH,
                            HasExploit = _mtk.Config.ChipConfig?.Blacklist?.Length > 0
                        },
                        MeId = _mtk.Config.Meid,
                        SocId = _mtk.Config.SocId
                    };

                    State = _mtk.Config.IsBrom ? MtkDeviceState.Brom : MtkDeviceState.Preloader;
                    OnStateChanged?.Invoke(State);
                    return true;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[MTK] Connect error: {ex.Message}", Color.Red);
                    _mtk?.Dispose();
                    _mtk = null;
                    State = MtkDeviceState.Error;
                    OnStateChanged?.Invoke(State);
                    return false;
                }
            }, ct);
        }

        public async Task<bool> LoadDaAsync(CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                if (_mtk == null) return false;
                if (!EnsureDaLoaderReady())
                {
                    State = MtkDeviceState.Error;
                    OnStateChanged?.Invoke(State);
                    return false;
                }

                State = MtkDeviceState.Da1Loaded;
                OnStateChanged?.Invoke(State);

                bool ok = _mtk.UploadDa();
                if (!ok)
                {
                    OnLog?.Invoke("[MTK] Primary DA upload failed. Retrying fallback loaders...", Color.Orange);
                    ok = TryFallbackDaUpload();
                }

                if (!ok)
                {
                    string loader = Path.GetFileName(_mtk.Config?.Loader ?? string.Empty);
                    OnLog?.Invoke(
                        $"[MTK] DA upload failed for chip 0x{_mtk.Config?.HwCode:X4} using {loader}.",
                        Color.Red);
                }

                State = ok ? MtkDeviceState.Da2Loaded : MtkDeviceState.Error;
                OnStateChanged?.Invoke(State);
                return ok;
            }, ct);
        }

        private bool TryFallbackDaUpload()
        {
            if (_mtk == null)
            {
                return false;
            }

            string currentLoader = _mtk.Config?.Loader ?? string.Empty;
            foreach (string candidate in ResolveDefaultDaLoaders())
            {
                if (string.Equals(candidate, currentLoader, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                OnLog?.Invoke($"[MTK] Fallback DA try: {Path.GetFileName(candidate)}", Color.Orange);
                if (!ApplyDaLoaderToRuntime(candidate))
                {
                    continue;
                }

                _mtk.State = SakuraEDL.MediaTek.MtkState.PreloaderReady;
                if (_mtk.DaLoader != null)
                {
                    _mtk.DaLoader.Patch = IsExploitVerificationMode();
                }

                if (_mtk.UploadDa())
                {
                    OnLog?.Invoke($"[MTK] Fallback DA success: {Path.GetFileName(candidate)}", Color.LimeGreen);
                    return true;
                }
            }

            return false;
        }

        public async Task<List<MtkPartitionInfo>> ReadPartitionTableAsync(CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var result = new List<MtkPartitionInfo>();
                if (_mtk?.DaHandler == null)
                {
                    OnLog?.Invoke("[MTK] DaHandler not initialized", Color.Red);
                    return result;
                }
                var gpt = _mtk.DaHandler.ReadGpt();
                if (gpt != null)
                {
                    foreach (var entry in gpt)
                    {
                        result.Add(new MtkPartitionInfo
                        {
                            Name = entry.Name,
                            Offset = entry.StartLba * 512,
                            Size = entry.SizeBytes,
                            StartSector = entry.StartLba
                        });
                    }
                }
                return result;
            }, ct);
        }

        public async Task<bool> ReadPartitionAsync(string name, string outputPath, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                if (_mtk?.DaHandler == null) return false;
                return _mtk.DaHandler.ReadPartition(name, outputPath,
                    pct => OnProgress?.Invoke(pct, 100));
            }, ct);
        }

        public async Task<bool> ReadPartitionAsync(string name, string outputPath, ulong size, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                if (_mtk?.DaHandler == null) return false;
                return _mtk.DaHandler.ReadPartition(name, outputPath,
                    pct => OnProgress?.Invoke(pct, 100));
            }, ct);
        }

        public async Task<bool> WritePartitionAsync(string name, string imagePath, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                if (_mtk?.DaHandler == null) return false;
                return _mtk.DaHandler.WritePartition(name, imagePath,
                    pct => OnProgress?.Invoke(pct, 100));
            }, ct);
        }

        public async Task<bool> ErasePartitionAsync(string name, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                if (_mtk?.DaHandler == null) return false;
                return _mtk.DaHandler.ErasePartition(name);
            }, ct);
        }

        public void Dispose()
        {
            _mtk?.Dispose();
            _mtk = null;
        }

        public async Task<bool> RebootAsync(CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                if (_mtk?.DaLoader?.Xft != null)
                    return _mtk.DaLoader.Xft.Shutdown();
                return false;
            }, ct);
        }

        public async Task<bool> RunAllinoneSignatureExploitAsync(
            string daPath = null, string sigPath = null, CancellationToken ct = default)
        {
            // AllinoneSignature removed per user request
            return await Task.FromResult(false);
        }

        public SakuraEDL.MediaTek.Auth.RealmSignRequest GetRealmeSignRequest()
        {
            if (CurrentDevice?.ChipInfo == null) return null;
            return new SakuraEDL.MediaTek.Auth.RealmSignRequest
            {
                HwCode = CurrentDevice.ChipInfo.HwCode,
                MeId = CurrentDevice.MeIdHex,
                SocId = CurrentDevice.SocIdHex
            };
        }
    }
}

// ============================================================================
// SakuraEDL.MediaTek.UI
// ============================================================================
namespace SakuraEDL.MediaTek.UI
{
    using SakuraEDL.MediaTek.Models;

    public class MediatekUIController
    {
        private readonly Action<string, Color> _log;
        private readonly Action<string> _debug;

        public event Action<int, int> OnProgress;
        public event Action<MtkDeviceState> OnStateChanged;
        public event Action<MtkDeviceInfo> OnDeviceConnected;
        public event Action<MtkDeviceInfo> OnDeviceDisconnected;
        public event Action<List<MtkPartitionInfo>> OnPartitionTableLoaded;

        public MediatekUIController(Action<string, Color> log, Action<string> debug)
        {
            _log = log;
            _debug = debug;
        }

        public void ReportProgress(int current, int total) => OnProgress?.Invoke(current, total);
        public void ReportState(MtkDeviceState state) => OnStateChanged?.Invoke(state);
        public void ReportDeviceConnected(MtkDeviceInfo device) => OnDeviceConnected?.Invoke(device);
        public void ReportDeviceDisconnected(MtkDeviceInfo device) => OnDeviceDisconnected?.Invoke(device);
        public void ReportPartitions(List<MtkPartitionInfo> parts) => OnPartitionTableLoaded?.Invoke(parts);

        public void Dispose() { }
        public void StopPortMonitoring() { }
        public void StartPortMonitoring() { }
    }
}

// ============================================================================
// SakuraEDL.MediaTek.Auth - SignServerType & RealmSignRequest
// ============================================================================
namespace SakuraEDL.MediaTek.Auth
{
    public enum SignServerType
    {
        Realme,
        Oppo,
        OnePlus
    }

    public class RealmSignRequest
    {
        public ushort HwCode { get; set; }
        public string MeId { get; set; }
        public string SocId { get; set; }
        public string Platform { get; set; }
        public string Chipset { get; set; }
        public string SerialNumber { get; set; }
        public string Challenge { get; set; }
        public string ProjectNo { get; set; }
        public string NvCode { get; set; }
    }
}
