// ============================================================================
// SakuraEDL - Huawei/Honor Support | 华为/荣耀支持
// ============================================================================
// [ZH] 华为/荣耀设备支持 - FRP 解锁、设备信息、Bootloader 操作
// [EN] Huawei/Honor Device Support - FRP unlock, device info, Bootloader ops
// [JA] Huawei/Honorデバイスサポート - FRPアンロック、デバイス情報
// [KO] Huawei/Honor 기기 지원 - FRP 해제, 기기 정보, 부트로더 작업
// [RU] Поддержка Huawei/Honor - Разблокировка FRP, информация, Bootloader
// [ES] Soporte Huawei/Honor - Desbloqueo FRP, info del dispositivo
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SakuraEDL.Fastboot.Protocol;
using SakuraEDL.Fastboot.Services;

namespace SakuraEDL.Fastboot.Vendor
{
    /// <summary>
    /// 华为/荣耀设备信息
    /// </summary>
    public class HuaweiHonorDeviceInfo
    {
        /// <summary>
        /// 序列号
        /// </summary>
        public string Serial { get; set; }

        /// <summary>
        /// 产品型号
        /// </summary>
        public string ProductModel { get; set; }

        /// <summary>
        /// 设备型号
        /// </summary>
        public string DeviceModel { get; set; }

        /// <summary>
        /// 固件构建号
        /// </summary>
        public string BuildNumber { get; set; }

        /// <summary>
        /// 软件信息
        /// </summary>
        public string SoftwareInfo { get; set; }

        /// <summary>
        /// 系统版本
        /// </summary>
        public string SystemVersion { get; set; }

        /// <summary>
        /// 基础版本
        /// </summary>
        public string BaseVersion { get; set; }

        /// <summary>
        /// 定制版本
        /// </summary>
        public string CustomVersion { get; set; }

        /// <summary>
        /// 预装版本
        /// </summary>
        public string PreloadVersion { get; set; }

        /// <summary>
        /// IMEI1
        /// </summary>
        public string Imei1 { get; set; }

        /// <summary>
        /// IMEI2
        /// </summary>
        public string Imei2 { get; set; }

        /// <summary>
        /// MEID
        /// </summary>
        public string Meid { get; set; }

        /// <summary>
        /// Bootloader 锁定状态
        /// </summary>
        public string BootloaderLockStatus { get; set; }

        /// <summary>
        /// 是否已解锁
        /// </summary>
        public bool IsUnlocked => BootloaderLockStatus?.ToUpper().Contains("UNLOCK") == true;

        /// <summary>
        /// 销售地区
        /// </summary>
        public string VendorCountry { get; set; }

        /// <summary>
        /// 电池信息
        /// </summary>
        public string BatteryInfo { get; set; }

        /// <summary>
        /// 硬件密钥版本
        /// </summary>
        public string HardwareKeyVersion { get; set; }

        /// <summary>
        /// 救援版本
        /// </summary>
        public string RescueVersion { get; set; }

        /// <summary>
        /// 系统更新状态
        /// </summary>
        public string SystemUpdateState { get; set; }

        /// <summary>
        /// 是否为华为设备
        /// </summary>
        public bool IsHuaweiDevice { get; set; }

        /// <summary>
        /// 是否为荣耀设备
        /// </summary>
        public bool IsHonorDevice { get; set; }

        /// <summary>
        /// 原始 PSID 响应
        /// </summary>
        public string RawPsidResponse { get; set; }

        /// <summary>
        /// 原始 BootInfo 响应
        /// </summary>
        public string RawBootInfoResponse { get; set; }

        public override string ToString()
        {
            return $"{ProductModel ?? DeviceModel ?? "Unknown"} ({Serial})";
        }
    }

    /// <summary>
    /// 华为/荣耀 Fastboot 支持
    /// 基于 HonorInfoTool 逆向分析实现
    /// 
    /// 支持功能：
    /// - 读取设备详细信息 (PSID, 型号, 固件版本, IMEI等)
    /// - FRP 解锁
    /// - 读取 OEM 信息
    /// </summary>
    public class HuaweiHonorSupport
    {
        private readonly FastbootNativeService _service;
        private readonly Action<string> _log;

        #region OEM 命令定义

        /// <summary>
        /// 获取 PSID (包含 IMEI/MEID 等)
        /// </summary>
        public const string OEM_GET_PSID = "get-psid";

        /// <summary>
        /// 获取产品型号
        /// </summary>
        public const string OEM_GET_PRODUCT_MODEL = "get-product-model";

        /// <summary>
        /// 获取构建号
        /// </summary>
        public const string OEM_GET_BUILD_NUMBER = "get-build-number";

