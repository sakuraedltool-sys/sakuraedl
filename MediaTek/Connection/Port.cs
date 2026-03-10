// mtkclient port: Port.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.IO.Ports;
using System.Threading;

namespace SakuraEDL.MediaTek.Connection
{
    /// <summary>
    /// Port — unified communication layer for USB and Serial.
    /// Port of mtkclient/Library/Port.py
    /// </summary>
    public class Port : IDisposable
    {
        private SerialPort _serial;
        private readonly Action<string> _log;
        private readonly Action<string> _debug;
        private readonly Action<string> _warning;
        private readonly Action<string> _error;
        private bool _disposed;

        public bool Connected { get; private set; }
        public string PortName { get; set; }

        // Handshake sequence: A0 0A 50 05 → 5F F5 AF FA
        private static readonly byte[] HandshakeCmd = { 0xA0, 0x0A, 0x50, 0x05 };

        public Port(Action<string> log = null, Action<string> debug = null,
                    Action<string> warning = null, Action<string> error = null)
        {
            _log = log ?? delegate { };
            _debug = debug ?? delegate { };
            _warning = warning ?? delegate { };
            _error = error ?? delegate { };
        }

        #region Connection

        public bool Connect(string portName = null, int baudRate = 115200)
        {
            if (portName != null) PortName = portName;
            if (string.IsNullOrEmpty(PortName)) return false;

            try
            {
                if (_serial != null && _serial.IsOpen)
                    _serial.Close();

                _serial = new SerialPort(PortName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 5000,
                    WriteTimeout = 5000,
                    ReadBufferSize = 0x14000,  // 81920
                    WriteBufferSize = 0x14000
                };
                _serial.Open();
                Connected = _serial.IsOpen;
                return Connected;
            }
            catch (Exception ex)
            {
                _debug($"Connect error: {ex.Message}");
                Connected = false;
                return false;
            }
        }

        public void Close()
        {
            try
            {
                if (_serial != null && _serial.IsOpen)
                    _serial.Close();
            }
            catch { }
            Connected = false;
        }

        #endregion

        #region Handshake (mtkclient: run_serial_handshake / run_handshake)

        public bool RunHandshake()
        {
            int i = 0;
            int length = HandshakeCmd.Length;

            try
            {
                while (i < length)
                {
                    if (UsbWrite(new byte[] { HandshakeCmd[i] }))
                    {
                        byte[] v = UsbRead(1, 20);
                        if (v != null && v.Length == 1 && v[0] == (byte)(~HandshakeCmd[i] & 0xFF))
                        {
                            i++;
                        }
                        else
                        {
                            i = 0;
                        }
                    }
                }
                _log("Device detected :)");
                return true;
            }
            catch (Exception ex)
            {
                _debug(ex.Message);
                Thread.Sleep(5);
            }
            return false;
        }

        public bool Handshake(int? maxTries = null)
        {
            int counter = 0;
            int loop = 0;

            while (true)
            {
                try
                {
                    if (!Connected)
                        Connected = Connect();

                    if (maxTries.HasValue && counter >= maxTries.Value)
                        break;
                    counter++;

                    if (Connected && RunHandshake())
                    {
                        _log("Handshake successful.");
                        return true;
                    }

                    if (loop == 5)
                    {
                        _log("Hint: Power off the phone before connecting.\n" +
                             "For brom mode, press and hold vol up/dwn and connect USB.\n" +
                             "For preloader mode, don't press any button and connect USB.");
                    }
                    loop++;
                    if (loop >= 20) loop = 0;
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("access denied"))
                        _warning(ex.Message);
                    _debug(ex.Message);
                }
            }
            return false;
        }

        #endregion

        #region Raw IO (mtkclient: usbwrite, usbread, echo, rbyte, rword, rdword)

