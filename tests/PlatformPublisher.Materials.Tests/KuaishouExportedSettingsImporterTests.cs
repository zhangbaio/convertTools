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

    [Fact]
    public async Task Imports_successful_uploads_by_nickname_and_collapses_duplicates()
    {
        var root = Temp();
        var createdProfiles = new List<string>();
        try
        {
            var packagePath = Path.Combine(root, "backup.zip");
            WriteUploadPackage(packagePath);
            var dataRoot = Path.Combine(root, "yunfan");
            var store = new AccountStore(null, Path.Combine(root, "accounts.json"));
            var importer = new KuaishouExportedSettingsImporter(store, dataRoot);
            using var package = PlatformPublisher.Kuaishou.Publishing.KuaishouSettingsExportPackage.Open(packagePath);

            var preview = importer.Preview(package);
            Assert.Equal(2, preview.PersonalUploads);
            Assert.Equal(1, preview.EnterpriseUploads);
            Assert.Equal(1, preview.UnmatchedUploads);
            Assert.Contains("个人版 2 部", importer.Describe(preview));

            var result = await importer.ApplyAsync(package);
            createdProfiles.AddRange(result.Accounts.Select(account => account.ProfileDir));
            Assert.Equal(3, result.ImportedUploads);
            Assert.Equal(0, result.MergedUploads);
            Assert.Equal(0, result.SkippedUploads);
            Assert.Equal(1, result.UnmatchedUploads);

            var personal = PlatformPublisher.Kuaishou.Publishing.KuaishouUploadRecordStore.List("personal", dataRoot);
            var mint = Assert.Single(personal, item => item.NewTitle == "鹿鸣");
            Assert.Equal("星燃漫剧", mint.Nickname);
            Assert.Equal("甲作者", mint.Author);
            Assert.Equal(40, mint.EpisodeCount);
            var other = Assert.Single(personal, item => item.NewTitle == "渡江");
            Assert.Equal("新元昵称", other.Nickname);
            Assert.NotEqual(mint.AccountId, other.AccountId);
            var enterprise = PlatformPublisher.Kuaishou.Publishing.KuaishouUploadRecordStore.List("enterprise", dataRoot);
            var company = Assert.Single(enterprise);
            Assert.Equal("企业剧", company.NewTitle);
            Assert.Equal("星燃企业", company.Nickname);

            using var again = PlatformPublisher.Kuaishou.Publishing.KuaishouSettingsExportPackage.Open(packagePath);
            var second = await importer.ApplyAsync(again);
            Assert.Equal(0, second.ImportedUploads);
            Assert.Equal(0, second.MergedUploads);
            Assert.Equal(3, second.SkippedUploads);
            Assert.Equal(2, PlatformPublisher.Kuaishou.Publishing.KuaishouUploadRecordStore.List("personal", dataRoot).Count);
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

    private static void WriteUploadPackage(string path)
    {
        var settings = new
        {
            schema_version = 1,
            app = "weixin-channel-tool",
            export_mode = "full",
            settings = new
            {
                account_profiles_json = JsonSerializer.Serialize(new object[]
                {
                    new
                    {
                        id = "acct-1",
                        name = "薄荷剧场",
                        kuaishou_personal_username = "ks-user",
                        kuaishou_personal_nickname = "星燃漫剧",
                        kuaishou_enterprise_nickname = "星燃企业",
                    },
                    new
                    {
                        id = "acct-2",
                        name = "新元到家",
                        kuaishou_personal_username = "xinyuan",
                        kuaishou_personal_nickname = "新元昵称",
                    },
                }),
            },
        };
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteText(archive, "settings.json", JsonSerializer.Serialize(settings));
        WriteText(archive, "manifest.json", "{}");
        WriteText(archive, "data/upload-records/kuaishou_personal_upload_records.json", """
            [
              {"记录时间":"2026-11-05 09:00:00","日期":"2026-11-05","上传状态":"成功","账号档案":"acct-1","新剧名":"鹿鸣","原剧名":"鹿鸣","项目名称":"鹿鸣","项目路径":"E:\\drama\\鹿鸣","作者":"","剧集数":"12"},
              {"记录时间":"2026-11-05 10:00:00","日期":"2026-11-05","上传状态":"成功","账号档案":"acct-1","账号名称":"薄荷剧场","快手昵称":"星燃漫剧","新剧名":"鹿鸣","原剧名":"鹿鸣","项目名称":"鹿鸣","项目路径":"E:\\drama\\鹿鸣","作者":"甲作者","剧集数":"40"},
              {"记录时间":"2026-11-05 11:00:00","日期":"2026-11-05","上传状态":"成功","账号档案":"","账号名称":"","快手昵称":"新元昵称","新剧名":"渡江","原剧名":"渡江","项目名称":"渡江","项目路径":"E:\\drama\\渡江","作者":"","剧集数":"8"},
              {"记录时间":"2026-11-05 12:00:00","日期":"2026-11-05","上传状态":"失败","账号档案":"acct-1","新剧名":"失败剧","项目路径":"E:\\drama\\失败剧"},
              {"记录时间":"2026-11-05 13:00:00","日期":"2026-11-05","上传状态":"成功","账号档案":"missing","新剧名":"无主剧","项目路径":"E:\\drama\\无主剧"}
            ]
            """);
        WriteText(archive, "data/upload-records/kuaishou_enterprise_upload_records.json", """
            [
              {"记录时间":"2026-11-05 14:00:00","日期":"2026-11-05","上传状态":"成功","账号档案":"acct-1","快手昵称":"星燃企业","新剧名":"企业剧","原剧名":"企业剧","项目名称":"企业剧","项目路径":"E:\\drama\\企业剧","剧集数":"6"}
            ]
            """);
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
