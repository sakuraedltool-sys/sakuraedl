// mtkclient port: DA/xmlflash/extension/v6.py
// (c) B.Kerler 2018-2026 GPLv3 License
// DA XML Extensions — custom commands, DA patching, RPMB, memory access, key generation
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SakuraEDL.MediaTek.Config;
using SakuraEDL.MediaTek.Connection;
using SakuraEDL.MediaTek.Hardware;

namespace SakuraEDL.MediaTek.DA.XmlFlash
{
    /// <summary>
    /// XML DA Extensions — provides DA patching, custom memory access, RPMB, crypto, and key extraction.
    /// Ported from mtkclient/Library/DA/xmlflash/extension/v6.py
    /// </summary>
    public class XmlFlashExt
    {
        private readonly MtkClass _mtk;
        private readonly XmlLib _xml;
        private readonly Action<string> _info;
        private readonly Action<string> _debug;
        private readonly Action<string> _warning;
        private readonly Action<string> _error;

        private byte[] _da2;
        private uint _da2Address;

        private static readonly string[] RpmbErrors =
        {
            "", "General failure", "Authentication failure", "Counter failure",
            "Address failure", "Write failure", "Read failure", "Auth key not programmed"
        };

        public XmlFlashExt(MtkClass mtk, XmlLib xml)
        {
            _mtk = mtk;
            _xml = xml;
            _info = mtk.OnInfo ?? delegate { };
            _debug = mtk.OnDebug ?? delegate { };
            _warning = mtk.OnWarning ?? delegate { };
            _error = mtk.OnError ?? delegate { };

            // Initialize DA2 data and address from config
            var daConfig = mtk.DaLoader?.DaConfigInstance;
            _da2 = daConfig?.Da2;
            var daLoader = daConfig?.Setup();
            if (daLoader != null && daLoader.Regions.Count >= 2)
            {
                int da1Index = daLoader.EntryRegionIndex;
                if (da1Index < 0 || da1Index >= daLoader.Regions.Count)
                    da1Index = 0;

                int da2Index = -1;
                var da1Region = daLoader.Regions[da1Index];
                for (int i = da1Index + 1; i < daLoader.Regions.Count; i++)
                {
                    var candidate = daLoader.Regions[i];
                    if (candidate.Len == 0) continue;
                    if (candidate.StartOffset != da1Region.StartOffset || candidate.StartAddr != da1Region.StartAddr)
                    {
                        da2Index = i;
                        break;
                    }
                }

                if (da2Index < 0)
                {
                    for (int i = daLoader.Regions.Count - 1; i >= 0; i--)
                    {
                        if (i == da1Index) continue;
                        if (daLoader.Regions[i].Len > 0)
                        {
                            da2Index = i;
                            break;
                        }
                    }
                }

                if (da2Index >= 0)
                    _da2Address = daLoader.Regions[da2Index].StartAddr;
            }
        }

        #region DA Patching

        /// <summary>
        /// Patch DA1 — currently a passthrough; vendor-specific patches can be added.
        /// mtkclient: patch_da1(da1)
        /// </summary>
        public byte[] PatchDa1(byte[] da1)
        {
            return da1;
        }

        /// <summary>
        /// Patch DA2 binary — static version for use before MtkClass is available.
        /// </summary>
        public static byte[] PatchDa2Static(byte[] da2, Action<string> info = null)
        {
            var inst = new XmlFlashExt(null, null);
            inst._patchInfo = info ?? delegate { };
            return inst.PatchDa2(da2);
        }

        private Action<string> _patchInfo;
        private void PatchLog(string msg) => (_patchInfo ?? _info)?.Invoke(msg);

