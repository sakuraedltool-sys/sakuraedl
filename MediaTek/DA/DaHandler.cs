// mtkclient port: DA/mtk_da_handler.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SakuraEDL.MediaTek.Config;
using SakuraEDL.MediaTek.Connection;
using SakuraEDL.MediaTek.DA.XFlash;
using SakuraEDL.MediaTek.DA.XmlFlash;
using SakuraEDL.MediaTek.Protocol;
using SakuraEDL.MediaTek.Utility;

namespace SakuraEDL.MediaTek.DA
{
    /// <summary>
    /// High-level DA flash operations — ported from mtkclient/Library/DA/mtk_da_handler.py
    /// Provides partition-level read/write/erase, GPT parsing, IMEI, NVRAM, etc.
    /// </summary>
    public class DaHandler
    {
        private readonly MtkClass _mtk;
        private readonly Action<string> _info;
        private readonly Action<string> _error;
        private readonly Action<string> _warning;

        public DaHandler(MtkClass mtk)
        {
            _mtk = mtk;
            _info = mtk.OnInfo ?? delegate { };
            _error = mtk.OnError ?? delegate { };
            _warning = mtk.OnWarning ?? delegate { };
        }

        #region Partition Table (GPT)

        /// <summary>
        /// Read GPT partition table from device.
        /// mtkclient: da_gpt(directory, display)
        /// </summary>
        public List<PartitionEntry> ReadGpt()
        {
            var partitions = new List<PartitionEntry>();

            if (_mtk.DaLoader?.Xft != null)
            {
                // XFlash mode: read GPT from LBA 0
                byte[] gptData = _mtk.DaLoader.Xft.ReadFlash(0, 0x22 * 512);
                if (gptData != null)
                    partitions = ParseGpt(gptData);
            }
            else if (_mtk.DaLoader?.FlashMode == DAmodes.XML && _mtk.DaLoader?.Xmlft != null)
            {
                // XML mode: use partition table command
                byte[] ptData = _mtk.DaLoader.Xmlft.ReadPartitionTable();
                if (ptData != null)
                {
                    string xmlResp = System.Text.Encoding.UTF8.GetString(ptData);
                    partitions = ParseXmlPartitionTable(xmlResp);
                }
            }

            _info($"Found {partitions.Count} partitions");
            return partitions;
        }

        /// <summary>
        /// Parse GPT from raw sector data.
        /// </summary>
        private List<PartitionEntry> ParseGpt(byte[] data)
        {
            var partitions = new List<PartitionEntry>();
            if (data == null || data.Length < 0x200) return partitions;

            // Check protective MBR signature
            if (data[0x1FE] != 0x55 || data[0x1FF] != 0xAA)
            {
                _warning("No valid MBR signature found");
                return partitions;
            }

            // GPT header at LBA 1
            int hdrOffset = 0x200;
            if (data.Length < hdrOffset + 0x5C) return partitions;

            // Check "EFI PART" signature
            string sig = Encoding.ASCII.GetString(data, hdrOffset, 8);
            if (sig != "EFI PART")
            {
                _warning("No valid GPT header found");
                return partitions;
            }

            uint partEntryStart = BitConverter.ToUInt32(data, hdrOffset + 0x48) > 0
                ? (uint)(BitConverter.ToUInt64(data, hdrOffset + 0x48) * 512)
                : 0x400; // default LBA 2
            uint numEntries = BitConverter.ToUInt32(data, hdrOffset + 0x50);
            uint entrySize = BitConverter.ToUInt32(data, hdrOffset + 0x54);

            if (entrySize == 0) entrySize = 128;
            if (numEntries > 128) numEntries = 128;

            for (uint i = 0; i < numEntries; i++)
            {
                int offset = (int)(partEntryStart + i * entrySize);
                if (offset + entrySize > data.Length) break;

                // Check if entry is empty (type GUID all zeros)
                bool empty = true;
                for (int j = 0; j < 16; j++)
                {
                    if (data[offset + j] != 0) { empty = false; break; }
                }
                if (empty) continue;

                ulong firstLba = BitConverter.ToUInt64(data, offset + 32);
                ulong lastLba = BitConverter.ToUInt64(data, offset + 40);

                // Partition name: UTF-16LE at offset 56, up to 72 bytes (36 chars)
                string name = Encoding.Unicode.GetString(data, offset + 56, 72).TrimEnd('\0');

                partitions.Add(new PartitionEntry
                {
                    Name = name,
                    StartLba = firstLba,
                    EndLba = lastLba,
                    SizeLba = lastLba - firstLba + 1,
                    SizeBytes = (lastLba - firstLba + 1) * 512
                });
            }

            return partitions;
        }

