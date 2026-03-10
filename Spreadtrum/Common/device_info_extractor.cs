// ============================================================================
// SakuraEDL - Device Info Extractor | 设备信息提取器
// ============================================================================
// [ZH] 设备信息提取 - 从 Boot 镜像提取展讯设备信息
// [EN] Device Info Extractor - Extract Spreadtrum device info from Boot image
// [JA] デバイス情報抽出 - BootイメージからSpreadtrumデバイス情報を抽出
// [KO] 기기 정보 추출 - Boot 이미지에서 Spreadtrum 기기 정보 추출
// [RU] Извлечение информации - Извлечение данных Spreadtrum из Boot образа
// [ES] Extractor de info - Extraer información de dispositivo de Boot
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SakuraEDL.Spreadtrum.Common
{
    /// <summary>
    /// 设备信息提取器 - 从 Boot.img 提取设备详细信息
    /// </summary>
    public class DeviceInfoExtractor
    {
        private readonly Action<string> _log;

        /// <summary>
        /// 属性键映射表 (显示名称 -> 属性键列表)
        /// </summary>
        public static readonly Dictionary<string, List<string>> PropertyMapping = new Dictionary<string, List<string>>
        {
            // 基本信息
            { "Brand", new List<string> { "ro.product.brand", "ro.product.product.brand", "ro.product.odm.brand", "ro.product.vendor.brand", "ro.product.system_ext.brand", "ro.product.bootimage.brand" } },
            { "Manufacturer", new List<string> { "ro.product.manufacturer", "ro.product.vendor.manufacturer", "ro.product.odm.manufacturer", "ro.product.product.manufacturer", "ro.product.system_ext.manufacturer", "ro.product.bootimage.manufacturer" } },
            { "Model", new List<string> { "ro.product.model", "ro.product.product.model", "ro.product.system_ext.model", "ro.product.system.model", "ro.product.vendor.model", "ro.product.odm.model", "ro.product.bootimage.model" } },
            { "Name", new List<string> { "ro.product.product.name", "ro.product.system_ext.name", "ro.product.system.name", "ro.product.name", "ro.product.bootimage.name" } },
            { "Device", new List<string> { "ro.product.device", "ro.product.product.device", "ro.product.system_ext.device", "ro.product.system.device", "ro.product.vendor.device", "ro.product.board", "ro.product.bootimage.device" } },
            { "MarketName", new List<string> { "ro.oppo.market.name", "ro.product.product.marketname", "ro.product.system_ext.marketname", "ro.product.system.marketname" } },
            
            // 版本信息
            { "AndroidVersion", new List<string> { "ro.odm.build.version.release", "ro.product.build.version.release", "ro.build.version.release", "ro.bootimage.build.version.release" } },
            { "SDKVersion", new List<string> { "ro.build.version.sdk", "ro.vendor.build.version.sdk", "ro.product.build.version.sdk", "ro.bootimage.build.version.sdk" } },
            { "BuildID", new List<string> { "ro.product.build.id", "ro.build.id", "ro.bootimage.build.id" } },
            { "Incremental", new List<string> { "ro.system.build.version.incremental", "ro.build.version.incremental", "ro.vendor.build.version.incremental" } },
            { "SecurityPatch", new List<string> { "ro.build.version.security_patch", "ro.vendor.build.security_patch", "ro.bootimage.build.security_patch" } },
            { "BuildDate", new List<string> { "ro.product.build.date", "ro.system.build.date", "ro.build.date", "ro.bootimage.build.date" } },
            
            // 平台信息
            { "Platform", new List<string> { "ro.board.platform", "ro.vendor.mediatek.platform", "ro.mediatek.platform", "ro.vivo.product.platform" } },
            { "Hardware", new List<string> { "ro.hardware", "ro.boot.hardware" } },
            { "CpuAbi", new List<string> { "ro.vendor.product.cpu.abilist", "ro.product.cpu.abi" } },
            { "Chipname", new List<string> { "ro.hardware.chipname", "ro.board.platform" } },
            
            // 指纹信息
            { "Fingerprint", new List<string> { "ro.build.fingerprint", "ro.bootimage.build.fingerprint", "ro.system.build.fingerprint" } },
            { "Description", new List<string> { "ro.build.description" } },
            
            // 区域信息
            { "Region", new List<string> { "ro.miui.build.region", "persist.sys.oppo.region", "ro.build.region" } },
            { "Timezone", new List<string> { "persist.sys.timezone" } },
            
            // 厂商特定
            { "MIUIVersion", new List<string> { "ro.miui.ui.version.name", "ro.miui.ui.version.code" } },
            { "ColorOSVersion", new List<string> { "ro.build.version.opporom", "ro.oppo.rom.version" } },
            { "EMUIVersion", new List<string> { "ro.build.version.emui" } },
            { "OneUIVersion", new List<string> { "ro.build.PDA" } },
            { "FuntouchOSVersion", new List<string> { "ro.vivo.os.build.display.id", "ro.vivo.os.version" } },
            
            // 软件版本
            { "SoftwareVersion", new List<string> { "ro.vendor.build.software.version", "ro.build.software.version" } },
            { "BaseOS", new List<string> { "ro.build.version.base_os" } },
            { "Codename", new List<string> { "ro.build.version.codename", "ro.system.build.version.release_or_codename" } },
        };

        public DeviceInfoExtractor(Action<string> log = null)
        {
            _log = log;
        }

        /// <summary>
        /// 从 Boot.img 文件提取设备信息
        /// </summary>
        public SprdDeviceDetails ExtractFromBootImage(string bootImagePath)
        {
            var parser = new BootParser(_log);
            var bootInfo = parser.Parse(bootImagePath);
            return ExtractFromBootInfo(bootInfo);
        }

        /// <summary>
        /// 从 Boot.img 数据提取设备信息
        /// </summary>
        public SprdDeviceDetails ExtractFromBootImage(byte[] bootImageData)
        {
            var parser = new BootParser(_log);
            var bootInfo = parser.Parse(bootImageData);
            return ExtractFromBootInfo(bootInfo);
        }

        /// <summary>
        /// 从解析的 Boot 信息提取设备详情
        /// </summary>
        public SprdDeviceDetails ExtractFromBootInfo(BootImageInfo bootInfo)
        {
            var details = new SprdDeviceDetails();
            var parser = new BootParser(_log);

            // 提取 ramdisk
            var entries = parser.ExtractRamdisk(bootInfo);
            if (entries == null || entries.Count == 0)
            {
                _log?.Invoke("[DeviceInfo] 无法提取 Ramdisk");
                return details;
            }

            // 获取所有属性
            var cpioParser = new CpioParser(_log);
            var allProps = cpioParser.GetAllProperties(entries);
            
            _log?.Invoke($"[DeviceInfo] 找到 {allProps.Count} 个属性");

            // 填充设备信息
            details.Brand = GetPropertyValue(allProps, "Brand");
            details.Manufacturer = GetPropertyValue(allProps, "Manufacturer");
            details.Model = GetPropertyValue(allProps, "Model");
            details.Name = GetPropertyValue(allProps, "Name");
            details.Device = GetPropertyValue(allProps, "Device");
            details.MarketName = GetPropertyValue(allProps, "MarketName");
            
            details.AndroidVersion = GetPropertyValue(allProps, "AndroidVersion");
            details.SdkVersion = GetPropertyValue(allProps, "SDKVersion");
            details.BuildId = GetPropertyValue(allProps, "BuildID");
            details.Incremental = GetPropertyValue(allProps, "Incremental");
            details.SecurityPatch = GetPropertyValue(allProps, "SecurityPatch");
            details.BuildDate = GetPropertyValue(allProps, "BuildDate");
            
            details.Platform = GetPropertyValue(allProps, "Platform");
            details.Hardware = GetPropertyValue(allProps, "Hardware");
            details.CpuAbi = GetPropertyValue(allProps, "CpuAbi");
            details.Chipname = GetPropertyValue(allProps, "Chipname");
            
            details.Fingerprint = GetPropertyValue(allProps, "Fingerprint");
            details.Description = GetPropertyValue(allProps, "Description");
            
            details.Region = GetPropertyValue(allProps, "Region");
            
            // 厂商版本
            details.VendorRomVersion = 
                GetPropertyValue(allProps, "MIUIVersion") ??
                GetPropertyValue(allProps, "ColorOSVersion") ??
                GetPropertyValue(allProps, "EMUIVersion") ??
                GetPropertyValue(allProps, "OneUIVersion") ??
                GetPropertyValue(allProps, "FuntouchOSVersion");

            // 存储所有原始属性
            details.AllProperties = allProps;

            // 从 cmdline 提取额外信息
            if (!string.IsNullOrEmpty(bootInfo.Header?.Cmdline))
            {
                details.Cmdline = bootInfo.Header.Cmdline;
                ParseCmdline(details, bootInfo.Header.Cmdline);
            }

            // Boot 镜像信息
            details.BootPageSize = bootInfo.Header?.PageSize ?? 0;
            details.BootHeaderVersion = bootInfo.Header?.HeaderVersion ?? 0;
            details.KernelAddress = bootInfo.Header?.KernelAddr ?? 0;
            details.RamdiskAddress = bootInfo.Header?.RamdiskAddr ?? 0;
            details.BootName = bootInfo.Header?.Name;

            LogDeviceInfo(details);
            return details;
        }

        /// <summary>
        /// 获取属性值
        /// </summary>
        private string GetPropertyValue(Dictionary<string, string> props, string mappingKey)
        {
            if (!PropertyMapping.ContainsKey(mappingKey))
                return null;

            foreach (var propKey in PropertyMapping[mappingKey])
            {
                if (props.ContainsKey(propKey) && !string.IsNullOrEmpty(props[propKey]))
                    return props[propKey];
            }

            return null;
        }

        /// <summary>
        /// 解析 cmdline
        /// </summary>
        private void ParseCmdline(SprdDeviceDetails details, string cmdline)
        {
            var parts = cmdline.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;

                string key = kv[0];
                string value = kv[1];

                switch (key)
                {
                    case "androidboot.hardware":
                        if (string.IsNullOrEmpty(details.Hardware))
                            details.Hardware = value;
                        break;
                    case "androidboot.serialno":
                        details.SerialNo = value;
                        break;
                    case "androidboot.bootloader":
                        details.Bootloader = value;
                        break;
                    case "androidboot.baseband":
                        details.Baseband = value;
                        break;
                    case "androidboot.verifiedbootstate":
                        details.VerifiedBootState = value;
                        break;
                    case "androidboot.slot_suffix":
                        details.SlotSuffix = value;
                        break;
                    case "androidboot.vbmeta.device_state":
                        details.DeviceState = value;
                        break;
                }
            }
        }

        /// <summary>
        /// 输出设备信息日志
        /// </summary>
        private void LogDeviceInfo(SprdDeviceDetails details)
        {
            _log?.Invoke("========== 设备信息 ==========");
            
            if (!string.IsNullOrEmpty(details.Brand))
                _log?.Invoke($"品牌: {details.Brand}");
            if (!string.IsNullOrEmpty(details.Manufacturer))
                _log?.Invoke($"厂商: {details.Manufacturer}");
            if (!string.IsNullOrEmpty(details.Model))
                _log?.Invoke($"型号: {details.Model}");
            if (!string.IsNullOrEmpty(details.MarketName))
                _log?.Invoke($"市场名: {details.MarketName}");
            if (!string.IsNullOrEmpty(details.Device))
                _log?.Invoke($"设备: {details.Device}");
            if (!string.IsNullOrEmpty(details.AndroidVersion))
                _log?.Invoke($"Android: {details.AndroidVersion}");
            if (!string.IsNullOrEmpty(details.SdkVersion))
                _log?.Invoke($"SDK: {details.SdkVersion}");
            if (!string.IsNullOrEmpty(details.SecurityPatch))
                _log?.Invoke($"安全补丁: {details.SecurityPatch}");
            if (!string.IsNullOrEmpty(details.Platform))
                _log?.Invoke($"平台: {details.Platform}");
            if (!string.IsNullOrEmpty(details.Fingerprint))
                _log?.Invoke($"指纹: {details.Fingerprint}");
            
            _log?.Invoke("==============================");
        }
    }

    /// <summary>
    /// 展讯设备详细信息
    /// </summary>
    public class SprdDeviceDetails
    {
        // 基本信息
        public string Brand { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string Name { get; set; }
        public string Device { get; set; }
        public string MarketName { get; set; }

        // 版本信息
        public string AndroidVersion { get; set; }
        public string SdkVersion { get; set; }
        public string BuildId { get; set; }
        public string Incremental { get; set; }
        public string SecurityPatch { get; set; }
        public string BuildDate { get; set; }

        // 平台信息
        public string Platform { get; set; }
        public string Hardware { get; set; }
        public string CpuAbi { get; set; }
        public string Chipname { get; set; }

        // 指纹
        public string Fingerprint { get; set; }
        public string Description { get; set; }

        // 区域
        public string Region { get; set; }

        // 厂商 ROM 版本
        public string VendorRomVersion { get; set; }

        // Cmdline 信息
        public string Cmdline { get; set; }
        public string SerialNo { get; set; }
        public string Bootloader { get; set; }
        public string Baseband { get; set; }
        public string VerifiedBootState { get; set; }
        public string SlotSuffix { get; set; }
        public string DeviceState { get; set; }

        // Boot 镜像信息
        public uint BootPageSize { get; set; }
        public uint BootHeaderVersion { get; set; }
        public uint KernelAddress { get; set; }
        public uint RamdiskAddress { get; set; }
        public string BootName { get; set; }

        // 所有属性
        public Dictionary<string, string> AllProperties { get; set; }

        /// <summary>
        /// 获取格式化的显示名称
        /// </summary>
        public string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(MarketName))
                return MarketName;
            if (!string.IsNullOrEmpty(Model))
                return $"{Brand} {Model}";
            if (!string.IsNullOrEmpty(Device))
                return Device;
            return "Unknown Device";
        }

        /// <summary>
        /// 获取格式化的版本信息
        /// </summary>
        public string GetVersionInfo()
        {
            var parts = new List<string>();
            
            if (!string.IsNullOrEmpty(AndroidVersion))
                parts.Add($"Android {AndroidVersion}");
            if (!string.IsNullOrEmpty(VendorRomVersion))
                parts.Add(VendorRomVersion);
            if (!string.IsNullOrEmpty(SecurityPatch))
                parts.Add($"Patch: {SecurityPatch}");

            return string.Join(" | ", parts);
        }

        public override string ToString()
        {
            return $"{GetDisplayName()} ({GetVersionInfo()})";
        }
    }
}
