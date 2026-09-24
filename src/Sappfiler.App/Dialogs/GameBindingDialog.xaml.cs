using GameTimeTracker.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameTimeTracker.App.Dialogs;

public sealed partial class GameBindingDialog : ContentDialog
{
    private IReadOnlyList<NotionGameCatalogItem> _catalog = Array.Empty<NotionGameCatalogItem>();
    private string? _selectedManualPageId;

    public GameBindingDialog()
    {
        InitializeComponent();
    }

    public void Setup(string gameName, string platformInfo, string exe, IReadOnlyList<GameCandidate> candidates, IReadOnlyList<NotionGameCatalogItem>? catalog = null)
    {
        GameNameText.Text = gameName;
        PlatformInfoText.Text = platformInfo;
        ExecutableText.Text = exe;
        _catalog = catalog ?? Array.Empty<NotionGameCatalogItem>();
        _selectedManualPageId = null;

        CandidatesPanel.Children.Clear();
        RadioButton? defaultRadio = null;

        if (candidates.Count > 0)
        {
            for (int i = 0; i < Math.Min(3, candidates.Count); i++)
            {
                var c = candidates[i];
                var rb = new RadioButton
                {
                    Content = $"{c.Title} ({Math.Round(c.Score)}% 匹配)",
                    Tag = c.PageId,
                    IsChecked = i == 0
                };
                rb.Checked += (s, e) => ClearManualSelection();
                if (i == 0) defaultRadio = rb;
                CandidatesPanel.Children.Add(rb);
            }
        }
        else
        {
            var emptyRb = new RadioButton
            {
                Content = "暂无高置信度匹配",
                IsEnabled = false
            };
            CandidatesPanel.Children.Add(emptyRb);
        }

        var newRb = new RadioButton
        {
            Content = $"➕ 将 \"{gameName}\" 新建为 Notion 游戏总表记录",
            Tag = "CREATE_NEW",
            IsChecked = defaultRadio == null
        };
        newRb.Checked += (s, e) => ClearManualSelection();
        CandidatesPanel.Children.Add(newRb);
    }

    private void ClearManualSelection()
    {
        _selectedManualPageId = null;
        if (SelectedManualGameText != null)
        {
            SelectedManualGameText.Visibility = Visibility.Collapsed;
            SelectedManualGameText.Text = string.Empty;
        }
    }

    private void OnCatalogSuggestBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                sender.ItemsSource = null;
            }
            else
            {
                sender.ItemsSource = _catalog
                    .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(15)
                    .Select(c => c.Name)
                    .ToList();
            }
        }
    }

    private void OnCatalogSuggestBoxSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string selectedName)
        {
            var item = _catalog.FirstOrDefault(c => c.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                _selectedManualPageId = item.PageId;
                SelectedManualGameText.Text = $"已选定: {item.Name}";
                SelectedManualGameText.Visibility = Visibility.Visible;

                // Uncheck radio buttons
                foreach (var rb in CandidatesPanel.Children.OfType<RadioButton>())
                {
                    rb.IsChecked = false;
                }
            }
        }
    }

    public string? GetSelectedPageId()
    {
        if (!string.IsNullOrEmpty(_selectedManualPageId))
        {
            return _selectedManualPageId;
        }
        var selectedRadio = CandidatesPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        return selectedRadio?.Tag as string;
    }
}
