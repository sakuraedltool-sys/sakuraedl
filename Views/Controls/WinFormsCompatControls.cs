using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FormsListViewItem = System.Windows.Forms.ListViewItem;
using DrawingColor = System.Drawing.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace SakuraEDL.Views.Controls
{
    internal static class CompatDispatcher
    {
        public static bool InvokeRequired(DispatcherObject obj) => !obj.Dispatcher.CheckAccess();

        public static void Invoke(DispatcherObject obj, Action action)
        {
            if (obj.Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            obj.Dispatcher.Invoke(action);
        }

        public static void BeginInvoke(DispatcherObject obj, Action action)
        {
            obj.Dispatcher.BeginInvoke(action);
        }

        public static void BeginInvoke<T1, T2>(DispatcherObject obj, Action<T1, T2> action, T1 arg1, T2 arg2)
        {
            obj.Dispatcher.BeginInvoke(new Action(() => action(arg1, arg2)));
        }

        public static SolidColorBrush ToBrush(DrawingColor color, SolidColorBrush fallback)
        {
            if (color.IsEmpty)
            {
                return fallback;
            }

            return new SolidColorBrush(MediaColor.FromArgb(color.A, color.R, color.G, color.B));
        }
    }

    public class CompatButton : Button
    {
        public string Text
        {
            get => Content?.ToString() ?? string.Empty;
            set => Content = value;
        }

        public bool Enabled
        {
            get => IsEnabled;
            set => IsEnabled = value;
        }

        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);
    }

    public class CompatLabel : Label
    {
        public string Text
        {
            get => Content?.ToString() ?? string.Empty;
            set => Content = value;
        }

        public bool Enabled
        {
            get => IsEnabled;
            set => IsEnabled = value;
        }

        public DrawingColor ForeColor
        {
            get
            {
                if (Foreground is SolidColorBrush brush)
                {
                    return DrawingColor.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
                }

                return DrawingColor.Empty;
            }
            set => Foreground = CompatDispatcher.ToBrush(value, new SolidColorBrush(Colors.Black));
        }

        public DrawingColor BackColor
        {
            get
            {
                if (Background is SolidColorBrush brush)
                {
                    return DrawingColor.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);
                }

                return DrawingColor.Empty;
            }
            set => Background = CompatDispatcher.ToBrush(value, new SolidColorBrush(Colors.Transparent));
        }

        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);
    }

    public class CompatCheckBox : CheckBox
    {
        public event EventHandler? CheckedChanged;

        public CompatCheckBox()
        {
            Checked += OnCheckedChanged;
            Unchecked += OnCheckedChanged;
        }

        public string Text
        {
            get => Content?.ToString() ?? string.Empty;
            set => Content = value;
        }

        public bool Enabled
        {
            get => IsEnabled;
            set => IsEnabled = value;
        }

        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);

        private void OnCheckedChanged(object sender, RoutedEventArgs e)
        {
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public class CompatRadioButton : RadioButton
    {
        public string Text
        {
            get => Content?.ToString() ?? string.Empty;
            set => Content = value;
        }

        public bool Enabled
        {
            get => IsEnabled;
            set => IsEnabled = value;
        }

        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);
    }

    public class CompatTextBox : TextBox
    {
        public event EventHandler? DoubleClick;
        public event EventHandler? SuffixClick;

        public CompatTextBox()
        {
            MouseDoubleClick += (_, __) =>
            {
                DoubleClick?.Invoke(this, EventArgs.Empty);
                SuffixClick?.Invoke(this, EventArgs.Empty);
            };
        }

        public bool Enabled
        {
            get => IsEnabled;
            set => IsEnabled = value;
        }

        public string PlaceholderText
        {
            get => (string)GetValue(PlaceholderTextProperty);
            set => SetValue(PlaceholderTextProperty, value);
        }

        public string Watermark
        {
            get => PlaceholderText;
            set => PlaceholderText = value;
        }

        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);

        public void BeginInvoke<T1, T2>(Action<T1, T2> action, T1 arg1, T2 arg2) => CompatDispatcher.BeginInvoke(this, action, arg1, arg2);

        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(CompatTextBox), new PropertyMetadata(string.Empty));
    }

    public class CompatComboBox : ComboBox
    {
        public event EventHandler? SelectedIndexChanged;
        public event EventHandler? DoubleClick;
        public event TextChangedEventHandler? TextChanged;

        public CompatComboBox()
        {
            SelectionChanged += (_, __) => SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            MouseDoubleClick += (_, __) => DoubleClick?.Invoke(this, EventArgs.Empty);
            AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler((sender, args) => TextChanged?.Invoke(this, args)));
        }

        public bool Enabled
        {
            get => IsEnabled;
            set => IsEnabled = value;
        }

        public string PlaceholderText
        {
            get => (string)GetValue(PlaceholderTextProperty);
            set => SetValue(PlaceholderTextProperty, value);
        }

        public string Watermark
        {
            get => PlaceholderText;
            set => PlaceholderText = value;
        }

        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);

        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(CompatComboBox), new PropertyMetadata(string.Empty));
    }

    public sealed class CompatSelectItem
    {
        public CompatSelectItem(string text)
        {
            Text = text;
        }

        public string Text { get; }

        public object? Tag { get; set; }

        public override string ToString() => Text;
    }

    public class CompatComboBoxItem : ComboBoxItem
    {
    }

    public class CompatGroupBox : GroupBox
    {
        public string Text
        {
            get => Header?.ToString() ?? string.Empty;
            set => Header = value;
        }

        public bool Enabled
        {
            get => IsEnabled;
            set => IsEnabled = value;
        }
    }

    public class CompatTabItem : TabItem
    {
        public string Text
        {
            get => Header?.ToString() ?? string.Empty;
            set => Header = value;
        }
    }

    public class CompatProgressBar : ProgressBar
    {
        private FrameworkElement? _track;
        private FrameworkElement? _indicator;

        public CompatProgressBar()
        {
            SizeChanged += (_, _) => UpdateIndicatorVisual();
        }

        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _track = GetTemplateChild("PART_Track") as FrameworkElement;
            _indicator = GetTemplateChild("PART_Indicator") as FrameworkElement;

            if (_track != null)
            {
                _track.SizeChanged -= Track_SizeChanged;
                _track.SizeChanged += Track_SizeChanged;
            }

            UpdateIndicatorVisual();
        }

        protected override void OnValueChanged(double oldValue, double newValue)
        {
            base.OnValueChanged(oldValue, newValue);
            UpdateIndicatorVisual();
        }

        protected override void OnMinimumChanged(double oldMinimum, double newMinimum)
        {
            base.OnMinimumChanged(oldMinimum, newMinimum);
            UpdateIndicatorVisual();
        }

        protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
        {
            base.OnMaximumChanged(oldMaximum, newMaximum);
            UpdateIndicatorVisual();
        }

        private void Track_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateIndicatorVisual();
        }

        private void UpdateIndicatorVisual()
        {
            if (_track == null || _indicator == null)
            {
                return;
            }

            double trackWidth = Math.Max(0, _track.ActualWidth);
            if (trackWidth <= 0)
            {
                _indicator.Width = 0;
                return;
            }

            if (IsIndeterminate)
            {
                _indicator.Width = Math.Max(trackWidth * 0.28, 32);
                return;
            }

            double range = Maximum - Minimum;
            double normalized = range <= 0 ? 0 : (Value - Minimum) / range;
            normalized = Math.Max(0, Math.Min(1, normalized));

            _indicator.Width = trackWidth * normalized;
        }
    }

    public class CompatRichTextBox : RichTextBox
    {
        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);

        public void BeginInvoke<T1, T2>(Action<T1, T2> action, T1 arg1, T2 arg2) => CompatDispatcher.BeginInvoke(this, action, arg1, arg2);
    }

    public class CompatColumnHeader
    {
        private readonly GridViewColumn _column;

        public CompatColumnHeader(GridViewColumn column)
        {
            _column = column;
        }

        public string Text
        {
            get => _column.Header?.ToString() ?? string.Empty;
            set => _column.Header = value;
        }
    }

    public class CompatListView : ListView
    {
        private sealed class RowModel : INotifyPropertyChanged
        {
            private readonly FormsListViewItem _item;

            public RowModel(FormsListViewItem item)
            {
                _item = item;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public FormsListViewItem SourceItem => _item;

            public bool IsChecked
            {
                get => _item.Checked;
                set
                {
                    if (_item.Checked == value)
                    {
                        return;
                    }

                    _item.Checked = value;
                    RaiseAll();
                }
            }

            public string Column0 => GetColumnText(0);
            public string Column1 => GetColumnText(1);
            public string Column2 => GetColumnText(2);
            public string Column3 => GetColumnText(3);
            public string Column4 => GetColumnText(4);
            public string Column5 => GetColumnText(5);
            public string Column6 => GetColumnText(6);
            public string Column7 => GetColumnText(7);
            public string Column8 => GetColumnText(8);
            public MediaBrush RowBackground => CompatDispatcher.ToBrush(_item.BackColor, new SolidColorBrush(Colors.Transparent));
            public MediaBrush RowForeground => CompatDispatcher.ToBrush(_item.ForeColor, new SolidColorBrush(Colors.Black));

            public void Refresh() => RaiseAll();

            private string GetColumnText(int index)
            {
                if (index == 0)
                {
                    return _item.Text ?? string.Empty;
                }

                return _item.SubItems.Count > index ? _item.SubItems[index].Text ?? string.Empty : string.Empty;
            }

            private void RaiseAll()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column0)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column1)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column2)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column3)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column4)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column5)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column6)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column7)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Column8)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowForeground)));
            }
        }

        private sealed class CompatItemCollection : Collection<FormsListViewItem>
        {
            private readonly CompatListView _owner;

            public CompatItemCollection(CompatListView owner)
            {
                _owner = owner;
            }

            protected override void ClearItems()
            {
                base.ClearItems();
                _owner.RefreshItems();
            }

            protected override void InsertItem(int index, FormsListViewItem item)
            {
                base.InsertItem(index, item);
                _owner.RefreshItems();
            }

            protected override void RemoveItem(int index)
            {
                base.RemoveItem(index);
                _owner.RefreshItems();
            }

            protected override void SetItem(int index, FormsListViewItem item)
            {
                base.SetItem(index, item);
                _owner.RefreshItems();
            }
        }

        private readonly ObservableCollection<RowModel> _rows = new();
        private readonly CompatItemCollection _items;
        private readonly List<CompatColumnHeader> _columns = new();
        private bool _syncingSelection;
        private bool _loaded;
        private int _updateDepth;
        private bool _checkBoxes;

        public CompatListView()
        {
            _items = new CompatItemCollection(this);
            base.ItemsSource = _rows;
            Loaded += OnLoaded;
            SelectionChanged += OnSelectionChanged;
            MouseDoubleClick += (_, __) => DoubleClick?.Invoke(this, EventArgs.Empty);
        }

        public new Collection<FormsListViewItem> Items => _items;

        public new IList<FormsListViewItem> SelectedItems => base.SelectedItems.Cast<RowModel>().Select(row => row.SourceItem).ToList();

        public IList<FormsListViewItem> CheckedItems => _items.Where(item => item.Checked).ToList();

        public IList<CompatColumnHeader> Columns => _columns;

        public bool MultiSelect
        {
            get => SelectionMode != SelectionMode.Single;
            set => SelectionMode = value ? SelectionMode.Extended : SelectionMode.Single;
        }

        public bool CheckBoxes
        {
            get => _checkBoxes;
            set
            {
                _checkBoxes = value;
                if (_loaded)
                {
                    ConfigureColumns();
                }
            }
        }

        public bool FullRowSelect { get; set; }

        public bool InvokeRequired => CompatDispatcher.InvokeRequired(this);

        public event EventHandler? DoubleClick;

        public void Invoke(Action action) => CompatDispatcher.Invoke(this, action);

        public void BeginInvoke(Action action) => CompatDispatcher.BeginInvoke(this, action);

        public void BeginUpdate()
        {
            _updateDepth++;
        }

        public void EndUpdate()
        {
            if (_updateDepth > 0)
            {
                _updateDepth--;
            }

            if (_updateDepth == 0)
            {
                RefreshItems();
            }
        }

        public void RefreshItems()
        {
            if (!_loaded || _updateDepth > 0)
            {
                return;
            }

            _syncingSelection = true;
            try
            {
                _rows.Clear();
                foreach (FormsListViewItem item in _items)
                {
                    _rows.Add(new RowModel(item));
                }

                base.SelectedItems.Clear();
                foreach (RowModel row in _rows.Where(row => row.SourceItem.Selected))
                {
                    base.SelectedItems.Add(row);
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            SyncColumns();
            ConfigureColumns();
            RefreshItems();
        }

        private void SyncColumns()
        {
            _columns.Clear();

            if (View is not GridView gridView)
            {
                return;
            }

            foreach (GridViewColumn column in gridView.Columns)
            {
                _columns.Add(new CompatColumnHeader(column));
            }
        }

        private void ConfigureColumns()
        {
            if (View is not GridView gridView)
            {
                return;
            }

            for (int i = 0; i < gridView.Columns.Count; i++)
            {
                GridViewColumn column = gridView.Columns[i];
                if (i == 0 && CheckBoxes)
                {
                    column.CellTemplate ??= BuildCheckboxTemplate();
                    column.DisplayMemberBinding = null;
                }
                else if (column.CellTemplate == null && column.DisplayMemberBinding == null)
                {
                    column.DisplayMemberBinding = new Binding($"Column{i}");
                }
            }
        }

        private static DataTemplate BuildCheckboxTemplate()
        {
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(RowModel.IsChecked)) { Mode = BindingMode.TwoWay });

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(RowModel.Column0)));

            panel.AppendChild(check);
            panel.AppendChild(text);

            return new DataTemplate { VisualTree = panel };
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection)
            {
                return;
            }

            foreach (RowModel row in _rows)
            {
                row.SourceItem.Selected = base.SelectedItems.Contains(row);
            }
        }
    }
}
