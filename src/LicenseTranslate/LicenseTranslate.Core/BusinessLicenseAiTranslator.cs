using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LicenseTranslate.Core;

public sealed class BusinessLicenseTranslationOptions
{
    public string GlossaryPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YunfanPlatformPublisher",
        "license-translation-glossary.json");

    public string BaseUrl { get; init; } = "https://api.deepseek.com/v1";
    public string Model { get; init; } = "deepseek-chat";
    public string? ApiKey { get; init; }
    public HttpMessageHandler? Handler { get; init; }
}

public static class BusinessLicenseAiTranslator
{
    public const string MissingApiKeyMessage = "未配置大模型翻译 API Key。请到系统设置 → 系统服务 → 翻译配置填写。";

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(90) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task TranslateAsync(
        BusinessLicenseDraft draft,
        BusinessLicenseTranslationOptions options,
        CancellationToken cancellationToken = default)
    {
        var glossary = new BusinessLicenseGlossary(options.GlossaryPath);
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        draft.EnglishName = Take(glossary, pending, "name", "englishName", draft.Name);
        draft.EnglishType = Take(glossary, pending, "type", "englishType", draft.Type);
        draft.EnglishAddress = Take(glossary, pending, "address", "englishAddress", draft.Address);
        draft.EnglishScope = Take(glossary, pending, "scope", "englishScope", draft.Scope);
        draft.EnglishAuthority = Take(glossary, pending, "authority", "englishAuthority", draft.Authority);
        if (pending.Count == 0) return;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException(MissingApiKeyMessage);

        var translated = await RequestAsync(draft, pending, options, cancellationToken);
        if (pending.ContainsKey("englishName")) draft.EnglishName = Required(translated.EnglishName, "名称");
        if (pending.ContainsKey("englishType")) draft.EnglishType = Required(translated.EnglishType, "类型");
        if (pending.ContainsKey("englishAddress")) draft.EnglishAddress = Required(translated.EnglishAddress, "地址");
        if (pending.ContainsKey("englishScope")) draft.EnglishScope = Required(translated.EnglishScope, "经营范围");
        if (pending.ContainsKey("englishAuthority")) draft.EnglishAuthority = Required(translated.EnglishAuthority, "登记机关");
    }

    public static void Remember(BusinessLicenseDraft draft, string glossaryPath)
    {
        var glossary = new BusinessLicenseGlossary(glossaryPath);
        glossary.Remember("name", draft.Name, draft.EnglishName);
        glossary.Remember("type", draft.Type, draft.EnglishType);
        glossary.Remember("address", draft.Address, draft.EnglishAddress);
        glossary.Remember("scope", draft.Scope, draft.EnglishScope);
        glossary.Remember("authority", draft.Authority, draft.EnglishAuthority);
    }

    private static string Take(
        BusinessLicenseGlossary glossary,
        Dictionary<string, string> pending,
        string field,
        string jsonName,
        string chinese)
    {
        chinese = chinese.Trim();
        if (chinese.Length == 0) return "";
        var cached = glossary.Find(field, chinese);
        if (!string.IsNullOrWhiteSpace(cached)) return cached;
        pending[jsonName] = chinese;
        return "";
    }

    private static string Required(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"大模型没有返回{label}的英文。");
        return value.Trim();
    }

    private static async Task<LicenseTranslationResponse> RequestAsync(
        BusinessLicenseDraft draft,
        Dictionary<string, string> pending,
        BusinessLicenseTranslationOptions options,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(options.Model) ? "deepseek-chat" : options.Model,
            temperature = 0.2,
            thinking = new { type = "disabled" },
            stream = false,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = UserPrompt(draft, pending) },
            },
        });
        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? "https://api.deepseek.com/v1" : options.BaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var owned = options.Handler is null ? null : new HttpClient(options.Handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(90) };
        var client = owned ?? SharedClient;
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"大模型翻译失败：{(int)response.StatusCode} {Trim(body)}");
        var content = JsonDocument.Parse(body).RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var json = ExtractJson(content);
        return JsonSerializer.Deserialize<LicenseTranslationResponse>(json, JsonOptions) ?? new LicenseTranslationResponse();
    }

    private static string UserPrompt(BusinessLicenseDraft draft, Dictionary<string, string> pending)
    {
        var fields = new Dictionary<string, string>(pending, StringComparer.Ordinal) { ["kind"] = draft.Kind };
        return JsonSerializer.Serialize(fields);
    }

    internal static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("大模型没有返回 JSON。");
        return content[start..(end + 1)];
    }

    private static string Trim(string text) => text.Length <= 240 ? text : text[..240];

    private const string SystemPrompt = """
        你是营业执照英文翻译。只输出一个 JSON 对象，不要 Markdown。
        只翻译用户给出的字段。键名只能是 englishName、englishType、englishAddress、englishScope、englishAuthority。
        规则：
        - 不翻译信用代码、网址、日期、金额和人名。
        - 公司名称：地名用英文，字号拼音连写，有限公司译为 Co., Ltd.。例：北京星茁科技有限公司 → Beijing Xingzhuo Technology Co., Ltd.
        - 个体户名称末尾保留 (Individual Industrial and Commercial Household)。例：沈阳市和平区智啼恺文化传媒工作室 → Shenyang Heping District Zhitikai Cultural Media Studio (Individual Industrial and Commercial Household)
        - 类型：有限责任公司（自然人独资）→ Limited Liability Company (Wholly Owned by Natural Person)；个体工商户 → Individual Business。
        - 地址从小到大写：房间或楼层、门牌、路、区、市、省。集群注册译为 cluster registration。例：02986, 3rd Floor, Building 1, No. 19, Zhengtong Road, Liangxiang, Fangshan District, Beijing (cluster registration)
        - 经营范围逐条翻译并用分号分隔。一般项目以 "General items: " 开头，许可项目以 "Licensed items: " 开头。
        - 经营范围里的法律套话使用这句，不要改写：Except for items subject to approval in accordance with laws, business activities shall be carried out independently in accordance with laws with the business license
        - 登记机关以 (Sealed) 结尾。例：Fangshan District Market Supervision Administration of Beijing (Sealed)
        """;

    private sealed class LicenseTranslationResponse
    {
        public string? EnglishName { get; set; }
        public string? EnglishType { get; set; }
        public string? EnglishAddress { get; set; }
        public string? EnglishScope { get; set; }
        public string? EnglishAuthority { get; set; }
    }
}
