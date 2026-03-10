// ============================================================================
// SakuraEDL - Spreadtrum Service | 展讯服务
// ============================================================================
// 重构版 — 消除 God Object 膨胀，统一错误处理
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SakuraEDL.Common;
using SakuraEDL.Spreadtrum.Common;
using SakuraEDL.Spreadtrum.Exploit;
using SakuraEDL.Spreadtrum.Protocol;
using SakuraEDL.Spreadtrum.ISP;

namespace SakuraEDL.Spreadtrum.Services
{
    public class SpreadtrumService : IDisposable
    {
        private FdlClient _client;
        private SprdPortDetector _portDetector;
        private PacParser _pacParser;
        private CancellationTokenSource _cts;
        private SprdExploitService _exploitService;
        private DiagClient _diagClient;
        private EmmcPartitionManager _ispManager;
        private Watchdog _watchdog;

        // 事件
        public event Action<string, Color> OnLog;
        public event Action<int, int> OnProgress;
        public event Action<SprdDeviceState> OnStateChanged;
        public event Action<SprdDeviceInfo> OnDeviceConnected;
        public event Action<SprdDeviceInfo> OnDeviceDisconnected;
        public event Action<SprdVulnerabilityCheckResult> OnVulnerabilityDetected;
        public event Action<SprdExploitResult> OnExploitCompleted;

        // 属性
        public bool IsConnected => _client?.IsConnected ?? false;
        public bool IsBromMode => _client?.IsBromMode ?? true;
        public FdlStage CurrentStage => _client?.CurrentStage ?? FdlStage.None;
        public SprdDeviceState State => _client?.State ?? SprdDeviceState.Disconnected;
        public PacInfo CurrentPac { get; private set; }
        public List<SprdPartitionInfo> CachedPartitions { get; private set; }
        public uint ChipId { get; private set; }
        public string CustomFdl1Path { get; private set; }
        public string CustomFdl2Path { get; private set; }
        public uint CustomFdl1Address { get; private set; }
        public uint CustomFdl2Address { get; private set; }
        public EmmcPartitionManager IspManager => _ispManager;
        public bool IsIspReady => _ispManager?.IsReady ?? false;

        public SpreadtrumService()
        {
            _pacParser = new PacParser(msg => Log(msg, Color.Gray));
            _portDetector = new SprdPortDetector();
            _exploitService = new SprdExploitService((msg, color) => Log(msg, color));
            _portDetector.OnLog += msg => Log(msg, Color.Gray);
            _portDetector.OnDeviceConnected += dev => OnDeviceConnected?.Invoke(dev);
            _portDetector.OnDeviceDisconnected += dev => OnDeviceDisconnected?.Invoke(dev);
            _exploitService.OnVulnerabilityDetected += r => OnVulnerabilityDetected?.Invoke(r);
            _exploitService.OnExploitCompleted += r => OnExploitCompleted?.Invoke(r);
            _watchdog = new Watchdog("Spreadtrum", WatchdogManager.DefaultTimeouts.Spreadtrum, msg => Log(msg, Color.Gray));
            _watchdog.OnTimeout += OnWatchdogTimeout;
        }

        #region 配置

        public void SetChipId(uint id) { ChipId = id; _client?.SetChipId(id); }
        public void SetCustomFdl1(string path, uint addr) { CustomFdl1Path = path; CustomFdl1Address = addr; _client?.SetCustomFdl1(path, addr); }
        public void SetCustomFdl2(string path, uint addr) { CustomFdl2Path = path; CustomFdl2Address = addr; _client?.SetCustomFdl2(path, addr); }
        public void ClearCustomFdl() { CustomFdl1Path = CustomFdl2Path = null; CustomFdl1Address = CustomFdl2Address = 0; _client?.ClearCustomFdl(); }
        public void FeedWatchdog() => _watchdog?.Feed();
        public void StartWatchdog(string op) => _watchdog?.Start(op);
        public void StopWatchdog() => _watchdog?.Stop();
        public SprdExploitService GetExploitService() => _exploitService;

