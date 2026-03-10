// mtkclient port: DA/xflash/xflash_lib.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Collections.Generic;
using System.Threading;
using SakuraEDL.MediaTek.Connection;
using SakuraEDL.MediaTek.Config;
using SakuraEDL.MediaTek.Protocol;
using SakuraEDL.MediaTek.Utility;

namespace SakuraEDL.MediaTek.DA.XFlash
{
    /// <summary>
    /// XFlash binary protocol — ported from mtkclient/Library/DA/xflash/xflash_lib.py
    /// Handles DA stage 2 communication via XFlash binary commands.
    /// </summary>
    public class XFlashLib
    {
        private readonly Port _port;
        private readonly MtkConfig _config;
        private readonly DaConfig _daConfig;
        private readonly ErrorHandler _eh;
        private readonly Action<string> _info;
        private readonly Action<string> _debug;
        private readonly Action<string> _warning;
        private readonly Action<string> _error;

        public uint ExtensionsAddress { get; set; } = 0x4FFF0000;
        public string DaVersion { get; set; }
        public bool DaExt { get; set; }

        // Storage info populated after reinit
        public EmmcInfo Emmc { get; set; }
        public UfsInfo Ufs { get; set; }
        public NandInfo Nand { get; set; }
        public NorInfo Nor { get; set; }
        public RamInfo Sram { get; set; }
        public RamInfo Dram { get; set; }
        public byte[] ChipId { get; set; }
        public byte[] RandomId { get; set; }

        public XFlashLib(Port port, MtkConfig config, DaConfig daConfig,
                         Action<string> info = null, Action<string> debug = null,
                         Action<string> warning = null, Action<string> error = null)
        {
            _port = port;
            _config = config;
            _daConfig = daConfig;
            _eh = new ErrorHandler();
            _info = info ?? delegate { };
            _debug = debug ?? delegate { };
            _warning = warning ?? delegate { };
            _error = error ?? delegate { };
        }

        #region Core Protocol (xsend, xread, status, ack)

        /// <summary>
        /// Send XFlash command with magic header.
        /// mtkclient: xsend(data, datatype, is64bit)
        /// </summary>
        public bool XSend(byte[] data, uint dataType = (uint)XFlashDataType.DT_PROTOCOL_FLOW)
        {
            byte[] hdr = new byte[12];
            WriteLE32(hdr, 0, XFlashCmd.MAGIC);
            WriteLE32(hdr, 4, dataType);
            WriteLE32(hdr, 8, (uint)data.Length);
            if (_port.UsbWrite(hdr))
                return _port.UsbWrite(data);
            return false;
        }

        public bool XSend(uint value, uint dataType = (uint)XFlashDataType.DT_PROTOCOL_FLOW)
        {
            return XSend(PackLE32(value), dataType);
        }

        public bool XSend(ulong value, uint dataType = (uint)XFlashDataType.DT_PROTOCOL_FLOW)
        {
            return XSend(PackLE64(value), dataType);
        }

