// mtkclient port: Hardware/hwcrypto.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using SakuraEDL.MediaTek.Config;

namespace SakuraEDL.MediaTek.Hardware
{
    /// <summary>
    /// Hardware crypto setup parameters.
    /// mtkclient: CryptoSetup
    /// </summary>
    public class CryptoSetup
    {
        public ushort HwCode { get; set; }
        public uint DxccBase { get; set; }
        public uint GcpuBase { get; set; }
        public uint DaPayloadAddr { get; set; }
        public uint SejBase { get; set; }
        public uint CqdmaBase { get; set; }
        public uint ApDmaMem { get; set; }
        public uint MeidAddr { get; set; }
        public uint SocIdAddr { get; set; }
        public uint ProvAddr { get; set; }
        public uint EfuseBase { get; set; }
        public (uint, uint)[] Blacklist { get; set; }

        // Memory access delegates
        public Func<uint, uint, uint> Read32 { get; set; }
        public Action<uint, uint> Write32 { get; set; }
        public Action<uint, byte[]> WriteMem { get; set; }

        public static CryptoSetup FromChipConfig(ChipConfig chip, Func<uint, uint, uint> read32,
                                                   Action<uint, uint> write32, Action<uint, byte[]> writeMem)
        {
            return new CryptoSetup
            {
                HwCode = (ushort)chip.DaCode,
                DxccBase = chip.DxccBase,
                GcpuBase = chip.GcpuBase,
                DaPayloadAddr = chip.DaPayloadAddr,
                SejBase = chip.SejBase,
                CqdmaBase = chip.CqdmaBase,
                ApDmaMem = chip.ApDmaMem,
                MeidAddr = chip.MeidAddr,
                SocIdAddr = chip.SocIdAddr,
                ProvAddr = chip.ProvAddr,
                EfuseBase = chip.EfuseAddr,
                Blacklist = chip.Blacklist,
                Read32 = read32,
                Write32 = write32,
                WriteMem = writeMem
            };
        }
    }

    /// <summary>
    /// Unified hardware crypto interface — ported from mtkclient/Library/Hardware/hwcrypto.py
    /// Wraps DXCC, GCPU, SEJ, and CQ-DMA engines.
    /// </summary>
    public class HwCrypto
    {
        private readonly CryptoSetup _setup;
        private readonly Action<string> _info;
        private readonly Action<string> _error;

        public Dxcc Dxcc { get; private set; }
        public GCpu GCpu { get; private set; }
        public Sej Sej { get; private set; }
        public Cqdma Cqdma { get; private set; }

        // Alias properties used by exploit classes
        public GCpu GcpuEngine => GCpu;
        public Cqdma CqdmaEngine => Cqdma;

        public HwCrypto(CryptoSetup setup, Action<string> info = null, Action<string> error = null)
        {
            _setup = setup;
            _info = info ?? delegate { };
            _error = error ?? delegate { };

            Dxcc = new Dxcc(setup, info, error);
            GCpu = new GCpu(setup, info, error);
            Sej = new Sej(setup, info, error);
            Cqdma = new Cqdma(setup, info, error);
        }

        /// <summary>
        /// Disable BROM range blacklist using the specified engine type.
        /// mtkclient: disable_range_blacklist(btype, mtk)
        /// </summary>
        public void DisableRangeBlacklist(string btype = "cqdma")
        {
            if (btype == "cqdma")
                Cqdma.DisableRangeBlacklist();
            else if (btype == "gcpu")
            {
                // GCPU-based blacklist disable uses CQ-DMA under the hood
                Cqdma.DisableRangeBlacklist();
            }
            else if (btype == "kamakiri")
            {
                Cqdma.DisableRangeBlacklist();
            }
        }

        /// <summary>
        /// Decrypt MTEE image via GCPU.
        /// mtkclient: mtee(data, keyseed, ivseed, aeskey1, aeskey2)
        /// </summary>
        public byte[] DecryptMtee(byte[] data, byte[] keySeed, byte[] ivSeed, byte[] aesKey1, byte[] aesKey2)
        {
            GCpu.Init();
            GCpu.Acquire();
            return GCpu.DecryptMteeImg(data, keySeed, ivSeed, aesKey1, aesKey2);
        }

        /// <summary>
        /// AES hardware encrypt/decrypt.
        /// mtkclient: aes_hwcrypt(data, iv, encrypt, otp, mode, btype, key_sz)
        /// </summary>
        public byte[] AesHwCrypt(byte[] data, byte[] iv = null, bool encrypt = true,
                                  byte[] otp = null, string mode = "cbc", string btype = "sej")
        {
            if (otp == null)
                otp = new byte[32];

            if (btype == "sej")
            {
                if (mode == "cbc")
                    return Sej.HwAes128CbcEncrypt(data, encrypt);
                if (mode == "rpmb")
                    return Sej.GenerateRpmb(data, otp);
                if (mode == "mtee")
                    return Sej.GenerateMtee(otp);
            }
            else if (btype == "gcpu")
            {
                if (mode == "ecb")
                    return GCpu.AesReadEcb(data, encrypt);
                if (mode == "cbc")
                {
                    if (GCpu.AesSetupCbc(_setup.DaPayloadAddr, data, iv, encrypt))
                        return GCpu.AesReadCbc(_setup.DaPayloadAddr, encrypt);
                }
            }
            else if (btype == "dxcc")
            {
                if (mode == "fde")
                    return Dxcc.GenerateRpmb(1);
                if (mode == "rpmb2")
                    return Dxcc.GenerateRpmb(2);
                if (mode == "rpmb")
                    return Dxcc.GenerateRpmb();
            }

            _error($"Unsupported crypto: btype={btype}, mode={mode}");
            return null;
        }
    }

    /// <summary>
    /// DXCC (ARM TrustZone CryptoCell) engine — register-level implementation.
    /// Ported from mtkclient/Library/Hardware/hwcrypto_dxcc.py
    /// Handles RPMB key derivation, provision keys, OTP reads via CryptoCell descriptors.
    /// </summary>
    public class Dxcc
    {
        private readonly CryptoSetup _setup;
        private readonly Action<string> _info;
        private readonly Action<string> _error;

        // Register offsets
        private const uint DX_HOST_IRR = 0xA00;
        private const uint DX_HOST_ICR = 0xA08;
        private const uint DX_DSCRPTR_QUEUE0_WORD0 = 0xE80;
        private const uint DX_DSCRPTR_QUEUE0_WORD1 = 0xE84;
        private const uint DX_DSCRPTR_QUEUE0_WORD2 = 0xE88;
        private const uint DX_DSCRPTR_QUEUE0_WORD3 = 0xE8C;
        private const uint DX_DSCRPTR_QUEUE0_WORD4 = 0xE90;
        private const uint DX_DSCRPTR_QUEUE0_WORD5 = 0xE94;
        private const uint DX_DSCRPTR_QUEUE0_CONTENT = 0xE9C;
        private const uint DX_HOST_SEP_HOST_GPR4 = 0xAA0;

        // Crypto constants
        private const int AES_IV_SIZE = 16;
        private const int AES_KEY128_SIZE = 16;

        // HwCryptoKey types
        private const int ROOT_KEY = 0;
        private const int PROVISIONING_KEY = 3;
        private const int PLATFORM_KEY = 2;
        private const int USER_KEY = 5;

        // SASI key hash indices
        private const int SASI_SB_HASH_BOOT_KEY_256B = 0;

        private uint _dxccBase;
        private uint _daPayloadAddr;
        private ushort _hwCode;

        public Dxcc(CryptoSetup setup, Action<string> info, Action<string> error)
        {
            _setup = setup;
            _info = info ?? delegate { };
            _error = error ?? delegate { };
            _dxccBase = setup.DxccBase;
            _hwCode = setup.HwCode;
            _daPayloadAddr = 0x200000; // Default DA payload address
        }

        public void SetDaPayloadAddr(uint addr) { _daPayloadAddr = addr; }

        #region Low-level register access

        private uint Read32(uint addr) => _setup.Read32(addr, 1);
        private void Write32(uint addr, uint val) => _setup.Write32(addr, val);
        private void WriteMem(uint addr, byte[] data) => _setup.WriteMem?.Invoke(addr, data);

        private uint[] Read32Array(uint addr, int count)
        {
            uint[] result = new uint[count];
            for (int i = 0; i < count; i++)
                result[i] = Read32(addr + (uint)(i * 4));
            return result;
        }

        #endregion

        #region Descriptor Queue

        private void SbHalClearInterruptBit() => Write32(_dxccBase + DX_HOST_ICR, 4);

        private uint SbCryptoWait()
        {
            for (int i = 0; i < 100000; i++)
            {
                uint val = Read32(_dxccBase + DX_HOST_IRR);
                if (val != 0) return val;
            }
            return 0;
        }

