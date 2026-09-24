using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace GameTimeTracker.App.Controls;

public sealed partial class MarqueeTextBlock : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MarqueeTextBlock),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private Storyboard? _marqueeStoryboard;
    private Storyboard? _returnStoryboard;
    private bool _isHovered;

    public MarqueeTextBlock()
    {
        InitializeComponent();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarqueeTextBlock control)
        {
            control.ResetToDefaultState();
        }
    }

    private void OnRootContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isHovered)
        {
            UpdateClip();
        }
    }

    private void UpdateClip()
    {
        if (RootContainer.ActualWidth > 0 && RootContainer.ActualHeight > 0)
        {
            RootContainer.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, RootContainer.ActualWidth, RootContainer.ActualHeight)
            };
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isHovered) return;
        _isHovered = true;
        _returnStoryboard?.Stop();

        UpdateClip();

        // 在不受任何宽度约束的 Canvas 内精确测量完整文字宽度
        MarqueeTextBlockElement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fullTextWidth = MarqueeTextBlockElement.DesiredSize.Width;
        double availableWidth = RootContainer.ActualWidth;
        if (availableWidth <= 0)
        {
            availableWidth = NormalTextBlock.ActualWidth;
        }

        bool isTrimmed = NormalTextBlock.IsTextTrimmed;

        // 如果文本未被截断且未超出可用宽度，无需触发跑马灯（必须重置 _isHovered 避免后续悬停锁死）
        if (!isTrimmed && (availableWidth <= 0 || fullTextWidth <= availableWidth + 2))
        {
            _isHovered = false;
            return;
        }

        // 精确获取 NormalTextBlock 当前在 RootContainer 内的实际物理渲染绝对坐标 (X, Y)
        // 保证跑马灯启动瞬间的文本坐标与 NormalTextBlock 完全重合，绝不发生任何像素级下沉或跳动
        double left = 0;
        double top = 0;
        try
        {
            GeneralTransform gt = NormalTextBlock.TransformToVisual(RootContainer);
            Point origin = gt.TransformPoint(new Point(0, 0));
            left = origin.X;
            top = origin.Y;
        }
        catch
        {
            top = Math.Max(0, (RootContainer.ActualHeight - MarqueeTextBlockElement.DesiredSize.Height) / 2);
        }

        Canvas.SetLeft(MarqueeTextBlockElement, left);
        Canvas.SetTop(MarqueeTextBlockElement, top);

        // 仅切换 Opacity，NormalTextBlock 始终保持 Visible 锚定容器物理尺寸，绝不引起父级重排或抖动
        NormalTextBlock.Opacity = 0;
        MarqueeCanvas.Opacity = 1;
        TextTransform.X = 0;

        // 计算滚过全部文字所需的总位移（确保末尾字符完全呈现并留有 24px 缓冲）
        double distance = Math.Max(28.0, fullTextWidth - availableWidth + 24);
        double durationSeconds = Math.Max(2.0, distance / 40.0);

        _marqueeStoryboard?.Stop();
        _marqueeStoryboard = new Storyboard
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        var anim = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = new Duration(TimeSpan.FromSeconds(durationSeconds)),
            BeginTime = TimeSpan.FromMilliseconds(400),
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(anim, TextTransform);
        Storyboard.SetTargetProperty(anim, "X");
        _marqueeStoryboard.Children.Add(anim);
        _marqueeStoryboard.Begin();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isHovered) return;
        _isHovered = false;
        _marqueeStoryboard?.Stop();

        if (Math.Abs(TextTransform.X) > 1.0)
        {
            _returnStoryboard?.Stop();
            _returnStoryboard = new Storyboard();
            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(anim, TextTransform);
            Storyboard.SetTargetProperty(anim, "X");
            _returnStoryboard.Children.Add(anim);
            _returnStoryboard.Completed += (s, ev) =>
            {
                if (!_isHovered)
                {
                    ResetToDefaultState();
                }
            };
            _returnStoryboard.Begin();
        }
        else
        {
            ResetToDefaultState();
        }
    }

    private void ResetToDefaultState()
    {
        _isHovered = false;
        _marqueeStoryboard?.Stop();
        _returnStoryboard?.Stop();
        TextTransform.X = 0;
        MarqueeCanvas.Opacity = 0;
        NormalTextBlock.Opacity = 1;
    }
}