        /// <summary>
        /// Parse XML partition table response into partition entries.
        /// XML format: <pt><pt name="..." start="..." size="..." />...</pt>
        /// </summary>
        private List<PartitionEntry> ParseXmlPartitionTable(string xml)
        {
            var partitions = new List<PartitionEntry>();
            if (string.IsNullOrEmpty(xml)) return partitions;

            // Simple XML attribute parser — avoids dependency on System.Xml.Linq
            int pos = 0;
            while (pos < xml.Length)
            {
                // Find <pt or <partition tags with attributes
                int tagStart = xml.IndexOf("<pt ", pos, StringComparison.OrdinalIgnoreCase);
                if (tagStart < 0)
                    tagStart = xml.IndexOf("<partition ", pos, StringComparison.OrdinalIgnoreCase);
                if (tagStart < 0) break;

                int tagEnd = xml.IndexOf(">", tagStart);
                if (tagEnd < 0) break;

                string tag = xml.Substring(tagStart, tagEnd - tagStart + 1);
                pos = tagEnd + 1;

                string name = GetAttr(tag, "name");
                if (string.IsNullOrEmpty(name))
                    name = GetAttr(tag, "partition_name");
                if (string.IsNullOrEmpty(name)) continue;

                string startStr = GetAttr(tag, "start") ?? GetAttr(tag, "start_addr") ?? "0";
                string sizeStr = GetAttr(tag, "size") ?? GetAttr(tag, "partition_size") ?? "0";

                ulong start = ParseHexOrDec(startStr);
                ulong size = ParseHexOrDec(sizeStr);

                partitions.Add(new PartitionEntry
                {
                    Name = name,
                    StartLba = start / 512,
                    EndLba = size > 0 ? (start + size) / 512 - 1 : 0,
                    SizeLba = size / 512,
                    SizeBytes = size
                });
            }

            return partitions;
        }

        private static string GetAttr(string tag, string attrName)
        {
            string search = attrName + "=\"";
            int idx = tag.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx += search.Length;
            int end = tag.IndexOf('"', idx);
            if (end < 0) return null;
            return tag.Substring(idx, end - idx);
        }

        private static ulong ParseHexOrDec(string val)
        {
            if (string.IsNullOrEmpty(val)) return 0;
            val = val.Trim();
            if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                ulong.TryParse(val.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out ulong r);
                return r;
            }
            ulong.TryParse(val, out ulong d);
            return d;
        }

        /// <summary>
        /// Save GPT to directory.
        /// </summary>
        public bool SaveGpt(string directory)
        {
            var partitions = ReadGpt();
            if (partitions.Count == 0) return false;

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string gptFile = Path.Combine(directory, "gpt.txt");
            using (var sw = new StreamWriter(gptFile))
            {
                foreach (var p in partitions)
                {
                    sw.WriteLine($"{p.Name}\t0x{p.StartLba * 512:X}\t0x{p.SizeBytes:X}");
                }
            }

            _info($"GPT saved to {gptFile}");
            return true;
        }

        #endregion

        #region Partition Read/Write/Erase

        /// <summary>
        /// Read a named partition to file.
        /// mtkclient: da_read(partitionname, parttype, filename, ...)
        /// </summary>
        public bool ReadPartition(string name, string outputPath, Action<int> progress = null)
        {
            var partitions = ReadGpt();
            var part = partitions.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (part == null)
            {
                _error($"Partition '{name}' not found");
                return false;
            }

            ulong addr = part.StartLba * 512;
            ulong length = part.SizeBytes;

            _info($"Reading partition '{name}': 0x{addr:X} size 0x{length:X}");
            byte[] data = _mtk.ReadFlash(addr, length, progress);
            if (data == null)
            {
                _error($"Failed to read partition '{name}'");
                return false;
            }

            File.WriteAllBytes(outputPath, data);
            _info($"Partition '{name}' saved to {outputPath}");
            return true;
        }