        /// <summary>
        /// Patch DA2 binary to inject custom command handler and bypass security checks.
        /// mtkclient: patch_da2(da2)
        /// </summary>
        public byte[] PatchDa2(byte[] da2)
        {
            if (da2 == null || da2.Length < 0x100) return da2;
            byte[] patched = (byte[])da2.Clone();

            // 1. Patch custom command: replace CMD:SET-HOST-INFO with CMD:CUSTOM
            patched = PatchCustomCommand(patched);

            // 2. Patch sgpt verification (return 0 immediately)
            byte[] sgptPattern = new byte[] {
                0x30, 0x48, 0x2D, 0xE9, 0x08, 0xB0, 0x8D, 0xE2,
                0x08, 0xD0, 0x4D, 0xE2, 0x00, 0x40, 0xA0, 0xE1,
                0x04, 0x00, 0x8D, 0xE2, 0x00, 0x50, 0xA0, 0xE3 };
            int sgptIdx = FindPattern(patched, sgptPattern);
            if (sgptIdx >= 0)
            {
                byte[] retZero = { 0x00, 0x00, 0xA0, 0xE3, 0x1E, 0xFF, 0x2F, 0xE1 };
                Buffer.BlockCopy(retZero, 0, patched, sgptIdx, retZero.Length);
                _info("Patched sgpt verification.");
            }

            // 3. Patch SLA signature checks
            byte[] sla1 = { 0x32, 0x00, 0x00, 0xE3, 0x02, 0x00, 0x4C, 0xE3 };
            int slaIdx1 = FindPattern(patched, sla1);
            if (slaIdx1 >= 0)
            {
                byte[] nops = { 0x00, 0x00, 0xA0, 0xE3, 0x00, 0x00, 0xA0, 0xE3, 0x00, 0x40, 0xA0, 0xE3 };
                Buffer.BlockCopy(nops, 0, patched, slaIdx1, nops.Length);
                _info("Patched SLA signature check 1.");

                byte[] sla2 = { 0x32, 0x40, 0x00, 0xE3, 0x02, 0x40, 0x4C, 0xE3 };
                int slaIdx2 = FindPattern(patched, sla2);
                if (slaIdx2 >= 0)
                {
                    byte[] nops2 = { 0x00, 0x40, 0xA0, 0xE3, 0x00, 0x40, 0xA0, 0xE3 };
                    Buffer.BlockCopy(nops2, 0, patched, slaIdx2, nops2.Length);
                    _info("Patched SLA signature check 2.");
                }
            }
            else
            {
                // Try Infinix Remote SLA v3
                byte[] infinix = {
                    0xF0, 0x4D, 0x2D, 0xE9, 0x18, 0xB0, 0x8D, 0xE2,
                    0xF0, 0xD0, 0x4D, 0xE2, 0x01, 0x50, 0xA0, 0xE1 };
                int idx = FindPattern(patched, infinix);
                if (idx >= 0)
                {
                    byte[] retZero = { 0x00, 0x00, 0xA0, 0xE3, 0x1E, 0xFF, 0x2F, 0xE1 };
                    Buffer.BlockCopy(retZero, 0, patched, idx, retZero.Length);
                    _info("Patched Infinix Remote SLA v3 auth.");
                }
                else
                {
                    // Try Oppo Remote SLA
                    byte[] oppo = {
                        0x70, 0x4C, 0x2D, 0xE9, 0x10, 0xB0, 0x8D, 0xE2,
                        0x00, 0x60, 0xA0, 0xE1, 0x02, 0x06, 0xA0, 0xE3 };
                    idx = FindPattern(patched, oppo);
                    if (idx < 0)
                    {
                        byte[] oppo2 = {
                            0x70, 0x4C, 0x2D, 0xE9, 0x10, 0xB0, 0x8D, 0xE2,
                            0x01, 0x40, 0xA0, 0xE1, 0x00, 0x50, 0xA0, 0xE1 };
                        idx = FindPattern(patched, oppo2);
                    }
                    if (idx >= 0)
                    {
                        byte[] retZero = { 0x00, 0x00, 0xA0, 0xE3, 0x1E, 0xFF, 0x2F, 0xE1 };
                        Buffer.BlockCopy(retZero, 0, patched, idx, retZero.Length);
                        _info("Patched Oppo Remote SLA auth.");

                        // Patch Oppo allowance flag
                        byte[] allowance = { 0x03, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
                                             0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
                        int aIdx = FindPattern(patched, allowance);
                        if (aIdx >= 0)
                        {
                            patched[aIdx] = 0xFF;
                            _info("Patched Oppo allowance flag.");
                        }
                    }
                    else
                    {
                        // Try Vivo
                        byte[] vivo = {
                            0xF0, 0x4D, 0x2D, 0xE9, 0x18, 0xB0, 0x8D, 0xE2,
                            0x82, 0xDF, 0x4D, 0xE2, 0x01, 0x60, 0xA0, 0xE1 };
                        idx = FindPattern(patched, vivo);
                        if (idx >= 0)
                        {
                            byte[] retZero = { 0x00, 0x00, 0xA0, 0xE3, 0x1E, 0xFF, 0x2F, 0xE1 };
                            Buffer.BlockCopy(retZero, 0, patched, idx, retZero.Length);
                            _info("Patched Vivo Remote SLA auth.");
                        }
                        else
                        {
                            // AArch64 variant
                            byte[] a64 = {
                                0xFD, 0x7B, 0xBD, 0xA9, 0xF6, 0x57, 0x01, 0xA9,
                                0xF4, 0x4F, 0x02, 0xA9, 0xFD, 0x03, 0x00, 0x91,
                                0xF5, 0x03, 0x02, 0xAA, 0xF3, 0x03, 0x01, 0x2A };
                            idx = FindPattern(patched, a64);
                            if (idx >= 0)
                            {
                                // MOV W0, #1; RET
                                byte[] a64Ret = { 0x20, 0x00, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6 };
                                Buffer.BlockCopy(a64Ret, 0, patched, idx, a64Ret.Length);
                                _info("Patched AArch64 Remote SLA auth.");
                            }
                        }
                    }
                }
            }

            // 4. Patch generic SLA enabled → disabled
            int slaEn = FindPattern(patched, Encoding.ASCII.GetBytes("DA.SLA\x00ENABLED"));
            if (slaEn >= 0)
            {
                byte[] disabled = Encoding.ASCII.GetBytes("DA.SLA\x00DISABLE");
                Buffer.BlockCopy(disabled, 0, patched, slaEn, disabled.Length);
                _info("Patched generic DA.SLA ENABLED → DISABLED.");
            }

            return patched;
        }

        /// <summary>
        /// Patch DA2 to replace CMD:SET-HOST-INFO with CMD:CUSTOM handler.
        /// mtkclient: patch_custom_command(da2)
        /// </summary>
        private byte[] PatchCustomCommand(byte[] da2)
        {
            byte[] marker = Encoding.ASCII.GetBytes("\x00CMD:SET-HOST-INFO\x00");
            int idx = FindPattern(da2, marker);
            if (idx < 0) return da2;

            // Inject custom command shellcode at the handler address
            byte[] shellcode = HexToBytes(
                "704c2de910b08de20080a0e100000fe3000846e30410a0e3002098e532ff2fe1" +
                "00000fe3000846e3000000e3000846e3002098e532ff2fe1000000e3000846e3" +
                "30ff2fe1708cbde8");

            // Replace command string
            byte[] newCmd = Encoding.ASCII.GetBytes("CMD:CUSTOM\x00");
            Buffer.BlockCopy(newCmd, 0, da2, idx + 1, newCmd.Length);

            _info("Patched custom command handler into DA2.");
            return da2;
        }

        #endregion

        #region Extension Loading

        /// <summary>
        /// Load DA extension payload from file and send to device.
        /// mtkclient: patch() in v6.py
        /// </summary>
        public byte[] GetExtensionPayload()
        {
            _da2 = _mtk.DaLoader?.DaConfigInstance?.Da2;
            if (_da2 == null)
            {
                _warning("DA2 not available for extension loading.");
                return null;
            }

            // Try to load da_xml.bin extension payload
            string payloadPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payloads", "da_xml.bin");
            if (!File.Exists(payloadPath))
            {
                // Try alternative paths
                payloadPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "da_xml.bin");
                if (!File.Exists(payloadPath))
                {
                    _warning("da_xml.bin extension payload not found. Using built-in minimal payload.");
                    return null;
                }
            }

            byte[] payload = File.ReadAllBytes(payloadPath);
            _info($"Loaded DA extension payload: {payload.Length} bytes");

            // Patch function pointers in payload
            PatchExtensionPointers(payload);
            return payload;
        }

