// ============================================================================
// SakuraEDL - Spreadtrum FDL Client | 展讯 FDL 客户端
// ============================================================================
// 重构版 - 基于 SpdIO 统一传输层，参考 spd_dump.c 架构
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Spreadtrum.Protocol
{
    public class FdlClient : IDisposable
    {
        private readonly SpdIO _io;
        private FdlStage _stage = FdlStage.None;
        private SprdDeviceState _state = SprdDeviceState.Disconnected;
        private volatile bool _disposed = false;

        public int DataChunkSize { get; set; } = 528;
        public const int BROM_CHUNK_SIZE = 528;
        public const int FDL_CHUNK_SIZE = 528;
        public int CommandRetries { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 500;

        public event Action<string> OnLog;
        public event Action<int, int> OnProgress;
        public event Action<SprdDeviceState> OnStateChanged;

        public bool IsConnected => _io.IsOpen;
        public FdlStage CurrentStage => _stage;
        public SprdDeviceState State => _state;
        public string PortName => _io.PortName;
        public bool IsBromMode { get; private set; } = true;
        public uint ChipId { get; private set; }
        public SerialPort GetPort() => _io.Port;

        public string CustomFdl1Path { get; private set; }
        public string CustomFdl2Path { get; private set; }
        public uint CustomFdl1Address { get; private set; }
        public uint CustomFdl2Address { get; private set; }
        public uint CustomExecAddress { get; private set; }
        public bool UseExecNoVerify { get; set; } = true;
        public string CustomExecNoVerifyPath { get; set; }

        public FdlClient()
        {
            _io = new SpdIO(msg => OnLog?.Invoke(msg));
        }

        #region 配置
        public void SetChipId(uint chipId)
        {
            ChipId = chipId;
            if (chipId > 0)
            {
                uint execAddr = SprdPlatform.GetExecAddress(chipId);
                if (CustomExecAddress == 0 && execAddr > 0) CustomExecAddress = execAddr;
                Log("[FDL] 芯片: {0}, FDL1=0x{1:X8}, FDL2=0x{2:X8}",
                    SprdPlatform.GetPlatformName(chipId),
                    SprdPlatform.GetFdl1Address(chipId), SprdPlatform.GetFdl2Address(chipId));
            }
        }
        public void SetCustomFdl1(string path, uint addr) { CustomFdl1Path = path; CustomFdl1Address = addr; }
        public void SetCustomFdl2(string path, uint addr) { CustomFdl2Path = path; CustomFdl2Address = addr; }
        public void ClearCustomFdl() { CustomFdl1Path = CustomFdl2Path = null; CustomFdl1Address = CustomFdl2Address = 0; }
        public void SetCustomExecAddress(uint addr) { CustomExecAddress = addr; }
        public void SetExecNoVerifyFile(string path) { CustomExecNoVerifyPath = path; }
        public uint GetFdl1Address() => CustomFdl1Address > 0 ? CustomFdl1Address : SprdPlatform.GetFdl1Address(ChipId);
        public uint GetFdl2Address() => CustomFdl2Address > 0 ? CustomFdl2Address : SprdPlatform.GetFdl2Address(ChipId);
        public string GetFdl1Path(string def) => !string.IsNullOrEmpty(CustomFdl1Path) && File.Exists(CustomFdl1Path) ? CustomFdl1Path : def;
        public string GetFdl2Path(string def) => !string.IsNullOrEmpty(CustomFdl2Path) && File.Exists(CustomFdl2Path) ? CustomFdl2Path : def;
        #endregion

        #region 连接管理
        public async Task<bool> ConnectAsync(string portName, int baudRate = 115200)
        {
            try
            {
                Log("[FDL] 连接: {0} @ {1}", portName, baudRate);
                _io.Open(portName, baudRate);
                bool ok = await HandshakeAsync();
                if (ok) { SetState(SprdDeviceState.Connected); Log("[FDL] 连接成功"); }
                return ok;
            }
            catch (Exception ex) { Log("[FDL] 连接失败: {0}", ex.Message); SetState(SprdDeviceState.Error); return false; }
        }

        public void Disconnect()
        {
            _io.Close();
            _stage = FdlStage.None;
            SetState(SprdDeviceState.Disconnected);
        }

        private async Task<bool> HandshakeAsync()
        {
            _io.Hdlc.SetBromMode();

            // 方法1: 单个 0x7E
            _io.TrySendRaw(new byte[] { 0x7E });
            await Task.Delay(100);
            int r = await _io.RecvMessageAsync(2000);
            if (r == (int)BslCommand.BSL_REP_VER) { LogVer(_io.LastFrame); IsBromMode = true; return true; }
            if (r == (int)BslCommand.BSL_REP_ACK) { IsBromMode = false; return true; }

            // 方法2: 多个 0x7E
            _io.Flush();
            for (int i = 0; i < 3; i++) { _io.TrySendRaw(new byte[] { 0x7E }); await Task.Delay(50); }
            await Task.Delay(100);
            r = await _io.RecvMessageAsync(2000);
            if (r == (int)BslCommand.BSL_REP_VER) { LogVer(_io.LastFrame); IsBromMode = true; return true; }
            if (r == (int)BslCommand.BSL_REP_ACK) { IsBromMode = false; return true; }

            // 方法3: CONNECT
            _io.Flush();
            var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_CONNECT, null, 3000);
            if (f != null)
            {
                if (f.Type == (int)BslCommand.BSL_REP_ACK) { IsBromMode = _stage == FdlStage.None; return true; }
                if (f.Type == (int)BslCommand.BSL_REP_VER) { LogVer(f); IsBromMode = true; return true; }
            }
            Log("[FDL] 握手失败"); return false;
        }

        private void LogVer(HdlcFrame f)
        {
            if (f?.Payload != null) Log("[FDL] 版本: {0}", Encoding.ASCII.GetString(f.Payload).TrimEnd('\0'));
        }
        #endregion

        #region FDL 下载
        public async Task<bool> DownloadFdlAsync(byte[] fdlData, uint baseAddr, FdlStage stage)
        {
            if (!IsConnected) { Log("[FDL] 未连接"); return false; }
            Log("[FDL] 下载 {0}: 0x{1:X8}, {2} bytes", stage, baseAddr, fdlData.Length);
            try
            {
                if (stage == FdlStage.FDL1) { _io.Hdlc.SetBromMode(); DataChunkSize = BROM_CHUNK_SIZE; }

                // BROM: 先 CONNECT
                if (IsBromMode || stage == FdlStage.FDL1)
                {
                    var cr = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_CONNECT, null, 3000);
                    if (cr?.Type == (int)BslCommand.BSL_REP_VER)
                        await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_CONNECT, null, 3000);
                }

                // 发送文件
                if (!await _io.SendFileAsync(fdlData, baseAddr, DataChunkSize, (c, t) => OnProgress?.Invoke(c, t)))
                { Log("[FDL] 文件传输失败"); return false; }

                // exec_no_verify (仅 FDL1)
                if (stage == FdlStage.FDL1 && UseExecNoVerify && CustomExecAddress > 0)
                    await SendExecNoVerifyAsync(CustomExecAddress);

                // EXEC_DATA
                _io.TrySendMessage((int)BslCommand.BSL_CMD_EXEC_DATA);
                int er = await _io.RecvMessageAsync(stage == FdlStage.FDL2 ? 15000 : 5000);

                return stage == FdlStage.FDL1 ? await TransitionFdl1Async() : await TransitionFdl2Async(er);
            }
            catch (Exception ex) { Log("[FDL] 异常: {0}", ex.Message); return false; }
        }

        private async Task<bool> TransitionFdl1Async()
        {
            _io.Hdlc.SetFdlMode();
            IsBromMode = false;
            byte[] cb = { 0x7E, 0x7E, 0x7E, 0x7E };
            for (int i = 0; i < 10; i++)
            {
                if (i > 0) { Log("[FDL] CHECK_BAUD {0}/10", i + 1); await Task.Delay(200); }
                _io.TrySendRaw(cb);
                int r = await _io.RecvMessageAsync(2000);
                if (r == (int)BslCommand.BSL_REP_VER || r == (int)BslCommand.BSL_REP_ACK)
                {
                    if (r == (int)BslCommand.BSL_REP_VER && _io.LastFrame?.Payload != null)
                        Log("[FDL] FDL1: {0}", Encoding.ASCII.GetString(_io.LastFrame.Payload).TrimEnd('\0'));
                    await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_CONNECT, null, 2000);
                    _stage = FdlStage.FDL1; SetState(SprdDeviceState.Fdl1Loaded);
                    Log("[FDL] FDL1 成功"); return true;
                }
            }
            Log("[FDL] FDL1 无响应"); return false;
        }

        private async Task<bool> TransitionFdl2Async(int execResp)
        {
            if (execResp == (int)BslCommand.BSL_REP_ACK || execResp == (int)BslCommand.BSL_REP_INCOMPATIBLE_PARTITION)
            { await DisableTranscodeAsync(); _stage = FdlStage.FDL2; SetState(SprdDeviceState.Fdl2Loaded); Log("[FDL] FDL2 成功"); return true; }

            await Task.Delay(500);
            int r = await _io.RecvMessageAsync(2000);
            if (r == (int)BslCommand.BSL_REP_ACK || r == (int)BslCommand.BSL_REP_INCOMPATIBLE_PARTITION)
            { await DisableTranscodeAsync(); _stage = FdlStage.FDL2; SetState(SprdDeviceState.Fdl2Loaded); Log("[FDL] FDL2 成功"); return true; }

            Log("[FDL] FDL2 失败"); return false;
        }

        public async Task<bool> DownloadFdlFromFileAsync(string path, uint addr, FdlStage stage)
        {
            if (!File.Exists(path)) return false;
            return await DownloadFdlAsync(File.ReadAllBytes(path), addr, stage);
        }
        #endregion

        #region exec_no_verify
        private async Task<bool> SendExecNoVerifyAsync(uint execAddr)
        {
            byte[] data = LoadExecNoVerify(execAddr);
            if (data == null) return true;
            Log("[FDL] 发送 exec_no_verify: 0x{0:X8} ({1} bytes)", execAddr, data.Length);
            _io.Hdlc.SetBromMode();

            byte[] sp = new byte[8]; SpdIO.WriteBE32(sp, 0, execAddr); SpdIO.WriteBE32(sp, 4, (uint)data.Length);
            if (!await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_START_DATA, sp, 5000)) return false;

            for (int off = 0; off < data.Length; off += 512)
            {
                int len = Math.Min(512, data.Length - off);
                byte[] chunk = new byte[len]; Array.Copy(data, off, chunk, 0, len);
                if (!await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_MIDST_DATA, chunk, 5000)) return false;
            }
            _io.TrySendMessage((int)BslCommand.BSL_CMD_END_DATA);
            await _io.RecvMessageAsync(5000);
            return true;
        }

        private byte[] LoadExecNoVerify(uint addr)
        {
            if (!string.IsNullOrEmpty(CustomExecNoVerifyPath) && File.Exists(CustomExecNoVerifyPath))
                return File.ReadAllBytes(CustomExecNoVerifyPath);
            var names = new[] { $"custom_exec_no_verify_{addr:x}.bin", $"custom_exec_no_verify_{addr:X8}.bin", "exec_no_verify.bin" };
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(CustomFdl1Path)) { var d = Path.GetDirectoryName(CustomFdl1Path); if (!string.IsNullOrEmpty(d)) dirs.Add(d); }
            var appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(appDir)) dirs.Add(appDir);
            foreach (var dir in dirs) foreach (var n in names) { var p = Path.Combine(dir, n); if (File.Exists(p)) return File.ReadAllBytes(p); }
            return null;
        }
        #endregion

        #region 分区操作
        public async Task<bool> WritePartitionAsync(string name, byte[] data, CancellationToken ct = default)
        {
            if (_stage != FdlStage.FDL2) { Log("[FDL] 需要 FDL2"); return false; }
            Log("[FDL] 写入: {0}, {1}", name, FormatSize((uint)data.Length));
            try
            {
                byte[] sp = _io.BuildPartitionPayload(name, (ulong)data.Length);
                if (!await RetryCmd((int)BslCommand.BSL_CMD_START_DATA, sp)) { Log("[FDL] START 失败"); return false; }

                int cs = Math.Min(DataChunkSize, 2048);
                int total = (data.Length + cs - 1) / cs;
                for (int i = 0; i < total; i++)
                {
                    if (ct.IsCancellationRequested) return false;
                    int off = i * cs, len = Math.Min(cs, data.Length - off);
                    byte[] chunk = new byte[len]; Array.Copy(data, off, chunk, 0, len);
                    if (!await RetryData((int)BslCommand.BSL_CMD_MIDST_DATA, chunk))
                    { Log("[FDL] 块 {0}/{1} 失败", i + 1, total); return false; }
                    OnProgress?.Invoke(i + 1, total);
                }
                if (!await RetryCmd((int)BslCommand.BSL_CMD_END_DATA, null)) { Log("[FDL] END 失败"); return false; }
                Log("[FDL] {0} 写入成功", name); return true;
            }
            catch (Exception ex) { Log("[FDL] 写入异常: {0}", ex.Message); return false; }
        }

        public async Task<byte[]> ReadPartitionAsync(string name, uint size, CancellationToken ct = default)
        {
            if (_stage != FdlStage.FDL2) return null;
            Log("[FDL] 读取: {0}, {1}", name, FormatSize(size));
            try
            {
                byte[] sp = _io.BuildPartitionPayload(name, size);
                if (!await RetryCmd((int)BslCommand.BSL_CMD_READ_START, sp)) return null;

                using (var ms = new MemoryStream())
                {
                    ulong off = 0;
                    while (off < size && !ct.IsCancellationRequested)
                    {
                        uint n = (uint)Math.Min((ulong)DataChunkSize, size - off);
                        byte[] mp = new byte[8]; SpdIO.WriteLE32(mp, 0, n); SpdIO.WriteLE32(mp, 4, (uint)off);
                        _io.TrySendMessage((int)BslCommand.BSL_CMD_READ_MIDST, mp);
                        int r = await _io.RecvMessageAsync(30000);
                        if (r == (int)BslCommand.BSL_REP_READ_FLASH && _io.LastFrame?.Payload != null)
                        { ms.Write(_io.LastFrame.Payload, 0, _io.LastFrame.Payload.Length); off += (uint)_io.LastFrame.Payload.Length; OnProgress?.Invoke((int)off, (int)size); }
                        else break;
                    }
                    _io.TrySendMessage((int)BslCommand.BSL_CMD_READ_END); await _io.RecvMessageAsync(1000);
                    return ms.ToArray();
                }
            }
            catch (Exception ex) { Log("[FDL] 读取异常: {0}", ex.Message); return null; }
        }

        public async Task<bool> ErasePartitionAsync(string name)
        {
            if (_stage != FdlStage.FDL2) return false;
            byte[] p = _io.BuildPartitionPayload(name, 0xFFFFFFFF);
            bool ok = await RetryCmd((int)BslCommand.BSL_CMD_ERASE_FLASH, p);
            Log("[FDL] 擦除 {0}: {1}", name, ok ? "OK" : "失败"); return ok;
        }

        public async Task<bool> CheckPartitionExistAsync(string name)
        {
            try
            {
                byte[] p = _io.BuildPartitionPayload(name, 8);
                _io.TrySendMessage((int)BslCommand.BSL_CMD_READ_START, p);
                int r = await _io.RecvMessageAsync(2000);
                _io.TrySendMessage((int)BslCommand.BSL_CMD_READ_END);
                await _io.RecvMessageAsync(500);
                return r == (int)BslCommand.BSL_REP_ACK;
            }
            catch
            {
                try { _io.TrySendMessage((int)BslCommand.BSL_CMD_READ_END); await _io.RecvMessageAsync(500); } catch { }
                return false;
            }
        }
        #endregion

        #region FDL2 初始化
        public async Task<bool> DisableTranscodeAsync()
        {
            await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_DISABLE_TRANSCODE, null, 5000);
            _io.Hdlc.DisableTranscode();
            return true;
        }
        #endregion

        #region 设备信息
        public async Task<string> ReadVersionAsync()
        {
            try { var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_READ_VERSION, null, 5000);
                if (f?.Type == (int)BslCommand.BSL_REP_VER && f.Payload != null) { var v = Encoding.UTF8.GetString(f.Payload).TrimEnd('\0'); Log("[FDL] 版本: {0}", v); return v; } return null; }
            catch { return null; }
        }

        public async Task<uint> ReadChipTypeAsync()
        {
            try { var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_READ_CHIP_TYPE, null, 5000);
                if (f?.Payload != null && f.Payload.Length >= 4) { uint id = BitConverter.ToUInt32(f.Payload, 0); Log("[FDL] 芯片: 0x{0:X8}", id); return id; } return 0; }
            catch { return 0; }
        }

        private static readonly string[] CommonPartitions = {
            "splloader","prodnv","miscdata","recovery","misc","trustos","trustos_bak",
            "sml","sml_bak","uboot","uboot_bak","logo","fbootlogo",
            "l_fixnv1","l_fixnv2","l_runtimenv1","l_runtimenv2",
            "gpsgl","gpsbd","wcnmodem","persist","l_modem",
            "l_deltanv","l_gdsp","l_ldsp","pm_sys","boot",
            "system","cache","vendor","uboot_log","userdata","dtb","socko","vbmeta",
            "super","metadata","user_partition"
        };

        public async Task<List<SprdPartitionInfo>> ReadPartitionTableAsync()
        {
            try
            {
                var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_READ_PARTITION, null, 10000);
                if (f?.Type == (int)BslCommand.BSL_REP_PARTITION && f.Payload != null) return ParsePartTable(f.Payload);
                if (f?.Type == (int)BslCommand.BSL_REP_UNSUPPORTED_COMMAND) return await TraversePartitionsAsync();
                return await TraversePartitionsAsync();
            }
            catch { return null; }
        }

        private async Task<List<SprdPartitionInfo>> TraversePartitionsAsync()
        {
            var list = new List<SprdPartitionInfo>();
            string[] pri = { "boot","system","userdata","cache","recovery","misc" };
            int fails = 0;
            foreach (var n in pri.Concat(CommonPartitions).Distinct())
            {
                if (fails >= 5) break;
                try
                {
                    byte[] p = _io.BuildPartitionPayload(n, 8);
                    _io.TrySendMessage((int)BslCommand.BSL_CMD_READ_START, p);
                    int r = await _io.RecvMessageAsync(2000);
                    _io.TrySendMessage((int)BslCommand.BSL_CMD_READ_END); await _io.RecvMessageAsync(500);
                    if (r == (int)BslCommand.BSL_REP_ACK) { list.Add(new SprdPartitionInfo { Name = n }); fails = 0; }
                    else if (r < 0) fails++;
                    else fails = 0;
                }
                catch { fails++; }
            }
            return list.Count > 0 ? list : null;
        }

        private List<SprdPartitionInfo> ParsePartTable(byte[] data)
        {
            var list = new List<SprdPartitionInfo>();
            const int ES = 76;
            for (int i = 0; i < data.Length / ES; i++)
            {
                int o = i * ES;
                string name = Encoding.Unicode.GetString(data, o, 72).TrimEnd('\0');
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new SprdPartitionInfo { Name = name, Size = BitConverter.ToUInt32(data, o + 72) });
            }
            return list;
        }
        #endregion

        #region 安全/NV/Flash
        public async Task<bool> UnlockAsync(byte[] data = null, bool relock = false)
        {
            byte[] p; if (data != null && data.Length > 0) { p = new byte[1 + data.Length]; p[0] = relock ? (byte)0 : (byte)1; Array.Copy(data, 0, p, 1, data.Length); }
            else p = new byte[] { relock ? (byte)0 : (byte)1 };
            return await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_UNLOCK, p, 10000);
        }
        public async Task<byte[]> ReadPublicKeyAsync()
        { try { var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_READ_PUBKEY, null, 5000); return f?.Payload; } catch { return null; } }
        public async Task<bool> SendSignatureAsync(byte[] sig)
        { if (sig == null) return false; return await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_SEND_SIGNATURE, sig, 10000); }
        public async Task<byte[]> ReadEfuseAsync(uint blockId = 0)
        { try { var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_READ_EFUSE, BitConverter.GetBytes(blockId), 5000); return f?.Payload; } catch { return null; } }

        public async Task<byte[]> ReadNvItemAsync(ushort itemId)
        { try { var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_READ_NVITEM, BitConverter.GetBytes(itemId), 5000);
            return f?.Type == (int)BslCommand.BSL_REP_DATA ? f.Payload : null; } catch { return null; } }
        public async Task<bool> WriteNvItemAsync(ushort itemId, byte[] data)
        { if (data == null) return false; var p = new byte[2 + data.Length]; BitConverter.GetBytes(itemId).CopyTo(p, 0); Array.Copy(data, 0, p, 2, data.Length);
          return await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_WRITE_NVITEM, p, 5000); }
        public async Task<string> ReadImeiAsync()
        { var d = await ReadNvItemAsync(0); if (d != null && d.Length >= 8) { var sb = new StringBuilder(); for (int i = 0; i < 8; i++) sb.AppendFormat("{0:X2}", d[i]);
          return sb.ToString().TrimStart('0').Substring(0, 15); } return null; }

        public async Task<SprdFlashInfo> ReadFlashInfoAsync()
        {
            try { var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_READ_FLASH_INFO, null, 5000);
                if (f?.Type == (int)BslCommand.BSL_REP_FLASH_INFO && f.Payload != null && f.Payload.Length >= 16)
                    return new SprdFlashInfo { FlashType = f.Payload[0], ManufacturerId = f.Payload[1],
                        DeviceId = BitConverter.ToUInt16(f.Payload, 2), BlockSize = BitConverter.ToUInt32(f.Payload, 4),
                        BlockCount = BitConverter.ToUInt32(f.Payload, 8), TotalSize = BitConverter.ToUInt32(f.Payload, 12) };
                return null; } catch { return null; }
        }

        public async Task<byte[]> ReadFlashAsync(uint flashId, uint offset, uint size, CancellationToken ct = default)
        {
            if (_stage != FdlStage.FDL2) return null;
            if (size == 0xFFFFFFFF) { var ds = await ReadDhtbSizeAsync(flashId); size = ds > 0 ? ds : 4 * 1024 * 1024; }
            uint step = 4096;
            using (var ms = new MemoryStream())
            {
                for (uint cur = offset; cur < offset + size && !ct.IsCancellationRequested; )
                {
                    uint n = Math.Min(step, offset + size - cur);
                    byte[] p = new byte[12]; SpdIO.WriteBE32(p, 0, flashId); SpdIO.WriteBE32(p, 4, n); SpdIO.WriteBE32(p, 8, cur);
                    _io.TrySendMessage((int)BslCommand.BSL_CMD_READ_FLASH, p);
                    int r = await _io.RecvMessageAsync(30000);
                    if (r == (int)BslCommand.BSL_REP_READ_FLASH && _io.LastFrame?.Payload != null)
                    { ms.Write(_io.LastFrame.Payload, 0, _io.LastFrame.Payload.Length); uint nr = (uint)_io.LastFrame.Payload.Length; cur += nr; if (nr < n) break; }
                    else break;
                }
                return ms.ToArray();
            }
        }

        private async Task<uint> ReadDhtbSizeAsync(uint flashId)
        {
            try { byte[] p = new byte[12]; SpdIO.WriteBE32(p, 0, flashId); SpdIO.WriteBE32(p, 4, 0x34); SpdIO.WriteBE32(p, 8, 0);
                var f = await _io.SendAndRecvAsync((int)BslCommand.BSL_CMD_READ_FLASH, p, 5000);
                if (f?.Payload != null && f.Payload.Length >= 0x34 && BitConverter.ToUInt32(f.Payload, 0) == 0x42544844 && BitConverter.ToUInt32(f.Payload, 4) == 1)
                    return BitConverter.ToUInt32(f.Payload, 0x30) + 0x200;
                return 0; } catch { return 0; }
        }

        public static uint ParseSpecialPartitionName(string name)
        { switch (name.ToLower()) { case "boot0": return 0x80000001; case "boot1": return 0x80000002;
            case "kernel": case "nor": return 0x80000003; case "user": case "user_partition": return 0x80000004; default: return 0; } }
        public static bool IsSpecialPartition(string name)
        { var l = name.ToLower(); return l == "boot0" || l == "boot1" || l == "kernel" || l == "nor" || l == "user" || l == "user_partition" || l == "splloader" || l == "spl_loader_bak" || l == "uboot"; }

        public async Task<string> GetPartitionListXmlAsync()
        { var ps = await ReadPartitionTableAsync(); if (ps == null) return null; var sb = new StringBuilder();
          sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"); sb.AppendLine("<partitions>");
          foreach (var p in ps) sb.AppendLine($"  <partition name=\"{p.Name}\" size=\"0x{p.Size:X}\" />");
          sb.AppendLine("</partitions>"); return sb.ToString(); }
        #endregion

        #region 设备控制
        public async Task<bool> RepartitionAsync(byte[] data)
        { if (_stage != FdlStage.FDL2 || data == null) return false; return await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_REPARTITION, data, 30000); }
        public async Task<bool> SetBaudRateAsync(int baud)
        { if (await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_SET_BAUD, BitConverter.GetBytes((uint)baud), 2000)) { await Task.Delay(100); _io.SetBaudRate(baud); _io.Flush(); return true; } return false; }
        public async Task<bool> CheckBaudRateAsync()
        { return await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_CHECK_BAUD, null, 2000); }
        public async Task<bool> EnterForceDownloadAsync()
        { _io.TrySendRaw(new byte[] { 0x7E, 0x7E, 0x7E, 0x7E }); await Task.Delay(100);
          if (await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_CONNECT, new byte[] { 0, 0, 0, 1 }, 5000)) return true;
          _io.TrySendMessage((int)BslCommand.BSL_CMD_RESET); await Task.Delay(1000);
          for (int i = 0; i < 10; i++) { _io.TrySendRaw(new byte[] { 0x7E, 0x7E, 0x7E, 0x7E }); if (await _io.RecvMessageAsync(500) >= 0) return true; } return false; }
        public async Task<bool> ResetDeviceAsync()
        { bool ok = await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_RESET, null, 2000); if (ok) { _stage = FdlStage.None; SetState(SprdDeviceState.Disconnected); } return ok; }
        public async Task<bool> PowerOffAsync()
        { bool ok = await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_POWER_OFF, null, 2000); if (ok) { _stage = FdlStage.None; SetState(SprdDeviceState.Disconnected); } return ok; }
        public async Task<bool> KeepChargeAsync(bool en = true)
        { return await _io.SendAndCheckAsync((int)BslCommand.BSL_CMD_KEEP_CHARGE, BitConverter.GetBytes(en ? 1 : 0), 2000); }
        #endregion

        #region 内部
        private async Task<bool> RetryCmd(int type, byte[] payload, int retries = -1)
        { if (retries < 0) retries = CommandRetries; for (int i = 0; i <= retries; i++) { if (i > 0) { await Task.Delay(RetryDelayMs); _io.Flush(); }
          if (await _io.SendAndCheckAsync(type, payload)) return true; } return false; }
        private async Task<bool> RetryData(int type, byte[] data, int retries = 2)
        { for (int i = 0; i <= retries; i++) { if (i > 0) await Task.Delay(RetryDelayMs / 2);
          if (await _io.SendAndCheckAsync(type, data)) return true; } return false; }

        private void SetState(SprdDeviceState s) { if (_state != s) { _state = s; OnStateChanged?.Invoke(s); } }
        private void Log(string fmt, params object[] args) { OnLog?.Invoke(string.Format(fmt, args)); }
        private static string FormatSize(uint s)
        { if (s >= 1024*1024*1024) return string.Format("{0:F2} GB", s/(1024.0*1024*1024));
          if (s >= 1024*1024) return string.Format("{0:F2} MB", s/(1024.0*1024));
          if (s >= 1024) return string.Format("{0:F2} KB", s/1024.0); return string.Format("{0} B", s); }

        public void Dispose() { if (!_disposed) { _disposed = true; _io.Dispose(); _stage = FdlStage.None; SetState(SprdDeviceState.Disconnected); } }
        ~FdlClient() { Dispose(); }
        #endregion
    }

    public class SprdPartitionInfo
    {
        public string Name { get; set; }
        public uint Offset { get; set; }
        public uint Size { get; set; }
        public override string ToString() => string.Format("{0} (0x{1:X8}, {2} bytes)", Name, Offset, Size);
    }

    public class SprdFlashInfo
    {
        public byte FlashType { get; set; }
        public byte ManufacturerId { get; set; }
        public ushort DeviceId { get; set; }
        public uint BlockSize { get; set; }
        public uint BlockCount { get; set; }
        public uint TotalSize { get; set; }
        public string ChipModel { get; set; }
        public string FlashTypeName { get { switch (FlashType) { case 1: return "NAND"; case 2: return "NOR"; case 3: return "eMMC"; case 4: return "UFS"; default: return "Unknown"; } } }
        public string ManufacturerName { get { switch (ManufacturerId) { case 0x15: return "Samsung"; case 0x45: return "SanDisk"; case 0x90: return "Hynix"; case 0xFE: return "Micron"; default: return string.Format("0x{0:X2}", ManufacturerId); } } }
        public override string ToString() => string.Format("{0} {1} {2}", FlashTypeName, ManufacturerName, TotalSize >= 1024*1024*1024 ? string.Format("{0:F1} GB", TotalSize/(1024.0*1024*1024)) : string.Format("{0} MB", TotalSize/(1024*1024)));
    }

    public class SprdSecurityInfo
    {
        public bool IsLocked { get; set; }
        public bool RequiresSignature { get; set; }
        public byte[] PublicKey { get; set; }
        public uint SecurityVersion { get; set; }
        public bool IsSecureBootEnabled { get; set; }
        public bool IsEfuseLocked { get; set; }
        public bool IsAntiRollbackEnabled { get; set; }
        public string PublicKeyHash { get; set; }
        public byte[] RawEfuseData { get; set; }
    }

    public static class SprdNvItems
    {
        public const ushort NV_IMEI = 0;
        public const ushort NV_IMEI2 = 1;
        public const ushort NV_BT_ADDR = 2;
        public const ushort NV_WIFI_ADDR = 3;
        public const ushort NV_SERIAL_NUMBER = 4;
        public const ushort NV_CALIBRATION = 100;
        public const ushort NV_RF_CALIBRATION = 101;
        public const ushort NV_GPS_CONFIG = 200;
        public const ushort NV_AUDIO_PARAM = 300;
    }
}
