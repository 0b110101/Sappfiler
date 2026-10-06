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

        // 月份标签层右侧需要追加的"空列"数 —— **只有标签层加，矩阵列数严格不变**。
        // 追加列会真实计入 MonthHeaderGrid 的期望宽度（Grid 期望宽 = Σ列宽），
        // 因而是"结构性"地扩大 ScrollViewer content width，不依赖 Margin / 溢出渲染的具体实现。
        int headerExtraCols = ComputeMonthHeaderTrailingColumns(data.MonthMarkers, totalWeeks);
        int headerCols = totalWeeks + headerExtraCols;

        for (int c = 0; c < headerCols; c++)
        {
            MonthHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_currentStep) });
        }

        // 矩阵层列数必须等于 totalWeeks（不给矩阵加无意义的右侧空白）
        for (int c = 0; c < totalWeeks; c++)
        {
            MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_currentStep) });
        }

        // 3. Render Month Headers
        // ⚠️ marker.ColumnIndex **绝不修改** —— 它同时决定标签左缘与相邻月份间距；
        //    最后一个月（如 10月）落在最后一列时，靠上面追加的空列取得完整绘制宽度，
        //    既不左移、也不会与上一个月标签重叠。
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
                // 可用列数 = 从本列到标签层最右（含追加空列），上限仍是 4 列
                Grid.SetColumnSpan(tb, Math.Min(4, headerCols - marker.ColumnIndex));
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

    /// <summary>
    /// 月份标签层右侧需要追加的**空列数**（仅标签层，矩阵不加）。
    ///
    /// <para>背景（2026-10-07）：当月 marker 会落在最后一列 —— 例：10 月的首个周日 10/4 正好是
    /// 尾列的周日 ⇒ <c>ColumnIndex == totalWeeks - 1</c>。渲染层给它 <c>ColumnSpan = 4</c>，
    /// 但 Grid 会把 span 截到"已存在的列数"✗ ⇒ 最末月份标签只剩 1 列宽（≈12.5~26px），
    /// 而「10月」需要 ≈27px ⇒ 末尾字符被硬裁。</para>
    ///
    /// <para>这里**不移动任何 marker**（左移会与上一个月标签重叠），改为在标签层右侧追加空列，
    /// 使所需绘制宽度**真实存在于 ScrollViewer 的 content 内**。</para>
    ///
    /// <para>宽度来源：对所有标签做实测 —— WinUI 允许对尚未入树的元素调用
    /// <c>Measure(infinity, infinity)</c>，其 <c>DesiredSize.Width</c> 即文本自然宽度。
    /// 若测量不可用（返回 0，例如字体尚未解析）则退回 <b>3 × FontSize</b> 的保守值：
    /// 「10月」= 2 个数字 + 1 个全角字 ≈ 0.62em×2 + 1em ≈ 2.3em ⇒ 3em 是安全上界。</para>
    /// </summary>
    private int ComputeMonthHeaderTrailingColumns(
        IReadOnlyList<ActivityHeatmapMonthMarker> markers, int totalWeeks)
    {
        if (markers.Count == 0) return 0;

        double fontSize = _currentStep <= 13.5 ? 9 : 10;
        double maxTextWidth = 0;

        foreach (var marker in markers)
        {
            if (marker.ColumnIndex < 0 || marker.ColumnIndex >= totalWeeks) continue;

            var probe = new TextBlock
            {
                Text = marker.MonthLabel,
                FontSize = fontSize,
                TextWrapping = TextWrapping.NoWrap
            };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (probe.DesiredSize.Width > maxTextWidth) maxTextWidth = probe.DesiredSize.Width;
        }

        // 测量不可用 ⇒ 保守值 3em（不依赖具体字体度量）
        if (maxTextWidth <= 0) maxTextWidth = fontSize * 3.0;

        const double safetyPx = 8.0;   // 少量安全余量：抗 DPI 缩放与栅格舍入
        double needed = maxTextWidth + safetyPx;

        // 最坏情况：标签落在最后一列 ⇒ 原本可用 1 列，其余靠追加列补足
        int neededCols = (int)Math.Ceiling(needed / Math.Max(1.0, _currentStep));
        int extra = neededCols - 1;

        return Math.Max(0, Math.Min(extra, 8));   // 上限保护：最多 8 列
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
