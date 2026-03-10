// mtkclient port: mtk_preloader.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Collections.Generic;
using System.Threading;
using SakuraEDL.MediaTek.Auth;
using SakuraEDL.MediaTek.Connection;

namespace SakuraEDL.MediaTek.Protocol
{
    #region Constants

    public static class PreloaderConstants
    {
        public const uint USBDL_BIT_EN = 0x00000001;
        public const uint USBDL_BROM = 0x00000002;
        public const uint USBDL_TIMEOUT_MASK = 0x0000FFFC;
        public const uint USBDL_TIMEOUT_MAX = USBDL_TIMEOUT_MASK >> 2;
        public const uint USBDL_MAGIC = 0x444C0000;
        public const ushort MISC_LOCK_KEY_MAGIC = 0xAD98;
    }

    #endregion

    #region Preloader Commands

    public static class PreloaderCmd
    {
        // if CFG_PRELOADER_AS_DA
        public const byte SEND_PARTITION_DATA = 0x70;
        public const byte JUMP_TO_PARTITION = 0x71;
        public const byte CHECK_USB_CMD = 0x72;
        public const byte STAY_STILL = 0x80;
        public const byte CMD_88 = 0x88;
        public const byte CMD_READ16_A2 = 0xA2;

        public const byte I2C_INIT = 0xB0;
        public const byte I2C_DEINIT = 0xB1;
        public const byte I2C_WRITE8 = 0xB2;
        public const byte I2C_READ8 = 0xB3;
        public const byte I2C_SET_SPEED = 0xB4;
        public const byte I2C_INIT_EX = 0xB6;
        public const byte I2C_DEINIT_EX = 0xB7;
        public const byte I2C_WRITE8_EX = 0xB8;
        public const byte I2C_READ8_EX = 0xB9;
        public const byte I2C_SET_SPEED_EX = 0xBA;
        public const byte GET_MAUI_FW_VER = 0xBF;

        public const byte OLD_SLA_SEND_AUTH = 0xC1;
        public const byte OLD_SLA_GET_RN = 0xC2;
        public const byte OLD_SLA_VERIFY_RN = 0xC3;
        public const byte PWR_INIT = 0xC4;
        public const byte PWR_DEINIT = 0xC5;
        public const byte PWR_READ16 = 0xC6;
        public const byte PWR_WRITE16 = 0xC7;
        public const byte CMD_C8 = 0xC8;

        public const byte READ16 = 0xD0;
        public const byte READ32 = 0xD1;
        public const byte WRITE16 = 0xD2;
        public const byte WRITE16_NO_ECHO = 0xD3;
        public const byte WRITE32 = 0xD4;
        public const byte JUMP_DA = 0xD5;
        public const byte JUMP_BL = 0xD6;
        public const byte SEND_DA = 0xD7;
        public const byte GET_TARGET_CONFIG = 0xD8;
        public const byte SEND_ENV_PREPARE = 0xD9;
        public const byte BROM_REGISTER_ACCESS = 0xDA;
        public const byte UART1_LOG_EN = 0xDB;
        public const byte UART1_SET_BAUDRATE = 0xDC;
        public const byte BROM_DEBUGLOG = 0xDD;
        public const byte JUMP_DA64 = 0xDE;
        public const byte GET_BROM_LOG_NEW = 0xDF;

        public const byte SEND_CERT = 0xE0;
        public const byte GET_ME_ID = 0xE1;
        public const byte SEND_AUTH = 0xE2;
        public const byte SLA = 0xE3;
        public const byte CMD_E4 = 0xE4;
        public const byte CMD_E5 = 0xE5;
        public const byte CMD_E6 = 0xE6;
        public const byte GET_SOC_ID = 0xE7;
        public const byte CMD_E8 = 0xE8;
        public const byte ZEROIZATION = 0xF0;
        public const byte CMD_FA = 0xFA;
        public const byte GET_PL_CAP = 0xFB;
        public const byte GET_HW_SW_VER = 0xFC;
        public const byte GET_HW_CODE = 0xFD;
        public const byte GET_BL_VER = 0xFE;
        public const byte GET_VERSION = 0xFF;
    }

    #endregion

    #region Preloader Capability

    [Flags]
    public enum PlCap : uint
    {
        PL_CAP0_XFLASH_SUPPORT = 0x1,
        PL_CAP0_MEID_SUPPORT = 0x2,
        PL_CAP0_SOCID_SUPPORT = 0x4
    }

