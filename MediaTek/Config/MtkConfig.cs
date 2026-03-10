// mtkclient port: mtk_class.py config state + settings.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SakuraEDL.MediaTek.Config
{
    /// <summary>
    /// Runtime configuration state — ported from mtkclient config fields.
    /// Holds device info, chip config, connection state, and user options.
    /// </summary>
    public class MtkConfig
    {
        // Device identification
        public ushort HwCode { get; set; }
        public ushort HwVer { get; set; }
        public ushort HwSubCode { get; set; }
        public ushort SwVer { get; set; }
        public byte BlVer { get; set; }
        public byte BromVer { get; set; }
        public bool IsBrom { get; set; }
        public bool Iot { get; set; }
        public string Cpu { get; set; } = "";

        // Security info
        public byte[] Meid { get; set; }
        public byte[] SocId { get; set; }
        public Dictionary<string, bool> TargetConfig { get; set; } = new Dictionary<string, bool>
        {
            { "sbc", false }, { "sla", false }, { "daa", false },
            { "epp", false }, { "cert", false },
            { "memread", false }, { "memwrite", false }, { "cmdC8", false }
        };
        public uint[] PlCap { get; set; } = new uint[2];

        // Chip config (from brom_config.py database)
        public ChipConfig ChipConfig { get; set; }

        // Paths
        public string HwParamPath { get; set; } = "logs";
        public string Loader { get; set; } = "";
        public string Preloader { get; set; } = "";
        public string Auth { get; set; }
        public string Cert { get; set; }

        // User options
        public bool SkipWdt { get; set; } = true;
        public bool ReadSocId { get; set; }
        public bool GenerateKeys { get; set; }
        public bool Gui { get; set; }

        // Callbacks
        public Action<string> GuiStatusCallback { get; set; }

        public void InitHwCode(ushort hwCode)
        {
            HwCode = hwCode;
            ChipConfig = BromConfig.GetConfig(hwCode);
        }

        public void SetMeid(byte[] meid)
        {
            Meid = meid;
        }

        public void SetSocId(byte[] socId)
        {
            SocId = socId;
        }

        public void SetGuiStatus(string status)
        {
            GuiStatusCallback?.Invoke(status);
        }

        public (uint addr, uint value) GetWatchdogAddr()
        {
            return BromConfig.GetWatchdogAddr(ChipConfig ?? new ChipConfig());
        }
    }

    /// <summary>
    /// Hardware parameter persistence — ported from settings.py HwParam.
    /// Stores device-specific parameters in JSON.
    /// </summary>
    public class HwParam
    {
        private Dictionary<string, string> _settings = new Dictionary<string, string>();
        private readonly string _paramFile = "hwparam.json";
        private readonly string _path;

        public HwParam(string path, string meid = null)
        {
            _path = path;
            if (!string.IsNullOrEmpty(meid))
            {
                string filePath = Path.Combine(path, _paramFile);
                if (File.Exists(filePath))
                {
                    try
                    {
                        string json = File.ReadAllText(filePath);
                        // Simple JSON parsing (no dependency)
                        _settings = SimpleJsonParse(json);
                        if (_settings.ContainsKey("meid") && _settings["meid"] != meid)
                            _settings = new Dictionary<string, string> { { "meid", meid } };
                    }
                    catch
                    {
                        _settings = new Dictionary<string, string>();
                    }
                }
                else
                {
                    _settings["meid"] = meid;
                }
            }
        }

        public string LoadSetting(string key)
        {
            return _settings.ContainsKey(key) ? _settings[key] : null;
        }

        public void WriteSetting(string key, string value)
        {
            _settings[key] = value;
            WriteJson();
        }

        private void WriteJson()
        {
            try
            {
                if (!Directory.Exists(_path))
                    Directory.CreateDirectory(_path);
                var sb = new StringBuilder();
                sb.Append("{");
                bool first = true;
                foreach (var kv in _settings)
                {
                    if (!first) sb.Append(",");
                    sb.Append($"\"{kv.Key}\":\"{kv.Value}\"");
                    first = false;
                }
                sb.Append("}");
                File.WriteAllText(Path.Combine(_path, _paramFile), sb.ToString());
            }
            catch { }
        }

        private static Dictionary<string, string> SimpleJsonParse(string json)
        {
            var result = new Dictionary<string, string>();
            json = json.Trim().TrimStart('{').TrimEnd('}');
            string[] pairs = json.Split(',');
            foreach (string pair in pairs)
            {
                int colonIdx = pair.IndexOf(':');
                if (colonIdx > 0)
                {
                    string key = pair.Substring(0, colonIdx).Trim().Trim('"');
                    string val = pair.Substring(colonIdx + 1).Trim().Trim('"');
                    result[key] = val;
                }
            }
            return result;
        }
    }
}
