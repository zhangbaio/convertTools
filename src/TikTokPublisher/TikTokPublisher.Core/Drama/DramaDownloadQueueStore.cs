using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Drama;

public static class DramaDownloadQueueStore
{
    public static DramaDownloadQueueState Load(string? databasePath = null)
    {
        if (AppSettingStore.TryLoadJson<DramaDownloadQueueState>(
                DramaDownloadQueueState.SettingKey, out var state, databasePath)
            && state is not null)
        {
            Normalize(state);
            return state;
        }

        return new DramaDownloadQueueState();
    }

    public static void Save(DramaDownloadQueueState state, string? databasePath = null)
    {
        Normalize(state);
        AppSettingStore.SaveJson(DramaDownloadQueueState.SettingKey, state, databasePath);
    }

    public static void Normalize(DramaDownloadQueueState state)
    {
        var previousVersion = state.Version;
        state.Version = DramaDownloadQueueState.CurrentVersion;
        state.QueueItems = state.QueueItems
            .Select(NormalizeItem)
            .Where(i => !string.IsNullOrWhiteSpace(i.Title) || !string.IsNullOrWhiteSpace(i.BookId))
            .ToList();
        state.DownloadConcurrent = Math.Clamp(state.DownloadConcurrent, 1, 10);
        if (string.IsNullOrWhiteSpace(state.DefaultQuality))
            state.DefaultQuality = "1080P";
        if (state.DownloadEpisodeNumberMode is not ("source" or "continuous"))
            state.DownloadEpisodeNumberMode = "source";
        state.CategoryInclude ??= "";
        state.CategoryExclude ??= "";
        if (state.AuthorExclude is null ||
            (previousVersion < DramaDownloadQueueState.CurrentVersion &&
             string.IsNullOrWhiteSpace(state.AuthorExclude)))
        {
            state.AuthorExclude = DramaDownloadQueueState.DefaultAuthorExclude;
        }
    }

    private static DramaDownloadQueueItem NormalizeItem(DramaDownloadQueueItem item)
    {
        if (item.Status is "下载中" or "生成素材中" or "生成派生产物中" or "已下载" or "解析链接中" or "校验文件")
        {
            item.Status = "待下载";
            item.Progress = "0%";
            item.Speed = "0 KB/s";
            if (string.IsNullOrWhiteSpace(item.LastError))
                item.LastError = "上次任务在应用关闭前未完成，已重置为待下载";
        }

        if (string.Equals(item.Status, "已完成", StringComparison.Ordinal))
            item.Status = "完成";

        if (item.Quality is not ("1080P+" or "1080P" or "720P" or "480P"))
            item.Quality = "1080P";
        if (item.EpisodeNumberMode is not ("source" or "continuous"))
            item.EpisodeNumberMode = "source";
        return item;
    }
}
