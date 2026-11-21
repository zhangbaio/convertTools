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
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

public partial class MaterialClipView : UserControl
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly Regex EpisodeNumber = new(@"第\s*(\d+)\s*[集话]|(?:^|\D)(\d+)$", RegexOptions.Compiled);
    private readonly ObservableCollection<MaterialClipQueueItem> _queue = [];
    private CancellationTokenSource? _running;

    public MaterialClipView()
    {
        InitializeComponent();
        TasksList.ItemsSource = _queue;
        UpdateButtons();
    }

    private MaterialClipQueueItem? Selected => TasksList.SelectedItem as MaterialClipQueueItem;
    public bool IsRunning => _running is not null;

    public void Stop() => _running?.Cancel();

    private async void ImportFiles(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要剪辑的视频", AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("视频素材") { Patterns = VideoExtensions.Select(ext => "*" + ext).ToArray() }],
            });
            var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().Where(IsVideo).ToArray();
            if (paths.Length == 0)
                return;
            var project = Path.GetDirectoryName(paths[0])!;
            var stem = Path.GetFileNameWithoutExtension(paths[0]);
            AddTask(project, paths.Length == 1 ? stem : $"{stem}等{paths.Length}个素材", paths);
        }
        catch (Exception ex) { QueueStatus.Text = $"导入视频失败。{ex.Message}"; }
    }

    private async void ImportFolders(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择剧集目录或包含多个剧集的目录", AllowMultiple = true,
            });
            var count = _queue.Count;
            foreach (var folder in folders.Select(folder => folder.TryGetLocalPath()).OfType<string>())
            {
                var videos = FindProjectVideos(folder);
                if (videos.Length > 0)
                {
                    AddTask(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), videos);
                    continue;
                }
                foreach (var child in Directory.EnumerateDirectories(folder).Where(path => !IsOutputFolder(path)))
                {
                    videos = FindProjectVideos(child);
                    if (videos.Length > 0)
                        AddTask(child, Path.GetFileName(child), videos);
                }
            }
            if (folders.Count > 0 && _queue.Count == count)
                QueueStatus.Text = "没有发现可新增的视频素材。";
        }
        catch (Exception ex) { QueueStatus.Text = $"导入目录失败。{ex.Message}"; }
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

    private void AddTask(string project, string title, IReadOnlyList<string> paths)
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
        var output = Path.Combine(project, "素材剪辑输出");
        var uniqueTitle = title;
        for (var suffix = 2; _queue.Any(item => item.OutputFolder == output && item.Title == uniqueTitle); suffix++)
            uniqueTitle = $"{title}-{suffix}";
        var task = new MaterialClipQueueItem(project, uniqueTitle, episodes, output);
        _queue.Add(task);
        TasksList.SelectedItem = task;
        QueueStatus.Text = $"已导入 {episodes.Count} 个视频，共 {_queue.Count} 个剪辑任务。";
        UpdateButtons();
    }

    private static int? TryEpisode(string path)
    {
        var match = EpisodeNumber.Match(Path.GetFileNameWithoutExtension(path));
        return match.Success && int.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
            out var number) && number > 0 ? number : null;
    }

    private async void StartQueue(object? sender, RoutedEventArgs e) => await RunAsync(_queue.Where(item => item.Pending).ToArray());

    private async void RetrySelected(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } task)
            await RunAsync([task]);
    }

    private void StopQueue(object? sender, RoutedEventArgs e)
    {
        QueueStatus.Text = "正在停止剪辑…";
        StopButton.IsEnabled = false;
        Stop();
    }

    private async Task RunAsync(IReadOnlyList<MaterialClipQueueItem> tasks)
    {
        if (_running is not null || tasks.Count == 0)
            return;
        MaterialClipSettings settings;
        TikTokPublisher.Core.Models.ClientSettings client;
        try
        {
            settings = MaterialClipSettingsStore.Load();
            client = ClientSettingsStore.Load();
            if (!settings.Clip.Modes.Any(mode => mode.Enabled))
            {
                QueueStatus.Text = "请在全局剪辑配置至少启用一种模式。";
                return;
            }
        }
        catch (Exception ex)
        {
            QueueStatus.Text = $"无法读取剪辑配置。{ex.Message}";
            return;
        }

        using var running = new CancellationTokenSource();
        _running = running;
        UpdateButtons();
        try
        {
            foreach (var task in tasks)
            {
                if (running.IsCancellationRequested)
                    break;
                TasksList.SelectedItem = task;
                task.Pending = false;
                task.Status = "剪辑中";
                task.Logs.Clear();
                task.Outputs.Clear();
                try
                {
                    var options = MaterialClipSettingsStore.BuildOptions(settings, client);
                    options.ProjectTitle = task.Title;
                    options.OutputDirectory = task.OutputFolder;
                    var modes = settings.Clip.Modes.Where(mode => mode.Enabled).Select(mode => mode.Label);
                    QueueStatus.Text = $"正在剪辑 {task.Title} · {string.Join(" / ", modes)}";
                    var result = await Task.Run(() => new ClipEngine().GenerateAsync(task.ProjectFolder, task.Episodes, options,
                        message => Dispatcher.UIThread.Post(() => AddLog(task, message)), running.Token,
                        path => Dispatcher.UIThread.Post(() => AddOutput(task, path))), running.Token);
                    foreach (var path in result.Outputs)
                        AddOutput(task, path);
                    task.Status = result.Ok ? (result.Issues.Count == 0 ? "已完成" : "有未完成项") : "失败";
                    if (!result.Ok && result.Error is not null)
                        AddLog(task, result.Error);
                }
                catch (OperationCanceledException) when (running.IsCancellationRequested)
                {
                    task.Status = "已停止";
                    AddLog(task, "剪辑已停止。重试会复用已完成的成片。");
                }
                catch (Exception ex)
                {
                    task.Status = "失败";
                    AddLog(task, ex.Message);
                }
                QueueStatus.Text = $"{task.Title} · {task.Status} · {task.Outputs.Count} 条成片";
            }
        }
        finally
        {
            _running = null;
            UpdateButtons();
        }
    }

    private void AddOutput(MaterialClipQueueItem task, string path)
    {
        var output = new MaterialClipOutput(path);
        if (!task.Outputs.Contains(output))
            task.Outputs.Add(output);
        if (ReferenceEquals(task, Selected) && Directory.Exists(task.OutputFolder))
            OpenOutputButton.IsEnabled = true;
    }

    private void AddLog(MaterialClipQueueItem task, string message)
    {
        if (task.Logs.Count >= 500)
            task.Logs.RemoveAt(0);
        var entry = $"{DateTime.Now:HH:mm:ss}  {message}";
        task.Logs.Add(entry);
        if (ReferenceEquals(task, Selected))
            LogList.ScrollIntoView(entry);
    }

    private void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LogList.ItemsSource = Selected?.Logs;
        OutputsList.ItemsSource = Selected?.Outputs;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var task = Selected;
        var busy = _running is not null;
        StartButton.IsEnabled = !busy && _queue.Any(item => item.Pending);
        RetryButton.IsEnabled = !busy && task is { Pending: false };
        StopButton.IsEnabled = _running is { IsCancellationRequested: false };
        RemoveButton.IsEnabled = ChangeOutputButton.IsEnabled = !busy && task is not null;
        OpenOutputButton.IsEnabled = task is not null && Directory.Exists(task.OutputFolder);
        SelectedSummary.Text = task is null ? "选择一个剪辑任务以查看进度。" : $"输出目录  {task.OutputFolder}";
    }

    private async void ChangeOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } task || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "设置成片输出目录" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } output)
            {
                task.OutputFolder = output;
                task.Pending = true;
                task.Status = "待剪辑";
                task.Outputs.Clear();
                UpdateButtons();
            }
        }
        catch (Exception ex) { QueueStatus.Text = ex.Message; }
    }

    private void RemoveSelected(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } task && _running is null)
            _queue.Remove(task);
        UpdateButtons();
    }

    private void OpenOutputFolder(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } task)
            OpenPath(task.OutputFolder);
    }

    private void OpenVideo(object? sender, TappedEventArgs e)
    {
        if (OutputsList.SelectedItem is MaterialClipOutput output)
            OpenPath(output.Path);
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
            QueueStatus.Text = "已保存全局剪辑配置，新任务将按保存的配置执行。";
    }
}

internal sealed record MaterialClipOutput(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

internal sealed partial class MaterialClipQueueItem(
    string projectFolder, string title, IReadOnlyList<EpisodeSource> episodes, string outputFolder) : ObservableObject
{
    public string ProjectFolder { get; } = projectFolder;
    public string Title { get; } = title;
    public IReadOnlyList<EpisodeSource> Episodes { get; } = episodes;
    public string Description => $"{Episodes.Count} 个视频  ·  {ProjectFolder}";
    public string OutputFolder { get; set; } = outputFolder;
    public bool Pending { get; set; } = true;
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<MaterialClipOutput> Outputs { get; } = [];

    [ObservableProperty]
    private string _status = "待剪辑";
}
