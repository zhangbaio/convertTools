using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Ui.Views;

public sealed class PublishAccountSelector : UserControl
{
    private readonly CheckBox _enabled = new() { Content = "启用" };
    private readonly StackPanel _details = new() { Spacing = 8, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
    private readonly StackPanel _groups = new() { Spacing = 8 };
    private readonly Button _fetchButton = new() { Content = "获取已创建账号", MinWidth = 132 };
    private readonly Dictionary<string, CheckBox> _accountBoxes = new(StringComparer.Ordinal);
    private readonly List<(string Country, CheckBox Box, string[] Keys)> _countryBoxes = [];
    private IReadOnlyList<TikTokPublishAccountOption> _catalog = [];
    private bool _loading;

    public event EventHandler? FetchRequested;

    public PublishAccountSelector()
    {
        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(new TextBlock
        {
            Text = "关闭后发布时仍默认全选全部已创建账号。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            FontSize = 12,
        });
        root.Children.Add(_enabled);
        var fetchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        fetchRow.Children.Add(_fetchButton);
        fetchRow.Children.Add(_summary);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _details.Children.Add(fetchRow);
        _details.Children.Add(new ScrollViewer
        {
            Content = _groups,
            MaxHeight = 240,
        });
        root.Children.Add(_details);
        Content = root;

        _enabled.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            UpdateDetailsVisibility();
        };
        _fetchButton.Click += (_, _) => FetchRequested?.Invoke(this, EventArgs.Empty);
        UpdateDetailsVisibility();
    }

    public IReadOnlyList<string> ReadSelectedKeys() =>
        _accountBoxes
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key)
            .ToArray();

    public void Load(TikTokAccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _loading = true;
        try
        {
            _enabled.IsChecked = profile.TiktokCustomPublishAccountsEnabled;
            _catalog = TikTokPublishAccountCatalog.Parse(profile.TiktokPublishAccountCatalogJson);
            var selected = TikTokPublishAccountCatalog.Retain(
                    profile.TiktokSelectedPublishAccountKeys,
                    _catalog)
                .KeptKeys
                .ToHashSet(StringComparer.Ordinal);
            RebuildGroups(selected);
            UpdateDetailsVisibility();
        }
        finally
        {
            _loading = false;
        }
    }

    public void WriteTo(TikTokAccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.TiktokCustomPublishAccountsEnabled = _enabled.IsChecked == true;
        profile.TiktokSelectedPublishAccountKeys = ReadSelectedKeys().ToList();
    }

    public void SetFetchEnabled(bool enabled) => _fetchButton.IsEnabled = enabled;

    private void UpdateDetailsVisibility() =>
        _details.IsVisible = _enabled.IsChecked == true;

    private void RebuildGroups(IReadOnlySet<string> selected)
    {
        _groups.Children.Clear();
        _accountBoxes.Clear();
        _countryBoxes.Clear();
        if (_catalog.Count == 0)
        {
            _summary.Text = "请先获取已创建账号。";
            return;
        }

        foreach (var countryGroup in _catalog.GroupBy(account => account.Country, StringComparer.Ordinal))
        {
            var keys = countryGroup
                .Select(account => TikTokPublishAccountCatalog.BuildKey(account.Country, account.DisplayName))
                .ToArray();
            var countryBox = new CheckBox { Content = countryGroup.Key, FontWeight = FontWeight.SemiBold };
            var accountPanel = new StackPanel { Margin = new Thickness(22, 0, 0, 0), Spacing = 4 };
            foreach (var account in countryGroup)
            {
                var key = TikTokPublishAccountCatalog.BuildKey(account.Country, account.DisplayName);
                var caption = string.IsNullOrWhiteSpace(account.Handle)
                    ? account.DisplayName
                    : $"{account.DisplayName}    {account.Handle}";
                var accountBox = new CheckBox
                {
                    Content = caption,
                    IsChecked = selected.Contains(key),
                };
                accountBox.IsCheckedChanged += (_, _) =>
                {
                    if (_loading) return;
                    SyncCountryBox(countryBox, keys);
                    UpdateSummary();
                };
                _accountBoxes[key] = accountBox;
                accountPanel.Children.Add(accountBox);
            }

            countryBox.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                if (countryBox.IsChecked is not bool selectAll) return;
                _loading = true;
                try
                {
                    foreach (var key in keys)
                    {
                        if (_accountBoxes.TryGetValue(key, out var accountBox))
                            accountBox.IsChecked = selectAll;
                    }
                }
                finally
                {
                    _loading = false;
                }

                UpdateSummary();
            };
            SyncCountryBox(countryBox, keys);
            _countryBoxes.Add((countryGroup.Key, countryBox, keys));
            var group = new StackPanel { Spacing = 4 };
            group.Children.Add(countryBox);
            group.Children.Add(accountPanel);
            _groups.Children.Add(group);
        }

        UpdateSummary();
    }

    private void SyncCountryBox(CheckBox countryBox, IReadOnlyList<string> keys)
    {
        var selectedCount = keys.Count(key =>
            _accountBoxes.TryGetValue(key, out var box) && box.IsChecked == true);
        var previousLoading = _loading;
        _loading = true;
        try
        {
            countryBox.IsChecked = selectedCount == 0
                ? false
                : selectedCount == keys.Count
                    ? true
                    : null;
        }
        finally
        {
            _loading = previousLoading;
        }
    }

    private void UpdateSummary()
    {
        var selectedCount = ReadSelectedKeys().Count;
        _summary.Text = $"已获取 {_catalog.Count} 个账号，已选 {selectedCount} 个。";
    }
}
