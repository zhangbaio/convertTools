using Avalonia.Controls;
using Avalonia.Threading;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.Views;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task<string?> ResubmitKuaishouAuditTimeoutAsync(Window owner)
    {
        if (!IsKuaishouPlatform)
            return "请先切换到快手分账。";
        if (_activeGlobalAccount is not { } account)
            return "请先选择账号。";
        if (IsBusy)
            return "当前有任务在执行，请稍后再重提。";

        var platform = SelectedPlatform.Value;
        string? summary = null;
        try
        {
            await RunBusyAsync(async ct =>
            {
                var config = KuaishouPersonalConfig.Load(new PublishJob
                {
                    Platform = platform,
                    AccountId = account.Id,
                    AccountName = account.Name,
                    ProjectName = "审核超时重提",
                });
                if (string.IsNullOrWhiteSpace(config.AuthStatePath) || !File.Exists(config.AuthStatePath))
                {
                    summary = "请先打开经营者管理平台登录。";
                    return;
                }

                var progress = new Progress<string>(message =>
                {
                    StatusMessage = message;
                    AppendActivityLog(message);
                });
                var service = new KuaishouAuditTimeoutResubmitService(
                    new AccountJsonSettingStore(new PlatformDatabase(PlatformPublisherPaths.MainDatabasePath)), account.Id);
                using var session = KuaishouApiSession.Open(config.AuthStatePath);
                var targets = await service.ListAsync(session, progress, ct);
                ct.ThrowIfCancellationRequested();
                if (targets.Count == 0)
                {
                    summary = "当前账号没有查到审核超时的剧集。";
                    return;
                }

                var label = string.IsNullOrWhiteSpace(account.Name) ? account.Id : account.Name;
                var selected = await Dispatcher.UIThread.InvokeAsync(
                    () => KuaishouAuditTimeoutDialog.ShowAsync(owner, label, targets));
                if (selected is not { Length: > 0 })
                {
                    StatusMessage = "已取消审核超时重提。";
                    return;
                }

                ct.ThrowIfCancellationRequested();
                if (_activeGlobalAccount?.Id != account.Id || SelectedPlatform.Value != platform)
                {
                    summary = "当前账号已切换，请重新查询并勾选。";
                    return;
                }

                var results = await service.SubmitAsync(session, selected, progress, ct);
                var accepted = results.Count(item => item.Status == KuaishouAuditTimeoutSubmitStatus.Accepted);
                var pending = results.Count(item => item.Status == KuaishouAuditTimeoutSubmitStatus.PendingConfirmation);
                var failed = results.Count(item => item.Status == KuaishouAuditTimeoutSubmitStatus.Failed);
                var skipped = results.Count(item => item.Status == KuaishouAuditTimeoutSubmitStatus.Skipped);
                summary = $"已受理 {accepted} 部，待确认 {pending} 部，失败 {failed} 部，跳过 {skipped} 部，未执行 {selected.Length - results.Count} 部。";
                if (results.Count > 0)
                    summary += Environment.NewLine + string.Join(Environment.NewLine, results.Select(item => item.Message));
            });
        }
        catch (OperationCanceledException)
        {
            summary = "已停止审核超时重提。";
        }
        catch (Exception ex)
        {
            summary = $"审核超时重提失败：{ex.Message}";
        }

        if (summary is not null)
        {
            var headline = summary.Split(Environment.NewLine, 2)[0];
            StatusMessage = headline;
            AppendActivityLog(headline);
        }
        return summary;
    }
}
