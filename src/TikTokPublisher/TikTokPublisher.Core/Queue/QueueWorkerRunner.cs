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
    Task<bool> EnsureAccountBrowserReadyAsync(TikTokAccountProfile account, CancellationToken ct);
    Task<PublishResult> PublishProjectAsync(
        TikTokAccountProfile account,
        QueueProjectItem project,
        FinalAction finalAction,
        Action<string> log,
        CancellationToken ct);
}

/// <summary>工作目录队列 Worker（预处理步骤 + <c>upload_series</c>，支持项目级并行）。</summary>
public sealed class QueueWorkerRunner
{
    private const int ProjectConcurrencyHardMax = 20;

    private readonly UploadSlotCoordinator _uploadSlots = new();
    public ManualInterventionCoordinator ManualIntervention { get; } = new();
    private static readonly TikTokMaterialValidationService.Options DefaultMaterialOptions = new();

    public async Task<QueueWorkerSummary> RunAsync(
        string workspaceRoot,
        IList<QueueProjectItem> items,
        QueueRunOptions options,
        IQueuePublishHost host,
        AccountStore accountStore,
        FinalAction finalAction,
        Action<QueueWorkerProgress>? onProgress,
        Action<IReadOnlyList<QueueProjectItem>>? onPersist,
        CancellationToken ct)
    {
        var workspace = Path.GetFullPath(workspaceRoot);
        var candidates = items
            .Where(i => i.Enabled && !i.Archived)
            .OrderBy(i => string.IsNullOrWhiteSpace(i.QueuedAt) ? "9999" : i.QueuedAt, StringComparer.Ordinal)
            .ThenBy(i => i.ProjectDir, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projectConcurrency = Math.Clamp(options.ProjectConcurrency, 1, ProjectConcurrencyHardMax);
        var orderedSteps = options.OrderedEnabledSteps();
        var preUploadSteps = orderedSteps.Where(s => s != QueueStepRegistry.UploadSeries).ToList();
        var uploadEnabled = options.IsStepEnabled(QueueStepRegistry.UploadSeries);

        ManualIntervention.Reset();
        var settings = ClientSettingsStore.Load();
        var manualInterventionAllowed = candidates.Count == 1
            && uploadEnabled
            && settings.TiktokManualInterventionOnSingleFailure;

        var success = 0;
        var failed = 0;
        var stopped = false;
        var stateLock = new object();

        var pendingPreUpload = new Queue<(int Index, QueueProjectItem Item)>(
            candidates.Select((item, index) => (index + 1, item)));
        var readyForUpload = new Queue<QueueProjectItem>();
        var preUploadTasks = new Dictionary<Task, QueueProjectItem>();
        var uploadTasks = new Dictionary<Task<bool>, (QueueProjectItem Item, string AccountKey)>();
        var activeUploadAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Report(onProgress, workspace, null,
            $"开始执行队列，共 {candidates.Count} 个项目（启用步骤：{string.Join(", ", orderedSteps)}，项目并发 {projectConcurrency}）");

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
                    uploadTasks[task] = (capturedItem, accountKey);
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
               || uploadTasks.Count > 0)
        {
            if (ct.IsCancellationRequested && !stopped)
            {
                stopped = true;
                DrainPendingAsStopped();
            }

            if (!stopped)
            {
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
                    Mutate(() =>
                    {
                        var step = string.IsNullOrWhiteSpace(preItem.CurrentStep)
                            ? preUploadSteps.LastOrDefault() ?? QueueStepRegistry.UploadSeries
                            : preItem.CurrentStep;
                        MarkFailed(preItem, step, ex.Message);
                        failed++;
                    });
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
                                    .ArchiveQueueProjectAsync(workspace, uploadCtx.Item.ProjectDir, ct: ct)
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

            var stepAccount = stepKey == QueueStepRegistry.RewriteInfo
                ? ResolveAccount(accountStore, item)
                : null;
            await RunPreUploadStepAsync(
                item,
                stepKey,
                options,
                stepAccount,
                msg => Report(onProgress, workspace, item, msg, stepKey),
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
        mutate(() => MarkRunning(item, QueueStepRegistry.UploadSeries));
        Report(onProgress, workspace, item, $"[{account.DisplayName}] 准备内置浏览器…", QueueStepRegistry.UploadSeries);

        Exception? failure = null;
        var failureMessage = "";
        var stopQueue = false;
        try
        {
            if (!await host.EnsureAccountBrowserReadyAsync(account, ct).ConfigureAwait(false))
            {
                failureMessage = "内置浏览器未就绪或未登录，请先在「浏览器」页完成登录";
            }
            else
            {
                Report(onProgress, workspace, item, "开始上传发布…", QueueStepRegistry.UploadSeries);
                var result = await host.PublishProjectAsync(
                    account,
                    item,
                    finalAction,
                    msg => Report(onProgress, workspace, item, msg, QueueStepRegistry.UploadSeries),
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
                    return true;
                }
                failureMessage = result.Message;
                stopQueue = result.StopQueue;
            }
        }
        catch (OperationCanceledException)
        {
            mutate(() => MarkStopped(item, QueueStepRegistry.UploadSeries));
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
            failureMessage = ex.Message;
        }

        if (stopQueue)
        {
            mutate(() => MarkFailed(item, QueueStepRegistry.UploadSeries, failureMessage));
            Report(onProgress, workspace, item,
                $"上传失败，已停止后续队列：{failureMessage}",
                QueueStepRegistry.UploadSeries);
            throw new QueueStopRequestedException(failureMessage);
        }

        if (manualIntervention is not null && !ct.IsCancellationRequested && !manualIntervention.WasResolved(item.ProjectDir))
        {
            mutate(() => MarkManualIntervention(item, failureMessage));
            Report(onProgress, workspace, item,
                $"上传失败：{failureMessage}｜浏览器保持打开，请在队列页处理后标记「成功 / 失败」",
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
                    return true;
                case ManualInterventionResult.Failed:
                    mutate(() => MarkFailed(item, QueueStepRegistry.UploadSeries, failureMessage));
                    Report(onProgress, workspace, item, "人工介入：已标记上传失败", QueueStepRegistry.UploadSeries);
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
                TikTokSmallVideoRepairService.Repair(item.ProjectDir, item.Title, item.OriginalTitle, log);
                break;
            case QueueStepRegistry.SilenceDetect:
                await TikTokSilenceDetectService.DetectAsync(
                    item.ProjectDir, item.Title, item.OriginalTitle, DefaultMaterialOptions, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.SilenceRepair:
                await TikTokSilenceRepairService.RepairAsync(
                    item.ProjectDir, item.Title, item.OriginalTitle, DefaultMaterialOptions, log, ct).ConfigureAwait(false);
                break;
            case QueueStepRegistry.MaterialValidate:
                await TikTokMaterialValidationService.ValidateAsync(
                    item.ProjectDir, item.Title, item.OriginalTitle, DefaultMaterialOptions, log, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"未知预处理步骤：{stepKey}");
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

    private sealed class QueueStopRequestedException(string message) : Exception(message);
}
