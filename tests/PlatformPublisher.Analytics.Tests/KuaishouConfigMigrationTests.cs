using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Persistence;
using ShortDrama.Core.Interfaces;
using Xunit;

namespace PlatformPublisher.Analytics.Tests;

public sealed class KuaishouConfigMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kuaishou-config-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LegacyImporterMapsCommonAndEditionSpecificFields()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """
            {
              "kuaishou_api_base_url": "https://example.test",
              "kuaishou_app_id": "app-id",
              "kuaishou_app_secret": "secret-value",
              "kuaishou_distribution_enabled": true,
              "kuaishou_distribution_default_rate_percent": 35,
              "kuaishou_product_method": "真人拍摄",
              "kuaishou_series_price_yuan": "2",
              "kuaishou_personal_real_name": "个人实名",
              "kuaishou_personal_last_workspace": "D:/personal-workspace",
              "kuaishou_enterprise_real_name": "企业实名"
            }
            """);

        var personal = new KuaishouPersonalConfig();
        var personalResult = KuaishouLegacyConfigImporter.Import(path, personal, PublishPlatform.KuaishouPersonalRevenue);
        Assert.Equal("https://example.test", personal.ApiBaseUrl);
        Assert.Equal("个人实名", personal.RealName);
        Assert.True(personal.DistributionEnabled);
        Assert.Equal(35, personal.DistributionDefaultRatePercent);
        Assert.Equal("真人拍摄", personal.ProductMethod);
        Assert.Equal("2", personal.SeriesPrice);
        Assert.Equal("D:/personal-workspace", personal.LastWorkspace);
        Assert.Equal(1, personalResult.ImportedSensitiveFields);

        var enterprise = new KuaishouPersonalConfig();
        KuaishouLegacyConfigImporter.Import(path, enterprise, PublishPlatform.KuaishouEnterpriseRevenue);
        Assert.Equal("企业实名", enterprise.RealName);
    }

    [Fact]
    public async Task SecretsAreExcludedFromJsonAndRoundTripThroughSecureStore()
    {
        Directory.CreateDirectory(_root);
        var database = new PlatformDatabase(Path.Combine(_root, "app.db"));
        var jsonStore = new AccountJsonSettingStore(database);
        var credentialStore = new KuaishouCredentialStore(new SecureBlobStore(database), new PassthroughProtector());
        KuaishouPersonalConfig.ConfigureDatabase(jsonStore, credentialStore);
        var job = new PublishJob { Platform = PublishPlatform.KuaishouPersonalRevenue, AccountId = "account-1" };
        var config = KuaishouPersonalConfig.Load(job);
        config.AppSecret = "secret-value";
        config.AccessToken = "access-value";
        var path = Path.Combine(_root, "config.json");

        await config.SaveAsync(path);

        var fileJson = File.ReadAllText(path);
        Assert.DoesNotContain("secret-value", fileJson);
        Assert.DoesNotContain("access-value", fileJson);
        var loaded = KuaishouPersonalConfig.Load(job);
        Assert.Equal("secret-value", loaded.AppSecret);
        Assert.Equal("access-value", loaded.AccessToken);
    }

    [Fact]
    public void EnterpriseUsesIndependentConfigurationPath()
    {
        var personal = KuaishouPersonalConfig.DefaultConfigPath("same-account", PublishPlatform.KuaishouPersonalRevenue);
        var enterprise = KuaishouPersonalConfig.DefaultConfigPath("same-account", PublishPlatform.KuaishouEnterpriseRevenue);
        Assert.NotEqual(personal, enterprise);
        Assert.Contains("kuaishou-enterprise", enterprise);
    }

    [Fact]
    public void ConfigurationValidatorRejectsInvalidDistributionSettings()
    {
        var config = new KuaishouPersonalConfig
        {
            DistributionEnabled = true,
            DistributionSubmitEnabled = true,
            DistributionDefaultRatePercent = 101,
            DistributionDistributorAccountsJson = "{invalid",
            ApiBaseUrl = string.Empty,
            AppId = string.Empty,
            AccessToken = string.Empty,
        };

        var issues = KuaishouConfigurationValidator.Validate(config);

        Assert.Contains(issues, issue => issue.Contains("分销比例", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("JSON", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("API Base URL", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("Access Token", StringComparison.Ordinal));
    }

    [Fact]
    public void ContentComplianceRejectsConfiguredBlockedTerms()
    {
        var config = new KuaishouPersonalConfig
        {
            SynopsisPolicyJson = """{"blockedTerms":["违规词"]}""",
        };
        var data = new KuaishouPersonalProjectData(
            _root, _root, "普通剧名", "简介包含违规词", "短标题", [], "", "", "", [], [], []);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new KuaishouContentComplianceService().Validate(data, config));

        Assert.Contains("违规词", exception.Message);
    }

    [Fact]
    public async Task DistributionDryRunDoesNotCallRemoteEndpoint()
    {
        var handler = new CountingHttpHandler();
        var service = new KuaishouDistributionService(new HttpClient(handler));
        var config = new KuaishouPersonalConfig
        {
            DistributionEnabled = true,
            StepDistributionSeries = true,
            DistributionSubmitEnabled = false,
        };

        await service.ApplyAsync("series-1", config, null, CancellationToken.None);

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CommitmentTemplateIsFilledBeforePdfRendering()
    {
        Directory.CreateDirectory(_root);
        var template = Path.Combine(_root, "template.docx");
        using (var document = WordprocessingDocument.Create(template, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("【北京晨钟科技有限公司】"))),
                new Paragraph(new Run(new Text("【公司】"))),
                new Paragraph(new Run(new Text("【剧名】")))));
            main.Document.Save();
        }
        var renderer = new InspectingDocumentRenderer();
        var service = new KuaishouCommitmentService(renderer);
        var config = new KuaishouPersonalConfig
        {
            CommitmentTemplateDocxPath = template,
            CommitmentRecipientCompanyName = "收函公司",
            ProductionOrganization = "制作公司",
        };
        var data = new KuaishouPersonalProjectData(
            _root, _root, "测试短剧", "简介", "短标题", [], "", "", "", [], [], []);

        var pdf = await service.ResolveAsync(data, config, CancellationToken.None);

        Assert.True(File.Exists(pdf));
        Assert.Contains("【收函公司】", renderer.RenderedDocumentText);
        Assert.Contains("【制作公司】", renderer.RenderedDocumentText);
        Assert.Contains("【测试短剧】", renderer.RenderedDocumentText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class PassthroughProtector : IDataProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class InspectingDocumentRenderer : IDocumentRenderService
    {
        public string RenderedDocumentText { get; private set; } = string.Empty;

        public Task<string> ConvertDocxToPdfAsync(
            string docxPath,
            string outputDir,
            CancellationToken cancellationToken)
        {
            using var document = WordprocessingDocument.Open(docxPath, false);
            RenderedDocumentText = document.MainDocumentPart?.Document.Body?.InnerText ?? string.Empty;
            var pdf = Path.Combine(outputDir, "快手承诺函.pdf");
            File.WriteAllText(pdf, "%PDF-test");
            return Task.FromResult(pdf);
        }

        public Task<string> ConvertPdfFirstPageToPngAsync(
            string pdfPath,
            string outputPngPath,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
