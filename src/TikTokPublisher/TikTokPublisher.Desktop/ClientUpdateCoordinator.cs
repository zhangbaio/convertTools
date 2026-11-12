using TikTokPublisher.Core.Updating;
using Velopack;

namespace TikTokPublisher.Desktop;

internal static class ClientUpdateCoordinator
{
    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;

    public static void Bind(Func<bool> queueRunning)
    {
        ClientUpdateRuntime.IsQueueRunning = queueRunning;
        ClientUpdateRuntime.DownloadAsync = DownloadAsync;
        ClientUpdateRuntime.Restart = Restart;
    }

    private static async Task<ClientUpdateDownloadResult> DownloadAsync(string feedUrl, CancellationToken cancellationToken)
    {
        var manager = new UpdateManager(feedUrl);
        if (!manager.IsInstalled)
        {
            return new ClientUpdateDownloadResult(false, false, true, "", "当前不是自动升级安装，不能直接替换程序。");
        }

        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
            return new ClientUpdateDownloadResult(false, false, false, manager.CurrentVersion?.ToString() ?? "", "已是最新版本。");

        await manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken).ConfigureAwait(false);
        _manager = manager;
        _pending = update;
        var version = update.TargetFullRelease.Version.ToString();
        return new ClientUpdateDownloadResult(true, true, false, version, $"已下载 {version}。");
    }

    private static void Restart()
    {
        if (_manager is null || _pending is null)
            return;
        if (ClientUpdateRuntime.IsQueueRunning?.Invoke() == true)
            return;
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
