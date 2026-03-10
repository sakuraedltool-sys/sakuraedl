// ============================================================================
// SakuraEDL - Spreadtrum Diag Client | 展讯 Diag 客户端
// ============================================================================
// 重构版 — Diag 协议使用独立的 CRC-16 和帧格式（非 BSL HDLC）
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Spreadtrum.Protocol
{
    /// <summary>
    /// 展讯 Diag 协议客户端
    /// 帧格式: 0x7E [cmd+payload (escaped)] [CRC16 (escaped)] 0x7E
    /// CRC: CRC-16-CCITT (LSB-first, init=0xFFFF, poly=0x8408)
    /// 注意: 这与 BSL HDLC 帧格式不同 (无 Type/Length 头)
    /// </summary>
    public class DiagClient : IDisposable
    {
        private SerialPort _port;
        private bool _isConnected;

        // Diag 命令
        public const byte DIAG_CMD_VERSION = 0x00;
        public const byte DIAG_CMD_IMEI_READ = 0x01;
        public const byte DIAG_CMD_IMEI_WRITE = 0x02;
        public const byte DIAG_CMD_NV_READ = 0x26;
        public const byte DIAG_CMD_NV_WRITE = 0x27;
        public const byte DIAG_CMD_SPC_UNLOCK = 0x47;
        public const byte DIAG_CMD_AT_COMMAND = 0x4B;
        public const byte DIAG_CMD_EFS_READ = 0x59;
        public const byte DIAG_CMD_EFS_WRITE = 0x5A;
        public const byte DIAG_CMD_RESTART = 0x29;
        public const byte DIAG_CMD_POWER_OFF = 0x3E;

        // NV ID 常量
        public const ushort NV_IMEI1 = 0x0005;
        public const ushort NV_IMEI2 = 0x0179;
        public const ushort NV_SN = 0x0006;
        public const ushort NV_BT_ADDR = 0x0191;
        public const ushort NV_WIFI_ADDR = 0x0192;

        public event Action<string> OnLog;
        public bool IsConnected => _isConnected;
        public string PortName => _port?.PortName;

        #region 连接管理

        public async Task<bool> ConnectAsync(string portName, int baudRate = 115200)
        {
            try
            {
                Disconnect();
                _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 5000,
                    WriteTimeout = 5000,
                    ReadBufferSize = 65536,
                    WriteBufferSize = 65536
                };
                _port.Open();

                if (await HandshakeAsync())
                {
                    _isConnected = true;
                    Log("[Diag] 连接成功: {0}", portName);
                    return true;
                }
                _port.Close();
                Log("[Diag] 握手失败");
                return false;
            }
            catch (Exception ex)
            {
                Log("[Diag] 连接失败: {0}", ex.Message);
                return false;
            }
        }

        public void Disconnect()
        {
            try { if (_port?.IsOpen == true) _port.Close(); } catch { }
            _isConnected = false;
        }

        private async Task<bool> HandshakeAsync()
        {
            SendFrame(DIAG_CMD_VERSION, null);
            byte[] resp = await RecvFrameAsync(2000);
            return resp != null && resp.Length > 0;
        }

        #endregion

        #region IMEI 操作

        public async Task<string> ReadImeiAsync(int slot = 1)
        {
            if (!_isConnected) return null;
            ushort nvId = slot == 1 ? NV_IMEI1 : NV_IMEI2;
            byte[] data = await ReadNvAsync(nvId, 8);
            return data != null && data.Length >= 8 ? ParseImei(data) : null;
        }

        public async Task<bool> WriteImeiAsync(string imei, int slot = 1)
        {
            if (!_isConnected || string.IsNullOrEmpty(imei) || imei.Length != 15) return false;
            return await WriteNvAsync(slot == 1 ? NV_IMEI1 : NV_IMEI2, EncodeImei(imei));
        }

        private string ParseImei(byte[] data)
        {
            var sb = new StringBuilder();
            sb.Append((data[0] >> 4) & 0x0F);
            for (int i = 1; i < 8; i++)
            {
                sb.Append(data[i] & 0x0F);
                sb.Append((data[i] >> 4) & 0x0F);
            }
            string imei = sb.ToString().TrimEnd('F');
            return imei.Length == 15 ? imei : null;
        }

        private byte[] EncodeImei(string imei)
        {
            byte[] data = new byte[8];
            data[0] = (byte)(((imei[0] - '0') << 4) | 0x0A);
            for (int i = 1; i < 8; i++)
            {
                int idx = (i - 1) * 2 + 1;
                byte low = (byte)(imei[idx] - '0');
                byte high = (byte)(idx + 1 < imei.Length ? (imei[idx + 1] - '0') : 0x0F);
                data[i] = (byte)((high << 4) | low);
            }
            return data;
        }

        #endregion

        #region NV 操作

        public async Task<byte[]> ReadNvAsync(ushort nvId, int length)
        {
            if (!_isConnected) return null;
            byte[] payload = new byte[4];
            payload[0] = (byte)(nvId & 0xFF);
            payload[1] = (byte)(nvId >> 8);
            payload[2] = (byte)(length & 0xFF);
            payload[3] = (byte)(length >> 8);

            SendFrame(DIAG_CMD_NV_READ, payload);
            byte[] resp = await RecvFrameAsync(3000);
            if (resp == null || resp.Length < 4 || resp[0] != DIAG_CMD_NV_READ) return null;

            byte[] data = new byte[resp.Length - 3];
            Array.Copy(resp, 3, data, 0, data.Length);
            return data;
        }

        public async Task<bool> WriteNvAsync(ushort nvId, byte[] data)
        {
            if (!_isConnected || data == null) return false;
            byte[] payload = new byte[2 + data.Length];
            payload[0] = (byte)(nvId & 0xFF);
            payload[1] = (byte)(nvId >> 8);
            Array.Copy(data, 0, payload, 2, data.Length);

            SendFrame(DIAG_CMD_NV_WRITE, payload);
            byte[] resp = await RecvFrameAsync(3000);
            return resp != null && resp.Length >= 1 && resp[0] == DIAG_CMD_NV_WRITE;
        }

        #endregion

        #region AT 命令

        public async Task<string> SendAtCommandAsync(string command, int timeout = 5000)
        {
            if (!_isConnected) return null;
            if (!command.EndsWith("\r")) command += "\r";
            SendFrame(DIAG_CMD_AT_COMMAND, Encoding.ASCII.GetBytes(command));
            byte[] resp = await RecvFrameAsync(timeout);
            return resp != null && resp.Length >= 2 ? Encoding.ASCII.GetString(resp, 1, resp.Length - 1).Trim() : null;
        }

        #endregion

        #region 设备控制

        public async Task<bool> RestartAsync()
        {
            if (!_isConnected) return false;
            SendFrame(DIAG_CMD_RESTART, null);
            await Task.Delay(500);
            return true;
        }

        public async Task<bool> PowerOffAsync()
        {
            if (!_isConnected) return false;
            SendFrame(DIAG_CMD_POWER_OFF, null);
            await Task.Delay(500);
            return true;
        }

        public async Task<bool> SwitchToDownloadModeAsync()
        {
            if (!_isConnected) return false;
            byte[] switchCmd = { 0x7E, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0xFE, 0x81, 0x7E };
            _port.Write(switchCmd, 0, switchCmd.Length);
            Log("[Diag] 切换到下载模式");
            _isConnected = false;
            await Task.CompletedTask;
            return true;
        }

        #endregion

        #region 帧编解码

        /// <summary>
        /// 编码并发送 Diag 帧
        /// 格式: 0x7E [cmd+payload (escaped)] [CRC16-LE (escaped)] 0x7E
        /// </summary>
        private void SendFrame(byte cmd, byte[] payload)
        {
            var raw = new List<byte> { cmd };
            if (payload != null) raw.AddRange(payload);

            ushort crc = CalcCrc16(raw.ToArray());

            var frame = new List<byte> { 0x7E };
            foreach (byte b in raw) Escape(frame, b);
            Escape(frame, (byte)(crc & 0xFF));
            Escape(frame, (byte)(crc >> 8));
            frame.Add(0x7E);

            _port.Write(frame.ToArray(), 0, frame.Count);
        }

        /// <summary>
        /// 接收并解码 Diag 帧
        /// </summary>
        private async Task<byte[]> RecvFrameAsync(int timeout)
        {
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    return await Task.Run(() =>
                    {
                        var buf = new MemoryStream();
                        bool inFrame = false;

                        while (!cts.Token.IsCancellationRequested)
                        {
                            if (_port == null || !_port.IsOpen) return null;
                            if (_port.BytesToRead > 0)
                            {
                                byte b = (byte)_port.ReadByte();
                                if (b == 0x7E)
                                {
                                    if (inFrame && buf.Length > 0)
                                    {
                                        buf.WriteByte(b);
                                        return DeescapeFrame(buf.ToArray());
                                    }
                                    inFrame = true;
                                    buf.SetLength(0);
                                    buf.WriteByte(b);
                                }
                                else if (inFrame)
                                {
                                    buf.WriteByte(b);
                                }
                            }
                            else
                            {
                                Thread.Sleep(5);
                            }
                        }
                        return null;
                    }, cts.Token);
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// 反转义帧数据，去除 CRC
        /// </summary>
        private byte[] DeescapeFrame(byte[] frame)
        {
            if (frame == null || frame.Length < 4) return null;
            var data = new List<byte>();
            bool escaped = false;
            for (int i = 1; i < frame.Length - 1; i++)
            {
                if (escaped) { data.Add((byte)(frame[i] ^ 0x20)); escaped = false; }
                else if (frame[i] == 0x7D) escaped = true;
                else data.Add(frame[i]);
            }
            if (data.Count < 3) return null;
            byte[] result = new byte[data.Count - 2];
            for (int i = 0; i < result.Length; i++) result[i] = data[i];
            return result;
        }

        private static void Escape(List<byte> frame, byte b)
        {
            if (b == 0x7E || b == 0x7D) { frame.Add(0x7D); frame.Add((byte)(b ^ 0x20)); }
            else frame.Add(b);
        }

        /// <summary>
        /// CRC-16-CCITT (LSB-first, init=0xFFFF, poly=0x8408)
        /// 注意: 这与 BSL HDLC 的 CRC-16 (MSB-first, init=0, poly=0x1021) 不同
        /// </summary>
        private static ushort CalcCrc16(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
            }
            return (ushort)(crc ^ 0xFFFF);
        }

        #endregion

        private void Log(string fmt, params object[] args) { OnLog?.Invoke(string.Format(fmt, args)); }
        public void Dispose() { Disconnect(); _port?.Dispose(); }
    }
}
