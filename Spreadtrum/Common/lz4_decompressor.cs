// ============================================================================
// SakuraEDL - LZ4 Decompressor | LZ4 解压器
// ============================================================================
// [ZH] LZ4 解压器 - 解压 Boot 镜像中的 LZ4 压缩数据
// [EN] LZ4 Decompressor - Decompress LZ4 compressed data in Boot image
// [JA] LZ4解凍器 - Bootイメージ内のLZ4圧縮データを解凍
// [KO] LZ4 압축 해제기 - Boot 이미지의 LZ4 압축 데이터 해제
// [RU] Декомпрессор LZ4 - Распаковка сжатых данных LZ4 в Boot образе
// [ES] Descompresor LZ4 - Descomprimir datos LZ4 en imagen Boot
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.IO;

namespace SakuraEDL.Spreadtrum.Common
{
    /// <summary>
    /// LZ4 解压器 - 用于解压 Android Boot 镜像中的 ramdisk
    /// 支持 LZ4 Legacy 和 LZ4 Frame 格式
    /// </summary>
    public static class Lz4Decompressor
    {
        // LZ4 魔数
        private const uint LZ4_LEGACY_MAGIC = 0x184C2102;  // Legacy format
        private const uint LZ4_FRAME_MAGIC = 0x184D2204;   // Frame format

        /// <summary>
        /// 检测数据是否为 LZ4 压缩
        /// </summary>
        public static bool IsLz4Compressed(byte[] data)
        {
            if (data == null || data.Length < 4)
                return false;

            uint magic = BitConverter.ToUInt32(data, 0);
            return magic == LZ4_LEGACY_MAGIC || magic == LZ4_FRAME_MAGIC;
        }

        /// <summary>
        /// 解压 LZ4 数据
        /// </summary>
        /// <param name="input">压缩数据</param>
        /// <param name="output">输出缓冲区 (需预分配足够空间)</param>
        /// <returns>解压后的实际大小，-1 表示失败</returns>
        public static int Decompress(byte[] input, ref byte[] output)
        {
            if (input == null || input.Length < 4)
                return -1;

            uint magic = BitConverter.ToUInt32(input, 0);

            if (magic == LZ4_LEGACY_MAGIC)
            {
                return DecompressLegacy(input, ref output);
            }
            else if (magic == LZ4_FRAME_MAGIC)
            {
                return DecompressFrame(input, ref output);
            }
            else
            {
                // 尝试作为原始 LZ4 块解压
                return DecompressRawBlock(input, ref output);
            }
        }

        /// <summary>
        /// 解压 LZ4 Legacy 格式
        /// 格式: [Magic(4)] + [Block(BlockSize(4) + CompressedData)]...
        /// </summary>
        private static int DecompressLegacy(byte[] input, ref byte[] output)
        {
            int inputOffset = 4;  // 跳过 magic
            int outputOffset = 0;

            while (inputOffset < input.Length - 4)
            {
                // 读取块大小 (little-endian)
                int blockSize = BitConverter.ToInt32(input, inputOffset);
                inputOffset += 4;

                if (blockSize <= 0 || inputOffset + blockSize > input.Length)
                    break;

                // 解压块
                int decompressedSize = DecompressBlock(
                    input, inputOffset, blockSize,
                    output, outputOffset, output.Length - outputOffset);

                if (decompressedSize < 0)
                    return -1;

                inputOffset += blockSize;
                outputOffset += decompressedSize;
            }

            return outputOffset;
        }

        /// <summary>
        /// 解压 LZ4 Frame 格式 (LZ4F)
        /// </summary>
        private static int DecompressFrame(byte[] input, ref byte[] output)
        {
            int inputOffset = 4;  // 跳过 magic
            int outputOffset = 0;

            // 解析帧描述符
            if (inputOffset >= input.Length)
                return -1;

            byte flg = input[inputOffset++];
            byte bd = input[inputOffset++];

            bool hasContentSize = (flg & 0x08) != 0;
            bool hasBlockChecksum = (flg & 0x10) != 0;
            bool hasContentChecksum = (flg & 0x04) != 0;
            int maxBlockSize = GetMaxBlockSize(bd);

            // 跳过内容大小 (如果存在)
            if (hasContentSize)
            {
                inputOffset += 8;
            }

            // 跳过头部校验和
            inputOffset += 1;

            // 解压数据块
            while (inputOffset < input.Length - 4)
            {
                // 读取块大小
                uint blockHeader = BitConverter.ToUInt32(input, inputOffset);
                inputOffset += 4;

                if (blockHeader == 0)  // End mark
                    break;

                bool isUncompressed = (blockHeader & 0x80000000) != 0;
                int blockSize = (int)(blockHeader & 0x7FFFFFFF);

                if (blockSize <= 0 || inputOffset + blockSize > input.Length)
                    break;

                if (isUncompressed)
                {
                    // 未压缩块，直接复制
                    Array.Copy(input, inputOffset, output, outputOffset, blockSize);
                    outputOffset += blockSize;
                }
                else
                {
                    // 压缩块
                    int decompressedSize = DecompressBlock(
                        input, inputOffset, blockSize,
                        output, outputOffset, output.Length - outputOffset);

                    if (decompressedSize < 0)
                        return -1;

                    outputOffset += decompressedSize;
                }

                inputOffset += blockSize;

                // 跳过块校验和 (如果存在)
                if (hasBlockChecksum)
                    inputOffset += 4;
            }

            return outputOffset;
        }

