using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Core.Tests;

public sealed class VideoDedupTests
{
    [Fact]
    public void Step_order_puts_dedup_after_download_and_retires_source_deletion()
    {
        QueueStepRegistry.All.Select(step => step.Key).Should().ContainInOrder(
            QueueStepKeys.Download,
            QueueStepKeys.VideoDedup,
            QueueStepKeys.RewriteInfo);
        QueueStepRegistry.All.Select(step => step.Key).Should().NotContain(QueueStepKeys.DeleteSourceVideos);
        QueueStepRegistry.UserSelectable.Select(step => step.Key).Should().Contain(QueueStepKeys.VideoDedup);
        QueueStepRegistry.LabelOf(QueueStepKeys.VideoDedup).Should().Be("视频去重");

        var options = QueueRunOptions.FromDictionary(new Dictionary<string, object?>
        {
            ["enabled_steps"] = new List<object?> { "download", "delete_source_videos", "video_dedup" },
        });
        options.EnabledSteps.Should().Equal(QueueStepKeys.Download, QueueStepKeys.VideoDedup);
    }

    [Fact]
    public void Importer_reads_the_processor_default_preset()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "name": "默认方案",
              "encodingPreference": "HardwareFirst",
              "parallel": 2,
              "minimumDurationSeconds": null,
              "maximumDurationSeconds": null,
              "steps": [
                { "type": "fps", "fps": 30 },
                { "type": "videoEncode", "codec": "h264", "bitrateMode": "Variable", "bitrateKbps": 4200 },
                { "type": "audioEncode", "codec": "aac" }
              ]
            }
            """;

        var preset = VideoDedupPresetImporter.FromProcessorJson(json);

        preset.Name.Should().Be("默认方案");
        preset.EncodingPreference.Should().Be(VideoDedupPreset.EncodingHardwareFirst);
        preset.Parallel.Should().Be(2);
        preset.Fps.Should().Be(30);
        preset.VideoCodec.Should().Be("h264");
        preset.BitrateMode.Should().Be(VideoDedupPreset.BitrateVbr);
        preset.VideoBitrateMbps.Should().Be(4.2);
        preset.AudioCodec.Should().Be("aac");
        preset.HasDurationLimit.Should().BeFalse();
    }

    [Fact]
    public void Duration_limit_merges_a_short_tail_and_keeps_whole_episodes()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.MinimumDurationSeconds = 60;
        preset.MaximumDurationSeconds = 100;
        preset.Normalize();

        var groups = VideoDedupPlanner.Group([80, 80, 20], preset);

        groups.Should().HaveCount(2);
        groups[0].Should().Equal(0);
        groups[1].Should().Equal(1, 2);
    }

    [Fact]
    public void Default_encode_arguments_use_h264_vbr_and_aac()
    {
        var preset = VideoDedupPreset.CreateDefault().NormalizedCopy();
        var clip = new VideoDedupClip(
            "episode-1.mp4",
            new VideoDedupProbe(12, 1080, 1920, true));

        var args = VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 3);
        var joined = string.Join(" ", args);

        joined.Should().Contain("-c:v libx264");
        joined.Should().Contain("-b:v 4200k");
        joined.Should().Contain("-r 30");
        joined.Should().Contain("-c:a aac");
        joined.Should().Contain("scale=1080:1920");
        args[^1].Should().Be("out.mp4");
    }

    [Fact]
    public void Landscape_source_swaps_the_single_resolution()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.PortraitWidth = 720;
        preset.PortraitHeight = 1280;
        preset.LandscapeWidth = 640;
        preset.LandscapeHeight = 360;
        preset.CropTopPercent = 2;
        preset.CropBottomPercent = 6;
        preset.Normalize();

        preset.CropTopPercent.Should().Be(6);
        preset.CropBottomPercent.Should().Be(6);
        preset.LandscapeWidth.Should().Be(1280);
        preset.LandscapeHeight.Should().Be(720);

        var clip = new VideoDedupClip("wide.mp4", new VideoDedupProbe(12, 1920, 1080, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));
        joined.Should().Contain("scale=1280:720");
    }

    [Fact]
    public void Original_resolution_skips_scaling()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.PortraitWidth = 0;
        preset.PortraitHeight = 0;
        preset.CropTopPercent = 4;
        preset.Normalize();

        preset.KeepsOriginalResolution.Should().BeTrue();
        preset.LandscapeWidth.Should().Be(0);
        preset.LandscapeHeight.Should().Be(0);

        var clip = new VideoDedupClip("wide.mp4", new VideoDedupProbe(12, 1920, 1080, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));
        joined.Should().NotContain("scale=");
        joined.Should().Contain("crop=w=iw*");
        joined.Should().Contain("fps=30");
    }

    [Fact]
    public void Hardware_arguments_follow_the_vendor_encoder()
    {
        var preset = VideoDedupPreset.CreateDefault().NormalizedCopy();
        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, true));

        var nvenc = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "h264_nvenc", seed: 1));
        nvenc.Should().Contain("-c:v h264_nvenc");
        nvenc.Should().Contain("-preset p4");
        nvenc.Should().Contain("-gpu any");
        nvenc.Should().Contain("-rc vbr");

        var amf = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "h264_amf", seed: 1));
        amf.Should().Contain("-c:v h264_amf");
        amf.Should().Contain("-usage transcoding");
        amf.Should().Contain("-quality balanced");
        amf.Should().Contain("-rc vbr_peak");
        amf.Should().NotContain("-preset");

        var qsv = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "h264_qsv", seed: 1));
        qsv.Should().Contain("-c:v h264_qsv");
        qsv.Should().Contain("-preset veryfast");
        qsv.Should().Contain("-b:v 4200k");
        qsv.Should().NotContain("-gpu");
    }

    [Fact]
    public async Task Auto_encoder_uses_the_first_card_that_can_encode()
    {
        VideoDedupService.EncoderWorksForTests = (encoder, _) =>
            Task.FromResult(encoder is "h264_amf");
        try
        {
            var choice = await VideoDedupService.ResolveEncoderForTests(
                VideoDedupPreset.CreateDefault(),
                CancellationToken.None);

            choice.Encoder.Should().Be("h264_amf");
            choice.Device.Should().Be("AMD");
        }
        finally
        {
            VideoDedupService.EncoderWorksForTests = null;
        }
    }

    [Fact]
    public async Task Auto_encoder_uses_cpu_when_no_graphics_card_can_encode()
    {
        VideoDedupService.EncoderWorksForTests = (_, _) => Task.FromResult(false);
        try
        {
            var choice = await VideoDedupService.ResolveEncoderForTests(
                VideoDedupPreset.CreateDefault(),
                CancellationToken.None);

            choice.Encoder.Should().Be("libx264");
            choice.Device.Should().Be("CPU");
            choice.Note.Should().Contain("NVIDIA").And.Contain("AMD");
        }
        finally
        {
            VideoDedupService.EncoderWorksForTests = null;
        }
    }

    [Fact]
    public void Saved_hardware_only_preference_becomes_automatic()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.EncodingPreference = "HardwareOnly";
        preset.Normalize();

        preset.EncodingPreference.Should().Be(VideoDedupPreset.EncodingHardwareFirst);
    }

    [Fact]
    public async Task Explicit_nvidia_fails_when_that_card_cannot_encode()
    {
        VideoDedupService.EncoderWorksForTests = (encoder, _) =>
            Task.FromResult(encoder is "h264_amf");
        try
        {
            var preset = VideoDedupPreset.CreateDefault();
            preset.EncodingPreference = VideoDedupPreset.EncodingNvenc;
            var act = () => VideoDedupService.ResolveEncoderForTests(preset, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*NVIDIA*");
        }
        finally
        {
            VideoDedupService.EncoderWorksForTests = null;
        }
    }

    [Fact]
    public async Task Run_replaces_visible_videos_and_keeps_originals()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "01.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        VideoDedupService.ProbeForTests = (_, _) =>
            Task.FromResult(new VideoDedupProbe(8, 720, 1280, true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (args, _) =>
        {
            File.WriteAllBytes(args[^1], [9, 9, 9, 9, 9]);
            return Task.CompletedTask;
        };
        try
        {
            var item = new QueueProjectItem { ProjectDir = root, EpisodeCount = 1 };
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1" };
            var logs = new List<string>();

            await VideoDedupService.RunAsync(item, account, force: false, logs.Add, CancellationToken.None);

            item.EpisodeCount.Should().Be(1);
            (await File.ReadAllBytesAsync(source)).Should().Equal([9, 9, 9, 9, 9]);
            var archived = Path.Combine(root, "source-originals", "01.mp4");
            (await File.ReadAllBytesAsync(archived)).Should().Equal([1, 2, 3, 4]);
            logs.Should().Contain(line => line.Contains("使用内置默认方案", StringComparison.Ordinal));
            logs.Should().Contain(line => line.Contains("参数：编码器自动选择", StringComparison.Ordinal) && line.Contains("H.264", StringComparison.Ordinal) && line.Contains("VBR 4.2 Mbps", StringComparison.Ordinal));
            logs.Should().Contain(line => line.Contains("第 1/1 集开始去重：01.mp4，8 秒，720x1280 → 01.mp4。", StringComparison.Ordinal));
            logs.Should().Contain(line => line.Contains("第 1/1 集去重完成", StringComparison.Ordinal));

            logs.Clear();
            await VideoDedupService.RunAsync(item, account, force: false, logs.Add, CancellationToken.None);
            logs.Should().Contain(line => line.StartsWith("跳过", StringComparison.Ordinal));
        }
        finally
        {
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public async Task Duration_merge_replaces_visible_files_and_updates_episode_count()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "a.mp4"), [1, 1]);
        await File.WriteAllBytesAsync(Path.Combine(root, "b.mp4"), [2, 2, 2]);
        await File.WriteAllTextAsync(
            Path.Combine(root, ".weixin-channel-download-state.json"),
            """{"video_count":2}""");
        VideoDedupService.ProbeForTests = (_, _) =>
            Task.FromResult(new VideoDedupProbe(15, 1080, 1920, true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (args, _) =>
        {
            File.WriteAllBytes(args[^1], [8, 8, 8]);
            return Task.CompletedTask;
        };
        try
        {
            var preset = VideoDedupPreset.CreateDefault();
            preset.MinimumDurationSeconds = 20;
            preset.MaximumDurationSeconds = 40;
            var item = new QueueProjectItem { ProjectDir = root, EpisodeCount = 2, SourceEpisodeCount = 2 };
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1", VideoDedupPreset = preset };

            await VideoDedupService.RunAsync(item, account, force: false, _ => { }, CancellationToken.None);

            item.EpisodeCount.Should().Be(1);
            item.SourceEpisodeCount.Should().Be(2);
            File.Exists(Path.Combine(root, "a.mp4")).Should().BeFalse();
            File.Exists(Path.Combine(root, "b.mp4")).Should().BeFalse();
            (await File.ReadAllBytesAsync(Path.Combine(root, "01.mp4"))).Should().Equal([8, 8, 8]);
            (await File.ReadAllBytesAsync(Path.Combine(root, "source-originals", "a.mp4"))).Should().Equal([1, 1]);
            (await File.ReadAllBytesAsync(Path.Combine(root, "source-originals", "b.mp4"))).Should().Equal([2, 2, 2]);
            var state = await File.ReadAllTextAsync(Path.Combine(root, ".weixin-channel-download-state.json"));
            state.Should().Contain("\"video_count\": 1");
            state.Should().Contain("\"source_video_count\": 2");
        }
        finally
        {
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public async Task Failed_encode_leaves_the_downloaded_video_in_place()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "01.mp4");
        await File.WriteAllBytesAsync(source, [4, 5, 6]);
        VideoDedupService.ProbeForTests = (_, _) =>
            Task.FromResult(new VideoDedupProbe(8, 720, 1280, true));
        VideoDedupService.EncoderForTests = "libx264";
        VideoDedupService.RunProcessForTests = (_, _) => throw new InvalidOperationException("encode failed");
        try
        {
            var item = new QueueProjectItem { ProjectDir = root, EpisodeCount = 1 };
            var account = new TikTokAccountProfile { Id = "a1", Name = "账号1" };
            var act = () => VideoDedupService.RunAsync(item, account, force: false, _ => { }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
            (await File.ReadAllBytesAsync(source)).Should().Equal([4, 5, 6]);
            item.EpisodeCount.Should().Be(1);
        }
        finally
        {
            VideoDedupService.ProbeForTests = null;
            VideoDedupService.EncoderForTests = null;
            VideoDedupService.RunProcessForTests = null;
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void Picture_filters_are_appended_after_geometry()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.Effects.Temperature = 40;
        preset.Sharpness = -1;
        preset.Effects.CurveAmount = 100;
        preset.Effects.CurveMaster =
        [
            new VideoDedupCurvePoint { X = 0, Y = 0 },
            new VideoDedupCurvePoint { X = 0.5, Y = 0.8 },
            new VideoDedupCurvePoint { X = 1, Y = 1 },
        ];
        preset.Effects.Layers.Add(new VideoDedupOverlayLayer
        {
            Kind = VideoDedupOverlayLayer.KindBorder,
            Color = "#FFFFFF",
            BorderPercent = 2,
        });
        preset.Normalize();

        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, true));
        var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));

        joined.Should().Contain("scale=1080:1920");
        joined.Should().Contain("colorbalance=rm=0.2:bm=-0.2");
        joined.Should().Contain("gblur=sigma=1");
        joined.Should().NotContain("unsharp=");
        joined.Should().Contain("curves=");
        joined.Should().Contain("0.5/0.8");
        joined.Should().Contain("drawbox=");
        var scaleAt = joined.IndexOf("scale=1080:1920", StringComparison.Ordinal);
        var colorAt = joined.IndexOf("colorbalance=rm=0.2", StringComparison.Ordinal);
        colorAt.Should().BeGreaterThan(scaleAt);
    }

    [Fact]
    public void Mosaic_and_partial_lut_use_a_filter_graph()
    {
        var lut = Path.Combine(Path.GetTempPath(), "dedup-lut-" + Guid.NewGuid().ToString("N") + ".cube");
        File.WriteAllText(lut, "LUT_3D_SIZE 2");
        try
        {
            var preset = VideoDedupPreset.CreateDefault();
            preset.Effects.LutAmount = 50;
            preset.Effects.LutPath = lut;
            preset.Effects.MosaicAmount = 80;
            preset.Effects.MosaicRegions.Add(new VideoDedupMosaicRegion { X = 10, Y = 20, Width = 30, Height = 15, Block = 16 });
            preset.Normalize();

            var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, false));
            var joined = string.Join(" ", VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1));
            joined.Should().Contain("-filter_complex");
            joined.Should().Contain("lut3d=");
            joined.Should().Contain("blend=all_opacity=0.5");
            joined.Should().Contain("crop=w=iw*0.3:h=ih*0.15");
        }
        finally
        {
            File.Delete(lut);
        }
    }

    [Fact]
    public void Missing_filter_asset_fails_before_encode()
    {
        var preset = VideoDedupPreset.CreateDefault();
        preset.Effects.LutAmount = 100;
        preset.Effects.LutPath = Path.Combine(Path.GetTempPath(), "missing-dedup-lut.cube");
        var clip = new VideoDedupClip("episode-1.mp4", new VideoDedupProbe(12, 1080, 1920, false));

        var act = () => VideoDedupPlanner.BuildArguments(preset, [clip], "out.mp4", "libx264", seed: 1);
        act.Should().Throw<InvalidOperationException>().WithMessage("*3D LUT*");
    }

    [Fact]
    public void Inactive_effects_do_not_change_the_dedup_fingerprint()
    {
        var untouched = VideoDedupPreset.CreateDefault().Fingerprint();
        var explicitDefaults = VideoDedupPreset.CreateDefault();
        explicitDefaults.Effects = new VideoDedupEffects();
        explicitDefaults.Fingerprint().Should().Be(untouched);

        var changed = VideoDedupPreset.CreateDefault();
        changed.Effects.Temperature = 12;
        changed.Fingerprint().Should().NotBe(untouched);
    }
}