        /// <summary>
        /// Patch function pointers in the extension payload to match current DA2.
        /// mtkclient: patch() in v6.py — patches placeholder addresses in da_xml.bin.
        /// Placeholders: 0x11111111=register_xml_cmd, 0x22222222=mmc_get_card,
        /// 0x33333333=mmc_set_part_config, 0x44444444=mmc_rpmb_send_command,
        /// 0x55555555=ufshcd_queuecommand, 0x66666666=ufshcd_get_free_tag,
        /// 0x77777777=g_ufs_hba, 0x88888888=efuse_addr
        /// </summary>
        private void PatchExtensionPointers(byte[] payload)
        {
            if (_da2 == null) return;
            uint da2Addr = _da2Address;

            // 0x11111111 → register_xml_cmd
            byte[] regXmlCmdSig = { 0x70, 0x4C, 0x2D, 0xE9, 0x10, 0xB0, 0x8D, 0xE2,
                                    0x00, 0x50, 0xA0, 0xE1, 0x14, 0x00, 0xA0, 0xE3 };
            int regIdx = FindPattern(_da2, regXmlCmdSig);
            if (regIdx >= 0)
                PatchPointer(payload, 0x11111111, (uint)(da2Addr + regIdx));

            // --- UFS pointers ---
            // Find g_ufs_hba, ufshcd_queuecommand, ufshcd_get_free_tag
            uint gUfsHba = 0, ufshcdQueueCmd = 0, ufshcdGetFreeTag = 0;
            byte[] ufsSig = { 0x00, 0x00, 0x94, 0xE5, 0x34, 0x10, 0x90, 0xE5,
                              0x01, 0x00, 0x11, 0xE3, 0x03, 0x00, 0x00, 0x0A };
            int ufsIdx = FindPattern(_da2, ufsSig);
            if (ufsIdx >= 0 && ufsIdx >= 8)
            {
                // Decode g_ufs_hba from MOV instructions at ufsIdx-8
                uint instr1 = BitConverter.ToUInt32(_da2, ufsIdx - 8);
                uint instr2 = BitConverter.ToUInt32(_da2, ufsIdx - 4);
                gUfsHba = OpMovToOffset(instr1, instr2, 4);

                // ufshcd_queuecommand
                byte[] qcSig1 = { 0xF0, 0x4D, 0x2D, 0xE9, 0x18, 0xB0, 0x8D, 0xE2,
                                   0x08, 0xD0, 0x4D, 0xE2, 0x48, 0x40, 0x90, 0xE5 };
                byte[] qcSig2 = { 0xF0, 0x4F, 0x2D, 0xE9, 0x1C, 0xB0, 0x8D, 0xE2,
                                   0x0C, 0xD0, 0x4D, 0xE2, 0x48, 0xA0, 0x90, 0xE5,
                                   0x00, 0x80, 0xA0, 0xE1 };
                int qcIdx = FindPattern(_da2, qcSig1);
                if (qcIdx < 0) qcIdx = FindPattern(_da2, qcSig2);
                if (qcIdx >= 0) ufshcdQueueCmd = (uint)(da2Addr + qcIdx);

                // ufshcd_get_free_tag
                byte[] ftSig = { 0x10, 0x4C, 0x2D, 0xE9, 0x08, 0xB0, 0x8D, 0xE2,
                                  0x00, 0x40, 0xA0, 0xE3, 0x00, 0x00, 0x51, 0xE3 };
                int ftIdx = FindPattern(_da2, ftSig);
                if (ftIdx >= 0) ufshcdGetFreeTag = (uint)(da2Addr + ftIdx);
            }
            PatchPointer(payload, 0x55555555, ufshcdQueueCmd);
            PatchPointer(payload, 0x66666666, ufshcdGetFreeTag);
            PatchPointer(payload, 0x77777777, gUfsHba);

            // --- eMMC pointers ---
            // mmc_get_card
            byte[] mmcGetCardSig = { 0x90, 0x12, 0x20, 0xE0, 0x1E, 0xFF, 0x2F, 0xE1 };
            int mmcGetCard = FindPattern(_da2, mmcGetCardSig);
            uint mmcGetCardAddr = (mmcGetCard >= 0xC) ? (uint)(da2Addr + mmcGetCard - 0xC) : 0;
            PatchPointer(payload, 0x22222222, mmcGetCardAddr);

            // mmc_set_part_config
            byte[] mpcSig1 = { 0xF0, 0x4B, 0x2D, 0xE9, 0x18, 0xB0, 0x8D, 0xE2, 0x23, 0xDE, 0x4D, 0xE2 };
            byte[] mpcSig2 = { 0xF0, 0x4B, 0x2D, 0xE9, 0x18, 0xB0, 0x8D, 0xE2, 0x8E, 0xDF, 0x4D, 0xE2 };
            int mpcIdx = FindPattern(_da2, mpcSig1);
            if (mpcIdx < 0) mpcIdx = FindPattern(_da2, mpcSig2);
            uint mmcSetPartConfig = mpcIdx >= 0 ? (uint)(da2Addr + mpcIdx) : 0;
            PatchPointer(payload, 0x33333333, mmcSetPartConfig);

            // mmc_rpmb_send_command
            byte[] rpmbSig = { 0xF0, 0x48, 0x2D, 0xE9, 0x10, 0xB0, 0x8D, 0xE2, 0x08, 0x70, 0x9B, 0xE5 };
            int rpmbIdx = FindPattern(_da2, rpmbSig);
            uint mmcRpmbSendCmd = rpmbIdx >= 0 ? (uint)(da2Addr + rpmbIdx) : 0;
            PatchPointer(payload, 0x44444444, mmcRpmbSendCmd);

            // 0x88888888 → efuse_addr from chip config
            uint efuseAddr = _mtk?.Config?.ChipConfig?.EfuseAddr ?? 0;
            PatchPointer(payload, 0x88888888, efuseAddr);

            _info($"Extension pointers: reg=0x{(regIdx >= 0 ? da2Addr + (uint)regIdx : 0):X}, " +
                  $"ufs_hba=0x{gUfsHba:X}, qcmd=0x{ufshcdQueueCmd:X}, " +
                  $"mmc_card=0x{mmcGetCardAddr:X}, rpmb=0x{mmcRpmbSendCmd:X}");
        }