        private void OnWatchdogTimeout(object sender, WatchdogTimeoutEventArgs e)
        {
            Log($"[展讯] 看门狗超时: {e.OperationName} ({e.ElapsedTime.TotalSeconds:F1}s)", Color.Orange);
            if (e.TimeoutCount >= 3) { e.ShouldReset = false; Disconnect(); }
        }

        #endregion

        #region 设备连接

        public void StartDeviceMonitor() => _portDetector.StartWatching();
        public void StopDeviceMonitor() => _portDetector.StopWatching();
        public IReadOnlyList<SprdDeviceInfo> GetConnectedDevices() => _portDetector.ConnectedDevices;

        public async Task<bool> ConnectAsync(string comPort, int baudRate = 115200)
        {
            return await SafeAsync("连接", async () =>
            {
                Disconnect();
                _client = new FdlClient();
                _client.OnLog += msg => Log(msg, Color.White);
                _client.OnProgress += (c, t) => OnProgress?.Invoke(c, t);
                _client.OnStateChanged += s => OnStateChanged?.Invoke(s);
                ApplyConfig();
                return await _client.ConnectAsync(comPort, baudRate);
            });
        }

        public async Task<bool> WaitAndConnectAsync(int timeoutMs = 30000)
        {
            Log("[展讯] 等待设备...", Color.Yellow);
            ResetCts();
            var dev = await _portDetector.WaitForDeviceAsync(timeoutMs, _cts.Token);
            return dev != null && await ConnectAsync(dev.ComPort);
        }

        public void Disconnect()
        {
            _cts?.Cancel();
            _client?.Disconnect();
            _client?.Dispose();
            _client = null;
        }

        public async Task<bool> ConnectAndInitializeAsync(string comPort, int baudRate = 115200)
        {
            return await ConnectAsync(comPort, baudRate) && await InitializeDeviceAsync();
        }

        private void ApplyConfig()
        {
            if (_client == null) return;
            if (ChipId > 0) _client.SetChipId(ChipId);
            if (!string.IsNullOrEmpty(CustomFdl1Path) || CustomFdl1Address > 0) _client.SetCustomFdl1(CustomFdl1Path, CustomFdl1Address);
            if (!string.IsNullOrEmpty(CustomFdl2Path) || CustomFdl2Address > 0) _client.SetCustomFdl2(CustomFdl2Path, CustomFdl2Address);
        }

        #endregion

        #region FDL 初始化

        public async Task<bool> InitializeDeviceAsync()
        {
            if (!IsConnected) { Log("[展讯] 未连接", Color.Red); return false; }
            if (CurrentStage == FdlStage.FDL2) return true;

            // FDL1
            if (IsBromMode || CurrentStage == FdlStage.None)
            {
                var (data, addr) = ResolveFdl(1);
                if (data == null || addr == 0) { Log("[展讯] 缺少 FDL1", Color.Orange); return false; }
                if (!await _client.DownloadFdlAsync(data, addr, FdlStage.FDL1)) { Log("[展讯] FDL1 失败", Color.Red); return false; }
            }

            // FDL2
            if (CurrentStage == FdlStage.FDL1)
            {
                var (data, addr) = ResolveFdl(2);
                if (data == null || addr == 0) { Log("[展讯] 缺少 FDL2", Color.Orange); return false; }
                if (!await _client.DownloadFdlAsync(data, addr, FdlStage.FDL2)) { Log("[展讯] FDL2 失败", Color.Red); return false; }
            }

            return CurrentStage == FdlStage.FDL2;
        }

