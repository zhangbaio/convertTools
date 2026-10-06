using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Ui.Views;

public sealed class PublishAccountSelector : UserControl
{
    private static readonly IBrush BorderBrushColor = new SolidColorBrush(Color.Parse("#D0D5DD"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#86909C"));
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#E8F3FF"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#1677FF"));

    public static readonly StyledProperty<double> LabelColumnWidthProperty =
        AvaloniaProperty.Register<PublishAccountSelector, double>(nameof(LabelColumnWidth), 140);

    private readonly CheckBox _enabled = new() { Content = "启用" };
    private readonly TextBlock _dropdownLabel;
    private readonly StackPanel _fetchRow = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Margin = new(0, 8, 0, 0),
    };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush };
    private readonly Button _fetchButton = new() { Content = "获取已创建账号", MinWidth = 132 };
    private readonly Border _dropdown;
    private readonly StackPanel _dropdownRow;
    private readonly TextBlock _dropdownText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _countryList = new() { Spacing = 2 };
    private readonly StackPanel _accountList = new() { Spacing = 2 };
    private readonly TextBlock _selectAll;
    private readonly TextBlock _clearAll;
    private readonly Dictionary<string, CheckBox> _accountBoxes = new(StringComparer.Ordinal);
    private readonly List<CountryRow> _countryRows = [];
    private IReadOnlyList<TikTokPublishAccountOption> _catalog = [];
    private bool _loading;

    public event EventHandler? FetchRequested;

    public double LabelColumnWidth
    {
        get => GetValue(LabelColumnWidthProperty);
        set => SetValue(LabelColumnWidthProperty, value);
    }