        /// <summary>
        /// Write image to a named partition.
        /// mtkclient: da_write(parttype, filenames, partitions)
        /// </summary>
        public bool WritePartition(string name, string imagePath, Action<int> progress = null)
        {
            var partitions = ReadGpt();
            var part = partitions.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (part == null)
            {
                _error($"Partition '{name}' not found");
                return false;
            }

            if (!File.Exists(imagePath))
            {
                _error($"Image file not found: {imagePath}");
                return false;
            }

            byte[] data = File.ReadAllBytes(imagePath);
            ulong addr = part.StartLba * 512;

            if ((ulong)data.Length > part.SizeBytes)
            {
                _error($"Image size (0x{data.Length:X}) exceeds partition size (0x{part.SizeBytes:X})");
                return false;
            }

            _info($"Writing partition '{name}': 0x{addr:X} size 0x{data.Length:X}");
            return _mtk.WriteFlash(addr, data, progress);
        }

        /// <summary>
        /// Erase a named partition.
        /// mtkclient: da_erase(partitions, parttype)
        /// </summary>
        public bool ErasePartition(string name)
        {
            var partitions = ReadGpt();
            var part = partitions.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (part == null)
            {
                _error($"Partition '{name}' not found");
                return false;
            }

            ulong addr = part.StartLba * 512;
            _info($"Erasing partition '{name}': 0x{addr:X} size 0x{part.SizeBytes:X}");
            return _mtk.FormatFlash(addr, part.SizeBytes);
        }

        /// <summary>
        /// Read all partitions to a directory.
        /// mtkclient: da_rl(directory, parttype, skip, display)
        /// </summary>
        public int ReadAllPartitions(string directory, Action<int> progress = null, List<string> skip = null)
        {
            var partitions = ReadGpt();
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            int success = 0;
            foreach (var p in partitions)
            {
                if (skip != null && skip.Contains(p.Name.ToLower()))
                {
                    _info($"Skipping partition '{p.Name}'");
                    continue;
                }

                string outputPath = Path.Combine(directory, $"{p.Name}.img");
                _info($"Reading '{p.Name}' ({p.SizeBytes / 1024}KB)...");

                ulong addr = p.StartLba * 512;
                byte[] data = _mtk.ReadFlash(addr, p.SizeBytes, progress);
                if (data != null)
                {
                    File.WriteAllBytes(outputPath, data);
                    success++;
                }
                else
                {
                    _warning($"Failed to read partition '{p.Name}'");
                }
            }

            _info($"Read {success}/{partitions.Count} partitions to {directory}");
            return success;
        }

        #endregion

        #region Preloader Dump