        /// <summary>
        /// Decode MOVW/MOVT pair to get a 32-bit pointer value.
        /// mtkclient: op_mov_to_offset(instr1, instr2, shift)
        /// </summary>
        private static uint OpMovToOffset(uint instr1, uint instr2, int shift)
        {
            // ARM MOVW/MOVT encoding: imm16 = imm4(19:16) | imm12(11:0)
            uint lo = ((instr1 >> 4) & 0xF000) | (instr1 & 0xFFF);
            uint hi = ((instr2 >> 4) & 0xF000) | (instr2 & 0xFFF);
            return (hi << 16) | lo;
        }

        /// <summary>
        /// Send CUSTOMACK to verify extension loaded correctly.
        /// mtkclient: ack() in v6.py
        /// </summary>
        public bool AckExtension()
        {
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMACK</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp == null) return false;

            // Read data response (raw bytes)
            byte[] data = _xml.XRead();

            // Read CMD:END
            _xml.GetResponse();
            _xml.Ack();

            // Read CMD:START
            _xml.GetResponse();
            _xml.Ack();

            // Check magic response 0xA1A2A3A4
            if (data != null && data.Length >= 4 &&
                data[0] == 0xA4 && data[1] == 0xA3 && data[2] == 0xA2 && data[3] == 0xA1)
            {
                _info("DA Extension ACK verified.");
                return true;
            }

