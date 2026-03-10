// ============================================================================
// SakuraEDL - LZMA Decoder | LZMA 解码器
// ============================================================================
// [ZH] LZMA 解码器 - 纯 C# 实现，基于 7-Zip LZMA SDK
// [EN] LZMA Decoder - Pure C# implementation based on 7-Zip LZMA SDK
// [JA] LZMAデコーダー - 7-Zip LZMA SDKベースの純粋なC#実装
// [KO] LZMA 디코더 - 7-Zip LZMA SDK 기반 순수 C# 구현
// [RU] Декодер LZMA - Чистая C# реализация на базе 7-Zip LZMA SDK
// [ES] Decodificador LZMA - Implementación C# pura basada en 7-Zip SDK
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.IO;

namespace SakuraEDL.Qualcomm.Common
{
    /// <summary>
    /// 纯 C# LZMA 解码器
    /// 支持 LZMA 和 LZMA2 格式解压
    /// </summary>
    public static class LzmaDecoder
    {
        #region Constants

        private const int kNumTopBits = 24;
        private const uint kTopValue = 1u << kNumTopBits;
        private const int kNumBitModelTotalBits = 11;
        private const int kBitModelTotal = 1 << kNumBitModelTotalBits;
        private const int kNumMoveBits = 5;

        private const int kNumPosBitsMax = 4;
        private const int kNumPosStatesMax = 1 << kNumPosBitsMax;

        private const int kLenNumLowBits = 3;
        private const int kLenNumLowSymbols = 1 << kLenNumLowBits;
        private const int kLenNumMidBits = 3;
        private const int kLenNumMidSymbols = 1 << kLenNumMidBits;
        private const int kLenNumHighBits = 8;
        private const int kLenNumHighSymbols = 1 << kLenNumHighBits;

        private const int kNumLitStates = 7;
        private const int kNumStates = 12;

        private const int kNumAlignBits = 4;
        private const int kStartPosModelIndex = 4;
        private const int kEndPosModelIndex = 14;
        private const int kNumPosModels = kEndPosModelIndex - kStartPosModelIndex;
        private const int kNumFullDistances = 1 << (kEndPosModelIndex >> 1);

        private const int kMatchMinLen = 2;

        private const uint kNumLenToPosStates = 4;
        private const uint kNumLenToPosStatesBits = 2;

        #endregion

        #region Public Methods

