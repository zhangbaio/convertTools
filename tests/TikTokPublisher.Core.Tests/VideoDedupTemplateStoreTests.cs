using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Core.Tests;

[CollectionDefinition("Video dedup template data root", DisableParallelization = true)]
public sealed class VideoDedupTemplateCollection { }

[Collection("Video dedup template data root")]
public sealed class VideoDedupTemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dedup-templates-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot = AppPaths.DataRootOverride;

    public VideoDedupTemplateStoreTests() => AppPaths.DataRootOverride = _root;

    [Fact]
    public void Loading_legacy_accounts_reuses_identical_parameters_and_keeps_bindings_after_reload()
    {
        var accounts = new AccountStore();
        accounts.Load();
        var first = accounts.ActiveAccount!;
        var second = accounts.Add("另一账号");
        var defaults = accounts.Add("保留默认");
        first.VideoDedupPreset = Preset("原方案", 1.05);
        second.VideoDedupPreset = Preset("同参数的旧方案", 1.05);
        accounts.Update(first);
        accounts.Update(second);

        var reloaded = new AccountStore();
        reloaded.Load();
        var id = reloaded.Accounts.Single(account => account.Id == first.Id).VideoDedupTemplateId;
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(id, reloaded.Accounts.Single(account => account.Id == second.Id).VideoDedupTemplateId);
        Assert.Empty(reloaded.Accounts.Single(account => account.Id == defaults.Id).VideoDedupTemplateId);
        Assert.Equal(first.VideoDedupPreset.Fingerprint(), Assert.Single(VideoDedupTemplateStore.Load()).Preset.Fingerprint());
        reloaded.Load();
        Assert.Equal(id, reloaded.Accounts.Single(account => account.Id == first.Id).VideoDedupTemplateId);
        Assert.Single(VideoDedupTemplateStore.Load());
    }

    [Fact]
    public void Edits_are_shared_by_bound_accounts_on_next_run_and_cannot_delete_in_use()
    {
        var shared = VideoDedupTemplateStore.Save(null, Preset("共用模板", 1.05));
        var other = VideoDedupTemplateStore.Save(null, Preset("独立模板", 1.12));
        var first = new TikTokAccountProfile { Id = "a", VideoDedupTemplateId = shared.Id };
        var second = new TikTokAccountProfile { Id = "b", VideoDedupTemplateId = shared.Id };
        var third = new TikTokAccountProfile { Id = "c", VideoDedupTemplateId = other.Id };
        TikTokAccountProfile[] accounts = [first, second, third];
        var before = VideoDedupTemplateStore.CaptureRunPresets(accounts, null);

        VideoDedupTemplateStore.Save(shared.Id, Preset("改名并更新", 1.18));
        var after = VideoDedupTemplateStore.CaptureRunPresets(accounts, null);
        Assert.Equal(1.05, before["a"]!.SpeedFactor);
        Assert.Equal(1.05, before["b"]!.SpeedFactor);
        Assert.Equal(1.18, after["a"]!.SpeedFactor);
        Assert.Equal(1.18, after["b"]!.SpeedFactor);
        Assert.Equal(1.12, after["c"]!.SpeedFactor);
        VideoDedupTemplateStore.Rename(shared.Id, "共享模板新名称");
        var renamed = VideoDedupTemplateStore.Load().Single(template => template.Id == shared.Id);
        Assert.Equal("共享模板新名称", renamed.Name);
        Assert.Equal(after["a"]!.Fingerprint(), renamed.Preset.Fingerprint());
        Assert.Throws<InvalidOperationException>(() => VideoDedupTemplateStore.Delete(shared.Id, accounts));

        first.VideoDedupTemplateId = VideoDedupTemplateStore.SystemId;
        second.VideoDedupTemplateId = other.Id;
        VideoDedupTemplateStore.Delete(shared.Id, accounts);
        Assert.Null(VideoDedupTemplateStore.Resolve(first, VideoDedupTemplateStore.Load()));
        Assert.Equal(other.Id, Assert.Single(VideoDedupTemplateStore.Load()).Id);
    }

    [Fact]
    public void Package_import_rebinds_conflicting_ids_without_overwriting_local_parameters()
    {
        var local = VideoDedupTemplateStore.Save(null, Preset("共用模板", 1.05));
        var account = new TikTokAccountProfile { Id = "a", VideoDedupTemplateId = local.Id };
        var path = Path.Combine(_root, "package", VideoDedupTemplateStore.FileName);
        VideoDedupTemplateStore.Export(path, [account]);
        VideoDedupTemplateStore.Save(local.Id, Preset("共用模板", 1.18));

        VideoDedupTemplateStore.Import(path, [account]);
        var templates = VideoDedupTemplateStore.Load();
        Assert.NotEqual(local.Id, account.VideoDedupTemplateId);
        Assert.Equal(1.18, templates.Single(template => template.Id == local.Id).Preset.SpeedFactor);
        Assert.Equal(1.05, VideoDedupTemplateStore.Resolve(account, templates)!.SpeedFactor);
        Assert.Equal(1.05, account.VideoDedupPreset!.SpeedFactor);
    }

    public void Dispose()
    {
        AppPaths.DataRootOverride = _priorRoot;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static VideoDedupPreset Preset(string name, double speed) => new() { Name = name, SpeedFactor = speed };
}
