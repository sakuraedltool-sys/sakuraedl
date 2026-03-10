// ============================================================================
// SakuraEDL - Qualcomm Diag Client | 高通 Diag 客户端
// ============================================================================
// [ZH] Diag 诊断协议客户端 - 读写 IMEI/MEID/QCN/NV
// [EN] Diag Protocol Client - Read/write IMEI/MEID/QCN/NV
// [JA] Diagプロトコルクライアント - IMEI/MEID/QCN/NV読み書き
// [KO] Diag 프로토콜 클라이언트 - IMEI/MEID/QCN/NV 읽기/쓰기
// [RU] Клиент протокола Diag - Чтение/запись IMEI/MEID/QCN/NV
// [ES] Cliente de protocolo Diag - Lectura/escritura IMEI/MEID/QCN/NV
// ============================================================================
// Pure C# implementation, no external QMSL DLL required
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

namespace SakuraEDL.Qualcomm.Protocol
{
    /// <summary>
    /// Qualcomm Diag 命令码
    /// </summary>
    public static class DiagCommands
    {
        // 基础命令
        public const byte DIAG_VERNO_F = 0x00;          // Version Number
        public const byte DIAG_ESN_F = 0x01;            // ESN
        public const byte DIAG_PEEKB_F = 0x02;          // Peek byte
        public const byte DIAG_PEEKW_F = 0x03;          // Peek word
        public const byte DIAG_PEEKD_F = 0x04;          // Peek dword
        public const byte DIAG_POKEB_F = 0x05;          // Poke byte
        public const byte DIAG_POKEW_F = 0x06;          // Poke word
        public const byte DIAG_POKED_F = 0x07;          // Poke dword
        public const byte DIAG_OUTP_F = 0x08;           // Byte output
        public const byte DIAG_OUTPW_F = 0x09;          // Word output
        public const byte DIAG_INP_F = 0x0A;            // Byte input
        public const byte DIAG_INPW_F = 0x0B;           // Word input
        public const byte DIAG_STATUS_F = 0x0C;         // Status
        public const byte DIAG_LOGMASK_F = 0x0F;        // Log mask
        public const byte DIAG_LOG_F = 0x10;            // Log
        public const byte DIAG_NV_PEEK_F = 0x11;        // NV Peek
        public const byte DIAG_NV_POKE_F = 0x12;        // NV Poke
        public const byte DIAG_BAD_CMD_F = 0x13;        // Bad command
        public const byte DIAG_BAD_PARM_F = 0x14;       // Bad parameter
        public const byte DIAG_BAD_LEN_F = 0x15;        // Bad length
        public const byte DIAG_BAD_MODE_F = 0x18;       // Bad mode
        public const byte DIAG_TAGRAPH_F = 0x19;        // TA graph
        public const byte DIAG_MARKOV_F = 0x1A;         // Markov
        public const byte DIAG_MARKOV_RESET_F = 0x1B;   // Markov reset
        public const byte DIAG_DIAG_VER_F = 0x1C;       // Diag version
        public const byte DIAG_TS_F = 0x1D;             // Timestamp
        public const byte DIAG_TA_PARM_F = 0x1E;        // TA parameter
        public const byte DIAG_MSG_F = 0x1F;            // Message
        public const byte DIAG_HS_KEY_F = 0x20;         // HS key
        public const byte DIAG_HS_LOCK_F = 0x21;        // HS lock
        public const byte DIAG_HS_SCREEN_F = 0x22;      // HS screen
        
        // NV 操作
        public const byte DIAG_NV_READ_F = 0x26;        // NV Read
        public const byte DIAG_NV_WRITE_F = 0x27;       // NV Write
        
