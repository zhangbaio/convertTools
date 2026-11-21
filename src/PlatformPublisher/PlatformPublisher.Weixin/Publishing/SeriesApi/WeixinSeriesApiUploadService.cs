using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Weixin.Publishing;

public enum WeixinSeriesUploadMode
{
    Browser,
    ApiUpload,
    ApiSubmit,
}

public enum WeixinSeriesApiResult
{
    Uploaded,
    Submitted,
    AlreadySubmitted,
    Cancelled,
}

public sealed record WeixinSeriesApiConfirmation(string Account, string Title, int EpisodeCount);

public sealed class WeixinSeriesSubmissionPendingException(string message) : InvalidOperationException(message);

public sealed class WeixinSeriesApiUploadService
{
    private readonly WeixinSeriesApiStateStore _state;
    private readonly WeixinSeriesReviewStore _browserReviews;
    private readonly Func<WeixinSeriesApiSession, string, IWeixinSeriesApiClient> _createClient;

    public WeixinSeriesApiUploadService()
        : this(Path.Combine(PlatformPublisherPaths.DataRoot, "weixin", "series-api"),
            WeixinSeriesReviewStore.CreateDefault(), WeixinSeriesApiClient.Create)
    {
    }

    internal WeixinSeriesApiUploadService(string stateRoot, WeixinSeriesReviewStore browserReviews,
        Func<WeixinSeriesApiSession, string, IWeixinSeriesApiClient> createClient)
    {
        _state = new WeixinSeriesApiStateStore(stateRoot);
        _browserReviews = browserReviews;
        _createClient = createClient;
    }

