using System.Net;
using System.Text;
using LicenseTranslate.Core;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public class BusinessLicenseTranslatorTests
{
    [Fact]
    public void Company_ocr_lines_become_an_editable_english_license()
    {
        var draft = BusinessLicenseParser.Parse("license.png", """
            统一社会信用代码
            91110111MAKAEG7T43
            名称
            北京星茁科技有限公司
            型有限责任公司（自然人独资）
            法定代表人黄星
            经营范围
            一般项目：技术服务、技术开发、技术咨询。
            10万元
            2026年04月08日
            北京市房山区良乡政通路19号1号楼3层02986（集
            群注册）
            登记机关
            """);

        Assert.Equal("公司", draft.Kind);
        Assert.Equal("91110111MAKAEG7T43", draft.CreditCode);
        Assert.Equal("北京星茁科技有限公司", draft.Name);
        Assert.Equal("有限责任公司（自然人独资）", draft.Type);
        Assert.Equal("黄星", draft.PersonName);
        Assert.Equal("10万元", draft.ExtraValue);
        Assert.Equal("2026年04月08日", draft.DateValue);
        Assert.Contains("房山区", draft.Address);
        Assert.Contains("集群注册", draft.Address);
        Assert.Contains("技术服务", draft.Scope);

        BusinessLicenseTranslator.ApplyFixed(draft);
        Assert.Equal("Huang Xing", draft.EnglishPersonName);
        Assert.Equal("", draft.EnglishName);
        Assert.Equal("CNY 100,000", draft.EnglishExtraValue);
        Assert.Equal("April 8, 2026", draft.EnglishDateValue);
        Assert.Equal("", draft.EnglishScope);
        var docx = Path.Combine(Path.GetTempPath(), "yunfan-license-test.docx");
        BusinessLicenseDocumentWriter.Write(docx, draft);
        Assert.True(new FileInfo(docx).Length > 1000);
    }

    [Fact]
    public async Task Sample_folder_images_produce_names_and_credit_codes()
    {
        var folder = @"D:\抠出的印章\狠丶不在乎 执照8个\执照原图";
        if (!Directory.Exists(folder)) return;
        var images = Directory.GetFiles(folder, "*.png");
        Assert.NotEmpty(images);
        foreach (var image in images)
        {
            var text = await BusinessLicenseOcr.RecognizeAsync(image, CancellationToken.None);
            var draft = BusinessLicenseParser.Parse(image, text);
            BusinessLicenseTranslator.ApplyFixed(draft);
            Assert.False(string.IsNullOrWhiteSpace(draft.CreditCode), image);
            Assert.Matches("^[0-9A-Z]{18}$", draft.CreditCode);
            Assert.False(string.IsNullOrWhiteSpace(draft.PersonName), text);
            Assert.False(string.IsNullOrWhiteSpace(draft.EnglishPersonName), draft.PersonName);
        }
    }

    [Fact]
    public void Individual_spaced_ocr_keeps_the_operator_and_place()
    {
        var draft = BusinessLicenseParser.Parse("license.png", """
            统 一 社 会 信 用 代 码
            9 2 2 1 0 1 0 2 M A E R 6 3 3 F X W
            称 沈 阳 市 和 平 区 智 啼 恺 文 化 传 媒 工 作 室 （ 个 体 工 商 户 ）
            个 人 经 营
            2025 年 08 月 12 日
            辽 宁 省 沈 阳 市 和 平 区 三 好 街 22 号 1307 房
            经 营 者 吕 斌
            经 营 范 围 一 般 项 目 ： 网 络 技 术 服 务 ；
            屋 w182
            """);

        Assert.Equal("个体工商户", draft.Kind);
        Assert.Equal("92210102MAER633FXW", draft.CreditCode);
        Assert.Equal("沈阳市和平区智啼恺文化传媒工作室", draft.Name);
        Assert.Equal("吕斌", draft.PersonName);
        Assert.Equal("个人经营", draft.ExtraValue);
        Assert.Contains("三好街", draft.Address);
        Assert.Contains("屋w182", draft.Address.Replace(" ", ""));
        BusinessLicenseTranslator.ApplyFixed(draft);
        Assert.Equal("Lyu Bin", draft.EnglishPersonName);
        Assert.Equal("", draft.EnglishName);
        Assert.Equal("Individual Operation", draft.EnglishExtraValue);
    }

    [Fact]
    public void Individual_name_uses_lyu_for_lv()
    {
        Assert.Equal("Lyu Bin", BusinessLicenseTranslator.PersonName("吕斌"));
    }

    [Fact]
    public async Task Cached_phrases_skip_the_model()
    {
        var glossary = Path.Combine(Path.GetTempPath(), "yunfan-license-glossary-" + Guid.NewGuid().ToString("N") + ".json");
        var draft = new BusinessLicenseDraft
        {
            Kind = "公司",
            Name = "北京星茁科技有限公司",
            Type = "有限责任公司（自然人独资）",
            Address = "北京市房山区良乡政通路19号",
            Scope = "一般项目：技术服务。",
        };
        new BusinessLicenseGlossary(glossary).Remember("name", draft.Name, "Beijing Xingzhuo Technology Co., Ltd.");
        new BusinessLicenseGlossary(glossary).Remember("type", draft.Type, "Limited Liability Company (Wholly Owned by Natural Person)");
        new BusinessLicenseGlossary(glossary).Remember("address", draft.Address, "No. 19, Zhengtong Road, Liangxiang, Fangshan District, Beijing");
        new BusinessLicenseGlossary(glossary).Remember("scope", draft.Scope, "General items: technical services.");

        await BusinessLicenseAiTranslator.TranslateAsync(draft, new BusinessLicenseTranslationOptions { GlossaryPath = glossary });

        Assert.Equal("Beijing Xingzhuo Technology Co., Ltd.", draft.EnglishName);
        Assert.Equal("Limited Liability Company (Wholly Owned by Natural Person)", draft.EnglishType);
        Assert.Contains("Zhengtong Road", draft.EnglishAddress);
        Assert.Equal("General items: technical services.", draft.EnglishScope);
    }

    [Fact]
    public async Task Missing_api_key_does_not_fall_back_to_pinyin()
    {
        var glossary = Path.Combine(Path.GetTempPath(), "yunfan-license-glossary-" + Guid.NewGuid().ToString("N") + ".json");
        var draft = new BusinessLicenseDraft { Name = "北京络视科技有限公司", Kind = "公司" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BusinessLicenseAiTranslator.TranslateAsync(draft, new BusinessLicenseTranslationOptions { GlossaryPath = glossary }));
        Assert.Equal(BusinessLicenseAiTranslator.MissingApiKeyMessage, error.Message);
        Assert.Equal("", draft.EnglishName);
    }

    [Fact]
    public async Task Model_json_fills_only_the_uncached_fields()
    {
        var glossary = Path.Combine(Path.GetTempPath(), "yunfan-license-glossary-" + Guid.NewGuid().ToString("N") + ".json");
        new BusinessLicenseGlossary(glossary).Remember("name", "北京星茁科技有限公司", "Beijing Xingzhuo Technology Co., Ltd.");
        var draft = new BusinessLicenseDraft
        {
            Kind = "公司",
            Name = "北京星茁科技有限公司",
            Scope = "一般项目：软件开发。",
        };
        var handler = new StubHandler("""{"choices":[{"message":{"content":"```json\n{\"englishScope\":\"General items: software development.\"}\n```"}}]}""");
        await BusinessLicenseAiTranslator.TranslateAsync(draft, new BusinessLicenseTranslationOptions
        {
            GlossaryPath = glossary,
            ApiKey = "test-key",
            Handler = handler,
        });
        Assert.Equal("Beijing Xingzhuo Technology Co., Ltd.", draft.EnglishName);
        Assert.Equal("General items: software development.", draft.EnglishScope);
        Assert.Contains("/chat/completions", handler.RequestUri);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