        // 控制命令
        public const byte DIAG_CONTROL_F = 0x29;        // Control
        public const byte DIAG_ERR_READ_F = 0x2A;       // Error read
        public const byte DIAG_ERR_CLEAR_F = 0x2B;      // Error clear
        public const byte DIAG_SER_RESET_F = 0x2C;      // Serial reset
        public const byte DIAG_SER_REPORT_F = 0x2D;     // Serial report
        public const byte DIAG_TEST_F = 0x2E;           // Test
        public const byte DIAG_GET_DIPSW_F = 0x2F;      // Get DIP switch
        public const byte DIAG_SET_DIPSW_F = 0x30;      // Set DIP switch
        public const byte DIAG_VOC_PCM_LB_F = 0x31;     // VOC PCM loopback
        public const byte DIAG_VOC_PKT_LB_F = 0x32;     // VOC packet loopback
        
        // SPC/安全
        public const byte DIAG_SPC_F = 0x41;            // SPC unlock
        public const byte DIAG_BAD_SPC_MODE_F = 0x42;   // Bad SPC mode
        public const byte DIAG_PARM_GET_F = 0x43;       // Parameter get
        public const byte DIAG_PARM_SET_F = 0x44;       // Parameter set
        public const byte DIAG_PARM_GET2_F = 0x45;      // Parameter get 2
        
        // 模式控制
        public const byte DIAG_MODE_CHANGE_F = 0x4B;    // Mode change
        public const byte DIAG_SUBSYS_CMD_F = 0x4B;     // Subsystem command
        
        // 扩展命令
        public const byte DIAG_FEATURE_QUERY_F = 0x51;  // Feature query
        public const byte DIAG_SMS_READ_F = 0x53;       // SMS read
        public const byte DIAG_SMS_WRITE_F = 0x54;      // SMS write
        public const byte DIAG_SUP_FER_F = 0x55;        // SUP FER
        public const byte DIAG_SUP_WALSH_CODES_F = 0x56;// SUP Walsh codes
        public const byte DIAG_SET_MAX_SUP_CH_F = 0x57; // Set max SUP ch
        public const byte DIAG_PARM_GET_IS95B_F = 0x58; // IS-95B param get
        public const byte DIAG_FS_OP_F = 0x59;          // EFS operation
        public const byte DIAG_AKEY_VERIFY_F = 0x5A;    // AKEY verify
        public const byte DIAG_BMP_HS_SCREEN_F = 0x5B;  // BMP HS screen
        
        // EFS2 操作
        public const byte DIAG_EFS2_F = 0x4B;           // EFS2 (subsystem)
        
        // 子系统 ID
        public const byte DIAG_SUBSYS_LEGACY = 0x00;
        public const byte DIAG_SUBSYS_FTM = 0x0B;
        public const byte DIAG_SUBSYS_NV = 0x32;
        public const byte DIAG_SUBSYS_EFS2 = 0x13;
        public const byte DIAG_SUBSYS_CALL_CMD = 0x64;
    }

    /// <summary>
    /// NV 项枚举
    /// </summary>
    public enum NvItems : uint
    {
        NV_ESN_I = 0,
        NV_ESN_CHKSUM_I = 1,
        NV_VERNO_MAJ_I = 2,
        NV_VERNO_MIN_I = 3,
        NV_SCM_I = 4,
        NV_SLOT_CYCLE_INDEX_I = 5,
        NV_IMSI_I = 6,
        NV_IMSI_S1_I = 7,
        NV_IMSI_S2_I = 8,
        NV_IMSI_S_I = 9,
        NV_IMSI_T_S1_I = 10,
        NV_IMSI_T_S2_I = 11,
        NV_IMSI_T_S_I = 12,
        NV_IMSI_MCC_I = 13,
        NV_IMSI_11_12_I = 14,
        NV_IMSI_T_MCC_I = 15,
        NV_IMSI_T_11_12_I = 16,
        NV_MEID_I = 1943,
        NV_UE_IMEI_I = 550,
        NV_UE_IMEISV_I = 2285,
        
        // IMEI 相关
        NV_IMEI_I = 550,
        NV_IMEI2_I = 65672,
        NV_IMEI3_I = 65673,
        NV_IMEI4_I = 65674,
        