        private void AddDescSequence(uint[] desc)
        {
            // Wait for queue space
            for (int i = 0; i < 10000; i++)
            {
                if ((Read32(_dxccBase + DX_DSCRPTR_QUEUE0_CONTENT) << 0x1C) != 0)
                    break;
            }
            Write32(_dxccBase + DX_DSCRPTR_QUEUE0_WORD0, desc[0]);
            Write32(_dxccBase + DX_DSCRPTR_QUEUE0_WORD1, desc[1]);
            Write32(_dxccBase + DX_DSCRPTR_QUEUE0_WORD2, desc[2]);
            Write32(_dxccBase + DX_DSCRPTR_QUEUE0_WORD3, desc[3]);
            Write32(_dxccBase + DX_DSCRPTR_QUEUE0_WORD4, desc[4]);
            Write32(_dxccBase + DX_DSCRPTR_QUEUE0_WORD5, desc[5]);
        }

        private void SbHalInit() => SbHalClearInterruptBit();

        private uint SbHalWaitDescCompletion(uint destPtr = 0)
        {
            SbHalClearInterruptBit();
            uint[] data = new uint[6];
            data[0] = 0;
            data[1] = 0x8000011;
            data[2] = destPtr;
            data[3] = 0x8000012;
            data[4] = 0x100;
            data[5] = 0;
            AddDescSequence(data);

            for (int i = 0; i < 100000; i++)
            {
                if ((SbCryptoWait() & 4) != 0) break;
            }

            for (int i = 0; i < 100000; i++)
            {
                uint val = Read32(_dxccBase + 0xBA0);
                if (val != 0) return val == 1 ? 0u : 0xF6000001;
            }
            return 0xF6000001;
        }

        #endregion

        #region AES CMAC Driver

        private bool AesCmacDriver(int aesKeyType, uint pInternalKey, uint pDataIn,
                                    int dmaMode, int blockSize, uint pDataOut)
        {
            int keySizeBytes;
            if (aesKeyType == ROOT_KEY)
            {
                keySizeBytes = ((Read32(_dxccBase + DX_HOST_SEP_HOST_GPR4) >> 1) & 1) == 1
                    ? 0x20 : 0x10;
            }
            else
            {
                keySizeBytes = 0x10;
            }

            SbHalInit();

            // Load IV (zero state)
            uint[] pdesc = new uint[6];
            pdesc[4] = 0x1801C20; // CMAC | ENCRYPT | LOAD_STATE0 | S_DIN_to_AES
            pdesc[1] = 0x8000041; // DIN_CONST, size=16
            AddDescSequence(pdesc);

            // Load key
            uint[] mdesc = new uint[6];
            if (aesKeyType == USER_KEY)
            {
                // DIN from SRAM
                mdesc[0] = pInternalKey;
                mdesc[1] = (uint)AES_KEY128_SIZE;
            }
            uint keyDescBits = (uint)(aesKeyType << 15); // cipher_do
            mdesc[4] = 0x0801C20 | keyDescBits; // CMAC | key_size | LOAD_KEY
            if (keySizeBytes == 0x20)
                mdesc[4] |= 0x400000; // AES-256
            AddDescSequence(mdesc);

            // Process data blocks
            if (blockSize > 0x10)
            {
                int fullBlocks = blockSize - 0x10;
                uint[] ddesc = new uint[6];
                ddesc[0] = pDataIn;
                ddesc[1] = (uint)fullBlocks;
                ddesc[4] = 0x20; // S_DIN_to_AES flow
                AddDescSequence(ddesc);
            }

            // Final block with MAC
            int lastBlockOff = blockSize > 0x10 ? blockSize - 0x10 : 0;
            uint[] fdesc = new uint[6];
            fdesc[0] = pDataIn + (uint)lastBlockOff;
            fdesc[1] = (uint)System.Math.Min(blockSize, 0x10);
            fdesc[2] = pDataOut;
            fdesc[3] = 0x10; // output 16 bytes
            fdesc[4] = 0x7801C20; // CMAC | ENCRYPT | S_DIN_to_AES | DOUT
            AddDescSequence(fdesc);

            return SbHalWaitDescCompletion(pDataOut) == 0;
        }

        private uint AesCmac(int aesKeyType, uint internalKey, byte[] dataIn,
                              int dmaMode, int bufferLen, uint destAddr)
        {
            uint ivAddr = destAddr;
            uint inputAddr = ivAddr + AES_IV_SIZE;
            int blockSize = (dataIn.Length / 0x20) * 0x20;
            if (blockSize < 0x20) blockSize = 0x20;
            uint outputAddr = inputAddr + (uint)blockSize;
            uint keyAddr = outputAddr + (uint)blockSize;

            if (internalKey != 0)
                WriteMem(keyAddr, BitConverter.GetBytes(internalKey));

            byte[] input = new byte[bufferLen];
            Buffer.BlockCopy(dataIn, 0, input, 0, System.Math.Min(dataIn.Length, bufferLen));
            WriteMem(inputAddr, input);

            if (AesCmacDriver(aesKeyType, keyAddr, inputAddr, dmaMode, bufferLen, ivAddr))
                return ivAddr;
            return 0;
        }

        #endregion

        #region Key Derivation

        /// <summary>
        /// SBROM key derivation using AES-CMAC.
        /// mtkclient: sbrom_key_derivation(aeskeytype, label, salt, requestedlen, destaddr)
        /// </summary>
        private byte[] KeyDerivation(int aesKeyType, byte[] label, byte[] salt, int requestedLen, uint destAddr)
        {
            if (requestedLen > 0xFF) return null;
            if (label == null || label.Length == 0 || label.Length > 0x20) return null;

            int iterLen = (requestedLen + 0xF) >> 4;
            byte[] result = new byte[iterLen * 16];
            int bufferLen = salt.Length + 3 + label.Length;

            for (int i = 0; i < iterLen; i++)
            {
                // buffer = pack("<B", i+1) + label + b"\x00" + salt + pack("<B", (8*requestedLen)&0xFF)
                byte[] buffer = new byte[bufferLen];
                buffer[0] = (byte)(i + 1);
                Buffer.BlockCopy(label, 0, buffer, 1, label.Length);
                buffer[1 + label.Length] = 0;
                Buffer.BlockCopy(salt, 0, buffer, 2 + label.Length, salt.Length);
                buffer[bufferLen - 1] = (byte)((8 * requestedLen) & 0xFF);

                uint dstAddr = AesCmac(aesKeyType, 0, buffer, 0, bufferLen, destAddr);
                if (dstAddr != 0)
                {
                    uint[] fields = Read32Array(dstAddr, 4);
                    for (int f = 0; f < 4; f++)
                        BitConverter.GetBytes(fields[f]).CopyTo(result, i * 16 + f * 4);
                }
            }

            byte[] final = new byte[requestedLen];
            Buffer.BlockCopy(result, 0, final, 0, requestedLen);
            return final;
        }

        #endregion

        #region Clock Control

        private void TzccClk(bool enable)
        {
            if (enable)
            {
                if (_hwCode == 0x1209)
                    Write32(0x10001084, 0x600);
                else
                    Write32(0x1000108C, 0x18000000);
            }
            else
            {
                if (_hwCode == 0x1209)
                    Write32(0x10001080, 0x200);
                else
                    Write32(0x10001088, 0x8000000);
            }
        }

        private uint GetDstAddr()
        {
            uint dst = _daPayloadAddr - 0x300;
            if (_hwCode == 0x1129) dst = 0x20F1000;
            return dst;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Generate RPMB key via DXCC key derivation.
        /// mtkclient: generate_rpmb(level)
        /// </summary>
        public byte[] GenerateRpmb(int level = 0)
        {
            if (_dxccBase == 0)
            {
                _error("DXCC not available on this chip.");
                return null;
            }

            byte[] rpmbIkey = System.Text.Encoding.ASCII.GetBytes("RPMB KEY");
            byte[] rpmbSalt = System.Text.Encoding.ASCII.GetBytes("SASI");

            // Shift by level
            for (int i = 0; i < rpmbIkey.Length; i++) rpmbIkey[i] = (byte)(rpmbIkey[i] + level);
            for (int i = 0; i < rpmbSalt.Length; i++) rpmbSalt[i] = (byte)(rpmbSalt[i] + level);

            int keyLen = level > 0 ? 0x10 : 0x20;
            TzccClk(true);
            byte[] key = KeyDerivation(1, rpmbIkey, rpmbSalt, keyLen, GetDstAddr());
            TzccClk(false);

            _info($"DXCC: Generated RPMB key (level={level}, len={keyLen})");
            return key;
        }

        /// <summary>
        /// Generate Motorola RPMB key.
        /// mtkclient: generate_moto_rpmb()
        /// </summary>
        public byte[] GenerateMotoRpmb()
        {
            if (_dxccBase == 0) return null;

            byte[] rpmbIkey = System.Text.Encoding.ASCII.GetBytes("CCUSTOMM");
            byte[] rpmbSalt = System.Text.Encoding.ASCII.GetBytes("MOTO");

            TzccClk(true);
            byte[] key = KeyDerivation(1, rpmbIkey, rpmbSalt, 0x10, GetDstAddr());
            TzccClk(false);

            _info("DXCC: Generated Moto RPMB key");
            return key;
        }

        /// <summary>
        /// Generate MITEE RPMB key.
        /// mtkclient: generate_rpmb_mitee()
        /// </summary>
        public byte[] GenerateRpmbMitee()
        {
            if (_dxccBase == 0) return null;

            byte[] rpmbIkey = new byte[] { 0xAD, 0x1A, 0xC6, 0xB4, 0xBD, 0xF4, 0xED, 0xB7 };
            byte[] rpmbSalt = new byte[] { 0x69, 0xEF, 0x65, 0x84 };

            TzccClk(true);
            byte[] key = KeyDerivation(1, rpmbIkey, rpmbSalt, 0x10, GetDstAddr());
            TzccClk(false);

            _info("DXCC: Generated MITEE RPMB key");
            return key;
        }

        /// <summary>
        /// Generate iTrustee FBE key.
        /// mtkclient: generate_itrustee_fbe(key_sz, appid)
        /// </summary>
        public byte[] GenerateITrusteeFbe(int keySz = 32, byte[] appId = null)
        {
            if (_dxccBase == 0) return null;
            byte[] salt = new byte[20 + 0x10 + (appId?.Length ?? 0)];
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("TrustedCorekeymaster"), 0, salt, 0, 20);
            for (int i = 20; i < 20 + 0x10; i++) salt[i] = 0x07;
            if (appId != null) Buffer.BlockCopy(appId, 0, salt, 36, appId.Length);
            return GenerateAesCmacKey(keySz, salt);
        }

