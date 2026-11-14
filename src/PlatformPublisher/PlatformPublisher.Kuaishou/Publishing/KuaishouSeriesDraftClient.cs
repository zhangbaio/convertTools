using System.Text.Json;
using Microsoft.Playwright;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouSeriesDraftClient
{
    private const string Origin = "https://kdj.kuaishou.com";
    private const string CreateUrl = Origin + "/rest/ad/miniSeries/product/pc/create";
    private const string UpdateUrl = Origin + "/rest/ad/miniSeries/product/pc/update";
    private const string TagListUrl = Origin + "/rest/ad/miniSeries/product/pc/tagList";
    private const string QueryListUrl = Origin + "/rest/ad/miniSeries/product/pc/queryList";
    private const string EpisodeDetailUrl = Origin + "/rest/ad/miniSeries/product/pc/episode/videoUpload/detail";
    private const string UploadImgUrl = Origin + "/rest/ad/miniSeries/product/pc/uploadImg";
    private const int ImageByteLimit = 5 * 1024 * 1024;
    private const int PdfByteLimit = 8 * 1024 * 1024;

    public async Task<string> SaveAsync(
        IPage page,
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        KuaishouPersonalFormResolver.Resolved form,
        string? existingMiniSeriesId,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var label = config.StoragePlatform.DisplayName();
        var description = data.Intro.Trim();
        if (description.Length < KuaishouSeriesDraft.DescriptionMinLength || description.Length > KuaishouSeriesDraft.DescriptionMaxLength)
            throw new InvalidOperationException($"短剧简介需要 {KuaishouSeriesDraft.DescriptionMinLength} 到 {KuaishouSeriesDraft.DescriptionMaxLength} 个字，当前 {description.Length} 个字。");
        if (form.HasRecordNumber)
            throw new InvalidOperationException("已勾选有备案号。接口保存需要备案号正文，请先改为无备案号后再上传。");
        RequireFile(data.HorizontalCoverPath, "短剧封面");
        RequireFile(data.VerticalCoverPath, "单集封面");
        RequireFile(data.CommitmentPdfPath, "承诺函");
        if (data.ProjectImagePaths.Count != 4)
            throw new InvalidOperationException($"短剧工程图需要 4 张 PNG，当前 {data.ProjectImagePaths.Count} 张。");
        foreach (var path in data.ProjectImagePaths)
            RequireFile(path, "工程图");
        if (!page.Url.Contains("kdj.kuaishou.com", StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync(Origin + "/home/content/content-management", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var enterprise = config.StoragePlatform == PublishPlatform.KuaishouEnterpriseRevenue;
        progress?.Report($"{label}：正在通过接口读取分类并上传封面、工程图和承诺函。");
        var catalog = KuaishouSeriesCatalog.Parse(await PostAsync(page, TagListUrl, "{}", cancellationToken));
        var tags = ReadLabels(data.WorkflowDirectory, "tag_label_list", enterprise);
        if (tags.Count == 0) throw new InvalidOperationException("项目没有可用的短剧分类。");
        var tagIds = tags.Take(3).Select(catalog.RequireTag).ToArray();
        var plots = ReadLabels(data.WorkflowDirectory, "plot_label_list", enterprise);
        var plotIds = plots.Take(3).Select(catalog.RequirePlot).ToArray();
        var audience = ReadAudienceGender(data.WorkflowDirectory, enterprise);
        if (audience is null && (enterprise || plotIds.Length > 0))
            throw new InvalidOperationException("请选择男女频道。");

        var coverKey = await UploadFileAsync(page, data.HorizontalCoverPath, "短剧封面", ImageByteLimit, cancellationToken);
        var episodeCoverKey = await UploadFileAsync(page, data.VerticalCoverPath, "单集封面", ImageByteLimit, cancellationToken);
        var projectKeys = new List<string>();
        foreach (var path in data.ProjectImagePaths)
            projectKeys.Add(await UploadFileAsync(page, path, "工程图", ImageByteLimit, cancellationToken));
        var commitmentKey = await UploadFileAsync(page, data.CommitmentPdfPath, "承诺函", PdfByteLimit, cancellationToken);

        var seriesId = await ResolveSeriesIdAsync(page, data.Title, existingMiniSeriesId, cancellationToken);
        var creating = string.IsNullOrWhiteSpace(seriesId);
        IReadOnlyList<string> episodeIds = [];
        if (!creating)
        {
            progress?.Report($"{label}：内容管理已有《{data.Title}》，改为更新短剧 {seriesId}。");
            var detail = await PostAsync(page, EpisodeDetailUrl, "{\"miniSeriesId\":" + seriesId + "}", cancellationToken);
            episodeIds = KuaishouSeriesDraft.ReadEpisodeIds(JsonDocument.Parse(detail).RootElement);
            if (episodeIds.Count == 0)
                throw new InvalidOperationException($"短剧 {seriesId} 没有单集，已停止更新。");
            if (episodeIds.Count != data.VideoPaths.Count)
                throw new InvalidOperationException($"已有短剧有 {episodeIds.Count} 集，本地有 {data.VideoPaths.Count} 集，已停止更新。");
        }
        else
        {
            progress?.Report($"{label}：正在通过接口创建短剧草稿。");
        }

        var contentType = KuaishouSeriesDraft.SeriesContentTypeCode(form.ContentTypeLabel);
        var productionOrg = enterprise
            ? First(config.CommitmentRecipientCompanyName, config.ProductionOrganization)
            : form.ProductionOrg;
        var director = ReadPerson(data.WorkflowDirectory, "director_info_list", "男", enterprise);
        var screenwriter = ReadPerson(data.WorkflowDirectory, "screenwriter_info_list", "女", enterprise);
        var seriesYuan = string.IsNullOrWhiteSpace(form.EpisodePrice) ? "0.11" : form.EpisodePrice;
        var episodeYuan = enterprise ? First(config.SeriesPrice, seriesYuan) : seriesYuan;
        if (!DateTime.TryParse(form.BroadcastDate, out var broadcastDate))
            throw new InvalidOperationException($"播出时间“{form.BroadcastDate}”无法识别。");
        var input = new KuaishouSeriesDraftInput
        {
            Title = data.Title,
            Description = data.Intro,
            Enterprise = enterprise,
            Creating = creating,
            MiniSeriesId = seriesId,
            SeriesContentType = contentType,
            ProductMethod = KuaishouSeriesDraft.ProductMethodCode(form.ComicMethodLabel, contentType),
            AuthorDeclaration = KuaishouSeriesDraft.ChoiceCode(KuaishouFormChoices.AuthorDeclarations, form.AuthorDeclarationLabel, "1", "作者声明"),
            CopyrightProofType = KuaishouSeriesDraft.ChoiceCode(KuaishouFormChoices.CopyrightProofTypes, form.CopyrightProofTypeLabel, "1", "版权证明类型"),
            SaleType = form.SaleTypeCode,
            PriceCents = KuaishouSeriesDraft.ToCents(seriesYuan, "全剧价格"),
            EpisodePriceCents = KuaishouSeriesDraft.ToCents(episodeYuan, "单集价格"),
            UpdateEnd = form.Finished,
            SmallAmountUnlock = form.SmallAmountUnlock,
            UnlockCount = form.SaleTypeCode == 3 ? form.UnlockCount : null,
            FreeEpisodeCount = form.FreeEpisodeCount,
            TagIds = tagIds,
            PlotIds = plotIds,
            AudienceGender = audience,
            CoverImgKey = coverKey,
            EpisodeCoverImgKey = episodeCoverKey,
            EpisodeTitles = Enumerable.Repeat(data.Title, data.VideoPaths.Count).ToArray(),
            EpisodeIds = episodeIds,
            ProductionForm = form.ProductionForm,
            ProductionCost = double.TryParse(form.ProductionCost, out var cost) ? cost : 1.0,
            EpisodeAvgTime = form.AverageDurationMinutes,
            ProductionDate = KuaishouSeriesDraft.ChinaMidnightUnixMilliseconds(new DateTime(form.ProductionYear, 1, 1)),
            DisplayDate = KuaishouSeriesDraft.ChinaMidnightUnixMilliseconds(broadcastDate),
            BroadcastPlatform = form.BroadcastPlatform,
            BroadcastChannel = form.BroadcastChannel,
            ProductionOrg = productionOrg,
            CreditName = form.CreditName,
            CreditGender = form.Gender,
            DirectorName = director.Name,
            DirectorGender = director.Gender,
            ScreenwriterName = screenwriter.Name,
            ScreenwriterGender = screenwriter.Gender,
            ProducerName = screenwriter.Name,
            ProducerGender = screenwriter.Gender,
            Actors = KuaishouPersonalFormResolver.ResolveUploadActors(data.WorkflowDirectory, config, enterprise),
            ProjectImageKeys = projectKeys,
            CommitmentKey = commitmentKey,
        };
        var body = KuaishouSeriesDraft.Build(input);
        if (creating)
        {
            try
            {
                var response = await PostAsync(page, CreateUrl, body, cancellationToken);
                var created = KuaishouSeriesDraft.ReadCreatedId(JsonDocument.Parse(response).RootElement);
                if (created.Length > 0) return created;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not KuaishouDailyUploadLimitException && ex is not InvalidOperationException)
            {
                var recovered = await FindSingleSeriesIdAsync(page, data.Title, cancellationToken);
                if (recovered.Length > 0) return recovered;
                throw new InvalidOperationException("快手创建结果无法确认，已停止再次创建。请先在内容管理核对是否已有同名短剧。", ex);
            }

            var existing = await FindSingleSeriesIdAsync(page, data.Title, cancellationToken);
            if (existing.Length > 0) return existing;
            throw new InvalidOperationException("快手创建结果无法确认，已停止再次创建。请先在内容管理核对是否已有同名短剧。");
        }

        await PostAsync(page, UpdateUrl, body, cancellationToken);
        return seriesId;
    }

    private static async Task<string> ResolveSeriesIdAsync(
        IPage page,
        string title,
        string? existingMiniSeriesId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(existingMiniSeriesId))
            return existingMiniSeriesId.Trim();
        return await FindSingleSeriesIdAsync(page, title, cancellationToken);
    }

    private static async Task<string> FindSingleSeriesIdAsync(IPage page, string title, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            pageNum = 1,
            pageSize = 20,
            episodeTitle = "",
            sellingStatus = 0,
            queryType = 0,
            sortParam = new { },
            miniSeriesTitle = title,
            auditStatus = 0,
            saleType = (int?)null,
            seriesIdList = Array.Empty<int>(),
            createDateFrom = "",
            createDateTo = "",
            sourceType4Filter = (int?)null,
        });
        var response = await PostAsync(page, QueryListUrl, body, cancellationToken);
        var matches = KuaishouSeriesDraft.ReadMatchingSeries(JsonDocument.Parse(response).RootElement, title);
        if (matches.Count > 1)
            throw new InvalidOperationException($"内容管理里有 {matches.Count} 部同名短剧《{title}》，已停止创建。");
        if (matches.Count == 0) return string.Empty;
        if (matches[0].AuditStatus != 1)
            throw new InvalidOperationException($"内容管理已有同名短剧《{title}》，当前不是待提交，已停止创建。");
        return matches[0].Id;
    }

    private async Task<string> UploadFileAsync(
        IPage page,
        string path,
        string label,
        int byteLimit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"缺少{label}。", path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length > byteLimit)
            throw new InvalidOperationException($"{label}超过 {byteLimit / 1024 / 1024} MB。");
        var payload = JsonSerializer.Serialize(new
        {
            name = Path.GetFileName(path),
            type = Mime(path),
            base64 = Convert.ToBase64String(bytes),
        });
        var raw = await page.EvaluateAsync<string>(
            """
            async ({ url, payload }) => {
              const file = JSON.parse(payload);
              const binary = atob(file.base64);
              const bytes = new Uint8Array(binary.length);
              for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
              const form = new FormData();
              form.append('file', new Blob([bytes], { type: file.type }), file.name);
              const response = await fetch(url, { method: 'POST', credentials: 'include', body: form });
              const text = await response.text();
              return JSON.stringify({ status: response.status, text: text.slice(0, 4000) });
            }
            """,
            new { url = UploadImgUrl, payload });
        var response = Unwrap(raw, label);
        var key = KuaishouSeriesDraft.ReadImageKey(JsonDocument.Parse(response).RootElement);
        if (key.Length == 0) throw new InvalidOperationException($"{label}上传没有返回 imgKey。");
        return key;
    }

    private static async Task<string> PostAsync(IPage page, string url, string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await page.EvaluateAsync<string>(
            """
            async ({ url, body }) => {
              const response = await fetch(url, {
                method: 'POST', credentials: 'include',
                headers: { 'Content-Type': 'application/json' },
                body,
              });
              const text = await response.text();
              return JSON.stringify({ status: response.status, text: text.slice(0, 200000) });
            }
            """,
            new { url, body });
        return Unwrap(raw, "快手接口");
    }

    private static string Unwrap(string? raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"{label}没有返回内容。");
        using var wrapper = JsonDocument.Parse(raw);
        var status = wrapper.RootElement.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var code)
            ? code
            : 0;
        var text = wrapper.RootElement.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString() ?? string.Empty
            : string.Empty;
        if (status == 401 || status == 403)
            throw new InvalidOperationException("快手登录态无效，请先扫码登录。");
        if (status >= 500)
            throw new HttpRequestException($"{label}返回 HTTP {status}。");
        if (status is < 200 or >= 300)
            throw new InvalidOperationException($"{label}返回 HTTP {status}。");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"{label}没有返回内容。");
        using var body = JsonDocument.Parse(text);
        KuaishouSeriesDraft.EnsureSuccess(body.RootElement);
        return text;
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        _ => "image/jpeg",
    };

    private static int? ReadAudienceGender(string workflowDirectory, bool enterprise)
    {
        if (!TryRead(workflowDirectory, enterprise, out var root)) return null;
        if (root.TryGetProperty("audience_gender", out var code) && code.TryGetInt32(out var value) && value is >= 1 and <= 3)
            return value;
        if (!root.TryGetProperty("audience_gender_label", out var label) || label.ValueKind != JsonValueKind.String)
            return null;
        var text = label.GetString()?.Trim() ?? string.Empty;
        return text.Length == 0 ? null : KuaishouSeriesDraft.AudienceGenderCode(text);
    }

    private static IReadOnlyList<string> ReadLabels(string workflowDirectory, string property, bool enterprise)
    {
        if (!TryRead(workflowDirectory, enterprise, out var root) ||
            !root.TryGetProperty(property, out var labels) ||
            labels.ValueKind != JsonValueKind.Array)
            return [];
        return labels.EnumerateArray()
            .Select(item => item.GetString()?.Trim() ?? string.Empty)
            .Where(item => item.Length > 0)
            .Take(3)
            .ToArray();
    }

    private static string ReadLabel(string workflowDirectory, string property, bool enterprise)
    {
        if (!TryRead(workflowDirectory, enterprise, out var root) ||
            !root.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
            return string.Empty;
        return value.GetString()?.Trim() ?? string.Empty;
    }

    private static (string Name, string Gender) ReadPerson(string workflowDirectory, string property, string defaultGender, bool enterprise)
    {
        if (!TryRead(workflowDirectory, enterprise, out var root) ||
            !root.TryGetProperty(property, out var people) ||
            people.ValueKind != JsonValueKind.Array)
            return (string.Empty, defaultGender);
        foreach (var person in people.EnumerateArray())
        {
            if (person.ValueKind != JsonValueKind.Object) continue;
            var name = person.TryGetProperty("name", out var nameValue) ? nameValue.GetString()?.Trim() ?? string.Empty : string.Empty;
            if (name.Length == 0) continue;
            var gender = person.TryGetProperty("gender", out var genderValue) ? genderValue.GetString()?.Trim() : string.Empty;
            return (name, string.IsNullOrWhiteSpace(gender) ? defaultGender : gender);
        }

        return (string.Empty, defaultGender);
    }

    private static bool TryRead(string workflowDirectory, bool enterprise, out JsonElement root)
    {
        root = default;
        var path = Path.Combine(workflowDirectory, enterprise ? "kuaishou-enterprise-auto-fill.json" : "kuaishou-auto-fill.json");
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void RequireFile(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"缺少{label}。", path);
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
