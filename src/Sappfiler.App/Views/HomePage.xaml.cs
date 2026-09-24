using GameTimeTracker.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

public sealed partial class HomePage : Page
{
    private HomeViewModel _viewModel = null!;
    public HomeViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            HookHeroTransitionHandler();
        }
    }

    private bool _isNarrowLayout;

    public HomePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += (s, e) => HookHeroTransitionHandler();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is HomeViewModel vm)
        {
            ViewModel = vm;
        }
    }

    private void HookHeroTransitionHandler()
    {
        if (_viewModel == null) return;

        // 多个正在运行的游戏轮播切换时的平滑横向滑入淡出（丝滑轮播切换动效）
        _viewModel.HeroTransitionHandler = async (updateAction) =>
        {
            try
            {
                var tcsOut = new TaskCompletionSource<bool>();
                var sbOut = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

                // 1. 退场动画：旧卡片渐隐微缩 (Opacity 1.0 -> 0.0, Scale 1.0 -> 0.96)
                var animFadeOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    EnableDependentAnimation = true
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animFadeOut, HeroCardContentGrid);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animFadeOut, "Opacity");
                sbOut.Children.Add(animFadeOut);

                var animScaleXOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.96,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    EnableDependentAnimation = true
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animScaleXOut, HeroCardScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animScaleXOut, "ScaleX");
                sbOut.Children.Add(animScaleXOut);

                var animScaleYOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.96,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    EnableDependentAnimation = true
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animScaleYOut, HeroCardScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animScaleYOut, "ScaleY");
                sbOut.Children.Add(animScaleYOut);

                sbOut.Completed += (s, ev) => tcsOut.TrySetResult(true);
                sbOut.Begin();
                await tcsOut.Task;

                // 2. 切换数据
                await updateAction();

                // 3. 进场动画：新卡片从 0.98 微放至 1.0 并渐显 (Opacity 0.0 -> 1.0, Scale 0.98 -> 1.00)
                var tcsIn = new TaskCompletionSource<bool>();
                var sbIn = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

                var animFadeIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(180),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    EnableDependentAnimation = true
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animFadeIn, HeroCardContentGrid);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animFadeIn, "Opacity");
                sbIn.Children.Add(animFadeIn);

                var animScaleXIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0.98,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(180),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    EnableDependentAnimation = true
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animScaleXIn, HeroCardScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animScaleXIn, "ScaleX");
                sbIn.Children.Add(animScaleXIn);

                var animScaleYIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0.98,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(180),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    EnableDependentAnimation = true
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animScaleYIn, HeroCardScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animScaleYIn, "ScaleY");
                sbIn.Children.Add(animScaleYIn);

                sbIn.Completed += (s, ev) => tcsIn.TrySetResult(true);
                sbIn.Begin();
                await tcsIn.Task;
            }
            catch
            {
                await updateAction();
                HeroCardContentGrid.Opacity = 1.0;
                HeroCardScale.ScaleX = 1.0;
                HeroCardScale.ScaleY = 1.0;
            }
        };
    }

    private void OnHeroCardPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.IsHeroCarouselPaused = true;
    }

    private void OnHeroCardPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.IsHeroCarouselPaused = false;
    }

    /// <summary>
    /// Hero 卡片右上角的平台胶囊（仅 Steam 有链接）被点击时，用系统默认浏览器打开商店页。
    /// </summary>
    private async void OnOpenStoreClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var url = ViewModel?.CurrentGameStoreUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch
        {
            // 打开失败无需打扰用户：这只是一个便利入口，不应把异常抛到 UI 线程。
        }
    }

    private void OnRootGridSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        // ⚠️ 这个回调必须挂在**最外层 ScrollViewer（RootScroll）**上，不能挂在 RootContentGrid 上。
        //    Grid 的宽度会被星号列里的内容反向影响 —— 热力图那 52 周 × 16px = 832px
        //    一旦被当成内容的"期望宽度"上报，Grid 就会从 ~415px 涨到 ~1250px，
        //    于是这里判定成"宽布局"，而窗口其实还窄着 → 右列被甩到屏幕外。
        //    挂在 ScrollViewer 上就与内容解耦：它的尺寸只由窗口决定。
        //    （配合 XAML 里补的 HorizontalScrollMode="Disabled"，两头都堵住。）
        //
        // 另外：只在布局模式真正切换时才修改属性。若在 SizeChanged 回调里无条件重设
        // 列宽与行列归属，会与布局过程相互触发，存在反复布局乃至栈溢出的风险。
        var width = e.NewSize.Width;

        // ── 分辨率自适应（2026-09-19 QA 的 2K 反馈）──────────────────────────────
        // 布局是按 1080p 调的：右列固定 340，内容一直拉满窗口。
        // 2K / 4K 下窗口宽得多，右列却还是 340 —— 看起来就是"右边第三块被挤扁了"。
        //
        // 两条措施：
        //   ① 给内容宽度设上限（约 1520~1840）并用**左右对称留白**居中。
        //      这里刻意用 Padding 而不是 MaxWidth + HorizontalAlignment=Center ——
        //      后者会让 Grid 退化成"按内容自适应宽度"，而星号列的期望宽度来自内容，
        //      于是左列会塌掉、布局整个错位。
        //   ② 右列按内容宽度取 26%，夹在 340~460。
        //      1080p 下算出来是 300 → 被夹回 340，**与原布局完全一致**（无回归）。
        var sidePadding = Math.Clamp((width - 1520) / 2, 24, 1000);
        var contentWidth = width - sidePadding * 2;
        var rightWidth = Math.Clamp(contentWidth * 0.26, 340, 460);

        // Padding 变了才算，避免无谓的布局失效
        if (Math.Abs(RootContentGrid.Padding.Left - sidePadding) > 0.5)
        {
            RootContentGrid.Padding = new Microsoft.UI.Xaml.Thickness(sidePadding, 16, sidePadding, 24);
        }

        var narrow = width < 880;
        if (narrow != _isNarrowLayout)
        {
            _isNarrowLayout = narrow;

            if (narrow)
            {
                ColRight.Width = new Microsoft.UI.Xaml.GridLength(0);
                RowRight.Height = Microsoft.UI.Xaml.GridLength.Auto;
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(RightPanel, 0);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(RightPanel, 1);
            }
            else
            {
                ColRight.Width = new Microsoft.UI.Xaml.GridLength(rightWidth);
                RowRight.Height = new Microsoft.UI.Xaml.GridLength(0);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(RightPanel, 1);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(RightPanel, 0);
            }

            return;
        }

        // 模式没变，但窗口可能已经跨到另一档宽度 —— 只调右列宽度，
        // 不动行列归属（改动越少，越不容易和布局过程互相触发）。
        if (!narrow && Math.Abs(ColRight.Width.Value - rightWidth) > 0.5)
        {
            ColRight.Width = new Microsoft.UI.Xaml.GridLength(rightWidth);
        }
    }
}
