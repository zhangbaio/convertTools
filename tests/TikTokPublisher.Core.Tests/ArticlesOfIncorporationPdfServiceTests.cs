using System.Text.Json;
using FluentAssertions;
using PdfSharp.Pdf.IO;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class ArticlesOfIncorporationPdfServiceTests
{
    [Theory]
    [InlineData("ANTHONY CLYDE CLUNIS", "ANTHONY CLUNIS")]
    [InlineData("ANTHONY CLUNIS", "ANTHONY CLUNIS")]
    [InlineData("ANTHONY", "ANTHONY")]
    [InlineData("  ANTHONY   CLYDE   CLUNIS ", "ANTHONY CLUNIS")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ToShortName_KeepsFirstAndLastOfTheSameName(string? fullName, string expected)
    {
        ArticlesOfIncorporationFields.ToShortName(fullName).Should().Be(expected);
    }

    [Fact]
    public void CreateRandomFilingStamp_StaysInside2025ThroughToday()
    {
        var shipped = Path.GetDirectoryName(
            ArticlesOfIncorporationPdfService.ResolveTemplatePath(CompanyNameCatalog.Template1FileName))!;
        var user = Path.Combine(Path.GetTempPath(), $"company-names-stamp-{Guid.NewGuid():N}");
        var previous = new CompanyLibrary(CompanyNameCatalog.Mississippi, CompanyNameCatalog.MississippiNotice);
        CompanyNameCatalog.SetMississippi(
            CompanyNameCatalog.Open(CompanyNameCatalog.Template1FileName, shipped, user));
        var today = new DateOnly(2026, 10, 19);
        var random = new Random(17);
        try
        {
            for (var i = 0; i < 200; i++)
            {
            var stamp = ArticlesOfIncorporationFields.CreateRandomFilingStamp(today, random);
            var signedOn = DateOnly.ParseExact(stamp.SignedOn, "MM/dd/yyyy");
            var filedAt = DateTime.ParseExact(stamp.FiledAt, "MM/dd/yyyy hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);

            signedOn.Should().BeOnOrAfter(new DateOnly(2025, 1, 1)).And.BeOnOrBefore(new DateOnly(2026, 5, 31));
            DateOnly.FromDateTime(filedAt).Should().Be(signedOn);
            stamp.FilingNumber.Should().HaveLength(10).And.StartWith(signedOn.Year.ToString());
            stamp.BusinessId.Should().HaveLength(7).And.StartWith("154");
            stamp.RegistrantShortName.Should().Be(ArticlesOfIncorporationFields.ToShortName(stamp.RegistrantName));
            stamp.RegistrantName.Split(' ').Should().HaveCount(3);
            stamp.Street.Should().MatchRegex(@"^\d{3,4} [A-Z][a-z]+ (Rd|St|Ave|Dr|Ln|Blvd|Ct|Way)$");
            stamp.City.Should().MatchRegex(@"^.+, [A-Z]{2} \d{5}$");
            stamp.CompanyName.Should().MatchRegex(@"^[A-Z][a-z]+ [A-Z][a-z]+ INC$");
            CompanyNameCatalog.Mississippi.Select(item => item.English).Should().Contain(stamp.CompanyName);
            stamp.Email.Should().MatchRegex(@"^[a-z]{6,}@[a-z]{14,}\.com$");
            stamp.Email.Should().NotContain(stamp.CompanyName.Split(' ')[0].ToLowerInvariant());
            }
        }
        finally
        {
            CompanyNameCatalog.SetMississippi(previous);
            if (Directory.Exists(user))
                Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public void CreateRandomFilingDates_StayOnOrBeforeTodayInsideTheWindow()
    {
        var today = new DateOnly(2026, 3, 15);
        var random = new Random(5);
        for (var i = 0; i < 40; i++)
        {
            var stamp = ArticlesOfIncorporationFields.CreateRandomFilingStamp(today, random);
            DateOnly.ParseExact(stamp.SignedOn, "MM/dd/yyyy").Should().BeOnOrBefore(today);

            var kentucky = KentuckyArticlesPdfService.CreateRandom(today, random);
            var filed = DateTime.ParseExact(kentucky.FiledAt, "M/d/yyyy h:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
            DateOnly.FromDateTime(filed).Should().BeOnOrBefore(today);
        }
    }

    [Fact]
    public void CreateRandomFilingStamp_RejectsATodayBefore2025()
    {
        var act = () => ArticlesOfIncorporationFields.CreateRandomFilingStamp(new DateOnly(2024, 12, 31), new Random(1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CompanyNameCatalog_ContainsOneThousandCompleteNames()
    {
        var shipped = Path.GetDirectoryName(ArticlesOfIncorporationPdfService.ResolveTemplatePath(CompanyNameCatalog.Template1FileName))!;
        var user = Path.Combine(Path.GetTempPath(), $"company-names-empty-{Guid.NewGuid():N}");
        var template1 = CompanyNameCatalog.Open(CompanyNameCatalog.Template1FileName, shipped, user);
        var template2 = CompanyNameCatalog.Open(CompanyNameCatalog.Template2FileName, shipped, user);

        template1.Names.Should().HaveCount(1000);
        template2.Names.Should().HaveCount(1000);
        template1.Names.Select(item => item.English).Should().OnlyHaveUniqueItems();
        template2.Names.Select(item => item.English).Should().OnlyHaveUniqueItems();
        template1.Names.Should().AllSatisfy(item =>
            item.English.Should().MatchRegex(@"^[A-Z][a-z]+ [A-Z][a-z]+ INC$"));
        template2.Names.Should().AllSatisfy(item =>
            item.English.Should().MatchRegex(@"^[A-Z]+ \d{2}[A-Z]+ [A-Z]+ Inc\.$"));
    }

    [Fact]
    public void CompanyNameCatalog_PrefersAValidUserFileAndKeepsTheOtherTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"company-names-{Guid.NewGuid():N}");
        var shipped = Path.Combine(root, "shipped");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(shipped);
        Directory.CreateDirectory(user);
        try
        {
            WriteLibrary(Path.Combine(shipped, CompanyNameCatalog.Template1FileName), "Harbor Peak INC");
            WriteLibrary(Path.Combine(shipped, CompanyNameCatalog.Template2FileName), "HARBOR 10VALLEY HOLDING Inc.");
            WriteLibrary(Path.Combine(user, CompanyNameCatalog.Template1FileName), "Silver Trade INC");
            File.WriteAllText(Path.Combine(user, CompanyNameCatalog.Template2FileName), "{");

            var template1 = CompanyNameCatalog.Open(CompanyNameCatalog.Template1FileName, shipped, user);
            var template2 = CompanyNameCatalog.Open(CompanyNameCatalog.Template2FileName, shipped, user);

            template1.Notice.Should().BeNull();
            template1.Names.Should().ContainSingle().Which.English.Should().Be("Silver Trade INC");
            template2.Names.Should().ContainSingle().Which.English.Should().Be("HARBOR 10VALLEY HOLDING Inc.");
            template2.Notice.Should().Be("公司库 company-names-template2.json 无法使用（文件不是有效的 JSON），已改用程序自带库。");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompanyNameCatalog_SkipsDuplicateEnglishNames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"company-names-{Guid.NewGuid():N}");
        var shipped = Path.Combine(root, "shipped");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(shipped);
        Directory.CreateDirectory(user);
        try
        {
            WriteLibrary(Path.Combine(shipped, CompanyNameCatalog.Template1FileName), "Maple Stone INC");
            File.WriteAllText(
                Path.Combine(user, CompanyNameCatalog.Template1FileName),
                """["Ridge Hunt INC","Hunt Basin INC","ridge hunt inc","Hunt Basin INC"]""");

            var library = CompanyNameCatalog.Open(CompanyNameCatalog.Template1FileName, shipped, user);

            library.Notice.Should().BeNull();
            library.Names.Select(item => item.English).Should().Equal("Ridge Hunt INC", "Hunt Basin INC");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompanyNameCatalog_ImportsALibraryAndSkipsDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"company-names-{Guid.NewGuid():N}");
        var user = Path.Combine(root, "user");
        var source = Path.Combine(root, "source.json");
        Directory.CreateDirectory(root);
        var previous = new CompanyLibrary(CompanyNameCatalog.Mississippi, CompanyNameCatalog.MississippiNotice);
        try
        {
            File.WriteAllText(source, """["Cedar Brook INC","Aspen Cove INC","cedar brook inc"," "]""");
            var invalid = CompanyNameCatalog.TryImport("template1", source, user, true, false, out var invalidMessage);
            invalid.Should().BeFalse();
            invalidMessage.Should().Be("公司库无法使用（存在空的公司名称）。");
            CompanyNameCatalog.Mississippi.Should().Equal(previous.Names);

            File.WriteAllText(source, """["Cedar Brook INC","Aspen Cove INC","cedar brook inc"]""");
            var imported = CompanyNameCatalog.TryImport("template1", source, user, true, false, out var message);
            imported.Should().BeTrue();
            message.Should().Be("已导入模板1公司库，共 2 个名称。");
            CompanyNameCatalog.Mississippi.Select(item => item.English).Should().Equal("Cedar Brook INC", "Aspen Cove INC");
            File.ReadAllText(Path.Combine(user, CompanyNameCatalog.Template1FileName))
                .Should().Contain("Cedar Brook INC")
                .And.Contain("Aspen Cove INC")
                .And.NotContain("cedar brook inc");
        }
        finally
        {
            CompanyNameCatalog.SetMississippi(previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""["Harbor Peak INC"," "]""", "存在空的公司名称")]
    [InlineData("[]", "没有公司名称")]
    public void CompanyNameCatalog_RejectsAUserFileThatCannotBeUsed(string json, string reason)
    {
        var root = Path.Combine(Path.GetTempPath(), $"company-names-{Guid.NewGuid():N}");
        var shipped = Path.Combine(root, "shipped");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(shipped);
        Directory.CreateDirectory(user);
        try
        {
            WriteLibrary(Path.Combine(shipped, CompanyNameCatalog.Template1FileName), "Maple Stone INC");
            File.WriteAllText(Path.Combine(user, CompanyNameCatalog.Template1FileName), json);

            var library = CompanyNameCatalog.Open(CompanyNameCatalog.Template1FileName, shipped, user);

            library.Names.Should().ContainSingle().Which.English.Should().Be("Maple Stone INC");
            library.Notice.Should().Be($"公司库 company-names-template1.json 无法使用（{reason}），已改用程序自带库。");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompanyNameCatalog_ReportsWhenEveryLibraryNameHasBeenUsed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"used-company-names-{Guid.NewGuid():N}.json");
        var catalog = new[]
        {
            new CompanyName("Harbor Peak INC"),
            new CompanyName("Silver Trade INC"),
        };
        var random = new Random(1);
        try
        {
            var first = CompanyNameCatalog.Take("template1", catalog, random, path);
            var second = CompanyNameCatalog.Take("template1", catalog, random, path);
            var third = CompanyNameCatalog.Take("template1", catalog, random, path);
            var otherTemplate = CompanyNameCatalog.Take("template2", catalog, random, path);

            first.Exhausted.Should().BeFalse();
            second.Exhausted.Should().BeFalse();
            second.English.Should().NotBe(first.English);
            third.Exhausted.Should().BeTrue();
            third.English.Should().BeOneOf(catalog.Select(item => item.English));
            otherTemplate.Exhausted.Should().BeFalse();

            var expanded = catalog.Append(new CompanyName("Maple Stone INC")).ToArray();
            var fourth = CompanyNameCatalog.Take("template1", expanded, random, path);
            fourth.Exhausted.Should().BeFalse();
            fourth.English.Should().Be("Maple Stone INC");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void CreateRandomKentucky_UsesTheFilingDayForTheEffectiveDate()
    {
        var today = new DateOnly(2026, 10, 20);
        var random = new Random(4);
        for (var i = 0; i < 50; i++)
        {
            var fields = KentuckyArticlesPdfService.CreateRandom(today, random);
            var filed = DateTime.ParseExact(fields.FiledAt, "M/d/yyyy h:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
            var effective = DateTime.ParseExact(fields.EffectiveOn.TrimEnd('.'), "dddd, MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);

            DateOnly.FromDateTime(filed).Should().Be(DateOnly.FromDateTime(effective));
            DateOnly.FromDateTime(filed).Should().BeOnOrAfter(new DateOnly(2025, 1, 1)).And.BeOnOrBefore(new DateOnly(2026, 5, 31));
            fields.FilingNumber.Should().MatchRegex(@"^157\d{4}\.\d{2}$");
            fields.CompanyName.Should().MatchRegex(@"^[A-Z]+ \d{2}[A-Z]+ [A-Z]+ Inc\.$");
            CompanyNameCatalog.Kentucky.Select(item => item.English).Should().Contain(fields.CompanyName);
            ArticlesChineseText.TranslateKentuckyCompany(fields.CompanyName).Should().NotMatchRegex("[A-Za-z]");
            fields.CityLine.Should().Contain(", KY ");
            fields.Street.Should().MatchRegex(@"^\d{3,4} [A-Za-z]+ (Rd|St|Ave|Dr|Ln|Ct|Way)$");
        }
    }

    [Fact]
    public void CreateRandomKentucky_DoesNotRepeatAPreviousDraw()
    {
        var today = new DateOnly(2026, 10, 20);
        var random = new Random(9);
        var first = KentuckyArticlesPdfService.CreateRandom(today, random);
        var second = KentuckyArticlesPdfService.CreateRandom(today, random, [first]);

        second.CompanyName.Should().NotBe(first.CompanyName);
        second.FilingNumber.Should().NotBe(first.FilingNumber);
        second.FiledAt.Should().NotBe(first.FiledAt);
    }

    [Theory]
    [InlineData("ANN LEE")]
    [InlineData("HUGO MAGDIEL DOMINGUEZ")]
    [InlineData("CHRISTOPHER ALEXANDER MONTGOMERY")]
    public void RenderKentucky_AcceptsShortAndLongRegistrantNames(string name)
    {
        var fields = new KentuckyArticlesFields(
            "1570001.01",
            "4/3/2026 4:24:54 PM",
            "SILVER 17CREEK BUREAU Inc.",
            "7919 Church Ct",
            "Frankfort, KY 40601",
            "Friday, April 3, 2026.",
            RegistrantName: name);
        var output = Path.Combine(Path.GetTempPath(), $"ky-name-{Guid.NewGuid():N}.pdf");
        try
        {
            KentuckyArticlesPdfService.Render(output, fields);
            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(1);
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    [Fact]
    public void RenderKentucky_FillsBothTemplates()
    {
        var fields = KentuckyArticlesPdfService.CreateRandom(new DateOnly(2026, 10, 20), new Random(2));
        var english = Path.Combine(Path.GetTempPath(), $"ky-{Guid.NewGuid():N}.pdf");
        var chinese = Path.Combine(Path.GetTempPath(), $"ky-zh-{Guid.NewGuid():N}.pdf");
        try
        {
            KentuckyArticlesPdfService.Render(english, fields);
            KentuckyArticlesPdfService.RenderChinese(chinese, fields);
            using var left = PdfReader.Open(english, PdfDocumentOpenMode.Import);
            using var right = PdfReader.Open(chinese, PdfDocumentOpenMode.Import);
            left.PageCount.Should().Be(1);
            right.PageCount.Should().Be(1);
        }
        finally
        {
            if (File.Exists(english)) File.Delete(english);
            if (File.Exists(chinese)) File.Delete(chinese);
        }
    }

    [Fact]
    public void Wrap_MovesLineStartPunctuationBackToThePreviousLine()
    {
        var lines = ArticlesOfIncorporationPdfService.Wrap(
            "通过在提供的空格中填写我的姓名，我证明我有权代表该实体提交本文件，已审阅该文件，并据我所知和所信，截至2025年10月28日，该文件真实、准确且完整。",
            32,
            text => text.Length);

        lines.Should().AllSatisfy(line => "，。、；：！？".Contains(line[0]).Should().BeFalse());
        string.Join(string.Empty, lines).Should().Be(
            "通过在提供的空格中填写我的姓名，我证明我有权代表该实体提交本文件，已审阅该文件，并据我所知和所信，截至2025年10月28日，该文件真实、准确且完整。");
    }

    [Fact]
    public void Translate_UsesChineseNameAddressAndDate()
    {
        ArticlesChineseText.TranslateName("LINDA JO HARRIS").Should().Be("琳达·乔·哈里斯");
        ArticlesChineseText.TranslateShortName("LINDA JO HARRIS").Should().Be("琳达·哈里斯");
        ArticlesChineseText.TranslateStreet("2708 Maple Ct").Should().Be("2708 枫树巷");
        ArticlesChineseText.TranslateCity("Sumrall, MS 39482").Should().Be("萨姆罗尔, 密西西比州 39482");
        ArticlesChineseText.TranslateCity("New Orleans, LA 70112").Should().Be("新奥尔良, 路易斯安那州 70112");
        ArticlesChineseText.TranslateFiledAt("02/24/2025 09:29 PM").Should().Be("2025年02月24日 下午09:29");
        ArticlesChineseText.TranslateSignedOn("02/24/2025").Should().Be("2025年2月24日");
        const string mississippiEnglish = "Harbor Peak INC";
        const string kentuckyEnglish = "HARBOR 89RIDGE CAPITAL Inc.";
        new ArticlesOfIncorporationFields("", "", "", "", "", "", "", "", "", "", mississippiEnglish)
            .ResolvedChineseCompanyName().Should().Be(ArticlesChineseText.TranslateCompany(mississippiEnglish));
        new ArticlesOfIncorporationFields("", "", "", "", "", "", "", "", "", "", mississippiEnglish, "", "手改公司")
            .ResolvedChineseCompanyName().Should().Be("手改公司");
        new ArticlesOfIncorporationFields("", "", "", "", "", "", "", "", "", "", mississippiEnglish, "", "")
            .ResolvedChineseCompanyName().Should().BeEmpty();
        new KentuckyArticlesFields("", "", kentuckyEnglish, "", "", "")
            .ResolvedChineseCompanyName().Should().Be(ArticlesChineseText.TranslateKentuckyCompany(kentuckyEnglish));
        new KentuckyArticlesFields("", "", kentuckyEnglish, "", "", "", "手改局公司")
            .ResolvedChineseCompanyName().Should().Be("手改局公司");
        ArticlesChineseText.TranslateCompany("Harbor Peak INC").Should().Be("Harbor Peak 公司");
        ArticlesChineseText.TranslateCompany("SOZepyqwu INC").Should().Be("SOZepyqwu 公司");
        ArticlesChineseText.TranslateCompany("").Should().BeEmpty();
        ArticlesChineseText.TranslateKentuckyCompany("GRAYSON 74ROAM BUREAU Inc.").Should().Be("格雷森74罗姆局公司");
        ArticlesChineseText.TranslateKentuckyCompany("Summit Ridge INC").Should().Be("顶峰岭公司");
        ArticlesChineseText.TranslateKentuckyCompany("SOGDOITE 12VALLEY HOLDING Inc.").Should().Be("索格多伊特12瓦利控股公司");
        ArticlesChineseText.TranslateKentuckyAddress("27 Reins Ct", "Elizabethtown, KY 42701")
            .Should().Be("27 Reins Ct, 埃利萨伯敦, 肯塔基州 42701");
        ArticlesChineseText.TranslateKentuckyFiledAt("5/5/2026 8:10:52 AM").Should().Be("2026年5月5日 上午8:10:52");
        ArticlesChineseText.TranslateKentuckyEffectiveOn("Tuesday, May 5, 2026.").Should().Be("2026年5月5日（星期二）");
        ArticlesChineseText.TranslateName("ZELDA").Should().NotContainAny("A", "E", "L", "Z");
    }

    [Fact]
    public void RenderChinese_FillsTranslatedTemplate()
    {
        var output = Path.Combine(Path.GetTempPath(), $"articles-zh-{Guid.NewGuid():N}.pdf");
        try
        {
            ArticlesOfIncorporationPdfService.RenderChinese(output, new ArticlesOfIncorporationFields(
                "2025657032",
                "9292556",
                "02/24/2025 09:29 PM",
                "LINDA JO HARRIS",
                "2708 Maple Ct",
                "Sumrall, MS 39482",
                "02/24/2025",
                "LINDA HARRIS",
                "2708 Maple Ct",
                "Sumrall, MS 39482"));

            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(1);
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    [Fact]
    public void Render_FillsClearedTemplateWithSampleFields()
    {
        var keepSample = string.Equals(Environment.GetEnvironmentVariable("KEEP_ARTICLES_SAMPLE"), "1", StringComparison.Ordinal);
        var output = keepSample
            ? Path.Combine(FindRepositoryRoot(), "tmp", "articles-of-incorporation-sample.pdf")
            : Path.Combine(Path.GetTempPath(), $"articles-{Guid.NewGuid():N}.pdf");
        try
        {
            ArticlesOfIncorporationPdfService.Render(output, ArticlesOfIncorporationFields.Sample);

            new FileInfo(output).Length.Should().BeGreaterThan(100);
            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(1);
        }
        finally
        {
            if (!keepSample && File.Exists(output))
                File.Delete(output);
        }
    }

    [Fact]
    public void Render_AllowsEveryFieldToStayBlank()
    {
        var output = Path.Combine(Path.GetTempPath(), $"articles-blank-{Guid.NewGuid():N}.pdf");
        try
        {
            ArticlesOfIncorporationPdfService.Render(output, new ArticlesOfIncorporationFields(
                "", "", "", "", "", "", "", "", "", ""));

            using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            pdf.PageCount.Should().Be(1);
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "global.json")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static void WriteLibrary(string path, params string[] names) =>
        File.WriteAllText(path, JsonSerializer.Serialize(names));
}
