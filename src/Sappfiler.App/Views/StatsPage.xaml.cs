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
}
