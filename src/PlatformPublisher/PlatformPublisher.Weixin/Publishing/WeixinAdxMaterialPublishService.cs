using System.Text.Json;
using System.Text.Json.Nodes;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Common.Models;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Weixin.Publishing;

public sealed class WeixinAdxMaterialPublishService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IWeixinChannelUploader _uploader;
    private readonly WeixinLocalVideoPublishService _localPublishService;
    private readonly AdxBatchStore _batchStore;

    public WeixinAdxMaterialPublishService(IWeixinChannelUploader uploader,
        WeixinLocalVideoPublishService localPublishService, AdxBatchStore batchStore)
    {
        _uploader = uploader;
        _localPublishService = localPublishService;
        _batchStore = batchStore;
    }

    public static AdxPublishPayload ReadPayload(PublishJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PlatformOptionsJson))
            throw new InvalidOperationException("ADX 发布任务缺少素材快照。");
        var payload = JsonSerializer.Deserialize<AdxPublishPayload>(job.PlatformOptionsJson, JsonOptions)
            ?? throw new InvalidOperationException("ADX 发布任务素材快照格式错误。");
        payload.Items = payload.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.MaterialId) && File.Exists(item.VideoPath))
            .GroupBy(item => item.MaterialId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (payload.Items.Count == 0) throw new InvalidOperationException("ADX 发布任务没有可用视频。");
        return payload;
    }

    public async Task PublishAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var payload = ReadPayload(job);
        var publishJob = CreatePublishJob(job, payload);
        var plan = _localPublishService.Prepare(publishJob);
        ApplyPerItemDescriptions(plan.ConfigPath, payload.Items);
        var byPath = payload.Items.ToDictionary(item => Path.GetFullPath(item.VideoPath), StringComparer.OrdinalIgnoreCase);
        progress?.Report($"ADX 素材发表：本次处理 {plan.PublishCount} 条，账号 {job.AccountName}。");
        var request = new WeixinUploadRequest(job.Id, job.ProjectDirectory, payload.NewTitle, plan.ConfigPath, Path.GetFileName(plan.ConfigPath))
        {
            MaterialItemCompleted = outcome =>
            {
                if (!byPath.TryGetValue(Path.GetFullPath(outcome.VideoPath), out var item)) return;
                _batchStore.RecordItem(item.ManifestPath, job.AccountId, item.MaterialId, outcome.Status, outcome.Message);
                progress?.Report($"ADX TOP 素材 {item.MaterialId}：{outcome.Message}");
            },
        };
        var result = await _uploader.UploadAsync(request, progress, cancellationToken);
        if (!result.Ok) throw new InvalidOperationException(result.Message ?? "ADX 素材发表失败。");
    }

    private static PublishJob CreatePublishJob(PublishJob source, AdxPublishPayload payload) => new()
    {
        Id = source.Id, Platform = source.Platform, Kind = PublishJobKind.AdxMaterials,
        ProjectName = string.IsNullOrWhiteSpace(payload.NewTitle) ? source.ProjectName : payload.NewTitle,
        ProjectDirectory = source.ProjectDirectory, ConfigPath = source.ConfigPath,
        AccountId = source.AccountId, AccountName = source.AccountName,
        AccountSessionDirectory = source.AccountSessionDirectory,
        DeclareOriginal = source.DeclareOriginal, HideLocation = source.HideLocation,
        AllowDuplicatePublish = source.AllowDuplicatePublish,
        PublishDescription = source.PublishDescription,
        PublishCount = payload.Items.Count,
        CustomVideoFiles = payload.Items.Select(item => item.VideoPath).ToList(),
        PlatformOptionsJson = payload.PublishOptionsJson,
    };

    private static void ApplyPerItemDescriptions(string configPath, IReadOnlyList<AdxPublishItem> items)
    {
        var root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
        var publish = root?["video_publish"]?.AsObject();
        if (root is null || publish is null) return;
        var descriptions = new JsonObject();
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item.Description)))
        {
            descriptions[item.VideoPath] = item.Description;
            descriptions[Path.GetFileName(item.VideoPath)] = item.Description;
        }
        publish["publish_video_description_map"] = descriptions;
        publish["replace_cover_with_local_image"] = items.Any(item => !string.IsNullOrWhiteSpace(item.CoverPath));
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
