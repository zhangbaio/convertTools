using ShortDrama.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Drama;

public sealed class DramaDownloadRunner
{
    public async Task RunQueueAsync(
        IReadOnlyList<DramaDownloadQueueItem> items,
        int downloadConcurrent,
        Action<DramaDownloadQueueItem, string> log,
        Action<DramaDownloadQueueItem> onUpdated,
        CancellationToken ct)
    {
        var pending = items.Where(i => i.Status is "待下载" or "失败" or "素材校验失败").ToList();
        if (pending.Count == 0) return;

        var concurrent = Math.Clamp(downloadConcurrent, 1, 10);
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            await RunOneAsync(item, concurrent, log, onUpdated, ct).ConfigureAwait(false);
        }
    }

    private static async Task RunOneAsync(
        DramaDownloadQueueItem item,
        int downloadConcurrent,
        Action<DramaDownloadQueueItem, string> log,
        Action<DramaDownloadQueueItem> onUpdated,
        CancellationToken ct)
    {
        try
        {
            item.Status = "下载中";
            item.StatusDetail = "准备下载…";
            item.Progress = "0%";
            item.LastError = "";
            onUpdated(item);
            log(item, $"[{item.Title}] 开始下载");

            var projectDir = Path.GetFullPath(item.ProjectDir);
            Directory.CreateDirectory(projectDir);

            var request = new DramaDownloadRequest(
                ProjectDir: projectDir,
                OutputDir: projectDir,
                DisplayName: item.Title,
                BookId: item.BookId,
                Episodes: item.Episodes,
                Quality: string.IsNullOrWhiteSpace(item.Quality) ? "1080P+" : item.Quality,
                Concurrent: downloadConcurrent,
                EpisodeNumberMode: string.IsNullOrWhiteSpace(item.EpisodeNumberMode) ? "source" : item.EpisodeNumberMode);

            var progress = new Progress<string>(msg =>
            {
                item.StatusDetail = msg;
                var percent = TryParseProgressPercent(msg);
                if (percent.HasValue)
                    item.Progress = $"{percent.Value}%";
                onUpdated(item);
            });

            var settings = ClientSettingsStore.Load();
            using var downloadSlot = await QueueDownloadSlotCoordinator.WaitAsync(
                settings.DramaDownloadMaxParallelProjects,
                item.Title,
                message => log(item, message),
                ct).ConfigureAwait(false);

            var result = await ShortDramaDramaServices.Downloader.DownloadAsync(request, progress, ct);

            if (result.Ok)
            {
                item.Status = "已下载";
                item.Progress = "95%";
                item.Speed = "-";
                item.StatusDetail = result.Message ?? $"共下载 {result.VideoCount} 集";
                onUpdated(item);
                log(item, $"[{item.Title}] {result.Message ?? "下载完成"}");

                if (item.GenerateMaterials)
                {
                    item.Status = "生成派生产物中";
                    item.Progress = "96%";
                    item.StatusDetail = "正在生成派生产物";
                    onUpdated(item);
                    ProjectWorkspaceService.EnsureWorkflowInfo(projectDir, Math.Max(1, result.VideoCount), message => log(item, message));
                }

                item.Status = "完成";
                item.Progress = "100%";
                item.Speed = "-";
                item.StatusDetail = $"共完成 {result.VideoCount} 集";
                item.CompletedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
                item.UpdatedAt = item.CompletedAt;
                onUpdated(item);
            }
            else
            {
                throw new InvalidOperationException(result.Message ?? "下载失败");
            }
        }
        catch (OperationCanceledException)
        {
            item.Status = "已停止";
            item.StatusDetail = "用户停止";
            onUpdated(item);
            throw;
        }
        catch (Exception ex)
        {
            item.Status = "失败";
            item.LastError = ex.Message;
            item.StatusDetail = ex.Message;
            onUpdated(item);
            log(item, $"[{item.Title}] 下载失败：{ex.Message}");
        }
    }

    private static int? TryParseProgressPercent(string message)
    {
        var idx = message.IndexOf('%');
        if (idx <= 0) return null;
        var start = idx - 1;
        while (start >= 0 && (char.IsDigit(message[start]) || message[start] == '.'))
            start--;
        var slice = message[(start + 1)..idx];
        return double.TryParse(slice, out var value) ? (int)Math.Round(value) : null;
    }
}
