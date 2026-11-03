using System.Text.Json;
using Avalonia.Threading;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using GlobalAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly string[] DefaultKsSettleDispatchSteps =
    [
        "download",
        "rewrite_info",
        "generate_poster",
        "generate_project_images",
        "auto_fill_info",
        "upload_series",
    ];
    public sealed record KsSettleRegistrationAccount(
        string Id,
        string Name,
        string Edition,
        string Workspace,
        bool AuthReady,
        bool RemoteDispatchEnabled);

    public sealed record KsSettleCommand(
        string Command,
        long AssignmentId,
        string ProgressPath,
        string AccountProfileId,
        string Edition,
        string Title,
        IReadOnlyList<string> EnabledSteps);

    public sealed record YunfanRemoteCommandResult(string Status, string Summary, string FailureKind = "");

    public sealed record KsSettleOpenAssignment(long Id, string Status, string SeriesId, string Title, string AccountProfileId, string Edition);

    public void LogKsSettleDispatch(string message) => AppendActivityLog(message);

    public IReadOnlyList<KsSettleRegistrationAccount> KsSettleRegistrationAccounts()
    {
        var accounts = new List<KsSettleRegistrationAccount>();
        foreach (var account in _globalAccounts)
        {
            if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
                continue;
            var edition = platform == PublishPlatform.KuaishouEnterpriseRevenue
                ? "enterprise"
                : "personal";
            var workspace = ResolveKuaishouWorkRoot(account, platform);
            var authPath = platform == PublishPlatform.KuaishouEnterpriseRevenue
                ? account.KuaishouEnterpriseAuthStatePath
                : account.KuaishouPersonalAuthStatePath;
            accounts.Add(new KsSettleRegistrationAccount(
                account.Id,
                account.Name,
                edition,
                workspace,
                !string.IsNullOrWhiteSpace(authPath) && File.Exists(authPath),
                true));
        }
        return accounts;
    }

    public Task<YunfanRemoteCommandResult> ExecuteKsSettlementCommandAsync(
        KsSettleCommand command,
        Func<string, string, string, string, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<YunfanRemoteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                done.TrySetResult(await ExecuteKsSettlementOnUiAsync(command, reportAsync, cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    public async Task RecoverKsSettleAssignmentsAsync(
        IReadOnlyList<KsSettleOpenAssignment> items,
        Func<long, string, string, string, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var located = FindMarkedProject(item.Id);
            var marker = located.Directory is null ? null : KsSettleMarker.Read(located.Directory);
            var action = KsSettleAutoPublishRecovery.Decide(
                item.Status,
                marker?.IsActive == true,
                marker?.FinalStatus,
                located.Directory is not null && Directory.Exists(located.Directory));
            switch (action)
            {
                case KsSettleAutoPublishRecovery.Action.ReportSuccess:
                    await reportAsync(item.Id, "success", "upload_series", "本地上传已经完成").ConfigureAwait(false);
                    if (located.Directory is not null)
                        KsSettleMarker.WriteFinal(located.Directory, item.Id, "success");
                    break;
                case KsSettleAutoPublishRecovery.Action.ReportInterrupted:
                    await reportAsync(item.Id, "interrupted", "", "客户端未找到本地项目").ConfigureAwait(false);
                    break;
                case KsSettleAutoPublishRecovery.Action.Resume when located.Directory is not null && located.Account is not null:
                    await ResumeMarkedProjectAsync(item, located.Account, located.Directory, reportAsync, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task<YunfanRemoteCommandResult> ExecuteKsSettlementOnUiAsync(
        KsSettleCommand command,
        Func<string, string, string, string, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        var account = _globalAccounts.FirstOrDefault(item =>
            string.Equals(item.Id, command.AccountProfileId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
            return new YunfanRemoteCommandResult("failed", "客户端没有这个快手分账账号");
        if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var accountPlatform))
            return new YunfanRemoteCommandResult("failed", "账号还没有选择快手分账类型");
        var edition = string.Equals(command.Edition, "enterprise", StringComparison.OrdinalIgnoreCase)
            ? "enterprise"
            : "personal";
        var commandPlatform = edition == "enterprise"
            ? PublishPlatform.KuaishouEnterpriseRevenue
            : PublishPlatform.KuaishouPersonalRevenue;
        if (accountPlatform != commandPlatform)
            return new YunfanRemoteCommandResult("failed", $"账号「{account.Name}」是{KuaishouAccountEdition.Label(account.KuaishouAccountEdition)}，不能执行另一版本");

        var key = KuaishouRunKey(account.Id, commandPlatform);
        var cleared = UncheckKuaishouJobsExcept(account.Id, commandPlatform, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (cleared > 0)
        {
            await PersistAsync(cancellationToken).ConfigureAwait(true);
            RefreshVisibleJobs();
            AppendActivityLog($"分发前已取消勾选 {cleared} 个手动选择的项目。");
        }
        if (IsKuaishouAccountRunning(key))
            return new YunfanRemoteCommandResult("failed", "当前已有快手分账队列在执行，请等待完成后再发起新任务。", "queue_busy");

        var workspace = ResolveKuaishouWorkRoot(account, commandPlatform);
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            return new YunfanRemoteCommandResult("failed", "快手分账工作目录不存在");
        if (string.IsNullOrWhiteSpace(command.Title))
            return new YunfanRemoteCommandResult("failed", "命令没有剧名");

        DramaTitleImportOutcome outcome;
        try
        {
            outcome = await _dramaTitleImportService.ImportAsync(workspace, command.Title, AppendActivityLog, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new YunfanRemoteCommandResult("failed", ex.Message);
        }

        if (outcome.ProjectDirectories.Count == 0)
        {
            var reason = outcome.Failures.FirstOrDefault()?.Reason ?? "没有创建本地项目";
            if (outcome.Failures.Count > 0 && outcome.Failures.All(failure => failure.Reason.Contains("已存在", StringComparison.Ordinal)))
                return new YunfanRemoteCommandResult("success", "没有需要上传的新剧（全部已存在）");
            return new YunfanRemoteCommandResult("failed", $"{command.Title}：{reason}");
        }

        var dirs = outcome.ProjectDirectories
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await AddImportedProjectJobsAsync(
            dirs,
            account.Id,
            account.Name,
            account.ProfileDir,
            commandPlatform,
            ResolveGlobalConfigPath(account, commandPlatform)).ConfigureAwait(true);
        UncheckKuaishouJobsExcept(account.Id, commandPlatform, dirs);
        await PersistAsync(cancellationToken).ConfigureAwait(true);
        RefreshVisibleJobs();
        var steps = command.EnabledSteps.Count == 0
            ? DefaultKsSettleDispatchSteps
            : command.EnabledSteps;
        foreach (var dir in dirs)
            KsSettleMarker.WriteActive(dir, command.AssignmentId, command.Title, steps);
        KuaishouPipelineRequest request;
        try
        {
            request = CreateDispatchPipelineRequest(account, commandPlatform, steps);
        }
        catch (Exception ex)
        {
            return new YunfanRemoteCommandResult("failed", "快手分账配置读取失败：" + ex.Message);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_kuaishouRunGate)
        {
            if (_kuaishouAccountRuns.ContainsKey(key))
            {
                cts.Dispose();
                return new YunfanRemoteCommandResult("failed", "当前已有快手分账队列在执行，请等待完成后再发起新任务。", "queue_busy");
            }
            _kuaishouAccountRuns[key] = cts;
        }
        NotifyCommands();
        _ = RunDispatchPipelineAsync(account, key, cts, dirs.ToArray(), request, command, reportAsync);
        return new YunfanRemoteCommandResult("accepted", $"已开始执行「{command.Title}」");
    }

    private async Task RunDispatchPipelineAsync(
        GlobalAccount account,
        string key,
        CancellationTokenSource cts,
        IReadOnlyList<string> targets,
        KuaishouPipelineRequest request,
        KsSettleCommand command,
        Func<string, string, string, string, Task> reportAsync)
    {
        if (command.AssignmentId > 0)
            await reportAsync("running", "download", $"开始执行「{command.Title}」", "").ConfigureAwait(true);
        try
        {
            PrepareKuaishouLogProjects(targets, key);
            var (succeeded, failed, error) = await RunKuaishouCheckedProjectsAsync(targets, key, request, cts.Token)
                .ConfigureAwait(true);
            if (failed > 0 || succeeded == 0)
            {
                foreach (var dir in targets)
                    KsSettleMarker.WriteFinal(dir, command.AssignmentId, "");
                await reportAsync("failed", "", string.IsNullOrWhiteSpace(error) ? "快手分账执行失败" : error, "")
                    .ConfigureAwait(true);
                return;
            }
            foreach (var dir in targets)
                KsSettleMarker.WriteFinal(dir, command.AssignmentId, "success");
            await reportAsync("success", "upload_series", $"上传完成：{command.Title}", "").ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await reportAsync("failed", "", ex.Message, "").ConfigureAwait(true);
        }
        finally
        {
            lock (_kuaishouRunGate)
            {
                if (_kuaishouAccountRuns.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                    _kuaishouAccountRuns.Remove(key);
            }
            cts.Dispose();
            NotifyCommands();
            NotifyKuaishouLogProjectsChanged();
        }
    }

    private async Task ResumeMarkedProjectAsync(
        KsSettleOpenAssignment item,
        GlobalAccount account,
        string project,
        Func<long, string, string, string, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<YunfanRemoteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                done.TrySetResult(await ResumeMarkedProjectOnUiAsync(item, account, project, reportAsync, cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        var result = await done.Task.ConfigureAwait(false);
        if (result.Status is "skipped" or "accepted")
            return;
        await reportAsync(item.Id, result.Status, "", result.Summary).ConfigureAwait(false);
    }

    private async Task<YunfanRemoteCommandResult> ResumeMarkedProjectOnUiAsync(
        KsSettleOpenAssignment item,
        GlobalAccount account,
        string project,
        Func<long, string, string, string, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
            return new YunfanRemoteCommandResult("failed", "账号还没有选择快手分账类型");
        var edition = platform == PublishPlatform.KuaishouEnterpriseRevenue ? "enterprise" : "personal";
        var wanted = string.Equals(item.Edition, "enterprise", StringComparison.OrdinalIgnoreCase) ? "enterprise" : "personal";
        if (edition != wanted)
            return new YunfanRemoteCommandResult("failed", "本地项目所在账号版本与派单不一致");

        var key = KuaishouRunKey(account.Id, platform);
        if (IsKuaishouAccountRunning(key))
        {
            AppendActivityLog($"快手分账派单 {item.Id} 的本地项目还在，当前队列仍在执行，稍后再继续。");
            return new YunfanRemoteCommandResult("skipped", "");
        }

        var directory = Path.GetFullPath(project);
        if (!Directory.Exists(directory))
            return new YunfanRemoteCommandResult("interrupted", "客户端未找到本地项目");

        await AddImportedProjectJobsAsync(
            new[] { directory },
            account.Id,
            account.Name,
            account.ProfileDir,
            platform,
            ResolveGlobalConfigPath(account, platform)).ConfigureAwait(true);
        UncheckKuaishouJobsExcept(account.Id, platform, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { directory });
        await PersistAsync(cancellationToken).ConfigureAwait(true);
        RefreshVisibleJobs();
        var saved = KsSettleMarker.Read(directory);
        var steps = saved?.Steps is { Length: > 0 } ? saved.Steps : DefaultKsSettleDispatchSteps;
        KsSettleMarker.WriteActive(directory, item.Id, string.IsNullOrWhiteSpace(item.SeriesId) ? item.Title : item.SeriesId, steps);
        KuaishouPipelineRequest request;
        try
        {
            request = CreateDispatchPipelineRequest(account, platform, steps);
        }
        catch (Exception ex)
        {
            return new YunfanRemoteCommandResult("failed", "快手分账配置读取失败：" + ex.Message);
        }

        var command = new KsSettleCommand(
            "ks_settlement_upload_series",
            item.Id,
            $"/client-api/kuaishou/settlement/auto-publish/assignments/{item.Id}/progress",
            account.Id,
            edition,
            string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileName(directory) : item.Title,
            steps);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_kuaishouRunGate)
        {
            if (_kuaishouAccountRuns.ContainsKey(key))
            {
                cts.Dispose();
                AppendActivityLog($"快手分账派单 {item.Id} 的本地项目还在，当前队列仍在执行，稍后再继续。");
                return new YunfanRemoteCommandResult("skipped", "");
            }
            _kuaishouAccountRuns[key] = cts;
        }
        NotifyCommands();
        AppendActivityLog($"快手分账派单从本地项目继续，不重新导入：{directory}");
        _ = RunDispatchPipelineAsync(
            account,
            key,
            cts,
            new[] { directory },
            request,
            command,
            (status, step, detail, failure) => reportAsync(item.Id, status, step, string.IsNullOrWhiteSpace(detail) ? failure : detail));
        return new YunfanRemoteCommandResult("accepted", $"已从本地项目继续「{command.Title}」");
    }

    private KuaishouPipelineRequest CreateDispatchPipelineRequest(
        GlobalAccount account,
        PublishPlatform platform,
        IReadOnlyList<string> steps)
    {
        var selected = new HashSet<string>(steps.Select(step => step.Trim()), StringComparer.OrdinalIgnoreCase);
        bool Has(string step) => selected.Contains(step);
        var configPath = ResolveGlobalConfigPath(account, platform);
        var config = KuaishouPersonalConfig.Load(new PublishJob
        {
            Platform = platform,
            AccountId = account.Id,
            ConfigPath = configPath,
        });
        return new KuaishouPipelineRequest(
            platform,
            account.Id,
            account.Name,
            configPath,
            Has("download"),
            Has("rewrite_info"),
            Has("generate_poster"),
            Has("generate_project_images"),
            Has("auto_fill_info"),
            Has("upload_series"),
            false,
            KuaishouProjectQueue.NormalizeParallelism(config.QueueMaxParallelProjects),
            platform == PublishPlatform.KuaishouEnterpriseRevenue ? "企业版" : "个人版",
            Has("auto_archive"));
    }

    private int UncheckKuaishouJobsExcept(string accountId, PublishPlatform platform, IReadOnlySet<string> keepDirs)
    {
        var cleared = 0;
        foreach (var job in _jobs)
        {
            if (job.Platform != platform || !string.Equals(job.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                continue;
            var dir = string.IsNullOrWhiteSpace(job.ProjectDirectory) ? "" : Path.GetFullPath(job.ProjectDirectory);
            var keep = dir.Length > 0 && keepDirs.Contains(dir);
            if (job.IsChecked == keep)
                continue;
            job.IsChecked = keep;
            if (!keep)
                cleared++;
        }
        return cleared;
    }

    private (string? Directory, GlobalAccount? Account) FindMarkedProject(long assignmentId)
    {
        if (assignmentId <= 0)
            return (null, null);
        foreach (var account in _globalAccounts)
        {
            if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
                continue;
            var workspace = ResolveKuaishouWorkRoot(account, platform);
            if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
                continue;
            foreach (var dir in Directory.EnumerateDirectories(workspace))
            {
                var marker = KsSettleMarker.Read(dir);
                if (marker?.AssignmentId == assignmentId)
                    return (dir, account);
            }
        }
        return (null, null);
    }
}

internal sealed class KsSettleMarker
{
    public long AssignmentId { get; set; }
    public string SeriesId { get; set; } = "";
    public string FinalStatus { get; set; } = "";
    public string[] Steps { get; set; } = [];
    public bool IsActive => AssignmentId > 0 && string.IsNullOrWhiteSpace(FinalStatus);

    public static KsSettleMarker? Read(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, KsSettleAutoPublishRecovery.MarkerFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<KsSettleMarker>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void WriteActive(string projectDirectory, long assignmentId, string seriesId, IReadOnlyList<string>? steps = null)
    {
        if (assignmentId <= 0)
            return;
        var current = Read(projectDirectory) ?? new KsSettleMarker();
        current.AssignmentId = assignmentId;
        current.SeriesId = seriesId;
        current.FinalStatus = "";
        if (steps is { Count: > 0 })
            current.Steps = steps.Where(step => !string.IsNullOrWhiteSpace(step)).Select(step => step.Trim()).ToArray();
        Write(projectDirectory, current);
    }

    public static void WriteFinal(string projectDirectory, long assignmentId, string finalStatus)
    {
        if (assignmentId <= 0)
            return;
        var current = Read(projectDirectory) ?? new KsSettleMarker { AssignmentId = assignmentId };
        current.AssignmentId = assignmentId;
        current.FinalStatus = finalStatus;
        Write(projectDirectory, current);
    }

    private static void Write(string projectDirectory, KsSettleMarker marker)
    {
        var path = Path.Combine(projectDirectory, KsSettleAutoPublishRecovery.MarkerFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(marker));
    }
}
