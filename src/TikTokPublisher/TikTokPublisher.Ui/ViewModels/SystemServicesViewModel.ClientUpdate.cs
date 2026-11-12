using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikTokPublisher.Core.Updating;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class SystemServicesViewModel
{
    public Func<string, Task<bool>>? ConfirmClientUpdate { get; set; }

    [ObservableProperty] private string _clientUpdateStatus = "尚未检查更新。";
    [ObservableProperty] private string _clientUpdateDetail = "";
    [ObservableProperty] private bool _clientUpdateCanRestart;
    [ObservableProperty] private bool _clientUpdateBusy;

    public Task CheckClientUpdateOnStartupAsync() => CheckClientUpdateCoreAsync();

    [RelayCommand]
    private Task CheckClientUpdate() => CheckClientUpdateCoreAsync();

    [RelayCommand]
    private void RestartClientUpdate()
    {
        if (ClientUpdateRuntime.IsQueueRunning?.Invoke() == true)
        {
            ClientUpdateCanRestart = false;
            ClientUpdateStatus = "队列还在运行，结束后再重启升级。";
            return;
        }

        ClientUpdateRuntime.Restart?.Invoke();
    }

    private async Task CheckClientUpdateCoreAsync()
    {
        if (ClientUpdateBusy)
            return;
        ClientUpdateBusy = true;
        try
        {
            var serverUrl = AuthServerUrl.Trim();
            if (serverUrl.Length == 0)
            {
                ClientUpdateStatus = "还没有授权服务器地址，无法检查更新。";
                ClientUpdateCanRestart = false;
                return;
            }

            ClientUpdateStatus = "正在检查更新…";
            ClientUpdateOffer? offer;
            try
            {
                offer = await ClientUpdateFeed.FetchAsync(serverUrl, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ClientUpdateStatus = $"检查更新失败：{ex.Message}";
                ClientUpdateCanRestart = false;
                return;
            }

            if (offer is null)
            {
                ClientUpdateStatus = "管理系统还没有发布可升级的版本。";
                ClientUpdateDetail = "";
                ClientUpdateCanRestart = false;
                return;
            }

            ClientUpdateDetail = string.IsNullOrWhiteSpace(offer.Notes)
                ? offer.SetupUrl
                : $"{offer.Notes}\n{offer.SetupUrl}".Trim();
            if (ClientUpdateRuntime.DownloadAsync is null)
            {
                ClientUpdateStatus = $"服务器版本 {offer.Version}。当前运行方式不能自动替换程序。";
                ClientUpdateCanRestart = false;
                return;
            }

            ClientUpdateDownloadResult downloaded;
            try
            {
                downloaded = await ClientUpdateRuntime.DownloadAsync(offer.FeedUrl, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ClientUpdateStatus = $"下载更新失败：{ex.Message}";
                ClientUpdateCanRestart = false;
                return;
            }

            if (downloaded.NeedsInstaller)
            {
                ClientUpdateStatus = $"服务器版本 {offer.Version}。请先安装自动升级包，之后才能直接更新。";
                ClientUpdateCanRestart = false;
                return;
            }

            if (!downloaded.Found)
            {
                ClientUpdateStatus = string.IsNullOrWhiteSpace(downloaded.Message) ? "已是最新版本。" : downloaded.Message;
                ClientUpdateCanRestart = false;
                return;
            }

            var queueRunning = ClientUpdateRuntime.IsQueueRunning?.Invoke() == true;
            ClientUpdateCanRestart = downloaded.Downloaded && !queueRunning;
            if (queueRunning)
            {
                ClientUpdateStatus = $"已下载 {downloaded.Version}。队列结束后再重启升级。";
                return;
            }

            ClientUpdateStatus = $"已下载 {downloaded.Version}，可以重启升级。";
            if (ConfirmClientUpdate is null || !ClientUpdateRuntime.ShouldPromptRestart(downloaded.Downloaded, queueRunning))
                return;
            if (await ConfirmClientUpdate($"服务器版本 {downloaded.Version} 已下载。现在重启并完成升级？"))
                ClientUpdateRuntime.Restart?.Invoke();
        }
        finally
        {
            ClientUpdateBusy = false;
        }
    }
}
