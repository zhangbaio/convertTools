using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;
using Xunit;

namespace TikTokPublisher.Core.Tests;

[CollectionDefinition("Batch account import data root", DisableParallelization = true)]
public sealed class BatchAccountImportDataRoot;

[Collection("Batch account import data root")]
public sealed class TikTokAccountBatchImporterTests
{
    private const string FixturePassword = "fixture-tt-password.";
    private const string FixtureMailbox = "fixture-mailbox-password";

    [Fact]
    public void ImportsOneHundredAccountsWithIndependentDirectoriesAndProtectedPasswords()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ImportFixture();
        var unmanaged = Directory.CreateDirectory(Path.Combine(fixture.Root, "公司 1")).FullName;
        File.WriteAllText(Path.Combine(unmanaged, "existing.txt"), "fixture");
        var seal = Path.Combine(fixture.Root, "seal.png");
        using (var image = new Image<Rgba32>(1, 1)) image.SaveAsPng(seal);
        var template = fixture.Catalog.Save(null, "批量模板", new VideoDedupPreset { TrimEndSeconds = 2 });
        var options = fixture.Options with { TemplateId = template.Id, Declarant = "声明公司", SealPath = seal };
        var rows = string.Join("\n\n", Enumerable.Range(1, 100).Select(i => Text($"公司 {i}", $"fixture{i}@example.test")));

        var preview = fixture.Importer.Preview(rows, options);
        Assert.All(preview.Rows, row => Assert.Equal(TikTokAccountImportStatus.Ready, row.Status));
        Assert.EndsWith("公司 1 (2)", preview.Rows[0].Workspace);
        Assert.Single(fixture.Store.Accounts);
        Assert.Single(Directory.GetDirectories(fixture.Root));
        Assert.DoesNotContain(FixturePassword, JsonSerializer.Serialize(preview.Rows));

