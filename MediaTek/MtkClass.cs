// mtkclient port: mtk_class.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SakuraEDL.MediaTek.Auth;
using SakuraEDL.MediaTek.Config;
using SakuraEDL.MediaTek.Connection;
using SakuraEDL.MediaTek.DA;
using SakuraEDL.MediaTek.Utility;
using SakuraEDL.MediaTek.DA.XFlash;
using SakuraEDL.MediaTek.Exploit;
using SakuraEDL.MediaTek.Hardware;
using SakuraEDL.MediaTek.Protocol;

namespace SakuraEDL.MediaTek
{
    /// <summary>
    /// Connection state for the MTK flashing flow.
    /// </summary>
    public enum MtkState
    {
        Disconnected,
        Handshake,
        PreloaderReady,
        DaLoading,
        DaReady,
        Error
    }

    /// <summary>
    /// MtkClass — top-level orchestrator for MediaTek device communication.
    /// Port of mtkclient/Library/mtk_class.py
    /// Manages the full flow: connection → handshake → device info → DA upload → flash operations.
    /// </summary>
    public class MtkClass : IDisposable
    {
        // Components
        public Port Port { get; private set; }
        public MtkConfig Config { get; private set; }
        public Preloader Preloader { get; private set; }
        public DaLoader DaLoader { get; private set; }
        public DaConfig DaConfigInstance => DaLoader?.DaConfigInstance;
        public DaHandler DaHandler { get; private set; }
        public HwCrypto HwCrypto { get; private set; }

        // State
        public MtkState State { get; set; } = MtkState.Disconnected;
        private bool _disposed;

        // Callbacks
        public Action<string> OnInfo { get; set; }
        public Action<string> OnDebug { get; set; }
        public Action<string> OnWarning { get; set; }
        public Action<string> OnError { get; set; }
        public Action<string> OnStatusChange { get; set; }
        public Action<int> OnProgress { get; set; }

        public MtkClass(MtkConfig config = null)
        {
            Config = config ?? new MtkConfig();

            // Default log handlers
            OnInfo = delegate { };
            OnDebug = delegate { };
            OnWarning = delegate { };
            OnError = delegate { };
            OnStatusChange = delegate { };

            // Route subsystem callbacks through live delegates so handlers assigned
            // after construction are still used by Port/Preloader/DA loader.
            Action<string> infoProxy = msg => OnInfo?.Invoke(msg);
            Action<string> debugProxy = msg => OnDebug?.Invoke(msg);
            Action<string> warningProxy = msg => OnWarning?.Invoke(msg);
            Action<string> errorProxy = msg => OnError?.Invoke(msg);

            Port = new Port(infoProxy, debugProxy, warningProxy, errorProxy);
            Preloader = new Preloader(Port, infoProxy, debugProxy, warningProxy, errorProxy);
            DaLoader = new DaLoader(Port, Config, Preloader, infoProxy, debugProxy, warningProxy, errorProxy);
        }

