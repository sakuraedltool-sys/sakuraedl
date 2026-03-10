// mtkclient port: DA/mtk_daloader.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.IO;
using System.Security.Cryptography;
using SakuraEDL.MediaTek.Config;
using SakuraEDL.MediaTek.Connection;
using SakuraEDL.MediaTek.Protocol;
using SakuraEDL.MediaTek.DA.XFlash;
using SakuraEDL.MediaTek.DA.XmlFlash;
using SakuraEDL.MediaTek.Utility;

namespace SakuraEDL.MediaTek.DA
{
    /// <summary>
    /// DA Loader — orchestrates DA upload and protocol initialization.
    /// Port of mtkclient/Library/DA/mtk_daloader.py
    /// </summary>
    public class DaLoader
    {
        private readonly Port _port;
        private readonly MtkConfig _config;
        private readonly Preloader _preloader;
        private readonly Action<string> _info;
        private readonly Action<string> _debug;
        private readonly Action<string> _warning;
        private readonly Action<string> _error;

        public DaConfig DaConfigInstance { get; set; }
        public XFlashLib Xft { get; set; }
        public XmlLib Xmlft { get; set; }
        public XmlFlashExt XmlExt { get; set; }
        public bool Patch { get; set; }
        public bool DaExt { get; set; }
        public DAmodes FlashMode { get; set; }

        public DaLoader(Port port, MtkConfig config, Preloader preloader,
                        Action<string> info = null, Action<string> debug = null,
                        Action<string> warning = null, Action<string> error = null)
        {
            _port = port;
            _config = config;
            _preloader = preloader;
            _info = info ?? delegate { };
            _debug = debug ?? delegate { };
            _warning = warning ?? delegate { };
            _error = error ?? delegate { };

            DaConfigInstance = new DaConfig(config, config?.Loader, info, error, warning);
        }

        /// <summary>
        /// Write connection state to .state file.
        /// mtkclient: writestate()
        /// </summary>
        public void WriteState()
        {
            try
            {
                string path = _config.HwParamPath;
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);

                string stateFile = Path.Combine(path, ".state");
                string flashMode = FlashMode == DAmodes.LEGACY ? "LEGACY" :
                                   FlashMode == DAmodes.XFLASH ? "XFLASH" : "XML";

                string json = $"{{\"flashmode\":\"{flashMode}\",\"patched\":{(Patch ? "true" : "false")}," +
                              $"\"hwcode\":{_config.HwCode}}}";
                File.WriteAllText(stateFile, json);
            }
            catch { }
        }

        /// <summary>
        /// Compute DA hash position for security bypass.
        /// mtkclient: compute_hash_pos(da1, da2, da1sig_len, da2sig_len, v6)
        /// </summary>
        public (int idx, int hashMode, int hashLen) ComputeHashPos(byte[] da1, byte[] da2, int da1SigLen, int da2SigLen, bool v6)
        {
            int hashLen = da2.Length - da2SigLen;
            var (hashMode, idx) = CalcDaHash(da1, da2, hashLen);
            if (idx == -1)
            {
                hashLen = da2.Length;
                (hashMode, idx) = CalcDaHash(da1, da2, hashLen);
                if (idx == -1)
                {
                    hashLen = da2.Length - da2SigLen;
                    if (v6)
                        (idx, hashMode) = FindDaHashV6(da1, da1SigLen);
                    else
                        (idx, hashMode) = FindDaHashV5(da1);

                    if (idx == -1)
                    {
                        _error("Hash computation failed.");
                        return (-1, -1, -1);
                    }
                }
            }
            return (idx, hashMode, hashLen);
        }

        /// <summary>
        /// Calculate DA hash and find its position in DA1.
        /// </summary>
        private (int hashMode, int idx) CalcDaHash(byte[] da1, byte[] da2, int hashLen)
        {
            byte[] hashData = new byte[hashLen];
            Buffer.BlockCopy(da2, 0, hashData, 0, hashLen);

            // Try SHA-256 first
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(hashData);
                int idx = ByteUtils.FindBytes(da1, hash);
                if (idx != -1)
                    return (2, idx); // hashmode 2 = SHA256
            }

