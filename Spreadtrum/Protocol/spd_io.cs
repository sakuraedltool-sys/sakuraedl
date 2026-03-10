// ============================================================================
// SakuraEDL - SPD I/O Layer | 展讯通信层
// ============================================================================
// 统一串口传输 + HDLC 帧编解码 (参考 spd_dump.c spdio_t)
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Spreadtrum.Protocol
{
    /// <summary>
    /// SPD I/O 层 — 串口传输 + HDLC 帧编解码
    /// 参考 spd_dump.c 的 spdio_t 设计，统一所有串口读写操作
    /// </summary>
    public class SpdIO : IDisposable
    {
        private SerialPort _port;
        private readonly HdlcProtocol _hdlc;

        // 端口配置 (用于重连)
        private string _portName;
        private int _baudRate = 115200;

        // 最后收到的解码帧
        private HdlcFrame _lastFrame;

        // 配置
        public int DefaultTimeout { get; set; } = 10000;

        // 事件
        public event Action<string> OnLog;

        // 属性
        public bool IsOpen => _port != null && _port.IsOpen;
        public string PortName => _portName;
        public int BaudRate => _baudRate;
        public SerialPort Port => _port;
        public HdlcProtocol Hdlc => _hdlc;

        /// <summary>
        /// 最后收到的已解码帧
        /// </summary>
        public HdlcFrame LastFrame => _lastFrame;

        public SpdIO(Action<string> log = null)
        {
            OnLog = log;
            _hdlc = new HdlcProtocol(msg => OnLog?.Invoke(msg));
        }

        #region 端口管理

        /// <summary>
        /// 打开串口
        /// </summary>
        public void Open(string portName, int baudRate = 115200)
        {
            Close();

            _portName = portName;
            _baudRate = baudRate;

            _port = new SerialPort(portName)
            {
                BaudRate = baudRate,
                DataBits = 8,
                StopBits = StopBits.One,
                Parity = Parity.None,
                ReadTimeout = 3000,
                WriteTimeout = 3000,
                ReadBufferSize = 65536,
                WriteBufferSize = 65536
            };

            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        public void Close()
        {
            if (_port != null)
            {
                try
                {
                    if (_port.IsOpen)
                    {
                        try { _port.DiscardInBuffer(); } catch { }
                        try { _port.DiscardOutBuffer(); } catch { }
                        _port.Close();
                    }
                    _port.Dispose();
                }
                catch { }
                _port = null;
            }
        }

        /// <summary>
        /// 更新波特率
        /// </summary>
        public void SetBaudRate(int baudRate)
        {
            _baudRate = baudRate;
            if (_port != null && _port.IsOpen)
            {
                _port.BaudRate = baudRate;
            }
        }

        /// <summary>
        /// 清空缓冲区
        /// </summary>
        public void Flush()
        {
            if (_port != null && _port.IsOpen)
            {
                try { _port.DiscardInBuffer(); } catch { }
                try { _port.DiscardOutBuffer(); } catch { }
            }
        }

        #endregion

        #region 底层发送/接收

        /// <summary>
        /// 发送原始数据到串口
        /// </summary>
        public void SendRaw(byte[] data)
        {
            if (_port == null || !_port.IsOpen)
                throw new InvalidOperationException("端口未打开");
            _port.Write(data, 0, data.Length);
        }

        /// <summary>
        /// 发送原始数据 (安全版本，不抛异常)
        /// </summary>
        public bool TrySendRaw(byte[] data)
        {
            try
            {
                if (_port == null || !_port.IsOpen)
                    return false;
                _port.Write(data, 0, data.Length);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 从串口读取一个完整的 HDLC 帧 (0x7E...0x7E)
        /// 参考 spd_dump.c recv_msg 的帧提取逻辑
        /// </summary>
        public async Task<byte[]> RecvRawFrameAsync(int timeout = 0)
        {
            if (_port == null || !_port.IsOpen)
                return null;

            if (timeout <= 0)
                timeout = DefaultTimeout;

            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    return await Task.Run(() => RecvRawFrameBlocking(cts.Token), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 阻塞式帧读取 (在 Task.Run 中执行)
        /// </summary>
        private byte[] RecvRawFrameBlocking(CancellationToken ct)
        {
            var ms = new MemoryStream();
            bool inFrame = false;

            while (!ct.IsCancellationRequested)
            {
                if (_port == null || !_port.IsOpen)
                    return null;

                try
                {
                    int available = _port.BytesToRead;
                    if (available > 0)
                    {
                        byte[] buf = new byte[available];
                        int read = _port.Read(buf, 0, available);

                        for (int i = 0; i < read; i++)
                        {
                            byte b = buf[i];

                            if (b == HdlcProtocol.HDLC_FLAG)
                            {
                                if (inFrame && ms.Length > 0)
                                {
                                    // 帧结束
                                    ms.WriteByte(b);
                                    return ms.ToArray();
                                }
                                // 帧开始
                                inFrame = true;
                                ms.SetLength(0);
                            }

                            if (inFrame)
                            {
                                ms.WriteByte(b);
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }
                catch (TimeoutException)
                {
                    Thread.Sleep(10);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }

            return null;
        }

        #endregion

        #region HDLC 消息层

        /// <summary>
        /// 编码并发送 HDLC 消息 (参考 spd_dump.c encode_msg + send_msg)
        /// </summary>
        public void SendMessage(int type, byte[] payload = null)
        {
            byte[] frame = _hdlc.BuildFrame((byte)type, payload);
            SendRaw(frame);
        }

        /// <summary>
        /// 编码并发送 HDLC 消息 (安全版本)
        /// </summary>
        public bool TrySendMessage(int type, byte[] payload = null)
        {
            try
            {
                byte[] frame = _hdlc.BuildFrame((byte)type, payload);
                return TrySendRaw(frame);
            }
            catch { return false; }
        }

        /// <summary>
        /// 接收并解码 HDLC 消息 (参考 spd_dump.c recv_msg)
        /// 返回响应类型，解码后的帧存储在 LastFrame 中
        /// </summary>
        public async Task<int> RecvMessageAsync(int timeout = 0)
        {
            _lastFrame = null;
            byte[] raw = await RecvRawFrameAsync(timeout);
            if (raw == null)
                return -1;

            try
            {
                _lastFrame = _hdlc.ParseFrame(raw);
                return _lastFrame.Type;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 发送消息并等待 ACK (参考 spd_dump.c send_and_check)
        /// </summary>
        public async Task<bool> SendAndCheckAsync(int type, byte[] payload = null, int timeout = 0)
        {
            TrySendMessage(type, payload);
            int resp = await RecvMessageAsync(timeout);
            return resp == (int)BslCommand.BSL_REP_ACK;
        }

        /// <summary>
        /// 发送消息并等待特定响应类型
        /// </summary>
        public async Task<HdlcFrame> SendAndRecvAsync(int type, byte[] payload = null, int timeout = 0)
        {
            TrySendMessage(type, payload);
            await RecvMessageAsync(timeout);
            return _lastFrame;
        }

        /// <summary>
        /// 发送消息并等待 ACK (带重试)
        /// </summary>
        public async Task<bool> SendAndCheckRetryAsync(int type, byte[] payload = null, int timeout = 0, int retries = 3)
        {
            for (int i = 0; i <= retries; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(300);
                    Flush();
                }

                if (await SendAndCheckAsync(type, payload, timeout))
                    return true;
            }
            return false;
        }

        #endregion

        #region 文件传输

        /// <summary>
        /// 发送文件数据 (参考 spd_dump.c send_file)
        /// START_DATA(addr, size) → MIDST_DATA(chunks) → END_DATA
        /// </summary>
        public async Task<bool> SendFileAsync(byte[] data, uint address, int chunkSize = 528,
            Action<int, int> onProgress = null)
        {
            // 1. START_DATA
            byte[] startPayload = new byte[8];
            WriteBE32(startPayload, 0, address);
            WriteBE32(startPayload, 4, (uint)data.Length);

            if (!await SendAndCheckAsync((int)BslCommand.BSL_CMD_START_DATA, startPayload, 5000))
                return false;

            // 2. MIDST_DATA (分块)
            int totalChunks = (data.Length + chunkSize - 1) / chunkSize;
            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * chunkSize;
                int length = Math.Min(chunkSize, data.Length - offset);

                byte[] chunk = new byte[length];
                Array.Copy(data, offset, chunk, 0, length);

                if (!await SendAndCheckAsync((int)BslCommand.BSL_CMD_MIDST_DATA, chunk, 10000))
                    return false;

                onProgress?.Invoke(i + 1, totalChunks);
            }

            // 3. END_DATA
            return await SendAndCheckAsync((int)BslCommand.BSL_CMD_END_DATA, null, 10000);
        }

        /// <summary>
        /// 选择分区 (用于 START_DATA / READ_START)
        /// 参考 spd_dump.c select_partition
        /// </summary>
        public byte[] BuildPartitionPayload(string name, ulong size)
        {
            bool mode64 = (size >> 32) != 0;
            int payloadSize = mode64 ? 80 : 76;
            byte[] payload = new byte[payloadSize];

            // 分区名: Unicode, 最多 36 字符 (72 字节)
            byte[] nameBytes = Encoding.Unicode.GetBytes(name);
            Array.Copy(nameBytes, 0, payload, 0, Math.Min(nameBytes.Length, 72));

            // 大小: Little-Endian (参考 spd_dump.c: WRITE32_LE)
            WriteLE32(payload, 72, (uint)(size & 0xFFFFFFFF));
            if (mode64)
                WriteLE32(payload, 76, (uint)(size >> 32));

            return payload;
        }

        #endregion

        #region 字节序工具

        public static void WriteBE32(byte[] buf, int off, uint val)
        {
            buf[off] = (byte)((val >> 24) & 0xFF);
            buf[off + 1] = (byte)((val >> 16) & 0xFF);
            buf[off + 2] = (byte)((val >> 8) & 0xFF);
            buf[off + 3] = (byte)(val & 0xFF);
        }

        public static void WriteBE16(byte[] buf, int off, ushort val)
        {
            buf[off] = (byte)((val >> 8) & 0xFF);
            buf[off + 1] = (byte)(val & 0xFF);
        }

        public static void WriteLE32(byte[] buf, int off, uint val)
        {
            buf[off] = (byte)(val & 0xFF);
            buf[off + 1] = (byte)((val >> 8) & 0xFF);
            buf[off + 2] = (byte)((val >> 16) & 0xFF);
            buf[off + 3] = (byte)((val >> 24) & 0xFF);
        }

        public static uint ReadBE32(byte[] buf, int off)
        {
            return (uint)((buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3]);
        }

        public static uint ReadLE32(byte[] buf, int off)
        {
            return (uint)(buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24));
        }

        #endregion

        #region 辅助

        private void Log(string fmt, params object[] args)
        {
            OnLog?.Invoke(string.Format(fmt, args));
        }

        public void Dispose()
        {
            Close();
        }

        #endregion
    }
}
