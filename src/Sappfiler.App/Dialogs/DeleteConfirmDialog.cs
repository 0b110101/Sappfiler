using GameTimeTracker.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GameTimeTracker.App.Dialogs;

/// <summary>
/// 删除确认对话框。
/// 双向删除是**物理删除 + Notion 归档**，之前这两个入口连确认都没有，
/// 误点一下就地删掉一个游戏的全部历史，所以统一在这里收口。
/// </summary>
internal static class DeleteConfirmDialog
{
    /// <summary>确认删除一个游戏。返回 true 表示用户点了"确认删除"。</summary>
    public static async Task<bool> ConfirmGameDeleteAsync(
        XamlRoot? xamlRoot,
        string gameName,
        GameDeletionImpact impact,
        bool notionConfigured)
    {
        if (xamlRoot == null) return false;

        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = $"「{gameName}」",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(Line($"本地将删除 {impact.SessionCount} 条会话记录、{impact.DailyRecordCount} 条每日汇总"));

        if (!notionConfigured)
        {
            panel.Children.Add(Line("Notion 未连接：本次只删本地数据"));
        }
        else if (impact.IsBoundToNotion)
        {
            panel.Children.Add(Line(
                $"Notion 将归档 {impact.SyncedDailyRecordCount} 条每日记录 + 总表里的游戏条目"));
        }
        else
        {
            panel.Children.Add(Line(impact.SyncedDailyRecordCount > 0
                ? $"Notion 将归档 {impact.SyncedDailyRecordCount} 条每日记录（该游戏未绑定总表）"
                : "Notion 上没有该游戏的记录"));
        }

        panel.Children.Add(Spacer());
        panel.Children.Add(Note("删除在本地不可恢复；Notion 侧是移入回收站，30 天内可恢复。"));
        panel.Children.Add(Note("若之后再次游玩，程序会把它识别成一款新游戏（需要重新绑定）。"));

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "确认删除这个游戏？",
            Content = new ScrollViewer { Content = panel, MaxHeight = 360 },
            PrimaryButtonText = "确认删除",
            PrimaryButtonStyle = BuildDestructiveStyle(),
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>确认删除某一天的每日记录。返回 true 表示用户确认。</summary>
    public static async Task<bool> ConfirmDailyDeleteAsync(
        XamlRoot? xamlRoot,
        string date,
        string gameName,
        int durationMinutes,
        bool hasNotionPage,
        bool notionConfigured)
    {
        if (xamlRoot == null) return false;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{date}　{gameName}　{durationMinutes} min",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(Line(hasNotionPage && notionConfigured
            ? "Notion 上对应的记录会一并移入回收站"
            : "Notion 上没有对应记录，只删本地"));

        panel.Children.Add(Spacer());
        panel.Children.Add(Note("本地记录不可恢复；Notion 侧 30 天内可恢复。"));
        panel.Children.Add(Note("当天剩余的游戏时长仍会重新累积并再次同步。"));

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "确认删除这条记录？",
            Content = panel,
            PrimaryButtonText = "确认删除",
            PrimaryButtonStyle = BuildDestructiveStyle(),
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static TextBlock Line(string text) => new()
    {
        Text = "• " + text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13
    };

    /// <summary>
    /// 危险操作按钮样式：红底白字。
    /// 对话框的 DefaultButton 故意设为 Close（回车走"取消"），
    /// 那就更要把"确认删除"从普通按钮里区分出来，否则两个按钮的视觉权重是反的。
    /// </summary>
    private static Style BuildDestructiveStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xDC, 0x26, 0x26))));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            new SolidColorBrush(Microsoft.UI.Colors.White)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xB9, 0x1C, 0x1C))));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(6)));
        return style;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Opacity = 0.7
    };

    private static Border Spacer() => new() { Height = 4, Background = null };
}