        public bool UsbWrite(byte[] data)
        {
            try
            {
                if (_serial == null || !_serial.IsOpen) return false;
                _serial.Write(data, 0, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                _debug($"Write error: {ex.Message}");
                return false;
            }
        }

        public byte[] UsbRead(int length, int timeout = 5000)
        {
            try
            {
                if (_serial == null || !_serial.IsOpen) return null;
                int oldTimeout = _serial.ReadTimeout;
                _serial.ReadTimeout = timeout;
                byte[] buf = new byte[length];
                int pos = 0;
                while (pos < length)
                {
                    int read = _serial.Read(buf, pos, length - pos);
                    if (read <= 0) break;
                    pos += read;
                }
                _serial.ReadTimeout = oldTimeout;
                return pos == length ? buf : null;
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _debug($"Read error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Echo: write data and verify echoed response matches.
        /// mtkclient: echo(data)
        /// </summary>
        public bool Echo(byte[] data)
        {
            UsbWrite(data);
            byte[] tmp = UsbRead(data.Length, 5000);
            if (tmp == null || tmp.Length != data.Length)
                return false;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != tmp[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Read single byte.
        /// </summary>
        public byte? Rbyte()
        {
            byte[] data = UsbRead(1);
            return data != null ? (byte?)data[0] : null;
        }

        /// <summary>
        /// Read uint16 (big-endian).
        /// </summary>
        public ushort? Rword()
        {
            byte[] data = UsbRead(2);
            if (data == null) return null;
            return (ushort)((data[0] << 8) | data[1]);
        }

        /// <summary>
        /// Read uint32 (big-endian).
        /// </summary>
        public uint? Rdword()
        {
            byte[] data = UsbRead(4);
            if (data == null) return null;
            return (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        }

        /// <summary>
        /// Send MTK command and read response.
        /// mtkclient: mtk_cmd(value, bytestoread, nocmd)
        /// </summary>
        public byte[] MtkCmd(byte[] value, int bytesToRead = 0, bool noCmd = false)
        {
            if (!UsbWrite(value))
            {
                _warning($"Couldn't send: {BitConverter.ToString(value)}");
                return new byte[0];
            }
            Thread.Sleep(50);

            if (noCmd)
            {
                return UsbRead(bytesToRead) ?? new byte[0];
            }

            byte[] cmdRsp = UsbRead(value.Length);
            if (cmdRsp == null || cmdRsp[0] != value[0])
            {
                _error($"Cmd error: {(cmdRsp != null ? BitConverter.ToString(cmdRsp) : "null")}");
                return null;
            }

            if (bytesToRead > 0)
                return UsbRead(bytesToRead) ?? new byte[0];

            return new byte[0];
        }

        public SerialPort GetSerialPort() => _serial;

        #endregion

        #region USB Control Transfers (for exploits)

        private IntPtr _usbHandle = IntPtr.Zero;
        private IntPtr _winusbHandle = IntPtr.Zero;

        /// <summary>
        /// Open WinUSB handle for USB control transfers.
        /// Called lazily on first CtrlTransfer if not already open.
        /// </summary>
        private bool EnsureUsbHandle()
        {
            if (_winusbHandle != IntPtr.Zero) return true;

            try
            {
                // Find USB device path via SetupAPI for MediaTek VID 0x0E8D
                string devicePath = UsbNative.FindMtkDevicePath(0x0E8D);
                if (devicePath == null)
                {
                    _warning("USB: Could not find MediaTek USB device for control transfers.");
                    return false;
                }

                _usbHandle = UsbNative.CreateFile(devicePath,
                    UsbNative.GENERIC_READ | UsbNative.GENERIC_WRITE,
                    UsbNative.FILE_SHARE_READ | UsbNative.FILE_SHARE_WRITE,
                    IntPtr.Zero, UsbNative.OPEN_EXISTING,
                    UsbNative.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

                if (_usbHandle == UsbNative.INVALID_HANDLE_VALUE)
                {
                    _warning("USB: Could not open device handle.");
                    _usbHandle = IntPtr.Zero;
                    return false;
                }

                if (!UsbNative.WinUsb_Initialize(_usbHandle, out _winusbHandle))
                {
                    _warning("USB: WinUsb_Initialize failed.");
                    UsbNative.CloseHandle(_usbHandle);
                    _usbHandle = IntPtr.Zero;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _warning($"USB: Init failed — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// USB control transfer (IN direction — reads data from device).
        /// Used by Kamakiri/KamakiriPl exploits for CDC control manipulation.
        /// </summary>
        public byte[] CtrlTransfer(byte bmRequestType, byte bRequest, int wValue, int wIndex, int wLength)
        {
            if (!EnsureUsbHandle())
                return new byte[wLength]; // Fallback: return zeros

            var setupPacket = new UsbNative.WINUSB_SETUP_PACKET
            {
                RequestType = bmRequestType,
                Request = bRequest,
                Value = (ushort)wValue,
                Index = (ushort)wIndex,
                Length = (ushort)wLength
            };

            byte[] buffer = new byte[wLength];
            if (UsbNative.WinUsb_ControlTransfer(_winusbHandle, setupPacket,
                    buffer, (uint)wLength, out uint bytesRead, IntPtr.Zero))
            {
                return buffer;
            }

            _debug($"USB CtrlTransfer IN failed (0x{bRequest:X2})");
            return new byte[wLength];
        }

        /// <summary>
        /// USB control transfer (OUT direction — sends data to device).
        /// </summary>
        public bool CtrlTransferOut(byte bmRequestType, byte bRequest, int wValue, int wIndex, byte[] data)
        {
            if (!EnsureUsbHandle())
                return false;

            var setupPacket = new UsbNative.WINUSB_SETUP_PACKET
            {
                RequestType = bmRequestType,
                Request = bRequest,
                Value = (ushort)wValue,
                Index = (ushort)wIndex,
                Length = (ushort)(data?.Length ?? 0)
            };

            byte[] buffer = data ?? new byte[0];
            if (UsbNative.WinUsb_ControlTransfer(_winusbHandle, setupPacket,
                    buffer, (uint)buffer.Length, out uint bytesWritten, IntPtr.Zero))
            {
                return true;
            }

            _debug($"USB CtrlTransfer OUT failed (0x{bRequest:X2})");
            return false;
        }

        /// <summary>
        /// Close WinUSB handles.
        /// </summary>
        private void CloseUsbHandles()
        {
            if (_winusbHandle != IntPtr.Zero)
            {
                UsbNative.WinUsb_Free(_winusbHandle);
                _winusbHandle = IntPtr.Zero;
            }
            if (_usbHandle != IntPtr.Zero)
            {
                UsbNative.CloseHandle(_usbHandle);
                _usbHandle = IntPtr.Zero;
            }
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                CloseUsbHandles();
                Close();
                _serial?.Dispose();
                _disposed = true;
            }
        }
    }
}
