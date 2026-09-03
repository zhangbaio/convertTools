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

    [Fact]
    public void OnlineQueueIsIsolatedByAccountAndEdition()
    {
        Directory.CreateDirectory(_root);
        var database = new PlatformDatabase(Path.Combine(_root, "queue.db"));
        var queue = new KuaishouOnlineQueueStore(new AccountJsonSettingStore(database));
        var data = new KuaishouPersonalProjectData(
            _root, _root, "待上架短剧", "简介", "短标题", [], "", "", "", [], [], []);
        var state = new KuaishouPersonalUploadState
        {
            MiniSeriesId = "12345",
            ReviewSubmitted = true,
        };
        var config = new KuaishouPersonalConfig
        {
            AutoOnlineEnabled = true,
            AdvertiserId = "67890",
        };
        var job = new PublishJob
        {
            AccountId = "account-a",
            Platform = PublishPlatform.KuaishouPersonalRevenue,
        };

        var item = queue.Register(job, data, state, config);

        Assert.NotNull(item);
        Assert.Single(queue.Load("account-a", PublishPlatform.KuaishouPersonalRevenue));
        Assert.Empty(queue.Load("account-b", PublishPlatform.KuaishouPersonalRevenue));
        Assert.Empty(queue.Load("account-a", PublishPlatform.KuaishouEnterpriseRevenue));
    }

    [Fact]
    public async Task OnlineProcessorChecksAuditBeforeCallingOnlineEndpoint()
    {
        Directory.CreateDirectory(_root);
        var database = new PlatformDatabase(Path.Combine(_root, "processor.db"));
        var jsonStore = new AccountJsonSettingStore(database);
        var credentialStore = new KuaishouCredentialStore(new SecureBlobStore(database), new PassthroughProtector());
        KuaishouPersonalConfig.ConfigureDatabase(jsonStore, credentialStore);
        var job = new PublishJob { AccountId = "processor-account", Platform = PublishPlatform.KuaishouPersonalRevenue };
        var config = KuaishouPersonalConfig.Load(job);
        config.AutoOnlineEnabled = true;
        config.ApiBaseUrl = "https://example.test";
        config.AccessToken = "token";
        config.AdvertiserId = "67890";
        await config.SaveAsync(KuaishouPersonalConfig.DefaultConfigPath(job.AccountId, job.Platform));

        var queue = new KuaishouOnlineQueueStore(jsonStore);
        var data = new KuaishouPersonalProjectData(
            _root, _root, "审核通过短剧", "简介", "短标题", [], "", "", "", [], [], []);
        queue.Register(job, data, new KuaishouPersonalUploadState { MiniSeriesId = "12345", ReviewSubmitted = true }, config);
        var items = queue.Load(job.AccountId, job.Platform).ToList();
        items[0].NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        queue.Save(job.AccountId, job.Platform, items);
        var handler = new SequenceHttpHandler(
            """{"data":{"audit_status":3,"selling_status":2}}""",
            """{"code":0,"message":"ok"}""");
        var httpClient = new HttpClient(handler);
        var processor = new KuaishouOnlineQueueProcessor(
            httpClient,
            queue,
            new KuaishouDistributionService(httpClient));

        var count = await processor.ProcessDueAsync(job.AccountId, job.Platform, null, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/seriesBaseInfo", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.EndsWith("/onlineOfflineManage", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("online", Assert.Single(queue.Load(job.AccountId, job.Platform)).Status);
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

    private sealed class SequenceHttpHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = responses[Math.Min(_index++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
            });
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
