using FluentAssertions;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokPublishDefaultsTests
{
    [Fact]
    public void Client_settings_ai_defaults_include_non_key_values_only()
    {
        var settings = new ClientSettings();

        settings.AiTextEndpoint.Should().Be(ClientSettingsDefaults.AiTextEndpoint);
        settings.AiTextModel.Should().Be(ClientSettingsDefaults.AiTextModel);
        settings.AiTextTimeoutSeconds.Should().Be(ClientSettingsDefaults.AiTextTimeoutSeconds);
        settings.AiTextMaxBatchSize.Should().Be(ClientSettingsDefaults.AiTextMaxBatchSize);
        settings.TiktokRoleReferenceSelectionMode.Should().Be("local");
        settings.TiktokRoleReferenceAiFallbackEnabled.Should().BeTrue();
        settings.TiktokRoleVectorViewMode.Should().Be("multi_angle");
        settings.AiTextApiKey.Should().BeEmpty();

        settings.ImageProvider.Should().Be(ClientSettingsDefaults.ImageProvider);
        settings.ImageModelId.Should().Be(ClientSettingsDefaults.ImageModelId);
        settings.ImageModelEndpoint.Should().Be(ClientSettingsDefaults.ImageModelEndpoint);
        settings.ImageModelApiKey.Should().BeEmpty();
        settings.OfoxImage2ModelId.Should().Be(ClientSettingsDefaults.OfoxImage2ModelId);
        settings.OfoxImage2Endpoint.Should().Be(ClientSettingsDefaults.OfoxImage2Endpoint);
        settings.OfoxImage2Quality.Should().Be(ClientSettingsDefaults.OfoxImage2Quality);
        settings.OfoxImage2Size.Should().Be(ClientSettingsDefaults.OfoxImage2Size);
        settings.OfoxImage2ApiKey.Should().BeEmpty();
        settings.PosterMode.Should().Be(ClientSettingsDefaults.PosterMode);
        settings.PosterTitleVerifyEnabled.Should().Be(ClientSettingsDefaults.PosterTitleVerifyEnabled);
        settings.PosterTitleVerifyMode.Should().Be(ClientSettingsDefaults.PosterTitleVerifyMode);
        settings.PosterTitleVerifyAiRetryCount.Should().Be(ClientSettingsDefaults.PosterTitleVerifyAiRetryCount);
        settings.FrameCoverPrompt.Should().Be(ClientSettingsDefaults.FrameCoverPrompt);
        settings.PosterInpaintPrompt.Should().Be(ClientSettingsDefaults.PosterInpaintPrompt);
        settings.PosterInpaintSafeRetryPrompt.Should().Be(ClientSettingsDefaults.PosterInpaintSafeRetryPrompt);
        settings.PosterGenerationPrompt.Should().Be(ClientSettingsDefaults.PosterGenerationPrompt);
        settings.PosterGenerationSafeRetryPrompt.Should().Be(ClientSettingsDefaults.PosterGenerationSafeRetryPrompt);
        settings.FrameExtractEpisodeIndex.Should().Be(ClientSettingsDefaults.FrameExtractEpisodeIndex);
        settings.FrameExtractTime.Should().Be(ClientSettingsDefaults.FrameExtractTime);
        settings.FrameExtractNeighborOffsetsSeconds.Should().Be(ClientSettingsDefaults.FrameExtractNeighborOffsetsSeconds);
        settings.FrameExtractFallbackPercents.Should().Be(ClientSettingsDefaults.FrameExtractFallbackPercents);
        settings.TiktokAllowOverLimitUploadImport.Should().Be(ClientSettingsDefaults.TiktokAllowOverLimitUploadImport);
        settings.TiktokOverLimitDownloadEpisodeCount.Should().Be(ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount);
        settings.TiktokProofTemplateDocxPath.Should().Be(ClientSettingsDefaults.TiktokProofTemplateDocxPath);
        ClientSettingsDefaults.TiktokProofTemplateDocxPath.Should().BeEmpty();
        settings.TiktokProofDeclarantCompanyName.Should().BeEmpty();
        settings.TiktokProofSealPath.Should().BeEmpty();
        settings.TiktokProofPdfRenderer.Should().Be("wps");
        settings.TiktokProofWpsPath.Should().BeEmpty();
        ClientSettingsDefaults.TiktokProofPdfRenderer.Should().Be("wps");
        settings.TiktokProofKeepDocx.Should().BeFalse();
    }

    [Fact]
    public void Client_settings_store_fresh_install_uses_single_title_only_poster_defaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-fresh-install-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        try
        {
            File.Exists(databasePath).Should().BeFalse();

            var settings = ClientSettingsStore.Load(databasePath);

            settings.PosterTitleVerifyEnabled.Should().BeTrue();
            settings.PosterTitleVerifyMode.Should().Be("fallback_repaint");
            settings.FrameCoverPrompt.Should().Be(ClientSettingsDefaults.FrameCoverPrompt);
            settings.PosterInpaintPrompt.Should().Be(ClientSettingsDefaults.PosterInpaintPrompt);
            settings.PosterInpaintSafeRetryPrompt.Should().Be(ClientSettingsDefaults.PosterInpaintSafeRetryPrompt);
            settings.PosterGenerationPrompt.Should().Be(ClientSettingsDefaults.PosterGenerationPrompt);
            settings.PosterGenerationSafeRetryPrompt.Should().Be(ClientSettingsDefaults.PosterGenerationSafeRetryPrompt);

            var prompts = new[]
            {
                settings.FrameCoverPrompt,
                settings.PosterInpaintPrompt,
                settings.PosterInpaintSafeRetryPrompt,
                settings.PosterGenerationPrompt,
                settings.PosterGenerationSafeRetryPrompt,
            };
            foreach (var prompt in prompts)
            {
                prompt.Should().Contain("{title}");
                prompt.Should().Contain("人物");
                prompt.Should().Contain("作者");
                prompt.Should().Contain("水印");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Client_settings_store_migrates_shared_asr_values_and_removes_retired_silence_keys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-asr-migration-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);
        try
        {
            AppSettingStore.SaveJson(
                ClientSettingsStore.SettingsKey,
                new Dictionary<string, object?>
                {
                    ["tiktok_silence_local_model_dir"] = "legacy-models",
                    ["tiktok_silence_local_vad_path"] = "legacy-vad.onnx",
                    ["tiktok_silence_asr_app_id"] = "legacy-app",
                    ["tiktok_silence_asr_access_token"] = "legacy-token",
                    ["tiktok_silence_asr_language"] = "zh-CN",
                    ["tiktok_silence_repair_mode"] = "speedup",
                },
                databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);
            loaded.TiktokAsrLocalModelDir.Should().Be("legacy-models");
            loaded.TiktokAsrLocalVadPath.Should().Be("legacy-vad.onnx");
            loaded.TiktokAsrAppId.Should().Be("legacy-app");
            loaded.TiktokAsrAccessToken.Should().Be("legacy-token");
            loaded.TiktokAsrLanguage.Should().Be("zh-CN");

            ClientSettingsStore.Save(loaded, databasePath);
            AppSettingStore.TryLoadJson<Dictionary<string, JsonElement>>(
                    ClientSettingsStore.SettingsKey,
                    out var saved,
                    databasePath)
                .Should().BeTrue();
            saved.Should().NotBeNull();
            saved!.Keys.Should().NotContain(key => key.StartsWith("tiktok_silence_", StringComparison.Ordinal));
            saved.Keys.Should().Contain("tiktok_asr_local_model_dir");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Installer_secret_reset_on_fresh_database_keeps_single_title_only_poster_defaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-fresh-reset-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        try
        {
            ClientSettingsStore.ResetInstallerDataSecrets(databasePath);

            var settings = ClientSettingsStore.Load(databasePath);

            settings.PosterTitleVerifyEnabled.Should().BeTrue();
            settings.PosterTitleVerifyMode.Should().Be("fallback_repaint");
            settings.PosterTitleVerifyAiRetryCount.Should().Be(ClientSettingsDefaults.PosterTitleVerifyAiRetryCount);
            settings.FrameCoverPrompt.Should().Be(ClientSettingsDefaults.FrameCoverPrompt);
            settings.PosterInpaintPrompt.Should().Be(ClientSettingsDefaults.PosterInpaintPrompt);
            settings.PosterInpaintSafeRetryPrompt.Should().Be(ClientSettingsDefaults.PosterInpaintSafeRetryPrompt);
            settings.PosterGenerationPrompt.Should().Be(ClientSettingsDefaults.PosterGenerationPrompt);
            settings.PosterGenerationSafeRetryPrompt.Should().Be(ClientSettingsDefaults.PosterGenerationSafeRetryPrompt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Client_settings_store_normalizes_blank_ai_non_key_values_to_defaults()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                AiTextEndpoint = "",
                AiTextApiKey = "",
                AiTextModel = "",
                AiTextTimeoutSeconds = 0,
                AiTextMaxBatchSize = 0,
                TiktokRoleReferenceSelectionMode = "invalid",
                TiktokRoleVectorViewMode = "invalid",
                PosterMode = "",
                ImageProvider = "",
                ImageModelId = "",
                ImageModelApiKey = "",
                ImageModelEndpoint = "",
                DoubaoImageResolution = "",
                DoubaoImageRatio = "",
                OfoxImage2ModelId = "",
                OfoxImage2ApiKey = "",
                OfoxImage2Endpoint = "",
                OfoxImage2Quality = "",
                OfoxImage2Size = "",
                PosterTitleVerifyMode = "",
                FrameExtractEpisodeIndex = 0,
                FrameExtractTime = double.NaN,
                FrameExtractNeighborOffsetsSeconds = "",
                FrameExtractFallbackPercents = "",
                TiktokOverLimitDownloadEpisodeCount = 0,
                TiktokProofTemplateDocxPath = "",
                TiktokProofDeclarantCompanyName = "  武汉速视科技有限公司  ",
                TiktokProofSealPath = "  C:\\proof\\seal.png  ",
                TiktokProofPdfRenderer = "invalid",
                TiktokProofWpsPath = "  C:\\WPS\\wps.exe  ",
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.AiTextEndpoint.Should().Be(ClientSettingsDefaults.AiTextEndpoint);
            loaded.AiTextApiKey.Should().BeEmpty();
            loaded.AiTextModel.Should().Be(ClientSettingsDefaults.AiTextModel);
            loaded.AiTextTimeoutSeconds.Should().Be(ClientSettingsDefaults.AiTextTimeoutSeconds);
            loaded.AiTextMaxBatchSize.Should().Be(ClientSettingsDefaults.AiTextMaxBatchSize);
            loaded.TiktokRoleReferenceSelectionMode.Should().Be("local");
            loaded.TiktokRoleVectorViewMode.Should().Be("multi_angle");
            loaded.PosterMode.Should().Be(ClientSettingsDefaults.PosterMode);
            loaded.ImageProvider.Should().Be(ClientSettingsDefaults.ImageProvider);
            loaded.ImageModelId.Should().Be(ClientSettingsDefaults.ImageModelId);
            loaded.ImageModelApiKey.Should().BeEmpty();
            loaded.ImageModelEndpoint.Should().Be(ClientSettingsDefaults.ImageModelEndpoint);
            loaded.DoubaoImageResolution.Should().Be(ClientSettingsDefaults.DoubaoImageResolution);
            loaded.DoubaoImageRatio.Should().Be(ClientSettingsDefaults.DoubaoImageRatio);
            loaded.OfoxImage2ModelId.Should().Be(ClientSettingsDefaults.OfoxImage2ModelId);
            loaded.OfoxImage2ApiKey.Should().BeEmpty();
            loaded.OfoxImage2Endpoint.Should().Be(ClientSettingsDefaults.OfoxImage2Endpoint);
            loaded.OfoxImage2Quality.Should().Be(ClientSettingsDefaults.OfoxImage2Quality);
            loaded.OfoxImage2Size.Should().Be(ClientSettingsDefaults.OfoxImage2Size);
            loaded.PosterTitleVerifyMode.Should().Be(ClientSettingsDefaults.PosterTitleVerifyMode);
            loaded.FrameExtractEpisodeIndex.Should().Be(ClientSettingsDefaults.FrameExtractEpisodeIndex);
            loaded.FrameExtractTime.Should().Be(ClientSettingsDefaults.FrameExtractTime);
            loaded.FrameExtractNeighborOffsetsSeconds.Should().Be(ClientSettingsDefaults.FrameExtractNeighborOffsetsSeconds);
            loaded.FrameExtractFallbackPercents.Should().Be(ClientSettingsDefaults.FrameExtractFallbackPercents);
            loaded.TiktokAllowOverLimitUploadImport.Should().Be(ClientSettingsDefaults.TiktokAllowOverLimitUploadImport);
            loaded.TiktokOverLimitDownloadEpisodeCount.Should().Be(ClientSettingsDefaults.TiktokOverLimitDownloadEpisodeCount);
            loaded.TiktokProofTemplateDocxPath.Should().Be(ClientSettingsDefaults.TiktokProofTemplateDocxPath);
            loaded.TiktokProofDeclarantCompanyName.Should().Be("武汉速视科技有限公司");
            loaded.TiktokProofSealPath.Should().Be("C:\\proof\\seal.png");
            loaded.TiktokProofPdfRenderer.Should().Be("wps");
            loaded.TiktokProofWpsPath.Should().Be("C:\\WPS\\wps.exe");
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Client_settings_store_migrates_removed_poster_mode_and_normalizes_frame_extract_values()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-video-frame-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                PosterMode = " VIDEO_FRAME ",
                FrameExtractEpisodeIndex = 42,
                FrameExtractTime = 7.5,
                FrameExtractNeighborOffsetsSeconds = " 1,3 ",
                FrameExtractFallbackPercents = " 20,60 ",
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.PosterMode.Should().Be(ClientSettingsDefaults.PosterMode);
            loaded.FrameExtractEpisodeIndex.Should().Be(42);
            loaded.FrameExtractTime.Should().Be(7.5);
            loaded.FrameExtractNeighborOffsetsSeconds.Should().Be("1,3");
            loaded.FrameExtractFallbackPercents.Should().Be("20,60");
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Client_settings_store_preserves_ai_erase_programmatic_title_mode()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-poster-mode-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                PosterMode = " POSTER_AI_ERASE_PIL_TITLE ",
            }, databasePath);

            ClientSettingsStore.Load(databasePath).PosterMode.Should().Be("poster_ai_erase_pil_title");
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Client_settings_store_migrates_legacy_poster_layout_detect_prompt()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-legacy-layout-prompt-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                PosterLayoutDetectPrompt = ClientSettingsDefaults.LegacyPosterLayoutDetectPrompt,
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.PosterLayoutDetectPrompt.Should().Be(ClientSettingsDefaults.PosterLayoutDetectPrompt);
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Client_settings_store_migrates_legacy_poster_text_cleanup_prompts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-legacy-poster-prompts-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                FrameCoverPrompt = ClientSettingsDefaults.LegacyFrameCoverPrompt,
                PosterInpaintPrompt = ClientSettingsDefaults.LegacyPosterInpaintPrompt,
                PosterInpaintSafeRetryPrompt = ClientSettingsDefaults.LegacyPosterInpaintSafeRetryPrompt,
                PosterGenerationPrompt = ClientSettingsDefaults.LegacyPosterGenerationPrompt,
                PosterGenerationSafeRetryPrompt = ClientSettingsDefaults.LegacyPosterGenerationSafeRetryPrompt,
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.FrameCoverPrompt.Should().Be(ClientSettingsDefaults.FrameCoverPrompt);
            loaded.PosterInpaintPrompt.Should().Be(ClientSettingsDefaults.PosterInpaintPrompt);
            loaded.PosterInpaintSafeRetryPrompt.Should().Be(ClientSettingsDefaults.PosterInpaintSafeRetryPrompt);
            loaded.PosterGenerationPrompt.Should().Be(ClientSettingsDefaults.PosterGenerationPrompt);
            loaded.PosterGenerationSafeRetryPrompt.Should().Be(ClientSettingsDefaults.PosterGenerationSafeRetryPrompt);
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Client_settings_store_preserves_custom_poster_text_cleanup_prompts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-custom-poster-prompts-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                FrameCoverPrompt = "自定义抽帧提示 {title}",
                PosterInpaintPrompt = "自定义局部提示 {title}",
                PosterInpaintSafeRetryPrompt = "自定义局部重试 {title}",
                PosterGenerationPrompt = "自定义整图提示 {title}",
                PosterGenerationSafeRetryPrompt = "自定义整图重试 {title}",
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.FrameCoverPrompt.Should().Be("自定义抽帧提示 {title}");
            loaded.PosterInpaintPrompt.Should().Be("自定义局部提示 {title}");
            loaded.PosterInpaintSafeRetryPrompt.Should().Be("自定义局部重试 {title}");
            loaded.PosterGenerationPrompt.Should().Be("自定义整图提示 {title}");
            loaded.PosterGenerationSafeRetryPrompt.Should().Be("自定义整图重试 {title}");
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Poster_defaults_require_the_target_title_to_be_the_only_visible_text()
    {
        var prompts = new[]
        {
            ClientSettingsDefaults.PosterInpaintPrompt,
            ClientSettingsDefaults.PosterInpaintSafeRetryPrompt,
            ClientSettingsDefaults.PosterGenerationPrompt,
            ClientSettingsDefaults.PosterGenerationSafeRetryPrompt,
            ClientSettingsDefaults.FrameCoverPrompt,
        };

        foreach (var prompt in prompts)
        {
            prompt.Should().Contain("{title}");
            prompt.Should().ContainAny("所有", "全部");
            prompt.Should().Contain("人物");
            prompt.Should().Contain("作者");
            prompt.Should().Contain("水印");
        }
    }

    [Fact]
    public void Client_settings_store_clamps_frame_extract_values_to_supported_ranges()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-video-frame-limits-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                PosterMode = "video_frame",
                PosterTitleVerifyAiRetryCount = 99,
                FrameExtractEpisodeIndex = 10_000,
                FrameExtractTime = 1_000,
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.PosterMode.Should().Be(ClientSettingsDefaults.PosterMode);
            loaded.PosterTitleVerifyAiRetryCount.Should().Be(3);
            loaded.FrameExtractEpisodeIndex.Should().Be(999);
            loaded.FrameExtractTime.Should().Be(600.0);
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Client_settings_clone_preserves_proof_material_configuration()
    {
        var settings = new ClientSettings
        {
            TiktokProofTemplateDocxPath = @"D:\templates\proof.docx",
            TiktokProofDeclarantCompanyName = "声明公司",
            TiktokProofSealPath = @"D:\templates\seal.png",
            TiktokProofPdfRenderer = "libreoffice",
            TiktokProofWpsPath = @"D:\apps\wps.exe",
            TiktokProofKeepDocx = true,
        };

        var clone = settings.Clone();

        clone.TiktokProofTemplateDocxPath.Should().Be(settings.TiktokProofTemplateDocxPath);
        clone.TiktokProofDeclarantCompanyName.Should().Be(settings.TiktokProofDeclarantCompanyName);
        clone.TiktokProofSealPath.Should().Be(settings.TiktokProofSealPath);
        clone.TiktokProofPdfRenderer.Should().Be("libreoffice");
        clone.TiktokProofWpsPath.Should().Be(settings.TiktokProofWpsPath);
        clone.TiktokProofKeepDocx.Should().BeTrue();
    }

    [Fact]
    public void Client_settings_store_preserves_explicit_libreoffice_renderer()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-proof-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                TiktokProofPdfRenderer = " LibreOffice ",
                TiktokProofKeepDocx = true,
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.TiktokProofPdfRenderer.Should().Be("libreoffice");
            loaded.TiktokProofKeepDocx.Should().BeTrue();
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Client_settings_store_load_repairs_legacy_dirty_pikachu_cookie()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-pikachu-legacy-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);
        try
        {
            var expected = BuildPikachuCookie();
            var dirty = $"  {expected}\0\b\u0003\u00fflimit offset query metadata  ";
            AppSettingStore.SaveJson(
                ClientSettingsStore.SettingsKey,
                new Dictionary<string, string>
                {
                    ["pikachu_fanqie_cookie"] = dirty,
                },
                databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.PikachuFanqieCookie.Should().Be(expected);
            loaded.PikachuFanqieCookie.All(value => value is >= ' ' and <= '~')
                .Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Client_settings_store_patch_normalizes_dirty_pikachu_cookie()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-pikachu-patch-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);
        try
        {
            var expected = BuildPikachuCookie();

            ClientSettingsStore.PatchPikachuRuntimeFields(
                fanqieCookie: $"{expected}\0\b\u0003metadata",
                databasePath: databasePath);

            ClientSettingsStore.Load(databasePath).PikachuFanqieCookie.Should().Be(expected);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Client_settings_store_preserves_unrecognized_manual_pikachu_cookie()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-pikachu-manual-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                PikachuFanqieCookie = "  manually-entered-cookie  ",
            }, databasePath);

            ClientSettingsStore.Load(databasePath).PikachuFanqieCookie
                .Should().Be("manually-entered-cookie");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Reset_installer_data_secrets_clears_sensitive_fields_only()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-reset-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);

        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                HgnewAccount = "demo@example.com",
                HgnewPassword = "secret",
                HgnewUdid = "abc-def",
                HgnewClientVersion = "1.3.8",
                AiTextApiKey = "text-key",
                ImageModelApiKey = "image-key",
                OfoxImage2ApiKey = "ofox-key",
                AiTextEndpoint = "https://example.test/v1",
                ImageModelEndpoint = "https://image.example.test/v1",
                OfoxImage2Endpoint = "https://ofox.example.test/v1",
                DramaDownloadConcurrent = 4,
                DownloadFileSegments = 7,
            }, databasePath);

            ClientSettingsStore.ResetInstallerDataSecrets(databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);
            loaded.HgnewAccount.Should().BeEmpty();
            loaded.HgnewPassword.Should().BeEmpty();
            loaded.AiTextApiKey.Should().BeEmpty();
            loaded.ImageModelApiKey.Should().BeEmpty();
            loaded.OfoxImage2ApiKey.Should().BeEmpty();
            loaded.HgnewUdid.Should().Be("ABC-DEF");
            loaded.HgnewClientVersion.Should().Be("1.4.2");
            loaded.AiTextEndpoint.Should().Be("https://example.test/v1");
            loaded.ImageModelEndpoint.Should().Be("https://image.example.test/v1");
            loaded.OfoxImage2Endpoint.Should().Be("https://ofox.example.test/v1");
            loaded.DramaDownloadConcurrent.Should().Be(4);
            loaded.DownloadFileSegments.Should().Be(7);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Account_profile_publish_defaults_match_python_settings()
    {
        var account = new TikTokAccountProfile();

        account.TiktokLoginBrowserMode.Should().Be("embedded");
        account.TiktokSubmitEnabled.Should().BeTrue();
        account.TiktokSubmitAction.Should().Be("submit");
        account.TiktokUploadBrowserMode.Should().Be("embedded");
        account.TiktokPlaywrightUploadHeadless.Should().BeFalse();
        account.TiktokContractId.Should().Be("");
        account.TiktokContractIdMode.Should().Be("manual");
        account.TiktokAnchorPromotionEnabled.Should().BeTrue();
        account.TiktokTargetAudienceMode.Should().Be("ai_recommend");
        account.TiktokGenreCount.Should().Be(3);
        account.TiktokSourceLanguage.Should().Be("zh");
        account.TiktokIsAiDrama.Should().BeTrue();
        account.TiktokContentCreationType.Should().Be("original");
        account.TiktokAiRewriteSynopsis.Should().BeTrue();
        account.TiktokIsOriginalRightsHolder.Should().BeTrue();
        account.TiktokContentOriginalityType.Should().Be("original");
        account.TiktokCopyrightMaterialTypes.Should().Equal("production_agreement");
        account.TiktokCopyrightMaterialFilePath.Should().BeEmpty();
        account.TiktokProofCopyrightCompanyName.Should().BeEmpty();
        account.TiktokProofSubjectCompanyName.Should().BeEmpty();
        account.TiktokProofDeclarantCompanyName.Should().BeEmpty();
        account.TiktokProofSealPath.Should().BeEmpty();
        account.TiktokProofAccountConfigMigrated.Should().BeFalse();
        account.TiktokPublishMode.Should().Be("auto_after_review");
        account.TiktokConsignmentEnabled.Should().BeTrue();
        account.TiktokZeroCostAdsEnabled.Should().BeFalse();
        account.TiktokDayZeroRoi.Should().Be(1.05);
        account.TiktokPaidEnabled.Should().BeFalse();
        account.TiktokPaidRatioEnabled.Should().BeFalse();
        account.TiktokPaidRatioPercent.Should().Be(20.0);
        account.TiktokProfilePreviewEpisodes.Should().Be(3);
        account.TiktokFreePreviewEpisodes.Should().Be(3);
        account.TiktokExpectedFullPriceMode.Should().Be("manual");
        account.TiktokExpectedFullPriceOptionIndex.Should().Be(1);
        account.TiktokUploadStrategy.Should().Be("classic");
        account.TiktokUploadBatchSize.Should().Be(3);
        account.TiktokUploadBatchStallSeconds.Should().Be(75);
        account.TiktokUploadBatchMaxRetries.Should().Be(3);
        account.TiktokDeleteVideosOnArchive.Should().BeTrue();
    }

    [Fact]
    public void Account_profile_ai_rewrite_synopsis_defaults_enabled_without_overriding_explicit_false()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var unconfigured = JsonSerializer.Deserialize<TikTokAccountProfile>("{}", options)!;
        var explicitlyDisabled = JsonSerializer.Deserialize<TikTokAccountProfile>(
            """{"tiktokAiRewriteSynopsis":false}""",
            options)!;

        unconfigured.TiktokAiRewriteSynopsis.Should().BeTrue();
        explicitlyDisabled.TiktokAiRewriteSynopsis.Should().BeFalse();
    }

    [Fact]
    public void Account_profile_zero_cost_ads_defaults_disabled_for_legacy_json()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var legacy = JsonSerializer.Deserialize<TikTokAccountProfile>("{}", options)!;
        var configured = JsonSerializer.Deserialize<TikTokAccountProfile>(
            """{"tiktokZeroCostAdsEnabled":true,"tiktokDayZeroRoi":1.27}""",
            options)!;

        legacy.TiktokZeroCostAdsEnabled.Should().BeFalse();
        legacy.TiktokDayZeroRoi.Should().Be(TikTokPublishOptions.DefaultDayZeroRoi);
        configured.TiktokZeroCostAdsEnabled.Should().BeTrue();
        configured.TiktokDayZeroRoi.Should().Be(1.27);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.234, 1.23)]
    [InlineData(1.235, 1.24)]
    [InlineData(1.5, 1.5)]
    [InlineData(0.99, 1.05)]
    [InlineData(1.51, 1.05)]
    public void Day_zero_roi_normalization_enforces_platform_range(double value, double expected)
    {
        TikTokPublishOptions.NormalizeDayZeroRoi(value).Should().Be(expected);
    }

    [Fact]
    public void Publish_options_copy_zero_cost_ads_account_settings()
    {
        var account = new TikTokAccountProfile
        {
            TiktokZeroCostAdsEnabled = true,
            TiktokDayZeroRoi = 1.28,
        };

        var options = TikTokPublishOptions.FromAccount(account);

        options.ZeroCostAdsEnabled.Should().BeTrue();
        options.DayZeroRoi.Should().Be(1.28);
    }

    [Fact]
    public void Account_store_normalizes_removed_external_upload_browser_to_embedded()
    {
        var method = typeof(AccountStore).GetMethod(
            "NormalizeUploadBrowserMode",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(null, ["external"]).Should().Be("embedded");
        method.Invoke(null, ["playwright"]).Should().Be("playwright");
        method.Invoke(null, ["embedded"]).Should().Be("embedded");
    }

    [Fact]
    public void Account_store_defaults_unconfigured_archive_video_deletion_to_enabled()
    {
        var method = typeof(AccountStore).GetMethod(
            "NormalizeProfileDefaults",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var legacyAccount = new TikTokAccountProfile
        {
            TiktokDeleteVideosOnArchive = false,
            TiktokDeleteVideosOnArchiveConfigured = false,
        };
        method!.Invoke(null, [legacyAccount]);
        legacyAccount.TiktokDeleteVideosOnArchive.Should().BeTrue();

        var explicitlyDisabledAccount = new TikTokAccountProfile
        {
            TiktokDeleteVideosOnArchive = false,
            TiktokDeleteVideosOnArchiveConfigured = true,
        };
        method.Invoke(null, [explicitlyDisabledAccount]);
        explicitlyDisabledAccount.TiktokDeleteVideosOnArchive.Should().BeFalse();
    }

    [Fact]
    public void Publish_options_builder_uses_python_fallbacks_for_missing_values()
    {
        var account = new TikTokAccountProfile
        {
            TiktokTargetAudienceMode = "",
            TiktokGenreCount = 0,
            TiktokSourceLanguage = "",
            TiktokPublishMode = "",
            TiktokProfilePreviewEpisodes = 0,
            TiktokFreePreviewEpisodes = 0,
            TiktokExpectedFullPriceMode = "",
            TiktokExpectedFullPriceOptionIndex = 0,
            TiktokUploadStallSeconds = 0,
            TiktokUploadStrategy = "",
            TiktokUploadBatchSize = 0,
            TiktokUploadBatchStallSeconds = 0,
            TiktokUploadBatchMaxRetries = 0,
        };

        var options = TikTokPublishOptionsBuilder.FromAccount(account);

        options.TargetAudienceMode.Should().Be("ai_recommend");
        options.GenreCount.Should().Be(3);
        options.SourceLanguage.Should().Be("zh");
        options.SourceLanguageLabels.Should().ContainInOrder("中文", "Chinese");
        options.ContentCreationType.Should().Be("original");
        options.IsOriginalRightsHolder.Should().BeTrue();
        options.ContentOriginalityType.Should().Be("original");
        options.CopyrightMaterialTypes.Should().Equal("production_agreement");
        options.UploadSourceInfoRoleVector.Should().BeFalse();
        options.SourceInfoPackageSelection.IncludeRoleVector.Should().BeFalse();
        options.PublishMode.Should().Be("auto_after_review");
        options.ZeroCostAdsEnabled.Should().BeFalse();
        options.DayZeroRoi.Should().Be(1.05);
        options.ProfilePreviewEpisodes.Should().Be(3);
        options.FreePreviewEpisodes.Should().Be(3);
        options.ExpectedFullPriceMode.Should().Be("manual");
        options.ExpectedFullPriceOptionIndex.Should().Be(1);
        options.UploadStallSeconds.Should().Be(180);
        options.UploadStrategy.Should().Be("classic");
        options.UploadBatchSize.Should().Be(3);
        options.UploadBatchStallSeconds.Should().Be(75);
        options.UploadBatchMaxRetries.Should().Be(3);
    }

    [Fact]
    public void Publish_options_builder_only_includes_role_vector_when_account_option_is_enabled()
    {
        var enabledSteps = Array.Empty<string>();
        var disabled = TikTokPublishOptionsBuilder.FromAccount(
            new TikTokAccountProfile(),
            enabledQueueSteps: enabledSteps);
        var enabled = TikTokPublishOptionsBuilder.FromAccount(
            new TikTokAccountProfile { TiktokUploadSourceInfoRoleVector = true },
            enabledQueueSteps: enabledSteps);

        disabled.SourceInfoPackageSelection.IncludeRoleVector.Should().BeFalse();
        enabled.SourceInfoPackageSelection.IncludeRoleVector.Should().BeTrue();
        enabled.UploadSourceInfoRoleVector.Should().BeTrue();
    }

    [Fact]
    public void Client_settings_store_preserves_ai_full_role_reference_mode_and_fallback_choice()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"client-settings-role-reference-{Guid.NewGuid():N}.db");
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                TiktokRoleReferenceSelectionMode = " AI_FULL_REVIEW ",
                TiktokRoleReferenceAiFallbackEnabled = false,
            }, databasePath);

            var loaded = ClientSettingsStore.Load(databasePath);

            loaded.TiktokRoleReferenceSelectionMode.Should().Be("ai_full_review");
            loaded.TiktokRoleReferenceAiFallbackEnabled.Should().BeFalse();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void Publish_options_builder_rejects_chinese_finished_content_remake_before_upload()
    {
        var account = new TikTokAccountProfile
        {
            TiktokSourceLanguage = "zh",
            TiktokIsAiDrama = true,
            TiktokContentCreationType = "remake",
        };

        var action = () => TikTokPublishOptionsBuilder.FromAccount(account);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*仅源语言为非中文的短剧可选择「成片重制」*");
    }

    [Theory]
    [InlineData("en", true, "remake")]
    [InlineData("zh", true, "original")]
    [InlineData("zh", true, "novel_adaptation")]
    [InlineData("zh", false, "remake")]
    public void Publish_options_builder_accepts_valid_content_creation_combinations(
        string sourceLanguage,
        bool isAiDrama,
        string contentCreationType)
    {
        var account = new TikTokAccountProfile
        {
            TiktokSourceLanguage = sourceLanguage,
            TiktokIsAiDrama = isAiDrama,
            TiktokContentCreationType = contentCreationType,
        };

        var options = TikTokPublishOptionsBuilder.FromAccount(account);

        options.ContentCreationType.Should().Be(contentCreationType);
    }

    [Fact]
    public void Publish_options_builder_prefers_generated_proof_material_over_account_file()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-proof-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var accountFile = Path.Combine(workflow, "account-material.pdf");
            File.WriteAllBytes(accountFile, "%PDF-1.7\naccount"u8.ToArray());
            var proofFile = TikTokProofMaterialService.GetPdfPath(workflow);
            File.WriteAllBytes(proofFile, "%PDF-1.7\nproof"u8.ToArray());
            var account = new TikTokAccountProfile
            {
                TiktokCopyrightMaterialFilePath = accountFile,
            };
            var logs = new List<string>();

            var options = TikTokPublishOptionsBuilder.FromAccount(account, workflow, logs.Add);

            options.CopyrightMaterialFilePath.Should().Be(proofFile);
            options.ResolveCopyrightMaterialFilePath("production_agreement").Should().Be(proofFile);
            options.ResolveCopyrightMaterialFilePath("filing_or_distribution_license").Should().BeEmpty();
            logs.Should().ContainSingle(message => message.Contains("项目生成文件", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Publish_options_builder_binds_missing_canonical_proof_and_ignores_account_file()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-proof-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var accountFile = Path.Combine(workflow, "account-material.pdf");
            File.WriteAllBytes(accountFile, "%PDF-1.7\naccount"u8.ToArray());
            File.WriteAllBytes(Path.Combine(workflow, "海报图片.png"), [1, 2, 3]);

            var configured = TikTokPublishOptionsBuilder.FromAccount(
                new TikTokAccountProfile { TiktokCopyrightMaterialFilePath = accountFile },
                workflow);
            var unconfigured = TikTokPublishOptionsBuilder.FromAccount(new TikTokAccountProfile(), workflow);
            var withoutWorkflow = TikTokPublishOptionsBuilder.FromAccount(
                new TikTokAccountProfile { TiktokCopyrightMaterialFilePath = accountFile });

            var canonicalProof = TikTokProofMaterialService.GetPdfPath(workflow);
            configured.CopyrightMaterialFilePath.Should().Be(canonicalProof);
            configured.ResolveCopyrightMaterialFilePath("production_agreement").Should().Be(canonicalProof);
            unconfigured.CopyrightMaterialFilePath.Should().Be(canonicalProof);
            withoutWorkflow.CopyrightMaterialFilePath.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Copyright_material_types_default_invalid_values_but_preserve_explicit_auxiliary_values()
    {
        TikTokPublishConstants.NormalizeCopyrightMaterialTypes(null)
            .Should().Equal("production_agreement");
        TikTokPublishConstants.NormalizeCopyrightMaterialTypes(["", "unknown"])
            .Should().Equal("production_agreement");
        TikTokPublishConstants.NormalizeCopyrightMaterialTypes(
                [" FILing_or_distribution_license ", "unknown"])
            .Should().Equal("filing_or_distribution_license");

        var oneAuxiliary = () => TikTokPublishConstants.ValidateCopyrightMaterialTypes(
            ["filing_or_distribution_license"]);
        oneAuxiliary.Should().Throw<InvalidOperationException>()
            .WithMessage("*至少选择 1 个核心材料，或至少 2 个辅助材料*");

        TikTokPublishConstants.ValidateCopyrightMaterialTypes(
                ["filing_or_distribution_license", "opening_ending_rights_notice"])
            .Should().Equal("filing_or_distribution_license", "opening_ending_rights_notice");
        TikTokPublishConstants.ValidateCopyrightMaterialTypes(["work_registration_certificate"])
            .Should().Equal("work_registration_certificate");
    }

    [Fact]
    public void Auto_managed_copyright_materials_allow_auxiliary_configuration_shrink()
    {
        var desired = TikTokPublishConstants.ValidateAutoManagedCopyrightMaterialTypes(
        [
            TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
        ]);

        desired.Should().Equal(
            TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
            TikTokPublishConstants.AiGenerationScreenshotsMaterialType);
        TikTokPublishConstants.AutoManagedCopyrightMaterialTypes.Should().Contain(
            TikTokPublishConstants.EditingProjectFilesMaterialType,
            "旧草稿中的第三项辅助材料仍需纳入清空范围");
    }

    [Fact]
    public void Auto_managed_copyright_materials_reject_manual_type_before_remote_cleanup()
    {
        var action = () => TikTokPublishConstants.ValidateAutoManagedCopyrightMaterialTypes(
        [
            TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
            "opening_ending_rights_notice",
        ]);

        action.Should().Throw<NotSupportedException>()
            .WithMessage("*片头片尾及权利标识*");
    }

    [Theory]
    [InlineData("production_agreement", true)]
    [InlineData("source_file_information", true)]
    [InlineData("ai_generation_screenshots", true)]
    [InlineData("editing_project_files", true)]
    [InlineData("work_registration_certificate", false)]
    public void Auto_generated_material_detection_matches_supported_generators(
        string materialType,
        bool expected)
    {
        TikTokPublishConstants.RequiresAutoGeneratedCopyrightMaterial([materialType])
            .Should().Be(expected);
    }

    [Fact]
    public void Copyright_material_i18n_keys_cover_every_supported_material_type()
    {
        TikTokPublishConstants.CopyrightMaterialI18nKeys.Keys
            .Should().BeEquivalentTo(TikTokPublishConstants.CopyrightMaterialLabels.Keys);

        TikTokPublishConstants.CopyrightMaterialI18nKeys["production_agreement"]
            .Should().Be(
                "contentPartnerHub_seriesEditPage_copyrightProof_material_productionAgreement");
        TikTokPublishConstants.CopyrightMaterialI18nKeys.Values
            .Should().OnlyHaveUniqueItems()
            .And.OnlyContain(value =>
                value.StartsWith(
                    "contentPartnerHub_seriesEditPage_copyrightProof_material_",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Client_settings_store_preserves_pikachu_short_type()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"client-settings-pikachu-type-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(tempDir, "app.db");
        Directory.CreateDirectory(tempDir);
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                PikachuDramaType = "short",
            }, databasePath);

            ClientSettingsStore.Load(databasePath).PikachuDramaType.Should().Be("short");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Filing_license_material_supports_current_and_legacy_tiktok_labels()
    {
        TikTokPublishConstants.CopyrightMaterialLabels[
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType]
            .Should().Be("网络剧片备案、发行许可、监管审批文件、可信时间戳认证证书");

        TikTokPublishConstants.GetCopyrightMaterialLabelCandidates(
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType)
            .Should().Equal(
                "网络剧片备案、发行许可、监管审批文件、可信时间戳认证证书",
                "网络剧片备案、发行许可、监管审批文件");
    }

    [Fact]
    public void Publish_options_builder_maps_filing_license_to_timestamp_certificate_only()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-proof-aux-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var proofFile = TikTokProofMaterialService.GetPdfPath(workflow);
            File.WriteAllBytes(proofFile, "%PDF-1.7\nproof"u8.ToArray());
            var account = new TikTokAccountProfile
            {
                TiktokCopyrightMaterialTypes =
                [
                    "production_agreement",
                    "filing_or_distribution_license",
                    "opening_ending_rights_notice",
                ],
            };

            var options = TikTokPublishOptionsBuilder.FromAccount(account, workflow);

            options.CopyrightMaterialFilePaths.Keys.Should().Equal(
                "production_agreement",
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType);
            options.ResolveCopyrightMaterialFilePath("production_agreement").Should().Be(proofFile);
            options.ResolveCopyrightMaterialFilePath("filing_or_distribution_license").Should().Be(
                Path.Combine(workflow, TikTokTimestampCertificateService.OutputFileName));
            options.ResolveCopyrightMaterialFilePath("opening_ending_rights_notice").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Fact]
    public void Publish_options_builder_rejects_invalid_existing_generated_proof_without_fallback()
    {
        var workflow = Path.Combine(Path.GetTempPath(), $"tiktok-invalid-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workflow);
        try
        {
            var accountFile = Path.Combine(workflow, "account-material.pdf");
            File.WriteAllBytes(accountFile, "%PDF-1.7\naccount"u8.ToArray());
            File.WriteAllBytes(TikTokProofMaterialService.GetPdfPath(workflow), [1, 2, 3]);
            var account = new TikTokAccountProfile
            {
                TiktokCopyrightMaterialFilePath = accountFile,
            };

            var action = () => TikTokPublishOptionsBuilder.FromAccount(account, workflow);

            action.Should().Throw<InvalidDataException>()
                .WithMessage("*证明材料 PDF*");
        }
        finally
        {
            Directory.Delete(workflow, recursive: true);
        }
    }

    [Theory]
    [InlineData("save")]
    [InlineData("draft")]
    [InlineData("保存")]
    [InlineData("保存草稿")]
    public void Final_action_parser_accepts_python_save_and_legacy_draft(string value)
    {
        FinalActionExtensions.Parse(value).Should().Be(FinalAction.Draft);
    }

    private static string BuildPikachuCookie() =>
        $"install_id=12345; ttreq=1${new string('b', 32)}; odin_tt={new string('a', 160)}";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Microsoft.Data.Sqlite can retain a pooled handle briefly on Windows.
        }
    }
}
