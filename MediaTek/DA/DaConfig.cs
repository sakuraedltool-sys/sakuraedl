// mtkclient port: DA/daconfig.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Collections.Generic;
using System.IO;
using SakuraEDL.MediaTek.Config;

namespace SakuraEDL.MediaTek.DA
{
    #region DA Binary Structures

    public class EntryRegion
    {
        public uint Buf { get; set; }
        public uint Len { get; set; }
        public uint StartAddr { get; set; }
        public uint StartOffset { get; set; }
        public uint SigLen { get; set; }

        public EntryRegion(byte[] data, int offset = 0)
        {
            Buf = BitConverter.ToUInt32(data, offset);
            Len = BitConverter.ToUInt32(data, offset + 4);
            StartAddr = BitConverter.ToUInt32(data, offset + 8);
            StartOffset = BitConverter.ToUInt32(data, offset + 12);
            SigLen = BitConverter.ToUInt32(data, offset + 16);
        }

        public override string ToString()
        {
            return $"Buf:0x{Buf:X},Len:0x{Len:X},Addr:0x{StartAddr:X},Offset:0x{StartOffset:X},Sig:0x{SigLen:X}";
        }
    }

    /// <summary>
    /// DA header parsed from DA loader binary.
    /// mtkclient: class DA
    /// </summary>
    public class DaEntry
    {
        public bool V6 { get; set; }
        public string Loader { get; set; }
        public ushort Magic { get; set; }
        public ushort HwCode { get; set; }
        public ushort HwSubCode { get; set; }
        public ushort HwVersion { get; set; }
        public ushort SwVersion { get; set; }
        public ushort Reserved1 { get; set; }
        public ushort PageSize { get; set; } = 512;
        public ushort Reserved3 { get; set; }
        public ushort EntryRegionIndex { get; set; } = 1;
        public ushort EntryRegionCount { get; set; }
        public List<EntryRegion> Regions { get; set; } = new List<EntryRegion>();
        public bool OldLoader { get; set; }

        public DaEntry(byte[] data, bool oldLdr = false, bool v6 = false)
        {
            OldLoader = oldLdr;
            V6 = v6;
            int pos = 0;

            Magic = BitConverter.ToUInt16(data, pos); pos += 2;
            HwCode = BitConverter.ToUInt16(data, pos); pos += 2;
            HwSubCode = BitConverter.ToUInt16(data, pos); pos += 2;
            HwVersion = BitConverter.ToUInt16(data, pos); pos += 2;

            if (!oldLdr)
            {
                SwVersion = BitConverter.ToUInt16(data, pos); pos += 2;
                Reserved1 = BitConverter.ToUInt16(data, pos); pos += 2;
            }

            PageSize = BitConverter.ToUInt16(data, pos); pos += 2;
            Reserved3 = BitConverter.ToUInt16(data, pos); pos += 2;
            EntryRegionIndex = BitConverter.ToUInt16(data, pos); pos += 2;
            EntryRegionCount = BitConverter.ToUInt16(data, pos); pos += 2;

            for (int i = 0; i < EntryRegionCount; i++)
            {
                Regions.Add(new EntryRegion(data, pos));
                pos += 20;
            }
        }

        public override string ToString()
        {
            return $"HWCode:0x{HwCode:X4},HWSubCode:0x{HwSubCode:X4},HWVer:0x{HwVersion:X4},SWVer:0x{SwVersion:X4}";
        }
    }

    #endregion

    /// <summary>
    /// DA configuration and loader parser — ported from mtkclient/Library/DA/daconfig.py
    /// </summary>
    public class DaConfig
    {
        private readonly Action<string> _info;
        private readonly Action<string> _error;
        private readonly Action<string> _warning;

        public MtkConfig Config { get; set; }
        public StorageInfo Storage { get; set; }
        public DaEntry DaLoader { get; set; }
        public byte[] Da2 { get; set; }
        public Dictionary<ushort, List<DaEntry>> DaSetup { get; set; } = new Dictionary<ushort, List<DaEntry>>();
        public string LoaderPath { get; set; }
        public byte[] Emi { get; set; }
        public int EmiVer { get; set; }
        public int SpareSize { get; set; }
        public int ReadSize { get; set; }
        public int PageSize { get; set; } = 512;

