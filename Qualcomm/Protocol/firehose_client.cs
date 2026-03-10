// ============================================================================
// SakuraEDL - Qualcomm Firehose Client | 高通 Firehose 客户端
// ============================================================================
// [ZH] Firehose 协议 - 高通 EDL 模式 XML 刷写协议
// [EN] Firehose Protocol - Qualcomm EDL XML flashing protocol
// [JA] Firehoseプロトコル - Qualcomm EDL XMLフラッシュプロトコル
// [KO] Firehose 프로토콜 - Qualcomm EDL XML 플래싱 프로토콜
// [RU] Протокол Firehose - XML протокол прошивки Qualcomm EDL
// [ES] Protocolo Firehose - Protocolo de flasheo XML para Qualcomm EDL
// ============================================================================
// Features: Partition R/W, VIP auth, GPT operations, UFS/eMMC, Sparse format
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SakuraEDL.Qualcomm.Common;
using SakuraEDL.Qualcomm.Models;

namespace SakuraEDL.Qualcomm.Protocol
{
    #region 错误处理

    /// <summary>
    /// Firehose 错误码助手
    /// </summary>
    public static class FirehoseErrorHelper
    {
        public static void ParseNakError(string errorText, out string message, out string suggestion, out bool isFatal, out bool canRetry)
        {
            message = "未知错误";
            suggestion = "请重试操作";
            isFatal = false;
            canRetry = true;

            if (string.IsNullOrEmpty(errorText))
                return;

            string lower = errorText.ToLowerInvariant();

            if (lower.Contains("authentication") || lower.Contains("auth failed"))
            {
                message = "认证失败";
                suggestion = "设备需要特殊认证";
                isFatal = true;
                canRetry = false;
            }
            else if (lower.Contains("signature") || lower.Contains("sign"))
            {
                message = "签名验证失败";
                suggestion = "镜像签名不正确";
                isFatal = true;
                canRetry = false;
            }
            else if (lower.Contains("hash") && (lower.Contains("mismatch") || lower.Contains("fail")))
            {
                message = "Hash 校验失败";
                suggestion = "数据完整性验证失败";
                isFatal = true;
                canRetry = false;
            }
            else if (lower.Contains("partition not found"))
            {
                message = "分区未找到";
                suggestion = "设备上不存在此分区";
                isFatal = true;
                canRetry = false;
            }
            else if (lower.Contains("invalid lun"))
            {
                message = "无效的 LUN";
                suggestion = "指定的 LUN 不存在";
                isFatal = true;
                canRetry = false;
            }
            else if (lower.Contains("write protect"))
            {
                message = "写保护";
                suggestion = "存储设备处于写保护状态";
                isFatal = true;
                canRetry = false;
            }
            else if (lower.Contains("timeout"))
            {
                message = "超时";
                suggestion = "操作超时，建议重试";
                isFatal = false;
                canRetry = true;
            }
            else if (lower.Contains("busy"))
            {
                message = "设备忙";
                suggestion = "设备正在处理其他操作";
                isFatal = false;
                canRetry = true;
            }
            else
            {
                message = "设备错误: " + errorText;
                suggestion = "请查看完整错误信息";
            }
        }
    }

    #endregion

    #region VIP 伪装策略

    /// <summary>
    /// VIP 伪装策略
    /// </summary>
    public struct VipSpoofStrategy
    {
        public string Filename { get; private set; }
        public string Label { get; private set; }
        public int Priority { get; private set; }

        public VipSpoofStrategy(string filename, string label, int priority)
        {
            Filename = filename;
            Label = label;
            Priority = priority;
        }

        public override string ToString()
        {
            return string.Format("{0}/{1}", Label, Filename);
        }
    }

    #endregion

    #region 简易缓冲池

    /// <summary>
    /// 简易字节数组缓冲池 (减少 GC 压力)
    /// </summary>
    internal static class SimpleBufferPool
    {
        private static readonly ConcurrentBag<byte[]> _pool16MB = new ConcurrentBag<byte[]>();
        private static readonly ConcurrentBag<byte[]> _pool4MB = new ConcurrentBag<byte[]>();
        private const int SIZE_16MB = 16 * 1024 * 1024;
        private const int SIZE_4MB = 4 * 1024 * 1024;

        public static byte[] Rent(int minSize)
        {
            if (minSize <= SIZE_4MB)
            {
                if (_pool4MB.TryTake(out byte[] buf4))
                    return buf4;
                return new byte[SIZE_4MB];
            }
            if (minSize <= SIZE_16MB)
            {
                if (_pool16MB.TryTake(out byte[] buf16))
                    return buf16;
                return new byte[SIZE_16MB];
            }
            // 超大缓冲区不池化
            return new byte[minSize];
        }

        public static void Return(byte[] buffer)
        {
            if (buffer == null) return;
            // 只池化标准大小
            if (buffer.Length == SIZE_4MB && _pool4MB.Count < 4)
                _pool4MB.Add(buffer);
            else if (buffer.Length == SIZE_16MB && _pool16MB.Count < 2)
                _pool16MB.Add(buffer);
            // 其他大小让 GC 回收
        }
    }

    #endregion

    /// <summary>
    /// Firehose 协议客户端 - 完整版
    /// </summary>
    public class FirehoseClient : IDisposable
    {
        private readonly SerialPortManager _port;
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;  // 详细调试日志 (只写入文件)
        private readonly Action<long, long> _progress;
        private bool _disposed;
        private readonly StringBuilder _rxBuffer = new StringBuilder();

        // 配置 - 速度优化 (USB 3.0 环境可达 100+ MB/s)
        private int _sectorSize = 4096;
        private int _maxPayloadSize = 16 * 1024 * 1024; // 16MB 默认 payload
        private int _lastSuccessfulGptStrategy = -1; // 缓存成功的 GPT 读取策略，避免重复尝试

        private const int ACK_TIMEOUT_MS = 15000;          // 大文件需要更长超时
        private const int FILE_BUFFER_SIZE = 4 * 1024 * 1024;  // 4MB 文件缓冲
        private const int OPTIMAL_PAYLOAD_REQUEST = 16 * 1024 * 1024; // 请求 16MB payload

        // 分段传输配置 (默认 0 = 不分段，使用设备支持的最大 payload)
        private int _customChunkSize = 0;

        // 公开属性
        public string StorageType { get; private set; }
        public int SectorSize { get { return _sectorSize; } }
        public int MaxPayloadSize { get { return _maxPayloadSize; } }
        
        /// <summary>
        /// 获取当前有效的分段大小
        /// </summary>
        public int EffectiveChunkSize 
        { 
            get 
            { 
                if (_customChunkSize > 0)
                    return Math.Min(_customChunkSize, _maxPayloadSize);
                return _maxPayloadSize;
            } 
        }
        
        /// <summary>
        /// 设置自定义分段大小 (0 = 使用默认值)
        /// </summary>
        /// <param name="chunkSize">分段大小 (字节), 必须是扇区大小的倍数</param>
        public void SetChunkSize(int chunkSize)
        {
            if (chunkSize < 0)
                throw new ArgumentException("分段大小不能为负数");
                
            if (chunkSize > 0)
            {
                // 确保是扇区大小的倍数
                chunkSize = (chunkSize / _sectorSize) * _sectorSize;
                if (chunkSize < _sectorSize)
                    chunkSize = _sectorSize;
                    
                // 不能超过设备支持的最大 payload
                chunkSize = Math.Min(chunkSize, _maxPayloadSize);
            }
            
            _customChunkSize = chunkSize;
            if (chunkSize == 0)
                _logDetail(string.Format("[Firehose] 分段模式: 关闭 (使用设备最大值 {0})", FormatSize(_maxPayloadSize)));
            else
                _logDetail(string.Format("[Firehose] 分段模式: 开启 ({0}/块)", FormatSize(chunkSize)));
        }
        
        /// <summary>
        /// 设置分段大小 (按 MB)
        /// </summary>
        public void SetChunkSizeMB(int megabytes)
        {
            SetChunkSize(megabytes * 1024 * 1024);
        }
        
