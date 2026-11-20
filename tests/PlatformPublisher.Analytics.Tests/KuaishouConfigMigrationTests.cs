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
    public void FixedSeriesDefaultsIgnoreSavedAndImportedOverrides()
    {
        var config = JsonSerializer.Deserialize<KuaishouPersonalConfig>(
            """{"Finished":false,"HasRecordNumber":true,"SmallAmountUnlock":true}""")!;
        using var legacy = JsonDocument.Parse(
            """{"kuaishou_is_finished":false,"kuaishou_has_record_number":true,"kuaishou_small_amount_unlock":true}""");

        var import = KuaishouLegacyConfigImporter.ImportElement(
            legacy.RootElement, config, PublishPlatform.KuaishouPersonalRevenue);
        var form = KuaishouPersonalFormResolver.Resolve(config);

        Assert.Equal(0, import.ImportedFields);
        Assert.True(form.Finished);
        Assert.False(form.HasRecordNumber);
        Assert.False(form.SmallAmountUnlock);
    }

    [Fact]
    public void ActorJsonAndTemplateRootCannotBeOverriddenByAccountConfiguration()
    {
        var config = JsonSerializer.Deserialize<KuaishouPersonalConfig>(
            """{"ActorInfoJson":"[]","ProjectImageTemplateRoot":"old-template-root"}""")!;
        using var legacy = JsonDocument.Parse(
            """{"kuaishou_actor_info_json":"[]","kuaishou_project_image_template_root":"new-template-root"}""");

        var import = KuaishouLegacyConfigImporter.ImportElement(
            legacy.RootElement, config, PublishPlatform.KuaishouPersonalRevenue);
        var saved = JsonSerializer.Serialize(config);

        Assert.Equal(0, import.ImportedFields);
        Assert.Equal("""[{"name":"","gender":"男","role":""},{"name":"","gender":"女","role":""}]""", config.ActorInfoJson);
        Assert.DoesNotContain("ActorInfoJson", saved);
        Assert.DoesNotContain("ProjectImageTemplateRoot", saved);
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
    public async Task PersonalAndEnterpriseLoadTheSameAccountConfiguration()
    {
        Directory.CreateDirectory(_root);
        var database = new PlatformDatabase(Path.Combine(_root, "shared-config.db"));
        var jsonStore = new AccountJsonSettingStore(database);
        var credentialStore = new KuaishouCredentialStore(new SecureBlobStore(database), new PassthroughProtector());
        KuaishouPersonalConfig.ConfigureDatabase(jsonStore, credentialStore);
        var personalJob = new PublishJob
        {
            Platform = PublishPlatform.KuaishouPersonalRevenue,
            AccountId = "same-account",
        };
        var config = KuaishouPersonalConfig.Load(personalJob);
        config.KuaishouNickname = "同一账号";
        config.Username = "张彪";
        await config.SaveAsync(Path.Combine(_root, "shared.json"));

        var enterprise = KuaishouPersonalConfig.Load(new PublishJob
        {
            Platform = PublishPlatform.KuaishouEnterpriseRevenue,
            AccountId = "same-account",
        });
        Assert.Equal("同一账号", enterprise.KuaishouNickname);
        Assert.Equal("张彪", enterprise.Username);
        Assert.Equal(PublishPlatform.KuaishouEnterpriseRevenue, enterprise.StoragePlatform);
    }

    [Fact]
    public void ConfigurationValidatorIgnoresRetiredDistributionSettings()
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

        Assert.Empty(issues);
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
    public async Task OnlineProcessorChecksAuditAndIgnoresRetiredDistributionSettings()
    {
        Directory.CreateDirectory(_root);
        var database = new PlatformDatabase(Path.Combine(_root, "processor.db"));
        var jsonStore = new AccountJsonSettingStore(database);
        var credentialStore = new KuaishouCredentialStore(new SecureBlobStore(database), new PassthroughProtector());
        KuaishouPersonalConfig.ConfigureDatabase(jsonStore, credentialStore);
        var job = new PublishJob { AccountId = "processor-account", Platform = PublishPlatform.KuaishouPersonalRevenue };
        var config = KuaishouPersonalConfig.Load(job);
        config.AutoOnlineEnabled = true;
        config.OnlineAutoDistributionEnabled = true;
        config.DistributionEnabled = true;
        config.StepDistributionSeries = true;
        config.DistributionSubmitEnabled = true;
        config.ApiBaseUrl = "https://example.test";
        config.AccessToken = "token";
        config.AdvertiserId = "67890";
        config.UploadTransport = KuaishouUploadTransport.Playwright;
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
        var processor = new KuaishouOnlineQueueProcessor(httpClient, queue);

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
