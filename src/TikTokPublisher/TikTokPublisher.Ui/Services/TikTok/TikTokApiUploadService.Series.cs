using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokApiUploadService
{
    public static async Task<PublishResult> PublishSeriesAsync(
        IPage page,
        PublishItem item,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        string? workflowDir,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
            throw new InvalidOperationException("接口上传：缺少封面文件，已停止。");

        var uploadPaths = payload.UploadVideoPaths.Count > 0 ? payload.UploadVideoPaths : payload.VideoPaths;
        if (ResolveLocalEpisodes(uploadPaths) is null)
            throw new InvalidOperationException("接口上传：本地视频文件名里有读不出集号或重复的集，已停止。");

        var seriesId = await ResolveSeriesIdAsync(page, item, payload.Title, options, log, ct);
        if (!string.IsNullOrWhiteSpace(workflowDir))
        {
            var draftUrl = $"https://www.tiktokdramacenter.com/series/draft/{seriesId}";
            if (TikTokUploadStateStore.TryRecordPlatformSeriesFromUrl(workflowDir, draftUrl, payload.Title, "api_upload"))
                log?.Invoke($"接口上传：已记录草稿 {draftUrl}");
        }

        log?.Invoke("接口上传：开始上传封面。");
        var coverUri = await UploadImageAsync(page, coverPath, options, "封面", log, ct);
        var copyrightFiles = await UploadCopyrightFilesAsync(page, options, log, ct);
        var audienceId = await ResolveAudienceTagIdAsync(page, recommendation.TargetAudience, ct);
        var themeIds = ResolveThemeTagIds(recommendation.Genres);
        var businessContractId = string.IsNullOrWhiteSpace(options.ContractId) ? null : options.ContractId.Trim();

        JsonObject? collection = null;
        using (var detail = await PageFetchAsync(page, "GET", $"/api/content-partner/series/{seriesId}", null, ct))
        {
            if (detail.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("collection", out var collectionElement) &&
                collectionElement.ValueKind == JsonValueKind.Object)
            {
                collection = JsonNode.Parse(collectionElement.GetRawText()) as JsonObject;
            }
        }

        var contract = TikTokApiSeriesDocument.Overlay(collection, new TikTokApiSeriesShell
        {
            Title = payload.Title,
            Description = payload.Description,
            CoverUri = coverUri,
            TotalVideoNum = Math.Max(payload.EpisodeCount, uploadPaths.Count),
            ProfilePreviewEpisodes = Math.Max(1, options.ProfilePreviewEpisodes),
            FreePreviewEpisodes = Math.Max(1, options.FreePreviewEpisodes),
            AnchorPromotionEnabled = options.AnchorPromotionEnabled,
            IsAiSeries = options.IsAiDrama,
            SourceLanguage = options.SourceLanguage,
            ThemeTagIds = themeIds,
            AudienceTagId = audienceId,
            ConsignmentEnabled = options.ConsignmentEnabled,
            BusinessContractId = businessContractId,
            PriceInUsd = options.PaidEnabled ? TikTokApiSeriesDocument.ReadPrice(options.ExpectedFullPriceValue) : null,
            IsOriginalRightsHolder = options.IsOriginalRightsHolder,
            IsAdaptation = string.Equals(options.ContentOriginalityType, "adapted", StringComparison.OrdinalIgnoreCase),
            CopyrightFiles = copyrightFiles.Count == 0 ? null : copyrightFiles,
        });
        var update = new JsonObject { ["contract"] = contract };
        if (contract["licenseId"] is JsonValue license &&
            license.TryGetValue<string>(out var licenseId) &&
            !string.IsNullOrWhiteSpace(licenseId))
        {
            update["licenseId"] = licenseId;
        }

        log?.Invoke($"接口上传：写入剧集资料，剧集 ID {seriesId}。");
        using (var _ = await PageFetchAsync(
            page,
            "PATCH",
            $"/api/content-partner/series/{seriesId}",
            update.ToJsonString(),
            ct))
        {
        }

        await SyncEpisodesAsync(page, seriesId, uploadPaths, options, log, ct);
        if (finalAction != FinalAction.Publish)
        {
            log?.Invoke("接口上传：剧集已保存，这次不提交。");
            return PublishResult.Success("接口上传已保存草稿");
        }

        log?.Invoke("接口上传：提交剧集。");
        try
        {
            using var _ = await PageFetchAsync(
                page,
                "POST",
                $"/api/content-partner/series/{seriesId}/submit",
                TikTokApiSeriesDocument.BuildSubmitBody(seriesId),
                ct);
        }
        catch (PageFetchException ex) when (ex.Message.Contains("3016009", StringComparison.Ordinal))
        {
            throw new TikTokDailyLimitException(ex.Message);
        }

        log?.Invoke($"接口上传完成：剧集 ID {seriesId} 已提交。");
        return PublishResult.Success("接口上传已提交");
    }

    internal static async Task<string> UploadImageAsync(
        IPage page,
        string path,
        TikTokPublishOptions options,
        string label,
        Action<string>? log,
        CancellationToken ct)
    {
        var fileSize = new FileInfo(path).Length;
        if (fileSize <= 0)
            throw new InvalidOperationException($"接口上传：文件为空：{path}");

        var retries = Math.Clamp(options.UploadBatchMaxRetries, 1, 10);
        using var http = await CreateHttpClientAsync(page, options);
        var sts = new StsCache(page, retries, log);
        using var gates = new UploadGates(1);
        var vodBase = ResolveServiceBaseUrl(page.Url, "vod");
        var imageBase = ResolveServiceBaseUrl(page.Url, "imagex");
        string sessionKey;
        await gates.Transfer.WaitAsync(ct);
        try
        {
            sessionKey = await TransferFileAsync(
                http,
                sts,
                gates,
                vodBase,
                imageBase,
                path,
                fileSize,
                log,
                0,
                retries,
                ct,
                applyAction: "ApplyImageUpload",
                fileType: "image",
                objectBase: imageBase,
                itemLabel: label);
        }
        finally
        {
            gates.Transfer.Release();
        }

        return await CommitImageAsync(http, sts, vodBase, sessionKey, label, retries, log, ct);
    }

    private static async Task<string> ResolveSeriesIdAsync(
        IPage page,
        PublishItem item,
        string title,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        var replaceTarget = item.ReplaceDraftTarget?.Trim() ?? "";
        if (replaceTarget.Length > 0)
        {
            var replaced = TryReadSeriesIdFromUrl(replaceTarget) ??
                           await TryReadExistingDraftIdAsync(page, replaceTarget, log, ct);
            return replaced ?? throw new InvalidOperationException(
                $"接口上传：没有找到要替换的草稿「{replaceTarget}」，已停止，不会新建另一部。");
        }

        var existing = await TryReadExistingDraftIdAsync(page, title, log, ct);
        if (existing is not null)
        {
            log?.Invoke($"接口上传：使用已有草稿 {existing}。");
            return existing;
        }

        if (item.ForceEditUpload)
            throw new InvalidOperationException("接口上传：未找到同名草稿，编辑剧集不会新建。");

        return await CreateSeriesAsync(
            page,
            title,
            await ResolveBusinessContractIdAsync(page, options, ct),
            log,
            ct);
    }

    private static async Task<string> CreateSeriesAsync(
        IPage page,
        string title,
        string? businessContractId,
        Action<string>? log,
        CancellationToken ct)
    {
        log?.Invoke("接口上传：新建剧集。");
        try
        {
            using var created = await PageFetchAsync(
                page,
                "POST",
                "/api/content-partner/series",
                TikTokApiSeriesDocument.BuildCreateBody(title, businessContractId),
                ct);
            var seriesId = ReadCreatedSeriesId(created.RootElement);
            log?.Invoke($"接口上传：已新建剧集 {seriesId}。");
            return seriesId;
        }
        catch (PageFetchException ex) when (ex.Message.Contains("3016009", StringComparison.Ordinal))
        {
            throw new TikTokDailyLimitException(ex.Message);
        }
        catch (PageFetchException ex) when (ex.Message.Contains("4097", StringComparison.Ordinal))
        {
            var existing = await TryReadExistingDraftIdAsync(page, title, log, ct);
            if (existing is not null)
            {
                log?.Invoke($"接口上传：平台已有同名剧集，使用 {existing}。");
                return existing;
            }

            throw new InvalidOperationException($"接口上传：新建剧集失败。{ex.Message}", ex);
        }
    }

    private static async Task<string?> ResolveBusinessContractIdAsync(
        IPage page,
        TikTokPublishOptions? options,
        CancellationToken ct)
    {
        var configured = options?.ContractId?.Trim() ?? "";
        if (configured.Length > 0)
            return configured;
        if (options is not null &&
            !string.Equals(options.ContractIdMode, TikTokPublishConstants.ContractIdModeFirstAvailable, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var listed = await PageFetchAsync(
            page,
            "POST",
            "/api/content-partner/institution/contract/list-not-used",
            """{"pageIndex":1,"pageSize":20}""",
            ct);
        return TikTokApiSeriesDocument.ReadFirstContractId(listed.RootElement);
    }

    private static async Task<string> ResolveAudienceTagIdAsync(
        IPage page,
        string targetAudience,
        CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(targetAudience) ? "female" : targetAudience.Trim();
        var labels = TikTokPublishConstants.TargetAudienceAliases.TryGetValue(key, out var aliases)
            ? aliases
            : TikTokPublishConstants.TargetAudienceAliases["female"];
        using var tags = await PageFetchAsync(
            page,
            "GET",
            "/api/content-partner/collections/tags?locale=zh-Hans",
            null,
            ct);
        var id = TikTokApiSeriesDocument.FindTagId(tags.RootElement, "ShortDramaTargetAudience", labels);
        return id ?? throw new InvalidOperationException(
            $"接口上传：平台标签里没有目标观众「{string.Join("/", labels)}」，已停止。");
    }

    private static IReadOnlyList<string> ResolveThemeTagIds(IReadOnlyList<string> genres)
    {
        var ids = new List<string>();
        foreach (var genre in genres)
        {
            if (string.IsNullOrWhiteSpace(genre))
                continue;
            if (!TikTokPublishConstants.TryResolveGenre(genre, out var option))
                throw new InvalidOperationException($"接口上传：题材「{genre}」对不上平台标签，已停止。");
            if (!ids.Contains(option.Value, StringComparer.Ordinal))
                ids.Add(option.Value);
        }

        if (ids.Count == 0)
            throw new InvalidOperationException("接口上传：没有可用的题材标签，已停止。");
        return ids;
    }

    private static async Task<IReadOnlyList<TikTokApiCopyrightFile>> UploadCopyrightFilesAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        var files = new List<TikTokApiCopyrightFile>();
        foreach (var materialType in options.CopyrightMaterialTypes)
        {
            var apiType = TikTokBrowserActions.CopyrightMaterialTypeByApiValue
                .FirstOrDefault(pair => string.Equals(pair.Value, materialType, StringComparison.Ordinal))
                .Key;
            if (apiType == 0)
                throw new InvalidOperationException($"接口上传：不认识的版权材料类型「{materialType}」，已停止。");

            var paths = options.ResolveCopyrightMaterialFilePaths(materialType);
            if (paths.Count == 0)
            {
                log?.Invoke($"接口上传：{TikTokPublishConstants.CopyrightMaterialLabels.GetValueOrDefault(materialType, materialType)} 没有本地文件，跳过。");
                continue;
            }

            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                log?.Invoke($"接口上传：上传版权材料 {name}。");
                var uri = await UploadImageAsync(page, path, options, name, log, ct);
                files.Add(new TikTokApiCopyrightFile(apiType, uri, name));
            }
        }

        return files;
    }

    private static string ReadCreatedSeriesId(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            foreach (var name in new[] { "contractId", "seriesId", "id" })
            {
                var value = ReadString(data, name);
                if (value.Length > 0)
                    return value;
                if (data.TryGetProperty("collection", out var collection))
                {
                    value = ReadString(collection, name);
                    if (value.Length > 0)
                        return value;
                }
            }
        }

        throw new InvalidOperationException("接口上传：新建剧集没有返回剧集 ID，已停止。");
    }

    private static async Task<string> CommitImageAsync(
        HttpClient http,
        StsCache sts,
        string vodBase,
        string sessionKey,
        string label,
        int retries,
        Action<string>? log,
        CancellationToken ct)
    {
        var commitBody = JsonSerializer.Serialize(new { SessionKey = sessionKey });
        var commitUri = new Uri(
            $"{vodBase}/?Action=CommitImageUpload&Version={ApiVersion}&ServiceId={ImageServiceId}");
        using var commitDocument = await ExecuteWithRetryAsync(
            retries,
            $"{label}提交图片",
            log,
            async token => await SendVodAsync(
                http, HttpMethod.Post, commitUri, await sts.GetAsync(token), commitBody, token),
            ct);
        return ReadImageUri(commitDocument.RootElement);
    }

    private static string ReadImageUri(JsonElement root)
    {
        if (root.TryGetProperty("Result", out var result))
        {
            foreach (var name in new[] { "Uri", "ImageUri", "StoreUri" })
            {
                var value = ReadString(result, name);
                if (value.Length > 0)
                    return value;
            }

            if (result.TryGetProperty("Results", out var results) &&
                results.ValueKind == JsonValueKind.Array &&
                results.GetArrayLength() > 0)
            {
                foreach (var name in new[] { "Uri", "ImageUri", "StoreUri", "PosterUri" })
                {
                    var value = ReadString(results[0], name);
                    if (value.Length > 0)
                        return value;
                }
            }
        }

        throw new InvalidOperationException("CommitImageUpload 没有返回图片地址。");
    }
}
