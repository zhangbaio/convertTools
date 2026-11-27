using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Weixin.Publishing;
using Account = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinSeriesPresetView : UserControl
{
    private readonly Dictionary<string, Func<string>> _editors = [];
    private readonly Dictionary<string, Func<string>> _projectEditors = [];
    private WeixinSeriesPresetStore? _store;
    private WeixinSeriesFormService? _forms;
    private Account? _account;
    private WeixinSeriesPresetCollection _library = new();
    private WeixinSeriesPreset? _selected;
    private CancellationTokenSource? _previewCancellation;
    private bool _loading;
    private string? _loadedProject;

    public WeixinSeriesPresetView()
    {
        InitializeComponent();
        ProjectDirectoryBox.TextChanged += (_, _) => ResetPreview();
        Sections.SelectionChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Source, Sections)) UpdateActions();
        };
        DetachedFromVisualTree += (_, _) => _previewCancellation?.Cancel();
        UpdateActions();
    }

    internal void Bind(WeixinSeriesPresetStore store, WeixinSeriesFormService forms)
    {
        _store = store;
        _forms = forms;
    }

    internal void SetAccount(Account? account)
    {
        if (account is not null && account.Id == _account?.Id) return;
        _account = account;
        ResetPreview();
        ConfigPathBox.Text = "";
        ProjectDirectoryBox.Text = account?.WorkRootDirectory ?? "";
        PreviewModePicker.SelectedIndex = 0;
        AccountSummary.Text = account is null ? "请先在左侧选择视频号账号。" : account.Name;
        _library = account is null || _store is null ? new() : _store.Load(account.Id);
        RenderPresetList(_library.DefaultId);
        StatusText.Text = "";
    }

    internal void OpenProject(string directory, string config, WeixinSeriesUploadMode mode)
    {
        PreviewModePicker.SelectedIndex = mode switch
        {
            WeixinSeriesUploadMode.ApiUpload => 1,
            WeixinSeriesUploadMode.ApiSubmit => 2,
            _ => 0,
        };
        LoadProject(directory, config);
        Sections.SelectedItem = ProjectTab;
    }

    private void LoadProject(string directory, string config = "")
    {
        ProjectDirectoryBox.Text = directory;
        ResetPreview();
        var form = _account is not null && Directory.Exists(directory) ? _store?.LoadProject(_account.Id, directory) : null;
        ConfigPathBox.Text = string.IsNullOrWhiteSpace(config) ? form?.ConfigPath ?? "" : config;
        RenderPresetList(form?.PresetId ?? _library.DefaultId);
    }

    private void RenderPresetList(string selectedId, WeixinSeriesPreset? newPreset = null)
    {
        _loading = true;
        try
        {
            var choices = new List<ComboBoxItem> { new() { Content = "使用项目原配置", Tag = "" } };
            foreach (var preset in _library.Presets)
                choices.Add(new ComboBoxItem { Content = preset.Name + (preset.Id == _library.DefaultId ? " · 默认" : ""), Tag = preset });
            if (newPreset is not null)
                choices.Add(new ComboBoxItem { Content = "新方案", Tag = newPreset });
            PresetPicker.ItemsSource = choices;
            PresetPicker.SelectedItem = choices.FirstOrDefault(item => item.Tag is WeixinSeriesPreset preset && preset.Id == selectedId)
                ?? choices[0];
        }
        finally { _loading = false; }
        LoadPresetEditor();
    }

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) LoadPresetEditor();
    }

    private void LoadPresetEditor()
    {
        _selected = (PresetPicker.SelectedItem as ComboBoxItem)?.Tag as WeixinSeriesPreset;
        _editors.Clear();
        PresetFields.Children.Clear();
        NameBox.Text = _selected?.Name ?? "";
        if (_selected is { } preset)
        {
            foreach (var group in WeixinSeriesFields.All.GroupBy(field => field.Group))
            {
                var section = new StackPanel { Spacing = 8 };
                section.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold, FontSize = 15 });
                foreach (var field in group)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 12 };
                    row.Children.Add(new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                    var input = CreateInput(field, preset.Values.GetValueOrDefault(field.Key), "跟随项目", _editors);
                    Grid.SetColumn(input, 1);
                    row.Children.Add(input);
                    section.Children.Add(row);
                }
                PresetFields.Children.Add(new Border
                {
                    Padding = new Thickness(12), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                    BorderBrush = Brush.Parse("#D9E2EC"), Background = Brushes.White, Child = section,
                });
            }
        }
        ResetPreview();
        PreviewNotes.Text = "生成后可核对取值来源，并保存当前项目的覆盖值。";
        UpdateActions();
    }

    private void UpdateActions()
    {
        var hasAccount = _account is not null;
        var busy = _previewCancellation is not null;
        Editor.IsEnabled = hasAccount && !busy;
        NewButton.IsEnabled = hasAccount && !busy;
        NameBox.IsEnabled = _selected is not null;
        SaveButton.IsEnabled = hasAccount && _selected is not null && !busy;
        DefaultButton.IsEnabled = SaveButton.IsEnabled;
        CancelButton.IsVisible = busy;
        ApplyButton.IsEnabled = hasAccount && !busy && Sections.SelectedItem == ProjectTab && _projectEditors.Count > 0;
    }

    private void OnNewClick(object? sender, RoutedEventArgs e)
    {
        if (_account is not { } account) return;
        var defaults = new Dictionary<string, string>
        {
            [WeixinSeriesFields.Producer] = account.CostReportCompanyName,
            [WeixinSeriesFields.Ratio] = account.CostReportActorPayRatio,
            [WeixinSeriesFields.Seal] = account.CostReportSealPath,
            [WeixinSeriesFields.Signature] = account.CostReportSignPath,
            [WeixinSeriesFields.Representative] = account.CostReportLegalRepresentative,
        };
        var preset = new WeixinSeriesPreset
        {
            Values = defaults.Where(item => !string.IsNullOrWhiteSpace(item.Value)).ToDictionary(item => item.Key, item => item.Value),
        };
        RenderPresetList(preset.Id, preset);
        Sections.SelectedIndex = 0;
        NameBox.Focus();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => SavePreset(makeDefault: false);
    private void OnDefaultClick(object? sender, RoutedEventArgs e) => SavePreset(makeDefault: true);

    private void SavePreset(bool makeDefault)
    {
        if (_account is null || _store is null || _selected is null) return;
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "请输入方案名称。";
            return;
        }
        if (_library.Presets.Any(preset => preset.Id != _selected.Id && preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "已有同名方案，请换一个名称。";
            return;
        }
        var next = _selected with { Name = name, Values = ReadInputs(_editors) };
        var presets = _library.Presets.ToList();
        var index = presets.FindIndex(preset => preset.Id == next.Id);
        if (index >= 0) presets[index] = next;
        else presets.Add(next);
        var updated = _library with { Presets = presets, DefaultId = makeDefault ? next.Id : _library.DefaultId };
        SaveLibrary(updated, next.Id, makeDefault ? "已保存并设为账号默认方案。" : "方案已保存。");
    }

    private void OnClearDefaultClick(object? sender, RoutedEventArgs e) =>
        SaveLibrary(_library with { DefaultId = "" }, _selected?.Id ?? "", "已关闭账号默认方案。");

    private void SaveLibrary(WeixinSeriesPresetCollection library, string selectedId, string status)
    {
        if (_account is null || _store is null) return;
        try
        {
            _store.Save(_account.Id, library);
            _library = library;
            RenderPresetList(selectedId);
            StatusText.Text = status;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存方案失败：{ex.Message}";
        }
    }

    private async void OnChooseProjectClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var account = _account?.Id;
        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择剧集项目目录" });
        if (result.Count > 0 && account == _account?.Id)
            LoadProject(result[0].Path.LocalPath);
    }

    private async void OnChooseConfigClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var account = _account?.Id;
        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频号剧集配置",
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (result.Count > 0 && account == _account?.Id)
            ConfigPathBox.Text = result[0].Path.LocalPath;
    }

    private async void OnGenerateClick(object? sender, RoutedEventArgs e) => await GenerateAsync(save: false);
    private async void OnApplyClick(object? sender, RoutedEventArgs e) => await GenerateAsync(save: true);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => _previewCancellation?.Cancel();

    private async Task GenerateAsync(bool save)
    {
        if (_account is not { } account || _forms is null || _store is null || _previewCancellation is not null) return;
        var directory = ProjectDirectoryBox.Text?.Trim() ?? "";
        if (!Directory.Exists(directory))
        {
            StatusText.Text = "请选择存在的剧集项目目录。";
            return;
        }
        var preset = _selected;
        var presetId = preset?.Id ?? "";
        if (preset is not null && (!_library.Presets.Any(item => item.Id == preset.Id) ||
            NameBox.Text?.Trim() != preset.Name ||
            !ReadInputs(_editors).OrderBy(item => item.Key).SequenceEqual(preset.Values.OrderBy(item => item.Key))))
        {
            StatusText.Text = "请先保存方案，再生成项目表单。";
            return;
        }
        var path = ConfigPathBox.Text?.Trim() ?? "";
        var overrides = _loadedProject == directory
            ? ReadInputs(_projectEditors)
            : _store.LoadProject(account.Id, directory)?.Values ?? [];
        var draft = new WeixinSeriesProjectForm(presetId, overrides, path);
        var job = new PublishJob
        {
            AccountId = account.Id, AccountName = account.Name, ProjectDirectory = directory,
            ProjectName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), ConfigPath = path,
            PlatformOptionsJson = new WeixinPublishOptions
            {
                SeriesUploadMode = PreviewModePicker.SelectedIndex switch
                {
                    1 => WeixinSeriesUploadMode.ApiUpload,
                    2 => WeixinSeriesUploadMode.ApiSubmit,
                    _ => WeixinSeriesUploadMode.Browser,
                },
            }.ToJson(),
        };
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        UpdateActions();
        StatusText.Text = "正在生成并校验表单…";
        try
        {
            var preview = await _forms.PreviewAsync(job, preset, draft, cancellation.Token);
            if (cancellation.IsCancellationRequested || account.Id != _account?.Id) return;
            _loadedProject = directory;
            if (save && preview.Errors.Count == 0)
            {
                foreach (var field in preview.Fields.Where(field => field.Source is "剧集类型 Agent" or "发布信息 Agent"))
                    overrides[field.Key] = field.Value;
                _store.SaveProject(account.Id, directory, draft with { PresetSnapshot = preset });
            }
            RenderPreview(preview, overrides);
            StatusText.Text = preview.Errors.Count > 0 ? $"发现 {preview.Errors.Count} 项需要处理。"
                : save ? "已保存项目表单，单剧上架和任务队列将使用此配置。" : "表单校验通过。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (account.Id == _account?.Id) StatusText.Text = "已取消生成。";
        }
        catch (Exception ex)
        {
            if (account.Id == _account?.Id) StatusText.Text = $"生成表单失败：{ex.Message}";
        }
        finally
        {
            _previewCancellation = null;
            UpdateActions();
        }
    }

    private void RenderPreview(WeixinSeriesFormPreview preview, IReadOnlyDictionary<string, string> overrides)
    {
        PreviewFields.Children.Clear();
        _projectEditors.Clear();
        PreviewNotes.Text = preview.TrialSummary.Length > 0 ? preview.TrialSummary : "覆盖值留空时使用方案或项目值。";
        foreach (var error in preview.Errors)
            PreviewFields.Children.Add(new TextBlock { Text = error, Foreground = Brush.Parse("#9A3412"), TextWrapping = TextWrapping.Wrap });
        foreach (var field in preview.Fields)
        {
            var definition = WeixinSeriesFields.All.First(item => item.Key == field.Key);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,*"), ColumnSpacing = 12 };
            row.Children.Add(new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap });
            var current = new TextBlock
            {
                Text = field.Value.Length == 0 ? "未填写" : $"{field.Value}  ·  {field.Source}",
                TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#475569"),
            };
            Grid.SetColumn(current, 1);
            row.Children.Add(current);
            var proposed = field.Source is "剧集类型 Agent" or "发布信息 Agent" ? field.Value : null;
            var input = CreateInput(definition, overrides.GetValueOrDefault(field.Key) ?? proposed,
                "填写本项目覆盖值", _projectEditors);
            Grid.SetColumn(input, 2);
            row.Children.Add(input);
            PreviewFields.Children.Add(row);
        }
        var episodes = new ListBox
        {
            MaxHeight = 230,
            ItemsSource = preview.Episodes.Select(episode =>
            {
                var duration = episode.Seconds is { } seconds ? $"{seconds:0.##} 秒" : "时长未读取";
                return $"第 {episode.Number} 集 · {Path.GetFileName(episode.Path)} · {duration} · {(episode.Selected ? "待上传" : "未选")}";
            }).ToArray(),
        };
        PreviewFields.Children.Add(new TextBlock { Text = "分集视频清单", FontWeight = FontWeight.SemiBold });
        PreviewFields.Children.Add(episodes);
    }

    private Control CreateInput(WeixinSeriesField field, string? value, string placeholder,
        Dictionary<string, Func<string>> editors)
    {
        if (field.Choices is { } choices)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var items = new[] { placeholder }.Concat(choices).ToList();
            if (!string.IsNullOrWhiteSpace(value) && !items.Contains(value)) items.Add(value);
            combo.ItemsSource = items;
            combo.SelectedIndex = Math.Max(items.IndexOf(value ?? ""), 0);
            editors[field.Key] = () => combo.SelectedIndex > 0 ? (string)combo.SelectedItem! : "";
            return combo;
        }
        var text = new TextBox { Watermark = placeholder, Text = value,
            AcceptsReturn = field.Kind is WeixinSeriesFieldKind.Multiline or WeixinSeriesFieldKind.Attachments };
        editors[field.Key] = () => text.Text?.Trim() ?? "";
        if (field.Kind is not (WeixinSeriesFieldKind.Attachment or WeixinSeriesFieldKind.Attachments)) return text;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        var browse = new Button { Content = "选择文件", VerticalAlignment = VerticalAlignment.Top };
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var account = _account?.Id;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择" + field.Label, AllowMultiple = field.Kind == WeixinSeriesFieldKind.Attachments,
            });
            if (files.Count > 0 && account == _account?.Id)
                text.Text = string.Join(Environment.NewLine, files.Select(file => file.Path.LocalPath));
        };
        grid.Children.Add(text);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        return grid;
    }

    private void ResetPreview()
    {
        _previewCancellation?.Cancel();
        _loadedProject = null;
        _projectEditors.Clear();
        PreviewFields.Children.Clear();
        UpdateActions();
    }

    private static Dictionary<string, string> ReadInputs(Dictionary<string, Func<string>> editors) =>
        editors.Select(item => (item.Key, Value: item.Value())).Where(item => item.Value.Length > 0)
            .ToDictionary(item => item.Key, item => item.Value);
}
