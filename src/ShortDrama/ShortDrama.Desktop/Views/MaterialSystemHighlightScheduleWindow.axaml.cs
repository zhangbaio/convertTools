using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShortDrama.Desktop.Services;
using ShortDrama.Desktop.ViewModels;
using System.Collections.ObjectModel;

namespace ShortDrama.Desktop.Views;

public partial class MaterialSystemHighlightScheduleWindow : Window
{
    private readonly MaterialSystemHighlightScheduleService _service;
    private readonly string _currentWorkspace;
    private readonly IReadOnlyList<MaterialUploadAccountItemViewModel> _accounts;
    private readonly ObservableCollection<RuleListItem> _ruleItems = [];
    private List<MaterialSystemHighlightScheduleRule> _rules;
    private int _currentRuleIndex = -1;
    private bool _loading;

    public MaterialSystemHighlightScheduleWindow(
        MaterialSystemHighlightScheduleService service,
        string currentWorkspace,
        IEnumerable<MaterialUploadAccountItemViewModel> accounts)
    {
        _service = service;
        _currentWorkspace = currentWorkspace;
        _accounts = accounts.ToArray();
        _rules = service.LoadRules().ToList();
        InitializeComponent();

        RuleListBox.ItemsSource = _ruleItems;
        BindOptions(TriggerModeComboBox, [new("fixed_time", "固定时间"), new("interval", "按频率")]);
        BindOptions(ScheduleModeComboBox, [new("daily", "每天"), new("weekly", "每周")]);
        BindProfiles();
        HookEvents();
        RefreshRuleList();
        if (_rules.Count == 0)
        {
            AddRule();
        }
        else
        {
            RuleListBox.SelectedIndex = 0;
        }
    }

    private void HookEvents()
    {
        RuleListBox.SelectionChanged += (_, _) => OnRuleSelectionChanged();
        AddRuleButton.Click += (_, _) => AddRule();
        CopyRuleButton.Click += (_, _) => CopyRule();
        RemoveRuleButton.Click += (_, _) => RemoveRule();
        BrowseWorkspaceButton.Click += BrowseWorkspaceButton_Click;
        UseCurrentWorkspaceButton.Click += (_, _) => WorkspaceTextBox.Text = _currentWorkspace;
        SaveButton.Click += (_, _) => SaveAndClose(runNow: false);
        RunNowButton.Click += (_, _) => SaveAndClose(runNow: true);
        CancelButton.Click += (_, _) => Close(null);
        PublishByCountRadioButton.IsCheckedChanged += (_, _) => RefreshState();
        PublishByTypeRadioButton.IsCheckedChanged += (_, _) => RefreshState();
        TriggerModeComboBox.SelectionChanged += (_, _) => RefreshState();
        ScheduleModeComboBox.SelectionChanged += (_, _) => RefreshState();
        RegenerateAfterPublishCheckBox.IsCheckedChanged += (_, _) => RefreshState();
    }