        /// <summary>
        /// 获取启动信息 (Bootloader 锁定状态)
        /// </summary>
        public const string OEM_GET_BOOTINFO = "get-bootinfo";

        /// <summary>
        /// 电池检查
        /// </summary>
        public const string OEM_BATTERY_CHECK = "battery_present_check";

        /// <summary>
        /// 读取系统版本
        /// </summary>
        public const string OEM_READ_SYSTEM_VERSION = "oeminforead-SYSTEM_VERSION";

        /// <summary>
        /// 读取基础版本
        /// </summary>
        public const string OEM_READ_BASE_VERSION = "oeminforead-BASE_VERSION";

        /// <summary>
        /// 读取定制版本
        /// </summary>
        public const string OEM_READ_CUSTOM_VERSION = "oeminforead-CUSTOM_VERSION";

        /// <summary>
        /// 读取预装版本
        /// </summary>
        public const string OEM_READ_PRELOAD_VERSION = "oeminforead-PRELOAD_VERSION";

        /// <summary>
        /// 获取硬件密钥版本
        /// </summary>
        public const string OEM_GET_KEY_VERSION = "get_key_version";

        /// <summary>
        /// FRP 解锁
        /// </summary>
        public const string OEM_FRP_UNLOCK = "frp-unlock";

        /// <summary>
        /// 重启到 EDL 模式
        /// </summary>
        public const string OEM_REBOOT_EDL = "reboot-edl";

        /// <summary>
        /// 获取 Device ID (用于解锁码计算)
        /// </summary>
        public const string OEM_GET_DEVICE_ID = "get-device-id";

        /// <summary>
        /// 解锁 Bootloader (需要解锁码)
        /// </summary>
        public const string OEM_UNLOCK = "unlock";

        /// <summary>
        /// 重新锁定 Bootloader
        /// </summary>
        public const string OEM_RELOCK = "relock";

        #endregion

        #region GetVar 变量定义

        /// <summary>
        /// 设备型号
        /// </summary>
        public const string VAR_DEVICE_MODEL = "devicemodel";

        /// <summary>
        /// 销售地区
        /// </summary>
        public const string VAR_VENDOR_COUNTRY = "vendorcountry";

        /// <summary>
        /// 救援模式手机信息
        /// </summary>
        public const string VAR_RESCUE_PHONEINFO = "rescue_phoneinfo";

        /// <summary>
        /// 救援版本
        /// </summary>
        public const string VAR_RESCUE_VERSION = "rescue_version";

        /// <summary>
        /// 系统更新状态
        /// </summary>
        public const string VAR_SYSTEM_UPDATE_STATE = "system_update_state";

        #endregion

        public HuaweiHonorSupport(FastbootNativeService service, Action<string> log = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _log = log ?? (msg => { });
        }

