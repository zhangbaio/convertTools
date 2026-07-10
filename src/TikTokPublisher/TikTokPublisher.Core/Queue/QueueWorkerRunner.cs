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
    public int StoppedAccountCount { get; init; }
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
        var projectConcurrency = Math.Clamp(options.ProjectConcurrency, 1, ProjectConcurrencyHardMax);
        var orderedSteps = options.OrderedEnabledSteps();
        var preUploadSteps = orderedSteps.Where(s => s != QueueStepRegistry.UploadSeries).ToList();
        var uploadEnabled = options.IsStepEnabled(QueueStepRegistry.UploadSeries);

        var candidateQuery = items
            .Where(i => i.Enabled && !i.Archived)
            .Where(i => filter is null || filter.Contains(Path.GetFullPath(i.ProjectDir)))
            .Where(i => orderedSteps.Any(stepKey => ShouldRunStep(i, stepKey, options, ResolveAccount(accountStore, i))));
        var candidates = filterOrder is not null
            ? candidateQuery
                .OrderBy(i => filterOrder.GetValueOrDefault(Path.GetFullPath(i.ProjectDir), int.MaxValue))
                .ThenBy(i => i.ProjectDir, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : candidateQuery
                .OrderBy(i => string.IsNullOrWhiteSpace(i.QueuedAt) ? "9999" : i.QueuedAt, StringComparer.Ordinal)
                .ThenBy(i => i.ProjectDir, StringComparer.OrdinalIgnoreCase)
                .ToList();

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

        Mutate(() =>
        {
            foreach (var item in candidates)
                MarkQueuedForRun(item, orderedSteps, options);
        });

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
                        var task = MakeUniqueTask(RunPreUploadPipelineAsync(
                            workspace,
                            captured,
                            preUploadSteps,
                            options,
                            accountStore,
                            onProgress,
                            ct,
                            Mutate));
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
                    if (account is null)
                    {
                        MarkFailed(
                            item,
                            QueueStepRegistry.UploadSeries,
                            $"未找到绑定账号：{DescribeBoundAccount(item)}，已中止上传，避免误用当前账号。");
                        failed++;
                        Persist(workspace, items, onPersist);
                        rotations = readyForUpload.Count;
                        continue;
                    }

                    var accountKey = account.Id;

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

                    if (!string.Equals((item.AccountProfileId ?? "").Trim(), account.Id, StringComparison.Ordinal) ||
                        !string.Equals((item.AccountProfileName ?? "").Trim(), account.DisplayName, StringComparison.Ordinal))
                    {
                        var oldBinding = DescribeBoundAccount(item);
                        item.AccountProfileId = account.Id;
                        item.AccountProfileName = account.DisplayName;
                        Report(
                            onProgress,
                            workspace,
                            item,
                            $"已修复队列账号绑定：{oldBinding} -> {account.DisplayName} ({account.Id})",
                            QueueStepRegistry.UploadSeries);
                        Persist(workspace, items, onPersist);
                    }

                    activeUploadAccounts.Add(accountKey);
                    var capturedAccount = account;
                    var capturedItem = item;
                    var task = MakeUniqueTask(RunUploadPipelineAsync(
                        workspace,
                        capturedItem,
                        capturedAccount,
                        host,
                        finalAction,
                        options,
                        onProgress,
                        ct,
                        Mutate,
                        manualInterventionAllowed ? ManualIntervention : null));
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
                DrainIncomingItems(
                    workspace,
                    items,
                    candidates,
                    pendingPreUpload,
                    readyForUpload,
                    preUploadTasks.Values,
                    uploadTasks.Values.Select(ctx => ctx.Item),
                    stateLock,
                    orderedSteps,
                    options,
                    onPersist);
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
                catch (OperationCanceledException ex)
                {
                    var failedStep = string.IsNullOrWhiteSpace(preItem.CurrentStep)
                        ? preUploadSteps.LastOrDefault() ?? QueueStepRegistry.UploadSeries
                        : preItem.CurrentStep;
                    if (ct.IsCancellationRequested)
                    {
                        Mutate(() => MarkStopped(preItem, failedStep));
                        stopped = true;
                        DrainPendingAsStopped();
                    }
                    else
                    {
                        var message = BuildNonQueueCancellationMessage(QueueStepRegistry.LabelOf(failedStep), ex);
                        Mutate(() =>
                        {
                            MarkFailed(preItem, failedStep, message);
                            failed++;
                        });
                        Report(
                            onProgress,
                            workspace,
                            preItem,
                            $"{message} 已标记此项目失败并继续后续队列。",
                            failedStep);
                    }
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
                                var deleteVideosOnArchive = uploadCtx.Account?.TiktokDeleteVideosOnArchive ?? true;
                                await TikTokArchivedProjectService
                                    .ArchiveQueueProjectAsync(
                                        workspace,
                                        uploadCtx.Item.ProjectDir,
                                        deleteSourceVideos: deleteVideosOnArchive,
                                        deleteWorkflowVideos: deleteVideosOnArchive,
                                        deleteMaterialVideos: deleteVideosOnArchive,
                                        account: uploadCtx.Account,
                                        queuedAt: uploadCtx.Item.QueuedAt,
                                        ct: ct)
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
                catch (OperationCanceledException ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Mutate(() => MarkStopped(uploadCtx.Item, QueueStepRegistry.UploadSeries));
                        stopped = true;
                        DrainPendingAsStopped();
                    }
                    else
                    {
                        var message = BuildNonQueueCancellationMessage(QueueStepRegistry.LabelOf(QueueStepRegistry.UploadSeries), ex);
                        Mutate(() =>
                        {
                            MarkFailed(uploadCtx.Item, QueueStepRegistry.UploadSeries, message);
                            failed++;
                        });
                        Report(
                            onProgress,
                            workspace,
                            uploadCtx.Item,
                            $"{message} 已标记此项目失败并继续后续队列。",
                            QueueStepRegistry.UploadSeries);
                    }
                }
            }
        }

        var summary = new QueueWorkerSummary
        {
            TotalCount = candidates.Count,
            SuccessCount = success,
            FailedCount = failed,
            StoppedAccountCount = candidates
                .Where(item => item.StepStates.GetValueOrDefault(QueueStepRegistry.UploadSeries) == QueueStepStatus.Stopped)
                .Select(item => item.AccountProfileId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
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
            var stepAccount = ResolveAccount(accountStore, item);
            if (!ShouldRunStep(item, stepKey, options, stepAccount))
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

        var consistency = TikTokUploadEpisodeConsistencyService.ValidateBeforeUpload(item);
        if (!consistency.Ok)
        {
            mutate(() => MarkFailed(item, QueueStepRegistry.UploadSeries, consistency.Message));
            Report(onProgress, workspace, item, consistency.Message, QueueStepRegistry.UploadSeries);
            return false;
        }

        var preflight = await TikTokUploadFilePreflightService
            .ValidateAsync(item, uploadLog, ct)
            .ConfigureAwait(false);
        if (!preflight.Ok)
        {
            mutate(() => MarkFailed(item, QueueStepRegistry.UploadSeries, preflight.Message));
            Report(onProgress, workspace, item, preflight.Message, QueueStepRegistry.UploadSeries);
            return false;
        }

        mutate(() => MarkRunning(item, QueueStepRegistry.UploadSeries));
        Report(onProgress, workspace, item, $"[{account.DisplayName}] 准备内置浏览器…", QueueStepRegistry.UploadSeries);

        Exception? failure = null;
        var failureMessage = "";
        var stopQueue = false;
        var skipManualIntervention = false;
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
                skipManualIntervention = result.SkipManualIntervention;
            }
        }
        catch (OperationCanceledException ex)
        {
            if (ct.IsCancellationRequested)
            {
                mutate(() => MarkStopped(item, QueueStepRegistry.UploadSeries));
                Report(onProgress, workspace, item,
                    "上传中断：队列收到停止信号（手动停止或程序退出），重新执行时将自动续传已有草稿",
                    QueueStepRegistry.UploadSeries);
                throw;
            }

            failure = ex;
            failureMessage = BuildNonQueueCancellationMessage(QueueStepRegistry.LabelOf(QueueStepRegistry.UploadSeries), ex);
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

        if (!skipManualIntervention &&
            manualIntervention is not null &&
            !ct.IsCancellationRequested &&
            !manualIntervention.WasResolved(item.ProjectDir))
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
            case QueueStepRegistry.GenerateProjectImages:
                await TikTokProjectImageService.GenerateAsync(
                    item, settings, options.ForceRerunCompletedSteps, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.DeleteSourceVideos:
                await QueueMaterialStepService.RunDeleteSourceVideosAsync(item, settings, log, ct).ConfigureAwait(false);
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

    private static bool ShouldRunStep(
        QueueProjectItem item,
        string stepKey,
        QueueRunOptions options,
        TikTokAccountProfile? account = null)
    {
        if (options.ForceRerunCompletedSteps) return true;
        if (stepKey == QueueStepRegistry.RewriteInfo &&
            item.StepStates.GetValueOrDefault(stepKey) == QueueStepStatus.Completed &&
            QueueMaterialStepService.NeedsAiRewrite(item, account))
        {
            return true;
        }
        if (stepKey == QueueStepRegistry.GenerateProjectImages &&
            item.StepStates.GetValueOrDefault(stepKey) == QueueStepStatus.Completed &&
            TikTokProjectImageService.NeedsGenerateProjectImages(item, ClientSettingsStore.Load()))
        {
            return true;
        }

        return item.StepStates.GetValueOrDefault(stepKey) != QueueStepStatus.Completed;
    }

    private static string BuildNonQueueCancellationMessage(string stepLabel, OperationCanceledException ex)
    {
        var detail = (ex.Message ?? "").Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"{stepLabel} 被取消或超时。"
            : $"{stepLabel} 被取消或超时：{detail}";
    }

    private static Task MakeUniqueTask(Task task)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.ContinueWith(
            completed =>
            {
                if (completed.IsCanceled)
                    tcs.TrySetCanceled();
                else if (completed.IsFaulted)
                    tcs.TrySetException(completed.Exception!.InnerExceptions);
                else
                    tcs.TrySetResult(null);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return tcs.Task;
    }

    private static Task<bool> MakeUniqueTask(Task<bool> task)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.ContinueWith(
            completed =>
            {
                if (completed.IsCanceled)
                    tcs.TrySetCanceled();
                else if (completed.IsFaulted)
                    tcs.TrySetException(completed.Exception!.InnerExceptions);
                else
                    tcs.TrySetResult(completed.Result);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return tcs.Task;
    }

    private static void MarkQueuedForRun(
        QueueProjectItem item,
        IReadOnlyList<string> orderedSteps,
        QueueRunOptions options)
    {
        var hasRunnableStep = orderedSteps.Any(stepKey => ShouldRunStep(item, stepKey, options));
        if (!hasRunnableStep)
            return;

        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Pending;
        item.LastError = "";

        foreach (var key in item.StepStates.Keys.ToList())
        {
            if (item.StepStates[key] is QueueStepStatus.Failed or QueueStepStatus.Stopped or QueueStepStatus.ManualIntervention)
                item.StepStates[key] = QueueStepStatus.Pending;
        }

        foreach (var stepKey in orderedSteps)
        {
            if (!ShouldRunStep(item, stepKey, options))
                continue;
            if (stepKey == QueueStepRegistry.UploadSeries)
                item.ManualUploadStatus = "";
            item.StepStates[stepKey] = QueueStepStatus.Pending;
        }
    }

    private static TikTokAccountProfile? ResolveAccount(AccountStore store, QueueProjectItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.AccountProfileId))
        {
            var bound = store.FindByNameOrId(item.AccountProfileId);
            if (bound is not null) return bound;

            if (!string.IsNullOrWhiteSpace(item.AccountProfileName))
            {
                var renamed = store.FindByNameOrId(item.AccountProfileName);
                if (renamed is not null) return renamed;
            }

            return null;
        }

        if (!string.IsNullOrWhiteSpace(item.AccountProfileName))
        {
            var named = store.FindByNameOrId(item.AccountProfileName);
            if (named is not null) return named;
        }

        var activeId = store.ActiveAccountId;
        return store.Accounts.FirstOrDefault(a => a.Id == activeId) ?? store.Accounts.FirstOrDefault();
    }

    private static string DescribeBoundAccount(QueueProjectItem item)
    {
        var id = (item.AccountProfileId ?? "").Trim();
        var name = (item.AccountProfileName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            return $"{name} ({id})";
        return !string.IsNullOrWhiteSpace(id)
            ? id
            : !string.IsNullOrWhiteSpace(name)
                ? name
                : "未绑定";
    }

    private static void MarkRunning(QueueProjectItem item, string stepKey)
    {
        if (stepKey == QueueStepRegistry.UploadSeries)
            item.ManualUploadStatus = "";
        item.CurrentStep = stepKey;
        item.StatusText = QueueStepStatus.Running;
        item.StepStates[stepKey] = QueueStepStatus.Running;
        item.LastError = "";
    }

    private static void MarkCompleted(QueueProjectItem item, string stepKey)
    {
        if (stepKey == QueueStepRegistry.UploadSeries)
            item.ManualUploadStatus = "";
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
        if (stepKey == QueueStepRegistry.UploadSeries)
            item.ManualUploadStatus = "";
        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Failed;
        item.StepStates[stepKey] = QueueStepStatus.Failed;
        item.LastError = error;
    }

    private static void MarkStopped(QueueProjectItem item, string stepKey)
    {
        if (stepKey == QueueStepRegistry.UploadSeries)
            item.ManualUploadStatus = "";
        item.CurrentStep = "";
        item.StatusText = QueueStepStatus.Stopped;
        if (!string.IsNullOrWhiteSpace(stepKey))
            item.StepStates[stepKey] = QueueStepStatus.Stopped;
    }

    private static void MarkWaitingSlot(QueueProjectItem item)
    {
        item.ManualUploadStatus = "";
        item.StatusText = QueueStepStatus.WaitingUploadSlot;
        item.StepStates[QueueStepRegistry.UploadSeries] = QueueStepStatus.WaitingUploadSlot;
    }

    private static void MarkManualIntervention(QueueProjectItem item, string error)
    {
        item.ManualUploadStatus = "";
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

    private static bool SameProjectDir(QueueProjectItem item, string projectDir) =>
        string.Equals(Path.GetFullPath(item.ProjectDir), projectDir, StringComparison.OrdinalIgnoreCase);

    private void DrainIncomingItems(
        string workspace,
        IList<QueueProjectItem> items,
        List<QueueProjectItem> candidates,
        Queue<(int Index, QueueProjectItem Item)> pendingPreUpload,
        Queue<QueueProjectItem> readyForUpload,
        IEnumerable<QueueProjectItem> activePreUploadItems,
        IEnumerable<QueueProjectItem> activeUploadItems,
        object stateLock,
        IReadOnlyList<string> orderedSteps,
        QueueRunOptions options,
        Action<IReadOnlyList<QueueProjectItem>>? onPersist)
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
            var added = false;
            foreach (var item in batch)
            {
                if (!item.Enabled)
                    continue;

                var projectDir = Path.GetFullPath(item.ProjectDir);
                var existingCandidate = candidates.FirstOrDefault(existing => SameProjectDir(existing, projectDir));
                if (existingCandidate is not null)
                {
                    if (IsProjectScheduledOrActive(projectDir, pendingPreUpload, readyForUpload, activePreUploadItems, activeUploadItems))
                        continue;

                    CopyQueueItemForAppend(existingCandidate, item);
                    MarkQueuedForRun(existingCandidate, orderedSteps, options);
                    if (!orderedSteps.Any(stepKey => ShouldRunStep(existingCandidate, stepKey, options)))
                        continue;

                    pendingPreUpload.Enqueue((candidates.IndexOf(existingCandidate) + 1, existingCandidate));
                    added = true;
                    continue;
                }

                var queueItem = items.FirstOrDefault(existing => SameProjectDir(existing, projectDir));
                if (queueItem is null)
                {
                    queueItem = item;
                    items.Add(queueItem);
                }

                queueItem.Enabled = true;
                CopyQueueItemForAppend(queueItem, item);
                MarkQueuedForRun(queueItem, orderedSteps, options);
                candidates.Add(queueItem);
                pendingPreUpload.Enqueue((candidates.Count, queueItem));
                added = true;
            }

            if (added)
                Persist(workspace, items, onPersist);
        }
    }

    private static bool IsProjectScheduledOrActive(
        string projectDir,
        Queue<(int Index, QueueProjectItem Item)> pendingPreUpload,
        Queue<QueueProjectItem> readyForUpload,
        IEnumerable<QueueProjectItem> activePreUploadItems,
        IEnumerable<QueueProjectItem> activeUploadItems) =>
        pendingPreUpload.Any(entry => SameProjectDir(entry.Item, projectDir)) ||
        readyForUpload.Any(item => SameProjectDir(item, projectDir)) ||
        activePreUploadItems.Any(item => SameProjectDir(item, projectDir)) ||
        activeUploadItems.Any(item => SameProjectDir(item, projectDir));

    private static void CopyQueueItemForAppend(QueueProjectItem target, QueueProjectItem source)
    {
        target.DisplayName = source.DisplayName;
        target.OriginalTitle = source.OriginalTitle;
        target.NewTitle = source.NewTitle;
        target.EpisodeCount = source.EpisodeCount;
        target.GenreCategory = source.GenreCategory;
        target.Description = source.Description;
        target.QueueEntryDramaType = source.QueueEntryDramaType;
        target.AccountProfileId = source.AccountProfileId;
        target.AccountProfileName = source.AccountProfileName;
        target.QueuedAt = source.QueuedAt;
        target.UploadCompletedAt = source.UploadCompletedAt;
        target.Enabled = source.Enabled;
        target.CurrentStep = source.CurrentStep;
        target.StatusText = source.StatusText;
        target.LastError = source.LastError;
        target.Remark = source.Remark;
        target.ManualUploadStatus = source.ManualUploadStatus;
        target.StepStates = new Dictionary<string, string>(source.StepStates);
        target.Archived = source.Archived;
        target.PrimaryVideoPath = source.PrimaryVideoPath;
        target.CoverPath = source.CoverPath;
        target.NormalizeStepStates();
    }

    private static QueueProjectItem CloneQueueItem(QueueProjectItem item) =>
        QueueProjectItem.FromPayload(item.ToPayload());

    private sealed class QueueStopRequestedException(string message) : Exception(message);
}
