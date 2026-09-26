using GameTimeTracker.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameTimeTracker.App.Dialogs;

public sealed partial class GameBindingDialog : ContentDialog
{
    private IReadOnlyList<NotionGameCatalogItem> _catalog = Array.Empty<NotionGameCatalogItem>();
    private IReadOnlyList<RemoteGameCandidate> _genericCatalog = Array.Empty<RemoteGameCandidate>();
    private string? _selectedManualPageId;
    private RemoteGameCandidate? _selectedCandidate;

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
        _genericCatalog = Array.Empty<RemoteGameCandidate>();
        _selectedManualPageId = null;
        _selectedCandidate = null;

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

    public void SetupForProvider(
        string providerName,
        string providerDisplayName,
        string gameName,
        string platformInfo,
        string exe,
        IReadOnlyList<MatchResult> candidates,
        IReadOnlyList<RemoteGameCandidate> catalog)
    {
        Title = $"🎮 关联游戏身份 · {providerDisplayName}";
        if (PromptTitleText != null) PromptTitleText.Text = $"请选择「{providerDisplayName}」中对应的笔记/条目：";
        if (SearchPromptText != null) SearchPromptText.Text = $"或者从 {providerDisplayName} 中搜索已有笔记或条目：";
        if (CatalogSuggestBox != null) CatalogSuggestBox.PlaceholderText = $"在 {providerDisplayName} 中搜索...";

        GameNameText.Text = gameName;
        PlatformInfoText.Text = platformInfo;
        ExecutableText.Text = exe;

        _genericCatalog = catalog ?? Array.Empty<RemoteGameCandidate>();
        _catalog = Array.Empty<NotionGameCatalogItem>();
        _selectedManualPageId = null;
        _selectedCandidate = null;

        CandidatesPanel.Children.Clear();
        RadioButton? defaultRadio = null;

        if (candidates.Count > 0)
        {
            for (int i = 0; i < Math.Min(4, candidates.Count); i++)
            {
                var m = candidates[i];
                var icon = m.Score >= 95 ? "⭐" : "●";
                var rb = new RadioButton
                {
                    Content = $"{icon} {m.Candidate.RemoteName} ({m.Evidence})",
                    Tag = m.Candidate,
                    IsChecked = i == 0 && m.Score >= 90
                };
                rb.Checked += (s, e) =>
                {
                    ClearManualSelection();
                    _selectedCandidate = m.Candidate;
                };
                if (i == 0 && m.Score >= 90)
                {
                    defaultRadio = rb;
                    _selectedCandidate = m.Candidate;
                }
                CandidatesPanel.Children.Add(rb);
            }
        }
        else
        {
            var emptyRb = new RadioButton
            {
                Content = "未检测到高置信度同名/别名笔记",
                IsEnabled = false
            };
            CandidatesPanel.Children.Add(emptyRb);
        }

        var newRb = new RadioButton
        {
            Content = $"➕ 将 \"{gameName}\" 新建为 {providerDisplayName} 游戏条目/笔记",
            Tag = "CREATE_NEW",
            IsChecked = defaultRadio == null
        };
        newRb.Checked += (s, e) =>
        {
            ClearManualSelection();
            _selectedCandidate = null;
        };
        CandidatesPanel.Children.Add(newRb);
    }

    private void ClearManualSelection()
    {
        _selectedManualPageId = null;
        _selectedCandidate = null;
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
            else if (_genericCatalog.Count > 0)
            {
                sender.ItemsSource = _genericCatalog
                    .Where(c => c.RemoteName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                c.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .Take(15)
                    .Select(c => c.RemoteName)
                    .Distinct()
                    .ToList();
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
            if (_genericCatalog.Count > 0)
            {
                var cand = _genericCatalog.FirstOrDefault(c => c.RemoteName.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
                if (cand != null)
                {
                    _selectedCandidate = cand;
                    _selectedManualPageId = cand.RemoteId;
                    SelectedManualGameText.Text = $"已选定: {cand.RemoteName}";
                    SelectedManualGameText.Visibility = Visibility.Visible;

                    foreach (var rb in CandidatesPanel.Children.OfType<RadioButton>())
                    {
                        rb.IsChecked = false;
                    }
                }
            }
            else
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
    }

    public RemoteGameCandidate? GetSelectedCandidate()
    {
        if (_selectedCandidate != null) return _selectedCandidate;
        var selectedRadio = CandidatesPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (selectedRadio?.Tag is RemoteGameCandidate cand) return cand;
        return null;
    }

    public string? GetSelectedPageId()
    {
        if (_selectedCandidate != null)
        {
            return _selectedCandidate.RemoteId;
        }
        if (!string.IsNullOrEmpty(_selectedManualPageId))
        {
            return _selectedManualPageId;
        }
        var selectedRadio = CandidatesPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (selectedRadio?.Tag is string strTag)
        {
            return strTag;
        }
        if (selectedRadio?.Tag is RemoteGameCandidate cand)
        {
            return cand.RemoteId;
        }
        return null;
    }
}
