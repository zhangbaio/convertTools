using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Views;

public sealed class TikTokMultiAccountQueueDialog : Window
{
    private readonly AccountOption[] _options;
    private readonly int _dramaCount;
    private readonly ListBox _list;
    private readonly TextBox _search;
    private readonly CheckBox _onlySelected;
    private readonly TextBlock _matchCount;
    private readonly TextBlock _summary;
    private readonly Button _selectMatches;
    private readonly Button _clear;
    private readonly Button _continue;
    private AccountOption[] _matches = [];
    private bool _changingSelection;

    internal TikTokMultiAccountQueueDialog(IReadOnlyList<TikTokQueueImportTarget> targets,
        TikTokQueueImportTarget? currentTarget, int dramaCount)
    {
        var platforms = targets.Select(target => target.PlatformLabel).Distinct().ToArray();
        Title = platforms.Length == 1 && platforms[0].Length > 0 ? $"加入{platforms[0]}队列" : "选择入队账号";
        Width = 660;
        Height = 550;
        MinWidth = 450;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _dramaCount = dramaCount;
        _options = targets.Select(target => new AccountOption(target)
        {
            IsChecked = target.WorkspaceIssue.Length == 0 && currentTarget is not null
                && target.AccountProfileId == currentTarget.AccountProfileId && target.PlatformKey == currentTarget.PlatformKey,
        }).ToArray();
        _search = new TextBox { Name = "AccountSearch", Watermark = "搜索昵称 / 账号 ID / 邮箱" };
        _onlySelected = new CheckBox { Name = "OnlySelectedAccounts", Content = "仅看已选" };
        _matchCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Classes = { "pageSummary" } };
        _selectMatches = new Button { Name = "SelectMatchingAccounts", Content = "全选筛选结果" };
        _clear = new Button { Name = "ClearSelectedAccounts", Content = "清空已选" };
        _continue = new Button { Name = "ContinueQueueImport", Content = "核对并继续", IsDefault = true, Classes = { "primaryAction" } };
        var cancel = new Button { Content = "取消", IsCancel = true };
        _summary = new TextBlock { Name = "AccountSelectionSummary", Classes = { "pageSummary" }, TextWrapping = TextWrapping.Wrap };
        _list = new ListBox
        {
            Name = "QueueAccountList",
            ItemTemplate = new FuncDataTemplate<AccountOption>((_, _) => BuildRow(), supportsRecycling: true),
        };
        foreach (var option in _options)
            option.PropertyChanged += (_, _) =>
            {
                if (_changingSelection) return;
                if (_onlySelected.IsChecked == true)
                    Filter();
                else
                    UpdateSummary();
            };
        _search.TextChanged += (_, _) => Filter();
        _onlySelected.IsCheckedChanged += (_, _) => Filter();
        _selectMatches.Click += (_, _) => UpdateSelection(_matches, true);
        _clear.Click += (_, _) => UpdateSelection(_options, false);
        _continue.Click += (_, _) => Close(_options.Where(option => option.IsChecked).Select(option => option.Target).ToArray());
        cancel.Click += (_, _) => Close(null);

        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        filters.Children.Add(_search);
        Grid.SetColumn(_onlySelected, 1);
        filters.Children.Add(_onlySelected);
        var tools = new WrapPanel();
        foreach (var control in new Control[] { _selectMatches, _clear, _matchCount })
        {
            control.Margin = new Thickness(0, 0, 8, 0);
            tools.Children.Add(control);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_continue);
        actions.Children.Add(cancel);
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            RowSpacing = 10,
            Margin = new Thickness(16),
        };
        root.Children.Add(filters);
        Grid.SetRow(tools, 1);
        root.Children.Add(tools);
        Grid.SetRow(_list, 2);
        root.Children.Add(_list);
        Grid.SetRow(_summary, 3);
        root.Children.Add(_summary);
        Grid.SetRow(actions, 4);
        root.Children.Add(actions);
        Content = root;
        Opened += (_, _) => _search.Focus();
        Filter();
    }

    private void Filter()
    {
        var query = _search.Text?.Trim() ?? "";
        _matches = _options.Where(option => (_onlySelected.IsChecked != true || option.IsChecked) &&
            (query.Length == 0 || option.Target.AccountProfileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Target.AccountProfileId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Target.LoginEmail.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        _list.ItemsSource = _matches;
        _matchCount.Text = _matches.Length > 0 ? $"显示 {_matches.Length} / {_options.Length} 个账号" : "没有匹配的账号";
        UpdateSummary();
    }

    private void UpdateSelection(IEnumerable<AccountOption> options, bool isChecked)
    {
        _changingSelection = true;
        foreach (var option in options)
            option.IsChecked = isChecked && option.IsAvailable;
        _changingSelection = false;
        Filter();
    }

    private void UpdateSummary()
    {
        var chosen = _options.Where(option => option.IsChecked).ToArray();
        _clear.IsEnabled = chosen.Length > 0;
        _selectMatches.IsEnabled = _matches.Any(option => option.IsAvailable && !option.IsChecked);
        var conflicts = chosen.GroupBy(option => DramaDownloadViewModel.QueueWorkspaceKey(option.Target.WorkspaceRoot), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).SelectMany(group => group).ToArray();
        _continue.IsEnabled = chosen.Length > 0 && conflicts.Length == 0;
        _summary.Text = $"已选 {chosen.Length} 个账号 · {_dramaCount} 部剧 · 预计 {_dramaCount * (long)chosen.Length} 条任务";
        if (conflicts.Length > 0)
            _summary.Text += $"\n{conflicts.Length} 个所选账号的工作目录有冲突（{string.Join("、", conflicts.Take(2).Select(option => option.Target.Label))}等）。请调整勾选。";
    }

    private static Control BuildRow()
    {
        var check = new CheckBox { MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AccountOption.IsChecked)) { Mode = BindingMode.TwoWay });
        check.Bind(IsEnabledProperty, new Binding(nameof(AccountOption.IsAvailable)));
        check.Bind(AutomationProperties.NameProperty, new Binding(nameof(AccountOption.Label)));

        var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(AccountOption.Label)));
        var details = new TextBlock { FontSize = 12, Classes = { "pageSummary" }, TextTrimming = TextTrimming.CharacterEllipsis };
        details.Bind(TextBlock.TextProperty, new Binding(nameof(AccountOption.Details)));
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(name);
        labels.Children.Add(details);
        check.Content = labels;
        check.Bind(ToolTip.TipProperty, new Binding(nameof(AccountOption.Tooltip)));
        return check;
    }

    private sealed class AccountOption(TikTokQueueImportTarget target) : ObservableObject
    {
        private bool _isChecked;
        public TikTokQueueImportTarget Target { get; } = target;
        public string Label => Target.Label;
        public bool IsAvailable => Target.WorkspaceIssue.Length == 0;
        public string Details
        {
            get
            {
                var id = Target.AccountProfileId.Length > 8 ? $"…{Target.AccountProfileId[^8..]}" : Target.AccountProfileId;
                var identity = string.IsNullOrWhiteSpace(Target.LoginEmail) ? $"ID {id}" : $"{Target.LoginEmail} · ID {id}";
                return IsAvailable ? identity : $"{identity} · {Target.WorkspaceIssue}";
            }
        }
        public string Tooltip => $"{Label}\nID {Target.AccountProfileId}\n{Target.WorkspaceRoot}";
        public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
    }
}