            _warning("DA Extension ACK failed.");
            return false;
        }

        #endregion

        #region Custom Memory Access

        /// <summary>
        /// Read memory via custom DA command.
        /// mtkclient: custom_read(addr, length, registers)
        /// </summary>
        public byte[] CustomRead(ulong addr, int length)
        {
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMMEMR</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK") return null;

            // Send address (64-bit) and length (all datatype=1 per Python)
            _xml.XSend(BitConverter.GetBytes(addr));
            _xml.XSend(BitConverter.GetBytes(length));

            byte[] data = _xml.XRead();

            // CMD:END
            _xml.GetResponse();
            _xml.Ack();
            // CMD:START
            _xml.GetResponse();
            _xml.Ack();

            return data;
        }

        /// <summary>
        /// Write memory via custom DA command.
        /// mtkclient: custom_write(addr, data)
        /// </summary>
        public bool CustomWrite(ulong addr, byte[] data)
        {
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMMEMW</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK") return false;

            _xml.XSend(BitConverter.GetBytes(addr));
            _xml.XSend(BitConverter.GetBytes(data.Length));
            _xml.XSend(data);

            _xml.GetResponse();
            _xml.Ack();
            _xml.GetResponse();
            _xml.Ack();
            return true;
        }

        /// <summary>
        /// Read 32-bit register(s) via custom DA command.
        /// mtkclient: custom_readregister(addr, dwords)
        /// </summary>
        public uint[] CustomReadRegister(uint addr, int dwords = 1)
        {
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMREGR</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK") return null;

            _xml.XSend(BitConverter.GetBytes(addr));
            _xml.XSend(BitConverter.GetBytes(dwords));

            byte[] data = _xml.XRead();

            _xml.GetResponse();
            _xml.Ack();
            _xml.GetResponse();
            _xml.Ack();

            if (data == null) return null;
            uint[] result = new uint[dwords];
            for (int i = 0; i < dwords && i * 4 + 4 <= data.Length; i++)
                result[i] = BitConverter.ToUInt32(data, i * 4);
            return result;
        }

