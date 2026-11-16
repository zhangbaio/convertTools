using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokApiUploadService
{
    public static async Task<PublishResult> PublishWithSavedLoginAsync(
        string storageStatePath,
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
        if (string.IsNullOrWhiteSpace(storageStatePath) || !File.Exists(storageStatePath))
            throw new InvalidOperationException("接口上传：没有保存的登录状态，请先在浏览器页登录该账号。");

        using var session = PartnerSession.Open(storageStatePath, options);
        var previous = PartnerSession.Current.Value;
        PartnerSession.Current.Value = session;
        try
        {
            return await PublishSeriesAsync(
                null,
                item,
                payload,
                options,
                recommendation,
                coverPath,
                workflowDir,
                finalAction,
                log,
                ct);
        }
        finally
        {
            PartnerSession.Current.Value = previous;
        }
    }

    public static async Task<PublishResult> PublishSeriesAsync(
        IPage? page,
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

        var replacingDraft = !string.IsNullOrWhiteSpace(item.ReplaceDraftTarget);
        var contractBinding = replacingDraft
            ? new TikTokApiSeriesDocument.TikTokSeriesContractBinding(null, null)
            : await ResolveSeriesContractAsync(page, options, log, ct);
        var seriesId = await ResolveSeriesIdAsync(page, item, payload.Title, contractBinding, log, ct);
        if (replacingDraft)
        {
            log?.Invoke(
                $"新剧替换草稿：使用接口替换「{item.ReplaceDraftTarget.Trim()}」，剧集 ID {seriesId}。合同保持草稿原值。");
            await ClearDraftVideosAsync(page, seriesId, options, log, ct);
        }
        if (!string.IsNullOrWhiteSpace(workflowDir))
        {
            var draftUrl = TikTokApiSites.SeriesDraftUrl(options.ApiSite, seriesId);
            if (TikTokUploadStateStore.TryRecordPlatformSeriesFromUrl(workflowDir, draftUrl, payload.Title, "api_upload"))
                log?.Invoke($"接口上传：已记录草稿 {draftUrl}");
        }

        log?.Invoke("接口上传：开始上传封面。");
        var coverUri = await UploadImageAsync(page, coverPath, options, "封面", log, ct);
        var audienceId = await ResolveAudienceTagIdAsync(page, recommendation.TargetAudience, ct);
        var themeIds = ResolveThemeTagIds(recommendation.Genres);
        var episodeCount = Math.Max(payload.EpisodeCount, uploadPaths.Count);
        var collection = await ReadCollectionAsync(page, seriesId, ct);
        var publishers = await ResolvePublisherIdsAsync(page, collection, options, ct);
        var publisherIds = publishers.Select(publisher => publisher.UserId).ToArray();

        LogSeriesForm(
            log,
            payload,
            options,
            recommendation,
            contractBinding,
            coverPath,
            episodeCount,
            publishers,
            replacingDraft ? ReadDraftContractLabel(collection) : null);
        await WriteSeriesAsync(
            page,
            seriesId,
            collection,
            BuildShell(payload, coverUri, episodeCount, audienceId, themeIds, publisherIds, contractBinding, options, copyrightFiles: null, signed: false),
            "写入剧名、简介、封面和发布设置",
            log,
            ct);

        await SyncEpisodesAsync(page, seriesId, uploadPaths, options, log, ct);

        log?.Invoke("接口上传：开始上传证明材料。");
        var copyrightFiles = await UploadCopyrightFilesAsync(page, options, log, ct);
        LogCopyrightForm(log, copyrightFiles);
        collection = await ReadCollectionAsync(page, seriesId, ct);
        await WriteSeriesAsync(
            page,
            seriesId,
            collection,
            BuildShell(payload, coverUri, episodeCount, audienceId, themeIds, publisherIds, contractBinding, options, copyrightFiles, signed: true),
            "写入证明材料和本人承诺",
            log,
            ct);

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

    private readonly record struct ChosenPublisher(string UserId, string Label);

    private static TikTokApiSeriesShell BuildShell(
        TikTokPublishPayload payload,
        string coverUri,
        int episodeCount,
        string audienceId,
        IReadOnlyList<string> themeIds,
        IReadOnlyList<string> publisherIds,
        TikTokApiSeriesDocument.TikTokSeriesContractBinding contractBinding,
        TikTokPublishOptions options,
        IReadOnlyList<TikTokApiCopyrightFile>? copyrightFiles,
        bool signed)
    {
        return new TikTokApiSeriesShell
        {
            Title = payload.Title,
            Description = payload.Description,
            CoverUri = coverUri,
            TotalVideoNum = episodeCount,
            ProfilePreviewEpisodes = Math.Max(1, options.ProfilePreviewEpisodes),
            FreePreviewEpisodes = Math.Max(1, options.FreePreviewEpisodes),
            AnchorPromotionEnabled = options.AnchorPromotionEnabled,
            IsAiSeries = options.IsAiDrama,
            SourceLanguage = options.SourceLanguage,
            ThemeTagIds = themeIds,
            AudienceTagId = audienceId,
            ConsignmentEnabled = options.ConsignmentEnabled,
            BusinessContractId = contractBinding.ContractedId,
            LicenseId = contractBinding.LicenseId,
            PriceInUsd = options.PaidEnabled ? TikTokApiSeriesDocument.ReadPrice(options.ExpectedFullPriceValue) : null,
            IsOriginalRightsHolder = options.IsOriginalRightsHolder,
            IsAdaptation = string.Equals(options.ContentOriginalityType, "adapted", StringComparison.OrdinalIgnoreCase),
            CopyrightFiles = copyrightFiles is { Count: > 0 } ? copyrightFiles : null,
            PublisherIds = publisherIds,
            Signed = signed,
            PublishMode = TikTokApiSeriesDocument.ResolvePublishMode(options.PublishMode),
        };
    }

    private static string ReadDraftContractLabel(JsonObject? collection)
    {
        var licenseId = ReadNodeString(collection, "licenseId");
        if (!string.IsNullOrWhiteSpace(licenseId))
            return licenseId;
        var contractedId = ReadNodeString(collection, "contractedId");
        return string.IsNullOrWhiteSpace(contractedId) ? "保持草稿原值" : contractedId;
    }

    private static string? ReadNodeString(JsonObject? node, string name)
    {
        if (node?[name] is JsonValue value && value.TryGetValue<string>(out var text))
            return text?.Trim();
        return null;
    }

    private static async Task<JsonObject?> ReadCollectionAsync(IPage? page, string seriesId, CancellationToken ct)
    {
        using var detail = await PageFetchAsync(page, "GET", $"/api/content-partner/series/{seriesId}", null, ct);
        if (detail.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("collection", out var collectionElement) &&
            collectionElement.ValueKind == JsonValueKind.Object)
        {
            return JsonNode.Parse(collectionElement.GetRawText()) as JsonObject;
        }

        return null;
    }

    private static async Task WriteSeriesAsync(
        IPage? page,
        string seriesId,
        JsonObject? collection,
        TikTokApiSeriesShell shell,
        string step,
        Action<string>? log,
        CancellationToken ct)
    {
        var contract = TikTokApiSeriesDocument.Overlay(collection, shell);
        var update = new JsonObject { ["contract"] = contract };
        if (contract["licenseId"] is JsonValue license &&
            license.TryGetValue<string>(out var licenseId) &&
            !string.IsNullOrWhiteSpace(licenseId))
        {
            update["licenseId"] = licenseId;
        }

        log?.Invoke($"接口上传：{step}，剧集 ID {seriesId}。");
        using var _ = await PageFetchAsync(
            page,
            "PATCH",
            $"/api/content-partner/series/{seriesId}",
            update.ToJsonString(),
            ct);
    }

    private static void LogSeriesForm(
        Action<string>? log,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        TikTokApiSeriesDocument.TikTokSeriesContractBinding contractBinding,
        string coverPath,
        int episodeCount,
        IReadOnlyList<ChosenPublisher> publishers,
        string? contractLabel = null)
    {
        void Field(string name, string? value)
        {
            var text = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            log?.Invoke($"接口上传：表单「{name}」{(text.Length == 0 ? "空" : text)}");
        }

        if (contractLabel is null)
        {
            contractLabel = contractBinding.LicenseId?.Trim();
            if (string.IsNullOrEmpty(contractLabel))
                contractLabel = contractBinding.ContractedId?.Trim();
        }

        Field("合同", contractLabel);
        Field("剧名", payload.Title);
        Field("简介", payload.Description);
        Field("封面", Path.GetFileName(coverPath));
        Field("自动挂载锚点", options.AnchorPromotionEnabled ? "已开启" : "未开启");
        Field("目标观众", AudienceLabel(recommendation.TargetAudience));
        Field("题材类型", string.Join("、", recommendation.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre))));
        Field("源语言", options.SourceLanguageLabels.FirstOrDefault() ?? options.SourceLanguage);
        Field("总集数", Math.Max(1, episodeCount).ToString());
        if (publishers.Count == 0)
            Field("发布账号", "");
        else
        {
            foreach (var publisher in publishers)
                Field("发布账号", publisher.Label);
        }

        Field("是否 AI 短剧", options.IsAiDrama ? "是" : "否");
        Field("内容创作类型", CreationLabel(options.ContentOriginalityType));
        Field("发布方式", options.PublishModeLabel);
        Field("托管模式", options.ConsignmentEnabled ? "已开启" : "未开启");
        var profilePreview = Math.Max(1, options.ProfilePreviewEpisodes);
        if (!options.PaidEnabled)
        {
            Field("商业模式", $"免费，个人页展示 {profilePreview} 集");
            return;
        }

        var price = TikTokApiSeriesDocument.ReadPrice(options.ExpectedFullPriceValue);
        Field(
            "商业模式",
            $"付费，个人页展示 {profilePreview} 集，免费预览 {Math.Max(1, options.FreePreviewEpisodes)} 集，全集价格 {(string.IsNullOrEmpty(price) ? "未填" : price)}");
    }

    private static void LogCopyrightForm(Action<string>? log, IReadOnlyList<TikTokApiCopyrightFile> copyrightFiles)
    {
        void Field(string name, string? value)
        {
            var text = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            log?.Invoke($"接口上传：表单「{name}」{(text.Length == 0 ? "空" : text)}");
        }

        Field("本人承诺", "已勾选");
        if (copyrightFiles.Count == 0)
        {
            Field("版权材料", "");
            return;
        }

        foreach (var group in copyrightFiles.GroupBy(file => file.MaterialType))
        {
            var key = TikTokBrowserActions.CopyrightMaterialTypeByApiValue.GetValueOrDefault(group.Key, "");
            var label = key.Length == 0
                ? group.Key.ToString()
                : TikTokPublishConstants.CopyrightMaterialFormTitle(key);
            Field(label, string.Join("、", group.Select(file => file.FileName)));
        }
    }

    private static string AudienceLabel(string? audience)
    {
        var key = string.IsNullOrWhiteSpace(audience) ? "female" : audience.Trim();
        return TikTokPublishConstants.TargetAudienceAliases.TryGetValue(key, out var labels) && labels.Count > 0
            ? labels[0]
            : key;
    }

    private static string CreationLabel(string? type)
    {
        var key = TikTokPublishConstants.NormalizeContentCreationType(type);
        return TikTokPublishConstants.ContentCreationTypeLabels.TryGetValue(key, out var labels) && labels.Count > 0
            ? labels[0]
            : key;
    }

    private static async Task<IReadOnlyList<ChosenPublisher>> ResolvePublisherIdsAsync(
        IPage? page,
        JsonObject? collection,
        TikTokPublishOptions options,
        CancellationToken ct)
    {
        var institutionId = collection?["institutionId"] is JsonValue institution &&
            institution.TryGetValue<string>(out var institutionText)
            ? institutionText.Trim()
            : "";
        if (institutionId.Length == 0)
            throw new InvalidOperationException("接口上传：剧集里没有机构 ID，无法读取发布账号。");

        using var detail = await PageFetchAsync(
            page,
            "POST",
            "/api/content-partner/institution/detail",
            new JsonObject
            {
                ["institutionID"] = institutionId,
                ["withCreatorList"] = true,
                ["withAccountList"] = false,
                ["withRegisterTaskStatus"] = false,
            }.ToJsonString(),
            ct);
        var accounts = TikTokApiSeriesDocument.ReadPublisherAccounts(detail.RootElement);
        var ids = TikTokApiSeriesDocument.SelectPublisherIds(
            accounts,
            options.CustomPublishAccountsEnabled,
            options.SelectedPublishAccountKeys);
        return ids
            .Select(id =>
            {
                var account = accounts.FirstOrDefault(item => item.UserId == id);
                var label = account.UserId.Length == 0
                    ? id
                    : TikTokApiSeriesDocument.DescribePublisher(account);
                return new ChosenPublisher(id, label);
            })
            .ToArray();
    }

    internal static async Task<string> UploadImageAsync(
        IPage? page,
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
        var site = TikTokApiSites.Resolve(options.ApiSite, CurrentPageUrl(page));
        var vodBase = TikTokApiSites.ImageApplyBase(site);
        var imageBase = TikTokApiSites.ImageObjectFallback(site);
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
        IPage? page,
        PublishItem item,
        string title,
        TikTokApiSeriesDocument.TikTokSeriesContractBinding contractBinding,
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
            contractBinding,
            log,
            ct);
    }

    private static async Task<string> CreateSeriesAsync(
        IPage? page,
        string title,
        TikTokApiSeriesDocument.TikTokSeriesContractBinding contractBinding,
        Action<string>? log,
        CancellationToken ct)
    {
        var licenseId = contractBinding.LicenseId?.Trim() ?? "";
        var contractedId = contractBinding.ContractedId?.Trim() ?? "";
        if (licenseId.Length == 0 && !TikTokApiSeriesDocument.IsPlatformContractId(contractedId))
        {
            throw new InvalidOperationException(
                "接口上传：没有可用的合同，已停止。请在「账号管理 → 发布配置」填写带 CT 的合同 ID。");
        }

        log?.Invoke(licenseId.Length > 0
            ? $"接口上传：使用授权合同 {licenseId} 新建剧集。"
            : $"接口上传：使用平台合同 {contractedId} 新建剧集。");
        try
        {
            using var created = await PageFetchAsync(
                page,
                "POST",
                "/api/content-partner/series",
                TikTokApiSeriesDocument.BuildCreateBody(title, contractBinding),
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

    private static async Task<TikTokApiSeriesDocument.TikTokSeriesContractBinding> ResolveSeriesContractAsync(
        IPage? page,
        TikTokPublishOptions? options,
        Action<string>? log,
        CancellationToken ct)
    {
        var configured = options?.ContractId?.Trim() ?? "";
        if (TikTokApiSeriesDocument.IsPlatformContractId(configured))
            return new TikTokApiSeriesDocument.TikTokSeriesContractBinding(configured, null);

        if (configured.Length > 0)
        {
            var licenseId = await ReadMatchedLicenseAsync(page, configured, ct);
            if (licenseId is not null)
            {
                log?.Invoke($"接口上传：合同编号「{configured}」使用授权合同 {licenseId}。");
                return new TikTokApiSeriesDocument.TikTokSeriesContractBinding(null, licenseId);
            }

            var keywordBody = JsonSerializer.Serialize(new { contractKeyWord = configured });
            var match = await ReadMatchedContractAsync(page, keywordBody, configured, ct)
                        ?? await ReadMatchedContractAsync(page, "{}", configured, ct);
            if (match is { } found)
            {
                log?.Invoke(found.Title.Length > 0
                    ? $"接口上传：合同编号「{configured}」对应「{found.Title}」。"
                    : $"接口上传：合同编号「{configured}」对应平台合同 {found.Id}。");
                return new TikTokApiSeriesDocument.TikTokSeriesContractBinding(found.Id, null);
            }

            throw new InvalidOperationException(
                $"接口上传：合同编号「{configured}」没有出现在授权合同列表里，已停止。请保留 CT 前缀，并核对「账号管理 → 发布配置」里的合同 ID。");
        }

        return new TikTokApiSeriesDocument.TikTokSeriesContractBinding(null, null);
    }

    private static async Task<string?> ReadMatchedLicenseAsync(
        IPage? page,
        string configured,
        CancellationToken ct)
    {
        using var listed = await PageFetchAsync(
            page,
            "POST",
            "/api/content-partner/enterprise/license-contract/list",
            TikTokApiSeriesDocument.LicenseListRequestBody,
            ct);
        return TikTokApiSeriesDocument.MatchLicenseId(listed.RootElement, configured);
    }

    private static async Task<TikTokApiSeriesDocument.TikTokBusinessContract?> ReadMatchedContractAsync(
        IPage? page,
        string body,
        string configured,
        CancellationToken ct)
    {
        using var listed = await PageFetchAsync(
            page,
            "POST",
            "/api/content-partner/institution/contract/list",
            body,
            ct);
        return TikTokApiSeriesDocument.MatchBusinessContract(listed.RootElement, configured);
    }

    private static async Task<string> ResolveAudienceTagIdAsync(
        IPage? page,
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
        IPage? page,
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

            var formTitle = TikTokPublishConstants.CopyrightMaterialFormTitle(materialType);
            var paths = options.ResolveCopyrightMaterialFilePaths(materialType);
            if (paths.Count == 0)
            {
                log?.Invoke($"接口上传：表单「{formTitle}」没有本地文件，跳过。");
                continue;
            }

            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                log?.Invoke($"接口上传：表单「{formTitle}」上传 {name}。");
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
                var value = ReadId(data, name);
                if (value.Length > 0)
                    return value;
                if (data.TryGetProperty("collection", out var collection))
                {
                    value = ReadId(collection, name);
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
        var commitUri = BuildImageCommitUri(vodBase);
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