        public DaConfig(MtkConfig config, string loader = null,
                        Action<string> info = null, Action<string> error = null, Action<string> warning = null)
        {
            Config = config;
            Storage = new StorageInfo();
            _info = info ?? delegate { };
            _error = error ?? delegate { };
            _warning = warning ?? delegate { };

            if (!string.IsNullOrEmpty(loader) && File.Exists(loader))
            {
                _info($"Using custom loader: {loader}");
                ParseDaLoader(loader);
            }
        }

        /// <summary>
        /// Parse DA loader binary file to extract DA entries for each HW code.
        /// mtkclient: parse_da_loader
        /// </summary>
        public bool ParseDaLoader(string loaderFile)
        {
            try
            {
                using (var fs = new FileStream(loaderFile, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    byte[] hdr = br.ReadBytes(0x68);
                    uint countDa = br.ReadUInt32();
                    bool v6 = System.Text.Encoding.ASCII.GetString(hdr).Contains("MTK_DA_v6");
                    bool oldLdr = false;

                    fs.Seek(0x6C + 0xD8, SeekOrigin.Begin);
                    byte[] check = br.ReadBytes(2);
                    if (check[0] == 0xDA && check[1] == 0xDA)
                        oldLdr = true;

                    int offset = oldLdr ? 0xD8 : 0xDC;

                    for (int i = 0; i < countDa; i++)
                    {
                        fs.Seek(0x6C + (i * offset), SeekOrigin.Begin);
                        byte[] daData = br.ReadBytes(offset);
                        var da = new DaEntry(daData, oldLdr, v6);
                        da.Loader = loaderFile;

                        if ((da.HwCode & 0xFF00) == 0x6500 && !loaderFile.Contains("mt6590"))
                        {
                            if (da.HwCode != 0x6580)
                                continue;
                        }

                        if (da.HwCode == 0) continue;

                        if (!DaSetup.ContainsKey(da.HwCode))
                            DaSetup[da.HwCode] = new List<DaEntry>();

                        bool found = false;
                        foreach (var ldr in DaSetup[da.HwCode])
                        {
                            if (da.HwVersion == ldr.HwVersion &&
                                da.SwVersion == ldr.SwVersion &&
                                da.HwSubCode == ldr.HwSubCode)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                            DaSetup[da.HwCode].Add(da);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _error($"Couldn't open loader: {loaderFile}. Reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Select matching DA loader for the detected chip.
        /// mtkclient: setup()
        /// </summary>
        public DaEntry Setup()
        {
            if (Config?.ChipConfig == null) return null;

            ushort dacode = Config.ChipConfig.DaCode;
            if (DaSetup.ContainsKey(dacode))
            {
                foreach (var loader in DaSetup[dacode])
                {
                    if (loader.HwVersion <= Config.HwVer || Config.HwVer == 0)
                    {
                        if (loader.SwVersion <= Config.SwVer || Config.SwVer == 0)
                        {
                            if (DaLoader == null)
                            {
                                if (loader.V6)
                                    Config.ChipConfig.DaMode = DAmodes.XML;
                                DaLoader = loader;
                                LoaderPath = loader.Loader;
                            }
                        }
                    }
                }
            }

            if (DaLoader == null && dacode != 0x6261)
                _error("No da_loader config set up");

            return DaLoader;
        }

        /// <summary>
        /// Read DA region data from loader file.
        /// </summary>
        public byte[] ReadDaRegion(DaEntry da, int regionIndex)
        {
            if (da == null || string.IsNullOrEmpty(da.Loader)) return null;
            if (regionIndex >= da.Regions.Count) return null;

            var region = da.Regions[regionIndex];
            try
            {
                using (var fs = new FileStream(da.Loader, FileMode.Open, FileAccess.Read))
                {
                    fs.Seek(region.StartOffset, SeekOrigin.Begin);
                    byte[] data = new byte[region.Len];
                    fs.Read(data, 0, data.Length);
                    return data;
                }
            }
            catch (Exception ex)
            {
                _error($"Failed to read DA region: {ex.Message}");
                return null;
            }
        }
    }
}