    #endregion

    #region Response

    public enum PreloaderRsp : byte
    {
        NONE = 0x00,
        CONF = 0x69,
        STOP = 0x96,
        ACK = 0x5A,
        NACK = 0xA5
    }

    #endregion

    #region Target Config

    public class TargetConfig
    {
        public bool Sbc { get; set; }
        public bool Sla { get; set; }
        public bool Daa { get; set; }
        public bool SwJtag { get; set; }
        public bool Epp { get; set; }
        public bool Cert { get; set; }
        public bool MemRead { get; set; }
        public bool MemWrite { get; set; }
        public bool CmdC8 { get; set; }
        public uint RawValue { get; set; }
    }

    #endregion

    /// <summary>
    /// Preloader protocol — ported from mtkclient/Library/mtk_preloader.py (45KB)
    /// Handles BROM/Preloader communication, device info, DA upload, SLA auth.
    /// </summary>
    public class Preloader
    {
        private readonly Port _port;
        private readonly ErrorHandler _eh;
        private readonly Action<string> _info;
        private readonly Action<string> _debug;
        private readonly Action<string> _warning;
        private readonly Action<string> _error;

        // Config state (populated during init)
        public ushort HwCode { get; set; }
        public ushort HwVer { get; set; }
        public ushort HwSubCode { get; set; }
        public ushort SwVer { get; set; }
        public byte BlVer { get; set; }
        public byte BromVer { get; set; }
        public bool IsBrom { get; set; }
        public bool IsIot { get; set; }
        public byte[] Meid { get; set; }
        public byte[] SocId { get; set; }
        public TargetConfig TargetCfg { get; set; }
        public uint[] PlCapability { get; set; }

        public Preloader(Port port, Action<string> info = null, Action<string> debug = null,
                         Action<string> warning = null, Action<string> error = null)
        {
            _port = port;
            _eh = new ErrorHandler();
            _info = info ?? delegate { };
            _debug = debug ?? delegate { };
            _warning = warning ?? delegate { };
            _error = error ?? delegate { };
        }

        #region Utility

        public static uint CalcXFlashChecksum(byte[] data)
        {
            uint checksum = 0;
            int pos = 0;
            for (int i = 0; i < data.Length / 4; i++)
            {
                checksum += BitConverter.ToUInt32(data, i * 4);
                pos += 4;
            }
            if (data.Length % 4 != 0)
            {
                for (int i = 0; i < 4 - (data.Length % 4); i++)
                {
                    checksum += data[pos];
                    pos++;
                }
            }
            return checksum;
        }

        private byte[] PackBE32(uint value)
        {
            byte[] buf = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(buf);
            return buf;
        }

        private uint UnpackBE32(byte[] data, int offset = 0)
        {
            if (BitConverter.IsLittleEndian)
            {
                return (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                              (data[offset + 2] << 8) | data[offset + 3]);
            }
            return BitConverter.ToUInt32(data, offset);
        }

