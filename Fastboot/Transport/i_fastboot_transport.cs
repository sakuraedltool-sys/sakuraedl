// ============================================================================
// SakuraEDL - Fastboot Transport Interface | Fastboot 传输接口
// ============================================================================
// [ZH] 传输层接口 - 定义 Fastboot 通信抽象接口
// [EN] Transport Interface - Define abstract interface for Fastboot communication
// [JA] トランスポートインターフェース - Fastboot通信の抽象インターフェース
// [KO] 전송 인터페이스 - Fastboot 통신 추상 인터페이스 정의
// [RU] Интерфейс транспорта - Абстрактный интерфейс связи Fastboot
// [ES] Interfaz de transporte - Definir interfaz abstracta de comunicación
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Fastboot.Transport
{
    /// <summary>
    /// Fastboot 传输层接口
    /// 支持 USB 和 TCP 两种传输方式
    /// </summary>
    public interface IFastbootTransport : IDisposable
    {
        /// <summary>
        /// 是否已连接
        /// </summary>
        bool IsConnected { get; }
        
        /// <summary>
        /// 设备标识（序列号或地址）
        /// </summary>
        string DeviceId { get; }
        
        /// <summary>
        /// 连接设备
        /// </summary>
        Task<bool> ConnectAsync(CancellationToken ct = default);
        
        /// <summary>
        /// 断开连接
        /// </summary>
        void Disconnect();
        
        /// <summary>
        /// 发送数据
        /// </summary>
        Task<int> SendAsync(byte[] data, int offset, int count, CancellationToken ct = default);
        
        /// <summary>
        /// 接收数据
        /// </summary>
        Task<int> ReceiveAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken ct = default);
        
        /// <summary>
        /// 发送并接收响应
        /// </summary>
        Task<byte[]> TransferAsync(byte[] command, int timeoutMs, CancellationToken ct = default);
    }
    
    /// <summary>
    /// Fastboot 设备信息
    /// </summary>
    public class FastbootDeviceDescriptor
    {
        public string Serial { get; set; }
        public string DevicePath { get; set; }
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public string Manufacturer { get; set; }
        public string Product { get; set; }
        public TransportType Type { get; set; }
        
        // TCP 连接信息
        public string Host { get; set; }
        public int Port { get; set; }
        
        public override string ToString()
        {
            if (Type == TransportType.Tcp)
                return $"{Host}:{Port}";
            return $"{Serial} ({VendorId:X4}:{ProductId:X4})";
        }
    }
    
    /// <summary>
    /// 传输类型
    /// </summary>
    public enum TransportType
    {
        Usb,
        Tcp
    }
}
