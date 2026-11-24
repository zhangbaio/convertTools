using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Ui.Views;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task<string> ResubmitKuaishouCommitmentAsync()
    {
        if (!IsKuaishouPlatform)
            return "请先切换到快手分账。";
        if (_activeGlobalAccount is null)
            return "请先选择账号。";
        if (IsBusy)
            return "当前有任务在执行，请稍后再重新提交。";

        var summary = "已停止重新提交。";
        try
        {
            await RunBusyAsync(async cancellationToken =>
            {
                summary = await RunCommitmentResubmitAsync(cancellationToken);
            });
        }
        catch (OperationCanceledException)
        {
            summary = "已停止重新提交。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary = "重新提交失败：" + ex.Message;
            StatusMessage = summary;
            AppendActivityLog(summary);
        }

        return summary;
    }

    private async Task<string> RunCommitmentResubmitAsync(CancellationToken cancellationToken)
    {
        var account = _activeGlobalAccount;
        if (account is null)
            return "请先选择账号。";
        var config = KuaishouPersonalConfig.Load(new PublishJob
        {
            Platform = SelectedPlatform.Value,
            AccountId = account.Id,
            AccountName = account.Name,
            ProjectName = "重新提交",
        });
        if (string.IsNullOrWhiteSpace(config.AuthStatePath) || !File.Exists(config.AuthStatePath))
            return "请先打开经营者管理平台登录。";

        var progress = new Progress<string>(message =>
        {
            StatusMessage = message;
            AppendActivityLog(message);
        });
        using var session = KuaishouApiSession.Open(config.AuthStatePath);
        var service = new KuaishouCommitmentResubmitService();
        var found = await service.ListAsync(session, progress, cancellationToken);
        if (found.Count == 0)
        {
            const string none = "没有需要重新提交的短剧。";
            StatusMessage = none;
            AppendActivityLog(none);
            return none;
        }

        var accountName = string.IsNullOrWhiteSpace(account.Name) ? account.Id : account.Name;
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var confirmed = await Dispatcher.UIThread.InvokeAsync(() =>
            ConfirmDialog.ShowAsync(
                owner,
                "重新提交",
                $"账号「{accountName}」有 {found.Count} 部已上架短剧带有承诺函未加盖公章的提示。将按原内容重新提交审核，不修改封面、简介、视频和承诺函。",
                560,
                240));
        if (!confirmed)
        {
            const string cancelled = "已取消重新提交。";
            StatusMessage = cancelled;
            return cancelled;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await service.SubmitAsync(session, found, progress, cancellationToken);
        var summary = $"重新提交结束：已提交 {result.Submitted} 部，失败 {result.Failed} 部。";
        if (result.StoppedByDailyLimit)
            summary += $"{KuaishouDailyUploadLimit.Detail}，其余未再提交。";
        StatusMessage = summary;
        AppendActivityLog(summary);
        var details = result.Lines.Where(line => !line.EndsWith("已重新提交。", StringComparison.Ordinal)).ToArray();
        return details.Length == 0 ? summary : summary + Environment.NewLine + string.Join(Environment.NewLine, details);
    }
}
