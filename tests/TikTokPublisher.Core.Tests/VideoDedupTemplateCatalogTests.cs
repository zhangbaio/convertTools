using System.IO.Compression;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;
using Xunit;

namespace TikTokPublisher.Core.Tests;

[Collection("Video dedup template data root")]
public sealed class VideoDedupTemplateCatalogTests
{
    [Fact]
    public void OpensLegacyTemplatesAndKeepsExistingAccountParameters()
    {
        using var fixture = new CatalogFixture();
        var old = new VideoDedupPreset { Name = "短分集", MinimumDurationSeconds = 35, MaximumDurationSeconds = 45 };
        var legacy = Path.Combine(fixture.Path, "video-dedup-templates.json");
        var legacyContents = JsonSerializer.Serialize(new[] { new { Id = "short", Preset = old } });
        File.WriteAllText(legacy, legacyContents);
        var original = new VideoDedupPreset { TrimEndSeconds = 1.5, Parallel = 3 };
        var accounts = new[] { Account("account1", old.Clone()), Account("account2", original), Account("account3") };

        var first = fixture.Catalog.Load(accounts);
        Assert.Equal("系统默认", first.Templates[0].Name);
        Assert.Equal("short", first.DefaultFor("account1").Id["legacy-".Length..]);
        Assert.Equal("短分集", first.DefaultFor("account1").Name);
        Assert.Equal(original.Fingerprint(), first.DefaultFor("account2").Preset!.Fingerprint());
        Assert.Null(first.DefaultFor("account3").Preset);
        var second = fixture.Catalog.Load(accounts);
        Assert.Equal(first.Templates.Select(item => item.Id), second.Templates.Select(item => item.Id));
        Assert.Equal(legacyContents, File.ReadAllText(legacy));
    }

    [Fact]
    public void DeletingAReferencedTemplateReplacesAllReferencesTogether()
    {
        using var fixture = new CatalogFixture();
        var a = fixture.Catalog.Save(null, "方案一", new VideoDedupPreset { TrimStartSeconds = 1 });
        var b = fixture.Catalog.Save(null, "方案二", new VideoDedupPreset { TrimEndSeconds = 1 });
        fixture.Catalog.SetDefault("first", a.Id);
        fixture.Catalog.SetDefault("second", a.Id);
        Assert.Throws<InvalidOperationException>(() => fixture.Catalog.Delete(a.Id));
        Assert.Equal(a.Id, fixture.Catalog.Load([]).DefaultFor("first").Id);

        fixture.Catalog.Delete(a.Id, b.Id);
        var result = fixture.Catalog.Load([]);
        Assert.All(result.AccountDefaults.Values, id => Assert.Equal(b.Id, id));
        Assert.DoesNotContain(result.Templates, item => item.Id == a.Id);
        Assert.Throws<InvalidOperationException>(() => fixture.Catalog.Delete(VideoDedupTemplateCatalog.SystemId));
    }

    [Fact]
    public void SavingNewConfigurationCreatesAndBindsTheTemplateToTheCurrentAccount()
    {
        using var fixture = new CatalogFixture();
        var saved = fixture.Catalog.SaveAccountConfiguration("first", null, "新配置",
            new VideoDedupPreset { TrimStartSeconds = 1.5 });

        var snapshot = fixture.Catalog.Load([]);
        Assert.Equal(saved.Id, snapshot.DefaultFor("first").Id);
        Assert.Equal("新配置", snapshot.DefaultFor("first").Name);
        Assert.Equal(1.5d, snapshot.DefaultFor("first").Preset!.TrimStartSeconds);
        Assert.Equal(VideoDedupTemplateCatalog.SystemId, snapshot.DefaultFor("second").Id);
    }

