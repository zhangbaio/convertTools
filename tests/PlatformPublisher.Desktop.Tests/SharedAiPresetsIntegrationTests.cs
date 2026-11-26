using System.Reflection;
using PlatformPublisher.Desktop.Views;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

[CollectionDefinition("Shared AI presets data root", DisableParallelization = true)]
public sealed class SharedAiPresetsDataRootCollection { }

[Collection("Shared AI presets data root")]
public sealed class SharedAiPresetsIntegrationTests
{
    [Fact]
    public void Kuaishou_and_TikTok_resolve_the_same_presets_without_replacing_Kuaishou_local_settings()
    {
        var temporary = Directory.CreateTempSubdirectory("shared-publisher-ai-");
        var overrideProperty = typeof(AppPaths).GetProperty("DataRootOverride",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = overrideProperty.GetValue(null);
        overrideProperty.SetValue(null, temporary.FullName);
        try
        {
            var implementation = typeof(KuaishouAccountAiEditor).Assembly
                .GetType("PlatformPublisher.Desktop.Services.KuaishouAccountAiSettings", throwOnError: true)!;
            var apply = implementation.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
            var defaults = new ClientSettings { ImageModelApiKey = "legacy-image-key", AiTextApiKey = "legacy-text-key" };
            var text = SharedAiPresetStore.Save(new("", SharedAiPresetKind.Text, "共用文本",
                "https://shared-text.example.test/v3", "shared-doubao"), "text-key");
            var image = SharedAiPresetStore.Save(new("", SharedAiPresetKind.Image, "共用图片",
                "https://shared-image.example.test/v3", "shared-seedream", Ratio: "3:4"), "image-key");
            var kuaishouAccount = new KuaishouAccountAiOptions
            {
                TextPresetId = text.Id,
                ImagePresetId = image.Id,
                PublishPrompt = "快手账号提示词",
            };
            var tikTokAccount = new TikTokAccountProfile
            {
                Id = "different-account",
                AiOptions = TikTokAccountAiOptions.FromSettings(defaults) with
                {
                    TextPresetId = text.Id, ImagePresetId = image.Id, PublishPrompt = "TikTok 账号提示词",
                },
            };
            var kuaishou = (ClientSettings)apply.Invoke(null, [defaults, kuaishouAccount])!;
            var tikTok = TikTokAccountAiSettings.Apply(defaults, tikTokAccount);
            Assert.Equal(tikTok.AiTextModel, kuaishou.AiTextModel);
            Assert.Equal(tikTok.AiTextApiKey, kuaishou.AiTextApiKey);
            Assert.Equal(tikTok.ImageModelId, kuaishou.ImageModelId);
            Assert.Equal(tikTok.ImageModelApiKey, kuaishou.ImageModelApiKey);
            Assert.Equal("doubao", kuaishou.ImageProvider);
            Assert.Equal("快手账号提示词", kuaishou.KuaishouPublishInfoPrompt);
            Assert.Equal("TikTok 账号提示词", tikTok.AiPublishInfoPrompt);

            SharedAiPresetStore.Save(text, "rotated-text-key");
            var refreshed = (ClientSettings)apply.Invoke(null, [defaults, kuaishouAccount])!;
            Assert.Equal("rotated-text-key", refreshed.AiTextApiKey);
            Assert.Equal("rotated-text-key", TikTokAccountAiSettings.Apply(defaults, tikTokAccount).AiTextApiKey);
            var legacy = (ClientSettings)apply.Invoke(null, [defaults, new KuaishouAccountAiOptions()])!;
            Assert.Equal("legacy-text-key", legacy.AiTextApiKey);
            Assert.Equal("legacy-image-key", legacy.ImageModelApiKey);
        }
        finally
        {
            overrideProperty.SetValue(null, previous);
            temporary.Delete(recursive: true);
        }
    }
}