            // Try SHA-1
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(hashData);
                int idx = ByteUtils.FindBytes(da1, hash);
                if (idx != -1)
                    return (1, idx); // hashmode 1 = SHA1
            }

            return (-1, -1);
        }

        /// <summary>
        /// Find DA hash position for V5 (XFlash) protocol.
        /// </summary>
        private (int idx, int hashMode) FindDaHashV5(byte[] da1)
        {
            // Search for SHA-256 hash pattern in DA1
            // V5 stores hash at known offset patterns
            byte[] pattern256 = new byte[] { 0x20, 0x00, 0x00, 0x00 }; // hash length = 0x20
            for (int i = 0; i < da1.Length - 36; i++)
            {
                if (da1[i] == pattern256[0] && da1[i + 1] == pattern256[1] &&
                    da1[i + 2] == pattern256[2] && da1[i + 3] == pattern256[3])
                {
                    // Verify this looks like a hash (high entropy)
                    bool isHash = true;
                    int zeros = 0;
                    for (int j = 4; j < 36; j++)
                    {
                        if (da1[i + j] == 0) zeros++;
                    }
                    if (zeros < 4 && isHash)
                        return (i + 4, 2);
                }
            }
            return (-1, -1);
        }

        /// <summary>
        /// Find DA hash position for V6 (XML) protocol.
        /// </summary>
        private (int idx, int hashMode) FindDaHashV6(byte[] da1, int da1SigLen)
        {
            int searchEnd = da1.Length - da1SigLen;
            // V6 uses different hash storage, search from end
            for (int i = searchEnd - 0x200; i > 0; i--)
            {
                // Look for SHA-256 hash block
                bool allZero = true;
                for (int j = 0; j < 32; j++)
                {
                    if (da1[i + j] != 0) { allZero = false; break; }
                }
                if (!allZero)
                {
                    // Check if surrounded by zeros (hash block pattern)
                    bool prevZero = true;
                    for (int j = -4; j < 0; j++)
                    {
                        if (i + j >= 0 && da1[i + j] != 0) { prevZero = false; break; }
                    }
                    if (prevZero)
                        return (i, 2);
                }
            }
            return (-1, -1);
        }

        /// <summary>
        /// Upload DA to device — main entry point.
        /// mtkclient: upload_da()
        /// </summary>
        public bool UploadDa()
        {
            var daLoader = DaConfigInstance.Setup();
            if (daLoader == null)
            {
                _error("No DA loader found for this chip.");
                return false;
            }

            FlashMode = _config.ChipConfig.DaMode;
            _info($"DA Mode: {FlashMode}, Loader: {daLoader.Loader}");

            // Read DA1 data
            if (daLoader.Regions.Count < 1)
            {
                _error("DA has no regions defined.");
                return false;
            }

            int da1RegionIndex = ResolveDa1RegionIndex(daLoader);
            if (da1RegionIndex < 0)
            {
                _error("DA has no usable stage 1 region.");
                return false;
            }

            byte[] da1Data = DaConfigInstance.ReadDaRegion(daLoader, da1RegionIndex);
            if (da1Data == null)
            {
                _error("Failed to read DA1 data.");
                return false;
            }

            var region0 = daLoader.Regions[da1RegionIndex];
            _info($"Uploading DA1 (region {da1RegionIndex}) to 0x{region0.StartAddr:X} (size: 0x{da1Data.Length:X})...");

            // Send DA1 via preloader
            if (!_preloader.SendDa(region0.StartAddr, (int)region0.Len, (int)region0.SigLen, da1Data))
            {
                _error("Failed to send DA1.");
                return false;
            }

            // Jump to DA1
            if (!_preloader.JumpDa(region0.StartAddr))
            {
                _error("Failed to jump to DA1.");
                return false;
            }

            _info("DA1 uploaded and running.");

            // Initialize protocol based on DA mode
            if (FlashMode == DAmodes.XFLASH)
            {
                return InitXFlash(daLoader, da1RegionIndex);
            }
            else if (FlashMode == DAmodes.XML)
            {
                return InitXml(daLoader, da1RegionIndex);
            }
            else
            {
                return InitLegacy(daLoader);
            }
        }

        private static bool IsUsableRegion(EntryRegion region)
        {
            return region != null && region.Len > 0;
        }

        private static bool IsSameRegion(EntryRegion left, EntryRegion right)
        {
            if (left == null || right == null) return false;
            return left.StartOffset == right.StartOffset
                && left.StartAddr == right.StartAddr
                && left.Len == right.Len
                && left.SigLen == right.SigLen;
        }

        private static int ResolveDa1RegionIndex(DaEntry daLoader)
        {
            if (daLoader?.Regions == null || daLoader.Regions.Count == 0)
                return -1;

            int idx = daLoader.EntryRegionIndex;
            if (idx < 0 || idx >= daLoader.Regions.Count || !IsUsableRegion(daLoader.Regions[idx]))
                idx = 0;

            if (IsUsableRegion(daLoader.Regions[idx]))
                return idx;

            for (int i = 0; i < daLoader.Regions.Count; i++)
            {
                if (IsUsableRegion(daLoader.Regions[i]))
                    return i;
            }

            return -1;
        }

        private static int ResolveDa2RegionIndex(DaEntry daLoader, int da1RegionIndex)
        {
            if (daLoader?.Regions == null || daLoader.Regions.Count < 2)
                return -1;
            if (da1RegionIndex < 0 || da1RegionIndex >= daLoader.Regions.Count)
                return -1;

            var da1Region = daLoader.Regions[da1RegionIndex];

            // Prefer the first region after DA1 that is not a duplicate.
            for (int i = da1RegionIndex + 1; i < daLoader.Regions.Count; i++)
            {
                var candidate = daLoader.Regions[i];
                if (IsUsableRegion(candidate) && !IsSameRegion(candidate, da1Region))
                    return i;
            }

            // Fallback: scan all non-DA1 regions for any usable non-duplicate.
            for (int i = 0; i < daLoader.Regions.Count; i++)
            {
                if (i == da1RegionIndex) continue;
                var candidate = daLoader.Regions[i];
                if (IsUsableRegion(candidate) && !IsSameRegion(candidate, da1Region))
                    return i;
            }

            // Last fallback: any other usable region.
            for (int i = daLoader.Regions.Count - 1; i >= 0; i--)
            {
                if (i == da1RegionIndex) continue;
                if (IsUsableRegion(daLoader.Regions[i]))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Initialize XFlash protocol after DA1 upload.
        /// mtkclient: DAXFlash.upload_da() — full flow
        /// Flow: sync → setup_env → setup_hw_init → SYNC wait →
        ///       get_expire_date → set_reset_key → set_checksum_level →
        ///       get_connection_agent → [send_emi if brom] →
        ///       boot_to(DA2) → get_sla_status → handle_sla →
        ///       reinit → [patch + boot_to(extensions) + custom_ack]
        /// </summary>
        private bool InitXFlash(DaEntry daLoader, int da1RegionIndex)
        {
            Xft = new XFlashLib(_port, _config, DaConfigInstance, _info, _debug, _warning, _error);

            // Wait for 0xC0 sync byte from DA1
            byte[] syncByte = _port.UsbRead(1);
            if (syncByte == null || syncByte.Length == 0 || syncByte[0] != 0xC0)
            {
                _error("XFlash: DA sync byte 0xC0 not received.");
                return false;
            }

            // Sync
            if (!Xft.Sync())
            {
                _error("XFlash sync failed.");
                return false;
            }
            _info("XFlash sync OK.");

            // Setup environment and hardware
            Xft.SetupEnv();
            Xft.SetupHwInit();

            // Wait for SYNC_SIGNAL from DA1
            byte[] syncRes = _port.UsbRead(4);
            if (syncRes == null || syncRes.Length < 4 ||
                BitConverter.ToUInt32(syncRes, 0) != XFlashCmd.SYNC_SIGNAL)
            {
                _error("XFlash: DA sync signal not received after hw_init.");
                return false;
            }
            _info("Successfully received DA sync.");

            // Post-sync setup
            Xft.GetExpireDate();
            Xft.SetResetKey(0x68);
            Xft.SetChecksumLevel(0);

            // Determine connection type (brom or preloader)
            string connAgent = Xft.GetConnectionAgent();
            _info($"Connection agent: {connAgent ?? "unknown"}");

            int stage = 0;

            if (connAgent == "brom")
            {
                stage = 1;
                // Need EMI/DRAM config for BROM connection
                if (DaConfigInstance.Emi != null)
                {
                    _info("Sending EMI data...");
                    if (!Xft.SendEmi(DaConfigInstance.Emi))
                    {
                        _error("EMI data not accepted.");
                        return false;
                    }
                    _info("EMI data accepted.");
                }
                else
                {
                    _warning("No EMI/DRAM config. Operation may fail due to missing DRAM setup.");
                }
            }
            else if (connAgent == "preloader")
            {
                stage = 1;
            }

            if (stage != 1)
            {
                _error($"Unexpected connection agent: {connAgent}");
                return false;
            }

            int da2RegionIndex = ResolveDa2RegionIndex(daLoader, da1RegionIndex);
            if (da2RegionIndex < 0)
            {
                _error("DA has no usable stage 2 region.");
                return false;
            }
            _info($"XFlash regions: DA1={da1RegionIndex}, DA2={da2RegionIndex}");

            byte[] da1Data = DaConfigInstance.ReadDaRegion(daLoader, da1RegionIndex);
            byte[] da2Data = DaConfigInstance.ReadDaRegion(daLoader, da2RegionIndex);
            if (da2Data == null)
            {
                _error("Failed to read DA2 data.");
                return false;
            }

            // Patch DA if needed
            int da1SigLen = (int)daLoader.Regions[da1RegionIndex].SigLen;
            int da2SigLen = (int)daLoader.Regions[da2RegionIndex].SigLen;

            if (Patch || !(_config.TargetConfig.ContainsKey("sbc") && _config.TargetConfig["sbc"]))
            {
                // Compute hash and patch DA
                var (hashIdx, hashMode, hashLen) = ComputeHashPos(
                    da1Data, da2Data, da1SigLen, da2SigLen, daLoader.V6);

                if (hashIdx >= 0)
                {
                    // Patch DA2 via extension
                    if (XmlExt != null)
                    {
                        da1Data = XmlExt.PatchDa1(da1Data);
                        da2Data = XmlExt.PatchDa2(da2Data);
                    }
                    da1Data = FixHash(da1Data, da2Data, hashIdx, hashMode, hashLen);
                    Patch = true;
                    DaConfigInstance.Da2 = SubArray(da2Data, 0, hashLen);
                }
                else
                {
                    Patch = false;
                    DaConfigInstance.Da2 = SubArray(da2Data, 0, da2Data.Length - da2SigLen);
                }
            }
            else
            {
                Patch = false;
                DaConfigInstance.Da2 = SubArray(da2Data, 0, da2Data.Length - da2SigLen);
            }

            // Upload DA2 via boot_to
            var region2 = daLoader.Regions[da2RegionIndex];
            _info($"Uploading stage 2 (region {da2RegionIndex}) to 0x{region2.StartAddr:X}...");
            if (!Xft.BootTo(region2.StartAddr, DaConfigInstance.Da2))
            {
                _error("Error on booting to DA2 (xflash).");
                return false;
            }
            _info("Successfully uploaded stage 2.");

            // SLA authentication
            bool slaEnabled = Xft.GetSlaStatus();
            if (slaEnabled)
            {
                _info("DA SLA is enabled.");
                if (!Xft.HandleSla(DaConfigInstance.Da2))
                    _error("Can't bypass DA SLA.");
            }
            else
            {
                _info("DA SLA is disabled.");
            }

            // Reinit: collect storage info, chip IDs, USB speed
            Xft.Reinit(true);

            // Load extensions if patched
            if (Patch)
            {
                byte[] extData = null;
                if (XmlExt != null)
                    extData = XmlExt.GetExtensionPayload();

                if (extData != null)
                {
                    DaExt = false;
                    if (Xft.BootTo(Xft.ExtensionsAddress, extData))
                    {
                        // Verify extension via CUSTOM_ACK devctrl
                        byte[] ackRes = Xft.SendDevCtrl(XFlashCmd.UNKNOWN_CTRL_CODE);
                        int ackStatus = Xft.Status();
                        if (ackStatus == 0 && ackRes != null && ackRes.Length >= 4 &&
                            BitConverter.ToUInt32(ackRes, 0) == 0xA1A2A3A4)
                        {
                            _info($"DA Extensions successfully added at 0x{Xft.ExtensionsAddress:X}.");
                            DaExt = true;
                            Xft.DaExt = true;
                        }

                        if (!DaExt)
                            _warning("DA Extensions failed to enable.");
                    }
                }
            }

            WriteState();
            _info("XFlash DA ready.");
            return true;
        }

        /// <summary>
        /// Initialize XML V6 protocol after DA1 upload.
        /// mtkclient: DAXML.upload_da() — full flow with DA patching, exploit, extension loading.
        /// Flow: patch DA2 → boot_to → setup_hw_init → change_usb_speed → check_sla →
        ///       handle_sla → reinit → check_lifecycle → CMD:CUSTOM extension.
        /// </summary>
        private bool InitXml(DaEntry daLoader, int da1RegionIndex)
        {
            Xmlft = new XmlLib(_port, _config, DaConfigInstance, _info, _debug, _warning, _error);
            DaExt = false;

            int da2RegionIndex = ResolveDa2RegionIndex(daLoader, da1RegionIndex);
            if (da2RegionIndex < 0)
            {
                _error("DA has no usable XML stage 2 region.");
                return false;
            }
            _info($"XML regions: DA1={da1RegionIndex}, DA2={da2RegionIndex}");

            // DA2 loading + patching
            bool loaded = false;
            if (daLoader.Regions.Count >= 2)
            {
                byte[] da2Data = DaConfigInstance.ReadDaRegion(daLoader, da2RegionIndex);
                if (da2Data != null)
                {
                    DaConfigInstance.Da2 = da2Data;
                    var region1 = daLoader.Regions[da2RegionIndex];
                    uint da2Offset = region1.StartAddr;

                    // Determine whether to patch
                    bool hasSbc = false;
                    _config.TargetConfig?.TryGetValue("sbc", out hasSbc);
                    bool shouldPatch = Patch || !hasSbc;

                    if (shouldPatch)
                    {
                        // Compute hash position before patching
                        byte[] da1Data = DaConfigInstance.ReadDaRegion(daLoader, da1RegionIndex);
                        int da1SigLen = (int)(daLoader.Regions.Count > da1RegionIndex ? daLoader.Regions[da1RegionIndex].SigLen : 0);
                        int da2SigLen = (int)region1.SigLen;

                        // Patch DA2: inject custom command + bypass security checks
                        byte[] patchedDa2 = XmlFlashExt.PatchDa2Static(da2Data, _info);
                        if (patchedDa2.Length == da2Data.Length)
                        {
                            da2Data = patchedDa2;
                            Patch = true;
                            _info("DA2 patched successfully.");

                            // Fix DA hash in DA1 to match patched DA2
                            if (da1Data != null)
                            {
                                var (hashIdx, hashMode, hashLen) = ComputeHashPos(da1Data, da2Data, da1SigLen, da2SigLen, true);
                                if (hashIdx >= 0)
                                {
                                    da1Data = FixHash(da1Data, da2Data, hashIdx, hashMode, hashLen);
                                    DaConfigInstance.Da2 = da2Data.Length > hashLen
                                        ? SubArray(da2Data, 0, hashLen) : da2Data;
                                    _info($"DA hash fixed at 0x{hashIdx:X} (mode={hashMode}, len={hashLen}).");
                                }
                            }
                        }
                        else
                        {
                            _warning("DA2 patched length mismatch, using unpatched.");
                            Patch = false;
                            DaConfigInstance.Da2 = da2Data;
                        }
                    }
                    else
                    {
                        Patch = false;
                        DaConfigInstance.Da2 = da2Data;
                    }

                    // Upload DA2 via BootTo
                    _info($"XML: Uploading DA2 to 0x{da2Offset:X} (size: 0x{da2Data.Length:X})...");
                    loaded = Xmlft.BootTo(da2Offset, da2Data);

                    if (!loaded && Patch)
                    {
                        // Patched DA2 upload failed — try stock DA2
                        _warning("Patched DA2 upload failed, trying stock...");
                        da2Data = DaConfigInstance.ReadDaRegion(daLoader, da2RegionIndex);
                        DaConfigInstance.Da2 = da2Data;
                        loaded = Xmlft.BootTo(da2Offset, da2Data);
                        Patch = false;
                        if (!loaded)
                        {
                            _error("DA2 upload failed.");
                            return false;
                        }
                    }

                    if (loaded)
                        _info("DA2 uploaded successfully.");
                }
            }

            // Step 1: Setup HW init (host capabilities + notify)
            Xmlft.SetupHwInit();

            // Step 2: USB high-speed
            Xmlft.ChangeUsbSpeed();

            // Step 3: SLA authentication (CheckSla + HandleSla combined)
            Xmlft.DoSlaAuth();

            // Step 4: Reinit (get HW info)
            Xmlft.Reinit();

            // Step 5: Check lifecycle
            Xmlft.CheckLifecycle();

            // Step 6: Load DA Extension if patched
            if (Patch)
            {
                // Initialize XmlExt here since we need _mtk reference
                // XmlExt will be set by MtkClass after we return

                _info("Loading DA XML Extension...");
                string customXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                                   "<command>CMD:CUSTOM</command></da>";
                if (Xmlft.XSendStr(customXml))
                {
                    string resp = Xmlft.GetResponse();
                    if (resp == "OK")
                    {
                        // XmlExt may not be initialized yet — try getting payload from static path
                        byte[] extPayload = LoadExtensionPayload();
                        if (extPayload != null)
                        {
                            Xmlft.XSend(BitConverter.GetBytes(extPayload.Length));
                            Xmlft.XSend(extPayload);

                            // CMD:END
                            Xmlft.GetResponse();
                            Xmlft.Ack();
                            // CMD:START
                            Xmlft.GetResponse();
                            Xmlft.Ack();

                            DaExt = true;
                            _info("DA XML Extension uploaded.");
                        }
                        else
                        {
                            _warning("No DA extension payload available.");
                            DaExt = false;
                        }
                    }
                }
            }

            WriteState();
            _info("XML V6 DA ready.");
            return true;
        }

        /// <summary>
        /// Load DA extension payload from known paths.
        /// </summary>
        private byte[] LoadExtensionPayload()
        {
            string[] paths = {
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payloads", "da_xml.bin"),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "da_xml.bin"),
            };
            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    byte[] data = File.ReadAllBytes(p);
                    _info($"Loaded DA extension: {data.Length} bytes from {p}");
                    return data;
                }
            }
            return null;
        }

        /// <summary>
        /// Fix DA hash in DA1 after patching DA2.
        /// mtkclient: fix_hash(da1, da2, hashaddr, hashmode, hashlen)
        /// </summary>
        public byte[] FixHash(byte[] da1, byte[] da2, int hashAddr, int hashMode, int hashLen)
        {
            byte[] result = (byte[])da1.Clone();
            byte[] hashData = new byte[hashLen];
            Buffer.BlockCopy(da2, 0, hashData, 0, Math.Min(da2.Length, hashLen));

            byte[] newHash;
            if (hashMode == 2)
            {
                using (var sha256 = SHA256.Create())
                    newHash = sha256.ComputeHash(hashData);
            }
            else
            {
                using (var sha1 = SHA1.Create())
                    newHash = sha1.ComputeHash(hashData);
            }

            Buffer.BlockCopy(newHash, 0, result, hashAddr, newHash.Length);
            return result;
        }

        private static byte[] SubArray(byte[] data, int offset, int length)
        {
            byte[] result = new byte[length];
            Buffer.BlockCopy(data, offset, result, 0, Math.Min(data.Length - offset, length));
            return result;
        }

        /// <summary>
        /// Initialize Legacy protocol after DA1 upload.
        /// Legacy protocol uses binary commands (pre-XFlash/XML).
        /// </summary>
        private bool InitLegacy(DaEntry daLoader)
        {
            _warning("Legacy DA protocol is not fully supported. Attempting XFlash fallback...");

            // Many legacy devices can still use XFlash protocol
            Xft = new XFlashLib(_port, _config, DaConfigInstance, _info, _debug, _warning, _error);
            if (Xft.Sync())
            {
                _info("Legacy: XFlash sync succeeded — using XFlash protocol.");
                FlashMode = DAmodes.XFLASH;

                Xft.SetChecksumLevel(0);
                Xft.SetBatteryOpt(2);
                Xft.SetResetKey(0x68);
                Xft.GetDaVersion();

                var emmc = Xft.GetEmmcInfo(true);
                if (emmc != null && emmc.UserSize > 0)
                {
                    DaConfigInstance.Storage.FlashType = "emmc";
                    DaConfigInstance.Storage.Emmc = emmc;
                }
                DaConfigInstance.Storage.SetFlashSize();
                WriteState();
                return true;
            }

            _error("Legacy protocol: Cannot establish communication with DA.");
            WriteState();
            return false;
        }

    }
}
