using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using ShortDrama.Core.Models;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly object _repairLogGate = new();
    private readonly Dictionary<string, (string ProjectName, string Directory)> _repairLogTargets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _repairRunningDirectories = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> RepairKuaishouVideosAsync(string titlesText)
    {
        if (!IsKuaishouPlatform)
            return "请先切换到快手分账。";
        if (_activeGlobalAccount is null)
            return "请先选择账号。";
        if (!Directory.Exists(DraftProjectDirectory))
            return "请先选择当前账号的工作目录。";
        var titles = KuaishouFailedEpisodeRepair.ParseTitles(titlesText);
        if (titles.Count == 0)
            return "请输入至少一个新剧名。";
        if (IsBusy)
            return "当前有任务在执行，请稍后再补传。";

        IReadOnlyList<KuaishouVideoRepairNote> notes = [];
        var restored = false;
        lock (_repairLogGate)
        {
            _repairLogTargets.Clear();
            _repairRunningDirectories.Clear();
        }
        try
        {
            await RunBusyAsync(async cancellationToken =>
            {
                StatusMessage = $"正在补传 {titles.Count} 部短剧的失败视频…";
                var sessionJob = new PublishJob
                {
                    Platform = SelectedPlatform.Value,
                    AccountId = _activeGlobalAccount.Id,
                    AccountName = _activeGlobalAccount.Name,
                    ConfigPath = ResolveGlobalConfigPath(_activeGlobalAccount, SelectedPlatform.Value),
                    ProjectDirectory = Path.GetFullPath(DraftProjectDirectory),
                    ProjectName = "视频补传",
                };
                notes = await _kuaishouUploadService.RepairFailedVideosAsync(
                    sessionJob,
                    titles,
                    (title, episodes, token) => ResolveRepairFilesAsync(title, episodes, () => restored = true, token),
                    new Progress<string>(message => LogRepair(RepairProjectForMessage(message), message)),
                    cancellationToken);
                if (restored)
                    await ScanWorkspaceCoreAsync(Path.GetFullPath(DraftProjectDirectory), SelectedPlatform.Value, cancellationToken);
                foreach (var note in notes)
                {
                    LogRepairNote(note);
                    await MarkRepairOutcomeAsync(note);
                }
                StatusMessage = $"视频补传结束：{notes.Count(item => item.Outcome == KuaishouVideoRepairOutcome.Uploaded)} 部已提交，" +
                                $"{notes.Count(item => item.Outcome == KuaishouVideoRepairOutcome.Failed)} 部失败，" +
                                $"{notes.Count(item => item.Outcome == KuaishouVideoRepairOutcome.Skipped)} 部跳过。";
            });
        }
        catch (OperationCanceledException)
        {
            LogRepairStopped("已停止。", "none");
            return JoinRepairNotes(notes, "已停止视频补传。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRepairStopped(ex.Message, "failed");
            LogRepair(null, "视频补传：" + ex.Message);
            return JoinRepairNotes(notes, ex.Message);
        }

        return JoinRepairNotes(notes, null);
    }

    private static string JoinRepairNotes(IReadOnlyList<KuaishouVideoRepairNote> notes, string? extra)
    {
        var lines = notes.Select(item => item.Message).ToList();
        if (!string.IsNullOrWhiteSpace(extra))
            lines.Add(extra);
        return lines.Count == 0 ? extra ?? "没有可补传的短剧。" : string.Join(Environment.NewLine, lines);
    }

    private async Task<KuaishouVideoRepairFiles> ResolveRepairFilesAsync(
        string title,
        IReadOnlyList<int> episodes,
        Action restored,
        CancellationToken cancellationToken)
    {
        try
        {
            var row = FindKuaishouRowByNewTitle(title);
            var listed = row is not null && Directory.Exists(row.ProjectDirectory);
            var hasVideos = listed && ProjectHasPlayableVideo(row!.ProjectDirectory);
            var archived = FindKuaishouArchive(title);
            switch (KuaishouFailedEpisodeRepair.Decide(listed, hasVideos, archived is not null))
            {
                case KuaishouVideoRepairRoute.UploadLocal:
                    AttachRepairLog(title, row!.ProjectDirectory, running: false);
                    var local = ReadyVideos(row.ProjectDirectory, title, episodes);
                    if (!local.ShouldSkip)
                        BeginRepairUpload(title, episodes);
                    return local;
                case KuaishouVideoRepairRoute.RestoreAndDownload:
                    var source = await RestoreAndDownloadAsync(archived!, title, restored, cancellationToken);
                    if (source is null)
                        return KuaishouVideoRepairFiles.Skip("回退后没有找到可下载的项目目录。");
                    AttachRepairLog(title, source, running: false);
                    var restoredFiles = ReadyVideos(source, title, episodes);
                    if (!restoredFiles.ShouldSkip)
                        BeginRepairUpload(title, episodes);
                    return restoredFiles;
                default:
                    return KuaishouVideoRepairFiles.Skip(listed
                        ? "在列表中，但没有可补传的视频文件。"
                        : "列表和归档里都没有这部剧。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return KuaishouVideoRepairFiles.Skip(ex.Message);
        }
    }

    private KuaishouVideoRepairFiles ReadyVideos(string projectDirectory, string title, IReadOnlyList<int> episodes)
    {
        var directories = KuaishouArtifactColumnStatusReader.ResolveDirectories(projectDirectory);
        var paths = KuaishouSettlementEpisodeVideoService.Prepare(
            directories.SourceDirectory,
            directories.WorkflowDirectory,
            title);
        var missing = KuaishouFailedEpisodeRepair.MissingEpisodes(paths, episodes);
        if (missing.Count > 0)
            return KuaishouVideoRepairFiles.Skip($"缺少{KuaishouFailedEpisodeRepair.FormatEpisodes(missing)}的本地视频。");
        return KuaishouVideoRepairFiles.Ready(paths);
    }

    private async Task<string?> RestoreAndDownloadAsync(
        ArchivedProjectItem archived,
        string title,
        Action restored,
        CancellationToken cancellationToken)
    {
        var workspace = Path.GetFullPath(DraftProjectDirectory);
        var platform = SelectedPlatform.Value;
        var config = KuaishouPersonalConfig.Load(CreateKuaishouArchiveJob(platform, title));
        if (string.IsNullOrWhiteSpace(config.ArchiveRootDirectory))
            throw new InvalidOperationException("请先配置当前版本的归档目录。");
        var planned = FirstExistingProject(archived.SourceProjectDir, archived.WorkflowProjectDir)
            ?? FirstText(archived.SourceProjectDir, archived.WorkflowProjectDir);
        var projectName = string.IsNullOrWhiteSpace(planned) ? title : AttachRepairLog(title, planned, running: true);
        LogRepair(projectName, $"视频补传：正在回退归档《{title}》。");
        TikTokArchivedProjectService.Restore(workspace, archived.ArchiveProjectDir, config.ArchiveRootDirectory);
        restored();
        var source = FirstExistingProject(archived.SourceProjectDir, archived.WorkflowProjectDir);
        if (source is null)
            return null;
        projectName = AttachRepairLog(title, source, running: true);
        LogRepair(projectName, $"视频补传：正在重新下载《{title}》。");
        var error = await RedownloadAsync(source, title, cancellationToken);
        if (error is not null)
            throw new InvalidOperationException(error);
        return source;
    }

    private async Task<string?> RedownloadAsync(string sourceDirectory, string title, CancellationToken cancellationToken)
    {
        SyncKuaishouWorkflow(sourceDirectory, title);
        var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        var runtimeConfigPath = PrepareWeixinRuntimeConfig(settings, sourceDirectory, "");
        try
        {
            var projectName = RepairProjectName(sourceDirectory);
            var progress = new Progress<WorkRunEvent>(item =>
            {
                if (!string.IsNullOrWhiteSpace(item.Message))
                    LogRepair(projectName, $"视频补传《{title}》：{item.Message}");
            });
            var downloaded = await _workService.RunProjectStepAsync(
                sourceDirectory,
                null,
                "download",
                true,
                progress,
                cancellationToken,
                runtimeConfigPath);
            if (!downloaded.Ok)
                return downloaded.Message ?? "重新下载失败。";
            SyncKuaishouWorkflow(sourceDirectory, title);
            return null;
        }
        finally
        {
            TryDeleteKuaishouRuntimeConfig(runtimeConfigPath);
        }
    }

    private string AttachRepairLog(string title, string directory, bool running)
    {
        var full = NormalizeLogDirectory(directory);
        var projectName = RepairProjectName(full);
        lock (_repairLogGate)
        {
            _repairLogTargets[title] = (projectName, full);
            if (running)
                _repairRunningDirectories.Add(full);
        }

        var key = CurrentKuaishouRunKey();
        if (!string.IsNullOrWhiteSpace(key))
            StoreKuaishouLogProjects([full], key, running ? "running" : "waiting", replace: false);
        if (running)
            SetKuaishouLogTone(full, "running");
        return projectName;
    }

    private void BeginRepairUpload(string title, IReadOnlyList<int> episodes)
    {
        if (!RepairTarget(title, out var target))
            return;
        lock (_repairLogGate)
            _repairRunningDirectories.Add(target.Directory);
        SetKuaishouLogTone(target.Directory, "running");
        LogRepair(target.ProjectName, $"视频补传《{title}》：准备补传{KuaishouFailedEpisodeRepair.FormatEpisodes(episodes)}。");
    }

    private void LogRepairNote(KuaishouVideoRepairNote note)
    {
        if (!RepairTarget(note.Title, out var target))
        {
            var row = FindKuaishouRowByNewTitle(note.Title);
            if (row is not null && Directory.Exists(row.ProjectDirectory))
                AttachRepairLog(note.Title, row.ProjectDirectory, running: false);
            RepairTarget(note.Title, out target);
        }

        var started = false;
        if (!string.IsNullOrWhiteSpace(target.Directory))
        {
            lock (_repairLogGate)
                started = _repairRunningDirectories.Remove(target.Directory);
            var tone = note.Outcome switch
            {
                KuaishouVideoRepairOutcome.Uploaded => "ok",
                KuaishouVideoRepairOutcome.Failed => "failed",
                _ => started ? "failed" : null,
            };
            if (tone is not null)
                SetKuaishouLogTone(target.Directory, tone);
        }

        var projectName = string.IsNullOrWhiteSpace(target.ProjectName) ? null : target.ProjectName;
        LogRepair(projectName, "视频补传：" + note.Message);
    }

    private void LogRepairStopped(string detail, string tone)
    {
        List<(string Title, string ProjectName, string Directory)> running;
        lock (_repairLogGate)
        {
            running = _repairLogTargets
                .Where(pair => _repairRunningDirectories.Contains(pair.Value.Directory))
                .Select(pair => (pair.Key, pair.Value.ProjectName, pair.Value.Directory))
                .ToList();
            _repairRunningDirectories.Clear();
        }

        foreach (var item in running)
        {
            SetKuaishouLogTone(item.Directory, tone);
            LogRepair(item.ProjectName, $"视频补传：《{item.Title}》{detail}");
        }
    }

    private string? RepairProjectForMessage(string message)
    {
        lock (_repairLogGate)
        {
            foreach (var pair in _repairLogTargets)
            {
                if (message.Contains($"《{pair.Key}》", StringComparison.Ordinal))
                    return pair.Value.ProjectName;
            }
        }

        return null;
    }

    private string RepairProjectName(string directory)
    {
        var normalized = NormalizeLogDirectory(directory);
        var job = _jobs.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.ProjectDirectory) &&
            string.Equals(NormalizeLogDirectory(item.ProjectDirectory), normalized, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(job?.ProjectName)
            ? Path.GetFileName(normalized)
            : job.ProjectName;
    }

    private bool RepairTarget(string title, out (string ProjectName, string Directory) target)
    {
        lock (_repairLogGate)
            return _repairLogTargets.TryGetValue(title, out target);
    }

    private void LogRepair(string? projectName, string message)
    {
        var previousAccount = ActivityLogAccountScope.Value;
        var previousPlatform = ActivityLogPlatformScope.Value;
        try
        {
            ActivityLogAccountScope.Value = CurrentKuaishouRunKey();
            ActivityLogPlatformScope.Value = SelectedPlatform.Value;
            AppendActivityLog(message, projectName);
        }
        finally
        {
            ActivityLogAccountScope.Value = previousAccount;
            ActivityLogPlatformScope.Value = previousPlatform;
        }
    }

    private static string? FirstText(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private async Task MarkRepairOutcomeAsync(KuaishouVideoRepairNote note)
    {
        if (note.Outcome == KuaishouVideoRepairOutcome.Skipped)
            return;
        var row = FindKuaishouRowByNewTitle(note.Title);
        if (row is null)
            return;
        var status = note.Outcome == KuaishouVideoRepairOutcome.Uploaded
            ? PublishJobStepStatus.Succeeded
            : PublishJobStepStatus.Failed;
        var message = note.Outcome == KuaishouVideoRepairOutcome.Uploaded ? "已补传失败视频" : note.Message;
        await UpdateStepStateAsync(row.Model, row, "weixin-upload", "上传剧集", status, message);
    }

    private PublishJobRowViewModel? FindKuaishouRowByNewTitle(string title)
    {
        var accountId = _activeGlobalAccount?.Id ?? "";
        PublishJobRowViewModel? match = null;
        foreach (var job in _jobs)
        {
            if (job.Platform != SelectedPlatform.Value || job.Kind != PublishJobKind.Series)
                continue;
            if (!string.IsNullOrWhiteSpace(accountId) &&
                !string.Equals(job.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_jobRowCache.TryGetValue(job.Id, out var row))
            {
                row = new PublishJobRowViewModel(job);
                _jobRowCache[job.Id] = row;
            }
            else
            {
                row.LoadTableInfo();
            }

            if (!string.Equals((row.NewTitle ?? "").Trim(), title, StringComparison.OrdinalIgnoreCase))
                continue;
            match = row;
            if (ProjectHasPlayableVideo(row.ProjectDirectory))
                return row;
        }

        return match;
    }

    private ArchivedProjectItem? FindKuaishouArchive(string title)
    {
        if (!Directory.Exists(DraftProjectDirectory))
            return null;
        var platform = SelectedPlatform.Value;
        var config = KuaishouPersonalConfig.Load(CreateKuaishouArchiveJob(platform, title));
        if (string.IsNullOrWhiteSpace(config.ArchiveRootDirectory) || !Directory.Exists(config.ArchiveRootDirectory))
            return null;
        var source = platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? "kuaishou_enterprise"
            : "kuaishou_personal";
        var accountId = _activeGlobalAccount?.Id ?? "";
        return TikTokArchivedProjectService
            .ListBySource(Path.GetFullPath(DraftProjectDirectory), config.ArchiveRootDirectory, source)
            .FirstOrDefault(item =>
                string.Equals((item.NewTitle ?? "").Trim(), title, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(item.AccountProfileId) ||
                 string.IsNullOrWhiteSpace(accountId) ||
                 string.Equals(item.AccountProfileId, accountId, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ProjectHasPlayableVideo(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
            return false;
        var directories = KuaishouArtifactColumnStatusReader.ResolveDirectories(projectDirectory);
        return KuaishouFailedEpisodeRepair.ContainsPlayableVideo(
            directories.SourceDirectory,
            directories.WorkflowDirectory);
    }

    private static string? FirstExistingProject(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                continue;
            if (File.Exists(Path.Combine(path, "shortdrama-project.json")) ||
                File.Exists(Path.Combine(path, "短剧信息.txt")))
                return path;
        }

        return paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }
}
