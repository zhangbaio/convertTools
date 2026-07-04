using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed class QueueWorkerProgress
{
    public required string WorkspaceRoot { get; init; }
    public QueueProjectItem? Item { get; init; }
    public string Message { get; init; } = "";
    public string? StepKey { get; init; }
}

public sealed class QueueWorkerSummary
{
    public int TotalCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public bool Stopped { get; init; }
}

/// <summary>由 UI 层提供内置浏览器 CDP 与剧集发布能力。</summary>
public interface IQueuePublishHost
{
    Task<QueueBrowserReadyResult> EnsureAccountBrowserReadyAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct);
    Task<PublishResult> PublishProjectAsync(
        TikTokAccountProfile account,
        QueueProjectItem project,
        FinalAction finalAction,
        QueueRunOptions options,
        Action<string> log,
        CancellationToken ct);
}

/// <summary>工作目录队列 Worker（预处理步骤 + <c>upload_series</c>，支持项目级并行）。</summary>
public sealed class QueueWorkerRunner
{
    private const int ProjectConcurrencyHardMax = 20;

    private readonly UploadSlotCoordinator _uploadSlots;
    private readonly List<QueueProjectItem> _incomingItems = new();
    private readonly object _incomingLock = new();
    public ManualInterventionCoordinator ManualIntervention { get; } = new();

    public QueueWorkerRunner(UploadSlotCoordinator? sharedUploadSlots = null) =>
        _uploadSlots = sharedUploadSlots ?? new UploadSlotCoordinator();

    public int AddItems(IEnumerable<QueueProjectItem> items)
    {
        var added = items
            .Where(item => item.Enabled)
            .Select(CloneQueueItem)
            .ToList();
        if (added.Count == 0)
            return 0;

        lock (_incomingLock)
            _incomingItems.AddRange(added);
        return added.Count;
    }

    private bool HasIncomingItems()
    {
        lock (_incomingLock)
            return _incomingItems.Count > 0;
    }