        // 其他常用
        NV_RF_CAL_DATE_I = 1776,
        NV_RF_CAL_VER_I = 1777,
        NV_FACTORY_INFO_I = 400,
    }

    /// <summary>
    /// Diag 响应状态
    /// </summary>
    public enum DiagStatus : byte
    {
        Success = 0,
        BadCommand = 0x13,
        BadParameter = 0x14,
        BadLength = 0x15,
        NvReadFail = 0x05,
        NvWriteFail = 0x06,
        NvNotActive = 0x07,
        BadSpc = 0x42,
    }

    /// <summary>
    /// IMEI 信息
    /// </summary>
    public class ImeiInfo
    {
        public string Imei1 { get; set; }
        public string Imei2 { get; set; }
        public string Imei3 { get; set; }
        public string Imei4 { get; set; }
    }

    /// <summary>
    /// 设备信息
    /// </summary>
    public class DiagDeviceInfo
    {
        public string CompilationDate { get; set; }
        public string CompilationTime { get; set; }
        public string ReleaseDate { get; set; }
        public string ReleaseTime { get; set; }
        public string ModelName { get; set; }
        public string MobileNumber { get; set; }
        public string Esn { get; set; }
        public string Meid { get; set; }
        public ImeiInfo Imei { get; set; } = new ImeiInfo();
    }

    /// <summary>
    /// Qualcomm Diag 客户端
    /// 纯 C# 实现，无需 QMSL DLL 依赖
    /// </summary>
    public class DiagClient : IDisposable
    {
        #region Constants
        
        private const byte HDLC_FLAG = 0x7E;
        private const byte HDLC_ESCAPE = 0x7D;
        private const byte HDLC_ESCAPE_XOR = 0x20;
        private const int DEFAULT_TIMEOUT = 5000;
        private const int BUFFER_SIZE = 4096;
        
        #endregion

        #region Fields
        
        private SerialPort _port;
        private bool _disposed;
        private readonly object _lockObj = new object();
        
        #endregion

        #region Properties
        
        public bool IsConnected => _port?.IsOpen ?? false;
        public string PortName => _port?.PortName;
        
        #endregion

        #region Connection
        
        /// <summary>
        /// 连接到诊断端口
        /// </summary>
        public async Task<bool> ConnectAsync(string portName, int baudRate = 115200)
        {
            try
            {
                if (_port?.IsOpen == true)
                {
                    _port.Close();
                }
                
                _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = DEFAULT_TIMEOUT,
                    WriteTimeout = DEFAULT_TIMEOUT,
                    ReadBufferSize = BUFFER_SIZE,
                    WriteBufferSize = BUFFER_SIZE,
                    DtrEnable = true,
                    RtsEnable = true
                };
                
                _port.Open();
                
                // 清空缓冲区
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
                
                await Task.Delay(100);
                
                // 发送 NOP 测试连接
                var version = await GetVersionAsync();
                return version != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
        
        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            if (_port?.IsOpen == true)
            {
                _port.Close();
            }
        }
        
        #endregion

        #region SPC Unlock
        
        /// <summary>
        /// SPC 解锁
        /// </summary>
        public async Task<bool> SendSpcAsync(string spc = "000000")
        {
            if (spc.Length != 6)
                throw new ArgumentException("SPC must be 6 digits");
            
            var cmd = new byte[7];
            cmd[0] = DiagCommands.DIAG_SPC_F;
            var spcBytes = Encoding.ASCII.GetBytes(spc);
            Array.Copy(spcBytes, 0, cmd, 1, 6);
            
            var response = await SendCommandAsync(cmd);
            return response != null && response.Length > 0 && response[0] == DiagCommands.DIAG_SPC_F;
        }
        
        #endregion

        #region Device Info
        