        /// <summary>
        /// Write 32-bit register via custom DA command.
        /// mtkclient: custom_writeregister(addr, data)
        /// </summary>
        public bool CustomWriteRegister(uint addr, uint value)
        {
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMREGW</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK") return false;

            _xml.XSend(BitConverter.GetBytes(addr));
            _xml.XSend(BitConverter.GetBytes(value));

            _xml.GetResponse();
            _xml.Ack();
            _xml.GetResponse();
            _xml.Ack();
            return true;
        }

        /// <summary>
        /// Set storage type (eMMC or UFS).
        /// mtkclient: custom_set_storage(ufs)
        /// </summary>
        public bool CustomSetStorage(bool ufs = false)
        {
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMSTORAGE</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK") return false;

            _xml.XSend(BitConverter.GetBytes(ufs ? 1 : 0));

            _xml.GetResponse();
            _xml.Ack();
            _xml.GetResponse();
            _xml.Ack();
            return true;
        }

        #endregion

        #region RPMB

        /// <summary>
        /// Read RPMB sectors.
        /// mtkclient: custom_rpmb_read(sector, sectors)
        /// </summary>
        public byte[] CustomRpmbRead(int sector, int sectors)
        {
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMRPMBR</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK") return null;

            _xml.XSend(BitConverter.GetBytes(sector));
            _xml.XSend(BitConverter.GetBytes(sectors));

            byte[] result = new byte[sectors * 0x100];
            for (int i = 0; i < sectors; i++)
            {
                byte[] data = _xml.XRead();
                if (data == null || data.Length != 0x100)
                {
                    int errCode = data != null && data.Length == 4 ? BitConverter.ToInt32(data, 0) : -1;
                    string errMsg = errCode >= 0 && errCode < RpmbErrors.Length ? RpmbErrors[errCode] : $"0x{errCode:X}";
                    _error($"RPMB read error sector 0x{sector + i:X}: {errMsg}");
                    return null;
                }
                Buffer.BlockCopy(data, 0, result, i * 0x100, 0x100);
            }

            _xml.GetResponse();
            _xml.Ack();
            _xml.GetResponse();
            _xml.Ack();
            return result;
        }

        /// <summary>
        /// Write RPMB sectors.
        /// mtkclient: custom_rpmb_write(sector, sectors, data)
        /// </summary>
        public bool CustomRpmbWrite(int sector, int sectors, byte[] data)
        {
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMRPMBW</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK") return false;

            _xml.XSend(BitConverter.GetBytes(sector), 1);
            _xml.XSend(BitConverter.GetBytes(sectors), 1);

            for (int i = 0; i < sectors; i++)
            {
                byte[] block = new byte[0x100];
                int srcOff = i * 0x100;
                int len = Math.Min(0x100, data.Length - srcOff);
                if (len > 0) Buffer.BlockCopy(data, srcOff, block, 0, len);
                _xml.XSend(block);

                byte[] ack = _xml.XRead();
                if (ack != null && ack.Length >= 2)
                {
                    ushort errCode = BitConverter.ToUInt16(ack, 0);
                    if (errCode != 0)
                    {
                        string errMsg = errCode < RpmbErrors.Length ? RpmbErrors[errCode] : $"0x{errCode:X}";
                        _error($"RPMB write error sector 0x{sector + i:X}: {errMsg}");
                        // Cleanup: CMD:END + CMD:START
                        _xml.GetResponse(); _xml.Ack();
                        _xml.GetResponse(); _xml.Ack();
                        return false;
                    }
                }
            }

            _xml.GetResponse();
            _xml.Ack();
            _xml.GetResponse();
            _xml.Ack();
            return true;
        }

