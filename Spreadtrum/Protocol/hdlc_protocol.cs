// ============================================================================
// SakuraEDL - Spreadtrum HDLC Protocol | 展讯 HDLC 协议
// ============================================================================
// [ZH] HDLC 帧协议 - 展讯/紫光展锐通信帧编码实现
// [EN] HDLC Frame Protocol - Spreadtrum/Unisoc communication frame encoding
// [JA] HDLCフレームプロトコル - Spreadtrum/Unisoc通信フレームエンコーディング
// [KO] HDLC 프레임 프로토콜 - Spreadtrum/Unisoc 통신 프레임 인코딩
// [RU] Протокол HDLC - Кодирование кадров связи Spreadtrum/Unisoc
// [ES] Protocolo HDLC - Codificación de tramas Spreadtrum/Unisoc
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace SakuraEDL.Spreadtrum.Protocol
{
    /// <summary>
    /// 展讯 HDLC 协议实现
    /// 帧格式: Flag(0x7E) + Type(1) + Length(2) + Payload(N) + CRC16(2) + Flag(0x7E)
    /// </summary>
    public class HdlcProtocol
    {
        // HDLC 帧定界符
        public const byte HDLC_FLAG = 0x7E;
        public const byte HDLC_ESCAPE = 0x7D;
        public const byte HDLC_ESCAPE_XOR = 0x20;

        private readonly Action<string> _log;
        
        // 是否跳过接收端 CRC 校验 (Spreadtrum BROM 兼容模式)
        public bool SkipRxCrcCheck { get; set; } = true;
        
        /// <summary>
        /// 校验模式: true = CRC16 (BROM 阶段), false = Checksum (FDL 阶段)
        /// BROM 阶段使用 CRC-16-CCITT，FDL 阶段使用 Spreadtrum 专有校验和
        /// </summary>
        public bool UseCrc16Mode { get; set; } = true;
        
        /// <summary>
        /// 转码模式: true = 启用转码 (默认), false = 禁用转码
        /// 当启用转码时，0x7D 和 0x7E 字节会被转义
        /// FDL2 执行后通常需要禁用转码以提高传输效率
        /// </summary>
        public bool UseTranscode { get; set; } = true;
        
        /// <summary>
        /// Raw 模式: true = 直接发送原始数据（无 HDLC 封装）
        /// 用于 FDL2 阶段发送大块分区数据
        /// </summary>
        public bool RawMode { get; set; } = false;
        
        public HdlcProtocol(Action<string> log = null)
        {
            _log = log;
        }
        
        /// <summary>
        /// 切换到 FDL 模式 (使用 Spreadtrum checksum)
        /// </summary>
        public void SetFdlMode()
        {
            UseCrc16Mode = false;
            _log?.Invoke("[HDLC] 切换到 FDL 模式 (Checksum)");
        }
        
        /// <summary>
        /// 切换到 BROM 模式 (使用 CRC16)
        /// </summary>
        public void SetBromMode()
        {
            UseCrc16Mode = true;
            _log?.Invoke("[HDLC] 切换到 BROM 模式 (CRC16)");
        }

        /// <summary>
        /// 切换校验模式 (参考 SPRDClientCore)
        /// </summary>
        public void ToggleChecksumMode()
        {
            UseCrc16Mode = !UseCrc16Mode;
            _log?.Invoke($"[HDLC] 切换校验模式: {(UseCrc16Mode ? "CRC16" : "Checksum")}");
        }
        
        /// <summary>
        /// 禁用转码 (FDL2 必需步骤)
        /// 参考: spd_dump.c - io->flags &= ~FLAGS_TRANSCODE
        /// </summary>
        public void DisableTranscode()
        {
            UseTranscode = false;
            _log?.Invoke("[HDLC] 转码已禁用");
        }
        
        /// <summary>
        /// 启用转码 (默认)
        /// </summary>
        public void EnableTranscode()
        {
            UseTranscode = true;
            _log?.Invoke("[HDLC] 转码已启用");
        }

        /// <summary>
        /// 构建 HDLC 帧
        /// 支持动态端序切换和 Raw 模式
        /// </summary>
        /// <param name="type">命令类型</param>
        /// <param name="payload">数据负载</param>
        /// <returns>完整的 HDLC 帧</returns>
        public byte[] BuildFrame(byte type, byte[] payload)
        {
            if (payload == null)
                payload = new byte[0];

            using (var ms = new MemoryStream())
            {
                // Raw 模式: 直接返回 payload（无 HDLC 封装）
                if (RawMode)
                {
                    return payload;
                }
                
                // 帧头
                ms.WriteByte(HDLC_FLAG);

                // 构建数据部分
                // 格式: Type(2) + Length(2) + Payload + CRC(2)
                var data = new List<byte>();
                
                // Type: 2 bytes (always Big-Endian, per spd_dump.c encode_msg)
                data.Add(0x00);  // Type high byte (SubType)
                data.Add(type);  // Type low byte (Command)
                
                // Length: 2 bytes (always Big-Endian, per spd_dump.c encode_msg)
                ushort length = (ushort)payload.Length;
                data.Add((byte)((length >> 8) & 0xFF));  // Length high byte
                data.Add((byte)(length & 0xFF));         // Length low byte
                
                // 添加 payload
                if (payload.Length > 0)
                    data.AddRange(payload);

                // 根据模式选择校验算法
                ushort checksum;
                if (UseCrc16Mode)
                {
                    // BROM 模式: 使用 CRC-16-CCITT
                    checksum = CalculateCRC16Ccitt(data.ToArray());
                }
                else
                {
                    // FDL 模式: 使用 Spreadtrum 专有校验和
                    checksum = CalculateSprdChecksum(data.ToArray());
                }
                
                // 校验和 (always Big-Endian, per spd_dump.c encode_msg)
                data.Add((byte)((checksum >> 8) & 0xFF));  // high byte
                data.Add((byte)(checksum & 0xFF));         // low byte

                // 转义写入
                foreach (byte b in data)
                {
                    WriteEscaped(ms, b);
                }

                // 帧尾
                ms.WriteByte(HDLC_FLAG);

                return ms.ToArray();
            }
        }
        
        /// <summary>
        /// 构建简单命令帧 (无 payload)
        /// </summary>
        public byte[] BuildCommand(byte type)
        {
            return BuildFrame(type, null);
        }

        /// <summary>
        /// 解析 HDLC 帧
        /// </summary>
        /// <param name="frame">原始帧数据</param>
        /// <returns>命令类型和负载数据</returns>
        public HdlcFrame ParseFrame(byte[] frame)
        {
            HdlcFrame result;
            HdlcParseError error;
            if (!TryParseFrame(frame, out result, out error))
            {
                throw new InvalidDataException(GetErrorMessage(error));
            }
            return result;
        }

        /// <summary>
        /// 尝试解析 HDLC 帧 (不抛异常)
        /// 支持自动校验模式切换 (参考 SPRDClientCore)
        /// </summary>
        public bool TryParseFrame(byte[] frame, out HdlcFrame result, out HdlcParseError error)
        {
            result = null;
            error = HdlcParseError.None;

            if (frame == null || frame.Length < 7)
            {
                error = HdlcParseError.FrameTooShort;
                return false;
            }

            if (frame[0] != HDLC_FLAG || frame[frame.Length - 1] != HDLC_FLAG)
            {
                error = HdlcParseError.InvalidDelimiter;
                return false;
            }

            // 反转义数据
            var data = new List<byte>();
            bool escaped = false;

            for (int i = 1; i < frame.Length - 1; i++)
            {
                if (escaped)
                {
                    data.Add((byte)(frame[i] ^ HDLC_ESCAPE_XOR));
                    escaped = false;
                }
                else if (frame[i] == HDLC_ESCAPE)
                {
                    escaped = true;
                }
                else
                {
                    data.Add(frame[i]);
                }
            }

            if (data.Count < 6) // Type(2) + Length(2) + CRC(2)
            {
                error = HdlcParseError.FrameIncomplete;
                return false;
            }

            // 解析字段 - Spreadtrum 使用 Big-Endian 格式
            // 格式: [Type Hi] [Type Lo] [Length Hi] [Length Lo] [Payload...] [CRC Hi] [CRC Lo]
            byte subType = data[0];  // Type high byte (通常为 0x00)
            byte type = data[1];     // Type low byte (Command)
            ushort length = (ushort)((data[2] << 8) | data[3]);  // Big-endian

            // 提取 payload
            byte[] payload = new byte[length];
            if (length > 0)
            {
                if (data.Count < 4 + length + 2)
                {
                    error = HdlcParseError.PayloadMismatch;
                    return false;
                }
                
                for (int i = 0; i < length; i++)
                    payload[i] = data[4 + i];
            }

            // 验证 CRC (Big-Endian，与开源 spd_dump 实现一致)
            int crcOffset = 4 + length;
            ushort receivedCrc = (ushort)((data[crcOffset] << 8) | data[crcOffset + 1]);  // Big-Endian
            
            // Spreadtrum BROM 使用不同的 CRC 算法，兼容模式下跳过校验
            if (!SkipRxCrcCheck)
            {
                byte[] crcData = data.GetRange(0, crcOffset).ToArray();
                
                // 使用当前校验模式计算
                ushort calculatedCrc = UseCrc16Mode 
                    ? CalculateCRC16Ccitt(crcData) 
                    : CalculateSprdChecksum(crcData);
                
                if (receivedCrc != calculatedCrc)
                {
                    // 自动尝试另一种校验模式 (参考 SPRDClientCore)
                    ushort alternativeCrc = UseCrc16Mode 
                        ? CalculateSprdChecksum(crcData) 
                        : CalculateCRC16Ccitt(crcData);
                    
                    if (receivedCrc == alternativeCrc)
                    {
                        // 自动切换校验模式
                        UseCrc16Mode = !UseCrc16Mode;
                        _log?.Invoke(string.Format("[HDLC] 自动切换校验模式: {0}", 
                            UseCrc16Mode ? "CRC16" : "Checksum"));
                    }
                    else
                    {
                        _log?.Invoke(string.Format("[HDLC] CRC 校验失败: 接收=0x{0:X4}, CRC16=0x{1:X4}, Checksum=0x{2:X4}", 
                            receivedCrc, 
                            UseCrc16Mode ? calculatedCrc : alternativeCrc,
                            UseCrc16Mode ? alternativeCrc : calculatedCrc));
                        error = HdlcParseError.CrcMismatch;
                        return false;
                    }
                }
            }

            result = new HdlcFrame
            {
                Type = type,
                Length = length,
                Payload = payload,
                Crc = receivedCrc
            };
            return true;
        }

        private string GetErrorMessage(HdlcParseError error)
        {
            switch (error)
            {
                case HdlcParseError.FrameTooShort: return "帧数据太短";
                case HdlcParseError.InvalidDelimiter: return "无效的帧定界符";
                case HdlcParseError.FrameIncomplete: return "帧数据不完整";
                case HdlcParseError.PayloadMismatch: return "Payload 长度不匹配";
                case HdlcParseError.CrcMismatch: return "CRC 校验失败";
                default: return "未知错误";
            }
        }

        /// <summary>
        /// 尝试从数据流中提取完整帧
        /// </summary>
        public bool TryExtractFrame(byte[] buffer, int length, out byte[] frame, out int consumed)
        {
            frame = null;
            consumed = 0;

            // 查找帧起始
            int startIndex = -1;
            for (int i = 0; i < length; i++)
            {
                if (buffer[i] == HDLC_FLAG)
                {
                    startIndex = i;
                    break;
                }
            }

            if (startIndex < 0)
                return false;

            // 查找帧结束
            for (int i = startIndex + 1; i < length; i++)
            {
                if (buffer[i] == HDLC_FLAG)
                {
                    // 找到完整帧
                    int frameLength = i - startIndex + 1;
                    frame = new byte[frameLength];
                    Array.Copy(buffer, startIndex, frame, 0, frameLength);
                    consumed = i + 1;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 写入转义字节
        /// 当 UseTranscode = false 时，直接写入原始字节（FDL2 模式）
        /// </summary>
        private void WriteEscaped(Stream stream, byte b)
        {
            // 关键修复: 检查转码标志
            if (UseTranscode && (b == HDLC_FLAG || b == HDLC_ESCAPE))
            {
                stream.WriteByte(HDLC_ESCAPE);
                stream.WriteByte((byte)(b ^ HDLC_ESCAPE_XOR));
            }
            else
            {
                stream.WriteByte(b);
            }
        }

        /// <summary>
        /// 计算 CRC-16-CCITT (Spreadtrum BROM 使用的算法)
        /// 使用标准多项式 0x1021 (MSB-first)，与开源 spd_dump 实现一致
        /// </summary>
        public ushort CalculateCRC16Ccitt(byte[] data)
        {
            // 参考 spd_dump.c 的 spd_crc16 实现
            // 多项式: 0x1021 (CCITT), MSB-first, 初始值 0
            uint crc = 0;
            
            foreach (byte b in data)
            {
                crc ^= (uint)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (crc << 1) ^ 0x11021;
                    else
                        crc <<= 1;
                }
            }

            return (ushort)(crc & 0xFFFF);
        }
        

        /// <summary>
        /// 计算 Spreadtrum 专有的 checksum (用于 FDL1 阶段之后的通信)
        /// 参考 sprdproto/sprd_io.c 的 calc_sprdcheck 实现
        /// </summary>
        public ushort CalculateSprdChecksum(byte[] data)
        {
            uint ctr = 0;
            int len = data.Length;
            int i = 0;

            // 每次处理 2 字节 (Little-Endian 读取)
            while (len > 1)
            {
                ctr += (uint)(data[i] | (data[i + 1] << 8));  // Little-Endian
                i += 2;
                len -= 2;
            }

            // 处理剩余的单字节
            if (len > 0)
                ctr += data[i];

            // 折叠到 16 位并取反
            ctr = (ctr >> 16) + (ctr & 0xFFFF);
            ctr = ~(ctr + (ctr >> 16)) & 0xFFFF;
            
            // 关键: 字节交换 (与 sprdproto 一致)
            return (ushort)((ctr >> 8) | ((ctr & 0xFF) << 8));
        }
        
        /// <summary>
        /// 验证 CRC (调试用)
        /// </summary>
        public bool VerifyCrc(byte[] data, ushort expectedCrc)
        {
            ushort calculated = CalculateCRC16Ccitt(data);
            _log?.Invoke(string.Format("[HDLC] CRC 验证: 计算=0x{0:X4}, 期望=0x{1:X4}", calculated, expectedCrc));
            return calculated == expectedCrc;
        }
        

        /// <summary>
        /// 格式化帧为十六进制字符串 (调试用)
        /// </summary>
        public static string FormatHex(byte[] data, int maxLength = 64)
        {
            if (data == null || data.Length == 0)
                return "(empty)";

            int displayLength = Math.Min(data.Length, maxLength);
            var hex = BitConverter.ToString(data, 0, displayLength).Replace("-", " ");
            
            if (data.Length > maxLength)
                hex += string.Format(" ... ({0} bytes total)", data.Length);

            return hex;
        }
    }

    /// <summary>
    /// HDLC 帧结构
    /// </summary>
    public class HdlcFrame
    {
        public byte Type { get; set; }
        public ushort Length { get; set; }
        public byte[] Payload { get; set; }
        public ushort Crc { get; set; }

        public override string ToString()
        {
            return string.Format("HdlcFrame[Type=0x{0:X2}, Length={1}, CRC=0x{2:X4}]", Type, Length, Crc);
        }
    }

    /// <summary>
    /// HDLC 解析错误类型
    /// </summary>
    public enum HdlcParseError
    {
        None,               // 无错误
        FrameTooShort,      // 帧数据太短
        InvalidDelimiter,   // 无效的帧定界符
        FrameIncomplete,    // 帧数据不完整
        PayloadMismatch,    // Payload 长度不匹配
        CrcMismatch         // CRC 校验失败
    }
}
