using GameTimeTracker.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GameTimeTracker.App.Controls;

public sealed partial class HeatmapControl : UserControl
{
    public static readonly DependencyProperty HeatmapDataProperty =
        DependencyProperty.Register(
            nameof(HeatmapData),
            typeof(ActivityHeatmapResult),
            typeof(HeatmapControl),
            new PropertyMetadata(null, OnHeatmapDataChanged));

    public ActivityHeatmapResult? HeatmapData
    {
        get => (ActivityHeatmapResult?)GetValue(HeatmapDataProperty);
        set => SetValue(HeatmapDataProperty, value);
    }

    public event EventHandler<ActivityHeatmapCell>? CellSelected;

    public HeatmapControl()
    {
        InitializeComponent();
        HeatmapScroll.Loaded += (s, e) => ScrollToEnd();
        HeatmapScroll.SizeChanged += (s, e) => ScrollToEnd();

        // 主题切换后重建热力图：格子是在代码里创建的，不会自动跟随 ThemeResource 变化
        ActualThemeChanged += (s, e) => RenderHeatmap(HeatmapData);
    }

    private bool _scrollQueued;

    /// <summary>
    /// 把「滚动到最右端」推迟到下一次 Dispatcher 调度执行。
    ///
    /// 绝不能在 SizeChanged / LayoutUpdated 里同步调用 ChangeView(..., disableAnimation: true)：
    /// 该重载会强制一次同步布局，进而再次触发 SizeChanged，形成无界递归，
    /// 实测会导致 stack overflow（异常码 0xC00000FD）使进程直接崩溃。
    /// 这里同时做了三件事来断开递归：异步调度、防重入标志、以及偏移量无变化时不发起滚动。
    /// </summary>
    private void ScrollToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;

            var target = HeatmapScroll.ScrollableWidth;
            if (target <= 0) return;
            if (Math.Abs(HeatmapScroll.HorizontalOffset - target) < 0.5) return;

