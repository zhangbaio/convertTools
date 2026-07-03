using ShortDrama.Core.Models;

namespace TikTokPublisher.Core.Drama;

public sealed class DramaDownloadRunner
{
    private const int EpisodeConcurrent = 3;

    public async Task RunQueueAsync(
        IReadOnlyList<DramaDownloadQueueItem> items,
        int concurrency,
        Action<DramaDownloadQueueItem, string> log,
        Action<DramaDownloadQueueItem> onUpdated,
        CancellationToken ct)
    {
        var pending = items.Where(i => i.Status is "待下载" or "失败").ToList();
        if (pending.Count == 0) return;

        var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 10));
        var tasks = pending.Select(item => RunOneAsync(item, gate, log, onUpdated, ct)).ToList();
        await Task.WhenAll(tasks);
    }

    private static async Task RunOneAsync(
        DramaDownloadQueueItem item,
        SemaphoreSlim gate,
        Action<DramaDownloadQueueItem, string> log,
        Action<DramaDownloadQueueItem> onUpdated,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
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
                Concurrent: EpisodeConcurrent);

            var progress = new Progress<string>(msg =>
            {
                item.StatusDetail = msg;
                var percent = TryParseProgressPercent(msg);
                if (percent.HasValue)
                    item.Progress = $"{percent.Value}%";
                onUpdated(item);
            });

            var result = await ShortDramaDramaServices.Downloader.DownloadAsync(request, progress, ct);

            if (result.Ok)
            {
                item.Status = "已完成";
                item.Progress = "100%";
                item.Speed = "0 KB/s";
                item.StatusDetail = result.Message ?? $"共 {result.VideoCount} 集";
                item.CompletedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
                item.UpdatedAt = item.CompletedAt;
                onUpdated(item);
                log(item, $"[{item.Title}] {result.Message ?? "下载完成"}");
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
        finally
        {
            gate.Release();
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