        /// <summary>
        /// 格式化大小显示
        /// </summary>
        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024 * 1024)
                return string.Format("{0:F2} GB", bytes / (1024.0 * 1024 * 1024));
            if (bytes >= 1024 * 1024)
                return string.Format("{0:F1} MB", bytes / (1024.0 * 1024));
            if (bytes >= 1024)
                return string.Format("{0:F0} KB", bytes / 1024.0);
            return string.Format("{0} B", bytes);
        }
        public List<string> SupportedFunctions { get; private set; }

        // 芯片信息
        public string ChipSerial { get; set; }
        public string ChipHwId { get; set; }
        public string ChipPkHash { get; set; }

        // 每个 LUN 的 GPT Header 信息 (用于负扇区转换)
        private Dictionary<int, GptHeaderInfo> _lunHeaders = new Dictionary<int, GptHeaderInfo>();

        /// <summary>
        /// 获取 LUN 的总扇区数 (用于负扇区转换)
        /// </summary>
        public long GetLunTotalSectors(int lun)
        {
            GptHeaderInfo header;
            if (_lunHeaders.TryGetValue(lun, out header))
            {
                // AlternateLba 是备份 GPT Header 的位置 (通常是磁盘最后一个扇区)
                // 总扇区数 = AlternateLba + 1
                return (long)(header.AlternateLba + 1);
            }
            return -1; // 未知
        }

        /// <summary>
        /// 将负扇区转换为绝对扇区 (负数表示从磁盘末尾倒数)
        /// </summary>
        public long ResolveNegativeSector(int lun, long sector)
        {
            if (sector >= 0) return sector;
            
            long totalSectors = GetLunTotalSectors(lun);
            if (totalSectors <= 0)
            {
                _logDetail(string.Format("[GPT] 无法解析负扇区: LUN{0} 总扇区数未知", lun));
                return -1;
            }
            
            // 负数扇区表示从末尾倒数
            // 例如: -5 表示 totalSectors - 5
            long absoluteSector = totalSectors + sector;
            _logDetail(string.Format("[GPT] 负扇区转换: LUN{0} sector {1} -> {2} (总扇区: {3})", 
                lun, sector, absoluteSector, totalSectors));
            return absoluteSector;
        }

        // OnePlus 认证参数 (认证成功后保存，写入时附带)
        public string OnePlusProgramToken { get; set; }
        public string OnePlusProgramPk { get; set; }
        public string OnePlusProjId { get; set; }
        public bool IsOnePlusAuthenticated { get { return !string.IsNullOrEmpty(OnePlusProgramToken); } }

        // 分区缓存
        private List<PartitionInfo> _cachedPartitions = null;

        // 速度统计
        private Stopwatch _transferStopwatch;
        private long _transferTotalBytes;

        public bool IsConnected { get { return _port.IsOpen; } }

        public FirehoseClient(SerialPortManager port, Action<string> log = null, Action<long, long> progress = null, Action<string> logDetail = null)
        {
            _port = port;
            _log = log ?? delegate { };
            _logDetail = logDetail ?? delegate { };
            _progress = progress;
            StorageType = "ufs";
            SupportedFunctions = new List<string>();
            ChipSerial = "";
            ChipHwId = "";
            ChipPkHash = "";
        }

        /// <summary>
        /// 报告字节级进度 (用于速度计算)
        /// </summary>
        public void ReportProgress(long current, long total)
        {
            if (_progress != null)
                _progress(current, total);
        }

        #region 动态伪装策略

        /// <summary>
        /// 获取动态伪装策略列表
        /// </summary>
        public static List<VipSpoofStrategy> GetDynamicSpoofStrategies(int lun, long startSector, string partitionName, bool isGptRead)
        {
            var strategies = new List<VipSpoofStrategy>();

            // =====================================================
            // OPLUS VIP 分段规则:
            // 段1 (0-5/0-33): 使用 gpt_main0 / PrimaryGPT
            // 段2 (6/34): 使用该 LUN 第一个分区的 filename/label
            // 段3 (7-n/35-n): 使用 gpt_main0 / PrimaryGPT
            // =====================================================
            
            // GPT 区域特殊处理 (段1)
            if (isGptRead || startSector <= 33)
            {
                // 优先使用 PrimaryGPT (段1 使用 gpt_main0)
                strategies.Add(new VipSpoofStrategy(string.Format("gpt_main{0}.bin", lun), "PrimaryGPT", 0));
                strategies.Add(new VipSpoofStrategy("gpt_main0.bin", "PrimaryGPT", 1));
                strategies.Add(new VipSpoofStrategy(string.Format("gpt_backup{0}.bin", lun), "BackupGPT", 2));
                strategies.Add(new VipSpoofStrategy("gpt_backup0.bin", "BackupGPT", 3));
            }

            // 通用 backup 伪装
            strategies.Add(new VipSpoofStrategy("gpt_backup0.bin", "BackupGPT", 4));

            // 分区名称伪装
            if (!string.IsNullOrEmpty(partitionName))
            {
                string safeName = SanitizePartitionName(partitionName);
                strategies.Add(new VipSpoofStrategy("gpt_backup0.bin", safeName, 3));
                strategies.Add(new VipSpoofStrategy(safeName + ".bin", safeName, 4));
            }

            // 通用伪装
            strategies.Add(new VipSpoofStrategy("ssd", "ssd", 5));
            strategies.Add(new VipSpoofStrategy("gpt_main0.bin", "gpt_main0.bin", 6));
            strategies.Add(new VipSpoofStrategy("buffer.bin", "buffer", 8));

            // 无伪装
            strategies.Add(new VipSpoofStrategy("", "", 99));

            return strategies;
        }

        private static string SanitizePartitionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "rawdata";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                bool isValid = true;
                foreach (char inv in invalid)
                {
                    if (c == inv) { isValid = false; break; }
                }
                if (isValid) sb.Append(c);
            }

            string safeName = sb.ToString().ToLowerInvariant();
            if (safeName.Length > 32) safeName = safeName.Substring(0, 32);
            return string.IsNullOrEmpty(safeName) ? "rawdata" : safeName;
        }

        #endregion

        #region 基础配置

        /// <summary>
        /// 配置 Firehose
        /// </summary>
        public async Task<bool> ConfigureAsync(string storageType = "ufs", int preferredPayloadSize = 0, CancellationToken ct = default(CancellationToken))
        {
            StorageType = storageType.ToLower();
            _sectorSize = (StorageType == "emmc") ? 512 : 4096;

            int requestedPayload = preferredPayloadSize > 0 ? preferredPayloadSize : OPTIMAL_PAYLOAD_REQUEST;

            // 优化：请求更大的双向传输缓冲区
            // MaxPayloadSizeToTargetInBytes - 写入时每个块的最大大小
            // MaxPayloadSizeFromTargetInBytes - 读取时每个块的最大大小 (关键优化点！)
            // AckRawDataEveryNumPackets=0 - 不需要每个包确认，加速传输
            // ZlpAwareHost=1 - 启用零长度包感知，提高 USB 效率
            string xml = string.Format(
                "<?xml version=\"1.0\" ?><data><configure MemoryName=\"{0}\" Verbose=\"0\" " +
                "AlwaysValidate=\"0\" MaxPayloadSizeToTargetInBytes=\"{1}\" " +
                "MaxPayloadSizeFromTargetInBytes=\"{1}\" " +
                "AckRawDataEveryNumPackets=\"0\" ZlpAwareHost=\"1\" " +
                "SkipStorageInit=\"0\" /></data>",
                storageType, requestedPayload);

            _log("[Firehose] 配置设备...");
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));

            // 最多等待 15 秒 (每次超时 3 秒，最多 5 次重试)
            for (int i = 0; i < 5; i++)
            {
                if (ct.IsCancellationRequested) return false;

                var resp = await ProcessXmlResponseAsync(ct, 3000);
                if (resp != null)
                {
                    string val = resp.Attribute("value") != null ? resp.Attribute("value").Value : "";
                    bool isAck = val.Equals("ACK", StringComparison.OrdinalIgnoreCase);

                    if (isAck || val.Equals("NAK", StringComparison.OrdinalIgnoreCase))
                    {
                        var ssAttr = resp.Attribute("SectorSizeInBytes");
                        if (ssAttr != null)
                        {
                            int size;
                            if (int.TryParse(ssAttr.Value, out size)) _sectorSize = size;
                        }

                        var mpAttr = resp.Attribute("MaxPayloadSizeToTargetInBytes");
                        if (mpAttr != null)
                        {
                            int maxPayload;
                            if (int.TryParse(mpAttr.Value, out maxPayload) && maxPayload > 0)
                                _maxPayloadSize = Math.Max(64 * 1024, Math.Min(maxPayload, 64 * 1024 * 1024)); // 最大 64MB
                        }

                        _logDetail(string.Format("[Firehose] 配置成功 - SectorSize:{0}, MaxPayload:{1}KB", _sectorSize, _maxPayloadSize / 1024));
                        return true;
                    }
                    else if (!string.IsNullOrEmpty(val))
                    {
                        _logDetail(string.Format("[Firehose] 收到非预期响应: {0}", val));
                    }
                }
                else
                {
                    _logDetail(string.Format("[Firehose] 等待响应超时 ({0}/5)...", i + 1));
                }
                await Task.Delay(100, ct);
            }
            
            _log("[Firehose] 配置超时，设备可能不在 Firehose 模式");
            return false;
        }

        /// <summary>
        /// 设置存储扇区大小
        /// </summary>
        public void SetSectorSize(int size)
        {
            _sectorSize = size;
        }

        #endregion

        #region 读取分区表

        /// <summary>
        /// 读取 GPT 分区表 (支持多 LUN)
        /// </summary>
        public async Task<List<PartitionInfo>> ReadGptPartitionsAsync(bool useVipMode = false, CancellationToken ct = default(CancellationToken), IProgress<int> lunProgress = null)
        {
            var partitions = new List<PartitionInfo>();
            
            // 重置槽位检测状态，准备合并所有 LUN 的结果
            ResetSlotDetection();

            for (int lun = 0; lun < 6; lun++)
            {
                // 报告当前 LUN 进度
                if (lunProgress != null) lunProgress.Report(lun);
                byte[] gptData = null;

                // GPT 头在 LBA 1，分区条目从 LBA 2 开始
                // 小米/Redmi 设备可能有超过 128 个分区条目（最多 256 个）
                // 256 个条目 * 128 字节 = 32KB
                // 对于 512 字节扇区: 32KB / 512 = 64 个扇区 + 2 (MBR+Header) = 66 个
                // 对于 4096 字节扇区: 32KB / 4096 = 8 个扇区 + 2 = 10 个
                // 读取 256 个扇区确保覆盖所有可能的分区条目（包括小米设备）
                // 对于 512B 扇区 = 128KB，对于 4KB 扇区 = 1MB
                int gptSectors = 256;

                if (useVipMode)
                {
                    // =====================================================
                    // OPLUS VIP 分段读写规则 (Digest 地址检查限制)
                    // =====================================================
                    // 每个 LUN 分三段，单次读写不能跨越分段：
                    //
                    // [eMMC版] 扇区 0-33 | 34 | 35-n  (三段)
                    // [UFS版]  扇区 0-5  | 6  | 7-n   (三段)
                    //
                    // 段1和段3: 使用 gpt_main0.bin / PrimaryGPT
                    // 段2(特殊): 使用该LUN第一个分区的 filename/label
                    //
                    // GPT 数据在段1内，不需要跨段：
                    // - UFS: 扇区 0-5 (6个, 24KB) ✓
                    // - eMMC: 扇区 0-33 (34个, 17KB) ✓
                    // =====================================================
                    int vipGptSectors = (_sectorSize == 4096) ? 6 : 34;
                    
                    // 仅在第一个 LUN 时打印详细信息
                    if (lun == 0)
                        _log(string.Format("[GPT] VIP 模式读取 (扇区大小={0}B, {1} 扇区/LUN, {2}KB)", _sectorSize, vipGptSectors, vipGptSectors * _sectorSize / 1024));
                    
                    // 构建当前 LUN 的策略列表
                    var strategies = new List<(string label, string filename)>();
                    
                    // 如果已有成功策略，优先使用（仅改变 filename 中的 LUN 编号）
                    if (_lastSuccessfulGptStrategy >= 0)
                    {
                        // 使用上次成功的策略类型
                        switch (_lastSuccessfulGptStrategy)
                        {
                            case 0: strategies.Add(("BackupGPT", string.Format("gpt_backup{0}.bin", lun))); break;
                            case 1: strategies.Add(("BackupGPT", "gpt_backup0.bin")); break;
                            case 2: strategies.Add(("PrimaryGPT", string.Format("gpt_main{0}.bin", lun))); break;
                            case 3: strategies.Add(("ssd", "ssd")); break;
                        }
                    }
                    else
                    {
                        // 首次尝试，使用完整策略列表
                        // 根据 OPLUS VIP 规则：段1 (0-5/0-33) 使用 PrimaryGPT/gpt_main0
                        // 优先尝试 PrimaryGPT，因为我们读取的是段1内的数据
                        strategies.Add(("PrimaryGPT", string.Format("gpt_main{0}.bin", lun)));
                        strategies.Add(("PrimaryGPT", "gpt_main0.bin"));
                        strategies.Add(("BackupGPT", string.Format("gpt_backup{0}.bin", lun)));
                        strategies.Add(("BackupGPT", "gpt_backup0.bin"));
                        strategies.Add(("ssd", "ssd"));
                    }

                    for (int i = 0; i < strategies.Count; i++)
                    {
                        try
                        {
                            // LUN0 添加详细日志
                            if (lun == 0)
                                _logDetail(string.Format("[GPT] 尝试策略 {0}/{1}: {2}/{3}", i + 1, strategies.Count, strategies[i].label, strategies[i].filename));
                            
                            gptData = await ReadGptPacketWithTimeoutAsync(lun, 0, vipGptSectors, strategies[i].label, strategies[i].filename, ct, 8000);
                            
                            if (gptData != null && gptData.Length >= 512)
                            {
                                // 记住成功的策略索引（仅在首次成功时）
                                if (_lastSuccessfulGptStrategy < 0)
                                {
                                    _lastSuccessfulGptStrategy = i;
                                    _log(string.Format("[GPT] 使用伪装策略: {0}", strategies[i].label));
                                }
                                break;
                            }
                            else if (lun == 0)
                            {
                                _logDetail(string.Format("[GPT] 策略 {0} 返回空数据或太小 (len={1})", strategies[i].label, gptData?.Length ?? 0));
                            }
                        }
                        catch (TimeoutException)
                        {
                            _log(string.Format("[GPT] LUN{0} 策略 {1} 超时", lun, strategies[i].label));
                        }
                        catch (Exception ex)
                        {
                            _log(string.Format("[GPT] LUN{0} 策略 {1} 异常: {2}", lun, strategies[i].label, ex.Message));
                        }
                        
                        if (i < strategies.Count - 1)
                            await Task.Delay(100, ct); // 减少延迟
                    }
                }
                else
                {
                    // 普通模式读取
                    try
                    {
                        PurgeBuffer();
                        if (lun > 0) await Task.Delay(50, ct);

                        gptData = await ReadSectorsAsync(lun, 0, gptSectors, ct);
                        // 不输出每个 LUN 的详细日志，只在有数据时统计
                    }
                    catch (Exception ex)
                    {
                        _logDetail(string.Format("[GPT] LUN{0} 读取异常: {1}", lun, ex.Message));
                    }
                }

                if (gptData == null || gptData.Length < 512)
                {
                    // 主日志显示读取失败，便于用户排查
                    _log(string.Format("[GPT] LUN{0} 读取失败 (数据为空或太小)", lun));
                    continue;
                }
                
                // 诊断: 检查 GPT 签名
                bool hasGptSignature = false;
                for (int sigOffset = 0; sigOffset < Math.Min(gptData.Length - 8, 8192); sigOffset += 512)
                {
                    if (gptData.Length > sigOffset + 7 &&
                        gptData[sigOffset] == 0x45 && gptData[sigOffset + 1] == 0x46 &&
                        gptData[sigOffset + 2] == 0x49 && gptData[sigOffset + 3] == 0x20 &&
                        gptData[sigOffset + 4] == 0x50 && gptData[sigOffset + 5] == 0x41 &&
                        gptData[sigOffset + 6] == 0x52 && gptData[sigOffset + 7] == 0x54)
                    {
                        hasGptSignature = true;
                        _logDetail(string.Format("[GPT] LUN{0} 找到 GPT 签名 @ 偏移 {1}", lun, sigOffset));
                        break;
                    }
                }
                if (!hasGptSignature)
                {
                    _logDetail(string.Format("[GPT] LUN{0} 未找到 GPT 签名 (数据长度={1})", lun, gptData.Length));
                    // 输出前 64 字节用于诊断
                    if (gptData.Length >= 64)
                    {
                        _logDetail(string.Format("[GPT] LUN{0} 前64字节: {1}", lun, 
                            BitConverter.ToString(gptData, 0, 64).Replace("-", " ")));
                    }
                }

                var lunPartitions = ParseGptPartitions(gptData, lun);
                if (lunPartitions.Count > 0)
                {
                    partitions.AddRange(lunPartitions);
                    // 主日志显示每个 LUN 的分区数
                    _log(string.Format("[GPT] LUN{0}: {1} 个分区", lun, lunPartitions.Count));
                }
                // 没有分区的 LUN 不输出日志（很多设备只有 LUN0 有数据）
            }

            if (partitions.Count > 0)
            {
                _cachedPartitions = partitions;
                _log(string.Format("[Firehose] 共读取 {0} 个分区", partitions.Count));
                
                // 输出合并后的槽位状态
                if (_mergedSlot != "nonexistent")
                {
                    _logDetail(string.Format("[Firehose] 设备槽位: {0} (A激活={1}, B激活={2})", 
                        _mergedSlot, _slotACount, _slotBCount));
                }
            }

            return partitions;
        }

        /// <summary>
        /// 读取 GPT 数据包 (使用伪装)
        /// </summary>
        public async Task<byte[]> ReadGptPacketAsync(int lun, long startSector, int numSectors, string label, string filename, CancellationToken ct)
        {
            return await ReadGptPacketWithTimeoutAsync(lun, startSector, numSectors, label, filename, ct, 30000);
        }

        /// <summary>
        /// 读取 GPT 数据包 (带超时保护，防止卡死)
        /// </summary>
        public async Task<byte[]> ReadGptPacketWithTimeoutAsync(int lun, long startSector, int numSectors, string label, string filename, CancellationToken ct, int timeoutMs = 10000)
        {
            double sizeKB = (numSectors * _sectorSize) / 1024.0;
            long startByte = startSector * _sectorSize;

            string xml = string.Format(
                "<?xml version=\"1.0\" ?><data>\n" +
                "<read SECTOR_SIZE_IN_BYTES=\"{0}\" file_sector_offset=\"0\" filename=\"{1}\" " +
                "label=\"{2}\" num_partition_sectors=\"{3}\" partofsingleimage=\"true\" " +
                "physical_partition_number=\"{4}\" readbackverify=\"false\" size_in_KB=\"{5:F1}\" " +
                "sparse=\"false\" start_byte_hex=\"0x{6:X}\" start_sector=\"{7}\" />\n</data>\n",
                _sectorSize, filename, label, numSectors, lun, sizeKB, startByte, startSector);

            _logDetail(string.Format("[GPT] 读取 LUN{0} (伪装: {1}/{2})...", lun, label, filename));
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));

            var buffer = new byte[numSectors * _sectorSize];
            
            // 使用超时保护，防止设备不响应导致卡死
            using (var timeoutCts = new CancellationTokenSource(timeoutMs))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
            {
                try
                {
                    // 使用带超时的接收方法
                    var receiveTask = ReceiveDataAfterAckAsync(buffer, linkedCts.Token);
                    var delayTask = Task.Delay(timeoutMs, ct);
                    
                    var completedTask = await Task.WhenAny(receiveTask, delayTask);
                    
                    if (completedTask == delayTask)
                    {
                        _logDetail(string.Format("[GPT] LUN{0} 读取超时 ({1}ms)", lun, timeoutMs));
                        throw new TimeoutException(string.Format("GPT 读取超时: LUN{0}", lun));
                    }
                    
                    if (await receiveTask)
                    {
                        await WaitForAckAsync(linkedCts.Token, 10);
                        _logDetail(string.Format("[GPT] LUN{0} 读取成功 ({1} 字节)", lun, buffer.Length));
                        return buffer;
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    _logDetail(string.Format("[GPT] LUN{0} 读取超时 ({1}ms)", lun, timeoutMs));
                    throw new TimeoutException(string.Format("GPT 读取超时: LUN{0}", lun));
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logDetail(string.Format("[GPT] LUN{0} 读取异常: {1}", lun, ex.Message));
                }
            }

            _logDetail(string.Format("[GPT] LUN{0} 读取失败", lun));
            return null;
        }

        /// <summary>
        /// 最后一次解析的 GPT 结果 (包含槽位信息)
        /// </summary>
        public GptParseResult LastGptResult { get; private set; }

        /// <summary>
        /// 合并后的槽位状态 (来自所有 LUN)
        /// </summary>
        private string _mergedSlot = "nonexistent";
        private int _slotACount = 0;
        private int _slotBCount = 0;

        /// <summary>
        /// 当前槽位 ("a", "b", "undefined", "nonexistent") - 合并所有 LUN 的结果
        /// </summary>
        public string CurrentSlot
        {
            get { return _mergedSlot; }
        }

        /// <summary>
        /// 重置槽位检测状态 (在开始新的 GPT 读取前调用)
        /// </summary>
        public void ResetSlotDetection()
        {
            _mergedSlot = "nonexistent";
            _slotACount = 0;
            _slotBCount = 0;
        }

        /// <summary>
        /// 合并 LUN 的槽位检测结果
        /// </summary>
        private void MergeSlotInfo(GptParseResult result)
        {
            if (result?.SlotInfo == null) return;
            
            var slotInfo = result.SlotInfo;
            
            // 如果这个 LUN 有 A/B 分区
            if (slotInfo.HasAbPartitions)
            {
                // 至少有 A/B 分区存在
                if (_mergedSlot == "nonexistent")
                    _mergedSlot = "undefined";
                
                // 统计激活的槽位
                if (slotInfo.CurrentSlot == "a")
                    _slotACount++;
                else if (slotInfo.CurrentSlot == "b")
                    _slotBCount++;
            }
            
            // 根据统计结果确定最终槽位
            if (_slotACount > _slotBCount && _slotACount > 0)
                _mergedSlot = "a";
            else if (_slotBCount > _slotACount && _slotBCount > 0)
                _mergedSlot = "b";
            else if (_slotACount > 0 && _slotBCount > 0)
                _mergedSlot = "unknown";  // 冲突
            // 否则保持 "undefined" 或 "nonexistent"
        }

        /// <summary>
        /// 解析 GPT 分区 (使用增强版 GptParser)
        /// </summary>
        public List<PartitionInfo> ParseGptPartitions(byte[] gptData, int lun)
        {
            var parser = new GptParser(_log, _logDetail);
            var result = parser.Parse(gptData, lun, _sectorSize);
            
            // 保存解析结果
            LastGptResult = result;
            
            // 合并槽位检测结果
            MergeSlotInfo(result);

            if (result.Success && result.Header != null)
            {
                // 存储 LUN 的 Header 信息 (用于负扇区转换)
                _lunHeaders[lun] = result.Header;

                // 自动更新扇区大小
                if (result.Header.SectorSize > 0 && result.Header.SectorSize != _sectorSize)
                {
                    _logDetail(string.Format("[GPT] 更新扇区大小: {0} -> {1}", _sectorSize, result.Header.SectorSize));
                    _sectorSize = result.Header.SectorSize;
                }

                // 输出详细信息 (只写入日志文件)
                _logDetail(string.Format("[GPT] 磁盘 GUID: {0}", result.Header.DiskGuid));
                _logDetail(string.Format("[GPT] 分区数据区: LBA {0} - {1}", 
                    result.Header.FirstUsableLba, result.Header.LastUsableLba));
                _logDetail(string.Format("[GPT] CRC: {0}", result.Header.CrcValid ? "有效" : "无效"));
                
                if (result.SlotInfo.HasAbPartitions)
                {
                    string slotMethod = result.SlotInfoV2?.DetectionMethod ?? "";
                    _logDetail(string.Format("[GPT] 当前槽位: {0} ({1})", result.SlotInfo.CurrentSlot, slotMethod));
                }
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                _logDetail(string.Format("[GPT] 解析失败: {0}", result.ErrorMessage));
            }

            return result.Partitions;
        }

        /// <summary>
        /// 生成 rawprogram.xml
        /// </summary>
        public string GenerateRawprogramXml()
        {
            if (_cachedPartitions == null || _cachedPartitions.Count == 0)
                return null;

            var parser = new GptParser(_log, _logDetail);
            return parser.GenerateRawprogramXml(_cachedPartitions, _sectorSize);
        }

        /// <summary>
        /// 生成 partition.xml
        /// </summary>
        public string GeneratePartitionXml()
        {
            if (_cachedPartitions == null || _cachedPartitions.Count == 0)
                return null;

            var parser = new GptParser(_log, _logDetail);
            return parser.GeneratePartitionXml(_cachedPartitions, _sectorSize);
        }

        #endregion

        #region 读取分区

        /// <summary>
        /// 读取分区到文件 (支持自定义分段)
        /// </summary>
        /// <param name="partition">分区信息</param>
        /// <param name="savePath">保存路径</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="chunkProgress">分段进度回调 (当前块索引, 总块数, 块字节数)</param>
        public async Task<bool> ReadPartitionAsync(PartitionInfo partition, string savePath, 
            CancellationToken ct = default(CancellationToken),
            Action<int, int, long> chunkProgress = null)
        {
            return await ReadPartitionChunkedAsync(partition.Lun, partition.StartSector, 
                partition.NumSectors, partition.SectorSize, savePath, partition.Name, ct, chunkProgress);
        }

        /// <summary>
        /// 分段读取分区到文件 (核心实现)
        /// </summary>
        /// <param name="lun">LUN 编号</param>
        /// <param name="startSector">起始扇区</param>
        /// <param name="numSectors">扇区数量</param>
        /// <param name="sectorSize">扇区大小</param>
        /// <param name="savePath">保存路径</param>
        /// <param name="label">分区名称 (日志用)</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="chunkProgress">分段进度回调</param>
        public async Task<bool> ReadPartitionChunkedAsync(int lun, long startSector, long numSectors, 
            int sectorSize, string savePath, string label,
            CancellationToken ct = default(CancellationToken),
            Action<int, int, long> chunkProgress = null)
        {
            _log(string.Format("[Firehose] 读取: {0} ({1})", label, FormatSize(numSectors * sectorSize)));

            // 使用有效分段大小 (默认使用设备最大值，不分段)
            int chunkSize = EffectiveChunkSize;
            long sectorsPerChunk = chunkSize / sectorSize;
            
            // 计算总块数
            int totalChunks = (int)Math.Ceiling((double)numSectors / sectorsPerChunk);
            long totalSize = numSectors * sectorSize;
            long totalRead = 0L;
            
            // 只有启用自定义分段时才显示分段信息
            if (_customChunkSize > 0)
            {
                _logDetail(string.Format("[Firehose] 分段传输: {0}/块, 共 {1} 块", 
                    FormatSize(chunkSize), totalChunks));
            }

            StartTransferTimer(totalSize);

            using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, FILE_BUFFER_SIZE))
            {
                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    if (ct.IsCancellationRequested) 
                    {
                        _log("[Firehose] 读取已取消");
                        return false;
                    }

                    long sectorOffset = chunkIndex * sectorsPerChunk;
                    long sectorsToRead = Math.Min(sectorsPerChunk, numSectors - sectorOffset);
                    long currentStartSector = startSector + sectorOffset;

                    // 分段进度回调
                    chunkProgress?.Invoke(chunkIndex + 1, totalChunks, sectorsToRead * sectorSize);

                    var data = await ReadSectorsAsync(lun, currentStartSector, (int)sectorsToRead, ct);
                    if (data == null)
                    {
                        _log(string.Format("[Firehose] 读取失败 @ 块 {0}/{1}, sector {2}", 
                            chunkIndex + 1, totalChunks, currentStartSector));
                        return false;
                    }

                    await fs.WriteAsync(data, 0, data.Length, ct);
                    totalRead += data.Length;

                    // 总进度回调
                    _progress?.Invoke(totalRead, totalSize);
                }
            }

            StopTransferTimer("读取", totalRead);
            _log(string.Format("[Firehose] {0} 读取完成: {1}", label, FormatSize(totalRead)));
            return true;
        }

        /// <summary>
        /// 分段读取到内存 (适用于小分区)
        /// </summary>
        public async Task<byte[]> ReadPartitionToMemoryAsync(PartitionInfo partition, 
            CancellationToken ct = default(CancellationToken),
            Action<int, int, long> chunkProgress = null)
        {
            return await ReadToMemoryChunkedAsync(partition.Lun, partition.StartSector, 
                partition.NumSectors, partition.Name, ct, chunkProgress);
        }

        /// <summary>
        /// 分段读取到内存 (核心实现)
        /// </summary>
        public async Task<byte[]> ReadToMemoryChunkedAsync(int lun, long startSector, long numSectors,
            string label, CancellationToken ct = default(CancellationToken),
            Action<int, int, long> chunkProgress = null)
        {
            int chunkSize = EffectiveChunkSize;
            long sectorsPerChunk = chunkSize / _sectorSize;
            int totalChunks = (int)Math.Ceiling((double)numSectors / sectorsPerChunk);
            long totalSize = numSectors * _sectorSize;

            using (var ms = new MemoryStream((int)Math.Min(totalSize, int.MaxValue)))
            {
                long totalRead = 0L;

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    if (ct.IsCancellationRequested) return null;

                    long sectorOffset = chunkIndex * sectorsPerChunk;
                    long sectorsToRead = Math.Min(sectorsPerChunk, numSectors - sectorOffset);
                    long currentStartSector = startSector + sectorOffset;

                    chunkProgress?.Invoke(chunkIndex + 1, totalChunks, sectorsToRead * _sectorSize);

                    var data = await ReadSectorsAsync(lun, currentStartSector, (int)sectorsToRead, ct);
                    if (data == null) return null;

                    ms.Write(data, 0, data.Length);
                    totalRead += data.Length;

                    _progress?.Invoke(totalRead, totalSize);
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// 读取扇区数据
        /// </summary>
        public async Task<byte[]> ReadSectorsAsync(int lun, long startSector, int numSectors, CancellationToken ct, bool useVipMode = false, string partitionName = null)
        {
            if (useVipMode)
            {
                bool isGptRead = startSector <= 33;
                var strategies = GetDynamicSpoofStrategies(lun, startSector, partitionName, isGptRead);

                foreach (var strategy in strategies)
                {
                    try
                    {
                        if (ct.IsCancellationRequested) return null;
                        PurgeBuffer();

                        string xml;
                        double sizeKB = (numSectors * _sectorSize) / 1024.0;

                        if (string.IsNullOrEmpty(strategy.Label))
                        {
                            xml = string.Format(
                                "<?xml version=\"1.0\" ?><data>\n" +
                                "<read SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" " +
                                "physical_partition_number=\"{2}\" size_in_KB=\"{3:F1}\" start_sector=\"{4}\" />\n</data>\n",
                                _sectorSize, numSectors, lun, sizeKB, startSector);
                        }
                        else
                        {
                            xml = string.Format(
                                "<?xml version=\"1.0\" ?><data>\n" +
                                "<read SECTOR_SIZE_IN_BYTES=\"{0}\" filename=\"{1}\" label=\"{2}\" " +
                                "num_partition_sectors=\"{3}\" physical_partition_number=\"{4}\" " +
                                "size_in_KB=\"{5:F1}\" sparse=\"false\" start_sector=\"{6}\" />\n</data>\n",
                                _sectorSize, strategy.Filename, strategy.Label, numSectors, lun, sizeKB, startSector);
                        }

                        _port.Write(Encoding.UTF8.GetBytes(xml));

                        int expectedSize = numSectors * _sectorSize;
                        var buffer = new byte[expectedSize];

                        if (await ReceiveDataAfterAckAsync(buffer, ct))
                        {
                            await WaitForAckAsync(ct);
                            return buffer;
                        }
                    }
                    catch (Exception ex)
                    {
                        // VIP 策略尝试失败，继续下一个策略
                        _logDetail(string.Format("[Firehose] VIP 策略 {0} 失败: {1}", strategy.Label ?? "直接读取", ex.Message));
                    }
                }

                return null;
            }
            else
            {
                try
                {
                    PurgeBuffer();

                    double sizeKB = (numSectors * _sectorSize) / 1024.0;

                    string xml = string.Format(
                        "<?xml version=\"1.0\" ?><data>\n" +
                        "<read SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" " +
                        "physical_partition_number=\"{2}\" size_in_KB=\"{3:F1}\" start_sector=\"{4}\" />\n</data>\n",
                        _sectorSize, numSectors, lun, sizeKB, startSector);

                    _port.Write(Encoding.UTF8.GetBytes(xml));

                    int expectedSize = numSectors * _sectorSize;
                    var buffer = new byte[expectedSize];

                    if (await ReceiveDataAfterAckAsync(buffer, ct))
                    {
                        await WaitForAckAsync(ct);
                        return buffer;
                    }
                }
                catch (Exception ex)
                {
                    _log(string.Format("[Read] 异常: {0}", ex.Message));
                }

                return null;
            }
        }

        #endregion

        #region 写入分区

        /// <summary>
        /// 写入分区数据 (支持自定义分段)
        /// </summary>
        /// <param name="partition">分区信息</param>
        /// <param name="imagePath">镜像文件路径</param>
        /// <param name="useOppoMode">是否使用 OPPO 模式</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="chunkProgress">分段进度回调 (当前块索引, 总块数, 块字节数)</param>
        public async Task<bool> WritePartitionAsync(PartitionInfo partition, string imagePath, 
            bool useOppoMode = false, CancellationToken ct = default(CancellationToken),
            Action<int, int, long> chunkProgress = null)
        {
            return await WritePartitionChunkedAsync(partition.Lun, partition.StartSector, _sectorSize, 
                imagePath, partition.Name, useOppoMode, ct, chunkProgress);
        }

        /// <summary>
        /// 分段写入分区数据 (核心实现)
        /// </summary>
        public async Task<bool> WritePartitionChunkedAsync(int lun, long startSector, int sectorSize, 
            string imagePath, string label = "Partition", bool useOppoMode = false, 
            CancellationToken ct = default(CancellationToken),
            Action<int, int, long> chunkProgress = null)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("镜像文件不存在", imagePath);

            // 检查是否为 Sparse 镜像
            bool isSparse = SparseStream.IsSparseFile(imagePath);
            
            if (isSparse)
            {
                // 智能 Sparse 写入：只写入有数据的部分，跳过 DONT_CARE
                return await WriteSparsePartitionSmartAsync(lun, startSector, sectorSize, imagePath, label, useOppoMode, ct);
            }
            
            long fileSize = new FileInfo(imagePath).Length;
            _log(string.Format("[Firehose] 写入: {0} ({1})", label, FormatSize(fileSize)));

            // 使用有效分段大小 (默认使用设备最大值，不分段)
            int chunkSize = EffectiveChunkSize;
            long sectorsPerChunk = chunkSize / sectorSize;
            long bytesPerChunk = sectorsPerChunk * sectorSize;
            
            // 计算总块数
            int totalChunks = (int)Math.Ceiling((double)fileSize / bytesPerChunk);
            
            // 只有启用自定义分段时才显示分段信息
            if (_customChunkSize > 0)
            {
                _logDetail(string.Format("[Firehose] 分段传输: {0}/块, 共 {1} 块", 
                    FormatSize(chunkSize), totalChunks));
            }

            // 使用顺序访问提示和大缓冲区优化读取速度
            using (Stream sourceStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, 
                FileShare.Read, FILE_BUFFER_SIZE, FileOptions.SequentialScan))
            {
                var totalBytes = sourceStream.Length;
                var totalWritten = 0L;
                int currentChunk = 0;

                StartTransferTimer(totalBytes);

                // 使用 ArrayPool 减少 GC 压力
                var buffer = SimpleBufferPool.Rent((int)bytesPerChunk);
                try
                {
                    var currentSector = startSector;

                    while (totalWritten < totalBytes)
                    {
                        if (ct.IsCancellationRequested) 
                        {
                            _log("[Firehose] 写入已取消");
                            return false;
                        }

                        currentChunk++;
                        var bytesToRead = (int)Math.Min(bytesPerChunk, totalBytes - totalWritten);
                        var bytesRead = sourceStream.Read(buffer, 0, bytesToRead);
                        if (bytesRead == 0) break;

                        // 分段进度回调
                        chunkProgress?.Invoke(currentChunk, totalChunks, bytesRead);

                        // 补齐到扇区边界
                        var paddedSize = ((bytesRead + sectorSize - 1) / sectorSize) * sectorSize;
                        if (paddedSize > bytesRead)
                            Array.Clear(buffer, bytesRead, paddedSize - bytesRead);

                        var sectorsToWrite = paddedSize / sectorSize;

                        if (!await WriteSectorsAsync(lun, currentSector, buffer, paddedSize, label, useOppoMode, ct))
                        {
                            _log(string.Format("[Firehose] 写入失败 @ 块 {0}/{1}, sector {2}", 
                                currentChunk, totalChunks, currentSector));
                            return false;
                        }

                        totalWritten += bytesRead;
                        currentSector += sectorsToWrite;

                        // 总进度回调
                        _progress?.Invoke(totalWritten, totalBytes);
                    }

                    StopTransferTimer("写入", totalWritten);
                    _log(string.Format("[Firehose] {0} 写入完成: {1}", label, FormatSize(totalWritten)));
                    return true;
                }
                finally
                {
                    SimpleBufferPool.Return(buffer);
                }
            }
        }

        /// <summary>
        /// 智能写入 Sparse 镜像（只写有数据的 chunks，跳过 DONT_CARE）
        /// </summary>
        private async Task<bool> WriteSparsePartitionSmartAsync(int lun, long startSector, int sectorSize, string imagePath, string label, bool useOppoMode, CancellationToken ct)
        {
            // 在后台线程解析 Sparse 文件信息，避免 UI 卡住
            _logDetail(string.Format("[Sparse] 正在解析 {0}...", Path.GetFileName(imagePath)));
            
            SparseStream sparse = null;
            long totalExpandedSize = 0;
            long realDataSize = 0;
            List<Tuple<long, long>> dataRanges = null;
            
            try
            {
                // 在后台线程打开和解析 Sparse 文件
                await Task.Run(() =>
                {
                    sparse = SparseStream.Open(imagePath, _log);
                    totalExpandedSize = sparse.Length;
                    realDataSize = sparse.GetRealDataSize();
                    dataRanges = sparse.GetDataRanges();
                });
                
                // 主日志显示写入信息
                _log(string.Format("[Firehose] 写入: {0} ({1}) [Sparse]", label, FormatFileSize(realDataSize)));
                _logDetail(string.Format("[Sparse] 展开大小: {0:N0} MB, 实际数据: {1:N0} MB, 节省: {2:P1}", 
                    totalExpandedSize / 1024.0 / 1024.0, 
                    realDataSize / 1024.0 / 1024.0,
                    realDataSize > 0 ? (1.0 - (double)realDataSize / totalExpandedSize) : 1.0));
                
                if (dataRanges == null || dataRanges.Count == 0)
                {
                    // 空 Sparse 镜像: 使用 erase 命令清空分区
                    _logDetail(string.Format("[Sparse] 镜像无实际数据，擦除分区 {0}...", label));
                    long numSectors = totalExpandedSize / sectorSize;
                    bool eraseOk = await EraseSectorsAsync(lun, startSector, numSectors, ct);
                    if (eraseOk)
                        _logDetail(string.Format("[Sparse] 分区 {0} 擦除完成 ({1:F2} MB)", label, totalExpandedSize / 1024.0 / 1024.0));
                    else
                        _log(string.Format("[Sparse] 分区 {0} 擦除失败", label));
                    return eraseOk;
                }
                
                var sectorsPerChunk = _maxPayloadSize / sectorSize;
                var bytesPerChunk = sectorsPerChunk * sectorSize;
                var totalWritten = 0L;
                
                StartTransferTimer(realDataSize);
                
                // 使用 ArrayPool 减少 GC 压力
                var buffer = SimpleBufferPool.Rent(bytesPerChunk);
                try
                {
                    // 逐个写入有数据的范围
                    foreach (var range in dataRanges)
                    {
                        if (ct.IsCancellationRequested) return false;
                        
                        var rangeOffset = range.Item1;
                        var rangeSize = range.Item2;
                        var rangeStartSector = startSector + (rangeOffset / sectorSize);
                        
                        // 定位到该范围
                        sparse.Seek(rangeOffset, SeekOrigin.Begin);
                        var rangeWritten = 0L;
                        
                        while (rangeWritten < rangeSize)
                        {
                            if (ct.IsCancellationRequested) return false;
                            
                            var bytesToRead = (int)Math.Min(bytesPerChunk, rangeSize - rangeWritten);
                            var bytesRead = sparse.Read(buffer, 0, bytesToRead);
                            if (bytesRead == 0) break;
                            
                            // 补齐到扇区边界
                            var paddedSize = ((bytesRead + sectorSize - 1) / sectorSize) * sectorSize;
                            if (paddedSize > bytesRead)
                                Array.Clear(buffer, bytesRead, paddedSize - bytesRead);
                            
                            var sectorsToWrite = paddedSize / sectorSize;
                            var currentSector = rangeStartSector + (rangeWritten / sectorSize);
                            
                            if (!await WriteSectorsAsync(lun, currentSector, buffer, paddedSize, label, useOppoMode, ct))
                            {
                                _log(string.Format("[Firehose] 写入失败 @ sector {0}", currentSector));
                                return false;
                            }
                            
                            rangeWritten += bytesRead;
                            totalWritten += bytesRead;
                            
                            if (_progress != null)
                                _progress(totalWritten, realDataSize);
                        }
                    }
                    
                    StopTransferTimer("写入", totalWritten);
                    _logDetail(string.Format("[Firehose] {0} 完成: {1:N0} 字节 (跳过 {2:N0} MB)", 
                        label, totalWritten, (totalExpandedSize - realDataSize) / 1024.0 / 1024.0));
                    return true;
                }
                finally
                {
                    SimpleBufferPool.Return(buffer);
                }
            }
            finally
            {
                // 确保 SparseStream 被释放
                if (sparse != null)
                {
                    try { sparse.Dispose(); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// 写入扇区数据 (高速优化版)
        /// </summary>
        private async Task<bool> WriteSectorsAsync(int lun, long startSector, byte[] data, int length, string label, bool useOppoMode, CancellationToken ct)
        {
            int numSectors = length / _sectorSize;
            
            // 使用实际的分区名称，而不是硬编码的 GPT 值
            string xml = string.Format(
                "<?xml version=\"1.0\" ?><data>" +
                "<program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" " +
                "physical_partition_number=\"{2}\" start_sector=\"{3}\" label=\"{4}\" />" +
                "</data>",
                _sectorSize, numSectors, lun, startSector, label);

            // 发送命令 (使用异步写入)
            _port.DiscardInBuffer(); // 只清空输入缓冲区，不清空输出
            await _port.WriteAsync(Encoding.UTF8.GetBytes(xml), 0, xml.Length, ct);

            if (!await WaitForRawDataModeAsync(ct))
            {
                _log("[Firehose] Program 命令未确认");
                return false;
            }

            // 使用异步写入数据
            if (!await _port.WriteAsync(data, 0, length, ct))
            {
                _log("[Firehose] 数据写入失败");
                return false;
            }

            return await WaitForAckAsync(ct, 10);
        }

        /// <summary>
        /// 从文件刷写分区
        /// </summary>
        public async Task<bool> FlashPartitionFromFileAsync(string partitionName, string filePath, int lun, long startSector, IProgress<double> progress, CancellationToken ct, bool useVipMode = false)
        {
            if (!File.Exists(filePath))
            {
                _log("Firehose: 文件不存在 - " + filePath);
                return false;
            }

            // 检查是否为 Sparse 镜像
            bool isSparse = SparseStream.IsSparseFile(filePath);
            
            // Sparse 镜像使用智能写入，跳过 DONT_CARE
            if (isSparse)
            {
                return await FlashSparsePartitionSmartAsync(partitionName, filePath, lun, startSector, progress, ct, useVipMode);
            }
            
            // Raw 镜像的常规写入 (使用顺序访问提示优化读取)
            using (Stream sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, FILE_BUFFER_SIZE, FileOptions.SequentialScan))
            {
                long fileSize = sourceStream.Length;
                int numSectors = (int)Math.Ceiling((double)fileSize / _sectorSize);

                _log(string.Format("Firehose: 刷写 {0} -> {1} ({2}){3}", 
                    Path.GetFileName(filePath), partitionName, FormatFileSize(fileSize),
                    useVipMode ? " [VIP模式]" : ""));

                // VIP 模式使用伪装策略
                if (useVipMode)
                {
                    return await FlashPartitionVipModeAsync(partitionName, sourceStream, lun, startSector, numSectors, fileSize, progress, ct);
                }

                // 标准模式 (支持 OnePlus Token 认证)
                string xml;
                if (IsOnePlusAuthenticated)
                {
                    // OnePlus 设备需要附带认证 Token - 添加 label 和 read_back_verify 符合官方协议
                    xml = string.Format(
                        "<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                        "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                        "start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" " +
                        "read_back_verify=\"true\" token=\"{5}\" pk=\"{6}\"/></data>",
                        _sectorSize, numSectors, lun, startSector, partitionName,
                        OnePlusProgramToken, OnePlusProgramPk);
                    _log("[OnePlus] 使用认证令牌写入");
                }
                else
                {
                    // 标准模式 - 添加 label 属性符合官方协议
                    xml = string.Format(
                        "<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                        "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                        "start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" " +
                        "read_back_verify=\"true\"/></data>",
                        _sectorSize, numSectors, lun, startSector, partitionName);
                }

                _port.Write(Encoding.UTF8.GetBytes(xml));

                if (!await WaitForRawDataModeAsync(ct))
                {
                    _log("Firehose: Program 命令被拒绝");
                    return false;
                }

                return await SendStreamDataAsync(sourceStream, fileSize, progress, ct);
            }
        }

        /// <summary>
        /// 使用官方 NUM_DISK_SECTORS-N 负扇区格式刷写分区
        /// 用于 BackupGPT 等需要写入磁盘末尾的分区
        /// </summary>
        public async Task<bool> FlashPartitionWithNegativeSectorAsync(string partitionName, string filePath, int lun, long startSector, IProgress<double> progress, CancellationToken ct)
        {
            if (!File.Exists(filePath))
            {
                _log("Firehose: 文件不存在 - " + filePath);
                return false;
            }

            // 负扇区不支持 Sparse 镜像
            if (SparseStream.IsSparseFile(filePath))
            {
                _log("Firehose: 负扇区格式不支持 Sparse 镜像");
                return false;
            }
            
            // 使用顺序访问提示优化读取
            using (Stream sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, FILE_BUFFER_SIZE, FileOptions.SequentialScan))
            {
                long fileSize = sourceStream.Length;
                int numSectors = (int)Math.Ceiling((double)fileSize / _sectorSize);

                // 格式化负扇区: NUM_DISK_SECTORS-N. (官方格式，注意尾部的点)
                string startSectorStr;
                if (startSector < 0)
                {
                    startSectorStr = string.Format("NUM_DISK_SECTORS{0}.", startSector);
                }
                else
                {
                    startSectorStr = startSector.ToString();
                }

                _log(string.Format("Firehose: 刷写 {0} -> {1} ({2}) @ {3}", 
                    Path.GetFileName(filePath), partitionName, FormatFileSize(fileSize), startSectorStr));

                // 构造 program XML，使用官方负扇区格式
                string xml = string.Format(
                    "<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                    "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                    "start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" " +
                    "read_back_verify=\"true\"/></data>",
                    _sectorSize, numSectors, lun, startSectorStr, partitionName);

                _port.Write(Encoding.UTF8.GetBytes(xml));

                if (!await WaitForRawDataModeAsync(ct))
                {
                    _log("Firehose: Program 命令被拒绝 (负扇区格式)");
                    return false;
                }

                return await SendStreamDataAsync(sourceStream, fileSize, progress, ct);
            }
        }

        /// <summary>
        /// 智能刷写 Sparse 镜像（只写有数据的 chunks）
        /// </summary>
        private async Task<bool> FlashSparsePartitionSmartAsync(string partitionName, string filePath, int lun, long startSector, IProgress<double> progress, CancellationToken ct, bool useVipMode)
        {
            // 在后台线程解析 Sparse 文件信息，避免 UI 卡住
            _logDetail(string.Format("[Sparse] 正在解析 {0}...", Path.GetFileName(filePath)));
            
            SparseStream sparse = null;
            long totalExpandedSize = 0;
            long realDataSize = 0;
            List<Tuple<long, long>> dataRanges = null;
            
            try
            {
                // 在后台线程打开和解析 Sparse 文件（耗时操作）
                await Task.Run(() =>
                {
                    sparse = SparseStream.Open(filePath, _log);
                    totalExpandedSize = sparse.Length;
                    realDataSize = sparse.GetRealDataSize();
                    dataRanges = sparse.GetDataRanges();
                });
                
                // 主日志显示刷写信息
                _log(string.Format("Firehose: 刷写 {0} -> {1} ({2}) [Sparse]{3}", 
                    Path.GetFileName(filePath), partitionName, FormatFileSize(realDataSize), useVipMode ? " [VIP]" : ""));
                _logDetail(string.Format("[Sparse] 展开: {0:F2} MB, 实际数据: {1:F2} MB, 节省: {2:P1}", 
                    totalExpandedSize / 1024.0 / 1024.0, 
                    realDataSize / 1024.0 / 1024.0,
                    realDataSize > 0 ? (1.0 - (double)realDataSize / totalExpandedSize) : 1.0));
                
                if (dataRanges == null || dataRanges.Count == 0)
                {
                    // 空 Sparse 镜像 (如 userdata): 使用 erase 命令清空分区
                    _logDetail(string.Format("[Sparse] 镜像无实际数据，擦除分区 {0}...", partitionName));
                    long numSectors = totalExpandedSize / _sectorSize;
                    bool eraseOk = await EraseSectorsAsync(lun, startSector, numSectors, ct).ConfigureAwait(false);
                    if (progress != null) progress.Report(100.0);
                    if (eraseOk)
                        _logDetail(string.Format("[Sparse] 分区 {0} 擦除完成 ({1:F2} MB)", partitionName, totalExpandedSize / 1024.0 / 1024.0));
                    else
                        _log(string.Format("[Sparse] 分区 {0} 擦除失败", partitionName));
                    return eraseOk;
                }
                
                var totalWritten = 0L;
                var rangeIndex = 0;
                
                // 逐个写入有数据的范围
                foreach (var range in dataRanges)
                {
                    if (ct.IsCancellationRequested) return false;
                    rangeIndex++;
                    
                    var rangeOffset = range.Item1;
                    var rangeSize = range.Item2;
                    var rangeStartSector = startSector + (rangeOffset / _sectorSize);
                    var numSectors = (int)Math.Ceiling((double)rangeSize / _sectorSize);
                    
                    // 定位到该范围
                    sparse.Seek(rangeOffset, SeekOrigin.Begin);
                    
                    // 构建 program 命令
                    string xml;
                    if (useVipMode)
                    {
                        // VIP 模式伪装
                        xml = string.Format(
                            "<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                            "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                            "start_sector=\"{3}\" filename=\"gpt_main{2}.bin\" label=\"PrimaryGPT\" " +
                            "read_back_verify=\"true\"/></data>",
                            _sectorSize, numSectors, lun, rangeStartSector);
                    }
                    else if (IsOnePlusAuthenticated)
                    {
                        xml = string.Format(
                            "<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                            "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                            "start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" " +
                            "read_back_verify=\"true\" token=\"{5}\" pk=\"{6}\"/></data>",
                            _sectorSize, numSectors, lun, rangeStartSector, partitionName,
                            OnePlusProgramToken, OnePlusProgramPk);
                    }
                    else
                    {
                        xml = string.Format(
                            "<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                            "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                            "start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" " +
                            "read_back_verify=\"true\"/></data>",
                            _sectorSize, numSectors, lun, rangeStartSector, partitionName);
                    }
                    
                    // 使用异步写入
                    var xmlBytes = Encoding.UTF8.GetBytes(xml);
                    await _port.WriteAsync(xmlBytes, 0, xmlBytes.Length, ct).ConfigureAwait(false);
                    
                    if (!await WaitForRawDataModeAsync(ct).ConfigureAwait(false))
                    {
                        _logDetail(string.Format("[Sparse] 第 {0}/{1} 段 Program 命令被拒绝", rangeIndex, dataRanges.Count));
                        return false;
                    }
                    
                    // 发送该范围的数据 (使用优化的块大小以获得最佳性能)
                    var sent = 0L;
                    // 使用 4MB 块大小 (USB 2.0/3.0 最佳平衡)
                    const int OPTIMAL_CHUNK = 4 * 1024 * 1024;
                    var chunkSize = Math.Min(OPTIMAL_CHUNK, _maxPayloadSize);
                    var buffer = new byte[chunkSize];
                    DateTime lastProgressTime = DateTime.MinValue;
                    
                    while (sent < rangeSize)
                    {
                        if (ct.IsCancellationRequested) return false;
                        
                        var toRead = (int)Math.Min(chunkSize, rangeSize - sent);
                        var read = sparse.Read(buffer, 0, toRead);
                        if (read == 0) break;
                        
                        // 补齐到扇区边界
                        var paddedSize = ((read + _sectorSize - 1) / _sectorSize) * _sectorSize;
                        if (paddedSize > read)
                            Array.Clear(buffer, read, paddedSize - read);
                        
                        // 使用异步写入避免阻塞
                        await _port.WriteAsync(buffer, 0, paddedSize, ct).ConfigureAwait(false);
                        
                        sent += read;
                        totalWritten += read;
                        
                        // 节流进度报告
                        var now = DateTime.Now;
                        if (progress != null && realDataSize > 0 && (now - lastProgressTime).TotalMilliseconds > 200)
                        {
                            progress.Report(totalWritten * 100.0 / realDataSize);
                            lastProgressTime = now;
                        }
                    }
                    
                    if (!await WaitForAckAsync(ct, 30).ConfigureAwait(false))
                    {
                        _logDetail(string.Format("[Sparse] 第 {0}/{1} 段写入未确认", rangeIndex, dataRanges.Count));
                        return false;
                    }
                }
                
                _logDetail(string.Format("[Sparse] {0} 写入完成: {1:N0} 字节 (跳过 {2:N0} MB 空白)", 
                    partitionName, totalWritten, (totalExpandedSize - realDataSize) / 1024.0 / 1024.0));
                return true;
            }
            finally
            {
                // 确保 SparseStream 被释放
                if (sparse != null)
                {
                    try { sparse.Dispose(); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// VIP 模式刷写分区 (使用伪装策略)
        /// </summary>
        private async Task<bool> FlashPartitionVipModeAsync(string partitionName, Stream sourceStream, int lun, long startSector, int numSectors, long fileSize, IProgress<double> progress, CancellationToken ct)
        {
            // 获取伪装策略
            var strategies = GetDynamicSpoofStrategies(lun, startSector, partitionName, false);
            
            foreach (var strategy in strategies)
            {
                if (ct.IsCancellationRequested) break;

                string spoofLabel = string.IsNullOrEmpty(strategy.Label) ? partitionName : strategy.Label;
                string spoofFilename = string.IsNullOrEmpty(strategy.Filename) ? partitionName : strategy.Filename;

                _logDetail(string.Format("[VIP Write] 尝试伪装: {0}/{1}", spoofLabel, spoofFilename));
                PurgeBuffer();

                // VIP 模式 program 命令 - 添加 read_back_verify 符合官方协议
                string xml = string.Format(
                    "<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                    "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                    "start_sector=\"{3}\" filename=\"{4}\" label=\"{5}\" " +
                    "partofsingleimage=\"true\" read_back_verify=\"true\" sparse=\"false\"/></data>",
                    _sectorSize, numSectors, lun, startSector, spoofFilename, spoofLabel);

                _port.Write(Encoding.UTF8.GetBytes(xml));

                if (await WaitForRawDataModeAsync(ct))
                {
                    _logDetail(string.Format("[VIP Write] 伪装 {0} 成功，开始传输数据...", spoofLabel));
                    
                    // 每次尝试前重置流位置
                    sourceStream.Position = 0;
                    bool success = await SendStreamDataAsync(sourceStream, fileSize, progress, ct);
                    if (success)
                    {
                        _logDetail(string.Format("[VIP Write] {0} 写入成功", partitionName));
                        return true;
                    }
                }

                await Task.Delay(100, ct);
            }

            _log(string.Format("[VIP Write] {0} 所有伪装策略都失败", partitionName));
            return false;
        }

        /// <summary>
        /// 发送流数据 (极速优化版 - 使用双缓冲和更大的块大小)
        /// 优化点：
        /// 1. 增大块大小到 4MB (USB 3.0 最佳)
        /// 2. 双缓冲并行读写
        /// 3. 减少进度更新频率
        /// 4. 使用 Buffer.BlockCopy 代替 Array.Clear
        /// </summary>
        private async Task<bool> SendStreamDataAsync(Stream stream, long streamSize, IProgress<double> progress, CancellationToken ct)
        {
            long sent = 0;
            
            // 使用双缓冲实现读写并行
            // 块大小优化：USB 3.0 环境下 8MB 是最佳块大小，可减少 ACK 等待开销
            // USB 2.0/3.0 通用最佳值
            const int OPTIMAL_CHUNK_SIZE = 4 * 1024 * 1024; // 4MB 块
            int chunkSize = Math.Min(OPTIMAL_CHUNK_SIZE, _maxPayloadSize);
            
            byte[] buffer1 = new byte[chunkSize];
            byte[] buffer2 = new byte[chunkSize];
            byte[] currentBuffer = buffer1;
            byte[] nextBuffer = buffer2;
            
            double lastPercent = -1;
            DateTime lastProgressTime = DateTime.MinValue;
            const int PROGRESS_INTERVAL_MS = 200; // 降低进度更新频率到 200ms
            
            // 预读第一块
            int currentRead = stream.Read(currentBuffer, 0, (int)Math.Min(chunkSize, streamSize));
            if (currentRead <= 0) return await WaitForAckAsync(ct, 60);

            while (sent < streamSize)
            {
                if (ct.IsCancellationRequested) return false;

                // 计算剩余数据
                long remaining = streamSize - sent - currentRead;
                
                // 启动下一块的异步读取（如果还有数据）
                Task<int> readTask = null;
                if (remaining > 0)
                {
                    int nextToRead = (int)Math.Min(chunkSize, remaining);
                    readTask = stream.ReadAsync(nextBuffer, 0, nextToRead, ct);
                }

                // 补齐到扇区边界
                int toWrite = currentRead;
                if (currentRead % _sectorSize != 0)
                {
                    toWrite = ((currentRead / _sectorSize) + 1) * _sectorSize;
                    Array.Clear(currentBuffer, currentRead, toWrite - currentRead);
                }

                // 发送当前块 (使用同步写入提高效率)
                try
                {
                    _port.Write(currentBuffer, 0, toWrite);
                }
                catch (Exception ex)
                {
                    _log(string.Format("Firehose: 数据写入失败 - {0}", ex.Message));
                    return false;
                }

                sent += currentRead;

                // 节流进度报告：每 200ms 或每 1% 更新一次
                var now = DateTime.Now;
                double currentPercent = (100.0 * sent / streamSize);
                if (currentPercent > lastPercent + 1.0 || (now - lastProgressTime).TotalMilliseconds > PROGRESS_INTERVAL_MS)
                {
                    if (_progress != null) _progress(sent, streamSize);
                    if (progress != null) progress.Report(currentPercent);
                    
                    lastPercent = currentPercent;
                    lastProgressTime = now;
                }

                // 等待下一块读取完成并交换缓冲区
                if (readTask != null)
                {
                    currentRead = await readTask;
                    if (currentRead <= 0) break;
                    
                    // 交换缓冲区
                    var temp = currentBuffer;
                    currentBuffer = nextBuffer;
                    nextBuffer = temp;
                }
                else
                {
                    break;
                }
            }

            // 确保最后一次进度报告
            if (_progress != null) _progress(streamSize, streamSize);
            if (progress != null) progress.Report(100.0);

            // 等待最终 ACK (减少重试次数，加快响应)
            return await WaitForAckAsync(ct, 60);
        }

        #endregion

        #region 擦除分区

        /// <summary>
        /// 擦除分区
        /// </summary>
        public async Task<bool> ErasePartitionAsync(PartitionInfo partition, CancellationToken ct = default(CancellationToken), bool useVipMode = false)
        {
            _log(string.Format("[Firehose] 擦除分区: {0}{1}", partition.Name, useVipMode ? " [VIP模式]" : ""));

            if (useVipMode)
            {
                return await ErasePartitionVipModeAsync(partition, ct);
            }

            var xml = string.Format(
                "<?xml version=\"1.0\" ?><data><erase SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                "start_sector=\"{3}\" /></data>",
                _sectorSize, partition.NumSectors, partition.Lun, partition.StartSector);

            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));

            if (await WaitForAckAsync(ct))
            {
                _log(string.Format("[Firehose] 分区 {0} 擦除完成", partition.Name));
                return true;
            }

            _log("[Firehose] 擦除失败");
            return false;
        }

        /// <summary>
        /// VIP 模式擦除分区
        /// </summary>
        private async Task<bool> ErasePartitionVipModeAsync(PartitionInfo partition, CancellationToken ct)
        {
            var strategies = GetDynamicSpoofStrategies(partition.Lun, partition.StartSector, partition.Name, false);

            foreach (var strategy in strategies)
            {
                if (ct.IsCancellationRequested) break;

                string spoofLabel = string.IsNullOrEmpty(strategy.Label) ? partition.Name : strategy.Label;
                string spoofFilename = string.IsNullOrEmpty(strategy.Filename) ? partition.Name : strategy.Filename;

                _log(string.Format("[VIP Erase] 尝试伪装: {0}/{1}", spoofLabel, spoofFilename));
                PurgeBuffer();

                var xml = string.Format(
                    "<?xml version=\"1.0\" ?><data><erase SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                    "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                    "start_sector=\"{3}\" label=\"{4}\" filename=\"{5}\" /></data>",
                    _sectorSize, partition.NumSectors, partition.Lun, partition.StartSector, spoofLabel, spoofFilename);

                _port.Write(Encoding.UTF8.GetBytes(xml));

                if (await WaitForAckAsync(ct))
                {
                    _log(string.Format("[VIP Erase] {0} 擦除成功", partition.Name));
                    return true;
                }

                await Task.Delay(100, ct);
            }

            _log(string.Format("[VIP Erase] {0} 所有伪装策略都失败", partition.Name));
            return false;
        }

        /// <summary>
        /// 擦除分区 (参数版本)
        /// </summary>
        public async Task<bool> ErasePartitionAsync(string partitionName, int lun, long startSector, long numSectors, CancellationToken ct, bool useVipMode = false)
        {
            _log(string.Format("Firehose: 擦除分区 {0}{1}", partitionName, useVipMode ? " [VIP模式]" : ""));

            if (useVipMode)
            {
                var partition = new PartitionInfo
                {
                    Name = partitionName,
                    Lun = lun,
                    StartSector = startSector,
                    NumSectors = numSectors,
                    SectorSize = _sectorSize
                };
                return await ErasePartitionVipModeAsync(partition, ct);
            }

            string xml = string.Format(
                "<?xml version=\"1.0\"?><data><erase SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                "start_sector=\"{3}\"/></data>",
                _sectorSize, numSectors, lun, startSector);

            _port.Write(Encoding.UTF8.GetBytes(xml));
            bool success = await WaitForAckAsync(ct, 100);
            _log(success ? "Firehose: 擦除成功" : "Firehose: 擦除失败");

            return success;
        }

        /// <summary>
        /// 擦除指定扇区范围 (简化版)
        /// </summary>
        public async Task<bool> EraseSectorsAsync(int lun, long startSector, long numSectors, CancellationToken ct)
        {
            string xml = string.Format(
                "<?xml version=\"1.0\"?><data><erase SECTOR_SIZE_IN_BYTES=\"{0}\" " +
                "num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" " +
                "start_sector=\"{3}\"/></data>",
                _sectorSize, numSectors, lun, startSector);

            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));
            return await WaitForAckAsync(ct, 120);
        }

        #endregion

        #region 设备控制

        /// <summary>
        /// 重启设备
        /// </summary>
        public async Task<bool> ResetAsync(string mode = "reset", CancellationToken ct = default(CancellationToken))
        {
            _log(string.Format("[Firehose] 重启设备 (模式: {0})", mode));

            var xml = string.Format("<?xml version=\"1.0\" ?><data><power value=\"{0}\" /></data>", mode);
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));

            return await WaitForAckAsync(ct);
        }

        /// <summary>
        /// 关机
        /// </summary>
        public async Task<bool> PowerOffAsync(CancellationToken ct = default(CancellationToken))
        {
            _log("[Firehose] 关机...");

            string xml = "<?xml version=\"1.0\"?><data><power value=\"off\"/></data>";
            _port.Write(Encoding.UTF8.GetBytes(xml));

            return await WaitForAckAsync(ct);
        }

        /// <summary>
        /// 进入 EDL 模式
        /// </summary>
        public async Task<bool> RebootToEdlAsync(CancellationToken ct = default(CancellationToken))
        {
            string xml = "<?xml version=\"1.0\"?><data><power value=\"edl\"/></data>";
            _port.Write(Encoding.UTF8.GetBytes(xml));

            return await WaitForAckAsync(ct);
        }

        /// <summary>
        /// 设置活动槽位 (A/B) - 完整实现
        /// 优先使用 setactiveslot 命令，失败则回退到 patch 方式
        /// </summary>
        public async Task<bool> SetActiveSlotAsync(string slot, CancellationToken ct = default(CancellationToken))
        {
            slot = slot?.ToLower() ?? "a";
            if (slot != "a" && slot != "b")
            {
                _log("[Firehose] 错误: 槽位必须是 'a' 或 'b'");
                return false;
            }

            _log(string.Format("[Firehose] 设置活动 Slot: {0}", slot));

            // 方法 1: 尝试 setactiveslot 命令 (部分设备支持)
            var xml = string.Format("<?xml version=\"1.0\" ?><data><setactiveslot slot=\"{0}\" /></data>", slot);
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));

            if (await WaitForAckAsync(ct, 3))
            {
                _log("[Firehose] setactiveslot 命令成功");
                return true;
            }

            // 方法 2: 回退到 patch 方式修改 GPT 属性
            _log("[Firehose] setactiveslot 不支持，使用 patch 方式...");
            return await SetActiveSlotViaPatchAsync(slot, ct);
        }

        /// <summary>
        /// 通过 patch 命令修改 GPT 分区属性来设置活动槽位
        /// </summary>
        private async Task<bool> SetActiveSlotViaPatchAsync(string targetSlot, CancellationToken ct)
        {
            if (_cachedPartitions == null || _cachedPartitions.Count == 0)
            {
                _log("[Firehose] 错误: 没有缓存的分区信息，请先读取分区表");
                return false;
            }

            // 需要修改的核心 A/B 分区 (按启动顺序)
            string[] coreAbPartitions = {
                "boot", "dtbo", "vbmeta", "vendor_boot", "init_boot"
            };

            // 可选的 A/B 分区
            string[] optionalAbPartitions = {
                "system", "vendor", "product", "odm", "system_ext",
                "vendor_dlkm", "odm_dlkm", "system_dlkm"
            };

            string activeSuffix = "_" + targetSlot;
            string inactiveSuffix = targetSlot == "a" ? "_b" : "_a";
            
            int patchCount = 0;
            int failCount = 0;

            // 1. 处理核心分区 (必须成功)
            foreach (var baseName in coreAbPartitions)
            {
                var result = await PatchSlotPairAsync(baseName, activeSuffix, inactiveSuffix, ct);
                if (result > 0) patchCount += result;
                else if (result < 0) failCount++;
            }

            // 2. 处理可选分区 (失败不影响整体)
            foreach (var baseName in optionalAbPartitions)
            {
                var result = await PatchSlotPairAsync(baseName, activeSuffix, inactiveSuffix, ct, true);
                if (result > 0) patchCount += result;
            }

            if (patchCount == 0)
            {
                _log("[Firehose] 未找到任何 A/B 分区");
                return false;
            }

            _log(string.Format("[Firehose] 已修改 {0} 个分区属性", patchCount));

            // 3. 修复 GPT 以保存更改
            _log("[Firehose] 正在保存 GPT 更改...");
            bool fixResult = await FixGptAsync(-1, false, ct);
            
            if (fixResult)
                _log(string.Format("[Firehose] 活动槽位已切换到: {0}", targetSlot));
            else
                _log("[Firehose] 警告: GPT 修复失败，更改可能未保存");

            return fixResult && failCount == 0;
        }

        /// <summary>
        /// 修改一对 A/B 分区的属性
        /// </summary>
        /// <returns>修改的分区数量，-1 表示失败</returns>
        private async Task<int> PatchSlotPairAsync(string baseName, string activeSuffix, string inactiveSuffix, 
            CancellationToken ct, bool optional = false)
        {
            int count = 0;

            // 激活目标槽位
            var activePart = _cachedPartitions.Find(p => 
                p.Name.Equals(baseName + activeSuffix, StringComparison.OrdinalIgnoreCase));
            
            if (activePart != null)
            {
                ulong newAttr = SetSlotFlags(activePart.Attributes, active: true, priority: 3, successful: false, unbootable: false);
                
                if (await PatchPartitionAttributesAsync(activePart, newAttr, ct))
                {
                    _logDetail(string.Format("[Firehose] {0}: 已激活 (attr=0x{1:X16})", activePart.Name, newAttr));
                    count++;
                }
                else if (!optional)
                {
                    _log(string.Format("[Firehose] 错误: 无法修改 {0} 属性", activePart.Name));
                    return -1;
                }
            }

            // 停用另一个槽位
            var inactivePart = _cachedPartitions.Find(p => 
                p.Name.Equals(baseName + inactiveSuffix, StringComparison.OrdinalIgnoreCase));
            
            if (inactivePart != null)
            {
                ulong newAttr = SetSlotFlags(inactivePart.Attributes, active: false, priority: 1, successful: null, unbootable: null);
                
                if (await PatchPartitionAttributesAsync(inactivePart, newAttr, ct))
                {
                    _logDetail(string.Format("[Firehose] {0}: 已停用 (attr=0x{1:X16})", inactivePart.Name, newAttr));
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 使用 patch 命令修改分区属性
        /// </summary>
        private async Task<bool> PatchPartitionAttributesAsync(PartitionInfo partition, ulong newAttributes, CancellationToken ct)
        {
            // GPT Entry 结构 (128 字节):
            // Offset 0-15:  Type GUID (16 bytes)
            // Offset 16-31: Unique GUID (16 bytes)
            // Offset 32-39: Start LBA (8 bytes)
            // Offset 40-47: End LBA (8 bytes)
            // Offset 48-55: Attributes (8 bytes) <-- 我们要修改这里
            // Offset 56-127: Name (72 bytes)
            
            const int GPT_ENTRY_SIZE = 128;
            const int ATTR_OFFSET_IN_ENTRY = 48;
            
            // 将属性转换为小端字节序的十六进制字符串
            byte[] attrBytes = BitConverter.GetBytes(newAttributes);
            string attrHex = BitConverter.ToString(attrBytes).Replace("-", "");

            // 方法 1: 使用分区名称的 patch (部分设备支持)
            string xml1 = string.Format(
                "<?xml version=\"1.0\" ?><data>" +
                "<patch SECTOR_SIZE_IN_BYTES=\"{0}\" byte_offset=\"{1}\" " +
                "filename=\"{2}\" physical_partition_number=\"{3}\" " +
                "size_in_bytes=\"8\" start_sector=\"0\" value=\"{4}\" what=\"attributes\" />" +
                "</data>",
                _sectorSize, ATTR_OFFSET_IN_ENTRY, partition.Name, partition.Lun, attrHex);

            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml1));

            if (await WaitForAckAsync(ct, 3))
                return true;

            // 方法 2: 使用精确的 GPT 条目位置 (需要 EntryIndex)
            if (partition.EntryIndex >= 0)
            {
                // 计算 GPT 条目在磁盘上的精确位置
                // GPT 条目起始于 LBA 2 (通常)，每个条目 128 字节
                long gptEntriesStartByte = partition.GptEntriesStartSector * _sectorSize;
                long entryByteOffset = gptEntriesStartByte + (partition.EntryIndex * GPT_ENTRY_SIZE);
                long attrByteOffset = entryByteOffset + ATTR_OFFSET_IN_ENTRY;
                
                // 计算扇区和字节偏移
                long startSector = attrByteOffset / _sectorSize;
                int byteOffset = (int)(attrByteOffset % _sectorSize);

                _logDetail(string.Format("[Firehose] Patch {0}: Entry#{1}, Sector={2}, Offset={3}", 
                    partition.Name, partition.EntryIndex, startSector, byteOffset));

                string xml2 = string.Format(
                    "<?xml version=\"1.0\" ?><data>" +
                    "<patch SECTOR_SIZE_IN_BYTES=\"{0}\" byte_offset=\"{1}\" " +
                    "filename=\"DISK\" physical_partition_number=\"{2}\" " +
                    "size_in_bytes=\"8\" start_sector=\"{3}\" value=\"{4}\" />" +
                    "</data>",
                    _sectorSize, byteOffset, partition.Lun, startSector, attrHex);

                PurgeBuffer();
                _port.Write(Encoding.UTF8.GetBytes(xml2));

                if (await WaitForAckAsync(ct, 3))
                    return true;

                _logDetail(string.Format("[Firehose] 方法 2 失败: {0}", partition.Name));
            }
            else
            {
                _logDetail(string.Format("[Firehose] {0} 缺少 EntryIndex，跳过精确 patch", partition.Name));
            }

            // 方法 3: 尝试使用 setactivepartition 命令 (某些设备支持)
            string xml3 = string.Format(
                "<?xml version=\"1.0\" ?><data>" +
                "<setactivepartition name=\"{0}\" slot=\"{1}\" /></data>",
                partition.Name.TrimEnd('_', 'a', 'b'),
                partition.Name.EndsWith("_a") ? "a" : "b");

            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml3));

            if (await WaitForAckAsync(ct, 2))
                return true;

            _logDetail(string.Format("[Firehose] 所有 patch 方法均失败: {0}", partition.Name));
            return false;
        }

        #region A/B 槽位属性位操作

        /// <summary>
        /// 设置槽位标志
        /// </summary>
        /// <param name="attr">原始属性</param>
        /// <param name="active">是否激活 (null = 不修改)</param>
        /// <param name="priority">优先级 0-3 (null = 不修改)</param>
        /// <param name="successful">启动成功标志 (null = 不修改)</param>
        /// <param name="unbootable">不可启动标志 (null = 不修改)</param>
        private ulong SetSlotFlags(ulong attr, bool? active = null, int? priority = null, 
            bool? successful = null, bool? unbootable = null)
        {
            // A/B 属性位布局 (在 Attributes 字段的第 48-55 位):
            // Bit 48-49: Priority (0-3)
            // Bit 50: Active
            // Bit 51: Successful
            // Bit 52: Unbootable
            
            const ulong PRIORITY_MASK = 3UL << 48;
            const ulong ACTIVE_BIT = 1UL << 50;
            const ulong SUCCESSFUL_BIT = 1UL << 51;
            const ulong UNBOOTABLE_BIT = 1UL << 52;

            if (priority.HasValue)
            {
                attr &= ~PRIORITY_MASK;
                attr |= ((ulong)(priority.Value & 3) << 48);
            }

            if (active.HasValue)
            {
                if (active.Value)
                    attr |= ACTIVE_BIT;
                else
                    attr &= ~ACTIVE_BIT;
            }

            if (successful.HasValue)
            {
                if (successful.Value)
                    attr |= SUCCESSFUL_BIT;
                else
                    attr &= ~SUCCESSFUL_BIT;
            }

            if (unbootable.HasValue)
            {
                if (unbootable.Value)
                    attr |= UNBOOTABLE_BIT;
                else
                    attr &= ~UNBOOTABLE_BIT;
            }

            return attr;
        }

        /// <summary>
        /// 检查槽位是否激活
        /// </summary>
        public bool IsSlotActive(ulong attributes)
        {
            return (attributes & (1UL << 50)) != 0;
        }

        /// <summary>
        /// 获取槽位优先级
        /// </summary>
        public int GetSlotPriority(ulong attributes)
        {
            return (int)((attributes >> 48) & 3);
        }

        #endregion

        /// <summary>
        /// 修复 GPT
        /// </summary>
        public async Task<bool> FixGptAsync(int lun = -1, bool growLastPartition = true, CancellationToken ct = default(CancellationToken))
        {
            string lunValue = (lun == -1) ? "all" : lun.ToString();
            string growValue = growLastPartition ? "1" : "0";

            _log(string.Format("[Firehose] 修复 GPT (LUN={0})...", lunValue));
            var xml = string.Format("<?xml version=\"1.0\" ?><data><fixgpt lun=\"{0}\" grow_last_partition=\"{1}\" /></data>", lunValue, growValue);
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));

            if (await WaitForAckAsync(ct, 10))
            {
                _log("[Firehose] GPT 修复成功");
                return true;
            }

            _log("[Firehose] GPT 修复失败");
            return false;
        }

        /// <summary>
        /// 设置启动 LUN
        /// </summary>
        public async Task<bool> SetBootLunAsync(int lun, CancellationToken ct = default(CancellationToken))
        {
            _log(string.Format("[Firehose] 设置启动 LUN: {0}", lun));
            var xml = string.Format("<?xml version=\"1.0\" ?><data><setbootablestoragedrive value=\"{0}\" /></data>", lun);
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));
            return await WaitForAckAsync(ct);
        }

        #region UFS Provision (存储配置)

        /// <summary>
        /// Provision 功能开关 (默认禁用，因为这是危险操作)
        /// 必须显式设置为 true 才能使用 Provision 功能
        /// </summary>
        public bool EnableProvision { get; set; } = false;

        /// <summary>
        /// 发送 UFS 全局配置 (Provision 第一步)
        /// 警告: 这是危险操作，错误配置可能导致设备变砖!
        /// </summary>
        public async Task<bool> SendUfsGlobalConfigAsync(
            byte bNumberLU, byte bBootEnable, byte bDescrAccessEn, byte bInitPowerMode,
            byte bHighPriorityLUN, byte bSecureRemovalType, byte bInitActiveICCLevel,
            short wPeriodicRTCUpdate, byte bConfigDescrLock,
            CancellationToken ct = default(CancellationToken))
        {
            if (!EnableProvision)
            {
                _log("[Provision] 功能已禁用，请先设置 EnableProvision = true");
                return false;
            }

            _log(string.Format("[Provision] 发送 UFS 全局配置 (LUN数={0}, Boot={1})...", bNumberLU, bBootEnable));
            
            var xml = string.Format(
                "<?xml version=\"1.0\" ?><data><ufs bNumberLU=\"{0}\" bBootEnable=\"{1}\" " +
                "bDescrAccessEn=\"{2}\" bInitPowerMode=\"{3}\" bHighPriorityLUN=\"{4}\" " +
                "bSecureRemovalType=\"{5}\" bInitActiveICCLevel=\"{6}\" wPeriodicRTCUpdate=\"{7}\" " +
                "bConfigDescrLock=\"{8}\" /></data>",
                bNumberLU, bBootEnable, bDescrAccessEn, bInitPowerMode,
                bHighPriorityLUN, bSecureRemovalType, bInitActiveICCLevel,
                wPeriodicRTCUpdate, bConfigDescrLock);
            
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));
            
            bool ack = await WaitForAckAsync(ct, 30); // 30秒超时
            if (ack)
                _logDetail("[Provision] 全局配置已发送");
            else
                _log("[Provision] 全局配置发送失败");
            
            return ack;
        }

        /// <summary>
        /// 发送 UFS LUN 配置 (Provision 第二步，每个 LUN 调用一次)
        /// 警告: 这是危险操作，错误配置可能导致设备变砖!
        /// </summary>
        public async Task<bool> SendUfsLunConfigAsync(
            byte luNum, byte bLUEnable, byte bBootLunID, long sizeInKB,
            byte bDataReliability, byte bLUWriteProtect, byte bMemoryType,
            byte bLogicalBlockSize, byte bProvisioningType, short wContextCapabilities,
            CancellationToken ct = default(CancellationToken))
        {
            if (!EnableProvision)
            {
                _log("[Provision] 功能已禁用");
                return false;
            }

            string sizeStr = sizeInKB >= 1024 * 1024 ? 
                string.Format("{0:F1}GB", sizeInKB / (1024.0 * 1024)) : 
                string.Format("{0}MB", sizeInKB / 1024);
            
            _logDetail(string.Format("[Provision] 配置 LUN{0}: {1}, 启用={2}, Boot={3}",
                luNum, sizeStr, bLUEnable, bBootLunID));
            
            var xml = string.Format(
                "<?xml version=\"1.0\" ?><data><ufs LUNum=\"{0}\" bLUEnable=\"{1}\" " +
                "bBootLunID=\"{2}\" size_in_kb=\"{3}\" bDataReliability=\"{4}\" " +
                "bLUWriteProtect=\"{5}\" bMemoryType=\"{6}\" bLogicalBlockSize=\"{7}\" " +
                "bProvisioningType=\"{8}\" wContextCapabilities=\"{9}\" /></data>",
                luNum, bLUEnable, bBootLunID, sizeInKB,
                bDataReliability, bLUWriteProtect, bMemoryType,
                bLogicalBlockSize, bProvisioningType, wContextCapabilities);
            
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));
            
            return await WaitForAckAsync(ct, 30);
        }

        /// <summary>
        /// 提交 UFS Provision 配置 (最终步骤)
        /// 警告: 此操作可能是 OTP (一次性编程)，一旦执行无法撤销!
        /// </summary>
        public async Task<bool> CommitUfsProvisionAsync(CancellationToken ct = default(CancellationToken))
        {
            if (!EnableProvision)
            {
                _log("[Provision] 功能已禁用，无法提交配置");
                return false;
            }

            _log("[Provision] 提交 UFS 配置 (此操作可能不可逆!)...");
            
            var xml = "<?xml version=\"1.0\" ?><data><ufs commit=\"true\" /></data>";
            
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));
            
            bool ack = await WaitForAckAsync(ct, 60); // 60秒超时，Provision 可能较慢
            if (ack)
                _log("[Provision] UFS 配置已提交成功");
            else
                _log("[Provision] UFS 配置提交失败");
            
            return ack;
        }

        /// <summary>
        /// 读取当前 UFS 存储信息 (如果设备支持)
        /// </summary>
        public async Task<bool> GetStorageInfoAsync(CancellationToken ct = default(CancellationToken))
        {
            _log("[Provision] 读取存储信息...");
            
            // 尝试使用 getstorageinfo 命令 (不是所有设备都支持)
            var xml = "<?xml version=\"1.0\" ?><data><getstorageinfo /></data>";
            
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));
            
            // 等待响应
            bool result = await WaitForAckAsync(ct, 10);
            if (!result)
                _logDetail("[Provision] getstorageinfo 命令可能不被支持");
            
            return result;
        }

        #endregion

        /// <summary>
        /// 应用单个补丁 (支持官方 NUM_DISK_SECTORS-N 负扇区格式)
        /// </summary>
        public async Task<bool> ApplyPatchAsync(int lun, long startSector, int byteOffset, int sizeInBytes, string value, CancellationToken ct = default(CancellationToken))
        {
            // 跳过空补丁
            if (string.IsNullOrEmpty(value) || sizeInBytes == 0)
                return true;

            // 格式化 start_sector: 负数使用官方格式 NUM_DISK_SECTORS-N.
            string startSectorStr;
            if (startSector < 0)
            {
                startSectorStr = string.Format("NUM_DISK_SECTORS{0}.", startSector);
                _logDetail(string.Format("[Patch] LUN{0} Sector {1} Offset{2} Size{3}", lun, startSectorStr, byteOffset, sizeInBytes));
            }
            else
            {
                startSectorStr = startSector.ToString();
                _logDetail(string.Format("[Patch] LUN{0} Sector{1} Offset{2} Size{3}", lun, startSector, byteOffset, sizeInBytes));
            }

            string xml = string.Format(
                "<?xml version=\"1.0\" ?><data>\n" +
                "<patch SECTOR_SIZE_IN_BYTES=\"{0}\" byte_offset=\"{1}\" filename=\"DISK\" " +
                "physical_partition_number=\"{2}\" size_in_bytes=\"{3}\" start_sector=\"{4}\" value=\"{5}\" />\n</data>\n",
                _sectorSize, byteOffset, lun, sizeInBytes, startSectorStr, value);

            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes(xml));
            return await WaitForAckAsync(ct);
        }

        /// <summary>
        /// 从 Patch XML 文件应用所有补丁
        /// </summary>
        public async Task<int> ApplyPatchXmlAsync(string patchXmlPath, CancellationToken ct = default(CancellationToken))
        {
            if (!System.IO.File.Exists(patchXmlPath))
            {
                _log(string.Format("[Firehose] Patch 文件不存在: {0}", patchXmlPath));
                return 0;
            }

            _logDetail(string.Format("[Firehose] 应用 Patch: {0}", System.IO.Path.GetFileName(patchXmlPath)));

            int successCount = 0;
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(patchXmlPath);
                var root = doc.Root;
                if (root == null) return 0;

                foreach (var elem in root.Elements("patch"))
                {
                    if (ct.IsCancellationRequested) break;

                    string value = elem.Attribute("value")?.Value ?? "";
                    if (string.IsNullOrEmpty(value)) continue;

                    int lun = 0;
                    int.TryParse(elem.Attribute("physical_partition_number")?.Value ?? "0", out lun);
                    
                    long startSector = 0;
                    var startSectorAttr = elem.Attribute("start_sector")?.Value ?? "0";
                    
                    // 处理 NUM_DISK_SECTORS-N 形式的负扇区 (保持负数，让 ApplyPatchAsync 使用官方格式发送)
                    if (startSectorAttr.Contains("NUM_DISK_SECTORS"))
                    {
                        if (startSectorAttr.Contains("-"))
                        {
                            string offsetStr = startSectorAttr.Split('-')[1].TrimEnd('.');
                            long offset;
                            if (long.TryParse(offsetStr, out offset))
                                startSector = -offset; // 负数，ApplyPatchAsync 会使用官方格式
                        }
                        else
                        {
                            startSector = -1;
                        }
                        // 不再尝试客户端转换，直接使用负数让设备计算
                    }
                    else if (startSectorAttr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        long.TryParse(startSectorAttr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out startSector);
                    }
                    else
                    {
                        // 移除可能的尾随点号 (如 "5.")
                        if (startSectorAttr.EndsWith("."))
                            startSectorAttr = startSectorAttr.Substring(0, startSectorAttr.Length - 1);
                        long.TryParse(startSectorAttr, out startSector);
                    }

                    int byteOffset = 0;
                    int.TryParse(elem.Attribute("byte_offset")?.Value ?? "0", out byteOffset);

                    int sizeInBytes = 0;
                    int.TryParse(elem.Attribute("size_in_bytes")?.Value ?? "0", out sizeInBytes);

                    if (sizeInBytes == 0) continue;

                    if (await ApplyPatchAsync(lun, startSector, byteOffset, sizeInBytes, value, ct))
                        successCount++;
                    else
                        _logDetail(string.Format("[Patch] 失败: LUN{0} Sector{1}", lun, startSector));
                }
            }
            catch (Exception ex)
            {
                _log(string.Format("[Patch] 应用异常: {0}", ex.Message));
            }

            _logDetail(string.Format("[Patch] {0} 成功应用 {1} 个补丁", System.IO.Path.GetFileName(patchXmlPath), successCount));
            return successCount;
        }

        /// <summary>
        /// Ping/NOP 测试连接
        /// </summary>
        public async Task<bool> PingAsync(CancellationToken ct = default(CancellationToken))
        {
            PurgeBuffer();
            _port.Write(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" ?><data><nop /></data>"));
            return await WaitForAckAsync(ct, 3);
        }

        #endregion

        #region 分区缓存

        public void SetPartitionCache(List<PartitionInfo> partitions)
        {
            _cachedPartitions = partitions;
        }

        public PartitionInfo FindPartition(string name)
        {
            if (_cachedPartitions == null) return null;
            foreach (var p in _cachedPartitions)
            {
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        #endregion

        #region 通信方法

        private async Task<XElement> ProcessXmlResponseAsync(CancellationToken ct, int timeoutMs = 5000)
        {
            try
            {
                var sb = new StringBuilder();
                var startTime = DateTime.Now;
                int emptyReads = 0;

                while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
                {
                    if (ct.IsCancellationRequested) return null;

                    int available = _port.BytesToRead;
                    if (available > 0)
                    {
                        emptyReads = 0;
                        byte[] buffer = new byte[Math.Min(available, 65536)];
                        int read = _port.Read(buffer, 0, buffer.Length);
                        if (read > 0)
                        {
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));

                            var content = sb.ToString();

                            // 提取设备日志 (详细日志，不在主界面显示)
                            if (content.Contains("<log "))
                            {
                                var logMatches = Regex.Matches(content, @"<log value=""([^""]*)""\s*/>");
                                foreach (Match m in logMatches)
                                {
                                    if (m.Groups.Count > 1)
                                        _logDetail("[Device] " + m.Groups[1].Value);
                                }
                            }

                            if (content.Contains("</data>") || content.Contains("<response"))
                            {
                                int start = content.IndexOf("<response");
                                if (start >= 0)
                                {
                                    int end = content.IndexOf("/>", start);
                                    if (end > start)
                                    {
                                        var respXml = content.Substring(start, end - start + 2);
                                        return XElement.Parse(respXml);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        emptyReads++;
                        // 使用自旋等待代替 Task.Delay，减少上下文切换
                        if (emptyReads < 20)
                            Thread.SpinWait(500);  // 快速自旋
                        else if (emptyReads < 100)
                            Thread.Yield();  // 让出时间片
                        else if (emptyReads < 500)
                            await Task.Yield();  // 异步让出
                        else
                            await Task.Delay(1, ct);  // 短暂等待
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消操作是正常的（包括 TaskCanceledException），不记录日志
                return null;
            }
            catch (Exception ex)
            {
                _logDetail(string.Format("[Firehose] 响应解析异常: {0}", ex.Message));
            }
            return null;
        }

        private async Task<bool> WaitForAckAsync(CancellationToken ct, int maxRetries = 50)
        {
            int emptyCount = 0;
            int totalWaitMs = 0;
            const int MAX_WAIT_MS = 30000; // 最大等待 30 秒
            
            for (int i = 0; i < maxRetries && totalWaitMs < MAX_WAIT_MS; i++)
            {
                if (ct.IsCancellationRequested) return false;

                var resp = await ProcessXmlResponseAsync(ct);
                if (resp != null)
                {
                    emptyCount = 0; // 重置空响应计数
                    var valAttr = resp.Attribute("value");
                    string val = valAttr != null ? valAttr.Value : "";

                    if (val.Equals("ACK", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("true", StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (val.Equals("NAK", StringComparison.OrdinalIgnoreCase))
                    {
                        var errorAttr = resp.Attribute("error");
                        string errorDesc = errorAttr != null ? errorAttr.Value : resp.ToString();
                        string message, suggestion;
                        bool isFatal, canRetry;
                        FirehoseErrorHelper.ParseNakError(errorDesc, out message, out suggestion, out isFatal, out canRetry);
                        _log(string.Format("[Firehose] NAK: {0}", message));
                        if (!string.IsNullOrEmpty(suggestion))
                            _log(string.Format("[Firehose] {0}", suggestion));
                        return false;
                    }
                }
                else
                {
                    // 空响应时使用自旋等待 + 渐进式退避
                    emptyCount++;
                    int waitMs;
                    if (emptyCount < 50)
                    {
                        // 前 50 次快速自旋 (约 0.5ms 每次)
                        Thread.SpinWait(1000);
                        waitMs = 0;
                    }
                    else if (emptyCount < 200)
                    {
                        // 中等等待 (1ms)
                        await Task.Yield(); // 让出时间片但不真正等待
                        waitMs = 1;
                    }
                    else
                    {
                        // 较长等待 (5ms)
                        await Task.Delay(5, ct);
                        waitMs = 5;
                    }
                    totalWaitMs += waitMs;
                }
            }

            _log("[Firehose] 等待 ACK 超时");
            return false;
        }

        /// <summary>
        /// 接收数据响应 (高速流水线版 - 极速优化)
        /// 优化点：
        /// 1. 使用更大的探测缓冲区 (256KB) 减少 I/O 次数
        /// 2. 批量读取数据块 (最大 8MB) 提高吞吐量
        /// 3. 减少字符串解析开销，使用字节级扫描
        /// 4. 零拷贝设计，直接写入目标缓冲区
        /// </summary>
        private async Task<bool> ReceiveDataAfterAckAsync(byte[] buffer, CancellationToken ct)
        {
            try
            {
                int totalBytes = buffer.Length;
                int received = 0;
                bool headerFound = false;

                // 加大探测缓冲区 (256KB) - 许多设备一次性发送响应头+数据
                byte[] probeBuf = new byte[256 * 1024];
                int probeIdx = 0;
                
                // 用于快速字节匹配的模式
                byte[] rawmodePattern = Encoding.ASCII.GetBytes("rawmode=\"true\"");
                byte[] dataEndPattern = Encoding.ASCII.GetBytes("</data>");
                byte[] nakPattern = Encoding.ASCII.GetBytes("NAK");

                var sw = Stopwatch.StartNew();
                const int TIMEOUT_MS = 30000; // 30秒超时

                while (received < totalBytes && sw.ElapsedMilliseconds < TIMEOUT_MS)
                {
                    if (ct.IsCancellationRequested) return false;

                    if (!headerFound)
                    {
                        // 1. 寻找 XML 头部 - 批量读取
                        int toRead = probeBuf.Length - probeIdx;
                        if (toRead <= 0) { probeIdx = 0; toRead = probeBuf.Length; }
                        
                        int read = await _port.ReadAsync(probeBuf, probeIdx, toRead, ct);
                        if (read <= 0)
                        {
                            // 短暂等待后重试
                            await Task.Delay(1, ct);
                            continue;
                        }
                        probeIdx += read;

                        // 快速字节级扫描 - 避免字符串转换开销
                        int ackIndex = IndexOfPattern(probeBuf, 0, probeIdx, rawmodePattern);
                        
                        if (ackIndex >= 0)
                        {
                            int xmlEndIndex = IndexOfPattern(probeBuf, ackIndex, probeIdx - ackIndex, dataEndPattern);
                            if (xmlEndIndex >= 0)
                            {
                                headerFound = true;
                                int dataStart = xmlEndIndex + dataEndPattern.Length;
                                
                                // 跳过空白符 (换行等)
                                while (dataStart < probeIdx && (probeBuf[dataStart] == '\n' || probeBuf[dataStart] == '\r' || probeBuf[dataStart] == ' '))
                                    dataStart++;

                                // 零拷贝：将探测缓冲区中剩余的数据直接存入目标 buffer
                                int leftover = probeIdx - dataStart;
                                if (leftover > 0)
                                {
                                    int toCopy = Math.Min(leftover, totalBytes);
                                    Buffer.BlockCopy(probeBuf, dataStart, buffer, 0, toCopy);
                                    received = toCopy;
                                }
                            }
                        }
                        else if (IndexOfPattern(probeBuf, 0, probeIdx, nakPattern) >= 0)
                        {
                            // 设备拒绝 - 提取错误信息
                            try
                            {
                                string responseStr = Encoding.UTF8.GetString(probeBuf, 0, Math.Min(probeIdx, 2048));
                                _logDetail("[Read] NAK 响应: " + responseStr.Replace("\n", " ").Replace("\r", "").Substring(0, Math.Min(responseStr.Length, 500)));
                            }
                            catch { }
                            return false;
                        }
                    }
                    else
                    {
                        // 2. 高速读取原始数据块 - 使用更大的块 (8MB)
                        // USB 3.0 理论带宽 5Gbps，实际吞吐约 400MB/s
                        // 使用大块读取可以最大化 USB 带宽利用率
                        int toRead = Math.Min(totalBytes - received, 8 * 1024 * 1024);
                        
                        int read = await _port.ReadAsync(buffer, received, toRead, ct);
                        if (read <= 0)
                        {
                            // 短暂等待后重试
                            await Task.Delay(1, ct);
                            continue;
                        }
                        received += read;
                    }
                }
                
                return received >= totalBytes;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logDetail("[Read] 高速读取异常: " + ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// 高效字节模式匹配 (Boyer-Moore 简化版)
        /// </summary>
        private static int IndexOfPattern(byte[] data, int start, int length, byte[] pattern)
        {
            if (pattern.Length == 0 || length < pattern.Length) return -1;
            
            int end = start + length - pattern.Length;
            for (int i = start; i <= end; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>
        /// 等待设备进入 Raw 数据模式 (极速优化版)
        /// 优化点：
        /// 1. 使用字节级扫描代替字符串操作
        /// 2. 更大的缓冲区 (16KB) 减少 I/O 次数
        /// 3. 更激进的自旋策略减少延迟
        /// </summary>
        private async Task<bool> WaitForRawDataModeAsync(CancellationToken ct, int timeoutMs = 5000)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 使用更大的缓冲区
                    var buffer = new byte[16384];
                    int bufferPos = 0;
                    var sw = Stopwatch.StartNew();
                    int spinCount = 0;
                    
                    // 预定义字节模式 (避免运行时分配)
                    byte[] rawmodePattern = { (byte)'r', (byte)'a', (byte)'w', (byte)'m', (byte)'o', (byte)'d', (byte)'e', (byte)'=', (byte)'"', (byte)'t', (byte)'r', (byte)'u', (byte)'e', (byte)'"' };
                    byte[] dataEndPattern = { (byte)'<', (byte)'/', (byte)'d', (byte)'a', (byte)'t', (byte)'a', (byte)'>' };
                    byte[] nakPattern = { (byte)'N', (byte)'A', (byte)'K' };
                    byte[] ackPattern = { (byte)'A', (byte)'C', (byte)'K' };

                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        if (ct.IsCancellationRequested) return false;

                        int bytesAvailable = _port.BytesToRead;
                        if (bytesAvailable > 0)
                        {
                            // 读取尽可能多的数据
                            int toRead = Math.Min(buffer.Length - bufferPos, bytesAvailable);
                            if (toRead <= 0)
                            {
                                // 缓冲区满，重置
                                bufferPos = 0;
                                toRead = Math.Min(buffer.Length, bytesAvailable);
                            }
                            
                            int read = _port.Read(buffer, bufferPos, toRead);
                            if (read > 0)
                            {
                                bufferPos += read;
                                
                                // 快速字节级检查
                                if (IndexOfPattern(buffer, 0, bufferPos, nakPattern) >= 0)
                                {
                                    _logDetail("[Write] 设备拒绝 (NAK)");
                                    return false;
                                }

                                // 检查 rawmode 或 ACK
                                bool hasRawMode = IndexOfPattern(buffer, 0, bufferPos, rawmodePattern) >= 0;
                                bool hasAck = IndexOfPattern(buffer, 0, bufferPos, ackPattern) >= 0;
                                bool hasDataEnd = IndexOfPattern(buffer, 0, bufferPos, dataEndPattern) >= 0;
                                
                                if ((hasRawMode || hasAck) && hasDataEnd)
                                    return true;
                                
                                spinCount = 0;
                            }
                        }
                        else
                        {
                            // 极速自旋策略：减少上下文切换
                            spinCount++;
                            if (spinCount < 500)
                            {
                                Thread.SpinWait(50); // CPU 自旋
                            }
                            else if (spinCount < 2000)
                            {
                                Thread.Yield();
                            }
                            else
                            {
                                Thread.Sleep(0);
                            }
                        }
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    _logDetail(string.Format("[Write] 等待异常: {0}", ex.Message));
                    return false;
                }
            }, ct);
        }

        private void PurgeBuffer()
        {
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            _rxBuffer.Clear();
        }

        #endregion

        #region 速度统计

        private void StartTransferTimer(long totalBytes)
        {
            _transferStopwatch = Stopwatch.StartNew();
            _transferTotalBytes = totalBytes;
        }

        private void StopTransferTimer(string operationName, long bytesTransferred)
        {
            if (_transferStopwatch == null) return;

            _transferStopwatch.Stop();
            double seconds = _transferStopwatch.Elapsed.TotalSeconds;

            if (seconds > 0.1 && bytesTransferred > 0)
            {
                double mbps = (bytesTransferred / 1024.0 / 1024.0) / seconds;
                double mbTotal = bytesTransferred / 1024.0 / 1024.0;

                if (mbTotal >= 1)
                    _log(string.Format("[速度] {0}: {1:F1}MB 用时 {2:F1}s ({3:F2} MB/s)", operationName, mbTotal, seconds, mbps));
            }

            _transferStopwatch = null;
        }

        #endregion

        #region 认证支持方法

        public async Task<string> SendRawXmlAsync(string xmlOrCommand, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                PurgeBuffer();
                string xml = xmlOrCommand;
                if (!xmlOrCommand.TrimStart().StartsWith("<?xml"))
                    xml = string.Format("<?xml version=\"1.0\" ?><data><{0} /></data>", xmlOrCommand);

                _port.Write(Encoding.UTF8.GetBytes(xml));
                return await ReadRawResponseAsync(5000, ct);
            }
            catch (Exception ex)
            {
                _logDetail(string.Format("[Firehose] 发送原始 XML 异常: {0}", ex.Message));
                return null;
            }
        }

        public async Task<string> SendRawBytesAndGetResponseAsync(byte[] data, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                PurgeBuffer();
                _port.Write(data, 0, data.Length);
                await Task.Delay(100, ct);
                return await ReadRawResponseAsync(5000, ct);
            }
            catch (Exception ex)
            {
                _logDetail(string.Format("[Firehose] 发送原始字节异常: {0}", ex.Message));
                return null;
            }
        }

        public async Task<string> SendXmlCommandWithAttributeResponseAsync(string xml, string attrName, int maxRetries = 10, CancellationToken ct = default(CancellationToken))
        {
            for (int i = 0; i < maxRetries; i++)
            {
                if (ct.IsCancellationRequested) return null;
                try
                {
                    PurgeBuffer();
                    _port.Write(Encoding.UTF8.GetBytes(xml));
                    string response = await ReadRawResponseAsync(3000, ct);
                    if (string.IsNullOrEmpty(response)) continue;

                    string pattern = string.Format("{0}=\"([^\"]*)\"", attrName);
                    var match = Regex.Match(response, pattern, RegexOptions.IgnoreCase);
                    if (match.Success && match.Groups.Count > 1)
                        return match.Groups[1].Value;
                }
                catch (Exception ex)
                {
                    // 重试中，记录详细日志
                    _logDetail(string.Format("[Firehose] 获取属性 {0} 重试 {1}/{2}: {3}", attrName, i + 1, maxRetries, ex.Message));
                }
                await Task.Delay(100, ct);
            }
            return null;
        }

        private async Task<string> ReadRawResponseAsync(int timeoutMs, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var startTime = DateTime.Now;

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (ct.IsCancellationRequested) break;
                if (_port.BytesToRead > 0)
                {
                    byte[] buffer = new byte[_port.BytesToRead];
                    int read = _port.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        string content = sb.ToString();
                        if (content.Contains("</data>") || content.Contains("/>"))
                            return content;
                    }
                }
                await Task.Delay(20, ct);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        #endregion

        #region OPLUS (OPPO/Realme/OnePlus) VIP 认证

        /// <summary>
        /// 执行 VIP 认证流程 (基于 Digest 和 Signature 文件)
        /// 6 步流程: 1. Digest → 2. TransferCfg → 3. Verify(EnableVip=1) → 4. Signature → 5. SHA256Init → 6. Configure
        /// 参考 edl_vip_auth.py 和 qdl-gpt 实现
        /// </summary>
        public async Task<bool> PerformVipAuthAsync(string digestPath, string signaturePath, CancellationToken ct = default(CancellationToken))
        {
            if (!File.Exists(digestPath) || !File.Exists(signaturePath))
            {
                _log("[VIP] 认证失败：缺少 Digest 或 Signature 文件");
                return false;
            }

            _log("[VIP] 正在执行安全验证...");
            _logDetail(string.Format("[VIP] Digest: {0}", digestPath));
            _logDetail(string.Format("[VIP] Signature: {0}", signaturePath));
            
            bool hasError = false;
            string errorDetail = "";
            
            try
            {
                // 清空缓冲区
                PurgeBuffer();

                // ========== Step 1: 直接发送 Digest (二进制数据) ==========
                byte[] digestData = File.ReadAllBytes(digestPath);
                _logDetail(string.Format("[VIP] Step 1/6: Digest ({0} 字节)", digestData.Length));
                if (digestData.Length >= 16)
                {
                    _logDetail(string.Format("[VIP] Digest 头部: {0}", BitConverter.ToString(digestData, 0, 16)));
                }
                await _port.WriteAsync(digestData, 0, digestData.Length, ct);
                await Task.Delay(500, ct);
                string resp1 = await ReadAndLogDeviceResponseAsync(ct, 3000);
                _logDetail(string.Format("[VIP] Step 1 响应: {0}", TruncateResponse(resp1)));
                if (resp1.Contains("NAK"))
                {
                    hasError = true;
                    errorDetail = "Digest 被拒绝";
                }

                // ========== Step 2: 发送 TransferCfg (关键步骤！) ==========
                _logDetail("[VIP] Step 2/6: TransferCfg");
                string transferCfgXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                    "<data><transfercfg reboot_type=\"off\" timeout_in_sec=\"90\" /></data>";
                _port.Write(Encoding.UTF8.GetBytes(transferCfgXml));
                await Task.Delay(300, ct);
                string resp2 = await ReadAndLogDeviceResponseAsync(ct, 2000);
                _logDetail(string.Format("[VIP] Step 2 响应: {0}", TruncateResponse(resp2)));

                // ========== Step 3: 发送 Verify (启用 VIP 模式) ==========
                _logDetail("[VIP] Step 3/6: Verify (EnableVip=1)");
                string verifyXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                    "<data><verify value=\"ping\" EnableVip=\"1\"/></data>";
                _port.Write(Encoding.UTF8.GetBytes(verifyXml));
                await Task.Delay(300, ct);
                string resp3 = await ReadAndLogDeviceResponseAsync(ct, 2000);
                _logDetail(string.Format("[VIP] Step 3 响应: {0}", TruncateResponse(resp3)));

                // ========== Step 4: 直接发送 Signature (二进制数据) ==========
                byte[] sigData = File.ReadAllBytes(signaturePath);
                _logDetail(string.Format("[VIP] Step 4/6: Signature ({0} 字节)", sigData.Length));
                if (sigData.Length >= 16)
                {
                    _logDetail(string.Format("[VIP] Signature 头部: {0}", BitConverter.ToString(sigData, 0, 16)));
                }
                
                // rawmode="true" 时，设备期望按扇区大小 (4096 字节) 接收数据
                bool isRawMode = resp3.Contains("rawmode=\"true\"");
                int targetSize = isRawMode ? 4096 : sigData.Length;
                
                byte[] sigDataPadded;
                if (sigData.Length < targetSize)
                {
                    sigDataPadded = new byte[targetSize];
                    Array.Copy(sigData, 0, sigDataPadded, 0, sigData.Length);
                    _logDetail(string.Format("[VIP] Signature 填充: {0} → {1} 字节", sigData.Length, targetSize));
                }
                else
                {
                    sigDataPadded = sigData;
                }
                
                await _port.WriteAsync(sigDataPadded, 0, sigDataPadded.Length, ct);
                await Task.Delay(500, ct);
                string resp4 = await ReadAndLogDeviceResponseAsync(ct, 3000);
                _logDetail(string.Format("[VIP] Step 4 响应: {0}", TruncateResponse(resp4)));
                
                // 检查响应 - 区分真正的错误和警告
                if (resp4.Contains("NAK"))
                {
                    hasError = true;
                    errorDetail = "Signature 被设备拒绝 (NAK)";
                    _log("[VIP] ⚠ " + errorDetail);
                    _log(string.Format("[VIP] 详细响应: {0}", resp4));
                }
                else if (resp4.Contains("ERROR") && !resp4.Contains("ACK"))
                {
                    hasError = true;
                    errorDetail = "Signature 传输错误";
                    _log("[VIP] ⚠ " + errorDetail);
                    _log(string.Format("[VIP] 详细响应: {0}", resp4));
                }

                // ========== Step 5: 发送 SHA256Init ==========
                _logDetail("[VIP] Step 5/6: SHA256Init");
                string sha256Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                    "<data><sha256init Verbose=\"1\"/></data>";
                _port.Write(Encoding.UTF8.GetBytes(sha256Xml));
                await Task.Delay(300, ct);
                string respSha = await ReadAndLogDeviceResponseAsync(ct, 2000);
                _logDetail(string.Format("[VIP] Step 5 响应: {0}", TruncateResponse(respSha)));

                // Step 6: Configure 将在外部调用
                if (!hasError)
                {
                    _log("[VIP] ✓ 安全验证完成");
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[VIP] 验证被取消");
                throw;
            }
            catch (Exception ex)
            {
                _log(string.Format("[VIP] 验证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 执行 VIP 认证流程 (使用 byte[] 数据，无需文件)
        /// </summary>
        /// <param name="digestData">Digest 数据 (Hash Segment)</param>
        /// <param name="signatureData">签名数据 (256 字节 RSA-2048)</param>
        public async Task<bool> PerformVipAuthAsync(byte[] digestData, byte[] signatureData, CancellationToken ct = default(CancellationToken))
        {
            if (digestData == null || digestData.Length == 0)
            {
                _log("[VIP] 认证失败：缺少 Digest 数据");
                return false;
            }
            if (signatureData == null || signatureData.Length == 0)
            {
                _log("[VIP] 认证失败：缺少 Signature 数据");
                return false;
            }

            _log("[VIP] 开始安全验证 (内存数据模式)...");

            try
            {
                // Step 1: 发送 Digest
                await SendVipDigestAsync(digestData, ct);

                // Step 2-3: 准备 VIP 模式
                await PrepareVipModeAsync(ct);

                // Step 4: 发送签名 (256 字节)
                await SendVipSignatureAsync(signatureData, ct);

                // Step 5: 完成认证
                await FinalizeVipAuthAsync(ct);

                // 只要流程完成就认为成功（签名响应检测可能不准确）
                _log("[VIP] VIP 认证流程完成");
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[VIP] 验证被取消");
                throw;
            }
            catch (Exception ex)
            {
                _log(string.Format("[VIP] 验证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Step 1: 发送 VIP Digest (Hash Segment)
        /// </summary>
        public async Task<bool> SendVipDigestAsync(byte[] digestData, CancellationToken ct = default(CancellationToken))
        {
            _log(string.Format("[VIP] Step 1: 发送 Digest ({0} 字节)...", digestData.Length));
            PurgeBuffer();

            await _port.WriteAsync(digestData, 0, digestData.Length, ct);
            await Task.Delay(500, ct);

            string resp = await ReadAndLogDeviceResponseAsync(ct, 3000);
            if (resp.Contains("NAK") || resp.Contains("ERROR"))
            {
                _log("[VIP] Digest 响应异常，尝试继续...");
            }
            return true;
        }

        /// <summary>
        /// Step 2-3: 准备 VIP 模式 (TransferCfg + Verify)
        /// TransferCfg 是关键步骤，参考 edl_vip_auth.py
        /// </summary>
        public async Task<bool> PrepareVipModeAsync(CancellationToken ct = default(CancellationToken))
        {
            // Step 2: TransferCfg (关键步骤！)
            _log("[VIP] Step 2: 发送 TransferCfg...");
            string transferCfgXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                "<data><transfercfg reboot_type=\"off\" timeout_in_sec=\"90\" /></data>";
            _port.Write(Encoding.UTF8.GetBytes(transferCfgXml));
            await Task.Delay(300, ct);
            string resp2 = await ReadAndLogDeviceResponseAsync(ct, 2000);
            if (resp2.Contains("NAK") || resp2.Contains("ERROR"))
            {
                _log("[VIP] TransferCfg 失败，尝试继续...");
            }

            // Step 3: Verify (启用 VIP 模式)
            _log("[VIP] Step 3: 发送 Verify (EnableVip=1)...");
            string verifyXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                "<data><verify value=\"ping\" EnableVip=\"1\"/></data>";
            _port.Write(Encoding.UTF8.GetBytes(verifyXml));
            await Task.Delay(300, ct);
            string resp3 = await ReadAndLogDeviceResponseAsync(ct, 2000);
            if (resp3.Contains("NAK") || resp3.Contains("ERROR"))
            {
                _log("[VIP] Verify 失败，尝试继续...");
            }

            return true;
        }

        /// <summary>
        /// Step 4: 发送 VIP 签名 (256 字节 RSA-2048，rawmode 下需填充到 4096 字节)
        /// 这是核心方法：在发送 Digest 后写入签名
        /// </summary>
        /// <param name="signatureData">签名数据 (256 字节)</param>
        /// <param name="padTo4096">是否填充到 4096 字节 (rawmode 下需要)</param>
        public async Task<bool> SendVipSignatureAsync(byte[] signatureData, CancellationToken ct = default(CancellationToken), bool padTo4096 = true)
        {
            // 处理签名数据大小
            byte[] sig;
            if (signatureData.Length == 256)
            {
                // 已经是正确大小
                sig = signatureData;
            }
            else if (signatureData.Length > 256)
            {
                // 提取前 256 字节 (处理带填充的 sign.bin)
                sig = new byte[256];
                Array.Copy(signatureData, 0, sig, 0, 256);
                _log(string.Format("[VIP] 从 {0} 字节数据中提取 256 字节签名", signatureData.Length));
            }
            else
            {
                // 数据不足，填充零
                sig = new byte[256];
                Array.Copy(signatureData, 0, sig, 0, signatureData.Length);
                _log(string.Format("[VIP] 警告: 签名数据不足 256 字节 (实际 {0})", signatureData.Length));
            }
            
            // rawmode 下设备期望 4096 字节 (扇区大小)
            byte[] sigPadded;
            if (padTo4096 && sig.Length < 4096)
            {
                sigPadded = new byte[4096];
                Array.Copy(sig, 0, sigPadded, 0, sig.Length);
                _log(string.Format("[VIP] Step 4: 发送 Signature ({0} → {1} 字节, rawmode 填充)...", sig.Length, sigPadded.Length));
            }
            else
            {
                sigPadded = sig;
                _log(string.Format("[VIP] Step 4: 发送 Signature ({0} 字节)...", sig.Length));
            }
            
            await _port.WriteAsync(sigPadded, 0, sigPadded.Length, ct);
            await Task.Delay(500, ct);

            string resp = await ReadAndLogDeviceResponseAsync(ct, 3000);
            
            // 检查响应 - 区分真正的错误和警告
            bool success = false;
            if (resp.Contains("NAK"))
            {
                _log("[VIP] Signature 被设备拒绝 (NAK)");
            }
            else if (resp.Contains("ACK"))
            {
                // 有 ACK 表示成功，即使有 ERROR 日志也可能只是警告
                _log("[VIP] ✓ Signature 已接受");
                success = true;
            }
            else if (resp.Contains("ERROR") && !resp.Contains("ACK"))
            {
                _log("[VIP] Signature 传输错误");
            }
            else if (string.IsNullOrEmpty(resp))
            {
                _log("[VIP] Signature 发送完成 (无响应)");
                success = true; // 无响应也可能是成功
            }
            else
            {
                _log("[VIP] Signature 发送完成");
                success = true;
            }

            return success;
        }

        /// <summary>
        /// Step 5: 完成 VIP 认证 (SHA256Init)
        /// </summary>
        public async Task<bool> FinalizeVipAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            _log("[VIP] Step 5: 发送 SHA256Init...");
            string sha256Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                "<data><sha256init Verbose=\"1\"/></data>";
            _port.Write(Encoding.UTF8.GetBytes(sha256Xml));
            await Task.Delay(300, ct);
            string resp = await ReadAndLogDeviceResponseAsync(ct, 2000);
            if (resp.Contains("NAK") || resp.Contains("ERROR"))
            {
                _log("[VIP] SHA256Init 失败，尝试继续...");
            }

            _log("[VIP] VIP 验证流程完成");
            return true;
        }
        
        /// <summary>
        /// 截断响应字符串用于日志显示
        /// </summary>
        private string TruncateResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return "(空)";
            
            // 移除换行符，便于显示
            string clean = response.Replace("\r", "").Replace("\n", " ").Trim();
            
            // 截断过长的响应
            if (clean.Length > 300)
                return clean.Substring(0, 300) + "...";
            
            return clean;
        }

        /// <summary>
        /// 读取并记录设备响应 (异步非阻塞)
        /// </summary>
        private async Task<string> ReadAndLogDeviceResponseAsync(CancellationToken ct, int timeoutMs = 2000)
        {
            var startTime = DateTime.Now;
            var sb = new StringBuilder();
            
            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                
                // 检查可用数据
                int bytesToRead = _port.BytesToRead;
                if (bytesToRead > 0)
                {
                    byte[] buffer = new byte[bytesToRead];
                    int read = _port.Read(buffer, 0, bytesToRead);
                    
                    if (read > 0)
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        
                        var content = sb.ToString();
                        
                        // 提取设备日志 (详细日志，不在主界面显示)
                        var logMatches = Regex.Matches(content, @"<log value=""([^""]*)""\s*/>");
                        foreach (Match m in logMatches)
                        {
                            if (m.Groups.Count > 1)
                                _logDetail(string.Format("[Device] {0}", m.Groups[1].Value));
                        }
                        
                        // 检查响应
                        if (content.Contains("<response") || content.Contains("</data>"))
                        {
                            if (content.Contains("value=\"ACK\"") || content.Contains("verify passed"))
                            {
                                return content; // 成功
                            }
                            if (content.Contains("NAK") || content.Contains("ERROR"))
                            {
                                return content; // 失败但返回响应
                            }
                        }
                    }
                }
                
                await Task.Delay(50, ct);
            }
            
            return sb.ToString();
        }

        /// <summary>
        /// 获取设备当前的挑战码 (用于在线签名)
        /// </summary>
        public async Task<string> GetVipChallengeAsync(CancellationToken ct = default(CancellationToken))
        {
            _log("[VIP] 正在获取设备挑战码 (getsigndata)...");
            string xml = "<?xml version=\"1.0\" ?><data>\n<getsigndata value=\"ping\" />\n</data>\n";
            _port.Write(Encoding.UTF8.GetBytes(xml));

            // 尝试从返回的 INFO 日志中提取 NV 数据
            var response = await ReadRawResponseAsync(3000, ct);
            if (response != null && response.Contains("NV:"))
            {
                var match = Regex.Match(response, "NV:([^;\\s]+)");
                if (match.Success) return match.Groups[1].Value;
            }
            return null;
        }

        /// <summary>
        /// 初始化 SHA256 (OPLUS 分区写入前需要)
        /// </summary>
        public async Task<bool> Sha256InitAsync(CancellationToken ct = default(CancellationToken))
        {
            string xml = "<?xml version=\"1.0\" ?><data>\n<sha256init />\n</data>\n";
            _port.Write(Encoding.UTF8.GetBytes(xml));
            return await WaitForAckAsync(ct);
        }

        /// <summary>
        /// 完成 SHA256 (OPLUS 分区写入后需要)
        /// </summary>
        public async Task<bool> Sha256FinalAsync(CancellationToken ct = default(CancellationToken))
        {
            string xml = "<?xml version=\"1.0\" ?><data>\n<sha256final />\n</data>\n";
            _port.Write(Encoding.UTF8.GetBytes(xml));
            return await WaitForAckAsync(ct);
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 格式化文件大小 (不足1MB按KB，满1GB按GB)
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return string.Format("{0:F2} GB", bytes / (1024.0 * 1024 * 1024));
            if (bytes >= 1024 * 1024)
                return string.Format("{0:F2} MB", bytes / (1024.0 * 1024));
            if (bytes >= 1024)
                return string.Format("{0:F0} KB", bytes / 1024.0);
            return string.Format("{0} B", bytes);
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