        /// <summary>
        /// 解析 FDL 数据和地址: 自定义路径 > PAC 内置 > 芯片数据库
        /// </summary>
        private (byte[] data, uint addr) ResolveFdl(int stage)
        {
            string customPath = stage == 1 ? CustomFdl1Path : CustomFdl2Path;
            uint customAddr = stage == 1 ? CustomFdl1Address : CustomFdl2Address;

            // 自定义
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
                return (File.ReadAllBytes(customPath), customAddr);

            // PAC
            if (CurrentPac != null)
            {
                var entry = stage == 1 ? _pacParser.GetFdl1(CurrentPac) : _pacParser.GetFdl2(CurrentPac);
                if (entry != null)
                {
                    byte[] data = _pacParser.ExtractFileData(CurrentPac.FilePath, entry);
                    uint addr = entry.Address != 0 ? entry.Address : customAddr;
                    return (data, addr);
                }
            }

            // 芯片数据库
            if (ChipId != 0)
            {
                var chip = Database.SprdFdlDatabase.GetChipById(ChipId);
                if (chip != null)
                {
                    uint addr = stage == 1 ? chip.Fdl1Address : chip.Fdl2Address;
                    return (null, addr);  // 只有地址没有数据
                }
            }

            return (null, customAddr);
        }

        #endregion

        #region PAC 操作

        public PacInfo LoadPac(string path)
        {
            try
            {
                CurrentPac = _pacParser.Parse(path);
                Log($"[展讯] PAC: {CurrentPac.Header.ProductName}, {CurrentPac.Files.Count} 文件", Color.Cyan);
                _pacParser.ParseXmlConfigs(CurrentPac);
                return CurrentPac;
            }
            catch (Exception ex) { Log($"[展讯] PAC 失败: {ex.Message}", Color.Red); return null; }
        }

        public async Task ExtractPacAsync(string outDir, CancellationToken ct = default)
        {
            if (CurrentPac == null) return;
            await Task.Run(() => _pacParser.ExtractAll(CurrentPac, outDir, (c, t, n) => { OnProgress?.Invoke(c, t); }), ct);
        }

        #endregion

        #region 刷机

        public async Task<bool> FlashPacAsync(List<string> selected = null, CancellationToken ct = default)
        {
            if (CurrentPac == null || !IsConnected) return false;
            return await SafeAsync("刷机", async () =>
            {
                // 初始化 FDL
                if (!await InitializeDeviceAsync()) return false;

                // 筛选分区
                var parts = CurrentPac.Files.Where(e =>
                    e.Type != PacFileType.FDL1 && e.Type != PacFileType.FDL2 &&
                    e.Type != PacFileType.XML && e.Size > 0 &&
                    (selected == null || selected.Contains(e.PartitionName))).ToList();

                int total = parts.Count, cur = 0;
                foreach (var entry in parts)
                {
                    ct.ThrowIfCancellationRequested();
                    cur++;
                    Log($"[展讯] ({cur}/{total}) {entry.PartitionName}", Color.White);

                    byte[] data = ExtractAndDecompress(entry);
                    if (data == null) return false;

                    if (!await _client.WritePartitionAsync(entry.PartitionName, data, ct))
                    { Log($"[展讯] {entry.PartitionName} 失败", Color.Red); return false; }
                }
                Log("[展讯] 刷机完成", Color.Green);
                return true;
            });
        }

        public async Task<bool> FlashPartitionAsync(string name, string path) => await FlashImageFileAsync(name, path);

        public async Task<bool> FlashImageFileAsync(string name, string path, CancellationToken ct = default)
        {
            return await RequireFdl2Async("刷写", async () =>
            {
                byte[] data = LoadAndDecompressSparse(path);
                return await _client.WritePartitionAsync(name, data, ct);
            });
        }

        public async Task<bool> FlashMultipleImagesAsync(Dictionary<string, string> files, CancellationToken ct = default)
        {
            if (!IsConnected || CurrentStage != FdlStage.FDL2) return false;
            int ok = 0, total = files.Count;
            foreach (var kv in files)
            {
                ct.ThrowIfCancellationRequested();
                if (await FlashImageFileAsync(kv.Key, kv.Value, ct)) ok++;
            }
            return ok == total;
        }

