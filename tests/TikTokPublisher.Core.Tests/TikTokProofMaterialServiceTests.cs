using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using System.Text.Json;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProofMaterialServiceTests
{
    private static readonly byte[] TemplateSealBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static readonly byte[] ReplacementSealBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z8kAAAAAASUVORK5CYII=");

    [Fact]
    public void Request_defaults_to_wps_and_180_second_timeout()
    {
        var request = CreateRequest("template.docx", "证明材料.pdf");

        request.PreferredPdfRenderer.Should().Be(TikTokProofMaterialPdfRendererPreference.Wps);
        request.RenderTimeout.Should().Be(TimeSpan.FromSeconds(180));
        TikTokProofMaterialPdfRendererPreferenceExtensions.Parse(null)
            .Should().Be(TikTokProofMaterialPdfRendererPreference.Wps);
        TikTokProofMaterialPdfRendererPreferenceExtensions.Parse("libreoffice")
            .Should().Be(TikTokProofMaterialPdfRendererPreference.LibreOffice);
    }

    [Fact]
    public async Task Publish_item_prerequisite_skips_generation_when_cooperation_agreement_is_not_selected()
    {
        var account = new TikTokAccountProfile
        {
            TiktokCopyrightMaterialTypes =
            [
                "filing_or_distribution_license",
                "opening_ending_rights_notice",
            ],
        };

        var result = await TikTokProofMaterialService.EnsureCurrentForUploadAsync(
            new PublishItem(), account, log: null, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Publish_item_prerequisite_requires_current_project_directory_for_cooperation_agreement()
    {
        Func<Task<string>> action = () => TikTokProofMaterialService.EnsureCurrentForUploadAsync(
            new PublishItem(), new TikTokAccountProfile(), log: null, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未提供当前项目目录*workflow/证明材料.pdf*");
    }

    [Fact]
    public void Builder_replaces_split_text_and_preserves_floating_seal_and_formatting()
    {
        using var fixture = new ProofTemplateFixture();
        var templatePath = fixture.CreateTemplate();
        var sourceHash = File.ReadAllBytes(templatePath);
        var sourceSnapshot = ReadDocumentSnapshot(templatePath);
        var request = CreateRequest(templatePath, Path.Combine(fixture.DirectoryPath, "证明材料.pdf")) with
        {
            CopyrightCompanyName = "北京版权科技有限公司",
            DramaTitle = "替换后的新剧名",
            TemporaryDirectory = fixture.DirectoryPath,
        };

        var result = new TikTokProofMaterialDocumentBuilder().CreateTemporaryDocx(request);
        try
        {
            result.Replacements.Should().Be(
                new TikTokProofMaterialReplacementCounts(1, 2, 1, 1, 0));
            File.ReadAllBytes(templatePath).Should().Equal(sourceHash);

            var outputSnapshot = ReadDocumentSnapshot(result.DocxPath);
            outputSnapshot.Text.Should().Contain("致【北京版权科技有限公司】");
            outputSnapshot.Text.Should().Contain("剧名暂定【替换后的新剧名】");
            outputSnapshot.Text.Should().Contain("2026年【7】月【14】日");
            outputSnapshot.Text.Should().NotContain(TikTokProofMaterialDocumentBuilder.TemplateCopyrightCompanyName);
            outputSnapshot.Text.Should().NotContain(TikTokProofMaterialDocumentBuilder.TemplateDramaTitle);
            outputSnapshot.AnchorXml.Should().Be(sourceSnapshot.AnchorXml);
            outputSnapshot.ImageBytes.Should().Equal(sourceSnapshot.ImageBytes);
            outputSnapshot.BoldCount.Should().Be(sourceSnapshot.BoldCount);
        }
        finally
        {
            TikTokProofMaterialDocumentBuilder.TryDeleteDirectory(result.WorkingDirectory);
        }
    }

    [Fact]
    public void Builder_reports_locked_template_in_chinese()
    {
        using var fixture = new ProofTemplateFixture();
        var templatePath = fixture.CreateTemplate();
        var request = CreateRequest(
            templatePath,
            Path.Combine(fixture.DirectoryPath, "证明材料.pdf"));
        using var lockedTemplate = new FileStream(
            templatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var action = () => new TikTokProofMaterialDocumentBuilder().CreateTemporaryDocx(request);

        action.Should().Throw<IOException>()
            .WithMessage("*证明材料 Word 模板正在被其他程序占用*")
            .WithMessage("*请关闭 WPS、Word*");
    }

    [Fact]
    public void Builder_replaces_seal_bytes_without_rebuilding_floating_anchor()
    {
        using var fixture = new ProofTemplateFixture();
        var templatePath = fixture.CreateTemplate();
        var sealPath = Path.Combine(fixture.DirectoryPath, "new-seal.png");
        File.WriteAllBytes(sealPath, ReplacementSealBytes);
        var sourceSnapshot = ReadDocumentSnapshot(templatePath);
        var request = CreateRequest(templatePath, Path.Combine(fixture.DirectoryPath, "证明材料.pdf")) with
        {
            DeclarantCompanyName = "上海新主体科技有限公司",
            SealImagePath = sealPath,
            TemporaryDirectory = fixture.DirectoryPath,
        };

        var result = new TikTokProofMaterialDocumentBuilder().CreateTemporaryDocx(request);
        try
        {
            result.Replacements.SealImages.Should().Be(1);
            var outputSnapshot = ReadDocumentSnapshot(result.DocxPath);
            outputSnapshot.AnchorXml.Should().Be(sourceSnapshot.AnchorXml);
            outputSnapshot.ImageBytes.Should().Equal(ReplacementSealBytes);
            outputSnapshot.Text.Split("上海新主体科技有限公司").Length.Should().Be(3);
        }
        finally
        {
            TikTokProofMaterialDocumentBuilder.TryDeleteDirectory(result.WorkingDirectory);
        }
    }

    [Fact]
    public void Builder_rejects_declarant_change_without_matching_seal()
    {
        using var fixture = new ProofTemplateFixture();
        var templatePath = fixture.CreateTemplate();
        var request = CreateRequest(templatePath, Path.Combine(fixture.DirectoryPath, "证明材料.pdf")) with
        {
            DeclarantCompanyName = "另一家公司",
            TemporaryDirectory = fixture.DirectoryPath,
        };

        var action = () => new TikTokProofMaterialDocumentBuilder().CreateTemporaryDocx(request);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("声明公司与模板印章不一致，请配置印章。");
    }

    [Fact]
    public void Builder_rejects_template_when_exact_hit_count_does_not_match()
    {
        using var fixture = new ProofTemplateFixture();
        var templatePath = fixture.CreateTemplate(includeDramaTitle: false);
        var request = CreateRequest(templatePath, Path.Combine(fixture.DirectoryPath, "证明材料.pdf")) with
        {
            TemporaryDirectory = fixture.DirectoryPath,
        };

        var action = () => new TikTokProofMaterialDocumentBuilder().CreateTemporaryDocx(request);

        action.Should().Throw<InvalidDataException>()
            .WithMessage("*改写后剧名应命中 1 处，实际命中 0 处*");
    }

    [Fact]
    public async Task Pdf_render_uses_wps_first_then_libreoffice_fallback_and_atomically_replaces_output()
    {
        using var fixture = new ProofTemplateFixture();
        var docxPath = fixture.CreateTemplate();
        var outputPath = Path.Combine(fixture.DirectoryPath, TikTokProofMaterialService.ProofPdfFileName);
        await File.WriteAllTextAsync(outputPath, "old-pdf");
        var calls = new List<string>();
        var wps = new StubRenderer("WPS", (_, _, _) =>
        {
            calls.Add("WPS");
            throw new InvalidOperationException("wps unavailable");
        });
        var libreOffice = new StubRenderer("LibreOffice", async (_, path, ct) =>
        {
            calls.Add("LibreOffice");
            await File.WriteAllBytesAsync(path, "%PDF-1.7\nvalid"u8.ToArray(), ct);
        });
        var service = new TikTokProofMaterialPdfRenderService(wps, libreOffice);

        var result = await service.RenderAsync(docxPath, outputPath);

        calls.Should().Equal("WPS", "LibreOffice");
        result.RendererName.Should().Be("LibreOffice");
        File.ReadAllBytes(outputPath).Take(5).Should().Equal("%PDF-"u8.ToArray());
        Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp.pdf").Should().BeEmpty();
    }

    [Fact]
    public async Task Pdf_render_keeps_existing_output_when_all_renderers_fail_validation()
    {
        using var fixture = new ProofTemplateFixture();
        var docxPath = fixture.CreateTemplate();
        var outputPath = Path.Combine(fixture.DirectoryPath, TikTokProofMaterialService.ProofPdfFileName);
        var originalBytes = "%PDF-1.4\nold-valid"u8.ToArray();
        await File.WriteAllBytesAsync(outputPath, originalBytes);
        var wps = new StubRenderer("WPS", (_, path, _) =>
        {
            File.WriteAllText(path, "not a pdf");
            return Task.CompletedTask;
        });
        var libreOffice = new StubRenderer("LibreOffice", (_, _, _) =>
            throw new InvalidOperationException("fallback unavailable"));
        var service = new TikTokProofMaterialPdfRenderService(wps, libreOffice);

        var action = () => service.RenderAsync(docxPath, outputPath);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*WPS*LibreOffice*");
        File.ReadAllBytes(outputPath).Should().Equal(originalBytes);
    }

    [Fact]
    public void Pdf_validation_rejects_files_over_platform_limit()
    {
        using var fixture = new ProofTemplateFixture();
        var outputPath = Path.Combine(fixture.DirectoryPath, TikTokProofMaterialService.ProofPdfFileName);
        using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write("%PDF-"u8);
            stream.SetLength(TikTokProofMaterialPdfRenderService.MaxPlatformPdfBytes + 1);
        }

        var action = () => TikTokProofMaterialPdfRenderService.ValidatePdf(outputPath);

        action.Should().Throw<InvalidDataException>()
            .WithMessage("*超过 TikTok 平台 10 MB 限制*");
    }

    [Fact]
    public void Wps_script_uses_expected_com_ids_and_closes_without_saving()
    {
        var script = WpsProofMaterialPdfRenderer.BuildPowerShellScript(
            @"C:\work\proof.docx",
            @"C:\work\proof.pdf",
            @"C:\WPS\wps.exe");

        script.Should().Contain("'KWPS.Application', 'wps.Application'");
        script.Should().Contain("$documents.Open($docPath, $false, $true)");
        script.Should().Contain("$doc.Close(0)");
        script.Should().Contain("$app.Quit()");
        script.Should().Contain("ExportAsFixedFormat");
    }

    [Fact]
    public void Queue_request_requires_new_title_and_never_falls_back_to_original_title()
    {
        var item = new QueueProjectItem
        {
            ProjectDir = Path.GetTempPath(),
            OriginalTitle = "原剧名",
            NewTitle = "",
        };
        var settings = new ClientSettings();
        var account = new TikTokAccountProfile { TiktokProofCopyrightCompanyName = "版权公司" };

        var action = () => TikTokProofMaterialService.CreateQueueRequest(
            item,
            settings,
            account,
            Path.GetTempPath(),
            new DateOnly(2026, 7, 14));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*改写后剧名不能为空*");
    }

    [Fact]
    public void Account_profile_reads_legacy_subject_json_into_copyright_company_and_writes_only_canonical_name()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var account = JsonSerializer.Deserialize<TikTokAccountProfile>(
            """{"tiktokProofSubjectCompanyName":"旧版权公司"}""",
            options)!;

        account.TiktokProofCopyrightCompanyName.Should().Be("旧版权公司");
        account.TiktokProofSubjectCompanyName.Should().Be("旧版权公司");

        var serialized = JsonSerializer.Serialize(account, options);
        using var document = JsonDocument.Parse(serialized);
        document.RootElement.GetProperty("tiktokProofCopyrightCompanyName").GetString()
            .Should().Be("旧版权公司");
        document.RootElement.TryGetProperty("tiktokProofSubjectCompanyName", out _).Should().BeFalse();
    }

    [Fact]
    public void Queue_request_uses_account_level_proof_configuration_before_legacy_globals()
    {
        using var fixture = new ProofTemplateFixture();
        var item = new QueueProjectItem { NewTitle = "账号配置剧名" };
        var settings = new ClientSettings
        {
            TiktokProofTemplateDocxPath = fixture.CreateTemplate(),
            TiktokProofDeclarantCompanyName = "旧全局声明公司",
            TiktokProofSealPath = @"C:\legacy\seal.png",
            TiktokProofWpsPath = @"C:\WPS\wps.exe",
        };
        var account = new TikTokAccountProfile
        {
            TiktokProofCopyrightCompanyName = "账号版权公司",
            TiktokProofDeclarantCompanyName = "账号声明公司",
            TiktokProofSealPath = @"C:\account\seal.png",
            TiktokProofAccountConfigMigrated = true,
        };

        var request = TikTokProofMaterialService.CreateQueueRequest(
            item,
            settings,
            account,
            Path.GetTempPath(),
            new DateOnly(2026, 7, 14));

        request.CopyrightCompanyName.Should().Be("账号版权公司");
        request.DeclarantCompanyName.Should().Be("账号声明公司");
        request.SealImagePath.Should().Be(@"C:\account\seal.png");
        request.WpsExecutablePath.Should().Be(@"C:\WPS\wps.exe");
    }

    [Fact]
    public void Queue_requests_keep_proof_material_configuration_isolated_between_accounts()
    {
        using var fixture = new ProofTemplateFixture();
        var item = new QueueProjectItem { NewTitle = "账号独立配置剧名" };
        var settings = new ClientSettings
        {
            TiktokProofTemplateDocxPath = fixture.CreateTemplate(),
        };
        var firstAccount = new TikTokAccountProfile
        {
            TiktokProofCopyrightCompanyName = "甲方版权公司",
            TiktokProofDeclarantCompanyName = "甲方本公司",
            TiktokProofSealPath = @"C:\accounts\first-seal.png",
            TiktokProofAccountConfigMigrated = true,
        };
        var secondAccount = new TikTokAccountProfile
        {
            TiktokProofCopyrightCompanyName = "乙方版权公司",
            TiktokProofDeclarantCompanyName = "乙方本公司",
            TiktokProofSealPath = @"C:\accounts\second-seal.png",
            TiktokProofAccountConfigMigrated = true,
        };

        var firstRequest = TikTokProofMaterialService.CreateQueueRequest(
            item,
            settings,
            firstAccount,
            Path.Combine(Path.GetTempPath(), "first"),
            new DateOnly(2026, 7, 14));
        var secondRequest = TikTokProofMaterialService.CreateQueueRequest(
            item,
            settings,
            secondAccount,
            Path.Combine(Path.GetTempPath(), "second"),
            new DateOnly(2026, 7, 14));

        firstRequest.CopyrightCompanyName.Should().Be("甲方版权公司");
        firstRequest.DeclarantCompanyName.Should().Be("甲方本公司");
        firstRequest.SealImagePath.Should().Be(@"C:\accounts\first-seal.png");
        secondRequest.CopyrightCompanyName.Should().Be("乙方版权公司");
        secondRequest.DeclarantCompanyName.Should().Be("乙方本公司");
        secondRequest.SealImagePath.Should().Be(@"C:\accounts\second-seal.png");
    }

    [Fact]
    public void Queue_request_uses_legacy_globals_only_until_account_config_is_migrated()
    {
        using var fixture = new ProofTemplateFixture();
        var item = new QueueProjectItem { NewTitle = "兼容剧名" };
        var settings = new ClientSettings
        {
            TiktokProofTemplateDocxPath = fixture.CreateTemplate(),
            TiktokProofDeclarantCompanyName = "旧全局声明公司",
            TiktokProofSealPath = @"C:\legacy\seal.png",
        };
        var legacyAccount = new TikTokAccountProfile
        {
            TiktokProofSubjectCompanyName = "旧账号版权公司",
            TiktokProofAccountConfigMigrated = false,
        };

        var legacyRequest = TikTokProofMaterialService.CreateQueueRequest(
            item,
            settings,
            legacyAccount,
            Path.GetTempPath(),
            new DateOnly(2026, 7, 14));
        legacyRequest.CopyrightCompanyName.Should().Be("旧账号版权公司");
        legacyRequest.DeclarantCompanyName.Should().Be("旧全局声明公司");
        legacyRequest.SealImagePath.Should().Be(@"C:\legacy\seal.png");

        legacyAccount.TiktokProofAccountConfigMigrated = true;
        var migratedRequest = TikTokProofMaterialService.CreateQueueRequest(
            item,
            settings,
            legacyAccount,
            Path.GetTempPath(),
            new DateOnly(2026, 7, 14));
        migratedRequest.DeclarantCompanyName.Should().BeEmpty();
        migratedRequest.SealImagePath.Should().BeEmpty();
    }

    [Fact]
    public void Account_store_migrates_legacy_global_values_once_without_overwriting_account_values()
    {
        var account = new TikTokAccountProfile
        {
            TiktokProofCopyrightCompanyName = "旧账号版权公司",
            TiktokProofDeclarantCompanyName = "账号已有声明公司",
            TiktokProofSealPath = "",
            TiktokProofAccountConfigMigrated = false,
        };
        var legacySettings = new ClientSettings
        {
            TiktokProofDeclarantCompanyName = "旧全局声明公司",
            TiktokProofSealPath = @"C:\legacy\seal.png",
        };

        AccountStore.ApplyLegacyProofMaterialConfig([account], legacySettings).Should().BeTrue();
        account.TiktokProofDeclarantCompanyName.Should().Be("账号已有声明公司");
        account.TiktokProofSealPath.Should().Be(@"C:\legacy\seal.png");
        account.TiktokProofAccountConfigMigrated.Should().BeTrue();

        legacySettings.TiktokProofSealPath = @"C:\changed\seal.png";
        AccountStore.ApplyLegacyProofMaterialConfig([account], legacySettings).Should().BeFalse();
        account.TiktokProofSealPath.Should().Be(@"C:\legacy\seal.png");
    }

    [Fact]
    public void Account_store_marks_new_profile_skeleton_as_account_configured()
    {
        var method = typeof(AccountStore).GetMethod(
            "CreateProfileSkeleton",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        var account = (TikTokAccountProfile)method!.Invoke(null, ["acct-test", "测试账号"])!;

        account.TiktokProofAccountConfigMigrated.Should().BeTrue();
    }

    [Fact]
    public void Fingerprint_changes_with_date_renderer_wps_path_and_china_date_uses_utc_plus_eight()
    {
        using var fixture = new ProofTemplateFixture();
        var templatePath = fixture.CreateTemplate();
        var first = CreateRequest(templatePath, Path.Combine(fixture.DirectoryPath, "证明材料.pdf"));
        var nextDay = first with { StatementDate = new DateOnly(2026, 7, 15) };
        var libreOffice = first with
        {
            PreferredPdfRenderer = TikTokProofMaterialPdfRendererPreference.LibreOffice,
        };
        var customWps = first with { WpsExecutablePath = @"C:\WPS\wps.exe" };

        TikTokProofMaterialService.ComputeFingerprint(first)
            .Should().NotBe(TikTokProofMaterialService.ComputeFingerprint(nextDay));
        TikTokProofMaterialService.ComputeFingerprint(first)
            .Should().NotBe(TikTokProofMaterialService.ComputeFingerprint(libreOffice));
        TikTokProofMaterialService.ComputeFingerprint(first)
            .Should().NotBe(TikTokProofMaterialService.ComputeFingerprint(customWps));
        TikTokProofMaterialService.GetChinaToday(
                new FixedTimeProvider(new DateTimeOffset(2026, 7, 13, 16, 30, 0, TimeSpan.Zero)))
            .Should().Be(new DateOnly(2026, 7, 14));
    }

    private static TikTokProofMaterialRequest CreateRequest(string templatePath, string outputPath) =>
        new(
            templatePath,
            outputPath,
            TikTokProofMaterialDocumentBuilder.TemplateCopyrightCompanyName,
            TikTokProofMaterialDocumentBuilder.TemplateDeclarantCompanyName,
            "新剧名",
            new DateOnly(2026, 7, 14));

    private static DocumentSnapshot ReadDocumentSnapshot(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var mainPart = document.MainDocumentPart!;
        var body = mainPart.Document.Body!;
        var text = string.Concat(body.Descendants<W.Text>().Select(node => node.Text));
        var anchorXml = body.Descendants<DW.Anchor>().Single().OuterXml;
        var imagePart = mainPart.ImageParts.Single();
        using var imageStream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
        using var imageBuffer = new MemoryStream();
        imageStream.CopyTo(imageBuffer);
        return new DocumentSnapshot(
            text,
            anchorXml,
            imageBuffer.ToArray(),
            body.Descendants<W.Bold>().Count());
    }

    private sealed record DocumentSnapshot(
        string Text,
        string AnchorXml,
        byte[] ImageBytes,
        int BoldCount);

    private sealed class StubRenderer(
        string name,
        Func<string, string, CancellationToken, Task> render) : ITikTokProofMaterialPdfRenderer
    {
        public string Name { get; } = name;

        public Task RenderAsync(
            string docxPath,
            string outputPdfPath,
            TikTokProofMaterialPdfRenderOptions options,
            CancellationToken cancellationToken) =>
            render(docxPath, outputPdfPath, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ProofTemplateFixture : IDisposable
    {
        public ProofTemplateFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"proof-material-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string CreateTemplate(bool includeDramaTitle = true)
        {
            var path = Path.Combine(DirectoryPath, $"template-{Guid.NewGuid():N}.docx");
            using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new W.Document(new W.Body());
            var imagePart = mainPart.AddImagePart(ImagePartType.Png);
            using (var stream = new MemoryStream(TemplateSealBytes, writable: false))
            {
                imagePart.FeedData(stream);
            }

            var imageRelationshipId = mainPart.GetIdOfPart(imagePart);
            var body = mainPart.Document.Body!;
            body.Append(
                new W.Paragraph(
                    new W.Run(
                        new W.RunProperties(new W.Bold()),
                        new W.Text("致【武汉")),
                    new W.Run(new W.Text("星漫光年科技有限公司】（下称 “贵方”）"))),
                new W.Paragraph(
                    new W.Run(new W.Text("本公司【武汉速")),
                    new W.Run(new W.Text("视科技有限公司】与贵方完整签署协议，剧名暂定【")),
                    new W.Run(new W.Text(includeDramaTitle ? "创业路上" : "不存在的")),
                    new W.Run(new W.Text(includeDramaTitle ? "闺蜜反目维权】。" : "模板标题】。"))),
                new W.Paragraph(
                    new W.Run(new W.Text("声明人：【武汉速视科技有限公司】 ")),
                    new W.Run(CreateFloatingSeal(imageRelationshipId))),
                new W.Paragraph(
                    new W.Run(new W.Text("2026")),
                    new W.Run(new W.Text("年【")),
                    new W.Run(new W.Text("7")),
                    new W.Run(new W.Text("】月【")),
                    new W.Run(new W.Text("13")),
                    new W.Run(new W.Text("】日"))));
            mainPart.Document.Save();
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
            }
        }

        private static W.Drawing CreateFloatingSeal(string relationshipId)
        {
            const long width = 1_800_000L;
            const long height = 1_800_000L;
            var picture = new PIC.Picture(
                new PIC.NonVisualPictureProperties(
                    new PIC.NonVisualDrawingProperties { Id = 1U, Name = "template-seal.png" },
                    new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(
                    new A.Blip { Embed = relationshipId },
                    new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = 0L, Y = 0L },
                        new A.Extents { Cx = width, Cy = height }),
                    new A.PresetGeometry(new A.AdjustValueList())
                    {
                        Preset = A.ShapeTypeValues.Rectangle,
                    }));
            var graphic = new A.Graphic(
                new A.GraphicData(picture)
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
                });
            var anchor = new DW.Anchor(
                new DW.SimplePosition { X = 0L, Y = 0L },
                new DW.HorizontalPosition(new DW.PositionOffset("0"))
                {
                    RelativeFrom = DW.HorizontalRelativePositionValues.Column,
                },
                new DW.VerticalPosition(new DW.PositionOffset("0"))
                {
                    RelativeFrom = DW.VerticalRelativePositionValues.Paragraph,
                },
                new DW.Extent { Cx = width, Cy = height },
                new DW.EffectExtent
                {
                    LeftEdge = 0L,
                    TopEdge = 0L,
                    RightEdge = 0L,
                    BottomEdge = 0L,
                },
                new DW.WrapNone(),
                new DW.DocProperties { Id = 1U, Name = "template-seal.png" },
                new DW.NonVisualGraphicFrameDrawingProperties(
                    new A.GraphicFrameLocks { NoChangeAspect = true }),
                graphic)
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
                SimplePos = false,
                RelativeHeight = 251_658_240U,
                BehindDoc = false,
                Locked = false,
                LayoutInCell = true,
                AllowOverlap = true,
            };
            return new W.Drawing(anchor);
        }
    }
}
