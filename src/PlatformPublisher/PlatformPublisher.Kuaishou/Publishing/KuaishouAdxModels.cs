using System.Text.Json;
using PlatformPublisher.Adx.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouAdxPublishOptions
{
    public string TitleTemplate { get; set; } = "{新剧名}{排名}-{素材ID}";
    public string MaterialType { get; set; } = "高光";
    public string AuthorDeclaration { get; set; } = "含AI生成内容";
    public string CoverMode { get; set; } = "adx";
    public string CoverPath { get; set; } = string.Empty;
}

public sealed class KuaishouAdxPublishItem
{
    public string MaterialId { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string VideoPath { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public string ManifestPath { get; set; } = string.Empty;
}

public sealed class KuaishouAdxPublishPayload
{
    public string OriginalTitle { get; set; } = string.Empty;
    public string NewTitle { get; set; } = string.Empty;
    public KuaishouAdxPublishOptions Options { get; set; } = new();
    public List<KuaishouAdxPublishItem> Items { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this);

    public static KuaishouAdxPublishPayload FromJson(string json) =>
        JsonSerializer.Deserialize<KuaishouAdxPublishPayload>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("快手 ADX 发布任务配置为空。");
}

public enum KuaishouLocalAdxMaterialStatus { Available, Published, Missing, SubmissionUnknown }

public sealed record KuaishouLocalAdxMaterial(
    string SelectionId,
    string BatchId,
    string MaterialId,
    int Rank,
    string VideoPath,
    string? CoverPath,
    string ManifestPath,
    KuaishouLocalAdxMaterialStatus Status,
    DateTimeOffset? PublishedAt = null);

public static class KuaishouAdxIdentity
{
    public static string AccountKey(string accountId) => $"kuaishou-personal:{accountId.Trim()}";

    public static string FormatTitle(string template, string newTitle, string originalTitle, int rank, string materialId)
    {
        var value = (string.IsNullOrWhiteSpace(template) ? "{新剧名}{排名}-{素材ID}" : template)
            .Replace("{新剧名}", newTitle, StringComparison.Ordinal)
            .Replace("{原剧名}", originalTitle, StringComparison.Ordinal)
            .Replace("{排名}", rank.ToString(), StringComparison.Ordinal)
            .Replace("{素材ID}", materialId, StringComparison.Ordinal)
            .Trim();
        var suffix = "-" + materialId;
        if (!value.Contains(materialId, StringComparison.Ordinal)) value += suffix;
        if (value.Length <= 20) return value;
        if (suffix.Length >= 20) throw new InvalidOperationException("素材 ID 过长，无法生成可安全去重的快手标题。");
        var prefixLength = 20 - suffix.Length;
        var prefix = value.EndsWith(suffix, StringComparison.Ordinal)
            ? value[..^suffix.Length]
            : newTitle + rank;
        return prefix[..Math.Min(prefixLength, prefix.Length)] + suffix;
    }
}

public sealed class KuaishouAdxBatchResolver
{
    private readonly Adx.Storage.AdxBatchStore _store;
    public KuaishouAdxBatchResolver(Adx.Storage.AdxBatchStore store) => _store = store;

    public IReadOnlyList<KuaishouLocalAdxMaterial> List(string workflowDirectory, string accountId)
    {
        var accountKey = KuaishouAdxIdentity.AccountKey(accountId);
        return _store.ListInventory(workflowDirectory)
            .SelectMany(batch => batch.Items.Select(item => (Batch: batch, Item: item)))
            .GroupBy(value => value.Item.MaterialId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(value => value.Batch.CreatedAt).First())
            .OrderBy(value => value.Item.Rank)
            .Select(value =>
            {
                value.Batch.PublishByAccount.TryGetValue(accountKey, out var account);
                AdxItemPublishStatus? status = null;
                if (account is not null) account.Items.TryGetValue(value.Item.MaterialId, out status);
                var missing = string.IsNullOrWhiteSpace(value.Item.VideoPath) || !File.Exists(value.Item.VideoPath);
                var published = status?.Status is "success" or "draft_saved";
                var unknown = status?.Status == "submission_unknown";
                return new KuaishouLocalAdxMaterial(
                    $"{value.Batch.BatchId}:{value.Item.MaterialId}", value.Batch.BatchId,
                    value.Item.MaterialId, value.Item.Rank, value.Item.VideoPath,
                    value.Item.CoverPath, value.Batch.ManifestPath,
                    missing ? KuaishouLocalAdxMaterialStatus.Missing : published
                        ? KuaishouLocalAdxMaterialStatus.Published : unknown
                            ? KuaishouLocalAdxMaterialStatus.SubmissionUnknown : KuaishouLocalAdxMaterialStatus.Available,
                    published ? status!.UpdatedAt : null);
            }).ToArray();
    }

    public IReadOnlyList<KuaishouAdxPublishItem> Validate(
        string workflowDirectory, IEnumerable<KuaishouAdxPublishItem> requested, string fallbackCover)
    {
        var adxRoot = Path.GetFullPath(Path.Combine(workflowDirectory, "materials", "adx"));
        var result = new List<KuaishouAdxPublishItem>();
        foreach (var item in requested.GroupBy(value => value.MaterialId, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var manifest = _store.Read(item.ManifestPath)
                ?? throw new InvalidOperationException($"ADX 批次清单不存在：{item.ManifestPath}");
            EnsureWithin(adxRoot, manifest.ManifestPath, "ADX 批次清单");
            var stored = manifest.Items.FirstOrDefault(value =>
                value.MaterialId.Equals(item.MaterialId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"ADX 批次不包含素材：{item.MaterialId}");
            EnsureWithin(manifest.DownloadDirectory, stored.VideoPath, "素材视频");
            if (!File.Exists(stored.VideoPath) || new FileInfo(stored.VideoPath).Length == 0)
                throw new InvalidOperationException($"素材视频不存在或为空：{stored.VideoPath}");
            if (new FileInfo(stored.VideoPath).Length > 1024L * 1024 * 1024)
                throw new InvalidOperationException($"素材视频超过 1GiB：{Path.GetFileName(stored.VideoPath)}");
            if (!(new[] { ".mp4", ".mov", ".ogg", ".webm" }).Contains(
                    Path.GetExtension(stored.VideoPath), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"快手不支持该素材格式：{Path.GetFileName(stored.VideoPath)}");
            var cover = !string.IsNullOrWhiteSpace(stored.CoverPath) && File.Exists(stored.CoverPath)
                ? stored.CoverPath : fallbackCover;
            if (string.IsNullOrWhiteSpace(cover) || !File.Exists(cover) || new FileInfo(cover).Length == 0)
                throw new InvalidOperationException($"素材封面不存在：{item.MaterialId}");
            if (!string.IsNullOrWhiteSpace(stored.CoverPath)) EnsureWithin(manifest.DownloadDirectory, stored.CoverPath, "素材封面");
            result.Add(new KuaishouAdxPublishItem
            {
                MaterialId = stored.MaterialId, Rank = stored.Rank, VideoPath = stored.VideoPath,
                CoverPath = cover, ManifestPath = manifest.ManifestPath,
            });
        }
        if (result.Count == 0) throw new InvalidOperationException("没有选择可发布的 ADX 素材。");
        return result;
    }

    private static void EnsureWithin(string parentDirectory, string targetPath, string label)
    {
        var parent = Path.GetFullPath(parentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(targetPath);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{label}不属于所选 ADX 批次。");
    }
}