        /// <summary>
        /// 获取设备版本信息
        /// </summary>
        public async Task<DiagDeviceInfo> GetVersionAsync()
        {
            var cmd = new byte[] { DiagCommands.DIAG_VERNO_F };
            var response = await SendCommandAsync(cmd);
            
            if (response == null || response.Length < 10)
                return null;
            
            var info = new DiagDeviceInfo();
            
            try
            {
                // 解析响应
                int offset = 1;
                if (response.Length > 50)
                {
                    info.CompilationDate = Encoding.ASCII.GetString(response, offset, 11).Trim('\0');
                    offset += 11;
                    info.CompilationTime = Encoding.ASCII.GetString(response, offset, 8).Trim('\0');
                    offset += 8;
                    info.ReleaseDate = Encoding.ASCII.GetString(response, offset, 11).Trim('\0');
                    offset += 11;
                    info.ReleaseTime = Encoding.ASCII.GetString(response, offset, 8).Trim('\0');
                }
            }
            catch { }
            
            return info;
        }
        
        /// <summary>
        /// 读取 ESN
        /// </summary>
        public async Task<string> ReadEsnAsync()
        {
            var cmd = new byte[] { DiagCommands.DIAG_ESN_F };
            var response = await SendCommandAsync(cmd);
            
            if (response == null || response.Length < 5)
                return null;
            
            uint esn = BitConverter.ToUInt32(response, 1);
            return esn.ToString("X8");
        }
        
        /// <summary>
        /// 读取 MEID
        /// </summary>
        public async Task<string> ReadMeidAsync()
        {
            var data = await NvReadAsync(NvItems.NV_MEID_I);
            if (data == null || data.Length < 7)
                return null;
            
            return BitConverter.ToString(data, 0, 7).Replace("-", "");
        }
        
        #endregion

        #region IMEI Operations
        
        /// <summary>
        /// 读取 IMEI
        /// </summary>
        public async Task<string> ReadImeiAsync(int slot = 1)
        {
            NvItems nvItem = slot switch
            {
                1 => NvItems.NV_UE_IMEI_I,
                2 => NvItems.NV_IMEI2_I,
                3 => NvItems.NV_IMEI3_I,
                4 => NvItems.NV_IMEI4_I,
                _ => NvItems.NV_UE_IMEI_I
            };
            
            var data = await NvReadAsync(nvItem);
            if (data == null || data.Length < 9)
                return null;
            
            return DecodeImei(data);
        }
        
        /// <summary>
        /// 写入 IMEI
        /// </summary>
        public async Task<bool> WriteImeiAsync(string imei, int slot = 1)
        {
            if (string.IsNullOrEmpty(imei) || imei.Length != 15)
                throw new ArgumentException("IMEI must be 15 digits");
            
            NvItems nvItem = slot switch
            {
                1 => NvItems.NV_UE_IMEI_I,
                2 => NvItems.NV_IMEI2_I,
                3 => NvItems.NV_IMEI3_I,
                4 => NvItems.NV_IMEI4_I,
                _ => NvItems.NV_UE_IMEI_I
            };
            
            var data = EncodeImei(imei);
            return await NvWriteAsync(nvItem, data);
        }
        
        /// <summary>
        /// 读取所有 IMEI
        /// </summary>
        public async Task<ImeiInfo> ReadAllImeiAsync()
        {
            var info = new ImeiInfo
            {
                Imei1 = await ReadImeiAsync(1),
                Imei2 = await ReadImeiAsync(2),
                Imei3 = await ReadImeiAsync(3),
                Imei4 = await ReadImeiAsync(4)
            };
            return info;
        }
        
