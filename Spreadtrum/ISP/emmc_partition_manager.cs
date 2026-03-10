// ============================================================================
// SakuraEDL - eMMC Partition Manager | eMMC 分区管理器
// ============================================================================
// [ZH] eMMC 分区管理 - 提供 eMMC 分区的读写和管理功能
// [EN] eMMC Partition Manager - Provide read/write and management of eMMC
// [JA] eMMCパーティション管理 - eMMCパーティションの読み書きと管理
// [KO] eMMC 파티션 관리자 - eMMC 파티션 읽기/쓰기 및 관리
// [RU] Менеджер разделов eMMC - Чтение/запись и управление разделами eMMC
// [ES] Gestor de particiones eMMC - Lectura/escritura y gestión de eMMC
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Spreadtrum.ISP
{
    /// <summary>
    /// 检测到的 USB 存储设备信息
    /// </summary>
    public class DetectedUsbStorage
    {
        public string DevicePath { get; set; }          // \\.\PhysicalDriveX
        public string FriendlyName { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public long Size { get; set; }
        public bool IsRemovable { get; set; }
        public string InterfaceType { get; set; }       // USB, SCSI, etc.
        public string PnpDeviceId { get; set; }
        public int DeviceNumber { get; set; }

        public override string ToString()
        {
            string sizeStr = Size > 0 ? $"{Size / 1024 / 1024} MB" : "Unknown";
            return $"{FriendlyName} ({Model}) - {sizeStr}";
        }
    }

    /// <summary>
    /// 分区操作结果
    /// </summary>
    public class PartitionOperationResult
    {
        public bool Success { get; set; }
        public string PartitionName { get; set; }
        public string Operation { get; set; }
        public long BytesTransferred { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// eMMC 分区管理器
    /// 提供分区级别的读写操作
    /// </summary>
    public class EmmcPartitionManager : IDisposable
    {
        #region Fields

        private EmmcDevice _device;
        private EmmcGptParser _gpt;
        private bool _disposed;

        #endregion

        #region Properties

        /// <summary>
        /// eMMC 设备
        /// </summary>
        public EmmcDevice Device => _device;

        /// <summary>
        /// GPT 解析器
        /// </summary>
        public EmmcGptParser Gpt => _gpt;

        /// <summary>
        /// 分区列表
        /// </summary>
        public List<EmmcPartitionInfo> Partitions => _gpt?.Partitions ?? new List<EmmcPartitionInfo>();

        /// <summary>
        /// 设备是否就绪
        /// </summary>
        public bool IsReady => _device?.IsOpen ?? false;

        #endregion

        #region Events

        public event Action<string> OnLog;
        public event Action<long, long> OnProgress;

        #endregion

        #region Constructor

        public EmmcPartitionManager()
        {
            _device = new EmmcDevice();
            _gpt = new EmmcGptParser();

            _device.OnLog += msg => OnLog?.Invoke(msg);
            _device.OnProgress += (cur, total) => OnProgress?.Invoke(cur, total);
        }

        #endregion

        #region Device Detection

        /// <summary>
        /// 检测 USB 存储设备
        /// </summary>
        public static List<DetectedUsbStorage> DetectUsbStorageDevices()
        {
            var devices = new List<DetectedUsbStorage>();

            try
            {
                // 查询物理磁盘
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_DiskDrive WHERE InterfaceType='USB'"))
                {
                    foreach (ManagementObject disk in searcher.Get())
                    {
                        var device = new DetectedUsbStorage
                        {
                            DevicePath = disk["DeviceID"]?.ToString(),
                            FriendlyName = disk["Caption"]?.ToString() ?? "Unknown Device",
                            Manufacturer = disk["Manufacturer"]?.ToString(),
                            Model = disk["Model"]?.ToString(),
                            SerialNumber = disk["SerialNumber"]?.ToString(),
                            Size = Convert.ToInt64(disk["Size"] ?? 0),
                            InterfaceType = disk["InterfaceType"]?.ToString(),
                            PnpDeviceId = disk["PNPDeviceID"]?.ToString(),
                            IsRemovable = true
                        };

                        // 提取设备编号
                        if (!string.IsNullOrEmpty(device.DevicePath))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(
                                device.DevicePath, @"PHYSICALDRIVE(\d+)", 
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                device.DeviceNumber = int.Parse(match.Groups[1].Value);
                            }
                        }

                        devices.Add(device);
                    }
                }

                // 也查询 SCSI 设备 (某些 USB 设备被识别为 SCSI)
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_DiskDrive WHERE InterfaceType='SCSI'"))
                {
                    foreach (ManagementObject disk in searcher.Get())
                    {
                        var pnpId = disk["PNPDeviceID"]?.ToString() ?? "";
                        
                        // 只添加 USB 相关的 SCSI 设备
                        if (pnpId.Contains("USB") || pnpId.Contains("USBSTOR"))
                        {
                            var device = new DetectedUsbStorage
                            {
                                DevicePath = disk["DeviceID"]?.ToString(),
                                FriendlyName = disk["Caption"]?.ToString() ?? "Unknown Device",
                                Manufacturer = disk["Manufacturer"]?.ToString(),
                                Model = disk["Model"]?.ToString(),
                                SerialNumber = disk["SerialNumber"]?.ToString(),
                                Size = Convert.ToInt64(disk["Size"] ?? 0),
                                InterfaceType = "USB",
                                PnpDeviceId = pnpId,
                                IsRemovable = true
                            };

                            if (!string.IsNullOrEmpty(device.DevicePath))
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(
                                    device.DevicePath, @"PHYSICALDRIVE(\d+)",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (match.Success)
                                {
                                    device.DeviceNumber = int.Parse(match.Groups[1].Value);
                                }
                            }

                            // 避免重复
                            if (!devices.Any(d => d.DevicePath == device.DevicePath))
                            {
                                devices.Add(device);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"检测设备失败: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// 检测 Spreadtrum/Unisoc ISP 设备
        /// </summary>
        public static DetectedUsbStorage DetectSprdIspDevice()
        {
            var devices = DetectUsbStorageDevices();

            // 查找 Spreadtrum 相关设备
            // VID/PID: 1782:4D00 或类似
            foreach (var device in devices)
            {
                if (device.PnpDeviceId != null)
                {
                    // 检查 Spreadtrum VID
                    if (device.PnpDeviceId.Contains("VID_1782") ||
                        device.PnpDeviceId.Contains("VID_05C6") ||
                        (device.Model != null && device.Model.ToUpper().Contains("SPRD")) ||
                        (device.Model != null && device.Model.ToUpper().Contains("SPREADTRUM")) ||
                        (device.Model != null && device.Model.ToUpper().Contains("UNISOC")))
                    {
                        return device;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 等待设备连接
        /// </summary>
        public static async Task<DetectedUsbStorage> WaitForDeviceAsync(
            int timeoutSeconds = 60, 
            CancellationToken ct = default)
        {
            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
            {
                if (ct.IsCancellationRequested)
                    return null;

                var device = DetectSprdIspDevice();
                if (device != null)
                    return device;

                await Task.Delay(500, ct);
            }

            return null;
        }

        #endregion

        #region Initialize

        /// <summary>
        /// 打开设备并初始化
        /// </summary>
        public bool Open(string devicePath)
        {
            if (!_device.Open(devicePath))
                return false;

            // 解析 GPT
            _gpt.SectorSize = _device.SectorSize;
            if (!_gpt.ParseFromDevice(_device))
            {
                Log("警告: 无法解析 GPT 分区表");
                // 不关闭设备，允许原始访问
            }
            else
            {
                Log($"检测到 {_gpt.Partitions.Count} 个分区");
            }

            return true;
        }

        /// <summary>
        /// 关闭设备
        /// </summary>
        public void Close()
        {
            _device?.Close();
        }

        #endregion

        #region Partition Operations

        /// <summary>
        /// 读取分区到文件
        /// </summary>
        public async Task<PartitionOperationResult> ReadPartitionAsync(
            string partitionName, 
            string outputPath,
            CancellationToken ct = default)
        {
            var result = new PartitionOperationResult
            {
                PartitionName = partitionName,
                Operation = "Read"
            };

            var startTime = DateTime.Now;

            var partition = _gpt.FindPartition(partitionName);
            if (partition == null)
            {
                result.ErrorMessage = $"分区不存在: {partitionName}";
                return result;
            }

            Log($"读取分区 {partitionName}: LBA {partition.StartLba} - {partition.EndLba}");

            var readResult = await _device.ReadToFileAsync(
                partition.StartLba, 
                partition.SectorCount, 
                outputPath, 
                ct);

            result.Success = readResult.Success;
            result.BytesTransferred = readResult.BytesTransferred;
            result.ErrorMessage = readResult.ErrorMessage;
            result.Duration = DateTime.Now - startTime;

            if (result.Success)
            {
                Log($"分区 {partitionName} 读取成功: {result.BytesTransferred / 1024 / 1024} MB");
            }

            return result;
        }

        /// <summary>
        /// 写入文件到分区
        /// </summary>
        public async Task<PartitionOperationResult> WritePartitionAsync(
            string partitionName,
            string inputPath,
            CancellationToken ct = default)
        {
            var result = new PartitionOperationResult
            {
                PartitionName = partitionName,
                Operation = "Write"
            };

            var startTime = DateTime.Now;

            var partition = _gpt.FindPartition(partitionName);
            if (partition == null)
            {
                result.ErrorMessage = $"分区不存在: {partitionName}";
                return result;
            }

            if (!File.Exists(inputPath))
            {
                result.ErrorMessage = $"文件不存在: {inputPath}";
                return result;
            }

            var fileInfo = new FileInfo(inputPath);
            long partitionSize = partition.GetSize(_device.SectorSize);

            if (fileInfo.Length > partitionSize)
            {
                result.ErrorMessage = $"文件大小 ({fileInfo.Length}) 超过分区大小 ({partitionSize})";
                return result;
            }

            Log($"写入分区 {partitionName}: LBA {partition.StartLba}, 文件大小 {fileInfo.Length / 1024 / 1024} MB");

            var writeResult = await _device.WriteFromFileAsync(
                partition.StartLba,
                inputPath,
                ct);

            result.Success = writeResult.Success;
            result.BytesTransferred = writeResult.BytesTransferred;
            result.ErrorMessage = writeResult.ErrorMessage;
            result.Duration = DateTime.Now - startTime;

            if (result.Success)
            {
                Log($"分区 {partitionName} 写入成功: {result.BytesTransferred / 1024 / 1024} MB");
            }

            return result;
        }

        /// <summary>
        /// 擦除分区
        /// </summary>
        public PartitionOperationResult ErasePartition(string partitionName)
        {
            var result = new PartitionOperationResult
            {
                PartitionName = partitionName,
                Operation = "Erase"
            };

            var startTime = DateTime.Now;

            var partition = _gpt.FindPartition(partitionName);
            if (partition == null)
            {
                result.ErrorMessage = $"分区不存在: {partitionName}";
                return result;
            }

            Log($"擦除分区 {partitionName}: LBA {partition.StartLba} - {partition.EndLba}");

            var eraseResult = _device.EraseSectors(partition.StartLba, partition.SectorCount);

            result.Success = eraseResult.Success;
            result.BytesTransferred = eraseResult.BytesTransferred;
            result.ErrorMessage = eraseResult.ErrorMessage;
            result.Duration = DateTime.Now - startTime;

            if (result.Success)
            {
                Log($"分区 {partitionName} 擦除成功");
            }

            return result;
        }

        /// <summary>
        /// 备份所有分区
        /// </summary>
        public async Task<List<PartitionOperationResult>> BackupAllPartitionsAsync(
            string outputFolder,
            CancellationToken ct = default)
        {
            var results = new List<PartitionOperationResult>();

            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);

            int total = Partitions.Count;
            int current = 0;

            foreach (var partition in Partitions)
            {
                if (ct.IsCancellationRequested)
                    break;

                current++;
                Log($"备份分区 ({current}/{total}): {partition.Name}");

                string outputPath = Path.Combine(outputFolder, $"{partition.Name}.img");
                var result = await ReadPartitionAsync(partition.Name, outputPath, ct);
                results.Add(result);
            }

            return results;
        }

        #endregion

        #region Raw Operations

        /// <summary>
        /// 读取原始扇区
        /// </summary>
        public byte[] ReadRawSectors(long startSector, int sectorCount)
        {
            var result = _device.ReadSectors(startSector, sectorCount);
            return result.Success ? result.Data : null;
        }

        /// <summary>
        /// 写入原始扇区
        /// </summary>
        public bool WriteRawSectors(long startSector, byte[] data)
        {
            var result = _device.WriteSectors(startSector, data);
            return result.Success;
        }

        /// <summary>
        /// 读取 GPT
        /// </summary>
        public byte[] ReadGpt()
        {
            // 读取 MBR + GPT Header + Partition Table (34 扇区)
            var result = _device.ReadSectors(0, 34);
            return result.Success ? result.Data : null;
        }

        /// <summary>
        /// 备份 GPT 到文件
        /// </summary>
        public bool BackupGpt(string outputPath)
        {
            var gptData = ReadGpt();
            if (gptData == null)
                return false;

            File.WriteAllBytes(outputPath, gptData);
            Log($"GPT 备份成功: {outputPath}");
            return true;
        }

        /// <summary>
        /// 恢复 GPT 从文件
        /// </summary>
        public bool RestoreGpt(string inputPath)
        {
            if (!File.Exists(inputPath))
            {
                Log($"GPT 文件不存在: {inputPath}");
                return false;
            }

            var gptData = File.ReadAllBytes(inputPath);
            
            // 验证 GPT
            if (!EmmcGptParser.IsValidGpt(gptData, _device.SectorSize))
            {
                Log("无效的 GPT 数据");
                return false;
            }

            var result = _device.WriteSectors(0, gptData);
            
            if (result.Success)
            {
                Log("GPT 恢复成功");
                // 重新解析 GPT
                _gpt.ParseFromDevice(_device);
            }

            return result.Success;
        }

        #endregion

        #region Helper

        private void Log(string message) => OnLog?.Invoke(message);

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                _device?.Dispose();
                _disposed = true;
            }
        }

        #endregion
    }
}
