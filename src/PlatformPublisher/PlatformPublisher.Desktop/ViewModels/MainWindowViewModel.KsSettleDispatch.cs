using System.Text.Json;
using Avalonia.Threading;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Publishing;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Ui.Views;
using GlobalAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly KsSettleRecoverySession _ksRecoverySession = KsSettleRecoverySession.ForCurrentProcess();
    private bool _ksRestartResumeChecked;
    private bool _ksRestartResumeConfirmed;

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
        bool RemoteDispatchEnabled,
        bool QueueRunning = false);

    public sealed record KsSettleCommand(
        string Command,
        long AssignmentId,
        string ProgressPath,
        string AccountProfileId,
        string Edition,
        string Title,
        IReadOnlyList<string> EnabledSteps,
        bool? ForceRerun = null,
        int ExpectedEpisodes = 0);

    public sealed record YunfanRemoteCommandResult(string Status, string Summary, string FailureKind = "");

    public sealed record KsSettleOpenAssignment(long Id, string Status, string SeriesId, string Title, string AccountProfileId, string Edition, int RecoveryCount = 0);

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
                account.ParticipatesInXingeRemoteDispatch,
                IsKuaishouAccountRunning(KuaishouRunKey(account.Id, platform))));
        }
        return accounts;
    }

    internal void NoteKsSettleAssignmentReceived(long assignmentId) => _ksRecoverySession.TryRecord(assignmentId);

    public Task<YunfanRemoteCommandResult> ExecuteKsSettlementCommandAsync(
        KsSettleCommand command,
        Func<string, string, string, string, Task> reportAsync,
        CancellationToken cancellationToken)
    {
        NoteKsSettleAssignmentReceived(command.AssignmentId);
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
        CancellationToken cancellationToken,
        Func<string, bool>? isQuotaFull = null)
    {
        if (!_ksRecoverySession.CanRecoverAt(DateTimeOffset.Now))
            return;

        var resume = new List<(KsSettleOpenAssignment Item, GlobalAccount Account, string Directory)>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var located = FindMarkedProject(item.Id);
            var marker = located.Directory is null ? null : KsSettleMarker.Read(located.Directory);
            // 同一进程内的手动重跑也会完成旧派单，账号满额时仍需释放旧占位。
            if (string.Equals(marker?.FinalStatus, "success", StringComparison.OrdinalIgnoreCase) ||
                (located.Directory is null && HasCompletedArchivedAssignment(item)))
            {
                await reportAsync(item.Id, "success", "upload_series", "已核对到本地完成记录").ConfigureAwait(false);
                continue;
            }
            if (_ksRecoverySession.HasSeen(item.Id) || isQuotaFull?.Invoke(item.AccountProfileId) == true)
                continue;
            var action = KsSettleAutoPublishRecovery.Decide(
                item.Status,
                marker?.IsActive == true,
                marker?.FinalStatus,
                located.Directory is not null && Directory.Exists(located.Directory),
                item.RecoveryCount);
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
                case KsSettleAutoPublishRecovery.Action.ReportFailed:
                    await reportAsync(item.Id, "failed", "", "上次执行已经失败，不再自动重试").ConfigureAwait(false);
                    break;
                case KsSettleAutoPublishRecovery.Action.Resume when located.Directory is not null && located.Account is not null:
                    if (!IsKsRecoveryProjectBusy(located.Account, located.Directory))
                        resume.Add((item, located.Account, located.Directory));
                    break;
            }
        }

        resume.RemoveAll(entry => _ksRecoverySession.HasSeen(entry.Item.Id) ||
            IsKsRecoveryProjectBusy(entry.Account, entry.Directory));
        if (resume.Count == 0 || !await ConfirmKsRestartResumeAsync(resume, cancellationToken).ConfigureAwait(false))
            return;

        foreach (var entry in resume)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResumeMarkedProjectAsync(entry.Item, entry.Account, entry.Directory, reportAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsKsRecoveryProjectBusy(GlobalAccount account, string directory)
    {
        if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
            return false;
        var key = KuaishouRunKey(account.Id, platform);
        var fullPath = Path.GetFullPath(directory);
        return IsManualKuaishouRun(key) || LaneDirectories(key).Contains(fullPath, StringComparer.OrdinalIgnoreCase);
    }

    // 本次启动只问一次。倒计时结束或点立即执行才续跑，暂不执行则这次启动不再自动续跑。
    private async Task<bool> ConfirmKsRestartResumeAsync(
        IReadOnlyList<(KsSettleOpenAssignment Item, GlobalAccount Account, string Directory)> resume,
        CancellationToken cancellationToken)
    {
        if (_ksRestartResumeChecked)
            return _ksRestartResumeConfirmed;
        _ksRestartResumeChecked = true;
        var session = new DispatchConfirmSession(DateTimeOffset.Now, DispatchConfirmSession.DefaultCountdownSeconds);
        foreach (var entry in resume)
        {
            session.TryAdd(new DispatchConfirmItem
            {
                MessageId = entry.Item.Id,
                AssignmentId = entry.Item.Id,
                AccountName = entry.Account.Name,
                Edition = entry.Item.Edition,
                Title = entry.Item.Title,
                ConfirmSeconds = DispatchConfirmSession.DefaultCountdownSeconds,
            });
        }

        LogKsSettleDispatch($"客户端重启后将继续执行 {resume.Count} 部下发剧集，{DispatchConfirmSession.DefaultCountdownSeconds} 秒后自动开始。");
        _ksRestartResumeConfirmed = await XingeRestartResumePrompt.ConfirmAsync(session).WaitAsync(cancellationToken).ConfigureAwait(false);
        LogKsSettleDispatch(_ksRestartResumeConfirmed
            ? "已确认继续执行重启前下发的剧集。"
            : "已关闭继续执行提示，本次启动不再自动执行已下发剧集。");
        return _ksRestartResumeConfirmed;
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
        if (IsKuaishouDailyUploadLimited(account.Id))
            return new YunfanRemoteCommandResult("failed", KuaishouDailyUploadLimit.Detail, KuaishouDailyUploadLimit.FailureKind);
        if (!account.ParticipatesInXingeRemoteDispatch)
            return new YunfanRemoteCommandResult("failed", "账号未启用远程分发", "account_remote_disabled");
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
        if (command.AssignmentId <= 0 && IsKuaishouAccountRunning(key))
            return new YunfanRemoteCommandResult("failed", "当前已有快手分账队列在执行，请等待完成后再发起新任务。", "queue_busy");
        while (command.AssignmentId > 0 && IsManualKuaishouRun(key))
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(true);

        var workspace = ResolveKuaishouWorkRoot(account, commandPlatform);
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            return new YunfanRemoteCommandResult("failed", "快手分账工作目录不存在");
        if (string.IsNullOrWhiteSpace(command.Title))
            return new YunfanRemoteCommandResult("failed", "命令没有剧名");
        if (command.AssignmentId > 0)
        {
            var located = FindMarkedProject(command.AssignmentId);
            if (!string.IsNullOrWhiteSpace(located.Directory) && Directory.Exists(located.Directory))
                return EnqueueExistingKsSettleProject(account, commandPlatform, key, located.Directory, command, reportAsync);
        }

        if (command.AssignmentId > 0 && (command.ExpectedEpisodes <= 0 ||
            string.IsNullOrWhiteSpace(command.Title) || command.Title.Trim().All(char.IsDigit)))
            return new YunfanRemoteCommandResult("failed", "派单缺少真实原剧名或预期总集数，请更新或重新下发派单。");

        DramaTitleImportOutcome outcome;
        try
        {
            outcome = await _dramaTitleImportService.ImportAsync(workspace, command.Title, AppendActivityLog, cancellationToken, command.ExpectedEpisodes)
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
        var keep = new HashSet<string>(dirs, StringComparer.OrdinalIgnoreCase);
        if (command.AssignmentId > 0)
        {
            foreach (var dir in LaneDirectories(key))
                keep.Add(dir);
        }
        await AddImportedProjectJobsAsync(
            dirs,
            account.Id,
            account.Name,
            account.ProfileDir,
            commandPlatform,
            ResolveGlobalConfigPath(account, commandPlatform)).ConfigureAwait(true);
        var cleared = UncheckKuaishouJobsExcept(account.Id, commandPlatform, keep);
        if (cleared > 0)
            AppendActivityLog($"分发前已取消勾选 {cleared} 个手动选择的项目。");
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
            request = CreateDispatchPipelineRequest(account, commandPlatform, steps, command.AssignmentId > 0 ? command.ForceRerun : null, command.AssignmentId > 0);
        }
        catch (Exception ex)
        {
            return new YunfanRemoteCommandResult("failed", "快手分账配置读取失败：" + ex.Message);
        }

        if (command.AssignmentId <= 0)
        {
            CancellationTokenSource cts;
            while (true)
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var started = false;
                lock (_kuaishouRunGate)
                {
                    if (!_kuaishouAccountRuns.ContainsKey(key))
                    {
                        _kuaishouAccountRuns[key] = cts;
                        started = true;
                    }
                }
                if (started)
                    break;
                cts.Dispose();
                return new YunfanRemoteCommandResult("failed", "当前已有快手分账队列在执行，请等待完成后再发起新任务。", "queue_busy");
            }
            NotifyCommands();
            _ = RunDispatchPipelineAsync(account, key, cts, dirs.ToArray(), request, command, reportAsync);
            return new YunfanRemoteCommandResult("accepted", $"已开始执行「{command.Title}」");
        }

        EnqueueKsSettleLane(account, key, dirs.ToArray(), request, command, reportAsync);
        AppendActivityLog($"快手分账{request.EditionLabel}已加入执行队列「{command.Title}」，按任务队列并发数同时执行 {request.Parallelism} 部。");
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
        // 派单是从界面线程发起的。先离开界面线程，下载和上传才不会把窗口卡死。
        await Task.Delay(1).ConfigureAwait(false);
        if (command.AssignmentId > 0)
            await reportAsync("running", "download", $"开始执行「{command.Title}」", "").ConfigureAwait(false);
        try
        {
            PrepareKuaishouLogProjects(targets, key);
            var (succeeded, failed, error) = await RunKuaishouCheckedProjectsAsync(targets, key, request, cts.Token)
                .ConfigureAwait(false);
            if (KuaishouDailyUploadLimit.Matches(error))
            {
                foreach (var dir in targets)
                    KsSettleMarker.WriteFinal(dir, command.AssignmentId, "failed");
                await reportAsync("failed", "", KuaishouDailyUploadLimit.Detail, KuaishouDailyUploadLimit.FailureKind).ConfigureAwait(false);
                return;
            }
            if (failed > 0 || succeeded == 0)
            {
                foreach (var dir in targets)
                    KsSettleMarker.WriteFinal(dir, command.AssignmentId, "failed");
                await reportAsync("failed", "", ShortDispatchFailure(error), KsSettleAutoPublishRecovery.FailureKindForPipeline(error)).ConfigureAwait(false);
                return;
            }
            foreach (var dir in targets)
                KsSettleMarker.WriteFinal(dir, command.AssignmentId, "success");
            await reportAsync("success", "upload_series", $"上传完成：{command.Title}", "").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var dir in targets)
                KsSettleMarker.WriteFinal(dir, command.AssignmentId, "failed");
            await reportAsync("failed", "", ShortDispatchFailure(ex.Message), KsSettleAutoPublishRecovery.FailureKindForPipeline(ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            lock (_kuaishouRunGate)
            {
                if (_kuaishouAccountRuns.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                    _kuaishouAccountRuns.Remove(key);
            }
            cts.Dispose();
            PostUi(NotifyCommands);
            NotifyKuaishouLogProjectsChanged();
        }
    }

    private static string ShortDispatchFailure(string? error)
    {
        var text = (error ?? "").Trim();
        if (text.Length == 0)
            return "快手分账执行失败";
        var line = text.Split('；', ';')[0].Trim();
        return line.Length <= 180 ? line : line[..180];
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
        if (_ksRecoverySession.HasSeen(item.Id) || IsKsRecoveryProjectBusy(account, project))
            return new YunfanRemoteCommandResult("skipped", "");

        if (!KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
            return new YunfanRemoteCommandResult("failed", "账号还没有选择快手分账类型");
        var edition = platform == PublishPlatform.KuaishouEnterpriseRevenue ? "enterprise" : "personal";
        var wanted = string.Equals(item.Edition, "enterprise", StringComparison.OrdinalIgnoreCase) ? "enterprise" : "personal";
        if (edition != wanted)
            return new YunfanRemoteCommandResult("failed", "本地项目所在账号版本与派单不一致");

        var key = KuaishouRunKey(account.Id, platform);
        if (IsManualKuaishouRun(key))
        {
            AppendActivityLog($"快手分账派单 {item.Id} 的本地项目还在，当前队列仍在执行，稍后再继续。");
            return new YunfanRemoteCommandResult("skipped", "");
        }

        var directory = Path.GetFullPath(project);
        if (!Directory.Exists(directory))
            return new YunfanRemoteCommandResult("interrupted", "客户端未找到本地项目");

        var savedMarker = KsSettleMarker.Read(directory);
        if (savedMarker is not { IsActive: true } || savedMarker.AssignmentId != item.Id ||
            !_ksRecoverySession.TryRecord(item.Id))
            return new YunfanRemoteCommandResult("skipped", "");

        await AddImportedProjectJobsAsync(
            new[] { directory },
            account.Id,
            account.Name,
            account.ProfileDir,
            platform,
            ResolveGlobalConfigPath(account, platform)).ConfigureAwait(true);
        var keep = new HashSet<string>(LaneDirectories(key), StringComparer.OrdinalIgnoreCase) { directory };
        UncheckKuaishouJobsExcept(account.Id, platform, keep);
        await PersistAsync(cancellationToken).ConfigureAwait(true);
        RefreshVisibleJobs();
        var saved = KsSettleMarker.Read(directory);
        var steps = saved?.Steps is { Length: > 0 } ? saved.Steps : DefaultKsSettleDispatchSteps;
        KsSettleMarker.WriteActive(directory, item.Id, string.IsNullOrWhiteSpace(item.SeriesId) ? item.Title : item.SeriesId, steps);
        KuaishouPipelineRequest request;
        try
        {
            request = CreateDispatchPipelineRequest(account, platform, steps, null, true);
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
        AppendActivityLog($"快手分账派单从本地项目继续，不重新导入：{directory}");
        EnqueueKsSettleLane(
            account,
            key,
            new[] { directory },
            request,
            command,
            (status, step, detail, failure) => reportAsync(item.Id, status, step, string.IsNullOrWhiteSpace(detail) ? failure : detail));
        return new YunfanRemoteCommandResult("accepted", $"已从本地项目继续「{command.Title}」");
    }

    private readonly object _ksSettleLaneGate = new();
    private readonly Dictionary<string, KsSettleAccountLane> _ksSettleLanes = new(StringComparer.OrdinalIgnoreCase);

    private sealed class KsSettleAccountLane
    {
        public CancellationTokenSource Cts { get; } = new();
        public KuaishouDispatchLane<KsSettleLaneWork> Scheduler { get; } = new();
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Parallelism { get; set; } = 1;
    }

    private sealed class KsSettleLaneWork
    {
        public required GlobalAccount Account;
        public required string Key;
        public required IReadOnlyList<string> Directories;
        public required KuaishouPipelineRequest Request;
        public required KsSettleCommand Command;
        public required Func<string, string, string, string, Task> ReportAsync;
    }

    private bool IsManualKuaishouRun(string key)
    {
        lock (_ksSettleLaneGate)
        {
            if (_ksSettleLanes.ContainsKey(key))
                return false;
        }
        return IsKuaishouAccountRunning(key);
    }

    private IReadOnlyCollection<string> LaneDirectories(string key)
    {
        lock (_ksSettleLaneGate)
        {
            return _ksSettleLanes.TryGetValue(key, out var lane)
                ? lane.Directories.ToArray()
                : [];
        }
    }

    private YunfanRemoteCommandResult EnqueueExistingKsSettleProject(
        GlobalAccount account,
        PublishPlatform platform,
        string key,
        string directory,
        KsSettleCommand command,
        Func<string, string, string, string, Task> reportAsync)
    {
        var marker = KsSettleMarker.Read(directory);
        if (string.Equals(marker?.FinalStatus, "success", StringComparison.OrdinalIgnoreCase))
            return new YunfanRemoteCommandResult("success", "本地上传已经完成");
        var steps = command.EnabledSteps.Count == 0 ? DefaultKsSettleDispatchSteps : command.EnabledSteps;
        KsSettleMarker.WriteActive(directory, command.AssignmentId, string.IsNullOrWhiteSpace(command.Title) ? directory : command.Title, steps);
        KuaishouPipelineRequest request;
        try
        {
            request = CreateDispatchPipelineRequest(account, platform, steps, command.ForceRerun, true);
        }
        catch (Exception ex)
        {
            return new YunfanRemoteCommandResult("failed", "快手分账配置读取失败：" + ex.Message);
        }

        EnqueueKsSettleLane(account, key, new[] { directory }, request, command, reportAsync);
        AppendActivityLog($"快手分账重新执行勾选队列，不重新导入：{directory}");
        return new YunfanRemoteCommandResult("accepted", $"已开始执行「{command.Title}」");
    }

    private void EnqueueKsSettleLane(
        GlobalAccount account,
        string key,
        IReadOnlyList<string> directories,
        KuaishouPipelineRequest request,
        KsSettleCommand command,
        Func<string, string, string, string, Task> reportAsync)
    {
        var work = new KsSettleLaneWork
        {
            Account = account,
            Key = key,
            Directories = directories,
            Request = request,
            Command = command,
            ReportAsync = reportAsync,
        };
        IReadOnlyList<KsSettleLaneWork> started;
        lock (_ksSettleLaneGate)
        {
            if (!_ksSettleLanes.TryGetValue(key, out var lane) || lane.Cts.IsCancellationRequested)
            {
                lane = new KsSettleAccountLane { Parallelism = request.Parallelism };
                _ksSettleLanes[key] = lane;
                lock (_kuaishouRunGate)
                    _kuaishouAccountRuns[key] = lane.Cts;
                NotifyCommands();
            }
            else
            {
                lane.Parallelism = request.Parallelism;
            }
            foreach (var dir in directories)
                lane.Directories.Add(dir);
            started = lane.Scheduler.Enqueue(work, lane.Parallelism);
        }
        foreach (var item in started)
            _ = RunKsSettleLaneItemAsync(item);
    }

    private async Task RunKsSettleLaneItemAsync(KsSettleLaneWork work)
    {
        KsSettleAccountLane? lane;
        lock (_ksSettleLaneGate)
            _ksSettleLanes.TryGetValue(work.Key, out lane);
        if (lane is null)
            return;
        await Task.Delay(1).ConfigureAwait(false);
        var token = lane.Cts.Token;
        try
        {
            var remaining = work.Directories.Count;
            AppendKuaishouLogProjects(work.Directories, work.Key);
            foreach (var directory in work.Directories)
            {
                token.ThrowIfCancellationRequested();
                using var projectSlot = await KuaishouMachineGate.Shared.AcquireProjectAsync(
                    work.Account.Id,
                    KuaishouAccountOrder(work.Account.Id),
                    token,
                    () => AppendActivityLog($"等待全局剧集名额：{work.Command.Title}"));
                await work.ReportAsync("running", "download", $"开始执行「{work.Command.Title}」", "").ConfigureAwait(false);
                var trackedJob = _jobs.FirstOrDefault(job =>
                    job.Platform == work.Request.Platform &&
                    string.Equals(job.AccountId, work.Request.AccountId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(job.ProjectDirectory) &&
                    string.Equals(Path.GetFullPath(job.ProjectDirectory), directory, StringComparison.OrdinalIgnoreCase));
                if (trackedJob is not null)
                    EnsureStepStates(trackedJob);
                var projectName = string.IsNullOrWhiteSpace(trackedJob?.ProjectName)
                    ? Path.GetFileName(directory)
                    : trackedJob!.ProjectName;
                ActivityLogProjectScope.Value = projectName;
                ActivityLogAccountScope.Value = work.Key;
                ActivityLogPlatformScope.Value = work.Request.Platform;
                try
                {
                    await RunKuaishouSettlementPipelineAsync(
                        directory,
                        trackedJob,
                        trackedJob is null ? null : RowForJob(trackedJob),
                        work.Request,
                        token,
                        beforeArchive: async () =>
                        {
                            KsSettleMarker.WriteFinal(directory, work.Command.AssignmentId, "success");
                            if (--remaining == 0)
                            {
                                var detail = work.Request.Upload
                                    ? $"上传流程完成：{work.Command.Title}"
                                    : $"所选步骤已完成：{work.Command.Title}";
                                await work.ReportAsync("success", work.Request.Upload ? "upload_series" : "", detail, "").ConfigureAwait(false);
                            }
                        }).ConfigureAwait(false);
                    SetKuaishouLogTone(directory, "ok");
                    RememberKuaishouUploadReport(directory, work.Request, succeeded: true, error: "");
                }
                finally
                {
                    ActivityLogProjectScope.Value = null;
                    ActivityLogAccountScope.Value = null;
                    ActivityLogPlatformScope.Value = null;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsKuaishouDailyUploadLimited(work.Account.Id) && work.Command.AssignmentId > 0)
            {
                foreach (var dir in work.Directories)
                {
                    SetKuaishouLogTone(dir, "failed");
                    KsSettleMarker.WriteFinal(dir, work.Command.AssignmentId, "failed");
                }
                await work.ReportAsync("failed", "", KuaishouDailyUploadLimit.Detail, KuaishouDailyUploadLimit.FailureKind).ConfigureAwait(false);
            }
            else
            {
                foreach (var dir in work.Directories)
                    SetKuaishouLogTone(dir, "none");
            }
        }
        catch (Exception ex) when (KuaishouDailyUploadLimit.Is(ex))
        {
            foreach (var dir in work.Directories)
                SetKuaishouLogTone(dir, "failed");
            NoteKuaishouDailyUploadLimit(work.Account.Id, work.Account.Name);
            try
            {
                lane.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            foreach (var dir in work.Directories)
                KsSettleMarker.WriteFinal(dir, work.Command.AssignmentId, "failed");
            await work.ReportAsync("failed", "", KuaishouDailyUploadLimit.Detail, KuaishouDailyUploadLimit.FailureKind).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsKuaishouLoginFailure(ex))
        {
            foreach (var dir in work.Directories)
            {
                SetKuaishouLogTone(dir, "failed");
                KsSettleMarker.WriteFinal(dir, work.Command.AssignmentId, "failed");
            }

            try
            {
                lane.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            await work.ReportAsync("failed", "", ex.Message, KsSettleAutoPublishRecovery.FailureKindForPipeline(ex.Message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var dir in work.Directories)
            {
                SetKuaishouLogTone(dir, "failed");
                KsSettleMarker.WriteFinal(dir, work.Command.AssignmentId, "failed");
            }
            await work.ReportAsync("failed", "", ShortDispatchFailure(ex.Message), KsSettleAutoPublishRecovery.FailureKindForPipeline(ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            ReleaseKsSettleLaneItem(work.Key, lane, token.IsCancellationRequested);
        }
    }

    private void ReleaseKsSettleLaneItem(string key, KsSettleAccountLane lane, bool stopStarting)
    {
        IReadOnlyList<KsSettleLaneWork> next;
        var idle = false;
        lock (_ksSettleLaneGate)
        {
            if (!_ksSettleLanes.TryGetValue(key, out var current) || !ReferenceEquals(current, lane))
                return;
            next = lane.Scheduler.Completed(lane.Parallelism, stopStarting);
            idle = lane.Scheduler.Idle;
            if (idle)
                _ksSettleLanes.Remove(key);
        }
        if (idle)
        {
            lock (_kuaishouRunGate)
            {
                if (_kuaishouAccountRuns.TryGetValue(key, out var cts) && ReferenceEquals(cts, lane.Cts))
                    _kuaishouAccountRuns.Remove(key);
            }
            lane.Cts.Dispose();
            PostUi(NotifyCommands);
        }
        foreach (var item in next)
            _ = RunKsSettleLaneItemAsync(item);
    }

    private KuaishouPipelineRequest CreateDispatchPipelineRequest(
        GlobalAccount account,
        PublishPlatform platform,
        IReadOnlyList<string> steps,
        bool? forceRerun,
        bool applyVisible)
    {
        if (applyVisible)
            ApplyVisibleKuaishouSteps(steps, forceRerun);
        var selected = new HashSet<string>(steps.Select(step => step.Trim()), StringComparer.OrdinalIgnoreCase);
        bool Has(string step) => applyVisible
            ? step switch
            {
                "download" => PipelineDownloadEnabled,
                "rewrite_info" => PipelineRewriteEnabled,
                "generate_poster" => PipelinePosterEnabled,
                "generate_project_images" => PipelineProjectImageEnabled,
                "auto_fill_info" => PipelineAutoFillEnabled,
                "upload_series" => PipelineUploadEpisodesEnabled,
                "auto_archive" => PipelineAutoArchiveAfterUpload,
                _ => false,
            }
            : selected.Contains(step);
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
            applyVisible ? PipelineForceRerun : false,
            KuaishouProjectQueue.NormalizeParallelism(config.QueueMaxParallelProjects),
            platform == PublishPlatform.KuaishouEnterpriseRevenue ? "企业版" : "个人版",
            Has("auto_archive"),
            GetKuaishouDedupPreset(config, selected.Contains("video_dedup") ||
                (Has("upload_series") && KuaishouVideoDedup.IsEnabled(account.Id))));
    }

    private void ApplyVisibleKuaishouSteps(IReadOnlyList<string> steps, bool? forceRerun)
    {
        var selected = new HashSet<string>(steps.Select(step => step.Trim()), StringComparer.OrdinalIgnoreCase);
        PipelineDownloadEnabled = selected.Contains("download");
        PipelineRewriteEnabled = selected.Contains("rewrite_info");
        PipelinePosterEnabled = selected.Contains("generate_poster");
        PipelineProjectImageEnabled = selected.Contains("generate_project_images");
        PipelineAutoFillEnabled = selected.Contains("auto_fill_info");
        PipelineUploadEpisodesEnabled = selected.Contains("upload_series");
        PipelineAutoArchiveAfterUpload = selected.Contains("auto_archive");
        if (forceRerun is bool force)
            PipelineForceRerun = force;
        var names = new List<string>();
        if (PipelineDownloadEnabled) names.Add("下载");
        if (PipelineRewriteEnabled) names.Add("改写信息");
        if (PipelinePosterEnabled) names.Add("生成海报");
        if (PipelineProjectImageEnabled) names.Add("生成工程图");
        if (PipelineAutoFillEnabled) names.Add("补齐字段");
        if (PipelineUploadEpisodesEnabled) names.Add("上传剧集");
        if (PipelineForceRerun) names.Add("强制重跑已完成步骤");
        if (PipelineAutoArchiveAfterUpload) names.Add("上传后自动归档");
        AppendActivityLog("已按分发配置勾选步骤：" + (names.Count == 0 ? "（无）" : string.Join("、", names)));
    }

    private int UncheckKuaishouJobsExcept(string accountId, PublishPlatform platform, IReadOnlySet<string> keepDirs)
    {
        var cleared = 0;
        var now = DateTimeOffset.Now;
        foreach (var job in _jobs)
        {
            if (job.Platform != platform || !string.Equals(job.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                continue;
            var dir = string.IsNullOrWhiteSpace(job.ProjectDirectory) ? "" : Path.GetFullPath(job.ProjectDirectory);
            // 今日分发和已经执行的剧保持勾选。更早、还没执行的手动勾选才清掉。
            var keep = (dir.Length > 0 && keepDirs.Contains(dir))
                || KuaishouDispatchSelection.ShouldStayChecked(job, job.IsChecked, now);
            if (_jobRowCache.TryGetValue(job.Id, out var row))
            {
                if (row.IsChecked == keep)
                {
                    job.IsChecked = keep;
                    continue;
                }

                row.IsChecked = keep;
            }
            else if (job.IsChecked == keep)
            {
                continue;
            }
            else
            {
                job.IsChecked = keep;
            }

            if (!keep)
                cleared++;
        }
        return cleared;
    }

    private bool HasCompletedArchivedAssignment(KsSettleOpenAssignment item)
    {
        var account = _globalAccounts.FirstOrDefault(account =>
            string.Equals(account.Id, item.AccountProfileId, StringComparison.OrdinalIgnoreCase));
        if (account is null || !KuaishouAccountEdition.TryGetPlatform(account.KuaishouAccountEdition, out var platform))
            return false;
        var workspace = ResolveKuaishouWorkRoot(account, platform);
        if (string.IsNullOrWhiteSpace(workspace))
            return false;
        try
        {
            var (archiveRoot, _) = ReadKuaishouUploadCountSettings(new PublishJob
            {
                AccountId = account.Id,
                Platform = platform,
                ConfigPath = ResolveGlobalConfigPath(account, platform),
            });
            return TikTokArchivedProjectService.List(workspace, archiveRoot).Any(archive =>
                string.Equals(archive.AccountProfileId, account.Id, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(archive.UploadCompletedAt) &&
                !string.IsNullOrWhiteSpace(archive.ArchivedSourceDir) &&
                KsSettleMarker.Read(archive.ArchivedSourceDir) is { } marker &&
                marker.AssignmentId == item.Id &&
                (marker.FinalStatus == "success" ||
                    KuaishouAutoArchivePolicy.UploadCompleted(false, KuaishouArtifactColumnStatusReader.Read(
                        archive.ArchivedWorkflowDir.Length > 0 ? archive.ArchivedWorkflowDir : archive.ArchivedSourceDir,
                        platform == PublishPlatform.KuaishouEnterpriseRevenue).UploadStatus)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogKsSettleDispatch($"核对快手派单 {item.Id} 的归档结果失败：{ex.Message}");
            return false;
        }
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
