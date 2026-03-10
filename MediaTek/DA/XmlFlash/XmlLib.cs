// mtkclient port: DA/xmlflash/xml_lib.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Collections.Generic;
using System.Text;
using SakuraEDL.MediaTek.Connection;
using SakuraEDL.MediaTek.Config;
using SakuraEDL.MediaTek.Protocol;
using SakuraEDL.MediaTek.Utility;

namespace SakuraEDL.MediaTek.DA.XmlFlash
{
    #region Protocol Result Types

    /// <summary>
    /// Device requests host to download a file (host → device).
    /// mtkclient: DwnFile
    /// </summary>
    public class DwnFile
    {
        public string Checksum;
        public string Info;
        public string SourceFile;
        public int PacketLength;

        public DwnFile(string checksum, string info, string sourceFile, int packetLength)
        {
            Checksum = checksum;
            Info = info;
            SourceFile = sourceFile;
            PacketLength = packetLength;
        }
    }

    /// <summary>
    /// Device requests host to upload a file (device → host).
    /// mtkclient: UpFile
    /// </summary>
    public class UpFile
    {
        public string Checksum;
        public string Info;
        public string TargetFile;
        public int PacketLength;

        public UpFile(string checksum, string info, string targetFile, int packetLength)
        {
            Checksum = checksum;
            Info = info;
            TargetFile = targetFile;
            PacketLength = packetLength;
        }
    }

    /// <summary>
    /// Device requests a file system operation.
    /// mtkclient: FileSysOp
    /// </summary>
    public class FileSysOp
    {
        public string Key;
        public string FilePath;

        public FileSysOp(string key, string filePath)
        {
            Key = key;
            FilePath = filePath;
        }
    }

    /// <summary>
    /// Result from GetCommandResult — wraps the parsed command and its value.
    /// </summary>
    public class CmdResult
    {
        public string Command;
        public object Value; // string, DwnFile, UpFile, FileSysOp, or byte[]

        public CmdResult(string cmd, object val) { Command = cmd; Value = val; }

        public bool IsDwnFile => Value is DwnFile;
        public bool IsUpFile => Value is UpFile;
        public bool IsFileSysOp => Value is FileSysOp;
        public DwnFile AsDwnFile => Value as DwnFile;
        public UpFile AsUpFile => Value as UpFile;
        public FileSysOp AsFileSysOp => Value as FileSysOp;
        public string AsString => Value as string ?? "";
    }

    #endregion

    /// <summary>
    /// XML V6 DA protocol — ported from mtkclient/Library/DA/xmlflash/xml_lib.py
    /// Uses XML-based command/response over the XFlash binary transport.
    /// </summary>
    public class XmlLib
    {
        private readonly Port _port;
        private readonly MtkConfig _config;
        private readonly DaConfig _daConfig;
        private readonly ErrorHandler _eh;
        private readonly Action<string> _info;
        private readonly Action<string> _debug;
        private readonly Action<string> _warning;
        private readonly Action<string> _error;

        private const uint MAGIC = 0xFEEEEEEF;

        public string DaVersion { get; set; }
        public bool DaExt { get; set; }

