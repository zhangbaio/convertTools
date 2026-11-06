using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LicenseTranslate.Core;

public static class BusinessLicenseVision
{
    public static async Task<BusinessLicenseDraft> ReadAsync(
        string imagePath,
        BusinessLicenseTranslationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException(BusinessLicenseAiTranslator.MissingApiKeyMessage);

        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(bytes);
        var payload = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(options.Model) ? "doubao-seed-2-1-lite-260915" : options.Model,
            temperature = 0,
            thinking = new { type = "disabled" },
            max_tokens = 4096,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = Prompt },
                        new { type = "image_url", image_url = new { url = dataUri, detail = "high" } },
                    },
                },
            },
        });
        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? "https://ark.cn-beijing.volces.com/api/v3"
            : options.BaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"执照识别失败：{(int)response.StatusCode} {Trim(body)}");
        var content = JsonDocument.Parse(body).RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return Parse(imagePath, BusinessLicenseAiTranslator.ExtractJson(content));
    }

    private static BusinessLicenseDraft Parse(string imagePath, string json)
    {
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("执照识别没有返回 JSON。");
        string Read(string name) => node[name]?.GetValue<string>()?.Trim() ?? "";
        var type = Read("type");
        var individual = type.Contains("个体", StringComparison.Ordinal);
        return new BusinessLicenseDraft
        {
            SourcePath = imagePath,
            Kind = individual ? "个体工商户" : "公司",
            CreditCode = Read("creditCode"),
            Name = Read("name"),
            Type = type,
            PersonRole = individual ? "经营者" : "法定代表人",
            PersonName = Read("personName"),
            ExtraLabel = individual ? "组成形式" : "注册资本",
            ExtraValue = Read("capital"),
            DateLabel = individual ? "注册日期" : "成立日期",
            DateValue = Read("establishDate"),
            AddressLabel = individual ? "经营场所" : "住所",
            Address = Read("address"),
            Scope = Read("scope"),
            Authority = Read("authority"),
            IssueDate = Read("issueDate"),
        };
    }

    private static string Trim(string text) => text.Length <= 240 ? text : text[..240];

    private const string Prompt = """
        你在读取一张中国营业执照照片。只输出一个 JSON 对象，不要 Markdown。
        按原件逐字抄录，不要翻译，不要编造看不清的字。看不清就留空字符串。
        键名只能是 creditCode、name、type、personName、capital、establishDate、address、scope、authority、issueDate。
        creditCode 是 18 位统一社会信用代码。
        capital 抄注册资本原文，例如壹拾万圆人民币。
        establishDate 和 issueDate 用原件日期，发照日期在登记机关红章旁边，不要和成立日期搞混。
        address 抄完整住所，包括小区、楼栋、楼层和房号。
        scope 抄完整经营范围，保留一般项目和许可项目。
        authority 抄登记机关名称。红章外圈文字就是登记机关，不要写成国家市场监督管理总局。
        """;
}
