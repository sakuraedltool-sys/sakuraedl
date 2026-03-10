// ============================================================================
// SakuraEDL - ADB Helper | ADB 辅助工具
// ============================================================================
// [ZH] ADB 辅助工具 - 调用 ADB 命令实现设备操作
// [EN] ADB Helper - Call ADB commands for device operations
// [JA] ADBヘルパー - ADBコマンドでデバイス操作を実行
// [KO] ADB 헬퍼 - ADB 명령으로 기기 작업 수행
// [RU] Помощник ADB - Вызов команд ADB для операций с устройством
// [ES] Ayudante ADB - Llamar comandos ADB para operaciones del dispositivo
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Fastboot.Common
{
    /// <summary>
    /// ADB 命令执行辅助类
    /// 依赖外部 adb.exe 执行命令
    /// </summary>
    public static class AdbHelper
    {
        // ADB 可执行文件路径
        private static string _adbPath = null;
        
        /// <summary>
        /// 获取 ADB 路径 (优先使用程序目录下的 adb.exe)
        /// </summary>
        public static string GetAdbPath()
        {
            if (_adbPath != null)
                return _adbPath;
            
            // 1. 优先使用程序目录下的 adb.exe
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string localAdb = Path.Combine(appDir, "adb.exe");
            if (File.Exists(localAdb))
            {
                _adbPath = localAdb;
                return _adbPath;
            }
            
            // 2. 尝试 platform-tools 子目录
            string platformTools = Path.Combine(appDir, "platform-tools", "adb.exe");
            if (File.Exists(platformTools))
            {
                _adbPath = platformTools;
                return _adbPath;
            }
            
            // 3. 假设 adb 在系统 PATH 中
            _adbPath = "adb";
            return _adbPath;
        }
        
        /// <summary>
        /// 检查 ADB 是否可用
        /// </summary>
        public static async Task<bool> IsAvailableAsync()
        {
            try
            {
                var result = await ExecuteAsync("version", 5000);
                return result.ExitCode == 0 && result.Output.Contains("Android Debug Bridge");
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// 执行 ADB 命令
        /// </summary>
        /// <param name="arguments">ADB 命令参数</param>
        /// <param name="timeoutMs">超时时间 (毫秒)</param>
        /// <param name="ct">取消令牌</param>
        public static async Task<AdbResult> ExecuteAsync(string arguments, int timeoutMs = 10000, CancellationToken ct = default)
        {
            var result = new AdbResult();
            
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GetAdbPath(),
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                
                using (var process = new Process { StartInfo = psi })
                {
                    var outputBuilder = new System.Text.StringBuilder();
                    var errorBuilder = new System.Text.StringBuilder();
                    
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            outputBuilder.AppendLine(e.Data);
                    };
                    
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            errorBuilder.AppendLine(e.Data);
                    };
                    
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    
                    // 等待进程完成或超时
                    var completed = await Task.Run(() => process.WaitForExit(timeoutMs), ct);
                    
                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        result.ExitCode = -1;
                        result.Error = "命令执行超时";
                        return result;
                    }
                    
                    result.ExitCode = process.ExitCode;
                    result.Output = outputBuilder.ToString().Trim();
                    result.Error = errorBuilder.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.Error = $"执行失败: {ex.Message}";
            }
            
            return result;
        }
        
        #region 快捷方法
        
        /// <summary>
        /// 重启到系统
        /// </summary>
        public static Task<AdbResult> RebootAsync(CancellationToken ct = default)
            => ExecuteAsync("reboot", 10000, ct);
        
        /// <summary>
        /// 重启到 Bootloader (Fastboot)
        /// </summary>
        public static Task<AdbResult> RebootBootloaderAsync(CancellationToken ct = default)
            => ExecuteAsync("reboot bootloader", 10000, ct);
        
        /// <summary>
        /// 重启到 Fastbootd
        /// </summary>
        public static Task<AdbResult> RebootFastbootAsync(CancellationToken ct = default)
            => ExecuteAsync("reboot fastboot", 10000, ct);
        
        /// <summary>
        /// 重启到 Recovery
        /// </summary>
        public static Task<AdbResult> RebootRecoveryAsync(CancellationToken ct = default)
            => ExecuteAsync("reboot recovery", 10000, ct);
        
        /// <summary>
        /// 重启到 EDL 模式 (联想/安卓)
        /// </summary>
        public static Task<AdbResult> RebootEdlAsync(CancellationToken ct = default)
            => ExecuteAsync("reboot edl", 10000, ct);
        
        /// <summary>
        /// 获取设备列表
        /// </summary>
        public static Task<AdbResult> DevicesAsync(CancellationToken ct = default)
            => ExecuteAsync("devices", 5000, ct);
        
        /// <summary>
        /// 获取设备状态
        /// </summary>
        public static Task<AdbResult> GetStateAsync(CancellationToken ct = default)
            => ExecuteAsync("get-state", 5000, ct);
        
        /// <summary>
        /// 执行 shell 命令
        /// </summary>
        public static Task<AdbResult> ShellAsync(string command, int timeoutMs = 30000, CancellationToken ct = default)
            => ExecuteAsync($"shell {command}", timeoutMs, ct);
        
        #endregion
    }
    
    /// <summary>
    /// ADB 命令执行结果
    /// </summary>
    public class AdbResult
    {
        /// <summary>
        /// 退出代码 (0 = 成功)
        /// </summary>
        public int ExitCode { get; set; } = -1;
        
        /// <summary>
        /// 标准输出
        /// </summary>
        public string Output { get; set; } = string.Empty;
        
        /// <summary>
        /// 错误输出
        /// </summary>
        public string Error { get; set; } = string.Empty;
        
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success => ExitCode == 0;
        
        /// <summary>
        /// 获取完整输出 (stdout + stderr)
        /// </summary>
        public string FullOutput => string.IsNullOrEmpty(Error) ? Output : $"{Output}\n{Error}".Trim();
    }
}
