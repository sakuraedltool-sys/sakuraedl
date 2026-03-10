// ============================================================================
// SakuraEDL - Cloud Loader Integration | 云端 Loader 集成
// ============================================================================
// [ZH] 云端 Loader 集成 - 自动匹配云端 EDL Loader 资源
// [EN] Cloud Loader Integration - Auto-match cloud EDL Loader resources
// [JA] クラウドLoader統合 - クラウドEDL Loaderリソースの自動マッチング
// [KO] 클라우드 Loader 통합 - 클라우드 EDL Loader 리소스 자동 매칭
// [RU] Интеграция облачного Loader - Автоматический подбор облачных ресурсов
// [ES] Integración Cloud Loader - Auto-coincidencia de recursos Loader
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Drawing;
using System.Threading.Tasks;
using SakuraEDL.Qualcomm.Database;
using SakuraEDL.Qualcomm.Services;
using SakuraEDL.Qualcomm.UI;

namespace SakuraEDL.Qualcomm.Services
{
    /// <summary>
    /// 云端 Loader 集成帮助类
    /// 在 Form1.cs 中使用此类来实现云端自动匹配
    /// </summary>
    public static class CloudLoaderIntegration
    {
        /// <summary>
        /// 初始化云端服务
        /// 在 Form1 构造函数中调用
        /// </summary>
        public static void Initialize(Action<string> log, Action<string> logDetail)
        {
            var service = CloudLoaderService.Instance;
            service.SetLogger(log, logDetail);
            
            // 配置 (可选)
            // service.ApiBase = "https://api.xiriacg.top/api";  // 生产环境
            // service.EnableCache = true;
            // service.TimeoutSeconds = 15;
        }
        
        /// <summary>
        /// 云端自动匹配连接
        /// 替代原有的 PAK 资源选择方式
        /// </summary>
        /// <param name="controller">高通控制器</param>
        /// <param name="deviceInfo">设备信息 (Sahara 握手后获取)</param>
        /// <param name="storageType">存储类型</param>
        /// <param name="log">日志回调</param>
        /// <returns>连接结果</returns>
        public static async Task<bool> ConnectWithCloudMatchAsync(
            QualcommUIController controller,
            SaharaDeviceInfo deviceInfo,
            string storageType,
            Action<string, Color> log)
        {
            var cloudService = CloudLoaderService.Instance;
            
            // 1. 云端匹配
            log("[云端] 正在匹配 Loader...", Color.Cyan);
            
            var result = await cloudService.MatchLoaderAsync(
                deviceInfo.MsmId,
                deviceInfo.PkHash,
                deviceInfo.OemId,
                storageType
            );
            
            if (result != null && result.Data != null)
            {
                // 2. 匹配成功，使用云端 Loader
                log($"[云端] 匹配成功: {result.Filename}", Color.Green);
                log($"[云端] 厂商: {result.Vendor}, 芯片: {result.Chip}", Color.Blue);
                log($"[云端] 置信度: {result.Confidence}%, 匹配类型: {result.MatchType}", Color.Blue);
                
                // 3. 根据认证类型选择连接方式
                string authMode = result.AuthType?.ToLower() switch
                {
                    "miauth" => "xiaomi",
                    "demacia" => "oneplus",
                    "vip" => "vip",
                    _ => "none"
                };
                
                // 4. 连接设备
                bool success = await controller.ConnectWithLoaderDataAsync(
                    storageType,
                    result.Data,
                    result.Filename,
                    authMode
                );
                
                // 5. 上报设备日志
                cloudService.ReportDeviceLog(
                    deviceInfo.MsmId,
                    deviceInfo.PkHash,
                    deviceInfo.OemId,
                    storageType,
                    success ? "success" : "failed"
                );
                
                return success;
            }
            else
            {
                // 6. 云端无匹配，回退到本地 PAK
                log("[云端] 无匹配，尝试本地资源...", Color.Yellow);
                
                // 上报未匹配
                cloudService.ReportDeviceLog(
                    deviceInfo.MsmId,
                    deviceInfo.PkHash,
                    deviceInfo.OemId,
                    storageType,
                    "not_found"
                );
                
                return await FallbackToLocalPakAsync(controller, deviceInfo, storageType, log);
            }
        }
        
