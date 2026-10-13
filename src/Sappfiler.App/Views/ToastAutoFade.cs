using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace GameTimeTracker.App.Views;

/// <summary>
/// 「右下角悬浮提示」（今日页与「多设备同步设置」页的那两个 <see cref="InfoBar"/>）的
/// <b>自动渐隐收起</b>（2026-10-13 用户要求）。
///
/// <para>
/// 行为：
/// <list type="bullet">
///   <item>显示后等待 <see cref="Delay"/>（默认 <b>3 秒</b>）⇒ 开始 <see cref="FadeDuration"/>
///         （默认 <b>1300 毫秒</b>）的**缓慢渐隐** ⇒ 完全透明后才真正收起（不会"啪"地跳没）；</item>
///   <item><b>鼠标移上去 ⇒ 立刻取消收起</b>（连正在渐隐的也复原成完全不透明）；</item>
///   <item><b>鼠标移开 ⇒ 重新倒数</b> <see cref="Delay"/> 秒，之后再次渐隐；</item>
///   <item>用户点右上的 ✕ 关闭时，计时与动画一并停掉（不会残留一个正在跑的渐隐）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ <b>是否自动收起由调用方决定</b>（<c>autoHide</c>）：既有的产品规则是
/// "一次性结果（成功/信息）才自动收起，进行中与错误必须留着"
/// —— 长任务要能看到进展，错误往往要读全并照着重试。本类只负责"怎么收"。
/// </para>
/// </summary>
internal sealed class ToastAutoFade
{
    private readonly InfoBar _bar;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    private Storyboard? _fade;
    private bool _isPointerOver;
    private bool _autoHide;

    /// <param name="bar">要接管的提示条。</param>
    /// <param name="delay">完全静止多久后开始渐隐（默认 3 秒）。</param>
    /// <param name="fadeDuration">渐隐时长（默认 800 毫秒，刻意偏慢）。</param>
    public ToastAutoFade(InfoBar bar, TimeSpan? delay = null, TimeSpan? fadeDuration = null)
    {
        _bar = bar;
        Delay = delay ?? TimeSpan.FromSeconds(3);
        // ⚠️ 2026-10-13 用户反馈"再慢一点点"：800ms ⇒ 1300ms（收尾更缓）。
        FadeDuration = fadeDuration ?? TimeSpan.FromMilliseconds(1300);

        _timer = bar.DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = Delay;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!_isPointerOver) StartFade();
        };

        // 鼠标移上去：不合上（正在渐隐的也复原）。
        _bar.PointerEntered += (_, _) =>
        {
            _isPointerOver = true;
            CancelFade(restoreOpacity: true);
        };

        // 鼠标移开：重新倒数。
        _bar.PointerExited += (_, _) =>
        {
            _isPointerOver = false;
            if (_autoHide) RestartCountdown();
        };

        // 用户自己点 ✕：停掉一切，避免残留一个还在跑的渐隐。
        _bar.CloseButtonClick += (_, _) => StopAll();
    }

    /// <summary>开始渐隐前的静止时长。</summary>
    public TimeSpan Delay { get; }

    /// <summary>渐隐动画时长。</summary>
    public TimeSpan FadeDuration { get; }

    /// <summary>
    /// 显示提示。
    /// </summary>
    /// <param name="title">可选标题（两个页面的既有风格不同，允许为 null 表示不设标题）。</param>
    /// <param name="message">正文（调用方负责脱敏）。</param>
    /// <param name="severity">严重级别（决定配色/图标）。</param>
    /// <param name="autoHide">
    /// 是否自动渐隐收起。<c>false</c> = 保持可见（进行中、警告、错误），且鼠标移开也不会开始倒数。
    /// </param>
    public void Show(string? title, string message, InfoBarSeverity severity, bool autoHide)
    {
        // 重新显示前先把上一次的动画/计时清干净，并强制回到不透明（否则会继承上一次的透明度）。
        StopAll();
        _bar.Opacity = 1;

        _bar.Severity = severity;
        if (title is not null) _bar.Title = title;
        _bar.Message = message;
        _bar.IsOpen = true;

        _autoHide = autoHide;

        // 鼠标此刻就停在上面时不倒数（用户正在看），移开后照常开始倒数。
        if (autoHide && !_isPointerOver) RestartCountdown();
    }

    /// <summary>立即收起（不渐隐），例如页面离开或需要立刻清场时使用。</summary>
    public void HideNow()
    {
        StopAll();
        _bar.Opacity = 1;
        _bar.IsOpen = false;
    }

    private void RestartCountdown()
    {
        _timer.Stop();
        _timer.Start();
    }

    private void StartFade()
    {
        CancelFade(restoreOpacity: false);

        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(FadeDuration),
            // 先快后慢，收尾更"柔"，避免在最后几帧突然消失。
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        Storyboard.SetTarget(animation, _bar);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            _bar.IsOpen = false;
            // 复位，保证下一次显示是可见的。
            _bar.Opacity = 1;
            _fade = null;
        };

        _fade = storyboard;
        storyboard.Begin();
    }

    /// <summary>停掉渐隐动画；<paramref name="restoreOpacity"/> = true 时把透明度还原（鼠标移上去的情形）。</summary>
    private void CancelFade(bool restoreOpacity)
    {
        _timer.Stop();

        if (_fade is not null)
        {
            _fade.Stop();
            _fade = null;
        }

        if (restoreOpacity && _bar.IsOpen) _bar.Opacity = 1;
    }

    private void StopAll()
    {
        _timer.Stop();

        if (_fade is not null)
        {
            _fade.Stop();
            _fade = null;
        }
    }
}
