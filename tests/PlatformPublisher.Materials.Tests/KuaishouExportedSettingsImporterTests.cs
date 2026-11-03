using System.IO.Compression;
using System.Text.Json;
using ChannelsPublisher.Core.Services;
using Xunit;
using PlatformPublisher.Desktop.Services;

namespace PlatformPublisher.Materials.Tests;

public sealed class KuaishouExportedSettingsImporterTests
{
    [Fact]
    public async Task Imports_personal_config_login_state_and_missing_directory()
    {
        var root = Temp();
        var createdProfiles = new List<string>();
        try
        {
            var packagePath = Path.Combine(root, "backup.zip");
            var workspace = Path.Combine(root, "missing-workspace");
            var pdf = Path.Combine(root, "not-here", "promise.pdf");
            WritePackage(packagePath, workspace, pdf);
            var store = new AccountStore(null, Path.Combine(root, "accounts.json"));
            var importer = new KuaishouExportedSettingsImporter(store, Path.Combine(root, "yunfan"));
            using var package = PlatformPublisher.Kuaishou.Publishing.KuaishouSettingsExportPackage.Open(packagePath);

            var preview = importer.Preview(package);
            var row = Assert.Single(preview.Accounts);
            Assert.True(row.IsNew);
            Assert.True(row.HasPersonalLogin);
            Assert.Contains(workspace, row.MissingPaths);
            Assert.Equal(1, preview.SkippedProfiles);

            var result = await importer.ApplyAsync(package);
            createdProfiles.AddRange(result.Accounts.Select(account => account.ProfileDir));

            Assert.Equal(1, result.Created);
            Assert.Equal(0, result.Updated);
            Assert.Equal(1, result.AuthFiles);
            Assert.Equal(1, result.Assets);
            var account = Assert.Single(result.Accounts);
            Assert.Equal("personal", account.KuaishouAccountEdition);
            Assert.Equal(workspace, account.KuaishouPersonalWorkRootDirectory);
            Assert.True(File.Exists(account.KuaishouPersonalAuthStatePath));
            var saved = File.ReadAllText(account.KuaishouPersonalConfigPath);
            Assert.Contains("app-from-export", saved);
            Assert.Contains("imported-kuaishou-assets", saved);

            using var again = PlatformPublisher.Kuaishou.Publishing.KuaishouSettingsExportPackage.Open(packagePath);
            var second = await importer.ApplyAsync(again);
            Assert.Equal(0, second.Created);
            Assert.Equal(1, second.Updated);
            Assert.Equal("personal", account.KuaishouAccountEdition);
        }
        finally
        {
            foreach (var profile in createdProfiles)
            {
                if (Directory.Exists(profile)) Directory.Delete(profile, true);
            }
            Directory.Delete(root, true);
        }
    }

    private static void WritePackage(string path, string workspace, string pdfPath)
    {
        var settings = new
        {
            schema_version = 1,
            app = "weixin-channel-tool",
            export_mode = "full",
            settings = new
            {
                kuaishou_app_id = "app-from-export",
                account_profiles_json = JsonSerializer.Serialize(new object[]
                {
                    new
                    {
                        id = "profile-ks",
                        name = "快手甲",
                        kuaishou_personal_username = "ks-user",
                        kuaishou_personal_last_workspace = workspace,
                        kuaishou_personal_commitment_pdf_path = pdfPath,
                    },
                    new { id = "profile-wx", name = "只有视频号" },
                }),
            },
        };
        var manifest = new
        {
            assets = new[]
            {
                new
                {
                    archive_path = "assets/promise/promise.pdf",
                    refs = new[] { new { path = pdfPath } },
                },
            },
        };
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteText(archive, "settings.json", JsonSerializer.Serialize(settings));
        WriteText(archive, "manifest.json", JsonSerializer.Serialize(manifest));
        WriteText(archive, "data/auth/profiles/profile-ks/kuaishou_personal_kdj_auth_state.json", JsonSerializer.Serialize(new
        {
            cookies = new[] { new { name = "session", value = "abc", domain = ".kuaishou.com", path = "/", httpOnly = true, secure = true, sameSite = "Lax", expires = 2_000_000_000 } },
            origins = Array.Empty<object>(),
        }));
        var asset = archive.CreateEntry("assets/promise/promise.pdf");
        using var assetStream = asset.Open();
        assetStream.Write([1, 2, 3]);
    }

    private static void WriteText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "kuaishou-export-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
