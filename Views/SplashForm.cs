using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SakuraEDL.Common;
using SakuraEDL;

namespace SakuraEDL.Views
{
    public partial class SplashForm : Window
    {
        private DispatcherTimer _timer;
        private int _angle = 0;
        private int _progress = 0;
        private bool _lowPerformanceMode;
        private int _logoAnimStep = 0;
        private int _logoAnimTotal = 36;
        private Color _spinnerBaseColor = Color.FromRgb(88, 222, 164);

        public SplashForm()
        {
            InitializeComponent();
            if (Application.Current?.TryFindResource("ThemeAccentBrush") is SolidColorBrush accentBrush)
            {
                _spinnerBaseColor = accentBrush.Color;
            }

            // 读取性能配置
            _lowPerformanceMode = PerformanceConfig.LowPerformanceMode;

            // 启动时背景略微透明，随后淡入
            this.Opacity = _lowPerformanceMode ? 1.0 : 0.85;

            // 启动后台预加载
            PreloadManager.StartPreload();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(PerformanceConfig.AnimationInterval);
            _timer.Tick += Timer_Tick;

            // 低配模式减少动画步骤
            if (_lowPerformanceMode)
            {
                _logoAnimTotal = 18;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            // Update spinner angle
            _angle = (_angle + (_lowPerformanceMode ? 12 : 6)) % 360;

            // Fade in animation
            if (!_lowPerformanceMode && this.Opacity < 1.0)
                this.Opacity = Math.Min(1.0, this.Opacity + 0.02);

            // Logo animation
            try
            {
                if (_logoAnimStep < _logoAnimTotal)
                {
                    _logoAnimStep++;
                    float t = (float)_logoAnimStep / _logoAnimTotal;

                    // ease-out animation
                    t = 1f - (1f - t) * (1f - t);
                    LogoLabel.Opacity = t;
                }
            }
            catch { }

            // Sync preload progress
            int targetProgress = Math.Max(_progress, PreloadManager.Progress);

            if (_progress < targetProgress)
                _progress = Math.Min(_progress + 2, targetProgress);
            else if (_progress < 100 && PreloadManager.IsPreloadComplete)
                _progress++;

            // Draw spinner and update UI
            try
            {
                DrawSpinner(_angle);
                ProgressBar1.Value = _progress;
                StatusLabel.Text = PreloadManager.CurrentStatus;
            }
            catch { }

            // Close splash and show main window
            if (_progress >= 100 && PreloadManager.IsPreloadComplete)
            {
                _timer.Stop();

                DispatcherTimer delayTimer = new DispatcherTimer();
                delayTimer.Interval = TimeSpan.FromMilliseconds(300);

                delayTimer.Tick += (s, args) =>
                {
                    delayTimer.Stop();

                    var app = System.Windows.Application.Current;
                    if (app == null)
                        return;

                    var main = app.MainWindow as MainWindow;

                    if (main == null)
                    {
                        main = new MainWindow();
                        app.MainWindow = main;
                    }

                    main.Opacity = 1.0;
                    main.ShowInTaskbar = true;

                    if (main.Visibility != Visibility.Visible)
                        main.Show();

                    main.Activate();

                    this.Close();
                };

                delayTimer.Start();
            }
        }
        private void DrawSpinner(int angle)
        {
            SpinnerCanvas.Children.Clear();

            int segments = 12;
            double radius = 32;
            Point center = new Point(32, 32);

            for (int i = 0; i < segments; i++)
            {
                double a = (i * 360.0 / segments + angle) * Math.PI / 180.0;
                double lx = center.X + (radius - 10) * Math.Cos(a);
                double ly = center.Y + (radius - 10) * Math.Sin(a);
                double size = 6.0 * (1.0 - (i / (double)segments));

                Ellipse ellipse = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new SolidColorBrush(Color.FromArgb(
                        (byte)(200 * (1.0 - (i / (double)segments))),
                        _spinnerBaseColor.R,
                        _spinnerBaseColor.G,
                        _spinnerBaseColor.B))
                };

                Canvas.SetLeft(ellipse, lx - size / 2);
                Canvas.SetTop(ellipse, ly - size / 2);
                SpinnerCanvas.Children.Add(ellipse);
            }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                this.Close();
        }
    }
}

