using System.IO.Compression;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Core.Tests;

[CollectionDefinition("Video dedup account data root", DisableParallelization = true)]
public sealed class VideoDedupAccountCollection { }

[Collection("Video dedup account data root")]
public sealed class VideoDedupAccountMigrationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dedup-accounts-");
    private readonly string? _priorRoot = AppPaths.DataRootOverride;

    public VideoDedupAccountMigrationTests() => AppPaths.DataRootOverride = _root.FullName;

    [Fact]
    public void Legacy_config_packages_do_not_recreate_old_templates_or_bindings()
    {
        var packageRoot = Path.Combine(_root.FullName, "legacy-package");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, "video-dedup-templates.json"),
            """[{"id":"old","preset":{"name":"旧模板","trimStartSeconds":19}}]""");
        File.WriteAllText(Path.Combine(packageRoot, "accounts.json"), JsonSerializer.Serialize(new[]
        {
            new TikTokAccountProfile
            {
                Id = "legacy", Name = "legacy", TiktokDramaMarket = TikTokAccountProfile.DramaMarketOverseas,
                VideoDedupTemplateId = "old", VideoDedupPreset = new VideoDedupPreset { TrimStartSeconds = 19 },
            },
        }, JsonOptions));
        File.WriteAllText(Path.Combine(packageRoot, "manifest.json"),
            """{"version":1,"scope":"config","activeAccountId":"legacy","accounts":[]}""");
        var package = Path.Combine(_root.FullName, "legacy.zip");
        ZipFile.CreateFromDirectory(packageRoot, package);

        PublisherConfigTransfer.ImportFrom(package, includeRunData: false);
        var store = new AccountStore();
        store.Load();
        var account = Assert.Single(store.Accounts);
        var catalog = VideoDedupTemplateCatalog.OpenDefault().Load();
        Assert.Equal(2, catalog.Templates.Count);
        Assert.Empty(catalog.AccountDefaults);
        Assert.Equal(VideoDedupTemplateCatalog.EnglishId, catalog.DefaultFor(VideoDedupTemplateAccount.ForTikTok(account)).Id);
    }

    [Fact]
    public void New_config_packages_preserve_explicit_template_selections_and_dynamic_defaults()
    {
        var first = new TikTokAccountProfile { Id = "first", Name = "first" };
        var second = new TikTokAccountProfile { Id = "second", Name = "second", TiktokDramaMarket = TikTokAccountProfile.DramaMarketOverseas };
        AccountStore.SaveAccounts([first, second]);
        var source = VideoDedupTemplateCatalog.OpenDefault();
        source.SaveAccountConfiguration(VideoDedupTemplateAccount.ForTikTok(first), null, "备份的共享模板",
            new VideoDedupPreset { TrimStartSeconds = 7 });

        var package = Path.Combine(_root.FullName, "current.zip");
        PublisherConfigTransfer.ExportTo(package, includeRunData: false);
        using var zip = ZipFile.OpenRead(package);
        using var accountsFile = zip.GetEntry("accounts.json")!.Open();
        Assert.All(JsonSerializer.Deserialize<TikTokAccountProfile[]>(accountsFile, JsonOptions)!,
            account => Assert.Null(account.VideoDedupPreset));

        AppPaths.DataRootOverride = Path.Combine(_root.FullName, "restored");
        PublisherConfigTransfer.ImportFrom(package, includeRunData: false);
        PublisherConfigTransfer.ImportFrom(package, includeRunData: false);
        var restored = VideoDedupTemplateCatalog.OpenDefault().Load();
        var restoredTemplate = restored.DefaultFor(VideoDedupTemplateAccount.ForTikTok(first));
        Assert.Equal("备份的共享模板", restoredTemplate.Name);
        Assert.Equal(7, restoredTemplate.Preset.TrimStartSeconds);
        Assert.Equal(VideoDedupTemplateCatalog.EnglishId, restored.DefaultFor(VideoDedupTemplateAccount.ForTikTok(second)).Id);
        Assert.Equal(3, restored.Templates.Count);
        Assert.Single(restored.AccountDefaults);
    }

    public void Dispose()
    {
        AppPaths.DataRootOverride = _priorRoot;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }
}
