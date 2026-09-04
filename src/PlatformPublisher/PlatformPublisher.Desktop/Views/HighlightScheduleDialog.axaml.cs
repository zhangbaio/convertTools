using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlatformPublisher.Desktop.Services;

namespace PlatformPublisher.Desktop.Views;

public partial class HighlightScheduleDialog : Window
{
    private readonly ObservableCollection<ScheduleRuleListItem> _items = [];
    private readonly List<WeixinHighlightScheduleRule> _rules = [];
    private string _accountId = string.Empty;
    private string _workspace = string.Empty;
    private bool _loading;
    private int _currentIndex = -1;

    public HighlightScheduleDialog() { InitializeComponent(); RuleList.ItemsSource = _items; }

    public HighlightScheduleDialog(IEnumerable<WeixinHighlightScheduleRule> rules, string accountId, string workspace)
        : this()
    {
        _accountId = accountId;
        _workspace = workspace;
        _rules.AddRange(rules);
        if (_rules.Count == 0) _rules.Add(WeixinHighlightScheduleRule.Create(accountId, workspace));
        RefreshList();
        RuleList.SelectedIndex = 0;
    }

    public IReadOnlyList<WeixinHighlightScheduleRule> Rules => _rules;
    public string? RunNowRuleId { get; private set; }

    private void OnRuleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        StoreCurrent();
        LoadRule(RuleList.SelectedIndex);
    }

    private void OnAddRuleClick(object? sender, RoutedEventArgs e)
    {
        StoreCurrent();
        _rules.Add(WeixinHighlightScheduleRule.Create(_accountId, _workspace));
        RefreshList();
        RuleList.SelectedIndex = _rules.Count - 1;
    }

    private void OnDeleteRuleClick(object? sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedIndex < 0 || RuleList.SelectedIndex >= _rules.Count) return;
        _rules.RemoveAt(RuleList.SelectedIndex);
        if (_rules.Count == 0) _rules.Add(WeixinHighlightScheduleRule.Create(_accountId, _workspace));
        RefreshList();
        RuleList.SelectedIndex = 0;
    }

    private void OnTriggerChanged(object? sender, SelectionChangedEventArgs e) => RefreshTriggerState();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!TryStore()) return;
        Close(true);
    }

    private void OnRunNowClick(object? sender, RoutedEventArgs e)
    {
        if (!TryStore()) return;
        RunNowRuleId = _rules[_currentIndex].Id;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private bool TryStore()
    {
        StoreCurrent();
        var current = _currentIndex >= 0 && _currentIndex < _rules.Count ? _rules[_currentIndex] : null;
        if (current is null || string.IsNullOrWhiteSpace(current.TitlesText))
        {
            ValidationText.Text = "请至少填写一个剧名。";
            TitlesInput.Focus();
            return false;
        }
        ValidationText.Text = string.Empty;
        return true;
    }

    private void StoreCurrent()
    {
        if (_loading || _currentIndex < 0 || _currentIndex >= _rules.Count) return;
        var original = _rules[_currentIndex];
        _rules[_currentIndex] = WeixinHighlightScheduleService.Normalize(original with
        {
            Name = NameInput.Text?.Trim() ?? string.Empty,
            Enabled = EnabledInput.IsChecked == true,
            AccountId = _accountId,
            WorkspaceDirectory = _workspace,
            TriggerMode = SelectedTag(TriggerInput, "fixed_time"),
            IntervalMinutes = (int)(IntervalInput.Value ?? 30),
            ScheduleMode = SelectedTag(ScheduleModeInput, "daily"),
            ScheduleTime = TimeInput.Text?.Trim() ?? "09:00",
            Weekdays = WeekdaysInput.Text?.Trim() ?? "0,1,2,3,4,5,6",
            CatchUpOnStartup = CatchUpInput.IsChecked == true,
            TitlesText = TitlesInput.Text?.Trim() ?? string.Empty,
            PublishCount = (int)(CountInput.Value ?? 10),
            VideoTypes = TypesInput.Text?.Trim() ?? string.Empty,
            Description = DescriptionInput.Text?.Trim() ?? string.Empty,
        });
        RefreshList(preserveIndex: _currentIndex);
    }

    private void LoadRule(int index)
    {
        if (index < 0 || index >= _rules.Count) return;
        _loading = true;
        try
        {
            _currentIndex = index;
            var rule = WeixinHighlightScheduleService.Normalize(_rules[index]);
            _rules[index] = rule;
            NameInput.Text = rule.Name;
            EnabledInput.IsChecked = rule.Enabled;
            SelectTag(TriggerInput, rule.TriggerMode);
            IntervalInput.Value = rule.IntervalMinutes;
            SelectTag(ScheduleModeInput, rule.ScheduleMode);
            TimeInput.Text = rule.ScheduleTime;
            WeekdaysInput.Text = rule.Weekdays;
            CatchUpInput.IsChecked = rule.CatchUpOnStartup;
            TitlesInput.Text = rule.TitlesText;
            CountInput.Value = rule.PublishCount;
            TypesInput.Text = rule.VideoTypes;
            DescriptionInput.Text = rule.Description;
            RefreshTriggerState();
        }
        finally { _loading = false; }
    }

    private void RefreshList(int preserveIndex = -1)
    {
        _loading = true;
        try
        {
            _items.Clear();
            foreach (var rule in _rules)
                _items.Add(new ScheduleRuleListItem(rule.Id, $"{(rule.Enabled ? "●" : "○")} {rule.Name}"));
            if (preserveIndex >= 0 && preserveIndex < _items.Count) RuleList.SelectedIndex = preserveIndex;
        }
        finally { _loading = false; }
    }

    private void RefreshTriggerState()
    {
        var interval = SelectedTag(TriggerInput, "fixed_time") == "interval";
        IntervalInput.IsEnabled = interval;
        ScheduleModeInput.IsEnabled = !interval;
        TimeInput.IsEnabled = !interval;
        WeekdaysInput.IsEnabled = !interval;
    }

    private static string SelectedTag(ComboBox comboBox, string fallback) =>
        comboBox.SelectedItem is ComboBoxItem { Tag: string value } ? value : fallback;

    private static void SelectTag(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }
}

public sealed record ScheduleRuleListItem(string Id, string Label)
{
    public override string ToString() => Label;
}