            HeatmapScroll.ChangeView(target, null, null, true);
        });
    }

    private static void OnHeatmapDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeatmapControl control)
        {
            control.RenderHeatmap(e.NewValue as ActivityHeatmapResult);
        }
    }

    public void RenderHeatmap(ActivityHeatmapResult? data)
    {
        MonthHeaderGrid.ColumnDefinitions.Clear();
        MonthHeaderGrid.Children.Clear();
        MatrixGrid.ColumnDefinitions.Clear();
        MatrixGrid.RowDefinitions.Clear();
        MatrixGrid.Children.Clear();

        if (data == null || data.Cells.Count == 0) return;

        // 1. Setup 7 Rows (16px each)
        for (int r = 0; r < 7; r++)
        {
            MatrixGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        }

        // 2. Setup Columns dynamically (16px each)
        int totalWeeks = Math.Max(52, data.Cells.Max(c => c.WeekIndex) + 1);
        for (int c = 0; c < totalWeeks; c++)
        {
            MonthHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        }

        // 3. Render Month Headers (ColumnSpan 4 ensures 10月, 11月, 12月 never truncate)
        foreach (var marker in data.MonthMarkers)
        {
            if (marker.ColumnIndex >= 0 && marker.ColumnIndex < totalWeeks)
            {
                var tb = new TextBlock
                {
                    Text = marker.MonthLabel,
                    FontSize = 10,
                    Foreground = GetTextSecondaryBrush(this),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(tb, marker.ColumnIndex);
                Grid.SetColumnSpan(tb, 4);
                MonthHeaderGrid.Children.Add(tb);
            }
        }

        // 4. Render Cells
        foreach (var cell in data.Cells)
        {
            if (cell.WeekIndex >= totalWeeks || cell.DayOfWeek >= 7) continue;

            var border = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = cell
            };

            if (cell.IsFuture)
            {
                border.Opacity = 0.0;
                border.IsHitTestVisible = false;
            }
            else
            {
                border.Background = GetThemeBrush(cell.Level, this);

                if (cell.Level == 0)
                {
                    border.BorderBrush = GetLevel0BorderBrush(this);
                    border.BorderThickness = new Thickness(1);
                }
                else
                {
                    border.BorderThickness = new Thickness(0);
                }

                if (cell.IsToday)
                {
                    border.BorderBrush = GetAccentBlueBrush(this);
                    border.BorderThickness = new Thickness(1.5);
                }

                if (!string.IsNullOrEmpty(cell.TooltipText))
                {
                    ToolTipService.SetToolTip(border, cell.TooltipText);
                }

                border.PointerEntered += (s, e) =>
                {
                    border.Opacity = 0.85;
                };
                border.PointerExited += (s, e) =>
                {
                    border.Opacity = 1.0;
                };

                border.Tapped += (s, e) =>
                {
                    CellSelected?.Invoke(this, cell);
                };
            }

            Grid.SetRow(border, cell.DayOfWeek);
            Grid.SetColumn(border, cell.WeekIndex);
            MatrixGrid.Children.Add(border);
        }

        // Scroll to the right end (most recent dates)
        DispatcherQueue.TryEnqueue(() =>
        {
            HeatmapScroll.ChangeView(HeatmapScroll.ScrollableWidth, null, null, true);
        });
    }

    public static Brush GetThemeBrush(int level, FrameworkElement? context = null)
    {
        var brushKey = $"HeatmapLevel{level}Brush";
        if (TryResolveThemeBrush(brushKey, context, out var brush))
        {
            return brush;
        }

        return IsDarkContext(context) ? GetDarkFallbackBrush(level) : GetLightFallbackBrush(level);
    }

    public static Brush GetLevel0BorderBrush(FrameworkElement? context = null)
    {
        if (TryResolveThemeBrush("HeatmapLevel0BorderBrush", context, out var brush))
        {
            return brush;
        }

        return IsDarkContext(context)
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x33, 0x3A, 0x4D))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0xE9, 0xF0));
    }

    public static Brush GetAccentBlueBrush(FrameworkElement? context = null)
    {
        if (TryResolveThemeBrush("AccentBlueBrush", context, out var brush))
        {
            return brush;
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4F, 0x8F, 0xFF));
    }

    public static Brush GetTextSecondaryBrush(FrameworkElement? context = null)
    {
        if (TryResolveThemeBrush("TextSecondaryBrush", context, out var brush))
        {
            return brush;
        }

        return IsDarkContext(context)
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8B, 0x8F, 0xA3))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x64, 0x74, 0x8B));
    }

    /// <summary>
    /// 按【元素所在的实际主题】解析主题化画刷。这里有两个必须避开的坑：
    /// ① ResourceDictionary 的 ThemeDictionaries 不参与普通 TryGetValue，
    ///    原先用 Application.Current.Resources.TryGetValue 永远取不到；
    /// ② 主窗口只覆盖了内容根的 RequestedTheme（见 MainWindow.ApplyTheme），
    ///    Application.Current.RequestedTheme 仍可能是 Light，
    ///    于是深色界面下会取到浅色画刷 —— 热力图空格变成亮色，非常扎眼。
    /// </summary>
    private static bool TryResolveThemeBrush(string key, FrameworkElement? context, out Brush brush)
    {
        brush = null!;
        if (context is null) return false;

        var themeKey = IsDarkContext(context) ? "Dark" : "Light";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictObj) &&
            dictObj is ResourceDictionary dict &&
            dict.TryGetValue(key, out var res) &&
            res is Brush fromTheme)
        {
            brush = fromTheme;
            return true;
        }

        return false;
    }

    private static bool IsDarkContext(FrameworkElement? context)
    {
        var theme = context?.ActualTheme ?? ElementTheme.Default;
        return theme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
    }

    private static Brush GetLightFallbackBrush(int level) => level switch
    {
        1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xDC, 0xEB, 0xFA)),
        2 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xBF, 0xD9, 0xF5)),
        3 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8C, 0xB9, 0xF0)),
        4 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4F, 0x8F, 0xE8)),
        5 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x25, 0x63, 0xD9)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF1, 0xF6, 0xFC))
    };

    private static Brush GetDarkFallbackBrush(int level) => level switch
    {
        1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1E, 0x20, 0x40)),
        2 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x33, 0x33, 0xAA)),
        3 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x66, 0x66, 0xCC)),
        4 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x99, 0x99, 0xDD)),
        5 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xCC, 0xCC, 0xFF)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x26, 0x2B, 0x3A))
    };
}
