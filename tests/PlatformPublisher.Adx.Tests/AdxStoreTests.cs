using System.Text.Json;
using PlatformPublisher.Adx.Automation;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Adx.Security;
using PlatformPublisher.Adx.Storage;
using Xunit;

namespace PlatformPublisher.Adx.Tests;

public sealed class AdxStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "platform-adx-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SettingsAreNormalizedAndSecretsAreNotWrittenAsPlainText()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var store = new AdxSettingsStore(settingsPath);
        store.Save(new AdxSettings { BaseUrl = " https://example.test/admin/ ", Username = " user ", DefaultTopCount = 99, QueryLimit = 999, DownloadConcurrency = 0 });
        var loaded = store.Load();
        Assert.Equal(20, loaded.DefaultTopCount);
        Assert.Equal(200, loaded.QueryLimit);
        Assert.Equal(1, loaded.DownloadConcurrency);

        var secretPath = Path.Combine(_root, "password.dat");
        var secret = new AdxCredentialStore(secretPath, new ReversingProtector());
        secret.Save("not-plain-password");
        Assert.Equal("not-plain-password", secret.Load());
        Assert.DoesNotContain("not-plain-password", File.ReadAllText(secretPath));
    }

    [Fact]
    public void BatchV1IsReadAndPerItemSuccessIsPreservedDuringRetry()
    {
        var workflow = Path.Combine(_root, "workflow");
        var batchDir = Path.Combine(workflow, "materials", "adx", "202608191319");
        Directory.CreateDirectory(batchDir);
        var video = Path.Combine(batchDir, "新剧-TOP001-123.mp4");
        File.WriteAllText(video, "video");
        var manifestPath = Path.Combine(batchDir, AdxBatchStore.ManifestFileName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            version = 1, seriesName = "新剧", originalTitle = "原剧",
            createdAt = DateTimeOffset.UtcNow,
            items = new[] { new { materialId = "123", rank = 1, videoPath = video, status = "downloaded" } },
        }));
        var store = new AdxBatchStore();
        var batch = Assert.Single(store.List(workflow));
        Assert.Equal("202608191319", batch.BatchId);
        store.RecordItem(manifestPath, "account-1", "123", "success", "完成");
        store.RecordItem(manifestPath, "account-1", "123", "failed", "旧成功不得覆盖");
        Assert.Equal("success", store.Read(manifestPath)!.PublishByAccount["account-1"].Items["123"].Status);
    }

    [Fact]
    public void MissingManifestIsRecoveredFromSidecar()
    {
        var workflow = Path.Combine(_root, "workflow");
        var batchDir = Path.Combine(workflow, "materials", "adx", "202608191320");
        Directory.CreateDirectory(batchDir);
        var stem = "新剧-TOP002-456";
        var video = Path.Combine(batchDir, stem + ".mp4");
        File.WriteAllText(video, "video");
        File.WriteAllText(Path.Combine(batchDir, stem + ".publish.json"), JsonSerializer.Serialize(new
        { materialId = "456", rank = 2, originalTitle = "原剧", newTitle = "新剧" }));
        var batch = Assert.Single(new AdxBatchStore().List(workflow));
        Assert.Equal("456", Assert.Single(batch.Items).MaterialId);
        Assert.True(File.Exists(Path.Combine(batchDir, AdxBatchStore.ManifestFileName)));
    }

    [Theory]
    [InlineData("曝光 1.4万", "曝光", 14000)]
    [InlineData("播放 123", "播放", 123)]
    public void MetricsAreParsed(string text, string label, long expected) =>
        Assert.Equal(expected, AdxAutomationService.Metric(text, label));

    [Fact]
    public void WindowsDpapiRoundTripsForCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new WindowsAdxDataProtector();
        var input = System.Text.Encoding.UTF8.GetBytes("adx-secret-roundtrip");
        Assert.Equal(input, protector.Unprotect(protector.Protect(input)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ReversingProtector : IAdxDataProtector
    {
        public byte[] Protect(byte[] value) => value.Reverse().ToArray();
        public byte[] Unprotect(byte[] value) => value.Reverse().ToArray();
    }
}