        /// <summary>
        /// Generate AES-CMAC derived key.
        /// mtkclient: generate_aes_cmac(key_sz, salt)
        /// </summary>
        public byte[] GenerateAesCmacKey(int keySz = 32, byte[] salt = null)
        {
            if (_dxccBase == 0) return null;
            salt = salt ?? new byte[0];

            byte[] fdeKey = new byte[keySz];
            uint dstAddr = GetDstAddr();
            TzccClk(true);

            for (int ctr = 0; ctr < keySz / 16; ctr++)
            {
                byte[] seed = new byte[salt.Length + 1];
                Buffer.BlockCopy(salt, 0, seed, 0, salt.Length);
                seed[salt.Length] = (byte)ctr;

                uint pAddr = AesCmac(1, 0, seed, 0, seed.Length, dstAddr);
                if (pAddr != 0)
                {
                    uint[] fields = Read32Array(pAddr, 4);
                    for (int f = 0; f < 4; f++)
                        BitConverter.GetBytes(fields[f]).CopyTo(fdeKey, ctr * 16 + f * 4);
                }
            }

            TzccClk(false);
            return fdeKey;
        }

        /// <summary>
        /// Generate platform + provisioning keys.
        /// mtkclient: generate_provision_key()
        /// </summary>
        public (byte[] platKey, byte[] provKey) GenerateProvisionKey()
        {
            if (_dxccBase == 0) return (null, null);

            byte[] platLabel = System.Text.Encoding.ASCII.GetBytes("KEY PLAT");
            byte[] provLabel = System.Text.Encoding.ASCII.GetBytes("PROVISION KEY");

            TzccClk(true);
            uint dstAddr = GetDstAddr();

            byte[] salt = PubKeyHashGet(SASI_SB_HASH_BOOT_KEY_256B);
            byte[] platKey = KeyDerivation(PLATFORM_KEY, platLabel, salt ?? new byte[32], 0x10, dstAddr);

            // Wait for completion
            for (int i = 0; i < 10000; i++)
            {
                if ((Read32(_dxccBase + 0xAF4) & 1) != 0) break;
            }

            byte[] provKey = KeyDerivation(PROVISIONING_KEY, provLabel, salt ?? new byte[32], 0x10, dstAddr);

            // Clear key registers
            Write32(_dxccBase + 0xAC0, 0);
            Write32(_dxccBase + 0xAC4, 0);
            Write32(_dxccBase + 0xAC8, 0);
            Write32(_dxccBase + 0xACC, 0);

            // Lock engine
            uint[] lockDesc = new uint[6];
            lockDesc[1] = 0x8000081;
            lockDesc[4] = 0x4801C20;
            AddDescSequence(lockDesc);
            SbHalWaitDescCompletion();

            TzccClk(false);
            _info("DXCC: Generated provision keys");
            return (platKey, provKey);
        }

        /// <summary>
        /// Read OTP word from DXCC.
        /// mtkclient: sasi_bsv_otp_word_read(otpAddress)
        /// </summary>
        public uint? OtpWordRead(int otpAddress)
        {
            if (otpAddress > 0x24) return null;

            for (int i = 0; i < 10000; i++)
            {
                if ((Read32(_dxccBase + (0x2AF * 4)) & 1) != 0) break;
            }
            Write32(_dxccBase + (0x2A9 * 4), (uint)((4 * otpAddress) | 0x10000));
            for (int i = 0; i < 10000; i++)
            {
                if ((Read32(_dxccBase + (0x2AD * 4)) & 1) != 0) break;
            }
            return Read32(_dxccBase + (0x2AB * 4));
        }

        /// <summary>
        /// Get lifecycle state from DXCC.
        /// mtkclient: sasi_bsv_lcs_get()
        /// </summary>
        public uint LcsGet()
        {
            for (int i = 0; i < 10000; i++)
            {
                if ((Read32(_dxccBase + (0x2AF * 4)) & 1) != 0) break;
            }
            uint regVal = Read32(_dxccBase + (0x2A1 * 4));
            return regVal & 0x7;
        }

        /// <summary>
        /// Get public key hash.
        /// mtkclient: sasi_bsv_pub_key_hash_get(keyindex)
        /// </summary>
        public byte[] PubKeyHashGet(int keyIndex = 0)
        {
            uint baseOff = (uint)(keyIndex == 0 ? 0x2B0 : 0x2B8);
            byte[] hash = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                uint val = Read32(_dxccBase + (baseOff + (uint)i) * 4);
                BitConverter.GetBytes(val).CopyTo(hash, i * 4);
            }
            return hash;
        }

        /// <summary>
        /// Disable DXCC security.
        /// mtkclient: sasi_bsv_security_disable()
        /// </summary>
        public void SecurityDisable()
        {
            uint lcs = LcsGet();
            if (lcs == 7) return;
            Write32(_dxccBase + 0xAC0, 0);
            Write32(_dxccBase + 0xAC4, 0);
            Write32(_dxccBase + 0xAC8, 0);
            Write32(_dxccBase + 0xACC, 0);
            Write32(_dxccBase + 0xAD8, 1);
            _info("DXCC: Security disabled.");
        }

        /// <summary>
        /// Compute SoC ID via DXCC.
        /// mtkclient: sasi_bsv_socid_compute()
        /// </summary>
        public byte[] ComputeSocId()
        {
            if (_dxccBase == 0) return null;

            byte[] key = new byte[] { 0x49 };
            byte[] salt = new byte[32];

            TzccClk(true);
            uint dstAddr = GetDstAddr();
            byte[] pubKey = PubKeyHashGet(SASI_SB_HASH_BOOT_KEY_256B);
            byte[] derivedKey = KeyDerivation(1, key, salt, 0x10, dstAddr);
            TzccClk(false);

            if (pubKey == null || derivedKey == null) return null;

            byte[] combined = new byte[pubKey.Length + derivedKey.Length];
            Buffer.BlockCopy(pubKey, 0, combined, 0, pubKey.Length);
            Buffer.BlockCopy(derivedKey, 0, combined, pubKey.Length, derivedKey.Length);

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
                return sha256.ComputeHash(combined);
        }