        private ushort UnpackBE16(byte[] data, int offset = 0)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private byte[] PackBE16(ushort value)
        {
            return new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) };
        }

        private byte[] PackLE32(uint value)
        {
            return BitConverter.GetBytes(value);
        }

        private bool Echo(byte cmd)
        {
            return _port.Echo(new byte[] { cmd });
        }

        private bool Echo(uint value)
        {
            return _port.Echo(PackBE32(value));
        }

        private bool Echo(byte[] data)
        {
            return _port.Echo(data);
        }

        #endregion

        #region Init (mtkclient: Preloader.init)

        /// <summary>
        /// Initialize Preloader: handshake, read hw info, read MEID/SOCID, handle SLA.
        /// </summary>
        public bool Init(bool display = true, bool readSocId = false)
        {
            _info("Status: Waiting for PreLoader VCOM, please reconnect device to brom mode");

            if (!_port.Handshake(maxTries: 100))
            {
                _error("Handshake failed.");
                return false;
            }

            // GET_HW_CODE (0xFD)
            if (!Echo(PreloaderCmd.GET_HW_CODE))
            {
                if (!Echo(PreloaderCmd.GET_HW_CODE))
                {
                    _error("Sync error. Please power off the device and retry.");
                    return false;
                }
            }

            uint? val = _port.Rdword();
            if (val.HasValue)
            {
                HwCode = (ushort)((val.Value >> 16) & 0xFFFF);
                HwVer = (ushort)(val.Value & 0xFFFF);
                _info("Detected regular mode.");
            }
            else
            {
                IsIot = true;
                HwVer = (ushort)ReadA2(0x80000000);
                HwCode = (ushort)ReadA2(0x80000008);
                HwSubCode = (ushort)ReadA2(0x8000000C);
                uint swval = Read32(0xA01C0108);
                SwVer = (ushort)((swval & 0xFFFF0000) >> 16);
                _info("Detected IoT mode.");
            }

            if (display)
            {
                _info($"\tHW Code:\t\t0x{HwCode:X4}");
                _info($"\tHW version:\t\t0x{HwVer:X4}");
            }

            // GET_BL_VER (0xFE) — also detects BROM vs preloader
            BlVer = GetBlVer();

            // GET_TARGET_CONFIG (0xD8)
            TargetCfg = GetTargetConfig(display);

            // GET_PL_CAP (0xFB)
            PlCapability = GetPlCap();

            // GET_ME_ID (0xE1)
            Meid = GetMeid();
            if (Meid != null && display)
                _info($"\tME_ID:\t\t\t{BitConverter.ToString(Meid).Replace("-", "")}");

            // GET_SOC_ID (0xE7)
            if (readSocId)
            {
                SocId = GetSocId();
                if (SocId != null && SocId.Length >= 16 && display)
                    _info($"\tSOC_ID:\t\t\t{BitConverter.ToString(SocId).Replace("-", "")}");
            }

            // Handle SLA if required
            if (TargetCfg != null && TargetCfg.Sla && IsBrom)
            {
                if (!HandleSla(isBrom: true))
                {
                    _warning("SLA authentication failed.");
                }
            }

            return true;
        }

        #endregion

        #region Read/Write Memory

        public uint ReadA2(uint addr, uint dwords = 1)
        {
            if (Echo(PreloaderCmd.CMD_READ16_A2))
            {
                if (Echo(addr))
                {
                    Echo(dwords);
                    byte[] data = _port.UsbRead(2);
                    if (data != null && data.Length == 2)
                        return UnpackBE16(data);
                }
            }
            return 0;
        }

        public uint Read32(uint addr, uint dwords = 1)
        {
            return Read(addr, dwords, 32);
        }

        public ushort Read16(uint addr, uint dwords = 1)
        {
            return (ushort)Read(addr, dwords, 16);
        }

        private uint Read(uint addr, uint dwords, int length)
        {
            byte cmd = length == 16 ? PreloaderCmd.READ16 : PreloaderCmd.READ32;
            if (Echo(cmd))
            {
                if (Echo(addr))
                {
                    bool ack = Echo(dwords);
                    ushort? status = _port.Rword();
                    if (ack && status.HasValue && status.Value <= 0xFF)
                    {
                        uint? result;
                        if (length == 32)
                            result = _port.Rdword();
                        else
                            result = _port.Rword();

                        ushort? status2 = _port.Rword();
                        if (status2.HasValue && status2.Value <= 0xFF)
                            return result ?? 0;
                    }
                    else if (status.HasValue)
                    {
                        _error($"Read error at 0x{addr:X}: {_eh.StatusToString(status.Value)}");
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Read block of memory (multiple dwords).
        /// </summary>
        public byte[] ReadMem(uint addr, int length)
        {
            byte[] result = new byte[length];
            int pos = 0;
            while (pos < length)
            {
                uint val = Read32(addr + (uint)pos);
                byte[] valBytes = BitConverter.GetBytes(val);
                int copyLen = Math.Min(4, length - pos);
                Buffer.BlockCopy(valBytes, 0, result, pos, copyLen);
                pos += 4;
            }
            return result;
        }

        public bool Write32(uint addr, uint value)
        {
            return Write(addr, new uint[] { value }, 32);
        }

        public bool Write16(uint addr, ushort value)
        {
            return Write(addr, new uint[] { value }, 16);
        }

        private bool Write(uint addr, uint[] values, int length)
        {
            byte cmd = length == 16 ? PreloaderCmd.WRITE16 : PreloaderCmd.WRITE32;

            if (Echo(cmd))
            {
                if (Echo(addr))
                {
                    bool ack = Echo((uint)values.Length);
                    ushort? status = _port.Rword();
                    if (status.HasValue && status.Value > 0xFF)
                    {
                        _error($"Write error at 0x{addr:X}: {_eh.StatusToString(status.Value)}");
                        return false;
                    }
                    if (ack && status.HasValue && status.Value <= 3)
                    {
                        foreach (uint val in values)
                        {
                            byte[] packed = length == 16 ? PackBE16((ushort)val) : PackBE32(val);
                            if (!Echo(packed)) break;
                        }
                        ushort? status2 = _port.Rword();
                        if (status2.HasValue && status2.Value <= 0xFF)
                            return true;
                        _error($"Write error at 0x{addr:X}: {_eh.StatusToString(status2 ?? 0xFFFF)}");
                    }
                }
            }
            return false;
        }

        public void WriteMem(uint addr, byte[] data)
        {
            for (int i = 0; i < data.Length; i += 4)
            {
                byte[] chunk = new byte[4];
                int copyLen = Math.Min(4, data.Length - i);
                Buffer.BlockCopy(data, i, chunk, 0, copyLen);
                Write32(addr + (uint)i, BitConverter.ToUInt32(chunk, 0));
            }
        }

        #endregion

        #region Device Info

        public byte GetBromVer()
        {
            if (_port.UsbWrite(new byte[] { PreloaderCmd.GET_VERSION }))
            {
                byte[] res = _port.UsbRead(1);
                if (res != null && res.Length == 1)
                {
                    BromVer = res[0];
                    return BromVer;
                }
            }
            return 0xFF;
        }

        public byte GetBlVer()
        {
            if (_port.UsbWrite(new byte[] { PreloaderCmd.GET_BL_VER }))
            {
                byte[] res = _port.UsbRead(1);
                if (res != null && res.Length == 1)
                {
                    if (res[0] == PreloaderCmd.GET_BL_VER)
                    {
                        _info("BROM mode detected.");
                        IsBrom = true;
                    }
                    BlVer = res[0];
                    return BlVer;
                }
            }
            return 0xFF;
        }

        public TargetConfig GetTargetConfig(bool display = true)
        {
            if (Echo(PreloaderCmd.GET_TARGET_CONFIG))
            {
                byte[] data = _port.UsbRead(6);
                if (data != null && data.Length == 6)
                {
                    uint targetConfig = UnpackBE32(data, 0);
                    ushort status = UnpackBE16(data, 4);

                    var cfg = new TargetConfig
                    {
                        RawValue = targetConfig,
                        Sbc = (targetConfig & 0x1) != 0,
                        Sla = (targetConfig & 0x2) != 0,
                        Daa = (targetConfig & 0x4) != 0,
                        SwJtag = (targetConfig & 0x6) != 0,
                        Epp = (targetConfig & 0x8) != 0,
                        Cert = (targetConfig & 0x10) != 0,
                        MemRead = (targetConfig & 0x20) != 0,
                        MemWrite = (targetConfig & 0x40) != 0,
                        CmdC8 = (targetConfig & 0x80) != 0
                    };

                    if (display)
                    {
                        _info($"\tTarget config:\t\t0x{targetConfig:X8}");
                        _info($"\t  SBC enabled:\t\t{cfg.Sbc}");
                        _info($"\t  SLA enabled:\t\t{cfg.Sla}");
                        _info($"\t  DAA enabled:\t\t{cfg.Daa}");
                        _info($"\t  Mem read auth:\t{cfg.MemRead}");
                        _info($"\t  Mem write auth:\t{cfg.MemWrite}");
                    }

                    if (status > 0xFF)
                    {
                        _error("Get Target Config error");
                        return cfg;
                    }
                    TargetCfg = cfg;
                    return cfg;
                }
            }
            _warning("CMD Get_Target_Config not supported.");
            return new TargetConfig();
        }

        public ushort GetHwCode()
        {
            byte[] res = _port.MtkCmd(new byte[] { PreloaderCmd.GET_HW_CODE }, 4);
            if (res != null && res.Length >= 4)
            {
                HwCode = UnpackBE16(res, 0);
                HwVer = UnpackBE16(res, 2);
                return HwCode;
            }
            return 0;
        }

        public uint[] GetPlCap()
        {
            byte[] res = _port.MtkCmd(new byte[] { PreloaderCmd.GET_PL_CAP }, 8);
            if (res != null && res.Length >= 8)
            {
                PlCapability = new uint[] { UnpackBE32(res, 0), UnpackBE32(res, 4) };
                return PlCapability;
            }
            return new uint[] { 0, 0 };
        }

        public byte[] GetMeid()
        {
            if (_port.UsbWrite(new byte[] { PreloaderCmd.GET_BL_VER }))
            {
                byte[] res = _port.UsbRead(1);
                if (res == null || res.Length == 0) return null;

                if (res[0] == PreloaderCmd.GET_BL_VER)
                {
                    // BROM mode
                    IsBrom = true;
                    return ReadMeidInternal();
                }
                else if (res[0] > 2)
                {
                    // Preloader mode
                    IsBrom = false;
                    return ReadMeidInternal();
                }
                IsBrom = false;
            }
            return null;
        }

        private byte[] ReadMeidInternal()
        {
            _port.UsbWrite(new byte[] { PreloaderCmd.GET_ME_ID });
            byte[] echoRes = _port.UsbRead(1);
            if (echoRes != null && echoRes[0] == PreloaderCmd.GET_ME_ID)
            {
                byte[] lenBuf = _port.UsbRead(4);
                if (lenBuf != null)
                {
                    uint length = UnpackBE32(lenBuf);
                    Meid = _port.UsbRead((int)length);
                    byte[] statusBuf = _port.UsbRead(2);
                    if (statusBuf != null)
                    {
                        ushort status = (ushort)(statusBuf[0] | (statusBuf[1] << 8)); // LE
                        if (status == 0)
                            return Meid;
                        _error($"Error on get_meid: {_eh.StatusToString(status)}");
                    }
                }
            }
            return null;
        }

        public byte[] GetSocId()
        {
            if (_port.UsbWrite(new byte[] { PreloaderCmd.GET_BL_VER }))
            {
                byte[] res = _port.UsbRead(1);
                if (res == null) return null;

                _port.UsbWrite(new byte[] { PreloaderCmd.GET_SOC_ID });
                byte[] echoRes = _port.UsbRead(1);
                if (echoRes != null && echoRes[0] == PreloaderCmd.GET_SOC_ID)
                {
                    byte[] lenBuf = _port.UsbRead(4);
                    if (lenBuf != null)
                    {
                        uint length = UnpackBE32(lenBuf);
                        SocId = _port.UsbRead((int)length);
                        byte[] statusBuf = _port.UsbRead(2);
                        if (statusBuf != null)
                        {
                            ushort status = (ushort)(statusBuf[0] | (statusBuf[1] << 8));
                            if (status == 0)
                                return SocId;
                        }
                    }
                }
            }
            return null;
        }

        #endregion

        #region DA Upload (mtkclient: send_da, jump_da)

        /// <summary>
        /// Prepare data for upload: compute checksum and pad.
        /// mtkclient: prepare_data(data, sigdata, maxsize)
        /// </summary>
        public static (ushort checksum, byte[] data) PrepareData(byte[] data, byte[] sigData, int maxSize)
        {
            ushort genChksum = 0;
            byte[] combined;

            if (maxSize > 0 && data.Length > maxSize)
            {
                combined = new byte[maxSize + sigData.Length];
                Buffer.BlockCopy(data, 0, combined, 0, maxSize);
                Buffer.BlockCopy(sigData, 0, combined, maxSize, sigData.Length);
            }
            else
            {
                combined = new byte[data.Length + sigData.Length];
                Buffer.BlockCopy(data, 0, combined, 0, data.Length);
                Buffer.BlockCopy(sigData, 0, combined, data.Length, sigData.Length);
            }

            // Pad to even length
            if (combined.Length % 2 != 0)
            {
                Array.Resize(ref combined, combined.Length + 1);
            }

            for (int x = 0; x < combined.Length; x += 2)
            {
                genChksum ^= (ushort)(combined[x] | (combined[x + 1] << 8));
            }

            return (genChksum, combined);
        }

        /// <summary>
        /// Upload data block to device.
        /// mtkclient: upload_data(data, gen_chksum)
        /// </summary>
        public bool UploadData(byte[] data, ushort genChksum)
        {
            int bytesToWrite = data.Length;
            int maxInSize = 0x400;
            int pos = 0;

            while (bytesToWrite > 0)
            {
                int sz = Math.Min(bytesToWrite, maxInSize);
                byte[] chunk = new byte[sz];
                Buffer.BlockCopy(data, pos, chunk, 0, sz);
                _port.UsbWrite(chunk);
                bytesToWrite -= sz;
                pos += sz;
                if (pos % 0x2000 == 0)
                    _port.UsbWrite(new byte[0]);
            }

            _port.UsbWrite(new byte[0]);
            Thread.Sleep(120);

            try
            {
                // Read checksum + status (2 * uint16)
                byte[] csData = _port.UsbRead(2);
                byte[] stData = _port.UsbRead(2);
                if (csData != null && stData != null)
                {
                    ushort checksum = UnpackBE16(csData);
                    ushort status = UnpackBE16(stData);

                    if (genChksum != checksum && checksum != 0)
                        _warning("Checksum of upload doesn't match!");

                    if (status <= 0xFF)
                        return true;

                    _error($"upload_data failed: {_eh.StatusToString(status)}");
                }
            }
            catch (Exception ex)
            {
                _error($"upload_data resp error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Send DA binary to device.
        /// mtkclient: send_da(address, size, sig_len, dadata)
        /// </summary>
        public bool SendDa(uint address, int size, int sigLen, byte[] daData)
        {
            _info("Sending DA...");

            byte[] mainData = new byte[daData.Length - sigLen];
            byte[] sigData = new byte[sigLen];
            Buffer.BlockCopy(daData, 0, mainData, 0, mainData.Length);
            if (sigLen > 0)
                Buffer.BlockCopy(daData, daData.Length - sigLen, sigData, 0, sigLen);

            var (genChksum, data) = PrepareData(mainData, sigData, size);

            if (!Echo(PreloaderCmd.SEND_DA))
            {
                _error("Error on DA_Send cmd");
                return false;
            }
            if (!Echo(address))
            {
                _error("Error on DA_Send address");
                return false;
            }
            if (!Echo((uint)data.Length))
            {
                _error("Error on DA_Send size");
                return false;
            }
            if (!Echo((uint)sigLen))
            {
                _error("Error on DA_Send sig_len");
                return false;
            }

            ushort? status = _port.Rword();
            if (!status.HasValue) return false;

            if (status.Value == 0x1D0D)
            {
                _info("SLA required...");
                // SLA flow would be triggered here
            }

            if (status.Value != 0)
            {
                _error($"DA_Send status error: {_eh.StatusToString(status.Value)}");
                return false;
            }

            return UploadData(data, genChksum);
        }

        /// <summary>
        /// Jump to DA address.
        /// mtkclient: jump_da(addr)
        /// </summary>
        public bool JumpDa(uint addr)
        {
            _info($"Jumping to 0x{addr:X}");
            if (Echo(PreloaderCmd.JUMP_DA))
            {
                _port.UsbWrite(PackBE32(addr));
                try
                {
                    uint? resAddr = _port.Rdword();
                    if (resAddr.HasValue && resAddr.Value == addr)
                    {
                        ushort? status = _port.Rword();
                        Thread.Sleep(100);
                        if (status.HasValue && status.Value == 0)
                        {
                            _info($"Jumping to 0x{addr:X}: ok.");
                            return true;
                        }
                        _error($"Jump_DA status error: {_eh.StatusToString(status ?? 0xFFFF)}");
                    }
                }
                catch (Exception ex)
                {
                    _error($"Jump_DA error: {ex.Message}");
                }
            }
            return false;
        }

        /// <summary>
        /// Jump to DA in 64-bit mode.
        /// mtkclient: jump_da64(addr)
        /// </summary>
        public bool JumpDa64(uint addr)
        {
            if (Echo(PreloaderCmd.JUMP_DA64))
            {
                _port.UsbWrite(PackBE32(addr));
                try
                {
                    uint? resAddr = _port.Rdword();
                    if (resAddr.HasValue && resAddr.Value == addr)
                    {
                        Echo(new byte[] { 0x01 }); // 64-bit flag
                        ushort? status = _port.Rword();
                        if (status.HasValue && status.Value == 0)
                            return true;
                        _error($"Jump_DA64 status error: {_eh.StatusToString(status ?? 0xFFFF)}");
                    }
                }
                catch (Exception ex)
                {
                    _error($"Jump_DA64 error: {ex.Message}");
                }
            }
            return false;
        }

        #endregion

        #region SLA Authentication (mtkclient: handle_sla)

        /// <summary>
        /// Handle SLA challenge/response authentication.
        /// mtkclient: handle_sla(func, isbrom)
        /// Tries BromSlaKeys first (for BROM-level auth), then DaSlaKeys as fallback.
        /// </summary>
        public bool HandleSla(bool isBrom = true)
        {
            if (!isBrom)
                return true; // DA mode doesn't need BROM SLA

            // Try BROM SLA keys first (36 keys), then DA SLA keys (8 keys) as fallback
            foreach (var key in SlaKeys.BromSlaKeys)
            {
                int result = TrySlaKey(key);
                if (result == 1) return true;   // success
                if (result == -1) return false;  // fatal error
                // result == 0: try next key
            }

            foreach (var key in SlaKeys.DaSlaKeys)
            {
                int result = TrySlaKey(key);
                if (result == 1) return true;
                if (result == -1) return false;
                // result == 0: try next key
            }

            return false;
        }

        /// <summary>
        /// Attempt SLA authentication with a single key.
        /// Returns: 1 = success, 0 = key rejected (try next), -1 = fatal error.
        /// </summary>
        private int TrySlaKey(SlaKey key)
        {
            if (!Echo(PreloaderCmd.SLA))
                return 0;

            ushort? status = _port.Rword();
            if (!status.HasValue) return -1;

            if (status.Value == 0x7017)
                return 1; // Already authenticated

            if (status.Value > 0xFF)
            {
                _error($"Send auth error: {_eh.StatusToString(status.Value)}");
                return -1;
            }

            // Read challenge
            uint? challengeLength = _port.Rdword();
            if (!challengeLength.HasValue) return -1;

            byte[] challenge = _port.UsbRead((int)challengeLength.Value);
            if (challenge == null) return -1;

            // Generate response using current key (N as modulus, D as exponent)
            byte[] response = Sla.GenerateBromSlaChallenge(
                challenge, key.GetN(), key.GetD());

            int respLen = response.Length;

            // Send response length (little-endian)
            _port.UsbWrite(PackLE32((uint)respLen));

            uint? rlen = _port.Rdword();
            if (!rlen.HasValue || rlen.Value != respLen)
                return 0; // length mismatch, try next key

            ushort? status2 = _port.Rword();
            if (!status2.HasValue || status2.Value > 0xFF)
            {
                _error($"Send SLA challenge response len error: {_eh.StatusToString(status2 ?? 0xFFFF)}");
                return -1;
            }

            // Send actual response
            byte[] respData = new byte[respLen];
            Buffer.BlockCopy(response, 0, respData, 0, respLen);
            _port.UsbWrite(respData);

            uint? status3 = _port.Rdword();
            if (status3.HasValue && status3.Value < 0xFF)
            {
                _info("SLA authentication successful.");
                return 1;
            }
            _debug($"SLA key '{key.Name}' rejected: {_eh.StatusToString(status3 ?? 0xFFFF)}");
            return 0; // try next key
        }

        #endregion

        #region Auth / Cert (mtkclient: send_auth, send_root_cert)

        public bool SendAuth(byte[] authData)
        {
            if (Echo(PreloaderCmd.SEND_AUTH))
            {
                _port.UsbWrite(PackBE32((uint)authData.Length));
                ushort? lenAck = _port.Rword();
                if (lenAck.HasValue && lenAck.Value == 0)
                {
                    _port.UsbWrite(authData);
                    ushort? status = _port.Rword();
                    if (status.HasValue && status.Value == 0)
                        return true;
                    _error($"Send auth error: {_eh.StatusToString(status ?? 0xFFFF)}");
                }
            }
            return false;
        }

        public bool SendRootCert(byte[] certData)
        {
            if (Echo(PreloaderCmd.SEND_CERT))
            {
                _port.UsbWrite(PackBE32((uint)certData.Length));
                ushort? lenAck = _port.Rword();
                if (lenAck.HasValue && lenAck.Value == 0)
                {
                    _port.UsbWrite(certData);
                    ushort? status = _port.Rword();
                    if (status.HasValue && status.Value == 0)
                        return true;
                    _error($"Send cert error: {_eh.StatusToString(status ?? 0xFFFF)}");
                }
            }
            return false;
        }

        #endregion

        #region Watchdog / Reset (mtkclient: setreg_disablewatchdogtimer, reset_to_brom)

        public void DisableWatchdog(uint watchdogAddr, uint watchdogValue = 0x22000064)
        {
            Write32(watchdogAddr, watchdogValue);
        }

        public void ResetToBrom(uint miscLock, bool enable = true, int timeout = 0)
        {
            uint usbdlreg = 0;

            timeout = timeout == 0 ? (int)PreloaderConstants.USBDL_TIMEOUT_MAX : timeout / 1000;
            timeout <<= 2;
            timeout &= (int)PreloaderConstants.USBDL_TIMEOUT_MASK;

            usbdlreg |= (uint)timeout;
            if (enable)
                usbdlreg |= PreloaderConstants.USBDL_BIT_EN;
            usbdlreg &= ~PreloaderConstants.USBDL_BROM;
            usbdlreg |= PreloaderConstants.USBDL_MAGIC;

            uint rstCon = miscLock + 8;
            uint usbdlFlag = miscLock - 0x20;
            Write32(miscLock, PreloaderConstants.MISC_LOCK_KEY_MAGIC);
            Write32(rstCon, 1);
            Write32(miscLock, 0);
            Write32(usbdlFlag, usbdlreg);
        }

        #endregion

        #region BROM Register Access (mtkclient: brom_register_access)

        /// <summary>
        /// BROM register read/write via CMD 0xDA.
        /// Used by Kamakiri2 exploit.
        /// </summary>
        public byte[] BromRegisterAccess(uint address, int length, byte[] data = null,
                                          bool checkStatus = true, int mode = -1)
        {
            if (mode == -1)
                mode = data == null ? 0 : 1;

            if (_port.Echo(new byte[] { PreloaderCmd.BROM_REGISTER_ACCESS }))
            {
                _port.Echo(PackBE32((uint)mode));
                _port.Echo(PackBE32(address));
                _port.Echo(PackBE32((uint)length));

                byte[] statusBuf = _port.UsbRead(2);
                if (statusBuf == null)
                    throw new Exception("BromRegisterAccess: no status response");

                ushort status = (ushort)(statusBuf[0] | (statusBuf[1] << 8)); // LE
                if (status != 0)
                {
                    if (status == 0x1A1D)
                        throw new Exception("Kamakiri2 failed, cache issue :(");
                    throw new Exception($"BromRegisterAccess error: {_eh.StatusToString(status)}");
                }

                if (mode == 0 || mode == 2)
                {
                    data = _port.UsbRead(length);
                }
                else
                {
                    byte[] writeData = new byte[length];
                    Buffer.BlockCopy(data, 0, writeData, 0, Math.Min(data.Length, length));
                    _port.UsbWrite(writeData);
                }

                if (checkStatus)
                {
                    byte[] statusBuf2 = _port.UsbRead(2);
                    if (statusBuf2 != null)
                    {
                        ushort status2 = (ushort)(statusBuf2[0] | (statusBuf2[1] << 8));
                        if (status2 != 0)
                            throw new Exception($"BromRegisterAccess status2 error: {_eh.StatusToString(status2)}");
                    }
                }

                return data;
            }
            return null;
        }

        #endregion

        #region BROM Log

        public byte[] GetBromLog()
        {
            if (Echo(PreloaderCmd.BROM_DEBUGLOG))
            {
                uint? length = _port.Rdword();
                if (length.HasValue)
                    return _port.UsbRead((int)length.Value);
            }
            return null;
        }

        public byte[] GetBromLogNew()
        {
            if (Echo(PreloaderCmd.GET_BROM_LOG_NEW))
            {
                uint? length = _port.Rdword();
                if (length.HasValue)
                {
                    byte[] logData = _port.UsbRead((int)length.Value);
                    ushort? status = _port.Rword();
                    if (status.HasValue && status.Value == 0)
                        return logData;
                }
            }
            return null;
        }

        #endregion

        #region Misc

        public bool JumpBl()
        {
            if (Echo(PreloaderCmd.JUMP_BL))
            {
                ushort? status = _port.Rword();
                if (status.HasValue && status.Value <= 0xFF)
                {
                    ushort? status2 = _port.Rword();
                    return status2.HasValue && status2.Value <= 0xFF;
                }
            }
            return false;
        }

        public bool JumpToPartition(string partitionName)
        {
            byte[] nameBytes = new byte[64];
            byte[] src = System.Text.Encoding.UTF8.GetBytes(partitionName);
            Buffer.BlockCopy(src, 0, nameBytes, 0, Math.Min(src.Length, 64));

            if (Echo(PreloaderCmd.JUMP_TO_PARTITION))
            {
                _port.UsbWrite(nameBytes);
                ushort? status = _port.Rword();
                return status.HasValue && status.Value <= 0xFF;
            }
            return false;
        }

        public void RunExtCmd(byte cmd = 0xB1)
        {
            _port.UsbWrite(new byte[] { PreloaderCmd.CMD_C8 });
            _port.UsbRead(1);
            _port.UsbWrite(new byte[] { cmd });
            _port.UsbRead(1);
            _port.UsbRead(1);
            _port.UsbRead(2);
        }

        #endregion
    }
}
