using System;
using GameTimeTracker.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameTimeTracker.App.Views;

public sealed partial class StatsPage : Page
{
    public StatsViewModel? ViewModel { get; set; }

    public StatsPage()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (ViewModel != null)
        {
            await ViewModel.RefreshDataAsync();
        }
    }

    private void OnTrendChartSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 50 && ViewModel != null)
        {
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 1.0 || Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 1.0)
            {
                ViewModel.UpdateChartSize(e.NewSize.Width, e.NewSize.Height);
            }
        }
    }

    private DateTime _lastMonthClick = DateTime.MinValue;
    private DateTime _lastQuarterClick = DateTime.MinValue;
    private DateTime _lastYearClick = DateTime.MinValue;
    private const double DoubleClickThresholdMs = 500.0;

    private async void OnMonthButtonClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var now = DateTime.UtcNow;
        if ((now - _lastMonthClick).TotalMilliseconds < DoubleClickThresholdMs)
        {
            _lastMonthClick = DateTime.MinValue;
            await ViewModel.ResetToCurrentMonthAsync();
            return;
        }
        _lastMonthClick = now;
        await ViewModel.SetMonthModeAsync();
    }

    private async void OnMonthButtonDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _lastMonthClick = DateTime.MinValue;
        if (ViewModel != null)
        {
            await ViewModel.ResetToCurrentMonthAsync();
        }
    }

    private async void OnQuarterButtonClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var now = DateTime.UtcNow;
        if ((now - _lastQuarterClick).TotalMilliseconds < DoubleClickThresholdMs)
        {
            _lastQuarterClick = DateTime.MinValue;
            await ViewModel.ResetToCurrentQuarterAsync();
            return;
        }
        _lastQuarterClick = now;
        await ViewModel.SetQuarterModeAsync();
    }

    private async void OnQuarterButtonDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _lastQuarterClick = DateTime.MinValue;
        if (ViewModel != null)
        {
            await ViewModel.ResetToCurrentQuarterAsync();
        }
    }

    private async void OnYearButtonClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var now = DateTime.UtcNow;
        if ((now - _lastYearClick).TotalMilliseconds < DoubleClickThresholdMs)
        {
            _lastYearClick = DateTime.MinValue;
            await ViewModel.ResetToCurrentYearAsync();
            return;
        }
        _lastYearClick = now;
        await ViewModel.SetYearModeAsync();
    }

    private async void OnYearButtonDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _lastYearClick = DateTime.MinValue;
        if (ViewModel != null)
        {
            await ViewModel.ResetToCurrentYearAsync();
        }
    }

    private async void OnPeriodTitleDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (ViewModel != null)
        {
            await ViewModel.ResetToCurrentPeriodAsync();
        }
    }
}