        /// <summary>
        /// 检测是否为华为/荣耀设备
        /// </summary>
        public async Task<bool> IsHuaweiHonorDeviceAsync(CancellationToken ct = default)
        {
            try
            {
                // 尝试执行华为特有命令
                string psid = await _service.ExecuteOemCommandAsync(OEM_GET_PSID, ct);
                if (!string.IsNullOrEmpty(psid) && psid.Contains("(bootloader)"))
                {
                    return true;
                }

                // 检查产品型号
                string model = await _service.ExecuteOemCommandAsync(OEM_GET_PRODUCT_MODEL, ct);
                if (!string.IsNullOrEmpty(model) && model.Contains("(bootloader)"))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读取完整设备信息
        /// </summary>
        public async Task<HuaweiHonorDeviceInfo> ReadDeviceInfoAsync(CancellationToken ct = default)
        {
            var info = new HuaweiHonorDeviceInfo();

            _log("[华为/荣耀] 正在读取设备信息...");

            // 并行读取多个 OEM 命令结果
            var tasks = new Dictionary<string, Task<string>>
            {
                ["psid"] = ExecuteOemSafeAsync(OEM_GET_PSID, ct),
                ["model"] = ExecuteOemSafeAsync(OEM_GET_PRODUCT_MODEL, ct),
                ["build"] = ExecuteOemSafeAsync(OEM_GET_BUILD_NUMBER, ct),
                ["bootinfo"] = ExecuteOemSafeAsync(OEM_GET_BOOTINFO, ct),
                ["battery"] = ExecuteOemSafeAsync(OEM_BATTERY_CHECK, ct),
                ["sysver"] = ExecuteOemSafeAsync(OEM_READ_SYSTEM_VERSION, ct),
                ["basever"] = ExecuteOemSafeAsync(OEM_READ_BASE_VERSION, ct),
                ["cusver"] = ExecuteOemSafeAsync(OEM_READ_CUSTOM_VERSION, ct),
                ["prever"] = ExecuteOemSafeAsync(OEM_READ_PRELOAD_VERSION, ct),
                ["hwkey"] = ExecuteOemSafeAsync(OEM_GET_KEY_VERSION, ct),
            };

            var varTasks = new Dictionary<string, Task<string>>
            {
                ["devicemodel"] = GetVariableSafeAsync(VAR_DEVICE_MODEL, ct),
                ["country"] = GetVariableSafeAsync(VAR_VENDOR_COUNTRY, ct),
                ["phoneinfo"] = GetVariableSafeAsync(VAR_RESCUE_PHONEINFO, ct),
                ["rescuever"] = GetVariableSafeAsync(VAR_RESCUE_VERSION, ct),
                ["updatestate"] = GetVariableSafeAsync(VAR_SYSTEM_UPDATE_STATE, ct),
            };

            // 等待所有任务完成
            await Task.WhenAll(tasks.Values);
            await Task.WhenAll(varTasks.Values);

            // 解析 PSID (包含 IMEI/MEID)
            string psid = await tasks["psid"];
            if (!string.IsNullOrEmpty(psid))
            {
                info.RawPsidResponse = psid;
                ParsePsidResponse(psid, info);
            }

            // 解析产品型号
            string model = await tasks["model"];
            info.ProductModel = ParseBootloaderValue(model);

            // 解析设备型号
            string deviceModel = await varTasks["devicemodel"];
            info.DeviceModel = ParseGetvarValue(deviceModel, VAR_DEVICE_MODEL);

            // 解析构建号
            string build = await tasks["build"];
            info.BuildNumber = ParseBootloaderValue(build);

            // 解析软件信息
            string phoneInfo = await varTasks["phoneinfo"];
            info.SoftwareInfo = ParseGetvarValue(phoneInfo, VAR_RESCUE_PHONEINFO);

            // 解析启动信息 (Bootloader 锁定状态)
            string bootInfo = await tasks["bootinfo"];
            if (!string.IsNullOrEmpty(bootInfo))
            {
                info.RawBootInfoResponse = bootInfo;
                info.BootloaderLockStatus = ParseBootloaderValue(bootInfo);
            }

            // 解析电池信息
            string battery = await tasks["battery"];
            info.BatteryInfo = ParseBootloaderValue(battery);

            // 解析版本信息
            info.SystemVersion = ParseBootloaderValueV2(await tasks["sysver"]);
            info.BaseVersion = ParseBootloaderValue(await tasks["basever"]);
            info.CustomVersion = ParseBootloaderValue(await tasks["cusver"]);
            info.PreloadVersion = ParseBootloaderValue(await tasks["prever"]);

            // 解析销售地区
            string country = await varTasks["country"];
            info.VendorCountry = ParseGetvarValue(country, VAR_VENDOR_COUNTRY);

            // 解析硬件密钥版本
            string hwKey = await tasks["hwkey"];
            info.HardwareKeyVersion = ParseBootloaderValueV2(hwKey);

            // 解析救援版本
            string rescueVer = await varTasks["rescuever"];
            info.RescueVersion = ParseGetvarValue(rescueVer, VAR_RESCUE_VERSION);

            // 解析系统更新状态
            string updateState = await varTasks["updatestate"];
            info.SystemUpdateState = ParseGetvarValue(updateState, VAR_SYSTEM_UPDATE_STATE);

            // 设置序列号
            info.Serial = _service.CurrentSerial;

            // 检测设备品牌
            DetectDeviceBrand(info);

            _log($"[华为/荣耀] 设备: {info.ProductModel ?? info.DeviceModel ?? "未知"}");
            if (!string.IsNullOrEmpty(info.Imei1))
                _log($"[华为/荣耀] IMEI1: {info.Imei1}");
            if (!string.IsNullOrEmpty(info.BootloaderLockStatus))
                _log($"[华为/荣耀] BL状态: {info.BootloaderLockStatus}");

            return info;
        }

        /// <summary>
        /// FRP 解锁
        /// </summary>
        /// <param name="frpKey">FRP 密钥 (通常为设备序列号)</param>
        public async Task<bool> UnlockFrpAsync(string frpKey, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(frpKey))
            {
                _log("[华为/荣耀] FRP 密钥不能为空");
                return false;
            }

            _log($"[华为/荣耀] 正在执行 FRP 解锁 (密钥: {frpKey})...");

            try
            {
                string result = await _service.ExecuteOemCommandAsync($"{OEM_FRP_UNLOCK} {frpKey}", ct);
                
                if (result == null || result.ToLower().Contains("okay"))
                {
                    _log("[华为/荣耀] FRP 解锁命令已发送");
                    return true;
                }
                
                _log($"[华为/荣耀] FRP 解锁响应: {result}");
                return !result.ToLower().Contains("fail");
            }
            catch (Exception ex)
            {
                _log($"[华为/荣耀] FRP 解锁失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取 Device ID (用于解锁码计算)
        /// </summary>
        public async Task<string> GetDeviceIdAsync(CancellationToken ct = default)
        {
            try
            {
                string result = await _service.ExecuteOemCommandAsync(OEM_GET_DEVICE_ID, ct);
                return ParseBootloaderValue(result);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 使用解锁码解锁 Bootloader
        /// </summary>
        public async Task<bool> UnlockBootloaderWithCodeAsync(string unlockCode, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(unlockCode))
            {
                _log("[华为/荣耀] 解锁码不能为空");
                return false;
            }

            _log($"[华为/荣耀] 正在解锁 Bootloader...");

            try
            {
                string result = await _service.ExecuteOemCommandAsync($"{OEM_UNLOCK} {unlockCode}", ct);
                
                if (result == null || result.ToLower().Contains("okay"))
                {
                    _log("[华为/荣耀] Bootloader 解锁成功");
                    return true;
                }
                
                _log($"[华为/荣耀] Bootloader 解锁响应: {result}");
                return !result.ToLower().Contains("fail");
            }
            catch (Exception ex)
            {
                _log($"[华为/荣耀] Bootloader 解锁失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重新锁定 Bootloader
        /// </summary>
        public async Task<bool> RelockBootloaderAsync(CancellationToken ct = default)
        {
            _log("[华为/荣耀] 正在锁定 Bootloader...");

            try
            {
                string result = await _service.ExecuteOemCommandAsync(OEM_RELOCK, ct);
                
                if (result == null || result.ToLower().Contains("okay"))
                {
                    _log("[华为/荣耀] Bootloader 锁定成功");
                    return true;
                }
                
                _log($"[华为/荣耀] Bootloader 锁定响应: {result}");
                return !result.ToLower().Contains("fail");
            }
            catch (Exception ex)
            {
                _log($"[华为/荣耀] Bootloader 锁定失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 重启到 EDL 模式
        /// </summary>
        public async Task<bool> RebootToEdlAsync(CancellationToken ct = default)
        {
            _log("[华为/荣耀] 正在重启到 EDL 模式...");

            try
            {
                string result = await _service.ExecuteOemCommandAsync(OEM_REBOOT_EDL, ct);
                return result == null || !result.ToLower().Contains("fail");
            }
            catch (Exception ex)
            {
                _log($"[华为/荣耀] 重启到 EDL 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 读取 OEM 信息
        /// </summary>
        public async Task<string> ReadOemInfoAsync(string infoName, CancellationToken ct = default)
        {
            try
            {
                string result = await _service.ExecuteOemCommandAsync($"oeminforead-{infoName}", ct);
                return ParseBootloaderValue(result) ?? ParseBootloaderValueV2(result);
            }
            catch
            {
                return null;
            }
        }

        #region 解析辅助方法

        /// <summary>
        /// 安全执行 OEM 命令
        /// </summary>
        private async Task<string> ExecuteOemSafeAsync(string command, CancellationToken ct)
        {
            try
            {
                return await _service.ExecuteOemCommandAsync(command, ct);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 安全获取变量
        /// </summary>
        private async Task<string> GetVariableSafeAsync(string name, CancellationToken ct)
        {
            try
            {
                return await _service.GetVariableAsync(name, ct);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析 PSID 响应 (包含 IMEI/MEID)
        /// 格式: (bootloader) IMEI:xxxx\r\n(bootloader) IMEI1:xxxx\r\n(bootloader) MEID:xxxx
        /// </summary>
        private void ParsePsidResponse(string psid, HuaweiHonorDeviceInfo info)
        {
            if (string.IsNullOrEmpty(psid)) return;

            string lower = psid.ToLower();
            
            // 检查是否包含 okay 和 (bootloader)
            if (!lower.Contains("okay") || !lower.Contains("(bootloader)")) return;

            var parts = psid.Split(new[] { "(bootloader)" }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var part in parts)
            {
                string line = part.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // 解析 IMEI
                if (line.ToLower().StartsWith("imei:"))
                {
                    string value = ExtractValueFromLine(line, "imei:");
                    if (!string.IsNullOrEmpty(value))
                        info.Imei1 = value;
                }
                // 解析 IMEI1 (第二个 IMEI)
                else if (line.ToLower().StartsWith("imei1:"))
                {
                    string value = ExtractValueFromLine(line, "imei1:");
                    if (!string.IsNullOrEmpty(value))
                        info.Imei2 = value;
                }
                // 解析 MEID
                else if (line.ToLower().StartsWith("meid:"))
                {
                    string value = ExtractValueFromLine(line, "meid:");
                    if (!string.IsNullOrEmpty(value))
                        info.Meid = value;
                }
            }
        }

        /// <summary>
        /// 从行中提取值
        /// </summary>
        private string ExtractValueFromLine(string line, string prefix)
        {
            if (string.IsNullOrEmpty(line)) return null;
            
            int idx = line.ToLower().IndexOf(prefix.ToLower());
            if (idx < 0) return null;

            string value = line.Substring(idx + prefix.Length);
            
            // 截取到换行符
            int newlineIdx = value.IndexOf("\r\n");
            if (newlineIdx > 0)
                value = value.Substring(0, newlineIdx);
            
            return value.Trim().ToUpper();
        }

        /// <summary>
        /// 解析 Bootloader 响应格式 1
        /// 格式: ...\r\n(bootloader)value\r\n
        /// </summary>
        private string ParseBootloaderValue(string response)
        {
            if (string.IsNullOrEmpty(response)) return null;

            string lower = response.ToLower();
            if (!lower.Contains("okay") || !lower.Contains("...\r\n(bootloader)")) return null;

            var parts = response.Split(new[] { "...\r\n(bootloader)" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Contains("\r\n"))
                {
                    string value = part.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                    value = value.Trim().Replace(":", "").ToUpper();
                    if (!string.IsNullOrEmpty(value) && !value.ToLower().Contains("okay"))
                        return value;
                }
            }

            return null;
        }

        /// <summary>
        /// 解析 Bootloader 响应格式 2
        /// 格式: (bootloader) :value\r\n
        /// </summary>
        private string ParseBootloaderValueV2(string response)
        {
            if (string.IsNullOrEmpty(response)) return null;

            string lower = response.ToLower();
            if (!lower.Contains("okay") || !lower.Contains("(bootloader) :")) return null;

            string clean = response.Replace("(bootloader) :", "");
            var lines = clean.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                string value = line.Trim().ToUpper();
                if (!string.IsNullOrEmpty(value) && !value.ToLower().Contains("okay") && !value.ToLower().Contains("finished"))
                    return value;
            }

            return null;
        }

        /// <summary>
        /// 解析 GetVar 响应
        /// 格式: varname:value\r\n
        /// </summary>
        private string ParseGetvarValue(string response, string varName)
        {
            if (string.IsNullOrEmpty(response)) return null;

            string lower = response.ToLower();
            
            // 检查是否失败或不存在
            if (lower.Contains("failed") || lower.Contains("not exist") || lower.Contains("not found"))
                return null;

            // 移除变量名前缀
            string prefix = $"{varName}:".ToLower();
            if (lower.Contains(prefix))
            {
                int idx = lower.IndexOf(prefix);
                string value = response.Substring(idx + prefix.Length);
                
                // 截取到换行符
                int newlineIdx = value.IndexOf("\r\n");
                if (newlineIdx > 0)
                    value = value.Substring(0, newlineIdx);
                
                return value.Trim().ToUpper();
            }

            return null;
        }

        /// <summary>
        /// 检测设备品牌
        /// </summary>
        private void DetectDeviceBrand(HuaweiHonorDeviceInfo info)
        {
            string combined = $"{info.ProductModel} {info.DeviceModel} {info.SoftwareInfo}".ToLower();
            
            info.IsHonorDevice = combined.Contains("honor") || 
                                 combined.Contains("hra-") || 
                                 combined.Contains("any-") ||
                                 combined.Contains("dra-") ||
                                 combined.Contains("jat-") ||
                                 combined.Contains("lld-") ||
                                 combined.Contains("bkk-") ||
                                 combined.Contains("pct-") ||
                                 combined.Contains("stk-");
            
            info.IsHuaweiDevice = !info.IsHonorDevice && (
                                  combined.Contains("huawei") ||
                                  combined.Contains("hwa-") ||
                                  combined.Contains("vog-") ||
                                  combined.Contains("ele-") ||
                                  combined.Contains("mar-") ||
                                  combined.Contains("ana-") ||
                                  combined.Contains("nop-") ||
                                  combined.Contains("tas-") ||
                                  combined.Contains("was-"));
        }

        #endregion
    }
}