    [Fact]
    public void SavingSharedConfigurationUpdatesItsParametersAndBindsTheSelectedAccount()
    {
        using var fixture = new CatalogFixture();
        var template = fixture.Catalog.Save(null, "共享方案", new VideoDedupPreset { TrimEndSeconds = 1 });
        fixture.Catalog.SetDefault("second", template.Id);

        fixture.Catalog.SaveAccountConfiguration("first", template.Id, "更新名称",
            new VideoDedupPreset { TrimEndSeconds = 2 });

        var snapshot = fixture.Catalog.Load([]);
        Assert.Equal(template.Id, snapshot.DefaultFor("first").Id);
        Assert.Equal(template.Id, snapshot.DefaultFor("second").Id);
        Assert.Equal("更新名称", snapshot.DefaultFor("first").Name);
        Assert.Equal(2d, snapshot.DefaultFor("second").Preset!.TrimEndSeconds);
    }

    [Fact]
    public void SavingOnlyASelectionUsesTheLatestSharedParametersOrSystemDefault()
    {
        using var fixture = new CatalogFixture();
        var template = fixture.Catalog.Save(null, "原名称", new VideoDedupPreset { TrimEndSeconds = 1 });
        fixture.Catalog.Save(template.Id, "新名称", new VideoDedupPreset { TrimEndSeconds = 3 });
        var saved = fixture.Catalog.SaveAccountConfiguration("first", template.Id);
        Assert.Equal("新名称", saved.Name);
        Assert.Equal(3d, saved.Preset!.TrimEndSeconds);
        Assert.Equal(template.Id, fixture.Catalog.Load([]).DefaultFor("first").Id);

        var system = fixture.Catalog.SaveAccountConfiguration("first", VideoDedupTemplateCatalog.SystemId);
        var snapshot = fixture.Catalog.Load([]);
        Assert.Null(system.Preset);
        Assert.Equal(VideoDedupTemplateCatalog.SystemId, snapshot.DefaultFor("first").Id);
        Assert.Equal(3d, Assert.Single(snapshot.Templates, item => item.Id == template.Id).Preset!.TrimEndSeconds);
    }

    [Fact]
    public void InvalidConfigurationLeavesBothTheTemplateAndAccountBindingUnchanged()
    {
        using var fixture = new CatalogFixture();
        var original = fixture.Catalog.Save(null, "原配置", new VideoDedupPreset { TrimEndSeconds = 1 });
        var other = fixture.Catalog.Save(null, "其他配置", new VideoDedupPreset { TrimEndSeconds = 2 });
        fixture.Catalog.SetDefault("first", original.Id);
        var file = Assert.Single(Directory.GetFiles(fixture.Path));
        var before = File.ReadAllText(file);

        Assert.Throws<InvalidOperationException>(() => fixture.Catalog.SaveAccountConfiguration(
            "first", other.Id, original.Name, new VideoDedupPreset { TrimEndSeconds = 3 }));

        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal(original.Id, fixture.Catalog.Load([]).DefaultFor("first").Id);
    }

    [Fact]
    public void CapturedRunPresetsKeepTheValuesWhenTemplatesAreEdited()
    {
        using var fixture = new CatalogFixture();
        var template = fixture.Catalog.Save(null, "共享模板", new VideoDedupPreset { TrimEndSeconds = 1 });
        var accounts = new[] { Account("first", new VideoDedupPreset { TrimEndSeconds = 3 }), Account("second") };
        fixture.Catalog.SetDefault("first", template.Id);
        fixture.Catalog.SetDefault("second", VideoDedupTemplateCatalog.SystemId);
        var captured = fixture.Catalog.CaptureRunPresets(accounts, null);
        fixture.Catalog.SaveAccountConfiguration("first", template.Id, "共享模板", new VideoDedupPreset { TrimEndSeconds = 2 });

        Assert.Equal(1d, captured["first"]!.TrimEndSeconds);
        Assert.Null(captured["second"]);
        Assert.Equal(2d, fixture.Catalog.CaptureRunPresets(accounts, null)["first"]!.TrimEndSeconds);
        Assert.Equal(3d, accounts[0].VideoDedupPreset!.TrimEndSeconds);
    }

    [Fact]
    public void RunningWithOnlyLegacyAccountParametersDoesNotCreateATemplate()
    {
        using var fixture = new CatalogFixture();
        var account = Account("legacy", new VideoDedupPreset { TrimEndSeconds = 2 });
        Assert.Equal(2d, fixture.Catalog.CaptureRunPresets([account], null)["legacy"]!.TrimEndSeconds);
        Assert.Empty(Directory.GetFiles(fixture.Path));
    }

