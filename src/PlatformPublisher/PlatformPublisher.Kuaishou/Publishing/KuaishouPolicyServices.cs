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
        ValidateJson(config.SynopsisPolicyJson, "简介策略 JSON", issues);
        return issues;
    }

    private static void ValidateJson(string? value, string label, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try { JsonNode.Parse(value); }
        catch (JsonException ex) { issues.Add($"{label}格式错误：{ex.Message}"); }
    }
}