    private async void BrowseWorkspaceButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择工作目录",
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(WorkspaceTextBox.Text)
                ? await StorageProvider.TryGetFolderFromPathAsync(WorkspaceTextBox.Text)
                : null
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            WorkspaceTextBox.Text = folder.Path.LocalPath;
        }
    }

    private void AddRule()
    {
        StoreCurrentRule();
        var activeProfile = _accounts.FirstOrDefault(item => item.IsActive)?.Id ?? string.Empty;
        _rules.Add(_service.NormalizeRule(MaterialSystemHighlightScheduleRule.CreateDefault(_currentWorkspace, activeProfile)));
        RefreshRuleList();
        RuleListBox.SelectedIndex = _rules.Count - 1;
    }

    private void CopyRule()
    {
        StoreCurrentRule();
        var index = RuleListBox.SelectedIndex;
        if (index < 0 || index >= _rules.Count)
        {
            return;
        }

        var copied = _rules[index] with
        {
            Id = string.Empty,
            Name = $"{_rules[index].Name} - 副本"
        };
        _rules.Add(_service.NormalizeRule(copied));
        RefreshRuleList();
        RuleListBox.SelectedIndex = _rules.Count - 1;
    }

    private void RemoveRule()
    {
        var index = RuleListBox.SelectedIndex;
        if (index < 0 || index >= _rules.Count)
        {
            return;
        }

        _rules.RemoveAt(index);
        RefreshRuleList();
        RuleListBox.SelectedIndex = Math.Min(index, _rules.Count - 1);
        if (_rules.Count == 0)
        {
            AddRule();
        }
    }

    private void OnRuleSelectionChanged()
    {
        if (_loading)
        {
            return;
        }

        StoreCurrentRule();
        LoadRule(RuleListBox.SelectedIndex);
    }

    private void StoreCurrentRule()
    {
        if (_loading || _currentRuleIndex < 0 || _currentRuleIndex >= _rules.Count)
        {
            return;
        }

        var publishCount = Math.Max(1, (int)(PublishCountUpDown.Value ?? 10));
        _rules[_currentRuleIndex] = _service.NormalizeRule(new MaterialSystemHighlightScheduleRule(
            Id: _rules[_currentRuleIndex].Id,
            Name: NameTextBox.Text?.Trim() ?? string.Empty,
            Enabled: EnabledCheckBox.IsChecked == true,
            ProfileId: ProfileComboBox.SelectedItem is OptionItem profile ? profile.Key : string.Empty,
            WorkspacePath: WorkspaceTextBox.Text?.Trim() ?? string.Empty,
            TriggerMode: TriggerModeComboBox.SelectedItem is OptionItem trigger ? trigger.Key : "fixed_time",
            IntervalMinutes: Math.Max(1, (int)(IntervalMinutesUpDown.Value ?? 30)),
            ScheduleMode: ScheduleModeComboBox.SelectedItem is OptionItem schedule ? schedule.Key : "daily",
            Time: TimeTextBox.Text?.Trim() ?? "09:00",
            Weekdays: SelectedWeekdaysText(),
            CatchUpOnStartup: CatchUpOnStartupCheckBox.IsChecked == true,
            OnlyWhenIdle: OnlyWhenIdleCheckBox.IsChecked == true,
            DefaultDescription: DefaultDescriptionTextBox.Text?.Trim() ?? string.Empty,
            PublishCount: publishCount,
            PublishTargetMode: PublishByTypeRadioButton.IsChecked == true ? "type" : "count",
            PublishVideoTypes: CheckedTypes(PublishMashupCheckBox, PublishCommentaryCheckBox, PublishSliceCheckBox),
            RegenerateAfterPublish: RegenerateAfterPublishCheckBox.IsChecked == true,
            RegenerateVideoTypes: CheckedTypes(RegenerateMashupCheckBox, RegenerateCommentaryCheckBox, RegenerateSliceCheckBox),
            Dramas: MaterialSystemHighlightScheduleService.ParseDramaLines(DramaTitlesTextBox.Text ?? string.Empty, publishCount)));
        RefreshRuleList(preserveSelection: true);
    }

    private void LoadRule(int index)
    {
        _loading = true;
        try
        {
            _currentRuleIndex = index;
            if (index < 0 || index >= _rules.Count)
            {
                return;
            }

            var rule = _service.NormalizeRule(_rules[index]);
            EnabledCheckBox.IsChecked = rule.Enabled;
            NameTextBox.Text = rule.Name;
            SelectOption(ProfileComboBox, rule.ProfileId);
            WorkspaceTextBox.Text = rule.WorkspacePath;
            SelectOption(TriggerModeComboBox, rule.TriggerMode);
            IntervalMinutesUpDown.Value = rule.IntervalMinutes;
            SelectOption(ScheduleModeComboBox, rule.ScheduleMode);
            TimeTextBox.Text = rule.Time;
            SetWeekdaysText(rule.Weekdays);
            CatchUpOnStartupCheckBox.IsChecked = rule.CatchUpOnStartup;
            OnlyWhenIdleCheckBox.IsChecked = rule.OnlyWhenIdle;
            DefaultDescriptionTextBox.Text = rule.DefaultDescription;
            PublishCountUpDown.Value = rule.PublishCount;
            PublishByTypeRadioButton.IsChecked = rule.PublishTargetMode == "type";
            PublishByCountRadioButton.IsChecked = rule.PublishTargetMode != "type";
            SetTypeChecks(rule.PublishVideoTypes, PublishMashupCheckBox, PublishCommentaryCheckBox, PublishSliceCheckBox);
            RegenerateAfterPublishCheckBox.IsChecked = rule.RegenerateAfterPublish;
            SetTypeChecks(rule.RegenerateVideoTypes, RegenerateMashupCheckBox, RegenerateCommentaryCheckBox, RegenerateSliceCheckBox);
            DramaTitlesTextBox.Text = string.Join(Environment.NewLine, rule.Dramas.Where(item => item.Enabled).Select(item => item.Title));
            RefreshState();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveAndClose(bool runNow)
    {
        StoreCurrentRule();
        var saved = _service.SaveRules(_rules);
        var selectedId = RuleListBox.SelectedItem is RuleListItem item ? item.Id : saved.FirstOrDefault()?.Id ?? string.Empty;
        Close(new MaterialSystemHighlightScheduleDialogResult(saved, runNow ? selectedId : string.Empty));
    }

    private void RefreshRuleList(bool preserveSelection = false)
    {
        var selectedId = preserveSelection && RuleListBox.SelectedItem is RuleListItem selected ? selected.Id : string.Empty;
        _ruleItems.Clear();
        foreach (var rule in _rules)
        {
            _ruleItems.Add(new RuleListItem(rule.Id, $"{rule.Name} [{(rule.Enabled ? "启用" : "关闭")}]"));
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            RuleListBox.SelectedIndex = _ruleItems.Select((item, index) => (item, index)).FirstOrDefault(pair => pair.item.Id == selectedId).index;
        }
    }

    private void RefreshState()
    {
        var publishByType = PublishByTypeRadioButton.IsChecked == true;
        PublishCountUpDown.IsEnabled = !publishByType;
        PublishMashupCheckBox.IsEnabled = publishByType;
        PublishCommentaryCheckBox.IsEnabled = publishByType;
        PublishSliceCheckBox.IsEnabled = publishByType;

        var interval = TriggerModeComboBox.SelectedItem is OptionItem trigger && trigger.Key == "interval";
        IntervalMinutesUpDown.IsEnabled = interval;
        ScheduleModeComboBox.IsEnabled = !interval;
        TimeTextBox.IsEnabled = !interval;
        var weekly = ScheduleModeComboBox.SelectedItem is OptionItem schedule && schedule.Key == "weekly";
        foreach (var checkBox in WeekdayCheckBoxes())
        {
            checkBox.IsEnabled = !interval && weekly;
        }

        var regenerate = RegenerateAfterPublishCheckBox.IsChecked == true;
        RegenerateMashupCheckBox.IsEnabled = regenerate;
        RegenerateCommentaryCheckBox.IsEnabled = regenerate;
        RegenerateSliceCheckBox.IsEnabled = regenerate;
    }

    private void BindProfiles()
    {
        var items = new List<OptionItem> { new(string.Empty, "当前生效账号") };
        items.AddRange(_accounts.Select(account => new OptionItem(account.Id, account.DisplayName)));
        ProfileComboBox.ItemsSource = items;
        ProfileComboBox.SelectedIndex = 0;
    }

    private static void BindOptions(ComboBox comboBox, IReadOnlyList<OptionItem> items)
    {
        comboBox.ItemsSource = items;
        comboBox.SelectedIndex = 0;
    }

    private static void SelectOption(ComboBox comboBox, string key)
    {
        var items = (comboBox.ItemsSource as IEnumerable<OptionItem>)?.ToArray() ?? [];
        var index = Array.FindIndex(items, item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedIndex = index < 0 ? 0 : index;
    }

    private string SelectedWeekdaysText()
    {
        var values = WeekdayCheckBoxes()
            .Select((checkBox, index) => (checkBox, day: index + 1))
            .Where(pair => pair.checkBox.IsChecked == true)
            .Select(pair => pair.day.ToString())
            .ToArray();
        return string.Join(",", values.Length == 0 ? ["1", "2", "3", "4", "5", "6", "7"] : values);
    }

    private void SetWeekdaysText(string text)
    {
        var selected = (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        foreach (var (checkBox, index) in WeekdayCheckBoxes().Select((checkBox, index) => (checkBox, index)))
        {
            checkBox.IsChecked = selected.Count == 0 || selected.Contains((index + 1).ToString());
        }
    }

    private static IReadOnlyList<string> CheckedTypes(params CheckBox[] checkBoxes)
    {
        var selected = checkBoxes
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => checkBox.Content?.ToString() ?? string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        return selected.Length == 0 ? MaterialSystemHighlightBatchPublishService.VideoTypeOptions : selected;
    }

    private static void SetTypeChecks(IReadOnlyList<string> types, params CheckBox[] checkBoxes)
    {
        var selected = types.ToHashSet(StringComparer.Ordinal);
        foreach (var checkBox in checkBoxes)
        {
            checkBox.IsChecked = selected.Count == 0 || selected.Contains(checkBox.Content?.ToString() ?? string.Empty);
        }
    }

    private CheckBox[] WeekdayCheckBoxes() =>
    [
        Weekday1CheckBox,
        Weekday2CheckBox,
        Weekday3CheckBox,
        Weekday4CheckBox,
        Weekday5CheckBox,
        Weekday6CheckBox,
        Weekday7CheckBox
    ];

    private sealed record OptionItem(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record RuleListItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}

public sealed record MaterialSystemHighlightScheduleDialogResult(
    IReadOnlyList<MaterialSystemHighlightScheduleRule> Rules,
    string RunNowRuleId);
