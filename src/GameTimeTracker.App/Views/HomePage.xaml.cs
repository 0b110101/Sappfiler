using GameTimeTracker.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; set; } = null!;
    private bool _isDataLoaded;
    private bool _isNarrowLayout;

    public HomePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is HomeViewModel vm)
        {
            ViewModel = vm;
            if (!_isDataLoaded)
            {
                _isDataLoaded = true;
                await ViewModel.RefreshAllDataAsync();
            }
        }
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
        var narrow = e.NewSize.Width < 880;
        if (narrow == _isNarrowLayout) return;
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
            ColRight.Width = new Microsoft.UI.Xaml.GridLength(340);
            RowRight.Height = new Microsoft.UI.Xaml.GridLength(0);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(RightPanel, 1);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(RightPanel, 0);
        }
    }
}
