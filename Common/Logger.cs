// ============================================================================
// SakuraEDL - 全局日志管理器
// Global Logger - 统一日志输出和管理
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace SakuraEDL.Common
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Fatal = 4
    }

    /// <summary>
    /// 全局日志管理器 - 统一日志输出
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logFilePath;
        private static LogLevel _minLevel = LogLevel.Debug;
        private static Action<string, Color?> _uiLogger;
        #pragma warning disable CS0414
        private static bool _isInitialized;
        #pragma warning restore CS0414

        /// <summary>
        /// 最小日志级别 (低于此级别的日志不会输出)
        /// </summary>
        public static LogLevel MinLevel
        {
            get => _minLevel;
            set => _minLevel = value;
        }

        /// <summary>
        /// 初始化日志系统
        /// </summary>
        /// <param name="logFilePath">日志文件路径</param>
        /// <param name="uiLogger">UI 日志回调 (可选)</param>
        public static void Initialize(string logFilePath, Action<string, Color?> uiLogger = null)
        {
            lock (_lock)
            {
                _logFilePath = logFilePath;
                _uiLogger = uiLogger;
                _isInitialized = true;

                // 确保日志目录存在
                try
                {
                    string dir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Logger] Failed to create log directory: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 设置 UI 日志回调
        /// </summary>
        public static void SetUILogger(Action<string, Color?> uiLogger)
        {
            lock (_lock)
            {
                _uiLogger = uiLogger;
            }
        }

        /// <summary>
        /// 记录调试日志 (仅写入文件)
        /// </summary>
        public static void Debug(string message, string category = null)
        {
            Log(LogLevel.Debug, message, category, null, false);
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        public static void Info(string message, string category = null, bool showInUI = true)
        {
            Log(LogLevel.Info, message, category, null, showInUI);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        public static void Warning(string message, string category = null, bool showInUI = true)
        {
            Log(LogLevel.Warning, message, category, Color.Orange, showInUI);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        public static void Error(string message, string category = null, bool showInUI = true)
        {
            Log(LogLevel.Error, message, category, Color.Red, showInUI);
        }

        /// <summary>
        /// 记录错误日志 (带异常)
        /// </summary>
        public static void Error(string message, Exception ex, string category = null, bool showInUI = true)
        {
            string fullMessage = $"{message}: {ex.Message}";
            Log(LogLevel.Error, fullMessage, category, Color.Red, showInUI);
            
            // 写入详细堆栈到文件
            WriteToFile(LogLevel.Error, $"Error Details: {ex}", category);
        }

        /// <summary>
        /// 记录致命错误日志
        /// </summary>
        public static void Fatal(string message, Exception ex = null, string category = null)
        {
            string fullMessage = ex != null ? $"{message}: {ex.Message}" : message;
            Log(LogLevel.Fatal, fullMessage, category, Color.DarkRed, true);
            
            if (ex != null)
            {
                WriteToFile(LogLevel.Fatal, $"Fatal anomaly details: {ex}", category);
            }
        }

        /// <summary>
        /// 核心日志方法
        /// </summary>
        private static void Log(LogLevel level, string message, string category, Color? color, bool showInUI)
        {
            if (level < _minLevel)
                return;

            string formattedMessage = FormatMessage(level, message, category);

            // 写入文件
            WriteToFile(level, message, category);

            // 输出到调试窗口
            System.Diagnostics.Debug.WriteLine(formattedMessage);

            // 输出到 UI
            if (showInUI && _uiLogger != null)
            {
                try
                {
                    string uiMessage = string.IsNullOrEmpty(category) ? message : $"[{category}] {message}";
                    _uiLogger(uiMessage, color);
                }
                catch
                {
                    // UI 回调失败，忽略
                }
            }
        }

        /// <summary>
        /// 格式化日志消息
        /// </summary>
        private static string FormatMessage(LogLevel level, string message, string category)
        {
            string levelStr = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Fatal => "FTL",
                _ => "???"
            };

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            
            if (string.IsNullOrEmpty(category))
                return $"[{timestamp}] [{levelStr}] {message}";
            else
                return $"[{timestamp}] [{levelStr}] [{category}] {message}";
        }

        /// <summary>
        /// 写入日志文件
        /// </summary>
        private static void WriteToFile(LogLevel level, string message, string category)
        {
            if (string.IsNullOrEmpty(_logFilePath))
                return;

            try
            {
                string formattedMessage = FormatMessage(level, message, category);
                lock (_lock)
                {
                    File.AppendAllText(_logFilePath, formattedMessage + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 文件写入失败，忽略
            }
        }

        /// <summary>
        /// 创建带类别的日志记录器
        /// </summary>
        public static CategoryLogger ForCategory(string category)
        {
            return new CategoryLogger(category);
        }
    }

    /// <summary>
    /// 带类别的日志记录器
    /// </summary>
    public class CategoryLogger
    {
        private readonly string _category;

        public CategoryLogger(string category)
        {
            _category = category;
        }

        public void Debug(string message) => Logger.Debug(message, _category);
        public void Info(string message, bool showInUI = true) => Logger.Info(message, _category, showInUI);
        public void Warning(string message, bool showInUI = true) => Logger.Warning(message, _category, showInUI);
        public void Error(string message, bool showInUI = true) => Logger.Error(message, _category, showInUI);
        public void Error(string message, Exception ex, bool showInUI = true) => Logger.Error(message, ex, _category, showInUI);
        public void Fatal(string message, Exception ex = null) => Logger.Fatal(message, ex, _category);
    }
}
