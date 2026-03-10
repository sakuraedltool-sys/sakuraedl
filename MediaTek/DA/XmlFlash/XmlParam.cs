// mtkclient port: DA/xmlflash/xml_param.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;

namespace SakuraEDL.MediaTek.DA.XmlFlash
{
    public static class XmlConstants
    {
        public const int MaxNodeValueLength = 256;
        public const int MaxAddressLength = 9;
        public const int MaxXmlDataLength = 0x200000;
    }

    public enum XmlDataType : uint
    {
        DT_PROTOCOL_FLOW = 1,
        DT_MESSAGE = 2
    }

    public static class XmlChecksumAlgorithm
    {
        public const string NONE = "NONE";
        public const string USB = "USB";
        public const string STORAGE = "STORAGE";
        public const string USB_STORAGE = "USB-STORAGE";
    }

    public static class XmlLogLevel
    {
        public const string TRACE = "TRACE";
        public const string DEBUG = "DEBUG";
        public const string INFO = "INFO";
        public const string WARN = "WARN";
        public const string ERROR = "ERROR";
    }

    public static class XmlLogChannel
    {
        public const string USB = "USB";
        public const string UART = "UART";
    }

    public static class XmlBatterySetting
    {
        public const string YES = "YES";
        public const string NO = "NO";
        public const string AUTO_DETECT = "AUTO-DETECT";
    }

    public static class XmlFtSystemOSE
    {
        public const string OS_WIN = "WINDOWS";
        public const string OS_LINUX = "LINUX";
    }
}
