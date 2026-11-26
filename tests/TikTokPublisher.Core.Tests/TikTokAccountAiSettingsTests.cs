using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAssertions;
using ShortDrama.Core.Posters;
using ShortDrama.Infrastructure.Files;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Views;

namespace TikTokPublisher.Core.Tests;

[CollectionDefinition("Account AI data root", DisableParallelization = true)]
public sealed class AccountAiDataRootCollection { }

[Collection("Account AI data root")]
public sealed class TikTokAccountAiSettingsTests
{
    [Fact]
    public void Local_english_publish_text_defaults_off_and_persists_for_each_account()
    {
        var previous = AppPaths.DataRootOverride;
        var root = Directory.CreateTempSubdirectory("tiktok-account-publish-language-");
        AppPaths.DataRootOverride = root.FullName;
        try
        {
            var store = new AccountStore();
            store.Load();
            var enabled = store.ActiveAccount!;
            var disabled = store.Add("另一个账号");
            enabled.UsesEnglishPublishText().Should().BeFalse();
            enabled.TiktokLocalDramaEnglishPublishText = true;
            store.Update(enabled);

            var reloaded = new AccountStore();
            reloaded.Load();
            var restored = reloaded.Accounts.Single(account => account.Id == enabled.Id);
            restored.UsesEnglishPublishText().Should().BeTrue();
            reloaded.Accounts.Single(account => account.Id == disabled.Id).UsesEnglishPublishText().Should().BeFalse();

            restored.TiktokDramaMarket = TikTokAccountProfile.DramaMarketOverseas;
            restored.UsesEnglishPublishText().Should().BeFalse();
            JsonSerializer.Deserialize<TikTokAccountProfile>("{}")!.UsesEnglishPublishText().Should().BeFalse();
        }
        finally
        {
            AppPaths.DataRootOverride = previous;
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Account_editor_displays_the_generation_prompt_for_each_account_poster_mode()
    {
        var previous = AppPaths.DataRootOverride;
        var root = Directory.CreateTempSubdirectory("tiktok-image-prompt-");
        AppPaths.DataRootOverride = root.FullName;
        try
        {
            var defaults = new ClientSettings { PosterAgentDirectPrompt = "非账号运行时配置的自定义提示词" };
            var initialOptions = TikTokAccountAiOptions.FromSettings(defaults) with { VerifyTitle = true };
            var account = new TikTokAccountProfile { Id = "a", AiOptions = initialOptions };
            var editor = new AccountAiEditor();
            var fields = ((StackPanel)editor.Content!).Children.OfType<StackPanel>().ToArray();
            var modeOptions = fields.Single(panel => panel.Children.OfType<TextBlock>().Any(label => label.Text == "海报模式"))
                .Children.OfType<WrapPanel>().Single();
            void SelectMode(string mode) => modeOptions.Children.OfType<RadioButton>()
                .Single(button => Equals(button.Tag, mode)).IsChecked = true;
            void AssertSelectedMode(string mode) => modeOptions.Children.OfType<RadioButton>()
                .Should().ContainSingle(button => button.IsChecked == true).Which.Tag.Should().Be(mode);
            var row = fields
                .Single(panel => panel.Children.OfType<TextBlock>().Any(label => label.Text!.StartsWith("图片提示词")));
            var label = row.Children.OfType<TextBlock>().Single();
            var promptBox = row.Children.OfType<TextBox>().Single();
            promptBox.IsReadOnly.Should().BeFalse();

            editor.Load(account, defaults);
            modeOptions.Children.OfType<RadioButton>().Should().HaveCount(2);
            AssertSelectedMode(ClientSettingsDefaults.PosterModeDirect);
            label.Text.Should().Contain("AI 直接生成");
            promptBox.Text.Should().Be(PosterGenerationTemplates.PlainTextDirectPrompt).And.NotContain("{");
            fields.SelectMany(panel => panel.Children.OfType<TextBlock>())
                .Should().NotContain(field => field.Text == "标题校验提示词（仅报告）");
            ((StackPanel)editor.Content!).Children.OfType<TextBlock>()
                .Should().NotContain(block => block.Text != null && block.Text.Contains("临时接口或图片响应失败"));

            const string direct = "用电影光线\n\n突出人物。";
            const string erase = "仅清除文字。";
            promptBox.Text = direct;
            SelectMode(ClientSettingsDefaults.PosterModeEraseFixedTitle);
            AssertSelectedMode(ClientSettingsDefaults.PosterModeEraseFixedTitle);
            label.Text.Should().Contain("去字后固定字体写入");
            promptBox.Text.Should().Be(PosterGenerationTemplates.ErasePrompt);
            account.AiOptions.Should().Be(initialOptions);
            promptBox.Text = erase;
            SelectMode(ClientSettingsDefaults.PosterModeDirect);
            promptBox.Text.Should().Be(direct);
            SelectMode(ClientSettingsDefaults.PosterModeEraseFixedTitle);
            promptBox.Text.Should().Be(erase);

            editor.Save(account);
            account.AiOptions!.PosterMode.Should().Be(ClientSettingsDefaults.PosterModeEraseFixedTitle);
            account = JsonSerializer.Deserialize<TikTokAccountProfile>(JsonSerializer.Serialize(account))!;
            var effective = TikTokAccountAiSettings.Apply(defaults, account);
            effective.PosterAgentDirectPrompt.Should().Be(direct);
            effective.PosterAgentErasePrompt.Should().Be(erase);
            effective.PosterTitleVerifyEnabled.Should().BeTrue();

            var retiredOptions = initialOptions with { PosterMode = ClientSettingsDefaults.PosterModeReferenceRegenerate };
            var retiredAccount = new TikTokAccountProfile { Id = "retired", AiOptions = retiredOptions };
            editor.Load(retiredAccount, defaults);
            modeOptions.Children.OfType<RadioButton>().Should().HaveCount(2);
            AssertSelectedMode(ClientSettingsDefaults.PosterModeDirect);
            label.Text.Should().Contain("AI 直接生成");
            promptBox.Text.Should().Be(PosterGenerationTemplates.PlainTextDirectPrompt);
            retiredAccount.AiOptions.Should().Be(retiredOptions);

            editor.Save(retiredAccount);
            retiredAccount.AiOptions!.PosterMode.Should().Be(ClientSettingsDefaults.PosterModeDirect);

            var legacyAccount = new TikTokAccountProfile
            {
                Id = "legacy",
                AiOptions = initialOptions with { PosterMode = "video_frame" },
            };
            editor.Load(legacyAccount, defaults);
            AssertSelectedMode("video_frame");
            promptBox.Text.Should().Be(PosterRenamer.BuildImageGenerationPrompt(defaults.FrameCoverPrompt, "示例剧名"));
            editor.Save(legacyAccount);
            legacyAccount.AiOptions!.PosterMode.Should().Be("video_frame");

            editor.Load(account, defaults);
            AssertSelectedMode(ClientSettingsDefaults.PosterModeEraseFixedTitle);
            promptBox.Text.Should().Be(erase);
            row.Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                .Single(button => Equals(button.Content, "恢复默认")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.Save(account);
            account.AiOptions!.ErasePosterPrompt.Should().BeNull();
            account.AiOptions.DirectPosterPrompt.Should().Be(direct);
            editor.Clear();
            modeOptions.Children.OfType<RadioButton>().Should().NotContain(button => button.IsChecked == true);
            promptBox.Text.Should().BeEmpty();
        }
        finally
        {
            AppPaths.DataRootOverride = previous;
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Account_editor_displays_the_builtin_prompt_on_reset_and_after_reload()
    {
        var previous = AppPaths.DataRootOverride;
        var root = Directory.CreateTempSubdirectory("tiktok-prompt-reset-");
        AppPaths.DataRootOverride = root.FullName;
        try
        {
            var defaults = new ClientSettings { AiPublishInfoPrompt = "全局自定义提示词" };
            var account = new TikTokAccountProfile
            {
                Id = "a",
                AiOptions = TikTokAccountAiOptions.FromSettings(defaults) with { PublishPrompt = "账号自定义提示词" },
            };
            var editor = new AccountAiEditor();
            var promptRow = ((StackPanel)editor.Content!).Children.OfType<StackPanel>()
                .Single(panel => panel.Children.OfType<TextBlock>().Any(label => label.Text == "文本提示词"));
            var controls = promptRow.Children.OfType<Grid>().Single().Children;
            var promptBox = controls.OfType<TextBox>().Single();
            var reset = controls.OfType<Button>().Single(button => Equals(button.Content, "恢复默认"));

            editor.Load(account, defaults);
            promptBox.Text.Should().Be("账号自定义提示词");

            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            promptBox.Text.Should().Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);
            account.AiOptions!.PublishPrompt.Should().Be("账号自定义提示词");

            editor.Save(account);
            account.AiOptions.PublishPrompt.Should().BeEmpty();
            editor.Load(account, defaults);
            promptBox.Text.Should().Be(TikTokAccountAiSettings.Apply(defaults, account).AiPublishInfoPrompt)
                .And.Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);

            promptBox.Text = "重新编辑的提示词";
            editor.Save(account);
            account.AiOptions.PublishPrompt.Should().Be("重新编辑的提示词");
        }
        finally
        {
            AppPaths.DataRootOverride = previous;
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void An_empty_prompt_uses_the_builtin_default_and_saved_user_text_is_used_verbatim()
    {
        TikTokAccountAiSettings.ResolvePublishPrompt(null)
            .Should().Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);
        TikTokAccountAiSettings.ResolvePublishPrompt(" \r\n ")
            .Should().Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);
        TikTokAccountAiSettings.ResolvePublishPrompt(ClientSettingsDefaults.AiPublishInfoPrompt.ReplaceLineEndings("\n"))
            .Should().Be(ClientSettingsDefaults.AiPublishInfoPrompt.ReplaceLineEndings("\n"));
        TikTokAccountAiSettings.ResolvePublishPrompt(ClientSettingsDefaults.AiFullInfoSystemPrompt)
            .Should().Be(ClientSettingsDefaults.AiFullInfoSystemPrompt);
        const string custom = "使用账号自己的文字约定。";
        TikTokAccountAiSettings.ResolvePublishPrompt(custom).Should().Be(custom);
        TikTokAccountAiOptions.FromSettings(new ClientSettings()).PublishPrompt
            .Should().Be(ClientSettingsDefaults.TikTokUnifiedPublishInfoPrompt);
        TikTokAccountAiOptions.FromSettings(new ClientSettings { AiPublishInfoPrompt = custom }).PublishPrompt
            .Should().Be(custom);
    }

    [Fact]
    public void Accounts_with_a_shared_image_preset_keep_their_own_poster_mode()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tiktok-account-ai-{Guid.NewGuid():N}");
        var previous = AppPaths.DataRootOverride;
        AppPaths.DataRootOverride = root;
        try
        {
            var defaults = new ClientSettings { PosterAgentDirectPrompt = "自定义旧模式提示词" };
            var preset = new TikTokAiImagePreset("shared", "Shared", "https://ark.cn-beijing.volces.com/api/v3", "shared-model", "2K", "9:16");
            var original = TikTokAccountAiOptions.FromSettings(defaults) with { ImagePresetId = preset.Id };
            var account = new TikTokAccountProfile
            {
                Id = "a",
                AiOptions = original with { PosterMode = ClientSettingsDefaults.PosterModeEraseFixedTitle },
            };
            TikTokAccountAiSettings.Save(account.Id, new(), [], [preset]);
            var saved = JsonSerializer.Deserialize<TikTokAccountProfile>(JsonSerializer.Serialize(account));

            var selected = TikTokAccountAiSettings.Apply(defaults, saved);
            var other = TikTokAccountAiSettings.Apply(defaults, new TikTokAccountProfile { Id = "b", AiOptions = original });
            selected.ImageModelId.Should().Be(other.ImageModelId).And.Be(preset.Model);
            other.PosterMode.Should().Be(ClientSettingsDefaults.PosterModeDirect);

            var path = ClientSettingsWorkflowConfigWriter.WriteTempConfig(selected);
            using var payload = JsonDocument.Parse(File.ReadAllText(path));
            payload.RootElement.GetProperty("PosterMode").GetString().Should().Be(ClientSettingsDefaults.PosterModeEraseFixedTitle);
            payload.RootElement.GetProperty("ImageSize").GetString().Should().Be("1584x2816");
        }
        finally
        {
            AppPaths.DataRootOverride = previous;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Effective_settings_use_direct_generation_for_a_retired_reference_poster_mode()
    {
        var previous = AppPaths.DataRootOverride;
        var root = Directory.CreateTempSubdirectory("tiktok-retired-poster-mode-");
        AppPaths.DataRootOverride = root.FullName;
        try
        {
            var defaults = new ClientSettings { PosterMode = ClientSettingsDefaults.PosterModeEraseFixedTitle };
            var account = new TikTokAccountProfile
            {
                Id = "retired",
                AiOptions = TikTokAccountAiOptions.FromSettings(defaults) with
                {
                    PosterMode = ClientSettingsDefaults.PosterModeReferenceRegenerate,
                },
            };
            var saved = JsonSerializer.Deserialize<TikTokAccountProfile>(JsonSerializer.Serialize(account))!;

            TikTokAccountAiSettings.Apply(defaults, saved).PosterMode.Should().Be(ClientSettingsDefaults.PosterModeDirect);
            saved.AiOptions!.PosterMode.Should().Be(ClientSettingsDefaults.PosterModeReferenceRegenerate);

            defaults.PosterMode = ClientSettingsDefaults.PosterModeReferenceRegenerate;
            TikTokAccountAiSettings.Apply(defaults, null).PosterMode.Should().Be(ClientSettingsDefaults.PosterModeDirect);
        }
        finally
        {
            AppPaths.DataRootOverride = previous;
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Account_uses_local_model_and_keys_without_serializing_keys_into_the_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tiktok-account-ai-{Guid.NewGuid():N}");
        var previous = AppPaths.DataRootOverride;
        AppPaths.DataRootOverride = root;
        try
        {
            var defaults = new ClientSettings
            {
                AiTextEndpoint = "https://default.example/v1",
                AiTextApiKey = "default-text-key",
                ImageProvider = "ofox_image2",
                TiktokRoleVectorViewMode = "single",
                PosterTitleVerifyAiRetryCount = 3,
                ImageModelApiKey = "default-image-key",
                PosterAgentDirectPrompt = "legacy custom poster prompt",
                PosterAgentErasePrompt = "legacy custom erase prompt",
            };
            var preset = new TikTokAiTextPreset("preset", "Text", "https://other.example/v1", "account-model", 77);
            var account = new TikTokAccountProfile
            {
                Id = "a",
                AiOptions = TikTokAccountAiOptions.FromSettings(defaults) with
                {
                    TextPresetId = preset.Id,
                    PublishPrompt = "account prompt",
                },
            };
            TikTokAccountAiSettings.Save(account.Id,
                new(new(preset.Endpoint, "local-text-key"), new(defaults.ImageModelEndpoint, "local-image-key")),
                [preset], []);

            var effective = TikTokAccountAiSettings.Apply(defaults, account);
            effective.AiTextModel.Should().Be("account-model");
            effective.AiTextTimeoutSeconds.Should().Be(77);
            effective.AiTextApiKey.Should().Be("local-text-key");
            effective.ImageProvider.Should().Be("doubao");
            effective.TiktokRoleVectorViewMode.Should().Be("multi_angle");
            effective.PosterTitleVerifyAiRetryCount.Should().Be(1);
            effective.ImageModelApiKey.Should().Be("local-image-key");
            effective.AiPublishInfoPrompt.Should().Be("account prompt");
            JsonSerializer.Serialize(account).Should().NotContain("local-text-key").And.NotContain("local-image-key");
            effective.PosterAgentDirectPrompt.Should().Be(PosterGenerationTemplates.PlainTextDirectPrompt);
            effective.PosterAgentErasePrompt.Should().Be(PosterGenerationTemplates.ErasePrompt);

            var legacy = TikTokAccountAiSettings.Apply(defaults, new TikTokAccountProfile());
            legacy.AiTextApiKey.Should().Be(defaults.AiTextApiKey);
            legacy.ImageProvider.Should().Be("doubao");
            legacy.TiktokRoleVectorViewMode.Should().Be("multi_angle");
            legacy.PosterTitleVerifyAiRetryCount.Should().Be(1);
            legacy.PosterAgentDirectPrompt.Should().Be(PosterGenerationTemplates.PlainTextDirectPrompt);
            defaults.PosterAgentDirectPrompt.Should().Be("legacy custom poster prompt");

            TikTokAccountAiSettings.Save("b", new(), [preset with { Endpoint = "https://changed.example/v1" }], []);
            TikTokAccountAiSettings.Apply(defaults, account).AiTextApiKey.Should().BeEmpty();
            account.AiOptions = account.AiOptions with { TextPresetId = "missing" };
            TikTokAccountAiSettings.Apply(defaults, account).AiTextApiKey.Should().BeEmpty();
        }
        finally
        {
            AppPaths.DataRootOverride = previous;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
