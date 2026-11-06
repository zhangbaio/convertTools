using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using ShortDrama.Core.Models;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
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
                    new Progress<string>(message => AppendActivityLog(message)),
                    cancellationToken);
                if (restored)
                    await ScanWorkspaceCoreAsync(Path.GetFullPath(DraftProjectDirectory), SelectedPlatform.Value, cancellationToken);
                foreach (var note in notes)
                    await MarkRepairOutcomeAsync(note);
                StatusMessage = $"视频补传结束：{notes.Count(item => item.Outcome == KuaishouVideoRepairOutcome.Uploaded)} 部已提交，" +
                                $"{notes.Count(item => item.Outcome == KuaishouVideoRepairOutcome.Failed)} 部失败，" +
                                $"{notes.Count(item => item.Outcome == KuaishouVideoRepairOutcome.Skipped)} 部跳过。";
            });
        }
        catch (OperationCanceledException)
        {
            return JoinRepairNotes(notes, "已停止视频补传。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
                    return ReadyVideos(row!.ProjectDirectory, title, episodes);
                case KuaishouVideoRepairRoute.RestoreAndDownload:
                    var source = await RestoreAndDownloadAsync(archived!, title, restored, cancellationToken);
                    return source is null
                        ? KuaishouVideoRepairFiles.Skip("回退后没有找到可下载的项目目录。")
                        : ReadyVideos(source, title, episodes);
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
        AppendActivityLog($"视频补传：正在回退归档《{title}》。", title);
        TikTokArchivedProjectService.Restore(workspace, archived.ArchiveProjectDir, config.ArchiveRootDirectory);
        restored();
        var source = FirstExistingProject(archived.SourceProjectDir, archived.WorkflowProjectDir);
        if (source is null)
            return null;
        AppendActivityLog($"视频补传：正在重新下载《{title}》。", title);
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
            var progress = new Progress<WorkRunEvent>(item =>
            {
                if (!string.IsNullOrWhiteSpace(item.Message))
                    AppendActivityLog(item.Message, title);
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