    private static Dictionary<string, int>? BuildProjectDirOrder(IReadOnlyCollection<string>? projectDirFilter)
    {
        if (projectDirFilter is null || projectDirFilter.Count == 0)
            return null;

        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in projectDirFilter)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var fullPath = Path.GetFullPath(path);
            if (!order.ContainsKey(fullPath))
                order[fullPath] = order.Count;
        }

        return order.Count == 0 ? null : order;
    }

    public async Task<QueueWorkerSummary> RunAsync(
        string workspaceRoot,
        IList<QueueProjectItem> items,
        QueueRunOptions options,
        IQueuePublishHost host,
        AccountStore accountStore,
        FinalAction finalAction,
        Action<QueueWorkerProgress>? onProgress,
        Action<IReadOnlyList<QueueProjectItem>>? onPersist,
        CancellationToken ct,
        IReadOnlyCollection<string>? projectDirFilter = null)
    {
        var workspace = Path.GetFullPath(workspaceRoot);
        var filterOrder = BuildProjectDirOrder(projectDirFilter);
        var filter = filterOrder?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateQuery = items
            .Where(i => i.Enabled && !i.Archived)
            .Where(i => filter is null || filter.Contains(Path.GetFullPath(i.ProjectDir)));
        var candidates = filterOrder is not null
            ? candidateQuery
                .OrderBy(i => filterOrder.GetValueOrDefault(Path.GetFullPath(i.ProjectDir), int.MaxValue))
                .ThenBy(i => i.ProjectDir, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : candidateQuery
                .OrderBy(i => string.IsNullOrWhiteSpace(i.QueuedAt) ? "9999" : i.QueuedAt, StringComparer.Ordinal)
                .ThenBy(i => i.ProjectDir, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var projectConcurrency = Math.Clamp(options.ProjectConcurrency, 1, ProjectConcurrencyHardMax);
        var orderedSteps = options.OrderedEnabledSteps();
        var preUploadSteps = orderedSteps.Where(s => s != QueueStepRegistry.UploadSeries).ToList();
        var uploadEnabled = options.IsStepEnabled(QueueStepRegistry.UploadSeries);

        ManualIntervention.Reset();
        var settings = ClientSettingsStore.Load();
        var manualInterventionAllowed = uploadEnabled
            && settings.TiktokManualInterventionOnSingleFailure;

        var success = 0;
        var failed = 0;
        var stopped = false;
        var stateLock = new object();

        var pendingPreUpload = new Queue<(int Index, QueueProjectItem Item)>(
            candidates.Select((item, index) => (index + 1, item)));
        var readyForUpload = new Queue<QueueProjectItem>();
        var preUploadTasks = new Dictionary<Task, QueueProjectItem>();
        var uploadTasks = new Dictionary<Task<bool>, (QueueProjectItem Item, string AccountKey, TikTokAccountProfile Account)>();
        var activeUploadAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Report(onProgress, workspace, null,
            $"开始执行队列，共 {candidates.Count} 个项目（启用步骤：{string.Join(", ", orderedSteps)}，" +
            $"强制重跑已完成步骤：{(options.ForceRerunCompletedSteps ? "开" : "关")}，项目并发 {projectConcurrency}）");

        void Mutate(Action action)
        {
            lock (stateLock)
            {
                action();
                Persist(workspace, items, onPersist);
            }
        }

        void FillPreUploadSlots()
        {
            lock (stateLock)
            {
                while (!stopped
                       && !ct.IsCancellationRequested
                       && pendingPreUpload.Count > 0
                       && preUploadTasks.Count < projectConcurrency)
                {
                    var (_, item) = pendingPreUpload.Dequeue();
                    if (preUploadSteps.Count > 0)
                    {
                        var captured = item;
                        var task = RunPreUploadPipelineAsync(
                            workspace,
                            captured,
                            preUploadSteps,
                            options,
                            accountStore,
                            onProgress,
                            ct,
                            Mutate);
                        preUploadTasks[task] = captured;
                        continue;
                    }

                    if (uploadEnabled && ShouldRunStep(item, QueueStepRegistry.UploadSeries, options))
                    {
                        MarkWaitingSlot(item);
                        readyForUpload.Enqueue(item);
                    }
                    else
                    {
                        success++;
                        MarkProjectCompleted(item);
                    }

                    Persist(workspace, items, onPersist);
                }
            }
        }

        int StartReadyUploads()
        {
            lock (stateLock)
            {
                if (readyForUpload.Count == 0) return 0;

                var started = 0;
                var rotations = readyForUpload.Count;
                while (readyForUpload.Count > 0 && rotations > 0)
                {
                    var item = readyForUpload.Dequeue();
                    var account = ResolveAccount(accountStore, item);
                    var accountKey = account?.Id ?? "default";

                    if (activeUploadAccounts.Contains(accountKey))
                    {
                        readyForUpload.Enqueue(item);
                        rotations--;
                        continue;
                    }

                    if (!_uploadSlots.TryAcquire(accountKey))
                    {
                        readyForUpload.Enqueue(item);
                        rotations--;
                        continue;
                    }

                    if (account is null)
                    {
                        _uploadSlots.Release(accountKey);
                        MarkFailed(item, QueueStepRegistry.UploadSeries, $"未找到绑定账号：{item.AccountProfileId}");
                        failed++;
                        rotations = readyForUpload.Count;
                        continue;
                    }

                    activeUploadAccounts.Add(accountKey);
                    var capturedAccount = account;
                    var capturedItem = item;
                    var task = RunUploadPipelineAsync(
                        workspace,
                        capturedItem,
                        capturedAccount,
                        host,
                        finalAction,
                        options,
                        onProgress,
                        ct,
                        Mutate,
                        manualInterventionAllowed ? ManualIntervention : null);
                    uploadTasks[task] = (capturedItem, accountKey, capturedAccount);
                    started++;
                    rotations = readyForUpload.Count;
                }

                return started;
            }
        }

        void DrainPendingAsStopped()
        {
            Mutate(() =>
            {
                while (pendingPreUpload.Count > 0)
                {
                    var (_, item) = pendingPreUpload.Dequeue();
                    MarkStopped(item, item.CurrentStep);
                }

                while (readyForUpload.Count > 0)
                {
                    var item = readyForUpload.Dequeue();
                    MarkStopped(item, QueueStepRegistry.UploadSeries);
                }
            });
        }

        FillPreUploadSlots();
        StartReadyUploads();

        while (pendingPreUpload.Count > 0
               || preUploadTasks.Count > 0
               || readyForUpload.Count > 0
               || uploadTasks.Count > 0
               || HasIncomingItems())
        {
            if (ct.IsCancellationRequested && !stopped)
            {
                stopped = true;
                DrainPendingAsStopped();
            }

            if (!stopped)
            {
                DrainIncomingItems(items, candidates, pendingPreUpload, stateLock);
                FillPreUploadSlots();
                StartReadyUploads();
            }
            else if (preUploadTasks.Count == 0 && uploadTasks.Count == 0)
            {
                DrainPendingAsStopped();
                break;
            }

            Task[] watchTasks;
            lock (stateLock)
                watchTasks = preUploadTasks.Keys.Concat(uploadTasks.Keys).ToArray();

            if (watchTasks.Length == 0)
            {
                if (readyForUpload.Count > 0 || pendingPreUpload.Count > 0)
                {
                    try
                    {
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        stopped = true;
                        DrainPendingAsStopped();
                    }

                    continue;
                }

                break;
            }

            Task completed;
            try
            {
                completed = await Task.WhenAny(watchTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
                DrainPendingAsStopped();
                break;
            }

            if (preUploadTasks.Remove(completed, out var preItem))
            {
                try
                {
                    await completed.ConfigureAwait(false);
                    Mutate(() =>
                    {
                        if (stopped)
                        {
                            MarkStopped(preItem, uploadEnabled ? QueueStepRegistry.UploadSeries : preItem.CurrentStep);
                        }
                        else if (uploadEnabled && ShouldRunStep(preItem, QueueStepRegistry.UploadSeries, options))
                        {
                            MarkWaitingSlot(preItem);
                            readyForUpload.Enqueue(preItem);
                        }
                        else
                        {
                            success++;
                            MarkProjectCompleted(preItem);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    Mutate(() => MarkStopped(preItem, preItem.CurrentStep));
                    stopped = true;
                    DrainPendingAsStopped();
                }
                catch (Exception ex)
                {
                    var failedStep = string.IsNullOrWhiteSpace(preItem.CurrentStep)
                        ? preUploadSteps.LastOrDefault() ?? QueueStepRegistry.UploadSeries
                        : preItem.CurrentStep;
                    Mutate(() =>
                    {
                        MarkFailed(preItem, failedStep, ex.Message);
                        failed++;
                    });
                    Report(
                        onProgress,
                        workspace,
                        preItem,
                        $"{QueueStepRegistry.LabelOf(failedStep)} 失败：{ex.Message}",
                        failedStep);
                }

                continue;
            }

            if (completed is Task<bool> uploadTask && uploadTasks.Remove(uploadTask, out var uploadCtx))
            {
                activeUploadAccounts.Remove(uploadCtx.AccountKey);
                _uploadSlots.Release(uploadCtx.AccountKey);

                try
                {
                    var ok = await uploadTask.ConfigureAwait(false);
                    if (ok)
                    {
                        success++;
                        if (options.AutoArchiveAfterUpload)
                        {
                            try
                            {
                                await TikTokArchivedProjectService
                                    .ArchiveQueueProjectAsync(workspace, uploadCtx.Item.ProjectDir, account: uploadCtx.Account, ct: ct)
                                    .ConfigureAwait(false);
                                Mutate(() => uploadCtx.Item.Archived = true);
                                Report(onProgress, workspace, uploadCtx.Item,
                                    "上传完成，已自动归档", QueueStepRegistry.UploadSeries);
                            }
                            catch (Exception ex)
                            {
                                Report(onProgress, workspace, uploadCtx.Item,
                                    $"自动归档失败：{ex.Message}", QueueStepRegistry.UploadSeries);
                            }
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (QueueStopRequestedException ex)
                {
                    failed++;
                    stopped = true;
                    Report(onProgress, workspace, uploadCtx.Item,
                        $"队列已停止：{ex.Message}", QueueStepRegistry.UploadSeries);
                    DrainPendingAsStopped();
                }
                catch (OperationCanceledException)
                {
                    Mutate(() => MarkStopped(uploadCtx.Item, QueueStepRegistry.UploadSeries));
                    stopped = true;
                    DrainPendingAsStopped();
                }
            }
        }

        var summary = new QueueWorkerSummary
        {
            TotalCount = candidates.Count,
            SuccessCount = success,
            FailedCount = failed,
            Stopped = stopped || ct.IsCancellationRequested,
        };
        Report(onProgress, workspace, null,
            summary.Stopped
                ? $"队列已停止：成功 {summary.SuccessCount}，失败 {summary.FailedCount}"
                : $"队列执行结束：成功 {summary.SuccessCount}，失败 {summary.FailedCount}");
        return summary;
    }

    private static async Task RunPreUploadPipelineAsync(
        string workspace,
        QueueProjectItem item,
        IReadOnlyList<string> preUploadSteps,
        QueueRunOptions options,
        AccountStore accountStore,
        Action<QueueWorkerProgress>? onProgress,
        CancellationToken ct,
        Action<Action> mutate)
    {
        foreach (var stepKey in preUploadSteps)
        {
            ct.ThrowIfCancellationRequested();

            var wasCompletedBeforeRun = item.StepStates.GetValueOrDefault(stepKey) == QueueStepStatus.Completed;
            if (!ShouldRunStep(item, stepKey, options))
            {
                Report(onProgress, workspace, item, $"{QueueStepRegistry.LabelOf(stepKey)} 已完成，跳过", stepKey);
                continue;
            }

            if (!QueueStepRegistry.IsImplemented(stepKey))
            {
                Report(onProgress, workspace, item,
                    $"{QueueStepRegistry.LabelOf(stepKey)} 尚未接入 C# 版，跳过", stepKey);
                continue;
            }

            mutate(() => MarkRunning(item, stepKey));
            Report(onProgress, workspace, item, $"开始 {QueueStepRegistry.LabelOf(stepKey)}…", stepKey);

            var stepAccount = ResolveAccount(accountStore, item);
            var useSummaryLog = wasCompletedBeforeRun && options.ForceRerunCompletedSteps;
            Action<string> stepLog = useSummaryLog
                ? QueueStepLogFilters.SummaryOnly(msg => Report(onProgress, workspace, item, msg, stepKey))
                : msg => Report(onProgress, workspace, item, msg, stepKey);

            await RunPreUploadStepAsync(
                item,
                stepKey,
                options,
                stepAccount,
                stepLog,
                ct).ConfigureAwait(false);

            mutate(() => MarkCompleted(item, stepKey));
            Report(onProgress, workspace, item, $"{QueueStepRegistry.LabelOf(stepKey)} 完成", stepKey);
        }
    }

    private static async Task<bool> RunUploadPipelineAsync(
        string workspace,
        QueueProjectItem item,
        TikTokAccountProfile account,
        IQueuePublishHost host,
        FinalAction finalAction,
        QueueRunOptions options,
        Action<QueueWorkerProgress>? onProgress,
        CancellationToken ct,
        Action<Action> mutate,
        ManualInterventionCoordinator? manualIntervention)
    {
        var wasCompletedBeforeRun =
            item.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries) == QueueStepStatus.Completed;
        var useSummaryLog = wasCompletedBeforeRun && options.ForceRerunCompletedSteps;
        Action<string> uploadLog = useSummaryLog
            ? QueueStepLogFilters.SummaryOnly(msg =>
                Report(onProgress, workspace, item, msg, QueueStepRegistry.UploadSeries))
            : msg => Report(onProgress, workspace, item, msg, QueueStepRegistry.UploadSeries);

        mutate(() => MarkRunning(item, QueueStepRegistry.UploadSeries));
        Report(onProgress, workspace, item, $"[{account.DisplayName}] 准备内置浏览器…", QueueStepRegistry.UploadSeries);

        Exception? failure = null;
        var failureMessage = "";
        var stopQueue = false;
        try
        {
            Action<string> browserLog = msg =>
                Report(onProgress, workspace, item, msg, QueueStepRegistry.UploadSeries);
            var browserReady = await host.EnsureAccountBrowserReadyAsync(account, browserLog, ct).ConfigureAwait(false);
            if (!browserReady.Ok)
            {
                failureMessage = browserReady.Message;
                Report(onProgress, workspace, item, $"上传失败：{failureMessage}", QueueStepRegistry.UploadSeries);
            }
            else
            {
                Report(onProgress, workspace, item, "开始上传发布…", QueueStepRegistry.UploadSeries);
                var result = await host.PublishProjectAsync(
                    account,
                    item,
                    finalAction,
                    options,
                    uploadLog,
                    ct).ConfigureAwait(false);

                if (result.Ok)
                {
                    mutate(() =>
                    {
                        MarkCompleted(item, QueueStepRegistry.UploadSeries);
                        item.UploadCompletedAt = DateTimeOffset.Now.ToString("o");
                        item.AccountProfileId = account.Id;
                        item.AccountProfileName = account.DisplayName;
                    });
                    Report(onProgress, workspace, item, result.Message, QueueStepRegistry.UploadSeries);
                    await SyncManagementAfterUploadIfEnabledAsync(
                        options, workspace, item, account, onProgress, ct).ConfigureAwait(false);
                    return true;
                }
                failureMessage = result.Message;
                stopQueue = result.StopQueue;
            }
        }
        catch (OperationCanceledException)
        {
            mutate(() => MarkStopped(item, QueueStepRegistry.UploadSeries));
            Report(onProgress, workspace, item,
                "上传中断：队列收到停止信号（手动停止或程序退出），重新执行时将自动续传已有草稿",
                QueueStepRegistry.UploadSeries);
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
            failureMessage = ex.Message;
        }

        if (stopQueue)
        {
            // 单日创建剧集上限：对齐 Python，项目标记为「已停止」而非失败（明天重跑即可），并输出专用提示。
            var isDailyLimit = failureMessage.Contains("单日创建剧集上限", StringComparison.Ordinal);
            if (isDailyLimit)
            {
                mutate(() => MarkStopped(item, QueueStepRegistry.UploadSeries));
                Report(onProgress, workspace, item,
                    $"已达单日创建剧集上限，任务队列已停止，请明天再继续上传：{failureMessage}",
                    QueueStepRegistry.UploadSeries);
            }
            else
            {
                mutate(() => MarkFailed(item, QueueStepRegistry.UploadSeries, failureMessage));
                Report(onProgress, workspace, item,
                    $"上传失败，已停止后续队列：{failureMessage}",
                    QueueStepRegistry.UploadSeries);
            }

            throw new QueueStopRequestedException(failureMessage);
        }

        if (manualIntervention is not null && !ct.IsCancellationRequested && !manualIntervention.WasResolved(item.ProjectDir))
        {
            mutate(() => MarkManualIntervention(item, failureMessage));
            Report(onProgress, workspace, item,
                $"上传失败：{failureMessage}｜浏览器保持打开，等待弹窗选择人工处理完成或跳过",
                QueueStepRegistry.UploadSeries);

            ManualInterventionResult action;
            try
            {
                action = await manualIntervention.AwaitAsync(item, failureMessage, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                mutate(() => MarkStopped(item, QueueStepRegistry.UploadSeries));
                throw;
            }

            switch (action)
            {
                case ManualInterventionResult.Success:
                    mutate(() =>
                    {
                        MarkCompleted(item, QueueStepRegistry.UploadSeries);
                        item.UploadCompletedAt = DateTimeOffset.Now.ToString("o");
                        item.AccountProfileId = account.Id;
                        item.AccountProfileName = account.DisplayName;
                    });
                    Report(onProgress, workspace, item, "人工介入：已标记上传成功", QueueStepRegistry.UploadSeries);
                    await SyncManagementAfterUploadIfEnabledAsync(
                        options, workspace, item, account, onProgress, ct).ConfigureAwait(false);
                    return true;
                case ManualInterventionResult.Failed:
                    mutate(() => MarkFailed(item, QueueStepRegistry.UploadSeries, failureMessage));
                    Report(onProgress, workspace, item, "人工介入：已跳过此项目并标记上传失败", QueueStepRegistry.UploadSeries);
                    return false;
                case ManualInterventionResult.Stopped:
                    mutate(() => MarkStopped(item, QueueStepRegistry.UploadSeries));
                    return false;
            }
        }

        mutate(() => MarkFailed(item, QueueStepRegistry.UploadSeries, failureMessage));
        if (failure is not null) return false;
        return false;
    }

    private static async Task RunPreUploadStepAsync(
        QueueProjectItem item,
        string stepKey,
        QueueRunOptions options,
        TikTokAccountProfile? account,
        Action<string> log,
        CancellationToken ct)
    {
        var settings = ClientSettingsStore.Load();
        var materialOptions = TikTokMaterialValidationService.Options.FromAccount(account, settings);
        switch (stepKey)
        {
            case QueueStepRegistry.Download:
                await QueueMaterialStepService.RunDownloadAsync(item, settings, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.RewriteInfo:
                await QueueMaterialStepService.RunRewriteAsync(
                    item, settings, account, options.ForceRerunCompletedSteps, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.GeneratePoster:
                await QueueMaterialStepService.RunGeneratePosterAsync(item, settings, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.DeleteSourceVideos:
                await QueueMaterialStepService.RunDeleteSourceVideosAsync(item, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.SmallVideoRepair:
                TikTokSmallVideoRepairService.Repair(item.ProjectDir, item.Title, item.OriginalTitle, log, ct);
                break;
            case QueueStepRegistry.SilenceDetect:
                await TikTokSilenceDetectService.DetectAsync(
                    item.ProjectDir, item.Title, item.OriginalTitle, materialOptions, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.SilenceRepair:
                await TikTokSilenceRepairService.RepairAsync(
                    item.ProjectDir, item.Title, item.OriginalTitle, materialOptions, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.MaterialValidate:
                await TikTokMaterialValidationService.ValidateAsync(
                    item.ProjectDir, item.Title, item.OriginalTitle, materialOptions, log, ct, account).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"未知预处理步骤：{stepKey}");
        }
    }

    private static async Task SyncManagementAfterUploadIfEnabledAsync(
        QueueRunOptions options,
        string workspace,
        QueueProjectItem item,
        TikTokAccountProfile account,
        Action<QueueWorkerProgress>? onProgress,
        CancellationToken ct)
    {
        if (!options.SyncManagementAfterUpload) return;

        try
        {
            var result = await TikTokManagementUploadRecordSyncService
                .SyncUploadRecordAsync(item, account, ct)
                .ConfigureAwait(false);
            Report(
                onProgress,
                workspace,
                item,
                result.Ok
                    ? $"已同步管理系统：{result.Message}"
                    : $"同步管理系统失败：{result.Message}",
                QueueStepRegistry.UploadSeries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report(onProgress, workspace, item, $"同步管理系统异常：{ex.Message}", QueueStepRegistry.UploadSeries);
        }
    }

    private static bool ShouldRunStep(QueueProjectItem item, string stepKey, QueueRunOptions options)
    {
        if (options.ForceRerunCompletedSteps) return true;
        if (stepKey == QueueStepRegistry.RewriteInfo &&
            item.StepStates.GetValueOrDefault(stepKey) == QueueStepStatus.Completed &&
            QueueMaterialStepService.NeedsAiRewrite(item))
        {
            return true;
        }

        return item.StepStates.GetValueOrDefault(stepKey) != QueueStepStatus.Completed;
    }

    private static TikTokAccountProfile? ResolveAccount(AccountStore store, QueueProjectItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.AccountProfileId))
        {
            var bound = store.FindByNameOrId(item.AccountProfileId);
            if (bound is not null) return bound;
        }
        var activeId = store.ActiveAccountId;
        return store.Accounts.FirstOrDefault(a => a.Id == activeId) ?? store.Accounts.FirstOrDefault();
    }

    private static void MarkRunning(QueueProjectItem item, string stepKey)
    {
        item.CurrentStep = stepKey;
        item.StatusText = QueueStepStatus.Running;
        item.StepStates[stepKey] = QueueStepStatus.Running;
        item.LastError = "";
    }

    private static void MarkCompleted(QueueProjectItem item, string stepKey)
    {
        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Completed;
        item.StepStates[stepKey] = QueueStepStatus.Completed;
        item.LastError = "";
        item.NormalizeStepStates();
    }

    private static void MarkProjectCompleted(QueueProjectItem item)
    {
        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Completed;
        item.LastError = "";
        item.NormalizeStepStates();
    }

    private static void MarkFailed(QueueProjectItem item, string stepKey, string error)
    {
        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Failed;
        item.StepStates[stepKey] = QueueStepStatus.Failed;
        item.LastError = error;
    }

    private static void MarkStopped(QueueProjectItem item, string stepKey)
    {
        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Stopped;
        if (!string.IsNullOrWhiteSpace(stepKey))
            item.StepStates[stepKey] = QueueStepStatus.Stopped;
    }

    private static void MarkWaitingSlot(QueueProjectItem item)
    {
        item.StatusText = QueueStepStatus.WaitingUploadSlot;
        item.StepStates[QueueStepRegistry.UploadSeries] = QueueStepStatus.WaitingUploadSlot;
    }

    private static void MarkManualIntervention(QueueProjectItem item, string error)
    {
        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.ManualIntervention;
        item.StepStates[QueueStepRegistry.UploadSeries] = QueueStepStatus.ManualIntervention;
        item.LastError = error ?? "";
    }

    private static void Persist(
        string workspace,
        IList<QueueProjectItem>? items,
        Action<IReadOnlyList<QueueProjectItem>>? onPersist)
    {
        if (onPersist is null || items is null) return;
        onPersist(items.ToList());
    }

    private static void Report(
        Action<QueueWorkerProgress>? onProgress,
        string workspace,
        QueueProjectItem? item,
        string message,
        string? stepKey = null)
    {
        onProgress?.Invoke(new QueueWorkerProgress
        {
            WorkspaceRoot = workspace,
            Item = item,
            Message = message,
            StepKey = stepKey,
        });
    }

    private void DrainIncomingItems(
        IList<QueueProjectItem> items,
        List<QueueProjectItem> candidates,
        Queue<(int Index, QueueProjectItem Item)> pendingPreUpload,
        object stateLock)
    {
        List<QueueProjectItem> batch;
        lock (_incomingLock)
        {
            if (_incomingItems.Count == 0)
                return;
            batch = _incomingItems.ToList();
            _incomingItems.Clear();
        }

        lock (stateLock)
        {
            foreach (var item in batch)
            {
                if (!item.Enabled)
                    continue;

                var projectDir = Path.GetFullPath(item.ProjectDir);
                if (items.Any(existing =>
                        string.Equals(Path.GetFullPath(existing.ProjectDir), projectDir, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                items.Add(item);
                candidates.Add(item);
                pendingPreUpload.Enqueue((candidates.Count, item));
            }
        }
    }

    private static QueueProjectItem CloneQueueItem(QueueProjectItem item) =>
        QueueProjectItem.FromPayload(item.ToPayload());

    private sealed class QueueStopRequestedException(string message) : Exception(message);
}
