using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace GameTimeTracker.App.Controls;

public sealed partial class RoundedProgressBar : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(RoundedProgressBar),
            new PropertyMetadata(0.0, OnValueChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum),
            typeof(double),
            typeof(RoundedProgressBar),
            new PropertyMetadata(100.0, OnMaximumChanged));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private bool _isLoaded;
    private Storyboard? _widthStoryboard;

    public RoundedProgressBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        UpdateLayoutAndProgress(false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _widthStoryboard?.Stop();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RoundedProgressBar bar)
        {
            bar.UpdateLayoutAndProgress(bar._isLoaded);
        }
    }

    private static void OnMaximumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RoundedProgressBar bar)
        {
            bar.UpdateLayoutAndProgress(false);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutAndProgress(false);
    }

    private void UpdateLayoutAndProgress(bool animate)
    {
        double width = RootGrid.ActualWidth;
        if (width <= 0) return;

        double max = Maximum > 0 ? Maximum : 100.0;
        double ratio = Math.Clamp(Value / max, 0.0, 1.0);
        double targetWidth = width * ratio;

        if (targetWidth <= 0)
        {
            _widthStoryboard?.Stop();
            IndicatorBorder.Visibility = Visibility.Collapsed;
            IndicatorBorder.Width = 0;
            return;
        }

        IndicatorBorder.Visibility = Visibility.Visible;

        if (animate && Math.Abs(IndicatorBorder.ActualWidth - targetWidth) > 1.0)
        {
            _widthStoryboard?.Stop();
            _widthStoryboard = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = targetWidth,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, IndicatorBorder);
            Storyboard.SetTargetProperty(anim, "Width");
            _widthStoryboard.Children.Add(anim);
            _widthStoryboard.Begin();
        }
        else
        {
            _widthStoryboard?.Stop();
            IndicatorBorder.Width = targetWidth;
        }
    }
}
