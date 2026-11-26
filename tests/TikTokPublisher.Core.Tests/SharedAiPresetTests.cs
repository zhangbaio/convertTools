using Avalonia.Controls;
using Avalonia.LogicalTree;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Core.Tests;

[Collection("Account AI data root")]
public sealed class SharedAiPresetTests : IDisposable
{
    private readonly string? _previousRoot = AppPaths.DataRootOverride;
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("shared-ai-presets-");

    public SharedAiPresetTests() => AppPaths.DataRootOverride = _temporary.FullName;

    [Fact]
    public void Stores_only_encrypted_keys_and_requires_a_new_key_for_a_changed_endpoint()
    {
        const string key = "test-key-system-presets-8c73e";
        var preset = SharedAiPresetStore.Save(
            new("", SharedAiPresetKind.Text, "共享文本", "https://text.example.test/api/v3/", "doubao-text", 75), key);
        Assert.True(SharedAiPresetStore.IsShared(preset.Id));
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(_temporary.FullName, "shared-ai-presets.json")));
        var sameEndpoint = SharedAiPresetStore.Save(preset with { Model = "doubao-text-next" }, "");
        var runtime = new ClientSettings();
        Assert.True(SharedAiPresetStore.ApplyText(runtime, preset.Id));
        Assert.Equal(key, runtime.AiTextApiKey);
        Assert.Equal(sameEndpoint.Model, runtime.AiTextModel);

        Assert.Throws<ArgumentException>(() => SharedAiPresetStore.Save(
            sameEndpoint with { Endpoint = "https://other.example.test/v1" }, ""));
        Assert.Equal(sameEndpoint, Assert.Single(SharedAiPresetStore.List()));
        Assert.Throws<InvalidOperationException>(() => SharedAiPresetStore.ApplyImage(runtime, preset.Id));
    }

    [Fact]
    public void TikTok_editor_saves_shared_ids_and_preserves_the_legacy_account_key_and_prompt()
    {
        var text = SharedAiPresetStore.Save(
            new("", SharedAiPresetKind.Text, "共享文本", "https://shared.example.test/v3", "doubao-text", 70), "shared-text");
        var image = SharedAiPresetStore.Save(
            new("", SharedAiPresetKind.Image, "共享图片", "https://image.example.test/v3", "seedream-model", Ratio: "9:16"),
            "shared-image");
        var defaults = new ClientSettings { ImageModelId = "legacy-image" };
        var local = new TikTokAiTextPreset("local-id", "旧文本", "https://old.example.test/v3", "legacy-text", 90);
        var account = new TikTokAccountProfile
        {
            Id = "legacy-account",
            AiOptions = TikTokAccountAiOptions.FromSettings(defaults) with
            {
                TextPresetId = local.Id,
                PublishPrompt = "账号自己的提示词",
            },
        };
        var oldKeys = new TikTokAccountAiSettings.Keys(new(local.Endpoint, "legacy-key"));
        TikTokAccountAiSettings.Save(account.Id, oldKeys, [local], []);
        Assert.Equal("legacy-key", TikTokAccountAiSettings.Apply(defaults, account).AiTextApiKey);

        var editor = new AccountAiEditor();
        editor.Load(account, defaults);
        Select(text.Id);
        Select(image.Id);
        editor.Save(account);

        var runtime = TikTokAccountAiSettings.Apply(defaults, account);
        Assert.Equal(text.Id, account.AiOptions!.TextPresetId);
        Assert.Equal(image.Id, account.AiOptions.ImagePresetId);
        Assert.Equal("shared-text", runtime.AiTextApiKey);
        Assert.Equal("shared-image", runtime.ImageModelApiKey);
        Assert.Equal("9:16", runtime.DoubaoImageRatio);
        Assert.Equal(image.Model, runtime.ImageModelId);
        Assert.Equal("账号自己的提示词", runtime.AiPublishInfoPrompt);
        Assert.Equal(oldKeys, TikTokAccountAiSettings.Load().AccountKeys[account.Id]);

        Select(local.Id);
        editor.Save(account);
        Assert.Equal("legacy-key", TikTokAccountAiSettings.Apply(defaults, account).AiTextApiKey);

        void Select(string id)
        {
            var box = Assert.Single(editor.GetLogicalDescendants().OfType<ComboBox>(),
                combo => combo.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, id)));
            box.SelectedItem = Assert.Single(box.Items.OfType<ComboBoxItem>(), item => Equals(item.Tag, id));
        }
    }

    public void Dispose()
    {
        AppPaths.DataRootOverride = _previousRoot;
        _temporary.Delete(recursive: true);
    }
}
