using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using ChannelsPublisher.Core.Services;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Weixin.Publishing;
using Account = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinSeriesPresetView : UserControl
{
    private readonly Dictionary<string, Func<string>> _editors = [];
    private readonly Dictionary<string, Func<string>> _projectEditors = [];
    private readonly List<UniformGrid> _fieldRows = [];
    private WeixinSeriesPresetStore? _store;
    private WeixinSeriesFormService? _forms;
    private Account? _account;
    private CancellationTokenSource? _previewCancellation;
    private string? _loadedProject;
    private string _workDirectory = "";

    public WeixinSeriesPresetView()
    {
        InitializeComponent();
        ProjectDirectoryBox.TextChanged += (_, _) => ResetPreview();
        AccountFields.SizeChanged += (_, _) => UpdateFieldColumns();
        DetachedFromVisualTree += (_, _) => ShowAccountForm();
        UpdateActions();
    }

    internal void Bind(WeixinSeriesPresetStore store, WeixinSeriesFormService forms)
    {
        _store = store;
        _forms = forms;
    }

    internal void SetAccount(Account? account, string workDirectory)
    {
        var changed = account?.Id != _account?.Id;
        _account = account;
        _workDirectory = workDirectory;
        AccountSummary.Text = account is null
            ? "请先在左侧选择视频号账号。"
            : $"当前账号 · {WeixinWorkspaceAccounts.DisplayName(account)}";
        if (!changed) return;

        ShowAccountForm();
        _editors.Clear();
        _fieldRows.Clear();
        AccountFields.Children.Clear();
        if (account is not null && _store is not null)
        {
            try
            {
                RenderAccountEditor(_store.LoadAccountForm(account.Id));
            }
            catch (Exception ex)
            {
                StatusText.Text = $"读取发布配置失败：{ex.Message}";
            }
        }
        UpdateActions();
    }

    internal void OpenProject(string directory, string config, WeixinSeriesUploadMode mode)
    {
        PreviewModePicker.SelectedIndex = mode switch
        {
            WeixinSeriesUploadMode.ApiUpload => 1,
            WeixinSeriesUploadMode.ApiSubmit => 2,
            _ => 0,
        };
        AccountEditor.IsVisible = false;
        ProjectEditor.IsVisible = true;
        LoadProject(string.IsNullOrWhiteSpace(directory) ? _workDirectory : directory, config);
        StatusText.Text = "";
        UpdateActions();
    }

    private void LoadProject(string directory, string config = "")
    {
        ProjectDirectoryBox.Text = directory;
        ResetPreview();
        var form = _account is not null && Directory.Exists(directory) ? _store?.LoadProject(_account.Id, directory) : null;
        ConfigPathBox.Text = string.IsNullOrWhiteSpace(config) ? form?.ConfigPath ?? "" : config;
        PreviewNotes.Text = "生成后可检查字段来源，并保存当前项目的覆盖值。";
    }

    private void ShowAccountForm()
    {
        ProjectEditor.IsVisible = false;
        AccountEditor.IsVisible = true;
        ResetPreview();
        StatusText.Text = "";
    }

    private void OnReturnToAccountClick(object? sender, RoutedEventArgs e) => ShowAccountForm();

    private void RenderAccountEditor(IReadOnlyDictionary<string, string> values)
    {
        foreach (var group in WeixinSeriesFields.All.GroupBy(field => field.Group))
        {
            if (AccountFields.Children.Count > 0)
                AccountFields.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 2, 0, 0), Background = Brush.Parse("#E4E9F0") });

            var section = new StackPanel { Spacing = 10 };
            section.Children.Add(new TextBlock { Text = group.Key, Classes = { "configurationHeading" } });
            UniformGrid? pair = null;
            foreach (var field in group)
            {
                var editor = new StackPanel { Spacing = 5 };
                editor.Children.Add(new TextBlock { Text = field.Label, Classes = { "configurationLabel" } });
                editor.Children.Add(CreateInput(field, values.GetValueOrDefault(field.Key), "跟随项目", _editors));
                if (field.Kind is WeixinSeriesFieldKind.Multiline or WeixinSeriesFieldKind.Attachment or WeixinSeriesFieldKind.Attachments)
                {
                    section.Children.Add(editor);
                    pair = null;
                    continue;
                }

                if (pair is null || pair.Children.Count == 2)
                {
                    pair = new UniformGrid { Columns = 2 };
                    _fieldRows.Add(pair);
                    section.Children.Add(pair);
                }
                editor.Classes.Add("configurationField");
                pair.Children.Add(editor);
            }
            AccountFields.Children.Add(section);
        }
        UpdateFieldColumns();
    }

    private void UpdateFieldColumns()
    {
        var columns = AccountFields.Bounds.Width >= 620 ? 2 : 1;
        foreach (var row in _fieldRows)
            row.Columns = columns;
    }

    private void UpdateActions()
    {
        var hasAccount = _account is not null;
        var busy = _previewCancellation is not null;
        var projectMode = ProjectEditor.IsVisible;
        Editor.IsEnabled = hasAccount && !busy;
        SaveButton.IsVisible = !projectMode;
        SaveButton.IsEnabled = hasAccount && _editors.Count > 0 && !busy;
        CancelButton.IsVisible = busy && projectMode;
        ApplyButton.IsVisible = projectMode;
        ApplyButton.IsEnabled = hasAccount && !busy && projectMode && _projectEditors.Count > 0;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_account is null || _store is null || _editors.Count == 0) return;
        try
        {
            _store.SaveAccountForm(_account.Id, ReadInputs(_editors));
            StatusText.Text = "已保存当前账号的发布配置。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存发布配置失败：{ex.Message}";
        }
    }

    private async void OnChooseProjectClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var account = _account?.Id;
        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择剧集项目目录" });
        if (result.Count > 0 && account == _account?.Id && ProjectEditor.IsVisible)
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
        if (result.Count > 0 && account == _account?.Id && ProjectEditor.IsVisible)
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
        var path = ConfigPathBox.Text?.Trim() ?? "";
        var overrides = _loadedProject == directory
            ? ReadInputs(_projectEditors)
            : _store.LoadProject(account.Id, directory)?.Values ?? [];
        var draft = new WeixinSeriesProjectForm("", overrides, path);
        var job = new PublishJob
        {
            AccountId = account.Id, AccountName = WeixinWorkspaceAccounts.DisplayName(account), ProjectDirectory = directory,
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
            var preview = await _forms.PreviewAsync(job, draft, cancellation.Token);
            if (cancellation.IsCancellationRequested || account.Id != _account?.Id) return;
            _loadedProject = directory;
            if (save && preview.Errors.Count == 0)
            {
                foreach (var field in preview.Fields.Where(field => field.Source is "剧集类型 Agent" or "发布信息 Agent"))
                    overrides[field.Key] = field.Value;
                _store.SaveProject(account.Id, directory, draft);
            }
            RenderPreview(preview, overrides);
            StatusText.Text = preview.Errors.Count > 0 ? $"发现 {preview.Errors.Count} 项需要处理。"
                : save ? "已保存项目表单，单剧上架和任务队列将使用此配置。" : "表单校验通过。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (account.Id == _account?.Id && ProjectEditor.IsVisible) StatusText.Text = "已取消生成。";
        }
        catch (Exception ex)
        {
            if (account.Id == _account?.Id && ProjectEditor.IsVisible) StatusText.Text = $"生成表单失败：{ex.Message}";
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
        PreviewNotes.Text = preview.TrialSummary.Length > 0 ? preview.TrialSummary : "覆盖值留空时使用账号配置或项目原值。";
        foreach (var error in preview.Errors)
            PreviewFields.Children.Add(new TextBlock { Text = error, Foreground = Brush.Parse("#9A3412"), TextWrapping = TextWrapping.Wrap });
        foreach (var field in preview.Fields)
        {
            var definition = WeixinSeriesFields.All.First(item => item.Key == field.Key);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,*"), ColumnSpacing = 12 };
            row.Children.Add(new TextBlock { Text = field.Label, Classes = { "configurationLabel" } });
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
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "configurationInput" } };
            AutomationProperties.SetName(combo, field.Label);
            var items = new[] { placeholder }.Concat(choices).ToList();
            if (!string.IsNullOrWhiteSpace(value) && !items.Contains(value)) items.Add(value);
            combo.ItemsSource = items;
            combo.SelectedIndex = Math.Max(items.IndexOf(value ?? ""), 0);
            editors[field.Key] = () => combo.SelectedIndex > 0 ? (string)combo.SelectedItem! : "";
            return combo;
        }
        var text = new TextBox { Watermark = placeholder, Text = value, Classes = { "configurationInput" },
            AcceptsReturn = field.Kind is WeixinSeriesFieldKind.Multiline or WeixinSeriesFieldKind.Attachments };
        AutomationProperties.SetName(text, field.Label);
        if (text.AcceptsReturn)
        {
            text.TextWrapping = TextWrapping.Wrap;
            text.MinHeight = field.Kind == WeixinSeriesFieldKind.Multiline ? 74 : 48;
        }
        editors[field.Key] = () => text.Text?.Trim() ?? "";
        if (field.Kind is not (WeixinSeriesFieldKind.Attachment or WeixinSeriesFieldKind.Attachments)) return text;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        var browse = new Button { Content = "浏览…", VerticalAlignment = VerticalAlignment.Top };
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