        #endregion
    }

    /// <summary>
    /// GCPU (MediaTek General-Purpose Crypto Unit) — full register-level implementation.
    /// Ported from mtkclient/Library/Hardware/hwcrypto_gcpu.py
    /// </summary>
    public class GCpu
    {
        private readonly CryptoSetup _setup;
        private readonly Action<string> _info;
        private readonly Action<string> _error;

        #region GCPU Register Offsets
        private const uint GCPU_REG_CTL = 0x0;
        private const uint GCPU_REG_MSC = 0x4;
        private const uint GCPU_AXI = 0x20;
        private const uint GCPU_UNK2 = 0x24;
        private const uint GCPU_REG_PC_CTL = 0x400;
        private const uint GCPU_REG_MEM_ADDR = 0x404;
        private const uint GCPU_REG_MEM_DATA = 0x408;
        private const uint GCPU_REG_READ_REG = 0x410;
        private const uint GCPU_REG_MONCTL = 0x414;
        private const uint GCPU_REG_DRAM_MON = 0x418;
        private const uint GCPU_REG_INT_SET = 0x800;
        private const uint GCPU_REG_INT_CLR = 0x804;
        private const uint GCPU_REG_INT_EN = 0x808;
        private const uint GCPU_REG_MEM_CMD = 0xC00;
        // Parameter registers: P0..P15 at MEM_CMD + N*4
        private const uint GCPU_REG_MEM_P0 = 0xC00 + 1 * 4;
        private const uint GCPU_REG_MEM_P1 = 0xC00 + 2 * 4;
        private const uint GCPU_REG_MEM_P2 = 0xC00 + 3 * 4;
        private const uint GCPU_REG_MEM_P3 = 0xC00 + 4 * 4;
        private const uint GCPU_REG_MEM_P4 = 0xC00 + 5 * 4;
        private const uint GCPU_REG_MEM_P5 = 0xC00 + 6 * 4;
        private const uint GCPU_REG_MEM_P6 = 0xC00 + 7 * 4;
        #endregion

        #region AES Command Codes
        private const uint AES_D = 0x20;
        private const uint AES_E = 0x21;
        private const uint AES_DCBC = 0x26;
        private const uint AES_ECBC = 0x27;
        private const uint AESPK_D = 0x78;
        private const uint AESPK_E = 0x79;
        private const uint AESPK_DCBC = 0x7C;
        private const uint AESPK_ECBC = 0x7D;
        private const uint AESPK_EK_D = 0x76;
        private const uint AESPK_EK_E = 0x77;
        private const uint AESPK_EK_DCBC = 0x7E;
        private const uint GCPU_WRITE = 0x6E;
        private const uint GCPU_READ = 0x6F;
        #endregion

        public GCpu(CryptoSetup setup, Action<string> info, Action<string> error)
        {
            _setup = setup;
            _info = info ?? delegate { };
            _error = error ?? delegate { };
        }

        #region Register Access
        private void GWrite(uint offset, uint value)
        {
            _setup.Write32(_setup.GcpuBase + offset, value);
        }

        private uint GRead(uint offset)
        {
            return _setup.Read32(_setup.GcpuBase + offset, 1);
        }

        private void MemPtrSet(int slot, byte[] data)
        {
            if (_setup.GcpuBase == 0) return;
            uint[] dwords = BytesToDwords(data);
            for (int i = 0; i < dwords.Length; i++)
                _setup.Write32(_setup.GcpuBase + GCPU_REG_MEM_CMD + (uint)(slot * 4) + (uint)(i * 4), dwords[i]);
        }

        private void MemPtrSet(int slot, uint[] data)
        {
            if (_setup.GcpuBase == 0) return;
            for (int i = 0; i < data.Length; i++)
                _setup.Write32(_setup.GcpuBase + GCPU_REG_MEM_CMD + (uint)(slot * 4) + (uint)(i * 4), data[i]);
        }

        private byte[] MemPtrGet(int slot, int length)
        {
            byte[] result = new byte[length];
            for (int i = 0; i < length; i += 4)
            {
                uint val = _setup.Read32(_setup.GcpuBase + GCPU_REG_MEM_CMD + (uint)(slot * 4) + (uint)i, 1);
                int copyLen = Math.Min(4, length - i);
                byte[] vb = BitConverter.GetBytes(val);
                Buffer.BlockCopy(vb, 0, result, i, copyLen);
            }
            return result;
        }
        #endregion

        #region Core Engine

        /// <summary>
        /// Initialize GCPU engine.
        /// mtkclient: init()
        /// </summary>
        public void Init()
        {
            if (_setup.GcpuBase == 0) return;
            // Clear key slots
            MemPtrSet(18, new uint[] { 0, 0, 0, 0 });   // Key slot
            MemPtrSet(22, new uint[] { 0, 0, 0, 0 });   // Seed slot
            MemPtrSet(26, new uint[] { 0, 0, 0, 0, 0, 0, 0, 0 }); // IV slot
        }

        /// <summary>
        /// Acquire GCPU hardware — chip-specific initialization.
        /// mtkclient: acquire()
        /// </summary>
        public void Acquire()
        {
            if (_setup.GcpuBase == 0) return;

            // Generic acquire path (works for most chips)
            uint ctl = GRead(GCPU_REG_CTL);
            GWrite(GCPU_REG_CTL, (ctl & 0xFFFFFFF0) | 0xF);
            uint msc = GRead(GCPU_REG_MSC);
            GWrite(GCPU_REG_MSC, msc | 0x10000);
            ctl = GRead(GCPU_REG_CTL);
            GWrite(GCPU_REG_CTL, (ctl & 0xFFFFFFE0) | 0x1F);
            msc = GRead(GCPU_REG_MSC);
            GWrite(GCPU_REG_MSC, msc | 0x2000);
        }

        /// <summary>
        /// Release GCPU hardware.
        /// mtkclient: release()
        /// </summary>
        public void Release()
        {
            if (_setup.GcpuBase == 0) return;
            uint ctl = GRead(GCPU_REG_CTL);
            GWrite(GCPU_REG_CTL, (ctl & 0xFFFFFFF0) | 0xF);
        }

        /// <summary>
        /// Execute GCPU command and wait for completion.
        /// mtkclient: cmd(cmd, addr, args)
        /// </summary>
        public int Cmd(uint cmd, uint addr = 0, uint[] args = null)
        {
            if (_setup.GcpuBase == 0) return -1;

            // Setup parameters
            if (args != null)
            {
                for (int i = 0; i < args.Length && i < 48; i++)
                    _setup.Write32(_setup.GcpuBase + GCPU_REG_MEM_CMD + (uint)((i + 1) * 4), args[i]);
            }

            // Clear/enable interrupt
            GWrite(GCPU_REG_INT_CLR, 3);
            GWrite(GCPU_REG_INT_EN, 3);

            // Set command
            GWrite(GCPU_REG_MEM_CMD, cmd);

            // Set PC
            GWrite(GCPU_REG_PC_CTL, addr);

            // Wait for completion
            int timeout = 10000;
            while (timeout-- > 0)
            {
                uint intSet = GRead(GCPU_REG_INT_SET);
                if (intSet != 0)
                {
                    GWrite(GCPU_REG_INT_CLR, 3);
                    if ((intSet & 2) != 0 && (intSet & 1) == 0)
                        return -1; // Error
                    return 0; // Success
                }
            }

            _error("GCPU: Command timeout");
            GWrite(GCPU_REG_INT_CLR, 3);
            return -1;
        }

        /// <summary>
        /// Select AES mode command code.
        /// mtkclient: set_mode_cmd(encrypt, mode, encryptedkey)
        /// </summary>
        private int SetModeCmd(bool encrypt = false, string mode = "cbc", bool encryptedKey = true)
        {
            uint cmd;
            if (encrypt)
            {
                if (mode == "ecb")
                    cmd = encryptedKey ? AESPK_EK_E : AESPK_E;
                else // cbc
                    cmd = AESPK_ECBC;
            }
            else
            {
                if (mode == "ecb")
                    cmd = encryptedKey ? AESPK_EK_D : AESPK_D;
                else // cbc
                    cmd = encryptedKey ? AESPK_EK_DCBC : AESPK_DCBC;
            }
            return Cmd(cmd);
        }
        #endregion

        #region AES Operations

        /// <summary>
        /// AES CBC operation.
        /// mtkclient: aes_cbc(encrypt, src, dst, length, keyslot, ivslot)
        /// </summary>
        public bool AesCbc(bool encrypt, uint src, uint dst, int length = 16, int keySlot = 18, int ivSlot = 26)
        {
            int dLength = length / 16;
            if (length % 16 != 0) dLength++;

            GWrite(GCPU_REG_MEM_P0, src);       // SrcStartAddr
            GWrite(GCPU_REG_MEM_P1, dst);       // DstStartAddr
            GWrite(GCPU_REG_MEM_P2, (uint)dLength); // DataLen/16
            GWrite(GCPU_REG_MEM_P4, (uint)keySlot);  // Key slot
            GWrite(GCPU_REG_MEM_P5, (uint)ivSlot);   // IV slot
            GWrite(GCPU_REG_MEM_P6, (uint)ivSlot);   // IV slot (same)

            if (SetModeCmd(encrypt, "cbc", true) != 0)
            {
                _error("GCPU: AES CBC command failed");
                return false;
            }
            return true;
        }

        /// <summary>
        /// AES CBC read 16 bytes from address — reads result from IV slot.
        /// mtkclient: aes_read_cbc(addr, encrypt, keyslot, ivslot)
        /// </summary>
        public byte[] AesReadCbc(uint addr, bool encrypt, int keySlot = 18, int ivSlot = 26)
        {
            if (_setup.GcpuBase == 0) return null;
            AesCbc(encrypt, addr, 0, 16, keySlot, ivSlot);
            // Read result from IV slot
            return MemPtrGet(ivSlot, 16);
        }

        /// <summary>
        /// AES CBC read 16 bytes (decrypt mode, used by Amonet).
        /// </summary>
        public byte[] AesReadCbc(uint addr)
        {
            return AesReadCbc(addr, false);
        }

        /// <summary>
        /// Setup AES CBC with IV XOR pattern.
        /// mtkclient: aes_setup_cbc(addr, data, iv, encrypt)
        /// </summary>
        public bool AesSetupCbc(uint addr, byte[] data, byte[] iv = null, bool encrypt = false)
        {
            if (_setup.GcpuBase == 0) return false;
            if (data.Length != 16)
            {
                _error("GCPU: AES setup CBC data must be 16 bytes");
                return false;
            }

            int keySlot = 0x12;
            int seedSlot = 0x16;
            int ivSlot = 0x1A;

            if (iv == null)
                iv = HexToBytes("4dd12bdf0ec7d26c482490b3482a1b1f");

            // IV XOR with data
            uint[] words = new uint[4];
            for (int x = 0; x < 4; x++)
            {
                uint word = BitConverter.ToUInt32(data, x * 4);
                uint pat = BitConverter.ToUInt32(iv, x * 4);
                words[x] = word ^ pat;
            }

            // Clear and set slots
            MemPtrSet(keySlot, new uint[] { 0, 0, 0, 0 });
            MemPtrSet(seedSlot, new uint[] { 0, 0, 0, 0 });
            MemPtrSet(ivSlot, new uint[] { 0, 0, 0, 0, 0, 0, 0, 0 });
            MemPtrSet(ivSlot, words);

            return AesCbc(encrypt, 0, addr, 16, keySlot, ivSlot);
        }

        /// <summary>
        /// AES ECB read using predetermined key.
        /// mtkclient: aes_read_ecb(data, encrypt, src, dst, keyslot)
        /// </summary>
        public byte[] AesReadEcb(byte[] data, bool encrypt, int src = 0x12, int dst = 0x1A, int keySlot = 0x30)
        {
            if (_setup.GcpuBase == 0) return null;

            if (LoadHwKey(0x30))
            {
                MemPtrSet(src, data);
                if (encrypt)
                {
                    if (AesEncryptEcb(keySlot, src, dst) == 0)
                        return MemPtrGet(dst, 16);
                }
                else
                {
                    if (AesDecryptEcb(keySlot, src, dst) == 0)
                        return MemPtrGet(dst, 16);
                }
            }
            return null;
        }

        /// <summary>
        /// Overload for compatibility.
        /// </summary>
        public byte[] AesReadEcb(byte[] data, bool encrypt)
        {
            return AesReadEcb(data, encrypt, 0x12, 0x1A, 0x30);
        }

        /// <summary>
        /// AES decrypt ECB via GCPU command.
        /// mtkclient: aes_decrypt_ecb(key_offset, data_offset, out_offset)
        /// </summary>
        private int AesDecryptEcb(int keyOffset, int dataOffset, int outOffset)
        {
            GWrite(GCPU_REG_MEM_P0, 1); // Decrypt
            GWrite(GCPU_REG_MEM_P1, (uint)keyOffset);
            GWrite(GCPU_REG_MEM_P2, (uint)dataOffset);
            GWrite(GCPU_REG_MEM_P3, (uint)outOffset);
            return Cmd(AESPK_D);
        }

        /// <summary>
        /// AES encrypt ECB via GCPU command.
        /// mtkclient: aes_encrypt_ecb(key_offset, data_offset, out_offset)
        /// </summary>
        private int AesEncryptEcb(int keyOffset, int dataOffset, int outOffset)
        {
            GWrite(GCPU_REG_MEM_P0, 0); // Encrypt
            GWrite(GCPU_REG_MEM_P1, (uint)keyOffset);
            GWrite(GCPU_REG_MEM_P2, (uint)dataOffset);
            GWrite(GCPU_REG_MEM_P3, (uint)outOffset);
            return Cmd(AESPK_E);
        }

        /// <summary>
        /// Load hardware key into GCPU memory slot.
        /// mtkclient: load_hw_key(offset)
        /// </summary>
        public bool LoadHwKey(int offset)
        {
            if (_setup.GcpuBase == 0) return false;
            // vGcpuMEMExeNoIntr — load HW key into slot
            GWrite(GCPU_REG_MEM_P0, (uint)offset);
            int result = Cmd(GCPU_READ);
            return result == 0;
        }

        #endregion

        #region MTEE Decryption

        /// <summary>
        /// Decrypt MTEE image using GCPU AES.
        /// mtkclient: mtk_gcpu_decrypt_mtee_img(data, keyseed, ivseed, aeskey1, aeskey2)
        /// </summary>
        public byte[] DecryptMteeImg(byte[] data, byte[] keySeed, byte[] ivSeed, byte[] aesKey1, byte[] aesKey2)
        {
            if (_setup.GcpuBase == 0) return null;

            uint src = 0x43001240;
            uint dst = 0x43001000;

            // Write data to src address
            uint[] dataDwords = BytesToDwords(data);
            for (int i = 0; i < dataDwords.Length; i++)
                _setup.Write32(src + (uint)(i * 4), dataDwords[i]);

            // Setup key and seed
            MemPtrSet(0x12, aesKey1);
            MemPtrSet(0x16, keySeed);

            // Decrypt seed to get actual key
            GWrite(GCPU_REG_MEM_P0, 1); // Decrypt
            GWrite(GCPU_REG_MEM_P1, 0x12); // Key
            GWrite(GCPU_REG_MEM_P2, 0x16); // Data
            GWrite(GCPU_REG_MEM_P3, 0x1A); // Out (IV)
            Cmd(AESPK_D);

            // XOR ivseed with aeskey2
            byte[] xorKey = new byte[16];
            for (int i = 0; i < 16 && i < aesKey2.Length && i < ivSeed.Length; i++)
                xorKey[i] = (byte)(ivSeed[i] ^ aesKey2[i]);

            MemPtrSet(0x12, xorKey);

            int length = data.Length;
            // Setup CBC decryption
            GWrite(GCPU_REG_MEM_P0, src);
            GWrite(GCPU_REG_MEM_P1, dst);
            GWrite(GCPU_REG_MEM_P2, (uint)(length / 16));
            GWrite(GCPU_REG_MEM_P4, 0x12); // Key
            GWrite(GCPU_REG_MEM_P5, 0x1A); // IV
            GWrite(GCPU_REG_MEM_P6, 0x1A); // IV

            if (SetModeCmd(false, "cbc", false) != 0)
            {
                _error("GCPU: MTEE decrypt failed");
                return null;
            }

            // Read result from dst
            byte[] result = new byte[length];
            for (int i = 0; i < length; i += 4)
            {
                uint val = _setup.Read32(dst + (uint)i, 1);
                int copyLen = Math.Min(4, length - i);
                byte[] vb = BitConverter.GetBytes(val);
                Buffer.BlockCopy(vb, 0, result, i, copyLen);
            }
            return result;
        }

        #endregion

        #region Blacklist

        /// <summary>
        /// Disable range blacklist via GCPU write commands.
        /// mtkclient: disable_range_blacklist()
        /// </summary>
        public void DisableRangeBlacklist()
        {
            if (_setup.Blacklist == null || _setup.GcpuBase == 0) return;
            _info("GCPU: Disabling bootrom range checks...");
            foreach (var field in _setup.Blacklist)
            {
                uint addr = field.Item1;
                uint value = field.Item2;
                // Use GCPU_WRITE to bypass blacklist
                GWrite(GCPU_REG_MEM_P0, addr);
                GWrite(GCPU_REG_MEM_P1, value);
                Cmd(GCPU_WRITE);
            }
        }

        #endregion

        #region Helpers
        private static uint[] BytesToDwords(byte[] data)
        {
            int count = (data.Length + 3) / 4;
            uint[] result = new uint[count];
            for (int i = 0; i < count; i++)
            {
                if (i * 4 + 4 <= data.Length)
                    result[i] = BitConverter.ToUInt32(data, i * 4);
                else
                {
                    byte[] tmp = new byte[4];
                    int remaining = data.Length - i * 4;
                    Buffer.BlockCopy(data, i * 4, tmp, 0, remaining);
                    result[i] = BitConverter.ToUInt32(tmp, 0);
                }
            }
            return result;
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
        #endregion
    }

    /// <summary>
    /// SEJ (Security Engine for JTAG) — full register-level implementation.
    /// Ported from mtkclient/Library/Hardware/hwcrypto_sej.py
    /// </summary>
    public class Sej
    {
        private readonly CryptoSetup _setup;
        private readonly Action<string> _info;
        private readonly Action<string> _error;

        #region HACC Register Offsets
        private const uint HACC_CON    = 0x0000;
        private const uint HACC_ACON   = 0x0004;
        private const uint HACC_ACON2  = 0x0008;
        private const uint HACC_ACONK  = 0x000C;
        private const uint HACC_ASRC0  = 0x0010;
        private const uint HACC_ASRC1  = 0x0014;
        private const uint HACC_ASRC2  = 0x0018;
        private const uint HACC_ASRC3  = 0x001C;
        private const uint HACC_AKEY0  = 0x0020;
        private const uint HACC_AKEY1  = 0x0024;
        private const uint HACC_AKEY2  = 0x0028;
        private const uint HACC_AKEY3  = 0x002C;
        private const uint HACC_AKEY4  = 0x0030;
        private const uint HACC_AKEY5  = 0x0034;
        private const uint HACC_AKEY6  = 0x0038;
        private const uint HACC_AKEY7  = 0x003C;
        private const uint HACC_ACFG0  = 0x0040;
        private const uint HACC_ACFG1  = 0x0044;
        private const uint HACC_ACFG2  = 0x0048;
        private const uint HACC_ACFG3  = 0x004C;
        private const uint HACC_AOUT0  = 0x0050;
        private const uint HACC_AOUT1  = 0x0054;
        private const uint HACC_AOUT2  = 0x0058;
        private const uint HACC_AOUT3  = 0x005C;
        private const uint HACC_SW_OTP0 = 0x0060;
        private const uint HACC_SW_OTP1 = 0x0064;
        private const uint HACC_SW_OTP2 = 0x0068;
        private const uint HACC_SW_OTP3 = 0x006C;
        private const uint HACC_SW_OTP4 = 0x0070;
        private const uint HACC_SW_OTP5 = 0x0074;
        private const uint HACC_SW_OTP6 = 0x0078;
        private const uint HACC_SW_OTP7 = 0x007C;
        private const uint HACC_SECINIT0 = 0x0080;
        private const uint HACC_SECINIT1 = 0x0084;
        private const uint HACC_SECINIT2 = 0x0088;
        private const uint HACC_MKJ    = 0x00A0;
        private const uint HACC_UNK    = 0x00BC;
        #endregion

        #region HACC Bit Constants
        private const uint HACC_AES_DEC = 0x00;
        private const uint HACC_AES_ENC = 0x01;
        private const uint HACC_AES_CBC = 0x02;
        private const uint HACC_AES_ECB = 0x00;
        private const uint HACC_AES_128 = 0x00;
        private const uint HACC_AES_192 = 0x10;
        private const uint HACC_AES_256 = 0x20;
        private const uint HACC_AES_CHG_BO_OFF = 0x00;
        private const uint HACC_AES_CHG_BO_ON  = 0x40;
        private const uint HACC_AES_START = 0x01;
        private const uint HACC_AES_CLR   = 0x02;
        private const uint HACC_AES_RDY   = 0x8000;
        private const uint HACC_AES_R2K   = 0x100;
        private const uint HACC_AES_BK2C  = 0x10;
        #endregion

        #region Keys and IVs
        private static readonly uint[] g_HACC_CFG_1 = { 0x9ED40400, 0x00E884A1, 0xE3F083BD, 0x2F4E6D8A };
        private static readonly uint[] g_HACC_CFG_2 = { 0xFC446BD8, 0x1445F980, 0x6E5A1490, 0xEF38DDB0 };
        private static readonly uint[] g_HACC_CFG_3 = { 0x9E691538, 0x758F11A3, 0xA23A6B5C, 0xDC8B19F0 };
        private static readonly uint[] g_HACC_CFG_MTEE = { 0x584E5744, 0x42534553, 0x41544144, 0x00000000 };
        private static readonly uint[] g_CFG_RANDOM_PATTERN =
        {
            0x1F2E3D4C, 0x5A6B7C8D, 0x9EAFB0C1, 0xD2E3F4A5,
            0xB6C7D8E9, 0xFA0B1C2D, 0x3E4F5A6B, 0x7C8D9EAF,
            0xB0C1D2E3, 0xF4A5B6C7, 0xD8E9FA0B, 0x1C2D3E4F
        };
        private static readonly uint[] g_aes_swotp =
        {
            0x7D4F7A57, 0x6025FC1D, 0xE2A78AFC, 0x98347309,
            0xDDBC43BD, 0x2425A444, 0xEF7F1ACB, 0x70131C4F
        };
        private static readonly uint[] g_UnqKey_IV =
        {
            0x6786CFBD, 0x44B7F1E0, 0x1544B07B, 0x53A28EB3,
            0xD7AB8AA2, 0xB9E30E7E, 0x172156E0, 0x3064C973
        };
        #endregion

        public Sej(CryptoSetup setup, Action<string> info, Action<string> error)
        {
            _setup = setup;
            _info = info ?? delegate { };
            _error = error ?? delegate { };
        }

        #region Register Access
        private void RegWrite(uint offset, uint value)
        {
            _setup.Write32(_setup.SejBase + offset, value);
        }

        private uint RegRead(uint offset)
        {
            return _setup.Read32(_setup.SejBase + offset, 1);
        }

        private void RegOr(uint offset, uint bits)
        {
            uint val = RegRead(offset);
            RegWrite(offset, val | bits);
        }

        private void RegAnd(uint offset, uint mask)
        {
            uint val = RegRead(offset);
            RegWrite(offset, val & mask);
        }
        #endregion

        #region Core Engine

        /// <summary>
        /// Run HACC AES engine on data blocks (16 bytes per iteration).
        /// mtkclient: HACC_V3_Run(data, noread, legacy, attr, sej_param)
        /// </summary>
        private byte[] HaccV3Run(uint[] psrc, bool noRead = false, bool legacy = false, uint attr = 0, uint sejParam = 0)
        {
            byte[] pdst = new byte[psrc.Length * 4];
            int plen = psrc.Length;

            if (legacy)
            {
                if ((attr & 8) != 0 && (sejParam & 2) != 0)
                    RegOr(HACC_ACONK, HACC_AES_R2K);
                else
                    RegAnd(HACC_ACONK, 0xFFFFFEFF);
            }

            int outPos = 0;
            for (int i = 0; i < plen; i += 4)
            {
                RegWrite(HACC_ASRC0, psrc[i]);
                RegWrite(HACC_ASRC1, i + 1 < plen ? psrc[i + 1] : 0);
                RegWrite(HACC_ASRC2, i + 2 < plen ? psrc[i + 2] : 0);
                RegWrite(HACC_ASRC3, i + 3 < plen ? psrc[i + 3] : 0);

                RegWrite(HACC_ACON2, HACC_AES_START);

                // Wait for ready
                int timeout = 20;
                while (timeout-- > 0)
                {
                    if ((RegRead(HACC_ACON2) & HACC_AES_RDY) != 0)
                        break;
                }
                if (timeout <= 0)
                    _error("SEJ: HACC hardware timeout — results may be wrong.");

                if (!noRead)
                {
                    WriteLE(pdst, outPos, RegRead(HACC_AOUT0)); outPos += 4;
                    WriteLE(pdst, outPos, RegRead(HACC_AOUT1)); outPos += 4;
                    WriteLE(pdst, outPos, RegRead(HACC_AOUT2)); outPos += 4;
                    WriteLE(pdst, outPos, RegRead(HACC_AOUT3)); outPos += 4;
                }
                else
                {
                    outPos += 16;
                }
            }

            if (outPos < pdst.Length)
            {
                byte[] trimmed = new byte[outPos];
                Buffer.BlockCopy(pdst, 0, trimmed, 0, outPos);
                return trimmed;
            }
            return pdst;
        }

        /// <summary>
        /// Initialize SEJ V3 for AES operation.
        /// mtkclient: SEJ_V3_Init(ben, iv, legacy)
        /// </summary>
        private uint SejV3Init(bool encrypt = true, uint[] iv = null, bool legacy = false)
        {
            if (iv == null) iv = g_HACC_CFG_1;

            uint aconSetting = HACC_AES_CHG_BO_OFF | HACC_AES_128;
            if (iv != null) aconSetting |= HACC_AES_CBC;
            aconSetting |= encrypt ? HACC_AES_ENC : HACC_AES_DEC;

            // Clear key
            for (uint k = HACC_AKEY0; k <= HACC_AKEY7; k += 4)
                RegWrite(k, 0);

            // Generate META key
            RegWrite(HACC_ACON, HACC_AES_CHG_BO_OFF | HACC_AES_CBC | HACC_AES_128 | HACC_AES_DEC);
            RegWrite(HACC_ACONK, HACC_AES_BK2C);
            RegOr(HACC_ACONK, HACC_AES_R2K);

            // Clear ASRC/ACFG/AOUT
            RegWrite(HACC_ACON2, HACC_AES_CLR);

            // Set IV
            RegWrite(HACC_ACFG0, iv[0]);
            RegWrite(HACC_ACFG1, iv[1]);
            RegWrite(HACC_ACFG2, iv[2]);
            RegWrite(HACC_ACFG3, iv[3]);

            if (legacy)
            {
                RegOr(HACC_UNK, 2);
                RegOr(HACC_ACON2, 0x40000000);
                int timeout = 20;
                while (timeout-- > 0)
                {
                    if (RegRead(HACC_ACON2) > 0x80000000)
                        break;
                }
                if (timeout <= 0)
                    _error("SEJ: Legacy hardware timeout.");
                RegAnd(HACC_UNK, 0xFFFFFFFE);
                RegWrite(HACC_ACONK, HACC_AES_BK2C);
                RegWrite(HACC_ACON, aconSetting);
            }
            else
            {
                RegWrite(HACC_UNK, 1);

                // Encrypt random pattern 3 rounds to derive key from HUID/HUK
                for (int round = 0; round < 3; round++)
                {
                    int pos = round * 4;
                    RegWrite(HACC_ASRC0, g_CFG_RANDOM_PATTERN[pos]);
                    RegWrite(HACC_ASRC1, g_CFG_RANDOM_PATTERN[pos + 1]);
                    RegWrite(HACC_ASRC2, g_CFG_RANDOM_PATTERN[pos + 2]);
                    RegWrite(HACC_ASRC3, g_CFG_RANDOM_PATTERN[pos + 3]);
                    RegWrite(HACC_ACON2, HACC_AES_START);

                    int timeout = 20;
                    while (timeout-- > 0)
                    {
                        if ((RegRead(HACC_ACON2) & HACC_AES_RDY) != 0)
                            break;
                    }
                    if (timeout <= 0)
                        _error("SEJ: HACC init round timeout.");
                }

                RegWrite(HACC_ACON2, HACC_AES_CLR);
                RegWrite(HACC_ACFG0, iv[0]);
                RegWrite(HACC_ACFG1, iv[1]);
                RegWrite(HACC_ACFG2, iv[2]);
                RegWrite(HACC_ACFG3, iv[3]);
                RegWrite(HACC_ACON, aconSetting);
                RegWrite(HACC_ACONK, 0);
            }

            return aconSetting;
        }

        /// <summary>
        /// Terminate SEJ session.
        /// mtkclient: sej_terminate()
        /// </summary>
        private void SejTerminate()
        {
            RegWrite(HACC_ACON2, HACC_AES_CLR);
            for (uint k = HACC_AKEY0; k <= HACC_AKEY7; k += 4)
                RegWrite(k, 0);
        }

        /// <summary>
        /// Set OTP (One-Time Programmable) values into HACC SW_OTP registers.
        /// mtkclient: sej_set_otp(data)
        /// </summary>
        public void SejSetOtp(byte[] data)
        {
            uint[] pd;
            if (data.Length >= 32)
                pd = BytesToDwords(data, 8);
            else
                pd = BytesToDwords(data, data.Length / 4);

            if (pd.Length > 0) RegWrite(HACC_SW_OTP0, pd[0]);
            if (pd.Length > 1) RegWrite(HACC_SW_OTP1, pd[1]);
            if (pd.Length > 2) RegWrite(HACC_SW_OTP2, pd[2]);
            if (pd.Length > 3) RegWrite(HACC_SW_OTP3, pd[3]);
            if (pd.Length > 4) RegWrite(HACC_SW_OTP4, pd[4]);
            if (pd.Length > 5) RegWrite(HACC_SW_OTP5, pd[5]);
            if (pd.Length > 6) RegWrite(HACC_SW_OTP6, pd[6]);
            if (pd.Length > 7) RegWrite(HACC_SW_OTP7, pd[7]);
        }

        private void SejSetOtp(uint[] data)
        {
            if (data.Length > 0) RegWrite(HACC_SW_OTP0, data[0]);
            if (data.Length > 1) RegWrite(HACC_SW_OTP1, data[1]);
            if (data.Length > 2) RegWrite(HACC_SW_OTP2, data[2]);
            if (data.Length > 3) RegWrite(HACC_SW_OTP3, data[3]);
            if (data.Length > 4) RegWrite(HACC_SW_OTP4, data[4]);
            if (data.Length > 5) RegWrite(HACC_SW_OTP5, data[5]);
            if (data.Length > 6) RegWrite(HACC_SW_OTP6, data[6]);
            if (data.Length > 7) RegWrite(HACC_SW_OTP7, data[7]);
        }

        #endregion

        #region Public API

        /// <summary>
        /// AES-128-CBC encrypt/decrypt using SEJ hardware.
        /// mtkclient: hw_aes128_cbc_encrypt(buf, encrypt, iv, legacy)
        /// </summary>
        public byte[] HwAes128CbcEncrypt(byte[] buf, bool encrypt = true, uint[] iv = null, bool legacy = false)
        {
            if (_setup.SejBase == 0)
            {
                _error("SEJ not available on this chip.");
                return null;
            }
            if (iv == null) iv = g_HACC_CFG_1;

            _info("AES128 CBC - HACC init");
            SejV3Init(encrypt, iv, legacy);
            _info("AES128 CBC - HACC run");
            uint[] psrc = BytesToDwords(buf, buf.Length / 4);
            byte[] result = HaccV3Run(psrc);
            _info("AES128 CBC - HACC terminate");
            SejTerminate();
            return result;
        }

        /// <summary>
        /// Overload for compatibility — encrypt flag only.
        /// </summary>
        public byte[] HwAes128CbcEncrypt(byte[] buf, bool encrypt)
        {
            return HwAes128CbcEncrypt(buf, encrypt, null, false);
        }

        /// <summary>
        /// Generate RPMB key from MEID and OTP via SEJ hardware AES.
        /// mtkclient: generate_rpmb(meid, otp, derivedlen)
        /// </summary>
        public byte[] GenerateRpmb(byte[] meid, byte[] otp, int derivedLen = 32)
        {
            if (_setup.SejBase == 0)
            {
                _error("SEJ not available on this chip.");
                return null;
            }

            SejSetOtp(otp);

            // Repeat MEID bytes to fill derivedLen
            byte[] buf = new byte[derivedLen];
            for (int i = 0; i < derivedLen; i++)
                buf[i] = meid[i % meid.Length];

            return HwAes128CbcEncrypt(buf, true, g_HACC_CFG_1, false);
        }

        /// <summary>
        /// Generate MTEE key via SEJ dev_kdf.
        /// mtkclient: generate_mtee(otp)
        /// </summary>
        public byte[] GenerateMtee(byte[] otp)
        {
            if (_setup.SejBase == 0)
            {
                _error("SEJ not available on this chip.");
                return null;
            }

            if (otp != null) SejSetOtp(otp);

            // "KeymasterMaster\0"
            byte[] buf = new byte[]
            {
                0x4B, 0x65, 0x79, 0x6D, 0x61, 0x73, 0x74, 0x65,
                0x72, 0x4D, 0x61, 0x73, 0x74, 0x65, 0x72, 0x00
            };
            return DevKdf(buf, 16);
        }

        /// <summary>
        /// Generate MTEE key from hardware using MTEE IV.
        /// mtkclient: generate_mtee_hw(otp)
        /// </summary>
        public byte[] GenerateMteeHw(byte[] otp)
        {
            if (_setup.SejBase == 0) return null;
            if (otp != null) SejSetOtp(otp);

            _info("MTee - HACC init");
            SejV3Init(true, g_HACC_CFG_MTEE);
            _info("MTee - HACC run");
            // "www.mediatek.com0123456789ABCDEF"
            byte[] data = new byte[]
            {
                0x77, 0x77, 0x77, 0x2E, 0x6D, 0x65, 0x64, 0x69,
                0x61, 0x74, 0x65, 0x6B, 0x2E, 0x63, 0x6F, 0x6D,
                0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
                0x38, 0x39, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46
            };
            uint[] psrc = BytesToDwords(data, data.Length / 4);
            byte[] result = HaccV3Run(psrc);
            _info("MTee - HACC terminate");
            SejTerminate();
            return result;
        }

        /// <summary>
        /// Decrypt/encrypt secure config data via SEJ.
        /// mtkclient: sej_sec_cfg_hw(data, encrypt, noxor)
        /// </summary>
        public byte[] SejSecCfgHw(byte[] data, bool encrypt = true, bool noXor = false)
        {
            if (_setup.SejBase == 0) return null;
            byte[] workData;
            if (encrypt && !noXor)
                workData = XorData(data);
            else
                workData = (byte[])data.Clone();

            _info("SecCfg Hw - HACC init");
            SejV3Init(encrypt, g_HACC_CFG_1, true);
            _info("SecCfg Hw - HACC run");
            uint[] psrc = BytesToDwords(workData, workData.Length / 4);
            byte[] dec = HaccV3Run(psrc);
            _info("SecCfg Hw - HACC terminate");
            SejTerminate();

            if (!encrypt && !noXor)
                dec = XorData(dec);

            return dec;
        }

        /// <summary>
        /// SP HACC internal — used by dev_kdf.
        /// mtkclient: sp_hacc_internal(buf, b_ac, user, b_do_lock, aes_type, b_en)
        /// </summary>
        public byte[] SpHaccInternal(byte[] buf, int user = 0, bool encrypt = true)
        {
            uint[] iv;
            switch (user)
            {
                case 0: iv = g_HACC_CFG_1; break;
                case 1: iv = g_HACC_CFG_2; break;
                case 3: iv = g_HACC_CFG_3; break;
                default: iv = g_HACC_CFG_1; break;
            }

            _info($"SP_HAcc{user} - HACC init");
            SejV3Init(encrypt, iv);
            _info($"SP_HAcc{user} - HACC run");
            uint[] psrc = BytesToDwords(buf, buf.Length / 4);
            byte[] result = HaccV3Run(psrc);
            _info($"SP_HAcc{user} - HACC terminate");
            SejTerminate();
            return result;
        }

        /// <summary>
        /// Device key derivation function — chains sp_hacc_internal calls.
        /// mtkclient: dev_kdf(buf, derivelen)
        /// </summary>
        public byte[] DevKdf(byte[] buf, int deriveLen = 16)
        {
            byte[] result = new byte[deriveLen];
            int pos = 0;
            for (int i = 0; i < deriveLen / 16; i++)
            {
                byte[] block = new byte[16];
                Buffer.BlockCopy(buf, i * 16, block, 0, Math.Min(16, buf.Length - i * 16));
                byte[] enc = SpHaccInternal(block, 0, true);
                if (enc != null)
                {
                    int copyLen = Math.Min(16, deriveLen - pos);
                    Buffer.BlockCopy(enc, 0, result, pos, copyLen);
                }
                pos += 16;
            }
            return result;
        }

        /// <summary>
        /// XOR data with CustomSeed pattern.
        /// mtkclient: xor_data(data)
        /// </summary>
        private byte[] XorData(byte[] data)
        {
            // CustomSeed from sej.py (256 bytes)
            byte[] seed = HexToBytes(
                "00be13bb95e218b53d07a089cb935255294f70d4088f3930350bc636cc49c902" +
                "5ece7a62c292853ef55b23a6ef7b7464c7f3f2a74ae919416d6b4d9c1d680965" +
                "5dd82d43d65999cf041a386e1c0f1e58849d8ed09ef07e6a9f0d7d3b8dad6cba" +
                "e4668a2fd53776c3d26f88b0bf617c8112b8b1a871d322d9513491e07396e163" +
                "8090055f4b8b9aa2f4ec24ebaeb917e81f468783ea771b278614cd5779a3ca50" +
                "df5cc5af0edc332e2b69b2b42154bcfffd0af13ce5a467abb7fb107fe794f928" +
                "da44b6db7215aa53bd0398e3403126fad1f7de2a56edfe474c5a06f8dd9bc0b3" +
                "422c45a9a132e64e48fcacf63f787560c4c89701d7c125118c20a5ee820c3a16");

            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ seed[i % seed.Length]);
            return result;
        }

        #endregion

        #region Helpers
        private static uint[] BytesToDwords(byte[] buf, int count)
        {
            uint[] result = new uint[count];
            for (int i = 0; i < count && i * 4 + 3 < buf.Length; i++)
                result[i] = BitConverter.ToUInt32(buf, i * 4);
            return result;
        }

        private static void WriteLE(byte[] buf, int offset, uint val)
        {
            if (offset + 4 > buf.Length) return;
            buf[offset] = (byte)(val & 0xFF);
            buf[offset + 1] = (byte)((val >> 8) & 0xFF);
            buf[offset + 2] = (byte)((val >> 16) & 0xFF);
            buf[offset + 3] = (byte)((val >> 24) & 0xFF);
        }

        private static byte[] HexToBytes(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
        #endregion
    }

    /// <summary>
    /// CQ-DMA (Command Queue DMA) engine — ported from mtkclient/Library/Hardware/cqdma.py
    /// Used by exploits to bypass blacklisted memory regions via DMA transfers.
    /// </summary>
    public class Cqdma
    {
        private readonly CryptoSetup _setup;
        private readonly Action<string> _info;
        private readonly Action<string> _error;

        // CQ-DMA register offsets
        private const uint CQDMA_INT_FLAG = 0x0;
        private const uint CQDMA_INT_EN = 0x4;
        private const uint CQDMA_EN = 0x8;
        private const uint CQDMA_RESET = 0xC;
        private const uint CQDMA_FLUSH = 0x14;
        private const uint CQDMA_SRC = 0x1C;
        private const uint CQDMA_DST = 0x20;
        private const uint CQDMA_LEN1 = 0x24;
        private const uint CQDMA_LEN2 = 0x28;
        private const uint CQDMA_SRC2 = 0x60;
        private const uint CQDMA_DST2 = 0x64;

        public Cqdma(CryptoSetup setup, Action<string> info, Action<string> error)
        {
            _setup = setup;
            _info = info ?? delegate { };
            _error = error ?? delegate { };
        }

        private void RegWrite(uint regOffset, uint value)
        {
            _setup.Write32(_setup.CqdmaBase + regOffset, value);
        }

        private uint RegRead(uint regOffset)
        {
            return _setup.Read32(_setup.CqdmaBase + regOffset, 1);
        }

        /// <summary>
        /// Read memory via CQ-DMA (bypasses blacklist).
        /// mtkclient: cqread32(addr, dwords)
        /// </summary>
        public byte[] CqRead32(uint addr, int dwords)
        {
            if (_setup.CqdmaBase == 0 || _setup.Read32 == null) return null;

            byte[] result = new byte[dwords * 4];
            uint dstAddr = _setup.ApDmaMem;

            for (int i = 0; i < dwords; i++)
            {
                RegWrite(CQDMA_SRC, addr + (uint)(i * 4));
                RegWrite(CQDMA_DST, dstAddr);
                RegWrite(CQDMA_LEN1, 4);
                RegWrite(CQDMA_EN, 1);

                // Wait for transfer complete
                int timeout = 1000;
                while ((RegRead(CQDMA_EN) & 1) != 0 && timeout-- > 0) { }

                uint val = _setup.Read32(dstAddr, 1);
                result[i * 4] = (byte)(val & 0xFF);
                result[i * 4 + 1] = (byte)((val >> 8) & 0xFF);
                result[i * 4 + 2] = (byte)((val >> 16) & 0xFF);
                result[i * 4 + 3] = (byte)((val >> 24) & 0xFF);
            }
            return result;
        }

        /// <summary>
        /// Write memory via CQ-DMA (bypasses blacklist).
        /// mtkclient: cqwrite32(addr, dwords)
        /// </summary>
        public void CqWrite32(uint addr, uint[] values)
        {
            if (_setup.CqdmaBase == 0 || _setup.Write32 == null) return;

            uint dstAddr = _setup.ApDmaMem;

            for (int i = 0; i < values.Length; i++)
            {
                _setup.Write32(dstAddr, values[i]);
                RegWrite(CQDMA_SRC, dstAddr);
                RegWrite(CQDMA_DST, addr + (uint)(i * 4));
                RegWrite(CQDMA_LEN1, 4);
                RegWrite(CQDMA_EN, 1);

                int timeout = 1000;
                while ((RegRead(CQDMA_EN) & 1) != 0 && timeout-- > 0) { }

                _setup.Write32(dstAddr, 0xCAFEBABE);
            }
        }

        /// <summary>
        /// Read memory, optionally via CQ-DMA.
        /// mtkclient: mem_read(addr, length, ucqdma)
        /// </summary>
        public byte[] MemRead(uint addr, int length, bool useCqdma = false)
        {
            int dwords = (length + 3) / 4;

            if (useCqdma)
            {
                byte[] data = CqRead32(addr, dwords);
                if (data != null && data.Length > length)
                {
                    byte[] trimmed = new byte[length];
                    Buffer.BlockCopy(data, 0, trimmed, 0, length);
                    return trimmed;
                }
                return data;
            }
            else
            {
                byte[] result = new byte[length];
                for (int i = 0; i < dwords && i * 4 < length; i++)
                {
                    uint val = _setup.Read32(addr + (uint)(i * 4), 1);
                    int copyLen = Math.Min(4, length - i * 4);
                    byte[] valBytes = BitConverter.GetBytes(val);
                    Buffer.BlockCopy(valBytes, 0, result, i * 4, copyLen);
                }
                return result;
            }
        }

        /// <summary>
        /// Write memory, optionally via CQ-DMA.
        /// mtkclient: mem_write(addr, data, ucqdma)
        /// </summary>
        public void MemWrite(uint addr, byte[] data, bool useCqdma = false)
        {
            int padded = data.Length;
            if (padded % 4 != 0) padded += (4 - padded % 4);
            byte[] aligned = new byte[padded];
            Buffer.BlockCopy(data, 0, aligned, 0, data.Length);

            uint[] dwords = new uint[padded / 4];
            for (int i = 0; i < dwords.Length; i++)
                dwords[i] = BitConverter.ToUInt32(aligned, i * 4);

            if (useCqdma)
            {
                CqWrite32(addr, dwords);
            }
            else
            {
                for (int i = 0; i < dwords.Length; i++)
                    _setup.Write32(addr + (uint)(i * 4), dwords[i]);
            }
        }

        /// <summary>
        /// Disable BROM range blacklist via CQ-DMA writes.
        /// mtkclient: disable_range_blacklist()
        /// </summary>
        public void DisableRangeBlacklist()
        {
            if (_setup.Blacklist == null || _setup.CqdmaBase == 0) return;

            _info("Disabling bootrom range checks via CQ-DMA...");
            foreach (var field in _setup.Blacklist)
            {
                uint addr = field.Item1;
                uint value = field.Item2;
                CqWrite32(addr, new uint[] { value });
            }
        }
    }
}