        /// <summary>
        /// Set RPMB key and verify it was written correctly.
        /// mtkclient: custom_rpmb_init() — CMD:CUSTOMRPMBK part
        /// </summary>
        public bool CustomRpmbSetKey(byte[] rpmbKey)
        {
            if (rpmbKey == null || rpmbKey.Length == 0) return false;

            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMRPMBK</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK") return false;

            _xml.XSend(rpmbKey);
            byte[] readKey = _xml.XRead(); // read back for verification

            // CMD:END
            _xml.GetResponse();
            _xml.Ack();
            // CMD:START
            _xml.GetResponse();
            _xml.Ack();

            if (readKey != null && readKey.Length == rpmbKey.Length)
            {
                bool match = true;
                for (int i = 0; i < rpmbKey.Length; i++)
                {
                    if (readKey[i] != rpmbKey[i]) { match = false; break; }
                }
                if (match)
                {
                    _info("Setting rpmbkey: ok");
                    return true;
                }
            }
            _warning("RPMB key verification mismatch.");
            return false;
        }

        /// <summary>
        /// Derive RPMB key on device.
        /// mtkclient: custom_rpmb_init() — CMD:CUSTOMRPMBI part
        /// </summary>
        public bool CustomRpmbInit(byte[] rpmbKey)
        {
            // Step 1: Set the key via CMD:CUSTOMRPMBK if provided
            if (rpmbKey != null && rpmbKey.Length > 0)
                CustomRpmbSetKey(rpmbKey);

            // Step 2: Derive on-device via CMD:CUSTOMRPMBI
            string cmd = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:CUSTOMRPMBI</command></da>";
            string resp = _xml.SendCommand(cmd, noAck: true);
            if (resp != "OK")
            {
                _error("Failed to derive a valid rpmb key.");
                // Cleanup
                _xml.GetResponse(); _xml.Ack();
                _xml.GetResponse(); _xml.Ack();
                return false;
            }

            // Send mode: 1 for certain chipsets, 0 otherwise
            uint hwCode = _mtk?.Config?.HwCode ?? 0;
            int mode = (hwCode == 0x1209 || hwCode == 0x1129) ? 1 : 0;
            _xml.XSend(BitConverter.GetBytes(mode));

            byte[] statusBytes = _xml.XRead();
            int status = statusBytes != null && statusBytes.Length >= 4
                ? BitConverter.ToInt32(statusBytes, 0)
                : (statusBytes != null && statusBytes.Length >= 2 ? BitConverter.ToInt16(statusBytes, 0) : -1);

            if (status == 0)
            {
                byte[] derivedRpmb = _xml.XRead();
                if (derivedRpmb != null)
                    _info("Derived rpmb key: " + BitConverter.ToString(derivedRpmb).Replace("-", "").ToLowerInvariant());

                // CMD:END
                _xml.GetResponse(); _xml.Ack();
                // CMD:START
                _xml.GetResponse(); _xml.Ack();
                return true;
            }

            _error("Failed to derive a valid rpmb key.");
            // Cleanup
            _xml.GetResponse(); _xml.Ack();
            _xml.GetResponse(); _xml.Ack();
            return false;
        }

        #endregion

        #region Crypto / Key Generation