    [Fact]
    public void RunningBeforeMigrationResolvesLegacyTemplateIdsWithoutWritingTheCatalog()
    {
        using var fixture = new CatalogFixture();
        var path = Path.Combine(fixture.Path, "video-dedup-templates.json");
        var template = new VideoDedupPreset { Name = "已绑定", TrimEndSeconds = 3 };
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { new { Id = "existing", Preset = template } }));
        var first = Account("first", new VideoDedupPreset { TrimEndSeconds = 1 });
        first.VideoDedupTemplateId = "existing";
        var second = Account("second", new VideoDedupPreset { TrimEndSeconds = 2 });
        second.VideoDedupTemplateId = VideoDedupTemplateCatalog.SystemId;
        var presets = fixture.Catalog.CaptureRunPresets([first, second], null);
        Assert.Equal(3d, presets["first"]!.TrimEndSeconds);
        Assert.Null(presets["second"]);
        Assert.Equal(path, Assert.Single(Directory.GetFiles(fixture.Path)));
    }

    [Fact]
    public void ReimportingIdenticalTemplatesDoesNotMakeExtraCopies()
    {
        using var fixture = new CatalogFixture();
        var account = Account("first", new VideoDedupPreset { Name = "导出方案", TrimEndSeconds = 1 });
        fixture.Catalog.ApplyImportedAccountPresets([account]);
        var id = fixture.Catalog.Load([account]).DefaultFor("first").Id;
        fixture.Catalog.ApplyImportedAccountPresets([account]);
        var reopened = fixture.Catalog.Load([account]);
        Assert.Equal(id, reopened.DefaultFor("first").Id);
        Assert.Equal(2, reopened.Templates.Count);
    }

    [Fact]
    public void ConfigurationPackageRoundTripsTheAppliedTemplateUsingTheLegacyFormat()
    {
        using var fixture = new CatalogFixture();
        var previousRoot = AppPaths.DataRootOverride;
        AppPaths.DataRootOverride = fixture.Path;
        try
        {
            var account = Account("account", new VideoDedupPreset { TrimEndSeconds = 1 });
            AccountStore.SaveAccounts([account]);
            var chosen = fixture.Catalog.Save(null, "配置包方案", new VideoDedupPreset { TrimEndSeconds = 2 });
            fixture.Catalog.SetDefault(account.Id, chosen.Id);
            var zip = Path.Combine(fixture.Path, "settings.zip");
            PublisherConfigTransfer.ExportTo(zip, includeRunData: false);
            using (var archive = ZipFile.OpenRead(zip))
            using (var accountEntry = archive.GetEntry("accounts.json")!.Open())
            using (var templatesEntry = archive.GetEntry("video-dedup-templates.json")!.Open())
            using (var accounts = JsonDocument.Parse(accountEntry))
            using (var templates = JsonDocument.Parse(templatesEntry))
            {
                var exported = accounts.RootElement[0];
                Assert.Equal(2d, exported.GetProperty("videoDedupPreset").GetProperty("trimEndSeconds").GetDouble());
                Assert.Equal(exported.GetProperty("videoDedupTemplateId").GetString(), templates.RootElement[0].GetProperty("id").GetString());
            }

            fixture.Catalog.Save(chosen.Id, "配置包方案", new VideoDedupPreset { TrimEndSeconds = 4 });
            PublisherConfigTransfer.ImportFrom(zip, includeRunData: false);
            Assert.Equal(2d, fixture.Catalog.CaptureRunPresets([account], null)[account.Id]!.TrimEndSeconds);
        }
        finally { AppPaths.DataRootOverride = previousRoot; }
    }

    private static TikTokAccountProfile Account(string id, VideoDedupPreset? preset = null) =>
        new() { Id = id, Name = id, VideoDedupPreset = preset };

    private sealed class CatalogFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dedup-template-catalog-");
        public string Path => _root.FullName;
        public VideoDedupTemplateCatalog Catalog => new(Path);
        public void Dispose() => _root.Delete(recursive: true);
    }
}
