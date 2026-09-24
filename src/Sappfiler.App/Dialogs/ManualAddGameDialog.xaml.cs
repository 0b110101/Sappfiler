using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GameTimeTracker.App.Dialogs;

public sealed partial class ManualAddGameDialog : ContentDialog
{
    private readonly nint _windowHandle;

    public string GameName => GameNameBox.Text.Trim();
    public string GamePath => PathBox.Text.Trim();

    public ManualAddGameDialog(nint windowHandle = 0)
    {
        InitializeComponent();
        _windowHandle = windowHandle;

        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(GameName))
        {
            ErrorText.Text = "请输入游戏名称";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(GamePath))
        {
            ErrorText.Text = "请输入或选择游戏路径";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        if (!File.Exists(GamePath) && !Directory.Exists(GamePath))
        {
            ErrorText.Text = "指定的路径或可执行文件不存在，请检查后重试";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
    }

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".exe");

            if (_windowHandle != 0)
            {
                InitializeWithWindow.Initialize(picker, _windowHandle);
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                PathBox.Text = file.Path;
                if (string.IsNullOrWhiteSpace(GameNameBox.Text))
                {
                    GameNameBox.Text = Path.GetFileNameWithoutExtension(file.Path);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"打开选择器失败: {ex.Message}，可直接手动粘贴路径";
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