        /// <summary>
        /// 解码 IMEI
        /// </summary>
        private string DecodeImei(byte[] data)
        {
            if (data == null || data.Length < 9)
                return null;
            
            var sb = new StringBuilder();
            
            // IMEI 格式: 1 byte length + 8 bytes data (BCD)
            int length = data[0];
            
            for (int i = 1; i <= 8 && i < data.Length; i++)
            {
                byte b = data[i];
                int low = b & 0x0F;
                int high = (b >> 4) & 0x0F;
                
                if (i == 1)
                {
                    // 第一个字节只有高4位
                    if (high < 10) sb.Append(high);
                }
                else
                {
                    if (low < 10) sb.Append(low);
                    if (high < 10) sb.Append(high);
                }
            }
            
            return sb.Length >= 14 ? sb.ToString() : null;
        }
        
        /// <summary>
        /// 编码 IMEI
        /// </summary>
        private byte[] EncodeImei(string imei)
        {
            var data = new byte[9];
            data[0] = 0x08;  // Length
            
            // IMEI 编码为 BCD
            data[1] = (byte)(0xA0 | (imei[0] - '0'));
            
            for (int i = 1; i < 8; i++)
            {
                int idx = i * 2 - 1;
                int low = imei[idx] - '0';
                int high = idx + 1 < imei.Length ? imei[idx + 1] - '0' : 0x0F;
                data[i + 1] = (byte)((high << 4) | low);
            }
            
            return data;
        }
        
        #endregion

        #region NV Operations
        
        /// <summary>
        /// 读取 NV 项
        /// </summary>
        public async Task<byte[]> NvReadAsync(NvItems item)
        {
            return await NvReadAsync((ushort)item);
        }
        
        /// <summary>
        /// 读取 NV 项
        /// </summary>
        public async Task<byte[]> NvReadAsync(ushort item)
        {
            var cmd = new byte[3];
            cmd[0] = DiagCommands.DIAG_NV_READ_F;
            cmd[1] = (byte)(item & 0xFF);
            cmd[2] = (byte)((item >> 8) & 0xFF);
            
            // 填充到 133 字节
            var fullCmd = new byte[133];
            Array.Copy(cmd, fullCmd, cmd.Length);
            
            var response = await SendCommandAsync(fullCmd);
            
            if (response == null || response.Length < 4)
                return null;
            
            if (response[0] != DiagCommands.DIAG_NV_READ_F)
                return null;
            
            // 检查状态
            if (response.Length >= 134 && response[133] != 0)
                return null;
            
            // 返回数据部分 (跳过命令码和项号)
            var data = new byte[128];
            Array.Copy(response, 3, data, 0, Math.Min(128, response.Length - 3));
            return data;
        }
        
        /// <summary>
        /// 写入 NV 项
        /// </summary>
        public async Task<bool> NvWriteAsync(NvItems item, byte[] data)
        {
            return await NvWriteAsync((ushort)item, data);
        }
        
        /// <summary>
        /// 写入 NV 项
        /// </summary>
        public async Task<bool> NvWriteAsync(ushort item, byte[] data)
        {
            var cmd = new byte[133];
            cmd[0] = DiagCommands.DIAG_NV_WRITE_F;
            cmd[1] = (byte)(item & 0xFF);
            cmd[2] = (byte)((item >> 8) & 0xFF);
            
            if (data != null && data.Length > 0)
            {
                Array.Copy(data, 0, cmd, 3, Math.Min(data.Length, 128));
            }
            
            var response = await SendCommandAsync(cmd);
            
            if (response == null || response.Length < 1)
                return false;
            
            return response[0] == DiagCommands.DIAG_NV_WRITE_F;
        }
        
        #endregion

        #region QCN Operations
        