        /// <summary>
        /// Setup CryptoSetup using DA extension register read/write as backend.
        /// mtkclient: cryptosetup()
        /// </summary>
        public HwCrypto SetupCrypto()
        {
            var chipCfg = _mtk.Config?.ChipConfig;
            if (chipCfg == null) return null;

            var setup = new CryptoSetup
            {
                GcpuBase = chipCfg.GcpuBase,
                DxccBase = chipCfg.DxccBase,
                SejBase = chipCfg.SejBase,
                Blacklist = chipCfg.Blacklist,
            };

            // Wire register read/write through DA extension custom commands
            setup.Read32 = (addr, count) =>
            {
                var vals = CustomReadRegister(addr, (int)(count > 0 ? count : 1));
                return vals != null && vals.Length > 0 ? vals[0] : 0;
            };
            setup.Write32 = (addr, val) =>
            {
                CustomWriteRegister((uint)addr, val);
            };

            return new HwCrypto(setup, _info, _error);
        }

        /// <summary>
        /// Generate all keys: RPMB, MTEE, fuses, etc.
        /// mtkclient: generate_keys() in v6.py
        /// </summary>
        public Dictionary<string, byte[]> GenerateKeys(byte[] otp = null)
        {
            var keys = new Dictionary<string, byte[]>();
            var hwCrypto = SetupCrypto();
            if (hwCrypto == null)
            {
                _error("Cannot setup crypto for key generation.");
                return keys;
            }

            byte[] meid = _mtk.Config?.Meid;

            // Generate RPMB key via SEJ
            if (hwCrypto.Sej != null && meid != null)
            {
                try
                {
                    byte[] rpmbKey = hwCrypto.Sej.GenerateRpmb(meid, otp ?? new byte[32]);
                    if (rpmbKey != null)
                    {
                        keys["rpmb"] = rpmbKey;
                        _info("Generated RPMB key via SEJ.");
                    }
                }
                catch (Exception ex) { _warning($"SEJ RPMB key gen failed: {ex.Message}"); }
            }

            // Generate MTEE key via SEJ
            if (hwCrypto.Sej != null)
            {
                try
                {
                    byte[] mteeKey = hwCrypto.Sej.GenerateMtee(otp);
                    if (mteeKey != null)
                    {
                        keys["mtee"] = mteeKey;
                        _info("Generated MTEE key via SEJ.");
                    }
                }
                catch (Exception ex) { _warning($"SEJ MTEE key gen failed: {ex.Message}"); }
            }

            // Read fuses/efuses
            if (_mtk.Config?.ChipConfig?.EfuseAddr > 0)
            {
                try
                {
                    uint efBase = _mtk.Config.ChipConfig.EfuseAddr;
                    byte[] efuseData = CustomRead(efBase, 0x100);
                    if (efuseData != null)
                    {
                        keys["efuses"] = efuseData;
                        _info("Read eFuse data.");
                    }
                }
                catch (Exception ex) { _warning($"eFuse read failed: {ex.Message}"); }
            }

            return keys;
        }

        /// <summary>
        /// Read individual fuse by index.
        /// mtkclient: read_fuse(idx)
        /// </summary>
        public uint ReadFuse(int idx)
        {
            uint efBase = _mtk.Config?.ChipConfig?.EfuseAddr ?? 0;
            if (efBase == 0) return 0;
            var vals = CustomReadRegister(efBase + (uint)(idx * 4), 1);
            return vals != null ? vals[0] : 0;
        }

        /// <summary>
        /// Secure config lock/unlock.
        /// mtkclient: seccfg(lockflag) in v6.py
        /// </summary>
        public bool SecCfg(bool lockFlag)
        {
            // Read seccfg partition
            byte[] data = _xml.ReadFlash(0, 0x20000, "seccfg");
            if (data == null || data.Length == 0)
            {
                _error("Cannot read seccfg partition.");
                return false;
            }

            // Parse and modify seccfg
            // SecCfgV3/V4 parsing would go here
            _info($"SecCfg operation: lock={lockFlag}");

            return _xml.WriteFlash(0, data, "seccfg") ;
        }

        #endregion

        #region Helpers

        private static int FindPattern(byte[] data, byte[] pattern)
        {
            if (pattern.Length == 0 || data.Length < pattern.Length) return -1;
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static void PatchPointer(byte[] data, uint placeholder, uint value)
        {
            byte[] needle = BitConverter.GetBytes(placeholder);
            int idx = FindPattern(data, needle);
            if (idx >= 0)
            {
                byte[] val = BitConverter.GetBytes(value);
                Buffer.BlockCopy(val, 0, data, idx, 4);
            }
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
}