        /// <summary>
        /// Dump preloader from RAM.
        /// mtkclient: dump_preloader_ram(write_preloader_to_file)
        /// </summary>
        public byte[] DumpPreloaderRam()
        {
            try
            {
                // Read preloader region at 0x200000
                uint baseAddr = 0x200000;
                int dwords = 0x10000 / 4;
                byte[] data = new byte[0x10000];

                for (int i = 0; i < dwords; i++)
                {
                    uint val = _mtk.Preloader.Read32(baseAddr + (uint)(i * 4), 1);
                    data[i * 4] = (byte)(val & 0xFF);
                    data[i * 4 + 1] = (byte)((val >> 8) & 0xFF);
                    data[i * 4 + 2] = (byte)((val >> 16) & 0xFF);
                    data[i * 4 + 3] = (byte)((val >> 24) & 0xFF);
                }

                // Find preloader header: "MMM\x01\x38\x00\x00\x00"
                byte[] marker = new byte[] { 0x4D, 0x4D, 0x4D, 0x01, 0x38, 0x00, 0x00, 0x00 };
                int idx = ByteUtils.FindBytes(data, marker);
                if (idx == -1)
                {
                    _warning("Preloader header not found in RAM");
                    return null;
                }

                uint length = BitConverter.ToUInt32(data, idx + 0x20);
                _info($"Preloader found at offset 0x{idx:X}, length 0x{length:X}");

                // Read full preloader
                byte[] preloader = new byte[length];
                int readPos = 0;
                uint readAddr = baseAddr + (uint)idx;
                while (readPos < length)
                {
                    int chunkDwords = Math.Min(128, ((int)length - readPos) / 4 + 1);
                    for (int i = 0; i < chunkDwords && readPos < length; i++)
                    {
                        uint val = _mtk.Preloader.Read32(readAddr, 1);
                        int remaining = (int)length - readPos;
                        int copyLen = Math.Min(4, remaining);
                        byte[] valBytes = BitConverter.GetBytes(val);
                        Buffer.BlockCopy(valBytes, 0, preloader, readPos, copyLen);
                        readPos += 4;
                        readAddr += 4;
                    }
                }

                return preloader;
            }
            catch (Exception ex)
            {
                _error($"Preloader dump failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region VBMeta

        /// <summary>
        /// Patch vbmeta to disable verification.
        /// mtkclient: patch_vbmeta(vbmeta, vbmode)
        /// </summary>
        public byte[] PatchVbMeta(byte[] vbmeta, int vbMode = 3)
        {
            if (vbmeta == null || vbmeta.Length < 256) return vbmeta;

            // Check "AVB0" magic
            if (vbmeta[0] != 0x41 || vbmeta[1] != 0x56 || vbmeta[2] != 0x42 || vbmeta[3] != 0x30)
            {
                _warning("Not a valid vbmeta image");
                return vbmeta;
            }

            byte[] patched = (byte[])vbmeta.Clone();
            // Set flags at offset 0x7B to disable verification
            patched[0x7B] = (byte)vbMode;
            _info($"VBMeta patched with mode={vbMode}");
            return patched;
        }

        /// <summary>
        /// Read, patch, and write vbmeta partition.
        /// mtkclient: da_vbmeta(vbmode, display)
        /// </summary>
        public bool PatchVbMetaPartition(int vbMode = 3)
        {
            var partitions = ReadGpt();

            string[] vbmetaNames = { "vbmeta", "vbmeta_a", "vbmeta_b",
                                     "vbmeta_system", "vbmeta_system_a", "vbmeta_system_b",
                                     "vbmeta_vendor", "vbmeta_vendor_a", "vbmeta_vendor_b" };

            bool anyPatched = false;
            foreach (var name in vbmetaNames)
            {
                var part = partitions.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (part == null) continue;

                ulong addr = part.StartLba * 512;
                byte[] data = _mtk.ReadFlash(addr, part.SizeBytes);
                if (data == null) continue;

                byte[] patched = PatchVbMeta(data, vbMode);
                if (patched != null && _mtk.WriteFlash(addr, patched))
                {
                    _info($"Patched vbmeta partition: {name}");
                    anyPatched = true;
                }
            }

            return anyPatched;
        }

        #endregion

        #region BROM Peek/Poke

        /// <summary>
        /// Read memory via BROM.
        /// mtkclient: da_peek(addr, length, filename, registers)
        /// </summary>
        public byte[] Peek(uint addr, int length)
        {
            _info($"Peek: 0x{addr:X} len=0x{length:X}");
            byte[] result = new byte[length];

            int pos = 0;
            while (pos < length)
            {
                int chunkDwords = Math.Min(64, (length - pos + 3) / 4);
                for (int i = 0; i < chunkDwords && pos < length; i++)
                {
                    uint val = _mtk.Preloader.Read32((uint)(addr + pos), 1);
                    byte[] valBytes = BitConverter.GetBytes(val);
                    int copyLen = Math.Min(4, length - pos);
                    Buffer.BlockCopy(valBytes, 0, result, pos, copyLen);
                    pos += 4;
                }
            }
            return result;
        }

        /// <summary>
        /// Write memory via BROM.
        /// mtkclient: da_poke(addr, data, filename)
        /// </summary>
        public bool Poke(uint addr, byte[] data)
        {
            _info($"Poke: 0x{addr:X} len=0x{data.Length:X}");
            int pos = 0;
            while (pos < data.Length)
            {
                int remaining = data.Length - pos;
                if (remaining >= 4)
                {
                    uint val = BitConverter.ToUInt32(data, pos);
                    _mtk.Preloader.Write32((uint)(addr + pos), val);
                    pos += 4;
                }
                else
                {
                    // Partial write: read-modify-write
                    uint existing = _mtk.Preloader.Read32((uint)(addr + pos), 1);
                    byte[] existBytes = BitConverter.GetBytes(existing);
                    Buffer.BlockCopy(data, pos, existBytes, 0, remaining);
                    uint newVal = BitConverter.ToUInt32(existBytes, 0);
                    _mtk.Preloader.Write32((uint)(addr + pos), newVal);
                    pos += remaining;
                }
            }
            return true;
        }

        #endregion

    }

    /// <summary>
    /// GPT partition entry.
    /// </summary>
    public class PartitionEntry
    {
        public string Name { get; set; }
        public ulong StartLba { get; set; }
        public ulong EndLba { get; set; }
        public ulong SizeLba { get; set; }
        public ulong SizeBytes { get; set; }

        public override string ToString() => $"{Name}: LBA {StartLba}-{EndLba} ({SizeBytes / 1024}KB)";
    }
}
