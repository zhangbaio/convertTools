using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouConfigurationValidator
{
    public static IReadOnlyList<string> Validate(KuaishouPersonalConfig config)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(config.EntryUrl)) issues.Add("经营者平台入口不能为空");
        if (config.QueueMaxParallelProjects < 1) issues.Add("并行项目数必须大于 0");
        if (string.IsNullOrWhiteSpace(config.MaterialTitleTemplate)) issues.Add("宣发素材标题模板不能为空");
        if (string.IsNullOrWhiteSpace(config.MaterialType)) issues.Add("宣发素材剪辑类型不能为空");
        if (string.IsNullOrWhiteSpace(config.MaterialAuthorDeclaration)) issues.Add("宣发素材作者声明不能为空");
        if (config.MaterialCoverMode is not ("adx" or "project-poster" or "single-image"))
            issues.Add("宣发素材封面模式无效");
        if (config.StoragePlatform == PlatformPublisher.Common.Models.PublishPlatform.KuaishouEnterpriseRevenue)
        {
            var price = string.IsNullOrWhiteSpace(config.SeriesPrice) ? config.EpisodePrice : config.SeriesPrice;
            if (!decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
                issues.Add("企业版单集价格必须是大于或等于 0 的数字");
        }
        if (config.DistributionDefaultRatePercent is < 0 or > 100) issues.Add("分销比例必须为 0–100");
        ValidateJson(config.DistributionDistributorAccountsJson, "分销商账号 JSON", issues);
        ValidateJson(config.SynopsisPolicyJson, "简介策略 JSON", issues);
        if (config.DistributionEnabled && config.DistributionSubmitEnabled)
        {
            if (string.IsNullOrWhiteSpace(config.ApiBaseUrl)) issues.Add("启用分销提交时 API Base URL 不能为空");
            if (string.IsNullOrWhiteSpace(config.AppId)) issues.Add("启用分销提交时 AppID 不能为空");
            if (string.IsNullOrWhiteSpace(config.AccessToken))
                issues.Add(config.RemoteTokenEnabled
                    ? "已选择共享 Token，但当前客户端尚未取得共享 Token"
                    : "启用分销提交时必须配置 Access Token");
        }
        return issues;
    }

    private static void ValidateJson(string? value, string label, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try { JsonNode.Parse(value); }
        catch (JsonException ex) { issues.Add($"{label}格式错误：{ex.Message}"); }
    }
}

public sealed class KuaishouContentComplianceService
{
    public void Validate(KuaishouPersonalProjectData data, KuaishouPersonalConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SynopsisPolicyJson)) return;
        var policy = JsonNode.Parse(config.SynopsisPolicyJson)?.AsObject();
        var blocked = policy?["blockedTerms"]?.AsArray() ?? policy?["blocked_terms"]?.AsArray();
        if (blocked is null) return;
        var content = data.Title + "\n" + data.Intro;
        var matches = blocked
            .Select(item => item?.ToString()?.Trim())
            .Where(term => !string.IsNullOrWhiteSpace(term) && content.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length > 0)
            throw new InvalidOperationException($"快手内容合规检查未通过，命中：{string.Join('、', matches)}");
    }
}

public sealed class KuaishouDistributionService
{
    private readonly HttpClient _httpClient;
    public KuaishouDistributionService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task ApplyAsync(
        string miniSeriesId,
        KuaishouPersonalConfig config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!config.DistributionEnabled || !config.StepDistributionSeries) return;
        if (!config.DistributionSubmitEnabled)
        {
            progress?.Report("快手分销配置已生成；自动提交未启用，跳过线上提交。");
            return;
        }
        if (string.IsNullOrWhiteSpace(miniSeriesId))
            throw new InvalidOperationException("分销提交需要已创建的短剧 ID。");

        var accounts = string.IsNullOrWhiteSpace(config.DistributionDistributorAccountsJson)
            ? new JsonArray()
            : JsonNode.Parse(config.DistributionDistributorAccountsJson);
        var payload = new JsonObject
        {
            ["appId"] = config.AppId,
            ["advertiserId"] = config.AdvertiserId,
            ["miniSeriesId"] = miniSeriesId,
            ["ratePercent"] = config.DistributionDefaultRatePercent,
            ["mode"] = config.DistributionMode,
            ["allowJuxing"] = config.DistributionAllowJuxing,
            ["allowOnlineTime"] = config.DistributionAllowOnlineTime,
            ["accounts"] = accounts?.DeepClone(),
        };
        var url = config.ApiBaseUrl.TrimEnd('/') + "/" + config.DistributionApiPath.TrimStart('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        var header = string.IsNullOrWhiteSpace(config.TokenHeader) ? "Access-Token" : config.TokenHeader.Trim();
        if (!string.IsNullOrWhiteSpace(config.AccessToken)) request.Headers.TryAddWithoutValidation(header, config.AccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"快手分销提交失败：HTTP {(int)response.StatusCode} {Trim(responseText)}");
        progress?.Report("快手分销配置已提交。");
    }

    private static string Trim(string value) => value.Length <= 300 ? value : value[..300];
}
