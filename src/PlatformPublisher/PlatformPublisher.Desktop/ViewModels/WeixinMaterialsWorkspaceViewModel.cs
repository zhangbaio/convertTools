using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ChannelsAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class WeixinMaterialsWorkspaceViewModel : ObservableObject
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".ts", ".wmv", ".webm",
    };

    private readonly IProjectScanner _projectScanner;
    private readonly List<MaterialProjectRowViewModel> _allProjects = [];
    private bool _applyingAccount;

    public WeixinMaterialsWorkspaceViewModel(IProjectScanner projectScanner)
    {
        _projectScanner = projectScanner;
    }

    public ObservableCollection<ChannelsAccount> Accounts { get; } = [];
    public ObservableCollection<MaterialProjectRowViewModel> Projects { get; } = [];

    [ObservableProperty] private ChannelsAccount? _selectedAccount;
    [ObservableProperty] private string _workspaceRoot = string.Empty;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private string _statusMessage = "请选择工作账号和工作目录。";
    [ObservableProperty] private bool _isBusy;

    public event Action<ChannelsAccount>? AccountSelectionRequested;

    public string SelectionSummary => $"已选择 {Projects.Count(item => item.IsSelected)} / {Projects.Count}";
    public bool HasProjects => Projects.Count > 0;

    public void ApplyAccounts(IEnumerable<ChannelsAccount> accounts, ChannelsAccount? selected)
    {
        _applyingAccount = true;
        try
        {
            Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(account);
            SelectedAccount = selected is null
                ? Accounts.FirstOrDefault()
                : Accounts.FirstOrDefault(item => string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            WorkspaceRoot = SelectedAccount?.WorkRootDirectory ?? string.Empty;
            _allProjects.Clear();
            Projects.Clear();
            StatusMessage = SelectedAccount is null
                ? "请先选择视频号工作账号。"
                : string.IsNullOrWhiteSpace(WorkspaceRoot)
                    ? "当前账号尚未配置工作目录。"
                    : "工作目录已就绪，点击“扫描项目”刷新项目清单。";
            NotifyProjectState();
        }
        finally
        {
            _applyingAccount = false;
        }
    }

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is null)
        {
            StatusMessage = "请先选择视频号工作账号。";
            return;
        }
        if (!Directory.Exists(WorkspaceRoot))
        {
            StatusMessage = "请选择有效的素材发布工作目录。";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "正在扫描云帆项目和旧短剧助手项目…";
            var result = await _projectScanner.ScanAsync(Path.GetFullPath(WorkspaceRoot), null, cancellationToken);
            _allProjects.Clear();
            foreach (var project in result.Projects)
            {
                var row = BuildRow(project);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MaterialProjectRowViewModel.IsSelected))
                        OnPropertyChanged(nameof(SelectionSummary));
                };
                _allProjects.Add(row);
            }
            ApplyFilter();
            StatusMessage = $"扫描完成：发现 {_allProjects.Count} 个项目。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "项目扫描已停止。";
        }
        catch (Exception ex)
        {
            StatusMessage = "项目扫描失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SetWorkspace(string path)
    {
        WorkspaceRoot = path;
        _allProjects.Clear();
        Projects.Clear();
        NotifyProjectState();
    }

    public void SetAllSelected(bool selected)
    {
        foreach (var project in Projects) project.IsSelected = selected;
        OnPropertyChanged(nameof(SelectionSummary));
    }

    partial void OnSelectedAccountChanged(ChannelsAccount? value)
    {
        if (_applyingAccount || value is null) return;
        WorkspaceRoot = value.WorkRootDirectory ?? string.Empty;
        AccountSelectionRequested?.Invoke(value);
    }

    partial void OnQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selectedIds = Projects.Where(item => item.IsSelected).Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var token = Normalize(Query);
        Projects.Clear();
        foreach (var row in _allProjects.Where(item => token.Length == 0 || Normalize(
                     $"{item.OriginalTitle} {item.NewTitle} {item.SourceLabel} {item.WorkflowDirectory}").Contains(token)))
        {
            if (selectedIds.Contains(row.Key)) row.IsSelected = true;
            Projects.Add(row);
        }
        NotifyProjectState();
    }

    private void NotifyProjectState()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasProjects));
    }

    private static MaterialProjectRowViewModel BuildRow(ScannedProject project)
    {
        var workflowDirectory = Directory.Exists(project.WorkflowProjectDir)
            ? Path.GetFullPath(project.WorkflowProjectDir!)
            : Path.GetFullPath(project.SourceProjectDir);
        var sourceType = DetectSourceType(workflowDirectory);
        return new MaterialProjectRowViewModel(
            project.ProjectKey,
            Path.GetFullPath(project.SourceProjectDir),
            workflowDirectory,
            project.OriginalTitle,
            project.DisplayName,
            project.VideoCount,
            CountMaterials(workflowDirectory),
            sourceType.Label,
            sourceType.Kind);
    }

    private static (string Label, string Kind) DetectSourceType(string directory)
    {
        var normalized = directory.Replace('/', '\\');
        var configured = ReadSourceType(directory);
        if (configured.Contains("downloaded_system_highlight", StringComparison.OrdinalIgnoreCase) ||
            configured.Contains("downloaded_highlight", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("downloaded_system_highlight", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("system-highlight", StringComparison.OrdinalIgnoreCase))
            return ("下载的系统高光", "downloaded_system_highlight");
        if (configured.Contains("material_video_download", StringComparison.OrdinalIgnoreCase))
            return ("下载素材视频", "material_video_download");
        return ("云帆 / 项目素材", "project");
    }

    private static string ReadSourceType(string directory)
    {
        foreach (var fileName in new[] { "shortdrama-project.json", ".system-highlight-download.json" })
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                foreach (var name in new[] { "sourceType", "source_type", "queueEntrySource" })
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString() ?? string.Empty;
            }
            catch
            {
                // Invalid metadata should not prevent the rest of the workspace from loading.
            }
        }
        return string.Empty;
    }

    private static int CountMaterials(string directory)
    {
        var roots = new[]
        {
            Path.Combine(directory, "material-videos"),
            Path.Combine(directory, "materials"),
            Path.Combine(directory, "downloads"),
        };
        try
        {
            return roots.Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }
        catch
        {
            return 0;
        }
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}

public sealed partial class MaterialProjectRowViewModel : ObservableObject
{
    public MaterialProjectRowViewModel(string key, string sourceDirectory, string workflowDirectory,
        string originalTitle, string newTitle, int episodeCount, int materialCount, string sourceLabel, string sourceType)
    {
        Key = key;
        SourceDirectory = sourceDirectory;
        WorkflowDirectory = workflowDirectory;
        OriginalTitle = originalTitle;
        NewTitle = newTitle;
        EpisodeCount = episodeCount;
        MaterialCount = materialCount;
        SourceLabel = sourceLabel;
        SourceType = sourceType;
    }

    public string Key { get; }
    public string SourceDirectory { get; }
    public string WorkflowDirectory { get; }
    public string OriginalTitle { get; }
    public string NewTitle { get; }
    public int EpisodeCount { get; }
    public int MaterialCount { get; }
    public string SourceLabel { get; }
    public string SourceType { get; }
    public bool IsDownloadedSystemHighlight => string.Equals(SourceType, "downloaded_system_highlight", StringComparison.Ordinal);

    [ObservableProperty] private bool _isSelected;
}