        /// <summary>
        /// 回退到本地 PAK 资源
        /// </summary>
        private static async Task<bool> FallbackToLocalPakAsync(
            QualcommUIController controller,
            SaharaDeviceInfo deviceInfo,
            string storageType,
            Action<string, Color> log)
        {
            // 检查本地 PAK 是否可用
            #pragma warning disable CS0618 // 弃用警告：EdlLoaderDatabase
            if (!EdlLoaderDatabase.IsPakAvailable())
            {
                log("[本地] edl_loaders.pak 不存在", Color.Red);
                return false;
            }
            
            // 尝试按 HW ID (MSM ID) 匹配
            var loaders = EdlLoaderDatabase.GetByChip(deviceInfo.MsmId);
            if (loaders.Length > 0)
            {
                var loader = loaders[0];
                var data = EdlLoaderDatabase.LoadLoader(loader.Id);
                
                if (data != null)
                {
                    log($"[本地] 使用: {loader.Name}", Color.Cyan);
                    return await controller.ConnectWithLoaderDataAsync(
                        storageType,
                        data,
                        loader.Name,
                        loader.AuthMode ?? "none"
                    );
                }
            }
            
            log("[本地] 未找到匹配的 Loader", Color.Red);
            #pragma warning restore CS0618
            return false;
        }
    }
    
    /// <summary>
    /// Sahara 设备信息 (从握手协议获取)
    /// </summary>
    public class SaharaDeviceInfo
    {
        public int SaharaVersion { get; set; }  // Sahara 协议版本 (1/2/3)
        public string MsmId { get; set; }       // 如 "009600E1"
        public string PkHash { get; set; }      // 64 字符
        public string OemId { get; set; }       // 如 "0x0001"
        public string ModelId { get; set; }     // Model ID
        public string HwId { get; set; }        // 完整 HWID
        public string Serial { get; set; }      // 序列号
        public string ChipName { get; set; }    // 芯片名称 (如 SM8550)
        public string Vendor { get; set; }      // 厂商名称 (如 Xiaomi)
        public bool IsUfs { get; set; }
    }
}

/*
================================================================================
                            Form1.cs 集成示例
================================================================================

1. 在 Form1.cs 顶部添加引用：
   using SakuraEDL.Qualcomm.Services;
   using SakuraEDL.Qualcomm.Integration;

2. 在 Form1 构造函数中初始化云端服务：
   
   public Form1()
   {
       InitializeComponent();
       
       // 初始化云端 Loader 服务
       CloudLoaderIntegration.Initialize(
           msg => AppendLog(msg, Color.Blue),
           msg => AppendLog(msg, Color.Gray)
       );
   }

3. 修改连接方法，添加云端自动匹配选项：

   private async Task<bool> ConnectQualcommDeviceAsync()
   {
       // 检查是否启用云端自动匹配
       if (checkbox_CloudMatch.Checked)  // 添加一个复选框控制
       {
           // 先获取设备信息 (Sahara 握手)
           var deviceInfo = await GetSaharaDeviceInfoAsync();
           
           if (deviceInfo != null)
           {
               return await CloudLoaderIntegration.ConnectWithCloudMatchAsync(
                   _qualcommController,
                   deviceInfo,
                   _storageType,
                   (msg, color) => AppendLog(msg, color)
               );
           }
       }
       
       // 原有的 PAK 资源选择逻辑
       return await ConnectWithSelectedLoaderAsync();
   }

4. 或者更简单的方式，直接使用 CloudLoaderService：

   private async Task<bool> ConnectWithAutoMatchAsync()
   {
       var cloud = CloudLoaderService.Instance;
       
       // 获取设备信息后调用
       var result = await cloud.MatchLoaderAsync(
           deviceInfo.MsmId,
           deviceInfo.PkHash,
           deviceInfo.OemId,
           "ufs"
       );
       
       if (result?.Data != null)
       {
           AppendLog($"云端匹配: {result.Filename}", Color.Green);
           return await _qualcommController.ConnectWithLoaderDataAsync(
               "ufs", result.Data, result.Filename, "none");
       }
       
       return false;
   }

================================================================================
                          删除 PAK 资源相关代码
================================================================================

如果完全使用云端匹配，可以删除以下文件/代码：

1. 删除文件:
   - edl_loaders.pak (约 50-100MB)
   
2. 可选保留以下代码作为离线回退:
   - Qualcomm/Database/edl_loader_database.cs (保留元数据，删除 PAK 加载逻辑)
   
3. 修改 Form1.cs:
   - 删除 PAK 可用性检查相关代码
   - 删除 EDL Loader 下拉列表构建代码 (或改为从云端获取列表)
   - 将连接逻辑改为云端优先

================================================================================
*/
