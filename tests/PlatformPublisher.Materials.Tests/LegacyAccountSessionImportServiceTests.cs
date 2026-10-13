using System.Text.Json;
using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Services;
using PlatformPublisher.Desktop.Services;
using Xunit;

namespace PlatformPublisher.Materials.Tests;

public sealed class LegacyAccountSessionImportServiceTests
{
    [Fact]
    public void Discovers_profile_names_and_platform_session_fallbacks()
    {
        var root = Temp();
        try
        {
            var profileRoot = Directory.CreateDirectory(Path.Combine(root, "profiles", "legacy-1")).FullName;
            File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new
            {
                account_profiles_json = JsonSerializer.Serialize(new[] { new { id = "legacy-1", name = "旧账号一" } }),
            }));
            WriteState(Path.Combine(profileRoot, "wx_auth_state.json"), ".weixin.qq.com");
            WriteState(Path.Combine(profileRoot, "kuaishou_kdj_auth_state.json"), ".kuaishou.com");
            var service = new LegacyAccountSessionImportService(
                new AccountStore(null, Path.Combine(root, "accounts.json")), Path.Combine(root, "target"));

            var candidate = Assert.Single(service.Discover(root));

            Assert.Equal("旧账号一", candidate.Name);
            Assert.True(candidate.Weixin.Exists);
            Assert.True(candidate.KuaishouPersonal.Exists);
            Assert.True(candidate.KuaishouEnterprise.Exists);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Imports_weixin_state_to_isolated_snapshot_and_compatibility_paths()
    {
        var root = Temp();
        try
        {
            var profileRoot = Directory.CreateDirectory(Path.Combine(root, "profiles", "legacy-1")).FullName;
            var source = Path.Combine(profileRoot, "wx_auth_state.json");
            WriteState(source, ".weixin.qq.com");
            var targetRoot = Path.Combine(root, "target-data");
            var target = new PublishAccount
            {
                Id = "account-1",
                Name = "当前账号",
                ProfileDir = Path.Combine(root, "current-profile"),
            };
            var service = new LegacyAccountSessionImportService(
                new AccountStore(null, Path.Combine(root, "accounts.json")), targetRoot);
            var candidate = new LegacyAccountSessionCandidate("legacy-1", "旧账号", root,
                new LegacySessionFile(source, true, File.GetLastWriteTimeUtc(source), new FileInfo(source).Length),
                new LegacySessionFile(string.Empty, false, null, 0),
                new LegacySessionFile(string.Empty, false, null, 0));

            var result = await service.ImportAsync(candidate, target,
                new LegacySessionImportSelection(true, false, false));

            Assert.Equal(["视频号"], result.Platforms);
            Assert.True(File.Exists(target.WeixinAuthStatePath));
            Assert.True(File.Exists(Path.Combine(target.ProfileDir, "weixin-series-auth.json")));
            Assert.Equal(File.ReadAllText(source), File.ReadAllText(target.WeixinAuthStatePath));
            Assert.Equal("legacy-1", target.LegacyProfileId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Rejects_storage_state_for_the_wrong_platform()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            cookies = new[] { new { name = "session", value = "secret", domain = ".example.com", path = "/" } },
            origins = Array.Empty<object>(),
        });

        Assert.Throws<InvalidOperationException>(() =>
            LegacyAccountSessionImportService.ValidateStorageState(bytes, "weixin.qq.com"));
    }

    private static void WriteState(string path, string domain)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            cookies = new[] { new { name = "session", value = "redacted", domain, path = "/", httpOnly = true, secure = true, sameSite = "Lax", expires = 2_000_000_000 } },
            origins = Array.Empty<object>(),
        }));
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "legacy-session-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
