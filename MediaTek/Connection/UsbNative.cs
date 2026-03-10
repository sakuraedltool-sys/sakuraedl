// WinUSB and SetupAPI P/Invoke declarations for USB control transfers.
// Used by Port.cs for KamakiriPl exploit CDC manipulation.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SakuraEDL.MediaTek.Connection
{
    /// <summary>
    /// Native P/Invoke wrappers for WinUSB and SetupAPI.
    /// </summary>
    internal static class UsbNative
    {
        #region Constants

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x01;
        public const uint FILE_SHARE_WRITE = 0x02;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        // SetupAPI flags
        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;

        // WinUSB GUID (generic USB device interface)
        public static readonly Guid GUID_DEVINTERFACE_USB_DEVICE =
            new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");

        #endregion

        #region Structures

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WINUSB_SETUP_PACKET
        {
            public byte RequestType;
            public byte Request;
            public ushort Value;
            public ushort Index;
            public ushort Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        #endregion

        #region Kernel32

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        #endregion

        #region WinUSB

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Initialize(
            IntPtr DeviceHandle,
            out IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Free(IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_ControlTransfer(
            IntPtr InterfaceHandle,
            WINUSB_SETUP_PACKET SetupPacket,
            [In, Out] byte[] Buffer,
            uint BufferLength,
            out uint LengthTransferred,
            IntPtr Overlapped);

        #endregion

        #region SetupAPI

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid ClassGuid,
            string Enumerator,
            IntPtr hwndParent,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr DeviceInfoSet,
            IntPtr DeviceInfoData,
            ref Guid InterfaceClassGuid,
            uint MemberIndex,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize,
            out uint RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        #endregion

        #region Helpers

        /// <summary>
        /// Find USB device path for MediaTek device with given VID.
        /// </summary>
        public static string FindMtkDevicePath(ushort vid)
        {
            Guid guid = GUID_DEVINTERFACE_USB_DEVICE;
            IntPtr devInfoSet = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (devInfoSet == INVALID_HANDLE_VALUE) return null;

            try
            {
                string vidStr = $"VID_{vid:X4}".ToUpperInvariant();

                var interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = Marshal.SizeOf(interfaceData);

                for (uint i = 0; i < 256; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref guid, i, ref interfaceData))
                        break;

                    // Get required size
                    SetupDiGetDeviceInterfaceDetail(devInfoSet, ref interfaceData,
                        IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);

                    if (requiredSize == 0) continue;

                    // Allocate and fill detail data
                    IntPtr detailData = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize = 5 on x86, 8 on x64
                        Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 5);

                        if (SetupDiGetDeviceInterfaceDetail(devInfoSet, ref interfaceData,
                                detailData, requiredSize, out _, IntPtr.Zero))
                        {
                            // DevicePath starts at offset 4
                            string path = Marshal.PtrToStringAuto(detailData + 4);
                            if (path != null && path.ToUpperInvariant().Contains(vidStr))
                                return path;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailData);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return null;
        }

        #endregion
    }
}