        /// <summary>
        /// Full initialization flow: connect → handshake → read device info → prepare DA.
        /// </summary>
        public bool Init(string portName = null, bool display = true)
        {
            State = MtkState.Handshake;
            OnStatusChange("Connecting...");

            // Connect and handshake
            if (!string.IsNullOrEmpty(portName))
                Port.PortName = portName;

            if (!Preloader.Init(display, Config.ReadSocId))
            {
                State = MtkState.Error;
                OnError("Preloader init failed.");
                return false;
            }

            // Copy device info to config
            Config.HwCode = Preloader.HwCode;
            Config.HwVer = Preloader.HwVer;
            Config.IsBrom = Preloader.IsBrom;
            Config.Meid = Preloader.Meid;
            Config.SocId = Preloader.SocId;
            Config.BlVer = Preloader.BlVer;
            Config.InitHwCode(Preloader.HwCode);

            // Copy target config
            if (Preloader.TargetCfg != null)
            {
                Config.TargetConfig["sla"] = Preloader.TargetCfg.Sla;
                Config.TargetConfig["sbc"] = Preloader.TargetCfg.Sbc;
                Config.TargetConfig["daa"] = Preloader.TargetCfg.Daa;
                Config.TargetConfig["cert"] = Preloader.TargetCfg.Cert;
                OnInfo($"Security config: SLA={Preloader.TargetCfg.Sla}, SBC={Preloader.TargetCfg.Sbc}, DAA={Preloader.TargetCfg.Daa}, CERT={Preloader.TargetCfg.Cert}");
            }

            // If DAA is enabled and an auth file was configured, attempt auth now.
            if (Config.IsBrom && Preloader.TargetCfg != null && Preloader.TargetCfg.Daa)
            {
                if (!string.IsNullOrWhiteSpace(Config.Auth) && File.Exists(Config.Auth))
                {
                    try
                    {
                        byte[] authData = File.ReadAllBytes(Config.Auth);
                        if (authData.Length > 0)
                        {
                            if (Preloader.SendAuth(authData))
                            {
                                OnInfo($"Auth accepted: {Path.GetFileName(Config.Auth)}");
                            }
                            else
                            {
                                OnWarning("Auth was provided but rejected by the device.");
                            }
                        }
                        else
                        {
                            OnWarning("Auth file is empty.");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnWarning($"Auth load failed: {ex.Message}");
                    }
                }
                else
                {
                    OnWarning("DAA is enabled but no auth file is configured.");
                }
            }

            // Disable watchdog
            if (Config.SkipWdt && Config.ChipConfig != null)
            {
                var (wdtAddr, wdtVal) = Config.GetWatchdogAddr();
                if (wdtAddr != 0)
                {
                    Preloader.DisableWatchdog(wdtAddr, wdtVal);
                    OnInfo($"Watchdog disabled at 0x{wdtAddr:X}");
                }
            }

            State = MtkState.PreloaderReady;
            OnStatusChange($"Device ready: {Config.ChipConfig?.Name ?? "Unknown"}");
            OnInfo($"Device: {Config.ChipConfig?.Name} (0x{Config.HwCode:X4}), " +
                   $"DA mode: {Config.ChipConfig?.DaMode}, " +
                   $"BROM: {Config.IsBrom}");

            return true;
        }

        /// <summary>
        /// Upload DA and initialize flash protocol.
        /// </summary>
        public bool UploadDa()
        {
            if (State != MtkState.PreloaderReady)
            {
                OnError("Device not in PreloaderReady state.");
                return false;
            }

            State = MtkState.DaLoading;
            OnStatusChange("Uploading DA...");

            if (!DaLoader.UploadDa())
            {
                State = MtkState.Error;
                OnError("DA upload failed.");
                return false;
            }

            // Initialize XmlFlashExt for XML mode
            if (DaLoader.FlashMode == SakuraEDL.MediaTek.Config.DAmodes.XML && DaLoader.Xmlft != null)
            {
                DaLoader.XmlExt = new DA.XmlFlash.XmlFlashExt(this, DaLoader.Xmlft);

                // Verify extension loaded + set storage type
                if (DaLoader.DaExt)
                {
                    if (DaLoader.XmlExt.AckExtension())
                    {
                        OnInfo("DA XML Extensions verified.");
                        bool isUfs = DaLoader.DaConfigInstance?.Storage?.FlashType == "ufs";
                        DaLoader.XmlExt.CustomSetStorage(isUfs);
                    }
                    else
                    {
                        OnWarning("DA XML Extension ACK failed.");
                        DaLoader.DaExt = false;
                    }
                }
            }

            // Initialize XmlFlashExt for XFlash mode (reuses same extension system)
            if (DaLoader.FlashMode == SakuraEDL.MediaTek.Config.DAmodes.XFLASH && DaLoader.Xft != null)
            {
                // XmlFlashExt needs XmlLib but XFlash uses binary protocol
                // Create it with null xml — only used for DA patching, not custom commands
                DaLoader.XmlExt = new DA.XmlFlash.XmlFlashExt(this, null);

                if (DaLoader.DaExt)
                {
                    bool isUfs = DaLoader.DaConfigInstance?.Storage?.FlashType == "ufs";
                    OnInfo($"XFlash DA Extensions active. Storage: {(isUfs ? "UFS" : "eMMC")}");
                }
            }

            DaHandler = new DaHandler(this);
            State = MtkState.DaReady;
            OnStatusChange("DA ready.");
            return true;
        }

        /// <summary>
        /// Initialize hardware crypto engine (after DA is loaded or via exploit).
        /// </summary>
        public void InitHwCrypto()
        {
            if (Config.ChipConfig == null) return;

            var setup = CryptoSetup.FromChipConfig(
                Config.ChipConfig,
                (addr, dwords) => Preloader.Read32(addr, dwords),
                (addr, val) => Preloader.Write32(addr, val),
                (addr, data) => Preloader.WriteMem(addr, data)
            );
            setup.HwCode = Config.HwCode;
            HwCrypto = new HwCrypto(setup, OnInfo, OnError);
        }

        /// <summary>
        /// Patch preloader security (DA1) to bypass restrictions.
        /// mtkclient: patch_preloader_security_da1(data)
        /// </summary>
        public byte[] PatchPreloaderSecurity(byte[] data)
        {
            bool patched = false;
            byte[] result = (byte[])data.Clone();

            var patches = new (string pattern, string patch, string desc)[]
            {
                ("A3687BB12846", "0123A3602846", "oppo security"),
                ("A3687BB1", "0123A360", "oppo security v2"),
                ("B3F5807F01D1", "B3F5807F01E0", "ingsings"),
            };

            foreach (var (patternHex, patchHex, desc) in patches)
            {
                byte[] pattern = HexToBytes(patternHex);
                byte[] patch = HexToBytes(patchHex);

                int idx = ByteUtils.FindBytes(result, pattern);
                if (idx != -1)
                {
                    Buffer.BlockCopy(patch, 0, result, idx, patch.Length);
                    OnInfo($"Patched \"{desc}\" in preloader at 0x{idx:X}");
                    patched = true;
                }
            }

            if (!patched)
                OnWarning("No preloader security patches applied.");

            return result;
        }

        #region High-level Flash Operations

        /// <summary>
        /// Read flash data via the active DA protocol.
        /// </summary>
        public byte[] ReadFlash(ulong addr, ulong length, Action<int> progress = null)
        {
            if (State != MtkState.DaReady)
            {
                OnError("DA not ready for flash operations.");
                return null;
            }
            if (DaLoader.Xft != null)
                return DaLoader.Xft.ReadFlash(addr, length, progressCallback: progress ?? OnProgress);
            if (DaLoader.Xmlft != null)
                return DaLoader.Xmlft.ReadFlash(addr, length, progressCallback: progress ?? OnProgress);
            OnError("No flash protocol available.");
            return null;
        }

        /// <summary>
        /// Write flash data via the active DA protocol.
        /// </summary>
        public bool WriteFlash(ulong addr, byte[] data, Action<int> progress = null)
        {
            if (State != MtkState.DaReady)
            {
                OnError("DA not ready for flash operations.");
                return false;
            }
            if (DaLoader.Xft != null)
                return DaLoader.Xft.WriteFlash(addr, data, progressCallback: progress ?? OnProgress);
            if (DaLoader.Xmlft != null)
                return DaLoader.Xmlft.WriteFlash(addr, data, progressCallback: progress ?? OnProgress);
            OnError("No flash protocol available.");
            return false;
        }

        /// <summary>
        /// Format/erase flash region.
        /// </summary>
        public bool FormatFlash(ulong addr, ulong length)
        {
            if (State != MtkState.DaReady)
            {
                OnError("DA not ready for flash operations.");
                return false;
            }
            if (DaLoader.Xft != null)
                return DaLoader.Xft.FormatFlash(addr, length);
            if (DaLoader.Xmlft != null)
                return DaLoader.Xmlft.FormatFlash(addr, length);
            OnError("No flash protocol available.");
            return false;
        }

        /// <summary>
        /// Shutdown device.
        /// </summary>
        public bool Shutdown()
        {
            if (DaLoader?.Xft != null)
                return DaLoader.Xft.Shutdown();
            if (DaLoader?.Xmlft != null)
                return DaLoader.Xmlft.Shutdown();
            return false;
        }

        #endregion

        #region Utility

        private static byte[] HexToBytes(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                Port?.Dispose();
                _disposed = true;
            }
        }
    }
}
