// ============================================================================
// SakuraEDL - Application Startup
// ============================================================================
// [ZH] SakuraEDL 应用启动 (WPF)
// [EN] SakuraEDL Application Startup Class
// ============================================================================

using System;
using System.Diagnostics;
using System.Windows;
using SakuraEDL.Common;

namespace SakuraEDL
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // 检测多实例
                var processes = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName);
                if (processes.Length > 1)
                {
                    var multiWindow = new Views.MultiInstanceForm
                    {
                        Message = "检测到已打开多个进程，请关闭其他程序"
                    };

                    multiWindow.ShowDialog();
                    this.Shutdown(1);
                    return;
                }

                // 先创建主窗口（完全隐藏：不可见且不出现在任务栏）
                this.MainWindow = new MainWindow
                {
                    Opacity = 0,
                    ShowInTaskbar = false,
                    Visibility = Visibility.Hidden
                };
                this.MainWindow.Hide();

                // 显示启动画面
                var splashWindow = new Views.SplashForm();
                splashWindow.Show();

                // 初始化应用
                InitializeApplication();

                // 直接显示主窗口（不显示 Splash）
              //  var mainWindow = new MainWindow();
              //  this.MainWindow = mainWindow;
               // mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"应用启动失败: {ex.Message}", "错误");
                this.Shutdown(1);
            }
        }

        private void InitializeApplication()
        {
            try
            {
                // 初始化配置
                PerformanceConfig.ResetCache();
                LanguageManager.Initialize();
                PreloadManager.StartPreload();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"应用初始化失败: {ex.Message}", "错误");
            }
        }
    }
}