        /// <summary>
        /// Read XFlash response (magic + type + length + payload).
        /// mtkclient: xread()
        /// </summary>
        public byte[] XRead()
        {
            try
            {
                byte[] hdr = _port.UsbRead(12);
                if (hdr == null || hdr.Length < 12) return null;

                uint magic = ReadLE32(hdr, 0);
                uint length = ReadLE32(hdr, 8);

                if (magic != XFlashCmd.MAGIC)
                {
                    _error("xread error: Wrong magic");
                    return null;
                }

                return _port.UsbRead((int)length);
            }
            catch (Exception ex)
            {
                _error($"xread error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Read uint32 from XFlash response.
        /// </summary>
        public uint XReadDword()
        {
            byte[] data = XRead();
            if (data != null && data.Length >= 4)
                return ReadLE32(data, 0);
            return 0xFFFFFFFF;
        }

        /// <summary>
        /// Read status from device.
        /// mtkclient: status()
        /// </summary>
        public int Status()
        {
            byte[] hdr = _port.UsbRead(12);
            if (hdr == null || hdr.Length < 12) return -1;

            uint magic = ReadLE32(hdr, 0);
            uint length = ReadLE32(hdr, 8);

            if (magic != XFlashCmd.MAGIC)
            {
                _error("Status error: Wrong magic");
                return -1;
            }

            byte[] tmp = _port.UsbRead((int)length);
            if (tmp == null || tmp.Length < length)
            {
                _error("Status length error: Too few data");
                return -1;
            }

            if (length == 2)
            {
                ushort status = (ushort)(tmp[0] | (tmp[1] << 8));
                return status;
            }
            else if (length == 4)
            {
                uint status = ReadLE32(tmp, 0);
                if (status == XFlashCmd.MAGIC) return 0;
                return (int)status;
            }
            else if (length >= 4)
            {
                return (int)ReadLE32(tmp, 0);
            }
            return -1;
        }

        /// <summary>
        /// Send ACK to device.
        /// mtkclient: ack(rstatus)
        /// </summary>
        public int Ack(bool readStatus = true)
        {
            try
            {
                ushort daCode = _config.ChipConfig?.DaCode ?? 0;
                if (daCode == 0x6781 || daCode == 0x6771)
                {
                    byte[] stmp = new byte[16];
                    WriteLE32(stmp, 0, XFlashCmd.MAGIC);
                    WriteLE32(stmp, 4, (uint)XFlashDataType.DT_PROTOCOL_FLOW);
                    WriteLE32(stmp, 8, 4);
                    WriteLE32(stmp, 12, 0);
                    _port.UsbWrite(stmp);
                }
                else
                {
                    byte[] hdr = new byte[12];
                    WriteLE32(hdr, 0, XFlashCmd.MAGIC);
                    WriteLE32(hdr, 4, (uint)XFlashDataType.DT_PROTOCOL_FLOW);
                    WriteLE32(hdr, 8, 4);
                    _port.UsbWrite(hdr);
                    _port.UsbWrite(PackLE32(0));
                }

                if (readStatus)
                    return Status();
                return 0;
            }
            catch
            {
                return -1;
            }
        }

        #endregion

        #region Param / DevCtrl

        /// <summary>
        /// Send parameter blocks.
        /// mtkclient: send_param(params)
        /// </summary>
        public bool SendParam(byte[][] parameters)
        {
            foreach (byte[] param in parameters)
            {
                byte[] pkt = new byte[12];
                WriteLE32(pkt, 0, XFlashCmd.MAGIC);
                WriteLE32(pkt, 4, (uint)XFlashDataType.DT_PROTOCOL_FLOW);
                WriteLE32(pkt, 8, (uint)param.Length);
                _port.UsbWrite(pkt);

                int length = param.Length;
                int pos = 0;
                while (length > 0)
                {
                    int dsize = Math.Min(length, 0x200);
                    byte[] chunk = new byte[dsize];
                    Buffer.BlockCopy(param, pos, chunk, 0, dsize);
                    _port.UsbWrite(chunk);
                    pos += dsize;
                    length -= dsize;
                }
            }

            int status = Status();
            if (status == 0) return true;
            if (status != unchecked((int)0xc0040050))
                _error($"Error on sending parameter: {_eh.StatusToString((uint)status)}");
            return false;
        }

        /// <summary>
        /// Send device control command.
        /// mtkclient: send_devctrl(cmd, param, status)
        /// </summary>
        public byte[] SendDevCtrl(uint cmd, byte[][] param = null)
        {
            if (XSend(XFlashCmd.DEVICE_CTRL))
            {
                int status = Status();
                if (status == 0)
                {
                    if (XSend(cmd))
                    {
                        status = Status();
                        if (status == 0)
                        {
                            if (param == null)
                                return XRead();
                            SendParam(param);
                            return new byte[0];
                        }
                    }
                }
                if (status != unchecked((int)0xC0010004))
                    _error($"Error on sending dev ctrl 0x{cmd:X}: {_eh.StatusToString((uint)status)}");
            }
            return null;
        }

        #endregion

        #region Info Commands

        public string GetDaVersion(bool display = true)
        {
            if (XSend(XFlashCmd.GET_DA_VERSION))
            {
                int status = Status();
                if (status == 0)
                {
                    byte[] data = XRead();
                    if (data != null)
                    {
                        DaVersion = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0');
                        if (display)
                            _info($"DA Version: {DaVersion}");
                        return DaVersion;
                    }
                }
            }
            return null;
        }

        public byte[] GetChipId(bool display = true)
        {
            if (XSend(XFlashCmd.GET_CHIP_ID))
            {
                int status = Status();
                if (status == 0)
                {
                    ChipId = XRead();
                    if (display && ChipId != null)
                        _info($"Chip ID: {BitConverter.ToString(ChipId).Replace("-", "")}");
                    return ChipId;
                }
            }
            return null;
        }

        public byte[] GetRandomId()
        {
            if (XSend(XFlashCmd.GET_RANDOM_ID))
            {
                int status = Status();
                if (status == 0)
                {
                    RandomId = XRead();
                    return RandomId;
                }
            }
            return null;
        }

        public EmmcInfo GetEmmcInfo(bool display = true)
        {
            if (XSend(XFlashCmd.GET_EMMC_INFO))
            {
                int status = Status();
                if (status == 0)
                {
                    byte[] data = XRead();
                    if (data != null && data.Length >= 4)
                    {
                        Emmc = new EmmcInfo();
                        int pos = 0;
                        Emmc.Type = ReadLE32(data, pos); pos += 4;
                        if (data.Length >= 68)
                        {
                            Emmc.BlockSize = ReadLE32(data, pos); pos += 4;
                            Emmc.Boot1Size = ReadLE64(data, pos); pos += 8;
                            Emmc.Boot2Size = ReadLE64(data, pos); pos += 8;
                            Emmc.RpmbSize = ReadLE64(data, pos); pos += 8;
                            Emmc.Gp1Size = ReadLE64(data, pos); pos += 8;
                            Emmc.Gp2Size = ReadLE64(data, pos); pos += 8;
                            Emmc.Gp3Size = ReadLE64(data, pos); pos += 8;
                            Emmc.Gp4Size = ReadLE64(data, pos); pos += 8;
                            Emmc.UserSize = ReadLE64(data, pos); pos += 8;
                        }
                        if (display)
                        {
                            _info($"eMMC: Boot1={Emmc.Boot1Size / 1024}KB, Boot2={Emmc.Boot2Size / 1024}KB, " +
                                  $"RPMB={Emmc.RpmbSize / 1024}KB, User={Emmc.UserSize / (1024 * 1024)}MB");
                        }
                        return Emmc;
                    }
                }
            }
            return null;
        }

        public UfsInfo GetUfsInfo(bool display = true)
        {
            if (XSend(XFlashCmd.GET_UFS_INFO))
            {
                int status = Status();
                if (status == 0)
                {
                    byte[] data = XRead();
                    if (data != null && data.Length >= 4)
                    {
                        Ufs = new UfsInfo();
                        int pos = 0;
                        Ufs.Type = ReadLE32(data, pos); pos += 4;
                        if (data.Length >= 44)
                        {
                            Ufs.BlockSize = ReadLE32(data, pos); pos += 4;
                            Ufs.Lu0Size = ReadLE64(data, pos); pos += 8;
                            Ufs.Lu1Size = ReadLE64(data, pos); pos += 8;
                            Ufs.Lu2Size = ReadLE64(data, pos); pos += 8;
                            Ufs.Lu3Size = ReadLE64(data, pos); pos += 8;
                        }
                        if (display)
                        {
                            _info($"UFS: LU0={Ufs.Lu0Size / (1024 * 1024)}MB, LU1={Ufs.Lu1Size / (1024 * 1024)}MB, " +
                                  $"LU2={Ufs.Lu2Size / (1024 * 1024)}MB");
                        }
                        return Ufs;
                    }
                }
            }
            return null;
        }

        public RamInfo GetRamInfo()
        {
            if (XSend(XFlashCmd.GET_RAM_INFO))
            {
                int status = Status();
                if (status == 0)
                {
                    byte[] data = XRead();
                    if (data != null && data.Length >= 4)
                    {
                        Sram = new RamInfo();
                        int pos = 0;
                        Sram.Type = ReadLE32(data, pos); pos += 4;
                        if (data.Length >= 16)
                        {
                            Sram.BaseAddr = ReadLE64(data, pos); pos += 8;
                            Sram.Size = ReadLE64(data, pos); pos += 8;
                        }
                        if (data.Length >= pos + 20)
                        {
                            Dram = new RamInfo();
                            Dram.Type = ReadLE32(data, pos); pos += 4;
                            Dram.BaseAddr = ReadLE64(data, pos); pos += 8;
                            Dram.Size = ReadLE64(data, pos);
                        }
                        return Sram;
                    }
                }
            }
            return null;
        }

        #endregion

        #region Flash Operations

        /// <summary>
        /// Read flash data.
        /// mtkclient: readflash(addr, length, filename, parttype, display)
        /// </summary>
        public byte[] ReadFlash(ulong addr, ulong length, DaStorage storage = DaStorage.MTK_DA_STORAGE_EMMC,
                                Action<int> progressCallback = null)
        {
            if (XSend(XFlashCmd.READ_DATA))
            {
                int status = Status();
                if (status != 0)
                {
                    _error($"ReadFlash status error: {_eh.StatusToString((uint)status)}");
                    return null;
                }

                // Send read parameters
                byte[] param = new byte[20];
                WriteLE64(param, 0, addr);
                WriteLE64(param, 8, length);
                WriteLE32(param, 16, (uint)storage);
                SendParam(new byte[][] { param });

                // Read data in chunks
                byte[] result = new byte[length];
                ulong bytesRead = 0;
                while (bytesRead < length)
                {
                    byte[] chunk = XRead();
                    if (chunk == null || chunk.Length == 0) break;

                    Buffer.BlockCopy(chunk, 0, result, (int)bytesRead, chunk.Length);
                    bytesRead += (ulong)chunk.Length;

                    progressCallback?.Invoke((int)(bytesRead * 100 / length));
                    Ack(false);
                }

                status = Status();
                if (status == 0)
                    return result;

                _error($"ReadFlash final status: {_eh.StatusToString((uint)status)}");
            }
            return null;
        }

        /// <summary>
        /// Write flash data.
        /// mtkclient: writeflash(addr, length, filename, offset, parttype, wdata, display)
        /// </summary>
        public bool WriteFlash(ulong addr, byte[] data, DaStorage storage = DaStorage.MTK_DA_STORAGE_EMMC,
                               Action<int> progressCallback = null)
        {
            if (XSend(XFlashCmd.WRITE_DATA))
            {
                int status = Status();
                if (status != 0)
                {
                    _error($"WriteFlash status error: {_eh.StatusToString((uint)status)}");
                    return false;
                }

                ulong length = (ulong)data.Length;
                byte[] param = new byte[20];
                WriteLE64(param, 0, addr);
                WriteLE64(param, 8, length);
                WriteLE32(param, 16, (uint)storage);
                SendParam(new byte[][] { param });

                // Write data in chunks
                ulong pos = 0;
                while (pos < length)
                {
                    int chunkSize = (int)Math.Min(length - pos, 0x100000); // 1MB chunks
                    byte[] chunk = new byte[chunkSize];
                    Buffer.BlockCopy(data, (int)pos, chunk, 0, chunkSize);

                    XSend(chunk);
                    Ack(false);

                    pos += (ulong)chunkSize;
                    progressCallback?.Invoke((int)(pos * 100 / length));
                }

                status = Status();
                if (status == 0) return true;
                _error($"WriteFlash final status: {_eh.StatusToString((uint)status)}");
            }
            return false;
        }

        /// <summary>
        /// Format/erase flash.
        /// mtkclient: formatflash(addr, length, storage)
        /// </summary>
        public bool FormatFlash(ulong addr, ulong length, DaStorage storage = DaStorage.MTK_DA_STORAGE_EMMC)
        {
            if (XSend(XFlashCmd.FORMAT))
            {
                int status = Status();
                if (status == 0)
                {
                    byte[] param = new byte[28];
                    WriteLE32(param, 0, 2); // format type: normal
                    WriteLE64(param, 4, addr);
                    WriteLE64(param, 12, length);
                    WriteLE32(param, 20, (uint)storage);
                    if (SendParam(new byte[][] { param }))
                    {
                        // Wait for format completion
                        while (true)
                        {
                            byte[] progress = XRead();
                            if (progress == null) break;
                            if (progress.Length >= 4)
                            {
                                uint pct = ReadLE32(progress, 0);
                                if (pct >= 100) break;
                            }
                            Ack(false);
                        }
                        status = Status();
                        return status == 0;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Shutdown device.
        /// mtkclient: shutdown(async_mode, dl_bit, bootmode)
        /// </summary>
        public bool Shutdown(int asyncMode = 0, int dlBit = 0, int bootMode = 0)
        {
            if (XSend(XFlashCmd.SHUTDOWN))
            {
                int status = Status();
                if (status == 0)
                {
                    byte[] param = new byte[12];
                    WriteLE32(param, 0, (uint)asyncMode);
                    WriteLE32(param, 4, (uint)dlBit);
                    WriteLE32(param, 8, (uint)bootMode);
                    return SendParam(new byte[][] { param });
                }
            }
            return false;
        }

        #endregion

        #region Setup / Upload

        /// <summary>
        /// Sync with DA after upload.
        /// mtkclient: sync()
        /// </summary>
        public bool Sync()
        {
            byte[] syncData = PackLE32(XFlashCmd.SYNC_SIGNAL);
            _port.UsbWrite(syncData);
            byte[] res = _port.UsbRead(4, 10000);
            if (res != null && res.Length == 4)
            {
                uint val = ReadLE32(res, 0);
                if (val == XFlashCmd.SYNC_SIGNAL)
                    return true;
            }
            _error("DA sync failed");
            return false;
        }

        /// <summary>
        /// Send chunked data to device.
        /// mtkclient: send_data(data)
        /// </summary>
        public bool SendData(byte[] data)
        {
            byte[] hdr = new byte[12];
            WriteLE32(hdr, 0, XFlashCmd.MAGIC);
            WriteLE32(hdr, 4, (uint)XFlashDataType.DT_PROTOCOL_FLOW);
            WriteLE32(hdr, 8, (uint)data.Length);
            if (_port.UsbWrite(hdr))
            {
                int bytesToWrite = data.Length;
                int maxOut = 0x200; // USB max packet size
                int pos = 0;
                while (bytesToWrite > 0)
                {
                    int chunkLen = Math.Min(bytesToWrite, maxOut);
                    byte[] chunk = new byte[chunkLen];
                    Buffer.BlockCopy(data, pos, chunk, 0, chunkLen);
                    if (!_port.UsbWrite(chunk)) return false;
                    pos += chunkLen;
                    bytesToWrite -= chunkLen;
                }
                int status = Status();
                if (status == 0) return true;
                _error($"Error on sending data: {_eh.StatusToString((uint)status)}");
            }
            return false;
        }

        /// <summary>
        /// Boot to DA2 or extension address.
        /// mtkclient: boot_to(addr, da, display, timeout)
        /// </summary>
        public bool BootTo(ulong addr, byte[] da, bool display = true, int timeoutMs = 500)
        {
            if (XSend(XFlashCmd.BOOT_TO))
            {
                if (Status() == 0)
                {
                    // Send addr + length as param
                    byte[] param = new byte[16];
                    WriteLE64(param, 0, addr);
                    WriteLE64(param, 8, (ulong)da.Length);

                    byte[] pkt = new byte[12];
                    WriteLE32(pkt, 0, XFlashCmd.MAGIC);
                    WriteLE32(pkt, 4, (uint)XFlashDataType.DT_PROTOCOL_FLOW);
                    WriteLE32(pkt, 8, (uint)param.Length);

                    if (_port.UsbWrite(pkt) && _port.UsbWrite(param))
                    {
                        if (SendData(da))
                        {
                            if (addr == ExtensionsAddress)
                            {
                                if (display) _info("Extensions were accepted. Jumping to extensions...");
                            }
                            else
                            {
                                if (display) _info("Upload data was accepted. Jumping to stage 2...");
                            }

                            if (timeoutMs > 0)
                                Thread.Sleep(timeoutMs);

                            int status;
                            try
                            {
                                status = Status();
                            }
                            catch
                            {
                                _error("Stage wasn't executed. Maybe DRAM issue?");
                                return false;
                            }

                            if (status == unchecked((int)XFlashCmd.SYNC_SIGNAL) || status == 0)
                            {
                                if (display) _info("Boot to succeeded.");
                                return true;
                            }
                            _error($"Error on boot to: {_eh.StatusToString((uint)status)}, addr: 0x{addr:X}");
                        }
                        else
                            _error($"Error on boot to send_data, addr: 0x{addr:X}");
                    }
                    else
                        _error($"Error on boot usbwrite, addr: 0x{addr:X}");
                }
                else
                    _error($"Error on boot to, addr: 0x{addr:X}");
            }
            return false;
        }

        /// <summary>
        /// Setup environment (log level, channel, OS, UFS provision).
        /// mtkclient: setup_env()
        /// </summary>
        public bool SetupEnv()
        {
            if (XSend(XFlashCmd.SETUP_ENVIRONMENT))
            {
                uint daLogLevel = 4; // INFO
                uint logChannel = 1; // USB
                uint systemOs = 1;   // LINUX (Python: ft_system_ose.OS_LINUX)
                uint ufsProvision = 0;
                byte[] param = new byte[20];
                WriteLE32(param, 0, daLogLevel);
                WriteLE32(param, 4, logChannel);
                WriteLE32(param, 8, systemOs);
                WriteLE32(param, 12, ufsProvision);
                WriteLE32(param, 16, 0);
                if (SendParam(new byte[][] { param }))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Setup hardware init.
        /// mtkclient: setup_hw_init()
        /// </summary>
        public bool SetupHwInit()
        {
            if (XSend(XFlashCmd.SETUP_HW_INIT_PARAMS))
            {
                byte[] param = PackLE32(0); // No config
                if (SendParam(new byte[][] { param }))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Send EMI/DRAM configuration.
        /// mtkclient: send_emi(emi)
        /// </summary>
        public bool SendEmi(byte[] emi)
        {
            if (emi == null || emi.Length == 0) return false;
            if (XSend(XFlashCmd.INIT_EXT_RAM))
            {
                int status = Status();
                if (status == 0)
                {
                    Thread.Sleep(10);
                    if (XSend((uint)emi.Length))
                    {
                        try
                        {
                            if (SendParam(new byte[][] { emi }))
                            {
                                _info("DRAM setup passed.");
                                return true;
                            }
                            _info("DRAM setup failed.");
                        }
                        catch (Exception ex)
                        {
                            _info($"DRAM setup failed: {ex.Message}");
                        }
                    }
                }
                else
                    _error($"Error on sending emi: {_eh.StatusToString((uint)status)}");
            }
            return false;
        }

        /// <summary>
        /// Set checksum level.
        /// mtkclient: set_checksum_level
        /// </summary>
        public bool SetChecksumLevel(uint level = 0)
        {
            byte[] param = PackLE32(level);
            byte[] res = SendDevCtrl(XFlashCmd.SET_CHECKSUM_LEVEL, new byte[][] { param });
            return res != null;
        }

        /// <summary>
        /// Set battery option.
        /// mtkclient: set_battery_opt
        /// </summary>
        public bool SetBatteryOpt(uint option = 2)
        {
            byte[] param = PackLE32(option);
            byte[] res = SendDevCtrl(XFlashCmd.SET_BATTERY_OPT, new byte[][] { param });
            return res != null;
        }

        /// <summary>
        /// Set reset key.
        /// mtkclient: set_reset_key
        /// </summary>
        public bool SetResetKey(uint resetKey = 0x68)
        {
            byte[] param = PackLE32(resetKey);
            byte[] res = SendDevCtrl(XFlashCmd.SET_RESET_KEY, new byte[][] { param });
            return res != null;
        }

        /// <summary>
        /// Get partition table category.
        /// mtkclient: get_partition_table_category
        /// </summary>
        public string GetPartitionTableCategory()
        {
            byte[] res = SendDevCtrl(XFlashCmd.GET_PARTITION_TBL_CATA);
            if (res != null && res.Length >= 4)
            {
                uint val = ReadLE32(res, 0);
                if (val == 0x64) return "GPT";
                if (val == 0x65) return "PMT";
            }
            return null;
        }

        /// <summary>
        /// Get packet length (write + read).
        /// mtkclient: get_packet_length
        /// </summary>
        public (uint write, uint read) GetPacketLength()
        {
            byte[] resp = SendDevCtrl(XFlashCmd.GET_PACKET_LENGTH);
            if (resp != null && resp.Length >= 8)
            {
                int status = Status();
                if (status == 0)
                    return (ReadLE32(resp, 0), ReadLE32(resp, 4));
                _error($"Error on getting packet length: {_eh.StatusToString((uint)status)}");
            }
            return (0, 0);
        }

        #endregion

        #region Connection / USB

        /// <summary>
        /// Get connection agent (brom or preloader).
        /// mtkclient: get_connection_agent()
        /// </summary>
        public string GetConnectionAgent()
        {
            byte[] res = SendDevCtrl(XFlashCmd.GET_CONNECTION_AGENT);
            if (res != null && res.Length > 0)
            {
                int status = Status();
                if (status == 0)
                    return System.Text.Encoding.ASCII.GetString(res).TrimEnd('\0');
                _error($"Error on getting connection agent: {_eh.StatusToString((uint)status)}");
            }
            return null;
        }

        /// <summary>
        /// Get USB speed string.
        /// mtkclient: get_usb_speed()
        /// </summary>
        public string GetUsbSpeed()
        {
            byte[] resp = SendDevCtrl(XFlashCmd.GET_USB_SPEED);
            if (resp != null && resp.Length > 0)
            {
                int status = Status();
                if (status == 0)
                    return System.Text.Encoding.ASCII.GetString(resp).TrimEnd('\0');
                _error($"Error on getting usb speed: {_eh.StatusToString((uint)status)}");
            }
            return null;
        }

        /// <summary>
        /// Switch to high-speed USB.
        /// mtkclient: set_usb_speed()
        /// </summary>
        public bool SetUsbSpeed()
        {
            if (XSend(XFlashCmd.SWITCH_USB_SPEED))
            {
                int status = Status();
                if (status == 0)
                {
                    if (XSend(0x0E8D2001))
                    {
                        status = Status();
                        if (status == 0) return true;
                    }
                }
                _error($"Error on setting usb speed: {_eh.StatusToString((uint)status)}");
            }
            return false;
        }

        #endregion

        #region SLA Authentication

        /// <summary>
        /// Check if SLA is enabled.
        /// mtkclient: get_sla_status()
        /// </summary>
        public bool GetSlaStatus()
        {
            byte[] resp = SendDevCtrl(XFlashCmd.SLA_ENABLED_STATUS);
            if (resp != null && resp.Length > 0)
            {
                int status = Status();
                if (status == 0)
                {
                    uint val = resp.Length >= 4 ? ReadLE32(resp, 0) : resp[0];
                    return val != 0;
                }
                _error($"Error on getting sla enabled status: {_eh.StatusToString((uint)status)}");
            }
            return false;
        }

        /// <summary>
        /// Get device firmware info (used for SLA challenge).
        /// mtkclient: get_dev_fw_info()
        /// </summary>
        public byte[] GetDevFwInfo()
        {
            byte[] res = SendDevCtrl(XFlashCmd.GET_DEV_FW_INFO);
            if (res != null && res.Length > 0)
            {
                int status = Status();
                if (status == 0) return res;
                _error($"Error on getting dev fw info: {_eh.StatusToString((uint)status)}");
            }
            return null;
        }

        /// <summary>
        /// Set remote security policy (send SLA signature).
        /// mtkclient: set_remote_sec_policy(data)
        /// </summary>
        public bool SetRemoteSecPolicy(byte[] data)
        {
            byte[] res = SendDevCtrl(XFlashCmd.SET_REMOTE_SEC_POLICY, new byte[][] { data });
            return res != null;
        }

        /// <summary>
        /// Handle SLA authentication.
        /// mtkclient: handle_sla(da2)
        /// </summary>
        public bool HandleSla(byte[] da2)
        {
            // Try to find matching RSA key in DA2
            System.Security.Cryptography.RSAParameters? slaKey = null;
            foreach (var key in Auth.SlaKeys.DaSlaKeys)
            {
                byte[] nBytes = Auth.Sla.BigIntegerToBytes(key.GetN(), 256);
                if (ByteUtils.FindBytes(da2, nBytes) >= 0)
                {
                    slaKey = key.ToRsaParameters();
                    break;
                }
            }

            if (!slaKey.HasValue)
            {
                _info("No valid SLA key found, trying dummy auth...");
                byte[] dummySig = new byte[0x100];
                if (SetRemoteSecPolicy(dummySig))
                {
                    _info("SLA Signature was accepted (dummy).");
                    return true;
                }
                return false;
            }

            byte[] fwInfo = GetDevFwInfo();
            if (fwInfo != null && fwInfo.Length >= 0x14)
            {
                byte[] challenge = new byte[0x10];
                Buffer.BlockCopy(fwInfo, 4, challenge, 0, 0x10);
                byte[] signature = Auth.Sla.GenerateDaSlaSignature(challenge, slaKey.Value);
                if (signature != null && SetRemoteSecPolicy(signature))
                {
                    _info("SLA Signature was accepted.");
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region Extended Info

        /// <summary>
        /// Get NAND info.
        /// mtkclient: get_nand_info()
        /// </summary>
        public NandInfo GetNandInfo(bool display = true)
        {
            if (XSend(XFlashCmd.GET_NAND_INFO))
            {
                int status = Status();
                if (status == 0)
                {
                    byte[] data = XRead();
                    if (data != null && data.Length >= 4)
                    {
                        Nand = new NandInfo();
                        int pos = 0;
                        Nand.Type = ReadLE32(data, pos); pos += 4;
                        if (data.Length >= 28)
                        {
                            Nand.PageSize = ReadLE32(data, pos); pos += 4;
                            Nand.BlockSize = (uint)ReadLE64(data, pos); pos += 8;
                            Nand.TotalSize = ReadLE64(data, pos); pos += 8;
                            Nand.SpareSize = ReadLE32(data, pos);
                        }
                        if (display && Nand.Type != 0)
                            _info($"NAND: Page={Nand.PageSize}, Block={Nand.BlockSize}, Total={Nand.TotalSize / (1024 * 1024)}MB");
                        return Nand;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Get NOR info.
        /// mtkclient: get_nor_info()
        /// </summary>
        public NorInfo GetNorInfo(bool display = true)
        {
            if (XSend(XFlashCmd.GET_NOR_INFO))
            {
                int status = Status();
                if (status == 0)
                {
                    byte[] data = XRead();
                    if (data != null && data.Length >= 4)
                    {
                        Nor = new NorInfo();
                        int pos = 0;
                        Nor.Type = ReadLE32(data, pos); pos += 4;
                        if (data.Length >= 20)
                        {
                            Nor.PageSize = ReadLE32(data, pos); pos += 4;
                            Nor.TotalSize = ReadLE64(data, pos);
                        }
                        if (display && Nor.Type != 0)
                            _info($"NOR: Page={Nor.PageSize}, Total={Nor.TotalSize / 1024}KB");
                        return Nor;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Get expire date.
        /// mtkclient: get_expire_date()
        /// </summary>
        public byte[] GetExpireDate()
        {
            byte[] res = SendDevCtrl(XFlashCmd.GET_EXPIRE_DATE);
            if (res != null && res.Length > 0)
            {
                int status = Status();
                if (status == 0) return res;
            }
            return null;
        }

        /// <summary>
        /// Get HRID.
        /// mtkclient: get_hrid()
        /// </summary>
        public byte[] GetHrid()
        {
            byte[] res = SendDevCtrl(XFlashCmd.GET_HRID);
            if (res != null && res.Length > 0)
            {
                int status = Status();
                if (status == 0) return res;
                _error($"Error on getting hrid info: {_eh.StatusToString((uint)status)}");
            }
            return null;
        }

        /// <summary>
        /// Get RPMB status.
        /// mtkclient: get_rpmb_status()
        /// </summary>
        public uint GetRpmbStatus()
        {
            byte[] res = SendDevCtrl(XFlashCmd.GET_RPMB_STATUS);
            if (res != null && res.Length >= 4)
            {
                int status = Status();
                if (status == 0)
                    return ReadLE32(res, 0);
            }
            return 0;
        }

        /// <summary>
        /// Check DA storage lifecycle.
        /// mtkclient: get_da_stor_life_check()
        /// </summary>
        public uint LifecycleCheck()
        {
            byte[] res = SendDevCtrl(XFlashCmd.DA_STOR_LIFE_CYCLE_CHECK);
            if (res != null && res.Length >= 4)
                return ReadLE32(res, 0);
            return 0;
        }

        /// <summary>
        /// Reinitialize after DA2 boot — collect storage info, chip ID, version, USB speed.
        /// mtkclient: reinit(display)
        /// </summary>
        public void Reinit(bool display = false)
        {
            GetRamInfo();
            Emmc = GetEmmcInfo(display);
            Nand = GetNandInfo(display);
            Nor = GetNorInfo(display);
            Ufs = GetUfsInfo(display);

            // Determine flash type
            if (Emmc != null && Emmc.Type != 0)
            {
                _daConfig.Storage.FlashType = "emmc";
                _daConfig.Storage.Emmc = Emmc;
            }
            else if (Nand != null && Nand.Type != 0)
            {
                _daConfig.Storage.FlashType = "nand";
                _daConfig.Storage.Nand = Nand;
            }
            else if (Nor != null && Nor.Type != 0)
            {
                _daConfig.Storage.FlashType = "nor";
                _daConfig.Storage.Nor = Nor;
            }
            else if (Ufs != null && Ufs.Type != 0)
            {
                _daConfig.Storage.FlashType = "ufs";
                _daConfig.Storage.Ufs = Ufs;
            }

            ChipId = GetChipId(false);
            DaVersion = GetDaVersion(false);
            RandomId = GetRandomId();
            _daConfig.Storage.SetFlashSize();

            // USB speed upgrade
            string speed = GetUsbSpeed();
            if (speed == "full-speed")
            {
                _info("Reconnecting to stage2 with higher speed");
                SetUsbSpeed();
                _port.Close();
                Thread.Sleep(2000);
                _port.Connect();
                _info("Connected to stage2 with higher speed");
            }
        }

        #endregion

        #region LE Helpers

        private static byte[] PackLE32(uint val)
        {
            return BitConverter.GetBytes(val);
        }

        private static byte[] PackLE64(ulong val)
        {
            return BitConverter.GetBytes(val);
        }

        private static void WriteLE32(byte[] buf, int offset, uint val)
        {
            buf[offset] = (byte)(val & 0xFF);
            buf[offset + 1] = (byte)((val >> 8) & 0xFF);
            buf[offset + 2] = (byte)((val >> 16) & 0xFF);
            buf[offset + 3] = (byte)((val >> 24) & 0xFF);
        }

        private static void WriteLE64(byte[] buf, int offset, ulong val)
        {
            for (int i = 0; i < 8; i++)
                buf[offset + i] = (byte)((val >> (i * 8)) & 0xFF);
        }

        private static uint ReadLE32(byte[] data, int offset)
        {
            return BitConverter.ToUInt32(data, offset);
        }

        private static ulong ReadLE64(byte[] data, int offset)
        {
            return BitConverter.ToUInt64(data, offset);
        }

        #endregion
    }
}
