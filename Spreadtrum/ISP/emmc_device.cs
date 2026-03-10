// ============================================================================
// SakuraEDL - ISP eMMC Device | ISP eMMC 设备
// ============================================================================
// [ZH] ISP eMMC 直接访问 - 通过 Windows API 直接读写存储
// [EN] ISP eMMC Direct Access - Direct storage R/W via Windows kernel API
// [JA] ISP eMMC直接アクセス - Windows APIによる直接ストレージ読み書き
// [KO] ISP eMMC 직접 액세스 - Windows API를 통한 직접 스토리지 읽기/쓰기
// [RU] Прямой доступ ISP eMMC - Прямое чтение/запись через Windows API
// [ES] Acceso directo ISP eMMC - Lectura/escritura directa via Windows API
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SakuraEDL.Spreadtrum.ISP
{
    /// <summary>
    /// eMMC 设备信息
    /// </summary>
    public class EmmcDeviceInfo
    {
        public string DevicePath { get; set; }
        public string FriendlyName { get; set; }
        public long TotalSize { get; set; }
        public int SectorSize { get; set; } = 512;
        public long TotalSectors => TotalSize / SectorSize;
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public bool IsRemovable { get; set; }
    }

    /// <summary>
    /// eMMC 读写结果
    /// </summary>
    public class EmmcOperationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public long BytesTransferred { get; set; }
        public byte[] Data { get; set; }
    }

    /// <summary>
    /// ISP eMMC 直接访问设备
    /// 通过 Windows 内核 API 绕过 FDL 直接读写 eMMC
    /// </summary>
    public class EmmcDevice : IDisposable
    {
        #region Windows API
        
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            FileAccess dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            FileMode dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            ref long lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

        // IOCTL 控制码
        private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
        private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        #endregion

        #region Structures

        [StructLayout(LayoutKind.Sequential)]
        private struct DISK_GEOMETRY
        {
            public long Cylinders;
            public uint MediaType;
            public uint TracksPerCylinder;
            public uint SectorsPerTrack;
            public uint BytesPerSector;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GET_LENGTH_INFORMATION
        {
            public long Length;
        }

        #endregion

        #region Fields

        private SafeFileHandle _handle;
        private FileStream _stream;
        private readonly object _lock = new object();
        private bool _disposed;
        private string _devicePath;
        private int _sectorSize = 512;
        private long _totalSize;

        #endregion

        #region Properties

        /// <summary>
        /// 设备是否已打开
        /// </summary>
        public bool IsOpen => _handle != null && !_handle.IsInvalid && !_handle.IsClosed;

        /// <summary>
        /// 设备路径
        /// </summary>
        public string DevicePath => _devicePath;

        /// <summary>
        /// 扇区大小
        /// </summary>
        public int SectorSize => _sectorSize;

        /// <summary>
        /// 总大小 (字节)
        /// </summary>
        public long TotalSize => _totalSize;

        /// <summary>
        /// 总扇区数
        /// </summary>
        public long TotalSectors => _totalSize / _sectorSize;

        #endregion

        #region Events

        public event Action<string> OnLog;
        public event Action<long, long> OnProgress;

        #endregion

        #region Open/Close

        /// <summary>
        /// 打开 eMMC 设备
        /// </summary>
        /// <param name="devicePath">设备路径，如 \\.\PhysicalDrive1</param>
        public bool Open(string devicePath)
        {
            if (IsOpen)
                Close();

            _devicePath = devicePath;

            // 确保路径格式正确
            if (!devicePath.StartsWith("\\\\.\\"))
            {
                devicePath = "\\\\.\\" + devicePath.Replace("\\\\.\\", "");
            }

            int retries = 0;
            const int maxRetries = 60;

            while (retries < maxRetries)
            {
                _handle = CreateFile(
                    devicePath,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    IntPtr.Zero,
                    FileMode.Open,
                    FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
                    IntPtr.Zero);

                if (!_handle.IsInvalid)
                {
                    // 获取设备信息
                    GetDeviceGeometry();
                    
                    Log($"eMMC 设备已打开: {devicePath}");
                    Log($"扇区大小: {_sectorSize} 字节");
                    Log($"总大小: {_totalSize / 1024 / 1024} MB");
                    
                    return true;
                }

                retries++;
                Thread.Sleep(100);
            }

            var error = Marshal.GetLastWin32Error();
            Log($"打开设备失败: {devicePath}, 错误码: {error}");
            return false;
        }

        /// <summary>
        /// 关闭设备
        /// </summary>
        public void Close()
        {
            lock (_lock)
            {
                _stream?.Dispose();
                _stream = null;

                _handle?.Dispose();
                _handle = null;
            }
        }

        /// <summary>
        /// 获取设备几何信息
        /// </summary>
        private void GetDeviceGeometry()
        {
            if (_handle == null || _handle.IsInvalid)
                return;

            // 获取扇区大小
            var geometry = new DISK_GEOMETRY();
            int geometrySize = Marshal.SizeOf(geometry);
            IntPtr geometryPtr = Marshal.AllocHGlobal(geometrySize);

            try
            {
                uint bytesReturned;
                if (DeviceIoControl(_handle, IOCTL_DISK_GET_DRIVE_GEOMETRY,
                    IntPtr.Zero, 0, geometryPtr, (uint)geometrySize, out bytesReturned, IntPtr.Zero))
                {
                    geometry = Marshal.PtrToStructure<DISK_GEOMETRY>(geometryPtr);
                    _sectorSize = (int)geometry.BytesPerSector;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(geometryPtr);
            }

            // 获取总大小
            long length = 0;
            uint bytesRet;
            if (DeviceIoControl(_handle, IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero, 0, ref length, 8, out bytesRet, IntPtr.Zero))
            {
                _totalSize = length;
            }
        }

        #endregion

        #region Read Operations

        /// <summary>
        /// 读取扇区
        /// </summary>
        /// <param name="startSector">起始扇区</param>
        /// <param name="sectorCount">扇区数量</param>
        public EmmcOperationResult ReadSectors(long startSector, int sectorCount)
        {
            var result = new EmmcOperationResult();

            if (!IsOpen)
            {
                result.ErrorMessage = "设备未打开";
                return result;
            }

            lock (_lock)
            {
                try
                {
                    long position = startSector * _sectorSize;
                    int length = sectorCount * _sectorSize;
                    byte[] buffer = new byte[length];

                    // 创建流
                    using (var stream = new FileStream(_handle, FileAccess.Read))
                    {
                        stream.Seek(position, SeekOrigin.Begin);
                        int bytesRead = stream.Read(buffer, 0, length);

                        result.Success = bytesRead == length;
                        result.BytesTransferred = bytesRead;
                        result.Data = buffer;

                        if (!result.Success)
                        {
                            result.ErrorMessage = $"读取不完整: 期望 {length}, 实际 {bytesRead}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.ErrorMessage = ex.Message;
                }
            }

            return result;
        }

        /// <summary>
        /// 读取指定地址的数据
        /// </summary>
        /// <param name="offset">起始偏移 (字节)</param>
        /// <param name="length">长度 (字节)</param>
        public EmmcOperationResult ReadBytes(long offset, int length)
        {
            // 对齐到扇区边界
            long startSector = offset / _sectorSize;
            int sectorOffset = (int)(offset % _sectorSize);
            int totalSectors = (sectorOffset + length + _sectorSize - 1) / _sectorSize;

            var result = ReadSectors(startSector, totalSectors);

            if (result.Success && result.Data != null)
            {
                // 提取实际数据
                byte[] actualData = new byte[length];
                Array.Copy(result.Data, sectorOffset, actualData, 0, length);
                result.Data = actualData;
                result.BytesTransferred = length;
            }

            return result;
        }

        /// <summary>
        /// 异步读取分区数据到文件
        /// </summary>
        public async Task<EmmcOperationResult> ReadToFileAsync(
            long startSector, 
            long sectorCount, 
            string outputPath,
            CancellationToken ct = default)
        {
            var result = new EmmcOperationResult();

            if (!IsOpen)
            {
                result.ErrorMessage = "设备未打开";
                return result;
            }

            const int bufferSectors = 1024; // 每次读取 512KB
            long totalBytes = sectorCount * _sectorSize;
            long bytesWritten = 0;

            try
            {
                using (var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 512 * 1024))
                using (var stream = new FileStream(_handle, FileAccess.Read))
                {
                    stream.Seek(startSector * _sectorSize, SeekOrigin.Begin);

                    byte[] buffer = new byte[bufferSectors * _sectorSize];
                    long remainingSectors = sectorCount;

                    while (remainingSectors > 0 && !ct.IsCancellationRequested)
                    {
                        int sectorsToRead = (int)Math.Min(bufferSectors, remainingSectors);
                        int bytesToRead = sectorsToRead * _sectorSize;

                        int bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead, ct);
                        if (bytesRead == 0) break;

                        await outStream.WriteAsync(buffer, 0, bytesRead, ct);

                        bytesWritten += bytesRead;
                        remainingSectors -= sectorsToRead;

                        Progress(bytesWritten, totalBytes);
                    }

                    result.Success = bytesWritten == totalBytes;
                    result.BytesTransferred = bytesWritten;

                    if (!result.Success && !ct.IsCancellationRequested)
                    {
                        result.ErrorMessage = $"读取不完整: 期望 {totalBytes}, 实际 {bytesWritten}";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "操作已取消";
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region Write Operations

        /// <summary>
        /// 写入扇区
        /// </summary>
        /// <param name="startSector">起始扇区</param>
        /// <param name="data">数据 (必须是扇区大小的整数倍)</param>
        public EmmcOperationResult WriteSectors(long startSector, byte[] data)
        {
            var result = new EmmcOperationResult();

            if (!IsOpen)
            {
                result.ErrorMessage = "设备未打开";
                return result;
            }

            if (data.Length % _sectorSize != 0)
            {
                result.ErrorMessage = $"数据长度必须是扇区大小 ({_sectorSize}) 的整数倍";
                return result;
            }

            lock (_lock)
            {
                try
                {
                    long position = startSector * _sectorSize;

                    using (var stream = new FileStream(_handle, FileAccess.Write))
                    {
                        stream.Seek(position, SeekOrigin.Begin);
                        stream.Write(data, 0, data.Length);
                        stream.Flush();

                        result.Success = true;
                        result.BytesTransferred = data.Length;
                    }
                }
                catch (Exception ex)
                {
                    result.ErrorMessage = ex.Message;
                }
            }

            return result;
        }

        /// <summary>
        /// 异步从文件写入分区
        /// </summary>
        public async Task<EmmcOperationResult> WriteFromFileAsync(
            long startSector,
            string inputPath,
            CancellationToken ct = default)
        {
            var result = new EmmcOperationResult();

            if (!IsOpen)
            {
                result.ErrorMessage = "设备未打开";
                return result;
            }

            if (!File.Exists(inputPath))
            {
                result.ErrorMessage = $"文件不存在: {inputPath}";
                return result;
            }

            var fileInfo = new FileInfo(inputPath);
            long totalBytes = fileInfo.Length;

            // 对齐到扇区边界
            if (totalBytes % _sectorSize != 0)
            {
                totalBytes = ((totalBytes / _sectorSize) + 1) * _sectorSize;
            }

            const int bufferSectors = 1024; // 每次写入 512KB
            long bytesWritten = 0;

            try
            {
                using (var inStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 512 * 1024))
                using (var stream = new FileStream(_handle, FileAccess.Write))
                {
                    stream.Seek(startSector * _sectorSize, SeekOrigin.Begin);

                    byte[] buffer = new byte[bufferSectors * _sectorSize];
                    int bytesRead;

                    while ((bytesRead = await inStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        if (ct.IsCancellationRequested) break;

                        // 对齐到扇区边界
                        int alignedBytes = bytesRead;
                        if (alignedBytes % _sectorSize != 0)
                        {
                            alignedBytes = ((alignedBytes / _sectorSize) + 1) * _sectorSize;
                            // 填充 0
                            Array.Clear(buffer, bytesRead, alignedBytes - bytesRead);
                        }

                        await stream.WriteAsync(buffer, 0, alignedBytes, ct);
                        bytesWritten += bytesRead;

                        Progress(bytesWritten, fileInfo.Length);
                    }

                    stream.Flush();

                    result.Success = true;
                    result.BytesTransferred = bytesWritten;
                }
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "操作已取消";
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// 擦除扇区 (填充 0xFF)
        /// </summary>
        public EmmcOperationResult EraseSectors(long startSector, long sectorCount)
        {
            var result = new EmmcOperationResult();

            if (!IsOpen)
            {
                result.ErrorMessage = "设备未打开";
                return result;
            }

            const int bufferSectors = 1024;
            byte[] eraseBuffer = new byte[bufferSectors * _sectorSize];
            
            // 填充 0xFF
            for (int i = 0; i < eraseBuffer.Length; i++)
                eraseBuffer[i] = 0xFF;

            long currentSector = startSector;
            long remainingSectors = sectorCount;
            long totalBytes = sectorCount * _sectorSize;
            long bytesErased = 0;

            try
            {
                using (var stream = new FileStream(_handle, FileAccess.Write))
                {
                    stream.Seek(startSector * _sectorSize, SeekOrigin.Begin);

                    while (remainingSectors > 0)
                    {
                        int sectorsToErase = (int)Math.Min(bufferSectors, remainingSectors);
                        int bytesToWrite = sectorsToErase * _sectorSize;

                        stream.Write(eraseBuffer, 0, bytesToWrite);

                        bytesErased += bytesToWrite;
                        remainingSectors -= sectorsToErase;

                        Progress(bytesErased, totalBytes);
                    }

                    stream.Flush();

                    result.Success = true;
                    result.BytesTransferred = bytesErased;
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// 读取 GPT 头
        /// </summary>
        public byte[] ReadGptHeader()
        {
            // GPT 头在 LBA 1 (第二个扇区)
            var result = ReadSectors(1, 1);
            return result.Success ? result.Data : null;
        }

        /// <summary>
        /// 读取 GPT 分区表
        /// </summary>
        public byte[] ReadGptPartitionTable()
        {
            // GPT 分区表通常在 LBA 2 开始，占 32 个扇区
            var result = ReadSectors(2, 32);
            return result.Success ? result.Data : null;
        }

        /// <summary>
        /// 读取 MBR
        /// </summary>
        public byte[] ReadMbr()
        {
            var result = ReadSectors(0, 1);
            return result.Success ? result.Data : null;
        }

        #endregion

        #region Helper

        private void Log(string message) => OnLog?.Invoke(message);
        private void Progress(long current, long total) => OnProgress?.Invoke(current, total);

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _disposed = true;
            }
        }

        #endregion
    }
}