    public async Task<WeixinSeriesApiResult> RunAsync(PublishJob job, string configPath, WeixinPublishOptions options,
        IProgress<string>? progress, Func<WeixinSeriesApiConfirmation, CancellationToken, Task<bool>>? confirm,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.AccountId) || string.IsNullOrWhiteSpace(job.AccountSessionDirectory))
            throw new InvalidOperationException("接口上传需要选择一个已配置会话目录的视频号账号。");
        if (options.SeriesUploadMode is not (WeixinSeriesUploadMode.ApiUpload or WeixinSeriesUploadMode.ApiSubmit))
            throw new InvalidOperationException("当前任务未选择视频号接口上传。");

        using var account = _state.OpenAccount(job.AccountId);
        if (_browserReviews.HasConfirmedSubmission(job.AccountId, job.ProjectDirectory))
            return WeixinSeriesApiResult.AlreadySubmitted;
        if (options.SeriesUploadMode == WeixinSeriesUploadMode.ApiSubmit &&
            _browserReviews.HasReachedReview(job.AccountId, job.ProjectDirectory))
            throw Pending("此项目曾通过浏览器进入提审页，请先核对提审结果。");
        using (var existing = account.OpenProject(job.ProjectDirectory))
        {
            if (existing is not null)
                EnsureNotPending(existing.Load());
        }

        var project = await WeixinSeriesApiProject.LoadAsync(configPath, job.ProjectName, options, job.PublishCount, ct)
            .ConfigureAwait(false);
        var authPath = Path.Combine(job.AccountSessionDirectory, "weixin-series-auth.json");
        var session = await WeixinSeriesApiSession.LoadAsync(authPath, ct).ConfigureAwait(false);
        account.VerifyIdentity(session.FinderId);
        using var client = _createClient(session, project.UserAgent);
        progress?.Report("正在校验当前视频号账号及提审服务会话。");
        await client.AuthenticateAsync(session, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        account.BindIdentity(session.FinderId);

        using var ledger = account.OpenProject(job.ProjectDirectory, create: true)!;
        var state = ledger.Load();
        if (state.Submission == WeixinSeriesSubmissionState.Submitted)
        {
            if (!string.Equals(state.Title, project.Title, StringComparison.Ordinal))
                throw new InvalidOperationException("该项目已有提审记录，当前剧名与记录不一致。请核对平台状态。");
            return WeixinSeriesApiResult.AlreadySubmitted;
        }
        EnsureNotPending(state);

        if (project.Form is { } form)
        {
            if (confirm is not null && !await confirm(new(job.AccountName, form.Title, form.EpisodeCount), ct).ConfigureAwait(false))
                return WeixinSeriesApiResult.Cancelled;
            await client.CheckNameAsync(form.Title, ct).ConfigureAwait(false);
        }

        var cache = new Dictionary<(string Kind, string Path), WeixinSeriesApiReceipt>();
        foreach (var receipt in state.Files)
            cache[Key(receipt.File)] = receipt;
        var touched = new HashSet<(string Kind, string Path)>();
        var selected = new List<WeixinSeriesApiReceipt>(project.Files.Count);
        foreach (var file in project.Files)
        {
            ct.ThrowIfCancellationRequested();
            var key = Key(file);
            var refresh = options.ReuploadSeriesApiFiles && touched.Add(key);
            if (!refresh && cache.TryGetValue(key, out var previous) &&
                previous.File.Length == file.Length && previous.File.ModifiedUtcTicks == file.ModifiedUtcTicks)
            {
                selected.Add(new(file, previous.MediaId));
                progress?.Report($"[{selected.Count}/{project.Files.Count}] 沿用回执：{file.Name}");
                continue;
            }

            progress?.Report($"[{selected.Count + 1}/{project.Files.Count}] 接口上传「{file.Label}」：{file.Name}");
            var mediaId = await client.UploadAsync(file, ct).ConfigureAwait(false);
            var receipt = new WeixinSeriesApiReceipt(file, mediaId);
            cache[key] = receipt;
            selected.Add(receipt);
            state = state with { Files = cache.Values.ToList() };
            ledger.Store(state);
        }

        if (project.Form is null)
            return WeixinSeriesApiResult.Uploaded;

        var payload = project.Form.BuildPayload(selected);
        await client.CheckMediaAsync(payload, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var pending = state with { Title = project.Title, Submission = WeixinSeriesSubmissionState.Pending };
        ledger.Store(pending);
        progress?.Report($"正在提交《{project.Title}》审核。");
        try
        {
            await client.SubmitAsync(payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WeixinSeriesApiRejectedException or WeixinSeriesApiQuotaExceededException)
        {
            try { ledger.Store(state); }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                throw Pending("平台拒绝了提审，但本地回执更新失败，请先核对提审结果。");
            }
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or OperationCanceledException)
        {
            throw Pending("接口提审结果待确认，请先到视频号剧集管理核对，核对前无法重新提交。");
        }

        try
        {
            ledger.Store(pending with { Submission = WeixinSeriesSubmissionState.Submitted });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Pending("视频号已响应接收提审，但无法保存本地回执，请核对提审结果。");
        }
        return WeixinSeriesApiResult.Submitted;
    }

    internal IDisposable? OpenBrowserRun(PublishJob job)
    {
        if (string.IsNullOrWhiteSpace(job.AccountId))
            return null;

        var account = _state.OpenAccount(job.AccountId);
        WeixinSeriesApiStateStore.ProjectScope? ledger = null;
        try
        {
            if (_browserReviews.HasConfirmedSubmission(job.AccountId, job.ProjectDirectory))
                throw new InvalidOperationException("此项目已核对为已提审，请到视频号后台查看审核状态。");
            ledger = account.OpenProject(job.ProjectDirectory);
            if (ledger is not null)
            {
                var state = ledger.Load();
                EnsureNotPending(state);
                if (state.Submission == WeixinSeriesSubmissionState.Submitted)
                    throw new InvalidOperationException("此项目已通过接口提交审核，请到视频号后台查看审核状态。");
            }
            return new BrowserLease(account, ledger);
        }
        catch
        {
            ledger?.Dispose();
            account.Dispose();
            throw;
        }
    }

    public bool ResolveReview(string accountId, string projectDirectory, bool submitted)
    {
        using var account = _state.OpenAccount(accountId);
        using var ledger = account.OpenProject(projectDirectory);
        if (ledger?.Load() is { Submission: WeixinSeriesSubmissionState.Pending } pending)
        {
            ledger.Store(pending with
            {
                Submission = submitted ? WeixinSeriesSubmissionState.Submitted : WeixinSeriesSubmissionState.None,
                Title = submitted ? pending.Title : null,
            });
            return true;
        }
        return _browserReviews.ResolveReview(accountId, projectDirectory, submitted);
    }

    private static (string Kind, string Path) Key(WeixinSeriesApiFile file) =>
        (file.Kind, OperatingSystem.IsWindows() ? file.Path.ToUpperInvariant() : file.Path);

    private static void EnsureNotPending(WeixinSeriesApiState state)
    {
        if (state.Submission == WeixinSeriesSubmissionState.Pending)
            throw Pending("上次接口提审结果待确认，请先核对平台记录，再点击「核对提审结果」。");
    }

    private static WeixinSeriesSubmissionPendingException Pending(string message) => new(message);

    private sealed class BrowserLease(IDisposable account, IDisposable? project) : IDisposable
    {
        public void Dispose()
        {
            project?.Dispose();
            account.Dispose();
        }
    }
}