        private byte[] ExtractAndDecompress(PacFileEntry entry)
        {
            string tmp = Path.Combine(Path.GetTempPath(), entry.FileName);
            try
            {
                _pacParser.ExtractFile(CurrentPac.FilePath, entry, tmp);
                byte[] data = LoadAndDecompressSparse(tmp);
                return data;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private byte[] LoadAndDecompressSparse(string path)
        {
            if (SparseHandler.IsSparseImage(path))
            {
                string raw = path + ".raw";
                try
                {
                    new SparseHandler(msg => Log(msg, Color.Gray)).Decompress(path, raw, (c, t) => { });
                    return File.ReadAllBytes(raw);
                }
                finally { try { File.Delete(raw); } catch { } }
            }
            return File.ReadAllBytes(path);
        }

        #endregion

        #region 分区操作

        public async Task<bool> ReadPartitionAsync(string name, string outPath, uint size)
            => await RequireFdl2Async("读取", async () => { var d = await _client.ReadPartitionAsync(name, size); if (d != null) File.WriteAllBytes(outPath, d); return d != null; });

        public async Task<bool> ErasePartitionAsync(string name)
            => await RequireFdl2Async("擦除", () => _client.ErasePartitionAsync(name));

        public async Task<List<SprdPartitionInfo>> ReadPartitionTableAsync()
        {
            if (!IsConnected || CurrentStage != FdlStage.FDL2) return null;
            var p = await _client.ReadPartitionTableAsync();
            if (p?.Count > 0) CachedPartitions = p;
            return p;
        }

        public uint GetPartitionSize(string name) =>
            CachedPartitions?.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Size ?? 0;

        #endregion

        #region 设备控制

        public async Task<bool> RebootAsync() => IsConnected && await _client.ResetDeviceAsync();
        public async Task<bool> PowerOffAsync() => IsConnected && await _client.PowerOffAsync();
        public async Task<uint> ReadChipTypeAsync() => IsConnected ? await _client.ReadChipTypeAsync() : 0;
        public async Task<bool> SetBaudRateAsync(int baud) => IsConnected && await _client.SetBaudRateAsync(baud);
        public async Task<bool> EnterForceDownloadModeAsync() => IsConnected && await _client.EnterForceDownloadAsync();

        #endregion

        #region 安全/NV

        public async Task<bool> UnlockAsync(byte[] data = null) => IsConnected && await _client.UnlockAsync(data);
        public async Task<byte[]> ReadPublicKeyAsync() => IsConnected ? await _client.ReadPublicKeyAsync() : null;
        public async Task<bool> SendSignatureAsync(byte[] sig) => IsConnected && await _client.SendSignatureAsync(sig);
        public async Task<byte[]> ReadEfuseAsync(uint block = 0) => IsConnected ? await _client.ReadEfuseAsync(block) : null;
        public async Task<byte[]> ReadNvItemAsync(ushort id) => IsConnected && CurrentStage == FdlStage.FDL2 ? await _client.ReadNvItemAsync(id) : null;
        public async Task<bool> WriteNvItemAsync(ushort id, byte[] data) => IsConnected && CurrentStage == FdlStage.FDL2 && await _client.WriteNvItemAsync(id, data);
        public async Task<string> ReadImeiAsync() => IsConnected && CurrentStage == FdlStage.FDL2 ? await _client.ReadImeiAsync() : null;
        public async Task<SprdFlashInfo> ReadFlashInfoAsync() => IsConnected ? await _client.ReadFlashInfoAsync() : null;
        public async Task<SprdFlashInfo> GetFlashInfoAsync() => await ReadFlashInfoAsync();
        public async Task<bool> RepartitionAsync(byte[] data) => IsConnected && CurrentStage == FdlStage.FDL2 && await _client.RepartitionAsync(data);

        public async Task<bool> WriteImeiAsync(string imei)
        {
            if (!IsConnected || CurrentStage != FdlStage.FDL2 || string.IsNullOrEmpty(imei) || imei.Length != 15) return false;
            byte[] data = new byte[9]; data[0] = 0x08;
            for (int i = 0; i < 15; i += 2)
            { int h = imei[i] - '0', l = i + 1 < 15 ? imei[i + 1] - '0' : 0xF; data[1 + i / 2] = (byte)((l << 4) | h); }
            return await _client.WriteNvItemAsync(0, data);
        }

        #endregion

        #region 安全信息 / Bootloader

        public async Task<SprdSecurityInfo> GetSecurityInfoAsync()
        {
            if (!IsConnected) return null;
            try
            {
                var info = new SprdSecurityInfo();
                var efuse = await _client.ReadEfuseAsync(0);
                if (efuse != null)
                {
                    info.RawEfuseData = efuse;
                    if (efuse.Length >= 4) { uint f = BitConverter.ToUInt32(efuse, 0); info.IsEfuseLocked = (f & 1) != 0; info.IsAntiRollbackEnabled = (f & 2) != 0; }
                    if (efuse.Length >= 8) info.SecurityVersion = BitConverter.ToUInt32(efuse, 4);
                }
                var pk = await _client.ReadPublicKeyAsync();
                if (pk?.Length > 0) { info.PublicKeyHash = Hash(pk); info.IsSecureBootEnabled = !info.PublicKeyHash.All(c => c == '0' || c == 'F' || c == 'f'); }
                return info;
            }
            catch { return null; }
        }

        public async Task<SprdBootloaderStatus> GetBootloaderStatusAsync()
        {
            if (!IsConnected || CurrentStage != FdlStage.FDL2) return null;
            try
            {
                var s = new SprdBootloaderStatus();
                var efuse = await _client.ReadEfuseAsync();
                if (efuse?.Length >= 4) { uint f = BitConverter.ToUInt32(efuse, 0); s.IsSecureBootEnabled = (f & 1) != 0; s.IsUnlocked = (f & 0x10) != 0; s.IsUnfused = (f & 1) == 0; }
                if (efuse?.Length >= 8) s.SecurityVersion = BitConverter.ToUInt32(efuse, 4);
                var pk = await _client.ReadPublicKeyAsync();
                if (pk != null && SprdExploitDatabase.IsUnfusedDevice(Hash(pk))) s.IsUnfused = true;
                var fi = await _client.ReadFlashInfoAsync();
                if (fi != null) s.DeviceModel = fi.ChipModel ?? "Unknown";
                return s;
            }
            catch { return null; }
        }

        public async Task<bool> UnlockBootloaderAsync(bool useExploit = false)
        {
            if (!IsConnected || CurrentStage != FdlStage.FDL2) return false;
            if (useExploit)
            {
                var r = await _exploitService.TryExploitAsync(_client.GetPort(), ChipId);
                if (!r.Success) return false;
            }
            return await _client.UnlockAsync(null, false);
        }

        public async Task<bool> UnlockBootloaderWithCodeAsync(string code)
        {
            if (!IsConnected || CurrentStage != FdlStage.FDL2 || string.IsNullOrEmpty(code)) return false;
            byte[] b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = Convert.ToByte(code.Substring(i * 2, 2), 16);
            return await _client.UnlockAsync(b);
        }

        public async Task<bool> LockBootloaderAsync()
            => IsConnected && CurrentStage == FdlStage.FDL2 && await _client.UnlockAsync(null, true);

        public async Task<bool> RelockBootloaderAsync() => await LockBootloaderAsync();

        #endregion

        #region 漏洞利用

        public SprdVulnerabilityCheckResult CheckVulnerability(string pkHash = null)
            => _exploitService.CheckVulnerability(_client?.ChipId ?? ChipId, pkHash);

        public async Task<SprdExploitResult> TryExploitAsync(SerialPort port = null, string pkHash = null, CancellationToken ct = default)
        {
            var p = port ?? _client?.GetPort();
            if (p == null) return new SprdExploitResult { Success = false, Message = "无串口" };
            return await _exploitService.TryExploitAsync(p, _client?.ChipId ?? ChipId, pkHash, ct);
        }

        public async Task<SprdExploitResult> CheckAndExploitAsync(CancellationToken ct = default)
        {
            var v = CheckVulnerability();
            return v.HasVulnerability ? await TryExploitAsync(ct: ct) : new SprdExploitResult { Success = false, Message = "无漏洞" };
        }

        #endregion

        #region 高级功能

        public async Task<bool> SetActiveSlotAsync(ActiveSlot slot)
            => await RequireFdl2Async("槽位", async () => await _client.CheckPartitionExistAsync("misc") && await _client.WritePartitionAsync("misc", SlotPayloads.GetPayload(slot)));

        public async Task<bool> SetDmVerityAsync(bool enable)
        {
            return await RequireFdl2Async("DM-Verity", async () =>
            {
                if (!await _client.CheckPartitionExistAsync("vbmeta")) return false;
                var parts = await _client.ReadPartitionTableAsync();
                var vi = parts?.FirstOrDefault(p => p.Name.Equals("vbmeta", StringComparison.OrdinalIgnoreCase));
                if (vi == null) return false;
                byte[] data = await _client.ReadPartitionAsync("vbmeta", vi.Size);
                if (data == null || data.Length < DmVerityControl.VbmetaFlagOffset + 1) return false;
                data[DmVerityControl.VbmetaFlagOffset] = DmVerityControl.GetVerityData(enable)[0];
                return await _client.WritePartitionAsync("vbmeta", data);
            });
        }

        public async Task<bool> ResetToModeAsync(ResetToMode mode)
        {
            return await RequireFdl2Async("重启", async () =>
            {
                if (mode == ResetToMode.EraseFrp)
                {
                    if (await _client.CheckPartitionExistAsync("frp")) await _client.ErasePartitionAsync("frp");
                    if (await _client.CheckPartitionExistAsync("persistent")) await _client.ErasePartitionAsync("persistent");
                    return true;
                }
                if (mode != ResetToMode.Normal)
                {
                    if (await _client.CheckPartitionExistAsync("misc"))
                    { var d = MiscCommands.CreateMiscData(mode); if (d != null) await _client.WritePartitionAsync("misc", d); }
                    if (await _client.CheckPartitionExistAsync("frp")) await _client.ErasePartitionAsync("frp");
                    if (await _client.CheckPartitionExistAsync("persistent")) await _client.ErasePartitionAsync("persistent");
                }
                await _client.ResetDeviceAsync();
                return true;
            });
        }

        public async Task<bool> EraseFrpAsync() => await ResetToModeAsync(ResetToMode.EraseFrp);
        public async Task<bool> IsAbSystemAsync() => IsConnected && CurrentStage == FdlStage.FDL2 && await _client.CheckPartitionExistAsync("boot_a");
        public async Task<bool> WritePartitionSkipVerifyAsync(string name, byte[] data)
            => SkipVerifyHelper.CanUseSkipVerify(name) && await RequireFdl2Async("写入", () => _client.WritePartitionAsync(name, data));

        #endregion

        #region 校准数据

        private static readonly string[] CalibPartitions = {
            "nvitem","nv","nvram","wcnmodem","wcn","l_modem","modem",
            "l_fixnv1","l_fixnv2","l_runtimenv1","l_runtimenv2","prodnv","prodinfo","miscdata","factorydata"
        };
        public string[] GetCalibrationPartitionNames() => CalibPartitions;

        public async Task<bool> BackupCalibrationDataAsync(string outDir, CancellationToken ct = default)
        {
            return await RequireFdl2Async("备份校准", async () =>
            {
                Directory.CreateDirectory(outDir);
                var parts = await _client.ReadPartitionTableAsync();
                if (parts == null) return false;
                int n = 0;
                foreach (var p in parts.Where(p => CalibPartitions.Any(c => p.Name.ToLower().Contains(c))))
                {
                    ct.ThrowIfCancellationRequested();
                    var d = await _client.ReadPartitionAsync(p.Name, p.Size, ct);
                    if (d?.Length > 0) { File.WriteAllBytes(Path.Combine(outDir, p.Name + ".bin"), d); n++; }
                }
                return n > 0;
            });
        }

        public async Task<bool> RestoreCalibrationDataAsync(string inDir, CancellationToken ct = default)
        {
            if (!Directory.Exists(inDir)) return false;
            return await RequireFdl2Async("恢复校准", async () =>
            {
                int n = 0;
                foreach (var f in Directory.GetFiles(inDir, "*.bin"))
                {
                    ct.ThrowIfCancellationRequested();
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (CalibPartitions.Any(c => name.ToLower().Contains(c)) && await FlashImageFileAsync(name, f, ct)) n++;
                }
                return n > 0;
            });
        }

        public async Task<bool> FactoryResetAsync(bool eraseData = true, bool eraseCache = true, CancellationToken ct = default)
        {
            return await RequireFdl2Async("出厂重置", async () =>
            {
                var parts = await _client.ReadPartitionTableAsync();
                if (parts == null) return false;
                var toErase = new List<string>();
                if (eraseData) { var u = parts.Find(p => p.Name.ToLower().Contains("userdata") || p.Name.ToLower() == "data"); if (u != null) toErase.Add(u.Name); }
                if (eraseCache) { var c = parts.Find(p => p.Name.ToLower().Contains("cache")); if (c != null) toErase.Add(c.Name); }
                var m = parts.Find(p => p.Name.ToLower() == "metadata"); if (m != null) toErase.Add(m.Name);
                int n = 0;
                foreach (var name in toErase) { ct.ThrowIfCancellationRequested(); if (await _client.ErasePartitionAsync(name)) n++; }
                return n > 0;
            });
        }

        #endregion

        #region Diag

        public async Task<bool> ConnectDiagAsync(string port)
        { if (_diagClient == null) { _diagClient = new DiagClient(); _diagClient.OnLog += msg => Log(msg, Color.Gray); } return await _diagClient.ConnectAsync(port); }
        public void DisconnectDiag() => _diagClient?.Disconnect();
        public async Task<string> ReadImeiViaDiagAsync(int slot = 1) => _diagClient?.IsConnected == true ? await _diagClient.ReadImeiAsync(slot) : null;
        public async Task<bool> WriteImeiViaDiagAsync(string imei, int slot = 1) => _diagClient?.IsConnected == true && await _diagClient.WriteImeiAsync(imei, slot);
        public async Task<string> SendAtCommandViaDiagAsync(string cmd) => _diagClient?.IsConnected == true ? await _diagClient.SendAtCommandAsync(cmd) : null;
        public async Task<bool> SwitchToDownloadModeViaDiagAsync() => _diagClient?.IsConnected == true && await _diagClient.SwitchToDownloadModeAsync();

        #endregion

        #region Boot 解析 / 加解密

        private DeviceInfoExtractor _infoExtractor;

        public SprdDeviceDetails ExtractDeviceInfoFromBoot(string path)
        { try { _infoExtractor = _infoExtractor ?? new DeviceInfoExtractor(msg => Log(msg, Color.Gray)); return _infoExtractor.ExtractFromBootImage(path); } catch { return null; } }
        public SprdDeviceDetails ExtractDeviceInfoFromBoot(byte[] data)
        { try { _infoExtractor = _infoExtractor ?? new DeviceInfoExtractor(msg => Log(msg, Color.Gray)); return _infoExtractor.ExtractFromBootImage(data); } catch { return null; } }

        public async Task<SprdDeviceDetails> ExtractDeviceInfoFromPacAsync(string pacPath)
        {
            try
            {
                var pac = _pacParser.Parse(pacPath);
                var boot = pac?.Files.FirstOrDefault(f => f.FileName.ToLower().Contains("boot") && (f.FileName.EndsWith(".img") || f.FileName.EndsWith(".bin")));
                if (boot == null) return null;
                byte[] data = await Task.Run(() => _pacParser.ExtractFileData(pac.FilePath, boot));
                if (data == null) return null;
                if (SprdCryptograph.IsEncrypted(data)) data = SprdCryptograph.TryDecrypt(data);
                return ExtractDeviceInfoFromBoot(data);
            }
            catch { return null; }
        }

        public List<CpioEntry> ExtractRamdiskFiles(string bootPath)
        { try { var bp = new BootParser(msg => Log(msg, Color.Gray)); return bp.ExtractRamdisk(bp.Parse(bootPath)); } catch { return new List<CpioEntry>(); } }

        public void DecryptFirmware(string input, string output, string pw = null) { SprdCryptograph.DecryptFile(input, output, pw); }
        public void EncryptFirmware(string input, string output, string pw = null) { SprdCryptograph.EncryptFile(input, output, pw); }
        public bool IsFirmwareEncrypted(string path) => SprdCryptograph.IsEncrypted(path);

        #endregion

        #region ISP eMMC

        public List<DetectedUsbStorage> DetectIspDevices() => EmmcPartitionManager.DetectUsbStorageDevices();
        public DetectedUsbStorage DetectSprdIspDevice() => EmmcPartitionManager.DetectSprdIspDevice();
        public async Task<DetectedUsbStorage> WaitForIspDeviceAsync(int sec = 60) => await EmmcPartitionManager.WaitForDeviceAsync(sec, _cts?.Token ?? default);

        public bool OpenIspDevice(string path)
        {
            _ispManager = _ispManager ?? new EmmcPartitionManager();
            _ispManager.OnLog += msg => Log($"[ISP] {msg}", Color.Cyan);
            _ispManager.OnProgress += (c, t) => OnProgress?.Invoke((int)(c * 100 / t), 100);
            return _ispManager.Open(path);
        }

        public void CloseIspDevice() => _ispManager?.Close();
        public async Task<bool> IspReadPartitionAsync(string name, string outPath) => IsIspReady && (await _ispManager.ReadPartitionAsync(name, outPath, _cts?.Token ?? default)).Success;
        public async Task<bool> IspWritePartitionAsync(string name, string inPath) => IsIspReady && (await _ispManager.WritePartitionAsync(name, inPath, _cts?.Token ?? default)).Success;
        public bool IspErasePartition(string name) => IsIspReady && _ispManager.ErasePartition(name).Success;
        public async Task<bool> IspBackupAllPartitionsAsync(string outDir) => IsIspReady && (await _ispManager.BackupAllPartitionsAsync(outDir, _cts?.Token ?? default)).All(r => r.Success);
        public bool IspBackupGpt(string path) => IsIspReady && _ispManager.BackupGpt(path);
        public bool IspRestoreGpt(string path) => IsIspReady && _ispManager.RestoreGpt(path);
        public byte[] IspReadRawSectors(long start, int count) => IsIspReady ? _ispManager.ReadRawSectors(start, count) : null;
        public bool IspWriteRawSectors(long start, byte[] data) => IsIspReady && _ispManager.WriteRawSectors(start, data);
        public List<EmmcPartitionInfo> GetIspPartitions() => _ispManager?.Partitions ?? new List<EmmcPartitionInfo>();
        public EmmcPartitionInfo FindIspPartition(string name) => _ispManager?.Gpt?.FindPartition(name);

        #endregion

        #region 内部辅助

        private async Task<bool> SafeAsync(string op, Func<Task<bool>> action)
        {
            try { return await action(); }
            catch (Exception ex) { Log($"[展讯] {op}异常: {ex.Message}", Color.Red); return false; }
        }

        private async Task<bool> RequireFdl2Async(string op, Func<Task<bool>> action)
        {
            if (!IsConnected || CurrentStage != FdlStage.FDL2) { Log($"[展讯] {op}: 需要 FDL2", Color.Orange); return false; }
            return await SafeAsync(op, action);
        }

        private void Log(string msg, Color c) => OnLog?.Invoke(msg, c);
        private static string Hash(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""); }

        private void ResetCts()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = new CancellationTokenSource();
        }

        public void Dispose()
        {
            Disconnect();
            _portDetector?.Dispose();
            _ispManager?.Dispose();
            _diagClient?.Dispose();
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _watchdog?.Dispose();
        }

        #endregion
    }
}
