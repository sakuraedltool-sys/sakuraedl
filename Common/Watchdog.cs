// ============================================================================
// SakuraEDL - 看门狗机制
// Watchdog Mechanism for Protocol Communication
// ============================================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SakuraEDL.Common
{
    /// <summary>
    /// 看门狗状态
    /// </summary>
    public enum WatchdogState
    {
        Idle,       // 空闲
        Running,    // 运行中
        Timeout,    // 已超时
        Stopped     // 已停止
    }

    /// <summary>
    /// 看门狗超时事件参数
    /// </summary>
    public class WatchdogTimeoutEventArgs : EventArgs
    {
        public string ModuleName { get; set; }
        public string OperationName { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public int TimeoutCount { get; set; }
        public bool ShouldReset { get; set; } = true;
    }

    /// <summary>
    /// 通用看门狗接口
    /// </summary>
    public interface IWatchdog : IDisposable
    {
        /// <summary>
        /// 当前状态
        /// </summary>
        WatchdogState State { get; }

        /// <summary>
        /// 超时时间
        /// </summary>
        TimeSpan Timeout { get; set; }

        /// <summary>
        /// 超时次数
        /// </summary>
        int TimeoutCount { get; }

        /// <summary>
        /// 启动看门狗
        /// </summary>
        void Start(string operationName = null);

        /// <summary>
        /// 停止看门狗
        /// </summary>
        void Stop();

        /// <summary>
        /// 喂狗 (重置计时器)
        /// </summary>
        void Feed();

        /// <summary>
        /// 检查是否超时
        /// </summary>
        bool IsTimedOut { get; }

        /// <summary>
        /// 超时事件
        /// </summary>
        event EventHandler<WatchdogTimeoutEventArgs> OnTimeout;
    }

    /// <summary>
    /// 通用看门狗实现
    /// </summary>
    public class Watchdog : IWatchdog
    {
        private readonly string _moduleName;
        private readonly Action<string> _log;
        private readonly Stopwatch _stopwatch;
        private readonly object _lock = new object();
        
        private string _currentOperation;
        private int _timeoutCount;
        private bool _disposed;
        private CancellationTokenSource _cts;
        private Task _monitorTask;

        public WatchdogState State { get; private set; } = WatchdogState.Idle;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public int TimeoutCount => _timeoutCount;
        public bool IsTimedOut => State == WatchdogState.Timeout;

        public event EventHandler<WatchdogTimeoutEventArgs> OnTimeout;

        /// <summary>
        /// 创建看门狗
        /// </summary>
        /// <param name="moduleName">模块名称 (Qualcomm/Spreadtrum/Fastboot)</param>
        /// <param name="timeout">超时时间</param>
        /// <param name="log">日志回调</param>
        public Watchdog(string moduleName, TimeSpan timeout, Action<string> log = null)
        {
            _moduleName = moduleName;
            Timeout = timeout;
            _log = log;
            _stopwatch = new Stopwatch();
        }

        /// <summary>
        /// 启动看门狗
        /// </summary>
        public void Start(string operationName = null)
        {
            lock (_lock)
            {
                if (_disposed) return;

                _currentOperation = operationName ?? "Unknown";
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                _stopwatch.Restart();
                State = WatchdogState.Running;

                _log?.Invoke($"[{_moduleName}] Watchdog started: {_currentOperation} (Timeout: {Timeout.TotalSeconds} seconds)");

                // 启动后台监控任务
                _monitorTask = MonitorAsync(_cts.Token);
            }
        }

        /// <summary>
        /// 停止看门狗
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                _cts?.Cancel();
                _stopwatch.Stop();
                State = WatchdogState.Stopped;
                _log?.Invoke($"[{_moduleName}] Watchdog stopped: {_currentOperation}");
            }
        }

        /// <summary>
        /// 喂狗 - 重置计时器
        /// </summary>
        public void Feed()
        {
            lock (_lock)
            {
                if (State == WatchdogState.Running)
                {
                    _stopwatch.Restart();
                }
            }
        }

        /// <summary>
        /// 后台监控任务
        /// </summary>
        private async Task MonitorAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && State == WatchdogState.Running)
                {
                    await Task.Delay(1000, ct); // 每秒检查一次

                    lock (_lock)
                    {
                        if (_stopwatch.Elapsed > Timeout && State == WatchdogState.Running)
                        {
                            State = WatchdogState.Timeout;
                            _timeoutCount++;

                            _log?.Invoke($"[{_moduleName}] Watchdog timeout! Operation: {_currentOperation}, Waited: {_stopwatch.Elapsed.TotalSeconds:F1} seconds, Timeout Count: {_timeoutCount}");

                            var args = new WatchdogTimeoutEventArgs
                            {
                                ModuleName = _moduleName,
                                OperationName = _currentOperation,
                                ElapsedTime = _stopwatch.Elapsed,
                                TimeoutCount = _timeoutCount
                            };

                            OnTimeout?.Invoke(this, args);

                            // 如果需要重置，自动重启看门狗
                            if (args.ShouldReset)
                            {
                                _stopwatch.Restart();
                                State = WatchdogState.Running;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[{_moduleName}] Watchdog monitoring error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Task taskToWait = null;
            
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                _cts?.Cancel();
                _stopwatch.Stop();
                State = WatchdogState.Stopped;
                taskToWait = _monitorTask;
            }
            
            // 在锁外等待任务完成，避免死锁
            if (taskToWait != null)
            {
                try
                {
                    // 最多等待 2 秒
                    taskToWait.Wait(2000);
                }
                catch (AggregateException)
                {
                    // 忽略任务取消异常
                }
                catch (ObjectDisposedException)
                {
                    // 忽略已释放异常
                }
            }
            
            lock (_lock)
            {
                _cts?.Dispose();
                _cts = null;
                _monitorTask = null;
            }
        }
    }

    /// <summary>
    /// 看门狗作用域 (using 模式)
    /// </summary>
    public class WatchdogScope : IDisposable
    {
        private readonly IWatchdog _watchdog;

        public WatchdogScope(IWatchdog watchdog, string operationName)
        {
            _watchdog = watchdog;
            _watchdog.Start(operationName);
        }

        /// <summary>
        /// 喂狗
        /// </summary>
        public void Feed() => _watchdog.Feed();

        public void Dispose()
        {
            _watchdog.Stop();
        }
    }

    /// <summary>
    /// 看门狗管理器 - 统一管理各模块的看门狗
    /// </summary>
    public static class WatchdogManager
    {
        private static readonly object _lock = new object();
        private static Watchdog _qualcommWatchdog;
        private static Watchdog _spreadtrumWatchdog;
        private static Watchdog _fastbootWatchdog;

        /// <summary>
        /// 默认超时配置
        /// </summary>
        public static class DefaultTimeouts
        {
            public static readonly TimeSpan Qualcomm = TimeSpan.FromSeconds(60);
            public static readonly TimeSpan Spreadtrum = TimeSpan.FromSeconds(45);
            public static readonly TimeSpan Fastboot = TimeSpan.FromSeconds(90);
        }

        /// <summary>
        /// 获取或创建高通看门狗
        /// </summary>
        public static Watchdog GetQualcommWatchdog(Action<string> log = null)
        {
            lock (_lock)
            {
                if (_qualcommWatchdog == null)
                {
                    _qualcommWatchdog = new Watchdog("Qualcomm", DefaultTimeouts.Qualcomm, log);
                }
                return _qualcommWatchdog;
            }
        }

        /// <summary>
        /// 获取或创建展讯看门狗
        /// </summary>
        public static Watchdog GetSpreadtrumWatchdog(Action<string> log = null)
        {
            lock (_lock)
            {
                if (_spreadtrumWatchdog == null)
                {
                    _spreadtrumWatchdog = new Watchdog("Spreadtrum", DefaultTimeouts.Spreadtrum, log);
                }
                return _spreadtrumWatchdog;
            }
        }

        /// <summary>
        /// 获取或创建 Fastboot 看门狗
        /// </summary>
        public static Watchdog GetFastbootWatchdog(Action<string> log = null)
        {
            lock (_lock)
            {
                if (_fastbootWatchdog == null)
                {
                    _fastbootWatchdog = new Watchdog("Fastboot", DefaultTimeouts.Fastboot, log);
                }
                return _fastbootWatchdog;
            }
        }

        /// <summary>
        /// 释放所有看门狗
        /// </summary>
        public static void DisposeAll()
        {
            lock (_lock)
            {
                _qualcommWatchdog?.Dispose();
                _qualcommWatchdog = null;

                _spreadtrumWatchdog?.Dispose();
                _spreadtrumWatchdog = null;

                _fastbootWatchdog?.Dispose();
                _fastbootWatchdog = null;
            }
        }
    }
}