        /// <summary>
        /// 读取 QCN 文件
        /// </summary>
        public async Task<bool> ReadQcnAsync(string filePath, IProgress<int> progress = null)
        {
            var nvItems = new List<ushort>();
            
            // 常用 NV 项列表
            ushort[] commonItems = {
                0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
                (ushort)NvItems.NV_UE_IMEI_I,
                (ushort)NvItems.NV_MEID_I,
                (ushort)NvItems.NV_FACTORY_INFO_I,
                // ... 更多 NV 项
            };
            
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(fs))
            {
                // 写入 QCN 头
                writer.Write(Encoding.ASCII.GetBytes("QCN\0"));
                writer.Write(1); // Version
                
                int count = 0;
                foreach (var item in commonItems)
                {
                    try
                    {
                        var data = await NvReadAsync(item);
                        if (data != null)
                        {
                            writer.Write(item);
                            writer.Write((short)data.Length);
                            writer.Write(data);
                            nvItems.Add(item);
                        }
                    }
                    catch { }
                    
                    count++;
                    progress?.Report(count * 100 / commonItems.Length);
                }
                
                // 更新项目数
                fs.Seek(8, SeekOrigin.Begin);
                writer.Write(nvItems.Count);
            }
            
            return nvItems.Count > 0;
        }
        