        var result = fixture.Importer.Import(preview);
        Assert.Equal(100, result.Accounts.Count);
        Assert.All(result.Rows, row => Assert.Equal(TikTokAccountImportStatus.Succeeded, row.Status));
        Assert.Equal(100, result.Rows.Select(row => row.Workspace).Distinct().Count());
        Assert.Equal("existing", fixture.Store.ActiveAccountId);
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(unmanaged, "existing.txt")));

        var raw = File.ReadAllText(AppPaths.AccountsFile);
        Assert.DoesNotContain(FixturePassword, raw);
        Assert.DoesNotContain(FixtureMailbox, raw);
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(100, json.RootElement.EnumerateArray().Count(profile => profile.TryGetProperty("tiktokProtectedLoginPassword", out _)));

        var reopened = new AccountStore();
        reopened.Load();
        var catalog = fixture.Catalog.Load(reopened.Accounts);
        Assert.All(reopened.Accounts.Where(account => account.Id != "existing"), account =>
        {
            Assert.Equal(account.TiktokImportedCompanyName, account.TiktokAccountNickname);
            Assert.Equal(account.TiktokAccountNickname, account.TiktokProofCopyrightCompanyName);
            Assert.Equal("声明公司", account.TiktokProofDeclarantCompanyName);
            Assert.Equal(seal, account.TiktokProofSealPath);
            Assert.Equal(FixturePassword, account.TiktokLoginPassword);
            Assert.True(Directory.Exists(account.ResolveWorkspacePath()));
            Assert.Equal(template.Id, catalog.DefaultFor(account.Id).Id);
        });
    }

    [Fact]
    public void PreviewSkipsExistingAndRepeatedCompanyAndEmailAndReportsMalformedRows()
    {
        using var fixture = new ImportFixture();
        var input = string.Join("\n\n",
            Text("现有公司", "new@example.test"),
            Text("不同公司", "ExIsTiNg@example.test"),
            Text("Ａ公司", "first@example.test"),
            Text("A公司", "other@example.test"),
            Text("另一个公司", "FIRST@example.test"),
            "缺邮箱",
            "公司空密码\nempty@example.test\nTT密码：");
        var preview = fixture.Importer.Preview(input, fixture.Options);
        Assert.Equal(new[] { TikTokAccountImportStatus.Skipped, TikTokAccountImportStatus.Skipped,
            TikTokAccountImportStatus.Ready, TikTokAccountImportStatus.Skipped, TikTokAccountImportStatus.Skipped,
            TikTokAccountImportStatus.Failed, TikTokAccountImportStatus.Failed }, preview.Rows.Select(row => row.Status));
        Assert.Contains("同名", preview.Rows[0].Reason);
        Assert.Contains("邮箱", preview.Rows[1].Reason);
        Assert.DoesNotContain("first@example.test", JsonSerializer.Serialize(preview.Rows));
    }

    [Fact]
    public void MarkdownSeparatorsPreservePasswordPunctuationAndCompanyIdentityAfterNicknameChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ImportFixture();
        var plain = Text("固定公司", "first@example.test", "fixture.\\");
        var markdown = string.Join("\\\n", plain.Split('\n')) + "\\";
        var result = fixture.Importer.Import(fixture.Importer.Preview(markdown, fixture.Options));
        var first = Assert.Single(result.Accounts);
        Assert.Equal("fixture.\\", first.TiktokLoginPassword);
        first.Name = first.TiktokAccountNickname = "新昵称";
        fixture.Store.Update(first);
        var second = fixture.Importer.Preview(Text("固定公司", "different@example.test"), fixture.Options);
        Assert.Equal(TikTokAccountImportStatus.Skipped, Assert.Single(second.Rows).Status);
    }

    [Fact]
    public void InvalidSealAndDisappearingWorkspaceAreReportedByRow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ImportFixture();
        var invalid = Path.Combine(fixture.Root, "bad.png");
        File.WriteAllText(invalid, "not an image");
        var input = Text("新公司", "f@example.test");
        var sealPreview = fixture.Importer.Preview(input,
            fixture.Options with { SealPath = invalid, Declarant = "声明人" });
        Assert.Contains("印章", Assert.Single(sealPreview.Rows).Reason);

        var first = Directory.CreateDirectory(Path.Combine(fixture.Root, "manual-a")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(fixture.Root, "manual-b")).FullName;
        var plan = fixture.Importer.Preview(input + "\n\n" + Text("另一家公司", "s@example.test"),
            fixture.Options with { CreateWorkspaces = false },
            new Dictionary<int, string> { [1] = first, [2] = second });
        Directory.Delete(first);
        var result = fixture.Importer.Import(plan);
        Assert.Equal(TikTokAccountImportStatus.Failed, result.Rows[0].Status);
        Assert.Contains("工作目录", result.Rows[0].Reason);
        Assert.Equal(second, Assert.Single(result.Accounts).TiktokUploadProfilePath);
        Assert.Equal(TikTokAccountImportStatus.Succeeded, result.Rows[1].Status);
    }

    [Fact]
    public void AnOptionalSealCanBeSelectedIndependentlyOfTheDeclarant()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ImportFixture();
        var path = Path.Combine(fixture.Root, "optional-seal.png");
        using (var image = new Image<Rgba32>(1, 1)) image.SaveAsPng(path);
        var preview = fixture.Importer.Preview(Text("新公司", "new@example.test"),
            fixture.Options with { SealPath = path, CreateWorkspaces = false });
        var account = Assert.Single(fixture.Importer.Import(preview).Accounts);
        Assert.Equal("", account.TiktokProofDeclarantCompanyName);
        Assert.Equal(path, account.TiktokProofSealPath);
        Assert.Equal("", account.TiktokUploadProfilePath);
    }

    [Fact]
    public void CommitFailureCleansNewDirectoriesAndTemplateBindingsForRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ImportFixture();
        var input = Text("失败后重试", "retry@example.test");
        var plan = fixture.Importer.Preview(input, fixture.Options);
        var target = Assert.Single(plan.Rows).Workspace;
        var backup = AppPaths.AccountsFile + ".fixture";
        File.Move(AppPaths.AccountsFile, backup);
        Directory.CreateDirectory(AppPaths.AccountsFile);
        try
        {
            var failed = fixture.Importer.Import(plan);
            Assert.Equal(TikTokAccountImportStatus.Failed, Assert.Single(failed.Rows).Status);
            Assert.Empty(failed.Accounts);
            Assert.False(Directory.Exists(target));
            Assert.Empty(fixture.Catalog.Load([]).AccountDefaults);
            Assert.Single(fixture.Store.Accounts);
        }
        finally
        {
            Directory.Delete(AppPaths.AccountsFile);
            File.Move(backup, AppPaths.AccountsFile);
        }
        var retry = fixture.Importer.Import(fixture.Importer.Preview(input, fixture.Options));
        Assert.Equal(TikTokAccountImportStatus.Succeeded, Assert.Single(retry.Rows).Status);
        Assert.Equal(target, Assert.Single(retry.Accounts).TiktokUploadProfilePath);
    }

    [Fact]
    public void ExistingPlaintextAccountCanStillBeLoaded()
    {
        using var fixture = new ImportFixture();
        var reopened = new AccountStore();
        reopened.Load();
        Assert.Equal("fixture-existing", Assert.Single(reopened.Accounts).TiktokLoginPassword);
    }

    private static string Text(string company, string email, string password = FixturePassword) =>
        $"{company}\n{email}\n邮箱密码：{FixtureMailbox}\nTT密码：{password}";

    private sealed class ImportFixture : IDisposable
    {
        private readonly string? _prior = AppPaths.DataRootOverride;
        private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("tiktok-batch-test-");
        public string Root { get; }
        public AccountStore Store { get; } = new();
        public VideoDedupTemplateCatalog Catalog { get; }
        public TikTokAccountBatchImporter Importer { get; }
        public TikTokAccountImportOptions Options => new("local", true, "zh", VideoDedupTemplateCatalog.SystemId, "", "", true, Root);

        public ImportFixture()
        {
            AppPaths.DataRootOverride = _temporary.CreateSubdirectory("data").FullName;
            Root = _temporary.CreateSubdirectory("workspaces").FullName;
            Store.ReplaceAll([new TikTokAccountProfile
            {
                Id = "existing", Name = "历史昵称", TiktokAccountNickname = "现有公司",
                TiktokLoginEmail = "existing@example.test", TiktokLoginPassword = "fixture-existing",
            }]);
            Catalog = VideoDedupTemplateCatalog.OpenDefault();
            Importer = new TikTokAccountBatchImporter(Store, Catalog);
        }

        public void Dispose()
        {
            AppPaths.DataRootOverride = _prior;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            _temporary.Delete(recursive: true);
        }
    }
}