        /// <summary>
        /// 解压原始 LZ4 块
        /// </summary>
        private static int DecompressRawBlock(byte[] input, ref byte[] output)
        {
            return DecompressBlock(input, 0, input.Length, output, 0, output.Length);
        }

        /// <summary>
        /// 解压单个 LZ4 块
        /// LZ4 块格式: [Token(1)] + [Literal Length(0-n)] + [Literals] + [Offset(2)] + [Match Length(0-n)]
        /// </summary>
        private static int DecompressBlock(
            byte[] input, int inputOffset, int inputLength,
            byte[] output, int outputOffset, int outputLength)
        {
            int ip = inputOffset;
            int ipEnd = inputOffset + inputLength;
            int op = outputOffset;
            int opEnd = outputOffset + outputLength;

            while (ip < ipEnd)
            {
                // 读取 token
                byte token = input[ip++];
                int literalLength = token >> 4;
                int matchLength = token & 0x0F;

                // 读取额外的 literal 长度
                if (literalLength == 15)
                {
                    byte s;
                    do
                    {
                        if (ip >= ipEnd) return -1;
                        s = input[ip++];
                        literalLength += s;
                    } while (s == 255);
                }

                // 复制 literals
                if (literalLength > 0)
                {
                    if (ip + literalLength > ipEnd || op + literalLength > opEnd)
                        return -1;

                    Array.Copy(input, ip, output, op, literalLength);
                    ip += literalLength;
                    op += literalLength;
                }

                // 检查是否到达末尾
                if (ip >= ipEnd)
                    break;

                // 读取偏移量 (little-endian, 2 bytes)
                if (ip + 2 > ipEnd)
                    return -1;

                int offset = input[ip] | (input[ip + 1] << 8);
                ip += 2;

                if (offset == 0)
                    return -1;  // 无效偏移

                // 计算匹配位置
                int matchPos = op - offset;
                if (matchPos < outputOffset)
                    return -1;  // 偏移超出范围

                // 读取额外的 match 长度
                matchLength += 4;  // 最小匹配长度为 4
                if ((token & 0x0F) == 15)
                {
                    byte s;
                    do
                    {
                        if (ip >= ipEnd) return -1;
                        s = input[ip++];
                        matchLength += s;
                    } while (s == 255);
                }

                // 复制匹配数据 (可能有重叠)
                if (op + matchLength > opEnd)
                    return -1;

                for (int i = 0; i < matchLength; i++)
                {
                    output[op++] = output[matchPos++];
                }
            }

            return op - outputOffset;
        }

        /// <summary>
        /// 根据 BD 字节获取最大块大小
        /// </summary>
        private static int GetMaxBlockSize(byte bd)
        {
            int blockSizeId = (bd >> 4) & 0x07;
            switch (blockSizeId)
            {
                case 4: return 64 * 1024;      // 64 KB
                case 5: return 256 * 1024;     // 256 KB
                case 6: return 1024 * 1024;    // 1 MB
                case 7: return 4 * 1024 * 1024; // 4 MB
                default: return 64 * 1024;
            }
        }

        /// <summary>
        /// 解压到新缓冲区 (自动分配)
        /// </summary>
        public static byte[] Decompress(byte[] input, int maxOutputSize = 50 * 1024 * 1024)
        {
            byte[] output = new byte[maxOutputSize];
            int size = Decompress(input, ref output);

            if (size <= 0)
                return null;

            // 裁剪到实际大小
            byte[] result = new byte[size];
            Array.Copy(output, result, size);
            return result;
        }
    }
}