        /// <summary>
        /// 写入 QCN 文件
        /// </summary>
        public async Task<bool> WriteQcnAsync(string filePath, IProgress<int> progress = null)
        {
            if (!File.Exists(filePath))
                return false;
            
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                // 读取头
                var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (!magic.StartsWith("QCN"))
                    return false;
                
                int version = reader.ReadInt32();
                int itemCount = reader.ReadInt32();
                
                int written = 0;
                for (int i = 0; i < itemCount; i++)
                {
                    try
                    {
                        ushort item = reader.ReadUInt16();
                        short length = reader.ReadInt16();
                        var data = reader.ReadBytes(length);
                        
                        if (await NvWriteAsync(item, data))
                            written++;
                    }
                    catch { }
                    
                    progress?.Report((i + 1) * 100 / itemCount);
                }
                
                return written > 0;
            }
        }
        
        #endregion

        #region Mode Control
        
        /// <summary>
        /// 切换到下载模式 (EDL)
        /// </summary>
        public async Task<bool> SwitchToDownloadModeAsync()
        {
            var cmd = new byte[] { DiagCommands.DIAG_MODE_CHANGE_F, 0x0E };  // Download mode
            var response = await SendCommandAsync(cmd);
            return response != null;
        }
        
        /// <summary>
        /// 重启设备
        /// </summary>
        public async Task<bool> RebootAsync()
        {
            var cmd = new byte[] { DiagCommands.DIAG_MODE_CHANGE_F, 0x01 };  // Reset
            var response = await SendCommandAsync(cmd);
            return response != null;
        }
        
        /// <summary>
        /// 切换到离线模式
        /// </summary>
        public async Task<bool> SwitchToOfflineModeAsync()
        {
            var cmd = new byte[] { DiagCommands.DIAG_MODE_CHANGE_F, 0x04 };  // Offline digital
            var response = await SendCommandAsync(cmd);
            return response != null;
        }
        
        /// <summary>
        /// 关机
        /// </summary>
        public async Task<bool> PowerOffAsync()
        {
            var cmd = new byte[] { DiagCommands.DIAG_MODE_CHANGE_F, 0x02 };  // Power off
            var response = await SendCommandAsync(cmd);
            return response != null;
        }
        
        #endregion

        #region AT Commands (via Diag)
        
        /// <summary>
        /// 发送 AT 命令
        /// </summary>
        public async Task<string> SendAtCommandAsync(string command, int timeout = 5000)
        {
            // AT 命令通过 EFS 子系统发送
            // 格式: SUBSYS_CMD(0x4B) + SUBSYS_ID + CMD_CODE + DATA
            
            var atBytes = Encoding.ASCII.GetBytes(command + "\r");
            var cmd = new byte[3 + atBytes.Length];
            cmd[0] = DiagCommands.DIAG_SUBSYS_CMD_F;
            cmd[1] = 0x64;  // Call subsystem
            cmd[2] = 0x03;  // AT command
            Array.Copy(atBytes, 0, cmd, 3, atBytes.Length);
            
            var response = await SendCommandAsync(cmd, timeout);
            
            if (response == null || response.Length < 4)
                return null;
            
            return Encoding.ASCII.GetString(response, 3, response.Length - 3).Trim('\0', '\r', '\n');
        }
        
        #endregion

        #region HDLC Framing
        
        /// <summary>
        /// 发送命令
        /// </summary>
        private async Task<byte[]> SendCommandAsync(byte[] cmd, int timeout = DEFAULT_TIMEOUT)
        {
            if (_port == null || !_port.IsOpen)
                return null;
            
            lock (_lockObj)
            {
                // HDLC 编码
                var frame = HdlcEncode(cmd);
                
                // 发送
                _port.DiscardInBuffer();
                _port.Write(frame, 0, frame.Length);
            }
            
            // 等待响应
            return await Task.Run(() => ReceiveResponse(timeout));
        }
        
        /// <summary>
        /// 接收响应
        /// </summary>
        private byte[] ReceiveResponse(int timeout)
        {
            var buffer = new List<byte>();
            var startTime = DateTime.Now;
            bool foundStart = false;
            
            while ((DateTime.Now - startTime).TotalMilliseconds < timeout)
            {
                if (_port.BytesToRead > 0)
                {
                    int b = _port.ReadByte();
                    
                    if (b == HDLC_FLAG)
                    {
                        if (foundStart && buffer.Count > 0)
                        {
                            // 帧结束
                            return HdlcDecode(buffer.ToArray());
                        }
                        foundStart = true;
                        buffer.Clear();
                    }
                    else if (foundStart)
                    {
                        buffer.Add((byte)b);
                    }
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
            
            return buffer.Count > 0 ? HdlcDecode(buffer.ToArray()) : null;
        }
        
        /// <summary>
        /// HDLC 编码
        /// </summary>
        private byte[] HdlcEncode(byte[] data)
        {
            var result = new List<byte>();
            result.Add(HDLC_FLAG);
            
            // 计算 CRC
            ushort crc = CalculateCrc(data);
            var dataWithCrc = new byte[data.Length + 2];
            Array.Copy(data, dataWithCrc, data.Length);
            dataWithCrc[data.Length] = (byte)(crc & 0xFF);
            dataWithCrc[data.Length + 1] = (byte)((crc >> 8) & 0xFF);
            
            // 转义
            foreach (byte b in dataWithCrc)
            {
                if (b == HDLC_FLAG || b == HDLC_ESCAPE)
                {
                    result.Add(HDLC_ESCAPE);
                    result.Add((byte)(b ^ HDLC_ESCAPE_XOR));
                }
                else
                {
                    result.Add(b);
                }
            }
            
            result.Add(HDLC_FLAG);
            return result.ToArray();
        }
        
        /// <summary>
        /// HDLC 解码
        /// </summary>
        private byte[] HdlcDecode(byte[] data)
        {
            if (data == null || data.Length < 2)
                return null;
            
            var result = new List<byte>();
            bool escape = false;
            
            foreach (byte b in data)
            {
                if (escape)
                {
                    result.Add((byte)(b ^ HDLC_ESCAPE_XOR));
                    escape = false;
                }
                else if (b == HDLC_ESCAPE)
                {
                    escape = true;
                }
                else
                {
                    result.Add(b);
                }
            }
            
            // 移除 CRC
            if (result.Count >= 2)
            {
                result.RemoveRange(result.Count - 2, 2);
            }
            
            return result.ToArray();
        }
        
        /// <summary>
        /// 计算 CRC-16
        /// </summary>
        private ushort CalculateCrc(byte[] data)
        {
            ushort crc = 0xFFFF;
            
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 1) != 0)
                        crc = (ushort)((crc >> 1) ^ 0x8408);
                    else
                        crc >>= 1;
                }
            }
            
            return (ushort)~crc;
        }
        
        #endregion

        #region IDisposable
        
        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _port?.Dispose();
                _disposed = true;
            }
        }
        
        #endregion
    }
}
