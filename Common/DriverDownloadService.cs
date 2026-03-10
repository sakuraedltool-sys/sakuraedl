// ============================================================================
// SakuraEDL - 驱动下载服务
// Driver Download Service
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Common
{
    /// <summary>
    /// 驱动类型枚举
    /// </summary>
    public enum DriverType
    {
        Qualcomm,
        MediaTek,
        Spreadtrum
    }

    /// <summary>
    /// 下载进度信息
    /// </summary>
    public class DownloadProgress
    {
        public int Percentage { get; set; }           // 进度百分比 0-100
        public long DownloadedBytes { get; set; }     // 已下载字节
        public long TotalBytes { get; set; }          // 总字节数
        public double SpeedBytesPerSec { get; set; }  // 速度 (字节/秒)
        public string SpeedText { get; set; }         // 速度文本 (如 "1.5 MB/s")
        public string ProgressText { get; set; }      // 进度文本 (如 "5.2 / 10.0 MB")
    }

    /// <summary>
    /// 驱动下载服务 - 从云端下载驱动并以管理员权限安装
    /// </summary>
    public class DriverDownloadService
    {
        // 云端驱动下载地址 (主域名)
        private const string BASE_URL = "https://sakuraedl.org";
        
        private static readonly string[] QualcommDriverUrls = {
            $"{BASE_URL}/downloads/qualcomm/qc_driver.exe"
        };
        
        private static readonly string[] MtkDriverUrls = {
            $"{BASE_URL}/downloads/mediatek/mtk_driver.exe"
        };
        
        private static readonly string[] SpdDriverUrls = {
            $"{BASE_URL}/downloads/spreadtrum/spd_driver_v2.exe"
        };

        // 本地驱动文件名
        private const string QC_DRIVER_FILENAME = "qc_driver.exe";
        private const string MTK_DRIVER_FILENAME = "mtk_driver.exe";
        private const string SPD_DRIVER_FILENAME = "spd_driver.exe";

        private readonly Action<string> _logCallback;
        private readonly Action<int> _progressCallback;
        private readonly Action<DownloadProgress> _detailProgressCallback;

        public DriverDownloadService(
            Action<string> logCallback = null, 
            Action<int> progressCallback = null,
            Action<DownloadProgress> detailProgressCallback = null)
        {
            _logCallback = logCallback;
            _progressCallback = progressCallback;
            _detailProgressCallback = detailProgressCallback;
        }

        /// <summary>
        /// 获取驱动名称
        /// </summary>
        public static string GetDriverName(DriverType type)
        {
            switch (type)
            {
                case DriverType.Qualcomm: return "Qualcomm 9008 driver";

                case DriverType.MediaTek: return "MTK USB driver";

                case DriverType.Spreadtrum: return "Spreadtrum USB driver";

                default: return "Unknown driver";
            }
        }

        /// <summary>
        /// 获取本地驱动目录
        /// </summary>
        private string GetDriversDirectory()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string driversDir = Path.Combine(appDir, "drivers");
            if (!Directory.Exists(driversDir))
            {
                Directory.CreateDirectory(driversDir);
            }
            return driversDir;
        }

        /// <summary>
        /// 获取本地驱动路径
        /// </summary>
        public string GetLocalDriverPath(DriverType type)
        {
            string driversDir = GetDriversDirectory();
            string filename;
            switch (type)
            {
                case DriverType.Qualcomm: filename = QC_DRIVER_FILENAME; break;
                case DriverType.MediaTek: filename = MTK_DRIVER_FILENAME; break;
                case DriverType.Spreadtrum: filename = SPD_DRIVER_FILENAME; break;
                default: return null;
            }
            return Path.Combine(driversDir, filename);
        }

        /// <summary>
        /// 检查本地驱动是否存在
        /// </summary>
        public bool IsDriverExists(DriverType type)
        {
            string path = GetLocalDriverPath(type);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        /// <summary>
        /// 获取云端驱动 URL
        /// </summary>
        private string[] GetDriverUrls(DriverType type)
        {
            switch (type)
            {
                case DriverType.Qualcomm: return QualcommDriverUrls;
                case DriverType.MediaTek: return MtkDriverUrls;
                case DriverType.Spreadtrum: return SpdDriverUrls;
                default: return new string[0];
            }
        }

        /// <summary>
        /// 格式化文件大小
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F2} MB";
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }

        /// <summary>
        /// 格式化速度
        /// </summary>
        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:F1} KB/s";
            return $"{bytesPerSec / 1024.0 / 1024.0:F2} MB/s";
        }

        /// <summary>
        /// 下载驱动
        /// </summary>
        public async Task<bool> DownloadDriverAsync(DriverType type, CancellationToken cancellationToken = default)
        {
            string driverName = GetDriverName(type);
            string localPath = GetLocalDriverPath(type);
            string[] urls = GetDriverUrls(type);

            if (urls.Length == 0)
            {
                Log($"[driver] {driverName} download address not configured");
                return false;
            }

            Log($"[driver] is downloading {driverName}...");

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromMinutes(10); // 10分钟超时

                foreach (var url in urls)
                {
                    try
                    {
                        // 不显示详细 URL，只在调试时使用
                        // Log($"[驱动] 正在从 {url} 下载...");

                        using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                // 静默尝试下一个 URL
                                continue;
                            }

                            var totalBytes = response.Content.Headers.ContentLength ?? -1;
                            var totalMB = totalBytes > 0 ? totalBytes / 1024.0 / 1024.0 : 0;

                            Log($"[Driver] File Size: {totalMB:F2} MB");

                            // 确保目录存在
                            string dir = Path.GetDirectoryName(localPath);
                            if (!Directory.Exists(dir))
                                Directory.CreateDirectory(dir);

                            // 下载到临时文件
                            string tempPath = localPath + ".tmp";

                            // 速度计算变量
                            var stopwatch = Stopwatch.StartNew();
                            long lastSpeedBytes = 0;
                            DateTime lastSpeedTime = DateTime.Now;

                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                            {
                                var buffer = new byte[65536]; // 64KB 缓冲区
                                long downloadedBytes = 0;
                                int bytesRead;
                                int lastProgress = -1;
                                double currentSpeed = 0;

                                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                                    downloadedBytes += bytesRead;

                                    // 计算速度 (每500ms更新一次)
                                    var now = DateTime.Now;
                                    var timeDiff = (now - lastSpeedTime).TotalSeconds;
                                    if (timeDiff >= 0.5)
                                    {
                                        var bytesDiff = downloadedBytes - lastSpeedBytes;
                                        currentSpeed = bytesDiff / timeDiff;
                                        lastSpeedBytes = downloadedBytes;
                                        lastSpeedTime = now;
                                    }

                                    if (totalBytes > 0)
                                    {
                                        int progress = (int)(downloadedBytes * 100 / totalBytes);
                                        if (progress != lastProgress)
                                        {
                                            lastProgress = progress;
                                            _progressCallback?.Invoke(progress);

                                            // 发送详细进度
                                            _detailProgressCallback?.Invoke(new DownloadProgress
                                            {
                                                Percentage = progress,
                                                DownloadedBytes = downloadedBytes,
                                                TotalBytes = totalBytes,
                                                SpeedBytesPerSec = currentSpeed,
                                                SpeedText = FormatSpeed(currentSpeed),
                                                ProgressText = $"{FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes)}"
                                            });
                                        }
                                    }
                                }
                            }

                            stopwatch.Stop();
                            var avgSpeed = totalBytes / stopwatch.Elapsed.TotalSeconds;

                            // 下载完成，重命名
                            if (File.Exists(localPath))
                                File.Delete(localPath);
                            File.Move(tempPath, localPath);

                            Log($"[driver] {driverName} Download complete (Average speed: {FormatSpeed(avgSpeed)})");
                            _progressCallback?.Invoke(100);
                            
                            // 发送完成进度
                            _detailProgressCallback?.Invoke(new DownloadProgress
                            {
                                Percentage = 100,
                                DownloadedBytes = totalBytes,
                                TotalBytes = totalBytes,
                                SpeedBytesPerSec = avgSpeed,
                                SpeedText = FormatSpeed(avgSpeed),
                                ProgressText = $"{FormatBytes(totalBytes)} / {FormatBytes(totalBytes)}"
                            });
                            
                            return true;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log($"[Driver] Download canceled");
                        // 清理临时文件
                        string tempPath = localPath + ".tmp";
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"[Driver] Download failed: {ex.Message}");
                        // 清理临时文件
                        string tempPath = localPath + ".tmp";
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                }
            }

            Log($"[driver] {driverName} All download sources failed");
            return false;
        }

        /// <summary>
        /// 以管理员权限运行驱动安装程序
        /// </summary>
        public bool RunDriverAsAdmin(DriverType type)
        {
            string driverPath = GetLocalDriverPath(type);
            string driverName = GetDriverName(type);

            if (!File.Exists(driverPath))
            {
                Log($"[driver] {driverName} file does not exist: {driverPath}");
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = driverPath,
                    UseShellExecute = true,
                    Verb = "runas" // 请求管理员权限
                };

                Process.Start(startInfo);
                Log($"[driver] {driverName} installer has started");
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) // 用户取消了 UAC 提示
                {
                    Log($"[Driver] User canceled administrator privilege request");
                }
                else
                {
                    Log($"[Driver] Startup failed: {ex.Message}");
                }
                return false;
            }
            catch (Exception ex)
            {
                Log($"[Driver] Startup failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 下载并安装驱动 (如果本地不存在则下载)
        /// </summary>
        public async Task<bool> DownloadAndInstallAsync(DriverType type, CancellationToken cancellationToken = default)
        {
            string driverName = GetDriverName(type);

            // 检查本地是否已有驱动
            if (IsDriverExists(type))

            {
                Log($"[Driver] {driverName} already exists, install directly");

                return RunDriverAsAdmin(type);

            }

            // Download driver

            bool downloaded = await DownloadDriverAsync(type, cancellationToken);

            if (!downloaded)

            {
                Log($"[Driver] {driverName} download failed");

                return false;

            }

            // 安装驱动
            return RunDriverAsAdmin(type);
        }

        private void Log(string message)
        {
            _logCallback?.Invoke(message);
        }
    }
}
