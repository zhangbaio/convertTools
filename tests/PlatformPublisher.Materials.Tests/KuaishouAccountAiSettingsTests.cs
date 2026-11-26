using System.Text;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Persistence;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using Xunit;

namespace PlatformPublisher.Materials.Tests;

public sealed class KuaishouAccountAiSettingsTests
{
    [Fact]
    public void ExistingAccountsInheritTheMachinesEffectiveModelsAndPrompts()
    {
        var defaults = MakeDefaults();
        var settings = KuaishouAccountAiSettings.Apply(defaults, new());

        Assert.Equal(defaults.AiTextModel, settings.AiTextModel);
        Assert.Equal(defaults.AiTextApiKey, settings.AiTextApiKey);
        Assert.Equal(defaults.ImageModelApiKey, settings.ImageModelApiKey);
        Assert.Equal(defaults.PosterMode, settings.PosterMode);
        Assert.Equal(defaults.KuaishouPublishInfoPrompt, ClientSettingsStore.ResolveKuaishouPublishInfoPrompt(settings));
        Assert.Equal(defaults.KuaishouPosterHorizontalErasePrompt,
            ClientSettingsStore.ResolveKuaishouPosterPrompt(settings, true, settings.PosterMode));
    }

    [Fact]
    public void AccountPresetsAndKeysAreIsolatedAndOnlyUsedAtTheirConfiguredAddresses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var blobs = new MemoryBlobs();
        var store = new KuaishouAccountAiStore(blobs, new WindowsDataProtector());
        var defaults = MakeDefaults();
        const string customText = "https://test.example/v1";
        const string customImage = "https://images.example/v1";
        var a = new KuaishouAccountAiOptions
        {
            TextPresetId = "a",
            TextPresets = [new("a", "文本 A", customText + "/", "text-a", 46)],
            TextKeys = { [customText] = "private-text-key-a" },
            ImagePresetId = "a",
            ImagePresets = [new("a", "图片 A", customImage, "image-a", "ofox_image2", "1024x1536", "high")],
            ImageKeys = { [customImage] = "private-image-key-a" },
            PublishPrompt = "账号 A 发布提示词",
            PosterMode = ClientSettingsDefaults.PosterModeDirect,
            VerticalDirectPrompt = "竖屏 A",
            HorizontalDirectPrompt = "横屏 A",
            HorizontalErasePrompt = "横屏去字 A",
        };
        var b = a with
        {
            TextPresets = [new("a", "文本 B", customText + "/other", "text-b", 62)],
            ImagePresets = [new("a", "图片 B", customImage + "/other", "image-b", "ofox_image2", "auto", "medium")],
            PosterMode = ClientSettingsDefaults.PosterModeEraseFixedTitle,
            HorizontalErasePrompt = "横屏去字 B",
        };
        store.Save("account-a", a);
        store.Save("account-b", b);

        Assert.DoesNotContain("private-text-key-a", Encoding.UTF8.GetString(blobs.Load("kuaishou.ai.account.account-a", "account-a")!));
        var settingsA = KuaishouAccountAiSettings.Apply(defaults, store.Load("account-a"));
        var settingsB = KuaishouAccountAiSettings.Apply(defaults, store.Load("account-b"));
        Assert.Equal("text-a", settingsA.AiTextModel);
        Assert.Equal(46, settingsA.AiTextTimeoutSeconds);
        Assert.Equal("private-text-key-a", settingsA.AiTextApiKey);
        Assert.Equal("private-image-key-a", settingsA.OfoxImage2ApiKey);
        Assert.Equal("text-b", settingsB.AiTextModel);
        Assert.Empty(settingsB.AiTextApiKey);
        Assert.Empty(settingsB.OfoxImage2ApiKey);
        Assert.Equal("账号 A 发布提示词", ClientSettingsStore.ResolveKuaishouPublishInfoPrompt(settingsA));
        Assert.Equal("竖屏 A", ClientSettingsStore.ResolveKuaishouPosterPrompt(settingsA, false, settingsA.PosterMode));
        Assert.Equal("横屏 A", ClientSettingsStore.ResolveKuaishouPosterPrompt(settingsA, true, settingsA.PosterMode));
        Assert.Equal("横屏去字 B", ClientSettingsStore.ResolveKuaishouPosterPrompt(settingsB, true, settingsB.PosterMode));
        Assert.Empty(store.Load("other-account").TextPresets);
        Assert.Equal("system-text", defaults.AiTextModel);
    }

    private static ClientSettings MakeDefaults() => new()
    {
        AiTextEndpoint = "https://system.example/v1",
        AiTextModel = "system-text",
        AiTextApiKey = "system-text-key",
        ImageProvider = "doubao",
        ImageModelEndpoint = "https://image-system.example/v1",
        ImageModelApiKey = "system-image-key",
        KuaishouPublishInfoPrompt = "本机发布信息提示词",
        PosterMode = ClientSettingsDefaults.PosterModeEraseFixedTitle,
        KuaishouPosterHorizontalErasePrompt = "本机横屏去字提示词",
    };

    private sealed class MemoryBlobs : ISecureBlobStore
    {
        private readonly Dictionary<string, (string Identity, byte[] Value)> _entries = [];
        public bool Contains(string key) => _entries.ContainsKey(key);
        public byte[]? Load(string key, string identity) =>
            _entries.TryGetValue(key, out var item) && item.Identity == identity ? item.Value : null;
        public void Save(string key, string identity, byte[] encryptedBlob, string encryptionKind = "dpapi-current-user") =>
            _entries[key] = (identity, encryptedBlob);
        public void Delete(string key) => _entries.Remove(key);
    }
}
