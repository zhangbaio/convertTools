using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Core.Tests;

public sealed class VideoDedupTemplateCatalogTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Fresh_library_has_two_built_ins_and_resolves_account_defaults_by_drama_market()
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Catalog.Load();
        Assert.Equal(["通用中文模板", "通用英文模板"], snapshot.Templates.Select(item => item.Name));
        var profile = Profile("account");
        Assert.Equal(VideoDedupTemplateCatalog.ChineseId, snapshot.DefaultFor(VideoDedupTemplateAccount.ForTikTok(profile)).Id);
        profile.TiktokLocalDramaEnglishPublishText = true;
        Assert.Equal(VideoDedupTemplateCatalog.ChineseId, snapshot.DefaultFor(VideoDedupTemplateAccount.ForTikTok(profile)).Id);
        profile.TiktokDramaMarket = TikTokAccountProfile.DramaMarketOverseas;
        Assert.Equal(VideoDedupTemplateCatalog.EnglishId, snapshot.DefaultFor(VideoDedupTemplateAccount.ForTikTok(profile)).Id);
        Assert.Equal(VideoDedupTemplateCatalog.ChineseId, snapshot.DefaultFor(VideoDedupTemplateAccount.ForKuaishou(profile.Id)).Id);
        Assert.Empty(snapshot.AccountDefaults);
        Assert.All(snapshot.Templates, item => Assert.Null(item.Preset.DurationRangeError()));
    }

    [Fact]
    public void Upgrade_preserves_only_the_two_named_templates_and_runs_once()
    {
        using var fixture = new Fixture();
        var chinese = new VideoDedupTemplateEntry("old-zh", "通用中文模板", Preset(2));
        var english = new VideoDedupTemplateEntry("old-en", "通用英文模板", Preset(3));
        fixture.WriteV1([new("older", "旧账号的模板", Preset(4)), english, chinese]);
        File.WriteAllText(Path.Combine(fixture.Root, "video-dedup-templates.json"), "[{\"id\":\"older\"}]");

        var catalog = fixture.Catalog;
        var upgraded = catalog.Load();
        Assert.Equal([2d, 3d], upgraded.Templates.Select(item => item.Preset.TrimStartSeconds));
        Assert.Empty(upgraded.AccountDefaults);
        var custom = catalog.Save(null, "新模板", Preset(5));

        var reopened = new VideoDedupTemplateCatalog(fixture.Root).Load();
        Assert.Equal(3, reopened.Templates.Count);
        Assert.Contains(reopened.Templates, item => item.Id == custom.Id);
        Assert.DoesNotContain(reopened.Templates, item => item.Id == "older");
        using var json = JsonDocument.Parse(File.ReadAllText(fixture.CatalogPath));
        Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Invalid_existing_builtin_cannot_trigger_partial_upgrade()
    {
        using var fixture = new Fixture();
        var invalid = new VideoDedupPreset { MinimumDurationSeconds = 60, MaximumDurationSeconds = 10 };
        fixture.WriteV1([new("old-zh", "通用中文模板", invalid), new("older", "旧配置", Preset(3))]);
        var existing = File.ReadAllText(fixture.CatalogPath);
        Assert.Throws<JsonException>(() => fixture.Catalog.Load());
        Assert.Equal(existing, File.ReadAllText(fixture.CatalogPath));
    }

    [Fact]
    public void Shared_edits_and_custom_bindings_cross_platforms_without_account_id_collisions()
    {
        using var fixture = new Fixture();
        var tikTok = VideoDedupTemplateAccount.ForTikTok(Profile("same", overseas: true));
        var kuaishou = VideoDedupTemplateAccount.ForKuaishou("same");
        var catalog = fixture.Catalog;
        var custom = catalog.SaveAccountConfiguration(kuaishou, null, "快手创建的模板", Preset(5));
        var copy = catalog.Save(null, "自定义副本", custom.Preset);

        var otherClient = new VideoDedupTemplateCatalog(fixture.Root);
        Assert.Equal(VideoDedupTemplateCatalog.EnglishId, otherClient.Load().DefaultFor(tikTok).Id);
        var renamed = otherClient.SaveAccountConfiguration(tikTok, custom.Id, "更新名称", Preset(6));
        Assert.Equal(custom.Id, renamed.Id);
        Assert.Equal(6, catalog.Load().DefaultFor(kuaishou).Preset.TrimStartSeconds);
        Assert.Equal(5, catalog.Load().Templates.Single(item => item.Id == copy.Id).Preset.TrimStartSeconds);
        Assert.Throws<InvalidOperationException>(() => catalog.Delete(custom.Id));

        catalog.Delete(custom.Id, VideoDedupTemplateCatalog.FollowDefaultId);
        var defaults = otherClient.Load();
        Assert.Equal(VideoDedupTemplateCatalog.EnglishId, defaults.DefaultFor(tikTok).Id);
        Assert.Equal(VideoDedupTemplateCatalog.ChineseId, defaults.DefaultFor(kuaishou).Id);
        Assert.Empty(defaults.AccountDefaults);
    }

    [Fact]
    public void Built_ins_are_editable_but_keep_their_names_and_cannot_be_deleted()
    {
        using var fixture = new Fixture();
        var account = VideoDedupTemplateAccount.ForKuaishou("ks");
        var catalog = fixture.Catalog;
        var edited = catalog.SaveAccountConfiguration(account, VideoDedupTemplateCatalog.FollowDefaultId, "修改名称", Preset(4));
        Assert.Equal("通用中文模板", edited.Name);
        Assert.Empty(catalog.Load().AccountDefaults);
        Assert.Equal(4, catalog.Load().DefaultFor(VideoDedupTemplateAccount.ForTikTok(Profile("tk"))).Preset.TrimStartSeconds);
        Assert.Throws<InvalidOperationException>(() => catalog.Delete(VideoDedupTemplateCatalog.ChineseId));
        Assert.Throws<InvalidOperationException>(() => catalog.Delete(VideoDedupTemplateCatalog.EnglishId));
    }

    [Fact]
    public void Failed_edit_preserves_template_parameters_and_binding()
    {
        using var fixture = new Fixture();
        var account = VideoDedupTemplateAccount.ForKuaishou("ks");
        var catalog = fixture.Catalog;
        var original = catalog.SaveAccountConfiguration(account, null, "原配置", Preset(4));
        var invalid = new VideoDedupPreset { MinimumDurationSeconds = 2 };
        Assert.Throws<InvalidOperationException>(() => catalog.SaveAccountConfiguration(account, original.Id, "修改", invalid));
        var current = catalog.Load().DefaultFor(account);
        Assert.Equal(original.Id, current.Id);
        Assert.Equal(4, current.Preset.TrimStartSeconds);
        Assert.Equal("原配置", current.Name);
    }

    [Fact]
    public void Running_queue_captures_a_snapshot_and_ignores_legacy_account_parameters()
    {
        using var fixture = new Fixture();
        var local = Profile("local");
        var overseas = Profile("overseas", overseas: true);
        local.VideoDedupPreset = Preset(30);
        var catalog = fixture.Catalog;
        var preset = catalog.Save(null, "排队使用", Preset(3));
        catalog.SaveAccountConfiguration(VideoDedupTemplateAccount.ForTikTok(overseas), preset.Id);
        var captured = catalog.CaptureRunPresets([local, overseas], null);

        catalog.Save(preset.Id, preset.Name, Preset(4));
        Assert.Equal("通用中文模板", captured["local"]!.Name);
        Assert.Equal(3, captured["overseas"]!.TrimStartSeconds);
        Assert.Equal(4, catalog.CaptureRunPresets([overseas], null)["overseas"]!.TrimStartSeconds);
    }

    [Fact]
    public void Concurrent_clients_commit_both_updates()
    {
        using var fixture = new Fixture();
        var first = new VideoDedupTemplateCatalog(fixture.Root);
        var second = new VideoDedupTemplateCatalog(fixture.Root);
        Parallel.Invoke(
            () => first.Save(null, "第一份自定义", Preset(1)),
            () => second.Save(null, "第二份自定义", Preset(2)));
        Assert.Equal(4, fixture.Catalog.Load().Templates.Count);
    }

    private static VideoDedupPreset Preset(double trim) => new() { TrimStartSeconds = trim };

    private static TikTokAccountProfile Profile(string id, bool overseas = false) =>
        new() { Id = id, TiktokDramaMarket = overseas ? TikTokAccountProfile.DramaMarketOverseas : TikTokAccountProfile.DramaMarketLocal };

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("video-dedup-catalog-");
        public string Root => _directory.FullName;
        public string CatalogPath => Path.Combine(Root, "video-dedup-template-library.json");
        public VideoDedupTemplateCatalog Catalog => new(Root);

        public void WriteV1(VideoDedupTemplateEntry[] templates) =>
            File.WriteAllText(CatalogPath, JsonSerializer.Serialize(new
            {
                Version = 1,
                Templates = templates,
                AccountDefaults = new Dictionary<string, string> { ["old-account"] = "older" },
            }, JsonOptions));

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
