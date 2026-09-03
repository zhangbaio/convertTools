using PlatformPublisher.Common.Models;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouOnlineQueueItem
{
    public string Id { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public PublishPlatform Platform { get; set; }
    public string MiniSeriesId { get; set; } = string.Empty;
    public string AdvertiserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceDirectory { get; set; } = string.Empty;
    public string WorkflowDirectory { get; set; } = string.Empty;
    public string Status { get; set; } = "pending_audit";
    public int CheckedCount { get; set; }
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextCheckAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string LastError { get; set; } = string.Empty;
}

public sealed class KuaishouOnlineQueueStore
{
    private static readonly object Sync = new();
    private readonly AccountJsonSettingStore _store;

    public KuaishouOnlineQueueStore(AccountJsonSettingStore store) => _store = store;

    public IReadOnlyList<KuaishouOnlineQueueItem> Load(string accountId, PublishPlatform platform)
    {
        lock (Sync)
            return LoadCore(accountId, platform).Select(Clone).ToArray();
    }

    public KuaishouOnlineQueueItem? Register(
        PublishJob job,
        KuaishouPersonalProjectData data,
        KuaishouPersonalUploadState state,
        KuaishouPersonalConfig config)
    {
        if (!config.AutoOnlineEnabled && !config.StepOnlineSeries) return null;
        if (string.IsNullOrWhiteSpace(job.AccountId) || string.IsNullOrWhiteSpace(state.MiniSeriesId)) return null;

        lock (Sync)
        {
            var items = LoadCore(job.AccountId, job.Platform);
            var id = $"{config.AdvertiserId.Trim()}:{state.MiniSeriesId.Trim()}";
            var item = items.FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new KuaishouOnlineQueueItem { Id = id };
                items.Add(item);
            }
            item.AccountId = job.AccountId;
            item.Platform = job.Platform;
            item.MiniSeriesId = state.MiniSeriesId.Trim();
            item.AdvertiserId = config.AdvertiserId.Trim();
            item.Title = data.Title;
            item.SourceDirectory = data.SourceDirectory;
            item.WorkflowDirectory = data.WorkflowDirectory;
            item.Status = item.Status is "online" or "manual_online" ? item.Status : "pending_audit";
            item.SubmittedAt = state.UpdatedAt;
            item.NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(config.AutoOnlineIntervalMinutes, 1, 1440));
            item.UpdatedAt = DateTimeOffset.UtcNow;
            item.LastError = string.Empty;
            SaveCore(job.AccountId, job.Platform, items);
            return Clone(item);
        }
    }

    private List<KuaishouOnlineQueueItem> LoadCore(string accountId, PublishPlatform platform) =>
        _store.TryLoad<List<KuaishouOnlineQueueItem>>(accountId, Key(platform), out var items) && items is not null
            ? items
            : [];

    private void SaveCore(string accountId, PublishPlatform platform, IReadOnlyList<KuaishouOnlineQueueItem> items) =>
        _store.Save(accountId, Key(platform), items, schemaVersion: 1);

    private static string Key(PublishPlatform platform) => platform == PublishPlatform.KuaishouEnterpriseRevenue
        ? "kuaishou.enterprise.online.queue"
        : "kuaishou.personal.online.queue";

    private static KuaishouOnlineQueueItem Clone(KuaishouOnlineQueueItem item) => new()
    {
        Id = item.Id,
        AccountId = item.AccountId,
        Platform = item.Platform,
        MiniSeriesId = item.MiniSeriesId,
        AdvertiserId = item.AdvertiserId,
        Title = item.Title,
        SourceDirectory = item.SourceDirectory,
        WorkflowDirectory = item.WorkflowDirectory,
        Status = item.Status,
        CheckedCount = item.CheckedCount,
        SubmittedAt = item.SubmittedAt,
        NextCheckAt = item.NextCheckAt,
        UpdatedAt = item.UpdatedAt,
        LastError = item.LastError,
    };
}
