using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChannelsPublisher.Clip;
using CommunityToolkit.Mvvm.ComponentModel;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public partial class MaterialClipView : UserControl
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly Regex EpisodeNumber = new(@"第\s*(\d+)\s*[集话]|(?:^|\D)(\d+)$", RegexOptions.Compiled);
    private readonly ObservableCollection<MaterialClipQueueItem> _queue = [];
    private CancellationTokenSource? _running;
    private Func<IReadOnlyList<string>, Task<string?>>? _sendToWeixin;
    private bool _sendingToWeixin;
    private string _outputStatus = "";

    public MaterialClipView()
    {
        InitializeComponent();
        TasksList.ItemsSource = _queue;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        UpdateButtons();
    }

    private MaterialClipQueueItem? Selected => TasksList.SelectedItem as MaterialClipQueueItem;
    public bool IsRunning => _running is not null;

    public void BindWeixinExport(Func<IReadOnlyList<string>, Task<string?>> send)
    {
        _sendToWeixin = send;
        SendToWeixinButton.IsVisible = true;
        UpdateButtons();
    }

    public void Stop() => _running?.Cancel();

    private async void ImportFiles(object? sender, RoutedEventArgs e)
    {
        if (IsRunning || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要剪辑的视频", AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("视频素材") { Patterns = VideoExtensions.Select(ext => "*" + ext).ToArray() }],
            });
            ImportMaterials(files);
        }
        catch (Exception ex) { QueueStatus.Text = $"导入视频失败。{ex.Message}"; }
    }

    private async void ImportFolders(object? sender, RoutedEventArgs e)
    {
        if (IsRunning || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择剧集目录或包含多个剧集的目录", AllowMultiple = true,
            });
            ImportMaterials(folders);
        }
        catch (Exception ex) { QueueStatus.Text = $"导入目录失败。{ex.Message}"; }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !IsRunning && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try { ImportMaterials(e.DataTransfer.TryGetFiles() ?? []); }
        catch (Exception ex) { QueueStatus.Text = $"导入素材失败。{ex.Message}"; }
    }

    private void ImportMaterials(IReadOnlyList<IStorageItem> items)
    {
        if (IsRunning || items.Count == 0)
            return;

        var defaults = new MaterialClipTaskConfiguration(MaterialClipSettingsStore.Load().Clip);
        var initialCount = _queue.Count;
        var paths = items.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
        var videos = paths.Where(IsVideo).ToArray();
        if (videos.Length > 0)
        {
            var stem = Path.GetFileNameWithoutExtension(videos[0]);
            AddTask(Path.GetDirectoryName(videos[0])!, videos.Length == 1 ? stem : $"{stem}等{videos.Length}个素材", videos, defaults);
        }

        foreach (var folder in paths.Where(Directory.Exists))
        {
            if (IsOutputFolder(folder))
                continue;
            videos = FindProjectVideos(folder);
            if (videos.Length > 0)
            {
                AddTask(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), videos, defaults);
                continue;
            }
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                videos = FindProjectVideos(child);
                if (videos.Length > 0)
                    AddTask(child, Path.GetFileName(child), videos, defaults);
            }
        }
        var added = _queue.Count - initialCount;
        QueueStatus.Text = added == 0 ? "没有发现可新增的视频素材。" : $"已添加 {added} 个剪辑任务。";
    }

    private static string[] FindProjectVideos(string folder)
    {
        if (IsOutputFolder(folder))
            return [];
        foreach (var subfolder in new[] { "videos", "source-originals", "video", "episodes", "剧集", "原视频", "" })
        {
            var directory = Path.Combine(folder, subfolder);
            if (!Directory.Exists(directory))
                continue;
            var videos = Directory.EnumerateFiles(directory).Where(IsVideo).ToArray();
            if (videos.Length > 0)
                return videos;
        }
        return [];
    }

    private static bool IsOutputFolder(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is "素材剪辑输出" or "material-clip-output";

    private static bool IsVideo(string path) => File.Exists(path) &&
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void AddTask(string project, string title, IReadOnlyList<string> paths, MaterialClipTaskConfiguration defaults)
    {
        var entries = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path: path, Episode: TryEpisode(path))).OrderBy(source => source.Episode ?? int.MaxValue)
            .ThenBy(source => source.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var reserved = entries.Select(source => source.Episode).OfType<int>().ToHashSet();
        var used = new HashSet<int>();
        var fallback = 1;
        var episodes = new List<EpisodeSource>();
        foreach (var entry in entries)
        {
            var number = entry.Episode;
            if (number is null || !used.Add(number.Value))
            {
                while (reserved.Contains(fallback) || used.Contains(fallback))
                    fallback++;
                number = fallback++;
                used.Add(number.Value);
            }
            episodes.Add(new EpisodeSource(number.Value, entry.Path));
        }

        var existing = _queue.FirstOrDefault(item => item.Episodes.SequenceEqual(episodes));
        if (existing is not null)
        {
            TasksList.SelectedItem = existing;
            return;
        }
        Enqueue(project, title, episodes, Path.Combine(project, "素材剪辑输出"), defaults.Copy());
    }

    private void Enqueue(string project, string title, IReadOnlyList<EpisodeSource> episodes, string output,
        MaterialClipTaskConfiguration configuration)
    {
        var uniqueTitle = title;
        for (var suffix = 2; OutputNameInUse(uniqueTitle, output); suffix++)
            uniqueTitle = $"{title}-{suffix}";
        var task = new MaterialClipQueueItem(project, uniqueTitle, episodes, output, configuration);
        task.PropertyChanged += OnTaskPropertyChanged;
        _queue.Add(task);
        TasksList.SelectedItem = task;
        TaskTabs.SelectedIndex = 0;
        UpdateButtons();
    }

    private bool OutputNameInUse(string title, string output, MaterialClipQueueItem? except = null) =>
        _queue.Any(item => item != except && string.Equals(ClipEngine.OutputFileStem(item.Title),
                ClipEngine.OutputFileStem(title), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.TrimEndingDirectorySeparator(item.OutputFolder),
                Path.TrimEndingDirectorySeparator(output), StringComparison.OrdinalIgnoreCase));

    private static int? TryEpisode(string path)
    {
        var match = EpisodeNumber.Match(Path.GetFileNameWithoutExtension(path));
        return match.Success && int.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
            out var number) && number > 0 ? number : null;
    }

    private async void StartQueue(object? sender, RoutedEventArgs e) =>
        await RunConfiguredTasksAsync(_queue.Where(item => item.Pending).ToArray());

    private async void RetrySelected(object? sender, RoutedEventArgs e)
    {
        if (Selected is { CanRetry: true } task)
            await RunConfiguredTasksAsync([task]);
    }

    private async Task RunConfiguredTasksAsync(IReadOnlyList<MaterialClipQueueItem> tasks)
    {
        if (IsRunning || _sendingToWeixin || tasks.Count == 0)
            return;
        foreach (var task in tasks)
        {
            if (task.Configuration.Validate() is not { } error)
                continue;
            TasksList.SelectedItem = task;
            TaskTabs.SelectedIndex = 0;
            QueueStatus.Text = $"{task.Title} · {error}";
            return;
        }

        (MaterialClipQueueItem Task, ClipEngineOptions Options)[] runs;
        try
        {
            var services = MaterialClipSettingsStore.Load().Services;
            var client = ClientSettingsStore.Load();
            runs = tasks.Select(task => (task, task.CreateRun(services, client))).ToArray();
        }
        catch (Exception ex)
        {
            QueueStatus.Text = $"无法准备剪辑任务。{ex.Message}";
            return;
        }
        await RunAsync(runs);
    }

    private void StopQueue(object? sender, RoutedEventArgs e)
    {
        QueueStatus.Text = "正在停止剪辑，尚未开始的任务会留在队列中。";
        Stop();
        UpdateButtons();
    }

    private async Task RunAsync(IReadOnlyList<(MaterialClipQueueItem Task, ClipEngineOptions Options)> runs)
    {
        if (IsRunning || _sendingToWeixin)
            return;
        using var running = new CancellationTokenSource();
        _running = running;
        UpdateButtons();
        try
        {
            foreach (var (task, options) in runs)
            {
                if (running.IsCancellationRequested)
                    break;
                task.BeginRun(options);
                foreach (var mode in options.ModeSettings ?? [])
                {
                    var label = MaterialClipModeEditor.Label(mode.Mode, options.StoryHighlights);
                    var ranges = string.Join("、", mode.Ranges.Select(range => $"{range.MinSeconds}–{range.MaxSeconds} 秒"));
                    AddLog(task, $"{label}出片时长：{ranges}");
                }
                var setupLogCount = task.Logs.Count;
                if (ReferenceEquals(task, Selected))
                    UpdateOutputs();
                QueueStatus.Text = $"正在剪辑 {task.Title}";
                try
                {
                    var result = await Task.Run(() => new ClipEngine().GenerateAsync(task.ProjectFolder, task.Episodes, options,
                        message => Dispatcher.UIThread.Post(() => AddLog(task, message)), running.Token,
                        path => Dispatcher.UIThread.Post(() => AddOutput(task, path, options.FfprobePath)),
                        progress => Dispatcher.UIThread.Post(() => SetProgress(task, progress))), running.Token);
                    foreach (var path in result.Outputs)
                        AddOutput(task, path, options.FfprobePath);
                    task.Finish(result.Ok ? (result.Issues.Count == 0 ? "已完成" : "有未完成项") : "失败");
                    if (!result.Ok && result.Error is not null && task.Logs.Count == setupLogCount)
                        AddLog(task, result.Error);
                }
                catch (OperationCanceledException) when (running.IsCancellationRequested)
                {
                    task.Finish("已停止");
                    AddLog(task, "剪辑已停止。可重试当前任务，或继续队列中尚未开始的任务。");
                }
                catch (Exception ex)
                {
                    task.Finish("失败");
                    AddLog(task, ex.Message);
                }
                QueueStatus.Text = $"{task.Title} · {task.Status} · {task.Outputs.Count} 条成片";
                UpdateOutputs();
            }
        }
        finally
        {
            _running = null;
            UpdateButtons();
        }
    }

    private static void SetProgress(MaterialClipQueueItem task, ClipProgress progress)
    {
        if (!task.Running)
            return;
        var mode = progress.Mode is { } key ? MaterialClipModeEditor.Label(key, task.Configuration.StoryHighlights) : "";
        task.Status = progress.Stage switch
        {
            ClipStage.Analyzing => $"分析素材 {progress.Current}/{progress.Total} 集",
            ClipStage.Reviewing => "补充硬字幕或复评画面",
            ClipStage.Selecting => $"{mode} · 选段 {progress.Current}/{progress.Total}",
            ClipStage.Rendering => $"{mode} · 渲染 {progress.Current}/{progress.Total}",
            _ => "剪辑中",
        };
    }

    private void AddOutput(MaterialClipQueueItem task, string path, string ffprobe)
    {
        if (task.Outputs.Any(output => string.Equals(output.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;
        var output = new MaterialClipOutput(path);
        task.Outputs.Add(output);
        task.RefreshDescription();
        if (ReferenceEquals(task, Selected))
        {
            if (OutputsList.SelectedIndex < 0)
                OutputsList.SelectedIndex = 0;
            UpdateOutputs();
        }
        _ = output.LoadReviewAsync(ffprobe);
    }

    private void AddLog(MaterialClipQueueItem task, string message)
    {
        if (task.Logs.Count >= 500)
            task.Logs.RemoveAt(0);
        var entry = $"{DateTime.Now:HH:mm:ss}  {message}";
        task.Logs.Add(entry);
        if (ReferenceEquals(task, Selected) && TaskTabs.SelectedIndex == 2)
            LogList.ScrollIntoView(entry);
    }

    private void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var task = Selected;
        LogList.ItemsSource = task?.Logs;
        OutputsList.ItemsSource = task?.Outputs;
        OutputsList.SelectedIndex = task is { Outputs.Count: > 0 } ? 0 : -1;
        TaskTabs.SelectedIndex = task is { Outputs.Count: > 0 } ? 1 : 0;
        RebuildEditor();
        UpdateButtons();
    }

    private void OnTaskPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MaterialClipQueueItem.IsChecked))
            UpdateButtons();
    }

    private void RebuildEditor()
    {
        var task = Selected;
        ModeEditors.Children.Clear();
        AddModeButton.Flyout = null;
        if (task is null)
            return;
        var config = task.Configuration;
        var enabled = config.EnabledModes;
        foreach (var mode in enabled)
        {
            var editor = new MaterialClipModeEditor(config, mode, () => ConfigurationChanged(task), () => RebuildEditor());
            ModeEditors.Children.Add(editor);
        }

        var flyout = new MenuFlyout();
        foreach (var mode in MaterialClipModeEditor.Modes.Except(enabled))
        {
            var item = new MenuItem { Header = MaterialClipModeEditor.Label(mode, story: true) };
            item.Click += (_, _) =>
            {
                if (IsRunning)
                    return;
                config.SetModeEnabled(mode, true);
                if (mode == "highlight")
                    config.StoryHighlights = true;
                ConfigurationChanged(task);
                RebuildEditor();
            };
            flyout.Items.Add(item);
        }
        AddModeButton.Flyout = flyout;
        AddModeButton.IsVisible = flyout.Items.Count > 0;
        UpdateSettingsSummary();
    }

    private void ConfigurationChanged(MaterialClipQueueItem task)
    {
        task.MarkConfigurationEdited();
        UpdateButtons();
    }

    private void ApplyToChecked(object? sender, RoutedEventArgs e)
    {
        if (IsRunning || Selected is not { } source || source.Configuration.Validate() is not null)
            return;
        var targets = _queue.Where(item => item.IsChecked && item != source).ToArray();
        foreach (var target in targets)
        {
            target.Configuration = source.Configuration.Copy();
            target.PrepareForNewRun();
        }
        QueueStatus.Text = $"已将剪辑配置应用到 {targets.Length} 个勾选任务。";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var task = Selected;
        var busy = IsRunning || _sendingToWeixin;
        var hasTasks = _queue.Count > 0;
        var pending = _queue.Count(item => item.Pending);
        var checkedCount = _queue.Count(item => item.IsChecked);
        ImportButton.IsVisible = Workspace.IsVisible = hasTasks;
        EmptyPanel.IsVisible = !hasTasks;
        ImportButton.IsEnabled = !busy;
        StartButton.IsVisible = !IsRunning && pending > 0;
        StartButton.IsEnabled = !busy;
        StartButton.Content = $"开始队列（{pending}）";
        StopButton.IsVisible = IsRunning;
        StopButton.IsEnabled = _running is { IsCancellationRequested: false };
        RetryButton.IsVisible = task is { CanRetry: true };
        RetryButton.IsEnabled = !busy;
        DuplicateButton.IsEnabled = !busy && task is not null;
        RemoveButton.IsEnabled = !busy && (task is not null || checkedCount > 0);
        RemoveButton.Content = checkedCount > 0 ? $"移除勾选（{checkedCount}）" : "移除当前任务";
        SettingsPanel.IsEnabled = !busy && task is not null;
        ApplyCheckedButton.IsEnabled = !busy && task is not null &&
            _queue.Any(item => item != task && item.IsChecked) && task.Configuration.Validate() is null;
        SelectedSummary.Text = task?.Title ?? "请选择剪辑任务";
        TaskTabs.IsVisible = task is not null;
        UpdateSettingsSummary();
        UpdateOutputs();
    }

    private void UpdateSettingsSummary()
    {
        OutputFolderText.Text = Selected?.OutputFolder;
        SettingsStatus.Text = Selected?.Configuration.Validate();
    }

    private void UpdateOutputs()
    {
        var task = Selected;
        PreviewButton.IsEnabled = OutputsList.SelectedItem is MaterialClipOutput;
        OpenOutputButton.IsEnabled = task is not null && Directory.Exists(task.OutputFolder);
        SendToWeixinButton.IsEnabled = !IsRunning && !_sendingToWeixin && task is { Outputs.Count: > 0 };
        OutputsSummary.Text = _outputStatus.Length > 0 ? _outputStatus : task?.Outputs.Count switch
        {
            > 0 => $"已有 {task.Outputs.Count} 条成片",
            _ when task?.Running == true => "正在剪辑，生成后可预览成片。",
            _ => "暂无成片。可在「日志」查看执行详情。",
        };
    }

    private void OutputSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _outputStatus = "";
        UpdateOutputs();
    }

    private async void ChangeOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (IsRunning || Selected is not { } task || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "设置成片输出目录" });
            if (IsRunning || !_queue.Contains(task) || folders.FirstOrDefault()?.TryGetLocalPath() is not { } output ||
                string.Equals(Path.TrimEndingDirectorySeparator(task.OutputFolder), Path.TrimEndingDirectorySeparator(output), StringComparison.OrdinalIgnoreCase))
                return;
            if (OutputNameInUse(task.Title, output, task))
            {
                QueueStatus.Text = "此目录已有同名任务，请选择其他输出目录。";
                return;
            }
            task.OutputFolder = output;
            ConfigurationChanged(task);
        }
        catch (Exception ex) { QueueStatus.Text = ex.Message; }
    }

    private void DuplicateSelected(object? sender, RoutedEventArgs e)
    {
        if (!IsRunning && Selected is { } task)
            Enqueue(task.ProjectFolder, task.Title, task.Episodes, task.OutputFolder, task.Configuration.Copy());
    }

    private void RemoveSelected(object? sender, RoutedEventArgs e)
    {
        if (IsRunning || _sendingToWeixin)
            return;
        var checkedTasks = _queue.Where(item => item.IsChecked).ToArray();
        var targets = checkedTasks.Length > 0 ? checkedTasks : Selected is { } task ? [task] : [];
        foreach (var target in targets)
        {
            target.PropertyChanged -= OnTaskPropertyChanged;
            _queue.Remove(target);
        }
        if (Selected is null && _queue.Count > 0)
            TasksList.SelectedIndex = 0;
        QueueStatus.Text = "";
        UpdateButtons();
    }

    private void OpenOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } task)
            OpenPath(task.OutputFolder);
    }

    private async void OnSendToWeixinClick(object? sender, RoutedEventArgs e)
    {
        if (_sendToWeixin is not { } send || Selected is not { } task || IsRunning || _sendingToWeixin)
            return;
        var paths = task.Outputs.Select(output => output.Path).Where(File.Exists).ToArray();
        if (paths.Length == 0)
        {
            QueueStatus.Text = "找不到可发送的视频号成片。";
            return;
        }
        _sendingToWeixin = true;
        UpdateButtons();
        try { QueueStatus.Text = await send(paths) ?? $"已发送 {paths.Length} 条成片到视频号"; }
        catch (Exception ex) { QueueStatus.Text = $"发送成片失败。{ex.Message}"; }
        finally
        {
            _sendingToWeixin = false;
            UpdateButtons();
        }
    }

    private async void PreviewSelected(object? sender, RoutedEventArgs e) => await PreviewAsync();
    private async void OpenVideo(object? sender, TappedEventArgs e) => await PreviewAsync();

    private async Task PreviewAsync()
    {
        if (OutputsList.SelectedItem is not MaterialClipOutput output || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        if (!File.Exists(output.Path))
        {
            _outputStatus = "成片文件已移动或删除。";
            UpdateOutputs();
            return;
        }

        try { await new MaterialClipPreviewWindow(output, OpenPath).ShowDialog(owner); }
        catch (Exception ex)
        {
            _outputStatus = $"无法打开预览。{ex.Message}";
            UpdateOutputs();
        }
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { QueueStatus.Text = $"无法打开路径。{ex.Message}"; }
    }

    private async void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        MaterialClipSettings settings;
        var error = "";
        try { settings = MaterialClipSettingsStore.Load(); }
        catch (Exception ex) { settings = new MaterialClipSettings(); error = $"读取配置失败。{ex.Message}"; }
        var dialog = new MaterialClipSettingsDialog(settings, error);
        if (await dialog.ShowDialog<bool>(owner))
            QueueStatus.Text = "已保存默认剪辑设置。新导入的任务会使用新的默认模式和参数。";
    }
}
