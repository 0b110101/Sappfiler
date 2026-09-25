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
        HeatmapScroll.SizeChanged += OnHeatmapViewportChanged;

        // 主题切换后重建热力图：格子是在代码里创建的，不会自动跟随 ThemeResource 变化
        ActualThemeChanged += (s, e) => RenderHeatmap(HeatmapData);
    }

    /// <summary>格子步进（含间距）下限。按小屏紧凑显示调整，允许在 13~14 寸笔记本视口内完整展示 52 周。</summary>
    private const double MinStep = 12.5;

    /// <summary>格子步进上限。屏幕很宽时不让格子粗到失真。</summary>
    private const double MaxStep = 26.0;

    private double _currentStep = MinStep;

    /// <summary>
    /// 按**可用宽度**算格子步进：放得下就让所有周铺满整行，放不下就维持 MinStep 并横向滚动。
    ///
    /// 取 ViewportWidth 而不是 ExtentWidth 是关键 —— 前者只由控件自身尺寸决定，
    /// 与"我们渲染了多宽的内容"无关，所以重渲染不会反过来改变它，不存在布局循环。
    /// </summary>
    private double ComputeStep(ActivityHeatmapResult? data)
    {
        if (data == null || data.Cells.Count == 0) return MinStep;

        int weeks = Math.Max(52, data.Cells.Max(c => c.WeekIndex) + 1);

        double avail = HeatmapScroll.ViewportWidth;
        if (avail <= 0) avail = HeatmapScroll.ActualWidth;
        if (avail <= 0) return MinStep;   // 还没布局完，先给下限，等 SizeChanged 再纠正

        return Math.Clamp(avail / weeks, MinStep, MaxStep);
    }

    /// <summary>
    /// 可视宽度变化 → 重算格子尺寸。**这是「2K / 4K 自适应」的落点**：
    /// 1080p 下宽度不够，维持 16px 并横向滚动（与原来完全一致）；
    /// 屏幕更宽时格子随之变大、把卡片铺满，而不是缩成一条小色带（雾山反馈的现象）。
    ///
    /// 重渲染排进 Dispatcher 队列：SizeChanged 处于布局过程中，在这里同步改列宽
    /// 会与布局互相触发 —— 本文件 ScrollToEnd 的注释里记着同类事故（栈溢出 0xC00000FD）。
    /// </summary>
    private void OnHeatmapViewportChanged(object sender, SizeChangedEventArgs e)
    {
        ScrollToEnd();

        if (Math.Abs(ComputeStep(HeatmapData) - _currentStep) < 0.5) return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // 队列里再确认一次：期间可能又变过一次尺寸
            if (Math.Abs(ComputeStep(HeatmapData) - _currentStep) < 0.5) return;
            RenderHeatmap(HeatmapData);
            ScrollToEnd();
        });
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

        // 1. 先定格子步进（取决于当前可用宽度，见 ComputeStep）
        _currentStep = ComputeStep(data);
        double gap = _currentStep <= 13.5 ? 2.5 : 4.0;
        double cellSize = Math.Max(8.0, Math.Round(_currentStep - gap));
        double rowHeight = Math.Max(11.0, Math.Round(_currentStep - 1));

        // 2. Setup 7 Rows
        for (int r = 0; r < 7; r++)
        {
            MatrixGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });
        }

        // 左侧「一 / 三 / 五」的行高必须跟矩阵一致，否则星期标签会错位
        for (int r = 0; r < WeekdayLabelGrid.RowDefinitions.Count; r++)
        {
            WeekdayLabelGrid.RowDefinitions[r].Height = new GridLength(rowHeight);
        }

        // 3. Setup Columns dynamically
        int totalWeeks = Math.Max(52, data.Cells.Max(c => c.WeekIndex) + 1);
        for (int c = 0; c < totalWeeks; c++)
        {
            MonthHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_currentStep) });
            MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_currentStep) });
        }

        // 3. Render Month Headers (ColumnSpan 4 ensures 10月, 11月, 12月 never truncate)
        foreach (var marker in data.MonthMarkers)
        {
            if (marker.ColumnIndex >= 0 && marker.ColumnIndex < totalWeeks)
            {
                var tb = new TextBlock
                {
                    Text = marker.MonthLabel,
                    FontSize = _currentStep <= 13.5 ? 9 : 10,
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
                Width = cellSize,
                Height = cellSize,
                CornerRadius = new CornerRadius(_currentStep <= 13.5 ? 2 : 3),
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

                if (cell.Level == 0 && !IsDarkContext(this))
                {
                    border.BorderBrush = GetLevel0BorderBrush(this);
                    border.BorderThickness = new Thickness(1);
                }
                else
                {
                    border.BorderThickness = new Thickness(0);
                }

                if (cell.IsToday && !IsDarkContext(this))
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
        1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x3C, 0x3E, 0x5F)),
        2 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x60, 0x61, 0x87)),
        3 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x84, 0x85, 0xAF)),
        4 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xA8, 0xA8, 0xD7)),
        5 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xCC, 0xCC, 0xFF)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2A, 0x2D, 0x45))
    };
}
