using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Ui.Views;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task<string> ReplaceKuaishouDraftAsync(string seriesId)
    {
        if (!IsKuaishouPlatform)
            return "请先切换到快手分账。";
        if (_activeGlobalAccount is null)
            return "请先选择账号。";
        if (IsBusy || IsKuaishouAccountRunning(CurrentKuaishouRunKey()))
            return "当前有任务在执行，请稍后再替换。";
        var rows = VisibleJobs.Where(row => row.IsChecked).ToArray();
        if (rows.Length == 0)
            return "请先勾选一部要替换上去的本地新剧。";
        if (rows.Length != 1)
            return "一次只能替换一部，请只勾选一部本地新剧。";

        var row = rows[0];
        var job = row.Model;
        if (!string.IsNullOrWhiteSpace(job.AccountId) &&
            !string.Equals(job.AccountId, _activeGlobalAccount.Id, StringComparison.OrdinalIgnoreCase))
            return "请勾选当前账号下的项目。";

        var summary = "已停止替换草稿。";
        try
        {
            await RunBusyAsync(async cancellationToken =>
            {
                summary = await RunDraftReplaceAsync(job, seriesId, cancellationToken);
            });
        }
        catch (OperationCanceledException)
        {
            summary = "已停止替换草稿。";
            SetKuaishouLogTone(job.ProjectDirectory, "none");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary = "替换失败：" + ex.Message;
            StatusMessage = summary;
            AppendActivityLog(summary);
            SetKuaishouLogTone(job.ProjectDirectory, "failed");
        }

        RefreshVisibleJobs(row.Id);
        return summary;
    }

    private async Task<string> RunDraftReplaceAsync(PublishJob job, string seriesId, CancellationToken cancellationToken)
    {
        KuaishouDraftReplacePreview preview;
        try
        {
            preview = await _kuaishouUploadService.PreviewReplaceAsync(job, seriesId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            AppendActivityLog(ex.Message);
            return ex.Message;
        }

        var busyName = ProjectUploadingSeries(preview.SeriesId, job.ProjectDirectory, job.Platform);
        if (busyName.Length > 0)
        {
            var busy = $"短剧 {preview.SeriesId} 正在被《{busyName}》上传，不能替换。";
            StatusMessage = busy;
            AppendActivityLog(busy);
            return busy;
        }

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var confirmed = await Dispatcher.UIThread.InvokeAsync(() =>
            ConfirmDialog.ShowAsync(
                owner,
                "新剧替换草稿",
                $"将用《{preview.LocalTitle}》替换平台上的《{preview.PlatformTitle}》（{preview.SeriesId}）。原标题、封面、简介和视频会被覆盖。不新建短剧。",
                560,
                240,
                "开始替换"));
        if (!confirmed)
        {
            const string cancelled = "已取消替换草稿。";
            StatusMessage = cancelled;
            return cancelled;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetKuaishouLogTone(job.ProjectDirectory, "running");
        var progress = new Progress<string>(message =>
        {
            StatusMessage = message;
            AppendActivityLog(message, preview.LocalTitle);
        });
        await _kuaishouUploadService.ReplaceDraftAsync(
            job,
            preview.SeriesId,
            preview.PlatformTitle,
            progress,
            cancellationToken);
        var state = LoadKuaishouUploadState(job.ProjectDirectory, job.Platform);
        if (state is null ||
            !string.Equals(state.Status, "completed", StringComparison.OrdinalIgnoreCase) ||
            state.CurrentStage == "already_on_platform")
        {
            var error = state?.LastError?.Trim() ?? "";
            var failed = error.Length == 0 ? "替换没有完成。" : "替换失败：" + error;
            StatusMessage = failed;
            AppendActivityLog(failed, preview.LocalTitle);
            SetKuaishouLogTone(job.ProjectDirectory, "failed");
            return failed;
        }

        var summary = $"替换完成：已用《{preview.LocalTitle}》替换《{preview.PlatformTitle}》（{preview.SeriesId}）。";
        StatusMessage = summary;
        AppendActivityLog(summary, preview.LocalTitle);
        SetKuaishouLogTone(job.ProjectDirectory, "ok");
        return summary;
    }

    private string ProjectUploadingSeries(string seriesId, string exceptDirectory, PublishPlatform platform)
    {
        var except = string.IsNullOrWhiteSpace(exceptDirectory) ? "" : Path.GetFullPath(exceptDirectory);
        foreach (var job in _jobs)
        {
            if (job.Platform != platform || string.IsNullOrWhiteSpace(job.ProjectDirectory))
                continue;
            string directory;
            try
            {
                directory = Path.GetFullPath(job.ProjectDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (except.Length > 0 && string.Equals(directory, except, StringComparison.OrdinalIgnoreCase))
                continue;
            KuaishouPersonalUploadState? state;
            try
            {
                state = LoadKuaishouUploadState(directory, platform);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (state is null ||
                !string.Equals(state.MiniSeriesId?.Trim(), seriesId, StringComparison.Ordinal) ||
                !string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase))
                continue;
            return string.IsNullOrWhiteSpace(job.ProjectName) ? Path.GetFileName(directory) : job.ProjectName.Trim();
        }

        return "";
    }
}