        public XmlLib(Port port, MtkConfig config, DaConfig daConfig,
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

        #region Transport

        /// <summary>
        /// Read XML response from device.
        /// mtkclient: xread()
        /// </summary>
        public byte[] XRead()
        {
            try
            {
                byte[] hdr = _port.UsbRead(12);
                if (hdr == null || hdr.Length < 12) return null;
                uint magic = BitConverter.ToUInt32(hdr, 0);
                uint dataType = BitConverter.ToUInt32(hdr, 4);
                uint length = BitConverter.ToUInt32(hdr, 8);
                if (magic != MAGIC)
                {
                    _error("XML xread: Wrong magic");
                    return null;
                }
                if (length == 0) return new byte[0];
                // Read payload, may arrive in multiple USB packets
                byte[] result = new byte[length];
                int bytesRead = 0;
                while (bytesRead < (int)length)
                {
                    byte[] tmp = _port.UsbRead((int)length - bytesRead);
                    if (tmp == null || tmp.Length == 0) break;
                    Buffer.BlockCopy(tmp, 0, result, bytesRead, tmp.Length);
                    bytesRead += tmp.Length;
                }
                return result;
            }
            catch (Exception ex)
            {
                _error($"XML xread error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Send data with XFlash header.
        /// mtkclient: xsend(data, datatype, is64bit)
        /// </summary>
        public bool XSend(byte[] data, uint dataType = 1)
        {
            byte[] hdr = new byte[12];
            WriteLE32(hdr, 0, MAGIC);
            WriteLE32(hdr, 4, dataType);
            WriteLE32(hdr, 8, (uint)data.Length);
            if (_port.UsbWrite(hdr))
                return _port.UsbWrite(data);
            return false;
        }

        /// <summary>
        /// Send string with null terminator (matches Python xsend for str type).
        /// </summary>
        public bool XSendStr(string s, uint dataType = 1)
        {
            byte[] data = Encoding.UTF8.GetBytes(s + "\0");
            return XSend(data, dataType);
        }

        /// <summary>
        /// Send ACK — "OK\0" with null terminator.
        /// mtkclient: ack()
        /// </summary>
        public void Ack()
        {
            XSendStr("OK");
        }

        /// <summary>
        /// Send ACK with hex length value.
        /// mtkclient: ack_value(length)
        /// </summary>
        public void AckValue(int length)
        {
            XSendStr($"OK@0x{length:X}");
        }

        /// <summary>
        /// Send ACK with text.
        /// mtkclient: ack_text(text)
        /// </summary>
        public void AckText(string text)
        {
            XSendStr($"OK@{text}");
        }

        #endregion

        #region Response Parsing

        /// <summary>
        /// Read text response from device.
        /// mtkclient: get_response(raw)
        /// </summary>
        public string GetResponse(bool raw = false)
        {
            byte[] data = XRead();
            if (data == null) return null;
            if (data.Length == 0) return "";

            if (raw) return Encoding.UTF8.GetString(data);

            // Strip trailing null bytes
            string response = Encoding.UTF8.GetString(data).TrimEnd('\0');
            return response;
        }

        /// <summary>
        /// Read raw binary response data.
        /// mtkclient: get_response_data()
        /// </summary>
        public byte[] GetResponseData()
        {
            return XRead();
        }

        /// <summary>
        /// Read and parse the next device command/result XML.
        /// mtkclient: get_command_result()
        /// Protocol: device sends XML with &lt;command&gt; tag indicating what it wants.
        /// </summary>
        public CmdResult GetCommandResult()
        {
            string data = GetResponse();
            if (data == null) return new CmdResult("", "ERROR");

            string cmd = GetField(data, "command") ?? "";

            // Handle OK@0xLEN inline data transfer
            if (cmd == "" && data.Contains("OK@"))
            {
                string tmp = data.Split('@')[1];
                int len = Convert.ToInt32(tmp.Substring(2), 16);
                Ack();
                string sresp = GetResponse();
                if (sresp != null && sresp.Contains("OK"))
                {
                    Ack();
                    byte[] inlineData = new byte[len];
                    int bytesRead = 0;
                    while (bytesRead < len)
                    {
                        byte[] chunk = GetResponseData();
                        if (chunk == null || chunk.Length == 0) break;
                        Buffer.BlockCopy(chunk, 0, inlineData, bytesRead, Math.Min(chunk.Length, len - bytesRead));
                        bytesRead += chunk.Length;
                    }
                    Ack();
                    return new CmdResult(cmd, inlineData);
                }
            }

            // CMD:PROGRESS-REPORT — wait until OK!EOT
            if (cmd == "CMD:PROGRESS-REPORT")
            {
                Ack();
                string pr = "";
                while (pr != "OK!EOT")
                {
                    pr = GetResponse();
                    Ack();
                    if (pr == null) break;
                }
                // Re-read next command
                data = GetResponse();
                if (data == null) return new CmdResult("", "ERROR");
                cmd = GetField(data, "command") ?? "";
            }

            // CMD:START
            if (cmd == "CMD:START")
            {
                Ack();
                return new CmdResult(cmd, "START");
            }

            // CMD:DOWNLOAD-FILE — device wants host to send data
            if (cmd == "CMD:DOWNLOAD-FILE")
            {
                string checksum = GetField(data, "checksum") ?? "CHK_NO";
                string info = GetField(data, "info") ?? "";
                string sourceFile = GetField(data, "source_file") ?? "";
                string pktLenStr = GetField(data, "packet_length") ?? "0x1000";
                int packetLen = ParseHexOrDec(pktLenStr);
                Ack();
                return new CmdResult(cmd, new DwnFile(checksum, info, sourceFile, packetLen));
            }

            // CMD:UPLOAD-FILE — device wants to send data to host
            if (cmd == "CMD:UPLOAD-FILE")
            {
                string checksum = GetField(data, "checksum") ?? "CHK_NO";
                string info = GetField(data, "info") ?? "";
                string targetFile = GetField(data, "target_file") ?? "";
                string pktLenStr = GetField(data, "packet_length") ?? "0x1000";
                int packetLen = ParseHexOrDec(pktLenStr);
                Ack();
                return new CmdResult(cmd, new UpFile(checksum, info, targetFile, packetLen));
            }

            // CMD:FILE-SYS-OPERATION
            if (cmd == "CMD:FILE-SYS-OPERATION")
            {
                string key = GetField(data, "key") ?? "";
                string filePath = GetField(data, "file_path") ?? "";
                Ack();
                return new CmdResult(cmd, new FileSysOp(key, filePath));
            }

            // CMD:END — command finished
            if (cmd == "CMD:END")
            {
                string result = GetField(data, "result") ?? "";
                if (data.Contains("message") && result != "OK")
                {
                    string message = GetField(data, "message") ?? result;
                    return new CmdResult(cmd, message);
                }
                return new CmdResult(cmd, result);
            }

            return new CmdResult(cmd, GetField(data, "result") ?? data);
        }

        #endregion

        #region Command Protocol

        /// <summary>
        /// Send XML command and handle full protocol cycle.
        /// mtkclient: send_command(xmldata, noack)
        ///
        /// Full cycle: send → get "OK" → get CMD:END → ack → get CMD:START.
        /// noack=true: send → get "OK" → return (caller handles rest).
        /// Returns CmdResult for commands that return DwnFile/UpFile,
        /// or true/false for simple commands.
        /// </summary>
        public object SendCommandEx(string xmlData, bool noAck = false)
        {
            if (!XSendStr(xmlData))
            {
                _error("XML send_command: write failed");
                return null;
            }

            string result = GetResponse();
            if (result == null) return null;

            if (result == "OK")
            {
                if (noAck) return true;

                // Full cycle: read CMD:END (or DwnFile/UpFile)
                var cmdResult = GetCommandResult();
                if (cmdResult.Command == "CMD:END")
                {
                    Ack();
                    string endValue = cmdResult.AsString;
                    if (endValue == "2nd DA address is invalid. reset.\r\n")
                    {
                        _error(endValue);
                        return null;
                    }
                    // Read CMD:START
                    var startResult = GetCommandResult();
                    if (startResult.Command == "CMD:START")
                    {
                        return endValue == "OK" ? (object)true : (object)false;
                    }
                    return endValue == "OK";
                }
                else
                {
                    // Returned DwnFile, UpFile, FileSysOp, etc.
                    return cmdResult;
                }
            }
            else if (result == "ERR!UNSUPPORTED")
            {
                var sr = GetCommandResult();
                Ack();
                var tr = GetCommandResult();
                return false;
            }
            else if (result.Contains("ERR!"))
            {
                _error($"XML command error: {result}");
                return result;
            }

            return result;
        }

        /// <summary>
        /// Simplified SendCommand — returns string for backward compat.
        /// For simple commands, returns "OK" on success, null on failure.
        /// </summary>
        public string SendCommand(string xmlData, bool noAck = false)
        {
            var result = SendCommandEx(xmlData, noAck);
            if (result == null) return null;
            if (result is bool b) return b ? "OK" : null;
            if (result is string s) return s;
            if (result is CmdResult cr)
            {
                // Caller didn't expect DwnFile/UpFile — this is a protocol mismatch
                _warning($"SendCommand got unexpected CmdResult: {cr.Command}");
                return "OK";
            }
            return result.ToString();
        }

        #endregion

        #region Data Transfer

        /// <summary>
        /// Upload data to device (host → device) using DwnFile protocol.
        /// mtkclient: upload(result, data, display, raw)
        /// </summary>
        public bool Upload(DwnFile dwn, byte[] data, bool raw = false)
        {
            if (dwn == null) return false;

            string sourceFile = dwn.SourceFile ?? "";
            int packetLength = dwn.PacketLength > 0 ? dwn.PacketLength : 0x1000;

            // Determine total length
            int length;
            if (sourceFile.Contains(":"))
            {
                string[] parts = sourceFile.Split(':');
                if (parts.Length >= 3)
                    length = ParseHexOrDec(parts[2]);
                else
                    length = data.Length;
            }
            else
            {
                length = data.Length;
            }

            AckValue(length);
            string resp = GetResponse();

            int pos = 0;
            int bytesToWrite = length;
            if (resp != null && resp.Contains("OK"))
            {
                while (bytesToWrite > 0)
                {
                    AckValue(0);
                    resp = GetResponse();
                    if (resp == null || !resp.Contains("OK"))
                    {
                        string rmsg = resp != null ? GetField(resp, "message") : "unknown";
                        _error($"Error on upload ACK at pos 0x{pos:X}: {rmsg}");
                        return false;
                    }

                    int chunkSize = Math.Min(packetLength, data.Length - pos);
                    if (chunkSize <= 0) break;
                    byte[] chunk = new byte[chunkSize];
                    Buffer.BlockCopy(data, pos, chunk, 0, chunkSize);
                    XSend(chunk);
                    resp = GetResponse();
                    if (resp == null || !resp.Contains("OK"))
                    {
                        _error($"Error on upload data at pos 0x{pos:X}");
                        return false;
                    }

                    pos += chunkSize;
                    bytesToWrite -= packetLength;
                }
            }
            return true;
        }

        /// <summary>
        /// Download data from device (device → host) using UpFile protocol.
        /// mtkclient: download(result)
        /// </summary>
        public byte[] Download(UpFile upf)
        {
            if (upf == null) return null;

            string resp = GetResponse();
            if (resp != null && resp.Contains("OK@"))
            {
                string tmp = resp.Split('@')[1];
                int length = ParseHexOrDec(tmp);
                Ack();
                string sresp = GetResponse();
                if (sresp != null && sresp.Contains("OK"))
                {
                    Ack();
                    byte[] data = new byte[length];
                    int bytesRead = 0;
                    while (bytesRead < length)
                    {
                        byte[] chunk = GetResponseData();
                        if (chunk == null || chunk.Length == 0) break;
                        int copyLen = Math.Min(chunk.Length, length - bytesRead);
                        Buffer.BlockCopy(chunk, 0, data, bytesRead, copyLen);
                        bytesRead += chunk.Length;
                    }
                    Ack();
                    return data;
                }
            }
            _error("Error on downloading data: " + (resp ?? "null"));
            return null;
        }

        /// <summary>
        /// Download raw data from device (device → host) for flash reads.
        /// mtkclient: download_raw(result, filename, display)
        /// </summary>
        public byte[] DownloadRaw(UpFile upf, Action<int> progressCallback = null)
        {
            if (upf == null) return null;

            string resp = GetResponse();
            if (resp != null && resp.Contains("OK@"))
            {
                string tmp = resp.Split('@')[1];
                int length = ParseHexOrDec(tmp);
                Ack();

                byte[] data = new byte[length];
                int bytesRead = 0;
                while (bytesRead < length)
                {
                    byte[] chunk = GetResponseData();
                    if (chunk == null || chunk.Length == 0) break;
                    int copyLen = Math.Min(chunk.Length, length - bytesRead);
                    Buffer.BlockCopy(chunk, 0, data, bytesRead, copyLen);
                    bytesRead += chunk.Length;
                    progressCallback?.Invoke(length > 0 ? (bytesRead * 100 / length) : 100);
                    Ack();
                }
                return data;
            }
            _error("Error on downloading raw data: " + (resp ?? "null"));
            return null;
        }

        #endregion

        #region Device Info

        /// <summary>
        /// Get device info via XML protocol.
        /// mtkclient: get_dev_info()
        /// </summary>
        public Dictionary<string, byte[]> GetDevInfo()
        {
            var content = new Dictionary<string, byte[]>();
            string cmd = MakeCmd("GET-DEV-INFO");
            var result = SendCommandEx(cmd, noAck: true);
            if (result == null || result is bool b && !b) return content;

            var cr = GetCommandResult();
            if (!cr.IsUpFile) return content;

            byte[] data = Download(cr.AsUpFile);
            // CMD:END
            var endR = GetCommandResult();
            Ack();
            if (endR.AsString == "OK")
            {
                var startR = GetCommandResult();
                if (startR.Command == "CMD:START")
                {
                    if (data != null)
                    {
                        string xml = Encoding.UTF8.GetString(data);
                        string rnd = GetField(xml, "rnd");
                        if (!string.IsNullOrEmpty(rnd))
                            content["rnd"] = HexToBytes(rnd);
                        string hrid = GetField(xml, "hrid");
                        if (!string.IsNullOrEmpty(hrid))
                            content["hrid"] = HexToBytes(hrid);
                        string socid = GetField(xml, "socid");
                        if (!string.IsNullOrEmpty(socid))
                            content["socid"] = HexToBytes(socid);
                    }
                }
            }
            return content;
        }

        /// <summary>
        /// Get hardware info.
        /// mtkclient: get_hw_info()
        /// </summary>
        public bool GetHwInfo()
        {
            string cmd = MakeCmd("GET-HW-INFO");
            var result = SendCommandEx(cmd, noAck: true);
            if (result == null) return false;
            var cr = GetCommandResult();
            if (!cr.IsUpFile) return false;
            byte[] data = Download(cr.AsUpFile);
            var endR = GetCommandResult();
            Ack();
            if (endR.AsString == "OK")
            {
                var startR = GetCommandResult();
                return startR.Command == "CMD:START";
            }
            return false;
        }

        /// <summary>
        /// Read partition table.
        /// mtkclient: read_partition_table()
        /// </summary>
        public byte[] ReadPartitionTable()
        {
            string cmd = MakeCmd("READ-PARTITION-TABLE");
            var result = SendCommandEx(cmd, noAck: true);
            if (result == null || (result is bool b && !b)) return null;

            var cr = GetCommandResult();
            if (!cr.IsUpFile) return null;
            byte[] data = Download(cr.AsUpFile);

            // CMD:END
            var endR = GetCommandResult();
            Ack();
            if (endR.AsString == "OK")
            {
                var startR = GetCommandResult();
                if (startR.Command == "CMD:START")
                    return data;
            }
            return null;
        }

        /// <summary>
        /// Check SLA status.
        /// mtkclient: check_sla()
        /// </summary>
        public bool CheckSla()
        {
            byte[] propData = GetSysProperty("DA.SLA", 0x200000);
            if (propData == null) return false;

            string data = Encoding.UTF8.GetString(propData);
            if (data.Contains("item key="))
            {
                string tmp = data.Substring(data.IndexOf("item key=") + 8);
                int gt = tmp.IndexOf('>');
                int lt = tmp.IndexOf('<', gt > 0 ? gt : 0);
                if (gt >= 0 && lt > gt)
                {
                    string val = tmp.Substring(gt + 1, lt - gt - 1).Trim();
                    return !val.Equals("DISABLED", StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }

        /// <summary>
        /// Get system property — returns raw bytes.
        /// mtkclient: get_sys_property(key, length)
        /// </summary>
        public byte[] GetSysProperty(string key = "DA.SLA", int length = 0x200000)
        {
            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         $"<command>CMD:GET-SYS-PROPERTY</command><arg>" +
                         $"<key>{key}</key><length>{length}</length></arg></da>";

            var result = SendCommandEx(cmd, noAck: true);
            if (result == null || (result is bool b && !b)) return null;

            var cr = GetCommandResult();
            if (!cr.IsUpFile) return null;

            byte[] data = Download(cr.AsUpFile);
            // CMD:END
            var endR = GetCommandResult();
            Ack();
            if (endR.AsString == "OK")
            {
                var startR = GetCommandResult();
                if (startR.Command == "CMD:START")
                    return data;
            }
            return null;
        }

        #endregion

        #region Flash Operations

        /// <summary>
        /// Read flash via XML protocol.
        /// mtkclient: readflash(addr, length, filename, parttype, display)
        /// </summary>
        public byte[] ReadFlash(ulong addr, ulong length, string partType = null,
                                Action<int> progressCallback = null)
        {
            string storage = partType ?? "user";
            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         $"<command>CMD:READ-FLASH</command><arg>" +
                         $"<target_file>{storage}</target_file>" +
                         $"<offset>0x{addr:X}</offset><length>0x{length:X}</length></arg></da>";

            var result = SendCommandEx(cmd, noAck: true);
            if (result == null || (result is bool b && !b)) return null;

            var cr = GetCommandResult();
            if (!cr.IsUpFile)
            {
                _error($"ReadFlash: unexpected response: {cr.AsString}");
                return null;
            }

            byte[] data = DownloadRaw(cr.AsUpFile, progressCallback);

            // CMD:START (readflash doesn't have explicit CMD:END ACK in Python)
            var startR = GetCommandResult();
            if (startR.Command == "CMD:START")
                return data;

            return data;
        }

        /// <summary>
        /// Write flash via XML protocol.
        /// mtkclient: writeflash(addr, length, filename, offset, parttype, wdata, display)
        /// </summary>
        public bool WriteFlash(ulong addr, byte[] data, string partType = null,
                               Action<int> progressCallback = null)
        {
            string storage = partType ?? "user";
            ulong length = (ulong)data.Length;
            // Align to 512
            if (length % 512 != 0)
                length += 512 - (length % 512);

            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         $"<command>CMD:WRITE-FLASH</command><arg>" +
                         $"<target_file>{storage}</target_file>" +
                         $"<offset>0x{addr:X}</offset><length>0x{length:X}</length></arg></da>";

            var result = SendCommandEx(cmd, noAck: true);
            if (result == null || (result is bool b && !b)) return false;

            // Expect FILE-SYS-OPERATION for file size
            var cr = GetCommandResult();
            if (cr.IsFileSysOp)
            {
                if (cr.AsFileSysOp.Key != "FILE-SIZE") return false;
                AckValue((int)length);

                // Expect CMD:DOWNLOAD-FILE
                var dcr = GetCommandResult();
                if (!dcr.IsDwnFile) return false;

                // Pad data to aligned length
                byte[] padded = data;
                if ((int)length > data.Length)
                {
                    padded = new byte[length];
                    Buffer.BlockCopy(data, 0, padded, 0, data.Length);
                }

                if (!Upload(dcr.AsDwnFile, padded, raw: true))
                {
                    _error($"Error on writing flash at 0x{addr:X}");
                    return false;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Format flash.
        /// mtkclient: formatflash(addr, length, storage)
        /// Note: formatflash uses a simpler protocol than read/write.
        /// </summary>
        public bool FormatFlash(ulong addr, ulong length, string partType = null)
        {
            string storage = partType ?? "user";
            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         $"<command>CMD:ERASE-FLASH</command><arg>" +
                         $"<target_file>{storage}</target_file>" +
                         $"<offset>0x{addr:X}</offset><length>0x{length:X}</length></arg></da>";

            // Python formatflash: send_command() then get_response() == "OK"
            var result = SendCommandEx(cmd);
            if (result is bool bv) return bv;
            // Fallback: read extra OK
            string resp = GetResponse();
            return resp != null && resp.Contains("OK");
        }

        /// <summary>
        /// Shutdown device.
        /// mtkclient: shutdown(async_mode, dl_bit, bootmode)
        /// </summary>
        public bool Shutdown(int asyncMode = 0, int dlBit = 0, int bootMode = 0)
        {
            string mode = bootMode == 0 ? "NORMAL" : bootMode == 1 ? "META" : "NORMAL";
            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         $"<command>CMD:SHUTDOWN</command><arg><async>{asyncMode}</async>" +
                         $"<dl_bit>{dlBit}</dl_bit><boot_mode>{mode}</boot_mode></arg></da>";

            var result = SendCommandEx(cmd);
            return result is bool b ? b : result != null;
        }

        #endregion

        #region SLA / Auth

        /// <summary>
        /// Handle SLA authentication — sends signature to device via upload protocol.
        /// mtkclient: handle_sla(data, display, timeout)
        /// Uses CMD:SECURITY-SET-FLASH-POLICY + DwnFile upload.
        /// </summary>
        public bool HandleSla(byte[] slaSignature = null)
        {
            slaSignature = slaSignature ?? new byte[0x100];

            // CMD:SECURITY-SET-FLASH-POLICY with MEM source
            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         $"<command>CMD:SECURITY-SET-FLASH-POLICY</command><arg>" +
                         $"<source_file>MEM://0x8000000:0x{slaSignature.Length:X}</source_file></arg></da>";

            var result = SendCommandEx(cmd);
            if (result is CmdResult cr && cr.IsDwnFile)
            {
                _info("Running SLA auth...");
                if (Upload(cr.AsDwnFile, slaSignature))
                {
                    _info("Successfully uploaded SLA auth.");
                    return true;
                }
            }
            else
            {
                // Fallback: try to handle result as bool
                if (result is bool b && b) return true;
            }
            return false;
        }

        /// <summary>
        /// Full SLA flow: check → find key → sign challenge → authenticate.
        /// Orchestrates the complete SLA handshake as in Python upload_da().
        /// </summary>
        public bool DoSlaAuth()
        {
            bool slaEnabled = CheckSla();
            if (!slaEnabled)
            {
                _info("DA XML SLA is disabled.");
                return true;
            }

            _info("DA XML SLA is enabled — authenticating...");

            // Find matching RSA key in DA2
            byte[] da2 = _daConfig?.Da2;
            Auth.SlaKey rsakey = null;
            if (da2 != null)
            {
                foreach (var key in Auth.SlaKeys.DaSlaKeys)
                {
                    byte[] nBytes = Auth.Sla.BigIntegerToBytes(key.GetN(), 0);
                    if (nBytes != null && ByteUtils.FindBytes(da2, nBytes) >= 0)
                    {
                        rsakey = key;
                        _info($"Found matching SLA key: {key.Vendor}/{key.Name}");
                        break;
                    }
                }
            }

            if (rsakey == null)
            {
                _warning("No valid SLA key found, using dummy auth...");

                // Check for Infinix/Transsion
                bool isTranssion = da2 != null && ByteUtils.FindBytes(da2, Encoding.ASCII.GetBytes("Transsion")) >= 0;
                byte[] slaSignature;
                if (isTranssion)
                {
                    slaSignature = HexToBytes(
                        "46177D7539E98DF6D6FF46C2E5BB459A059F91E5100EA9AE5908804F009E339372" +
                        "BD94D937C366B0320CD58ED905997D2D529E6285E3358EDE178C33BDD067295F4B" +
                        "18E701C39B96A7B6F4C8B8CE3DAC115BAF30254561CC5A64FB9FDCD45A28F7058E" +
                        "BC0A707ECC5146C296FF72E7C33C0CC254A2F2A78F6C7BD9D2692B2145C2334A10" +
                        "BEE6D8B9303D8F368E630B49AA2C783A170A5449F7E9DCFF8C8EA8690F05B6F3ED" +
                        "4D3DED5B1FAB30A82E93FBFC91D3B4F03728597AB27E3A4F06600D76FA0D50DE3D" +
                        "8BAD6E0CF45654DBA23121F601130C2AB61B3F4C867E5BC0FD6B25C33F9FEBCD1B" +
                        "3393EF6406AF8C440CE3454C3689AE88405F8A2B875EC0C32A9B");
                }
                else
                {
                    slaSignature = new byte[0x100];
                }

                if (HandleSla(slaSignature))
                {
                    _info("SLA Signature was accepted.");
                    GetHwInfo();
                    return true;
                }
                else
                {
                    _warning("SLA Key wasn't accepted.");
                    return false;
                }
            }
            else
            {
                // Have a matching key — get device info and sign challenge
                var devInfo = GetDevInfo();
                byte[] rnd = null;
                if (devInfo.ContainsKey("rnd"))
                    rnd = devInfo["rnd"];

                if (rnd == null || rnd.Length == 0)
                    rnd = new byte[32];

                byte[] slaSignature;
                try
                {
                    slaSignature = Auth.Sla.GenerateDaSlaSignature(rnd, rsakey.ToRsaParameters());
                }
                catch (Exception ex)
                {
                    _error($"SLA signing failed: {ex.Message}");
                    return false;
                }

                if (!HandleSla(slaSignature))
                {
                    _warning("SLA Key wasn't accepted.");
                    return false;
                }
                _info("SLA Signature was accepted.");
                return true;
            }
        }

        #endregion

        #region Setup

        /// <summary>
        /// Setup environment after DA upload — set runtime parameters.
        /// mtkclient: setup_env()
        /// </summary>
        public bool SetupEnv()
        {
            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.1</version>" +
                         "<command>CMD:SET-RUNTIME-PARAMETER</command><arg>" +
                         "<checksum_level>NONE</checksum_level>" +
                         "<battery_exist>AUTO-DETECT</battery_exist>" +
                         "<da_log_level>INFO</da_log_level>" +
                         "<log_channel>UART</log_channel>" +
                         "<system_os>LINUX</system_os>" +
                         "</arg><adv><initialize_dram>YES</initialize_dram></adv></da>\0";

            var result = SendCommandEx(cmd);
            return result is bool b ? b : result != null;
        }

        /// <summary>
        /// Setup HW init — notify device of host capabilities.
        /// mtkclient: setup_hw_init()
        /// </summary>
        public bool SetupHwInit()
        {
            // Step 1: Host supported commands
            string capCmd = MakeCmd("HOST-SUPPORTED-COMMANDS",
                "<host_capability>CMD:DOWNLOAD-FILE^1@CMD:FILE-SYS-OPERATION^1@CMD:PROGRESS-REPORT^1@CMD:UPLOAD-FILE^1@</host_capability>");
            SendCommandEx(capCmd);

            // Step 2: Notify init HW
            string initCmd = MakeCmd("NOTIFY-INIT-HW");
            var result = SendCommandEx(initCmd);
            return result is bool b ? b : result != null;
        }

        /// <summary>
        /// Boot to address (upload DA2).
        /// mtkclient: boot_to(addr, data, display, timeout)
        /// Uses CMD:BOOT-TO → DwnFile → upload protocol.
        /// </summary>
        public bool BootTo(uint addr, byte[] data)
        {
            _info($"XML: BootTo 0x{addr:X} (size: 0x{data.Length:X})");

            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         $"<command>CMD:BOOT-TO</command><arg>" +
                         $"<at_address>0x{addr:X}</at_address>" +
                         $"<jmp_address>0x{addr:X}</jmp_address>" +
                         $"<source_file>MEM://0x8000000:0x{data.Length:X}</source_file></arg></da>";

            var result = SendCommandEx(cmd);
            if (result is CmdResult cr && cr.IsDwnFile)
            {
                _info("Uploading stage 2...");
                if (Upload(cr.AsDwnFile, data))
                {
                    _info("Successfully uploaded stage 2.");
                    return true;
                }
                _error("Stage 2 upload failed.");
            }
            else
            {
                _error("Wrong boot_to response.");
            }
            return false;
        }

        /// <summary>
        /// Check device lifecycle.
        /// mtkclient: check_lifecycle()
        /// </summary>
        public bool CheckLifecycle()
        {
            string cmd = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                         "<command>CMD:EMMC-CONTROL</command><arg>" +
                         "<function>LIFE-CYCLE-STATUS</function></arg></da>";

            var result = SendCommandEx(cmd, noAck: true);
            if (result == null) return false;

            var cr = GetCommandResult();
            if (!cr.IsUpFile)
            {
                // CMD:END without data
                if (cr.Command == "CMD:END")
                {
                    Ack();
                    GetCommandResult(); // CMD:START
                }
                return false;
            }

            byte[] data = Download(cr.AsUpFile);
            var endR = GetCommandResult();
            Ack();
            if (endR.AsString == "OK")
            {
                var startR = GetCommandResult();
                if (startR.Command == "CMD:START")
                    return data != null && data.Length >= 3 &&
                           data[0] == (byte)'O' && data[1] == (byte)'K';
            }
            return false;
        }

        /// <summary>
        /// Change USB speed.
        /// mtkclient: change_usb_speed()
        /// </summary>
        public bool ChangeUsbSpeed()
        {
            string cmd = MakeCmd("CAN-HIGHER-USB-SPEED",
                "<target_file>MEM://0x8000000:0x40</target_file>");
            var result = SendCommandEx(cmd);
            return result is bool b ? b : result != null;
        }

        /// <summary>
        /// Re-init: read HW info to update device state.
        /// mtkclient: reinit()
        /// </summary>
        public bool Reinit()
        {
            return GetHwInfo();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Create a simple XML command string.
        /// </summary>
        private static string MakeCmd(string command, string argContent = null)
        {
            if (argContent != null)
                return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                       $"<command>CMD:{command}</command><arg>{argContent}</arg></da>";
            return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><da><version>1.0</version>" +
                   $"<command>CMD:{command}</command></da>";
        }

        private static void WriteLE32(byte[] buf, int offset, uint val)
        {
            buf[offset] = (byte)(val & 0xFF);
            buf[offset + 1] = (byte)((val >> 8) & 0xFF);
            buf[offset + 2] = (byte)((val >> 16) & 0xFF);
            buf[offset + 3] = (byte)((val >> 24) & 0xFF);
        }

        /// <summary>
        /// Extract field value from XML response string.
        /// mtkclient: get_field(data, fieldname)
        /// </summary>
        public static string GetField(string data, string fieldName)
        {
            if (data == null) return null;
            string openTag = $"<{fieldName}>";
            string closeTag = $"</{fieldName}>";
            int start = data.IndexOf(openTag);
            if (start < 0) return null;
            start += openTag.Length;
            int end = data.IndexOf(closeTag, start);
            if (end < 0) return null;
            return data.Substring(start, end - start);
        }

        private static int ParseHexOrDec(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt32(s.Substring(2), 16);
            return int.TryParse(s, out int v) ? v : 0;
        }

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new byte[0];
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        #endregion
    }
}