        /// <summary>
        /// 解压 LZMA 数据
        /// </summary>
        /// <param name="compressedData">压缩数据（包含5字节属性头）</param>
        /// <param name="uncompressedSize">解压后大小</param>
        /// <returns>解压后的数据</returns>
        public static byte[] Decompress(byte[] compressedData, long uncompressedSize)
        {
            if (compressedData == null || compressedData.Length < 5)
                return null;

            try
            {
                // 解析 LZMA 属性 (5 bytes)
                byte[] props = new byte[5];
                Array.Copy(compressedData, 0, props, 0, 5);

                byte[] actualData = new byte[compressedData.Length - 5];
                Array.Copy(compressedData, 5, actualData, 0, actualData.Length);

                return DecompressWithProps(actualData, props, uncompressedSize);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解压带属性的 LZMA 数据
        /// </summary>
        public static byte[] DecompressWithProps(byte[] compressedData, byte[] props, long uncompressedSize)
        {
            if (compressedData == null || props == null || props.Length < 5)
                return null;

            try
            {
                using (var inStream = new MemoryStream(compressedData))
                using (var outStream = new MemoryStream())
                {
                    var decoder = new LzmaDecoderInternal();
                    decoder.SetDecoderProperties(props);
                    decoder.Code(inStream, outStream, uncompressedSize);
                    return outStream.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解压 LZMA2 数据
        /// </summary>
        public static byte[] DecompressLzma2(byte[] compressedData, long uncompressedSize)
        {
            if (compressedData == null || compressedData.Length < 1)
                return null;

            try
            {
                // LZMA2 属性只有1字节
                byte dictSizeProp = compressedData[0];
                byte[] actualData = new byte[compressedData.Length - 1];
                Array.Copy(compressedData, 1, actualData, 0, actualData.Length);

                using (var inStream = new MemoryStream(actualData))
                using (var outStream = new MemoryStream())
                {
                    var decoder = new Lzma2DecoderInternal(dictSizeProp);
                    decoder.Code(inStream, outStream, uncompressedSize);
                    return outStream.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 检测并解压 LZMA/LZMA2 数据
        /// </summary>
        public static byte[] AutoDecompress(byte[] compressedData, long uncompressedSize)
        {
            if (compressedData == null || compressedData.Length < 5)
                return null;

            // 尝试 LZMA
            byte[] result = Decompress(compressedData, uncompressedSize);
            if (result != null && result.Length > 0)
                return result;

            // 尝试 LZMA2
            result = DecompressLzma2(compressedData, uncompressedSize);
            if (result != null && result.Length > 0)
                return result;

            return null;
        }

        #endregion

        #region Internal Decoder Class

        /// <summary>
        /// LZMA 解码器内部实现
        /// </summary>
        private class LzmaDecoderInternal
        {
            private uint m_PosStateMask;
            private ushort[] m_IsMatchDecoders = new ushort[kNumStates << kNumPosBitsMax];
            private ushort[] m_IsRepDecoders = new ushort[kNumStates];
            private ushort[] m_IsRepG0Decoders = new ushort[kNumStates];
            private ushort[] m_IsRepG1Decoders = new ushort[kNumStates];
            private ushort[] m_IsRepG2Decoders = new ushort[kNumStates];
            private ushort[] m_IsRep0LongDecoders = new ushort[kNumStates << kNumPosBitsMax];

            private BitTreeDecoder[] m_PosSlotDecoder = new BitTreeDecoder[kNumLenToPosStates];
            private ushort[] m_PosDecoders = new ushort[kNumFullDistances - kEndPosModelIndex];
            private BitTreeDecoder m_PosAlignDecoder = new BitTreeDecoder(kNumAlignBits);

            private LenDecoder m_LenDecoder = new LenDecoder();
            private LenDecoder m_RepLenDecoder = new LenDecoder();

            private LiteralDecoder m_LiteralDecoder = new LiteralDecoder();

            private uint m_DictionarySize;
            private OutWindow m_OutWindow = new OutWindow();
            private RangeDecoder m_RangeDecoder = new RangeDecoder();

            private uint m_DictionarySizeCheck;
            private int m_lc;
            private int m_lp;
            private int m_pb;

            public LzmaDecoderInternal()
            {
                for (uint i = 0; i < kNumLenToPosStates; i++)
                    m_PosSlotDecoder[i] = new BitTreeDecoder(6);
            }

            public void SetDecoderProperties(byte[] properties)
            {
                if (properties.Length < 5)
                    throw new ArgumentException("Invalid LZMA properties");

                int val = properties[0];
                if (val >= (9 * 5 * 5))
                    throw new ArgumentException("Invalid LZMA properties");

                m_lc = val % 9;
                val /= 9;
                m_lp = val % 5;
                m_pb = val / 5;
                m_PosStateMask = ((uint)1 << m_pb) - 1;

                m_DictionarySize = 0;
                for (int i = 0; i < 4; i++)
                    m_DictionarySize += (uint)properties[1 + i] << (i * 8);

                m_DictionarySizeCheck = Math.Max(m_DictionarySize, 1);
                m_LiteralDecoder.Create(m_lp, m_lc);

                uint numPosStates = (uint)1 << m_pb;
                m_LenDecoder.Create(numPosStates);
                m_RepLenDecoder.Create(numPosStates);
            }

            private void Init()
            {
                m_OutWindow.Init(false);

                InitBitModels(m_IsMatchDecoders);
                InitBitModels(m_IsRep0LongDecoders);
                InitBitModels(m_IsRepDecoders);
                InitBitModels(m_IsRepG0Decoders);
                InitBitModels(m_IsRepG1Decoders);
                InitBitModels(m_IsRepG2Decoders);
                InitBitModels(m_PosDecoders);

                m_LiteralDecoder.Init();

                for (uint i = 0; i < kNumLenToPosStates; i++)
                    m_PosSlotDecoder[i].Init();

                m_LenDecoder.Init();
                m_RepLenDecoder.Init();
                m_PosAlignDecoder.Init();

                m_RangeDecoder.Init();
            }

            private static void InitBitModels(ushort[] probs)
            {
                for (int i = 0; i < probs.Length; i++)
                    probs[i] = kBitModelTotal >> 1;
            }

            public void Code(Stream inStream, Stream outStream, long uncompressedSize)
            {
                uint blockSize = Math.Max(m_DictionarySizeCheck, 1 << 12);
                m_OutWindow.Create(blockSize);
                m_OutWindow.SetStream(outStream);

                m_RangeDecoder.SetStream(inStream);

                Init();

                uint state = 0;
                uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
                long nowPos64 = 0;

                while (nowPos64 < uncompressedSize)
                {
                    uint posState = (uint)nowPos64 & m_PosStateMask;

                    if (m_RangeDecoder.DecodeBit(m_IsMatchDecoders, (state << kNumPosBitsMax) + posState) == 0)
                    {
                        byte prevByte = m_OutWindow.GetByte(0);
                        byte b;
                        if (state < kNumLitStates)
                            b = m_LiteralDecoder.DecodeNormal(m_RangeDecoder, (uint)nowPos64, prevByte);
                        else
                            b = m_LiteralDecoder.DecodeWithMatchByte(m_RangeDecoder, (uint)nowPos64, prevByte, m_OutWindow.GetByte(rep0));
                        m_OutWindow.PutByte(b);
                        state = GetLiteralState(state);
                        nowPos64++;
                    }
                    else
                    {
                        uint len;
                        if (m_RangeDecoder.DecodeBit(m_IsRepDecoders, state) == 1)
                        {
                            if (m_RangeDecoder.DecodeBit(m_IsRepG0Decoders, state) == 0)
                            {
                                if (m_RangeDecoder.DecodeBit(m_IsRep0LongDecoders, (state << kNumPosBitsMax) + posState) == 0)
                                {
                                    state = GetShortRepState(state);
                                    m_OutWindow.PutByte(m_OutWindow.GetByte(rep0));
                                    nowPos64++;
                                    continue;
                                }
                            }
                            else
                            {
                                uint distance;
                                if (m_RangeDecoder.DecodeBit(m_IsRepG1Decoders, state) == 0)
                                    distance = rep1;
                                else
                                {
                                    if (m_RangeDecoder.DecodeBit(m_IsRepG2Decoders, state) == 0)
                                        distance = rep2;
                                    else
                                    {
                                        distance = rep3;
                                        rep3 = rep2;
                                    }
                                    rep2 = rep1;
                                }
                                rep1 = rep0;
                                rep0 = distance;
                            }
                            len = m_RepLenDecoder.Decode(m_RangeDecoder, posState) + kMatchMinLen;
                            state = GetRepState(state);
                        }
                        else
                        {
                            rep3 = rep2;
                            rep2 = rep1;
                            rep1 = rep0;
                            len = kMatchMinLen + m_LenDecoder.Decode(m_RangeDecoder, posState);
                            state = GetMatchState(state);
                            uint posSlot = m_PosSlotDecoder[GetLenToPosState(len)].Decode(m_RangeDecoder);
                            if (posSlot >= kStartPosModelIndex)
                            {
                                int numDirectBits = (int)((posSlot >> 1) - 1);
                                rep0 = (2 | (posSlot & 1)) << numDirectBits;
                                if (posSlot < kEndPosModelIndex)
                                    rep0 += BitTreeDecoder.ReverseDecode(m_PosDecoders, rep0 - posSlot - 1, m_RangeDecoder, numDirectBits);
                                else
                                {
                                    rep0 += m_RangeDecoder.DecodeDirectBits(numDirectBits - kNumAlignBits) << kNumAlignBits;
                                    rep0 += m_PosAlignDecoder.ReverseDecode(m_RangeDecoder);
                                }
                            }
                            else
                                rep0 = posSlot;
                        }

                        if (rep0 >= nowPos64 || rep0 >= m_DictionarySizeCheck)
                        {
                            if (rep0 == 0xFFFFFFFF)
                                break;
                            throw new Exception("LZMA data error");
                        }

                        m_OutWindow.CopyBlock(rep0, len);
                        nowPos64 += len;
                    }
                }

                m_OutWindow.Flush();
            }

            private static uint GetLiteralState(uint state) => state < 4 ? (uint)0 : (state < 10 ? state - 3 : state - 6);
            private static uint GetMatchState(uint state) => state < 7 ? (uint)7 : (uint)10;
            private static uint GetRepState(uint state) => state < 7 ? (uint)8 : (uint)11;
            private static uint GetShortRepState(uint state) => state < 7 ? (uint)9 : (uint)11;
            private static uint GetLenToPosState(uint len) => Math.Min(len - kMatchMinLen, kNumLenToPosStates - 1);
        }

        #endregion

        #region LZMA2 Decoder

        /// <summary>
        /// LZMA2 解码器内部实现
        /// </summary>
        private class Lzma2DecoderInternal
        {
            private LzmaDecoderInternal _lzmaDecoder;
            private uint _dictionarySize;

            public Lzma2DecoderInternal(byte dictSizeProp)
            {
                if (dictSizeProp > 40)
                    throw new ArgumentException("Invalid LZMA2 dictionary size");

                _dictionarySize = dictSizeProp == 40 ? 0xFFFFFFFF : (uint)((2u | (dictSizeProp & 1)) << (dictSizeProp / 2 + 11));
            }

            public void Code(Stream inStream, Stream outStream, long uncompressedSize)
            {
                using (var ms = new MemoryStream())
                {
                    while (true)
                    {
                        int control = inStream.ReadByte();
                        if (control == -1 || control == 0)
                            break;

                        if (control == 1) // 未压缩重置
                        {
                            int highSize = inStream.ReadByte();
                            int lowSize = inStream.ReadByte();
                            int size = ((highSize << 8) | lowSize) + 1;

                            byte[] buf = new byte[size];
                            inStream.Read(buf, 0, size);
                            outStream.Write(buf, 0, size);
                        }
                        else if (control == 2) // 未压缩
                        {
                            int highSize = inStream.ReadByte();
                            int lowSize = inStream.ReadByte();
                            int size = ((highSize << 8) | lowSize) + 1;

                            byte[] buf = new byte[size];
                            inStream.Read(buf, 0, size);
                            outStream.Write(buf, 0, size);
                        }
                        else if (control >= 0x80) // LZMA 块
                        {
                            bool resetDic = (control & 0x20) != 0;
                            bool resetState = (control & 0x40) != 0;
                            bool newProps = (control & 0x40) != 0;

                            int unpackHigh = (control & 0x1F);
                            int unpackMid = inStream.ReadByte();
                            int unpackLow = inStream.ReadByte();
                            int unpackSize = (unpackHigh << 16) | (unpackMid << 8) | unpackLow;
                            unpackSize++;

                            int packHigh = inStream.ReadByte();
                            int packLow = inStream.ReadByte();
                            int packSize = (packHigh << 8) | packLow;
                            packSize++;

                            byte[] props = null;
                            if (newProps)
                            {
                                props = new byte[5];
                                props[0] = (byte)inStream.ReadByte();
                                // 构造完整的5字节属性
                                props[1] = (byte)(_dictionarySize & 0xFF);
                                props[2] = (byte)((_dictionarySize >> 8) & 0xFF);
                                props[3] = (byte)((_dictionarySize >> 16) & 0xFF);
                                props[4] = (byte)((_dictionarySize >> 24) & 0xFF);

                                if (_lzmaDecoder == null)
                                    _lzmaDecoder = new LzmaDecoderInternal();
                                _lzmaDecoder.SetDecoderProperties(props);
                            }

                            byte[] packedData = new byte[packSize];
                            inStream.Read(packedData, 0, packSize);

                            using (var compressedStream = new MemoryStream(packedData))
                            {
                                _lzmaDecoder.Code(compressedStream, outStream, unpackSize);
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region Helper Classes

        private class OutWindow
        {
            private byte[] _buffer;
            private uint _pos;
            private uint _windowSize;
            private uint _streamPos;
            private Stream _stream;

            public void Create(uint windowSize)
            {
                if (_windowSize != windowSize)
                    _buffer = new byte[windowSize];
                _windowSize = windowSize;
                _pos = 0;
                _streamPos = 0;
            }

            public void SetStream(Stream stream) => _stream = stream;

            public void Init(bool solid)
            {
                if (!solid)
                {
                    _streamPos = 0;
                    _pos = 0;
                }
            }

            public void Flush()
            {
                uint size = _pos - _streamPos;
                if (size > 0)
                {
                    _stream.Write(_buffer, (int)_streamPos, (int)size);
                    if (_pos >= _windowSize)
                        _pos = 0;
                    _streamPos = _pos;
                }
            }

            public void PutByte(byte b)
            {
                _buffer[_pos++] = b;
                if (_pos >= _windowSize)
                    Flush();
            }

            public byte GetByte(uint distance)
            {
                uint pos = _pos - distance - 1;
                if (pos >= _windowSize)
                    pos += _windowSize;
                return _buffer[pos];
            }

            public void CopyBlock(uint distance, uint len)
            {
                uint pos = _pos - distance - 1;
                if (pos >= _windowSize)
                    pos += _windowSize;

                for (uint i = 0; i < len; i++)
                {
                    if (pos >= _windowSize)
                        pos = 0;
                    _buffer[_pos++] = _buffer[pos++];
                    if (_pos >= _windowSize)
                        Flush();
                }
            }
        }

        private class RangeDecoder
        {
            public const uint kTopMask = ~((1u << 24) - 1);

            private Stream _stream;
            private uint _code;
            private uint _range;

            public void SetStream(Stream stream) => _stream = stream;

            public void Init()
            {
                _code = 0;
                _range = 0xFFFFFFFF;
                for (int i = 0; i < 5; i++)
                    _code = (_code << 8) | (byte)_stream.ReadByte();
            }

            public void Normalize()
            {
                while (_range < kTopMask)
                {
                    _code = (_code << 8) | (byte)_stream.ReadByte();
                    _range <<= 8;
                }
            }

            public uint DecodeBit(ushort[] probs, uint index)
            {
                uint prob = probs[index];
                uint newBound = (_range >> kNumBitModelTotalBits) * prob;
                if (_code < newBound)
                {
                    _range = newBound;
                    probs[index] = (ushort)(prob + ((kBitModelTotal - prob) >> kNumMoveBits));
                    Normalize();
                    return 0;
                }
                else
                {
                    _range -= newBound;
                    _code -= newBound;
                    probs[index] = (ushort)(prob - (prob >> kNumMoveBits));
                    Normalize();
                    return 1;
                }
            }

            public uint DecodeDirectBits(int numTotalBits)
            {
                uint result = 0;
                for (int i = numTotalBits; i > 0; i--)
                {
                    _range >>= 1;
                    uint t = (_code - _range) >> 31;
                    _code -= _range & (t - 1);
                    result = (result << 1) | (1 - t);
                    Normalize();
                }
                return result;
            }
        }

        private class BitTreeDecoder
        {
            private ushort[] _models;
            private int _numBitLevels;

            public BitTreeDecoder(int numBitLevels)
            {
                _numBitLevels = numBitLevels;
                _models = new ushort[1 << numBitLevels];
            }

            public void Init()
            {
                for (int i = 0; i < _models.Length; i++)
                    _models[i] = kBitModelTotal >> 1;
            }

            public uint Decode(RangeDecoder rangeDecoder)
            {
                uint m = 1;
                for (int bitIndex = _numBitLevels; bitIndex > 0; bitIndex--)
                    m = (m << 1) + rangeDecoder.DecodeBit(_models, m);
                return m - (1u << _numBitLevels);
            }

            public uint ReverseDecode(RangeDecoder rangeDecoder)
            {
                uint m = 1;
                uint symbol = 0;
                for (int bitIndex = 0; bitIndex < _numBitLevels; bitIndex++)
                {
                    uint bit = rangeDecoder.DecodeBit(_models, m);
                    m = (m << 1) + bit;
                    symbol |= bit << bitIndex;
                }
                return symbol;
            }

            public static uint ReverseDecode(ushort[] models, uint startIndex, RangeDecoder rangeDecoder, int numBitLevels)
            {
                uint m = 1;
                uint symbol = 0;
                for (int bitIndex = 0; bitIndex < numBitLevels; bitIndex++)
                {
                    uint bit = rangeDecoder.DecodeBit(models, startIndex + m);
                    m = (m << 1) + bit;
                    symbol |= bit << bitIndex;
                }
                return symbol;
            }
        }

        private class LenDecoder
        {
            private ushort _choice;
            private ushort _choice2;
            private BitTreeDecoder[] _lowCoder = new BitTreeDecoder[kNumPosStatesMax];
            private BitTreeDecoder[] _midCoder = new BitTreeDecoder[kNumPosStatesMax];
            private BitTreeDecoder _highCoder = new BitTreeDecoder(kLenNumHighBits);

            public void Create(uint numPosStates)
            {
                for (uint posState = 0; posState < numPosStates; posState++)
                {
                    _lowCoder[posState] = new BitTreeDecoder(kLenNumLowBits);
                    _midCoder[posState] = new BitTreeDecoder(kLenNumMidBits);
                }
            }

            public void Init()
            {
                _choice = kBitModelTotal >> 1;
                _choice2 = kBitModelTotal >> 1;
                for (uint posState = 0; posState < _lowCoder.Length && _lowCoder[posState] != null; posState++)
                {
                    _lowCoder[posState].Init();
                    _midCoder[posState].Init();
                }
                _highCoder.Init();
            }

            public uint Decode(RangeDecoder rangeDecoder, uint posState)
            {
                ushort[] choice = new ushort[1] { _choice };
                if (rangeDecoder.DecodeBit(choice, 0) == 0)
                {
                    _choice = choice[0];
                    return _lowCoder[posState].Decode(rangeDecoder);
                }
                _choice = choice[0];

                ushort[] choice2 = new ushort[1] { _choice2 };
                if (rangeDecoder.DecodeBit(choice2, 0) == 0)
                {
                    _choice2 = choice2[0];
                    return kLenNumLowSymbols + _midCoder[posState].Decode(rangeDecoder);
                }
                _choice2 = choice2[0];
                return kLenNumLowSymbols + kLenNumMidSymbols + _highCoder.Decode(rangeDecoder);
            }
        }

        private class LiteralDecoder
        {
            private struct Decoder
            {
                public ushort[] Decoders;

                public void Create() => Decoders = new ushort[0x300];

                public void Init()
                {
                    for (int i = 0; i < 0x300; i++)
                        Decoders[i] = kBitModelTotal >> 1;
                }

                public byte DecodeNormal(RangeDecoder rangeDecoder)
                {
                    uint symbol = 1;
                    do
                        symbol = (symbol << 1) | rangeDecoder.DecodeBit(Decoders, symbol);
                    while (symbol < 0x100);
                    return (byte)symbol;
                }

                public byte DecodeWithMatchByte(RangeDecoder rangeDecoder, byte matchByte)
                {
                    uint symbol = 1;
                    do
                    {
                        uint matchBit = (uint)(matchByte >> 7) & 1;
                        matchByte <<= 1;
                        uint bit = rangeDecoder.DecodeBit(Decoders, ((1 + matchBit) << 8) + symbol);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                        {
                            while (symbol < 0x100)
                                symbol = (symbol << 1) | rangeDecoder.DecodeBit(Decoders, symbol);
                            break;
                        }
                    }
                    while (symbol < 0x100);
                    return (byte)symbol;
                }
            }

            private Decoder[] _coders;
            private int _numPrevBits;
            private int _numPosBits;
            private uint _posMask;

            public void Create(int numPosBits, int numPrevBits)
            {
                if (_coders != null && _numPrevBits == numPrevBits && _numPosBits == numPosBits)
                    return;
                _numPosBits = numPosBits;
                _posMask = ((uint)1 << numPosBits) - 1;
                _numPrevBits = numPrevBits;
                uint numStates = (uint)1 << (_numPrevBits + _numPosBits);
                _coders = new Decoder[numStates];
                for (uint i = 0; i < numStates; i++)
                    _coders[i].Create();
            }

            public void Init()
            {
                uint numStates = (uint)1 << (_numPrevBits + _numPosBits);
                for (uint i = 0; i < numStates; i++)
                    _coders[i].Init();
            }

            private uint GetState(uint pos, byte prevByte)
            {
                return ((pos & _posMask) << _numPrevBits) + (uint)(prevByte >> (8 - _numPrevBits));
            }

            public byte DecodeNormal(RangeDecoder rangeDecoder, uint pos, byte prevByte)
            {
                return _coders[GetState(pos, prevByte)].DecodeNormal(rangeDecoder);
            }

            public byte DecodeWithMatchByte(RangeDecoder rangeDecoder, uint pos, byte prevByte, byte matchByte)
            {
                return _coders[GetState(pos, prevByte)].DecodeWithMatchByte(rangeDecoder, matchByte);
            }
        }

        #endregion
    }
}