    public PublishAccountSelector()
    {
        _selectAll = ActionLink("全选", () => SetAll(true));
        _clearAll = ActionLink("取消全选", () => SetAll(false));
        _dropdown = BuildDropdown();
        _dropdownRow = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        _dropdownRow.Children.Add(_dropdown);
        _dropdownRow.Children.Add(new TextBlock
        {
            Text = "同一国家只能选择一个账号。",
            Foreground = MutedBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        _dropdownLabel = new TextBlock
        {
            Text = "发布账号",
            Classes = { "formLabel" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };

        var enableLabel = new TextBlock
        {
            Text = "是否启用发布账号",
            Classes = { "formLabel" },
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var enableColumn = new StackPanel { Spacing = 4 };
        enableColumn.Children.Add(_enabled);
        enableColumn.Children.Add(new TextBlock
        {
            Text = "关闭后发布时仍默认全选全部已创建账号。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedBrush,
            FontSize = 12,
        });
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _fetchRow.Children.Add(_fetchButton);
        _fetchRow.Children.Add(_summary);
        _fetchRow.IsVisible = false;
        enableColumn.Children.Add(_fetchRow);

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{LabelColumnWidth},*"),
            ColumnSpacing = 10,
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Grid.SetColumn(enableColumn, 1);
        Grid.SetColumn(_dropdownRow, 1);
        Grid.SetRow(_dropdownLabel, 1);
        Grid.SetRow(_dropdownRow, 1);
        root.Children.Add(enableLabel);
        root.Children.Add(enableColumn);
        root.Children.Add(_dropdownLabel);
        root.Children.Add(_dropdownRow);
        Content = root;

        _enabled.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            UpdateDetailsVisibility();
        };
        _fetchButton.Click += (_, _) => FetchRequested?.Invoke(this, EventArgs.Empty);
        UpdateDetailsVisibility();
        UpdateSummary();
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelColumnWidthProperty && Content is Grid grid)
            grid.ColumnDefinitions = new ColumnDefinitions($"{change.GetNewValue<double>()},*");
    }

    private void UpdateDetailsVisibility()
    {
        var enabled = _enabled.IsChecked == true;
        _fetchRow.IsVisible = enabled;
        _dropdownLabel.IsVisible = enabled;
        _dropdownRow.IsVisible = enabled;
    }

    private Border BuildDropdown()
    {
        var popupBody = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("188,1,*"),
            MinWidth = 440,
            MinHeight = 168,
            MaxHeight = 260,
        };
        popupBody.Children.Add(new ScrollViewer
        {
            Content = _countryList,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var divider = new Border { Background = BorderBrushColor };
        Grid.SetColumn(divider, 1);
        popupBody.Children.Add(divider);
        var accountScroll = new ScrollViewer
        {
            Content = _accountList,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(accountScroll, 2);
        popupBody.Children.Add(accountScroll);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 16,
            Margin = new Thickness(12, 8, 12, 8),
            Children = { _selectAll, _clearAll },
        };
        var footer = new Border
        {
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = actions,
        };
        var popup = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        popup.Children.Add(footer);
        popup.Children.Add(popupBody);

        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Standard,
            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = BorderBrushColor,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = popup,
            },
        };

        var caption = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        caption.Children.Add(_dropdownText);
        var arrow = new TextBlock
        {
            Text = "▾",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MutedBrush,
        };
        Grid.SetColumn(arrow, 1);
        caption.Children.Add(arrow);

        var trigger = new Border
        {
            Background = Brushes.White,
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6),
            MinHeight = 34,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = caption,
        };
        FlyoutBase.SetAttachedFlyout(trigger, flyout);
        trigger.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(trigger).Properties.IsLeftButtonPressed)
                return;
            FlyoutBase.ShowAttachedFlyout(trigger);
            e.Handled = true;
        };
        return trigger;
    }

    private void RebuildGroups(IReadOnlySet<string> selected)
    {
        _countryList.Children.Clear();
        _accountList.Children.Clear();
        _accountBoxes.Clear();
        _countryRows.Clear();
        var canSelect = _catalog.Count > 0;
        _selectAll.IsHitTestVisible = canSelect;
        _clearAll.IsHitTestVisible = canSelect;
        _selectAll.Foreground = canSelect ? LinkBrush : MutedBrush;
        _clearAll.Foreground = canSelect ? LinkBrush : MutedBrush;
        if (!canSelect)
        {
            UpdateSummary();
            return;
        }

        foreach (var countryGroup in _catalog.GroupBy(account => account.Country, StringComparer.Ordinal))
        {
            var country = countryGroup.Key;
            var keys = countryGroup
                .Select(account => TikTokPublishAccountCatalog.BuildKey(account.Country, account.DisplayName))
                .ToArray();
            var countryBox = new CheckBox { Content = country, VerticalAlignment = VerticalAlignment.Center };
            var countryHasSelection = false;
            foreach (var account in countryGroup)
            {
                var key = TikTokPublishAccountCatalog.BuildKey(account.Country, account.DisplayName);
                var accountBox = new CheckBox
                {
                    Content = account.DisplayName,
                    IsChecked = selected.Contains(key) && !countryHasSelection,
                    Margin = new Thickness(0, 2),
                };
                if (accountBox.IsChecked == true)
                    countryHasSelection = true;
                accountBox.IsCheckedChanged += (_, _) =>
                {
                    if (_loading) return;
                    if (accountBox.IsChecked == true)
                        ClearSiblingAccounts(keys, key);
                    SyncCountryBox(countryBox, keys);
                    UpdateSummary();
                };
                _accountBoxes[key] = accountBox;
            }

            countryBox.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                if (countryBox.IsChecked is not bool selectCountry) return;
                _loading = true;
                try
                {
                    for (var index = 0; index < keys.Length; index++)
                    {
                        if (_accountBoxes.TryGetValue(keys[index], out var accountBox))
                            accountBox.IsChecked = selectCountry && index == 0;
                    }
                }
                finally
                {
                    _loading = false;
                }

                UpdateSummary();
            };

            var chevron = Chevron();
            Grid.SetColumn(chevron, 1);
            var rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            };
            rowGrid.Children.Add(countryBox);
            rowGrid.Children.Add(chevron);
            var row = new Border
            {
                Padding = new Thickness(8, 4),
                CornerRadius = new CornerRadius(4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = rowGrid,
            };
            row.PointerPressed += (_, _) => ShowCountry(country);
            SyncCountryBox(countryBox, keys);
            _countryRows.Add(new CountryRow(country, row, keys));
            _countryList.Children.Add(row);
        }

        ShowCountry(_countryRows[0].Country);
        UpdateSummary();
    }

    private void ShowCountry(string country)
    {
        _accountList.Children.Clear();
        foreach (var row in _countryRows)
            row.Border.Background = string.Equals(row.Country, country, StringComparison.Ordinal)
                ? ActiveBrush
                : Brushes.Transparent;

        var match = _countryRows.FirstOrDefault(row => string.Equals(row.Country, country, StringComparison.Ordinal));
        if (match == null) return;
        foreach (var key in match.Keys)
        {
            if (_accountBoxes.TryGetValue(key, out var accountBox))
                _accountList.Children.Add(accountBox);
        }
    }

    private void SetAll(bool selected)
    {
        if (_catalog.Count == 0) return;
        _loading = true;
        try
        {
            var chosenCountries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var account in _catalog)
            {
                var key = TikTokPublishAccountCatalog.BuildKey(account.Country, account.DisplayName);
                if (!_accountBoxes.TryGetValue(key, out var box))
                    continue;
                box.IsChecked = selected && chosenCountries.Add(account.Country);
            }

            foreach (var row in _countryRows)
                SyncCountryBox(FindCountryBox(row), row.Keys);
        }
        finally
        {
            _loading = false;
        }

        UpdateSummary();
    }

    private void ClearSiblingAccounts(IReadOnlyList<string> keys, string keepKey)
    {
        _loading = true;
        try
        {
            foreach (var key in keys)
            {
                if (key == keepKey)
                    continue;
                if (_accountBoxes.TryGetValue(key, out var box))
                    box.IsChecked = false;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private CheckBox FindCountryBox(CountryRow row)
    {
        var grid = (Grid)row.Border.Child!;
        return (CheckBox)grid.Children[0];
    }

    private void SyncCountryBox(CheckBox countryBox, IReadOnlyList<string> keys)
    {
        var selectedCount = keys.Count(key =>
            _accountBoxes.TryGetValue(key, out var box) && box.IsChecked == true);
        var previousLoading = _loading;
        _loading = true;
        try
        {
            countryBox.IsChecked = selectedCount > 0;
        }
        finally
        {
            _loading = previousLoading;
        }
    }

    private void UpdateSummary()
    {
        if (_catalog.Count == 0)
        {
            _summary.Text = "请先获取已创建账号。";
            _dropdownText.Text = "暂无发布账号";
            _dropdownText.Foreground = MutedBrush;
            return;
        }

        var selectedKeys = ReadSelectedKeys();
        _summary.Text = $"已获取 {_catalog.Count} 个账号，已选 {selectedKeys.Count} 个。";
        if (selectedKeys.Count == 0)
        {
            _dropdownText.Text = "请选择发布账号";
            _dropdownText.Foreground = MutedBrush;
            return;
        }

        var firstKey = selectedKeys[0];
        var first = _catalog.First(account =>
            TikTokPublishAccountCatalog.BuildKey(account.Country, account.DisplayName) == firstKey);
        var extra = selectedKeys.Count - 1;
        _dropdownText.Text = extra > 0
            ? $"{first.DisplayName} · {first.Country}    +{extra}"
            : $"{first.DisplayName} · {first.Country}";
        _dropdownText.Foreground = Brushes.Black;
    }

    private static TextBlock Chevron() => new()
    {
        Text = "›",
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = MutedBrush,
        FontSize = 16,
    };

    private static TextBlock ActionLink(string text, Action action)
    {
        var link = new TextBlock
        {
            Text = text,
            Foreground = LinkBrush,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.PointerPressed += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return link;
    }

    private sealed record CountryRow(string Country, Border Border, string[] Keys);
}
